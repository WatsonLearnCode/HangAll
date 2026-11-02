using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;
using static HangAll.Interop.Native;

namespace HangAll.UI;

/// <summary>A compact preview anchored below the card that opened it.</summary>
internal sealed class QuickLookWindow : Window
{
    private const double CompactScale = 0.8;
    private const double TargetShortEdge = 200;
    private const double WindowInset = 24;
    private const double CardGap = 8;
    private const double PreviewMinWidth = 300;
    private const double PreviewMinHeight = 112;
    private const double HeaderHeight = 48;
    private const double ContentGap = 8;
    private const double SurfaceInset = 12;
    private const double SurfaceRadius = 16;
    private const double ImageRadius = 8;

    private static readonly TimeSpan PointerPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PointerAwayDelay = TimeSpan.FromMilliseconds(350);

    private Display _display;
    private Rect _cardRect;
    private readonly Border _surface;
    private readonly RowDefinition _headerRow;
    private readonly Border _bodyHost;
    private readonly Image _photo;
    private readonly Button _close;
    private readonly TextBlock _fileName;
    private readonly TextBlock _metadata;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _pointerTimer =
        new(DispatcherPriority.Background) { Interval = PointerPollInterval };
    private DateTime? _awaySince;
    private IntPtr _handle;
    private IntPtr _previousForeground;

    public QuickLookWindow(Pegged item, Display display, Rect cardRect)
    {
        _display = display;
        _cardRect = cardRect;
        Title = Strings.AppName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        _surface = Glass.Frame(SurfaceRadius * CompactScale);

        var root = new Grid();
        _headerRow = new RowDefinition { Height = new GridLength(HeaderHeight * CompactScale) };
        root.RowDefinitions.Add(_headerRow);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _fileName = new TextBlock
        {
            FontFamily = Typography.ForText(item.Path),
            FontSize = Typography.ControlSize,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typography.ApplyText(_fileName);
        Typography.BlockLineHeight(_fileName, Typography.ControlLineHeight);

        _metadata = new TextBlock
        {
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        Typography.ApplyText(_metadata);
        Typography.BlockLineHeight(_metadata, Typography.SmallLineHeight);

        labels.Children.Add(_fileName);
        labels.Children.Add(_metadata);
        header.Children.Add(labels);

        _close = new Button
        {
            Content = Morphicons.CreatePath(Morphicon.X, 12, 1.4, "MenuFg"),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Close,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _close.Click += (_, _) => Close();
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);
        root.Children.Add(header);

        _photo = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_photo, BitmapScalingMode.HighQuality);
        _message = new TextBlock
        {
            FontFamily = Typography.Ui,
            FontSize = Typography.BodySize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Typography.ApplyText(_message);
        Typography.BlockLineHeight(_message, Typography.BodyLineHeight);

        var body = new Grid();
        body.Children.Add(_photo);
        body.Children.Add(_message);
        _bodyHost = new Border
        {
            ClipToBounds = true,
            Child = body,
        };
        _bodyHost.SizeChanged += (_, _) => UpdateBodyClip();
        Grid.SetRow(_bodyHost, 1);
        root.Children.Add(_bodyHost);

        _surface.Child = root;
        Content = _surface;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Escape or Key.Space)) return;
            e.Handled = true;
            Close();
        };
        Loaded += (_, _) =>
        {
            UpdateBodyClip();
            PositionBelowCard();
            Activate();
            Focus();
            _pointerTimer.Start();
        };
        _pointerTimer.Tick += (_, _) => CheckPointer();
        Closed += (_, _) =>
        {
            _pointerTimer.Stop();
            if (_handle != IntPtr.Zero) FullScreen.Own.Remove(_handle);
            _handle = IntPtr.Zero;
            // Handing the foreground back while one of our menus is open killed
            // that menu (a popup closes as soon as the foreground moves), which
            // is what made a card menu die halfway down. The menu owns the
            // foreground until it closes, then Menus hands it back itself.
            if (_previousForeground != IntPtr.Zero && !Menus.AnyOpen)
                SetForegroundWindow(_previousForeground);
        };
        // Kept alive by the static theme event and by this window's Closed event.
        _ = new ThemeBinder(this, RefreshTheme, hwnd =>
        {
            _handle = hwnd;
            FullScreen.Own.Add(hwnd);
        });

