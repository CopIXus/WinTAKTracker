using System.Globalization;
using System.Text;
using System.Xml.Linq;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Identity;

namespace WinTAKTracker.Services.Reporting;

public sealed class CotIdentity
{
    public required string Uid { get; init; }
    public required string Callsign { get; init; }
    public required string Team { get; init; }
    public required string Role { get; init; }
    public required string CotType { get; init; }
    /// <summary>Optional; emitted as contact@phone only when non-whitespace.</summary>
    public string? Phone { get; init; }
    /// <summary>Optional remarks text (e.g. computer name when callsign differs).</summary>
    public string? Remarks { get; init; }
    public string Platform { get; init; } = "WinTAKTracker";
    public string Version { get; init; } = "0.1.0";
    public int? BatteryPercent { get; init; }
}

public static class CotEventBuilder
{
    public const string GroundUnitType = "a-f-G-U-C-I";
    public const string VehicleType = "a-f-G-E-V";

    public static string Build(GpsFix fix, CotIdentity identity, TimeSpan stale)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now;
        var time = fix.Timestamp;
        var staleTime = now + stale;
        // Network IP and held fixes are approximate — use estimated how + large CE.
        var how = fix.IsHeld || fix.Source == GpsSourceKind.NetworkIp ? "h-e" : "m-g";
        var ce = fix.AccuracyMeters ?? (fix.Hdop.HasValue ? fix.Hdop.Value * 5 : 9999999);
        var le = fix.AccuracyMeters ?? 9999999;
        var hae = fix.AltitudeMeters ?? 0;
        var speed = fix.SpeedMetersPerSecond ?? 0;
        var course = fix.CourseDegrees ?? 0;

        var evt = new XElement("event",
            new XAttribute("version", "2.0"),
            new XAttribute("uid", identity.Uid),
            new XAttribute("type", identity.CotType),
            new XAttribute("how", how),
            new XAttribute("time", FormatTakTime(time)),
            new XAttribute("start", FormatTakTime(start)),
            new XAttribute("stale", FormatTakTime(staleTime)),
            new XElement("point",
                new XAttribute("lat", F(fix.Latitude)),
                new XAttribute("lon", F(fix.Longitude)),
                new XAttribute("hae", F(hae)),
                new XAttribute("ce", F(ce)),
                new XAttribute("le", F(le))),
            new XElement("detail",
                BuildContact(identity),
                // ATAK-compatible self-SA fields — TAK Server binds callsign/UID from first PLI.
                new XElement("uid", new XAttribute("Droid", identity.Callsign)),
                new XElement("precisionlocation",
                    new XAttribute("altsrc", fix.IsHeld || fix.Source == GpsSourceKind.NetworkIp ? "DTED0" : "GPS"),
                    new XAttribute("geopointsrc", fix.IsHeld || fix.Source == GpsSourceKind.NetworkIp ? "USER" : "GPS")),
                new XElement("__group",
                    new XAttribute("name", identity.Team),
                    new XAttribute("role", identity.Role)),
                new XElement("track",
                    new XAttribute("speed", F(speed)),
                    new XAttribute("course", F(course))),
                new XElement("takv",
                    new XAttribute("platform", identity.Platform),
                    new XAttribute("version", identity.Version),
                    new XAttribute("device", Environment.MachineName),
                    new XAttribute("os", Environment.OSVersion.VersionString))));

        var detail = evt.Element("detail")!;
        if (!string.IsNullOrWhiteSpace(identity.Remarks))
            detail.Add(new XElement("remarks", identity.Remarks.Trim()));

        if (identity.BatteryPercent is int bat)
            detail.Add(new XElement("status", new XAttribute("battery", bat.ToString(CultureInfo.InvariantCulture))));

        var sb = new StringBuilder();
        sb.Append(evt.ToString(SaveOptions.DisableFormatting));
        sb.Append('\n');
        return sb.ToString();
    }

    public static CotIdentity FromConfig(AppConfig config, ServerProfile? server = null, int? battery = null)
    {
        config.EnsureIdentityDefaults();
        var baseIdentity = IdentityResolver.Resolve(config, activeUserSid: null);
        return FromActiveIdentity(config, baseIdentity, server, battery);
    }

    public static CotIdentity FromActiveIdentity(
        AppConfig config,
        ActiveIdentity active,
        ServerProfile? server = null,
        int? battery = null)
    {
        var uid = config.DeviceUid;
        if (string.IsNullOrWhiteSpace(uid))
        {
            uid = "WIN-" + Environment.MachineName.Replace(" ", "");
        }

        var callsign = server?.CallsignOverride is { Length: > 0 } c ? c : active.Callsign;
        return new CotIdentity
        {
            Uid = uid!,
            Callsign = callsign,
            Team = server?.TeamOverride is { Length: > 0 } t ? t : active.Team,
            Role = server?.RoleOverride is { Length: > 0 } r ? r : active.Role,
            CotType = string.IsNullOrWhiteSpace(active.CotType) ? GroundUnitType : active.CotType,
            Phone = string.IsNullOrWhiteSpace(active.Phone) ? null : active.Phone.Trim(),
            Remarks = BuildComputerNameRemarks(config, callsign),
            Version = typeof(CotEventBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            BatteryPercent = battery,
        };
    }

    /// <summary>
    /// When enabled and the PLI callsign is not the machine name, put the computer name in remarks.
    /// </summary>
    internal static string? BuildComputerNameRemarks(AppConfig config, string callsign)
    {
        if (!config.Reporting.IncludeComputerNameInRemarks) return null;
        var machine = Environment.MachineName?.Trim() ?? "";
        if (machine.Length == 0) return null;
        if (string.Equals(callsign.Trim(), machine, StringComparison.OrdinalIgnoreCase))
            return null;
        return machine;
    }

    private static XElement BuildContact(CotIdentity identity)
    {
        // endpoint=*:-1:stcp matches ATAK self-SA; servers use it when indexing contacts.
        var contact = new XElement("contact",
            new XAttribute("callsign", identity.Callsign),
            new XAttribute("endpoint", "*:-1:stcp"));
        if (!string.IsNullOrWhiteSpace(identity.Phone))
            contact.Add(new XAttribute("phone", identity.Phone.Trim()));
        return contact;
    }

    private static string F(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        return v.ToString("0.#######", CultureInfo.InvariantCulture);
    }

    private static string FormatTakTime(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
