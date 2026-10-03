namespace CodexBar.Providers.ClaudeCode;

/// <summary>
/// Finds the Claude Code CLI (<c>claude</c>). Checks, in order: <c>CODEXBAR_CLAUDE_PATH</c>,
/// the native installer location (<c>%USERPROFILE%\.local\bin</c>), the global npm folder,
/// then PATH.
/// </summary>
public static class ClaudeCodeExecutableLocator
{
    public static string Locate() =>
        TryLocate() ?? throw new FileNotFoundException(
            // Not localized: this is caught by onboarding, which shows a localized install panel.
            "Claude Code CLI tidak ditemukan. Instal dengan: npm install -g @anthropic-ai/claude-code");

    public static string? TryLocate()
    {
        var configured = Environment.GetEnvironmentVariable("CODEXBAR_CLAUDE_PATH");
        if (IsUsable(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd")
        };

        var found = candidates.FirstOrDefault(IsUsable);
        if (found is not null)
        {
            return found;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                var executable = Path.Combine(directory.Trim('"'), name);
                if (IsUsable(executable))
                {
                    return executable;
                }
            }
        }

        return null;
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
