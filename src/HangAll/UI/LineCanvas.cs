using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HangAll.Core;
using Line = HangAll.Core.Line;

namespace HangAll.UI;

internal enum PreviewTargetKind
{
    None,
    Card,
    ClipboardWallet,
}

internal readonly record struct PreviewTarget(PreviewTargetKind Kind, Guid? ItemId = null);

/// <summary>The line and where each photo hangs. Drives every animation from one clock.</summary>
public sealed class LineCanvas : Canvas
{
    private const int RestingZBase = 100_000;
    private const int HoverZBase = 500_000;
    private const int TagZ = 700_000;
    private const int ClipboardWalletZ = 600_000;
    private const int SearchZ = 750_000;
    private const int HeldZ = 1_000_000;
    private const int ClearRunnerZ = 1_050_000;
    private const int QuickNoteZ = 1_060_000;
    private const int ClearButtonZ = 1_100_000;
    private const double SearchHeight = 30;
    private const double SearchCollapsedWidth = 96;
    private const double SearchExpandedWidth = 320;
    private const double SearchRadius = 9;
    private const double SearchGap = 8;
    private const double SearchTextInset = 12;
    private const double QuickNoteHostWidth = 44;
    private const double QuickNoteHostHeight = 40;
    private const double QuickNoteChipWidth = 30;
    private const double QuickNoteChipHeight = 22;
    private const double QuickNoteLeft = 8;

    private readonly Line _line;
    private readonly List<PeggedControl> _cards = new();
    private readonly HashSet<Guid> _selectedIds = new();
    private Guid? _primarySelectedId;
    private Guid? _selectionAnchor;
    private Guid? _hoverFocusId;
    private Guid? _hoverCandidateId;
    private long _hoverCandidateSince;
    private static readonly TimeSpan HoverFocusDelay = TimeSpan.FromMilliseconds(250);
    private readonly System.Windows.Threading.DispatcherTimer _hoverFocusTimer =
        new() { Interval = HoverFocusDelay };
    // The idle button's GIF runs on its own timer, at the GIF's own frame rate,
    // so an idle line does not have to keep the render clock alive.
    private readonly System.Windows.Threading.DispatcherTimer _clearButtonTimer =
        new(System.Windows.Threading.DispatcherPriority.Render) { IsEnabled = false };
    private readonly List<PeggedControl> _hitCards = new();
    private readonly List<Rect> _hitRects = new();
    private readonly Grid _rope;
    private readonly Path _ropeHalo, _ropeShadow, _ropeCore, _ropeHighlight;
    private readonly Grid _tag;
    private Border _tagChip = null!;
    private System.Windows.Shapes.Path _tagGlyph = null!;
    private Grid _tagPeg = null!;
    private PeggedControl? _held;
    private bool _ropeDrag;
    private readonly Border _hint;
    private readonly Tween _hintOpacity = new(0);
    private Border _undo = null!;
    private Grid _undoHost = null!;
    private TextBlock _undoCount = null!;
    private TextBlock _undoAction = null!;
    private readonly Tween _undoOpacity = new(0);
    private IReadOnlyList<Line.SweptCard> _pendingSweep = Array.Empty<Line.SweptCard>();
    // The animated runner drops cards itself, so it keeps its own undo batch and
    // offers it only once the runner is home again - mid-sweep the pill would
    // land under the animation.
    private IReadOnlyList<Line.SweptCard> _clearSweep = Array.Empty<Line.SweptCard>();
    private static readonly TimeSpan UndoLifetime = TimeSpan.FromSeconds(5);
    private readonly System.Windows.Threading.DispatcherTimer _undoTimer =
        new() { Interval = UndoLifetime };
    private readonly Grid _dropOverlay;
    private readonly Path _dropBand;
    private readonly Border _dropCard;
    private readonly Rectangle _dropOutline;
    private readonly TextBlock _dropTitle;
    private readonly TextBlock _dropHint;
    private readonly Tween _dropOpacity = new(0);
    private Rect? _dropZone;
    private bool _dropZoneVisible;
    private bool _dropZoneHot;
    private readonly Border _clearButton;
    private readonly Image _clearButtonImage;
    private readonly ScaleTransform _clearButtonScale = new();
    private readonly Border _clearRunner;
    private readonly Image _clearRunnerImage;
    private readonly ScaleTransform _clearRunnerScale = new();
    private Grid _quickNote = null!;
    private Border _quickNoteChip = null!;
    private Path _quickNoteGlyph = null!;
    private Grid _quickNotePeg = null!;
    private bool _quickNoteHot;
    private readonly ClipboardWallet _clipboardWallet;
    private bool _clipboardWalletVisible;
    private ClearAnimation _clearAnimation;
    private int _clearFrame;
    private double _clearFrameAge;
    private int _clearButtonFrame;
    private bool _clearRunning;
    /// <summary>Set when the caller steps the animation itself instead of a dispatcher timer.</summary>
    private bool _clearButtonStill;
    private double _clearIdleAge;
    private readonly List<(Guid id, double x, bool dropped)> _clearTargets = new();
    private double _clearStartX;
    private double _clearEndX;
    private double _clearHomeX;
    private double _clearElapsed;
    private double _clearDuration;
    private bool _clearReturning;
    private bool _clearLeftToRight;
    private readonly TranslateTransform _reveal = new();
    // A touch under critically damped: the line lands with a 3-4 pt dip and is
    // still settled in well under half a second. The strip has ~20 pt of room
    // below the lowest card, so the dip can never clip one.
    private readonly Spring _revealSpring = Spring.FromResponse(0.40, 0.79, -(Layout.PanelHeight + 12));
    private readonly Tween _hideTween = new(-(Layout.PanelHeight + 12));
    private bool _revealed;
    private bool _usingSpring;
    private readonly Stopwatch _clock = new();
    private double _lastTime;
    private bool _ticking;
    private double _width;
    private string _filter = "";
    private bool _isFiltering;
    private Border _search = null!;
    private Grid _searchCollapsed = null!;
    private Grid _searchExpanded = null!;
    private Border _searchCollapsedHover = null!;
    private Border _searchFocusHalo = null!;
    private TextBox _searchInput = null!;
    private TextBlock _searchPlaceholder = null!;
    private TextBlock _searchCollapsedLabel = null!;
    private TextBlock _searchActionLabel = null!;
    private Path _searchCollapsedGlyph = null!;
    private Path _searchActionGlyph = null!;
    private Border _searchAction = null!;
    private Border _searchActionWash = null!;
    private readonly Tween _searchWidth = new(SearchCollapsedWidth);
    private bool _isSearchEditing;

