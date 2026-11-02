using System.Windows;
using HangAll.Core;

namespace HangAll.UI;

/// <summary>Where everything goes, in device independent pixels.</summary>
public static class Layout
{
    public const double PanelHeight = 240;
    public const double RopeTop = 10;
    public const double Spacing = 174;
    public const double CardWidth = 150;
    /// <summary>Keeps the rope's edge controls clear when cards overlap heavily.</summary>
    public const double SideInset = 72;
    /// <summary>How far above the rope the top of the clip sits.</summary>
    public const double PinAbove = 9.5;
    public const double PinHeight = 26;
    public const double PinWidth = 9;
    /// <summary>Distance from the top of the hanging view (the clip) to the card.</summary>
    public const double CardOffsetBelowTop = PinHeight - 12;
    public const double FrameRadius = 16;
    public const double FrameInset = 4;
    public const double PhotoMaxHeight = 104;
    // Characters per note line is Notes.TaskFontSize's ratio to the 360-wide design,
    // not this size: widening the card alone only enlarges the same 9 characters. The
    // two move together so a smaller design font still reads at about 11 DIP here.
    public const double NoteCardWidth = 152;
    public const double NoteCardHeight = 114;
    /// <summary>Files that are neither stills nor recordings get a compact desktop-icon card.</summary>
    public const double FileCardWidth = 120;
    public const double FileIconSize = 48;
    public const double FileIconTop = 12;
    public const double FileNamePadding = 8;
    public const double FileNameLines = 2;
    /// <summary>Keeps the name clear of the icon above it at every type scale.</summary>
    public const double FileNameGap = 8;
    /// <summary>The name block grows with the type scale, so nothing is clipped at the large setting.</summary>
    public static double FileNameHeight => Math.Ceiling(Typography.SmallLineHeight * FileNameLines) + 1;
    public static double FileNameTop => FileIconTop + FileIconSize + FileNameGap;
    public static double FileCardHeight => FileNameTop + FileNameHeight + 8;

    /// <summary>
    /// Top of the file icon. The card always reserves two name lines, so a
    /// one-line name would leave all of the spare room at the bottom; centring
    /// the icon-plus-name block keeps the card balanced without changing size.
    /// </summary>
    public static double FileIconTopFor(double nameHeight)
    {
        double content = FileIconSize + FileNameGap + Math.Clamp(nameHeight, Typography.SmallLineHeight, FileNameHeight);
        return Math.Max(FileIconTop, (FileCardHeight - content) / 2);
    }
    public const double DropZoneMaxWidth = 260;
    public const double DropZoneHeight = 64;
    /// <summary>Matches the top of a hanging card after its peg grips the rope.</summary>
    public const double DropZoneGap = CardOffsetBelowTop - PinAbove;

    /// <summary>The rope hangs as a parabola from edge to edge of the screen.</summary>
    public static double Sag(double width) => Math.Min(58, width * 0.036);

    public static double RopeY(double x, double width)
    {
        if (width <= 0) return RopeTop;
        double f = x / width;
        return RopeTop + 4 * Sag(width) * f * (1 - f);
    }

    public static Rect DropZone(double width)
    {
        double zoneWidth = Math.Min(DropZoneMaxWidth, Math.Max(180, width * 0.24));
        zoneWidth = Math.Min(zoneWidth, Math.Max(0, width - 24));
        double zoneTop = RopeY(width / 2, width) + DropZoneGap;
        double zoneHeight = Math.Min(DropZoneHeight, Math.Max(48, PanelHeight - zoneTop - 12));
        return new Rect((width - zoneWidth) / 2, zoneTop, zoneWidth, zoneHeight);
    }

    /// <summary>How many cards fit on one comfortable row before they would start to overlap.</summary>
    public static int FitCapacity(double width) =>
        Math.Max(1, (int)Math.Floor(Math.Max(0, width - CardWidth - 2 * SideInset) / Spacing) + 1);

    /// <summary>
    /// The step between cards when they are spread evenly edge to edge. It stays at
    /// <see cref="Spacing"/> while there is room and shrinks below the card width once
    /// there is not, so crowded rows overlap instead of folding or paging.
    /// </summary>
    public static double SpreadStep(int count, double leftLimit, double rightLimit)
    {
        if (count <= 1) return 0;
        double room = Math.Max(0, rightLimit - leftLimit - CardWidth);
        return Math.Min(Spacing, room / (count - 1));
    }

    /// <summary>
    /// Centres an evenly spread row of cards on the line. The row keeps the
    /// screen's centre even when the clipboard wallet reserves the right edge;
    /// it only shifts left by the amount that is actually needed to clear the
    /// reserved band. A row too wide for the band shrinks its step and centres
    /// inside the band instead.
    /// </summary>
    public static double SpreadX(
        int index,
        int count,
        double width,
        double leftLimit,
        double rightLimit)
    {
        if (width <= 0) return 0;
        double step = SpreadStep(count, leftLimit, rightLimit);
        double rowWidth = CardWidth + Math.Max(0, count - 1) * step;
        double half = rowWidth / 2;
        double minimum = leftLimit + half;
        double maximum = rightLimit - half;
        double centre = maximum < minimum
            ? (leftLimit + rightLimit) / 2
            : Math.Clamp(width / 2, minimum, maximum);
        return centre - half + CardWidth / 2 + index * step;
    }

    /// <summary>Keeps a card centre inside the usable band, clear of the wallet.</summary>
    public static double ClampCardCenterX(double x, double leftLimit, double rightLimit)
    {
        double half = CardWidth / 2;
        double minimum = leftLimit + half;
        double maximum = rightLimit - half;
        if (maximum < minimum) return (leftLimit + rightLimit) / 2;
        return Math.Clamp(x, minimum, maximum);
    }

    /// <summary>The photo fits inside the card area keeping its proportions.</summary>
    public static Size PhotoSize(double pixelWidth, double pixelHeight)
    {
        double maxW = CardWidth - 14, maxH = PhotoMaxHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0) return new Size(maxW, maxH);
        double scale = Math.Min(maxW / pixelWidth, maxH / pixelHeight);
        return new Size(pixelWidth * scale, pixelHeight * scale);
    }

    /// <summary>The card around the photo: the photo plus the glass inset.</summary>
    public static Size CardSize(double pixelWidth, double pixelHeight)
    {
        var p = PhotoSize(pixelWidth, pixelHeight);
        return new Size(p.Width + FrameInset * 2, p.Height + FrameInset * 2);
    }

    /// <summary>The card around any hung item, using the compact file card when appropriate.</summary>
    public static Size CardSize(Pegged item) =>
        Notes.IsNote(item.Path)
            ? new Size(NoteCardWidth, NoteCardHeight)
            : item.IsFile
                ? new Size(FileCardWidth, FileCardHeight)
                : CardSize(item.PixelWidth, item.PixelHeight);
}
