using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

public enum TimerMode { Pomodoro, Timer, Stopwatch, Alarm }

public enum TimerRun { Idle, Running, Paused }

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

/// <summary>A countdown that reached zero, or an alarm that went off: what it was, so the notch can say so and offer the next step.</summary>
public sealed record TimerFinished(TimerMode Mode, PomodoroPhase Phase, TimeSpan Duration, string Label, DateTimeOffset At = default);

/// <summary>Everything needed to carry the timer and the alarm across a restart.</summary>
public sealed class TimerState
{
    public TimerMode Mode { get; set; }
    public TimerRun Run { get; set; }
    public PomodoroPhase Phase { get; set; }
    public int CompletedFocus { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public double PausedRemainingSeconds { get; set; }
    public double StopwatchSeconds { get; set; }
    public DateTimeOffset StopwatchSince { get; set; }
    public double LastTimerSeconds { get; set; }
    public string Label { get; set; } = string.Empty;
    public double FocusMinutes { get; set; }
    public double ShortBreakMinutes { get; set; }
    public double LongBreakMinutes { get; set; }
    public DateTimeOffset? AlarmAt { get; set; }
    public string AlarmLabel { get; set; } = string.Empty;
    public List<double> LapSeconds { get; set; } = [];
}

/// <summary>
/// The notch's own timer: a pomodoro cycle (focus, short break, a long break every fourth
/// focus), a plain countdown and a stopwatch — one of those at a time — plus one alarm at a time
/// of day beside it. Time is kept as end instants rather than ticking counters, so sleep, a busy
/// UI thread or a restart lose nothing.
/// </summary>
public sealed class NotchTimer
{
    public static readonly TimeSpan DefaultFocus = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan DefaultShortBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLongBreak = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SnoozeLength = TimeSpan.FromMinutes(9);
    public const int FocusPerLongBreak = 4;

    public TimerMode Mode { get; private set; } = TimerMode.Pomodoro;
    public TimerRun Run { get; private set; }
    public PomodoroPhase Phase { get; private set; }

    /// <summary>Focus sessions finished in the current cycle of four.</summary>
    public int CompletedFocus { get; private set; }

    /// <summary>Length of the running (or paused) countdown.</summary>
    public TimeSpan Duration { get; private set; }

    public string Label { get; private set; } = string.Empty;

    public TimeSpan FocusLength { get; private set; } = DefaultFocus;
    public TimeSpan ShortBreakLength { get; private set; } = DefaultShortBreak;
    public TimeSpan LongBreakLength { get; private set; } = DefaultLongBreak;

    /// <summary>When the alarm goes off (null: none set).</summary>
    public DateTimeOffset? AlarmAt { get; private set; }

    public string AlarmLabel { get; private set; } = string.Empty;

    /// <summary>Stopwatch laps, as total time at each lap, oldest first.</summary>
    public IReadOnlyList<TimeSpan> Laps => _laps;

    private readonly List<TimeSpan> _laps = [];
    private DateTimeOffset _endsAt;
    private TimeSpan _pausedRemaining;
    private TimeSpan _stopwatch;
    private DateTimeOffset _stopwatchSince;
    private TimeSpan _lastTimer = TimeSpan.FromMinutes(5);

    public bool IsActive => Run != TimerRun.Idle;

    public bool IsCountdown => Mode is TimerMode.Pomodoro or TimerMode.Timer;

    /// <summary>Length of the countdown the timer mode starts with (the last one used).</summary>
    public TimeSpan LastTimer => _lastTimer;

    public DateTimeOffset EndsAt => _endsAt;

    public TimeSpan Remaining(DateTimeOffset now) => Run switch
    {
        TimerRun.Running when IsCountdown => Max(_endsAt - now, TimeSpan.Zero),
        TimerRun.Paused when IsCountdown => _pausedRemaining,
        _ => IsCountdown ? Duration : TimeSpan.Zero
    };

    /// <summary>Stopwatch time, or how much of the countdown has passed.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => Mode == TimerMode.Stopwatch
        ? _stopwatch + (Run == TimerRun.Running ? Max(now - _stopwatchSince, TimeSpan.Zero) : TimeSpan.Zero)
        : Duration - Remaining(now);

