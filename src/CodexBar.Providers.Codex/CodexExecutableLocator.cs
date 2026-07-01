namespace CodexBar.Providers.Codex;

public static class CodexExecutableLocator
{
    public static string Locate()
    {
        var configured = Environment.GetEnvironmentVariable("CODEXBAR_CODEX_PATH");
        if (IsUsable(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules", "@openai", "codex", "node_modules"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "OpenAI", "Codex", "bin")
        };

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var executable = Directory
                .EnumerateFiles(candidate, "codex.exe", SearchOption.AllDirectories)
                .FirstOrDefault(IsUsable);

            if (executable is not null)
            {
                return executable;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var executable = Path.Combine(directory.Trim('"'), "codex.exe");
            if (IsUsable(executable) &&
                !executable.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return executable;
            }
        }

        // Not localized: this is caught by onboarding, which shows a localized install panel.
        throw new FileNotFoundException(
            "Codex CLI mandiri tidak ditemukan. Instal dengan: npm install -g @openai/codex");
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

}
