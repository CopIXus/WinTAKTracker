namespace WinTAKTracker.Services;

/// <summary>
/// Ensures only one WinTAKTracker process runs per user session.
/// Recovers cleanly if a previous instance was killed (abandoned mutex).
/// </summary>
public sealed class SingleInstanceMutex : IDisposable
{
    // v2: recover from abandoned mutex after force-kill; new name avoids stuck v1 handles in-session.
    private const string MutexName = "Local\\CopIXus.WinTAKTracker.SingleInstance.v2";

    private readonly Mutex? _mutex;
    private readonly bool _owned;

    private SingleInstanceMutex(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public bool IsPrimaryInstance => _owned;

    public static SingleInstanceMutex TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        try
        {
            // WaitOne(0) throws AbandonedMutexException if the previous owner died — we then own it.
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return new SingleInstanceMutex(null, owned: false);
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed or was killed; this process is now the owner.
        }

        return new SingleInstanceMutex(mutex, owned: true);
    }

    public void Dispose()
    {
        if (_mutex is null)
            return;

        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
        }

        _mutex.Dispose();
    }
}
