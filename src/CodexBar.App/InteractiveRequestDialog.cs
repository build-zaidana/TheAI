using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;
using CodexBar.Providers;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;
using SelectionMode = System.Windows.Controls.SelectionMode;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

internal sealed class InteractiveRequestDialog : Window
{
    private readonly ProviderUserInputEvent _request;
    private readonly Dictionary<string, FrameworkElement> _answerControls = new(StringComparer.Ordinal);
    private readonly DispatcherTimer? _autoResolutionTimer;
    private DateTimeOffset? _autoResolveAt;
    private readonly TextBlock _countdown = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public InteractiveRequestDialog(ProviderUserInputEvent request)
    {
        _request = request;
        var loc = CodexBar.Core.Localization.Instance;
        Title = request.Title;
        FluentDialog.Prepare(this, 520);
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (Uri.TryCreate(request.ActionUrl, UriKind.Absolute, out var actionUri) &&
            actionUri.Scheme is "https" or "http")
        {
            var openLink = CreateButton($"{loc["dialog.openPrefix"]} {actionUri.Host}", true);
            openLink.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            openLink.Margin = new Thickness(0, 0, 0, 12);
            openLink.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(actionUri.ToString()) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    System.Windows.MessageBox.Show(this, exception.Message, loc["dialog.linkError"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            intro.Children.Add(openLink);
        }
        intro.Children.Add(FluentDialog.Label(request.Message, quiet: true));
        Grid.SetRow(intro, 0);
        root.Children.Add(intro);

        var questionsPanel = new StackPanel();
        foreach (var question in request.Questions)
        {
            AddQuestion(questionsPanel, question);
        }

        var scroll = new ScrollViewer
        {
            Content = questionsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 480
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _countdown.SetResourceReference(TextBlock.ForegroundProperty, "FluentText2");
        _countdown.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_countdown);

        var cancel = CreateButton(loc["dialog.cancel"], false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);

        var submit = CreateButton(loc["dialog.continue"], true);
        submit.Margin = new Thickness(8, 0, 0, 0);
        submit.Click += (_, _) => Submit();
        Grid.SetColumn(submit, 2);
        footer.Children.Add(submit);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        FluentDialog.Dress(this, request.Title, root);

        if (request.AutoResolutionMs is > 0 and <= int.MaxValue)
        {
            _autoResolveAt = DateTimeOffset.Now.AddMilliseconds(request.AutoResolutionMs.Value);
            _autoResolutionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoResolutionTimer.Tick += (_, _) => UpdateCountdown();
            _autoResolutionTimer.Start();
            UpdateCountdown();
        }

        Closed += (_, _) => _autoResolutionTimer?.Stop();
    }

    private void AddQuestion(Panel panel, ProviderInputQuestion question)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        group.Children.Add(FluentDialog.Label(question.Header, strong: true));
        var asked = FluentDialog.Label(question.Question, quiet: true);
        asked.Margin = new Thickness(0, 0, 0, 8);
        group.Children.Add(asked);

        FrameworkElement control;
        if (question.Options.Count > 0 && question.AllowsMultiple)
        {
            var list = new ListBox
            {
                Style = FluentDialog.Style(this, "FluentList"),
                MinHeight = 64,
                MaxHeight = 150,
                SelectionMode = SelectionMode.Multiple
            };
            foreach (var option in question.Options)
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(option.Description)
                        ? option.Label
                        : $"{option.Label} — {option.Description}",
                    Tag = option.Value ?? option.Label
                });
            }
            control = list;
        }
        else if (question.Options.Count > 0)
        {
            // The themed drop-down has no typing box, so a question that takes other answers keeps the plain one.
            var combo = question.AllowsOther
                ? new ComboBox { MinHeight = 32, Padding = new Thickness(8, 4, 8, 4), IsEditable = true }
                : new ComboBox { Style = FluentDialog.Style(this, "FluentPopup"), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            foreach (var option in question.Options)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(option.Description)
                        ? option.Label
                        : $"{option.Label} — {option.Description}",
                    Tag = option.Value ?? option.Label
                });
            }
            combo.SelectedIndex = 0;
            control = combo;
        }
        else if (question.IsSecret)
        {
            control = new PasswordBox { Style = FluentDialog.Style(this, "FluentSecretField") };
        }
        else
        {
            control = new TextBox
            {
                Style = FluentDialog.Style(this, "FluentArea"),
                MinHeight = 58,
                AcceptsReturn = true
            };
        }

        group.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, question.Header);
        System.Windows.Automation.AutomationProperties.SetHelpText(control, question.Question);
        _answerControls[question.Id] = control;
        panel.Children.Add(group);
    }

    private void Submit()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var question in _request.Questions)
        {
            if (!_answerControls.TryGetValue(question.Id, out var control))
            {
                continue;
            }

            var values = control switch
            {
                ListBox list => list.SelectedItems.OfType<ListBoxItem>()
                    .Select(item => item.Tag as string ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                ComboBox combo => new[]
                {
                    combo.SelectedItem is ComboBoxItem item
                        ? item.Tag as string ?? string.Empty
                        : combo.Text.Trim()
                },
                PasswordBox password => new[] { password.Password },
                TextBox text => new[] { text.Text.Trim() },
                _ => Array.Empty<string>()
            };
            var nonEmptyValues = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (question.IsRequired && nonEmptyValues.Length == 0)
            {
                var required = CodexBar.Core.Localization.Instance;
                System.Windows.MessageBox.Show(this, string.Format(required["dialog.required"], question.Header),
                    required["dialog.incomplete"], MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (nonEmptyValues.Length > 0 && !ValuesMatchType(question.ValueType, nonEmptyValues))
            {
                var loc = CodexBar.Core.Localization.Instance;
                var expected = question.ValueType == "integer" ? loc["dialog.integer"] : loc["dialog.number"];
                System.Windows.MessageBox.Show(this, string.Format(loc["dialog.invalidType"], question.Header, expected),
                    loc["dialog.invalid"], MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (nonEmptyValues.Length > 0)
            {
                answers[question.Id] = nonEmptyValues;
            }
        }

        Answers = answers;
        DialogResult = true;
        Close();
    }

    private static bool ValuesMatchType(string valueType, IEnumerable<string> values) => valueType switch
    {
        "integer" => values.All(value => long.TryParse(value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out _)),
        "number" => values.All(value => double.TryParse(value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)),
        _ => true
    };

    private void UpdateCountdown()
    {
        if (_autoResolveAt is null)
        {
            return;
        }

        var remaining = _autoResolveAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            _autoResolutionTimer?.Stop();
            Submit();
            return;
        }

        _countdown.Text = string.Format(
            CodexBar.Core.Localization.Instance["dialog.countdown"], Math.Ceiling(remaining.TotalSeconds));
    }

    private Button CreateButton(string text, bool primary)
    {
        var button = FluentDialog.Button(this, text, primary);
        button.IsDefault = primary;
        button.IsCancel = !primary;
        return button;
    }
}
