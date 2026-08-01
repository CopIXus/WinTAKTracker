using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WinTAKTracker.Services.Config;

namespace WinTAKTracker.Services.Tak;

/// <summary>
/// Loads PKCS#12 material for Windows Schannel (SslStream / HttpClient mTLS).
/// EphemeralKeySet must not be used for TLS client auth on Windows — Schannel/LSASS
/// cannot use in-memory keys (SEC_E_NO_CREDENTIALS / AuthenticationException).
/// </summary>
public static class SchannelCertificateLoader
{
    /// <summary>
    /// Load a client (or any) PFX with a key storage mode Schannel can use.
    /// Machine store → MachineKeySet (LocalSystem / service). User store → UserKeySet.
    /// </summary>
    public static X509Certificate2 LoadPfx(string path, string password, DataProtectionScope dpapiScope)
    {
        var flags = KeyFlags(dpapiScope);
        return new X509Certificate2(path, password, flags);
    }

    /// <summary>Same as <see cref="LoadPfx"/> but from bytes (e.g. SoftCert in-memory).</summary>
    public static X509Certificate2 LoadPfx(byte[] pfxBytes, string password, DataProtectionScope dpapiScope)
    {
        var flags = KeyFlags(dpapiScope);
        return new X509Certificate2(pfxBytes, password, flags);
    }

    public static X509KeyStorageFlags KeyFlags(DataProtectionScope dpapiScope) =>
        dpapiScope == DataProtectionScope.LocalMachine
            ? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;

    /// <summary>True when InnerException looks like Schannel ephemeral-key / no-credentials.</summary>
    public static bool IsSchannelNoCredentials(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is System.ComponentModel.Win32Exception w32 &&
                (w32.NativeErrorCode == unchecked((int)0x8009030E) || w32.NativeErrorCode == -2146893810))
                return true;
            if (e.Message.Contains("No credentials are available in the security package",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
