using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinTAKTracker.Services;

namespace WinTAKTracker.Services.Video;

/// <summary>Global hotkey for start/stop primary video feed (RegisterHotKey).</summary>
public static class VideoHotkeyService
{
    private const int HotkeyId = 0x57_54_54; // WTT
    private static HwndSource? _source;
    private static AppHost? _host;
    private static bool _registered;

    public static void Register(AppHost host)
    {
        _host = host;
        var spec = host.Config.Video.Hotkey;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            EnsureHook();
            Unregister();
            if (string.IsNullOrWhiteSpace(spec) || _source is null) return;
            if (!TryParse(spec, out var mods, out var key)) return;
            _registered = RegisterHotKey(_source.Handle, HotkeyId, mods, KeyInterop.VirtualKeyFromKey(key));
        });
    }

    private static void EnsureHook()
    {
        if (_source is not null) return;
        var helper = new WindowInteropHelper(Application.Current.MainWindow ?? new Window());
        if (helper.Handle == IntPtr.Zero)
        {
            // Create a message-only window host via hidden Window.
            var w = new Window
            {
                Width = 0,
                Height = 0,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                AllowsTransparency = true,
            };
            w.Show();
            helper = new WindowInteropHelper(w);
            helper.EnsureHandle();
            _source = HwndSource.FromHwnd(helper.Handle);
        }
        else
        {
            _source = HwndSource.FromHwnd(helper.Handle);
        }

        _source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg != wmHotkey || wParam.ToInt32() != HotkeyId || _host is null)
            return IntPtr.Zero;

        handled = true;
        _ = ToggleAsync(_host);
        return IntPtr.Zero;
    }

    private static async Task ToggleAsync(AppHost host)
    {
        try
        {
            var feed = host.Config.Video.Feeds.FirstOrDefault(f => f.Enabled);
            if (feed is null) return;
            var live = host.Video.SnapshotRuntimes().Any(r => r.FeedId == feed.Id && r.IsLive);
            if (live) await host.Video.StopFeedAsync(feed.Id).ConfigureAwait(false);
            else await host.Video.StartFeedAsync(feed.Id).ConfigureAwait(false);
        }
        catch
        {
            // ignore hotkey failures
        }
    }

    private static void Unregister()
    {
        if (!_registered || _source is null) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private static bool TryParse(string spec, out uint mods, out Key key)
    {
        mods = 0;
        key = Key.None;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts.Take(parts.Length - 1))
        {
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0002;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0001;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0004;
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0008;
        }

        return Enum.TryParse(parts[^1], true, out key) && key != Key.None;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
