using System.IO;
using System.Text;
using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.App;

// When TheAI goes down, a short note (version, Windows, the error and the top of its stack — no
// conversations, no files) is kept; the next start offers to report it. "Report" opens a GitHub
// issue pre-filled with that note, which the person reads and sends (or not) themselves. Nothing
// is ever sent automatically.
public partial class MainWindow
{
    private const string IssuesUrl = "https://github.com/build-zaidana/theai-app/issues/new";

    private static string CrashFile => Path.Combine(CodexBar.Providers.AppPaths.DataRoot, "crash.json");

    private sealed record CrashNote(string Version, string Windows, string Error, string Message, string Stack, DateTimeOffset At);

    /// <summary>Called by the app's last-chance handlers, before the process ends.</summary>
    internal static void RememberCrash(Exception exception)
    {
        try
        {
            var stack = string.Join('\n', (exception.StackTrace ?? string.Empty).Split('\n').Take(12).Select(line => line.Trim()));
            var note = new CrashNote(
                typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?",
                Environment.OSVersion.VersionString,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message.Length > 300 ? exception.Message[..300] : exception.Message,
                stack,
                DateTimeOffset.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(CrashFile)!);
            File.WriteAllText(CrashFile, JsonSerializer.Serialize(note));
        }
        catch
        {
            // Going down anyway: never make it worse.
        }
    }

    private CrashNote? _lastCrash;

    private void OfferCrashReport()
    {
        try
        {
            if (!File.Exists(CrashFile))
            {
                return;
            }

            _lastCrash = JsonSerializer.Deserialize<CrashNote>(File.ReadAllText(CrashFile));
            File.Delete(CrashFile);   // offered once
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return;
        }

        if (_lastCrash is null)
        {
            return;
        }

        _notifier.Show(
            Tr.Text("TheAI tadi tertutup tiba-tiba", "TheAI closed unexpectedly"),
            Tr.Text("Mau kirim laporannya? Kamu bisa membaca isinya dulu di GitHub.", "Report it? You'll see exactly what's sent on GitHub first."),
            "crash",
            withSound: false,
            [(Tr.Text("Laporkan", "Report"), "crash-report"), (Tr.Text("Abaikan", "Dismiss"), "crash-dismiss")],
            "crash");
    }

    private void ReportCrash()
    {
        if (_lastCrash is not { } crash)
        {
            return;
        }

        var body = new StringBuilder()
            .AppendLine("**What happened** (what were you doing when it closed?)")
            .AppendLine()
            .AppendLine()
            .AppendLine("---")
            .AppendLine($"- TheAI {crash.Version}")
            .AppendLine($"- {crash.Windows}")
            .AppendLine($"- {crash.At:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine("```")
            .AppendLine($"{crash.Error}: {crash.Message}")
            .AppendLine(crash.Stack)
            .AppendLine("```")
            .ToString();
        var url = $"{IssuesUrl}?labels=crash&title={Uri.EscapeDataString($"Crash: {crash.Error.Split('.').Last()} in {crash.Version}")}&body={Uri.EscapeDataString(body)}";
        if (url.Length > 7000)
        {
            url = url[..7000];   // browsers and GitHub cap the length of a link
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        _lastCrash = null;
    }
}
