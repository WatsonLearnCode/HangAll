using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HangAll.UI;

internal enum MenuGlyph
{
    Line,
    TakeDown,
    Capture,
    Note,
    QuickSettings,
    FolderImages,
    FolderInbox,
    Settings,
    About,
    Quit,
    Copy,
    CopyPath,
    CopyFileName,
    CopyMarkdown,
    CopyBase64,
    Open,
    Edit,
    Ocr,
    Reveal,
    Pin,
    Save,
    Discard,
    Recycle,
    Play,
    MultiSelect,
    ClearSelection,
    Clipboard,
    Drop,
    DropZone,
    ClipboardHang,
    KeepOpen,
    DragOut,
    Sound,
    Startup,
    Colour
}

/// <summary>
/// The shared line-art vocabulary used by the main menu, card menus and their
/// side panels. Every glyph is drawn at 18x18 inside the menu's 24px slot.
/// </summary>
internal static class MenuGlyphs
{
    public const double Size = 18;

    public static FrameworkElement Create(MenuGlyph glyph, bool accent = false)
    {
        var path = Morphicons.CreatePath(
            Glyph(glyph),
            Size,
            1.5,
            accent ? "AccentFill" : "MenuFg");

        var slot = new Canvas
        {
            Width = Size,
            Height = Size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            UseLayoutRounding = true,
        };
        slot.Children.Add(path);
        return slot;
    }

    public static FrameworkElement Swatch(Color color)
    {
        var dot = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        return new Grid
        {
            Width = Size,
            Height = Size,
            Children = { dot },
            IsHitTestVisible = false,
        };
    }

    private static Morphicon Glyph(MenuGlyph glyph) => glyph switch
    {
        MenuGlyph.Line => Morphicon.PanelTop,
        MenuGlyph.TakeDown => Morphicon.ArrowDownToLine,
        MenuGlyph.Capture => Morphicon.Camera,
        MenuGlyph.Note => Morphicon.StickyNote,
        MenuGlyph.QuickSettings => Morphicon.SlidersHorizontal,
        MenuGlyph.FolderImages => Morphicon.Images,
        MenuGlyph.FolderInbox => Morphicon.Inbox,
        MenuGlyph.Settings => Morphicon.Settings,
        MenuGlyph.About => Morphicon.Info,
        MenuGlyph.Quit => Morphicon.Power,
        MenuGlyph.Copy => Morphicon.Copy,
        MenuGlyph.CopyPath => Morphicon.Link,
        MenuGlyph.CopyFileName => Morphicon.FileText,
        MenuGlyph.CopyMarkdown => Morphicon.ArrowUpRight,
        MenuGlyph.CopyBase64 => Morphicon.Type,
        MenuGlyph.Open => Morphicon.ExternalLink,
        MenuGlyph.Edit => Morphicon.Pencil,
        MenuGlyph.Ocr => Morphicon.ScanText,
        MenuGlyph.Reveal => Morphicon.FolderOpen,
        MenuGlyph.Pin => Morphicon.Pin,
        MenuGlyph.Save => Morphicon.Save,
        MenuGlyph.Discard => Morphicon.Trash2,
        MenuGlyph.Recycle => Morphicon.Recycle,
        MenuGlyph.Play => Morphicon.Play,
        MenuGlyph.MultiSelect => Morphicon.LayoutGrid,
        MenuGlyph.ClearSelection => Morphicon.X,
        MenuGlyph.Clipboard => Morphicon.Clipboard,
        MenuGlyph.Drop => Morphicon.Upload,
        MenuGlyph.DropZone => Morphicon.Scan,
        MenuGlyph.ClipboardHang => Morphicon.ClipboardPaste,
        MenuGlyph.KeepOpen => Morphicon.Pin,
        MenuGlyph.DragOut => Morphicon.MoveUpRight,
        MenuGlyph.Sound => Morphicon.Volume2,
        MenuGlyph.Startup => Morphicon.Rocket,
        MenuGlyph.Colour => Morphicon.Palette,
        _ => Morphicon.Info,
    };
}
