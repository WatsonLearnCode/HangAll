using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HangAll.Core;
using Microsoft.Win32;

namespace HangAll.UI;

/// <summary>Light or dark, following Windows. The glass and the menus adapt.</summary>
public static class Theme
{
    public static bool AppsDark { get; private set; }
    public static bool SystemDark { get; private set; }
    public static event Action? Changed;

    public static readonly DependencyProperty FocusRingProperty = DependencyProperty.RegisterAttached(
        "FocusRing",
        typeof(bool),
        typeof(Theme),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty CheckMotionProperty = DependencyProperty.RegisterAttached(
        "CheckMotion",
        typeof(bool),
        typeof(Theme),
        new FrameworkPropertyMetadata(false, OnCheckMotionChanged));

    public static bool GetFocusRing(DependencyObject element) =>
        (bool)element.GetValue(FocusRingProperty);

    public static void SetFocusRing(DependencyObject element, bool value) =>
        element.SetValue(FocusRingProperty, value);

    public static bool GetCheckMotion(DependencyObject element) =>
        (bool)element.GetValue(CheckMotionProperty);

    public static void SetCheckMotion(DependencyObject element, bool value) =>
        element.SetValue(CheckMotionProperty, value);

    private static void OnCheckMotionChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not CheckBox box) return;

        box.Loaded -= OnAnimatedCheckLoaded;
        box.Checked -= OnAnimatedCheckChanged;
        box.Unchecked -= OnAnimatedCheckChanged;
        if (args.NewValue is not true) return;

