using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexBar.App;

/// <summary>
/// The first start: a short tour (what the notch is, the hotkey, "/"), then the features to switch
/// on — all optional, a few suggested. Reopened later from Settings, it shows what's on now.
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>Raised with the chosen feature keys (and whether to follow Claude Code sessions).</summary>
    public event Action<IReadOnlyCollection<string>, bool>? Finished;

    private readonly Dictionary<string, CheckBox> _switches = [];
    private CheckBox? _claudeSessions;

    /// <param name="current">What's on now (a new user: nothing; the suggested ones are pre-ticked).</param>
    /// <param name="hotkey">The launcher hotkey, as the user knows it.</param>
    /// <param name="claudeInstalled">Offer to show Claude Code sessions (installs hooks when chosen).</param>
    public WelcomeWindow(AppSettings current, string hotkey, bool claudeInstalled, bool tour = true)
    {
        InitializeComponent();
        FluentDialog.FitWorkArea(this);
        BuildTips(hotkey);
        BuildFeatures(current, claudeInstalled);
        if (!tour)
        {
            StepDots.Visibility = Visibility.Collapsed;
            ShowFeatures();
        }
    }

    private void BuildTips(string hotkey)
    {
        (string Key, string Text)[] tips =
        [
            (Tr.Text("Arahkan kursor", "Hover"), Tr.Text("ke notch untuk membukanya, lalu scroll untuk berpindah halaman.", "over the notch to open it, then scroll to move between its pages.")),
            (hotkey, Tr.Text("membuka launcher dari mana saja. Ketik pertanyaan, lalu Enter.", "opens the launcher from anywhere. Type a question and press Enter.")),
            ("/", Tr.Text("di launcher menampilkan perintah seperti /review.", "in the launcher lists commands like /review and /focus 25.")),
        ];
        foreach (var (key, text) in tips)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = key, FontWeight = FontWeights.SemiBold, FontSize = 14, LineHeight = 20, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20 };
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            Tips.Children.Add(row);
        }
    }

    private void BuildFeatures(AppSettings current, bool claudeInstalled)
    {
        var anyOn = NotchFeatures.All.Any(feature => feature.IsOn(current));
        (string Key, string Title)[] groups =
        [
            (NotchFeatures.Day, Tr.Text("Harimu", "Your day")),
            (NotchFeatures.Work, Tr.Text("Kerja dengan agent", "Working with agents")),
            (NotchFeatures.Computer, Tr.Text("Komputer", "Your computer")),
        ];
        foreach (var (group, title) in groups)
        {
            FeatureGroups.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("FluentSectionTitle"), Margin = new Thickness(2, 6, 0, 6) });
            var box = new StackPanel();
            if (group == NotchFeatures.Work && claudeInstalled)
            {
                _claudeSessions = AddRow(box, Tr.Text("Sesi Claude Code di notch", "Claude Code sessions in the notch"),
                    Tr.Text("Sesi dari terminal, VS Code, dan aplikasi desktop: langkah, jawaban, dan izin. Menambah hooks di ~/.claude/settings.json.",
                        "Sessions from the terminal, VS Code and the desktop app: steps, answers and permission requests. Adds hooks to ~/.claude/settings.json."),
                    current.ClaudeLiveSessions || !anyOn);
            }

            foreach (var feature in NotchFeatures.All.Where(feature => feature.Group == group))
            {
                // A new user (nothing on yet) sees the suggestions ticked; later, what's on now.
                var on = anyOn ? feature.IsOn(current) : feature.Recommended;
                var toggle = _switches[feature.Key] = AddRow(box, feature.Name(Tr.Text), feature.Description(Tr.Text), on);
                var key = feature.Key;
                var name = feature.Name(Tr.Text);
                ((FrameworkElement)toggle.Parent).MouseEnter += (_, _) => ShowPreview(key, name);
                toggle.GotKeyboardFocus += (_, _) => ShowPreview(key, name);
            }

            FeatureGroups.Children.Add(new Border { Style = (Style)FindResource("FluentGroupBox"), Margin = new Thickness(0, 0, 0, 14), Child = box });
        }
    }

    private CheckBox AddRow(StackPanel box, string name, string description, bool on)
    {
        if (box.Children.Count > 0)
        {
            box.Children.Add(new Border { Style = (Style)FindResource("FluentSeparator") });
        }

        var row = new Grid { Style = (Style)FindResource("FluentRow"), MinHeight = 52 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 64, 8) };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("FluentRowLabel") });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = (Brush)FindResource("FluentText2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(text);
        var toggle = new CheckBox
        {
            Style = (Style)FindResource("FluentSwitch"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = on
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(toggle, description);
        row.Children.Add(toggle);
        box.Children.Add(row);
        return toggle;
    }

    /// <summary>The notch picture for a feature (packed with the app), or just its name when there is none.</summary>
    private void ShowPreview(string key, string name)
    {
        PreviewImage.Source = PreviewFor(key);
        PreviewCaption.Text = name;
    }

    private static System.Windows.Media.Imaging.BitmapImage? PreviewFor(string key)
    {
        var file = key == "github" ? "activities" : key;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Previews/{file}.png");
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UriFormatException or NotSupportedException)
        {
            return null;   // music and the companion have no picture
        }
    }

    // Minimal: what nearly everyone uses. Recommended: that plus the timer. Everything: all on.
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var keys = ((FrameworkElement)sender).Tag switch
        {
            "minimal" => NotchFeatures.Minimal,
            "all" => NotchFeatures.Everything,
            _ => NotchFeatures.Suggested
        };
        foreach (var (key, toggle) in _switches)
        {
            toggle.IsChecked = keys.Contains(key);
        }

        var names = string.Join(", ", NotchFeatures.All.Where(feature => keys.Contains(feature.Key)).Select(feature => feature.Name(Tr.Text)).Take(4));
        ShowPreview("today", keys.Count == NotchFeatures.All.Count ? Tr.Text("Semua fitur menyala", "Every feature on") : names);
    }

    private void ShowFeatures()
    {
        TourStep.Visibility = Visibility.Collapsed;
        FeatureStep.Visibility = Visibility.Visible;
        BackButton.Visibility = StepDots.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Tr.Text("Mulai", "Start");
        Dot1.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FluentDotOff");
        Dot2.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FluentText");
        CloseCaption.ClearValue(ForegroundProperty);   // no longer over the dark picture
        ShowPreview("today", Tr.Text("Arahkan ke fitur untuk melihat contohnya", "Point at a feature to see what it looks like"));
        _switches.Values.FirstOrDefault()?.Focus();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (TourStep.Visibility == Visibility.Visible)
        {
            ShowFeatures();
            return;
        }

        var chosen = _switches.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        Finished?.Invoke(chosen, _claudeSessions?.IsChecked == true);
        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        FeatureStep.Visibility = Visibility.Collapsed;
        TourStep.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Content = Tr.Text("Lanjut", "Continue");
        Dot1.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FluentText");
        Dot2.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FluentDotOff");
        CloseCaption.Foreground = System.Windows.Media.Brushes.White;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not CheckBox and not System.Windows.Controls.Primitives.ButtonBase)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Closing without choosing keeps things as they are (it opens again from Settings).
            Close();
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
