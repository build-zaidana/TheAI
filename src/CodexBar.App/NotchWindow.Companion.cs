using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexBar.App;

// The idle companion: two small eyes in the compact notch while nothing else is shown there. They
// blink every few seconds and close after five minutes without keyboard or mouse input, opening
// again when the user is back. Both timers run only while the eyes are on screen; a blink is a
// 160 ms animation, so the notch stays still between them.
public partial class NotchWindow
{
    private static readonly TimeSpan DozeAfter = TimeSpan.FromMinutes(5);

    private readonly DispatcherTimer _blinkTimer = new();
    private readonly DispatcherTimer _dozeCheck = new();
    private readonly Random _blinkRandom = new();
    private bool _companionWired;
    private bool _dozing;
    private bool _companionPreview;

    /// <summary>Show the eyes when the setting is on and the compact notch has nothing else to show.</summary>
    private void ApplyCompanion()
    {
        if (_companionPreview)
        {
            return;
        }

        var show = _host.CompanionEyes && _state == NotchState.Idle && !MusicShowing && !_fullscreenHidden &&
                   CompactIndicator.Children.OfType<UIElement>().All(child => child == CompanionEyes || child.Visibility != Visibility.Visible) &&
                   CompactJobText.Visibility != Visibility.Visible;
        CompanionEyes.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            _blinkTimer.Stop();
            _dozeCheck.Stop();
            return;
        }

        if (!_companionWired)
        {
            _companionWired = true;
            _blinkTimer.Tick += (_, _) => Blink();
            _dozeCheck.Tick += (_, _) => CheckDoze();
            Closed += (_, _) =>
            {
                _blinkTimer.Stop();
                _dozeCheck.Stop();
            };
        }

        if (!_dozeCheck.IsEnabled)
        {
            _dozing = !_dozing;   // forces CheckDoze to set the lids and the blink timer
            CheckDoze();
        }
    }

    private void CheckDoze()
    {
        var dozing = IdleFor() >= DozeAfter;
        // Asleep: look often for the user's return (a cheap call, nothing redrawn). Awake: rarely.
        _dozeCheck.Interval = dozing ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(30);
        _dozeCheck.Start();
        if (dozing == _dozing)
        {
            return;
        }

        _dozing = dozing;
        CompanionLids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(dozing ? 0.18 : 1, TimeSpan.FromMilliseconds(dozing ? 900 : 200)));
        if (dozing)
        {
            _blinkTimer.Stop();
        }
        else
        {
            ScheduleBlink();
        }
    }

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + (_blinkRandom.NextDouble() * 4));
        _blinkTimer.Start();
    }

    private void Blink()
    {
        _blinkTimer.Stop();
        if (_dozing || CompanionEyes.Visibility != Visibility.Visible)
        {
            return;
        }

        CompanionLids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(80)) { AutoReverse = true });
        ScheduleBlink();
    }

    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    internal async void PreviewCompanion(string mode)
    {
        await Task.Delay(1500);
        _companionPreview = true;
        _blinkTimer.Stop();
        _dozeCheck.Stop();
        CompanionEyes.Visibility = Visibility.Visible;
        CompanionLids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        CompanionLids.ScaleY = mode == "asleep" ? 0.18 : 1;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(800);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-eyes-{mode}.png"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
