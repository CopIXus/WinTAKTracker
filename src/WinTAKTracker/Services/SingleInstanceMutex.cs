namespace WinTAKTracker.Services;

/// <summary>
/// Ensures only one WinTAKTracker process runs per user session.
/// </summary>
public sealed class SingleInstanceMutex : IDisposable
{
    private const string MutexName = "Local\\CopIXus.WinTAKTracker.SingleInstance";

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
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return new SingleInstanceMutex(null, owned: false);
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
