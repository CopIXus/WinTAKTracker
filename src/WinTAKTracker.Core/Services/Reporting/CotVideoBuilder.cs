using System.Globalization;
using System.Xml.Linq;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;

namespace WinTAKTracker.Services.Reporting;

/// <summary>Live video announce state pushed from the tray into Core/service.</summary>
public sealed class VideoAnnounceState
{
    public bool Active { get; set; }
    public List<VideoFeedAnnounce> Feeds { get; set; } = [];
    public bool SendFovSensorMarker { get; set; } = true;
}

public sealed class VideoFeedAnnounce
{
    public string FeedId { get; set; } = "";
    public string Tag { get; set; } = "cam1";
    public string StreamUrl { get; set; } = "";
    public string Alias { get; set; } = "";
    public string VideoUid { get; set; } = "";
    public double HfovDegrees { get; set; } = 60;
    public double VfovDegrees { get; set; } = 34;
    public double RangeMeters { get; set; } = 100;
    public double AzimuthDegrees { get; set; }
    public double ElevationDegrees { get; set; }
}

/// <summary>ICU-inspired <c>__video</c> / <c>sensor</c> CoT fragments for ATAK / CloudTAK / TAK Aware.</summary>
public static class CotVideoBuilder
{
    public static IEnumerable<XElement> BuildSelfDetailFragments(VideoAnnounceState state)
    {
        if (!state.Active) yield break;
        foreach (var feed in state.Feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.StreamUrl)) continue;
            yield return BuildVideoElement(feed);
            yield return BuildConnectionEntry(feed);
            yield return BuildSensorElement(feed);
            yield return BuildDeviceElement(feed);
        }
    }

    public static string BuildSensorMarkerEvent(
        VideoFeedAnnounce feed,
        GpsFix fix,
        string deviceUid,
        TimeSpan stale)
    {
        var now = DateTimeOffset.UtcNow;
        var uid = string.IsNullOrWhiteSpace(feed.VideoUid)
            ? $"{deviceUid}-SENSOR-{Sanitize(feed.Tag)}"
            : feed.VideoUid.Replace("-VIDEO", "-SENSOR", StringComparison.OrdinalIgnoreCase);
        if (!uid.Contains("SENSOR", StringComparison.OrdinalIgnoreCase))
            uid = $"{uid}-SENSOR";
        var evt = new XElement("event",
            new XAttribute("version", "2.0"),
            new XAttribute("uid", uid),
            new XAttribute("type", "b-m-p-s-p-loc"),
            new XAttribute("how", "h-e"),
            new XAttribute("time", CotEventBuilder.FormatTakTime(now)),
            new XAttribute("start", CotEventBuilder.FormatTakTime(now)),
            new XAttribute("stale", CotEventBuilder.FormatTakTime(now + stale)),
            new XElement("point",
                new XAttribute("lat", CotEventBuilder.F(fix.Latitude)),
                new XAttribute("lon", CotEventBuilder.F(fix.Longitude)),
                new XAttribute("hae", CotEventBuilder.F(fix.AltitudeMeters ?? 0)),
                new XAttribute("ce", "9999999"),
                new XAttribute("le", "9999999")),
            new XElement("detail",
                new XElement("contact", new XAttribute("callsign", feed.Alias)),
                BuildVideoElement(feed),
                BuildConnectionEntry(feed),
                BuildDeviceElement(feed),
                BuildSensorElement(feed)));
        return CotEventBuilder.Finalize(evt);
    }

    public static string BuildCollapsedSensorEvent(VideoFeedAnnounce feed, GpsFix fix, string deviceUid)
    {
        var collapsed = new VideoFeedAnnounce
        {
            FeedId = feed.FeedId,
            Tag = feed.Tag,
            StreamUrl = feed.StreamUrl,
            Alias = feed.Alias,
            VideoUid = feed.VideoUid,
            HfovDegrees = 0,
            RangeMeters = 0,
            AzimuthDegrees = feed.AzimuthDegrees,
            ElevationDegrees = feed.ElevationDegrees,
        };
        return BuildSensorMarkerEvent(collapsed, fix, deviceUid, TimeSpan.FromSeconds(15));
    }

    public static double ResolveAzimuth(AppConfig config, VideoFeedSettings feed, double? courseDegrees)
    {
        var baseCourse = courseDegrees ?? 0;
        return CotEventBuilder.NormalizeCourse(
            baseCourse + config.Gps.CourseOffsetDegrees + feed.AzimuthOffsetDegrees);
    }

    public static string MakeVideoUid(string? deviceUid, string tag) =>
        $"WTT-VID-{Sanitize(deviceUid ?? Environment.MachineName)}-{Sanitize(tag)}";

    public static string MakeAlias(string callsign, string tag)
    {
        var t = Sanitize(tag);
        if (string.IsNullOrWhiteSpace(t) || t.Equals("cam1", StringComparison.OrdinalIgnoreCase))
            return callsign;
        return $"{callsign}-{t}";
    }

    private static XElement BuildVideoElement(VideoFeedAnnounce feed) =>
        new XElement("__video",
            new XAttribute("uid", feed.VideoUid),
            new XAttribute("url", feed.StreamUrl));

    private static XElement BuildConnectionEntry(VideoFeedAnnounce feed)
    {
        var parsed = TryParseUrl(feed.StreamUrl);
        var reliable = feed.StreamUrl.Contains("tcp", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
        return new XElement("ConnectionEntry",
            new XAttribute("uid", feed.VideoUid),
            new XAttribute("alias", feed.Alias),
            new XAttribute("address", parsed.Host),
            new XAttribute("port", parsed.Port.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("path", parsed.Path),
            new XAttribute("protocol", parsed.Protocol),
            new XAttribute("networkTimeout", "5000"),
            new XAttribute("bufferTime", "-1"),
            new XAttribute("roverPort", "-1"),
            new XAttribute("rtspReliable", reliable),
            new XAttribute("ignoreEmbeddedKLV", "false"));
    }

    private static XElement BuildSensorElement(VideoFeedAnnounce feed) =>
        new XElement("sensor",
            new XAttribute("elevation", CotEventBuilder.F(feed.ElevationDegrees)),
            new XAttribute("vfov", CotEventBuilder.F(feed.VfovDegrees)),
            new XAttribute("roll", "0"),
            new XAttribute("range", CotEventBuilder.F(feed.RangeMeters)),
            new XAttribute("azimuth", CotEventBuilder.F(feed.AzimuthDegrees)),
            new XAttribute("fov", CotEventBuilder.F(feed.HfovDegrees)),
            new XAttribute("fovRed", "0.0"),
            new XAttribute("fovGreen", "0.6"),
            new XAttribute("fovBlue", "1.0"),
            new XAttribute("fovAlpha", "0.3"),
            new XAttribute("displayMagneticReference", "0"));

    private static XElement BuildDeviceElement(VideoFeedAnnounce feed) =>
        new XElement("device",
            new XAttribute("azimuth", CotEventBuilder.F(feed.AzimuthDegrees)),
            new XAttribute("pitch", CotEventBuilder.F(feed.ElevationDegrees)));

    private static UrlParts TryParseUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme.ToLowerInvariant();
            if (protocol.StartsWith("rtmps", StringComparison.Ordinal)) protocol = "rtmps";
            else if (protocol.StartsWith("rtmp", StringComparison.Ordinal)) protocol = "rtmp";
            else if (protocol.StartsWith("rtsps", StringComparison.Ordinal)) protocol = "rtsps";
            else if (protocol.StartsWith("rtsp", StringComparison.Ordinal)) protocol = "rtsp";
            else if (protocol.StartsWith("udp", StringComparison.Ordinal)) protocol = "udp";
            var port = uri.Port > 0
                ? uri.Port
                : protocol switch
                {
                    "rtmp" or "rtmps" => 1935,
                    "udp" => 5004,
                    _ => 8554,
                };
            return new UrlParts(protocol, uri.Host, port, string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath);
        }
        catch
        {
            return new UrlParts("rtsp", "127.0.0.1", 8554, "/live");
        }
    }

    public static string Sanitize(string value)
    {
        var chars = value.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "cam" : s;
    }

    private readonly record struct UrlParts(string Protocol, string Host, int Port, string Path);
}
