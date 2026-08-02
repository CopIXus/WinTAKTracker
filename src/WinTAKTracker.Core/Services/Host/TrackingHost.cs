using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Ipc;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Startup;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;
using WinTAKTracker.Services.Update;

namespace WinTAKTracker.Services.Host;

/// <summary>UI-free composition root for GPS + TAK + Mesh + reporting.</summary>
public sealed class TrackingHost : IDisposable
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
    public DeviceProfileSync ProfileSync { get; }
    public ReportingEngine Reporting { get; }
    public StatusExporter StatusExporter { get; }
    public IUpdateService Updates { get; }
    public PowerService Power { get; }
    public bool ServiceMode { get; }

    /// <summary>True when config.json was corrupt at load — automatic Saves on ctor were skipped.</summary>
    public bool LoadHadError { get; private set; }

    /// <summary>SID of the interactive companion currently owning GPS pushes / session identity.</summary>
    public string? ActiveCompanionSid { get; private set; }

    private string? _activeUserSid;
    private string? _activeUserName;

    public event EventHandler? StatusChanged;

    public TrackingHost(AppConfigStore? store = null, bool serviceMode = false)
    {
        ServiceMode = serviceMode;
        ConfigStore = store ?? (serviceMode ? AppConfigStore.ForMachine() : AppConfigStore.ForUser());
        ConfigStore.EnsureDirectories();
        var load = ConfigStore.LoadDetailed();
        Config = load.Config;
        LoadHadError = load.LoadHadError;
        SettingsLock = new SettingsLockService(ConfigStore);
        EnsureDeviceUid(allowSave: !LoadHadError);
        Config.EnsureIdentityDefaults();

        // First-run (missing file) may Save fresh. Never overwrite a corrupt path on ctor.
        if (load.CreatedFresh)
            ConfigStore.Save(Config);
        else if (!LoadHadError)
            ConfigStore.Save(Config);

        Log = new RedactedLogger(ConfigStore.LogsDirectory);
        ApplyLogSettings();

        if (LoadHadError)
        {
            Log.Error("Config",
                load.CorruptBackupPath is not null
                    ? $"config.json was corrupt — quarantined to {Path.GetFileName(load.CorruptBackupPath)}; using in-memory defaults (not overwriting)."
                    : "config.json was corrupt; using in-memory defaults (not overwriting).");
        }

        Pause = new PauseService();
        Gps = new GpsService(Log, serviceMode);
        Mesh = new MeshSaBroadcaster(Log);
        SoftCert = new SoftCertImporter(ConfigStore, Log);
        Enrollment = new EnrollmentService(ConfigStore, SoftCert, Log);
        ProfileSync = new DeviceProfileSync(ConfigStore, Log);
        Tak = new TakConnectionManager(ConfigStore, Log);
        Reporting = new ReportingEngine(Gps, Tak, Mesh, Pause, ConfigStore, Log, GetActiveIdentity);
        StatusExporter = new StatusExporter();
        Updates = new UpdateService(ConfigStore, Log, () => Config.Updates);
        Power = new PowerService();

        ProfileSync.IdentityApplied += (_, _) =>
        {
            // Config already mutated + saved by ProfileSync.
            Reporting.NotifyIdentityChanged();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        };
        Tak.ServerConnected += (_, profile) =>
        {
            // Delay briefly so tray SetActiveSession can win the race — otherwise the first PLI
            // uses computer callsign (machine name) and Portal/TAK Server keep that label.
            _ = AnnouncePresenceAfterConnectAsync(profile);
        };

        if (!serviceMode)
            _activeUserSid = IdentityResolver.CurrentUserSid();
    }

    public ActiveIdentity GetActiveIdentity() =>
        IdentityResolver.Resolve(Config, _activeUserSid);

    public void SetVideoAnnounce(Reporting.VideoAnnounceState? state) =>
        Reporting.SetVideoAnnounce(state);

    public TrackerStatusDto GetStatus()
    {
        var active = GetActiveIdentity();
        var hasFix = Gps.CurrentFix is not null;
        var tray = ComputeTrayState();
        var servers = Tak.GetStatuses().Select(s => new ServerStatusDto
        {
            ProfileId = s.ProfileId,
            DisplayName = s.DisplayName,
            Enabled = s.Enabled,
            Protocol = s.Protocol,
            State = s.State.ToString(),
            LastErrorCode = s.LastErrorCode,
            LastSendUtc = s.LastSendUtc,
        }).ToList();
        var fix = Gps.CurrentFix;
        return new TrackerStatusDto
        {
            ServiceMode = ServiceMode,
            Paused = Pause.IsPaused,
            HasGpsFix = hasFix,
            GpsSource = fix?.Source.ToString(),
            GpsSourceDisplay = fix?.SourceDisplayName,
            GpsIsHeld = fix?.IsHeld == true,
            Latitude = fix?.Latitude,
            Longitude = fix?.Longitude,
            AltitudeMeters = fix?.AltitudeMeters,
            SpeedMetersPerSecond = fix?.SpeedMetersPerSecond,
            CourseDegrees = fix?.CourseDegrees,
            AccuracyMeters = fix?.AccuracyMeters,
            GpsTimestampUtc = fix?.Timestamp,
            AnyTakConnected = Tak.AnyConnected,
            AnyTakReconnecting = Tak.AnyReconnecting,
            MeshReady = Config.MeshSa.Enabled && Mesh.IsReady && Mesh.LastErrorCode is null,
            MeshLastError = Mesh.LastErrorCode,
            LastPliSentUtc = Reporting.LastPliSentUtc,
            TrayState = tray.ToString(),
            ActiveIdentity = ActiveIdentityDto.From(active),
            ConfigRoot = ConfigStore.RootDirectory,
            Servers = servers,
        };
    }

    public TrayIconState ComputeTrayState()
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

    public async Task StartAsync()
    {
        Power.SetPreventSleep(Config.Startup.PreventSleepWhileTracking && !Pause.IsPaused);

        Pause.PauseChanged += (_, _) =>
        {
            Power.SetPreventSleep(!Pause.IsPaused && Config.Startup.PreventSleepWhileTracking);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        };
        Tak.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        Gps.FixChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        Reporting.Reported += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);

        await Gps.StartAsync(Config.Gps).ConfigureAwait(false);
        Mesh.ApplySettings(Config.MeshSa);
        await Tak.StartAsync(Config).ConfigureAwait(false);
        Reporting.Start(Config);

        Log.Info("Host", ServiceMode
            ? "WinTAKTracker service host started (machine store)."
            : "WinTAKTracker in-process host started (user store).");
    }

    public void SaveConfig()
    {
        Config.EnsureIdentityDefaults();
        ConfigStore.Save(Config);
        LoadHadError = false;
        ApplyLogSettings();
        Reporting.ApplyConfig(Config);
        Power.SetPreventSleep(Config.Startup.PreventSleepWhileTracking && !Pause.IsPaused);
    }

    public void ApplyLogSettings()
    {
        var levelName = Config.Diagnostics.LogLevel ?? "Error";
        if (Enum.TryParse<LogLevel>(levelName, true, out var level))
            Log.SetMinLevel(level);
        else
            Log.SetMinLevel(LogLevel.Error);

        var maxMb = Config.Diagnostics.MaxLogSizeMb;
        if (maxMb < 1) maxMb = 30;
        Log.SetMaxTotalSizeMb(maxMb);
        Log.EnforceSizeLimit();
    }

    public void ReplaceConfig(AppConfig config, bool save = true)
    {
        var previousCallsign = GetActiveIdentity().Callsign;
        Config = config;
        Config.EnsureIdentityDefaults();
        if (save)
            SaveConfig();
        else
        {
            ApplyLogSettings();
            Reporting.ApplyConfig(Config);
        }

        Reporting.NotifyIdentityChanged();
        var nextCallsign = GetActiveIdentity().Callsign;
        if (!string.Equals(previousCallsign, nextCallsign, StringComparison.Ordinal))
            _ = Reporting.AnnouncePresenceAsync();
    }

    public async Task ReloadConnectionsAsync()
    {
        SaveConfig();
        await Gps.ApplySettingsAsync(Config.Gps).ConfigureAwait(false);
        Mesh.ApplySettings(Config.MeshSa);
        await Tak.ReloadAsync(Config).ConfigureAwait(false);
        Reporting.NotifyIdentityChanged();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Bind or clear the active interactive companion. When cleared, companion GPS is dropped.
    /// CoT identity after clear follows <see cref="AppConfig.RevertToComputerCallsignOnLogoff"/>
    /// (default: sticky last user callsign).
    /// </summary>
    public void SetActiveSession(string? userSid, string? userName)
    {
        var previous = _activeUserSid;
        var previousCallsign = GetActiveIdentity().Callsign;
        _activeUserSid = userSid;
        _activeUserName = userName;
        ActiveCompanionSid = string.IsNullOrWhiteSpace(userSid) ? null : userSid;

        if (string.IsNullOrWhiteSpace(userSid) && !string.IsNullOrWhiteSpace(previous))
            Gps.ClearExternalFix();

        RememberInteractiveUserIfNeeded(userSid, userName);

        var next = GetActiveIdentity();
        Reporting.NotifyIdentityChanged();
        // Re-bind TAK Server / Portal connection label when user callsign replaces machine name.
        if (!string.Equals(previousCallsign, next.Callsign, StringComparison.Ordinal))
            _ = Reporting.AnnouncePresenceAsync();

        if (string.IsNullOrWhiteSpace(userSid))
        {
            Log.Info("Identity", Config.RevertToComputerCallsignOnLogoff
                ? $"Active identity → computer callsign (logged off). callsign={next.Callsign}"
                : $"Interactive session cleared; sticky identity source={next.Source} callsign={next.Callsign}.");
        }
        else
        {
            Log.Info("Identity",
                $"Active session user SID set; identity source={next.Source} callsign={next.Callsign}.");
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RememberInteractiveUserIfNeeded(string? userSid, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userSid) ||
            !Config.UserIdentities.TryGetValue(userSid, out var user) ||
            !user.HasCallsign)
            return;

        if (!string.IsNullOrWhiteSpace(userName))
            user.UserName = userName;

        if (string.Equals(Config.LastInteractiveUserSid, userSid, StringComparison.OrdinalIgnoreCase))
            return;

        Config.LastInteractiveUserSid = userSid;
        SaveConfig();
    }

    private async Task AnnouncePresenceAfterConnectAsync(ServerProfile profile)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
            await Reporting.AnnouncePresenceAsync().ConfigureAwait(false);
            await ProfileSync.TrySyncAsync(profile, Config, _activeUserSid, _activeUserName)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Identity", $"Post-connect announce/sync failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// True when <paramref name="callerSid"/> may push GPS / claim session
    /// (no active companion, or same SID).
    /// </summary>
    public bool IsCompanionSidAllowed(string? callerSid)
    {
        if (string.IsNullOrWhiteSpace(ActiveCompanionSid))
            return true;
        if (string.IsNullOrWhiteSpace(callerSid))
            return false;
        return string.Equals(ActiveCompanionSid, callerSid, StringComparison.OrdinalIgnoreCase);
    }

    public void SetComputerIdentity(string callsign, string team, string role, string cotType, string? phone = null)
    {
        Config.ComputerIdentity.Callsign = string.IsNullOrWhiteSpace(callsign)
            ? Environment.MachineName
            : callsign.Trim();
        Config.ComputerIdentity.Team = string.IsNullOrWhiteSpace(team) ? "Cyan" : team.Trim();
        Config.ComputerIdentity.Role = string.IsNullOrWhiteSpace(role) ? "Team Member" : role.Trim();
        Config.ComputerIdentity.CotType = string.IsNullOrWhiteSpace(cotType)
            ? CotEventBuilder.GroundUnitType
            : cotType.Trim();
        Config.ComputerIdentity.Phone = phone?.Trim() ?? "";
        SaveConfig();
        Reporting.NotifyIdentityChanged();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Apply Portal / remote identity prefs. Appends <c>.wtt</c> to callsign when missing.
    /// Honors <see cref="AppConfig.ApplyRemoteIdentityFromPortal"/>.
    /// </summary>
    public RemoteIdentityApply.Result ApplyRemoteIdentity(string? callsign, string? team, string? role = null)
    {
        if (!Config.ApplyRemoteIdentityFromPortal)
        {
            return new RemoteIdentityApply.Result
            {
                Applied = false,
                Message = "Remote identity apply disabled (ApplyRemoteIdentityFromPortal=false).",
            };
        }

        var result = RemoteIdentityApply.Apply(
            Config, callsign, team, role, _activeUserSid, _activeUserName);
        if (result.Applied)
        {
            SaveConfig();
            Reporting.NotifyIdentityChanged();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public void SetUserIdentity(
        string userSid,
        string userName,
        string callsign,
        string team,
        string role,
        string cotType,
        string? phone = null)
    {
        if (!Config.UserIdentities.TryGetValue(userSid, out var user))
        {
            user = new UserIdentitySettings();
            Config.UserIdentities[userSid] = user;
        }

        user.UserName = userName;
        user.Callsign = callsign.Trim();
        user.Team = team.Trim();
        user.Role = role.Trim();
        user.CotType = string.IsNullOrWhiteSpace(cotType) ? "" : cotType.Trim();
        user.Phone = phone?.Trim() ?? "";
        user.SetupPromptDismissed = false;
        if (user.HasCallsign)
            Config.LastInteractiveUserSid = userSid;
        SaveConfig();

        if (string.Equals(_activeUserSid, userSid, StringComparison.OrdinalIgnoreCase) ||
            _activeUserSid is null && !ServiceMode)
        {
            _activeUserSid ??= userSid;
            Reporting.NotifyIdentityChanged();
            _ = Reporting.AnnouncePresenceAsync();
        }
        else if (!Config.RevertToComputerCallsignOnLogoff &&
                 string.Equals(Config.LastInteractiveUserSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            Reporting.NotifyIdentityChanged();
            _ = Reporting.AnnouncePresenceAsync();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DismissUserSetupPrompt(string userSid, string? userName)
    {
        if (!Config.UserIdentities.TryGetValue(userSid, out var user))
        {
            user = new UserIdentitySettings { UserName = userName ?? "" };
            Config.UserIdentities[userSid] = user;
        }

        user.SetupPromptDismissed = true;
        if (!string.IsNullOrWhiteSpace(userName))
            user.UserName = userName;
        SaveConfig();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureDeviceUid(bool allowSave)
    {
        if (!string.IsNullOrWhiteSpace(Config.DeviceUid)) return;
        try
        {
            var guid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", null) as string;
            // Stable Windows UID (not ANDROID-*). TAK Server shows this after the first PLI.
            var suffix = string.IsNullOrWhiteSpace(guid)
                ? Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()
                : guid.Replace("-", "")[..Math.Min(16, guid.Replace("-", "").Length)].ToUpperInvariant();
            Config.DeviceUid = "WINDOWS-WinTAKTracker-" + suffix;
        }
        catch
        {
            Config.DeviceUid = "WINDOWS-WinTAKTracker-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        }

        if (allowSave)
            ConfigStore.Save(Config);
    }

    public void Dispose()
    {
        Reporting.Dispose();
        (Tak as IDisposable)?.Dispose();
        Mesh.Dispose();
        Gps.Stop();
        (Gps as IDisposable)?.Dispose();
        Power.Dispose();
        (Log as IDisposable)?.Dispose();
    }
}