    /// <summary>0–1 share of the countdown that has passed (a minute's sweep for the stopwatch).</summary>
    public double Progress(DateTimeOffset now)
    {
        if (Mode == TimerMode.Stopwatch)
        {
            return Elapsed(now).TotalSeconds % 60 / 60;
        }

        return Duration > TimeSpan.Zero ? Math.Clamp(Elapsed(now) / Duration, 0, 1) : 0;
    }

    /// <summary>The time to show: what is left of a countdown, what has passed on the stopwatch.</summary>
    public TimeSpan Display(DateTimeOffset now) => Mode == TimerMode.Stopwatch ? Elapsed(now) : Remaining(now);

    public TimeSpan PhaseLength(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => ShortBreakLength,
        PomodoroPhase.LongBreak => LongBreakLength,
        _ => FocusLength
    };

    /// <summary>The pomodoro lengths ("p50/10/20"); a waiting pomodoro shows the new length at once.</summary>
    public void SetPomodoro(TimeSpan focus, TimeSpan? shortBreak = null, TimeSpan? longBreak = null)
    {
        FocusLength = focus;
        ShortBreakLength = shortBreak ?? ShortBreakLength;
        LongBreakLength = longBreak ?? LongBreakLength;
        if (Mode == TimerMode.Pomodoro && !IsActive)
        {
            Duration = PhaseLength(Phase);
        }
    }

    public void StartPomodoro(DateTimeOffset now, PomodoroPhase? phase = null, string? label = null)
    {
        Mode = TimerMode.Pomodoro;
        Phase = phase ?? Phase;
        StartCountdown(PhaseLength(Phase), now, label);
    }

    public void StartTimer(TimeSpan duration, DateTimeOffset now, string? label = null)
    {
        Mode = TimerMode.Timer;
        _lastTimer = duration;
        StartCountdown(duration, now, label);
    }

    public void StartStopwatch(DateTimeOffset now, string? label = null)
    {
        Mode = TimerMode.Stopwatch;
        Run = TimerRun.Running;
        Duration = TimeSpan.Zero;
        _stopwatch = TimeSpan.Zero;
        _stopwatchSince = now;
        _laps.Clear();
        Label = label ?? string.Empty;
    }

    private void StartCountdown(TimeSpan duration, DateTimeOffset now, string? label)
    {
        Duration = duration;
        _endsAt = now + duration;
        Run = TimerRun.Running;
        // A pomodoro keeps its label through the cycle; a new timer starts unlabelled.
        Label = label ?? (Mode == TimerMode.Pomodoro ? Label : string.Empty);
    }

    /// <summary>Pause or resume; when nothing runs, start the shown mode from the top.</summary>
    public void Toggle(DateTimeOffset now)
    {
        switch (Run)
        {
            case TimerRun.Running:
                if (Mode == TimerMode.Stopwatch)
                {
                    _stopwatch = Elapsed(now);
                }
                else
                {
                    _pausedRemaining = Remaining(now);
                }

                Run = TimerRun.Paused;
                break;

            case TimerRun.Paused:
                if (Mode == TimerMode.Stopwatch)
                {
                    _stopwatchSince = now;
                }
                else
                {
                    _endsAt = now + _pausedRemaining;
                }

                Run = TimerRun.Running;
                break;

            default:
                Start(Mode, now);
                break;
        }
    }

    /// <summary>Start a mode with its usual length (pomodoro phase, last timer, stopwatch from zero).</summary>
    public void Start(TimerMode mode, DateTimeOffset now)
    {
        switch (mode)
        {
            case TimerMode.Pomodoro:
                StartPomodoro(now);
                break;
            case TimerMode.Stopwatch:
                StartStopwatch(now);
                break;
            default:
                StartTimer(_lastTimer, now);
                break;
        }
    }

    /// <summary>Show another mode without starting it (only while nothing runs).</summary>
    public void Select(TimerMode mode, TimeSpan? length = null)
    {
        if (IsActive || mode == TimerMode.Alarm)
        {
            return;
        }

        Mode = mode;
        if (mode == TimerMode.Timer && length is { } chosen && chosen > TimeSpan.Zero)
        {
            _lastTimer = chosen;
        }

        Duration = mode switch
        {
            TimerMode.Pomodoro => PhaseLength(Phase),
            TimerMode.Timer => _lastTimer,
            _ => TimeSpan.Zero
        };
    }

    public void Reset()
    {
        Run = TimerRun.Idle;
        _stopwatch = TimeSpan.Zero;
        _laps.Clear();
        Duration = Mode switch
        {
            TimerMode.Pomodoro => PhaseLength(Phase),
            TimerMode.Timer => _lastTimer,
            _ => TimeSpan.Zero
        };
    }

