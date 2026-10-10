using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using Image = System.Windows.Controls.Image;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace CodexBar.App;

// The file shelf, like NotchNook's tray: files dropped on the notch's tray target wait here (by
// reference, nothing is copied) until they are dragged out into another app, saved to a folder
// together, or cleared. The compact notch shows a tray and how many files wait.
public partial class NotchWindow
{
    private const double ShelfWidth = 440;
    private const int ShelfLimit = 24;

    private static readonly string ShelfFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "shelf.json");

    private readonly List<string> _shelf = [];
    private Point _shelfDragStart;
    private string? _shelfDragPath;

    private bool ShelfShowing => _shelf.Count > 0;

    private void InitShelf()
    {
        try
        {
            if (File.Exists(ShelfFile) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfFile)) is { } saved)
            {
                // Files moved or deleted since leave the shelf quietly.
                _shelf.AddRange(saved.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(ShelfLimit));
            }
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Shelf", exception.Message);
        }

        RenderShelf();
    }

    private void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile)!);
            File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf));
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Shelf", exception.Message);
        }
    }

    /// <summary>Put files on the shelf (duplicates skipped); returns how many were added.</summary>
    private int AddToShelf(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (_shelf.Count >= ShelfLimit)
            {
                break;
            }

            if (!_shelf.Contains(path, StringComparer.OrdinalIgnoreCase) && (File.Exists(path) || Directory.Exists(path)))
            {
                _shelf.Add(path);
                added++;
            }
        }

        if (added > 0)
        {
            ShelfChanged();
        }

        return added;
    }

    private void ShelfChanged()
    {
        SaveShelf();
        RenderShelf();
        ApplyMusicCompact();
        if (_view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }
        else if (_view == NotchView.Shelf && _shelf.Count == 0)
        {
            Collapse();
        }

        UpdateVisibility();
    }

    // ---- Compact: a tray and a count, when nothing livelier holds the notch -------------------

    private void ApplyShelfCompact()
    {
        var show = ShelfShowing && !TimerShowing && !MusicShowing && !CalendarShowing;
        CompactShelf.HorizontalAlignment = CompactGlyph.HorizontalAlignment;
        CompactShelf.VerticalAlignment = CompactGlyph.VerticalAlignment;
        CompactShelf.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactShelfCount.Visibility = show && _state == NotchState.Idle && !Vertical ? Visibility.Visible : Visibility.Collapsed;
        CompactShelfCount.Text = _shelf.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (show)
        {
            CompactGlyph.Visibility = Visibility.Collapsed;
            QuotaRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---- The shelf page ----------------------------------------------------------------------

    private void OpenShelf()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        _musicTick.Stop();
        _lastPage = NotchView.Shelf;
        RenderShelf();
        _view = NotchView.Shelf;
        ApplyView(animate: true);
        ApplyMusicCompact();
        UpdateVisibility();
    }

    private void RenderShelf()
    {
        ShelfItems.Children.Clear();
        foreach (var path in _shelf)
        {
            ShelfItems.Children.Add(BuildShelfItem(path));
        }

        RenderPageDots();
    }

    private FrameworkElement BuildShelfItem(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        var thumb = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("NotchFill"),
        };
        if (IsImage(path) && LoadShelfThumbnail(path) is { } image)
        {
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            thumb.Child = new Path
            {
                Stroke = (Brush)FindResource("NotchText"),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse(Directory.Exists(path)
                    ? "M 1,4 Q 1,2.5 2.5,2.5 L 7,2.5 L 9,4.5 L 17.5,4.5 Q 19,4.5 19,6 L 19,15.5 Q 19,17 17.5,17 L 2.5,17 Q 1,17 1,15.5 Z"
                    : "M 2,1 L 11,1 L 16,6 L 16,21 L 2,21 Z M 11,1 L 11,6 L 16,6")
            };
        }

        // A small remove mark in the corner while hovered.
        var remove = new Button
        {
            Style = (Style)FindResource("NotchTintButton"),
            Width = 20,
            Height = 20,
            Background = Frozen(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2E)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Visibility = Visibility.Hidden,
            Tag = path,
            Content = new Path { Stroke = (Brush)FindResource("NotchText"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7") }
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, Tr.Text("Keluarkan dari rak", "Take off the shelf"));
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _shelf.Remove(path);
            ShelfChanged();
        };

        var tile = new Grid { Width = 52, Height = 52 };
        tile.Children.Add(thumb);
        tile.Children.Add(remove);

        var item = new StackPanel { Width = 66, Margin = new Thickness(0, 0, 8, 6), Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Tag = path, ToolTip = path };
        item.Children.Add(tile);
        item.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("NotchText3"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });
        System.Windows.Automation.AutomationProperties.SetName(item, name);
        item.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        item.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        item.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _shelfDragStart = e.GetPosition(this);
            _shelfDragPath = path;
            if (e.ClickCount == 2)
            {
                OpenShelfItem(path);
            }
        };
        item.PreviewMouseMove += ShelfItem_PreviewMouseMove;
        return item;
    }

    // Dragging a file off the shelf hands it to whatever it is dropped on (Explorer, a mail, a chat…).
    private void ShelfItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfDragPath is null)
        {
            return;
        }

        var moved = e.GetPosition(this) - _shelfDragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var path = _shelfDragPath;
        _shelfDragPath = null;
        _pinned = true;   // stay open while the file is on its way
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _pinned = false;
        }
    }

    private static void OpenShelfItem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Shelf", exception.Message);
        }
    }

    private void ShelfSaveAll_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Tr.Text("Salin semua file di rak ke folder", "Copy every file on the shelf to a folder"),
            UseDescriptionForTitle = true
        };
        _pinned = true;
        try
        {
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            foreach (var path in _shelf.Where(File.Exists))
            {
                var target = System.IO.Path.Combine(dialog.SelectedPath, System.IO.Path.GetFileName(path));
                if (!File.Exists(target))
                {
                    File.Copy(path, target);
                }
            }

            Process.Start(new ProcessStartInfo(dialog.SelectedPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            CodexBar.Core.DiagnosticsLog.Error("Shelf", exception.Message);
        }
        finally
        {
            _pinned = false;
        }
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Clear();
        ShelfChanged();
    }

    private static bool IsImage(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    private static BitmapImage? LoadShelfThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 104;
            image.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    internal async void PreviewShelf(string mode)
    {
        await Task.Delay(400);
        var samples = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Fonts", "OFL.txt"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "CodexBar.App.dll"),
            AppContext.BaseDirectory.TrimEnd('\\')
        };
        _shelf.Clear();
        _shelf.AddRange(samples.Where(path => File.Exists(path) || Directory.Exists(path)));
        RenderShelf();
        ApplyMusicCompact();
        if (mode == "drop")
        {
            OpenLauncher(focus: false);
            ShowDropTargets(shelfHovered: true);
        }
        else if (mode == "open")
        {
            OpenShelf();
            _pinned = true;
        }
        else
        {
            ApplyView(animate: true);
        }

        if (Environment.GetEnvironmentVariable("CODEXBAR_SNAPSHOT_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(1500);
            SnapshotShell(System.IO.Path.Combine(dir, $"notch-shelf-{mode}.png"));
        }
    }
}
