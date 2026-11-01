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
    private const double TodayWidth = 460;   // panel: 2 * S1 + the 436 wide panes

    private static readonly Geometry CalendarGlyph = Icons.Calendar;
    private static readonly Geometry DropGlyph = Icons.Droplet;
    private static readonly Geometry BellGlyph = Icons.Bell;

    // Each feature's icon sits on a small app-style tile whose colour is the one people already
    // link to it: weather-app blue, green for prayer, calendar red, water teal, sticky-note yellow for
    // reminders, and the orange Windows uses for a microphone in use. Text stays white on black.
    internal static readonly Brush DayAccent = Frozen(System.Windows.Media.Color.FromRgb(0x64, 0xD2, 0xFF));
    internal static readonly Brush AttentionAccent = Frozen(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush WeatherTile = Tile(0x6A, 0xC1, 0xFF, 0x2F, 0x6F, 0xE0);
    private static readonly Brush PrayerTile = Tile(0x4A, 0xDE, 0x80, 0x15, 0x9A, 0x55);
    private static readonly Brush CalendarTile = Tile(0xFF, 0x7A, 0x6E, 0xE0, 0x32, 0x2B);
    private static readonly Brush WaterTile = Tile(0x4F, 0xE3, 0xE8, 0x0B, 0x9C, 0xB0);
    private static readonly Brush ReminderTile = Tile(0xFF, 0xD4, 0x4D, 0xE8, 0xA3, 0x00);
    private static readonly Brush MicTile = Tile(0xFF, 0xA9, 0x40, 0xF0, 0x6A, 0x00);

    private static Brush Tile(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(r1, g1, b1), System.Windows.Media.Color.FromRgb(r2, g2, b2),
            new System.Windows.Point(0, 0), new System.Windows.Point(0.6, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush TileFor(Geometry glyph) =>
        glyph == DropGlyph ? WaterTile
        : glyph == CalendarGlyph ? CalendarTile
        : glyph == BellGlyph ? ReminderTile
        : glyph == CrescentGlyph ? PrayerTile
        : glyph == MicGlyph || glyph == MicOffGlyph ? MicTile
        : WeatherTile;
    private static readonly Brush RowLine = Frozen(System.Windows.Media.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHover = Frozen(System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

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
        TodayDate.Text = now.ToString("dddd, d MMM", culture);

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
                AddTodayRow(BellGlyph, item.Text, ReminderList.When(item, now, Tr.Text), () => OpenReminders(),
                    item.DueAt <= now ? AttentionAccent : null);   // a due one says so in orange
            }
        }
    }

    private void AddTodayRow(Geometry glyph, string text, object right, Action open, Brush? sideAccent = null)
    {
        var grid = new Grid { MinHeight = DesignTokens.Row, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DesignTokens.RowIcon + DesignTokens.S4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = DesignTokens.RowIcon,
            Height = DesignTokens.RowIcon,
            CornerRadius = new CornerRadius(DesignTokens.RowIcon / 2),
            Background = TileFor(glyph),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new Path
            {
                Data = glyph,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = Brushes.White,
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var label = new TextBlock
        {
            Text = text,
            FontSize = 14,
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
            Foreground = sideAccent ?? TimerQuietBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        side.Margin = new Thickness(DesignTokens.S3, 0, 0, 0);
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
        var row = new Border
        {
            CornerRadius = new CornerRadius(DesignTokens.R3),
            Padding = new Thickness(12, 0, 12, 0),
            Background = Brushes.Transparent,
            Child = grid
        };
        if (TodayRows.Children.Count > 0)
        {
            // Hairline between rows, inset to the text like a macOS list.
            TodayRows.Children.Add(new Border { Height = 0.5, Background = RowLine, Margin = new Thickness(12 + DesignTokens.RowIcon + DesignTokens.S4, 0, 12, 0) });
        }

        row.MouseEnter += (_, _) => row.Background = RowHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
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
