using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CodexBar.App;

// The open notch's material, after macOS "Search or Ask": solid black where it meets the screen edge,
// thinning into frosted glass away from it, with a 1 px rim that is brightest along the far edge.
// The frost is the desktop under the notch, grabbed once as the notch opens (it is still closed then,
// so it is not in the picture), shrunk to a sixteenth and scaled back up, which is a cheap blur. It is
// a still picture kept in memory only, and it is dropped when the notch closes. Closed, the notch stays black.
public partial class NotchWindow
{
    private const double FrostWidth = 680, FrostHeight = 600;   // covers the widest and tallest page

    private Brush? _glassTint, _glassRim;
    private string? _glassEdge;
    private ImageBrush? _frost;
    private Rect _frostArea;                       // DIPs, screen coordinates
    private (double X, double Y) _frostScale = (1, 1);   // picture pixels per DIP

    private void ApplyShellMaterial(bool expanded, double windowLeft, double windowTop, double width, double height)
    {
        if (IsCorner)
        {
            return;
        }

        if (!expanded)
        {
            Shell.Background = Brushes.Black;
            ShellTint.Visibility = Visibility.Collapsed;
            ShellRim.Visibility = Visibility.Collapsed;
            _frost = null;
            return;
        }

        EnsureGlassBrushes();
        if (Edge == "top")
        {
            // The shape hangs centred in the window, from its top edge.
            var centreX = windowLeft + (ActualWidth > 0 ? ActualWidth : Width) / 2;
            _frost ??= CaptureFrost(centreX, windowTop);
            if (_frost is not null)
            {
                _frost.Viewbox = new Rect((centreX - width / 2 - _frostArea.Left) * _frostScale.X, (windowTop - _frostArea.Top) * _frostScale.Y,
                    width * _frostScale.X, height * _frostScale.Y);
            }
        }

        Shell.Background = (Brush?)_frost ?? Brushes.Black;
        ShellTint.Background = _glassTint;
        ShellTint.Visibility = Visibility.Visible;
        ShellRim.BorderBrush = _glassRim;
        ShellRim.Visibility = Visibility.Visible;
    }

    private void EnsureGlassBrushes()
    {
        if (_glassEdge == Edge)
        {
            return;
        }

        _glassEdge = Edge;
        // Gradients run from the attached edge outward.
        var (start, end) = Edge switch
        {
            "bottom" => (new Point(0, 1), new Point(0, 0)),
            "left" => (new Point(0, 0), new Point(1, 0)),
            "right" => (new Point(1, 0), new Point(0, 0)),
            _ => (new Point(0, 0), new Point(0, 1))
        };
        _glassTint = Gradient(start, end,
            (Color.FromArgb(0xFF, 0, 0, 0), 0),
            (Color.FromArgb(0xB3, 0, 0, 0), 0.25),
            (Color.FromArgb(0x66, 0x06, 0x06, 0x08), 0.6),
            (Color.FromArgb(0x4D, 0x0C, 0x0C, 0x0E), 1));
        _glassRim = Gradient(start, end,
            (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
            (Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.4),
            (Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 0.75),
            (Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 1));
    }

    /// <summary>A blurred still of the desktop below the notch, or null if it cannot be read.</summary>
    private ImageBrush? CaptureFrost(double centreX, double top)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _frostArea = new Rect(centreX - FrostWidth / 2, top, FrostWidth, FrostHeight);
            var pw = (int)Math.Round(FrostWidth * dpi.DpiScaleX);
            var ph = (int)Math.Round(FrostHeight * dpi.DpiScaleY);

            using var full = new System.Drawing.Bitmap(pw, ph);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                g.CopyFromScreen((int)Math.Round(_frostArea.Left * dpi.DpiScaleX), (int)Math.Round(_frostArea.Top * dpi.DpiScaleY), 0, 0, full.Size);
            }

            // Down to a sixteenth and back up to a quarter: smooth enough to read as frosted glass.
            using var tiny = Resample(full, Math.Max(1, pw / 16), Math.Max(1, ph / 16));
            using var soft = Resample(tiny, Math.Max(1, pw / 4), Math.Max(1, ph / 4));
            var data = soft.LockBits(new System.Drawing.Rectangle(0, 0, soft.Width, soft.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var source = BitmapSource.Create(soft.Width, soft.Height, 96, 96, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * soft.Height, data.Stride);
            soft.UnlockBits(data);
            source.Freeze();

            _frostScale = (soft.Width / FrostWidth, soft.Height / FrostHeight);
            return new ImageBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Glass", exception.Message);
            return null;
        }
    }

    private static System.Drawing.Bitmap Resample(System.Drawing.Image source, int width, int height)
    {
        var target = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = System.Drawing.Graphics.FromImage(target);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));
        return target;
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
