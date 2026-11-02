using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HangAll.Core;

namespace HangAll.UI;

/// <summary>A compact clipboard card on the line that opens the history window.</summary>
internal sealed class ClipboardWallet : Grid
{
    public const double CardWidth = 148;
    private const double LauncherHeight = 90;
    private const double CardHeight = 74;
    private const double CardCornerRadius = 12;

    private readonly Border _card;
    private Grid _clip = null!;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly TextBlock _preview;
    private readonly ClipboardHistoryWindow _window;
    private readonly ScaleTransform _cardScale = new(1, 1);
    private readonly RotateTransform _rotate = new();
    private readonly Spring _swing = Spring.Raw(46, 2.6);
    private readonly Tween _opacity = new(0);

    private IReadOnlyList<TextClipboardEntry> _items = Array.Empty<TextClipboardEntry>();
    private bool _expanded;
    private bool _focused;

    public ClipboardWallet()
    {
        Width = CardWidth;
        Height = LauncherHeight;
        Focusable = true;
        FocusVisualStyle = null;
        Background = null;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = false;
        AutomationProperties.SetAutomationId(this, "ClipboardWallet");

        _card = BuildCard(out _title, out _count, out _preview);
        _card.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            e.Handled = true;
        };
        _card.MouseLeftButtonUp += (_, e) =>
        {
            ToggleExpanded();
            e.Handled = true;
        };
        _card.MouseEnter += (_, _) => SetHovered(true);
        _card.MouseLeave += (_, _) => SetHovered(false);
        Children.Add(_card);

        PaintClip();

        _window = new ClipboardHistoryWindow(this);
        _window.CopyRequested += entry => CopyRequested?.Invoke(entry);
        _window.CopyPlainRequested += entry => CopyPlainRequested?.Invoke(entry);
        _window.DeleteRequested += entry => DeleteRequested?.Invoke(entry);
        _window.PinRequested += entry => PinRequested?.Invoke(entry);
        _window.ClearRequested += () => ClearRequested?.Invoke();
        _window.CloseRequested += () => SetExpanded(false);

        PreviewKeyDown += OnPreviewKeyDown;
        GotKeyboardFocus += (_, _) =>
        {
            _focused = true;
            RefreshFocus();
        };
        LostKeyboardFocus += (_, _) =>
        {
            _focused = false;
            RefreshFocus();
        };
        Loaded += (_, _) =>
        {
            RefreshLocalization();
            RefreshTypography();
            RefreshTheme();
        };
        Unloaded += (_, _) => SetExpanded(false);

