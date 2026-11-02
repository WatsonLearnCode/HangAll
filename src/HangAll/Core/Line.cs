using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HangAll.Interop;

namespace HangAll.Core;

/// <summary>One screenshot hanging on the line.</summary>
public sealed class Pegged
{
    private static readonly Random Rng = new();

    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; }
    public BitmapSource Thumb { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    /// <summary>A file with no real thumbnail, shown with the icon Windows draws for it.</summary>
    public bool IsIcon { get; set; }
    /// <summary>Every photo hangs a little crooked, like on a real line.</summary>
    public double Tilt { get; } = Rng.NextDouble() * 5 - 2.5;
    public bool Falling { get; set; }
    /// <summary>Still flying in from where it was captured; the card waits hidden.</summary>
    public bool Flying { get; set; }
    /// <summary>Copied, dragged out, opened or edited at least once. Restored photos count as used.</summary>
    public bool Used { get; set; }
    /// <summary>Kept on purpose: never pushed off by newer captures.</summary>
    public bool Pinned { get; set; }
    public DateTime HungAt { get; set; } = DateTime.Now;
    /// <summary>Last time the card was copied, opened, edited, dragged out or previewed.</summary>
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
    /// <summary>Where it hangs along the line, as a fraction of the width, when photos are not arranged automatically.</summary>
    public double? Spot { get; set; }
    /// <summary>A recording rather than a still: it gets a play badge and opens in the player.</summary>
    public bool IsVideo => Thumbnails.IsVideo(Path);
    public bool IsImage => Thumbnails.IsImage(Path);
    /// <summary>A document, archive, executable or other file that is neither a still nor a recording.</summary>
    public bool IsFile => !IsImage && !IsVideo;

    public Pegged(string path, Thumbnail thumb)
    {
        Path = path;
        Thumb = thumb.Image;
        PixelWidth = thumb.PixelWidth;
        PixelHeight = thumb.PixelHeight;
        IsIcon = thumb.IsIcon;
    }
}

/// <summary>
/// The line itself: what hangs on it and what you can do with each item.
/// The files never move. The line is only a view onto them.
/// </summary>
public sealed class Line
{
    private readonly List<Pegged> _items = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Random _rng = new();
    private List<Pegged>? _orderedCache;
    private string _orderedSort = "";
    private int _saveBatchDepth;
    private bool _savePending;
    private int _saveHoldDepth;
    private int _notificationBatchDepth;
    private bool _notificationPending;

    public IReadOnlyList<Pegged> Items => _items;

    /// <summary>The current view order. Manual keeps the stored order; the other modes are stable projections.</summary>
    public IReadOnlyList<Pegged> OrderedItems
    {
        get
        {
            var sort = Settings.CardSortOption(Settings.Current.CardSort);
            if (sort == "manual") return _items;
            if (_orderedCache is not null && _orderedSort == sort) return _orderedCache;

            _orderedCache = sort switch
            {
                "time" => _items
                    .OrderBy(i => i.HungAt)
                    .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "type" => _items
                    .GroupBy(i => Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Min(i => i.HungAt))
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g.OrderBy(i => i.HungAt).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
                _ => _items.ToList(),
            };
            _orderedSort = sort;
            return _orderedCache;
        }
    }

    /// <summary>The list changed: something hung, fell or was removed.</summary>
    public event Action? ItemsChanged;
    /// <summary>One item changed in place: landed, new thumbnail.</summary>
    public event Action<Pegged>? ItemUpdated;
    /// <summary>A little wind.</summary>
    public event Action? Gust;
    /// <summary>Called just before a photo starts falling, so the fall can be drawn over the whole screen.</summary>
    public event Action<Pegged>? Fall;
    public event Action? StateChanged;
    /// <summary>A note editor was requested for a note that is already hanging.</summary>
    internal event Action<string, Guid>? NoteEditRequested;

    private Guid? _copiedId, _draggingId, _pressedId;
    public Guid? CopiedId { get => _copiedId; set { if (_copiedId != value) { _copiedId = value; StateChanged?.Invoke(); } } }
    /// <summary>What the badge under the card says: "Copied", or "Text copied".</summary>
    public string CopiedLabel { get; set; } = UI.Strings.Copied;
    public Guid? DraggingId { get => _draggingId; set { if (_draggingId != value) { _draggingId = value; StateChanged?.Invoke(); } } }
    public Guid? PressedId { get => _pressedId; set { if (_pressedId != value) { _pressedId = value; StateChanged?.Invoke(); } } }

