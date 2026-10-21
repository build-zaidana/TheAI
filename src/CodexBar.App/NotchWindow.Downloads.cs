using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using CodexBar.Core;

namespace CodexBar.App;

// Finished downloads land on the shelf for a while (two hours), ready to be dragged into an app or
// the prompt; the notch says so with a short check and the file's name. The Downloads folder is
// watched (FileSystemWatcher); a browser's partial files are ignored, and a file is taken only
// once it has stopped growing and nothing holds it open.
public partial class NotchWindow
{
    private static readonly string DownloadsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "shelf-downloads.json");

    private readonly Dictionary<string, DateTimeOffset> _downloadExpiry = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _downloadPrune = new() { Interval = TimeSpan.FromMinutes(5) };
    private FileSystemWatcher? _downloads;

    private void InitDownloads()
    {
        try
        {
            if (File.Exists(DownloadsFile) && JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(DownloadsFile)) is { } saved)
            {
                foreach (var (path, expires) in saved)
                {
                    _downloadExpiry[path] = expires;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            DiagnosticsLog.Error("Downloads", exception.Message);
        }

        _downloadPrune.Tick += (_, _) => PruneDownloads();
        _downloadPrune.Start();
        PruneDownloads();
        ApplyDownloadWatcher();
        Closed += (_, _) =>
        {
            _downloadPrune.Stop();
            _downloads?.Dispose();
        };
    }

    /// <summary>Settings may have turned it on or off.</summary>
    private void ApplyDownloadWatcher()
    {
        if (_host.DownloadsToShelf && _downloads is null && DownloadsFolder() is { } folder)
        {
            try
            {
                _downloads = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 16 * 1024
                };
                _downloads.Created += (_, e) => Dispatcher.BeginInvoke(() => _ = TakeDownloadAsync(e.FullPath));
                _downloads.Renamed += (_, e) => Dispatcher.BeginInvoke(() => _ = TakeDownloadAsync(e.FullPath));
                _downloads.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            {
                DiagnosticsLog.Error("Downloads", exception.Message);
                _downloads = null;
            }
        }
        else if (!_host.DownloadsToShelf && _downloads is not null)
        {
            _downloads.Dispose();
            _downloads = null;
        }
    }

    // A browser renames its partial file when done; a plain copy appears and grows. Either way: wait
    // until the size holds still and the file can be opened, then shelve it.
    private async Task TakeDownloadAsync(string path)
    {
        if (!DownloadFilter.IsCandidate(path) || _downloadExpiry.ContainsKey(path))
        {
            return;
        }

        long lastSize = -1;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1.5 : 2));
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || (file.Attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) != 0)
                {
                    return;
                }

                if (file.Length == 0 || file.Length != lastSize)
                {
                    lastSize = file.Length;
                    continue;
                }

                using (file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                }

                break;
            }
            catch (IOException)
            {
                // Still being written.
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }

        if (!File.Exists(path) || _downloadExpiry.ContainsKey(path) || AddToShelf([path]) == 0)
        {
            return;
        }

        _downloadExpiry[path] = DateTimeOffset.Now + DownloadFilter.ShelfTime;
        SaveDownloads();
        ApplyJob(new LiveJobUpdate("dl", Path.GetFileName(path), Detail: Tr.Text("di rak", "on the shelf"), Done: true));
    }

    private void PruneDownloads()
    {
        var now = DateTimeOffset.Now;
        var expired = _downloadExpiry.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList();
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var path in expired)
        {
            _downloadExpiry.Remove(path);
        }

        // Only what is still on the shelf because it was downloaded goes; files put there by hand stay.
        if (_shelf.RemoveAll(path => expired.Contains(path, StringComparer.OrdinalIgnoreCase)) > 0)
        {
            ShelfChanged();
        }

        SaveDownloads();
    }

    private void SaveDownloads()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DownloadsFile)!);
            File.WriteAllText(DownloadsFile, JsonSerializer.Serialize(_downloadExpiry));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Downloads", exception.Message);
        }
    }

    private static string? DownloadsFolder()
    {
        // The real Downloads folder, wherever the user moved it.
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try
            {
                var path = Marshal.PtrToStringUni(pointer);
                if (Directory.Exists(path))
                {
                    return path;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
