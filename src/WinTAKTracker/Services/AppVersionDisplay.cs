namespace WinTAKTracker.Services;

/// <summary>Shared window-title / branding strings that include the running version.</summary>
public static class AppVersionDisplay
{
    public static string WindowTitle(string version, string page) =>
        string.IsNullOrWhiteSpace(page)
            ? $"WinTAKTracker {version}"
            : $"WinTAKTracker {version} — {page}";
}
