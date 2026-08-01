using System.Runtime.InteropServices;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Tak;

namespace WinTAKTracker.Services.Reporting;

/// <summary>
/// Dual-path (reliable server / unreliable mesh) CoT reporting with ASAP triggers.
/// Continues while the Windows session is locked / from a Windows Service when GPS is available.
/// </summary>
public sealed class ReportingEngine : IDisposable
{
    private readonly IGpsService _gps;
    private readonly ITakConnectionManager _tak;
    private readonly MeshSaBroadcaster _mesh;
    private readonly PauseService _pause;
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly Func<ActiveIdentity>? _identityProvider;
    private readonly object _gate = new();
    private AppConfig _config = new();
    private IReportingRate _rate = new AdaptiveReportingRate(new ReportingSettings());
    private System.Threading.Timer? _timer;
    private static readonly TimeSpan CotSendTimeout = TimeSpan.FromSeconds(8);

    private DateTimeOffset _lastReliable = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUnreliable = DateTimeOffset.MinValue;
    private double? _prevAlt;
    private double? _prevSpeedMph;
    private bool _identityDirty;
    private bool _asap;
    private int _tickBusy;

    public DateTimeOffset? LastPliSentUtc { get; private set; }
    public event EventHandler? Reported;

    public ReportingEngine(
        IGpsService gps,
        ITakConnectionManager tak,
        MeshSaBroadcaster mesh,
        PauseService pause,
        AppConfigStore store,
        IRedactedLogger log,
        Func<ActiveIdentity>? identityProvider = null)
    {
        _gps = gps;
        _tak = tak;
        _mesh = mesh;
        _pause = pause;
        _store = store;
        _log = log;
        _identityProvider = identityProvider;
        _gps.FixChanged += (_, _) => MaybeAsapFromFix();
    }

    public void Start(AppConfig config)
    {
        ApplyConfig(config);
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 500);
    }

    public void ApplyConfig(AppConfig config)
    {
        lock (_gate)
        {
            _config = config;
            _rate = ReportingRateFactory.Create(config.Reporting);
            _mesh.ApplySettings(config.MeshSa);
        }
    }

    public void NotifyIdentityChanged()
    {
        lock (_gate)
        {
            _identityDirty = true;
            _asap = true;
        }
    }

    public void RequestAsap()
    {
        lock (_gate) _asap = true;
    }

    /// <summary>
    /// TAK Server moves a TLS socket from <c>tls:N</c> → identified client on the first PLI.
    /// Call as soon as a server connects so callsign/UID/takv show in the connections UI.
    /// </summary>
    public Task AnnouncePresenceAsync() => AnnouncePresenceCoreAsync();

    private void MaybeAsapFromFix()
    {
        var fix = _gps.CurrentFix;
        if (fix is null) return;
        lock (_gate)
        {
            if (_rate.ShouldReportAsap(_prevAlt, fix.AltitudeMeters, _prevSpeedMph, fix.SpeedMph))
                _asap = true;
        }
    }

    private void Tick()
    {
        // Serialize CoT sends — skip overlapping ticks while a send is in flight.
        if (Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0)
            return;
        _ = TickCoreAsync();
    }

    private async Task TickCoreAsync()
    {
        try
        {
            await TickCoreBodyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Report", $"Tick error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private async Task AnnouncePresenceCoreAsync()
    {
        if (_pause.IsPaused) return;
        if (!_tak.AnyConnected) return;

        var fix = _gps.CurrentFix;
        if (fix is null)
        {
            // No GPS yet — still announce identity so TAK Server can bind callsign/UID to the TLS session.
            fix = new GpsFix
            {
                Latitude = 0,
                Longitude = 0,
                AltitudeMeters = 0,
                AccuracyMeters = 9999999,
                Timestamp = DateTimeOffset.UtcNow,
                Source = GpsSourceKind.NetworkIp,
                IsHeld = true,
            };
        }

        AppConfig config;
        IReportingRate rate;
        lock (_gate)
        {
            config = _config;
            rate = _rate;
            _asap = true;
        }

        var battery = TryGetBatteryPercent();
        var active = _identityProvider?.Invoke();
        var identity = active is not null
            ? CotEventBuilder.FromActiveIdentity(config, active, battery: battery)
            : CotEventBuilder.FromConfig(config, battery: battery);
        var stale = rate.GetStale(TimeSpan.FromSeconds(Math.Max(config.Reporting.ReliableMinSeconds, 30)));
        var cot = CotEventBuilder.Build(fix, identity, stale);

        try
        {
            using var timeout = new CancellationTokenSource(CotSendTimeout);
            await _tak.SendToAllAsync(cot, timeout.Token).ConfigureAwait(false);
            _lastReliable = DateTimeOffset.UtcNow;
            LastPliSentUtc = _lastReliable;
            lock (_gate)
            {
                _asap = false;
                _identityDirty = false;
            }

            _log.Info("Report", $"Presence announced callsign={identity.Callsign} uid={identity.Uid}.");
            Reported?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Warn("Report", $"Presence announce failed: {ex.GetType().Name}");
            RequestAsap();
        }
    }

    private async Task TickCoreBodyAsync()
    {
        if (_pause.IsPaused) return;
        var fix = _gps.CurrentFix;
        if (fix is null) return;

        AppConfig config;
        IReportingRate rate;
        bool asap;
        lock (_gate)
        {
            config = _config;
            rate = _rate;
            asap = _asap || _identityDirty;
        }

        var speed = fix.SpeedMph;
        var reliableDue = asap || DateTimeOffset.UtcNow - _lastReliable >= rate.GetInterval(ReportingPath.Reliable, speed);
        var unreliableDue = asap || DateTimeOffset.UtcNow - _lastUnreliable >= rate.GetInterval(ReportingPath.Unreliable, speed);

        if (!reliableDue && !unreliableDue) return;

        var battery = TryGetBatteryPercent();
        var active = _identityProvider?.Invoke();
        var identity = active is not null
            ? CotEventBuilder.FromActiveIdentity(config, active, battery: battery)
            : CotEventBuilder.FromConfig(config, battery: battery);
        var stale = rate.GetStale(rate.GetInterval(ReportingPath.Reliable, speed));
        var cot = CotEventBuilder.Build(fix, identity, stale);

        if (reliableDue && _tak.AnyConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CotSendTimeout);
                await _tak.SendToAllAsync(cot, timeout.Token).ConfigureAwait(false);
                _lastReliable = DateTimeOffset.UtcNow;
                LastPliSentUtc = _lastReliable;
            }
            catch (Exception ex)
            {
                _log.Warn("Report", $"Reliable CoT send timed out or failed: {ex.GetType().Name}");
            }
        }

        if (unreliableDue && ShouldSendMesh(config))
        {
            if (_mesh.TrySend(cot))
            {
                _lastUnreliable = DateTimeOffset.UtcNow;
                LastPliSentUtc = _lastUnreliable;
            }
        }

        lock (_gate)
        {
            _prevAlt = fix.AltitudeMeters;
            _prevSpeedMph = speed;
            _asap = false;
            _identityDirty = false;
        }

        Reported?.Invoke(this, EventArgs.Empty);
    }

    private bool ShouldSendMesh(AppConfig config)
    {
        if (!config.MeshSa.Enabled) return false;
        if (config.MeshSa.Mode.Equals("OnlyWhenDisconnected", StringComparison.OrdinalIgnoreCase))
            return !_tak.AnyConnected;
        return true; // Always
    }

    private static int? TryGetBatteryPercent()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return null;
            // 255 = unknown
            if (status.BatteryLifePercent is 255 or > 100) return null;
            return status.BatteryLifePercent;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
