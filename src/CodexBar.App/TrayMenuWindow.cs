using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace CodexBar.App;

/// <summary>
/// The tray icon's menu, drawn in the notch's own language: a black rounded panel with a thin
/// light rim, white text, stroke icons, and the display mode as an inline segmented control
/// instead of a submenu. It opens beside the cursor (above the taskbar) and closes when it loses
/// focus, on Esc, or after a choice; the chosen action runs once the menu is gone.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private static readonly Brush Text = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TextDim = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Icon = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hover = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Rim = Frozen(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Divider = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    private readonly StackPanel _items = new();
    private Action? _pending;
    private bool _closing;

    public TrayMenuWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = Text;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var panel = new Border
        {
            Width = 260,
            Margin = new Thickness(16),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0C)),
            BorderBrush = Rim,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            Child = _items,
            RenderTransformOrigin = new Point(1, 1),
            RenderTransform = new ScaleTransform(0.96, 0.96)
        };
        Content = panel;
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; // placed next to the cursor once its size is known
        Top = -10000;

        Deactivated += (_, _) => Dismiss();
        KeyDown += OnKey;
        Loaded += (_, _) =>
        {
            Place();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            var scale = (ScaleTransform)panel.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            Activate();
        };
        Closed += (_, _) => _pending?.Invoke();
    }

    // ---- Building --------------------------------------------------------------------------

    /// <summary>Agent status line at the top (green: working, orange: needs you, red: error).</summary>
    public void AddStatus(string text, Color dot, Action? onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dot), Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = onClick is null ? TextDim : Text, VerticalAlignment = VerticalAlignment.Center });
        _items.Children.Add(onClick is null
            ? new Border { Height = 32, Padding = new Thickness(10, 0, 10, 0), Child = row }
            : Row(row, onClick));
        AddSeparator();
    }

    public void AddItem(string iconData, string label, Action action, string? trailing = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse(iconData),
            Stroke = Icon,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing is not null)
        {
            var hint = new TextBlock { Text = trailing, FontSize = 11, Foreground = TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
        }

        _items.Children.Add(Row(grid, action));
    }

    /// <summary>A labelled segmented control (the display mode: Notch · Island · Bar).</summary>
    public void AddSegments(string label, IReadOnlyList<(string Key, string Text)> options, string selected, Action<string> pick)
    {
        var block = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        block.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = TextDim, Margin = new Thickness(0, 0, 0, 6) });
        var track = new Border
        {
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
        };
        var segments = new UniformGrid { Rows = 1 };
        foreach (var option in options)
        {
            var isOn = option.Key == selected;
            var button = new Button
            {
                Content = option.Text,
                Height = 28,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Template = SegmentTemplate(isOn)
            };
            var key = option.Key;
            button.Click += (_, _) => Choose(() => pick(key));
            segments.Children.Add(button);
        }

        track.Child = segments;
        block.Children.Add(track);
        _items.Children.Add(block);
    }

    public void AddSeparator() =>
        _items.Children.Add(new Border { Height = 1, Background = Divider, Margin = new Thickness(8, 4, 8, 4) });

    // ---- Rows ------------------------------------------------------------------------------

    private Button Row(UIElement content, Action action)
    {
        var button = new Button
        {
            Content = content,
            Height = 34,
            Foreground = Text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = RowTemplate()
        };
        button.Click += (_, _) => Choose(action);
        return button;
    }

    private static ControlTemplate RowTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "Bg"));
        template.Triggers.Add(hover);
        return template;
    }

    private static ControlTemplate SegmentTemplate(bool selected)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, selected ? Brushes.White : Brushes.Transparent);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        text.SetValue(TextBlock.ForegroundProperty, selected ? Brushes.Black : Text);
        text.SetValue(TextBlock.FontWeightProperty, selected ? FontWeights.SemiBold : FontWeights.Normal);
        text.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);
        template.VisualTree = border;
        if (!selected)
        {
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "Bg"));
            template.Triggers.Add(hover);
        }

        return template;
    }

    // ---- Behaviour -------------------------------------------------------------------------

    private void Choose(Action action)
    {
        _pending = action;
        Dismiss();
    }

    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
        }
    }

    // Bottom-right corner at the cursor (the tray sits at the bottom right), kept on the work area
    // of the monitor the cursor is on. The 16 px margin around the panel holds its shadow.
    private void Place()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double Dx(double px) => px / dpi.DpiScaleX;
        double Dy(double px) => px / dpi.DpiScaleY;

        var left = Dx(cursor.X) - ActualWidth + 16;
        var top = Dy(cursor.Y) - ActualHeight + 16;
        Left = Math.Clamp(left, Dx(area.Left) - 8, Dx(area.Right) - ActualWidth + 8);
        Top = Math.Clamp(top, Dy(area.Top) - 8, Dy(area.Bottom) - ActualHeight + 8);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
