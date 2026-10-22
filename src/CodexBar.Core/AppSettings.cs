namespace CodexBar.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 12;
    public string Provider { get; set; } = "codex";

    /// <summary>Bahasa UI: "auto" (ikuti Windows: Indonesia bila Windows berbahasa Indonesia, selain itu Inggris), "id" atau "en".</summary>
    public string Language { get; set; } = "auto";
    public string Hotkey { get; set; } = "Win+Shift+C";

    /// <summary>Hotkey khusus quick-capture: kirim teks terseleksi langsung tanpa membuka window.</summary>
    public string QuickCaptureHotkey { get; set; } = "Win+Shift+X";

    /// <summary>Hotkey screenshot area: pilih bagian layar lalu lampirkan ke prompt berikutnya.</summary>
    public string ScreenshotHotkey { get; set; } = "Win+Shift+Z";

    /// <summary>Hotkey input suara: tekan untuk mulai merekam, tekan lagi untuk berhenti dan mentranskripsi.</summary>
    public string VoiceHotkey { get; set; } = "Win+Alt+V";

    /// <summary>Hotkey chat: buka chat di samping notch (tekan lagi untuk menutup).</summary>
    public string ChatHotkey { get; set; } = "Win+Alt+C";

    /// <summary>Lebar chat di samping notch (DIP); 0 = bawaan.</summary>
    public double ChatWidth { get; set; }

    /// <summary>Bahasa ucapan untuk whisper: "id", "en", atau "auto" (deteksi, lebih lambat).</summary>
    public string VoiceLanguage { get; set; } = "id";

    /// <summary>Model whisper: "base" (cepat) atau "small" (lebih akurat).</summary>
    public string VoiceModel { get; set; } = "base";

    /// <summary>Sesi Claude Code dari terminal/IDE/desktop tampil live di notch (lewat hooks di ~/.claude/settings.json).</summary>
    public bool ClaudeLiveSessions { get; set; }

    /// <summary>Sesi Gemini CLI tampil live di notch (lewat hooks di ~/.gemini/settings.json).</summary>
    public bool GeminiLiveSessions { get; set; }

    /// <summary>Sesi lama Claude Code (dari ~/.claude/projects) ikut tampil di daftar Lanjutkan.</summary>
    public bool ClaudeImportSessions { get; set; } = true;

    /// <summary>Tebak prompt berikutnya setelah agent selesai (teks samar di input notch).</summary>
    public bool PromptSuggestions { get; set; } = true;

    /// <summary>Kirim ke sesi Claude Code yang sedang terbuka di aplikasinya: fork otomatis dulu.</summary>
    public bool AutoForkElsewhere { get; set; }

    /// <summary>Cek rilis baru di repositori GitHub publik saat mulai dan tiap hari.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Repositori rilis publik ("pemilik/nama").</summary>
    public const string ReleaseRepository = "build-zaidana/theai-app";

    public string UpdateRepository { get; set; } = ReleaseRepository;

    /// <summary>Tag rilis yang dipilih untuk dilewati (tidak diberi tahu lagi).</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>Sumber tebakan: "habit" (kebiasaan saja) atau "habit+ai" (model kecil saat kebiasaan belum yakin).</summary>
    public string PromptSuggestionSource { get; set; } = "habit";

    /// <summary>Sisi chat yang tumbuh dari notch: "auto" (terdekat dengan notch), "right", atau "left".</summary>
    public string ChatSide { get; set; } = "auto";

    /// <summary>Quick-capture: "notch" (notch menawarkan terjemahkan/jelaskan/ringkas/perbaiki/tanya), "capture" (kirim ke chat, tampil saat kirim), "done" (tampil saat selesai), atau "clipboard" (jawaban ke clipboard).</summary>
    public string QuickCaptureReveal { get; set; } = "notch";
    public bool LaunchAtLogin { get; set; }

    /// <summary>Tampilan utama: "notch" (menempel tepi atas), "island" (Dynamic Island melayang) atau "bar" (di atas taskbar).</summary>
    public string DisplayMode { get; set; } = "notch";

    /// <summary>Mode notch: sembunyikan pil saat tidak ada pekerjaan (muncul lagi saat agent bekerja).</summary>
    public bool NotchHideWhenIdle { get; set; }

    /// <summary>Notch menampilkan lagu yang diputar (kontrol media Windows): art, kontrol, volume per aplikasi.</summary>
    public bool MusicInNotch { get; set; } = true;

    /// <summary>Notifikasi kecil di notch saat lagu berganti.</summary>
    public bool MusicTrackToast { get; set; } = true;

    /// <summary>Lirik tersinkron dari lrclib.net (mengirim judul dan artis lagu); mati sampai dinyalakan.</summary>
    public bool MusicLyrics { get; set; }

    /// <summary>Volume, kecerahan, baterai dan headphone tampil sebentar di notch (seperti Dynamic Island).</summary>
    public bool SystemHud { get; set; } = true;

    /// <summary>Sumber kalender di notch: "off", "outlook" (lokal) atau "ics" (tautan iCal, disimpan di Credential Manager).</summary>
    public string CalendarSource { get; set; } = "off";

    /// <summary>Sembunyikan notch selama aplikasi layar penuh (game, video, presentasi) menutupi monitornya.</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Skrip boleh menampilkan aktivitas di notch lewat `TheAI.exe activity` / named pipe pengguna ini.</summary>
    public bool LiveActivities { get; set; } = true;

    /// <summary>CI pull request branch yang sedang dikerjakan tampil di notch (lewat GitHub CLI yang sudah login).</summary>
    public bool GitHubChecks { get; set; }

    /// <summary>Rekap hari ini muncul sendiri sekali di sore hari (17.00).</summary>
    public bool RecapEvening { get; set; } = true;

    /// <summary>Riwayat clipboard di notch (hanya di memori; yang disematkan disimpan). Isian pribadi dari password manager dilewati.</summary>
    public bool ClipboardHistory { get; set; }

    /// <summary>Unduhan yang selesai masuk rak file selama 2 jam.</summary>
    public bool DownloadsToShelf { get; set; } = true;

    /// <summary>Halaman sistem (CPU, GPU, RAM, disk) di notch dan peringatan saat RAM / disk C: hampir penuh.</summary>
    public bool SystemMonitor { get; set; } = true;

    /// <summary>Selama pomodoro fokus atau rapat, hasil agent dan permintaan izin ditahan dan muncul sekaligus saat istirahat.</summary>
    public bool HoldDuringFocus { get; set; } = true;

    /// <summary>Timer, stopwatch, pomodoro dan alarm sebagai halaman notch.</summary>
    public bool TimerInNotch { get; set; } = true;

    /// <summary>Cuaca (Open-Meteo, tanpa API key) dan jam dunia sebagai halaman notch.</summary>
    public bool WeatherEnabled { get; set; }

    /// <summary>Kota untuk cuaca (dicari lewat geocoding Open-Meteo); kosong = belum diatur.</summary>
    public string WeatherCity { get; set; } = string.Empty;

    /// <summary>Zona waktu tambahan untuk jam dunia (ID Windows atau IANA), dipisah koma.</summary>
    public string WorldClocks { get; set; } = string.Empty;

    /// <summary>HUD Caps Lock dan pergantian bahasa keyboard (hanya tombol Caps Lock yang dibaca; tidak ada yang disimpan).</summary>
    public bool KeyboardHud { get; set; }

    /// <summary>Pengingat minum air yang lembut, dengan catatan harian di notch.</summary>
    public bool WaterEnabled { get; set; }

    public int WaterGoalMl { get; set; } = 2000;

    public int WaterGlassMl { get; set; } = 250;

    /// <summary>Menit antar pengingat setelah gelas terakhir; 0 = tanpa pengingat (catatan saja).</summary>
    public int WaterEveryMinutes { get; set; } = 60;

    public int WaterFromHour { get; set; } = 8;

    public int WaterUntilHour { get; set; } = 21;

    /// <summary>Pengingat dan to-do singkat di notch.</summary>
    public bool RemindersEnabled { get; set; }

    /// <summary>Jendela sambutan (tur + pilih fitur) sudah ditunjukkan.</summary>
    public bool WelcomeShown { get; set; } = true;

    /// <summary>
    /// A new user starts with only the core (the agent and the launcher): every notch feature is off
    /// until they pick it in the welcome window. Existing users keep what they had.
    /// </summary>
    public void ApplyNewUserDefaults()
    {
        MusicInNotch = false;
        SystemHud = false;
        KeyboardHud = false;
        TimerInNotch = false;
        LiveActivities = false;
        HoldDuringFocus = false;
        RecapEvening = false;
        DownloadsToShelf = false;
        SystemMonitor = false;
        ClipboardHistory = false;
        GitHubChecks = false;
        WeatherEnabled = false;
        RemindersEnabled = false;
        CalendarSource = "off";
        WelcomeShown = false;
    }

    /// <summary>Sisi layar tempat notch menempel: top, bottom, left, right.</summary>
    public string NotchEdge { get; set; } = "top";

    /// <summary>Geser notch dari tengah sisinya, sebagai fraksi panjang sisi (-0.45..0.45).</summary>
    public double NotchOffset { get; set; }
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

    /// <summary>Salin otomatis respons ke clipboard saat selesai. Default mati.</summary>
    public bool AutoCopyResponse { get; set; }

    /// <summary>Bunyi pelan saat agent selesai menjawab.</summary>
    public bool CompletionSound { get; set; } = true;

    /// <summary>Notifikasi Windows (dengan Lihat / Salin jawaban) saat jawaban selesai tapi tidak sedang dilihat.</summary>
    public bool CompletionNotification { get; set; } = true;

    /// <summary>Aktifkan diagnostics logging opt-in ke file (untuk pelaporan bug). Default mati.</summary>
    public bool EnableDiagnosticsLogging { get; set; }

    /// <summary>Prompt yang menunggu dikirim ulang saat kuota reset (auto-resume). Null bila tak ada. Bertahan lintas restart.</summary>
    public PendingResume? PendingResume { get; set; }

    /// <summary>Template prompt tersimpan untuk quick-insert. Di-seed dengan contoh pada instal baru.</summary>
    public List<PromptTemplate> PromptTemplates { get; set; } = PromptTemplate.CreateDefaults();

    /// <summary>Prompt terjadwal yang akan dikirim otomatis pada waktu tertentu.</summary>
    public List<ScheduledPrompt> ScheduledPrompts { get; set; } = new();
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
        QuickCaptureHotkey = source.QuickCaptureHotkey;
        ScreenshotHotkey = source.ScreenshotHotkey;
        VoiceHotkey = source.VoiceHotkey;
        ChatHotkey = source.ChatHotkey;
        ChatWidth = source.ChatWidth;
        VoiceLanguage = source.VoiceLanguage;
        VoiceModel = source.VoiceModel;
        ClaudeLiveSessions = source.ClaudeLiveSessions;
        GeminiLiveSessions = source.GeminiLiveSessions;
        MusicInNotch = source.MusicInNotch;
        MusicTrackToast = source.MusicTrackToast;
        MusicLyrics = source.MusicLyrics;
        SystemHud = source.SystemHud;
        KeyboardHud = source.KeyboardHud;
        WaterEnabled = source.WaterEnabled;
        WaterGoalMl = source.WaterGoalMl;
        WaterGlassMl = source.WaterGlassMl;
        WaterEveryMinutes = source.WaterEveryMinutes;
        WaterFromHour = source.WaterFromHour;
        WaterUntilHour = source.WaterUntilHour;
        CalendarSource = source.CalendarSource;
        HideInFullscreen = source.HideInFullscreen;
        LiveActivities = source.LiveActivities;
        GitHubChecks = source.GitHubChecks;
        RecapEvening = source.RecapEvening;
        ClipboardHistory = source.ClipboardHistory;
        DownloadsToShelf = source.DownloadsToShelf;
        SystemMonitor = source.SystemMonitor;
        HoldDuringFocus = source.HoldDuringFocus;
        TimerInNotch = source.TimerInNotch;
        WeatherEnabled = source.WeatherEnabled;
        WeatherCity = source.WeatherCity;
        WorldClocks = source.WorldClocks;
        RemindersEnabled = source.RemindersEnabled;
        ClaudeImportSessions = source.ClaudeImportSessions;
        PromptSuggestions = source.PromptSuggestions;
        AutoForkElsewhere = source.AutoForkElsewhere;
        CheckForUpdates = source.CheckForUpdates;
        UpdateRepository = source.UpdateRepository;
        SkippedUpdate = source.SkippedUpdate;
        PromptSuggestionSource = source.PromptSuggestionSource;
        ChatSide = source.ChatSide;
        QuickCaptureReveal = source.QuickCaptureReveal;
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
        AutoCopyResponse = source.AutoCopyResponse;
        CompletionSound = source.CompletionSound;
        CompletionNotification = source.CompletionNotification;
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

