namespace CodexBar.Providers;

public sealed record ProviderCapabilities(
    bool SupportsThreads,
    bool SupportsApprovals,
    bool SupportsStreaming,
    bool SupportsWorkspace,
    bool SupportsApiKey,
    bool SupportsModelPicker);
