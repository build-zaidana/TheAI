using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBar.Core;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CodexBar.App;

// What was copied lately, as a page of the open notch: click one to copy it again, drag it into an
// app or the prompt, right-click to pin it. Windows tells us when the clipboard changes (a format
// listener on the notch's window). Password managers mark what they copy as private — that is
// skipped — and only pinned texts are written to disk; the rest is forgotten when TheAI quits.
public partial class NotchWindow
{
    private const double ClipWidth = 440;
    private const int WmClipboardUpdate = 0x031D;
    private const int ClipShown = 12;

    private readonly ClipboardHistory _clips = new();
    private readonly DispatcherTimer _clipRead = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _clipListening;
    private string? _clipSelf;   // what TheAI just put on the clipboard itself
    private Point _clipDragStart;

    private static string ClipPinsPath => Path.Combine(
        CodexBar.Providers.AppPaths.DataRoot, "clipboard-pins.json");

    private bool ClipboardAvailable => _clipListening && _clips.Items.Count > 0;

    private void InitClipboard()
    {
        _clipRead.Tick += (_, _) =>
        {
            _clipRead.Stop();
            ReadClipboard();
        };
        _clips.LoadPins(ClipPinsPath);
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            ApplyClipboardListener();
        }
        else
        {
            SourceInitialized += (_, _) => ApplyClipboardListener();
        }

