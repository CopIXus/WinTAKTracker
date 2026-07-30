namespace WinTAKTracker.Services.Config;

/// <summary>
/// Application configuration persisted under %LocalAppData%\WinTAKTracker\.
/// Secrets are stored separately via DPAPI — never put real hosts/tokens in repo samples.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public IdentitySettings Identity { get; set; } = new();

    public GpsSettings Gps { get; set; } = new();

    public ReportingSettings Reporting { get; set; } = new();

    public MeshSaSettings MeshSa { get; set; } = new();

    public StartupSettings Startup { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Server profiles (hosts/ports only in cleartext; credentials DPAPI-protected).</summary>
    public List<ServerProfile> Servers { get; set; } = [];

    /// <summary>Optional CloudTAK URL (user-configured; never commit real URLs).</summary>
    public string? CloudTakUrl { get; set; }

    /// <summary>Stable CoT UID for this machine (generated once).</summary>
    public string? DeviceUid { get; set; }
}

public sealed class IdentitySettings
{
    public string Callsign { get; set; } = "WIN-TRACKER";
    public string Team { get; set; } = "Cyan";
    public string Role { get; set; } = "Team Member";
    /// <summary>CoT type, e.g. a-f-G-U-C-I (Ground Unit) or a-f-G-E-V (Vehicle).</summary>
    public string CotType { get; set; } = "a-f-G-U-C-I";
}

public sealed class GpsSettings
{
    /// <summary>NmeaThenWindows | WindowsThenNmea | NmeaOnly | WindowsOnly</summary>
    public string SourcePriority { get; set; } = "NmeaThenWindows";
    public string? ComPort { get; set; }
    public int BaudRate { get; set; } = 4800;
    public int LastFixHoldSeconds { get; set; } = 30;
}

public sealed class ReportingSettings
{
    /// <summary>Dynamic | Constant</summary>
    public string Strategy { get; set; } = "Dynamic";
    public int ReliableStationarySeconds { get; set; } = 180;
    public int UnreliableStationarySeconds { get; set; } = 30;
    public int ReliableMinSeconds { get; set; } = 2;
    public int ReliableMaxMoveSeconds { get; set; } = 20;
    public int UnreliableMinSeconds { get; set; } = 2;
    public int UnreliableMaxMoveSeconds { get; set; } = 20;
    public int ConstantIntervalSeconds { get; set; } = 10;
}

public sealed class MeshSaSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Always | OnlyWhenDisconnected</summary>
    public string Mode { get; set; } = "Always";
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
}

public sealed class DiagnosticsSettings
{
    public string LogLevel { get; set; } = "Information";
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
}
