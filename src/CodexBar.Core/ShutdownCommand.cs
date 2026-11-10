using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>What was asked of the PC's power: turn off in some seconds, or call that off.</summary>
public readonly record struct ShutdownRequest(bool Cancel, int Seconds, TimeOnly? At = null);

/// <summary>
/// Reads the typed forms of Windows' own command: "shutdown -s -t 3600", "shutdown /s /t 3600",
/// "shutdown -a", the short "shutdown 30m" / "matikan 1h", and a time of day: "shutdown 22:30", "matikan jam 22".
/// Anything under MinSeconds is raised to it,
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

        if (Clock().Match(typed) is { Success: true } clock)
        {
            var hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (hour > 23 || minute > 59)
            {
                return false;
            }

            request = new ShutdownRequest(false, 0, new TimeOnly(hour, minute));
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

    /// <summary>Seconds until the next time the clock shows this: later today, else tomorrow.</summary>
    public static int SecondsUntil(TimeOnly at, DateTimeOffset now)
    {
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, at.Hour, at.Minute, 0, now.Offset);
        if (target <= now)
        {
            target = target.AddDays(1);
        }

        return (int)Math.Clamp(Math.Ceiling((target - now).TotalSeconds), MinSeconds, MaxSeconds);
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

    // "22:30" or "22.30" alone, or with a word for "at" ("jam 22", "pukul 7.15", "at 22:30", "@22:30").
    [GeneratedRegex(@"^(shutdown|matikan)\s+(?:(?:jam|pukul|at|@)\s*(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?|(?<h>\d{1,2})[:.](?<m>\d{2}))$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(shutdown\s+[-/]a|batal(kan)?\s+(shutdown|matikan))$")]
    private static partial Regex Abort();

    [GeneratedRegex(@"^shutdown(\.exe)?\s+[-/]s\s+[-/]t\s+(?<t>\d{1,12})$")]
    private static partial Regex Full();

    [GeneratedRegex(@"^(shutdown|matikan)\s+(?<n>\d{1,9})\s*(?<u>s|d|dtk|detik|m|mnt|menit|min|h|j|jam|hour)?$")]
    private static partial Regex Short();
}
