using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

/// <summary>
/// The corner-docked notch: an L hugging two screen edges. Drawn in "top-left" space and
/// mirrored for the other corners. <see cref="ArmH"/> is the thickness of the arm lying along the
/// horizontal edge, <see cref="ArmV"/> the thickness of the arm along the vertical edge; the arms'
/// lengths are the element's own width and height. When both thicknesses grow to the full size
/// the L fills in to a rectangle — which is how the expand animation morphs the corner notch into
/// a panel. Only the edges facing into the screen get the thin white outline.
/// </summary>
public sealed class NotchShape : FrameworkElement
{
    public static readonly DependencyProperty ArmHProperty = Register(nameof(ArmH), 30);
    public static readonly DependencyProperty ArmVProperty = Register(nameof(ArmV), 30);
    public static readonly DependencyProperty OuterRadiusProperty = Register(nameof(OuterRadius), 14);
    public static readonly DependencyProperty InnerRadiusProperty = Register(nameof(InnerRadius), 14);

    public static readonly DependencyProperty CornerProperty = DependencyProperty.Register(
        nameof(Corner), typeof(string), typeof(NotchShape),
        new FrameworkPropertyMetadata("top-left", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush FillBrush = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Pen RimPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF))), 1));

    public double ArmH { get => (double)GetValue(ArmHProperty); set => SetValue(ArmHProperty, value); }
    public double ArmV { get => (double)GetValue(ArmVProperty); set => SetValue(ArmVProperty, value); }
    public double OuterRadius { get => (double)GetValue(OuterRadiusProperty); set => SetValue(OuterRadiusProperty, value); }
    public double InnerRadius { get => (double)GetValue(InnerRadiusProperty); set => SetValue(InnerRadiusProperty, value); }
    public string Corner { get => (string)GetValue(CornerProperty); set => SetValue(CornerProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1)
        {
            return;
        }

        var right = Corner.EndsWith("right", StringComparison.Ordinal);
        var bottom = Corner.StartsWith("bottom", StringComparison.Ordinal);
        dc.PushTransform(new MatrixTransform(right ? -1 : 1, 0, 0, bottom ? -1 : 1, right ? w : 0, bottom ? h : 0));

        var (fill, rim) = Build(w, h, Math.Clamp(ArmH, 1, h), Math.Clamp(ArmV, 1, w), OuterRadius, InnerRadius);
        dc.DrawGeometry(FillBrush, null, fill);
        dc.DrawGeometry(null, RimPen, rim);
        dc.Pop();
    }

    // Canonical top-left L: horizontal arm [0,w]×[0,th], vertical arm [0,tv]×[0,h].
    private static (Geometry Fill, Geometry Rim) Build(double w, double h, double th, double tv, double ro, double ri)
    {
        ro = Math.Max(0, Math.Min(ro, Math.Min(th, tv) / 2));
        var fill = new StreamGeometry();
        var rim = new StreamGeometry();

        // Once the gap between the arms is smaller than the corner rounding, the L has filled in:
        // draw a rectangle with only its inner (screen-facing) corner rounded.
        var merged = w - tv < (2 * ro) + 1 || h - th < (2 * ro) + 1;
        if (merged)
        {
            using (var g = fill.Open())
            {
                g.BeginFigure(new Point(0, 0), true, true);
                Rect(g, w, h, ro);
            }

            using (var g = rim.Open())
            {
                g.BeginFigure(new Point(w, 0), false, false);
                g.LineTo(new Point(w, h - ro), true, true);
                g.ArcTo(new Point(w - ro, h), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(0, h), true, true);
            }
        }
        else
        {
            ri = Math.Max(0, Math.Min(ri, Math.Min(w - tv - ro, h - th - ro)));
            using (var g = fill.Open())
            {
                g.BeginFigure(new Point(0, 0), true, true);
                L(g, w, h, th, tv, ro, ri);
                g.LineTo(new Point(0, h), true, true);
            }

            using (var g = rim.Open())
            {
                g.BeginFigure(new Point(w, 0), false, false);
                L(g, w, h, th, tv, ro, ri);
                g.LineTo(new Point(0, h), true, true);
            }
        }

        fill.Freeze();
        rim.Freeze();
        return (fill, rim);
    }

    private static void Rect(StreamGeometryContext g, double w, double h, double ro)
    {
        g.LineTo(new Point(w, 0), true, true);
        g.LineTo(new Point(w, h - ro), true, true);
        g.ArcTo(new Point(w - ro, h), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
        g.LineTo(new Point(0, h), true, true);
    }

    private static void L(StreamGeometryContext g, double w, double h, double th, double tv, double ro, double ri)
    {
        g.LineTo(new Point(w, 0), true, true);
        g.LineTo(new Point(w, th - ro), true, true);
        g.ArcTo(new Point(w - ro, th), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
        g.LineTo(new Point(tv + ri, th), true, true);
        if (ri > 0.01)
        {
            g.ArcTo(new Point(tv, th + ri), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        g.LineTo(new Point(tv, h - ro), true, true);
        g.ArcTo(new Point(tv - ro, h), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
    }

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(NotchShape),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
