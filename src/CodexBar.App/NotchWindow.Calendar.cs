using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.Hud;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Today's calendar in the notch: within half an hour of the next event the compact notch shows a
// calendar and the minutes left; a minute before it starts the notch opens on it; the calendar
// page lists what is left of the day, the next event large with a camera to join its call.
public partial class NotchWindow
{
    private const double CalendarWidth = 420;
    private static readonly TimeSpan CalendarSoon = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _calendarRefresh = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _calendarClock = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _calendarReminder = new() { Interval = TimeSpan.FromSeconds(8) };
    private IReadOnlyList<CalendarEvent> _events = [];
    private string _calendarSource = "off";
    private string _announced = string.Empty;
    private bool _calendarPreview;

    /// <summary>The next timed event that hasn't ended.</summary>
    private CalendarEvent? NextEvent =>
        _events.FirstOrDefault(item => !item.AllDay && item.End > DateTimeOffset.Now);

    /// <summary>The next event is close (or has just begun): the compact notch counts down to it.</summary>
    private bool CalendarShowing =>
        NextEvent is { } next && next.Start - DateTimeOffset.Now <= CalendarSoon && DateTimeOffset.Now - next.Start < TimeSpan.FromMinutes(5);

    private bool CalendarPageAvailable => _events.Any(item => item.AllDay || item.End > DateTimeOffset.Now);

    private void InitCalendar()
    {
        _calendarRefresh.Tick += (_, _) => _ = RefreshCalendarAsync();
        _calendarClock.Tick += (_, _) => CalendarClock();
        _calendarReminder.Tick += (_, _) =>
        {
            _calendarReminder.Stop();
            if (_view == NotchView.Calendar && !Shell.IsMouseOver && !_pinned)
            {
                Collapse();
            }
        };
        Closed += (_, _) =>
        {
            _calendarRefresh.Stop();
            _calendarClock.Stop();
        };
        RestartCalendar();
    }

    /// <summary>Settings may have switched the source: read again (or stop).</summary>
    private void RestartCalendar()
    {
        if (_calendarPreview)
        {
            return;
        }

        _calendarSource = _host.CalendarSource;
        if (_calendarSource == "off")
        {
            _calendarRefresh.Stop();
            _calendarClock.Stop();
            _events = [];
            CalendarChanged();
            return;
        }

        _calendarRefresh.Interval = _calendarSource == "ics" ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
        _calendarRefresh.Start();
        _calendarClock.Start();
        _ = RefreshCalendarAsync();
    }

    private async Task RefreshCalendarAsync()
    {
        var source = _calendarSource;
        var events = await CalendarReader.TodayAsync(source);
        if (source != _calendarSource)
        {
            return;   // switched meanwhile
        }

        _events = events;
        CalendarChanged();
    }

    private void CalendarChanged()
    {
        RenderCalendar();
        ApplyMusicCompact();
        if (_view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }
        else if (_view == NotchView.Calendar && !CalendarPageAvailable)
        {
            Collapse();
        }

        UpdateVisibility();
    }

    // Every 20 s: the countdown moves, the compact notch appears / goes, and a minute before an event it opens.
    private void CalendarClock()
    {
        var was = CompactCalendar.Visibility == Visibility.Visible;
        ApplyMusicCompact();
        if (_view == NotchView.Compact && was != CalendarShowing)
        {
            ApplyView(animate: true);
            UpdateVisibility();
        }

        if (_view == NotchView.Calendar)
        {
            RenderCalendar();
        }

        if (NextEvent is { } next && next.Start - DateTimeOffset.Now <= TimeSpan.FromMinutes(1) && next.Start > DateTimeOffset.Now.AddMinutes(-1))
        {
            var key = next.Title + next.Start.ToString("O", CultureInfo.InvariantCulture);
            if (key != _announced && _view == NotchView.Compact && !HasPendingRequest && !_suspendedForChat)
            {
                _announced = key;
                OpenCalendar();
                _calendarReminder.Start();
            }
        }
    }

