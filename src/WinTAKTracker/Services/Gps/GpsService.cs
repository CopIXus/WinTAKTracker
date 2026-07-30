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
}

/// <summary>Orchestrates NMEA serial + Windows Location + optional IP geolocation with last-fix hold.</summary>
public sealed class GpsService : IGpsService, IDisposable
{
    private readonly IRedactedLogger _log;
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
    private bool _started;

    public GpsService(IRedactedLogger log)
    {
        _log = log;
        _nmea.FixReceived += OnNmeaFix;
        _nmea.ErrorOccurred += (_, msg) => _log.Warn("GPS/NMEA", msg);
        _windows.FixReceived += OnWindowsFix;
        _windows.ErrorOccurred += (_, msg) => _log.Warn("GPS/Windows", msg);
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

        _nmea.Stop();
        _windows.Stop();
        _network.Stop();

        if (useNmea)
        {
            var port = settings.ComPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                var ports = GetComPorts();
                port = ports.FirstOrDefault();
            }

            if (!string.IsNullOrWhiteSpace(port))
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
                _log.Info("GPS", "No COM ports available for NMEA.");
            }
        }

        if (useWin)
            await _windows.StartAsync();

        if (settings.EnableNetworkFallback)
        {
            _network.Start();
            _log.Info("GPS", "Network IP geolocation fallback enabled (ipwho.is, approximate).");
        }
        else
        {
            lock (_gate) _networkFix = null;
        }
    }

    public void Stop()
    {
        _started = false;
        _holdTimer?.Dispose();
        _holdTimer = null;
        _nmea.Stop();
        _windows.Stop();
        _network.Stop();
    }

    private void OnNmeaFix(object? sender, GpsFix fix) => AcceptPrecisionLive(fix);

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

        AcceptPrecisionLive(fix);
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
                if (age > 2.5)
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
        source is GpsSourceKind.NmeaSerial or GpsSourceKind.WindowsLocation or GpsSourceKind.Held;

    public void Dispose()
    {
        Stop();
        _nmea.Dispose();
        _windows.Dispose();
        _network.Dispose();
    }
}
