using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HangAll.Core;

/// <summary>
/// Sticky notes. A note is a small picture of coloured paper with your words
/// on it, written into the caught-captures folder, so it hangs, swings,
/// copies and drags out exactly like a screenshot. What it says and what
/// colour it is live in the settings, so it can be edited again.
/// </summary>
public static class Notes
{
    public static readonly string[] Colors = { "yellow", "pink", "blue", "green", "orange" };
    public const int CurrentVersion = 6;

    /// <summary>Raised after a task is toggled outside its editor.</summary>
    public static event Action<string>? TasksChanged;

    public static Color Paper(string key) => key switch
    {
        "pink" => Color.FromRgb(0xFB, 0xC4, 0xD6),
        "blue" => Color.FromRgb(0xBF, 0xDC, 0xFB),
        "green" => Color.FromRgb(0xC9, 0xEA, 0xC6),
        "orange" => Color.FromRgb(0xFF, 0xD6, 0xA3),
        _ => Color.FromRgb(0xFF, 0xF1, 0x8E),
    };

    public static bool IsNote(string path) => Settings.Current.Notes.ContainsKey(path);

    public static NoteData? Get(string path) => Settings.Current.Notes.TryGetValue(path, out var d) ? d : null;

    /// <summary>A fresh, empty note of the given colour. Returns the path of its picture.</summary>
    public static string Create(string color)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(Inbox.Folder, $"Note {stamp}.png");
        int n = 2;
        while (File.Exists(path)) path = Path.Combine(Inbox.Folder, $"Note {stamp} ({n++}).png");
        var data = new NoteData { Color = color };
        Settings.Current.Notes[path] = data;
        Render(data, path);
        Settings.Current.Save();
        return path;
    }

    /// <summary>New tasks or a new colour: the picture is drawn again.</summary>
    public static void Update(string path, IReadOnlyList<NoteTask> tasks, string color)
        => Update(path, Get(path)?.Title ?? "", tasks, color);

    /// <summary>New title, tasks or colour: the picture is drawn again.</summary>
    public static void Update(string path, string title, IReadOnlyList<NoteTask> tasks, string color)
    {
        var data = Get(path) ?? new NoteData();
        var snapshot = tasks
            .Select(task => new NoteTask
            {
                Text = task.Text,
                Completed = task.Completed,
                Attachments = task.Attachments?.ToList() ?? new List<string>(),
            })
            .ToList();
        if (snapshot.Count == 0) snapshot.Add(new NoteTask());

        // The editor keeps NoteTask references in its row controls. Updating
        // those objects in place lets an autosave update persistence without
        // leaving the visible list bound to stale copies.
        for (int i = 0; i < snapshot.Count; i++)
        {
            if (i >= data.Tasks.Count)
            {
                data.Tasks.Add(new NoteTask());
            }

            data.Tasks[i].Text = snapshot[i].Text;
            data.Tasks[i].Completed = snapshot[i].Completed;
            data.Tasks[i].Attachments = snapshot[i].Attachments.ToList();
        }
        if (data.Tasks.Count > snapshot.Count)
            data.Tasks.RemoveRange(snapshot.Count, data.Tasks.Count - snapshot.Count);
        Normalize(data);
        data.Title = NormalizeTitle(title);
        data.Color = color;
        data.LegacyText = null;
        data.Version = CurrentVersion;
        Settings.Current.Notes[path] = data;
        Render(data, path);
        Settings.Current.Save();
    }

    public static bool SetTaskCompleted(string path, int index, bool completed)
    {
        var data = Get(path);
        if (data is null || !SetTaskCompleted(data, index, completed)) return false;
        Render(data, path);
        Settings.Current.Save();
        TasksChanged?.Invoke(path);
        return true;
    }

    internal static bool SetTaskCompleted(NoteData note, int index, bool completed)
    {
        Normalize(note);
        if (index < 0 || index >= note.Tasks.Count) return false;
        note.Tasks[index].Completed = completed;
        return true;
    }

    internal static bool DeleteTask(NoteData note, int index)
    {
        Normalize(note);
        if (index < 0 || index >= note.Tasks.Count) return false;
        if (note.Tasks.Count == 1)
        {
            note.Tasks[0].Text = "";
            note.Tasks[0].Completed = false;
            note.Tasks[0].Attachments.Clear();
        }
        else
        {
            note.Tasks.RemoveAt(index);
        }
        return true;
    }

    internal static void Normalize(NoteData note)
    {
        note.Title = NormalizeTitle(note.Title);
        note.Tasks ??= new List<NoteTask>();
        for (int i = note.Tasks.Count - 1; i >= 0; i--)
            if (note.Tasks[i] is null) note.Tasks.RemoveAt(i);
        foreach (var task in note.Tasks)
        {
            task.Attachments ??= new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            task.Attachments = task.Attachments
                .Select(path => path?.Trim() ?? "")
                .Where(path => path.Length > 0 && seen.Add(path))
                .ToList();
        }
        if (note.Tasks.Count == 0) note.Tasks.Add(new NoteTask());
    }

    internal static string NormalizeTitle(string? title) =>
        (title ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static List<NoteTask> TasksFromLegacyText(string? text)
    {
        var tasks = (text ?? "")
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => new NoteTask { Text = line })
            .ToList();
        if (tasks.Count == 0) tasks.Add(new NoteTask());
        return tasks;
    }

    internal static bool TryMigrate(NoteData note, string path, Func<NoteData, bool> render)
    {
        bool migrateLegacyText = note.LegacyText is not null;
        if (!migrateLegacyText && note.Version >= CurrentVersion) return false;

        // Render from an isolated copy. The live note is committed only after
        // the new paper has been written successfully, so a failed rerender
        // keeps the old version and all user content available for a retry.
        var candidate = new NoteData
        {
            Version = CurrentVersion,
            Color = note.Color,
            Title = NormalizeTitle(note.Title),
            Tasks = migrateLegacyText
                ? TasksFromLegacyText(note.LegacyText)
                : CloneTasks(note.Tasks),
        };
        Normalize(candidate);
        if (!render(candidate)) return false;

        note.Version = CurrentVersion;
        note.Title = candidate.Title;
        note.Color = candidate.Color;
        note.Tasks = candidate.Tasks;
        note.LegacyText = null;
        return true;
    }

    private static List<NoteTask> CloneTasks(IEnumerable<NoteTask>? tasks) =>
        (tasks ?? Enumerable.Empty<NoteTask>())
        .Where(task => task is not null)
        .Select(task => new NoteTask
        {
            Text = task.Text,
            Completed = task.Completed,
            Attachments = task.Attachments?.ToList() ?? new List<string>(),
        })
        .ToList();

    internal static int MigrateLegacyNotes()
    {
        int migrated = 0;
        foreach (var (path, note) in Settings.Current.Notes)
        {
            try
            {
                if (TryMigrate(note, path, candidate =>
                    {
                        Render(candidate, path);
                        return true;
                    }))
                    migrated++;
            }
            catch (Exception e)
            {
                Log.Error($"Could not migrate note '{path}': {e.Message}");
            }
        }
        if (migrated > 0) Settings.Current.Save();
        return migrated;
    }

    public static void Forget(string path)
    {
        if (Settings.Current.Notes.Remove(path)) Settings.Current.Save();
    }

    internal const double W = 360, H = 270;
    private const double TitleTop = 11;
    private const double TitleLeft = 30;
    private const double TitleRight = 30;
    private const double TitleHeight = 28;
    private const double TitleTaskTopOffset = 28;
    private const double TaskTop = 26;
    private const double TaskLeft = 30;
    private const double TaskRight = 30;
    private const double TaskLineHeight = 42;
    private const double TaskCheckbox = 22;
    private const double TaskTextGap = 12;
    private const double TaskFontSize = 26;

    /// <summary>The single complete paper silhouette shared by every note surface.</summary>
    internal static Geometry SheetGeometry(double width, double height)
    {
        var sheet = new RectangleGeometry(
            new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)),
            3,
            3);
        sheet.Freeze();
        return sheet;
    }

    /// <summary>
    /// A deterministic paper wash shared by the editor and the mounted PNG.
    /// The grain is deliberately faint so text stays the strongest element.
    /// </summary>
    internal static Brush PaperBrush(string color, double width, double height, string seed)
    {
        var paper = Paper(color);
        var light = Lighten(paper, 0.09);
        var dark = Darken(paper, 0.035);
        var bounds = new Rect(0, 0, Math.Max(1, width), Math.Max(1, height));
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) };

        var wash = new LinearGradientBrush(light, dark, new Point(0.12, 0), new Point(0.88, 1));
        group.Children.Add(new GeometryDrawing(wash, null, new RectangleGeometry(bounds)));

        var topLight = new LinearGradientBrush(
            Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
            new Point(0.5, 0),
            new Point(0.5, 1));
        group.Children.Add(new GeometryDrawing(
            topLight,
            null,
            new RectangleGeometry(new Rect(0, 0, bounds.Width, Math.Max(1, bounds.Height * 0.42)))));

        var random = new Random(StableSeed(seed + "|" + color));
        var darkFibers = new GeometryGroup();
        var lightFibers = new GeometryGroup();
        for (int i = 0; i < 24; i++)
        {
            double y = random.NextDouble() * height;
            double x = random.NextDouble() * width * 0.72;
            double length = 22 + random.NextDouble() * Math.Max(24, width * 0.2);
            double rise = (random.NextDouble() - 0.5) * 2.2;
            var fiber = new LineGeometry(new Point(x, y), new Point(Math.Min(width, x + length), y + rise));
            (i % 3 == 0 ? lightFibers : darkFibers).Children.Add(fiber);
        }

        var darkPen = new Pen(new SolidColorBrush(Color.FromArgb(0x12, Darken(paper, 0.52).R,
            Darken(paper, 0.52).G, Darken(paper, 0.52).B)), 0.55);
        var lightPen = new Pen(new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), 0.65);
        group.Children.Add(new GeometryDrawing(null, darkPen, darkFibers));
        group.Children.Add(new GeometryDrawing(null, lightPen, lightFibers));

        var flecks = new GeometryGroup();
        for (int i = 0; i < 72; i++)
        {
            double x = random.NextDouble() * width;
            double y = random.NextDouble() * height;
            double radius = 0.25 + random.NextDouble() * 0.65;
            flecks.Children.Add(new EllipseGeometry(new Point(x, y), radius, radius));
        }
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x12, Darken(paper, 0.58).R,
                Darken(paper, 0.58).G, Darken(paper, 0.58).B)),
            null,
            flecks));

        group.Freeze();
        var brush = new DrawingBrush(group)
        {
            Viewbox = bounds,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = bounds,
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    internal static List<string> FilterExistingAttachments(
        IEnumerable<string>? paths,
        IEnumerable<string>? existing = null)
    {
        var seen = new HashSet<string>(
            (existing ?? Array.Empty<string>())
                .Select(path => path?.Trim() ?? "")
                .Where(path => path.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var accepted = new List<string>();
        foreach (string raw in paths ?? Array.Empty<string>())
        {
            string candidate = raw?.Trim() ?? "";
            if (candidate.Length == 0 || !File.Exists(candidate)) continue;
            try
            {
                string full = Path.GetFullPath(candidate);
                if (seen.Add(full)) accepted.Add(full);
            }
            catch
            {
                // Invalid paths are ignored rather than interrupting an edit.
            }
        }
        return accepted;
    }

    internal static IReadOnlyList<VisibleTaskRow> VisibleTaskRows(NoteData note)
    {
        Normalize(note);
        if (note.Tasks.Count <= 5)
            return Enumerable.Range(0, note.Tasks.Count).Select(index => new VisibleTaskRow(index, 0)).ToList();
        var rows = Enumerable.Range(0, 4).Select(index => new VisibleTaskRow(index, 0)).ToList();
        rows.Add(new VisibleTaskRow(null, note.Tasks.Count - 4));
        return rows;
    }

    internal readonly record struct VisibleTaskRow(int? TaskIndex, int MoreCount);

    internal readonly record struct TaskHitArea(int TaskIndex, Rect Bounds);

    /// <summary>Rows that can be toggled directly on the mounted note, in PNG coordinates.</summary>
    internal static IReadOnlyList<TaskHitArea> TaskHitAreas(NoteData note)
    {
        Normalize(note);
        var areas = new List<TaskHitArea>();
        var rows = VisibleTaskRows(note);
        double taskTop = TaskTopFor(note);
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row].TaskIndex is not int index) continue;
            double top = taskTop + row * TaskLineHeight;
            areas.Add(new TaskHitArea(index, new Rect(0, top, W, TaskLineHeight)));
        }
        return areas;
    }

    private static bool HasTitle(NoteData note) => NormalizeTitle(note.Title).Length > 0;
    private static double TaskTopFor(NoteData note) => TaskTop + (HasTitle(note) ? TitleTaskTopOffset : 0);

    /// <summary>Coloured paper and its task list, drawn at twice the size for crisp text.</summary>
    public static void Render(NoteData note, string path)
    {
        Normalize(note);
        var paper = Paper(note.Color);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sheet = SheetGeometry(W, H);
            var fill = PaperBrush(note.Color, W, H, path + "|" + note.Title);
            dc.DrawGeometry(fill, null, sheet);

            // The mounted image keeps one restrained edge so the paper still
            // separates from bright desktops, but it has no fold or inner seam.
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x66, 0x50, 0x22)), 1.25);
            outline.Freeze();
            dc.DrawGeometry(null, outline, sheet);

            string title = NormalizeTitle(note.Title);
            if (title.Length > 0)
            {
                DrawTitle(dc, title);
                var divider = new Pen(new SolidColorBrush(Color.FromArgb(0x2C, 0x3A, 0x30, 0x1E)), 1);
                divider.Freeze();
                double dividerY = TitleTop + TitleHeight + 3;
                dc.DrawLine(divider, new Point(TitleLeft, dividerY), new Point(W - TitleRight, dividerY));
            }

            var rows = VisibleTaskRows(note);
            double taskTop = TaskTopFor(note);
            for (int row = 0; row < rows.Count; row++)
            {
                double top = taskTop + row * TaskLineHeight;
                if (rows[row].TaskIndex is int index)
                {
                    var task = note.Tasks[index];
                    DrawTask(dc, task.Text, task.Completed, top, paper);
                }
                else
                {
                    DrawSummary(dc, rows[row].MoreCount, top, paper);
                }
            }
        }
        var rtb = new RenderTargetBitmap((int)(W * 2), (int)(H * 2), 192, 192, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static void DrawTitle(DrawingContext dc, string title)
    {
        bool rtl = IsArabic(title);
        var typeface = new Typeface(
            HangAll.UI.Typography.ForText(title),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x1E));
        var text = new FormattedText(
            title,
            CultureInfo.CurrentUICulture,
            rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            typeface,
            24,
            foreground,
            1.0)
        {
            MaxTextWidth = W - TitleLeft - TitleRight,
            MaxTextHeight = TitleHeight,
            LineHeight = TitleHeight,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left,
        };
        dc.DrawText(text, new Point(TitleLeft, TitleTop));
    }

    private static void DrawTask(DrawingContext dc, string text, bool completed, double top, Color paper)
    {
        bool rtl = IsArabic(text);
        double boxX = rtl ? W - TaskRight - TaskCheckbox : TaskLeft;
        var box = new Rect(boxX, top + (TaskLineHeight - TaskCheckbox) / 2, TaskCheckbox, TaskCheckbox);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x9A, 0x3A, 0x30, 0x1E)), 1.8);
        var boxFill = new SolidColorBrush(completed
            ? Color.FromRgb(0x57, 0x7D, 0x62)
            : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        dc.DrawRoundedRectangle(boxFill, outline, box, 5.5, 5.5);
        if (completed)
        {
            var mark = new StreamGeometry();
            using (var ctx = mark.Open())
            {
                ctx.BeginFigure(new Point(box.Left + 5, box.Top + 11), false, false);
                ctx.LineTo(new Point(box.Left + 9.5, box.Top + 15.5), true, false);
                ctx.LineTo(new Point(box.Left + 17, box.Top + 7), true, false);
            }
            mark.Freeze();
            dc.DrawGeometry(null,
                new Pen(new SolidColorBrush(System.Windows.Media.Colors.White), 2.4)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                }, mark);
        }

        double textLeft = rtl ? TaskLeft : TaskLeft + TaskCheckbox + TaskTextGap;
        double textWidth = W - TaskLeft - TaskRight - TaskCheckbox - TaskTextGap;
        var brush = new SolidColorBrush(completed
            ? Color.FromArgb(0xB8, 0x4B, 0x40, 0x2C)
            : Color.FromRgb(0x3A, 0x30, 0x1E));
        var ft = TaskText(text, brush, textWidth, rtl, completed);
        dc.DrawText(ft, new Point(textLeft, top + Math.Max(0, (TaskLineHeight - ft.Height) / 2)));
    }

    private static void DrawSummary(DrawingContext dc, int count, double top, Color paper)
    {
        string text = HangAll.UI.Strings.MoreTasks(count);
        bool rtl = IsArabic(text);
        var brush = new SolidColorBrush(Darken(paper, 0.52));
        double textLeft = rtl ? TaskLeft : TaskLeft + TaskCheckbox + TaskTextGap;
        double textWidth = W - TaskLeft - TaskRight - TaskCheckbox - TaskTextGap;
        var ft = TaskText(text, brush, textWidth, rtl, false);
        ft.SetFontWeight(FontWeights.SemiBold);
        dc.DrawText(ft, new Point(textLeft, top + Math.Max(0, (TaskLineHeight - ft.Height) / 2)));
    }

    private static FormattedText TaskText(string text, Brush brush, double width, bool rtl, bool completed)
    {
        var typeface = new Typeface(HangAll.UI.Typography.ForText(text), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture,
            rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            typeface, TaskFontSize, brush, 1.0)
        {
            MaxTextWidth = width,
            MaxTextHeight = TaskLineHeight,
            LineHeight = TaskLineHeight,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left,
        };
        if (completed) ft.SetTextDecorations(TextDecorations.Strikethrough);
        return ft;
    }

    private static bool IsArabic(string s) => s.Any(c => c >= '؀' && c <= 'ۿ');
    private static int StableSeed(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }
    private static Color Lighten(Color c, double k) => Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
    private static Color Darken(Color c, double k) => Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));
}
