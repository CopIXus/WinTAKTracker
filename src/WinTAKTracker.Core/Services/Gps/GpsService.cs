using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Gps;

public interface IGpsService
{
    GpsFix? CurrentFix { get; }
    GpsPermissionState WindowsPermission { get; }
    bool HasLiveFix { get; }
    event EventHandler<GpsFix?>? FixChanged;
    Task StartAsync(GpsSettings settings);
    Task ApplySettingsAsync(GpsSettings settings);
    void Stop();
    string[] GetComPorts();
    Task<GpsPermissionState> RequestWindowsLocationAccessAsync();
    /// <summary>Accept a fix from the interactive tray (WinRT) over IPC while the service owns tracking.</summary>
    void AcceptExternalFix(GpsFix fix);
    /// <summary>Clear tray-bridged fixes when the companion disconnects.</summary>
    void ClearExternalFix();
}

/// <summary>
/// Orchestrates NMEA serial + Windows Location + optional IP geolocation with last-fix hold.
/// Preference: USB NMEA (if configured) → Windows Location (Wi‑Fi/OS) → IP geolocation last.
/// </summary>
public sealed class GpsService : IGpsService, IDisposable
{
    /// <summary>Give Windows Location time to acquire Wi‑Fi fix before starting coarse IP fallback.</summary>
    private static readonly TimeSpan NetworkFallbackDelay = TimeSpan.FromSeconds(18);

    private readonly IRedactedLogger _log;
    private readonly bool _serviceMode;
    private readonly NmeaSerialGps _nmea = new();
    private readonly WindowsLocationGps _windows = new();
    private readonly NetworkIpGeolocationGps _network = new();
    private readonly object _gate = new();
    private GpsSettings _settings = new();
    private GpsFix? _liveFix;
    private GpsFix? _heldFix;
    private GpsFix? _networkFix;
    private DateTimeOffset? _lastLiveUtc;
    private System.Threading.Timer? _holdTimer;
    private CancellationTokenSource? _networkDelayCts;
    private bool _started;
    private bool _companionFixActive;

    public GpsService(IRedactedLogger log, bool serviceMode = false)
    {
        _log = log;
        _serviceMode = serviceMode;
        _nmea.FixReceived += OnNmeaFix;
        _nmea.ErrorOccurred += (_, msg) => _log.Warn("GPS/NMEA", msg);
        _windows.FixReceived += OnWindowsFix;
        _windows.ErrorOccurred += (_, msg) => _log.Warn("GPS/Windows", msg);
        _windows.StatusMessage += (_, msg) => _log.Info("GPS/Windows", msg);
        _network.FixReceived += OnNetworkFix;
        _network.ErrorOccurred += (_, msg) => _log.Warn("GPS/Network", msg);
    }

    public GpsFix? CurrentFix
    {
        get
        {
            lock (_gate)
            {
                if (_liveFix is not null && IsPrecisionSource(_liveFix.Source))
                    return _liveFix;
                if (_heldFix is not null && IsHoldActive() && IsPrecisionSource(_heldFix.Source))
                    return _heldFix;
                if (_settings.EnableNetworkFallback && _networkFix is not null)
                    return _networkFix;
                if (_liveFix is not null) return _liveFix;
                if (_heldFix is not null && IsHoldActive()) return _heldFix;
                return null;
            }
        }
    }

    public GpsPermissionState WindowsPermission => _windows.PermissionState;
    public bool HasLiveFix
    {
        get { lock (_gate) return _liveFix is not null && IsPrecisionSource(_liveFix.Source); }
    }

    public event EventHandler<GpsFix?>? FixChanged;

    public string[] GetComPorts() => NmeaSerialGps.GetAvailablePorts();

    public Task<GpsPermissionState> RequestWindowsLocationAccessAsync() =>
        _windows.RequestAccessAsync();

    public void AcceptExternalFix(GpsFix fix)
    {
        if (!_started || !fix.HasFix) return;

        // Prefer recent NMEA over tray Wi‑Fi when both are live.
        lock (_gate)
        {
            if (_nmea.IsOpen && _liveFix?.Source == GpsSourceKind.NmeaSerial &&
                _lastLiveUtc.HasValue &&
                (DateTimeOffset.UtcNow - _lastLiveUtc.Value).TotalSeconds < 3)
                return;
        }

        var bridged = new GpsFix
        {
            Latitude = fix.Latitude,
            Longitude = fix.Longitude,
            AltitudeMeters = fix.AltitudeMeters,
            SpeedMetersPerSecond = fix.SpeedMetersPerSecond,
            CourseDegrees = fix.CourseDegrees,
            AccuracyMeters = fix.AccuracyMeters,
            Hdop = fix.Hdop,
            Timestamp = fix.Timestamp,
            Source = fix.Source is GpsSourceKind.None or GpsSourceKind.Held
                ? GpsSourceKind.Companion
                : fix.Source is GpsSourceKind.WindowsLocation
                    ? GpsSourceKind.Companion
                    : fix.Source,
            IsHeld = false,
        };

        lock (_gate) _companionFixActive = true;
        AcceptPrecisionLive(bridged);
        CancelNetworkDelay();
        if (_network.IsRunning)
            _network.Stop();
        lock (_gate) _networkFix = null;
    }

