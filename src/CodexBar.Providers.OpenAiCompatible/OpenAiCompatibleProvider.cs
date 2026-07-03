using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.OpenAiCompatible;

/// <summary>
/// Base for providers that speak the OpenAI chat-completions streaming wire format
/// (OpenAI itself, OpenRouter, and other compatible gateways). Like
/// <c>AnthropicProvider</c> this keeps the conversation locally (no server-side
/// threads/workspace), so it reports reduced <see cref="ProviderCapabilities"/>.
///
/// Requests are built against an absolute URI (not <see cref="HttpClient.BaseAddress"/>)
/// because the app shares a single <see cref="HttpClient"/> across providers; relying on
/// <c>BaseAddress</c> would let whichever provider initializes first pick the host for
/// every other provider on the same client.
/// </summary>
public class OpenAiCompatibleProvider : IAgentProvider
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string, string>? _localize;
    private readonly List<ChatTurn> _messages = new();
    private readonly Uri _chatCompletionsUri;
    private readonly IReadOnlyList<ProviderModelInfo> _staticModels;
    private readonly string _defaultModel;

    private CancellationTokenSource? _turnCts;
    private string? _selectedModel;

    public OpenAiCompatibleProvider(
        HttpClient httpClient,
        string id,
        string displayName,
        Uri baseAddress,
        string chatCompletionsPath,
        Func<string?> apiKeyProvider,
        Func<string, string>? localize,
        IReadOnlyList<ProviderModelInfo> models,
        string defaultModel)
    {
        _http = httpClient;
        Id = id;
        DisplayName = displayName;
        _chatCompletionsUri = new Uri(baseAddress, chatCompletionsPath);
        _apiKeyProvider = apiKeyProvider;
        _localize = localize;
        _staticModels = models;
        _defaultModel = defaultModel;
    }

    public string Id { get; }
    public string DisplayName { get; }

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
        Emit(new ProviderStatusEvent(LF("status.ready", "{0} siap", DisplayName)));
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
            PlanType: $"{DisplayName} API",
            RequiresOpenAiAuth: false));
    }

    public Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadSummary>>([]);

    public Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadSummary>>([]);

    public Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadMessage>>(
            _messages.Select(m => new ProviderThreadMessage(m.Role, m.Text)).ToArray());

    public virtual Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_staticModels);

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
            Emit(new ProviderErrorEvent(LF("error.noKey", "API key {0} belum diatur.", DisplayName)));
            return;
        }

        _selectedModel = model ?? _selectedModel ?? _defaultModel;
        _messages.Add(new ChatTurn("user", prompt));
        SaveConversation(threadId);

        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _turnCts.Token;

        var request = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = new StringContent(
                BuildRequestJson(_selectedModel, _messages),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var assistant = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;

        try
        {
            Emit(new ProviderStatusEvent(LF("status.thinking", "{0} sedang menjawab…", DisplayName)));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                Emit(new ProviderErrorEvent(string.Format(
                    LF("error.http", "{0} API error {{0}}: {{1}}", DisplayName),
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
                if (payload.Length == 0)
                {
                    continue;
                }

                if (payload == "[DONE]")
                {
                    break;
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
                LF("error.request", "Gagal menghubungi {0}: {{0}}", DisplayName), exception.Message)));
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

    protected HttpClient Http => _http;

    private void Emit(ProviderEvent providerEvent) => EventReceived?.Invoke(this, providerEvent);

    private void SaveConversation(string threadId) => LocalConversationStore.Save(
        Id, threadId, _messages.Select(m => new ProviderThreadMessage(m.Role, m.Text)).ToArray());

    /// <summary>Localize a "{Id}.{key}" lookup; falls back to <paramref name="fallback"/> formatted with <paramref name="args"/>.</summary>
    private string LF(string key, string fallback, params object[] args)
    {
        var value = _localize?.Invoke($"{Id}.{key}");
        var template = string.IsNullOrEmpty(value) || value == $"{Id}.{key}" ? fallback : value;
        return args.Length == 0 ? template : string.Format(template, args);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ---- Testable pure helpers -------------------------------------------------

    public readonly record struct ChatTurn(string Role, string Text);

    public readonly record struct SseChunk(
        string? TextDelta,
        long? InputTokens,
        long? OutputTokens,
        string? Error);

    public static string BuildRequestJson(string model, IReadOnlyList<ChatTurn> messages)
    {
        var payload = new
        {
            model,
            stream = true,
            stream_options = new { include_usage = true },
            messages = messages.Select(m => new { role = m.Role, content = m.Text }).ToArray()
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Parse a single OpenAI-compatible SSE <c>data:</c> JSON payload into a neutral chunk.</summary>
    public static SseChunk ParseSseData(string dataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                var message = errorElement.ValueKind == JsonValueKind.Object &&
                              errorElement.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : "unknown error";
                return new SseChunk(null, null, null, message);
            }

            string? textDelta = null;
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    textDelta = content.GetString();
                }
            }

            long? inputTokens = null;
            long? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var promptTokens) &&
                    promptTokens.TryGetInt64(out var pTok))
                {
                    inputTokens = pTok;
                }

                if (usage.TryGetProperty("completion_tokens", out var completionTokens) &&
                    completionTokens.TryGetInt64(out var cTok))
                {
                    outputTokens = cTok;
                }
            }

            return new SseChunk(textDelta, inputTokens, outputTokens, null);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
