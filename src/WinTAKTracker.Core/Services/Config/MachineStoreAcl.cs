using System.Security.AccessControl;
using System.Security.Principal;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Hardens %ProgramData%\WinTAKTracker ACLs for the Windows Service + tray companion.
/// Root / logs / updates: SYSTEM + Administrators Full; Authenticated Users Modify.
/// secrets / certs: SYSTEM + Administrators Full only (tray mutates secrets via IPC when possible).
/// </summary>
public static class MachineStoreAcl
{
    /// <summary>
    /// Create machine root + sensitive subdirs and apply the hardened ACL model.
    /// Best-effort: succeeds when called as SYSTEM/admin; tray may fail silently if already locked down.
    /// </summary>
    public static void EnsureUsersCanModify(string? rootDir = null)
    {
        var root = rootDir ?? ConfigPaths.MachineRoot;
        Directory.CreateDirectory(root);
        var secrets = Path.Combine(root, "secrets");
        var certs = Path.Combine(root, "certs");
        var logs = Path.Combine(root, "logs");
        var updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(secrets);
        Directory.CreateDirectory(certs);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(updates);

        try
        {
            ApplyRootAcl(root);
            ApplySensitiveAcl(secrets);
            ApplySensitiveAcl(certs);
            // logs / updates inherit Authenticated Users Modify from root (also re-assert for clarity).
            ApplyRootAcl(logs);
            ApplyRootAcl(updates);
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

    /// <summary>SYSTEM + Admins Full; Authenticated Users Modify (OI)(CI).</summary>
    public static void ApplyRootAcl(string path)
    {
        Directory.CreateDirectory(path);
        var dir = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        security.AddAccessRule(FullControl(system));
        security.AddAccessRule(FullControl(admins));
        security.AddAccessRule(new FileSystemAccessRule(
            authUsers,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        dir.SetAccessControl(security);
    }

    /// <summary>SYSTEM + Admins Full only — no Authenticated Users / Builtin Users Modify.</summary>
    public static void ApplySensitiveAcl(string path)
    {
        Directory.CreateDirectory(path);
        var dir = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(FullControl(system));
        security.AddAccessRule(FullControl(admins));

        dir.SetAccessControl(security);
    }

    private static FileSystemAccessRule FullControl(SecurityIdentifier sid) =>
        new(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

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
