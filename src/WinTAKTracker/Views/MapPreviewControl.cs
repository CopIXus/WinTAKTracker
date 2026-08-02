using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Wpf;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Theme;
using NtsPoint = NetTopologySuite.Geometries.Point;
using MapsuiPen = Mapsui.Styles.Pen;
using MapsuiBrush = Mapsui.Styles.Brush;
using MapsuiColor = Mapsui.Styles.Color;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;
using WpfButton = System.Windows.Controls.Button;

namespace WinTAKTracker.Views;

/// <summary>Small self-location preview (not a COP). OSM in light theme, Carto dark in dark theme.</summary>
public sealed class MapPreviewControl : System.Windows.Controls.UserControl
{
    private readonly MapControl _mapControl;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _attribution;
    private readonly MemoryLayer _markerLayer;
    private TileLayer? _basemapLayer;
    private bool _follow = true;
    private bool _userZoomed;
    private bool? _lightBasemap;

    public MapPreviewControl()
    {
        _mapControl = new MapControl();
        // Stop wheel-zoom so Settings page scroll keeps working when the pointer is over the map.
        _mapControl.PreviewMouseWheel += OnMapPreviewMouseWheel;

        _markerLayer = new MemoryLayer("self")
        {
            Style = null,
            Features = [],
        };

        ApplyBasemap(ThemeManager.ReadAppsUseLightTheme());
        _mapControl.Map?.Layers.Add(_markerLayer);

        _placeholder = new TextBlock
        {
            Text = "Waiting for GPS…",
            HorizontalAlignment = WpfHAlign.Center,
            VerticalAlignment = WpfVAlign.Center,
            FontSize = 14,
            IsHitTestVisible = false,
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        _attribution = new TextBlock
        {
            FontSize = 10,
            HorizontalAlignment = WpfHAlign.Right,
            VerticalAlignment = WpfVAlign.Bottom,
            Margin = new Thickness(0, 0, 6, 4),
            IsHitTestVisible = false,
        };
        _attribution.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        UpdateAttributionText();

        var zoomPanel = BuildZoomButtons();

        var root = new Grid { Height = 220 };
        root.Children.Add(_mapControl);
        root.Children.Add(_placeholder);
        root.Children.Add(zoomPanel);
        root.Children.Add(_attribution);
        Content = root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool FollowMe
    {
        get => _follow;
        set => _follow = value;
    }

    public void UpdateFix(GpsFix? fix, string? teamColorName = null)
    {
        EnsureBasemapMatchesTheme();

        if (fix is null)
        {
            _placeholder.Visibility = Visibility.Visible;
            _markerLayer.Features = [];
            _mapControl.Refresh();
            return;
        }

        _placeholder.Visibility = Visibility.Collapsed;
        var (x, y) = SphericalMercator.FromLonLat(fix.Longitude, fix.Latitude);
        var color = TeamToColor(teamColorName, fix.IsHeld);
        var feature = new GeometryFeature
        {
            Geometry = new NtsPoint(x, y),
        };
        feature.Styles.Add(new SymbolStyle
        {
            SymbolScale = 0.6,
            Fill = new MapsuiBrush(color),
            Outline = new MapsuiPen(MapsuiColor.White, 2),
        });

        _markerLayer.Features = [feature];

        if (_follow && _mapControl.Map is not null)
        {
            var point = new MPoint(x, y);
            if (_userZoomed)
            {
                _mapControl.Map.Navigator.CenterOn(point);
            }
            else
            {
                var resolutions = _mapControl.Map.Navigator.Resolutions;
                var resolution = resolutions.Count > 10 ? resolutions[10] : resolutions[^1];
                _mapControl.Map.Navigator.CenterOnAndZoomTo(point, resolution);
            }
        }

        _mapControl.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.Current is { } theme)
            theme.ThemeChanged += OnThemeChanged;
        EnsureBasemapMatchesTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.Current is { } theme)
            theme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(EnsureBasemapMatchesTheme);

    private void EnsureBasemapMatchesTheme()
    {
        var light = ThemeManager.Current?.IsLightTheme ?? ThemeManager.ReadAppsUseLightTheme();
        if (_lightBasemap == light) return;
        ApplyBasemap(light);
        UpdateAttributionText();
        _mapControl.Refresh();
    }

    private void ApplyBasemap(bool light)
    {
        var map = _mapControl.Map;
        if (map is null) return;

        if (_basemapLayer is not null)
            map.Layers.Remove(_basemapLayer);

        _basemapLayer = light ? CreateOsmLayer() : CreateDarkLayer();
        // Keep basemap under the marker layer.
        var markerIndex = -1;
        for (var i = 0; i < map.Layers.Count; i++)
        {
            if (ReferenceEquals(map.Layers[i], _markerLayer))
            {
                markerIndex = i;
                break;
            }
        }

        if (markerIndex >= 0)
            map.Layers.Insert(markerIndex, _basemapLayer);
        else
            map.Layers.Insert(0, _basemapLayer);

        _lightBasemap = light;
    }

    private static TileLayer CreateOsmLayer()
    {
        var source = new HttpTileSource(
            new GlobalSphericalMercator(),
            "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            name: "OpenStreetMap",
            attribution: new Attribution(
                "© OpenStreetMap contributors",
                "https://www.openstreetmap.org/copyright"),
            userAgent: "WinTAKTracker");
        return new TileLayer(source) { Name = "OpenStreetMap" };
    }

    private static TileLayer CreateDarkLayer()
    {
        var source = new HttpTileSource(
            new GlobalSphericalMercator(),
            "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
            ["a", "b", "c", "d"],
            name: "Carto Dark Matter",
            attribution: new Attribution(
                "© OpenStreetMap contributors © CARTO",
                "https://carto.com/attributions"),
            userAgent: "WinTAKTracker");
        return new TileLayer(source) { Name = "CartoDark" };
    }

    private void UpdateAttributionText() =>
        _attribution.Text = _lightBasemap == false
            ? "© OpenStreetMap · © CARTO"
            : "© OpenStreetMap contributors";

    private StackPanel BuildZoomButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = WpfHAlign.Right,
            VerticalAlignment = WpfVAlign.Top,
            Margin = new Thickness(0, 10, 10, 0),
        };

