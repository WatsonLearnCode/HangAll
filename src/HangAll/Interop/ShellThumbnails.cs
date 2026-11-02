using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace HangAll.Interop;

/// <summary>
/// The picture Windows itself draws for a file: a frame from a recording, the
/// first page of a PDF, and so on. Pictures never come through here; they are
/// decoded properly in Core.Thumbnails.
/// </summary>
public static class ShellThumbnails
{
    // SIIGBF_* from shobjidl_core.h
    private const uint BiggerSizeOk = 0x01;
    private const uint IconOnly = 0x04;
    private const uint ThumbnailOnly = 0x08;
    private const uint ScaleUp = 0x100;

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid IID_IImageList = new("46eb5926-582e-4017-9fdf-e8998daa0950");

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Native.SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [ComImport, Guid("46eb5926-582e-4017-9fdf-e8998daa0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        // The leading methods keep GetIcon at the correct vtable slot. Only
        // GetIcon is called by this app.
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // SHGFI_* from shellapi.h
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const int SHIL_EXTRALARGE = 2;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    /// <summary>A thumbnail no larger than `size` on its longest side, or null when Windows has none.</summary>
    public static BitmapSource? Get(string path, int size) => Get(path, size, ThumbnailOnly);

    private static BitmapSource? Get(string path, int size, uint mode)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory is null) return null;
            var box = new Native.SIZE { cx = size, cy = size };
            if (factory.GetImage(box, mode | BiggerSizeOk | ScaleUp, out hbm) != 0 || hbm == IntPtr.Zero) return null;

            // The shell hands back a GDI bitmap; WPF wants a DIB-backed source.
            var image = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// The icon Windows itself draws for a file, for anything without a real
    /// thumbnail: a PDF reader missing, a zip, an executable, a plain text file.
    /// </summary>
    public static BitmapSource? Icon(string path, int size)
    {
        // Ask the shell image factory for its icon mode first; it can return
        // the current 256px or larger artwork on modern Windows.
        var factoryIcon = Get(path, Math.Clamp(size, 32, 256), IconOnly);
        if (factoryIcon is not null && factoryIcon.PixelWidth > 0 && factoryIcon.PixelHeight > 0)
            return factoryIcon;

        // Fall back through the system image lists. Jumbo is 256px and extra
        // large is 48px, both much clearer than the old 32px large icon.
        var jumbo = FromImageList(path, SHIL_JUMBO);
        if (jumbo is not null) return jumbo;
        var extraLarge = FromImageList(path, SHIL_EXTRALARGE);
        if (extraLarge is not null) return extraLarge;

        var info = new SHFILEINFO();
        var cb = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, 0, ref info, cb, SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static BitmapSource? FromImageList(string path, int imageListSize)
    {
        IImageList? list = null;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var info = new SHFILEINFO();
            var cb = (uint)Marshal.SizeOf<SHFILEINFO>();
            if (SHGetFileInfo(path, 0, ref info, cb, SHGFI_SYSICONINDEX) == IntPtr.Zero) return null;

            var iid = IID_IImageList;
            if (SHGetImageList(imageListSize, ref iid, out list) != 0 || list is null) return null;
            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero) return null;

            var image = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            if (list is not null) Marshal.ReleaseComObject(list);
        }
    }
}
