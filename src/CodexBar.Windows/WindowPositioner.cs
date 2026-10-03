using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexBar.Windows;

public static class WindowPositioner
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void PlaceAboveTaskbar(Window window, string position, double margin = 12)
    {
        var cursor = new NativePoint();
        GetCursorPos(ref cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var source = PresentationSource.FromVisual(window);
        var dpi = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var workTopLeft = dpi.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        var workBottomRight = dpi.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
        var workWidth = workBottomRight.X - workTopLeft.X;

        window.Left = position switch
        {
            "bottom-left" => workTopLeft.X + margin,
            "bottom-right" => workBottomRight.X - window.ActualWidth - margin,
            _ => workTopLeft.X + ((workWidth - window.ActualWidth) / 2)
        };
        window.Top = workBottomRight.Y - window.ActualHeight - margin;
    }

    /// <summary>
    /// Places <paramref name="window"/> flush against the top edge of the monitor under the
    /// cursor, horizontally centred — the anchor for the notch-style surface.
    /// </summary>
    public static void PlaceTopCenter(Window window)
    {
        var cursor = new NativePoint();
        GetCursorPos(ref cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var source = PresentationSource.FromVisual(window);
        var dpi = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = dpi.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        var bottomRight = dpi.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        window.Left = topLeft.X + ((bottomRight.X - topLeft.X - window.ActualWidth) / 2);
        window.Top = topLeft.Y;
    }

    /// <summary>Full bounds (not work area) of the monitor the window is on, in DIPs.</summary>
    public static Rect GetMonitorBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = handle != 0 ? MonitorFromWindow(handle, MonitorDefaultToNearest) : 0;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(
            transform.Transform(new Point(info.Monitor.Left, info.Monitor.Top)),
            transform.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom)));
    }

    public static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = handle != 0
            ? MonitorFromWindow(handle, MonitorDefaultToNearest)
            : 0;

        if (monitor == 0)
        {
            var cursor = new NativePoint();
            GetCursorPos(ref cursor);
            monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = transform.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