    private static string Until(CalendarEvent item)
    {
        var left = item.Start - DateTimeOffset.Now;
        return left <= TimeSpan.Zero ? Tr.Text("kini", "now") : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    // ---- Compact -------------------------------------------------------------------------

    /// <summary>Calendar face of the compact notch; returns whether it took the notch.</summary>
    private bool ApplyCalendarCompact()
    {
        var show = CalendarShowing && NextEvent is not null;
        CompactCalendar.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactCalendar.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactCalendar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactCalendarText.Visibility = show && _state == NotchState.Idle && !Vertical ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            CompactCalendarText.Text = Until(NextEvent!);
            CompactArt.Visibility = Visibility.Collapsed;
            CompactEq.Visibility = Visibility.Collapsed;
            CompactGlyph.Visibility = Visibility.Collapsed;
            QuotaRing.Visibility = Visibility.Collapsed;
        }

        return show;
    }

    // ---- The calendar page ---------------------------------------------------------------

    private void OpenCalendar()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Calendar;
        RenderCalendar();
        _view = NotchView.Calendar;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderCalendar()
    {
        CalendarRows.Children.Clear();
        var now = DateTimeOffset.Now;
        var upcoming = _events.Where(item => !item.AllDay && item.End > now).Take(4).ToList();
        var allDay = _events.Where(item => item.AllDay).Take(2).ToList();
        foreach (var item in allDay)
        {
            CalendarRows.Children.Add(new TextBlock
            {
                Text = "• " + item.Title,
                FontSize = 12,
                Foreground = (Brush)FindResource("NotchText3"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 0, 6)
            });
        }

        for (var i = 0; i < upcoming.Count; i++)
        {
            CalendarRows.Children.Add(BuildEventRow(upcoming[i], first: i == 0));
        }

        RenderPageDots();
    }

    private FrameworkElement BuildEventRow(CalendarEvent item, bool first)
    {
        var row = new Grid { Height = first ? 48 : 38 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var time = new TextBlock
        {
            Text = item.Start.ToLocalTime().ToString("HH'.'mm", CultureInfo.InvariantCulture),
            Style = (Style)FindResource("NotchDigits"),
            FontSize = first ? 24 : 16,
            Foreground = first ? TimerFocusBrush : TimerQuietBrush
        };
        row.Children.Add(time);
        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = first ? 14 : 13,
            FontWeight = first ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = first ? TimerFocusBrush : TimerQuietBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        if (item.JoinUrl is { } url)
        {
            var join = new Button
            {
                Style = (Style)FindResource("NotchTintButton"),
                Width = first ? 40 : 32,
                Height = first ? 40 : 32,
                Background = Frozen(Color.FromArgb(first ? (byte)0x38 : (byte)0x1F, 0xFF, 0xFF, 0xFF)),
                ToolTip = url,
                Content = new Path
                {
                    Stroke = TimerFocusBrush,
                    StrokeThickness = 1.6,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M 1,1.5 Q 1,0.5 2,0.5 L 10,0.5 Q 11,0.5 11,1.5 L 11,10.5 Q 11,11.5 10,11.5 L 2,11.5 Q 1,11.5 1,10.5 Z M 11,4.5 L 16,1.5 L 16,10.5 L 11,7.5")
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(join, Tr.Text("Gabung rapat", "Join the call"));
            join.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    DiagnosticsLog.Error("Calendar", exception.Message);
                }
            };
            Grid.SetColumn(join, 2);
            row.Children.Add(join);
        }

        if (!first)
        {
            return new Border { BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        }

        return row;
    }

    internal async void PreviewCalendar(string mode)
    {
        _calendarPreview = true;
        _calendarRefresh.Stop();
        _calendarClock.Stop();
        var today = DateTimeOffset.Now.Date;
        DateTimeOffset At(double minutesFromNow) => DateTimeOffset.Now.AddMinutes(minutesFromNow);
        _events =
        [
            new CalendarEvent("Daily standup", At(10), At(25), false, "https://meet.google.com/abc-defg-hij"),
            new CalendarEvent("Review desain", At(150), At(210), false, null),
            new CalendarEvent("Sesi fokus", At(330), At(420), false, null)
        ];
        await Task.Delay(400);
        CalendarChanged();
        if (mode == "open")
        {
            OpenCalendar();
            _pinned = true;
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-calendar-{mode}.png"));
        }
    }
}
