using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using HangAll.Core;
using HangAll.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using static HangAll.Interop.Native;

namespace HangAll.UI;

/// <summary>
/// A raw Win32 drop target covering only the dashed zone. The main line window
/// stays permanently click-through, while this window is the only surface OLE
/// has to discover. WPF's transparent windows already own an internal drop
/// target, so using a raw HWND here avoids replacing or racing that target.
/// </summary>
[ComVisible(true)]
public sealed class DropTargetWindow : IDropTarget, IDisposable
{
    private const short CF_HDROP = 15;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWLP_USERDATA = -21;
    private const uint LWA_ALPHA = 0x00000002;
    private const int WM_NCCREATE = 0x0081;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PAINT = 0x000F;
    private const int WHITE_BRUSH = 0;

    private static readonly object ClassLock = new();
    private static WndProcDelegate? _wndProc;
    private static ushort _classAtom;
    private static string? _className;
    private static IntPtr _classBrush;

    private readonly LineWindow _line;
    private IntPtr _handle;
    private bool _registered;
    private bool _armed;
    private bool _active;
    private bool _hot;
    private bool _ignored;
    private bool _shown;
    private bool _mouseTransparent = true;
    private System.Windows.Rect? _lastTarget;
    private bool _lastTargetShown;
    private bool _disposed;

    public bool Active => _active;

    public event Action<bool, bool>? DropStateChanged;
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public DropTargetWindow(LineWindow line)
    {
        _line = line;
        _line.Positioned += SyncBounds;
        _line.Ordered += SyncTopmost;
    }

    /// <summary>Creates and shows the native hit surface when universal drop is enabled.</summary>
    public void Show()
    {
        if (_disposed || !Settings.Current.DropZoneEnabled) return;
        EnsureHandle();
        if (_handle == IntPtr.Zero) return;
        _shown = true;
        SyncBounds();
    }

    /// <summary>Registers or removes OLE support when the setting changes.</summary>
    public void SyncDropTarget()
    {
        if (_disposed) return;
        if (!Settings.Current.DropZoneEnabled)
        {
            ClearState();
            _ignored = false;
            _armed = false;
            HideTarget();
            if (_registered && _handle != IntPtr.Zero) RevokeDragDrop(_handle);
            _registered = false;
            return;
        }

        EnsureHandle();
        if (_handle == IntPtr.Zero) return;
        if (!_registered)
        {
            int hr = RegisterDragDrop(_handle, this);
            if (unchecked((uint)hr) == 0x80040101u)
            {
                // A stale registration can survive if a previous process was
                // terminated during an active drag. The raw HWND should not
                // normally hit this path.
                int revoke = RevokeDragDrop(_handle);
                Log.Notice($"Drop proxy registration was already owned; revoke=0x{revoke:X8}");
                hr = RegisterDragDrop(_handle, this);
            }
            if (hr >= 0)
            {
                _registered = true;
                Log.Notice(
                    $"Drop proxy registered hr=0x{hr:X8} hwnd=0x{_handle.ToInt64():X} " +
                    $"exstyle=0x{GetWindowLongPtr(_handle, GWL_EXSTYLE).ToInt64():X}");
            }
            else
            {
                Log.Error($"Could not register the drop proxy: 0x{hr:X8}");
            }
        }

        Show();
        DragAcceptFiles(_handle, true);
        SyncBounds();
    }

    /// <summary>
    /// Arms the proxy only while the shell reports an OLE drag session. The
    /// DragEnter handler still requires CF_HDROP before accepting the drop.
    /// While unarmed the window is fully mouse-transparent, so another app's
    /// window can still be dragged from underneath the drop zone.
    /// </summary>
    public void Arm(bool armed)
    {
        if (_disposed) return;

        if (!armed)
        {
            // The OLE session is over. Forget rejected drags so the next file
            // drag can arm the proxy again.
            _ignored = false;
            ClearState();
            if (_handle != IntPtr.Zero) SyncBounds();
            return;
        }

        if (!Settings.Current.DropZoneEnabled || !_registered || _ignored)
            armed = false;
        if (_armed == armed)
        {
            SyncMouseTransparency();
            if (_handle != IntPtr.Zero) SyncBounds();
            return;
        }

        _armed = armed;
        if (armed) Show();
        SyncMouseTransparency();
        if (_handle != IntPtr.Zero) SyncBounds();
    }

    private void EnsureHandle()
    {
        if (_handle != IntPtr.Zero || _disposed) return;
        EnsureWindowClass();
        if (_classAtom == 0) return;

        var self = GCHandle.Alloc(this);
        _handle = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            _className!,
            "HangAll drop target",
            WS_POPUP,
            0,
            0,
            (int)Layout.DropZoneMaxWidth,
            (int)Layout.DropZoneHeight,
            _line.Handle,
            IntPtr.Zero,
            GetModuleHandle(null),
            GCHandle.ToIntPtr(self));

