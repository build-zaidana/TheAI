using System.Globalization;
using System.Text.Json;

namespace CodexBar.Core;

public enum LiveJobState { Running, Done, Failed }

/// <summary>Something a script, build or download reports to the notch ("live activity").</summary>
public sealed class LiveJob
{
    public required string Id { get; init; }
    public string Title { get; set; } = string.Empty;
    /// <summary>0–1, or null for "running, no idea how far" (a spinner and the time instead).</summary>
    public double? Progress { get; set; }
    public string Detail { get; set; } = string.Empty;
    /// <summary>Opened when its row is clicked; only http(s) is ever accepted.</summary>
    public Uri? Url { get; set; }
    public LiveJobState State { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Updated { get; set; }
    public DateTimeOffset? Finished { get; set; }
    /// <summary>Hidden until then: a terminal command shows only once it has run a while.</summary>
    public DateTimeOffset VisibleFrom { get; set; }
    /// <summary>The folder it runs in, when known (terminal commands): what "ask the agent" tells it.</summary>
    public string Folder { get; set; } = string.Empty;

    public TimeSpan Elapsed(DateTimeOffset now) => (Finished ?? now) - Started;

    public bool IsVisible(DateTimeOffset now) => now >= VisibleFrom;
}

/// <summary>
/// One update for a job: what <c>TheAI.exe activity &lt;id&gt; …</c> sends down the pipe as a JSON line,
/// e.g. <c>{"id":"build","title":"npm run build","progress":0.4}</c>, <c>{"id":"build","done":true}</c>,
/// <c>{"id":"deploy","failed":"exit 1"}</c> or <c>{"id":"build","remove":true}</c>.
/// </summary>
public sealed record LiveJobUpdate(
    string Id,
    string? Title = null,
    double? Progress = null,
    string? Detail = null,
    string? Url = null,
    bool Done = false,
    string? Failed = null,
    bool Remove = false,
    double? After = null,
    string? Folder = null)
{
    public const int MaxText = 120;

    /// <summary>One JSON line → an update; null when it isn't one (bad JSON, no id).</summary>
    public static LiveJobUpdate? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "id") is not { Length: > 0 } id)
            {
                return null;
            }

            double? progress = root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            var failed = root.TryGetProperty("failed", out var f)
                ? f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty
                  : f.ValueKind == JsonValueKind.True ? string.Empty : null
                : null;
            return Clean(new LiveJobUpdate(
                id,
                Text(root, "title"),
                progress,
                Text(root, "detail"),
                Text(root, "url"),
                root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True,
                failed,
                root.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True,
                root.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
                Text(root, "folder")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Command-line form: <c>&lt;id&gt; [--title t] [--progress 0.4|40%] [--detail d] [--url u] [--done] [--failed [why]] [--remove]</c>.
    /// Returns null with an error message when the arguments make no sense.
    /// </summary>
    public static (LiveJobUpdate? Update, string? Error) FromArgs(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            return (null, "Missing the activity id: TheAI.exe activity <id> [--title …] [--progress 0.4] [--done | --failed reason]");
        }

        var update = new LiveJobUpdate(args[0]);
        for (var i = 1; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            switch (args[i])
            {
                case "--title" or "-t":
                    update = update with { Title = Next() };
                    break;
                case "--progress" or "-p":
                    if (ParseProgress(Next()) is not { } progress)
                    {
                        return (null, "--progress takes a fraction (0.4) or a percentage (40%).");
                    }

                    update = update with { Progress = progress };
                    break;
                case "--detail" or "-d":
                    update = update with { Detail = Next() };
                    break;
                case "--url":
                    update = update with { Url = Next() };
                    break;
                case "--done":
                    update = update with { Done = true };
                    break;
                case "--failed":
                    update = update with { Failed = Next() ?? string.Empty };
                    break;
                case "--remove":
                    update = update with { Remove = true };
                    break;
                case "--after":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
                    {
                        return (null, "--after takes seconds (10).");
                    }

                    update = update with { After = after };
                    break;
                case "--folder":
                    update = update with { Folder = Next() };
                    break;
                default:
                    return (null, $"Unknown option {args[i]}.");
            }
        }

        return (Clean(update), null);
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["title"] = Title,
        ["progress"] = Progress,
        ["detail"] = Detail,
        ["url"] = Url,
        ["done"] = Done ? true : null,
        ["failed"] = Failed,
        ["remove"] = Remove ? true : null,
        ["after"] = After,
        ["folder"] = Folder
    }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value));

    private static double? ParseProgress(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var percent = text.EndsWith('%');
        if (!double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // "40" is surely 40 %, not 4000 %.
        return percent || value > 1 ? value / 100 : value;
    }

    private static LiveJobUpdate Clean(LiveJobUpdate update) => update with
    {
        Id = Cut(update.Id.Trim(), 64)!,
        Title = Cut(update.Title?.Trim(), MaxText),
        Detail = Cut(update.Detail?.Trim(), MaxText),
        Failed = Cut(update.Failed?.Trim(), MaxText),
        Progress = update.Progress is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null,
        After = update.After is { } a && double.IsFinite(a) ? Math.Clamp(a, 0, 3600) : null,
        Folder = Cut(update.Folder?.Trim(), 260)
    };

    private static string? Cut(string? text, int max) =>
        text is null ? null : (text.Length <= max ? text : text[..max] + "…").ReplaceLineEndings(" ");

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// The jobs the notch shows. At most <see cref="MaxJobs"/> (the oldest goes); a finished one stays
/// <see cref="DoneShown"/> on the compact notch and is then dropped; a failed one stays until it is
/// dismissed or <see cref="Stale"/> passes; a running one that stops reporting goes after <see cref="Stale"/>.
/// </summary>
public sealed class LiveJobBoard
{
    public const int MaxJobs = 4;
    public static readonly TimeSpan DoneShown = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private readonly List<LiveJob> _jobs = [];

    public IReadOnlyList<LiveJob> Jobs => _jobs;

    /// <summary>Apply one update; returns the job it touched (null when it was removed or ignored).</summary>
    public LiveJob? Apply(LiveJobUpdate update, DateTimeOffset now)
    {
        var job = _jobs.FirstOrDefault(item => item.Id == update.Id);
        if (update.Remove)
        {
            if (job is not null)
            {
                _jobs.Remove(job);
            }

            return null;
        }

        if (job is null)
        {
            job = new LiveJob { Id = update.Id, Title = update.Id, Started = now, VisibleFrom = now.AddSeconds(update.After ?? 0) };
            _jobs.Add(job);
            while (_jobs.Count > MaxJobs)
            {
                _jobs.RemoveAt(0);
            }
        }
        else if (job.State != LiveJobState.Running && !update.Done && update.Failed is null)
        {
            // Reused id after it finished: a new run.
            job.State = LiveJobState.Running;
            job.Started = now;
            job.VisibleFrom = now.AddSeconds(update.After ?? 0);
            job.Finished = null;
            job.Progress = null;
            job.Detail = string.Empty;
        }

        if (update.Title is { Length: > 0 } title)
        {
            job.Title = title;
        }

        if (update.Detail is not null)
        {
            job.Detail = update.Detail;
        }

        if (update.Folder is { Length: > 0 } folder)
        {
            job.Folder = folder;
        }

        if (update.Progress is { } progress)
        {
            job.Progress = progress;
        }

        if (update.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            job.Url = uri;
        }

        if ((update.Done || update.Failed is not null) && job.VisibleFrom > now)
        {
            job.VisibleFrom = now;   // the sender judged it long enough to report how it ended
        }

        if (update.Failed is { } failed)
        {
            job.State = LiveJobState.Failed;
            job.Finished = now;
            if (failed.Length > 0)
            {
                job.Detail = failed;
            }
        }
        else if (update.Done)
        {
            job.State = LiveJobState.Done;
            job.Finished = now;
            job.Progress = 1;
        }

        job.Updated = now;
        return job;
    }

    public bool Dismiss(string id) => _jobs.RemoveAll(job => job.Id == id) > 0;

    /// <summary>Drop what has run its course; returns whether anything changed.</summary>
    public bool Prune(DateTimeOffset now) => _jobs.RemoveAll(job => job.State switch
    {
        LiveJobState.Done => now - job.Finished!.Value > DoneShown,
        _ => now - job.Updated > Stale
    }) > 0;

    /// <summary>The jobs to show now (a terminal command waits until it has run a while).</summary>
    public IReadOnlyList<LiveJob> Visible(DateTimeOffset now) => _jobs.Where(job => job.IsVisible(now)).ToList();

    /// <summary>What the compact notch shows: a failure first, then a fresh finish, then the latest running job.</summary>
    public LiveJob? Headline(DateTimeOffset now)
    {
        var visible = Visible(now);
        return visible.Where(job => job.State == LiveJobState.Failed).MaxBy(job => job.Updated)
            ?? visible.Where(job => job.State == LiveJobState.Done && now - job.Finished!.Value <= DoneShown).MaxBy(job => job.Finished)
            ?? visible.Where(job => job.State == LiveJobState.Running).MaxBy(job => job.Updated);
    }
}
