using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Prompts queued behind the running turn (the queue itself lives in the host). Shown in the
// activity view under the output; while the agent works they simply wait, and once it stops
// with prompts still waiting (stopped or failed turn) the list says "paused" and offers
// "send now".
public partial class NotchWindow
{
    /// <summary>Re-render after the host's queue changed.</summary>
    public void RefreshQueue()
    {
        var queued = _host.QueuedPrompts;
        QueueList.Children.Clear();
        for (var i = 0; i < queued.Count; i++)
        {
            QueueList.Children.Add(BuildQueueRow(i, queued[i]));
        }

        QueueBox.Visibility = queued.Count > 0 && _activity.IsMain ? Visibility.Visible : Visibility.Collapsed;
        QueueTitleRun.Text = $"{L("notch.queue.title")} · {queued.Count}";
        QueueSendNowButton.Content = L("notch.queue.sendNow");
        ApplyQueueState();
    }

    private void ApplyQueueState()
    {
        var paused = !_host.IsBusy && !MainActivity.IsBusy && _host.QueuedPrompts.Count > 0;
        QueueStateRun.Text = paused ? "  " + L("notch.queue.paused") : string.Empty;
        QueueSendNowButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildQueueRow(int index, string prompt)
    {
        var remove = new Button
        {
            Style = (Style)FindResource("NotchIconButton"),
            Width = 24,
            Height = 24,
            Tag = index,
            ToolTip = L("notch.queue.remove"),
            Content = new Path
            {
                Stroke = (Brush)FindResource("NotchText2"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7")
            }
        };
        remove.Click += RemoveQueued_Click;

        var grid = new Grid { Height = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = (index + 1).ToString(),
            FontSize = 11,
            Foreground = (Brush)FindResource("NotchText3"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new TextBlock
        {
            Text = prompt.ReplaceLineEndings(" "),
            FontSize = 13,
            Foreground = (Brush)FindResource("NotchText2"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            ToolTip = prompt
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        return new Border
        {
            Background = (Brush)FindResource("NotchFill"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid
        };
    }

    private void RemoveQueued_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int index)
        {
            _host.RemoveQueuedPrompt(index);
        }
    }

    private async void QueueSendNow_Click(object sender, RoutedEventArgs e) => await _host.SendNextQueuedAsync();
}
