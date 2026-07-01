using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Windows;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using DirectoryInfo = System.IO.DirectoryInfo;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace CodexBar.App;

public partial class MainWindow : Window
{
    private static readonly Brush StatusReadyBrush = new SolidColorBrush(Color.FromRgb(0x5E, 0xE6, 0xA8));
    private static readonly Brush StatusBusyBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xB4, 0x5E));
    private static readonly Brush StatusErrorBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0x6A, 0x6A));
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    // Window memakai SizeToContent=Height, jadi tinggi selalu pas isi. Bottom di-anchor di atas taskbar.
    private bool _bottomAnchored;
    private string _workspaceLabel = "workspace";

    // Lebar path direktori menyusut saat input dipakai, melebar saat direktori di-hover.
    private const double DirWidthIdle = 150;
    private const double DirWidthExpanded = 280;
    private const double DirWidthCollapsed = 0;
    private bool _promptFocused;
    private bool _fieldHover;
    private bool _dirHover;

    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _settingsStore;
    private readonly ConversationController _controller;
    private OutputWindow? _outputWindow;
    private readonly GlobalHotKey _hotKey = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _renderTimer;
    private readonly SemaphoreSlim _approvalGate = new(1, 1);
    private readonly List<ChatMessage> _messages = new();
    private readonly List<ProviderInputAttachment> _attachments = new();
    private readonly HashSet<string> _temporaryAttachmentPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderThreadStatusEvent> _threadStatuses = new(StringComparer.Ordinal);
    private ChatMessage? _streamingAgent;
    private string? _historyLoadedThreadId;
    private bool _conversationCollapsed;
    private SidebarWindow? _sidebarWindow;
    private Forms.ToolStripMenuItem? _attentionMenuItem;

    private bool _settingsReady;
    private bool _modelsReady;
    private bool _sourceReady;
    private bool _renderPending;
    private bool _autoFollow = true;
    private bool _isExiting;
    private bool _approvalVisible;
    private bool _dialogVisible;
    private bool _suppressSizeSave;
    private bool _permissionChangePending;
    private bool _contextWarningShown;
    private int? _lastContextPercent;
    private double _responseZoomDelta;
    private OnboardingKind _onboardingKind;

    private Brush _primaryText = new SolidColorBrush(Color.FromRgb(236, 238, 244));
    private Brush _mutedText = new SolidColorBrush(Color.FromRgb(170, 178, 194));
    private Brush _inlineCode = new SolidColorBrush(Color.FromRgb(158, 203, 255));
    private Brush _codeBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x10, 0x14, 0x1C));
    private Brush _codeBorder = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private Brush _quoteBar = new SolidColorBrush(Color.FromRgb(62, 123, 250));

    public MainWindow(
        AppSettings settings,
        JsonSettingsStore settingsStore,
        IAgentProvider provider)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _controller = new ConversationController(provider);
        _controller.EventReceived += Controller_EventReceived;

        InitializeComponent();
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        InlineConversation.ScrollChanged += ResponseScroll_ScrollChanged;

        ConfigureFromSettings();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _renderTimer.Tick += (_, _) =>
        {
            if (!_renderPending)
            {
                return;
            }

            _renderPending = false;
            if (_streamingAgent is not null)
            {
                ActiveConversation.UpdateLast(_streamingAgent);
                if (_autoFollow)
                {
                    ScrollActiveToEnd();
                }
            }
        };
        _renderTimer.Start();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "CodexBar",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleOverlay);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowFromExternalActivation);
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/codexbar.ico"));
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            // Jatuh ke ikon default bila resource tidak tersedia.
        }

        return Drawing.SystemIcons.Application;
    }

    private ConversationView ActiveConversation =>
        _settings.OutputDocked ? InlineConversation : GetOutputWindow().Conversation;

    private OutputWindow GetOutputWindow()
    {
        if (_outputWindow is not null)
        {
            return _outputWindow;
        }

        var window = new OutputWindow
        {
            Width = _settings.Window.Width,
            Height = Math.Clamp(_settings.Window.MaxExpandedHeight, 180, 900),
            Topmost = _settings.AlwaysOnTop
        };
        window.OutputScrollChanged += ResponseScroll_ScrollChanged;
        window.JumpLatestRequested += (_, _) => JumpToLatest();
        window.CopyRequested += (_, _) => CopyResponse();
        window.OpenInCodexRequested += (_, _) => OpenCurrentThread();
        window.SetContext(_workspaceLabel, StatusText.Text);
        window.ApplyBackdrop(!IsLightTheme());
        _outputWindow = window;
        return window;
    }

    private double EffectiveResponseFontSize =>
        Math.Clamp(_settings.Appearance.ResponseFontSize + _responseZoomDelta, 10, 24);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowPositioner.PlaceAboveTaskbar(this, _settings.Window.Position);
            _bottomAnchored = true;
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AboutText.Text =
                $"CodexBar v{version?.ToString(3) ?? "1.0.0"} · aplikasi komunitas tidak resmi. " +
                "Codex CLI memproses workspace lokal dan mengirim konteks yang diperlukan ke layanan OpenAI " +
                "sesuai metode login serta kebijakan akun kamu.";
            PromptBox.Focus();
            await StartupAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task StartupAsync()
    {
        HideOnboarding();

        try
        {
            SetStatus("Menghubungkan Codex…", StatusBusyBrush);
            await _controller.InitializeAsync();
        }
        catch (System.IO.FileNotFoundException)
        {
            ShowOnboarding(OnboardingKind.InstallNeeded);
            return;
        }
        catch (Exception exception)
        {
            ShowOnboarding(OnboardingKind.Error, exception.Message);
            return;
        }

        try
        {
            var account = await _controller.GetAccountAsync();
            if (!account.IsAuthenticated)
            {
                ShowOnboarding(OnboardingKind.LoginNeeded);
                return;
            }

            if (!account.AuthenticationMethod.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) &&
                !_settings.ApiKeyWarningAcknowledged)
            {
                var choice = System.Windows.MessageBox.Show(
                    this,
                    "Codex CLI sedang login menggunakan API key. Penggunaan akan ditagihkan ke akun OpenAI Platform dan tidak memakai kuota paket ChatGPT. Lanjutkan?",
                    "Konfirmasi metode login",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (choice != MessageBoxResult.Yes)
                {
                    ShowOnboarding(
                        OnboardingKind.LoginNeeded,
                        "Login CLI saat ini memakai API key. Jalankan codex logout lalu codex login untuk menggunakan akun ChatGPT.");
                    return;
                }

                _settings.ApiKeyWarningAcknowledged = true;
                await SaveSettingsSafeAsync();
            }
        }
        catch (Exception exception)
        {
            ShowOnboarding(OnboardingKind.Error, exception.Message);
            return;
        }

        try
        {
            HideOnboarding();
            await LoadModelsAsync();
            if (string.IsNullOrWhiteSpace(_settings.WorkspacePath) ||
                !Directory.Exists(_settings.WorkspacePath))
            {
                ShowOnboarding(OnboardingKind.WorkspaceNeeded);
                return;
            }

            await OpenWorkspaceAsync(_settings.WorkspacePath);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private enum OnboardingKind
    {
        InstallNeeded,
        LoginNeeded,
        WorkspaceNeeded,
        Error
    }

    private string _onboardingCommand = string.Empty;

    private void ShowOnboarding(OnboardingKind kind, string? detail = null)
    {
        _onboardingKind = kind;
        InlineResponseArea.Visibility = Visibility.Collapsed;
        OnboardingRetryButton.Content = "Coba lagi";
        OnboardingDocsButton.Visibility = Visibility.Visible;
        OnboardingActionButton.Visibility = Visibility.Collapsed;

        switch (kind)
        {
            case OnboardingKind.InstallNeeded:
                OnboardingTitle.Text = "Codex CLI belum terpasang";
                OnboardingBody.Text =
                    "CodexBar memakai Codex CLI yang sudah login. Pasang lewat PowerShell/Terminal, " +
                    "lalu jalankan \"codex login\", kemudian klik Coba lagi.";
                _onboardingCommand = "npm install -g @openai/codex";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = "Pasang otomatis";
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus("Codex belum terpasang", StatusErrorBrush);
                break;

            case OnboardingKind.LoginNeeded:
                OnboardingTitle.Text = "Belum login ke Codex";
                OnboardingBody.Text = detail ??
                    "Codex sudah terpasang tetapi belum login. Jalankan perintah ini, selesaikan login " +
                    "di browser, lalu klik Coba lagi.";
                _onboardingCommand = "codex login";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = "Buka login";
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus("Belum login", StatusBusyBrush);
                break;

            case OnboardingKind.WorkspaceNeeded:
                OnboardingTitle.Text = "Pilih workspace";
                OnboardingBody.Text =
                    "Pilih folder proyek yang boleh dibaca dan diubah Codex. CodexBar tidak memakai folder instalasi atau System32 sebagai workspace otomatis.";
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                OnboardingRetryButton.Content = "Pilih folder";
                OnboardingDocsButton.Visibility = Visibility.Collapsed;
                SetStatus("Workspace belum dipilih", StatusBusyBrush);
                break;

            default:
                OnboardingTitle.Text = "Gagal menghubungkan Codex";
                OnboardingBody.Text = detail ?? "Terjadi kesalahan saat menghubungkan Codex.";
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                SetStatus("Error", StatusErrorBrush);
                break;
        }

        OnboardingCommand.Text = _onboardingCommand;
        OnboardingPanel.Visibility = Visibility.Visible;
    }

    private void HideOnboarding() => OnboardingPanel.Visibility = Visibility.Collapsed;

    private void OnboardingCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingCommand.Length > 0)
        {
            try
            {
                System.Windows.Clipboard.SetText(_onboardingCommand);
                OnboardingCopyButton.Content = "Tersalin";
            }
            catch (Exception exception)
            {
                ShowError($"Clipboard tidak tersedia: {exception.Message}");
            }
        }
    }

    private async void OnboardingRetryButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingRetryButton.IsEnabled = false;
        try
        {
            if (_onboardingKind == OnboardingKind.WorkspaceNeeded)
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Pilih workspace Codex",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Multiselect = false
                };
                try
                {
                    _dialogVisible = true;
                    if (dialog.ShowDialog(this) == true)
                    {
                        await OpenWorkspaceSafeAsync(dialog.FolderName);
                        HideOnboarding();
                    }
                }
                finally
                {
                    _dialogVisible = false;
                }
            }
            else
            {
                await StartupAsync();
            }
        }
        catch (Exception exception)
        {
            _dialogVisible = false;
            ShowError(exception.Message);
        }
        finally
        {
            OnboardingRetryButton.IsEnabled = true;
            OnboardingCopyButton.Content = "Salin";
        }
    }

    private void OnboardingDocsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/openai/codex#installation")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnboardingActionButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingActionButton.IsEnabled = false;
        OnboardingRetryButton.IsEnabled = false;
        try
        {
            if (_onboardingKind == OnboardingKind.LoginNeeded)
            {
                LaunchCodexLogin();
                OnboardingBody.Text = "Selesaikan login pada jendela yang terbuka, lalu klik Coba lagi.";
                return;
            }

            if (_onboardingKind != OnboardingKind.InstallNeeded)
            {
                return;
            }

            OnboardingBody.Text = "Menyiapkan Codex CLI… Jendela ini boleh tetap terbuka.";
            SetStatus("Memasang Codex CLI…", StatusBusyBrush);
            var npm = FindCommand("npm.cmd") ?? FindCommand("npm.exe");
            if (npm is null)
            {
                var winget = FindCommand("winget.exe")
                    ?? throw new InvalidOperationException(
                        "Node.js belum terpasang dan Windows Package Manager tidak ditemukan. Buka panduan untuk instalasi manual.");
                await RunSetupProcessAsync(winget,
                    "install", "--id", "OpenJS.NodeJS.LTS", "--exact",
                    "--accept-package-agreements", "--accept-source-agreements", "--silent");
                npm = FindCommand("npm.cmd")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
                if (!File.Exists(npm))
                {
                    throw new FileNotFoundException("Node.js selesai dipasang tetapi npm belum dapat ditemukan. Tutup dan buka CodexBar, lalu coba lagi.");
                }
            }

            await RunSetupProcessAsync(npm, "install", "-g", "@openai/codex");
            ShowOnboarding(OnboardingKind.LoginNeeded,
                "Codex CLI sudah terpasang. Klik Buka login untuk menghubungkan akun ChatGPT/Codex.");
        }
        catch (Exception exception)
        {
            ShowOnboarding(OnboardingKind.Error, $"Instalasi otomatis gagal: {exception.Message}");
        }
        finally
        {
            OnboardingActionButton.IsEnabled = true;
            OnboardingRetryButton.IsEnabled = true;
        }
    }

    private static string? FindCommand(string command)
    {
        var appDataCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", command);
        if (File.Exists(appDataCandidate))
        {
            return appDataCandidate;
        }

        var programFilesCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", command);
        if (File.Exists(programFilesCandidate))
        {
            return programFilesCandidate;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), command))
            .FirstOrDefault(File.Exists);
    }

    private static async Task RunSetupProcessAsync(string executable, params string[] arguments)
    {
        var isCommandScript = Path.GetExtension(executable) is ".cmd" or ".bat";
        var startInfo = new ProcessStartInfo(
            isCommandScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isCommandScript)
        {
            var commandLine = string.Join(' ', new[] { executable }.Concat(arguments)
                .Select(value => $"\"{value.Replace("\"", "\"\"")}\""));
            startInfo.Arguments = $"/d /s /c \"{commandLine}\"";
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Tidak dapat menjalankan {Path.GetFileName(executable)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            detail = detail.Trim();
            if (detail.Length > 500)
            {
                detail = detail[^500..];
            }
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} berhenti dengan kode {process.ExitCode}. {detail}");
        }
    }

    private static void LaunchCodexLogin()
    {
        var npmBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var launcher = new[]
        {
            Path.Combine(npmBin, "codex.cmd"),
            Path.Combine(npmBin, "codex.exe")
        }.FirstOrDefault(File.Exists) ?? "codex";
        Process.Start(new ProcessStartInfo(launcher, "login") { UseShellExecute = true });
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _sourceReady = true;
        ApplyBackdrop();

        try
        {
            _hotKey.Pressed += GlobalHotKey_Pressed;
            _hotKey.Register(this, _settings.Hotkey);
            _settings.Hotkey = _hotKey.CurrentGesture ?? _settings.Hotkey;
            HotkeyTextBox.Text = _settings.Hotkey;
            HotkeyStatusText.Text = $"Aktif: {_settings.Hotkey}";
            HotkeyStatusText.Foreground = StatusReadyBrush;
        }
        catch (Exception exception)
        {
            HotkeyStatusText.Text = exception.Message;
            HotkeyStatusText.Foreground = StatusErrorBrush;
            SetStatus(exception.Message, StatusErrorBrush);
            _trayIcon.ShowBalloonTip(
                5000,
                "Hotkey CodexBar tidak aktif",
                exception.Message,
                Forms.ToolTipIcon.Warning);
        }
    }

    private void GlobalHotKey_Pressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(ToggleOverlay);

    private void ApplyBackdrop()
    {
        if (!_sourceReady)
        {
            return;
        }

        var dark = !IsLightTheme();
        var background = dark
            ? Color.FromRgb(23, 25, 29)
            : Color.FromRgb(247, 248, 250);
        WindowBackdrop.ApplySolid(this, dark, background);
        _outputWindow?.ApplyBackdrop(dark);
        _sidebarWindow?.ApplyTheme(dark);
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await _controller.ListModelsAsync();
            if (models.Count == 0)
            {
                return;
            }

            ModelCombo.ItemsSource = models;
            ModelCombo.SelectedItem = models.FirstOrDefault(model =>
                    string.Equals(model.Model, _settings.SelectedModel, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.IsDefault)
                ?? models[0];
            ModelRow.Visibility = Visibility.Visible;
            _modelsReady = true;
            UpdateModelToolTip();
        }
        catch (Exception exception)
        {
            ModelRow.Visibility = Visibility.Collapsed;
            ModelCombo.ToolTip = $"Daftar model tidak tersedia: {exception.Message}";
        }
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modelsReady || ModelCombo.SelectedItem is not ProviderModelInfo model)
        {
            return;
        }

        _settings.SelectedModel = model.Model;
        UpdateModelToolTip();
        _ = SaveSettingsSafeAsync();
    }

    private void UpdateModelToolTip()
    {
        if (ModelCombo.SelectedItem is not ProviderModelInfo model)
        {
            return;
        }

        var effort = string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
            ? string.Empty
            : $" · reasoning {model.DefaultReasoningEffort}";
        ModelCombo.ToolTip = $"{model.Description}{effort}";
    }

    private async Task OpenWorkspaceAsync(
        string workspacePath,
        bool newThread = false,
        string? requestedThreadId = null)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"Workspace tidak ditemukan: {normalizedPath}");
        }

        var threadKey = _settings.ThreadKey(normalizedPath);
        var previousThread = newThread
            ? null
            : requestedThreadId ?? (_settings.ThreadIds.TryGetValue(threadKey, out var stored) ? stored : null);

        await _controller.OpenWorkspaceAsync(normalizedPath, previousThread, _settings.SandboxMode, _settings.ApprovalPolicy);
        _settings.WorkspacePath = normalizedPath;
        _workspaceLabel = new DirectoryInfo(normalizedPath).Name;
        WorkspacePathText.Text = FormatWorkspaceDisplay(normalizedPath);
        WorkspaceButton.ToolTip = normalizedPath;
        SetStatus("Siap", StatusReadyBrush);
        _outputWindow?.SetContext(_workspaceLabel, "Siap");

        if (_controller.ThreadId is { } threadId)
        {
            _settings.ThreadIds[threadKey] = threadId;
        }

        await SaveSettingsSafeAsync();

        // History dibaca hanya saat pengguna membuka percakapan. Launcher tetap cepat dan ringan.
        _messages.Clear();
        _streamingAgent = null;
        _historyLoadedThreadId = newThread ? _controller.ThreadId : null;
        InlineConversation.ClearVisuals();
        _outputWindow?.Conversation.ClearVisuals();
        UpdateLayout(force: false);
    }

    private async Task LoadThreadMessagesAsync(string threadId)
    {
        var messages = await _controller.ReadThreadAsync(threadId);
        _messages.Clear();
        _streamingAgent = null;
        foreach (var message in messages.TakeLast(200))
        {
            _messages.Add(new ChatMessage
            {
                IsUser = message.Role == "user",
                Markdown = message.Text.Trim()
            });
        }

        _autoFollow = true;
        _historyLoadedThreadId = threadId;
        RenderConversation();
    }

    private async Task EnsureHistoryLoadedAsync()
    {
        if (_controller.ThreadId is not { } threadId ||
            string.Equals(_historyLoadedThreadId, threadId, StringComparison.Ordinal))
        {
            return;
        }

        SetStatus("Memuat percakapan…", StatusBusyBrush);
        await LoadThreadMessagesAsync(threadId);
        SetStatus("Siap", StatusReadyBrush);
    }

    private static string FormatWorkspaceDisplay(string fullPath)
    {
        var parts = fullPath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // Tampilkan path ringkas namun jelas: <root>\…\<induk>\<folder>.
        if (parts.Length <= 3)
        {
            return string.Join(Path.DirectorySeparatorChar, parts);
        }

        return $"{parts[0]}{Path.DirectorySeparatorChar}…{Path.DirectorySeparatorChar}{parts[^2]}{Path.DirectorySeparatorChar}{parts[^1]}";
    }

    private void SetStatus(string message, Brush dot)
    {
        StatusText.Text = message;
        StatusDot.Fill = dot;
        StatusDot.ToolTip = message;
        if (_trayIcon is not null)
        {
            var trayText = $"CodexBar — {message}";
            _trayIcon.Text = trayText.Length <= 63 ? trayText : trayText[..60] + "…";
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Klik pada field input, output, atau tombol tidak boleh memicu drag.
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null ||
             FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null))
        {
            return;
        }

        DragMove();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async void PromptBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryAttachClipboardContent())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private async Task SendPromptAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if ((prompt.Length == 0 && _attachments.Count == 0) || _controller.IsBusy)
        {
            return;
        }

        var pendingAttachments = _attachments.ToArray();
        var attachmentSummary = pendingAttachments.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, pendingAttachments.Select(item => $"- 📎 **{item.DisplayName}**"));
        var userMarkdown = prompt.Length == 0
            ? attachmentSummary
            : attachmentSummary.Length == 0
                ? prompt
                : $"{prompt}{Environment.NewLine}{Environment.NewLine}{attachmentSummary}";
        var userMessage = new ChatMessage { IsUser = true, Markdown = userMarkdown };
        _historyLoadedThreadId = _controller.ThreadId;
        _messages.Add(userMessage);
        _streamingAgent = null;
        _autoFollow = true;
        _responseZoomDelta = 0;
        RenderConversation();
        SetBusyUi(true);
        _outputWindow?.SetContext(_workspaceLabel, "Mengirim…");
        ShowOutputSurface();
        SetStatus("Mengirim…", StatusBusyBrush);

        try
        {
            var selectedModel = (ModelCombo.SelectedItem as ProviderModelInfo)?.Model
                ?? _settings.SelectedModel;
            var effort = string.IsNullOrWhiteSpace(_settings.ReasoningEffort) ? null : _settings.ReasoningEffort;
            await _controller.SendAsync(prompt, pendingAttachments, selectedModel, effort);
            PromptBox.Clear();
            _attachments.Clear();
            CleanupTemporaryAttachments(pendingAttachments.Select(item => item.Path));
            RenderAttachments();
        }
        catch (Exception exception)
        {
            _messages.Remove(userMessage);
            RenderConversation();
            SetBusyUi(false);
            ShowError(exception.Message);
        }
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Tambahkan gambar atau file ke prompt",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Gambar dan file|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.txt;*.md;*.json;*.yaml;*.yml;*.xml;*.csv;*.pdf;*.docx;*.xlsx;*.cs;*.js;*.ts;*.py;*.html;*.css|Semua file|*.*"
        };

        try
        {
            _dialogVisible = true;
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            foreach (var fileName in dialog.FileNames)
            {
                if (_attachments.Count >= 10)
                {
                    ShowError("Maksimal 10 lampiran per prompt.");
                    break;
                }

                AddAttachment(fileName);
            }

            RenderAttachments();
        }
        catch (Exception exception)
        {
            ShowError($"Lampiran gagal ditambahkan: {exception.Message}");
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private bool TryAttachClipboardContent()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is null)
                {
                    return false;
                }

                var folder = Path.Combine(Path.GetTempPath(), "CodexBar", "clipboard");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                _temporaryAttachmentPaths.Add(path);
                AddAttachment(path, "Screenshot clipboard.png");
                RenderAttachments();
                SetStatus("Screenshot clipboard ditambahkan", StatusReadyBrush);
                return true;
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var added = false;
                foreach (var path in System.Windows.Clipboard.GetFileDropList().Cast<string>())
                {
                    added |= AddAttachment(path);
                }
                if (added)
                {
                    RenderAttachments();
                    SetStatus("File clipboard ditambahkan", StatusReadyBrush);
                }
                return added;
            }
        }
        catch (Exception exception)
        {
            ShowError($"Isi clipboard tidak dapat dilampirkan: {exception.Message}");
            return true;
        }

        return false;
    }

    private bool AddAttachment(string fileName, string? displayName = null)
    {
        if (_attachments.Count >= 10)
        {
            ShowError("Maksimal 10 lampiran per prompt.");
            return false;
        }

        var fullPath = Path.GetFullPath(fileName);
        if (!File.Exists(fullPath) ||
            _attachments.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _attachments.Add(new ProviderInputAttachment(
            fullPath,
            displayName ?? Path.GetFileName(fullPath),
            ImageExtensions.Contains(Path.GetExtension(fullPath))
                ? ProviderAttachmentKind.LocalImage
                : ProviderAttachmentKind.FileMention));
        return true;
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = !_controller.IsBusy && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_controller.IsBusy ||
            !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var added = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            if (AddAttachment(path))
            {
                added++;
            }
        }

        if (added > 0)
        {
            RenderAttachments();
            SetStatus($"{added} file ditambahkan", StatusReadyBrush);
            PromptBox.Focus();
        }
    }

    private void CleanupTemporaryAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(_temporaryAttachmentPaths.Remove))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort; file sementara dicoba lagi saat aplikasi ditutup.
            }
        }
    }

    private void RenderAttachments()
    {
        AttachmentPanel.Children.Clear();
        foreach (var attachment in _attachments)
        {
            var button = new Button
            {
                Content = $"×  {attachment.DisplayName}",
                Tag = attachment,
                ToolTip = attachment.Path,
                Height = 26,
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(2),
                IsEnabled = !_controller.IsBusy
            };
            button.SetResourceReference(StyleProperty, "GlassPillButton");
            button.Click += RemoveAttachmentButton_Click;
            AttachmentPanel.Children.Add(button);
        }

        AttachmentBar.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSendButtonState();
    }

    private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderInputAttachment attachment })
        {
            _attachments.Remove(attachment);
            CleanupTemporaryAttachments([attachment.Path]);
            RenderAttachments();
        }
    }

    private void Controller_EventReceived(object? sender, ProviderEvent providerEvent)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            switch (providerEvent)
            {
                case ProviderTextDeltaEvent textDelta:
                    if (_streamingAgent is null)
                    {
                        _streamingAgent = new ChatMessage { IsUser = false, Markdown = textDelta.Delta };
                        _messages.Add(_streamingAgent);
                        if (HasVisibleConversationSurface)
                        {
                            RenderConversation();
                        }
                    }
                    else
                    {
                        _streamingAgent.Markdown += textDelta.Delta;
                        _renderPending = true;
                    }
                    if (!_autoFollow)
                    {
                        SetJumpLatestVisible(true);
                    }
                    break;

                case ProviderReasoningDeltaEvent reasoningDelta:
                    if (_streamingAgent is null)
                    {
                        _streamingAgent = new ChatMessage { IsUser = false, Reasoning = reasoningDelta.Delta };
                        _messages.Add(_streamingAgent);
                        if (HasVisibleConversationSurface)
                        {
                            RenderConversation();
                        }
                    }
                    else
                    {
                        _streamingAgent.Reasoning += reasoningDelta.Delta;
                        _renderPending = true;
                    }
                    if (!_autoFollow)
                    {
                        SetJumpLatestVisible(true);
                    }
                    break;

                case ProviderStatusEvent status:
                    SetStatus(status.Message, StatusBusyBrush);
                    _outputWindow?.SetContext(_workspaceLabel, status.Message);
                    break;

                case ProviderTokenUsageEvent usage:
                    TokenUsageText.Text = FormatTokenUsage(usage);
                    UpdateContextUsage(usage);
                    break;

                case ProviderThreadStatusEvent threadStatus:
                    UpdateThreadAttention(threadStatus);
                    break;

                case ProviderCompletedEvent completed:
                    var completedNormally = completed.Status == "completed";
                    SetStatus(
                        completedNormally ? "Selesai" : completed.Status == "interrupted" ? "Dibatalkan" : completed.Status,
                        completedNormally || completed.Status == "interrupted" ? StatusReadyBrush : StatusErrorBrush);
                    _outputWindow?.SetContext(_workspaceLabel, StatusText.Text);
                    SetBusyUi(false);
                    if (_streamingAgent is not null)
                    {
                        ActiveConversation.UpdateLast(_streamingAgent);
                        _streamingAgent = null;
                    }
                    if (!IsVisible || !IsActive)
                    {
                        _trayIcon.ShowBalloonTip(2500, "CodexBar", "Respons Codex sudah selesai.", Forms.ToolTipIcon.Info);
                    }
                    break;

                case ProviderErrorEvent error:
                    SetStatus(error.WillRetry ? "Mencoba kembali…" : "Error",
                        error.WillRetry ? StatusBusyBrush : StatusErrorBrush);
                    if (error.WillRetry)
                    {
                        _outputWindow?.SetContext(_workspaceLabel, "Mencoba kembali…");
                        break;
                    }

                    SetBusyUi(false);
                    ShowError(error.Message);
                    break;

                case ProviderApprovalEvent approval:
                    await ShowApprovalAsync(approval);
                    break;

                case ProviderUserInputEvent input:
                    await ShowUserInputAsync(input);
                    break;
            }
        });
    }

    private async Task ShowUserInputAsync(ProviderUserInputEvent input)
    {
        await _approvalGate.WaitAsync();
        try
        {
            var requestThreadId = input.RawParams.TryGetProperty("threadId", out var threadIdElement)
                ? threadIdElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(requestThreadId) &&
                !string.Equals(requestThreadId, _controller.ThreadId, StringComparison.Ordinal))
            {
                await input.RespondAsync(null);
                SetStatus("Pertanyaan thread lama dibatalkan", StatusErrorBrush);
                return;
            }

            if (input.IsPending?.Invoke() == false)
            {
                return;
            }

            _approvalVisible = true;
            Show();
            Activate();

            var dialog = new InteractiveRequestDialog(input) { Owner = this };
            var accepted = dialog.ShowDialog() == true;
            if (input.IsPending?.Invoke() != false)
            {
                await input.RespondAsync(accepted ? dialog.Answers : null);
            }
        }
        catch (Exception exception)
        {
            ShowError($"Jawaban gagal diproses: {exception.Message}");
        }
        finally
        {
            _approvalVisible = false;
            ApplyOpacity(isActive: IsActive);
            _approvalGate.Release();
        }
    }

    private async Task ShowApprovalAsync(ProviderApprovalEvent approval)
    {
        await _approvalGate.WaitAsync();
        try
        {
            var requestThreadId = approval.RawParams.TryGetProperty("threadId", out var threadIdElement)
                ? threadIdElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(requestThreadId) &&
                !string.Equals(requestThreadId, _controller.ThreadId, StringComparison.Ordinal))
            {
                await approval.RespondAsync(ProviderApprovalDecision.Decline);
                SetStatus("Approval thread lama ditolak", StatusErrorBrush);
                return;
            }

            if (approval.IsPending?.Invoke() == false)
            {
                return;
            }

            _approvalVisible = true;
            if (!IsVisible || !IsActive)
            {
                _trayIcon.ShowBalloonTip(4000, "Codex membutuhkan izin",
                    "Buka CodexBar untuk meninjau permintaan agent.", Forms.ToolTipIcon.Warning);
            }
            Show();
            Activate();

            var dialog = new ApprovalRequestDialog(approval) { Owner = this };
            dialog.ShowDialog();

            if (approval.IsPending?.Invoke() != false)
            {
                await approval.RespondAsync(dialog.Decision);
            }
        }
        catch (Exception exception)
        {
            ShowError($"Approval gagal diproses: {exception.Message}");
        }
        finally
        {
            _approvalVisible = false;
            ApplyOpacity(isActive: IsActive);
            _approvalGate.Release();
        }
    }

    private void SetBusyUi(bool busy)
    {
        SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendButtonState();
        AttachButton.IsEnabled = !busy;
        foreach (var button in AttachmentPanel.Children.OfType<Button>())
        {
            button.IsEnabled = !busy;
        }
        SandboxCombo.IsEnabled = !busy && !_permissionChangePending;
        WorkspaceButton.IsEnabled = !busy;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await StopCurrentTurnAsync();

    private async Task StopCurrentTurnAsync()
    {
        try
        {
            StopButton.IsEnabled = false;
            SetStatus("Membatalkan…", StatusBusyBrush);
            await _controller.CancelAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            StopButton.IsEnabled = true;
        }
    }

    private void RenderConversation()
    {
        if (!HasVisibleConversationSurface)
        {
            return;
        }

        ActiveConversation.Render(_messages, BuildChatTheme());
        UpdateLayout(force: false);

        if (_autoFollow)
        {
            Dispatcher.BeginInvoke(ScrollActiveToEnd, DispatcherPriority.Background);
            SetJumpLatestVisible(false);
        }
    }

    private bool HasVisibleConversationSurface => IsVisible || _outputWindow is { IsVisible: true };

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_messages.Count > 0 && !_conversationCollapsed)
            {
                RenderConversation();
            }
            return;
        }

        InlineConversation.ClearVisuals();
        if (_outputWindow is not { IsVisible: true })
        {
            _outputWindow?.Conversation.ClearVisuals();
        }
    }

    private ChatTheme BuildChatTheme()
    {
        var dark = !IsLightTheme();
        Brush userBubble = new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xF6));
        Brush agentBubble = new SolidColorBrush(
            dark ? Color.FromRgb(0x2A, 0x2F, 0x38) : Color.FromRgb(0xEC, 0xEF, 0xF4));
        return new ChatTheme(
            BuildMarkdownStyle(),
            UserBubble: userBubble,
            UserText: Brushes.White,
            AgentBubble: agentBubble,
            Placeholder: _mutedText,
            ReasoningText: _mutedText,
            ShowReasoning: _settings.ShowReasoning);
    }

    private MarkdownStyle BuildMarkdownStyle() => new(
        Primary: _primaryText,
        Muted: _mutedText,
        InlineCodeForeground: _inlineCode,
        CodeForeground: _primaryText,
        CodeBackground: _codeBackground,
        CodeBorder: _codeBorder,
        QuoteBar: _quoteBar,
        CodeFont: ResolveCodeFont(),
        FontSize: EffectiveResponseFontSize,
        CodeWrap: _settings.Appearance.CodeWordWrap);

    private void ConfigureFromSettings()
    {
        _settingsReady = false;
        _suppressSizeSave = true;

        Width = Math.Clamp(_settings.Window.Width, 500, 1200);
        var outputHeight = Math.Clamp(_settings.Window.MaxExpandedHeight, 180, 900);
        Topmost = _settings.AlwaysOnTop;
        TopmostToggle.IsChecked = _settings.AlwaysOnTop;
        WidthSlider.Value = Width;
        HeightSlider.Value = outputHeight;
        ActiveOpacitySlider.Value = Math.Clamp(_settings.Appearance.ActiveOpacity, 0.4, 1);
        InactiveOpacitySlider.Value = Math.Clamp(_settings.Appearance.InactiveOpacity, 0.25, 1);
        InputFontSlider.Value = Math.Clamp(_settings.Appearance.InputFontSize, 10, 24);
        ResponseFontSlider.Value = Math.Clamp(_settings.Appearance.ResponseFontSize, 10, 24);
        WordWrapCheckBox.IsChecked = _settings.Appearance.CodeWordWrap;
        PinCheckBox.IsChecked = _settings.Pinned;
        OutputDockedCheck.IsChecked = _settings.OutputDocked;
        ShowReasoningCheck.IsChecked = _settings.ShowReasoning;
        OutputPositionCombo.SelectedIndex = _settings.OutputPosition == "above" ? 1 : 0;
        ApprovalCombo.SelectedIndex = _settings.ApprovalPolicy switch
        {
            "untrusted" => 0,
            "on-failure" => 1,
            "never" => 3,
            _ => 2
        };
        EffortCombo.SelectedIndex = _settings.ReasoningEffort switch
        {
            "minimal" => 1,
            "low" => 2,
            "medium" => 3,
            "high" => 4,
            _ => 0
        };
        MenuDockedCheck.IsChecked = _settings.MenuDocked;
        ThemeCombo.SelectedIndex = _settings.Appearance.Theme.ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        HotkeyTextBox.Text = _settings.Hotkey;
        SelectSandboxMode(_settings.SandboxMode);
        SandboxStatusText.Text = SandboxDescription(_settings.SandboxMode);

        PromptBox.FontSize = _settings.Appearance.InputFontSize;
        ApplyOutputPosition();
        ApplyTheme();
        UpdateSettingLabels();
        ApplyOpacity(isActive: true);
        OutputButton.Visibility = _settings.OutputDocked ? Visibility.Collapsed : Visibility.Visible;

        _suppressSizeSave = false;
        _settingsReady = true;
        RenderConversation();
    }

    private bool IsLightTheme() => _settings.Appearance.Theme.ToLowerInvariant() switch
    {
        "light" => true,
        "dark" => false,
        _ => IsWindowsLightTheme()
    };

    private void ApplyTheme()
    {
        var dark = !IsLightTheme();
        if (!dark)
        {
            RootBorder.Background = new SolidColorBrush(Color.FromRgb(247, 248, 250));
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(210, 215, 223));
            InlineResponseArea.Background = Brushes.Transparent;
            PromptField.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            PromptField.BorderBrush = new SolidColorBrush(Color.FromRgb(202, 209, 219));
            SettingsPanel.Background = Brushes.Transparent;
            SettingsPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 230));
            _primaryText = new SolidColorBrush(Color.FromRgb(28, 32, 40));
            _mutedText = new SolidColorBrush(Color.FromRgb(82, 91, 108));
            _inlineCode = new SolidColorBrush(Color.FromRgb(10, 90, 170));
            _codeBackground = new SolidColorBrush(Color.FromArgb(0x55, 0xE5, 0xE9, 0xF0));
            _codeBorder = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            _quoteBar = new SolidColorBrush(Color.FromRgb(62, 123, 250));
            PromptBox.Foreground = _primaryText;
            PromptBox.CaretBrush = Brushes.DodgerBlue;
        }
        else
        {
            RootBorder.Background = new SolidColorBrush(Color.FromRgb(23, 25, 29));
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 63, 72));
            InlineResponseArea.Background = Brushes.Transparent;
            PromptField.Background = new SolidColorBrush(Color.FromRgb(35, 39, 47));
            PromptField.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 71, 82));
            SettingsPanel.Background = Brushes.Transparent;
            SettingsPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 63, 72));
            _primaryText = new SolidColorBrush(Color.FromRgb(236, 238, 244));
            _mutedText = new SolidColorBrush(Color.FromRgb(208, 214, 224));
            _inlineCode = new SolidColorBrush(Color.FromRgb(158, 203, 255));
            _codeBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x10, 0x14, 0x1C));
            _codeBorder = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            _quoteBar = new SolidColorBrush(Color.FromRgb(62, 123, 250));
            PromptBox.Foreground = _primaryText;
            PromptBox.CaretBrush = new SolidColorBrush(Color.FromRgb(121, 169, 255));
        }

        StatusText.Foreground = _mutedText;
        WorkspaceButton.Foreground = _primaryText;
        MenuButton.Foreground = _primaryText;
        OutputButton.Foreground = _primaryText;
        SettingsButton.Foreground = _primaryText;
        ModelCombo.Foreground = _primaryText;
        Resources["SettingsLabelBrush"] = _mutedText;
        Resources["SettingsValueBrush"] = _primaryText;
        UpdateSharedThemeBrush("TextPrimary", _primaryText);
        UpdateSharedThemeBrush("TextMuted", _mutedText);
        UpdateSharedThemeBrush("IconBrush", _mutedText);
        UpdateSharedThemeBrush("IconBrushHover", _primaryText);
        UpdateSharedThemeBrush("GlassTrack", new SolidColorBrush(
            dark ? Color.FromRgb(39, 43, 50) : Color.FromRgb(235, 238, 243)));
        UpdateSharedThemeBrush("GlassStroke", new SolidColorBrush(
            dark ? Color.FromRgb(68, 74, 85) : Color.FromRgb(205, 211, 220)));

        ApplyBackdrop();

        if (_settingsReady)
        {
            RenderConversation();
        }
    }

    private static void UpdateSharedThemeBrush(string key, Brush value)
    {
        if (System.Windows.Application.Current.TryFindResource(key) is SolidColorBrush existing &&
            value is SolidColorBrush replacement &&
            !existing.IsFrozen)
        {
            existing.Color = replacement.Color;
        }
    }

    private static bool IsWindowsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_settings.Appearance.Theme.Equals("system", StringComparison.OrdinalIgnoreCase) ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(ApplyTheme);
    }

    private System.Windows.Media.FontFamily ResolveCodeFont()
    {
        var requested = new System.Windows.Media.FontFamily(_settings.Appearance.CodeFontFamily);
        return requested.FamilyNames.Count > 0
            ? requested
            : new System.Windows.Media.FontFamily("Consolas");
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var outputDockedBefore = _settings.OutputDocked;

        _settings.Window.Width = WidthSlider.Value;
        _settings.Window.MaxExpandedHeight = HeightSlider.Value;
        _settings.Appearance.ActiveOpacity = ActiveOpacitySlider.Value;
        _settings.Appearance.InactiveOpacity = InactiveOpacitySlider.Value;
        _settings.Appearance.InputFontSize = InputFontSlider.Value;
        _settings.Appearance.ResponseFontSize = ResponseFontSlider.Value;
        _settings.Appearance.CodeWordWrap = WordWrapCheckBox.IsChecked == true;
        _settings.Pinned = PinCheckBox.IsChecked == true;
        _settings.OutputDocked = OutputDockedCheck.IsChecked == true;
        _settings.ShowReasoning = ShowReasoningCheck.IsChecked == true;
        _settings.OutputPosition = (OutputPositionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "above" ? "above" : "below";
        _settings.ApprovalPolicy = (ApprovalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "on-request";
        _settings.ReasoningEffort = (EffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _settings.MenuDocked = MenuDockedCheck.IsChecked == true;
        _settings.Appearance.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";

        _suppressSizeSave = true;
        Width = _settings.Window.Width;
        if (_outputWindow is not null)
        {
            _outputWindow.Width = _settings.Window.Width;
            _outputWindow.Height = _settings.Window.MaxExpandedHeight;
        }
        _suppressSizeSave = false;

        PromptBox.FontSize = _settings.Appearance.InputFontSize;
        OutputButton.Visibility = _settings.OutputDocked ? Visibility.Collapsed : Visibility.Visible;

        if (outputDockedBefore != _settings.OutputDocked)
        {
            // Pindahkan konten ke permukaan output yang aktif.
            if (_settings.OutputDocked)
            {
                _outputWindow?.Hide();
            }
            else if (_messages.Count > 0)
            {
                GetOutputWindow().ShowNear(this);
            }
        }

        ApplyOutputPosition();
        ApplyTheme();
        ApplyOpacity(isActive: IsActive);
        UpdateSettingLabels();
        RenderConversation();
        RepositionSidebarIfDocked();
        _ = SaveSettingsSafeAsync();
    }

    private void UpdateSettingLabels()
    {
        WidthValue.Text = $"{WidthSlider.Value:0}";
        HeightValue.Text = $"{HeightSlider.Value:0}";
        ActiveOpacityValue.Text = $"{ActiveOpacitySlider.Value:P0}";
        InactiveOpacityValue.Text = $"{InactiveOpacitySlider.Value:P0}";
        InputFontValue.Text = $"{InputFontSlider.Value:0}";
        ResponseFontValue.Text = $"{ResponseFontSlider.Value:0}";
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            HotkeyTextBox.Text = _hotKey.CurrentGesture ?? _settings.Hotkey;
            ApplyHotkeyButton.IsEnabled = false;
            HotkeyStatusText.Text = "Perubahan dibatalkan.";
            HotkeyStatusText.Foreground = _mutedText;
            return;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            HotkeyStatusText.Text = "Tahan modifier, lalu tekan satu tombol utama.";
            HotkeyStatusText.Foreground = _mutedText;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatusText.Text = "Gunakan minimal satu modifier: Win, Ctrl, Alt, atau Shift.";
            HotkeyStatusText.Foreground = StatusErrorBrush;
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
            HotkeyTextBox.Text = GlobalHotKey.NormalizeGesture(string.Join('+', parts));
            ApplyHotkeyButton.IsEnabled = !string.Equals(
                HotkeyTextBox.Text,
                _hotKey.CurrentGesture,
                StringComparison.Ordinal);
            HotkeyStatusText.Text = ApplyHotkeyButton.IsEnabled
                ? "Klik Terapkan untuk mengaktifkan kombinasi baru."
                : $"Aktif: {HotkeyTextBox.Text}";
            HotkeyStatusText.Foreground = _mutedText;
        }
        catch (FormatException exception)
        {
            HotkeyStatusText.Text = exception.Message;
            HotkeyStatusText.Foreground = StatusErrorBrush;
        }
    }

    private async void ApplyHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyHotkeyButton.IsEnabled = false;
        var previousGesture = _hotKey.CurrentGesture;
        var previousSetting = _settings.Hotkey;
        try
        {
            _hotKey.ChangeGesture(HotkeyTextBox.Text);
            _settings.Hotkey = _hotKey.CurrentGesture
                ?? throw new InvalidOperationException("Hotkey baru tidak aktif.");
            HotkeyTextBox.Text = _settings.Hotkey;
            HotkeyStatusText.Text = $"Aktif: {_settings.Hotkey}";
            HotkeyStatusText.Foreground = StatusReadyBrush;
            SetStatus("Hotkey diperbarui", StatusReadyBrush);
            try
            {
                await _settingsStore.SaveAsync(_settings);
            }
            catch (Exception saveException)
            {
                if (previousGesture is not null)
                {
                    _hotKey.ChangeGesture(previousGesture);
                    _settings.Hotkey = previousSetting;
                    HotkeyTextBox.Text = previousGesture;
                }

                throw new InvalidOperationException(
                    "Hotkey tidak dapat disimpan; kombinasi sebelumnya dipulihkan.",
                    saveException);
            }
        }
        catch (Exception exception)
        {
            HotkeyStatusText.Text = exception.Message;
            HotkeyStatusText.Foreground = StatusErrorBrush;
            ApplyHotkeyButton.IsEnabled = !string.Equals(
                HotkeyTextBox.Text,
                _hotKey.CurrentGesture,
                StringComparison.Ordinal);
        }
    }

    private async void SandboxCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || _permissionChangePending ||
            SandboxCombo.SelectedItem is not ComboBoxItem selected ||
            selected.Tag is not string requestedMode)
        {
            return;
        }

        var previousMode = _settings.SandboxMode;
        if (requestedMode == previousMode)
        {
            SandboxStatusText.Text = SandboxDescription(requestedMode);
            return;
        }

        if (requestedMode == "danger-full-access")
        {
            MessageBoxResult confirmation;
            try
            {
                _dialogVisible = true;
                confirmation = System.Windows.MessageBox.Show(
                    this,
                    "danger-full-access memungkinkan Codex membaca dan mengubah file di luar workspace serta menjalankan perintah tanpa batas sandbox. Approval tetap on-request, tetapi mode ini tetap berisiko. Aktifkan?",
                    "Konfirmasi danger-full-access",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
            }
            finally
            {
                _dialogVisible = false;
            }

            if (confirmation != MessageBoxResult.Yes)
            {
                _permissionChangePending = true;
                SelectSandboxMode(previousMode);
                _permissionChangePending = false;
                return;
            }
        }

        _permissionChangePending = true;
        SandboxCombo.IsEnabled = false;
        SandboxStatusText.Text = "Menerapkan permission…";
        var appliedToThread = false;
        try
        {
            await _controller.ChangePermissionsAsync(requestedMode, _settings.ApprovalPolicy);
            appliedToThread = _controller.ThreadId is not null;
            _settings.SandboxMode = requestedMode;
            await _settingsStore.SaveAsync(_settings);
            SandboxStatusText.Text = SandboxDescription(requestedMode);
            SandboxStatusText.Foreground = StatusReadyBrush;
            SetStatus("Permission diperbarui", StatusReadyBrush);
        }
        catch (Exception exception)
        {
            if (appliedToThread)
            {
                try
                {
                    await _controller.ChangePermissionsAsync(previousMode, _settings.ApprovalPolicy);
                }
                catch
                {
                }
            }

            _settings.SandboxMode = previousMode;
            SelectSandboxMode(previousMode);
            SandboxStatusText.Text = $"Permission gagal diubah: {exception.Message}";
            SandboxStatusText.Foreground = StatusErrorBrush;
        }
        finally
        {
            SandboxCombo.IsEnabled = !_controller.IsBusy;
            _permissionChangePending = false;
        }
    }

    private void SelectSandboxMode(string sandboxMode)
    {
        SandboxCombo.SelectedIndex = sandboxMode switch
        {
            "read-only" => 0,
            "danger-full-access" => 2,
            _ => 1
        };
    }

    private static string SandboxDescription(string sandboxMode) => sandboxMode switch
    {
        "read-only" => "read-only: Codex hanya membaca; perubahan/perintah yang butuh akses lebih akan meminta approval.",
        "danger-full-access" => "danger-full-access: sandbox file dinonaktifkan. Gunakan hanya pada workspace tepercaya.",
        _ => "workspace-write: Codex dapat mengubah file di workspace; akses lain tetap meminta approval."
    };

    /// <summary>Menukar urutan baris: output di bawah (default) atau di atas input bar.</summary>
    private void ApplyOutputPosition()
    {
        var above = _settings.OutputPosition == "above";
        Grid.SetRow(InputBarArea, above ? 1 : 0);
        Grid.SetRow(InlineResponseArea, above ? 0 : 1);
        Grid.SetRow(OnboardingPanel, above ? 0 : 1);
    }

    private async Task SaveSettingsSafeAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Settings gagal disimpan: {exception.Message}";
        }
    }

    private void ApplyOpacity(bool isActive)
    {
        // Card harus selalu opaque. Area di luar card ditangani oleh per-pixel
        // transparency window, bukan dengan menurunkan opacity card.
        Opacity = 1;
        RootBorder.Opacity = 1;
    }

    private void ShowOutputSurface()
    {
        _conversationCollapsed = false;
        if (_settings.OutputDocked)
        {
            InlineResponseArea.Visibility = Visibility.Visible;
            UpdateLayout(force: true);
        }
        else
        {
            GetOutputWindow().ShowNear(this);
        }
    }

    private void UpdateLayout(bool force)
    {
        // SizeToContent=Height membuat window menyesuaikan isi; di sini cukup atur
        // visibilitas & tinggi area output. Bottom tetap menempel taskbar via SizeChanged.
        var hasContent = _messages.Count > 0;
        var dockedOutput = _settings.OutputDocked && hasContent && !_conversationCollapsed;

        if (dockedOutput)
        {
            InlineResponseArea.Height = Math.Clamp(_settings.Window.MaxExpandedHeight, 160, 760);
            InlineResponseArea.Visibility = Visibility.Visible;
        }
        else
        {
            InlineResponseArea.Visibility = Visibility.Collapsed;
            InlineResponseArea.ClearValue(HeightProperty);
        }
    }

    private async void ConversationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureHistoryLoadedAsync();
        }
        catch (Exception exception)
        {
            ShowError($"Riwayat tidak dapat dimuat: {exception.Message}");
            return;
        }

        if (_settings.OutputDocked)
        {
            if (InlineResponseArea.Visibility != Visibility.Visible)
            {
                _conversationCollapsed = false;
                RenderConversation();
                UpdateLayout(force: true);
                return;
            }

            _conversationCollapsed = !_conversationCollapsed;
            if (!_conversationCollapsed)
            {
                RenderConversation();
            }
            UpdateLayout(force: true);
        }
        else if (_outputWindow is { IsVisible: true })
        {
            _outputWindow.Hide();
        }
        else
        {
            RenderConversation();
            var output = GetOutputWindow();
            output.ShowNear(this);
            output.Activate();
        }
    }

    private void ToggleOverlay()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        Show();
        WindowPositioner.PlaceAboveTaskbar(this, _settings.Window.Position);
        Activate();
        PromptBox.Focus();
    }

    public void ShowFromExternalActivation()
    {
        _trayIcon.Visible = false;
        _trayIcon.Visible = true;
        Show();
        WindowState = WindowState.Normal;
        WindowPositioner.PlaceAboveTaskbar(this, _settings.Window.Position);
        Activate();
        PromptBox.Focus();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _attentionMenuItem = new Forms.ToolStripMenuItem("Tidak ada agent aktif") { Enabled = false };
        _attentionMenuItem.Click += (_, _) => Dispatcher.Invoke(ShowAttentionCenter);
        menu.Items.Add(_attentionMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Tampilkan", null, (_, _) => Dispatcher.Invoke(ToggleOverlay));
        menu.Items.Add("Thread baru", null, (_, _) => Dispatcher.BeginInvoke(
            () => _ = RunUiTaskAsync(CreateThreadFromSidebarAsync)));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() =>
        {
            Show();
            Activate();
            SetSettingsVisible(true);
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Keluar", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _isExiting = true;
            _trayIcon.Visible = false;
            Close();
            System.Windows.Application.Current.Shutdown();
        }));
        return menu;
    }

    private void UpdateThreadAttention(ProviderThreadStatusEvent status)
    {
        var hadAttention = _threadStatuses.TryGetValue(status.ThreadId, out var previous) &&
            previous.ActiveFlags.Count > 0;
        if (status.Status is "idle" or "notLoaded")
        {
            _threadStatuses.Remove(status.ThreadId);
        }
        else
        {
            _threadStatuses[status.ThreadId] = status;
        }

        var active = _threadStatuses.Values.Count(value => value.Status == "active");
        var attention = _threadStatuses.Values.Count(value => value.ActiveFlags.Count > 0);
        var errors = _threadStatuses.Values.Count(value => value.Status == "systemError");
        if (_attentionMenuItem is not null)
        {
            _attentionMenuItem.Text = attention > 0
                ? $"{attention} perlu perhatian · {active} aktif"
                : errors > 0
                    ? $"{errors} thread error"
                    : active > 0
                        ? $"{active} agent sedang bekerja"
                        : "Tidak ada agent aktif";
            _attentionMenuItem.Enabled = active + attention + errors > 0;
        }

        if (!hadAttention && status.ActiveFlags.Count > 0 &&
            !string.Equals(status.ThreadId, _controller.ThreadId, StringComparison.Ordinal))
        {
            var reason = status.ActiveFlags.Contains("waitingOnApproval")
                ? "membutuhkan izin"
                : "membutuhkan jawaban";
            _trayIcon.ShowBalloonTip(4000, "Agent membutuhkan perhatian",
                $"Thread lain {reason}. Klik untuk membuka daftar.", Forms.ToolTipIcon.Warning);
        }
    }

    private void ShowAttentionCenter()
    {
        ShowFromExternalActivation();
        ShowSidebar();
    }

    private async Task RunUiTaskAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        AdjustZoom(e.Delta > 0 ? 1 : -1);
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HotkeyTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key is Key.Add or Key.OemPlus)
        {
            e.Handled = true;
            AdjustZoom(1);
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.Subtract or Key.OemMinus)
        {
            e.Handled = true;
            AdjustZoom(-1);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.D0)
        {
            e.Handled = true;
            _responseZoomDelta = 0;
            RenderConversation();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
        {
            e.Handled = true;
            ToggleTopmost();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W)
        {
            e.Handled = true;
            WordWrapCheckBox.IsChecked = !(WordWrapCheckBox.IsChecked == true);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            OpenCurrentThread();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            await RunUiTaskAsync(CreateThreadFromSidebarAsync);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.OemPeriod)
        {
            e.Handled = true;
            await StopCurrentTurnAsync();
        }
        else if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void AdjustZoom(double delta)
    {
        var target = Math.Clamp(EffectiveResponseFontSize + delta, 10, 24);
        _responseZoomDelta = target - _settings.Appearance.ResponseFontSize;
        RenderConversation();
    }

    private void ResponseScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scrollableHeight = e.ExtentHeight - e.ViewportHeight;
        var isAtBottom = scrollableHeight - e.VerticalOffset <= 2;
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0)
        {
            _autoFollow = isAtBottom;
        }

        if (isAtBottom)
        {
            _autoFollow = true;
            SetJumpLatestVisible(false);
        }
    }

    private void SetJumpLatestVisible(bool visible)
    {
        if (_settings.OutputDocked)
        {
            JumpLatestButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _outputWindow?.SetJumpLatestVisible(visible);
        }
    }

    private void ScrollActiveToEnd() => ActiveConversation.ScrollToEnd();

    private void JumpLatestButton_Click(object sender, RoutedEventArgs e) => JumpToLatest();

    private void JumpToLatest()
    {
        _autoFollow = true;
        ScrollActiveToEnd();
        SetJumpLatestVisible(false);
    }

    private void WorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih workspace Codex",
            InitialDirectory = Directory.Exists(_settings.WorkspacePath)
                ? _settings.WorkspacePath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };

        try
        {
            _dialogVisible = true;
            if (dialog.ShowDialog(this) == true)
            {
                _ = OpenWorkspaceSafeAsync(dialog.FolderName);
            }
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private async Task OpenWorkspaceSafeAsync(string workspacePath)
    {
        try
        {
            await OpenWorkspaceAsync(workspacePath);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarWindow is { IsVisible: true })
        {
            _sidebarWindow.Close();
            return;
        }

        ShowSidebar();
    }

    private void ShowSidebar()
    {
        if (_sidebarWindow is { IsVisible: true })
        {
            _sidebarWindow.Activate();
            return;
        }

        _sidebarWindow = new SidebarWindow(
            _controller,
            OpenThreadFromSidebarAsync,
            CreateThreadFromSidebarAsync,
            dark: !IsLightTheme());
        _sidebarWindow.Closed += (_, _) => _sidebarWindow = null;
        _sidebarWindow.Topmost = _settings.AlwaysOnTop;
        _sidebarWindow.ShowNextTo(this, _settings.MenuDocked);
    }

    private void RepositionSidebarIfDocked()
    {
        if (_settings.MenuDocked && _sidebarWindow is { IsVisible: true })
        {
            _sidebarWindow.SnapTo(this);
        }
    }

    private static string FormatTokenUsage(ProviderTokenUsageEvent usage)
    {
        var total = usage.TotalTokens.ToString("N0");
        var window = usage.ContextWindow is { } cw && cw > 0
            ? $" / {cw:N0}"
            : string.Empty;
        return $"Token: {total}{window} (turn {usage.LastTurnTokens:N0})";
    }

    private void UpdateContextUsage(ProviderTokenUsageEvent usage)
    {
        if (usage.ContextWindow is not { } contextWindow || contextWindow <= 0)
        {
            ContextUsageButton.Visibility = Visibility.Collapsed;
            return;
        }

        var ratio = Math.Clamp((double)usage.TotalTokens / contextWindow, 0, 1);
        var percent = (int)Math.Round(ratio * 100);
        _lastContextPercent = percent;
        UpdateContextBadgeLayout();
        ContextUsageButton.ToolTip =
            $"Context {usage.TotalTokens:N0} / {contextWindow:N0} token. Klik untuk memadatkan.";
        ContextUsageButton.Foreground = ratio switch
        {
            >= 0.9 => StatusErrorBrush,
            >= 0.75 => StatusBusyBrush,
            _ => _primaryText
        };
        ContextUsageButton.Visibility = Visibility.Visible;

        if (ratio >= 0.9 && !_contextWarningShown)
        {
            _contextWarningShown = true;
            _trayIcon.ShowBalloonTip(
                4000,
                "Context Codex hampir penuh",
                "Klik indikator Ctx di CodexBar untuk memadatkan percakapan.",
                Forms.ToolTipIcon.Warning);
        }
        else if (ratio < 0.75)
        {
            _contextWarningShown = false;
        }
    }

    private async void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsBusy)
        {
            SetStatus("Tunggu pekerjaan aktif selesai sebelum compact.", StatusBusyBrush);
            return;
        }

        if (_controller.ThreadId is null)
        {
            SetStatus("Belum ada percakapan untuk dipadatkan.", StatusBusyBrush);
            return;
        }

        try
        {
            SetStatus("Memadatkan konteks…", StatusBusyBrush);
            await _controller.CompactAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outputWindow is { IsVisible: true })
        {
            _outputWindow.Hide();
        }
        else
        {
            var output = GetOutputWindow();
            output.ShowNear(this);
            output.Activate();
        }
    }

    private async Task OpenThreadFromSidebarAsync(ProviderThreadSummary thread)
    {
        if (_controller.IsBusy)
        {
            throw new InvalidOperationException("Selesaikan atau batalkan pekerjaan aktif sebelum berpindah percakapan.");
        }

        await OpenWorkspaceAsync(thread.WorkspacePath, requestedThreadId: thread.Id);
        await EnsureHistoryLoadedAsync();
        ShowOutputSurface();
        Activate();
        PromptBox.Focus();
    }

    private async Task CreateThreadFromSidebarAsync()
    {
        if (_controller.IsBusy)
        {
            throw new InvalidOperationException("Selesaikan atau batalkan pekerjaan aktif sebelum membuat percakapan baru.");
        }

        await OpenWorkspaceAsync(_settings.WorkspacePath, newThread: true);
        _messages.Clear();
        _streamingAgent = null;
        RenderConversation();
        Activate();
        PromptBox.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SetSettingsVisible(SettingsPanel.Visibility != Visibility.Visible);

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => SetSettingsVisible(false);

    private void SetSettingsVisible(bool visible)
    {
        if (visible)
        {
            HotkeyTextBox.Text = _hotKey.CurrentGesture ?? _settings.Hotkey;
            ApplyHotkeyButton.IsEnabled = false;
            HotkeyStatusText.Text = _hotKey.CurrentGesture is { } active
                ? $"Aktif: {active}. Klik field lalu tekan kombinasi baru."
                : "Hotkey belum aktif. Klik field lalu tekan kombinasi baru.";
            HotkeyStatusText.Foreground = _hotKey.CurrentGesture is null
                ? StatusErrorBrush
                : _mutedText;
        }

        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout(force: true);
    }

    private void ResetLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressSizeSave = true;
        WidthSlider.Value = 620;
        HeightSlider.Value = 420;
        Width = 620;
        if (_outputWindow is not null)
        {
            _outputWindow.Width = 620;
            _outputWindow.Height = 420;
        }
        _suppressSizeSave = false;
        _settings.Window.Width = 620;
        _settings.Window.MaxExpandedHeight = 420;
        _settings.Window.Position = "bottom-center";
        WindowPositioner.PlaceAboveTaskbar(this, _settings.Window.Position);
        UpdateLayout(force: true);
        _ = SaveSettingsSafeAsync();
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e) => CopyResponse();

    private async void ExportCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0)
        {
            SetStatus("Belum ada percakapan untuk diekspor.", StatusBusyBrush);
            return;
        }

        var workspaceName = string.IsNullOrWhiteSpace(_settings.WorkspacePath)
            ? "workspace"
            : new DirectoryInfo(_settings.WorkspacePath).Name;
        var safeName = string.Concat(workspaceName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export checkpoint percakapan",
            FileName = $"{safeName}-codex-checkpoint-{DateTime.Now:yyyyMMdd-HHmm}.md",
            DefaultExt = ".md",
            Filter = "Markdown|*.md|Semua file|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        try
        {
            _dialogVisible = true;
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var checkpoint = new StringBuilder();
            checkpoint.AppendLine("# CodexBar conversation checkpoint");
            checkpoint.AppendLine();
            checkpoint.AppendLine($"- Exported: {DateTimeOffset.Now:O}");
            checkpoint.AppendLine($"- Workspace: `{_settings.WorkspacePath}`");
            checkpoint.AppendLine($"- Thread: `{_controller.ThreadId ?? "unknown"}`");
            checkpoint.AppendLine();
            foreach (var message in _messages)
            {
                checkpoint.AppendLine(message.IsUser ? "## User" : "## Agent");
                checkpoint.AppendLine();
                checkpoint.AppendLine(message.Markdown);
                checkpoint.AppendLine();
            }

            await File.WriteAllTextAsync(dialog.FileName, checkpoint.ToString(), new UTF8Encoding(false));
            SetStatus("Checkpoint tersimpan", StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError($"Checkpoint gagal disimpan: {exception.Message}");
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private void CopyResponse()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var transcript = new StringBuilder();
        foreach (var message in _messages)
        {
            transcript.AppendLine(message.IsUser ? "**Kamu**" : "**Codex**");
            transcript.AppendLine(message.Markdown.Trim());
            transcript.AppendLine();
        }

        try
        {
            System.Windows.Clipboard.SetText(transcript.ToString().Trim());
            SetStatus("Percakapan tersalin", StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError($"Clipboard tidak tersedia: {exception.Message}");
        }
    }

    private void TopmostToggle_Click(object sender, RoutedEventArgs e) => ToggleTopmost();

    private void ToggleTopmost()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        if (_outputWindow is not null)
        {
            _outputWindow.Topmost = _settings.AlwaysOnTop;
        }
        if (_sidebarWindow is not null)
        {
            _sidebarWindow.Topmost = _settings.AlwaysOnTop;
        }
        TopmostToggle.IsChecked = _settings.AlwaysOnTop;
        _ = SaveSettingsSafeAsync();
    }

    private void OpenCurrentThread()
    {
        if (_controller.ThreadId is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo($"codex://threads/{_controller.ThreadId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError($"Codex Desktop tidak dapat dibuka: {exception.Message}");
        }
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendButtonState();
        PromptPlaceholder.Visibility = PromptBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSendButtonState() =>
        SendButton.IsEnabled = !_controller.IsBusy &&
            (PromptBox.Text.Trim().Length > 0 || _attachments.Count > 0);

    private void PromptBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _promptFocused = true;
        UpdateDirWidth();
    }

    private void PromptBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _promptFocused = false;
        UpdateDirWidth();
    }

    private void PromptField_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _fieldHover = true;
        UpdateDirWidth();
    }

    private void PromptField_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _fieldHover = false;
        UpdateDirWidth();
    }

    private void WorkspaceButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _dirHover = true;
        UpdateDirWidth();
    }

    private void WorkspaceButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _dirHover = false;
        UpdateDirWidth();
    }

    private void UpdateDirWidth()
    {
        var compact = ActualWidth > 0 && ActualWidth < 600;
        var idleWidth = compact ? 0 : DirWidthIdle;
        var expandedWidth = compact ? 150 : DirWidthExpanded;
        // Prioritas: hover direktori → lebar penuh; sedang mengetik/hover input → menyusut; selain itu idle.
        var target = _dirHover
            ? expandedWidth
            : (_promptFocused || _fieldHover ? DirWidthCollapsed : idleWidth);

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        WorkspacePathText.BeginAnimation(FrameworkElement.MaxWidthProperty, animation);
    }

    private void UpdateContextBadgeLayout()
    {
        if (_lastContextPercent is not { } percent)
        {
            return;
        }

        ContextUsageButton.Content = ActualWidth > 0 && ActualWidth < 600
            ? $"{percent}%"
            : $"Ctx {percent}%";
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        ApplyOpacity(isActive: true);
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        ApplyOpacity(isActive: false);
        if (!_settings.Pinned &&
            _settings.HideOnFocusLost &&
            !_approvalVisible &&
            !_dialogVisible &&
            SettingsPanel.Visibility != Visibility.Visible &&
            _sidebarWindow is not { IsVisible: true })
        {
            await Task.Delay(120);
            if (!IsActive && !_approvalVisible && !_dialogVisible)
            {
                Hide();
            }
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e) => RepositionSidebarIfDocked();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Jaga tepi bawah tetap menempel di atas taskbar saat tinggi berubah (output/settings/multiline)
        // sehingga window tumbuh ke atas, bukan menutupi taskbar.
        if (e.HeightChanged && _bottomAnchored && WindowState == WindowState.Normal)
        {
            Top -= e.NewSize.Height - e.PreviousSize.Height;
        }

        if (e.WidthChanged)
        {
            UpdateDirWidth();
            UpdateContextBadgeLayout();
        }

        if (!e.WidthChanged || !_settingsReady || _suppressSizeSave || WindowState != WindowState.Normal)
        {
            RepositionSidebarIfDocked();
            return;
        }

        _settings.Window.Width = ActualWidth;
        if (_outputWindow is not null)
        {
            _outputWindow.Width = ActualWidth;
        }
        WidthSlider.Value = Math.Clamp(ActualWidth, WidthSlider.Minimum, WidthSlider.Maximum);
        RepositionSidebarIfDocked();
        _ = SaveSettingsSafeAsync();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _renderTimer.Stop();
        _sidebarWindow?.Close();
        if (_outputWindow is not null)
        {
            _outputWindow.PrepareForExit();
            _outputWindow.Close();
        }
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _hotKey.Dispose();
        _approvalGate.Dispose();
        CleanupTemporaryAttachments(_temporaryAttachmentPaths.ToArray());
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void ShowError(string message)
    {
        SetStatus("Error", StatusErrorBrush);
        _streamingAgent = null;
        var markdown = $"**Error:** {message}";
        if (_messages.LastOrDefault() is not { IsUser: false } last || last.Markdown != markdown)
        {
            _messages.Add(new ChatMessage { IsUser = false, Markdown = markdown });
        }
        RenderConversation();
        ShowOutputSurface();
    }
}
