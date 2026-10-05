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

    public async Task<string> StartOrResumeThreadAsync(
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

        // A Claude Code transcript can be tens of megabytes: read it off the caller's (UI) thread.
        var id = threadId;
        await Task.Run(() => LoadThread(id), cancellationToken).ConfigureAwait(false);
        return threadId;
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

    // The session file is the truth (Claude Code may have moved on since): read it fresh while no
    // turn of ours is running; the local copy is only a fallback.
    public async Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (_turnProcess is null && ClaudeSessionFiles.FindFile(threadId) is { } transcript)
        {
            var fresh = await Task.Run(() => ClaudeSessionFiles.ReadMessages(transcript), cancellationToken).ConfigureAwait(false);
            var context = await Task.Run(() => ClaudeSessionFiles.LastContextTokens(transcript), cancellationToken).ConfigureAwait(false);
            if (context > 0)
            {
                Emit(new ProviderTokenUsageEvent(context, 0, ClaudeSessionFiles.ContextWindow(null, context)));
            }

            if (fresh.Count > 0)
            {
                if (threadId == _loadedThreadId)
                {
                    _messages.Clear();
                    _messages.AddRange(fresh);
                }

                return fresh;
            }
        }

        return threadId == _loadedThreadId
            ? _messages.ToArray()
            : LocalConversationStore.Load(Id, threadId);
    }

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

            _messages.Add(new ProviderThreadMessage("assistant", outcome.Text, outcome.Steps is { Count: > 0 } ? outcome.Steps : null));
            Save(threadId);

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

    private readonly record struct TurnOutcome(
        string Text, string? Error, long TotalTokens, long OutputTokens, IReadOnlyList<ProviderActivityStep>? Steps = null);

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

        // The turn's work as Claude Code lists it, from the complete assistant messages.
        var parts = new List<(ProviderActivityStep Step, string? ToolId)>();
        var failed = new HashSet<string>(StringComparer.Ordinal);

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)) is not null)
        {
            TrackFileEdits(line, pendingEdits);
            TrackActivity(line, parts, failed);
            if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) &&
                ClaudeSessionFiles.ContextTokens(line) is > 0 and var context)
            {
                Emit(new ProviderTokenUsageEvent(context, 0, ClaudeSessionFiles.ContextWindow(model, context)));
            }
            if (line.Contains("\"rate_limit_event\"", StringComparison.Ordinal) && ParseRateLimitEvent(line) is { } quota)
            {
                Emit(quota);
            }

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

        if (parts.Any(part => !part.Step.IsText))
        {
            var (steps, answer) = ProviderActivity.SplitTurn(parts, failed);
            return new TurnOutcome(answer, error, totalTokens, outputTokens, steps);
        }

        return new TurnOutcome(text.ToString(), error, totalTokens, outputTokens);
    }

    // A tool call (or a failed result) changes the work list: report it with the answer so far.
    private void TrackActivity(string line, List<(ProviderActivityStep Step, string? ToolId)> parts, HashSet<string> failed)
    {
        var changed = false;
        if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
        {
            var added = ClaudeSessionFiles.AssistantParts(line).ToArray();
            parts.AddRange(added);
            changed = added.Any(part => !part.Step.IsText);
        }
        else if (line.Contains("\"is_error\":true", StringComparison.Ordinal))
        {
            foreach (var id in ClaudeSessionFiles.FailedToolIds(line))
            {
                changed |= failed.Add(id);
            }
        }

        if (changed)
        {
            var (steps, answer) = ProviderActivity.SplitTurn(parts, failed);
            Emit(new ProviderActivityEvent(steps, answer));
        }
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

    /// <summary>
    /// One stateless question to a small model through the user's Claude Code login (used for
    /// next-prompt suggestions). Runs in the temp folder, so the throwaway session never shows
    /// up among the user's projects; null when the CLI is missing, fails, or times out.
    /// </summary>
    public static async Task<string?> AskOnceAsync(string prompt, string model, CancellationToken cancellationToken)
    {
        if (ClaudeCodeExecutableLocator.TryLocate() is not { } executable)
        {
            return null;
        }

        // No MCP servers and only user settings: loading the user's plugins and servers would more
        // than double the start-up time of this one-shot call.
        var arguments = string.Join(' ', new[]
        {
            "-p", "--model", model, "--output-format", "text", "--max-turns", "1",
            "--strict-mcp-config", "--setting-sources", "user"
        }.Select(Quote));
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, Path.GetTempPath()) };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
            });
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
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
        // Lets CodexBar's own hook relay recognise (and skip) the sessions it runs itself.
        startInfo.Environment["CODEXBAR_CHILD"] = "1";
        // Don't pass on the identity of a Claude Code session CodexBar itself may have been started
        // from (it would label CodexBar's sessions as that session's entrypoint).
        startInfo.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        startInfo.Environment.Remove("CLAUDECODE");
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
        if (ClaudeSessionFiles.FindFile(threadId) is { } transcript)
        {
            // The session file (ours or one started in a terminal, IDE or the desktop app) is
            // the history; turns continue it with --resume.
            _messages.AddRange(ClaudeSessionFiles.ReadMessages(transcript));
        }

        if (_messages.Count == 0)
        {
            _messages.AddRange(LocalConversationStore.Load(Id, threadId));
        }

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

    /// <summary>
    /// Subscription usage from a <c>rate_limit_event</c> line. <c>unifiedWindows</c> carries every
    /// window (five_hour, seven_day, …) with utilization 0–1; older CLIs only name the window that
    /// is closest to its limit, which is still enough to show it when the limit is hit.
    /// </summary>
    internal static ProviderQuotaEvent? ParseRateLimitEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (GetString(root, "type") != "rate_limit_event" ||
                !root.TryGetProperty("rate_limit_info", out var info) ||
                info.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var rejected = GetString(info, "status") == "rejected";
            var windows = new List<ProviderQuotaWindow>();
            if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
            {
                foreach (var window in unified.EnumerateObject())
                {
                    if (window.Value.ValueKind != JsonValueKind.Object ||
                        !window.Value.TryGetProperty("utilization", out var utilization) ||
                        utilization.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    windows.Add(new ProviderQuotaWindow(
                        QuotaWindowId(window.Name),
                        Math.Clamp(utilization.GetDouble(), 0, 1),
                        FromUnixSeconds(GetLong(window.Value, "resetsAt"))));
                }
            }
            else if (rejected && GetString(info, "rateLimitType") is { Length: > 0 } type)
            {
                windows.Add(new ProviderQuotaWindow(QuotaWindowId(type), 1, FromUnixSeconds(GetLong(info, "resetsAt"))));
            }

            return windows.Count > 0 || rejected ? new ProviderQuotaEvent(windows, rejected) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string QuotaWindowId(string name) => name switch
    {
        "five_hour" => "session",
        "seven_day" => "weekly",
        _ => name
    };

    private static DateTimeOffset? FromUnixSeconds(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

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
