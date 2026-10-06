using System.IO;
using System.Text.Json;

namespace CodexBar.App;

/// <summary>
/// Versions of a prompt, as Claude shows "‹ 1/2 ›": sessions that go on differently from the
/// same point (<see cref="Anchor"/> = the answer they share last, "" = the very start).
/// </summary>
public sealed record BranchGroup(string Anchor, List<string> Sessions);

/// <summary>The version groups, kept in %LOCALAPPDATA%\CodexBar\branches.json.</summary>
internal static class BranchStore
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "branches.json");

    // Read once, then kept in memory (every prompt asks for its versions on each render).
    private static List<BranchGroup>? _groups;

    public static List<BranchGroup> Load()
    {
        lock (Gate)
        {
            if (_groups is not null)
            {
                return _groups;
            }

            try
            {
                _groups = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<BranchGroup>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch (Exception)
            {
                _groups = [];
            }

            return _groups;
        }
    }

    /// <summary>Records <paramref name="branch"/> as another version of what <paramref name="source"/> did after the anchor.</summary>
    public static void Add(string anchor, string source, string branch)
    {
        lock (Gate)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Anchor == anchor && g.Sessions.Contains(source, StringComparer.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new BranchGroup(anchor, [source]);
                groups.Add(group);
            }

            if (!group.Sessions.Contains(branch, StringComparer.OrdinalIgnoreCase))
            {
                group.Sessions.Add(branch);
            }

            Save(groups);
        }
    }

    /// <summary>The versions of what follows <paramref name="anchor"/> that include <paramref name="session"/>.</summary>
    public static BranchGroup? Find(string anchor, string session)
    {
        lock (Gate)
        {
            return Load().FirstOrDefault(g => g.Anchor == anchor && g.Sessions.Contains(session, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Forgets sessions that no longer exist (deleted outside CodexBar).</summary>
    public static void Forget(string session)
    {
        lock (Gate)
        {
            var groups = Load();
            foreach (var group in groups)
            {
                group.Sessions.RemoveAll(s => string.Equals(s, session, StringComparison.OrdinalIgnoreCase));
            }

            groups.RemoveAll(g => g.Sessions.Count < 2);
            Save(groups);
        }
    }

    private static void Save(List<BranchGroup> groups)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(groups));
        }
        catch (Exception)
        {
        }
    }
}
