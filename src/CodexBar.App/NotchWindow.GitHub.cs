using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CodexBar.Core;

namespace CodexBar.App;

// The pull request of the branch being worked on, as a live activity: its checks as a ring while
// CI runs, then a check or the failed checks (with "ask the agent"). Read with the GitHub CLI the
// user is already signed in to (no token of TheAI's own), in the folder of the last terminal
// command or else the workspace; every 30 s while checks run, every 5 min otherwise, and soon
// after a push. Nothing happens without gh, a sign-in or a PR.
public partial class NotchWindow
{
    private static readonly TimeSpan PrIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PrBusy = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _prTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? _ghPath;
    private string _prReported = string.Empty;
    private string _prFolder = string.Empty;
    private bool _prChecking;

    private void InitGitHub()
    {
        _ghPath = LocateGh();
        if (_ghPath is null)
        {
            return;
        }

        _prTimer.Tick += async (_, _) => await CheckPullRequestAsync();
        _prTimer.Start();
        Closed += (_, _) => _prTimer.Stop();
    }

    /// <summary>A push (or a new PR) was just made: look again shortly.</summary>
    private void PullRequestSoon()
    {
        if (_ghPath is null)
        {
            return;
        }

        _prTimer.Stop();
        _prTimer.Interval = TimeSpan.FromSeconds(15);
        _prTimer.Start();
    }

    private async Task CheckPullRequestAsync()
    {
        if (_prChecking || !_host.LiveActivities || !_host.GitHubChecks)
        {
            _prTimer.Interval = PrIdle;
            return;
        }

        _prChecking = true;
        _prTimer.Stop();
        var next = PrIdle;
        try
        {
            var folder = PullRequestFolder();
            if (folder is null)
            {
                return;
            }

            if (!string.Equals(folder, _prFolder, StringComparison.OrdinalIgnoreCase))
            {
                _prFolder = folder;
                _prReported = string.Empty;   // another repository: start fresh
                _jobs.Dismiss("gh-pr");
            }

            var (exitCode, output) = await RunGhAsync(folder, $"pr view --json {PullRequestStatus.JsonFields}");
            if (exitCode != 0 || PullRequestStatus.Parse(output) is not { } pr)
            {
                return;   // no PR for this branch, not signed in, offline: quietly try later
            }

            if (pr.Pending)
            {
                next = PrBusy;
            }

            if (pr.ToJob(folder, _prReported, out var reported) is { } update)
            {
                ApplyJob(update);
            }

            _prReported = reported;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("GitHub", exception.Message);
        }
        finally
        {
            _prChecking = false;
            _prTimer.Interval = next;
            _prTimer.Start();
        }
    }

    /// <summary>Where the work is: the folder of the latest terminal command, else the workspace — if it's a git repository.</summary>
    private string? PullRequestFolder()
    {
        var candidates = _jobs.Jobs.Where(job => job.Id.StartsWith("sh-", StringComparison.Ordinal) && job.Folder.Length > 0)
            .OrderByDescending(job => job.Updated)
            .Select(job => job.Folder)
            .Append(_host.WorkspacePath);
        return candidates.FirstOrDefault(IsGitRepository);
    }

    private static bool IsGitRepository(string? folder)
    {
        for (var directory = folder is { Length: > 0 } && Directory.Exists(folder) ? new DirectoryInfo(folder) : null;
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return true;
            }
        }

        return false;
    }

    private static string? LocateGh()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "gh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<(int ExitCode, string Output)> RunGhAsync(string folder, string arguments)
    {
        var start = new ProcessStartInfo(_ghPath!, arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return (-1, string.Empty);
        }
    }
}
