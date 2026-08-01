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

    /// <summary>Most recent update check result (tray quiet check or Settings → Updates).</summary>
    public UpdateCheckResult? LastUpdateCheck { get; private set; }

    /// <summary>Tray-side WinRT location when attached to the Windows Service (null in portable mode).</summary>
    public CompanionLocationBridge? CompanionLocation { get; private set; }

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
        // Tray runs as the interactive user — finish Setup's config/certs-only copy by re-protecting
        // CurrentUser DPAPI secrets into ProgramData (LocalMachine) so the service can connect.
        TryCompleteMachineMigrationFromUser();

        // Service mode: best-effort start so tray can attach; HKCU Run still launches this tray.
        TryEnsureServiceRunning();

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

                // Push any locally completed migration bits, then ask service to (re)connect enabled profiles.
                await ServiceClient.SetConfigAsync(Config).ConfigureAwait(false);
                await ServiceClient.ReloadConnectionsAsync().ConfigureAwait(false);
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

        // Always honor Start with Windows for the tray (portable tracking UI, or service companion + GPS bridge).
        StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);

        // Bind this interactive session so CoT uses per-user identity when set.
        var sid = IdentityResolver.CurrentUserSid();
        var userName = IdentityResolver.CurrentUserName();
        if (!string.IsNullOrWhiteSpace(sid))
            Core.SetActiveSession(sid, userName);

        if (ServiceClient is null)
        {
            // Portable / no-service: own the tracker in this process.
            await Core.StartAsync().ConfigureAwait(false);
            Core.StatusChanged += (_, _) => RefreshTray();
            Pause.PauseChanged += OnPauseChanged;
        }
        else
        {
            // Companion mode: tracking owned by service; tray feeds Windows Location over IPC.
            Pause.PauseChanged += OnPauseChangedService;
            CompanionLocation = new CompanionLocationBridge(Log);
            try
            {
                await CompanionLocation.StartAsync(ServiceClient).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("GPS/Companion", $"Failed to start tray location bridge: {ex.Message}");
            }
        }

        _trayTimer = new System.Threading.Timer(_ => RefreshTray(), null, 1000, 2000);
        _updateTimer = new System.Threading.Timer(async _ => await CheckUpdatesQuietAsync(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

        RefreshTray();
        Log.Info("App", AttachedToService
            ? "WinTAKTracker tray started (service companion)."
            : "WinTAKTracker started (in-process tracking).");
    }

    /// <summary>
    /// Live per-server stream status. When attached to the service, uses IPC status (not the idle local Tak manager).
    /// </summary>
    public IReadOnlyList<ServerConnectionStatus> GetServerStatuses()
    {
        if (AttachedToService)
        {
            var remote = LastServiceStatus?.Servers;
            if (remote is null)
                return Array.Empty<ServerConnectionStatus>();

            return remote.Select(s => new ServerConnectionStatus
            {
                ProfileId = s.ProfileId,
                DisplayName = s.DisplayName,
                Enabled = s.Enabled,
                Protocol = s.Protocol,
                State = Enum.TryParse<TakConnectionState>(s.State, true, out var st)
                    ? st
                    : TakConnectionState.Disconnected,
                LastErrorCode = s.LastErrorCode,
                LastSendUtc = s.LastSendUtc,
            }).ToList();
        }

        return Tak.GetStatuses();
    }

    public void SaveConfig()
    {
        Core.SaveConfig();
        // Tray Run-key applies in both portable and service-companion modes.
        StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
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
    }

    /// <summary>
    /// If the Windows Service is installed but stopped, try to start it (may require elevation).
    /// </summary>
    private void TryEnsureServiceRunning()
    {
        if (!ConfigPaths.IsServiceInstalled()) return;
        if (ConfigPaths.TryEnsureServiceRunning())
            Log.Info("App", "WinTAKTracker Windows Service is running.");
        else if (!string.Equals(
                     ConfigPaths.GetWindowsServiceStatusLabel(),
                     "Running",
                     StringComparison.OrdinalIgnoreCase))
            Log.Warn("App", "Could not start Windows Service (may need elevation or SCM access).");
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

    public void SaveCurrentUserIdentity(string callsign, string team, string role, string cotType, string? phone = null)
    {
        var sid = IdentityResolver.CurrentUserSid() ?? throw new InvalidOperationException("No Windows user SID.");
        var userName = IdentityResolver.CurrentUserName() ?? Environment.UserName;
        Core.SetUserIdentity(sid, userName, callsign, team, role, cotType, phone);
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
                Phone = phone,
            }).GetAwaiter().GetResult();
            ServiceClient.NotifyCurrentUserSessionAsync().GetAwaiter().GetResult();
        }
        else
        {
            Reporting.NotifyIdentityChanged();
        }

        RefreshTray();
    }

    public void SaveComputerIdentity(string callsign, string team, string role, string cotType, string? phone = null)
    {
        Core.SetComputerIdentity(callsign, team, role, cotType, phone);
        if (ServiceClient is not null)
        {
            ServiceClient.SetComputerIdentityAsync(new IdentityUpdateDto
            {
                Callsign = callsign,
                Team = team,
                Role = role,
                CotType = cotType,
                Phone = phone,
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

    private void TryCompleteMachineMigrationFromUser()
    {
        try
        {
            if (!string.Equals(
                    Path.GetFullPath(ConfigStore.RootDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(ConfigPaths.MachineRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;

            var user = AppConfigStore.ForUser();
            if (!File.Exists(Path.Combine(user.RootDirectory, "config.json")))
                return;

            // If machine config is empty/missing servers but user has them, take full migrate.
            if (Config.Servers.Count == 0 && user.Load().Servers.Count > 0)
            {
                AppConfigStore.MigrateUserStoreToMachine(user, ConfigStore);
                Core.ReplaceConfig(ConfigStore.Load());
                Log.Info("App", "Migrated portable servers into ProgramData machine store.");
                return;
            }

            if (AppConfigStore.CompleteUserToMachineMigration(user, ConfigStore))
                Log.Info("App", "Re-protected portable secrets/certs into ProgramData for the Windows Service.");
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"User→machine migration assist failed: {ex.Message}");
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

    /// <summary>
    /// Record an update check (from Settings or quiet tray check), persist availability hint, refresh tray tip.
    /// </summary>
    public void NoteUpdateCheck(UpdateCheckResult result, bool persist = true)
    {
        LastUpdateCheck = result;
        Config.Updates.LastCheckedUtc = DateTimeOffset.UtcNow.ToString("O");
        if (result.Success)
        {
            Config.Updates.LastAvailableVersion = result.UpdateAvailable &&
                                                 !string.IsNullOrWhiteSpace(result.LatestVersion)
                ? result.LatestVersion
                : null;
        }

        if (persist)
            SaveConfig();

        Tray.RefreshTooltip();
    }

    private async Task CheckUpdatesQuietAsync()
    {
        try
        {
            var result = await Updates.CheckAsync().ConfigureAwait(false);
            NoteUpdateCheck(result, persist: result.Success);

            if (!result.Success || !result.UpdateAvailable) return;

            if (Config.Updates.AutomaticallyDownloadAndInstall)
            {
                // Setup installs: downloads WinTAKTracker-Setup.exe and launches elevated (UAC).
                // Portable: schedules EXE replace helper. Only Shutdown after apply is armed.
                var (ok, msg) = await Updates.DownloadAndApplyAsync(result).ConfigureAwait(false);
                if (ok)
                {
                    Tray.ShowBalloon("Updating", msg);
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        System.Windows.Application.Current.Shutdown());
                }
                else
                {
                    Log.Warn("Update", $"Automatic update failed: {msg}");
                    Tray.ShowBalloon("Update failed", msg);
                }
            }
            else
            {
                var package = result.AssetKind == UpdateAssetKind.SetupInstaller
                    ? " (Setup — UAC may prompt)"
                    : "";
                Tray.ShowBalloon("Update available",
                    $"Version {result.LatestVersion} is available{package}. Open Settings → Updates.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update", $"Quiet update check failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Request Windows Location from the interactive process (tray Geolocator, or in-process GPS).
    /// </summary>
    public Task<GpsPermissionState> RequestWindowsLocationAccessAsync()
    {
        if (CompanionLocation is not null)
            return CompanionLocation.RequestAccessAsync();
        return Gps.RequestWindowsLocationAccessAsync();
    }

    public GpsPermissionState WindowsLocationPermission =>
        CompanionLocation?.PermissionState ?? Gps.WindowsPermission;

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _trayTimer?.Dispose();
        Pause.PauseChanged -= OnPauseChanged;
        Pause.PauseChanged -= OnPauseChangedService;
        if (CompanionLocation is not null)
        {
            try { CompanionLocation.StopAsync().GetAwaiter().GetResult(); }
            catch { /* ignore */ }
            CompanionLocation.Dispose();
            CompanionLocation = null;
        }
        if (ServiceClient is not null)
            ServiceClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Tray.Dispose();
        Core.Dispose();
    }
}
