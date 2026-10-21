using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.Hud;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CodexBar.App;

// The machine at a glance: CPU, GPU, memory and the system drive as a page of the open notch
// (read every 1.5 s only while it is shown), and a word through the HUD when memory or the drive
// runs short (looked at every 30 s, then quiet for 10 min). Busy CPU or GPU is never a warning.
public partial class NotchWindow
{
    private const double SystemWidth = 420;
    private static readonly Geometry WarningIcon = Geometry.Parse("M 9,1.5 L 17,15.5 L 1,15.5 Z M 9,6.5 L 9,10.5 M 9,12.8 L 9,13");

    private readonly DispatcherTimer _systemTick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _systemWatch = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SystemAlerts _systemAlerts = new();
    private SystemMonitor? _monitor;
    private (Border Fill, TextBlock Value)[]? _systemBars;

    private bool SystemPageAvailable => _host.SystemMonitor;

    private void InitSystem()
    {
        _systemTick.Tick += (_, _) =>
        {
            if (_view == NotchView.System)
            {
                RenderSystem();
            }
            else
            {
                _systemTick.Stop();
            }
        };
        _systemWatch.Tick += (_, _) => WatchSystem();
        _systemWatch.Start();
        Closed += (_, _) =>
        {
            _systemTick.Stop();
            _systemWatch.Stop();
            _monitor?.Dispose();
        };
    }

    private SystemMonitor Monitor => _monitor ??= new SystemMonitor();

    private void WatchSystem()
    {
        if (!_host.SystemMonitor)
        {
            return;
        }

        if (_systemAlerts.Check(Monitor.Sample(), DateTimeOffset.Now) is not { } alert)
        {
            return;
        }

        var sample = Monitor.Sample();
        var text = alert.Kind == SystemAlertKind.Memory
            ? string.Format(CultureInfo.InvariantCulture, Tr.Text("RAM {0:0}% terpakai", "RAM {0:0}% used"), alert.Value)
            : string.Format(Tr.Text("C: tinggal {0}", "C: {0} left"), SystemAlerts.Bytes(sample.DiskFree));
        HudIconFill.Data = Geometry.Empty;
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudIconStroke.Data = WarningIcon;
        HudIconStroke.Stroke = (Brush)FindResource("NotchRed");
        HudBar.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Collapsed;
        HudName.Text = text;
        HudName.Visibility = Visibility.Visible;
        ShowHud(HudLong);
    }

    private void OpenSystem()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.System;
        RenderSystem();
        _view = NotchView.System;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        _systemTick.Start();
        RenderPageDots();
    }

    private void RenderSystem()
    {
        if (_systemBars is null)
        {
            SystemRows.Children.Clear();
            _systemBars = new[] { "CPU", "GPU", "RAM", "C:" }.Select(AddSystemRow).ToArray();
        }

        var sample = Monitor.Sample();
        SetBar(0, sample.Cpu, sample.Cpu is { } cpu ? $"{cpu:0}%" : "—");
        SetBar(1, sample.Gpu, sample.Gpu is { } gpu ? $"{gpu:0}%" : "—");
        SetBar(2, sample.MemoryLoad, $"{SystemAlerts.Bytes(sample.MemoryUsed)} / {SystemAlerts.Bytes(sample.MemoryTotal)}");
        var diskUsed = sample.DiskTotal > 0 ? 100.0 * (sample.DiskTotal - sample.DiskFree) / sample.DiskTotal : (double?)null;
        SetBar(3, diskUsed, string.Format(Tr.Text("{0} kosong", "{0} free"), SystemAlerts.Bytes(sample.DiskFree)));
    }

    private void SetBar(int index, double? percent, string value)
    {
        var (fill, text) = _systemBars![index];
        var track = fill.Parent is Border bar && bar.ActualWidth > 0 ? bar.ActualWidth : 220;
        fill.Width = Math.Max(4, track * Math.Clamp(percent ?? 0, 0, 100) / 100);
        // Full memory or a full drive is the one red: a busy CPU or GPU is just busy.
        var warn = index >= 2 && percent >= (index == 2 ? SystemAlerts.MemoryLimit : 100 - SystemAlerts.DiskPercentLimit);
        fill.Background = warn ? (Brush)FindResource("NotchRed") : TimerFocusBrush;
        text.Text = value;
        text.Foreground = warn ? (Brush)FindResource("NotchRed") : TimerFocusBrush;
    }

    private (Border Fill, TextBlock Value) AddSystemRow(string label)
    {
        var grid = new Grid { Height = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = TimerQuietBrush, VerticalAlignment = VerticalAlignment.Center });
        var fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Background = TimerFocusBrush };
        var bar = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = fill
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        var value = new TextBlock
        {
            Style = (Style)FindResource("NotchDigits"),
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        SystemRows.Children.Add(grid);
        return (fill, value);
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewSystem(string mode)
    {
        await Task.Delay(400);
        if (mode == "alert")
        {
            _ = Monitor.Sample();
            HudName.Text = Tr.Text("RAM 93% terpakai", "RAM 93% used");
            HudIconFill.Data = Geometry.Empty;
            HudIconBattery.Visibility = Visibility.Collapsed;
            HudIconStroke.Data = WarningIcon;
            HudIconStroke.Stroke = (Brush)FindResource("NotchRed");
            HudBar.Visibility = Visibility.Collapsed;
            HudValue.Visibility = Visibility.Collapsed;
            HudName.Visibility = Visibility.Visible;
            ShowHud(HudLong);
            _hudTimer.Stop();
        }
        else
        {
            OpenSystem();
            _pinned = true;
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(2500);
            if (mode != "alert")
            {
                RenderSystem();
                await Task.Delay(200);
            }

            SnapshotShell(System.IO.Path.Combine(dir, $"notch-system-{mode}.png"));
        }
    }
}
