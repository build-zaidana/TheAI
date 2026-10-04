using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>One Claude Code hook call relayed to CodexBar (the hook's JSON input).</summary>
internal sealed record ClaudeHookEvent(string Name, string SessionId, string Cwd, JsonElement Data);

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
            var hook = new ClaudeHookEvent(
                Str(root, "hook_event_name") ?? string.Empty,
                Str(root, "session_id") ?? string.Empty,
                Str(root, "cwd") ?? string.Empty,
                root);
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
        var root = Load();
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var name in Events)
        {
            var list = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = list;
            RemoveOurs(list);
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

        Save(root);
    }

    public static void Uninstall()
    {
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        var root = Load();
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var name in hooks.Select(pair => pair.Key).ToArray())
            {
                if (hooks[name] is JsonArray list)
                {
                    RemoveOurs(list);
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

        Save(root);
    }

    // Bash (Claude Code runs hooks with Git Bash on Windows): post the hook input, never fail the
    // hook. A permission request waits for the notch; everything else returns at once.
    internal static string Command(bool waitForAnswer) => waitForAnswer
        ? $"curl -s -m {(int)ClaudeHookServer.PermissionWait.TotalSeconds + 5} -H 'X-CodexBar-Hook: 1' -H \"X-CodexBar-Child: ${{CODEXBAR_CHILD:-0}}\" -H 'Content-Type: application/json' --data-binary @- http://{Marker}/ 2>/dev/null || true"
        : $"curl -s -m 2 -o /dev/null -H 'X-CodexBar-Hook: 1' -H \"X-CodexBar-Child: ${{CODEXBAR_CHILD:-0}}\" -H 'Content-Type: application/json' --data-binary @- http://{Marker}/ 2>/dev/null || true";

    private static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?.ToJsonString().Contains(Marker, StringComparison.Ordinal) == true)
            {
                list.RemoveAt(i);
            }
        }
    }

    private static JsonObject Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(SettingsPath);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException("~/.claude/settings.json is not a JSON object.");
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var backup = SettingsPath + ".codexbar-backup";
        if (File.Exists(SettingsPath) && !File.Exists(backup))
        {
            File.Copy(SettingsPath, backup);
        }

        var temp = SettingsPath + ".codexbar-tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
