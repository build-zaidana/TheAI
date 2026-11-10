using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexBar.App;

// The launcher as a command palette: "/" lists what TheAI can do — prompts that do a whole job
// (/review, /explain, /fix, /translate…), the user's own templates as /their-name, and the
// notch's pages and actions (/focus 25, /timer 10, /clipboard, /recap, /settings…). Typing narrows
// it, ↑/↓ choose, Tab completes, Enter runs. Typing anything else also searches what was said in
// past conversations, not just their titles.
public partial class NotchWindow
{
    private const int CommandRowsShown = 7;

    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<SmartCommand> _commandMatches = [];
    private int _commandIndex;
    private IReadOnlyList<ProviderThreadSummary> _searchHits = [];
    private string _searched = string.Empty;
    private int _searchVersion;

    private bool CommandMode => LauncherInput.Text.TrimStart().StartsWith('/');

    private IEnumerable<SmartCommand> AllCommands =>
        SmartCommands.BuiltIn.Concat(SmartCommands.FromTemplates(_host.Templates));

    private void InitCommands()
    {
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await SearchHistoryAsync(LauncherInput.Text.Trim());
        };
    }

    // ---- The palette -------------------------------------------------------------------------

    /// <summary>Show the commands for what's typed (or hide them when it isn't a command).</summary>
    private void RenderCommands()
    {
        if (!CommandMode)
        {
            CommandPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var typed = LauncherInput.Text.TrimStart();
        var space = typed.IndexOf(' ');
        var name = space < 0 ? typed : typed[..space];
        var matches = SmartCommands.Match(name, AllCommands, Tr.Text);
        if (space >= 0)
        {
            // Past the name: only the command being given its argument.
            matches = matches.Where(command => command.Name == name.TrimStart('/').ToLowerInvariant()).ToList();
        }

        if (!ReferenceEquals(_commandMatches, matches) && !_commandMatches.Select(c => c.Name).SequenceEqual(matches.Select(c => c.Name)))
        {
            _commandIndex = 0;
        }

        _commandMatches = matches;
        CommandRows.Children.Clear();
        foreach (var (command, index) in matches.Take(CommandRowsShown).Select((command, index) => (command, index)))
        {
            CommandRows.Children.Add(BuildCommandRow(command, index == _commandIndex));
        }

        CommandEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = Visibility.Visible;
        RecentHeader.Visibility = Visibility.Collapsed;
        RecentHeaderRow.Margin = new Thickness(0);
        RecentFilters.Visibility = Visibility.Collapsed;
        RecentList.ItemsSource = null;
        ChipPanel.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement BuildCommandRow(SmartCommand command, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = "/" + command.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(name);
        var hint = new TextBlock
        {
            Text = command.ArgumentHint(Tr.Text),
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = (Brush)FindResource("NotchText3"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);
        var description = new TextBlock
        {
            Text = command.Description(Tr.Text),
            FontSize = 12.5,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = (Brush)FindResource("NotchText3"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300
        };
        Grid.SetColumn(description, 2);
        grid.Children.Add(description);

        var row = new Button
        {
            Style = (Style)FindResource("NotchRowButton"),
            Content = grid,
            Tag = command
        };
        System.Windows.Automation.AutomationProperties.SetName(row, $"/{command.Name} — {command.Description(Tr.Text)}");
        System.Windows.Automation.AutomationProperties.SetItemStatus(row, selected ? Tr.Text("dipilih", "selected") : string.Empty);
        row.Click += async (_, _) => await RunCommandAsync(command, ArgumentFor(command));
        // The row chosen with ↑/↓ (what Enter runs) sits on a soft highlight.
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = selected ? Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            Child = row
        };
    }

    /// <summary>The palette's keys; true when it handled the key.</summary>
    private async Task<bool> CommandKeyAsync(KeyEventArgs e)
    {
        if (!CommandMode || CommandPanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        var shown = Math.Min(_commandMatches.Count, CommandRowsShown);
        switch (e.Key)
        {
            case Key.Down when shown > 0:
                _commandIndex = (_commandIndex + 1) % shown;
                RenderCommands();
                return true;
            case Key.Up when shown > 0:
                _commandIndex = (_commandIndex - 1 + shown) % shown;
                RenderCommands();
                return true;
            case Key.Tab when shown > 0:
                CompleteCommand(_commandMatches[_commandIndex]);
                return true;
            case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                if (SmartCommands.Split(LauncherInput.Text) is { } typed &&
                    AllCommands.FirstOrDefault(command => command.Name == typed.Name) is { } exact)
                {
                    await RunCommandAsync(exact, typed.Argument);
                }
                else if (shown > 0)
                {
                    var chosen = _commandMatches[_commandIndex];
                    await RunCommandAsync(chosen, ArgumentFor(chosen));
                }

                return true;
            default:
                return false;
        }
    }

    private string ArgumentFor(SmartCommand command) =>
        SmartCommands.Split(LauncherInput.Text) is { } typed && typed.Name == command.Name ? typed.Argument : string.Empty;

    private void CompleteCommand(SmartCommand command)
    {
        LauncherInput.Text = "/" + command.Name + " ";
        LauncherInput.CaretIndex = LauncherInput.Text.Length;
    }

    private async Task RunCommandAsync(SmartCommand command, string argument)
    {
        if (command.Kind == SmartCommandKind.Prompt)
        {
            var prompt = SmartCommands.Expand(command, argument, new SmartCommandContext(ClipboardText(), LastFailure(), Tr.IsEnglish));
            if (prompt is null)
            {
                // It needs text: let the user type it (or copy it) first.
                CompleteCommand(command);
                CommandEmpty.Text = Tr.Text("Ketik teksnya setelah perintah, atau salin dulu ke clipboard.", "Type the text after the command, or copy it first.");
                CommandEmpty.Visibility = Visibility.Visible;
                return;
            }

            LauncherInput.Clear();
            await SubmitAsync(prompt);
            return;
        }

        LauncherInput.Clear();
        var now = DateTimeOffset.Now;
        switch (command.Name)
        {
            case "new":
                Collapse();
                await _host.NewConversationAsync();
                break;
            case "focus":
                RunTimerCommand(TimerCommandParser.Parse(int.TryParse(argument, out var minutes) && minutes is > 0 and <= 180 ? $"p{minutes}" : "p"));
                OpenTimer();
                break;
            case "timer":
                if (argument.Length > 0)
                {
                    RunTimerCommand(TimerCommandParser.Parse(argument));
                }

                OpenTimer(focus: argument.Length == 0);
                break;
            case "clipboard":
                if (!_host.ClipboardHistory)
                {
                    ClipEmpty.Text = Tr.Text("Riwayat clipboard mati — nyalakan di Pengaturan → Notch.", "Clipboard history is off — turn it on in Settings → Notch.");
                }

                OpenClipboard();
                break;
            case "recap":
                OpenRecap();
                break;
            case "standup":
                OpenRecap();
                RecapCopy_Click(this, new RoutedEventArgs());
                break;
            case "system":
                OpenSystem();
                break;
            case "weather":
                OpenWeather();
                break;
            case "water":
                OpenWater();
                break;
            case "today":
                OpenToday();
                break;
            case "mute":
                ToggleMicMute();
                break;
            case "activities":
                if (_jobs.Visible(now).Count > 0)
                {
                    OpenJobs();
                }
                else
                {
                    Collapse();
                }

                break;
            case "settings":
                Collapse();
                _host.OpenSettings();
                break;
            case "remind" or "todo":
                // "/todo 15m x" stays a to-do: only /remind reads a time.
                if (argument.Length == 0 || (command.Name == "todo" ? AddTodo(argument) : AddReminder(argument)) is null)
                {
                    OpenReminders(focusInput: true);
                    break;
                }

                OpenReminders();
                _reminderCollapse.Stop();
                _reminderCollapse.Start();
                break;
        }
    }

    private static string ClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            return string.Empty;
        }
    }

    /// <summary>The most recent failure the notch knows: a terminal command or a CI run.</summary>
    private string LastFailure() =>
        _jobs.Jobs.Where(job => job.State == LiveJobState.Failed).MaxBy(job => job.Updated) is { } job
            ? $"`{job.Title}`" + (job.Detail.Length > 0 ? $" ({job.Detail})" : string.Empty) + (job.Folder.Length > 0 ? $" in `{job.Folder}`" : string.Empty)
            : string.Empty;

    internal async void PreviewPalette(string typed)
    {
        await Task.Delay(400);
        OpenLauncher(focus: false);
        _pinned = true;
        await Task.Delay(300);
        LauncherInput.Text = typed;
        LauncherInput.CaretIndex = typed.Length;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-palette.png"));
        }
    }

    // ---- Searching past conversations ----------------------------------------------------------

    /// <summary>Typed text that isn't a command: look in what was said, a moment after typing stops.</summary>
    private void ScheduleHistorySearch()
    {
        _searchDelay.Stop();
        var query = LauncherInput.Text.Trim();
        if (CommandMode || query.Length < 3)
        {
            _searchHits = [];
            _searched = string.Empty;
            return;
        }

        if (query != _searched)
        {
            _searchDelay.Start();
        }
    }

    private async Task SearchHistoryAsync(string query)
    {
        if (query.Length < 3 || CommandMode)
        {
            return;
        }

        var version = ++_searchVersion;
        IReadOnlyList<ProviderThreadSummary> hits;
        try
        {
            hits = await _host.SearchThreadsAsync(query);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Search", exception.Message);
            return;
        }

        if (version != _searchVersion || LauncherInput.Text.Trim() != query)
        {
            return;   // typed on meanwhile
        }

        _searched = query;
        _searchHits = hits;
        RenderRecent();
    }
}
