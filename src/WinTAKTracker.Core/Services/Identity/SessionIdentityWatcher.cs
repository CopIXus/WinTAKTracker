using System.Runtime.InteropServices;
using WinTAKTracker.Services.Diagnostics;
using WinTAKTracker.Services.Host;

namespace WinTAKTracker.Services.Identity;

/// <summary>
/// Best-effort console session watch: when the interactive session goes away, revert to computer callsign.
/// Tray UI also pushes SetActiveSession over IPC on logon for a clearer SID.
/// </summary>
public sealed class SessionIdentityWatcher : IDisposable
{
    private readonly TrackingHost _host;
    private readonly IRedactedLogger _log;
    private readonly System.Threading.Timer _timer;
    private bool? _hadInteractiveSession;

    public SessionIdentityWatcher(TrackingHost host, IRedactedLogger log)
    {
        _host = host;
        _log = log;
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    private void Poll()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            var hasInteractive = sessionId != 0xFFFFFFFF && sessionId != 0;

            // Session 0 services: console session id 0 can mean no one, or services session.
            // Prefer WTSQuerySessionInformation when available; fall back to "has console session".
            var loggedOn = TryHasLoggedOnUser(sessionId);

            if (_hadInteractiveSession == loggedOn) return;
            _hadInteractiveSession = loggedOn;

            if (!loggedOn)
            {
                _host.SetActiveSession(null, null);
                _log.Info("Session", "No interactive user — CoT identity reverted to computer callsign.");
            }
            else
            {
                // SID is filled in when the tray attaches via IPC; until then keep computer identity
                // unless a previous SID was already set.
                _log.Info("Session", "Interactive session present — awaiting tray identity attach.");
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Session", ex.Message);
        }
    }

    private static bool TryHasLoggedOnUser(uint sessionId)
    {
        if (sessionId == 0xFFFFFFFF) return false;
        // Query username for the console session.
        if (!WTSQuerySessionInformation(IntPtr.Zero, (int)sessionId, WTS_INFO_CLASS.WTSUserName,
                out var buffer, out var bytes) || buffer == IntPtr.Zero || bytes <= 2)
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            return false;
        }

        try
        {
            var name = Marshal.PtrToStringUni(buffer)?.Trim() ?? "";
            return name.Length > 0;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    public void Dispose() => _timer.Dispose();
}