    /// <summary>Pomodoro: go to the next phase now and run it.</summary>
    public void Skip(DateTimeOffset now)
    {
        if (Mode != TimerMode.Pomodoro)
        {
            Reset();
            return;
        }

        Phase = NextPhase(countFocus: false);
        StartCountdown(PhaseLength(Phase), now, null);
    }

    /// <summary>Lengthen (or shorten) the running countdown; never below zero.</summary>
    public void Add(TimeSpan amount, DateTimeOffset now)
    {
        if (!IsCountdown || !IsActive)
        {
            return;
        }

        var remaining = Max(Remaining(now) + amount, TimeSpan.Zero);
        Duration = Max(Duration + amount, remaining);
        if (Run == TimerRun.Running)
        {
            _endsAt = now + remaining;
        }
        else
        {
            _pausedRemaining = remaining;
        }
    }

    /// <summary>Stopwatch: note the time now. Returns false when there is no running stopwatch.</summary>
    public bool Lap(DateTimeOffset now)
    {
        if (Mode != TimerMode.Stopwatch || !IsActive)
        {
            return false;
        }

        _laps.Add(Elapsed(now));
        return true;
    }

    public void SetAlarm(DateTimeOffset at, string? label = null)
    {
        AlarmAt = at;
        AlarmLabel = label?.Trim() ?? string.Empty;
    }

    public void CancelAlarm()
    {
        AlarmAt = null;
        AlarmLabel = string.Empty;
    }

    /// <summary>Ring again in nine minutes, like a bedside clock.</summary>
    public void Snooze(DateTimeOffset now, string label) => SetAlarm(now + SnoozeLength, label);

    /// <summary>The next moment a clock time comes round: today if still ahead, else tomorrow.</summary>
    public static DateTimeOffset NextOccurrence(TimeSpan timeOfDay, DateTimeOffset now)
    {
        var at = new DateTimeOffset(now.Date + timeOfDay, now.Offset);
        return at > now ? at : at.AddDays(1);
    }

    /// <summary>Call regularly: returns what finished — an alarm going off, or a countdown reaching zero.</summary>
    public TimerFinished? Tick(DateTimeOffset now)
    {
        if (AlarmAt is { } alarm && now >= alarm)
        {
            var rang = new TimerFinished(TimerMode.Alarm, Phase, TimeSpan.Zero, AlarmLabel, alarm);
            CancelAlarm();
            return rang;
        }

        if (Run != TimerRun.Running || !IsCountdown || now < _endsAt)
        {
            return null;
        }

        var finished = new TimerFinished(Mode, Phase, Duration, Label);
        Run = TimerRun.Idle;
        if (Mode == TimerMode.Pomodoro)
        {
            Phase = NextPhase(countFocus: true);
            Duration = PhaseLength(Phase);
        }

        return finished;
    }

    private PomodoroPhase NextPhase(bool countFocus)
    {
        if (Phase != PomodoroPhase.Focus)
        {
            if (Phase == PomodoroPhase.LongBreak)
            {
                CompletedFocus = 0;
            }

            return PomodoroPhase.Focus;
        }

        if (countFocus)
        {
            CompletedFocus++;
        }

        return CompletedFocus >= FocusPerLongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
    }

    public void SetLabel(string label) => Label = label.Trim();

    public TimerState Save() => new()
    {
        Mode = Mode,
        Run = Run,
        Phase = Phase,
        CompletedFocus = CompletedFocus,
        DurationSeconds = Duration.TotalSeconds,
        EndsAt = _endsAt,
        PausedRemainingSeconds = _pausedRemaining.TotalSeconds,
        StopwatchSeconds = _stopwatch.TotalSeconds,
        StopwatchSince = _stopwatchSince,
        LastTimerSeconds = _lastTimer.TotalSeconds,
        Label = Label,
        FocusMinutes = FocusLength.TotalMinutes,
        ShortBreakMinutes = ShortBreakLength.TotalMinutes,
        LongBreakMinutes = LongBreakLength.TotalMinutes,
        AlarmAt = AlarmAt,
        AlarmLabel = AlarmLabel,
        LapSeconds = _laps.Select(lap => lap.TotalSeconds).ToList()
    };