        UpdatePreview(item, display, cardRect);
    }

    public Guid ItemId { get; private set; }

    public void UpdatePreview(Pegged item, Display display, Rect cardRect)
    {
        _display = display;
        _cardRect = cardRect;
        ItemId = item.Id;

        double uiScale = CompactScale;
        double targetShortEdge = TargetShortEdge * Settings.QuickLookScaleOption(Settings.Current.QuickLookScale);
        double surfaceInset = SurfaceInset * uiScale;
        double headerHeight = HeaderHeight * uiScale;
        double contentGap = ContentGap * uiScale;
        double previewMinWidth = PreviewMinWidth * uiScale;
        double previewMinHeight = PreviewMinHeight * uiScale;
        double windowInset = WindowInset * uiScale;
        double displayScale = Math.Max(0.1, display.Scale);
        double workWidth = display.Work.Width / displayScale;
        double workHeight = display.Work.Height / displayScale;
        double availableBelow =
            (display.Work.Bottom - cardRect.Bottom - CardGap * uiScale * displayScale) / displayScale;
        double maxWindowWidth = Math.Max(previewMinWidth, workWidth - windowInset);
        double maxWindowHeight = Math.Max(
            previewMinHeight,
            Math.Min(workHeight - windowInset, availableBelow));
        double maxImageWidth = Math.Max(1, maxWindowWidth - surfaceInset * 2);
        double maxImageHeight = Math.Max(
            1,
            maxWindowHeight - surfaceInset * 2 - headerHeight - contentGap);

        _surface.Padding = new Thickness(surfaceInset);
        _surface.CornerRadius = new CornerRadius(SurfaceRadius * uiScale);
        _headerRow.Height = new GridLength(headerHeight);
        _bodyHost.Margin = new Thickness(0, contentGap, 0, 0);
        _bodyHost.CornerRadius = new CornerRadius(ImageRadius * uiScale);
        _close.Width = 28 * uiScale;
        _close.Height = 28 * uiScale;
        _close.MinWidth = 28 * uiScale;
        _close.MinHeight = 28 * uiScale;

        _fileName.Text = Path.GetFileName(item.Path);
        _fileName.FontFamily = Typography.ForText(item.Path);
        _fileName.ToolTip = item.Path;

        var preview = LoadPreview(item);
        _metadata.Text = ImageMetadata(item, preview.PixelWidth, preview.PixelHeight);
        if (preview.Image is { } image)
        {
            double shortEdge = Math.Max(1, Math.Min(image.PixelWidth, image.PixelHeight));
            double fit = Math.Min(
                1,
                Math.Min(
                    targetShortEdge / shortEdge,
                    Math.Min(maxImageWidth / image.PixelWidth, maxImageHeight / image.PixelHeight)));
            double imageWidth = Math.Max(1, image.PixelWidth * fit);
            double imageHeight = Math.Max(1, image.PixelHeight * fit);
            Width = Math.Min(imageWidth + surfaceInset * 2, maxWindowWidth);
            Height = Math.Clamp(
                imageHeight + headerHeight + contentGap + surfaceInset * 2,
                previewMinHeight,
                maxWindowHeight);
            _photo.Source = image;
            _photo.Visibility = Visibility.Visible;
            _message.Visibility = Visibility.Collapsed;
        }
        else
        {
            Width = Math.Min(380 * uiScale, maxWindowWidth);
            Height = Math.Min(132 * uiScale, maxWindowHeight);
            _message.Text = preview.Message ?? Strings.QuickLookFailed;
            _message.Margin = new Thickness(12 * uiScale, 0, 12 * uiScale, 0);
            _photo.Source = null;
            _photo.Visibility = Visibility.Collapsed;
            _message.Visibility = Visibility.Visible;
        }

        RefreshTheme();
        if (IsLoaded)
        {
            UpdateLayout();
            UpdateBodyClip();
            PositionBelowCard();
        }
    }

    public void ShowPreview()
    {
        _previousForeground = GetForegroundWindow();
        Show();
    }

    private void PositionBelowCard()
    {
        double displayScale = Math.Max(0.1, _display.Scale);
        double width = ActualWidth * displayScale;
        double height = ActualHeight * displayScale;
        double margin = WindowInset * CompactScale * displayScale / 2;
        var work = _display.Work;

        double minLeft = work.Left + margin;
        double maxLeft = work.Right - width - margin;
        double left = _cardRect.Left + (_cardRect.Width - width) / 2;
        left = maxLeft < minLeft ? minLeft : Math.Clamp(left, minLeft, maxLeft);

        double minTop = work.Top + margin;
        double maxTop = work.Bottom - height - margin;
        double top = _cardRect.Bottom + CardGap * CompactScale * displayScale;
        top = maxTop < minTop ? minTop : Math.Clamp(top, minTop, maxTop);

        Left = left / displayScale;
        Top = top / displayScale;
    }

    private void UpdateBodyClip()
    {
        if (_bodyHost.ActualWidth <= 0 || _bodyHost.ActualHeight <= 0)
        {
            _bodyHost.Clip = null;
            return;
        }

        double radius = ImageRadius * CompactScale;
        _bodyHost.Clip = new RectangleGeometry(
            new Rect(0, 0, _bodyHost.ActualWidth, _bodyHost.ActualHeight),
            radius,
            radius);
    }

    private void CheckPointer()
    {
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var preview))
        {
            _awaySince = null;
            return;
        }

        var pointer = Displays.Cursor();
        bool keepOpen = Contains(_cardRect, pointer) ||
                        preview.Contains(pointer) ||
                        InConnector(pointer, preview);
        if (keepOpen)
        {
            _awaySince = null;
            return;
        }

        _awaySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _awaySince >= PointerAwayDelay) Close();
    }

    private bool InConnector(POINT pointer, RECT preview)
    {
        double top = Math.Min(_cardRect.Bottom, preview.Top);
        double bottom = Math.Max(_cardRect.Bottom, preview.Top);
        double left = Math.Max(_cardRect.Left, preview.Left);
        double right = Math.Min(_cardRect.Right, preview.Right);
        if (right <= left)
        {
            left = Math.Min(_cardRect.Left, preview.Left);
            right = Math.Max(_cardRect.Right, preview.Right);
        }
        return left <= pointer.X && pointer.X <= right &&
               top <= pointer.Y && pointer.Y <= bottom;
    }

    private static bool Contains(Rect rect, POINT point) =>
        rect.Left <= point.X && point.X < rect.Right &&
        rect.Top <= point.Y && point.Y < rect.Bottom;

    private void RefreshTheme()
    {
        Glass.Paint(_surface);
        _fileName.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Primary));
        _metadata.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Secondary));
        _message.Foreground = Theme.Freeze(new SolidColorBrush(Theme.Secondary));
        _bodyHost.Background = Theme.Freeze(new SolidColorBrush(Theme.Gray(0, Theme.AppsDark ? 0.24 : 0.06)));
    }

    private static string ImageMetadata(Pegged item, int pixelWidth, int pixelHeight)
    {
        var parts = new List<string>();
        string format = Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
        if (format.Length > 0) parts.Add(format);

        int width = item.PixelWidth > 0 ? item.PixelWidth : pixelWidth;
        int height = item.PixelHeight > 0 ? item.PixelHeight : pixelHeight;
        if (width > 0 && height > 0) parts.Add($"{width} × {height}");

        string size = FileSize(item.Path);
        if (size.Length > 0) parts.Add(size);
        return string.Join(" · ", parts);
    }

    private static string FileSize(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
        }
        catch
        {
            return "";
        }
    }

    private static (BitmapSource? Image, string? Message, int PixelWidth, int PixelHeight) LoadPreview(Pegged item)
    {
        if (!File.Exists(item.Path)) return (null, Strings.QuickLookMissing, 0, 0);
        if (!Thumbnails.IsImage(item.Path)) return (null, Strings.QuickLookUnsupported, 0, 0);

        int maxPixels = Math.Clamp(Math.Max(item.PixelWidth, item.PixelHeight), 1024, 8192);
        var loaded = Thumbnails.Load(item.Path, maxPixels, attempts: 1);
        return loaded is { } thumb
            ? (thumb.Image, null, thumb.PixelWidth, thumb.PixelHeight)
            : (null, Strings.QuickLookFailed, 0, 0);
    }
}
