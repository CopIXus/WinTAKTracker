namespace WinTAKTracker.Services.Config;

/// <summary>Well-known config roots for portable (user) vs always-on (service) modes.</summary>
public static class ConfigPaths
{
    public const string AppFolderName = "WinTAKTracker";

    /// <summary>Legacy / portable tray: %LocalAppData%\WinTAKTracker\</summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>Machine-wide service store: %ProgramData%\WinTAKTracker\</summary>
    public static string MachineRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppFolderName);

    public static bool MachineStoreExists() =>
        File.Exists(Path.Combine(MachineRoot, "config.json"));

    public static bool IsServiceInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\WinTAKTracker");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
