using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>
/// Detects provider "usage limit / rate limit" errors and works out when to retry, so
/// CodexBar can auto-resume a prompt once the quota resets. Pure and dependency-free so
/// the parsing (the tricky part) is fully unit-testable without ever hitting a real limit.
/// </summary>
public static partial class RateLimit
{
    public static bool IsRateLimited(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429");
    }

    /// <summary>
    /// Parse a retry moment from a provider message relative to <paramref name="now"/>.
    /// Handles Codex's "try again at 3:23 AM" and "try again in 2 hours/30 minutes".
    /// Returns null when no time is present.
    /// </summary>
    public static DateTimeOffset? ParseRetryAt(string? message, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var at = AtTimeRegex().Match(message);
        if (at.Success)
        {
            var hour = int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture);
            var meridiem = at.Groups[3].Value.ToUpperInvariant();
            if (hour is < 0 or > 23 || minute is < 0 or > 59)
            {
                return null;
            }

            if (meridiem == "PM" && hour != 12)
            {
                hour += 12;
            }
            else if (meridiem == "AM" && hour == 12)
            {
                hour = 0;
            }

            var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
            if (candidate <= now)
            {
                candidate = candidate.AddDays(1);
            }

            return candidate;
        }

        var inMatch = InDurationRegex().Match(message);
        if (inMatch.Success)
        {
            var amount = int.Parse(inMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = inMatch.Groups[2].Value.ToLowerInvariant();
            return unit switch
            {
                "hour" or "hours" => now.AddHours(amount),
                "minute" or "minutes" or "min" => now.AddMinutes(amount),
                "second" or "seconds" or "sec" => now.AddSeconds(amount),
                _ => null
            };
        }

        return null;
    }

    /// <summary>Retry moment from an HTTP <c>Retry-After</c> header value in seconds (e.g. Anthropic 429).</summary>
    public static DateTimeOffset FromRetryAfterSeconds(double seconds, DateTimeOffset now) =>
        now.AddSeconds(Math.Max(0, seconds));

    [GeneratedRegex(@"try again at\s+(\d{1,2}):(\d{2})\s*(AM|PM)?", RegexOptions.IgnoreCase)]
    private static partial Regex AtTimeRegex();

    [GeneratedRegex(@"try again in\s+(\d+)\s*(hours?|minutes?|min|seconds?|sec)", RegexOptions.IgnoreCase)]
    private static partial Regex InDurationRegex();
}
