using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.App.Hud;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// "Today": in the combined page mode, weather, the next meeting, water and reminders share one page
// of short rows instead of a page each. A row opens its feature's full page; scrolling from there
// comes back here. The separate mode keeps a page per feature.
public partial class NotchWindow
{
    private const double TodayWidth = 420;

    private static readonly Geometry CalendarGlyph = Geometry.Parse("M 4,6 L 20,6 L 20,20 L 4,20 Z M 4,10 L 20,10 M 8,3.5 L 8,7.5 M 16,3.5 L 16,7.5");
    private static readonly Geometry DropGlyph = Geometry.Parse("M 12,2.5 C 12,2.5 5,10.5 5,15 A 7,7 0 0 0 19,15 C 19,10.5 12,2.5 12,2.5 Z");
    private static readonly Geometry BellGlyph = Geometry.Parse("M 6,16 L 6,10.5 A 6,6 0 0 1 18,10.5 L 18,16 L 20,18 L 4,18 Z M 10,20.5 A 2,2 0 0 0 14,20.5");

    /// <summary>Views the combined mode folds into Today.</summary>
    private static bool FoldedIntoToday(NotchView view) =>
        view is NotchView.Weather or NotchView.Calendar or NotchView.Reminders or NotchView.Water;

    private bool TodayAvailable =>
        _host.CombinedPages && (WeatherAvailable || CalendarPageAvailable || RemindersAvailable || WaterAvailable || PrayerAvailable ||
                                _privacyShown.Microphone.Count > 0);

    private void OpenToday()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Today;
        RenderToday();
        _view = NotchView.Today;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderToday()
    {
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.CurrentCulture;
        TodayRows.Children.Clear();

        if (WeatherAvailable)
        {
            var (glyph, text) = _weather is { } weather && _place is { } place
                ? (WeatherIcon.Data ?? SunGlyph,
                   $"{Math.Round(weather.Temperature):0}° · {WeatherClient.Describe(WeatherClient.Kind(weather.Code), Tr.Text)} · {place.Name}")
                : (SunGlyph, Tr.Text("Cuaca belum dimuat", "Weather not loaded yet"));
            AddTodayRow(glyph, text, WorldClock.ParseList(_host.WorldClocks).FirstOrDefault() is { } clock
                ? $"{clock.Label} {TimeZoneInfo.ConvertTime(now, clock.Zone):HH:mm}" : string.Empty, OpenWeather);
        }

        if (_privacyShown.Microphone.Count > 0)
        {
            var muted = MicMute.IsMuted() == true;
            var mute = new Button
            {
                Style = (Style)FindResource(muted ? "NotchPrimaryPill" : "NotchPill"),
                Height = 24,
                FontSize = 11.5,
                Content = muted ? Tr.Text("Nyalakan", "Unmute") : Tr.Text("Bisukan", "Mute")
            };
            mute.Click += (_, _) => ToggleMicMute();
            AddTodayRow(muted ? MicOffGlyph : MicGlyph,
                (muted ? Tr.Text("Mikrofon dibisukan · ", "Microphone muted · ") : Tr.Text("Mikrofon dipakai · ", "Microphone in use · ")) +
                string.Join(", ", _privacyShown.Microphone), mute, ToggleMicMute);
        }

        if (PrayerAvailable && NextPrayer(now) is { } prayer)
        {
            var left = prayer.At - now;
            AddTodayRow(CrescentGlyph, string.Format(culture, Tr.Text("{0} pukul {1:HH:mm}", "{0} at {1:HH:mm}"), prayer.Name(Tr.Text), prayer.At),
                left < TimeSpan.FromHours(1) ? string.Format(culture, Tr.Text("{0} mnt lagi", "in {0} min"), (int)Math.Ceiling(left.TotalMinutes)) : string.Empty,
                OpenWeather);
        }

        if (CalendarPageAvailable && NextEvent is { } next)
        {
            var when = next.Start <= now ? Tr.Text("sekarang", "now")
                : next.Start - now < TimeSpan.FromHours(1) ? string.Format(culture, Tr.Text("{0} mnt lagi", "in {0} min"), (int)Math.Ceiling((next.Start - now).TotalMinutes))
                : next.Start.ToString("HH:mm", culture);
            AddTodayRow(CalendarGlyph, next.Title, when, OpenCalendar);
        }

        if (WaterAvailable)
        {
            var plan = _host.Water;
            var add = new Button
            {
                Style = (Style)FindResource("NotchPill"),
                Height = 24,
                FontSize = 11.5,
                Content = string.Format(culture, "+{0} ml", plan.GlassMl)
            };
            add.Click += (_, _) =>
            {
                AddWater(plan.GlassMl);
                RenderToday();
            };
            AddTodayRow(DropGlyph, string.Format(culture, "{0:N0} / {1:N0} ml", _water.Today(now), plan.GoalMl), add, OpenWater);
        }

        if (RemindersAvailable)
        {
            var open = _reminders.Open.Take(3).ToList();
            if (open.Count == 0)
            {
                AddTodayRow(BellGlyph, Tr.Text("Tidak ada pengingat", "No reminders"), string.Empty, () => OpenReminders(focusInput: true));
            }

            foreach (var item in open)
            {
                AddTodayRow(BellGlyph, item.Text, ReminderList.When(item, now, Tr.Text), () => OpenReminders());
            }
        }
    }

    private void AddTodayRow(Geometry glyph, string text, object right, Action open)
    {
        var grid = new Grid { MinHeight = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Path
        {
            Data = glyph,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = TimerQuietBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        });
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = TimerFocusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        FrameworkElement side = right as FrameworkElement ?? new TextBlock
        {
            Text = right as string ?? string.Empty,
            Style = (Style)FindResource("NotchDigits"),
            FontSize = 12,
            Foreground = TimerQuietBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        side.Margin = new Thickness(10, 0, 0, 0);
        side.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);
        grid.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is null)
            {
                e.Handled = true;
                open();
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(grid, text);
        TodayRows.Children.Add(new Border
        {
            BorderBrush = Frozen(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, TodayRows.Children.Count == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(0, 2, 0, 2),
            Child = grid
        });
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    internal async void PreviewToday()
    {
        await Task.Delay(400);
        _waterPreview = true;
        _remindersPreview = true;
        var now = DateTimeOffset.Now;
        _place = new GeoPlace("Jakarta", "Indonesia", -6.2, 106.8, "Asia/Jakarta");
        _placeFor = "Jakarta";
        _weather = new WeatherReport(31.4, 2, true, 33.1, 25.0, 70);
        RenderWeather();
        _privacyShown = new PrivacyState(["Zoom"], []);
        _water = new WaterLog();
        _water.Add(1250, now.AddMinutes(-40));
        _reminders = new ReminderList();
        _reminders.Add("14:30 " + Tr.Text("telepon klien", "call the client"), now);
        _reminders.Add(Tr.Text("beli susu", "buy milk"), now);
        OpenToday();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-today.png"));
        }
    }
}
