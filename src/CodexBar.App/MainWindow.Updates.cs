using System.Windows.Threading;
using CodexBar.Core;

namespace CodexBar.App;

// Update check: a little after start and then daily, the public release repository is asked for
// a newer version. A newer one shows a toast (Download / Skip this version) and a tray line;
// downloading opens the release page in the browser, nothing installs on its own.
public partial class MainWindow
{
    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _availableUpdate;

    private static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_settings.CheckForUpdates)
            {
                await CheckForUpdateAsync(manual: false);
            }
        };
        _updateTimer.Start();
    }

    /// <summary>Asks for the latest release; returns a line for the settings window.</summary>
    private async Task<string> CheckForUpdateAsync(bool manual)
    {
        if (!UpdateChecker.IsRepository(_settings.UpdateRepository))
        {
            return Tr.Text("Isi repositori rilis dulu (pemilik/nama).", "Set the release repository first (owner/name).");
        }

        try
        {
            var update = await UpdateChecker.CheckAsync(_settings.UpdateRepository, CurrentVersion);
            _availableUpdate = update;
            if (update is null)
            {
                return string.Format(Tr.Text("CodexBar {0} sudah yang terbaru.", "CodexBar {0} is up to date."), CurrentVersion.ToString(3));
            }

            if (manual || update.Tag != _settings.SkippedUpdate)
            {
                _notifier.Show(
                    string.Format(Tr.Text("CodexBar {0} tersedia", "CodexBar {0} is available"), update.Version.ToString(3)),
                    CompletionNotifier.Preview(update.Notes.Length > 0 ? update.Notes : Tr.Text("Versi baru sudah dirilis.", "A new version is out.")),
                    "update",
                    withSound: false,
                    [(Tr.Text("Unduh", "Download"), "update-download"), (Tr.Text("Lewati versi ini", "Skip this version"), "update-skip")],
                    "update");
            }

            return string.Format(Tr.Text("Versi {0} tersedia.", "Version {0} is available."), update.Version.ToString(3));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Update", exception.Message);
            return string.Format(Tr.Text("Gagal mengecek: {0}", "Couldn't check: {0}"), exception.Message);
        }
    }

    // Opens the release download (the installer when the release has one) in the browser.
    private void OpenUpdateDownload()
    {
        if (_availableUpdate is not { } update)
        {
            return;
        }

        var url = update.DownloadUrl ?? update.PageUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void SkipUpdate()
    {
        if (_availableUpdate is { } update)
        {
            _settings.SkippedUpdate = update.Tag;
            _availableUpdate = null;
            _ = SaveSettingsSafeAsync();
        }
    }

    Task<string> ISettingsHost.CheckForUpdateAsync() => CheckForUpdateAsync(manual: true);
}
