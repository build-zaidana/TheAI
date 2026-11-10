using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CodexBar.Core;
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
    private readonly TextBlock _empty = new();
    private readonly TextBox _minutes = new();
    private readonly TextBox _promptBox = new();
    private readonly List<ScheduledPrompt> _items;

    public SchedulePromptDialog(string prompt, IEnumerable<ScheduledPrompt> existing)
    {
        var loc = CodexBar.Core.Localization.Instance;
        _items = ScheduledPrompt.Normalize(existing);

        Title = loc["schedule.title"];
        FluentDialog.Prepare(this, 560);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel();

        var promptLabel = FluentDialog.Label(loc["schedule.promptLabel"]);
        promptLabel.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(promptLabel);
        _promptBox.Style = FluentDialog.Style(this, "FluentArea");
        _promptBox.Text = prompt.Trim();
        _promptBox.AcceptsReturn = true;
        _promptBox.MinHeight = 60;
        _promptBox.MaxHeight = 120;
        _promptBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
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
        _minutes.Style = FluentDialog.Style(this, "FluentField");
        _minutes.Width = 80;
        _minutes.Text = "30";
        System.Windows.Automation.AutomationProperties.SetName(_minutes, loc["schedule.minutes"]);
        addRow.Children.Add(_minutes);
        var add = FluentDialog.Button(this, loc["schedule.add"]);
        add.Margin = new Thickness(8, 0, 0, 0);
        add.Click += (_, _) => AddSchedule();
        addRow.Children.Add(add);
        root.Children.Add(addRow);

        System.Windows.Automation.AutomationProperties.SetName(_list, loc["schedule.list"]);
        _list.Style = FluentDialog.Style(this, "FluentList");
        _list.Height = 170;
        _empty.Text = Tr.Text("Belum ada jadwal.", "Nothing scheduled yet.");
        _empty.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.IsHitTestVisible = false;
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "FluentText2");
        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        root.Children.Add(listArea);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var remove = FluentDialog.Button(this, loc["schedule.remove"]);
        remove.Click += (_, _) => RemoveSelected();
        actions.Children.Add(remove);
        var done = FluentDialog.Button(this, loc["templates.done"], primary: true);
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

        FluentDialog.Dress(this, loc["schedule.title"], root);
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
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Preview(string prompt)
    {
        var firstLine = prompt.Split('\n')[0].Trim();
        return firstLine.Length <= 48 ? firstLine : firstLine[..48] + "…";
    }
}
