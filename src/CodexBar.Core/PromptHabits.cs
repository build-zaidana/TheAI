using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>What the last turn looked like when the user typed their next prompt.</summary>
public enum TurnContext
{
    /// <summary>A normal answer.</summary>
    Plain,

    /// <summary>The agent changed files.</summary>
    Files,

    /// <summary>The answer ended in a question or a numbered list of options.</summary>
    Question,

    /// <summary>The turn failed.</summary>
    Error
}

/// <summary>
/// Learns the short follow-up prompts a person keeps sending ("commit dulu perubahannya",
/// "lanjut", "kerjakan #3") and in which situation, to suggest the next one. Only short prompts
/// sent right after a finished turn count; everything stays in a local JSON file. Prompts ending
/// in a number are learnt as a sequence, so after "kerjakan #3" the suggestion is "kerjakan #4".
/// </summary>
public sealed partial class PromptHabits
{
    private const int MaxWords = 8;
    private const int MaxChars = 60;
    private const int MaxEntries = 200;

    private readonly string? _path;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        public string Text { get; set; } = string.Empty;
        public int LastNumber { get; set; } = -1;
        public Dictionary<TurnContext, int> Counts { get; set; } = new();
        public DateTimeOffset LastUsed { get; set; }

        public int Total => Counts.Values.Sum();
    }

    public PromptHabits(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path));
            foreach (var entry in loaded ?? [])
            {
                _entries[Key(entry.Text)] = entry;
            }
        }
        catch (Exception)
        {
            // A broken file only costs the learnt habits.
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "prompt-habits.json");

    /// <summary>Remember a prompt sent right after a turn of the given kind.</summary>
    public void Record(string prompt, TurnContext context, DateTimeOffset? now = null)
    {
        prompt = prompt.Trim();
        if (!IsHabitCandidate(prompt))
        {
            return;
        }

        var (template, number) = Template(prompt);
        var key = Key(template);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Text = template };
            _entries[key] = entry;
        }

        entry.Text = template; // keep the latest casing
        entry.Counts[context] = entry.Counts.GetValueOrDefault(context) + 1;
        entry.LastUsed = now ?? DateTimeOffset.Now;
        if (number >= 0)
        {
            entry.LastNumber = number;
        }

        if (_entries.Count > MaxEntries)
        {
            foreach (var stale in _entries.OrderBy(pair => pair.Value.Total).ThenBy(pair => pair.Value.LastUsed)
                         .Take(_entries.Count - MaxEntries).Select(pair => pair.Key).ToArray())
            {
                _entries.Remove(stale);
            }
        }

        Save();
    }

    /// <summary>The most likely next prompt for this situation, or null when nothing is confident enough.</summary>
    public string? Predict(TurnContext context, DateTimeOffset? now = null) => PredictMany(context, 1, now).FirstOrDefault();

    /// <summary>Up to <paramref name="count"/> confident guesses, best first.</summary>
    public IReadOnlyList<string> PredictMany(TurnContext context, int count, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        return _entries.Values
            // Confident: used at least twice in this situation, or a clear overall habit.
            .Where(entry => entry.Counts.GetValueOrDefault(context) >= 2 || entry.Total >= 4)
            .Select(entry => (Entry: entry, Score: Score(entry, context, at)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Take(count)
            .Select(candidate => Render(candidate.Entry))
            .ToArray();
    }

    public static bool IsHabitCandidate(string prompt) =>
        prompt.Length is > 0 and <= MaxChars &&
        !prompt.StartsWith('[') && // "[Request interrupted by user]" and other markers
        !prompt.StartsWith('/') && // slash commands
        prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxWords &&
        !prompt.Contains('\n');

    /// <summary>Classify a finished answer.</summary>
    public static TurnContext Classify(string answer, bool changedFiles, bool failed)
    {
        if (failed)
        {
            return TurnContext.Error;
        }

        var tail = answer.TrimEnd();
        if (tail.EndsWith('?') || NumberedOptions().Matches(answer).Count >= 2)
        {
            return TurnContext.Question;
        }

        return changedFiles ? TurnContext.Files : TurnContext.Plain;
    }

    private static double Score(Entry entry, TurnContext context, DateTimeOffset now)
    {
        var inContext = entry.Counts.GetValueOrDefault(context);
        var days = Math.Max(0, (now - entry.LastUsed).TotalDays);
        var recency = 1 / (1 + days / 7);
        return (inContext * 3 + entry.Total) * recency;
    }

    private static string Render(Entry entry) =>
        entry.LastNumber >= 0 ? entry.Text.Replace("{n}", (entry.LastNumber + 1).ToString(), StringComparison.Ordinal) : entry.Text;

    // "kerjakan #3" → ("kerjakan #{n}", 3); anything else stays as is.
    private static (string Template, int Number) Template(string prompt)
    {
        var match = TrailingNumber().Match(prompt);
        return match.Success && int.TryParse(match.Groups[2].Value, out var number)
            ? (match.Groups[1].Value + "{n}", number)
            : (prompt, -1);
    }

    private static string Key(string text) => text.Trim().ToLowerInvariant();

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries.Values.ToList()));
        }
        catch (Exception)
        {
        }
    }

    [GeneratedRegex(@"^(.*?#\s*)(\d{1,3})$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"(?m)^\s*(\d{1,2})[.)]\s+\S")]
    private static partial Regex NumberedOptions();
}
