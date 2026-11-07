using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

/// <summary>
/// The island's dimension and colour tokens (canvas Page 2, board 00). One inset everywhere: P 16 on
/// every open page. Corners are concentric: a corner item's radius plus its inset is the island's RE.
/// </summary>
internal static class IslandTokens
{
    // Compact pill
    public const double Hc = 32, Rc = 16, Ic = 20, Pt = 12;
    public const double Pc = (Hc - Ic) / 2;                 // 6: the slot sits concentric in the pill

    // Open island
    public const double P = 16;                             // the one inset
    public const double Re = 36;                            // island corner
    public const double Btn = 40, Ri = Re - P;              // corner items: 40 tall, R 20
    public const double Chip = 28, Row = 56, RowRadius = Re - 8, Tile = 52;
    public const double Header = 32;

    // Widths
    public const double WidthIdle = 150, WidthLive = 196, WidthHud = 240, WidthS = 360, WidthM = 420, WidthL = 564;

    // The live object of V3 (boards 19-20): a round thing that leaves the island's left side.
    public const double ObjectSize = 104, ObjectOut = 72, ObjectTop = 64;

    public static readonly Brush Island = Frozen(0x00, 0x00, 0x00);
    public static readonly Brush Surface1 = Frozen(0x1C, 0x1C, 0x1E);
    public static readonly Brush Surface2 = Frozen(0x2C, 0x2C, 0x2E);
    public static readonly Brush Text1 = Frozen(0xF5, 0xF5, 0xF7);
    public static readonly Brush Text2 = Frozen(0x98, 0x98, 0x9F);
    public static readonly Brush Text3 = Frozen(0x7C, 0x7C, 0x80);

    public static readonly Color Agent = Color.FromRgb(0x32, 0xD7, 0x4B);
    public static readonly Color Wait = Color.FromRgb(0xFF, 0xD6, 0x0A);
    public static readonly Color Error = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Timer = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Water = Color.FromRgb(0x64, 0xD2, 0xFF);
    public static readonly Color Music = Color.FromRgb(0xFF, 0x7A, 0x45);

    public static Brush Frozen(byte r, byte g, byte b) => Frozen(Color.FromRgb(r, g, b));

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
