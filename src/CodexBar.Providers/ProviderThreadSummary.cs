namespace CodexBar.Providers;

public sealed record ProviderThreadSummary(
    string Id,
    string Title,
    string Preview,
    string WorkspacePath,
    DateTimeOffset UpdatedAt,
    string Status = "unknown",
    IReadOnlyList<string>? ActiveFlags = null,
    string? Source = null);

/// <summary>
/// One message. An agent turn may carry the work that led to its answer (tool calls and
/// in-between text), shown like Claude Code's activity list above the final answer.
/// </summary>
public sealed record ProviderThreadMessage(
    string Role,
    string Text,
    IReadOnlyList<ProviderActivityStep>? Steps = null,
    IReadOnlyList<string>? Images = null,
    string? Id = null,
    DateTimeOffset? Time = null);

/// <summary>
/// One step of an agent turn: "Edited" + "Foo.cs", "Ran a command" + the command as detail, or
/// (<paramref name="IsText"/>) a piece of text the agent wrote between tool calls. File edits
/// carry the full path as <paramref name="Detail"/> and their changed line counts.
/// </summary>
public sealed record ProviderActivityStep(
    string Verb,
    string Target,
    string Detail = "",
    bool Failed = false,
    bool IsText = false,
    int Added = 0,
    int Removed = 0,
    IReadOnlyList<ProviderTextEdit>? Edits = null,
    string? Diff = null)
{
    public bool IsFileEdit => !IsText && Verb is "Edited" or "Wrote";

    /// <summary>A progress note the agent addressed to the person (shown as text, never folded).</summary>
    public bool IsNote => IsText && Verb == "note";
}
