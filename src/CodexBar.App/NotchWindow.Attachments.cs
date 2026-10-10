using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexBar.Providers;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// Files and screenshots on the notch. Dragging files over the notch drops it open into the
// launcher with a drop zone; releasing attaches them to the next prompt. The attachments live
// in the main window (one list for both surfaces); the notch only renders them as chips.
public partial class NotchWindow
{
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _dropTimerWired;
    private bool _openedByDrop;

    /// <summary>Re-render the chips after the host's attachment list changed.</summary>
    public void RefreshAttachments()
    {
        AttachmentTray.Children.Clear();
        foreach (var attachment in _host.Attachments)
        {
            AttachmentTray.Children.Add(BuildChip(attachment));
        }

        AttachmentTray.Visibility = AttachmentTray.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLauncherSendState();
    }

    /// <summary>Open the launcher showing the attachments (after a screenshot).</summary>
    public void ShowAttachments()
    {
        if (_view == NotchView.Request)
        {
            return;
        }

        if (_view != NotchView.Launcher)
        {
            OpenLauncher();
        }
        else
        {
            Activate();
            LauncherInput.Focus();
        }
    }

    /// <summary>Dev preview only: show the drop zone as if files were hovering.</summary>
    internal void PreviewDropZone() => ShowDropTargets(shelfHovered: false);

    private void UpdateLauncherSendState()
    {
        var hasAttachments = _host.Attachments.Count > 0;
        LauncherSendButton.IsEnabled = LauncherInput.Text.Trim().Length > 0 || hasAttachments || SuggestionFor(LauncherInput) is not null;
        LauncherPlaceholder.Text = hasAttachments
            ? L("notch.attachPlaceholder")
            : string.Format(L("notch.launcher.placeholder"), _host.ProviderName);
    }

    private FrameworkElement BuildChip(ProviderInputAttachment attachment)
    {
        FrameworkElement preview;
        if (attachment.Kind == ProviderAttachmentKind.LocalImage && TryLoadThumbnail(attachment.Path) is { } thumbnail)
        {
            preview = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(6),
                Background = new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill }
            };
        }
        else
        {
            preview = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("NotchFillHover"),
                Child = new Path
                {
                    Stroke = (Brush)FindResource("NotchText2"),
                    StrokeThickness = 1.3,
                    StrokeLineJoin = PenLineJoin.Round,
                    Width = 12,
                    Height = 14,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Data = Geometry.Parse("M 1,1 L 7.5,1 L 11,4.5 L 11,13 L 1,13 Z M 7.5,1 L 7.5,4.5 L 11,4.5")
                }
            };
        }

        var remove = new Button
        {
            Style = (Style)FindResource("NotchIconButton"),
            Width = 22,
            Height = 22,
            Margin = new Thickness(2, 0, 0, 0),
            Tag = attachment,
            ToolTip = Tr.Text("Hapus", "Remove"),
            Content = new Path
            {
                Stroke = (Brush)FindResource("NotchText2"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7")
            }
        };
        remove.Click += RemoveAttachment_Click;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(preview);
        row.Children.Add(new TextBlock
        {
            Text = attachment.DisplayName,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(remove);

        return new Border
        {
            Background = (Brush)FindResource("NotchFill"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 4, 4, 4),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = attachment.Path,
            Child = row
        };
    }

    private static BitmapImage? TryLoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 64;
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the temp file locked
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderInputAttachment attachment })
        {
            _host.RemoveAttachment(attachment);
            LauncherInput.Focus();
        }
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e) => _host.CaptureScreenshot();

    // ---- Drag and drop ----------------------------------------------------------------------

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void Shell_DragEnter(object sender, DragEventArgs e)
    {
        if (!_dropTimerWired)
        {
            _dropTimerWired = true;
            _dropLeaveTimer.Tick += (_, _) => EndDropHover(attached: false);
        }

        _dropLeaveTimer.Stop();
        e.Effects = DroppedFiles(e) is null || _view == NotchView.Request ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None || DropZone.Visibility == Visibility.Visible)
        {
            return;
        }

        if (_view != NotchView.Launcher)
        {
            _openedByDrop = true;
            OpenLauncher(focus: false);
        }

        ShowDropTargets(shelfHovered: false);
    }

    // Two targets side by side: attach to the prompt (off while a turn runs), or keep on the shelf.
    private void ShowDropTargets(bool shelfHovered)
    {
        DropZone.Visibility = Visibility.Visible;
        var attachOpen = !_host.IsBusy;
        DropAttachZone.Opacity = attachOpen ? 1 : 0.35;
        DropAttachZone.Background = !shelfHovered && attachOpen ? Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) : Frozen(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
        DropShelfZone.Background = shelfHovered ? Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) : Frozen(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
        DropAttachEdge.Stroke = Frozen(Color.FromArgb(!shelfHovered && attachOpen ? (byte)0xB3 : (byte)0x59, 0xFF, 0xFF, 0xFF));
        DropShelfEdge.Stroke = Frozen(Color.FromArgb(shelfHovered ? (byte)0xB3 : (byte)0x59, 0xFF, 0xFF, 0xFF));
        System.Windows.Automation.AutomationProperties.SetName(DropAttachZone, _host.IsBusy ? L("notch.dropBusy") : L("notch.drop"));
        System.Windows.Automation.AutomationProperties.SetName(DropShelfZone, Tr.Text("Simpan di rak", "Keep on the shelf"));
    }

    private bool OverShelfTarget(DragEventArgs e)
    {
        if (DropZone.Visibility != Visibility.Visible || DropShelfZone.ActualWidth <= 0)
        {
            return false;
        }

        var point = e.GetPosition(DropShelfZone);
        // Anywhere right of the gap counts as the shelf, so the targets split the notch in two.
        return point.X >= -5;
    }

    private void Shell_DragOver(object sender, DragEventArgs e)
    {
        _dropLeaveTimer.Stop();
        var shelf = OverShelfTarget(e);
        e.Effects = DroppedFiles(e) is null || _view == NotchView.Request || (!shelf && _host.IsBusy)
            ? DragDropEffects.None
            : DragDropEffects.Copy;
        e.Handled = true;
        if (DropZone.Visibility == Visibility.Visible)
        {
            ShowDropTargets(shelf);
        }
    }

    // DragLeave also fires when the cursor crosses between child elements or the shell grows
    // under it, so leaving is only final if no DragEnter follows shortly.
    private void Shell_DragLeave(object sender, DragEventArgs e) => _dropLeaveTimer.Start();

    private void Shell_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var added = 0;
        if (DroppedFiles(e) is { } paths && OverShelfTarget(e))
        {
            AddToShelf(paths);
            EndDropHover(attached: false);
            if (ShelfShowing)
            {
                OpenShelf();
            }

            return;
        }

        if (!_host.IsBusy && DroppedFiles(e) is { } files)
        {
            added = _host.AddAttachments(files);
        }

        EndDropHover(attached: added > 0);
    }

    private void EndDropHover(bool attached)
    {
        _dropLeaveTimer.Stop();
        DropZone.Visibility = Visibility.Collapsed;
        var openedByDrop = _openedByDrop;
        _openedByDrop = false;
        if (attached)
        {
            Activate();
            LauncherInput.Focus();
        }
        else if (openedByDrop && _host.Attachments.Count == 0 && !_pinned)
        {
            Collapse();
        }
    }
}
