namespace WinTAKTracker.Services.Tray;

/// <summary>
/// Visual/tooltip states for the tray icon (Phase 1 stubs; overlays in later phases).
/// </summary>
public enum TrayIconState
{
    Ok,
    NoGps,
    Disconnected,
    Reconnecting,
    Paused,
    Error,
}

public static class TrayIconStateExtensions
{
    public static string ToStatusLabel(this TrayIconState state) => state switch
    {
        TrayIconState.Ok => "OK",
        TrayIconState.NoGps => "No GPS",
        TrayIconState.Disconnected => "Disconnected",
        TrayIconState.Reconnecting => "Reconnecting…",
        TrayIconState.Paused => "Paused",
        TrayIconState.Error => "Error",
        _ => "Unknown",
    };

    /// <summary>
    /// Concise tray tip: app + version + status, optionally a short update hint.
    /// Keep under NotifyIcon.Text limits (~63–128 chars).
    /// </summary>
    public static string ToTooltip(this TrayIconState state, string version, string? updateVersion = null)
    {
        var ver = string.IsNullOrWhiteSpace(version) ? "" : $" {version.Trim()}";
        var tip = $"WinTAKTracker{ver} — {state.ToStatusLabel()}";
        if (!string.IsNullOrWhiteSpace(updateVersion))
            tip += $" · upd {updateVersion.Trim()}";
        return tip;
    }
}
