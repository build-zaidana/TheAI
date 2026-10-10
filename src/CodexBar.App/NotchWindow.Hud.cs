using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.Hud;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Forms = System.Windows.Forms;

namespace CodexBar.App;

// The notch as the system's HUD, like the Dynamic Island: volume and brightness keys, the charger
// going in, the battery running low and headphones becoming the output each widen the compact
// notch for a moment — an icon, a bar or a number — then it settles back. Only over the compact
// notch: an open panel or the chat is never interrupted.
public partial class NotchWindow
{
    private const double HudWidth = 240;
    private static readonly TimeSpan HudShort = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan HudLong = TimeSpan.FromMilliseconds(2400);

    // 16-unit icons: speaker body (fill) and its waves / mute cross (stroke), the sun, a bolt, headphones.
    private static readonly Geometry SpeakerBody = Geometry.Parse("M 1,5.5 L 4.2,5.5 L 8.6,1.8 L 8.6,14.2 L 4.2,10.5 L 1,10.5 Z");
    private static readonly Geometry SpeakerWaves0 = Geometry.Empty;
    private static readonly Geometry SpeakerWaves1 = Geometry.Parse("M 11.5,5.2 A 3.6,3.6 0 0 1 11.5,10.8");
    private static readonly Geometry SpeakerWaves2 = Geometry.Parse("M 11.5,5.2 A 3.6,3.6 0 0 1 11.5,10.8 M 13.8,2.8 A 7,7 0 0 1 13.8,13.2");
    private static readonly Geometry SpeakerMute = Geometry.Parse("M 11.5,5.5 L 16.5,10.5 M 16.5,5.5 L 11.5,10.5");
    private static readonly Geometry SunBody = Geometry.Parse("M 9,5.6 A 3.4,3.4 0 1 0 9.001,5.6 Z");
    private static readonly Geometry SunRays = Geometry.Parse("M 9,1.2 L 9,3.2 M 9,14.8 L 9,16.8 M 1.2,9 L 3.2,9 M 14.8,9 L 16.8,9 M 3.5,3.5 L 4.9,4.9 M 13.1,13.1 L 14.5,14.5 M 3.5,14.5 L 4.9,13.1 M 13.1,4.9 L 14.5,3.5");
    private static readonly Geometry Bolt = Geometry.Parse("M 8,1 L 2,9 L 6.5,9 L 5.5,15 L 12,7 L 7.5,7 Z");
    private static readonly Geometry HeadphoneBand = Geometry.Parse("M 2,10 L 2,8 A 7,7 0 0 1 16,8 L 16,10");
    private static readonly Geometry HeadphoneCups = Geometry.Parse("M 1,10.5 Q 1,9 2.5,9 L 3.5,9 Q 5,9 5,10.5 L 5,13.5 Q 5,15 3.5,15 L 2.5,15 Q 1,15 1,13.5 Z M 13,10.5 Q 13,9 14.5,9 L 15.5,9 Q 17,9 17,10.5 L 17,13.5 Q 17,15 15.5,15 L 14.5,15 Q 13,15 13,13.5 Z");

    private readonly DispatcherTimer _hudTimer = new();
    private AudioWatcher? _audio;
    private BrightnessWatcher? _brightness;
    private int _lastBatteryWarning = 101;
    private bool _wasOnPower;

    private void InitHud()
    {
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            if (_view == NotchView.Hud)
            {
                Collapse();
            }
        };

        _audio = new AudioWatcher(Dispatcher);
        _audio.VolumeChanged += (level, muted) => ShowVolumeHud(level, muted);
        _audio.HeadphonesConnected += async name => ShowHeadphonesHud(name, await BluetoothBattery.ForOutputAsync(name));
        _audio.Start();

        _brightness = new BrightnessWatcher(Dispatcher);
        _brightness.Changed += level => ShowBrightnessHud(level);
        _brightness.Start();

