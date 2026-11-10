using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace CodexBar.App;

/// <summary>
/// The frame the code-built dialogs share (canvas board 37): the same window, title and close button
/// as Settings, in the current theme. Closing it leaves DialogResult unset, the same as cancelling.
/// </summary>
internal static class FluentDialog
{
    private const double ShadowRoom = 22;

    /// <summary>Call first, so <see cref="Style"/> can find the theme's styles.</summary>
    public static void Prepare(Window window, double width)
    {
        // An app without our colours (the UI tests) still gets them, in light.
        if (window.TryFindResource("FluentText") is null)
        {
            window.Resources.MergedDictionaries.Add(Dictionary("FluentLight.xaml"));
        }

        window.Resources.MergedDictionaries.Add(Dictionary("FluentTheme.xaml"));
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.ResizeMode = ResizeMode.NoResize;
        window.UseLayoutRounding = true;
        window.Width = width + ShadowRoom * 2;
        window.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        window.FontSize = 14;
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "FluentText");
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
    }

    /// <summary>Puts the title, the close button and the body into the window.</summary>
    public static void Dress(Window window, string title, UIElement body)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 20,
            LineHeight = 28,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 40, 16)
        };
        var stack = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        DockPanel.SetDock(heading, Dock.Top);
        stack.Children.Add(heading);
        stack.Children.Add(body);

        var close = new Button
        {
            Style = Style(window, "FluentCaptionClose"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = Tr.Text("Tutup", "Close")
        };
        System.Windows.Automation.AutomationProperties.SetName(close, Tr.Text("Tutup", "Close"));
        close.Click += (_, _) => window.Close();

        var layers = new Grid();
        layers.Children.Add(stack);
        layers.Children.Add(close);

        var frame = new Border
        {
            Margin = new Thickness(ShadowRoom),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.28 },
            Child = layers
        };
        frame.SetResourceReference(Border.BackgroundProperty, "FluentWindow");
        frame.SetResourceReference(Border.BorderBrushProperty, "FluentEdge");
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                window.DragMove();
            }
        };
        window.Content = frame;
    }

    /// <summary>These windows don't resize, so on a small screen they shrink to fit and scroll, instead of running off it.</summary>
    public static void FitWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Width = Math.Min(window.Width, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
    }

    public static Style Style(Window window, string key) => (Style)window.FindResource(key);

    public static Button Button(Window window, string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 80,
        Style = Style(window, primary ? "FluentPrimaryButton" : "FluentButton")
    };

    public static TextBlock Label(string text, bool strong = false, bool quiet = false)
    {
        var label = new TextBlock
        {
            Text = text,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal
        };
        if (quiet)
        {
            label.SetResourceReference(TextBlock.ForegroundProperty, "FluentText2");
        }

        return label;
    }

    private static ResourceDictionary Dictionary(string file) =>
        new() { Source = new Uri($"pack://application:,,,/TheAI;component/{file}") };
}
