using System.Windows;
using System.Windows.Controls;
using CodexBar.Core;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

internal sealed class TemplateManagerDialog : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _name = new();
    private readonly TextBox _content = new();
    private readonly List<PromptTemplate> _templates;

    public TemplateManagerDialog(IEnumerable<PromptTemplate> templates)
    {
        var loc = CodexBar.Core.Localization.Instance;
        _templates = PromptTemplate.Normalize(templates)
            .Select(template => new PromptTemplate { Name = template.Name, Content = template.Content })
            .ToList();

        Title = loc["templates.manage"];
        FluentDialog.Prepare(this, 720);
        Height = 520 + 44;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _list.Style = FluentDialog.Style(this, "FluentList");
        _list.DisplayMemberPath = nameof(PromptTemplate.Name);
        _list.SelectionChanged += (_, _) => LoadSelected();
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["templates.listName"]);
        Grid.SetColumn(_list, 0);
        root.Children.Add(_list);

        var editor = new Grid();
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var nameLabel = FluentDialog.Label(loc["templates.name"]);
        nameLabel.Margin = new Thickness(0, 0, 0, 8);
        editor.Children.Add(nameLabel);
        _name.Style = FluentDialog.Style(this, "FluentField");
        System.Windows.Automation.AutomationProperties.SetName(_name, loc["templates.name"]);
        Grid.SetRow(_name, 1);
        editor.Children.Add(_name);
        var contentLabel = FluentDialog.Label(loc["templates.content"]);
        contentLabel.Margin = new Thickness(0, 12, 0, 8);
        Grid.SetRow(contentLabel, 2);
        editor.Children.Add(contentLabel);
        _content.Style = FluentDialog.Style(this, "FluentArea");
        _content.AcceptsReturn = true;
        _content.AcceptsTab = true;
        _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        System.Windows.Automation.AutomationProperties.SetName(_content, loc["templates.content"]);
        Grid.SetRow(_content, 3);
        editor.Children.Add(_content);
        Grid.SetColumn(editor, 2);
        root.Children.Add(editor);

        // New and Delete act on the list; the rest on the dialog.
        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var listActions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        var dialogActions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(listActions);
        actions.Children.Add(dialogActions);

        var create = FluentDialog.Button(this, loc["templates.new"]);
        create.Click += (_, _) => BeginNew();
        listActions.Children.Add(create);
        var delete = FluentDialog.Button(this, loc["templates.delete"]);
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.Click += (_, _) => DeleteSelected();
        listActions.Children.Add(delete);
        var save = FluentDialog.Button(this, loc["templates.save"]);
        save.Click += (_, _) => SaveEditor();
        dialogActions.Children.Add(save);
        var cancel = FluentDialog.Button(this, loc["dialog.cancel"]);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        dialogActions.Children.Add(cancel);
        var done = FluentDialog.Button(this, loc["templates.done"], primary: true);
        done.Margin = new Thickness(8, 0, 0, 0);
        done.IsDefault = true;
        done.Click += (_, _) =>
        {
            Templates = PromptTemplate.Normalize(_templates);
            DialogResult = true;
            Close();
        };
        dialogActions.Children.Add(done);
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 3);
        root.Children.Add(actions);

        FluentDialog.Dress(this, loc["templates.manage"], root);
        RefreshList();
        if (_templates.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    public IReadOnlyList<PromptTemplate> Templates { get; private set; } = [];

    private void BeginNew()
    {
        _list.SelectedIndex = -1;
        _name.Clear();
        _content.Clear();
        _name.Focus();
    }

    private void LoadSelected()
    {
        if (_list.SelectedItem is not PromptTemplate template)
        {
            return;
        }

        _name.Text = template.Name;
        _content.Text = template.Content;
    }

    private void SaveEditor()
    {
        var loc = CodexBar.Core.Localization.Instance;
        var name = _name.Text.Trim();
        var content = _content.Text;
        if (name.Length == 0 || content.Trim().Length == 0)
        {
            MessageBox.Show(this, loc["templates.validation"], loc["templates.manage"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var duplicate = _templates.FindIndex(template =>
            string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase));
        var selected = _list.SelectedIndex;
        if (duplicate >= 0 && duplicate != selected)
        {
            MessageBox.Show(this, loc["templates.duplicate"], loc["templates.manage"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var updated = new PromptTemplate { Name = name, Content = content };
        if (selected >= 0 && selected < _templates.Count)
        {
            _templates[selected] = updated;
        }
        else
        {
            _templates.Add(updated);
            selected = _templates.Count - 1;
        }

        RefreshList();
        _list.SelectedIndex = selected;
    }

    private void DeleteSelected()
    {
        var selected = _list.SelectedIndex;
        if (selected < 0 || selected >= _templates.Count)
        {
            return;
        }

        _templates.RemoveAt(selected);
        RefreshList();
        if (_templates.Count > 0)
        {
            _list.SelectedIndex = Math.Min(selected, _templates.Count - 1);
        }
        else
        {
            BeginNew();
        }
    }

    private void RefreshList()
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _templates;
    }
}
