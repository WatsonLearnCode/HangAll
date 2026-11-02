using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HangAll.Core;

namespace HangAll.UI;

/// <summary>
/// A small, dependency-free GIF playback source for the clear-line runner.
/// Frames are decoded and frozen once so the animation does not keep files open.
/// </summary>
internal sealed class ClearAnimation
{
    private const string BuiltInPath = "Assets/clear-run.gif";
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(30);
    private static readonly object CacheLock = new();
    private static ClearAnimation? _builtInCache;
    private static ClearAnimation? _fallbackCache;
    private static ClearAnimation? _customCache;
    private static string? _customCachePath;
    private static long _customCacheLength;
    private static long _customCacheWriteTicks;

    public IReadOnlyList<BitmapSource> Frames { get; }
    public IReadOnlyList<TimeSpan> Delays { get; }
    public bool IsFallback { get; }

    private ClearAnimation(IReadOnlyList<BitmapSource> frames, IReadOnlyList<TimeSpan> delays, bool fallback = false)
    {
        Frames = frames;
        Delays = delays;
        IsFallback = fallback;
    }

    /// <summary>
    /// Loads a user GIF when it is valid, otherwise the embedded GIF. A tiny
    /// generated frame keeps the button usable even if the resource is missing.
    /// </summary>
    public static ClearAnimation Load(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var custom = LoadCachedFile(customPath);
            if (custom is not null) return custom;
            Log.Notice($"Clear animation could not be read; using the built-in animation: {customPath}");
        }

        lock (CacheLock)
        {
            if (_builtInCache is not null) return _builtInCache;
            _builtInCache = TryLoadResource(BuiltInPath);
            if (_builtInCache is not null) return _builtInCache;

            Log.Error("Built-in clear animation could not be read; using a generated fallback.");
            _fallbackCache ??= Fallback();
            return _fallbackCache;
        }
    }

    private static ClearAnimation? LoadCachedFile(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch
        {
            return null;
        }

        lock (CacheLock)
        {
            if (_customCache is not null &&
                string.Equals(_customCachePath, path, StringComparison.OrdinalIgnoreCase) &&
                _customCacheLength == info.Length &&
                _customCacheWriteTicks == info.LastWriteTimeUtc.Ticks)
                return _customCache;

            var loaded = TryLoadFile(path);
            if (loaded is null) return null;
            _customCache = loaded;
            _customCachePath = path;
            _customCacheLength = info.Length;
            _customCacheWriteTicks = info.LastWriteTimeUtc.Ticks;
            return loaded;
        }
    }

    private static ClearAnimation? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    private static ClearAnimation? TryLoadResource(string path)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            return stream is null ? null : Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    private static ClearAnimation? Decode(Stream stream)
    {
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0) return null;

        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            var source = frame.CloneCurrentValue();
            if (source.CanFreeze) source.Freeze();
            frames.Add(source);
            delays.Add(DelayFor(frame));
        }
        return new ClearAnimation(frames, delays);
    }

    private static TimeSpan DelayFor(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var value = metadata.GetQuery("/grctlext/Delay");
                if (value is ushort ushortValue && ushortValue > 0)
                    return TimeSpan.FromMilliseconds(ushortValue * 10);
                if (value is int intValue && intValue > 0)
                    return TimeSpan.FromMilliseconds(intValue * 10);
                if (value is long longValue && longValue > 0)
                    return TimeSpan.FromMilliseconds(longValue * 10);
            }
        }
        catch
        {
            // Some GIFs expose malformed metadata; a stable frame rate is better.
        }
        return DefaultDelay;
    }

    private static ClearAnimation Fallback()
    {
        const int size = 48;
        const int stride = size * 4;
        var pixels = new byte[stride * size];
        var center = (size - 1) / 2.0;
        var radius = size * 0.34;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - center;
            double dy = y - center;
            if (dx * dx + dy * dy > radius * radius) continue;
            int i = y * stride + x * 4;
            pixels[i] = 0xF6;
            pixels[i + 1] = 0x7D;
            pixels[i + 2] = 0x2F;
            pixels[i + 3] = 0xE8;
        }

        var frame = BitmapSource.Create(
            size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        frame.Freeze();
        return new ClearAnimation(new[] { frame }, new[] { DefaultDelay }, fallback: true);
    }
}
