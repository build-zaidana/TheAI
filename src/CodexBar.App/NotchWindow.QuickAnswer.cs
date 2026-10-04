using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Answering with the agent's own recommendation in one click. Every request gets a recommended
// answer when there is a safe one: "allow once" for an approval, and for a question the option
// the agent marked "(Recommended)" (else its first option). A question with free text or a secret
// has none and always goes through the full request view.
//
// Two surfaces use it: a small Quick card the notch opens into when a request arrives while it
// is compact, and a "Menunggu kamu" section at the top of the launcher. Both are one click and
// never answer on their own.
public partial class NotchWindow
{
    private static readonly Regex RecommendedMark =
        new(@"\s*[\(\[]\s*(recommended|disarankan|rekomendasi)\s*[\)\]]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string _quickKind = string.Empty;
    private string _quickText = string.Empty;
    private string? _quickLabel;
    private Action? _quickAnswer;

    /// <summary>Prepare the recommendation for an approval (called when the request arrives).</summary>
    private void PrepareQuickApproval(string kind, string text)
    {
        _quickKind = kind;
        _quickText = text;
        _quickLabel = Tr.Text("Izinkan sekali", "Allow once");
        _quickAnswer = () => FinishApproval(ProviderApprovalDecision.AcceptOnce);
    }

    /// <summary>Prepare the recommendation for a question; none when any answer must be typed.</summary>
    private void PrepareQuickInput(ProviderUserInputEvent input)
    {
        _quickKind = string.Format(Tr.Text("{0} bertanya", "{0} is asking"), _activity.IsMain ? _host.ProviderName : _activity.Provider);
        _quickText = input.Questions.Count == 1 ? input.Questions[0].Question : input.Title;
        _quickLabel = null;
        _quickAnswer = null;

        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (var question in input.Questions)
        {
            if (question.Options.Count == 0 || question.IsSecret)
            {
                if (question.IsRequired)
                {
                    return; // something has to be typed: no one-click answer
                }

                continue;
            }

            var pick = question.Options.FirstOrDefault(o => RecommendedMark.IsMatch(o.Label)) ?? question.Options[0];
            answers[question.Id] = [pick.Value ?? pick.Label];
            labels.Add(RecommendedMark.Replace(pick.Label, " ").Trim());
        }

        if (answers.Count == 0)
        {
            return;
        }

        _quickLabel = labels.Count == 1 ? labels[0] : Tr.Text("Pakai rekomendasi", "Use recommended");
        _quickAnswer = () => FinishInput(answers);
    }

    private bool HasQuickAnswer => _quickAnswer is not null;

    // ---- Quick card (compact notch) --------------------------------------------------------

    private void FillQuickPanel()
    {
        QuickKind.Text = _quickKind;
        QuickText.Text = _quickText;
        if (_approvalSource is not null)
        {
            QuickText.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        }
        else
        {
            QuickText.ClearValue(TextBlock.FontFamilyProperty);
        }
        QuickPrimary.Content = CheckLabel(_quickLabel ?? string.Empty, Brushes.Black);
        if (_approvalSource is not null)
        {
            QuickSecondary.Content = Tr.Text("Tolak", "Decline");
            QuickMore.Visibility = Visibility.Visible;
        }
        else
        {
            QuickSecondary.Content = Tr.Text("Pilihan lain", "Other answers");
            QuickMore.Visibility = Visibility.Collapsed;
        }
    }

    private void QuickPrimary_Click(object sender, RoutedEventArgs e) => _quickAnswer?.Invoke();

    private void QuickSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_approvalSource is not null)
        {
            FinishApproval(ProviderApprovalDecision.Decline);
        }
        else
        {
            ShowFullRequest();
        }
    }

    private void QuickMore_Click(object sender, RoutedEventArgs e) => ShowFullRequest();

    private void ShowFullRequest()
    {
        _view = NotchView.Request;
        ApplyView(animate: true);
        UpdateVisibility();
        Activate();
    }

    // ---- "Menunggu kamu" in the launcher ---------------------------------------------------

    private void RenderWaiting()
    {
        WaitingList.Children.Clear();
        if (!HasPendingRequest)
        {
            WaitingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WaitingHeader.Text = L("notch.waiting.title");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var choice = ProviderChoices.FirstOrDefault(c => c.Id == _host.ProviderId);
        var tile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("NotchFill"),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new Path
            {
                Data = Logo(choice.Logo ?? OpenAiLogo),
                Fill = choice.ClaudeColor ? ClaudeBrush : Brushes.White,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform
            }
        };
        grid.Children.Add(tile);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = _quickText.ReplaceLineEndings(" "), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        meta.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = (Brush)FindResource("NotchOrange"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        meta.Children.Add(new TextBlock
        {
            Text = _activity.IsMain ? $"{_quickKind} · {_host.WorkspaceName}" : $"{_quickKind} · {L("notch.agents.parallel")}",
            FontSize = 11,
            Foreground = (Brush)FindResource("NotchText3")
        });
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (HasQuickAnswer)
        {
            var answer = new Button
            {
                Style = (Style)FindResource("NotchPrimaryPill"),
                Height = 30,
                Margin = new Thickness(10, 0, 0, 0),
                Content = CheckLabel(_quickLabel ?? string.Empty, Brushes.Black)
            };
            answer.Click += (_, _) => _quickAnswer?.Invoke();
            Grid.SetColumn(answer, 2);
            grid.Children.Add(answer);
        }

        var more = new Button
        {
            Style = (Style)FindResource("NotchIconButton"),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = Tr.Text("Pilihan lain", "Other answers"),
            Content = Dots()
        };
        more.Click += (_, _) => ShowFullRequest();
        Grid.SetColumn(more, 3);
        grid.Children.Add(more);

        WaitingList.Children.Add(new Border
        {
            Height = 56,
            Padding = new Thickness(8, 0, 6, 0),
            CornerRadius = new CornerRadius(14),
            Background = (Brush)FindResource("NotchFill"),
            Child = grid
        });
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private static StackPanel CheckLabel(string text, Brush color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Path
        {
            Data = Geometry.Parse("M 1,5 L 4.2,8.2 L 10,1.8"),
            Stroke = color,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static StackPanel Dots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++)
        {
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Fill = Brushes.White, Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) });
        }

        return row;
    }
}
