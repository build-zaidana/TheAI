using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.Persistence;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // Serialkan penulisan: banyak pemanggilan SaveAsync fire-and-forget dari UI
    // bisa bertabrakan pada file temp yang sama.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSettingsStore(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBar");
        SettingsPath = Path.Combine(BaseDirectory, "settings.json");
    }

    public string BaseDirectory { get; }
    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                       stream,
                       SerializerOptions,
                       cancellationToken)
                   ?? new AppSettings();
            return Normalize(settings);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var backupPath = $"{SettingsPath}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            File.Move(SettingsPath, backupPath, overwrite: false);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        Normalize(settings),
                        SerializerOptions,
                        cancellationToken);
                }

                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ExportAsync(
        AppSettings settings,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Folder tujuan export tidak valid."));
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(settings),
                    SerializerOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<AppSettings> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(sourcePath));
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
            stream,
            SerializerOptions,
            cancellationToken) ?? throw new JsonException("File settings kosong.");
        return Normalize(settings);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.SchemaVersion = 12;
        settings.ScheduledPrompts = ScheduledPrompt.Normalize(settings.ScheduledPrompts);
        settings.QuickCaptureReveal = settings.QuickCaptureReveal?.ToLowerInvariant() switch
        {
            "capture" => "capture",
            "clipboard" => "clipboard",
            _ => "done"
        };
        if (string.IsNullOrWhiteSpace(settings.QuickCaptureHotkey))
        {
            settings.QuickCaptureHotkey = "Win+Shift+X";
        }
        if (string.IsNullOrWhiteSpace(settings.ScreenshotHotkey))
        {
            settings.ScreenshotHotkey = "Win+Shift+Z";
        }
        if (string.IsNullOrWhiteSpace(settings.ChatHotkey))
        {
            settings.ChatHotkey = "Win+Alt+C";
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceHotkey))
        {
            settings.VoiceHotkey = "Win+Alt+V";
        }
        settings.VoiceLanguage = settings.VoiceLanguage?.ToLowerInvariant() switch
        {
            "en" => "en",
            "auto" => "auto",
            _ => "id"
        };
        settings.VoiceModel = settings.VoiceModel?.ToLowerInvariant() == "small" ? "small" : "base";
        settings.PromptSuggestionSource = settings.PromptSuggestionSource?.ToLowerInvariant() == "habit+ai" ? "habit+ai" : "habit";
        settings.ChatSide = settings.ChatSide?.ToLowerInvariant() switch
        {
            "right" => "right",
            "left" => "left",
            _ => "auto"
        };
        if (settings.PendingResume is { } pending && string.IsNullOrWhiteSpace(pending.Prompt))
        {
            settings.PendingResume = null;
        }
        settings.Provider = settings.Provider?.ToLowerInvariant() switch
        {
            "anthropic" => "anthropic",
            "claude-code" => "claude-code",
            "openai" => "openai",
            "openrouter" => "openrouter",
            _ => "codex"
        };
        settings.Language = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "id";
        settings.Appearance ??= new AppearanceSettings();
        settings.Window ??= new WindowSettings();
        settings.NotchOffset = double.IsFinite(settings.NotchOffset) ? Math.Clamp(settings.NotchOffset, -0.45, 0.45) : 0;
        settings.NotchEdge = settings.NotchEdge?.ToLowerInvariant() switch
        {
            "bottom" or "left" or "right" or "top-left" or "top-right" or "bottom-left" or "bottom-right" =>
                settings.NotchEdge.ToLowerInvariant(),
            _ => "top"
        };
        settings.DisplayMode = settings.DisplayMode?.ToLowerInvariant() switch
        {
            "island" or "bar" => "island", // the bar mode was removed; the floating island is closest
            _ => "notch"
        };
        settings.ThreadIds = new Dictionary<string, string>(
            settings.ThreadIds ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        settings.PromptTemplates = PromptTemplate.Normalize(settings.PromptTemplates);

        settings.Appearance.Theme = settings.Appearance.Theme?.ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system"
        };
        settings.Appearance.ActiveOpacity = FiniteClamp(settings.Appearance.ActiveOpacity, 0.4, 1, 0.95);
        settings.Appearance.InactiveOpacity = FiniteClamp(settings.Appearance.InactiveOpacity, 0.25, 1, 0.82);
        settings.Appearance.InputFontSize = FiniteClamp(settings.Appearance.InputFontSize, 10, 24, 14);
        settings.Appearance.ResponseFontSize = FiniteClamp(settings.Appearance.ResponseFontSize, 10, 24, 14);
        settings.Window.Width = FiniteClamp(settings.Window.Width, 500, 1200, 640);
        settings.Window.MaxExpandedHeight = FiniteClamp(settings.Window.MaxExpandedHeight, 180, 900, 520);
        settings.Window.Position = settings.Window.Position?.ToLowerInvariant() switch
        {
            "bottom-left" => "bottom-left",
            "bottom-right" => "bottom-right",
            _ => "bottom-center"
        };
        settings.WorkspacePath ??= string.Empty;
        settings.Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey)
            ? "Win+Shift+C"
            : settings.Hotkey.Trim();
        settings.SandboxMode = settings.SandboxMode?.ToLowerInvariant() switch
        {
            "read-only" => "read-only",
            "danger-full-access" => "danger-full-access",
            _ => "workspace-write"
        };
        settings.OutputPosition = settings.OutputPosition?.ToLowerInvariant() == "above" ? "above" : "below";
        settings.ApprovalPolicy = settings.ApprovalPolicy?.ToLowerInvariant() switch
        {
            "untrusted" => "untrusted",
            "on-failure" => "on-failure",
            "never" => "never",
            _ => "on-request"
        };
        settings.ReasoningEffort = settings.ReasoningEffort?.ToLowerInvariant() switch
        {
            "minimal" => "minimal",
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => string.Empty
        };
        settings.SelectedModel = string.IsNullOrWhiteSpace(settings.SelectedModel)
            ? null
            : settings.SelectedModel.Trim();
        return settings;
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
