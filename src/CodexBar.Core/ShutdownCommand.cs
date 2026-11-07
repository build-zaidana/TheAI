using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>What was asked of the PC's power: turn off in some seconds, or call that off.</summary>
public readonly record struct ShutdownRequest(bool Cancel, int Seconds);

/// <summary>
/// Reads the typed forms of Windows' own command: "shutdown -s -t 3600", "shutdown /s /t 3600",
/// "shutdown -a", and the short "shutdown 30m" / "matikan 1h". Anything under MinSeconds is raised to it,
/// so a slip never turns the machine off with no time to take it back.
/// </summary>
public static partial class ShutdownCommand
{
    public const int MinSeconds = 30;
    public const int MaxSeconds = 315_360_000;   // the most shutdown.exe takes (10 years)

    public static bool TryParse(string? text, out ShutdownRequest request)
    {
        request = default;
        var typed = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (Abort().IsMatch(typed))
        {
            request = new ShutdownRequest(true, 0);
            return true;
        }

        if (Full().Match(typed) is { Success: true } full)
        {
            return Seconds(full.Groups["t"].Value, 1, out request);
        }

        if (Short().Match(typed) is { Success: true } brief)
        {
            var unit = brief.Groups["u"].Value;
            var scale = unit.StartsWith('h') || unit.StartsWith('j') ? 3600 : unit.StartsWith('s') || unit.StartsWith('d') ? 1 : 60;
            return Seconds(brief.Groups["n"].Value, scale, out request);
        }

        return false;
    }

    private static bool Seconds(string number, int scale, out ShutdownRequest request)
    {
        request = default;
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        request = new ShutdownRequest(false, (int)Math.Clamp(value * scale, MinSeconds, MaxSeconds));
        return true;
    }

    [GeneratedRegex(@"^(shutdown\s+[-/]a|batal(kan)?\s+(shutdown|matikan))$")]
    private static partial Regex Abort();

    [GeneratedRegex(@"^shutdown(\.exe)?\s+[-/]s\s+[-/]t\s+(?<t>\d{1,12})$")]
    private static partial Regex Full();

    [GeneratedRegex(@"^(shutdown|matikan)\s+(?<n>\d{1,9})\s*(?<u>s|d|dtk|detik|m|mnt|menit|min|h|j|jam|hour)?$")]
    private static partial Regex Short();
}
