using System.ComponentModel;

namespace CodexBar.Core;

public enum AppLanguage
{
    Id,
    En
}

/// <summary>
/// Live-switchable UI localization. XAML binds to the string indexer:
/// <c>{Binding [key], Source={x:Static core:Localization.Instance}}</c>. Changing the
/// language raises <c>Item[]</c> so every bound string refreshes in place.
///
/// Migration is incremental: strings not yet in the table fall back to their key, so
/// partially-migrated views keep working.
/// </summary>
public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Instance { get; } = new();

    private AppLanguage _language = AppLanguage.Id;

    public AppLanguage Language
    {
        get => _language;
        private set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// <summary>Translate <paramref name="key"/> for the current language (key itself if missing).</summary>
    public string this[string key] => Strings.Get(_language, key);

    public void SetLanguage(AppLanguage language) => Language = language;

    /// <summary>Parse a settings code ("id"/"en") to a language; unknown values fall back to Indonesian.</summary>
    public static AppLanguage Parse(string? code) =>
        string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) ? AppLanguage.En : AppLanguage.Id;

    public static string ToCode(AppLanguage language) => language == AppLanguage.En ? "en" : "id";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Central UI string table. Add entries here as views are migrated to localization.</summary>
public static class Strings
{
    private static readonly Dictionary<string, (string Id, string En)> Table = new(StringComparer.Ordinal)
    {
        ["prompt.placeholder"] = ("Tanya Codex apa saja…  •  Ctrl+V screenshot", "Ask Codex anything…  •  Ctrl+V screenshot"),
        ["templates.tooltip"] = ("Template prompt", "Prompt templates"),
        ["templates.saveCurrent"] = ("＋  Simpan prompt ini sebagai template", "＋  Save this prompt as a template"),
        ["templates.none"] = ("(Belum ada template)", "(No templates yet)"),
        ["settings.language"] = ("Bahasa", "Language"),
        ["settings.diagnosticsLog"] = ("Log diagnostik", "Diagnostic log"),
        ["settings.openLogs"] = ("Buka folder log", "Open logs folder"),
        ["settings.close"] = ("Tutup", "Close"),
    };

    /// <summary>Look up a key for a language; returns the key itself when not yet translated.</summary>
    public static string Get(AppLanguage language, string key) =>
        Table.TryGetValue(key, out var entry)
            ? (language == AppLanguage.En ? entry.En : entry.Id)
            : key;

    public static bool Contains(string key) => Table.ContainsKey(key);
}
