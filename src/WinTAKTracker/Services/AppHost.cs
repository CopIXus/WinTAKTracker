using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Host;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Ipc;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Startup;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;
using WinTAKTracker.Services.Update;

namespace WinTAKTracker.Services;

/// <summary>
/// Tray composition root. When the Windows Service is reachable, attaches via IPC and does not
/// start a second in-process tracker. Otherwise runs TrackingHost in-process (portable mode).
/// </summary>
public sealed class AppHost : IDisposable
{
    public TrackingHost Core { get; }
    public TrackerIpcClient? ServiceClient { get; private set; }
    public bool AttachedToService => ServiceClient is not null;

    public AppConfigStore ConfigStore => Core.ConfigStore;
    public AppConfig Config => Core.Config;
    public SettingsLockService SettingsLock => Core.SettingsLock;
    public IRedactedLogger Log => Core.Log;
    public PauseService Pause => Core.Pause;
    public IGpsService Gps => Core.Gps;
    public MeshSaBroadcaster Mesh => Core.Mesh;
    public ITakConnectionManager Tak => Core.Tak;
    public EnrollmentService Enrollment => Core.Enrollment;
    public SoftCertImporter SoftCert => Core.SoftCert;
    public ReportingEngine Reporting => Core.Reporting;
    public StatusExporter StatusExporter => Core.StatusExporter;
    public IUpdateService Updates => Core.Updates;
    public PowerService Power => Core.Power;
    public TrayIconService Tray { get; }

    public TrackerStatusDto? LastServiceStatus { get; private set; }

    private System.Threading.Timer? _updateTimer;
    private System.Threading.Timer? _trayTimer;

    public AppHost()
    {
        // Detect service before constructing host so we pick the right config root.
        var serviceReachable = TrackerIpcClient.IsServiceReachableAsync(TimeSpan.FromMilliseconds(800))
            .GetAwaiter().GetResult();

        if (serviceReachable || ConfigPaths.IsServiceInstalled())
            Core = new TrackingHost(AppConfigStore.ForMachine(), serviceMode: false);
        else
            Core = new TrackingHost(AppConfigStore.ForUser(), serviceMode: false);

        Tray = new TrayIconService(this);
    }

