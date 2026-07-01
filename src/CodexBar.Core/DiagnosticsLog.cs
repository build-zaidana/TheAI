using System.Text;

namespace CodexBar.Core;

/// <summary>
/// Opt-in diagnostics logging. When enabled, writes newline-delimited entries to a
/// per-day file (<c>codexbar-yyyyMMdd.log</c>) under <c>{baseDirectory}\logs</c> and
/// prunes files older than the retention window on configure.
///
/// Disabled by default. Every operation is best-effort: logging must never throw or
/// crash the app, so all I/O is wrapped and failures are swallowed.
/// </summary>
public static class DiagnosticsLog
{
    private const int RetentionDays = 7;
    private static readonly object Gate = new();
    private static string? _logDirectory;

    public static bool IsEnabled { get; private set; }

    /// <summary>Directory logs are written to when enabled; <c>null</c> when disabled.</summary>
    public static string? LogDirectory
    {
        get { lock (Gate) { return _logDirectory; } }
    }

    /// <summary>
    /// Enable or disable logging. When enabling, ensures the log directory exists and
    /// prunes stale files. Safe to call repeatedly (e.g. when the setting is toggled).
    /// </summary>
    public static void Configure(bool enabled, string baseDirectory)
    {
        lock (Gate)
        {
            if (!enabled || string.IsNullOrWhiteSpace(baseDirectory))
            {
                IsEnabled = false;
                _logDirectory = null;
                return;
            }

            try
            {
                var directory = Path.Combine(baseDirectory, "logs");
                System.IO.Directory.CreateDirectory(directory);
                _logDirectory = directory;
                IsEnabled = true;
                Prune(directory);
            }
            catch
            {
                // If we cannot prepare the directory, stay disabled rather than crash.
                IsEnabled = false;
                _logDirectory = null;
            }
        }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);

    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write("ERROR", category, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string category, string message)
    {
        string directory;
        lock (Gate)
        {
            if (!IsEnabled || _logDirectory is null)
            {
                return;
            }

            directory = _logDirectory;
            try
            {
                var path = Path.Combine(directory, $"codexbar-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch
            {
                // Best-effort: never surface logging failures to the user.
            }
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "codexbar-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }
}
