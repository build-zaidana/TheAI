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
        ["settings.outputDocked"] = ("Output menyatu", "Docked output"),
        ["settings.menuDocked"] = ("Menu menyatu", "Docked menu"),
        ["settings.wrapCode"] = ("Wrap kode", "Wrap code"),
        ["settings.showThinking"] = ("Tampilkan thinking", "Show thinking"),
        ["settings.pin"] = ("Pin", "Pin"),
        ["settings.showOutput"] = ("Tampilkan output", "Show output"),
        ["settings.compact"] = ("Padatkan konteks", "Compact context"),
        ["settings.copyConversation"] = ("Salin percakapan", "Copy conversation"),
        ["settings.exportCheckpoint"] = ("Export checkpoint", "Export checkpoint"),
        ["settings.resetLayout"] = ("Reset layout", "Reset layout"),
        ["settings.appearance"] = ("Tampilan", "Appearance"),
        ["settings.width"] = ("Lebar", "Width"),
        ["settings.outputHeight"] = ("Tinggi output", "Output height"),
        ["settings.inputFont"] = ("Font input", "Input font"),
        ["settings.responseFont"] = ("Font respons", "Response font"),
        ["bar.menu"] = ("Menu & riwayat Codex", "Codex menu & history"),
        ["bar.workspace"] = ("Pilih workspace", "Choose workspace"),
        ["bar.attach"] = ("Tambahkan gambar atau file", "Add image or file"),
        ["bar.send"] = ("Kirim (Enter)", "Send (Enter)"),
        ["bar.stop"] = ("Hentikan pekerjaan (Ctrl+.)", "Stop (Ctrl+.)"),
        ["bar.contextUsage"] = ("Pemakaian context thread; klik untuk compact", "Thread context usage; click to compact"),
        ["bar.conversation"] = ("Tampilkan/sembunyikan percakapan", "Show/hide conversation"),
        ["bar.settings"] = ("Settings", "Settings"),
        ["sidebar.newThread"] = ("＋  Percakapan baru", "＋  New conversation"),
        ["sidebar.search"] = ("Cari percakapan", "Search conversations"),
        ["sidebar.empty"] = ("Belum ada percakapan", "No conversations yet"),
        ["sidebar.footer"] = ("Riwayat lokal Codex", "Local Codex history"),
        ["sidebar.close"] = ("Tutup menu", "Close menu"),
        ["sidebar.reload"] = ("Muat ulang", "Reload"),
        ["output.copy"] = ("Salin output", "Copy output"),
        ["output.openCodex"] = ("Buka di Codex", "Open in Codex"),
        ["output.hide"] = ("Sembunyikan output", "Hide output"),
        ["output.jumpLatest"] = ("↓ Terbaru", "↓ Latest"),
        ["approval.title"] = ("Persetujuan Codex", "Codex approval"),
        ["approval.heading"] = ("Codex membutuhkan izin", "Codex needs permission"),
        ["approval.subtitle"] = ("Tinjau tindakan dan pilih cakupan izin yang paling sempit.", "Review the action and pick the narrowest permission scope."),
        ["approval.detailsName"] = ("Detail tindakan yang membutuhkan izin", "Details of the action requiring permission"),
        ["approval.cancel"] = ("Tolak & hentikan", "Decline & stop"),
        ["approval.decline"] = ("Tolak", "Decline"),
        ["approval.acceptSession"] = ("Izinkan sesi", "Allow for session"),
        ["approval.acceptOnce"] = ("Izinkan sekali", "Allow once"),
    };

    /// <summary>Look up a key for a language; returns the key itself when not yet translated.</summary>
    public static string Get(AppLanguage language, string key) =>
        Table.TryGetValue(key, out var entry)
            ? (language == AppLanguage.En ? entry.En : entry.Id)
            : key;

    public static bool Contains(string key) => Table.ContainsKey(key);
}
