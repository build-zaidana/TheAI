namespace CodexBar.Providers;

/// <summary>
/// The product's name and where its data lives. The app was called CodexBar before it was
/// published as TheAI; <see cref="MigrateLegacyData"/> brings an existing CodexBar data folder
/// over once, on the first start of TheAI.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "TheAI";
    private const string LegacyName = "CodexBar";
    private const string MigratedMarker = ".migrated-from-codexbar";

    /// <summary>
    /// %LOCALAPPDATA%\TheAI (settings, conversations, caches, logs). THEAI_DATA_DIR points it
    /// elsewhere — the tests use that so they never touch the user's own data.
    /// </summary>
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("THEAI_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string LegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName);

    /// <summary>
    /// First start after the rename: everything in the old data folder moves into the new one (the
    /// old copy wins over anything already there — that can only be leftovers from a trial run),
    /// then a marker keeps it from happening again. Returns true when something moved. Never
    /// throws: what can't be moved simply stays in the old folder.
    /// </summary>
    public static bool MigrateLegacyData() => MigrateLegacyData(LegacyRoot, DataRoot);

    public static bool MigrateLegacyData(string legacy, string target)
    {
        try
        {
            if (!Directory.Exists(legacy) || File.Exists(Path.Combine(target, MigratedMarker)))
            {
                return false;
            }

            if (!Directory.Exists(target))
            {
                Directory.Move(legacy, target);
            }
            else
            {
                MoveContents(new DirectoryInfo(legacy), target);
                if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                {
                    Directory.Delete(legacy);
                }
            }

            File.WriteAllText(Path.Combine(target, MigratedMarker), DateTimeOffset.Now.ToString("O"));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MoveContents(DirectoryInfo from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles())
        {
            try
            {
                file.MoveTo(Path.Combine(to, file.Name), overwrite: true);
            }
            catch (IOException)
            {
                // In use or locked: it stays behind in the old folder.
            }
        }

        foreach (var directory in from.EnumerateDirectories())
        {
            MoveContents(directory, Path.Combine(to, directory.Name));
            try
            {
                if (!directory.EnumerateFileSystemInfos().Any())
                {
                    directory.Delete();
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
