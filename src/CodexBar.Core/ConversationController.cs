using CodexBar.Providers;

namespace CodexBar.Core;

public sealed class ConversationController(IAgentProvider provider)
{
    private readonly IAgentProvider _provider = provider;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private bool _eventSubscribed;
    private Task? _recoveryTask;
    private string _sandboxMode = "workspace-write";
    private string _approvalPolicy = "on-request";

    public string? ThreadId { get; private set; }
    public string? WorkspacePath { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>Capabilities of the active provider; the UI adapts to these (threads, workspace, api-key…).</summary>
    public ProviderCapabilities Capabilities => _provider.Capabilities;

    /// <summary>Stable id of the active provider (e.g. "codex", "anthropic").</summary>
    public string ProviderId => _provider.Id;

    public event EventHandler<ProviderEvent>? EventReceived;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_eventSubscribed)
        {
            _provider.EventReceived += ForwardEvent;
            _eventSubscribed = true;
        }

        await _provider.InitializeAsync(cancellationToken);
    }

    public async Task OpenWorkspaceAsync(
        string workspacePath,
        string? previousThreadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException(
                "Selesaikan atau batalkan pekerjaan aktif sebelum berpindah workspace atau thread.");
        }

        if (!Directory.Exists(workspacePath))
        {
            throw new DirectoryNotFoundException($"Workspace tidak ditemukan: {workspacePath}");
        }

        await EnsureConnectedAsync(cancellationToken);
        var normalizedPath = Path.GetFullPath(workspacePath);
        var startedThreadId = await _provider.StartOrResumeThreadAsync(
            normalizedPath,
            previousThreadId,
            sandboxMode,
            approvalPolicy,
            cancellationToken);
        WorkspacePath = normalizedPath;
        ThreadId = startedThreadId;
        _sandboxMode = sandboxMode;
        _approvalPolicy = approvalPolicy;
    }

    public async Task<ProviderAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _provider.GetAccountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _provider.ListThreadsAsync(limit, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _provider.SearchThreadsAsync(searchTerm, limit, cancellationToken);
    }

    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Tidak bisa memadatkan konteks saat Codex sedang bekerja.");
        }

        if (ThreadId is null)
        {
            return;
        }

        await EnsureConnectedAsync(cancellationToken);
        await _provider.CompactAsync(ThreadId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _provider.ReadThreadAsync(threadId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _provider.ListModelsAsync(cancellationToken);
    }

    public async Task SendAsync(
        string prompt,
        IReadOnlyList<ProviderInputAttachment>? attachments = null,
        string? model = null,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) && attachments is not { Count: > 0 })
        {
            return;
        }

        if (ThreadId is null)
        {
            throw new InvalidOperationException("Workspace/thread belum diinisialisasi.");
        }

        IsBusy = true;
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            await _provider.SendPromptAsync(ThreadId, prompt, attachments, model, reasoningEffort, cancellationToken);
        }
        catch
        {
            IsBusy = false;
            throw;
        }
    }

    public async Task ChangePermissionsAsync(
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Permission tidak dapat diubah saat Codex sedang bekerja.");
        }

        if (WorkspacePath is null || ThreadId is null)
        {
            return;
        }

        await EnsureConnectedAsync(cancellationToken);
        ThreadId = await _provider.StartOrResumeThreadAsync(
            WorkspacePath,
            ThreadId,
            sandboxMode,
            approvalPolicy,
            cancellationToken);
        _sandboxMode = sandboxMode;
        _approvalPolicy = approvalPolicy;
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        _provider.CancelAsync(cancellationToken);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_provider.IsInitialized)
        {
            return;
        }

        await _reconnectGate.WaitAsync(cancellationToken);
        try
        {
            if (_provider.IsInitialized)
            {
                return;
            }

            EventReceived?.Invoke(this, new ProviderStatusEvent(
                Localization.Instance["controller.status.reconnecting"]));
            await _provider.InitializeAsync(cancellationToken);
            if (WorkspacePath is not null && ThreadId is not null)
            {
                ThreadId = await _provider.StartOrResumeThreadAsync(
                    WorkspacePath,
                    ThreadId,
                    _sandboxMode,
                    _approvalPolicy,
                    cancellationToken);
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private void ForwardEvent(object? sender, ProviderEvent providerEvent)
    {
        if (providerEvent is ProviderErrorEvent { WillRetry: false } error && !_provider.IsInitialized)
        {
            var interruptedTurn = IsBusy;
            IsBusy = false;
            EventReceived?.Invoke(this, new ProviderErrorEvent(
                string.Format(Localization.Instance["controller.status.retrySuffix"], error.Message),
                WillRetry: true));
            if (_recoveryTask is null || _recoveryTask.IsCompleted)
            {
                _recoveryTask = RecoverAfterDisconnectAsync(interruptedTurn);
            }
            return;
        }

        if (providerEvent is ProviderCompletedEvent or ProviderErrorEvent { WillRetry: false })
        {
            IsBusy = false;
        }

        EventReceived?.Invoke(this, providerEvent);
    }

    private async Task RecoverAfterDisconnectAsync(bool interruptedTurn)
    {
        try
        {
            await EnsureConnectedAsync(CancellationToken.None);
            EventReceived?.Invoke(this, new ProviderStatusEvent(
                interruptedTurn
                    ? Localization.Instance["controller.status.interrupted"]
                    : Localization.Instance["controller.status.reconnected"]));
            if (interruptedTurn)
            {
                EventReceived?.Invoke(this, new ProviderCompletedEvent("interrupted"));
            }
        }
        catch (Exception exception)
        {
            EventReceived?.Invoke(this, new ProviderErrorEvent(
                string.Format(Localization.Instance["controller.error.reconnect"], exception.Message)));
        }
    }
}
