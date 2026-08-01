using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Identity;
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
    private string? _updateProgress;
    private readonly List<Action> _serverCardRefreshers = [];

    private bool CanEdit => _host.SettingsLock.IsUnlocked;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Title = AppVersionDisplay.WindowTitle(_host.Updates.CurrentVersion, "Settings");
        _lastUpdateCheck = _host.LastUpdateCheck;
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

        RefreshPauseButton();
        RefreshLockChrome();
        // SelectionChanged → ShowSection; do not call ShowSection again (re-parents map preview).
        SectionList.SelectedIndex = 0;
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
        var choice = AppDialog.Show(
            this,
            "Lock settings now?\n\nYes = Lock\nNo = Change lock password\nCancel = dismiss",
            "Settings lock",
            MessageBoxButton.YesNoCancel);
        if (choice == MessageBoxResult.Yes)
        {
            _host.SettingsLock.Lock();
            _ = _host.LockServiceSettingsAsync();
        }
        else if (choice == MessageBoxResult.No)
            TryChangeLockPassword();
    }

    private void TryUnlockInteractive()
    {
        var pwd = PasswordPromptWindow.Prompt(this, "Unlock settings",
            "Enter the settings lock password to allow edits.");
        if (pwd is null) return;
        if (!_host.SettingsLock.TryUnlock(pwd))
        {
            Msg("Incorrect password.", MessageBoxImage.Warning);
            return;
        }

        _ = _host.UnlockServiceSettingsAsync(pwd);
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
            "Companions" => "Companion apps",
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
            "Companions" or "ViewMap" => BuildCompanions(),
            "Startup" => BuildStartup(),
            "Diagnostics" => BuildDiagnostics(),
            "Updates" => BuildUpdates(),
            "About" => BuildAbout(),
            _ => new TextBlock { Text = "Coming soon." },
        });
    }

    private StackPanel? _statusPanel;
    private readonly Dictionary<string, TextBlock> _statusTileValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _statusTileDetails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _statusTiles = new(StringComparer.Ordinal);

    private UIElement BuildStatus()
    {
        _statusPanel = new StackPanel();
        _statusTileValues.Clear();
        _statusTileDetails.Clear();
        _statusTiles.Clear();

        _statusPanel.Children.Add(BuildModeBadge());

        var grid = new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, 0, -8, 4),
        };
        foreach (var key in new[]
                 {
                     "Tracking", "GPS", "Position", "Motion",
                     "Servers", "Mesh", "Last PLI", "Callsign",
                 })
            grid.Children.Add(CreateStatusTile(key));
        _statusPanel.Children.Add(grid);

        _statusPanel.Children.Add(new TextBlock
        {
            Text = "Many laptops have no GNSS chip — browsers use Wi‑Fi/IP location. WinTAKTracker needs Location services ON, “Let desktop apps access your location”, and a permission grant from this tray app. When the Windows Service owns tracking, the tray acquires the fix and feeds it over IPC. USB NMEA is best for always-on GPS after logoff.",
            Style = TryStyle("HelperText"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var perm = _host.WindowsLocationPermission;
        if (perm is GpsPermissionState.Denied or GpsPermissionState.NotAvailable or GpsPermissionState.Unknown)
        {
            var locWarnText = new TextBlock
            {
                Text = perm == GpsPermissionState.Unknown
                    ? "Windows Location permission has not been requested yet. Use the button below (or GPS → Request permission). Also enable Settings → Privacy & security → Location → Location services and “Let desktop apps access your location”."
                    : "Windows Location is not available to this app. Enable Settings → Privacy & security → Location (Location services + desktop apps), then request permission. Until then, only USB NMEA or approximate Network IP can be used.",
                TextWrapping = TextWrapping.Wrap,
            };
            SetTheme(locWarnText, TextBlock.ForegroundProperty, "DangerBrush");
            var locWarn = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = locWarnText,
            };
            SetTheme(locWarn, Border.BackgroundProperty, "DangerBgBrush");
            SetTheme(locWarn, Border.BorderBrushProperty, "AppBorderBrush");
            _statusPanel.Children.Add(locWarn);
        }

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        actionRow.Children.Add(Btn("Request location permission", async () =>
        {
            var state = await _host.RequestWindowsLocationAccessAsync();
            Msg(state == GpsPermissionState.Allowed
                ? "Windows Location allowed. Wait a few seconds for a Wi‑Fi/network fix; Status tiles and the map will update when a position arrives."
                : $"Permission: {state}. Open Windows Location privacy settings and enable Location services + “Let desktop apps access your location”.");
            if (SectionList.SelectedItem is ListBoxItem { Tag: string tag } && tag == "Status")
                ShowSection("Status");
        }));
        actionRow.Children.Add(Spacer(8));
        actionRow.Children.Add(Btn("Open Windows Location settings", () =>
            WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        _statusPanel.Children.Add(actionRow);

        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        copyRow.Children.Add(Btn("Copy lat", () =>
        {
            var f = GetEffectiveFix();
            if (f is not null) Copy(f.Latitude.ToString("F6"));
        }));
        copyRow.Children.Add(Spacer(8));
        copyRow.Children.Add(Btn("Copy lon", () =>
        {
            var f = GetEffectiveFix();
            if (f is not null) Copy(f.Longitude.ToString("F6"));
        }));
        _statusPanel.Children.Add(copyRow);

        _mapPreview ??= new MapPreviewControl();
        DetachFromLogicalParent(_mapPreview);

        _statusPanel.Children.Add(SectionHeader("Self map preview"));
        var mapChrome = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _mapPreview,
        };
        SetTheme(mapChrome, Border.BorderBrushProperty, "AppBorderBrush");
        _statusPanel.Children.Add(mapChrome);
        RefreshStatusLive();
        return _statusPanel;
    }

    private Border CreateStatusTile(string key)
    {
        var stack = new StackPanel();
        var label = new TextBlock { Text = key, Style = TryStyle("StatusTileLabel") };
        var value = new TextBlock { Text = "—", Style = TryStyle("StatusTileValue") };
        var detail = new TextBlock { Text = "", Style = TryStyle("StatusTileDetail"), Visibility = Visibility.Collapsed };
        stack.Children.Add(label);
        stack.Children.Add(value);
        stack.Children.Add(detail);

        var tile = new Border
        {
            Style = TryStyle("StatusTile"),
            Child = stack,
            Tag = key,
        };
        _statusTiles[key] = tile;
        _statusTileValues[key] = value;
        _statusTileDetails[key] = detail;
        return tile;
    }

    private void SetStatusTile(string key, string value, string? detail = null, string tone = "neutral")
    {
        if (_statusTileValues.TryGetValue(key, out var valueTb))
            valueTb.Text = value;
        if (_statusTileDetails.TryGetValue(key, out var detailTb))
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                detailTb.Text = "";
                detailTb.Visibility = Visibility.Collapsed;
            }
            else
            {
                detailTb.Text = detail;
                detailTb.Visibility = Visibility.Visible;
            }
        }

        if (!_statusTiles.TryGetValue(key, out var tile)) return;
        var (bg, fg) = tone switch
        {
            "ok" => ("StatusOkBgBrush", "StatusOkFgBrush"),
            "warn" => ("StatusWarnBgBrush", "StatusWarnFgBrush"),
            "info" => ("StatusInfoBgBrush", "StatusInfoFgBrush"),
            _ => ("StatusNeutralBgBrush", "StatusNeutralFgBrush"),
        };
        SetTheme(tile, Border.BackgroundProperty, bg);
        if (valueTb is not null)
            SetTheme(valueTb, TextBlock.ForegroundProperty, fg);
    }

    private GpsFix? GetEffectiveFix()
    {
        if (!_host.AttachedToService)
            return _host.Gps.CurrentFix;

        var st = _host.LastServiceStatus;
        if (st?.Latitude is not double lat || st.Longitude is not double lon)
            return null;

        var source = GpsSourceKind.None;
        if (!string.IsNullOrWhiteSpace(st.GpsSource))
            Enum.TryParse(st.GpsSource, true, out source);

        return new GpsFix
        {
            Latitude = lat,
            Longitude = lon,
            AltitudeMeters = st.AltitudeMeters,
            SpeedMetersPerSecond = st.SpeedMetersPerSecond,
            CourseDegrees = st.CourseDegrees,
            AccuracyMeters = st.AccuracyMeters,
            Timestamp = st.GpsTimestampUtc ?? DateTimeOffset.UtcNow,
            Source = source,
            IsHeld = st.GpsIsHeld,
        };
    }

    /// <summary>
    /// Clears a FrameworkElement from its current logical parent so it can be re-hosted.
    /// Map preview lives in a Border.Child (Decorator), not a Panel — Panel.Remove alone never ran.
    /// </summary>
    private static void DetachFromLogicalParent(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case System.Windows.Controls.Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case System.Windows.Controls.ContentControl contentControl:
                contentControl.Content = null;
                break;
            case ContentPresenter presenter:
                presenter.Content = null;
                break;
        }
    }

    private UIElement BuildModeBadge()
    {
        var service = _host.AttachedToService;
        var title = service ? "Mode: Windows Service" : "Mode: Standalone";
        var sub = service
            ? (ConfigPaths.IsServiceInstalled()
                ? "Tray attached via IPC · tracking owned by the Windows Service · location bridged from this session"
                : "IPC pipe connected · location bridged from this session")
            : (ConfigPaths.IsServiceInstalled()
                ? "In-process tracking · service installed but not attached"
                : "In-process tracking · Windows Service not installed");

        var stack = new StackPanel();
        var titleTb = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
        };
        SetTheme(titleTb, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(titleTb);
        var subTb = new TextBlock
        {
            Text = sub,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        SetTheme(subTb, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stack.Children.Add(subTb);

        return new Border
        {
            Style = TryStyle("InfoChip"),
            Margin = new Thickness(0, 0, 0, 14),
            Child = stack,
        };
    }

    private void RefreshStatusLive()
    {
        if (_statusTileValues.Count == 0) return;

        if (_host.AttachedToService)
            _host.RefreshTray();

        var fix = GetEffectiveFix();
        var servers = FormatServerStatusSummary(_host.GetServerStatuses());
        var paused = _host.AttachedToService
            ? _host.LastServiceStatus?.Paused == true
            : _host.Pause.IsPaused;
        var meshReady = _host.AttachedToService
            ? _host.LastServiceStatus?.MeshReady == true
            : (_host.Config.MeshSa.Enabled && _host.Mesh.IsReady && _host.Mesh.LastErrorCode is null);
        var meshDetail = _host.AttachedToService
            ? (_host.LastServiceStatus?.MeshLastError ?? (_host.Config.MeshSa.Enabled ? null : "Disabled"))
            : FormatMeshStatusLine();
        var lastPli = _host.AttachedToService
            ? _host.LastServiceStatus?.LastPliSentUtc
            : _host.Reporting.LastPliSentUtc;
        string callsign;
        string team;
        if (_host.AttachedToService && _host.LastServiceStatus?.ActiveIdentity is { } id)
        {
            callsign = $"{id.Callsign} ({id.Source})";
            team = id.Team;
        }
        else
        {
            var active = _host.Core.GetActiveIdentity();
            callsign = $"{active.Callsign} ({active.Source})";
            team = active.Team;
        }

        SetStatusTile("Tracking", paused ? "Paused" : "Active",
            _host.Tray.CurrentState.ToStatusLabel(),
            paused ? "warn" : "ok");

        if (fix is null)
        {
            SetStatusTile("GPS", "No fix",
                _host.AttachedToService
                    ? "Waiting for tray Windows Location, NMEA, or Network IP"
                    : "Waiting for NMEA, Windows Location, or Network IP",
                "warn");
            SetStatusTile("Position", "—", "No coordinates yet", "neutral");
            SetStatusTile("Motion", "—", null, "neutral");
        }
        else
        {
            var liveLabel = fix.IsHeld ? "Held" : "Live";
            var sourceName = !string.IsNullOrWhiteSpace(_host.LastServiceStatus?.GpsSourceDisplay) && _host.AttachedToService
                ? _host.LastServiceStatus!.GpsSourceDisplay!
                : fix.SourceDisplayName;
            SetStatusTile("GPS", $"{liveLabel}", sourceName, fix.IsHeld ? "info" : "ok");
            SetStatusTile("Position", $"{fix.Latitude:F5}, {fix.Longitude:F5}",
                fix.AccuracyMeters is double acc ? $"±{acc:F0} m" : null,
                "ok");
            var course = fix.CourseDegrees is double c ? $"{c:F0}°" : "—";
            var alt = fix.AltitudeMeters is double a ? $"{a:F0} m" : "—";
            SetStatusTile("Motion", $"{fix.SpeedMph:F1} mph",
                $"Course {course} · Alt {alt}" +
                (fix.Source == GpsSourceKind.NetworkIp ? " · city/region scale" : ""),
                fix.Source == GpsSourceKind.NetworkIp ? "info" : "ok");
        }

        var anyConnected = _host.GetServerStatuses().Any(s => s.State == TakConnectionState.Connected);
        SetStatusTile("Servers",
            string.IsNullOrEmpty(servers) || servers == "None" ? "None" : (anyConnected ? "Connected" : "Not connected"),
            string.IsNullOrEmpty(servers) ? null : servers,
            anyConnected ? "ok" : "neutral");

        SetStatusTile("Mesh",
            meshReady ? "Ready" : (_host.Config.MeshSa.Enabled ? "Not ready" : "Off"),
            meshReady ? null : meshDetail,
            meshReady ? "ok" : (_host.Config.MeshSa.Enabled ? "warn" : "neutral"));

        SetStatusTile("Last PLI",
            lastPli?.ToLocalTime().ToString("g") ?? "—",
            null,
            lastPli.HasValue ? "ok" : "neutral");

        SetStatusTile("Callsign", callsign, null, "info");

        _mapPreview?.UpdateFix(fix, team);
    }

    private static string FormatServerStatusSummary(IReadOnlyList<ServerConnectionStatus> statuses)
    {
        if (statuses.Count == 0) return "None";
        return string.Join("; ", statuses.Select(s =>
        {
            var label = s.State switch
            {
                TakConnectionState.Connected => "Connected",
                TakConnectionState.Connecting or TakConnectionState.Reconnecting => "Connecting",
                TakConnectionState.Error => "Error",
                _ when s.Enabled => "Enabled, not connected",
                _ => "Disconnected",
            };
            return $"{s.DisplayName}: {label}";
        }));
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
        panel.Children.Add(Blurb(
            _host.AttachedToService || ConfigPaths.IsServiceInstalled()
                ? "Enroll via URL/QR, SoftCert ZIP, or manual .p12. Profiles are stored under ProgramData for the Windows Service. Portal tokens are short-lived (~15 minutes)."
                : "Enroll via URL/QR, SoftCert ZIP, or manual .p12. Profiles stay in LocalAppData (portable). Portal tokens are short-lived (~15 minutes)."));

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
            SetTheme(enrollStatus, TextBlock.ForegroundProperty, "AccentBrush");
            var progress = new Progress<string>(msg => { enrollStatus.Text = msg; });
            var sid = IdentityResolver.CurrentUserSid();
            var userName = Environment.UserName;
            var result = await _host.Enrollment.ApplyAsync(
                input, _host.Config, progress, CancellationToken.None, sid, userName);
            if (!result.Success)
            {
                enrollStatus.Text = result.Error ?? "Enrollment failed.";
                SetTheme(enrollStatus, TextBlock.ForegroundProperty, "DangerBrush");
                Msg(result.Error ?? "Failed", MessageBoxImage.Warning);
                return;
            }

            enrollStatus.Text = result.Message ?? "Enrolled.";
            SetTheme(enrollStatus, TextBlock.ForegroundProperty, "AccentBrush");
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
            var result = _host.Enrollment.ImportSoftCertZip(
                ofd.FileName, _host.Config, displayName: null,
                IdentityResolver.CurrentUserSid(), Environment.UserName);
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
                if (AppDialog.Show(this, "Wipe ALL server profiles and certs?", "Forget all",
                        MessageBoxButton.YesNo, dangerPrimary: true) != MessageBoxResult.Yes) return;
                _host.Tak.WipeAll(_host.Config, _host.ConfigStore);
                ShowSection("Servers");
            }, edit, danger: true));
        }

        return panel;
    }

    private UIElement BuildServerCard(ServerProfile server, bool edit)
    {
        var card = new Border
        {
            Style = TryStyle("ServerCard"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var root = new DockPanel { LastChildFill = true };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(actions, Dock.Right);

        var testBtn = CompactBtn("Test", async () =>
        {
            var (ok, message) = await _host.Tak.TestServerAsync(server.Id, _host.Config);
            Msg(
                ok
                    ? $"{message}\n\nTest opens a temporary socket; check Connect to keep a live stream."
                    : message,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }, edit);
        testBtn.Margin = new Thickness(0, 0, 4, 0);

        var removeBtn = new Button
        {
            Content = "✕",
            Style = TryStyle("DangerIconButton") ?? TryStyle("IconButton"),
            ToolTip = "Remove profile",
            IsEnabled = edit,
        };
        removeBtn.Click += (_, _) =>
        {
            if (!EnsureEditable()) return;
            var label = string.IsNullOrWhiteSpace(server.Host) ? server.DisplayName : server.Host;
            if (AppDialog.Show(
                    this,
                    $"Delete profile \"{label}\" and its certs from this PC?",
                    "Remove profile",
                    MessageBoxButton.YesNo,
                    dangerPrimary: true) != MessageBoxResult.Yes) return;
            _host.Tak.WipeProfile(_host.Config, server.Id, _host.ConfigStore);
            if (_host.AttachedToService)
                _ = _host.ReloadConnectionsAsync();
            ShowSection("Servers");
        };

        actions.Children.Add(testBtn);
        actions.Children.Add(removeBtn);
        root.Children.Add(actions);

        var body = new StackPanel();
        var primary = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

        var connectCheck = new CheckBox
        {
            Content = "Connect",
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = edit,
            IsChecked = server.Enabled,
        };
        DockPanel.SetDock(connectCheck, Dock.Left);

        var statusBadge = StatusBadge(TakConnectionState.Disconnected);
        DockPanel.SetDock(statusBadge, Dock.Right);
        statusBadge.Margin = new Thickness(8, 0, 0, 0);

        var host = string.IsNullOrWhiteSpace(server.Host) ? server.DisplayName : server.Host;
        var protoPort = $"{server.Protocol.ToUpperInvariant()}:{server.Port}";
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(host) ? protoPort : $"{host}  ·  {protoPort}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = BuildServerTooltip(server),
        };
        SetTheme(title, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        primary.Children.Add(connectCheck);
        primary.Children.Add(statusBadge);
        primary.Children.Add(title);

        var errorText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(28, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        SetTheme(errorText, TextBlock.ForegroundProperty, "DangerBrush");

        body.Children.Add(primary);
        body.Children.Add(errorText);
        root.Children.Add(body);
        card.Child = root;

        var suppressing = false;
        void OnConnectChanged(object? s, RoutedEventArgs e)
        {
            if (suppressing || !EnsureEditable()) return;
            server.Enabled = connectCheck.IsChecked == true;
            Persist();
            _ = _host.ReloadConnectionsAsync();
        }

        connectCheck.Checked += OnConnectChanged;
        connectCheck.Unchecked += OnConnectChanged;

        void Refresh()
        {
            var status = _host.GetServerStatuses().FirstOrDefault(s => s.ProfileId == server.Id);
            var state = status?.State ?? TakConnectionState.Disconnected;
            var enabled = status?.Enabled ?? server.Enabled;
            var lastError = status?.LastErrorCode;
            ApplyStatusBadge(statusBadge, state, enabled, lastError);
            title.ToolTip = BuildServerTooltip(server, lastError);

            var showError = !string.IsNullOrWhiteSpace(lastError)
                            && state is not (TakConnectionState.Connected
                                or TakConnectionState.Connecting
                                or TakConnectionState.Reconnecting)
                            && (state == TakConnectionState.Error || enabled);
            if (showError)
            {
                var full = lastError!.Trim();
                errorText.Text = TruncateUi(full, 140);
                errorText.ToolTip = full.Length > 140 ? full : null;
                errorText.Visibility = Visibility.Visible;
            }
            else
            {
                errorText.Text = "";
                errorText.ToolTip = null;
                errorText.Visibility = Visibility.Collapsed;
            }

            if (connectCheck.IsChecked != enabled)
            {
                suppressing = true;
                connectCheck.IsChecked = enabled;
                suppressing = false;
            }
        }

        Refresh();
        _serverCardRefreshers.Add(Refresh);
        return card;
    }

    private static string TruncateUi(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..(maxChars - 1)] + "…";
    }

    private string BuildServerTooltip(ServerProfile server, string? error = null)
    {
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
        var tip = $"Identity: {identity}  ·  {certText}";
        if (!string.IsNullOrWhiteSpace(server.DisplayName) &&
            !string.Equals(server.DisplayName, server.Host, StringComparison.OrdinalIgnoreCase))
            tip = $"{server.DisplayName}\n{tip}";
        if (!string.IsNullOrWhiteSpace(error))
            tip += $"\n{error}";
        return tip;
    }

    private static Border StatusBadge(TakConnectionState state)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        ApplyStatusBadge(border, state, enabled: false, null);
        return border;
    }

    private static void ApplyStatusBadge(Border badge, TakConnectionState state, bool enabled, string? error)
    {
        string label;
        string bgKey;
        string fgKey;
        switch (state)
        {
            case TakConnectionState.Connected:
                label = "Connected";
                bgKey = "StatusOkBgBrush";
                fgKey = "StatusOkFgBrush";
                break;
            case TakConnectionState.Connecting:
            case TakConnectionState.Reconnecting:
                label = "Connecting";
                bgKey = "StatusInfoBgBrush";
                fgKey = "StatusInfoFgBrush";
                break;
            case TakConnectionState.Error:
                label = "Error";
                bgKey = "DangerBgBrush";
                fgKey = "DangerBrush";
                break;
            default:
                if (enabled)
                {
                    label = "Not connected";
                    bgKey = "StatusWarnBgBrush";
                    fgKey = "StatusWarnFgBrush";
                }
                else
                {
                    label = "Disconnected";
                    bgKey = "StatusNeutralBgBrush";
                    fgKey = "StatusNeutralFgBrush";
                }
                break;
        }

        SetTheme(badge, Border.BackgroundProperty, bgKey);
        if (badge.Child is TextBlock tb)
        {
            tb.Text = label;
            SetTheme(tb, TextBlock.ForegroundProperty, fgKey);
            if (state == TakConnectionState.Error && !string.IsNullOrWhiteSpace(error))
                badge.ToolTip = error;
            else if (enabled && state == TakConnectionState.Disconnected)
                badge.ToolTip = "Enabled profile; live stream is not up. Click Connect to retry, or Test to probe.";
            else
                badge.ToolTip = null;
        }
    }

    private static Button CompactBtn(string content, Action action, bool enabled = true, bool primary = false, bool danger = false)
    {
        var styleKey = primary ? "CompactPrimaryButton" : danger ? "CompactDangerButton" : "CompactButton";
        var b = new Button
        {
            Content = content,
            Style = TryStyle(styleKey) ?? TryStyle(primary ? "PrimaryButton" : danger ? "DangerButton" : "SecondaryButton"),
            IsEnabled = enabled,
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static Button CompactBtn(string content, Func<Task> action, bool enabled = true, bool primary = false, bool danger = false)
    {
        var styleKey = primary ? "CompactPrimaryButton" : danger ? "CompactDangerButton" : "CompactButton";
        var b = new Button
        {
            Content = content,
            Style = TryStyle(styleKey) ?? TryStyle(primary ? "PrimaryButton" : danger ? "DangerButton" : "SecondaryButton"),
            IsEnabled = enabled,
        };
        b.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Msg(ex.Message, MessageBoxImage.Warning); }
        };
        return b;
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
        };
        SetTheme(dlg, Window.BackgroundProperty, "ContentBgBrush");
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
        var computerPhone = new TextBox { Text = computer.Phone ?? "", IsEnabled = edit };
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
        panel.Children.Add(Label("Computer phone (optional)")); panel.Children.Add(computerPhone);
        panel.Children.Add(new TextBlock
        {
            Text = "Used on ATAK contact cards for Call when the computer identity is active. Sent cleartext on the TAK network.",
            Style = TryStyle("HelperText"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
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
            _host.SaveComputerIdentity(next, computerTeam.Text.Trim(), computerRole.Text.Trim(), cotType, computerPhone.Text.Trim());
        }

        BindPersistText(computerCallsign, SaveComputer);
        BindPersistText(computerTeam, SaveComputer);
        BindPersistText(computerRole, SaveComputer);
        BindPersistText(computerPhone, SaveComputer);
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
        var myPhone = new TextBox { Text = user?.Phone ?? "", IsEnabled = edit };
        panel.Children.Add(Label("My callsign")); panel.Children.Add(myCallsign);
        panel.Children.Add(Label("My team")); panel.Children.Add(myTeam);
        panel.Children.Add(Label("My role")); panel.Children.Add(myRole);
        panel.Children.Add(Label("My phone (optional)")); panel.Children.Add(myPhone);
        panel.Children.Add(new TextBlock
        {
            Text = "Used on ATAK contact cards for Call while you are logged in. Sent cleartext on the TAK network.",
            Style = TryStyle("HelperText"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
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
                computer.CotType,
                myPhone.Text.Trim());
        }

        BindPersistText(myCallsign, SaveUser);
        BindPersistText(myTeam, SaveUser);
        BindPersistText(myRole, SaveUser);
        BindPersistText(myPhone, SaveUser);
        panel.Children.Add(SectionHeader("Portal / remote identity"));
        var applyRemote = new CheckBox
        {
            Content = "Apply callsign/team from Portal / device-profile sync",
            IsChecked = _host.Config.ApplyRemoteIdentityFromPortal,
            IsEnabled = edit,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(applyRemote);
        panel.Children.Add(Blurb(
            "When enabled (default), TAK Server / OpenTAK preference packages can update callsign and team (with .wtt suffix). Turn off to keep local identity authoritative."));
        applyRemote.Checked += (_, _) =>
        {
            if (!CanEdit) return;
            _host.Config.ApplyRemoteIdentityFromPortal = true;
            Persist();
        };
        applyRemote.Unchecked += (_, _) =>
        {
            if (!CanEdit) return;
            _host.Config.ApplyRemoteIdentityFromPortal = false;
            Persist();
        };

        panel.Children.Add(Chip("Persisted", "Identity saves automatically when you change a field."));
        return panel;
    }

    private UIElement BuildGps()
    {
        var panel = new StackPanel();
        var edit = CanEdit;
        panel.Children.Add(Blurb(
            "Preferred order: USB NMEA (if you select a COM port) → Windows Location (Wi‑Fi/OS, same stack browsers use) → network/IP geolocation last (approximate, large CE via ipwho.is).\n\n" +
            "Laptops often have no GNSS chip — Chrome/Edge use Wi‑Fi/IP via the same Windows Location stack. Enable Settings → Privacy & security → Location → Location services ON, and “Let desktop apps access your location”. Then Request permission below (must run from this tray app, not the Windows Service).\n\n" +
            "Network/IP fallback defaults off for new configs (opt-in; coarse location).\n\n" +
            (_host.AttachedToService
                ? "Service mode: the tray acquires Windows Location and feeds the service over IPC while you are logged on. USB NMEA is best for always-on GPS after logoff."
                : "Standalone mode: this process owns GPS directly. USB NMEA is still best when you need a fix after the user session ends.")));
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
            // Permission must be requested from the interactive tray process (not LocalSystem).
            var state = await _host.RequestWindowsLocationAccessAsync();
            Msg(state == GpsPermissionState.Allowed
                ? "Windows Location allowed. If Status still shows Network IP, wait a few seconds for a Wi‑Fi fix or confirm Location services and desktop-apps access are on."
                : $"Permission: {state}. Open Windows Location privacy settings and enable Location + desktop apps.");
        }));
        permRow.Children.Add(new Border { Width = 8 });
        permRow.Children.Add(Btn("Open Windows Location settings", () =>
            WindowsLocationGps.OpenWindowsLocationPrivacySettings()));
        panel.Children.Add(permRow);
        panel.Children.Add(new TextBlock
        {
            Text = $"Current permission: {_host.WindowsLocationPermission}",
            Style = TryStyle("HelperText"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        async Task SaveGpsAsync()
        {
            if (!CanEdit) return;
            _host.Config.Gps.ComPort = port.Text.StartsWith('(') ? null : port.Text;
            _host.Config.Gps.BaudRate = int.TryParse(baud.Text, out var b) ? b : 4800;
            _host.Config.Gps.SourcePriority = priority.SelectedItem?.ToString() ?? "NmeaThenWindows";
            _host.Config.Gps.LastFixHoldSeconds = int.TryParse(hold.Text, out var h) ? h : 30;
            _host.Config.Gps.EnableNetworkFallback = network.IsChecked == true;
            Persist();
            if (_host.AttachedToService)
                await _host.ReloadConnectionsAsync();
            else
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

        var remarks = new CheckBox
        {
            Content = "Include computer name in CoT remarks when callsign differs",
            IsChecked = _host.Config.Reporting.IncludeComputerNameInRemarks,
            IsEnabled = edit,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(remarks);
        panel.Children.Add(Blurb(
            "When your PLI callsign is not this PC’s Windows name, peers see the computer name in ATAK remarks. Off skips that remarks line. Default on."));

        void SaveReporting()
        {
            if (!CanEdit) return;
            _host.Config.Reporting.Strategy = strategy.SelectedItem?.ToString() ?? "Dynamic";
            if (int.TryParse(relStat.Text, out var rs))
                _host.Config.Reporting.ReliableStationarySeconds = Math.Max(5, rs);
            if (int.TryParse(unrelStat.Text, out var us))
                _host.Config.Reporting.UnreliableStationarySeconds = Math.Max(5, us);
            if (int.TryParse(constant.Text, out var c))
                _host.Config.Reporting.ConstantIntervalSeconds = Math.Max(5, c);
            // Dynamic floors also default to ≥5s in the reporting engine.
            _host.Config.Reporting.ReliableMinSeconds = Math.Max(5, _host.Config.Reporting.ReliableMinSeconds);
            _host.Config.Reporting.UnreliableMinSeconds = Math.Max(5, _host.Config.Reporting.UnreliableMinSeconds);
            _host.Config.Reporting.IncludeComputerNameInRemarks = remarks.IsChecked == true;
            Persist();
        }

        strategy.SelectionChanged += (_, _) => SaveReporting();
        BindPersistText(relStat, SaveReporting);
        BindPersistText(unrelStat, SaveReporting);
        BindPersistText(constant, SaveReporting);
        remarks.Checked += (_, _) => SaveReporting();
        remarks.Unchecked += (_, _) => SaveReporting();
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
                Msg("Enable Broadcast Mesh SA first.");
                return;
            }

            _host.Mesh.Rebind();
            _host.Reporting.RequestAsap();
            RefreshStatusLive();
            Msg(
                $"Queued ASAP Mesh SA via {_host.Mesh.LastInterfaceDescription ?? "—"}.\n" +
                (_host.Mesh.LastInterfaceWarning ?? "Check ATAK on the same LAN for your callsign."),
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

    private UIElement BuildCompanions()
    {
        var panel = new StackPanel();
        panel.Children.Add(Blurb(
            "WinTAKTracker is tracking-only. Use a companion TAK map client to see yourself and others on the COP."));

        panel.Children.Add(CompanionLink(
            "Assets/companions/atak.png",
            "ATAK (Android)",
            "Google Play — ATAK-CIV",
            "https://play.google.com/store/apps/details?id=com.atakmap.app.civ"));
        panel.Children.Add(CompanionLink(
            "Assets/companions/itak.png",
            "iTAK / TAK Aware (iOS)",
            "App Store",
            "https://apps.apple.com/us/app/tak-aware/id6738631659"));
        panel.Children.Add(CompanionLink(
            "Assets/companions/wintak.png",
            "WinTAK (Windows)",
            "TAK.gov platform downloads",
            "https://tak.gov"));
        panel.Children.Add(CompanionLink(
            "Assets/companions/takgov.png",
            "TAK.gov",
            "Official TAK Product Center site",
            "https://tak.gov"));

        panel.Children.Add(new TextBlock
        {
            Text = "TAK / ATAK / iTAK / WinTAK are trademarks of their respective owners. Icons are simple platform marks, not official badges.",
            Style = TryStyle("HelperText"),
            Margin = new Thickness(0, 8, 0, 0),
        });
        return panel;
    }

    private UIElement CompanionLink(string packRelativePath, string title, string subtitle, string url)
    {
        var icon = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/{packRelativePath}");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            icon.Source = bmp;
        }
        catch
        {
            // icon optional
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        };
        SetTheme(titleTb, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var subTb = new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        };
        SetTheme(subTb, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        text.Children.Add(titleTb);
        text.Children.Add(subTb);

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
        SetTheme(border, Border.BackgroundProperty, "SurfaceBrush");
        SetTheme(border, Border.BorderBrushProperty, "AppBorderBrush");
        border.MouseLeftButtonUp += (_, _) => OpenUrl(url);
        return border;
    }

    private UIElement BuildStartup()
    {
        var panel = new StackPanel();
        var edit = CanEdit;

        panel.Children.Add(SectionHeader("Windows Service"));
        panel.Children.Add(Chip("Service status", ConfigPaths.GetWindowsServiceStatusLabel()));
        panel.Children.Add(Chip("Mode", ConfigPaths.GetTrackingModeLabel(_host.AttachedToService)));
        panel.Children.Add(Blurb(
            _host.AttachedToService
                ? "Tray is attached to the Windows Service over IPC. Tracking continues after logoff when the service is Running."
                : ConfigPaths.IsServiceInstalled()
                    ? "Service is installed but this tray session is running Standalone (IPC not attached). Restart the tray or start the service to attach."
                    : "Portable / Standalone mode — in-process tracking. Install via WinTAKTracker-Setup for always-on Service mode."));

        panel.Children.Add(SectionHeader("Options", topMargin: 4));
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
        panel.Children.Add(Blurb(
            _host.AttachedToService || ConfigPaths.IsServiceInstalled()
                ? "Start with Windows launches this tray at login (control UI, callsign prompt, Windows Location bridge). The Windows Service should already be set to Automatic for always-on PLI after logoff. Prevent-sleep is optional and off by default."
                : "Start with Windows launches the tray at login so tracking and the UI run for this user. Prevent-sleep is optional and off by default. Changes auto-save."));
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
        var configuredLevel = _host.Config.Diagnostics.LogLevel;
        var levels = new[] { "Debug", "Information", "Warning", "Error" };
        if (!levels.Contains(configuredLevel, StringComparer.OrdinalIgnoreCase))
            configuredLevel = "Error";

        var level = new ComboBox
        {
            ItemsSource = levels,
            SelectedItem = levels.FirstOrDefault(l => l.Equals(configuredLevel, StringComparison.OrdinalIgnoreCase)) ?? "Error",
            IsEnabled = edit,
        };
        var maxSize = new TextBox
        {
            Text = Math.Clamp(_host.Config.Diagnostics.MaxLogSizeMb, 1, 1024).ToString(),
            IsEnabled = edit,
            Width = 120,
            HorizontalAlignment = WpfHAlign.Left,
        };

        panel.Children.Add(Blurb("Logs are redacted (tokens, enroll URLs, key material). Default level is Error (includes TAK connection failures). Raise to Warning/Information temporarily when diagnosing."));
        panel.Children.Add(Label("Log level")); panel.Children.Add(level);
        panel.Children.Add(Label("Max log size (MB)")); panel.Children.Add(maxSize);
        panel.Children.Add(new TextBlock
        {
            Text = "When total size of log files exceeds this limit, oldest files are removed and the active file may be truncated.",
            Style = TryStyle("HelperText"),
        });

        var softTls = new CheckBox
        {
            Content = "Allow insecure TLS soft-accept (private CA / SoftCert lab)",
            IsChecked = _host.Config.Diagnostics.AllowInsecureTlsSoftAccept,
            IsEnabled = edit,
            Margin = new Thickness(0, 12, 0, 0),
        };
        panel.Children.Add(softTls);
        panel.Children.Add(new TextBlock
        {
            Text = "Default off: trust-store validation must succeed or TLS is rejected. Enable only for SoftCert / private-CA labs that cannot present a chain the OS trusts.",
            Style = TryStyle("HelperText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
        });

        void SaveDiagnostics()
        {
            if (!CanEdit) return;
            _host.Config.Diagnostics.LogLevel = level.SelectedItem?.ToString() ?? "Error";
            if (int.TryParse(maxSize.Text.Trim(), out var mb))
                _host.Config.Diagnostics.MaxLogSizeMb = Math.Clamp(mb, 1, 1024);
            else
                _host.Config.Diagnostics.MaxLogSizeMb = 30;
            maxSize.Text = _host.Config.Diagnostics.MaxLogSizeMb.ToString();
            _host.Config.Diagnostics.AllowInsecureTlsSoftAccept = softTls.IsChecked == true;
            Persist();
        }

        level.SelectionChanged += (_, _) => SaveDiagnostics();
        BindPersistText(maxSize, SaveDiagnostics);
        softTls.Checked += (_, _) => SaveDiagnostics();
        softTls.Unchecked += (_, _) => SaveDiagnostics();

        panel.Children.Add(Btn("Open log folder", () =>
        {
            Directory.CreateDirectory(_host.ConfigStore.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = _host.ConfigStore.LogsDirectory, UseShellExecute = true });
        }));
        panel.Children.Add(Btn("Clear logs older than 14 days", () =>
        {
            if (!EnsureEditable()) return;
            _host.Log.ClearOldLogs(TimeSpan.FromDays(14));
            _host.Log.EnforceSizeLimit();
            Msg("Old logs cleared.");
        }, edit));
        panel.Children.Add(Btn("Trim logs to size limit now", () =>
        {
            if (!EnsureEditable()) return;
            _host.Log.EnforceSizeLimit();
            Msg("Log size limit enforced.");
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

        var current = _lastUpdateCheck?.CurrentVersion ?? _host.Updates.CurrentVersion;
        var latest = _lastUpdateCheck?.LatestVersion;
        var statusText = FormatUpdateStatus(_lastUpdateCheck);
        var lastChecked = FormatLastChecked(_host.Config.Updates.LastCheckedUtc);

        panel.Children.Add(Chip("Current version", current));
        panel.Children.Add(Chip("Latest", latest ?? "(not checked)"));
        panel.Children.Add(Chip("Status", statusText));
        panel.Children.Add(Chip("Last checked", lastChecked));
        if (_lastUpdateCheck is { UpdateAvailable: true, AssetName: not null })
            panel.Children.Add(Chip("Package", _lastUpdateCheck.AssetName));

        if (!string.IsNullOrWhiteSpace(_lastUpdateCheck?.ReleaseNotes) && _lastUpdateCheck.UpdateAvailable)
        {
            var notes = new TextBlock
            {
                Text = _lastUpdateCheck.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            };
            SetTheme(notes, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(notes);
        }

        if (UpdateService.IsManagedInstall())
        {
            var uacHint = new TextBlock
            {
                Text = "Setup / service installs update via WinTAKTracker-Setup.exe. Windows may ask for administrator approval (UAC).",
                Style = TryStyle("HelperText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            SetTheme(uacHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(uacHint);
        }

        if (!string.IsNullOrWhiteSpace(_updateProgress))
        {
            var progress = new TextBlock
            {
                Text = _updateProgress,
                Style = TryStyle("HelperText"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            SetTheme(progress, TextBlock.ForegroundProperty, "AccentBrush");
            panel.Children.Add(progress);
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
            _host.NoteUpdateCheck(_lastUpdateCheck);
            // Inline only — no popup for up-to-date / available results.
            ShowSection("Updates");
        }, enabled: string.IsNullOrWhiteSpace(_updateProgress)));

        var updateAvailable = _lastUpdateCheck?.UpdateAvailable == true;
        if (updateAvailable)
        {
            updateRow.Children.Add(Spacer(8));
            updateRow.Children.Add(Btn("Update now", async () =>
            {
                if (!EnsureEditable()) return;
                if (!string.IsNullOrWhiteSpace(_updateProgress)) return;

                _lastUpdateCheck = await _host.Updates.CheckAsync();
                _host.NoteUpdateCheck(_lastUpdateCheck);
                if (!_lastUpdateCheck.Success || !_lastUpdateCheck.UpdateAvailable)
                {
                    ShowSection("Updates");
                    return;
                }

                var setup = _lastUpdateCheck.AssetKind == UpdateAssetKind.SetupInstaller
                            || _lastUpdateCheck.RequiresElevation;
                var confirmBody = setup
                    ? $"Download and run WinTAKTracker-Setup for version {_lastUpdateCheck.LatestVersion}?\n\n" +
                      "Windows may ask for administrator approval (UAC). " +
                      "After you approve, WinTAKTracker will quit so Setup can replace the service and tray. " +
                      "Your settings and certs are kept."
                    : $"Download and install version {_lastUpdateCheck.LatestVersion}?\n\n" +
                      "WinTAKTracker will quit, replace the portable EXE, and relaunch. Your settings and certs are kept.";

                var confirm = AppDialog.Show(
                    this,
                    confirmBody,
                    "Apply update",
                    MessageBoxButton.OKCancel);
                if (confirm != MessageBoxResult.OK)
                    return;

                _updateProgress = setup
                    ? "Downloading Setup installer…"
                    : "Downloading update…";
                ShowSection("Updates");

                var (ok, message) = await _host.Updates.DownloadAndApplyAsync(_lastUpdateCheck);
                _updateProgress = null;
                if (!ok)
                {
                    _host.Log.Warn("Update", $"Update now failed: {message}");
                    Msg(message, MessageBoxImage.Warning);
                    ShowSection("Updates");
                    return;
                }

                // Only quit after a real apply path armed (Setup process or portable helper).
                _host.Log.Info("Update", message);
                Application.Current.Shutdown();
            }, edit && string.IsNullOrWhiteSpace(_updateProgress), primary: true));
        }

        panel.Children.Add(updateRow);
        if (_lastUpdateCheck is { Success: false } && !string.IsNullOrWhiteSpace(_lastUpdateCheck.Error))
        {
            var err = new TextBlock
            {
                Text = _lastUpdateCheck.Error,
                Style = TryStyle("HelperText"),
                Margin = new Thickness(0, 10, 0, 0),
            };
            SetTheme(err, TextBlock.ForegroundProperty, "DangerBrush");
            panel.Children.Add(err);
        }

        return panel;
    }

    private static string FormatUpdateStatus(UpdateCheckResult? check)
    {
        if (check is null) return "Not checked yet";
        if (!check.Success) return check.Error ?? "Check failed";
        if (check.UpdateAvailable) return $"Update available ({check.LatestVersion})";
        if (!string.IsNullOrWhiteSpace(check.Error)) return check.Error;
        return "Up to date";
    }

    private static string FormatLastChecked(string? isoUtc)
    {
        if (string.IsNullOrWhiteSpace(isoUtc)) return "Never";
        if (DateTimeOffset.TryParse(isoUtc, out var dto))
            return dto.ToLocalTime().ToString("G");
        return isoUtc;
    }

    private UIElement BuildAbout()
    {
        var panel = new StackPanel();
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Assets/WinTAKTrackerLogo.png");
            bmp.DecodePixelWidth = 256;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();

            var logo = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = 112,
                Height = 112,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHAlign.Left,
                Margin = new Thickness(0, 0, 0, 12),
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            panel.Children.Add(logo);
        }
        catch { /* branding optional */ }

        var version = _host.Updates.CurrentVersion;
        var company = Assembly.GetExecutingAssembly()
                          .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
                      ?? "CopIX LLC";

        var verTb = new TextBlock
        {
            Text = $"WinTAKTracker {version}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        SetTheme(verTb, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(verTb);
        var companyTb = new TextBlock
        {
            Text = company,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        SetTheme(companyTb, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(companyTb);
        panel.Children.Add(Blurb(
            "Independent Windows PLI tracker. Not an official TAK Product Center application.\n\n" +
            "TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners.\n" +
            "Map tiles: © OpenStreetMap contributors.\n" +
            "Network location: ipwho.is (approximate IP geolocation).\n" +
            "License: WinTAKTracker Free Application License 1.0\n" +
            "(source available, free to use; no charging for the software).\n" +
            "See LICENSE in the repository.\n" +
            "Updates: github.com/CopIXus/WinTAKTracker"));
        return panel;
    }

    private void Persist() => _ = _host.SaveConfigAsync();

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
        var tb = new TextBlock
        {
            Text = $"{label}: {value}",
            FontSize = 13,
        };
        SetTheme(tb, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        border.Child = tb;
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

    /// <summary>Live DynamicResource binding so code-built UI follows light/dark swaps.</summary>
    private static void SetTheme(FrameworkElement element, DependencyProperty property, string resourceKey) =>
        element.SetResourceReference(property, resourceKey);

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* ignore */ }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    private static void Msg(string text, MessageBoxImage icon = MessageBoxImage.Information)
    {
        _ = icon;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        AppDialog.Show(owner, text);
    }

    private void PauseResumeButton_OnClick(object sender, RoutedEventArgs e) => _host.Pause.Toggle();
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
    private void OnPauseChanged(object? sender, bool paused) => Dispatcher.Invoke(RefreshPauseButton);
    private void RefreshPauseButton() =>
        PauseResumeButton.Content = _host.Pause.IsPaused ? "Resume tracking" : "Pause tracking";
}
