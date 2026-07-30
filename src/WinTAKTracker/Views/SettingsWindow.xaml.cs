using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;
using WinTAKTracker.Services.Update;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfSolidBrush = System.Windows.Media.SolidColorBrush;

namespace WinTAKTracker.Views;

public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private MapPreviewControl? _mapPreview;
    private UpdateCheckResult? _lastUpdateCheck;
    private readonly List<Action> _serverCardRefreshers = [];

    private bool CanEdit => _host.SettingsLock.IsUnlocked;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _host.Pause.PauseChanged += OnPauseChanged;
        _host.SettingsLock.LockStateChanged += OnLockStateChanged;
        Closed += (_, _) =>
        {
            _host.Pause.PauseChanged -= OnPauseChanged;
            _host.SettingsLock.LockStateChanged -= OnLockStateChanged;
            _refreshTimer.Stop();
        };

        _refreshTimer.Tick += (_, _) =>
        {
            if (SectionList.SelectedItem is not ListBoxItem { Tag: string tag }) return;
            if (tag == "Status") RefreshStatusLive();
            else if (tag == "Servers") RefreshServerCards();
        };
        _refreshTimer.Start();

        SectionList.SelectedIndex = 0;
        RefreshPauseButton();
        RefreshLockChrome();
        ShowSection("Status");
    }

    private void OnLockStateChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            RefreshLockChrome();
            if (SectionList.SelectedItem is ListBoxItem { Tag: string tag })
                ShowSection(tag);
        });

    private void RefreshLockChrome()
    {
        if (_host.SettingsLock.IsLocked)
        {
            LockSettingsButton.Content = "Unlock…";
            LockStatusText.Text = "Settings locked — view only";
        }
        else if (_host.SettingsLock.HasPassword)
        {
            LockSettingsButton.Content = "Lock settings";
            LockStatusText.Text = "Settings unlocked";
        }
        else
        {
            LockSettingsButton.Content = "Set lock…";
            LockStatusText.Text = "No lock password";
        }
    }

    private void LockSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_host.SettingsLock.IsLocked)
        {
            TryUnlockInteractive();
            return;
        }

        if (!_host.SettingsLock.HasPassword)
        {
            TryCreateLockPassword();
            return;
        }

        // Unlocked with password: offer lock / change
        var choice = MessageBox.Show(
            "Lock settings now?\n\nYes = Lock\nNo = Change lock password\nCancel = dismiss",
            "Settings lock",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Yes)
            _host.SettingsLock.Lock();
        else if (choice == MessageBoxResult.No)
            TryChangeLockPassword();
    }

    private void TryUnlockInteractive()
    {
        var pwd = PasswordPromptWindow.Prompt(this, "Unlock settings",
            "Enter the settings lock password to allow edits.");
        if (pwd is null) return;
        if (!_host.SettingsLock.TryUnlock(pwd))
            Msg("Incorrect password.", MessageBoxImage.Warning);
    }

    private void TryCreateLockPassword()
    {
        var pwd = PasswordPromptWindow.Prompt(this, "Set lock password",
            "Create a password to lock Settings edits. You can still view settings while locked.",
            requireConfirm: true);
        if (pwd is null) return;
        try
        {
            _host.SettingsLock.SetPassword(pwd);
            Msg("Lock password saved. Settings will start locked on the next launch.");
        }
        catch (Exception ex)
        {
            Msg(ex.Message, MessageBoxImage.Warning);
        }
    }

    private void TryChangeLockPassword()
    {
        var current = PasswordPromptWindow.Prompt(this, "Change lock password",
            "Enter your current settings lock password.");
        if (current is null) return;
        var next = PasswordPromptWindow.Prompt(this, "Change lock password",
            "Enter a new settings lock password.", requireConfirm: true);
        if (next is null) return;
        if (!_host.SettingsLock.ChangePassword(current, next))
            Msg("Incorrect current password.", MessageBoxImage.Warning);
        else
            Msg("Lock password updated.");
    }

    private void RefreshServerCards()
    {
        foreach (var refresh in _serverCardRefreshers.ToList())
        {
            try { refresh(); }
            catch { /* ignore stale UI */ }
        }
    }

    private void SectionList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is ListBoxItem item && item.Tag is string tag)
            ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        SectionContent.Children.Clear();
        SectionTitle.Text = tag switch
        {
            "Status" => "Status",
            "Servers" => "Servers",
            "Identity" => "Identity",
            "Gps" => "GPS",
            "Reporting" => "Reporting",
            "MeshSa" => "Mesh SA",
            "ViewMap" => "View the map",
            "Startup" => "Startup",
            "Diagnostics" => "Diagnostics",
            "Updates" => "Updates",
            "About" => "About",
            _ => tag,
        };

        SectionContent.Children.Add(tag switch
        {
            "Status" => BuildStatus(),
            "Servers" => BuildServers(),
            "Identity" => BuildIdentity(),
            "Gps" => BuildGps(),
            "Reporting" => BuildReporting(),
            "MeshSa" => BuildMeshSa(),
            "ViewMap" => BuildViewMap(),
            "Startup" => BuildStartup(),
            "Diagnostics" => BuildDiagnostics(),
            "Updates" => BuildUpdates(),
            "About" => BuildAbout(),
            _ => new TextBlock { Text = "Coming soon." },
        });
    }

    private StackPanel? _statusPanel;
    private readonly Dictionary<string, TextBlock> _statusValues = new(StringComparer.Ordinal);

    private UIElement BuildStatus()
    {
        _statusPanel = new StackPanel();
        _statusValues.Clear();

        var telemetry = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var key in new[] { "Mode", "Tray", "Tracking", "GPS", "Position", "Motion", "Servers", "Mesh SA", "Last PLI", "Active callsign" })
        {
            var row = StatusRow(key, "—");
            telemetry.Children.Add(row);
        }
        _statusPanel.Children.Add(telemetry);

        if (_host.Gps.WindowsPermission is GpsPermissionState.Denied or GpsPermissionState.NotAvailable)
        {
            _statusPanel.Children.Add(new Border
            {
                Background = ThemeBrush("DangerBgBrush", WpfBrushes.MistyRose),
                BorderBrush = ThemeBrush("AppBorderBrush", WpfBrushes.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "Windows Location is not available. Enable Settings → Privacy & security → Location (Location services + desktop apps), then request permission under GPS. Until then, only USB NMEA or approximate Network IP can be used.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("DangerBrush", WpfBrushes.DarkRed),
                },
            });
            _statusPanel.Children.Add(Btn("Open Windows Location privacy settings", () =>
                WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        }

        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        copyRow.Children.Add(Btn("Copy lat", () =>
        {
            var f = _host.Gps.CurrentFix;
            if (f is not null) Copy(f.Latitude.ToString("F6"));
        }));
        copyRow.Children.Add(Spacer(8));
        copyRow.Children.Add(Btn("Copy lon", () =>
        {
            var f = _host.Gps.CurrentFix;
            if (f is not null) Copy(f.Longitude.ToString("F6"));
        }));
        _statusPanel.Children.Add(copyRow);

        _mapPreview ??= new MapPreviewControl();
        if (_mapPreview.Parent is System.Windows.Controls.Panel oldParent)
            oldParent.Children.Remove(_mapPreview);

        _statusPanel.Children.Add(SectionHeader("Self map preview"));
        _statusPanel.Children.Add(new Border
        {
            BorderBrush = ThemeBrush("AppBorderBrush", WpfBrushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _mapPreview,
        });
        RefreshStatusLive();
        return _statusPanel;
    }

    private UIElement StatusRow(string key, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keyTb = new TextBlock { Text = key, Style = TryStyle("StatusKeyText") };
        var valueTb = new TextBlock { Text = value, Style = TryStyle("StatusValueText") };
        _statusValues[key] = valueTb;

        Grid.SetColumn(keyTb, 0);
        Grid.SetColumn(valueTb, 1);
        grid.Children.Add(keyTb);
        grid.Children.Add(valueTb);
        return grid;
    }

    private void SetStatus(string key, string value)
    {
        if (_statusValues.TryGetValue(key, out var tb))
            tb.Text = value;
    }

    private void RefreshStatusLive()
    {
        if (_statusValues.Count == 0) return;

        if (_host.AttachedToService)
        {
            _host.RefreshTray();
            var st = _host.LastServiceStatus;
            SetStatus("Mode", "Windows Service (companion UI)");
            SetStatus("Tray", _host.Tray.CurrentState.ToTooltip());
            SetStatus("Tracking", st?.Paused == true ? "Paused" : "Active");
            SetStatus("GPS", st?.HasGpsFix == true
                ? $"Fix via {st.GpsSource ?? "unknown"}"
                : "No fix");
            SetStatus("Position", "— (see service / map companions)");
            SetStatus("Motion", "—");
            SetStatus("Servers", st?.AnyTakConnected == true ? "Connected" :
                st?.AnyTakReconnecting == true ? "Reconnecting…" : "Disconnected");
            SetStatus("Mesh SA", st?.MeshReady == true ? "Ready" : (st?.MeshLastError ?? "—"));
            SetStatus("Last PLI", st?.LastPliSentUtc?.ToLocalTime().ToString("G") ?? "—");
            SetStatus("Active callsign", st?.ActiveIdentity is null
                ? "—"
                : $"{st.ActiveIdentity.Callsign} ({st.ActiveIdentity.Source})");
            return;
        }

        var fix = _host.Gps.CurrentFix;
        var servers = string.Join("; ", _host.Tak.GetStatuses().Select(s => $"{s.DisplayName}: {s.State}"));

        SetStatus("Mode", "In-process (portable)");
        SetStatus("Tray", _host.Tray.CurrentState.ToTooltip());
        SetStatus("Tracking", _host.Pause.IsPaused ? "Paused" : "Active");
        SetStatus("GPS", fix is null ? "No fix" : $"{(fix.IsHeld ? "Held" : "Live")} via {fix.SourceDisplayName}");
        SetStatus("Position", fix is null
            ? "—"
            : $"{fix.Latitude:F6}, {fix.Longitude:F6}");
        SetStatus("Motion", fix is null
            ? "—"
            : $"Speed {fix.SpeedMph:F1} mph · Course {(fix.CourseDegrees is double c ? $"{c:F0}°" : "—")} · Alt {(fix.AltitudeMeters is double a ? $"{a:F1} m" : "—")} · Acc {(fix.AccuracyMeters is double ac ? $"{ac:F0} m" : "—")}" +
              (fix.Source == GpsSourceKind.NetworkIp ? " · city/region scale" : ""));
        SetStatus("Servers", string.IsNullOrEmpty(servers) ? "None" : servers);
        SetStatus("Mesh SA", FormatMeshStatusLine());
        SetStatus("Last PLI", _host.Reporting.LastPliSentUtc?.ToLocalTime().ToString("G") ?? "—");
        var active = _host.Core.GetActiveIdentity();
        SetStatus("Active callsign", $"{active.Callsign} ({active.Source})");

        _mapPreview?.UpdateFix(fix, active.Team);
    }

    private UIElement BuildServers()
    {
        _serverCardRefreshers.Clear();
        var panel = new StackPanel();
        var edit = CanEdit;

        panel.Children.Add(SectionHeader("Server profiles"));

        if (_host.Config.Servers.Count == 0)
        {
            panel.Children.Add(Blurb("No server profiles yet. Add one below via enroll URL, SoftCert ZIP, QR, or manual .p12."));
        }
        else
        {
            foreach (var server in _host.Config.Servers.ToList())
                panel.Children.Add(BuildServerCard(server, edit));
        }

        panel.Children.Add(SectionHeader("Add server", topMargin: 8));
        panel.Children.Add(Blurb("Enroll via URL/QR, SoftCert ZIP, or manual .p12. Profiles stay in LocalAppData. Portal tokens are short-lived (~15 minutes)."));

        var paste = new TextBox
        {
            MinHeight = 64,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsEnabled = edit,
        };
        var enrollStatus = new TextBlock { Style = TryStyle("HelperText") };
        panel.Children.Add(Label("Paste enrollment URL or iTAK CSV"));
        panel.Children.Add(paste);
        panel.Children.Add(enrollStatus);

        async Task RunEnrollAsync(string input)
        {
            if (!EnsureEditable()) return;
            enrollStatus.Text = "Enrolling certificate…";
            enrollStatus.Foreground = ThemeBrush("AccentBrush", WpfBrushes.DarkSlateBlue);
            var progress = new Progress<string>(msg => { enrollStatus.Text = msg; });
            var result = await _host.Enrollment.ApplyAsync(input, _host.Config, progress, CancellationToken.None);
            if (!result.Success)
            {
                enrollStatus.Text = result.Error ?? "Enrollment failed.";
                enrollStatus.Foreground = ThemeBrush("DangerBrush", WpfBrushes.DarkRed);
                Msg(result.Error ?? "Failed", MessageBoxImage.Warning);
                return;
            }

            enrollStatus.Text = result.Message ?? "Enrolled.";
            enrollStatus.Foreground = ThemeBrush("AccentBrush", WpfBrushes.DarkGreen);
            await _host.ReloadConnectionsAsync();
            Msg(result.Message ?? "Applied.");
            ShowSection("Servers");
        }

        var addRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        addRow.Children.Add(Btn("Apply enrollment", async () => await RunEnrollAsync(paste.Text), edit, primary: true));
        addRow.Children.Add(Spacer(8));
        addRow.Children.Add(Btn("Scan QR…", async () =>
        {
            if (!EnsureEditable()) return;
            var dlg = new QrScanWindow { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ScannedText))
            {
                paste.Text = dlg.ScannedText;
                await RunEnrollAsync(dlg.ScannedText!);
            }
        }, edit));
        addRow.Children.Add(Spacer(8));
        addRow.Children.Add(Btn("Import SoftCert ZIP…", () =>
        {
            if (!EnsureEditable()) return;
            var ofd = new OpenFileDialog { Filter = "SoftCert ZIP (*.zip)|*.zip" };
            if (ofd.ShowDialog() != true) return;
            var result = _host.Enrollment.ImportSoftCertZip(ofd.FileName, _host.Config);
            if (!result.Success) { Msg(result.Error ?? "Failed"); return; }
            _ = _host.ReloadConnectionsAsync();
            Msg(result.Message ?? "Imported.");
            ShowSection("Servers");
        }, edit));
        addRow.Children.Add(Spacer(8));
        addRow.Children.Add(Btn("Manual .p12…", () =>
        {
            if (!EnsureEditable()) return;
            ShowManualImport();
        }, edit));
        panel.Children.Add(addRow);

        if (_host.Config.Servers.Count > 0)
        {
            panel.Children.Add(Btn("Forget all profiles", () =>
            {
                if (!EnsureEditable()) return;
                if (MessageBox.Show("Wipe ALL server profiles and certs?", "Forget all",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _host.Tak.WipeAll(_host.Config, _host.ConfigStore);
                ShowSection("Servers");
            }, edit, danger: true));
        }

        return panel;
    }

    private UIElement BuildServerCard(ServerProfile server, bool edit)
    {
        var outer = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var card = new Border
        {
            Style = TryStyle("ServerCard"),
            Margin = new Thickness(0, 0, 8, 0),
        };

        var body = new StackPanel();
        var titleRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var statusBadge = StatusBadge(TakConnectionState.Disconnected);
        DockPanel.SetDock(statusBadge, Dock.Right);
        titleRow.Children.Add(statusBadge);
        titleRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.DisplayName) ? server.Host : server.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = ThemeBrush("TextPrimaryBrush", new WpfSolidBrush(WpfColor.FromRgb(0x1A, 0x24, 0x20))),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(titleRow);

        var hostLine = string.IsNullOrWhiteSpace(server.Host)
            ? $"{server.Protocol.ToUpperInvariant()} · port {server.Port}"
            : $"{server.Host} · {server.Protocol.ToUpperInvariant()}:{server.Port}";
        body.Children.Add(new TextBlock
        {
            Text = hostLine,
            Foreground = ThemeBrush("TextSecondaryBrush", WpfBrushes.DimGray),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var meta = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrush("TextSecondaryBrush", new WpfSolidBrush(WpfColor.FromRgb(0x4A, 0x5C, 0x52))),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        body.Children.Add(meta);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var connectBtn = Btn(server.Enabled ? "Disconnect" : "Connect", () =>
        {
            if (!EnsureEditable()) return;
            server.Enabled = !server.Enabled;
            Persist();
            _ = _host.ReloadConnectionsAsync();
            ShowSection("Servers");
        }, edit, primary: true);
        connectBtn.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(connectBtn);
        actions.Children.Add(Btn("Test", async () =>
        {
            var (ok, message) = await _host.Tak.TestServerAsync(server.Id, _host.Config);
            Msg(message, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }, edit));
        body.Children.Add(actions);

        card.Child = body;
        Grid.SetColumn(card, 0);
        outer.Children.Add(card);

        var removeBtn = new Button
        {
            Content = "✕",
            Style = TryStyle("IconButton"),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Remove profile",
            IsEnabled = edit,
        };
        removeBtn.Click += (_, _) =>
        {
            if (!EnsureEditable()) return;
            if (MessageBox.Show(
                    $"Delete profile \"{server.DisplayName}\" and its certs from this PC?",
                    "Remove profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _host.Tak.WipeProfile(_host.Config, server.Id, _host.ConfigStore);
            ShowSection("Servers");
        };
        Grid.SetColumn(removeBtn, 1);
        outer.Children.Add(removeBtn);

        void Refresh()
        {
            var status = _host.Tak.GetStatuses().FirstOrDefault(s => s.ProfileId == server.Id);
            var state = status?.State ?? TakConnectionState.Disconnected;
            // Prefer Connected/Connecting/Error over Enabled disconnect intent for display.
            if (!server.Enabled && state is TakConnectionState.Disconnected or TakConnectionState.Error)
                state = TakConnectionState.Disconnected;
            ApplyStatusBadge(statusBadge, state, status?.LastErrorCode);

            var hasCert = !string.IsNullOrWhiteSpace(server.ClientCertFileName) &&
                          File.Exists(Path.Combine(_host.ConfigStore.CertsDirectory, server.ClientCertFileName!));
            var identity = server.CallsignOverride
                           ?? server.Username
                           ?? _host.Config.Identity.GetEffectiveCallsign();
            var certText = hasCert
                ? "Cert OK"
                : server.Protocol.Equals("ssl", StringComparison.OrdinalIgnoreCase)
                    ? "No client cert"
                    : "No cert (TCP)";
            meta.Text = $"Identity: {identity}  ·  {certText}";
            connectBtn.Content = server.Enabled ? "Disconnect" : "Connect";
        }

        Refresh();
        _serverCardRefreshers.Add(Refresh);
        return outer;
    }

    private static Border StatusBadge(TakConnectionState state)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        ApplyStatusBadge(border, state, null);
        return border;
    }

    private static void ApplyStatusBadge(Border badge, TakConnectionState state, string? error)
    {
        var (label, bg, fg) = state switch
        {
            TakConnectionState.Connected => ("Connected", WpfColor.FromRgb(0xE6, 0xF4, 0xEA), WpfColor.FromRgb(0x1B, 0x5E, 0x20)),
            TakConnectionState.Connecting => ("Connecting", WpfColor.FromRgb(0xE3, 0xF2, 0xFD), WpfColor.FromRgb(0x0D, 0x47, 0xA1)),
            TakConnectionState.Reconnecting => ("Connecting", WpfColor.FromRgb(0xE3, 0xF2, 0xFD), WpfColor.FromRgb(0x0D, 0x47, 0xA1)),
            TakConnectionState.Error => ("Error", WpfColor.FromRgb(0xFF, 0xEB, 0xEE), WpfColor.FromRgb(0xB7, 0x1C, 0x1C)),
            _ => ("Disconnected", WpfColor.FromRgb(0xEE, 0xEE, 0xEE), WpfColor.FromRgb(0x42, 0x42, 0x42)),
        };
        badge.Background = new WpfSolidBrush(bg);
        if (badge.Child is TextBlock tb)
        {
            tb.Text = label;
            tb.Foreground = new WpfSolidBrush(fg);
            if (state == TakConnectionState.Error && !string.IsNullOrWhiteSpace(error))
                badge.ToolTip = error;
            else
                badge.ToolTip = null;
        }
    }

    private bool EnsureEditable()
    {
        if (CanEdit) return true;
        Msg("Settings are locked. Unlock to make changes.", MessageBoxImage.Information);
        return false;
    }

    private static Border Spacer(double width) => new() { Width = width };

    private void ShowManualImport()
    {
        var dlg = new Window
        {
            Title = "Manual certificate import",
            Width = 480,
            Height = 440,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = TryStyle("AppWindow"),
            Background = ThemeBrush("ContentBgBrush", WpfBrushes.WhiteSmoke),
        };
        var sp = new StackPanel { Margin = new Thickness(20) };
        var host = new TextBox();
        var port = new TextBox { Text = "8089" };
        var proto = new ComboBox { ItemsSource = new[] { "ssl", "tcp" }, SelectedIndex = 0 };
        var clientPath = new TextBox();
        var trustPath = new TextBox();
        var clientPwd = new PasswordBox();
        var trustPwd = new PasswordBox();
        sp.Children.Add(Label("Host")); sp.Children.Add(host);
        sp.Children.Add(Label("Port")); sp.Children.Add(port);
        sp.Children.Add(Label("Protocol")); sp.Children.Add(proto);
        sp.Children.Add(Label("Client .p12")); sp.Children.Add(RowBrowse(clientPath, "Certificate (*.p12;*.pfx)|*.p12;*.pfx"));
        sp.Children.Add(Label("Client password")); sp.Children.Add(clientPwd);
        sp.Children.Add(Label("CA / trust (optional)")); sp.Children.Add(RowBrowse(trustPath, "Cert (*.p12;*.pfx;*.pem)|*.p12;*.pfx;*.pem"));
        sp.Children.Add(Label("Trust password (optional)")); sp.Children.Add(trustPwd);
        sp.Children.Add(Btn("Import", () =>
        {
            if (!int.TryParse(port.Text, out var p)) p = 8089;
            var result = _host.Enrollment.ImportManual(
                _host.Config, clientPath.Text, string.IsNullOrWhiteSpace(trustPath.Text) ? null : trustPath.Text,
                clientPwd.Password, string.IsNullOrEmpty(trustPwd.Password) ? null : trustPwd.Password,
                host.Text, p, proto.SelectedItem?.ToString() ?? "ssl");
            if (!result.Success) { Msg(result.Error ?? "Failed"); return; }
            _ = _host.ReloadConnectionsAsync();
            dlg.Close();
            ShowSection("Servers");
        }, primary: true));
        dlg.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dlg.ShowDialog();
    }

    private UIElement BuildIdentity()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        var teams = new[] { "Cyan", "Blue", "Green", "Yellow", "Orange", "Red", "Purple", "Magenta", "Maroon", "Teal", "White" };
        var roles = new[] { "Team Member", "Team Lead", "HQ", "Sniper", "Medic", "Forward Observer", "RTO", "K9" };
        var computer = _host.Config.ComputerIdentity;
        var sid = Services.Identity.IdentityResolver.CurrentUserSid();
        _host.Config.UserIdentities.TryGetValue(sid ?? "", out var user);

        panel.Children.Add(Blurb(
            "Computer callsign is used when nobody is logged on (Windows Service / logoff). " +
            "Your callsign is used while you are logged in. Defaults for computer callsign: this PC’s Windows name."));

        panel.Children.Add(SectionHeader("Computer callsign"));
        var computerCallsign = new TextBox { Text = computer.GetEffectiveCallsign(), IsEnabled = edit };
        var computerTeam = new ComboBox { IsEditable = true, ItemsSource = teams, Text = computer.Team, IsEnabled = edit };
        var computerRole = new ComboBox { IsEditable = true, ItemsSource = roles, Text = computer.Role, IsEnabled = edit };
        var computerCot = new ComboBox
        {
            ItemsSource = new[]
            {
                $"Ground Unit ({CotEventBuilder.GroundUnitType})",
                $"Vehicle ({CotEventBuilder.VehicleType})",
            },
            SelectedIndex = computer.CotType.Contains("E-V", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            IsEnabled = edit,
        };
        panel.Children.Add(Label("Computer callsign")); panel.Children.Add(computerCallsign);
        panel.Children.Add(Label("Computer team")); panel.Children.Add(computerTeam);
        panel.Children.Add(Label("Computer role")); panel.Children.Add(computerRole);
        panel.Children.Add(Label("Computer CoT type")); panel.Children.Add(computerCot);

        void SaveComputer()
        {
            if (!CanEdit) return;
            var next = computerCallsign.Text.Trim();
            if (string.IsNullOrWhiteSpace(next)) next = Environment.MachineName;
            computerCallsign.Text = next;
            var cotType = computerCot.SelectedIndex == 1
                ? CotEventBuilder.VehicleType
                : CotEventBuilder.GroundUnitType;
            _host.SaveComputerIdentity(next, computerTeam.Text.Trim(), computerRole.Text.Trim(), cotType);
        }

        BindPersistText(computerCallsign, SaveComputer);
        BindPersistText(computerTeam, SaveComputer);
        BindPersistText(computerRole, SaveComputer);
        computerCot.SelectionChanged += (_, _) => SaveComputer();

        panel.Children.Add(SectionHeader("My callsign (this Windows user)"));
        var myCallsign = new TextBox { Text = user?.Callsign ?? "", IsEnabled = edit };
        var myTeam = new ComboBox
        {
            IsEditable = true,
            ItemsSource = teams,
            Text = string.IsNullOrWhiteSpace(user?.Team) ? computer.Team : user!.Team,
            IsEnabled = edit,
        };
        var myRole = new ComboBox
        {
            IsEditable = true,
            ItemsSource = roles,
            Text = string.IsNullOrWhiteSpace(user?.Role) ? computer.Role : user!.Role,
            IsEnabled = edit,
        };
        panel.Children.Add(Label("My callsign")); panel.Children.Add(myCallsign);
        panel.Children.Add(Label("My team")); panel.Children.Add(myTeam);
        panel.Children.Add(Label("My role")); panel.Children.Add(myRole);
        panel.Children.Add(Blurb($"Windows user: {Services.Identity.IdentityResolver.CurrentUserName() ?? Environment.UserName}"));

        void SaveUser()
        {
            if (!CanEdit) return;
            var next = myCallsign.Text.Trim();
            if (string.IsNullOrWhiteSpace(next)) return;
            _host.SaveCurrentUserIdentity(
                next,
                myTeam.Text.Trim(),
                myRole.Text.Trim(),
                computer.CotType);
        }

        BindPersistText(myCallsign, SaveUser);
        BindPersistText(myTeam, SaveUser);
        BindPersistText(myRole, SaveUser);
        panel.Children.Add(Chip("Persisted", "Identity saves automatically when you change a field."));
        return panel;
    }

    private UIElement BuildGps()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        panel.Children.Add(Blurb(
            "Preferred order: USB NMEA (if you select a COM port) → Windows Location (Wi‑Fi/OS, same stack browsers use) → network/IP geolocation last (approximate, large CE via ipwho.is).\n\n" +
            "For accurate location without a GPS dongle: turn on Windows Location — Settings → Privacy & security → Location → Location services ON, and allow desktop apps to use your location. Then use Request Windows Location permission below."));
        var ports = _host.Gps.GetComPorts().ToList();
        ports.Insert(0, "(none — use Windows Location)");
        var selectedPort = string.IsNullOrWhiteSpace(_host.Config.Gps.ComPort)
            ? ports[0]
            : _host.Config.Gps.ComPort!;
        var port = new ComboBox { ItemsSource = ports, IsEditable = true, Text = selectedPort, IsEnabled = edit };
        var baud = new ComboBox
        {
            ItemsSource = new[] { "4800", "9600", "19200", "38400", "57600", "115200" },
            Text = _host.Config.Gps.BaudRate.ToString(),
            IsEnabled = edit,
        };
        var priority = new ComboBox
        {
            ItemsSource = new[] { "NmeaThenWindows", "WindowsThenNmea", "NmeaOnly", "WindowsOnly" },
            SelectedItem = _host.Config.Gps.SourcePriority,
            IsEnabled = edit,
        };
        var hold = new TextBox { Text = _host.Config.Gps.LastFixHoldSeconds.ToString(), IsEnabled = edit };
        var network = new CheckBox
        {
            Content = "Network / IP geolocation fallback (approximate)",
            IsChecked = _host.Config.Gps.EnableNetworkFallback,
            Margin = new Thickness(0, 8, 0, 8),
            IsEnabled = edit,
        };
        panel.Children.Add(Label("COM port")); panel.Children.Add(port);
        panel.Children.Add(Label("Baud")); panel.Children.Add(baud);
        panel.Children.Add(Label("Source priority")); panel.Children.Add(priority);
        panel.Children.Add(Label("Last-fix hold (seconds)")); panel.Children.Add(hold);
        panel.Children.Add(network);
        var permRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        permRow.Children.Add(Btn("Request Windows Location permission", async () =>
        {
            if (!EnsureEditable()) return;
            var state = await _host.Gps.RequestWindowsLocationAccessAsync();
            Msg(state == GpsPermissionState.Allowed
                ? "Windows Location allowed. If Status still shows Network IP, wait a few seconds for a Wi‑Fi fix or confirm Location services are on."
                : $"Permission: {state}. Open Windows Location privacy settings and enable Location + desktop apps.");
        }, edit));
        permRow.Children.Add(new Border { Width = 8 });
        permRow.Children.Add(Btn("Open Windows Location settings", () =>
            WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        panel.Children.Add(permRow);

        async Task SaveGpsAsync()
        {
            if (!CanEdit) return;
            _host.Config.Gps.ComPort = port.Text.StartsWith('(') ? null : port.Text;
            _host.Config.Gps.BaudRate = int.TryParse(baud.Text, out var b) ? b : 4800;
            _host.Config.Gps.SourcePriority = priority.SelectedItem?.ToString() ?? "NmeaThenWindows";
            _host.Config.Gps.LastFixHoldSeconds = int.TryParse(hold.Text, out var h) ? h : 30;
            _host.Config.Gps.EnableNetworkFallback = network.IsChecked == true;
            Persist();
            await _host.Gps.ApplySettingsAsync(_host.Config.Gps);
        }

        BindPersistText(port, () => _ = SaveGpsAsync());
        BindPersistText(baud, () => _ = SaveGpsAsync());
        BindPersistText(hold, () => _ = SaveGpsAsync());
        priority.SelectionChanged += (_, _) => _ = SaveGpsAsync();
        network.Checked += (_, _) => _ = SaveGpsAsync();
        network.Unchecked += (_, _) => _ = SaveGpsAsync();
        panel.Children.Add(Chip("Persisted", "GPS settings save automatically."));
        return panel;
    }

    private UIElement BuildReporting()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        panel.Children.Add(Blurb("ATAK-style Dynamic (default) or Constant rates. Reliable = servers; Unreliable = mesh. Changes auto-save."));
        var strategy = new ComboBox
        {
            ItemsSource = new[] { "Dynamic", "Constant" },
            SelectedItem = _host.Config.Reporting.Strategy,
            IsEnabled = edit,
        };
        var relStat = new TextBox { Text = _host.Config.Reporting.ReliableStationarySeconds.ToString(), IsEnabled = edit };
        var unrelStat = new TextBox { Text = _host.Config.Reporting.UnreliableStationarySeconds.ToString(), IsEnabled = edit };
        var constant = new TextBox { Text = _host.Config.Reporting.ConstantIntervalSeconds.ToString(), IsEnabled = edit };
        panel.Children.Add(Label("Strategy")); panel.Children.Add(strategy);
        panel.Children.Add(Label("Reliable stationary (s)")); panel.Children.Add(relStat);
        panel.Children.Add(Label("Unreliable stationary (s)")); panel.Children.Add(unrelStat);
        panel.Children.Add(Label("Constant interval (s)")); panel.Children.Add(constant);

        void SaveReporting()
        {
            if (!CanEdit) return;
            _host.Config.Reporting.Strategy = strategy.SelectedItem?.ToString() ?? "Dynamic";
            if (int.TryParse(relStat.Text, out var rs)) _host.Config.Reporting.ReliableStationarySeconds = rs;
            if (int.TryParse(unrelStat.Text, out var us)) _host.Config.Reporting.UnreliableStationarySeconds = us;
            if (int.TryParse(constant.Text, out var c)) _host.Config.Reporting.ConstantIntervalSeconds = c;
            Persist();
        }

        strategy.SelectionChanged += (_, _) => SaveReporting();
        BindPersistText(relStat, SaveReporting);
        BindPersistText(unrelStat, SaveReporting);
        BindPersistText(constant, SaveReporting);
        return panel;
    }

    private string FormatMeshStatusLine()
    {
        if (!_host.Config.MeshSa.Enabled) return "Off";
        var last = _host.Mesh.LastSendUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "no send yet";
        var iface = _host.Mesh.LastInterfaceDescription ?? "—";
        var line = $"On — last {last} via {iface}";
        if (_host.Mesh.LastErrorCode is not null)
            line += $" (error: {_host.Mesh.LastErrorCode})";
        else if (_host.Mesh.LastInterfaceWarning is not null)
            line += " ⚠ VPN/tunnel NIC — LAN ATAK may not see you";
        return line;
    }

    private UIElement BuildMeshSa()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        panel.Children.Add(Blurb(
            "UDP multicast Mesh SA (ATAK defaults 239.2.3.1:6969). Auto prefers Wi‑Fi/Ethernet and skips Tailscale/VPN tunnels. Changes auto-save."));
        var enabled = new CheckBox
        {
            Content = "Broadcast Mesh SA",
            IsChecked = _host.Config.MeshSa.Enabled,
            IsEnabled = edit,
        };
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Always", "OnlyWhenDisconnected" },
            SelectedItem = _host.Config.MeshSa.Mode,
            IsEnabled = edit,
        };
        var nics = MeshSaBroadcaster.ListInterfaces().Select(i => i.Name).ToList();
        nics.Insert(0, "Auto");
        var nic = new ComboBox
        {
            ItemsSource = nics,
            SelectedItem = _host.Config.MeshSa.NetworkInterface,
            IsEnabled = edit,
        };
        if (nic.SelectedItem is null) nic.SelectedIndex = 0;
        panel.Children.Add(enabled);
        panel.Children.Add(Label("Mode")); panel.Children.Add(mode);
        panel.Children.Add(Label("Network interface")); panel.Children.Add(nic);
        panel.Children.Add(Blurb(
            "Pick your Wi‑Fi or Ethernet adapter if Auto still shows a VPN name. Tailscale/Wintun does not carry ATAK Mesh multicast to LAN peers."));

        var statusChip = Chip("Status", MeshSaStatusText());
        panel.Children.Add(statusChip);
        if (_host.Mesh.LastInterfaceWarning is not null)
            panel.Children.Add(Blurb(_host.Mesh.LastInterfaceWarning));

        panel.Children.Add(Btn("Send test Mesh SA now", () =>
        {
            if (!EnsureEditable()) return;
            if (!_host.Config.MeshSa.Enabled)
            {
                MessageBox.Show("Enable Broadcast Mesh SA first.", "Mesh SA", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _host.Mesh.Rebind();
            _host.Reporting.RequestAsap();
            RefreshStatusLive();
            MessageBox.Show(
                $"Queued ASAP Mesh SA via {_host.Mesh.LastInterfaceDescription ?? "—"}.\n" +
                (_host.Mesh.LastInterfaceWarning ?? "Check ATAK on the same LAN for your callsign."),
                "Mesh SA",
                MessageBoxButton.OK,
                _host.Mesh.LastInterfaceWarning is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }, edit, primary: true));

        void SaveMesh()
        {
            if (!CanEdit) return;
            _host.Config.MeshSa.Enabled = enabled.IsChecked == true;
            _host.Config.MeshSa.Mode = mode.SelectedItem?.ToString() ?? "Always";
            _host.Config.MeshSa.NetworkInterface = nic.SelectedItem?.ToString() ?? "Auto";
            Persist();
            _host.Mesh.ApplySettings(_host.Config.MeshSa);
            if (statusChip.Child is TextBlock tb)
                tb.Text = $"Status: {MeshSaStatusText()}";
        }

        enabled.Checked += (_, _) => SaveMesh();
        enabled.Unchecked += (_, _) => SaveMesh();
        mode.SelectionChanged += (_, _) => SaveMesh();
        nic.SelectionChanged += (_, _) => SaveMesh();
        return panel;

        string MeshSaStatusText() =>
            $"Last send {_host.Mesh.LastSendUtc?.ToLocalTime():G} via {_host.Mesh.LastInterfaceDescription ?? "—"}" +
            (_host.Mesh.LastErrorCode is not null ? $" ({_host.Mesh.LastErrorCode})" : "");
    }

    private UIElement BuildViewMap()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        panel.Children.Add(Blurb(
            "This app reports your location to TAK. To see yourself and others on a map, use CloudTAK in a browser, ATAK on Android, TAK Aware on iOS, or WinTAK on Windows."));
        var url = new TextBox { Text = _host.Config.CloudTakUrl ?? "", IsEnabled = edit };
        panel.Children.Add(Label("CloudTAK URL"));
        panel.Children.Add(url);
        BindPersistText(url, () =>
        {
            if (!CanEdit) return;
            _host.Config.CloudTakUrl = string.IsNullOrWhiteSpace(url.Text) ? null : url.Text.Trim();
            Persist();
        });
        panel.Children.Add(Chip("Persisted", "CloudTAK URL saves when the field loses focus."));
        panel.Children.Add(Btn("Open CloudTAK", () =>
        {
            if (string.IsNullOrWhiteSpace(_host.Config.CloudTakUrl))
            {
                Msg("Configure a CloudTAK URL first.");
                return;
            }
            OpenUrl(_host.Config.CloudTakUrl!);
        }));
        panel.Children.Add(SectionHeader("Companion apps", topMargin: 8));
        panel.Children.Add(Btn("ATAK-CIV (Google Play)", () => OpenUrl("https://play.google.com/store/apps/details?id=com.atakmap.app.civ")));
        panel.Children.Add(Btn("ATAK / TAK.gov", () => OpenUrl("https://tak.gov")));
        panel.Children.Add(Btn("TAK Aware (App Store)", () => OpenUrl("https://apps.apple.com/us/app/tak-aware/id6738631659")));
        panel.Children.Add(Btn("WinTAK (tak.gov)", () => OpenUrl("https://tak.gov")));
        return panel;
    }

    private UIElement BuildStartup()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        var start = new CheckBox
        {
            Content = "Start with Windows",
            IsChecked = _host.Config.Startup.StartWithWindows,
            IsEnabled = edit,
        };
        var sleep = new CheckBox
        {
            Content = "Prevent sleep while tracking (uses more power)",
            IsChecked = _host.Config.Startup.PreventSleepWhileTracking,
            IsEnabled = edit,
        };
        panel.Children.Add(Blurb("PLI continues while the screen is locked. Prevent-sleep is optional and off by default. Changes auto-save."));
        panel.Children.Add(start);
        panel.Children.Add(sleep);

        void SaveStartup()
        {
            if (!CanEdit) return;
            _host.Config.Startup.StartWithWindows = start.IsChecked == true;
            _host.Config.Startup.PreventSleepWhileTracking = sleep.IsChecked == true;
            Persist();
        }

        start.Checked += (_, _) => SaveStartup();
        start.Unchecked += (_, _) => SaveStartup();
        sleep.Checked += (_, _) => SaveStartup();
        sleep.Unchecked += (_, _) => SaveStartup();
        return panel;
    }

    private UIElement BuildDiagnostics()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        var level = new ComboBox
        {
            ItemsSource = new[] { "Debug", "Information", "Warning", "Error" },
            SelectedItem = _host.Config.Diagnostics.LogLevel,
            IsEnabled = edit,
        };
        panel.Children.Add(Label("Log level")); panel.Children.Add(level);
        level.SelectionChanged += (_, _) =>
        {
            if (!CanEdit) return;
            _host.Config.Diagnostics.LogLevel = level.SelectedItem?.ToString() ?? "Information";
            if (Enum.TryParse<Services.Diagnostics.LogLevel>(_host.Config.Diagnostics.LogLevel, true, out var lv))
                _host.Log.SetMinLevel(lv);
            Persist();
        };
        panel.Children.Add(Btn("Open log folder", () =>
        {
            Directory.CreateDirectory(_host.ConfigStore.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = _host.ConfigStore.LogsDirectory, UseShellExecute = true });
        }));
        panel.Children.Add(Btn("Clear logs older than 14 days", () =>
        {
            if (!EnsureEditable()) return;
            _host.Log.ClearOldLogs(TimeSpan.FromDays(14));
            Msg("Old logs cleared.");
        }, edit));
        panel.Children.Add(Btn("Export redacted status…", () =>
        {
            var json = _host.StatusExporter.ExportJson(
                _host.Config, _host.Gps, _host.Tak, _host.Mesh, _host.Pause, _host.Tray.CurrentState);
            var sfd = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "wintaktracker-status.json" };
            if (sfd.ShowDialog() == true)
            {
                File.WriteAllText(sfd.FileName, json);
                Msg("Exported.");
            }
        }));
        return panel;
    }

    private UIElement BuildUpdates()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        var auto = new CheckBox
        {
            Content = "Automatically download and install updates",
            IsChecked = _host.Config.Updates.AutomaticallyDownloadAndInstall,
            IsEnabled = edit,
        };
        panel.Children.Add(Chip("Current version", _host.Updates.CurrentVersion));
        panel.Children.Add(Chip("Latest", _lastUpdateCheck?.LatestVersion ?? "(not checked)"));
        if (!string.IsNullOrWhiteSpace(_lastUpdateCheck?.ReleaseNotes))
        {
            panel.Children.Add(new TextBlock
            {
                Text = _lastUpdateCheck.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
        }
        panel.Children.Add(auto);
        auto.Checked += (_, _) =>
        {
            if (!CanEdit) return;
            _host.Config.Updates.AutomaticallyDownloadAndInstall = true;
            Persist();
        };
        auto.Unchecked += (_, _) =>
        {
            if (!CanEdit) return;
            _host.Config.Updates.AutomaticallyDownloadAndInstall = false;
            Persist();
        };
        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        updateRow.Children.Add(Btn("Check for updates", async () =>
        {
            _lastUpdateCheck = await _host.Updates.CheckAsync();
            if (!_lastUpdateCheck.Success)
                Msg(_lastUpdateCheck.Error ?? "Check failed.", MessageBoxImage.Warning);
            else if (_lastUpdateCheck.UpdateAvailable)
                Msg($"Update available: {_lastUpdateCheck.LatestVersion}");
            else if (!string.IsNullOrWhiteSpace(_lastUpdateCheck.Error))
                Msg(_lastUpdateCheck.Error, MessageBoxImage.Warning);
            else
                Msg($"You are up to date (current {_lastUpdateCheck.CurrentVersion}, latest {_lastUpdateCheck.LatestVersion ?? "unknown"}).");
            ShowSection("Updates");
        }));
        updateRow.Children.Add(Spacer(8));
        updateRow.Children.Add(Btn("Update now", async () =>
        {
            if (!EnsureEditable()) return;
            _lastUpdateCheck = await _host.Updates.CheckAsync();
            if (!_lastUpdateCheck.Success)
            {
                Msg(_lastUpdateCheck.Error ?? "Update check failed.", MessageBoxImage.Warning);
                ShowSection("Updates");
                return;
            }
            if (!_lastUpdateCheck.UpdateAvailable)
            {
                Msg(!string.IsNullOrWhiteSpace(_lastUpdateCheck.Error)
                    ? _lastUpdateCheck.Error
                    : $"No update available (current {_lastUpdateCheck.CurrentVersion}, latest {_lastUpdateCheck.LatestVersion ?? "unknown"}).");
                ShowSection("Updates");
                return;
            }

            var confirm = MessageBox.Show(
                $"Download and install version {_lastUpdateCheck.LatestVersion}?\n\n" +
                "WinTAKTracker will quit, replace the EXE, and relaunch. Your settings and certs in LocalAppData are kept.",
                "WinTAKTracker",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
                return;

            var (ok, message) = await _host.Updates.DownloadAndApplyAsync(_lastUpdateCheck);
            if (!ok)
            {
                Msg(message, MessageBoxImage.Warning);
                ShowSection("Updates");
                return;
            }

            // Helper is waiting on our PID — exit immediately (do not block on another MessageBox).
            Application.Current.Shutdown();
        }, edit, primary: true));
        panel.Children.Add(updateRow);
        return panel;
    }

    private UIElement BuildAbout()
    {
        var panel = new StackPanel();
        try
        {
            var logo = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/WinTAKTrackerLogo.png")),
                Width = 96,
                Height = 96,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = WpfHAlign.Left,
                Margin = new Thickness(0, 0, 0, 12),
            };
            panel.Children.Add(logo);
        }
        catch { /* branding optional */ }

        var version = _host.Updates.CurrentVersion;
        panel.Children.Add(new TextBlock
        {
            Text = $"WinTAKTracker {version}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextPrimaryBrush", WpfBrushes.Black),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(Blurb(
            "Independent Windows PLI tracker. Not an official TAK Product Center application.\n\n" +
            "TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners.\n" +
            "Map tiles: © OpenStreetMap contributors.\n" +
            "Network location: ipwho.is (approximate IP geolocation).\n" +
            "License: see LICENSE in the repository.\n" +
            "Updates: github.com/CopIXus/WinTAKTracker"));
        return panel;
    }

    private void Persist() => _host.SaveConfig();

    private static void BindPersistText(System.Windows.Controls.Control control, Action save)
    {
        control.LostFocus += (_, _) => save();
        if (control is ComboBox combo)
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.IsDropDownOpen) return;
                save();
            };
    }

    private static DockPanel RowBrowse(TextBox pathBox, string filter)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        var btn = new Button
        {
            Content = "…",
            Style = TryStyle("IconButton"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) =>
        {
            var ofd = new OpenFileDialog { Filter = filter };
            if (ofd.ShowDialog() == true) pathBox.Text = ofd.FileName;
        };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(pathBox);
        return dock;
    }

    private static TextBlock SectionHeader(string text, double topMargin = 0) => new()
    {
        Text = text,
        Style = TryStyle("SectionHeaderText"),
        Margin = new Thickness(0, topMargin > 0 ? topMargin + 12 : 0, 0, 8),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Style = TryStyle("FieldLabelText"),
    };

    private static TextBlock Blurb(string text) => new()
    {
        Text = text,
        Style = TryStyle("BlurbText"),
    };

    private static Border Chip(string label, string value)
    {
        var border = new Border { Style = TryStyle("InfoChip") };
        border.Child = new TextBlock
        {
            Text = $"{label}: {value}",
            FontSize = 13,
            Foreground = ThemeBrush("TextPrimaryBrush", WpfBrushes.Black),
        };
        return border;
    }

    private static Button Btn(string content, Action action, bool enabled = true, bool primary = false, bool danger = false)
    {
        var b = CreateButton(content, enabled, primary, danger);
        b.Click += (_, _) => action();
        return b;
    }

    private static Button Btn(string content, Func<Task> action, bool enabled = true, bool primary = false, bool danger = false)
    {
        var b = CreateButton(content, enabled, primary, danger);
        b.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Msg(ex.Message, MessageBoxImage.Warning); }
        };
        return b;
    }

    private static Button CreateButton(string content, bool enabled, bool primary, bool danger)
    {
        var styleKey = primary ? "PrimaryButton" : danger ? "DangerButton" : "SecondaryButton";
        return new Button
        {
            Content = content,
            Style = TryStyle(styleKey),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = WpfHAlign.Left,
            IsEnabled = enabled,
        };
    }

    private static Style? TryStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* ignore */ }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    private static void Msg(string text, MessageBoxImage icon = MessageBoxImage.Information) =>
        MessageBox.Show(text, "WinTAKTracker", MessageBoxButton.OK, icon);

    private void PauseResumeButton_OnClick(object sender, RoutedEventArgs e) => _host.Pause.Toggle();
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
    private void OnPauseChanged(object? sender, bool paused) => Dispatcher.Invoke(RefreshPauseButton);
    private void RefreshPauseButton() =>
        PauseResumeButton.Content = _host.Pause.IsPaused ? "Resume tracking" : "Pause tracking";
}
