using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace HangAll.Core;

/// <summary>Windows clipboard markers that opt content out of history and monitoring.</summary>
public static class ClipboardPrivacy
{
    public const string ExcludeFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";
    public const string ClipboardViewerIgnore = "Clipboard Viewer Ignore";
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";

    public static bool IsExcluded(
        IDataObject? data,
        IReadOnlyCollection<string>? formatNames = null)
    {
        if (data is null) return false;

        string[] formats;
        try { formats = formatNames?.ToArray() ?? data.GetFormats(false); }
        catch { return false; }

        if (HasFormat(formats, ExcludeFromMonitorProcessing) ||
            HasFormat(formats, ClipboardViewerIgnore))
        {
            return true;
        }

        string? canIncludeFormat = formats.FirstOrDefault(
            format => string.Equals(format, CanIncludeInHistory, StringComparison.OrdinalIgnoreCase));
        if (canIncludeFormat is null) return false;

        try { return IsZero(data.GetData(canIncludeFormat, false)); }
        catch { return false; }
    }

    private static bool HasFormat(IEnumerable<string> formats, string format) =>
        formats.Any(item => string.Equals(item, format, StringComparison.OrdinalIgnoreCase));

    private static bool IsZero(object? value)
    {
        switch (value)
        {
            case byte number: return number == 0;
            case sbyte number: return number == 0;
            case short number: return number == 0;
            case ushort number: return number == 0;
            case int number: return number == 0;
            case uint number: return number == 0;
            case long number: return number == 0;
            case ulong number: return number == 0;
            case string text:
                return long.TryParse(text.Trim(), out long parsed) && parsed == 0;
            case byte[] bytes:
                return bytes.Length > 0 && bytes.All(item => item == 0);
            case Stream stream:
                return IsZeroStream(stream);
            default:
                return false;
        }
    }

    private static bool IsZeroStream(Stream stream)
    {
        long? position = stream.CanSeek ? stream.Position : null;
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            var buffer = new byte[16];
            int read = stream.Read(buffer, 0, buffer.Length);
            return read > 0 && buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (position.HasValue)
            {
                try { stream.Position = position.Value; }
                catch { }
            }
        }
    }
}

/// <summary>The text-bearing formats read from one clipboard update.</summary>
public sealed record TextClipboardPayload(string? Text, string? Rtf, string? Html)
{
    [JsonIgnore]
    public bool HasContent =>
        !string.IsNullOrEmpty(Text) ||
        !string.IsNullOrEmpty(Rtf) ||
        !string.IsNullOrEmpty(Html);

    /// <summary>Reads any text, RTF and HTML present. File drops stay with the file-capture path.</summary>
    public static TextClipboardPayload? FromDataObject(
        IDataObject? data,
        IReadOnlyCollection<string>? formatNames = null)
    {
        if (data is null) return null;
        if (ClipboardPrivacy.IsExcluded(data, formatNames)) return null;

        string[] formats;
        try { formats = formatNames?.ToArray() ?? data.GetFormats(false); }
        catch { return null; }
        if (formats.Any(format =>
                string.Equals(format, DataFormats.FileDrop, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var payload = new TextClipboardPayload(
            ReadText(data, DataFormats.UnicodeText) ?? ReadText(data, DataFormats.Text),
            ReadText(data, DataFormats.Rtf),
            ReadText(data, DataFormats.Html)).Normalized();
        return payload.HasContent ? payload : null;
    }

    public TextClipboardPayload Normalized() => new(
        NormalizeLineEndings(Text),
        NormalizeLineEndings(Rtf),
        NormalizeLineEndings(Html));

    private static string? NormalizeLineEndings(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string? ReadText(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format, true)) return null;
            object? value = data.GetData(format, true);
            if (value is string text) return text;
            if (value is byte[] bytes) return Decode(bytes);
            if (value is MemoryStream stream)
            {
                if (stream.CanSeek) stream.Position = 0;
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                return reader.ReadToEnd();
            }
        }
        catch
        {
            // A provider may advertise a format and then refuse to render it.
        }
        return null;
    }

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>One immutable entry in the text clipboard history.</summary>
public sealed record TextClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string? Text { get; init; }
    public string? Rtf { get; init; }
    public string? Html { get; init; }
    /// <summary>Kept ahead of newer copies and last to be trimmed away.</summary>
    public bool Pinned { get; init; }

    [JsonIgnore]
    public bool HasContent =>
        !string.IsNullOrEmpty(Text) ||
        !string.IsNullOrEmpty(Rtf) ||
        !string.IsNullOrEmpty(Html);

    internal static TextClipboardEntry Create(TextClipboardPayload payload, DateTime capturedAt)
    {
        var normalized = payload.Normalized();
        return new TextClipboardEntry
        {
            CapturedAt = capturedAt,
            Text = normalized.Text,
            Rtf = normalized.Rtf,
            Html = normalized.Html,
        };
    }

    internal TextClipboardEntry Normalized()
    {
        var normalized = new TextClipboardPayload(Text, Rtf, Html).Normalized();
        return this with
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            CapturedAt = CapturedAt == default ? DateTime.Now : CapturedAt,
            Text = normalized.Text,
            Rtf = normalized.Rtf,
            Html = normalized.Html,
        };
    }

