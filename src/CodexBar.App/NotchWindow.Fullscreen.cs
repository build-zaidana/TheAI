using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexBar.App;

// Out of the way in fullscreen: a game, a video, a presentation covering the notch's monitor hides
// the whole notch until it ends — the most common complaint about notch apps is that they don't.
// No polling: Windows tells us when the foreground window changes and when that window moves or
// resizes (F11, a game going fullscreen), and when displays change. Sounds such as an alarm still play.
public partial class NotchWindow
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;

    private readonly DispatcherTimer _fullscreenCheck = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _fullscreenHidden;
    private readonly int _ownProcess = Environment.ProcessId;
    private WinEventProc? _onWinEvent;   // kept alive: Windows calls it
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private IntPtr _watchedWindow;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    private void InitFullscreen()
    {
        // A burst of move/resize events becomes one check, a moment later.
        _fullscreenCheck.Tick += (_, _) =>
        {
            _fullscreenCheck.Stop();
            CheckFullscreen();
        };
        _onWinEvent = OnWinEvent;
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _onWinEvent, 0, 0, WinEventOutOfContext);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        WatchForeground(GetForegroundWindow());
        Closed += (_, _) =>
        {
            _fullscreenCheck.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            UnhookWinEvent(_foregroundHook);
            UnhookWinEvent(_locationHook);
        };
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ScheduleFullscreenCheck);

    private void ScheduleFullscreenCheck()
    {
        _fullscreenCheck.Stop();
        _fullscreenCheck.Start();
    }

    // Follow the foreground window's own moves and resizes (only that process: cheap).
    private void WatchForeground(IntPtr window)
    {
        _watchedWindow = window;
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        if (window != IntPtr.Zero && _onWinEvent is not null)
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId != _ownProcess)
            {
                _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _onWinEvent, (uint)processId, 0, WinEventOutOfContext);
            }
        }

        ScheduleFullscreenCheck();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (eventType == EventSystemForeground)
        {
            WatchForeground(window);
        }
        else if (eventType == EventObjectLocationChange && objectId == 0 && window == _watchedWindow)
        {
            ScheduleFullscreenCheck();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private void CheckFullscreen()
    {
        var hide = _host.HideInFullscreen && SomethingFullscreen();
        if (hide == _fullscreenHidden)
        {
            return;
        }

        _fullscreenHidden = hide;
        if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_FULLSCREEN") is { Length: > 0 })
        {
            CodexBar.Core.DiagnosticsLog.Info("Fullscreen", hide ? "hidden" : "shown");
        }

        if (hide)
        {
            Hide();
        }
        else
        {
            UpdateVisibility();
        }
    }

    /// <summary>Is a fullscreen app covering the monitor the notch is on?</summary>
    private bool SomethingFullscreen()
    {
        // Windows' own verdict: exclusive-mode Direct3D (games), presentation mode, a "busy" fullscreen app.
        if (SHQueryUserNotificationState(out var state) == 0 && state is 2 /* busy */ or 3 /* D3D fullscreen */ or 4 /* presentation */)
        {
            return true;
        }

        // Borderless fullscreen (most games now, F11 in a browser, video players): the foreground
        // window covers its whole monitor, and that monitor is ours.
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground))
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == _ownProcess)
        {
            return false;
        }

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;   // the desktop and the taskbar cover the screen too, but they aren't "fullscreen"
        }

        var ours = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(foreground, 2 /* nearest */);
        if (ours != IntPtr.Zero && monitor != MonitorFromWindow(ours, 2))
        {
            return false;   // fullscreen on another monitor: the notch can stay
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
