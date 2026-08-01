using System.Windows;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace WinTAKTracker.Services.Theme;

/// <summary>Follows Windows AppsUseLightTheme; swaps Colors.Light / Colors.Dark at runtime.</summary>
public sealed class ThemeManager : IDisposable
{
    private readonly UISettings _uiSettings = new();
    private bool _isLight = true;
    private bool _disposed;
    private bool _windowHooked;

    public bool IsLightTheme => _isLight;

    public void Start()
    {
        ApplySystemTheme();
        HookWindowLoaded();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnColorValuesChanged(UISettings sender, object args) =>
        Application.Current?.Dispatcher.BeginInvoke(ApplySystemTheme);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            Application.Current?.Dispatcher.BeginInvoke(ApplySystemTheme);
    }

    private void HookWindowLoaded()
    {
        if (_windowHooked) return;
        _windowHooked = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            WindowChromeHelper.ApplyToWindow(window, dark: !_isLight);
    }

    public void ApplySystemTheme()
    {
        var light = ReadAppsUseLightTheme();
        if (light == _isLight && Application.Current?.Resources.MergedDictionaries.Count > 0)
        {
            // Still ensure colors dict is present on first call.
            if (FindColorsDictionary() is not null)
            {
                WindowChromeHelper.ApplyToAllWindows(dark: !light);
                return;
            }
        }

        _isLight = light;
        var app = Application.Current;
        if (app is null) return;

        var uri = new Uri(
            light
                ? "Themes/Colors.Light.xaml"
                : "Themes/Colors.Dark.xaml",
            UriKind.Relative);

        var colors = new ResourceDictionary { Source = uri };
        var existing = FindColorsDictionary();
        if (existing is not null)
            app.Resources.MergedDictionaries.Remove(existing);

        // Keep colors before AppTheme so DynamicResource resolves.
        app.Resources.MergedDictionaries.Insert(0, colors);
        WindowChromeHelper.ApplyToAllWindows(dark: !light);
    }

    private static ResourceDictionary? FindColorsDictionary()
    {
        var app = Application.Current;
        if (app is null) return null;
        foreach (var d in app.Resources.MergedDictionaries)
        {
            var src = d.Source?.OriginalString ?? "";
            if (src.Contains("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase))
                return d;
        }

        return null;
    }

    public static bool ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i != 0;
        }
        catch
        {
            // fall through
        }

        try
        {
            var settings = new UISettings();
            var bg = settings.GetColorValue(UIColorType.Background);
            // Light backgrounds are bright.
            return (bg.R + bg.G + bg.B) / 3.0 > 128;
        }
        catch
        {
            return true;
        }
    }
}
