using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>
/// Opens a context menu from windows that never activate. A hidden window
/// briefly owns the foreground so WPF can keep the whole menu session active,
/// including a hover-opened side panel hosted as a real submenu.
/// </summary>
public static class Menus
{
    private const string SidePanelAnchorTag = "MenuSidePanelAnchor";
    private const double HoverDelayMs = 320;
    private const double CloseGraceMs = 240;
    private const double HoverMonitorIntervalMs = 48;

    private static readonly HashSet<ContextMenu> OpenMenus = new();
    private static readonly Dictionary<ContextMenu, List<PanelLink>> AttachedPanels = new();
    private static readonly DispatcherTimer ExpandTimer = CreateTimer(HoverDelayMs);
    private static readonly DispatcherTimer CloseTimer = CreateTimer(CloseGraceMs);
    private static readonly DispatcherTimer HoverMonitorTimer =
        CreateTimer(HoverMonitorIntervalMs);

    private static HwndSource? _focus;
    private static IntPtr? _returnForeground;
    private static PanelLink? _pendingExpand;
    private static PanelLink? _activePanel;
    private static PanelLink? _hoveredAnchor;
    private static bool _hoverCloseArmed;
    private static bool _lastAnyOpen;
    // Menus that are fading out after a leaf item was activated.
    private static readonly HashSet<ContextMenu> FadingClosed = new();
    private static readonly DependencyProperty LeafFadeArmedProperty =
        DependencyProperty.RegisterAttached("LeafFadeArmed", typeof(bool), typeof(Menus), new PropertyMetadata(false));
    private static readonly DependencyProperty FadeClosedArmedProperty =
        DependencyProperty.RegisterAttached("FadeClosedArmed", typeof(bool), typeof(Menus), new PropertyMetadata(false));

    static Menus()
    {
        ExpandTimer.Tick += (_, _) => CompleteExpand();
        CloseTimer.Tick += (_, _) => BeginCollapse();
        HoverMonitorTimer.Tick += (_, _) => ScheduleCloseIfAway();
    }

    public static event Action? OpenStateChanged;

    public static bool AnyOpen =>
        OpenMenus.Any(menu => menu.IsOpen) ||
        _activePanel?.Anchor.IsSubmenuOpen == true;

