using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Windows;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using DirectoryInfo = System.IO.DirectoryInfo;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace CodexBar.App;

// First-run setup: installing and signing in to Codex / Claude Code, in the notch's sheet.
public partial class MainWindow
{
    private void ShowOnboarding(OnboardingKind kind, string? detail = null)
    {
        _onboardingKind = kind;
        InlineResponseArea.Visibility = Visibility.Collapsed;
        var loc = CodexBar.Core.Localization.Instance;
        OnboardingRetryButton.Content = loc["onboard.retry"];
        OnboardingCopyButton.Content = loc["onboard.copy"];
        OnboardingDocsButton.Content = loc["onboard.docs"];
        OnboardingDocsButton.Visibility = Visibility.Visible;
        OnboardingActionButton.Visibility = Visibility.Collapsed;

        switch (kind)
        {
            case OnboardingKind.InstallNeeded:
                OnboardingTitle.Text = loc[OnboardingKey("onboard.install.title")];
                OnboardingBody.Text = loc[OnboardingKey("onboard.install.body")];
                _onboardingCommand = IsClaudeCodeProvider
                    ? "npm install -g " + ClaudeCodeNpmPackage
                    : "npm install -g @openai/codex";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = loc["onboard.install.action"];
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus(loc[OnboardingKey("onboard.install.status")], StatusErrorBrush);
                break;

            case OnboardingKind.LoginNeeded:
                OnboardingTitle.Text = loc[OnboardingKey("onboard.login.title")];
                OnboardingBody.Text = detail ?? loc[OnboardingKey("onboard.login.body")];
                _onboardingCommand = IsClaudeCodeProvider ? "claude auth login" : "codex login";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = loc["onboard.login.action"];
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus(loc["onboard.login.status"], StatusBusyBrush);
                break;

            case OnboardingKind.WorkspaceNeeded:
                OnboardingTitle.Text = loc["onboard.workspace.title"];
                OnboardingBody.Text = loc["onboard.workspace.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                OnboardingRetryButton.Content = loc["onboard.workspace.pick"];
                OnboardingDocsButton.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.workspace.status"], StatusBusyBrush);
                break;

            case OnboardingKind.ApiKeyNeeded:
                OnboardingTitle.Text = loc["onboard.apikey.title"];
                OnboardingBody.Text = loc["onboard.apikey.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                OnboardingDocsButton.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.apikey.status"], StatusErrorBrush);
                break;

            default:
                OnboardingTitle.Text = loc["onboard.error.title"];
                OnboardingBody.Text = detail ?? loc["onboard.error.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.error.status"], StatusErrorBrush);
                break;
        }

