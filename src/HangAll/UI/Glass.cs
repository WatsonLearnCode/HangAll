using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace HangAll.UI;

/// <summary>The crisp glass frame and the aluminium clothespin, drawn in code.</summary>
public static class Glass
{
    /// <summary>A translucent sheet with a thin specular edge, lit from above.</summary>
    public static Border Frame(double radius)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(0.75),
            SnapsToDevicePixels = false,
        };
        Paint(b);
        return b;
    }

    public static void Paint(Border b)
    {
        b.Background = Theme.Freeze(new SolidColorBrush(Theme.GlassFill));
        b.BorderBrush = Theme.Freeze(new LinearGradientBrush(Theme.GlassEdgeTop, Theme.GlassEdgeBottom, 90));
    }

    /// <summary>
    /// The glass edge over a solid floor. Cards on the line want to read as part of
    /// the desktop; a panel that holds rows of text does not - at 72% a busy desktop
    /// shows through the gaps between them and the footer.
    /// </summary>
    public static void PaintPanel(Border b)
    {
        b.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        b.BorderBrush = Theme.Freeze(new LinearGradientBrush(Theme.GlassEdgeTop, Theme.GlassEdgeBottom, 90));
    }

    /// <summary>
    /// Shared elevation for every floating surface on the line: the empty-line
    /// hint, the drop target and the menu tag all lift off the rope the same way.
    /// </summary>
    public static void Elevate(Border b, double opacity = 0.18, double blur = 20, double depth = 5)
    {
        b.Effect = new DropShadowEffect
        {
            BlurRadius = blur, ShadowDepth = depth, Direction = 270,
            Opacity = opacity, Color = Colors.Black,
        };
    }

    /// <summary>
    /// A floating glass surface in the line's own style: the same fill, edge and
    /// corner radius as a hanging card, so overlays and the tag read as family.
    /// </summary>
    public static Border Surface(double radius = Layout.FrameRadius, double elevation = 0.18)
    {
        var b = Frame(radius);
        Elevate(b, elevation);
        return b;
    }

    /// <summary>A compact round-ended glass chip, for the menu tag on the rope.</summary>
    public static Border Chip(double side = 26)
    {
        var b = new Border
        {
            Width = side, Height = side,
            CornerRadius = new CornerRadius(side / 2),
            BorderThickness = new Thickness(0.75),
            SnapsToDevicePixels = false,
        };
        Paint(b);
        Elevate(b, 0.16, 14, 2);
        return b;
    }

    /// <summary>The round menu mark for the tag: three dots, the usual "more" glyph.</summary>
    public static Path MenuGlyph(double size, Brush fill) =>
        Morphicons.CreatePath(Morphicon.Ellipsis, size, 1.5, fill);

    /// <summary>The faint dark outline just outside the specular edge.</summary>
    public static Border Outline(double radius)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(radius + 0.5),
            BorderThickness = new Thickness(0.5),
            BorderBrush = Theme.Freeze(new SolidColorBrush(Theme.GlassOutline)),
            Margin = new Thickness(-0.5),
            IsHitTestVisible = false,
        };
    }

    public static Brush MetalBrush()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(Theme.Gray(0.70), 0));
        g.GradientStops.Add(new GradientStop(Theme.Gray(0.93), 0.35));
        g.GradientStops.Add(new GradientStop(Theme.Gray(0.82), 0.65));
        g.GradientStops.Add(new GradientStop(Theme.Gray(0.62), 1));
        g.Freeze();
        return g;
    }

    /// <summary>A warmer metal for a clip that keeps its photo on the line.</summary>
    public static Brush BrassBrush()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(Theme.Rgba(0.72, 0.56, 0.22, 1), 0));
        g.GradientStops.Add(new GradientStop(Theme.Rgba(0.98, 0.88, 0.55, 1), 0.35));
        g.GradientStops.Add(new GradientStop(Theme.Rgba(0.88, 0.74, 0.38, 1), 0.65));
        g.GradientStops.Add(new GradientStop(Theme.Rgba(0.62, 0.46, 0.16, 1), 1));
        g.Freeze();
        return g;
    }

    /// <summary>The icon paths used on cards: a cross, a check mark and a bin.</summary>
    public static Path Cross(double size, Brush stroke)
    {
        return Morphicons.CreatePath(Morphicon.X, size, 1.8, stroke);
    }

    public static Path Check(double size, Brush stroke)
    {
        return Morphicons.CreatePath(Morphicon.Check, size, 2, stroke);
    }

    /// <summary>A compact trash can for the button on a hanging card.</summary>
    public static Path Trash(double size, Brush stroke)
    {
        return Morphicons.CreatePath(Morphicon.Trash2, size, 1.45, stroke);
    }

    /// <summary>The play badge a recording wears in the corner of its card.</summary>
    public static Border PlayBadge(double side)
    {
        var triangle = Morphicons.CreateFilledPath(Morphicon.Play, 7, Brushes.White);
        triangle.Margin = new Thickness(1.6, 0, 0, 0);
        return new Border
        {
            Width = side, Height = side, CornerRadius = new CornerRadius(side / 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Layout.FrameInset + 6, 0, 0, Layout.FrameInset + 6),
            Background = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x11, 0x13, 0x18))),
            BorderBrush = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF))),
            BorderThickness = new Thickness(0.75),
            IsHitTestVisible = false,
            Child = triangle,
        };
    }
}
