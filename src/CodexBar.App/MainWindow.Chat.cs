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

// The chat beside the notch: the panel and its wiring, branches and versions, fork, export,
// file links, and the model / effort / mode pickers under the box.
public partial class MainWindow
{
    private ConversationView ActiveConversation => GetOutputWindow().Conversation;

    private ConversationPanel GetOutputWindow()
    {
        if (_outputWindow is not null)
        {
            return _outputWindow;
        }

        // The chat is part of the notch: always on top (the bar's "always on top" setting is not for it).
        var window = new ConversationPanel
        {
            NotchRect = () => _notch?.ShapeScreenInfo(),
            Side = _settings.ChatSide
        };
        window.Dismissed += (_, _) => _notch?.ResumeAfterChat();
        window.Opened += (_, _) =>
        {
            DiagnosticsLog.Info("Chat", "opened");
            if (_renderAfterOpen)
            {
                _renderAfterOpen = false;
                RenderConversation();
            }
        };
        window.OutputScrollChanged += ResponseScroll_ScrollChanged;
        window.JumpLatestRequested += (_, _) => JumpToLatest();
        window.CopyRequested += (_, _) => CopyResponse();
        window.SendRequested += (_, text) => PanelSend(text);
        window.StopRequested += (_, _) => _ = RunUiTaskAsync(StopCurrentTurnAsync);
        window.AttachRequested += (_, _) => AttachButton_Click(this, new RoutedEventArgs());
        window.RemoveAttachmentRequested += (_, path) =>
        {
            if (_attachments.FirstOrDefault(item => item.Path == path) is { } attachment)
            {
                _attachments.Remove(attachment);
                CleanupTemporaryAttachments([attachment.Path]);
                RenderAttachments();
            }
        };
        window.PanelWidth = _settings.ChatWidth;
        MarkdownRenderer.FileLinkClicked += OpenFileLink;
        window.SetContextUsage(_contextUsage.Tokens, _contextUsage.Window);
        window.CompactRequested += (_, _) => _ = RunUiTaskAsync(CompactFromChatAsync);
        window.ExportRequested += (_, _) => ExportConversation();
        window.WidthCommitted += (_, width) =>
        {
            _settings.ChatWidth = Math.Round(width);
            _ = SaveSettingsSafeAsync();
        };
        window.FilesDropped += (_, paths) => ((INotchHost)this).AddAttachments(paths);
        window.Conversation.VersionOf = VersionOf;
        window.Conversation.VersionRequested += (_, request) => _ = RunUiTaskAsync(() => ShowVersionAsync(request.Message, request.Step));
        window.LastPrompt = () => _messages.LastOrDefault(message => message.IsUser) is { } last ? StripAttachmentLines(last.Markdown) : null;
        window.Conversation.EditRequested += (_, message) => _ = RunUiTaskAsync(() => EditMessageAsync(message));
        window.Conversation.RetryRequested += (_, answer) => _ = RunUiTaskAsync(() => RetryAnswerAsync(answer));
        window.Conversation.CanFork = message => IsClaudeCodeProvider && message.SourceId is not null && _controller.ThreadId is not null;
        window.Conversation.ReadingKey = () => ReadAloud.Current;
        window.Conversation.ReadAloudRequested += (_, message) =>
            ReadAloud.Toggle(ConversationView.KeyOf(message), message.Markdown, _settings.Language);
        window.Conversation.PinToggled += (_, message) =>
        {
            if (_controller.ThreadId is { } thread)
            {
                ChapterStore.Toggle(_controller.ProviderId, thread, ConversationView.KeyOf(message), ConversationView.ChapterTitle(message));
                RenderConversation();
            }
        };
        window.Conversation.ForkRequested += (_, message) => _ = RunUiTaskAsync(() => ForkFromAsync(message));
        window.CommandInvoked += (_, command) =>
        {
            switch (command)
            {
                case "clear":
                    _ = RunUiTaskAsync(CreateThreadFromSidebarAsync);
                    break;
                case "settings":
                    OpenSettingsWindow();
                    break;
            }
        };
        window.ModelChosen += (_, id) =>
        {
            if ((ModelCombo.ItemsSource as IEnumerable<ProviderModelInfo>)?.FirstOrDefault(m => m.Model == id) is { } picked)
            {
                ModelCombo.SelectedItem = picked; // saves the setting
                UpdatePanelHeader();
            }
        };
        window.EffortChosen += (_, id) =>
        {
            _settings.ReasoningEffort = id;
            SelectEffortCombo(id);
            _ = SaveSettingsSafeAsync();
            UpdatePanelHeader();
        };
        window.ModeChosen += async (_, id) => await ChangeApprovalPolicyAsync(id);
        window.VoiceAvailable = () => CodexBar.App.Voice.WhisperEngine.IsInstalled(_settings.VoiceModel);
        window.Transcribe = pcm => ((INotchHost)this).TranscribeAsync(pcm);
        // Ctrl+V with a screenshot or copied files attaches them (plain text pastes as usual).
        window.PasteAttachment = () => !System.Windows.Clipboard.ContainsText() && TryAttachClipboardContent();
        window.ApplyBackdrop(!IsLightTheme());
        window.SetBusy(_controller.IsBusy);
        window.SetAttachments(_attachments.Select(item => (item.DisplayName, item.Path)).ToArray());
        _outputWindow = window;
        return window;
    }

