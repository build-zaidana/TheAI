using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CodexBar.Windows;

public sealed class GlobalHotKey : IDisposable
{
    private const int HotKeyId = 0x4342;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private nint _handle;
    private bool _registered;

    public event EventHandler? Pressed;
    public string? CurrentGesture { get; private set; }

    public void Register(Window window, string gesture)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            throw new InvalidOperationException("Handle window belum tersedia untuk mendaftarkan hotkey.");
        }

        if (_handle != 0 && _handle != handle)
        {
            throw new InvalidOperationException("GlobalHotKey sudah terhubung ke window lain.");
        }

        _handle = handle;
        if (_source is null)
        {
            _source = HwndSource.FromHwnd(_handle)
                ?? throw new InvalidOperationException("Sumber window tidak tersedia untuk mendaftarkan hotkey.");
            _source.AddHook(WindowProc);
        }

        ChangeGesture(gesture);
    }

    public void ChangeGesture(string gesture)
    {
        if (_handle == 0)
        {
            throw new InvalidOperationException("Window belum siap untuk mendaftarkan hotkey.");
        }

        var requested = Parse(gesture);
        if (_registered && string.Equals(CurrentGesture, requested.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _registered && CurrentGesture is { } current
            ? Parse(current)
            : null;

        if (_registered && !UnregisterHotKey(_handle, HotKeyId))
        {
            throw new InvalidOperationException("Hotkey lama tidak dapat dilepas. Coba tutup lalu buka kembali CodexBar.");
        }

        _registered = false;
        if (RegisterHotKey(_handle, HotKeyId, requested.Modifiers | ModNoRepeat, requested.VirtualKey))
        {
            _registered = true;
            CurrentGesture = requested.DisplayName;
            return;
        }

        var errorCode = Marshal.GetLastWin32Error();
        if (previous is not null &&
            RegisterHotKey(_handle, HotKeyId, previous.Modifiers | ModNoRepeat, previous.VirtualKey))
        {
            _registered = true;
            CurrentGesture = previous.DisplayName;
        }
        else
        {
            CurrentGesture = null;
        }

        throw new InvalidOperationException(
            $"Hotkey {requested.DisplayName} tidak dapat dipakai (Windows error {errorCode}). Kombinasi mungkin sedang digunakan aplikasi lain.");
    }

    public static string NormalizeGesture(string gesture) => Parse(gesture).DisplayName;

    private static ParsedHotKey Parse(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            throw new FormatException("Hotkey tidak boleh kosong.");
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (var token in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    continue;
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
            }

            if (key is not null)
            {
                throw new FormatException("Hotkey hanya boleh memiliki satu tombol utama.");
            }

            key = ParseKey(token);
        }

        if (modifiers == 0)
        {
            throw new FormatException("Hotkey harus memakai minimal satu modifier: Win, Ctrl, Alt, atau Shift.");
        }

        if (key is null)
        {
            throw new FormatException("Hotkey belum memiliki tombol utama.");
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        if (virtualKey == 0)
        {
            throw new FormatException("Tombol utama tidak didukung sebagai hotkey global.");
        }

        var parts = new List<string>();
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        parts.Add(DisplayKey(key.Value));
        return new ParsedHotKey(modifiers, virtualKey, string.Join('+', parts));
    }

    private static Key ParseKey(string token)
    {
        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            return Enum.Parse<Key>(char.ToUpperInvariant(token[0]).ToString());
        }

        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            return Enum.Parse<Key>($"D{token[0]}");
        }

        var normalized = token.Equals("Enter", StringComparison.OrdinalIgnoreCase)
            ? "Return"
            : token;
        if (!Enum.TryParse<Key>(normalized, ignoreCase: true, out var key) ||
            key is Key.None or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
                Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            throw new FormatException($"Tombol '{token}' tidak didukung.");
        }

        return key;
    }

    private static string DisplayKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        Key.Return => "Enter",
        _ => key.ToString()
    };

    private sealed record ParsedHotKey(uint Modifiers, uint VirtualKey, string DisplayName);

    public void Dispose()
    {
        if (_handle != 0 && _registered)
        {
            UnregisterHotKey(_handle, HotKeyId);
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
        _handle = 0;
        _registered = false;
        CurrentGesture = null;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
