using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinTAKTracker.Services.Tak;

/// <summary>Parsed Marti / ATAK fileshare announce from inbound CoT.</summary>
public sealed class FileShareOffer
{
    public string? Filename { get; init; }
    public string? Sha256 { get; init; }
    public string? SenderUrl { get; init; }
    public long? SizeInBytes { get; init; }

    public bool LooksLikePreferencePackage =>
        !string.IsNullOrWhiteSpace(Filename) &&
        Filename!.StartsWith("Pref-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Extracts fileshare / enterprise-sync offers from CoT XML.</summary>
public static class FileShareCotParser
{
    public static bool LooksLikeFileShareEvent(string xml) =>
        xml.Contains("fileshare", StringComparison.OrdinalIgnoreCase) ||
        xml.Contains("senderUrl", StringComparison.OrdinalIgnoreCase) ||
        (xml.Contains("b-f-t", StringComparison.OrdinalIgnoreCase) &&
         (xml.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
          xml.Contains("hash", StringComparison.OrdinalIgnoreCase)));

    public static FileShareOffer? TryParse(string xml)
    {
        if (!LooksLikeFileShareEvent(xml)) return null;

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var el in doc.Descendants())
            {
                var local = el.Name.LocalName;
                if (!local.Equals("fileshare", StringComparison.OrdinalIgnoreCase) &&
                    !local.Equals("__fileshare", StringComparison.OrdinalIgnoreCase))
                    continue;

                var filename = Attr(el, "filename") ?? Attr(el, "name");
                var sha = Attr(el, "sha256") ?? Attr(el, "hash") ?? Attr(el, "sha256hash");
                var url = Attr(el, "senderUrl") ?? Attr(el, "url");
                long? size = null;
                if (long.TryParse(Attr(el, "sizeInBytes") ?? Attr(el, "size"), out var n))
                    size = n;

                if (string.IsNullOrWhiteSpace(filename) && string.IsNullOrWhiteSpace(sha) &&
                    string.IsNullOrWhiteSpace(url))
                    continue;

                return new FileShareOffer
                {
                    Filename = filename,
                    Sha256 = sha,
                    SenderUrl = url,
                    SizeInBytes = size,
                };
            }
        }
        catch
        {
            // fall through to regex
        }

        var filenameM = Regex.Match(xml, """filename\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);
        var shaM = Regex.Match(xml, """sha256(?:hash)?\s*=\s*["']([A-Fa-f0-9]{32,})["']""", RegexOptions.IgnoreCase);
        if (!shaM.Success)
            shaM = Regex.Match(xml, """\bhash\s*=\s*["']([A-Fa-f0-9]{32,})["']""", RegexOptions.IgnoreCase);
        var urlM = Regex.Match(xml, """senderUrl\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);

        if (!filenameM.Success && !shaM.Success && !urlM.Success) return null;

        return new FileShareOffer
        {
            Filename = filenameM.Success ? filenameM.Groups[1].Value : null,
            Sha256 = shaM.Success ? shaM.Groups[1].Value : null,
            SenderUrl = urlM.Success ? urlM.Groups[1].Value : null,
        };
    }

    private static string? Attr(XElement el, string name) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
