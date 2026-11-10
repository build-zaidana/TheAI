using System.Globalization;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>
/// How much water was drunk per day, and when the last glass was, for the notch's gentle
/// reminder. Kept in water.json for 30 days; nothing else is recorded.
/// </summary>
public sealed class WaterLog
{
    private const int KeepDays = 30;

    /// <summary>Millilitres per day, keyed "yyyy-MM-dd" (local date).</summary>
    public Dictionary<string, int> Days { get; set; } = [];

    public DateTimeOffset? LastDrink { get; set; }

    /// <summary>When the reminder last went off (so it is not repeated before the next interval).</summary>
    public DateTimeOffset? LastNudge { get; set; }

    private static string Key(DateTimeOffset day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public int Today(DateTimeOffset now) => Days.GetValueOrDefault(Key(now));

    public void Add(int millilitres, DateTimeOffset now)
    {
        var key = Key(now);
        Days[key] = Math.Max(0, Days.GetValueOrDefault(key) + millilitres);
        if (millilitres > 0)
        {
            LastDrink = now;
        }

        Prune(now);
    }

    /// <summary>The last seven days, oldest first, today last.</summary>
    public IReadOnlyList<(DateTimeOffset Day, int Millilitres)> Week(DateTimeOffset now) =>
        Enumerable.Range(0, 7)
            .Select(back => now.AddDays(back - 6))
            .Select(day => (day, Days.GetValueOrDefault(Key(day))))
            .ToList();

    /// <summary>
    /// When the next reminder is due: an interval after the last glass (or the last reminder, or the
    /// start of the day), only between the active hours and only until the goal is reached.
    /// </summary>
    public static DateTimeOffset? NextNudge(WaterLog log, WaterPlan plan, DateTimeOffset now)
    {
        if (plan.EveryMinutes <= 0 || log.Today(now) >= plan.GoalMl)
        {
            return null;
        }

        var start = new DateTimeOffset(now.Date.AddHours(plan.FromHour), now.Offset);
        var end = new DateTimeOffset(now.Date.AddHours(plan.UntilHour), now.Offset);
        if (now >= end)
        {
            return null;   // tomorrow's first reminder is worked out tomorrow
        }

        var last = new[] { log.LastDrink, log.LastNudge }.Where(time => time?.Date == now.Date).Max() ?? start;
        var due = (last < start ? start : last).AddMinutes(plan.EveryMinutes);
        return due >= end ? null : due;
    }

    public void Prune(DateTimeOffset now)
    {
        var oldest = Key(now.AddDays(-KeepDays));
        foreach (var key in Days.Keys.Where(key => string.CompareOrdinal(key, oldest) < 0).ToList())
        {
            Days.Remove(key);
        }
    }

    public static WaterLog Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WaterLog>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Water", exception.Message);
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
            DiagnosticsLog.Error("Water", exception.Message);
        }
    }
}

/// <summary>The user's water settings, in one place for <see cref="WaterLog.NextNudge"/>.</summary>
public readonly record struct WaterPlan(int GoalMl, int GlassMl, int EveryMinutes, int FromHour, int UntilHour)
{
    public static WaterPlan From(AppSettings settings) => new(
        Math.Clamp(settings.WaterGoalMl, 500, 6000),
        Math.Clamp(settings.WaterGlassMl, 100, 1000),
        settings.WaterEveryMinutes <= 0 ? 0 : Math.Clamp(settings.WaterEveryMinutes, 15, 240),
        Math.Clamp(settings.WaterFromHour, 0, 23),
        Math.Clamp(settings.WaterUntilHour, 1, 24));
}
