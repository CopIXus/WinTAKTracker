using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Tak;

public sealed class EnrollmentApplyResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public ServerProfile? Profile { get; init; }
    public bool IdentityUpdated { get; init; }
    public string? Message { get; init; }
}

/// <summary>Applies parsed enrollment URLs, SoftCert ZIPs, and Marti CSR enrollment (best-effort).</summary>
public sealed class EnrollmentService
{
    private readonly AppConfigStore _store;
    private readonly SoftCertImporter _softCert;
    private readonly IRedactedLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public EnrollmentService(AppConfigStore store, SoftCertImporter softCert, IRedactedLogger log)
    {
        _store = store;
        _softCert = softCert;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
    }

    public async Task<EnrollmentApplyResult> ApplyAsync(string input, AppConfig config)
    {
        var parsed = EnrollmentUriParser.Parse(input);
        if (!parsed.Success)
            return Fail(parsed.Error ?? "Parse failed.");

        switch (parsed.Kind)
        {
            case EnrollmentKind.TakPreference:
                ApplyIdentity(config, parsed);
                _store.Save(config);
                return new EnrollmentApplyResult
                {
                    Success = true,
                    IdentityUpdated = true,
                    Message = "Preferences applied (callsign/team/role).",
                };

            case EnrollmentKind.TakImportUrl:
                if (string.IsNullOrWhiteSpace(parsed.ImportUrl))
                    return Fail("import URL missing.");
                return await ImportFromUrlAsync(parsed.ImportUrl!, config);

            case EnrollmentKind.ItakCsv:
                return ApplyItakCsv(parsed, config);

            case EnrollmentKind.OpenTakTrackerEnroll:
            case EnrollmentKind.TakEnroll:
                return await EnrollWithCredentialsAsync(parsed, config);

            default:
                return Fail("Unsupported enrollment kind.");
        }
    }

    public EnrollmentApplyResult ImportSoftCertZip(string zipPath, AppConfig config, string? displayName = null)
    {
        var result = _softCert.ImportZip(zipPath, displayName);
        if (!result.Success || result.Profile is null)
            return Fail(result.Error ?? "Import failed.");

        if (!string.IsNullOrWhiteSpace(result.Callsign)) config.Identity.Callsign = result.Callsign!;
        if (!string.IsNullOrWhiteSpace(result.Team)) config.Identity.Team = result.Team!;
        if (!string.IsNullOrWhiteSpace(result.Role)) config.Identity.Role = result.Role!;

        config.Servers.Add(result.Profile);
        _store.Save(config);
        return new EnrollmentApplyResult
        {
            Success = true,
            Profile = result.Profile,
            IdentityUpdated = true,
            Message = "SoftCert imported.",
        };
    }

    public EnrollmentApplyResult ImportManual(
        AppConfig config,
        string clientP12Path,
        string? trustPath,
        string clientPassword,
        string? trustPassword,
        string host,
        int port,
        string protocol,
        string? displayName = null)
    {
        var result = _softCert.ImportManual(clientP12Path, trustPath, clientPassword, trustPassword, host, port, protocol, displayName);
        if (!result.Success || result.Profile is null)
            return Fail(result.Error ?? "Manual import failed.");

        config.Servers.Add(result.Profile);
        _store.Save(config);
        return new EnrollmentApplyResult { Success = true, Profile = result.Profile, Message = "Manual cert imported." };
    }

    private EnrollmentApplyResult ApplyItakCsv(EnrollmentParseResult parsed, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(parsed.Host))
            return Fail("Host missing from iTAK CSV.");

