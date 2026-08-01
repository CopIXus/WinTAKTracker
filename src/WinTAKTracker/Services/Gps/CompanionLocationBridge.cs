using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Ipc;

namespace WinTAKTracker.Services.Gps;

/// <summary>
/// Acquires Windows Location in the interactive tray session and feeds fixes to the
/// Windows Service over IPC. LocalSystem cannot use WinRT location the way a logged-on user can.
/// </summary>
public sealed class CompanionLocationBridge : IDisposable
{
    private readonly IRedactedLogger _log;
    private readonly WindowsLocationGps _windows = new();
    private TrackerIpcClient? _client;
    private bool _started;
    private DateTimeOffset _lastPushUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinPushInterval = TimeSpan.FromMilliseconds(750);

    public CompanionLocationBridge(IRedactedLogger log)
    {
        _log = log;
        _windows.FixReceived += OnFix;
        _windows.ErrorOccurred += (_, msg) => _log.Warn("GPS/Companion", msg);
        _windows.StatusMessage += (_, msg) => _log.Info("GPS/Companion", msg);
    }

    public GpsPermissionState PermissionState => _windows.PermissionState;
    public bool IsRunning => _started;

    public async Task StartAsync(TrackerIpcClient client)
    {
        _client = client;
        if (_started) return;
        _started = true;
        await _windows.StartAsync().ConfigureAwait(false);
        _log.Info("GPS/Companion", "Tray Windows Location bridge started (feeds service over IPC).");
    }

    public Task<GpsPermissionState> RequestAccessAsync() => _windows.RequestAccessAsync();

    public async Task StopAsync()
    {
        if (!_started && _client is null) return;
        _started = false;
        _windows.Stop();
        var client = _client;
        _client = null;
        if (client is null) return;
        try
        {
            await client.ClearGpsFixAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("GPS/Companion", $"ClearGpsFix failed: {ex.Message}");
        }
    }

    private async void OnFix(object? sender, GpsFix fix)
    {
        var client = _client;
        if (!_started || client is null || !fix.HasFix) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPushUtc < MinPushInterval) return;
        _lastPushUtc = now;

        try
        {
            await client.PushGpsFixAsync(new GpsFixDto
            {
                Latitude = fix.Latitude,
                Longitude = fix.Longitude,
                AltitudeMeters = fix.AltitudeMeters,
                SpeedMetersPerSecond = fix.SpeedMetersPerSecond,
                CourseDegrees = fix.CourseDegrees,
                AccuracyMeters = fix.AccuracyMeters,
                TimestampUtc = fix.Timestamp,
                Source = nameof(GpsSourceKind.Companion),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("GPS/Companion", $"PushGpsFix failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _started = false;
        _windows.FixReceived -= OnFix;
        _windows.Dispose();
        _client = null;
    }
}
