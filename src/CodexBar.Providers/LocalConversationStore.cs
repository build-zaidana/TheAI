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

    private static string FilePath(string providerId, string threadId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar", "conversations", providerId, $"{Sanitize(threadId)}.json");

    private static string Sanitize(string threadId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = threadId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