        var power = Forms.SystemInformation.PowerStatus;
        _wasOnPower = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        SystemEvents.PowerModeChanged += PowerModeChanged;

        Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            _audio?.Dispose();
            _brightness?.Dispose();
        };
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var power = Forms.SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery))
            {
                return;   // a desktop: nothing to show
            }

            var percent = (int)Math.Round(power.BatteryLifePercent * 100);
            var onPower = power.PowerLineStatus == Forms.PowerLineStatus.Online;
            if (onPower && !_wasOnPower)
            {
                _lastBatteryWarning = 101;
                ShowChargingHud(percent);
            }
            else if (!onPower && percent <= 10 && WarningStep(percent) < _lastBatteryWarning)
            {
                // Once at 10 %, once more at 5 %.
                _lastBatteryWarning = WarningStep(percent);
                ShowLowBatteryHud(percent);
            }

            _wasOnPower = onPower;
        });
    }

    private static int WarningStep(int percent) => percent <= 5 ? 5 : 10;

    // ---- The HUD's faces ---------------------------------------------------------------------

    private void ShowVolumeHud(float level, bool muted)
    {
        var quiet = muted || level < 0.005f;
        HudIconFill.Data = SpeakerBody;
        HudIconFill.Fill = quiet ? TimerQuietBrush : TimerFocusBrush;
        HudIconStroke.Data = muted ? SpeakerMute : level < 0.005f ? SpeakerWaves0 : level < 0.5f ? SpeakerWaves1 : SpeakerWaves2;
        HudIconStroke.Stroke = quiet ? TimerQuietBrush : TimerFocusBrush;
        ShowBar(muted ? 0 : level);
        ShowHud(HudShort);
    }

    private void ShowBrightnessHud(int level)
    {
        HudIconFill.Data = SunBody;
        HudIconFill.Fill = TimerFocusBrush;
        HudIconStroke.Data = SunRays;
        HudIconStroke.Stroke = TimerFocusBrush;
        ShowBar(level / 100.0);
        ShowHud(HudShort);
    }

    private void ShowChargingHud(int percent)
    {
        HudIconFill.Data = Bolt;
        HudIconFill.Fill = TimerFocusBrush;
        HudIconStroke.Data = Geometry.Empty;
        ShowValue(percent, red: false);
        ShowHud(HudLong);
    }

    private void ShowLowBatteryHud(int percent)
    {
        HudIconFill.Data = Geometry.Empty;
        HudIconStroke.Data = Geometry.Empty;
        HudIconBattery.Visibility = Visibility.Visible;
        HudIconBatteryFill.Width = Math.Max(2, 16 * percent / 100.0);
        HudBar.Visibility = Visibility.Collapsed;
        HudName.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Visible;
        HudText.Text = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HudBattery.Visibility = Visibility.Collapsed;
        ShowHud(HudLong);
    }

    // Headphones: their battery when they report it (like the mockup), else their name.
    private void ShowHeadphonesHud(string name, int? battery)
    {
        HudIconFill.Data = HeadphoneCups;
        HudIconFill.Fill = TimerFocusBrush;
        HudIconStroke.Data = HeadphoneBand;
        HudIconStroke.Stroke = TimerFocusBrush;
        if (battery is { } level)
        {
            ShowValue(level, red: level <= 10);
        }
        else
        {
            HudIconBattery.Visibility = Visibility.Collapsed;
            HudBar.Visibility = Visibility.Collapsed;
            HudValue.Visibility = Visibility.Collapsed;
            HudName.Text = name;
            HudName.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ShowHud(HudLong);
    }

    private void ShowBar(double fraction)
    {
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudName.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Collapsed;
        HudBar.Visibility = Visibility.Visible;
        // The bar's width is known once laid out; before that the panel's own width stands in.
        var track = HudBar.ActualWidth > 0 ? HudBar.ActualWidth : HudPanel.Width - 36;
        HudBarFill.Width = Math.Clamp(fraction, 0, 1) * track;
    }

    private void ShowValue(int percent, bool red)
    {
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudBar.Visibility = Visibility.Collapsed;
        HudName.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Visible;
        HudBattery.Visibility = Visibility.Visible;
        HudText.Text = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HudBatteryFill.Width = Math.Max(2, 16 * percent / 100.0);
        HudBatteryFill.Background = red ? (Brush)FindResource("NotchRed") : TimerFocusBrush;
    }

    /// <summary>Show the HUD over the compact notch for a moment (never over an open panel or the chat).</summary>
    private void ShowHud(TimeSpan length)
    {
        if (!_host.SystemHud || _suspendedForChat || _view is not (NotchView.Compact or NotchView.Hud) || _dragging)
        {
            return;
        }

        _hudTimer.Stop();
        _hudTimer.Interval = length;
        if (_view != NotchView.Hud)
        {
            _view = NotchView.Hud;
            ApplyView(animate: true);
            UpdateVisibility();
        }

        _hudTimer.Start();
    }

    internal async void PreviewHud(string kind)
    {
        await Task.Delay(400);
        switch (kind)
        {
            case "mute":
                ShowVolumeHud(0.4f, muted: true);
                break;
            case "brightness":
                ShowBrightnessHud(42);
                break;
            case "charging":
                ShowChargingHud(82);
                break;
            case "low":
                ShowLowBatteryHud(10);
                break;
            case "headphones":
                ShowHeadphonesHud("WH-1000XM5", null);
                break;
            case "buds":
                ShowHeadphonesHud("Headphones (Adrian's Buds3)", await BluetoothBattery.ForOutputAsync("Headphones (Adrian's Buds3)"));
                break;
            default:
                ShowVolumeHud(0.64f, muted: false);
                break;
        }

        _hudTimer.Stop();   // stays for the screenshot
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1200);
            if (kind is "volume" or "mute" or "brightness")
            {
                // The bar is laid out by now: draw it at its real width.
                var fraction = kind == "brightness" ? 0.42 : kind == "mute" ? 0 : 0.64;
                HudBarFill.Width = fraction * HudBar.ActualWidth;
                await Task.Delay(100);
            }

            SnapshotShell(System.IO.Path.Combine(dir, $"notch-hud-{kind}.png"));
        }
    }
}
