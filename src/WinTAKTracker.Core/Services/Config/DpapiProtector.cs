using System.Security.Cryptography;
using System.Text;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Protects secret bytes with Windows DPAPI.
/// CurrentUser for portable tray; LocalMachine for service / ProgramData store.
/// </summary>
public static class DpapiProtector
{
    public static byte[] Protect(byte[] plaintext, DataProtectionScope scope = DataProtectionScope.CurrentUser) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, scope);

    public static byte[] Unprotect(byte[] protectedBytes, DataProtectionScope scope = DataProtectionScope.CurrentUser) =>
        ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope);

    public static string ProtectString(string plaintext, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = Protect(bytes, scope);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string protectedBase64, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var bytes = Unprotect(protectedBytes, scope);
        return Encoding.UTF8.GetString(bytes);
    }
}
