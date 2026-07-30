using Windows.Devices.Geolocation;
using WinTAKTracker.Services.Host;

namespace WinTAKTracker.Services.Gps;

/// <summary>
/// Windows Location API provider (Wi‑Fi / cell / GNSS via the OS location stack).
/// Prefer this over IP geolocation — same class of fix browsers get when Location is enabled.
/// Unreliable under LocalSystem / after logoff — prefer NMEA or IP for always-on service mode.
/// </summary>
public sealed class WindowsLocationGps : IDisposable
{
    private const uint DesiredAccuracyMeters = 50;
    private static readonly TimeSpan PositionMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PositionTimeout = TimeSpan.FromSeconds(20);
    private const int MaxAcquireAttempts = 4;

    private Geolocator? _geolocator;
    private bool _running;
    private int _acquireAttempt;

    public event EventHandler<GpsFix>? FixReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<string>? StatusMessage;

    public GpsPermissionState PermissionState { get; private set; } = GpsPermissionState.Unknown;
    public PositionStatus? LastPositionStatus { get; private set; }
    public bool IsRunning => _running;
    public bool HasPublishedFix { get; private set; }

    public async Task<GpsPermissionState> RequestAccessAsync()
    {
        await UiThreadMarshal.InvokeAsyncOrDirect(async () =>
        {
            try
            {
                var status = await Geolocator.RequestAccessAsync();
                PermissionState = status switch
                {
                    GeolocationAccessStatus.Allowed => GpsPermissionState.Allowed,
                    GeolocationAccessStatus.Denied => GpsPermissionState.Denied,
                    _ => GpsPermissionState.NotAvailable,
                };
            }
            catch (Exception ex)
            {
                PermissionState = GpsPermissionState.NotAvailable;
                ErrorOccurred?.Invoke(this, ex.Message);
            }
        });
        return PermissionState;
    }

    public async Task StartAsync()
    {
        if (_running) return;
        HasPublishedFix = false;
        _acquireAttempt = 0;

        var access = await RequestAccessAsync();
        if (access != GpsPermissionState.Allowed)
        {
            ErrorOccurred?.Invoke(this,
                access == GpsPermissionState.Denied
                    ? "Windows Location permission denied. Enable Location (and desktop apps) in Settings → Privacy & security → Location."
                    : "Windows Location is not available on this device.");
            return;
        }

        _geolocator = new Geolocator
        {
            // Prefer Wi‑Fi / network positioning when no GNSS dongle is present.
            DesiredAccuracyInMeters = DesiredAccuracyMeters,
            ReportInterval = 2000,
            // 0 = report on interval even while stationary (MovementThreshold would suppress updates).
            MovementThreshold = 0,
        };
        _geolocator.PositionChanged += OnPositionChanged;
        _geolocator.StatusChanged += OnStatusChanged;
        _running = true;
        StatusMessage?.Invoke(this, "Windows Location started (Wi‑Fi/network positioning).");

        // Do not block app startup / UI on Wi‑Fi acquisition (can take several seconds).
        _ = TryAcquirePositionAsync();
    }

    public void Stop()
    {
        _running = false;
        HasPublishedFix = false;
        _acquireAttempt = 0;
        if (_geolocator is null) return;
        _geolocator.PositionChanged -= OnPositionChanged;
        _geolocator.StatusChanged -= OnStatusChanged;
        _geolocator = null;
        LastPositionStatus = null;
    }

    private async void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args)
    {
        LastPositionStatus = args.Status;
        StatusMessage?.Invoke(this, $"Location status: {args.Status}");

        if (!_running) return;

        switch (args.Status)
        {
            case PositionStatus.Ready:
                if (!HasPublishedFix)
                    await TryAcquirePositionAsync();
                break;
            case PositionStatus.Initializing:
                // Normal while Wi‑Fi scan / providers warm up — keep waiting; do not treat as failure.
                break;
            case PositionStatus.NoData:
                // Transient on desktops without GNSS — retry a few times; PositionChanged may still arrive.
                if (!HasPublishedFix && _acquireAttempt < MaxAcquireAttempts)
                    await TryAcquirePositionAsync();
                else if (!HasPublishedFix)
                    ErrorOccurred?.Invoke(this, "Location status: NoData (will keep listening for Wi‑Fi/network fixes).");
                break;
            case PositionStatus.Disabled:
            case PositionStatus.NotAvailable:
                ErrorOccurred?.Invoke(this, $"Location status: {args.Status}");
                break;
        }
    }

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args) =>
        Publish(args.Position);

    private async Task TryAcquirePositionAsync()
    {
        var geo = _geolocator;
        if (!_running || geo is null) return;
        if (_acquireAttempt >= MaxAcquireAttempts) return;
        _acquireAttempt++;

        try
        {
            var pos = await geo.GetGeopositionAsync(PositionMaxAge, PositionTimeout);
            if (_running) Publish(pos);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this,
                $"GetGeoposition attempt {_acquireAttempt}/{MaxAcquireAttempts}: {ex.Message}");

            if (_running && _acquireAttempt < MaxAcquireAttempts)
            {
                try { await Task.Delay(1500); }
                catch { /* ignore */ }
                if (_running && !HasPublishedFix)
                    await TryAcquirePositionAsync();
            }
        }
    }

    private void Publish(Geoposition pos)
    {
        var c = pos.Coordinate;
        var p = c.Point.Position;
        if (double.IsNaN(p.Latitude) || double.IsNaN(p.Longitude))
            return;

        HasPublishedFix = true;
        FixReceived?.Invoke(this, new GpsFix
        {
            Latitude = p.Latitude,
            Longitude = p.Longitude,
            AltitudeMeters = FiniteOrNull(p.Altitude),
            SpeedMetersPerSecond = FiniteOrNull(c.Speed),
            CourseDegrees = FiniteOrNull(c.Heading),
            AccuracyMeters = c.Accuracy > 0 ? FiniteOrNull(c.Accuracy) : null,
            Timestamp = c.Timestamp.ToUniversalTime(),
            Source = GpsSourceKind.WindowsLocation,
        });
    }

    private static double? FiniteOrNull(double? value)
    {
        if (value is not double v) return null;
        return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    private static double? FiniteOrNull(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    public static void OpenWindowsLocationPrivacySettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-location",
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    public void Dispose() => Stop();
}