    /// <summary>
    /// Creates data for copying a history card. The marker and the matching
    /// clipboard sequence check keep HangAll's own copy out of the history.
    /// </summary>
    public DataObject CreateClipboardDataObject()
    {
        var data = new DataObject();
        if (!string.IsNullOrEmpty(Text))
        {
            data.SetData(DataFormats.UnicodeText, Text, false);
            data.SetData(DataFormats.Text, Text, false);
        }
        if (!string.IsNullOrEmpty(Rtf)) data.SetData(DataFormats.Rtf, Rtf, false);
        if (!string.IsNullOrEmpty(Html)) data.SetData(DataFormats.Html, Html, false);
        data.SetData(ClipboardWatcher.TextCopyMarker, "1", false);
        return data;
    }

    /// <summary>Creates a text-only copy while keeping HangAll's copy marker.</summary>
    public DataObject CreatePlainTextDataObject()
    {
        var data = new DataObject();
        string text = Text ?? string.Empty;
        data.SetData(DataFormats.UnicodeText, text, false);
        data.SetData(DataFormats.Text, text, false);
        data.SetData(ClipboardWatcher.TextCopyMarker, "1", false);
        return data;
    }
}

/// <summary>Applies the user's switch and appends only new clipboard content.</summary>
public sealed class TextClipboardRecorder
{
    private readonly Func<bool> _isEnabled;

    public TextClipboardRecorder(TextClipboardHistory history, Func<bool> isEnabled)
    {
        History = history;
        _isEnabled = isEnabled;
    }

    public TextClipboardHistory History { get; }

    public bool Capture(TextClipboardPayload payload, DateTime? capturedAt = null) =>
        _isEnabled() && History.Append(payload, capturedAt);
}

/// <summary>A standalone JSON history stored beside the other HangAll user data.</summary>
public sealed class TextClipboardHistory
{
    public const string FileName = "text-clipboard-history.json";
    private const int DefaultLimit = 200;
    /// <summary>Keeps the history file bounded even when large rich-text items are copied.</summary>
    private const long MaxRetainedChars = 8_000_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly ManualResetEventSlim _saveIdle = new(true);
    private readonly string _path;
    private readonly int _limit;
    private List<TextClipboardEntry> _items;
    private List<TextClipboardEntry>? _pendingSave;
    private bool _saveRunning;

    public TextClipboardHistory(string? path = null, int limit = DefaultLimit)
    {
        _path = path ?? System.IO.Path.Combine(Settings.Folder, FileName);
        _limit = Math.Max(1, limit);
        _items = Load();
        if (TrimLocked()) QueueSaveLocked();
    }

    public event Action? Changed;

    public IReadOnlyList<TextClipboardEntry> GetSnapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public bool Append(TextClipboardPayload payload, DateTime? capturedAt = null)
    {
        var normalized = payload.Normalized();
        if (!normalized.HasContent) return false;

        bool added;
        bool changed = false;
        lock (_gate)
        {
            int duplicateIndex = _items.FindIndex(item => SameContent(item, normalized));
            added = duplicateIndex < 0;
            if (added)
            {
                var fresh = TextClipboardEntry.Create(normalized, capturedAt ?? DateTime.Now);
                _items.Insert(PositionFor(fresh), fresh);
                changed = true;
            }
            else
            {
                var item = _items[duplicateIndex];
                var merged = item with
                {
                    Text = item.Text ?? normalized.Text,
                    Rtf = item.Rtf ?? normalized.Rtf,
                    Html = item.Html ?? normalized.Html,
                    // Copying the same thing again makes it the newest entry,
                    // not an old one that is about to be trimmed away.
                    CapturedAt = capturedAt ?? DateTime.Now,
                };
                _items.RemoveAt(duplicateIndex);
                _items.Insert(PositionFor(merged), merged);
                changed = true;
            }

            if (changed)
            {
                TrimLocked();
                QueueSaveLocked();
            }
        }

        if (changed) Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// The index an entry belongs at. The list is a pinned block followed by the
    /// rest, each newest first, so an entry lands inside its own class and in time
    /// order there - which is what makes unpinning an old entry put it back where
    /// it came from rather than at the front.
    /// </summary>
    private int PositionFor(TextClipboardEntry entry)
    {
        int firstUnpinned = _items.FindIndex(item => !item.Pinned);
        if (firstUnpinned < 0) firstUnpinned = _items.Count;
        int start = entry.Pinned ? 0 : firstUnpinned;
        int end = entry.Pinned ? firstUnpinned : _items.Count;
        for (int index = start; index < end; index++)
            if (_items[index].CapturedAt < entry.CapturedAt) return index;
        return end;
    }

    /// <summary>Pins an entry, or lets it go, and moves it to wherever it now belongs.</summary>
    public bool TogglePin(Guid id)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(item => item.Id == id);
            if (index < 0) return false;
            var updated = _items[index] with { Pinned = !_items[index].Pinned };
            _items.RemoveAt(index);
            _items.Insert(PositionFor(updated), updated);
            QueueSaveLocked();
        }

