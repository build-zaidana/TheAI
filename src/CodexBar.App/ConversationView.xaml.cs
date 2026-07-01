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

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = message.IsUser ? theme.UserBubble : theme.AgentBubble,
            Padding = new Thickness(13, 9, 13, 9),
            Margin = message.IsUser ? new Thickness(44, 3, 2, 3) : new Thickness(2, 3, 44, 3),
            HorizontalAlignment = message.IsUser
                ? System.Windows.HorizontalAlignment.Right
                : System.Windows.HorizontalAlignment.Left,
            MaxWidth = maxWidth,
            Child = inner
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

        var answerHost = new Border { Child = MarkdownRenderer.BuildPanel(message.Markdown, theme.Markdown) };
        stack.Children.Add(answerHost);

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
        stack.Children.Add(copyButton);

        // Simpan referensi gelembung agent terbaru untuk update streaming in-place.
        _lastAnswerHost = answerHost;
        _lastReasoningSection = reasoningSection;
        _lastReasoningBody = body;

        return stack;
    }

    private double BubbleMaxWidth()
    {
        var width = PART_Scroll.ViewportWidth > 0 ? PART_Scroll.ViewportWidth : ActualWidth;
        return width > 80 ? width * 0.82 : 320;
    }

    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        var maxWidth = BubbleMaxWidth();
        foreach (var bubble in _bubbles)
        {
            bubble.MaxWidth = maxWidth;
        }
    }
}
