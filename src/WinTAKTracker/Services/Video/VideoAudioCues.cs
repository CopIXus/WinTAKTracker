using System.Media;

namespace WinTAKTracker.Services.Video;

public static class VideoAudioCues
{
    public static void PlayStart(bool enabled)
    {
        if (!enabled) return;
        try { SystemSounds.Asterisk.Play(); } catch { /* ignore */ }
    }

    public static void PlayStop(bool enabled)
    {
        if (!enabled) return;
        try { SystemSounds.Hand.Play(); } catch { /* ignore */ }
    }

    public static void PlayPing(bool enabled)
    {
        if (!enabled) return;
        try { SystemSounds.Beep.Play(); } catch { /* ignore */ }
    }
}
