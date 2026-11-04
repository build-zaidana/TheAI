using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

/// <summary>
/// A slider that is a small glass (canvas board 25, G2): a dark capsule the liquid fills from the left,
/// its edge a slow wave. The icon and the number are drawn twice, light on the empty channel and dark
/// where the liquid has reached them, so they always read. Drag or click anywhere on it to set it.
/// </summary>
public sealed class LiquidCapsule : Grid
{
    private static readonly Brush Light = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7));
    private static readonly Brush Dark = Frozen(Color.FromArgb(0xE6, 0, 0, 0));

    private readonly GooSurface _surface = new() { Mode = GooSurface.LiquidMode.Horizontal };
    private readonly Grid _dark = new() { IsHitTestVisible = false };
    private readonly TextBlock _numberLight = Number(Light), _numberDark = Number(Dark);
    private bool _dragging, _enabled = true;

    /// <summary>The new value (0..100) while the user drags.</summary>
    public event Action<int>? Changed;

    public LiquidCapsule(Geometry glyph, double height = 40, bool warm = false)
    {
        Height = height;
        Cursor = System.Windows.Input.Cursors.Hand;
        Background = System.Windows.Media.Brushes.Transparent;
        if (warm)
        {
            _surface.LiquidColors = [Color.FromRgb(0xFF, 0xF7, 0xDF), Color.FromRgb(0xFF, 0xED, 0xBE), Color.FromRgb(0xF2, 0xD9, 0x8F)];
        }

        Children.Add(_surface);
        var light = new Grid { IsHitTestVisible = false };
        light.Children.Add(Glyph(glyph, Light));
        light.Children.Add(_numberLight);
        Children.Add(light);
        _dark.Children.Add(Glyph(glyph, Dark));
        _dark.Children.Add(_numberDark);
        Children.Add(_dark);

        SizeChanged += (_, _) => Layout();
        _surface.Frame += (_, _) => Follow();
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_enabled)
            {
                return;
            }

            _dragging = true;
            CaptureMouse();
            Drag(e.GetPosition(this).X);
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (_dragging)
            {
                Drag(e.GetPosition(this).X);
            }
        };
        MouseLeftButtonUp += (_, _) =>
        {
            _dragging = false;
            ReleaseMouseCapture();
        };
    }

    public int Value => (int)Math.Round(_surface.Level * 100);

    /// <summary>Show a value; <paramref name="spring"/> false jumps (when the page first opens).</summary>
    public void Set(int percent, bool enabled = true, bool spring = true)
    {
        _enabled = enabled;
        Opacity = enabled ? 1 : 0.4;
        if (spring)
        {
            _surface.Level = percent / 100.0;
        }
        else
        {
            _surface.SnapLevel(percent / 100.0);
        }
    }

    private void Drag(double x)
    {
        var percent = (int)Math.Round(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * 100);
        _surface.Level = percent / 100.0;
        Changed?.Invoke(percent);
    }

    private void Layout()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        _surface.Channel = [new GooSurface.Shape(0, 0, w, h, h / 2)];
        _surface.Span = (0, w);
        _surface.Redraw();
    }

    private void Follow()
    {
        var text = Math.Round(Math.Clamp(_surface.ShownLevel, 0, 1) * 100).ToString(CultureInfo.CurrentCulture);
        _numberLight.Text = text;
        _numberDark.Text = text;
        _dark.Clip = _surface.LiquidGeometry();
    }

    private static TextBlock Number(Brush brush) => new TextBlock
    {
        Foreground = brush,
        FontSize = 15,
        FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 16, 0),
        IsHitTestVisible = false,
        TextAlignment = TextAlignment.Right
    }.WithTabular();

    private static Path Glyph(Geometry data, Brush brush) => new()
    {
        Data = data,
        Width = 18,
        Height = 18,
        Stretch = Stretch.Uniform,
        Stroke = brush,
        StrokeThickness = 2,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(14, 0, 0, 0)
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal static class TextBlockExtensions
{
    public static TextBlock WithTabular(this TextBlock block)
    {
        System.Windows.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }
}
