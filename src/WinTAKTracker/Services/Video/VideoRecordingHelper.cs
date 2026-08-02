using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using WinTAKTracker.Services.Reporting;

namespace WinTAKTracker.Services.Video;

public static class VideoRecordingHelper
{
    public static string SanitizeFilePart(string value)
    {
        var s = CotVideoBuilder.Sanitize(value);
        return string.IsNullOrEmpty(s) ? "X" : s;
    }

    public static string BuildSegmentBaseName(
        DateTimeOffset utcStart,
        DateTimeOffset localStart,
        string computer,
        string callsign,
        string userName,
        string? tag)
    {
        var zulu = utcStart.UtcDateTime.ToString("yyyy-MMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
        var local = localStart.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        var parts = new[]
        {
            zulu,
            local,
            SanitizeFilePart(computer),
            SanitizeFilePart(callsign),
            SanitizeFilePart(userName),
        };
        var name = string.Join("_", parts);
        if (!string.IsNullOrWhiteSpace(tag) &&
            !tag.Equals("cam1", StringComparison.OrdinalIgnoreCase))
            name += "_" + SanitizeFilePart(tag);
        return name;
    }

    public static string WriteSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var hashPath = filePath + ".sha256";
        File.WriteAllText(hashPath, $"{hex}  {Path.GetFileName(filePath)}\n", Encoding.UTF8);
        return hashPath;
    }

    public static void WriteKmlTrack(string kmlPath, string name, IReadOnlyList<GpsSample> samples)
    {
        var coords = string.Join(" ", samples.Select(s =>
            string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                s.Longitude, s.Latitude, s.AltitudeMeters ?? 0)));

        var placemarks = new List<XElement>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            placemarks.Add(new XElement("Placemark",
                new XElement("name", $"#{i + 1}"),
                new XElement("TimeStamp",
                    new XElement("when",
                        s.Utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))),
                new XElement("Point",
                    new XElement("coordinates",
                        string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                            s.Longitude, s.Latitude, s.AltitudeMeters ?? 0)))));
        }

        var doc = new XDocument(
            new XElement(XNamespace.Get("http://www.opengis.net/kml/2.2") + "kml",
                new XElement("Document",
                    new XElement("name", name),
                    new XElement("Placemark",
                        new XElement("name", name + " track"),
                        new XElement("LineString",
                            new XElement("tessellate", "1"),
                            new XElement("coordinates", coords))),
                    placemarks)));
        doc.Save(kmlPath);
    }

    public static void EnforceFolderLimit(string folder, int maxMb, string policy)
    {
        if (!Directory.Exists(folder) || maxMb <= 0) return;
        var limit = maxMb * 1024L * 1024L;
        while (true)
        {
            var files = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            if (total <= limit) return;
            if (policy.Equals("StopRecording", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recording folder size limit reached.");
            var oldest = files.FirstOrDefault();
            if (oldest is null) return;
            try { oldest.Delete(); } catch { return; }
            TryDelete(oldest.FullName + ".sha256");
            TryDelete(Path.ChangeExtension(oldest.FullName, ".kml"));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

public sealed record GpsSample(DateTimeOffset Utc, double Latitude, double Longitude, double? AltitudeMeters);
