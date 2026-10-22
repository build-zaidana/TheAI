using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

// The notch knows when not to interrupt. While a pomodoro focus session runs, or a meeting from
// the calendar is under way, an agent finishing or asking for permission doesn't open the notch,
// toast or chime: the compact notch only counts it. When the focus ends (the break) or the meeting
// does, everything held opens at once — one interruption instead of five.
public partial class NotchWindow
{
    private sealed record HeldItem(string Key, string Agent, string Text, bool Ask);

    private readonly List<HeldItem> _held = [];
    private readonly DispatcherTimer _quietCheck = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _heldCollapse = new() { Interval = TimeSpan.FromSeconds(15) };
    private string _quietReason = string.Empty;
    private string _releasedBy = string.Empty;

    private bool FocusRunning =>
        _timer.Mode == TimerMode.Pomodoro && _timer.Phase == PomodoroPhase.Focus && _timer.Run == TimerRun.Running;

    private CalendarEvent? MeetingNow =>
        _events.FirstOrDefault(item => !item.AllDay && item.Start <= DateTimeOffset.Now && item.End > DateTimeOffset.Now);

    /// <summary>Is now a time not to be interrupted (and does the user want that)?</summary>
    internal bool Quiet => _host.HoldDuringFocus && (FocusRunning || MeetingNow is not null);

    private void InitFocus()
    {
        _quietCheck.Tick += (_, _) => CheckQuiet();
        UpdateQuietCheck();
        _heldCollapse.Tick += (_, _) =>
        {
            _heldCollapse.Stop();
            if (_view == NotchView.Held && !Shell.IsMouseOver && !_pinned)
            {
                Collapse();
            }
        };
        Closed += (_, _) =>
        {
            _quietCheck.Stop();
            _heldCollapse.Stop();
        };
    }

    /// <summary>Is anything for the focus hold to watch? (Otherwise its check sleeps.)</summary>
    private bool QuietMatters =>
        _held.Count > 0 || _quietReason.Length > 0 ||
        _host.HoldDuringFocus && (FocusRunning || _events.Any(item => !item.AllDay && item.End > DateTimeOffset.Now && item.Start - DateTimeOffset.Now < TimeSpan.FromMinutes(1)));

    private void UpdateQuietCheck()
    {
        if (QuietMatters)
        {
            _quietCheck.Start();
        }
        else
        {
            _quietCheck.Stop();
        }
    }

    private void CheckQuiet()
    {
        if (!QuietMatters)
        {
            _quietCheck.Stop();
            return;
        }

        var quiet = Quiet;
        if (quiet)
        {
            _quietReason = FocusRunning ? "focus" : "meeting";
            return;
        }

        if (_quietReason.Length > 0)
        {
            _releasedBy = _quietReason;
            _quietReason = string.Empty;
        }

        // Out of focus: show what waited, as soon as the notch is free (after the timer's own "done").
        if (_held.Count > 0 && _view == NotchView.Compact && !_fullscreenHidden && !_suspendedForChat && !_dragging)
        {
            OpenHeld();
        }
    }

    /// <summary>
    /// An agent finished (or asks) while the user shouldn't be interrupted: keep it for the break.
    /// Returns false when it should be shown as usual (not quiet, or the user is looking at the notch).
    /// </summary>
    internal bool HoldIfQuiet(string key, bool ask, string? text = null)
    {
        if (!Quiet || _view is not (NotchView.Compact or NotchView.Hud))
        {
            return false;
        }

        var activity = ActivityFor(key);
        var agent = activity.IsMain ? _host.ProviderName : activity.Provider;
        text ??= activity.Prompt.ReplaceLineEndings(" ");
        _held.RemoveAll(item => item.Key == key && item.Ask == ask);
        _held.Add(new HeldItem(key, agent, text, ask));
        UpdateQuietCheck();
        _quietReason = FocusRunning ? "focus" : "meeting";
        ApplyState();
        if (_view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }

        UpdateVisibility();
        return true;
    }

    private void ApplyHeldBadge()
    {
        var show = _held.Count > 0;
        CompactHeld.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactHeld.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        // Beside the agent's indicator while one works; at the edge otherwise.
        CompactHeld.Margin = _state == NotchState.Idle ? new Thickness(0) : new Thickness(0, 0, 21, 0);
        CompactHeldText.Text = _held.Count.ToString(CultureInfo.InvariantCulture);
        if (show)
        {
            CompactCount.Visibility = Visibility.Collapsed;
        }
    }

    // ---- The "while you were focused" panel ----------------------------------------------------

