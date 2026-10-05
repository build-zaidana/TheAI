using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cursors = System.Windows.Input.Cursors;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

/// <summary>Small rounded image previews for the chat (sent images and composer attachments).</summary>
internal static class ImageThumb
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// A rounded thumbnail <paramref name="height"/> tall (or a square crop); clicking it opens the
    /// image in the default viewer. Null when the file is not a readable image.
    /// </summary>
    public static FrameworkElement? Create(string path, double height, double maxWidth = 240, bool square = false)
    {
        if (!IsImage(path) || !File.Exists(path))
        {
            return null;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // the file may be deleted after sending
            bitmap.DecodePixelHeight = (int)(height * 2);
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch (Exception)
        {
            return null;
        }

        var aspect = bitmap.PixelHeight > 0 ? (double)bitmap.PixelWidth / bitmap.PixelHeight : 1;
        var width = square ? height : Math.Min(maxWidth, height * aspect);
        var thumb = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
            Cursor = Cursors.Hand,
            ToolTip = Path.GetFileName(path)
        };
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                // Only images get here, so the default viewer opens it.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        };
        return thumb;
    }

    /// <summary>A copy in the image cache (pasted screenshots are temporary); null if not an image.</summary>
    public static string? KeepCopy(string path)
    {
        if (!IsImage(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var folder = CodexBar.Providers.ClaudeCode.ClaudeSessionFiles.ImageCacheFolder;
            Directory.CreateDirectory(folder);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream))[..20];
            var target = Path.Combine(folder, hash + Path.GetExtension(path).ToLowerInvariant());
            if (!File.Exists(target))
            {
                File.Copy(path, target);
            }

            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
