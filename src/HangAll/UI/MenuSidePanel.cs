using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>
/// A compact companion surface for menus. It deliberately uses the same
/// semantic brushes as the main menu and settings workspace so theme changes
/// do not require rebuilding either surface.
/// </summary>
internal sealed class MenuSidePanel : ContentControl
{
    internal const double ShadowInset = 14;
    private const double SwitchTrackWidth = 36;
    private const double SwitchTrackHeight = 20;
    private const double SwitchThumbSize = 14;
    private const double SwitchThumbTravel = 16;
    private const double SwitchAnimationMs = 160;

    internal sealed record ToggleOption(
        string Header,
        Func<bool> IsChecked,
        Func<bool> IsEnabled,
        Action Toggle);

    internal sealed record ColorOption(string Header, Color Color, Action Select);

    private readonly List<ToggleRow> _toggleRows = new();
    private readonly List<Button> _rowButtons = new();
    private readonly Grid _revealRoot;
    private readonly ScaleTransform _revealScale = new(1, 1);
    private readonly TranslateTransform _revealOffset = new();
    private Action? _collapseCompletion;
    private bool _collapseAnimating;
    private bool _opensToRight;

    private MenuSidePanel(string title, double width)
    {
        Width = width;
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        FlowDirection = Strings.Flow;
        FontFamily = (FontFamily)FindResource("UiFontFamily");
        FontSize = (double)FindResource("UiBodyFontSize");
        Foreground = (Brush)FindResource("MenuFg");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.18,
                Color = Colors.Black,
            },
        };
        surface.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        surface.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");

        var content = new StackPanel();
        content.Children.Add(CreateHeading(title));
        content.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(9, 3, 9, 5),
            Background = (Brush)FindResource("MenuSeparator"),
        });

        var rows = new StackPanel();
        content.Children.Add(rows);

        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        surface.Child = scroll;
        _revealRoot = new Grid
        {
            Margin = new Thickness(ShadowInset),
            Children = { surface },
            Background = Brushes.Transparent,
            Opacity = 0,
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    _revealScale,
                    _revealOffset,
                },
            },
        };
        Content = _revealRoot;

        Rows = rows;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EscapeRequested?.Invoke();
                return;
            }

            if (e.Key is not (Key.Up or Key.Down)) return;
            MoveRowFocus(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
        };
    }

    private StackPanel Rows { get; }

    internal event Action? EscapeRequested;
    internal event Action? CloseRequested;
    internal bool IsCollapseAnimating => _collapseAnimating;

    internal void PlayReveal(bool opensToRight)
    {
        _collapseAnimating = false;
        _collapseCompletion = null;
        _opensToRight = opensToRight;
        _revealRoot.RenderTransformOrigin = new Point(opensToRight ? 0 : 1, 0.5);

        const double durationMs = 165;
        const double startScaleX = 0.96;
        double startOffset = opensToRight ? -4 : 4;

        _revealRoot.BeginAnimation(OpacityProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _revealOffset.BeginAnimation(TranslateTransform.XProperty, null);

        _revealRoot.Opacity = 1;
        _revealScale.ScaleX = 1;
        _revealOffset.X = 0;

        if (!SystemParameters.ClientAreaAnimation) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(durationMs);
        _revealRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        _revealScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(startScaleX, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        _revealOffset.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(startOffset, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    internal void PlayCollapse(Action completed)
    {
        _collapseAnimating = true;
        _collapseCompletion = completed;

        _revealRoot.BeginAnimation(OpacityProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _revealOffset.BeginAnimation(TranslateTransform.XProperty, null);

        const double durationMs = 125;
        const double endScaleX = 0.96;
        double endOffset = _opensToRight ? -4 : 4;

        _revealRoot.Opacity = 0;
        _revealScale.ScaleX = endScaleX;
        _revealOffset.X = endOffset;

        if (!SystemParameters.ClientAreaAnimation)
        {
            CompleteCollapse();
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var opacity = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        opacity.Completed += (_, _) => CompleteCollapse();
        _revealRoot.BeginAnimation(OpacityProperty, opacity);
        _revealScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, endScaleX, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        _revealOffset.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, endOffset, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    internal void CancelCollapse()
    {
        if (!_collapseAnimating) return;

        _collapseAnimating = false;
        _collapseCompletion = null;
        _revealRoot.BeginAnimation(OpacityProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _revealScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _revealOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _revealRoot.Opacity = 1;
        _revealScale.ScaleX = 1;
        _revealScale.ScaleY = 1;
        _revealOffset.X = 0;
    }

    private void CompleteCollapse()
    {
        if (!_collapseAnimating) return;
        var completed = _collapseCompletion;
        _collapseCompletion = null;
        if (completed is null)
        {
            _collapseAnimating = false;
            return;
        }
        // The callback has the last word on whether the panel really goes, and
        // CancelCollapse only brings the artwork back while a collapse is still
        // flagged as running - so the flag comes down after it, never before.
        completed();
        if (_collapseCompletion is null) _collapseAnimating = false;
    }

    public static MenuSidePanel ForToggles(string title, IEnumerable<ToggleOption> options)
    {
        var panel = new MenuSidePanel(title, 304);
        foreach (var option in options)
        {
            var row = panel.CreateToggleRow(option);
            panel._toggleRows.Add(row);
            panel._rowButtons.Add(row.Button);
            panel.Rows.Children.Add(row.Button);
        }
        panel.Refresh();
        return panel;
    }

    public static MenuSidePanel ForColors(string title, IEnumerable<ColorOption> options)
    {
        var panel = new MenuSidePanel(title, 256);
        foreach (var option in options)
        {
            var row = panel.CreateColorRow(option);
            panel._rowButtons.Add(row);
            panel.Rows.Children.Add(row);
        }
        return panel;
    }

    public void LimitHeight(Display display)
    {
        double screenHeight = display.Work.Height / display.Scale;
        MaxHeight = Math.Max(220, screenHeight - 24);
    }

    private TextBlock CreateHeading(string title)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontSize = (double)FindResource("UiHeadingFontSize"),
            LineHeight = (double)FindResource("UiHeadingLineHeight"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title,
            Margin = new Thickness(6, 5, 6, 4),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        return heading;
    }

    private ToggleRow CreateToggleRow(ToggleOption option)
    {
        var title = new TextBlock
        {
            Text = option.Header,
            FontSize = (double)FindResource("UiBodyFontSize"),
            LineHeight = (double)FindResource("UiBodyLineHeight"),
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = option.Header,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");

        var offTrack = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        offTrack.SetResourceReference(Border.BackgroundProperty, "MenuIconBg");
        offTrack.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");

        var accentTrack = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Opacity = 0,
        };
        accentTrack.SetResourceReference(Border.BackgroundProperty, "AccentFill");
        accentTrack.SetResourceReference(Border.BorderBrushProperty, "AccentFill");

        var thumbOff = new Border
        {
            Width = SwitchThumbSize,
            Height = SwitchThumbSize,
            CornerRadius = new CornerRadius(SwitchThumbSize / 2),
            BorderThickness = new Thickness(0.5),
        };
        thumbOff.SetResourceReference(Border.BackgroundProperty, "MenuFg");
        thumbOff.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");

        var thumbOn = new Border
        {
            Width = SwitchThumbSize,
            Height = SwitchThumbSize,
            CornerRadius = new CornerRadius(SwitchThumbSize / 2),
            Opacity = 0,
        };
        thumbOn.SetResourceReference(Border.BackgroundProperty, "AccentFg");

        var thumbOffset = new TranslateTransform();
        var thumb = new Border
        {
            Width = SwitchThumbSize,
            Height = SwitchThumbSize,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            RenderTransform = thumbOffset,
            Child = new Grid { Children = { thumbOff, thumbOn } },
        };

        var track = new Border
        {
            Width = SwitchTrackWidth,
            Height = SwitchTrackHeight,
            CornerRadius = new CornerRadius(SwitchTrackHeight / 2),
            Margin = new Thickness(12, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Child = new Grid { Children = { offTrack, accentTrack, thumb } },
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(track, 1);
        row.Children.Add(title);
        row.Children.Add(track);

        var button = CreateRowButton(option.Header, row);
        var toggleRow = new ToggleRow(
            button,
            track,
            accentTrack,
            thumb,
            thumbOn,
            thumbOffset,
            option);
        button.Click += (_, _) =>
        {
            if (!option.IsEnabled()) return;
            option.Toggle();
            Refresh(toggleRow);
        };
        return toggleRow;
    }

    private Button CreateColorRow(ColorOption option)
    {
        var swatch = MenuGlyphs.Swatch(option.Color);

        var title = new TextBlock
        {
            Text = option.Header,
            FontSize = (double)FindResource("UiBodyFontSize"),
            LineHeight = (double)FindResource("UiBodyLineHeight"),
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = option.Header,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(title, 1);
        row.Children.Add(swatch);
        row.Children.Add(title);

        var button = CreateRowButton(option.Header, row);
        button.Click += (_, _) =>
        {
            option.Select();
            CloseRequested?.Invoke();
        };
        return button;
    }

    private Button CreateRowButton(string automationName, UIElement content)
    {
        var button = new Button
        {
            Style = (Style)FindResource("MenuSidePanelRow"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = automationName,
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private void Refresh()
        => Refresh(null);

    private void Refresh(ToggleRow? animatedRow)
    {
        foreach (var row in _toggleRows)
        {
            bool enabled = row.Option.IsEnabled();
            bool isChecked = row.Option.IsChecked();
            row.Button.IsEnabled = enabled;
            ApplyToggleVisual(
                row,
                isChecked,
                ReferenceEquals(row, animatedRow));
        }
    }

    private static void ApplyToggleVisual(ToggleRow row, bool isChecked, bool animate)
    {
        double targetOffset = isChecked ? SwitchThumbTravel : 0;
        double targetAccentOpacity = isChecked ? 1 : 0;
        double currentOffset = row.ThumbOffset.X;
        double currentTrackOpacity = row.AccentTrack.Opacity;
        double currentThumbOpacity = row.ThumbOn.Opacity;

        row.ThumbOffset.BeginAnimation(TranslateTransform.XProperty, null);
        row.AccentTrack.BeginAnimation(OpacityProperty, null);
        row.ThumbOn.BeginAnimation(OpacityProperty, null);

        row.ThumbOffset.X = targetOffset;
        row.AccentTrack.Opacity = targetAccentOpacity;
        row.ThumbOn.Opacity = targetAccentOpacity;

        if (!animate || !SystemParameters.ClientAreaAnimation) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SwitchAnimationMs);
        row.ThumbOffset.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(currentOffset, targetOffset, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        row.AccentTrack.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentTrackOpacity, targetAccentOpacity, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        row.ThumbOn.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentThumbOpacity, targetAccentOpacity, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    internal void FocusFirstRow()
    {
        var first = _rowButtons.FirstOrDefault(button => button.IsEnabled);
        first?.Focus();
    }

    private void MoveRowFocus(int direction)
    {
        if (_rowButtons.Count == 0) return;

        int current = Keyboard.FocusedElement is Button focused
            ? _rowButtons.IndexOf(focused)
            : -1;
        int index = current;
        for (int step = 0; step < _rowButtons.Count; step++)
        {
            index = index < 0
                ? (direction > 0 ? 0 : _rowButtons.Count - 1)
                : (index + direction + _rowButtons.Count) % _rowButtons.Count;
            if (!_rowButtons[index].IsEnabled) continue;
            _rowButtons[index].Focus();
            return;
        }
    }

    private sealed record ToggleRow(
        Button Button,
        Border Track,
        Border AccentTrack,
        Border Thumb,
        Border ThumbOn,
        TranslateTransform ThumbOffset,
        ToggleOption Option);
}
