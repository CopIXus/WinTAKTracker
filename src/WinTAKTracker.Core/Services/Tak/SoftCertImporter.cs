using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Tak;

public sealed class SoftCertImportResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public ServerProfile? Profile { get; init; }
    public string? Callsign { get; init; }
    public string? Team { get; init; }
    public string? Role { get; init; }
}

/// <summary>Imports SoftCert / pref ZIP (config.pref + .p12 + MANIFEST).</summary>
public sealed class SoftCertImporter
{
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;

    public SoftCertImporter(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
    }

    public SoftCertImportResult ImportZip(string zipPath, string? displayName = null)
    {
        if (!File.Exists(zipPath))
            return Fail("ZIP file not found.");

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            string? prefXml = null;
            byte[]? clientP12 = null;
            byte[]? trustP12 = null;
            string? clientP12Name = null;
            string? trustP12Name = null;

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var fileName = Path.GetFileName(name);
                if (string.IsNullOrEmpty(fileName)) continue;

                if (fileName.Equals("config.pref", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".pref", StringComparison.OrdinalIgnoreCase))
                {
                    using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                    prefXml = sr.ReadToEnd();
                }
                else if (fileName.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
                         fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    entry.Open().CopyTo(ms);
                    var bytes = ms.ToArray();
                    if (fileName.Contains("trust", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("ca", StringComparison.OrdinalIgnoreCase))
                    {
                        trustP12 = bytes;
                        trustP12Name = fileName;
                    }
                    else
                    {
                        clientP12 ??= bytes;
                        clientP12Name ??= fileName;
                    }
                }
            }

            if (clientP12 is null && trustP12 is not null)
            {
                clientP12 = trustP12;
                clientP12Name = trustP12Name;
            }

            var host = "";
            var port = 8089;
            var protocol = "ssl";
            string? callsign = null, team = null, role = null;
            string? password = null;

            if (!string.IsNullOrWhiteSpace(prefXml))
            {
                ParsePref(prefXml, ref host, ref port, ref protocol, ref callsign, ref team, ref role, ref password);
            }

            if (string.IsNullOrWhiteSpace(host))
                return Fail("Could not find server host in SoftCert preferences.");

            var profileId = Guid.NewGuid().ToString("N");
            var clientFile = $"{profileId}-client.p12";
            var trustFile = trustP12 is not null ? $"{profileId}-trust.p12" : null;

            File.WriteAllBytes(Path.Combine(_store.CertsDirectory, clientFile), clientP12 ?? Array.Empty<byte>());
            if (trustP12 is not null && trustFile is not null)
                File.WriteAllBytes(Path.Combine(_store.CertsDirectory, trustFile), trustP12);

            var secretBlob = $"{profileId}-enroll";
            if (!string.IsNullOrEmpty(password))
            {
                _store.WriteSecret(secretBlob, password);
            }

            var certPwdBlob = $"{profileId}-certpwd";
            if (!string.IsNullOrEmpty(password))
                _store.WriteSecret(certPwdBlob, password);
            else
                _store.WriteSecret(certPwdBlob, "atakatak"); // common SoftCert default

            var profile = new ServerProfile
            {
                Id = profileId,
                DisplayName = displayName ?? $"SoftCert {host}",
                Enabled = true,
                Host = host,
                Port = port,
                Protocol = protocol,
                CallsignOverride = callsign,
                TeamOverride = team,
                RoleOverride = role,
                ClientCertFileName = clientFile,
                TrustStoreFileName = trustFile,
                CertPasswordBlobName = certPwdBlob,
                SecretBlobName = string.IsNullOrEmpty(password) ? null : secretBlob,
            };

            _log.Info("Enroll", "SoftCert ZIP imported (host redacted).");
            return new SoftCertImportResult
            {
                Success = true,
                Profile = profile,
                Callsign = callsign,
                Team = team,
                Role = role,
            };
        }
        catch (Exception ex)
        {
            _log.Error("Enroll", "SoftCert import failed", ex);
            return Fail(ex.Message);
        }
    }

    public SoftCertImportResult ImportManual(
        string clientP12Path,
        string? trustPath,
        string clientPassword,
        string? trustPassword,
        string host,
        int port,
        string protocol,
        string? displayName = null)
    {
        if (!File.Exists(clientP12Path))
            return Fail("Client certificate file not found.");
        if (string.IsNullOrWhiteSpace(host))
            return Fail("Host is required.");

        try
        {
            var profileId = Guid.NewGuid().ToString("N");
            var clientFile = $"{profileId}-client{Path.GetExtension(clientP12Path)}";
            File.Copy(clientP12Path, Path.Combine(_store.CertsDirectory, clientFile), overwrite: true);

            string? trustFile = null;
            if (!string.IsNullOrWhiteSpace(trustPath) && File.Exists(trustPath))
            {
                trustFile = $"{profileId}-trust{Path.GetExtension(trustPath)}";
                File.Copy(trustPath, Path.Combine(_store.CertsDirectory, trustFile), overwrite: true);
            }

            var certPwdBlob = $"{profileId}-certpwd";
            _store.WriteSecret(certPwdBlob, clientPassword ?? "");
            string? trustPwdBlob = null;
            if (!string.IsNullOrEmpty(trustPassword))
            {
                trustPwdBlob = $"{profileId}-trustpwd";
                _store.WriteSecret(trustPwdBlob, trustPassword);
            }

            var profile = new ServerProfile
            {
                Id = profileId,
                DisplayName = displayName ?? host,
                Enabled = true,
                Host = host.Trim(),
                Port = port,
                Protocol = protocol is "tcp" ? "tcp" : "ssl",
                ClientCertFileName = clientFile,
                TrustStoreFileName = trustFile,
                CertPasswordBlobName = certPwdBlob,
                TrustPasswordBlobName = trustPwdBlob,
            };

            return new SoftCertImportResult { Success = true, Profile = profile };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static void ParsePref(
        string xml,
        ref string host,
        ref int port,
        ref string protocol,
        ref string? callsign,
        ref string? team,
        ref string? role,
        ref string? password)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "entry" or "preference"))
            {
                var key = (string?)entry.Attribute("key") ?? entry.Attribute("name")?.Value ?? "";
                var value = entry.Attribute("value")?.Value ?? entry.Value;
                if (string.IsNullOrEmpty(key)) continue;

                if (key.Contains("connectString", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("TAKConnection", StringComparison.OrdinalIgnoreCase))
                {
                    // host:port:protocol
                    var m = Regex.Match(value, @"([^:]+):(\d+):(\w+)");
                    if (m.Success)
                    {
                        host = m.Groups[1].Value;
                        port = int.Parse(m.Groups[2].Value);
                        protocol = m.Groups[3].Value.Equals("tcp", StringComparison.OrdinalIgnoreCase) ? "tcp" : "ssl";
                    }
                }
                else if (key.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("host", StringComparison.OrdinalIgnoreCase))
                    host = value;
                else if (key.Contains("port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p))
                    port = p;
                else if (key.Contains("callsign", StringComparison.OrdinalIgnoreCase))
                    callsign = value;
                else if (key.Equals("locationTeam", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("team", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("teamColor", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("locationTeamColor", StringComparison.OrdinalIgnoreCase) ||
                         (key.Contains("team", StringComparison.OrdinalIgnoreCase) &&
                          !key.Contains("steam", StringComparison.OrdinalIgnoreCase) &&
                          !key.Contains("connect", StringComparison.OrdinalIgnoreCase)))
                    team = value;
                else if (key.Contains("role", StringComparison.OrdinalIgnoreCase))
                    role = value;
                else if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                         key.Contains("clientPassword", StringComparison.OrdinalIgnoreCase))
                    password = value;
            }

            // Also scan raw text for connectString patterns
            if (string.IsNullOrEmpty(host))
            {
                var m = Regex.Match(xml, @"([A-Za-z0-9.\-]+):(\d+):(ssl|tcp)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    host = m.Groups[1].Value;
                    port = int.Parse(m.Groups[2].Value);
                    protocol = m.Groups[3].Value.ToLowerInvariant();
                }
            }
        }
        catch
        {
            // fall through — host may still be empty
        }
    }

    private static SoftCertImportResult Fail(string error) =>
        new() { Success = false, Error = error };
}
