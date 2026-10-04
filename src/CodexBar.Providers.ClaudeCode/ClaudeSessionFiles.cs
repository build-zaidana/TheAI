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

    /// <summary>The conversation as user / assistant text (tool calls and results left out).</summary>
    public static IReadOnlyList<ProviderThreadMessage> ReadMessages(string filePath)
    {
        var messages = new List<ProviderThreadMessage>();
        var assistant = new StringBuilder();

        void FlushAssistant()
        {
            if (assistant.Length > 0)
            {
                messages.Add(new ProviderThreadMessage("assistant", assistant.ToString().Trim()));
                assistant.Clear();
            }
        }

        foreach (var line in ReadLines(filePath))
        {
            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && HumanPrompt(line) is { } prompt)
            {
                FlushAssistant();
                messages.Add(new ProviderThreadMessage("user", prompt));
            }
            else if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && AssistantText(line) is { } text)
            {
                if (assistant.Length > 0)
                {
                    assistant.Append("\n\n");
                }

                assistant.Append(text);
            }
        }

        FlushAssistant();
        return messages;
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
