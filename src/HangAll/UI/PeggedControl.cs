using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using HangAll.Core;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>
/// One photo with its clothespin. All the charm lives here: it drops onto
/// the line, swings, sways with the breeze, and reacts to the pointer.
/// Click copies, press and hold opens the editor, drag takes it out, the
/// cross in the corner discards.
/// </summary>
public sealed class PeggedControl : Canvas
{
    public Pegged Item { get; }
    private readonly Line _line;

    // Motion
    private readonly Spring _swing = Spring.Raw(46, 2.6);                       // degrees
    private readonly Spring _arrive = Spring.FromResponse(0.42, 0.72, -46);     // y offset while dropping in
    private readonly Spring _hoverScale = Spring.FromResponse(0.3, 0.6, 1);
    private readonly Tween _pressScale = new(1);
    private readonly Spring _x = Spring.FromResponse(0.55, 0.78);               // centre x on the canvas
    private readonly Tween _opacity = new(0);
    private readonly Tween _zLift = new(0);                                     // 0 resting, 1 lifted to the front
    private readonly Tween _cardOpacity = new(1);
    private readonly Tween _crossOpacity = new(0);
    private readonly Tween _crossScale = new(0.6);
    private readonly Tween _copiedOpacity = new(0);
    private readonly Tween _copiedOffset = new(-4);
    private readonly Tween _shadow = new(0);                                    // 0 resting, 1 hovering

    // Visuals
    private readonly RotateTransform _rotate = new();
    private readonly TranslateTransform _drop = new();
    private readonly ScaleTransform _cardScale = new();
    private readonly Grid _cardHost;
    private readonly Border _frame;
    private readonly Border _photoEdge;
    private readonly Border _outline;
    private readonly Border _selectionRing;
    private readonly System.Windows.Shapes.Path _noteOutline;
    private readonly Canvas _taskOverlay;
    private readonly Image _image;
    private readonly TextBlock _fileName;
    private readonly Border _cross;
    private readonly Border _pen;
    private readonly Border _trash;
    private readonly Border _crossHit = null!;
    private readonly Border _penHit = null!;
    private readonly Border _trashHit = null!;
    private readonly Border _copied;
    private readonly TranslateTransform _copiedTranslate = new();
    private readonly DropShadowEffect _shadowEffect;
    private Grid _pin = null!;
    private bool _reordering;
    private bool _filtered;
    private double _grabOffsetX;
    private Point _downInCanvas;
    private readonly TextBlock _copiedText;
    private readonly TextBlock _tipName = new();
    private readonly TextBlock _tipMeta = new();
    private Size _cardSize;

    // Input
    private Point? _downPoint;
    private bool _startedDrag, _didLongPress, _hovering, _previewFocused;
    private ModifierKeys _clickModifiers;
    private readonly DispatcherTimer _holdTimer;
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(450);

    public bool Hovering => _hovering;
    public bool Selected { get; private set; }
    public bool Reordering => _reordering;
    public bool Filtered => _filtered;
    public bool Active => !_swing.Resting || !_arrive.Resting || !_hoverScale.Resting || !_pressScale.Done || !_x.Resting
                          || !_opacity.Done || !_zLift.Done || !_cardOpacity.Done || !_crossOpacity.Done
                          || !_copiedOpacity.Done || !_shadow.Done;
    /// <summary>Whether the pointer can reach this card: on the row and actually painted.</summary>
    public bool VisibleForHit =>
        !Filtered && !Item.Falling && !Item.Flying && _opacity.Value > 0.05;

    /// <summary>How far this card has risen towards the front, 0 to 1. The canvas maps it to a z-order.</summary>
    public double ZOrderLift => _zLift.Value;
    /// <summary>The card's place in the sorted view, used to order the resting row.</summary>
    public int SortOrder { get; set; }

    public PeggedControl(Pegged item, Line line)
    {
        Item = item;
        _line = line;
        Width = Layout.CardWidth;
        ClipToBounds = false;
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        Background = null;

        // The whole hanging thing rotates about the top of the clip, where it grips the line.
        var group = new TransformGroup();
        group.Children.Add(_rotate);
        group.Children.Add(_drop);
        RenderTransform = group;
        _rotate.CenterX = Layout.CardWidth / 2;
        _rotate.CenterY = 0;

        // Card
        _cardSize = Layout.CardSize(item);
        _shadowEffect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 5, BlurRadius = 20, Opacity = 0.18 };
        _cardHost = new Grid
        {
            Width = _cardSize.Width, Height = _cardSize.Height,
            RenderTransform = _cardScale,
            Effect = _shadowEffect,
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow,
        };
        _cardScale.CenterX = _cardSize.Width / 2;
        _cardScale.CenterY = 0;