        var profile = new ServerProfile
        {
            DisplayName = parsed.DisplayName ?? parsed.Host!,
            Host = parsed.Host!,
            Port = parsed.Port ?? 8089,
            Protocol = parsed.Protocol,
            Enabled = true,
        };
        config.Servers.Add(profile);
        _store.Save(config);
        return new EnrollmentApplyResult
        {
            Success = true,
            Profile = profile,
            Message = "iTAK CSV server added. Import a client certificate if required.",
        };
    }

    private async Task<EnrollmentApplyResult> EnrollWithCredentialsAsync(EnrollmentParseResult parsed, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(parsed.Host))
            return Fail("Host missing from enrollment URL.");

        ApplyIdentity(config, parsed);

        var profileId = Guid.NewGuid().ToString("N");
        var profile = new ServerProfile
        {
            Id = profileId,
            DisplayName = parsed.Host!,
            Host = parsed.Host!,
            Port = parsed.Port ?? 8089,
            Protocol = parsed.Protocol,
            Username = parsed.Username,
            CallsignOverride = parsed.Callsign,
            TeamOverride = parsed.Team,
            RoleOverride = parsed.Role,
            Enabled = true,
        };

        if (!string.IsNullOrEmpty(parsed.Token))
        {
            var blob = $"{profileId}-token";
            _store.WriteSecret(blob, parsed.Token!);
            profile.SecretBlobName = blob;
        }

        // Best-effort Marti CSR enrollment on 8446
        try
        {
            var enrolled = await TryMartiEnrollAsync(profile, parsed.Username, parsed.Token);
            if (enrolled)
                _log.Info("Enroll", "Marti CSR enrollment succeeded.");
            else
                _log.Info("Enroll", "Profile saved; cert enrollment deferred (use SoftCert/manual if needed).");
        }
        catch (Exception ex)
        {
            _log.Warn("Enroll", $"Marti enroll attempt failed: {ex.GetType().Name}");
        }

        config.Servers.Add(profile);
        _store.Save(config);
        return new EnrollmentApplyResult
        {
            Success = true,
            Profile = profile,
            IdentityUpdated = true,
            Message = "Enrollment profile saved.",
        };
    }

    private async Task<bool> TryMartiEnrollAsync(ServerProfile profile, string? username, string? token)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            return false;

        // Generate CSR
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={username}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var csr = req.CreateSigningRequestPem();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        // Common Marti paths — try a few
        var urls = new[]
        {
            $"https://{profile.Host}:8446/Marti/api/tls/signClient/v2?clientUid={Uri.EscapeDataString(Environment.MachineName)}",
            $"https://{profile.Host}:8446/Marti/api/tls/signClient?clientUid={Uri.EscapeDataString(Environment.MachineName)}",
        };

        foreach (var url in urls)
        {
            try
            {
                using var content = new StringContent(csr, Encoding.UTF8, "application/pkcs10");
                using var resp = await http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode) continue;

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                // Response may be PKCS12 or PEM cert — store as client cert material
                var clientFile = $"{profile.Id}-client.p12";
                var path = Path.Combine(_store.CertsDirectory, clientFile);

                if (LooksLikePkcs12(bytes))
                {
                    File.WriteAllBytes(path, bytes);
                }
                else
                {
                    // Signed cert PEM + private key → export PFX
                    var certPem = Encoding.UTF8.GetString(bytes);
                    using var cert = X509Certificate2.CreateFromPem(certPem);
                    using var withKey = cert.CopyWithPrivateKey(rsa);
                    var pfx = withKey.Export(X509ContentType.Pkcs12, token);
                    File.WriteAllBytes(path, pfx);
                }

                profile.ClientCertFileName = clientFile;
                var pwdBlob = $"{profile.Id}-certpwd";
                _store.WriteSecret(pwdBlob, token!);
                profile.CertPasswordBlobName = pwdBlob;
                return true;
            }
            catch
            {
                // try next URL
            }
        }

        return false;
    }

    private async Task<EnrollmentApplyResult> ImportFromUrlAsync(string url, AppConfig config)
    {
        try
        {
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var tmp = Path.Combine(Path.GetTempPath(), $"wintak-softcert-{Guid.NewGuid():N}.zip");
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs);

            var result = ImportSoftCertZip(tmp, config);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return result;
        }
        catch (Exception ex)
        {
            return Fail($"Download failed: {ex.GetType().Name}");
        }
    }

    private static void ApplyIdentity(AppConfig config, EnrollmentParseResult parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Callsign)) config.Identity.Callsign = parsed.Callsign!;
        if (!string.IsNullOrWhiteSpace(parsed.Team)) config.Identity.Team = parsed.Team!;
        if (!string.IsNullOrWhiteSpace(parsed.Role)) config.Identity.Role = parsed.Role!;
    }

    private static bool LooksLikePkcs12(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0x30;

    private static EnrollmentApplyResult Fail(string error) =>
        new() { Success = false, Error = error };
}
