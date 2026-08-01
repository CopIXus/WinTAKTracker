using WinTAKTracker.Services.Config;

namespace WinTAKTracker.Services.Reporting;

public enum ReportingPath
{
    Reliable,
    Unreliable,
}

public interface IReportingRate
{
    TimeSpan GetInterval(ReportingPath path, double speedMph);
    bool ShouldReportAsap(double? previousAltM, double? currentAltM, double? previousSpeedMph, double currentSpeedMph);
    TimeSpan GetStale(TimeSpan interval);
}

/// <summary>ATAK-style Dynamic reporting rate (reliable vs unreliable).</summary>
public sealed class AdaptiveReportingRate : IReportingRate
{
    private readonly ReportingSettings _settings;

    public AdaptiveReportingRate(ReportingSettings settings) => _settings = settings;

    public TimeSpan GetInterval(ReportingPath path, double speedMph)
    {
        if (double.IsNaN(speedMph) || double.IsInfinity(speedMph))
            speedMph = 0;

        var stationary = path == ReportingPath.Reliable
            ? _settings.ReliableStationarySeconds
            : _settings.UnreliableStationarySeconds;
        var min = path == ReportingPath.Reliable
            ? _settings.ReliableMinSeconds
            : _settings.UnreliableMinSeconds;
        var maxMove = path == ReportingPath.Reliable
            ? _settings.ReliableMaxMoveSeconds
            : _settings.UnreliableMaxMoveSeconds;

        // Floor at 5s so Dynamic rates never hammer TAK/mesh.
        if (speedMph < 1.0)
            return TimeSpan.FromSeconds(Math.Max(5, stationary));

        if (speedMph >= 30.0)
            return TimeSpan.FromSeconds(Math.Max(5, min));

        // Linear interpolate 1–30 mph: maxMove → min
        var t = (speedMph - 1.0) / 29.0;
        var seconds = maxMove + (min - maxMove) * t;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            seconds = stationary;
        return TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    public bool ShouldReportAsap(double? previousAltM, double? currentAltM, double? previousSpeedMph, double currentSpeedMph)
    {
        if (previousAltM.HasValue && currentAltM.HasValue &&
            Math.Abs(currentAltM.Value - previousAltM.Value) > 50)
            return true;

        if (previousSpeedMph.HasValue && Math.Abs(currentSpeedMph - previousSpeedMph.Value) > 7)
            return true;

        return false;
    }

    public TimeSpan GetStale(TimeSpan interval) => interval * 2 + TimeSpan.FromSeconds(15);
}

/// <summary>Fixed-interval reporting for both paths.</summary>
public sealed class ConstantReportingRate : IReportingRate
{
    private readonly ReportingSettings _settings;

    public ConstantReportingRate(ReportingSettings settings) => _settings = settings;

    public TimeSpan GetInterval(ReportingPath path, double speedMph) =>
        TimeSpan.FromSeconds(Math.Max(5, _settings.ConstantIntervalSeconds));

    public bool ShouldReportAsap(double? previousAltM, double? currentAltM, double? previousSpeedMph, double currentSpeedMph) =>
        false;

    public TimeSpan GetStale(TimeSpan interval) => interval * 2 + TimeSpan.FromSeconds(15);
}

public static class ReportingRateFactory
{
    public static IReportingRate Create(ReportingSettings settings) =>
        string.Equals(settings.Strategy, "Constant", StringComparison.OrdinalIgnoreCase)
            ? new ConstantReportingRate(settings)
            : new AdaptiveReportingRate(settings);
}
