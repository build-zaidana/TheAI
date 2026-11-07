using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Reminders and to-dos in the notch: "/remind 15m stretch", "/todo buy milk", or typed in the page's
// own field. When one is due the notch opens on it (Done / Snooze 10m) and Windows shows a
// notification, also while a fullscreen app hides the notch.
public partial class NotchWindow
{
    private const double RemindersWidth = 420;
    private static readonly TimeSpan SnoozeLength = TimeSpan.FromMinutes(10);

    private static string RemindersPath => System.IO.Path.Combine(CodexBar.Providers.AppPaths.DataRoot, "reminders.json");

    private readonly DispatcherTimer _reminderClock = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _reminderCollapse = new() { Interval = TimeSpan.FromSeconds(20) };
    private ReminderList _reminders = new();
    private string? _ringingReminder;

    private bool RemindersAvailable => _host.RemindersEnabled || _reminders.Open.Any();

    private void InitReminders()
    {
        _reminders = ReminderList.Load(RemindersPath);
        _reminders.Prune(DateTimeOffset.Now);
        _reminderClock.Tick += (_, _) => CheckReminders();
        ScheduleReminderCheck();
        _reminderCollapse.Tick += (_, _) =>
        {
            _reminderCollapse.Stop();
            if (_view == NotchView.Reminders && !Shell.IsMouseOver && !_pinned && !ReminderInput.IsKeyboardFocused)
            {
                Collapse();
            }
        };
        Closed += (_, _) => _reminderClock.Stop();
    }

    private bool _remindersPreview;

    /// <summary>Wake up when the next reminder is due (and every 30 s while the page shows "in 12m").</summary>
    private void ScheduleReminderCheck()
    {
        _reminderClock.Stop();
        var now = DateTimeOffset.Now;
        var next = _reminders.Items.Where(item => !item.Done && !item.Announced && item.DueAt is not null).Min(item => item.DueAt);
        TimeSpan? wait = next is { } due ? due - now : null;
        if (_view == NotchView.Reminders)
        {
            wait = wait is { } w && w < TimeSpan.FromSeconds(30) ? w : TimeSpan.FromSeconds(30);
        }

        if (wait is { } interval)
        {
            _reminderClock.Interval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
                : interval > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : interval;
            _reminderClock.Start();
        }
    }

    private void SaveReminders()
    {
        ScheduleReminderCheck();
        if (!_remindersPreview)
        {
            _reminders.Save(RemindersPath);
        }
    }