    private static IntPtr Focus
    {
        get
        {
            _focus ??= new HwndSource(new HwndSourceParameters("HangAll.MenuFocus")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            });
            return _focus.Handle;
        }
    }

    public static void Show(ContextMenu menu)
    {
        var focus = Focus;
        if (OpenMenus.Count == 0 && _returnForeground is null)
            _returnForeground = Native.GetForegroundWindow();

        OpenMenus.Add(menu);
        if (Native.GetForegroundWindow() != focus) Native.SetForegroundWindow(focus);

        menu.Closed += (_, _) =>
        {
            OpenMenus.Remove(menu);
            Core.Log.Notice("Menu closed");
            DetachMenu(menu);
            NotifyOpenStateChanged();

            // A command may replace this menu with another one. Only the last
            // menu in the focus session should give the foreground back.
            menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (OpenMenus.Count != 0 || AnyOpen || _returnForeground is not { } before)
                    return;

                _returnForeground = null;
                if (before != IntPtr.Zero && Native.GetForegroundWindow() == focus)
                    Native.SetForegroundWindow(before);
            });
        };

        Core.Log.Notice("Menu open");
        CancelFade(menu);
        ArmFadeOnClick(menu);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        UiMotion.PopIn(menu, 115, 2);
        NotifyOpenStateChanged();
    }

    /// <summary>
    /// A leaf item now stays open when activated and the close is driven here,
    /// so the menu can fade out instead of vanishing. WPF's own dismissal paths
    /// (click outside, Esc) are untouched and stay instant.
    /// </summary>
    private static void ArmFadeOnClick(ContextMenu menu)
    {
        foreach (var item in LeafItems(menu))
        {
            if (item.GetValue(LeafFadeArmedProperty) is true) continue;
            item.SetValue(LeafFadeArmedProperty, true);
            item.StaysOpenOnClick = true;
            item.Click += (_, _) => FadeClose(menu);
        }

        if (menu.GetValue(FadeClosedArmedProperty) is true) return;
        menu.SetValue(FadeClosedArmedProperty, true);
        menu.Closed += (_, _) => CancelFade(menu);
    }

    private static IEnumerable<MenuItem> LeafItems(ItemsControl parent)
    {
        foreach (var entry in parent.Items)
        {
            if (entry is not MenuItem item) continue;
            if (item.Items.Count == 0)
            {
                yield return item;
                continue;
            }
            foreach (var child in LeafItems(item)) yield return child;
        }
    }

    private static void FadeClose(ContextMenu menu)
    {
        if (!menu.IsOpen) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            menu.IsOpen = false;
            return;
        }
        if (!FadingClosed.Add(menu)) return;
        menu.IsHitTestVisible = false;

        // A guard, in case the animation never completes (window hidden, clock
        // paused): the menu has to close anyway, or the app looks stuck.
        var guard = new DispatcherTimer(DispatcherPriority.Send, menu.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        void Finish()
        {
            guard.Stop();
            CancelFade(menu);
            if (menu.IsOpen) menu.IsOpen = false;
        }
        guard.Tick += (_, _) => Finish();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) => Finish();
        menu.BeginAnimation(UIElement.OpacityProperty, fade);
        guard.Start();
    }

    private static void CancelFade(ContextMenu menu)
    {
        FadingClosed.Remove(menu);
        menu.BeginAnimation(UIElement.OpacityProperty, null);
        menu.Opacity = 1;
        // WPF keeps IsHitTestVisible in step with IsOpen, so restoring it here
        // would only fight the framework: a closed menu is hit-test dead either way.
    }

    internal static void AttachPanel(
        ContextMenu menu,
        MenuItem anchor,
        Func<MenuSidePanel> factory)
    {
        var host = new MenuItem
        {
            Style = (Style)anchor.FindResource("MenuSidePanelHost"),
            StaysOpenOnClick = true,
            Visibility = Visibility.Collapsed,
        };
        anchor.Items.Add(host);
        anchor.Tag = SidePanelAnchorTag;
        anchor.StaysOpenOnClick = true;

        var link = new PanelLink(menu, anchor, host, factory);
        if (!AttachedPanels.TryGetValue(menu, out var links))
        {
            links = new List<PanelLink>();
            AttachedPanels.Add(menu, links);
            menu.Opened += (_, _) => OnMenuOpened(menu);
            menu.Closed += (_, _) => DetachMenu(menu);
        }
        links.Add(link);

        anchor.MouseEnter += (_, _) => OnAnchorEnter(link);
        anchor.MouseLeave += (_, _) => OnAnchorLeave(link);
        anchor.Click += (_, _) => OpenPanel(link, focusRows: false);
        anchor.PreviewKeyDown += (_, e) => OnAnchorKeyDown(link, e);
        anchor.SubmenuOpened += (_, _) =>
        {
            // WPF can open a submenu from its own hover delay. Only the explicit
            // hover timer or a click/keyboard action may turn the item into a
            // live side panel.
            if (!link.AllowSubmenuOpen)
            {
                link.Anchor.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (!link.AllowSubmenuOpen)
                        link.Anchor.IsSubmenuOpen = false;
                });
                return;
            }

            _activePanel = link;
            NotifyOpenStateChanged();
            RevealPanel(link, focusRows: link.FocusRowsAfterOpen);
        };
        anchor.SubmenuClosed += (_, _) => OnSubmenuClosed(link);

        menu.MouseEnter += (_, _) => OnMenuEnter(menu);
        menu.MouseLeave += (_, _) => OnMenuLeave(menu);
    }

    public static void CloseAll()
    {
        CancelTimers();
        CloseActivePanel();

        var menus = OpenMenus.ToArray();
        OpenMenus.Clear();
        foreach (var menu in menus)
            menu.IsOpen = false;

        foreach (var menu in AttachedPanels.Keys.ToArray())
            DetachMenu(menu);

        if (_returnForeground is { } before)
        {
            _returnForeground = null;
            if (before != IntPtr.Zero &&
                Native.GetForegroundWindow() == Focus)
                Native.SetForegroundWindow(before);
        }

        NotifyOpenStateChanged();
    }

    private static void OnMenuOpened(ContextMenu menu)
    {
        if (AttachedPanels.TryGetValue(menu, out var links))
        {
            foreach (var link in links)
            {
                link.AllowSubmenuOpen = false;
                link.FocusRowsAfterOpen = false;
            }
        }

        NotifyOpenStateChanged();
    }

    private static void OnMenuEnter(ContextMenu menu)
    {
        if (!OpenMenus.Contains(menu)) return;
        if (_activePanel?.Menu == menu)
        {
            _hoverCloseArmed = true;
            HoverMonitorTimer.Start();
        }
        CancelClose();
    }

    private static void OnMenuLeave(ContextMenu menu)
    {
        if (!OpenMenus.Contains(menu)) return;
        ScheduleCloseIfAway();
    }

    private static void OnAnchorEnter(PanelLink link)
    {
        if (!OpenMenus.Contains(link.Menu) || !link.Anchor.IsEnabled) return;

        CancelClose();
        _hoverCloseArmed = true;
        _hoveredAnchor = link;
        if (_activePanel == link && link.Anchor.IsSubmenuOpen) return;

        _pendingExpand = link;
        ExpandTimer.Stop();
        ExpandTimer.Start();
    }

    private static void OnAnchorLeave(PanelLink link)
    {
        if (ReferenceEquals(_hoveredAnchor, link)) _hoveredAnchor = null;
        if (ReferenceEquals(_pendingExpand, link))
        {
            ExpandTimer.Stop();
            _pendingExpand = null;
        }
        ScheduleCloseIfAway();
    }

    private static void OnAnchorKeyDown(PanelLink link, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Right or Key.Space)) return;
        e.Handled = true;
        OpenPanel(link, focusRows: true);
    }

    private static void OnSubmenuClosed(PanelLink link)
    {
        if (ReferenceEquals(_activePanel, link)) _activePanel = null;
        link.AllowSubmenuOpen = false;
        link.FocusRowsAfterOpen = false;
        if (ReferenceEquals(_pendingExpand, link))
        {
            ExpandTimer.Stop();
            _pendingExpand = null;
        }
        NotifyOpenStateChanged();
    }

    private static void CompleteExpand()
    {
        ExpandTimer.Stop();
        var link = _pendingExpand;
        _pendingExpand = null;
        if (link is null || !ReferenceEquals(_hoveredAnchor, link)) return;
        if (!link.Menu.IsOpen || !link.Anchor.IsEnabled) return;
        _hoverCloseArmed = true;
        OpenPanel(link, focusRows: false);
    }

    private static bool OpenPanel(PanelLink link, bool focusRows)
    {
        if (!link.Menu.IsOpen || !link.Anchor.IsEnabled) return false;

        CancelTimers();
        _hoveredAnchor ??= link;

        if (ReferenceEquals(_activePanel, link) && link.Anchor.IsSubmenuOpen)
        {
            if (focusRows) link.Panel?.FocusFirstRow();
            return true;
        }

        if (_activePanel is { } previous && !ReferenceEquals(previous, link))
        {
            previous.Anchor.IsSubmenuOpen = false;
            previous.AllowSubmenuOpen = false;
            previous.FocusRowsAfterOpen = false;
        }

        if (link.Panel is null)
        {
            link.Panel = link.Factory();
            link.Panel.EscapeRequested += CloseAll;
            link.Panel.CloseRequested += CloseAll;
            link.Panel.MouseEnter += (_, _) =>
            {
                _hoverCloseArmed = true;
                HoverMonitorTimer.Start();
                CancelClose();
            };
            link.Panel.MouseLeave += (_, _) =>
            {
                ScheduleCloseIfAway();
            };
            link.Host.Header = link.Panel;
            link.Host.Visibility = Visibility.Visible;
        }

        var anchorPoint = link.Anchor.PointToScreen(
            new Point(Math.Max(0, link.Anchor.ActualWidth / 2), Math.Max(0, link.Anchor.ActualHeight / 2)));
        link.Panel.LimitHeight(Displays.Nearest(new Native.POINT(
            (int)Math.Round(anchorPoint.X),
            (int)Math.Round(anchorPoint.Y))));

        _activePanel = link;
        link.FocusRowsAfterOpen = focusRows;
        link.AllowSubmenuOpen = true;
        link.Anchor.IsSubmenuOpen = true;
        if (IsPointerInside(link.Menu) || IsPointerInside(link.Anchor))
            _hoverCloseArmed = true;
        if (_hoverCloseArmed)
            HoverMonitorTimer.Start();

        NotifyOpenStateChanged();
        return true;
    }

    private static void RevealPanel(PanelLink link, bool focusRows)
    {
        link.Anchor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!link.Anchor.IsSubmenuOpen || link.Panel is not { } panel) return;

            link.Anchor.ApplyTemplate();
            link.Anchor.UpdateLayout();
            panel.ApplyTemplate();
            panel.UpdateLayout();

            bool opensToRight = OpensToRight(link);
            panel.PlayReveal(opensToRight);
            if (IsPointerInside(panel))
            {
                _hoverCloseArmed = true;
                HoverMonitorTimer.Start();
            }
            if (focusRows) panel.FocusFirstRow();
        });
    }

    private static bool OpensToRight(PanelLink link)
    {
        if (link.Panel is not { } panel) return true;

        var anchorLeft = link.Anchor.PointToScreen(new Point(0, 0));
        var anchorRight = link.Anchor.PointToScreen(
            new Point(Math.Max(1, link.Anchor.ActualWidth), 0));
        var panelLeft = panel.PointToScreen(new Point(MenuSidePanel.ShadowInset, 0));
        var panelRight = panel.PointToScreen(
            new Point(Math.Max(MenuSidePanel.ShadowInset + 1, panel.ActualWidth), 0));

        double rightGap = Math.Abs(panelLeft.X - anchorRight.X);
        double leftGap = Math.Abs(anchorLeft.X - panelRight.X);
        return rightGap <= leftGap;
    }

    private static void ScheduleCloseIfAway()
    {
        if (_activePanel is not { Anchor.IsSubmenuOpen: true } active ||
            active.Panel is null)
        {
            HoverMonitorTimer.Stop();
            return;
        }

        bool insideMenu = IsPointerInside(active.Menu);
        bool insidePanel = active.Panel is { } panel && IsPointerInside(panel);

        if (insideMenu || insidePanel)
        {
            _hoverCloseArmed = true;
            CancelClose();
            return;
        }

        if (!_hoverCloseArmed) return;
        if (active.Panel?.IsCollapseAnimating == true) return;

        if (!CloseTimer.IsEnabled)
            CloseTimer.Start();
    }

    private static void BeginCollapse()
    {
        CloseTimer.Stop();
        if (IsPointerInsideSession()) return;
        if (_activePanel is not { Anchor.IsSubmenuOpen: true } active ||
            active.Panel is not { } panel)
            return;

        panel.PlayCollapse(() =>
        {
            if (IsPointerInsideSession())
            {
                panel.CancelCollapse();
                return;
            }

            CloseActivePanel();
            active.Menu.IsOpen = false;
        });
    }

    private static void CancelClose()
    {
        CloseTimer.Stop();
        _activePanel?.Panel?.CancelCollapse();
    }

    private static void CancelTimers()
    {
        ExpandTimer.Stop();
        CloseTimer.Stop();
        HoverMonitorTimer.Stop();
        _pendingExpand = null;
    }

    private static void CloseActivePanel()
    {
        var active = _activePanel;
        _activePanel = null;
        _pendingExpand = null;
        _hoveredAnchor = null;
        _hoverCloseArmed = false;
        ExpandTimer.Stop();
        CloseTimer.Stop();
        HoverMonitorTimer.Stop();

        if (active is null) return;
        active.AllowSubmenuOpen = false;
        active.FocusRowsAfterOpen = false;
        active.Anchor.IsSubmenuOpen = false;
    }

    private static void DetachMenu(ContextMenu menu)
    {
        if (!AttachedPanels.Remove(menu, out var links)) return;

        CancelTimers();
        if (_activePanel is { } active && ReferenceEquals(active.Menu, menu))
            CloseActivePanel();

        foreach (var link in links)
        {
            link.AllowSubmenuOpen = false;
            link.Anchor.IsSubmenuOpen = false;
        }

        _hoverCloseArmed = false;
        _hoveredAnchor = null;
        HoverMonitorTimer.Stop();
        NotifyOpenStateChanged();
    }

    private static bool IsPointerInsideSession()
    {
        if (_activePanel is not { } active) return false;
        return IsPointerInside(active.Menu) ||
               active.Panel is { } panel && IsPointerInside(panel);
    }

    private static bool IsPointerInside(FrameworkElement element)
    {
        if (!element.IsVisible) return false;

        if (!Native.GetCursorPos(out Native.POINT cursor))
            return element.IsMouseOver;

        try
        {
            Point topLeft = element.PointToScreen(new Point(0, 0));
            Point bottomRight = element.PointToScreen(new Point(
                Math.Max(0, element.ActualWidth),
                Math.Max(0, element.ActualHeight)));
            return cursor.X >= Math.Min(topLeft.X, bottomRight.X) &&
                   cursor.X < Math.Max(topLeft.X, bottomRight.X) &&
                   cursor.Y >= Math.Min(topLeft.Y, bottomRight.Y) &&
                   cursor.Y < Math.Max(topLeft.Y, bottomRight.Y);
        }
        catch (InvalidOperationException)
        {
            return element.IsMouseOver;
        }
    }

    private static void NotifyOpenStateChanged()
    {
        bool anyOpen = AnyOpen;
        if (anyOpen == _lastAnyOpen) return;
        _lastAnyOpen = anyOpen;
        OpenStateChanged?.Invoke();
    }

    private static DispatcherTimer CreateTimer(double milliseconds)
        => new(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };

    private sealed class PanelLink(
        ContextMenu menu,
        MenuItem anchor,
        MenuItem host,
        Func<MenuSidePanel> factory)
    {
        internal ContextMenu Menu { get; } = menu;
        internal MenuItem Anchor { get; } = anchor;
        internal MenuItem Host { get; } = host;
        internal Func<MenuSidePanel> Factory { get; } = factory;
        internal MenuSidePanel? Panel { get; set; }
        internal bool AllowSubmenuOpen { get; set; }
        internal bool FocusRowsAfterOpen { get; set; }
    }
}
