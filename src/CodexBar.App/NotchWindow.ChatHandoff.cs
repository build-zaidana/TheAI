using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace CodexBar.App;

// Hand-off to the side chat. The chat grows out of the notch's own shape and folds back into
// it, so the notch reports where that shape is on screen and how its corners are rounded. While
// the chat is open the notch stays in place but fully transparent (and click-through): its
// compact shape can still be measured for the way back, and at the end it simply becomes visible
// again under the chat's last frame, so the swap has no gap.
public partial class NotchWindow
{
    private bool _suspendedForChat;

    private void CompactChatButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _hoverTimer.Stop();
        _host.OpenConversationWindow();
    }

    // The chat button sits on the logo's side of the compact notch, next to the logo when it shows.
    private void PlaceCompactChatButton()
    {
        var shift = CompactArt.Visibility == Visibility.Visible ? 24
            : CompactGlyph.Visibility == Visibility.Visible ? 19 : -4;
        var vertical = CompactGlyph.HorizontalAlignment == System.Windows.HorizontalAlignment.Center;
        CompactChatButton.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactChatButton.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactChatButton.Margin = vertical
            ? new Thickness(0, shift, 0, 0)
            : CompactGlyph.HorizontalAlignment == System.Windows.HorizontalAlignment.Right
                ? new Thickness(0, 0, shift, 0)
                : new Thickness(shift, 0, 0, 0);
    }

    /// <summary>The black shape's bounds in screen DIPs and its corner rounding (null when not showing).</summary>
    public (Rect Bounds, CornerRadius Corners)? ShapeScreenInfo()
    {
        if (!IsVisible || Shell.ActualWidth <= 0)
        {
            return null;
        }

        var topLeft = Shell.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = new Rect(topLeft.X / dpi.DpiScaleX, topLeft.Y / dpi.DpiScaleY, Shell.ActualWidth, Shell.ActualHeight);
        var corners = IsCorner
            ? CornerFor(Edge, 22) // the L sits in a screen corner: one rounded inner corner
            : Shell.CornerRadius;
        return (bounds, corners);
    }

    private static CornerRadius CornerFor(string edge, double radius) => edge switch
    {
        "top-right" => new CornerRadius(0, 0, 0, radius),
        "top-left" => new CornerRadius(0, 0, radius, 0),
        "bottom-right" => new CornerRadius(radius, 0, 0, 0),
        "bottom-left" => new CornerRadius(0, radius, 0, 0),
        _ => new CornerRadius(radius)
    };

    /// <summary>The chat took over: fold to compact and turn invisible (not hidden) until <see cref="ResumeAfterChat"/>.</summary>
    public void SuspendForChat()
    {
        _suspendedForChat = true;
        if (_view != NotchView.Compact)
        {
            _view = NotchView.Compact;
            _pinned = false;
            ApplyView(animate: false);
        }

        UpdateVisibility();
    }

    /// <summary>The chat folded back onto the notch: show it again.</summary>
    public void ResumeAfterChat()
    {
        _suspendedForChat = false;
        Opacity = 1;
        IsHitTestVisible = true;
        UpdateVisibility();
    }

    // While the chat is open the notch is see-through, unless the agent asks something.
    private bool ApplyChatSuspension()
    {
        if (!_suspendedForChat)
        {
            return false;
        }

        var asking = _view is NotchView.Request or NotchView.Quick;
        Opacity = asking ? 1 : 0;
        IsHitTestVisible = asking;
        if (!IsVisible)
        {
            Show();
        }

        return true;
    }
}
