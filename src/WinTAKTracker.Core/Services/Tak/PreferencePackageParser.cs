using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinTAKTracker.Services.Identity;

namespace WinTAKTracker.Services.Tak;

/// <summary>
/// Extracts callsign / team / role from ATAK SoftCert-style <c>*.pref</c> XML
/// or Portal Pref mission packages (<c>Pref-*.zip</c> with <c>MANIFEST/manifest.xml</c> +
/// <c>certs/config.pref</c>).
/// </summary>
public static class PreferencePackageParser
{
    public sealed class IdentityPrefs
    {
        public string? Callsign { get; init; }
        public string? Team { get; init; }
        public string? Role { get; init; }
        /// <summary>From MANIFEST <c>onReceiveImport</c>; null when no MANIFEST.</summary>
        public bool? OnReceiveImport { get; init; }
        public bool HasAny =>
            !string.IsNullOrWhiteSpace(Callsign) ||
            !string.IsNullOrWhiteSpace(Team) ||
            !string.IsNullOrWhiteSpace(Role);
    }

    public static IdentityPrefs ParsePrefXml(string xml)
    {
        string? callsign = null, team = null, role = null;
        try
        {
            var doc = XDocument.Parse(xml);
            // Later entries win — Portal duplicates keys under app_civ and app preference blocks.
            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "entry"))
            {
                var key = (string?)entry.Attribute("key") ?? entry.Attribute("name")?.Value ?? "";
                var value = entry.Attribute("value")?.Value ?? entry.Value;
                if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) continue;
                ApplyKey(key, value.Trim(), ref callsign, ref team, ref role, overwrite: true);
            }
        }
        catch
        {
            // fall through to regex scan
        }

        callsign ??= MatchPref(xml, "locationCallsign") ?? MatchPref(xml, "callsign");
        team ??= MatchPref(xml, "locationTeam") ?? MatchPref(xml, "teamColor") ?? MatchPref(xml, "team");
        role ??= MatchPref(xml, "atakRoleType") ?? MatchPref(xml, "locationRole") ?? MatchPref(xml, "role");

        return new IdentityPrefs
        {
            Callsign = callsign,
            Team = RemoteIdentityApply.NormalizeTeam(team),
            Role = role,
        };
    }

    public static IdentityPrefs ParseZipBytes(byte[] bytes)
    {
        string? callsign = null, team = null, role = null;
        bool? onReceiveImport = null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var manifest = FindEntry(zip, "MANIFEST/manifest.xml") ?? FindEntry(zip, "manifest.xml");
            if (manifest is not null)
            {
                using var stream = manifest.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                onReceiveImport = ParseOnReceiveImport(reader.ReadToEnd());
            }

            // Prefer Portal Pref layout: certs/config.pref, then any *.pref.
            var prefEntries = zip.Entries
                .Select(e => new { Entry = e, Path = e.FullName.Replace('\\', '/') })
                .Where(x =>
                    x.Path.EndsWith(".pref", StringComparison.OrdinalIgnoreCase) ||
                    x.Path.EndsWith("config.pref", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Path.EndsWith("certs/config.pref", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in prefEntries)
            {
                using var stream = item.Entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var prefs = ParsePrefXml(text);
                if (!string.IsNullOrWhiteSpace(prefs.Callsign)) callsign = prefs.Callsign;
                if (!string.IsNullOrWhiteSpace(prefs.Team)) team = prefs.Team;
                if (!string.IsNullOrWhiteSpace(prefs.Role)) role = prefs.Role;
            }
        }
        catch
        {
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (text.Contains('<'))
                {
                    var prefs = ParsePrefXml(text);
                    return new IdentityPrefs
                    {
                        Callsign = prefs.Callsign,
                        Team = prefs.Team,
                        Role = prefs.Role,
                        OnReceiveImport = onReceiveImport,
                    };
                }
            }
            catch { /* ignore */ }
        }

        return new IdentityPrefs
        {
            Callsign = callsign,
            Team = RemoteIdentityApply.NormalizeTeam(team),
            Role = role,
            OnReceiveImport = onReceiveImport,
        };
    }

    /// <summary>
    /// True when ZIP looks like a Portal Pref package (MANIFEST + config.pref, or Pref-*.zip identity prefs).
    /// </summary>
    public static bool IsPreferencePackage(byte[] bytes, string? filenameHint = null)
    {
        if (!string.IsNullOrWhiteSpace(filenameHint) &&
            filenameHint.StartsWith("Pref-", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var paths = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            var hasConfig = paths.Any(p =>
                p.EndsWith("certs/config.pref", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("/config.pref", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("config.pref", StringComparison.OrdinalIgnoreCase));
            var hasManifest = paths.Any(p =>
                p.EndsWith("MANIFEST/manifest.xml", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("manifest.xml", StringComparison.OrdinalIgnoreCase));
            if (hasConfig && hasManifest) return true;
            if (hasConfig && ParseZipBytes(bytes).HasAny) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>Auto-import when MANIFEST says so, or when no MANIFEST (device-profile / SoftCert).</summary>
    public static bool ShouldAutoImport(IdentityPrefs prefs) =>
        prefs.OnReceiveImport != false;

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string relativePath) =>
        zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    private static bool? ParseOnReceiveImport(string manifestXml)
    {
        try
        {
            var doc = XDocument.Parse(manifestXml);
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Parameter"))
            {
                var name = (string?)p.Attribute("name");
                if (!string.Equals(name, "onReceiveImport", StringComparison.OrdinalIgnoreCase)) continue;
                var value = (string?)p.Attribute("value") ?? p.Value;
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch { /* ignore */ }

        var m = Regex.Match(
            manifestXml,
            """name\s*=\s*["']onReceiveImport["'][^>]*value\s*=\s*["']([^"']+)["']""",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            if (m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return null;
    }

    private static void ApplyKey(
        string key,
        string value,
        ref string? callsign,
        ref string? team,
        ref string? role,
        bool overwrite)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return;
        if (key.Contains("connect", StringComparison.OrdinalIgnoreCase)) return;

        if (key.Contains("callsign", StringComparison.OrdinalIgnoreCase))
        {
            if (overwrite || callsign is null) callsign = value;
            return;
        }

        if (key.Equals("locationTeam", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("team", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("teamColor", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("locationTeamColor", StringComparison.OrdinalIgnoreCase) ||
            (key.Contains("team", StringComparison.OrdinalIgnoreCase) &&
             !key.Contains("steam", StringComparison.OrdinalIgnoreCase)))
        {
            if (overwrite || team is null) team = value;
            return;
        }

        // Portal Pref packages use atakRoleType (ATAK role preference key).
        if (key.Equals("atakRoleType", StringComparison.OrdinalIgnoreCase) ||
            (key.Contains("role", StringComparison.OrdinalIgnoreCase) &&
             !key.Contains("enroll", StringComparison.OrdinalIgnoreCase)))
        {
            if (overwrite || role is null) role = value;
        }
    }

    private static string? MatchPref(string xml, string key)
    {
        var pattern = $@"(?:key|name)\s*=\s*[""']{Regex.Escape(key)}[""'][^>]*value\s*=\s*[""']([^""']+)[""']";
        var m = Regex.Match(xml, pattern, RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        pattern = $@"(?:key|name)\s*=\s*[""']{Regex.Escape(key)}[""'][^>]*>([^<]+)<";
        m = Regex.Match(xml, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
