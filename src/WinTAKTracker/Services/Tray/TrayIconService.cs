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
    private Icon? _composedIcon;
    private TrayIconState _state = TrayIconState.Disconnected;
    private SettingsWindow? _settingsWindow;
    private VideoConsoleWindow? _videoConsole;

    public TrayIconService(AppHost host)
    {
        _host = host;
        _baseIcon = LoadAppIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Visible = true,
            Text = TruncateTooltip(BuildTooltip(_state)),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show settings", null, (_, _) => ShowSettings());
        var videoItem = new Forms.ToolStripMenuItem("Video Console…");
        videoItem.Click += (_, _) => ShowVideoConsole();
        menu.Items.Add(videoItem);
        var lockItem = new Forms.ToolStripMenuItem("Unlock settings…");
        lockItem.Click += (_, _) => ToggleSettingsLockFromTray();
        menu.Items.Add(lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var pauseItem = new Forms.ToolStripMenuItem("Pause tracking");
        pauseItem.Click += (_, _) => _host.Pause.Toggle();
        menu.Items.Add(pauseItem);
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _notifyIcon.ContextMenuStrip = menu;
        _host.SettingsLock.LockStateChanged += (_, _) =>
            Application.Current.Dispatcher.Invoke(RefreshLockMenuText);
        RefreshLockMenuText();

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

    /// <summary>Rebuild NotifyIcon.Text from current tray state, version, and last update check.</summary>
    public void RefreshTooltip() => ApplyState(_state);

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

    public void ShowVideoConsole()
    {
        if (_host.Config.Video.Enabled != true)
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_videoConsole is null)
            {
                _videoConsole = new VideoConsoleWindow(_host);
                _videoConsole.Closed += (_, _) => _videoConsole = null;
            }

            _videoConsole.Show();
            _videoConsole.Activate();
            if (_videoConsole.WindowState == WindowState.Minimized)
                _videoConsole.WindowState = WindowState.Normal;
        });
    }

    public void CloseVideoConsole()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_videoConsole is null) return;
            try { _videoConsole.Close(); }
            catch { /* ignore */ }
            _videoConsole = null;
        });
    }

    private void ApplyState(TrayIconState state)
    {
        _state = state;
        _notifyIcon.Text = TruncateTooltip(BuildTooltip(state));
        _notifyIcon.Icon = ComposeTrayIcon();
        RefreshVideoMenuEnabled();
    }

    private string BuildTooltip(TrayIconState state)
    {
        var version = _host.Updates.CurrentVersion;
        var updateVersion = ResolveUpdateVersionHint();
        var tip = state.ToTooltip(version, updateVersion);
        var video = _host.Config.Video;
        var liveCount = _host.Video?.LiveCount ?? 0;
        if (video is { Enabled: true } v && (v.IsConfigured || liveCount > 0))
        {
            var cams = v.Feeds?.Count(f => f.Enabled) ?? 0;
            tip += liveCount > 0 ? $" | Video: LIVE ×{liveCount}" : $" | Video: idle ({cams} cams)";
        }

        return tip;
    }

    private Icon ComposeTrayIcon()
    {
        _composedIcon?.Dispose();
        _composedIcon = null;
        var video = _host.Config.Video;
        var liveCount = _host.Video?.LiveCount ?? 0;
        var configured = video is { Enabled: true } && (video.IsConfigured || liveCount > 0);
        if (!configured) return _baseIcon;

        try
        {
            using var bmp = _baseIcon.ToBitmap();
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var live = liveCount > 0;
            var accent = live ? Color.FromArgb(220, 60, 60) : Color.FromArgb(40, 180, 200);
            // Small camera body in bottom-right.
            var x = bmp.Width - 10;
            var y = bmp.Height - 9;
            using var brush = new SolidBrush(accent);
            using var pen = new Pen(Color.White, 1f);
            g.FillRectangle(brush, x, y + 2, 8, 5);
            g.FillEllipse(brush, x + 2, y, 4, 4);
            g.DrawRectangle(pen, x, y + 2, 8, 5);
            _composedIcon = Icon.FromHandle(bmp.GetHicon());
            return _composedIcon;
        }
        catch
        {
            return _baseIcon;
        }
    }

    private void RefreshVideoMenuEnabled()
    {
        if (_notifyIcon.ContextMenuStrip?.Items.Count > 1 &&
            _notifyIcon.ContextMenuStrip.Items[1] is Forms.ToolStripMenuItem videoItem)
            videoItem.Enabled = _host.Config.Video?.Enabled == true;
    }

    private string? ResolveUpdateVersionHint()
    {
        if (_host.LastUpdateCheck is { Success: true, UpdateAvailable: true } check &&
            !string.IsNullOrWhiteSpace(check.LatestVersion))
            return check.LatestVersion;

        var fromConfig = _host.Config.Updates.LastAvailableVersion;
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
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
        // 0 Settings, 1 Video, 2 Lock, 3 sep, 4 Pause
        if (_notifyIcon.ContextMenuStrip?.Items.Count > 4 &&
            _notifyIcon.ContextMenuStrip.Items[4] is Forms.ToolStripMenuItem item)
            item.Text = _host.Pause.IsPaused ? "Resume tracking" : "Pause tracking";
    }

    private void RefreshLockMenuText()
    {
        if (_notifyIcon.ContextMenuStrip is null ||
            _notifyIcon.ContextMenuStrip.Items.Count <= 2 ||
            _notifyIcon.ContextMenuStrip.Items[2] is not Forms.ToolStripMenuItem item)
            return;

        if (!_host.SettingsLock.HasPassword)
            item.Text = "Set settings lock…";
        else if (_host.SettingsLock.IsLocked)
            item.Text = "Unlock settings…";
        else
            item.Text = "Lock settings";
    }

    private void ToggleSettingsLockFromTray()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_host.SettingsLock.HasPassword)
            {
                var created = PasswordPromptWindow.Prompt(
                    _settingsWindow,
                    "Set lock password",
                    "Create a password to lock Settings edits. You can still view settings while locked.",
                    requireConfirm: true);
                if (created is null) return;
                try
                {
                    _host.SettingsLock.SetPassword(created);
                    MessageBox.Show("Settings lock password saved. Settings will start locked next launch.",
                        "WinTAKTracker", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "WinTAKTracker", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (_host.SettingsLock.IsLocked)
            {
                var pwd = PasswordPromptWindow.Prompt(
                    _settingsWindow,
                    "Unlock settings",
                    "Enter the settings lock password to allow edits.");
                if (pwd is null) return;
                if (!_host.SettingsLock.TryUnlock(pwd))
                {
                    MessageBox.Show("Incorrect password.", "WinTAKTracker",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _ = _host.UnlockServiceSettingsAsync(pwd);
                return;
            }

            _host.SettingsLock.Lock();
            _ = _host.LockServiceSettingsAsync();
        });
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
        // Prefer WPF Resource (works for PublishSingleFile; Content files are not beside the EXE).
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/WinTAKTrackerLogo.ico");
            var info = Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    return new Icon(ms);
                }
            }
        }
        catch
        {
            /* fall through */
        }

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
        _composedIcon?.Dispose();
        _baseIcon.Dispose();
    }
}
