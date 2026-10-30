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

// Control Center (canvas board 12): eight toggles in one pane and brightness / volume as vertical
// sliders. Dimensions come from Tokens.xaml. Do Not Disturb and battery saver from the board are left
// out: Windows has no supported way to flip them from an app.
public partial class NotchWindow
{
    private const double ControlWidth = 402;   // 2 * S1 + the 378 wide row of panes

    private sealed record ControlTile(string Label, Geometry Glyph, Color On, Func<bool> IsOn, Func<Task> Tap);

    private readonly Dictionary<Border, ControlTile> _controlButtons = [];
    private bool? _wifiOn, _bluetoothOn;
    private bool _controlBuilt;
    private Border? _draggingSlider;

    private IEnumerable<ControlTile> ControlTileList() =>
    [
        new("Wi-Fi", Icons.Wifi, Color.FromRgb(0x0A, 0x84, 0xFF), () => _wifiOn == true, async () => _wifiOn = await SystemControls.ToggleRadioAsync(RadioKind.WiFi) ?? _wifiOn),
        new("Bluetooth", Icons.Bluetooth, Color.FromRgb(0x0A, 0x84, 0xFF), () => _bluetoothOn == true, async () => _bluetoothOn = await SystemControls.ToggleRadioAsync(RadioKind.Bluetooth) ?? _bluetoothOn),
        new(Tr.Text("Bisukan mikrofon", "Mute microphone"), Icons.MicOff, Color.FromRgb(0xFF, 0x45, 0x3A), () => MicMute.IsMuted() == true, () =>
        {
            ToggleMicMute();
            return Task.CompletedTask;
        }),
        new(Tr.Text("Timer 25 menit", "25-minute timer"), Icons.Timer, Color.FromRgb(0xFF, 0x6B, 0x5E), () => TimerShowing, () =>
        {
            _timer.StartTimer(TimeSpan.FromMinutes(25), DateTimeOffset.Now);
            return Task.CompletedTask;
        }),
        new(Tr.Text("Tangkapan layar", "Screenshot"), Icons.Screenshot, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return Task.CompletedTask;
        }),
        new(Tr.Text("+1 gelas air", "+1 glass of water"), Icons.Droplet, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            AddWater(_host.Water.GlassMl);
            return Task.CompletedTask;
        }),
        new(Tr.Text("Papan klip", "Clipboard"), Icons.Clipboard, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
        {
            if (ClipboardAvailable)
            {
                OpenClipboard();
            }

            return Task.CompletedTask;
        }),
        new(Tr.Text("Kunci layar", "Lock screen"), Icons.Lock, Color.FromRgb(0x2C, 0x2C, 0x2E), () => false, () =>
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
            SetSlider(CcBrightnessSlider, CcBrightnessFill, brightness ?? 0, enabled: brightness is not null);
        }
    }

    private void BuildControl()
    {
        if (_controlBuilt)
        {
            return;
        }

        _controlBuilt = true;
        CcBrightnessGlyph.Data = Icons.Brightness;
        CcVolumeGlyph.Data = Icons.Volume;
        foreach (var tile in ControlTileList())
        {
            var button = new Border
            {
                Width = DesignTokens.Tile,
                Height = DesignTokens.Tile,
                CornerRadius = new CornerRadius(DesignTokens.Tile / 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = tile.Label,
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
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                await tile.Tap();
                RefreshControl();
            };
            _controlButtons[button] = tile;
            ControlTiles.Children.Add(button);
        }

        foreach (var slider in new[] { CcBrightnessSlider, CcVolumeSlider })
        {
            // A Border's rounded corners do not clip its child, so the fill gets a pill-shaped clip of its own.
            var inner = (FrameworkElement)slider.Child;
            inner.SizeChanged += (_, _) => inner.Clip = new RectangleGeometry(new Rect(inner.RenderSize), inner.ActualWidth / 2, inner.ActualWidth / 2);
            slider.MouseLeftButtonDown += (_, e) =>
            {
                _draggingSlider = slider;
                slider.CaptureMouse();
                DragSlider(slider, e.GetPosition(slider).Y);
                e.Handled = true;
            };
            slider.MouseMove += (_, e) =>
            {
                if (_draggingSlider == slider)
                {
                    DragSlider(slider, e.GetPosition(slider).Y);
                }
            };
            slider.MouseLeftButtonUp += (_, _) =>
            {
                _draggingSlider = null;
                slider.ReleaseMouseCapture();
            };
        }
    }

    /// <summary>Tiles light in their own colour when on; the rest stay a plain glass disc.</summary>
    private void RefreshControl()
    {
        foreach (var (button, tile) in _controlButtons)
        {
            button.Background = new SolidColorBrush(tile.IsOn() ? tile.On : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }

        var volume = SystemControls.GetVolume();
        SetSlider(CcVolumeSlider, CcVolumeFill, volume ?? 0, enabled: volume is not null);
    }

    private void SetSlider(Border slider, Border fill, int percent, bool enabled)
    {
        slider.Opacity = enabled ? 1 : 0.4;
        fill.Height = (slider.Height - 2) * Math.Clamp(percent, 0, 100) / 100.0;
        // The icon sits at the bottom: dark on the lit fill, light on the empty track.
        var glyph = slider == CcVolumeSlider ? CcVolumeGlyph : CcBrightnessGlyph;
        glyph.Stroke = fill.Height > 34 ? IconOnFill : IconOnTrack;
    }

    private static readonly System.Windows.Media.Brush IconOnFill = Frozen(Color.FromArgb(0xCC, 0, 0, 0));
    private static readonly System.Windows.Media.Brush IconOnTrack = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7));

    private void DragSlider(Border slider, double y)
    {
        var percent = (int)Math.Round(Math.Clamp(1 - y / slider.Height, 0, 1) * 100);
        var isVolume = slider == CcVolumeSlider;
        SetSlider(slider, isVolume ? CcVolumeFill : CcBrightnessFill, percent, enabled: true);
        if (isVolume)
        {
            SystemControls.SetVolume(percent);
        }
        else
        {
            _ = SystemControls.SetBrightnessAsync(percent);
        }
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