        Changed?.Invoke();
        return true;
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(item => item.Id == id);
            if (index < 0) return false;
            _items.RemoveAt(index);
            QueueSaveLocked();
        }

        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Persist even when the in-memory list is already empty: a history
            // file left behind by a failed load must still be cleared.
            _items.Clear();
            QueueSaveLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>Copies a history card without creating another history entry.</summary>
    public static bool CopyToClipboard(TextClipboardEntry entry) =>
        CopyDataObject(entry.CreateClipboardDataObject());

    /// <summary>Copies only the plain-text representation of a history card.</summary>
    public static bool CopyPlainTextToClipboard(TextClipboardEntry entry) =>
        CopyDataObject(entry.CreatePlainTextDataObject());

    private static bool CopyDataObject(DataObject data)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                ClipboardWatcher.RememberSelfCopy();
                return true;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(attempt * 40);
            }
            catch (Exception e)
            {
                Log.Error($"Could not copy text history: {e.Message}");
                return false;
            }
        }
    }

    private static bool SameContent(TextClipboardEntry item, TextClipboardPayload payload)
    {
        if (!string.IsNullOrEmpty(item.Text) && !string.IsNullOrEmpty(payload.Text))
            return item.Text.Length == payload.Text.Length &&
                   string.Equals(item.Text, payload.Text, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(item.Html) && !string.IsNullOrEmpty(payload.Html))
            return item.Html.Length == payload.Html.Length &&
                   string.Equals(item.Html, payload.Html, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(item.Rtf) && !string.IsNullOrEmpty(payload.Rtf))
            return item.Rtf.Length == payload.Rtf.Length &&
                   string.Equals(item.Rtf, payload.Rtf, StringComparison.Ordinal);
        return false;
    }

    private List<TextClipboardEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TextClipboardEntry>();
            var document = JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(_path), Options);
            return (document?.Items ?? new List<TextClipboardEntry>())
                .Where(item => item is not null && item.HasContent)
                .Select(item => item.Normalized())
                .OrderByDescending(item => item.Pinned)
                .ThenByDescending(item => item.CapturedAt)
                .Take(_limit)
                .ToList();
        }
        catch (Exception e)
        {
            Log.Error($"Could not read text clipboard history: {e.Message}");
            // Keep the unreadable file beside the live one instead of letting
            // the next capture overwrite it and lose the user's history.
            try
            {
                if (File.Exists(_path))
                {
                    var backup = _path + ".bad";
                    File.Copy(_path, backup, true);
                    Log.Notice($"Kept the unreadable history as {System.IO.Path.GetFileName(backup)}");
                }
            }
            catch (Exception backupError)
            {
                Log.Error($"Could not back up the unreadable history: {backupError.Message}");
            }
            return new List<TextClipboardEntry>();
        }
    }

    /// <summary>Drops the oldest entries until both the count and the character budget fit.</summary>
    private bool TrimLocked()
    {
        long total = 0;
        foreach (var item in _items) total += EntryChars(item);
        bool changed = false;
        while (_items.Count > 1 && (_items.Count > _limit || total > MaxRetainedChars))
        {
            int last = _items.Count - 1;
            // Pinned entries are the user's, not the program's. Once only pinned
            // ones are left the budget loses: dropping them would be a surprise.
            if (_items[last].Pinned) break;
            total -= EntryChars(_items[last]);
            _items.RemoveAt(last);
            changed = true;
        }
        return changed;
    }

    private static long EntryChars(TextClipboardEntry item) =>
        (long)(item.Text?.Length ?? 0) + (item.Rtf?.Length ?? 0) + (item.Html?.Length ?? 0);

    /// <summary>Snapshots the list under the history lock and lets a background writer persist it.</summary>
    private void QueueSaveLocked()
    {
        var snapshot = _items.ToList();
        lock (_saveGate)
        {
            _pendingSave = snapshot;
            if (_saveRunning) return;
            _saveRunning = true;
            _saveIdle.Reset();
        }
        _ = Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        while (true)
        {
            List<TextClipboardEntry>? snapshot;
            lock (_saveGate)
            {
                snapshot = _pendingSave;
                _pendingSave = null;
                if (snapshot is null)
                {
                    _saveRunning = false;
                    _saveIdle.Set();
                    return;
                }
            }
            WriteSnapshot(snapshot);
        }
    }

    private void WriteSnapshot(IReadOnlyList<TextClipboardEntry> items)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var document = new HistoryDocument { Items = items.ToList() };
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception e)
        {
            Log.Error($"Could not save text clipboard history: {e.Message}");
        }
    }

    /// <summary>Waits for the latest queued write, for shutdown and for the acceptance probe.</summary>
    public void Flush()
    {
        if (!_saveIdle.Wait(2000))
            Log.Error("Text clipboard history did not finish saving in time");
    }

    private sealed class HistoryDocument
    {
        public int Version { get; set; } = 1;
        public List<TextClipboardEntry> Items { get; set; } = new();
    }
}
