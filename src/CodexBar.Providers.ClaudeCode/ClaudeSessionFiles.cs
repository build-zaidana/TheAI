using System.Text;
using System.Text.Json;
using CodexBar.Providers;

namespace CodexBar.Providers.ClaudeCode;

/// <summary>A Claude Code session found on disk (terminal, IDE, desktop app, or CodexBar itself).</summary>
public sealed record ClaudeSessionInfo(
    string SessionId,
    string FilePath,
    string Cwd,
    string Entrypoint,
    string Title,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Where the session was started, as a short label.</summary>
    public string Source => ClaudeSessionFiles.SourceLabel(Entrypoint);
}

/// <summary>
/// Reads the session transcripts Claude Code keeps under <c>~/.claude/projects/&lt;project&gt;/&lt;id&gt;.jsonl</c>,
/// so sessions started anywhere can be listed and continued from CodexBar. Files can be large
/// (tool output), so a summary only parses the few lines it needs and is cached by size and
/// write time.
/// </summary>
public static class ClaudeSessionFiles
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, DateTime Written, ClaudeSessionInfo? Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string ProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string SourceLabel(string entrypoint) => entrypoint switch
    {
        "cli" => "Terminal",
        "claude-vscode" => "VS Code",
        "claude-desktop" => "Desktop",
        _ when entrypoint.StartsWith("sdk", StringComparison.Ordinal) => "SDK",
        "" => "Claude Code",
        _ => entrypoint
    };

    /// <summary>Newest sessions first. Sessions in the temp folder (scripts, tests) are left out.</summary>
    public static IReadOnlyList<ClaudeSessionInfo> List(int limit = 50)
    {
        if (!Directory.Exists(ProjectsRoot))
        {
            return [];
        }

        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        var files = Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => Path.GetDirectoryName(Path.GetDirectoryName(file.FullName)) is { } parent &&
                           string.Equals(parent, ProjectsRoot, StringComparison.OrdinalIgnoreCase)) // not subagent logs
            .OrderByDescending(file => file.LastWriteTimeUtc);

        var result = new List<ClaudeSessionInfo>();
        foreach (var file in files)
        {
            var info = Summary(file);
            if (info is null || info.Cwd.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(info);
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    public static string? FindFile(string sessionId)
    {
        if (!Directory.Exists(ProjectsRoot) || !Guid.TryParse(sessionId, out _))
        {
            return null;
        }

        return Directory.EnumerateFiles(ProjectsRoot, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>The session's entrypoint (cli, claude-vscode, claude-desktop, sdk-cli…) from its first lines.</summary>
    public static string? ReadEntrypoint(string filePath)
    {
        try
        {
            var count = 0;
            foreach (var line in ReadLines(filePath))
            {
                if (line.Contains("\"entrypoint\"", StringComparison.Ordinal) && TryRead(line, out var document))
                {
                    using (document)
                    {
                        return Str(document.RootElement, "entrypoint");
                    }
                }

                if (++count > 200)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    public static ClaudeSessionInfo? Find(string sessionId) =>
        FindFile(sessionId) is { } path ? Summary(new FileInfo(path)) : null;

    /// <summary>
    /// The conversation as user / assistant messages. Each agent turn keeps the work before its
    /// answer (tool calls, text in between, failures) as steps; the text after the last tool call
    /// is the answer.
    /// </summary>
    public static IReadOnlyList<ProviderThreadMessage> ReadMessages(string filePath)
    {
        var messages = new List<ProviderThreadMessage>();
        var parts = new List<(ProviderActivityStep Step, string? ToolId)>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        string? turnEnd = null;

        // A session file can hold branches (e.g. a prompt sent from CodexBar while Claude Code
        // kept going): show only the latest one, from its root to its newest message.
        var all = ReadLines(filePath).ToList();
        var lines = LatestBranch(all) ?? all;

        void FlushAssistant()
        {
            if (parts.Count == 0)
            {
                return;
            }

            var (steps, answer) = SplitTurn(parts, failed);
            messages.Add(new ProviderThreadMessage("assistant", answer, steps.Count > 0 ? steps : null, Id: turnEnd));
            parts.Clear();
            failed.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Contains("\"subtype\":\"compact_boundary\"", StringComparison.Ordinal))
            {
                // As in Claude Code: a marker where the conversation was summarized.
                FlushAssistant();
                messages.Add(new ProviderThreadMessage("assistant", string.Empty,
                    [new ProviderActivityStep("note", "Conversation compacted", "*Conversation compacted*", IsText: true)]));
                continue;
            }

            var uuid = MessageUuid(line);

            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
            {
                var images = line.Contains("\"type\":\"image\"", StringComparison.Ordinal) ? HumanImages(line) : [];
                if (HumanPrompt(line) is { } prompt)
                {
                    FlushAssistant();
                    messages.Add(new ProviderThreadMessage("user", prompt, Images: images.Count > 0 ? images : null, Id: uuid));
                }
                else if (images.Count > 0)
                {
                    FlushAssistant();
                    messages.Add(new ProviderThreadMessage("user", string.Empty, Images: images, Id: uuid));
                }
                else if (line.Contains("\"is_error\":true", StringComparison.Ordinal))
                {
                    foreach (var id in FailedToolIds(line))
                    {
                        failed.Add(id);
                    }
                }
            }
            else if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
            {
                var added = AssistantParts(line).ToArray();
                parts.AddRange(added);
                if (added.Length > 0 && uuid is not null)
                {
                    turnEnd = uuid; // forking from this answer keeps everything up to here
                }
            }
        }

        FlushAssistant();
        return messages;
    }

    // Top-level uuid of a user / assistant line (null for other lines).
    private static string? MessageUuid(string line)
    {
        if (!line.Contains("\"uuid\"", StringComparison.Ordinal) ||
            !(line.Contains("\"type\":\"user\"", StringComparison.Ordinal) || line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) ||
            !TryRead(line, out var document))
        {
            return null;
        }

        using (document)
        {
            return Str(document.RootElement, "type") is "user" or "assistant" ? Str(document.RootElement, "uuid") : null;
        }
    }

    private sealed record Link(string Uuid, string? Parent, string Line, bool Sidechain);

    // Every line with a uuid is a link in the conversation chain (user, assistant, attachment,
    // system…); logicalParentUuid bridges a compaction.
    private static List<Link> Links(IReadOnlyList<string> lines)
    {
        var links = new List<Link>();
        foreach (var line in lines)
        {
            if (!line.Contains("\"uuid\"", StringComparison.Ordinal) || !TryRead(line, out var document))
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (Str(root, "uuid") is { Length: > 0 } uuid)
                {
                    links.Add(new Link(uuid, Str(root, "parentUuid") ?? Str(root, "logicalParentUuid"), line,
                        root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True));
                }
            }
        }

        return links;
    }

    // The lines from the root to <paramref name="tip"/>, in conversation order.
    private static List<string> Chain(IReadOnlyList<Link> links, string tip)
    {
        var byUuid = new Dictionary<string, Link>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            byUuid[link.Uuid] = link;
        }

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var at = (string?)tip; at is not null && seen.Add(at) && byUuid.TryGetValue(at, out var link); at = link.Parent)
        {
            chain.Add(link.Line);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The latest branch of the conversation, root first: the newest line no other line answers
    /// (a file can hold branches, e.g. a prompt sent from CodexBar while Claude Code went on).
    /// Lines are not always written in conversation order, so the chain decides it. Null when the
    /// file has no uuids (older format: read it in file order).
    /// </summary>
    private static List<string>? LatestBranch(IReadOnlyList<string> lines)
    {
        var links = Links(lines).Where(link => !link.Sidechain).ToList();
        if (!links.Any(link => MessageUuid(link.Line) is not null))
        {
            return null;
        }

        var parents = links.Select(link => link.Parent).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var tip = links.LastOrDefault(link => !parents.Contains(link.Uuid)) ?? links[^1];
        return Chain(links, tip.Uuid);
    }

    /// <summary>
    /// A new session holding this one's conversation up to <paramref name="uptoUuid"/> (that
    /// message and everything before it on its branch), so it can go on differently. Returns the
    /// new session id.
    /// </summary>
    public static string Fork(string filePath, string uptoUuid, string title)
    {
        var oldId = Path.GetFileNameWithoutExtension(filePath);
        var newId = Guid.NewGuid().ToString();
        var output = Chain(Links(ReadLines(filePath).ToList()), uptoUuid)
            .Select(line => line.Replace($"\"sessionId\":\"{oldId}\"", $"\"sessionId\":\"{newId}\"", StringComparison.Ordinal))
            .ToList();

        // The fork's title line, written by hand (no reflection-based serializer needed).
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "custom-title");
                writer.WriteString("customTitle", title);
                writer.WriteString("sessionId", newId);
                writer.WriteEndObject();
            }

            output.Add(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(filePath)!, newId + ".jsonl"), output);
        return newId;
    }

    /// <summary>
    /// Tokens the conversation fills in the model's context: the prompt side of the latest
    /// request (input + cache read + cache write) plus its output. 0 when unknown.
    /// </summary>
    public static long ContextTokens(string assistantLine)
    {
        if (!assistantLine.Contains("\"usage\"", StringComparison.Ordinal) || !TryRead(assistantLine, out var document))
        {
            return 0;
        }

        using (document)
        {
            var root = document.RootElement;
            if (Str(root, "type") != "assistant" ||
                root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) || !message.TryGetProperty("usage", out var usage))
            {
                return 0;
            }

            long Get(string name) => usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
            return Get("input_tokens") + Get("cache_read_input_tokens") + Get("cache_creation_input_tokens") + Get("output_tokens");
        }
    }

    /// <summary>The context in use at the end of a session (see <see cref="ContextTokens"/>).</summary>
    public static long LastContextTokens(string filePath)
    {
        long last = 0;
        foreach (var line in LatestBranch(ReadLines(filePath).ToList()) ?? [])
        {
            if (line.Contains("\"subtype\":\"compact_boundary\"", StringComparison.Ordinal))
            {
                last = 0; // smaller now, but by how much shows with the next answer
            }
            else if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && ContextTokens(line) is > 0 and var tokens)
            {
                last = tokens;
            }
        }

        return last;
    }

    /// <summary>Claude's context window: 200K, or 1M for the long-context models (or past 200K).</summary>
    public static long ContextWindow(string? model, long tokens) =>
        tokens > 200_000 || model?.Contains("[1m]", StringComparison.OrdinalIgnoreCase) == true ? 1_000_000 : 200_000;

    /// <summary>
    /// True when the session was written by Claude Code itself (terminal, IDE, desktop) in the last
    /// <paramref name="within"/>: it is probably open there, and a prompt sent from CodexBar
    /// becomes a branch that app won't see.
    /// </summary>
    public static bool ActiveElsewhere(string sessionId, TimeSpan within)
    {
        if (FindFile(sessionId) is not { } path)
        {
            return false;
        }

        var file = new FileInfo(path);
        if (DateTime.UtcNow - file.LastWriteTimeUtc > within)
        {
            return false;
        }

        string? last = null;
        foreach (var line in ReadLines(path))
        {
            if (line.Contains("\"entrypoint\"", StringComparison.Ordinal) && Field(line, "entrypoint") is { } entry)
            {
                last = entry;
            }
        }

        return last is not null and not "sdk-cli";
    }

    internal static (IReadOnlyList<ProviderActivityStep> Steps, string Answer) SplitTurn(
        IReadOnlyList<(ProviderActivityStep Step, string? ToolId)> parts, IReadOnlySet<string> failed) =>
        ProviderActivity.SplitTurn(parts, failed);

    // Text and tool_use blocks of one assistant line, in order.
    internal static IEnumerable<(ProviderActivityStep Step, string? ToolId)> AssistantParts(string line)
    {
        if (!TryRead(line, out var document))
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parts = new List<(ProviderActivityStep, string?)>();
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "thinking" when (Str(block, "thinking") ?? string.Empty).Trim() is { Length: > 0 } note:
                        // Claude Code keeps its short progress notes to the person here.
                        parts.Add((new ProviderActivityStep("note", OneLine(note, 160), note, IsText: true), null));
                        break;
                    case "text" when (Str(block, "text") ?? string.Empty).Trim() is { Length: > 0 } text:
                        parts.Add((new ProviderActivityStep(string.Empty, OneLine(text, 160), text, IsText: true), null));
                        break;
                    case "tool_use":
                        parts.Add((DescribeTool(Str(block, "name") ?? "Tool", block.TryGetProperty("input", out var input) ? input : default), Str(block, "id")));
                        break;
                }
            }

            return parts;
        }
    }

    /// <summary>Where pasted images from transcripts are kept (one file per image, by content).</summary>
    public static string ImageCacheFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "cache", "images");

    // Images the person pasted into a prompt, written once to the cache so the chat can show them.
    private static IReadOnlyList<string> HumanImages(string line)
    {
        if (!TryRead(line, out var document))
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var paths = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "image" || !block.TryGetProperty("source", out var source) ||
                    Str(source, "type") != "base64" || Str(source, "data") is not { Length: > 0 } data)
                {
                    continue;
                }

                var extension = (Str(source, "media_type") ?? "image/png") switch
                {
                    "image/jpeg" => ".jpg",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    _ => ".png"
                };
                try
                {
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(data)))[..20];
                    var path = Path.Combine(ImageCacheFolder, hash + extension);
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(ImageCacheFolder);
                        File.WriteAllBytes(path, Convert.FromBase64String(data));
                    }

                    paths.Add(path);
                }
                catch (Exception)
                {
                    // A broken image is left out; the prompt text still shows.
                }
            }

            return paths;
        }
    }

    internal static IEnumerable<string> FailedToolIds(string line)
    {
        if (!TryRead(line, out var document))
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return content.EnumerateArray()
                .Where(block => Str(block, "type") == "tool_result" &&
                                block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
                .Select(block => Str(block, "tool_use_id"))
                .OfType<string>()
                .ToArray();
        }
    }

    private static int LineCount(string text) => ProviderActivity.LineCount(text);

    internal static (int Added, int Removed) LineDelta(string oldText, string newText) => ProviderActivity.LineDelta(oldText, newText);

    // How Claude Code names a tool call: "Edited Foo.cs", "Ran a command", "Read bar.png"…
    private static ProviderActivityStep DescribeTool(string name, JsonElement input)
    {
        string Arg(string key) => input.ValueKind == JsonValueKind.Object ? Str(input, key) ?? string.Empty : string.Empty;
        string FileName(string key) => Arg(key) is { Length: > 0 } path ? Path.GetFileName(path.TrimEnd('/', '\\')) : string.Empty;

        var (added, removed) = name switch
        {
            "Edit" => LineDelta(Arg("old_string"), Arg("new_string")),
            "MultiEdit" when input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits) &&
                             edits.ValueKind == JsonValueKind.Array =>
                edits.EnumerateArray()
                    .Select(e => LineDelta(Str(e, "old_string") ?? string.Empty, Str(e, "new_string") ?? string.Empty))
                    .Aggregate((0, 0), (sum, d) => (sum.Item1 + d.Item1, sum.Item2 + d.Item2)),
            "Write" => (LineCount(Arg("content")), 0),
            _ => (0, 0)
        };

        // The replacements, so the turn can be undone.
        IReadOnlyList<ProviderTextEdit>? replaced = name switch
        {
            "Edit" => [new ProviderTextEdit(Arg("old_string"), Arg("new_string"),
                input.ValueKind == JsonValueKind.Object && input.TryGetProperty("replace_all", out var all) && all.ValueKind == JsonValueKind.True)],
            "MultiEdit" when input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var list) &&
                             list.ValueKind == JsonValueKind.Array =>
                list.EnumerateArray()
                    .Select(e => new ProviderTextEdit(Str(e, "old_string") ?? string.Empty, Str(e, "new_string") ?? string.Empty,
                        e.TryGetProperty("replace_all", out var each) && each.ValueKind == JsonValueKind.True))
                    .ToArray(),
            _ => null
        };

        return name switch
        {
            "Edit" or "MultiEdit" => new("Edited", FileName("file_path"), Arg("file_path"), Added: added, Removed: removed, Edits: replaced),
            "Write" => new("Wrote", FileName("file_path"), Arg("file_path"), Added: added, Removed: removed),
            "NotebookEdit" => new("Edited", FileName("notebook_path"), Arg("notebook_path")),
            "Read" => new("Read", FileName("file_path"), Arg("file_path")),
            "Bash" or "PowerShell" => new("Ran", "a command", Arg("command")),
            "Grep" => new("Searched", Arg("pattern"), Arg("path")),
            "Glob" => new("Found files", Arg("pattern"), Arg("path")),
            "WebFetch" => new("Fetched", Arg("url")),
            "WebSearch" => new("Searched the web", Arg("query")),
            "Task" or "Agent" => new("Ran agent", Arg("description"), Arg("prompt")),
            "TodoWrite" => new("Updated", "todos"),
            "Skill" => new("Used skill", Arg("skill")),
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) => new("Used", name.Split("__").Last()),
            _ => new("Used", name)
        };
    }

    /// <summary>
    /// The conversation as exchanges: each human prompt with the agent's text answer and whether
    /// the agent edited files while answering (Edit / Write / MultiEdit / NotebookEdit).
    /// </summary>
    public static IReadOnlyList<(string Prompt, string Answer, bool ChangedFiles)> ReadExchanges(string filePath)
    {
        var exchanges = new List<(string, string, bool)>();
        string? prompt = null;
        var answer = new StringBuilder();
        var changed = false;

        void Flush()
        {
            if (prompt is not null)
            {
                exchanges.Add((prompt, answer.ToString().Trim(), changed));
            }

            answer.Clear();
            changed = false;
        }

        foreach (var line in ReadLines(filePath))
        {
            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && HumanPrompt(line) is { } next)
            {
                Flush();
                prompt = next;
            }
            else if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
            {
                if (AssistantText(line) is { } text)
                {
                    answer.Append(answer.Length > 0 ? "\n\n" : string.Empty).Append(text);
                }

                if (line.Contains("\"name\":\"Edit\"", StringComparison.Ordinal) ||
                    line.Contains("\"name\":\"Write\"", StringComparison.Ordinal) ||
                    line.Contains("\"name\":\"MultiEdit\"", StringComparison.Ordinal) ||
                    line.Contains("\"name\":\"NotebookEdit\"", StringComparison.Ordinal))
                {
                    changed = true;
                }
            }
        }

        Flush();
        return exchanges;
    }

    // ---- Summary -----------------------------------------------------------------------------

    private static ClaudeSessionInfo? Summary(FileInfo file)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length && cached.Written == file.LastWriteTimeUtc)
            {
                return cached.Info;
            }
        }

        ClaudeSessionInfo? info = null;
        try
        {
            info = Parse(file);
        }
        catch (Exception)
        {
            // Being written, or unreadable: try again next time.
        }

        lock (Gate)
        {
            Cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, info);
        }

        return info;
    }

    internal static ClaudeSessionInfo? Parse(FileInfo file)
    {
        string? cwd = null, entrypoint = null, firstPrompt = null, customTitle = null, aiTitle = null;
        foreach (var line in ReadLines(file.FullName))
        {
            if (line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal))
            {
                customTitle = Field(line, "customTitle") ?? customTitle;
            }
            else if (line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal))
            {
                aiTitle = Field(line, "aiTitle") ?? aiTitle;
            }
            else if (firstPrompt is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
            {
                if (cwd is null && TryRead(line, out var root))
                {
                    using (root)
                    {
                        cwd = Str(root.RootElement, "cwd");
                        entrypoint = Str(root.RootElement, "entrypoint");
                    }
                }

                firstPrompt = HumanPrompt(line);
            }
        }

        if (cwd is null || firstPrompt is null)
        {
            return null; // no conversation in it
        }

        var title = customTitle ?? aiTitle ?? OneLine(firstPrompt, 90);
        return new ClaudeSessionInfo(
            Path.GetFileNameWithoutExtension(file.Name), file.FullName, cwd, entrypoint ?? string.Empty, title,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    // A prompt the person typed: plain text content (or text blocks), not tool results, not
    // meta/system injections or slash-command echoes.
    private static string? HumanPrompt(string line)
    {
        if (!TryRead(line, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (Str(root, "type") != "user" ||
                root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("isCompactSummary", out var summary) && summary.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            string? text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                    .Where(block => Str(block, "type") == "text")
                    .Select(block => Str(block, "text") ?? string.Empty)),
                _ => null
            };
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith('<'))
            {
                return null; // <command-name>, <local-command-stdout>, system reminders…
            }

            return text;
        }
    }

    private static string? AssistantText(string line)
    {
        if (!TryRead(line, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (Str(root, "type") != "assistant" ||
                root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var text = string.Join("\n", content.EnumerateArray()
                .Where(block => Str(block, "type") == "text")
                .Select(block => Str(block, "text") ?? string.Empty)).Trim();
            return text.Length > 0 ? text : null;
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static bool TryRead(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string? Field(string line, string name)
    {
        if (!TryRead(line, out var document))
        {
            return null;
        }

        using (document)
        {
            return Str(document.RootElement, name) is { Length: > 0 } value ? value : null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }
}
