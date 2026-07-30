namespace WinTAKTracker.Services.Pause;

/// <summary>
/// Pause / mute outbound CoT (servers + mesh). Phase 1 stub — no network paths yet.
/// </summary>
public sealed class PauseService
{
    private readonly object _gate = new();
    private bool _isPaused;

    public bool IsPaused
    {
        get { lock (_gate) return _isPaused; }
    }

    public event EventHandler<bool>? PauseChanged;

    public void Pause()
    {
        lock (_gate)
        {
            if (_isPaused)
                return;
            _isPaused = true;
        }

        PauseChanged?.Invoke(this, true);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_isPaused)
                return;
            _isPaused = false;
        }

        PauseChanged?.Invoke(this, false);
    }

    public void Toggle()
    {
        if (IsPaused)
            Resume();
        else
            Pause();
    }
}
