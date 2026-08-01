using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
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
        if (!config.ApplyRemoteIdentityFromPortal) return;
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
            X509Certificate2? clientCert = null;
            try
            {
                using var handler = CreateHandler(profile, config, out clientCert);
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
            finally
            {
                clientCert?.Dispose();
            }
        }

        return null;
    }

    private HttpClientHandler CreateHandler(ServerProfile profile, AppConfig config, out X509Certificate2? loadedCert)
    {
        loadedCert = null;
        var softAccept = profile.AllowInsecureTlsSoftAccept ?? config.Diagnostics.AllowInsecureTlsSoftAccept;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
                ValidateServerCertificate(profile, softAccept, cert, chain, errors),
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
            X509Certificate2 cert;
            try
            {
                cert = new X509Certificate2(
                    path, pwd, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException)
            {
                var fallback = _store.DpapiScope == DataProtectionScope.LocalMachine
                    ? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable
                    : X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;
                cert = new X509Certificate2(path, pwd, fallback);
            }

            loadedCert = cert;
            handler.ClientCertificates.Add(cert);
        }
        catch (Exception ex)
        {
            _log.Warn("Profile", $"Client cert load for profile sync failed: {ex.GetType().Name}");
        }

        return handler;
    }

    private bool ValidateServerCertificate(
        ServerProfile profile,
        bool softAccept,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        if (!string.IsNullOrWhiteSpace(profile.TrustStoreFileName) && certificate is not null)
        {
            var trustPath = Path.Combine(_store.CertsDirectory, profile.TrustStoreFileName);
            if (File.Exists(trustPath) && chain is not null)
            {
                X509Certificate2? leaf = null;
                try
                {
                    var pwd = profile.TrustPasswordBlobName is null
                        ? (profile.CertPasswordBlobName is null
                            ? "atakatak"
                            : _store.ReadSecret(profile.CertPasswordBlobName) ?? "atakatak")
                        : _store.ReadSecret(profile.TrustPasswordBlobName) ?? "";
                    using var trust = new X509Certificate2(trustPath, pwd,
                        X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(trust);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    leaf = new X509Certificate2(certificate);
                    if (chain.Build(leaf))
                        return true;
                }
                catch
                {
                    // fall through
                }
                finally
                {
                    leaf?.Dispose();
                }
            }
        }

        if (!softAccept)
        {
            _log.Warn("Profile", $"HTTPS rejected ({errors}) — soft-accept disabled.");
            return false;
        }

        _log.Warn("Profile", $"HTTPS soft-accept ({errors}).");
        return true;
    }
}
