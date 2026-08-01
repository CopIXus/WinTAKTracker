using System.ServiceProcess;

namespace WinTAKTracker.Services.Config;

/// <summary>Well-known config roots for portable (user) vs always-on (service) modes.</summary>
public static class ConfigPaths
{
    public const string AppFolderName = "WinTAKTracker";
    public const string ServiceName = "WinTAKTracker";

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
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Running / Stopped / Not installed / other SCM state.</summary>
    public static string GetWindowsServiceStatusLabel()
    {
        if (!IsServiceInstalled())
            return "Not installed";

        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.StartPending => "Starting",
                ServiceControllerStatus.StopPending => "Stopping",
                ServiceControllerStatus.Paused => "Paused",
                ServiceControllerStatus.PausePending => "Pausing",
                ServiceControllerStatus.ContinuePending => "Continuing",
                _ => sc.Status.ToString(),
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    public static string GetTrackingModeLabel(bool attachedToService) =>
        attachedToService ? "Service" : "Standalone";

    /// <summary>
    /// Best-effort start when installed but stopped. Returns true if running (or already was).
    /// May fail without elevation — callers should treat false as non-fatal.
    /// </summary>
    public static bool TryEnsureServiceRunning(TimeSpan? wait = null)
    {
        if (!IsServiceInstalled()) return false;
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return true;
            if (sc.Status != ServiceControllerStatus.Stopped)
                return false;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, wait ?? TimeSpan.FromSeconds(15));
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }
}
