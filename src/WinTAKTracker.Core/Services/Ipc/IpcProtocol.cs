using System.Text.Json;
using System.Text.Json.Serialization;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Tray;

namespace WinTAKTracker.Services.Ipc;

public static class IpcDefaults
{
    public const string PipeName = "WinTAKTracker.Control";
    public static string PipePath => @"\\.\pipe\" + PipeName;
}

public enum IpcMethod
{
    Ping,
    GetStatus,
    GetConfig,
    SetConfig,
    Pause,
    Resume,
    ReloadConnections,
    SetComputerIdentity,
    SetUserIdentity,
    SetActiveSession,
    DismissUserSetupPrompt,
}

public sealed class IpcRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Method { get; set; } = nameof(IpcMethod.Ping);
    public JsonElement? Payload { get; set; }
}

public sealed class IpcResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Result { get; set; }
}

public sealed class TrackerStatusDto
{
    public bool ServiceMode { get; set; } = true;
    public bool Paused { get; set; }
    public bool HasGpsFix { get; set; }
    public string? GpsSource { get; set; }
    public bool AnyTakConnected { get; set; }
    public bool AnyTakReconnecting { get; set; }
    public bool MeshReady { get; set; }
    public string? MeshLastError { get; set; }
    public DateTimeOffset? LastPliSentUtc { get; set; }
    public string TrayState { get; set; } = nameof(TrayIconState.Disconnected);
    public ActiveIdentityDto? ActiveIdentity { get; set; }
    public string ConfigRoot { get; set; } = "";
    /// <summary>Per-server live stream state (Connected means CotStreamClient is up).</summary>
    public List<ServerStatusDto> Servers { get; set; } = [];
}

public sealed class ServerStatusDto
{
    public string ProfileId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public string Protocol { get; set; } = "ssl";
    public string State { get; set; } = "Disconnected";
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? LastSendUtc { get; set; }
}

public sealed class ActiveIdentityDto
{
    public string Callsign { get; set; } = "";
    public string Team { get; set; } = "";
    public string Role { get; set; } = "";
    public string CotType { get; set; } = "";
    public string Source { get; set; } = "Computer";
    public string? UserSid { get; set; }
    public string? UserName { get; set; }

    public static ActiveIdentityDto From(ActiveIdentity id) => new()
    {
        Callsign = id.Callsign,
        Team = id.Team,
        Role = id.Role,
        CotType = id.CotType,
        Source = id.Source,
        UserSid = id.UserSid,
        UserName = id.UserName,
    };
}

public sealed class IdentityUpdateDto
{
    public string? UserSid { get; set; }
    public string? UserName { get; set; }
    public string Callsign { get; set; } = "";
    public string Team { get; set; } = "Cyan";
    public string Role { get; set; } = "Team Member";
    public string CotType { get; set; } = "a-f-G-U-C-I";
}

public sealed class SessionUpdateDto
{
    public string? UserSid { get; set; }
    public string? UserName { get; set; }
    public bool LoggedOn { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
