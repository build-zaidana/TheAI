using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>
/// One hook call relayed to CodexBar (the hook's JSON input). Gemini CLI's events arrive under
/// Claude Code's names (BeforeAgent → UserPromptSubmit, BeforeTool → PreToolUse, AfterAgent → Stop)
/// with <see cref="Agent"/> = "gemini".
/// </summary>
internal sealed record ClaudeHookEvent(string Name, string SessionId, string Cwd, JsonElement Data, string Agent = "claude");

/// <summary>
/// Receives Claude Code hook calls from every session on this PC (terminal, IDE, desktop) on a
/// loopback port. The hooks themselves are one-line <c>curl</c> commands that post the hook input
/// here and, for <c>PermissionRequest</c>, wait for the answer. Requests must carry the
/// <c>X-CodexBar-Hook</c> header, which a web page cannot add without a CORS preflight this server
/// never grants, and sessions CodexBar runs itself (tagged with <c>CODEXBAR_CHILD=1</c>) are skipped.
/// </summary>
internal sealed class ClaudeHookServer : IDisposable
{
    public const int Port = 47321;
    public static readonly TimeSpan PermissionWait = TimeSpan.FromSeconds(30);

    private HttpListener? _listener;
    private CancellationTokenSource? _stop;

    /// <summary>A non-blocking event (raised on a worker thread).</summary>
    public event Action<ClaudeHookEvent>? EventReceived;

    /// <summary>A permission request; the handler returns "allow", "deny", or null to leave it to Claude Code.</summary>
    public Func<ClaudeHookEvent, CancellationToken, Task<string?>>? PermissionHandler { get; set; }

