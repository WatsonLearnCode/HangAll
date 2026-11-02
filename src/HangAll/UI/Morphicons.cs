using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HangAll.UI;

/// <summary>
/// Semantic icons from Morphicons' default Lucide vocabulary.
/// All geometry uses the Lucide 24x24 grid and is rendered as one-color line art.
/// </summary>
internal enum Morphicon
{
    PanelTop,
    ArrowDownToLine,
    Camera,
    StickyNote,
    SlidersHorizontal,
    Images,
    Inbox,
    Settings,
    Info,
    Power,
    Copy,
    ExternalLink,
    Pencil,
    ScanText,
    FolderOpen,
    Pin,
    Save,
    Trash2,
    Recycle,
    Eraser,
    Play,
    LayoutGrid,
    X,
    Clipboard,
    Upload,
    Scan,
    ClipboardPaste,
    MoveUpRight,
    Volume2,
    Rocket,
    Palette,
    Layers,
    RefreshCw,
    Eye,
    FileText,
    Globe,
    Search,
    Ellipsis,
    Paperclip,
    Check,
    Highlighter,
    Circle,
    Square,
    ArrowUpRight,
    Type,
    Grid3x3,
    Plus,
    Minus,
    Undo2,
    Redo2,
    Link,
}

internal static class Morphicons
{
    public const double GridSize = 24;

    public static Path CreatePath(
        Morphicon icon,
        double size,
        double strokeThickness,
        Brush stroke)
    {
        var path = CreateOutline(icon, size, strokeThickness);
        path.Stroke = stroke;
        return path;
    }

    public static Path CreatePath(
        Morphicon icon,
        double size,
        double strokeThickness,
        string strokeResource)
    {
        var path = CreateOutline(icon, size, strokeThickness);
        path.SetResourceReference(Shape.StrokeProperty, strokeResource);
        return path;
    }

    public static Path CreateFilledPath(Morphicon icon, double size, Brush fill)
    {
        return new Path
        {
            Data = System.Windows.Media.Geometry.Parse(PathData(icon)),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = fill,
            Stroke = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
        };
    }

    public static Geometry Geometry(Morphicon icon) => System.Windows.Media.Geometry.Parse(PathData(icon));

