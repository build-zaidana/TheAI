using System.Text.Json;

namespace CodexBar.Providers;

/// <summary>
/// Persists conversations for providers with no server-side thread storage (Anthropic,
/// OpenAI, OpenRouter) so history survives an app restart instead of living only in the
/// provider's in-memory message list. One JSON file per thread under
/// <c>%LOCALAPPDATA%\CodexBar\conversations\{providerId}\{threadId}.json</c>.
/// </summary>
public static class LocalConversationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static IReadOnlyList<ProviderThreadMessage> Load(string providerId, string threadId)
    {
        try
        {
            var path = FilePath(providerId, threadId);
            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ProviderThreadMessage>>(json, Options) ?? [];
        }
        catch (Exception)
        {
            // Corrupt or unreadable file: behave like a fresh conversation rather than crash.
            return [];
        }
    }

    public static void Save(string providerId, string threadId, IReadOnlyList<ProviderThreadMessage> messages)
    {
        try
        {
            var path = FilePath(providerId, threadId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(messages, Options));
        }
        catch (Exception)
        {
            // Best-effort: losing persisted history must not fail the turn itself.
        }
    }

    /// <summary>
    /// Threads saved for <paramref name="providerId"/>, newest first, with a title/preview
    /// derived from the messages themselves (there is no separate metadata file). Threads
    /// with no messages are skipped. <paramref name="searchTerm"/> filters by title/preview.
    /// </summary>
    public static IReadOnlyList<ProviderThreadSummary> ListSummaries(
        string providerId, int limit, string? searchTerm = null)
    {
        try
        {
            var directory = ProviderDirectory(providerId);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var summaries = new List<ProviderThreadSummary>();
            foreach (var file in Directory.GetFiles(directory, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var threadId = Path.GetFileNameWithoutExtension(file);
                var messages = Load(providerId, threadId);
                if (messages.Count == 0)
                {
                    continue;
                }

                var title = BuildTitle(messages);
                var preview = BuildPreview(messages);
                if (!string.IsNullOrEmpty(searchTerm) &&
                    title.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) < 0 &&
                    preview.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                summaries.Add(new ProviderThreadSummary(
                    threadId,
                    title,
                    preview,
                    WorkspacePath: string.Empty,
                    UpdatedAt: new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)));

                if (summaries.Count >= limit)
                {
                    break;
                }
            }

            return summaries;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string BuildTitle(IReadOnlyList<ProviderThreadMessage> messages)
    {
        var firstUser = messages.FirstOrDefault(m => m.Role == "user")?.Text ?? messages[0].Text;
        return Truncate(CollapseWhitespace(firstUser), 60);
    }

    private static string BuildPreview(IReadOnlyList<ProviderThreadMessage> messages) =>
        Truncate(CollapseWhitespace(messages[^1].Text), 120);

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string ProviderDirectory(string providerId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar", "conversations", providerId);

    private static string FilePath(string providerId, string threadId) =>
        Path.Combine(ProviderDirectory(providerId), $"{Sanitize(threadId)}.json");

    private static string Sanitize(string threadId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = threadId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
