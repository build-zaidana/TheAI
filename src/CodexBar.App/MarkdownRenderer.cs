using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CodexBar.App;

/// <summary>
/// Mengubah teks Markdown sederhana menjadi <see cref="FlowDocument"/> agar dapat dirender
/// di <c>RichTextBox</c> read-only: teks bisa diseleksi sekaligus diformat (heading, bold,
/// italic, inline code, list, blockquote, dan blok kode berpagar).
/// </summary>
public sealed record MarkdownStyle(
    Brush Primary,
    Brush Muted,
    Brush InlineCodeForeground,
    Brush CodeForeground,
    Brush CodeBackground,
    Brush CodeBorder,
    Brush QuoteBar,
    FontFamily CodeFont,
    double FontSize,
    bool CodeWrap);

public static class MarkdownRenderer
{
    /// <summary>A file link in an answer was clicked (its target, e.g. "src/Foo.cs:42").</summary>
    public static event Action<string>? FileLinkClicked;

    private static readonly Regex InlineRegex = new(
        @"(?<link>\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\))|(?<code>`[^`]+`)|(?<bold>\*\*[^*]+\*\*)|(?<strike>~~[^~]+~~)|(?<italic>(?<![\w*])[\*_][^\*_]+[\*_](?![\w]))",
        RegexOptions.Compiled);

    /// <summary>
    /// Versi panel: merender Markdown menjadi kumpulan elemen WPF (TextBlock/Border) yang
    /// auto-tinggi &amp; membungkus, cocok dipakai sebagai isi gelembung chat.
    /// </summary>
    public static FrameworkElement BuildPanel(string markdown, MarkdownStyle style)
    {
        var root = new StackPanel();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var language = line.TrimStart()[3..].Trim();
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                index++;
                root.Children.Add(CreateCodePanel(language, code.ToString().TrimEnd('\n'), style));
                continue;
            }

            if (line.Trim().Length == 0)
            {
                index++;
                continue;
            }

