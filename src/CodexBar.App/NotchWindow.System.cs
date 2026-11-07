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
    private readonly List<double>[] _systemHistory = [[], [], [], []];
    private (System.Windows.Controls.Button Button, TextBlock Value)[]? _systemFoot;
    private int _systemPick;
    private bool _systemPickedByHand;
    private int _systemTicks;
    private readonly bool[] _systemSettled = new bool[4];   // Windows' first CPU reading is 0: level the line on the second
    private string? _systemHog;

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
        RestartSystem();
        Closed += (_, _) =>
        {
            _systemTick.Stop();
            _systemWatch.Stop();
            _monitor?.Dispose();
        };
    }

    private SystemMonitor Monitor => _monitor ??= new SystemMonitor();

    /// <summary>Settings may have turned it on or off.</summary>
    private void RestartSystem()
    {
        if (_host.SystemMonitor)
        {
            _systemWatch.Start();
        }
        else
        {
            _systemWatch.Stop();
            _monitor?.Dispose();
            _monitor = null;
        }
    }

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
        if (_systemFoot is null)
        {
            SystemRows.Children.Clear();
            _systemFoot = new[] { "CPU", "GPU", Tr.Text("Memori", "Memory"), "Disk C:" }.Select(AddSystemFoot).ToArray();
        }

        var sample = Monitor.Sample();
        var diskUsed = sample.DiskTotal > 0 ? 100.0 * (sample.DiskTotal - sample.DiskFree) / sample.DiskTotal : (double?)null;
        double?[] readings = [sample.Cpu, sample.Gpu, sample.MemoryLoad, diskUsed];
        var values = new[]
        {
            sample.Cpu is { } cpu ? $"{cpu:0} %" : "— %",
            sample.Gpu is { } gpu ? $"{gpu:0} %" : "— %",
            SystemAlerts.Bytes(sample.MemoryUsed),
            SystemAlerts.Bytes(sample.DiskFree)
        };

        // Full memory or a full drive is the one red: a busy CPU or GPU is just busy.
        var memoryFull = sample.MemoryLoad >= SystemAlerts.MemoryLimit;
        var diskFull = diskUsed >= 100 - SystemAlerts.DiskPercentLimit;
        var pick = _systemPickedByHand ? _systemPick : memoryFull ? 2 : diskFull ? 3 : _systemPick;
        var warn = (pick == 2 && memoryFull) || (pick == 3 && diskFull);
        var colour = warn ? IslandTokens.Error : System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF7);
        var moved = pick != _systemPick || SystemLine.Colour != colour;
        _systemPick = pick;

        for (var i = 0; i < 4; i++)
        {
            var history = _systemHistory[i];
            var reading = readings[i] ?? 0;
            if (history.Count == 0 || !_systemSettled[i])
            {
                _systemSettled[i] = history.Count > 0;
                history.Clear();
                history.AddRange(Enumerable.Repeat(reading, 40));   // the line starts level, then moves
                moved = true;
            }

            history.Add(reading);
            if (history.Count > 41)
            {
                history.RemoveAt(0);
            }

            var (button, value) = _systemFoot[i];
            button.Tag = i == pick ? "selected" : null;
            value.Text = values[i];
            button.Foreground = i == pick && warn ? IslandTokens.Frozen(IslandTokens.Error) : null;
            if (i != pick || !warn)
            {
                button.ClearValue(ForegroundProperty);
            }
        }

        SystemLine.Colour = colour;
        if (moved)
        {
            SystemLine.Show(_systemHistory[pick]);
        }
        else
        {
            SystemLine.Push(_systemHistory[pick][^1]);
        }

        var parts = values[pick].Split(' ');
        SystemValue.Text = parts[0];
        SystemValue.Foreground = warn ? IslandTokens.Frozen(IslandTokens.Error) : IslandTokens.Text1;
        SystemUnit.Text = parts.Length > 1 ? parts[1] : string.Empty;
        SystemLabel.Text = new[] { "CPU", "GPU", Tr.Text("Memori", "Memory"), "Disk C:" }[pick];
        if (pick == 2 && warn)
        {
            FindSystemHog();
        }

        SystemMeta.Text = pick switch
        {
            2 when warn && _systemHog is not null => _systemHog,
            2 => string.Format(Tr.Text("dari {0}", "of {0}"), SystemAlerts.Bytes(sample.MemoryTotal)),
            3 => Tr.Text("kosong", "free"),
            _ => Tr.Text("Satu menit terakhir", "The last minute")
        };
    }

    /// <summary>Which app holds the most memory: looked at every 15 s while memory is the problem.</summary>
    private void FindSystemHog()
    {
        if (_systemTicks++ % 10 != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var usage = new Dictionary<string, long>();
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        usage[process.ProcessName] = usage.GetValueOrDefault(process.ProcessName) + process.WorkingSet64;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Gone already, or not ours to read.
                    }
                }
            }

            return usage.Count == 0 ? null : (KeyValuePair<string, long>?)usage.MaxBy(pair => pair.Value);
        }).ContinueWith(task =>
        {
            if (task.Result is { } hog)
            {
                _systemHog = string.Format(Tr.Text("{0} memakai {1}", "{0} uses {1}"), hog.Key, SystemAlerts.Bytes((ulong)hog.Value));
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private (System.Windows.Controls.Button, TextBlock) AddSystemFoot(string label, int index)
    {
        var last = index == 3;
        var align = last ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var value = new TextBlock { Style = (Style)FindResource("IslandFootValue"), HorizontalAlignment = align };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("IslandFootLabel"), Text = label, HorizontalAlignment = align });
        stack.Children.Add(value);
        var button = new System.Windows.Controls.Button { Style = (Style)FindResource("IslandFootButton"), Content = stack };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            _systemPick = index;
            _systemPickedByHand = true;
            SystemLine.Show(_systemHistory[index]);
            RenderSystem();
        };
        SystemRows.Children.Add(button);
        return (button, value);
    }

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
