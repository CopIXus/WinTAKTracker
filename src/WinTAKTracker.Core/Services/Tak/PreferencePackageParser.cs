using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinTAKTracker.Services.Identity;

namespace WinTAKTracker.Services.Tak;

/// <summary>
/// Extracts callsign / team / role from ATAK SoftCert-style <c>*.pref</c> XML
/// or data-package ZIPs (Portal / OpenTAK “Send Configuration” / profile updates).
/// </summary>
public static class PreferencePackageParser
{
    public sealed class IdentityPrefs
    {
        public string? Callsign { get; init; }
        public string? Team { get; init; }
        public string? Role { get; init; }
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
            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "entry" or "preference"))
            {
                var key = (string?)entry.Attribute("key") ?? entry.Attribute("name")?.Value ?? "";
                var value = entry.Attribute("value")?.Value ?? entry.Value;
                if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) continue;
                ApplyKey(key, value.Trim(), ref callsign, ref team, ref role);
            }
        }
        catch
        {
            // fall through to regex scan
        }

        // Loose scan for common ATAK preference keys in non-well-formed blobs.
        callsign ??= MatchPref(xml, "locationCallsign") ?? MatchPref(xml, "callsign");
        team ??= MatchPref(xml, "locationTeam") ?? MatchPref(xml, "teamColor") ?? MatchPref(xml, "team");
        role ??= MatchPref(xml, "locationRole") ?? MatchPref(xml, "role");

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
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.EndsWith(".pref", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("config.pref", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var prefs = ParsePrefXml(text);
                callsign ??= prefs.Callsign;
                team ??= prefs.Team;
                role ??= prefs.Role;
            }
        }
        catch
        {
            // Not a ZIP — try as raw pref XML.
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (text.Contains('<'))
                    return ParsePrefXml(text);
            }
            catch { /* ignore */ }
        }

        return new IdentityPrefs
        {
            Callsign = callsign,
            Team = RemoteIdentityApply.NormalizeTeam(team),
            Role = role,
        };
    }

    private static void ApplyKey(string key, string value, ref string? callsign, ref string? team, ref string? role)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return;
        if (key.Contains("connect", StringComparison.OrdinalIgnoreCase)) return;

        if (key.Contains("callsign", StringComparison.OrdinalIgnoreCase))
        {
            callsign = value;
            return;
        }

        // ATAK locationTeam / Portal teamColor — team name is the marker color (Cyan, Blue, …).
        if (key.Equals("locationTeam", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("team", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("teamColor", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("locationTeamColor", StringComparison.OrdinalIgnoreCase) ||
            (key.Contains("team", StringComparison.OrdinalIgnoreCase) &&
             !key.Contains("steam", StringComparison.OrdinalIgnoreCase)))
        {
            team = value;
            return;
        }

        if (key.Contains("role", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("enroll", StringComparison.OrdinalIgnoreCase))
            role = value;
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
