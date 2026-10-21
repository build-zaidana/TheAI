using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>
/// What happened today, for the recap: focus sessions, what the agents finished, files they
/// changed, long terminal commands and pull request runs. One small JSON file per day under
/// <c>%LOCALAPPDATA%\CodexBar\days</c>; nothing leaves the PC.
/// </summary>
public sealed class DayLog
{
    public const int MaxItems = 40;

    public DateOnly Day { get; set; }
    public int FocusSessions { get; set; }
    public int FocusMinutes { get; set; }
    public List<string> AgentTasks { get; set; } = [];
    public List<string> Agents { get; set; } = [];
    public HashSet<string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Commands { get; set; }
    public int FailedCommands { get; set; }
    public List<string> PullRequests { get; set; } = [];
    /// <summary>The evening invitation was shown (once a day).</summary>
    public bool Invited { get; set; }

    public bool IsEmpty => FocusSessions == 0 && AgentTasks.Count == 0 && Files.Count == 0 && Commands == 0 && PullRequests.Count == 0;

    public void Focus(TimeSpan length)
    {
        FocusSessions++;
        FocusMinutes += (int)Math.Round(length.TotalMinutes);
    }

    public void AgentTask(string agent, string task)
    {
        task = OneLine(task, 80);
        if (task.Length == 0)
        {
            return;
        }

        AgentTasks.Remove(task);
        AgentTasks.Add(task);
        Trim(AgentTasks);
        if (agent.Length > 0 && !Agents.Contains(agent))
        {
            Agents.Add(agent);
        }
    }

    public void ChangedFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => path.Length > 0))
        {
            Files.Add(path);
        }
    }

    public void Command(bool succeeded)
    {
        Commands++;
        if (!succeeded)
        {
            FailedCommands++;
        }
    }

    public void PullRequest(string outcome)
    {
        PullRequests.Remove(outcome);
        PullRequests.Add(outcome);
        Trim(PullRequests);
    }

    /// <summary>A few lines for a standup, in Markdown.</summary>
    public string ToMarkdown(Func<string, string, string> text)
    {
        var culture = CultureInfo.CurrentUICulture;
        var builder = new StringBuilder();
        builder.Append("**").Append(text("Hari ini", "Today")).Append(" (")
            .Append(Day.ToString("d MMM", culture)).AppendLine(")**");
        if (FocusSessions > 0)
        {
            builder.Append("- ").Append(text("Fokus", "Focus")).Append(": ").Append(FocusSessions)
                .Append(' ').Append(text("sesi", FocusSessions == 1 ? "session" : "sessions")).Append(", ")
                .AppendLine(Duration(FocusMinutes, text));
        }

        if (AgentTasks.Count > 0)
        {
            builder.Append("- ").Append(text("Selesai bersama agent", "Done with agents"));
            if (Agents.Count > 0)
            {
                builder.Append(" (").Append(string.Join(", ", Agents)).Append(')');
            }

            builder.AppendLine(":");
            foreach (var task in AgentTasks.TakeLast(8))
            {
                builder.Append("  - ").AppendLine(task);
            }
        }

        var counts = new List<string>();
        if (Files.Count > 0)
        {
            counts.Add($"{Files.Count} {text("file diubah", Files.Count == 1 ? "file changed" : "files changed")}");
        }

        if (Commands > 0)
        {
            counts.Add(FailedCommands > 0
                ? $"{Commands} {text("perintah", "commands")} ({FailedCommands} {text("gagal", "failed")})"
                : $"{Commands} {text("perintah", Commands == 1 ? "command" : "commands")}");
        }

        if (counts.Count > 0)
        {
            builder.Append("- ").AppendLine(string.Join(", ", counts));
        }

        foreach (var outcome in PullRequests)
        {
            builder.Append("- ").AppendLine(outcome);
        }

        return builder.ToString().TrimEnd();
    }

    public static string Duration(int minutes, Func<string, string, string> text) =>
        minutes >= 60
            ? minutes % 60 == 0 ? $"{minutes / 60}{text("j", "h")}" : $"{minutes / 60}{text("j", "h")} {minutes % 60}m"
            : $"{minutes}m";

    // ---- Storage ---------------------------------------------------------------------------

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "days");

    public static string PathFor(DateOnly day, string? folder = null) =>
        Path.Combine(folder ?? Folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    public static DayLog Load(DateOnly day, string? folder = null)
    {
        try
        {
            var path = PathFor(day, folder);
            if (File.Exists(path) && JsonSerializer.Deserialize<DayLog>(File.ReadAllText(path)) is { } log)
            {
                log.Day = day;
                log.Files = new HashSet<string>(log.Files, StringComparer.OrdinalIgnoreCase);
                return log;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("DayLog", exception.Message);
        }

        return new DayLog { Day = day };
    }

    public void Save(string? folder = null)
    {
        try
        {
            var path = PathFor(Day, folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("DayLog", exception.Message);
        }
    }

    /// <summary>Days older than <paramref name="keep"/> are deleted.</summary>
    public static void Prune(TimeSpan keep, DateOnly today, string? folder = null)
    {
        try
        {
            var directory = new DirectoryInfo(folder ?? Folder);
            if (!directory.Exists)
            {
                return;
            }

            var oldest = today.AddDays(-(int)keep.TotalDays);
            foreach (var file in directory.EnumerateFiles("????-??-??.json"))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day < oldest)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("DayLog", exception.Message);
        }
    }

    private static void Trim(List<string> list)
    {
        if (list.Count > MaxItems)
        {
            list.RemoveRange(0, list.Count - MaxItems);
        }
    }

    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