    public async Task StartAsync()
    {
        ServiceClient = await TrackerIpcClient.TryConnectAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (ServiceClient is not null)
        {
            try
            {
                await ServiceClient.NotifyCurrentUserSessionAsync().ConfigureAwait(false);
                var remote = await ServiceClient.GetConfigDtoAsync().ConfigureAwait(false);
                if (remote is not null)
                    Core.ReplaceConfig(remote);
                LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
                Log.Info("App", "Attached to WinTAKTracker Windows Service (no in-process tracker).");
            }
            catch (Exception ex)
            {
                Log.Warn("App", $"Service attach incomplete: {ex.Message}. Falling back to in-process mode.");
                await ServiceClient.DisposeAsync().ConfigureAwait(false);
                ServiceClient = null;
            }
        }

        if (ServiceClient is null)
        {
            // Portable / no-service: own the tracker in this process.
            StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
            await Core.StartAsync().ConfigureAwait(false);
            Core.StatusChanged += (_, _) => RefreshTray();
            Pause.PauseChanged += OnPauseChanged;
        }
        else
        {
            // Companion mode: tray autostart only; tracking owned by service.
            StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
            Pause.PauseChanged += OnPauseChangedService;
        }

        _trayTimer = new System.Threading.Timer(_ => RefreshTray(), null, 1000, 2000);
        _updateTimer = new System.Threading.Timer(async _ => await CheckUpdatesQuietAsync(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

        RefreshTray();
        Log.Info("App", AttachedToService
            ? "WinTAKTracker tray started (service companion)."
            : "WinTAKTracker started (in-process tracking).");
    }

    public void SaveConfig()
    {
        Core.SaveConfig();
        if (ServiceClient is not null)
        {
            try
            {
                ServiceClient.SetConfigAsync(Config).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn("IPC", $"SetConfig failed: {ex.Message}");
            }
        }
        else
        {
            StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
        }
    }

    public async Task ReloadConnectionsAsync()
    {
        SaveConfig();
        if (ServiceClient is not null)
        {
            await ServiceClient.ReloadConnectionsAsync().ConfigureAwait(false);
            LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
        }
        else
        {
            await Core.ReloadConnectionsAsync().ConfigureAwait(false);
        }

        RefreshTray();
    }

    public bool CurrentUserNeedsCallsignSetup() =>
        IdentityResolver.CurrentUserNeedsSetup(Config);

    public void SaveCurrentUserIdentity(string callsign, string team, string role, string cotType)
    {
        var sid = IdentityResolver.CurrentUserSid() ?? throw new InvalidOperationException("No Windows user SID.");
        var userName = IdentityResolver.CurrentUserName() ?? Environment.UserName;
        Core.SetUserIdentity(sid, userName, callsign, team, role, cotType);
        Core.SetActiveSession(sid, userName);

        if (ServiceClient is not null)
        {
            ServiceClient.SetUserIdentityAsync(new IdentityUpdateDto
            {
                UserSid = sid,
                UserName = userName,
                Callsign = callsign,
                Team = team,
                Role = role,
                CotType = cotType,
            }).GetAwaiter().GetResult();
            ServiceClient.NotifyCurrentUserSessionAsync().GetAwaiter().GetResult();
        }
        else
        {
            Reporting.NotifyIdentityChanged();
        }

        RefreshTray();
    }

    public void SaveComputerIdentity(string callsign, string team, string role, string cotType)
    {
        Core.SetComputerIdentity(callsign, team, role, cotType);
        if (ServiceClient is not null)
        {
            ServiceClient.SetComputerIdentityAsync(new IdentityUpdateDto
            {
                Callsign = callsign,
                Team = team,
                Role = role,
                CotType = cotType,
            }).GetAwaiter().GetResult();
        }
        else
        {
            Reporting.NotifyIdentityChanged();
        }

        RefreshTray();
    }

    public void DismissCurrentUserSetupPrompt()
    {
        var sid = IdentityResolver.CurrentUserSid();
        if (sid is null) return;
        var userName = IdentityResolver.CurrentUserName();
        Core.DismissUserSetupPrompt(sid, userName);
        if (ServiceClient is not null)
        {
            ServiceClient.DismissUserSetupPromptAsync(new IdentityUpdateDto
            {
                UserSid = sid,
                UserName = userName,
            }).GetAwaiter().GetResult();
        }
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        Power.SetPreventSleep(!paused && Config.Startup.PreventSleepWhileTracking);
        RefreshTray();
        Tray.ShowBalloon(paused ? "Tracking paused" : "Tracking resumed",
            paused ? "Outbound CoT muted (servers + mesh)." : "PLI reporting active.");
    }

    private void OnPauseChangedService(object? sender, bool paused)
    {
        try
        {
            if (ServiceClient is null) return;
            if (paused) ServiceClient.PauseAsync().GetAwaiter().GetResult();
            else ServiceClient.ResumeAsync().GetAwaiter().GetResult();
            LastServiceStatus = ServiceClient.GetStatusDtoAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"Pause/resume failed: {ex.Message}");
        }

        RefreshTray();
        Tray.ShowBalloon(paused ? "Tracking paused" : "Tracking resumed",
            paused ? "Service outbound CoT muted." : "Service PLI reporting active.");
    }

    public void RefreshTray()
    {
        if (ServiceClient is not null)
        {
            try
            {
                LastServiceStatus = ServiceClient.GetStatusDtoAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // keep last
            }

            var state = Enum.TryParse<TrayIconState>(LastServiceStatus?.TrayState, out var parsed)
                ? parsed
                : TrayIconState.Disconnected;
            Tray.SetState(state);
            return;
        }

        Tray.SetState(Core.ComputeTrayState());
    }

    private async Task CheckUpdatesQuietAsync()
    {
        // Service-aware updater (stop → replace → start) is Phase 3; tray-only update in portable mode.
        if (AttachedToService) return;

        try
        {
            var result = await Updates.CheckAsync().ConfigureAwait(false);
            if (!result.Success || !result.UpdateAvailable) return;

            if (Config.Updates.AutomaticallyDownloadAndInstall)
            {
                var (ok, msg) = await Updates.DownloadAndApplyAsync(result).ConfigureAwait(false);
                if (ok)
                {
                    Tray.ShowBalloon("Updating", msg);
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        System.Windows.Application.Current.Shutdown());
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

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _trayTimer?.Dispose();
        Pause.PauseChanged -= OnPauseChanged;
        Pause.PauseChanged -= OnPauseChangedService;
        if (ServiceClient is not null)
            ServiceClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Tray.Dispose();
        Core.Dispose();
    }
}
