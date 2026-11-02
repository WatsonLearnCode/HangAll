using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;
using static HangAll.Interop.Native;

namespace HangAll.UI;

/// <summary>A borderless, scrollable grid of clipboard history cards.</summary>
internal sealed class ClipboardHistoryWindow : Window
{
    private const double PreferredWidth = 300;
    // The footer takes 34 DIP; the window grows by the same amount so the list
    // keeps the rows it had before the clear action moved there.
    private const double PreferredHeight = 494;
    private const double FooterHeight = 34;
    private const double CardHeight = 100;
    private const double CardGap = 4;
    private const double SearchHeight = 32;
    private const double FieldCornerRadius = 6;
    private const double SearchGap = 10;
    private const int PreviewMaxLines = 3;
    private const double PreviewLineHeight = 17;
    private const double SurfaceMargin = 8;
    private const double BodyInset = 10;
    private const double SurfaceCornerRadius = 16;
    private const double HeaderHeight = SurfaceCornerRadius * 2;

    private readonly Border _surface;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly TextBlock _empty;
    private readonly TextBlock _searchHint;
    private readonly TextBox _search;
    private readonly TextBlock _clearLabel;
    private readonly ScrollViewer _scroll;
    private readonly WrapPanel _itemsHost;
    private readonly List<StackPanel> _actionPanels = new();
    private readonly TranslateTransform _offset = new(0, -8);

    private IReadOnlyList<TextClipboardEntry> _items = Array.Empty<TextClipboardEntry>();
    private string _query = "";
    private bool _itemsDirty;
    private ClipboardWallet? _wallet;
    private bool _animatingOpen;
    private DateTime _suppressCloseUntil;
    private double _cardWidth = PreferredWidth - 2 * (SurfaceMargin + BodyInset + CardGap);

