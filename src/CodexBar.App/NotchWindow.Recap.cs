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
    private const double RecapWidth = 460;
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
        _recapClock.Start();
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

    // Late afternoon, once: the compact notch opens on the recap for a moment.
    private void RecapClock()
    {
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
        RecapDate.Text = day.Day.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMM", CultureInfo.CurrentUICulture);
        RecapTiles.Children.Clear();
        RecapTiles.Children.Add(Tile(day.FocusSessions.ToString(CultureInfo.InvariantCulture),
            day.FocusSessions > 0 ? Tr.Text("fokus · ", "focus · ") + DayLog.Duration(day.FocusMinutes, Tr.Text) : Tr.Text("fokus", "focus")));
        RecapTiles.Children.Add(Tile(day.AgentTasks.Count.ToString(CultureInfo.InvariantCulture), Tr.Text("tugas agent", "agent tasks")));
        RecapTiles.Children.Add(Tile(day.Files.Count.ToString(CultureInfo.InvariantCulture), Tr.Text("file diubah", "files changed")));
        RecapTiles.Children.Add(Tile(day.Commands.ToString(CultureInfo.InvariantCulture),
            day.FailedCommands > 0 ? string.Format(Tr.Text("perintah · {0} gagal", "commands · {0} failed"), day.FailedCommands) : Tr.Text("perintah", "commands")));

        RecapRows.Children.Clear();
        foreach (var task in day.AgentTasks.TakeLast(3).Reverse())
        {
            RecapRows.Children.Add(RecapRow(task));
        }

        foreach (var outcome in day.PullRequests.TakeLast(2))
        {
            RecapRows.Children.Add(RecapRow(outcome));
        }

        RenderPageDots();
    }

    private FrameworkElement Tile(string value, string label)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = value, FontFamily = (FontFamily)FindResource("SoraFont"), FontSize = 20, Foreground = TimerFocusBrush });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = TimerQuietBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Background = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 6, 8),
            Margin = new Thickness(0, 0, 6, 0),
            Child = stack
        };
    }

    private FrameworkElement RecapRow(string text) => new Border
    {
        BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(0, 7, 0, 7),
        Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = TimerFocusBrush, TextTrimming = TextTrimming.CharacterEllipsis }
    };

    private void RecapCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Today.ToMarkdown(Tr.Text));
            RecapCopy.Content = Tr.Text("Tersalin ✓", "Copied ✓");
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
