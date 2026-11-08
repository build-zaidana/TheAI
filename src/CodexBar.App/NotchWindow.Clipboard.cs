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
    private const double ClipWidth = IslandTokens.WidthM;
    private const int WmClipboardUpdate = 0x031D;
    private const int ClipShown = 13;   // the newest as the title, twelve rows under it

    private readonly ClipboardHistory _clips = new();
    private readonly DispatcherTimer _clipRead = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _clipListening;
    private string? _clipSelf;   // what TheAI just put on the clipboard itself
    private Point _clipDragStart;
    private bool _clipDragging;
    private readonly Dictionary<string, string> _clipSources = [];   // item id -> the app it came from

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

            if (ForegroundAppName() is { } source)
            {
                _clipSources[_clips.Items[0].Id] = source;
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
        var items = _clips.Items.Take(ClipShown).ToList();
        ClipCopyButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipThumb.Visibility = Visibility.Collapsed;
        if (items.Count == 0)
        {
            ClipTitle.Text = Tr.Text("Belum ada salinan", "Nothing copied yet");
            ClipSub.Text = Tr.Text("Salin apa saja, nanti muncul di sini", "Copy anything, it shows up here");
            ClipHint.Text = string.Empty;
            RenderPageDots();
            return;
        }

        var newest = items[0];
        var (title, sub) = ClipHeadline(newest);
        ClipTitle.Text = title;
        ClipSub.Text = sub;
        ClipHint.Text = ClipOrigin(newest);
        if (newest.IsImage)
        {
            ClipThumb.Background = new ImageBrush(DecodePng(newest.Png!, 128)) { Stretch = Stretch.UniformToFill };
            ClipThumb.Visibility = Visibility.Visible;
        }

        foreach (var item in items.Skip(1))
        {
            ClipTiles.Children.Add(BuildClipRow(item));
        }

        RenderPageDots();
    }

    private void ClipCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Items.Count > 0)
        {
            CopyClip(_clips.Items[0]);
        }
    }

    private string ClipOrigin(ClipItem item)
    {
        var when = ClipAge(item.At);
        return _clipSources.TryGetValue(item.Id, out var source) ? $"{source} · {when}" : when;
    }

    private static string ClipAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.Now - at;
        return age.TotalMinutes < 1 ? Tr.Text("baru saja", "just now")
            : age.TotalHours < 1 ? string.Format(Tr.Text("{0} mnt", "{0} min"), (int)age.TotalMinutes)
            : age.TotalDays < 1 ? string.Format(Tr.Text("{0} jam", "{0} h"), (int)age.TotalHours)
            : Tr.Text("kemarin", "yesterday");
    }

    private (string Title, string Sub) ClipHeadline(ClipItem item)
    {
        if (item.IsImage)
        {
            return (Tr.Text("Gambar", "Image"), Tr.Text("Salin untuk menempelkannya", "Copy to paste it"));
        }

        var text = item.Text!.Trim();
        var line = text.Split('\n')[0].Trim();
        if (Uri.TryCreate(line, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var path = url.AbsolutePath.Trim('/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return (parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts.Length == 1 ? parts[0] : url.Host, url.Host + (parts.Length > 2 ? " · " + string.Join('/', parts.Skip(2)) : string.Empty));
        }

        return (line, ClipKindName(text));
    }

    private static string ClipKindName(string text) =>
        IsClipColour(text) ? Tr.Text("Warna", "Colour")
        : IsClipCommand(text) ? Tr.Text("Perintah", "Command")
        : string.Format(Tr.Text("Teks · {0} karakter", "Text · {0} characters"), text.Length);

    private static bool IsClipColour(string text) =>
        text.Length is 4 or 7 or 9 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

    private static bool IsClipCommand(string text) =>
        !text.Contains('\n') && new[] { "git ", "dotnet ", "npm ", "pnpm ", "cd ", "curl ", "python ", "winget ", "ssh " }.Any(start => text.StartsWith(start, StringComparison.Ordinal));

    private FrameworkElement BuildClipRow(ClipItem item)
    {
        var text = item.IsImage ? Tr.Text("Gambar", "Image") : item.Text!.Trim().Split('\n')[0];
        var grid = new Grid { Height = 39, Background = System.Windows.Media.Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement mark;
        if (item.IsImage)
        {
            mark = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(5), Background = new ImageBrush(DecodePng(item.Png!, 48)) { Stretch = Stretch.UniformToFill } };
        }
        else if (IsClipColour(text) && System.Windows.Media.ColorConverter.ConvertFromString(text) is Color swatch)
        {
            mark = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(5), Background = IslandTokens.Frozen(swatch) };
        }
        else
        {
            mark = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(5, 0, 0, 0), Fill = item.Pinned ? TimerFocusBrush : IslandTokens.Text3 };
        }

        mark.HorizontalAlignment = HorizontalAlignment.Left;
        mark.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(mark);

        var body = new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = IslandTokens.Text1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (IsClipCommand(text))
        {
            body.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
            body.FontSize = 13;
        }

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var when = new TextBlock { Text = ClipAge(item.At), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = IslandTokens.Text3, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var row = new Border
        {
            BorderBrush = IslandTokens.Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = item.Id,
            ToolTip = item.IsImage ? text : item.Text!.Length > 400 ? item.Text[..400] + "…" : item.Text
        };
        System.Windows.Automation.AutomationProperties.SetName(row, text);
        row.MouseEnter += (_, _) => body.Foreground = IslandTokens.Text2;
        row.MouseLeave += (_, _) => body.Foreground = IslandTokens.Text1;
        row.MouseLeftButtonDown += (_, e) => _clipDragStart = e.GetPosition(this);
        row.MouseLeftButtonUp += (_, _) => CopyClip(item);
        row.MouseMove += (_, e) => DragClip(row, item, e);
        row.MouseRightButtonUp += (_, e) =>
        {
            TogglePin(item);
            e.Handled = true;
        };
        // From the keyboard too: Tab to a row, Enter / Space copies it, P (or the menu key) pins it.
        row.Focusable = true;
        row.FocusVisualStyle = null;
        row.GotKeyboardFocus += (_, _) => body.Foreground = TimerFocusBrush;
        row.LostKeyboardFocus += (_, _) => body.Foreground = IslandTokens.Text1;
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                CopyClip(item);
                e.Handled = true;
            }
            else if (e.Key is Key.P or Key.Apps)
            {
                TogglePin(item);
                e.Handled = true;
            }
        };
        System.Windows.Automation.AutomationProperties.SetHelpText(row,
            Tr.Text("Enter untuk menyalin, P untuk menyematkan", "Enter to copy, P to pin"));
        return row;
    }

    /// <summary>The app that was in front when something was copied, by its own name.</summary>
    private static string? ForegroundAppName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out int id);
            using var process = System.Diagnostics.Process.GetProcessById(id);
            var name = process.ProcessName;
            return name.Length == 0 ? null : char.ToUpperInvariant(name[0]) + name[1..];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void TogglePin(ClipItem item)
    {
        _clips.TogglePin(item.Id);
        _clips.SavePins(ClipPinsPath);
        RenderClipboard();
        // Keep the keyboard where it was after the redraw.
        ClipTiles.Children.OfType<Border>().FirstOrDefault(tile => (string?)tile.Tag == item.Id)?.Focus();
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

            ClipHint.Text = Tr.Text("Tersalin", "Copied");
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
        _clipDragging = true;
        try
        {
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
        }
        finally
        {
            _clipDragging = false;
        }
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
        _clips.AddText("https://github.com/build-zaidana/theai-app/releases", now);
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