        panel.Children.Add(MakeZoomButton("+", ZoomIn));
        panel.Children.Add(new Border { Height = 6 });
        panel.Children.Add(MakeZoomButton("−", ZoomOut));
        return panel;
    }

    private WpfButton MakeZoomButton(string label, Action action)
    {
        var btn = new WpfButton
        {
            Content = label,
            Width = 32,
            Height = 32,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = label == "+" ? "Zoom in" : "Zoom out",
        };
        btn.SetResourceReference(StyleProperty, "IconButton");
        btn.Click += (_, _) => action();
        return btn;
    }

    private void ZoomIn()
    {
        _userZoomed = true;
        _mapControl.Map?.Navigator.ZoomIn(280);
        _mapControl.Refresh();
    }

    private void ZoomOut()
    {
        _userZoomed = true;
        _mapControl.Map?.Navigator.ZoomOut(280);
        _mapControl.Refresh();
    }

    private void OnMapPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Consume the event so Mapsui does not zoom, then scroll the parent Settings page.
        e.Handled = true;
        var scroll = FindParentScrollViewer(this);
        if (scroll is null) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is ScrollViewer sv) return sv;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static MapsuiColor TeamToColor(string? team, bool held)
    {
        var baseColor = (team ?? "").ToLowerInvariant() switch
        {
            "red" => MapsuiColor.FromArgb(255, 200, 40, 40),
            "blue" => MapsuiColor.FromArgb(255, 40, 90, 200),
            "green" => MapsuiColor.FromArgb(255, 40, 160, 70),
            "yellow" => MapsuiColor.FromArgb(255, 220, 190, 40),
            "orange" => MapsuiColor.FromArgb(255, 230, 130, 30),
            "purple" => MapsuiColor.FromArgb(255, 140, 60, 180),
            "cyan" => MapsuiColor.FromArgb(255, 40, 180, 200),
            "magenta" => MapsuiColor.FromArgb(255, 200, 50, 160),
            "white" => MapsuiColor.FromArgb(255, 230, 230, 230),
            "maroon" => MapsuiColor.FromArgb(255, 120, 30, 40),
            "teal" => MapsuiColor.FromArgb(255, 30, 140, 140),
            _ => MapsuiColor.FromArgb(255, 40, 180, 200),
        };

        if (held)
            return MapsuiColor.FromArgb(160, baseColor.R, baseColor.G, baseColor.B);
        return baseColor;
    }
}
