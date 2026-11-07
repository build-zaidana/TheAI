using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>A to-do, or a reminder when it has a time.</summary>
public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset? DueAt { get; set; }
    public bool Done { get; set; }
    /// <summary>The notch already called it out (so it isn't announced twice).</summary>
    public bool Announced { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The notch's reminders and to-dos. Typed the way people say them: "15m stretch", "1h30m call",
/// "14:30 review", "7pm gym", "tomorrow 9:00 standup" / "besok 9.00 standup", or just "buy milk"
/// (a to-do without a time). Kept in reminders.json.
/// </summary>
public sealed partial class ReminderList
{
    public const int MaxOpen = 50;

    public List<ReminderItem> Items { get; set; } = [];

    /// <summary>Open items: due ones first (soonest first), then to-dos newest first.</summary>
    public IEnumerable<ReminderItem> Open =>
        Items.Where(item => !item.Done)
            .OrderBy(item => item.DueAt is null)
            .ThenBy(item => item.DueAt)
            .ThenByDescending(item => item.CreatedAt);

    [GeneratedRegex(@"^(?:(?<h>\d{1,2})\s*(?:h|j|jam)\s*)?(?:(?<m>\d{1,3})\s*(?:m|min|mnt|menit)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ampm>am|pm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClockPattern();

    /// <summary>"15m stretch" → ("stretch", now + 15 min); "buy milk" → ("buy milk", null). Null when empty.</summary>
    public static (string Text, DateTimeOffset? DueAt)? Parse(string input, DateTimeOffset now)
    {
        var words = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        DateTimeOffset? due = null;
        var day = now.Date;
        var dayWord = false;
        if (words[0].ToLowerInvariant() is "tomorrow" or "besok")
        {
            day = day.AddDays(1);
            dayWord = true;
            words.RemoveAt(0);
        }
        else if (words[0].ToLowerInvariant() is "today" or "hari-ini" or "nanti")
        {
            dayWord = true;
            words.RemoveAt(0);
        }

        if (words.Count > 0 && Clock(words[0]) is { } clock && (dayWord || words[0].Contains(':') || words[0].Contains('.') || words[0].EndsWith("am", StringComparison.OrdinalIgnoreCase) || words[0].EndsWith("pm", StringComparison.OrdinalIgnoreCase)))
        {
            due = new DateTimeOffset(day + clock, now.Offset);
            if (!dayWord && due <= now)
            {
                due = due.Value.AddDays(1);   // "7:30" said in the evening means tomorrow morning
            }

            words.RemoveAt(0);
        }
        else if (!dayWord && words.Count > 0 && Duration(words[0]) is { } duration)
        {
            due = now + duration;
            words.RemoveAt(0);
        }
        else if (dayWord && due is null)
        {
            due = new DateTimeOffset(day + TimeSpan.FromHours(9), now.Offset);   // "tomorrow call mom": 9:00
        }

        var text = string.Join(' ', words).Trim();
        return text.Length == 0 ? null : (text.Length > 200 ? text[..200] : text, due);
    }

    private static TimeSpan? Duration(string word)
    {
        // A unit is required: a bare number is too ambiguous ("3 apples").
        var match = DurationPattern().Match(word);
        if (!match.Success || !word.Any(char.IsDigit) || !word.Any(char.IsLetter))
        {
            return null;
        }

        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var total = new TimeSpan(hours, minutes, 0);
        return total > TimeSpan.Zero && total <= TimeSpan.FromDays(7) ? total : null;
    }

    private static TimeSpan? Clock(string word)
    {
        var match = ClockPattern().Match(word);
        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12)
            {
                return null;
            }

            hour = hour % 12 + (match.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        }

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeSpan(hour, minute, 0) : null;
    }

    public ReminderItem? Add(string input, DateTimeOffset now)
    {
        if (Parse(input, now) is not { } parsed || Items.Count(item => !item.Done) >= MaxOpen)
        {
            return null;
        }

        var item = new ReminderItem { Text = parsed.Text, DueAt = parsed.DueAt, CreatedAt = now };
        Items.Add(item);
        return item;
    }

    /// <summary>Reminders whose time has come and that haven't been called out yet.</summary>
    public IReadOnlyList<ReminderItem> DueNow(DateTimeOffset now) =>
        Items.Where(item => !item.Done && !item.Announced && item.DueAt <= now).OrderBy(item => item.DueAt).ToList();

    public void Complete(string id, bool done = true)
    {
        if (Items.FirstOrDefault(item => item.Id == id) is { } item)
        {
            item.Done = done;
        }
    }

    public void Snooze(string id, TimeSpan by, DateTimeOffset now)
    {
        if (Items.FirstOrDefault(item => item.Id == id) is { } item)
        {
            item.DueAt = now + by;
            item.Announced = false;
            item.Done = false;
        }
    }

    public void Remove(string id) => Items.RemoveAll(item => item.Id == id);

    /// <summary>Finished items older than a week go.</summary>
    public void Prune(DateTimeOffset now) =>
        Items.RemoveAll(item => item.Done && (item.DueAt ?? item.CreatedAt) < now.AddDays(-7));

    public static ReminderList Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ReminderList>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Reminders", exception.Message);
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Reminders", exception.Message);
        }
    }

    /// <summary>"in 12m", "14:30", "tomorrow 9:00", or nothing for a to-do.</summary>
    public static string When(ReminderItem item, DateTimeOffset now, Func<string, string, string> text)
    {
        if (item.DueAt is not { } due)
        {
            return string.Empty;
        }

        var left = due - now;
        if (left <= TimeSpan.Zero)
        {
            return text("sekarang", "now");
        }

        if (left < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        }

        var clock = due.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture);
        return due.ToLocalTime().Date == now.ToLocalTime().Date ? clock
            : due.ToLocalTime().Date == now.ToLocalTime().Date.AddDays(1) ? text("besok ", "tomorrow ") + clock
            : due.ToLocalTime().ToString("d MMM ", CultureInfo.CurrentUICulture) + clock;
    }
}
