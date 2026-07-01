using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.Codex;

public sealed class CodexProvider : IAgentProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _serverRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private CancellationTokenSource? _lifetime;
    private Process? _process;
    private ProcessJob? _processJob;
    private StreamWriter? _writer;
    private Task? _readLoop;
    private Task? _errorLoop;
    private long _nextRequestId;
    private string? _currentThreadId;
    private string? _currentTurnId;

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
            if (IsInitialized && _process is { HasExited: false })
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);
            var executable = CodexExecutableLocator.Locate();
            _lifetime = new CancellationTokenSource();

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "app-server --stdio",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            try
            {
                if (!_process.Start())
                {
                    throw new InvalidOperationException("Codex App Server gagal dijalankan.");
                }

                _processJob = ProcessJob.Attach(_process);

                _writer = _process.StandardInput;
                _writer.AutoFlush = true;
                _readLoop = ReadLoopAsync(_process, _lifetime.Token);
                _errorLoop = ReadErrorLoopAsync(_process, _lifetime.Token);

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
                Emit(new ProviderStatusEvent("Codex terhubung"));
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
                Emit(new ProviderStatusEvent("Thread sebelumnya tidak tersedia; membuat thread baru."));
                result = await StartThreadAsync(workspacePath, sandboxMode, approvalPolicy, cancellationToken);
            }
        }
        else
        {
            result = await StartThreadAsync(workspacePath, sandboxMode, approvalPolicy, cancellationToken);
        }

        _currentThreadId = result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Codex tidak mengembalikan thread ID.");

        Emit(new ProviderStatusEvent("Thread siap"));
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
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                switch (GetString(item, "type"))
                {
                    case "userMessage":
                        var userText = ReadUserMessage(item);
                        if (!string.IsNullOrWhiteSpace(userText))
                        {
                            messages.Add(new ProviderThreadMessage("user", userText));
                        }
                        break;

                    case "agentMessage":
                        if (GetString(item, "text") is { Length: > 0 } agentText)
                        {
                            messages.Add(new ProviderThreadMessage("assistant", agentText));
                        }
                        break;
                }
            }
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
                throw new FileNotFoundException($"Lampiran tidak ditemukan: {attachment.DisplayName}", fullPath);
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
        Emit(new ProviderStatusEvent("Codex sedang bekerja…"));
    }

    public async Task CompactAsync(string threadId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await SendRequestAsync("thread/compact/start", new { threadId }, cancellationToken);
        Emit(new ProviderStatusEvent("Memadatkan konteks percakapan…"));
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
        timeout.CancelAfter(RequestTimeout);

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
            throw new TimeoutException($"Codex tidak merespons permintaan '{method}' dalam {RequestTimeout.TotalSeconds:0} detik.");
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
        if (_writer is null || _process is null || _process.HasExited)
        {
            throw new InvalidOperationException("Codex App Server belum berjalan.");
        }

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
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
                    Emit(new ProviderErrorEvent($"Event Codex tidak valid: {exception.Message}"));
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
                MarkDisconnected(new IOException("Gagal membaca respons Codex App Server.", exception));
            }
        }
    }

    private async Task ReadErrorLoopAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
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
                _currentTurnId = GetNestedString(parameters, "turn", "id") ?? _currentTurnId;
                Emit(new ProviderStatusEvent("Codex sedang bekerja…"));
                break;

            case "turn/completed":
                var status = GetNestedString(parameters, "turn", "status") ?? "completed";
                _currentTurnId = null;
                _serverRequests.Clear();
                Emit(new ProviderCompletedEvent(status));
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
                Emit(new ProviderStatusEvent("Konteks percakapan dipadatkan."));
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
                Emit(new ProviderStatusEvent("Menjalankan langkah…"));
                break;
        }
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
            Emit(new ProviderStatusEvent("Format input MCP ini belum dapat ditampilkan dengan aman."));
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
            var description = GetString(fieldSchema, "description") ?? $"Masukkan nilai untuk {title}.";
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
            Emit(new ProviderStatusEvent("Form MCP tidak memiliki field yang dapat ditampilkan."));
            return;
        }

        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;
        var serverName = GetString(parameters, "serverName") ?? "MCP server";
        Emit(new ProviderUserInputEvent(
            $"Input untuk {serverName}",
            GetString(parameters, "message") ?? "Tool membutuhkan informasi tambahan.",
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
            Emit(new ProviderStatusEvent("MCP mengirim tautan yang tidak valid atau tidak aman."));
            return;
        }

        var requestKey = RequestKey(requestId);
        _serverRequests[requestKey] = 0;
        var serverName = GetString(parameters, "serverName") ?? "MCP server";
        Emit(new ProviderUserInputEvent(
            $"Hubungkan {serverName}",
            GetString(parameters, "message") ?? "Selesaikan langkah pada browser lalu kembali ke CodexBar.",
            [new ProviderInputQuestion(
                "completed",
                "Konfirmasi",
                "Setelah langkah di browser selesai, pilih konfirmasi berikut.",
                [new ProviderInputOption("Saya sudah selesai", string.Empty, "completed")])],
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

    private static IReadOnlyList<ProviderInputOption> ReadMcpOptions(JsonElement schema, string type)
    {
        if (type == "boolean")
        {
            return
            [
                new ProviderInputOption("Ya", string.Empty, "true"),
                new ProviderInputOption("Tidak", string.Empty, "false")
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
                    GetString(value, "title") ?? GetString(value, "const") ?? "Pilihan",
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
                    GetString(question, "header") ?? "Pertanyaan",
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
            Emit(new ProviderStatusEvent("Codex meminta input tanpa pertanyaan yang dapat ditampilkan."));
            return;
        }

        ulong? autoResolutionMs = null;
        if (parameters.TryGetProperty("autoResolutionMs", out var autoResolution) &&
            autoResolution.ValueKind == JsonValueKind.Number && autoResolution.TryGetUInt64(out var milliseconds))
        {
            autoResolutionMs = milliseconds;
        }

        Emit(new ProviderUserInputEvent(
            "Codex membutuhkan jawaban",
            "Jawab agar agent dapat melanjutkan pekerjaannya.",
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

    private static string BuildCommandApprovalSummary(
        JsonElement parameters,
        string? command,
        string? cwd,
        string? reason)
    {
        var lines = new List<string> { "Izinkan Codex menjalankan perintah berikut?", string.Empty };
        lines.Add(command ?? "Perintah tidak tersedia");
        AddDetail(lines, "Folder", cwd);
        AddDetail(lines, "Alasan", reason);

        if (parameters.TryGetProperty("networkApprovalContext", out var network) &&
            network.ValueKind == JsonValueKind.Object)
        {
            var host = GetString(network, "host");
            var protocol = GetString(network, "protocol");
            AddDetail(lines, "Akses jaringan", string.Join("://", new[] { protocol, host }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFileApprovalSummary(JsonElement parameters, string? reason)
    {
        var lines = new List<string> { "Izinkan Codex mengubah file?", string.Empty };
        AddDetail(lines, "Alasan", reason ?? "Codex meminta perubahan workspace");
        AddDetail(lines, "Akses tulis tambahan", GetString(parameters, "grantRoot"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPermissionApprovalSummary(
        JsonElement parameters,
        string? cwd,
        string? reason)
    {
        var lines = new List<string> { "Izinkan akses tambahan untuk langkah ini?", string.Empty };
        AddDetail(lines, "Folder kerja", cwd);
        AddDetail(lines, "Alasan", reason);
        if (parameters.TryGetProperty("permissions", out var permissions))
        {
            AddDetail(lines, "Izin yang diminta", permissions.ToString());
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
            Emit(new ProviderErrorEvent($"Gagal merespons Codex: {exception.Message}"));
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
            Emit(new ProviderErrorEvent($"Gagal merespons Codex: {exception.Message}"));
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
        if (!IsInitialized || _process is null || _process.HasExited)
        {
            throw new InvalidOperationException("CodexProvider belum diinisialisasi.");
        }
    }

    private async Task StopProcessAsync()
    {
        IsInitialized = false;
        _lifetime?.Cancel();
        FailPending(new IOException("Koneksi Codex App Server ditutup."));
        _serverRequests.Clear();

        if (_writer is not null)
        {
            try
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited && !process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
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

        _writer = null;
        _readLoop = null;
        _errorLoop = null;
        _process?.Dispose();
        _process = null;
        _processJob?.Dispose();
        _processJob = null;
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
