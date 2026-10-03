using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexBar.App;

/// <summary>One line of the onboarding checklist ("Claude Code · 2.1.212 ✓").</summary>
internal sealed record OnboardingStatus(string Label, string Value, bool Ok)
{
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x28, 0xA7, 0x45));
    private static readonly Brush PendingBrush = Freeze(Color.FromRgb(0xF2, 0x9B, 0x0A));

    public bool First { get; init; }
    public Brush Dot => Ok ? OkBrush : PendingBrush;
    public Visibility CheckVisibility => Ok ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SeparatorVisibility => First ? Visibility.Collapsed : Visibility.Visible;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>What the onboarding sheet shows and what its buttons do.</summary>
internal sealed record OnboardingContent(
    string Title,
    string Body,
    IReadOnlyList<OnboardingStatus> Status,
    string? Command,
    string PrimaryText,
    Func<Task> Primary,
    string SecondaryText,
    Func<Task> Secondary,
    string? LinkText,
    Action? Link);

/// <summary>
/// macOS-style sheet shown instead of the bar's inline onboarding panel when the app runs as a
/// notch: what is missing (CLI not installed, not signed in, no API key, no workspace), the
/// command to fix it and buttons that run the same actions the old panel did.
/// </summary>
public partial class OnboardingWindow : Window
{
    private OnboardingContent? _content;

    public OnboardingWindow()
    {
        InitializeComponent();
        CopyButton.Content = Tr.Text("Salin", "Copy");
    }

    internal void Show(OnboardingContent content)
    {
        _content = content;
        TitleText.Text = content.Title;
        BodyText.Text = content.Body;
        StatusList.ItemsSource = content.Status.Select((s, i) => s with { First = i == 0 }).ToArray();
        CommandRow.Visibility = string.IsNullOrWhiteSpace(content.Command) ? Visibility.Collapsed : Visibility.Visible;
        CommandLead.Text = Tr.Text("Atau jalankan ", "Or run ");
        CommandText.Text = content.Command ?? string.Empty;
        CopyButton.Content = Tr.Text("Salin", "Copy");
        PrimaryButton.Content = content.PrimaryText;
        SecondaryButton.Content = content.SecondaryText;
        LinkButton.Content = content.LinkText ?? string.Empty;
        LinkButton.Visibility = content.Link is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;

        if (!IsVisible)
        {
            Show();
        }
        Activate();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e) => await RunAsync(_content?.Primary);

    private async void Secondary_Click(object sender, RoutedEventArgs e) => await RunAsync(_content?.Secondary);

    private async Task RunAsync(Func<Task>? action)
    {
        if (action is null)
        {
            return;
        }

        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e) => _content?.Link?.Invoke();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(CommandText.Text);
            CopyButton.Content = Tr.Text("Tersalin", "Copied");
        }
        catch
        {
            // Clipboard busy: nothing useful to do.
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }
}
