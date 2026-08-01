namespace WinTAKTracker.Services.Config;

/// <summary>
/// Application configuration. Portable mode uses %LocalAppData%; service mode uses %ProgramData%.
/// Secrets are stored separately via DPAPI — never put real hosts/tokens in repo samples.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Legacy single identity (v1). Mirrored from <see cref="ComputerIdentity"/> on save/load
    /// for older UI paths; prefer ComputerIdentity / UserIdentities for new code.
    /// </summary>
    public IdentitySettings Identity { get; set; } = new();

    /// <summary>Machine-level CoT identity used when nobody is logged on / no user identity active.</summary>
    public IdentitySettings ComputerIdentity { get; set; } = new();

    /// <summary>Per-Windows-user identities keyed by SID string.</summary>
    public Dictionary<string, UserIdentitySettings> UserIdentities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public GpsSettings Gps { get; set; } = new();

    public ReportingSettings Reporting { get; set; } = new();

    public MeshSaSettings MeshSa { get; set; } = new();

    public StartupSettings Startup { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Server profiles (hosts/ports only in cleartext; credentials DPAPI-protected).</summary>
    public List<ServerProfile> Servers { get; set; } = [];

    /// <summary>
    /// Legacy CloudTAK URL (no longer shown in Settings). Kept for JSON round-trip of older configs.
    /// </summary>
    public string? CloudTakUrl { get; set; }

    /// <summary>Stable CoT UID for this machine (generated once).</summary>
    public string? DeviceUid { get; set; }

    /// <summary>
    /// When true (default), apply callsign/team/role from Portal / device-profile sync.
    /// Disable under Identity settings to keep local callsigns authoritative.
    /// </summary>
    public bool ApplyRemoteIdentityFromPortal { get; set; } = true;

    /// <summary>
    /// Migrate v1 Identity → ComputerIdentity and ensure computer callsign default.
    /// </summary>
    public void EnsureIdentityDefaults()
    {
        if (Identity is not null)
        {
            var legacy = Identity.Callsign?.Trim() ?? "";
            var computerEmpty = string.IsNullOrWhiteSpace(ComputerIdentity.Callsign);
            if (computerEmpty && legacy.Length > 0 &&
                !legacy.Equals("WIN-TRACKER", StringComparison.OrdinalIgnoreCase))
            {
                ComputerIdentity.Callsign = Identity.Callsign ?? "";
                if (!string.IsNullOrWhiteSpace(Identity.Team))
                    ComputerIdentity.Team = Identity.Team;
                if (!string.IsNullOrWhiteSpace(Identity.Role))
                    ComputerIdentity.Role = Identity.Role;
                if (!string.IsNullOrWhiteSpace(Identity.CotType))
                    ComputerIdentity.CotType = Identity.CotType;
                if (string.IsNullOrWhiteSpace(ComputerIdentity.Phone) &&
                    !string.IsNullOrWhiteSpace(Identity.Phone))
                    ComputerIdentity.Phone = Identity.Phone;
            }
            else if (computerEmpty)
            {
                // Copy team/role/cot even when callsign was empty/legacy default.
                if (!string.IsNullOrWhiteSpace(Identity.Team))
                    ComputerIdentity.Team = Identity.Team;
                if (!string.IsNullOrWhiteSpace(Identity.Role))
                    ComputerIdentity.Role = Identity.Role;
                if (!string.IsNullOrWhiteSpace(Identity.CotType))
                    ComputerIdentity.CotType = Identity.CotType;
                if (string.IsNullOrWhiteSpace(ComputerIdentity.Phone) &&
                    !string.IsNullOrWhiteSpace(Identity.Phone))
                    ComputerIdentity.Phone = Identity.Phone;
            }
            else if (string.IsNullOrWhiteSpace(ComputerIdentity.Phone) &&
                     !string.IsNullOrWhiteSpace(Identity.Phone))
            {
                ComputerIdentity.Phone = Identity.Phone;
            }
        }

        var current = ComputerIdentity.Callsign?.Trim() ?? "";
        if (current.Length == 0 ||
            current.Equals("WIN-TRACKER", StringComparison.OrdinalIgnoreCase))
        {
            ComputerIdentity.Callsign = Environment.MachineName;
        }

        // Keep legacy Identity mirrored for older UI/code paths during transition.
        Identity = new IdentitySettings
        {
            Callsign = ComputerIdentity.Callsign ?? Environment.MachineName,
            Team = ComputerIdentity.Team,
            Role = ComputerIdentity.Role,
            CotType = ComputerIdentity.CotType,
            Phone = ComputerIdentity.Phone,
        };
    }
}

/// <summary>Computer-level or shared identity fields.</summary>
public sealed class IdentitySettings
{
    /// <summary>Empty means resolve to <see cref="Environment.MachineName"/> at runtime / first persist.</summary>
    public string Callsign { get; set; } = "";
    public string Team { get; set; } = "Cyan";
    public string Role { get; set; } = "Team Member";
    /// <summary>CoT type, e.g. a-f-G-U-C-I (Ground Unit) or a-f-G-E-V (Vehicle).</summary>
    public string CotType { get; set; } = "a-f-G-U-C-I";
    /// <summary>Optional phone for ATAK contact Call (detail/contact@phone). Empty = omit from CoT.</summary>
    public string Phone { get; set; } = "";

