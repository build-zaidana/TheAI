using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.Hud;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
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
    private static readonly Geometry Bolt = Geometry.Parse("M 8,1 L 2,9 L 6.5,9 L 5.5,15 L 12,7 L 7.5,7 Z");
    private static readonly Geometry HeadphoneBand = Geometry.Parse("M 2,10 L 2,8 A 7,7 0 0 1 16,8 L 16,10");
    private static readonly Geometry CapsGlyph = Icons.CapsLock;
    private static readonly Geometry KeyboardGlyph = Icons.Keyboard;
    private static readonly Geometry WifiGlyph = Icons.Wifi;
    private static readonly Geometry WifiOffGlyph = Icons.WifiOff;
    private static readonly Geometry HeadphoneCups = Geometry.Parse("M 1,10.5 Q 1,9 2.5,9 L 3.5,9 Q 5,9 5,10.5 L 5,13.5 Q 5,15 3.5,15 L 2.5,15 Q 1,15 1,13.5 Z M 13,10.5 Q 13,9 14.5,9 L 15.5,9 Q 17,9 17,10.5 L 17,13.5 Q 17,15 15.5,15 L 14.5,15 Q 13,15 13,13.5 Z");

    private readonly DispatcherTimer _hudTimer = new();
    private AudioWatcher? _audio;
    private KeyboardWatcher? _keyboard;
    private bool _online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
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

        // The internet dropping and coming back: Windows' own event, no polling.
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            SourceInitialized += (_, _) => ApplyKeyboardWatcher();
        }
        else
        {
            ApplyKeyboardWatcher();
        }

        Closed += (_, _) =>
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            _keyboard?.Dispose();
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

    /// <summary>Settings may have turned the keyboard HUD on or off.</summary>
    private void ApplyKeyboardWatcher()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_host.KeyboardHud && _keyboard is null && handle != IntPtr.Zero &&
            System.Windows.Interop.HwndSource.FromHwnd(handle) is { } source)
        {
            _keyboard = new KeyboardWatcher(source);
            _keyboard.CapsLockChanged += ShowCapsHud;
            _keyboard.LanguageChanged += ShowLanguageHud;
        }
        else if (!_host.KeyboardHud && _keyboard is not null)
        {
            _keyboard.Dispose();
            _keyboard = null;
        }
    }

    private void NetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (e.IsAvailable == _online)
            {
                return;
            }

            _online = e.IsAvailable;
            ShowNetworkHud(e.IsAvailable);
        });

    private void ShowTextHud(Geometry glyph, string text, bool red, Brush? accent = null)
    {
        HudIconFill.Data = Geometry.Empty;
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudIconStroke.Data = glyph;
        HudIconStroke.Stroke = red ? (Brush)FindResource("NotchRed") : accent ?? TimerFocusBrush;
        HudBar.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Collapsed;
        HudName.Text = text;
        HudName.Visibility = Visibility.Visible;
        ShowHud(HudShort);
    }

    // Caps Lock is an on/off state, not a value: just its icon in a small capsule, bright when on and
    // dim when off (the words stay for screen readers).
    private const double HudIconOnlyWidth = 60;   // the icon (26) centred with 12 on each side, plus the edge margins
    private bool _hudIconOnly;
    private bool _hudShownIconOnly;

    private void ShowCapsHud(bool on)
    {
        _hudIconOnly = true;
        ShowTextHud(CapsGlyph, string.Empty, red: false, on ? TimerFocusBrush : Frozen(Color.FromRgb(0x63, 0x63, 0x66)));
        HudName.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(HudPanel, on ? Tr.Text("Caps Lock nyala", "Caps Lock on") : Tr.Text("Caps Lock mati", "Caps Lock off"));
    }

    private void ShowLanguageHud(System.Globalization.CultureInfo culture)
    {
        var name = (culture.IsNeutralCulture ? culture : culture.Parent).NativeName;
        name = name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : culture.Name;
        ShowTextHud(KeyboardGlyph, $"{culture.TwoLetterISOLanguageName.ToUpperInvariant()} · {name}", red: false);
    }

    // Offline: two white Wi-Fi arcs with a red exclamation mark standing in for the inner ones.
    private static readonly Geometry WifiAlertArcs = Geometry.Parse("M 2,8.82 A 15,15 0 0 1 22,8.82 M 5.5,12.6 A 10,10 0 0 1 8.7,10.7 M 15.3,10.7 A 10,10 0 0 1 18.5,12.6");
    private static readonly Geometry WifiAlertMark = Geometry.Parse("M 10.6,10.4 A 1.4,1.4 0 0 1 13.4,10.4 L 13.1,16 A 1.1,1.1 0 0 1 10.9,16 Z M 10.4,19.8 A 1.6,1.6 0 1 1 13.6,19.8 A 1.6,1.6 0 1 1 10.4,19.8 Z");

    private void ShowNetworkHud(bool online)
    {
        ShowTextHud(online ? WifiGlyph : WifiAlertArcs,
            online ? Tr.Text("Kembali online", "Back online") : Tr.Text("Internet terputus", "Internet connection lost"), red: false);
        if (!online)
        {
            HudIconFill.Data = WifiAlertMark;
            HudIconFill.Fill = (Brush)FindResource("NotchRed");
        }
    }

    // ---- The HUD's faces ---------------------------------------------------------------------

    // Volume and brightness: Lucide's own icons as one stroke each (no filled half that could break
    // up when the icon turns dark over the fill).
    private void ShowVolumeHud(float level, bool muted)
    {
        var quiet = muted || level < 0.005f;
        if (ShowLiquidLevel(LiquidHudKind.Volume, level, quiet))
        {
            _liquidWanted = true;
            ShowHud(HudShort);
            return;
        }

        HudIconFill.Data = Geometry.Empty;
        HudIconStroke.Data = quiet ? Icons.VolumeMute : Icons.Volume;
        HudIconStroke.Stroke = quiet ? TimerQuietBrush : TimerFocusBrush;
        ShowBar(muted ? 0 : level);
        ShowHud(HudShort);
    }

    private void ShowBrightnessHud(int level)
    {
        if (ShowLiquidLevel(LiquidHudKind.Brightness, level / 100.0, muted: false))
        {
            _liquidWanted = true;
            ShowHud(HudShort);
            return;
        }

        HudIconFill.Data = Geometry.Empty;
        HudIconStroke.Data = Icons.Brightness;
        HudIconStroke.Stroke = TimerFocusBrush;
        ShowBar(level / 100.0, BrightnessFill);
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

    private static readonly Brush VolumeFill = CapsuleFill(0xFF, 0xFF, 0xFF, 0xD8, 0xD8, 0xDE);
    private static readonly Brush BrightnessFill = CapsuleFill(0xFF, 0xE2, 0x7A, 0xFF, 0xBF, 0x1F);
    private static readonly Brush OnFillBrush = Frozen(Color.FromRgb(0x14, 0x14, 0x16));
    private bool _capsuleWanted, _liquidWanted;
    private const double HudRadius = 20;

    private static Brush CapsuleFill(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 90);
        brush.Freeze();
        return brush;
    }

    // Volume and brightness: the fill grows across the whole capsule; the icon turns dark once the
    // fill reaches under it, and the number stays on the right.
    private void ShowBar(double fraction, Brush? fill = null)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudName.Visibility = Visibility.Collapsed;
        HudBar.Visibility = Visibility.Collapsed;
        HudBattery.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Visible;
        HudText.Text = Math.Round(fraction * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _capsuleWanted = true;
        HudCapsule.Visibility = Visibility.Visible;
        HudCapsuleFill.Background = fill ?? VolumeFill;
        var width = HudCapsule.ActualWidth > 0 ? HudCapsule.ActualWidth : HudPanel.Width;
        HudCapsuleFill.Width = fraction * width;
        if (fraction * width > 34)
        {
            HudIconFill.Fill = OnFillBrush;
            HudIconStroke.Stroke = OnFillBrush;
        }
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
        // Only the volume and brightness faces use the capsule; every other face hides it.
        HudCapsule.Visibility = _capsuleWanted ? Visibility.Visible : Visibility.Collapsed;
        _capsuleWanted = false;
        if (!_liquidWanted)
        {
            _liquidHud = LiquidHudKind.None;   // a plain face: the liquid overlay steps aside
        }
        _liquidWanted = false;
        HudPanel.Width = (_hudIconOnly ? HudIconOnlyWidth : _liquidHud != LiquidHudKind.None ? LiquidHudWidth : HudWidth) - 8;
        _hudShownIconOnly = _hudIconOnly;
        _hudIconOnly = false;
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
            case "caps":
                ShowCapsHud(true);
                break;
            case "language":
                ShowLanguageHud(new System.Globalization.CultureInfo("id-ID"));
                break;
            case "offline":
                ShowNetworkHud(false);
                break;
            case "water":
                ShowWaterHud(1000, 1250, 2000);
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
                HudCapsuleFill.Width = fraction * HudCapsule.ActualWidth;
                if (fraction * HudCapsule.ActualWidth > 34)
                {
                    HudIconFill.Fill = OnFillBrush;
                    HudIconStroke.Stroke = OnFillBrush;
                }
                await Task.Delay(100);
            }

            SnapshotShell(System.IO.Path.Combine(dir, $"notch-hud-{kind}.png"), _liquidHud != LiquidHudKind.None ? Root : null);
        }
    }
}