        Closed += (_, _) => StopClipboardListener();
    }

    /// <summary>Settings may have turned the history on or off.</summary>
    private void ApplyClipboardListener()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (_host.ClipboardHistory && !_clipListening)
        {
            HwndSource.FromHwnd(handle)?.AddHook(ClipboardHook);
            _clipListening = AddClipboardFormatListener(handle);
        }
        else if (!_host.ClipboardHistory && _clipListening)
        {
            StopClipboardListener();
            _clips.Clear();
            if (_view == NotchView.Clipboard)
            {
                Collapse();
            }
        }
    }

    private void StopClipboardListener()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_clipListening && handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(handle);
            HwndSource.FromHwnd(handle)?.RemoveHook(ClipboardHook);
        }

        _clipListening = false;
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            // Apps often write several formats in a row: read once they're done.
            _clipRead.Stop();
            _clipRead.Start();
        }

        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
            {
                return;
            }

            var formats = data.GetFormats(autoConvert: false);
            if (IsPrivate(data, formats))
            {
                if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_CLIPBOARD") is { Length: > 0 })
                {
                    DiagnosticsLog.Info("Clipboard", "skipped a private copy");
                }

                return;
            }

            var now = DateTimeOffset.Now;
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
            {
                if (text == _clipSelf)
                {
                    _clipSelf = null;
                    _clips.AddText(text, now);   // re-copied from the notch: just moves up
                }
                else
                {
                    _clips.AddText(text, now);
                }
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } image)
            {
                _clips.AddImage(EncodePng(image), now);
            }
            else
            {
                return;   // files (the shelf has those) and other formats
            }

            // CODEXBAR_TEST_CLIPBOARD=1: say what was kept (kind and count only, never the content).
            if (Environment.GetEnvironmentVariable("CODEXBAR_TEST_CLIPBOARD") is { Length: > 0 })
            {
                DiagnosticsLog.Info("Clipboard", $"kept {(_clips.Items[0].IsImage ? "image" : "text")}, {_clips.Items.Count} in history");
            }

            if (_view == NotchView.Clipboard)
            {
                RenderClipboard();
            }
            else
            {
                RenderPageDots();
            }
        }
        catch (Exception exception) when (exception is COMException or ExternalException or InvalidOperationException)
        {
            // Another app still holds the clipboard: this copy is missed, the next one won't be.
        }
    }

    // Windows' convention for "keep this out of clipboard history" (password managers, private fields).
    private static bool IsPrivate(System.Windows.IDataObject data, string[] formats)
    {
        if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore"))
        {
            return true;
        }

        foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (formats.Contains(name) && data.GetData(name) is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = stream.ToArray();
                if (BitConverter.ToInt32(bytes, 0) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage DecodePng(byte[] png, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ---- The clipboard page ----------------------------------------------------------------

    private void OpenClipboard()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Clipboard;
        RenderClipboard();
        _view = NotchView.Clipboard;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderClipboard()
    {
        ClipTiles.Children.Clear();
        foreach (var item in _clips.Items.Take(ClipShown))
        {
            ClipTiles.Children.Add(BuildClipTile(item));
        }

        ClipEmpty.Visibility = _clips.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderPageDots();
    }

    private FrameworkElement BuildClipTile(ClipItem item)
    {
        FrameworkElement content = item.IsImage
            ? new Image { Source = DecodePng(item.Png!, 240), Stretch = Stretch.UniformToFill }
            : new TextBlock
            {
                Text = item.Text!.Trim(),
                FontSize = 11,
                LineHeight = 14,
                Foreground = Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 6, 8, 6)
            };
        var grid = new Grid();
        grid.Children.Add(content);
        if (item.Pinned)
        {
            grid.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = TimerFocusBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0)
            });
        }

        var tile = new Border
        {
            Height = 56,
            Margin = new Thickness(0, 0, 7, 7),
            CornerRadius = new CornerRadius(10),
            Background = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = grid,
            Tag = item.Id,
            ToolTip = item.IsImage ? Tr.Text("Gambar", "Image") : item.Text!.Length > 400 ? item.Text[..400] + "…" : item.Text
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, item.IsImage ? Tr.Text("Gambar", "Image") : item.Text!);
        tile.MouseEnter += (_, _) => tile.Background = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        tile.MouseLeftButtonDown += (_, e) => _clipDragStart = e.GetPosition(this);
        tile.MouseLeftButtonUp += (_, _) => CopyClip(item);
        tile.MouseMove += (_, e) => DragClip(tile, item, e);
        tile.MouseRightButtonUp += (_, e) =>
        {
            _clips.TogglePin(item.Id);
            _clips.SavePins(ClipPinsPath);
            RenderClipboard();
            e.Handled = true;
        };
        return tile;
    }

    private void CopyClip(ClipItem item)
    {
        try
        {
            if (item.IsImage)
            {
                Clipboard.SetImage(DecodePng(item.Png!, 0));
            }
            else
            {
                _clipSelf = item.Text;
                Clipboard.SetText(item.Text!);
            }

            ClipHint.Text = Tr.Text("Tersalin — tempel di mana saja", "Copied — paste anywhere");
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            ClipHint.Text = Tr.Text("Clipboard sedang dipakai app lain", "Another app is using the clipboard");
        }
    }

    private void DragClip(FrameworkElement tile, ClipItem item, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _clipDragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = item.IsImage
            ? new DataObject(DataFormats.Bitmap, DecodePng(item.Png!, 0))
            : new DataObject(DataFormats.UnicodeText, item.Text!);
        DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        _clips.Clear();
        if (_clips.Items.Count == 0)
        {
            Collapse();
        }
        else
        {
            RenderClipboard();
        }
    }

    // ---- Dev preview ---------------------------------------------------------------------

    internal async void PreviewClipboard()
    {
        await Task.Delay(400);
        _clipListening = true;   // shown without listening: nothing real is read
        var now = DateTimeOffset.Now;
        _clips.AddText("{\"id\":\"build\",\"progress\":0.4}", now);
        _clips.AddText("Rapat jam 3 dipindah ke Kamis", now);
        _clips.AddText("var job = _jobs.Apply(update, now);", now);
        _clips.AddText("https://github.com/build-zaidana/TheAI/pull/128", now);
        _clips.AddText("git push origin feature/live-activity", now);
        _clips.TogglePin(_clips.Items[0].Id);
        OpenClipboard();
        _pinned = true;
        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, "notch-clipboard.png"));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
