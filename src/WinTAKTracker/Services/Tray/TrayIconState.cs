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
    public static string ToTooltip(this TrayIconState state) => state switch
    {
        TrayIconState.Ok => "WinTAKTracker — OK",
        TrayIconState.NoGps => "WinTAKTracker — No GPS",
        TrayIconState.Disconnected => "WinTAKTracker — Disconnected",
        TrayIconState.Reconnecting => "WinTAKTracker — Reconnecting…",
        TrayIconState.Paused => "WinTAKTracker — Paused",
        TrayIconState.Error => "WinTAKTracker — Error",
        _ => "WinTAKTracker",
    };
}
