using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.Anthropic;

/// <summary>
/// Claude (Anthropic Messages API) provider. Unlike Codex this has no server-side
/// threads or workspace sandbox, so it keeps the conversation locally and reports
/// reduced <see cref="ProviderCapabilities"/>; the UI adapts via those flags.
///
/// The <see cref="HttpClient"/> is injected so the request building and SSE parsing
/// can be unit-tested without a live API call.
/// </summary>
public sealed class AnthropicProvider : IAgentProvider
{
    private const string MessagesPath = "v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 4096;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string, string>? _localize;
    private readonly List<ChatTurn> _messages = new();
    private readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private CancellationTokenSource? _turnCts;
    private string? _selectedModel;

    public AnthropicProvider(
        HttpClient httpClient,
        Func<string?> apiKeyProvider,
        Func<string, string>? localize = null)
    {
        _http = httpClient;
        _apiKeyProvider = apiKeyProvider;
        _localize = localize;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://api.anthropic.com/");
        }
    }

    public string Id => "anthropic";
    public string DisplayName => "Claude";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsThreads: false,
        SupportsApprovals: false,
        SupportsStreaming: true,
        SupportsWorkspace: false,
        SupportsApiKey: true,
        SupportsModelPicker: true);

    public bool IsInitialized { get; private set; }

    public event EventHandler<ProviderEvent>? EventReceived;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsInitialized = true;
        Emit(new ProviderStatusEvent(L("anthropic.status.ready", "Claude siap")));
        return Task.CompletedTask;
    }

    public Task<string> StartOrResumeThreadAsync(
        string workspacePath,
        string? threadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default)
    {
        // No server-side threads: a "thread" is just a fresh local conversation.
        if (string.IsNullOrWhiteSpace(threadId))
        {
            _messages.Clear();
            threadId = $"local-{Guid.NewGuid():N}";
        }
        else if (_messages.Count == 0)
        {
            // Resuming a thread id from a previous session: repopulate from disk (no-op if
            // nothing was ever saved under it, e.g. a thread that never got a first turn).
            _messages.AddRange(LocalConversationStore
                .Load(Id, threadId)
                .Select(m => new ChatTurn(m.Role, m.Text)));
        }

        return Task.FromResult(threadId);
    }

    public Task<ProviderAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var hasKey = !string.IsNullOrWhiteSpace(_apiKeyProvider());
        return Task.FromResult(new ProviderAccountInfo(
            IsAuthenticated: hasKey,
            AuthenticationMethod: "api-key",
            Email: null,
            PlanType: "Anthropic API",
            RequiresOpenAiAuth: false));
    }

    public Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalConversationStore.ListSummaries(Id, limit));

    public Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalConversationStore.ListSummaries(Id, limit, searchTerm));

    public Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadMessage>>(
            _messages.Select(m => new ProviderThreadMessage(m.Role, m.Text)).ToArray());

    public Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(KnownModels());

    public async Task SendPromptAsync(
        string threadId,
        string prompt,
        IReadOnlyList<ProviderInputAttachment>? attachments = null,
        string? model = null,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Emit(new ProviderErrorEvent(L("anthropic.error.noKey", "API key Anthropic belum diatur.")));
            return;
        }

        _selectedModel = model ?? _selectedModel ?? DefaultModel;
        _messages.Add(new ChatTurn("user", prompt));
        SaveConversation(threadId);

        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _turnCts.Token;

        var request = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = new StringContent(
                BuildRequestJson(_selectedModel, _messages, MaxTokens),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var assistant = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;

        try
        {
            Emit(new ProviderStatusEvent(L("anthropic.status.thinking", "Claude sedang menjawab…")));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                Emit(new ProviderErrorEvent(string.Format(
                    L("anthropic.error.http", "Claude API error {0}: {1}"),
                    (int)response.StatusCode, Truncate(body, 300))));
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]")
                {
                    continue;
                }

                var chunk = ParseSseData(payload);
                if (chunk.Error is { Length: > 0 } error)
                {
                    Emit(new ProviderErrorEvent(error));
                    return;
                }

                if (chunk.TextDelta is { Length: > 0 } delta)
                {
                    assistant.Append(delta);
                    Emit(new ProviderTextDeltaEvent(delta));
                }

                if (chunk.InputTokens is { } inTok)
                {
                    inputTokens = inTok;
                }

                if (chunk.OutputTokens is { } outTok)
                {
                    outputTokens = outTok;
                }

                if (chunk.Stop)
                {
                    break;
                }
            }

            _messages.Add(new ChatTurn("assistant", assistant.ToString()));
            SaveConversation(threadId);
            if (inputTokens + outputTokens > 0)
            {
                Emit(new ProviderTokenUsageEvent(inputTokens + outputTokens, outputTokens, null));
            }

            Emit(new ProviderCompletedEvent("completed"));
        }
        catch (OperationCanceledException)
        {
            Emit(new ProviderCompletedEvent("cancelled"));
        }
        catch (Exception exception)
        {
            Emit(new ProviderErrorEvent(string.Format(
                L("anthropic.error.request", "Gagal menghubungi Claude: {0}"), exception.Message)));
        }
    }

    public Task CompactAsync(string threadId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _turnCts?.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _turnCts?.Cancel();
        _turnCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Emit(ProviderEvent providerEvent) => EventReceived?.Invoke(this, providerEvent);

    private void SaveConversation(string threadId) => LocalConversationStore.Save(
        Id, threadId, _messages.Select(m => new ProviderThreadMessage(m.Role, m.Text)).ToArray());

    private string L(string key, string fallback)
    {
        var value = _localize?.Invoke(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ---- Testable pure helpers -------------------------------------------------

    internal readonly record struct ChatTurn(string Role, string Text);

    internal readonly record struct SseChunk(
        string? TextDelta,
        long? InputTokens,
        long? OutputTokens,
        bool Stop,
        string? Error);

    internal static string DefaultModel => "claude-sonnet-5";

    internal static IReadOnlyList<ProviderModelInfo> KnownModels() =>
    [
        new("claude-opus-4-8", "claude-opus-4-8", "Claude Opus 4.8", "Paling cerdas", false, "default"),
        new("claude-sonnet-5", "claude-sonnet-5", "Claude Sonnet 5", "Seimbang (default)", true, "default"),
        new("claude-haiku-4-5-20251001", "claude-haiku-4-5-20251001", "Claude Haiku 4.5", "Paling cepat", false, "default"),
        new("claude-fable-5", "claude-fable-5", "Claude Fable 5", "Kreatif", false, "default"),
    ];

    internal static string BuildRequestJson(string model, IReadOnlyList<ChatTurn> messages, int maxTokens)
    {
        var payload = new
        {
            model,
            max_tokens = maxTokens,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Text }).ToArray()
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Parse a single Anthropic SSE <c>data:</c> JSON payload into a neutral chunk.</summary>
    internal static SseChunk ParseSseData(string dataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            switch (type)
            {
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var deltaType) &&
                        deltaType.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var text))
                    {
                        return new SseChunk(text.GetString(), null, null, false, null);
                    }

                    return default;

                case "message_start":
                    return new SseChunk(null, ReadUsage(root, "message", "input_tokens"), null, false, null);

                case "message_delta":
                    return new SseChunk(null, null, ReadUsage(root, null, "output_tokens"), false, null);

                case "message_stop":
                    return new SseChunk(null, null, null, true, null);

                case "error":
                    var message = root.TryGetProperty("error", out var err) &&
                                  err.TryGetProperty("message", out var msg)
                        ? msg.GetString()
                        : "unknown error";
                    return new SseChunk(null, null, null, false, message);

                default:
                    return default;
            }
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static long? ReadUsage(JsonElement root, string? container, string field)
    {
        var scope = root;
        if (container is not null)
        {
            if (!root.TryGetProperty(container, out scope))
            {
                return null;
            }
        }

        return scope.TryGetProperty("usage", out var usage) &&
               usage.TryGetProperty(field, out var value) &&
               value.TryGetInt64(out var tokens)
            ? tokens
            : null;
    }
}
