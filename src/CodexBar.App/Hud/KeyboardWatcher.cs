using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexBar.App.Hud;

/// <summary>
/// Caps Lock and the keyboard language, for the HUD. Windows sends the notch's window a raw-input
/// message per key (RIDEV_INPUTSINK, so also while another app is in front); only whether the key
/// was Caps Lock is looked at, and after a key is released the foreground window's keyboard layout
/// is compared with the last one. Nothing is recorded or kept.
/// </summary>
internal sealed class KeyboardWatcher : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 0x01;
    private const ushort VkCapital = 0x14;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;

    private readonly HwndSource _source;
    private bool _capsOn;
    private IntPtr _layout;

    public event Action<bool>? CapsLockChanged;

    /// <summary>The new layout's language, e.g. "en-US".</summary>
    public event Action<CultureInfo>? LanguageChanged;

    public KeyboardWatcher(HwndSource source)
    {
        _source = source;
        _capsOn = (GetKeyState(VkCapital) & 1) != 0;
        _layout = CurrentLayout();
        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = source.Handle };
        if (RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            source.AddHook(Hook);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput)
        {
            return IntPtr.Zero;
        }

        uint size = (uint)Marshal.SizeOf<RawInputKeyboardPacket>();
        if (GetRawInputData(lParam, RidInput, out var packet, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) == uint.MaxValue ||
            packet.Header.Type != RimTypeKeyboard || (packet.Keyboard.Flags & RiKeyBreak) == 0)
        {
            return IntPtr.Zero;   // only key releases matter
        }

        if (packet.Keyboard.VKey == VkCapital)
        {
            var on = (GetKeyState(VkCapital) & 1) != 0;
            if (on != _capsOn)
            {
                _capsOn = on;
                CapsLockChanged?.Invoke(on);
            }
        }

        // A layout switch (Win+Space, Alt+Shift…) shows up in the foreground window right after.
        var layout = CurrentLayout();
        if (layout != _layout && layout != IntPtr.Zero)
        {
            _layout = layout;
            try
            {
                LanguageChanged?.Invoke(new CultureInfo((int)((long)layout & 0xFFFF)));
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr CurrentLayout()
    {
        var window = GetForegroundWindow();
        var thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, IntPtr.Zero);
        return GetKeyboardLayout(thread);
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevRemove, Target = IntPtr.Zero };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputKeyboardPacket
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, out RawInputKeyboardPacket data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);
}
