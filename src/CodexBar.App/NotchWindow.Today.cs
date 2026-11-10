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
    private const double TodayWidth = IslandTokens.WidthM;

    private static readonly Geometry CalendarGlyph = Icons.Calendar;
    private static readonly Geometry DropGlyph = Icons.Droplet;
    private static readonly Geometry BellGlyph = Icons.Bell;

    // Each feature keeps the colour people already link to it, on its icon: weather blue, prayer green,
    // calendar red, water cyan, reminder yellow, and orange for a microphone in use. Text stays white on black.
    internal static readonly Brush DayAccent = Frozen(System.Windows.Media.Color.FromRgb(0x64, 0xD2, 0xFF));
    internal static readonly Brush AttentionAccent = Frozen(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A));

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
        // D1: the date is the title; "Today" is implied.
        TodayDay.Text = culture.TextInfo.ToTitleCase(now.ToString("dddd", culture));
        TodayDate.Text = now.ToString("d MMM", culture);
        TodayMeta.Text = string.Empty;

        if (WeatherAvailable)
        {
            var (glyph, title, place) = _weather is { } weather && _place is { } where
                ? (WeatherIcon.Data ?? SunGlyph,
                   $"{Math.Round(weather.Temperature):0}° · {WeatherClient.Describe(WeatherClient.Kind(weather.Code), Tr.Text)}", where.Name)
                : (SunGlyph, Tr.Text("Cuaca belum dimuat", "Weather not loaded yet"), (string?)null);
            AddTodayRow(glyph, IslandTokens.Blue, title, place, WorldClock.ParseList(_host.WorldClocks).FirstOrDefault() is { } clock
                ? $"{clock.Label} {TimeZoneInfo.ConvertTime(now, clock.Zone):HH:mm}" : string.Empty, OpenWeather);
        }

        if (_privacyShown.Microphone.Count > 0)
        {
            var muted = MicMute.IsMuted() == true;
            var mute = TodayButton(muted ? Tr.Text("Nyalakan", "Unmute") : Tr.Text("Bisukan", "Mute"));
            mute.Click += (_, _) => ToggleMicMute();
            AddTodayRow(muted ? MicOffGlyph : MicGlyph, IslandTokens.Timer,
                muted ? Tr.Text("Mikrofon dibisukan", "Microphone muted") : Tr.Text("Mikrofon dipakai", "Microphone in use"),
                string.Join(", ", _privacyShown.Microphone), mute, ToggleMicMute);
        }

        if (PrayerAvailable && NextPrayer(now) is { } prayer)
        {
            var left = prayer.At - now;
            AddTodayRow(CrescentGlyph, IslandTokens.Agent, prayer.Name(Tr.Text), prayer.At.ToString("HH:mm", culture),
                left < TimeSpan.FromHours(1) ? string.Format(culture, Tr.Text("{0} mnt lagi", "in {0} min"), (int)Math.Ceiling(left.TotalMinutes)) : string.Empty,
                OpenWeather);
        }

        if (CalendarPageAvailable && NextEvent is { } next)
        {
            var when = next.Start <= now ? Tr.Text("sekarang", "now")
                : next.Start - now < TimeSpan.FromHours(1) ? string.Format(culture, Tr.Text("{0} mnt lagi", "in {0} min"), (int)Math.Ceiling((next.Start - now).TotalMinutes))
                : next.Start.ToString("HH:mm", culture);
            TodayMeta.Text = string.Format(culture, Tr.Text("Berikutnya {0}", "Next {0}"), when);
            AddTodayRow(CalendarGlyph, IslandTokens.Error, next.Title, Tr.Text("Kalender", "Calendar"), when, OpenCalendar);
        }

        if (WaterAvailable)
        {
            var plan = _host.Water;
            var add = TodayButton(string.Format(culture, "+{0} ml", plan.GlassMl));
            add.Click += (_, _) =>
            {
                AddWater(plan.GlassMl);
                RenderToday();
            };
            AddTodayRow(DropGlyph, IslandTokens.Water, string.Format(culture, "{0:N0} / {1:N0} ml", _water.Today(now), plan.GoalMl),
                Tr.Text("Air minum", "Water"), add, OpenWater);
        }

        if (RemindersAvailable)
        {
            var open = _reminders.Open.Take(3).ToList();
            if (open.Count == 0)
            {
                AddTodayRow(BellGlyph, IslandTokens.Wait, Tr.Text("Tidak ada pengingat", "No reminders"), null, string.Empty, () => OpenReminders(focusInput: true));
            }

            foreach (var item in open)
            {
                AddTodayRow(BellGlyph, IslandTokens.Wait, item.Text, ReminderList.When(item, now, Tr.Text), string.Empty, () => OpenReminders(),
                    item.DueAt <= now ? AttentionAccent : null);   // a due one says so in orange
            }
        }
    }

    private Button TodayButton(string text) => new()
    {
        Style = (Style)FindResource("IslandButton"),
        Content = text
    };

    private void TodayAdd_Click(object sender, RoutedEventArgs e) => OpenReminders(focusInput: true);

    /// <summary>
    /// A 40 row under a hairline (board 33), the same as the clipboard's: a 16 glyph in text/3, the title,
    /// and a time or a small button on the right. The colour stays out of it; the page is one voice.
    /// </summary>
    private void AddTodayRow(Geometry glyph, System.Windows.Media.Color accent, string title, string? sub, object right, Action open, Brush? sideAccent = null)
    {
        var grid = new Grid { Height = IslandTokens.Btn - 1, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Path
        {
            Data = glyph,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = IslandTokens.Text3,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });

        var body = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = IslandTokens.Text1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var label = right as string;
        FrameworkElement side = right as FrameworkElement ?? new TextBlock
        {
            Text = string.IsNullOrEmpty(label) ? sub ?? string.Empty : label,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = sideAccent ?? IslandTokens.Text3
        }.WithTabular();
        if (side is Button small)
        {
            small.Style = (Style)FindResource("IslandChip");
        }

        side.Margin = new Thickness(12, 0, 0, 0);
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
        System.Windows.Automation.AutomationProperties.SetName(grid, title);

        var row = new Border
        {
            BorderBrush = IslandTokens.Frozen(System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
        row.MouseEnter += (_, _) => body.Foreground = IslandTokens.Text2;
        row.MouseLeave += (_, _) => body.Foreground = IslandTokens.Text1;
        TodayRows.Children.Add(row);
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
