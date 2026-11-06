using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>
/// Windows' own "shutdown /s /t X" and "shutdown /a". Windows treats any X above 0 as forced: open apps
/// close without asking, which is why the page says so. Previews, snapshots and tests never reach
/// shutdown.exe: they log what would have run.
/// </summary>
internal static class SystemShutdown
{
    private static string SavedFile => Path.Combine(CodexBar.Providers.AppPaths.DataRoot, "shutdown.json");

    private static bool DryRun =>
        Environment.GetEnvironmentVariable("CODEXBAR_SHUTDOWN_DRYRUN") is { Length: > 0 } ||
        Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 };

    public static Task<bool> ScheduleAsync(int seconds) =>
        RunAsync("/s", "/t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static Task<bool> CancelAsync() => RunAsync("/a");

    private static async Task<bool> RunAsync(params string[] arguments)
    {
        if (DryRun)
        {
            DiagnosticsLog.Info("Shutdown", "dry run: shutdown " + string.Join(' ', arguments));
            return true;
        }

        try
        {
            var start = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticsLog.Error("Shutdown", exception.Message);
            return false;
        }
    }

    public sealed record Saved(DateTimeOffset At, DateTimeOffset Started);

    public static Saved? Load()
    {
        try
        {
            if (File.Exists(SavedFile) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedFile)) is { } saved && saved.At > DateTimeOffset.Now)
            {
                return saved;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Shutdown", exception.Message);
        }

        return null;
    }

    public static void Save(Saved? saved)
    {
        try
        {
            if (saved is null)
            {
                File.Delete(SavedFile);
            }
            else
            {
                File.WriteAllText(SavedFile, JsonSerializer.Serialize(saved));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Shutdown", exception.Message);
        }
    }
}
