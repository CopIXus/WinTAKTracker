using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Video;
using WpfHAlign = System.Windows.HorizontalAlignment;

namespace WinTAKTracker.Views;

public partial class VideoConsoleWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;

    public VideoConsoleWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        StayOnTopCheck.IsChecked = _host.Config.Video.ConsoleStayOnTop;
        Topmost = _host.Config.Video.ConsoleStayOnTop;
        _host.Video.EnsureWorkersForConfig();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RebuildTiles();
        Loaded += (_, _) =>
        {
            _refreshTimer.Start();
            RebuildTiles();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            if (_host.Config.Video.StopStreamsWhenConsoleCloses)
                _ = _host.Video.StopAllAsync();
        };
    }

    private void StayOnTop_Changed(object sender, RoutedEventArgs e)
    {
        var on = StayOnTopCheck.IsChecked == true;
        Topmost = on;
        _host.Config.Video.ConsoleStayOnTop = on;
        _ = _host.SaveConfigAsync();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        try { await _host.Video.StartAllAsync(); }
        catch (Exception ex) { StatusLine.Text = ex.Message; }
        RebuildTiles();
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        await _host.Video.StopAllAsync();
        RebuildTiles();
    }

    private void RebuildTiles()
    {
        var feeds = _host.Config.Video.Feeds.Where(f => f.Enabled).ToList();
        var runtimes = _host.Video.SnapshotRuntimes().ToDictionary(r => r.FeedId, StringComparer.OrdinalIgnoreCase);
        FeedGrid.Children.Clear();
        FeedGrid.Columns = feeds.Count <= 1 ? 1 : 2;

        foreach (var feed in feeds)
        {
            runtimes.TryGetValue(feed.Id, out var rt);
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10),
                MinHeight = 180,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceSubtleBrush");

            var stack = new StackPanel();
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(feed.Tag) ? feed.CameraName ?? feed.Id : feed.Tag,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var live = new TextBlock
            {
                Text = rt?.IsLive == true ? "LIVE" : "Idle",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = WpfHAlign.Right,
            };
            live.SetResourceReference(TextBlock.ForegroundProperty,
                rt?.IsLive == true ? "DangerBrush" : "TextMutedBrush");
            DockPanel.SetDock(live, Dock.Right);
            header.Children.Add(live);
            header.Children.Add(title);
            stack.Children.Add(header);

            var img = new Image
            {
                Height = 140,
                Stretch = Stretch.UniformToFill,
                Source = rt?.PreviewFrame,
            };
            stack.Children.Add(img);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var start = new Button { Content = "Start", Margin = new Thickness(0, 0, 8, 0) };
            start.SetResourceReference(StyleProperty, "CompactPrimaryButton");
            var stop = new Button { Content = "Stop" };
            stop.SetResourceReference(StyleProperty, "CompactButton");
            var feedId = feed.Id;
            start.Click += async (_, _) =>
            {
                try
                {
                    await _host.Video.StartFeedAsync(feedId);
                    StatusLine.Text = rt?.StreamUrl ?? "Streaming…";
                }
                catch (Exception ex)
                {
                    StatusLine.Text = ex.Message;
                }
            };
            stop.Click += async (_, _) =>
            {
                await _host.Video.StopFeedAsync(feedId);
                StatusLine.Text = "Stopped.";
            };
            row.Children.Add(start);
            row.Children.Add(stop);
            stack.Children.Add(row);

            if (!string.IsNullOrWhiteSpace(rt?.LastError))
            {
                var err = new TextBlock
                {
                    Text = rt.LastError,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                    FontSize = 11,
                };
                err.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                stack.Children.Add(err);
            }

            border.Child = stack;
            FeedGrid.Children.Add(border);
        }

        if (feeds.Count == 0)
            StatusLine.Text = "No enabled camera feeds. Open Settings → Video to configure.";
        else if (_host.Video.LiveCount > 0)
            StatusLine.Text = $"LIVE ×{_host.Video.LiveCount}";
    }
}
