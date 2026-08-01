using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinTAKTracker.Services.Theme;

/// <summary>Applies DWM immersive dark mode to WPF window title bars.</summary>
public static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static void ApplyToWindow(Window window, bool dark)
    {
        if (window is null) return;

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetImmersiveDarkMode(hwnd, dark);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply();
            return;
        }

        window.SourceInitialized -= OnSourceInitialized;
        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Apply();
        }
    }

    public static void ApplyToAllWindows(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        foreach (Window window in app.Windows)
            ApplyToWindow(window, dark);
    }

    private static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