    public bool IsRunning => _listener?.IsListening == true;

    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/hook/");
            _listener.Start();
            _stop = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _stop.Token));
            DiagnosticsLog.Info("ClaudeHooks", $"listening on 127.0.0.1:{Port}");
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ClaudeHooks", exception.Message);
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception)
        {
        }

        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        string? answer = null;
        try
        {
            var request = context.Request;
            if (request.HttpMethod != "POST" || request.Headers["X-CodexBar-Hook"] != "1")
            {
                context.Response.StatusCode = 403;
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            }

            if (request.Headers["X-CodexBar-Child"] == "1")
            {
                return; // CodexBar's own claude process: already shown by CodexBar
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();
            var gemini = request.Url?.AbsolutePath.StartsWith("/hook/gemini", StringComparison.Ordinal) == true;
            var name = Str(root, "hook_event_name") ?? string.Empty;
            var hook = new ClaudeHookEvent(
                gemini ? GeminiHooksInstaller.ClaudeName(name) : name,
                Str(root, "session_id") ?? string.Empty,
                Str(root, "cwd") ?? string.Empty,
                root,
                gemini ? "gemini" : "claude");
            if (hook.SessionId.Length == 0)
            {
                return;
            }

            if (hook.Name == "PermissionRequest" && PermissionHandler is { } handler)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(PermissionWait);
                try
                {
                    var behavior = await handler(hook, wait.Token).ConfigureAwait(false);
                    if (behavior is "allow" or "deny")
                    {
                        answer = JsonSerializer.Serialize(new
                        {
                            hookSpecificOutput = new
                            {
                                hookEventName = "PermissionRequest",
                                decision = behavior == "deny"
                                    ? (object)new { behavior, message = "Ditolak dari CodexBar." }
                                    : new { behavior }
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Nobody answered in the notch: Claude Code asks in its own UI as usual.
                }
            }
            else
            {
                EventReceived?.Invoke(hook);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ClaudeHooks", exception.Message);
        }
        finally
        {
            try
            {
                if (answer is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                }

                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Adds CodexBar's relay hooks to (and removes them from) the user's <c>~/.claude/settings.json</c>.
/// Everything else in the file is kept as is; a backup is written before the first change, and
/// CodexBar's entries are recognised by the relay URL in their command.
/// </summary>
internal static class ClaudeHooksInstaller
{
    private const string Marker = "127.0.0.1:47321/hook";

    private static readonly string[] Events =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd"];

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Install()
    {
        var root = LoadJson(SettingsPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var name in Events)
        {
            var list = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = list;
            RemoveOurs(list, Marker);
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Command(name == "PermissionRequest"),
                    ["timeout"] = name == "PermissionRequest" ? (int)ClaudeHookServer.PermissionWait.TotalSeconds + 10 : 5
                })
            };
            if (name is "PreToolUse" or "PermissionRequest")
            {
                entry["matcher"] = "*";
            }

            list.Add(entry);
        }

        SaveJson(SettingsPath, root);
    }

    public static void Uninstall() => RemoveFrom(SettingsPath, Marker);

    /// <summary>Takes the entries carrying <paramref name="marker"/> out of a settings file's "hooks".</summary>
    internal static void RemoveFrom(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var root = LoadJson(path);
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var name in hooks.Select(pair => pair.Key).ToArray())
            {
                if (hooks[name] is JsonArray list)
                {
                    RemoveOurs(list, marker);
                    if (list.Count == 0)
                    {
                        hooks.Remove(name);
                    }
                }
            }

            if (hooks.Count == 0)
            {
                root.Remove("hooks");
            }
        }

        SaveJson(path, root);
    }

    // Bash (Claude Code runs hooks with Git Bash on Windows): post the hook input, never fail the
    // hook. A permission request waits for the notch; everything else returns at once.
    internal static string Command(bool waitForAnswer) => waitForAnswer
        ? $"curl -s -m {(int)ClaudeHookServer.PermissionWait.TotalSeconds + 5} -H 'X-CodexBar-Hook: 1' -H \"X-CodexBar-Child: ${{CODEXBAR_CHILD:-0}}\" -H 'Content-Type: application/json' --data-binary @- http://{Marker}/ 2>/dev/null || true"
        : $"curl -s -m 2 -o /dev/null -H 'X-CodexBar-Hook: 1' -H \"X-CodexBar-Child: ${{CODEXBAR_CHILD:-0}}\" -H 'Content-Type: application/json' --data-binary @- http://{Marker}/ 2>/dev/null || true";

    internal static void RemoveOurs(JsonArray list, string marker)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?.ToJsonString().Contains(marker, StringComparison.Ordinal) == true)
            {
                list.RemoveAt(i);
            }
        }
    }

    internal static JsonObject LoadJson(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException($"{path} is not a JSON object.");
    }

    /// <summary>Writes the file atomically, keeping a backup of how it was before CodexBar first touched it.</summary>
    internal static void SaveJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".codexbar-backup";
        if (File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        var temp = path + ".codexbar-tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// The same relay for Gemini CLI, in <c>~/.gemini/settings.json</c>. Gemini CLI runs hook commands
/// with PowerShell on Windows, so the relay is a one-line <c>Invoke-RestMethod</c> that reads the hook
/// input from stdin, posts it to <c>/hook/gemini/</c> and prints nothing (stdout must stay empty or JSON).
/// Gemini's permission prompts can't be answered from outside, so the notch only shows them.
/// </summary>
internal static class GeminiHooksInstaller
{
    private const string Marker = "127.0.0.1:47321/hook/gemini";

    private static readonly string[] Events = ["SessionStart", "BeforeAgent", "BeforeTool", "AfterAgent", "Notification", "SessionEnd"];

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "settings.json");

    /// <summary>Gemini's event name → the Claude Code event CodexBar already handles.</summary>
    public static string ClaudeName(string gemini) => gemini switch
    {
        "BeforeAgent" => "UserPromptSubmit",
        "BeforeTool" => "PreToolUse",
        "AfterAgent" => "Stop",
        _ => gemini   // SessionStart, SessionEnd, Notification
    };

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string Command =>
        "try { $b = [Console]::In.ReadToEnd(); Invoke-RestMethod -Uri 'http://" + Marker + "/' -Method Post " +
        "-Headers @{ 'X-CodexBar-Hook' = '1' } -ContentType 'application/json; charset=utf-8' " +
        "-Body ([Text.Encoding]::UTF8.GetBytes($b)) -TimeoutSec 2 | Out-Null } catch { }";

    public static void Install()
    {
        var root = ClaudeHooksInstaller.LoadJson(SettingsPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var name in Events)
        {
            var list = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = list;
            ClaudeHooksInstaller.RemoveOurs(list, Marker);
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["name"] = "codexbar-relay",
                    ["type"] = "command",
                    ["command"] = Command,
                    ["timeout"] = 5000,
                    ["description"] = "Shows this session in the CodexBar notch"
                })
            };
            if (name == "BeforeTool")
            {
                entry["matcher"] = ".*";
            }

            list.Add(entry);
        }

        ClaudeHooksInstaller.SaveJson(SettingsPath, root);
    }

    public static void Uninstall() => ClaudeHooksInstaller.RemoveFrom(SettingsPath, Marker);
}
