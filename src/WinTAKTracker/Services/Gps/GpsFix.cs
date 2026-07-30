namespace WinTAKTracker.Services.Gps;

public enum GpsSourceKind
{
    None,
    NmeaSerial,
    WindowsLocation,
    Held,
}

public sealed class GpsFix
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public double? AltitudeMeters { get; init; }
    /// <summary>Speed in meters/second.</summary>
    public double? SpeedMetersPerSecond { get; init; }
    /// <summary>Course in degrees true.</summary>
    public double? CourseDegrees { get; init; }
    /// <summary>Horizontal accuracy / CE in meters.</summary>
    public double? AccuracyMeters { get; init; }
    public double? Hdop { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public GpsSourceKind Source { get; init; }
    public bool IsHeld { get; init; }
    public bool HasFix => !double.IsNaN(Latitude) && !double.IsNaN(Longitude);

    public double SpeedMph => (SpeedMetersPerSecond ?? 0) * 2.23693629;

    public GpsFix AsHeld() => new()
    {
        Latitude = Latitude,
        Longitude = Longitude,
        AltitudeMeters = AltitudeMeters,
        SpeedMetersPerSecond = SpeedMetersPerSecond,
        CourseDegrees = CourseDegrees,
        AccuracyMeters = AccuracyMeters.HasValue ? AccuracyMeters.Value * 2 : 50,
        Hdop = Hdop,
        Timestamp = Timestamp,
        Source = GpsSourceKind.Held,
        IsHeld = true,
    };
}

public enum GpsPermissionState
{
    Unknown,
    Allowed,
    Denied,
    NotAvailable,
}
