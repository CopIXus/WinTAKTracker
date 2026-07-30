namespace WinTAKTracker.Services.Config;

/// <summary>
/// Session lock for Settings edits. Password is DPAPI-protected under LocalAppData — never plaintext in config.json.
/// App start defaults to locked when a password is configured.
/// </summary>
public sealed class SettingsLockService
{
    public const string SecretBlobName = "settings-lock";

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
        if (stored is null || !FixedTimeEquals(stored, password ?? ""))
            return false;

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

        _store.WriteSecret(SecretBlobName, newPassword);
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

        var stored = _store.ReadSecret(SecretBlobName);
        if (stored is null || !FixedTimeEquals(stored, currentPassword ?? ""))
            return false;

        if (string.IsNullOrEmpty(newPassword))
            return false;

        _store.WriteSecret(SecretBlobName, newPassword);
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

        var stored = _store.ReadSecret(SecretBlobName);
        if (stored is null || !FixedTimeEquals(stored, currentPassword ?? ""))
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

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
        {
            // Still compare to reduce trivial timing leaks on length.
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, ba);
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
