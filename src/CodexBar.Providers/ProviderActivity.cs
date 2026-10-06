using System.Text;

namespace CodexBar.Providers;

/// <summary>One text replacement an agent made in a file (what Undo puts back).</summary>
public sealed record ProviderTextEdit(string Old, string New, bool All = false);

/// <summary>
/// Shared pieces of the Claude Code style activity view: splitting a turn into work and answer,
/// counting changed lines, and undoing a turn's text edits.
/// </summary>
public static class ProviderActivity
{
    /// <summary>
    /// A turn's parts as Claude Code shows them: everything up to the last tool call (or progress
    /// note) is work, failures marked; the text after it is the answer.
    /// </summary>
    public static (IReadOnlyList<ProviderActivityStep> Steps, string Answer) SplitTurn(
        IReadOnlyList<(ProviderActivityStep Step, string? ToolId)> parts, IReadOnlySet<string> failed)
    {
        var lastTool = -1;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (!parts[i].Step.IsText || parts[i].Step.IsNote)
            {
                lastTool = i;
                break;
            }
        }

        var answer = string.Join("\n\n", parts.Skip(lastTool + 1).Select(part => part.Step.Detail)).Trim();
        var steps = parts.Take(lastTool + 1)
            .Select(part => part.ToolId is not null && failed.Contains(part.ToolId) ? part.Step with { Failed = true } : part.Step)
            .ToArray();
        return (steps, answer);
    }

    public static int LineCount(string text) => text.Length == 0 ? 0 : text.TrimEnd('\n').Split('\n').Length;

    /// <summary>Lines added / removed by replacing old with new, counted as a diff does (LCS).</summary>
    public static (int Added, int Removed) LineDelta(string oldText, string newText)
    {
        var before = oldText.Length == 0 ? [] : oldText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var after = newText.Length == 0 ? [] : newText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        var end = 0;
        while (end < before.Length - start && end < after.Length - start &&
               before[before.Length - 1 - end] == after[after.Length - 1 - end])
        {
            end++;
        }

        // In between: lines kept are the longest common subsequence.
        var m = before.Length - start - end;
        var n = after.Length - start - end;
        if (m == 0 || n == 0 || (long)m * n > 4_000_000)
        {
            return (n, m);
        }

        var row = new int[n + 1];
        for (var i = 0; i < m; i++)
        {
            var diagonal = 0;
            for (var j = 0; j < n; j++)
            {
                var above = row[j + 1];
                row[j + 1] = before[start + i] == after[start + j] ? diagonal + 1 : Math.Max(above, row[j]);
                diagonal = above;
            }
        }

        var kept = row[n];
        return (n - kept, m - kept);
    }

    /// <summary>
    /// The lines of a file step's change for a diff view: '+' added, '-' removed, ' ' context,
    /// '@' a gap between hunks. From the provider's diff, else from the edits' old and new text
    /// (with up to three context lines around changes); empty when there is nothing to show.
    /// </summary>
    public static IReadOnlyList<(char Kind, string Text)> DiffLines(ProviderActivityStep step)
    {
        var lines = new List<(char, string)>();
        if (step.Diff is { Length: > 0 } diff)
        {
            foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ||
                    line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (lines.Count > 0)
                    {
                        lines.Add(('@', string.Empty));
                    }
                }
                else if (line.Length > 0 && line[0] is '+' or '-' or ' ')
                {
                    lines.Add((line[0], line[1..]));
                }
            }

            return lines;
        }

        foreach (var edit in step.Edits ?? [])
        {
            if (lines.Count > 0)
            {
                lines.Add(('@', string.Empty));
            }

            lines.AddRange(WithContext(LineOps(edit.Old, edit.New), 3));
        }

        return lines;
    }

    // Line-by-line operations turning old into new (LCS), in order.
    private static List<(char Kind, string Text)> LineOps(string oldText, string newText)
    {
        var before = oldText.Length == 0 ? [] : oldText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var after = newText.Length == 0 ? [] : newText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var ops = new List<(char, string)>();
        if ((long)before.Length * after.Length > 4_000_000)
        {
            ops.AddRange(before.Select(line => ('-', line)));
            ops.AddRange(after.Select(line => ('+', line)));
            return ops;
        }

        var table = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                table[i, j] = before[i] == after[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < before.Length && b < after.Length)
        {
            if (before[a] == after[b])
            {
                ops.Add((' ', before[a]));
                a++;
                b++;
            }
            else if (table[a + 1, b] >= table[a, b + 1])
            {
                ops.Add(('-', before[a++]));
            }
            else
            {
                ops.Add(('+', after[b++]));
            }
        }

        ops.AddRange(before.Skip(a).Select(line => ('-', line)));
        ops.AddRange(after.Skip(b).Select(line => ('+', line)));
        return ops;
    }

    // Keeps changed lines and up to <paramref name="context"/> unchanged lines around them.
    private static IEnumerable<(char Kind, string Text)> WithContext(List<(char Kind, string Text)> ops, int context)
    {
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind != ' ')
            {
                for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++)
                {
                    keep[k] = true;
                }
            }
        }

        var skipped = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (keep[i])
            {
                if (skipped)
                {
                    yield return ('@', string.Empty);
                    skipped = false;
                }

                yield return ops[i];
            }
            else
            {
                skipped = true;
            }
        }
    }

    /// <summary>Lines added / removed in a unified diff (headers and hunk markers left out).</summary>
    public static (int Added, int Removed) DiffCounts(string diff)
    {
        int added = 0, removed = 0;
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                added++;
            }
            else if (line.StartsWith('-'))
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>
    /// Puts back the text a turn's successful edits replaced, newest first. Edits without the
    /// replacement data (whole-file writes, other providers) or whose text has changed since are
    /// skipped. Returns how many file edits were undone and how many could not be.
    /// </summary>
    public static (int Undone, int Skipped) Undo(IReadOnlyList<ProviderActivityStep> steps)
    {
        int undone = 0, skipped = 0;
        foreach (var step in steps.Where(s => s.IsFileEdit && !s.Failed).Reverse())
        {
            if (step.Edits is not { Count: > 0 } edits || !File.Exists(step.Detail))
            {
                skipped++;
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(step.Detail);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                var ok = true;
                foreach (var edit in edits.Reverse())
                {
                    if (!TryRevert(ref text, edit))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    skipped++;
                    continue;
                }

                File.WriteAllText(step.Detail, text, new UTF8Encoding(bom));
                undone++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        return (undone, skipped);
    }

    private static bool TryRevert(ref string text, ProviderTextEdit edit)
    {
        // The file may use CRLF while the tool saw LF: try both spellings.
        foreach (var (now, before) in new[] { (edit.New, edit.Old), (edit.New.Replace("\n", "\r\n"), edit.Old.Replace("\n", "\r\n")) })
        {
            if (now.Length == 0)
            {
                return false; // a pure deletion leaves nothing to find
            }

            var index = text.IndexOf(now, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            text = edit.All ? text.Replace(now, before, StringComparison.Ordinal) : text[..index] + before + text[(index + now.Length)..];
            return true;
        }

        return false;
    }
}
