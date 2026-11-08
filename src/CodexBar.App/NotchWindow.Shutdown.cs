using System.Globalization;
using System.Windows;
using CodexBar.Core;

namespace CodexBar.App;

// Turning the PC off later: Windows' "shutdown /s /t X", set on the Timer page (the Matikan mode, by a
// length or a time of day, or typed: "shutdown -s -t 3600", "shutdown 30m", "shutdown 22:30", "shutdown -a"). Windows closes open apps by force, so
// the page says so, and the notch opens by itself a minute before, with the way to call it off.
public partial class NotchWindow
{
    private static readonly TimeSpan ShutdownWarning = TimeSpan.FromMinutes(1);

    private SystemShutdown.Saved? _shutdown;
    private TimeSpan _shutdownLength = TimeSpan.FromMinutes(30);
    private bool _idleShutdown;
    private bool _shutdownByClock;
    private TimeSpan _shutdownClock = TimeSpan.FromHours(22);
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

    private void ShutdownKind_Click(object sender, RoutedEventArgs e)
    {
        _shutdownByClock = !_shutdownByClock;
        if (_shutdownByClock)
        {
            // Starts an hour from now, on a round five minutes.
            var soon = DateTime.Now.AddHours(1);
            _shutdownClock = new TimeSpan(soon.Hour, soon.Minute / 5 * 5, 0);
        }

        RenderTimer();
    }

    private static string ClockText(TimeSpan time) => $"{(int)time.TotalHours}:{time.Minutes:00}";

    /// <summary>The label over the number: by the clock it says how long that is, and when it means tomorrow.</summary>
    private string ShutdownCaption(DateTimeOffset now)
    {
        if (!_shutdownByClock)
        {
            return Tr.Text("Matikan PC", "Shut down");
        }

        var seconds = ShutdownCommand.SecondsUntil(TimeOnly.FromTimeSpan(_shutdownClock), now);
        var left = TimeSpan.FromSeconds(seconds);
        var inTime = left.TotalHours >= 1
            ? string.Format(CultureInfo.CurrentCulture, Tr.Text("{0} j {1} mnt lagi", "in {0} h {1} min"), (int)left.TotalHours, left.Minutes)
            : string.Format(CultureInfo.CurrentCulture, Tr.Text("{0} mnt lagi", "in {0} min"), (int)Math.Ceiling(left.TotalMinutes));
        var tomorrow = now.AddSeconds(seconds).Date > now.Date ? Tr.Text("besok, ", "tomorrow, ") : string.Empty;
        return Tr.Text("Mati ", "Off ") + tomorrow + inTime;
    }

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
            if (mode == "clock")
            {
                _shutdownByClock = true;
                _shutdownClock = new TimeSpan(22, 30, 0);
            }
        }

        OpenTimer(focus: false);
        _pinned = true;
        SnapshotLater("notch-shutdown-" + mode, TimeSpan.FromSeconds(2));
    }
}