        if (_handle == IntPtr.Zero)
        {
            self.Free();
            Log.Error($"Could not create the drop proxy: {Marshal.GetLastWin32Error()}");
            return;
        }

        // A one-unit alpha is effectively invisible but keeps the HWND in hit
        // testing. Alpha zero layered windows are skipped by WindowFromPoint.
        SetLayeredWindowAttributes(_handle, 0, 1, LWA_ALPHA);
        _mouseTransparent = true;
        ShowWindow(_handle, SW_HIDE);
    }

    /// <summary>
    /// WS_EX_TRANSPARENT is required for cross-process mouse pass-through.
    /// WM_NCHITTEST alone only passes input within the same thread's hit-test
    /// chain, so another application otherwise cannot begin dragging its
    /// window from underneath the invisible proxy.
    /// </summary>
    private void SyncMouseTransparency()
    {
        if (_disposed || _handle == IntPtr.Zero) return;

        bool transparent = !Settings.Current.DropZoneEnabled || (!_armed && !_active);
        if (_mouseTransparent == transparent) return;

        if (transparent)
            AddExStyle(_handle, WS_EX_TRANSPARENT);
        else
            RemoveExStyle(_handle, WS_EX_TRANSPARENT);
        _mouseTransparent = transparent;
    }

    /// <summary>Keeps the invisible OLE surface above the visible line while it is armed.</summary>
    private void SyncTopmost()
    {
        if (_disposed || _handle == IntPtr.Zero || !_shown) return;
        SetWindowPos(
            _handle,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void SyncBounds()
    {
        if (_disposed || !Settings.Current.DropZoneEnabled || _handle == IntPtr.Zero)
            return;

        var target = _line.DropTargetScreenRect();
        if (target is null) return;
        var rect = target.Value;
        // Unarmed and already in place: another SetWindowPos every mouse tick
        // only makes DWM re-evaluate a window nobody can reach. When a drag
        // arms the proxy the position is still reasserted, so topmost stays.
        if (!_armed && _lastTarget == rect && _lastTargetShown == _shown) return;
        _lastTarget = rect;
        _lastTargetShown = _shown;
        uint flags = SWP_NOACTIVATE;
        if (_shown) flags |= SWP_SHOWWINDOW;
        SetWindowPos(
            _handle,
            HWND_TOPMOST,
            (int)Math.Round(rect.Left),
            (int)Math.Round(rect.Top),
            Math.Max(1, (int)Math.Round(rect.Width)),
            Math.Max(1, (int)Math.Round(rect.Height)),
            flags);
    }

    private void HideTarget()
    {
        _shown = false;
        SyncMouseTransparency();
        if (_handle != IntPtr.Zero) ShowWindow(_handle, SW_HIDE);
    }

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // The proxy takes the mouse only while the controller has
                // positively identified a drag approaching the dashed zone.
                // Unarmed presses pass through, including files placed under
                // the area covered by the screenshot line.
                return new IntPtr(Settings.Current.DropZoneEnabled && (_armed || _active)
                    ? HTCLIENT
                    : HTTRANSPARENT);
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_DROPFILES:
                HandleLegacyDrop(wParam);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0) return;
        lock (ClassLock)
        {
            if (_classAtom != 0) return;
            _className = $"HangAll.DropTarget.{Environment.ProcessId}";
            _wndProc = StaticWindowProc;
            _classBrush = GetStockObject(WHITE_BRUSH);
            var cls = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                hbrBackground = _classBrush,
                lpszClassName = _className,
            };
            _classAtom = RegisterClassEx(ref cls);
            if (_classAtom == 0)
                Log.Error($"Could not register the drop proxy class: {Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr StaticWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCCREATE)
        {
            var create = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            SetWindowLongPtr(hwnd, GWLP_USERDATA, create.lpCreateParams);
        }

        var selfPointer = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (selfPointer != IntPtr.Zero && GCHandle.FromIntPtr(selfPointer).Target is DropTargetWindow target)
        {
            if (msg == WM_NCDESTROY)
            {
                target._handle = IntPtr.Zero;
                SetWindowLongPtr(hwnd, GWLP_USERDATA, IntPtr.Zero);
                GCHandle.FromIntPtr(selfPointer).Free();
            }
            return target.HandleMessage(hwnd, msg, wParam, lParam);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool IsPointOnTarget(POINT screen)
        => _handle != IntPtr.Zero
           && GetWindowRect(_handle, out var rect)
           && rect.Contains(screen);

    private void SetState(bool active, bool hot)
    {
        hot &= active;
        if (_active == active && _hot == hot) return;
        bool changed = _active != active;
        _active = active;
        _hot = hot;
        SyncMouseTransparency();
        if (changed)
            Log.Notice(active ? "Drop proxy drag entered" : "Drop proxy drag left");
        DropStateChanged?.Invoke(_active, _hot);
    }

    private void ClearState()
    {
        SetState(false, false);
        _armed = false;
        SyncMouseTransparency();
    }

    private static string[] FilesFrom(ComIDataObject? dataObject)
    {
        if (dataObject is null) return Array.Empty<string>();
        var format = new FORMATETC
        {
            cfFormat = CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        try
        {
            dataObject.GetData(ref format, out STGMEDIUM medium);
            try
            {
                if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
                    return Array.Empty<string>();
                return ReadDropFiles(medium.unionmember);
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException)
        {
            return Array.Empty<string>();
        }
        catch (Exception e)
        {
            Log.Error($"Could not read dropped files in the proxy: {e.Message}");
            return Array.Empty<string>();
        }
    }

    private static string[] NormalizeFiles(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return null; }
            })
            .Where(path => path is not null && File.Exists(path) && !Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Reads every path from an HDROP, including multi-file shell drags.</summary>
    private static string[] ReadDropFiles(IntPtr drop)
    {
        if (drop == IntPtr.Zero) return Array.Empty<string>();
        uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
        if (count == 0) return Array.Empty<string>();

        var paths = new List<string>((int)Math.Min(count, 256));
        for (uint i = 0; i < count; i++)
        {
            uint length = DragQueryFile(drop, i, null, 0);
            if (length == 0) continue;
            var text = new StringBuilder((int)length + 1);
            if (DragQueryFile(drop, i, text, (uint)text.Capacity) > 0)
                paths.Add(text.ToString());
        }
        return NormalizeFiles(paths);
    }

    private void HandleLegacyDrop(IntPtr drop)
    {
        string[] files;
        bool over = false;
        try
        {
            files = ReadDropFiles(drop);
            if (DragQueryPoint(drop, out var client) && GetWindowRect(_handle, out var rect))
            {
                var screen = new POINT(rect.Left + client.X, rect.Top + client.Y);
                over = rect.Contains(screen);
            }
        }
        finally
        {
            DragFinish(drop);
        }

        bool accepted = Settings.Current.DropZoneEnabled
                        && !DragSource.IsDragging
                        && files.Length > 0
                        && over;
        if (!accepted)
        {
            ClearState();
            return;
        }

        ClearState();
        _line.Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(files));
    }

    int IDropTarget.DragEnter(ComIDataObject dataObject, uint keyState, POINT point, ref uint effect)
    {
        var files = Settings.Current.DropZoneEnabled && !DragSource.IsDragging
            ? FilesFrom(dataObject)
            : Array.Empty<string>();
        bool accepted = files.Length > 0 && IsPointOnTarget(point);
        Log.Notice($"Drop proxy DragEnter files={files.Length} point={point.X},{point.Y} over={IsPointOnTarget(point)} accepted={accepted}");
        effect = accepted ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        _ignored = !accepted;
        if (accepted)
        {
            SyncBounds();
            SyncTopmost();
            SetState(true, true);
        }
        else
        {
            _armed = false;
            SetState(false, false);
            SyncBounds();
        }
        return 0;
    }

    int IDropTarget.DragOver(uint keyState, POINT point, ref uint effect)
    {
        bool accepted = _active
                        && Settings.Current.DropZoneEnabled
                        && !DragSource.IsDragging
                        && IsPointOnTarget(point);
        effect = accepted ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        SetState(accepted, accepted);
        if (accepted) SyncTopmost();
        return 0;
    }

    int IDropTarget.DragLeave()
    {
        Log.Notice("Drop proxy DragLeave");
        _ignored = false;
        ClearState();
        return 0;
    }

    int IDropTarget.Drop(ComIDataObject dataObject, uint keyState, POINT point, ref uint effect)
    {
        var files = Settings.Current.DropZoneEnabled && !DragSource.IsDragging
            ? FilesFrom(dataObject)
            : Array.Empty<string>();
        bool accepted = _active && files.Length > 0 && IsPointOnTarget(point);
        effect = accepted ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        _ignored = false;
        ClearState();
        if (accepted)
        {
            Log.Notice($"Drop proxy accepted files={files.Length}");
            _line.Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(files));
        }
        else
        {
            Log.Notice("Drop proxy rejected");
        }
        return 0;
    }

    public void Close()
    {
        if (_disposed) return;
        _disposed = true;
        _line.Positioned -= SyncBounds;
        _line.Ordered -= SyncTopmost;
        ClearState();
        _ignored = false;
        if (_handle != IntPtr.Zero)
        {
            if (_registered) RevokeDragDrop(_handle);
            _registered = false;
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose() => Close();

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public string? lpszName;
        public string? lpszClass;
        public int dwExStyle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectType);
}
