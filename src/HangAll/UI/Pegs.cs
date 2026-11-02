using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using HangAll.Core;

namespace HangAll.UI;

/// <summary>
/// The clothespins and the colour of the line. A wooden peg with its spring
/// by default, the way they hang on a real line; aluminium or coloured
/// plastic if you prefer. Everything is drawn in code.
/// </summary>
public static class Pegs
{
    /// <summary>Raised when the colour of the line or the pegs changes in Settings.</summary>
    public static event Action? LookChanged;
    public static void RaiseLookChanged() => LookChanged?.Invoke();

    public static readonly string[] Styles = { "wood", "metal", "mixed", "red", "blue", "green", "yellow" };
    public static readonly string[] RopeColors = { "bronze", "gray", "black", "white", "red", "blue", "green", "gold", "pink", "purple" };

    /// <summary>The colour of the line, as chosen in Settings.</summary>
    public static Color Rope()
    {
        var key = Settings.Current.RopeColor?.Trim().ToLowerInvariant() ?? "bronze";
        if (key.StartsWith('#'))
        {
            try { return (Color)ColorConverter.ConvertFromString(key)!; } catch { }
        }
        return RopeColorOf(key);
    }

    public static Color RopeColorOf(string key) => key switch
    {
        "gray" => Theme.Gray(0.55),
        "black" => Theme.Gray(0.12),
        "white" => Theme.Gray(0.96),
        "red" => Color.FromRgb(0xB8, 0x3A, 0x3A),
        "blue" => Color.FromRgb(0x3A, 0x6E, 0xB8),
        "green" => Color.FromRgb(0x3E, 0x8E, 0x4E),
        "gold" => Color.FromRgb(0xC9, 0xA2, 0x3E),
        "pink" => Color.FromRgb(0xD8, 0x6E, 0x9E),
        "purple" => Color.FromRgb(0x7E, 0x5A, 0xB8),
        _ => Color.FromRgb(0x7A, 0x4E, 0x2A), // bronze: a twisted brown cord
    };

    public static Color Lighten(Color c, double k) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    /// <summary>A peg. `seed` picks the colour for the mixed plastic style, so each card keeps its own.</summary>
    public static Grid Create(string? style = null, int seed = 0, bool pinned = false)
    {
        style ??= Settings.Current.PegStyle;
        return style switch
        {
            "metal" => Metal(pinned),
            "wood" => Wood(pinned),
            "mixed" => Plastic(PlasticColors[Math.Abs(seed) % PlasticColors.Length], pinned),
            _ => Plastic(PlasticColor(style), pinned),
        };
    }

    /// <summary>
    /// The clip a fixed line item hangs by - the menu tag, the quick note, the clipboard.
    /// They are not photos, but they hang on the same line, so they take the peg the user
    /// chose rather than one drawn for them, and shrink to the size the smaller chips need.
    /// </summary>
    public static Grid CreateLineClip(int seed)
    {
        var peg = Create(seed: seed);
        peg.LayoutTransform = new ScaleTransform(0.75, 0.75);
        peg.HorizontalAlignment = HorizontalAlignment.Center;
        peg.VerticalAlignment = VerticalAlignment.Top;
        peg.IsHitTestVisible = false;
        return peg;
    }

    private static readonly Color[] PlasticColors =
    {
        Color.FromRgb(0xE2, 0x4A, 0x4A), Color.FromRgb(0x3C, 0x8C, 0xE2), Color.FromRgb(0x4C, 0xB8, 0x5C),
        Color.FromRgb(0xF2, 0xC2, 0x30), Color.FromRgb(0xF0, 0x7E, 0x3A), Color.FromRgb(0x9A, 0x5C, 0xD8),
    };

    private static Color PlasticColor(string key) => key switch
    {
        "red" => PlasticColors[0], "blue" => PlasticColors[1], "green" => PlasticColors[2], "yellow" => PlasticColors[3],
        _ => PlasticColors[0],
    };

    // The peg is drawn in a 12 x 30 box whose top centre is where it grips the line.
    private const double W = 12, H = 30;

