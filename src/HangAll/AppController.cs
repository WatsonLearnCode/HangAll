using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;
using HangAll.UI;
using static HangAll.Interop.Native;

namespace HangAll;

public sealed record TextClipboardState(
    bool Enabled,
    IReadOnlyList<TextClipboardEntry> Items);

/// <summary>
/// Runs the show: the tray icon, the shortcut, revealing and tucking away the
/// line, and the captures flying to it.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Line _line;
    private readonly LineWindow _panel;
    private readonly DropTargetWindow _drop;
    private readonly MessageWindow _messages = new();
    private readonly TrayIcon _tray;
    private readonly List<ScreenshotWatcher> _watchers = new();
    private readonly ClipboardWatcher _clipboard;
    private readonly TextClipboardHistory _textClipboardHistory;
    private readonly TextClipboardRecorder _textClipboardRecorder;
    private TextClipboardState _textClipboardState = new(false, Array.Empty<TextClipboardEntry>());
    private HotKey? _hotKey;
    private readonly DispatcherTimer _mouseTimer;
    private readonly DispatcherTimer _themeRefreshTimer;
    private readonly DispatcherTimer _autoDropTimer;
    private TimeSpan _mouseInterval;
    private QuickLookWindow? _quickLook;
    private NoteWindow? _notePreview;
    private (string Path, Guid Id)? _pendingNoteEdit;
    private bool _quickLookHotKeyRegistered;
    private PreviewTarget? _quickLookAttemptedTarget;
    private bool _disposing;

    /// <summary>
    /// The shortcut the app last tried to claim and whether Windows gave it up. A collision
    /// used to reach only the log, so the settings page showed a shortcut that did nothing.
    /// </summary>
    public static (string Shortcut, bool Claimed) LastHotKeyAttempt { get; private set; } = ("", true);
    public static event Action? HotKeyAttempted;

    private static readonly TimeSpan ActiveMouseInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly TimeSpan IdleMouseInterval = TimeSpan.FromSeconds(1.0 / 15);
    private static readonly TimeSpan OleDragIdleCheck = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FullScreenIdleCheck = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoDropInterval = TimeSpan.FromSeconds(15);
    private const int QuickLookHotKeyId = 0x4C51;
    private const uint QuickLookModifiers =
        MOD_NOREPEAT | (AppIdentity.IsTestBuild ? MOD_SHIFT : 0);
    /// <summary>Whether the panel is shown. It can be shown and still tucked away above the top edge.</summary>
    private bool _isPresent;
    /// <summary>Whether the line has slid down into view.</summary>
    private bool _isRevealed;
    /// <summary>Opened on purpose with the shortcut or the menu: it stays down until the pointer has visited it and left.</summary>
    private bool _pinned;
    /// <summary>A new screenshot shows itself for a moment, then tucks away.</summary>
    private DateTime _peekUntil = DateTime.MinValue;
    private DateTime? _hotZoneSince;
    private DateTime? _awaySince;
    /// <summary>Set when you open the line on purpose, so it stays up while empty.</summary>
    private bool _keepOpen;
    private int _lastLiveCount;
    /// <summary>The screen a new capture was taken on: the line goes there.</summary>
    private Display? _pendingMonitor;
    /// <summary>Captures that just hung from a file, so the clipboard copy of the same one is skipped.</summary>
    private readonly List<(DateTime when, int w, int h)> _recentFileCaptures = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _externalDropActive;

    /// <summary>A top-edge visit should reveal the line at most once. A click there also means work in the window, not a wish for the line.</summary>
    private bool _edgeSuppressed;
    private POINT _restingAt;
    private bool _previousButtonDown;
    private bool _oleDragActive;
    private DateTime _lastOleCheck = DateTime.MinValue;
    private DateTime _lastFullScreenCheck = DateTime.MinValue;
    private DateTime _lastTopmostRefresh = DateTime.MinValue;
    private bool _clearRunning;
    /// <summary>When the last context menu closed, so the click that chose an item does not also put the line away.</summary>
    private DateTime _menuClosedAt = DateTime.MinValue;
    /// <summary>The settings values the current session has already applied, so a change only refreshes what moved.</summary>
    private AppliedSettings? _applied;

    public TextClipboardState TextClipboard => Volatile.Read(ref _textClipboardState);
    public event Action<TextClipboardState>? TextClipboardChanged;

    public AppController()
    {
        OleInitialize(IntPtr.Zero);
        _line = new Line();
        Originals.Sweep(_line.Items.Select(i => i.Path));
        Theme.ApplySetting();
        Theme.Apply();
        Strings.Refresh();
        Typography.Apply();

        _panel = new LineWindow(_line);
        _panel.PlaceOn();
        _panel.Show();
        _panel.OrderOut();
        _panel.PlaceOn();
        UpdateCapacity();

        _drop = new DropTargetWindow(_panel);
        _drop.DropStateChanged += (active, hot) => _panel.SetExternalDrop(active, hot);
        _drop.FilesDropped += OnFilesDropped;
        _drop.Show();
        _drop.SyncDropTarget();

        Menus.OpenStateChanged += OnMenusOpenStateChanged;
        _tray = new TrayIcon { MenuProvider = BuildMenu };
        _tray.LeftClick += Toggle;
        _panel.Canvas.MenuRequested += () => Menus.Show(BuildMenu());
        _panel.ExternalDropChanged += OnExternalDropChanged;
        _panel.Canvas.ClearAnimationStarted += OnClearAnimationStarted;
        _panel.Canvas.ClearAnimationFinished += OnClearAnimationFinished;
        _panel.Canvas.SelectionChanged += UpdateQuickLookHotKey;
        _panel.Canvas.PreviewTargetChanged += UpdateQuickLookHotKey;
        _panel.Canvas.ClipboardCopyRequested += OnClipboardCopyRequested;
        _panel.Canvas.ClipboardCopyPlainRequested += OnClipboardCopyPlainRequested;
        _panel.Canvas.ClipboardDeleteRequested += OnClipboardDeleteRequested;
        _panel.Canvas.ClipboardPinRequested += OnClipboardPinRequested;
        _panel.Canvas.ClipboardClearRequested += OnClipboardClearRequested;
        _panel.Canvas.HoverFocusChanged += OnHoverFocusChanged;
        _panel.Canvas.SearchEditingChanged += OnSearchEditingChanged;
        _panel.Positioned += UpdateQuickLookHotKey;
        _line.StateChanged += UpdateQuickLookHotKey;
        _line.NoteEditRequested += OnNoteEditRequested;
        _messages.Message += OnMessage;

        _textClipboardHistory = new TextClipboardHistory();
        _textClipboardRecorder = new TextClipboardRecorder(
            _textClipboardHistory,
            () => Settings.Current.CatchTextClipboard);
        _textClipboardHistory.Changed += OnTextClipboardHistoryChanged;
        _clipboard = new ClipboardWatcher(
            _messages,
            OnClipboardCapture,
            files => OnFilesDropped(files),
            payload => _textClipboardRecorder.Capture(payload),
            RecentlyHungFromFile);
        SyncClipboardWatcher();
        PublishTextClipboardState();
        _clipboard.Start();
        StartWatchers();
        RegisterHotKey();

        _line.Fall += Fall;
        _line.ItemsChanged += ItemsChanged;
        _lastLiveCount = _line.LiveCount;

        _mouseTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ActiveMouseInterval };
        _mouseInterval = ActiveMouseInterval;
        _mouseTimer.Tick += (_, _) => Tick();

        _themeRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _themeRefreshTimer.Tick += (_, _) =>
        {
            _themeRefreshTimer.Stop();
            Theme.Refresh();
        };

        _autoDropTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoDropInterval };
        _autoDropTimer.Tick += (_, _) => AutoDropExpired();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (_line.LiveCount > 0)
        {
            Refresh();
        }

        if (!Settings.Current.Welcomed)
        {
            Settings.Current.Welcomed = true;
            Settings.Current.Save();
            Welcome();
        }

        // Keep the invisible top strip registered even while the line is empty,
        // so a file can be dragged onto it without first taking a screenshot.
        Refresh();
        _applied = Snapshot();
        AutoDropExpired();
        _autoDropTimer.Start();
        ScheduleUpdateCheck();
    }

    /// <summary>
    /// One quiet look for a newer release, ten seconds after start-up and at most
    /// once a day. Nothing is downloaded; the settings row shows the answer.
    /// </summary>
    private void ScheduleUpdateCheck()
    {
        if (!Updater.ShouldCheckInBackground()) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = Updater.CheckAsync();
        };
        timer.Start();
    }

    private void AutoDropExpired()
    {
        if (_line.AutoDropExpired(DateTime.Now).Count > 0)
            Log.Notice("Automatic take-down removed expired cards");
    }

    // MARK: Watching for captures

    private void StartWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var folders = new List<string> { Shell.ScreenshotsFolder(), Inbox.Folder };
        folders.AddRange(Settings.Current.WatchFolders.Select(Environment.ExpandEnvironmentVariables));
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new ScreenshotWatcher(folder,
                onNew: path => HangCapture(path, fromFile: true),
                onModified: path => _line.ReloadThumbnail(path),
                onChange: () => _line.Prune());
            watcher.Start();
            _watchers.Add(watcher);
            Log.Notice($"Watching {folder}");
        }
    }

    private void RegisterHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = new HotKey(_messages, Settings.Current.HotKey, Toggle);
        LastHotKeyAttempt = (Settings.Current.HotKey, _hotKey.Registered);
        if (!_hotKey.Registered) Log.Error($"Could not register the shortcut {Settings.Current.HotKey}");
        HotKeyAttempted?.Invoke();
    }

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_disposing || msg != WM_HOTKEY || wParam.ToInt32() != QuickLookHotKeyId) return;
        UnregisterQuickLookHotKey();
        var target = _panel.Canvas.QuickLookTarget;
        if (!CanOpenQuickLook(target))
            return;
        if (target.Kind == PreviewTargetKind.ClipboardWallet)
        {
            _panel.Canvas.TryToggleClipboardWallet();
            return;
        }
        if (target.Kind != PreviewTargetKind.Card || target.ItemId is not { } id ||
            _line.Find(id) is not { Falling: false } item)
            return;
        OpenQuickLook(item);
    }

    private bool CanOpenQuickLook(PreviewTarget target) =>
        !_disposing && _isPresent && _isRevealed && !_panel.Canvas.IsSearchEditing &&
        !_panel.Canvas.IsClipboardTextEditing &&
        !_line.MenuOpen && _pendingNoteEdit is null &&
        (target.Kind == PreviewTargetKind.ClipboardWallet && _panel.Canvas.IsClipboardWalletExpanded ||
         _panel.Frame.Contains(Displays.Cursor()));

    private bool IsCurrentNotePreviewTarget(Guid id) =>
        _notePreview is { } note &&
        _line.Find(id) is { Falling: false } item &&
        string.Equals(item.Path, note.NotePath, StringComparison.OrdinalIgnoreCase);

    private void UpdateQuickLookHotKey()
    {
        if (_disposing) return;
        if (Settings.QuickLookTriggerOption(Settings.Current.QuickLookTrigger) != "keyboard")
        {
            UnregisterQuickLookHotKey();
            return;
        }

        var target = _panel.Canvas.QuickLookTarget;
        bool validTarget = target.Kind switch
        {
            PreviewTargetKind.ClipboardWallet => true,
            PreviewTargetKind.Card when target.ItemId is { } id =>
                _line.Find(id) is { Falling: false } && !IsCurrentNotePreviewTarget(id),
            _ => false,
        };
        bool shouldRegister = _quickLook is null && validTarget && CanOpenQuickLook(target);
        if (!shouldRegister)
        {
            UnregisterQuickLookHotKey();
            return;
        }
        if (_quickLookHotKeyRegistered) return;

        if (_quickLookAttemptedTarget == target) return;
        _quickLookAttemptedTarget = target;
        var quickLookKeyText = Settings.QuickLookKeyOption(Settings.Current.QuickLookKey);
        if (!HotKey.Parse(quickLookKeyText, out var configuredModifiers, out var quickLookKey))
        {
            Log.Error($"Could not parse the Quick Look key {quickLookKeyText}");
            return;
        }
        _quickLookHotKeyRegistered = Native.RegisterHotKey(
            _messages.Handle, QuickLookHotKeyId, QuickLookModifiers | configuredModifiers, quickLookKey);
        if (!_quickLookHotKeyRegistered)
            Log.Error($"Could not register {quickLookKeyText} for Quick Look");
    }

    private void OnHoverFocusChanged(Guid? id)
    {
        UpdateQuickLookHotKey();
        if (_disposing || Settings.QuickLookTriggerOption(Settings.Current.QuickLookTrigger) != "auto" ||
            id is not { } targetId || _quickLook?.ItemId == targetId ||
            !CanOpenQuickLook(new PreviewTarget(PreviewTargetKind.Card, targetId)) ||
            _line.Find(targetId) is not { Falling: false } item)
            return;
        OpenQuickLook(item);
    }

    private void OnNoteEditRequested(string path, Guid id)
    {
        if (_disposing) return;
        var quickLook = _quickLook;
        _quickLook = null;
        quickLook?.Close();

        var request = (path, id);
        _pendingNoteEdit = request;
        _panel.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposing || _pendingNoteEdit != request) return;
            _pendingNoteEdit = null;
            OpenRequestedNoteEditor(path, id);
        }));
    }

    private void OpenRequestedNoteEditor(string path, Guid id)
    {
        CloseDifferentNotePreview(path);

        var display = _panel.Display ?? Displays.UnderPointer();
        if (_panel.CardScreenRect(id, includeReveal: false, stable: true) is not { } cardRect)
        {
            Log.Error($"Could not locate card '{id}' for note '{path}'");
            UpdateQuickLookHotKey();
            return;
        }

        var note = NoteWindow.ShowBelowCard(path, _line, display, cardRect);
        if (note is null)
        {
            Log.Error($"Could not open note '{path}' because its data is unavailable");
            UpdateQuickLookHotKey();
            return;
        }
        TrackNotePreview(note);
    }

    private void CloseDifferentNotePreview(string path)
    {
        if (_notePreview is { } current &&
            !string.Equals(current.NotePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _notePreview = null;
            current.Close();
        }
    }

    private void TrackNotePreview(NoteWindow? note)
    {
        if (note is null) return;
        if (!ReferenceEquals(_notePreview, note))
        {
            _notePreview = note;
            note.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_notePreview, note)) return;
                _notePreview = null;
                UpdateQuickLookHotKey();
            };
        }
        UpdateQuickLookHotKey();
    }

    private void OnSearchEditingChanged()
    {
        if (_panel.Canvas.IsSearchEditing)
        {
            _pendingNoteEdit = null;
            UnregisterQuickLookHotKey();
            var quickLook = _quickLook;
            _quickLook = null;
            quickLook?.Close();
            var notePreview = _notePreview;
            _notePreview = null;
            notePreview?.Close();
        }
        UpdateQuickLookHotKey();
    }

    private void UnregisterQuickLookHotKey()
    {
        if (_quickLookHotKeyRegistered)
            UnregisterHotKey(_messages.Handle, QuickLookHotKeyId);
        _quickLookHotKeyRegistered = false;
        _quickLookAttemptedTarget = null;
    }

    private void OpenQuickLook(Pegged item)
    {
        if (Notes.IsNote(item.Path))
        {
            // Notes use the editor instead of the image preview so their tasks
            // can be added and completed directly from Quick Look.
            _line.MarkUsed(item.Id);
            OnNoteEditRequested(item.Path, item.Id);
            return;
        }

        var display = _panel.Display ?? Displays.UnderPointer();
        if (_panel.CardScreenRect(item.Id, stable: true) is not { } cardRect)
        {
            Log.Error("Could not locate the selected card for Quick Look");
            UpdateQuickLookHotKey();
            return;
        }
        if (_notePreview is { } notePreview)
        {
            _notePreview = null;
            notePreview.Close();
        }
        if (_quickLook is { } current)
        {
            current.UpdatePreview(item, display, cardRect);
            _line.MarkUsed(item.Id);
            return;
        }

        var preview = new QuickLookWindow(item, display, cardRect);
        _quickLook = preview;
        preview.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_quickLook, preview)) return;
            _quickLook = null;
            UpdateQuickLookHotKey();
        };
        _line.MarkUsed(item.Id);
        try
        {
            preview.ShowPreview();
        }
        catch (Exception e)
        {
            Log.Error($"Could not open Quick Look: {e.Message}");
            _quickLook = null;
            preview.Close();
            UpdateQuickLookHotKey();
        }
    }

    private bool RecentlyHungFromFile(int w, int h)
    {
        var cutoff = DateTime.Now.AddSeconds(-4);
        _recentFileCaptures.RemoveAll(c => c.when < cutoff);
        return _recentFileCaptures.Any(c => c.w == w && c.h == h);
    }

    private void OnClipboardCapture(string path, int w, int h) => HangCapture(path, fromFile: false);

    private void SyncClipboardWatcher()
    {
        _clipboard.CatchImages = Settings.Current.CatchClipboard;
        _clipboard.CatchFiles = Settings.Current.ClipboardHangEnabled;
        _clipboard.CatchText = Settings.Current.CatchTextClipboard;
        _clipboard.Enabled = _clipboard.CatchImages || _clipboard.CatchFiles || _clipboard.CatchText;
    }

    private void OnTextClipboardHistoryChanged()
    {
        if (Volatile.Read(ref _disposing)) return;
        if (_panel.Dispatcher.CheckAccess())
        {
            PublishTextClipboardState();
            return;
        }
        _panel.Dispatcher.BeginInvoke(
            new Action(PublishTextClipboardState),
            DispatcherPriority.DataBind);
    }

    private void PublishTextClipboardState()
    {
        if (Volatile.Read(ref _disposing)) return;
        var state = new TextClipboardState(
            Settings.Current.CatchTextClipboard,
            _textClipboardHistory.GetSnapshot());
        Volatile.Write(ref _textClipboardState, state);
        _panel.Canvas.SetClipboardWalletVisible(state.Enabled);
        _panel.Canvas.SetClipboardItems(state.Items);
        TextClipboardChanged?.Invoke(state);
    }

    private void OnClipboardCopyRequested(TextClipboardEntry entry)
    {
        if (_disposing) return;
        TextClipboardHistory.CopyToClipboard(entry);
    }

    private void OnClipboardCopyPlainRequested(TextClipboardEntry entry)
    {
        if (_disposing) return;
        TextClipboardHistory.CopyPlainTextToClipboard(entry);
    }

    private void OnClipboardDeleteRequested(TextClipboardEntry entry)
    {
        if (_disposing) return;
        _textClipboardHistory.Remove(entry.Id);
    }

    private void OnClipboardPinRequested(TextClipboardEntry entry)
    {
        if (_disposing) return;
        _textClipboardHistory.TogglePin(entry.Id);
    }

    private void OnClipboardClearRequested() => ClearTextClipboardHistory();

    /// <summary>Empties the text history now and waits for the empty snapshot to reach disk.</summary>
    private void ClearTextClipboardHistory()
    {
        if (_disposing) return;
        int count = _textClipboardHistory.Count;
        _textClipboardHistory.Clear();
        _textClipboardHistory.Flush();
        Log.Notice($"Text clipboard history cleared ({count})");
    }

    private void OnExternalDropChanged(bool active)
    {
        _externalDropActive = active;
        if (active)
        {
            var display = Displays.Containing(Displays.Cursor());
            if (display is not null && (_panel.Display is null || !_panel.Display.Equals(display)))
            {
                _panel.PlaceOn(display);
                UpdateCapacity();
            }
            _keepOpen = true;
            Refresh();
            Reveal(pinned: true, reason: "file drop");
            return;
        }

        // A dropped file is hung by OnFilesDropped, which runs before this
        // callback. If nothing was accepted, return to the normal auto-hide.
        Later(0.12, () =>
        {
            if (_externalDropActive) return;
            _keepOpen = false;
            _pinned = false;
            if (_line.LiveCount == 0)
            {
                Refresh();
            }
        });
    }

    private void OnClearAnimationStarted()
    {
        _clearRunning = true;
        _keepOpen = true;
        _awaySince = null;
        Refresh();
        Reveal(pinned: true, reason: "one-click clear");
    }

    private void OnClearAnimationFinished()
    {
        _clearRunning = false;
        Later(0.12, () =>
        {
            if (_clearRunning) return;
            _keepOpen = false;
            _pinned = false;
            if (_line.LiveCount == 0)
            {
                Refresh();
            }
        });
    }

    private async void OnFilesDropped(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        Log.Notice($"Files dropped for hanging: {string.Join(" | ", files)}");
        try
        {
            var display = Displays.Containing(Displays.Cursor());
            if (display is not null) _panel.PlaceOn(display);
            UpdateCapacity();
            _keepOpen = true;
            Refresh();
            Reveal(pinned: true, reason: "file drop");

            var loaded = await Task.Run(() => files
                .Where(File.Exists)
                .Where(path => !Directory.Exists(path))
                .Select(path =>
                {
                    var thumbnail = Thumbnails.Load(path);
                    Log.Notice($"Drop thumbnail {(thumbnail is null ? "failed" : "ready")}: {path}");
                    return (path, thumbnail);
                })
                .Where(entry => entry.thumbnail is not null)
                .Select(entry => (entry.path, thumbnail: entry.thumbnail!.Value))
                .ToList());

            var ids = _line.HangMany(loaded, quietly: true);
            foreach (var (path, _) in loaded) Log.Notice($"Drop hang submitted: {path}");
            int hung = ids.Count;

            _keepOpen = false;
            if (hung > 0) Reveal(peekFor: 2.5, reason: "file drop");
            Refresh();
            Log.Notice($"Files dropped result: loaded={loaded.Count} hung={hung}");
        }
        catch (Exception e)
        {
            Log.Error($"Could not hang dropped files: {e}");
            _keepOpen = false;
            Refresh();
        }
    }

    // MARK: The capture flying to the line

    /// <summary>A new screenshot lifts off from where it was taken and flies to its place on the line.</summary>
    private async void HangCapture(string path, bool fromFile)
    {
        // Decoding a 4K capture costs around 100ms. Keep it off the UI thread,
        // and serialise captures so several files still hang in arrival order.
        await _captureGate.WaitAsync();
        try
        {
            if (_disposing) return;
            Thumbnail? thumb;
            if (Thumbnails.IsImage(path))
                thumb = await Task.Run(() => Thumbnails.Load(path));
            else
                thumb = Thumbnails.Load(path);
            if (_disposing || thumb is null) return;
            if (fromFile) _recentFileCaptures.Add((DateTime.Now, thumb.Value.PixelWidth, thumb.Value.PixelHeight));

            var from = CaptureRect.Infer(thumb.Value.PixelWidth, thumb.Value.PixelHeight);
            if (from is not null) _pendingMonitor = from.Value.monitor;
            var id = _line.Hang(path, flying: from is not null, preloaded: thumb);
            if (id is null || from is null) return;
            // Let the line come down and lay out before measuring the landing spot.
            Later(0.03, () => Fly(id.Value, from.Value.rect, from.Value.monitor));
        }
        catch (Exception e)
        {
            Log.Error($"Could not prepare capture: {e}");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private void Fly(Guid id, RECT from, Display monitor)
    {
        // The short pause that lets the line settle is an untracked timer, so the
        // app can already be on its way out by the time this runs.
        if (_disposing) return;
        var item = _line.Find(id);
        if (item is null) return;
        if (!_isPresent || !_isRevealed || _panel.Display is null || !_panel.Display.Equals(monitor) || CardFrame(id) is not { } to)
        {
            _line.Land(id);
            return;
        }
        int pixels = (int)Math.Max(from.Width, from.Height);
        var image = Thumbnails.Load(item.Path, Math.Min(3000, Math.Max(400, pixels)), 2)?.Image ?? item.Thumb;
        FlightWindow.Fly(item, image, from, to, item.Tilt, monitor, Seed(item), () => _line.Land(id));
    }

    /// <summary>A discarded card falls over the whole screen, from where it hangs.</summary>
    private void Fall(Pegged item)
    {
        if (!_isPresent || !_isRevealed || item.Flying || _panel.Display is null) return;
        if (CardFrame(item.Id) is not { } card) return;
        FlightWindow.Fall(item, card, item.Tilt, _panel.Display, Seed(item));
    }

    private static int Seed(Pegged item) => item.Path.Aggregate(17, (h, c) => h * 31 + c);

    /// <summary>Where a card hangs, in screen pixels, using the same layout as the canvas.</summary>
    private RECT? CardFrame(Guid id)
    {
        var card = _panel.Canvas.Cards.FirstOrDefault(c => c.Item.Id == id);
        if (card is null || _panel.Display is null) return null;
        var item = card.Item;
        var live = _line.Items.Where(i => !i.Falling).ToList();
        int index = live.IndexOf(item);
        if (index < 0) return null;
        double width = _panel.Canvas.ActualWidth;
        double x = card.CenterX > 0
            ? card.CenterX
            : card.TargetX > 0
                ? card.TargetX
                : width / 2;
        double viewTop = Layout.RopeY(x, width) - Layout.PinAbove;
        double cardTop = viewTop + Layout.CardOffsetBelowTop;
        var size = Layout.CardSize(item);
        var frame = _panel.Frame;
        double s = _panel.Display.Scale;
        int l = frame.Left + (int)Math.Round((x - size.Width / 2) * s);
        int t = frame.Top + (int)Math.Round(cardTop * s);
        return new RECT(l, t, l + (int)Math.Round(size.Width * s), t + (int)Math.Round(size.Height * s));
    }

    // MARK: Showing and hiding

    private void ItemsChanged()
    {
        if (_quickLook is { } preview &&
            _line.Find(preview.ItemId) is not { Falling: false })
        {
            _quickLook = null;
            preview.Close();
            UpdateQuickLookHotKey();
        }

        int live = _line.LiveCount;
        if (live > _lastLiveCount)
        {
            _panel.PlaceOn(_pendingMonitor);
            _pendingMonitor = null;
            UpdateCapacity();
            Refresh();
            Reveal(peekFor: 2.5, reason: "new capture");
        }
        else if (live == 0 && !_keepOpen)
        {
            Later(0.7, () =>
            {
                if (_line.LiveCount != 0 || _keepOpen) return;
                Refresh();
            });
        }
        _lastLiveCount = live;
    }

    /// <summary>Decides whether the panel is shown at all: something to show, and no full screen app on that screen.</summary>
    private void Refresh()
    {
        var monitor = _panel.Display ?? Displays.UnderPointer();
        bool blocked = FullScreen.IsActive(monitor);
        if (blocked) Dismiss(); else Present();
        // The pointer is also watched while the line is empty: holding a file
        // against the top edge must arm the invisible OLE drop surface.
        _mouseTimer.Start();
    }

    private void Present()
    {
        if (_isPresent) return;
        _isPresent = true;
        _panel.OrderFront();
        _lastTopmostRefresh = DateTime.Now;
        UpdateQuickLookHotKey();
    }

    private void Dismiss()
    {
        if (!_isPresent) return;
        _isPresent = false;
        Tuck("dismiss: full screen");
        Later(0.4, () => { if (!_isPresent) _panel.OrderOut(); });
    }

    /// <summary>Puts the line away and records why, so a stray tuck is traceable in the log.</summary>
    private void Tuck(string reason)
    {
        var mouse = Displays.Cursor();
        Log.Notice($"Tuck: {reason} (pointer {mouse.X},{mouse.Y}, menu={Menus.AnyOpen}, lineMenu={_line.MenuOpen})");
        SetRevealed(false);
    }

    private void Reveal(bool pinned = false, double peekFor = 0, string reason = "edge")
    {
        if (!_isPresent) return;
        if (!_isRevealed) Log.Notice($"Reveal: {reason}");
        if (pinned) _pinned = true;
        if (peekFor > 0) _peekUntil = DateTime.Now.AddSeconds(peekFor);
        _awaySince = null;
        SetRevealed(true);
    }

    private void SetRevealed(bool on)
    {
        if (!on) _panel.Canvas.CloseSearch();
        if (on == _isRevealed) return;
        _isRevealed = on;
        _panel.Canvas.Revealed = on;
        if (on)
        {
            _panel.OrderFront();
            _lastTopmostRefresh = DateTime.Now;
        }
        if (!on)
        {
            _pinned = false;
            _peekUntil = DateTime.MinValue;
            _panel.ClickThrough = true;
            _panel.Canvas.UpdateHover(null);
        }
        UpdateQuickLookHotKey();
    }

    public void Toggle()
    {
        if (_isRevealed)
        {
            Tuck("toggle");
            if (_line.LiveCount == 0)
            {
                _keepOpen = false;
                Refresh();
            }
        }
        else
        {
            _keepOpen = true;
            _panel.PlaceOn();
            UpdateCapacity();
            Refresh();
            Reveal(pinned: true, reason: "toggle");
        }
    }

    private void SetMouseRate(bool active)
    {
        var interval = active ? ActiveMouseInterval : IdleMouseInterval;
        if (_mouseInterval == interval) return;
        _mouseInterval = interval;
        _mouseTimer.Interval = interval;
    }

    private static bool ButtonDown() => (GetAsyncKeyState(0x01) < 0) || (GetAsyncKeyState(0x02) < 0) || (GetAsyncKeyState(0x04) < 0);

    /// <summary>
    /// Only the left button means "you are working in the window underneath".
    /// A right press belongs to our own cards or to the desktop's menu, and
    /// treating it as a click-through tore the line down before the context menu
    /// had even opened.
    /// </summary>
    private static bool LeftDown() => GetAsyncKeyState(0x01) < 0;

    private void Tick()
    {
        UpdateQuickLookHotKey();

        var mouse = Displays.Cursor();
        var now = DateTime.Now;
        var displayUnderPointer = Displays.Containing(mouse);
        bool nearDropTarget = displayUnderPointer is not null
            && mouse.Y <= displayUnderPointer.Work.Top +
            (int)Math.Ceiling((Layout.PanelHeight + 44) * displayUnderPointer.Scale);
        // IsOleDragActive walks every top-level window. The drop proxy can only
        // be reached near the top edge, so it is enough to look there each tick;
        // elsewhere a slower check notices a drag that ended out of reach.
        if (nearDropTarget || now - _lastOleCheck >= OleDragIdleCheck)
        {
            _oleDragActive = IsOleDragActive();
            _lastOleCheck = now;
        }

        SetMouseRate(
            _isRevealed || _externalDropActive || _oleDragActive || _clearRunning ||
            _line.DraggingId is not null || _line.PressedId is not null ||
            _panel.Canvas.RopeDragging || _line.MenuOpen || _panel.Canvas.IsSearchEditing);

        double revealDelay = Settings.RevealDelayOption(Settings.Current.RevealDelay);
        double revealHotBand = Settings.RevealHotBandDips(revealDelay);
        bool inHotBand = displayUnderPointer is not null
                         && displayUnderPointer.HotBandFor(revealHotBand).Contains(mouse);
        bool buttonDown = ButtonDown();
        bool justPressed = buttonDown && !_previousButtonDown;
        _previousButtonDown = buttonDown;
        if (!inHotBand) _edgeSuppressed = false;

        // While the line is tucked away an invisible window can wait a moment
        // before noticing a full-screen app; when it is down, check every tick.
        if (_isPresent && _panel.Display is { } current &&
            (_isRevealed || now - _lastFullScreenCheck >= FullScreenIdleCheck))
        {
            _lastFullScreenCheck = now;
            if (FullScreen.IsActive(current))
            {
                Dismiss();
                return;
            }
        }
        if (!_isPresent)
        {
            Refresh();
            if (!_isPresent) return;
        }

        if (_oleDragActive && nearDropTarget &&
            (_panel.Display is null || !_panel.Display.Equals(displayUnderPointer)))
        {
            _panel.PlaceOn(displayUnderPointer);
            UpdateCapacity();
        }

        // Keep the native proxy alive only while OLE is carrying a drag. The
        // DragEnter handler still uses CF_HDROP to reject links, text, and
        // internal card drags.
        _drop.Arm(_oleDragActive);

        // A context menu lives in its own topmost popup. Re-asserting the strip's
        // topmost position while one is open lifts the strip over the menu: the
        // menu keeps drawing (the strip is transparent there) but every click in
        // that area lands on the strip instead, so the lower items go dead about
        // a second after the menu opens.
        if (_isRevealed && _quickLook is null && !Menus.AnyOpen &&
            now - _lastTopmostRefresh >= TimeSpan.FromSeconds(1))
        {
            _panel.OrderFront();
            _lastTopmostRefresh = now;
        }

        if (!_isRevealed)
        {
            _panel.ClickThrough = true;
            // The top edge is where every maximised window keeps its tabs, so the
            // line only comes down when the pointer rests there, still, with no
            // button pressed. A click up there says you are working, not asking.
            if (buttonDown && inHotBand) _edgeSuppressed = true;
            bool resting = Settings.Current.RevealAtTopEdge && displayUnderPointer is not null && inHotBand && !buttonDown
                           && !_edgeSuppressed && !FullScreen.IsActive(displayUnderPointer);
            if (resting)
            {
                if (_hotZoneSince is null || Math.Abs(mouse.X - _restingAt.X) > 6 || Math.Abs(mouse.Y - _restingAt.Y) > 6)
                {
                    _hotZoneSince = now;
                    _restingAt = mouse;
                }
                if (now - _hotZoneSince.Value >= TimeSpan.FromSeconds(revealDelay))
                {
                    _hotZoneSince = null;
                    // Do not fire again until the pointer leaves the hot band.
                    _edgeSuppressed = true;
                    if (_panel.Display is null || !_panel.Display.Equals(displayUnderPointer))
                    {
                        _panel.PlaceOn(displayUnderPointer);
                        UpdateCapacity();
                    }
                    Refresh();
                    Reveal();
                }
            }
            else
            {
                _hotZoneSince = null;
            }
            return;
        }

        bool dragging = DragSource.IsDragging || _line.DraggingId is not null;
        bool holding = _line.PressedId is not null || _panel.Canvas.RopeDragging || _panel.Canvas.Cards.Any(c => c.Reordering);
        bool externalFileDrag = _oleDragActive && !DragSource.IsDragging;
        _panel.HoldMouse = dragging || holding;
        // OLE must keep reaching the native drop proxy even when the pointer is
        // over an opaque card. Internal card drags still belong to the line.
        // An open menu owns the mouse: the strip stays out of its way so a click
        // that is aimed at a menu item can never be swallowed by the strip.
        _panel.ClickThrough = externalFileDrag || Menus.AnyOpen ||
                              !(dragging || holding || _clearRunning || _panel.IsOverPhoto(mouse));
        var local = _panel.ToCanvas(mouse);
        _panel.Canvas.UpdateHover(dragging || externalFileDrag ? null : local);

        // A click that goes through the strip into the window underneath puts
        // the line away at once: you are working there. While the clipboard
        // history is open it is the window under the strip, so its own clicks
        // must not be mistaken for a click into the app beneath.
        bool leftPress = justPressed && LeftDown() &&
                         now - _menuClosedAt > TimeSpan.FromMilliseconds(350);
        if (leftPress && !dragging && !holding && !_clearRunning && !_line.MenuOpen && !Menus.AnyOpen &&
            !_panel.IsOverPhoto(mouse) && _notePreview is null && _pendingNoteEdit is null &&
            !_panel.Canvas.IsClipboardWalletExpanded)
        {
            _edgeSuppressed = inHotBand;
            Tuck("click through");
            return;
        }

        // The line's zone runs from its lowest point up to the top of the screen, so moving up never hides it.
        var frame = _panel.Frame;
        var zone = frame;
        if (_panel.Display is { } m) zone.Top = m.Bounds.Top;
        bool inside = zone.Contains(mouse);
        if (inside && _pinned) _pinned = false;

        bool pendingHold = Settings.Current.StayDownWhileUnused && _line.HasUnused;
        // A menu hangs below the strip, so the pointer leaves the line's zone as
        // soon as you reach its lower items. Menus.AnyOpen is checked as well as
        // the flag it raises, so a menu that opened on this very tick still holds.
        bool busy = _clearRunning || _pinned || dragging || holding || _line.MenuOpen || Menus.AnyOpen ||
                    _externalDropActive || _quickLook is not null || _notePreview is not null ||
                    _pendingNoteEdit is not null ||
                    _panel.Canvas.IsSearchEditing || _panel.Canvas.IsClipboardWalletExpanded ||
                    _panel.Canvas.IsUndoShowing ||
                    now < _peekUntil || pendingHold;
        if (inside || busy)
        {
            _awaySince = null;
        }
        else
        {
            _awaySince ??= now;
            if (now - _awaySince.Value >= TimeSpan.FromSeconds(Settings.Current.RetractDelay))
            {
                _awaySince = null;
                Tuck("idle away");
            }
        }
    }

    private void UpdateCapacity()
    {
        double width = _panel.Canvas.Width;
        if (double.IsNaN(width) || width <= 0) width = _panel.ActualWidth;
        // Not measured yet: leave the restored line untouched until we know the width.
        if (width <= 0) return;
        int fit = Layout.FitCapacity(width);
        // null keeps the line to what the screen holds; a negative value is
        // "no limit" and never trims; a positive value is an exact cap.
        _line.MaxItems = Settings.Current.MaxItems is { } max
            ? max < 0 ? -1 : max
            : fit;
        _line.TrimToCapacity();
        _panel.Canvas.RefreshOrder();
    }

    // MARK: Welcome

    private void Welcome()
    {
        _keepOpen = true;
        Refresh();
        Reveal(pinned: true, reason: "welcome");
        _tray.Balloon(Strings.WelcomeTitle, string.Format(Strings.WelcomeBody, Settings.Current.HotKey));
        Later(6, () =>
        {
            if (_line.LiveCount != 0) return;
            _keepOpen = false;
            Refresh();
        });
    }

    /// <summary>
    /// Settings changed. Only the groups whose values actually moved are refreshed,
    /// so nudging one option no longer rescans folders, re-registers the shortcut or
    /// rebuilds the whole line.
    /// </summary>
    public void ApplySettings()
    {
        var next = Snapshot();
        var prev = _applied;
        _applied = next;

        if (prev is null || prev.Value.Appearance != next.Appearance)
        {
            _themeRefreshTimer.Stop();
            Theme.ApplySetting();
        }

        if (prev is null || prev.Value.Language != next.Language || Math.Abs(prev.Value.FontScale - next.FontScale) > 0.001)
        {
            Strings.Refresh();
            ApplyTypography();
            _tray.Repaint();
        }

        bool clipboardDistanceChanged = prev is not null &&
            Math.Abs(prev.Value.ClipboardEdgeDistance - next.ClipboardEdgeDistance) > 0.001;
        if (prev is null || prev.Value.RopeColor != next.RopeColor || prev.Value.PegStyle != next.PegStyle ||
            prev.Value.LineOffset != next.LineOffset || prev.Value.LineOpacity != next.LineOpacity ||
            prev.Value.OneClickClear != next.OneClickClear || prev.Value.ClearAnimationPath != next.ClearAnimationPath ||
            Math.Abs(prev.Value.ClearAnimationSize - next.ClearAnimationSize) > 0.001 ||
            prev.Value.ClearAnimationSide != next.ClearAnimationSide ||
            Math.Abs(prev.Value.ClearAnimationOffset - next.ClearAnimationOffset) > 0.001 ||
            prev.Value.ClearAnimationMirrored != next.ClearAnimationMirrored ||
            clipboardDistanceChanged)
        {
            Pegs.RaiseLookChanged();
            _panel.ApplyOpacity();
            _panel.Canvas.RefreshClearAnimation();
            if (_panel.Display is { } d) _panel.PlaceOn(d);
            if (clipboardDistanceChanged) _panel.Canvas.RelayoutNow();
        }

        if (prev is null || prev.Value.MaxItems != next.MaxItems || prev.Value.CardSort != next.CardSort)
            UpdateCapacity();

        if (prev is null || prev.Value.CatchClipboard != next.CatchClipboard ||
            prev.Value.CatchTextClipboard != next.CatchTextClipboard ||
            prev.Value.UniversalDrop != next.UniversalDrop || prev.Value.DropZoneHang != next.DropZoneHang ||
            prev.Value.ClipboardAutoHang != next.ClipboardAutoHang)
        {
            SyncClipboardWatcher();
            _drop.SyncDropTarget();
        }
        if (prev is not null && prev.Value.CatchTextClipboard != next.CatchTextClipboard)
            PublishTextClipboardState();

        if (prev is null || prev.Value.HotKey != next.HotKey) RegisterHotKey();
        bool quickLookTriggerChanged = prev is null || prev.Value.QuickLookTrigger != next.QuickLookTrigger;
        bool quickLookKeyChanged = prev is null || prev.Value.QuickLookKey != next.QuickLookKey;
        if (quickLookTriggerChanged || quickLookKeyChanged)
        {
            if (quickLookTriggerChanged)
            {
                var quickLook = _quickLook;
                _quickLook = null;
                quickLook?.Close();
            }
            UnregisterQuickLookHotKey();
            UpdateQuickLookHotKey();
        }
        if (prev is not null && Math.Abs(prev.Value.QuickLookScale - next.QuickLookScale) > 0.001 &&
            _quickLook is { } preview &&
            _line.Find(preview.ItemId) is { Falling: false } previewItem &&
            (_panel.Display ?? Displays.UnderPointer()) is { } previewDisplay &&
            _panel.CardScreenRect(previewItem.Id, stable: true) is { } previewCardRect)
        {
            preview.UpdatePreview(previewItem, previewDisplay, previewCardRect);
        }
        if (prev is null || prev.Value.Watch != next.Watch) StartWatchers();
    }

    private AppliedSettings Snapshot()
    {
        var s = Settings.Current;
        return new AppliedSettings(
            s.Appearance, s.Language, s.FontScale, s.RopeColor, s.PegStyle, s.LineOffset, s.LineOpacity,
            s.OneClickClear, s.ClearAnimationPath, s.ClearAnimationSize,
            Settings.ClearAnimationSideOption(s.ClearAnimationSide),
            Settings.ClearAnimationOffsetOption(s.ClearAnimationOffset),
            s.ClearAnimationMirrored,
            Settings.ClipboardEdgeDistanceOption(s.ClipboardEdgeDistance),
            s.MaxItems, s.CardSort, s.CatchClipboard, s.CatchTextClipboard,
            s.UniversalDrop, s.DropZoneHang, s.ClipboardAutoHang,
            s.HotKey, WatchKey(), s.RevealDelay, s.RevealAtTopEdge,
            Settings.QuickLookTriggerOption(s.QuickLookTrigger),
            Settings.QuickLookKeyOption(s.QuickLookKey),
            Settings.QuickLookScaleOption(s.QuickLookScale));
    }

    private static string WatchKey()
    {
        var folders = new List<string> { Shell.ScreenshotsFolder(), Inbox.Folder };
        folders.AddRange(Settings.Current.WatchFolders.Select(Environment.ExpandEnvironmentVariables));
        return string.Join("\n", folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private readonly record struct AppliedSettings(
        string Appearance, string Language, double FontScale, string RopeColor, string PegStyle,
        double LineOffset, double LineOpacity, bool OneClickClear, string? ClearAnimationPath, double ClearAnimationSize,
        string ClearAnimationSide, double ClearAnimationOffset, bool ClearAnimationMirrored, double ClipboardEdgeDistance,
        int? MaxItems, string CardSort, bool CatchClipboard, bool CatchTextClipboard,
        bool UniversalDrop, bool DropZoneHang, bool ClipboardAutoHang, string HotKey, string Watch,
        double RevealDelay, bool RevealAtTopEdge,
        string QuickLookTrigger, string QuickLookKey, double QuickLookScale);

    /// <summary>Apply the selected type scale everywhere a window is already open.</summary>
    private void ApplyTypography()
    {
        Typography.Apply();
        SettingsWindow.RefreshTypography();
        MarkupWindow.RefreshTypography();
        NoteWindow.RefreshTypography();
        AboutWindow.RefreshTypography();
        _panel.Canvas.RefreshTypography();
    }

    // MARK: Menu

    private void OnMenusOpenStateChanged()
    {
        bool open = Menus.AnyOpen;
        if (!open) _menuClosedAt = DateTime.Now;
        _line.MenuOpen = open;
    }

    private ContextMenu BuildMenu()
    {
        _panel.Canvas.CloseSearch();
        var menu = new ContextMenu { FlowDirection = Strings.Flow };
        menu.Opened += (_, _) =>
        {
            var notePreview = _notePreview;
            _notePreview = null;
            notePreview?.Close();
        };

        var showLine = PeggedControl.MenuItemFor(_isRevealed ? Strings.HideLine : Strings.ShowLine, Toggle, Settings.Current.HotKey);
        showLine.Icon = MenuGlyphs.Create(MenuGlyph.Line);
        menu.Items.Add(showLine);

        var clear = PeggedControl.MenuItemFor(Strings.TakeEverythingDown, () => _line.Clear());
        clear.Icon = MenuGlyphs.Create(MenuGlyph.TakeDown);
        clear.IsEnabled = _line.LiveCount > 0;
        menu.Items.Add(clear);

        // A second, always-reachable entry to the text history, beside the
        // clipboard window's own footer button.
        var clearText = PeggedControl.MenuItemFor(Strings.ClipboardWalletClear, ClearTextClipboardHistory);
        clearText.Icon = MenuGlyphs.Create(MenuGlyph.Clipboard);
        clearText.IsEnabled = _textClipboardHistory.Count > 0;
        menu.Items.Add(clearText);
        menu.Items.Add(new Separator());

        var newCapture = PeggedControl.MenuItemFor(Strings.NewCapture, Shell.Snip, "Win+Shift+S");
        newCapture.Icon = MenuGlyphs.Create(MenuGlyph.Capture);
        menu.Items.Add(newCapture);

        var note = PeggedControl.MenuItemFor(Strings.NewNote, () => { });
        note.Icon = MenuGlyphs.Create(MenuGlyph.Note);
        Menus.AttachPanel(menu, note, () =>
            NoteColourPanel(Strings.NewNote, CreateNote));
        menu.Items.Add(note);

        var quick = PeggedControl.MenuItemFor(Strings.QuickSettings, () => { });
        quick.Icon = MenuGlyphs.Create(MenuGlyph.QuickSettings);
        Menus.AttachPanel(menu, quick, BuildQuickSettingsPanel);
        menu.Items.Add(quick);
        menu.Items.Add(new Separator());

        var screenshots = PeggedControl.MenuItemFor(Strings.OpenScreenshotsFolder, () => Shell.OpenFolder(Shell.ScreenshotsFolder()));
        screenshots.Icon = MenuGlyphs.Create(MenuGlyph.FolderImages);
        menu.Items.Add(screenshots);

        var inbox = PeggedControl.MenuItemFor(Strings.OpenInboxFolder, () => Shell.OpenFolder(Inbox.Folder));
        inbox.Icon = MenuGlyphs.Create(MenuGlyph.FolderInbox);
        menu.Items.Add(inbox);
        menu.Items.Add(new Separator());

        var settings = PeggedControl.MenuItemFor(Strings.Settings, () => SettingsWindow.Open(ApplySettings));
        settings.Icon = MenuGlyphs.Create(MenuGlyph.Settings);
        menu.Items.Add(settings);

        var about = PeggedControl.MenuItemFor(Strings.About, ShowAbout);
        about.Icon = MenuGlyphs.Create(MenuGlyph.About);
        menu.Items.Add(about);
        menu.Items.Add(new Separator());

        var quit = PeggedControl.MenuItemFor(Strings.Quit, () => Application.Current.Shutdown());
        quit.Icon = MenuGlyphs.Create(MenuGlyph.Quit);
        menu.Items.Add(quit);
        return menu;
    }

    private static MenuSidePanel NoteColourPanel(string title, Action<string> select)
    {
        var options = Notes.Colors.Select(key =>
        {
            string colour = key;
            return new MenuSidePanel.ColorOption(
                Strings.NoteName(colour),
                Notes.Paper(colour),
                () => select(colour));
        });
        return MenuSidePanel.ForColors(title, options);
    }

    private MenuSidePanel BuildQuickSettingsPanel()
    {
        return MenuSidePanel.ForToggles(Strings.QuickSettings, new[]
        {
            new MenuSidePanel.ToggleOption(Strings.CatchClipboard,
                () => Settings.Current.CatchClipboard, () => true, ToggleCaptureClipboard),
            new MenuSidePanel.ToggleOption(Strings.UniversalDrop,
                () => Settings.Current.UniversalDrop, () => true, ToggleUniversalDrop),
            new MenuSidePanel.ToggleOption(Strings.DropZoneHang,
                () => Settings.Current.DropZoneHang, () => Settings.Current.UniversalDrop, ToggleDropZoneHang),
            new MenuSidePanel.ToggleOption(Strings.ClipboardAutoHang,
                () => Settings.Current.ClipboardAutoHang, () => Settings.Current.UniversalDrop, ToggleClipboardAutoHang),
            new MenuSidePanel.ToggleOption(Strings.StayDownWhileUnused,
                () => Settings.Current.StayDownWhileUnused, () => true, ToggleStayDownWhileUnused),
            new MenuSidePanel.ToggleOption(Strings.TakeDownAfterDrag,
                () => Settings.Current.TakeDownAfterDrag, () => true, ToggleTakeDownAfterDrag),
            new MenuSidePanel.ToggleOption(Strings.Sounds,
                () => _line.SoundOn, () => true, ToggleSound),
            new MenuSidePanel.ToggleOption(Strings.StartWithWindows,
                () => Shell.StartsWithWindows, () => true, ToggleStartWithWindows),
        });
    }

    private void CreateNote(string colour)
    {
        _keepOpen = true;
        _panel.PlaceOn();
        UpdateCapacity();
        Refresh();
        _line.NewNote(colour);
        Reveal(pinned: true, reason: "note");
    }

    private void ToggleCaptureClipboard()
    {
        Settings.Current.CatchClipboard = !Settings.Current.CatchClipboard;
        Settings.Current.Save();
        SyncClipboardWatcher();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleUniversalDrop()
    {
        Settings.Current.UniversalDrop = !Settings.Current.UniversalDrop;
        Settings.Current.Save();
        _drop.SyncDropTarget();
        SyncClipboardWatcher();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleDropZoneHang()
    {
        Settings.Current.DropZoneHang = !Settings.Current.DropZoneHang;
        Settings.Current.Save();
        _drop.SyncDropTarget();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleClipboardAutoHang()
    {
        Settings.Current.ClipboardAutoHang = !Settings.Current.ClipboardAutoHang;
        Settings.Current.Save();
        SyncClipboardWatcher();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleStayDownWhileUnused()
    {
        Settings.Current.StayDownWhileUnused = !Settings.Current.StayDownWhileUnused;
        Settings.Current.Save();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleTakeDownAfterDrag()
    {
        Settings.Current.TakeDownAfterDrag = !Settings.Current.TakeDownAfterDrag;
        Settings.Current.Save();
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleSound()
    {
        _line.SoundOn = !_line.SoundOn;
        SettingsWindow.SyncFromSettings();
    }

    private void ToggleStartWithWindows()
    {
        try { Shell.StartsWithWindows = !Shell.StartsWithWindows; }
        catch (Exception e) { Log.Error($"Could not change the startup setting: {e.Message}"); }
        SettingsWindow.SyncFromSettings();
    }

    private void ShowAbout()
        => AboutWindow.Open();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _panel.Dispatcher.BeginInvoke(() =>
        {
            _panel.PlaceOn(_panel.Display is { } m && Displays.All().Contains(m) ? m : null);
            UpdateCapacity();
            if (_notePreview is { } notePreview &&
                _line.Items.FirstOrDefault(item =>
                    !item.Falling &&
                    string.Equals(item.Path, notePreview.NotePath, StringComparison.OrdinalIgnoreCase)) is { } item &&
                _panel.CardScreenRect(item.Id, includeReveal: false, stable: true) is { } cardRect)
            {
                notePreview.PlaceBelowCard(_panel.Display ?? Displays.UnderPointer(), cardRect);
            }
        });
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        _panel.Dispatcher.BeginInvoke(() =>
        {
            if (!_themeRefreshTimer.IsEnabled) _themeRefreshTimer.Start();
        });
    }

    private static void Later(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.001, seconds)) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    public void Dispose()
    {
        _disposing = true;
        _pendingNoteEdit = null;
        _mouseTimer.Stop();
        _themeRefreshTimer.Stop();
        _autoDropTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _messages.Message -= OnMessage;
        _panel.Canvas.SelectionChanged -= UpdateQuickLookHotKey;
        _panel.Canvas.PreviewTargetChanged -= UpdateQuickLookHotKey;
        _panel.Canvas.ClipboardCopyRequested -= OnClipboardCopyRequested;
        _panel.Canvas.ClipboardCopyPlainRequested -= OnClipboardCopyPlainRequested;
        _panel.Canvas.ClipboardDeleteRequested -= OnClipboardDeleteRequested;
        _panel.Canvas.ClipboardPinRequested -= OnClipboardPinRequested;
        _panel.Canvas.ClipboardClearRequested -= OnClipboardClearRequested;
        _panel.Canvas.HoverFocusChanged -= OnHoverFocusChanged;
        _panel.Canvas.SearchEditingChanged -= OnSearchEditingChanged;
        _panel.Positioned -= UpdateQuickLookHotKey;
        _line.StateChanged -= UpdateQuickLookHotKey;
        _line.NoteEditRequested -= OnNoteEditRequested;
        Menus.CloseAll();
        Menus.OpenStateChanged -= OnMenusOpenStateChanged;
        _line.MenuOpen = false;
        UnregisterQuickLookHotKey();
        var quickLook = _quickLook;
        _quickLook = null;
        quickLook?.Close();
        var notePreview = _notePreview;
        _notePreview = null;
        notePreview?.Close();
        _line.FlushSave();
        foreach (var w in _watchers) w.Dispose();
        _textClipboardHistory.Changed -= OnTextClipboardHistoryChanged;
        _clipboard.Dispose();
        _textClipboardHistory.Flush();
        _hotKey?.Dispose();
        _drop.Close();
        _panel.Close();
        _tray.Dispose();
        _messages.Dispose();
    }
}