    // Sent from the panel's own input: queued while a turn runs, otherwise sent with the
    // attachments that are waiting.
    // "Fork from here" (Claude Code): a new session with the conversation up to this answer,
    // opened in the chat so it can go on differently; the original stays as it is.
    private async Task ForkFromAsync(ChatMessage message)
    {
        if (await BranchAtAsync(message, "Fork: " + (_outputWindow?.TitleText.Text ?? ConversationView.ChapterTitle(message))))
        {
            SetStatus(Tr.Text("Fork dibuat", "Forked"), StatusReadyBrush);
        }
    }

    // A new Claude Code session with the conversation up to <paramref name="upto"/> (an answer),
    // opened in the chat; null means from the very start (a new conversation, same folder).
    // With asVersion, the new session counts as another version of what followed that point
    // (Edit / Retry), shown as "‹ 1/2 ›" under the prompt.
    private async Task<bool> BranchAtAsync(ChatMessage? upto, string title, bool asVersion = false)
    {
        if (_controller.IsBusy || _controller.ThreadId is not { } thread)
        {
            return false;
        }

        _outputWindow?.SetNotice(null); // a branch of our own is not open anywhere else

        if (upto is null)
        {
            var folder = CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.Find(thread)?.Cwd ?? _settings.WorkspacePath;
            await OpenWorkspaceAsync(folder, newThread: true);
            _messages.Clear();
            _streamingAgent = null;
            if (asVersion && _controller.ThreadId is { } fresh)
            {
                BranchStore.Add(string.Empty, thread, fresh);
            }

            RenderConversation();
            return true;
        }

        if (upto.SourceId is not { } uuid || CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.FindFile(thread) is not { } transcript)
        {
            return false;
        }

        var forkId = await Task.Run(() => CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.Fork(transcript, uuid, title));
        if (asVersion)
        {
            BranchStore.Add(uuid, thread, forkId);
        }

        var workspace = CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.Find(forkId)?.Cwd ?? _settings.WorkspacePath;
        await OpenThreadFromSidebarAsync(new ProviderThreadSummary(forkId, title, string.Empty, workspace, DateTimeOffset.Now));
        return true;
    }

    // The version group a prompt belongs to: keyed by the answer before it ("" for the first prompt).
    private string? VersionAnchor(ChatMessage prompt)
    {
        if (!IsClaudeCodeProvider || !prompt.IsUser)
        {
            return null;
        }

        if (AnswerBefore(prompt) is { } before)
        {
            return before.SourceId;
        }

        return ReferenceEquals(_messages.FirstOrDefault(message => message.IsUser), prompt) ? string.Empty : null;
    }