            if (IsRule(line))
            {
                root.Children.Add(new Border
                {
                    Height = 1,
                    Background = style.CodeBorder,
                    Margin = new Thickness(0, 10, 0, 14)
                });
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                var tableLines = new List<string>();
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    tableLines.Add(lines[index]);
                    index++;
                }
                root.Children.Add(CreateTable(tableLines, style));
                continue;
            }

            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var heading = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 6),
                    FontSize = style.FontSize + (level <= 1 ? 6 : level == 2 ? 4 : 2),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = style.Primary
                };
                AddInlines(heading.Inlines, headingMatch.Groups[2].Value, style);
                root.Children.Add(heading);
                index++;
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.AppendLine(lines[index].TrimStart().TrimStart('>').Trim());
                    index++;
                }
                var quoteText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = style.FontSize, Foreground = style.Muted };
                AddInlines(quoteText.Inlines, quote.ToString().Trim(), style);
                root.Children.Add(new Border
                {
                    BorderBrush = style.QuoteBar,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 4, 2),
                    Margin = new Thickness(0, 4, 0, 4),
                    Child = quoteText
                });
                continue;
            }

            if (IsListItem(line, out _))
            {
                FrameworkElement? lastRow = null;
                while (index < lines.Length && IsListItem(lines[index], out var content))
                {
                    var item = lines[index];
                    var level = Math.Min(3, IndentOf(item) / 2);
                    var numbered = Regex.Match(item.TrimStart(), @"^(\d+)[\.\)]\s");
                    string marker;
                    if (Regex.Match(content, @"^\[( |x|X)\]\s+") is { Success: true } task)
                    {
                        // - [ ] / - [x]: a checkbox, like Claude Code's task lists.
                        marker = task.Groups[1].Value == " " ? "☐" : "☑";
                        content = content[task.Length..];
                    }
                    else
                    {
                        marker = numbered.Success ? numbered.Groups[1].Value + "." : (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
                    }

                    // Marker in its own column, so wrapped lines hang under the text (as in Claude Code);
                    // nested items step in one marker width per level.
                    var row = new Grid { Margin = new Thickness(4 + level * 20, 0, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(numbered.Success ? 24 : 18) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var markerText = new TextBlock
                    {
                        Text = marker,
                        FontSize = style.FontSize,
                        Foreground = numbered.Success ? style.Primary : style.Muted,
                        LineHeight = style.FontSize * 1.6,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                    };
                    if (marker is "☐" or "☑")
                    {
                        // Symbol font: the ballot boxes stay monochrome instead of turning into emoji.
                        markerText.FontFamily = new FontFamily("Segoe UI Symbol");
                    }
                    row.Children.Add(markerText);
                    var text = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = style.FontSize,
                        Foreground = style.Primary,
                        LineHeight = style.FontSize * 1.6,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                    };
                    AddInlines(text.Inlines, content, style);
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    root.Children.Add(row);
                    lastRow = row;
                    index++;
                }

                if (lastRow is not null)
                {
                    var margin = lastRow.Margin;
                    lastRow.Margin = new Thickness(margin.Left, 0, 0, Math.Round(style.FontSize * 0.7));
                }
                continue;
            }

            var buffer = new StringBuilder();
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && !Regex.IsMatch(lines[index], @"^#{1,6}\s")
                   && !lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal)
                   && !IsRule(lines[index])
                   && !IsTableStart(lines, index)
                   && !IsListItem(lines[index], out _))
            {
                if (buffer.Length > 0)
                {
                    buffer.Append(' ');
                }
                buffer.Append(lines[index].Trim());
                index++;
            }

            var paragraph = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                // Reading rhythm of Claude Code's chat: 1.6 line height, a clear gap between paragraphs.
                Margin = new Thickness(0, 0, 0, Math.Round(style.FontSize * 0.7)),
                FontSize = style.FontSize,
                Foreground = style.Primary,
                LineHeight = style.FontSize * 1.6,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            AddInlines(paragraph.Inlines, buffer.ToString(), style);
            root.Children.Add(paragraph);
        }

        return root;
    }

    private static FrameworkElement CreateCodePanel(string language, string code, MarkdownStyle style)
    {
        var content = new StackPanel();

        // Header: optional language label (left) + a copy-to-clipboard button (right).
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(language))
        {
            header.Children.Add(new TextBlock
            {
                Text = language.ToLowerInvariant(),
                Foreground = style.Muted,
                FontSize = style.FontSize - 3,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var copyButton = MessageActions.Copy(() => code, CodexBar.Core.Localization.Instance["code.copy"], style.Muted, style.Primary, 22);
        copyButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        copyButton.Margin = new Thickness(0, -4, -6, 0);
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);
        content.Children.Add(header);

        var codeText = new TextBlock
        {
            FontFamily = style.CodeFont,
            FontSize = style.FontSize - 0.5,
            Foreground = style.CodeForeground,
            TextWrapping = style.CodeWrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        CodeHighlighter.Fill(codeText.Inlines, code, language, IsDark(style.Primary));

        FrameworkElement codeHost = codeText;
        if (!style.CodeWrap)
        {
            codeHost = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = codeText
            };
        }

        content.Children.Add(codeHost);

        return new Border
        {
            Background = style.CodeBackground,
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Child = content
        };
    }

    private static int IndentOf(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }

    private static bool IsRule(string line) => Regex.IsMatch(line, @"^\s{0,3}([-*_])(\s*\1){2,}\s*$");

    // | a | b |  followed by  |---|:--:|
    private static bool IsTableStart(string[] lines, int index) =>
        index + 1 < lines.Length
        && lines[index].Trim().StartsWith('|')
        && Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

    private static string[] SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    // A Markdown table as Claude Code shows it: bold header, hairlines between rows, columns
    // sized by their content and wrapping when space runs out.
    private static FrameworkElement CreateTable(List<string> tableLines, MarkdownStyle style)
    {
        var header = SplitRow(tableLines[0]);
        var aligns = SplitRow(tableLines[1]).Select(spec =>
            spec.StartsWith(':') && spec.EndsWith(':') ? TextAlignment.Center
            : spec.EndsWith(':') ? TextAlignment.Right
            : TextAlignment.Left).ToArray();
        var rows = new List<string[]> { header };
        rows.AddRange(tableLines.Skip(2).Select(SplitRow));
        var columns = rows.Max(row => row.Length);

        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            var longest = rows.Max(row => c < row.Length ? row[c].Length : 0);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(longest, 4, 40), GridUnitType.Star) });
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var cell = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = style.FontSize - 0.5,
                    Foreground = style.Primary,
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    TextAlignment = c < aligns.Length ? aligns[c] : TextAlignment.Left,
                    LineHeight = (style.FontSize - 0.5) * 1.5,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };
                AddInlines(cell.Inlines, c < rows[r].Length ? rows[r][c] : string.Empty, style);
                var box = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    BorderBrush = style.CodeBorder,
                    BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1),
                    Background = r == 0 ? style.CodeBackground : System.Windows.Media.Brushes.Transparent,
                    Child = cell
                };
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        return new Border
        {
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 12),
            ClipToBounds = true,
            Child = grid
        };
    }

    private static bool IsDark(Brush text) =>
        text is SolidColorBrush { Color: var c } && (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 128;

    private static bool IsListItem(string line, out string content)
    {
        var trimmed = line.TrimStart();
        var match = Regex.Match(trimmed, @"^([-*+]|\d+[\.\)])\s+(.*)$");
        if (match.Success)
        {
            content = match.Groups[2].Value;
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static void AddInlines(InlineCollection target, string text, MarkdownStyle style)
    {
        var cursor = 0;
        foreach (Match match in InlineRegex.Matches(text))
        {
            if (match.Index > cursor)
            {
                target.Add(new Run(text[cursor..match.Index]));
            }

            if (match.Groups["link"].Success)
            {
                // [text](url): web links open in the browser; file links show as link text.
                var url = match.Groups["lurl"].Value;
                var label = new Run(match.Groups["ltext"].Value);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    var link = new Hyperlink(label) { NavigateUri = uri, Foreground = style.InlineCodeForeground };
                    link.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                        }
                        catch (Exception)
                        {
                        }
                    };
                    target.Add(link);
                }
                else
                {
                    // A file link ([Foo.cs](src/Foo.cs:42)): the host opens it in the editor.
                    var link = new Hyperlink(label) { Foreground = style.InlineCodeForeground, ToolTip = url };
                    var target2 = Uri.UnescapeDataString(url);
                    link.Click += (_, _) => FileLinkClicked?.Invoke(target2);
                    target.Add(link);
                }
            }
            else if (match.Groups["code"].Success)
            {
                var inner = match.Value.Trim('`');
                target.Add(new Run(inner)
                {
                    FontFamily = style.CodeFont,
                    Foreground = style.InlineCodeForeground,
                    Background = style.CodeBackground
                });
            }
            else if (match.Groups["strike"].Success)
            {
                var struck = new Run(match.Value.Trim('~'));
                struck.TextDecorations = TextDecorations.Strikethrough;
                target.Add(struck);
            }
            else if (match.Groups["bold"].Success)
            {
                target.Add(new Bold(new Run(match.Value.Trim('*'))));
            }
            else if (match.Groups["italic"].Success)
            {
                target.Add(new Italic(new Run(match.Value.Trim('*', '_'))));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            target.Add(new Run(text[cursor..]));
        }
    }
}
