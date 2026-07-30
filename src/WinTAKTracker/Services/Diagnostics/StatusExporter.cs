using System.Text.Json;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;

namespace WinTAKTracker.Services.Diagnostics;

public sealed class StatusExporter
{
    public string ExportJson(
        AppConfig config,
        IGpsService gps,
        ITakConnectionManager tak,
        MeshSaBroadcaster mesh,
        PauseService pause,
        TrayIconState trayState)
    {
        var servers = tak.GetStatuses().Select(s => new
        {
            id = MaskId(s.ProfileId),
            enabled = s.Enabled,
            state = s.State.ToString(),
            protocol = s.Protocol,
            lastErrorCode = s.LastErrorCode,
        }).ToList();

        var fix = gps.CurrentFix;
        var payload = new
        {
            exportedAt = DateTimeOffset.UtcNow,
            appVersion = typeof(StatusExporter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            os = Environment.OSVersion.VersionString,
            trayState = trayState.ToString(),
            paused = pause.IsPaused,
            gps = new
            {
                hasFix = fix is not null,
                isHeld = fix?.IsHeld ?? false,
                source = fix?.Source.ToString() ?? "None",
                windowsPermission = gps.WindowsPermission.ToString(),
                // Coordinates intentionally omitted from default redacted export.
            },
            servers = new
            {
                configured = config.Servers.Count,
                enabled = config.Servers.Count(s => s.Enabled),
                connected = servers.Count(s => s.state == nameof(TakConnectionState.Connected)),
                details = servers,
            },
            mesh = new
            {
                enabled = config.MeshSa.Enabled,
                mode = config.MeshSa.Mode,
                lastSendUtc = mesh.LastSendUtc,
                lastInterface = mesh.LastInterfaceDescription,
                lastErrorCode = mesh.LastErrorCode,
            },
            identity = new
            {
                cotType = config.Identity.CotType,
                // callsign/team omitted by default
            },
            clockSkewWarning = DetectClockSkew(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string MaskId(string id) =>
        id.Length <= 6 ? "***" : id[..3] + "…" + id[^2..];

    private static string? DetectClockSkew()
    {
        // Coarse check: year wildly off relative to build era.
        var year = DateTime.UtcNow.Year;
        if (year < 2020 || year > 2100)
            return "System clock year looks wrong; CoT stale windows may fail.";
        return null;
    }
}
