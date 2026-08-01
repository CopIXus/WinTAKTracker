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
    public ReportingEngine Reporting { get; }
    public StatusExporter StatusExporter { get; }
    public IUpdateService Updates { get; }
    public PowerService Power { get; }
    public bool ServiceMode { get; }

    private string? _activeUserSid;
    private string? _activeUserName;

    public event EventHandler? StatusChanged;

    public TrackingHost(AppConfigStore? store = null, bool serviceMode = false)
    {
        ServiceMode = serviceMode;
        ConfigStore = store ?? (serviceMode ? AppConfigStore.ForMachine() : AppConfigStore.ForUser());
        ConfigStore.EnsureDirectories();
        Config = ConfigStore.Load();
        SettingsLock = new SettingsLockService(ConfigStore);
        EnsureDeviceUid();
        Config.EnsureIdentityDefaults();
        ConfigStore.Save(Config);

        Log = new RedactedLogger(ConfigStore.LogsDirectory);
        ApplyLogSettings();

        Pause = new PauseService();
        Gps = new GpsService(Log);
        Mesh = new MeshSaBroadcaster(Log);
        SoftCert = new SoftCertImporter(ConfigStore, Log);
        Enrollment = new EnrollmentService(ConfigStore, SoftCert, Log);
        Tak = new TakConnectionManager(ConfigStore, Log);
        Reporting = new ReportingEngine(Gps, Tak, Mesh, Pause, ConfigStore, Log, GetActiveIdentity);
        StatusExporter = new StatusExporter();
        Updates = new UpdateService(ConfigStore, Log, () => Config.Updates);
        Power = new PowerService();

        if (!serviceMode)
            _activeUserSid = IdentityResolver.CurrentUserSid();
    }

    public ActiveIdentity GetActiveIdentity() =>
        IdentityResolver.Resolve(Config, _activeUserSid);

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

    public void ReplaceConfig(AppConfig config)
    {
        Config = config;
        Config.EnsureIdentityDefaults();
        SaveConfig();
        Reporting.NotifyIdentityChanged();
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

    public void SetActiveSession(string? userSid, string? userName)
    {
        _activeUserSid = userSid;
        _activeUserName = userName;
        Reporting.NotifyIdentityChanged();
        Log.Info("Identity", string.IsNullOrWhiteSpace(userSid)
            ? "Active identity → computer callsign (logged off / no session)."
            : $"Active session user SID set; identity source={GetActiveIdentity().Source}.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetComputerIdentity(string callsign, string team, string role, string cotType)
    {
        Config.ComputerIdentity.Callsign = string.IsNullOrWhiteSpace(callsign)
            ? Environment.MachineName
            : callsign.Trim();
        Config.ComputerIdentity.Team = string.IsNullOrWhiteSpace(team) ? "Cyan" : team.Trim();
        Config.ComputerIdentity.Role = string.IsNullOrWhiteSpace(role) ? "Team Member" : role.Trim();
        Config.ComputerIdentity.CotType = string.IsNullOrWhiteSpace(cotType)
            ? CotEventBuilder.GroundUnitType
            : cotType.Trim();
        SaveConfig();
        Reporting.NotifyIdentityChanged();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetUserIdentity(
        string userSid,
        string userName,
        string callsign,
        string team,
        string role,
        string cotType)
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
        user.SetupPromptDismissed = false;
        SaveConfig();

        if (string.Equals(_activeUserSid, userSid, StringComparison.OrdinalIgnoreCase) ||
            _activeUserSid is null && !ServiceMode)
        {
            _activeUserSid ??= userSid;
            Reporting.NotifyIdentityChanged();
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
