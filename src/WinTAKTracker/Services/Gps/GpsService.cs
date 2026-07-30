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

/// <summary>Orchestrates NMEA serial + Windows Location with last-fix hold.</summary>
public sealed class GpsService : IGpsService, IDisposable
{
    private readonly IRedactedLogger _log;
    private readonly NmeaSerialGps _nmea = new();
    private readonly WindowsLocationGps _windows = new();
    private readonly object _gate = new();
    private GpsSettings _settings = new();
    private GpsFix? _liveFix;
    private GpsFix? _heldFix;
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
    }

    public GpsFix? CurrentFix
    {
        get
        {
            lock (_gate)
            {
                if (_liveFix is not null) return _liveFix;
                if (_heldFix is not null && IsHoldActive()) return _heldFix;
                return null;
            }
        }
    }

    public GpsPermissionState WindowsPermission => _windows.PermissionState;
    public bool HasLiveFix
    {
        get { lock (_gate) return _liveFix is not null; }
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
        {
            // Prefer Windows first when configured, otherwise as fallback after NMEA attempt.
            if (priority is "WindowsThenNmea" or "WindowsOnly" || !_nmea.IsOpen)
                await _windows.StartAsync();
            else
                await _windows.StartAsync(); // keep both when NmeaThenWindows
        }
    }

    public void Stop()
    {
        _started = false;
        _holdTimer?.Dispose();
        _holdTimer = null;
        _nmea.Stop();
        _windows.Stop();
    }

    private void OnNmeaFix(object? sender, GpsFix fix) => AcceptLive(fix);

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

        AcceptLive(fix);
    }

    private void AcceptLive(GpsFix fix)
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
            if (_liveFix is not null && _lastLiveUtc.HasValue)
            {
                var age = (DateTimeOffset.UtcNow - _lastLiveUtc.Value).TotalSeconds;
                if (age > 2.5)
                {
                    // Drop live; may enter hold.
                    _liveFix = null;
                    if (IsHoldActive())
                        publish = _heldFix;
                    else
                    {
                        _heldFix = null;
                        publish = null;
                    }

                    goto Emit;
                }
            }

            if (_liveFix is null && _heldFix is not null && !IsHoldActive())
            {
                _heldFix = null;
                publish = null;
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

    public void Dispose()
    {
        Stop();
        _nmea.Dispose();
        _windows.Dispose();
    }
}
