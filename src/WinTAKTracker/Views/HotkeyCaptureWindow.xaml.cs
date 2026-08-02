using System.Windows;
using System.Windows.Input;
using WinTAKTracker.Services.Video;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WinTAKTracker.Views;

public partial class HotkeyCaptureWindow : Window
{
    public string? CapturedHotkey { get; private set; }

    public HotkeyCaptureWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (VideoHotkeyService.IsModifierKey(key))
            return;

        var mods = Keyboard.Modifiers;
        // Ignore bare Tab / Enter noise while focusing buttons.
        if (mods == ModifierKeys.None && key is Key.Tab or Key.Enter or Key.Space)
            return;

        if (!VideoHotkeyService.TryFormat(mods, key, out var spec) || spec is null)
            return;

        CapturedHotkey = spec;
        CapturedText.Text = spec;
        AcceptBtn.IsEnabled = true;

        var warning = VideoHotkeyService.DescribeWindowsConflict(mods, key);
        if (warning is null)
        {
            WarnBanner.Visibility = Visibility.Collapsed;
            WarnText.Text = "";
            AcceptBtn.Content = "Use this hotkey";
        }
        else
        {
            WarnBanner.Visibility = Visibility.Visible;
            WarnText.Text = "Windows / system shortcut conflict: " + warning +
                            "\nUsing this may override or fight the normal OS behavior. Continue only if you intend that.";
            AcceptBtn.Content = "Use anyway";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CapturedHotkey = "";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CapturedHotkey))
            return;

        var warning = VideoHotkeyService.DescribeWindowsConflict(CapturedHotkey);
        if (warning is not null)
        {
            var confirm = AppDialog.Show(
                this,
                $"This looks like a normal Windows shortcut:\n\n{CapturedHotkey}\n\n{warning}\n\nUse it for video start/stop anyway?",
                "Confirm hotkey",
                MessageBoxButton.YesNo,
                dangerPrimary: true);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        DialogResult = true;
        Close();
    }
}
