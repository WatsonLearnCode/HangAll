using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;
using Line = HangAll.Core.Line;
using Path = System.Windows.Shapes.Path;

namespace HangAll.UI;

/// <summary>
/// The screenshot, enlarged, with a pen in your hand. Ballpoint, highlighter,
/// circle, box and arrow; undo and redo; every mark is saved into the file as
/// you go, and the untouched original is kept so you can always go back.
/// </summary>
public sealed class MarkupWindow : Window
{
    private static readonly Dictionary<string, MarkupWindow> Open = new(StringComparer.OrdinalIgnoreCase);
    private const double ControlHeight = 30;
    private const double CornerRadiusValue = 8;
    private static readonly Thickness ButtonPadding = new(12, 4, 12, 4);

    public static void Show(string path, Line line)
    {
        if (Open.TryGetValue(path, out var existing)) { existing.Activate(); return; }
        var full = Thumbnails.Load(path, int.MaxValue, 4);
        if (full is null) { System.Media.SystemSounds.Beep.Play(); return; }
        var w = new MarkupWindow(path, full.Value, line);
        Open[path] = w;
        w.Closed += (_, _) => Open.Remove(path);
        w.Show();
        w.Activate();
    }

    private enum Tool { Pen, Highlighter, Circle, Box, Arrow, Text, Blur }

