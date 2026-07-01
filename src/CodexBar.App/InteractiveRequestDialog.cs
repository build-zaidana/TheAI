using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using CodexBar.Providers;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
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
        Title = request.Title;
        Width = 520;
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        intro.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (Uri.TryCreate(request.ActionUrl, UriKind.Absolute, out var actionUri) &&
            actionUri.Scheme is "https" or "http")
        {
            var openLink = CreateButton($"Buka {actionUri.Host}", true);
            openLink.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            openLink.Margin = new Thickness(0, 12, 0, 0);
            openLink.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(actionUri.ToString()) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    System.Windows.MessageBox.Show(this, exception.Message, "Tautan tidak dapat dibuka",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            intro.Children.Add(openLink);
        }
        intro.Children.Add(new TextBlock
        {
            Text = request.Message,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC)),
            TextWrapping = TextWrapping.Wrap
        });
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
        _countdown.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC));
        _countdown.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_countdown);

        var cancel = CreateButton("Batal", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);

        var submit = CreateButton("Lanjutkan", true);
        submit.Margin = new Thickness(8, 0, 0, 0);
        submit.Click += (_, _) => Submit();
        Grid.SetColumn(submit, 2);
        footer.Children.Add(submit);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;

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
        group.Children.Add(new TextBlock
        {
            Text = question.Header,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xFF))
        });
        group.Children.Add(new TextBlock
        {
            Text = question.Question,
            Margin = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });

        FrameworkElement control;
        if (question.Options.Count > 0 && question.AllowsMultiple)
        {
            var list = new ListBox
            {
                MinHeight = 64,
                MaxHeight = 150,
                SelectionMode = SelectionMode.Multiple,
                Padding = new Thickness(4)
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
            var combo = new ComboBox
            {
                MinHeight = 34,
                Padding = new Thickness(8, 4, 8, 4),
                IsEditable = question.AllowsOther
            };
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
            control = new PasswordBox { MinHeight = 34, Padding = new Thickness(8, 5, 8, 5) };
        }
        else
        {
            control = new TextBox
            {
                MinHeight = 58,
                Padding = new Thickness(8),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap
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
                System.Windows.MessageBox.Show(this, $"{question.Header} wajib diisi.", "Input belum lengkap",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (nonEmptyValues.Length > 0 && !ValuesMatchType(question.ValueType, nonEmptyValues))
            {
                var expected = question.ValueType == "integer" ? "bilangan bulat" : "angka";
                System.Windows.MessageBox.Show(this, $"{question.Header} harus berupa {expected}.",
                    "Input tidak valid", MessageBoxButton.OK, MessageBoxImage.Information);
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

        _countdown.Text = $"Pilihan default dipakai dalam {Math.Ceiling(remaining.TotalSeconds)} dtk";
    }

    private static Button CreateButton(string text, bool primary) => new()
    {
        Content = text,
        MinWidth = 92,
        Height = 34,
        Padding = new Thickness(14, 0, 14, 0),
        Background = primary
            ? new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xF6))
            : new SolidColorBrush(Color.FromRgb(0x36, 0x3B, 0x46)),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        IsDefault = primary,
        IsCancel = !primary
    };
}