    internal async void PreviewReminders(string mode)
    {
        await Task.Delay(400);
        _remindersPreview = true;
        var now = DateTimeOffset.Now;
        _reminders = new ReminderList();
        _reminders.Add("14:30 " + Tr.Text("telepon klien", "call the client"), now);
        _reminders.Add("45m " + Tr.Text("minum air", "drink water"), now);
        _reminders.Add(Tr.Text("beli susu", "buy milk"), now);
        _reminders.Add(Tr.Text("kirim invoice Oktober", "send the October invoice"), now);
        if (mode == "due")
        {
            var stretch = _reminders.Add("1m " + Tr.Text("peregangan", "stretch"), now.AddMinutes(-1))!;
            _ = stretch;
            CheckReminders();
            _reminderCollapse.Stop();
        }
        else
        {
            OpenReminders();
        }

        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-reminders-{mode}.png"));
        }
    }

    /// <summary>Add from the palette or the page's field; returns the item, or null when there was no text.</summary>
    private ReminderItem? AddReminder(string input)
    {
        var item = _reminders.Add(input, DateTimeOffset.Now);
        if (item is not null)
        {
            SaveReminders();
            RenderReminders();
            RenderPageDots();
        }

        return item;
    }

    private ReminderItem? AddTodo(string text)
    {
        var item = _reminders.Add(text, DateTimeOffset.Now);
        if (item is not null)
        {
            item.DueAt = null;   // a to-do, even if it starts with something like a time
            SaveReminders();
            RenderReminders();
        }

        return item;
    }

    private void CheckReminders()
    {
        var due = _reminders.DueNow(DateTimeOffset.Now);
        if (due.Count == 0)
        {
            if (_view == NotchView.Reminders)
            {
                RenderReminders();   // the "in 12m" labels move on
            }

            ScheduleReminderCheck();
            return;
        }

        foreach (var item in due)
        {
            item.Announced = true;
        }

        SaveReminders();
        var first = due[0];
        _host.ShowReminder(first.Text, due.Count - 1);
        CompletionNotifier.PlaySound();
        if (!Quiet && !_fullscreenHidden && !_suspendedForChat && _view is NotchView.Compact or NotchView.Hud or NotchView.Reminders)
        {
            _ringingReminder = first.Id;
            OpenReminders();
            _reminderCollapse.Stop();
            _reminderCollapse.Start();
        }
    }

    internal void AnswerRingingReminder(bool done)
    {
        // The one ringing in the notch, else the latest one announced (the notch may have been hidden).
        var id = _ringingReminder ?? _reminders.Items.Where(item => item.Announced && !item.Done).MaxBy(item => item.DueAt)?.Id;
        if (id is null)
        {
            return;
        }

        if (done)
        {
            CompleteReminder(id);
        }
        else
        {
            SnoozeReminder(id);
        }
    }

    internal void CompleteReminder(string id)
    {
        _reminders.Complete(id);
        SaveReminders();
        RenderReminders();
    }

    internal void SnoozeReminder(string id)
    {
        _reminders.Snooze(id, SnoozeLength, DateTimeOffset.Now);
        SaveReminders();
        RenderReminders();
    }

    private void OpenReminders(bool focusInput = false)
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Reminders;
        RenderReminders();
        _view = NotchView.Reminders;
        ScheduleReminderCheck();
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
        if (focusInput)
        {
            Activate();
            ReminderInput.Focus();
        }
    }

    private void RenderReminders()
    {
        var now = DateTimeOffset.Now;
        var open = _reminders.Open.Take(7).ToList();
        ReminderRows.Children.Clear();
        foreach (var item in open)
        {
            ReminderRows.Children.Add(BuildReminderRow(item, now));
        }

        ReminderCount.Text = open.Count == 0 ? string.Empty : _reminders.Open.Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReminderEmpty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReminderHint.Visibility = ReminderInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildReminderRow(ReminderItem item, DateTimeOffset now)
    {
        var ringing = item.Id == _ringingReminder && item.DueAt <= now;
        var grid = new Grid { MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = item.Done,
            Cursor = Cursors.Hand,
            Template = CircleCheckTemplate()
        };
        System.Windows.Automation.AutomationProperties.SetName(check, Tr.Text("Selesai: ", "Done: ") + item.Text);
        check.Click += (_, _) => CompleteReminder(item.Id);
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = ringing ? 14 : 13,
            FontWeight = ringing ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = TimerFocusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        FrameworkElement right;
        if (ringing)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var snooze = new Button { Style = (Style)FindResource("NotchPill"), Height = 26, FontSize = 12, Content = Tr.Text("Tunda 10m", "Snooze 10m") };
            snooze.Click += (_, _) => SnoozeReminder(item.Id);
            var done = new Button { Style = (Style)FindResource("NotchPrimaryPill"), Height = 26, FontSize = 12, Content = Tr.Text("Selesai", "Done") };
            done.Click += (_, _) => CompleteReminder(item.Id);
            actions.Children.Add(snooze);
            actions.Children.Add(done);
            right = actions;
        }
        else
        {
            right = new TextBlock
            {
                Text = ReminderList.When(item, now, Tr.Text),
                Style = (Style)FindResource("NotchDigits"),
                FontSize = 12,
                Foreground = item.DueAt <= now ? (Brush)FindResource("NotchRed") : TimerQuietBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
        }

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return new Border
        {
            BorderBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, ReminderRows.Children.Count == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid
        };
    }

    // A round tick box, white on the notch's black.
    private static ControlTemplate CircleCheckTemplate()
    {
        var template = new ControlTemplate(typeof(CheckBox));
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(WidthProperty, 16.0);
        ring.SetValue(HeightProperty, 16.0);
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        ring.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
        ring.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = ring;
        return template;
    }

    private void ReminderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (AddReminder(ReminderInput.Text) is not null)
        {
            ReminderInput.Clear();
        }
    }

    private void ReminderInput_TextChanged(object sender, TextChangedEventArgs e) =>
        ReminderHint.Visibility = ReminderInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}
