using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HangAll.Core;
using HangAll.Interop;
using static HangAll.Interop.Native;

namespace HangAll.UI;

/// <summary>
/// A transparent strip along the top of the screen that floats over every
/// app, never takes focus, and lets clicks pass through everywhere except
/// over the photos.
/// </summary>
[ComVisible(true)]
public sealed class LineWindow : Window
{
    public LineCanvas Canvas { get; }
    public Display? Display { get; private set; }
    public IntPtr Handle { get; private set; }
    private HwndSource? _source;
    private double _ropeStartOffset, _ropeStartY;
    private readonly System.Windows.Threading.DispatcherTimer _ropeFollow = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IntPtr _searchPreviousForeground;
    private bool _searchActivated;

    /// <summary>While a drag or a press is in progress the whole strip stays solid, so events keep coming.</summary>
    public bool HoldMouse { get; set; }

    private bool _clickThrough = true;
    private bool _externalDropActive;
    private bool _externalDropHot;

    internal event Action? Positioned;
    internal event Action? Ordered;

    /// <summary>Raised when a file drag enters or leaves the top strip.</summary>
    public event Action<bool>? ExternalDropChanged;

    /// <summary>True: every click goes to the window underneath. False: the strip takes the mouse.</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value || Handle == IntPtr.Zero) return;
            _clickThrough = value;
            UpdateTransparentStyle();
        }
    }

    public LineWindow(Line line)
    {
        Canvas = new LineCanvas(line);
        Canvas.SearchEditingChanged += OnSearchEditingChanged;
        Title = Strings.AppName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 800; Height = Layout.PanelHeight;
        Content = Canvas;
        Focusable = false;

        // Holding the rope moves the whole line up or down the screen.
        Canvas.RopeDragStarted += () =>
        {
            _ropeStartOffset = Settings.Current.LineOffset;
            _ropeStartY = Displays.Cursor().Y;
            _ropeFollow.Start();
        };
        _ropeFollow.Tick += (_, _) =>
        {
            if (Display is null) return;
            double dy = (Displays.Cursor().Y - _ropeStartY) / Display.Scale;
            double offset = Math.Max(0, _ropeStartOffset + dy);
            if (Math.Abs(offset - Settings.Current.LineOffset) < 0.5) return;
            Settings.Current.LineOffset = offset;
            PlaceOn(Display);
        };
        Canvas.RopeDragEnded += () => { _ropeFollow.Stop(); Settings.Current.Save(); };
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        ApplyOpacity();
    }

    /// <summary>Applies the chosen opacity to the whole line window, including pegs and cards.</summary>
    public void ApplyOpacity()
        => Opacity = Settings.LineOpacityOption(Settings.Current.LineOpacity);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        Handle = _source.Handle;
        FullScreen.Own.Add(Handle);
        // Per pixel transparency: where nothing is drawn, clicks reach the window
        // underneath, in any app. WS_EX_TRANSPARENT makes the whole strip pass
        // clicks through, and is lifted only while the pointer is over something.
        AddExStyle(Handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        _clickThrough = true;
        _source.AddHook(Hook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ropeFollow.Stop();
        Canvas.SearchEditingChanged -= OnSearchEditingChanged;
        Canvas.Detach();
        _source?.RemoveHook(Hook);
        if (Handle != IntPtr.Zero) FullScreen.Own.Remove(Handle);
        _source = null;
        base.OnClosed(e);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // The strip spans the whole width of the screen, so it only takes
                // the mouse over real UI. External file drags use DropTargetWindow.
                handled = true;
                int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
                var point = new POINT(x, y);
                bool interactive = !_clickThrough && !DragSource.IsDragging && IsOverPhoto(point);
                return new IntPtr(HoldMouse || interactive
                    ? HTCLIENT
                    : HTTRANSPARENT);
            }
            case WM_MOUSEACTIVATE:
                handled = true;
                if (GetCursorPos(out var cursor) && IsOverSearch(cursor))
                {
                    RememberSearchForeground();
                    return new IntPtr(MA_ACTIVATE);
                }
                return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (Canvas.IsSearchEditing && _searchActivated)
            Canvas.CloseSearch();
    }

    private void OnSearchEditingChanged()
    {
        if (Canvas.IsSearchEditing) BeginSearchEditing();
        else EndSearchEditing();
    }

    private void BeginSearchEditing()
    {
        if (Handle == IntPtr.Zero) return;
        RememberSearchForeground();
        _searchActivated = false;
        _clickThrough = false;
        Focusable = true;
        RemoveExStyle(Handle, WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        Activate();
        SetForegroundWindow(Handle);
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (!Canvas.IsSearchEditing) return;
                if (GetForegroundWindow() != Handle)
                {
                    Canvas.CloseSearch();
                    return;
                }
                _searchActivated = true;
                Canvas.FocusSearchInput();
            }));
    }

    private void EndSearchEditing()
    {
        if (Handle == IntPtr.Zero) return;
        bool ownedForeground = GetForegroundWindow() == Handle;
        _searchActivated = false;
        Focusable = false;
        _clickThrough = true;
        UpdateTransparentStyle();
        AddExStyle(Handle, WS_EX_NOACTIVATE);
        if (ownedForeground && _searchPreviousForeground != IntPtr.Zero)
            SetForegroundWindow(_searchPreviousForeground);
        _searchPreviousForeground = IntPtr.Zero;
    }

    private void RememberSearchForeground()
    {
        if (_searchPreviousForeground != IntPtr.Zero) return;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != Handle)
            _searchPreviousForeground = foreground;
    }

    private void UpdateTransparentStyle()
    {
        if (Handle == IntPtr.Zero) return;
        if (_clickThrough) AddExStyle(Handle, WS_EX_TRANSPARENT);
        else RemoveExStyle(Handle, WS_EX_TRANSPARENT);
    }

    internal void SetExternalDrop(bool active, bool hot = false)
    {
        hot &= active;
        if (_externalDropActive == active && _externalDropHot == hot) return;
        bool changed = _externalDropActive != active;
        _externalDropActive = active;
        _externalDropHot = hot;
        if (changed) Log.Notice(active ? "External file drag entered the line" : "External file drag left the line");
        RefreshDropZone();
        Dispatcher.BeginInvoke(() =>
        {
            if (changed) ExternalDropChanged?.Invoke(active);
        });
    }

    private void RefreshDropZone()
        => Canvas.SetDropZone(_externalDropActive, _externalDropHot);

    /// <summary>Screen pixels to canvas coordinates.</summary>
    public Point? ToCanvas(POINT screen)
    {
        if (_source is null || !GetWindowRect(Handle, out var r)) return null;
        var m = _source.CompositionTarget.TransformFromDevice;
        var p = m.Transform(new Point(screen.X - r.Left, screen.Y - r.Top));
        // The canvas slides; undo its offset.
        p.Y -= ((TranslateTransform)Canvas.RenderTransform).Y;
        return p;
    }

    /// <summary>
    /// The drop target in screen pixels, based on where it sits once the rope
    /// is revealed. The reveal transform is intentionally ignored so OLE can
    /// discover the target while the rope is still sliding into view.
    /// </summary>
    internal Rect? DropTargetScreenRect()
    {
        if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var r)) return null;
        double width = Canvas.ActualWidth;
        if (width <= 0) return null;
        var zone = Canvas.DropZoneRect ?? Layout.DropZone(width);
        double scale = _source?.CompositionTarget?.TransformToDevice.M11 ?? Display?.Scale ?? 1;
        if (scale <= 0) scale = 1;
        return new Rect(
            r.Left + zone.Left * scale,
            r.Top + zone.Top * scale,
            zone.Width * scale,
            zone.Height * scale);
    }

    /// <summary>The visible card rectangle in physical screen pixels, including the reveal offset and DPI.</summary>
    internal Rect? CardScreenRect(Guid id, bool includeReveal = true, bool stable = false)
    {
        if (Handle == IntPtr.Zero || _source is null || !GetWindowRect(Handle, out var window)) return null;
        var card = Canvas.Cards.FirstOrDefault(c => c.Item.Id == id);
        if (card is null || card.Item.Falling || card.Visibility != Visibility.Visible) return null;

        double revealY = includeReveal ? ((TranslateTransform)Canvas.RenderTransform).Y : 0;
        var local = stable ? card.StableCardRect : card.CardRect;
        var device = _source.CompositionTarget.TransformToDevice;
        var topLeft = device.Transform(new Point(local.Left, local.Top + revealY));
        var bottomRight = device.Transform(new Point(local.Right, local.Bottom + revealY));
        return new Rect(
            window.Left + topLeft.X,
            window.Top + topLeft.Y,
            Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y));
    }

    public bool IsOverPhoto(POINT screen)
    {
        if (!Canvas.Revealed) return false;
        var p = ToCanvas(screen);
        if (p is null) return false;
        if (Canvas.SearchRect is { } search && search.Contains(p.Value)) return true;
        if (Canvas.ClearButtonRect is { } clear && clear.Contains(p.Value)) return true;
        if (Canvas.NoteQuickAddRect is { } quickNote && quickNote.Contains(p.Value)) return true;
        if (Canvas.ClipboardWalletRect is { } wallet && wallet.Contains(p.Value)) return true;
        if (Canvas.TagRect is { } tag && tag.Contains(p.Value)) return true;
        if (Canvas.IsOverCard(p.Value)) return true;
        if (Canvas.HintRect is { } hint && hint.Contains(p.Value)) return true;
        if (Canvas.UndoRect is { } undo && undo.Contains(p.Value)) return true;
        return Canvas.RopeDragging || Canvas.IsOverRope(p.Value);
    }

    private bool IsOverSearch(POINT screen)
    {
        if (!Canvas.Revealed || ToCanvas(screen) is not { } point) return false;
        return Canvas.SearchRect is { } search && search.Contains(point);
    }

    /// <summary>The line hangs on the screen you are using, under the taskbar if it is at the top.</summary>
    public void PlaceOn(Display? monitor = null)
    {
        var m = monitor ?? Displays.UnderPointer();
        Display = m;
        int height = (int)Math.Round(Layout.PanelHeight * m.Scale);
        var work = m.Work;
        // The line can hang lower than the very top, below the tabs of a maximised window.
        double maxOffset = Math.Max(0, work.Height / m.Scale - Layout.PanelHeight);
        double offset = Math.Clamp(Settings.Current.LineOffset, 0, maxOffset);
        Settings.Current.LineOffset = offset;
        int top = work.Top + (int)Math.Round(offset * m.Scale);
        if (Handle == IntPtr.Zero)
        {
            Left = work.Left; Top = top; Width = work.Width / m.Scale; Height = Layout.PanelHeight;
            return;
        }
        const uint flags = SWP_NOACTIVATE;
        SetWindowPos(Handle, HWND_TOPMOST, work.Left, top, work.Width, height, flags);
        // Crossing to a screen with another DPI makes Windows suggest a rescaled
        // frame first. Say it again, now that the DPI has settled.
        if (GetWindowRect(Handle, out var r) && (r.Left != work.Left || r.Top != top || r.Width != work.Width || r.Height != height))
            SetWindowPos(Handle, HWND_TOPMOST, work.Left, top, work.Width, height, flags);
        Canvas.Width = work.Width / m.Scale;
        Canvas.UpdateLayout();
        Positioned?.Invoke();
    }

    public void OrderFront()
    {
        if (!IsVisible) Show();
        if (Handle == IntPtr.Zero) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Ordered?.Invoke();
    }

    public void OrderOut()
    {
        if (Handle != IntPtr.Zero) ShowWindow(Handle, SW_HIDE);
    }

    /// <summary>The panel's frame in physical pixels.</summary>
    public RECT Frame
    {
        get
        {
            GetWindowRect(Handle, out var r);
            return r;
        }
    }
}
