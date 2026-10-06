using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexBar.App;

/// <summary>
/// Everything the settings window needs from the app. The window only edits
/// <see cref="Settings"/> and asks the host to apply it; actions with their own validation or
/// confirmation (provider switch, permissions, hotkeys, API keys, dialogs) go through the host so
/// the existing, tested code paths stay the only ones.
/// </summary>
internal interface ISettingsHost
{
    AppSettings Settings { get; }
    string ProviderId { get; }
    bool ProviderSupportsApprovals { get; }
    string WorkspacePath { get; }
    IReadOnlyList<ProviderModelInfo> Models { get; }

    void ApplySettings();
    void SelectProvider(string providerId);
    Task<ProviderAccountInfo?> GetAccountAsync();
    string SaveApiKey(string key);
    string ClearApiKey();
    void SelectModel(ProviderModelInfo model);
    void SelectSandbox(string mode);
    void ChooseWorkspace();
    (bool Ok, string Message) ApplyHotkey(string gesture);
    (bool Ok, string Message) ApplyQuickCaptureHotkey(string gesture);
    (bool Ok, string Message) ApplyScreenshotHotkey(string gesture);
    (bool Ok, string Message) ApplyVoiceHotkey(string gesture);
    (bool Ok, string Message) ApplyChatHotkey(string gesture);
    (bool Ok, string Message) SetClaudeLiveSessions(bool on);
    Task InstallVoiceAsync(IProgress<double> progress, CancellationToken cancellationToken);
    void UninstallVoice();
    void OpenLogin();
    void RunAction(string action);

    event EventHandler? StateChanged;
}

