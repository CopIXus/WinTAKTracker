using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPath = System.Windows.Shapes.Path;

namespace WinTAKTracker.Views;

/// <summary>
/// Outline icons (24×24 viewBox) for Settings sidebar + Status tiles.
/// Stroke-based — always set <see cref="Shape.Stroke"/> after create.
/// </summary>
public static class UiIcons
{
    // Distinctive Heroicons-style outlines (simplified for WPF Geometry.Parse).
    private static readonly Dictionary<string, string> Geometries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Sidebar / general
        ["IconStatus"] = "M3.5,12 H7.5 L9.5,6.5 L12.5,17.5 L15,9.5 L17,12 H20.5",
        ["IconServers"] =
            "M4,5 H20 V9.5 H4 Z M4,11 H20 V15.5 H4 Z M4,17 H20 V21.5 H4 Z M7,7 H8.5 M7,13 H8.5 M7,19 H8.5",
        ["IconIdentity"] =
            "M7,4 H17 A2,2 0 0 1 19,6 V9 H5 V6 A2,2 0 0 1 7,4 Z M5,10.5 H19 V18 A2,2 0 0 1 17,20 H7 A2,2 0 0 1 5,18 Z M8.5,15 H15.5",
        ["IconGps"] = "M12,2.5 L14.8,9.2 L21.5,12 L14.8,14.8 L12,21.5 L9.2,14.8 L2.5,12 L9.2,9.2 Z",
        ["IconVideo"] =
            "M4,7 H14 A2,2 0 0 1 16,9 V15 A2,2 0 0 1 14,17 H4 A2,2 0 0 1 2,15 V9 A2,2 0 0 1 4,7 Z M16,10.5 L21,8 V16 L16,13.5 Z",
        ["IconReporting"] =
            "M5,4.5 H14.5 A2,2 0 0 1 16.5,6.5 V12.5 A2,2 0 0 1 14.5,14.5 H10.5 L7.5,17.5 V14.5 H5 A2,2 0 0 1 3,12.5 V6.5 A2,2 0 0 1 5,4.5 Z",
        ["IconMesh"] =
            "M12,3.5 A2.2,2.2 0 1 0 12,7.9 A2.2,2.2 0 1 0 12,3.5 Z M4.5,15.5 A2.2,2.2 0 1 0 4.5,19.9 A2.2,2.2 0 1 0 4.5,15.5 Z M19.5,15.5 A2.2,2.2 0 1 0 19.5,19.9 A2.2,2.2 0 1 0 19.5,15.5 Z M11,7.5 L5.8,15.8 M13,7.5 L18.2,15.8",
        ["IconCompanions"] =
            "M3.5,3.5 H10 V10 H3.5 Z M14,3.5 H20.5 V10 H14 Z M3.5,14 H10 V20.5 H3.5 Z M14,14 H20.5 V20.5 H14 Z",
        ["IconStartup"] = "M12,2.5 V11.5 M7.2,6.8 A6.8,6.8 0 1 0 16.8,6.8",
        ["IconDiagnostics"] =
            "M4,18.5 V11 H8 V18.5 Z M10,18.5 V6.5 H14 V18.5 Z M16,18.5 V13 H20 V18.5 Z M3,20.5 H21",
        ["IconUpdates"] = "M16.5,7.5 L12,3 L12,7.5 H16.5 M7.5,8 A6.5,6.5 0 1 0 12,5.5",
        ["IconAbout"] =
            "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,10.5 V16.5 M12,7.2 V8.2",

        // Status tiles
        ["IconTracking"] = "M3.5,12 H8 L10,6.5 L13.5,17.5 L16,10.5 H20.5",
        ["IconPosition"] =
            "M12,2.8 C8.2,2.8 5.2,6 5.2,10 C5.2,15.2 12,21.2 12,21.2 C12,21.2 18.8,15.2 18.8,10 C18.8,6 15.8,2.8 12,2.8 Z M12,7.5 A2.5,2.5 0 1 0 12,12.5 A2.5,2.5 0 1 0 12,7.5 Z",
        ["IconMotion"] =
            "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,12 L16.5,7.5 M7.5,16 H11",
        ["IconClock"] =
            "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7 V12.2 L15.8,14.5",
        ["IconCallsign"] =
            "M12,3.5 A3.8,3.8 0 1 0 12,11.1 A3.8,3.8 0 1 0 12,3.5 Z M5,20.5 C5,16.2 8,13.5 12,13.5 C16,13.5 19,16.2 19,20.5",
        ["IconChip"] =
            "M8,7.5 H16 A1.5,1.5 0 0 1 17.5,9 V15 A1.5,1.5 0 0 1 16,16.5 H8 A1.5,1.5 0 0 1 6.5,15 V9 A1.5,1.5 0 0 1 8,7.5 Z M9,4.5 V7.5 M12,4.5 V7.5 M15,4.5 V7.5 M9,16.5 V19.5 M12,16.5 V19.5 M15,16.5 V19.5",
        ["IconShield"] = "M12,2.8 L19.5,6 V11.2 C19.5,15.8 16.2,18.8 12,21.2 C7.8,18.8 4.5,15.8 4.5,11.2 V6 Z",
        ["IconGear"] =
            "M12,8.2 A3.8,3.8 0 1 0 12,15.8 A3.8,3.8 0 1 0 12,8.2 Z M12,3 V5.8 M12,18.2 V21 M3,12 H5.8 M18.2,12 H21 M5.8,5.8 L7.8,7.8 M16.2,16.2 L18.2,18.2 M18.2,5.8 L16.2,7.8 M7.8,16.2 L5.8,18.2",
        ["IconTrash"] =
            "M4,7 H20 M9,7 V4.5 A1,1 0 0 1 10,3.5 H14 A1,1 0 0 1 15,4.5 V7 M6.5,7 L7.4,19 A1.5,1.5 0 0 0 8.9,20.5 H15.1 A1.5,1.5 0 0 0 16.6,19 L17.5,7 M10,11 V16.5 M14,11 V16.5",
    };

    public static WpfPath Create(string key, double strokeThickness = 1.7, string? strokeResourceKey = null)
    {
        if (!Geometries.TryGetValue(key, out var data))
            data = "M4,12 H20";

        var path = new WpfPath
        {
            Data = Geometry.Parse(data),
            Fill = WpfBrushes.Transparent,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 24,
            Height = 24,
            SnapsToDevicePixels = true,
        };

        var brushKey = strokeResourceKey ?? "TextPrimaryBrush";
        if (Application.Current?.TryFindResource(brushKey) is WpfBrush brush)
            path.Stroke = brush;
        else
            path.Stroke = WpfBrushes.White;

        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        return path;
    }

    public static FrameworkElement Boxed(string key, double size, double strokeThickness = 1.7, string? strokeResourceKey = null)
    {
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = Create(key, strokeThickness, strokeResourceKey),
            SnapsToDevicePixels = true,
        };
    }
}