    private void OpenHeld()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        RenderHeld();
        _held.Clear();
        ApplyHeldBadge();
        _view = NotchView.Held;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        _heldCollapse.Stop();
        _heldCollapse.Start();
    }

    private void RenderHeld()
    {
        var breakNow = _timer.Mode == TimerMode.Pomodoro && _timer.Phase != PomodoroPhase.Focus;
        (HeldTitle.Text, HeldMeta.Text) = (_releasedBy, breakNow) switch
        {
            ("meeting", _) => (Tr.Text("Rapat selesai", "Meeting over"), Tr.Text("selama rapat", "during the meeting")),
            (_, true) => (string.Format(Tr.Text("Istirahat {0}", "Break {0}"), NotchTimer.Format(_timer.Display(DateTimeOffset.Now))),
                Tr.Text("selama fokus", "while you focused")),
            _ => (Tr.Text("Selama fokus", "While you focused"), string.Empty)
        };

        HeldRows.Children.Clear();
        foreach (var item in _held.TakeLast(5))
        {
            HeldRows.Children.Add(BuildHeldRow(item));
        }
    }

    private FrameworkElement BuildHeldRow(HeldItem item)
    {
        var grid = new Grid { Height = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AgentBadge(item.Agent));

        var pending = item.Ask && HasPendingRequest;
        var text = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = TimerFocusBrush };
        text.Inlines.Add(new System.Windows.Documents.Run(item.Ask ? Tr.Text("Minta izin — ", "Asks permission — ") : Tr.Text("Selesai — ", "Done — ")));
        text.Inlines.Add(new System.Windows.Documents.Run(item.Text) { Foreground = TimerQuietBrush });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var open = new Button
        {
            Style = (Style)FindResource(pending ? "NotchPrimaryPill" : "NotchPill"),
            Height = 26,
            FontSize = 12,
            Content = pending ? Tr.Text("Jawab", "Answer") : Tr.Text("Lihat", "View")
        };
        open.Click += (_, _) =>
        {
            _heldCollapse.Stop();
            if (pending && HasPendingRequest)
            {
                ShowPendingRequest();
                return;
            }

            SelectActivity(ActivityFor(item.Key));
            OpenActivity();
        };
        System.Windows.Automation.AutomationProperties.SetName(open, $"{open.Content}: {item.Agent} — {item.Text}");
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);

        return new Border
        {
            BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
    }

    /// <summary>A small square naming the agent: Claude's orange C, Codex's white X, Gemini's gradient G.</summary>
    private static FrameworkElement AgentBadge(string agent)
    {
        var (letter, background, foreground) = agent switch
        {
            _ when agent.Contains("Claude", StringComparison.OrdinalIgnoreCase) || agent.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) =>
                ("C", (Brush)Frozen(Color.FromRgb(0xD9, 0x77, 0x57)), Brushes.White),
            _ when agent.Contains("Gemini", StringComparison.OrdinalIgnoreCase) =>
                ("G", GeminiBrush, Brushes.White),
            _ when agent.Contains("Codex", StringComparison.OrdinalIgnoreCase) || agent.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) =>
                ("X", (Brush)Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7)), Brushes.Black),
            _ => (agent.Length > 0 ? agent[..1].ToUpperInvariant() : "?", (Brush)Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), Brushes.White)
        };
        return new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static readonly Brush GeminiBrush = CreateGeminiBrush();

    private static Brush CreateGeminiBrush()
    {
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x42, 0x85, 0xF4), 0),
            new(Color.FromRgb(0x9B, 0x72, 0xCB), 0.6),
            new(Color.FromRgb(0xD9, 0x65, 0x70), 1)
        }, 45);
        brush.Freeze();
        return brush;
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewHeld(string mode)
    {
        await Task.Delay(400);
        BeginActivity("cc:preview-1", "Refactor auth — 6 file", "Claude", "api · Terminal");
        HandleProviderEvent(new CodexBar.Providers.ProviderCompletedEvent("completed"), "cc:preview-1");
        BeginActivity("cc:preview-2", "Tulis docs API", "Gemini", "api · Gemini CLI");
        HandleProviderEvent(new CodexBar.Providers.ProviderCompletedEvent("completed"), "cc:preview-2");
        if (mode == "compact")
        {
            // Focus running (never saved), so what arrives is held.
            _timerPreview = true;
            _timer.StartPomodoro(DateTimeOffset.Now.AddMinutes(-6));
        }

        _held.Add(new HeldItem("cc:preview-1", "Claude", "Refactor auth — 6 file", false));
        _held.Add(new HeldItem(MainActivityKey, "Codex", "npm install", true));
        _held.Add(new HeldItem("cc:preview-2", "Gemini", "Tulis docs API", false));
        if (mode == "compact")
        {
            await Task.Delay(4500);   // the agents' "done" check fades first
            ApplyState();
            ApplyMusicCompact();
            ApplyView(animate: true);
            UpdateVisibility();
        }
        else
        {
            _releasedBy = "focus";
            OpenHeld();
            _heldCollapse.Stop();
            _pinned = true;
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-held-{mode}.png"));
        }
    }
}
