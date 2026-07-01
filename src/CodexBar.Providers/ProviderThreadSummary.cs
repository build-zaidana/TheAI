namespace CodexBar.Providers;

public sealed record ProviderThreadSummary(
    string Id,
    string Title,
    string Preview,
    string WorkspacePath,
    DateTimeOffset UpdatedAt,
    string Status = "unknown",
    IReadOnlyList<string>? ActiveFlags = null);

public sealed record ProviderThreadMessage(string Role, string Text);
