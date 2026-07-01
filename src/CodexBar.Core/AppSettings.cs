namespace CodexBar.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 8;
    public string Provider { get; set; } = "codex";

    /// <summary>Bahasa UI: "id" (default) atau "en".</summary>
    public string Language { get; set; } = "id";
    public string Hotkey { get; set; } = "Win+Shift+C";
    public bool LaunchAtLogin { get; set; }
    public bool HideOnFocusLost { get; set; } = true;
    public bool ShowResponseInline { get; set; } = true;
    public bool Pinned { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool ApiKeyWarningAcknowledged { get; set; }
    public string SandboxMode { get; set; } = "workspace-write";

    /// <summary>Kebijakan approval Codex: untrusted | on-failure | on-request | never.</summary>
    public string ApprovalPolicy { get; set; } = "on-request";

    /// <summary>Reasoning effort override: kosong = default model, atau minimal/low/medium/high.</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>Output menyatu (docked) menempel pada input bar, bukan window terpisah.</summary>
    public bool OutputDocked { get; set; } = true;

    /// <summary>Posisi output saat docked relatif terhadap input bar: "below" atau "above".</summary>
    public string OutputPosition { get; set; } = "below";

    /// <summary>Tampilkan proses berpikir (reasoning) agent di gelembung respons.</summary>
    public bool ShowReasoning { get; set; } = true;

    /// <summary>Codex menu (riwayat) menyatu menempel pada input bar, bukan window terpisah.</summary>
    public bool MenuDocked { get; set; } = true;

    /// <summary>Aktifkan diagnostics logging opt-in ke file (untuk pelaporan bug). Default mati.</summary>
    public bool EnableDiagnosticsLogging { get; set; }

    /// <summary>Template prompt tersimpan untuk quick-insert. Di-seed dengan contoh pada instal baru.</summary>
    public List<PromptTemplate> PromptTemplates { get; set; } = PromptTemplate.CreateDefaults();
    public AppearanceSettings Appearance { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public string WorkspacePath { get; set; } = string.Empty;
    public string? SelectedModel { get; set; }
    public Dictionary<string, string> ThreadIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ThreadKey(string workspacePath) => $"{Provider}|{Path.GetFullPath(workspacePath)}";

    public void ApplyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SchemaVersion = source.SchemaVersion;
        Provider = source.Provider;
        Language = source.Language;
        Hotkey = source.Hotkey;
        LaunchAtLogin = source.LaunchAtLogin;
        HideOnFocusLost = source.HideOnFocusLost;
        ShowResponseInline = source.ShowResponseInline;
        Pinned = source.Pinned;
        AlwaysOnTop = source.AlwaysOnTop;
        ApiKeyWarningAcknowledged = source.ApiKeyWarningAcknowledged;
        SandboxMode = source.SandboxMode;
        ApprovalPolicy = source.ApprovalPolicy;
        ReasoningEffort = source.ReasoningEffort;
        OutputDocked = source.OutputDocked;
        OutputPosition = source.OutputPosition;
        ShowReasoning = source.ShowReasoning;
        MenuDocked = source.MenuDocked;
        EnableDiagnosticsLogging = source.EnableDiagnosticsLogging;
        PromptTemplates = PromptTemplate.Normalize(source.PromptTemplates);
        Appearance = new AppearanceSettings
        {
            Theme = source.Appearance.Theme,
            ActiveOpacity = source.Appearance.ActiveOpacity,
            InactiveOpacity = source.Appearance.InactiveOpacity,
            InputFontSize = source.Appearance.InputFontSize,
            ResponseFontSize = source.Appearance.ResponseFontSize,
            CodeFontFamily = source.Appearance.CodeFontFamily,
            CodeWordWrap = source.Appearance.CodeWordWrap
        };
        Window = new WindowSettings
        {
            Width = source.Window.Width,
            MaxExpandedHeight = source.Window.MaxExpandedHeight,
            Position = source.Window.Position,
            RememberPerMonitor = source.Window.RememberPerMonitor
        };
        WorkspacePath = source.WorkspacePath;
        SelectedModel = source.SelectedModel;
        ThreadIds = new Dictionary<string, string>(source.ThreadIds, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "system";
    public double ActiveOpacity { get; set; } = 0.95;
    public double InactiveOpacity { get; set; } = 0.82;
    public double InputFontSize { get; set; } = 14;
    public double ResponseFontSize { get; set; } = 14;
    public string CodeFontFamily { get; set; } = "Cascadia Mono";
    public bool CodeWordWrap { get; set; } = true;
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 640;
    public double MaxExpandedHeight { get; set; } = 520;
    public string Position { get; set; } = "bottom-center";
    public bool RememberPerMonitor { get; set; } = true;
}

public sealed class PromptTemplate
{
    /// <summary>Maximum number of templates retained (older/overflow entries are dropped on normalize).</summary>
    public const int MaxCount = 50;
    private const int MaxNameLength = 80;

    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>Seed templates offered on a fresh install; users can edit or remove them.</summary>
    public static List<PromptTemplate> CreateDefaults() =>
    [
        new() { Name = "Jelaskan kode", Content = "Jelaskan apa yang dilakukan kode berikut dan potensi masalahnya:\n\n" },
        new() { Name = "Perbaiki bug", Content = "Temukan dan perbaiki bug pada kode ini. Jelaskan penyebab dan perbaikannya:\n\n" },
        new() { Name = "Tulis unit test", Content = "Tulis unit test yang menyeluruh untuk kode berikut:\n\n" },
        new() { Name = "Review & rapikan", Content = "Review kode ini untuk keterbacaan, dan sederhanakan tanpa mengubah perilaku:\n\n" },
    ];

    /// <summary>
    /// Clean a template list: trim fields, drop entries with an empty name or content,
    /// de-duplicate by name (case-insensitive, first wins), and cap to <see cref="MaxCount"/>.
    /// </summary>
    public static List<PromptTemplate> Normalize(IEnumerable<PromptTemplate>? templates)
    {
        var result = new List<PromptTemplate>();
        if (templates is null)
        {
            return result;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            var name = template?.Name?.Trim() ?? string.Empty;
            var content = template?.Content ?? string.Empty;
            if (name.Length == 0 || content.Trim().Length == 0)
            {
                continue;
            }

            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength];
            }

            if (!seenNames.Add(name))
            {
                continue;
            }

            result.Add(new PromptTemplate { Name = name, Content = content });
            if (result.Count >= MaxCount)
            {
                break;
            }
        }

        return result;
    }

    public static string Expand(string content, string? selection, string? clipboard) =>
        (content ?? string.Empty)
            .Replace("{selection}", selection ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{clipboard}", clipboard ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
