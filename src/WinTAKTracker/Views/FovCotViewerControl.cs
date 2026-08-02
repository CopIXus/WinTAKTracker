using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Theme;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace WinTAKTracker.Views;

/// <summary>Top-down FOV wedges for aiming multi-camera feeds (Settings setup).</summary>
public sealed class FovCotViewerControl : UserControl
{
    private readonly Canvas _canvas;
    private readonly TextBlock _placeholder;
    private AppConfig? _config;
    private string? _selectedFeedId;
    private double? _lat;
    private double? _lon;
    private double? _course;

    public event EventHandler<string>? FeedSelected;

    public FovCotViewerControl()
    {
        var root = new Grid { Height = 220 };
        _canvas = new Canvas();
        _canvas.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "SurfaceBrush");
        _placeholder = new TextBlock
        {
            Text = "Waiting for GPS fix to aim FOV…",
            HorizontalAlignment = WpfHAlign.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        root.Children.Add(_canvas);
        root.Children.Add(_placeholder);
        Content = root;
        SizeChanged += (_, _) => Redraw();
    }

    public void Update(
        AppConfig config,
        string? selectedFeedId,
        double? lat,
        double? lon,
        double? courseDegrees)
    {
        _config = config;
        _selectedFeedId = selectedFeedId;
        _lat = lat;
        _lon = lon;
        _course = courseDegrees;
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        if (_config is null || ActualWidth < 10 || ActualHeight < 10)
            return;

        var feeds = _config.Video.Feeds.Where(f => f.Enabled).ToList();
        if (feeds.Count == 0)
        {
            _placeholder.Text = "Add a camera feed to preview FOV wedges.";
            _placeholder.Visibility = Visibility.Visible;
            return;
        }

        _placeholder.Visibility = _lat is null ? Visibility.Visible : Visibility.Collapsed;
        if (_lat is null)
            _placeholder.Text = "Waiting for GPS fix to aim FOV…";

        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var maxRange = Math.Max(10, feeds.Max(f => f.RangeMeters));
        var scale = Math.Min(ActualWidth, ActualHeight) * 0.42 / maxRange;

        // Course line
        var course = CotEventBuilder.NormalizeCourse((_course ?? 0) + _config.Gps.CourseOffsetDegrees);
        DrawRay(cx, cy, course, maxRange * scale * 1.05, WpfBrushes.Gray, 1.5, dash: true);

        var colors = new[]
        {
            WpfColor.FromRgb(40, 180, 200),
            WpfColor.FromRgb(230, 130, 30),
            WpfColor.FromRgb(140, 60, 180),
            WpfColor.FromRgb(40, 160, 70),
        };

        for (var i = 0; i < feeds.Count; i++)
        {
            var feed = feeds[i];
            var az = CotVideoBuilder.ResolveAzimuth(_config, feed, _course);
            var color = colors[i % colors.Length];
            var selected = string.Equals(feed.Id, _selectedFeedId, StringComparison.OrdinalIgnoreCase);
            DrawWedge(cx, cy, az, feed.HfovDegrees, feed.RangeMeters * scale, color, selected, feed.Id, feed.Tag);
        }

        // Self dot
        var self = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = ThemeManager.ReadAppsUseLightTheme() ? WpfBrushes.Black : WpfBrushes.White,
        };
        Canvas.SetLeft(self, cx - 5);
        Canvas.SetTop(self, cy - 5);
        _canvas.Children.Add(self);
    }

    private void DrawWedge(
        double cx, double cy, double azimuth, double hfov, double radius,
        WpfColor color, bool selected, string feedId, string tag)
    {
        var half = hfov / 2;
        var start = azimuth - half;
        var end = azimuth + half;
        var figure = new PathFigure { StartPoint = new System.Windows.Point(cx, cy), IsClosed = true };
        figure.Segments.Add(new LineSegment(Polar(cx, cy, start, radius), true));
        // Approximate arc with line segments
        for (var a = start; a <= end; a += Math.Max(2, hfov / 12))
            figure.Segments.Add(new LineSegment(Polar(cx, cy, a, radius), true));
        figure.Segments.Add(new LineSegment(Polar(cx, cy, end, radius), true));

        var geo = new PathGeometry([figure]);
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = new SolidColorBrush(WpfColor.FromArgb(selected ? (byte)90 : (byte)50, color.R, color.G, color.B)),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = selected ? 2.5 : 1.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = feedId,
        };
        path.MouseLeftButtonUp += (_, _) => FeedSelected?.Invoke(this, feedId);
        _canvas.Children.Add(path);

        var tip = Polar(cx, cy, azimuth, radius * 0.85);
        var label = new TextBlock
        {
            Text = tag,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
        };
        Canvas.SetLeft(label, tip.X + 4);
        Canvas.SetTop(label, tip.Y - 8);
        _canvas.Children.Add(label);
    }

    private void DrawRay(double cx, double cy, double azimuth, double length, WpfBrush brush, double thickness, bool dash)
    {
        var tip = Polar(cx, cy, azimuth, length);
        var line = new Line
        {
            X1 = cx,
            Y1 = cy,
            X2 = tip.X,
            Y2 = tip.Y,
            Stroke = brush,
            StrokeThickness = thickness,
        };
        if (dash)
            line.StrokeDashArray = [4, 3];
        _canvas.Children.Add(line);
    }

    /// <summary>Screen Y increases downward; heading 0° = north (up).</summary>
    private static System.Windows.Point Polar(double cx, double cy, double headingDeg, double radius)
    {
        var rad = headingDeg * Math.PI / 180.0;
        var x = cx + radius * Math.Sin(rad);
        var y = cy - radius * Math.Cos(rad);
        return new System.Windows.Point(x, y);
    }
}
