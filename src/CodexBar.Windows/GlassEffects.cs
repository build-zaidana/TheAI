using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexBar.Windows;

/// <summary>
/// Efek dekoratif untuk mendekati kesan "liquid glass" di WPF: highlight specular
/// yang mengikuti kursor dan tepi kaca (rim) bergradien. Bukan refraksi sungguhan
/// (WPF tak bisa membiaskan piksel di belakang window), tetapi memberi kesan kaca hidup.
/// </summary>
public static class GlassEffects
{
    /// <summary>
    /// Shell kaca tipis dengan bias warna ambient. Permukaan sengaja ringan;
    /// kontras teks ditangani oleh inner surface, bukan menggelapkan seluruh window.
    /// </summary>
    public static LinearGradientBrush CreateSurface(bool dark)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };

        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 0x5B, 0x3C, 0x8D), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x36, 0x0C, 0x11, 0x20), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x42, 0x70, 0x2F, 0x68), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x54, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x34, 0xF4, 0xF0, 0xFF), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x42, 0xFF, 0xD8, 0xF3), 1));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>Lapisan lokal untuk input dan kartu yang memerlukan kontras lebih tinggi.</summary>
    public static LinearGradientBrush CreateInnerSurface(bool dark)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };

        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x18, 0x12, 0x2C), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x58, 0x09, 0x0D, 0x18), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0x28, 0x11, 0x31), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x84, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x68, 0xF8, 0xF8, 0xFF), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x76, 0xFF, 0xE9, 0xF8), 1));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>Gerakkan pantulan cahaya halus mengikuti pointer.</summary>
    public static void UpdateSpecular(Border layer, Point position, Size surfaceSize)
    {
        if (surfaceSize.Width <= 0 || surfaceSize.Height <= 0)
        {
            return;
        }

        var center = new Point(
            Math.Clamp(position.X / surfaceSize.Width, 0, 1),
            Math.Clamp(position.Y / surfaceSize.Height, 0, 1));

        if (layer.Background is not RadialGradientBrush brush)
        {
            brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                RadiusX = 0.42,
                RadiusY = 0.62
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x72, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), 0.35));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            layer.Background = brush;
        }

        brush.Center = center;
        brush.GradientOrigin = center;
        layer.Opacity = 0.24;
    }

    public static void HideSpecular(Border layer) => layer.Opacity = 0;

    /// <summary>Border specular: terang di kiri-atas, melemah, sedikit menyala lagi di kanan-bawah.</summary>
    public static LinearGradientBrush CreateRim(bool dark)
    {
        var strong = dark
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);
        var soft = dark
            ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(strong, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5));
        brush.GradientStops.Add(new GradientStop(soft, 1));
        brush.Freeze();
        return brush;
    }
}
