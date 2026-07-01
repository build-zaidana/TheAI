namespace CodexBar.Providers;

public interface IAgentProvider : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    bool IsInitialized { get; }

    event EventHandler<ProviderEvent>? EventReceived;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string> StartOrResumeThreadAsync(
        string workspacePath,
        string? threadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default);
    Task<ProviderAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default);
    Task SendPromptAsync(
        string threadId,
        string prompt,
        IReadOnlyList<ProviderInputAttachment>? attachments = null,
        string? model = null,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default);
    Task CompactAsync(string threadId, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}