    /// <summary>The outline of a spring peg seen from the front: a rounded head, a waist at the spring, two legs with a gap between them.</summary>
    // Pegs are created once per card and again on every theme change, so the
    // four shapes are parsed once and shared frozen instead of per peg.
    private static readonly Geometry BodyGeometry = Frozen(
        "M3,0 H9 Q11.5,0 11.5,2.5 V9.5 Q11.5,11.5 10.5,12 L11.5,13 V30 H7.2 L6.6,21.5 H5.4 L4.8,30 H0.5 V13 L1.5,12 Q0.5,11.5 0.5,9.5 V2.5 Q0.5,0 3,0 Z");
    private static readonly Geometry GrainGeometry = Frozen(
        "M3.2,2 V10 M8.8,2 V10 M3.6,15 V27 M8.4,15 V27");
    private static readonly Geometry SplitGeometry = Frozen("M6,0.4 V11.5");
    private static readonly Geometry SpringGeometry = Frozen(
        "M2.2,12 C1.6,15 3.6,17.2 6,16.2 C8.4,17.2 10.4,15 9.8,12");
    private static readonly DropShadowEffect PegShadow = FrozenShadow();

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static DropShadowEffect FrozenShadow()
    {
        var effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1.5, Direction = 270, Opacity = 0.30, Color = Colors.Black };
        effect.Freeze();
        return effect;
    }

    private static Grid Box()
    {
        var g = new Grid { Width = W, Height = H, IsHitTestVisible = false, Margin = new Thickness(-(W - Layout.PinWidth) / 2, 0, 0, 0) };
        g.HorizontalAlignment = HorizontalAlignment.Left;
        g.VerticalAlignment = VerticalAlignment.Top;
        return g;
    }

    private static Path Shape(Geometry data, Brush fill, Brush? stroke = null, double thickness = 0.6) => new()
    {
        Data = data, Fill = fill, Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round,
    };

    private static Grid Wood(bool pinned)
    {
        var box = Box();
        var wood = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0xA6, 0x72), 0));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(0xE9, 0xD1, 0xA4), 0.3));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(0xDC, 0xBE, 0x8C), 0.52));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0xCC, 0x9C), 0.6));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(0xB8, 0x94, 0x60), 1));
        wood.Freeze();
        var body = Shape(BodyGeometry, wood, Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x5A, 0x3E, 0x1E))), 0.6);
        body.Effect = PegShadow;
        box.Children.Add(body);
        // The grain: two faint lines, and the split between the halves.
        box.Children.Add(Shape(GrainGeometry, Brushes.Transparent,
            Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x6A, 0x48, 0x20))), 0.5));
        box.Children.Add(Shape(SplitGeometry, Brushes.Transparent,
            Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x4A, 0x30, 0x14))), 0.7));
        box.Children.Add(Spring(pinned));
        return box;
    }

    private static Grid Plastic(Color color, bool pinned)
    {
        var box = Box();
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        fill.GradientStops.Add(new GradientStop(Darken(color, 0.18), 0));
        fill.GradientStops.Add(new GradientStop(Lighten(color, 0.25), 0.3));
        fill.GradientStops.Add(new GradientStop(color, 0.55));
        fill.GradientStops.Add(new GradientStop(Darken(color, 0.28), 1));
        fill.Freeze();
        var body = Shape(BodyGeometry, fill, Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0))), 0.5);
        body.Effect = PegShadow;
        box.Children.Add(body);
        box.Children.Add(Shape(SplitGeometry, Brushes.Transparent,
            Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0))), 0.7));
        box.Children.Add(Spring(pinned));
        return box;
    }

    /// <summary>The wire spring around the waist, where the line passes. Gold when the photo is kept on purpose.</summary>
    private static UIElement Spring(bool pinned)
    {
        var metal = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (pinned)
        {
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xD7, 0x80), 0));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0xB8, 0x8A, 0x2E), 1));
        }
        else
        {
            metal.GradientStops.Add(new GradientStop(Theme.Gray(0.95), 0));
            metal.GradientStops.Add(new GradientStop(Theme.Gray(0.55), 1));
        }
        metal.Freeze();
        var g = new Grid { IsHitTestVisible = false };
        // The coil: a short, bright cylinder across the waist, and the loop of wire that holds the halves.
        g.Children.Add(new Rectangle
        {
            Width = 11, Height = 3.6, RadiusX = 1.8, RadiusY = 1.8, Fill = metal,
            Stroke = Theme.Freeze(new SolidColorBrush(Theme.Gray(0, 0.35))), StrokeThickness = 0.4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8.2, 0, 0),
        });
        g.Children.Add(Shape(SpringGeometry, Brushes.Transparent,
            Theme.Freeze(new SolidColorBrush(pinned ? Color.FromRgb(0xB8, 0x8A, 0x2E) : Theme.Gray(0.5))), 0.9));
        return g;
    }

    private static Grid Metal(bool pinned)
    {
        var box = new Grid { Width = Layout.PinWidth, Height = Layout.PinHeight, IsHitTestVisible = false };
        var body = new Border
        {
            CornerRadius = new CornerRadius(3.5),
            Background = pinned ? Glass.BrassBrush() : Glass.MetalBrush(),
            BorderThickness = new Thickness(0.6),
            BorderBrush = Theme.Freeze(new LinearGradientBrush(Theme.Gray(1, 0.9), Theme.Gray(0, 0.18), 90)),
            Effect = PegShadow,
        };
        var slot = new Rectangle
        {
            Width = 5, Height = 1.4, RadiusX = 0.7, RadiusY = 0.7,
            Fill = Theme.Freeze(new SolidColorBrush(Theme.Gray(0, 0.32))),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8.5, 0, 0),
        };
        box.Children.Add(body);
        box.Children.Add(slot);
        return box;
    }

    private static Color Darken(Color c, double k) => Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));

}