    private readonly string _path;
    private readonly Line _line;
    private readonly BitmapSource _image;
    private readonly InkCanvas _ink;
    private readonly ShapeLayer _shapes;
    private readonly Grid _page;
    private readonly double _scale;      // display units per image pixel
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock _status = new();
    private readonly List<ToggleButton> _toolButtons = new();
    private readonly List<(Border swatch, Color color)> _swatches = new();
    private readonly List<ToggleButton> _sizeButtons = new();
    private readonly List<Ellipse> _sizeMarks = new();
    private readonly List<Path> _glyphs = new();
    private readonly List<Rectangle> _gaps = new();
    private readonly List<(Button button, bool accent)> _buttons = new();
    private readonly List<(Button button, string text)> _textButtons = new();
    private Tool _tool = Tool.Pen;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private int _size = 1; // 0 thin, 1 medium, 2 thick
    private bool _dirty;
    private Button _undoButton = null!, _redoButton = null!;
    private Border _chrome = null!;
    private Border _pageFrame = null!;
    private bool _dark;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0x1E, 0x88, 0xE5),
        Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0x12, 0x12, 0x12), Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private MarkupWindow(string path, Thumbnail full, Line line)
    {
        _path = path;
        _line = line;
        _image = full.Image;

        Title = $"{System.IO.Path.GetFileName(path)} · {Strings.MarkUp}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = true;
        Icon = Brand.Icon;
        Topmost = true;
        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        UseLayoutRounding = true;

        // Fit the picture comfortably into the screen it was taken on.
        var display = Displays.UnderPointer();
        double availW = display.Work.Width / display.Scale * 0.86, availH = display.Work.Height / display.Scale * 0.86 - 56;
        _scale = Math.Min(1, Math.Max(1, Math.Min(availW / full.PixelWidth, availH / full.PixelHeight)));
        if (full.PixelWidth * _scale < 360) _scale = Math.Min(360.0 / full.PixelWidth, availW / full.PixelWidth);
        double imgW = full.PixelWidth * _scale, imgH = full.PixelHeight * _scale;

        _dark = Theme.AppsDark;
        _chrome = new Border
        {
            Background = new SolidColorBrush(_dark ? Theme.Gray(0.13) : Theme.Gray(0.955)),
            BorderBrush = new SolidColorBrush(_dark ? Theme.Gray(1, 0.12) : Theme.Gray(0, 0.14)),
            BorderThickness = new Thickness(1),
        };
        var root = new DockPanel();
        _chrome.Child = root;
        Content = _chrome;
        Background = _chrome.Background;
        Foreground = new SolidColorBrush(Theme.Primary);

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // The page: the picture, the ink above it, the shapes above that.
        _page = new Grid { Width = imgW, Height = imgH, Margin = new Thickness(12, 4, 12, 12), ClipToBounds = true };
        _pageFrame = new Border
        {
            Child = Fast(new Image { Source = _image, Stretch = Stretch.Fill }),
            BorderBrush = new SolidColorBrush(Theme.Gray(_dark ? 1 : 0, 0.18)), BorderThickness = new Thickness(1),
        };
        _page.Children.Add(_pageFrame);
        _ink = new InkCanvas { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.Ink, Cursor = Cursors.Pen };
        _ink.StrokeCollected += (_, e) => Commit(e.Stroke);
        _page.Children.Add(_ink);
        _shapes = new ShapeLayer(this) { IsHitTestVisible = false };
        _page.Children.Add(_shapes);
        root.Children.Add(new Viewbox { Child = _page, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center });

        Width = imgW + 26; Height = imgH + 18 + 48;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var work = display.Work;
        Left = (work.Left + (work.Width - Width * display.Scale) / 2) / display.Scale;
        Top = (work.Top + (work.Height - Height * display.Scale) / 2) / display.Scale;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        KeyDown += OnKey;
        Closing += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); } };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _chrome || ReferenceEquals(e.OriginalSource, _chrome.Child)) DragMove(); };
        // The binder is kept alive by the static theme event and by this
        // window's Closed event, so it needs no field of its own.
        _ = new ThemeBinder(this, RefreshTheme, hwnd =>
        {
            FullScreen.Own.Add(hwnd);
            Closed += (_, _) => FullScreen.Own.Remove(hwnd);
            // Rounded corners and a shadow from the DWM, which costs nothing per frame.
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        });
        Strings.LanguageChanged += ApplyLanguage;
        Closed += (_, _) => Strings.LanguageChanged -= ApplyLanguage;

        ApplyTool();
    }

    /// <summary>Re-apply the type scale to an open editor.</summary>
    public static void RefreshTypography()
    {
        foreach (var window in Open.Values) Typography.Apply(window);
        foreach (var window in Open.Values) window.RefreshThemedStyles();
    }

    private void ApplyLanguage()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyLanguage));
            return;
        }

        Title = $"{System.IO.Path.GetFileName(_path)} · {Strings.MarkUp}";
        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        RefreshThemedStyles();
    }

    /// <summary>Light/dark switch: repaint the chrome and rebuild every themed brush.</summary>
    private void RefreshTheme()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshTheme));
            return;
        }

        _dark = Theme.AppsDark;
        var chromeBg = new SolidColorBrush(_dark ? Theme.Gray(0.13) : Theme.Gray(0.955));
        var chromeEdge = new SolidColorBrush(_dark ? Theme.Gray(1, 0.12) : Theme.Gray(0, 0.14));
        _chrome.Background = chromeBg;
        _chrome.BorderBrush = chromeEdge;
        Background = chromeBg;
        Foreground = new SolidColorBrush(Theme.Primary);
        _pageFrame.BorderBrush = new SolidColorBrush(Theme.Gray(_dark ? 1 : 0, 0.18));

        RefreshThemedStyles();
    }

    private void RefreshThemedStyles()
    {
        var foreground = new SolidColorBrush(Theme.Primary);
        foreach (var (button, accent) in _buttons) button.Style = ButtonStyle(_dark, accent);
        foreach (var button in _toolButtons) button.Style = ToolStyle(Theme.AppsDark);
        foreach (var button in _sizeButtons) button.Style = ToolStyle(Theme.AppsDark);
        foreach (var glyph in _glyphs) glyph.Stroke = foreground;
        foreach (var mark in _sizeMarks) mark.Fill = foreground;
        foreach (var gap in _gaps) gap.Fill = new SolidColorBrush(Theme.Gray(0.5, 0.35));
        _status.Foreground = new SolidColorBrush(Theme.Secondary);
        _status.FontSize = Typography.SmallSize;
        Typography.BlockLineHeight(_status, Typography.SmallLineHeight);
        ApplyTool();
    }

    private static Image Fast(Image image)
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        RenderOptions.SetCachingHint(image, CachingHint.Cache);
        return image;
    }

    // MARK: Toolbar

    private UIElement BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(12, 10, 12, 6), VerticalAlignment = VerticalAlignment.Center };

        void AddTool(Tool t, Morphicon glyph, string label)
        {
            var b = ToolButton(glyph, label, _dark);
            b.Click += (_, _) => { _tool = t; ApplyTool(); };
            _toolButtons.Add(b);
            bar.Children.Add(b);
        }
        AddTool(Tool.Pen, Morphicon.Pencil, Strings.Pen);
        AddTool(Tool.Highlighter, Morphicon.Highlighter, Strings.Highlighter);
        AddTool(Tool.Circle, Morphicon.Circle, Strings.Circle);
        AddTool(Tool.Box, Morphicon.Square, Strings.Box);
        AddTool(Tool.Arrow, Morphicon.ArrowUpRight, Strings.Arrow);
        AddTool(Tool.Text, Morphicon.Type, Strings.TextTool);
        AddTool(Tool.Blur, Morphicon.Grid3x3, Strings.Blur);
        bar.Children.Add(Gap());

        for (int i = 0; i < Palette.Length; i++)
        {
            var c = Palette[i];
            var swatch = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = Strings.Colour,
                BorderBrush = new SolidColorBrush(Theme.Gray(_dark ? 1 : 0, 0.25)), BorderThickness = new Thickness(1),
            };
            swatch.MouseLeftButtonDown += (_, e) => { e.Handled = true; _color = c; ApplyTool(); };
            _swatches.Add((swatch, c));
            bar.Children.Add(swatch);
        }
        bar.Children.Add(Gap());

        for (int i = 0; i < 3; i++)
        {
            int size = i;
            double d = 5 + i * 4;
            var mark = new Ellipse { Width = d, Height = d, Fill = Foreground };
            _sizeMarks.Add(mark);
            var b = new ToggleButton
            {
                Content = mark,
                Width = 30, Height = ControlHeight, Margin = new Thickness(1, 0, 1, 0), ToolTip = Strings.Thickness,
                Style = ToolStyle(_dark),
            };
            b.Click += (_, _) => { _size = size; ApplyTool(); };
            _sizeButtons.Add(b);
            bar.Children.Add(b);
        }
        bar.Children.Add(Gap());

        _undoButton = ActionButton(Morphicon.Undo2, Strings.Undo + " (Ctrl+Z)", _dark);
        _undoButton.Click += (_, _) => Undo();
        _redoButton = ActionButton(Morphicon.Redo2, Strings.Redo + " (Ctrl+Y)", _dark);
        _redoButton.Click += (_, _) => Redo();
        bar.Children.Add(_undoButton);
        bar.Children.Add(_redoButton);
        bar.Children.Add(Gap());

        var revert = TextButton(Strings.Revert, _dark);
        revert.Click += (_, _) => Revert();
        var copy = TextButton(Strings.Copy, _dark);
        copy.Click += (_, _) => { Save(); _line.CopyPath(_path); _status.Text = Strings.Copied; };
        var done = TextButton(Strings.Done, _dark, accent: true);
        done.Click += (_, _) => Close();
        bar.Children.Add(revert);
        bar.Children.Add(copy);
        bar.Children.Add(done);

        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_status);
        return bar;
    }

    private FrameworkElement Gap()
    {
        var gap = new Rectangle
        {
            Width = 1,
            Height = 20,
            Fill = new SolidColorBrush(Theme.Gray(0.5, 0.35)),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gaps.Add(gap);
        return gap;
    }

    private Style ToolStyle(bool dark)
    {
        var style = new Style(typeof(ToggleButton));
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(CornerRadiusValue));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Theme.Gray(dark ? 1 : 0, 0.08)), "Bd"));
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(dark ? Color.FromArgb(0x50, 0x4A, 0x8C, 0xFF) : Color.FromArgb(0x30, 0x1E, 0x66, 0xE5)), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(on);
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(ForegroundProperty, Foreground));
        return style;
    }

    private ToggleButton ToolButton(Morphicon glyph, string label, bool dark)
    {
        return new ToggleButton
        {
            Content = Glyph(glyph), Width = 32, Height = ControlHeight, Margin = new Thickness(1, 0, 1, 0), ToolTip = label, Style = ToolStyle(dark),
        };
    }

    private Button ActionButton(Morphicon glyph, string label, bool dark)
    {
        var b = new Button { Content = Glyph(glyph), Width = 32, Height = ControlHeight, Margin = new Thickness(1, 0, 1, 0), ToolTip = label };
        b.Style = ButtonStyle(dark, accent: false);
        _buttons.Add((b, false));
        return b;
    }

    private Button TextButton(string text, bool dark, bool accent = false)
    {
        var b = new Button { Content = text, Padding = ButtonPadding, MinHeight = ControlHeight, Margin = new Thickness(3, 0, 3, 0), FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal };
        b.Style = ButtonStyle(dark, accent);
        _buttons.Add((b, accent));
        _textButtons.Add((b, text));
        return b;
    }

    private Style ButtonStyle(bool dark, bool accent)
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(CornerRadiusValue));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(Border.BackgroundProperty, accent
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x66, 0xE5))
            : Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, accent ? new SolidColorBrush(Color.FromRgb(0x2A, 0x74, 0xF0)) : new SolidColorBrush(Theme.Gray(dark ? 1 : 0, 0.08)), "Bd"));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.35));
        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(ForegroundProperty, accent ? Brushes.White : Foreground));
        return style;
    }

    private Path Glyph(Morphicon icon)
    {
        var glyph = Morphicons.CreatePath(icon, 18, 1.6, Foreground);
        _glyphs.Add(glyph);
        return glyph;
    }

    // MARK: Tools

    private double Thickness => (_size switch { 0 => 2.2, 1 => 4, _ => 7 }) * Math.Max(0.6, Math.Min(1.6, 1 / _scale));

    private void ApplyTool()
    {
        for (int i = 0; i < _toolButtons.Count; i++) _toolButtons[i].IsChecked = (int)_tool == i;
        for (int i = 0; i < _sizeButtons.Count; i++) _sizeButtons[i].IsChecked = _size == i;
        foreach (var (swatch, swatchColor) in _swatches)
        {
            bool on = swatchColor == _color;
            swatch.BorderThickness = new Thickness(on ? 2.5 : 1);
            swatch.BorderBrush = on
                ? new SolidColorBrush(_dark ? Colors.White : Color.FromRgb(0x1E, 0x66, 0xE5))
                : new SolidColorBrush(Theme.Gray(_dark ? 1 : 0, 0.25));
        }

        bool inking = _tool is Tool.Pen or Tool.Highlighter;
        _ink.EditingMode = inking ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        _shapes.IsHitTestVisible = !inking;
        _shapes.Cursor = _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        if (_tool != Tool.Text) CommitText();
        bool hi = _tool == Tool.Highlighter;
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = hi ? Color.FromArgb(0x70, _color.R, _color.G, _color.B) : _color,
            Width = hi ? Thickness * 4 : Thickness,
            Height = hi ? Thickness * 4 : Thickness,
            IsHighlighter = hi,
            StylusTip = hi ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = true,
            IgnorePressure = true,
        };
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textEntry is not null && _textEntry.IsKeyboardFocused) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { Close(); return; }
        if (ctrl && e.Key == Key.Z) { Undo(); return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))) { Redo(); return; }
        if (ctrl && e.Key == Key.C) { Save(); _line.CopyPath(_path); _status.Text = Strings.Copied; return; }
        if (ctrl) return;
        switch (e.Key)
        {
            case Key.P: _tool = Tool.Pen; break;
            case Key.H: _tool = Tool.Highlighter; break;
            case Key.C: _tool = Tool.Circle; break;
            case Key.B: case Key.R: _tool = Tool.Box; break;
            case Key.A: _tool = Tool.Arrow; break;
            case Key.T: _tool = Tool.Text; break;
            case Key.X: _tool = Tool.Blur; break;
            case Key.D1: _size = 0; break;
            case Key.D2: _size = 1; break;
            case Key.D3: _size = 2; break;
            default: return;
        }
        ApplyTool();
    }

    // MARK: Marks, undo, redo

    private void Commit(object mark)
    {
        _undo.Push(mark);
        _redo.Clear();
        _dirty = true;
        ApplyTool();
        _status.Text = "";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var mark = _undo.Pop();
        if (mark is Stroke s) _ink.Strokes.Remove(s); else if (mark is ShapeMark m) _shapes.Remove(m);
        _redo.Push(mark);
        _dirty = true;
        ApplyTool();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var mark = _redo.Pop();
        if (mark is Stroke s) _ink.Strokes.Add(s); else if (mark is ShapeMark m) _shapes.Add(m);
        _undo.Push(mark);
        _dirty = true;
        ApplyTool();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    // MARK: Saving

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            // The untouched picture is kept once, so Revert is always possible.
            // A backup that will not copy is no reason to lose the annotations.
            try { Originals.Keep(_path); }
            catch (Exception keepError) { Log.Error($"Could not keep the original: {keepError.Message}"); }

            int pw = _image.PixelWidth, ph = _image.PixelHeight;
            CommitText();
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_image, new Rect(0, 0, pw, ph));
                dc.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));
                _ink.Strokes.Draw(dc);
                _shapes.Draw(dc);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            BitmapEncoder encoder = System.IO.Path.GetExtension(_path).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            var tmp = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
                File.Move(tmp, _path, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            _dirty = false;
            _status.Text = Strings.Saved;
            _line.ReloadThumbnail(_path);
        }
        catch (Exception e)
        {
            Log.Error($"Could not save the markup: {e.Message}");
            _status.Text = Strings.CouldNotSave;
        }
    }

    private void Revert()
    {
        _saveTimer.Stop();
        _ink.Strokes.Clear();
        _shapes.Clear();
        _undo.Clear();
        _redo.Clear();
        try
        {
            if (Originals.Revert(_path)) _line.ReloadThumbnail(_path);
            // The editor keeps showing the picture it opened with, which is the original if it was never marked before.
            var fresh = Thumbnails.Load(_path, int.MaxValue, 3);
            if (fresh is not null) ((Image)((Border)_page.Children[0]).Child).Source = fresh.Value.Image;
        }
        catch (Exception e) { Log.Error($"Could not revert: {e.Message}"); }
        _dirty = false;
        _status.Text = Strings.Reverted;
        ApplyTool();
    }


    // MARK: Shapes

    private sealed record ShapeMark(Tool Kind, Point From, Point To, Color Color, double Thickness, string? Text = null);

    private static double FontSizeFor(double thickness) => thickness * 4.5 + 6;

    // MARK: Text

    private TextBox? _textEntry;

    /// <summary>A click with the text tool: type where you clicked, Enter to keep it, Esc to drop it.</summary>
    private void BeginText(Point at)
    {
        CommitText();
        var box = new TextBox
        {
            MinWidth = 60, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x1E, 0x66, 0xE5)), BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(_color), FontSize = FontSizeFor(Thickness), FontWeight = FontWeights.SemiBold,
            FontFamily = Typography.Ui, Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(at.X - 3, at.Y - FontSizeFor(Thickness) * 0.7, 0, 0),
            Tag = at,
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; DropText(); }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; CommitText(); }
        };
        box.LostKeyboardFocus += (_, _) => CommitText();
        _textEntry = box;
        _page.Children.Add(box);
        box.Focus();
    }

    private void CommitText()
    {
        var box = _textEntry;
        if (box is null) return;
        _textEntry = null;
        _page.Children.Remove(box);
        var text = box.Text.TrimEnd();
        if (text.Length == 0) return;
        var at = (Point)box.Tag;
        var mark = new ShapeMark(Tool.Text, at, at, ((SolidColorBrush)box.Foreground).Color, (box.FontSize - 6) / 4.5, text);
        _shapes.Add(mark);
        Commit(mark);
    }

    private void DropText()
    {
        var box = _textEntry;
        if (box is null) return;
        _textEntry = null;
        _page.Children.Remove(box);
    }

    // MARK: Blur

    private BitmapSource? _pixelated;

    /// <summary>The picture in coarse blocks, for hiding what should not be shared.</summary>
    private BitmapSource Pixelated()
    {
        if (_pixelated is not null) return _pixelated;
        int block = Math.Clamp(_image.PixelWidth / 96, 8, 40);
        var small = new TransformedBitmap(_image, new ScaleTransform(1.0 / block, 1.0 / block));
        small.Freeze();
        _pixelated = small;
        return small;
    }

    /// <summary>Circles, boxes, arrows, text and blurs. They live here, above the ink.</summary>
    private sealed class ShapeLayer : FrameworkElement
    {
        private readonly MarkupWindow _owner;
        private readonly List<ShapeMark> _marks = new();
        private ShapeMark? _live;
        private Point _start;

        public ShapeLayer(MarkupWindow owner)
        {
            _owner = owner;
            // Blurs are drawn as enlarged blocks: no smoothing.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        public void Add(ShapeMark m) { _marks.Add(m); InvalidateVisual(); }
        public void ShowLive(ShapeMark? m) { _live = m; InvalidateVisual(); }
        public void Remove(ShapeMark m) { _marks.Remove(m); InvalidateVisual(); }
        public void Clear() { _marks.Clear(); InvalidateVisual(); }

        protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _start = e.GetPosition(this);
            if (_owner._tool == Tool.Text)
            {
                _owner.BeginText(_start);
                e.Handled = true;
                return;
            }
            _live = new ShapeMark(_owner._tool, _start, _start, _owner._color, _owner.Thickness);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_live is null) return;
            _live = _live with { To = e.GetPosition(this) };
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_live is null) return;
            ReleaseMouseCapture();
            var mark = _live with { To = e.GetPosition(this) };
            _live = null;
            if ((mark.To - mark.From).Length >= 4) { _marks.Add(mark); _owner.Commit(mark); }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            Draw(dc);
        }

        public void Draw(DrawingContext dc)
        {
            // Blurs go under everything else, so a circle around a hidden bit stays visible.
            foreach (var m in _marks) if (m.Kind == Tool.Blur) DrawMark(dc, m);
            if (_live is { Kind: Tool.Blur }) DrawMark(dc, _live);
            foreach (var m in _marks) if (m.Kind != Tool.Blur) DrawMark(dc, m);
            if (_live is { } live && live.Kind != Tool.Blur) DrawMark(dc, live);
        }

        private void DrawMark(DrawingContext dc, ShapeMark m)
        {
            var pen = new Pen(new SolidColorBrush(m.Color), m.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var rect = new Rect(m.From, m.To);
            switch (m.Kind)
            {
                case Tool.Text:
                {
                    var ft = new FormattedText(m.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture,
                        Strings.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                        new Typeface(Typography.ForText(m.Text), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        FontSizeFor(m.Thickness), new SolidColorBrush(m.Color), 1.0);
                    var origin = new Point(m.From.X, m.From.Y - FontSizeFor(m.Thickness) * 0.7);
                    // A thin halo in the opposite tone keeps the words readable over anything.
                    var halo = m.Color.R + m.Color.G + m.Color.B > 380 ? Color.FromArgb(0xA0, 0, 0, 0) : Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF);
                    var geometry = ft.BuildGeometry(origin);
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(halo), Math.Max(1.5, m.Thickness * 0.5)) { LineJoin = PenLineJoin.Round }, geometry);
                    dc.DrawText(ft, origin);
                    break;
                }
                case Tool.Blur:
                {
                    if (rect.Width < 1 || rect.Height < 1) break;
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawImage(_owner.Pixelated(), new Rect(0, 0, _owner._page.Width, _owner._page.Height));
                    dc.Pop();
                    break;
                }
                case Tool.Circle:
                    dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                    break;
                case Tool.Box:
                    dc.DrawRoundedRectangle(null, pen, rect, m.Thickness, m.Thickness);
                    break;
                case Tool.Arrow:
                {
                    dc.DrawLine(pen, m.From, m.To);
                    var dir = m.To - m.From;
                    if (dir.Length < 1) break;
                    dir.Normalize();
                    double head = Math.Max(10, m.Thickness * 4);
                    var left = m.To - dir * head + new Vector(-dir.Y, dir.X) * head * 0.55;
                    var right = m.To - dir * head - new Vector(-dir.Y, dir.X) * head * 0.55;
                    dc.DrawLine(pen, m.To, left);
                    dc.DrawLine(pen, m.To, right);
                    break;
                }
            }
        }
    }
}
