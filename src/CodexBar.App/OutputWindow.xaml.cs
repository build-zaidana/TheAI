using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.Windows;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace CodexBar.App;

public partial class OutputWindow : Window
{
    private bool _hasManualPosition;
    private bool _allowClose;

    public OutputWindow()
    {
        InitializeComponent();
        OutputConversation.ScrollChanged += ResponseScroll_ScrollChanged;
    }

    public event ScrollChangedEventHandler? OutputScrollChanged;
    public event EventHandler? JumpLatestRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? OpenInCodexRequested;

    public ConversationView Conversation => OutputConversation;

    public void PrepareForExit() => _allowClose = true;

    public void ApplyBackdrop(bool dark)
    {
        var background = dark
            ? Color.FromRgb(23, 25, 29)
            : Color.FromRgb(247, 248, 250);
        RootBorder.Background = new SolidColorBrush(background);
        RootBorder.BorderBrush = new SolidColorBrush(
            dark ? Color.FromRgb(58, 63, 72) : Color.FromRgb(210, 215, 223));
        var primary = new SolidColorBrush(
            dark ? Color.FromRgb(244, 246, 250) : Color.FromRgb(28, 32, 40));
        var muted = new SolidColorBrush(
            dark ? Color.FromRgb(208, 214, 224) : Color.FromRgb(82, 91, 108));
        TitleText.Foreground = primary;
        ContextText.Foreground = muted;
        CopyOutputButton.Foreground = primary;
        OpenCodexButton.Foreground = primary;
        CloseOutputButton.Foreground = primary;
        WindowBackdrop.ApplySolid(this, dark, background);
    }

    public void ShowNear(Window anchor)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_hasManualPosition)
        {
            var workArea = WindowPositioner.GetWorkArea(anchor);
            Left = Math.Clamp(anchor.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
            var preferredTop = anchor.Top - ActualHeight - 10;
            Top = preferredTop >= workArea.Top
                ? preferredTop
                : Math.Clamp(anchor.Top + anchor.ActualHeight + 10, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        }
    }

    public void SetContext(string workspaceName, string status)
    {
        ContextText.Text = $"{workspaceName} · {status}";
    }

    public void SetJumpLatestVisible(bool visible) =>
        JumpLatestButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void ScrollToEnd() => OutputConversation.ScrollToEnd();

    private void ResponseScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        OutputScrollChanged?.Invoke(sender, e);

    private void JumpLatestButton_Click(object sender, RoutedEventArgs e) =>
        JumpLatestRequested?.Invoke(this, EventArgs.Empty);

    private void CopyButton_Click(object sender, RoutedEventArgs e) =>
        CopyRequested?.Invoke(this, EventArgs.Empty);

    private void OpenCodexButton_Click(object sender, RoutedEventArgs e) =>
        OpenInCodexRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        _hasManualPosition = true;
    }

}
