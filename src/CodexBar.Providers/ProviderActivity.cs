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
