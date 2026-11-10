using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexBar.App;

/// <summary>First child flush left, last flush right, equal gaps between (board 31's footer).</summary>
public sealed class SpaceBetweenPanel : System.Windows.Controls.Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToList();
        var used = shown.Sum(child => child.DesiredSize.Width);
        var gap = shown.Count > 1 ? Math.Max(0, (finalSize.Width - used) / (shown.Count - 1)) : 0;
        double x = 0;
        foreach (var child in shown)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + gap;
        }

        return finalSize;
    }
}

/// <summary>Something that only moves while it can be seen.</summary>
public abstract class IslandLive : FrameworkElement
{
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    protected IslandLive()
    {
        _frame.Tick += (_, _) => OnFrame();
        IsVisibleChanged += (_, _) => Refresh();
        Unloaded += (_, _) => _frame.Stop();
    }

    protected abstract bool Moving { get; }

    protected abstract void OnFrame();

    protected void Refresh()
    {
        if (IsVisible && Moving)
        {
            _frame.Start();
        }
        else
        {
            _frame.Stop();
        }
    }
}

/// <summary>
/// A length on a ruler (board 32 A): the scale slides under a fixed needle. Drag it or scroll it; one
/// minute is 10 px, a longer tick and a number every 5. Read-only pages just move Value.
/// </summary>
public sealed class RulerPicker : IslandLive
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(TimeSpan), typeof(RulerPicker),
        new FrameworkPropertyMetadata(TimeSpan.FromMinutes(5), (d, _) => ((RulerPicker)d).ValueMoved()));

    private const double PerMinute = 10;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Pen Small = Line(Color.FromRgb(0x48, 0x48, 0x4A), 1.2);
    private static readonly Pen Large = Line(Color.FromRgb(0x8E, 0x8E, 0x93), 1.6);

    private double _shown = 5;
    private double _dragStartX;
    private double _dragStartMinutes;

    public RulerPicker()
    {
        Cursor = Cursors.SizeWE;
        Focusable = false;
    }

    public TimeSpan Value
    {
        get => (TimeSpan)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MaxMinutes { get; set; } = 180;

    /// <summary>A time of day: the scale reads H:mm, turns past midnight, and the mouse wheel moves 5 minutes.</summary>
    public bool ClockFace { get; set; }

    public event Action<TimeSpan>? Changed;

    private static Pen Line(Color colour, double thickness)
    {
        var pen = new Pen(IslandTokens.Frozen(colour), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    protected override bool Moving => Math.Abs(_shown - Value.TotalMinutes) > 0.01;

    private void ValueMoved()
    {
        if (Math.Abs(_shown - Value.TotalMinutes) < 0.5 || IsMouseCaptured || !IsVisible || (ClockFace && Math.Abs(_shown - Value.TotalMinutes) > 720))
        {
            _shown = Value.TotalMinutes;
        }

        Refresh();
        InvalidateVisual();
    }

    protected override void OnFrame()
    {
        _shown += (Value.TotalMinutes - _shown) * 0.28;
        if (!Moving)
        {
            _shown = Value.TotalMinutes;
            Refresh();
        }

        InvalidateVisual();
    }

    private void Pick(double minutes)
    {
        var picked = TimeSpan.FromMinutes(ClockFace ? ((Math.Round(minutes) % 1440) + 1440) % 1440 : Math.Clamp(Math.Round(minutes), 1, MaxMinutes));
        if (picked != Value)
        {
            Value = picked;
            Changed?.Invoke(picked);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStartX = e.GetPosition(this).X;
        _dragStartMinutes = Value.TotalMinutes;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (IsMouseCaptured)
        {
            Pick(_dragStartMinutes - ((e.GetPosition(this).X - _dragStartX) / PerMinute));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var step = ClockFace ? 5 : 1;
        Pick(Math.Round(Value.TotalMinutes) + (e.Delta > 0 ? step : -step));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var width = ActualWidth;
        var middle = width / 2;
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var first = (int)Math.Floor(_shown - (middle / PerMinute));
        var last = (int)Math.Ceiling(_shown + (middle / PerMinute));
        if (!ClockFace)
        {
            first = Math.Max(0, first);
            last = (int)Math.Min(MaxMinutes, last);
        }

        for (var minute = first; minute <= last; minute++)
        {
            var x = middle + ((minute - _shown) * PerMinute);
            var fade = Math.Clamp(Math.Min(x, width - x) / (width * 0.18), 0, 1);
            if (fade <= 0)
            {
                continue;
            }

            dc.PushOpacity(fade);
            var major = ((minute % 5) + 5) % 5 == 0;
            dc.DrawLine(major ? Large : Small, new Point(x, 8), new Point(x, major ? 28 : 18));
            if (major)
            {
                var day = ((minute % 1440) + 1440) % 1440;
                var label = ClockFace ? $"{day / 60}:{day % 60:00}" : minute.ToString(CultureInfo.CurrentCulture);
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, IslandTokens.Text3, dip);
                dc.DrawText(text, new Point(x - (text.Width / 2), 40));
            }

            dc.Pop();
        }

        dc.DrawRoundedRectangle(IslandTokens.Text1, null, new Rect(middle - 1, 0, 2, 32), 1, 1);
    }
}

/// <summary>
/// The last minute of a reading as a line (board 31): curve, gradient under it, a dot on the right end.
/// Each new reading slides in over 300 ms; the dot's ring breathes while the page is open.
/// </summary>
public sealed class Sparkline : IslandLive
{
    private const int Points = 40;
    private readonly List<double> _values = [];
    private readonly DateTime _born = DateTime.UtcNow;
    private DateTime _pushed = DateTime.MinValue;
    private Color _colour;
    private System.Windows.Media.Brush _fill = System.Windows.Media.Brushes.Transparent;
    private Pen _pen = new(System.Windows.Media.Brushes.White, 1.6);

    public Sparkline() => Colour = Color.FromRgb(0xF5, 0xF5, 0xF7);

    /// <summary>A whole series spread over the width, with the dot on this point (the hours of a day); null scrolls.</summary>
    public int? Mark { get; set; }

    public Color Colour
    {
        get => _colour;
        set
        {
            _colour = value;
            _fill = new LinearGradientBrush(Color.FromArgb(0x59, value.R, value.G, value.B), Color.FromArgb(0, value.R, value.G, value.B), 90);
            _fill.Freeze();
            _pen = new Pen(IslandTokens.Frozen(value), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _pen.Freeze();
            InvalidateVisual();
        }
    }

    protected override bool Moving => _values.Count > 1;

    /// <summary>Shows a reading's history at once (switching metric, or opening the page).</summary>
    public void Show(IEnumerable<double> values)
    {
        _values.Clear();
        _values.AddRange(values.TakeLast(Points + 1));
        _pushed = DateTime.MinValue;
        Refresh();
        InvalidateVisual();
    }

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > Points + 1)
        {
            _values.RemoveAt(0);
        }

        _pushed = DateTime.UtcNow;
        Refresh();
        InvalidateVisual();
    }

    protected override void OnFrame() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        if (_values.Count < 2)
        {
            return;
        }

        double width = ActualWidth, height = ActualHeight;
        var low = _values.Min();
        var spread = _values.Max() - low;
        var range = Math.Max(spread, Math.Max(Math.Abs(low) * 0.1, 2));   // small wobbles stay small
        var bottom = low - (range * 0.14) - ((range - spread) / 2);   // a level line sits in the middle
        var top = bottom + (range * 1.28);
        double Y(double value) => height - 2 - ((value - bottom) / (top - bottom) * (height - 4));

        var step = width / (Points - 1);
        var slide = 1 - Math.Clamp((DateTime.UtcNow - _pushed).TotalMilliseconds / 300, 0, 1);
        slide *= slide;
        var count = _values.Count;
        double X(int index) => Mark is null
            ? width - ((count - 1 - index) * step) + (slide * step)
            : index * width / (count - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var path = line.Open())
        using (var fill = area.Open())
        {
            var start = new Point(X(0), Y(_values[0]));
            path.BeginFigure(start, false, false);
            fill.BeginFigure(new Point(start.X, height), true, true);
            fill.LineTo(start, false, false);
            for (var i = 1; i < count; i++)
            {
                double x0 = X(i - 1), x1 = X(i), y0 = Y(_values[i - 1]), y1 = Y(_values[i]), mid = (x0 + x1) / 2;
                path.BezierTo(new Point(mid, y0), new Point(mid, y1), new Point(x1, y1), true, true);
                fill.BezierTo(new Point(mid, y0), new Point(mid, y1), new Point(x1, y1), false, true);
            }

            fill.LineTo(new Point(X(count - 1), height), false, false);
        }

        line.Freeze();
        area.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(-1, -4, width + 1, height + 8)));
        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _pen, line);
        dc.Pop();

        var mark = Math.Clamp(Mark ?? count - 1, 0, count - 1);
        var dotY = Mark is null ? Y(_values[^1]) + ((Y(_values[^2]) - Y(_values[^1])) * slide) : Y(_values[mark]);
        var beat = (DateTime.UtcNow - _born).TotalSeconds % 1.6 / 1.6;
        var ring = IslandTokens.Frozen(Color.FromArgb((byte)(128 * (1 - beat)), _colour.R, _colour.G, _colour.B));
        var dot = new Point(Mark is null ? width : X(mark), dotY);
        dc.DrawEllipse(ring, null, dot, 3 + (6 * beat), 3 + (6 * beat));
        dc.DrawEllipse(IslandTokens.Frozen(_colour), null, dot, 3, 3);
    }
}

