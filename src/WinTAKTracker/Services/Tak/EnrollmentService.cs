using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

/// <summary>
/// Applies parsed enrollment URLs, SoftCert ZIPs, and Marti CSR enrollment (ATAK-compatible on 8446).
/// </summary>
public sealed class EnrollmentService
{
    private const string DefaultP12Password = "atakatak";
    private static readonly TimeSpan EnrollHttpTimeout = TimeSpan.FromSeconds(90);

    private readonly AppConfigStore _store;
    private readonly SoftCertImporter _softCert;
    private readonly IRedactedLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public EnrollmentService(AppConfigStore store, SoftCertImporter softCert, IRedactedLogger log)
    {
        _store = store;
        _softCert = softCert;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
    }

    public Task<EnrollmentApplyResult> ApplyAsync(string input, AppConfig config) =>
        ApplyAsync(input, config, progress: null, CancellationToken.None);

    public async Task<EnrollmentApplyResult> ApplyAsync(
        string input,
        AppConfig config,
        IProgress<string>? progress,
        CancellationToken ct)
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
                progress?.Report("Downloading SoftCert package…");
                return await ImportFromUrlAsync(parsed.ImportUrl!, config, ct);

            case EnrollmentKind.ItakCsv:
                return ApplyItakCsv(parsed, config);

            case EnrollmentKind.OpenTakTrackerEnroll:
            case EnrollmentKind.TakEnroll:
                return await EnrollWithCredentialsAsync(parsed, config, progress, ct);

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

