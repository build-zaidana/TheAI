using System.IO;
using System.Text.Json;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Providers.ClaudeCode;

namespace CodexBar.App;

// Claude Code sessions from anywhere on this PC. Live: the relay hooks report every terminal,
// IDE and desktop session to the notch as its own activity ("cc:<session id>"): the prompt, each
// tool step, the final answer, and permission requests the notch can answer (Claude Code asks in
// its own UI if nobody answers within 30 s). History: past sessions are read from
// ~/.claude/projects and listed under "Lanjutkan" next to CodexBar's own threads.
public partial class MainWindow
{
    private const string ExternalKeyPrefix = "cc:";

    private readonly ClaudeHookServer _hookServer = new();
    private readonly Dictionary<string, ExternalSession> _externalSessions = new(StringComparer.Ordinal);

    private sealed class ExternalSession(string id, string cwd)
    {
        public string Id { get; } = id;
        public string Key => ExternalKeyPrefix + Id;
        public string Cwd { get; set; } = cwd;
        public string Source { get; set; } = "Claude Code";
        public string Project => Cwd.Length > 0 ? new DirectoryInfo(Cwd).Name : "Claude Code";
        public bool Started { get; set; }
        public bool Busy { get; set; }
        public DateTimeOffset TurnStarted { get; set; } = DateTimeOffset.Now;
        public string Answer { get; set; } = string.Empty;
    }

    // CODEXBAR_TEST_CLAUDE_SYNC=1: receive hook calls without installing anything (for a test
    // project whose own .claude/settings.json carries the relay hooks); never saved.
    private readonly bool _claudeLiveTest = Environment.GetEnvironmentVariable("CODEXBAR_TEST_CLAUDE_SYNC") is { Length: > 0 };

    private bool ClaudeLiveActive => _settings.ClaudeLiveSessions || _claudeLiveTest;

    private static bool IsExternalKey(string key) => key.StartsWith(ExternalKeyPrefix, StringComparison.Ordinal);

