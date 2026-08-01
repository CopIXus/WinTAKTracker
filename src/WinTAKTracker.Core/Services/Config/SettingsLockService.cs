using System.Security.Cryptography;
using System.Text;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Session lock for Settings edits. Password is DPAPI-protected under the config store —
/// never plaintext in config.json. New passwords are stored as SHA-256 + salt; legacy
/// plaintext blobs are re-hashed on successful unlock.
/// App start defaults to locked when a password is configured.
/// </summary>
public sealed class SettingsLockService
{
    public const string SecretBlobName = "settings-lock";
    private const string HashPrefix = "sha256$v1$";

    private readonly AppConfigStore _store;
    private bool _sessionUnlocked;

    public SettingsLockService(AppConfigStore store)
    {
        _store = store;
        // Safer default for a tracker left running: locked when a password exists.
        _sessionUnlocked = !HasPasswordConfigured();
    }

    public event EventHandler? LockStateChanged;

    /// <summary>True when a lock password blob exists.</summary>
    public bool HasPassword => HasPasswordConfigured();

    /// <summary>True when a password is set and this session has not unlocked.</summary>
    public bool IsLocked => HasPassword && !_sessionUnlocked;

    public bool IsUnlocked => !IsLocked;

    public bool TryUnlock(string password)
    {
        if (!HasPassword)
        {
            _sessionUnlocked = true;
            Raise();
            return true;
        }

        var stored = _store.ReadSecret(SecretBlobName);
        if (stored is null)
            return false;

        if (IsHashedBlob(stored))
        {
            if (!VerifyHash(stored, password ?? ""))
                return false;
        }
        else
        {
            // Legacy plaintext DPAPI blob — migrate to salted hash on success.
            if (!FixedTimeEquals(stored, password ?? ""))
                return false;
            _store.WriteSecret(SecretBlobName, HashPassword(password ?? ""));
        }

        _sessionUnlocked = true;
        Raise();
        return true;
    }

    public void Lock()
    {
        if (!HasPassword) return;
        if (!_sessionUnlocked) return;
        _sessionUnlocked = false;
        Raise();
    }

    /// <summary>Create or replace the lock password. Caller must ensure session is unlocked (or no password yet).</summary>
    public void SetPassword(string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("Password cannot be empty.", nameof(newPassword));

        _store.WriteSecret(SecretBlobName, HashPassword(newPassword));
        _sessionUnlocked = true;
        Raise();
    }

    public bool ChangePassword(string currentPassword, string newPassword)
    {
        if (!HasPassword)
        {
            SetPassword(newPassword);
            return true;
        }

        if (!TryUnlock(currentPassword))
            return false;

        if (string.IsNullOrEmpty(newPassword))
            return false;

        _store.WriteSecret(SecretBlobName, HashPassword(newPassword));
        _sessionUnlocked = true;
        Raise();
        return true;
    }

    /// <summary>Remove lock password (requires current password). Leaves session unlocked.</summary>
    public bool ClearPassword(string currentPassword)
    {
        if (!HasPassword)
        {
            _sessionUnlocked = true;
            Raise();
            return true;
        }

        if (!TryUnlock(currentPassword))
            return false;

        _store.DeleteSecret(SecretBlobName);
        _sessionUnlocked = true;
        Raise();
        return true;
    }

    private bool HasPasswordConfigured()
    {
        var stored = _store.ReadSecret(SecretBlobName);
        return !string.IsNullOrEmpty(stored);
    }

    private void Raise() => LockStateChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsHashedBlob(string stored) =>
        stored.StartsWith(HashPrefix, StringComparison.Ordinal);

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Hash(password, salt);
        return HashPrefix + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    private static bool VerifyHash(string stored, string password)
    {
        // sha256$v1$<saltB64>$<hashB64>
        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Hash(string password, byte[] salt)
    {
        var pwd = Encoding.UTF8.GetBytes(password);
        var buf = new byte[salt.Length + pwd.Length];
        Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
        Buffer.BlockCopy(pwd, 0, buf, salt.Length, pwd.Length);
        return SHA256.HashData(buf);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
        {
            CryptographicOperations.FixedTimeEquals(ba, ba);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