        _frame = Glass.Frame(Layout.FrameRadius);
        _image = new Image
        {
            Source = item.Thumb,
            // A real thumbnail fills the glass; a Windows file icon keeps its
            // own square shape, so it is never stretched wide and flattened.
            Stretch = item.IsFile || item.IsIcon ? Stretch.Uniform : Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = item.IsFile ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = item.IsFile
                ? new Thickness(0, Layout.FileIconTop, 0, 0)
                : new Thickness(Layout.FrameInset),
            Width = item.IsFile ? Layout.FileIconSize : double.NaN,
            Height = item.IsFile ? Layout.FileIconSize : double.NaN,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        ApplyPhotoClip();
        _photoEdge = new Border
        {
            Margin = new Thickness(Layout.FrameInset),
            CornerRadius = new CornerRadius(Layout.FrameRadius - Layout.FrameInset),
            BorderThickness = new Thickness(0.5),
            BorderBrush = Theme.Freeze(new SolidColorBrush(Theme.PhotoEdge)),
            IsHitTestVisible = false,
            Visibility = item.IsFile ? Visibility.Collapsed : Visibility.Visible,
        };
        _outline = Glass.Outline(Layout.FrameRadius);
        _selectionRing = new Border
        {
            CornerRadius = new CornerRadius(Layout.FrameRadius + 2),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Theme.Freeze(new SolidColorBrush(Theme.Primary)),
            Margin = new Thickness(-2.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _noteOutline = new System.Windows.Shapes.Path
        {
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _taskOverlay = new Canvas();
        _fileName = new TextBlock
        {
            Text = System.IO.Path.GetFileName(item.Path),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = Layout.FileNameHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Layout.FileNamePadding, Layout.FileNameTop, Layout.FileNamePadding, 0),
            IsHitTestVisible = false,
            Visibility = item.IsFile ? Visibility.Visible : Visibility.Collapsed,
        };
        _fileName.FontFamily = Typography.ForText(_fileName.Text);
        _fileName.FontSize = Typography.SmallSize;
        Typography.ApplyText(_fileName);
        Typography.BlockLineHeight(_fileName, Typography.SmallLineHeight);
        _cardHost.Children.Add(_frame);
        _cardHost.Children.Add(_image);
        _cardHost.Children.Add(_taskOverlay);
        _cardHost.Children.Add(_photoEdge);
        _cardHost.Children.Add(_outline);
        _cardHost.Children.Add(_selectionRing);
        _cardHost.Children.Add(_noteOutline);
        _cardHost.Children.Add(_fileName);
        if (Notes.IsNote(item.Path))
        {
            // A note is the paper itself: no glass, no inset.
            _frame.Visibility = _photoEdge.Visibility = _outline.Visibility = Visibility.Collapsed;
            _fileName.Visibility = Visibility.Collapsed;
            _image.Margin = new Thickness(0);
        }
        else if (item.IsVideo)
        {
            // A recording wears a play badge, like the ones Tendedero hangs.
            _cardHost.Children.Add(Glass.PlayBadge(22));
        }
        ApplyCardVisuals();

        // The discard cross in the top left corner.
        _cross = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3),
            BorderThickness = new Thickness(0.75),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Discard,
            Child = Glass.Cross(8, Theme.Freeze(new SolidColorBrush(Theme.Primary))),
        };
        Glass.Paint(_cross);
        _cross.MouseLeftButtonDown += (_, e) => { e.Handled = true; _downPoint = null; _line.Discard(Item.Id); };
        _crossHit = HitPad(_cross, new Thickness(1), () => _line.Discard(Item.Id));
        _cardHost.Children.Add(_crossHit);
        _cardHost.Children.Add(_cross);

        // The pen in the top right corner: mark the photo up.
        _pen = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 27, 0),
            BorderThickness = new Thickness(0.75),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Cursor = Cursors.Hand,
            ToolTip = Strings.MarkUp,
            Child = Morphicons.CreatePath(
                Morphicon.Pencil,
                10,
                1.4,
                Theme.Freeze(new SolidColorBrush(Theme.Primary))),
        };
        Glass.Paint(_pen);
        _pen.MouseLeftButtonDown += (_, e) => { e.Handled = true; _downPoint = null; _line.Edit(Item.Id); };
        _penHit = HitPad(_pen, new Thickness(0, 1, 25, 0), () => _line.Edit(Item.Id));
        _cardHost.Children.Add(_penHit);
        _cardHost.Children.Add(_pen);
        // Nothing to mark up on a recording; the card keeps just the bin and the cross.
        if (item.IsVideo || item.IsFile)
        {
            _pen.Visibility = Visibility.Collapsed;
            _penHit.Visibility = Visibility.Collapsed;
        }

        // The bin in the top right corner: remove the file from the line and
        // send it to the Windows Recycle Bin.
        _trash = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3),
            BorderThickness = new Thickness(0.75),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Cursor = Cursors.Hand,
            ToolTip = Strings.MoveToRecycleBin,
            Child = Glass.Trash(11, Theme.Freeze(new SolidColorBrush(Theme.Primary))),
        };
        Glass.Paint(_trash);
        _trash.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _downPoint = null;
            _line.Trash(Item.Id);
        };
        _trashHit = HitPad(_trash, new Thickness(1), () => _line.Trash(Item.Id));
        _cardHost.Children.Add(_trashHit);
        _cardHost.Children.Add(_trash);

        // "Copied", under the card.
        var copiedRow = new StackPanel { Orientation = Orientation.Horizontal };
        copiedRow.Children.Add(Glass.Check(10, Theme.Freeze(new SolidColorBrush(Theme.Primary))));
        _copiedText = new TextBlock
        {
            Text = Strings.Copied, Margin = new Thickness(6, 0, 0, 0),
            FontFamily = Typography.ForText(Strings.Copied), FontSize = Typography.SmallSize, FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary)), VerticalAlignment = VerticalAlignment.Center,
        };
        copiedRow.Children.Add(_copiedText);
        _copied = new Border
        {
            Child = copiedRow, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0.75), Opacity = 0, IsHitTestVisible = false,
            RenderTransform = _copiedTranslate,
        };
        Glass.Paint(_copied);

        Children.Add(_cardHost);
        Children.Add(_copied);
        PaintPin();

        // A quiet tooltip: the file, its size and how long it has been hanging.
        var tip = new StackPanel();
        _tipName.FontWeight = FontWeights.SemiBold;
        _tipMeta.Opacity = 0.7;
        _tipMeta.Margin = new Thickness(0, 2, 0, 0);
        tip.Children.Add(_tipName);
        tip.Children.Add(_tipMeta);
        var toolTip = new ToolTip { Content = tip, FlowDirection = Strings.Flow };
        toolTip.Opened += (_, _) => RefreshTip();
        ToolTipService.SetInitialShowDelay(_cardHost, 900);
        ToolTipService.SetShowDuration(_cardHost, 6000);
        ToolTipService.SetPlacement(_cardHost, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        _cardHost.ToolTip = toolTip;
        SetLeft(_cardHost, (Layout.CardWidth - _cardSize.Width) / 2);
        SetTop(_cardHost, Layout.CardOffsetBelowTop);
        PlaceCopied();

        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoldDuration };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_downPoint is null || _startedDrag) return;
            _didLongPress = true;
            SetPressed(false);
            _line.Edit(Item.Id);
        };

        _cardHost.MouseLeftButtonDown += OnMouseDown;
        _cardHost.MouseMove += OnMouseMove;
        _cardHost.MouseLeftButtonUp += OnMouseUp;
        _cardHost.MouseRightButtonUp += OnRightClick;
        _cardHost.LostMouseCapture += (_, _) =>
        {
            if (!_startedDrag) EndPress();
            if (_reordering) { _reordering = false; (Parent as LineCanvas)?.EndReorder(this); }
        };

        RebuildNoteInteraction();
        Opacity = 0;
        Arrive();
    }

    private void ApplyPhotoClip()
    {
        var photo = Layout.PhotoSize(Item.PixelWidth, Item.PixelHeight);
        double r = Layout.FrameRadius - Layout.FrameInset;
        if (Notes.IsNote(Item.Path) || Item.IsFile || Item.IsIcon) { _image.Clip = null; return; }
        _image.Clip = new RectangleGeometry(new Rect(0, 0, photo.Width, photo.Height), r, r);
    }

    /// <summary>
    /// Keeps a file card visually separate from a photo or recording: the
    /// source image sits at desktop-icon size and the complete file name sits
    /// underneath it. Stills and recordings retain their existing crop.
    /// </summary>
    /// <summary>
    /// A transparent ring that widens a corner badge's hit area to 24 DIP without
    /// touching its 20 DIP artwork. It tracks the badge's opacity, and WPF does not
    /// hit-test an element at zero, so a faded-out badge never becomes a phantom target.
    /// </summary>
    private Border HitPad(Border badge, Thickness margin, Action action)
    {
        var pad = new Border
        {
            Width = 24,
            Height = 24,
            Margin = margin,
            HorizontalAlignment = badge.HorizontalAlignment,
            VerticalAlignment = badge.VerticalAlignment,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Opacity = badge.Opacity,
        };
        pad.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _downPoint = null;
            action();
        };
        return pad;
    }

    private void ApplyCardVisuals()
    {
        string name = System.IO.Path.GetFileName(Item.Path);
        _fileName.Text = name;
        _fileName.FontFamily = Typography.ForText(name);
        _fileName.FlowDirection = FileNameFlow(name);
        // Paint the name from the live theme: the card outlives a light/dark
        // switch, so an inherited brush would keep the old colour.
        _fileName.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));

        if (Notes.IsNote(Item.Path))
        {
            _frame.Visibility = _photoEdge.Visibility = _outline.Visibility = Visibility.Collapsed;
            _fileName.Visibility = Visibility.Collapsed;
            _image.Width = _image.Height = double.NaN;
            _image.Margin = new Thickness(0);
            _image.HorizontalAlignment = HorizontalAlignment.Center;
            _image.VerticalAlignment = VerticalAlignment.Center;
            _image.Stretch = Stretch.Fill;
            _image.Clip = null;
            return;
        }

        _frame.Visibility = _outline.Visibility = Visibility.Visible;
        if (Item.IsFile)
        {
            _photoEdge.Visibility = Visibility.Collapsed;
            _fileName.Visibility = Visibility.Visible;
            _image.Width = _image.Height = Layout.FileIconSize;
            _image.HorizontalAlignment = HorizontalAlignment.Center;
            _image.VerticalAlignment = VerticalAlignment.Top;
            _image.Stretch = Stretch.Uniform;
            _image.Clip = null;
            ApplyFileMetrics();
            return;
        }

        _photoEdge.Visibility = Visibility.Visible;
        _fileName.Visibility = Visibility.Collapsed;
        _image.Width = _image.Height = double.NaN;
        _image.Margin = new Thickness(Layout.FrameInset);
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _image.Stretch = Item.IsIcon ? Stretch.Uniform : Stretch.Fill;
        ApplyPhotoClip();
    }

    private void PlaceCopied()
    {
        _copied.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var s = _copied.DesiredSize;
        SetLeft(_copied, (Layout.CardWidth - s.Width) / 2);
        SetTop(_copied, Layout.CardOffsetBelowTop + _cardSize.Height + 16 - s.Height / 2);
    }

    /// <summary>The thumbnail changed, for example after an edit.</summary>
    public void Refresh()
    {
        _image.Source = Item.Thumb;
        ApplyCardVisuals();
        PaintPin();
        RebuildCardSize();
        ApplyPhotoClip();
        if (!Item.Flying) Nudge(2.2);
    }

    /// <summary>Resizes the card to the current type scale and re-centres it.</summary>
    private void RebuildCardSize()
    {
        var size = Layout.CardSize(Item);
        if (size != _cardSize)
        {
            _cardSize = size;
            _cardHost.Width = size.Width; _cardHost.Height = size.Height;
            _cardScale.CenterX = size.Width / 2;
            SetLeft(_cardHost, (Layout.CardWidth - size.Width) / 2);
            PlaceCopied();
        }
        RebuildNoteInteraction();
    }

    private void RebuildNoteInteraction()
    {
        _taskOverlay.Children.Clear();
        _noteOutline.Data = null;
        _noteOutline.Visibility = Visibility.Collapsed;
        bool isNote = Notes.IsNote(Item.Path);
        _taskOverlay.IsHitTestVisible = isNote;
        _selectionRing.Visibility = Visibility.Collapsed;
        if (!isNote) return;

        _noteOutline.Data = Notes.SheetGeometry(_cardSize.Width, _cardSize.Height);
        _noteOutline.Stroke = Theme.Freeze(new SolidColorBrush(Theme.Primary));

        var note = Notes.Get(Item.Path);
        if (note is null) return;
        double xScale = _cardSize.Width / Notes.W;
        double yScale = _cardSize.Height / Notes.H;
        foreach (var area in Notes.TaskHitAreas(note))
        {
            int taskIndex = area.TaskIndex;
            var region = new Border
            {
                Width = area.Bounds.Width * xScale,
                Height = area.Bounds.Height * yScale,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = Strings.ToggleTaskStatus,
                SnapsToDevicePixels = true,
            };
            Canvas.SetLeft(region, area.Bounds.X * xScale);
            Canvas.SetTop(region, area.Bounds.Y * yScale);

            bool armed = false;
            region.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                armed = true;
                region.CaptureMouse();
            };
            region.MouseLeftButtonUp += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                bool invoke = armed && new Rect(0, 0, region.ActualWidth, region.ActualHeight)
                    .Contains(e.GetPosition(region));
                armed = false;
                region.ReleaseMouseCapture();
                if (invoke) _line.ToggleNoteTask(Item.Id, taskIndex);
            };
            region.LostMouseCapture += (_, _) => armed = false;
            _taskOverlay.Children.Add(region);
        }
        RefreshSelectionRing();
    }

    /// <summary>Applies a new UI type scale without rebuilding the card.</summary>
    public void RefreshTypography()
    {
        _copiedText.FontFamily = Typography.ForText(Strings.Copied);
        _copiedText.FontSize = Typography.SmallSize;
        Typography.ApplyText(_copiedText);
        Typography.BlockLineHeight(_copiedText, Typography.SmallLineHeight);
        _fileName.FontFamily = Typography.ForText(_fileName.Text);
        _fileName.FontSize = Typography.SmallSize;
        _fileName.FlowDirection = FileNameFlow(_fileName.Text);
        _fileName.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        Typography.ApplyText(_fileName);
        Typography.BlockLineHeight(_fileName, Typography.SmallLineHeight);
        _fileName.MaxHeight = Layout.FileNameHeight;
        ApplyFileMetrics();
        RebuildCardSize();
        PlaceCopied();
    }

    /// <summary>
    /// Balances a file card: the fixed two-line name slot leaves spare room for
    /// a one-line name, so the icon and name move down together and the block
    /// sits centred in the card instead of hugging the top.
    /// </summary>
    private void ApplyFileMetrics()
    {
        if (!Item.IsFile) return;
        _fileName.Measure(new Size(
            Math.Max(0, Layout.FileCardWidth - Layout.FileNamePadding * 2),
            double.PositiveInfinity));
        double iconTop = Layout.FileIconTopFor(_fileName.DesiredSize.Height);
        _image.Margin = new Thickness(0, iconTop, 0, 0);
        _fileName.Margin = new Thickness(
            Layout.FileNamePadding,
            iconTop + Layout.FileIconSize + Layout.FileNameGap,
            Layout.FileNamePadding,
            0);
    }

    /// <summary>Repaints shared card surfaces after a light/dark theme switch.</summary>
    public void RefreshTheme()
    {
        var primary = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        Glass.Paint(_frame);
        Glass.Paint(_cross);
        Glass.Paint(_pen);
        Glass.Paint(_trash);
        Glass.Paint(_copied);
        _outline.BorderBrush = Theme.Freeze(new SolidColorBrush(Theme.GlassOutline));
        _selectionRing.BorderBrush = primary;
        _noteOutline.Stroke = primary;
        _photoEdge.BorderBrush = Theme.Freeze(new SolidColorBrush(Theme.PhotoEdge));
        _fileName.Foreground = primary;
        _copiedText.Foreground = primary;
        PaintGlyph(_cross.Child, primary);
        PaintGlyph(_pen.Child, primary);
        PaintGlyph(_trash.Child, primary);
        PaintGlyph(_copied.Child, primary);
        PaintPin();
        ApplyPhotoClip();
    }

    /// <summary>Reapplies language-sensitive text and tooltips without rebuilding the card.</summary>
    public void RefreshLocalization()
    {
        _cross.ToolTip = Strings.Discard;
        _pen.ToolTip = Strings.MarkUp;
        _trash.ToolTip = Strings.MoveToRecycleBin;
        foreach (UIElement child in _taskOverlay.Children)
            if (child is Border region) region.ToolTip = Strings.ToggleTaskStatus;
        if (_cardHost.ToolTip is ToolTip tip) tip.FlowDirection = Strings.Flow;
        _copiedText.Text = _line.CopiedLabel;
        _copiedText.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        RefreshTypography();
    }

    private static void PaintGlyph(object? content, Brush brush)
    {
        if (content is System.Windows.Shapes.Shape shape) shape.Stroke = brush;
        if (content is TextBlock text) text.Foreground = brush;
        if (content is Panel panel)
            foreach (UIElement child in panel.Children) PaintGlyph(child, brush);
    }

    private static FlowDirection FileNameFlow(string name) =>
        name.Any(c => c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F')
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    /// <summary>A stable number for this photo, so a mixed peg keeps its colour.</summary>
    private int Seed => Item.Path.Aggregate(17, (h, c) => h * 31 + c);

    private void PaintPin()
    {
        if (_pin is not null) Children.Remove(_pin);
        _pin = Pegs.Create(seed: Seed, pinned: Item.Pinned);
        Children.Add(_pin);
        SetZIndex(_pin, 3);
        SetLeft(_pin, (Layout.CardWidth - Layout.PinWidth) / 2);
        SetTop(_pin, 0);
    }

    private void RefreshTip()
    {
        _tipName.Text = System.IO.Path.GetFileName(Item.Path);
        // A pixel count says nothing about a document, so the file card shows
        // the size on disk instead.
        string detail = Item.IsFile ? FileSize(Item.Path) : $"{Item.PixelWidth} × {Item.PixelHeight}";
        var parts = new[] { detail, Age(Item.HungAt), Item.Pinned ? Strings.KeepOnLine : "" };
        _tipMeta.Text = string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    private static string FileSize(string path)
    {
        try
        {
            long bytes = new System.IO.FileInfo(path).Length;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
        }
        catch
        {
            return "";
        }
    }

    private static string Age(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 45) return Strings.JustNow;
        if (span.TotalMinutes < 60) return string.Format(Strings.MinutesAgo, (int)span.TotalMinutes);
        if (span.TotalHours < 24) return string.Format(Strings.HoursAgo, (int)span.TotalHours);
        return when.ToString("d MMM HH:mm");
    }

    // MARK: Layout on the line

    public void SetTargetX(double centerX, bool snap)
    {
        if (snap) _x.Snap(centerX); else _x.Target = centerX;
    }

    public double CenterX => _x.Value;
    public double TargetX => _x.Target;
    /// <summary>The rope sags, so the card follows it up and down as it moves along.</summary>
    public Func<double, double>? RopeYAt { get; set; }

    /// <summary>The card's rectangle in canvas coordinates, ignoring the tilt. Used to decide where the pointer counts.</summary>
    public Rect CardRect
    {
        get
        {
            double top = GetTop(this);
            double scale = _cardScale.ScaleX;
            double w = _cardSize.Width * scale, h = _cardSize.Height * scale;
            double x = _x.Value - w / 2;
            double y = top + Layout.CardOffsetBelowTop + _drop.Y;
            return new Rect(x, y, w, h);
        }
    }

    /// <summary>
    /// The settled card rectangle after entrance motion and horizontal springing finish.
    /// Used when an anchored preview must not jump during the first layout ticks.
    /// </summary>
    public Rect StableCardRect
    {
        get
        {
            double w = _cardSize.Width;
            double h = _cardSize.Height;
            double x = TargetX;
            double y = (RopeYAt is null ? GetTop(this) : RopeYAt(x) - Layout.PinAbove)
                       + Layout.CardOffsetBelowTop;
            return new Rect(x - w / 2, y, w, h);
        }
    }

    // MARK: Motion

    private void Arrive()
    {
        if (Item.Flying)
        {
            // A capture that flew in is already in place; the flight did the arriving.
            _arrive.Snap(0);
            _swing.Snap(0);
            _opacity.Snap(0);
            return;
        }
        _swing.Snap(16);
        _swing.Target = 0;
        _arrive.Target = 0;
        _opacity.Go(1, 0.28, Ease.OutQuad);
    }

    /// <summary>Landing after the flight: no jump, just a small sway from rest.</summary>
    public void Land()
    {
        _opacity.Go(1, 0.16, Ease.OutQuad);
        Nudge(2.2);
    }

    public void Breeze(bool now = false)
    {
        var rng = Random.Shared;
        double delay = rng.NextDouble() * 0.35;
        double degrees = 1.6 + rng.NextDouble() * 1.8;
        if (now) { Nudge(degrees); return; }
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.01, delay)) };
        t.Tick += (_, _) => { t.Stop(); Nudge(degrees); };
        t.Start();
    }

    private void Nudge(double degrees)
    {
        // A push, then the spring brings it back and lets it swing.
        _swing.Velocity += degrees * 7.5;
        _swing.Target = 0;
    }

    public void SetHovering(bool on)
    {
        if (_hovering == on) return;
        _hovering = on;
        bool dragging = _line.DraggingId == Item.Id;
        _hoverScale.Target = on ? 1.05 : 1;
        // Rising to the front is the point of the animation: the card under the
        // pointer climbs over its neighbours instead of sliding beneath them.
        _zLift.Go(on ? 1 : 0, on ? 0.16 : 0.24, on ? Ease.OutCubic : Ease.InOutSine);
        _shadow.Go(on ? 1 : 0, 0.18);
        _crossOpacity.Go(on && !dragging ? 1 : 0, 0.18);
        _crossScale.Go(on ? 1 : 0.6, 0.18);
    }

    internal void SetSelected(bool on)
    {
        Selected = on;
        RefreshSelectionRing();
    }

    internal void SetPreviewFocused(bool on)
    {
        if (_previewFocused == on) return;
        _previewFocused = on;
        RefreshSelectionRing();
    }

    private void RefreshSelectionRing()
    {
        bool visible = Selected || _previewFocused;
        if (Notes.IsNote(Item.Path))
        {
            _selectionRing.Visibility = Visibility.Collapsed;
            _noteOutline.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _noteOutline.Visibility = Visibility.Collapsed;
        _selectionRing.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetFiltered(bool on)
    {
        if (_filtered == on) return;
        _filtered = on;
        if (on) SetHovering(false);
        Visibility = Filtered || Item.Falling || Item.Flying || _opacity.Value < 0.01
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    public void StateChanged()
    {
        bool copied = _line.CopiedId == Item.Id;
        if (copied) _copiedText.Text = _line.CopiedLabel;
        bool dragging = _line.DraggingId == Item.Id;
        if (copied && _copiedOpacity.Target < 1) { _copiedOpacity.Go(1, 0.2); _copiedOffset.Go(0, 0.2); Nudge(3); }
        if (!copied && _copiedOpacity.Target > 0) { _copiedOpacity.Go(0, 0.2); _copiedOffset.Go(-4, 0.2); }
        _cardOpacity.Go(dragging ? 0.45 : 1, 0.15);
        _crossOpacity.Go(_hovering && !dragging ? 1 : 0, 0.18);
    }

    private void SetPressed(bool on)
    {
        _line.PressedId = on ? Item.Id : (_line.PressedId == Item.Id ? null : _line.PressedId);
        // Holding presses the photo in slowly, so a long press feels like it is building up to something.
        if (on) _pressScale.Go(0.95, 0.45, Ease.InOutSine);
        else _pressScale.Go(1, 0.3, Ease.OutCubic);
    }

    /// <summary>Advances every spring and tween by `dt` seconds and applies them.</summary>
    public void Tick(double dt)
    {
        _swing.Step(dt); _arrive.Step(dt); _hoverScale.Step(dt); _pressScale.Step(dt); _x.Step(dt);
        _opacity.Step(dt); _zLift.Step(dt); _cardOpacity.Step(dt); _crossOpacity.Step(dt); _crossScale.Step(dt);
        _copiedOpacity.Step(dt); _copiedOffset.Step(dt); _shadow.Step(dt);

        if (RopeYAt is not null)
        {
            double ropeY = RopeYAt(_x.Value);
            SetLeft(this, _x.Value - Layout.CardWidth / 2);
            SetTop(this, ropeY - Layout.PinAbove);
            Height = Layout.PanelHeight - ropeY;
        }
        _rotate.Angle = _swing.Value + Item.Tilt;
        _drop.Y = _arrive.Value;
        Opacity = Item.Falling || Item.Flying ? 0 : _opacity.Value;
        Visibility = Filtered || Opacity < 0.01 ? Visibility.Hidden : Visibility.Visible;

        double scale = _hoverScale.Value * _pressScale.Value;
        _cardScale.ScaleX = _cardScale.ScaleY = scale;
        _cardHost.Opacity = _cardOpacity.Value;

        double s = _shadow.Value;
        _shadowEffect.Opacity = 0.18 + 0.08 * s;
        _shadowEffect.BlurRadius = 20 + 8 * s;
        _shadowEffect.ShadowDepth = 5 + 3 * s;

        _cross.Opacity = _pen.Opacity = _trash.Opacity = _crossOpacity.Value;
        _crossHit.Opacity = _penHit.Opacity = _trashHit.Opacity = _crossOpacity.Value;
        ((ScaleTransform)_cross.RenderTransform).ScaleX = ((ScaleTransform)_cross.RenderTransform).ScaleY = _crossScale.Value;
        ((ScaleTransform)_pen.RenderTransform).ScaleX = ((ScaleTransform)_pen.RenderTransform).ScaleY = _crossScale.Value;
        ((ScaleTransform)_trash.RenderTransform).ScaleX = ((ScaleTransform)_trash.RenderTransform).ScaleY = _crossScale.Value;
        _copied.Opacity = _copiedOpacity.Value;
        _copiedTranslate.Y = _copiedOffset.Value;
    }

    // MARK: Input

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _downPoint = null;
            EndPress();
            if (_line.IsNote(Item.Id)) _line.EditNote(Item.Id); else _line.Open(Item.Id);
            return;
        }
        _downPoint = e.GetPosition(this);
        _downInCanvas = Parent is UIElement parent ? e.GetPosition(parent) : _downPoint.Value;
        _grabOffsetX = _downInCanvas.X - CenterX;
        _startedDrag = false;
        _reordering = false;
        _didLongPress = false;
        _clickModifiers = Keyboard.Modifiers;
        _cardHost.CaptureMouse();
        SetPressed(true);
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void EndPress()
    {
        _holdTimer.Stop();
        SetPressed(false);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_downPoint is null || _startedDrag || _didLongPress || e.LeftButton != MouseButtonState.Pressed) return;
        var canvas = Parent as LineCanvas;
        var p = canvas is not null ? e.GetPosition(canvas) : e.GetPosition(this);
        double dx = p.X - _downInCanvas.X, dy = p.Y - _downInCanvas.Y;

        if (_reordering)
        {
            // Pulled away from the line: it becomes a real drag into another app.
            if (Math.Abs(dy) > 44)
            {
                _reordering = false;
                canvas?.EndReorder(this);
                _startedDrag = true;
                _cardHost.ReleaseMouseCapture();
                StartDrag(e.GetPosition(_cardHost));
                return;
            }
            canvas?.ReorderTo(this, p.X - _grabOffsetX);
            return;
        }

        if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        EndPress();
        // Sliding along the line reorders the cards, but only in manual order
        // with nothing folded away. Otherwise the drag becomes a real drag-out.
        if (canvas is not null && Math.Abs(dy) <= 44 && canvas.CanReorderCards)
        {
            _reordering = true;
            canvas.BeginReorder(this);
            canvas.ReorderTo(this, p.X - _grabOffsetX);
            return;
        }
        _startedDrag = true;
        _cardHost.ReleaseMouseCapture();
        StartDrag(e.GetPosition(_cardHost));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool click = _downPoint is not null && !_startedDrag && !_didLongPress && !_reordering;
        EndPress();
        _cardHost.ReleaseMouseCapture();
        if (_reordering) { _reordering = false; (Parent as LineCanvas)?.EndReorder(this); }
        if (click)
        {
            var canvas = Parent as LineCanvas;
            if ((_clickModifiers & ModifierKeys.Control) != 0)
            {
                canvas?.ToggleSelected(this);
            }
            else if ((_clickModifiers & ModifierKeys.Shift) != 0)
            {
                canvas?.SelectRangeTo(this);
            }
            else
            {
                canvas?.ClearSelection();
                _line.Copy(Item.Id);
            }
        }
        _downPoint = null;
        _didLongPress = false;
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndPress();
        _downPoint = null;
        var canvas = Parent as LineCanvas;
        if (canvas is not null && !canvas.IsSelected(Item.Id)) canvas.ClearSelection();
        Menus.Show(BuildMenu());
    }

    private ContextMenu BuildMenu()
    {
        var id = Item.Id;
        var menu = NewMenu();
        var canvas = Parent as LineCanvas;
        var selectedIds = canvas?.SelectedIds ?? Array.Empty<Guid>();
        if (canvas is not null && selectedIds.Count > 1 && selectedIds.Contains(id))
        {
            int count = selectedIds.Count;
            menu.Items.Add(CardMenuItem($"{Strings.Copy} ({count})", MenuGlyph.Copy, () =>
            {
                _line.CopyMany(selectedIds);
                canvas.ClearSelection();
            }));
            AppendCopyAs(menu, selectedIds,
                allImages: selectedIds.All(only => _line.Find(only) is { IsImage: true }),
                after: canvas.ClearSelection);
            menu.Items.Add(new Separator());
            menu.Items.Add(CardMenuItem($"{Strings.TakeDown} ({count})", MenuGlyph.TakeDown, () =>
            {
                _line.TakeDownMany(selectedIds);
                canvas.ClearSelection();
            }));
            menu.Items.Add(CardMenuItem($"{Strings.MoveToRecycleBin} ({count})", MenuGlyph.Recycle, () =>
            {
                _line.TrashMany(selectedIds);
                canvas.ClearSelection();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(CardMenuItem(Strings.ClearSelection, MenuGlyph.ClearSelection, canvas.ClearSelection));
            return menu;
        }
        bool inInbox = _line.IsInInbox(id);
        bool isNote = _line.IsNote(id);
        menu.Items.Add(CardMenuItem(Strings.Copy, MenuGlyph.Copy, () => _line.Copy(id)));
        // A note's own PNG path is internal bookkeeping, not something to paste elsewhere.
        if (!isNote) AppendCopyAs(menu, new[] { id }, Item.IsImage);
        if (isNote)
        {
            menu.Items.Add(CardMenuItem(Strings.EditNote, MenuGlyph.Edit, () => _line.EditNote(id)));
            var colour = CardMenuItem(Strings.NoteColour, MenuGlyph.Colour, () => { });
            Menus.AttachPanel(menu, colour, () =>
                MenuSidePanel.ForColors(
                    Strings.NoteColour,
                    Notes.Colors.Select(key =>
                    {
                        string colourName = key;
                        return new MenuSidePanel.ColorOption(
                            Strings.NoteName(colourName),
                            Notes.Paper(colourName),
                            () => _line.RecolourNote(id, colourName));
                    })));
            menu.Items.Add(colour);
        }
        else if (Item.IsVideo)
        {
            // Marking up or opening in Paint makes no sense for a recording.
            menu.Items.Add(CardMenuItem(Strings.Play, MenuGlyph.Play, () => _line.Open(id)));
        }
        else if (Item.IsImage)
        {
            menu.Items.Add(CardMenuItem(Strings.Open, MenuGlyph.Open, () => _line.Open(id)));
            menu.Items.Add(CardMenuItem(Strings.MarkUp, MenuGlyph.Edit, () => _line.Edit(id)));
            menu.Items.Add(CardMenuItem(Strings.EditInPaint, MenuGlyph.Edit, () => _line.EditExternal(id)));
            menu.Items.Add(CardMenuItem(Strings.CopyText, MenuGlyph.Ocr, () =>
            {
                if (Ocr.Available)
                {
                    _line.CopyText(id);
                    return;
                }

                var owner = Window.GetWindow(this);
                var answer = MessageBox.Show(
                    owner,
                    $"{Strings.OcrUnavailable}\n\n{Strings.OpenWindowsSettings}",
                    Strings.AppName,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes && !Shell.OpenOcrLanguageSettings())
                    MessageBox.Show(owner, Strings.OcrUnavailable, Strings.AppName,
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }));
        }
        else
        {
            // Any other file: no marking up, no OCR, no Paint. Copy, open and
            // drag it out like Explorer would.
            menu.Items.Add(CardMenuItem(Strings.Open, MenuGlyph.Open, () => _line.Open(id)));
        }
        menu.Items.Add(CardMenuItem(Strings.ShowInExplorer, MenuGlyph.Reveal, () => _line.Reveal(id)));
        var keep = CardMenuItem(Strings.KeepOnLine, MenuGlyph.Pin, () => _line.TogglePin(id));
        keep.IsChecked = Item.Pinned;
        menu.Items.Add(keep);
        // Caught screenshots keep the save-and-discard flow. Other file types
        // are references, so taking one down must never recycle the original.
        bool caughtImage = inInbox && Item.IsImage;
        if (caughtImage)
        {
            menu.Items.Add(CardMenuItem(Strings.SaveToDesktop, MenuGlyph.Save, () => _line.SaveToDesktop(id)));
            menu.Items.Add(CardMenuItem(Strings.SaveToScreenshots, MenuGlyph.Save, () => _line.SaveToScreenshots(id)));
        }
        menu.Items.Add(new Separator());
        if (caughtImage)
        {
            menu.Items.Add(CardMenuItem(Strings.Discard, MenuGlyph.Discard, () => _line.Discard(id)));
        }
        else
        {
            menu.Items.Add(CardMenuItem(Strings.TakeDown, MenuGlyph.TakeDown, () => _line.Discard(id)));
            menu.Items.Add(CardMenuItem(Strings.MoveToRecycleBin, MenuGlyph.Recycle, () => _line.Trash(id)));
        }
        return menu;
    }

    private static ContextMenu NewMenu()
        => new() { FlowDirection = Strings.Flow };

    public static MenuItem MenuItemFor(string header, Action action, string gesture = "")
    {
        var label = new TextBlock
        {
            Text = header,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var item = new MenuItem
        {
            Header = label,
            InputGestureText = gesture,
            ToolTip = header,
        };
        System.Windows.Automation.AutomationProperties.SetName(item, header);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// The "copy as" forms the line can already produce. Markdown and Base64 only mean
    /// something for pictures, and Base64 only for a single one, so the group shrinks to
    /// match what is actually selected.
    /// </summary>
    private void AppendCopyAs(ContextMenu menu, IReadOnlyList<Guid> ids, bool allImages, Action? after = null)
    {
        void Run(Action copy)
        {
            copy();
            after?.Invoke();
        }

        menu.Items.Add(CardMenuItem(Strings.CopyAsPath, MenuGlyph.CopyPath,
            () => Run(() => _line.CopyPaths(ids))));
        menu.Items.Add(CardMenuItem(Strings.CopyAsFileName, MenuGlyph.CopyFileName,
            () => Run(() => _line.CopyFileNames(ids))));
        if (!allImages) return;
        menu.Items.Add(CardMenuItem(Strings.CopyAsMarkdown, MenuGlyph.CopyMarkdown,
            () => Run(() => _line.CopyMarkdownLinks(ids))));
        if (ids.Count == 1)
        {
            var only = ids[0];
            menu.Items.Add(CardMenuItem(Strings.CopyAsBase64, MenuGlyph.CopyBase64,
                () => Run(() => { _ = _line.CopyImageBase64(only); })));
        }
    }

    private static MenuItem CardMenuItem(string header, MenuGlyph glyph, Action action, string gesture = "")
    {
        var item = MenuItemFor(header, action, gesture);
        item.Icon = MenuGlyphs.Create(glyph);
        return item;
    }

    // MARK: Drag and drop

    /// <summary>
    /// Drags the file out as Explorer would. An app gets a copy, and the photo
    /// stays. A folder keeps it, and the photo leaves. The Recycle Bin discards
    /// it. Nowhere that accepts it: nothing happens.
    /// </summary>
    private void StartDrag(Point grabInCard)
    {
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var image = RenderDragImage(scale);
        var grab = new Point(grabInCard.X * scale, grabInCard.Y * scale);
        var canvas = Parent as LineCanvas;
        var ids = canvas?.SelectedIds ?? Array.Empty<Guid>();
        if (ids.Count == 0 || !ids.Contains(Item.Id)) ids = new[] { Item.Id };
        var paths = ids
            .Select(_line.Find)
            .Where(item => item is not null && !item.Falling && System.IO.File.Exists(item.Path))
            .Select(item => item!.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) paths.Add(Item.Path);

        _line.DraggingId = Item.Id;
        uint effect = DragSource.DROPEFFECT_NONE;
        try
        {
            effect = paths.Count == 1
                ? DragSource.Drag(paths[0], image, grab)
                : DragSource.Drag(paths, image, grab);
        }
        catch (Exception ex)
        {
            Log.Error($"Drag failed: {ex.Message}");
        }
        finally
        {
            _line.DraggingId = null;
            _startedDrag = false;
            _downPoint = null;
            canvas?.ClearSelection();
        }
        Log.Notice($"Drag ended with effect {effect}");
        bool accepted = effect != DragSource.DROPEFFECT_NONE;
        if (accepted)
        {
            _line.HoldSave();
            try
            {
                foreach (var id in ids) _line.MarkUsed(id);
            }
            finally { _line.ReleaseSaveHold(); }
        }

        // A target that reports a move is trusted to have done it: Explorer
        // moves the file itself, and the line notices it is gone. HangAll
        // never deletes a file on a drop target's word, so an app that
        // answers "move" after merely reading the file costs nothing.

        // Dropped into an app and sent on its way: with the option on, it leaves the line too.
        if (accepted && Settings.Current.TakeDownAfterDrag)
        {
            var idsToTakeDown = ids
                .Where(id => _line.Find(id) is { Falling: false } item && System.IO.File.Exists(item.Path))
                .ToList();
            if (idsToTakeDown.Count == 0) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
            t.Tick += (_, _) => { t.Stop(); _line.TakeDownMany(idsToTakeDown); };
            t.Start();
            return;
        }

        // Moved into a folder: it is saved where you wanted it, and leaves the
        // line. Explorer finishes a moment later, so look again then.
        _line.Prune();
        foreach (var delay in new[] { 0.3, 0.8, 2.0 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) => { t.Stop(); _line.Prune(); };
            t.Start();
        }
    }

    /// <summary>The drag preview: the card as it hangs, without the clip.</summary>
    private BitmapSource RenderDragImage(double scale)
    {
        double w = _cardSize.Width, h = _cardSize.Height;
        double pad = 12;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(pad, pad));
            var frame = new Rect(0, 0, w, h);
            dc.DrawRoundedRectangle(new SolidColorBrush(Theme.GlassFill), new Pen(new SolidColorBrush(Theme.GlassOutline), 0.5), frame, Layout.FrameRadius, Layout.FrameRadius);
            if (Item.IsFile)
            {
                DrawFileCard(dc, w);
            }
            else
            {
                var photo = new Rect(Layout.FrameInset, Layout.FrameInset, w - 2 * Layout.FrameInset, h - 2 * Layout.FrameInset);
                double r = Layout.FrameRadius - Layout.FrameInset;
                dc.PushClip(new RectangleGeometry(photo, r, r));
                if (Item.IsIcon)
                {
                    double sourceAspect = Item.Thumb.PixelWidth / (double)Item.Thumb.PixelHeight;
                    double targetAspect = photo.Width / photo.Height;
                    var icon = targetAspect > sourceAspect
                        ? new Rect(photo.X + (photo.Width - photo.Height * sourceAspect) / 2, photo.Y, photo.Height * sourceAspect, photo.Height)
                        : new Rect(photo.X, photo.Y + (photo.Height - photo.Width / sourceAspect) / 2, photo.Width, photo.Width / sourceAspect);
                    dc.DrawImage(Item.Thumb, icon);
                }
                else
                {
                    dc.DrawImage(Item.Thumb, photo);
                }
                dc.Pop();
            }
            dc.DrawRoundedRectangle(null, new Pen(new LinearGradientBrush(Theme.GlassEdgeTop, Theme.GlassEdgeBottom, 90), 0.75), frame, Layout.FrameRadius, Layout.FrameRadius);
            dc.Pop();
        }
        int pw = (int)Math.Ceiling((w + 2 * pad) * scale), ph = (int)Math.Ceiling((h + 2 * pad) * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        // The grab point is relative to the card, so shift it by the padding.
        return new CroppedBitmap(rtb, new Int32Rect((int)(pad * scale), (int)(pad * scale), (int)(w * scale), (int)(h * scale)));
    }

    private void DrawFileCard(DrawingContext dc, double width)
    {
        string name = System.IO.Path.GetFileName(Item.Path);
        var text = new FormattedText(
            name,
            CultureInfo.CurrentUICulture,
            _fileName.FlowDirection,
            new Typeface(Typography.ForText(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Typography.SmallSize,
            Theme.Freeze(new SolidColorBrush(Theme.Primary)),
            1.0)
        {
            MaxTextWidth = Math.Max(0, width - Layout.FileNamePadding * 2),
            MaxTextHeight = Layout.FileNameHeight,
            TextAlignment = TextAlignment.Center,
            LineHeight = Typography.SmallLineHeight,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        double iconTop = Layout.FileIconTopFor(text.Height);
        double side = Layout.FileIconSize;
        double sourceAspect = Item.Thumb.PixelWidth / (double)Item.Thumb.PixelHeight;
        double targetAspect = 1;
        var icon = targetAspect > sourceAspect
            ? new Rect((width - side * sourceAspect) / 2, iconTop, side * sourceAspect, side)
            : new Rect((width - side) / 2, iconTop + (side - side / sourceAspect) / 2, side, side / sourceAspect);
        dc.DrawImage(Item.Thumb, icon);

        dc.DrawText(text, new Point(
            Layout.FileNamePadding,
            iconTop + Layout.FileIconSize + Layout.FileNameGap));
    }
}
