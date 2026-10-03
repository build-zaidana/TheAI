using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.ClaudeCode;

/// <summary>
/// Claude via the user's Claude subscription (Pro/Max/Team), by driving the Claude Code CLI
/// in print mode: <c>claude -p --output-format stream-json</c>. Auth is whatever
/// <c>claude auth login</c> stored, so no API key is involved — analogous to how
/// <c>CodexProvider</c> rides on <c>codex login</c>.
///
/// Every prompt is one short-lived <c>claude</c> process. The thread id is a GUID handed to
/// the CLI as <c>--session-id</c> on the first turn and <c>--resume</c> afterwards, so the CLI
/// keeps the real context; <see cref="LocalConversationStore"/> keeps a copy for the sidebar.
/// </summary>
public sealed class ClaudeCodeProvider : IAgentProvider
{
    private readonly Func<string, string>? _localize;
    private readonly List<ProviderThreadMessage> _messages = new();

    private string? _executable;
    private string _workspacePath = Environment.CurrentDirectory;
    private string _sandboxMode = "workspace-write";
    private string? _loadedThreadId;
    private Process? _turnProcess;
    private CancellationTokenSource? _turnCts;

    public ClaudeCodeProvider(Func<string, string>? localize = null)
    {
        _localize = localize;
    }

    public string Id => "claude-code";
    public string DisplayName => "Claude (Langganan)";

    // Approvals=true only so the sandbox picker stays enabled: it maps to --permission-mode.
    // The CLI never asks for interactive approval in print mode.
    public ProviderCapabilities Capabilities { get; } = new(
        SupportsThreads: true,
        SupportsApprovals: true,
        SupportsStreaming: true,
        SupportsWorkspace: true,
        SupportsApiKey: false,
        SupportsModelPicker: true);

    public bool IsInitialized { get; private set; }

