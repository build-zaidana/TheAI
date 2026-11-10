using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace CodexBar.App;

// The live object of V3 (canvas Page 2, boards 04-06 and 19-20): whatever is running is a round thing
// that leaves the island's left side in the island's own black, joined to it by a liquid neck. Like the
// iPhone's Dynamic Island there is no glow: the island stays clean black and the object carries the colour. Music: a record whose label is the cover. Timer: a dial
// whose arc is the time left. Agent: an orb that says the state. The text beside it starts 16 after its
// edge. Top-centre island only.
public partial class NotchWindow
{
    private enum LiveKind { None, Record, Dial, Orb }


    private static readonly Geometry CheckGlyph = Geometry.Parse("M 20,6 L 9,17 L 4,12");
    private static readonly Geometry CrossGlyph = Geometry.Parse("M 18,6 L 6,18 M 6,6 L 18,18");
    private static readonly Geometry AlertGlyph = Geometry.Parse("M 12,7 L 12,13 M 12,17 L 12.01,17");
    private static readonly Geometry SparkleGlyph = Geometry.Parse("M 12,3 L 12,6 M 12,18 L 12,21 M 3,12 L 6,12 M 18,12 L 21,12 M 5.6,5.6 L 7.7,7.7 M 16.3,16.3 L 18.4,18.4 M 5.6,18.4 L 7.7,16.3 M 16.3,7.7 L 18.4,5.6");

    private LiveKind _liveKind;
    private double _liveRadius = 52, _liveCyMax = 64, _liveLow, _liveOut, _liveOutTarget, _liveOutVelocity;
    private bool _liveAnimating;
    private TimeSpan _liveLast;
    private GooSurface? _liveNeck;
    private Path? _liveRim;
    private Grid? _liveVisual;
    private object? _liveKey;            // what the visual was built for, so it is rebuilt only when that changes

    // Dial and orb parts that change without a rebuild.
    private RotateTransform? _recordSpin;
    private Border? _recordLabel;

    private bool LiveObjectAllowed => Edge == "top" && !IsCorner && !_floating;

    /// <summary>The text column's left inset when the object is out: 16 after the object's edge.</summary>

    /// <summary>
    /// Show (or update) the live object. <paramref name="key"/> identifies the visual: the same key keeps
    /// the built visual and only moves it; a new key rebuilds it with <paramref name="build"/>.
    /// </summary>
    private void SetLiveObject(LiveKind kind, double radius, double cyMax, double outBy, object key, Func<double, Grid> build, double low = 0)
    {
        if (!LiveObjectAllowed)
        {
            ClearLiveObject();
            return;
        }

        EnsureLiveLayers();
        var fresh = _liveKind == LiveKind.None;
        _liveKind = kind;
        _liveRadius = radius;
        _liveCyMax = cyMax;
        _liveLow = low;
        _liveOutTarget = outBy;
        if (fresh)
        {
            _liveOut = 0;
            _liveOutVelocity = 0;
        }

        if (!Equals(_liveKey, key) || _liveVisual!.Children.Count == 0)
        {
            _liveKey = key;
            _liveVisual!.Children.Clear();
            _liveVisual.Children.Add(build(radius > 30 ? radius - 2 : radius - 1));
        }

        _liveNeck!.Visibility = _liveVisual!.Visibility = Visibility.Visible;
        UpdateLiveObject();
        StartLiveSpring();
    }

    private void ClearLiveObject()
    {
        if (_liveKind == LiveKind.None)
        {
            return;
        }

        _liveKind = LiveKind.None;
        _liveKey = null;
        ApplyKeyline();   // the Shell draws its own outline again
        _recordSpin = null;
        _recordLabel = null;
        if (_liveNeck is not null)
        {
            _liveNeck.Visibility = Visibility.Collapsed;
            _liveRim!.Visibility = Visibility.Collapsed;
            _liveVisual!.Visibility = Visibility.Collapsed;
            _liveVisual.Children.Clear();
        }
    }

