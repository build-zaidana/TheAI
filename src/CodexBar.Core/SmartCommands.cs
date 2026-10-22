namespace CodexBar.Core;

public enum SmartCommandKind
{
    /// <summary>Becomes a prompt for the agent.</summary>
    Prompt,
    /// <summary>Does something in TheAI itself (a page, a timer, settings…).</summary>
    Action
}

/// <summary>One entry of the launcher's "/" palette.</summary>
public sealed record SmartCommand(
    string Name,
    SmartCommandKind Kind,
    string DescriptionId,
    string DescriptionEn,
    string? Template = null,
    string ArgumentHintId = "",
    string ArgumentHintEn = "")
{
    public string Description(Func<string, string, string> text) => text(DescriptionId, DescriptionEn);
    public string ArgumentHint(Func<string, string, string> text) => text(ArgumentHintId, ArgumentHintEn);
}

/// <summary>What the commands may use from where the user is: the clipboard, the last failed command.</summary>
public sealed record SmartCommandContext(string Clipboard, string LastFailure, bool English);

/// <summary>
/// The launcher's commands: "/" lists them, typing narrows them down, Tab completes, Enter runs.
/// Prompt commands expand to a ready prompt for the agent ({input} = what follows the command,
/// or the clipboard when nothing does); actions are handled by the app. The user's own templates
/// join the list as /their-name.
/// </summary>
public static class SmartCommands
{
    public static readonly IReadOnlyList<SmartCommand> BuiltIn =
    [
        new("review", SmartCommandKind.Prompt, "Review perubahan yang belum di-commit", "Review the uncommitted changes",
            "Review the uncommitted changes in this folder (git diff, including staged ones). List real problems first — bugs, security, missing tests — each with file and line, then smaller suggestions. Don't change any files.{focus}"),
        new("explain", SmartCommandKind.Prompt, "Jelaskan kode / teks (atau isi clipboard)", "Explain code or text (or the clipboard)",
            "Explain clearly what this does, step by step, and point out anything surprising or risky:\n\n{input}", "teks atau kode", "text or code"),
        new("fix", SmartCommandKind.Prompt, "Perbaiki perintah terakhir yang gagal", "Fix the last failed command",
            "{failure}Find out why it failed in this folder, fix it, and run it again to confirm.{focus}"),
        new("test", SmartCommandKind.Prompt, "Jalankan tes dan perbaiki yang gagal", "Run the tests and fix failures",
            "Run this project's tests. If any fail, find the cause and fix it, then run them again until they pass. Summarize what you changed.{focus}"),
        new("commit", SmartCommandKind.Prompt, "Tulis pesan commit & commit perubahan", "Write a commit message and commit",
            "Look at git status and the diff in this folder, write a concise, conventional commit message that explains why, and commit the changes. Don't push.{focus}"),
        new("translate", SmartCommandKind.Prompt, "Terjemahkan teks (atau isi clipboard)", "Translate text (or the clipboard)",
            "Translate the following into {language} (if it's already in {language}, into {other} instead). Keep the meaning, tone and formatting; reply with the translation only.\n\n{input}", "ke-bahasa teks", "to-language text"),
        new("proofread", SmartCommandKind.Prompt, "Perbaiki ejaan & tata bahasa", "Fix spelling and grammar",
            "Fix the spelling and grammar of this and make it read naturally. Keep its language, meaning and tone; reply with the corrected text only.\n\n{input}", "teks", "text"),
        new("summarize", SmartCommandKind.Prompt, "Ringkas teks (atau isi clipboard)", "Summarize text (or the clipboard)",
            "Summarize this in a few bullet points, most important first:\n\n{input}", "teks", "text"),
        new("docs", SmartCommandKind.Prompt, "Tulis dokumentasi untuk kode ini", "Write documentation for this code",
            "Write clear documentation for this folder's code (or for the part named here): what it is for, how to use it, the main parts and their gotchas. Match the project's existing docs style.{focus}"),

        new("new", SmartCommandKind.Action, "Percakapan baru", "New conversation"),
        new("focus", SmartCommandKind.Action, "Mulai fokus (pomodoro)", "Start a focus session (pomodoro)", ArgumentHintId: "menit", ArgumentHintEn: "minutes"),
        new("timer", SmartCommandKind.Action, "Timer", "Timer", ArgumentHintId: "mis. 10 atau 1:30", ArgumentHintEn: "e.g. 10 or 1:30"),
        new("clipboard", SmartCommandKind.Action, "Riwayat clipboard", "Clipboard history"),
        new("recap", SmartCommandKind.Action, "Rekap hari ini", "Today's recap"),
        new("standup", SmartCommandKind.Action, "Salin rekap untuk standup", "Copy the recap for standup"),
        new("system", SmartCommandKind.Action, "CPU, GPU, RAM, disk", "CPU, GPU, memory, disk"),
        new("weather", SmartCommandKind.Action, "Cuaca & jam dunia", "Weather & world clock"),
        new("water", SmartCommandKind.Action, "Minum air", "Drink water"),
        new("activities", SmartCommandKind.Action, "Aktivitas berjalan (build, CI…)", "Running activities (builds, CI…)"),
        new("remind", SmartCommandKind.Action, "Pengingat (15m, 14:30, besok 9:00…)", "Reminder (15m, 14:30, tomorrow 9:00…)", ArgumentHintId: "kapan apa", ArgumentHintEn: "when what"),
        new("todo", SmartCommandKind.Action, "Tambah to-do", "Add a to-do", ArgumentHintId: "teks", ArgumentHintEn: "text"),
        new("settings", SmartCommandKind.Action, "Buka pengaturan", "Open settings"),
    ];

