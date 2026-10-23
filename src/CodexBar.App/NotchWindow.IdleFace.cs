using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace CodexBar.App;

// The closed notch at rest: the time beside the chat button and, on the right, one glance (the
// temperature or the next prayer time). One timer that wakes on the minute, only while the face
// shows, so a resting notch redraws once a minute and no more.
public partial class NotchWindow
{
    private const double IdleFaceWidth = 196;

    private readonly DispatcherTimer _idleClock = new();
    private bool _idleClockWired;

    private bool IdleFaceShowing => IdleFaceShowingFor(_view);

    private bool IdleFaceShowingFor(NotchView view) =>
        view == NotchView.Compact && _state == NotchState.Idle && !CompactLive && !Vertical && !IsCorner;

    internal async void PreviewIdleFace()
    {
        await Task.Delay(1500);
        _place = new CodexBar.Core.GeoPlace("Jakarta", "Indonesia", -6.2, 106.8, "Asia/Jakarta");
        _weather = new CodexBar.Core.WeatherReport(31.4, 2, true, 33.1, 25.0, 70);
        ApplyMusicCompact();
        ApplyView(animate: false);
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(800);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-idle.png"));
        }
    }

    private void ApplyIdleFace()
    {
        var show = IdleFaceShowing;
        CompactClock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            CompactGlance.Visibility = Visibility.Collapsed;
            _idleClock.Stop();
            return;
        }

        if (!_idleClockWired)
        {
            _idleClockWired = true;
            _idleClock.Tick += (_, _) => ApplyIdleFace();
            Closed += (_, _) => _idleClock.Stop();
        }

        var now = DateTime.Now;
        CompactClock.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var glance = _weather is { } weather && WeatherAvailable ? $"{Math.Round(weather.Temperature):0}°"
            : NextPrayer(DateTimeOffset.Now) is { } prayer ? $"{prayer.Name(Tr.Text)} {prayer.At:HH:mm}"
            : string.Empty;
        CompactGlance.Text = glance;
        // The companion's eyes take the right side when they are on.
        CompactGlance.Visibility = glance.Length > 0 && CompanionEyes.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        _idleClock.Interval = TimeSpan.FromSeconds(60 - now.Second) + TimeSpan.FromMilliseconds(200);
        _idleClock.Start();
    }
}
