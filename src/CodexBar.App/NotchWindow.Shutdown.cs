using System.Globalization;
using System.Windows;
using CodexBar.Core;

namespace CodexBar.App;

// Turning the PC off later: Windows' "shutdown /s /t X", set on the Timer page (the Matikan mode, or
// typed: "shutdown -s -t 3600", "shutdown 30m", "shutdown -a"). Windows closes open apps by force, so
// the page says so, and the notch opens by itself a minute before, with the way to call it off.
public partial class NotchWindow
{
    private static readonly TimeSpan ShutdownWarning = TimeSpan.FromMinutes(1);

    private SystemShutdown.Saved? _shutdown;
    private TimeSpan _shutdownLength = TimeSpan.FromMinutes(30);
    private bool _idleShutdown;
    private bool _shutdownWarned;
    private string? _shutdownProblem;

    private bool ShutdownShowing => _shutdown is not null;

    private void InitShutdown() => _shutdown = SystemShutdown.Load();

    private async Task StartShutdown(int seconds)
    {
        if (await SystemShutdown.ScheduleAsync(seconds))
        {
            var now = DateTimeOffset.Now;
            _shutdown = new SystemShutdown.Saved(now.AddSeconds(seconds), now);
            _shutdownProblem = null;
            _shutdownWarned = false;
            if (!_timerPreview)
            {
                SystemShutdown.Save(_shutdown);
            }
        }
        else
        {
            _shutdownProblem = Tr.Text("Windows menolak jadwalnya", "Windows refused the schedule");
        }

        TimerChanged();
    }

    private async Task CancelShutdown()
    {
        // Nothing pending at Windows' end is fine too: either way there's no shutdown left to show.
        await SystemShutdown.CancelAsync();
        _shutdown = null;
        _shutdownProblem = null;
        SystemShutdown.Save(null);
        TimerChanged();
    }

    private void ShutdownCancel_Click(object sender, RoutedEventArgs e) => _ = CancelShutdown();

    private void TickShutdown(DateTimeOffset now)
    {
        if (_shutdown is not { } shutdown)
        {
            return;
        }

        var left = shutdown.At - now;
        if (left <= TimeSpan.Zero)
        {
            // Still here after the moment passed: it was called off outside TheAI.
            _shutdown = null;
            SystemShutdown.Save(null);
            TimerChanged();
        }
        else if (left <= ShutdownWarning && !_shutdownWarned)
        {
            _shutdownWarned = true;
            OpenTimer(focus: false);
        }
    }

    private void RenderShutdown(DateTimeOffset now)
    {
        ShutdownRun.Visibility = ShutdownShowing ? Visibility.Visible : Visibility.Collapsed;
        if (_shutdown is not { } shutdown)
        {
            return;
        }

        var left = shutdown.At - now;
        var length = shutdown.At - shutdown.Started;
        ShutdownBig.Text = NotchTimer.Format(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        ShutdownMeta.Text = string.Format(CultureInfo.CurrentCulture, Tr.Text("Mati pukul {0}", "Off at {0}"), AlarmClock(shutdown.At));
        ShutdownRuler.Value = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        ShutdownFootStart.Text = AlarmClock(shutdown.Started);
        ShutdownFootGone.Text = NotchTimer.Format(length - left);
        ShutdownFootLength.Text = MinutesText(length);
    }

    private static string MinutesText(TimeSpan length) => length.TotalMinutes >= 120
        ? string.Format(CultureInfo.CurrentCulture, Tr.Text("{0:0.#} jam", "{0:0.#} h"), length.TotalHours)
        : string.Format(CultureInfo.CurrentCulture, Tr.Text("{0:0} mnt", "{0:0} min"), length.TotalMinutes);

    internal async void PreviewShutdown(string mode)
    {
        await Task.Delay(400);
        _timerPreview = true;
        if (mode == "run")
        {
            var now = DateTimeOffset.Now;
            _shutdown = new SystemShutdown.Saved(now.AddMinutes(18.7), now.AddMinutes(-11.3));
        }
        else
        {
            _idleShutdown = true;
        }

        OpenTimer(focus: false);
        _pinned = true;
        SnapshotLater("notch-shutdown-" + mode, TimeSpan.FromSeconds(2));
    }
}