    /// <summary>"/review the auth module" → ("review", "the auth module"); not a command → null.</summary>
    public static (string Name, string Argument)? Split(string input)
    {
        var text = input.TrimStart();
        if (!text.StartsWith('/') || text.Length < 2 || char.IsWhiteSpace(text[1]))
        {
            return null;
        }

        var space = text.IndexOfAny([' ', '\n', '\t']);
        return space < 0
            ? (text[1..].ToLowerInvariant(), string.Empty)
            : (text[1..space].ToLowerInvariant(), text[(space + 1)..].Trim());
    }

    /// <summary>The commands for what's typed after "/": exact name first, then prefix, then the description.</summary>
    public static IReadOnlyList<SmartCommand> Match(string typed, IEnumerable<SmartCommand> commands, Func<string, string, string> text)
    {
        var query = typed.Trim().TrimStart('/').ToLowerInvariant();
        var list = commands.ToList();
        if (query.Length == 0)
        {
            return list;
        }

        return list
            .Select(command => (command, rank:
                command.Name == query ? 0
                : command.Name.StartsWith(query, StringComparison.Ordinal) ? 1
                : command.Name.Contains(query, StringComparison.Ordinal) ? 2
                : command.Description(text).Contains(query, StringComparison.OrdinalIgnoreCase) ? 3
                : -1))
            .Where(pair => pair.rank >= 0)
            .OrderBy(pair => pair.rank)
            .Select(pair => pair.command)
            .ToList();
    }

    /// <summary>The user's templates as commands: "Tulis unit test" → /tulis-unit-test.</summary>
    public static IEnumerable<SmartCommand> FromTemplates(IEnumerable<PromptTemplate> templates) =>
        templates
            .Select(template => new SmartCommand(Slug(template.Name), SmartCommandKind.Prompt, template.Name, template.Name, template.Content.TrimEnd() + "\n\n{input}"))
            .Where(command => command.Name.Length > 0 && BuiltIn.All(builtIn => builtIn.Name != command.Name));

    /// <summary>
    /// The prompt a prompt command becomes. Null when it needs text and has none (no argument and an
    /// empty clipboard), so the launcher can ask for it instead of sending an empty request.
    /// </summary>
    public static string? Expand(SmartCommand command, string argument, SmartCommandContext context)
    {
        if (command.Template is not { } template)
        {
            return null;
        }

        var needsInput = template.Contains("{input}", StringComparison.Ordinal);
        var language = context.English ? "English" : "Indonesian";
        var input = argument;
        if (command.Name == "translate" && argument.Split(' ', 2) is [var first, var rest] && LanguageName(first) is { } named)
        {
            language = named;
            input = rest;
        }
        else if (command.Name == "translate" && LanguageName(argument) is { } only)
        {
            language = only;
            input = string.Empty;
        }

        if (needsInput && input.Length == 0)
        {
            input = context.Clipboard.Trim();
            if (input.Length == 0)
            {
                return null;
            }
        }

        var failure = context.LastFailure.Length > 0 ? $"This command just failed: {context.LastFailure}. " : string.Empty;
        var focus = !needsInput && argument.Length > 0 ? $"\n\nFocus on: {argument}" : string.Empty;
        return template
            .Replace("{input}", input, StringComparison.Ordinal)
            .Replace("{language}", language, StringComparison.Ordinal)
            .Replace("{other}", language == "English" ? "Indonesian" : "English", StringComparison.Ordinal)
            .Replace("{failure}", failure, StringComparison.Ordinal)
            .Replace("{focus}", focus, StringComparison.Ordinal)
            .Trim();
    }

    private static string? LanguageName(string word) => word.ToLowerInvariant() switch
    {
        "en" or "english" or "inggris" => "English",
        "id" or "indonesian" or "indonesia" => "Indonesian",
        "ja" or "japanese" or "jepang" => "Japanese",
        "zh" or "chinese" or "mandarin" => "Chinese",
        "ko" or "korean" or "korea" => "Korean",
        "ar" or "arabic" or "arab" => "Arabic",
        "es" or "spanish" or "spanyol" => "Spanish",
        "fr" or "french" or "prancis" => "French",
        "de" or "german" or "jerman" => "German",
        "nl" or "dutch" or "belanda" => "Dutch",
        "jv" or "javanese" or "jawa" => "Javanese",
        "su" or "sundanese" or "sunda" => "Sundanese",
        _ => null
    };

    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 32 ? slug[..32].TrimEnd('-') : slug;
    }
}
