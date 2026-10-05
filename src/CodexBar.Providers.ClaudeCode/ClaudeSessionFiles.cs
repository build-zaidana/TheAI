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

        void FlushAssistant()
        {
            if (parts.Count == 0)
            {
                return;
            }

            var (steps, answer) = SplitTurn(parts, failed);
            messages.Add(new ProviderThreadMessage("assistant", answer, steps.Count > 0 ? steps : null));
            parts.Clear();
            failed.Clear();
        }

        foreach (var line in ReadLines(filePath))
        {
            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
            {
                var images = line.Contains("\"type\":\"image\"", StringComparison.Ordinal) ? HumanImages(line) : [];
                if (HumanPrompt(line) is { } prompt)
                {
                    FlushAssistant();
                    messages.Add(new ProviderThreadMessage("user", prompt, Images: images.Count > 0 ? images : null));
                }
                else if (images.Count > 0)
                {
                    FlushAssistant();
                    messages.Add(new ProviderThreadMessage("user", string.Empty, Images: images));
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
                parts.AddRange(AssistantParts(line));
            }
        }

        FlushAssistant();
        return messages;
    }

    /// <summary>
    /// A turn's parts as Claude Code shows them: everything up to the last tool call is work
    /// (steps, failures marked), the text after it is the answer.
    /// </summary>
    internal static (IReadOnlyList<ProviderActivityStep> Steps, string Answer) SplitTurn(
        IReadOnlyList<(ProviderActivityStep Step, string? ToolId)> parts, IReadOnlySet<string> failed)
    {
        var lastTool = -1;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (!parts[i].Step.IsText || parts[i].Step.IsNote)
            {
                lastTool = i;
                break;
            }
        }

        var answer = string.Join("\n\n", parts.Skip(lastTool + 1).Select(part => part.Step.Detail)).Trim();
        var steps = parts.Take(lastTool + 1)
            .Select(part => part.ToolId is not null && failed.Contains(part.ToolId) ? part.Step with { Failed = true } : part.Step)
            .ToArray();
        return (steps, answer);
    }

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

    private static int LineCount(string text) => text.Length == 0 ? 0 : text.TrimEnd('\n').Split('\n').Length;

    // Lines removed / added by replacing old with new (lines both share at the ends don't count).
    internal static (int Added, int Removed) LineDelta(string oldText, string newText)
    {
        var before = oldText.Length == 0 ? [] : oldText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var after = newText.Length == 0 ? [] : newText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        var end = 0;
        while (end < before.Length - start && end < after.Length - start &&
               before[before.Length - 1 - end] == after[after.Length - 1 - end])
        {
            end++;
        }

        // In between: lines kept are the longest common subsequence (as a diff counts them).
        var m = before.Length - start - end;
        var n = after.Length - start - end;
        if (m == 0 || n == 0 || (long)m * n > 4_000_000)
        {
            return (n, m);
        }

        var row = new int[n + 1];
        for (var i = 0; i < m; i++)
        {
            var diagonal = 0;
            for (var j = 0; j < n; j++)
            {
                var above = row[j + 1];
                row[j + 1] = before[start + i] == after[start + j] ? diagonal + 1 : Math.Max(above, row[j]);
                diagonal = above;
            }
        }

        var kept = row[n];
        return (n - kept, m - kept);
    }

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

        return name switch
        {
            "Edit" or "MultiEdit" => new("Edited", FileName("file_path"), Arg("file_path"), Added: added, Removed: removed),
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
