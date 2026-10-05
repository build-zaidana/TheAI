using System.Text;

namespace CodexBar.App;

/// <summary>A conversation as a Markdown file: the messages, and the agent's work per turn.</summary>
internal static class ConversationExport
{
    public static string Build(string title, string context, IReadOnlyList<ChatMessage> messages)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {title}");
        text.AppendLine();
        text.AppendLine($"_{context} · {Tr.Text("diekspor dari CodexBar", "exported from CodexBar")} {DateTime.Now:yyyy-MM-dd HH:mm}_");

        foreach (var message in messages)
        {
            text.AppendLine();
            text.AppendLine(message.IsUser ? $"## {Tr.Text("Kamu", "You")}" : $"## {Tr.Text("Agen", "Agent")}");
            text.AppendLine();

            if (message.IsUser)
            {
                text.AppendLine(message.Markdown.Trim());
                foreach (var image in message.Images)
                {
                    text.AppendLine($"![{System.IO.Path.GetFileName(image)}]({new Uri(image).AbsoluteUri})");
                }

                continue;
            }

            if (message.Steps.Count > 0)
            {
                // The work before the answer, folded the way Claude Code shows it.
                var tools = message.Steps.Count(step => !step.IsText);
                text.AppendLine($"<details><summary>{Tr.Text("Langkah kerja", "Work")} ({tools})</summary>");
                text.AppendLine();
                foreach (var step in message.Steps)
                {
                    if (step.IsText)
                    {
                        text.AppendLine($"> {step.Detail.Replace("\n", "\n> ")}");
                        text.AppendLine();
                        continue;
                    }

                    var line = $"- {step.Verb} {step.Target}".TrimEnd();
                    if (step.IsFileEdit)
                    {
                        line += $" (+{step.Added} −{step.Removed})";
                    }

                    if (step.Failed)
                    {
                        line += " — **Failed**";
                    }

                    if (!step.IsFileEdit && step.Detail.Length > 0 && step.Detail != step.Target)
                    {
                        line += $": `{step.Detail.Replace("`", "'").Replace("\n", " ")}`";
                    }

                    text.AppendLine(line);
                }

                text.AppendLine();
                text.AppendLine("</details>");
                text.AppendLine();
            }

            if (message.Markdown.Trim().Length > 0)
            {
                text.AppendLine(message.Markdown.Trim());
            }
        }

        return text.ToString();
    }

    /// <summary>A file name from a title: no characters Windows refuses, not too long.</summary>
    public static string FileName(string title)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.', '-');
        if (name.Length > 60)
        {
            name = name[..60].Trim();
        }

        return (name.Length > 0 ? name : "Percakapan") + ".md";
    }
}
