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
        TodayDate.Text = now.ToString("dddd, d MMMM", culture);

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

    /// <summary>
    /// A 56 row: a 40 item (R 20, surface/1) holding the feature's icon in its colour, a title and an
    /// optional line under it, and a time or a 40 button on the right. Hover lights the row 8 in from the
    /// island's side with R 28, concentric with RE 36, and lifts the item and button to surface/2.
    /// </summary>
    private void AddTodayRow(Geometry glyph, System.Windows.Media.Color accent, string title, string? sub, object right, Action open, Brush? sideAccent = null)
    {
        var grid = new Grid { Height = IslandTokens.Row, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IslandTokens.Btn + 12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var item = new Border
        {
            Width = IslandTokens.Btn,
            Height = IslandTokens.Btn,
            CornerRadius = new CornerRadius(IslandTokens.Ri),
            Background = IslandTokens.Surface1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new Path
            {
                Data = glyph,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Stroke = IslandTokens.Frozen(accent),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(item);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Foreground = IslandTokens.Text1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(sub))
        {
            text.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 13,
                Foreground = IslandTokens.Text2,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        FrameworkElement side = right as FrameworkElement ?? new TextBlock
        {
            Text = right as string ?? string.Empty,
            FontSize = 13,
            Foreground = sideAccent ?? IslandTokens.Text2,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTabular();
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

        // The row sits 8 in from the island's side; its 8 padding puts the item at P 16.
        var row = new Border
        {
            CornerRadius = new CornerRadius(IslandTokens.RowRadius),
            Padding = new Thickness(8, 0, 8, 0),
            Background = Brushes.Transparent,
            Child = grid
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = IslandTokens.Surface1;
            item.Background = IslandTokens.Surface2;
            if (side is Button button)
            {
                button.Background = IslandTokens.Surface2;
            }
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            item.Background = IslandTokens.Surface1;
            if (side is Button button)
            {
                button.Background = IslandTokens.Surface1;
            }
        };
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