    private void StartClaudeSync()
    {
        // CODEXBAR_TEST_CLAUDE_LIST=1: log the sessions "Lanjutkan" would list (no UI).
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_CLAUDE_LIST") is { Length: > 0 })
        {
            _ = Task.Run(() =>
            {
                var started = DateTime.UtcNow;
                var sessions = ClaudeSessionFiles.List(40);
                DiagnosticsLog.Info("ClaudeSessions", $"listed {sessions.Count} in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
                foreach (var session in sessions.Take(8))
                {
                    DiagnosticsLog.Info("ClaudeSessions", $"{session.Source} | {new DirectoryInfo(session.Cwd).Name} | {session.Title} | {session.UpdatedAt:g}");
                }

                started = DateTime.UtcNow;
                ClaudeSessionFiles.List(40);
                DiagnosticsLog.Info("ClaudeSessions", $"cached listing in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            });
        }

        _hookServer.EventReceived += hook => Dispatcher.BeginInvoke(() => HandleHook(hook));
        _hookServer.PermissionHandler = (hook, token) => Dispatcher.Invoke(() => AskPermissionAsync(hook, token));
        ApplyClaudeLive();
    }

    private void ApplyClaudeLive()
    {
        if (_settings.ClaudeLiveSessions && ClaudeHooksInstaller.IsInstalled() || _claudeLiveTest)
        {
            _hookServer.Start();
        }
        else
        {
            _hookServer.Stop();
        }
    }

    (bool Ok, string Message) ISettingsHost.SetClaudeLiveSessions(bool on)
    {
        try
        {
            if (on)
            {
                ClaudeHooksInstaller.Install();
            }
            else
            {
                ClaudeHooksInstaller.Uninstall();
            }

            _settings.ClaudeLiveSessions = on;
            _ = SaveSettingsSafeAsync();
            ApplyClaudeLive();
            if (on && !_hookServer.IsRunning)
            {
                return (false, LF("claudeSync.portBusy", ClaudeHookServer.Port));
            }

            return (true, on ? L("claudeSync.installed") : L("claudeSync.removed"));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ClaudeHooks", exception.Message);
            return (false, exception.Message);
        }
    }

    // ---- Live events -------------------------------------------------------------------------

    private ExternalSession Session(ClaudeHookEvent hook)
    {
        if (!_externalSessions.TryGetValue(hook.SessionId, out var session))
        {
            session = new ExternalSession(hook.SessionId, hook.Cwd);
            if (ClaudeHookServer.Str(hook.Data, "transcript_path") is { } transcript &&
                ClaudeSessionFiles.ReadEntrypoint(transcript) is { } entrypoint)
            {
                session.Source = ClaudeSessionFiles.SourceLabel(entrypoint);
            }

            _externalSessions[hook.SessionId] = session;
        }
        else if (hook.Cwd.Length > 0)
        {
            session.Cwd = hook.Cwd;
        }

        return session;
    }

    private void EnsureActivity(ExternalSession session, string prompt)
    {
        if (session.Started)
        {
            return;
        }

        session.Started = true;
        session.Busy = true;
        session.TurnStarted = DateTimeOffset.Now;
        _notch?.BeginActivity(session.Key, prompt, "Claude", $"{session.Project} · {session.Source}");
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_CLAUDE_SYNC") == "open")
        {
            _notch?.PreviewAgent(session.Key);
        }
    }

    private void HandleHook(ClaudeHookEvent hook)
    {
        if (!ClaudeLiveActive)
        {
            return;
        }

        var session = Session(hook);
        if (session.Source == "Claude Code" &&
            ClaudeHookServer.Str(hook.Data, "transcript_path") is { } transcript &&
            ClaudeSessionFiles.ReadEntrypoint(transcript) is { } entrypoint)
        {
            // The transcript only has its first lines once the first prompt is written.
            session.Source = ClaudeSessionFiles.SourceLabel(entrypoint);
        }

        switch (hook.Name)
        {
            case "UserPromptSubmit":
                session.Started = false;
                session.Answer = string.Empty;
                EnsureActivity(session, ClaudeHookServer.Str(hook.Data, "prompt")?.Trim() ?? string.Empty);
                break;

            case "PreToolUse":
                EnsureActivity(session, L("claudeSync.running"));
                session.Busy = true;
                _notch?.HandleProviderEvent(new ProviderStatusEvent(DescribeTool(hook.Data)), session.Key);
                break;

            case "Notification" when ClaudeHookServer.Str(hook.Data, "message") is { Length: > 0 } message && session.Started:
                _notch?.HandleProviderEvent(new ProviderStatusEvent(message), session.Key);
                break;

            case "Stop":
                if (!session.Started)
                {
                    break;
                }

                session.Busy = false;
                session.Started = false;
                session.Answer = ClaudeHookServer.Str(hook.Data, "last_assistant_message") ?? string.Empty;
                if (session.Answer.Length > 0)
                {
                    _notch?.HandleProviderEvent(new ProviderTextDeltaEvent(session.Answer), session.Key);
                }

                _notch?.HandleProviderEvent(new ProviderCompletedEvent("completed"), session.Key);
                NotifyCompletion(session.Key, "Claude", session.Project, session.Answer, DateTimeOffset.Now - session.TurnStarted);
                break;

            case "SessionEnd":
                // Keep the last answer readable for a while; then the page goes away.
                _externalSessions.Remove(hook.SessionId);
                var key = session.Key;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!_externalSessions.ContainsKey(hook.SessionId))
                    {
                        _notch?.RemoveActivity(key);
                    }
                };
                timer.Start();
                break;
        }
    }

    // A permission prompt from a session elsewhere, answered in the notch like CodexBar's own.
    private async Task<string?> AskPermissionAsync(ClaudeHookEvent hook, CancellationToken token)
    {
        if (!ClaudeLiveActive)
        {
            return null;
        }

        var session = Session(hook);
        EnsureActivity(session, L("claudeSync.running"));
        var tool = ClaudeHookServer.Str(hook.Data, "tool_name") ?? "tool";
        var input = hook.Data.TryGetProperty("tool_input", out var value) ? value : default;
        var (method, summary) = tool switch
        {
            "Bash" or "PowerShell" => ("item/commandExecution/requestApproval", string.Join('\n',
                L("claudeSync.askCommand"), string.Empty, Str(input, "command") ?? string.Empty, LF("claudeSync.folder", session.Cwd))),
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ("item/fileChange/requestApproval", string.Join('\n',
                L("claudeSync.askFile"), string.Empty, Str(input, "file_path") ?? Str(input, "notebook_path") ?? string.Empty)),
            _ => ("item/permissions/requestApproval", string.Join('\n',
                LF("claudeSync.askTool", tool), string.Empty, input.ValueKind == JsonValueKind.Undefined ? string.Empty : Shorten(input.ToString(), 400)))
        };

        var decision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => decision.TrySetCanceled());
        var approval = new ProviderApprovalEvent(
            method,
            summary,
            answer =>
            {
                decision.TrySetResult(answer switch
                {
                    ProviderApprovalDecision.AcceptOnce or ProviderApprovalDecision.AcceptForSession => "allow",
                    ProviderApprovalDecision.Decline => "deny",
                    _ => null
                });
                return Task.CompletedTask;
            },
            hook.Data,
            () => !token.IsCancellationRequested && !decision.Task.IsCompleted);

        _notch?.HandleProviderEvent(approval, session.Key);
        _ = ShowApprovalAsync(approval, null, session.Key);
        try
        {
            return await decision.Task;
        }
        finally
        {
            if (session.Started)
            {
                // Answered here, or handed back to Claude Code's own prompt.
                _notch?.HandleProviderEvent(new ProviderStatusEvent(decision.Task.IsCompletedSuccessfully
                    ? L("claudeSync.answered")
                    : LF("claudeSync.handedBack", session.Source)), session.Key);
            }
        }
    }

    private static string DescribeTool(JsonElement data)
    {
        var tool = ClaudeHookServer.Str(data, "tool_name") ?? "tool";
        var input = data.TryGetProperty("tool_input", out var value) ? value : default;
        var detail = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Read" or "Edit" or "Write" or "MultiEdit" => Str(input, "file_path") is { } path ? Path.GetFileName(path) : null,
            "Grep" or "Glob" => Str(input, "pattern"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Task" or "Agent" => Str(input, "description"),
            _ => null
        };
        return detail is { Length: > 0 } ? $"{tool}: {Shorten(detail.ReplaceLineEndings(" "), 90)}" : tool;
    }

    private static string? Str(JsonElement element, string name) => ClaudeHookServer.Str(element, name);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // ---- Notch actions on external sessions -------------------------------------------------

    private void DismissExternal(string key)
    {
        var id = key[ExternalKeyPrefix.Length..];
        if (_externalSessions.TryGetValue(id, out var session) && !session.Busy)
        {
            session.Started = false;
        }
    }

    private async Task OpenExternalInWindowAsync(string key)
    {
        var id = key[ExternalKeyPrefix.Length..];
        if (_externalSessions.TryGetValue(id, out var running) && running.Busy)
        {
            throw new InvalidOperationException(LF("claudeSync.stillRunning", running.Source));
        }

        if (_controller.ProviderId != "claude-code")
        {
            throw new InvalidOperationException(L("claudeSync.needClaude"));
        }

        var info = await Task.Run(() => ClaudeSessionFiles.Find(id))
            ?? throw new InvalidOperationException(L("claudeSync.notFound"));
        _notch?.RemoveActivity(key);
        ShowFromExternalActivation();
        await OpenThreadFromSidebarAsync(new ProviderThreadSummary(info.SessionId, info.Title, string.Empty, info.Cwd, info.UpdatedAt, Source: info.Source));
    }

    // ---- History in the launcher --------------------------------------------------------------

    private async Task<IReadOnlyList<ProviderThreadSummary>> WithClaudeSessionsAsync(IReadOnlyList<ProviderThreadSummary> own)
    {
        if (_controller.ProviderId != "claude-code" || !_settings.ClaudeImportSessions)
        {
            return own;
        }

        IReadOnlyList<ClaudeSessionInfo> sessions;
        try
        {
            sessions = await Task.Run(() => ClaudeSessionFiles.List(40));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ClaudeSessions", exception.Message);
            return own;
        }

        var ownIds = own.Select(thread => thread.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return own.Select(thread => thread with { Source = thread.Source ?? "CodexBar" })
            .Concat(sessions.Where(s => !ownIds.Contains(s.SessionId)).Select(s =>
                new ProviderThreadSummary(s.SessionId, s.Title, string.Empty, s.Cwd, s.UpdatedAt, "idle", null, s.Source)))
            .OrderByDescending(thread => thread.UpdatedAt)
            .Take(30)
            .ToArray();
    }
}
