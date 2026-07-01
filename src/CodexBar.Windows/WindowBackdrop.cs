using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexBar.Windows;

/// <summary>
/// Menerapkan backdrop kaca asli Windows 11 (DWM system backdrop / acrylic) dan sudut
/// membulat native pada window WPF borderless tanpa <c>AllowsTransparency</c>.
/// Membuat permukaan WPF transparan + memperluas frame ("sheet of glass") agar backdrop
/// DWM tampak menembus. Efek hanya terlihat bila "Transparency effects" Windows aktif.
/// </summary>
public static class WindowBackdrop
{
    public enum BackdropKind
    {
        None = 1,        // DWMSBT_NONE
        Mica = 2,        // DWMSBT_MAINWINDOW
        Acrylic = 3,     // DWMSBT_TRANSIENTWINDOW
        Tabbed = 4,      // DWMSBT_TABBEDWINDOW

        // Blur transparan tanpa tint material abu-abu bawaan Desktop Acrylic.
        // Nilai ini internal dan tidak diteruskan ke DWMWA_SYSTEMBACKDROP_TYPE.
        ClearBlur = 100
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int WCA_ACCENT_POLICY = 19;

    /// <summary>
    /// Mematikan material blur/transparan dan mempertahankan sudut native Windows.
    /// Warna permukaan sepenuhnya digambar oleh WPF.
    /// </summary>
    public static bool ApplySolid(Window window, bool dark, Color backgroundColor)
    {
        // Layered WPF window menangani transparency di luar card secara per-pixel.
        // Jangan menambahkan permukaan HWND/DWM lain di belakang card tersebut.
        if (window.AllowsTransparency)
        {
            return true;
        }

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (HwndSource.FromHwnd(hwnd) is { } source && source.CompositionTarget is { } target)
        {
            // Samakan permukaan native dengan root WPF. Ini menutup area non-client
            // yang dapat terlihat saat window kehilangan fokus tanpa membuat layer
            // kedua, karena kartu input sekarang sudah menyatu dengan root.
            target.BackgroundColor = backgroundColor;
        }

        DisableAccent(hwnd);
        var noBackdrop = (int)BackdropKind.None;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref noBackdrop, sizeof(int));
        var noFrame = new Margins();
        _ = DwmExtendFrameIntoClientArea(hwnd, ref noFrame);

        var darkValue = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
        var corner = DWMWCP_ROUND;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int)) == 0;
    }

    public static bool Apply(Window window, BackdropKind kind, bool dark = true, bool roundedCorners = true)
    {
        // Layered WPF windows provide real per-pixel transparency. Applying a
        // DWM backdrop behind them would add the opaque gray material again.
        if (window.AllowsTransparency)
        {
            return true;
        }

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == nint.Zero)
        {
            return false;
        }

        // Permukaan WPF transparan agar backdrop DWM menembus + hilangkan latar hitam.
        if (HwndSource.FromHwnd(hwnd) is { } source && source.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        var darkValue = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

        if (roundedCorners)
        {
            var corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        if (kind == BackdropKind.ClearBlur)
        {
            // Desktop Acrylic selalu membawa tint sistem. Matikan material tersebut,
            // batalkan frame kaca penuh, lalu gunakan blur-behind dengan tint nol.
            var noBackdrop = (int)BackdropKind.None;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref noBackdrop, sizeof(int));

            var noFrame = new Margins();
            _ = DwmExtendFrameIntoClientArea(hwnd, ref noFrame);

            if (ApplyAccent(hwnd, AccentState.BlurBehind, gradientColor: 0x00000000))
            {
                return true;
            }

            // Fallback resmi untuk perangkat yang menolak accent blur.
            kind = BackdropKind.Acrylic;
        }

        DisableAccent(hwnd);

        // System backdrop perlu frame yang diperluas ke seluruh client area.
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var backdrop = (int)kind;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
    }

    private static bool ApplyAccent(nint hwnd, AccentState state, int gradientColor)
    {
        var policy = new AccentPolicy
        {
            State = state,
            GradientColor = gradientColor
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = pointer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void DisableAccent(nint hwnd) =>
        _ = ApplyAccent(hwnd, AccentState.Disabled, gradientColor: 0);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        nint hwnd,
        ref WindowCompositionAttributeData data);

    private enum AccentState
    {
        Disabled = 0,
        BlurBehind = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState State;
        public int Flags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

}
