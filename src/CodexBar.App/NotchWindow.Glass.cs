using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CodexBar.App;

// The open notch's material, after macOS "Search or Ask": solid black where it meets the screen edge,
// thinning into dark glass away from it, with a 1 px rim that is brightest along the far edge.
// Closed, the notch stays plain black so it still reads as part of the bezel.
public partial class NotchWindow
{
    private Brush? _glassFill, _glassRim;
    private string? _glassEdge;

    private void ApplyShellMaterial(bool expanded)
    {
        if (IsCorner)
        {
            return;
        }

        if (!expanded)
        {
            Shell.Background = Brushes.Black;
            ShellRim.Visibility = Visibility.Collapsed;
            return;
        }

        if (_glassEdge != Edge)
        {
            _glassEdge = Edge;
            // Gradients run from the attached edge outward.
            var (start, end) = Edge switch
            {
                "bottom" => (new Point(0, 1), new Point(0, 0)),
                "left" => (new Point(0, 0), new Point(1, 0)),
                "right" => (new Point(1, 0), new Point(0, 0)),
                _ => (new Point(0, 0), new Point(0, 1))
            };
            _glassFill = Gradient(start, end,
                (Color.FromArgb(0xFF, 0, 0, 0), 0),
                (Color.FromArgb(0xF5, 0, 0, 0), 0.35),
                (Color.FromArgb(0xC8, 0x0C, 0x0C, 0x0E), 1));
            _glassRim = Gradient(start, end,
                (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                (Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.4),
                (Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0.75),
                (Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 1));
        }

        Shell.Background = _glassFill;
        ShellRim.BorderBrush = _glassRim;
        ShellRim.Visibility = Visibility.Visible;
    }

    private static Brush Gradient(Point start, Point end, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }
}
