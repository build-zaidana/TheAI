using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.Providers;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

internal sealed class ApprovalRequestDialog : Window
{
    public ProviderApprovalDecision Decision { get; private set; } = ProviderApprovalDecision.Decline;

    public ApprovalRequestDialog(ProviderApprovalEvent approval)
    {
        var loc = CodexBar.Core.Localization.Instance;
        Title = loc["approval.title"];
        Width = 560;
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C));
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = loc["approval.heading"],
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = loc["approval.subtitle"],
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC))
        });
        var details = new TextBox
        {
            Text = approval.Summary,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 390,
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x20)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x4B, 0x57)),
            BorderThickness = new Thickness(1)
        };
        System.Windows.Automation.AutomationProperties.SetName(
            details, loc["approval.detailsName"]);
        root.Children.Add(details);

        var buttons = new WrapPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(CreateDecisionButton(loc["approval.cancel"], ProviderApprovalDecision.Cancel, false));
        buttons.Children.Add(CreateDecisionButton(loc["approval.decline"], ProviderApprovalDecision.Decline, false));
        buttons.Children.Add(CreateDecisionButton(loc["approval.acceptSession"], ProviderApprovalDecision.AcceptForSession, false));
        buttons.Children.Add(CreateDecisionButton(loc["approval.acceptOnce"], ProviderApprovalDecision.AcceptOnce, true));
        root.Children.Add(buttons);
        Content = root;
    }

    private Button CreateDecisionButton(
        string text,
        ProviderApprovalDecision decision,
        bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 34,
            MinWidth = 92,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xF6))
                : new SolidColorBrush(Color.FromRgb(0x36, 0x3B, 0x46)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            IsDefault = primary,
            IsCancel = decision == ProviderApprovalDecision.Decline
        };
        button.Click += (_, _) =>
        {
            Decision = decision;
            DialogResult = true;
            Close();
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }
}
