namespace CodexBar.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 5;
    public string Provider { get; set; } = "codex";
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
    public AppearanceSettings Appearance { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public string WorkspacePath { get; set; } = string.Empty;
    public string? SelectedModel { get; set; }
    public Dictionary<string, string> ThreadIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ThreadKey(string workspacePath) => $"{Provider}|{Path.GetFullPath(workspacePath)}";
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
