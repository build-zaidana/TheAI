using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CodexBar.App;

// Drinking water as a page of the notch: today's millilitres against the goal, one click per glass,
// and the last seven days. When it's been a while since the last glass (during the active hours,
// until the goal is met) the notch opens on the page for a moment, without a sound or a Windows
// notification. One timer, set for the next reminder; nothing runs while the feature is off.
public partial class NotchWindow
{
    private const double WaterWidth = 420;


    private static string WaterPath => System.IO.Path.Combine(CodexBar.Providers.AppPaths.DataRoot, "water.json");

    private readonly DispatcherTimer _waterNudge = new();
    private readonly DispatcherTimer _waterCollapse = new() { Interval = TimeSpan.FromSeconds(15) };
    private WaterLog _water = new();
    private bool _waterPreview;

    private bool WaterAvailable => _host.WaterEnabled;

    private void InitWater()
    {
        _water = WaterLog.Load(WaterPath);
        _waterNudge.Tick += (_, _) => NudgeWater();
        _waterCollapse.Tick += (_, _) =>
        {
            _waterCollapse.Stop();
            if (_view == NotchView.Water && !Shell.IsMouseOver && !_pinned)
            {
                Collapse();
            }
        };
        Closed += (_, _) =>
        {
            _waterNudge.Stop();
            _waterCollapse.Stop();
        };
        ScheduleWaterNudge();
    }

    /// <summary>Set the one timer for the next reminder (settings or a glass may have moved it).</summary>
    private void ScheduleWaterNudge()
    {
        _waterNudge.Stop();
        if (!_host.WaterEnabled)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var next = WaterLog.NextNudge(_water, _host.Water, now);
        // No reminder left today: look again just after midnight.
        var wait = (next ?? new DateTimeOffset(now.Date.AddDays(1).AddMinutes(1), now.Offset)) - now;
        _waterNudge.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        _waterNudge.Start();
    }

    private void NudgeWater()
    {
        var now = DateTimeOffset.Now;
        if (WaterLog.NextNudge(_water, _host.Water, now) is { } due && due <= now.AddSeconds(5))
        {
            _water.LastNudge = now;
            SaveWater();
            if (!Quiet && !_fullscreenHidden && !_suspendedForChat && _view is NotchView.Compact or NotchView.Water)
            {
                OpenWater();
                _waterCollapse.Stop();
                _waterCollapse.Start();
            }
        }

        ScheduleWaterNudge();
    }

    private void SaveWater()
    {
        if (!_waterPreview)
        {
            _water.Save(WaterPath);
        }
    }

    private void AddWater(int millilitres)
    {
        var before = _water.Today(DateTimeOffset.Now);
        _water.Add(millilitres, DateTimeOffset.Now);
        if (millilitres > 0)
        {
            // Over the closed notch the glass fills (E3) and hands back; an open page shows it itself.
            ShowWaterHud(before, _water.Today(DateTimeOffset.Now), _host.Water.GoalMl);
        }
        SaveWater();
        ScheduleWaterNudge();
        _waterCollapse.Stop();
        RenderWater();
    }

    private void WaterAdd_Click(object sender, RoutedEventArgs e) => AddWater(_host.Water.GlassMl);

    private void WaterUndo_Click(object sender, RoutedEventArgs e) => AddWater(-_host.Water.GlassMl);

    private void OpenWater()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Water;
        RenderWater();
        _view = NotchView.Water;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderWater()
    {
        var now = DateTimeOffset.Now;
        var plan = _host.Water;
        var today = _water.Today(now);
        var culture = CultureInfo.CurrentCulture;
        WaterToday.Text = today.ToString("N0", culture);
        WaterGoal.Text = string.Format(culture, "{0:N0} ml", plan.GoalMl);
        WaterLeft.Text = string.Format(culture, "{0:N0} ml", Math.Max(0, plan.GoalMl - today));
        WaterGlasses.Text = plan.GlassMl > 0 ? (today / plan.GlassMl).ToString(culture) : "0";
        WaterUndoValue.Text = string.Format(culture, "−{0}", plan.GlassMl);
        WaterUndo.Visibility = today > 0 ? Visibility.Visible : Visibility.Collapsed;
        WaterAdd.ToolTip = string.Format(culture, "+{0} ml", plan.GlassMl);
        System.Windows.Automation.AutomationProperties.SetName(WaterAdd, string.Format(culture, Tr.Text("Tambah {0} ml", "Add {0} ml"), plan.GlassMl));
        System.Windows.Automation.AutomationProperties.SetName(WaterUndo, string.Format(culture, Tr.Text("Kurangi {0} ml", "Remove {0} ml"), plan.GlassMl));

        WaterNext.Text = today >= plan.GoalMl
            ? Tr.Text("Target hari ini tercapai", "Today's goal reached")
            : WaterLog.NextNudge(_water, plan, now) is { } next
                ? next <= now.AddSeconds(30)
                    ? Tr.Text("Waktunya segelas air", "Time for a glass of water")
                    : string.Format(culture, Tr.Text("Pengingat berikutnya {0:HH:mm}", "Next reminder at {0:HH:mm}"), next)
                : string.Format(culture, Tr.Text("{0:N0} ml lagi", "{0:N0} ml to go"), plan.GoalMl - today);

        RenderWaterWeek(now, plan);
    }

    private void RenderWaterWeek(DateTimeOffset now, WaterPlan plan)
    {
        WaterWeek.Children.Clear();
        WaterWeek.ColumnDefinitions.Clear();
        var week = _water.Week(now);
        var top = Math.Max(plan.GoalMl, week.Max(day => day.Millilitres));
        for (var i = 0; i < week.Count; i++)
        {
            var (day, millilitres) = week[i];
            WaterWeek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var met = millilitres >= plan.GoalMl;
            var column = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
            var label = new TextBlock
            {
                Text = day.ToString("ddd", CultureInfo.CurrentCulture),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = i == week.Count - 1 ? IslandTokens.Text1 : IslandTokens.Text3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            DockPanel.SetDock(label, Dock.Bottom);
            column.Children.Add(label);
            column.Children.Add(new Border
            {
                Height = Math.Max(4, 48 * millilitres / (double)top),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(6),
                // Today is white; a day that met the goal is lighter than one that didn't. One voice, no blue.
                Background = i == week.Count - 1 ? IslandTokens.Text1 : IslandTokens.Frozen(met ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x3A, 0x3A, 0x3C)),
                ToolTip = string.Format(CultureInfo.CurrentCulture, "{0:N0} ml", millilitres)
            });
            Grid.SetColumn(column, i);
            WaterWeek.Children.Add(column);
        }
    }

    internal async void PreviewWater(string mode)
    {
        await Task.Delay(400);
        _waterPreview = true;
        var now = DateTimeOffset.Now;
        _water = new WaterLog();
        int[] sample = [2250, 1500, 2000, 1750, 2500, 1250];
        for (var back = 6; back >= 1; back--)
        {
            _water.Days[now.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = sample[6 - back];
        }

        _water.Add(1250, now.AddMinutes(-70));
        if (mode == "nudge")
        {
            NudgeWater();
            _waterCollapse.Stop();
        }
        else
        {
            OpenWater();
        }

        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-water-{mode}.png"));
        }
    }
}
