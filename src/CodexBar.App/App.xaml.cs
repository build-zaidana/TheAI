using System.Windows;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Providers.Codex;

namespace CodexBar.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CodexBar.SingleInstance";
    private const string ActivationEventName = "Local\\CodexBar.Activate";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private IAgentProvider? _provider;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationLoop;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            _provider = new CodexProvider();

            var window = new MainWindow(settings, settingsStore, _provider);
            MainWindow = window;
            window.Show();
            _activationLoop = Task.Run(() => WaitForActivation(window, _activationCancellation.Token));
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"CodexBar gagal dimulai.\n\n{exception.Message}",
                "CodexBar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
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
