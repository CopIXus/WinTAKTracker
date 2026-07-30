using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Startup;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;
using WinTAKTracker.Services.Update;

namespace WinTAKTracker.Services;

/// <summary>Composition root for background services.</summary>
public sealed class AppHost : IDisposable
{
    public AppConfigStore ConfigStore { get; }
    public AppConfig Config { get; private set; }
    public SettingsLockService SettingsLock { get; }
    public IRedactedLogger Log { get; }
    public PauseService Pause { get; }
    public IGpsService Gps { get; }
    public MeshSaBroadcaster Mesh { get; }
    public ITakConnectionManager Tak { get; }
    public EnrollmentService Enrollment { get; }
    public SoftCertImporter SoftCert { get; }
    public ReportingEngine Reporting { get; }
    public StatusExporter StatusExporter { get; }
    public IUpdateService Updates { get; }
    public PowerService Power { get; }
    public TrayIconService Tray { get; }

    private System.Threading.Timer? _updateTimer;
    private System.Threading.Timer? _trayTimer;

    public AppHost()
    {
        ConfigStore = new AppConfigStore();
        ConfigStore.EnsureDirectories();
        Config = ConfigStore.Load();
        SettingsLock = new SettingsLockService(ConfigStore);
        EnsureDeviceUid();
        EnsureDefaultCallsign();

        Log = new RedactedLogger(ConfigStore.LogsDirectory);
        if (Enum.TryParse<LogLevel>(Config.Diagnostics.LogLevel, true, out var level))
            Log.SetMinLevel(level);

        Pause = new PauseService();
        Gps = new GpsService(Log);
        Mesh = new MeshSaBroadcaster(Log);
        SoftCert = new SoftCertImporter(ConfigStore, Log);
        Enrollment = new EnrollmentService(ConfigStore, SoftCert, Log);
        Tak = new TakConnectionManager(ConfigStore, Log);
        Reporting = new ReportingEngine(Gps, Tak, Mesh, Pause, ConfigStore, Log);
        StatusExporter = new StatusExporter();
        Updates = new UpdateService(ConfigStore, Log, () => Config.Updates);
        Power = new PowerService();
        Tray = new TrayIconService(this);
    }

    public async Task StartAsync()
    {
        StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
        Power.SetPreventSleep(Config.Startup.PreventSleepWhileTracking && !Pause.IsPaused);

        Pause.PauseChanged += OnPauseChanged;
        Tak.StatusChanged += (_, _) => RefreshTray();
        Gps.FixChanged += (_, _) => RefreshTray();
        Reporting.Reported += (_, _) => RefreshTray();

        await Gps.StartAsync(Config.Gps);
        Mesh.ApplySettings(Config.MeshSa);
        await Tak.StartAsync(Config);
        Reporting.Start(Config);

        _trayTimer = new System.Threading.Timer(_ => RefreshTray(), null, 1000, 2000);
        _updateTimer = new System.Threading.Timer(async _ => await CheckUpdatesQuietAsync(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

        RefreshTray();
        Log.Info("App", "WinTAKTracker started.");
    }

    public void SaveConfig()
    {
        ConfigStore.Save(Config);
        Reporting.ApplyConfig(Config);
        Power.SetPreventSleep(Config.Startup.PreventSleepWhileTracking && !Pause.IsPaused);
        StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
    }

    public async Task ReloadConnectionsAsync()
    {
        SaveConfig();
        await Gps.ApplySettingsAsync(Config.Gps);
        Mesh.ApplySettings(Config.MeshSa);
        await Tak.ReloadAsync(Config);
        Reporting.NotifyIdentityChanged();
        RefreshTray();
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        Power.SetPreventSleep(!paused && Config.Startup.PreventSleepWhileTracking);
        RefreshTray();
        Tray.ShowBalloon(paused ? "Tracking paused" : "Tracking resumed",
            paused ? "Outbound CoT muted (servers + mesh)." : "PLI reporting active.");
    }

    public void RefreshTray()
    {
        var state = ComputeTrayState();
        Tray.SetState(state);
    }

    private TrayIconState ComputeTrayState()
    {
        if (Pause.IsPaused) return TrayIconState.Paused;

        var hasFix = Gps.CurrentFix is not null;
        var anyConnected = Tak.AnyConnected;
        var reconnecting = Tak.AnyReconnecting;
        var meshOk = Config.MeshSa.Enabled && Mesh.IsReady && Mesh.LastErrorCode is null;
        var meshSending = meshOk && Mesh.LastSendUtc.HasValue;

        if (!hasFix) return TrayIconState.NoGps;
        if (reconnecting && !anyConnected) return TrayIconState.Reconnecting;
        if (anyConnected || meshSending || (meshOk && Config.MeshSa.Enabled))
        {
            if (anyConnected || meshOk) return TrayIconState.Ok;
        }

        return TrayIconState.Disconnected;
    }

    private async Task CheckUpdatesQuietAsync()
    {
        try
        {
            var result = await Updates.CheckAsync();
            if (!result.Success || !result.UpdateAvailable) return;

            if (Config.Updates.AutomaticallyDownloadAndInstall)
            {
                var (ok, msg) = await Updates.DownloadAndApplyAsync(result);
                if (ok)
                {
                    // Balloon is non-blocking; helper waits for PID then swaps + relaunches.
                    Tray.ShowBalloon("Updating", msg);
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
            }
            else
            {
                Tray.ShowBalloon("Update available",
                    $"Version {result.LatestVersion} is available. Open Settings → Updates.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Update", ex.Message);
        }
    }

    private void EnsureDeviceUid()
    {
        if (!string.IsNullOrWhiteSpace(Config.DeviceUid)) return;
        try
        {
            var guid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", null) as string;
            Config.DeviceUid = string.IsNullOrWhiteSpace(guid)
                ? "WIN-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()
                : "WIN-" + guid.Replace("-", "")[..Math.Min(16, guid.Replace("-", "").Length)].ToUpperInvariant();
        }
        catch
        {
            Config.DeviceUid = "WIN-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        }

        ConfigStore.Save(Config);
    }

    /// <summary>
    /// Persist Windows computer name when callsign was never set (empty or legacy hard-coded default).
    /// Enrollment / SoftCert / Identity UI overwrite this when the user sets a real callsign.
    /// </summary>
    private void EnsureDefaultCallsign()
    {
        var current = Config.Identity.Callsign?.Trim() ?? "";
        if (current.Length > 0 &&
            !current.Equals("WIN-TRACKER", StringComparison.OrdinalIgnoreCase))
            return;

        Config.Identity.Callsign = Environment.MachineName;
        ConfigStore.Save(Config);
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _trayTimer?.Dispose();
        Reporting.Dispose();
        (Tak as IDisposable)?.Dispose();
        Mesh.Dispose();
        Gps.Stop();
        (Gps as IDisposable)?.Dispose();
        Power.Dispose();
        Tray.Dispose();
        (Log as IDisposable)?.Dispose();
    }
}