    public static NotchTimer Restore(TimerState? state)
    {
        var timer = new NotchTimer();
        if (state is null)
        {
            timer.Duration = timer.FocusLength;
            return timer;
        }

        static TimeSpan Minutes(double value, TimeSpan fallback) => value > 0 ? TimeSpan.FromMinutes(value) : fallback;
        timer.FocusLength = Minutes(state.FocusMinutes, DefaultFocus);
        timer.ShortBreakLength = Minutes(state.ShortBreakMinutes, DefaultShortBreak);
        timer.LongBreakLength = Minutes(state.LongBreakMinutes, DefaultLongBreak);
        timer.Mode = state.Mode == TimerMode.Alarm ? TimerMode.Timer : state.Mode;
        timer.Run = state.Run;
        timer.Phase = state.Phase;
        timer.CompletedFocus = Math.Clamp(state.CompletedFocus, 0, FocusPerLongBreak);
        timer.Duration = TimeSpan.FromSeconds(Math.Max(0, state.DurationSeconds));
        timer._endsAt = state.EndsAt;
        timer._pausedRemaining = TimeSpan.FromSeconds(Math.Max(0, state.PausedRemainingSeconds));
        timer._stopwatch = TimeSpan.FromSeconds(Math.Max(0, state.StopwatchSeconds));
        timer._stopwatchSince = state.StopwatchSince;
        timer._lastTimer = state.LastTimerSeconds > 0 ? TimeSpan.FromSeconds(state.LastTimerSeconds) : TimeSpan.FromMinutes(5);
        timer.Label = state.Label ?? string.Empty;
        timer.AlarmAt = state.AlarmAt;
        timer.AlarmLabel = state.AlarmLabel ?? string.Empty;
        timer._laps.AddRange((state.LapSeconds ?? []).Select(TimeSpan.FromSeconds));
        return timer;
    }

    /// <summary>"24:12", "1:05:00" — minutes and seconds, hours only when there are some.</summary>
    public static string Format(TimeSpan time)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, time.TotalSeconds) - 0.0001);
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        var rest = seconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>Stopwatch time with hundredths: "00:05.08", "1:02:03.40".</summary>
    public static string FormatPrecise(TimeSpan time)
    {
        var ticks = Math.Max(0, time.Ticks);
        var total = TimeSpan.FromTicks(ticks);
        var hundredths = total.Milliseconds / 10;
        return total.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}.{hundredths:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total.Minutes:00}:{total.Seconds:00}.{hundredths:00}");
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

public enum TimerCommandKind
{
    None, Timer, Adjust, Pomodoro, PomodoroLengths, Stopwatch, ShowTimer, ShowAlarm,
    Toggle, Reset, Skip, Lap, Alarm, CancelAlarm, Snooze, Label
}

/// <summary>What was typed in the notch's timer.</summary>
public sealed record TimerCommand(
    TimerCommandKind Kind,
    TimeSpan Duration = default,
    string Label = "",
    TimeSpan? ShortBreak = null,
    TimeSpan? LongBreak = null);

