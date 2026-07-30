using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Tak;
using WinTAKTracker.Services.Tray;
using WinTAKTracker.Services.Update;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfHAlign = System.Windows.HorizontalAlignment;

namespace WinTAKTracker.Views;

public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private MapPreviewControl? _mapPreview;
    private UpdateCheckResult? _lastUpdateCheck;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _host.Pause.PauseChanged += OnPauseChanged;
        Closed += (_, _) =>
        {
            _host.Pause.PauseChanged -= OnPauseChanged;
            _refreshTimer.Stop();
        };

        _refreshTimer.Tick += (_, _) =>
        {
            if (SectionList.SelectedItem is ListBoxItem { Tag: "Status" })
                RefreshStatusLive();
        };
        _refreshTimer.Start();

        SectionList.SelectedIndex = 0;
        RefreshPauseButton();
        ShowSection("Status");
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
    private TextBlock? _statusSummary;

    private UIElement BuildStatus()
    {
        _statusPanel = new StackPanel();
        _statusSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        _statusPanel.Children.Add(_statusSummary);

        if (_host.Gps.WindowsPermission is GpsPermissionState.Denied or GpsPermissionState.NotAvailable)
        {
            _statusPanel.Children.Add(new TextBlock
            {
                Text = "Windows Location is not available. Enable Settings → Privacy & security → Location (Location services + desktop apps), then request permission under GPS. Until then, only USB NMEA or approximate Network IP can be used.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DarkRed,
                Margin = new Thickness(0, 4, 0, 8),
            });
            _statusPanel.Children.Add(Btn("Open Windows Location privacy settings", () =>
                WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        }

        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        copyRow.Children.Add(Btn("Copy lat", () =>
        {
            var f = _host.Gps.CurrentFix;
            if (f is not null) Copy(f.Latitude.ToString("F6"));
        }));
        copyRow.Children.Add(new Border { Width = 8 });
        copyRow.Children.Add(Btn("Copy lon", () =>
        {
            var f = _host.Gps.CurrentFix;
            if (f is not null) Copy(f.Longitude.ToString("F6"));
        }));
        _statusPanel.Children.Add(copyRow);

        _mapPreview ??= new MapPreviewControl();
        if (_mapPreview.Parent is System.Windows.Controls.Panel oldParent)
            oldParent.Children.Remove(_mapPreview);
        _statusPanel.Children.Add(new TextBlock { Text = "Self map preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        _statusPanel.Children.Add(_mapPreview);
        RefreshStatusLive();
        return _statusPanel;
    }

    private void RefreshStatusLive()
    {
        if (_statusSummary is null) return;
        var fix = _host.Gps.CurrentFix;
        var servers = string.Join("; ", _host.Tak.GetStatuses().Select(s => $"{s.DisplayName}:{s.State}"));
        _statusSummary.Text =
            $"Tray: {_host.Tray.CurrentState.ToTooltip()}\n" +
            $"Tracking: {(_host.Pause.IsPaused ? "Paused" : "Active")}\n" +
            $"GPS: {(fix is null ? "No fix" : $"{(fix.IsHeld ? "Held" : "Live")} via {fix.SourceDisplayName}")}\n" +
            (fix is null ? "" :
                $"Lat: {fix.Latitude:F6}  Lon: {fix.Longitude:F6}\n" +
                $"Speed: {fix.SpeedMph:F1} mph  Course: {(fix.CourseDegrees is double c ? $"{c:F0}°" : "—")}  " +
                $"Alt: {(fix.AltitudeMeters is double a ? $"{a:F1} m" : "—")}  " +
                $"Acc: {(fix.AccuracyMeters is double ac ? $"{ac:F0} m" : "—")}" +
                (fix.Source == GpsSourceKind.NetworkIp ? " — city/region scale, not Wi‑Fi" : "") + "\n") +
            $"Servers: {(string.IsNullOrEmpty(servers) ? "None" : servers)}\n" +
            $"Mesh SA: {FormatMeshStatusLine()}\n" +
            $"Last PLI: {_host.Reporting.LastPliSentUtc?.ToLocalTime().ToString("G") ?? "—"}";

        _mapPreview?.UpdateFix(fix, _host.Config.Identity.Team);
    }

    private UIElement BuildServers()
    {
        var panel = new StackPanel();
        panel.Children.Add(Blurb("Enroll via URL/QR, SoftCert ZIP, or manual .p12. Profiles stay in LocalAppData."));

        var paste = new TextBox { Height = 56, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Label("Paste enrollment URL or iTAK CSV"));
        panel.Children.Add(paste);
        panel.Children.Add(Btn("Apply enrollment", async () =>
        {
            var result = await _host.Enrollment.ApplyAsync(paste.Text, _host.Config);
            if (!result.Success) { Msg(result.Error ?? "Failed"); return; }
            await _host.ReloadConnectionsAsync();
            Msg(result.Message ?? "Applied.");
            ShowSection("Servers");
        }));
        panel.Children.Add(Btn("Scan QR…", async () =>
        {
            var dlg = new QrScanWindow { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ScannedText))
            {
                paste.Text = dlg.ScannedText;
                var result = await _host.Enrollment.ApplyAsync(dlg.ScannedText!, _host.Config);
                if (!result.Success) { Msg(result.Error ?? "Failed"); return; }
                await _host.ReloadConnectionsAsync();
                Msg(result.Message ?? "Enrolled from QR.");
                ShowSection("Servers");
            }
        }));
        panel.Children.Add(Btn("Import SoftCert ZIP…", () =>
        {
            var ofd = new OpenFileDialog { Filter = "SoftCert ZIP (*.zip)|*.zip" };
            if (ofd.ShowDialog() != true) return;
            var result = _host.Enrollment.ImportSoftCertZip(ofd.FileName, _host.Config);
            if (!result.Success) { Msg(result.Error ?? "Failed"); return; }
            _ = _host.ReloadConnectionsAsync();
            Msg(result.Message ?? "Imported.");
            ShowSection("Servers");
        }));
        panel.Children.Add(Btn("Manual .p12 import…", () => ShowManualImport(panel)));

        panel.Children.Add(new TextBlock { Text = "Profiles", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
        foreach (var server in _host.Config.Servers.ToList())
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var status = _host.Tak.GetStatuses().FirstOrDefault(s => s.ProfileId == server.Id);
            box.Children.Add(new TextBlock
            {
                Text = $"{server.DisplayName} — {server.Protocol}:{server.Port} — {status?.State.ToString() ?? "—"}",
                FontWeight = FontWeights.SemiBold,
            });
            var en = new CheckBox { Content = "Enabled", IsChecked = server.Enabled };
            en.Checked += (_, _) => { server.Enabled = true; Persist(); _ = _host.ReloadConnectionsAsync(); };
            en.Unchecked += (_, _) => { server.Enabled = false; Persist(); _ = _host.ReloadConnectionsAsync(); };
            box.Children.Add(en);
            box.Children.Add(Btn("Test server", async () =>
            {
                var (ok, message) = await _host.Tak.TestServerAsync(server.Id, _host.Config);
                Msg(message, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }));
            box.Children.Add(Btn("Remove / wipe profile", () =>
            {
                if (MessageBox.Show("Delete this server profile and its certs from disk?", "Wipe profile",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _host.Tak.WipeProfile(_host.Config, server.Id, _host.ConfigStore);
                ShowSection("Servers");
            }));
            panel.Children.Add(box);
        }

        panel.Children.Add(Btn("Forget all profiles", () =>
        {
            if (MessageBox.Show("Wipe ALL server profiles and certs?", "Forget all",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _host.Tak.WipeAll(_host.Config, _host.ConfigStore);
            ShowSection("Servers");
        }));

        return panel;
    }

    private void ShowManualImport(StackPanel hostPanel)
    {
        var dlg = new Window
        {
            Title = "Manual certificate import",
            Width = 480,
            Height = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
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
        }));
        dlg.Content = new ScrollViewer { Content = sp };
        dlg.ShowDialog();
    }

    private UIElement BuildIdentity()
    {
        var panel = new StackPanel();
        panel.Children.Add(Blurb("Manual identity — changes auto-save and trigger ASAP re-report. Default callsign is this PC’s Windows name until you change it."));
        var callsign = new TextBox { Text = _host.Config.Identity.GetEffectiveCallsign() };
        var team = new ComboBox
        {
            IsEditable = true,
            ItemsSource = new[] { "Cyan", "Blue", "Green", "Yellow", "Orange", "Red", "Purple", "Magenta", "Maroon", "Teal", "White" },
            Text = _host.Config.Identity.Team,
        };
        var role = new ComboBox
        {
            IsEditable = true,
            ItemsSource = new[] { "Team Member", "Team Lead", "HQ", "Sniper", "Medic", "Forward Observer", "RTO", "K9" },
            Text = _host.Config.Identity.Role,
        };
        var cot = new ComboBox
        {
            ItemsSource = new[]
            {
                $"Ground Unit ({CotEventBuilder.GroundUnitType})",
                $"Vehicle ({CotEventBuilder.VehicleType})",
            },
            SelectedIndex = _host.Config.Identity.CotType.Contains("E-V", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        };
        panel.Children.Add(Label("Callsign")); panel.Children.Add(callsign);
        panel.Children.Add(Label("Team")); panel.Children.Add(team);
        panel.Children.Add(Label("Role")); panel.Children.Add(role);
        panel.Children.Add(Label("Device / CoT type")); panel.Children.Add(cot);

        void SaveIdentity()
        {
            var next = callsign.Text.Trim();
            _host.Config.Identity.Callsign = string.IsNullOrWhiteSpace(next)
                ? Environment.MachineName
                : next;
            callsign.Text = _host.Config.Identity.Callsign;
            _host.Config.Identity.Team = team.Text.Trim();
            _host.Config.Identity.Role = role.Text.Trim();
            _host.Config.Identity.CotType = cot.SelectedIndex == 1
                ? CotEventBuilder.VehicleType
                : CotEventBuilder.GroundUnitType;
            Persist();
            _host.Reporting.NotifyIdentityChanged();
        }

        BindPersistText(callsign, SaveIdentity);
        BindPersistText(team, SaveIdentity);
        BindPersistText(role, SaveIdentity);
        cot.SelectionChanged += (_, _) => SaveIdentity();
        panel.Children.Add(Chip("Persisted", "Identity saves automatically when you change a field."));
        return panel;
    }

    private UIElement BuildGps()
    {
        var panel = new StackPanel();
        panel.Children.Add(Blurb(
            "Preferred order: USB NMEA (if you select a COM port) → Windows Location (Wi‑Fi/OS, same stack browsers use) → network/IP geolocation last (approximate, large CE via ipwho.is).\n\n" +
            "For accurate location without a GPS dongle: turn on Windows Location — Settings → Privacy & security → Location → Location services ON, and allow desktop apps to use your location. Then use Request Windows Location permission below."));
        var ports = _host.Gps.GetComPorts().ToList();
        ports.Insert(0, "(none — use Windows Location)");
        var selectedPort = string.IsNullOrWhiteSpace(_host.Config.Gps.ComPort)
            ? ports[0]
            : _host.Config.Gps.ComPort!;
        var port = new ComboBox { ItemsSource = ports, IsEditable = true, Text = selectedPort };
        var baud = new ComboBox
        {
            ItemsSource = new[] { "4800", "9600", "19200", "38400", "57600", "115200" },
            Text = _host.Config.Gps.BaudRate.ToString(),
        };
        var priority = new ComboBox
        {
            ItemsSource = new[] { "NmeaThenWindows", "WindowsThenNmea", "NmeaOnly", "WindowsOnly" },
            SelectedItem = _host.Config.Gps.SourcePriority,
        };
        var hold = new TextBox { Text = _host.Config.Gps.LastFixHoldSeconds.ToString() };
        var network = new CheckBox
        {
            Content = "Network / IP geolocation fallback (approximate)",
            IsChecked = _host.Config.Gps.EnableNetworkFallback,
            Margin = new Thickness(0, 8, 0, 8),
        };
        panel.Children.Add(Label("COM port")); panel.Children.Add(port);
        panel.Children.Add(Label("Baud")); panel.Children.Add(baud);
        panel.Children.Add(Label("Source priority")); panel.Children.Add(priority);
        panel.Children.Add(Label("Last-fix hold (seconds)")); panel.Children.Add(hold);
        panel.Children.Add(network);
        var permRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        permRow.Children.Add(Btn("Request Windows Location permission", async () =>
        {
            var state = await _host.Gps.RequestWindowsLocationAccessAsync();
            Msg(state == GpsPermissionState.Allowed
                ? "Windows Location allowed. If Status still shows Network IP, wait a few seconds for a Wi‑Fi fix or confirm Location services are on."
                : $"Permission: {state}. Open Windows Location privacy settings and enable Location + desktop apps.");
        }));
        permRow.Children.Add(new Border { Width = 8 });
        permRow.Children.Add(Btn("Open Windows Location settings", () =>
            WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        panel.Children.Add(permRow);

        async Task SaveGpsAsync()
        {
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
        panel.Children.Add(Blurb("ATAK-style Dynamic (default) or Constant rates. Reliable = servers; Unreliable = mesh. Changes auto-save."));
        var strategy = new ComboBox { ItemsSource = new[] { "Dynamic", "Constant" }, SelectedItem = _host.Config.Reporting.Strategy };
        var relStat = new TextBox { Text = _host.Config.Reporting.ReliableStationarySeconds.ToString() };
        var unrelStat = new TextBox { Text = _host.Config.Reporting.UnreliableStationarySeconds.ToString() };
        var constant = new TextBox { Text = _host.Config.Reporting.ConstantIntervalSeconds.ToString() };
        panel.Children.Add(Label("Strategy")); panel.Children.Add(strategy);
        panel.Children.Add(Label("Reliable stationary (s)")); panel.Children.Add(relStat);
        panel.Children.Add(Label("Unreliable stationary (s)")); panel.Children.Add(unrelStat);
        panel.Children.Add(Label("Constant interval (s)")); panel.Children.Add(constant);

        void SaveReporting()
        {
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
        panel.Children.Add(Blurb(
            "UDP multicast Mesh SA (ATAK defaults 239.2.3.1:6969). Auto prefers Wi‑Fi/Ethernet and skips Tailscale/VPN tunnels. Changes auto-save."));
        var enabled = new CheckBox { Content = "Broadcast Mesh SA", IsChecked = _host.Config.MeshSa.Enabled };
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Always", "OnlyWhenDisconnected" },
            SelectedItem = _host.Config.MeshSa.Mode,
        };
        var nics = MeshSaBroadcaster.ListInterfaces().Select(i => i.Name).ToList();
        nics.Insert(0, "Auto");
        var nic = new ComboBox { ItemsSource = nics, SelectedItem = _host.Config.MeshSa.NetworkInterface };
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
        }));

        void SaveMesh()
        {
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
        panel.Children.Add(Blurb(
            "This app reports your location to TAK. To see yourself and others on a map, use CloudTAK in a browser, ATAK on Android, TAK Aware on iOS, or WinTAK on Windows."));
        var url = new TextBox { Text = _host.Config.CloudTakUrl ?? "" };
        panel.Children.Add(Label("CloudTAK URL"));
        panel.Children.Add(url);
        BindPersistText(url, () =>
        {
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
        panel.Children.Add(new TextBlock { Text = "Companion apps", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
        panel.Children.Add(Btn("ATAK-CIV (Google Play)", () => OpenUrl("https://play.google.com/store/apps/details?id=com.atakmap.app.civ")));
        panel.Children.Add(Btn("ATAK / TAK.gov", () => OpenUrl("https://tak.gov")));
        panel.Children.Add(Btn("TAK Aware (App Store)", () => OpenUrl("https://apps.apple.com/us/app/tak-aware/id6738631659")));
        panel.Children.Add(Btn("WinTAK (tak.gov)", () => OpenUrl("https://tak.gov")));
        return panel;
    }

    private UIElement BuildStartup()
    {
        var panel = new StackPanel();
        var start = new CheckBox { Content = "Start with Windows", IsChecked = _host.Config.Startup.StartWithWindows };
        var sleep = new CheckBox
        {
            Content = "Prevent sleep while tracking (uses more power)",
            IsChecked = _host.Config.Startup.PreventSleepWhileTracking,
        };
        panel.Children.Add(Blurb("PLI continues while the screen is locked. Prevent-sleep is optional and off by default. Changes auto-save."));
        panel.Children.Add(start);
        panel.Children.Add(sleep);

        void SaveStartup()
        {
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
        var level = new ComboBox
        {
            ItemsSource = new[] { "Debug", "Information", "Warning", "Error" },
            SelectedItem = _host.Config.Diagnostics.LogLevel,
        };
        panel.Children.Add(Label("Log level")); panel.Children.Add(level);
        level.SelectionChanged += (_, _) =>
        {
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
            _host.Log.ClearOldLogs(TimeSpan.FromDays(14));
            Msg("Old logs cleared.");
        }));
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
        var auto = new CheckBox
        {
            Content = "Automatically download and install updates",
            IsChecked = _host.Config.Updates.AutomaticallyDownloadAndInstall,
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
            _host.Config.Updates.AutomaticallyDownloadAndInstall = true;
            Persist();
        };
        auto.Unchecked += (_, _) =>
        {
            _host.Config.Updates.AutomaticallyDownloadAndInstall = false;
            Persist();
        };
        panel.Children.Add(Btn("Check for updates", async () =>
        {
            _lastUpdateCheck = await _host.Updates.CheckAsync();
            if (!_lastUpdateCheck.Success)
                Msg(_lastUpdateCheck.Error ?? "Check failed.", MessageBoxImage.Warning);
            else if (_lastUpdateCheck.UpdateAvailable)
                Msg($"Update available: {_lastUpdateCheck.LatestVersion}");
            else
                Msg("You are up to date (or no release asset yet).");
            ShowSection("Updates");
        }));
        panel.Children.Add(Btn("Update now", async () =>
        {
            _lastUpdateCheck ??= await _host.Updates.CheckAsync();
            if (_lastUpdateCheck is null || !_lastUpdateCheck.UpdateAvailable)
            {
                Msg("No update available.");
                return;
            }
            var (ok, message) = await _host.Updates.DownloadAndApplyAsync(_lastUpdateCheck);
            Msg(message, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) Application.Current.Shutdown();
        }));
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
            Text =
                $"WinTAKTracker {version} — independent Windows PLI tracker.\n" +
                "Not an official TAK Product Center application.\n" +
                "TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners.\n" +
                "Map tiles: © OpenStreetMap contributors.\n" +
                "Network location: ipwho.is (approximate IP geolocation).\n" +
                "License: see LICENSE in the repository.\n" +
                "Updates: github.com/CopIXus/WinTAKTracker",
            TextWrapping = TextWrapping.Wrap,
        });
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

    private static UIElement GpsRow(string label, string value, Action onCopy)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var btn = new Button { Content = "Copy", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        btn.Click += (_, _) => onCopy();
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(Chip(label, value));
        return dock;
    }

    private static DockPanel RowBrowse(TextBox pathBox, string filter)
    {
        var dock = new DockPanel();
        var btn = new Button { Content = "…", Width = 32, Margin = new Thickness(6, 0, 0, 0) };
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

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 8, 0, 4),
        FontWeight = FontWeights.SemiBold,
    };

    private static TextBlock Blurb(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = System.Windows.Media.Brushes.DimGray,
        Margin = new Thickness(0, 0, 0, 12),
    };

    private static Border Chip(string label, string value)
    {
        var border = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEE, 0xF5, 0xF0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        border.Child = new TextBlock { Text = $"{label}: {value}", FontSize = 13 };
        return border;
    }

    private static Button Btn(string content, Action action)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = WpfHAlign.Left,
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static Button Btn(string content, Func<Task> action)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = WpfHAlign.Left,
        };
        b.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Msg(ex.Message, MessageBoxImage.Warning); }
        };
        return b;
    }

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
