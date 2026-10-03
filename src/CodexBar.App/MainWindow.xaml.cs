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

public partial class MainWindow : Window, INotchHost, ISettingsHost
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
    private readonly Func<string, IAgentProvider> _providerFactory;
    private OutputWindow? _outputWindow;
    private NotchWindow? _notch;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboardingWindow;
    private readonly GlobalHotKey _hotKey = new();
    private readonly GlobalHotKey _quickCaptureHotKey = new(0x4343);
    private string? _pendingQuickCaptureReveal;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _renderTimer;
    private readonly SemaphoreSlim _approvalGate = new(1, 1);
    private readonly List<ChatMessage> _messages = new();
    private readonly List<ProviderInputAttachment> _attachments = new();
    private readonly PromptHistory _promptHistory = new();
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
    private bool _quickCaptureActive;
    private bool _autoResumeArmed;
    private bool _autoResumeIsContinuation;
    private bool _pendingResumeChecked;
    private DateTimeOffset? _autoResumeAt;
    private string? _lastPrompt;
    private string? _autoResumeSendText;
    private DispatcherTimer? _autoResumeTimer;
    private DispatcherTimer? _scheduleTimer;
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
        IAgentProvider provider,
        Func<string, IAgentProvider> providerFactory)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _providerFactory = providerFactory;
        _controller = new ConversationController(provider);
        _controller.EventReceived += Controller_EventReceived;

        InitializeComponent();
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        InlineConversation.ScrollChanged += ResponseScroll_ScrollChanged;

        ConfigureFromSettings();
        CodexBar.Core.Localization.Instance.PropertyChanged += Localization_Changed;
        ApplyLanguage();

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

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _scheduleTimer.Tick += ScheduleTick;
        _scheduleTimer.Start();

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

    public event Action<IAgentProvider>? ActiveProviderChanged;

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
            AboutText.Text = LF("settings.about", version?.ToString(3) ?? "1.0.0");
            PromptBox.Focus();
            await StartupAsync();
            ApplyDisplayMode();
            OpenDevPreviews();
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
            SetStatus(L("status.connecting"), StatusBusyBrush);
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
                ShowOnboarding(_controller.Capabilities.SupportsApiKey
                    ? OnboardingKind.ApiKeyNeeded
                    : OnboardingKind.LoginNeeded);
                return;
            }

            // The ChatGPT-vs-API-key warning only applies to the Codex CLI (key-based providers skip it).
            if (_controller.ProviderId == "codex" &&
                !_controller.Capabilities.SupportsApiKey &&
                !account.AuthenticationMethod.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) &&
                !_settings.ApiKeyWarningAcknowledged)
            {
                var choice = System.Windows.MessageBox.Show(
                    this,
                    L("dialog.apiKeyWarning"),
                    L("dialog.apiKeyTitle"),
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
            if (!_controller.Capabilities.SupportsWorkspace)
            {
                // Threadless provider (e.g. Claude): no workspace needed; CWD is an inert placeholder.
                await OpenWorkspaceAsync(Environment.CurrentDirectory);
            }
            else if (string.IsNullOrWhiteSpace(_settings.WorkspacePath) ||
                !Directory.Exists(_settings.WorkspacePath))
            {
                ShowOnboarding(OnboardingKind.WorkspaceNeeded);
                return;
            }
            else
            {
                await OpenWorkspaceAsync(_settings.WorkspacePath);
            }

            if (_controller.ThreadId is not null)
            {
                RestorePendingResume();
            }
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
        ApiKeyNeeded,
        Error
    }

    private string _onboardingCommand = string.Empty;

    private void ShowOnboarding(OnboardingKind kind, string? detail = null)
    {
        _onboardingKind = kind;
        InlineResponseArea.Visibility = Visibility.Collapsed;
        var loc = CodexBar.Core.Localization.Instance;
        OnboardingRetryButton.Content = loc["onboard.retry"];
        OnboardingCopyButton.Content = loc["onboard.copy"];
        OnboardingDocsButton.Content = loc["onboard.docs"];
        OnboardingDocsButton.Visibility = Visibility.Visible;
        OnboardingActionButton.Visibility = Visibility.Collapsed;

        switch (kind)
        {
            case OnboardingKind.InstallNeeded:
                OnboardingTitle.Text = loc[OnboardingKey("onboard.install.title")];
                OnboardingBody.Text = loc[OnboardingKey("onboard.install.body")];
                _onboardingCommand = IsClaudeCodeProvider
                    ? "npm install -g " + ClaudeCodeNpmPackage
                    : "npm install -g @openai/codex";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = loc["onboard.install.action"];
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus(loc[OnboardingKey("onboard.install.status")], StatusErrorBrush);
                break;

            case OnboardingKind.LoginNeeded:
                OnboardingTitle.Text = loc[OnboardingKey("onboard.login.title")];
                OnboardingBody.Text = detail ?? loc[OnboardingKey("onboard.login.body")];
                _onboardingCommand = IsClaudeCodeProvider ? "claude auth login" : "codex login";
                OnboardingCommandBox.Visibility = Visibility.Visible;
                OnboardingActionButton.Content = loc["onboard.login.action"];
                OnboardingActionButton.Visibility = Visibility.Visible;
                SetStatus(loc["onboard.login.status"], StatusBusyBrush);
                break;

            case OnboardingKind.WorkspaceNeeded:
                OnboardingTitle.Text = loc["onboard.workspace.title"];
                OnboardingBody.Text = loc["onboard.workspace.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                OnboardingRetryButton.Content = loc["onboard.workspace.pick"];
                OnboardingDocsButton.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.workspace.status"], StatusBusyBrush);
                break;

            case OnboardingKind.ApiKeyNeeded:
                OnboardingTitle.Text = loc["onboard.apikey.title"];
                OnboardingBody.Text = loc["onboard.apikey.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                OnboardingDocsButton.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.apikey.status"], StatusErrorBrush);
                break;

            default:
                OnboardingTitle.Text = loc["onboard.error.title"];
                OnboardingBody.Text = detail ?? loc["onboard.error.body"];
                _onboardingCommand = string.Empty;
                OnboardingCommandBox.Visibility = Visibility.Collapsed;
                SetStatus(loc["onboard.error.status"], StatusErrorBrush);
                break;
        }

        OnboardingCommand.Text = _onboardingCommand;
        OnboardingPanel.Visibility = Visibility.Visible;
        if (IsNotchMode)
        {
            ShowOnboardingWindow(kind, detail);
        }
    }

    private void HideOnboarding()
    {
        OnboardingPanel.Visibility = Visibility.Collapsed;
        _onboardingWindow?.Hide();
    }

    private const string ClaudeCodeNpmPackage = "@anthropic-ai/claude-code";

    private bool IsClaudeCodeProvider => _controller.ProviderId == "claude-code";

    // Claude-subscription onboarding reuses the Codex panel with its own copy ("onboard.claude.*").
    private string OnboardingKey(string codexKey) =>
        IsClaudeCodeProvider ? codexKey.Replace("onboard.", "onboard.claude.") : codexKey;

    private void OnboardingCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingCommand.Length > 0)
        {
            try
            {
                System.Windows.Clipboard.SetText(_onboardingCommand);
                OnboardingCopyButton.Content = CodexBar.Core.Localization.Instance["onboard.copied"];
            }
            catch (Exception exception)
            {
                ShowError(LF("status.clipboardUnavailable", exception.Message));
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
                    Title = L("dialog.chooseWorkspace"),
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
            OnboardingCopyButton.Content = CodexBar.Core.Localization.Instance["onboard.copy"];
        }
    }

    private void OnboardingDocsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(IsClaudeCodeProvider
                ? "https://docs.claude.com/en/docs/claude-code/setup"
                : "https://github.com/openai/codex#installation")
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
                if (IsClaudeCodeProvider)
                {
                    LaunchClaudeLogin();
                }
                else
                {
                    LaunchCodexLogin();
                }

                OnboardingBody.Text = CodexBar.Core.Localization.Instance["onboard.login.wait"];
                return;
            }

            if (_onboardingKind != OnboardingKind.InstallNeeded)
            {
                return;
            }

            var loc = CodexBar.Core.Localization.Instance;
            OnboardingBody.Text = loc[OnboardingKey("onboard.install.progress")];
            SetStatus(loc[OnboardingKey("onboard.install.statusProgress")], StatusBusyBrush);
            var npm = FindCommand("npm.cmd") ?? FindCommand("npm.exe");
            if (npm is null)
            {
                var winget = FindCommand("winget.exe")
                    ?? throw new InvalidOperationException(
                        loc["onboard.install.nodeMissing"]);
                await RunSetupProcessAsync(winget,
                    "install", "--id", "OpenJS.NodeJS.LTS", "--exact",
                    "--accept-package-agreements", "--accept-source-agreements", "--silent");
                npm = FindCommand("npm.cmd")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
                if (!File.Exists(npm))
                {
                    throw new FileNotFoundException(loc["onboard.install.npmMissing"]);
                }
            }

            await RunSetupProcessAsync(npm, "install", "-g",
                IsClaudeCodeProvider ? ClaudeCodeNpmPackage : "@openai/codex");
            ShowOnboarding(OnboardingKind.LoginNeeded,
                loc[OnboardingKey("onboard.install.success")]);
        }
        catch (Exception exception)
        {
            ShowOnboarding(OnboardingKind.Error, string.Format(
                CodexBar.Core.Localization.Instance["onboard.install.failed"], exception.Message));
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

    private static void LaunchClaudeLogin()
    {
        var launcher = CodexBar.Providers.ClaudeCode.ClaudeCodeExecutableLocator.TryLocate() ?? "claude";
        Process.Start(new ProcessStartInfo(launcher, "auth login") { UseShellExecute = true });
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
            HotkeyStatusText.Text = LF("hotkey.active", _settings.Hotkey);
            HotkeyStatusText.Foreground = StatusReadyBrush;
        }
        catch (Exception exception)
        {
            HotkeyStatusText.Text = exception.Message;
            HotkeyStatusText.Foreground = StatusErrorBrush;
            SetStatus(exception.Message, StatusErrorBrush);
            _trayIcon.ShowBalloonTip(
                5000,
                L("notification.hotkeyInactive"),
                exception.Message,
                Forms.ToolTipIcon.Warning);
        }

        try
        {
            _quickCaptureHotKey.Pressed += QuickCaptureHotKey_Pressed;
            _quickCaptureHotKey.Register(this, _settings.QuickCaptureHotkey);
            _settings.QuickCaptureHotkey = _quickCaptureHotKey.CurrentGesture ?? _settings.QuickCaptureHotkey;
            DiagnosticsLog.Info("QuickCaptureHotkey", $"registered {_settings.QuickCaptureHotkey}");
        }
        catch (Exception exception)
        {
            // The quick-capture hotkey is optional; a conflict must not break the main hotkey,
            // but make the failure visible so the user knows the combo didn't register.
            DiagnosticsLog.Error("QuickCaptureHotkey", exception.Message);
            SetStatus(LF("quickCapture.registerFailed", _settings.QuickCaptureHotkey), StatusErrorBrush);
        }
    }

    // Dedicated hotkey: capture the foreground selection and send it straight to the AI without
    // necessarily opening the window. What happens on completion is driven by QuickCaptureReveal.
    private async void QuickCaptureHotKey_Pressed(object? sender, EventArgs e)
    {
        if (_quickCaptureActive)
        {
            return;
        }

        if (_controller.IsBusy || _controller.ThreadId is null)
        {
            return;
        }

        _quickCaptureActive = true;
        try
        {
            var selection = await SelectionCapture.TryCaptureTextAsync();
            if (string.IsNullOrWhiteSpace(selection))
            {
                return;
            }

            _pendingQuickCaptureReveal = _settings.QuickCaptureReveal;
            if (_settings.QuickCaptureReveal == "capture")
            {
                ShowFromExternalActivation();
            }

            PromptBox.Text = selection;
            await SendPromptAsync();
        }
        catch (Exception exception)
        {
            _pendingQuickCaptureReveal = null;
            DiagnosticsLog.Error("QuickCapture", exception.Message);
        }
        finally
        {
            _quickCaptureActive = false;
        }
    }

    // The main hotkey only toggles the overlay. Capturing a selection is now the dedicated
    // quick-capture hotkey's job (see QuickCaptureHotKey_Pressed).
    private void GlobalHotKey_Pressed(object? sender, EventArgs e)
    {
        if (IsNotchMode && _notch is not null)
        {
            if (OnboardingPanel.Visibility == Visibility.Visible && _onboardingWindow is not null)
            {
                _onboardingWindow.Show();
                _onboardingWindow.Activate();
            }
            else
            {
                _notch.ToggleLauncher();
            }
            return;
        }

        if (IsVisible && IsActive)
        {
            ToggleOverlay();
        }
        else
        {
            ShowFromExternalActivation();
        }
    }

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
            SettingsStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ModelRow.Visibility = Visibility.Collapsed;
            ModelCombo.ToolTip = LF("status.modelsUnavailable", exception.Message);
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
            throw new DirectoryNotFoundException(LF("error.workspaceNotFound", normalizedPath));
        }

        var threadKey = _settings.ThreadKey(normalizedPath);
        var previousThread = newThread
            ? null
            : requestedThreadId ?? (_settings.ThreadIds.TryGetValue(threadKey, out var stored) ? stored : null);

        await _controller.OpenWorkspaceAsync(normalizedPath, previousThread, _settings.SandboxMode, _settings.ApprovalPolicy);
        if (_controller.Capabilities.SupportsWorkspace)
        {
            _settings.WorkspacePath = normalizedPath;
        }
        _workspaceLabel = new DirectoryInfo(normalizedPath).Name;
        WorkspacePathText.Text = FormatWorkspaceDisplay(normalizedPath);
        WorkspaceButton.ToolTip = normalizedPath;
        SetStatus(L("status.ready"), StatusReadyBrush);
        _outputWindow?.SetContext(_workspaceLabel, L("status.ready"));

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
        SettingsStateChanged?.Invoke(this, EventArgs.Empty);
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

        SetStatus(L("status.loadingConversation"), StatusBusyBrush);
        await LoadThreadMessagesAsync(threadId);
        SetStatus(L("status.ready"), StatusReadyBrush);
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

    private static string L(string key) => CodexBar.Core.Localization.Instance[key];

    private static string LF(string key, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

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

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down &&
            TryRecallPrompt(e.Key == Key.Up))
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

        DisarmAutoResume();
        _lastPrompt = prompt;

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
        _notch?.OnTurnStarted(prompt.Length > 0 ? prompt : attachmentSummary);
        _streamingAgent = null;
        _autoFollow = true;
        _responseZoomDelta = 0;
        RenderConversation();
        SetBusyUi(true);
        _outputWindow?.SetContext(_workspaceLabel, "Mengirim…");
        if (!IsNotchMode || IsVisible)
        {
            ShowOutputSurface();
        }
        SetStatus(L("status.sending"), StatusBusyBrush);

        try
        {
            var selectedModel = (ModelCombo.SelectedItem as ProviderModelInfo)?.Model
                ?? _settings.SelectedModel;
            var effort = string.IsNullOrWhiteSpace(_settings.ReasoningEffort) ? null : _settings.ReasoningEffort;
            DiagnosticsLog.Info("Send",
                $"provider={_controller.ProviderId} model={selectedModel ?? "default"} promptChars={prompt.Length} attachments={pendingAttachments.Length}");
            await _controller.SendAsync(prompt, pendingAttachments, selectedModel, effort);
            _promptHistory.Add(prompt);
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
            _notch?.HandleProviderEvent(new ProviderErrorEvent(exception.Message));
            ShowError(exception.Message);
        }
    }

    // Auto-resume: when a provider reports a usage/rate limit, schedule one resend of the
    // last prompt at the reset time it names. Transparent (shows a countdown status) and
    // cancelled the moment the user sends anything manually.
    private bool TryArmAutoResume(string message)
    {
        var now = DateTimeOffset.Now;
        var retryAt = CodexBar.Core.RateLimit.ParseRetryAt(message, now) ?? now.AddMinutes(5);
        if (retryAt <= now || retryAt > now.AddHours(24))
        {
            return false;
        }

        // If a response had already started streaming when the limit hit, resume by asking
        // the model to continue the cut-off part rather than resending the whole prompt.
        var isContinuation = _streamingAgent is { } partial && !string.IsNullOrWhiteSpace(partial.Markdown);
        var sendText = isContinuation ? L("autoResume.continueInstruction") : _lastPrompt;
        if (string.IsNullOrWhiteSpace(sendText))
        {
            return false;
        }

        ScheduleAutoResume(sendText, retryAt, isContinuation, persist: true);
        return true;
    }

    private void ScheduleAutoResume(string sendText, DateTimeOffset retryAt, bool isContinuation, bool persist)
    {
        _autoResumeSendText = sendText;
        _autoResumeAt = retryAt;
        _autoResumeIsContinuation = isContinuation;
        _autoResumeArmed = true;

        if (persist)
        {
            _settings.PendingResume = new CodexBar.Core.PendingResume
            {
                Prompt = sendText,
                RetryAt = retryAt,
                IsContinuation = isContinuation,
                ThreadId = _controller.ThreadId
            };
            _ = SaveSettingsSafeAsync();
        }

        if (_autoResumeTimer is null)
        {
            _autoResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoResumeTimer.Tick += AutoResumeTick;
        }

        _autoResumeTimer.Start();
        UpdateAutoResumeStatus();
    }

    // Re-arm (or fire immediately) a resume persisted from a previous run. Runs once at startup.
    private void RestorePendingResume()
    {
        if (_pendingResumeChecked)
        {
            return;
        }

        _pendingResumeChecked = true;
        if (_settings.PendingResume is not { } pending || string.IsNullOrWhiteSpace(pending.Prompt))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (pending.RetryAt > now.AddHours(24))
        {
            ClearPendingResume();
            return;
        }

        _lastPrompt = pending.Prompt;
        if (pending.RetryAt <= now)
        {
            // Reset moment already passed while the app was closed: resume now.
            ClearPendingResume();
            if (!_controller.IsBusy)
            {
                SetStatus(L("autoResume.resuming"), StatusBusyBrush);
                PromptBox.Text = pending.Prompt;
                _ = SendPromptAsync();
            }

            return;
        }

        ScheduleAutoResume(pending.Prompt, pending.RetryAt, pending.IsContinuation, persist: false);
    }

    private void AutoResumeTick(object? sender, EventArgs e)
    {
        if (!_autoResumeArmed || _autoResumeAt is not { } retryAt)
        {
            _autoResumeTimer?.Stop();
            return;
        }

        if (DateTimeOffset.Now < retryAt)
        {
            UpdateAutoResumeStatus();
            return;
        }

        _autoResumeTimer?.Stop();
        _autoResumeArmed = false;
        AutoResumeBar.Visibility = Visibility.Collapsed;
        ClearPendingResume();
        if (_controller.IsBusy || string.IsNullOrWhiteSpace(_autoResumeSendText))
        {
            return;
        }

        SetStatus(L("autoResume.resuming"), StatusBusyBrush);
        PromptBox.Text = _autoResumeSendText;
        _ = SendPromptAsync();
    }

    private void UpdateAutoResumeStatus()
    {
        if (_autoResumeAt is { } retryAt)
        {
            var key = _autoResumeIsContinuation ? "autoResume.scheduledContinue" : "autoResume.scheduled";
            var text = LF(key, retryAt.ToLocalTime().ToString("HH:mm"));
            SetStatus(text, StatusBusyBrush);
            AutoResumeText.Text = text;
            AutoResumeBar.Visibility = Visibility.Visible;
        }
    }

    private void CancelAutoResumeButton_Click(object sender, RoutedEventArgs e)
    {
        DisarmAutoResume();
        SetStatus(L("autoResume.cancelled"), StatusReadyBrush);
    }

    private void DisarmAutoResume()
    {
        if (!_autoResumeArmed)
        {
            return;
        }

        _autoResumeArmed = false;
        _autoResumeAt = null;
        _autoResumeTimer?.Stop();
        AutoResumeBar.Visibility = Visibility.Collapsed;
        ClearPendingResume();
    }

    private void ClearPendingResume()
    {
        if (_settings.PendingResume is not null)
        {
            _settings.PendingResume = null;
            _ = SaveSettingsSafeAsync();
        }
    }

    private async void ScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SchedulePromptDialog(PromptBox.Text, _settings.ScheduledPrompts) { Owner = this };
        try
        {
            _dialogVisible = true;
            if (dialog.ShowDialog() == true)
            {
                _settings.ScheduledPrompts = new List<CodexBar.Core.ScheduledPrompt>(dialog.Schedules);
                await SaveSettingsSafeAsync();
                SetStatus(LF("schedule.saved", _settings.ScheduledPrompts.Count), StatusReadyBrush);
            }
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private void ScheduleTick(object? sender, EventArgs e)
    {
        if (_controller.IsBusy || _controller.ThreadId is null || _settings.ScheduledPrompts.Count == 0)
        {
            return;
        }

        var due = CodexBar.Core.ScheduledPrompt.Due(_settings.ScheduledPrompts, DateTimeOffset.Now);
        if (due.Count == 0)
        {
            return;
        }

        // Fire the earliest due prompt; the rest run on later ticks.
        var next = due[0];
        _settings.ScheduledPrompts.RemoveAll(item => item.Id == next.Id);
        _ = SaveSettingsSafeAsync();
        SetStatus(L("schedule.running"), StatusBusyBrush);
        PromptBox.Text = next.Prompt;
        _ = SendPromptAsync();
    }

    private bool TryRecallPrompt(bool previous)
    {
        if (_promptHistory.Items.Count == 0 || PromptBox.SelectionLength > 0)
        {
            return false;
        }

        var line = PromptBox.GetLineIndexFromCharacterIndex(PromptBox.CaretIndex);
        if (previous && line > 0 || !previous && line >= 0 && line < PromptBox.LineCount - 1)
        {
            return false;
        }

        PromptBox.Text = previous
            ? _promptHistory.Previous(PromptBox.Text)
            : _promptHistory.Next();
        PromptBox.CaretIndex = PromptBox.Text.Length;
        return true;
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
                    ShowError(L("status.maxAttachments"));
                    break;
                }

                AddAttachment(fileName);
            }

            RenderAttachments();
        }
        catch (Exception exception)
        {
            ShowError(LF("status.attachmentFailed", exception.Message));
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
                SetStatus(L("status.screenshotAdded"), StatusReadyBrush);
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
                    SetStatus(L("status.clipboardFileAdded"), StatusReadyBrush);
                }
                return added;
            }
        }
        catch (Exception exception)
        {
            ShowError(LF("status.clipboardAttachFailed", exception.Message));
            return true;
        }

        return false;
    }

    private bool AddAttachment(string fileName, string? displayName = null)
    {
        if (_attachments.Count >= 10)
        {
            ShowError(L("status.maxAttachments"));
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
            SetStatus(LF("status.filesAdded", added), StatusReadyBrush);
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
            _notch?.HandleProviderEvent(providerEvent);
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
                    DiagnosticsLog.Info("Turn", $"completed status={completed.Status}");
                    var completedNormally = completed.Status == "completed";
                    SetStatus(
                        completedNormally ? L("status.finished") : completed.Status == "interrupted"
                            ? L("status.cancelled")
                            : completed.Status,
                        completedNormally || completed.Status == "interrupted" ? StatusReadyBrush : StatusErrorBrush);
                    _outputWindow?.SetContext(_workspaceLabel, StatusText.Text);
                    SetBusyUi(false);
                    if (_streamingAgent is not null)
                    {
                        ActiveConversation.UpdateLast(_streamingAgent);
                        _streamingAgent = null;
                    }
                    if (completedNormally && _settings.AutoCopyResponse)
                    {
                        var done = _messages.LastOrDefault(message => !message.IsUser);
                        if (done is { } && !string.IsNullOrWhiteSpace(done.Markdown))
                        {
                            try { System.Windows.Clipboard.SetText(done.Markdown); } catch { /* best-effort */ }
                        }
                    }

                    if (_pendingQuickCaptureReveal is { } qcReveal)
                    {
                        _pendingQuickCaptureReveal = null;
                        if (completedNormally && qcReveal == "clipboard")
                        {
                            var qcResponse = _messages.LastOrDefault(message => !message.IsUser);
                            if (qcResponse is { } && !string.IsNullOrWhiteSpace(qcResponse.Markdown))
                            {
                                try { System.Windows.Clipboard.SetText(qcResponse.Markdown); } catch { /* best-effort */ }
                            }
                        }
                        else if (completedNormally && qcReveal == "done")
                        {
                            ShowFromExternalActivation();
                        }
                    }
                    else if (!IsNotchMode && (!IsVisible || !IsActive))
                    {
                        _trayIcon.ShowBalloonTip(2500, "CodexBar", L("notification.responseDone"),
                            Forms.ToolTipIcon.Info);
                    }
                    break;

                case ProviderErrorEvent error:
                    DiagnosticsLog.Error("Provider", error.Message);
                    if (!error.WillRetry && !_autoResumeArmed && _lastPrompt is { Length: > 0 } &&
                        CodexBar.Core.RateLimit.IsRateLimited(error.Message))
                    {
                        SetBusyUi(false);
                        if (TryArmAutoResume(error.Message))
                        {
                            break;
                        }
                    }

                    SetStatus(error.WillRetry ? L("status.retrying") : L("status.error"),
                        error.WillRetry ? StatusBusyBrush : StatusErrorBrush);
                    if (error.WillRetry)
                    {
                        _outputWindow?.SetContext(_workspaceLabel, L("status.retrying"));
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
                SetStatus(L("status.oldQuestionCancelled"), StatusErrorBrush);
                return;
            }

            if (input.IsPending?.Invoke() == false)
            {
                return;
            }

            // Notch mode: the question is answered inside the notch, no window pops up.
            if (IsNotchMode && _notch is not null)
            {
                _approvalVisible = true;
                var answers = await _notch.RequestInputAsync(input);
                if (input.IsPending?.Invoke() != false)
                {
                    await input.RespondAsync(answers);
                }
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
            ShowError(LF("status.answerFailed", exception.Message));
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
                SetStatus(L("status.oldApprovalDeclined"), StatusErrorBrush);
                return;
            }

            if (approval.IsPending?.Invoke() == false)
            {
                return;
            }

            // Notch mode: approve or decline inside the notch, no dialog and no focus steal.
            if (IsNotchMode && _notch is not null)
            {
                _approvalVisible = true;
                var decision = await _notch.RequestApprovalAsync(approval);
                if (approval.IsPending?.Invoke() != false)
                {
                    await approval.RespondAsync(decision);
                }
                return;
            }

            _approvalVisible = true;
            if (!IsVisible || !IsActive)
            {
                var loc = CodexBar.Core.Localization.Instance;
                _trayIcon.ShowBalloonTip(4000, loc["approval.notificationTitle"],
                    loc["approval.notificationBody"], Forms.ToolTipIcon.Warning);
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
            ShowError(LF("status.approvalFailed", exception.Message));
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
            SetStatus(L("status.cancelling"), StatusBusyBrush);
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
        DisplayModeCombo.SelectedIndex = DisplayModeIndex(_settings.DisplayMode);
        NotchHideIdleCheck.IsChecked = _settings.NotchHideWhenIdle;
        AutoCopyResponseCheck.IsChecked = _settings.AutoCopyResponse;
        LaunchAtLoginCheck.IsChecked = _settings.LaunchAtLogin;
        QuickCaptureHotkeyBox.Text = _settings.QuickCaptureHotkey;
        QuickCaptureRevealCombo.SelectedIndex = _settings.QuickCaptureReveal switch
        {
            "capture" => 0,
            "clipboard" => 2,
            _ => 1
        };
        DiagnosticsLoggingCheck.IsChecked = _settings.EnableDiagnosticsLogging;
        LanguageCombo.SelectedIndex = _settings.Language == "en" ? 1 : 0;
        SelectProviderComboItem(_settings.Provider);
        ApiKeyPanel.Visibility = IsApiKeyProvider(_settings.Provider) ? Visibility.Visible : Visibility.Collapsed;
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
        var displayModeBefore = _settings.DisplayMode;
        _settings.DisplayMode = (DisplayModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "bar" => "bar",
            "island" => "island",
            _ => "notch"
        };
        _settings.NotchHideWhenIdle = NotchHideIdleCheck.IsChecked == true;
        _settings.AutoCopyResponse = AutoCopyResponseCheck.IsChecked == true;
        _settings.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
        StartupRegistration.Apply(_settings.LaunchAtLogin);
        _settings.QuickCaptureReveal = (QuickCaptureRevealCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "capture" => "capture",
            "clipboard" => "clipboard",
            _ => "done"
        };
        _settings.EnableDiagnosticsLogging = DiagnosticsLoggingCheck.IsChecked == true;
        DiagnosticsLog.Configure(_settings.EnableDiagnosticsLogging, _settingsStore.BaseDirectory);
        _settings.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "en" ? "en" : "id";
        CodexBar.Core.Localization.Instance.SetLanguage(CodexBar.Core.Localization.Parse(_settings.Language));
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
        if (displayModeBefore != _settings.DisplayMode)
        {
            ApplyDisplayMode(keepMainVisible: true);
        }
        else
        {
            _notch?.ShowIdle();
        }
        _ = SaveSettingsSafeAsync();
    }

    // ---- Display mode (notch / bar) -------------------------------------------------------

    // "notch" and "island" share the same surface; only the shape differs.
    private bool IsNotchMode => _settings.DisplayMode is "notch" or "island";

    private static int DisplayModeIndex(string mode) => mode switch { "island" => 1, "bar" => 2, _ => 0 };

    private void SetDisplayMode(string mode)
    {
        if (_settings.DisplayMode == mode)
        {
            return;
        }

        _settings.DisplayMode = mode;
        _settingsReady = false;
        DisplayModeCombo.SelectedIndex = DisplayModeIndex(mode);
        _settingsReady = true;
        ApplyDisplayMode();
        _ = SaveSettingsSafeAsync();
    }

    // Notch mode keeps this window as the (hidden) engine: settings, onboarding, approvals and
    // "open in window" still surface it. Bar mode is the original taskbar-anchored launcher.
    private void ApplyDisplayMode(bool keepMainVisible = false)
    {
        if (IsNotchMode)
        {
            _notch ??= new NotchWindow(this);
            _notch.SetIsland(_settings.DisplayMode == "island");
            _notch.ShowIdle();
            if (!keepMainVisible)
            {
                Hide();
            }

            if (OnboardingPanel.Visibility == Visibility.Visible)
            {
                ShowOnboardingWindow(_onboardingKind, null);
            }
        }
        else
        {
            _notch?.Hide();
            if (!IsVisible)
            {
                ShowFromExternalActivation();
            }
        }
    }

    // ---- Settings window --------------------------------------------------------------------

    private event EventHandler? SettingsStateChanged;

    event EventHandler? ISettingsHost.StateChanged
    {
        add => SettingsStateChanged += value;
        remove => SettingsStateChanged -= value;
    }

    internal void OpenSettingsWindow(string page = "general")
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.ShowPage(page);
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowPage(page);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    AppSettings ISettingsHost.Settings => _settings;

    string ISettingsHost.ProviderId => _controller.ProviderId;

    bool ISettingsHost.ProviderSupportsApprovals => _controller.ProviderId == "codex";

    string ISettingsHost.WorkspacePath => _settings.WorkspacePath;

    IReadOnlyList<ProviderModelInfo> ISettingsHost.Models =>
        (ModelCombo.ItemsSource as IEnumerable<ProviderModelInfo>)?.ToArray() ?? [];

    // The settings window edits _settings; push it through the same path the old inline panel
    // used (controls ← settings, then the shared apply/save routine).
    void ISettingsHost.ApplySettings()
    {
        ConfigureFromSettings();
        SettingsControl_Changed(this, new RoutedEventArgs());
    }

    void ISettingsHost.SelectProvider(string providerId) => SelectProviderComboItem(providerId);

    async Task<ProviderAccountInfo?> ISettingsHost.GetAccountAsync()
    {
        try
        {
            return await _controller.GetAccountAsync();
        }
        catch
        {
            return null;
        }
    }

    string ISettingsHost.SaveApiKey(string key)
    {
        ApiKeyBox.Password = key;
        SaveApiKeyButton_Click(this, new RoutedEventArgs());
        _ = RunUiTaskAsync(StartupAsync);
        return ApiKeyStatusText.Text;
    }

    string ISettingsHost.ClearApiKey()
    {
        ClearApiKeyButton_Click(this, new RoutedEventArgs());
        return ApiKeyStatusText.Text;
    }

    void ISettingsHost.SelectModel(ProviderModelInfo model) => ModelCombo.SelectedItem = model;

    void ISettingsHost.SelectSandbox(string mode) =>
        SandboxCombo.SelectedItem = SandboxCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == mode);

    void ISettingsHost.ChooseWorkspace() => WorkspaceButton_Click(this, new RoutedEventArgs());

    (bool Ok, string Message) ISettingsHost.ApplyHotkey(string gesture)
    {
        HotkeyTextBox.Text = gesture;
        ApplyHotkeyButton_Click(this, new RoutedEventArgs());
        return (ReferenceEquals(HotkeyStatusText.Foreground, StatusReadyBrush), HotkeyStatusText.Text);
    }

    (bool Ok, string Message) ISettingsHost.ApplyQuickCaptureHotkey(string gesture)
    {
        try
        {
            _quickCaptureHotKey.ChangeGesture(gesture);
            _settings.QuickCaptureHotkey = _quickCaptureHotKey.CurrentGesture ?? _settings.QuickCaptureHotkey;
            QuickCaptureHotkeyBox.Text = _settings.QuickCaptureHotkey;
            _ = SaveSettingsSafeAsync();
            return (true, LF("hotkey.active", _settings.QuickCaptureHotkey));
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    void ISettingsHost.OpenLogin()
    {
        if (_controller.ProviderId == "claude-code")
        {
            LaunchClaudeLogin();
        }
        else
        {
            LaunchCodexLogin();
        }
    }

    void ISettingsHost.RunAction(string action)
    {
        var args = new RoutedEventArgs();
        switch (action)
        {
            case "recenter": _notch?.Recenter(); break;
            case "logs": OpenLogsButton_Click(this, args); break;
            case "templates": ManageTemplatesButton_Click(this, args); break;
            case "export": ExportSettingsButton_Click(this, args); break;
            case "import": ImportSettingsButton_Click(this, args); break;
            case "bug": ReportBugButton_Click(this, args); break;
            case "reset-layout": ResetLayoutButton_Click(this, args); break;
        }
    }

    // Developer aids for checking the new windows without clicking through the tray:
    // CODEXBAR_OPEN_SETTINGS=<page> and CODEXBAR_PREVIEW_ONBOARDING=install|login|apikey|workspace|error.
    private void OpenDevPreviews()
    {
        if (Environment.GetEnvironmentVariable("CODEXBAR_OPEN_SETTINGS") is { Length: > 0 } page)
        {
            OpenSettingsWindow(page);
        }

        // CODEXBAR_PREVIEW_REQUEST=approval|input: show a sample request in the notch (no provider involved).
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_REQUEST") is { Length: > 0 } request && _notch is not null)
        {
            var raw = System.Text.Json.JsonDocument.Parse("{}").RootElement;
            if (request == "input")
            {
                var question = new ProviderInputQuestion("target", "Target", "Deploy ke lingkungan mana?",
                    [new ProviderInputOption("Staging", "Aman untuk dicoba"), new ProviderInputOption("Production", "Pengguna sungguhan")],
                    AllowsOther: true);
                var note = new ProviderInputQuestion("note", "Catatan", "Ada pesan untuk tim?", [], IsRequired: false);
                _ = _notch.RequestInputAsync(new ProviderUserInputEvent("Konfirmasi deploy", "Agent butuh dua jawaban sebelum lanjut.",
                    [question, note], answers => { DiagnosticsLog.Info("PreviewRequest", $"answers={answers?.Count ?? -1}"); return Task.CompletedTask; }, raw));
            }
            else
            {
                _ = _notch.RequestApprovalAsync(new ProviderApprovalEvent("item/commandExecution/requestApproval",
                    string.Join('\n', "Izinkan Codex menjalankan perintah berikut?", "", "npm install --save-dev vitest",
                        @"Folder: C:\Users\adria\AgentPlayground\ClaudeCode"),
                    decision => { DiagnosticsLog.Info("PreviewRequest", $"decision={decision}"); return Task.CompletedTask; }, raw));
            }
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_ONBOARDING") is { Length: > 0 } preview)
        {
            ShowOnboardingWindow(preview switch
            {
                "install" => OnboardingKind.InstallNeeded,
                "apikey" => OnboardingKind.ApiKeyNeeded,
                "workspace" => OnboardingKind.WorkspaceNeeded,
                "error" => OnboardingKind.Error,
                _ => OnboardingKind.LoginNeeded
            }, null);
        }
    }

    // ---- Onboarding sheet (notch mode) ---------------------------------------------------------

    private void ShowOnboardingWindow(OnboardingKind kind, string? detail)
    {
        var claude = IsClaudeCodeProvider;
        var tool = claude ? "Claude Code" : "Codex CLI";
        var installed = claude
            ? CodexBar.Providers.ClaudeCode.ClaudeCodeExecutableLocator.TryLocate() is not null
            : TryLocateCodex();
        var loc = CodexBar.Core.Localization.Instance;
        var status = new List<OnboardingStatus>();
        if (!_controller.Capabilities.SupportsApiKey)
        {
            status.Add(new OnboardingStatus(tool, installed ? Tr.Text("Terpasang", "Installed") : Tr.Text("Belum terpasang", "Not installed"), installed));
            status.Add(new OnboardingStatus(Tr.Text("Akun", "Account"),
                kind is OnboardingKind.LoginNeeded or OnboardingKind.InstallNeeded ? Tr.Text("Belum masuk", "Not signed in") : Tr.Text("Terhubung", "Connected"),
                kind is not (OnboardingKind.LoginNeeded or OnboardingKind.InstallNeeded)));
        }
        else
        {
            status.Add(new OnboardingStatus("API key", Tr.Text("Belum diatur", "Not set"), false));
        }

        if (kind == OnboardingKind.WorkspaceNeeded)
        {
            status.Add(new OnboardingStatus(Tr.Text("Folder kerja", "Workspace"), Tr.Text("Belum dipilih", "Not chosen"), false));
        }

        Task Retry() => StartupAsync();
        var args = new RoutedEventArgs();
        OnboardingContent content = kind switch
        {
            OnboardingKind.InstallNeeded => new(
                Tr.Text($"Pasang {tool}", $"Install {tool}"),
                loc[OnboardingKey("onboard.install.body")],
                status, _onboardingCommand,
                Tr.Text("Pasang otomatis", "Install automatically"), () => { OnboardingActionButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                Tr.Text("Panduan…", "Guide…"), () => OnboardingDocsButton_Click(this, args)),
            OnboardingKind.LoginNeeded => new(
                claude ? Tr.Text("Hubungkan Claude", "Connect Claude") : Tr.Text("Hubungkan Codex", "Connect Codex"),
                detail ?? (claude
                    ? Tr.Text("CodexBar memakai Claude Code yang login dengan langganan Pro atau Max. Tidak perlu API key.", "CodexBar uses Claude Code signed in with a Pro or Max subscription. No API key needed.")
                    : Tr.Text("CodexBar memakai Codex CLI yang login dengan akun ChatGPT.", "CodexBar uses the Codex CLI signed in with your ChatGPT account.")),
                status, _onboardingCommand,
                Tr.Text("Masuk…", "Sign in…"), () => { OnboardingActionButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                Tr.Text("Pakai API key…", "Use an API key…"), () => OpenSettingsWindow("provider")),
            OnboardingKind.ApiKeyNeeded => new(
                Tr.Text("Masukkan API key", "Add an API key"),
                loc["onboard.apikey.body"],
                status, null,
                Tr.Text("Buka pengaturan", "Open settings"), () => { OpenSettingsWindow("provider"); return Task.CompletedTask; },
                Tr.Text("Periksa lagi", "Check again"), Retry,
                null, null),
            OnboardingKind.WorkspaceNeeded => new(
                Tr.Text("Pilih folder kerja", "Choose a workspace"),
                loc["onboard.workspace.body"],
                status, null,
                Tr.Text("Pilih folder…", "Choose folder…"), () => { OnboardingRetryButton_Click(this, args); return Task.CompletedTask; },
                Tr.Text("Nanti", "Later"), () => { _onboardingWindow?.Hide(); return Task.CompletedTask; },
                null, null),
            _ => new(
                loc["onboard.error.title"],
                detail ?? loc["onboard.error.body"],
                status, null,
                Tr.Text("Coba lagi", "Try again"), Retry,
                Tr.Text("Nanti", "Later"), () => { _onboardingWindow?.Hide(); return Task.CompletedTask; },
                Tr.Text("Buka log…", "Open log…"), () => OpenLogsButton_Click(this, args))
        };

        _onboardingWindow ??= new OnboardingWindow();
        _onboardingWindow.Show(content);
    }

    private static bool TryLocateCodex()
    {
        try
        {
            CodexBar.Providers.Codex.CodexExecutableLocator.Locate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    string INotchHost.ProviderName => _controller.ProviderId switch
    {
        "claude-code" => "Claude",
        "anthropic" => "Claude API",
        "openai" => "OpenAI",
        "openrouter" => "OpenRouter",
        _ => "Codex"
    };

    string INotchHost.WorkspaceName => _workspaceLabel;

    bool INotchHost.IsBusy => _controller.IsBusy;

    bool INotchHost.HideNotchWhenIdle => _settings.NotchHideWhenIdle;

    string INotchHost.NotchEdge
    {
        get => _settings.NotchEdge;
        set
        {
            _settings.NotchEdge = value;
            _ = SaveSettingsSafeAsync();
        }
    }

    double INotchHost.NotchOffset
    {
        get => _settings.NotchOffset;
        set
        {
            _settings.NotchOffset = value;
            _ = SaveSettingsSafeAsync();
        }
    }

    IReadOnlyList<PromptTemplate> INotchHost.Templates => _settings.PromptTemplates;

    async Task INotchHost.SubmitFromNotchAsync(string prompt)
    {
        PromptBox.Text = prompt;
        await SendPromptAsync();
    }

    Task INotchHost.StopFromNotchAsync() => StopCurrentTurnAsync();

    Task<IReadOnlyList<ProviderThreadSummary>> INotchHost.ListRecentThreadsAsync() =>
        _controller.ListThreadsAsync(20);

    async Task INotchHost.OpenThreadFromNotchAsync(ProviderThreadSummary thread)
    {
        ShowFromExternalActivation();
        await OpenThreadFromSidebarAsync(thread);
    }

    void INotchHost.OpenConversationWindow()
    {
        ShowFromExternalActivation();
        _ = RunUiTaskAsync(async () =>
        {
            await EnsureHistoryLoadedAsync();
            RenderConversation();
            ShowOutputSurface();
        });
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
            HotkeyStatusText.Text = L("hotkey.cancelled");
            HotkeyStatusText.Foreground = _mutedText;
            return;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            HotkeyStatusText.Text = L("hotkey.holdModifier");
            HotkeyStatusText.Foreground = _mutedText;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatusText.Text = L("hotkey.modifierRequired");
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
                ? L("hotkey.applyHint")
                : LF("hotkey.active", HotkeyTextBox.Text);
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
                ?? throw new InvalidOperationException(L("hotkey.notActive"));
            HotkeyTextBox.Text = _settings.Hotkey;
            HotkeyStatusText.Text = LF("hotkey.active", _settings.Hotkey);
            HotkeyStatusText.Foreground = StatusReadyBrush;
            SetStatus(L("status.hotkeyUpdated"), StatusReadyBrush);
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
                    L("hotkey.saveFailed"),
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
                    L("dialog.dangerWarning"),
                    L("dialog.dangerTitle"),
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
        SandboxStatusText.Text = L("sandbox.applying");
        var appliedToThread = false;
        try
        {
            await _controller.ChangePermissionsAsync(requestedMode, _settings.ApprovalPolicy);
            appliedToThread = _controller.ThreadId is not null;
            _settings.SandboxMode = requestedMode;
            await _settingsStore.SaveAsync(_settings);
            SandboxStatusText.Text = SandboxDescription(requestedMode);
            SandboxStatusText.Foreground = StatusReadyBrush;
            SetStatus(L("status.permissionUpdated"), StatusReadyBrush);
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
            SandboxStatusText.Text = LF("sandbox.failed", exception.Message);
            SandboxStatusText.Foreground = StatusErrorBrush;
        }
        finally
        {
            SandboxCombo.IsEnabled = !_controller.IsBusy;
            _permissionChangePending = false;
            SettingsStateChanged?.Invoke(this, EventArgs.Empty);
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
        "read-only" => L("sandbox.readOnly"),
        "danger-full-access" => L("sandbox.fullAccess"),
        _ => L("sandbox.workspaceWrite")
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
            StatusText.Text = LF("status.settingsSaveFailed", exception.Message);
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
            ShowError(LF("status.historyFailed", exception.Message));
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
        menu.Items.Add(L("tray.show"), null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (IsNotchMode && _notch is not null)
            {
                _notch.ToggleLauncher();
            }
            else
            {
                ToggleOverlay();
            }
        }));
        var modeMenu = new Forms.ToolStripMenuItem(L("tray.displayMode"));
        var notchItem = new Forms.ToolStripMenuItem(L("displayMode.notch")) { Checked = _settings.DisplayMode == "notch" };
        var islandItem = new Forms.ToolStripMenuItem(L("displayMode.island")) { Checked = _settings.DisplayMode == "island" };
        var barItem = new Forms.ToolStripMenuItem(L("displayMode.bar")) { Checked = _settings.DisplayMode == "bar" };
        notchItem.Click += (_, _) => Dispatcher.Invoke(() => SetDisplayMode("notch"));
        islandItem.Click += (_, _) => Dispatcher.Invoke(() => SetDisplayMode("island"));
        barItem.Click += (_, _) => Dispatcher.Invoke(() => SetDisplayMode("bar"));
        modeMenu.DropDownItems.Add(notchItem);
        modeMenu.DropDownItems.Add(islandItem);
        modeMenu.DropDownItems.Add(barItem);
        modeMenu.DropDownOpening += (_, _) =>
        {
            notchItem.Checked = _settings.DisplayMode == "notch";
            islandItem.Checked = _settings.DisplayMode == "island";
            barItem.Checked = _settings.DisplayMode == "bar";
        };
        menu.Items.Add(modeMenu);
        menu.Items.Add(L("tray.recenterNotch"), null, (_, _) => Dispatcher.Invoke(() => _notch?.Recenter()));
        menu.Items.Add("Thread baru", null, (_, _) => Dispatcher.BeginInvoke(
            () => _ = RunUiTaskAsync(CreateThreadFromSidebarAsync)));
        menu.Items.Add(L("tray.settings"), null, (_, _) => Dispatcher.Invoke(() => OpenSettingsWindow()));
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
                ? LF("attention.needs", attention, active)
                : errors > 0
                    ? LF("attention.errors", errors)
                    : active > 0
                        ? LF("attention.active", active)
                        : L("attention.none");
            _attentionMenuItem.Enabled = active + attention + errors > 0;
        }

        if (!hadAttention && status.ActiveFlags.Count > 0 &&
            !string.Equals(status.ThreadId, _controller.ThreadId, StringComparison.Ordinal))
        {
            var message = status.ActiveFlags.Contains("waitingOnApproval")
                ? L("notification.attentionApproval")
                : L("notification.attentionAnswer");
            _trayIcon.ShowBalloonTip(4000, L("notification.attentionTitle"),
                message, Forms.ToolTipIcon.Warning);
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
        if (modifiers == ModifierKeys.Control && e.Key == Key.R)
        {
            e.Handled = true;
            Regenerate();
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.Add or Key.OemPlus)
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
            Title = L("dialog.chooseWorkspace"),
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
        ContextUsageButton.ToolTip = LF("context.tooltip", usage.TotalTokens, contextWindow);
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
                L("notification.contextTitle"),
                L("notification.contextBody"),
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
            SetStatus(L("status.compactBusy"), StatusBusyBrush);
            return;
        }

        if (_controller.ThreadId is null)
        {
            SetStatus(L("status.compactEmpty"), StatusBusyBrush);
            return;
        }

        try
        {
            SetStatus(L("status.compacting"), StatusBusyBrush);
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
            throw new InvalidOperationException(L("busy.switchThread"));
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
            throw new InvalidOperationException(L("busy.newThread"));
        }

        await OpenWorkspaceAsync(_settings.WorkspacePath, newThread: true);
        _messages.Clear();
        _streamingAgent = null;
        RenderConversation();
        Activate();
        PromptBox.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => SetSettingsVisible(false);

    private void SetSettingsVisible(bool visible)
    {
        if (visible)
        {
            HotkeyTextBox.Text = _hotKey.CurrentGesture ?? _settings.Hotkey;
            ApplyHotkeyButton.IsEnabled = false;
            HotkeyStatusText.Text = _hotKey.CurrentGesture is { } active
                ? LF("hotkey.activeHint", active)
                : L("hotkey.inactiveHint");
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
            SetStatus(L("status.exportEmpty"), StatusBusyBrush);
            return;
        }

        var workspaceName = string.IsNullOrWhiteSpace(_settings.WorkspacePath)
            ? "workspace"
            : new DirectoryInfo(_settings.WorkspacePath).Name;
        var safeName = string.Concat(workspaceName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L("dialog.exportTitle"),
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
            SetStatus(L("status.checkpointSaved"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(LF("status.checkpointFailed", exception.Message));
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = DiagnosticsLog.LogDirectory
                ?? Path.Combine(_settingsStore.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError(LF("status.openLogsFailed", exception.Message));
        }
    }

    private async void ApplyQuickHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _quickCaptureHotKey.ChangeGesture(QuickCaptureHotkeyBox.Text);
            _settings.QuickCaptureHotkey = _quickCaptureHotKey.CurrentGesture ?? _settings.QuickCaptureHotkey;
            QuickCaptureHotkeyBox.Text = _settings.QuickCaptureHotkey;
            await SaveSettingsSafeAsync();
            SetStatus(LF("hotkey.active", _settings.QuickCaptureHotkey), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void RegenerateButton_Click(object sender, RoutedEventArgs e) => Regenerate();

    private void Regenerate()
    {
        if (_controller.IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastPrompt))
        {
            SetStatus(L("response.nothingToRegenerate"), StatusBusyBrush);
            return;
        }

        PromptBox.Text = _lastPrompt;
        _ = SendPromptAsync();
    }

    private void CopyLastResponseButton_Click(object sender, RoutedEventArgs e)
    {
        var last = _messages.LastOrDefault(message => !message.IsUser);
        if (last is null || string.IsNullOrWhiteSpace(last.Markdown))
        {
            SetStatus(L("response.noneToCopy"), StatusBusyBrush);
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(last.Markdown);
            SetStatus(L("response.copied"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(LF("status.clipboardUnavailable", exception.Message));
        }
    }

    private void EditLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastPrompt))
        {
            SetStatus(L("response.nothingToRegenerate"), StatusBusyBrush);
            return;
        }

        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SetSettingsVisible(false);
        }

        PromptBox.Text = _lastPrompt;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.Focus();
    }

    private void OpenInEditorButton_Click(object sender, RoutedEventArgs e)
    {
        var last = _messages.LastOrDefault(message => !message.IsUser);
        if (last is null || string.IsNullOrWhiteSpace(last.Markdown))
        {
            SetStatus(L("response.noneToCopy"), StatusBusyBrush);
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"codexbar-response-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, last.Markdown);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void ReportBugButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            var info = string.Join(Environment.NewLine,
                $"CodexBar {version}",
                $"OS: {Environment.OSVersion}",
                $"Provider: {_settings.Provider}",
                $"DiagnosticsLogging: {_settings.EnableDiagnosticsLogging}",
                $"Language: {_settings.Language}",
                $"Time: {DateTimeOffset.Now:O}");
            var zipPath = CodexBar.Core.BugReport.Create(_settingsStore.BaseDirectory, info);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
            SetStatus(
                _settings.EnableDiagnosticsLogging ? L("bugReport.created") : L("bugReport.createdNoLogs"),
                StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(LF("bugReport.failed", exception.Message));
        }
    }

    private async void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "codex";
        ApiKeyPanel.Visibility = IsApiKeyProvider(providerId) ? Visibility.Visible : Visibility.Collapsed;

        if (providerId == _settings.Provider)
        {
            return;
        }

        var previousProviderId = _settings.Provider;
        ProviderCombo.IsEnabled = false;
        SetStatus(L("provider.switch.switching"), StatusBusyBrush);
        try
        {
            var replacement = _providerFactory(providerId);
            await _controller.SwitchProviderAsync(replacement);

            _settings.Provider = providerId;
            ActiveProviderChanged?.Invoke(replacement);
            _modelsReady = false;
            ModelCombo.ItemsSource = null;
            ModelRow.Visibility = Visibility.Collapsed;
            _threadStatuses.Clear();
            _messages.Clear();
            _streamingAgent = null;
            _historyLoadedThreadId = null;
            RenderConversation();
            await SaveSettingsSafeAsync();
            await StartupAsync();
            if (_controller.ThreadId is not null)
            {
                SetStatus(LF("provider.switch.ready", replacement.DisplayName), StatusReadyBrush);
            }
        }
        catch (Exception exception)
        {
            _settingsReady = false;
            SelectProviderComboItem(previousProviderId);
            ApiKeyPanel.Visibility = IsApiKeyProvider(previousProviderId) ? Visibility.Visible : Visibility.Collapsed;
            _settingsReady = true;
            ShowError(LF("provider.switch.failed", exception.Message));
        }
        finally
        {
            ProviderCombo.IsEnabled = true;
        }
    }

    private static readonly string[] ApiKeyProviderIds = ["anthropic", "openai", "openrouter"];

    private static bool IsApiKeyProvider(string providerId) => ApiKeyProviderIds.Contains(providerId);

    private void SelectProviderComboItem(string providerId)
    {
        var item = ProviderCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == providerId);
        ProviderCombo.SelectedItem = item ?? ProviderCombo.Items.OfType<ComboBoxItem>().First();
    }

    private void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "codex";
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyStatusText.Text = L("provider.apiKey.empty");
            return;
        }

        if (CodexBar.Core.CredentialStore.Save(App.ApiKeyCredentialTarget(providerId), key.Trim()))
        {
            ApiKeyBox.Clear();
            ApiKeyStatusText.Text = L("provider.apiKey.saved");
        }
        else
        {
            ApiKeyStatusText.Text = L("provider.apiKey.saveFailed");
        }
    }

    private void ClearApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "codex";
        CodexBar.Core.CredentialStore.Delete(App.ApiKeyCredentialTarget(providerId));
        ApiKeyBox.Clear();
        ApiKeyStatusText.Text = L("provider.apiKey.cleared");
    }

    private void TemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        if (_settings.PromptTemplates.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = CodexBar.Core.Localization.Instance["templates.none"],
                IsEnabled = false
            });
        }
        else
        {
            foreach (var template in _settings.PromptTemplates)
            {
                var preview = template.Content.Length <= 80
                    ? template.Content
                    : template.Content[..80] + "…";
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = template.Name,
                    ToolTip = preview.Replace('\n', ' ').Trim()
                };
                var captured = template;
                item.Click += (_, _) => InsertTemplate(captured);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new System.Windows.Controls.Separator());
        var save = new System.Windows.Controls.MenuItem { Header = CodexBar.Core.Localization.Instance["templates.saveCurrent"] };
        save.Click += async (_, _) => await SaveCurrentPromptAsTemplateAsync();
        menu.Items.Add(save);

        menu.PlacementTarget = TemplatesButton;
        menu.IsOpen = true;
    }

    private void InsertTemplate(PromptTemplate template)
    {
        var selection = PromptBox.SelectedText;
        var clipboard = string.Empty;
        if (template.Content.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                clipboard = System.Windows.Clipboard.ContainsText()
                    ? System.Windows.Clipboard.GetText()
                    : string.Empty;
            }
            catch
            {
            }
        }

        PromptBox.Text = PromptTemplate.Expand(template.Content, selection, clipboard);
        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.Focus();
    }

    private async Task SaveCurrentPromptAsTemplateAsync()
    {
        var content = PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            SetStatus(L("status.templateEmpty"), StatusBusyBrush);
            return;
        }

        var firstLine = content.Split('\n')[0].Trim();
        var name = firstLine.Length <= 40 ? firstLine : firstLine[..40];

        // New entry first so Normalize's first-wins de-dup lets it replace a same-named one.
        var updated = new List<PromptTemplate> { new() { Name = name, Content = content } };
        updated.AddRange(_settings.PromptTemplates);
        _settings.PromptTemplates = PromptTemplate.Normalize(updated);

        await _settingsStore.SaveAsync(_settings);
        SetStatus(LF("status.templateSaved", name), StatusReadyBrush);
    }

    private async void ManageTemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TemplateManagerDialog(_settings.PromptTemplates) { Owner = this };
        _dialogVisible = true;
        try
        {
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _settings.PromptTemplates = PromptTemplate.Normalize(dialog.Templates);
            await _settingsStore.SaveAsync(_settings);
            SetStatus(L("templates.updated"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _dialogVisible = false;
        }
    }

    private async void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L("settings.exportTitle"),
            FileName = $"CodexBar-settings-{DateTime.Now:yyyyMMdd}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _settingsStore.ExportAsync(_settings, dialog.FileName);
            SetStatus(L("settings.exported"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(LF("settings.exportFailed", exception.Message));
        }
    }

    private async void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("settings.importTitle"),
            CheckFileExists = true,
            Multiselect = false,
            Filter = "JSON|*.json|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var previousHotkey = _hotKey.CurrentGesture;
        try
        {
            var imported = await _settingsStore.ImportAsync(dialog.FileName);
            var activeWorkspace = _settings.WorkspacePath;
            var activeThreads = new Dictionary<string, string>(_settings.ThreadIds, StringComparer.OrdinalIgnoreCase);

            if (!string.Equals(imported.Hotkey, previousHotkey, StringComparison.OrdinalIgnoreCase))
            {
                _hotKey.ChangeGesture(imported.Hotkey);
                imported.Hotkey = _hotKey.CurrentGesture ?? imported.Hotkey;
            }

            if (_controller.ThreadId is not null &&
                (!string.Equals(imported.SandboxMode, _settings.SandboxMode, StringComparison.Ordinal) ||
                 !string.Equals(imported.ApprovalPolicy, _settings.ApprovalPolicy, StringComparison.Ordinal)))
            {
                await _controller.ChangePermissionsAsync(imported.SandboxMode, imported.ApprovalPolicy);
            }

            _settings.ApplyFrom(imported);
            _settings.WorkspacePath = activeWorkspace;
            _settings.ThreadIds = activeThreads;
            await _settingsStore.SaveAsync(_settings);
            CodexBar.Core.Localization.Instance.SetLanguage(CodexBar.Core.Localization.Parse(_settings.Language));
            DiagnosticsLog.Configure(_settings.EnableDiagnosticsLogging, _settingsStore.BaseDirectory);
            ConfigureFromSettings();
            SetStatus(L("settings.imported"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            if (previousHotkey is not null &&
                !string.Equals(_hotKey.CurrentGesture, previousHotkey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _hotKey.ChangeGesture(previousHotkey);
                }
                catch
                {
                }
            }
            ShowError(LF("settings.importFailed", exception.Message));
        }
    }

    private void Localization_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyLanguage();

    /// <summary>Apply the current UI language to controls whose text is localized (grows per i18n phase).</summary>
    private void ApplyLanguage()
    {
        var loc = CodexBar.Core.Localization.Instance;
        PromptPlaceholder.Text = loc["prompt.placeholder"];
        LanguageLabel.Text = loc["settings.language"];

        // Input bar tooltips.
        MenuButton.ToolTip = loc["bar.menu"];
        TemplatesButton.ToolTip = loc["templates.tooltip"];
        WorkspaceButton.ToolTip = loc["bar.workspace"];
        AttachButton.ToolTip = loc["bar.attach"];
        SendButton.ToolTip = loc["bar.send"];
        StopButton.ToolTip = loc["bar.stop"];
        ContextUsageButton.ToolTip = loc["bar.contextUsage"];
        ConversationButton.ToolTip = loc["bar.conversation"];
        SettingsButton.ToolTip = loc["bar.settings"];

        // Settings toggles.
        OutputDockedCheck.Content = loc["settings.outputDocked"];
        MenuDockedCheck.Content = loc["settings.menuDocked"];
        WordWrapCheckBox.Content = loc["settings.wrapCode"];
        ShowReasoningCheck.Content = loc["settings.showThinking"];
        PinCheckBox.Content = loc["settings.pin"];
        DiagnosticsLoggingCheck.Content = loc["settings.diagnosticsLog"];

        // Settings action buttons.
        OutputButton.Content = loc["settings.showOutput"];
        CompactButton.Content = loc["settings.compact"];
        RegenerateButton.Content = loc["response.regenerate"];
        CopyLastResponseButton.Content = loc["response.copy"];
        EditLastButton.Content = loc["response.editLast"];
        OpenInEditorButton.Content = loc["response.openInEditor"];
        AutoCopyResponseCheck.Content = loc["settings.autoCopy"];
        LaunchAtLoginCheck.Content = loc["settings.launchAtLogin"];

        // Controls that were previously left as static XAML text (so they didn't switch language).
        TopmostToggle.Content = loc["settings.alwaysOnTop"];
        ApplyHotkeyButton.Content = loc["hotkey.apply"];
        QuickCaptureRevealLabel.Text = loc["quickCapture.label"];
        QuickCaptureHotkeyLabel.Text = loc["quickCapture.hotkeyLabel"];
        if (QuickCaptureRevealCombo.Items.Count >= 3)
        {
            ((System.Windows.Controls.ComboBoxItem)QuickCaptureRevealCombo.Items[0]).Content = loc["quickCapture.revealCapture"];
            ((System.Windows.Controls.ComboBoxItem)QuickCaptureRevealCombo.Items[1]).Content = loc["quickCapture.revealDone"];
            ((System.Windows.Controls.ComboBoxItem)QuickCaptureRevealCombo.Items[2]).Content = loc["quickCapture.revealClipboard"];
        }
        ModelCombo.ToolTip = loc["settings.modelTooltip"];
        if (OutputPositionCombo.Items.Count >= 2)
        {
            ((System.Windows.Controls.ComboBoxItem)OutputPositionCombo.Items[0]).Content = loc["output.below"];
            ((System.Windows.Controls.ComboBoxItem)OutputPositionCombo.Items[1]).Content = loc["output.above"];
        }
        // Re-localize dynamic status labels so they follow a language switch too.
        SandboxStatusText.Text = SandboxDescription(_settings.SandboxMode);
        HotkeyStatusText.Text = LF("hotkey.active", _settings.Hotkey);
        CopyConversationButton.Content = loc["settings.copyConversation"];
        ManageTemplatesButton.Content = loc["templates.manage"];
        ScheduleButton.Content = loc["schedule.button"];
        ExportSettingsButton.Content = loc["settings.export"];
        ImportSettingsButton.Content = loc["settings.import"];
        ExportCheckpointButton.Content = loc["settings.exportCheckpoint"];
        OpenLogsButton.Content = loc["settings.openLogs"];
        ReportBugButton.Content = loc["settings.reportBug"];
        CancelAutoResumeButton.Content = loc["autoResume.cancel"];
        ResetLayoutButton.Content = loc["settings.resetLayout"];
        CloseSettingsButton.Content = loc["settings.close"];

        // Settings appearance labels.
        AppearanceHeader.Text = loc["settings.appearance"];
        WidthLabel.Text = loc["settings.width"];
        OutputHeightLabel.Text = loc["settings.outputHeight"];
        InputFontLabel.Text = loc["settings.inputFont"];
        ResponseFontLabel.Text = loc["settings.responseFont"];

        OnboardingCopyButton.Content = loc["onboard.copy"];
        OnboardingDocsButton.Content = loc["onboard.docs"];

        // Screen-reader labels follow the selected UI language as well.
        System.Windows.Automation.AutomationProperties.SetName(MenuButton, loc["automation.main.menu"]);
        System.Windows.Automation.AutomationProperties.SetName(TemplatesButton, loc["automation.main.templates"]);
        System.Windows.Automation.AutomationProperties.SetName(WorkspaceButton, loc["automation.main.workspace"]);
        System.Windows.Automation.AutomationProperties.SetName(PromptBox, loc["automation.main.prompt"]);
        System.Windows.Automation.AutomationProperties.SetName(AttachButton, loc["automation.main.attach"]);
        System.Windows.Automation.AutomationProperties.SetName(SendButton, loc["automation.main.send"]);
        System.Windows.Automation.AutomationProperties.SetName(StopButton, loc["automation.main.stop"]);
        System.Windows.Automation.AutomationProperties.SetName(ContextUsageButton, loc["automation.main.context"]);
        System.Windows.Automation.AutomationProperties.SetName(ConversationButton, loc["automation.main.conversation"]);
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, loc["automation.main.settings"]);
        System.Windows.Automation.AutomationProperties.SetName(HotkeyTextBox, loc["automation.main.hotkey"]);
        System.Windows.Automation.AutomationProperties.SetName(SandboxCombo, loc["automation.main.permission"]);
        System.Windows.Automation.AutomationProperties.SetName(LanguageCombo, loc["automation.main.language"]);
        System.Windows.Automation.AutomationProperties.SetName(OutputPositionCombo, loc["automation.main.outputPosition"]);
        System.Windows.Automation.AutomationProperties.SetName(ApprovalCombo, loc["automation.main.approvalPolicy"]);
        System.Windows.Automation.AutomationProperties.SetName(EffortCombo, loc["automation.main.reasoningEffort"]);
        System.Windows.Automation.AutomationProperties.SetName(OpenLogsButton, loc["automation.main.openLogs"]);

        // Disable (not hide) controls the active provider doesn't support, so the layout
        // never shifts. This is a no-op for Codex (supports everything); it only kicks in
        // for reduced-capability providers like Claude.
        if (!_controller.Capabilities.SupportsWorkspace)
        {
            WorkspaceButton.IsEnabled = false;
            WorkspaceButton.ToolTip = loc["provider.capability.noWorkspace"];
        }

        if (!_controller.Capabilities.SupportsApprovals)
        {
            var note = loc["provider.capability.noApprovals"];
            SandboxCombo.IsEnabled = false;
            SandboxCombo.ToolTip = note;
            ApprovalCombo.IsEnabled = false;
            ApprovalCombo.ToolTip = note;
        }

        // Provider / API key section.
        ProviderHeader.Text = loc["provider.section.title"];
        ProviderLabel.Text = loc["provider.section.label"];
        ProviderRestartNote.Text = loc["provider.section.restartNote"];
        ApiKeyLabel.Text = loc["provider.apiKey.label"];
        SaveApiKeyButton.Content = loc["provider.apiKey.save"];
        ClearApiKeyButton.Content = loc["provider.apiKey.clear"];
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
            SetStatus(L("status.conversationCopied"), StatusReadyBrush);
        }
        catch (Exception exception)
        {
            ShowError(LF("status.clipboardUnavailable", exception.Message));
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
            ShowError(LF("status.openCodexFailed", exception.Message));
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
        _notch?.Close();
        if (_outputWindow is not null)
        {
            _outputWindow.PrepareForExit();
            _outputWindow.Close();
        }
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _hotKey.Dispose();
        _quickCaptureHotKey.Dispose();
        _approvalGate.Dispose();
        CleanupTemporaryAttachments(_temporaryAttachmentPaths.ToArray());
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void ShowError(string message)
    {
        SetStatus(L("status.error"), StatusErrorBrush);
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