    private void EnsureLiveLayers()
    {
        if (_liveNeck is not null)
        {
            return;
        }

        _liveNeck = new GooSurface { NeckSoftness = 16 };
        _liveRim = new Path { StrokeThickness = 1, IsHitTestVisible = false };
        _liveVisual = new Grid { IsHitTestVisible = false };
        ObjectLayer.Children.Add(_liveNeck);
        ObjectLayer.Children.Add(_liveRim);
        ObjectLayer.Children.Add(_liveVisual);
    }

    private void StartLiveSpring()
    {
        if (_liveAnimating)
        {
            return;
        }

        _liveAnimating = true;
        _liveLast = TimeSpan.Zero;
        CompositionTarget.Rendering += OnLiveFrame;
    }

    private void OnLiveFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_liveLast == TimeSpan.Zero || now <= _liveLast)
        {
            _liveLast = now;
            return;
        }

        var dt = Math.Min((now - _liveLast).TotalSeconds, 1.0 / 30);
        _liveLast = now;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _liveOut = _liveOutTarget;
            _liveOutVelocity = 0;
        }
        else
        {
            // The drip: a spring with a little overshoot, so the object pops out and settles.
            const double stiffness = 170, damping = 17;
            _liveOutVelocity += ((_liveOutTarget - _liveOut) * stiffness - (_liveOutVelocity * damping)) * dt;
            _liveOut += _liveOutVelocity * dt;
        }

        UpdateLiveObject();
        if (_liveKind == LiveKind.None || (Math.Abs(_liveOutTarget - _liveOut) < 0.2 && Math.Abs(_liveOutVelocity) < 2))
        {
            _liveOut = _liveOutTarget;
            UpdateLiveObject();
            _liveAnimating = false;
            CompositionTarget.Rendering -= OnLiveFrame;
        }
    }

    /// <summary>Lay the object and its neck out against the Shell as it is now.</summary>
    private void UpdateLiveObject()
    {
        if (_liveKind == LiveKind.None || _liveNeck is null)
        {
            return;
        }

        // Whole DIPs, as layout rounding draws the Shell; and the layer sits inside the Shell's border,
        // so it is moved back by that border to share the Shell's own coordinates.
        var w = Math.Round(Shell.Width is > 0 and var sw ? sw : Shell.ActualWidth);
        var h = Math.Round(Shell.Height is > 0 and var shh ? shh : Shell.ActualHeight);
        ObjectLayer.Margin = new Thickness(-Shell.BorderThickness.Left, -Shell.BorderThickness.Top, 0, 0);
        var r = _liveRadius;
        var cy = Math.Min(h / 2, _liveCyMax) + _liveLow;
        var cx = r - _liveOut;
        var radius = Shell.CornerRadius.BottomLeft;

        // The neck: the island's left part and the object melted into one black surface.
        // The surface draws only the left part, where the neck can reach; past `split` the island is
        // plain, so a path with the same outline carries the rim on (cheap to redraw every frame).
        var left = -((2 * r) + 40);
        var split = Math.Min(w, (2 * r) + 24);
        _liveNeck.Width = split - left;
        _liveNeck.Height = h + 24;
        Canvas.SetLeft(_liveNeck, left);
        Canvas.SetTop(_liveNeck, 0);
        var rim = IslandRim();
        _liveNeck.Rim = rim;
        _liveNeck.NeckSoftness = r > 30 ? 16 : 8;
        Shell.BorderBrush = System.Windows.Media.Brushes.Transparent;
        _liveRim!.Visibility = rim is null ? Visibility.Collapsed : Visibility.Visible;
        if (rim is { } line)
        {
            var top = _island ? 0.5 : -radius - 2;   // the notch has no rim where it meets the screen edge
            _liveRim.Data = new RectangleGeometry(new Rect(0.5, top, Math.Max(0, w - 1), Math.Max(0, h - 0.5 - top)), radius - 0.5, radius - 0.5);
            _liveRim.Stroke = IslandTokens.Frozen(line);
            _liveRim.Clip = new RectangleGeometry(new Rect(split, 0, Math.Max(0, w - split + 2), h + 2));
        }
        _liveNeck.Silhouette =
        [
            new GooSurface.Shape(-left, 0, w, h, radius, FlatTop: !_island),
            GooSurface.Shape.Circle(cx - left, cy, r)
        ];
        _liveNeck.Redraw();

        _liveVisual!.Width = _liveVisual.Height = 2 * r;
        Canvas.SetLeft(_liveVisual, cx - r);
        Canvas.SetTop(_liveVisual, cy - r);
    }

    /// <summary>The Shell's outline colour where it has one, for shapes that continue the Shell.</summary>
    private Color? IslandRim() =>
        Shell.BorderThickness.Bottom > 0 && (_state == NotchState.Waiting ? WaitingRim : OutlineBrush) is SolidColorBrush rim ? rim.Color : null;

    // ---- The objects ------------------------------------------------------------------------------

    /// <summary>A record: grooves, two sheens tinted with the cover's colour, the cover as its label.</summary>
    private Grid BuildRecord(double r, Brush label, Color tint, bool spinning)
    {
        var grid = new Grid { Width = 2 * r, Height = 2 * r, RenderTransformOrigin = new Point(0.5, 0.5) };
        grid.Children.Add(new Ellipse { Fill = IslandTokens.Frozen(0x0B, 0x0B, 0x0C) });
        for (var g = r - 4; g > r * 0.44; g -= 2.6)
        {
            grid.Children.Add(new Ellipse { Width = 2 * g, Height = 2 * g, Stroke = IslandTokens.Frozen(0x1C, 0x1C, 0x1F), StrokeThickness = 0.7 });
        }

        grid.Children.Add(new Ellipse
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0.18),
                    new GradientStop(Color.FromArgb(0x44, tint.R, tint.G, tint.B), 0.27),
                    new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.31),
                    new GradientStop(Colors.Transparent, 0.4),
                    new GradientStop(Colors.Transparent, 0.62),
                    new GradientStop(Color.FromArgb(0x33, tint.R, tint.G, tint.B), 0.7),
                    new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.73),
                    new GradientStop(Colors.Transparent, 0.8)
                }
            }
        });
        var labelSize = Math.Round(r * 0.8);
        _recordLabel = new Border
        {
            Width = labelSize,
            Height = labelSize,
            CornerRadius = new CornerRadius(labelSize / 2),
            Background = label,
            BorderBrush = IslandTokens.Frozen(Color.FromArgb(0x66, 0, 0, 0)),
            BorderThickness = new Thickness(1)
        };
        grid.Children.Add(_recordLabel);
        var hole = Math.Clamp(r * 0.1, 2, 6);
        grid.Children.Add(new Ellipse { Width = hole, Height = hole, Fill = System.Windows.Media.Brushes.Black });
        _recordSpin = new RotateTransform();
        grid.RenderTransform = _recordSpin;
        SpinRecord(spinning);
        return grid;
    }

    // The record is only ever small now (the compact pill): 20 steps a second look the same as an animation,
    // which would make WPF redraw 60 times a second for as long as a song plays.
    private System.Windows.Threading.DispatcherTimer? _recordTick;

    private void SpinRecord(bool spinning)
    {
        if (_recordSpin is null)
        {
            _recordTick?.Stop();
            return;
        }

        if (_recordTick is null)
        {
            _recordTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _recordTick.Tick += (_, _) =>
            {
                if (_recordSpin is null)
                {
                    _recordTick.Stop();
                    return;
                }

                if (!IsVisible)
                {
                    return;   // hidden for a full-screen app: nothing to turn, but it goes on when it's back
                }

                _recordSpin.Angle = (_recordSpin.Angle + (360 * 0.05 / 1.8)) % 360;
            };
        }

        if (spinning && SystemParameters.ClientAreaAnimation)
        {
            _recordTick.Start();
        }
        else
        {
            _recordTick.Stop();
        }

        if (_recordLabel is not null)
        {
            _recordLabel.Opacity = spinning ? 1 : 0.7;
        }
    }

    private static Geometry Arc(Point c, double radius, double fraction)
    {
        var g = new StreamGeometry();
        if (fraction <= 0.001)
        {
            return g;
        }

        var angle = fraction * 2 * Math.PI;
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - radius), false, false);
            ctx.ArcTo(new Point(c.X + (radius * Math.Sin(angle)), c.Y - (radius * Math.Cos(angle))), new System.Windows.Size(radius, radius), 0,
                fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The compact objects (board 01): a ring of the level, a dot, or a disc with a mark.</summary>
    private Grid BuildMiniRing(double r, double fraction, Color color, bool spin)
    {
        var grid = new Grid { Width = 2 * r, Height = 2 * r };
        var ring = new Grid { Width = 22, Height = 22, RenderTransformOrigin = new Point(0.5, 0.5) };
        ring.Children.Add(new Ellipse { Stroke = IslandTokens.Surface2, StrokeThickness = 3 });
        ring.Children.Add(new Path
        {
            Data = Arc(new Point(11, 11), 9.5, fraction),
            Stroke = IslandTokens.Frozen(color),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        if (spin && SystemParameters.ClientAreaAnimation)
        {
            var turn = new RotateTransform();
            ring.RenderTransform = turn;
            var turning = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(turning, 24);
            turn.BeginAnimation(RotateTransform.AngleProperty, turning);
        }
        grid.Children.Add(ring);
        return grid;
    }

    private static Grid BuildMiniMark(double r, Color color, Geometry? mark)
    {
        var grid = new Grid { Width = 2 * r, Height = 2 * r };
        if (mark is null)
        {
            grid.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = IslandTokens.Frozen(color) });
            return grid;
        }

        grid.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = IslandTokens.Frozen(color) });
        grid.Children.Add(Glyph(mark, System.Windows.Media.Brushes.Black, 12, 3.4));
        return grid;
    }

    private static Geometry Segment(Point c, double radius, double fromDegrees, double toDegrees)
    {
        var a0 = fromDegrees * Math.PI / 180;
        var a1 = toDegrees * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + (radius * Math.Sin(a0)), c.Y - (radius * Math.Cos(a0))), false, false);
            ctx.ArcTo(new Point(c.X + (radius * Math.Sin(a1)), c.Y - (radius * Math.Cos(a1))), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static Path Glyph(Geometry data, Brush stroke, double size, double thickness) => new()
    {
        Data = data,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The two strongest colours of a picture (the cover), for the record's light.</summary>
    private static (Color First, Color Second) StrongColours(ImageSource? art, Color fallback)
    {
        if (art is not System.Windows.Media.Imaging.BitmapSource bitmap || bitmap.PixelWidth == 0)
        {
            return (fallback, Shade(fallback, 0.6));
        }

        try
        {
            var small = new System.Windows.Media.Imaging.TransformedBitmap(bitmap,
                new ScaleTransform(8.0 / bitmap.PixelWidth, 8.0 / bitmap.PixelHeight));
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            var colours = new List<(Color Colour, double Score)>();
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var c = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
                var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
                var saturation = max <= 0 ? 0 : (max - min) / max;
                colours.Add((c, (saturation * 0.7) + (max * 0.3)));
            }

            var ranked = colours.OrderByDescending(c => c.Score).Select(c => c.Colour).ToList();
            var first = ranked[0];
            var second = ranked.FirstOrDefault(c => Distance(c, first) > 90, Shade(first, 0.6));
            return (Lift(first), Lift(second));
        }
        catch (Exception)
        {
            return (fallback, Shade(fallback, 0.6));
        }
    }

    private static double Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static Color Shade(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    /// <summary>Very dark colours give no light: lift them toward a visible brightness.</summary>
    private static Color Lift(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max >= 150 || max == 0)
        {
            return max == 0 ? Color.FromRgb(0x60, 0x60, 0x66) : c;
        }

        var f = 150.0 / max;
        return Color.FromRgb((byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
    }
}
