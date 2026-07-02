using System.Runtime.InteropServices;
using System.Windows;

namespace CodexBar.Windows;

/// <summary>
/// Copies the current foreground application's selection without activating CodexBar.
/// A changed clipboard sequence is required, so an empty selection never reuses stale text.
/// </summary>
public static class SelectionCapture
{
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const uint KeyEventKeyUp = 0x0002;

    public static async Task<string?> TryCaptureTextAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // WM_HOTKEY is raised while the registered modifiers may still be physically held.
        // Wait for release so the synthetic Ctrl+C is not interpreted as Win+Shift+Ctrl+C.
        var releaseDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(750);
        while (AnyModifierDown() && DateTime.UtcNow < releaseDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(15, cancellationToken);
        }

        if (AnyModifierDown())
        {
            return null;
        }

        IDataObject? previousClipboard = null;
        try
        {
            previousClipboard = Clipboard.GetDataObject();
        }
        catch
        {
            // Clipboard backup is best-effort. Capture can still succeed.
        }

        var sequenceBefore = GetClipboardSequenceNumber();
        if (!SendCopyShortcut())
        {
            return null;
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMilliseconds(450));
        string? captured = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken);

            var sequenceAfter = GetClipboardSequenceNumber();
            if (sequenceAfter == sequenceBefore)
            {
                continue;
            }

            try
            {
                if (Clipboard.ContainsText())
                {
                    captured = Clipboard.GetText();
                }
            }
            catch
            {
                // Clipboard may still be owned briefly by the source application.
                continue;
            }

            break;
        }

        if (previousClipboard is not null)
        {
            try
            {
                Clipboard.SetDataObject(previousClipboard, copy: true);
            }
            catch
            {
                // Do not turn a successful capture into a failure if restore is rejected.
            }
        }

        return NormalizeCapturedText(sequenceBefore, GetClipboardSequenceNumber(), captured);
    }

    public static string? NormalizeCapturedText(uint sequenceBefore, uint sequenceAfter, string? text) =>
        sequenceAfter != sequenceBefore && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool SendCopyShortcut()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0),
            KeyboardInput(VkC, 0),
            KeyboardInput(VkC, KeyEventKeyUp),
            KeyboardInput(VkControl, KeyEventKeyUp)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static bool AnyModifierDown() =>
        IsKeyDown(VkControl) || IsKeyDown(VkShift) || IsKeyDown(VkMenu) ||
        IsKeyDown(VkLeftWindows) || IsKeyDown(VkRightWindows);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static Input KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = 1,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Flags = flags
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