        OnboardingCommand.Text = _onboardingCommand;
        OnboardingPanel.Visibility = Visibility.Visible;
        ShowOnboardingWindow(kind, detail);
    }

    private void HideOnboarding()
    {
        OnboardingPanel.Visibility = Visibility.Collapsed;
        _onboardingWindow?.Hide();
    }

    private const string ClaudeCodeNpmPackage = "@anthropic-ai/claude-code";

    private bool IsClaudeCodeProvider => _controller.ProviderId == "claude-code";

    // Claude-subscription onboarding reuses the Codex panel with its own copy ("onboard.claude.*").
    private string OnboardingKey(string codexKey) =>
        IsClaudeCodeProvider ? codexKey.Replace("onboard.", "onboard.claude.") : codexKey;

    private void OnboardingCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingCommand.Length > 0)
        {
            try
            {
                System.Windows.Clipboard.SetText(_onboardingCommand);
                OnboardingCopyButton.Content = CodexBar.Core.Localization.Instance["onboard.copied"];
            }
            catch (Exception exception)
            {
                ShowError(LF("status.clipboardUnavailable", exception.Message));
            }
        }
    }

    private async void OnboardingRetryButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingRetryButton.IsEnabled = false;
        try
        {
            if (_onboardingKind == OnboardingKind.WorkspaceNeeded)
            {
                var dialog = new OpenFolderDialog
                {
                    Title = L("dialog.chooseWorkspace"),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Multiselect = false
                };
                try
                {
                    _dialogVisible = true;
                    if (dialog.ShowDialog(this) == true)
                    {
                        await OpenWorkspaceSafeAsync(dialog.FolderName);
                        HideOnboarding();
                    }
                }
                finally
                {
                    _dialogVisible = false;
                }
            }
            else
            {
                await StartupAsync();
            }
        }
        catch (Exception exception)
        {
            _dialogVisible = false;
            ShowError(exception.Message);
        }
        finally
        {
            OnboardingRetryButton.IsEnabled = true;
            OnboardingCopyButton.Content = CodexBar.Core.Localization.Instance["onboard.copy"];
        }
    }

    private void OnboardingDocsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(IsClaudeCodeProvider
                ? "https://docs.claude.com/en/docs/claude-code/setup"
                : "https://github.com/openai/codex#installation")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnboardingActionButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingActionButton.IsEnabled = false;
        OnboardingRetryButton.IsEnabled = false;
        try
        {
            if (_onboardingKind == OnboardingKind.LoginNeeded)
            {
                if (IsClaudeCodeProvider)
                {
                    LaunchClaudeLogin();
                }
                else
                {
                    LaunchCodexLogin();
                }

                OnboardingBody.Text = CodexBar.Core.Localization.Instance["onboard.login.wait"];
                return;
            }

            if (_onboardingKind != OnboardingKind.InstallNeeded)
            {
                return;
            }

            var loc = CodexBar.Core.Localization.Instance;
            OnboardingBody.Text = loc[OnboardingKey("onboard.install.progress")];
            SetStatus(loc[OnboardingKey("onboard.install.statusProgress")], StatusBusyBrush);
            var npm = FindCommand("npm.cmd") ?? FindCommand("npm.exe");
            if (npm is null)
            {
                var winget = FindCommand("winget.exe")
                    ?? throw new InvalidOperationException(
                        loc["onboard.install.nodeMissing"]);
                await RunSetupProcessAsync(winget,
                    "install", "--id", "OpenJS.NodeJS.LTS", "--exact",
                    "--accept-package-agreements", "--accept-source-agreements", "--silent");
                npm = FindCommand("npm.cmd")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
                if (!File.Exists(npm))
                {
                    throw new FileNotFoundException(loc["onboard.install.npmMissing"]);
                }
            }

            await RunSetupProcessAsync(npm, "install", "-g",
                IsClaudeCodeProvider ? ClaudeCodeNpmPackage : "@openai/codex");
            ShowOnboarding(OnboardingKind.LoginNeeded,
                loc[OnboardingKey("onboard.install.success")]);
        }
        catch (Exception exception)
        {
            ShowOnboarding(OnboardingKind.Error, string.Format(
                CodexBar.Core.Localization.Instance["onboard.install.failed"], exception.Message));
        }
        finally
        {
            OnboardingActionButton.IsEnabled = true;
            OnboardingRetryButton.IsEnabled = true;
        }
    }

    private static string? FindCommand(string command)
    {
        var appDataCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", command);
        if (File.Exists(appDataCandidate))
        {
            return appDataCandidate;
        }

        var programFilesCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", command);
        if (File.Exists(programFilesCandidate))
        {
            return programFilesCandidate;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), command))
            .FirstOrDefault(File.Exists);
    }

    private static async Task RunSetupProcessAsync(string executable, params string[] arguments)
    {
        var isCommandScript = Path.GetExtension(executable) is ".cmd" or ".bat";
        var startInfo = new ProcessStartInfo(
            isCommandScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isCommandScript)
        {
            var commandLine = string.Join(' ', new[] { executable }.Concat(arguments)
                .Select(value => $"\"{value.Replace("\"", "\"\"")}\""));
            startInfo.Arguments = $"/d /s /c \"{commandLine}\"";
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Tidak dapat menjalankan {Path.GetFileName(executable)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            detail = detail.Trim();
            if (detail.Length > 500)
            {
                detail = detail[^500..];
            }
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} berhenti dengan kode {process.ExitCode}. {detail}");
        }
    }

    private static void LaunchCodexLogin()
    {
        var npmBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var launcher = new[]
        {
            Path.Combine(npmBin, "codex.cmd"),
            Path.Combine(npmBin, "codex.exe")
        }.FirstOrDefault(File.Exists) ?? "codex";
        Process.Start(new ProcessStartInfo(launcher, "login") { UseShellExecute = true });
    }

    private static void LaunchClaudeLogin()
    {
        var launcher = CodexBar.Providers.ClaudeCode.ClaudeCodeExecutableLocator.TryLocate() ?? "claude";
        Process.Start(new ProcessStartInfo(launcher, "auth login") { UseShellExecute = true });
    }

    // ---- Onboarding sheet (notch mode) ---------------------------------------------------------

    private void ShowOnboardingWindow(OnboardingKind kind, string? detail)
    {
        var claude = IsClaudeCodeProvider;
        var tool = claude ? "Claude Code" : "Codex CLI";
        var installed = claude
            ? CodexBar.Providers.ClaudeCode.ClaudeCodeExecutableLocator.TryLocate() is not null
            : TryLocateCodex();
        var loc = CodexBar.Core.Localization.Instance;
        var status = new List<OnboardingStatus>();
        if (!_controller.Capabilities.SupportsApiKey)
        {
            status.Add(new OnboardingStatus(tool, installed ? Tr.Text("Terpasang", "Installed") : Tr.Text("Belum terpasang", "Not installed"), installed));
            status.Add(new OnboardingStatus(Tr.Text("Akun", "Account"),
                kind is OnboardingKind.LoginNeeded or OnboardingKind.InstallNeeded ? Tr.Text("Belum masuk", "Not signed in") : Tr.Text("Terhubung", "Connected"),
                kind is not (OnboardingKind.LoginNeeded or OnboardingKind.InstallNeeded)));
        }
        else
        {
            status.Add(new OnboardingStatus("API key", Tr.Text("Belum diatur", "Not set"), false));
        }

        if (kind == OnboardingKind.WorkspaceNeeded)
        {
            status.Add(new OnboardingStatus(Tr.Text("Folder kerja", "Workspace"), Tr.Text("Belum dipilih", "Not chosen"), false));
        }

        Task Retry() => StartupAsync();
        var args = new RoutedEventArgs();
        OnboardingContent content = kind switch
        {
            OnboardingKind.InstallNeeded => new(
                Tr.Text($"Pasang {tool}", $"Install {tool}"),
                loc[OnboardingKey("onboard.install.body")],
                status, _onboardingCommand,
                Tr.Text("Pasang otomatis", "Install automatically"), () => { OnboardingActionButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                Tr.Text("Panduan…", "Guide…"), () => OnboardingDocsButton_Click(this, args)),
            OnboardingKind.LoginNeeded => new(
                claude ? Tr.Text("Hubungkan Claude", "Connect Claude") : Tr.Text("Hubungkan Codex", "Connect Codex"),
                detail ?? (claude
                    ? Tr.Text("CodexBar memakai Claude Code yang login dengan langganan Pro atau Max. Tidak perlu API key.", "CodexBar uses Claude Code signed in with a Pro or Max subscription. No API key needed.")
                    : Tr.Text("CodexBar memakai Codex CLI yang login dengan akun ChatGPT.", "CodexBar uses the Codex CLI signed in with your ChatGPT account.")),
                status, _onboardingCommand,
                Tr.Text("Masuk…", "Sign in…"), () => { OnboardingActionButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                Tr.Text("Pakai API key…", "Use an API key…"), () => OpenSettingsWindow("provider")),
            OnboardingKind.ApiKeyNeeded => new(
                Tr.Text("Masukkan API key", "Add an API key"),
                loc["onboard.apikey.body"],
                status, null,
                Tr.Text("Buka pengaturan", "Open settings"), () => { OpenSettingsWindow("provider"); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                null, null),
            OnboardingKind.WorkspaceNeeded => new(
                Tr.Text("Pilih folder kerja", "Choose a workspace"),
                loc["onboard.workspace.body"],
                status, null,
                Tr.Text("Pilih folder…", "Choose folder…"), () => { OnboardingRetryButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Nanti", "Later"), () => { _onboardingWindow?.Hide(); return Task.CompletedTask; },
                null, null),
            _ => new(
                loc["onboard.error.title"],
                detail ?? loc["onboard.error.body"],
                status, null,
                Tr.Text("Coba lagi", "Try again"), Retry,
                Tr.Text("Nanti", "Later"), () => { _onboardingWindow?.Hide(); return Task.CompletedTask; },
                Tr.Text("Buka log…", "Open log…"), () => OpenLogsButton_Click(this, args))
        };

        _onboardingWindow ??= new OnboardingWindow();
        _onboardingWindow.Show(content);
    }

    private static bool TryLocateCodex()
    {
        try
        {
            CodexBar.Providers.Codex.CodexExecutableLocator.Locate();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
