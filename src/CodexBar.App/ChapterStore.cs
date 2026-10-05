using System.IO;
using System.Text.Json;

namespace CodexBar.App;

/// <summary>A pinned point in a conversation ("Pin as chapter"): the message's key and a title.</summary>
public sealed record Chapter(string Key, string Title);

/// <summary>Chapters per conversation, kept in %LOCALAPPDATA%\CodexBar\chapters.</summary>
internal static class ChapterStore
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "chapters");

    private static string FileFor(string provider, string thread) =>
        Path.Combine(Folder, provider, string.Concat(thread.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public static List<Chapter> Load(string provider, string? thread)
    {
        if (thread is null)
        {
            return [];
        }

        try
        {
            var path = FileFor(provider, thread);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Chapter>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Pins the message, or unpins it when it already is; returns the chapters now.</summary>
    public static List<Chapter> Toggle(string provider, string thread, string key, string title)
    {
        var chapters = Load(provider, thread);
        if (chapters.RemoveAll(chapter => chapter.Key == key) == 0)
        {
            chapters.Add(new Chapter(key, title));
        }

        try
        {
            var path = FileFor(provider, thread);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(chapters));
        }
        catch (Exception)
        {
        }

        return chapters;
    }
}