    public ClipboardHistoryWindow(ClipboardWallet? wallet = null)
    {
        _wallet = wallet;
        Title = Strings.ClipboardWalletTitle;
        Width = PreferredWidth;
        Height = PreferredHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        AutomationProperties.SetAutomationId(this, "ClipboardHistoryWindow");

        _surface = new Border
        {
            Margin = new Thickness(SurfaceMargin),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SurfaceCornerRadius),
            BorderThickness = new Thickness(0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 7,
                Direction = 270,
                Opacity = 0.32,
                Color = Color.FromRgb(0x0A, 0x0F, 0x18),
            },
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "MenuBg");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FooterHeight) });

        var header = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The 32 DIP slot centers the logo at the surface's 16 DIP corner radius.
        var icon = new Grid
        {
            Width = HeaderHeight,
            Height = HeaderHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.Children.Add(Morphicons.CreatePath(Morphicon.Clipboard, 14, 1.45, "AccentFill"));
        header.Children.Add(icon);

        _title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);

        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        Grid.SetColumn(_count, 2);
        header.Children.Add(_count);

        var closeButton = ActionButton(Morphicon.X, Strings.Close, () => CloseRequested?.Invoke(), 24);
        Grid.SetColumn(closeButton, 3);
        header.Children.Add(closeButton);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(BodyInset, 4, BodyInset, 0) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);

        _search = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(26, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _search.SetResourceReference(TextBox.CaretBrushProperty, "MenuFg");
        _search.SetResourceReference(Control.ForegroundProperty, "MenuFg");
        AutomationProperties.SetName(_search, Strings.ClipboardWalletSearch);
        AutomationProperties.SetAutomationId(_search, "ClipboardHistorySearch");

        var searchGlyph = Morphicons.CreatePath(Morphicon.Search, 12, 1.4, "MenuFgSecondary");
        searchGlyph.IsHitTestVisible = false;
        searchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        searchGlyph.VerticalAlignment = VerticalAlignment.Center;
        searchGlyph.Margin = new Thickness(8, 0, 0, 0);

        _searchHint = new TextBlock
        {
            Text = Strings.ClipboardWalletSearch,
            IsHitTestVisible = false,
            Margin = new Thickness(26, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");

        // The field takes the same rounded sheet as every other input in the app, and the
        // same side inset as the cards under it, so the two columns of text line up instead
        // of the field running out to the panel's own edge.
        var field = new Border
        {
            CornerRadius = new CornerRadius(FieldCornerRadius),
            BorderThickness = new Thickness(0.8),
            Child = _search,
        };
        field.SetResourceReference(Border.BackgroundProperty, "FieldBg");
        field.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");

        var searchRow = new Grid
        {
            Height = SearchHeight,
            Margin = new Thickness(CardGap, 0, CardGap, SearchGap),
        };
        searchRow.Children.Add(field);
        searchRow.Children.Add(searchGlyph);
        searchRow.Children.Add(_searchHint);
        body.Children.Add(searchRow);

        var listHost = new Grid { ClipToBounds = true };
        Grid.SetRow(listHost, 1);

        _empty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        listHost.Children.Add(_empty);

        _itemsHost = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _scroll = new ScrollViewer
        {
            Content = _itemsHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = true,
        };
        SmoothScroll.Attach(_scroll);
        listHost.Children.Add(_scroll);
        body.Children.Add(listHost);

        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text.Trim();
            _searchHint.Visibility = _search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            RebuildCards(keepOffset: false);
        };
        root.Children.Add(body);

        _clearLabel = new TextBlock
        {
            Text = Strings.ClipboardWalletClear,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = Typography.Ui,
            FontSize = Typography.CaptionSize,
        };
        _clearLabel.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");

        var footer = new Grid { Height = FooterHeight };
        var footerLine = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(BodyInset, 0, BodyInset, 0),
        };
        footerLine.SetResourceReference(Border.BackgroundProperty, "FieldBorder");
        footer.Children.Add(footerLine);
        footer.Children.Add(BuildClearButton(_clearLabel));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _surface.Child = root;
        var shell = new Grid { RenderTransform = _offset };
        shell.Children.Add(_surface);
        Content = shell;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                RequestClear();
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _search.SelectAll();
                _search.Focus();
                return;
            }
            if (e.Key != Key.Escape) return;
            // Escape steps out of the filter before it closes the window.
            if (_search.Text.Length > 0)
            {
                _search.Clear();
                e.Handled = true;
                return;
            }
            e.Handled = true;
            CloseRequested?.Invoke();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || IsActive || IsMouseOver || DateTime.UtcNow < _suppressCloseUntil) return;
            CloseRequested?.Invoke();
        }, DispatcherPriority.Background);
        Loaded += (_, _) =>
        {
            PositionNearWallet();
            AnimateOpen();
        };

        RefreshLocalization();
        RefreshTypography();
        RefreshTheme();
    }

    public event Action<TextClipboardEntry>? CopyRequested;
    public event Action<TextClipboardEntry>? CopyPlainRequested;
    public event Action<TextClipboardEntry>? DeleteRequested;
    public event Action<TextClipboardEntry>? PinRequested;
    public event Action? ClearRequested;
    public event Action? CloseRequested;

    /// <summary>
    /// The clear action used to be a 24 DIP icon-only button in the header and
    /// was the only control there that did not take a click. It now lives in a
    /// footer row as a wide, labelled target, so the action no longer depends on
    /// a tiny icon's hit area.
    /// </summary>
    private Border BuildClearButton(TextBlock label)
    {
        var icon = Morphicons.CreatePath(Morphicon.Eraser, 13, 1.4, "MenuFgSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(icon);
        label.Margin = new Thickness(7, 0, 0, 0);
        content.Children.Add(label);

        var button = new Border
        {
            Height = 26,
            Padding = new Thickness(10, 0, 12, 0),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(BodyInset, 4, BodyInset, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            Child = content,
            ToolTip = Strings.ClipboardWalletClear,
        };
        AutomationProperties.SetName(button, Strings.ClipboardWalletClear);
        AutomationProperties.SetAutomationId(button, "ClipboardClearHistory");

        bool pressed = false;
        void Paint(bool hover)
        {
            if (hover) button.SetResourceReference(Border.BackgroundProperty, "MenuHover");
            else button.Background = Brushes.Transparent;
        }

        button.MouseEnter += (_, _) => Paint(true);
        button.MouseLeave += (_, _) =>
        {
            pressed = false;
            Paint(false);
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            button.Focus();
            Paint(true);
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            RequestClear();
        };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            RequestClear();
        };
        return button;
    }

    private void RequestClear()
    {
        SuppressClose();
        ClearRequested?.Invoke();
    }

    /// <summary>Builds the cards for an offscreen render that never shows the window.</summary>
    internal void PrepareForRender() => RebuildCards();

    public void SetItems(IReadOnlyList<TextClipboardEntry> items)
    {
        _items = (items ?? Array.Empty<TextClipboardEntry>())
            .Where(item => item.HasContent)
            .Take(200)
            .ToArray();
        UpdateCardWidth();
        // The window lives hidden between uses. Rebuilding up to 200 cards with
        // their shadows on every text copy is wasted work; build on first show.
        _itemsDirty = true;
        if (IsVisible) RebuildCards();
    }

    internal void ShowFirstActionsForSnapshot()
    {
        if (_actionPanels.Count == 0) return;
        var actions = _actionPanels[0];
        actions.BeginAnimation(OpacityProperty, null);
        actions.Opacity = 1;
        actions.IsHitTestVisible = true;
    }

    public void ShowNear(ClipboardWallet wallet)
    {
        _wallet = wallet;
        _suppressCloseUntil = DateTime.MinValue;
        if (Owner is null)
        {
            var owner = Window.GetWindow(wallet);
            if (owner is not null && !ReferenceEquals(owner, this) && owner.IsVisible)
                Owner = owner;
        }
        if (!IsVisible)
        {
            _animatingOpen = true;
            Show();
            // The line window never takes the foreground, so the owned history
            // window has to claim it explicitly. Without that it can be treated
            // as deactivated and close before the first click lands.
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            // Both this panel and the line are topmost, and the line re-asserts itself on
            // every reveal. Without the same assertion here the panel can end up under the
            // line's own cards, which reads as the top of the panel - the search field -
            // being bitten off.
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            // HideWindow leaves the panel nudged up ready for the slide-in, and a
            // hidden-then-shown window does not raise Loaded a second time, so the
            // trip back down has to be driven from here rather than from Loaded.
            AnimateOpen();
        }
        PositionNearWallet();
        Dispatcher.BeginInvoke(() =>
        {
            _scroll.ScrollToTop();
            // Typing filters the list straight away, the way the shell's own
            // history does, so the field takes the caret on every show.
            _search.Focus();
        }, DispatcherPriority.Loaded);
    }

    public void HideWindow()
    {
        _animatingOpen = false;
        BeginAnimation(OpacityProperty, null);
        _offset.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1;
        _offset.Y = -8;
        if (IsVisible) Hide();
    }

    public void RefreshTheme()
    {
        Glass.PaintPanel(_surface);
        _surface.Opacity = Settings.LineOpacityOption(Settings.Current.LineOpacity);
        InvalidateCards();
    }

    public void RefreshTypography()
    {
        Typography.ApplyText(this);
        _title.FontFamily = Typography.Ui;
        _title.FontSize = Typography.ControlSize;
        Typography.ApplyText(_title);
        Typography.BlockLineHeight(_title, Typography.ControlLineHeight);
        _count.FontFamily = Typography.Ui;
        _count.FontSize = Typography.SmallSize;
        Typography.ApplyText(_count);
        _empty.FontFamily = Typography.Ui;
        _empty.FontSize = Typography.ControlSize;
        Typography.ApplyText(_empty);
        _search.FontFamily = Typography.Ui;
        _search.FontSize = Typography.BodySize;
        Typography.ApplyText(_search);
        _searchHint.FontFamily = Typography.Ui;
        _searchHint.FontSize = Typography.BodySize;
        Typography.ApplyText(_searchHint);
        _clearLabel.FontFamily = Typography.Ui;
        _clearLabel.FontSize = Typography.SmallSize;
        Typography.ApplyText(_clearLabel);
        InvalidateCards();
    }

    public void RefreshLocalization()
    {
        _title.Text = Strings.ClipboardWalletTitle;
        _empty.Text = string.IsNullOrEmpty(_query)
            ? Strings.ClipboardWalletEmpty
            : Strings.ClipboardWalletNoMatches;
        _searchHint.Text = Strings.ClipboardWalletSearch;
        AutomationProperties.SetName(_search, Strings.ClipboardWalletSearch);
        _clearLabel.Text = Strings.ClipboardWalletClear;
        ToolTip = Strings.ClipboardWalletTitle;
        FlowDirection = Strings.Flow;
        InvalidateCards();
        AutomationProperties.SetName(this, Strings.ClipboardWalletTitle);
        AutomationProperties.SetHelpText(this, Strings.ClipboardWalletEmpty);
    }

    private void InvalidateCards()
    {
        _itemsDirty = true;
        if (IsVisible) RebuildCards();
    }

    private void RebuildCards(bool keepOffset = true)
    {
        _itemsDirty = false;
        double previousOffset = _scroll.VerticalOffset;
        bool wasVisible = IsVisible;
        IReadOnlyList<TextClipboardEntry> shown = string.IsNullOrEmpty(_query)
            ? _items
            : _items.Where(item => item.Text?.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false).ToArray();
        _itemsHost.Children.Clear();
        _actionPanels.Clear();
        bool empty = shown.Count == 0;
        _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = string.IsNullOrEmpty(_query)
            ? Strings.ClipboardWalletEmpty
            : Strings.ClipboardWalletNoMatches;
        _count.Text = shown.Count.ToString(CultureInfo.CurrentCulture);
        if (empty) return;

        foreach (var entry in shown)
        {
            var card = BuildCard(entry);
            card.Margin = new Thickness(CardGap);
            _itemsHost.Children.Add(card);
        }
        if (wasVisible)
        {
            // A filter change has to start at the top of the new list; the restore is
            // queued, so doing it here rather than scrolling after would be undone.
            double restoreOffset = keepOffset ? previousOffset : 0;
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisible) return;
                _scroll.ScrollToVerticalOffset(restoreOffset);
                // Rebuilding after every keystroke must not pull the caret out of the field.
                if (!IsKeyboardFocusWithin) _scroll.Focus();
            }, DispatcherPriority.Background);
        }
    }

    private Border BuildCard(TextClipboardEntry entry)
    {
        var card = new Border
        {
            Width = _cardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.7),
            Padding = new Thickness(9, 6, 8, 5),
            SnapsToDevicePixels = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        Glass.Elevate(card, 0.10, 9, 1.5);
        UiMotion.HoverLift(card, 2, 0.07);
        AutomationProperties.SetName(card, $"{ClipboardWallet.FormatLabel(entry)}: {ClipboardWallet.PreviewText(entry)}");

        var face = new Grid();
        face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        var format = new TextBlock
        {
            Text = ClipboardWallet.FormatLabel(entry).ToUpperInvariant(),
            FontFamily = Typography.Ui,
            FontSize = Typography.CaptionSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        format.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
        // A pinned card needs to say so without hovering it, because the pin button
        // that reveals the state only appears on hover.
        if (entry.Pinned)
        {
            var tag = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tag.Children.Add(format);
            var mark = Morphicons.CreatePath(Morphicon.Pin, 9, 1.3, "MenuFgSecondary");
            mark.Margin = new Thickness(6, 0, 0, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            tag.Children.Add(mark);
            header.Children.Add(tag);
        }
        else
        {
            header.Children.Add(format);
        }

        var time = new TextBlock
        {
            Text = FormatTimestamp(entry.CapturedAt),
            FontFamily = Typography.Ui,
            FontSize = Typography.CaptionSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        header.Children.Add(time);
        face.Children.Add(header);

        var preview = new TextBlock
        {
            Text = ClipboardWallet.PreviewText(entry),
            FontFamily = Typography.ForText(entry.Text),
            FontSize = Typography.SmallSize,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = PreviewLineHeight * PreviewMaxLines,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        Grid.SetRow(preview, 1);
        face.Children.Add(preview);

        var footer = new Grid();
        var code = new TextBlock
        {
            Text = "**** " + entry.Id.ToString("N")[..4].ToUpperInvariant(),
            FontFamily = Typography.Ui,
            FontSize = Typography.CaptionSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        code.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        footer.Children.Add(code);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        actions.Children.Add(ActionButton(
            Morphicon.Pin,
            entry.Pinned ? Strings.ClipboardUnpin : Strings.ClipboardPin,
            () =>
            {
                SuppressClose();
                PinRequested?.Invoke(entry);
            },
            21));
        actions.Children.Add(ActionButton(Morphicon.Copy, Strings.ClipboardWalletCopyRich, () => CopyRequested?.Invoke(entry), 21));
        actions.Children.Add(ActionButton(Morphicon.Type, Strings.ClipboardWalletCopyPlain, () => CopyPlainRequested?.Invoke(entry), 21));
        actions.Children.Add(ActionButton(
            Morphicon.Trash2,
            Strings.ClipboardWalletDeleteEntry,
            () =>
            {
                SuppressClose();
                DeleteRequested?.Invoke(entry);
            },
            21));
        _actionPanels.Add(actions);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        face.Children.Add(footer);
        card.Child = face;

        card.MouseEnter += (_, _) =>
        {
            card.SetResourceReference(Border.BorderBrushProperty, "AccentFill");
            ShowActions(actions, true);
        };
        card.MouseLeave += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (card.IsMouseOver) return;
            card.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
            ShowActions(actions, false);
        }, DispatcherPriority.Input);
        return card;
    }

    private static void ShowActions(StackPanel actions, bool show)
    {
        actions.IsHitTestVisible = show;
        if (actions.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            actions.RenderTransform = slide;
        }
        actions.BeginAnimation(OpacityProperty, null);
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            actions.Opacity = show ? 1 : 0;
            slide.X = show ? 0 : 6;
            return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(110);
        // The actions slide out from under the timestamp on the way in.
        slide.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(slide.X, show ? 0 : 6, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
        slide.X = show ? 0 : 6;
        actions.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(actions.Opacity, show ? 1 : 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private Button ActionButton(Morphicon icon, string name, Action action, double size)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FocusVisualStyle = null,
            // A rebuild of the list destroys these buttons, so a click that puts the
            // caret here loses it as soon as the card it belongs to is made again.
            // The stripped focus ring already kept them out of reach of the keyboard.
            Focusable = false,
            Cursor = Cursors.Hand,
            Content = Morphicons.CreatePath(icon, size * 0.5, 1.3, "MenuFgSecondary"),
            ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        button.Template = ActionButtonTemplate();
        button.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private static ControlTemplate ActionButtonTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        chrome.SetValue(UIElement.OpacityProperty, 0.0);
        chrome.SetResourceReference(Border.BackgroundProperty, "MenuHover");
        root.AppendChild(chrome);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 1.0, "Chrome"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(OpacityProperty, 1.0, "Chrome"));
        template.Triggers.Add(focus);
        return template;
    }

    private void PositionNearWallet()
    {
        if (_wallet is null) return;
        Rect bounds = _wallet.BoundsRect;
        if (bounds.IsEmpty)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_wallet is not null && IsVisible) PositionNearWallet();
            });
            return;
        }

        var display = Displays.Nearest(new POINT(
            (int)Math.Round(bounds.Left + bounds.Width / 2),
            (int)Math.Round(bounds.Top + bounds.Height / 2)));
        double scale = Math.Max(0.1, display.Scale);
        var work = display.Work;
        double margin = 8 * scale;
        Width = Math.Min(PreferredWidth, Math.Max(260, work.Width / scale - 16));
        Height = Math.Min(PreferredHeight, Math.Max(260, work.Height / scale - 16));
        double previousCardWidth = _cardWidth;
        UpdateCardWidth();
        if (_itemsDirty || Math.Abs(previousCardWidth - _cardWidth) > 0.5) RebuildCards();
        UpdateLayout();

        double pixelWidth = Width * scale;
        double left = bounds.Left + (bounds.Width - pixelWidth) / 2;
        left = Math.Clamp(left, work.Left + margin, Math.Max(work.Left + margin, work.Right - pixelWidth - margin));
        double top = bounds.Bottom + 8 * scale;
        double availableHeight = (work.Bottom - top - margin) / scale;
        Height = Math.Max(220, Math.Min(Height, availableHeight));
        double pixelHeight = Height * scale;
        double maxTop = work.Bottom - pixelHeight - margin;
        top = Math.Min(top, Math.Max(work.Top + margin, maxTop));
        Left = left / scale;
        Top = top / scale;
    }

    private void SuppressClose() => _suppressCloseUntil = DateTime.UtcNow.AddMilliseconds(350);

    private void UpdateCardWidth()
    {
        double windowWidth = Width > 0 ? Width : PreferredWidth;
        _cardWidth = Math.Max(
            180,
            windowWidth - 2 * (SurfaceMargin + BodyInset + CardGap));
    }

    private string FormatTimestamp(DateTime capturedAt) =>
        capturedAt.ToString(
            _cardWidth < 240 ? "MM/dd HH:mm" : "g",
            CultureInfo.CurrentCulture);

    private void AnimateOpen()
    {
        if (!_animatingOpen) return;
        _animatingOpen = false;
        BeginAnimation(OpacityProperty, null);
        _offset.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1;
        _offset.Y = 0;
        if (!SystemParameters.ClientAreaAnimation) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        _offset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }
}