        box.Loaded += OnAnimatedCheckLoaded;
        box.Checked += OnAnimatedCheckChanged;
        box.Unchecked += OnAnimatedCheckChanged;
        if (box.IsLoaded) ApplyCheckState(box, animate: false);
    }

    private static void OnAnimatedCheckLoaded(object sender, RoutedEventArgs e) =>
        ApplyCheckState((CheckBox)sender, animate: false);

    private static void OnAnimatedCheckChanged(object sender, RoutedEventArgs e) =>
        ApplyCheckState((CheckBox)sender, animate: true);

    private static void ApplyCheckState(CheckBox box, bool animate)
    {
        box.ApplyTemplate();
        if (box.Template?.FindName("CheckOutline", box) is not Shape outline ||
            box.Template.FindName("CheckMark", box) is not Shape mark)
        {
            return;
        }

        bool isChecked = box.IsChecked == true;
        outline.BeginAnimation(UIElement.OpacityProperty, null);
        mark.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        outline.Opacity = isChecked ? 0 : 1;
        mark.StrokeDashOffset = isChecked ? 0 : 21;

        if (!animate || !SystemParameters.ClientAreaAnimation) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        outline.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(isChecked ? 1 : 0, isChecked ? 0 : 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        mark.BeginAnimation(
            Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(isChecked ? 21 : 0, isChecked ? 0 : 21, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    static Theme() { Read(); }

    private static bool? _override;

    public static void Override(bool dark) { _override = dark; AppsDark = SystemDark = dark; Apply(); }

    /// <summary>Follows the Appearance setting: auto, light or dark.</summary>
    public static void ApplySetting()
    {
        _override = Settings.Current.Appearance switch { "light" => false, "dark" => true, _ => null };
        Refresh();
    }

    public static void Read()
    {
        if (_override is { } o) { AppsDark = SystemDark = o; return; }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            AppsDark = key?.GetValue("AppsUseLightTheme") is int apps && apps == 0;
            SystemDark = key?.GetValue("SystemUsesLightTheme") is int sys && sys == 0;
        }
        catch { AppsDark = SystemDark = false; }
    }

    public static void Refresh()
    {
        bool a = AppsDark, s = SystemDark;
        Read();
        Apply();
        if (a != AppsDark || s != SystemDark) Changed?.Invoke();
    }

    public static Color Rgba(double r, double g, double b, double a) =>
        Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    public static Color Gray(double white, double alpha = 1) => Rgba(white, white, white, alpha);

    // The glass frame around each photo: the system's blurred material cannot be
    // cut into a card shape on Windows, so it is a crisp translucent sheet with a
    // specular edge lit from above.
    public static Color GlassFill => AppsDark ? Gray(0.16, 0.74) : Gray(0.97, 0.72);
    public static Color GlassEdgeTop => AppsDark ? Gray(1, 0.32) : Gray(1, 0.55);
    public static Color GlassEdgeBottom => AppsDark ? Gray(1, 0.08) : Gray(1, 0.12);
    public static Color GlassOutline => Gray(0, AppsDark ? 0.28 : 0.10);
    public static Color PhotoEdge => Gray(1, 0.18);
    public static Color Primary => AppsDark ? Gray(0.96) : Gray(0.10);
    public static Color Secondary => AppsDark ? Gray(0.75) : Gray(0.38);
    /// <summary>Lowest tier of text: hints under a heading, still legible on glass.
    /// 0.40 is the lightest gray that clears 4.5:1 on the hint's own fill.</summary>
    public static Color Caption => AppsDark ? Gray(0.66) : Gray(0.40);

    // One accent hue for the whole product. The solid form carries fills and text; the
    // washed form is the same hue lightened, because the solid one sinks into a dark
    // surface as soon as it is only a hairline, a halo or an icon stroke.
    // The light value sits a step under Brand.Cobalt on purpose: white 12.5px text on
    // Cobalt only reaches 4.4:1, and deepening it clears AA without touching the brand
    // colour used in the mark and the wordmark.
    private static readonly Color AccentSolidLight = Color.FromRgb(0x1A, 0x63, 0xEA);
    private static readonly Color AccentSolidDark = Color.FromRgb(0x3B, 0x82, 0xFF);
    private static readonly Color AccentWashDark = Color.FromRgb(0x69, 0x9B, 0xFF);
    public static Color AccentFor(bool dark) => dark ? AccentSolidDark : AccentSolidLight;
    public static Color AccentWashFor(bool dark) => dark ? AccentWashDark : AccentSolidLight;
    public static Color Accent => AccentFor(AppsDark);
    public static Color AccentWash => AccentWashFor(AppsDark);
    public static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Pushes the menu colours into the application resources.</summary>
    public static void Apply()
    {
        var res = Application.Current?.Resources;
        if (res is null) return;
        bool dark = AppsDark;
        res["MenuBg"] = Freeze(new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF9, 0xF9, 0xF9)));
        res["MenuBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.09) : Gray(0, 0.10)));
        res["MenuFg"] = Freeze(new SolidColorBrush(dark ? Gray(0.96) : Gray(0.11)));
        res["MenuFgSecondary"] = Freeze(new SolidColorBrush(dark ? Gray(0.68) : Gray(0.45)));
        res["MenuHover"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.07) : Gray(0, 0.05)));
        res["MenuIconBg"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.07) : Gray(0, 0.04)));
        res["MenuChecked"] = Freeze(new SolidColorBrush(
            Alpha(AccentFor(dark), dark ? (byte)0x26 : (byte)0x14)));
        res["MenuFocus"] = Freeze(new SolidColorBrush(AccentWashFor(dark)));
        res["MenuSeparator"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.09) : Gray(0, 0.08)));
        res["SettingsWorkspaceBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.10) : Gray(0, 0.08)));
        res["SettingsHitSurface"] = Freeze(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));
        res["SettingsTextPrimary"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0xF7, 0xF7, 0xF8)
            : Color.FromRgb(0x1C, 0x1C, 0x1C)));
        res["SettingsTextSecondary"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0xC3, 0xC7, 0xCF)
            : Color.FromRgb(0x5F, 0x63, 0x68)));
        res["SettingsTextTertiary"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0xA5, 0xAB, 0xB5)
            : Color.FromRgb(0x6A, 0x6F, 0x76)));
        res["SettingsBrandTileBg"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(0xE8, 0x35, 0x36, 0x3E)
            : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)));
        res["SettingsBrandTileBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.12) : Gray(0, 0.08)));
        res["SettingsAvatarBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.18) : Gray(0, 0.18)));
        res["SettingsSectionBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.08) : Gray(0, 0.07)));
        res["SettingsCardBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.11) : Gray(0, 0.13)));
        res["SettingsGlassBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.10) : Gray(0, 0.07)));
        res["SettingsWindowButtonHover"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.08) : Gray(0, 0.06)));
        res["SettingsWindowCloseHover"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(0xD9, 0xD8, 0x5B, 0x52)
            : Color.FromArgb(0xF2, 0xE8, 0x69, 0x5C)));
        Color navButton = dark
            ? Color.FromRgb(0x30, 0x31, 0x38)
            : Color.FromRgb(0xF3, 0xF4, 0xF6);
        res["SettingsNavText"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0xF7, 0xF8, 0xFA)
            : Color.FromRgb(0x11, 0x13, 0x15)));
        res["SettingsNavButtonBg"] = Freeze(new SolidColorBrush(Colors.Transparent));
        res["SettingsNavHover"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.055) : Gray(0, 0.035)));
        res["SettingsNavPressed"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.10) : Gray(0, 0.065)));
        byte categoryBlend = dark ? (byte)0x38 : (byte)0x24;
        res["SettingsNavLineBg"] = Freeze(new SolidColorBrush(
            BlendOverOpaque(navButton, AccentFor(dark), categoryBlend)));
        res["SettingsNavCardsBg"] = Freeze(new SolidColorBrush(
            BlendOverOpaque(navButton, Color.FromRgb(0x8B, 0x5C, 0xF6), categoryBlend)));
        res["SettingsNavCaptureBg"] = Freeze(new SolidColorBrush(
            BlendOverOpaque(navButton, Color.FromRgb(0x38, 0xB2, 0xA8), categoryBlend)));
        res["SettingsNavStartupBg"] = Freeze(new SolidColorBrush(
            BlendOverOpaque(navButton, Color.FromRgb(0xF5, 0x9E, 0x0B), categoryBlend)));
        res["SettingsNavLineIcon"] = Freeze(new SolidColorBrush(AccentWashFor(dark)));
        res["SettingsNavCardsIcon"] = Freeze(new SolidColorBrush(dark ? Color.FromRgb(0xB5, 0x9A, 0xFA) : Color.FromRgb(0x8B, 0x5C, 0xF6)));
        res["SettingsNavCaptureIcon"] = Freeze(new SolidColorBrush(dark ? Color.FromRgb(0x6B, 0xD8, 0xCB) : Color.FromRgb(0x2A, 0x9D, 0x8F)));
        res["SettingsNavStartupIcon"] = Freeze(new SolidColorBrush(dark ? Color.FromRgb(0xFB, 0xB5, 0x5A) : Color.FromRgb(0xD9, 0x77, 0x06)));
        // Fields and buttons sit a touch above the window so they read as controls
        // rather than painted-on text, in either theme.
        res["FieldBg"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.06) : Colors.White));
        res["FieldBorder"] = Freeze(new SolidColorBrush(dark ? Gray(1, 0.11) : Gray(0, 0.13)));
        res["AccentFill"] = Freeze(new SolidColorBrush(AccentFor(dark)));
        // The close-button red above is a fill behind white text; as small text itself it
        // only reaches about 3:1. This is the same alarm hue pushed dark enough to read.
        res["TextDanger"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0xF2, 0xA2, 0x9A)
            : Color.FromRgb(0xB3, 0x26, 0x1E)));
        res["AccentFg"] = Freeze(new SolidColorBrush(Colors.White));
        // Text that sits on the accent fill. White clears AA on the deepened light
        // accent but only 3.6:1 on the lighter dark one, which is why Fluent pairs
        // black there. AccentFg stays white because the side panel uses it as a knob.
        res["AccentText"] = Freeze(new SolidColorBrush(dark
            ? Color.FromRgb(0x0B, 0x0D, 0x12)
            : Colors.White));
        // Accent-coloured text drawn on a surface, the mirror case to AccentText. The
        // solid accent sinks into a dark panel at 3.9:1, so dark mode uses the washed
        // form there; light mode keeps the deepened solid.
        res["AccentInk"] = Freeze(new SolidColorBrush(AccentWashFor(dark)));
        res["AccentSoft"] = Freeze(new SolidColorBrush(
            Alpha(AccentWashFor(dark), dark ? (byte)0x24 : (byte)0x18)));
        res["AccentHalo"] = Freeze(new SolidColorBrush(
            Alpha(AccentWashFor(dark), dark ? (byte)0x66 : (byte)0x52)));
        ApplySettingsGlassLevels(Settings.Current.GlassTransparency);
    }

    private static Color BlendOverOpaque(Color background, Color foreground, byte alpha)
    {
        double amount = alpha / 255d;
        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(background.R + (foreground.R - background.R) * amount),
            (byte)Math.Round(background.G + (foreground.G - background.G) * amount),
            (byte)Math.Round(background.B + (foreground.B - background.B) * amount));
    }

    /// <summary>
    /// Couples the right workspace and cards to the Settings glass slider while
    /// keeping a readable opacity floor.
    /// </summary>
    internal static void ApplySettingsGlassLevels(double transparency)
    {
        var res = Application.Current?.Resources;
        if (res is null) return;

        double t = Settings.GlassTransparencyOption(transparency);
        bool dark = AppsDark;
        byte workspaceAlpha = (byte)Math.Round(255 - 102 * t, MidpointRounding.AwayFromZero);
        byte contentAlpha = (byte)Math.Round(255 - 115 * t, MidpointRounding.AwayFromZero);

        res["SettingsWorkspaceBg"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(workspaceAlpha, 0x20, 0x21, 0x26)
            : Color.FromArgb(workspaceAlpha, 0xFF, 0xFF, 0xFF)));
        res["SettingsSectionBg"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(contentAlpha, 0x29, 0x2A, 0x31)
            : Color.FromArgb(contentAlpha, 0xF7, 0xF8, 0xFA)));
        res["SettingsContactCardBg"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
        res["SettingsCardBg"] = Freeze(new SolidColorBrush(dark
            ? Color.FromArgb(contentAlpha, 0x2F, 0x30, 0x37)
            : Color.FromArgb(contentAlpha, 0xFF, 0xFF, 0xFF)));
        ApplyAlpha(res, "SettingsNavLineBg", contentAlpha);
        ApplyAlpha(res, "SettingsNavCardsBg", contentAlpha);
        ApplyAlpha(res, "SettingsNavCaptureBg", contentAlpha);
        ApplyAlpha(res, "SettingsNavStartupBg", contentAlpha);
    }

    private static void ApplyAlpha(ResourceDictionary res, string key, byte alpha)
    {
        if (res[key] is not SolidColorBrush brush) return;

        var color = brush.Color;
        res[key] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
    }

}
