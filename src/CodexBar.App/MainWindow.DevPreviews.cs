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

// Development previews (CODEXBAR_PREVIEW_* environment variables) for checking the UI.
public partial class MainWindow
{
    // Developer aids for checking the new windows without clicking through the tray:
    // CODEXBAR_OPEN_SETTINGS=<page> and CODEXBAR_PREVIEW_ONBOARDING=install|login|apikey|workspace|error.
    private void OpenDevPreviews()
    {
        if (Environment.GetEnvironmentVariable("CODEXBAR_OPEN_SETTINGS") is { Length: > 0 } page)
        {
            OpenSettingsWindow(page);
        }

        // CODEXBAR_PREVIEW_REQUEST=approval|input[+launcher]: show a sample request in the notch (no provider involved);
        // "+launcher" then opens the launcher with the request under "Menunggu kamu".
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_REQUEST") is { Length: > 0 } request && _notch is not null)
        {
            var raw = System.Text.Json.JsonDocument.Parse("{}").RootElement;
            var openLauncher = request.EndsWith("+launcher", StringComparison.Ordinal);
            request = request.Replace("+launcher", string.Empty, StringComparison.Ordinal);
            if (openLauncher)
            {
                _ = Dispatcher.BeginInvoke(() => _notch.ToggleLauncher(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (request == "input")
            {
                var question = new ProviderInputQuestion("target", "Target", "Deploy ke lingkungan mana?",
                    [new ProviderInputOption("Staging", "Aman untuk dicoba"), new ProviderInputOption("Production", "Pengguna sungguhan")],
                    AllowsOther: true);
                var note = new ProviderInputQuestion("note", "Catatan", "Ada pesan untuk tim?", [], IsRequired: false);
                _ = _notch.RequestInputAsync(new ProviderUserInputEvent("Konfirmasi deploy", "Agent butuh dua jawaban sebelum lanjut.",
                    [question, note], answers => { DiagnosticsLog.Info("PreviewRequest", $"answers={answers?.Count ?? -1}"); return Task.CompletedTask; }, raw));
            }
            else
            {
                _ = _notch.RequestApprovalAsync(new ProviderApprovalEvent("item/commandExecution/requestApproval",
                    string.Join('\n', "Izinkan Codex menjalankan perintah berikut?", "", "npm install --save-dev vitest",
                        @"Folder: C:\Users\adria\AgentPlayground\ClaudeCode"),
                    decision => { DiagnosticsLog.Info("PreviewRequest", $"decision={decision}"); return Task.CompletedTask; }, raw));
            }
        }

        // CODEXBAR_PREVIEW_SUGGESTION=activity|launcher: a finished sample turn with habit guesses.
        // CODEXBAR_TEST_SUGGEST_AI=1: ask the AI for a guess on a sample turn and log it (no UI).
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_SUGGESTION") is { Length: > 0 } suggestionPreview && _notch is not null)
        {
            _notch.OnTurnStarted("Rapikan validasi form login dan tambah test");
            _notch.HandleProviderEvent(new ProviderStatusEvent("Mengubah 3 file"));
            _notch.HandleProviderEvent(new ProviderTextDeltaEvent("Validasi email sekarang memakai regex dan menolak input kosong. 6 test baru lulus."));
            _notch.HandleProviderEvent(new ProviderCompletedEvent("completed"));
            _notch.SetSuggestion(["commit dulu perubahannya", "lanjut", "jelaskan perubahannya"], fromAi: false);
            if (suggestionPreview == "launcher")
            {
                _notch.ToggleLauncher();
            }
            else
            {
                _notch.PreviewAgent(NotchWindow.MainActivityKey);
            }
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_SUGGEST_AI") is { Length: > 0 })
        {
            _lastTurnContext = TurnContext.Files;
            _ = SuggestWithAiAsync(
                "Rapikan validasi form login dan tambah test untuk kasus email kosong",
                "Sudah. Validasi email sekarang memakai regex dan menolak input kosong. Saya menambahkan 6 test di LoginForm.test.ts dan semuanya lulus. File yang diubah: LoginForm.tsx, email.ts, login.test.ts.");
        }

        // CODEXBAR_PREVIEW_PANEL=1: open the conversation panel on the current thread.
        // "cycle" also folds the chat back into the notch after 4 seconds.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_PANEL") is { Length: > 0 } panelPreview)
        {
            ((INotchHost)this).OpenConversationWindow();
            if (panelPreview == "cycle")
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _outputWindow?.Dismiss();
                };
                timer.Start();
            }
        }

        // CODEXBAR_PREVIEW_TRAY=1: open the tray menu beside the cursor.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_TRAY") is { Length: > 0 })
        {
            ShowTrayMenu();
        }

        // CODEXBAR_PREVIEW_LAUNCHER=1|menu: the launcher, optionally with the provider menu open.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_LAUNCHER") is { Length: > 0 } launcherPreview && _notch is not null)
        {
            if (launcherPreview == "menu")
            {
                _notch.PreviewProviderMenu();
            }
            else
            {
                _notch.ToggleLauncher();
            }
        }

        // CODEXBAR_PREVIEW_VOICE=setup|recording: the voice download offer, or the listening state.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_VOICE") is { Length: > 0 } voicePreview && _notch is not null)
        {
            _notch.PreviewVoice(voicePreview);
        }

        // CODEXBAR_TEST_VOICE=<16 kHz mono wav>[|lang]: install the voice engine if needed (real
        // download + hash check), then transcribe the file twice and log the timings.
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_VOICE") is { Length: > 0 } voiceTest)
        {
            _ = RunUiTaskAsync(async () =>
            {
                var parts = voiceTest.Split('|');
                var language = parts.Length > 1 ? parts[1] : _settings.VoiceLanguage;
                var pcm = File.ReadAllBytes(parts[0])[44..];
                var install = DateTime.UtcNow;
                await _voiceEngine.InstallAsync(_settings.VoiceModel, new Progress<double>(), CancellationToken.None);
                DiagnosticsLog.Info("VoiceTest", $"installed in {(DateTime.UtcNow - install).TotalSeconds:0.0}s");
                var warm = DateTime.UtcNow;
                await _voiceEngine.WarmUpAsync(_settings.VoiceModel, language);
                DiagnosticsLog.Info("VoiceTest", $"server warm in {(DateTime.UtcNow - warm).TotalMilliseconds:0} ms");
                for (var run = 0; run < 2; run++)
                {
                    var started = DateTime.UtcNow;
                    var text = await _voiceEngine.TranscribeAsync(pcm, _settings.VoiceModel, language, CancellationToken.None);
                    DiagnosticsLog.Info("VoiceTest", $"run {run}: {(DateTime.UtcNow - started).TotalMilliseconds:0} ms -> {text}");
                }
            });
        }

        // CODEXBAR_TEST_NOTIFY=1: a sample "done" notification as if nobody was looking.
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_NOTIFY") is { Length: > 0 })
        {
            const string sample = "Validasi email sekarang memakai regex dan menolak input kosong. Saya juga menambahkan **6 test** untuk kasus tepi.";
            _notifiedAnswers[NotchWindow.MainActivityKey] = sample;
            var shown = _notifier.Show(LF("notify.doneTitle", ((INotchHost)this).ProviderName, _workspaceLabel),
                CompletionNotifier.Preview(sample), NotchWindow.MainActivityKey, _settings.CompletionSound);
            DiagnosticsLog.Info("Notifier", $"test toast shown={shown}");
        }

        // CODEXBAR_TEST_PARALLEL=<prompt>: start a real parallel agent with that prompt (end-to-end check).
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_PARALLEL") is { Length: > 0 } parallelPrompt && _notch is not null)
        {
            _ = RunUiTaskAsync(async () =>
            {
                var key = await ((INotchHost)this).StartParallelAgentAsync(parallelPrompt);
                _notch.PreviewAgent(key);
            });
        }

        // CODEXBAR_PREVIEW_AGENTS=1|compact: the main conversation and two parallel agents, all
        // simulated (no processes start); "1" opens the second agent, "compact" leaves the notch shut.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_AGENTS") is { Length: > 0 } agentsPreview && _notch is not null)
        {
            _notch.OnTurnStarted("Perbaiki bug login di halaman checkout");
            _notch.HandleProviderEvent(new ProviderStatusEvent("Membaca src/checkout"));
            _notch.BeginActivity("agent-preview1", "Tulis unit test untuk PaymentService", "Claude", _workspaceLabel);
            _notch.HandleProviderEvent(new ProviderStatusEvent("Menjalankan npm test"), "agent-preview1");
            _notch.HandleProviderEvent(new ProviderTextDeltaEvent("Menambahkan 6 test untuk refund dan retry pembayaran…"), "agent-preview1");
            _notch.BeginActivity("agent-preview2", "Update README bagian arsitektur", "Claude", _workspaceLabel);
            _notch.HandleProviderEvent(new ProviderCompletedEvent("completed"), "agent-preview2");
            if (agentsPreview != "compact")
            {
                _notch.PreviewAgent("agent-preview1");
            }
        }

        // CODEXBAR_PREVIEW_QUEUE=1: a running sample turn with two queued prompts (nothing is sent).
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_QUEUE") is { Length: > 0 } && _notch is not null)
        {
            _notch.OnTurnStarted("Refactor modul pembayaran jadi service terpisah");
            _notch.HandleProviderEvent(new ProviderStatusEvent("Membaca src/payments"));
            _queue.Add(new QueuedPrompt("Setelah itu tambahkan unit test untuk PaymentService", []));
            _queue.Add(new QueuedPrompt("Update README bagian arsitektur", []));
            _notch.RefreshQueue();
            _notch.PreviewActivity();
        }

        // CODEXBAR_PREVIEW_QUOTA=1|ring: sample usage (not saved); "1" opens the launcher to show the bars.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_QUOTA") is { Length: > 0 } quotaPreview && _notch is not null)
        {
            StoreQuota(_controller.ProviderId, new ProviderQuotaEvent([
                new ProviderQuotaWindow("session", 0.78, DateTimeOffset.Now.AddHours(2.4)),
                new ProviderQuotaWindow("weekly", 0.42, DateTimeOffset.Now.AddDays(3.2))]), persist: false);
            _notch.RefreshQuota();
            if (quotaPreview != "ring")
            {
                _notch.ToggleLauncher();
            }
        }

        // CODEXBAR_PREVIEW_FILES=1: a finished sample turn that edited a few files.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_FILES") is { Length: > 0 } && _notch is not null)
        {
            var root = string.IsNullOrWhiteSpace(_settings.WorkspacePath) ? @"C:\project" : _settings.WorkspacePath;
            _notch.OnTurnStarted("Rapikan validasi form login dan tambah test");
            _notch.HandleProviderEvent(new ProviderFileChangeEvent([
                new ProviderFileChange(Path.Combine(root, "src", "auth", "LoginForm.tsx"), "update",
                    LineDiff.Build("export function validate(email) {\n  if (!email) return false;\n  return true;\n}\n",
                                   "export function validate(email: string) {\n  if (!email.trim()) return false;\n  return EMAIL.test(email);\n}\n"))]));
            _notch.HandleProviderEvent(new ProviderFileChangeEvent([
                new ProviderFileChange(Path.Combine(root, "src", "auth", "email.ts"), "add", "export const EMAIL = /^[^@\\s]+@[^@\\s]+$/;\n"),
                new ProviderFileChange(Path.Combine(root, "tests", "login.test.ts"), "update", "@@ -1,3 +1,6 @@\n import { validate } from '../src/auth/LoginForm';\n+\n+test('rejects blank email', () => {\n+  expect(validate('  ')).toBe(false);\n+});\n"),
                new ProviderFileChange(Path.Combine(root, "src", "legacy", "oldValidate.js"), "delete", "module.exports = () => true;\n")]));
            _notch.HandleProviderEvent(new ProviderTextDeltaEvent("Validasi email sekarang memakai regex dan menolak input kosong."));
            _notch.HandleProviderEvent(new ProviderCompletedEvent("completed"));
            _notch.PreviewActivity();
        }

        // CODEXBAR_PREVIEW_ATTACH=path1;path2[;drop]: open the notch launcher with those files
        // attached ("drop" also shows the drop zone), to check the chips without dragging.
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_ATTACH") is { Length: > 0 } attach && _notch is not null)
        {
            var parts = attach.Split(';', StringSplitOptions.RemoveEmptyEntries);
            ((INotchHost)this).AddAttachments(parts.Where(part => part != "drop"));
            _notch.ShowAttachments();
            if (parts.Contains("drop"))
            {
                _notch.PreviewDropZone();
            }
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_ONBOARDING") is { Length: > 0 } preview)
        {
            ShowOnboardingWindow(preview switch
            {
                "install" => OnboardingKind.InstallNeeded,
                "apikey" => OnboardingKind.ApiKeyNeeded,
                "workspace" => OnboardingKind.WorkspaceNeeded,
                "error" => OnboardingKind.Error,
                _ => OnboardingKind.LoginNeeded
            }, null);
        }
    }
}
