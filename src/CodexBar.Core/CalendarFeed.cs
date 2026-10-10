using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>One event on a day, as the notch shows it.</summary>
public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string? JoinUrl);

/// <summary>Finds the video-call link in an event's location, description or URL.</summary>
public static partial class MeetingLinks
{
    [GeneratedRegex(@"https://(?:[\w-]+\.)?(?:meet\.google\.com|teams\.microsoft\.com|teams\.live\.com|zoom\.us|[\w-]+\.zoom\.us|webex\.com|[\w-]+\.webex\.com|whereby\.com|meet\.jit\.si)/[^\s""<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    public static string? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (!string.IsNullOrEmpty(text) && Link().Match(text) is { Success: true } match)
            {
                return match.Value.TrimEnd('.', ',', ')', ';');
            }
        }

        return null;
    }
}

/// <summary>
/// Reads an iCalendar feed (the "secret address in iCal format" calendars offer) and lists one
/// day's events: time zones (TZID, UTC, floating), all-day events, cancelled ones left out, and
/// recurrence — DAILY / WEEKLY (BYDAY) / MONTHLY / YEARLY with INTERVAL, COUNT, UNTIL, EXDATE and
/// moved single instances (RECURRENCE-ID). That covers what calendars actually send for meetings.
/// </summary>
public static class IcsCalendar
{
    private sealed class Entry
    {
        public string Uid = string.Empty;
        public string Summary = string.Empty;
        public string Location = string.Empty;
        public string Description = string.Empty;
        public string Url = string.Empty;
        public DateTimeOffset Start;
        public DateTimeOffset End;
        public bool AllDay;
        public bool Cancelled;
        public string? RRule;
        public readonly List<DateTimeOffset> ExDates = [];
        public DateTimeOffset? RecurrenceId;
    }

    /// <summary>The events that start on <paramref name="day"/> (in <paramref name="zone"/>), earliest first.</summary>
    public static IReadOnlyList<CalendarEvent> EventsOn(string ics, DateOnly day, TimeZoneInfo zone)
    {
        var entries = Parse(ics, zone);
        var moved = entries.Where(entry => entry.RecurrenceId is not null)
            .Select(entry => (entry.Uid, Original: entry.RecurrenceId!.Value))
            .ToHashSet();
        var result = new List<CalendarEvent>();
        foreach (var entry in entries.Where(entry => !entry.Cancelled))
        {
            foreach (var start in Occurrences(entry, day, zone))
            {
                if (entry.RecurrenceId is null && moved.Contains((entry.Uid, start)))
                {
                    continue;   // this instance was moved; its own entry shows it
                }

                var end = start + (entry.End - entry.Start);
                result.Add(new CalendarEvent(entry.Summary, start, end, entry.AllDay,
                    MeetingLinks.Find(entry.Location, entry.Url, entry.Description)));
            }
        }

        return result.OrderBy(item => item.AllDay ? 0 : 1).ThenBy(item => item.Start).ToList();
    }

