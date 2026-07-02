using System.IO.Compression;

namespace CodexBar.Core;

/// <summary>
/// Bundles diagnostics into a single zip a user can attach to a bug report: an info
/// summary plus any diagnostics log files. Kept dependency-free and pure so it can be
/// unit-tested without the UI.
/// </summary>
public static class BugReport
{
    /// <summary>
    /// Create a bug-report zip under <c>{baseDirectory}\reports</c> containing an
    /// <c>info.txt</c> summary and every <c>*.log</c> from <c>{baseDirectory}\logs</c>.
    /// Returns the zip path.
    /// </summary>
    public static string Create(string baseDirectory, string info)
    {
        var reportsDirectory = Path.Combine(baseDirectory, "reports");
        Directory.CreateDirectory(reportsDirectory);

        var zipPath = Path.Combine(reportsDirectory, $"codexbar-bugreport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var infoEntry = archive.CreateEntry("info.txt");
        using (var writer = new StreamWriter(infoEntry.Open()))
        {
            writer.Write(info);
        }

        var logsDirectory = Path.Combine(baseDirectory, "logs");
        if (Directory.Exists(logsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
            {
                archive.CreateEntryFromFile(file, $"logs/{Path.GetFileName(file)}");
            }
        }

        return zipPath;
    }
}
