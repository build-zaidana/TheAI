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
        Width = 720;
        Height = 470;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
        editor.Children.Add(new TextBlock
        {
            Text = loc["templates.name"],
            Margin = new Thickness(0, 0, 0, 5),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC))
        });
        _name.MinHeight = 32;
        _name.Padding = new Thickness(8, 4, 8, 4);
        System.Windows.Automation.AutomationProperties.SetName(_name, loc["templates.name"]);
        Grid.SetRow(_name, 1);
        editor.Children.Add(_name);
        var contentLabel = new TextBlock
        {
            Text = loc["templates.content"],
            Margin = new Thickness(0, 12, 0, 5),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC))
        };
        Grid.SetRow(contentLabel, 2);
        editor.Children.Add(contentLabel);
        _content.AcceptsReturn = true;
        _content.AcceptsTab = true;
        _content.TextWrapping = TextWrapping.Wrap;
        _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _content.Padding = new Thickness(8);
        System.Windows.Automation.AutomationProperties.SetName(_content, loc["templates.content"]);
        Grid.SetRow(_content, 3);
        editor.Children.Add(_content);
        Grid.SetColumn(editor, 2);
        root.Children.Add(editor);

        var actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var create = CreateButton(loc["templates.new"]);
        create.Click += (_, _) => BeginNew();
        actions.Children.Add(create);
        var delete = CreateButton(loc["templates.delete"]);
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.Click += (_, _) => DeleteSelected();
        Grid.SetColumn(delete, 1);
        actions.Children.Add(delete);
        var save = CreateButton(loc["templates.save"]);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => SaveEditor();
        Grid.SetColumn(save, 2);
        actions.Children.Add(save);
        var cancel = CreateButton(loc["dialog.cancel"]);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 3);
        actions.Children.Add(cancel);
        var done = CreateButton(loc["templates.done"], primary: true);
        done.Margin = new Thickness(8, 0, 0, 0);
        done.IsDefault = true;
        done.Click += (_, _) =>
        {
            Templates = PromptTemplate.Normalize(_templates);
            DialogResult = true;
            Close();
        };
        Grid.SetColumn(done, 4);
        actions.Children.Add(done);
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 3);
        root.Children.Add(actions);

        Content = root;
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
