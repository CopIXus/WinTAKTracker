using System.Drawing;
using System.Windows;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Views;
using Forms = System.Windows.Forms;

namespace WinTAKTracker.Services.Tray;

/// <summary>
/// WinForms NotifyIcon host for the WPF tray experience.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly PauseService _pauseService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _baseIcon;
    private TrayIconState _state = TrayIconState.Disconnected;
    private SettingsWindow? _settingsWindow;

    public TrayIconService(PauseService pauseService)
    {
        _pauseService = pauseService;
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
        pauseItem.Click += (_, _) => TogglePause();
        menu.Items.Add(pauseItem);
        menu.Items.Add("Open CloudTAK", null, (_, _) => OpenCloudTakStub());
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

        _pauseService.PauseChanged += OnPauseChanged;
        ApplyState(_state);
        RefreshPauseMenuText();
    }

    public TrayIconState CurrentState => _state;

    public void SetState(TrayIconState state)
    {
        if (_pauseService.IsPaused && state != TrayIconState.Paused && state != TrayIconState.Error)
            state = TrayIconState.Paused;

        ApplyState(state);
    }

    public void ShowSettings()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(_pauseService, this);
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
        // Phase 1: same base icon; later phases add state overlays.
        _notifyIcon.Icon = _baseIcon;
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ApplyState(paused ? TrayIconState.Paused : TrayIconState.Disconnected);
            RefreshPauseMenuText();
        });
    }

    private void RefreshPauseMenuText()
    {
        if (_notifyIcon.ContextMenuStrip?.Items[2] is Forms.ToolStripMenuItem item)
            item.Text = _pauseService.IsPaused ? "Resume tracking" : "Pause tracking";
    }

    private void TogglePause() => _pauseService.Toggle();

    private static void OpenCloudTakStub()
    {
        System.Windows.MessageBox.Show(
            "CloudTAK URL is not configured yet. Set it under Settings → View the map in a later phase.",
            "WinTAKTracker",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OpenLogFolder()
    {
        var logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTAKTracker",
            "logs");
        Directory.CreateDirectory(logs);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = logs,
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
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tak.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath);

        // Embedded resource / content fallback — pack URI extract via stream if present
        var asm = typeof(TrayIconService).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tak.ico", StringComparison.OrdinalIgnoreCase));
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
        _pauseService.PauseChanged -= OnPauseChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _baseIcon.Dispose();
    }
}
