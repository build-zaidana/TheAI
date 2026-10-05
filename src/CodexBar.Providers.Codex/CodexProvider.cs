using System.Collections.Concurrent;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.Codex;

public sealed class CodexProvider : IAgentProvider
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _serverRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderQuotaWindow> _quotaWindows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly Func<string, string>? _localize;
    private readonly Func<ICodexTransport> _transportFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private CancellationTokenSource? _lifetime;
    private ICodexTransport? _transport;
    private Task? _readLoop;
    private Task? _errorLoop;
    private long _nextRequestId;
    private string? _currentThreadId;
    private string? _currentTurnId;

    public CodexProvider(Func<string, string>? localize = null)
        : this(
            () => new ProcessCodexTransport(CodexExecutableLocator.Locate()),
            DefaultRequestTimeout,
            localize)
    {
    }

    internal CodexProvider(
        Func<ICodexTransport> transportFactory,
        TimeSpan requestTimeout,
        Func<string, string>? localize = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _localize = localize;
    }

    public string Id => "codex";
    public string DisplayName => "Codex";
    public ProviderCapabilities Capabilities { get; } = new(
        SupportsThreads: true,
        SupportsApprovals: true,
        SupportsStreaming: true,
        SupportsWorkspace: true,
        SupportsApiKey: false,
        SupportsModelPicker: true);
    public bool IsInitialized { get; private set; }

    public event EventHandler<ProviderEvent>? EventReceived;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized && _transport is { IsRunning: true })
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);
            _lifetime = new CancellationTokenSource();
            _transport = _transportFactory();

            try
            {
                await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
                _readLoop = ReadLoopAsync(_transport, _lifetime.Token);
                _errorLoop = ReadErrorLoopAsync(_transport, _lifetime.Token);

                await SendRequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new
                        {
                            name = "codexbar",
                            title = "CodexBar",
                            version = typeof(CodexProvider).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
                IsInitialized = true;
                Emit(new ProviderStatusEvent(L("provider.status.connected", "Codex terhubung")));
            }
            catch
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<string> StartOrResumeThreadAsync(
        string workspacePath,
        string? threadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        sandboxMode = NormalizeSandboxMode(sandboxMode);
        approvalPolicy = NormalizeApprovalPolicy(approvalPolicy);
        JsonElement result;

        if (!string.IsNullOrWhiteSpace(threadId))
        {
            try
            {
                result = await SendRequestAsync(
                    "thread/resume",
                    new
                    {
                        threadId,
                        cwd = Path.GetFullPath(workspacePath),
                        sandbox = sandboxMode,
                        approvalPolicy
                    },
                    cancellationToken);
            }
            catch (CodexRpcException exception) when (
                !cancellationToken.IsCancellationRequested && IsMissingThread(exception.Message))
            {
                Emit(new ProviderStatusEvent(L("provider.status.threadReplaced",
                    "Thread sebelumnya tidak tersedia; membuat thread baru.")));
                result = await StartThreadAsync(workspacePath, sandboxMode, approvalPolicy, cancellationToken);
            }
        }
        else
        {
            result = await StartThreadAsync(workspacePath, sandboxMode, approvalPolicy, cancellationToken);
        }

        _currentThreadId = result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException(L("provider.error.noThreadId", "Codex tidak mengembalikan thread ID."));

        Emit(new ProviderStatusEvent(L("provider.status.threadReady", "Thread siap")));
        return _currentThreadId;
    }

    public async Task<ProviderAccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await SendRequestAsync(
            "account/read",
            new { refreshToken = true },
            cancellationToken);

        var requiresOpenAiAuth = result.TryGetProperty("requiresOpenaiAuth", out var requiresAuth) &&
            requiresAuth.ValueKind == JsonValueKind.True;

        if (!result.TryGetProperty("account", out var account) ||
            account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new ProviderAccountInfo(
                IsAuthenticated: false,
                AuthenticationMethod: "none",
                Email: null,
                PlanType: null,
                RequiresOpenAiAuth: requiresOpenAiAuth);
        }

        var method = GetString(account, "type") ?? "unknown";
        _ = RefreshRateLimitsAsync();
        return new ProviderAccountInfo(
            IsAuthenticated: true,
            AuthenticationMethod: method,
            Email: GetString(account, "email"),
            PlanType: GetString(account, "planType"),
            RequiresOpenAiAuth: requiresOpenAiAuth);
    }

    public async Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var result = await SendRequestAsync(
            "thread/list",
            new
            {
                limit = Math.Clamp(limit, 1, 100),
                sortKey = "recency_at",
                sortDirection = "desc",
                archived = false,
                sourceKinds = new[] { "cli", "vscode", "appServer" }
            },
            cancellationToken);

        return ParseThreadSummaries(result);
    }

    public async Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return [];
        }

        var result = await SendRequestAsync(
            "thread/search",
            new
            {
                searchTerm = searchTerm.Trim(),
                limit = Math.Clamp(limit, 1, 100),
                sortKey = "recency_at",
                sortDirection = "desc",
                archived = false,
                sourceKinds = new[] { "cli", "vscode", "appServer" }
            },
            cancellationToken);

        return ParseThreadSummaries(result);
    }

    internal static IReadOnlyList<ProviderThreadSummary> ParseThreadSummaries(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var threads = new List<ProviderThreadSummary>();
        foreach (var thread in data.EnumerateArray())
        {
            var id = GetString(thread, "id");
            var cwd = GetString(thread, "cwd");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd))
            {
                continue;
            }

            var preview = GetString(thread, "preview")?.Trim() ?? string.Empty;
            var name = GetString(thread, "name")?.Trim();
            var title = !string.IsNullOrWhiteSpace(name)
                ? name
                : FirstDisplayLine(preview);
            var updatedAt = thread.TryGetProperty("updatedAt", out var updated) && updated.TryGetInt64(out var unixTime)
                ? DateTimeOffset.FromUnixTimeSeconds(unixTime)
                : DateTimeOffset.MinValue;
            var (status, activeFlags) = ParseThreadStatus(thread);

            threads.Add(new ProviderThreadSummary(
                id,
                string.IsNullOrWhiteSpace(title) ? "Percakapan tanpa judul" : title,
                preview,
                cwd,
                updatedAt,
                status,
                activeFlags));
        }

        return threads;
    }

    public async Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        JsonElement result;
        try
        {
            result = await SendRequestAsync(
                "thread/read",
                new { threadId, includeTurns = true },
                cancellationToken);
        }
        catch (CodexRpcException exception) when (IsThreadNotMaterialized(exception.Message))
        {
            // Thread baru yang belum punya pesan: anggap percakapan kosong, bukan error.
            return [];
        }

        if (!result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<ProviderThreadMessage>();
        var parts = new List<(ProviderActivityStep Step, string? ToolId)>();

        // An agent turn as Claude Code shows it: its commands and file changes as steps, the
        // message after the last one as the answer.
        void FlushAgent()
        {
            if (parts.Count == 0)
            {
                return;
            }

            var (steps, answer) = ProviderActivity.SplitTurn(parts, new HashSet<string>());
            messages.Add(new ProviderThreadMessage("assistant", answer, steps.Count > 0 ? steps : null));
            parts.Clear();
        }

        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (GetString(item, "type") == "userMessage")
                {
                    var userText = ReadUserMessage(item);
                    if (!string.IsNullOrWhiteSpace(userText))
                    {
                        FlushAgent();
                        messages.Add(new ProviderThreadMessage("user", userText));
                    }
                }
                else
                {
                    parts.AddRange(ItemParts(item));
                }
            }

            FlushAgent();
        }

        return messages;
    }

    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var models = new List<ProviderModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;

        do
        {
            var result = await SendRequestAsync(
                "model/list",
                new { cursor, includeHidden = false, limit = 100 },
                cancellationToken);

            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var model = GetString(item, "model");
                    if (string.IsNullOrWhiteSpace(model) || !seen.Add(model))
                    {
                        continue;
                    }

                    models.Add(new ProviderModelInfo(
                        Id: GetString(item, "id") ?? model,
                        Model: model,
                        DisplayName: GetString(item, "displayName") ?? model,
                        Description: GetString(item, "description") ?? string.Empty,
                        IsDefault: item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True,
                        DefaultReasoningEffort: GetString(item, "defaultReasoningEffort") ?? string.Empty));
                }
            }

            cursor = GetString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return models;
    }

    public async Task SendPromptAsync(
        string threadId,
        string prompt,
        IReadOnlyList<ProviderInputAttachment>? attachments = null,
        string? model = null,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var input = new List<object>();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            input.Add(new { type = "text", text = prompt });
        }

        foreach (var attachment in attachments ?? [])
        {
            var fullPath = Path.GetFullPath(attachment.Path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    string.Format(L("provider.error.attachmentNotFound", "Lampiran tidak ditemukan: {0}"), attachment.DisplayName),
                    fullPath);
            }

            input.Add(attachment.Kind == ProviderAttachmentKind.LocalImage
                ? new { type = "localImage", path = fullPath }
                : new { type = "mention", name = attachment.DisplayName, path = fullPath });
        }

        if (input.Count == 0)
        {
            return;
        }

        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim().ToLowerInvariant();
        var result = await SendRequestAsync(
            "turn/start",
            new
            {
                threadId,
                input,
                model,
                effort
            },
            cancellationToken);

        _currentThreadId = threadId;
        _currentTurnId = result.GetProperty("turn").GetProperty("id").GetString();
        Emit(new ProviderStatusEvent(L("provider.status.working", "Codex sedang bekerja…")));
    }

    public async Task CompactAsync(string threadId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await SendRequestAsync("thread/compact/start", new { threadId }, cancellationToken);
        Emit(new ProviderStatusEvent(L("provider.status.compacting", "Memadatkan konteks percakapan…")));
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_currentThreadId is null || _currentTurnId is null)
        {
            return;
        }

        await SendRequestAsync(
            "turn/interrupt",
            new { threadId = _currentThreadId, turnId = _currentTurnId },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _initializeLock.Dispose();
    }

    private Task<JsonElement> StartThreadAsync(
        string workspacePath,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            "thread/start",
            new
            {
                cwd = Path.GetFullPath(workspacePath),
                sandbox = NormalizeSandboxMode(sandboxMode),
                approvalPolicy = NormalizeApprovalPolicy(approvalPolicy),
                personality = "pragmatic",
                ephemeral = false
            },
            cancellationToken);

    internal static string NormalizeApprovalPolicy(string approvalPolicy) =>
        approvalPolicy?.ToLowerInvariant() switch
        {
            "untrusted" => "untrusted",
            "on-failure" => "on-failure",
            "never" => "never",
            _ => "on-request"
        };

    internal static string NormalizeSandboxMode(string sandboxMode) =>
        sandboxMode?.ToLowerInvariant() switch
        {
            "read-only" => "read-only",
            "danger-full-access" => "danger-full-access",
            _ => "workspace-write"
        };

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var lifetimeToken = _lifetime?.Token ?? CancellationToken.None;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        timeout.CancelAfter(_requestTimeout);

        using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(timeout.Token);
            }
        });

        try
        {
            await SendAsync(new { method, id, @params = parameters }, timeout.Token)
                .ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(
                L("provider.error.timeout", "Codex tidak merespons permintaan '{0}' dalam {1} detik."),
                method, _requestTimeout.TotalSeconds.ToString("0.###")));
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        SendAsync(new { method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_transport is not { IsRunning: true } transport)
        {
            throw new InvalidOperationException(L("provider.error.notRunning", "Codex App Server belum berjalan."));
        }

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await transport.WriteLineAsync(json, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(ICodexTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await transport.ReadOutputLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (root.TryGetProperty("id", out var idElement) &&
                        (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
                    {
                        HandleResponse(root, idElement);
                        continue;
                    }

                    if (!root.TryGetProperty("method", out var methodElement))
                    {
                        continue;
                    }

                    var method = methodElement.GetString() ?? string.Empty;
                    var parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : JsonSerializer.SerializeToElement(new { });

                    if (root.TryGetProperty("id", out var requestId))
                    {
                        HandleServerRequest(requestId.Clone(), method, parameters);
                    }
                    else
                    {
                        HandleNotification(method, parameters);
                    }
                }
                catch (JsonException exception)
                {
                    Emit(new ProviderErrorEvent(LF("provider.error.invalidEvent",
                        "Event Codex tidak valid: {0}", exception.Message)));
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                MarkDisconnected(new IOException("Koneksi Codex App Server terputus."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                MarkDisconnected(new IOException(L("provider.error.readFailed", "Gagal membaca respons Codex App Server."), exception));
            }
        }
    }

    private async Task ReadErrorLoopAsync(ICodexTransport transport, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await transport.ReadErrorLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                Emit(new ProviderStatusEvent(line));
            }
        }
    }

    private void HandleResponse(JsonElement root, JsonElement idElement)
    {
        if (!idElement.TryGetInt64(out var id) || !_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = GetString(error, "message") ?? error.ToString();
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                ? value
                : 0;
            completion.TrySetException(new CodexRpcException(code, message));
            return;
        }

        completion.TrySetResult(root.GetProperty("result").Clone());
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "item/agentMessage/delta":
                if (GetString(parameters, "delta") is { Length: > 0 } delta)
                {
                    Emit(new ProviderTextDeltaEvent(delta));
                }
                break;

            case "item/reasoning/textDelta":
            case "item/reasoning/summaryTextDelta":
                if (GetString(parameters, "delta") is { Length: > 0 } reasoningDelta)
                {
                    Emit(new ProviderReasoningDeltaEvent(reasoningDelta));
                }
                break;

            case "turn/started":
                _turnParts.Clear();
                _currentTurnId = GetNestedString(parameters, "turn", "id") ?? _currentTurnId;
                Emit(new ProviderStatusEvent(L("provider.status.working", "Codex sedang bekerja…")));
                break;

            case "turn/completed":
                var status = GetNestedString(parameters, "turn", "status") ?? "completed";
                _currentTurnId = null;
                _serverRequests.Clear();
                Emit(new ProviderCompletedEvent(status));
                _ = RefreshRateLimitsAsync();
                break;

            case "account/rateLimits/updated":
                if (parameters.TryGetProperty("rateLimits", out var updatedLimits))
                {
                    EmitQuota(updatedLimits);
                }
                break;

            case "thread/tokenUsage/updated":
                EmitTokenUsage(parameters);
                break;

            case "thread/status/changed":
                var threadId = GetString(parameters, "threadId");
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    var (threadStatus, activeFlags) = ParseThreadStatus(parameters);
                    Emit(new ProviderThreadStatusEvent(threadId, threadStatus, activeFlags));
                }
                break;

            case "thread/compacted":
                Emit(new ProviderStatusEvent(L("provider.status.compacted", "Konteks percakapan dipadatkan.")));
                break;

            case "serverRequest/resolved":
                if (parameters.TryGetProperty("requestId", out var resolvedRequestId))
                {
                    _serverRequests.TryRemove(RequestKey(resolvedRequestId), out _);
                }
                break;

            case "error":
                var errorMessage = parameters.TryGetProperty("error", out var error)
                    ? GetString(error, "message") ?? error.ToString()
                    : parameters.ToString();
                var willRetry = parameters.TryGetProperty("willRetry", out var retry) && retry.GetBoolean();
                Emit(new ProviderErrorEvent(errorMessage, willRetry));
                break;

            case "item/started":
                Emit(new ProviderStatusEvent(L("provider.status.runningStep", "Menjalankan langkah…")));
                break;

            case "item/completed":
                if (parameters.TryGetProperty("item", out var completedItem))
                {
                    if (ParseFileChangeItem(completedItem) is { Count: > 0 } changes)
                    {
                        Emit(new ProviderFileChangeEvent(changes));
                    }

                    // Live work list: a finished command / change / tool call joins it.
                    var added = ItemParts(completedItem).ToArray();
                    if (added.Length > 0)
                    {
                        _turnParts.AddRange(added);
                        if (added.Any(part => !part.Step.IsText))
                        {
                            var (steps, answer) = ProviderActivity.SplitTurn(_turnParts, new HashSet<string>());
                            Emit(new ProviderActivityEvent(steps, answer));
                        }
                    }
                }
                break;
        }
    }

    // Subscription usage (ChatGPT plan limits). Optional: API-key accounts and older app-servers
    // simply don't answer, and the meter stays hidden.
    private async Task RefreshRateLimitsAsync()
    {
        try
        {
            var result = await SendRequestAsync("account/rateLimits/read", null!, CancellationToken.None).ConfigureAwait(false);
            if (result.TryGetProperty("rateLimits", out var limits))
            {
                EmitQuota(limits);
            }
        }
        catch (Exception)
        {
        }
    }

    // Updates are sparse: merge into what we already know, then report the whole picture.
    private void EmitQuota(JsonElement snapshot)
    {
        foreach (var window in ParseRateLimitSnapshot(snapshot))
        {
            _quotaWindows[window.Id] = window;
        }

        if (_quotaWindows.IsEmpty)
        {
            return;
        }

        var reached = snapshot.TryGetProperty("rateLimitReachedType", out var reachedType) &&
            reachedType.ValueKind == JsonValueKind.String;
        Emit(new ProviderQuotaEvent(_quotaWindows.Values.OrderBy(w => w.Id == "session" ? 0 : 1).ToArray(), reached));
    }

    /// <summary>The primary/secondary windows of a <c>RateLimitSnapshot</c>.</summary>
    internal static IReadOnlyList<ProviderQuotaWindow> ParseRateLimitSnapshot(JsonElement snapshot)
    {
        var windows = new List<ProviderQuotaWindow>();
        foreach (var (name, fallbackId) in new[] { ("primary", "session"), ("secondary", "weekly") })
        {
            if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            long? minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? duration.GetInt64()
                : null;
            var id = minutes switch
            {
                <= 6 * 60 => "session",
                >= 6 * 24 * 60 => "weekly",
                _ => fallbackId
            };
            DateTimeOffset? resetsAt = window.TryGetProperty("resetsAt", out var resets) && resets.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(resets.GetInt64())
                : null;
            windows.Add(new ProviderQuotaWindow(id, Math.Clamp(used.GetDouble() / 100.0, 0, 1), resetsAt));
        }

        return windows;
    }

    private readonly List<(ProviderActivityStep Step, string? ToolId)> _turnParts = [];

    /// <summary>
    /// A thread item as steps in Claude Code's activity view: commands, file changes (one per
    /// file, with line counts), MCP tool calls and web searches; agent messages as text.
    /// </summary>
    internal static IEnumerable<(ProviderActivityStep Step, string? ToolId)> ItemParts(JsonElement item)
    {
        var status = GetString(item, "status");
        var failed = status is "failed" or "declined";
        switch (GetString(item, "type"))
        {
            case "agentMessage" when GetString(item, "text") is { Length: > 0 } text:
                yield return (new ProviderActivityStep(string.Empty, text.Trim().Split('\n')[0], text.Trim(), IsText: true), null);
                break;

            case "commandExecution":
                var command = item.TryGetProperty("command", out var commandElement)
                    ? commandElement.ValueKind == JsonValueKind.Array
                        ? string.Join(' ', commandElement.EnumerateArray().Select(part => part.GetString()))
                        : commandElement.GetString() ?? string.Empty
                    : string.Empty;
                var exitFailed = item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() != 0;
                yield return (new ProviderActivityStep("Ran", "a command", command, Failed: failed || exitFailed), null);
                break;

            case "fileChange" when item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array:
                foreach (var change in changes.EnumerateArray())
                {
                    if (GetString(change, "path") is not { Length: > 0 } path)
                    {
                        continue;
                    }

                    var kind = change.TryGetProperty("kind", out var kindElement)
                        ? kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() : GetString(kindElement, "type")
                        : "update";
                    var (added, removed) = ProviderActivity.DiffCounts(GetString(change, "diff") ?? string.Empty);
                    yield return (new ProviderActivityStep(kind == "add" ? "Wrote" : "Edited", Path.GetFileName(path), path,
                        Failed: failed, Added: added, Removed: removed), null);
                }
                break;

            case "mcpToolCall":
                yield return (new ProviderActivityStep("Used", GetString(item, "tool") ?? "tool", GetString(item, "server") ?? string.Empty, Failed: failed), null);
                break;

            case "webSearch":
                yield return (new ProviderActivityStep("Searched the web", GetString(item, "query") ?? string.Empty), null);
                break;
        }
    }

    /// <summary>
    /// Files from a completed <c>fileChange</c> item (empty for other items, or for patches that
    /// failed or were declined).
    /// </summary>
    internal static IReadOnlyList<ProviderFileChange> ParseFileChangeItem(JsonElement item)
    {
        if (GetString(item, "type") != "fileChange" ||
            GetString(item, "status") is not ("completed" or null) ||
            !item.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ProviderFileChange>();
        foreach (var change in changes.EnumerateArray())
        {
            if (GetString(change, "path") is not { Length: > 0 } path)
            {
                continue;
            }

            var kind = "update";
            string? movePath = null;
            if (change.TryGetProperty("kind", out var kindElement))
            {
                kind = kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString() ?? "update"
                    : GetString(kindElement, "type") ?? "update";
                movePath = kindElement.ValueKind == JsonValueKind.Object ? GetString(kindElement, "move_path") : null;
            }

            result.Add(new ProviderFileChange(path, kind, GetString(change, "diff") ?? string.Empty, movePath));
        }

        return result;
    }

    private void HandleServerRequest(JsonElement requestId, string method, JsonElement parameters)
    {
        if (method is "mcpServer/elicitation/request")
        {
            HandleMcpElicitationRequest(requestId, parameters);
            return;
        }

        if (method is "item/tool/requestUserInput")
        {
            HandleUserInputRequest(requestId, parameters);
            return;
        }

        if (method is not ("item/commandExecution/requestApproval" or
            "item/fileChange/requestApproval" or
            "item/permissions/requestApproval"))
        {
            _ = SendRpcErrorSafeAsync(requestId, -32601, $"Request belum didukung CodexBar: {method}");
            return;
        }

        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;

        var reason = GetString(parameters, "reason");
        var command = GetString(parameters, "command");
        var cwd = GetString(parameters, "cwd");
        var summary = method switch
        {
            "item/commandExecution/requestApproval" => BuildCommandApprovalSummary(parameters, command, cwd, reason),
            "item/fileChange/requestApproval" => BuildFileApprovalSummary(parameters, reason),
            _ => BuildPermissionApprovalSummary(parameters, cwd, reason)
        };

        Emit(new ProviderApprovalEvent(
            method,
            summary,
            async decision =>
            {
                if (!_serverRequests.TryRemove(requestKey, out _))
                {
                    return;
                }

                if (method == "item/permissions/requestApproval")
                {
                    var approved = decision is ProviderApprovalDecision.AcceptOnce or
                        ProviderApprovalDecision.AcceptForSession;
                    var permissions = approved && parameters.TryGetProperty("permissions", out var requested)
                        ? requested.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                    await SendRpcResultAsync(requestId, new
                    {
                        permissions,
                        scope = decision == ProviderApprovalDecision.AcceptForSession ? "session" : "turn"
                    });
                    return;
                }

                await SendRpcResultAsync(requestId, new
                {
                    decision = decision switch
                    {
                        ProviderApprovalDecision.AcceptOnce => "accept",
                        ProviderApprovalDecision.AcceptForSession => "acceptForSession",
                        ProviderApprovalDecision.Cancel => "cancel",
                        _ => "decline"
                    }
                });
            },
            parameters,
            () => _serverRequests.ContainsKey(requestKey)));
    }

    private void HandleMcpElicitationRequest(JsonElement requestId, JsonElement parameters)
    {
        var mode = GetString(parameters, "mode");
        if (mode == "url")
        {
            HandleMcpUrlElicitation(requestId, parameters);
            return;
        }

        if (mode != "form" ||
            !parameters.TryGetProperty("requestedSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            _ = SendRpcResultSafeAsync(requestId, new { action = "decline" });
            Emit(new ProviderStatusEvent(L("provider.mcp.unsupported",
                "Format input MCP ini belum dapat ditampilkan dengan aman.")));
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        var questions = new List<ProviderInputQuestion>();
        var fieldSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            var fieldSchema = property.Value.Clone();
            fieldSchemas[property.Name] = fieldSchema;
            var title = GetString(fieldSchema, "title") ?? property.Name;
            var description = GetString(fieldSchema, "description") ?? LF(
                "provider.mcp.fieldDescription", "Masukkan nilai untuk {0}.", title);
            var type = GetString(fieldSchema, "type") ?? "string";
            var options = ReadMcpOptions(fieldSchema, type);
            questions.Add(new ProviderInputQuestion(
                property.Name,
                title,
                description,
                options,
                false,
                false,
                type == "array",
                required.Contains(property.Name),
                type));
        }

        if (questions.Count == 0)
        {
            _ = SendRpcResultSafeAsync(requestId, new { action = "decline" });
            Emit(new ProviderStatusEvent(L("provider.mcp.emptyForm",
                "Form MCP tidak memiliki field yang dapat ditampilkan.")));
            return;
        }

        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;
        var serverName = GetString(parameters, "serverName") ?? "MCP server";
        Emit(new ProviderUserInputEvent(
            LF("provider.mcp.inputTitle", "Input untuk {0}", serverName),
            GetString(parameters, "message") ?? L("provider.mcp.inputMessage",
                "Tool membutuhkan informasi tambahan."),
            questions,
            async answers =>
            {
                if (!_serverRequests.TryRemove(requestKey, out _))
                {
                    return;
                }

                if (answers is null)
                {
                    await SendRpcResultAsync(requestId, new { action = "cancel" });
                    return;
                }

                var content = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var answer in answers)
                {
                    if (fieldSchemas.TryGetValue(answer.Key, out var fieldSchema))
                    {
                        content[answer.Key] = ConvertMcpAnswer(fieldSchema, answer.Value);
                    }
                }

                await SendRpcResultAsync(requestId, new { action = "accept", content });
            },
            parameters,
            null,
            () => _serverRequests.ContainsKey(requestKey)));
    }

    private void HandleMcpUrlElicitation(JsonElement requestId, JsonElement parameters)
    {
        var url = GetString(parameters, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            _ = SendRpcResultSafeAsync(requestId, new { action = "decline" });
            Emit(new ProviderStatusEvent(L("provider.mcp.invalidUrl",
                "MCP mengirim tautan yang tidak valid atau tidak aman.")));
            return;
        }

        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;
        var serverName = GetString(parameters, "serverName") ?? "MCP server";
        Emit(new ProviderUserInputEvent(
            LF("provider.mcp.connectTitle", "Hubungkan {0}", serverName),
            GetString(parameters, "message") ?? L("provider.mcp.connectMessage",
                "Selesaikan langkah pada browser lalu kembali ke CodexBar."),
            [new ProviderInputQuestion(
                "completed",
                L("provider.mcp.confirmHeader", "Konfirmasi"),
                L("provider.mcp.confirmQuestion",
                    "Setelah langkah di browser selesai, pilih konfirmasi berikut."),
                [new ProviderInputOption(L("provider.mcp.confirmDone", "Saya sudah selesai"),
                    string.Empty, "completed")])],
            async answers =>
            {
                if (!_serverRequests.TryRemove(requestKey, out _))
                {
                    return;
                }

                await SendRpcResultAsync(requestId, new
                {
                    action = answers is null ? "cancel" : "accept"
                });
            },
            parameters,
            null,
            () => _serverRequests.ContainsKey(requestKey),
            uri.ToString()));
    }

    private IReadOnlyList<ProviderInputOption> ReadMcpOptions(JsonElement schema, string type)
    {
        if (type == "boolean")
        {
            return
            [
                new ProviderInputOption(L("provider.option.yes", "Ya"), string.Empty, "true"),
                new ProviderInputOption(L("provider.option.no", "Tidak"), string.Empty, "false")
            ];
        }

        var enumOwner = type == "array" && schema.TryGetProperty("items", out var items) ? items : schema;
        if (enumOwner.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            return values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => new ProviderInputOption(value.GetString()!, string.Empty))
                .ToArray();
        }

        var titledProperty = type == "array" ? "anyOf" : "oneOf";
        if (enumOwner.TryGetProperty(titledProperty, out var titled) && titled.ValueKind == JsonValueKind.Array)
        {
            return titled.EnumerateArray()
                .Select(value => new ProviderInputOption(
                    GetString(value, "title") ?? GetString(value, "const") ??
                        L("provider.option.choice", "Pilihan"),
                    string.Empty,
                    GetString(value, "const")))
                .ToArray();
        }

        return [];
    }

    private static object? ConvertMcpAnswer(JsonElement schema, IReadOnlyList<string> answers)
    {
        var type = GetString(schema, "type") ?? "string";
        if (type == "array")
        {
            return answers.ToArray();
        }

        var value = answers.FirstOrDefault() ?? string.Empty;
        return type switch
        {
            "boolean" when bool.TryParse(value, out var result) => result,
            "integer" when long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var result) => result,
            "number" when double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var result) => result,
            _ => value
        };
    }

    private void HandleUserInputRequest(JsonElement requestId, JsonElement parameters)
    {
        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;

        var questions = new List<ProviderInputQuestion>();
        if (parameters.TryGetProperty("questions", out var questionElements) &&
            questionElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in questionElements.EnumerateArray())
            {
                var id = GetString(question, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var options = new List<ProviderInputOption>();
                if (question.TryGetProperty("options", out var optionElements) &&
                    optionElements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionElements.EnumerateArray())
                    {
                        var label = GetString(option, "label");
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            options.Add(new ProviderInputOption(
                                label,
                                GetString(option, "description") ?? string.Empty));
                        }
                    }
                }

                questions.Add(new ProviderInputQuestion(
                    id,
                    GetString(question, "header") ?? L("provider.input.question", "Pertanyaan"),
                    GetString(question, "question") ?? string.Empty,
                    options,
                    question.TryGetProperty("isOther", out var isOther) && isOther.ValueKind == JsonValueKind.True,
                    question.TryGetProperty("isSecret", out var isSecret) && isSecret.ValueKind == JsonValueKind.True));
            }
        }

        if (questions.Count == 0)
        {
            _serverRequests.TryRemove(requestKey, out _);
            _ = SendRpcResultSafeAsync(requestId, new { answers = new { } });
            Emit(new ProviderStatusEvent(L("provider.input.empty",
                "Codex meminta input tanpa pertanyaan yang dapat ditampilkan.")));
            return;
        }

        ulong? autoResolutionMs = null;
        if (parameters.TryGetProperty("autoResolutionMs", out var autoResolution) &&
            autoResolution.ValueKind == JsonValueKind.Number && autoResolution.TryGetUInt64(out var milliseconds))
        {
            autoResolutionMs = milliseconds;
        }

        Emit(new ProviderUserInputEvent(
            L("provider.input.title", "Codex membutuhkan jawaban"),
            L("provider.input.message", "Jawab agar agent dapat melanjutkan pekerjaannya."),
            questions,
            async answers =>
            {
                if (!_serverRequests.TryRemove(requestKey, out _))
                {
                    return;
                }

                var payload = new Dictionary<string, object>(StringComparer.Ordinal);
                if (answers is not null)
                {
                    foreach (var answer in answers)
                    {
                        payload[answer.Key] = new { answers = answer.Value };
                    }
                }

                await SendRpcResultAsync(requestId, new { answers = payload });
            },
            parameters,
            autoResolutionMs,
            () => _serverRequests.ContainsKey(requestKey)));
    }

    private string BuildCommandApprovalSummary(
        JsonElement parameters,
        string? command,
        string? cwd,
        string? reason)
    {
        var lines = new List<string>
        {
            L("provider.approval.command", "Izinkan Codex menjalankan perintah berikut?"),
            string.Empty
        };
        lines.Add(command ?? L("provider.approval.commandMissing", "Perintah tidak tersedia"));
        AddDetail(lines, L("provider.approval.folder", "Folder"), cwd);
        AddDetail(lines, L("provider.approval.reason", "Alasan"), reason);

        if (parameters.TryGetProperty("networkApprovalContext", out var network) &&
            network.ValueKind == JsonValueKind.Object)
        {
            var host = GetString(network, "host");
            var protocol = GetString(network, "protocol");
            AddDetail(lines, L("provider.approval.network", "Akses jaringan"),
                string.Join("://", new[] { protocol, host }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildFileApprovalSummary(JsonElement parameters, string? reason)
    {
        var lines = new List<string> { L("provider.approval.file", "Izinkan Codex mengubah file?"), string.Empty };
        AddDetail(lines, L("provider.approval.reason", "Alasan"),
            reason ?? L("provider.approval.fileReason", "Codex meminta perubahan workspace"));
        AddDetail(lines, L("provider.approval.writeRoot", "Akses tulis tambahan"),
            GetString(parameters, "grantRoot"));
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildPermissionApprovalSummary(
        JsonElement parameters,
        string? cwd,
        string? reason)
    {
        var lines = new List<string>
        {
            L("provider.approval.permission", "Izinkan akses tambahan untuk langkah ini?"),
            string.Empty
        };
        AddDetail(lines, L("provider.approval.workspace", "Folder kerja"), cwd);
        AddDetail(lines, L("provider.approval.reason", "Alasan"), reason);
        if (parameters.TryGetProperty("permissions", out var permissions))
        {
            AddDetail(lines, L("provider.approval.permissions", "Izin yang diminta"), permissions.ToString());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddDetail(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add(string.Empty);
            lines.Add($"{label}: {value}");
        }
    }

    private string L(string key, string fallback)
    {
        var localized = _localize?.Invoke(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }

    private string LF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key, fallback), arguments);

    private static string RequestKey(JsonElement requestId) =>
        requestId.ValueKind == JsonValueKind.String
            ? $"s:{requestId.GetString()}"
            : $"j:{requestId.GetRawText()}";

    private Task SendRpcResultAsync(JsonElement id, object result) =>
        SendAsync(new { id, result }, CancellationToken.None);

    private Task SendRpcErrorAsync(JsonElement id, int code, string message) =>
        SendAsync(new { id, error = new { code, message } }, CancellationToken.None);

    private async Task SendRpcResultSafeAsync(JsonElement id, object result)
    {
        try
        {
            await SendRpcResultAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Emit(new ProviderErrorEvent(LF("provider.error.respond",
                "Gagal merespons Codex: {0}", exception.Message)));
        }
    }

    private async Task SendRpcErrorSafeAsync(JsonElement id, int code, string message)
    {
        try
        {
            await SendRpcErrorAsync(id, code, message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Emit(new ProviderErrorEvent(LF("provider.error.respond",
                "Gagal merespons Codex: {0}", exception.Message)));
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string child) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(parent, out var nested)
            ? GetString(nested, child)
            : null;

    internal static (string Status, IReadOnlyList<string> ActiveFlags) ParseThreadStatus(JsonElement element)
    {
        var status = element.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.Object
            ? statusElement
            : element;
        var type = GetString(status, "type") ?? "unknown";
        if (!status.TryGetProperty("activeFlags", out var flags) || flags.ValueKind != JsonValueKind.Array)
        {
            return (type, []);
        }

        return (type, flags.EnumerateArray()
            .Where(flag => flag.ValueKind == JsonValueKind.String)
            .Select(flag => flag.GetString()!)
            .ToArray());
    }

    private void EmitTokenUsage(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        long? context = usage.TryGetProperty("modelContextWindow", out var cw) && cw.TryGetInt64(out var cwValue)
            ? cwValue
            : null;
        Emit(new ProviderTokenUsageEvent(ReadTotalTokens(usage, "total"), ReadTotalTokens(usage, "last"), context));

        static long ReadTotalTokens(JsonElement usage, string key) =>
            usage.TryGetProperty(key, out var breakdown) &&
            breakdown.ValueKind == JsonValueKind.Object &&
            breakdown.TryGetProperty("totalTokens", out var tokens) &&
            tokens.TryGetInt64(out var value)
                ? value
                : 0;
    }

    internal static string FirstDisplayLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? string.Empty;
        return line.Length <= 72 ? line : $"{line[..69]}...";
    }

    private static string ReadUserMessage(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var input in content.EnumerateArray())
        {
            if (GetString(input, "type") == "text" && GetString(input, "text") is { Length: > 0 } text)
            {
                parts.Add(text);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized || _transport is not { IsRunning: true })
        {
            throw new InvalidOperationException(L("provider.error.notInitialized", "CodexProvider belum diinisialisasi."));
        }
    }

    private async Task StopProcessAsync()
    {
        IsInitialized = false;
        _lifetime?.Cancel();
        FailPending(new IOException("Koneksi Codex App Server ditutup."));
        _serverRequests.Clear();

        if (_transport is { } transport)
        {
            try
            {
                await transport.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var loops = new[] { _readLoop, _errorLoop }.Where(task => task is not null).Cast<Task>().ToArray();
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(loops), Task.Delay(TimeSpan.FromSeconds(2)))
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _readLoop = null;
        _errorLoop = null;
        if (_transport is not null)
        {
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _transport = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _currentTurnId = null;
    }

    private void MarkDisconnected(Exception exception)
    {
        IsInitialized = false;
        FailPending(exception);
        Emit(new ProviderErrorEvent(exception.Message));
    }

    private void FailPending(Exception exception)
    {
        foreach (var (id, completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    internal static bool IsMissingThread(string message) =>
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unknown thread", StringComparison.OrdinalIgnoreCase) ||
        // Thread lama yang file rollout-nya hilang/berbeda versi: mis. "no rollout found for thread id …".
        message.Contains("rollout", StringComparison.OrdinalIgnoreCase);

    // Thread baru yang belum dimaterialisasi (belum ada user message): mis.
    // "thread … is not materialized yet; includeTurns is unavailable before first user message".
    internal static bool IsThreadNotMaterialized(string message) =>
        message.Contains("not materialized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("includeTurns is unavailable", StringComparison.OrdinalIgnoreCase);

    private void Emit(ProviderEvent providerEvent)
    {
        var handlers = EventReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ProviderEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, providerEvent);
            }
            catch
            {
                // Satu consumer UI tidak boleh mematikan read loop protokol.
            }
        }
    }

    private sealed class CodexRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
