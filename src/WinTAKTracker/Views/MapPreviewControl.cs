using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Wpf;
using WinTAKTracker.Services.Gps;
using NtsPoint = NetTopologySuite.Geometries.Point;
using MapsuiPen = Mapsui.Styles.Pen;
using MapsuiBrush = Mapsui.Styles.Brush;
using MapsuiColor = Mapsui.Styles.Color;
using WpfColor = System.Windows.Media.Color;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace WinTAKTracker.Views;

/// <summary>Small OSM self-location preview (not a COP).</summary>
public sealed class MapPreviewControl : System.Windows.Controls.UserControl
{
    private readonly MapControl _mapControl;
    private readonly TextBlock _placeholder;
    private readonly MemoryLayer _markerLayer;
    private bool _follow = true;

    public MapPreviewControl()
    {
        _mapControl = new MapControl();
        _mapControl.Map?.Layers.Add(OpenStreetMap.CreateTileLayer());
        _markerLayer = new MemoryLayer("self")
        {
            Style = null,
            Features = [],
        };
        _mapControl.Map?.Layers.Add(_markerLayer);

        _placeholder = new TextBlock
        {
            Text = "Waiting for GPS…",
            HorizontalAlignment = WpfHAlign.Center,
            VerticalAlignment = WpfVAlign.Center,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x66, 0x66, 0x66)),
            FontSize = 14,
        };

        var attribution = new TextBlock
        {
            Text = "© OpenStreetMap contributors",
            FontSize = 10,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = WpfHAlign.Right,
            VerticalAlignment = WpfVAlign.Bottom,
            Margin = new Thickness(0, 0, 6, 4),
        };

        var root = new Grid { Height = 220 };
        root.Children.Add(_mapControl);
        root.Children.Add(_placeholder);
        root.Children.Add(attribution);
        Content = root;
    }

    public bool FollowMe
    {
        get => _follow;
        set => _follow = value;
    }

    public void UpdateFix(GpsFix? fix, string? teamColorName = null)
    {
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
            var resolutions = _mapControl.Map.Navigator.Resolutions;
            var resolution = resolutions.Count > 10 ? resolutions[10] : resolutions[^1];
            _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), resolution);
        }

        _mapControl.Refresh();
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