    public static string PathData(Morphicon icon) => icon switch
    {
        Morphicon.PanelTop =>
            "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z " +
            "M3 9 H21",
        Morphicon.ArrowDownToLine =>
            "M12 17 V3 M6 11 L12 17 L18 11 M19 21 H5",
        Morphicon.Camera =>
            "M13.997 4 A2 2 0 0 1 15.757 5.05 L16.243 5.95 A2 2 0 0 0 18.003 7 H20 " +
            "A2 2 0 0 1 22 9 V18 A2 2 0 0 1 20 20 H4 A2 2 0 0 1 2 18 V9 A2 2 0 0 1 4 7 H5.997 " +
            "A2 2 0 0 0 7.756 5.952 L8.245 5.048 A2 2 0 0 1 10.004 4 Z " +
            "M15 13 A3 3 0 1 1 9 13 A3 3 0 1 1 15 13",
        Morphicon.StickyNote =>
            "M21 9 A2.4 2.4 0 0 0 20.294 7.294 L16.706 3.706 A2.4 2.4 0 0 0 15 3 H5 " +
            "A2 2 0 0 0 3 5 V19 A2 2 0 0 0 5 21 H19 A2 2 0 0 0 21 19 Z " +
            "M15 3 V8 A1 1 0 0 0 16 9 H21",
        Morphicon.SlidersHorizontal =>
            "M10 5 H3 M12 19 H3 M14 3 V7 M16 17 V21 M21 12 H12 M21 19 H16 M21 5 H14 M8 10 V14 M8 12 H3",
        Morphicon.Images =>
            "M22 11 L20.704 9.704 A2.4 2.4 0 0 0 17.296 9.704 L11 16 " +
            "M4 8 A2 2 0 0 0 2 10 V20 A2 2 0 0 0 4 22 H14 A2 2 0 0 0 16 20 " +
            "M14 7 A1 1 0 1 1 12 7 A1 1 0 1 1 14 7 " +
            "M10 2 H20 A2 2 0 0 1 22 4 V14 A2 2 0 0 1 20 16 H10 A2 2 0 0 1 8 14 V4 A2 2 0 0 1 10 2 Z",
        Morphicon.Inbox =>
            "M22 12 H16 L14 15 H10 L8 12 H2 " +
            "M5.45 5.11 L2 12 V18 A2 2 0 0 0 4 20 H20 A2 2 0 0 0 22 18 V12 L18.55 5.11 " +
            "A2 2 0 0 0 16.76 4 H7.24 A2 2 0 0 0 5.45 5.11 Z",
        Morphicon.Settings =>
            "M9.671 4.136 A2.34 2.34 0 0 1 14.33 4.136 A2.34 2.34 0 0 0 17.649 6.051 " +
            "A2.34 2.34 0 0 1 19.979 10.084 A2.34 2.34 0 0 0 19.979 13.915 " +
            "A2.34 2.34 0 0 1 17.649 17.948 A2.34 2.34 0 0 0 14.33 19.863 " +
            "A2.34 2.34 0 0 1 9.671 19.863 A2.34 2.34 0 0 0 6.351 17.948 " +
            "A2.34 2.34 0 0 1 4.021 13.915 A2.34 2.34 0 0 0 4.021 10.084 " +
            "A2.34 2.34 0 0 1 6.35 6.051 A2.34 2.34 0 0 0 9.671 4.136 Z " +
            "M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12",
        Morphicon.Info =>
            "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 16 V12 M12 8 H12.01",
        Morphicon.Power =>
            "M12 2 V12 M18.4 6.6 A9 9 0 1 1 5.63 6.64",
        Morphicon.Copy =>
            "M10 8 H20 A2 2 0 0 1 22 10 V20 A2 2 0 0 1 20 22 H10 A2 2 0 0 1 8 20 V10 A2 2 0 0 1 10 8 Z " +
            "M4 16 C2.9 16 2 15.1 2 14 V4 C2 2.9 2.9 2 4 2 H14 C15.1 2 16 2.9 16 4",
        Morphicon.ExternalLink =>
            "M15 3 H21 V9 M10 14 L21 3 M18 13 V19 A2 2 0 0 1 16 21 H5 A2 2 0 0 1 3 19 V8 " +
            "A2 2 0 0 1 5 6 H11",
        Morphicon.Pencil =>
            "M21.174 6.812 A1 1 0 0 0 17.188 2.825 L3.842 16.174 A2 2 0 0 0 3.342 17.004 " +
            "L2.021 21.356 A0.5 0.5 0 0 0 2.644 21.978 L6.997 20.658 A2 2 0 0 0 7.827 20.161 Z " +
            "M15 5 L19 9",
        Morphicon.ScanText =>
            "M3 7 V5 A2 2 0 0 1 5 3 H7 M17 3 H19 A2 2 0 0 1 21 5 V7 " +
            "M21 17 V19 A2 2 0 0 1 19 21 H17 M7 21 H5 A2 2 0 0 1 3 19 V17 " +
            "M7 8 H15 M7 12 H17 M7 16 H13",
        Morphicon.FolderOpen =>
            "M6 14 L7.5 11.1 A2 2 0 0 1 9.24 10 H20 A2 2 0 0 1 21.94 12.5 L20.4 18.5 " +
            "A2 2 0 0 1 18.45 20 H4 A2 2 0 0 1 2 18 V5 A2 2 0 0 1 4 3 H7.9 " +
            "A2 2 0 0 1 9.59 3.9 L10.4 5.1 A2 2 0 0 0 12.07 6 H18 A2 2 0 0 1 20 8 V10",
        Morphicon.Pin =>
            "M12 17 V22 M9 10.76 A2 2 0 0 1 7.89 12.55 L6.11 13.45 A2 2 0 0 0 5 15.24 V16 " +
            "A1 1 0 0 0 6 17 H18 A1 1 0 0 0 19 16 V15.24 A2 2 0 0 0 17.89 13.45 " +
            "L16.11 12.55 A2 2 0 0 1 15 10.76 V7 A1 1 0 0 1 16 6 A2 2 0 0 0 16 2 H8 " +
            "A2 2 0 0 0 8 6 A1 1 0 0 1 9 7 Z",
        Morphicon.Save =>
            "M15.2 3 A2 2 0 0 1 16.6 3.6 L20.4 7.4 A2 2 0 0 1 21 8.8 V19 " +
            "A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z " +
            "M17 21 V14 A1 1 0 0 0 16 13 H8 A1 1 0 0 0 7 14 V21 " +
            "M7 3 V7 A1 1 0 0 0 8 8 H15",
        Morphicon.Trash2 =>
            "M10 11 V17 M14 11 V17 M19 6 V20 A2 2 0 0 1 17 22 H7 A2 2 0 0 1 5 20 V6 " +
            "M3 6 H21 M8 6 V4 A2 2 0 0 1 10 2 H14 A2 2 0 0 1 16 4 V6",
        Morphicon.Recycle =>
            "M7 19 H4.815 A1.83 1.83 0 0 1 3.245 18.119 A1.785 1.785 0 0 1 3.241 16.335 L7.196 9.5 " +
            "M11 19 H19.203 A1.83 1.83 0 0 0 20.759 18.11 A1.784 1.784 0 0 0 20.759 16.335 L19.533 14.215 " +
            "M14 16 L11 19 L14 22 M8.293 13.596 L7.196 9.5 L3.1 10.598 " +
            "M9.344 5.811 L10.437 3.919 A1.83 1.83 0 0 1 11.985 3 A1.784 1.784 0 0 1 13.531 3.888 " +
            "L17.474 10.731 M13.378 9.633 L17.474 10.731 L18.571 6.635",
        Morphicon.Eraser =>
            "M21 21 H8 A2 2 0 0 1 6.58 20.413 L2.586 16.414 A2 2 0 0 1 2.586 13.586 " +
            "L12.586 3.586 A2 2 0 0 1 15.415 3.586 L20.42 8.591 A2 2 0 0 1 20.42 11.419 " +
            "L12.079 21 M22 22 H8 M5.5 12.5 L12.5 19.5",
        Morphicon.Play =>
            "M5 5 A2 2 0 0 1 8.008 3.272 L20.005 10.27 A2 2 0 0 1 20.008 13.728 " +
            "L8.008 20.728 A2 2 0 0 1 5 19 Z",
        Morphicon.LayoutGrid =>
            "M4 3 H9 A1 1 0 0 1 10 4 V9 A1 1 0 0 1 9 10 H4 A1 1 0 0 1 3 9 V4 A1 1 0 0 1 4 3 Z " +
            "M15 3 H20 A1 1 0 0 1 21 4 V9 A1 1 0 0 1 20 10 H15 A1 1 0 0 1 14 9 V4 A1 1 0 0 1 15 3 Z " +
            "M15 14 H20 A1 1 0 0 1 21 15 V20 A1 1 0 0 1 20 21 H15 A1 1 0 0 1 14 20 V15 A1 1 0 0 1 15 14 Z " +
            "M4 14 H9 A1 1 0 0 1 10 15 V20 A1 1 0 0 1 9 21 H4 A1 1 0 0 1 3 20 V15 A1 1 0 0 1 4 14 Z",
        Morphicon.X =>
            "M18 6 L6 18 M6 6 L18 18",
        Morphicon.Clipboard =>
            "M10 2 H14 A1 1 0 0 1 15 3 V5 A1 1 0 0 1 14 6 H10 A1 1 0 0 1 9 5 V3 A1 1 0 0 1 10 2 Z " +
            "M16 4 H18 A2 2 0 0 1 20 6 V20 A2 2 0 0 1 18 22 H6 A2 2 0 0 1 4 20 V6 A2 2 0 0 1 6 4 H8",
        Morphicon.Upload =>
            "M12 3 V15 M17 8 L12 3 L7 8 M21 15 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V15",
        Morphicon.Scan =>
            "M3 7 V5 A2 2 0 0 1 5 3 H7 M17 3 H19 A2 2 0 0 1 21 5 V7 " +
            "M21 17 V19 A2 2 0 0 1 19 21 H17 M7 21 H5 A2 2 0 0 1 3 19 V17",
        Morphicon.ClipboardPaste =>
            "M11 14 H21 M16 4 H18 A2 2 0 0 1 20 6 V7.344 M17 18 L21 14 L17 10 " +
            "M8 4 H6 A2 2 0 0 0 4 6 V20 A2 2 0 0 0 6 22 H18 A2 2 0 0 0 19.793 20.887 " +
            "M10 2 H14 A1 1 0 0 1 15 3 V5 A1 1 0 0 1 14 6 H10 A1 1 0 0 1 9 5 V3 A1 1 0 0 1 10 2 Z",
        Morphicon.MoveUpRight =>
            "M13 5 H19 V11 M19 5 L5 19",
        Morphicon.Volume2 =>
            "M11 4.702 A0.705 0.705 0 0 0 9.797 4.204 L6.413 7.587 A1.4 1.4 0 0 1 5.416 8 H3 " +
            "A1 1 0 0 0 2 9 V15 A1 1 0 0 0 3 16 H5.416 A1.4 1.4 0 0 1 6.413 16.413 " +
            "L9.796 19.797 A0.705 0.705 0 0 0 11 19.298 Z " +
            "M16 9 A5 5 0 0 1 16 15 M19.364 18.364 A9 9 0 0 0 19.364 5.636",
        Morphicon.Rocket =>
            "M12 15 V20 C15.03 19.45 16 18 16 18 C17.08 16.38 16 13 16 13 " +
            "M4.5 16.5 C3 17.76 2.5 21.5 2.5 21.5 C2.5 21.5 6.24 21 7.5 19.5 " +
            "C8.21 18.66 8.2 17.37 7.41 16.59 A2.18 2.18 0 0 0 4.5 16.5 " +
            "M9 12 A22 22 0 0 1 11 8.05 A12.88 12.88 0 0 1 22 2 C22 4.72 21.22 9.5 16 13 " +
            "A22.4 22.4 0 0 1 12 15 Z M9 12 H4 C4 12 4.55 8.97 6 8 C7.62 6.92 11 8.05 11 8.05",
        Morphicon.Palette =>
            "M12 22 A1 1 0 0 1 12 2 A10 9 0 0 1 22 11 A5 5 0 0 1 17 16 H14.75 " +
            "A1.75 1.75 0 0 0 13.35 18.8 L13.65 19.2 A1.75 1.75 0 0 1 12.25 22 Z " +
            "M14 6.5 A0.5 0.5 0 1 1 13 6.5 A0.5 0.5 0 1 1 14 6.5 " +
            "M18 10.5 A0.5 0.5 0 1 1 17 10.5 A0.5 0.5 0 1 1 18 10.5 " +
            "M7 12.5 A0.5 0.5 0 1 1 6 12.5 A0.5 0.5 0 1 1 7 12.5 " +
            "M9 7.5 A0.5 0.5 0 1 1 8 7.5 A0.5 0.5 0 1 1 9 7.5",
        Morphicon.Layers =>
            "M12.83 2.18 A2 2 0 0 0 11.17 2.18 L2.6 6.08 A1 1 0 0 0 2.6 7.91 L11.18 11.82 " +
            "A2 2 0 0 0 12.84 11.82 L21.42 7.92 A1 1 0 0 0 21.42 6.09 Z " +
            "M2 12 A1 1 0 0 0 2.58 12.91 L11.18 16.82 A2 2 0 0 0 12.83 16.82 L21.41 12.92 A1 1 0 0 0 22 12 " +
            "M2 17 A1 1 0 0 0 2.58 17.91 L11.18 21.82 A2 2 0 0 0 12.83 21.82 L21.41 17.92 A1 1 0 0 0 22 17",
        Morphicon.RefreshCw =>
            "M3 12 A9 9 0 0 1 12 3 A9.75 9.75 0 0 1 18.74 5.74 L21 8 M21 3 V8 H16 " +
            "M21 12 A9 9 0 0 1 12 21 A9.75 9.75 0 0 1 5.26 18.26 L3 16 M8 16 H3 V21",
        Morphicon.Eye =>
            "M2.062 12.348 A1 1 0 0 1 2.062 11.652 A10.75 10.75 0 0 1 21.938 11.652 " +
            "A1 1 0 0 1 21.938 12.348 A10.75 10.75 0 0 1 2.062 12.348 " +
            "M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12",
        Morphicon.FileText =>
            "M6 22 A2 2 0 0 1 4 20 V4 A2 2 0 0 1 6 2 H14 A2.4 2.4 0 0 1 15.704 2.706 " +
            "L19.292 6.294 A2.4 2.4 0 0 1 20 8 V20 A2 2 0 0 1 18 22 Z " +
            "M14 2 V7 A1 1 0 0 0 15 8 H20 M10 9 H8 M16 13 H8 M16 17 H8",
        Morphicon.Globe =>
            "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 " +
            "M12 2 A14.5 14.5 0 0 0 12 22 A14.5 14.5 0 0 0 12 2 M2 12 H22",
        Morphicon.Search =>
            "M21 21 L16.66 16.66 M19 11 A8 8 0 1 1 3 11 A8 8 0 1 1 19 11",
        Morphicon.Ellipsis =>
            "M13 12 A1 1 0 1 1 11 12 A1 1 0 1 1 13 12 " +
            "M20 12 A1 1 0 1 1 18 12 A1 1 0 1 1 20 12 " +
            "M6 12 A1 1 0 1 1 4 12 A1 1 0 1 1 6 12",
        Morphicon.Paperclip =>
            "M16 6 L7.586 14.586 A2 2 0 0 0 10.415 17.415 L18.829 8.829 " +
            "A4 4 0 1 0 13.172 3.172 L4.793 11.723 A6 6 0 1 0 13.278 20.208 L21.657 11.657",
        Morphicon.Check =>
            "M20 6 L9 17 L4 12",
        Morphicon.Highlighter =>
            "M9 11 L3 17 V20 H12 L15 17 M22 12 L17.4 16.6 A2 2 0 0 1 14.6 16.6 " +
            "L9.4 11.4 A2 2 0 0 1 9.4 8.6 L14 4",
        Morphicon.Circle =>
            "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12",
        Morphicon.Square =>
            "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z",
        Morphicon.ArrowUpRight =>
            "M7 7 H17 V17 M17 7 L7 17",
        Morphicon.Type =>
            "M12 4 V20 M4 7 V5 A1 1 0 0 1 5 4 H19 A1 1 0 0 1 20 5 V7 M9 20 H15",
        Morphicon.Grid3x3 =>
            "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z " +
            "M3 9 H21 M3 15 H21 M9 3 V21 M15 3 V21",
        Morphicon.Plus =>
            "M5 12 H19 M12 5 V19",
        Morphicon.Minus =>
            "M5 12 H19",
        Morphicon.Undo2 =>
            "M9 14 L4 9 L9 4 " +
            "M4 9 H14.5 A5.5 5.5 0 0 1 20 14.5 A5.5 5.5 0 0 1 14.5 20 H11",
        Morphicon.Redo2 =>
            "M15 14 L20 9 L15 4 " +
            "M20 9 H9.5 A5.5 5.5 0 0 0 4 14.5 A5.5 5.5 0 0 0 9.5 20 H13",
        Morphicon.Link =>
            "M10 13 A5 5 0 0 0 17.54 13.54 L20.54 10.54 A5 5 0 0 0 13.47 3.47 L11.75 5.18 " +
            "M14 11 A5 5 0 0 0 6.46 10.46 L3.46 13.46 A5 5 0 0 0 10.53 20.53 L12.24 18.82",
        _ => throw new ArgumentOutOfRangeException(nameof(icon)),
    };

    private static Path CreateOutline(Morphicon icon, double size, double strokeThickness)
    {
        return new Path
        {
            Data = System.Windows.Media.Geometry.Parse(PathData(icon)),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = strokeThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
        };
    }
}