/// <summary>
/// The timer's command language, after AllTime's: a length starts a timer ("25", "5m", "90s",
/// "2h", "1h30", "1:30" = 1 h 30, optionally followed by a label); "+5" / "-2" / "+30s" adjust the
/// running one; "@7:30" / "@19:05 rapat" / "@7pm" set an alarm; "p", "tm", "sw", "a" jump to
/// pomodoro / timer / stopwatch / alarm; "p25", "p25/5", "p25/5/15" set the pomodoro lengths;
/// "go", "skip", "lap", "stop", "snooze", "x" control it. Anything else becomes the label —
/// nothing has to be learnt first, and a typo can't break anything.
/// </summary>
public static partial class TimerCommandParser
{
    [GeneratedRegex(@"^(?:(?<h>\d{1,2})h(?<hm>\d{1,2})?m?|(?<hh>\d{1,2}):(?<mm>\d{2})|(?<n>\d{1,4})(?<u>s|m|h|d|j)?)(?:\s+(?<label>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Length();

    [GeneratedRegex(@"^(?<sign>[+-])\s*(?<n>\d{1,4})(?<u>s|m|h)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Adjust();

    [GeneratedRegex(@"^@\s*(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ampm>am|pm)?(?:\s+(?<label>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Alarm();

    [GeneratedRegex(@"^p\s*(?<f>\d{1,3})(?:/(?<s>\d{1,3}))?(?:/(?<l>\d{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pomodoro();

    public static TimerCommand Parse(string? text)
    {
        var input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            return new TimerCommand(TimerCommandKind.None);
        }

        switch (input.ToLowerInvariant())
        {
            case "p":
            case "pomo":
            case "pomodoro":
            case "fokus":
            case "focus":
                return new TimerCommand(TimerCommandKind.Pomodoro);
            case "tm":
            case "timer":
                return new TimerCommand(TimerCommandKind.ShowTimer);
            case "sw":
            case "stopwatch":
                return new TimerCommand(TimerCommandKind.Stopwatch);
            case "a":
            case "alarm":
                return new TimerCommand(TimerCommandKind.ShowAlarm);
            case "go":
            case "start":
            case "mulai":
            case "jeda":
            case "pause":
                return new TimerCommand(TimerCommandKind.Toggle);
            case "stop":
            case "reset":
            case "batal":
                return new TimerCommand(TimerCommandKind.Reset);
            case "skip":
            case "lewati":
            case "next":
                return new TimerCommand(TimerCommandKind.Skip);
            case "lap":
            case "putaran":
                return new TimerCommand(TimerCommandKind.Lap);
            case "snooze":
            case "zz":
            case "tunda":
                return new TimerCommand(TimerCommandKind.Snooze);
            case "x":
            case "@x":
            case "@-":
                return new TimerCommand(TimerCommandKind.CancelAlarm);
        }

        if (Alarm().Match(input) is { Success: true } alarm)
        {
            var hour = int.Parse(alarm.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = alarm.Groups["m"].Success ? int.Parse(alarm.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (alarm.Groups["ampm"].Success)
            {
                var pm = alarm.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
                hour = hour % 12 + (pm ? 12 : 0);
            }

            if (hour < 24 && minute < 60)
            {
                return new TimerCommand(TimerCommandKind.Alarm, new TimeSpan(hour, minute, 0), alarm.Groups["label"].Value.Trim());
            }
        }

        if (Pomodoro().Match(input) is { Success: true } pomodoro)
        {
            static TimeSpan? Minutes(Group group) =>
                group.Success && int.Parse(group.Value, CultureInfo.InvariantCulture) is > 0 and var n ? TimeSpan.FromMinutes(n) : null;
            if (Minutes(pomodoro.Groups["f"]) is { } focus)
            {
                return new TimerCommand(TimerCommandKind.PomodoroLengths, focus, string.Empty,
                    Minutes(pomodoro.Groups["s"]), Minutes(pomodoro.Groups["l"]));
            }
        }

        if (Adjust().Match(input) is { Success: true } adjust)
        {
            var amount = Amount(adjust.Groups["n"].Value, adjust.Groups["u"].Value);
            return new TimerCommand(TimerCommandKind.Adjust, adjust.Groups["sign"].Value == "-" ? -amount : amount);
        }

        if (Length().Match(input) is { Success: true } length)
        {
            TimeSpan duration;
            if (length.Groups["h"].Success)
            {
                duration = TimeSpan.FromHours(int.Parse(length.Groups["h"].Value, CultureInfo.InvariantCulture)) +
                           TimeSpan.FromMinutes(length.Groups["hm"].Success ? int.Parse(length.Groups["hm"].Value, CultureInfo.InvariantCulture) : 0);
            }
            else if (length.Groups["hh"].Success)
            {
                duration = TimeSpan.FromHours(int.Parse(length.Groups["hh"].Value, CultureInfo.InvariantCulture)) +
                           TimeSpan.FromMinutes(int.Parse(length.Groups["mm"].Value, CultureInfo.InvariantCulture));
            }
            else
            {
                duration = Amount(length.Groups["n"].Value, length.Groups["u"].Value);
            }

            if (duration > TimeSpan.Zero && duration <= TimeSpan.FromHours(24))
            {
                return new TimerCommand(TimerCommandKind.Timer, duration, length.Groups["label"].Value.Trim());
            }
        }

        return new TimerCommand(TimerCommandKind.Label, Label: input);
    }

    private static TimeSpan Amount(string number, string unit)
    {
        var n = int.Parse(number, CultureInfo.InvariantCulture);
        return unit.ToLowerInvariant() switch
        {
            "s" or "d" => TimeSpan.FromSeconds(n), // "d" = detik
            "h" or "j" => TimeSpan.FromHours(n),   // "j" = jam
            _ => TimeSpan.FromMinutes(n)
        };
    }
}
