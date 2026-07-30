using System.Diagnostics;
using System.Drawing;
using System.Windows;
using WinTAKTracker.Views;
using Forms = System.Windows.Forms;

namespace WinTAKTracker.Services.Tray;

/// <summary>WinForms NotifyIcon host for the WPF tray experience.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppHost _host;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _baseIcon;
    private TrayIconState _state = TrayIconState.Disconnected;
    private SettingsWindow? _settingsWindow;

    public TrayIconService(AppHost host)
    {
        _host = host;
        _baseIcon = LoadAppIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Visible = true,
            Text = _state.ToTooltip(),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var pauseItem = new Forms.ToolStripMenuItem("Pause tracking");
        pauseItem.Click += (_, _) => _host.Pause.Toggle();
        menu.Items.Add(pauseItem);
        menu.Items.Add("Open CloudTAK", null, (_, _) => OpenCloudTak());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowSettings();
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _host.Pause.PauseChanged += OnPauseChanged;
        ApplyState(_state);
        RefreshPauseMenuText();
    }

    public TrayIconState CurrentState => _state;

    public void SetState(TrayIconState state)
    {
        if (_host.Pause.IsPaused && state != TrayIconState.Paused && state != TrayIconState.Error)
            state = TrayIconState.Paused;
        ApplyState(state);
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch { /* ignore */ }
    }

    public void ShowSettings()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(_host);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            _settingsWindow.Show();
            _settingsWindow.Activate();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
        });
    }

    private void ApplyState(TrayIconState state)
    {
        _state = state;
        _notifyIcon.Text = TruncateTooltip(state.ToTooltip());
        _notifyIcon.Icon = _baseIcon;
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshPauseMenuText();
            _host.RefreshTray();
        });
    }

    private void RefreshPauseMenuText()
    {
        if (_notifyIcon.ContextMenuStrip?.Items[2] is Forms.ToolStripMenuItem item)
            item.Text = _host.Pause.IsPaused ? "Resume tracking" : "Pause tracking";
    }

    private void OpenCloudTak()
    {
        var url = _host.Config.CloudTakUrl
                  ?? _host.Config.Servers.Select(s => s.CloudTakUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(
                "CloudTAK URL is not configured. Set it under Settings → View the map.",
                "WinTAKTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open CloudTAK: {ex.Message}", "WinTAKTracker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_host.ConfigStore.LogsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _host.ConfigStore.LogsDirectory,
            UseShellExecute = true,
        });
    }

    private void Quit()
    {
        _notifyIcon.Visible = false;
        Application.Current.Shutdown();
    }

    private static Icon LoadAppIcon()
    {
        foreach (var fileName in new[] { "WinTAKTrackerLogo.ico", "tak.ico" })
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (File.Exists(icoPath))
                return new Icon(icoPath);
        }

        var asm = typeof(TrayIconService).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n =>
                n.EndsWith("WinTAKTrackerLogo.ico", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("tak.ico", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is not null)
                return new Icon(stream);
        }

        return SystemIcons.Application;
    }

    private static string TruncateTooltip(string text) =>
        text.Length <= 63 ? text : text[..63];

    public void Dispose()
    {
        _host.Pause.PauseChanged -= OnPauseChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _baseIcon.Dispose();
    }
}