    /// <summary>A positive value is an exact limit, -1 is unlimited and 0 is treated as unlimited.</summary>
    public int MaxItems { get; set; } = 8;
    private bool _menuOpen;
    /// <summary>A context menu is open: clicks belong to it, not to the window underneath.</summary>
    public bool MenuOpen
    {
        get => _menuOpen;
        set
        {
            if (_menuOpen == value) return;
            _menuOpen = value;
            StateChanged?.Invoke();
        }
    }

    public bool SoundOn
    {
        get => Settings.Current.SoundOn;
        set { Settings.Current.SoundOn = value; Sounds.Enabled = value; Settings.Current.Save(); }
    }

    public int LiveCount => _items.Count(i => !i.Falling);
    /// <summary>Whether something hangs that has not been copied, dragged out or opened yet.</summary>
    public bool HasUnused => _items.Any(i => !i.Falling && !i.Used);

    private readonly bool _persist;

    public Line(bool persist = true)
    {
        _persist = persist;
        Sounds.Enabled = persist && SoundOn;
        if (persist)
        {
            // Automatic capacity is only known once the line is on a screen, so
            // restoring starts unlimited and AppController trims it afterwards.
            MaxItems = Settings.Current.MaxItems is -1 ? -1 : Settings.Current.MaxItems ?? -1;
            Notes.MigrateLegacyNotes();
            Restore();
        }
        ScheduleGust();
    }

    public Pegged? Find(Guid id) => _items.FirstOrDefault(i => i.Id == id);

    // MARK: Hanging and dropping

    public Guid? Hang(string path, bool quietly = false, bool flying = false, Thumbnail? preloaded = null)
    {
        if (_items.Any(i => !i.Falling && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return null;
        var thumb = preloaded ?? Thumbnails.Load(path);
        if (thumb is null) return null;
        var hungAt = Settings.Current.HangTimes.TryGetValue(path, out var stored) ? stored : DateTime.Now;
        var lastUsedAt = Settings.Current.LastUsedTimes.TryGetValue(path, out var usedAt) ? usedAt : hungAt;
        var item = new Pegged(path, thumb.Value) { Flying = flying, HungAt = hungAt, LastUsedAt = lastUsedAt };
        _items.Add(item);
        InvalidateOrder();
        TrimToCapacity();
        Save();
        NotifyItemsChanged();
        if (!quietly) Sounds.PlayTink();
        return item.Id;
    }

    /// <summary>
    /// Hangs a group of already decoded files with one view refresh and one
    /// settings write. The caller keeps the existing per-card fly-in rules.
    /// </summary>
    public IReadOnlyList<Guid> HangMany(IEnumerable<(string Path, Thumbnail Thumbnail)> files, bool quietly = true, bool flying = false)
    {
        var result = new List<Guid>();
        BeginNotificationBatch();
        BeginSaveBatch();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // One pass over the hanging cards instead of a linear scan per file.
            foreach (var item in _items)
                if (!item.Falling) seen.Add(item.Path);
            foreach (var (path, thumb) in files)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || Directory.Exists(path)) continue;
                if (!seen.Add(path)) continue;

                var hungAt = Settings.Current.HangTimes.TryGetValue(path, out var stored) ? stored : DateTime.Now;
                var lastUsedAt = Settings.Current.LastUsedTimes.TryGetValue(path, out var usedAt) ? usedAt : hungAt;
                var item = new Pegged(path, thumb) { Flying = flying, HungAt = hungAt, LastUsedAt = lastUsedAt };
                _items.Add(item);
                result.Add(item.Id);
            }

            if (result.Count > 0)
            {
                InvalidateOrder();
                TrimToCapacity();
                Save();
                NotifyItemsChanged();
            }
        }
        finally
        {
            EndSaveBatch();
            EndNotificationBatch();
        }

