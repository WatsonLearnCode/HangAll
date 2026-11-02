using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HangAll.Interop;
using Forms = System.Windows.Forms;

namespace HangAll.UI;

/// <summary>The icon in the notification area, and its menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon = new();
    private Icon? _logo;
    private Icon? _fallback;

    public event Action? LeftClick;
    public Func<ContextMenu>? MenuProvider { get; set; }

    public TrayIcon()
    {
        _icon.Text = Strings.AppName;
        _icon.Visible = true;
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        Repaint();
        Theme.Changed += Repaint;
    }

    public void Repaint()
    {
        _logo ??= LoadLogo();
        if (_logo is not null)
        {
            _icon.Icon = _logo;
            return;
        }

        _fallback ??= DrawLogo();
        _icon.Icon = _fallback;
    }

    /// <summary>The app icon, at the size the taskbar wants.</summary>
    private static Icon? LoadLogo()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/HangAll.ico"));
            if (info is null) return null;
            using var stream = info.Stream;
            int size = (int)Math.Round(16 * (System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920) / 1920.0 * 1.0);
            return new Icon(stream, new Size(Math.Max(16, size), Math.Max(16, size)));
        }
        catch { return null; }
    }

    private void ShowMenu()
    {
        var menu = MenuProvider?.Invoke();
        if (menu is null) return;
        Menus.Show(menu);
    }

    public void Balloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.None); } catch { }
    }

    /// <summary>Renders the shared brand mark if the embedded ICO cannot be read.</summary>
    private static Icon? DrawLogo()
    {
        if (Brand.Mark is not { } mark) return null;

        const int size = 32;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var tile = new System.Windows.Rect(0.5, 0.5, size - 1, size - 1);
            double radius = size * 0.22;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                double right = tile.Right, bottom = tile.Bottom;
                path.BeginFigure(new System.Windows.Point(tile.Left + radius, tile.Top), true, true);
                path.LineTo(new System.Windows.Point(right - radius, tile.Top), true, false);
                path.ArcTo(new System.Windows.Point(right, tile.Top + radius), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new System.Windows.Point(right, bottom - radius), true, false);
                path.ArcTo(new System.Windows.Point(right - radius, bottom), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new System.Windows.Point(tile.Left + radius, bottom), true, false);
                path.ArcTo(new System.Windows.Point(tile.Left, bottom - radius), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new System.Windows.Point(tile.Left, tile.Top + radius), true, false);
                path.ArcTo(new System.Windows.Point(tile.Left + radius, tile.Top), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(System.Windows.Media.Brushes.White, null, geometry);
            context.DrawImage(mark, new System.Windows.Rect(0, 0, size, size));
        }

        var raster = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        raster.Render(visual);
        using var stream = new System.IO.MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(raster));
        encoder.Save(stream);
        stream.Position = 0;

        using var bitmap = new Bitmap(stream);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        Theme.Changed -= Repaint;
        _icon.Visible = false;
        _icon.Dispose();
        _logo?.Dispose();
        _fallback?.Dispose();
    }
}
