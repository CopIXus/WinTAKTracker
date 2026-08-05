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
using WinTAKTracker.Services.Video;

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
    public VideoService Video { get; }

    public TrackerStatusDto? LastServiceStatus { get; private set; }

    /// <summary>Most recent update check result (tray quiet check or Settings → Updates).</summary>
    public UpdateCheckResult? LastUpdateCheck { get; private set; }

    /// <summary>Tray-side WinRT location when attached to the Windows Service (null in portable mode).</summary>
    public CompanionLocationBridge? CompanionLocation { get; private set; }

    private System.Threading.Timer? _updateTimer;
    private System.Threading.Timer? _trayTimer;
    private bool _migrationChangedMachineStore;

    public AppHost()
    {
        // Detect service before constructing host so we pick the right config root.
        var serviceReachable = TrackerIpcClient.IsServiceReachableAsync(TimeSpan.FromMilliseconds(800))
            .GetAwaiter().GetResult();

        if (serviceReachable || ConfigPaths.IsServiceInstalled())
            Core = new TrackingHost(AppConfigStore.ForMachine(), serviceMode: false);
        else
            Core = new TrackingHost(AppConfigStore.ForUser(), serviceMode: false);

        // Video before Tray: tray ctor builds tooltip/icon and reads Video.LiveCount.
        Video = new VideoService(this);
        Config.Video ??= new VideoSettings();
        Config.Video.Feeds ??= [];
        if (Config.Video.Feeds.Count == 0)
            Config.Video.Feeds.Add(new VideoFeedSettings());

        Tray = new TrayIconService(this);
        Video.StateChanged += (_, _) => Tray.RefreshTooltip();
    }

    public async Task StartAsync()
    {
        // Tray runs as the interactive user — finish Setup's config/certs-only copy by re-protecting
        // CurrentUser DPAPI secrets into ProgramData (LocalMachine) so the service can connect.
        TryCompleteMachineMigrationFromUser();

        // Service mode: best-effort start so tray can attach; HKCU Run still launches this tray.
        TryEnsureServiceRunning();

        var serviceInstalled = ConfigPaths.IsServiceInstalled();
        ServiceClient = await TryAttachWithRetryAsync(serviceInstalled).ConfigureAwait(false);

        if (ServiceClient is not null)
        {
            try
            {
                await AttachToServiceAsync().ConfigureAwait(false);
                Log.Info("App", "Attached to WinTAKTracker Windows Service (no in-process tracker).");
            }
            catch (Exception ex)
            {
                Log.Warn("App", $"Service attach incomplete: {ex.Message}.");
                await ServiceClient.DisposeAsync().ConfigureAwait(false);
                ServiceClient = null;

                if (serviceInstalled)
                {
                    Log.Error("App",
                        "Service unreachable after attach failure — not starting in-process tracker (companion-only).");
                }
            }
        }
        else if (serviceInstalled)
        {
            Log.Error("App",
                "Service unreachable — not starting in-process tracker (companion-only: tray UI + timers).");
        }

        // Always honor Start with Windows for the tray (portable tracking UI, or service companion + GPS bridge).
        StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);

        // Bind this interactive session so CoT uses per-user identity when set.
        var sid = IdentityResolver.CurrentUserSid();
        var userName = IdentityResolver.CurrentUserName();
        if (!string.IsNullOrWhiteSpace(sid))
            Core.SetActiveSession(sid, userName);

        if (ServiceClient is null && !serviceInstalled)
        {
            // Portable / no-service: own the tracker in this process.
            await Core.StartAsync().ConfigureAwait(false);
            Core.StatusChanged += (_, _) => _ = RefreshTrayAsync();
            Pause.PauseChanged += OnPauseChanged;
        }
        else if (ServiceClient is not null)
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
        else
        {
            // Service installed but unreachable: companion-only UI — never Core.StartAsync().
            Pause.PauseChanged += (_, _) => RefreshTray();
        }

        _trayTimer = new System.Threading.Timer(_ => _ = RefreshTrayAsync(), null, 1000, 2000);
        _updateTimer = new System.Threading.Timer(async _ => await CheckUpdatesQuietAsync(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

        await RefreshTrayAsync().ConfigureAwait(false);
        Log.Info("App", AttachedToService
            ? "WinTAKTracker tray started (service companion)."
            : serviceInstalled
                ? "WinTAKTracker tray started (companion-only; service unreachable)."
                : "WinTAKTracker started (in-process tracking).");
    }

    private async Task<TrackerIpcClient?> TryAttachWithRetryAsync(bool serviceInstalled)
    {
        var attempts = serviceInstalled ? 5 : 1;
        for (var i = 0; i < attempts; i++)
        {
            var client = await TrackerIpcClient.TryConnectAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (client is not null)
                return client;

            if (i + 1 < attempts)
            {
                Log.Info("App", $"Service attach attempt {i + 1}/{attempts} failed; retrying…");
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                TryEnsureServiceRunning();
            }
        }

        return null;
    }

    /// <summary>
    /// Prefer service-authoritative config. Only SetConfig when local migration actually changed something.
    /// At most one ReloadConnections.
    /// </summary>
    private async Task AttachToServiceAsync()
    {
        if (ServiceClient is null) return;

        await ServiceClient.NotifyCurrentUserSessionAsync().ConfigureAwait(false);
        var remote = await ServiceClient.GetConfigDtoAsync().ConfigureAwait(false);
        if (remote is not null)
            Core.ReplaceConfig(remote, save: false);

        LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);

        if (_migrationChangedMachineStore)
        {
            // Push migrated secrets/certs-related config once, then a single reload.
            var response = await ServiceClient.SetConfigAsync(Config).ConfigureAwait(false);
            if (!response.Ok)
                Log.Warn("IPC", $"SetConfig after migration: {response.Error}");
            else
                await ServiceClient.ReloadConnectionsAsync().ConfigureAwait(false);
        }

        LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Best-effort re-pull of the service-authoritative config. Portal Pref pushes and device
    /// profile syncs mutate the service copy; pushing a stale tray copy via SetConfig would
    /// clobber the remotely applied identity. Call before opening Settings.
    /// </summary>
    public async Task RefreshConfigFromServiceAsync()
    {
        if (ServiceClient is null) return;
        try
        {
            var remote = await ServiceClient.GetConfigDtoAsync().ConfigureAwait(false);
            if (remote is not null)
                Core.ReplaceConfig(remote, save: false);
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"Config refresh failed: {ex.Message}");
        }
    }

    public void SaveConfig() => _ = SaveConfigAsync();

    public async Task SaveConfigAsync()
    {
        try
        {
            Core.SaveConfig();
            // Tray Run-key applies in both portable and service-companion modes.
            StartupRegistration.SetEnabled(Config.Startup.StartWithWindows);
            if (ServiceClient is not null)
            {
                var response = await ServiceClient.SetConfigAsync(Config).ConfigureAwait(false);
                if (!response.Ok)
                    Log.Warn("IPC", $"SetConfig failed: {response.Error}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"SaveConfig failed: {ex.Message}");
        }
    }

    public async Task UnlockServiceSettingsAsync(string password)
    {
        if (ServiceClient is null) return;
        try
        {
            var response = await ServiceClient.UnlockSettingsAsync(password).ConfigureAwait(false);
            if (!response.Ok)
                Log.Warn("IPC", $"UnlockSettings failed: {response.Error}");
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"UnlockSettings failed: {ex.Message}");
        }
    }

    public async Task LockServiceSettingsAsync()
    {
        if (ServiceClient is null) return;
        try
        {
            await ServiceClient.LockSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"LockSettings failed: {ex.Message}");
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
        await SaveConfigAsync().ConfigureAwait(false);
        if (ServiceClient is not null)
        {
            await ServiceClient.ReloadConnectionsAsync().ConfigureAwait(false);
            LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
        }
        else if (!ConfigPaths.IsServiceInstalled())
        {
            await Core.ReloadConnectionsAsync().ConfigureAwait(false);
        }

        await RefreshTrayAsync().ConfigureAwait(false);
    }

    public bool CurrentUserNeedsCallsignSetup() =>
        IdentityResolver.CurrentUserNeedsSetup(Config);

    public void SaveCurrentUserIdentity(string callsign, string team, string role, string cotType, string? phone = null) =>
        _ = SaveCurrentUserIdentityAsync(callsign, team, role, cotType, phone);

    public async Task SaveCurrentUserIdentityAsync(
        string callsign, string team, string role, string cotType, string? phone = null)
    {
        var sid = IdentityResolver.CurrentUserSid() ?? throw new InvalidOperationException("No Windows user SID.");
        var userName = IdentityResolver.CurrentUserName() ?? Environment.UserName;
        Core.SetUserIdentity(sid, userName, callsign, team, role, cotType, phone);
        Core.SetActiveSession(sid, userName);

        if (ServiceClient is not null)
        {
            try
            {
                await ServiceClient.SetUserIdentityAsync(new IdentityUpdateDto
                {
                    UserSid = sid,
                    UserName = userName,
                    Callsign = callsign,
                    Team = team,
                    Role = role,
                    CotType = cotType,
                    Phone = phone,
                }).ConfigureAwait(false);
                await ServiceClient.NotifyCurrentUserSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("IPC", $"SetUserIdentity failed: {ex.Message}");
            }
        }
        else
        {
            Reporting.NotifyIdentityChanged();
        }

        await RefreshTrayAsync().ConfigureAwait(false);
    }

    public void SaveComputerIdentity(string callsign, string team, string role, string cotType, string? phone = null) =>
        _ = SaveComputerIdentityAsync(callsign, team, role, cotType, phone);

    public async Task SaveComputerIdentityAsync(
        string callsign, string team, string role, string cotType, string? phone = null)
    {
        Core.SetComputerIdentity(callsign, team, role, cotType, phone);
        if (ServiceClient is not null)
        {
            try
            {
                await ServiceClient.SetComputerIdentityAsync(new IdentityUpdateDto
                {
                    Callsign = callsign,
                    Team = team,
                    Role = role,
                    CotType = cotType,
                    Phone = phone,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("IPC", $"SetComputerIdentity failed: {ex.Message}");
            }
        }
        else
        {
            Reporting.NotifyIdentityChanged();
        }

        await RefreshTrayAsync().ConfigureAwait(false);
    }

    public void DismissCurrentUserSetupPrompt()
    {
        var sid = IdentityResolver.CurrentUserSid();
        if (sid is null) return;
        var userName = IdentityResolver.CurrentUserName();
        Core.DismissUserSetupPrompt(sid, userName);
        if (ServiceClient is not null)
        {
            _ = ServiceClient.DismissUserSetupPromptAsync(new IdentityUpdateDto
            {
                UserSid = sid,
                UserName = userName,
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Warn("IPC", $"DismissUserSetupPrompt failed: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
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
                _migrationChangedMachineStore = true;
                Log.Info("App", "Migrated portable servers into ProgramData machine store.");
                return;
            }

            if (AppConfigStore.CompleteUserToMachineMigration(user, ConfigStore))
            {
                _migrationChangedMachineStore = true;
                Log.Info("App", "Re-protected portable secrets/certs into ProgramData for the Windows Service.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"User→machine migration assist failed: {ex.Message}");
        }
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        Power.SetPreventSleep(!paused && Config.Startup.PreventSleepWhileTracking);
        _ = RefreshTrayAsync();
        Tray.ShowBalloon(paused ? "Tracking paused" : "Tracking resumed",
            paused ? "Outbound CoT muted (servers + mesh)." : "PLI reporting active.");
    }

    private async void OnPauseChangedService(object? sender, bool paused)
    {
        try
        {
            if (ServiceClient is null) return;
            if (paused) await ServiceClient.PauseAsync().ConfigureAwait(false);
            else await ServiceClient.ResumeAsync().ConfigureAwait(false);
            LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("IPC", $"Pause/resume failed: {ex.Message}");
        }

        await RefreshTrayAsync().ConfigureAwait(false);
        Tray.ShowBalloon(paused ? "Tracking paused" : "Tracking resumed",
            paused ? "Service outbound CoT muted." : "Service PLI reporting active.");
    }

    public void RefreshTray() => _ = RefreshTrayAsync();

    public async Task RefreshTrayAsync()
    {
        if (ServiceClient is not null)
        {
            try
            {
                LastServiceStatus = await ServiceClient.GetStatusDtoAsync().ConfigureAwait(false);
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

        if (ConfigPaths.IsServiceInstalled() && ServiceClient is null)
        {
            Tray.SetState(TrayIconState.Disconnected);
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
                    // Do not claim install success — Setup/UAC may still be pending.
                    Tray.ShowBalloon("Update started", msg);
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
        Video.Dispose();
        Tray.Dispose();
        Core.Dispose();
    }
}