    private async Task<EnrollmentApplyResult> EnrollWithCredentialsAsync(
        EnrollmentParseResult parsed,
        AppConfig config,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parsed.Host))
            return Fail("Host missing from enrollment URL.");
        if (string.IsNullOrWhiteSpace(parsed.Username))
            return Fail("Username missing from enrollment URL.");
        if (string.IsNullOrWhiteSpace(parsed.Token))
            return Fail("Token/password missing from enrollment URL. Portal enroll links include a short-lived token.");

        ApplyIdentity(config, parsed);

        var profileId = Guid.NewGuid().ToString("N");
        var profile = new ServerProfile
        {
            Id = profileId,
            DisplayName = parsed.Host!,
            Host = parsed.Host!,
            Port = parsed.Port ?? 8089,
            Protocol = string.IsNullOrWhiteSpace(parsed.Protocol) ? "ssl" : parsed.Protocol,
            Username = parsed.Username,
            CallsignOverride = parsed.Callsign,
            TeamOverride = parsed.Team,
            RoleOverride = parsed.Role,
            Enabled = true,
        };

        var tokenBlob = $"{profileId}-token";
        _store.WriteSecret(tokenBlob, parsed.Token!);
        profile.SecretBlobName = tokenBlob;

        progress?.Report("Enrolling certificate…");
        _log.Info("Enroll", "Starting Marti CSR enrollment (host/token redacted).");

        MartiEnrollResult enrolled;
        try
        {
            enrolled = await MartiEnrollAsync(
                profile.Host,
                parsed.EnrollmentPort > 0 ? parsed.EnrollmentPort : 8446,
                parsed.Username!,
                parsed.Token!,
                config.DeviceUid ?? Environment.MachineName,
                profileId,
                progress,
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            CleanupPartialProfile(profile);
            return Fail("Certificate enrollment timed out reaching the server on port 8446. Check network/firewall and try again with a fresh Portal token.");
        }
        catch (OperationCanceledException)
        {
            CleanupPartialProfile(profile);
            return Fail("Certificate enrollment was canceled.");
        }
        catch (Exception ex)
        {
            CleanupPartialProfile(profile);
            _log.Warn("Enroll", $"Marti enroll failed: {ex.GetType().Name}");
            return Fail(DescribeEnrollException(ex));
        }

        if (!enrolled.Success)
        {
            CleanupPartialProfile(profile);
            return Fail(enrolled.Error ?? "Certificate enrollment failed.");
        }

        profile.ClientCertFileName = enrolled.ClientCertFileName;
        profile.TrustStoreFileName = enrolled.TrustStoreFileName;
        profile.CertPasswordBlobName = enrolled.CertPasswordBlobName;
        profile.TrustPasswordBlobName = enrolled.TrustPasswordBlobName;

        config.Servers.Add(profile);
        _store.Save(config);
        _log.Info("Enroll", "Marti CSR enrollment succeeded; streaming profile saved.");

        progress?.Report("Certificate enrolled — connecting…");
        return new EnrollmentApplyResult
        {
            Success = true,
            Profile = profile,
            IdentityUpdated = true,
            Message = "Certificate enrolled. Server profile ready for SSL CoT on port 8089.",
        };
    }

    private async Task<MartiEnrollResult> MartiEnrollAsync(
        string host,
        int enrollmentPort,
        string username,
        string token,
        string clientUid,
        string profileId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var rsa = RSA.Create(4096);
        using var handler = new HttpClientHandler
        {
            // Enrollment port often uses a private CA / Let's Encrypt; client has no trust yet.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = EnrollHttpTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        progress?.Report("Fetching enrollment config (8446)…");
        var csrConfig = await FetchTlsConfigAsync(http, host, enrollmentPort, ct);
        csrConfig["CN"] = username;

        progress?.Report("Generating certificate request…");
        var csrPem = CreateCsrPem(csrConfig, rsa);
        var uid = Uri.EscapeDataString(SanitizeClientUid(clientUid));

        // Prefer v2 (JSON signedCert + caN); fall back to v1 PKCS12.
        progress?.Report("Signing certificate with server…");
        var v2Url = $"https://{host}:{enrollmentPort}/Marti/api/tls/signClient/v2?clientUid={uid}";
        var v1Url = $"https://{host}:{enrollmentPort}/Marti/api/tls/signClient?clientUid={uid}";

        string? lastError = null;

        var v2 = await PostCsrAsync(http, v2Url, csrPem, ct);
        if (v2.StatusCode == HttpStatusCode.Unauthorized || v2.StatusCode == HttpStatusCode.Forbidden)
            return MartiFail(DescribeHttpAuthFailure(v2.StatusCode, v2.BodyText));
        if (v2.Ok)
        {
            var built = TryBuildFromV2Json(v2.BodyBytes, rsa, profileId);
            if (built.Success) return built;
            lastError = built.Error;
            _log.Info("Enroll", "signClient/v2 response not usable; trying v1.");
        }
        else
        {
            lastError = DescribeHttpFailure(v2.StatusCode, v2.BodyText, enrollmentPort);
        }

        var v1 = await PostCsrAsync(http, v1Url, csrPem, ct);
        if (v1.StatusCode == HttpStatusCode.Unauthorized || v1.StatusCode == HttpStatusCode.Forbidden)
            return MartiFail(DescribeHttpAuthFailure(v1.StatusCode, v1.BodyText));
        if (v1.Ok)
        {
            var built = TryBuildFromV1Body(v1.BodyBytes, rsa, profileId);
            if (built.Success) return built;
            lastError = built.Error;
        }
        else
        {
            lastError = DescribeHttpFailure(v1.StatusCode, v1.BodyText, enrollmentPort);
        }

        return MartiFail(lastError ?? "Certificate enrollment failed on both signClient/v2 and v1.");
    }

    private async Task<Dictionary<string, string>> FetchTlsConfigAsync(
        HttpClient http, string host, int enrollmentPort, CancellationToken ct)
    {
        var url = $"https://{host}:{enrollmentPort}/Marti/api/tls/config";
        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body = await SafeReadTextAsync(resp, ct);
            throw new InvalidOperationException(DescribeHttpAuthFailure(resp.StatusCode, body));
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadTextAsync(resp, ct);
            throw new InvalidOperationException(DescribeHttpFailure(resp.StatusCode, body, enrollmentPort));
        }

        var xml = await resp.Content.ReadAsStringAsync(ct);
        var config = ParseTlsConfigXml(xml);
        if (config.Count == 0)
            _log.Warn("Enroll", "tls/config returned no name entries; using CN only.");
        return config;
    }

    private static Dictionary<string, string> ParseTlsConfigXml(string xml)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var elem in doc.Descendants())
            {
                var local = elem.Name.LocalName;
                if (local is not ("nameEntry" or "entry")) continue;
                var name = (string?)elem.Attribute("name") ?? "";
                var value = (string?)elem.Attribute("value") ?? elem.Value;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                    config[name] = value;
            }
        }
        catch
        {
            // CN-only CSR still attempted
        }

        return config;
    }

    private static string CreateCsrPem(Dictionary<string, string> config, RSA rsa)
    {
        var dn = new StringBuilder();
        void Append(string oidKey, string label)
        {
            if (!config.TryGetValue(oidKey, out var v) || string.IsNullOrWhiteSpace(v)) return;
            if (dn.Length > 0) dn.Append(", ");
            dn.Append(label).Append('=').Append(EscapeDn(v));
        }

        Append("CN", "CN");
        Append("O", "O");
        Append("OU", "OU");
        Append("C", "C");
        Append("ST", "ST");
        Append("L", "L");

        if (dn.Length == 0)
            dn.Append("CN=").Append(EscapeDn(config.GetValueOrDefault("CN") ?? "WinTAKTracker"));

        var req = new CertificateRequest(dn.ToString(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSigningRequestPem();
    }

    private static string EscapeDn(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);

    private async Task<(bool Ok, HttpStatusCode StatusCode, byte[] BodyBytes, string? BodyText)> PostCsrAsync(
        HttpClient http, string url, string csrPem, CancellationToken ct)
    {
        using var content = new StringContent(csrPem, Encoding.UTF8, "application/pkcs10");
        using var resp = await http.PostAsync(url, content, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        string? text = null;
        if (bytes.Length > 0 && bytes.Length < 512_000 && LooksLikeText(bytes))
            text = Encoding.UTF8.GetString(bytes);
        return (resp.IsSuccessStatusCode, resp.StatusCode, bytes, text);
    }

    private MartiEnrollResult TryBuildFromV2Json(byte[] body, RSA rsa, string profileId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("signedCert", out var signedEl))
                return MartiFail("signClient/v2 response missing signedCert.");

            var signedPem = EnsurePem(signedEl.GetString() ?? "", "CERTIFICATE");
            var caPems = new List<string>();
            for (var i = 0; ; i++)
            {
                if (!root.TryGetProperty($"ca{i}", out var caEl)) break;
                var ca = caEl.GetString();
                if (!string.IsNullOrWhiteSpace(ca))
                    caPems.Add(EnsurePem(ca, "CERTIFICATE"));
            }

            return PersistCertMaterial(signedPem, caPems, rsa, profileId);
        }
        catch (JsonException)
        {
            return MartiFail("signClient/v2 did not return JSON.");
        }
        catch (Exception ex)
        {
            return MartiFail($"Failed to build certificate from v2 response: {ex.GetType().Name}.");
        }
    }

    private MartiEnrollResult TryBuildFromV1Body(byte[] body, RSA rsa, string profileId)
    {
        // PEM signed cert
        if (LooksLikeText(body))
        {
            var text = Encoding.UTF8.GetString(body).Trim();
            if (text.Contains("BEGIN CERTIFICATE", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("BEGIN PKCS", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return PersistCertMaterial(EnsurePem(text, "CERTIFICATE"), [], rsa, profileId);
                }
                catch (Exception ex)
                {
                    return MartiFail($"Failed to parse PEM enrollment response: {ex.GetType().Name}.");
                }
            }
        }

        // PKCS12 bundle (password usually atakatak)
        foreach (var pwd in new[] { DefaultP12Password, "", })
        {
            try
            {
                var col = new X509Certificate2Collection();
                col.Import(body, pwd, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                if (col.Count == 0) continue;

                X509Certificate2? leaf = null;
                var cas = new List<X509Certificate2>();
                foreach (var c in col)
                {
                    if (leaf is null && c.HasPrivateKey)
                        leaf = c;
                    else if (leaf is null)
                        leaf = c;
                    else
                        cas.Add(c);
                }

                leaf ??= col[0];
                var signedPem = PemEncoding.WriteString("CERTIFICATE", leaf.RawData);
                var caPems = cas.Select(c => PemEncoding.WriteString("CERTIFICATE", c.RawData)).ToList();
                return PersistCertMaterial(signedPem, caPems, rsa, profileId);
            }
            catch (CryptographicException)
            {
                // try next password
            }
        }

        return MartiFail("Could not parse signClient v1 response as PKCS12 or PEM.");
    }

    private MartiEnrollResult PersistCertMaterial(
        string signedCertPem,
        IReadOnlyList<string> caPems,
        RSA rsa,
        string profileId)
    {
        using var cert = X509Certificate2.CreateFromPem(signedCertPem);
        using var withKey = cert.CopyWithPrivateKey(rsa);
        using var exportable = new X509Certificate2(
            withKey.Export(X509ContentType.Pfx, DefaultP12Password),
            DefaultP12Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        var clientFile = $"{profileId}-client.p12";
        var clientPath = Path.Combine(_store.CertsDirectory, clientFile);
        File.WriteAllBytes(clientPath, exportable.Export(X509ContentType.Pkcs12, DefaultP12Password));

        var certPwdBlob = $"{profileId}-certpwd";
        _store.WriteSecret(certPwdBlob, DefaultP12Password);

        string? trustFile = null;
        string? trustPwdBlob = null;
        if (caPems.Count > 0)
        {
            try
            {
                trustFile = $"{profileId}-trust.p12";
                var trustPath = Path.Combine(_store.CertsDirectory, trustFile);
                using var caCert = X509Certificate2.CreateFromPem(caPems[0]);
                // Store first CA; additional CAs appended as PEM sidecar if present.
                File.WriteAllBytes(trustPath, caCert.Export(X509ContentType.Pkcs12, DefaultP12Password));
                trustPwdBlob = $"{profileId}-trustpwd";
                _store.WriteSecret(trustPwdBlob, DefaultP12Password);

                if (caPems.Count > 1)
                {
                    var pemPath = Path.Combine(_store.CertsDirectory, $"{profileId}-trust-chain.pem");
                    File.WriteAllText(pemPath, string.Join('\n', caPems));
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Enroll", $"Trust store save failed: {ex.GetType().Name}");
                trustFile = null;
                trustPwdBlob = null;
            }
        }

        return new MartiEnrollResult
        {
            Success = true,
            ClientCertFileName = clientFile,
            TrustStoreFileName = trustFile,
            CertPasswordBlobName = certPwdBlob,
            TrustPasswordBlobName = trustPwdBlob,
        };
    }

    private void CleanupPartialProfile(ServerProfile profile)
    {
        try
        {
            if (profile.SecretBlobName is not null) _store.DeleteSecret(profile.SecretBlobName);
            if (profile.CertPasswordBlobName is not null) _store.DeleteSecret(profile.CertPasswordBlobName);
            if (profile.TrustPasswordBlobName is not null) _store.DeleteSecret(profile.TrustPasswordBlobName);
            void Del(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var path = Path.Combine(_store.CertsDirectory, name);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            }
            Del(profile.ClientCertFileName);
            Del(profile.TrustStoreFileName);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<EnrollmentApplyResult> ImportFromUrlAsync(string url, AppConfig config, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var tmp = Path.Combine(Path.GetTempPath(), $"wintak-softcert-{Guid.NewGuid():N}.zip");
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs, ct);

            var result = ImportSoftCertZip(tmp, config);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return result;
        }
        catch (Exception ex)
        {
            return Fail($"Download failed: {DescribeEnrollException(ex)}");
        }
    }

    private static void ApplyIdentity(AppConfig config, EnrollmentParseResult parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Callsign)) config.Identity.Callsign = parsed.Callsign!;
        if (!string.IsNullOrWhiteSpace(parsed.Team)) config.Identity.Team = parsed.Team!;
        if (!string.IsNullOrWhiteSpace(parsed.Role)) config.Identity.Role = parsed.Role!;
    }

    private static string EnsurePem(string raw, string label)
    {
        raw = raw.Trim();
        if (raw.Contains("BEGIN ", StringComparison.Ordinal))
            return raw;
        // v2 may return headerless base64
        var b64 = raw.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN ").Append(label).AppendLine("-----");
        for (var i = 0; i < b64.Length; i += 64)
            sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
        sb.Append("-----END ").Append(label).AppendLine("-----");
        return sb.ToString();
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        var sample = Math.Min(bytes.Length, 64);
        var texty = 0;
        for (var i = 0; i < sample; i++)
        {
            var b = bytes[i];
            if (b is 9 or 10 or 13 || b is >= 32 and < 127) texty++;
        }
        return texty >= sample * 0.9;
    }

    private static string SanitizeClientUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return Guid.NewGuid().ToString("N");
        var chars = uid.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        return chars.Length == 0 ? Guid.NewGuid().ToString("N") : new string(chars);
    }

    private static string DescribeHttpAuthFailure(HttpStatusCode status, string? body)
    {
        var hint = status == HttpStatusCode.Unauthorized
            ? "Unauthorized — enroll token may be expired or already used (Portal tokens are typically valid ~15 minutes)."
            : "Forbidden — enrollment credentials rejected.";
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 200 && !LooksLikeSecret(body))
            return $"{hint} Server said: {body.Trim()}";
        return hint;
    }

    private static string DescribeHttpFailure(HttpStatusCode status, string? body, int enrollmentPort)
    {
        if (status == 0)
            return $"Could not reach enrollment port {enrollmentPort}. Check host/firewall/VPN.";
        var msg = $"Enrollment HTTP {(int)status} ({status}) on port {enrollmentPort}.";
        if (status == HttpStatusCode.NotFound)
            msg += " Server may not expose Marti certificate enrollment.";
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 200 && !LooksLikeSecret(body))
            msg += $" {body.Trim()}";
        return msg;
    }

    private static string DescribeEnrollException(Exception ex) => ex switch
    {
        HttpRequestException => "Enrollment port unreachable (network/DNS/TLS). Confirm host and that 8446 is open.",
        TaskCanceledException or OperationCanceledException =>
            "Enrollment timed out. Confirm 8446 is reachable and paste a fresh Portal token promptly.",
        InvalidOperationException ioe => ioe.Message,
        _ => $"Enrollment failed ({ex.GetType().Name}).",
    };

    private static bool LooksLikeSecret(string text) =>
        text.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        text.Length > 64 && text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static MartiEnrollResult MartiFail(string error) => new() { Success = false, Error = error };

    private static EnrollmentApplyResult Fail(string error) =>
        new() { Success = false, Error = error };

    private sealed class MartiEnrollResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? ClientCertFileName { get; init; }
        public string? TrustStoreFileName { get; init; }
        public string? CertPasswordBlobName { get; init; }
        public string? TrustPasswordBlobName { get; init; }
    }

    private static async Task<string?> SafeReadTextAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > 4000 || !LooksLikeText(bytes)) return null;
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
