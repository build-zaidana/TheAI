using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexBar.App;

// The notch's own timer — pomodoro, countdown, stopwatch, and an alarm beside them — drawn after
// the Clock app's live activities: tinted round controls, thin orange digits, a minute ruler to
// pick a length. Anything typed while the Timer page has focus is one of AllTime's commands
// (+5, 25, @7:30, p, sw, lap…), previewed before Enter runs it. When a countdown ends or the alarm
// rings, the notch springs out, glows and sends rings outward until it is touched.
public partial class NotchWindow
{
    private const double TimerWidth = 420;
    private const double TimerDoneWidth = 420;
    private static readonly TimeSpan CelebrationLength = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AlarmRingLength = TimeSpan.FromSeconds(60);
    // Neutral ink: which phase it is shows as an icon, not as a colour.
    private static readonly Brush TimerFocusBrush = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7));
    private static readonly Brush TimerBreakBrush = TimerFocusBrush;
    private static readonly Brush TimerQuietBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    // Small 16-unit icons: focus (a target), a break (a cup), a plain timer (an hourglass), laps (a flag), next (round arrow).
    private static readonly Geometry FocusIcon = Geometry.Parse("F0 M8,1.5 A6.5,6.5 0 1 0 8.001,1.5 Z M8,3.6 A4.4,4.4 0 1 0 8.001,3.6 Z M8,5.9 A2.1,2.1 0 1 0 8.001,5.9 Z");
    private static readonly Geometry BreakIcon = Geometry.Parse("F0 M1.5,4.5 H11.5 V9.5 A4.5,4.5 0 0 1 7,14 H6 A4.5,4.5 0 0 1 1.5,9.5 Z M11.5,6 H12.6 A2.2,2.2 0 0 1 12.6,10.4 H11.4 V9 H12.6 A0.8,0.8 0 0 0 12.6,7.4 H11.5 Z");
    private static readonly Geometry TimerIcon = Geometry.Parse("F0 M3,1 H13 V2.6 H12 C12,5.6 9.4,6.6 9.4,8 C9.4,9.4 12,10.4 12,13.4 H13 V15 H3 V13.4 H4 C4,10.4 6.6,9.4 6.6,8 C6.6,6.6 4,5.6 4,2.6 H3 Z M5.6,2.6 C5.6,4.6 7.8,5.6 8,7 C8.2,5.6 10.4,4.6 10.4,2.6 Z M8,9.6 C7,10.6 5.6,11.4 5.6,13.4 H10.4 C10.4,11.4 9,10.6 8,9.6 Z");
    private static readonly Geometry LapIcon = Geometry.Parse("F0 M3,1.5 H4.6 V14.5 H3 Z M4.6,2 H13 L10.8,5 L13,8 H4.6 Z");

    private static readonly string TimerFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "timer.json");

    private static readonly string WatchFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "stopwatch.json");

    // Two slots, like DynamicLake's stacked timers: a countdown (pomodoro or timer, plus the alarm)
    // and a stopwatch, each running on its own and shown as its own row.
    private NotchTimer _timer = NotchTimer.Restore(null);
    private NotchTimer _watch = NotchTimer.Restore(null);
    private readonly DispatcherTimer _timerTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private TimerFinished? _timerDone;
    private DateTime _celebrationStarted;
    private bool _timerPreview;
    private TimerMode _idleMode = TimerMode.Timer;   // what Start begins when nothing runs

    /// <summary>A countdown or stopwatch runs (or is paused), or an alarm is set.</summary>
    private bool TimerShowing => _timer.IsActive || _watch.IsActive || _timer.AlarmAt is not null;

    /// <summary>Something live besides the agent keeps the compact notch wide and visible.</summary>
    private bool CompactLive => MusicShowing || TimerShowing || ShelfShowing || CalendarShowing;

    private void InitTimer()
    {
        _timer = LoadTimer(TimerFile);
        _watch = LoadTimer(WatchFile);
        if (_timer.Mode == TimerMode.Stopwatch)
        {
            // Saved before the stopwatch had its own slot: move it there, keep the alarm where it was.
            var alarm = _timer.AlarmAt;
            var label = _timer.AlarmLabel;
            if (_timer.IsActive && !_watch.IsActive)
            {
                _watch = _timer;
            }

            _timer = NotchTimer.Restore(null);
            if (alarm is { } at)
            {
                _timer.SetAlarm(at, label);
            }
        }

        _timerTick.Tick += (_, _) => TimerTick();
        _pulseTimer.Tick += (_, _) => Pulse();
        _idleMode = _timer.Mode is TimerMode.Pomodoro or TimerMode.Stopwatch ? _timer.Mode : TimerMode.Timer;
        TimerWheel.Value = _timer.LastTimer;
        TimerWheel.Changed += length =>
        {
            if (!_timer.IsActive && length > TimeSpan.Zero)
            {
                _timer.Select(TimerMode.Timer, length);
                SaveTimer();
            }
        };
        Closed += (_, _) =>
        {
            _timerTick.Stop();
            _pulseTimer.Stop();
        };
        UpdateTimerTick();
    }

    // ---- State and persistence --------------------------------------------------------------

    private static NotchTimer LoadTimer(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return NotchTimer.Restore(JsonSerializer.Deserialize<TimerState>(File.ReadAllText(file)));
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Timer", exception.Message);
        }

        return NotchTimer.Restore(null);
    }

    private void SaveTimer()
    {
        if (_timerPreview)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TimerFile)!);
            foreach (var (file, slot) in new[] { (TimerFile, _timer), (WatchFile, _watch) })
            {
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(slot.Save()));
                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Timer", exception.Message);
        }
    }

    /// <summary>After any change: persist, redraw, and let the compact notch grow or shrink.</summary>
    private void TimerChanged()
    {
        SaveTimer();
        RenderTimer();
        ApplyMusicCompact();
        if (_view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }

        UpdateVisibility();
        UpdateTimerTick();
    }

    private void UpdateTimerTick()
    {
        if (!TimerShowing && _timerDone is null)
        {
            _timerTick.Stop();
            return;
        }

        // Hundredths on an open stopwatch; quarter seconds otherwise; an alarm alone needs a check a second.
        _timerTick.Interval = _view == NotchView.Timer && _watch.Run == TimerRun.Running
            ? TimeSpan.FromMilliseconds(40)
            : _timer.IsActive || _watch.IsActive || _timerDone is not null ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
        _timerTick.Start();
    }

    private void TimerTick()
    {
        var now = DateTimeOffset.Now;
        if (_timer.Tick(now) is { } finished)
        {
            SaveTimer();
            OnTimerFinished(finished);
            return;
        }

        var limit = _timerDone?.Mode == TimerMode.Alarm ? AlarmRingLength : CelebrationLength;
        if (_timerDone is not null && DateTime.UtcNow - _celebrationStarted > limit && !Shell.IsMouseOver)
        {
            Collapse();   // nobody looked: settle back
            return;
        }

        ApplyTimerCompact();
        if (_view == NotchView.Timer)
        {
            RenderTimer();
        }
    }

    // ---- Compact ---------------------------------------------------------------------------

    private void ApplyTimerCompact()
    {
        var showing = TimerShowing;
        CompactTimer.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactTimer.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactTimer.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        if (!showing)
        {
            CompactTimerText.Visibility = Visibility.Collapsed;
            // Below the timer: an event about to start, then (when nothing plays) the shelf.
            if (ApplyCalendarCompact())
            {
                CompactShelf.Visibility = Visibility.Collapsed;
                CompactShelfCount.Visibility = Visibility.Collapsed;
            }
            else
            {
                ApplyShelfCompact();
            }

            PlaceCompactChatButton();
            ApplyKeyline();
            return;
        }

        CompactShelf.Visibility = Visibility.Collapsed;
        CompactShelfCount.Visibility = Visibility.Collapsed;
        CompactCalendar.Visibility = Visibility.Collapsed;
        CompactCalendarText.Visibility = Visibility.Collapsed;

        // The timer outranks the music in the compact notch; the music stays one scroll away.
        CompactArt.Visibility = Visibility.Collapsed;
        CompactEq.Visibility = Visibility.Collapsed;
        CompactGlyph.Visibility = Visibility.Collapsed;
        QuotaRing.Visibility = Visibility.Collapsed;

        var now = DateTimeOffset.Now;
        string text;
        Brush brush;
        double opacity = 1;
        var slot = _timer.IsActive ? _timer : _watch.IsActive ? _watch : null;
        if (slot is not null)
        {
            brush = TimerBrush();
            text = NotchTimer.Format(slot.Display(now));
            CompactTimerArc.Stroke = brush;
            CompactTimerTrack.Stroke = Tint(brush, 0x40);
            CompactTimerArc.Data = RingGeometry(16, 2.2, slot.IsCountdown ? 1 - slot.Progress(now) : slot.Progress(now));
            // Last minute: the digits breathe; paused: they dim.
            var lastMinute = slot.IsCountdown && slot.Run == TimerRun.Running && slot.Remaining(now) < TimeSpan.FromMinutes(1);
            opacity = slot.Run == TimerRun.Paused ? 0.55 : lastMinute && now.Millisecond >= 500 ? 0.4 : 1;
        }
        else
        {
            brush = TimerFocusBrush;
            text = AlarmClock(_timer.AlarmAt!.Value);
        }

        CompactTimerRing.Visibility = slot is not null ? Visibility.Visible : Visibility.Collapsed;
        CompactAlarmGlyph.Visibility = slot is not null ? Visibility.Collapsed : Visibility.Visible;
        var agentRight = _state != NotchState.Idle;
        var narrow = Vertical;
        CompactTimerLeftText.Visibility = agentRight && !narrow ? Visibility.Visible : Visibility.Collapsed;
        CompactTimerText.Visibility = !agentRight && !narrow ? Visibility.Visible : Visibility.Collapsed;
        foreach (var block in new[] { CompactTimerLeftText, CompactTimerText })
        {
            block.Text = text;
            block.Foreground = brush;
            block.Opacity = opacity;
        }

        PlaceCompactChatButton();
        ApplyKeyline();
    }

    private static string AlarmClock(DateTimeOffset at) =>
        at.ToLocalTime().ToString("H':'mm", CultureInfo.InvariantCulture);

    /// <summary>The outline stays the notch's usual one; the timer's colour lives in its content only.</summary>
    private void ApplyKeyline() => Shell.BorderBrush = OutlineBrush;

    private Brush TimerBrush() =>
        _timer.Mode == TimerMode.Pomodoro && _timer.Phase != PomodoroPhase.Focus ? TimerBreakBrush : TimerFocusBrush;

    private static SolidColorBrush Tint(Brush brush, byte alpha)
    {
        var hue = ((SolidColorBrush)brush).Color;
        var tint = new SolidColorBrush(Color.FromArgb(alpha, hue.R, hue.G, hue.B));
        tint.Freeze();
        return tint;
    }

    // A ring drawn clockwise from 12 o'clock in a square of the given size.
    private static Geometry RingGeometry(double size, double thickness, double fraction)
    {
        var centre = size / 2;
        var radius = centre - thickness / 2;
        if (fraction >= 0.999)
        {
            return new EllipseGeometry(new Point(centre, centre), radius, radius);
        }

        if (fraction <= 0.001)
        {
            return Geometry.Empty;
        }

        var angle = fraction * 2 * Math.PI;
        var end = new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(centre, centre - radius) };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    // ---- The Timer page --------------------------------------------------------------------

    private void OpenTimer(bool focus = false)
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        StopCelebration();
        _lastPage = NotchView.Timer;
        RenderTimer();
        _view = NotchView.Timer;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        UpdateTimerTick();
        if (focus)
        {
            Activate();
            TimerInput.Focus();
        }
    }

    private void RenderTimer()
    {
        var now = DateTimeOffset.Now;
        var active = _timer.IsActive;
        var typed = TimerInput.Text.Trim();
        var command = TimerCommandParser.Parse(typed);
        var result = typed.Length > 0 ? CommandResult(command, now) : null;

        // Running row: typing shows in place of the label, the number shows what Enter would make of it.
        TimerRunRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var brush = TimerBrush();
            var running = _timer.Run == TimerRun.Running;
            TimerPlayButton.Background = Tint(brush, 0x38);
            TimerPlayGlyph.Fill = brush;
            TimerPauseGlyph.Fill = brush;
            TimerPlayGlyph.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            TimerPauseGlyph.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(TimerPlayButton, running ? Tr.Text("Jeda", "Pause") : Tr.Text("Lanjut", "Resume"));
            var stopwatch = _timer.Mode == TimerMode.Stopwatch;
            TimerStopGlyph.Visibility = stopwatch ? Visibility.Collapsed : Visibility.Visible;
            TimerResetGlyph.Visibility = stopwatch ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(TimerStopButton, stopwatch ? "Reset" : Tr.Text("Hentikan", "Stop"));

            ShowTyped(TimerCaption, typed, TimerCaptionText(), TimerQuietBrush);
            var icon = TimerCaptionIcon_();
            TimerCaptionIcon.Data = icon;
            TimerCaptionIcon.Visibility = typed.Length == 0 && icon is not null ? Visibility.Visible : Visibility.Collapsed;
            TimerBig.Text = result ?? (stopwatch ? NotchTimer.FormatPrecise(_timer.Elapsed(now)) : NotchTimer.Format(_timer.Remaining(now)));
            TimerBig.Foreground = brush;
            TimerBig.Opacity = running || result is not null ? 1 : 0.55;
        }

        // Stopwatch row, stacked under the countdown.
        TimerWatchRow.Visibility = _watch.IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (_watch.IsActive)
        {
            var watching = _watch.Run == TimerRun.Running;
            TimerWatchRow.Margin = new Thickness(0, active ? 8 : 0, 0, 0);
            TimerWatchPlayGlyph.Visibility = watching ? Visibility.Collapsed : Visibility.Visible;
            TimerWatchPauseGlyph.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(TimerWatchPlay, watching ? Tr.Text("Jeda", "Pause") : Tr.Text("Lanjut", "Resume"));
            TimerWatchLap.Text = _watch.Laps.Count > 0 ? (_watch.Laps.Count + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
            TimerWatchLapIcon.Visibility = TimerWatchLap.Visibility = _watch.Laps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TimerWatchBig.Text = NotchTimer.FormatPrecise(_watch.Elapsed(now));
            TimerWatchBig.Opacity = watching ? 1 : 0.55;
        }

        // Alarm row, stacked under the running ones.
        TimerAlarmRow.Visibility = _timer.AlarmAt is null ? Visibility.Collapsed : Visibility.Visible;
        if (_timer.AlarmAt is { } at)
        {
            TimerAlarmRow.Margin = new Thickness(0, active || _watch.IsActive ? 8 : 0, 0, 0);
            // The clock icon says "alarm"; a label only when one was given; "+1" when it rings tomorrow.
            TimerAlarmCaption.Text = _timer.AlarmLabel;
            TimerAlarmCaption.Visibility = _timer.AlarmLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TimerAlarmTime.Margin = new Thickness(_timer.AlarmLabel.Length > 0 ? 16 : 0, 0, 0, 0);
            TimerAlarmTime.Text = AlarmClock(at) + (at.ToLocalTime().Date > now.Date ? " +1" : string.Empty);
        }

        // Nothing running at all: the chosen mode, ready to start.
        var idle = !active && !_watch.IsActive;
        TimerIdle.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        if (idle)
        {
            TimerIdle.Margin = new Thickness(0, _timer.AlarmAt is null ? 0 : 8, 0, 0);
            // Timer: the hour / minute / second wheels; the other modes (or anything typed) show their number.
            var wheel = _idleMode == TimerMode.Timer && typed.Length == 0;
            TimerWheel.Visibility = wheel ? Visibility.Visible : Visibility.Collapsed;
            TimerIdleShown.Visibility = wheel ? Visibility.Collapsed : Visibility.Visible;
            if (!TimerWheel.IsMouseCaptured)
            {
                TimerWheel.Value = _timer.LastTimer;
            }

            System.Windows.Automation.AutomationProperties.SetName(TimerStartButton, _idleMode switch
            {
                TimerMode.Pomodoro => Tr.Text("Mulai fokus", "Start focus"),
                TimerMode.Stopwatch => Tr.Text("Mulai stopwatch", "Start the stopwatch"),
                _ => Tr.Text("Mulai timer", "Start the timer")
            });
            var shown = _idleMode switch
            {
                TimerMode.Pomodoro => NotchTimer.Format(_timer.FocusLength),
                TimerMode.Stopwatch => NotchTimer.FormatPrecise(TimeSpan.Zero),
                _ => NotchTimer.Format(TimerWheel.Value)
            };
            TimerIdleDigits.Text = result ?? shown;
            ShowTyped(TimerIdleCaption, typed, string.Empty, TimerQuietBrush);
            TimerModeTimer.Tag = _idleMode == TimerMode.Timer ? "selected" : null;
            TimerModePomodoro.Tag = _idleMode == TimerMode.Pomodoro ? "selected" : null;
            TimerModeStopwatch.Tag = _idleMode == TimerMode.Stopwatch ? "selected" : null;
            TimerModeAlarm.Tag = typed.StartsWith('@') ? "selected" : null;
        }

        RenderPageDots();
    }

    // The label slot shows the typed text (white, with a caret) while typing, else its usual label.
    private static void ShowTyped(TextBlock slot, string typed, string label, Brush labelBrush)
    {
        slot.Text = typed.Length > 0 ? typed + "▏" : label;
        slot.Foreground = typed.Length > 0 ? System.Windows.Media.Brushes.White : labelBrush;
        slot.Visibility = slot.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the number would read after Enter (null: the command doesn't change the time shown).</summary>
    private string? CommandResult(TimerCommand command, DateTimeOffset now) => command.Kind switch
    {
        TimerCommandKind.Timer => NotchTimer.Format(command.Duration),
        TimerCommandKind.Adjust when _timer.IsActive && _timer.IsCountdown =>
            NotchTimer.Format(_timer.Remaining(now) + command.Duration > TimeSpan.Zero ? _timer.Remaining(now) + command.Duration : TimeSpan.Zero),
        TimerCommandKind.Alarm => AlarmClock(NotchTimer.NextOccurrence(command.Duration, now)),
        TimerCommandKind.Pomodoro => NotchTimer.Format(_timer.FocusLength),
        TimerCommandKind.PomodoroLengths => NotchTimer.Format(command.Duration),
        TimerCommandKind.Stopwatch => NotchTimer.FormatPrecise(TimeSpan.Zero),
        TimerCommandKind.ShowTimer => NotchTimer.Format(_timer.LastTimer),
        TimerCommandKind.Snooze or TimerCommandKind.ShowAlarm when _timer.AlarmAt is { } at => AlarmClock(at),
        _ => null
    };

    // Words only where an icon can't say it: a label the person typed, or which lap it is.
    private string TimerCaptionText() => _timer.Mode == TimerMode.Stopwatch
        ? (_timer.Laps.Count > 0 ? (_timer.Laps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : _timer.Label)
        : _timer.Label;

    private Geometry? TimerCaptionIcon_() => _timer.Mode switch
    {
        TimerMode.Stopwatch => _timer.Laps.Count > 0 ? LapIcon : null,
        TimerMode.Pomodoro => _timer.Phase == PomodoroPhase.Focus ? FocusIcon : BreakIcon,
        _ => TimerIcon
    };

    private void TimerPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click anywhere on the page makes it take typing (buttons keep working: they don't take focus).
        Activate();
        TimerInput.Focus();
    }

    private void TimerPlay_Click(object sender, RoutedEventArgs e)
    {
        _timer.Toggle(DateTimeOffset.Now);
        TimerChanged();
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        _timer.Reset();
        TimerChanged();
    }

    private void TimerWatchPlay_Click(object sender, RoutedEventArgs e)
    {
        _watch.Toggle(DateTimeOffset.Now);
        TimerChanged();
    }

    private void TimerWatchReset_Click(object sender, RoutedEventArgs e)
    {
        _watch.Reset();
        TimerChanged();
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        if (TimerInput.Text.Trim().Length > 0)
        {
            SubmitTimerInput();   // something typed goes first, as if Enter was pressed
            return;
        }

        var now = DateTimeOffset.Now;
        switch (_idleMode)
        {
            case TimerMode.Pomodoro:
                _timer.StartPomodoro(now, PomodoroPhase.Focus);
                break;
            case TimerMode.Stopwatch:
                _watch.StartStopwatch(now);
                break;
            default:
                if (TimerWheel.Value > TimeSpan.Zero)
                {
                    _timer.StartTimer(TimerWheel.Value, now);
                }
                break;
        }

        TimerChanged();
    }

    private void TimerMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender == TimerModeAlarm)
        {
            // An alarm is a time of day: start the command for it and let the person type the time.
            Activate();
            TimerInput.Text = "@";
            TimerInput.CaretIndex = 1;
            TimerInput.Focus();
            return;
        }

        _idleMode = sender == TimerModePomodoro ? TimerMode.Pomodoro : sender == TimerModeStopwatch ? TimerMode.Stopwatch : TimerMode.Timer;
        TimerInput.Clear();
        RenderTimer();
    }

    private void TimerAlarmCancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.CancelAlarm();
        TimerChanged();
    }

    // ---- Typing: AllTime's commands ---------------------------------------------------------

    private void TimerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        System.Windows.Automation.AutomationProperties.SetHelpText(TimerInput, CommandPreview(TimerCommandParser.Parse(TimerInput.Text)));
        RenderTimer();
    }

    /// <summary>What Enter will do, in words — so a command never surprises.</summary>
    private string CommandPreview(TimerCommand command)
    {
        var now = DateTimeOffset.Now;
        string Minutes(TimeSpan span) => span.TotalSeconds < 60 || span.TotalSeconds % 60 != 0
            ? NotchTimer.Format(span.Duration())
            : string.Format(Tr.Text("{0} menit", "{0} min"), (int)Math.Abs(span.TotalMinutes));
        var alarmAt = NotchTimer.NextOccurrence(command.Duration, now);
        return command.Kind switch
        {
            TimerCommandKind.Timer => $"Timer {NotchTimer.Format(command.Duration)}" + (command.Label.Length > 0 ? $" · {command.Label}" : string.Empty),
            TimerCommandKind.Adjust when !_timer.IsActive || !_timer.IsCountdown => Tr.Text("Tidak ada timer berjalan", "No timer running"),
            TimerCommandKind.Adjust => (command.Duration < TimeSpan.Zero ? Tr.Text("Kurangi ", "Take off ") : Tr.Text("Tambah ", "Add ")) + Minutes(command.Duration),
            TimerCommandKind.Alarm => $"Alarm {AlarmClock(alarmAt)}" +
                (alarmAt.Date > now.Date ? Tr.Text(" besok", " tomorrow") : string.Empty) +
                (command.Label.Length > 0 ? $" · {command.Label}" : string.Empty),
            TimerCommandKind.Pomodoro => string.Format(Tr.Text("Fokus {0}", "Focus {0}"), Minutes(_timer.FocusLength)),
            TimerCommandKind.PomodoroLengths => string.Format(CultureInfo.InvariantCulture, "Pomodoro {0}/{1}/{2}",
                (int)command.Duration.TotalMinutes,
                (int)(command.ShortBreak ?? _timer.ShortBreakLength).TotalMinutes,
                (int)(command.LongBreak ?? _timer.LongBreakLength).TotalMinutes),
            TimerCommandKind.Stopwatch => Tr.Text("Mulai stopwatch", "Start the stopwatch"),
            TimerCommandKind.ShowTimer => $"Timer {NotchTimer.Format(_timer.LastTimer)}",
            TimerCommandKind.ShowAlarm => _timer.AlarmAt is { } at
                ? $"Alarm {AlarmClock(at)}"
                : Tr.Text("Belum ada alarm — ketik @7:30", "No alarm — type @7:30"),
            TimerCommandKind.Toggle => (_timer.IsActive ? _timer : _watch).Run == TimerRun.Running ? Tr.Text("Jeda", "Pause") : Tr.Text("Mulai", "Start"),
            TimerCommandKind.Reset => !_timer.IsActive && _watch.IsActive ? "Reset" : Tr.Text("Hentikan", "Stop"),
            TimerCommandKind.Skip => _timer.Mode == TimerMode.Pomodoro
                ? _timer.Phase == PomodoroPhase.Focus ? Tr.Text("Lewati ke istirahat", "Skip to the break") : Tr.Text("Lewati ke fokus", "Skip to focus")
                : Tr.Text("Hentikan", "Stop"),
            TimerCommandKind.Lap => _watch.IsActive
                ? Tr.Text("Catat lap", "Record a lap") : Tr.Text("Stopwatch tidak berjalan", "No stopwatch running"),
            TimerCommandKind.Snooze => Tr.Text("Tunda alarm 9 menit", "Snooze the alarm 9 min"),
            TimerCommandKind.CancelAlarm => _timer.AlarmAt is null ? Tr.Text("Belum ada alarm", "No alarm set") : Tr.Text("Hapus alarm", "Clear the alarm"),
            TimerCommandKind.Label => string.Format(Tr.Text("Label \"{0}\"", "Label \"{0}\""), command.Label),
            _ => string.Empty
        };
    }

    private void TimerInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SubmitTimerInput();
    }

    private void SubmitTimerInput()
    {
        var command = TimerCommandParser.Parse(TimerInput.Text);
        TimerInput.Clear();
        if (command.Kind != TimerCommandKind.None)
        {
            RunTimerCommand(command);
        }
    }

    /// <summary>Esc with something typed clears it (instead of closing the notch).</summary>
    private bool ClearTimerInput()
    {
        if (!TimerInput.IsKeyboardFocused || TimerInput.Text.Length == 0)
        {
            return false;
        }

        TimerInput.Clear();
        return true;
    }

    private void RunTimerCommand(TimerCommand command)
    {
        var now = DateTimeOffset.Now;
        switch (command.Kind)
        {
            case TimerCommandKind.Timer:
                _timer.StartTimer(command.Duration, now, command.Label);
                break;
            case TimerCommandKind.Adjust:
                _timer.Add(command.Duration, now);
                break;
            case TimerCommandKind.Pomodoro:
                if (_timer.Mode != TimerMode.Pomodoro || !_timer.IsActive)
                {
                    _timer.StartPomodoro(now, PomodoroPhase.Focus);
                }
                break;
            case TimerCommandKind.PomodoroLengths:
                _timer.SetPomodoro(command.Duration, command.ShortBreak, command.LongBreak);
                if (!_timer.IsActive)
                {
                    _timer.StartPomodoro(now, PomodoroPhase.Focus);
                }
                break;
            case TimerCommandKind.Stopwatch:
                _watch.StartStopwatch(now);
                break;
            case TimerCommandKind.ShowTimer:
                _timer.Select(TimerMode.Timer);
                break;
            case TimerCommandKind.ShowAlarm:
                break;
            case TimerCommandKind.Toggle:
                if (_timer.IsActive)
                {
                    _timer.Toggle(now);
                }
                else if (_watch.IsActive)
                {
                    _watch.Toggle(now);
                }
                else
                {
                    _timer.StartTimer(TimerWheel.Value > TimeSpan.Zero ? TimerWheel.Value : _timer.LastTimer, now);
                }
                break;
            case TimerCommandKind.Reset:
                if (_timer.IsActive)
                {
                    _timer.Reset();
                }
                else
                {
                    _watch.Reset();
                }
                break;
            case TimerCommandKind.Skip:
                _timer.Skip(now);
                break;
            case TimerCommandKind.Lap:
                _watch.Lap(now);
                break;
            case TimerCommandKind.Alarm:
                _timer.SetAlarm(NotchTimer.NextOccurrence(command.Duration, now), command.Label);
                break;
            case TimerCommandKind.CancelAlarm:
                _timer.CancelAlarm();
                break;
            case TimerCommandKind.Snooze:
                if (_timerDone?.Mode == TimerMode.Alarm)
                {
                    SnoozeRinging();
                    return;
                }
                break;
            case TimerCommandKind.Label:
                _timer.SetLabel(command.Label);
                break;
            default:
                return;
        }

        TimerChanged();
    }

    // ---- Time's up / the alarm rings ---------------------------------------------------------

    private void OnTimerFinished(TimerFinished finished)
    {
        if (!_timerPreview)
        {
            CompletionNotifier.PlaySound();
        }

        _timerDone = finished;
        var alarm = finished.Mode == TimerMode.Alarm;
        var accent = finished.Mode == TimerMode.Pomodoro && finished.Phase == PomodoroPhase.Focus ? TimerBreakBrush : TimerFocusBrush;
        TimerDoneRow.Visibility = alarm ? Visibility.Collapsed : Visibility.Visible;
        TimerRingRow.Visibility = alarm ? Visibility.Visible : Visibility.Collapsed;
        if (alarm)
        {
            TimerRingCaption.Text = finished.Label;
            TimerRingCaption.Visibility = finished.Label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TimerRingTime.Margin = new Thickness(finished.Label.Length > 0 ? 16 : 0, 0, 0, 0);
            TimerRingTime.Text = AlarmClock(finished.At);
        }
        else
        {
            // An icon and a number say what the round button starts next: a cup and 5, a target and 25.
            var nextBreak = finished.Mode == TimerMode.Pomodoro && finished.Phase == PomodoroPhase.Focus;
            var nextLength = finished.Mode == TimerMode.Timer ? finished.Duration
                : nextBreak ? _timer.PhaseLength(_timer.Phase == PomodoroPhase.LongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak)
                : _timer.FocusLength;
            TimerDoneIcon.Data = finished.Mode == TimerMode.Timer ? TimerIcon : nextBreak ? BreakIcon : FocusIcon;
            TimerDoneTitle.Text = finished.Label.Length > 0 && finished.Mode == TimerMode.Timer
                ? finished.Label
                : nextLength.TotalMinutes >= 1 && nextLength.TotalSeconds % 60 == 0
                    ? ((int)nextLength.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : NotchTimer.Format(nextLength);
            System.Windows.Automation.AutomationProperties.SetName(TimerDoneNext, finished.Mode == TimerMode.Timer
                ? Tr.Text("Ulangi", "Repeat") : nextBreak ? Tr.Text("Mulai istirahat", "Start the break") : Tr.Text("Fokus lagi", "Focus again"));
            TimerDoneTitle.Foreground = TimerQuietBrush;
            TimerDoneDigits.Foreground = accent;
            TimerDoneNextGlyph.Stroke = accent;
            TimerDoneNext.Background = Tint(accent, 0x38);
        }

        TimerRipple1.Stroke = accent;
        TimerRipple2.Stroke = accent;

        ApplyMusicCompact();
        if (HasPendingRequest || _suspendedForChat)
        {
            // An approval waits, or the chat covers the notch: the sound has to do.
            _timerDone = null;
            TimerChanged();
            return;
        }

        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _musicTick.Stop();
        _view = NotchView.TimerDone;
        ApplyView(animate: true);
        UpdateVisibility();
        _celebrationStarted = DateTime.UtcNow;
        Pulse();
        _pulseTimer.Start();
        UpdateTimerTick();
    }

    // One beat: the shape springs out a little and back, the zero fades (the bell shakes), two rings spread.
    private void Pulse()
    {
        if (_view != NotchView.TimerDone)
        {
            StopCelebration();
            return;
        }

        var width = _shapeWidth.Target;
        var height = _shapeHeight.Target;
        Retarget(_shapeWidth, width + 26, 0.3, 0.5);
        Retarget(_shapeHeight, height + 10, 0.3, 0.5);
        StartMotion();
        var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            if (_view == NotchView.TimerDone)
            {
                Retarget(_shapeWidth, width, 0.5, 0.55);
                Retarget(_shapeHeight, height, 0.5, 0.55);
                StartMotion();
            }
        };
        back.Start();

        if (_timerDone?.Mode == TimerMode.Alarm)
        {
            // The bell rings: a quick shake, then still.
            if (TimerRingIcon.RenderTransform is RotateTransform shake)
            {
                var ring = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
                foreach (var (angle, ms) in new[] { (0.0, 0), (-14.0, 60), (14.0, 140), (-12.0, 220), (12.0, 300), (-8.0, 380), (8.0, 460), (0.0, 560) })
                {
                    ring.KeyFrames.Add(new LinearDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
                }

                shake.BeginAnimation(RotateTransform.AngleProperty, ring);
            }
        }
        else
        {
            // The zero fades out and back in once per beat (within the 2 s an animation may take).
            TimerDoneDigits.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        // Rings only where there is room under the notch: docked to the top edge.
        if (Edge == "top")
        {
            TimerRipples.Margin = new Thickness(0, height + Shell.Margin.Top, 0, 0);
            TimerRipples.Visibility = Visibility.Visible;
            Ripple(TimerRipple1, TimeSpan.Zero);
            Ripple(TimerRipple2, TimeSpan.FromMilliseconds(450));
        }
    }

    private static void Ripple(FrameworkElement ring, TimeSpan delay)
    {
        var length = TimeSpan.FromMilliseconds(1300);
        var grow = new DoubleAnimation(0.5, 2.6, length) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.85, 0, length) { BeginTime = delay };
        if (ring.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        ring.BeginAnimation(OpacityProperty, fade);
    }

    private void StopCelebration()
    {
        _pulseTimer.Stop();
        TimerRipples.Visibility = Visibility.Collapsed;
        if (_timerDone is not null)
        {
            _timerDone = null;
            ApplyKeyline();
            UpdateTimerTick();
        }
    }

    private void TimerDoneNext_Click(object sender, RoutedEventArgs e)
    {
        var finished = _timerDone;
        StopCelebration();
        var now = DateTimeOffset.Now;
        if (finished?.Mode == TimerMode.Timer)
        {
            _timer.StartTimer(finished.Duration, now, finished.Label);
        }
        else
        {
            _timer.StartPomodoro(now);   // the phase already moved on
        }

        TimerChanged();
        Collapse();
    }

    private void TimerDoneClose_Click(object sender, RoutedEventArgs e)
    {
        StopCelebration();
        Collapse();
    }

    private void TimerSnooze_Click(object sender, RoutedEventArgs e) => SnoozeRinging();

    private void SnoozeRinging()
    {
        var label = _timerDone?.Label ?? string.Empty;
        StopCelebration();
        _timer.Snooze(DateTimeOffset.Now, label);
        TimerChanged();
        Collapse();
    }

    internal async void PreviewTimer(string mode)
    {
        _timerPreview = true;
        var now = DateTimeOffset.Now;
        _timer = NotchTimer.Restore(null);
        _watch = NotchTimer.Restore(null);
        switch (mode)
        {
            case "stopwatch":
                _watch.StartStopwatch(now - TimeSpan.FromMilliseconds(5080));
                break;
            case "two":
                _timer.StartPomodoro(now - TimeSpan.FromSeconds(48), PomodoroPhase.Focus);
                _watch.StartStopwatch(now - TimeSpan.FromMilliseconds(5080));
                break;
            case "alarm":
                _timer.SetAlarm(new DateTimeOffset(now.Date.AddDays(1).AddHours(7.75), now.Offset), "Bangun");
                break;
            case "idle":
                _timer.Select(TimerMode.Timer, TimeSpan.FromMinutes(15));
                _idleMode = TimerMode.Timer;
                break;
            case "ring":
                break;
            default:
                _timer.StartPomodoro(now - TimeSpan.FromSeconds(48), PomodoroPhase.Focus);
                break;
        }

        if (mode is "done" or "pulse")
        {
            if (_timer.Tick(now + _timer.FocusLength) is { } finished)
            {
                OnTimerFinished(finished);
            }

            if (mode == "done")
            {
                _pulseTimer.Stop();
            }
        }
        else if (mode == "ring")
        {
            OnTimerFinished(new TimerFinished(TimerMode.Alarm, PomodoroPhase.Focus, TimeSpan.Zero, string.Empty,
                new DateTimeOffset(now.Date.AddHours(7.75), now.Offset)));
            _pulseTimer.Stop();
        }
        else
        {
            TimerChanged();
            if (mode is "open" or "idle" or "stopwatch" or "alarm" or "typing" or "two")
            {
                OpenTimer(focus: mode == "typing");
                _pinned = true;
                if (mode == "typing")
                {
                    TimerInput.Text = "+5";
                    TimerInput.CaretIndex = 2;
                }
            }
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            if (mode == "pulse")
            {
                await Task.Delay(1600 + 1600 - 1500 + 380);   // mid-beat of the second pulse
                SnapshotShell(System.IO.Path.Combine(dir, "notch-timer-pulse.png"), Root);
                return;
            }

            SnapshotShell(System.IO.Path.Combine(dir, $"notch-timer-{mode}.png"));
        }
    }
}
