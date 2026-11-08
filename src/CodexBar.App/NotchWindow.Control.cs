using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexBar.App.Hud;
using Windows.Devices.Radios;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CodexBar.App;

// Control Center (canvas Page 2, board 03): eight 52 tiles with a caption under each, then brightness
// and volume as liquid capsules (board 25, G2). W_S 360, P 16 all round; the gap between tiles makes the
// first and last tile flush with the sliders. Do Not Disturb and battery saver are left out: Windows has
// no supported way to flip them from an app.
public partial class NotchWindow
{
    private const double ControlWidth = IslandTokens.WidthM;

    private sealed record ControlTile(string Label, string Caption, Geometry Glyph, Color On, Func<bool> IsOn, Func<Task> Tap);

    private readonly Dictionary<Border, ControlTile> _controlButtons = [];
    private bool? _wifiOn, _bluetoothOn;
    private bool _controlBuilt;
    private LiquidCapsule? _brightnessSlider, _volumeSlider;

    private IEnumerable<ControlTile> ControlTileList() =>
    [
        new("Wi-Fi", "Wi-Fi", Icons.Wifi, Color.FromRgb(0x0A, 0x84, 0xFF), () => _wifiOn == true, async () => _wifiOn = await SystemControls.ToggleRadioAsync(RadioKind.WiFi) ?? _wifiOn),
        new("Bluetooth", "Bluetooth", Icons.Bluetooth, Color.FromRgb(0x0A, 0x84, 0xFF), () => _bluetoothOn == true, async () => _bluetoothOn = await SystemControls.ToggleRadioAsync(RadioKind.Bluetooth) ?? _bluetoothOn),
        new(Tr.Text("Bisukan mikrofon", "Mute microphone"), Tr.Text("Mikrofon", "Microphone"), Icons.MicOff, Color.FromRgb(0xFF, 0x45, 0x3A), () => MicMute.IsMuted() == true, () =>
        {
            ToggleMicMute();
            return Task.CompletedTask;
        }),
        new(Tr.Text("Timer 25 menit", "25-minute timer"), "Timer 25", Icons.Timer, Color.FromRgb(0xFF, 0x6B, 0x5E), () => _timer.IsActive, () =>
        {
            _timer.StartTimer(TimeSpan.FromMinutes(25), DateTimeOffset.Now);
            return Task.CompletedTask;
        }),
        new(Tr.Text("Tangkapan layar", "Screenshot"), Tr.Text("Tangkap", "Capture"), Icons.Screenshot, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return Task.CompletedTask;
        }),
        new(Tr.Text("+1 gelas air", "+1 glass of water"), Tr.Text("+1 gelas", "+1 glass"), Icons.Droplet, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            AddWater(_host.Water.GlassMl);
            return Task.CompletedTask;
        }),
        new(Tr.Text("Papan klip", "Clipboard"), Tr.Text("Papan klip", "Clipboard"), Icons.Clipboard, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            if (ClipboardAvailable)
            {
                OpenClipboard();
            }

            return Task.CompletedTask;
        }),
        new(Tr.Text("Kunci layar", "Lock screen"), Tr.Text("Kunci", "Lock"), Icons.Lock, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            LockWorkStation();
            return Task.CompletedTask;
        })
    ];

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    private async void OpenControl()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Control;
        BuildControl();
        RefreshControl();
        _view = NotchView.Control;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        RenderPageDots();

        // Radios and brightness answer asynchronously; the page is already up while they do.
        _wifiOn = await SystemControls.GetRadioAsync(RadioKind.WiFi);
        _bluetoothOn = await SystemControls.GetRadioAsync(RadioKind.Bluetooth);
        _wifiName = await Task.Run(ConnectedWifiName);
        var brightness = await SystemControls.GetBrightnessAsync();
        if (_view == NotchView.Control)
        {
            RefreshControl();
            _brightnessSlider?.Set(brightness ?? 0, enabled: brightness is not null);
        }
    }

    private void BuildControl()
    {
        if (_controlBuilt)
        {
            return;
        }

        _controlBuilt = true;
        foreach (var tile in ControlTileList())
        {
            var scale = new ScaleTransform();
            var button = new Border
            {
                Width = IslandTokens.Btn,
                Height = IslandTokens.Btn,
                CornerRadius = new CornerRadius(IslandTokens.Ri),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = IslandTokens.Surface1,
                ToolTip = tile.Label,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = scale,
                Child = new Path
                {
                    Data = tile.Glyph,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 1.8,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(button, tile.Label);
            // Pressed: 40 -> 36 while the pointer is down; leaving the tile cancels. Hover names it in the label.
            button.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                scale.ScaleX = scale.ScaleY = 36.0 / IslandTokens.Btn;
            };
            button.MouseEnter += (_, _) => ControlLabel.Text = tile.Caption;
            button.MouseLeave += (_, _) =>
            {
                scale.ScaleX = scale.ScaleY = 1;
                ControlLabel.Text = Tr.Text("Kontrol", "Control");
            };
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                scale.ScaleX = scale.ScaleY = 1;
                await tile.Tap();
                RefreshControl();
            };
            _controlButtons[button] = tile;

            ControlTiles.Children.Add(button);
        }

        _volumeSlider = new LiquidCapsule(Icons.Volume, 32);
        _brightnessSlider = new LiquidCapsule(Icons.Brightness, 32, warm: true) { Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_brightnessSlider, Tr.Text("Kecerahan", "Brightness"));
        System.Windows.Automation.AutomationProperties.SetName(_volumeSlider, "Volume");
        _brightnessSlider.Changed += percent => _ = SystemControls.SetBrightnessAsync(percent);
        _volumeSlider.Changed += percent => SystemControls.SetVolume(percent);
        ControlSliders.Children.Add(_volumeSlider);
        ControlSliders.Children.Add(_brightnessSlider);
    }

    /// <summary>On: the tile is white with a black glyph. Off: surface/1. One voice, like the other pages.</summary>
    private void RefreshControl()
    {
        foreach (var (button, tile) in _controlButtons)
        {
            var on = tile.IsOn();
            button.Background = on ? IslandTokens.Text1 : IslandTokens.Surface1;
            ((Path)button.Child).Stroke = on ? System.Windows.Media.Brushes.Black : IslandTokens.Text1;
        }

        // The network's own name when Windows will say it (it may need location access); else just on/off.
        ControlTitle.Text = _wifiOn == false ? Tr.Text("Wi-Fi mati", "Wi-Fi off") : _wifiName ?? Tr.Text("Wi-Fi aktif", "Wi-Fi on");
        var wifi = _wifiOn == false ? null : _wifiName is null ? null : Tr.Text("Tersambung", "Connected");
        var bluetooth = _bluetoothOn == true ? Tr.Text("Bluetooth aktif", "Bluetooth on") : Tr.Text("Bluetooth mati", "Bluetooth off");
        ControlSub.Text = wifi is null ? bluetooth : $"{wifi} · {bluetooth}";
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        ControlMeta.Text = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            ? string.Empty
            : string.Format(Tr.Text("Baterai {0:0}%", "Battery {0:0}%"), power.BatteryLifePercent * 100);

        var volume = SystemControls.GetVolume();
        _volumeSlider?.Set(volume ?? 0, enabled: volume is not null);
    }

    private string? _wifiName;

    private static string? ConnectedWifiName()
    {
        try
        {
            var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var name = profile is { IsWlanConnectionProfile: true } ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : null;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    internal async void PreviewControl()
    {
        await Task.Delay(400);
        OpenControl();
        // CODEXBAR_TEST_LEAVE=1: left open and unpinned, to see the page close by itself (logged).
        _pinned = Environment.GetEnvironmentVariable("CODEXBAR_TEST_LEAVE") is not { Length: > 0 };
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-control.png"));
        }
    }
}
