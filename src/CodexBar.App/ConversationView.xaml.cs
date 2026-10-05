using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CodexBar.App;

/// <summary>Satu pesan dalam percakapan (gelembung chat).</summary>
public sealed class ChatMessage
{
    public bool IsUser { get; init; }
    public string Markdown { get; set; } = string.Empty;

    /// <summary>Proses berpikir (reasoning) agent untuk pesan ini, bila ada.</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>The work before the answer (tool calls, text in between), as in Claude Code.</summary>
    public IReadOnlyList<CodexBar.Providers.ProviderActivityStep> Steps { get; set; } = [];

    /// <summary>Where the message is in its source (e.g. the Claude Code transcript), for forking.</summary>
    public string? SourceId { get; init; }

    /// <summary>Images the person sent with the message (local files), shown above the bubble.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];
}

/// <summary>Warna gelembung + gaya markdown untuk merender percakapan.</summary>
public sealed record ChatTheme(
    MarkdownStyle Markdown,
    Brush UserBubble,
    Brush UserText,
    Brush AgentBubble,
    Brush Placeholder,
    Brush ReasoningText,
    bool ShowReasoning);

/// <summary>
/// Menampilkan percakapan sebagai gelembung chat: pesan pengguna rata-kanan,
/// respons agen rata-kiri (mirip Codex / Claude Code).
/// </summary>
public partial class ConversationView : System.Windows.Controls.UserControl
{
    // Floating companion harus tetap ringan; history lama dimuat atas permintaan.
    private const int MessagePageSize = 20;
    private readonly List<FrameworkElement> _bubbles = new();
    private ChatTheme? _theme;
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();
    private int _visibleMessageCount = MessagePageSize;
    private int _previousMessageCount;
    private ChatMessage? _last;
    private Border? _lastBubble;
    private Border? _lastAnswerHost;
    private Border? _lastActivityHost;
    private Border? _lastSummaryHost;
    private FrameworkElement? _lastActions;
    private Button? _lastRetryButton;

    /// <summary>Retry on the last answer: send the prompt before it again.</summary>
    public event EventHandler<ChatMessage>? RetryRequested;

    /// <summary>Edit on one of the person's messages: put its text back in the input.</summary>
    public event EventHandler<string>? EditRequested;

    /// <summary>"Fork from here", "Pin as chapter" and "Read aloud" from an answer's ⋯ menu.</summary>
    public event EventHandler<ChatMessage>? ForkRequested;
    public event EventHandler<ChatMessage>? PinToggled;
    public event EventHandler<ChatMessage>? ReadAloudRequested;

    /// <summary>Whether an answer can be forked from (the host knows its provider).</summary>
    public Func<ChatMessage, bool>? CanFork { get; set; }

    /// <summary>Keys of the pinned messages (chapters), shown as dividers above them.</summary>
    public IReadOnlySet<string> PinnedKeys { get; set; } = new HashSet<string>();

