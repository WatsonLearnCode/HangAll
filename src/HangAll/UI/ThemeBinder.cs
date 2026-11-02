using System.Windows;
using System.Windows.Interop;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>
/// One place for the theme wiring every custom window shares: the dark native
/// title bar, the subscription to light/dark switches and a guaranteed
/// unsubscribe when the window closes. Keeping it here removes the per-window
/// subscribe/unsubscribe boilerplate and the leaks that come with forgetting one.
/// </summary>
internal sealed class ThemeBinder : IDisposable
{
    private readonly Window _window;
    private readonly Action _refresh;
    private readonly Action<IntPtr>? _onSource;
    private readonly Action<IntPtr>? _afterChrome;
    private IntPtr _handle;
    private IntPtr _largeIcon;
    private IntPtr _smallIcon;
    private bool _disposed;

    public ThemeBinder(
        Window window,
        Action refresh,
        Action<IntPtr>? onSource = null,
        Action<IntPtr>? afterChrome = null)
    {
        _window = window;
        _refresh = refresh;
        _onSource = onSource;
        _afterChrome = afterChrome;
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnClosed;
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>Re-run the window's own theme refresh and repaint the native chrome.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        _refresh();
        ApplyChrome();
        ApplyPostChrome();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        ApplyWindowIcon();
        _onSource?.Invoke(_handle);
        ApplyChrome();
        ApplyPostChrome();
    }

    private void ApplyWindowIcon()
    {
        if (_handle == IntPtr.Zero) return;
        (_largeIcon, _smallIcon) = Native.LoadApplicationIcons();
        if (_largeIcon != IntPtr.Zero)
            Native.SendMessage(_handle, Native.WM_SETICON, new IntPtr(Native.ICON_BIG), _largeIcon);
        if (_smallIcon != IntPtr.Zero)
        {
            Native.SendMessage(_handle, Native.WM_SETICON, new IntPtr(Native.ICON_SMALL), _smallIcon);
            Native.SendMessage(_handle, Native.WM_SETICON, new IntPtr(Native.ICON_SMALL2), _smallIcon);
        }
    }

    private void OnThemeChanged() => Refresh();

    private void ApplyChrome()
    {
        if (_handle != IntPtr.Zero) Native.SetImmersiveDarkMode(_handle, Theme.AppsDark);
    }

    private void ApplyPostChrome()
    {
        if (_handle != IntPtr.Zero) _afterChrome?.Invoke(_handle);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Theme.Changed -= OnThemeChanged;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        if (_largeIcon != IntPtr.Zero) Native.DestroyIcon(_largeIcon);
        if (_smallIcon != IntPtr.Zero) Native.DestroyIcon(_smallIcon);
        _largeIcon = IntPtr.Zero;
        _smallIcon = IntPtr.Zero;
    }
}
