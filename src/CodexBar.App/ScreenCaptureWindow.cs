using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Image = System.Windows.Controls.Image;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CodexBar.App;

/// <summary>
/// Region screenshot for attaching to a prompt. The monitor under the cursor is frozen into an
/// image; the user drags a rectangle (or clicks for the whole screen, Esc cancels) and the crop
/// is saved as a PNG in %TEMP%\CodexBar\screenshots. Everything stays in the same pixel space:
/// the monitor rectangle, the capture and the window placement all come from the same API.
/// </summary>
internal sealed class ScreenCaptureWindow : Window
{
    private readonly System.Drawing.Rectangle _bounds;
    private readonly BitmapSource _frozen;
    private readonly Canvas _canvas = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _sizeTag = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 3, 7, 3),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11 };
    private Point? _start;
    private Rect _selection = Rect.Empty;

    public string? SavedPath { get; private set; }

    private ScreenCaptureWindow(System.Drawing.Rectangle bounds, BitmapSource frozen)
    {
        _bounds = bounds;
        _frozen = frozen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        Background = Brushes.Black;
        Title = "CodexBar screenshot";

        var root = new Grid();
        root.Children.Add(new Image { Source = frozen, Stretch = Stretch.Fill });
        root.Children.Add(_dim);
        _sizeTag.Child = _sizeText;
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_sizeTag);
        root.Children.Add(_canvas);
        root.Children.Add(new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7, 14, 7),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Text = Tr.Text("Seret untuk memilih area · Klik untuk seluruh layar · Esc batal",
                               "Drag to select an area · Click for the whole screen · Esc cancels")
            }
        });
        Content = root;

        SourceInitialized += (_, _) =>
        {
            // Cover the monitor exactly, in the same pixel space the capture used.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, -1, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040);
        };
        Loaded += (_, _) =>
        {
            UpdateDim();
            Activate();
            Focus();
        };
        SizeChanged += (_, _) => UpdateDim();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
    }

    /// <summary>Freeze the screen under the cursor and let the user pick a region. Null = cancelled.</summary>
    public static string? Capture()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var bounds = screen.Bounds;
        BitmapSource frozen;
        using (var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
        {
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                frozen = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                frozen.Freeze();
            }
            finally
            {
                DeleteObject(handle);
            }
        }

        var window = new ScreenCaptureWindow(bounds, frozen);
        window.ShowDialog();
        return window.SavedPath;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _selection = new Rect(_start.Value, _start.Value);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        _selection = new Rect(start, e.GetPosition(this));
        UpdateDim();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null)
        {
            return;
        }

        ReleaseMouseCapture();
        _start = null;
        // A click (no real drag) takes the whole screen.
        Save(_selection.Width < 4 || _selection.Height < 4 ? new Rect(0, 0, ActualWidth, ActualHeight) : _selection);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            Save(new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (_selection.IsEmpty || _selection.Width < 1 || _selection.Height < 1)
        {
            _dim.Data = full;
            _frame.Visibility = Visibility.Collapsed;
            _sizeTag.Visibility = Visibility.Collapsed;
            return;
        }

        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
        _frame.Visibility = Visibility.Visible;
        _frame.Width = _selection.Width;
        _frame.Height = _selection.Height;
        Canvas.SetLeft(_frame, _selection.X);
        Canvas.SetTop(_frame, _selection.Y);

        var (pw, ph) = (Math.Round(_selection.Width * ScaleX), Math.Round(_selection.Height * ScaleY));
        _sizeText.Text = $"{pw} × {ph}";
        _sizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeTag, _selection.X);
        Canvas.SetTop(_sizeTag, _selection.Bottom + 6 + 24 < ActualHeight ? _selection.Bottom + 6 : _selection.Y - 26);
    }

    private double ScaleX => ActualWidth > 0 ? _frozen.PixelWidth / ActualWidth : 1;

    private double ScaleY => ActualHeight > 0 ? _frozen.PixelHeight / ActualHeight : 1;

    private void Save(Rect area)
    {
        var x = (int)Math.Clamp(Math.Round(area.X * ScaleX), 0, _frozen.PixelWidth - 1);
        var y = (int)Math.Clamp(Math.Round(area.Y * ScaleY), 0, _frozen.PixelHeight - 1);
        var w = (int)Math.Clamp(Math.Round(area.Width * ScaleX), 1, _frozen.PixelWidth - x);
        var h = (int)Math.Clamp(Math.Round(area.Height * ScaleY), 1, _frozen.PixelHeight - y);

        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexBar", "screenshots");
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(_frozen, new Int32Rect(x, y, w, h))));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        SavedPath = path;
        Close();
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
