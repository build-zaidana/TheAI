using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CodexBar.App;

/// <summary>
/// One drawn surface for the island's liquid language (canvas Page 2, boards 19-25): black shapes
/// melted into one by a smooth union (the "neck" between the island and an object leaving it), an
/// optional dark channel inside them, and a liquid in that channel whose leading edge is a slow wave.
/// Everything is a signed distance per pixel, so every edge is anti-aliased and the shapes can be
/// anything built from rounded rectangles and circles. All coordinates are DIPs in this element.
/// </summary>
public sealed class GooSurface : FrameworkElement
{
    public enum LiquidMode { None, Horizontal, Vertical }

    /// <summary>A rounded rectangle (r = corner radius) or, with <see cref="Circle"/>, a disc.</summary>
    public readonly record struct Shape(double X, double Y, double W, double H, double R, bool FlatTop = false)
    {
        public static Shape Circle(double cx, double cy, double r) => new(cx - r, cy - r, 2 * r, 2 * r, r);

        public double Distance(double px, double py)
        {
            var top = FlatTop ? Y - R - 2 : Y;          // a flat top runs into the screen edge: no top corners
            var hw = W / 2;
            var hh = (Y + H - top) / 2;
            var dx = Math.Abs(px - (X + hw)) - hw + R;
            var dy = Math.Abs(py - (top + hh)) - hh + R;
            var ox = Math.Max(dx, 0);
            var oy = Math.Max(dy, 0);
            return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(dx, dy), 0) - R;
        }
    }

    public IReadOnlyList<Shape> Silhouette { get; set; } = [];
    public IReadOnlyList<Shape> Channel { get; set; } = [];
    /// <summary>Pixels well inside this shape are left clear: something else (the Shell) already draws them.</summary>
    public Shape? Exclude { get; set; }
    public double NeckSoftness { get; set; } = 12;
    public double ChannelSoftness { get; set; } = 8;
    public Color SilhouetteColor { get; set; } = Colors.Black;
    public Color ChannelColor { get; set; } = Color.FromRgb(0x1C, 0x1C, 0x1E);
    /// <summary>The island's 1 px outline, drawn along the melted silhouette so it wraps the neck too.</summary>
    public Color? Rim { get; set; }

    public LiquidMode Mode { get; set; }
    /// <summary>Horizontal: x of an empty and a full level. Vertical: y of an empty and a full level.</summary>
    public (double Empty, double Full) Span { get; set; }
    public Color[] LiquidColors { get; set; } = [Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF7, 0xF7, 0xF9), Color.FromRgb(0xEC, 0xEC, 0xF0)];
    public double WaveLength { get; set; } = 44;
    public double WaveAmplitude { get; set; } = 4;
    /// <summary>Wave lengths travelled per second.</summary>
    public double WaveSpeed { get; set; } = 0.5;

    private double _target, _level, _velocity, _phase;
    private bool _animating;
    private TimeSpan _last;
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];

    /// <summary>The level being shown (0..1), springing toward <see cref="Level"/> with a small overshoot.</summary>
    public double ShownLevel => _level;

    /// <summary>Raised after every drawn frame, so text that depends on the level can follow it.</summary>
    public event EventHandler? Frame;

    public double Level
    {
        get => _target;
        set
        {
            _target = Math.Clamp(value, 0, 1);
            Wake();
        }
    }

    /// <summary>Jump straight to a level, no spring (first show).</summary>
    public void SnapLevel(double value)
    {
        _target = _level = Math.Clamp(value, 0, 1);
        _velocity = 0;
        Redraw();
    }

    public GooSurface()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Wake();
        Unloaded += (_, _) => Sleep();
    }

    /// <summary>Call after changing shapes or colours.</summary>
    public void Redraw()
    {
        Draw();
        InvalidateVisual();
        Frame?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The area the liquid covers, for clipping a dark copy of text that sits over it.</summary>
    public Geometry LiquidGeometry()
    {
        var g = new StreamGeometry();
        if (Mode == LiquidMode.None)
        {
            return g;
        }

        using (var c = g.Open())
        {
            var w = ActualWidth;
            var h = ActualHeight;
            const int steps = 48;
            if (Mode == LiquidMode.Horizontal)
            {
                c.BeginFigure(new Point(-1, -1), true, true);
                for (var i = 0; i <= steps; i++)
                {
                    var y = -1 + ((h + 2) * i / steps);
                    c.LineTo(new Point(Edge(y), y), false, false);
                }
                c.LineTo(new Point(-1, h + 1), false, false);
            }
            else
            {
                c.BeginFigure(new Point(-1, h + 1), true, true);
                for (var i = 0; i <= steps; i++)
                {
                    var x = -1 + ((w + 2) * i / steps);
                    c.LineTo(new Point(x, Edge(x)), false, false);
                }
                c.LineTo(new Point(w + 1, h + 1), false, false);
            }
        }

        g.Freeze();
        return g;
    }

    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    private double Edge(double along)
    {
        var at = Span.Empty + ((Span.Full - Span.Empty) * _level);
        // The wave calms near empty and full, so an empty glass is dry and a full one has no gaps.
        var calm = Math.Clamp(Math.Min(_level, 1 - _level) * 12, 0, 1);
        var wave = ReducedMotion ? 0 : WaveAmplitude * calm * Math.Sin((along / WaveLength + _phase) * 2 * Math.PI);
        return at + wave;
    }

    private void Wake()
    {
        if (IsVisible && Mode != LiquidMode.None && !_animating)
        {
            _animating = true;
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!IsVisible)
        {
            Sleep();
        }

        if (IsVisible)
        {
            Redraw();
        }
    }

    private void Sleep()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero || now <= _last)
        {
            _last = now;
            return;
        }

        var dt = Math.Min((now - _last).TotalSeconds, 1.0 / 30);
        _last = now;
        if (ReducedMotion)
        {
            _level = _target;
            _velocity = 0;
        }
        else
        {
            // A light spring: about 2% past the value, then it settles (the canvas's 92 -> 90).
            const double stiffness = 260, damping = 24;
            _velocity += ((_target - _level) * stiffness - _velocity * damping) * dt;
            _level += _velocity * dt;
            _phase = (_phase + (WaveSpeed * dt)) % 1;
        }

        Redraw();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap is not null)
        {
            dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    private static double SmoothMin(double a, double b, double k)
    {
        if (k <= 0)
        {
            return Math.Min(a, b);
        }

        var h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - (h * h * k * 0.25);
    }

    private static double Field(IReadOnlyList<Shape> shapes, double x, double y, double k)
    {
        var d = double.MaxValue;
        for (var i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i].Distance(x, y);
            d = i == 0 ? s : SmoothMin(d, s, k);
        }

        return d;
    }

    private void Draw()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1 || !IsVisible)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = (int)Math.Ceiling(w * dpi.DpiScaleX);
        var ph = (int)Math.Ceiling(h * dpi.DpiScaleY);
        if (_bitmap is null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
        {
            _bitmap = new WriteableBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
            _pixels = new int[pw * ph];
        }

        var sx = 1 / dpi.DpiScaleX;
        var sy = 1 / dpi.DpiScaleY;
        var aa = dpi.DpiScaleX;                 // one device pixel of soft edge
        var hasChannel = Channel.Count > 0;
        var liquid = hasChannel && Mode != LiquidMode.None;
        var (top, bottom) = ChannelBounds();

        // The wave is one value per row (horizontal) or per column (vertical): compute it once.
        var edges = liquid ? new double[Mode == LiquidMode.Horizontal ? ph : pw] : [];
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = Edge((i + 0.5) * (Mode == LiquidMode.Horizontal ? sy : sx));
        }

        var silhouette = Silhouette;
        var channel = Channel;
        var exclude = Exclude;
        var sil = SilhouetteColor;
        var trk = ChannelColor;
        var rim = Rim;
        for (var py = 0; py < ph; py++)
        {
            var y = (py + 0.5) * sy;
            var row = py * pw;
            for (var px = 0; px < pw; px++)
            {
                var x = (px + 0.5) * sx;
                double a = 0, r = 0, g = 0, b = 0;   // premultiplied, 0..1

                if (silhouette.Count > 0)
                {
                    var d = Field(silhouette, x, y, NeckSoftness);
                    var cover = Math.Clamp(0.5 - (d * aa), 0, 1);
                    if (cover > 0 && exclude is { } ex && ex.Distance(x, y) * aa < -1.5)
                    {
                        cover = 0;                  // the Shell draws this part
                    }
                    if (rim is { } line)
                    {
                        // Like the Shell's Border: the fill stops 1 DIP inside the edge and the outline
                        // lies in that outer ring, over whatever is behind (so both look the same).
                        var inner = Math.Clamp(0.5 - ((d + 1) * aa), 0, 1);
                        Over(ref a, ref r, ref g, ref b, sil, inner);
                        Over(ref a, ref r, ref g, ref b, Color.FromRgb(line.R, line.G, line.B), (cover - inner) * line.A / 255.0);
                    }
                    else
                    {
                        Over(ref a, ref r, ref g, ref b, sil, cover);
                    }
                }

                if (hasChannel)
                {
                    var dc = Field(channel, x, y, ChannelSoftness);
                    var inside = Math.Clamp(0.5 - (dc * aa), 0, 1);
                    if (inside > 0)
                    {
                        Over(ref a, ref r, ref g, ref b, trk, inside);
                        if (liquid)
                        {
                            var wet = Mode == LiquidMode.Horizontal
                                ? Math.Clamp(0.5 - ((x - edges[py]) * aa), 0, 1)
                                : Math.Clamp(0.5 + ((y - edges[px]) * aa), 0, 1);
                            if (wet > 0)
                            {
                                var t = bottom > top ? Math.Clamp((y - top) / (bottom - top), 0, 1) : 0;
                                Over(ref a, ref r, ref g, ref b, LiquidAt(t), inside * wet);
                            }
                        }
                    }
                }

                _pixels[row + px] = a <= 0 ? 0
                    : ((int)(a * 255 + 0.5) << 24) | ((int)(r * 255 + 0.5) << 16) | ((int)(g * 255 + 0.5) << 8) | (int)(b * 255 + 0.5);
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _pixels, pw * 4, 0);
    }

    private (double Top, double Bottom) ChannelBounds()
    {
        if (Channel.Count == 0)
        {
            return (0, 0);
        }

        return (Channel.Min(s => s.Y), Channel.Max(s => s.Y + s.H));
    }

    private Color LiquidAt(double t)
    {
        var c = LiquidColors;
        if (c.Length == 1)
        {
            return c[0];
        }

        var at = t * (c.Length - 1);
        var i = Math.Min((int)at, c.Length - 2);
        var f = at - i;
        return Color.FromRgb((byte)(c[i].R + ((c[i + 1].R - c[i].R) * f)), (byte)(c[i].G + ((c[i + 1].G - c[i].G) * f)), (byte)(c[i].B + ((c[i + 1].B - c[i].B) * f)));
    }

    private static void Over(ref double a, ref double r, ref double g, ref double b, Color c, double alpha)
    {
        if (alpha <= 0)
        {
            return;
        }

        var keep = 1 - alpha;
        r = (c.R / 255.0 * alpha) + (r * keep);
        g = (c.G / 255.0 * alpha) + (g * keep);
        b = (c.B / 255.0 * alpha) + (b * keep);
        a = alpha + (a * keep);
    }
}
