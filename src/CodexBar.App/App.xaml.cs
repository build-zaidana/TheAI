using System.Windows;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Providers.Anthropic;
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
            DiagnosticsLog.Error("UnhandledDispatcher", args.Exception.Message, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Error("UnhandledDomain",
                (args.ExceptionObject as Exception)?.Message ?? "unknown",
                args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticsLog.Error("UnobservedTask", args.Exception.Message, args.Exception);

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
                    "CodexBar sedang berjalan, tetapi tidak dapat ditampilkan. Gunakan hotkey yang dipilih atau menu tray CodexBar.",
                    "CodexBar",
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

            var settingsStore = new JsonSettingsStore();
            var settings = await settingsStore.LoadAsync();
            CodexBar.Core.Localization.Instance.SetLanguage(CodexBar.Core.Localization.Parse(settings.Language));
            DiagnosticsLog.Configure(settings.EnableDiagnosticsLogging, settingsStore.BaseDirectory);
            DiagnosticsLog.Info("App",
                $"CodexBar {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"} dimulai.");
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
            DiagnosticsLog.Error("Startup", "CodexBar gagal dimulai.", exception);
            System.Windows.MessageBox.Show(
                $"CodexBar gagal dimulai.\n\n{exception.Message}",
                "CodexBar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Credential Manager target name for a provider's API key.</summary>
    public static string ApiKeyCredentialTarget(string providerId) => $"CodexBar/apiKey/{providerId}";

    // Selects the provider implementation from settings. Codex is the default. Each API-key
    // provider reads its key from Windows Credential Manager (set via Settings), falling back
    // to a well-known environment variable for advanced/dev use.
    internal static IAgentProvider CreateProvider(string providerId)
    {
        Func<string, string> localize = key => CodexBar.Core.Localization.Instance[key];
        return providerId switch
        {
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
        DiagnosticsLog.Info("App", "CodexBar keluar.");
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
            // pernah ter-kill dan CodexBar.App.exe menggantung.
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
