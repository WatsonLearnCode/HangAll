using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HangAll.Core;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>A themed About sheet that shares the settings workspace language.</summary>
public sealed class AboutWindow : Window
{
    private static AboutWindow? _open;
    private const double PageWidth = 460;
    private const double PageMinWidth = 320;
    private const double WorkAreaInset = 60;
    private const double WorkspaceRadius = 14;
    private const double CardRadius = 12;
    private const double ControlHeight = 30;
    private const double IconSlotSize = 24;
    private const double IconSlotRadius = 6;
    private readonly ThemeBinder _theme;
    private readonly Display _display;
    private readonly double _contentMaxHeight;

    public static void Open()
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new AboutWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>Apply a new UI type scale to the About sheet while it is open.</summary>
    public static void RefreshTypography()
    {
        if (_open is null) return;
        Typography.Apply(_open);
        _open.Content = _open.Build();
    }

    private AboutWindow()
    {
        Title = Strings.About;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        Icon = Brand.Icon;

        _display = Displays.UnderPointer();
        double availableWidth = _display.Work.Width / _display.Scale - WorkAreaInset;
        double availableHeight = _display.Work.Height / _display.Scale - WorkAreaInset;
        Width = Math.Max(PageMinWidth, Math.Min(PageWidth, availableWidth));
        MaxHeight = Math.Max(1, availableHeight);
        _contentMaxHeight = Math.Max(190, MaxHeight - 164);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) => CenterOnDisplay();

        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        UseLayoutRounding = true;
        RefreshTheme();
        _theme = new ThemeBinder(this, RefreshTheme, hwnd =>
        {
            FullScreen.Own.Add(hwnd);
            Closed += (_, _) => FullScreen.Own.Remove(hwnd);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        });
        Strings.LanguageChanged += ApplyLanguage;
        Closed += (_, _) => Strings.LanguageChanged -= ApplyLanguage;
    }

    private void CenterOnDisplay()
    {
        Left = (_display.Work.Left + (_display.Work.Width - ActualWidth * _display.Scale) / 2) / _display.Scale;
        Top = (_display.Work.Top + (_display.Work.Height - ActualHeight * _display.Scale) / 2) / _display.Scale;
    }

    private void RefreshTheme()
    {
        Background = (Brush)Application.Current.Resources["MenuBg"];
        Foreground = (Brush)Application.Current.Resources["MenuFg"];
        // The brand lockup bakes the theme's colours, so the sheet is rebuilt.
        Content = Build();
    }

    private void ApplyLanguage()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyLanguage));
            return;
        }

        Title = Strings.About;
        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        Content = Build();
    }

    private UIElement Build()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(WorkspaceRadius),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        root.SetResourceReference(Border.BackgroundProperty, "SettingsWorkspaceBg");
        root.SetResourceReference(Border.BorderBrushProperty, "SettingsWorkspaceBorder");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = Header();
        layout.Children.Add(header);

        var body = Body();
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var footer = Footer();
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        root.Child = layout;
        return root;
    }

    private FrameworkElement Header()
    {
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        var header = new Grid { Margin = new Thickness(20, 18, 16, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var markTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        markTile.SetResourceReference(Border.BackgroundProperty, "SettingsBrandTileBg");
        markTile.SetResourceReference(Border.BorderBrushProperty, "SettingsBrandTileBorder");
        if (Brand.Mark is { } mark)
        {
            markTile.Child = new Image
            {
                Source = CropBrandMark(mark),
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var fallback = Text("H", 18, 22, FontWeights.Bold);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            markTile.Child = fallback;
        }
        header.Children.Add(markTile);

        var identity = new StackPanel
        {
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = Text(
            Strings.ProductName,
            Typography.WindowTitleSize,
            Typography.WindowTitleLineHeight,
            FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = Strings.ProductName;
        titleRow.Children.Add(title);

        var versionPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(9, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        versionPill.SetResourceReference(Border.BackgroundProperty, "SettingsCardBg");
        versionPill.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        var versionLabel = Text(
            $"{Strings.Version} {version}",
            Typography.CaptionSize,
            Typography.CaptionLineHeight,
            foregroundResource: "SettingsTextTertiary");
        versionLabel.TextWrapping = TextWrapping.NoWrap;
        versionPill.Child = versionLabel;
        titleRow.Children.Add(versionPill);
        identity.Children.Add(titleRow);

        var tagline = Text(
            Strings.Tagline,
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        tagline.Margin = new Thickness(1, 2, 0, 0);
        tagline.TextWrapping = TextWrapping.Wrap;
        identity.Children.Add(tagline);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);

        var close = CloseButton();
        close.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && !IsWithinButton(e.OriginalSource))
                DragMove();
        };
        return header;
    }

    private FrameworkElement Body()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var aboutCard = SectionCard(Strings.About, Morphicon.Info, out var aboutBody);
        aboutBody.Children.Add(Paragraph(
            string.Format(Strings.AboutBody, Settings.Current.HotKey),
            Typography.BodySize,
            Typography.BodyLineHeight,
            "SettingsTextPrimary"));

        var inspirationCard = SectionCard(null, Morphicon.Link, out var inspirationBody);
        var inspiration = InspirationLine();
        inspirationBody.Children.Add(inspiration);
        var codex = Text(
            Strings.AboutCodex,
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        codex.Margin = new Thickness(0, 6, 0, 0);
        inspirationBody.Children.Add(codex);

        content.Children.Add(aboutCard);
        inspirationCard.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(inspirationCard);

        var scroll = new ScrollViewer
        {
            Content = content,
            Margin = new Thickness(20, 0, 16, 0),
            MaxHeight = _contentMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
        };
        return scroll;
    }

    private FrameworkElement Footer()
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(20, 0, 16, 16),
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSectionBg");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsSectionBorder");

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var disclaimer = Text(
            Strings.Disclaimer,
            Typography.CaptionSize,
            Typography.CaptionLineHeight,
            foregroundResource: "SettingsTextTertiary");
        disclaimer.VerticalAlignment = VerticalAlignment.Center;
        disclaimer.TextWrapping = TextWrapping.Wrap;
        var pathTip = new TextBlock { Text = Inbox.Folder };
        Typography.ApplyPath(pathTip);
        disclaimer.ToolTip = pathTip;
        footer.Children.Add(disclaimer);

        var github = GitHubButton();
        github.Margin = new Thickness(12, 0, 0, 0);
        github.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(github, 1);
        footer.Children.Add(github);

        card.Child = footer;
        return card;
    }

    private static Border SectionCard(string? title, Morphicon icon, out StackPanel body)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSectionBg");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsSectionBorder");

        var stack = new StackPanel();
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconChip = new Border
        {
            Width = IconSlotSize,
            Height = IconSlotSize,
            CornerRadius = new CornerRadius(IconSlotRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconChip.SetResourceReference(Border.BackgroundProperty, "SettingsCardBg");
        iconChip.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        iconChip.Child = Morphicons.CreatePath(icon, 14, 1.4, "SettingsNavLineIcon");
        heading.Children.Add(iconChip);

        if (!string.IsNullOrWhiteSpace(title))
        {
            var caption = Text(
                title,
                Typography.HeadingSize,
                Typography.HeadingLineHeight,
                FontWeights.SemiBold);
            caption.Margin = new Thickness(8, 0, 0, 0);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.TextWrapping = TextWrapping.NoWrap;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            caption.ToolTip = title;
            heading.Children.Add(caption);
        }

        stack.Children.Add(heading);
        body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(body);
        card.Child = stack;
        return card;
    }

    private static Button CloseButton()
    {
        var glyph = Morphicons.CreatePath(Morphicon.X, 14, 1.4, "MenuFg");
        var button = new Button
        {
            Content = glyph,
            Width = ControlHeight,
            Height = ControlHeight,
            MinWidth = ControlHeight,
            MinHeight = ControlHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Close,
            Focusable = true,
            IsTabStop = true,
        };
        button.SetResourceReference(StyleProperty, "SettingsWindowCloseButton");
        AutomationProperties.SetName(button, Strings.Close);
        button.Click += (_, _) => ((Window)Window.GetWindow(button)!).Close();
        return button;
    }

    private static Button GitHubButton()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = AuthorLinks.IconPath(AuthorLinks.GitHubGlyph, false, 16);
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SettingsTextPrimary");
        icon.Margin = new Thickness(0, 0, 6, 0);
        content.Children.Add(icon);
        var label = Text(
            Strings.VisitHomepage,
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextPrimary");
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Height = ControlHeight,
            MinHeight = ControlHeight,
            MinWidth = 86,
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = Strings.HomepageToolTip,
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(StyleProperty, "SettingsActionButton");
        AutomationProperties.SetName(button, Strings.HomepageToolTip);
        button.Click += (_, _) => AuthorLinks.OpenHomepage(Window.GetWindow(button)!);
        return button;
    }

    private static TextBlock InspirationLine()
    {
        const string firstMarker = "\uE000";
        const string secondMarker = "\uE001";
        var text = string.Format(Strings.AboutInspiration, firstMarker, secondMarker);
        var block = Text(
            string.Empty,
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        int first = text.IndexOf(firstMarker, StringComparison.Ordinal);
        int second = text.IndexOf(secondMarker, StringComparison.Ordinal);
        if (first < 0 || second < 0)
        {
            block.Text = Strings.AboutInspiration;
            return block;
        }

        const string tendederoLabel = "tendedero";
        const string tendederoUrl = "https://github.com/alejandrobujan/tendedero";
        const string snaplineLabel = "snapline";
        const string snaplineUrl = "https://github.com/mohabujubara/snapline";
        void AddPlain(string value)
        {
            if (!string.IsNullOrEmpty(value)) block.Inlines.Add(new Run(value));
        }

        void AddLink(string label, string url)
        {
            var link = new Hyperlink(new Run(label))
            {
                NavigateUri = new Uri(url),
                Cursor = Cursors.Hand,
                TextDecorations = null,
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentInk");
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            link.Click += (_, e) =>
            {
                e.Handled = true;
                if (!Shell.OpenUrl(url))
                {
                    MessageBox.Show(
                        Strings.HomepageOpenFailed,
                        Strings.AppName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            };
            block.Inlines.Add(link);
        }

        if (first < second)
        {
            AddPlain(text[..first]);
            AddLink(tendederoLabel, tendederoUrl);
            AddPlain(text[(first + firstMarker.Length)..second]);
            AddLink(snaplineLabel, snaplineUrl);
            AddPlain(text[(second + secondMarker.Length)..]);
        }
        else
        {
            AddPlain(text[..second]);
            AddLink(snaplineLabel, snaplineUrl);
            AddPlain(text[(second + secondMarker.Length)..first]);
            AddLink(tendederoLabel, tendederoUrl);
            AddPlain(text[(first + firstMarker.Length)..]);
        }
        return block;
    }

    private static TextBlock Paragraph(
        string text,
        double size,
        double lineHeight,
        string foregroundResource)
    {
        var block = Text(text, size, lineHeight, foregroundResource: foregroundResource);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private static TextBlock Text(
        string text,
        double size,
        double lineHeight,
        FontWeight? weight = null,
        string foregroundResource = "SettingsTextPrimary")
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        Typography.ApplyText(block);
        Typography.BlockLineHeight(block, lineHeight);
        return block;
    }

    private static bool IsWithinButton(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// The source mark has a large transparent canvas. Crop that canvas so the
    /// visible glyph can occupy the brand tile instead of looking undersized.
    /// </summary>
    private static BitmapSource? CropBrandMark(BitmapSource source)
    {
        const double left = 38d / 1024d;
        const double top = 219d / 1024d;
        const double right = 985d / 1024d;
        const double bottom = 840d / 1024d;

        int x = Math.Clamp((int)Math.Round(source.PixelWidth * left), 0, source.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(source.PixelHeight * top), 0, source.PixelHeight - 1);
        int rightEdge = Math.Clamp((int)Math.Ceiling(source.PixelWidth * right), x + 1, source.PixelWidth);
        int bottomEdge = Math.Clamp((int)Math.Ceiling(source.PixelHeight * bottom), y + 1, source.PixelHeight);
        try
        {
            var crop = new CroppedBitmap(source, new Int32Rect(x, y, rightEdge - x, bottomEdge - y));
            crop.Freeze();
            return crop;
        }
        catch (ArgumentException)
        {
            return source;
        }
    }

}
