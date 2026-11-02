using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace HangAll.UI;

/// <summary>
/// One place for the app's type. Language selects the font family and text
/// rendering mode; sizes and line heights stay identical across languages.
/// </summary>
public static class Typography
{
    private const string ChineseFamily = "Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Segoe UI";
    private const string TraditionalChineseFamily = "Microsoft JhengHei UI, Microsoft JhengHei, Noto Sans CJK TC, Segoe UI";
    private const string JapaneseFamily = "Yu Gothic UI, Meiryo UI, MS PGothic, Segoe UI";
    private const string KoreanFamily = "Malgun Gothic, Noto Sans KR, Segoe UI";
    private const string LatinFamily = "Segoe UI Variable Text, Segoe UI";
    private const string ArabicFamily = "Segoe UI, Tahoma";

    // FontFamily is immutable, so one instance per language is enough; creating a
    // new one on every access showed up in the render paths that read Ui/ForText.
    private static readonly FontFamily Latin = new(LatinFamily);
    private static readonly FontFamily Chinese = new(ChineseFamily);
    private static readonly FontFamily TraditionalChinese = new(TraditionalChineseFamily);
    private static readonly FontFamily Japanese = new(JapaneseFamily);
    private static readonly FontFamily Korean = new(KoreanFamily);
    private static readonly FontFamily Arabic = new(ArabicFamily);

    public static FontFamily Ui => Family(Strings.Language);
    public static FontFamily Display => Ui;
    // Keep paths on a Latin monospace face. Some Japanese UI faces draw the
    // reverse solidus as a yen sign even when the path itself uses en-US.
    public static FontFamily PathUi { get; } = new("Consolas");
    public static XmlLanguage PathLanguage => XmlLanguage.GetLanguage("en-US");
    public static double FontScale { get; private set; } = 1.0;

    // Type sizes deliberately do not follow the selected language. Only the
    // font family and rendering mode change, so switching to Chinese cannot
    // suddenly make the same window look larger or less balanced.
    // Four roles cover the whole UI: body copy for labels and prose, a slightly
    // denser control role for buttons/fields, small copy for status text, and
    // caption copy for footers and version lines.
    public static double BodySize => Scaled(13);
    public static double BodyLineHeight => Scaled(20);
    public static double ControlSize => Scaled(12.5);
    public static double ControlLineHeight => Scaled(19);
    public static double SmallSize => Scaled(11.5);
    public static double SmallLineHeight => Scaled(17);
    public static double CaptionSize => Scaled(10.5);
    public static double CaptionLineHeight => Scaled(16);
    public static double HeadingSize => Scaled(14);
    public static double HeadingLineHeight => Scaled(21);
    public static double WindowTitleSize => Scaled(18);
    public static double WindowTitleLineHeight => Scaled(26);

    public static FontFamily ForText(string? text)
    {
        if (ContainsKorean(text)) return Family("ko");
        if (ContainsCjk(text)) return Family(Strings.Language is "ja" or "ko" or "zh-Hant" ? Strings.Language : "zh");
        if (ContainsArabic(text)) return Family("ar");
        return Family(Strings.Language);
    }

    public static double NoteSize => Scaled(22);
    public static double NoteLineHeight => Scaled(32);

    public static void Apply()
    {
        FontScale = Core.Settings.FontScaleOption(Core.Settings.Current.FontScale);
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        resources["UiFontFamily"] = Ui;
        resources["UiDisplayFontFamily"] = Display;
        resources["UiBodyFontSize"] = BodySize;
        resources["UiBodyLineHeight"] = BodyLineHeight;
        resources["UiControlFontSize"] = ControlSize;
        resources["UiControlLineHeight"] = ControlLineHeight;
        resources["UiSmallFontSize"] = SmallSize;
        resources["UiSmallLineHeight"] = SmallLineHeight;
        resources["UiCaptionFontSize"] = CaptionSize;
        resources["UiCaptionLineHeight"] = CaptionLineHeight;
        resources["UiHeadingFontSize"] = HeadingSize;
        resources["UiHeadingLineHeight"] = HeadingLineHeight;
        resources["UiWindowTitleFontSize"] = WindowTitleSize;
        resources["UiWindowTitleLineHeight"] = WindowTitleLineHeight;
        resources["UiNoteFontSize"] = NoteSize;
        resources["UiNoteLineHeight"] = NoteLineHeight;
    }

    public static void Apply(Control element)
    {
        element.FontFamily = Ui;
        element.FontSize = ControlSize;
        ApplyText(element);
    }

    /// <summary>
    /// Paths use a Latin UI face and an English language tag. Without that,
    /// Japanese fonts can draw the backslash in a path as the yen sign.
    /// </summary>
    public static void ApplyPath(Control element)
    {
        element.FontFamily = PathUi;
        ApplyPathDirection(element);
    }

    public static void ApplyPath(TextBlock element)
    {
        element.FontFamily = PathUi;
        ApplyPathDirection(element);
    }

    public static void ApplyPath(Run element)
    {
        element.FontFamily = PathUi;
        element.Language = PathLanguage;
        ApplyText(element);
    }

    private static void ApplyPathDirection(FrameworkElement element)
    {
        element.Language = PathLanguage;
        element.FlowDirection = FlowDirection.LeftToRight;
        ApplyText(element);
    }

    /// <summary>
    /// Ideal keeps the proportions of dense CJK glyphs; Display can make small
    /// Chinese text look mechanically narrowed on some Windows DPI settings.
    /// ClearType still keeps the result crisp on LCD displays.
    /// </summary>
    public static void ApplyText(DependencyObject element)
    {
        TextOptions.SetTextFormattingMode(element, Strings.IsEastAsian ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(element, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(element, TextHintingMode.Fixed);
    }

    /// <summary>
    /// WPF clips the last line of a fixed LineHeight unless the block strategy
    /// is told to reserve the whole box. Without this, dense CJK glyphs look
    /// squashed into their line on some DPI scales.
    /// </summary>
    public static void BlockLineHeight(DependencyObject element, double lineHeight)
    {
        if (element is not FrameworkElement fe) return;
        TextBlock.SetLineStackingStrategy(fe, LineStackingStrategy.BlockLineHeight);
        TextBlock.SetLineHeight(fe, lineHeight);
    }

    private static FontFamily Family(string language) => language switch
    {
        "zh" => Chinese,
        "zh-Hant" => TraditionalChinese,
        "ja" => Japanese,
        "ko" => Korean,
        "ar" => Arabic,
        _ => Latin,
    };

    private static bool ContainsCjk(string? text) =>
        ContainsRange(text, '\u3400', '\u9FFF') || ContainsRange(text, '\uF900', '\uFAFF');

    private static bool ContainsKorean(string? text) =>
        ContainsRange(text, '\uAC00', '\uD7AF') || ContainsRange(text, '\u1100', '\u11FF');

    private static bool ContainsArabic(string? text) =>
        ContainsRange(text, '\u0600', '\u06FF') || ContainsRange(text, '\u0750', '\u077F');

    // A plain loop: LINQ over a string allocates an enumerator on every call.
    private static bool ContainsRange(string? text, char from, char to)
    {
        if (text is null) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= from && c <= to) return true;
        }
        return false;
    }

    /// <summary>Half-pixel steps keep ClearType crisp at every supported DPI.</summary>
    private static double Scaled(double value) =>
        Math.Round(value * FontScale * 2, MidpointRounding.AwayFromZero) / 2;
}