        if (result.Count > 0 && !quietly) Sounds.PlayTink();
        return result;
    }

    /// <summary>Enforces a positive exact limit; unlimited and automatic defer to the caller.</summary>
    public void TrimToCapacity()
    {
        if (MaxItems <= 0) return;
        while (LiveCount > MaxItems)
        {
            var oldest = _items
                .Where(i => !i.Falling && !i.Pinned)
                .OrderBy(i => i.HungAt)
                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (oldest is null) break; // everything left is kept on purpose
            Drop(oldest.Id, quietly: true);
        }
    }

    /// <summary>The capture has reached the line: the real card takes over.</summary>
    public void Land(Guid id)
    {
        var item = Find(id);
        if (item is null || !item.Flying) return;
        item.Flying = false;
        ItemUpdated?.Invoke(item);
    }

    public void Drop(Guid id, bool quietly = false)
    {
        var item = Find(id);
        if (item is null || item.Falling) return;
        UI.NoteWindow.Detach(item.Path);
        Fall?.Invoke(item);
        item.Falling = true;
        Originals.Forget(item.Path);
        Save();
        NotifyItemsChanged();
        if (!quietly) Sounds.PlayPop();
        Later(0.6, () =>
        {
            if (_items.Remove(item))
            {
                InvalidateOrder();
                NotifyItemsChanged();
            }
        });
    }

    public void Clear()
    {
        var live = _items.Where(i => !i.Falling).ToList();
        if (live.Count == 0) return;
        var sweep = Snapshot(live.Select(i => i.Id));
        // The cascade of falls writes the settings file once at the end instead
        // of once per card. FlushSave() still forces a write on exit.
        HoldSave();
        for (int n = 0; n < live.Count; n++)
        {
            var id = live[n].Id;
            bool quiet = n > 0;
            Later(0.06 * n, () => Drop(id, quiet));
        }
        Later(0.06 * (live.Count - 1) + 0.65, () =>
        {
            ReleaseSaveHold();
            // Only once the last card has left: undo re-hangs by path, and a card
            // still falling would leave two live entries for the same file.
            AnnounceSweep(sweep);
        });
    }

    /// <summary>
    /// One card as it hung, kept so a sweep can be undone. Spot stays nullable: a card
    /// that was never put down by hand has no place to return to.
    /// </summary>
    internal readonly record struct SweptCard(string Path, double? Spot, bool Pinned);

    /// <summary>Raised when a whole sweep leaves the line, before the cards fall.</summary>
    internal event Action<IReadOnlyList<SweptCard>>? Swept;

    /// <summary>Remembers how these cards hung, so a sweep started elsewhere can be undone.</summary>
    internal IReadOnlyList<SweptCard> Snapshot(IEnumerable<Guid> ids) => ids
        .Select(Find)
        .OfType<Pegged>()
        .Where(i => !i.Falling)
        .Select(i => new SweptCard(i.Path, i.Spot, i.Pinned))
        .ToList();

    /// <summary>Offers one undo for a batch captured before it fell.</summary>
    internal void AnnounceSweep(IReadOnlyList<SweptCard> batch)
    {
        if (batch.Count > 0) Swept?.Invoke(batch);
    }

    /// <summary>
    /// Puts a swept batch back. A full sweep lands in its old order because the line
    /// was emptied first; a partial one lands behind what is still hanging.
    /// </summary>
    internal void Restore(IReadOnlyList<SweptCard> batch)
    {
        if (batch.Count == 0) return;
        BeginNotificationBatch();
        BeginSaveBatch();
        try
        {
            foreach (var card in batch)
            {
                if (!File.Exists(card.Path)) continue;
                if (Hang(card.Path, quietly: true) is not { } id) continue;
                if (Find(id) is not { } item) continue;
                item.Spot = card.Spot;
                item.Pinned = card.Pinned;
            }
            InvalidateOrder();
        }
        finally
        {
            EndSaveBatch();
            EndNotificationBatch();
        }
    }

    /// <summary>Remembers where a photo was put by hand.</summary>
    public void SetSpot(Guid id, double fraction, bool save)
    {
        var item = Find(id);
        if (item is null) return;
        item.Spot = Math.Clamp(fraction, 0, 1);
        if (save) Save();
    }

    /// <summary>Moves a photo to another place on the line. `liveIndex` counts only photos that are not falling.</summary>
    public void Move(Guid id, int liveIndex)
    {
        var item = Find(id);
        if (item is null) return;
        var live = _items.Where(i => !i.Falling && i != item).ToList();
        liveIndex = Math.Clamp(liveIndex, 0, live.Count);
        if (live.Count > 0 && liveIndex < live.Count && _items.IndexOf(live[liveIndex]) == _items.IndexOf(item) + 1) return;
        _items.Remove(item);
        int at = liveIndex < live.Count ? _items.IndexOf(live[liveIndex]) : _items.Count;
        _items.Insert(at, item);
        InvalidateOrder();
        Save();
        NotifyItemsChanged();
    }

    /// <summary>Photos whose file was deleted or moved away fall off by themselves.</summary>
    public void Prune()
    {
        foreach (var item in _items.Where(i => !i.Falling && !File.Exists(i.Path)).ToList())
        {
            Notes.Forget(item.Path);
            Drop(item.Id, quietly: true);
        }
    }

    /// <summary>
    /// Finishes the drops at once. The app lets a card fall for 0.6s on a
    /// dispatcher timer before removing it; a caller that drives the animation
    /// itself has no such clock, so it clears the fallen cards at the end.
    /// </summary>
    internal void SweepDrops()
    {
        if (_items.RemoveAll(item => item.Falling) == 0) return;
        InvalidateOrder();
        NotifyItemsChanged();
    }

    // MARK: Actions on one photo

    public void Copy(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        CopiedLabel = UI.Strings.Copied;
        try
        {
            var data = new DataObject();
            data.SetData(ClipboardWatcher.CopyMarker, "1", false);
            // A still goes onto the clipboard as a picture; a recording goes as a
            // file, which is what a chat or an editor can actually paste.
            var png = item.IsImage ? Thumbnails.PngBytes(item.Path) : null;
            if (png is not null)
            {
                data.SetData("PNG", new MemoryStream(png), false);
                var full = Thumbnails.Load(item.Path, int.MaxValue, 3);
                if (full is not null) data.SetImage(full.Value.Image);
            }
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Path });
            if (Notes.Get(item.Path) is { } note && !string.IsNullOrWhiteSpace(note.Text)) data.SetText(note.Text);
            Clipboard.SetDataObject(data, true);
            ClipboardWatcher.RememberSelfCopy(new[] { item.Path });
            MarkUsed(item);
        }
        catch (Exception e)
        {
            Log.Error($"Could not copy: {e.Message}");
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        CopiedId = id;
        Later(1.2, () => { if (CopiedId == id) CopiedId = null; });
    }

    public void Open(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        MarkUsed(item);
        if (!Shell.Open(item.Path)) System.Media.SystemSounds.Beep.Play();
    }

    /// <summary>A new sticky note hangs on the line, ready to be written on.</summary>
    public Guid? NewNote(string color = "yellow")
    {
        var path = Notes.Create(color);
        var id = Hang(path);
        if (id is { } noteId) RequestNoteEditor(path, noteId);
        return id;
    }

    public bool IsNote(Guid id) => Find(id) is { } item && Notes.IsNote(item.Path);

    public void EditNote(Guid id)
    {
        var item = Find(id);
        if (item is null || !Notes.IsNote(item.Path)) return;
        MarkUsed(item);
        RequestNoteEditor(item.Path, item.Id);
    }

    private void RequestNoteEditor(string path, Guid id)
    {
        if (NoteEditRequested is { } requested) requested(path, id);
        else UI.NoteWindow.Show(path, this);
    }

    public void RecolourNote(Guid id, string color)
    {
        var item = Find(id);
        if (item is null || Notes.Get(item.Path) is not { } data) return;
        Notes.Update(item.Path, data.Tasks, color);
        ReloadThumbnail(item.Path);
    }

    public bool ToggleNoteTask(Guid id, int taskIndex)
    {
        var item = Find(id);
        if (item is null || Notes.Get(item.Path) is not { } data) return false;
        int index = taskIndex;
        if (index < 0 || index >= data.Tasks.Count) return false;
        if (!Notes.SetTaskCompleted(item.Path, index, !data.Tasks[index].Completed)) return false;
        ReloadThumbnail(item.Path);
        return true;
    }

    /// <summary>Press and hold: the photo opens enlarged with a pen. Every mark is saved into the file.</summary>
    public void Edit(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        MarkUsed(item);
        if (Notes.IsNote(item.Path)) RequestNoteEditor(item.Path, item.Id);
        // A recording cannot be marked up: pressing and holding plays it instead.
        else if (!item.IsImage) Open(id);
        else UI.MarkupWindow.Show(item.Path, this);
    }

    /// <summary>Open the photo in Paint, or whatever edits images on this PC.</summary>
    public void EditExternal(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        MarkUsed(item);
        // Paint cannot open a recording; the default player can.
        bool ok = item.IsImage ? Shell.Edit(item.Path) : Shell.Open(item.Path);
        if (!ok) System.Media.SystemSounds.Beep.Play();
    }

    /// <summary>Copies a file by path, the same way a click on its card would.</summary>
    public void CopyPath(string path)
    {
        var item = _items.FirstOrDefault(i => !i.Falling && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is not null) Copy(item.Id);
    }

    /// <summary>Marks a card as used and records the time for automatic take-down.</summary>
    public void MarkUsed(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        MarkUsed(item);
    }

    /// <summary>Copies several cards as one file list, including only live files.</summary>
    public void CopyMany(IEnumerable<Guid> ids)
    {
        var items = ids.Distinct()
            .Select(Find)
            .Where(i => i is not null && !i.Falling)
            .Cast<Pegged>()
            .ToList();
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            Copy(items[0].Id);
            return;
        }

        var files = new System.Collections.Specialized.StringCollection();
        foreach (var item in items) files.Add(item.Path);
        var data = new DataObject();
        data.SetData(ClipboardWatcher.CopyMarker, "1", false);
        data.SetFileDropList(files);
        if (!TrySetClipboard(data))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        ClipboardWatcher.RememberSelfCopy(items.Select(i => i.Path));
        foreach (var item in items)
        {
            item.LastUsedAt = DateTime.Now;
            item.Used = true;
        }
        Save();
        CopiedLabel = UI.Strings.Copied;
        CopiedId = items[0].Id;
        Later(1.2, () =>
        {
            if (items.Count > 0 && CopiedId == items[0].Id) CopiedId = null;
        });
    }

    /// <summary>Copies selected paths, one per line.</summary>
    public bool CopyPaths(IEnumerable<Guid> ids) =>
        CopySelectedText(ids, items => string.Join(Environment.NewLine, items.Select(i => i.Path)));

    /// <summary>Copies selected file names, one per line.</summary>
    public bool CopyFileNames(IEnumerable<Guid> ids) =>
        CopySelectedText(ids, items => string.Join(Environment.NewLine, items.Select(i => Path.GetFileName(i.Path))));

    /// <summary>Copies Markdown image links for an all-image selection.</summary>
    public bool CopyMarkdownLinks(IEnumerable<Guid> ids)
    {
        var items = LiveItems(ids);
        if (items.Count == 0 || items.Any(i => !i.IsImage)) return false;
        string text = string.Join(Environment.NewLine, items.Select(item =>
        {
            string alt = Path.GetFileNameWithoutExtension(item.Path).Replace("[", "\\[").Replace("]", "\\]");
            return $"![{alt}]({FileUri(item.Path)})";
        }));
        return CopyPlainText(text, items);
    }

    /// <summary>Reads one image and copies its raw Base64 representation.</summary>
    public async Task<bool> CopyImageBase64(Guid id)
    {
        var item = Find(id);
        if (item is null || item.Falling || !item.IsImage) return false;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(item.Path); }
        catch (Exception e)
        {
            Log.Error($"Could not read image as Base64: {e.Message}");
            System.Media.SystemSounds.Beep.Play();
            return false;
        }
        return CopyPlainText(Convert.ToBase64String(bytes), new[] { item });
    }

    /// <summary>Takes a list of cards off the line without deleting any file.</summary>
    public IReadOnlyList<Guid> TakeDownMany(IEnumerable<Guid> ids)
    {
        var removed = new List<Guid>();
        var targets = ids.Distinct().ToList();
        // Captured before the first discard: afterwards the cards are gone. Cards whose
        // file goes to the Recycle Bin with them are left out - offering an undo that
        // cannot deliver one is worse than saying nothing.
        var snapshot = Snapshot(targets.Where(id => Find(id) is { } card && !OwnedCapture(card)).ToList());
        HoldSave();
        try
        {
            foreach (var id in targets)
            {
                var item = Find(id);
                if (item is null || item.Falling) continue;
                Discard(id);
                if (Find(id)?.Falling == true) removed.Add(id);
            }
        }
        finally { ReleaseSaveHold(); }
        // Discard drops each card on its own 0.6s fall, so the offer waits for the
        // same reason Clear() does.
        if (snapshot.Count > 0) Later(0.7, () => AnnounceSweep(snapshot));
        return removed;
    }

    /// <summary>Recycles the files that can be recycled and returns only the successful card IDs.</summary>
    public IReadOnlyList<Guid> TrashMany(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        var removed = new List<Guid>();
        HoldSave();
        try
        {
            foreach (var id in list)
            {
                var item = Find(id);
                if (item is null || item.Falling) continue;
                if (File.Exists(item.Path) && !Shell.Recycle(item.Path))
                {
                    Log.Error($"Could not recycle {item.Path}");
                    continue;
                }
                Log.Notice($"Recycled {Path.GetFileName(item.Path)}");
                Notes.Forget(item.Path);
                Drop(id, quietly: true);
                removed.Add(id);
            }
        }
        finally { ReleaseSaveHold(); }
        if (removed.Count > 0) Sounds.PlayWhoosh();
        else if (list.Count > 0) System.Media.SystemSounds.Beep.Play();
        return removed;
    }

    /// <summary>Takes eligible unpinned cards off the line. The default mode uses last-use time.</summary>
    public IReadOnlyList<Guid> AutoDropExpired(DateTime now)
    {
        var settings = Settings.Current;
        if (!settings.AutoDropEnabled) return Array.Empty<Guid>();
        int minutes = Settings.AutoDropMinutesOption(settings.AutoDropMinutes);
        string mode = Settings.AutoDropModeOption(settings.AutoDropMode);
        var cutoff = now.AddMinutes(-minutes);
        var expired = _items
            .Where(i => !i.Falling && !i.Pinned)
            .Where(i => mode == "hung" ? i.HungAt <= cutoff : i.LastUsedAt <= cutoff)
            .Select(i => i.Id)
            .ToList();
        if (expired.Count == 0) return Array.Empty<Guid>();

        var removed = new List<Guid>();
        HoldSave();
        try
        {
            foreach (var id in expired)
            {
                var item = Find(id);
                if (item is null || item.Falling || item.Pinned) continue;
                Drop(id, quietly: true);
                if (Find(id)?.Falling == true) removed.Add(id);
            }
        }
        finally { ReleaseSaveHold(); }
        return removed;
    }

    /// <summary>Moves the file to the Recycle Bin and takes the photo off the line.</summary>
    public void Trash(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        if (File.Exists(item.Path) && !Shell.Recycle(item.Path))
        {
            Log.Error($"Could not recycle {item.Path}");
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        Log.Notice($"Recycled {Path.GetFileName(item.Path)}");
        Notes.Forget(item.Path);
        Sounds.PlayWhoosh();
        Drop(id, quietly: true);
    }

    /// <summary>Whether the file lives in HangAll's own folder.</summary>
    public bool IsInInbox(Guid id)
    {
        var item = Find(id);
        return item is not null && Inbox.Contains(item.Path);
    }

    /// <summary>The corner cross and "Take down" both end up here.</summary>
    public void Discard(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        // Only HangAll's own captures and notes are safe to discard with the
        // file. A plain file that merely lives in Inbox is still a reference.
        if (OwnedCapture(item)) Trash(id); else Drop(id);
    }

    /// <summary>
    /// True when taking this card down also recycles the file, which puts it beyond
    /// the reach of an undo that only re-hangs a path.
    /// </summary>
    private bool OwnedCapture(Pegged item) =>
        IsInInbox(item.Id) && (item.IsImage || item.IsVideo || Notes.IsNote(item.Path));

    /// <summary>Keep a caught capture by moving it to the Desktop.</summary>
    public void SaveToDesktop(Guid id) => MoveTo(id, Shell.Desktop());

    /// <summary>Keep a caught capture by moving it to Pictures\Screenshots.</summary>
    public void SaveToScreenshots(Guid id) => MoveTo(id, Shell.ScreenshotsFolder());

    private void MoveTo(Guid id, string folder)
    {
        var item = Find(id);
        if (item is null) return;
        // A capture folder the user pointed at the Desktop already holds the file
        // where "Keep on the Desktop" would put it; moving it beside itself would
        // leave a duplicate and take the card down for nothing.
        if (string.Equals(Path.GetDirectoryName(item.Path)?.TrimEnd('\\'),
                folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var target = Inbox.UniquePath(folder, Path.GetFileName(item.Path));
            File.Move(item.Path, target);
            Drop(id, quietly: true);
        }
        catch (Exception e)
        {
            Log.Error($"Could not move to {folder}: {e.Message}");
            System.Media.SystemSounds.Beep.Play();
        }
    }

    /// <summary>After editing, the photo on the line shows the new version.</summary>
    public void ReloadThumbnail(string path)
    {
        var item = _items.FirstOrDefault(i => !i.Falling && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        var thumb = Thumbnails.Load(path);
        if (thumb is null) return;
        item.Thumb = thumb.Value.Image;
        item.PixelWidth = thumb.Value.PixelWidth;
        item.PixelHeight = thumb.Value.PixelHeight;
        item.IsIcon = thumb.Value.IsIcon;
        ItemUpdated?.Invoke(item);
    }

    public void Reveal(Guid id)
    {
        var item = Find(id);
        if (item is not null) Shell.ShowInExplorer(item.Path);
    }

    /// <summary>Keeps a photo on the line: newer captures never push it off.</summary>
    public void TogglePin(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        item.Pinned = !item.Pinned;
        Save();
        ItemUpdated?.Invoke(item);
    }

    /// <summary>Reads the text in the screenshot and puts it on the clipboard.</summary>
    public async void CopyText(Guid id)
    {
        var item = Find(id);
        if (item is null) return;
        string? text;
        try { text = await Ocr.ReadAsync(item.Path); }
        catch (Exception e) { Log.Error($"OCR failed: {e.Message}"); text = null; }
        if (string.IsNullOrWhiteSpace(text))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        var data = new DataObject();
        data.SetData(ClipboardWatcher.TextCopyMarker, "1", false);
        data.SetData(DataFormats.UnicodeText, text, false);
        data.SetData(DataFormats.Text, text, false);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                ClipboardWatcher.RememberSelfCopy();
                break;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(attempt * 40);
            }
            catch (Exception e)
            {
                Log.Error($"Could not copy text: {e.Message}");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
        }
        MarkUsed(item);
        CopiedLabel = UI.Strings.TextCopied;
        CopiedId = id;
        Later(1.4, () => { if (CopiedId == id) CopiedId = null; });
    }

    private void MarkUsed(Pegged item)
    {
        item.Used = true;
        item.LastUsedAt = DateTime.Now;
        Save();
    }

    private List<Pegged> LiveItems(IEnumerable<Guid> ids) =>
        ids.Distinct().Select(Find).Where(i => i is not null && !i.Falling).Cast<Pegged>().ToList();

    private bool CopySelectedText(IEnumerable<Guid> ids, Func<List<Pegged>, string> text)
    {
        var items = LiveItems(ids);
        return items.Count > 0 && CopyPlainText(text(items), items);
    }

    private bool CopyPlainText(string text, IReadOnlyList<Pegged> items)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var data = new DataObject();
        data.SetData(ClipboardWatcher.TextCopyMarker, "1", false);
        data.SetData(DataFormats.UnicodeText, text, false);
        data.SetData(DataFormats.Text, text, false);
        if (!TrySetClipboard(data)) return false;
        ClipboardWatcher.RememberSelfCopy();
        foreach (var item in items)
        {
            item.Used = true;
            item.LastUsedAt = DateTime.Now;
        }
        Save();
        CopiedLabel = UI.Strings.TextCopied;
        CopiedId = items[0].Id;
        Later(1.4, () => { if (CopiedId == items[0].Id) CopiedId = null; });
        return true;
    }

    private static string FileUri(string path)
    {
        // Escape path data segment by segment so spaces, '#', '%' and non-ASCII
        // characters produce a valid Markdown-safe absolute file URI. Keep the
        // drive colon intact; Uri.EscapeDataString would turn it into %3A.
        string full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.StartsWith("//", StringComparison.Ordinal))
        {
            var unc = full.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (unc.Length >= 2)
            {
                string host = Uri.EscapeDataString(unc[0]);
                string tail = string.Join("/", unc.Skip(1).Select(Uri.EscapeDataString));
                return $"file://{host}/{tail}";
            }
        }

        var parts = full.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "file:///";
        string drive = parts[0];
        string tailPath = string.Join("/", parts.Skip(1).Select(Uri.EscapeDataString));
        return tailPath.Length == 0 ? $"file:///{drive}" : $"file:///{drive}/{tailPath}";
    }

    private static bool TrySetClipboard(DataObject data)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(attempt * 40);
            }
            catch (Exception e)
            {
                Log.Error($"Could not copy to the clipboard: {e.Message}");
                System.Media.SystemSounds.Beep.Play();
                return false;
            }
        }
    }

    // MARK: Breeze

    /// <summary>Every so often a little wind moves the line. It is what makes it feel like an object and not a widget.</summary>
    private void ScheduleGust()
    {
        Later(7 + _rng.NextDouble() * 9, () =>
        {
            if (_items.Count > 0 && DraggingId is null) Gust?.Invoke();
            ScheduleGust();
        });
    }

    // MARK: Persistence

    private void Save()
    {
        if (!_persist) return;
        if (_saveBatchDepth > 0 || _saveHoldDepth > 0)
        {
            _savePending = true;
            return;
        }
        SaveNow();
    }

    private void SaveNow()
    {
        if (!_persist) return;
        var live = _items.Where(i => !i.Falling).ToList();
        Settings.Current.Pegged = live.Select(i => i.Path).ToList();
        Settings.Current.PinnedPaths = live.Where(i => i.Pinned).Select(i => i.Path).ToList();
        Settings.Current.Spots = live.Where(i => i.Spot is not null).ToDictionary(i => i.Path, i => i.Spot!.Value, StringComparer.OrdinalIgnoreCase);
        Settings.Current.HangTimes = live.ToDictionary(i => i.Path, i => i.HungAt, StringComparer.OrdinalIgnoreCase);
        Settings.Current.LastUsedTimes = live.ToDictionary(i => i.Path, i => i.LastUsedAt, StringComparer.OrdinalIgnoreCase);
        Settings.Current.Save();
        _savePending = false;
    }

    private void BeginSaveBatch() => _saveBatchDepth++;

    private void EndSaveBatch()
    {
        if (_saveBatchDepth == 0) return;
        _saveBatchDepth--;
        if (_saveBatchDepth == 0 && _savePending && _saveHoldDepth == 0) SaveNow();
    }

    /// <summary>
    /// Holds disk writes while a burst of changes plays out, so a clear or a big
    /// drop does not rewrite settings.json once per card.
    /// </summary>
    public void HoldSave() => _saveHoldDepth++;

    /// <summary>Releases one hold; the last one writes once if anything changed.</summary>
    public void ReleaseSaveHold()
    {
        if (_saveHoldDepth == 0) return;
        _saveHoldDepth--;
        if (_saveHoldDepth == 0 && _saveBatchDepth == 0 && _savePending) SaveNow();
    }

    /// <summary>Writes any deferred line state before the application exits.</summary>
    public void FlushSave()
    {
        if (!_persist) return;
        SaveNow();
    }

    private void NotifyItemsChanged()
    {
        if (_notificationBatchDepth > 0)
        {
            _notificationPending = true;
            return;
        }
        ItemsChanged?.Invoke();
    }

    private void BeginNotificationBatch() => _notificationBatchDepth++;

    private void EndNotificationBatch()
    {
        if (_notificationBatchDepth == 0) return;
        _notificationBatchDepth--;
        if (_notificationBatchDepth != 0 || !_notificationPending) return;
        _notificationPending = false;
        ItemsChanged?.Invoke();
    }

    private void InvalidateOrder()
    {
        _orderedCache = null;
        _orderedSort = "";
    }

    private void Restore()
    {
        var paths = Settings.Current.Pegged.ToList();
        var fallback = DateTime.Now.AddMinutes(-paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (!Settings.Current.HangTimes.ContainsKey(path))
                Settings.Current.HangTimes[path] = fallback.AddMinutes(i);
        }
        BeginSaveBatch();
        BeginNotificationBatch();
        try
        {
            foreach (var path in paths)
                if (File.Exists(path) && Hang(path, quietly: true) is { } id && Find(id) is { } item)
                {
                    item.Used = true;
                    item.LastUsedAt = Settings.Current.LastUsedTimes.TryGetValue(path, out var usedAt) ? usedAt : item.HungAt;
                    item.Pinned = Settings.Current.PinnedPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
                    if (Settings.Current.Spots.TryGetValue(path, out var spot)) item.Spot = spot;
                }
        }
        finally
        {
            EndSaveBatch();
            EndNotificationBatch();
        }
    }

    private void Later(double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(Math.Max(0.001, seconds)) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }
}
