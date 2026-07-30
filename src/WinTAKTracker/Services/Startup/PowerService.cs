using System.Runtime.InteropServices;

namespace WinTAKTracker.Services.Startup;

/// <summary>Optional prevent-sleep while tracking (default off).</summary>
public sealed class PowerService : IDisposable
{
    private bool _preventing;

    public void SetPreventSleep(bool enabled)
    {
        if (enabled == _preventing) return;
        if (enabled)
        {
            SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.AwayModeRequired);
            _preventing = true;
        }
        else
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            _preventing = false;
        }
    }

    public void Dispose()
    {
        if (_preventing) SetPreventSleep(false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [Flags]
    private enum ExecutionState : uint
    {
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }
}