    public void ClearExternalFix()
    {
        lock (_gate)
        {
            if (!_companionFixActive && _liveFix?.Source != GpsSourceKind.Companion)
                return;

            _companionFixActive = false;
            if (_liveFix?.Source == GpsSourceKind.Companion)
                _liveFix = null;
            // Held loses original source via AsHeld(); drop it when companion was the supplier
            // and NMEA is not open (NMEA will republish on its own).
            if (!_nmea.IsOpen)
                _heldFix = null;
        }

        FixChanged?.Invoke(this, CurrentFix);
    }

    public async Task StartAsync(GpsSettings settings)
    {
        _settings = settings;
        _started = true;
        _holdTimer = new System.Threading.Timer(_ => TickHold(), null, 1000, 1000);
        await ApplySettingsAsync(settings);
    }

    public async Task ApplySettingsAsync(GpsSettings settings)
    {
        _settings = settings;
        if (!_started) return;

        var priority = settings.SourcePriority ?? "NmeaThenWindows";
        var useNmea = priority is not "WindowsOnly";
        var useWin = priority is not "NmeaOnly";

        CancelNetworkDelay();
        _nmea.Stop();
        _windows.Stop();
        _network.Stop();
        lock (_gate) _networkFix = null;

        if (useNmea)
        {
            // Only open serial when the user selected a COM port — do not auto-grab an unrelated device.
            var port = settings.ComPort;
            if (!string.IsNullOrWhiteSpace(port) && !port.StartsWith('('))
            {
                try
                {
                    _nmea.Start(port!, settings.BaudRate <= 0 ? 4800 : settings.BaudRate);
                    _log.Info("GPS", $"NMEA serial started on {port} @ {settings.BaudRate}");
                }
                catch (Exception ex)
                {
                    _log.Warn("GPS", $"NMEA open failed: {ex.Message}");
                }
            }
            else
            {
                var ports = GetComPorts();
                _log.Info("GPS", ports.Length == 0
                    ? "No COM port selected; USB NMEA idle."
                    : $"No COM port selected ({ports.Length} port(s) available); USB NMEA idle — using Windows Location.");
            }
        }

        // Session 0 / LocalSystem: WinRT Geolocator is unreliable — companion tray + NMEA + optional IP only.
        if (useWin && !_serviceMode)
            await _windows.StartAsync();
        else if (useWin && _serviceMode)
            _log.Info("GPS", "Windows Location skipped in service mode (tray companion / NMEA / IP only).");
        else
            _log.Info("GPS", "Windows Location disabled by source priority.");

        if (settings.EnableNetworkFallback)
            ScheduleNetworkFallback(useWin && !_serviceMode);
        else
            _log.Info("GPS", "Network IP geolocation fallback disabled.");
    }

    public void Stop()
    {
        _started = false;
        CancelNetworkDelay();
        _holdTimer?.Dispose();
        _holdTimer = null;
        _nmea.Stop();
        _windows.Stop();
        _network.Stop();
    }

    private void ScheduleNetworkFallback(bool windowsEnabled)
    {
        CancelNetworkDelay();
        var cts = new CancellationTokenSource();
        _networkDelayCts = cts;
        _ = WaitThenStartNetworkAsync(windowsEnabled, cts.Token);
    }

    private async Task WaitThenStartNetworkAsync(bool windowsEnabled, CancellationToken ct)
    {
        try
        {
            // If Windows Location is denied/unavailable, fall back quickly; otherwise wait for Wi‑Fi fix.
            var delay = NetworkFallbackDelay;
            if (windowsEnabled)
            {
                var perm = _windows.PermissionState;
                if (perm is GpsPermissionState.Denied or GpsPermissionState.NotAvailable)
                    delay = TimeSpan.FromSeconds(1);
            }
            else
            {
                delay = TimeSpan.FromSeconds(2);
            }

            _log.Info("GPS",
                $"Network IP fallback armed (starts in {delay.TotalSeconds:0}s only if no Windows/NMEA fix).");

            var step = TimeSpan.FromMilliseconds(500);
            var waited = TimeSpan.Zero;
            while (waited < delay)
            {
                ct.ThrowIfCancellationRequested();
                if (!_started || !_settings.EnableNetworkFallback) return;
                if (HasPrecisionFixAvailable()) return;
                // Permission may flip after the consent prompt.
                if (windowsEnabled &&
                    _windows.PermissionState is GpsPermissionState.Denied or GpsPermissionState.NotAvailable &&
                    waited >= TimeSpan.FromSeconds(1))
                    break;
                if (_windows.HasPublishedFix) return;

                await Task.Delay(step, ct);
                waited += step;
            }

            ct.ThrowIfCancellationRequested();
            if (!_started || !_settings.EnableNetworkFallback) return;
            if (HasPrecisionFixAvailable() || _windows.HasPublishedFix) return;

            _network.Start();
            _log.Info("GPS",
                "Network IP geolocation fallback started (ipwho.is, approximate) — Windows Location had no fix.");
        }
        catch (OperationCanceledException)
        {
            // Settings reapplied or service stopped.
        }
        catch (Exception ex)
        {
            _log.Warn("GPS", $"Network fallback schedule error: {ex.Message}");
        }
    }

