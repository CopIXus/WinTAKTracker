using System.Security.Cryptography;
using System.Text;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Protects secret bytes with Windows DPAPI (CurrentUser scope).
/// </summary>
public static class DpapiProtector
{
    public static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public static string ProtectString(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = Protect(bytes);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string protectedBase64)
    {
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var bytes = Unprotect(protectedBytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
