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

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System;

    public static bool TryFormat(ModifierKeys modifiers, Key key, out string? spec)
    {
        spec = null;
        if (key == Key.None || IsModifierKey(key)) return false;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatKey(key));
        spec = string.Join("+", parts);
        return true;
    }

    public static bool TryParse(string spec, out uint mods, out Key key)
    {
        mods = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(spec)) return false;
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
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                mods |= 0x0008;
            else
                return false;
        }

        return Enum.TryParse(parts[^1], true, out key) && key != Key.None && !IsModifierKey(key);
    }

    public static string? DescribeWindowsConflict(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !TryParse(spec, out var mods, out var key))
            return null;
        var mk = ModifierKeys.None;
        if ((mods & 0x0002) != 0) mk |= ModifierKeys.Control;
        if ((mods & 0x0001) != 0) mk |= ModifierKeys.Alt;
        if ((mods & 0x0004) != 0) mk |= ModifierKeys.Shift;
        if ((mods & 0x0008) != 0) mk |= ModifierKeys.Windows;
        return DescribeWindowsConflict(mk, key);
    }

    public static string? DescribeWindowsConflict(ModifierKeys modifiers, Key key)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows))
            return "Win+… shortcuts are reserved for Windows (Start, Snap, desktop, etc.).";

        if (modifiers == ModifierKeys.Alt && key is Key.Tab or Key.Escape or Key.F4)
            return key switch
            {
                Key.Tab => "Alt+Tab switches windows.",
                Key.Escape => "Alt+Esc cycles windows.",
                Key.F4 => "Alt+F4 closes the active window.",
                _ => "Common Alt system shortcut.",
            };

        if (modifiers == ModifierKeys.Control && key == Key.Escape)
            return "Ctrl+Esc opens the Start menu.";

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Escape)
            return "Ctrl+Shift+Esc opens Task Manager.";

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.Delete)
            return "Ctrl+Alt+Delete is a secure attention sequence and cannot be reliably remapped.";

        if (modifiers == ModifierKeys.Control)
        {
            return key switch
            {
                Key.C => "Ctrl+C is Copy.",
                Key.V => "Ctrl+V is Paste.",
                Key.X => "Ctrl+X is Cut.",
                Key.A => "Ctrl+A is Select all.",
                Key.Z => "Ctrl+Z is Undo.",
                Key.Y => "Ctrl+Y is Redo.",
                Key.S => "Ctrl+S is Save.",
                Key.P => "Ctrl+P is Print.",
                Key.N => "Ctrl+N is New.",
                Key.O => "Ctrl+O is Open.",
                Key.W => "Ctrl+W often closes the current tab/window.",
                Key.F => "Ctrl+F is Find.",
                Key.H => "Ctrl+H is often Replace / History.",
                Key.T => "Ctrl+T often opens a new tab.",
                Key.R => "Ctrl+R is often Refresh.",
                Key.D => "Ctrl+D is often Bookmark / desktop shortcut.",
                Key.L => "Ctrl+L often focuses the address bar.",
                Key.Tab => "Ctrl+Tab cycles tabs in many apps.",
                _ => null,
            };
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return key switch
            {
                Key.N => "Ctrl+Shift+N is often New window / Incognito.",
                Key.T => "Ctrl+Shift+T restores the last closed tab.",
                Key.Escape => "Ctrl+Shift+Esc opens Task Manager.",
                _ => null,
            };
        }

        if (modifiers == ModifierKeys.None && key == Key.F1)
            return "F1 usually opens Help.";

        if (modifiers == ModifierKeys.None && key is Key.PrintScreen or Key.Snapshot)
            return "Print Screen captures the screen.";

        return null;
    }

    private static string FormatKey(Key key) => key.ToString();

    private static void EnsureHook()
    {
        if (_source is not null) return;
        var helper = new WindowInteropHelper(Application.Current.MainWindow ?? new Window());
        if (helper.Handle == IntPtr.Zero)
        {
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

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
