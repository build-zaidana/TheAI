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
    private Button? _lastCopyButton;
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
        for (var i = firstVisibleIndex; i < _messages.Count; i++)
        {
            var message = _messages[i];
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

        if (_lastCopyButton is not null)
        {
            _lastCopyButton.Visibility = message.Markdown.Length == 0 && message.Steps.Count > 0
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
            ? new TextBlock
            {
                Text = message.Markdown,
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme.UserText,
                FontSize = theme.Markdown.FontSize
            }
            : BuildAgentContent(message, theme, isLast);

        if (message.IsUser && message.Images.Count > 0)
        {
            return BuildUserWithImages(message, theme, inner, maxWidth);
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

    // Like Claude Code: the pasted images as thumbnails above the person's bubble.
    private static Border BuildUserWithImages(ChatMessage message, ChatTheme theme, FrameworkElement text, double maxWidth)
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

        return new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(44, 6, 2, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            MaxWidth = maxWidth,
            Child = stack
        };
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

        // Tombol salin jawaban (menyalin teks markdown pesan ini).
        var copyButton = new Button
        {
            Content = loc["conversation.copy"],
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = Math.Max(11, theme.Markdown.FontSize - 2.5),
            Foreground = theme.ReasoningText,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(copyButton, loc["conversation.copyName"]);
        copyButton.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(message.Markdown);
                copyButton.Content = CodexBar.Core.Localization.Instance["conversation.copied"];
            }
            catch
            {
                copyButton.Content = CodexBar.Core.Localization.Instance["conversation.copyFailed"];
            }
        };
        if (message.Markdown.Length == 0 && message.Steps.Count > 0)
        {
            copyButton.Visibility = Visibility.Collapsed; // still working: nothing to copy yet
        }
        stack.Children.Add(copyButton);

        // Simpan referensi gelembung agent terbaru untuk update streaming in-place.
        _lastAnswerHost = answerHost;
        _lastActivityHost = activityHost;
        _lastSummaryHost = summaryHost;
        _lastCopyButton = copyButton;
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
