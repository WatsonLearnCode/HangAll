using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HangAll.Core;

public readonly record struct Thumbnail(BitmapSource Image, int PixelWidth, int PixelHeight, bool IsIcon = false);

/// <summary>Decodes screenshots down to the size the line needs, patiently if the file is still being written.</summary>
public static class Thumbnails
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".avif",
    };

    /// <summary>Recordings hang on the line too, the way Tendedero does it.</summary>
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    /// <summary>Something the line knows how to show: a picture or a recording.</summary>
    public static bool IsHangable(string path) => IsImage(path) || IsVideo(path);

    /// <summary>A thumbnail no larger than `maxPixels` on its longest side. Null if the file cannot be read.</summary>
    public static Thumbnail? Load(string path, int maxPixels = 480, int attempts = 8)
    {
        // Recordings (and anything else Windows has a thumbnail provider for)
        // get the picture Windows draws for them instead of a decoder.
        if (!IsImage(path)) return FromShell(path, maxPixels);

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0) throw new IOException("empty");
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                int w = frame.PixelWidth, h = frame.PixelHeight;
                if (w <= 0 || h <= 0) throw new IOException("no pixels");

                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = stream;
                if (Math.Max(w, h) > maxPixels)
                {
                    if (w >= h) image.DecodePixelWidth = maxPixels; else image.DecodePixelHeight = maxPixels;
                }
                image.EndInit();
                image.Freeze();
                return new Thumbnail(image, w, h);
            }
            catch (Exception e) when (i < attempts - 1 && (e is IOException or UnauthorizedAccessException || (i < 2 && e is NotSupportedException or FileFormatException)))
            {
                // The capture is still being written. Give it a moment. A format
                // Windows cannot decode at all gets two tries, not eight.
                Thread.Sleep(80 + i * 40);
            }
            catch (Exception e)
            {
                Log.Error($"Could not decode {path}: {e.Message}");
                return null;
            }
        }
        return null;
    }

    private static Thumbnail? FromShell(string path, int maxPixels)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Documents and other files are shown in a compact card, so ask
            // Windows for a desktop-quality source instead of a small icon.
            bool fileCard = !IsVideo(path);
            // A file card shows a 48 DIP icon; 256 px still covers it at 200%
            // DPI and keeps the decoded Shell bitmap substantially smaller.
            int shellSize = fileCard ? Math.Clamp(Math.Max(maxPixels, 256), 64, 256)
                                     : Math.Clamp(maxPixels, 32, 1024);
            var image = Interop.ShellThumbnails.Get(path, shellSize);
            if (image is not null && image.PixelWidth > 0 && image.PixelHeight > 0)
                return new Thumbnail(image, image.PixelWidth, image.PixelHeight);

            // No thumbnail provider: every file still gets the icon Explorer
            // draws for it, so a document, archive or executable can hang too.
            int iconSize = fileCard ? 256 : Math.Clamp(maxPixels, 32, 256);
            var icon = Interop.ShellThumbnails.Icon(path, Math.Clamp(iconSize, 32, 256));
            if (icon is null || icon.PixelWidth <= 0 || icon.PixelHeight <= 0) return null;
            return new Thumbnail(icon, icon.PixelWidth, icon.PixelHeight, IsIcon: true);
        }
        catch (Exception e)
        {
            Log.Error($"Could not draw a thumbnail for {path}: {e.Message}");
            return null;
        }
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    public static byte[]? PngBytes(string path)
    {
        // A recording travels as a file, never as a still.
        if (!IsImage(path)) return null;
        try
        {
            if (string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(path);
            var loaded = Load(path, int.MaxValue, 3);
            if (loaded is null) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(loaded.Value.Image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception e)
        {
            Log.Error($"Could not read {path}: {e.Message}");
            return null;
        }
    }
}