/// <summary>
/// A prompt queued for auto-resend once the provider's usage limit resets. Persisted in
/// settings so it survives an app restart. <see cref="IsContinuation"/> means the previous
/// response was cut off mid-stream, so on resume we ask the model to continue rather than
/// resending the original prompt.
/// </summary>
public sealed class PendingResume
{
    public string Prompt { get; set; } = string.Empty;
    public DateTimeOffset RetryAt { get; set; }
    public bool IsContinuation { get; set; }
    public string? ThreadId { get; set; }
}

/// <summary>A prompt queued to run automatically at <see cref="RunAt"/>.</summary>
public sealed class ScheduledPrompt
{
    public const int MaxCount = 50;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Prompt { get; set; } = string.Empty;
    public DateTimeOffset RunAt { get; set; }

    /// <summary>Clean a list: drop entries with a blank prompt, order by time, cap the count.</summary>
    public static List<ScheduledPrompt> Normalize(IEnumerable<ScheduledPrompt>? items)
    {
        if (items is null)
        {
            return new List<ScheduledPrompt>();
        }

        return items
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Prompt))
            .OrderBy(item => item.RunAt)
            .Take(MaxCount)
            .ToList();
    }

    /// <summary>Entries whose time has arrived (RunAt &lt;= now), earliest first.</summary>
    public static List<ScheduledPrompt> Due(IEnumerable<ScheduledPrompt>? items, DateTimeOffset now) =>
        items is null
            ? new List<ScheduledPrompt>()
            : items.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Prompt) && item.RunAt <= now)
                .OrderBy(item => item.RunAt)
                .ToList();
}
