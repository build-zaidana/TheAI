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
    private static readonly Regex InlineRegex = new(
        @"(?<code>`[^`]+`)|(?<bold>\*\*[^*]+\*\*)|(?<italic>(?<![\w*])[\*_][^\*_]+[\*_](?![\w]))",
        RegexOptions.Compiled);

    public static FlowDocument Build(string markdown, MarkdownStyle style)
    {
        var document = new FlowDocument
        {
            FontSize = style.FontSize,
            Foreground = style.Primary,
            PagePadding = new Thickness(0),
            LineHeight = style.FontSize * 1.5,
            TextAlignment = TextAlignment.Left
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            // Blok kode berpagar ```
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
                index++; // lewati pagar penutup
                document.Blocks.Add(CreateCodeBlock(language, code.ToString().TrimEnd('\n'), style));
                continue;
            }

            // Baris kosong
            if (line.Trim().Length == 0)
            {
                index++;
                continue;
            }

            // Heading
            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0, level <= 2 ? 10 : 7, 0, 4),
                    FontSize = style.FontSize + (level <= 1 ? 6 : level == 2 ? 4 : 2),
                    FontWeight = FontWeights.SemiBold
                };
                AddInlines(paragraph.Inlines, headingMatch.Groups[2].Value, style);
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }

            // Blockquote
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.AppendLine(lines[index].TrimStart().TrimStart('>').Trim());
                    index++;
                }
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(12, 4, 6, 4),
                    BorderBrush = style.QuoteBar,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Foreground = style.Muted
                };
                AddInlines(paragraph.Inlines, quote.ToString().Trim(), style);
                document.Blocks.Add(paragraph);
                continue;
            }

            // List (unordered atau ordered)
            if (IsListItem(line, out _))
            {
                var ordered = Regex.IsMatch(line.TrimStart(), @"^\d+[\.\)]\s");
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(18, 0, 0, 0)
                };
                while (index < lines.Length && IsListItem(lines[index], out var content))
                {
                    var item = new ListItem();
                    var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInlines(paragraph.Inlines, content, style);
                    item.Blocks.Add(paragraph);
                    list.ListItems.Add(item);
                    index++;
                }
                document.Blocks.Add(list);
                continue;
            }

            // Paragraf biasa (gabungkan baris berturut hingga baris kosong / blok lain)
            var buffer = new StringBuilder();
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && !Regex.IsMatch(lines[index], @"^#{1,6}\s")
                   && !lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal)
                   && !IsListItem(lines[index], out _))
            {
                if (buffer.Length > 0)
                {
                    buffer.Append(' ');
                }
                buffer.Append(lines[index].Trim());
                index++;
            }

            var body = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
            AddInlines(body.Inlines, buffer.ToString(), style);
            document.Blocks.Add(body);
        }

        return document;
    }

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

            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var heading = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level <= 2 ? 8 : 5, 0, 3),
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
                var ordered = Regex.IsMatch(line.TrimStart(), @"^\d+[\.\)]\s");
                var number = 1;
                while (index < lines.Length && IsListItem(lines[index], out var content))
                {
                    var row = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(2, 1, 0, 1),
                        FontSize = style.FontSize,
                        Foreground = style.Primary
                    };
                    row.Inlines.Add(new Run(ordered ? $"{number}.  " : "•  ") { Foreground = style.Muted });
                    AddInlines(row.Inlines, content, style);
                    root.Children.Add(row);
                    index++;
                    number++;
                }
                continue;
            }

            var buffer = new StringBuilder();
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && !Regex.IsMatch(lines[index], @"^#{1,6}\s")
                   && !lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal)
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
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = style.FontSize,
                Foreground = style.Primary,
                LineHeight = style.FontSize * 1.45,
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
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Children.Add(new TextBlock
            {
                Text = language.ToLowerInvariant(),
                Foreground = style.Muted,
                FontSize = style.FontSize - 3,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = style.CodeFont,
            FontSize = style.FontSize - 0.5,
            Foreground = style.CodeForeground,
            TextWrapping = style.CodeWrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };

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

    private static Block CreateCodeBlock(string language, string code, MarkdownStyle style)
    {
        var paragraph = new Paragraph
        {
            Background = style.CodeBackground,
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 6, 0, 6),
            FontFamily = style.CodeFont,
            FontSize = style.FontSize - 0.5,
            Foreground = style.CodeForeground
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            paragraph.Inlines.Add(new Run(language.ToLowerInvariant())
            {
                Foreground = style.Muted,
                FontSize = style.FontSize - 3
            });
            paragraph.Inlines.Add(new LineBreak());
        }

        var codeLines = code.Split('\n');
        for (var i = 0; i < codeLines.Length; i++)
        {
            paragraph.Inlines.Add(new Run(codeLines[i]));
            if (i < codeLines.Length - 1)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
        }

        return paragraph;
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

            if (match.Groups["code"].Success)
            {
                var inner = match.Value.Trim('`');
                target.Add(new Run(inner)
                {
                    FontFamily = style.CodeFont,
                    Foreground = style.InlineCodeForeground,
                    Background = style.CodeBackground
                });
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
