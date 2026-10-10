using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexBar.App;

/// <summary>
/// A timer length picked like the Clock app's wheels: hours, minutes and seconds, each its own
/// column that turns up and down. The chosen value sits large in the middle with its neighbours
/// fading above and below. Scroll a column or drag it vertically; click a neighbour to pick it.
/// </summary>
public sealed class TimeWheel : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(TimeSpan), typeof(TimeWheel),
        new FrameworkPropertyMetadata(TimeSpan.FromMinutes(5), FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Clamp((TimeSpan)value)));

    private const double ColumnWidth = 78;
    private const double Gap = 20;          // room for the ":" between columns
    private const double RowHeight = 30;
    private const int VisibleRows = 5;      // two above, the chosen one, two below
    private const double DragStep = 22;     // px of vertical drag per step

    // Sora, bundled with the app (Fonts/OFL.txt), like the timer's digits.
    private static readonly System.Windows.Media.FontFamily Sora = new(new Uri("pack://application:,,,/"), "./Fonts/#Sora");
    private static readonly int[] Limits = [24, 60, 60];

    private int _dragColumn = -1;
    private double _dragStartY;
    private int _dragStartValue;
    private bool _dragMoved;

    public TimeWheel()
    {
        Focusable = false;
        Cursor = Cursors.SizeNS;
    }

    /// <summary>The chosen length (0:00:00 – 23:59:59).</summary>
    public TimeSpan Value
    {
        get => (TimeSpan)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Raised whenever the length changes.</summary>
    public event Action<TimeSpan>? Changed;

    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1) : value;

    private int Part(int column) => column switch
    {
        0 => Value.Hours,
        1 => Value.Minutes,
        _ => Value.Seconds
    };

    private void SetPart(int column, int value)
    {
        var limit = Limits[column];
        value = ((value % limit) + limit) % limit;   // wheels wrap round, like the Clock app's
        var parts = new[] { Value.Hours, Value.Minutes, Value.Seconds };
        if (parts[column] == value)
        {
            return;
        }

        parts[column] = value;
        Value = new TimeSpan(parts[0], parts[1], parts[2]);
        Changed?.Invoke(Value);
    }

    private double TotalWidth => ColumnWidth * 3 + Gap * 2;

    protected override Size MeasureOverride(Size availableSize) => new(TotalWidth, RowHeight * VisibleRows);

    private double ColumnLeft(int column) => (ActualWidth - TotalWidth) / 2 + column * (ColumnWidth + Gap);

    private int ColumnAt(double x)
    {
        for (var column = 0; column < 3; column++)
        {
            var left = ColumnLeft(column);
            if (x >= left - Gap / 2 && x < left + ColumnWidth + Gap / 2)
            {
                return column;
            }
        }

        return -1;
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize)); // the whole picker takes the mouse
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var middle = ActualHeight / 2;

        // The band behind the chosen row, as in the Clock app.
        context.DrawRoundedRectangle(Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), null,
            new Rect((ActualWidth - TotalWidth) / 2 - 8, middle - RowHeight / 2 - 2, TotalWidth + 16, RowHeight + 4), 10, 10);

        for (var column = 0; column < 3; column++)
        {
            var centre = ColumnLeft(column) + ColumnWidth / 2;
            var value = Part(column);
            for (var offset = -2; offset <= 2; offset++)
            {
                var shown = ((value + offset) % Limits[column] + Limits[column]) % Limits[column];
                var size = offset == 0 ? 26 : 18 - Math.Abs(offset) * 1.5;
                var alpha = offset == 0 ? 255 : Math.Abs(offset) == 1 ? 0x73 : 0x2E;
                var text = new FormattedText(shown.ToString("00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(Sora, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    size, Frozen(Color.FromArgb((byte)alpha, 0xFF, 0xFF, 0xFF)), dpi);
                var y = middle + offset * RowHeight - text.Height / 2;
                context.DrawText(text, new Point(centre - text.Width / 2, y));
            }

            if (column < 2)
            {
                var colon = new FormattedText(":", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Sora, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 24,
                    Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), dpi);
                context.DrawText(colon, new Point(ColumnLeft(column) + ColumnWidth + Gap / 2 - colon.Width / 2, middle - colon.Height / 2 - 1));
            }
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;   // the wheel turns the column under it; the notch's pages don't turn
        var column = ColumnAt(e.GetPosition(this).X);
        if (column >= 0)
        {
            SetPart(column, Part(column) + (e.Delta > 0 ? -1 : 1));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;   // not the start of dragging the notch around
        _dragColumn = ColumnAt(e.GetPosition(this).X);
        if (_dragColumn < 0)
        {
            return;
        }

        _dragStartY = e.GetPosition(this).Y;
        _dragStartValue = Part(_dragColumn);
        _dragMoved = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragColumn < 0 || !IsMouseCaptured)
        {
            return;
        }

        var delta = _dragStartY - e.GetPosition(this).Y;   // drag up = later values come to the middle
        if (Math.Abs(delta) > 3)
        {
            _dragMoved = true;
        }

        SetPart(_dragColumn, _dragStartValue + (int)Math.Round(delta / DragStep));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragColumn < 0)
        {
            return;
        }

        e.Handled = true;
        if (!_dragMoved)
        {
            // A click on a neighbour picks it.
            var rows = (int)Math.Round((e.GetPosition(this).Y - ActualHeight / 2) / RowHeight);
            SetPart(_dragColumn, _dragStartValue + Math.Clamp(rows, -2, 2));
        }

        _dragColumn = -1;
        ReleaseMouseCapture();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