    /// <summary>Effective callsign for CoT/UI — machine name when unset.</summary>
    public string GetEffectiveCallsign() =>
        string.IsNullOrWhiteSpace(Callsign) ? Environment.MachineName : Callsign.Trim();
}

/// <summary>Per-Windows-user CoT identity.</summary>
public sealed class UserIdentitySettings
{
    public string UserName { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string Team { get; set; } = "";
    public string Role { get; set; } = "";
    public string CotType { get; set; } = "";
    /// <summary>Optional phone for ATAK contact Call (detail/contact@phone). Empty = omit from CoT.</summary>
    public string Phone { get; set; } = "";
    /// <summary>True when the user dismissed the first-login callsign prompt without saving.</summary>
    public bool SetupPromptDismissed { get; set; }

    public bool HasCallsign => !string.IsNullOrWhiteSpace(Callsign);
}

public sealed class GpsSettings
{
    /// <summary>NmeaThenWindows | WindowsThenNmea | NmeaOnly | WindowsOnly</summary>
    public string SourcePriority { get; set; } = "NmeaThenWindows";
    public string? ComPort { get; set; }
    public int BaudRate { get; set; } = 4800;
    public int LastFixHoldSeconds { get; set; } = 30;
    /// <summary>
    /// When NMEA and Windows Location have no fix, use approximate IP geolocation.
    /// Default false for new configs (coarse; opt-in).
    /// </summary>
    public bool EnableNetworkFallback { get; set; }
}

public sealed class ReportingSettings
{
    /// <summary>Dynamic | Constant</summary>
    public string Strategy { get; set; } = "Dynamic";
    public int ReliableStationarySeconds { get; set; } = 180;
    public int UnreliableStationarySeconds { get; set; } = 30;
    public int ReliableMinSeconds { get; set; } = 5;
    public int ReliableMaxMoveSeconds { get; set; } = 20;
    public int UnreliableMinSeconds { get; set; } = 5;
    public int UnreliableMaxMoveSeconds { get; set; } = 20;
    public int ConstantIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// When the active callsign differs from the Windows computer name, emit the
    /// machine name in CoT <c>detail/remarks</c> so peers can see which device reported.
    /// Default on.
    /// </summary>
    public bool IncludeComputerNameInRemarks { get; set; } = true;
}

public sealed class MeshSaSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Always | OnlyWhenDisconnected — new installs default to OnlyWhenDisconnected.</summary>
    public string Mode { get; set; } = "OnlyWhenDisconnected";
    public string MulticastAddress { get; set; } = "239.2.3.1";
    public int MulticastPort { get; set; } = 6969;
    public string NetworkInterface { get; set; } = "Auto";
}

public sealed class StartupSettings
{
    public bool StartWithWindows { get; set; }
    public bool PreventSleepWhileTracking { get; set; }
}

public sealed class UpdateSettings
{
    public bool AutomaticallyDownloadAndInstall { get; set; }
    public string ReleasesApiUrl { get; set; } =
        "https://api.github.com/repos/CopIXus/WinTAKTracker/releases/latest";
    /// <summary>UTC timestamp of the last successful or failed update check (ISO 8601).</summary>
    public string? LastCheckedUtc { get; set; }
    /// <summary>
    /// Newest version reported by the last successful check when an update was available; cleared when up to date.
    /// </summary>
    public string? LastAvailableVersion { get; set; }
}

public sealed class DiagnosticsSettings
{
    /// <summary>Minimum log level written to disk. Default Error keeps machines quiet.</summary>
    public string LogLevel { get; set; } = "Error";
    /// <summary>Max total size of rotated log files in megabytes (trim oldest / truncate).</summary>
    public int MaxLogSizeMb { get; set; } = 30;
    /// <summary>
    /// When false (default), TLS soft-accept is disabled: trust-store validation must succeed
    /// or the connection is rejected. When true, keep SoftCert-style soft-accept with a warn log.
    /// </summary>
    public bool AllowInsecureTlsSoftAccept { get; set; }
}

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Server";
    public bool Enabled { get; set; } = true;
    /// <summary>Placeholder host only in samples — runtime values stay local.</summary>
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8089;
    /// <summary>ssl | tcp</summary>
    public string Protocol { get; set; } = "ssl";
    public string? Username { get; set; }
    public string? CallsignOverride { get; set; }
    public string? TeamOverride { get; set; }
    public string? RoleOverride { get; set; }
    /// <summary>Filename of DPAPI-protected secret blob (token/password), if any.</summary>
    public string? SecretBlobName { get; set; }
    public string? CertPasswordBlobName { get; set; }
    public string? TrustPasswordBlobName { get; set; }
    public string? ClientCertFileName { get; set; }
    public string? TrustStoreFileName { get; set; }
    public string? CloudTakUrl { get; set; }
    /// <summary>
    /// Per-profile override for TLS soft-accept. Null = use <see cref="DiagnosticsSettings.AllowInsecureTlsSoftAccept"/>.
    /// </summary>
    public bool? AllowInsecureTlsSoftAccept { get; set; }
}
