using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;


namespace CodexBar.App;

// Several agents at once. Every running conversation is an Activity: the main one (the
// conversation of the main window) plus parallel agents started from the launcher with Ctrl+Enter.
// Each keeps its own prompt, steps, output, changed files and state; the activity view shows one
// of them and pages between them like Live Activities. The compact notch shows the most urgent
// state of all of them (waiting > working > error > done) and how many are running.
public partial class NotchWindow
{
    internal const string MainActivityKey = "main";

    private sealed class Activity(string key)
    {
        public string Key { get; } = key;
        public bool IsMain => Key == MainActivityKey;
        public string Prompt { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Workspace { get; set; } = string.Empty;
        public ObservableCollection<StepItem> Steps { get; } = new();
        public StringBuilder Output { get; } = new();
        public Dictionary<string, ChangedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FilesExpanded { get; set; }
        public NotchState State { get; set; } = NotchState.Idle;
        public bool IsBusy => State is NotchState.Working or NotchState.Waiting;
    }

    private readonly List<Activity> _activities = [new Activity(MainActivityKey)];
    private Activity _activity = null!;

    private Activity MainActivity => _activities[0];

    // Pages shown in the switcher: the main conversation once it has run, and every parallel agent.
    private List<Activity> VisibleActivities =>
        _activities.Where(a => !a.IsMain || a.Prompt.Length > 0 || a.State != NotchState.Idle).ToList();

    private Activity ActivityFor(string key) =>
        _activities.FirstOrDefault(a => a.Key == key) ?? MainActivity;

    private bool ActivityBusy(Activity activity) =>
        activity.IsMain ? _host.IsBusy || activity.IsBusy : activity.IsBusy;

    /// <summary>A turn starts in an activity (main conversation or a parallel agent).</summary>
    public void BeginActivity(string key, string prompt, string provider, string workspace)
    {
        var activity = _activities.FirstOrDefault(a => a.Key == key);
        if (activity is null)
        {
            activity = new Activity(key);
            _activities.Add(activity);
        }

        activity.Prompt = prompt;
        activity.Provider = provider;
        activity.Workspace = workspace;
        activity.Steps.Clear();
        activity.Output.Clear();
        activity.Files.Clear();
        activity.FilesExpanded = false;
        _doneTimer.Stop();
        AddStep(activity, L("notch.step.sending"));

        // Don't yank the view away from an agent the user is reading.
        if (ReferenceEquals(activity, _activity) || _view != NotchView.Activity)
        {
            SelectActivity(activity);
        }

        SetActivityState(activity, NotchState.Working);
        RenderPager();
    }

    /// <summary>A parallel agent was closed; its page disappears.</summary>
    public void RemoveActivity(string key)
    {
        var activity = _activities.FirstOrDefault(a => a.Key == key && !a.IsMain);
        if (activity is null)
        {
            return;
        }

        _activities.Remove(activity);
        if (ReferenceEquals(activity, _activity))
        {
            SelectActivity(VisibleActivities.LastOrDefault() ?? MainActivity);
            if (VisibleActivities.Count == 0 && _view == NotchView.Activity)
            {
                Collapse();
            }
        }

        SetState(AggregateState());
        RenderPager();
        RenderAgentList();
    }

    private void SelectActivity(Activity activity)
    {
        _activity = activity;
        StepList.ItemsSource = activity.Steps;
        ActivityPrompt.Text = activity.Prompt;
        ActivityProviderRun.Text = activity.IsMain ? _host.ProviderName : activity.Provider;
        ActivityContextRun.Text = activity.IsMain
            ? "  " + _host.WorkspaceName
            : activity.Key.StartsWith("cc:", StringComparison.Ordinal)
                ? "  " + activity.Workspace // "project · Terminal"
                : $"  {activity.Workspace} · {L("notch.agents.parallel")}";
        RenderOutput();
        RenderChangedFiles();
        RefreshQueue();
        RenderPager();
        ApplyState();
    }

    // When opening from compact, show what needs attention rather than a finished page.
    private void SelectMostRelevant()
    {
        if (_activity.State != NotchState.Idle)
        {
            return;
        }

        var urgent = _activities.OrderByDescending(a => Urgency(a.State)).First();
        if (urgent.State != NotchState.Idle)
        {
            SelectActivity(urgent);
        }
    }

    private void SetActivityState(Activity activity, NotchState state)
    {
        activity.State = state;
        SetState(AggregateState());
        RenderAgentList();
    }

    private NotchState AggregateState() =>
        _activities.Select(a => a.State).OrderByDescending(Urgency).First();