public partial class SettingsWindow : Window
{
    private static readonly string[] ApiKeyProviders = ["anthropic", "openai", "openrouter"];
    private static readonly Brush Green = Frozen(Color.FromRgb(0x28, 0xA7, 0x45));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xF2, 0x9B, 0x0A));
    private static readonly Brush Grey = Frozen(Color.FromRgb(0xC7, 0xC7, 0xCC));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xE5, 0x48, 0x3E));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x6E, 0x6E, 0x73));

    private readonly ISettingsHost _host;
    private readonly DispatcherTimer _sliderApply = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;

    internal SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        _sliderApply.Tick += (_, _) =>
        {
            _sliderApply.Stop();
            ReadSliders();
            _host.ApplySettings();
        };

        ApiKeyHint.Text = Tr.Text(
            "Disimpan di Windows Credential Manager, tidak pernah ditulis ke file pengaturan.",
            "Stored in Windows Credential Manager, never written to the settings file.");
        HotkeyHint.Text = Tr.Text(
            "Klik kolom, lalu tekan kombinasi baru (misalnya Ctrl+Shift+A). Esc untuk batal.",
            "Click a field, then press the new combination (e.g. Ctrl+Shift+A). Esc cancels.");
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"CodexBar {version}";

        _host.StateChanged += Host_StateChanged;
        Closed += (_, _) => _host.StateChanged -= Host_StateChanged;
        Load();
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "provider" => NavProvider,
            "model" => NavModel,
            "workspace" => NavWorkspace,
            "appearance" => NavAppearance,
            "shortcuts" => NavShortcuts,
            "advanced" => NavAdvanced,
            _ => NavGeneral
        };
        nav.IsChecked = true;
    }

    // ---- Load ------------------------------------------------------------------------------

    private void Load()
    {
        _loading = true;
        var s = _host.Settings;

        Select(LanguagePopup, s.Language);
        Select(ThemePopup, s.Appearance.Theme.ToLowerInvariant());
        LaunchAtLoginSwitch.IsChecked = s.LaunchAtLogin;
        AutoCopySwitch.IsChecked = s.AutoCopyResponse;
        CompletionSoundSwitch.IsChecked = s.CompletionSound;
        SuggestSwitch.IsChecked = s.PromptSuggestions;
        AutoForkSwitch.IsChecked = s.AutoForkElsewhere;
        Select(ChatSidePopup, s.ChatSide);
        SuggestHabitItem.Content = Tr.Text("Kebiasaan saja", "Habits only");
        SuggestAiItem.Content = Tr.Text("Kebiasaan + AI", "Habits + AI");
        Select(SuggestSourcePopup, s.PromptSuggestionSource);
        SuggestHint.Text = Tr.Text(
            "Setelah agent selesai, kolom input menampilkan tebakan samar: Tab untuk memakai, Enter untuk langsung mengirim. \"Kebiasaan saja\" belajar dari prompt pendek yang sering kamu kirim dan tidak memakai kuota. \"Kebiasaan + AI\" memanggil Claude Haiku lewat Claude Code saat kebiasaanmu belum cukup yakin.",
            "After the agent finishes, the input shows a faint guess: Tab takes it, Enter sends it. \"Habits only\" learns from the short prompts you often send and uses no quota. \"Habits + AI\" asks Claude Haiku through Claude Code when your habits aren't sure.");
        NotifyHint.Text = Tr.Text(
            "Notifikasi muncul saat jawaban selesai tapi tidak sedang kamu lihat: jendela tertutup, notch tersembunyi, atau giliran yang lama. Mode Fokus Windows tetap dihormati.",
            "A notification appears when an answer finishes while you're not looking: window closed, notch hidden, or a long turn. Windows Focus mode is respected.");
        CompletionNotificationSwitch.IsChecked = s.CompletionNotification;
        AlwaysOnTopSwitch.IsChecked = s.AlwaysOnTop;

        ModeNotch.IsChecked = s.DisplayMode == "notch";
        ModeIsland.IsChecked = s.DisplayMode == "island";
        HideIdleSwitch.IsChecked = s.NotchHideWhenIdle;
        BarWidthSlider.Value = Math.Clamp(s.Window.Width, 500, 1200);
        BarHeightSlider.Value = Math.Clamp(s.Window.MaxExpandedHeight, 180, 900);
        InputFontSlider.Value = Math.Clamp(s.Appearance.InputFontSize, 10, 24);
        ResponseFontSlider.Value = Math.Clamp(s.Appearance.ResponseFontSize, 10, 24);
        DockedSwitch.IsChecked = s.OutputDocked;
        Select(OutputPositionPopup, s.OutputPosition);

        Select(ProviderPopup, _host.ProviderId);
        ReasoningSwitch.IsChecked = s.ShowReasoning;
        foreach (var segment in EffortGroup.Children.OfType<RadioButton>())
        {
            segment.IsChecked = (string)segment.Tag == (s.ReasoningEffort ?? string.Empty);
        }

        Select(SandboxPopup, s.SandboxMode);
        Select(ApprovalPopup, s.ApprovalPolicy);
        HotkeyField.Text = s.Hotkey;
        QuickHotkeyField.Text = s.QuickCaptureHotkey;
        ShotHotkeyField.Text = s.ScreenshotHotkey;
        ClaudeLiveSwitch.IsChecked = s.ClaudeLiveSessions && ClaudeHooksInstaller.IsInstalled();
        ClaudeImportSwitch.IsChecked = s.ClaudeImportSessions;
        ClaudeLiveStatus.Text = ClaudeLiveSwitch.IsChecked == true
            ? Tr.Text("Hooks terpasang di ~/.claude/settings.json", "Hooks installed in ~/.claude/settings.json")
            : Tr.Text("Memasang hooks di ~/.claude/settings.json", "Adds hooks to ~/.claude/settings.json");
        ClaudeSyncHint.Text = Tr.Text(
            "Sesi dari terminal, VS Code, dan aplikasi desktop ikut muncul di notch: langkah kerja, jawaban, dan permintaan izin yang bisa dijawab dari notch (kalau tidak dijawab dalam 30 detik, Claude Code bertanya seperti biasa). Mematikan sakelar ini melepas hooks-nya lagi.",
            "Sessions from the terminal, VS Code, and the desktop app show up in the notch: steps, answers, and permission requests you can answer from the notch (unanswered after 30 seconds, Claude Code asks as usual). Turning this off removes the hooks again.");
        VoiceHotkeyField.Text = s.VoiceHotkey;
        ChatHotkeyField.Text = s.ChatHotkey;
        Select(VoiceLanguagePopup, s.VoiceLanguage);
        VoiceModelBase.Content = Tr.Text("Cepat (base · 57 MB)", "Fast (base · 57 MB)");
        VoiceModelSmall.Content = Tr.Text("Akurat (small · 181 MB)", "Accurate (small · 181 MB)");
        Select(VoiceModelPopup, s.VoiceModel);
        VoiceHint.Text = Tr.Text(
            "Tahan tombol mic di notch untuk bicara, atau tekan hotkey input suara untuk mulai dan sekali lagi untuk selesai. Suara diproses di PC ini dengan whisper.cpp, tanpa internet.",
            "Hold the mic button in the notch to talk, or press the voice hotkey to start and again to finish. Speech is processed on this PC with whisper.cpp, offline.");
        RenderVoiceStatus();
        Select(QuickRevealPopup, s.QuickCaptureReveal);
        DiagnosticsSwitch.IsChecked = s.EnableDiagnosticsLogging;

        LoadProviderDependent();
        UpdateNotchGroup();
        _loading = false;
        _ = RefreshAccountAsync();
    }

    // Provider, models, workspace and approvals change underneath us (provider switch, folder
    // picked, models loaded): refresh just those parts.
    private void LoadProviderDependent()
    {
        var wasLoading = _loading;
        _loading = true;
        var providerId = _host.ProviderId;
        Select(ProviderPopup, providerId);
        var usesKey = ApiKeyProviders.Contains(providerId);
        ApiKeyGroup.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.Visibility = usesKey ? Visibility.Collapsed : Visibility.Visible;
        ProviderHint.Text = providerId switch
        {
            "codex" => Tr.Text("Memakai Codex CLI yang login dengan akun ChatGPT.", "Uses the Codex CLI signed in with your ChatGPT account."),
            "claude-code" => Tr.Text("Memakai Claude Code yang login dengan langganan Pro atau Max. Tidak perlu API key.", "Uses Claude Code signed in with a Pro or Max subscription. No API key needed."),
            _ => string.Empty
        };
        AccountName.Text = ProviderName(providerId);

        var models = _host.Models;
        ModelPopup.ItemsSource = models;
        ModelPopup.SelectedItem = models.FirstOrDefault(m => string.Equals(m.Model, _host.Settings.SelectedModel, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
        ModelHint.Text = models.Count == 0
            ? Tr.Text("Daftar model belum tersedia. Hubungkan provider dulu.", "No models yet. Connect the provider first.")
            : Tr.Text("Berlaku mulai pesan berikutnya.", "Applies from the next message.");

        WorkspacePathText.Text = _host.WorkspacePath;
        WorkspacePathText.ToolTip = _host.WorkspacePath;
        var approvals = _host.ProviderSupportsApprovals ? Visibility.Visible : Visibility.Collapsed;
        ApprovalRow.Visibility = approvals;
        ApprovalSeparator.Visibility = approvals;
        Select(SandboxPopup, _host.Settings.SandboxMode);
        SandboxHint.Text = _host.Settings.SandboxMode switch
        {
            "read-only" => Tr.Text("Agent hanya membaca file dan menyusun rencana. Tidak ada yang diubah.", "The agent only reads files and plans. Nothing is changed."),
            "danger-full-access" => Tr.Text("Agent boleh menjalankan perintah apa pun tanpa bertanya. Pakai hanya di folder tepercaya.", "The agent may run any command without asking. Use only in trusted folders."),
            _ => Tr.Text("Agent boleh mengubah file di folder ini. Perintah lain mengikuti aturan persetujuan.", "The agent may edit files in this folder. Other commands follow the approval rule.")
        };
        SandboxHint.Foreground = _host.Settings.SandboxMode == "danger-full-access" ? Red : Muted;
        _loading = wasLoading;
    }

    private async Task RefreshAccountAsync()
    {
        AccountStatus.Text = Tr.Text("Memeriksa…", "Checking…");
        StatusText.Text = AccountStatus.Text;
        StatusDot.Fill = Grey;
        ProviderAccountInfo? account = null;
        try
        {
            account = await _host.GetAccountAsync();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Info("Settings", exception.Message);
        }

        string text;
        if (account is null)
        {
            text = Tr.Text("Tidak terhubung", "Not connected");
            StatusDot.Fill = Grey;
        }
        else if (!account.IsAuthenticated)
        {
            text = ApiKeyProviders.Contains(_host.ProviderId)
                ? Tr.Text("API key belum diatur", "API key not set")
                : Tr.Text("Belum masuk", "Not signed in");
            StatusDot.Fill = Orange;
        }
        else
        {
            var plan = string.IsNullOrWhiteSpace(account.PlanType) ? null : Capitalise(account.PlanType);
            text = plan is null ? Tr.Text("Terhubung", "Connected") : Tr.Text($"Terhubung · {plan}", $"Connected · {plan}");
            StatusDot.Fill = Green;
        }

        StatusText.Text = text;
        AccountStatus.Text = text;
    }

    private void Host_StateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        LoadProviderDependent();
        _ = RefreshAccountAsync();
    });

    // ---- Apply -----------------------------------------------------------------------------

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var s = _host.Settings;
        s.Language = TagOf(LanguagePopup) == "en" ? "en" : "id";
        s.Appearance.Theme = TagOf(ThemePopup) ?? "system";
        s.LaunchAtLogin = LaunchAtLoginSwitch.IsChecked == true;
        s.AutoCopyResponse = AutoCopySwitch.IsChecked == true;
        s.CompletionSound = CompletionSoundSwitch.IsChecked == true;
        s.PromptSuggestions = SuggestSwitch.IsChecked == true;
        s.AutoForkElsewhere = AutoForkSwitch.IsChecked == true;
        s.ChatSide = TagOf(ChatSidePopup) ?? s.ChatSide;
        s.PromptSuggestionSource = TagOf(SuggestSourcePopup) ?? s.PromptSuggestionSource;
        s.ClaudeImportSessions = ClaudeImportSwitch.IsChecked == true;
        s.VoiceLanguage = TagOf(VoiceLanguagePopup) ?? s.VoiceLanguage;
        s.VoiceModel = TagOf(VoiceModelPopup) ?? s.VoiceModel;
        RenderVoiceStatus();
        s.CompletionNotification = CompletionNotificationSwitch.IsChecked == true;
        s.AlwaysOnTop = AlwaysOnTopSwitch.IsChecked == true;
        s.NotchHideWhenIdle = HideIdleSwitch.IsChecked == true;
        s.OutputDocked = DockedSwitch.IsChecked == true;
        s.OutputPosition = TagOf(OutputPositionPopup) == "above" ? "above" : "below";
        s.ShowReasoning = ReasoningSwitch.IsChecked == true;
        s.ApprovalPolicy = TagOf(ApprovalPopup) ?? s.ApprovalPolicy;
        s.QuickCaptureReveal = TagOf(QuickRevealPopup) ?? s.QuickCaptureReveal;
        s.EnableDiagnosticsLogging = DiagnosticsSwitch.IsChecked == true;
        ReadSliders();
        _host.ApplySettings();
    }

    private void ReadSliders()
    {
        var s = _host.Settings;
        s.Window.Width = Math.Round(BarWidthSlider.Value);
        s.Window.MaxExpandedHeight = Math.Round(BarHeightSlider.Value);
        s.Appearance.InputFontSize = Math.Round(InputFontSlider.Value);
        s.Appearance.ResponseFontSize = Math.Round(ResponseFontSlider.Value);
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _sliderApply.Stop();
        _sliderApply.Start();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        UpdateNotchGroup();
        if (_loading)
        {
            return;
        }

        _host.Settings.DisplayMode = sender == ModeIsland ? "island" : "notch";
        _host.ApplySettings();
    }

    private void UpdateNotchGroup()
    {
        NotchGroup.Opacity = 1;
        NotchGroup.IsEnabled = true;
    }

    private void Effort_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string effort })
        {
            return;
        }

        _host.Settings.ReasoningEffort = effort;
        _host.ApplySettings();
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(ProviderPopup) is not { } providerId || providerId == _host.ProviderId)
        {
            return;
        }

        _host.SelectProvider(providerId);
        LoadProviderDependent();
        _ = RefreshAccountAsync();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ModelPopup.SelectedItem is ProviderModelInfo model)
        {
            _host.SelectModel(model);
        }
    }

    private void Sandbox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(SandboxPopup) is not { } mode || mode == _host.Settings.SandboxMode)
        {
            return;
        }

        _host.SelectSandbox(mode);
        // The host may ask for confirmation and decline; show whatever ended up applied.
        Dispatcher.BeginInvoke(LoadProviderDependent, DispatcherPriority.Background);
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyHint.Text = _host.SaveApiKey(ApiKeyField.Password);
        ApiKeyField.Clear();
        _ = RefreshAccountAsync();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyHint.Text = _host.ClearApiKey();
        _ = RefreshAccountAsync();
    }

    private void Login_Click(object sender, RoutedEventArgs e) => _host.OpenLogin();

    private void AccountCard_Click(object sender, RoutedEventArgs e) => NavProvider.IsChecked = true;

    private void ChooseWorkspace_Click(object sender, RoutedEventArgs e) => _host.ChooseWorkspace();

    private void Recenter_Click(object sender, RoutedEventArgs e) => _host.RunAction("recenter");

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _host.RunAction("logs");

    private void Templates_Click(object sender, RoutedEventArgs e) => _host.RunAction("templates");

    private void Export_Click(object sender, RoutedEventArgs e) => _host.RunAction("export");

    private void Import_Click(object sender, RoutedEventArgs e) => _host.RunAction("import");

    private void ReportBug_Click(object sender, RoutedEventArgs e) => _host.RunAction("bug");

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => _host.RunAction("reset-layout");

    // ---- Hotkeys -----------------------------------------------------------------------------

    private void HotkeyField_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var field = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var current = (string)field.Tag switch
        {
            "quick" => _host.Settings.QuickCaptureHotkey,
            "shot" => _host.Settings.ScreenshotHotkey,
            "voice" => _host.Settings.VoiceHotkey,
            "chat" => _host.Settings.ChatHotkey,
            _ => _host.Settings.Hotkey
        };

        if (key == Key.Escape)
        {
            field.Text = current;
            SetHotkeyHint(Tr.Text("Dibatalkan.", "Cancelled."), Muted);
            return;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            SetHotkeyHint(Tr.Text("Pakai minimal satu tombol pengubah (Ctrl, Alt, Shift atau Win).", "Use at least one modifier (Ctrl, Alt, Shift or Win)."), Red);
            return;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key == Key.Return ? "Enter" : key.ToString());

        try
        {
            field.Text = GlobalHotKey.NormalizeGesture(string.Join('+', parts));
            SetHotkeyHint(Tr.Text("Klik Terapkan untuk memakai kombinasi ini.", "Click Apply to use this combination."), Muted);
        }
        catch (FormatException exception)
        {
            SetHotkeyHint(exception.Message, Red);
        }
    }

    private void ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = _host.ApplyHotkey(HotkeyField.Text);
        HotkeyField.Text = _host.Settings.Hotkey;
        SetHotkeyHint(message, ok ? Green : Red);
    }

    private void ApplyQuickHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = _host.ApplyQuickCaptureHotkey(QuickHotkeyField.Text);
        QuickHotkeyField.Text = _host.Settings.QuickCaptureHotkey;
        SetHotkeyHint(message, ok ? Green : Red);
    }

    private void ApplyShotHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = _host.ApplyScreenshotHotkey(ShotHotkeyField.Text);
        ShotHotkeyField.Text = _host.Settings.ScreenshotHotkey;
        SetHotkeyHint(message, ok ? Green : Red);
    }

    private void ClaudeLive_Click(object sender, RoutedEventArgs e)
    {
        var on = ClaudeLiveSwitch.IsChecked == true;
        var (ok, message) = _host.SetClaudeLiveSessions(on);
        if (!ok && on)
        {
            ClaudeLiveSwitch.IsChecked = ClaudeHooksInstaller.IsInstalled();
        }

        ClaudeLiveStatus.Text = message;
    }

    private void ApplyVoiceHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = _host.ApplyVoiceHotkey(VoiceHotkeyField.Text);
        VoiceHotkeyField.Text = _host.Settings.VoiceHotkey;
        SetHotkeyHint(message, ok ? Green : Red);
    }

    private void ApplyChatHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = _host.ApplyChatHotkey(ChatHotkeyField.Text);
        ChatHotkeyField.Text = _host.Settings.ChatHotkey;
        SetHotkeyHint(message, ok ? Green : Red);
    }

    private CancellationTokenSource? _voiceInstall;

    private void RenderVoiceStatus()
    {
        if (_voiceInstall is not null)
        {
            return;
        }

        var model = _host.Settings.VoiceModel;
        var installed = CodexBar.App.Voice.WhisperEngine.IsInstalled(model);
        VoiceStatusText.Text = installed
            ? Tr.Text("Siap · offline", "Ready · offline")
            : string.Format(Tr.Text("Belum diunduh · ±{0} MB", "Not downloaded · about {0} MB"), CodexBar.App.Voice.WhisperEngine.DownloadSizeMb(model));
        VoiceInstallButton.Content = installed ? Tr.Text("Hapus", "Remove") : Tr.Text("Unduh", "Download");
    }

    private async void VoiceInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceInstall is not null)
        {
            _voiceInstall.Cancel();
            return;
        }

        if (CodexBar.App.Voice.WhisperEngine.IsInstalled(_host.Settings.VoiceModel))
        {
            _host.UninstallVoice();
            RenderVoiceStatus();
            return;
        }

        _voiceInstall = new CancellationTokenSource();
        VoiceInstallButton.Content = Tr.Text("Batal", "Cancel");
        var progress = new Progress<double>(value =>
            VoiceStatusText.Text = string.Format(Tr.Text("Mengunduh… {0}%", "Downloading… {0}%"), Math.Round(value * 100)));
        try
        {
            await _host.InstallVoiceAsync(progress, _voiceInstall.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            VoiceStatusText.Text = exception.Message;
            _voiceInstall.Dispose();
            _voiceInstall = null;
            VoiceInstallButton.Content = Tr.Text("Unduh", "Download");
            return;
        }

        _voiceInstall.Dispose();
        _voiceInstall = null;
        RenderVoiceStatus();
    }

    private void SetHotkeyHint(string text, Brush brush)
    {
        HotkeyHint.Text = text;
        HotkeyHint.Foreground = brush;
    }

    // ---- Navigation & chrome -------------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string page } button || PageGeneral is null)
        {
            return;
        }

        var pages = new Dictionary<string, FrameworkElement>
        {
            ["general"] = PageGeneral,
            ["appearance"] = PageAppearance,
            ["provider"] = PageProvider,
            ["model"] = PageModel,
            ["workspace"] = PageWorkspace,
            ["shortcuts"] = PageShortcuts,
            ["advanced"] = PageAdvanced
        };
        foreach (var (key, element) in pages)
        {
            element.Visibility = key == page ? Visibility.Visible : Visibility.Collapsed;
        }

        PageTitle.Text = ((button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text) ?? string.Empty;
    }

    // Search jumps to the first page whose rows mention the query.
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var query = SearchBox.Text.Trim();
        if (query.Length < 2)
        {
            return;
        }

        var navs = new[] { NavGeneral, NavAppearance, NavProvider, NavModel, NavWorkspace, NavShortcuts, NavAdvanced };
        var pages = new FrameworkElement[] { PageGeneral, PageAppearance, PageProvider, PageModel, PageWorkspace, PageShortcuts, PageAdvanced };
        for (var i = 0; i < pages.Length; i++)
        {
            if (Texts(pages[i]).Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                navs[i].IsChecked = true;
                return;
            }
        }
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock { Text: { Length: > 0 } text })
        {
            yield return text;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { IsReadOnly: true })
        {
            Close();
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void Select(ComboBox combo, string? tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string ProviderName(string id) => id switch
    {
        "claude-code" => Tr.Text("Claude (Langganan)", "Claude (Subscription)"),
        "anthropic" => "Claude API",
        "openai" => "OpenAI",
        "openrouter" => "OpenRouter",
        _ => "Codex"
    };

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
