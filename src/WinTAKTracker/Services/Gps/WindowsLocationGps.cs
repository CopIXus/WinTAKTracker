using Windows.Devices.Geolocation;

namespace WinTAKTracker.Services.Gps;

/// <summary>Windows Location API provider (built-in GPS / location providers).</summary>
public sealed class WindowsLocationGps : IDisposable
{
    private Geolocator? _geolocator;
    private bool _running;

    public event EventHandler<GpsFix>? FixReceived;
    public event EventHandler<string>? ErrorOccurred;

    public GpsPermissionState PermissionState { get; private set; } = GpsPermissionState.Unknown;

    public async Task<GpsPermissionState> RequestAccessAsync()
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

        return PermissionState;
    }

    public async Task StartAsync()
    {
        if (_running) return;
        var access = await RequestAccessAsync();
        if (access != GpsPermissionState.Allowed)
        {
            ErrorOccurred?.Invoke(this, "Windows Location permission not granted.");
            return;
        }

        _geolocator = new Geolocator
        {
            DesiredAccuracy = PositionAccuracy.High,
            ReportInterval = 1000,
            MovementThreshold = 0.5,
        };
        _geolocator.PositionChanged += OnPositionChanged;
        _geolocator.StatusChanged += OnStatusChanged;
        _running = true;

        try
        {
            var pos = await _geolocator.GetGeopositionAsync();
            Publish(pos);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    public void Stop()
    {
        _running = false;
        if (_geolocator is null) return;
        _geolocator.PositionChanged -= OnPositionChanged;
        _geolocator.StatusChanged -= OnStatusChanged;
        _geolocator = null;
    }

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args) =>
        Publish(args.Position);

    private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args)
    {
        if (args.Status is PositionStatus.Disabled or PositionStatus.NotAvailable)
            ErrorOccurred?.Invoke(this, $"Location status: {args.Status}");
    }

    private void Publish(Geoposition pos)
    {
        var c = pos.Coordinate;
        var p = c.Point.Position;
        FixReceived?.Invoke(this, new GpsFix
        {
            Latitude = p.Latitude,
            Longitude = p.Longitude,
            AltitudeMeters = double.IsNaN(p.Altitude) ? null : p.Altitude,
            SpeedMetersPerSecond = c.Speed,
            CourseDegrees = c.Heading,
            AccuracyMeters = c.Accuracy,
            Timestamp = c.Timestamp,
            Source = GpsSourceKind.WindowsLocation,
        });
    }

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
