using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>One line of synced lyrics and when it starts.</summary>
public sealed record LyricLine(TimeSpan Start, string Text);

/// <summary>
/// Synced lyrics in LRC form ("[01:23.45] line"), as LRCLIB serves them: parsed once, then the
/// line playing at a position is looked up for the notch.
/// </summary>
public static partial class SyncedLyrics
{
    [GeneratedRegex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    /// <summary>The timed lines, in order (a line with several stamps appears once per stamp; empty lines kept as pauses).</summary>
    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }

        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0)
            {
                continue; // [ar:...] tags and untimed text
            }

            var text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                var fraction = stamp.Groups[3].Success ? stamp.Groups[3].Value.PadRight(3, '0')[..3] : "0";
                var start = new TimeSpan(0, 0,
                    int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(fraction, CultureInfo.InvariantCulture));
                lines.Add(new LyricLine(start, text));
            }
        }

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        return lines;
    }

    /// <summary>Index of the line playing at <paramref name="position"/>; -1 before the first.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var index = -1;
        for (var i = 0; i < lines.Count && lines[i].Start <= position; i++)
        {
            index = i;
        }

        return index;
    }
}
