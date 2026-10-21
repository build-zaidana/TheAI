using System.Text.Json;

namespace CodexBar.Core;

/// <summary>
/// The pull request of the branch being worked on and its checks, read from
/// <c>gh pr view --json number,title,url,headRefOid,statusCheckRollup</c>.
/// </summary>
public sealed record PullRequestStatus(
    int Number,
    string Title,
    string Url,
    string HeadSha,
    int Total,
    int Finished,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Running)
{
    public const string JsonFields = "number,title,url,headRefOid,statusCheckRollup";

    public bool HasChecks => Total > 0;
    public bool Pending => Finished < Total;
    public bool Passed => HasChecks && !Pending && Failed.Count == 0;

    public static PullRequestStatus? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var failed = new List<string>();
            var running = new List<string>();
            var total = 0;
            var finished = 0;
            if (root.TryGetProperty("statusCheckRollup", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                // A check re-run shows up more than once: the latest entry of each name counts.
                var latest = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var check in checks.EnumerateArray())
                {
                    var name = Text(check, "name") ?? Text(check, "context") ?? "check";
                    latest[name] = check;
                }

                foreach (var (name, check) in latest)
                {
                    total++;
                    switch (Outcome(check))
                    {
                        case CheckOutcome.Passed:
                            finished++;
                            break;
                        case CheckOutcome.Failed:
                            finished++;
                            failed.Add(name);
                            break;
                        default:
                            running.Add(name);
                            break;
                    }
                }
            }

            return new PullRequestStatus(
                number.GetInt32(),
                Text(root, "title") ?? string.Empty,
                Text(root, "url") ?? string.Empty,
                Text(root, "headRefOid") ?? string.Empty,
                total,
                finished,
                failed,
                running);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum CheckOutcome { Running, Passed, Failed }

    private static CheckOutcome Outcome(JsonElement check)
    {
        // A commit status (StatusContext) has a state; a check run has a status and, once completed, a conclusion.
        if (Text(check, "state") is { } state)
        {
            return state switch
            {
                "SUCCESS" => CheckOutcome.Passed,
                "FAILURE" or "ERROR" => CheckOutcome.Failed,
                _ => CheckOutcome.Running   // PENDING, EXPECTED
            };
        }

        if (Text(check, "status") is { } status && status != "COMPLETED")
        {
            return CheckOutcome.Running;
        }

        return Text(check, "conclusion") switch
        {
            "SUCCESS" or "NEUTRAL" or "SKIPPED" => CheckOutcome.Passed,
            null or "" => CheckOutcome.Running,
            _ => CheckOutcome.Failed   // FAILURE, TIMED_OUT, CANCELLED, ACTION_REQUIRED, STARTUP_FAILURE…
        };
    }

    /// <summary>
    /// The live activity for this state of the PR, or null when there is nothing to show. A run is
    /// reported while it runs and once when it ends (<paramref name="lastReported"/> is the key of
    /// what was already shown, so an ended run isn't shown again on every check).
    /// </summary>
    public LiveJobUpdate? ToJob(string folder, string lastReported, out string reported)
    {
        reported = lastReported;
        if (!HasChecks)
        {
            return null;
        }

        var title = $"CI #{Number}";
        var url = Url.Length > 0 ? Url.TrimEnd('/') + "/checks" : null;
        if (Pending)
        {
            reported = $"{HeadSha}/running/{Finished}";
            if (reported == lastReported)
            {
                return null;
            }

            var detail = Failed.Count > 0
                ? $"{string.Join(", ", Failed)} ✕ · {Running.Count} running"
                : Running.FirstOrDefault() ?? string.Empty;
            return new LiveJobUpdate("gh-pr", title, (double)Finished / Total, detail, url, Folder: folder);
        }

        reported = $"{HeadSha}/{(Passed ? "passed" : "failed")}";
        // Already reported, or it ended before CodexBar started looking (no news at every launch).
        if (reported == lastReported || lastReported.Length == 0)
        {
            return null;
        }

        return Passed
            ? new LiveJobUpdate("gh-pr", title, 1, Title, url, Done: true, Folder: folder)
            : new LiveJobUpdate("gh-pr", title, Detail: Title, Url: url, Failed: string.Join(", ", Failed), Folder: folder);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
