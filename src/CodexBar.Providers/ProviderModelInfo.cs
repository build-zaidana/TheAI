namespace CodexBar.Providers;

public sealed record ProviderModelInfo(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    string DefaultReasoningEffort)
{
    public override string ToString() => DisplayName;
}
