using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Identity;

namespace WinTAKTracker.Services.Tak;

/// <summary>
/// Fetches TAK Server / OpenTAK device profile updates after connect
/// (<c>GET /Marti/api/device/profile/connection</c>), matching ATAK’s
/// “Apply TAK Server Profile Updates” path used by Portal “Send Configuration”.
/// Only callsign / team / role identity prefs are applied (tracking-only).
/// </summary>
public sealed class DeviceProfileSync
{
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttemptUtc = new(StringComparer.Ordinal);

    public DeviceProfileSync(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
    }

    public event EventHandler<RemoteIdentityApply.Result>? IdentityApplied;

    /// <summary>
    /// Best-effort profile pull. Never throws; failures are logged and ignored.
    /// </summary>
    public async Task TrySyncAsync(
        ServerProfile profile,
        AppConfig config,
        string? activeUserSid,
        string? activeUserName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Host)) return;
        if (string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return;

        lock (_gate)
        {
            if (_lastAttemptUtc.TryGetValue(profile.Id, out var last) &&
                DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2))
                return;
            _lastAttemptUtc[profile.Id] = DateTimeOffset.UtcNow;
        }

        try
        {
            var bytes = await DownloadProfilePackageAsync(profile, config, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                _log.Info("Profile", "No device profile package from server (empty or unsupported).");
                return;
            }

            var prefs = PreferencePackageParser.ParseZipBytes(bytes);
            if (!prefs.HasAny)
            {
                _log.Info("Profile", "Device profile package had no callsign/team/role prefs.");
                return;
            }

            var result = RemoteIdentityApply.Apply(
                config,
                prefs.Callsign,
                prefs.Team,
                prefs.Role,
                activeUserSid,
                activeUserName);

            if (!result.Applied) return;

            _store.Save(config);
            _log.Info("Profile", $"Remote identity applied ({result.Target}): callsign/team updated.");
            IdentityApplied?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Warn("Profile", $"Device profile sync skipped: {ex.GetType().Name}");
        }
    }

    private async Task<byte[]?> DownloadProfilePackageAsync(
        ServerProfile profile,
        AppConfig config,
        CancellationToken ct)
    {
        var clientUid = config.DeviceUid ?? Environment.MachineName;
        // ATAK uses HTTPS API ports; try common Marti HTTPS ports with client cert when present.
        var ports = new[] { 8443, 8446 };
        foreach (var port in ports)
        {
            try
            {
                using var handler = CreateHandler(profile);
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                var url =
                    $"https://{profile.Host}:{port}/Marti/api/device/profile/connection?clientUid={Uri.EscapeDataString(clientUid)}";
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 0)
                    return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // try next port
            }
        }

        return null;
    }

    private HttpClientHandler CreateHandler(ServerProfile profile)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return handler;

        var path = Path.Combine(_store.CertsDirectory, profile.ClientCertFileName);
        if (!File.Exists(path))
            return handler;

        var pwd = profile.CertPasswordBlobName is null
            ? ""
            : _store.ReadSecret(profile.CertPasswordBlobName) ?? "";

        try
        {
            var cert = new X509Certificate2(path, pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            handler.ClientCertificates.Add(cert);
        }
        catch (Exception ex)
        {
            _log.Warn("Profile", $"Client cert load for profile sync failed: {ex.GetType().Name}");
        }

        return handler;
    }
}
