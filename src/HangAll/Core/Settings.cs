using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HangAll.Interop;

namespace HangAll.Core;

/// <summary>Everything the app remembers, in one small JSON file.</summary>
public sealed class Settings
{
    public const string DefaultHotKey = AppIdentity.DefaultHotKey;
    public const string DefaultQuickLookKey = "Space";
    public const double DefaultClearAnimationOffset = 60;
    public const double MinClearAnimationOffset = 8;
    public const double MaxClearAnimationOffset = 160;
    public const double DefaultClipboardEdgeDistance = 52;
    public const double MinClipboardEdgeDistance = 8;
    public const double MaxClipboardEdgeDistance = 320;
    private const double DefaultGlassTransparency = 0.20;
    private const string LegacyDefaultHotKey = "Ctrl+Alt+T";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.DataFolderName);

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    // Declared before Current: static fields run in order, and Load needs the options.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Current { get; } = Load();

    /// <summary>Files hanging on the line, oldest first.</summary>
    public List<string> Pegged { get; set; } = new();
    public List<string> PinnedPaths { get; set; } = new();
    public bool SoundOn { get; set; } = true;
    public bool CatchClipboard { get; set; } = true;
    /// <summary>Save copied plain text, RTF and HTML to the independent text history.</summary>
    public bool CatchTextClipboard { get; set; }
    /// <summary>Distance from the clipboard card stack's outer edge to the right side of the screen.</summary>
    public double ClipboardEdgeDistance { get; set; } = DefaultClipboardEdgeDistance;
    /// <summary>Keep the line down while a capture has not been used yet.</summary>
    public bool StayDownWhileUnused { get; set; }
    /// <summary>A photo dragged into an app leaves the line, as if it had been dropped into a folder.</summary>
    public bool TakeDownAfterDrag { get; set; }
    /// <summary>Bring the line down when the pointer rests still at the top edge of the screen.</summary>
    public bool RevealAtTopEdge { get; set; } = true;
    /// <summary>How long the pointer rests at the edge before the line comes down, in seconds.</summary>
    public double RevealDelay { get; set; } = 0.5;
    /// <summary>How long the line waits after the pointer leaves before tucking away, in seconds.</summary>
    public double RetractDelay { get; set; } = 0.5;
    /// <summary>How far below the top of the work area the line hangs, in device independent pixels.</summary>
    public double LineOffset { get; set; }
    /// <summary>A preset name like "bronze", or a hex colour like "#8C5A2E".</summary>
    public string RopeColor { get; set; } = "bronze";
    /// <summary>wood, metal, mixed, red, blue, green or yellow.</summary>
    public string PegStyle { get; set; } = "wood";
    /// <summary>Spread the photos evenly along the line. Off, each stays where you put it.</summary>
    public bool AutoArrange { get; set; }
    /// <summary>manual, time or type.</summary>
    public string CardSort { get; set; } = "manual";
    /// <summary>Where each photo hangs, as a fraction of the width of the screen, by file path.</summary>
    public Dictionary<string, double> Spots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When each referenced file first hung on the line, by path.</summary>
    public Dictionary<string, DateTime> HangTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When each card was last copied, opened, edited, dropped or previewed, by path.</summary>
    public Dictionary<string, DateTime> LastUsedTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Automatically take cards off the line after they have been idle for long enough.</summary>
    public bool AutoDropEnabled { get; set; }
    /// <summary>hung or unused.</summary>
    public string AutoDropMode { get; set; } = "unused";
    /// <summary>Minutes before an eligible card is taken off the line.</summary>
    public int AutoDropMinutes { get; set; } = 30;
    /// <summary>Sticky notes hanging on the line, by the path of the picture each is drawn into.</summary>
    public Dictionary<string, NoteData> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>auto, light or dark.</summary>
    public string Appearance { get; set; } = "auto";
    /// <summary>auto, en, zh, zh-Hant, ja or ko.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>UI type scale: 0.9 (small), 1.0 (normal) or 1.1 (large).</summary>
    public double FontScale { get; set; } = 1.0;
    /// <summary>keyboard (hover then a key) or auto (hover opens Quick Look).</summary>
    public string QuickLookTrigger { get; set; } = "keyboard";
    /// <summary>The key pressed after hovering to open Quick Look.</summary>
    public string QuickLookKey { get; set; } = DefaultQuickLookKey;
    /// <summary>Quick Look image scale from 0.5 to 3.0 in 0.05 increments.</summary>
    public double QuickLookScale { get; set; } = 1.0;
    /// <summary>Opacity of the line, pegs and cards, from 0 (invisible) to 1 (opaque).</summary>
    public double LineOpacity { get; set; } = 1.0;
    /// <summary>Desktop visibility through the Settings window glass, from 0 (opaque) to 1 (clear).</summary>
    public double GlassTransparency { get; set; } = DefaultGlassTransparency;
    /// <summary>Master switch for hanging files from outside the line.</summary>
    public bool UniversalDrop { get; set; } = true;
    /// <summary>Files can be dropped into the line's dashed drop zone.</summary>
    public bool DropZoneHang { get; set; } = true;
    /// <summary>Files copied to the clipboard are hung by reference.</summary>
    public bool ClipboardAutoHang { get; set; }
    /// <summary>The dashed drop zone is active only while both its master switch and child switch are on.</summary>
    [JsonIgnore]
    public bool DropZoneEnabled => UniversalDrop && DropZoneHang;
    /// <summary>Clipboard files are hung only while both their master switch and child switch are on.</summary>
    [JsonIgnore]
    public bool ClipboardHangEnabled => UniversalDrop && ClipboardAutoHang;
    /// <summary>The animated button at the right end can sweep cards off the line.</summary>
    public bool OneClickClear { get; set; } = true;
    /// <summary>A user GIF copied into the settings folder, or null for the built-in animation.</summary>
    public string? ClearAnimationPath { get; set; }
    /// <summary>Size of the clear-run animation: 32, 48 or 64 DIP.</summary>
    public double ClearAnimationSize { get; set; } = 48;
    /// <summary>Which end of the line hosts the clear animation: left or right.</summary>
    public string ClearAnimationSide { get; set; } = "right";
    /// <summary>Distance from the button's outer edge to the chosen side, in DIP.</summary>
    public double ClearAnimationOffset { get; set; } = DefaultClearAnimationOffset;
    /// <summary>Mirror the clear animation artwork without reversing its sweep direction.</summary>
    public bool ClearAnimationMirrored { get; set; }
    public bool Welcomed { get; set; }
    /// <summary>Looks for a newer release in the background, at most once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>When the last check finished, in ISO 8601 round-trip form, or null.</summary>
    public string? LastUpdateCheck { get; set; }
    /// <summary>A release the user asked not to be reminded about.</summary>
    public string? SkipVersion { get; set; }
    public string HotKey { get; set; } = DefaultHotKey;
    /// <summary>Extra folders to watch, for ShareX, Greenshot and friends.</summary>
    public List<string> WatchFolders { get; set; } = new();
    /// <summary>Where clipboard captures are written. Defaults to HangAll's own folder.</summary>
    public string? InboxFolder { get; set; }
    /// <summary>Monitor DPI scale aware maximum; null means as many as fit.</summary>
    public int? MaxItems { get; set; }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
                // Paths are matched regardless of case; the deserializer forgets that.
                loaded.Spots = Rekey(loaded.Spots);
                loaded.HangTimes = Rekey(loaded.HangTimes);
                loaded.LastUsedTimes = Rekey(loaded.LastUsedTimes);
                loaded.Notes = Rekey(loaded.Notes);
                foreach (var note in loaded.Notes.Values) HangAll.Core.Notes.Normalize(note);
                loaded.Pegged = Paths(loaded.Pegged);
                loaded.PinnedPaths = Paths(loaded.PinnedPaths);
                loaded.WatchFolders = Paths(loaded.WatchFolders);
                loaded.FontScale = FontScaleOption(loaded.FontScale);
                loaded.QuickLookTrigger = QuickLookTriggerOption(loaded.QuickLookTrigger);
                loaded.QuickLookKey = QuickLookKeyOption(loaded.QuickLookKey);
                loaded.QuickLookScale = QuickLookScaleOption(loaded.QuickLookScale);
                loaded.RevealDelay = RevealDelayOption(loaded.RevealDelay);
                loaded.RetractDelay = RetractDelayOption(loaded.RetractDelay);
                loaded.LineOpacity = LineOpacityOption(loaded.LineOpacity);
                loaded.GlassTransparency = GlassTransparencyOption(loaded.GlassTransparency);
                loaded.ClearAnimationSize = ClearAnimationSizeOption(loaded.ClearAnimationSize);
                loaded.ClearAnimationSide = ClearAnimationSideOption(loaded.ClearAnimationSide);
                loaded.ClearAnimationOffset = ClearAnimationOffsetOption(loaded.ClearAnimationOffset);
                loaded.ClipboardEdgeDistance = ClipboardEdgeDistanceOption(loaded.ClipboardEdgeDistance);
                loaded.MaxItems = MaxItemsOption(loaded.MaxItems);
                loaded.CardSort = CardSortOption(loaded.CardSort);
                loaded.AutoDropMode = AutoDropModeOption(loaded.AutoDropMode);
                loaded.AutoDropMinutes = AutoDropMinutesOption(loaded.AutoDropMinutes);
                if (string.Equals(loaded.HotKey, LegacyDefaultHotKey, StringComparison.OrdinalIgnoreCase))
                    loaded.HotKey = DefaultHotKey;
                // Arabic was retired in favour of Japanese and Korean; an old
                // "ar" choice goes back to following Windows, so a Japanese or
                // Korean desktop lands on its own language instead of English.
                if (loaded.Language is not ("auto" or "en" or "zh" or "zh-Hant" or "ja" or "ko")) loaded.Language = "auto";
                return loaded;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not read settings: {e.Message}");
            // The defaults that follow get written over the file on the first
            // save, so keep the unreadable one beside it for recovery.
            try
            {
                if (File.Exists(FilePath))
                {
                    var backup = FilePath + ".bad";
                    File.Copy(FilePath, backup, true);
                    Log.Notice($"Kept the unreadable settings as {Path.GetFileName(backup)}");
                }
            }
            catch (Exception backupError)
            {
                Log.Error($"Could not back up the unreadable settings: {backupError.Message}");
            }
        }
        return new Settings();
    }

    /// <summary>
    /// Re-keys a hand-edited table so paths match regardless of case. A section
    /// the user emptied arrives as null, and two names that differ only in case
    /// are one path on NTFS; both throw out of the Dictionary constructor, which
    /// used to discard every other setting with them.
    /// </summary>
    private static Dictionary<string, T> Rekey<T>(Dictionary<string, T>? values)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (path, value) in values)
            if (path is not null && value is not null) result[path] = value;
        return result;
    }

    /// <summary>Drops the nulls a hand-edited list can carry, so no reader has to expect them.</summary>
    private static List<string> Paths(List<string>? values) =>
        values is null
            ? new List<string>()
            : values.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

    /// <summary>Keeps hand-edited settings on one of the three supported UI scales.</summary>
    public static double FontScaleOption(double value) =>
        new[] { 0.9, 1.0, 1.1 }.OrderBy(option => Math.Abs(option - value)).First();

    /// <summary>Keeps Quick Look on one of its two supported trigger modes.</summary>
    public static string QuickLookTriggerOption(string? value) =>
        value is "auto" ? "auto" : "keyboard";

    /// <summary>Falls back to Space when a hand-edited Quick Look key cannot be registered.</summary>
    public static string QuickLookKeyOption(string? value)
    {
        var key = value?.Trim();
        return !string.IsNullOrEmpty(key) && HangAll.Interop.HotKey.Parse(key, out _, out _)
            ? key
            : DefaultQuickLookKey;
    }

    /// <summary>Clamps Quick Look size and snaps hand-edited values to a five percent step.</summary>
    public static double QuickLookScaleOption(double value) =>
        Math.Round(
            Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.5, 3.0) / 0.05,
            MidpointRounding.AwayFromZero) * 0.05;

    /// <summary>Keeps the exposed top-edge sensitivity on one of its three supported values.</summary>
    public static double RevealDelayOption(double value) =>
        new[] { 1.0, 0.5, 0.25 }.OrderBy(option => Math.Abs(option - value)).First();

    /// <summary>Keeps the exposed auto-hide delay on one of its four supported values.</summary>
    public static double RetractDelayOption(double value) =>
        new[] { 0.2, 0.5, 1.0, 2.0 }.OrderBy(option => Math.Abs(option - value)).First();

    /// <summary>Clamps a hand-edited line opacity to the supported 0..1 range.</summary>
    public static double LineOpacityOption(double value) => Math.Clamp(value, 0.0, 1.0);

    /// <summary>Clamps the Settings-window glass transparency to 0..1 and 1% precision.</summary>
    public static double GlassTransparencyOption(double value) =>
        Math.Round(
            Math.Clamp(double.IsFinite(value) ? value : DefaultGlassTransparency, 0.0, 1.0),
            2,
            MidpointRounding.AwayFromZero);

    /// <summary>Keeps the clear animation on one of its three supported sizes.</summary>
    public static double ClearAnimationSizeOption(double value) =>
        new[] { 32.0, 48.0, 64.0 }.OrderBy(option => Math.Abs(option - value)).First();

    /// <summary>Keeps the clear animation on the left or right edge of the line.</summary>
    public static string ClearAnimationSideOption(string? value) =>
        string.Equals(value?.Trim(), "left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";

    /// <summary>Clamps the clear button's edge distance and snaps it to whole DIP.</summary>
    public static double ClearAnimationOffsetOption(double value) =>
        Math.Round(
            Math.Clamp(
                double.IsFinite(value) ? value : DefaultClearAnimationOffset,
                MinClearAnimationOffset,
                MaxClearAnimationOffset),
            MidpointRounding.AwayFromZero);

    /// <summary>Clamps the clipboard stack's edge distance and snaps it to whole DIP.</summary>
    public static double ClipboardEdgeDistanceOption(double value) =>
        Math.Round(
            Math.Clamp(
                double.IsFinite(value) ? value : DefaultClipboardEdgeDistance,
                MinClipboardEdgeDistance,
                MaxClipboardEdgeDistance),
            MidpointRounding.AwayFromZero);

    /// <summary>Null means automatic, -1 means unlimited; positive values snap to 6, 8, 10 or 12.</summary>
    public static int? MaxItemsOption(int? value) =>
        value switch
        {
            null => null,
            < 0 => -1,
            _ => new[] { 6, 8, 10, 12 }.OrderBy(option => Math.Abs(option - value.Value)).First(),
        };

    /// <summary>Keeps the card ordering on one of the three supported modes.</summary>
    public static string CardSortOption(string? value) =>
        value is "time" or "type" ? value : "manual";

    /// <summary>Keeps automatic take-down on one of the two supported clocks.</summary>
    public static string AutoDropModeOption(string? value) =>
        value is "hung" ? "hung" : "unused";

    /// <summary>Keeps automatic take-down on one of its four supported delays.</summary>
    public static int AutoDropMinutesOption(int value) =>
        new[] { 5, 15, 30, 60 }.OrderBy(option => Math.Abs(option - value)).First();

    /// <summary>The hot-band extension that pairs with each top-edge sensitivity.</summary>
    public static double RevealHotBandDips(double value) => RevealDelayOption(value) switch
    {
        1.0 => 2,
        0.5 => 8,
        _ => 16,
    };

    /// <summary>True while rendering art: the real settings file is left alone.</summary>
    public static bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Error($"Could not save settings: {e.Message}");
        }
    }
}

/// <summary>A tiny log in %LOCALAPPDATA%\HangAll\log.txt, for when something is off.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Settings.Folder, "log.txt");
    public static string Location => FilePath;

    public static void Notice(string message) => Write("notice", message);
    public static void Error(string message) => Write("error", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
    }
}

/// <summary>One line on a sticky note.</summary>
public sealed class NoteTask
{
    public string Text { get; set; } = "";
    public bool Completed { get; set; }
    public List<string> Attachments { get; set; } = new();
}

/// <summary>What a sticky note says and what colour its paper is.</summary>
public sealed class NoteData
{
    public int Version { get; set; } = Notes.CurrentVersion;
    public string Title { get; set; } = "";
    public List<NoteTask> Tasks { get; set; } = new();
    public string Color { get; set; } = "yellow";

    /// <summary>Version 1 notes had one free-form text block.</summary>
    [JsonPropertyName("text")]
    public string? LegacyText { get; set; }

    /// <summary>The full task text, retained for copying and searching.</summary>
    [JsonIgnore]
    public string Text => string.Join(Environment.NewLine, Tasks.Select(task => task.Text));
}
