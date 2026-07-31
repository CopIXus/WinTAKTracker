using System.Security.AccessControl;
using System.Security.Principal;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Ensures interactive users can read/write %ProgramData%\WinTAKTracker when the folder
/// was first created by LocalSystem (installer/service). Without this, the tray (asInvoker)
/// hits "Access to the path is denied" on EnsureDirectories/Save.
/// </summary>
public static class MachineStoreAcl
{
    /// <summary>
    /// Create the machine root if needed and grant Builtin Users Modify (OI)(CI).
    /// Best-effort: succeeds when called as SYSTEM/admin; tray may fail silently if already locked down.
    /// </summary>
    public static void EnsureUsersCanModify(string? rootDir = null)
    {
        var root = rootDir ?? ConfigPaths.MachineRoot;
        Directory.CreateDirectory(root);

        try
        {
            var dir = new DirectoryInfo(root);
            var security = dir.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var rule = new FileSystemAccessRule(
                users,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            security.ModifyAccessRule(AccessControlModification.Add, rule, out _);
            dir.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            // Non-elevated caller cannot change SYSTEM-owned ACLs.
        }
        catch (Exception)
        {
            // Best-effort only — service/installer are the reliable paths.
        }
    }

    public static bool IsMachineRoot(string rootDir)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ConfigPaths.MachineRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
