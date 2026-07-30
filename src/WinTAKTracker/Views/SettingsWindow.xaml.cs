using System.Windows;
using System.Windows.Controls;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Tray;

namespace WinTAKTracker.Views;

public partial class SettingsWindow : Window
{
    private readonly PauseService _pauseService;
    private readonly TrayIconService _trayIconService;

    public SettingsWindow(PauseService pauseService, TrayIconService trayIconService)
    {
        _pauseService = pauseService;
        _trayIconService = trayIconService;
        InitializeComponent();
        _pauseService.PauseChanged += OnPauseChanged;
        Closed += (_, _) => _pauseService.PauseChanged -= OnPauseChanged;
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

        SectionContent.Children.Add(CreatePlaceholder(tag));
    }

    private UIElement CreatePlaceholder(string tag)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = SectionBlurb(tag),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 16),
        });

        if (tag == "Status")
        {
            panel.Children.Add(Chip("Tray", _trayIconService.CurrentState.ToTooltip()));
            panel.Children.Add(Chip("Tracking", _pauseService.IsPaused ? "Paused" : "Active (stub)"));
            panel.Children.Add(Chip("GPS", "Not connected (Phase 2)"));
            panel.Children.Add(Chip("Servers", "None (Phase 3)"));
            panel.Children.Add(Chip("Mesh SA", "Not broadcasting (Phase 3)"));
        }
        else if (tag == "About")
        {
            panel.Children.Add(new TextBlock
            {
                Text = "WinTAKTracker 0.1.0 — independent Windows PLI tracker.\n" +
                       "Not an official TAK Product Center application.\n" +
                       "TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners.\n" +
                       "Map tiles (later): © OpenStreetMap contributors.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        return panel;
    }

    private static string SectionBlurb(string tag) => tag switch
    {
        "Status" => "GPS details, connection chips, and map preview will appear here.",
        "Servers" => "Enroll, import SoftCert/p12, multi-server TLS/TCP — Phase 3.",
        "Identity" => "Callsign, team, role, and CoT device type — Phase 5.",
        "Gps" => "NMEA serial and Windows Location GPS — Phase 2.",
        "Reporting" => "Adaptive / constant CoT reporting rates — Phase 2.",
        "MeshSa" => "ATAK-compatible UDP Mesh SA multicast — Phase 3.",
        "ViewMap" => "CloudTAK URL and companion app links (ATAK / TAK Aware / WinTAK) — Phase 5.",
        "Startup" => "Start with Windows and power options — Phase 5.",
        "Diagnostics" => "Logs, redacted export, log folder — Phase 5.",
        "Updates" => "GitHub Releases updater — later phase.",
        "About" => "Version, license, and attributions.",
        _ => "Coming soon.",
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
        border.Child = new TextBlock
        {
            Text = $"{label}: {value}",
            FontSize = 13,
        };
        return border;
    }

    private void PauseResumeButton_OnClick(object sender, RoutedEventArgs e) => _pauseService.Toggle();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnPauseChanged(object? sender, bool paused) =>
        Dispatcher.Invoke(RefreshPauseButton);

    private void RefreshPauseButton() =>
        PauseResumeButton.Content = _pauseService.IsPaused ? "Resume tracking" : "Pause tracking";
}
