namespace CodexBar.Core;

/// <summary>One thing the notch can do, which the user switches on or off.</summary>
public sealed record NotchFeature(
    string Key,
    string Group,
    string NameId,
    string NameEn,
    string DescriptionId,
    string DescriptionEn,
    Func<AppSettings, bool> IsOn,
    Action<AppSettings, bool> Set,
    bool Recommended = false)
{
    public string Name(Func<string, string, string> text) => text(NameId, NameEn);
    public string Description(Func<string, string, string> text) => text(DescriptionId, DescriptionEn);
}

/// <summary>
/// Every optional notch feature in one list: the welcome window offers them to a new user (all
/// off; a few suggested), and settings switch them later. Features that install something outside
/// TheAI (Claude Code / Gemini hooks, the PowerShell profile line) are offered by the app itself.
/// </summary>
public static class NotchFeatures
{
    public const string Day = "day";
    public const string Work = "work";
    public const string Computer = "computer";

    public static readonly IReadOnlyList<NotchFeature> All =
    [
        new("music", Day, "Musik", "Music",
            "Lagu yang sedang diputar, kontrol, dan volume per aplikasi.", "What's playing, its controls and per-app volume.",
            s => s.MusicInNotch, (s, on) => s.MusicInNotch = on, Recommended: true),
        new("timer", Day, "Timer & pomodoro", "Timer & pomodoro",
            "Hitung mundur, stopwatch, pomodoro, dan alarm.", "Countdowns, a stopwatch, pomodoro and alarms.",
            s => s.TimerInNotch, (s, on) => s.TimerInNotch = on, Recommended: true),
        new("calendar", Day, "Kalender", "Calendar",
            "Hitung mundur ke rapat berikutnya dan tombol gabung. Dari Outlook; tautan iCal di Pengaturan.", "A countdown to your next meeting and a join button. From Outlook; an iCal link in Settings.",
            s => s.CalendarSource != "off", (s, on) => s.CalendarSource = on ? (s.CalendarSource == "off" ? "outlook" : s.CalendarSource) : "off"),
        new("weather", Day, "Cuaca & jam dunia", "Weather & world clock",
            "Cuaca kotamu dan jam di zona waktu lain.", "Your city's weather and the time in other zones.",
            s => s.WeatherEnabled, (s, on) => s.WeatherEnabled = on),
        new("reminders", Day, "Pengingat & to-do", "Reminders & to-do",
            "Catatan singkat dan pengingat yang muncul tepat waktu.", "Quick notes and reminders that show up on time.",
            s => s.RemindersEnabled, (s, on) => s.RemindersEnabled = on),
        new("recap", Day, "Rekap harian", "Daily recap",
            "Fokus, hasil agent, dan file yang berubah hari ini, siap disalin untuk standup.", "Today's focus, agent results and changed files, ready to copy for standup.",
            s => s.RecapEvening, (s, on) => s.RecapEvening = on),

        new("focus", Work, "Tahan agent saat fokus", "Hold agents during focus",
            "Saat pomodoro atau rapat, hasil dan permintaan agent menunggu sampai istirahat.", "During a pomodoro or a meeting, agent results and requests wait for the break.",
            s => s.HoldDuringFocus, (s, on) => s.HoldDuringFocus = on),
        new("activities", Work, "Aktivitas dari skrip", "Activities from scripts",
            "Build, deploy, dan skrip apa pun melapor progresnya ke notch.", "Builds, deploys and any script report their progress to the notch.",
            s => s.LiveActivities, (s, on) => s.LiveActivities = on),
        new("github", Work, "CI pull request", "Pull request CI",
            "Check PR branch-mu lewat GitHub CLI yang sudah login.", "Your branch's PR checks, through your signed-in GitHub CLI.",
            s => s.GitHubChecks, (s, on) => s.GitHubChecks = on),

        new("hud", Computer, "Volume, kecerahan & baterai", "Volume, brightness & battery",
            "Menggantikan pop-up Windows dengan notch.", "Shown in the notch instead of Windows' pop-ups.",
            s => s.SystemHud, (s, on) => s.SystemHud = on, Recommended: true),
        new("clipboard", Computer, "Riwayat clipboard", "Clipboard history",
            "30 salinan terakhir, hanya di memori. Salinan password manager dilewati.", "Your last 30 copies, in memory only. Password managers' copies are skipped.",
            s => s.ClipboardHistory, (s, on) => s.ClipboardHistory = on),
        new("downloads", Computer, "Unduhan ke rak file", "Downloads on the shelf",
            "File yang selesai diunduh tersedia di notch selama 2 jam.", "Finished downloads wait in the notch for two hours.",
            s => s.DownloadsToShelf, (s, on) => s.DownloadsToShelf = on),
        new("system", Computer, "Monitor sistem", "System monitor",
            "CPU, GPU, RAM, dan disk, plus peringatan saat hampir penuh.", "CPU, GPU, memory and disk, plus a warning when running short.",
            s => s.SystemMonitor, (s, on) => s.SystemMonitor = on),
    ];

    public static NotchFeature? Find(string key) => All.FirstOrDefault(feature => feature.Key == key);

    /// <summary>Switch on exactly the chosen features (by key); everything else off.</summary>
    public static void Apply(AppSettings settings, IEnumerable<string> chosen)
    {
        var keys = chosen.ToHashSet(StringComparer.Ordinal);
        foreach (var feature in All)
        {
            feature.Set(settings, keys.Contains(feature.Key));
        }
    }
}
