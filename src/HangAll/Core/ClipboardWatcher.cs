using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HangAll.Interop;

namespace HangAll.Core;

/// <summary>
/// Catches captures that only reach the clipboard: Win+Shift+S when the
/// Snipping Tool is not saving files, PrtScn, Alt+PrtScn. A pure image on
/// the clipboard, with no text, HTML or files beside it, is written to the
/// inbox folder and hung. Anything we put on the clipboard ourselves carries
/// a file beside the image, so it is never caught.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    /// <summary>Marks clipboard data written by HangAll itself so it is never caught as a new capture.</summary>
    public const string CopyMarker = "HangAll.ClipboardCopy";
    /// <summary>Marks text written by HangAll itself so it is never caught as text history.</summary>
    public const string TextCopyMarker = "HangAll.TextClipboardCopy";
    private static readonly object SelfCopyGate = new();
    private static uint _selfCopySequence;
    private static HashSet<string> _selfCopyPaths = new(StringComparer.OrdinalIgnoreCase);
    private static long _selfCopyAt;

    private readonly MessageWindow _window;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, int, int> _onCapture;
    private readonly Action<IReadOnlyList<string>> _onFiles;
    private readonly Action<TextClipboardPayload> _onText;
    private readonly Func<int, int, bool> _recentlyHungFromFile;
    private bool _listening;
    private uint _lastSequence;
    // One timer for the whole watcher: every clipboard update used to allocate a
    // new one, and nothing stopped the old ones when the app closed.
    private readonly DispatcherTimer _catchTimer;
    private uint _catchSequence;

    public bool Enabled { get; set; } = true;
    public bool CatchImages { get; set; } = true;
    public bool CatchFiles { get; set; }
    public bool CatchText { get; set; }

    /// <summary>
    /// Remembers the clipboard write made by HangAll itself. The custom
    /// marker normally prevents a loop; the sequence number keeps other
    /// clipboard managers from stripping that marker and causing one.
    /// </summary>
    public static void RememberSelfCopy() => RememberSelfCopy(Array.Empty<string>());

    public static void RememberSelfCopy(IEnumerable<string> paths)
    {
        var normalized = paths
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return null; }
            })
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (SelfCopyGate)
        {
            _selfCopySequence = Native.GetClipboardSequenceNumber();
            _selfCopyPaths = normalized;
            _selfCopyAt = Environment.TickCount64;
        }
    }

    internal static bool IsSelfCopyFormats(IReadOnlyCollection<string>? formats) =>
        formats?.Any(format =>
            string.Equals(format, CopyMarker, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, TextCopyMarker, StringComparison.OrdinalIgnoreCase)) == true;

    /// <param name="recentlyHungFromFile">Whether a file with these pixel dimensions just hung, so the clipboard copy of the same capture is skipped.</param>
    public ClipboardWatcher(
        MessageWindow window,
        Action<string, int, int> onCapture,
        Action<IReadOnlyList<string>> onFiles,
        Action<TextClipboardPayload> onText,
        Func<int, int, bool> recentlyHungFromFile)
    {
        _window = window;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onCapture = onCapture;
        _onFiles = onFiles;
        _onText = onText;
        _recentlyHungFromFile = recentlyHungFromFile;
        _catchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(1200) };
        _catchTimer.Tick += (_, _) => OnCatchDue();
        _window.Message += OnMessage;
    }

    public void Start()
    {
        if (_listening) return;
        _listening = Native.AddClipboardFormatListener(_window.Handle);
        _lastSequence = Native.GetClipboardSequenceNumber();
    }

    private void OnMessage(int msg, IntPtr w, IntPtr l)
    {
        if (msg != Native.WM_CLIPBOARDUPDATE || !Enabled) return;
        var seq = Native.GetClipboardSequenceNumber();
        if (seq == _lastSequence) return;
        _lastSequence = seq;
        if (ConsumeSelfCopy(seq)) return;
        if (TryCatchText()) return;

        // The Snipping Tool may also be saving a file. Let that path hang the
        // capture first; then only catch what never reached a file.
        _catchSequence = seq;
        _catchTimer.Stop();
        _catchTimer.Start();
    }

    private void OnCatchDue()
    {
        _catchTimer.Stop();
        if (!_listening) return;
        if (Native.GetClipboardSequenceNumber() != _catchSequence) return; // already replaced
        TryCatch();
    }

    private bool TryCatchText()
    {
        if (!CatchText) return false;
        try
        {
            IDataObject data;
            try { data = Clipboard.GetDataObject(); }
            catch { return false; }
            if (data is null) return false;
            var formats = data.GetFormats(false);
            if (formats is null || formats.Length == 0) return false;
            if (ClipboardPrivacy.IsExcluded(data, formats)) return false;
            if (IsSelfCopyFormats(formats)) return false;
            var payload = TextClipboardPayload.FromDataObject(data, formats);
            if (payload is null) return false;
            _onText(payload);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Clipboard text capture failed: {e.Message}");
            return false;
        }
    }

    private static bool ConsumeSelfCopy(uint sequence)
    {
        lock (SelfCopyGate)
        {
            if (_selfCopySequence == 0 || _selfCopySequence != sequence) return false;
            ForgetSelfCopy();
            return true;
        }
    }

    private static bool ConsumeSelfCopyPaths(IReadOnlyList<string> files)
    {
        lock (SelfCopyGate)
        {
            if (_selfCopySequence == 0 || _selfCopyPaths.Count == 0) return false;
            if (Environment.TickCount64 - _selfCopyAt > 5000) return false;
            if (!files.All(path => _selfCopyPaths.Contains(path))) return false;
            ForgetSelfCopy();
            return true;
        }
    }

    private static void ForgetSelfCopy()
    {
        _selfCopySequence = 0;
        _selfCopyPaths.Clear();
        _selfCopyAt = 0;
    }

    private static readonly string[] NotPureImage =
    {
        DataFormats.Text, DataFormats.UnicodeText, DataFormats.Html, DataFormats.Rtf, DataFormats.FileDrop,
        "Object Descriptor", "Link Source", "Embed Source", "Shell IDList Array", "FileName", "FileNameW",
    };

    private void TryCatch()
    {
        if (!Enabled) return;
        try
        {
            IDataObject data;
            try { data = Clipboard.GetDataObject(); }
            catch { return; }
            if (data is null) return;
            var formats = data.GetFormats(false);
            if (formats is null || formats.Length == 0) return;
            if (ClipboardPrivacy.IsExcluded(data, formats)) return;
            if (IsSelfCopyFormats(formats)) return;
            if (CatchText &&
                TextClipboardPayload.FromDataObject(data, formats) is { } textPayload)
            {
                _onText(textPayload);
                return;
            }
            var hasImage = formats.Contains(DataFormats.Bitmap)
                || formats.Contains(DataFormats.Dib)
                || formats.Contains("PNG");
            if (!hasImage)
            {
                // Screenshot tools must take priority over Explorer file lists.
                // Only path-only clipboard payloads reach the file branch.
                if (formats.Contains(DataFormats.FileDrop)) TryCatchFiles(formats);
                return;
            }
            if (!CatchImages) return;
            if (NotPureImage.Any(f => formats.Contains(f))) return;

            BitmapSource? image = null;
            if (formats.Contains("PNG") && data.GetData("PNG") is Stream png)
            {
                try
                {
                    var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    image = decoder.Frames[0];
                }
                catch { image = null; }
            }
            image ??= Clipboard.GetImage();
            if (image is null || image.PixelWidth < 8 || image.PixelHeight < 8) return;
            if (_recentlyHungFromFile(image.PixelWidth, image.PixelHeight)) return;

            var path = Inbox.NewCapturePath();
            Thumbnails.SavePng(image, path);
            Log.Notice($"Caught a clipboard capture: {Path.GetFileName(path)}");
            _onCapture(path, image.PixelWidth, image.PixelHeight);
        }
        catch (Exception e)
        {
            Log.Error($"Clipboard capture failed: {e.Message}");
        }
    }

    private bool TryCatchFiles(string[] formats)
    {
        if (!CatchFiles || !formats.Contains(DataFormats.FileDrop)) return false;
        string[] files;
        try
        {
            files = Clipboard.GetFileDropList()
                .Cast<string>()
                .Select(path =>
                {
                    try { return Path.GetFullPath(path); }
                    catch { return null; }
                })
                .Where(path => path is not null && File.Exists(path) && !Directory.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e)
        {
            Log.Error($"Could not read copied files: {e.Message}");
            return false;
        }

        if (files.Length == 0) return false;
        if (ConsumeSelfCopyPaths(files)) return true;
        Log.Notice($"Caught {files.Length} file(s) from the clipboard");
        _onFiles(files);
        return true;
    }

    public void Dispose()
    {
        _catchTimer.Stop();
        _window.Message -= OnMessage;
        if (_listening) Native.RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
    }
}