    /// <summary>The message being read aloud (its key), for the menu label.</summary>
    public Func<string?>? ReadingKey { get; set; }

    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.Ordinal);

    /// <summary>A stable key for a message: its source id, or a hash of its text.</summary>
    public static string KeyOf(ChatMessage message) =>
        message.SourceId ?? "h" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes((message.IsUser ? "u:" : "a:") + message.Markdown)))[..16];

    /// <summary>A chapter title: the message's first line without markdown marks, shortened.</summary>
    public static string ChapterTitle(ChatMessage message)
    {
        var line = message.Markdown.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '>', '-', '*', ' ').Replace("**", string.Empty).Replace("`", string.Empty).Trim())
            .FirstOrDefault(l => l.Length > 0) ?? Tr.Text("Chapter", "Chapter");
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    /// <summary>Scrolls a pinned message into view (loading older messages when needed).</summary>
    public void ScrollToKey(string key)
    {
        if (!_anchors.ContainsKey(key) && _messages.Any(m => KeyOf(m) == key))
        {
            _visibleMessageCount = _messages.Count;
            RenderVisibleMessages();
            UpdateLayout();
        }

        if (_anchors.TryGetValue(key, out var anchor))
        {
            anchor.BringIntoView(new Rect(0, 0, 1, Math.Max(1, PART_Scroll.ViewportHeight - 40)));
        }
    }
    private IReadOnlyList<CodexBar.Providers.ProviderActivityStep>? _lastSteps;
    private Border? _lastReasoningSection;
    private TextBlock? _lastReasoningBody;

    public ConversationView()
    {
        InitializeComponent();
        PART_Scroll.ScrollChanged += (s, e) => ScrollChanged?.Invoke(s, e);
    }

    public event ScrollChangedEventHandler? ScrollChanged;

    public void ScrollToEnd() => PART_Scroll.ScrollToEnd();

    public void ClearVisuals()
    {
        MessagesPanel.Children.Clear();
        _bubbles.Clear();
        _last = null;
        _lastBubble = null;
        _lastAnswerHost = null;
        _lastReasoningSection = null;
        _lastReasoningBody = null;
    }

    public void Render(IReadOnlyList<ChatMessage> messages, ChatTheme theme)
    {
        if (messages.Count < _previousMessageCount || messages.Count == 0)
        {
            _visibleMessageCount = MessagePageSize;
        }

        _messages = messages;
        _previousMessageCount = messages.Count;
        _theme = theme;
        RenderVisibleMessages();
    }

    private void RenderVisibleMessages()
    {
        if (_theme is null)
        {
            return;
        }

        var theme = _theme;
        MessagesPanel.Children.Clear();
        _bubbles.Clear();
        _last = null;
        _lastBubble = null;
        _lastAnswerHost = null;
        _lastReasoningSection = null;
        _lastReasoningBody = null;

        if (_messages.Count == 0)
        {
            MessagesPanel.Children.Add(new TextBlock
            {
                Text = CodexBar.Core.Localization.Instance["conversation.placeholder"],
                Foreground = theme.Placeholder,
                Margin = new Thickness(6, 8, 6, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var firstVisibleIndex = Math.Max(0, _messages.Count - _visibleMessageCount);
        if (firstVisibleIndex > 0)
        {
            var older = new Button
            {
                Content = string.Format(CodexBar.Core.Localization.Instance["conversation.older"],
                    Math.Min(MessagePageSize, firstVisibleIndex)),
                Margin = new Thickness(60, 4, 60, 10),
                Padding = new Thickness(10, 6, 10, 6),
                Background = Brushes.Transparent,
                Foreground = theme.ReasoningText,
                BorderBrush = theme.ReasoningText,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            older.Click += (_, _) =>
            {
                var oldOffset = PART_Scroll.VerticalOffset;
                var oldExtent = PART_Scroll.ExtentHeight;
                _visibleMessageCount += MessagePageSize;
                RenderVisibleMessages();
                UpdateLayout();
                PART_Scroll.ScrollToVerticalOffset(oldOffset + Math.Max(0, PART_Scroll.ExtentHeight - oldExtent));
            };
            MessagesPanel.Children.Add(older);
        }

        var maxWidth = BubbleMaxWidth();
        _anchors.Clear();
        for (var i = firstVisibleIndex; i < _messages.Count; i++)
        {
            var message = _messages[i];
            if (PinnedKeys.Count > 0 && PinnedKeys.Contains(KeyOf(message)))
            {
                var divider = ChapterDivider(ChapterTitle(message), theme);
                MessagesPanel.Children.Add(divider);
                _anchors[KeyOf(message)] = divider;
            }

            var bubble = BuildBubble(message, theme, maxWidth, isLast: i == _messages.Count - 1);
            MessagesPanel.Children.Add(bubble);
            _bubbles.Add(bubble);
            _last = message;
            _lastBubble = bubble;
        }
    }

    /// <summary>Render ulang isi gelembung terakhir (dipakai saat respons streaming).</summary>
    public void UpdateLast(ChatMessage message)
    {
        if (_theme is null || _last is null || _lastBubble is null || _last.IsUser)
        {
            return;
        }

        _last.Markdown = message.Markdown;
        _last.Reasoning = message.Reasoning;
        _last.Steps = message.Steps;

        if (!ReferenceEquals(_lastSteps, message.Steps))
        {
            // New tool calls: rebuild the work list and the changed-files summary.
            _lastSteps = message.Steps;
            FillActivity(message, _theme);
        }

        if (_lastActions is not null)
        {
            _lastActions.Visibility = message.Markdown.Length == 0 && message.Steps.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (_lastAnswerHost is not null)
        {
            _lastAnswerHost.Child = MarkdownRenderer.BuildPanel(message.Markdown, _theme.Markdown);
        }

        if (_lastReasoningBody is not null && _lastReasoningSection is not null)
        {
            _lastReasoningBody.Text = message.Reasoning;
            _lastReasoningSection.Visibility = _theme.ShowReasoning && message.Reasoning.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private Border BuildBubble(ChatMessage message, ChatTheme theme, double maxWidth, bool isLast)
    {
        FrameworkElement inner = message.IsUser
            ? BuildUserText(message.Markdown, theme)
            : BuildAgentContent(message, theme, isLast);

        if (message.IsUser)
        {
            return BuildUserMessage(message, theme, inner, maxWidth);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = message.IsUser ? theme.UserBubble : theme.AgentBubble,
            // Like Claude Code: the person's message in a soft bubble, the answer as plain text
            // running the full width.
            Padding = message.IsUser ? new Thickness(14, 9, 14, 9) : new Thickness(4, 2, 4, 2),
            Margin = message.IsUser ? new Thickness(44, 6, 2, 10) : new Thickness(2, 2, 8, 8),
            HorizontalAlignment = message.IsUser
                ? System.Windows.HorizontalAlignment.Right
                : System.Windows.HorizontalAlignment.Left,
            // The answer runs the full column; the person's bubble keeps some room on the left.
            MaxWidth = message.IsUser ? maxWidth : double.PositiveInfinity,
            Child = inner
        };
    }

    // A pinned chapter: a hairline with its title, as Claude Code marks one.
    private static FrameworkElement ChapterDivider(string title, ChatTheme theme)
    {
        var grid = new Grid { Margin = new Thickness(2, 14, 2, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Math.Max(11, theme.Markdown.FontSize - 2),
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.ReasoningText,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var line = new Border { Height = 1, Background = theme.Markdown.CodeBorder, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    // The person's text; attached files (sent as "- 📎 **name**" lines) as a quiet list under it.
    private static FrameworkElement BuildUserText(string markdown, ChatTheme theme)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var files = lines.Where(line => line.TrimStart().StartsWith("- 📎", StringComparison.Ordinal))
            .Select(line => line.TrimStart()[4..].Trim().Trim('*').Trim())
            .ToArray();
        var text = string.Join("\n", lines.Where(line => !line.TrimStart().StartsWith("- 📎", StringComparison.Ordinal))).Trim();
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.UserText,
            FontSize = theme.Markdown.FontSize
        };
        if (files.Length == 0)
        {
            return block;
        }

        var stack = new StackPanel();
        if (text.Length > 0)
        {
            stack.Children.Add(block);
        }

        stack.Children.Add(new TextBlock
        {
            Text = string.Join("   ", files.Select(name => "📎 " + name)),
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.ReasoningText,
            FontSize = Math.Max(11, theme.Markdown.FontSize - 2),
            Margin = new Thickness(0, text.Length > 0 ? 4 : 0, 0, 0)
        });
        return stack;
    }

    // Like Claude: pasted images as thumbnails above the person's bubble, and copy / edit under
    // it while the pointer is over the message.
    private Border BuildUserMessage(ChatMessage message, ChatTheme theme, FrameworkElement text, double maxWidth)
    {
        var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var images = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        foreach (var path in message.Images)
        {
            if (ImageThumb.Create(path, 120) is { } thumb)
            {
                thumb.Margin = new Thickness(6, 0, 0, 6);
                images.Children.Add(thumb);
            }
        }

        stack.Children.Add(images);
        if (message.Markdown.Length > 0)
        {
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = theme.UserBubble,
                Padding = new Thickness(14, 9, 14, 9),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Child = text
            });
        }

        var loc = CodexBar.Core.Localization.Instance;
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = 0
        };
        actions.Children.Add(MessageActions.Copy(() => message.Markdown, loc["conversation.copyName"], theme.ReasoningText, theme.Markdown.Primary));
        var edit = MessageActions.Create(MessageActions.EditIcon, Tr.Text("Edit", "Edit"), theme.ReasoningText, theme.Markdown.Primary);
        edit.Click += (_, _) => EditRequested?.Invoke(this, message.Markdown);
        actions.Children.Add(edit);
        if (message.Markdown.Length > 0)
        {
            stack.Children.Add(actions);
        }

        var host = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(44, 6, 2, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            MaxWidth = maxWidth,
            Child = stack
        };
        host.MouseEnter += (_, _) => actions.Opacity = 1;
        host.MouseLeave += (_, _) => actions.Opacity = 0;
        return host;
    }

    private FrameworkElement BuildAgentContent(ChatMessage message, ChatTheme theme, bool isLast)
    {
        var stack = new StackPanel();
        var loc = CodexBar.Core.Localization.Instance;

        // Bagian "Berpikir" (reasoning) yang bisa diciutkan.
        var body = new TextBlock
        {
            Text = message.Reasoning,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            FontSize = Math.Max(11, theme.Markdown.FontSize - 1.5),
            Foreground = theme.ReasoningText,
            Margin = new Thickness(0, 6, 0, 2),
            Visibility = isLast ? Visibility.Visible : Visibility.Collapsed
        };
        var header = new Button
        {
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = Math.Max(11, theme.Markdown.FontSize - 2),
            Foreground = theme.ReasoningText,
            Content = (body.Visibility == Visibility.Visible ? "▾  " : "▸  ") + loc["conversation.thinking"],
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(
            header, loc["conversation.thinkingName"]);
        header.Click += (_, _) =>
        {
            body.Visibility = body.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            header.Content = (body.Visibility == Visibility.Visible ? "▾  " : "▸  ") +
                CodexBar.Core.Localization.Instance["conversation.thinking"];
        };

        var reasoningStack = new StackPanel();
        reasoningStack.Children.Add(header);
        reasoningStack.Children.Add(body);
        var reasoningSection = new Border
        {
            BorderBrush = theme.ReasoningText,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 6),
            Opacity = 0.85,
            Child = reasoningStack,
            Visibility = theme.ShowReasoning && message.Reasoning.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        stack.Children.Add(reasoningSection);

        var activityHost = new Border();
        stack.Children.Add(activityHost);

        var answerHost = new Border { Child = MarkdownRenderer.BuildPanel(message.Markdown, theme.Markdown) };
        stack.Children.Add(answerHost);

        var summaryHost = new Border();
        stack.Children.Add(summaryHost);

        // Under the answer, as in Claude: copy, and retry on the latest answer.
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(-4, 2, 0, 0)
        };
        actions.Children.Add(MessageActions.Copy(() => message.Markdown, loc["conversation.copyName"], theme.ReasoningText, theme.Markdown.Primary));
        var retry = MessageActions.Create(MessageActions.RetryIcon, Tr.Text("Coba lagi", "Retry"), theme.ReasoningText, theme.Markdown.Primary);
        retry.Click += (_, _) => RetryRequested?.Invoke(this, message);
        retry.Visibility = isLast ? Visibility.Visible : Visibility.Collapsed;
        actions.Children.Add(retry);
        var more = MessageActions.Create(MessageActions.MoreIcon, Tr.Text("Lainnya", "More"), theme.ReasoningText, theme.Markdown.Primary);
        more.Click += (_, _) =>
        {
            var key = KeyOf(message);
            var items = new List<(string, Action)>();
            if (CanFork?.Invoke(message) == true)
            {
                items.Add(("Fork from here", () => ForkRequested?.Invoke(this, message)));
            }

            items.Add((PinnedKeys.Contains(key) ? "Unpin chapter" : "Pin as chapter", () => PinToggled?.Invoke(this, message)));
            items.Add((ReadingKey?.Invoke() == key ? "Stop reading" : "Read aloud", () => ReadAloudRequested?.Invoke(this, message)));
            MessageActions.ShowMenu(more, items);
        };
        actions.Children.Add(more);
        if (message.Markdown.Length == 0 && message.Steps.Count > 0)
        {
            actions.Visibility = Visibility.Collapsed; // still working: nothing to copy yet
        }
        stack.Children.Add(actions);

        // Simpan referensi gelembung agent terbaru untuk update streaming in-place.
        _lastAnswerHost = answerHost;
        _lastActivityHost = activityHost;
        _lastSummaryHost = summaryHost;
        _lastActions = actions;
        _lastRetryButton = retry;
        _lastSteps = message.Steps;
        FillActivity(message, theme);
        _lastReasoningSection = reasoningSection;
        _lastReasoningBody = body;

        return stack;
    }

    // The work list above the answer and the changed-files summary under it (Claude Code style).
    private void FillActivity(ChatMessage message, ChatTheme theme)
    {
        if (_lastActivityHost is not null)
        {
            _lastActivityHost.Child = message.Steps.Count > 0 ? ActivityList.Build(message.Steps, theme.Markdown) : null;
        }

        if (_lastSummaryHost is not null)
        {
            _lastSummaryHost.Child = ActivityList.BuildSummary(message.Steps, theme.Markdown);
        }
    }

    private double BubbleMaxWidth(double? width = null)
    {
        // The new size when resizing (the viewport still reports the old one at that point).
        var available = width ?? (PART_Scroll.ViewportWidth > 0 ? PART_Scroll.ViewportWidth : ActualWidth);
        return available > 80 ? available * 0.82 : 320;
    }

    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        var maxWidth = BubbleMaxWidth(e.NewSize.Width);
        foreach (var bubble in _bubbles)
        {
            if (!double.IsPositiveInfinity(bubble.MaxWidth))
            {
                bubble.MaxWidth = maxWidth;
            }
        }
    }
}
