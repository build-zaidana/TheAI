using System.Text;

namespace CodexBar.Providers;

/// <summary>
/// Small line diff for showing agent edits: LCS over lines, printed as unified-style hunks with a
/// little context. Good enough for edit snippets and modest files; very large inputs fall back to
/// "everything removed, everything added" rather than spending quadratic memory.
/// </summary>
public static class LineDiff
{
    private const int MaxCells = 4_000_000;

    public static string Build(string oldText, string newText, int context = 2)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        // Trim the common prefix/suffix first; edits are usually local.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
               a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var ops = new List<(char Op, string Line)>();
        for (var i = 0; i < prefix; i++)
        {
            ops.Add((' ', a[i]));
        }

        var midA = a[prefix..(a.Length - suffix)];
        var midB = b[prefix..(b.Length - suffix)];
        if ((long)midA.Length * midB.Length > MaxCells)
        {
            ops.AddRange(midA.Select(line => ('-', line)));
            ops.AddRange(midB.Select(line => ('+', line)));
        }
        else
        {
            ops.AddRange(Lcs(midA, midB));
        }

        for (var i = a.Length - suffix; i < a.Length; i++)
        {
            ops.Add((' ', a[i]));
        }

        return Format(ops, context);
    }

    /// <summary>Count added and removed lines of a unified diff (file headers ignored).</summary>
    public static (int Added, int Removed) Count(string diff)
    {
        int added = 0, removed = 0;
        foreach (var line in SplitLines(diff))
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

    public static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<(char, string)> Lcs(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var ops = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                ops.Add((' ', a[x++]));
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                ops.Add(('-', a[x++]));
            }
            else
            {
                ops.Add(('+', b[y++]));
            }
        }

        while (x < a.Length)
        {
            ops.Add(('-', a[x++]));
        }

        while (y < b.Length)
        {
            ops.Add(('+', b[y++]));
        }

        return ops;
    }

    // Keep `context` unchanged lines around each change; collapse the rest into "@@" separators.
    private static string Format(List<(char Op, string Line)> ops, int context)
    {
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Op == ' ')
            {
                continue;
            }

            for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++)
            {
                keep[k] = true;
            }
        }

        var builder = new StringBuilder();
        var gap = true;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i])
            {
                gap = true;
                continue;
            }

            if (gap)
            {
                builder.Append("@@\n");
                gap = false;
            }

            builder.Append(ops[i].Op).Append(ops[i].Line).Append('\n');
        }

        return builder.ToString();
    }
}