    private (int Index, int Count)? VersionOf(ChatMessage prompt)
    {
        if (VersionAnchor(prompt) is not { } anchor || _controller.ThreadId is not { } thread ||
            BranchStore.Find(anchor, thread) is not { } group)
        {
            return null;
        }

        var index = group.Sessions.FindIndex(s => string.Equals(s, thread, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : (index, group.Sessions.Count);
    }

    // ‹ / › under a prompt: open the neighbouring version (sessions deleted since are forgotten).
    private async Task ShowVersionAsync(ChatMessage prompt, int step)
    {
        if (_controller.IsBusy || VersionAnchor(prompt) is not { } anchor || _controller.ThreadId is not { } thread ||
            BranchStore.Find(anchor, thread) is not { } group)
        {
            return;
        }

        var index = group.Sessions.FindIndex(s => string.Equals(s, thread, StringComparison.OrdinalIgnoreCase));
        var target = index + step;
        if (target < 0 || target >= group.Sessions.Count)
        {
            return;
        }

        var session = group.Sessions[target];
        if (CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.Find(session) is not { } info)
        {
            BranchStore.Forget(session);
            RenderConversation();
            return;
        }

        await OpenThreadFromSidebarAsync(new ProviderThreadSummary(session, info.Title, string.Empty, info.Cwd, info.UpdatedAt));
    }

    // The answer just before a prompt (null when the prompt opens the conversation).
    private ChatMessage? AnswerBefore(ChatMessage prompt)
    {
        var index = _messages.IndexOf(prompt);
        return index <= 0 ? null : _messages.Take(index).LastOrDefault(message => !message.IsUser);
    }

    // Claude Code conversations branch like Claude: editing or retrying goes on from the point
    // before that prompt in a new session, and the original stays as it was.
    private bool CanBranchBefore(ChatMessage prompt) =>
        IsClaudeCodeProvider && !_controller.IsBusy && _controller.ThreadId is not null &&
        AnswerBefore(prompt) is null or { SourceId: not null };

    // Edit under one of your messages: its text back in the box, on a branch from just before it
    // when the provider can (so sending it replaces that message, as in Claude).
    private async Task EditMessageAsync(ChatMessage prompt)
    {
        var text = StripAttachmentLines(prompt.Markdown);
        var isLatestPrompt = ReferenceEquals(_messages.LastOrDefault(message => message.IsUser), prompt);
        if (!isLatestPrompt || _messages.LastOrDefault() is { IsUser: false })
        {
            if (CanBranchBefore(prompt))
            {
                await BranchAtAsync(AnswerBefore(prompt), _outputWindow?.TitleText.Text ?? ConversationView.ChapterTitle(prompt), asVersion: true);
                SetStatus(Tr.Text("Cabang baru dari sebelum pesan itu — kirim untuk melanjutkan.", "New branch from before that message — send to go on."), StatusReadyBrush);
            }
        }

        _outputWindow?.SetInputText(text);
    }

    // Export (header button or /export): the conversation as a Markdown file of your choice.
    private void ExportConversation()
    {
        if (_messages.Count == 0)
        {
            SetStatus(Tr.Text("Belum ada percakapan untuk diekspor.", "Nothing to export yet."), StatusBusyBrush);
            return;
        }

        var title = _outputWindow?.TitleText.Text ?? L("panel.newConversation");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = ConversationExport.FileName(title),
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(_outputWindow) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, ConversationExport.Build(title, _outputWindow?.ContextText.Text ?? string.Empty, _messages));
            SetStatus(Tr.Text("Percakapan diekspor.", "Conversation exported."), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    // Retry under the latest answer: ask again. Claude Code branches from before the prompt (the
    // old answer stays in the original session); other providers send the prompt once more.
    private async Task RetryAnswerAsync(ChatMessage answer)
    {
        if (_controller.IsBusy)
        {
            return;
        }

        var index = _messages.IndexOf(answer);
        var prompt = _messages.Take(index < 0 ? _messages.Count : index).LastOrDefault(message => message.IsUser);
        if (prompt is null || StripAttachmentLines(prompt.Markdown) is not { Length: > 0 } text)
        {
            return;
        }

        if (CanBranchBefore(prompt))
        {
            await BranchAtAsync(AnswerBefore(prompt), _outputWindow?.TitleText.Text ?? ConversationView.ChapterTitle(prompt), asVersion: true);
        }

        await SendCoreAsync(text, [], clearPromptBox: false);
    }

    // A sent message lists its attachments as "- 📎 **name**" lines; those aren't part of the prompt.
    private static string StripAttachmentLines(string markdown) =>
        string.Join("\n", markdown.Replace("\r\n", "\n").Split('\n').Where(line => !line.TrimStart().StartsWith("- 📎", StringComparison.Ordinal))).Trim();

    private void PanelSend(string text)
    {
        if (_controller.IsBusy)
        {
            EnqueuePrompt(text);
            return;
        }

        _ = RunUiTaskAsync(async () =>
        {
            // Optional: a session open in Claude Code gets a fork first, so the two never clash.
            if (_settings.AutoForkElsewhere && IsClaudeCodeProvider && _controller.ThreadId is { } thread &&
                _messages.LastOrDefault(message => !message.IsUser && message.SourceId is not null) is { } latest &&
                await Task.Run(() => CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.ActiveElsewhere(thread, TimeSpan.FromMinutes(15))))
            {
                await BranchAtAsync(latest, _outputWindow?.TitleText.Text ?? ConversationView.ChapterTitle(latest));
                _outputWindow?.SetNotice(null);
            }

            await SendCoreAsync(text, _attachments.ToArray(), clearPromptBox: false);
        });
    }

    // A file link in an answer: open it in VS Code at its line when installed, else show it in
    // Explorer (never run it). Relative paths are the session's folder.
    private void OpenFileLink(string target)
    {
        var match = System.Text.RegularExpressions.Regex.Match(target, @"^(?<path>.+?)(?::(?<line>\d+)(?::\d+)?)?(?:#L(?<line2>\d+))?$");
        var path = match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar);
        var line = match.Groups["line"].Success ? match.Groups["line"].Value : match.Groups["line2"].Success ? match.Groups["line2"].Value : null;
        if (!Path.IsPathRooted(path))
        {
            var folder = (IsClaudeCodeProvider && _controller.ThreadId is { } thread
                ? CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.Find(thread)?.Cwd
                : null) ?? _settings.WorkspacePath;
            path = Path.GetFullPath(Path.Combine(folder, path));
        }

        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
                return;
            }

            if (!File.Exists(path))
            {
                SetStatus(string.Format(Tr.Text("File tidak ditemukan: {0}", "File not found: {0}"), path), StatusErrorBrush);
                return;
            }

            if (FindVsCode() is { } code)
            {
                var start = new System.Diagnostics.ProcessStartInfo(code) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-g");
                start.ArgumentList.Add(line is null ? path : $"{path}:{line}");
                System.Diagnostics.Process.Start(start);
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private static string? FindVsCode()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe");
        if (File.Exists(machine))
        {
            return machine;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir.Trim(), "code.cmd"))
            .FirstOrDefault(File.Exists);
    }

    private void UpdatePanelHeader()
    {
        if (_outputWindow is null)
        {
            return;
        }

        var first = _messages.FirstOrDefault(message => message.IsUser)?.Markdown ?? string.Empty;
        var title = first.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (title.Length > 60)
        {
            title = title[..60].TrimEnd() + "…";
        }

        var model = (ModelCombo.SelectedItem as ProviderModelInfo)?.DisplayName;
        var provider = ((INotchHost)this).ProviderName;
        var chapters = ChapterStore.Load(_controller.ProviderId, _controller.ThreadId);
        _outputWindow.SetChapters(chapters);
        var pinned = chapters.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (!pinned.SetEquals(_outputWindow.Conversation.PinnedKeys))
        {
            _outputWindow.Conversation.PinnedKeys = pinned;
        }

        var models = (ModelCombo.ItemsSource as IEnumerable<ProviderModelInfo>)?
            .Select(m => new ConversationPanel.Choice(m.Model, m.DisplayName, m.Description)).ToArray() ?? [];
        _outputWindow.SetComposerChoices(
            models, (ModelCombo.SelectedItem as ProviderModelInfo)?.Model ?? string.Empty, model is { Length: > 0 } ? model : provider,
            EffortChoices, _settings.ReasoningEffort,
            ModeChoices, _settings.ApprovalPolicy is { Length: > 0 } policy ? policy : "on-request");
        _outputWindow.SetHeader(
            title.Length > 0 ? title : L("panel.newConversation"),
            string.Join(" · ", new[] { provider, model is { Length: > 0 } && model != provider ? model : null, _workspaceLabel }.Where(part => part is { Length: > 0 })));
    }

    private static readonly ConversationPanel.Choice[] EffortChoices =
    [
        new(string.Empty, "Default", "The model's own setting"),
        new("minimal", "Minimal"),
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High")
    ];

    private static readonly ConversationPanel.Choice[] ModeChoices =
    [
        new("on-request", "Auto", "Asks only when the agent wants to go beyond the sandbox"),
        new("untrusted", "Ask", "Asks before anything that isn't plainly safe"),
        new("on-failure", "On failure", "Asks only when a command fails in the sandbox"),
        new("never", "Never ask", "Never stops to ask")
    ];

    private void SelectEffortCombo(string effort) =>
        EffortCombo.SelectedIndex = effort switch
        {
            "minimal" => 1,
            "low" => 2,
            "medium" => 3,
            "high" => 4,
            _ => 0
        };

    // Approval mode from the chat: applied to the open thread right away, like the sandbox picker.
    private async Task ChangeApprovalPolicyAsync(string policy)
    {
        var previous = _settings.ApprovalPolicy;
        _settings.ApprovalPolicy = policy;
        UpdatePanelHeader();
        try
        {
            await _controller.ChangePermissionsAsync(_settings.SandboxMode, policy);
            ApprovalCombo.SelectedIndex = policy switch
            {
                "untrusted" => 0,
                "on-failure" => 1,
                "never" => 3,
                _ => 2
            };
            await _settingsStore.SaveAsync(_settings);
            SetStatus(L("status.permissionUpdated"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            _settings.ApprovalPolicy = previous;
            UpdatePanelHeader();
            ShowError(exception.Message);
        }
    }

    // Win+Alt+C: open the chat, or fold it back when it is already open.
    private void ToggleChatFromHotkey()
    {
        if (_outputWindow is { IsVisible: true })
        {
            _outputWindow.Dismiss();
            return;
        }

        ((INotchHost)this).OpenConversationWindow();
    }

    private double EffectiveResponseFontSize =>
        Math.Clamp(_settings.Appearance.ResponseFontSize + _responseZoomDelta, 10, 24);
}
