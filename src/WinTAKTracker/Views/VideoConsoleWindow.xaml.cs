using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Video;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace WinTAKTracker.Views;

public partial class VideoConsoleWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, FeedTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    public VideoConsoleWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        StayOnTopCheck.IsChecked = _host.Config.Video.ConsoleStayOnTop;
        Topmost = _host.Config.Video.ConsoleStayOnTop;
        _host.Video.EnsureWorkersForConfig();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += (_, _) => RefreshTiles();
        Loaded += (_, _) =>
        {
            _refreshTimer.Start();
            RefreshTiles();
        };
        SizeChanged += (_, _) => LayoutTiles();
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
        if (_busy) return;
        _busy = true;
        try { await _host.Video.StartAllAsync(); }
        catch (Exception ex) { StatusLine.Text = ex.Message; }
        finally { _busy = false; }
        RefreshTiles();
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { await _host.Video.StopAllAsync(); }
        catch (Exception ex) { StatusLine.Text = ex.Message; }
        finally { _busy = false; }
        StatusLine.Text = "Stopped.";
        RefreshTiles();
    }

    private void RefreshTiles()
    {
        var feeds = _host.Config.Video.Feeds.Where(f => f.Enabled).ToList();
        var runtimes = _host.Video.SnapshotRuntimes().ToDictionary(r => r.FeedId, StringComparer.OrdinalIgnoreCase);
        var ids = feeds.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in _tiles.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            FeedGrid.Children.Remove(_tiles[stale].Root);
            _tiles.Remove(stale);
        }

        foreach (var feed in feeds)
        {
            if (!_tiles.TryGetValue(feed.Id, out var tile))
            {
                tile = CreateTile(feed.Id);
                _tiles[feed.Id] = tile;
                FeedGrid.Children.Add(tile.Root);
            }

            runtimes.TryGetValue(feed.Id, out var rt);
            var live = rt?.IsLive == true;
            var playable = rt?.IsPlayable == true;
            var name = string.IsNullOrWhiteSpace(feed.Tag) ? (feed.CameraName ?? feed.Id) : feed.Tag;
            tile.Title.Text = name;
            tile.Preview.Source = rt?.PreviewFrame;

            if (live)
            {
                tile.Toggle.Content = playable ? "LIVE" : "Starting…";
                tile.Toggle.Style = TryFindResource("DangerButton") as Style
                                    ?? TryFindResource("CompactDangerButton") as Style
                                    ?? tile.Toggle.Style;
                tile.Toggle.Background = (System.Windows.Media.Brush)(FindResource("DangerBrush")
                    ?? System.Windows.Media.Brushes.IndianRed);
                tile.Toggle.Foreground = (System.Windows.Media.Brush)(FindResource("OnAccentBrush")
                    ?? System.Windows.Media.Brushes.White);
            }
            else
            {
                tile.Toggle.Content = "Go LIVE";
                tile.Toggle.Style = TryFindResource("PrimaryButton") as Style
                                    ?? TryFindResource("CompactPrimaryButton") as Style
                                    ?? tile.Toggle.Style;
                tile.Toggle.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                tile.Toggle.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            }

            if (!string.IsNullOrWhiteSpace(rt?.LastError) && !playable)
            {
                tile.Error.Text = rt!.LastError!;
                tile.Error.Visibility = Visibility.Visible;
            }
            else
            {
                tile.Error.Text = "";
                tile.Error.Visibility = Visibility.Collapsed;
            }
        }

        LayoutTiles();

        if (feeds.Count == 0)
            StatusLine.Text = "No enabled camera feeds. Open Settings → Video to configure.";
        else if (_host.Video.LiveCount > 0)
        {
            var liveRt = runtimes.Values.FirstOrDefault(r => r.IsLive && !string.IsNullOrWhiteSpace(r.StreamUrl));
            StatusLine.Text = liveRt?.StreamUrl is { } url
                ? $"LIVE ×{_host.Video.LiveCount} — {url}"
                : $"LIVE ×{_host.Video.LiveCount}";
        }
        else if (string.IsNullOrWhiteSpace(StatusLine.Text) ||
                 StatusLine.Text.StartsWith("LIVE", StringComparison.OrdinalIgnoreCase))
        {
            StatusLine.Text = "Idle — click Go LIVE to start streaming.";
        }
    }

    private void LayoutTiles()
    {
        var n = _tiles.Count;
        if (n == 0)
        {
            FeedGrid.RowDefinitions.Clear();
            FeedGrid.ColumnDefinitions.Clear();
            return;
        }

        var cols = n <= 1 ? 1 : 2;
        var rows = (int)Math.Ceiling(n / (double)cols);
        while (FeedGrid.ColumnDefinitions.Count < cols)
            FeedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        while (FeedGrid.ColumnDefinitions.Count > cols)
            FeedGrid.ColumnDefinitions.RemoveAt(FeedGrid.ColumnDefinitions.Count - 1);
        while (FeedGrid.RowDefinitions.Count < rows)
            FeedGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        while (FeedGrid.RowDefinitions.Count > rows)
            FeedGrid.RowDefinitions.RemoveAt(FeedGrid.RowDefinitions.Count - 1);

        var i = 0;
        foreach (var tile in _tiles.Values)
        {
            Grid.SetColumn(tile.Root, i % cols);
            Grid.SetRow(tile.Root, i / cols);
            i++;
        }
    }

    private FeedTile CreateTile(string feedId)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(10),
            MinHeight = 160,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceSubtleBrush");

        var root = new DockPanel { LastChildFill = true };

        var title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var err = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        err.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        DockPanel.SetDock(err, Dock.Bottom);

        var toggle = new Button
        {
            Content = "Go LIVE",
            MinWidth = 120,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = WpfHAlign.Left,
        };
        toggle.SetResourceReference(StyleProperty, "PrimaryButton");
        toggle.Click += async (_, _) => await ToggleFeedAsync(feedId);
        DockPanel.SetDock(toggle, Dock.Bottom);

        root.Children.Add(err);
        root.Children.Add(toggle);

        var previewHost = new Border
        {
            Background = System.Windows.Media.Brushes.Black,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            MinHeight = 120,
        };
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = WpfHAlign.Center,
            VerticalAlignment = WpfVAlign.Center,
        };
        previewHost.Child = img;
        root.Children.Add(previewHost);

        border.Child = root;
        return new FeedTile(border, title, img, toggle, err);
    }

    private async Task ToggleFeedAsync(string feedId)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var live = _host.Video.SnapshotRuntimes().Any(r => r.FeedId == feedId && r.IsLive);
            if (live)
            {
                await _host.Video.StopFeedAsync(feedId);
                StatusLine.Text = "Stopped.";
            }
            else
            {
                await _host.Video.StartFeedAsync(feedId);
                var rt = _host.Video.SnapshotRuntimes().FirstOrDefault(r => r.FeedId == feedId);
                StatusLine.Text = rt?.StreamUrl ?? "Streaming…";
            }
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshTiles();
        }
    }

    private sealed class FeedTile(
        Border root,
        TextBlock title,
        Image preview,
        Button toggle,
        TextBlock error)
    {
        public Border Root { get; } = root;
        public TextBlock Title { get; } = title;
        public Image Preview { get; } = preview;
        public Button Toggle { get; } = toggle;
        public TextBlock Error { get; } = error;
    }
}
