namespace CodexBar.App;

/// <summary>Code-side twin of Tokens.xaml. Keep both in step with canvas board 16.</summary>
internal static class DesignTokens
{
    public const double S1 = 12, S2 = 16, S3 = 10, S4 = 12;
    public const double R1 = 48, R2 = R1 - S1, R3 = R2 - S2;
    public const double Row = 48, Header = 44, RowIcon = 32;
    public const double ListPadY = S2 - (Row - RowIcon) / 2;
    public const double Tile = 2 * (R2 - S1);

    /// <summary>Radius for a pane of height <paramref name="h"/>: a pane shorter than 2 * R2 becomes a pill.</summary>
    public static double RadiusFor(double h) => System.Math.Min(R2, h / 2);
}
