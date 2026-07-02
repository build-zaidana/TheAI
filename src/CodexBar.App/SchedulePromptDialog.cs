using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.Core;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

/// <summary>
/// Schedules the current prompt to run after N minutes and manages existing schedules.
/// Returns the updated list via <see cref="Schedules"/>. Kept code-built and simple to
/// mirror the other dialogs (and stay UiSmokeTest-friendly).
/// </summary>
internal sealed class SchedulePromptDialog : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _minutes = new();
    private readonly TextBox _promptBox = new();
    private readonly List<ScheduledPrompt> _items;

    public SchedulePromptDialog(string prompt, IEnumerable<ScheduledPrompt> existing)
    {
        var loc = CodexBar.Core.Localization.Instance;
        _items = ScheduledPrompt.Normalize(existing);

        Title = loc["schedule.title"];
        Width = 560;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C));
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = loc["schedule.promptLabel"],
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        _promptBox.Text = prompt.Trim();
        _promptBox.AcceptsReturn = true;
        _promptBox.TextWrapping = TextWrapping.Wrap;
        _promptBox.MinHeight = 60;
        _promptBox.MaxHeight = 120;
        _promptBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _promptBox.Padding = new Thickness(8);
        _promptBox.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x20));
        _promptBox.Foreground = Brushes.White;
        _promptBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x4B, 0x57));
        _promptBox.BorderThickness = new Thickness(1);
        System.Windows.Automation.AutomationProperties.SetName(_promptBox, loc["schedule.promptLabel"]);
        root.Children.Add(_promptBox);

        var addRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 12)
        };
        addRow.Children.Add(new TextBlock
        {
            Text = loc["schedule.minutes"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _minutes.Width = 80;
        _minutes.Text = "30";
        _minutes.Padding = new Thickness(8, 4, 8, 4);
        System.Windows.Automation.AutomationProperties.SetName(_minutes, loc["schedule.minutes"]);
        addRow.Children.Add(_minutes);
        var add = CreateButton(loc["schedule.add"]);
        add.Margin = new Thickness(8, 0, 0, 0);
        add.Click += (_, _) => AddSchedule();
        addRow.Children.Add(add);
        root.Children.Add(addRow);

        System.Windows.Automation.AutomationProperties.SetName(_list, loc["schedule.list"]);
        _list.Height = 170;
        root.Children.Add(_list);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var remove = CreateButton(loc["schedule.remove"]);
        remove.Click += (_, _) => RemoveSelected();
        actions.Children.Add(remove);
        var done = CreateButton(loc["templates.done"], primary: true);
        done.Margin = new Thickness(8, 0, 0, 0);
        done.IsDefault = true;
        done.Click += (_, _) =>
        {
            Schedules = ScheduledPrompt.Normalize(_items);
            DialogResult = true;
            Close();
        };
        actions.Children.Add(done);
        root.Children.Add(actions);

        Content = root;
        RefreshList();
    }

    public IReadOnlyList<ScheduledPrompt> Schedules { get; private set; } = [];

    private void AddSchedule()
    {
        var loc = CodexBar.Core.Localization.Instance;
        var prompt = _promptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            MessageBox.Show(this, loc["schedule.emptyPrompt"], loc["schedule.title"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(_minutes.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            minutes <= 0)
        {
            MessageBox.Show(this, loc["schedule.invalidMinutes"], loc["schedule.title"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _items.Add(new ScheduledPrompt { Prompt = prompt, RunAt = DateTimeOffset.Now.AddMinutes(minutes) });
        RefreshList();
    }

    private void RemoveSelected()
    {
        var index = _list.SelectedIndex;
        if (index >= 0 && index < _items.Count)
        {
            _items.RemoveAt(index);
            RefreshList();
        }
    }

    private void RefreshList()
    {
        var ordered = _items.OrderBy(item => item.RunAt).ToList();
        _items.Clear();
        _items.AddRange(ordered);
        _list.ItemsSource = null;
        _list.ItemsSource = _items
            .Select(item => $"{item.RunAt.LocalDateTime:yyyy-MM-dd HH:mm}  ·  {Preview(item.Prompt)}")
            .ToList();
    }

    private static string Preview(string prompt)
    {
        var firstLine = prompt.Split('\n')[0].Trim();
        return firstLine.Length <= 48 ? firstLine : firstLine[..48] + "…";
    }

    private static Button CreateButton(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 82,
        Height = 32,
        Padding = new Thickness(12, 0, 12, 0),
        Background = primary
            ? new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xF6))
            : new SolidColorBrush(Color.FromRgb(0x36, 0x3B, 0x46)),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0)
    };
}
