namespace CodexBar.Core;

/// <summary>
/// Which files appearing in the Downloads folder are finished downloads worth putting on the
/// shelf: not a browser's or downloader's partial file, not hidden or an Office lock file.
/// </summary>
public static class DownloadFilter
{
    /// <summary>How long a download stays on the shelf.</summary>
    public static readonly TimeSpan ShelfTime = TimeSpan.FromHours(2);

    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload",   // Chrome, Edge, Brave
        ".part",         // Firefox
        ".partial",      // older Edge
        ".download",     // Safari
        ".opdownload",   // Opera
        ".tmp",
        ".temp",
        ".!ut",          // µTorrent
        ".aria2",
        ".downloading"
    };

    public static bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal) ||
            name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !PartialExtensions.Contains(Path.GetExtension(name)) &&
               // Chrome's in-progress name before the .crdownload: "Unconfirmed 123456.crdownload".
               !name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase);
    }
}