/// <summary>A song's position as a bar shape (board 33): played bars in white and moving, the rest grey.</summary>
public sealed class Waveform : IslandLive
{
    private const double Bar = 3, Gap = 3;
    private static readonly System.Windows.Media.Brush Rest = IslandTokens.Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C));
    private readonly DateTime _born = DateTime.UtcNow;
    private int _seed = 1;
    private double _progress;
    private bool _playing;

    public Waveform() => IsHitTestVisible = false;

    public string Seed
    {
        set
        {
            _seed = string.IsNullOrEmpty(value) ? 1 : value.Aggregate(17, (hash, c) => unchecked((hash * 31) + c));
            InvalidateVisual();
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            _progress = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public bool Playing
    {
        set
        {
            _playing = value;
            Refresh();
            InvalidateVisual();
        }
    }

    protected override bool Moving => _playing;

    protected override void OnFrame() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var count = (int)((ActualWidth + Gap) / (Bar + Gap));
        var seconds = (DateTime.UtcNow - _born).TotalSeconds;
        var phase = (_seed & 0xFF) / 40.0;
        for (var i = 0; i < count; i++)
        {
            var played = i < _progress * count;
            var shape = Math.Abs(Math.Sin((i * 0.47) + phase) * Math.Cos((i * 0.19) + 1 + phase));
            var bar = 10 + (40 * shape) + (((i * 37) + _seed) & 7);
            if (played && _playing)
            {
                bar *= 0.78 + (0.22 * Math.Sin((seconds * 5) + (i * 1.7)));
            }

            var x = i * (Bar + Gap);
            dc.DrawRoundedRectangle(played ? IslandTokens.Text1 : Rest, null, new Rect(x, (ActualHeight - bar) / 2, Bar, bar), 1.5, 1.5);
        }
    }
}