        RefreshLocalization();
        RefreshTypography();
        RefreshTheme();
        _opacity.Snap(0);
        _opacity.Go(1, 0.24, Ease.OutQuad);
        Nudge(2.2);
    }

    public event Action<TextClipboardEntry>? CopyRequested;
    public event Action<TextClipboardEntry>? CopyPlainRequested;
    public event Action<TextClipboardEntry>? DeleteRequested;
    public event Action<TextClipboardEntry>? PinRequested;
    public event Action? ClearRequested;
    public event Action<bool>? ExpandedChanged;

    public bool IsExpanded => _expanded;
    /// <summary>True while the caret is in the open history panel. The Quick Look
    /// shortcut is a bare key by default, and the panel sits inside the line's own
    /// hover geometry, so the line has to stand down while the field owns the keyboard.</summary>
    public bool HoldsTextFocus => _expanded && _window.IsKeyboardFocusWithin;
    internal bool Active => !_swing.Resting || !_opacity.Done;

    public Rect BoundsRect
    {
        get
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return Rect.Empty;
            try
            {
                var topLeft = PointToScreen(new Point(0, 0));
                var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
                return new Rect(topLeft, bottomRight);
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;
            }
        }
    }

    public void SetItems(IReadOnlyList<TextClipboardEntry> items)
    {
        _items = items ?? Array.Empty<TextClipboardEntry>();
        _count.Text = _items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _preview.Text = _items.FirstOrDefault() is { } latest
            ? PreviewText(latest)
            : Strings.ClipboardWalletEmpty;
        _window.SetItems(_items);
        UpdateAccessibility();
    }

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        if (expanded)
        {
            Breeze(now: true);
            _window.ShowNear(this);
        }
        else
        {
            _window.HideWindow();
        }
        UpdateAccessibility();
        ExpandedChanged?.Invoke(_expanded);
    }

    /// <summary>The clip this card hangs by: the peg chosen in Settings, like every other
    /// thing on the line, remade whenever the look changes.</summary>
    private void PaintClip()
    {
        if (_clip is not null) Children.Remove(_clip);
        _clip = Pegs.CreateLineClip(seed: 3);
        _clip.Margin = new Thickness(0, -4, 0, 0);
        SetZIndex(_clip, 20);
        Children.Add(_clip);
    }

    public void RefreshTheme()
    {
        Glass.Paint(_card);
        Glass.Elevate(_card, _focused ? 0.20 : 0.14, 16, 4);
        PaintClip();
        _window.RefreshTheme();
        RefreshFocus();
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
        _preview.FontFamily = Typography.ForText(_preview.Text);
        _preview.FontSize = Typography.SmallSize;
        Typography.ApplyText(_preview);
        _window.RefreshTypography();
    }

    public void RefreshLocalization()
    {
        _title.Text = Strings.ClipboardLauncherTitle;
        _preview.Text = _items.FirstOrDefault() is { } latest
            ? PreviewText(latest)
            : Strings.ClipboardWalletEmpty;
        ToolTip = Strings.ClipboardWalletTitle;
        FlowDirection = Strings.Flow;
        _window.RefreshLocalization();
        UpdateAccessibility();
    }

    internal static string FormatLabel(TextClipboardEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Html)) return Strings.ClipboardWalletFormatHtml;
        if (!string.IsNullOrEmpty(entry.Rtf)) return Strings.ClipboardWalletFormatRich;
        return Strings.ClipboardWalletFormatPlain;
    }

    internal static string PreviewText(TextClipboardEntry entry)
    {
        string? text = entry.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = !string.IsNullOrEmpty(entry.Rtf) || !string.IsNullOrEmpty(entry.Html)
                ? FormatLabel(entry)
                : Strings.ClipboardWalletEmpty;

        var compact = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                compact.Append(' ');
                pendingSpace = false;
            }
            compact.Append(ch);
            if (compact.Length >= 360) break;
        }
        return compact.ToString();
    }

    internal void Tick(double dt)
    {
        _swing.Step(dt);
        _opacity.Step(dt);
        _rotate.Angle = _swing.Value;
        _card.Opacity = _opacity.Value;
    }

    internal void Breeze(bool now = false)
    {
        double delay = Random.Shared.NextDouble() * 0.35;
        double degrees = 1.6 + Random.Shared.NextDouble() * 1.8;
        if (now)
        {
            Nudge(degrees);
            return;
        }
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0.01, delay)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Nudge(degrees);
        };
        timer.Start();
    }

    private Border BuildCard(
        out TextBlock title,
        out TextBlock count,
        out TextBlock preview)
    {
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(CardCornerRadius),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(0, 0, 10, 6),
            RenderTransformOrigin = new Point(0.5, 0),
        };
        _rotate.CenterX = CardWidth / 2;
        _rotate.CenterY = 0;
        _cardScale.CenterX = CardWidth / 2;
        _cardScale.CenterY = 0;
        card.RenderTransform = new TransformGroup { Children = { _rotate, _cardScale } };
        Glass.Paint(card);
        Glass.Elevate(card, 0.14, 16, 4);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The 24 DIP icon slot is centered on the 12 DIP corner arc.
        var icon = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.Children.Add(Morphicons.CreatePath(Morphicon.Clipboard, 14, 1.45, "AccentFill"));
        header.Children.Add(icon);

        title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0),
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        Grid.SetColumn(count, 2);
        header.Children.Add(count);
        grid.Children.Add(header);

        preview = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28, 2, 0, 0),
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgSecondary");
        Grid.SetRow(preview, 1);
        grid.Children.Add(preview);
        card.Child = grid;
        return card;
    }

    private void SetHovered(bool hovered)
    {
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        double target = hovered ? 1.025 : 1;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _cardScale.ScaleX = _cardScale.ScaleY = target;
            return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(120);
        _cardScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_cardScale.ScaleX, target, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            });
        _cardScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_cardScale.ScaleY, target, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            });
    }

    private void Nudge(double degrees)
    {
        _swing.Velocity += degrees * 7.5;
        _swing.Target = 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            ToggleExpanded();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _expanded)
        {
            SetExpanded(false);
            e.Handled = true;
        }
    }

    private void ToggleExpanded() => SetExpanded(!_expanded);

    private void RefreshFocus()
    {
        _card.SetResourceReference(
            Border.BorderBrushProperty,
            _focused ? "AccentFill" : "FieldBorder");
    }

    private void UpdateAccessibility()
    {
        AutomationProperties.SetName(this, Strings.ClipboardLauncherTitle);
        AutomationProperties.SetHelpText(
            this,
            _expanded ? Strings.ClipboardWalletTitle : Strings.ClipboardWalletEmpty);
    }
}
