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
    private const double ControlWidth = IslandTokens.WidthS;

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
        new(Tr.Text("Timer 25 menit", "25-minute timer"), "Timer 25", Icons.Timer, Color.FromRgb(0xFF, 0x6B, 0x5E), () => TimerShowing, () =>
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
        // Tile columns: 52, gap, 52, gap, 52, gap, 52 across the 328 inside P 16.
        var gap = (IslandTokens.WidthS - (2 * IslandTokens.P) - (4 * IslandTokens.Tile)) / 3;
        for (var c = 0; c < 7; c++)
        {
            ControlTiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c % 2 == 0 ? IslandTokens.Tile : gap) });
        }
        ControlTiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ControlTiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        ControlTiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var index = 0;
        foreach (var tile in ControlTileList())
        {
            var scale = new ScaleTransform();
            var button = new Border
            {
                Width = IslandTokens.Tile,
                Height = IslandTokens.Tile,
                CornerRadius = new CornerRadius(IslandTokens.Tile / 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = IslandTokens.Surface1,
                ToolTip = tile.Label,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = scale,
                Child = new Path
                {
                    Data = tile.Glyph,
                    Width = 20,
                    Height = 20,
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
            // Pressed: 52 -> 48 while the pointer is down; leaving the tile cancels.
            button.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                scale.ScaleX = scale.ScaleY = 48.0 / IslandTokens.Tile;
            };
            button.MouseLeave += (_, _) => scale.ScaleX = scale.ScaleY = 1;
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                scale.ScaleX = scale.ScaleY = 1;
                await tile.Tap();
                RefreshControl();
            };
            _controlButtons[button] = tile;

            var cell = new StackPanel();
            cell.Children.Add(button);
            cell.Children.Add(new TextBlock
            {
                Text = tile.Caption,
                FontSize = 11,
                Foreground = IslandTokens.Text2,
                Margin = new Thickness(-24, 4, -24, 0),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            });
            Grid.SetColumn(cell, index % 4 * 2);
            Grid.SetRow(cell, index / 4 * 2);
            ControlTiles.Children.Add(cell);
            index++;
        }

        _brightnessSlider = new LiquidCapsule(Icons.Brightness, IslandTokens.Btn, warm: true);
        _volumeSlider = new LiquidCapsule(Icons.Volume, IslandTokens.Btn) { Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_brightnessSlider, Tr.Text("Kecerahan", "Brightness"));
        System.Windows.Automation.AutomationProperties.SetName(_volumeSlider, "Volume");
        _brightnessSlider.Changed += percent => _ = SystemControls.SetBrightnessAsync(percent);
        _volumeSlider.Changed += percent => SystemControls.SetVolume(percent);
        ControlSliders.Children.Add(_brightnessSlider);
        ControlSliders.Children.Add(_volumeSlider);
    }

    /// <summary>On: the tile fills with its colour. Off: surface/1. No outline, no glow.</summary>
    private void RefreshControl()
    {
        foreach (var (button, tile) in _controlButtons)
        {
            button.Background = tile.IsOn() ? new SolidColorBrush(tile.On) : IslandTokens.Surface1;
        }

        var volume = SystemControls.GetVolume();
        _volumeSlider?.Set(volume ?? 0, enabled: volume is not null);
    }

    internal async void PreviewControl()
    {
        await Task.Delay(400);
        OpenControl();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-control.png"));
        }
    }
}