    public LineCanvas(Line line)
    {
        _line = line;
        Background = Brushes.Transparent;
        ClipToBounds = false;
        Height = Layout.PanelHeight;
        RenderTransform = _reveal;
        _reveal.Y = -(Layout.PanelHeight + 12);

        // A thin, neutral line: a mid gray core with a faint highlight and a soft
        // shadow, so it reads on light and dark backgrounds alike. It fades out at
        // both ends so it seems to come from beyond the screen.
        // Three thin strokes stand in for a blur: a full-width BlurEffect is
        // re-rasterised on every window update, which made the swinging cards
        // cost far more CPU than the rope itself.
        _ropeHalo = new Path { Stroke = Theme.Freeze(new SolidColorBrush(Theme.Gray(0, 0.05))), StrokeThickness = 6, RenderTransform = new TranslateTransform(0, 1.4) };
        _ropeShadow = new Path { Stroke = Theme.Freeze(new SolidColorBrush(Theme.Gray(0, 0.12))), StrokeThickness = 3, RenderTransform = new TranslateTransform(0, 1.2) };
        _ropeCore = new Path { StrokeThickness = 1.6 };
        _ropeHighlight = new Path { StrokeThickness = 0.5, RenderTransform = new TranslateTransform(0, -0.45) };
        PaintRope();
        _rope = new Grid { IsHitTestVisible = false };
        _rope.Children.Add(_ropeHalo);
        _rope.Children.Add(_ropeShadow);
        _rope.Children.Add(_ropeCore);
        _rope.Children.Add(_ropeHighlight);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();
        _rope.OpacityMask = mask;
        Children.Add(_rope);
        SetZIndex(_rope, 0);

        _search = BuildSearchControl();
        Children.Add(_search);
        SetZIndex(_search, SearchZ);

        var hintText = new TextBlock
        {
            Text = Strings.Hint,
            FontFamily = Typography.Ui, FontSize = Typography.SmallSize, FontWeight = FontWeights.Medium,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Secondary)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.ApplyText(hintText);
        Typography.BlockLineHeight(hintText, Typography.SmallLineHeight);
        _hint = Glass.Surface(Layout.FrameRadius, 0.15);
        _hint.Child = hintText;
        _hint.Padding = new Thickness(14, 7, 14, 7);
        _hint.Opacity = 0;
        _hint.Cursor = System.Windows.Input.Cursors.Hand;
        _hint.ToolTip = Strings.NewCaptureTip;
        // The hint is a button too: click it to start a snip.
        _hint.MouseLeftButtonUp += (_, e) => { e.Handled = true; HangAll.Interop.Shell.Snip(); };
        Children.Add(_hint);
        SetZIndex(_hint, 1);

        // A sweep leaves one way back for a few seconds, on a pill of the same family
        // as the hint: taking everything down should never be the last word.
        _undoCount = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
        };
        _undoAction = new TextBlock
        {
            Text = Strings.Undo,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        _undoAction.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
        var undoFace = new StackPanel { Orientation = Orientation.Horizontal };
        undoFace.Children.Add(_undoCount);
        undoFace.Children.Add(_undoAction);
        _undo = Glass.Surface(Layout.FrameRadius, 0.15);
        Glass.PaintPanel(_undo);
        _undo.Child = undoFace;
        _undo.Padding = new Thickness(14, 7, 14, 7);
        _undo.Opacity = 0;
        _undo.IsHitTestVisible = false;
        _undo.Cursor = System.Windows.Input.Cursors.Hand;
        _undo.MouseLeftButtonUp += (_, e) => { e.Handled = true; UndoSweep(); };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); HideUndo(); };
        // The pill centres itself inside a full-width host, so its position never
        // depends on a measurement taken before the sweep count was written.
        _undo.HorizontalAlignment = HorizontalAlignment.Center;
        _undo.VerticalAlignment = VerticalAlignment.Top;
        _undoHost = new Grid { IsHitTestVisible = false };
        _undoHost.Children.Add(_undo);
        Children.Add(_undoHost);
        SetZIndex(_undoHost, 1);

        _dropBand = new Path
        {
            StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        _dropTitle = new TextBlock
        {
            Text = Strings.DropToHang,
            FontFamily = Typography.Ui,
            FontSize = Typography.HeadingSize,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        Typography.ApplyText(_dropTitle);
        Typography.BlockLineHeight(_dropTitle, Typography.HeadingLineHeight);
        _dropHint = new TextBlock
        {
            Text = Strings.DropToHangHint,
            FontFamily = Typography.Ui,
            FontSize = Typography.CaptionSize,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 3, 0, 0),
        };
        Typography.ApplyText(_dropHint);
        Typography.BlockLineHeight(_dropHint, Typography.CaptionLineHeight);
        _dropOutline = new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            StrokeThickness = 1.25,
            StrokeDashArray = new DoubleCollection { 5, 4 },
            StrokeDashCap = PenLineCap.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _dropCard = new Border
        {
            Child = new Grid
            {
                Children =
                {
                    _dropOutline,
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _dropTitle, _dropHint },
                    },
                },
            },
            Padding = new Thickness(18, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(Layout.FrameRadius),
            BorderThickness = new Thickness(0.75),
            IsHitTestVisible = false,
        };
        _dropOverlay = new Grid
        {
            Height = Layout.PanelHeight,
            Opacity = 0,
            IsHitTestVisible = false,
            Children = { _dropBand, _dropCard },
        };
        Children.Add(_dropOverlay);
        SetZIndex(_dropOverlay, 8);
        UpdateDropOverlayStyle();

        _clearAnimation = ClearAnimation.Load(Settings.Current.ClearAnimationPath);
        _clearButtonImage = new Image
        {
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            RenderTransform = _clearButtonScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        RenderOptions.SetBitmapScalingMode(_clearButtonImage, BitmapScalingMode.HighQuality);
        _clearButton = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _clearButton.Child = _clearButtonImage;
        _clearButton.Padding = new Thickness(5);
        _clearButton.Cursor = System.Windows.Input.Cursors.Hand;
        _clearButton.MouseLeftButtonUp += OnClearButtonUp;
        Children.Add(_clearButton);
        SetZIndex(_clearButton, ClearButtonZ);

        _clearRunnerImage = new Image
        {
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            RenderTransform = _clearRunnerScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        RenderOptions.SetBitmapScalingMode(_clearRunnerImage, BitmapScalingMode.HighQuality);
        _clearRunner = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _clearRunner.Child = _clearRunnerImage;
        _clearRunner.Padding = new Thickness(0);
        _clearRunner.IsHitTestVisible = false;
        _clearRunner.Visibility = Visibility.Hidden;
        Children.Add(_clearRunner);
        SetZIndex(_clearRunner, ClearRunnerZ);
        RefreshClearAnimation();

        _quickNote = BuildQuickNoteButton();
        Children.Add(_quickNote);
        SetZIndex(_quickNote, QuickNoteZ);
        RefreshQuickNoteStyle();

        _clipboardWallet = new ClipboardWallet { Visibility = Visibility.Collapsed };
        _clipboardWallet.CopyRequested += entry => ClipboardCopyRequested?.Invoke(entry);
        _clipboardWallet.CopyPlainRequested += entry => ClipboardCopyPlainRequested?.Invoke(entry);
        _clipboardWallet.DeleteRequested += entry => ClipboardDeleteRequested?.Invoke(entry);
        _clipboardWallet.PinRequested += entry => ClipboardPinRequested?.Invoke(entry);
        _clipboardWallet.ClearRequested += () => ClipboardClearRequested?.Invoke();
        _clipboardWallet.MouseEnter += (_, _) => PreviewTargetChanged?.Invoke();
        _clipboardWallet.MouseLeave += (_, _) => PreviewTargetChanged?.Invoke();
        _clipboardWallet.GotKeyboardFocus += (_, _) => PreviewTargetChanged?.Invoke();
        _clipboardWallet.LostKeyboardFocus += (_, _) => PreviewTargetChanged?.Invoke();
        _clipboardWallet.ExpandedChanged += _ => PreviewTargetChanged?.Invoke();
        Children.Add(_clipboardWallet);
        SetZIndex(_clipboardWallet, ClipboardWalletZ);

        _line.ItemsChanged += OnItemsChanged;
        _line.ItemUpdated += OnItemUpdated;
        _line.Gust += OnGust;
        _line.StateChanged += OnStateChanged;
        _line.Swept += OnSwept;
        MouseLeftButtonDown += OnRopeDown;
        MouseLeftButtonUp += OnRopeUp;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        PreviewMouseRightButtonDown += OnPreviewMouseDown;
        MouseRightButtonUp += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (SearchRect is { } search && search.Contains(p)) { e.Handled = true; return; }
            if (ClearButtonRect is { } clear && clear.Contains(p)) { e.Handled = true; return; }
            if (NoteQuickAddRect is { } quickNote && quickNote.Contains(p)) { e.Handled = true; return; }
            if (ClipboardWalletRect is { } wallet && wallet.Contains(p)) { e.Handled = true; return; }
            if (IsOverRope(p) || (TagRect is { } t && t.Contains(p))) { e.Handled = true; MenuRequested?.Invoke(); }
        };

        // A small paper tag always hangs at the end of the line: the menu, within reach.
        _tag = BuildTag();
        Children.Add(_tag);
        // Keep the menu reachable even on a very narrow screen where cards overlap.
        SetZIndex(_tag, TagZ);

        MouseMove += (_, _) => UpdateHover(System.Windows.Input.Mouse.GetPosition(this));
        MouseLeave += (_, _) => UpdateHover(null);
        _hoverFocusTimer.Tick += (_, _) =>
            UpdateHover(IsMouseOver ? System.Windows.Input.Mouse.GetPosition(this) : null);
        _clearButtonTimer.Tick += (_, _) => AdvanceClearButtonFrame();
        LostMouseCapture += (_, _) =>
        {
            if (!_ropeDrag) return;
            _ropeDrag = false;
            UpdateQuickNoteState();
            RopeDragEnded?.Invoke();
        };
        SizeChanged += (_, _) => Relayout(snap: true);
        // Subscribed last: Repaint touches the tag, which BuildTag just created.
        Theme.Changed += OnThemeChanged;
        Pegs.LookChanged += OnLookChanged;
        Strings.LanguageChanged += OnLanguageChanged;
        Sync();
    }

    public IReadOnlyList<PeggedControl> Cards => _cards;
    public bool IsSearchEditing => _isSearchEditing;
    internal Rect? SearchRect => _width > 0
        ? new Rect(GetLeft(_search), GetTop(_search), Math.Max(1, _search.Width), SearchHeight)
        : null;
    internal PreviewTarget QuickLookTarget
    {
        get
        {
            if (_clipboardWalletVisible &&
                (_clipboardWallet.IsMouseOver || _clipboardWallet.IsKeyboardFocusWithin || _clipboardWallet.IsExpanded))
                return new PreviewTarget(PreviewTargetKind.ClipboardWallet);
            if (_hoverFocusId is { } hover)
                return new PreviewTarget(PreviewTargetKind.Card, hover);
            if (_primarySelectedId is { } selected)
                return new PreviewTarget(PreviewTargetKind.Card, selected);
            return new PreviewTarget(PreviewTargetKind.None);
        }
    }
    internal bool IsClipboardWalletExpanded => _clipboardWalletVisible && _clipboardWallet.IsExpanded;
    internal bool IsClipboardTextEditing => _clipboardWalletVisible && _clipboardWallet.HoldsTextFocus;
    internal Rect? ClipboardWalletRect => _width > 0 && _clipboardWalletVisible &&
                                          _clipboardWallet.ActualWidth > 0 && _clipboardWallet.ActualHeight > 0
        ? new Rect(
            GetLeft(_clipboardWallet),
            GetTop(_clipboardWallet),
            _clipboardWallet.ActualWidth,
            _clipboardWallet.ActualHeight)
        : null;
    internal event Action? SelectionChanged;
    internal event Action? PreviewTargetChanged;
    internal event Action<TextClipboardEntry>? ClipboardCopyRequested;
    internal event Action<TextClipboardEntry>? ClipboardCopyPlainRequested;
    internal event Action<TextClipboardEntry>? ClipboardDeleteRequested;
    internal event Action<TextClipboardEntry>? ClipboardPinRequested;
    internal event Action? ClipboardClearRequested;
    internal event Action<Guid?>? HoverFocusChanged;
    internal event Action? SearchEditingChanged;
    internal IReadOnlyList<Guid> SelectedIds => _line.OrderedItems
        .Where(item => !item.Falling && _selectedIds.Contains(item.Id))
        .Select(item => item.Id)
        .ToList();

    internal bool IsSelected(Guid id) => _selectedIds.Contains(id);

    internal void SetClipboardWalletVisible(bool visible)
    {
        if (_clipboardWalletVisible == visible) return;
        _clipboardWalletVisible = visible;
        if (!visible) _clipboardWallet.SetExpanded(false);
        _clipboardWallet.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            _clipboardWallet.Breeze(now: true);
            StartTicking();
        }
        Relayout(snap: true);
        PreviewTargetChanged?.Invoke();
    }

    internal void SetClipboardItems(IReadOnlyList<TextClipboardEntry> items) =>
        _clipboardWallet.SetItems(items);

    internal void TryToggleClipboardWallet()
    {
        if (!_clipboardWalletVisible || !_revealed) return;
        _clipboardWallet.Focus();
        _clipboardWallet.SetExpanded(!_clipboardWallet.IsExpanded);
    }

    internal void ToggleSelected(PeggedControl card)
    {
        var id = card.Item.Id;
        if (_selectedIds.Remove(id))
        {
            if (_primarySelectedId == id)
            {
                _primarySelectedId = _line.OrderedItems
                    .LastOrDefault(item => !item.Falling && _selectedIds.Contains(item.Id))?.Id;
            }
        }
        else
        {
            _selectedIds.Add(id);
            _primarySelectedId = id;
        }
        _selectionAnchor = id;
        ApplySelection();
        SelectionChanged?.Invoke();
    }

    internal void SelectRangeTo(PeggedControl card)
    {
        var ordered = _line.OrderedItems.Where(item => !item.Falling).ToList();
        int end = ordered.FindIndex(item => item.Id == card.Item.Id);
        if (end < 0) return;
        int start = _selectionAnchor is { } anchor
            ? ordered.FindIndex(item => item.Id == anchor)
            : -1;

        var next = new HashSet<Guid>();
        if (start < 0)
        {
            next.Add(card.Item.Id);
            _selectionAnchor = card.Item.Id;
        }
        else
        {
            int first = Math.Min(start, end);
            int last = Math.Max(start, end);
            for (int i = first; i <= last; i++) next.Add(ordered[i].Id);
        }

        bool changed = !_selectedIds.SetEquals(next) || _primarySelectedId != card.Item.Id;
        _selectedIds.Clear();
        _selectedIds.UnionWith(next);
        _primarySelectedId = card.Item.Id;
        ApplySelection();
        if (changed) SelectionChanged?.Invoke();
    }

    internal void ClearSelection()
    {
        if (_selectedIds.Count == 0 && _primarySelectedId is null && _selectionAnchor is null) return;
        _selectedIds.Clear();
        _primarySelectedId = null;
        _selectionAnchor = null;
        ApplySelection();
        SelectionChanged?.Invoke();
    }

    private void ApplySelection()
    {
        foreach (var card in _cards)
        {
            card.SetSelected(_selectedIds.Contains(card.Item.Id));
            card.SetPreviewFocused(card.Item.Id == _hoverFocusId);
        }
    }

    internal void Detach()
    {
        if (_clearRunning)
        {
            _clearRunning = false;
            _clearTargets.Clear();
            _line.ReleaseSaveHold();
        }
        _line.ItemsChanged -= OnItemsChanged;
        _line.ItemUpdated -= OnItemUpdated;
        _line.Gust -= OnGust;
        _line.StateChanged -= OnStateChanged;
        _line.Swept -= OnSwept;
        Theme.Changed -= OnThemeChanged;
        Pegs.LookChanged -= OnLookChanged;
        Strings.LanguageChanged -= OnLanguageChanged;
        CompositionTarget.Rendering -= OnRendering;
        _hoverFocusTimer.Stop();
        _clearButtonTimer.Stop();
        _undoTimer.Stop();
        _clipboardWallet.SetExpanded(false);
        _ticking = false;
    }

    private void OnItemsChanged() => Sync();

    private void OnGust()
    {
        foreach (var card in _cards)
            if (!card.Item.Falling) card.Breeze();
        _clipboardWallet.Breeze();
        StartTicking();
    }

    private void OnStateChanged()
    {
        if (_isSearchEditing && (_line.MenuOpen || _line.DraggingId is not null)) CloseSearch();
        if (_line.MenuOpen || _line.DraggingId is not null) ResetHoverInteraction();
        foreach (var card in _cards) card.StateChanged();
        UpdateQuickNoteState();
        StartTicking();
    }

    private void PaintRope()
    {
        var color = Pegs.Rope();
        _ropeCore.Stroke = Theme.Freeze(new SolidColorBrush(color));
        _ropeHighlight.Stroke = Theme.Freeze(new SolidColorBrush(Pegs.Lighten(color, 0.55)));
        _ropeHighlight.Opacity = 0.7;
    }

    private void OnThemeChanged() => RefreshVisuals(refreshCards: true);

    private void OnLookChanged() => RefreshVisuals(refreshCards: true);

    private void OnLanguageChanged()
    {
        RefreshVisuals(refreshCards: false);
        _clipboardWallet.RefreshLocalization();
        _clipboardWallet.RefreshTypography();
        foreach (var card in _cards) card.RefreshLocalization();
        Relayout(snap: true);
    }

    private void RefreshVisuals(bool refreshCards)
    {
        PaintRope();
        if (_width > 0) DrawRope();
        RefreshSurfaces();
        if (_hint.Child is TextBlock hintText)
        {
            hintText.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Secondary));
            hintText.Text = Strings.Hint;
            hintText.FontFamily = Typography.Ui;
            hintText.FontSize = Typography.SmallSize;
            hintText.FontWeight = FontWeights.Medium;
            hintText.FlowDirection = Strings.Flow;
            Typography.ApplyText(hintText);
            Typography.BlockLineHeight(hintText, Typography.SmallLineHeight);
        }
        _tagGlyph.Fill = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _tag.FlowDirection = Strings.Flow;
        _tag.ToolTip = Strings.Menu;
        _hint.ToolTip = Strings.NewCaptureTip;
        _hint.FlowDirection = Strings.Flow;
        _undoAction.Text = Strings.Undo;
        Typography.ApplyText(_undoAction);
        _undo.FlowDirection = Strings.Flow;
        if (_pendingSweep.Count > 0) _undoCount.Text = string.Format(Strings.SweptCards, _pendingSweep.Count);
        _clipboardWallet.RefreshTheme();
        RefreshSearchStyle();
        UpdateClearButtonState();
        UpdateQuickNoteState();
        if (refreshCards)
            foreach (var card in _cards) card.RefreshTheme();
    }

    private Border BuildSearchControl()
    {
        var shell = Glass.Frame(SearchRadius);
        shell.Height = SearchHeight;
        shell.Width = SearchCollapsedWidth;
        shell.ClipToBounds = true;
        shell.VerticalAlignment = VerticalAlignment.Top;
        shell.HorizontalAlignment = HorizontalAlignment.Left;

        var root = new Grid();

        _searchCollapsed = new Grid
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Search,
            FlowDirection = Strings.Flow,
        };
        _searchCollapsedHover = new Border
        {
            CornerRadius = new CornerRadius(SearchRadius - 1),
            Margin = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        _searchCollapsedHover.SetResourceReference(Border.BackgroundProperty, "MenuHover");
        _searchCollapsed.Children.Add(_searchCollapsedHover);

        _searchCollapsedGlyph = SearchGlyph(13, Theme.Freeze(new SolidColorBrush(Theme.Secondary)));
        _searchCollapsedLabel = new TextBlock
        {
            Text = Strings.Search,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary)),
        };
        Typography.ApplyText(_searchCollapsedLabel);
        Typography.BlockLineHeight(_searchCollapsedLabel, Typography.SmallLineHeight);
        var collapsedContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        collapsedContent.Children.Add(_searchCollapsedGlyph);
        collapsedContent.Children.Add(_searchCollapsedLabel);
        _searchCollapsed.Children.Add(collapsedContent);
        _searchCollapsed.MouseEnter += (_, _) =>
        {
            UiMotion.Fade(_searchCollapsedHover, true, 110);
            UiMotion.Scale(_searchCollapsedGlyph, 1.06, 110);
        };
        _searchCollapsed.MouseLeave += (_, _) =>
        {
            UiMotion.Fade(_searchCollapsedHover, false, 110);
            UiMotion.Scale(_searchCollapsedGlyph, 1, 110);
        };
        _searchCollapsed.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_search.IsEnabled) return;
            e.Handled = true;
            UiMotion.Scale(_searchCollapsedGlyph, 0.92, 80);
            OpenSearch();
        };
        _searchCollapsed.PreviewMouseLeftButtonUp += (_, _) =>
            UiMotion.Scale(_searchCollapsedGlyph, _searchCollapsed.IsMouseOver ? 1.06 : 1, 110);
        root.Children.Add(_searchCollapsed);

        _searchExpanded = new Grid
        {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            FlowDirection = Strings.Flow,
        };
        _searchExpanded.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _searchExpanded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputHost = new Grid();
        _searchInput = new TextBox
        {
            Height = SearchHeight,
            Margin = new Thickness(SearchTextInset, 0, 5, 0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary)),
            CaretBrush = Theme.Freeze(new SolidColorBrush(Theme.Primary)),
            Cursor = Cursors.IBeam,
        };
        Typography.ApplyText(_searchInput);
        Theme.SetFocusRing(_searchInput, false);
        _searchInput.TextChanged += (_, _) => ApplySearchQuery();
        _searchInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSearch();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplySearchQuery();
                FocusSearchInput();
            }
        };
        inputHost.Children.Add(_searchInput);

        _searchPlaceholder = new TextBlock
        {
            Text = Strings.SearchCardsPlaceholder,
            Margin = new Thickness(SearchTextInset, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Caption)),
            IsHitTestVisible = false,
        };
        Typography.ApplyText(_searchPlaceholder);
        Typography.BlockLineHeight(_searchPlaceholder, Typography.SmallLineHeight);
        inputHost.Children.Add(_searchPlaceholder);
        _searchExpanded.Children.Add(inputHost);

        _searchAction = new Border
        {
            Margin = new Thickness(0, 3, 3, 3),
            Padding = new Thickness(0),
            MinWidth = 76,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Search,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _searchActionGlyph = SearchGlyph(12, Theme.Freeze(new SolidColorBrush(Theme.Primary)));
        _searchActionLabel = new TextBlock
        {
            Text = Strings.Search,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary)),
        };
        Typography.ApplyText(_searchActionLabel);
        Typography.BlockLineHeight(_searchActionLabel, Typography.SmallLineHeight);
        var actionContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 11, 0),
        };
        actionContent.Children.Add(_searchActionGlyph);
        actionContent.Children.Add(_searchActionLabel);
        _searchActionWash = new Border
        {
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        _searchActionWash.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _searchAction.Child = new Grid { Children = { _searchActionWash, actionContent } };
        _searchAction.MouseEnter += (_, _) =>
        {
            UiMotion.Fade(_searchActionWash, true, 110);
            UiMotion.Scale(_searchActionGlyph, 1.06, 110);
        };
        _searchAction.MouseLeave += (_, _) =>
        {
            UiMotion.Fade(_searchActionWash, false, 110);
            UiMotion.Scale(_searchActionGlyph, 1, 110);
        };
        _searchAction.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            UiMotion.Scale(_searchActionGlyph, 0.92, 80);
            ApplySearchQuery();
            FocusSearchInput();
        };
        _searchAction.PreviewMouseLeftButtonUp += (_, _) =>
            UiMotion.Scale(_searchActionGlyph, _searchAction.IsMouseOver ? 1.06 : 1, 110);
        Grid.SetColumn(_searchAction, 1);
        _searchExpanded.Children.Add(_searchAction);
        root.Children.Add(_searchExpanded);

        _searchFocusHalo = new Border
        {
            CornerRadius = new CornerRadius(SearchRadius),
            BorderThickness = new Thickness(1.1),
            Margin = new Thickness(0.75),
            Opacity = 0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        _searchFocusHalo.SetResourceReference(Border.BorderBrushProperty, "AccentHalo");
        root.Children.Add(_searchFocusHalo);
        shell.Child = root;
        RefreshSearchStyle();
        return shell;
    }

    private static Path SearchGlyph(double size, Brush stroke) =>
        Morphicons.CreatePath(Morphicon.Search, size, 1.55, stroke);

    private void RefreshSearchStyle()
    {
        if (_search is null) return;
        Glass.Paint(_search);
        _search.FlowDirection = Strings.Flow;
        _searchCollapsed.FlowDirection = Strings.Flow;
        _searchExpanded.FlowDirection = Strings.Flow;
        _searchInput.FlowDirection = Strings.Flow;
        _searchInput.FontFamily = Typography.Ui;
        _searchInput.FontSize = Typography.SmallSize;
        _searchInput.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _searchInput.CaretBrush = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _searchInput.SelectionBrush = Theme.Freeze(new SolidColorBrush(Theme.Alpha(Theme.Accent, 0x55)));
        _searchCollapsedLabel.Text = Strings.Search;
        _searchCollapsedLabel.FontFamily = Typography.Ui;
        _searchCollapsedLabel.FontSize = Typography.SmallSize;
        _searchCollapsedLabel.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _searchPlaceholder.Text = Strings.SearchCardsPlaceholder;
        _searchPlaceholder.FontFamily = Typography.Ui;
        _searchPlaceholder.FontSize = Typography.SmallSize;
        _searchPlaceholder.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Caption));
        _searchActionLabel.Text = Strings.Search;
        _searchActionLabel.FontFamily = Typography.Ui;
        _searchActionLabel.FontSize = Typography.SmallSize;
        _searchActionLabel.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _searchCollapsedGlyph.Stroke = Theme.Freeze(new SolidColorBrush(Theme.Secondary));
        _searchActionGlyph.Stroke = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _searchAction.ToolTip = Strings.Search;
        _searchCollapsed.ToolTip = Strings.Search;
    }

    internal void OpenSearch()
    {
        if (_isSearchEditing || !_revealed || _line.MenuOpen || _line.DraggingId is not null || _line.LiveCount == 0)
            return;
        _isSearchEditing = true;
        ResetHoverInteraction();
        UpdateQuickNoteState();
        _searchInput.Clear();
        UiMotion.Fade(_searchCollapsed, false, 80);
        UiMotion.Fade(_searchExpanded, true, 110);
        UiMotion.Fade(_searchPlaceholder, true, 130);
        UiMotion.Fade(_searchFocusHalo, true, 140);
        SetSearchWidth(ExpandedSearchWidth());
        RefreshSearchStyle();
        SearchEditingChanged?.Invoke();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(FocusSearchInput));
    }

    internal void CloseSearch()
    {
        if (!_isSearchEditing && !_isFiltering) return;
        bool changed = _isSearchEditing;
        _isSearchEditing = false;
        UpdateQuickNoteState();
        _searchInput.Clear();
        SetFilter("");
        UiMotion.Fade(_searchExpanded, false, 80);
        UiMotion.Fade(_searchCollapsed, true, 110);
        UiMotion.Fade(_searchFocusHalo, false, 100);
        UiMotion.Fade(_searchActionWash, false, 90);
        UiMotion.Scale(_searchActionGlyph, 1, 110);
        RefreshSearchStyle();
        SetSearchWidth(CollapsedSearchWidth());
        if (changed) SearchEditingChanged?.Invoke();
    }

    internal void FocusSearchInput()
    {
        if (!_isSearchEditing) return;
        _searchInput.Focus();
        Keyboard.Focus(_searchInput);
        _searchInput.CaretIndex = _searchInput.Text.Length;
    }

    private void ApplySearchQuery()
    {
        SetFilter(_searchInput.Text);
        UiMotion.Fade(_searchPlaceholder, string.IsNullOrWhiteSpace(_searchInput.Text), 90);
    }

    private void UpdateSearchAvailability()
    {
        bool available = _revealed && _line.LiveCount > 0;
        if (!available && _isSearchEditing) CloseSearch();
        _search.IsEnabled = available;
        _search.IsHitTestVisible = available;
        _search.Opacity = available ? 1 : 0.42;
    }

    private double AvailableSearchWidth() => Math.Max(1, (_width > 0 ? _width : ActualWidth) - 16);

    private double CollapsedSearchWidth() => Math.Min(SearchCollapsedWidth, AvailableSearchWidth());

    private double ExpandedSearchWidth() => Math.Min(SearchExpandedWidth, AvailableSearchWidth());

    private void LayoutSearch()
    {
        if (_width <= 0) return;
        double target = _isSearchEditing ? ExpandedSearchWidth() : CollapsedSearchWidth();
        if (Math.Abs(_searchWidth.Target - target) > 0.5) SetSearchWidth(target);
        else PlaceSearch();
    }

    private void SetSearchWidth(double target)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _searchWidth.Snap(target);
            PlaceSearch();
            return;
        }
        _searchWidth.Go(target, 0.18, Ease.OutCubic);
        StartTicking();
    }

    private void PlaceSearch()
    {
        if (_width <= 0) return;
        double width = Math.Clamp(_searchWidth.Value, 1, AvailableSearchWidth());
        _search.Width = width;
        double mid = _width / 2;
        SetLeft(_search, mid - width / 2);
        SetTop(_search, Math.Max(0, Layout.RopeY(mid, _width) - SearchGap - SearchHeight));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isSearchEditing) return;
        var point = e.GetPosition(this);
        if (SearchRect is { } search && search.Contains(point)) return;
        CloseSearch();
    }

    private void UpdateDropOverlayStyle()
    {
        var accent = Theme.Accent;
        var outline = _dropZoneHot
            ? accent
            : Theme.AppsDark ? Theme.AccentWash : accent;
        _dropBand.Stroke = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)));
        _dropOutline.Stroke = Theme.Freeze(new SolidColorBrush(outline));
        // Solid: the empty-line hint sits behind this card, and at 75-85% the two
        // sentences overlapped into unreadable double exposure on a light desktop.
        _dropCard.Background = Theme.Freeze(new SolidColorBrush(_dropZoneHot
            ? Theme.AppsDark ? Color.FromArgb(0xFF, 0x28, 0x35, 0x4A) : Color.FromArgb(0xFF, 0xEE, 0xF5, 0xFF)
            : Theme.AppsDark ? Color.FromArgb(0xFF, 0x24, 0x28, 0x30) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
        _dropCard.BorderBrush = Theme.Freeze(_dropZoneHot
            ? new LinearGradientBrush(outline, Theme.GlassEdgeTop, 90)
            : new LinearGradientBrush(Theme.GlassEdgeTop, Theme.GlassEdgeBottom, 90));
        Glass.Elevate(_dropCard, _dropZoneHot ? Theme.AppsDark ? 0.46 : 0.24 : Theme.AppsDark ? 0.30 : 0.15);
        _dropTitle.Text = Strings.DropToHang;
        _dropTitle.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _dropTitle.FontFamily = Typography.Ui;
        _dropTitle.FontSize = Typography.HeadingSize;
        _dropTitle.FlowDirection = Strings.Flow;
        Typography.ApplyText(_dropTitle);
        Typography.BlockLineHeight(_dropTitle, Typography.HeadingLineHeight);
        _dropHint.Text = Strings.DropToHangHint;
        _dropHint.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Caption));
        _dropHint.FontFamily = Typography.Ui;
        _dropHint.FontSize = Typography.CaptionSize;
        _dropHint.FlowDirection = Strings.Flow;
        Typography.ApplyText(_dropHint);
        Typography.BlockLineHeight(_dropHint, Typography.CaptionLineHeight);
    }

    /// <summary>Refresh type in the line overlay and existing cards after a scale change.</summary>
    public void RefreshTypography()
    {
        RefreshVisuals(refreshCards: false);
        foreach (var card in _cards) card.RefreshTypography();
        Relayout(snap: true);
    }

    /// <summary>Reloads the clear animation and reapplies its button and runner size.</summary>
    public void RefreshClearAnimation()
    {
        _clearAnimation = ClearAnimation.Load(Settings.Current.ClearAnimationPath);
        _clearFrame = 0;
        _clearFrameAge = 0;
        _clearButtonFrame = 0;
        ApplyClearRunnerFrame();
        ApplyClearButtonFrame();
        LayoutClearControls();
        UpdateClearButtonAnimation();
        StartTicking();
    }

    public Rect? ClearButtonRect =>
        _width > 0 && _revealed && Settings.Current.OneClickClear &&
        _clearButton.Visibility == Visibility.Visible
            ? new Rect(GetLeft(_clearButton), GetTop(_clearButton), _clearButton.Width, _clearButton.Height)
            : null;

    public Rect? NoteQuickAddRect =>
        _width > 0 && _revealed && _quickNote.Visibility == Visibility.Visible
            ? QuickNoteBounds
            : null;

    private Rect QuickNoteBounds => new(
        QuickNoteLeft,
        Layout.RopeY(QuickNoteLeft + QuickNoteHostWidth / 2, _width) - Layout.PinAbove * 0.75,
        QuickNoteHostWidth,
        QuickNoteHostHeight);

    public event Action? ClearAnimationStarted;
    public event Action? ClearAnimationFinished;

    private void ApplyClearRunnerFrame()
    {
        if (_clearAnimation.Frames.Count == 0) return;
        int index = Math.Abs(_clearFrame) % _clearAnimation.Frames.Count;
        _clearRunnerImage.Source = _clearAnimation.Frames[index];
    }

    private void ApplyClearButtonFrame()
    {
        if (_clearAnimation.Frames.Count == 0) return;
        int index = Math.Abs(_clearButtonFrame) % _clearAnimation.Frames.Count;
        _clearButtonImage.Source = _clearAnimation.Frames[index];
    }

    private double ClearAnimationSize()
        => Settings.ClearAnimationSizeOption(Settings.Current.ClearAnimationSize);

    private void LayoutClearControls()
    {
        if (_width <= 0) return;
        double size = ClearAnimationSize();
        double buttonSize = size + 10;
        _clearButton.Width = buttonSize;
        _clearButton.Height = buttonSize;
        _clearRunner.Width = size;
        _clearRunner.Height = size;

        double halfButton = buttonSize / 2;
        bool onLeft = Settings.ClearAnimationSideOption(Settings.Current.ClearAnimationSide) == "left";
        double offset = Settings.ClearAnimationOffsetOption(Settings.Current.ClearAnimationOffset);
        double desiredX = onLeft ? offset + halfButton : _width - offset - halfButton;
        double buttonX = Math.Clamp(
            desiredX,
            halfButton,
            Math.Max(halfButton, _width - halfButton));
        _clearHomeX = buttonX;
        double buttonTop = Math.Max(4, Layout.RopeY(buttonX, _width) - buttonSize - 8);
        if (onLeft)
        {
            var quickBounds = QuickNoteBounds;
            bool sharesLeftColumn =
                buttonX - halfButton < quickBounds.Right &&
                buttonX + halfButton > quickBounds.Left;
            if (sharesLeftColumn)
                buttonTop = Math.Max(4, quickBounds.Bottom + 6);
            buttonTop = Math.Min(buttonTop, Math.Max(4, Layout.PanelHeight - buttonSize - 4));
        }
        SetLeft(_clearButton, buttonX - buttonSize / 2);
        SetTop(_clearButton, buttonTop);

        double scaleX = Settings.Current.ClearAnimationMirrored ? -1 : 1;
        _clearButtonScale.ScaleX = scaleX;
        _clearRunnerScale.ScaleX = scaleX;

        UpdateClearButtonState();
        if (_clearRunning)
        {
            double progress = _clearDuration <= 0 ? 1 : Math.Clamp(_clearElapsed / _clearDuration, 0, 1);
            PlaceClearRunner(_clearStartX + (_clearEndX - _clearStartX) * progress);
        }
    }

    private void UpdateClearButtonState()
    {
        bool canRun = Settings.Current.OneClickClear && _revealed && _line.LiveCount > 0 && !_clearRunning;
        _clearButton.Visibility = Settings.Current.OneClickClear && !_clearRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        _clearButton.Opacity = 1;
        _clearButton.Cursor = canRun ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        _clearButton.ToolTip = Strings.OneClickClearTip;
        UpdateClearButtonAnimation();
    }

    private void PlaceClearRunner(double x)
    {
        SetLeft(_clearRunner, x - _clearRunner.Width / 2);
        SetTop(_clearRunner, Layout.RopeY(x, _width) - _clearRunner.Height / 2);
    }

    private void OnClearButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        SweepIfAllowed();
    }

    /// <summary>Runs the one-click clear, as pressing the clear button would.</summary>
    internal void RunClearSweep() => SweepIfAllowed();

    /// <summary>Calls a sweep back, as pressing the undo pill would.</summary>
    internal void RunUndoSweep() => UndoSweep();

    /// <summary>
    /// Hands the idle clear button's loop to <see cref="Advance"/>. Its own loop
    /// runs on a dispatcher timer, which an offscreen render never pumps; the
    /// sweep itself is stepped by <see cref="Advance"/> either way.
    /// </summary>
    internal void DetachClearButtonLoop()
    {
        _clearButtonStill = true;
        _clearButtonTimer.Stop();
        _clearButtonFrame = 0;
        ApplyClearButtonFrame();
    }

    private void SweepIfAllowed()
    {
        if (!Settings.Current.OneClickClear || !_revealed || _clearRunning || _line.LiveCount == 0 || _width <= 0) return;
        _clearLeftToRight = Settings.ClearAnimationSideOption(Settings.Current.ClearAnimationSide) == "left";
        var targets = _cards
            .Where(card => card.VisibleForHit)
            .Select(card => (card.Item.Id, x: card.CenterX))
            .ToList();
        targets.Sort((a, b) =>
            _clearLeftToRight ? a.x.CompareTo(b.x) : b.x.CompareTo(a.x));
        if (targets.Count == 0) return;

        _clearSweep = _line.Snapshot(targets.Select(t => t.Id));
        _clearTargets.Clear();
        foreach (var target in targets) _clearTargets.Add((target.Id, target.x, false));
        _clearStartX = _clearLeftToRight ? -28 : _width + 28;
        _clearEndX = _clearLeftToRight ? _width + 28 : -28;
        _clearElapsed = 0;
        _clearDuration = Math.Max(0.4, Math.Abs(_clearEndX - _clearStartX) / 900.0);
        _clearReturning = false;
        _clearRunning = true;
        // The runner drops cards one at a time for the animation; keep the
        // settings file still until the whole sweep is done.
        _line.HoldSave();
        _clearFrame = 0;
        _clearFrameAge = 0;
        ApplyClearRunnerFrame();
        PlaceClearRunner(_clearStartX);
        _clearRunner.Visibility = Visibility.Visible;
        UpdateClearButtonState();
        UpdateQuickNoteState();
        ClearAnimationStarted?.Invoke();
        StartTicking();
    }

    /// <summary>Re-applies the line's surface styling after a theme change.</summary>
    private void RefreshSurfaces()
    {
        Glass.Paint(_hint);
        // The pill carries accent-coloured text, which on a 72% sheet ranges from 5:1
        // over a pale desktop to 2.5:1 over a black one. A solid floor makes it fixed.
        Glass.PaintPanel(_undo);
        if (_tagChip is not null) Glass.Paint(_tagChip);
        UpdateDropOverlayStyle();
        RefreshQuickNoteStyle();
        RepaintFixedPegs();
    }

    /// <summary>
    /// The tag and the quick note are built once, so their clips have to be remade here:
    /// a peg is a shape rather than a brush, and nothing about repainting a surface would
    /// otherwise turn a wooden clip into a brass one when the setting changes.
    /// </summary>
    private void RepaintFixedPegs()
    {
        if (_tag is not null)
        {
            _tag.Children.Remove(_tagPeg);
            _tagPeg = Pegs.CreateLineClip(seed: 1);
            _tag.Children.Add(_tagPeg);
        }
        if (_quickNote is not null)
        {
            _quickNote.Children.Remove(_quickNotePeg);
            _quickNotePeg = Pegs.CreateLineClip(seed: 2);
            _quickNote.Children.Add(_quickNotePeg);
        }
    }

    private Grid BuildQuickNoteButton()
    {
        var button = new Grid
        {
            Width = QuickNoteHostWidth,
            Height = QuickNoteHostHeight,
            Background = Brushes.Transparent,
        };
        button.Cursor = Cursors.Hand;
        button.ToolTip = Strings.QuickNewNote;
        button.FlowDirection = Strings.Flow;

        _quickNoteChip = Glass.Chip(QuickNoteChipHeight);
        _quickNoteChip.Width = QuickNoteChipWidth;
        _quickNoteChip.CornerRadius = new CornerRadius(5);
        _quickNoteChip.BorderThickness = new Thickness(0.8);
        _quickNoteChip.HorizontalAlignment = HorizontalAlignment.Center;
        _quickNoteChip.VerticalAlignment = VerticalAlignment.Top;
        _quickNoteChip.Margin = new Thickness(0, Layout.PinHeight * 0.75 - 5, 0, 0);
        _quickNoteChip.RenderTransformOrigin = new Point(0.5, 0);
        _quickNoteChip.RenderTransform = new RotateTransform(3);
        _quickNoteChip.IsHitTestVisible = false;

        _quickNoteGlyph = Morphicons.CreatePath(Morphicon.Plus, 12, 1.7, Brushes.Transparent);
        _quickNoteChip.Child = _quickNoteGlyph;

        _quickNotePeg = Pegs.CreateLineClip(seed: 2);
        _quickNotePeg.Margin = new Thickness(0);

        button.Children.Add(_quickNoteChip);
        button.Children.Add(_quickNotePeg);
        button.MouseEnter += (_, _) =>
        {
            _quickNoteHot = true;
            RefreshQuickNoteStyle();
        };
        button.MouseLeave += (_, _) =>
        {
            _quickNoteHot = false;
            RefreshQuickNoteStyle();
        };
        button.MouseLeftButtonUp += OnQuickNoteUp;
        return button;
    }

    private void RefreshQuickNoteStyle()
    {
        if (_quickNote is null) return;
        var paper = Notes.Paper("yellow");
        _quickNoteChip.Background = Theme.Freeze(new SolidColorBrush(
            _quickNoteHot ? Pegs.Lighten(paper, 0.14) : paper));
        _quickNoteChip.BorderBrush = Theme.Freeze(new SolidColorBrush(
            _quickNoteHot ? Theme.Primary : Color.FromArgb(0x72, 0x5A, 0x48, 0x18)));
        _quickNote.ToolTip = Strings.QuickNewNote;
        _quickNote.FlowDirection = Strings.Flow;
        _quickNoteGlyph.Stroke = Theme.Freeze(new SolidColorBrush(
            _quickNoteHot ? Theme.Primary : Color.FromRgb(0x55, 0x43, 0x12)));
    }

    private void UpdateQuickNoteState()
    {
        if (_quickNote is null) return;
        bool visible = _revealed && _width > 0;
        bool enabled = visible && !_line.MenuOpen && _line.DraggingId is null && !_ropeDrag && !_clearRunning;
        _quickNote.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _quickNote.IsEnabled = enabled;
        _quickNote.IsHitTestVisible = enabled;
        _quickNote.Opacity = enabled ? 1 : 0.46;
    }

    private void PlaceQuickNote()
    {
        if (_width <= 0) return;
        var bounds = QuickNoteBounds;
        SetLeft(_quickNote, bounds.Left);
        SetTop(_quickNote, bounds.Top);
    }

    private void OnQuickNoteUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_revealed || _line.MenuOpen || _line.DraggingId is not null || _ropeDrag || _clearRunning) return;
        _line.NewNote("yellow");
    }

    public Rect? DropZoneRect => _dropZone;

    public void SetDropZone(bool visible, bool hot)
    {
        if (visible && _isSearchEditing) CloseSearch();
        hot &= visible;
        if (_dropZoneVisible == visible && _dropZoneHot == hot) return;
        _dropZoneVisible = visible;
        _dropZoneHot = hot;
        UpdateDropOverlayStyle();
        _dropOpacity.Go(visible ? 1 : 0, visible ? 0.16 : 0.22, visible ? Ease.OutCubic : Ease.InCubic);
        StartTicking();
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (_revealed == value) return;
            _revealed = value;
            if (value)
            {
                // Tucked away, the whole line waits above the top edge and slides
                // out from under the taskbar, the way an auto-hiding taskbar does.
                _revealSpring.Value = _reveal.Y;
                _revealSpring.Velocity = 0;
                _revealSpring.Target = 0;
                _usingSpring = true;
            }
            else
            {
                CloseSearch();
                _clipboardWallet.SetExpanded(false);
                _hideTween.Snap(_reveal.Y);
                _hideTween.Go(-(Layout.PanelHeight + 12), 0.22, Ease.InCubic);
                _usingSpring = false;
            }
            UpdateSearchAvailability();
            UpdateQuickNoteState();
            UpdateClearButtonAnimation();
            StartTicking();
        }
    }

    // MARK: Items

    private void Sync()
    {
        var items = _line.Items;
        var live = new HashSet<Pegged>(items);
        var liveIds = new HashSet<Guid>(items.Select(item => item.Id));
        int selectedCount = _selectedIds.Count;
        var primary = _primarySelectedId;
        _selectedIds.IntersectWith(liveIds);
        if (_selectionAnchor is { } anchor && !liveIds.Contains(anchor)) _selectionAnchor = null;
        if (_primarySelectedId is { } selected && (!liveIds.Contains(selected) || !_selectedIds.Contains(selected)))
        {
            _primarySelectedId = _line.OrderedItems
                .LastOrDefault(item => !item.Falling && _selectedIds.Contains(item.Id))?.Id;
        }
        if (_hoverCandidateId is { } candidate && !liveIds.Contains(candidate))
            ResetHoverCandidate();
        if (_hoverFocusId is { } focused && !liveIds.Contains(focused))
            SetHoverFocus(null);
        // Remove what is gone.
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            var card = _cards[i];
            if (live.Contains(card.Item)) continue;
            _cards.Remove(card);
            Children.Remove(card);
        }
        // Add what is new, in order.
        var known = new HashSet<Pegged>(_cards.Count);
        foreach (var card in _cards) known.Add(card.Item);
        foreach (var item in items)
        {
            if (known.Contains(item)) continue;
            var card = new PeggedControl(item, _line);
            _cards.Add(card);
            Children.Add(card);
            SetZIndex(card, 2);
        }
        // The stored list drives add/remove; the view order drives the layout.
        var order = _line.OrderedItems;
        var rank = new Dictionary<Pegged, int>(order.Count);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
        _cards.Sort((a, b) => Rank(a.Item, rank).CompareTo(Rank(b.Item, rank)));
        ApplySelection();
        ApplyFilter();
        if (selectedCount != _selectedIds.Count || primary != _primarySelectedId) SelectionChanged?.Invoke();
        Relayout(snap: false);
        bool empty = _line.LiveCount == 0;
        UpdateSearchAvailability();
        UpdateClearButtonState();
        UpdateQuickNoteState();
        _hintOpacity.Go(empty ? 1 : 0, 0.3);
        StartTicking();
    }

    private static int Rank(Pegged item, Dictionary<Pegged, int> rank) =>
        rank.TryGetValue(item, out var r) ? r : int.MaxValue;

    /// <summary>Re-reads the view order, for example after a sort or capacity change.</summary>
    public void RefreshOrder()
    {
        Sync();
    }

    /// <summary>Shows only cards whose file name, title or note contains the query.</summary>
    public (int Total, int Matched) SetFilter(string? query)
    {
        _filter = query?.Trim() ?? "";
        _isFiltering = _filter.Length > 0;
        var result = ApplyFilter();
        Relayout(snap: true);
        StartTicking();
        return result;
    }

    private (int Total, int Matched) ApplyFilter()
    {
        int total = 0;
        int matched = 0;
        foreach (var card in _cards)
        {
            if (card.Item.Falling)
            {
                card.SetFiltered(false);
                continue;
            }

            total++;
            bool visible = !_isFiltering || MatchesFilter(card.Item, _filter);
            if (visible) matched++;
            card.SetFiltered(!visible);
        }
        return (total, matched);
    }

    private static bool MatchesFilter(Pegged item, string query)
    {
        string name = item.Path;
        string title = item.Path;
        try { name = System.IO.Path.GetFileName(item.Path); } catch { }
        try { title = System.IO.Path.GetFileNameWithoutExtension(name); } catch { }
        return name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (Notes.Get(item.Path)?.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Cards can be slid along the line only in manual order.</summary>
    public bool CanReorderCards =>
        !_isFiltering && Settings.CardSortOption(Settings.Current.CardSort) == "manual";

    private void OnItemUpdated(Pegged item)
    {
        var card = _cards.FirstOrDefault(c => c.Item == item);
        if (card is null) return;
        if (!item.Flying && card.Opacity == 0 && !item.Falling) card.Land();
        card.Refresh();
        if (_isFiltering)
        {
            ApplyFilter();
            Relayout(snap: false);
        }
        RebuildHitCache();
        UpdateClearButtonState();
        StartTicking();
    }

    private void Relayout(bool snap)
    {
        _width = ActualWidth;
        if (_width <= 0) return;
        _dropOverlay.Width = _width;
        _dropZone = Layout.DropZone(_width);
        _dropCard.Width = _dropZone.Value.Width;
        _dropCard.Height = _dropZone.Value.Height;
        _dropCard.Margin = new Thickness(0, _dropZone.Value.Top, 0, 0);
        UpdateDropBandGeometry(_dropZone.Value, _width);
        DrawRope();

        // Every card hangs on one row. When the number outgrows the screen the
        // step shrinks below the card width, so the row overlaps itself instead
        // of folding into stacks or paging.
        int count = _cards.Count(card => !card.Filtered);
        // The clipboard wallet reserves the right edge. The row stays centred on
        // the line and only moves left by what the reserved band really needs;
        // the full screen width is used again whenever the wallet is hidden.
        var band = CardBand();
        double leftLimit = band.Left;
        double rightLimit = band.Right;
        double walletLeft = band.WalletLeft;
        int visibleIndex = 0;
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            if (card.Filtered)
            {
                card.SortOrder = -1;
                ApplyBaseZOrder(card);
                continue;
            }

            int index = visibleIndex++;
            card.SortOrder = index;
            ApplyBaseZOrder(card);
            card.RopeYAt ??= x => Layout.RopeY(x, _width);
            double x = Layout.SpreadX(index, count, _width, leftLimit, rightLimit);
            double ropeY = Layout.RopeY(x, _width);
            bool fresh = double.IsNaN(GetTop(card));
            SetTop(card, ropeY - Layout.PinAbove);
            card.Height = Layout.PanelHeight - ropeY;
            if (card != _held) card.SetTargetX(x, snap || fresh);
        }

        double mid = _width / 2;
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tagX = _width - 30;
        SetLeft(_tag, tagX - Layout.CardWidth / 2);
        SetTop(_tag, Layout.RopeY(tagX, _width) - Layout.PinAbove * 0.75);
        SetLeft(_hint, mid - _hint.DesiredSize.Width / 2);
        SetTop(_hint, Layout.RopeY(mid, _width) + 34 - _hint.DesiredSize.Height / 2);
        double undoMid = Layout.RopeY(mid, _width) + 34;
        // The hint owns the slot above an empty line, so the undo steps in below it.
        if (_line.LiveCount == 0) undoMid += _hint.DesiredSize.Height / 2 + 6 + _undo.DesiredSize.Height / 2;
        SetLeft(_undoHost, 0);
        _undoHost.Width = _width;
        SetTop(_undoHost, undoMid - _undo.DesiredSize.Height / 2);
        if (_clipboardWalletVisible)
        {
            double walletX = walletLeft + ClipboardWallet.CardWidth / 2;
            SetLeft(_clipboardWallet, walletLeft);
            SetTop(_clipboardWallet, Layout.RopeY(walletX, _width) - Layout.PinAbove);
        }
        LayoutSearch();
        LayoutClearControls();
        PlaceQuickNote();
        UpdateQuickNoteState();
        RebuildHitCache();
    }

    /// <summary>
    /// Keeps the undo pill centred on the line. It is measured here every time the
    /// text changes, because the sweep sets the count after the last full layout and
    /// a stale width left the pill off-centre until the next relayout.
    /// </summary>

    /// <summary>The horizontal band a card row may occupy, excluding the clipboard wallet.</summary>
    private (double Left, double Right, double WalletLeft) CardBand()
    {
        double left = Layout.SideInset;
        double right = _width - Layout.SideInset;
        double walletLeft = 0;
        if (_clipboardWalletVisible)
        {
            double edge = Settings.ClipboardEdgeDistanceOption(Settings.Current.ClipboardEdgeDistance);
            walletLeft = Math.Max(0, _width - edge - ClipboardWallet.CardWidth);
            right = Math.Min(right, Math.Max(left + 1, walletLeft - 12));
        }
        return (left, right, walletLeft);
    }

    private void DrawRope()
    {
        double top = Layout.RopeTop, w = _width;
        var fig = new PathFigure { StartPoint = new Point(-20, top) };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, top + 2 * Layout.Sag(w)), new Point(w + 20, top), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        _ropeHalo.Data = _ropeShadow.Data = _ropeCore.Data = _ropeHighlight.Data = geo;
        _rope.Width = w;
        _rope.Height = Layout.PanelHeight;

    }

    private void UpdateDropBandGeometry(Rect zone, double width)
    {
        if (width <= 0)
        {
            _dropBand.Data = null;
            return;
        }

        // The rope is a quadratic Bezier from (-20, RopeTop) through the
        // centre sag to (width + 20, RopeTop). Its x coordinate is linear in
        // t, so the drop-zone edges map directly to the same curve.
        var p0 = new Point(-20, Layout.RopeTop);
        var p1 = new Point(width / 2, Layout.RopeTop + 2 * Layout.Sag(width));
        var p2 = new Point(width + 20, Layout.RopeTop);
        double t0 = Math.Clamp((zone.Left + 20) / (width + 40), 0, 1);
        double t1 = Math.Clamp((zone.Right + 20) / (width + 40), t0, 1);

        static Point At(Point a, Point b, Point c, double t)
        {
            double u = 1 - t;
            return new Point(
                u * u * a.X + 2 * u * t * b.X + t * t * c.X,
                u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
        }

        static Point Derivative(Point a, Point b, Point c, double t)
        {
            double u = 1 - t;
            return new Point(
                2 * (u * (b.X - a.X) + t * (c.X - b.X)),
                2 * (u * (b.Y - a.Y) + t * (c.Y - b.Y)));
        }

        var start = At(p0, p1, p2, t0);
        var end = At(p0, p1, p2, t1);
        var tangent = Derivative(p0, p1, p2, t0);
        double span = t1 - t0;
        var control = new Point(
            start.X + span * tangent.X / 2,
            start.Y + span * tangent.Y / 2);
        var figure = new PathFigure { StartPoint = start, IsFilled = false };
        figure.Segments.Add(new QuadraticBezierSegment(control, end, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        _dropBand.Data = geometry;
    }

    // MARK: Reordering by hand

    public void BeginReorder(PeggedControl card)
    {
        if (_isSearchEditing) CloseSearch();
        _held = card;
        SetZIndex(card, HeldZ);
        card.SetHovering(true);
        StartTicking();
    }

    /// <summary>The held card follows the pointer; the others make room.</summary>
    public void ReorderTo(PeggedControl card, double centerX)
    {
        if (_held != card) return;
        var band = CardBand();
        centerX = Layout.ClampCardCenterX(centerX, band.Left, band.Right);
        card.SetTargetX(centerX, snap: true);
        RebuildHitCache();
        int from = -1, liveCount = 0;
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Item.Falling) continue;
            if (_cards[i] == card) from = liveCount;
            liveCount++;
        }
        if (from < 0) return;
        // Which slot is the pointer over now? The nearest one.
        int to = 0;
        double best = double.MaxValue;
        for (int i = 0; i < liveCount; i++)
        {
            double d = Math.Abs(centerX - Layout.SpreadX(i, liveCount, _width, band.Left, band.Right));
            if (d < best) { best = d; to = i; }
        }
        if (to != from) _line.Move(card.Item.Id, to);
        StartTicking();
    }

    public void EndReorder(PeggedControl card)
    {
        if (_held != card) return;
        _held = null;
        ApplyBaseZOrder(card);
        Relayout(snap: false);
        StartTicking();
    }

    // MARK: Moving the line itself

    /// <summary>The menu was asked for, from the tag or a right click on the rope.</summary>
    public event Action? MenuRequested;

    private const double TagWidth = 30, TagHeight = 22;
    private const double TagIconSize = 12;

    private Grid BuildTag()
    {
        var g = new Grid { Width = Layout.CardWidth, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = Strings.Menu, Background = null };
        _tagPeg = Pegs.CreateLineClip(seed: 1);
        _tagPeg.Margin = new Thickness(0);
        _tagChip = Glass.Chip(TagHeight);
        _tagChip.Width = TagWidth;
        _tagChip.HorizontalAlignment = HorizontalAlignment.Center;
        _tagChip.VerticalAlignment = VerticalAlignment.Top;
        _tagChip.Margin = new Thickness(0, Layout.PinHeight * 0.75 - 5, 0, 0);
        _tagChip.RenderTransformOrigin = new Point(0.5, 0);
        _tagChip.RenderTransform = new RotateTransform(-3);
        _tagGlyph = Glass.MenuGlyph(TagIconSize, Theme.Freeze(new SolidColorBrush(Theme.Primary)));
        _tagGlyph.VerticalAlignment = VerticalAlignment.Center;
        _tagGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _tagChip.Child = _tagGlyph;
        g.Children.Add(_tagChip);
        g.Children.Add(_tagPeg);
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; MenuRequested?.Invoke(); };
        g.MouseRightButtonUp += (_, e) => { e.Handled = true; MenuRequested?.Invoke(); };
        return g;
    }

    /// <summary>Where the tag hangs, for hit testing.</summary>
    public Rect? TagRect => _width > 0 && _tag.Opacity > 0
        ? new Rect(GetLeft(_tag) + (Layout.CardWidth - TagWidth) / 2 - 4, GetTop(_tag), TagWidth + 8, Layout.PinHeight * 0.75 + TagHeight + 2)
        : null;

    public event Action? RopeDragStarted;
    public event Action? RopeDragEnded;
    public bool RopeDragging => _ropeDrag;

    /// <summary>Whether a canvas point lies on the rope, within a comfortable band, and not over a photo.</summary>
    public bool IsOverRope(Point p)
    {
        if (_width <= 0 || p.X < 0 || p.X > _width) return false;
        if (ClearButtonRect is { } clear && clear.Contains(p)) return false;
        if (NoteQuickAddRect is { } quickNote && quickNote.Contains(p)) return false;
        if (ClipboardWalletRect is { } wallet && wallet.Contains(p)) return false;
        if (IsOverCard(p)) return false;
        return Math.Abs(p.Y - Layout.RopeY(p.X, _width)) <= 9;
    }

    private void OnRopeDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isSearchEditing) CloseSearch();
        if (ClearButtonRect is { } clear && clear.Contains(e.GetPosition(this))) return;
        if (NoteQuickAddRect is { } quickNote && quickNote.Contains(e.GetPosition(this))) return;
        if (!IsOverRope(e.GetPosition(this))) return;
        e.Handled = true;
        _ropeDrag = true;
        CaptureMouse();
        RopeDragStarted?.Invoke();
    }

    private void OnRopeUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_ropeDrag) return;
        e.Handled = true;
        _ropeDrag = false;
        ReleaseMouseCapture();
        UpdateQuickNoteState();
        RopeDragEnded?.Invoke();
    }

    /// <summary>Whether a canvas point is over a currently painted card.</summary>
    public bool IsOverCard(Point p, double padding = 4)
    {
        for (int i = 0; i < _hitRects.Count; i++)
        {
            var rect = _hitRects[i];
            rect.Inflate(padding, padding);
            if (rect.Contains(p)) return true;
        }
        return false;
    }

    /// <summary>The hint counts as well while the line is empty, so it can be clicked.</summary>
    public Rect? HintRect => _line.LiveCount == 0 && _hint.Opacity > 0.5
        ? new Rect(GetLeft(_hint), GetTop(_hint), _hint.ActualWidth, _hint.ActualHeight)
        : null;

    /// <summary>The undo pill, while a sweep can still be called back.</summary>
    public Rect? UndoRect => _undo.Opacity > 0.5
        ? _undo.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, _undo.ActualWidth, _undo.ActualHeight))
        : null;

    /// <summary>True while one sweep can still be called back, which holds the line open.</summary>
    public bool IsUndoShowing => _undo.Opacity > 0.5;

    private void OnSwept(IReadOnlyList<Line.SweptCard> batch)
    {
        _pendingSweep = batch;
        _undoCount.Text = string.Format(Strings.SweptCards, batch.Count);
        Typography.ApplyText(_undoCount);
        Typography.BlockLineHeight(_undoCount, Typography.SmallLineHeight);
        _undoCount.FontFamily = Typography.Ui;
        _undoCount.FontSize = Typography.SmallSize;
        _undoCount.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        _undoAction.FontFamily = Typography.Ui;
        _undoAction.FontSize = Typography.SmallSize;
        Typography.ApplyText(_undoAction);
        Typography.BlockLineHeight(_undoAction, Typography.SmallLineHeight);
        InvalidateArrange();
        _undoTimer.Stop();
        _undoTimer.Start();
        _undo.IsHitTestVisible = true;
        _undoOpacity.Go(1, 0.18);
        StartTicking();
    }

    private void HideUndo()
    {
        _pendingSweep = Array.Empty<Line.SweptCard>();
        _undo.IsHitTestVisible = false;
        _undoOpacity.Go(0, 0.24);
        StartTicking();
    }

    private void UndoSweep()
    {
        if (_pendingSweep.Count == 0) return;
        var batch = _pendingSweep;
        HideUndo();
        _line.Restore(batch);
    }

    private void RebuildHitCache()
    {
        _hitCards.Clear();
        _hitRects.Clear();
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            if (!card.VisibleForHit) continue;
            _hitCards.Add(card);
            _hitRects.Add(card.CardRect);
        }
    }

    /// <summary>
    /// Gives every card a resting place in the sorted row. Earlier cards sit on
    /// top of later ones when the row overlaps, so the oldest stay in front and
    /// the newest tuck behind them.
    /// </summary>
    private void ApplyBaseZOrder(PeggedControl card)
    {
        if (card == _held) return;
        SetZIndex(card, Math.Max(2, RestingZBase - card.SortOrder));
    }

    /// <summary>Maps a card's hover lift onto its z-order so it animates over its neighbours.</summary>
    private void ApplyZOrder(PeggedControl card)
    {
        if (card == _held) { SetZIndex(card, HeldZ); return; }
        if (card.Hovering || card.ZOrderLift > 0.001)
        {
            SetZIndex(card, HoverZBase + (int)Math.Round(card.ZOrderLift * 1_000));
            return;
        }
        ApplyBaseZOrder(card);
    }

    public void UpdateHover(Point? pointInCanvas)
    {
        if (_isSearchEditing)
        {
            ResetHoverInteraction();
            Cursor = null;
            return;
        }
        if (_line.MenuOpen || _line.DraggingId is not null)
        {
            ResetHoverInteraction();
            return;
        }

        PeggedControl? over = null;
        if (pointInCanvas is { } p)
        {
            if ((ClearButtonRect is { } clear && clear.Contains(p)) ||
                (NoteQuickAddRect is { } quickNote && quickNote.Contains(p)) ||
                (ClipboardWalletRect is { } wallet && wallet.Contains(p)))
            {
                pointInCanvas = null;
            }
        }
        if (pointInCanvas is { } hitPoint)
        {
            double nearest = double.MaxValue;
            int nearestZ = int.MinValue;
            for (int i = 0; i < _hitCards.Count; i++)
            {
                var card = _hitCards[i];
                var rect = _hitRects[i];
                rect.Inflate(6, 6);
                if (!rect.Contains(hitPoint)) continue;
                double distance = Math.Abs(hitPoint.X - card.CenterX);
                int z = GetZIndex(card);
                if (distance < nearest - 0.1 || (Math.Abs(distance - nearest) <= 0.1 && z > nearestZ))
                {
                    over = card;
                    nearest = distance;
                    nearestZ = z;
                }
            }
        }
        bool any = false;
        foreach (var c in _cards)
        {
            bool on = c == over && _line.DraggingId is null;
            if (c.Hovering != on) { c.SetHovering(on); any = true; }
        }
        TrackHoverFocus(over is { Item.Falling: false } ? over : null);
        if (any) StartTicking();
        Cursor = pointInCanvas is { } q && over is null && IsOverRope(q) ? System.Windows.Input.Cursors.SizeNS : null;
    }

    private void TrackHoverFocus(PeggedControl? over)
    {
        if (over is null)
        {
            ResetHoverCandidate();
            SetHoverFocus(null);
            return;
        }

        var id = over.Item.Id;
        if (_hoverCandidateId != id)
        {
            ResetHoverCandidate();
            SetHoverFocus(null);
            _hoverCandidateId = id;
            _hoverCandidateSince = Stopwatch.GetTimestamp();
            _hoverFocusTimer.Start();
            return;
        }

        if (_hoverFocusId == id)
        {
            _hoverFocusTimer.Stop();
            return;
        }
        double elapsed = (Stopwatch.GetTimestamp() - _hoverCandidateSince) / (double)Stopwatch.Frequency;
        if (elapsed < HoverFocusDelay.TotalSeconds) return;
        SetHoverFocus(id);
    }

    private void ResetHoverCandidate()
    {
        _hoverCandidateId = null;
        _hoverCandidateSince = 0;
        _hoverFocusTimer.Stop();
    }

    private void ResetHoverInteraction()
    {
        ResetHoverCandidate();
        SetHoverFocus(null);
        bool any = false;
        foreach (var card in _cards)
        {
            if (!card.Hovering) continue;
            card.SetHovering(false);
            any = true;
        }
        if (any) StartTicking();
    }

    private void SetHoverFocus(Guid? id)
    {
        if (_hoverFocusId == id) return;
        _hoverFocusTimer.Stop();
        _hoverFocusId = id;
        foreach (var card in _cards)
            card.SetPreviewFocused(card.Item.Id == id);
        PreviewTargetChanged?.Invoke();
        HoverFocusChanged?.Invoke(id);
    }

    /// <summary>Lays the cards out again right now, snapping them into place. For offscreen rendering.</summary>
    public void RelayoutNow() => Relayout(snap: true);

    /// <summary>Steps every animation forward without a window, for offscreen rendering.</summary>
    public void Advance(double seconds, double step = 1.0 / 60)
    {
        _ticking = false;
        CompositionTarget.Rendering -= OnRendering;
        for (double t = 0; t < seconds; t += step) StepAll(step);
        StepIdleClearButton(seconds);
    }

    /// <summary>
    /// The idle clear button loops through its frames. The app does that on a
    /// dispatcher timer; a caller that steps the animation itself would never
    /// pump that timer, so it advances the frames here instead.
    /// </summary>
    private void StepIdleClearButton(double seconds)
    {
        if (!_clearButtonStill || _clearRunning) return;
        if (_clearAnimation.Frames.Count <= 1) return;
        if (_clearButton.Visibility != Visibility.Visible) return;
        _clearIdleAge += seconds;
        bool changed = false;
        while (_clearIdleAge >= ClearFrameDelay(_clearButtonFrame).TotalSeconds)
        {
            _clearIdleAge -= ClearFrameDelay(_clearButtonFrame).TotalSeconds;
            int count = _clearAnimation.Frames.Count;
            _clearButtonFrame = (_clearButtonFrame + 1) % Math.Max(1, count);
            changed = true;
        }
        if (changed) ApplyClearButtonFrame();
    }

    private bool StepAll(double dt)
    {
        bool active = false;
        bool moved = false;
        if (_usingSpring)
        {
            _revealSpring.Step(dt);
            _reveal.Y = _revealSpring.Value;
            active |= !_revealSpring.Resting;
            moved |= !_revealSpring.Resting;
        }
        else
        {
            _hideTween.Step(dt);
            _reveal.Y = _hideTween.Value;
            active |= !_hideTween.Done;
            moved |= !_hideTween.Done;
        }
        _hintOpacity.Step(dt);
        _hint.Opacity = _hintOpacity.Value;
        active |= !_hintOpacity.Done;
        _undoOpacity.Step(dt);
        _undo.Opacity = _undoOpacity.Value;
        active |= !_undoOpacity.Done;
        _dropOpacity.Step(dt);
        _dropOverlay.Opacity = _dropOpacity.Value;
        active |= !_dropOpacity.Done;
        _searchWidth.Step(dt);
        active |= !_searchWidth.Done;
        PlaceSearch();
        active |= StepClearAnimation(dt);
        _clipboardWallet.Tick(dt);
        active |= _clipboardWallet.Active;
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            bool wasActive = card.Active;
            card.Tick(dt);
            active |= card.Active;
            if (wasActive || card.Active)
            {
                moved = true;
                ApplyZOrder(card);
            }
        }
        if (!moved) return active;
        RebuildHitCache();
        // Re-evaluate after the cards have moved, so the front-most card keeps
        // the pointer as the row settles.
        if (IsMouseOver) UpdateHover(System.Windows.Input.Mouse.GetPosition(this));
        return active;
    }

    /// <summary>Keeps the idle button's GIF playing on its own timer, not on the render clock.</summary>
    private void UpdateClearButtonAnimation()
    {
        bool play = !_clearButtonStill && _revealed && Settings.Current.OneClickClear &&
                    _clearButton.Visibility == Visibility.Visible &&
                    _clearAnimation.Frames.Count > 1;
        if (!play)
        {
            _clearButtonTimer.Stop();
            if (!_revealed || !Settings.Current.OneClickClear)
            {
                _clearButtonFrame = 0;
            }
            return;
        }
        if (_clearButtonTimer.IsEnabled) return;
        _clearButtonTimer.Interval = ClearFrameDelay(_clearButtonFrame);
        _clearButtonTimer.Start();
    }

    private void AdvanceClearButtonFrame()
    {
        if (_clearAnimation.Frames.Count <= 1)
        {
            _clearButtonTimer.Stop();
            return;
        }
        _clearButtonFrame = (_clearButtonFrame + 1) % _clearAnimation.Frames.Count;
        ApplyClearButtonFrame();
        // Each frame keeps its own delay, so the GIF plays at its authored speed.
        _clearButtonTimer.Interval = ClearFrameDelay(_clearButtonFrame);
    }

    private bool StepClearAnimation(double dt)
    {
        if (!_clearRunning) return false;

        if (_clearAnimation.Frames.Count > 0)
        {
            _clearFrameAge += dt;
            while (_clearFrameAge >= ClearFrameDelay(_clearFrame).TotalSeconds)
            {
                _clearFrameAge -= ClearFrameDelay(_clearFrame).TotalSeconds;
                _clearFrame = (_clearFrame + 1) % _clearAnimation.Frames.Count;
                if (_clearAnimation.Frames.Count > 1) ApplyClearRunnerFrame();
            }
        }

        _clearElapsed = Math.Min(_clearDuration, _clearElapsed + dt);
        double progress = _clearDuration <= 0 ? 1 : Math.Clamp(_clearElapsed / _clearDuration, 0, 1);
        double x = _clearStartX + (_clearEndX - _clearStartX) * progress;
        PlaceClearRunner(x);

        for (int i = 0; i < _clearTargets.Count; i++)
        {
            var target = _clearTargets[i];
            if (target.dropped) continue;
            bool reached = _clearLeftToRight ? x >= target.x : x <= target.x;
            if (!reached) continue;
            _clearTargets[i] = (target.id, target.x, true);
            _line.Drop(target.id, quietly: true);
        }

        if (progress < 1) return true;

        if (!_clearReturning)
        {
            _clearReturning = true;
            _clearStartX = _clearLeftToRight ? -28 : _width + 28;
            _clearEndX = _clearHomeX > 0 ? _clearHomeX : Math.Max(32, _width - 42);
            _clearElapsed = 0;
            _clearDuration = Math.Max(0.4, Math.Abs(_clearEndX - _clearStartX) / 900.0);
            PlaceClearRunner(_clearStartX);
            return true;
        }

        _clearRunning = false;
        _clearReturning = false;
        _clearRunner.Visibility = Visibility.Hidden;
        _clearTargets.Clear();
        _line.ReleaseSaveHold();
        _line.AnnounceSweep(_clearSweep);
        _clearSweep = Array.Empty<Line.SweptCard>();
        UpdateClearButtonState();
        UpdateQuickNoteState();
        ClearAnimationFinished?.Invoke();
        return false;
    }

    private TimeSpan ClearFrameDelay(int frame)
    {
        if (_clearAnimation.Delays.Count == 0) return TimeSpan.FromMilliseconds(30);
        var delay = _clearAnimation.Delays[Math.Abs(frame) % _clearAnimation.Delays.Count];
        return delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(30) : delay;
    }

    // MARK: The clock

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _clock.Restart();
        _lastTime = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTime, 0, 1.0 / 20);
        _lastTime = now;
        if (dt <= 0) return;

        if (!StepAll(dt))
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}