    private static IEnumerable<DateTimeOffset> Occurrences(Entry entry, DateOnly day, TimeZoneInfo zone)
    {
        var firstDay = LocalDate(entry.Start, zone, entry.AllDay);
        if (entry.RRule is null || entry.RecurrenceId is not null)
        {
            if (firstDay == day)
            {
                yield return entry.Start;
            }

            yield break;
        }

        if (day < firstDay)
        {
            yield break;
        }

        var rule = entry.RRule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1], StringComparer.OrdinalIgnoreCase);
        var frequency = rule.GetValueOrDefault("FREQ", string.Empty).ToUpperInvariant();
        var interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var every) && every > 0 ? every : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var times) && times > 0 ? times : null;
        DateTimeOffset? until = rule.TryGetValue("UNTIL", out var untilText) ? ParseStamp(untilText, null, zone).Value : null;
        var byDay = rule.TryGetValue("BYDAY", out var days)
            ? days.Split(',').Select(code => DayOf(code[^2..])).Where(d => d is not null).Select(d => d!.Value).ToHashSet()
            : null;

        // The occurrence's start: the same wall-clock time on that day, in the event's zone.
        var startLocal = TimeZoneInfo.ConvertTime(entry.Start, zone);
        DateTimeOffset At(DateOnly date)
        {
            var local = date.ToDateTime(TimeOnly.FromTimeSpan(entry.AllDay ? TimeSpan.Zero : startLocal.TimeOfDay));
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        bool Matches(DateOnly date) => frequency switch
        {
            "DAILY" => (date.DayNumber - firstDay.DayNumber) % interval == 0,
            "WEEKLY" => (WeekStart(date).DayNumber - WeekStart(firstDay).DayNumber) / 7 % interval == 0
                        && (byDay ?? [firstDay.DayOfWeek]).Contains(date.DayOfWeek),
            "MONTHLY" => ((date.Year - firstDay.Year) * 12 + date.Month - firstDay.Month) % interval == 0 && date.Day == firstDay.Day,
            "YEARLY" => (date.Year - firstDay.Year) % interval == 0 && date.Month == firstDay.Month && date.Day == firstDay.Day,
            _ => date == firstDay
        };

        if (!Matches(day))
        {
            yield break;
        }

        var candidate = At(day);
        if (until is { } last && candidate > last)
        {
            yield break;
        }

        if (count is { } limit)
        {
            // Count the occurrences from the first one up to this day (bounded: a few years of days).
            var seen = 0;
            for (var date = firstDay; date <= day && seen <= limit; date = date.AddDays(1))
            {
                if (Matches(date))
                {
                    seen++;
                }

                if (date.DayNumber - firstDay.DayNumber > 3660)
                {
                    break;
                }
            }

            if (seen > limit)
            {
                yield break;
            }
        }

        if (entry.ExDates.Any(excluded => excluded == candidate || (entry.AllDay && LocalDate(excluded, zone, true) == day)))
        {
            yield break;
        }

        yield return candidate;
    }

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));   // weeks start on Monday (WKST=MO)

    private static DayOfWeek? DayOf(string code) => code.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null
    };

    private static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo zone, bool allDay) =>
        DateOnly.FromDateTime(allDay ? value.DateTime : TimeZoneInfo.ConvertTime(value, zone).DateTime);

    private static List<Entry> Parse(string ics, TimeZoneInfo zone)
    {
        var entries = new List<Entry>();
        Entry? current = null;
        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Entry();
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && current.Start != default)
                {
                    if (current.End == default)
                    {
                        current.End = current.AllDay ? current.Start.AddDays(1) : current.Start;
                    }

                    entries.Add(current);
                }

                current = null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var colon = IndexOfValueColon(line);
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon];
            var value = line[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0].ToUpperInvariant();
            var parameters = parts.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
            parameters.TryGetValue("TZID", out var tzid);
            switch (name)
            {
                case "UID":
                    current.Uid = value;
                    break;
                case "SUMMARY":
                    current.Summary = Unescape(value);
                    break;
                case "LOCATION":
                    current.Location = Unescape(value);
                    break;
                case "DESCRIPTION":
                    current.Description = Unescape(value);
                    break;
                case "URL":
                    current.Url = value;
                    break;
                case "STATUS":
                    current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                    break;
                case "RRULE":
                    current.RRule = value;
                    break;
                case "DTSTART":
                    (current.Start, current.AllDay) = ParseStamp(value, tzid, zone);
                    break;
                case "DTEND":
                    current.End = ParseStamp(value, tzid, zone).Value;
                    break;
                case "RECURRENCE-ID":
                    current.RecurrenceId = ParseStamp(value, tzid, zone).Value;
                    break;
                case "EXDATE":
                    foreach (var stamp in value.Split(','))
                    {
                        current.ExDates.Add(ParseStamp(stamp, tzid, zone).Value);
                    }
                    break;
            }
        }

        return entries;
    }

    // "DESCRIPTION;X=a:b:c" — the value starts after the first colon outside quotes.
    private static int IndexOfValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var builder = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                builder.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }

            builder.Clear().Append(raw.TrimEnd('\r'));
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    /// <summary>A DATE ("20261010", all day), UTC ("…T103000Z"), zoned (TZID) or floating stamp.</summary>
    private static (DateTimeOffset Value, bool AllDay) ParseStamp(string value, string? tzid, TimeZoneInfo zone)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (new DateTimeOffset(date, zone.GetUtcOffset(date)), true);
        }

        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            return (new DateTimeOffset(utc, TimeSpan.Zero), false);
        }

        if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var eventZone = tzid is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var found) ? found : zone;
            return (new DateTimeOffset(local, eventZone.GetUtcOffset(local)), false);
        }

        return (default, false);
    }
}
