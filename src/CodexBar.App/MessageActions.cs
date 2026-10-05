using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;

namespace CodexBar.App;

/// <summary>
/// The small icon buttons under messages and on code blocks, as in Claude: icon only (the name
/// is in the tooltip), muted, a soft square behind it on hover; copy shows a check for a moment.
/// </summary>
internal static class MessageActions
{
    public static readonly Geometry CopyIcon = Geometry.Parse(
        "M 4,4 L 4,2 Q 4,1 5,1 L 11,1 Q 12,1 12,2 L 12,8 Q 12,9 11,9 L 9,9 M 2,4 L 8,4 Q 9,4 9,5 L 9,11 Q 9,12 8,12 L 2,12 Q 1,12 1,11 L 1,5 Q 1,4 2,4 Z");

    public static readonly Geometry CheckIcon = Geometry.Parse("M 1.5,6.5 L 4.8,9.8 L 11.5,2.5");

    // Circular arrow (retry).
    public static readonly Geometry RetryIcon = Geometry.Parse(
        "M 11.2,4.2 A 5,5 0 1 0 11.6,8 M 11.4,1 L 11.4,4.4 L 8,4.4");

    // Pencil (edit).
    public static readonly Geometry EditIcon = Geometry.Parse(
        "M 8.6,2.2 L 10.8,4.4 M 1.5,11.5 L 2,8.8 L 9.3,1.5 Q 9.8,1 10.3,1.5 L 11.5,2.7 Q 12,3.2 11.5,3.7 L 4.2,11 Z");

    // Three dots (more).
    public static readonly Geometry MoreIcon = Geometry.Parse(
        "M 2,6.5 A 0.6,0.6 0 1 1 2,6.49 M 6.5,6.5 A 0.6,0.6 0 1 1 6.5,6.49 M 11,6.5 A 0.6,0.6 0 1 1 11,6.49");

    // A list (chapters).
    public static readonly Geometry ListIcon = Geometry.Parse("M 4,2.5 L 12,2.5 M 4,6.5 L 12,6.5 M 4,10.5 L 12,10.5 M 1,2.5 L 1.4,2.5 M 1,6.5 L 1.4,6.5 M 1,10.5 L 1.4,10.5");

    /// <summary>A small dark menu above or below <paramref name="anchor"/>: one row per (label, action).</summary>
    public static void ShowMenu(FrameworkElement anchor, IEnumerable<(string Label, Action Run)> items, bool above = false)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = above ? System.Windows.Controls.Primitives.PlacementMode.Top : System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = above ? -4 : 4,
            AllowsTransparency = true,
            StaysOpen = false
        };
        var list = new StackPanel { MinWidth = 180 };
        foreach (var (label, run) in items)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            };
            var row = new Border
            {
                Child = text,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            row.MouseEnter += (_, _) => row.Background = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
            var action = run;
            row.MouseLeftButtonUp += (_, _) =>
            {
                popup.IsOpen = false;
                action();
            };
            System.Windows.Automation.AutomationProperties.SetName(row, label);
            list.Children.Add(row);
        }

        popup.Child = new Border
        {
            Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = list
        };
        popup.IsOpen = true;
    }

    public static Button Create(Geometry icon, string name, Brush muted, Brush hover, double size = 26)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = icon,
            Stroke = muted,
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform
        };

        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Border), "Bg");
        root.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);
        template.VisualTree = root;
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), "Bg"));
        template.Triggers.Add(hoverTrigger);

        var button = new Button
        {
            Content = path,
            Template = template,
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            ToolTip = name,
            Tag = path
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.MouseEnter += (_, _) => path.Stroke = hover;
        button.MouseLeave += (_, _) => path.Stroke = muted;
        return button;
    }

    /// <summary>A copy button: puts <paramref name="text"/> on the clipboard, then shows a check briefly.</summary>
    public static Button Copy(Func<string> text, string name, Brush muted, Brush hover, double size = 26)
    {
        var button = Create(CopyIcon, name, muted, hover, size);
        button.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text());
            }
            catch
            {
                return; // the clipboard can be locked by another app for a moment
            }

            if (button.Tag is System.Windows.Shapes.Path path)
            {
                path.Data = CheckIcon;
                var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                back.Tick += (_, _) =>
                {
                    back.Stop();
                    path.Data = CopyIcon;
                };
                back.Start();
            }
        };
        return button;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