    public event EventHandler<ProviderEvent>? EventReceived;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _executable = ClaudeCodeExecutableLocator.Locate();
        IsInitialized = true;
        Emit(new ProviderStatusEvent(L("claudeCode.status.ready", "Claude siap")));
        return Task.CompletedTask;
    }

    public Task<string> StartOrResumeThreadAsync(
        string workspacePath,
        string? threadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            _workspacePath = workspacePath;
        }

        _sandboxMode = sandboxMode;
        if (string.IsNullOrWhiteSpace(threadId) || !Guid.TryParse(threadId, out _))
        {
            threadId = Guid.NewGuid().ToString();
        }

        LoadThread(threadId);
        return Task.FromResult(threadId);
    }

    public async Task<ProviderAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var executable = _executable ?? ClaudeCodeExecutableLocator.Locate();
        var (exitCode, stdout, _) = await RunToEndAsync(executable, "auth status", cancellationToken)
            .ConfigureAwait(false);
        var status = ParseAuthStatus(stdout);
        return new ProviderAccountInfo(
            IsAuthenticated: exitCode == 0 && status.LoggedIn,
            AuthenticationMethod: status.AuthMethod ?? "none",
            Email: status.Email,
            PlanType: status.SubscriptionType ?? "Claude",
            RequiresOpenAiAuth: false);
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
        Task.FromResult(threadId == _loadedThreadId
            ? (IReadOnlyList<ProviderThreadMessage>)_messages.ToArray()
            : LocalConversationStore.Load(Id, threadId));

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
        var executable = _executable ?? ClaudeCodeExecutableLocator.Locate();
        LoadThread(threadId);

        var fullPrompt = AppendAttachments(prompt, attachments);
        var resume = _messages.Any(m => m.Role == "assistant");
        _messages.Add(new ProviderThreadMessage("user", prompt));
        Save(threadId);

        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _turnCts.Token;

        try
        {
            Emit(new ProviderStatusEvent(L("claudeCode.status.thinking", "Claude sedang menjawab…")));
            var outcome = await RunTurnAsync(executable, threadId, resume, fullPrompt, model, reasoningEffort,
                attachments, token).ConfigureAwait(false);

            // A cancelled/failed first turn can still have created the CLI session; the id is
            // then "already in use", so retry once as a resume.
            if (!resume && outcome.Error is { } firstError &&
                firstError.Contains("already in use", StringComparison.OrdinalIgnoreCase))
            {
                outcome = await RunTurnAsync(executable, threadId, true, fullPrompt, model, reasoningEffort,
                    attachments, token).ConfigureAwait(false);
            }

            if (outcome.Error is { } error)
            {
                Emit(new ProviderErrorEvent(error));
                return;
            }

            _messages.Add(new ProviderThreadMessage("assistant", outcome.Text));
            Save(threadId);
            if (outcome.TotalTokens > 0)
            {
                Emit(new ProviderTokenUsageEvent(outcome.TotalTokens, outcome.OutputTokens, null));
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
                L("claudeCode.error.run", "Gagal menjalankan Claude Code: {0}"), exception.Message)));
        }
    }

    public Task CompactAsync(string threadId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _turnCts?.Cancel();
        KillTurnProcess();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _turnCts?.Cancel();
        KillTurnProcess();
        _turnCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- Process plumbing --------------------------------------------------------

    private readonly record struct TurnOutcome(string Text, string? Error, long TotalTokens, long OutputTokens);

    private async Task<TurnOutcome> RunTurnAsync(
        string executable,
        string threadId,
        bool resume,
        string prompt,
        string? model,
        string? reasoningEffort,
        IReadOnlyList<ProviderInputAttachment>? attachments,
        CancellationToken token)
    {
        var extraDirs = attachments?
            .Select(a => Path.GetDirectoryName(a.Path))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var arguments = BuildArguments(threadId, resume, model, reasoningEffort, _sandboxMode, extraDirs);

        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, _workspacePath) };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("claude gagal dijalankan.");
        }

        _turnProcess = process;
        process.BeginErrorReadLine();
        using var registration = token.Register(KillTurnProcess);

        // Prompt goes through stdin: no quoting or command-line length limits.
        await process.StandardInput.WriteAsync(prompt.AsMemory(), token).ConfigureAwait(false);
        process.StandardInput.Close();

        var text = new StringBuilder();
        var sawDelta = false;
        string? resultText = null;
        string? error = null;
        long totalTokens = 0;
        long outputTokens = 0;
        var pendingEdits = new Dictionary<string, ProviderFileChange>(StringComparer.Ordinal);

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)) is not null)
        {
            TrackFileEdits(line, pendingEdits);
            var chunk = ParseStreamLine(line);
            switch (chunk.Kind)
            {
                case StreamKind.TextBlockStart when text.Length > 0 && !text.ToString().EndsWith('\n'):
                    text.Append("\n\n");
                    Emit(new ProviderTextDeltaEvent("\n\n"));
                    break;
                case StreamKind.TextDelta:
                    sawDelta = true;
                    text.Append(chunk.Text);
                    Emit(new ProviderTextDeltaEvent(chunk.Text!));
                    break;
                case StreamKind.ReasoningDelta:
                    Emit(new ProviderReasoningDeltaEvent(chunk.Text!));
                    break;
                case StreamKind.ToolUse:
                    Emit(new ProviderStatusEvent(string.Format(
                        L("claudeCode.status.tool", "Claude memakai {0}…"), chunk.Text)));
                    break;
                case StreamKind.Result:
                    resultText = chunk.Text;
                    totalTokens = chunk.TotalTokens;
                    outputTokens = chunk.OutputTokens;
                    if (chunk.IsError)
                    {
                        error = string.IsNullOrWhiteSpace(chunk.Text)
                            ? L("claudeCode.error.unknown", "Claude Code melaporkan error.")
                            : chunk.Text;
                    }
                    break;
            }
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        _turnProcess = null;
        token.ThrowIfCancellationRequested();

        if (error is null && resultText is null && process.ExitCode != 0)
        {
            string stderrText;
            lock (stderr)
            {
                stderrText = stderr.ToString().Trim();
            }

            error = string.Format(
                L("claudeCode.error.exit", "Claude Code keluar dengan kode {0}: {1}"),
                process.ExitCode,
                Truncate(stderrText.Length > 0 ? stderrText : "-", 400));
        }

        // Without partial-message support nothing streamed; fall back to the final result.
        if (error is null && !sawDelta && !string.IsNullOrEmpty(resultText))
        {
            text.Append(resultText);
            Emit(new ProviderTextDeltaEvent(resultText));
        }

        return new TurnOutcome(text.ToString(), error, totalTokens, outputTokens);
    }

    // Edit/Write tool calls arrive in the full assistant message before the tool runs; they are
    // reported once the matching tool_result says the edit went through.
    private void TrackFileEdits(string line, Dictionary<string, ProviderFileChange> pending)
    {
        if (line.Contains("\"tool_use\"", StringComparison.Ordinal))
        {
            foreach (var edit in ParseFileEdits(line))
            {
                if (ToFileChange(edit) is { } change)
                {
                    pending[edit.ToolUseId] = change;
                }
            }
        }

        if (line.Contains("\"tool_result\"", StringComparison.Ordinal))
        {
            var done = new List<ProviderFileChange>();
            foreach (var (id, isError) in ParseToolResults(line))
            {
                if (pending.Remove(id, out var change) && !isError)
                {
                    done.Add(change);
                }
            }

            if (done.Count > 0)
            {
                Emit(new ProviderFileChangeEvent(done));
            }
        }
    }

    private static ProviderFileChange? ToFileChange(FileEdit edit)
    {
        if (edit.Content is { } content)
        {
            // Write: diff against what is on disk right now (the tool has not run yet).
            string? before = null;
            try
            {
                var info = new FileInfo(edit.Path);
                if (info.Exists && info.Length <= 512 * 1024)
                {
                    before = File.ReadAllText(edit.Path);
                }
                else if (info.Exists)
                {
                    return new ProviderFileChange(edit.Path, "update", string.Empty);
                }
            }
            catch (Exception)
            {
                // Unreadable: show it as a plain rewrite.
            }

            return before is null
                ? new ProviderFileChange(edit.Path, "add", string.Concat(LineDiff.SplitLines(content).Select(l => "+" + l + "\n")))
                : new ProviderFileChange(edit.Path, "update", LineDiff.Build(before, content));
        }

        var diff = string.Concat(edit.Replacements.Select(r => LineDiff.Build(r.Old, r.New)));
        return new ProviderFileChange(edit.Path, "update", diff);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string arguments, string workingDirectory)
    {
        var isScript = Path.GetExtension(executable) is ".cmd" or ".bat";
        var startInfo = new ProcessStartInfo
        {
            FileName = isScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable,
            Arguments = isScript ? $"/d /s /c \"\"{executable}\" {arguments}\"" : arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        // An API key in the environment would take precedence over the subscription login.
        startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        startInfo.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToEndAsync(
        string executable, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executable, arguments, Environment.CurrentDirectory)
        };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private void KillTurnProcess()
    {
        try
        {
            if (_turnProcess is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already exited or disposed.
        }
    }

    private void LoadThread(string threadId)
    {
        if (_loadedThreadId == threadId)
        {
            return;
        }

        _messages.Clear();
        _messages.AddRange(LocalConversationStore.Load(Id, threadId));
        _loadedThreadId = threadId;
    }

    private void Save(string threadId) => LocalConversationStore.Save(Id, threadId, _messages.ToArray());

    private void Emit(ProviderEvent providerEvent) => EventReceived?.Invoke(this, providerEvent);

    private string L(string key, string fallback)
    {
        var value = _localize?.Invoke(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ---- Testable pure helpers -------------------------------------------------

    internal enum StreamKind { None, TextBlockStart, TextDelta, ReasoningDelta, ToolUse, Result }

    internal readonly record struct StreamChunk(
        StreamKind Kind,
        string? Text = null,
        bool IsError = false,
        long TotalTokens = 0,
        long OutputTokens = 0);

    internal sealed record FileEdit(
        string ToolUseId,
        string Path,
        IReadOnlyList<(string Old, string New)> Replacements,
        string? Content);

    /// <summary>Edit, MultiEdit and Write tool calls in a full <c>assistant</c> stream-json message.</summary>
    internal static IReadOnlyList<FileEdit> ParseFileEdits(string line)
    {
        var edits = new List<FileEdit>();
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (GetString(root, "type") != "assistant" ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return edits;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (GetString(block, "type") != "tool_use" ||
                    GetString(block, "id") is not { Length: > 0 } id ||
                    !block.TryGetProperty("input", out var input) ||
                    GetString(input, "file_path") is not { Length: > 0 } path)
                {
                    continue;
                }

                switch (GetString(block, "name"))
                {
                    case "Edit":
                        edits.Add(new FileEdit(id, path,
                            [(GetString(input, "old_string") ?? string.Empty, GetString(input, "new_string") ?? string.Empty)], null));
                        break;
                    case "MultiEdit" when input.TryGetProperty("edits", out var list) && list.ValueKind == JsonValueKind.Array:
                        edits.Add(new FileEdit(id, path, list.EnumerateArray()
                            .Select(e => (GetString(e, "old_string") ?? string.Empty, GetString(e, "new_string") ?? string.Empty))
                            .ToArray(), null));
                        break;
                    case "Write":
                        edits.Add(new FileEdit(id, path, [], GetString(input, "content") ?? string.Empty));
                        break;
                }
            }
        }
        catch (JsonException)
        {
        }

        return edits;
    }

    /// <summary>tool_result blocks in a <c>user</c> stream-json message: (tool_use_id, is_error).</summary>
    internal static IReadOnlyList<(string Id, bool IsError)> ParseToolResults(string line)
    {
        var results = new List<(string, bool)>();
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (GetString(root, "type") != "user" ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (GetString(block, "type") == "tool_result" && GetString(block, "tool_use_id") is { Length: > 0 } id)
                {
                    results.Add((id, block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True));
                }
            }
        }
        catch (JsonException)
        {
        }

        return results;
    }

    internal readonly record struct AuthStatus(
        bool LoggedIn, string? AuthMethod, string? Email, string? SubscriptionType);

    internal static string DefaultModel => "default";

    internal static IReadOnlyList<ProviderModelInfo> KnownModels() =>
    [
        new("default", "default", "Default akun", "Model bawaan langganan", true, "default"),
        new("opus", "opus", "Claude Opus", "Paling cerdas", false, "default"),
        new("sonnet", "sonnet", "Claude Sonnet", "Seimbang", false, "default"),
        new("haiku", "haiku", "Claude Haiku", "Paling cepat", false, "default"),
    ];

    internal static string BuildArguments(
        string sessionId,
        bool resume,
        string? model,
        string? reasoningEffort,
        string sandboxMode,
        IReadOnlyList<string> extraDirectories)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            resume ? "--resume" : "--session-id", sessionId,
            "--permission-mode", PermissionModeFor(sandboxMode)
        };

        if (!string.IsNullOrWhiteSpace(model) && model != DefaultModel)
        {
            args.Add("--model");
            args.Add(model);
        }

        var effort = reasoningEffort switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null
        };
        if (effort is not null)
        {
            args.Add("--effort");
            args.Add(effort);
        }

        foreach (var directory in extraDirectories)
        {
            args.Add("--add-dir");
            args.Add(directory);
        }

        return string.Join(' ', args.Select(Quote));
    }

    internal static string PermissionModeFor(string sandboxMode) => sandboxMode switch
    {
        "read-only" => "plan",
        "danger-full-access" => "bypassPermissions",
        _ => "acceptEdits"
    };

    internal static string AppendAttachments(string prompt, IReadOnlyList<ProviderInputAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return prompt;
        }

        var builder = new StringBuilder(prompt).AppendLine().AppendLine();
        builder.AppendLine("Lampiran (baca file ini):");
        foreach (var attachment in attachments)
        {
            builder.Append("- ").AppendLine(attachment.Path);
        }

        return builder.ToString();
    }

    /// <summary>Parse one line of <c>claude --output-format stream-json</c> into a neutral chunk.</summary>
    internal static StreamChunk ParseStreamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (GetString(root, "type"))
            {
                case "stream_event" when root.TryGetProperty("event", out var ev):
                    return ParseStreamEvent(ev);

                case "result":
                    long input = 0, output = 0;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        input = GetLong(usage, "input_tokens") +
                                GetLong(usage, "cache_read_input_tokens") +
                                GetLong(usage, "cache_creation_input_tokens");
                        output = GetLong(usage, "output_tokens");
                    }

                    var isError = (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True) ||
                                  (GetString(root, "subtype") is { } subtype && subtype != "success");
                    return new StreamChunk(StreamKind.Result, GetString(root, "result"), isError, input + output, output);

                default:
                    return default;
            }
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static StreamChunk ParseStreamEvent(JsonElement ev)
    {
        switch (GetString(ev, "type"))
        {
            case "content_block_start" when ev.TryGetProperty("content_block", out var block):
                return GetString(block, "type") switch
                {
                    "text" => new StreamChunk(StreamKind.TextBlockStart),
                    "tool_use" => new StreamChunk(StreamKind.ToolUse, GetString(block, "name") ?? "tool"),
                    _ => default
                };

            case "content_block_delta" when ev.TryGetProperty("delta", out var delta):
                return GetString(delta, "type") switch
                {
                    "text_delta" when GetString(delta, "text") is { Length: > 0 } text =>
                        new StreamChunk(StreamKind.TextDelta, text),
                    "thinking_delta" when GetString(delta, "thinking") is { Length: > 0 } thinking =>
                        new StreamChunk(StreamKind.ReasoningDelta, thinking),
                    _ => default
                };

            default:
                return default;
        }
    }

    /// <summary>Parse the JSON printed by <c>claude auth status</c>.</summary>
    internal static AuthStatus ParseAuthStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
            return new AuthStatus(
                loggedIn,
                GetString(root, "authMethod"),
                GetString(root, "email"),
                GetString(root, "subscriptionType"));
        }
        catch (JsonException)
        {
            return new AuthStatus(false, null, null, null);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0
            ? argument
            : "\"" + argument.Replace("\"", "\\\"") + (argument.EndsWith('\\') ? "\\\"" : "\"");
}