    private static int Urgency(NotchState state) => state switch
    {
        NotchState.Waiting => 4,
        NotchState.Working => 3,
        NotchState.Error => 2,
        NotchState.Done => 1,
        _ => 0
    };

    private void ApplyCompactCount()
    {
        var running = _activities.Count(a => a.IsBusy);
        CompactCount.Text = running.ToString();
        CompactCount.Visibility = running > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bring one conversation up in the activity view (from a notification).</summary>
    public void ShowActivityFor(string key)
    {
        if (HasPendingRequest)
        {
            ShowPendingRequest();
            return;
        }

        SelectActivity(ActivityFor(key));
        OpenActivity();
        Activate();
    }

    /// <summary>Dev preview only: open one activity, pinned.</summary>
    internal void PreviewAgent(string key)
    {
        SelectActivity(ActivityFor(key));
        OpenActivity();
        _pinned = true;
        ApplyPin();
    }

    // ---- Pager -----------------------------------------------------------------------------

    private void RenderPager()
    {
        var pages = VisibleActivities;
        var index = pages.IndexOf(_activity);
        ActivityPager.Visibility = pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ActivityPagerText.Text = index >= 0 ? $"{index + 1}/{pages.Count}" : $"–/{pages.Count}";
        ApplyCompactCount();
    }

    private void PageActivity(int direction)
    {
        var pages = VisibleActivities;
        if (pages.Count < 2)
        {
            return;
        }

        var index = pages.IndexOf(_activity);
        var next = pages[((index < 0 ? 0 : index) + direction + pages.Count) % pages.Count];
        SelectActivity(next);
    }

    private void PrevActivity_Click(object sender, RoutedEventArgs e) => PageActivity(-1);

    private void NextActivity_Click(object sender, RoutedEventArgs e) => PageActivity(1);

    // ---- Launcher list of parallel agents --------------------------------------------------

    private void RenderAgentList()
    {
        AgentList.Children.Clear();
        foreach (var activity in _activities.Where(a => !a.IsMain))
        {
            var dot = activity.State switch
            {
                NotchState.Working => (Brush)FindResource("NotchAccent"),
                NotchState.Waiting => (Brush)FindResource("NotchOrange"),
                NotchState.Error => (Brush)FindResource("NotchRed"),
                NotchState.Done => DotDone,
                _ => DotIdle
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = dot,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var title = new TextBlock
            {
                Text = activity.Prompt.ReplaceLineEndings(" "),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            var meta = new TextBlock
            {
                Text = StateLabel(activity.State) + "  " + activity.Workspace,
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = (Brush)FindResource("NotchText3"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var row = new Button { Style = (Style)FindResource("NotchRowButton"), Content = grid, Tag = activity.Key };
            row.Click += AgentRow_Click;
            AgentList.Children.Add(row);
        }

        AgentListPanel.Visibility = AgentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AgentHeader.Text = L("notch.agents.title");
    }

    private static string StateLabel(NotchState state) => state switch
    {
        NotchState.Working => L("notch.agents.working"),
        NotchState.Waiting => L("notch.agents.waiting"),
        NotchState.Error => L("notch.agents.error"),
        NotchState.Done => L("notch.agents.done"),
        _ => L("notch.agents.idle")
    };

    private void AgentRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
        {
            SelectActivity(ActivityFor(key));
            OpenActivity();
        }
    }

    // ---- Starting and driving parallel agents ---------------------------------------------

    private async Task StartParallelAsync(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 && _host.Attachments.Count == 0)
        {
            return;
        }

        LauncherInput.Clear();
        try
        {
            var key = await _host.StartParallelAgentAsync(prompt);
            SelectActivity(ActivityFor(key));
            OpenActivity();
        }
        catch (Exception exception)
        {
            // Nothing started: report it on the launcher's own surface.
            LauncherInput.Text = prompt;
            LauncherInput.CaretIndex = prompt.Length;
            LauncherPlaceholder.Visibility = Visibility.Collapsed;
            AddStep(MainActivity, Truncate(exception.Message, 120), isError: true);
            SelectActivity(MainActivity);
            SetActivityState(MainActivity, NotchState.Error);
            OpenActivity();
        }
    }

    // Follow-up typed in the activity view of a parallel agent.
    private async Task SendToAgentAsync(Activity activity, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || activity.IsBusy)
        {
            return;
        }

        try
        {
            await _host.SendToActivityAsync(activity.Key, text);
        }
        catch (Exception exception)
        {
            AddStep(activity, Truncate(exception.Message, 120), isError: true);
            SetActivityState(activity, NotchState.Error);
        }
    }
}
