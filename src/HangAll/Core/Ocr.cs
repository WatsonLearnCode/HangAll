using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HangAll.Core;

/// <summary>
/// Reads the text in a screenshot with the OCR built into Windows. No
/// network, no service: the same engine the Snipping Tool uses.
/// </summary>
public static class Ocr
{
    private static readonly Regex CjkCharacterSpacing = new(
        @"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsCJKCompatibilityIdeographs}\p{IsHiragana}\p{IsKatakana}\u3000-\u303F\uFF00-\uFFEF])[ \t]+(?=[\p{IsCJKUnifiedIdeographs}\p{IsCJKCompatibilityIdeographs}\p{IsHiragana}\p{IsKatakana}\u3000-\u303F\uFF00-\uFFEF])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Small text is enlarged before recognition; Windows OCR likes more pixels.</summary>
    private const double MaxUpscale = 3.0;

    /// <summary>The hard cap on the long edge of the bitmap handed to a recognizer.</summary>
    private const double MaxLongEdge = 6000.0;

    public static bool Available => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <returns>The recognised lines joined by new lines, empty if nothing was read, null if OCR is unavailable.</returns>
    public static async Task<string?> ReadAsync(string path)
    {
        var engines = AvailableEngines();
        if (engines.Count == 0) return null;

        var file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        using var prepared = await PrepareBitmapAsync(stream);

        var candidates = new List<Candidate>();
        foreach (var engine in engines)
        {
            string text;
            try
            {
                // Each recognizer gets its own copy; a bitmap cannot be shared between them.
                using var copy = new SoftwareBitmap(
                    prepared.BitmapPixelFormat, prepared.PixelWidth, prepared.PixelHeight, prepared.BitmapAlphaMode);
                prepared.CopyTo(copy);
                var result = await engine.RecognizeAsync(copy);
                text = Clean(result);
            }
            catch (Exception e)
            {
                Log.Error($"OCR recognizer {engine.RecognizerLanguage.LanguageTag} failed: {e.Message}");
                continue;
            }
            candidates.Add(new Candidate(engine.RecognizerLanguage.LanguageTag, text, Analyze(text)));
        }

        if (candidates.Count == 0) return string.Empty;

        // The script of the picture is decided by the union of all readings: a
        // recognizer that cannot represent a script never emits it, so any
        // substantial block of Han or Kana is real evidence about the source.
        var family = SceneFamily(candidates);
        var preferred = PreferredLanguage();
        Candidate? best = null;
        Score bestScore = default;
        foreach (var candidate in candidates)
        {
            var score = ScoreText(candidate, family, preferred);
            if (best is null || score.CompareTo(bestScore) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best!.Text;
    }

    /// <summary>Every installed Windows recognizer.</summary>
    private static List<OcrEngine> AvailableEngines()
    {
        var engines = new List<OcrEngine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            if (OcrEngine.TryCreateFromLanguage(language) is { } engine && seen.Add(language.LanguageTag))
                engines.Add(engine);
        }
        return engines;
    }

    /// <summary>
    /// Scales the picture once, keeping EXIF orientation: small text is
    /// enlarged up to 3x, and anything longer than 6000 px on a side is
    /// brought back under the cap.
    /// </summary>
    private static async Task<SoftwareBitmap> PrepareBitmapAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double width = decoder.PixelWidth;
        double height = decoder.PixelHeight;
        double scale = Math.Min(MaxUpscale, MaxLongEdge / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    /// <summary>Joins the recognised lines with the CJK character spacing cleaned up.</summary>
    private static string Clean(OcrResult result)
    {
        var lines = result.Lines
            .Select(l => CjkCharacterSpacing.Replace(l.Text.Trim(), string.Empty))
            .Where(t => t.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>How the running app's language maps onto a recognizer tag.</summary>
    private static string PreferredLanguage()
    {
        var chosen = Settings.Current.Language;
        var culture = chosen is "en" or "zh" or "zh-Hant" or "ja" or "ko"
            ? chosen
            : CultureInfo.CurrentUICulture.Name;
        var name = culture.ToLowerInvariant();
        if (name.StartsWith("zh", StringComparison.Ordinal))
        {
            // zh-Hant covers generic Traditional Chinese, including TW, HK and MO variants.
            return name.Contains("hant") || name.Contains("-tw") || name.Contains("-hk") || name.Contains("-mo")
                ? "zh-Hant"
                : "zh-Hans";
        }
        if (name.StartsWith("ja", StringComparison.Ordinal)) return "ja";
        if (name.StartsWith("ko", StringComparison.Ordinal)) return "ko";
        return "en";
    }

    /// <summary>Which script the picture mostly uses, judged from every reading together.</summary>
    private static string SceneFamily(IEnumerable<Candidate> candidates)
    {
        bool kana = false, hangul = false, han = false;
        foreach (var candidate in candidates)
        {
            var stats = candidate.Stats;
            kana |= stats.Dominant(static s => s.Kana);
            hangul |= stats.Dominant(static s => s.Hangul);
            han |= stats.Dominant(static s => s.Han);
        }
        if (kana) return "ja";
        if (hangul) return "ko";
        if (han) return "zh";
        return "en";
    }

    /// <summary>
    /// Counts what a reading is made of. Letters of every alphabet share the
    /// Latin bucket: for choosing between recognizers only "CJK or not" matters.
    /// </summary>
    private static Stats Analyze(string text)
    {
        int han = 0, kana = 0, hangul = 0, latin = 0, symbols = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            if (char.IsSurrogatePair(text, i)) i++;

            if (IsHan(cp)) { han++; continue; }
            if (IsKana(cp)) { kana++; continue; }
            if (IsHangul(cp)) { hangul++; continue; }
            if (IsLatin(cp)) { latin++; continue; }

            switch (CharUnicodeInfo.GetUnicodeCategory(cp))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.OtherNumber:
                case UnicodeCategory.LetterNumber:
                    latin++;
                    break;
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                    break;
                default:
                    symbols++;
                    break;
            }
        }
        return new Stats(han, kana, hangul, latin, symbols);
    }

    /// <summary>Scores a reading: matching script first, then the user's language, then how much was read.</summary>
    private static Score ScoreText(Candidate candidate, string family, string preferred)
    {
        var stats = candidate.Stats;
        return new Score(
            FamilyMatches(candidate.LanguageTag, family) ? 1 : 0,
            MatchesLanguage(candidate.LanguageTag, preferred) ? 1 : 0,
            Math.Min(stats.Letters, 1_000_000),
            -stats.Symbols);
    }

    /// <summary>Does this recognizer read the script the picture is written in?</summary>
    private static bool FamilyMatches(string languageTag, string family)
    {
        var tag = languageTag.ToLowerInvariant();
        return family switch
        {
            "ja" => tag.StartsWith("ja", StringComparison.Ordinal) || tag.StartsWith("zh", StringComparison.Ordinal),
            "zh" => tag.StartsWith("zh", StringComparison.Ordinal) || tag.StartsWith("ja", StringComparison.Ordinal),
            "ko" => tag.StartsWith("ko", StringComparison.Ordinal),
            _ => tag.StartsWith("en", StringComparison.Ordinal),
        };
    }

    private static bool MatchesLanguage(string languageTag, string preferred)
    {
        var tag = languageTag.ToLowerInvariant();
        return preferred switch
        {
            "zh-Hant" => tag.StartsWith("zh", StringComparison.Ordinal) && (tag.Contains("hant") || tag.Contains("-tw") || tag.Contains("-hk") || tag.Contains("-mo")),
            "zh-Hans" or "zh" => tag.StartsWith("zh", StringComparison.Ordinal) && !tag.Contains("hant"),
            "ja" => tag.StartsWith("ja", StringComparison.Ordinal),
            "ko" => tag.StartsWith("ko", StringComparison.Ordinal),
            _ => tag.StartsWith("en", StringComparison.Ordinal),
        };
    }

    private static bool IsHan(int cp) =>
        cp is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF;

    private static bool IsKana(int cp) =>
        cp is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF;

    private static bool IsHangul(int cp) =>
        cp is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;

    private static bool IsLatin(int cp) =>
        cp is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 0x00C0 and <= 0x024F;

    /// <summary>One recognizer's reading of the picture.</summary>
    private sealed record Candidate(string LanguageTag, string Text, Stats Stats);

    /// <summary>What a reading is made of.</summary>
    private readonly record struct Stats(int Han, int Kana, int Hangul, int Latin, int Symbols)
    {
        public int Letters => Han + Kana + Hangul + Latin;

        /// <summary>A tenth of the letters, and at least three, is enough to call the picture that script.</summary>
        public bool Dominant(Func<Stats, int> count)
        {
            int value = count(this);
            return value >= 3 && value * 10 >= Letters;
        }
    }

    /// <summary>A better script match wins; the rest break ties in order.</summary>
    private readonly record struct Score(int Family, int Language, int Letters, int Symbols) : IComparable<Score>
    {
        public int CompareTo(Score other)
        {
            int result = Family.CompareTo(other.Family);
            if (result != 0) return result;
            result = Language.CompareTo(other.Language);
            if (result != 0) return result;
            result = Letters.CompareTo(other.Letters);
            if (result != 0) return result;
            return Symbols.CompareTo(other.Symbols);
        }
    }
}
