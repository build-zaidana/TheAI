using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace CodexBar.App;

// Today, recapped: focus sessions, what the agents finished, files changed, long terminal
// commands and PR runs — kept as they happen in a small local file per day (DayLog). The recap is
// a page of the open notch, invites itself once in the late afternoon, and copies as a few lines
// of Markdown for a standup.
public partial class NotchWindow
{
    private const double RecapWidth = IslandTokens.WidthM;
    private static readonly TimeSpan RecapEvening = new(17, 0, 0);

    // Dev previews run with sample agents and timers: none of that belongs in the user's day.
    private static readonly bool RecordingDay = !Environment.GetEnvironmentVariables().Keys.Cast<string>()
        .Any(key => key.StartsWith("CODEXBAR_PREVIEW_", StringComparison.OrdinalIgnoreCase));

    private readonly DispatcherTimer _daySave = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _recapClock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _recapCollapse = new() { Interval = TimeSpan.FromSeconds(15) };
    private DayLog? _day;

    // Loaded on first use: the music and timer pages ask for it before InitRecap has run.
    private DayLog Today
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_day is null)
            {
                _day = DayLog.Load(today);
            }
            else if (_day.Day != today)
            {
                SaveDay();
                _day = DayLog.Load(today);
            }

            return _day;
        }
    }

    private bool RecapAvailable => _host.RecapEvening && !Today.IsEmpty;

    private void InitRecap()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _ = Today;
        _ = Task.Run(() => DayLog.Prune(TimeSpan.FromDays(30), today));
        _daySave.Tick += (_, _) =>
        {
            _daySave.Stop();
            SaveDay();
        };
        _recapClock.Tick += (_, _) => RecapClock();
        ScheduleRecap();
        _recapCollapse.Tick += (_, _) =>
        {
            _recapCollapse.Stop();
            if (_view == NotchView.Recap && !Shell.IsMouseOver && !_pinned)
            {
                Collapse();
            }
        };
        Closed += (_, _) =>
        {
            _recapClock.Stop();
            SaveDay();
        };
    }

    private void SaveDay()
    {
        if (RecordingDay)
        {
            _day?.Save();
        }
    }

    /// <summary>Something for today's recap happened: note it (saved a moment later).</summary>
    private void RecordDay(Action<DayLog> change)
    {
        if (!RecordingDay)
        {
            return;
        }

        change(Today);
        _daySave.Stop();
        _daySave.Start();
        if (_view == NotchView.Recap)
        {
            RenderRecap();
        }
    }

    /// <summary>Wake at 5 pm (or a minute from now, if it's past and the notch was busy then).</summary>
    private void ScheduleRecap()
    {
        _recapClock.Stop();
        if (!_host.RecapEvening || !RecordingDay)
        {
            return;
        }

        var now = DateTime.Now;
        var at = now.Date + RecapEvening;
        var wait = Today.Invited ? at.AddDays(1) - now : at > now ? at - now : TimeSpan.FromMinutes(1);
        _recapClock.Interval = wait < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait;
        _recapClock.Start();
    }

    // Late afternoon, once: the compact notch opens on the recap for a moment.
    private void RecapClock()
    {
        ScheduleRecap();
        var day = Today;
        if (!_host.RecapEvening || day.Invited || day.IsEmpty || DateTime.Now.TimeOfDay < RecapEvening ||
            _view != NotchView.Compact || Quiet || _fullscreenHidden || _suspendedForChat || HasPendingRequest)
        {
            return;
        }

        RecordDay(log => log.Invited = true);
        OpenRecap();
        _recapCollapse.Start();
    }

    private void OpenRecap()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Recap;
        RenderRecap();
        _view = NotchView.Recap;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderRecap()
    {
        var day = Today;
        // Focus is the big number; on a day without any, the agent's work is.
        var focused = day.FocusSessions > 0;
        RecapValue.Text = focused ? DayLog.Duration(day.FocusMinutes, Tr.Text) : day.AgentTasks.Count.ToString(CultureInfo.InvariantCulture);
        RecapUnit.Text = focused ? Tr.Text("fokus", "focus") : Tr.Text("tugas", "tasks");
        var facts = new List<string>
        {
            string.Format(Tr.Text("{0} tugas", "{0} tasks"), day.AgentTasks.Count),
            string.Format(Tr.Text("{0} file", "{0} files"), day.Files.Count),
            day.FailedCommands > 0
                ? string.Format(Tr.Text("{0} perintah, {1} gagal", "{0} commands, {1} failed"), day.Commands, day.FailedCommands)
                : string.Format(Tr.Text("{0} perintah", "{0} commands"), day.Commands)
        };
        RecapDate.Text = string.Join(" · ", facts);
        RecapDate.ToolTip = day.Day.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMM", CultureInfo.CurrentUICulture);

        RecapRows.Children.Clear();
        foreach (var task in day.AgentTasks.AsEnumerable().Reverse())
        {
            RecapRows.Children.Add(RecapRow(task));
        }

        foreach (var outcome in day.PullRequests.TakeLast(2))
        {
            RecapRows.Children.Add(RecapRow(outcome));
        }

        RenderPageDots();
    }

    private FrameworkElement RecapRow(string text)
    {
        var grid = new Grid { Height = IslandTokens.Btn - 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = CheckGlyph,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Stroke = IslandTokens.Text3,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });
        var body = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.Medium, Foreground = IslandTokens.Text1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return new Border
        {
            BorderBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
    }

    private void RecapCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Today.ToMarkdown(Tr.Text));
            RecapDate.Text = Tr.Text("Tersalin", "Copied");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Recap", exception.Message);
        }
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewRecap()
    {
        await Task.Delay(400);
        _day = new DayLog { Day = DateOnly.FromDateTime(DateTime.Now) };
        _day.Focus(TimeSpan.FromMinutes(25));
        _day.Focus(TimeSpan.FromMinutes(25));
        _day.Focus(TimeSpan.FromMinutes(50));
        _day.AgentTask("Claude", "Refactor auth module");
        _day.AgentTask("Codex", "Fix the login test");
        _day.AgentTask("Claude", "Docs for the live activity API");
        _day.ChangedFiles(Enumerable.Range(0, 23).Select(i => $"C:\\p\\f{i}.cs"));
        for (var i = 0; i < 12; i++)
        {
            _day.Command(i % 6 != 0);
        }

        _day.PullRequest("CI #128 ✓");
        OpenRecap();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-recap.png"));
        }
    }
}
