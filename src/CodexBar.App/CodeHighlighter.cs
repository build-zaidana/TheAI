using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

/// <summary>
/// Light-weight syntax colouring for fenced code blocks (comments, strings, numbers, keywords),
/// enough to read code the way Claude Code shows it. Unknown languages stay plain.
/// </summary>
internal static class CodeHighlighter
{
    private static readonly HashSet<string> HashComment = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "py", "bash", "sh", "shell", "zsh", "console", "powershell", "ps1", "pwsh", "ruby", "rb",
        "yaml", "yml", "toml", "r", "perl", "makefile", "dockerfile", "ini", "conf"
    };

    private static readonly HashSet<string> SlashComment = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "cpp", "c++", "h", "cs", "csharp", "c#", "java", "js", "javascript", "jsx", "ts", "typescript", "tsx",
        "go", "golang", "rust", "rs", "swift", "kotlin", "kt", "php", "dart", "scala", "json", "jsonc", "css", "scss"
    };

    private static readonly HashSet<string> DashComment = new(StringComparer.OrdinalIgnoreCase) { "sql", "lua", "haskell", "hs" };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "def", "default",
        "defer", "del", "do", "elif", "else", "enum", "except", "export", "extends", "false", "False", "final", "finally",
        "fn", "for", "foreach", "from", "func", "function", "go", "if", "impl", "implements", "import", "in", "interface",
        "internal", "is", "lambda", "let", "map", "match", "mod", "mut", "namespace", "new", "nil", "None", "not", "null",
        "or", "package", "pass", "private", "protected", "pub", "public", "raise", "range", "readonly", "record", "return",
        "select", "self", "static", "struct", "super", "switch", "this", "throw", "true", "True", "try", "type", "typeof",
        "using", "var", "void", "while", "with", "yield", "chan", "string", "int", "bool", "float", "double", "long", "char",
        "byte", "error", "sealed", "override", "virtual", "where", "then", "fi", "done", "esac", "echo", "local", "export",
        "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "JOIN", "ON", "GROUP", "BY", "ORDER", "AND", "OR", "NOT",
        "INTO", "VALUES", "CREATE", "TABLE", "AS", "LIMIT"
    };

    public static void Fill(InlineCollection target, string code, string language, bool dark)
    {
        code = code.Replace("\r\n", "\n");
        var lang = language.Trim();
        var comment = HashComment.Contains(lang) ? @"\#[^\n]*"
            : SlashComment.Contains(lang) ? @"//[^\n]*|/\*[\s\S]*?\*/"
            : DashComment.Contains(lang) ? @"--[^\n]*"
            : null;
        if (comment is null && !lang.Equals("html", StringComparison.OrdinalIgnoreCase) && !lang.Equals("xml", StringComparison.OrdinalIgnoreCase))
        {
            target.Add(new Run(code));
            return;
        }

        comment ??= @"<!--[\s\S]*?-->";
        var palette = dark
            ? (Comment: Rgb(0x7F, 0x84, 0x8E), Text: Rgb(0x98, 0xC3, 0x79), Number: Rgb(0xD1, 0x9A, 0x66), Keyword: Rgb(0xC6, 0x78, 0xDD))
            : (Comment: Rgb(0x8A, 0x8F, 0x98), Text: Rgb(0x50, 0xA1, 0x4F), Number: Rgb(0x98, 0x68, 0x01), Keyword: Rgb(0xA6, 0x26, 0xA4));

        var pattern = new Regex(
            $@"(?<comment>{comment})|(?<text>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`[^`]*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)");
        var cursor = 0;
        foreach (Match match in pattern.Matches(code))
        {
            Brush? brush = match.Groups["comment"].Success ? palette.Comment
                : match.Groups["text"].Success ? palette.Text
                : match.Groups["number"].Success ? palette.Number
                : match.Groups["word"].Success && Keywords.Contains(match.Value) ? palette.Keyword
                : null;
            if (brush is null)
            {
                continue;
            }

            if (match.Index > cursor)
            {
                target.Add(new Run(code[cursor..match.Index]));
            }

            var run = new Run(match.Value) { Foreground = brush };
            if (match.Groups["comment"].Success)
            {
                run.FontStyle = FontStyles.Italic;
            }

            target.Add(run);
            cursor = match.Index + match.Length;
        }

        if (cursor < code.Length)
        {
            target.Add(new Run(code[cursor..]));
        }
    }

    private static Brush Rgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
