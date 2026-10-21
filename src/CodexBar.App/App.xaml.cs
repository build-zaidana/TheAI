using System.Windows;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Providers.Anthropic;
using CodexBar.Providers.ClaudeCode;
using CodexBar.Providers.Codex;
using CodexBar.Providers.OpenAI;
using CodexBar.Providers.OpenRouter;

namespace CodexBar.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CodexBar.SingleInstance";
    private const string ActivationEventName = "Local\\CodexBar.Activate";
    private static readonly System.Net.Http.HttpClient SharedHttp = new();
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private IAgentProvider? _provider;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationLoop;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Best-effort crash capture. No-op until diagnostics logging is enabled from
        // settings below; we never mark the exception handled, so behavior is unchanged.
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Error("UnhandledDispatcher", args.Exception.Message, args.Exception);
            CodexBar.App.MainWindow.RememberCrash(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DiagnosticsLog.Error("UnhandledDomain",
                (args.ExceptionObject as Exception)?.Message ?? "unknown",
                args.ExceptionObject as Exception);
            if (args.ExceptionObject is Exception exception)
            {
                CodexBar.App.MainWindow.RememberCrash(exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticsLog.Error("UnobservedTask", args.Exception.Message, args.Exception);

        // "TheAI.exe activity build --progress 0.4": a live activity for the running notch, then exit.
        if (e.Args.Length > 0 && e.Args[0].Equals("activity", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(SendActivity(e.Args.Skip(1).ToArray()));
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                System.Windows.MessageBox.Show(
                    "TheAI sedang berjalan, tetapi tidak dapat ditampilkan. Gunakan hotkey yang dipilih atau menu tray TheAI.",
                    "TheAI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        try
        {
            _activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivationEventName);
            _activationCancellation = new CancellationTokenSource();

            // CodexBar became TheAI: its data folder, saved keys and sign-in entry move over once.
            var migrated = CodexBar.Providers.AppPaths.MigrateLegacyData();
            MigrateLegacyCredentials();
            var settingsStore = new JsonSettingsStore();
            var firstStart = !System.IO.File.Exists(settingsStore.SettingsPath);
            var settings = await settingsStore.LoadAsync();
            if (firstStart)
            {
                // A new user starts with the agent they already have, not an onboarding for another one.
                settings.Provider = InstalledAgent() ?? settings.Provider;
            }
            if (settings.UpdateRepository is "build-zaidana/CodexBar" or "build-zaidana/TheAI")
            {
                settings.UpdateRepository = AppSettings.ReleaseRepository;
            }

            CodexBar.Core.Localization.Instance.SetLanguage(CodexBar.Core.Localization.Parse(settings.Language));
            DiagnosticsLog.Configure(settings.EnableDiagnosticsLogging, settingsStore.BaseDirectory);
            DiagnosticsLog.Info("App",
                $"TheAI {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"} dimulai.");
            if (migrated)
            {
                DiagnosticsLog.Info("App", "Data CodexBar dipindahkan ke " + CodexBar.Providers.AppPaths.DataRoot);
            }
            if (settings.LaunchAtLogin)
            {
                // Refresh the Run-key path in case the executable was moved since last run.
                StartupRegistration.Apply(true);
            }

            _provider = CreateProvider(settings.Provider);

            var window = new MainWindow(settings, settingsStore, _provider, CreateProvider);
            window.ActiveProviderChanged += provider => _provider = provider;
            MainWindow = window;
            window.Show();
            _activationLoop = Task.Run(() => WaitForActivation(window, _activationCancellation.Token));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Startup", "TheAI gagal dimulai.", exception);
            System.Windows.MessageBox.Show(
                $"TheAI gagal dimulai.\n\n{exception.Message}",
                "TheAI",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>The CLI agent installed on this PC: Codex first (the original default), else Claude Code.</summary>
    private static string? InstalledAgent()
    {
        try
        {
            CodexExecutableLocator.Locate();
            return "codex";
        }
        catch (Exception)
        {
        }

        return ClaudeCodeExecutableLocator.TryLocate() is not null ? "claude-code" : null;
    }

    private static void MigrateLegacyCredentials()
    {
        foreach (var target in new[] { "apiKey/anthropic", "apiKey/openai", "apiKey/openrouter", "CalendarIcs" })
        {
            if (CodexBar.Core.CredentialStore.TryRead("CodexBar/" + target) is { } secret &&
                CodexBar.Core.CredentialStore.TryRead("TheAI/" + target) is null &&
                CodexBar.Core.CredentialStore.Save("TheAI/" + target, secret))
            {
                CodexBar.Core.CredentialStore.Delete("CodexBar/" + target);
            }
        }
    }

    private static int SendActivity(string[] args)
    {
        var (update, error) = CodexBar.Core.LiveJobUpdate.FromArgs(args);
        error ??= update is null ? null : Hud.LiveJobPipe.Send(update);
        if (error is null)
        {
            return 0;
        }

        // A GUI program has no console of its own: borrow the terminal it was started from.
        if (AttachConsole(-1))
        {
            using var stderr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            stderr.WriteLine();
            stderr.WriteLine("theai: " + error);
        }

        return 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>Credential Manager target name for a provider's API key.</summary>
    public static string ApiKeyCredentialTarget(string providerId) => $"TheAI/apiKey/{providerId}";

    // Selects the provider implementation from settings. Codex is the default. Each API-key
    // provider reads its key from Windows Credential Manager (set via Settings), falling back
    // to a well-known environment variable for advanced/dev use.
    internal static IAgentProvider CreateProvider(string providerId)
    {
        Func<string, string> localize = key => CodexBar.Core.Localization.Instance[key];
        return providerId switch
        {
            "claude-code" => new ClaudeCodeProvider(localize),
            "anthropic" => new AnthropicProvider(
                SharedHttp,
                () => CodexBar.Core.CredentialStore.TryRead(ApiKeyCredentialTarget("anthropic"))
                    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                localize),
            "openai" => new OpenAIProvider(
                SharedHttp,
                () => CodexBar.Core.CredentialStore.TryRead(ApiKeyCredentialTarget("openai"))
                    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                localize),
            "openrouter" => new OpenRouterProvider(
                SharedHttp,
                () => CodexBar.Core.CredentialStore.TryRead(ApiKeyCredentialTarget("openrouter"))
                    ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                localize),
            _ => new CodexProvider(localize)
        };
    }

    private void WaitForActivation(MainWindow window, CancellationToken cancellationToken)
    {
        if (_activationEvent is null)
        {
            return;
        }

        var handles = new[] { _activationEvent, cancellationToken.WaitHandle };
        while (!cancellationToken.IsCancellationRequested && WaitHandle.WaitAny(handles) == 0)
        {
            Dispatcher.BeginInvoke(window.ShowFromExternalActivation);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticsLog.Info("App", "TheAI keluar.");
        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        try
        {
            _activationLoop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
        }

        if (_provider is not null)
        {
            // Jalankan di thread pool agar tidak deadlock: OnExit berada di UI thread,
            // dan DisposeAsync menunggu read loop yang continuation-nya bisa tertangkap
            // oleh SynchronizationContext WPF. Tanpa ini, child process Codex tidak
            // pernah ter-kill dan TheAI.exe menggantung.
            var provider = _provider;
            var disposeTask = Task.Run(async () => await provider.DisposeAsync().ConfigureAwait(false));

            // Beri batas waktu: child process Codex sudah dibunuh di awal DisposeAsync,
            // jadi kalaupun pembersihan sisa (read loop) lambat, jangan sampai menahan
            // exit. Task berjalan di background thread sehingga tidak menahan proses.
            disposeTask.Wait(TimeSpan.FromSeconds(5));
        }

        if (_singleInstanceMutex is not null && _ownsMutex)
        {
            _singleInstanceMutex.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _activationEvent?.Dispose();
        _activationCancellation?.Dispose();

        base.OnExit(e);
    }
}
