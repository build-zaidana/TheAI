using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CodexBar.App;

/// <summary>
/// The agent's work before its answer, the way Claude Code shows it: the text written between
/// tool calls as ordinary paragraphs, and each run of tool calls folded into one muted line
/// ("Created a file, ran 6 commands, read 3 files, and 3 more actions  +421 −7  ›") that opens
/// into a framed list of rows ("Edited Foo.cs", "Ran a command", … with "Failed" on errors).
/// </summary>
public static class ActivityList
{
    private const int VisibleSteps = 8;

    private static readonly Brush FailedBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x5E));

    public static FrameworkElement Build(IReadOnlyList<ProviderActivityStep> steps, MarkdownStyle style)
    {
        // Like Claude Code: the text before the first tool call and the agent's progress notes
        // read as paragraphs; text written between tool calls folds into the run of calls.
        var turn = new StackPanel();
        var run = new List<ProviderActivityStep>();
        var sawTool = false;

        void FlushRun()
        {
            if (run.Count > 0)
            {
                turn.Children.Add(BuildGroup(run.ToArray(), style));
                run.Clear();
            }
        }

        foreach (var step in steps)
        {
            if (step.IsNote || (step.IsText && !sawTool))
            {
                FlushRun();
                turn.Children.Add(MarkdownRenderer.BuildPanel(step.Detail, style));
            }
            else
            {
                sawTool |= !step.IsText;
                run.Add(step);
            }
        }

        FlushRun();
        return turn;
    }

    // One run of tool calls: a muted summary line; clicking it opens the list of calls.
    private static FrameworkElement BuildGroup(IReadOnlyList<ProviderActivityStep> tools, MarkdownStyle style)
    {
        var size = style.FontSize - 0.5;
        var line = new TextBlock
        {
            FontSize = size,
            Foreground = style.Muted,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        line.Inlines.Add(new Run(Describe(tools.Where(step => !step.IsText).ToArray())));

        var edits = tools.Where(step => step.IsFileEdit && !step.Failed).ToArray();
        if (edits.Length > 0)
        {
            line.Inlines.Add(new Run("  "));
            line.Inlines.Add(new InlineUIContainer(DiffBadge(edits.Sum(e => e.Added), edits.Sum(e => e.Removed), style, size - 1.5))
            {
                BaselineAlignment = BaselineAlignment.Center
            });
        }

        if (tools.Any(step => step.Failed))
        {
            line.Inlines.Add(new Run("  " + "Failed") { Foreground = FailedBrush });
        }

        var chevron = new Run("  ›");
        line.Inlines.Add(chevron);

        var list = BuildList(tools, style);
        list.Visibility = Visibility.Collapsed;
        list.Margin = new Thickness(0, 6, 0, 2);

        line.Cursor = Cursors.Hand;
        line.Background = Brushes.Transparent;
        line.MouseLeftButtonUp += (_, _) =>
        {
            var open = list.Visibility != Visibility.Visible;
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "  ⌄" : "  ›";
        };

        var group = new StackPanel { Margin = new Thickness(0, 0, 0, Math.Round(style.FontSize * 0.7)) };
        group.Children.Add(line);
        group.Children.Add(list);
        return group;
    }

    // "Created a file, ran 6 commands, read 3 files, and 3 more actions"
    internal static string Describe(IReadOnlyList<ProviderActivityStep> tools)
    {
        var kinds = tools
            .GroupBy(step => step.Verb switch
            {
                "Searched" or "Found files" => "search",
                _ => step.Verb
            })
            .Select(group => (Key: group.Key, Steps: group.ToArray()))
            .ToArray();

        static string Count(int n, string one, string many) => n == 1 ? one : string.Format(many, n);
        static int Files(ProviderActivityStep[] steps) => steps.Select(s => s.Detail.Length > 0 ? s.Detail : s.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var phrases = kinds.Select(kind => kind.Key switch
        {
            "Edited" => Count(Files(kind.Steps), "edited a file", "edited {0} files"),
            "Wrote" => Count(Files(kind.Steps), "created a file", "created {0} files"),
            "Read" => Count(Files(kind.Steps), "read a file", "read {0} files"),
            "Ran" => Count(kind.Steps.Length, "ran a command", "ran {0} commands"),
            "search" => Count(kind.Steps.Length, "searched once", "searched {0} times"),
            "Fetched" => Count(kind.Steps.Length, "fetched a page", "fetched {0} pages"),
            "Searched the web" => Count(kind.Steps.Length, "searched the web", "searched the web {0} times"),
            "Ran agent" => Count(kind.Steps.Length, "ran an agent", "ran {0} agents"),
            "Updated" => "updated todos",
            _ => Count(kind.Steps.Length, "used a tool", "used {0} tools")
        }).ToList();

        const int MaxPhrases = 3;
        if (phrases.Count > MaxPhrases)
        {
            var rest = kinds.Skip(MaxPhrases).Sum(kind => kind.Steps.Length);
            // (the remainder phrase is appended last and joined with "and" below)
            phrases = phrases.Take(MaxPhrases).Append(Count(rest, "1 more action", "{0} more actions")).ToList();
        }

        if (phrases.Count == 0)
        {
            return "Worked";
        }

        // "Read 2 files, created a file, ran 4 commands" — "and" only before the remainder.
        string text;
        if (phrases.Count > MaxPhrases)
        {
            text = string.Join(", ", phrases.Take(MaxPhrases)) + ", and " + phrases[^1];
        }
        else
        {
            text = string.Join(", ", phrases);
        }
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static Border DiffBadge(int added, int removed, MarkdownStyle style, double size)
    {
        var text = new TextBlock { FontSize = size, FontFamily = style.CodeFont };
        text.Inlines.Add(new Run("+" + added) { Foreground = AddedBrush });
        text.Inlines.Add(new Run(" −" + removed) { Foreground = FailedBrush });
        return new Border
        {
            Background = style.CodeBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            Child = text
        };
    }

    private static Border BuildList(IReadOnlyList<ProviderActivityStep> steps, MarkdownStyle style)
    {
        var list = new StackPanel();
        var hidden = Math.Max(0, steps.Count - VisibleSteps);
        var folded = new List<FrameworkElement>();

        for (var i = 0; i < steps.Count; i++)
        {
            var row = BuildRow(steps[i], style, first: i == 0);
            if (i < hidden)
            {
                row.Visibility = Visibility.Collapsed;
                folded.Add(row);
            }

            list.Children.Add(row);
        }

        if (hidden > 0)
        {
            var more = new TextBlock
            {
                Text = string.Format(Tr.Text("Tampilkan {0} langkah sebelumnya", "Show {0} earlier steps"), hidden),
                FontSize = style.FontSize - 1,
                Foreground = style.Muted,
                Padding = new Thickness(12, 7, 12, 7),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            more.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var row in folded)
                {
                    row.Visibility = Visibility.Visible;
                }

                more.Visibility = Visibility.Collapsed;
            };
            list.Children.Insert(0, more);
            if (folded.Count > 0 && folded[0] is Border { } firstHidden)
            {
                firstHidden.BorderThickness = new Thickness(0, 1, 0, 0);
            }
        }

        return new Border
        {
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = list
        };
    }

    private static readonly Brush AddedBrush = Frozen(Color.FromRgb(0x4A, 0xD6, 0x6D));

    /// <summary>
    /// "Edited 2 files  +112 −4" with a row per file, as Claude Code ends a turn that changed
    /// files; clicking a file shows it in Explorer. Null when the turn changed nothing.
    /// </summary>
    public static FrameworkElement? BuildSummary(IReadOnlyList<ProviderActivityStep> steps, MarkdownStyle style)
    {
        var files = steps
            .Where(step => step.IsFileEdit && !step.Failed && step.Detail.Length > 0 && !IsScratch(step.Detail))
            .GroupBy(step => step.Detail, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, Name: group.First().Target, Added: group.Sum(s => s.Added), Removed: group.Sum(s => s.Removed)))
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        var size = style.FontSize - 0.5;
        var list = new StackPanel();
        var edited = steps.Where(step => step.IsFileEdit && !step.Failed && step.Detail.Length > 0 && !IsScratch(step.Detail)).ToArray();
        list.Children.Add(SummaryRow(
            files.Length == 1 ? "Edited 1 file" : $"Edited {files.Length} files",
            files.Sum(f => f.Added), files.Sum(f => f.Removed), style, size, bold: false, first: true, path: null));
        foreach (var file in files)
        {
            list.Children.Add(SummaryRow(file.Name, file.Added, file.Removed, style, size, bold: false, first: false, path: file.Path));
        }

        // Undo, as in Claude Code: puts back what this turn's edits replaced.
        if (edited.Any(step => step.Edits is { Count: > 0 }) && list.Children[0] is Border { Child: DockPanel header })
        {
            var undo = new TextBlock
            {
                Text = "Undo",
                FontSize = size - 0.5,
                Foreground = style.Muted,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = Tr.Text("Kembalikan perubahan file dari giliran ini", "Revert this turn's file changes")
            };
            undo.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (undo.Tag is "done")
                {
                    return;
                }

                var (done, skipped) = ProviderActivity.Undo(edited);
                undo.Tag = "done";
                undo.Cursor = null;
                undo.Text = skipped == 0
                    ? Tr.Text("Dikembalikan", "Undone")
                    : string.Format(Tr.Text("Dikembalikan {0}, {1} dilewati", "Undone {0}, skipped {1}"), done, skipped);
            };
            DockPanel.SetDock(undo, Dock.Right);
            header.Children.Insert(1, undo);
        }

        return new Border
        {
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 2, 0, 8),
            Child = list
        };
    }

    // Scratch files the agent keeps in the temp folder are not part of the work.
    private static bool IsScratch(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Border SummaryRow(string label, int added, int removed, MarkdownStyle style, double size, bool bold, bool first, string? path)
    {
        var counts = new TextBlock { FontSize = size - 0.5, VerticalAlignment = VerticalAlignment.Center, FontFamily = style.CodeFont };
        counts.Inlines.Add(new Run("+" + added) { Foreground = AddedBrush });
        counts.Inlines.Add(new Run(" −" + removed) { Foreground = FailedBrush });
        counts.Inlines.Add(new Run("   ›") { Foreground = style.Muted, FontFamily = new FontFamily("Segoe UI") });

        // Leading glyph: a stack for the header, </> for a file.
        var glyph = new TextBlock
        {
            Text = path is null ? "⧉" : "</>",
            FontSize = size - 2.5,
            FontFamily = style.CodeFont,
            Foreground = style.Muted,
            Width = 26,
            VerticalAlignment = VerticalAlignment.Center
        };

        var name = new TextBlock
        {
            Text = label,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = style.Primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(counts, Dock.Right);
        DockPanel.SetDock(glyph, Dock.Left);
        line.Children.Add(counts);
        line.Children.Add(glyph);
        line.Children.Add(name);

        var row = new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = Brushes.Transparent,
            Child = line
        };

        if (path is not null)
        {
            row.Cursor = Cursors.Hand;
            row.ToolTip = path;
            row.MouseLeftButtonUp += (_, _) =>
            {
                // Show it in Explorer (never run it: the agent may have written a script).
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    }
                }
                catch (Exception)
                {
                }
            };
        }

        return row;
    }

    private static Border BuildRow(ProviderActivityStep step, MarkdownStyle style, bool first)
    {
        var size = style.FontSize - 0.5;
        var title = new TextBlock
        {
            FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = style.Primary,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (step.IsText)
        {
            title.Inlines.Add(new Run(step.Target));
        }
        else
        {
            title.Inlines.Add(new Run(step.Verb) { Foreground = style.Muted });
            if (step.Target.Length > 0)
            {
                title.Inlines.Add(new Run(" " + step.Target));
            }

            if (step.Failed)
            {
                title.Inlines.Add(new Run(" " + "Failed") { Foreground = FailedBrush });
            }
        }

        var hasDetail = step.Detail.Length > 0 && (step.IsText || step.Detail != step.Target);
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = size + 1,
            Foreground = style.Muted,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = hasDetail ? Visibility.Visible : Visibility.Hidden
        };

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        header.Children.Add(chevron);
        header.Children.Add(title);

        var body = new StackPanel();
        body.Children.Add(header);

        FrameworkElement? detail = null;
        if (hasDetail)
        {
            detail = step.IsText
                ? MarkdownRenderer.BuildPanel(step.Detail, style)
                : new TextBlock
                {
                    Text = step.Detail,
                    FontFamily = style.CodeFont,
                    FontSize = size - 1,
                    Foreground = style.Muted,
                    TextWrapping = TextWrapping.Wrap
                };
            detail.Margin = new Thickness(0, 6, 0, 2);
            detail.Visibility = Visibility.Collapsed;
            body.Children.Add(detail);
        }

        var row = new Border
        {
            Padding = new Thickness(12, 7, 10, 7),
            BorderBrush = style.CodeBorder,
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Background = Brushes.Transparent,
            Cursor = hasDetail ? Cursors.Hand : null,
            Child = body
        };

        if (detail is not null)
        {
            header.MouseLeftButtonUp += (_, _) =>
            {
                var open = detail.Visibility != Visibility.Visible;
                detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "⌄" : "›";
                title.TextTrimming = open ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                if (step.IsText)
                {
                    title.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                }
            };
            header.Background = Brushes.Transparent;
        }

        return row;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