    private void CancelNetworkDelay()
    {
        try { _networkDelayCts?.Cancel(); } catch { /* ignore */ }
        try { _networkDelayCts?.Dispose(); } catch { /* ignore */ }
        _networkDelayCts = null;
    }

    private bool HasPrecisionFixAvailable()
    {
        lock (_gate)
        {
            if (_liveFix is not null && IsPrecisionSource(_liveFix.Source)) return true;
            if (_heldFix is not null && IsHoldActive() && IsPrecisionSource(_heldFix.Source)) return true;
            return false;
        }
    }

    private void OnNmeaFix(object? sender, GpsFix fix)
    {
        lock (_gate) _companionFixActive = false;
        AcceptPrecisionLive(fix);
    }

    private void OnWindowsFix(object? sender, GpsFix fix)
    {
        // Prefer NMEA when both active and NMEA has recent data.
        lock (_gate)
        {
            if (_nmea.IsOpen && _liveFix?.Source == GpsSourceKind.NmeaSerial &&
                _lastLiveUtc.HasValue &&
                (DateTimeOffset.UtcNow - _lastLiveUtc.Value).TotalSeconds < 3)
                return;
        }

        lock (_gate) _companionFixActive = false;
        AcceptPrecisionLive(fix);
        // Precision fix available — cancel pending IP fallback and clear any IP fix in use.
        CancelNetworkDelay();
        if (_network.IsRunning)
            _network.Stop();
        lock (_gate) _networkFix = null;
    }

    private void OnNetworkFix(object? sender, GpsFix fix)
    {
        lock (_gate)
        {
            _networkFix = fix;
            // Do not displace a live/held precision fix.
            if (_liveFix is not null && IsPrecisionSource(_liveFix.Source))
                return;
            if (_heldFix is not null && IsHoldActive() && IsPrecisionSource(_heldFix.Source))
                return;
        }

        FixChanged?.Invoke(this, CurrentFix);
    }

    private void AcceptPrecisionLive(GpsFix fix)
    {
        lock (_gate)
        {
            _liveFix = fix;
            _heldFix = fix.AsHeld();
            _lastLiveUtc = DateTimeOffset.UtcNow;
        }

        FixChanged?.Invoke(this, fix);
    }

    private void TickHold()
    {
        GpsFix? publish;
        lock (_gate)
        {
            if (_liveFix is not null && IsPrecisionSource(_liveFix.Source) && _lastLiveUtc.HasValue)
            {
                var age = (DateTimeOffset.UtcNow - _lastLiveUtc.Value).TotalSeconds;
                // Windows Location / tray companion report on an interval; allow a slightly longer live window.
                var liveGrace = _liveFix.Source is GpsSourceKind.WindowsLocation or GpsSourceKind.Companion
                    ? 5.0
                    : 2.5;
                if (age > liveGrace)
                {
                    _liveFix = null;
                    if (IsHoldActive())
                        publish = _heldFix;
                    else
                    {
                        _heldFix = null;
                        publish = _settings.EnableNetworkFallback ? _networkFix : null;
                    }

                    goto Emit;
                }
            }

            if (_liveFix is null && _heldFix is not null && !IsHoldActive())
            {
                _heldFix = null;
                publish = _settings.EnableNetworkFallback ? _networkFix : null;
                goto Emit;
            }

            return;
        }

    Emit:
        FixChanged?.Invoke(this, publish);
    }

    private bool IsHoldActive()
    {
        if (!_lastLiveUtc.HasValue || _heldFix is null) return false;
        var hold = Math.Max(0, _settings.LastFixHoldSeconds);
        return (DateTimeOffset.UtcNow - _lastLiveUtc.Value).TotalSeconds <= hold;
    }

    private static bool IsPrecisionSource(GpsSourceKind source) =>
        source is GpsSourceKind.NmeaSerial or GpsSourceKind.WindowsLocation
            or GpsSourceKind.Companion or GpsSourceKind.Held;

    public void Dispose()
    {
        Stop();
        _nmea.Dispose();
        _windows.Dispose();
        _network.Dispose();
    }
}
