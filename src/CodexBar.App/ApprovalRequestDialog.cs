using System.Windows;
using System.Windows.Controls;
using CodexBar.Providers;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

internal sealed class ApprovalRequestDialog : Window
{
    public ProviderApprovalDecision Decision { get; private set; } = ProviderApprovalDecision.Decline;

    public ApprovalRequestDialog(ProviderApprovalEvent approval)
    {
        var loc = CodexBar.Core.Localization.Instance;
        Title = loc["approval.title"];
        FluentDialog.Prepare(this, 560);
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel();
        var subtitle = FluentDialog.Label(loc["approval.subtitle"], quiet: true);
        subtitle.Margin = new Thickness(0, -12, 0, 16);
        root.Children.Add(subtitle);
        var details = new TextBox
        {
            Style = FluentDialog.Style(this, "FluentArea"),
            Text = approval.Summary,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 390
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
        FluentDialog.Dress(this, loc["approval.heading"], root);
    }

    private Button CreateDecisionButton(
        string text,
        ProviderApprovalDecision decision,
        bool primary)
    {
        var button = FluentDialog.Button(this, text, primary);
        button.Margin = new Thickness(8, 0, 0, 0);
        button.IsDefault = primary;
        button.IsCancel = decision == ProviderApprovalDecision.Decline;
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
