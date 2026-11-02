using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>
/// Everything you can change, arranged as a quiet four-section settings center.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly Action _apply;
    private TextBox _hotKey = null!;
    private TextBlock _hotKeyError = null!;
    private TextBox _quickLookKey = null!;
    private ListBox _folders = null!;
    private TextBlock _inbox = null!;
    private TextBox _offset = null!;
    private Control? _languageButton;
    private Button? _fontSizeButton;
    private readonly Dictionary<SettingsCategory, ScrollViewer> _pages = new();
    private readonly Dictionary<SettingsCategory, Button> _navButtons = new();
    private readonly Dictionary<SettingsCategory, System.Windows.Shapes.Path> _navIcons = new();
    private SettingsCategory _activeCategory = SettingsCategory.Line;
    private TextBlock? _categoryTitle;
    private TextBlock? _categoryDescription;
    private Border? _glassTint;
    private Border? _glassShell;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private HwndSourceHook? _sourceHook;
    private readonly List<(CheckBox box, Func<bool> read, Func<bool>? enabledBy)> _checks = new();
    private readonly List<(FrameworkElement element, Func<bool> enabledBy)> _dependentControls = new();
    private bool _building;
    private string _appliedLanguage;
    private double _appliedFontScale;
    private readonly DispatcherTimer _applyTimer;
    private readonly DispatcherTimer _liveSaveTimer;
    private readonly ThemeBinder _theme;
    // The update row, refreshed from Updater.Changed instead of on every build.
    private TextBlock? _updateStatus;
    private Button? _updateMain;
    private Button? _updateSkip;

    private const double PageWidth = 960;
    private const double PageHeight = 660;
    private const double PageMinWidth = 940;
    private const double PageMinHeight = 620;
    private const double SidebarWidth = 210;
    private const double ShellRadius = 14;
    private const double SectionGap = 12;
    private const double RowGap = 8;
    private const double SectionHeadingGap = 10;
    private const double HelperGap = 4;
    private const double PairGap = 10;
    private const double LabelControlGap = 10;
    private const double SettingsContentTopOffset = 10;
    private const double FieldLabelWidth = 120;
    private const double HelperLabelMaxWidth = 120;
    // Full-width rows can afford a wider label column than a paired row, which lets
    // the longest help strings finish in two lines instead of trailing off.
    private const double WideLabelWidth = 176;
    private const double RowMinHeight = 32;
    private const double ControlHeight = 30;
    private const double ContactCardWidth = 166;
    private const double ContactAvatarSize = 42;
    private const double ContactGithubWidth = 112;
    private const string AuthorHandle = "@WatsonLearnCode";
    private const double IconSlotSize = 24;
    private const double NavIconGap = 10;
    private const double NavPaddingLeft = 14;
    private const double NavPaddingRight = 12;
    private const double ControlRadius = 8;
    private const double CardRadius = 10;
    private const double IconSlotRadius = 6;
    private const double FolderBaseHeight = 56;
    private const double FolderMinHeight = 48;
    private enum FocusTarget { None, HotKey, Offset, Folders, Language, FontSize }
    private enum SettingsCategory { Line, CardsPreview, CaptureImport, AppSettings }
    private enum SettingsGlyph
    {
        HangingRail,
        StackedCards,
        CaptureImport,
        PowerFeedback,
        UpdateCheck,
        AppSettings,
        Appearance,
        ClearSweep,
        PreviewEye,
        HangingFile,
        LanguageGlobe,
    }
    private static readonly Thickness ButtonPadding = new(10, 3, 10, 3);

    public static void Open(Action apply)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new SettingsWindow(apply);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>Rebuild only when the type scale or language actually changed.</summary>
    public static void RefreshTypography()
    {
        if (_open is null) return;
        bool languageChanged = !string.Equals(_open._appliedLanguage, Strings.Language, StringComparison.OrdinalIgnoreCase);
        bool fontScaleChanged = Math.Abs(_open._appliedFontScale - Typography.FontScale) >= 0.001;
        if (!languageChanged && !fontScaleChanged) return;
        _open.FlushApply();
        _open._appliedLanguage = Strings.Language;
        _open._appliedFontScale = Typography.FontScale;
        _open.Title = Strings.SettingsTitle;
        _open.FlowDirection = Strings.Flow;
        Typography.Apply(_open);
        _open.ReplaceContent(languageChanged ? FocusTarget.Language : FocusTarget.None);
    }

    /// <summary>Refresh the check boxes after the line menu changes a setting.</summary>
    public static void SyncFromSettings() => _open?.SyncChecks();
    private SettingsWindow(Action apply)
    {
        _apply = apply;
        Title = Strings.SettingsTitle;
        MinWidth = PageMinWidth; MinHeight = PageMinHeight;
        Width = Math.Max(MinWidth, Math.Min(PageWidth, SystemParameters.WorkArea.Width - 60));
        Height = Math.Max(MinHeight, Math.Min(PageHeight, SystemParameters.WorkArea.Height - 60));
        AllowsTransparency = false;
        SetResourceReference(BackgroundProperty, "SettingsHitSurface");
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Icon = Brand.Icon;
        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        _appliedLanguage = Strings.Language;
        _appliedFontScale = Typography.FontScale;
        _applyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _applyTimer.Tick += (_, _) => FlushApply();
        _liveSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _liveSaveTimer.Tick += (_, _) =>
        {
            _liveSaveTimer.Stop();
            Settings.Current.Save();
        };
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 62,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        StateChanged += (_, _) =>
        {
            UpdateGlassShape();
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ReapplyNativeGlass));
        };
        RefreshTheme();
        _theme = new ThemeBinder(this, RefreshTheme, OnSourceInitialized, ApplyNativeGlass);
        Closed += (_, _) =>
        {
            AppController.HotKeyAttempted -= SyncHotKeyError;
            FlushApply();
            _applyTimer.Stop();
            _liveSaveTimer.Stop();
            Settings.Current.Save();
            if (_hwndSource is not null && _sourceHook is not null)
                _hwndSource.RemoveHook(_sourceHook);
        };
        // Subscribed once here rather than in Build, which runs again on every
        // language or scale change.
        AppController.HotKeyAttempted += SyncHotKeyError;
        Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => Updater.Changed -= OnUpdaterChanged;
    }

    /// <summary>A check finished on a background thread; bring the row up to date on ours.</summary>
    private void OnUpdaterChanged()
    {
        if (!IsLoaded || _updateStatus is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshUpdateRow));
    }

    /// <summary>The last section: what this build is, and the one button that asks GitHub.</summary>
    private Grid BuildUpdateSection()
    {
        var s = Settings.Current;
        var (section, body) = Section(
            Strings.SettingsSectionUpdate,
            SettingsCategory.AppSettings,
            SettingsGlyph.PowerFeedback);

        var version = Text(
            string.Format(Strings.UpdateCurrent, Updater.CurrentVersion),
            Typography.BodySize,
            Typography.BodyLineHeight,
            FontWeights.Medium);
        _updateStatus = Text("", Typography.SmallSize, Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        _updateStatus.TextWrapping = TextWrapping.Wrap;
        _updateStatus.Margin = new Thickness(0, HelperGap, 0, 0);

        _updateMain = ActionButton(Strings.UpdateCheck, Strings.UpdateCheckTip);
        _updateMain.MinWidth = 96;
        _updateMain.Click += (_, _) => StartUpdateCheck();

        _updateSkip = ActionButton(Strings.UpdateSkip, Strings.UpdateSkipTip);
        _updateSkip.Margin = new Thickness(0, 0, HelperGap * 2, 0);
        _updateSkip.Visibility = Visibility.Collapsed;
        _updateSkip.Click += (_, _) =>
        {
            Updater.Skip();
            RefreshUpdateRow();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(_updateSkip);
        buttons.Children.Add(_updateMain);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(version);
        labels.Children.Add(_updateStatus);
        head.Children.Add(labels);
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        body.Children.Add(head);

        var auto = Check(Strings.UpdateAuto, null, s.AutoCheckUpdates,
            v => s.AutoCheckUpdates = v, () => s.AutoCheckUpdates);
        auto.Margin = new Thickness(0, RowGap, 0, 0);
        body.Children.Add(auto);
        var help = Text(Strings.UpdateAutoHelp, Typography.SmallSize, Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        help.Margin = new Thickness(0, HelperGap, 0, 0);
        body.Children.Add(help);

        RefreshUpdateRow();
        return section;
    }

    /// <summary>Paints the row from whatever the updater last told us.</summary>
    private void RefreshUpdateRow()
    {
        if (_updateStatus is null || _updateMain is null || _updateSkip is null) return;
        _updateMain.IsEnabled = Updater.State != Updater.Status.Checking;
        _updateSkip.Visibility = Visibility.Collapsed;

        switch (Updater.State)
        {
            case Updater.Status.Checking:
                _updateStatus.Text = Strings.UpdateChecking;
                _updateMain.Content = Strings.UpdateCheck;
                break;
            case Updater.Status.Available when Updater.Found is { } found:
                _updateStatus.Text = string.Format(Strings.UpdateFound, found.Version);
                _updateMain.Content = Strings.UpdateView;
                _updateSkip.Visibility = Visibility.Visible;
                break;
            case Updater.Status.UpToDate:
                _updateStatus.Text = Strings.UpdateUpToDate;
                _updateMain.Content = Strings.UpdateCheck;
                break;
            case Updater.Status.Failed:
                _updateStatus.Text = Updater.FailureReason switch
                {
                    Updater.Reason.NoRepo => Strings.UpdateNoRepo,
                    Updater.Reason.NoRelease => Strings.UpdateNoRelease,
                    Updater.Reason.Timeout => Strings.UpdateTimeout,
                    Updater.Reason.Network => Strings.UpdateOffline,
                    _ => string.Format(Strings.UpdateFailed, Updater.Failure ?? ""),
                };
                _updateMain.Content = Strings.UpdateRetry;
                break;
            default:
                _updateStatus.Text = Updater.LastChecked is { } last
                    ? string.Format(Strings.UpdateLast, last.ToString("yyyy-MM-dd HH:mm"))
                    : Strings.UpdateNever;
                _updateMain.Content = Strings.UpdateCheck;
                break;
        }
    }

    /// <summary>The one button: ask GitHub, or open the release page once one is known.</summary>
    private void StartUpdateCheck()
    {
        if (Updater.State == Updater.Status.Available && Updater.Found is not null)
        {
            Updater.OpenPage();
            return;
        }
        // Fire and forget: the row follows Updater.Changed when the answer lands.
        _ = Updater.CheckAsync();
        RefreshUpdateRow();
    }

    /// <summary>Runs a pending debounced apply, if one is waiting.</summary>
    private void FlushApply()
    {
        if (!_applyTimer.IsEnabled) return;
        _applyTimer.Stop();
        _apply();
    }

    private void RefreshTheme()
    {
        SetResourceReference(BackgroundProperty, "SettingsHitSurface");
        Foreground = (Brush)Application.Current.Resources["MenuFg"];
        ReplaceContent();
        UpdateGlassShape();
    }

    private void OnSourceInitialized(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Native.RemoveStyle(hwnd, Native.WS_MAXIMIZEBOX);
        Native.RemoveExStyle(hwnd, Native.WS_EX_LAYERED);
        _hwndSource ??= HwndSource.FromHwnd(hwnd);
        if (_hwndSource is not null && _sourceHook is null)
        {
            _sourceHook = (IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
            {
                if (message == Native.WM_SYSCOMMAND &&
                    (wParam.ToInt64() & 0xFFF0) == Native.SC_MAXIMIZE)
                    handled = true;
                if (message is Native.WM_DPICHANGED or Native.WM_DWMCOMPOSITIONCHANGED)
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(ReapplyNativeGlass));
                return IntPtr.Zero;
            };
            _hwndSource.AddHook(_sourceHook);
        }
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        UpdateGlassShape();
        ApplyGlassTint();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ReapplyNativeGlass));
    }

    private void ApplyGlassTint()
    {
        double transparency = Settings.GlassTransparencyOption(Settings.Current.GlassTransparency);
        Theme.ApplySettingsGlassLevels(transparency);
        if (_glassTint is null) return;

        byte alpha = Native.SettingsGlassFallbackOpaque
            ? (byte)255
            : (byte)Math.Round(1 + (1 - transparency) * 254);
        Color baseColor = Application.Current?.TryFindResource("MenuBg") is SolidColorBrush menu
            ? menu.Color
            : (Theme.AppsDark
                ? Color.FromRgb(0x2B, 0x2B, 0x2B)
                : Color.FromRgb(0xF9, 0xF9, 0xF9));
        Color color = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        _glassTint.Background = Theme.Freeze(new SolidColorBrush(color));
    }

    private void ReapplyNativeGlass()
    {
        if (!IsLoaded) return;
        ApplyNativeGlass(_hwnd == IntPtr.Zero ? new WindowInteropHelper(this).Handle : _hwnd);
    }

    private void ApplyNativeGlass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_hwnd == IntPtr.Zero) _hwnd = hwnd;
        if (_hwnd == IntPtr.Zero) return;
        Native.ApplySettingsGlass(
            hwnd,
            Settings.Current.GlassTransparency,
            Theme.AppsDark,
            rounded: true);
        ApplyGlassTint();
    }

    private void UpdateGlassShape()
    {
        if (_glassShell is null) return;
        _glassShell.CornerRadius = new CornerRadius(ShellRadius);
        _glassShell.BorderThickness = new Thickness(1);
    }

    /// <summary>
    /// Rebuilds the visual tree without touching the native window. Theme and
    /// language changes both use this so their position, scroll and focus survive.
    /// </summary>
    private void ReplaceContent(FocusTarget forcedFocus = FocusTarget.None)
    {
        // The brand lockup and theme brushes are baked into the tree, so replacing
        // Content is necessary; the window itself stays put.
        var left = Left; var top = Top; var width = Width; var height = Height;
        var scrollOffsets = _pages.ToDictionary(page => page.Key, page => page.Value.VerticalOffset);
        var focusTarget = forcedFocus != FocusTarget.None ? forcedFocus
            : _hotKey?.IsKeyboardFocusWithin == true ? FocusTarget.HotKey
            : _offset?.IsKeyboardFocusWithin == true ? FocusTarget.Offset
            : _folders?.IsKeyboardFocusWithin == true ? FocusTarget.Folders
            : _languageButton?.IsKeyboardFocusWithin == true ? FocusTarget.Language
            : _fontSizeButton?.IsKeyboardFocusWithin == true ? FocusTarget.FontSize
            : _pages.TryGetValue(_activeCategory, out var activePage) && activePage.IsKeyboardFocusWithin
                ? FocusTarget.None
            : FocusTarget.None;
        var folderIndex = _folders?.SelectedIndex ?? -1;
        _languageButton = null;
        _fontSizeButton = null;
        _pages.Clear();
        _navButtons.Clear();
        _navIcons.Clear();
        _checks.Clear();
        _dependentControls.Clear();
        _building = true;
        try { Content = Build(); }
        finally { _building = false; }
        ShowPage(_activeCategory, moveFocus: false);
        Left = left; Top = top; Width = width; Height = height;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            foreach (var (category, page) in _pages)
                if (scrollOffsets.TryGetValue(category, out double offset))
                    page.ScrollToVerticalOffset(offset);
        }));
        if (folderIndex >= 0 && folderIndex < _folders!.Items.Count)
            _folders.SelectedIndex = folderIndex;
        if (focusTarget != FocusTarget.None)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var target = focusTarget switch
                {
                    FocusTarget.Language => _languageButton,
                    FocusTarget.FontSize => _fontSizeButton,
                    _ => null,
                };
                if (target is not null)
                {
                    target.Focus();
                }
                else if (focusTarget == FocusTarget.HotKey)
                {
                    _hotKey!.Focus();
                    _hotKey.SelectAll();
                }
                else if (focusTarget == FocusTarget.Offset)
                {
                    _offset!.Focus();
                    _offset.SelectAll();
                }
                else
                {
                    _folders!.Focus();
                }
            }));
        }
    }

    private UIElement Build()
    {
        _building = true;
        var s = Settings.Current;
        var inboxPath = Inbox.Folder;
        var screenshotsPath = Shell.ScreenshotsFolder();
        var settingsPath = System.IO.Path.Combine(Settings.Folder, "settings.json");

        var lineSections = new List<UIElement>();
        var cardSections = new List<UIElement>();
        var captureSections = new List<UIElement>();
        var appSections = new List<UIElement>();

        // ---- Line and appearance ----
        var (lineSection, lineBody) = Section(
            Strings.SettingsSectionLineControls,
            SettingsCategory.Line,
            SettingsGlyph.HangingRail);
        lineBody.Children.Add(Check(
            Strings.RevealAtTopEdge,
            Strings.RevealAtTopEdgeTip,
            s.RevealAtTopEdge,
            v => s.RevealAtTopEdge = v,
            () => s.RevealAtTopEdge,
            help: Strings.RevealAtTopEdgeHelp));
        var sensitivity = RevealSensitivitySwitch();
        _dependentControls.Add((sensitivity, () => s.RevealAtTopEdge));

        _hotKey = StandardField(new TextBox
        {
            Text = s.HotKey, IsReadOnly = true, ToolTip = Strings.ShortcutTip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 0, 12, 0),
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        });
        _hotKey.PreviewKeyDown += OnHotKeyPressed;
        _hotKeyError = new TextBlock
        {
            Text = Strings.ShortcutTaken,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = Typography.Ui,
            FontSize = Typography.SmallSize,
        };
        _hotKeyError.SetResourceReference(TextBlock.ForegroundProperty, "TextDanger");
        var hotKeyField = new StackPanel();
        hotKeyField.Children.Add(_hotKey);
        hotKeyField.Children.Add(_hotKeyError);
        lineBody.Children.Add(PairRow(
            Row(Strings.RevealSensitivityShort, sensitivity, Strings.RevealSensitivityTip),
            Row(Strings.Shortcut, hotKeyField, Strings.ShortcutTip)));
        SyncHotKeyError();

        _offset = StandardField(new TextBox
        {
            Text = ((int)s.LineOffset).ToString(), ToolTip = Strings.LineDistanceTip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 0, 9, 0),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        });
        _offset.LostFocus += (_, _) =>
        {
            if (double.TryParse(_offset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                s.LineOffset = Math.Max(0, v);
                Changed();
            }
        };
        _offset.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Keyboard.ClearFocus();
            if (double.TryParse(_offset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                s.LineOffset = Math.Max(0, v);
                Changed();
            }
        };
        var offsetRow = new Grid();
        offsetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offsetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offsetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offsetRow.Children.Add(_offset);
        var offsetUnit = new TextBlock
        {
            Text = "DIP",
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight,
        };
        Typography.ApplyText(offsetUnit);
        Grid.SetColumn(offsetUnit, 1);
        offsetRow.Children.Add(offsetUnit);
        var resetOffset = ResetDefaultButton(Strings.LineDistanceResetTip, () =>
        {
            Settings.Current.LineOffset = 0;
            _offset.Text = "0";
            Changed();
        });
        resetOffset.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(resetOffset, 2);
        offsetRow.Children.Add(resetOffset);
        lineBody.Children.Add(PairRow(
            Row(Strings.LineDistance, offsetRow, Strings.LineDistanceTip),
            Row(
                Strings.RetractDelayShort,
                RetractDelaySwitch(),
                Strings.RetractDelayTip,
                Strings.RetractDelayHelp)));
        var stayOpen = Check(Strings.StayDownWhileUnused, Strings.StayDownWhileUnusedTip, s.StayDownWhileUnused, v => s.StayDownWhileUnused = v, () => s.StayDownWhileUnused);
        var takeDown = Check(Strings.TakeDownAfterDrag, Strings.TakeDownAfterDragTip, s.TakeDownAfterDrag, v => s.TakeDownAfterDrag = v, () => s.TakeDownAfterDrag);
        stayOpen.Margin = new Thickness(0, 0, 0, RowGap);
        takeDown.Margin = new Thickness(0, 0, 0, RowGap);
        var lineToggles = new Grid();
        lineToggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lineToggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PairGap) });
        lineToggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lineToggles.Children.Add(stayOpen);
        Grid.SetColumn(takeDown, 2);
        lineToggles.Children.Add(takeDown);
        lineBody.Children.Add(lineToggles);
        lineSections.Add(lineSection);

        var (lookSection, lookBody) = Section(
            Strings.SettingsSectionAppearance,
            SettingsCategory.Line,
            SettingsGlyph.Appearance);

        var rope = StandardField(new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(9, 0, 6, 0),
            ItemTemplate = (DataTemplate)FindResource("RopeColorOptionTemplate"),
        });
        foreach (var key in Pegs.RopeColors)
            rope.Items.Add(new RopeColorOption(
                key,
                Strings.ColorName(key),
                Brush(Pegs.RopeColorOf(key)),
                Brush(Color.FromArgb(0x40, 0, 0, 0))));
        rope.SelectedIndex = Math.Max(0, Array.IndexOf(Pegs.RopeColors, s.RopeColor));
        rope.SelectionChanged += (_, _) => { if (rope.SelectedIndex >= 0) { s.RopeColor = Pegs.RopeColors[rope.SelectedIndex]; Changed(); } };
        var pegs = StandardField(new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(9, 0, 6, 0),
            ItemTemplate = (DataTemplate)FindResource("PegStyleOptionTemplate"),
        });
        foreach (var key in Pegs.Styles)
            pegs.Items.Add(new PegStyleOption(
                key,
                Strings.PegName(key),
                PegBodyBrush(key),
                PegBandBrush(key),
                Brush(Color.FromArgb(0x66, 0, 0, 0))));
        pegs.SelectedIndex = Math.Max(0, Array.IndexOf(Pegs.Styles, s.PegStyle));
        pegs.SelectionChanged += (_, _) => { if (pegs.SelectedIndex >= 0) { s.PegStyle = Pegs.Styles[pegs.SelectedIndex]; Changed(); } };
        lookBody.Children.Add(PairRow(
            FieldRow(Strings.RopeColor, rope),
            FieldRow(Strings.PegStyle, pegs)));

        lookBody.Children.Add(Row(
            Strings.LineOpacity,
            LineOpacitySwitch(),
            Strings.LineOpacityTip,
            Strings.LineOpacityHelp,
            labelWidth: WideLabelWidth));
        lineSections.Add(lookSection);

        var (cardsSection, cardsBody) = Section(
            Strings.SettingsSectionCards,
            SettingsCategory.CardsPreview,
            SettingsGlyph.StackedCards);
        cardsBody.Children.Add(PairRow(
            Row(
                Strings.MaxItemsShort,
                MaxItemsSwitch(),
                Strings.MaxItemsTip,
                Strings.MaxItemsHelp),
            Row(
                Strings.CardSortShort,
                CardSortSwitch(),
                Strings.CardSortTip,
                Strings.CardSortHelp)));
        var autoDropEnabled = Check(
            Strings.AutoDropEnabled,
            Strings.AutoDropEnabledTip,
            s.AutoDropEnabled,
            v => s.AutoDropEnabled = v,
            () => s.AutoDropEnabled,
            help: Strings.AutoDropHelp);
        autoDropEnabled.Margin = new Thickness(0);
        cardsBody.Children.Add(autoDropEnabled);
        var autoDropClock = AutoDropClockSwitch();
        var autoDropDelay = AutoDropDelaySwitch();
        _dependentControls.Add((autoDropClock, () => s.AutoDropEnabled));
        _dependentControls.Add((autoDropDelay, () => s.AutoDropEnabled));
        cardsBody.Children.Add(PairRow(
            Row(Strings.AutoDropClockShort, autoDropClock, Strings.AutoDropClockTip),
            Row(Strings.AutoDropAfterShort, autoDropDelay, Strings.AutoDropDelayTip)));
        cardSections.Add(cardsSection);

        var (clearSection, clearBody) = Section(
            Strings.SettingsSectionClearAnimation,
            SettingsCategory.CardsPreview,
            SettingsGlyph.ClearSweep);
        clearBody.Children.Add(ClearAnimationControls());
        cardSections.Add(clearSection);

        var (quickLookSection, quickLookBody) = Section(
            Strings.SettingsSectionQuickLook,
            SettingsCategory.CardsPreview,
            SettingsGlyph.PreviewEye);
        var quickLookKey = QuickLookKeyControl();
        _dependentControls.Add((quickLookKey, () =>
            Settings.QuickLookTriggerOption(s.QuickLookTrigger) == "keyboard"));
        quickLookBody.Children.Add(PairRow(
            Row(
                Strings.QuickLookSettings,
                QuickLookTriggerSwitch(),
                Strings.QuickLookTriggerTip,
                Strings.QuickLookHelp),
            Row(
                Strings.QuickLookKey,
                quickLookKey,
                Strings.QuickLookKeyTip)));
        quickLookBody.Children.Add(Row(
            Strings.QuickLookSize,
            QuickLookSizeSwitch(),
            Strings.QuickLookSizeTip));
        cardSections.Add(quickLookSection);

        // ---- Cards, capture and application ----
        var (captureSection, captureBody) = Section(
            Strings.SettingsSectionCaptureImport,
            SettingsCategory.CaptureImport,
            SettingsGlyph.CaptureImport);
        captureBody.Children.Add(Check(
            Strings.CatchClipboard,
            Strings.CatchClipboardTip,
            s.CatchClipboard,
            v => s.CatchClipboard = v,
            () => s.CatchClipboard,
            help: Strings.CatchClipboardHelp));
        captureBody.Children.Add(ClipboardTextAndDistanceRow());

        var inboxRow = new Grid { Margin = new Thickness(0, RowGap, 0, 0) };
        inboxRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inboxRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var inboxLabel = new StackPanel();
        inboxLabel.Children.Add(Text(Strings.CaughtCapturesFolder, Typography.BodySize, Typography.BodyLineHeight));
        _inbox = new TextBlock
        {
            Text = inboxPath, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Strings.IsRtl ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        _inbox.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");
        Typography.ApplyPath(_inbox);
        _inbox.ToolTip = PathTooltip(inboxPath);
        inboxLabel.Children.Add(_inbox);
        inboxRow.Children.Add(inboxLabel);

        var change = ActionButton(Strings.Change);
        change.Margin = new Thickness(10, 0, 0, 0);
        change.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.OpenInboxFolder, InitialDirectory = Inbox.Folder };
            if (dialog.ShowDialog(this) != true) return;
            var guarded = new List<string> { Shell.ScreenshotsFolder(), Shell.Desktop(),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            guarded.AddRange(s.WatchFolders.Select(Environment.ExpandEnvironmentVariables));
            if (!Inbox.IsSafeFolder(dialog.FolderName, guarded))
            {
                MessageBox.Show(this, Strings.InboxFolderRefused, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            s.InboxFolder = dialog.FolderName;
            Changed();
            _inbox.Text = Inbox.Folder;
            _inbox.ToolTip = PathTooltip(Inbox.Folder);
        };
        Grid.SetColumn(change, 1);
        inboxRow.Children.Add(change);
        captureBody.Children.Add(inboxRow);

        var foldersLabel = Text(Strings.WatchFoldersLabel, Typography.BodySize, Typography.BodyLineHeight);

        _folders = new ListBox
        {
            MinHeight = FolderMinHeight,
            Height = FolderBaseHeight,
            Margin = new Thickness(0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        VirtualizingStackPanel.SetIsVirtualizing(_folders, true);
        VirtualizingStackPanel.SetVirtualizationMode(_folders, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_folders, true);
        Typography.ApplyPath(_folders);
        _folders.ItemTemplate = TrimmedTextTemplate();
        var screenshots = new TextBlock { Text = screenshotsPath, TextTrimming = TextTrimming.CharacterEllipsis };
        screenshots.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");
        Typography.ApplyPath(screenshots);
        _folders.Items.Add(new ListBoxItem { Content = screenshots, IsEnabled = false });
        foreach (var f in s.WatchFolders) _folders.Items.Add(f);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var add = ActionButton(Strings.AddFolder);
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.AddFolderDialogTitle };
            if (dialog.ShowDialog(this) == true && !s.WatchFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                s.WatchFolders.Add(dialog.FolderName);
                _folders.Items.Add(dialog.FolderName);
                Changed();
            }
        };
        var remove = ActionButton(Strings.RemoveFolder);
        remove.Margin = new Thickness(8, 0, 0, 0);
        remove.Click += (_, _) =>
        {
            if (_folders.SelectedItem is string f)
            {
                s.WatchFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
                _folders.Items.Remove(f);
                Changed();
            }
        };
        buttons.Children.Add(add);
        buttons.Children.Add(remove);

        var foldersHeader = new Grid { Margin = new Thickness(0, RowGap, 0, RowGap) };
        foldersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foldersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foldersHeader.Children.Add(foldersLabel);
        Grid.SetColumn(buttons, 1);
        foldersHeader.Children.Add(buttons);
        captureBody.Children.Add(foldersHeader);
        captureBody.Children.Add(_folders);
        captureSections.Add(captureSection);

        var (fileSection, fileBody) = Section(
            Strings.SettingsSectionFileHanging,
            SettingsCategory.CaptureImport,
            SettingsGlyph.HangingFile);
        fileBody.Children.Add(UniversalDropRow());
        captureSections.Add(fileSection);

        var (interfaceSection, interfaceBody) = Section(
            Strings.SettingsSectionInterfaceLanguage,
            SettingsCategory.AppSettings,
            SettingsGlyph.LanguageGlobe);
        interfaceBody.Children.Add(Row(Strings.LanguageLabel, LanguageSwitch()));
        var appearance = Segmented(
            new[] { ("auto", Strings.Auto), ("light", Strings.Light), ("dark", Strings.Dark) },
            s.Appearance,
            v =>
            {
                s.Appearance = v;
                Changed();
            },
            HorizontalAlignment.Stretch,
            VerticalAlignment.Center,
            compact: true);
        interfaceBody.Children.Add(PairRow(
            Row(Strings.Appearance, appearance),
            Row(Strings.FontSize, FontSizeSwitch(), Strings.FontSizeTip)));
        interfaceBody.Children.Add(Row(
            Strings.GlassTransparency,
            GlassTransparencySwitch(),
            Strings.GlassTransparencyTip,
            Strings.GlassTransparencyHelp,
            labelWidth: WideLabelWidth));
        appSections.Add(interfaceSection);

        var (generalSection, generalBody) = Section(
            Strings.SettingsSectionStartupFeedback,
            SettingsCategory.AppSettings,
            SettingsGlyph.UpdateCheck);
        var sounds = Check(Strings.Sounds, null, s.SoundOn, v => { s.SoundOn = v; Sounds.Enabled = v; }, () => s.SoundOn);
        var startup = Check(Strings.StartWithWindows, null, Shell.StartsWithWindows, v =>
        {
            try { Shell.StartsWithWindows = v; } catch (Exception e) { Log.Error($"Could not change the startup setting: {e.Message}"); }
        }, () => Shell.StartsWithWindows);
        var feedback = new Grid();
        feedback.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        feedback.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PairGap) });
        feedback.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sounds.Margin = new Thickness(0, 0, 0, RowGap);
        startup.Margin = new Thickness(0, 0, 0, RowGap);
        feedback.Children.Add(sounds);
        Grid.SetColumn(startup, 2);
        feedback.Children.Add(startup);
        generalBody.Children.Add(feedback);
        appSections.Add(generalSection);
        appSections.Add(BuildUpdateSection());

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "SettingsHitSurface");
        _glassTint = new Border
        {
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(ShellRadius),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        root.Children.Add(_glassTint);
        var shellFrame = new Border
        {
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(ShellRadius),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        shellFrame.SetResourceReference(Border.BorderBrushProperty, "SettingsGlassBorder");
        _glassShell = shellFrame;
        root.Children.Add(shellFrame);
        ApplyGlassTint();

        var shell = new Grid();
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var categoryPages = new Dictionary<SettingsCategory, UIElement>
        {
            [SettingsCategory.Line] = SettingsPage(SettingsCategory.Line, lineSections.ToArray()),
            [SettingsCategory.CardsPreview] = SettingsPage(SettingsCategory.CardsPreview, cardSections.ToArray()),
            [SettingsCategory.CaptureImport] = SettingsPage(SettingsCategory.CaptureImport, captureSections.ToArray()),
            [SettingsCategory.AppSettings] = SettingsPage(SettingsCategory.AppSettings, appSections.ToArray()),
        };

        var sidebar = SettingsSidebar();
        shell.Children.Add(sidebar);

        var workspace = SettingsWorkspace(categoryPages, settingsPath);
        Grid.SetColumn(workspace, 1);
        shell.Children.Add(workspace);
        shellFrame.Child = shell;

        SyncDependentStates();
        _building = false;
        ShowPage(_activeCategory, moveFocus: false);
        return root;
    }

    private void ShowPage(SettingsCategory category, bool moveFocus = true)
    {
        _activeCategory = category;
        foreach (var (key, page) in _pages)
            page.Visibility = key == category ? Visibility.Visible : Visibility.Collapsed;
        if (moveFocus && _pages.TryGetValue(category, out var activePage))
        {
            // The page is the scroller, so its sections do the entrance instead
            // of fading the whole page and the sections on top of each other.
            if (activePage.Content is Panel sections) UiMotion.StaggerIn(sections);
            else UiMotion.RevealPage(activePage);
        }

        string title = CategoryTitle(category);
        string description = CategoryDescription(category);
        if (_categoryTitle is not null)
        {
            _categoryTitle.Text = title;
            _categoryTitle.ToolTip = title;
        }
        if (_categoryDescription is not null)
        {
            _categoryDescription.Text = description;
            _categoryDescription.ToolTip = description;
        }

        foreach (var (key, button) in _navButtons)
        {
            bool selected = key == category;
            if (selected)
            {
                button.SetResourceReference(BackgroundProperty, CategorySurfaceKey(category));
            }
            else
            {
                button.SetResourceReference(BackgroundProperty, "SettingsNavButtonBg");
            }
            button.SetResourceReference(ForegroundProperty, "SettingsNavText");
            // The template fades its left accent mark when the tag reads "on".
            button.Tag = selected ? "on" : null;
            // The mark follows the category colour, not the global accent, so a
            // purple page does not get a blue edge.
            if (button.Template?.FindName("AccentMark", button) is Border accentMark)
                accentMark.SetResourceReference(Border.BackgroundProperty, CategoryIconKey(category));
            if (_navIcons.TryGetValue(key, out var icon))
            {
                icon.SetResourceReference(
                    System.Windows.Shapes.Shape.StrokeProperty,
                    selected ? CategoryIconKey(category) : "SettingsNavText");
            }
        }

        if (moveFocus && _navButtons.TryGetValue(category, out var activeButton))
            activeButton.Focus();
    }

    private static string CategoryTitle(SettingsCategory category) => category switch
    {
        SettingsCategory.Line => Strings.SettingsNavLineAppearance,
        SettingsCategory.CardsPreview => Strings.SettingsNavCardsPreview,
        SettingsCategory.CaptureImport => Strings.SettingsNavCaptureImport,
        _ => Strings.SettingsNavStartupFeedback,
    };

    private static string CategoryDescription(SettingsCategory category) => category switch
    {
        SettingsCategory.Line => Strings.SettingsCategoryLineAppearanceDescription,
        SettingsCategory.CardsPreview => Strings.SettingsCategoryCardsPreviewDescription,
        SettingsCategory.CaptureImport => Strings.SettingsCategoryCaptureImportDescription,
        _ => Strings.SettingsCategoryStartupFeedbackDescription,
    };

    private static string CategorySurfaceKey(SettingsCategory category) => category switch
    {
        SettingsCategory.Line => "SettingsNavLineBg",
        SettingsCategory.CardsPreview => "SettingsNavCardsBg",
        SettingsCategory.CaptureImport => "SettingsNavCaptureBg",
        _ => "SettingsNavStartupBg",
    };

    private static string CategoryIconKey(SettingsCategory category) => category switch
    {
        SettingsCategory.Line => "SettingsNavLineIcon",
        SettingsCategory.CardsPreview => "SettingsNavCardsIcon",
        SettingsCategory.CaptureImport => "SettingsNavCaptureIcon",
        _ => "SettingsNavStartupIcon",
    };

    private static SettingsGlyph CategoryGlyph(SettingsCategory category) => category switch
    {
        SettingsCategory.Line => SettingsGlyph.HangingRail,
        SettingsCategory.CardsPreview => SettingsGlyph.StackedCards,
        SettingsCategory.CaptureImport => SettingsGlyph.CaptureImport,
        _ => SettingsGlyph.AppSettings,
    };

    private static Morphicon Glyph(SettingsGlyph glyph) => glyph switch
    {
        SettingsGlyph.HangingRail => Morphicon.PanelTop,
        SettingsGlyph.StackedCards => Morphicon.Layers,
        SettingsGlyph.CaptureImport => Morphicon.Camera,
        SettingsGlyph.PowerFeedback => Morphicon.Power,
        SettingsGlyph.UpdateCheck => Morphicon.RefreshCw,
        SettingsGlyph.AppSettings => Morphicon.Settings,
        SettingsGlyph.Appearance => Morphicon.SlidersHorizontal,
        SettingsGlyph.ClearSweep => Morphicon.Eraser,
        SettingsGlyph.PreviewEye => Morphicon.Eye,
        SettingsGlyph.HangingFile => Morphicon.FileText,
        SettingsGlyph.LanguageGlobe => Morphicon.Globe,
        _ => throw new ArgumentOutOfRangeException(nameof(glyph)),
    };

    private static System.Windows.Shapes.Path GlyphPath(
        SettingsGlyph glyph,
        double size,
        double strokeThickness,
        string strokeResource)
    {
        var icon = Morphicons.CreatePath(Glyph(glyph), size, strokeThickness, strokeResource);
        icon.SnapsToDevicePixels = true;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    /// <summary>
    /// "Capture clipboard text" and the card distance share one row and use the
    /// same two-column geometry as every other paired setting, so the distance
    /// label starts exactly under "shortcut" and "retract delay" on the line page.
    /// </summary>
    private FrameworkElement ClipboardTextAndDistanceRow()
    {
        var settings = Settings.Current;
        var capture = Check(
            Strings.CatchTextClipboard,
            Strings.CatchTextClipboardTip,
            settings.CatchTextClipboard,
            v => settings.CatchTextClipboard = v,
            () => settings.CatchTextClipboard,
            help: Strings.CatchTextClipboardHelp);
        capture.MaxWidth = 280;
        capture.MinHeight = RowMinHeight;
        capture.HorizontalAlignment = HorizontalAlignment.Left;
        capture.VerticalAlignment = VerticalAlignment.Center;

        var left = new Grid { MinHeight = RowMinHeight };
        left.Children.Add(capture);

        var distance = Row(
            Strings.ClipboardEdgeDistancePair,
            ClipboardEdgeDistanceControl(),
            Strings.ClipboardEdgeDistanceTip);
        return PairRow(left, distance);
    }

    private FrameworkElement ClipboardEdgeDistanceControl()
    {
        var settings = Settings.Current;
        double initial = Settings.ClipboardEdgeDistanceOption(settings.ClipboardEdgeDistance);
        var value = new TextBlock
        {
            Text = $"{initial:0} DIP",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        Typography.ApplyText(value);
        value.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");

        var valueHost = new Grid
        {
            Width = 58,
            Height = ControlHeight,
            Margin = new Thickness(8, 0, 0, 0),
            Children = { value },
        };
        var slider = new Slider
        {
            Minimum = Settings.MinClipboardEdgeDistance,
            Maximum = Settings.MaxClipboardEdgeDistance,
            Value = initial,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 10,
            Height = ControlHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = Strings.ClipboardEdgeDistanceTip,
        };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSlider");
        AutomationProperties.SetName(slider, Strings.ClipboardEdgeDistanceShort);
        slider.ValueChanged += (_, e) =>
        {
            double distance = Settings.ClipboardEdgeDistanceOption(e.NewValue);
            settings.ClipboardEdgeDistance = distance;
            value.Text = $"{distance:0} DIP";
            // Live feedback, but save once the drag settles instead of
            // rewriting the whole settings file on every slider tick.
            _apply();
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        };

        var control = new Grid { Height = ControlHeight };
        control.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        control.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        control.Children.Add(slider);
        Grid.SetColumn(valueHost, 1);
        control.Children.Add(valueHost);
        control.ToolTip = Strings.ClipboardEdgeDistanceTip;
        AutomationProperties.SetName(control, Strings.ClipboardEdgeDistanceShort);
        return control;
    }

    private FrameworkElement SettingsPage(SettingsCategory category, params UIElement[] sections)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        for (int index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            if (index == sections.Length - 1 && section is FrameworkElement last)
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            content.Children.Add(section);
        }

        var scroll = new ScrollViewer
        {
            Content = content,
            // Reserve the 10 DIP scrollbar on every page. With Auto, pages that
            // happen to be tall enough get a narrower viewport than short pages,
            // which shifts every two-column row and breaks label alignment
            // between categories. The theme's scrollbar track is transparent,
            // so short pages only gain an invisible gutter.
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            Visibility = category == _activeCategory ? Visibility.Visible : Visibility.Collapsed,
        };
        SmoothScroll.Attach(scroll);
        _pages[category] = scroll;
        return scroll;
    }

    private FrameworkElement SettingsWorkspace(
        IReadOnlyDictionary<SettingsCategory, UIElement> pages,
        string settingsPath)
    {
        var workspace = new Grid
        {
            Margin = new Thickness(8, 4, 4, 4),
        };
        var panel = new Border
        {
            CornerRadius = new CornerRadius(ShellRadius),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        panel.SetResourceReference(Border.BackgroundProperty, "SettingsWorkspaceBg");
        panel.SetResourceReference(Border.BorderBrushProperty, "SettingsWorkspaceBorder");
        workspace.Children.Add(panel);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(20, 10, 12, 7) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, SettingsContentTopOffset, 0, 0),
        };
        _categoryTitle = Text(CategoryTitle(_activeCategory), Typography.WindowTitleSize, Typography.WindowTitleLineHeight, FontWeights.SemiBold);
        AutomationProperties.SetName(_categoryTitle, "SettingsCategoryTitle");
        _categoryTitle.TextWrapping = TextWrapping.NoWrap;
        heading.Children.Add(_categoryTitle);
        _categoryDescription = Text(
            CategoryDescription(_activeCategory),
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        _categoryDescription.Margin = new Thickness(0, HelperGap, 0, 0);
        _categoryDescription.TextWrapping = TextWrapping.NoWrap;
        _categoryDescription.TextTrimming = TextTrimming.CharacterEllipsis;
        _categoryDescription.ToolTip = CategoryDescription(_activeCategory);
        heading.Children.Add(_categoryDescription);
        header.Children.Add(heading);

        var windowControls = WindowControls();
        windowControls.VerticalAlignment = VerticalAlignment.Top;
        windowControls.Margin = new Thickness(18, 2, 0, 0);
        Grid.SetColumn(windowControls, 1);
        header.Children.Add(windowControls);
        layout.Children.Add(header);

        var pageHost = new Grid { Margin = new Thickness(20, 0, 12, 0) };
        foreach (var page in pages.Values) pageHost.Children.Add(page);
        Grid.SetRow(pageHost, 1);
        layout.Children.Add(pageHost);

        var status = new Grid
        {
            MinHeight = 28,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var statusContent = new Grid { Margin = new Thickness(20, 2, 14, 4) };
        var footer = Footer(Strings.SettingsFooter, settingsPath);
        footer.VerticalAlignment = VerticalAlignment.Center;
        footer.Margin = new Thickness(0);
        statusContent.Children.Add(footer);
        status.Children.Add(statusContent);
        Grid.SetRow(status, 2);
        layout.Children.Add(status);

        panel.Child = layout;
        return workspace;
    }

    private FrameworkElement WindowControls()
    {
        var group = new Grid
        {
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var minimize = WindowControlButton(
            Strings.MinimizeWindow,
            Morphicon.Minus,
            close: false,
            () => WindowState = WindowState.Minimized);
        buttons.Children.Add(minimize);

        var close = WindowControlButton(
            Strings.CloseWindow,
            Morphicon.X,
            close: true,
            Close);
        buttons.Children.Add(close);

        group.Children.Add(buttons);
        return group;
    }

    private Button WindowControlButton(
        string label,
        Morphicon icon,
        bool close,
        Action action)
    {
        var glyph = Morphicons.CreatePath(icon, 10, 1.1, Brushes.Transparent);
        glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });

        var button = new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = true,
            IsTabStop = true,
            ToolTip = label,
        };
        button.SetResourceReference(StyleProperty, close ? "SettingsWindowCloseButton" : "SettingsWindowButton");
        AutomationProperties.SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }

    private FrameworkElement SettingsSidebar()
    {
        var sidebar = new Grid
        {
            Margin = new Thickness(18, 20, 10, 12),
        };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var brand = new StackPanel { Margin = new Thickness(2, 0, 4, 18) };
        var brandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var markTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        markTile.SetResourceReference(Border.BackgroundProperty, "SettingsBrandTileBg");
        markTile.SetResourceReference(Border.BorderBrushProperty, "SettingsBrandTileBorder");
        AutomationProperties.SetName(markTile, "SettingsBrandMark");
        if (Brand.Mark is { } mark)
        {
            markTile.Child = new Image
            {
                Source = CropBrandMark(mark),
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var fallback = Text("H", 18, 22, FontWeights.Bold);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            markTile.Child = fallback;
        }
        brandRow.Children.Add(markTile);

        var wordmark = new TextBlock
        {
            Text = Strings.ProductName,
            FontFamily = Brand.Wordmark,
            FontWeight = Brand.WordmarkWeight,
            FontSize = 21,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, -1, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
        };
        wordmark.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
        brandRow.Children.Add(wordmark);
        brand.Children.Add(brandRow);
        sidebar.Children.Add(brand);

        var nav = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var category in Enum.GetValues<SettingsCategory>())
            nav.Children.Add(NavButton(category));
        Grid.SetRow(nav, 1);
        sidebar.Children.Add(nav);

        var lower = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        lower.Children.Add(ContactCard());

        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        var versionLine = Text(
            $"{Strings.Version} {version}",
            Typography.CaptionSize,
            Typography.CaptionLineHeight,
            foregroundResource: "SettingsTextTertiary");
        versionLine.HorizontalAlignment = HorizontalAlignment.Center;
        versionLine.Margin = new Thickness(0, 6, 0, 0);
        lower.Children.Add(versionLine);
        Grid.SetRow(lower, 2);
        sidebar.Children.Add(lower);

        return sidebar;
    }

    private Button NavButton(SettingsCategory category)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlotSize) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavIconGap) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconSlot = new Grid
        {
            Width = IconSlotSize,
            Height = IconSlotSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = GlyphPath(CategoryGlyph(category), 18, 1.5, "SettingsNavText");
        iconSlot.Children.Add(icon);
        content.Children.Add(iconSlot);
        var label = Text(
            CategoryTitle(category),
            Typography.BodySize,
            Typography.BodyLineHeight,
            FontWeights.Medium,
            "SettingsNavText");
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 2);
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Height = 46,
            MinHeight = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(NavPaddingLeft, 0, NavPaddingRight, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = CategoryDescription(category),
        };
        button.SetResourceReference(StyleProperty, "SettingsNavButton");
        button.SetResourceReference(BackgroundProperty, "SettingsNavButtonBg");
        button.SetResourceReference(ForegroundProperty, "SettingsNavText");
        button.Click += (_, _) => ShowPage(category);
        _navButtons[category] = button;
        _navIcons[category] = icon;
        return button;
    }

    /// <summary>
    /// The source mark has a large transparent canvas. Crop that canvas so the
    /// visible glyph can occupy the brand tile instead of looking undersized.
    /// </summary>
    private static BitmapSource? CropBrandMark(BitmapSource source)
    {
        const double left = 38d / 1024d;
        const double top = 219d / 1024d;
        const double right = 985d / 1024d;
        const double bottom = 840d / 1024d;

        int x = Math.Clamp((int)Math.Round(source.PixelWidth * left), 0, source.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(source.PixelHeight * top), 0, source.PixelHeight - 1);
        int rightEdge = Math.Clamp((int)Math.Ceiling(source.PixelWidth * right), x + 1, source.PixelWidth);
        int bottomEdge = Math.Clamp((int)Math.Ceiling(source.PixelHeight * bottom), y + 1, source.PixelHeight);
        try
        {
            var crop = new CroppedBitmap(source, new Int32Rect(x, y, rightEdge - x, bottomEdge - y));
            crop.Freeze();
            return crop;
        }
        catch (ArgumentException)
        {
            return source;
        }
    }

    private FrameworkElement ContactCard()
    {
        var avatar = AuthorAvatar(ContactAvatarSize);

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        content.Children.Add(avatar);

        var authorName = Text(
            Strings.AuthorName,
            Typography.BodySize,
            Typography.BodyLineHeight,
            FontWeights.SemiBold);
        authorName.HorizontalAlignment = HorizontalAlignment.Center;
        authorName.Margin = new Thickness(0, 4, 0, 0);
        content.Children.Add(authorName);

        var handle = Text(
            AuthorHandle,
            Typography.SmallSize,
            Typography.SmallLineHeight,
            foregroundResource: "SettingsTextSecondary");
        handle.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(handle);

        var githubContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var githubIcon = AuthorLinks.IconPath(AuthorLinks.GitHubGlyph, false, 14);
        githubIcon.Margin = new Thickness(0, 0, 7, 0);
        githubContent.Children.Add(githubIcon);
        var githubLabel = Text(
            Strings.VisitHomepage,
            Typography.SmallSize,
            Typography.SmallLineHeight);
        githubLabel.VerticalAlignment = VerticalAlignment.Center;
        githubContent.Children.Add(githubLabel);

        var github = ActionButton(githubContent, Strings.HomepageToolTip);
        github.Width = ContactGithubWidth;
        github.MinWidth = ContactGithubWidth;
        github.Height = ControlHeight;
        github.MinHeight = ControlHeight;
        github.Margin = new Thickness(0, 8, 0, 0);
        github.HorizontalAlignment = HorizontalAlignment.Center;
        github.HorizontalContentAlignment = HorizontalAlignment.Center;
        github.VerticalContentAlignment = VerticalAlignment.Center;
        github.Padding = new Thickness(10, 0, 10, 0);
        github.Opacity = 0;
        github.IsHitTestVisible = false;
        AutomationProperties.SetName(github, Strings.HomepageToolTip);
        github.Click += (_, _) => AuthorLinks.OpenHomepage(this);
        var githubOffset = new TranslateTransform(0, 12);
        github.RenderTransform = githubOffset;
        content.Children.Add(github);

        var viewport = new Grid
        {
            Margin = new Thickness(10),
            ClipToBounds = true,
        };
        viewport.Children.Add(content);

        var shadow = new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.08,
            Color = Colors.Black,
        };
        var surface = new Border
        {
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(0),
            Child = viewport,
            Effect = shadow,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SettingsContactCardBg");

        double compactHeight = Math.Ceiling(
            ContactAvatarSize +
            4 +
            Typography.BodyLineHeight +
            Typography.SmallLineHeight +
            21);
        double expandedHeight = Math.Ceiling(
            ContactAvatarSize +
            4 +
            Typography.BodyLineHeight +
            Typography.SmallLineHeight +
            8 +
            ControlHeight +
            23);

        var card = new Grid
        {
            Width = ContactCardWidth,
            Height = compactHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
        };
        card.Children.Add(surface);

        bool expanded = false;
        bool pointerInside = false;
        void SetExpanded(bool value)
        {
            if (expanded == value) return;
            expanded = value;
            github.IsHitTestVisible = value;
            UiMotion.Scale(avatar, value ? 1.05 : 1, 320);

            double fromHeight = card.ActualHeight > 0 ? card.ActualHeight : card.Height;
            double fromOpacity = github.Opacity;
            double fromOffset = githubOffset.Y;
            double fromShadow = shadow.Opacity;
            double targetHeight = value ? expandedHeight : compactHeight;
            double targetOffset = value ? 0 : 12;
            double targetShadow = value ? 0.16 : 0.08;

            card.BeginAnimation(FrameworkElement.HeightProperty, null);
            github.BeginAnimation(UIElement.OpacityProperty, null);
            githubOffset.BeginAnimation(TranslateTransform.YProperty, null);
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);

            card.Height = targetHeight;
            github.Opacity = value ? 1 : 0;
            githubOffset.Y = targetOffset;
            shadow.Opacity = targetShadow;

            if (!SystemParameters.ClientAreaAnimation) return;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(
                FrameworkElement.HeightProperty,
                new DoubleAnimation(fromHeight, targetHeight, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                },
                HandoffBehavior.SnapshotAndReplace);
            github.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(fromOpacity, value ? 1 : 0, TimeSpan.FromMilliseconds(260))
                {
                    BeginTime = TimeSpan.FromMilliseconds(value ? 90 : 0),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                },
                HandoffBehavior.SnapshotAndReplace);
            githubOffset.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(fromOffset, targetOffset, TimeSpan.FromMilliseconds(320))
                {
                    BeginTime = TimeSpan.FromMilliseconds(value ? 90 : 0),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                },
                HandoffBehavior.SnapshotAndReplace);
            shadow.BeginAnimation(
                DropShadowEffect.OpacityProperty,
                new DoubleAnimation(fromShadow, targetShadow, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        card.MouseEnter += (_, _) =>
        {
            pointerInside = true;
            SetExpanded(true);
        };
        card.MouseLeave += (_, _) =>
        {
            pointerInside = false;
            if (!card.IsKeyboardFocusWithin) SetExpanded(false);
        };
        card.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (card.IsKeyboardFocusWithin || pointerInside) SetExpanded(true);
            else SetExpanded(false);
        };
        return card;
    }

    private static FrameworkElement AuthorAvatar(double size)
    {
        var avatarBox = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        BitmapSource? avatarSource = null;
        try
        {
            string assemblyName = typeof(SettingsWindow).Assembly.GetName().Name ?? "HangAll";
            var avatarUri = new Uri(
                $"pack://application:,,,/{assemblyName};component/Assets/AuthorWatson.jpg",
                UriKind.Absolute);
            var info = Application.GetResourceStream(avatarUri);
            if (info is not null)
            {
                avatarSource = BitmapDecoder.Create(info.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                avatarSource.Freeze();
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not load the author avatar: {e.Message}");
        }

        var avatar = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.5,
            Fill = avatarSource is null
                ? Theme.Freeze(new SolidColorBrush(Theme.Primary))
                : new ImageBrush(avatarSource)
                {
                    Viewbox = new Rect(0, 0, 1, 1),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                },
        };
        avatar.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SettingsAvatarBorder");
        RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.HighQuality);
        avatarBox.Children.Add(avatar);
        if (avatarSource is null)
        {
            avatarBox.Children.Add(new TextBlock
            {
                Text = "W",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return avatarBox;
    }

    /// <summary>The language chooser uses an evenly divided segmented control.</summary>
    private FrameworkElement LanguageSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("auto", Strings.Automatic),
            ("zh", "简体中文"),
            ("zh-Hant", "繁體中文"),
            ("en", "English"),
            ("ja", "日本語"),
            ("ko", "한국어"),
        };
        return Segmented(
            options,
            s.Language,
            key =>
            {
                s.Language = key;
                Changed();
            },
            HorizontalAlignment.Stretch,
            VerticalAlignment.Center,
            compact: true,
            selectedButton: button => _languageButton = button);
    }

    /// <summary>The three UI scales: each change is saved and the page is rebuilt at the new type size.</summary>
    private FrameworkElement FontSizeSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("small", Strings.FontSmall),
            ("normal", Strings.FontNormal),
            ("large", Strings.FontLarge),
        };
        string active = Settings.FontScaleOption(s.FontScale) switch
        {
            <= 0.95 => "small",
            >= 1.05 => "large",
            _ => "normal",
        };
        return Segmented(options, active, key =>
        {
            s.FontScale = key switch
            {
                "small" => 0.9,
                "large" => 1.1,
                _ => 1.0,
            };
            Changed();
        }, HorizontalAlignment.Stretch, VerticalAlignment.Center, compact: true, selectedButton: button => _fontSizeButton = button);
    }

    /// <summary>The top-edge trigger sensitivity: low, standard or high.</summary>
    private FrameworkElement RevealSensitivitySwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("low", Strings.RevealLow),
            ("standard", Strings.SegmentStandard),
            ("high", Strings.RevealHigh),
        };
        string active = Settings.RevealDelayOption(s.RevealDelay) switch
        {
            >= 0.75 => "low",
            <= 0.35 => "high",
            _ => "standard",
        };
        return Segmented(options, active, key =>
        {
            s.RevealDelay = key switch
            {
                "low" => 1.0,
                "high" => 0.25,
                _ => 0.5,
            };
            Changed();
        }, HorizontalAlignment.Stretch, VerticalAlignment.Center, compact: true);
    }

    /// <summary>Whether hovering waits for a key or opens Quick Look automatically.</summary>
    private FrameworkElement QuickLookTriggerSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("keyboard", Strings.QuickLookHoverKey),
            ("auto", Strings.QuickLookAutoPreview),
        };
        return Segmented(
            options,
            Settings.QuickLookTriggerOption(s.QuickLookTrigger),
            key =>
            {
                s.QuickLookTrigger = Settings.QuickLookTriggerOption(key);
                Changed();
                SyncDependentStates();
            },
            HorizontalAlignment.Stretch,
            VerticalAlignment.Center,
            compact: true);
    }

    /// <summary>
    /// The maximum number of live cards. Null fits the screen, -1 keeps every
    /// card on the same overlapping row, a number is an exact cap.
    /// </summary>
    private FrameworkElement MaxItemsSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("auto", Strings.Automatic),
            ("6", "6"),
            ("8", "8"),
            ("10", "10"),
            ("12", "12"),
            ("-1", "∞"),
        };
        string active = s.MaxItems is { } max
            ? Settings.MaxItemsOption(max)?.ToString() ?? "auto"
            : "auto";
        return Choice(options, active, key =>
        {
            s.MaxItems = key == "auto" ? null : int.Parse(key);
        });
    }

    /// <summary>Manual keeps the stored order; time and type arrange the line for you.</summary>
    private FrameworkElement CardSortSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("manual", Strings.SortManualShort),
            ("time", Strings.SortTimeShort),
            ("type", Strings.SortTypeShort),
        };
        var active = Settings.CardSortOption(s.CardSort);
        return Segmented(options, active, key =>
        {
            s.CardSort = Settings.CardSortOption(key);
            Changed();
            SyncDependentStates();
        }, HorizontalAlignment.Stretch, VerticalAlignment.Center, compact: true);
    }

    /// <summary>How long the line waits after the pointer leaves before tucking away.</summary>
    private FrameworkElement RetractDelaySwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("0.2", $"0.2 {Strings.SecondsShort}"),
            ("0.5", $"0.5 {Strings.SecondsShort}"),
            ("1", $"1 {Strings.SecondsShort}"),
            ("2", $"2 {Strings.SecondsShort}"),
        };
        string active = Settings.RetractDelayOption(s.RetractDelay).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        return Choice(options, active, key =>
        {
            s.RetractDelay = double.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Whether automatic take-down counts from hanging or from the last use.</summary>
    private FrameworkElement AutoDropClockSwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("hung", Strings.AutoDropHungShort),
            ("unused", Strings.AutoDropUnusedShort),
        };
        var active = Settings.AutoDropModeOption(s.AutoDropMode);
        return Segmented(options, active, key =>
        {
            s.AutoDropMode = Settings.AutoDropModeOption(key);
            Changed();
        }, HorizontalAlignment.Stretch, VerticalAlignment.Center, compact: true);
    }

    /// <summary>How long a card may stay before automatic take-down removes it.</summary>
    private FrameworkElement AutoDropDelaySwitch()
    {
        var s = Settings.Current;
        var options = new (string key, string label)[]
        {
            ("5", $"5 {Strings.MinutesShort}"),
            ("15", $"15 {Strings.MinutesShort}"),
            ("30", $"30 {Strings.MinutesShort}"),
            ("60", $"60 {Strings.MinutesShort}"),
        };
        string active = Settings.AutoDropMinutesOption(s.AutoDropMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Choice(options, active, key =>
        {
            s.AutoDropMinutes = Settings.AutoDropMinutesOption(int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
        });
    }

    /// <summary>The opacity applied to the whole line window: rope, pegs and cards.</summary>
    private FrameworkElement LineOpacitySwitch()
    {
        var s = Settings.Current;
        static double DisplayValue(double lineOpacity) =>
            Math.Round((1 - Math.Clamp(lineOpacity, 0, 1)) * 100);

        var percent = new TextBlock
        {
            Text = $"{DisplayValue(s.LineOpacity):0}%",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        Typography.ApplyText(percent);
        var percentHost = new Grid
        {
            Width = 46,
            Height = ControlHeight,
            Margin = new Thickness(8, 0, 0, 0),
        };
        percentHost.Children.Add(percent);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = DisplayValue(s.LineOpacity),
            IsSnapToTickEnabled = false,
            Height = ControlHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SmallChange = 1,
            LargeChange = 10,
        };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSlider");
        slider.ValueChanged += (_, e) =>
        {
            s.LineOpacity = Math.Clamp(1 - e.NewValue / 100.0, 0, 1);
            percent.Text = $"{Math.Round(e.NewValue):0}%";
            _apply();
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        };
        var row = new Grid { Height = ControlHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(percentHost, 1);
        row.Children.Add(percentHost);
        return row;
    }

    /// <summary>The image scale used by Quick Look, applied as the slider moves.</summary>
    private FrameworkElement QuickLookSizeSwitch()
    {
        var s = Settings.Current;
        double initial = Math.Round(Settings.QuickLookScaleOption(s.QuickLookScale) * 100);
        var percent = new TextBlock
        {
            Text = $"{initial:0}%",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        Typography.ApplyText(percent);
        var percentHost = new Grid
        {
            Width = 46,
            Height = ControlHeight,
            Margin = new Thickness(8, 0, 0, 0),
        };
        percentHost.Children.Add(percent);
        var slider = new Slider
        {
            Minimum = 50,
            Maximum = 300,
            Value = initial,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Height = ControlHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SmallChange = 5,
            LargeChange = 25,
        };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSlider");
        slider.ValueChanged += (_, e) =>
        {
            s.QuickLookScale = Settings.QuickLookScaleOption(e.NewValue / 100.0);
            percent.Text = $"{Math.Round(s.QuickLookScale * 100):0}%";
            _apply();
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        };
        var row = new Grid { Height = ControlHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(percentHost, 1);
        row.Children.Add(percentHost);
        return row;
    }

    /// <summary>Controls how much of the desktop remains visible through the Settings glass.</summary>
    private FrameworkElement GlassTransparencySwitch()
    {
        var s = Settings.Current;
        double initial = Math.Round(Settings.GlassTransparencyOption(s.GlassTransparency) * 100);
        var percent = new TextBlock
        {
            Text = $"{initial:0}%",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        Typography.ApplyText(percent);
        var percentHost = new Grid
        {
            Width = 46,
            Height = ControlHeight,
            Margin = new Thickness(8, 0, 0, 0),
        };
        percentHost.Children.Add(percent);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initial,
            IsSnapToTickEnabled = false,
            Height = ControlHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SmallChange = 1,
            LargeChange = 10,
        };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSlider");
        AutomationProperties.SetName(slider, Strings.GlassTransparency);
        slider.ValueChanged += (_, e) =>
        {
            s.GlassTransparency = Settings.GlassTransparencyOption(e.NewValue / 100.0);
            percent.Text = $"{Math.Round(e.NewValue):0}%";
            ApplyGlassTint();
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        };
        var row = new Grid { Height = ControlHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(percentHost, 1);
        row.Children.Add(percentHost);
        return row;
    }

    private static Button ActionButton(object content, string? tip = null)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SettingsActionButton");
        return button;
    }

    private static Button ResetDefaultButton(string? tip, Action reset)
    {
        var button = ActionButton(Strings.RestoreDefaultShort, tip);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Click += (_, _) => reset();
        return button;
    }

    private FrameworkElement ClearAnimationControls()
    {
        var s = Settings.Current;
        var current = new TextBlock
        {
            Text = ClearAnimationName(s.ClearAnimationPath),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        Typography.ApplyText(current);
        current.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextPrimary");

        var choose = ActionButton(current, Strings.ClearAnimationTip);
        choose.Height = ControlHeight;
        choose.MinHeight = ControlHeight;
        choose.MinWidth = 0;
        choose.Padding = new Thickness(9, 0, 8, 0);
        choose.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        choose.VerticalContentAlignment = VerticalAlignment.Center;

        void RefreshName()
        {
            current.Text = ClearAnimationName(s.ClearAnimationPath);
            choose.ToolTip = s.ClearAnimationPath ?? Strings.BuiltInAnimationName;
        }
        RefreshName();

        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.ChangeAnimation,
                Filter = "GIF (*.gif)|*.gif",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var destination = Path.Combine(Settings.Folder, "clear-animation.gif");
                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    File.Copy(dialog.FileName, destination, overwrite: true);
                s.ClearAnimationPath = destination;
                RefreshName();
                Changed();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not copy the clear animation: {ex.Message}");
                MessageBox.Show(this, Strings.ClearAnimationCopyFailed, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };

        var restore = ActionButton(Strings.RestoreDefaultAnimation, Strings.RestoreDefaultAnimation);
        restore.Margin = new Thickness(8, 0, 0, 0);
        restore.HorizontalContentAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(restore, Strings.RestoreDefaultAnimation);
        restore.Click += (_, _) =>
        {
            s.ClearAnimationPath = null;
            RefreshName();
            Changed();
        };
        var clearCheck = Check(
            Strings.ClearLineShort,
            Strings.OneClickClearTip,
            s.OneClickClear,
            v => s.OneClickClear = v,
            () => s.OneClickClear);
        clearCheck.Margin = new Thickness(0);
        AutomationProperties.SetName(clearCheck, Strings.ClearLineShort);

        var mirrored = Check(
            Strings.ClearAnimationMirrorShort,
            Strings.ClearAnimationMirrorTip,
            s.ClearAnimationMirrored,
            v => s.ClearAnimationMirrored = v,
            () => s.ClearAnimationMirrored,
            () => s.OneClickClear);
        mirrored.Margin = new Thickness(0);
        AutomationProperties.SetName(mirrored, Strings.ClearAnimationMirrorShort);

        var placement = Segmented(
            new[]
            {
                ("left", Strings.ClearAnimationLeft),
                ("right", Strings.ClearAnimationRight),
            },
            Settings.ClearAnimationSideOption(s.ClearAnimationSide),
            key =>
            {
                s.ClearAnimationSide = Settings.ClearAnimationSideOption(key);
                Changed();
            },
            HorizontalAlignment.Stretch,
            VerticalAlignment.Center,
            compact: true);
        placement.ToolTip = Strings.ClearAnimationPlacementTip;
        AutomationProperties.SetName(placement, Strings.ClearAnimationPlacementShort);
        _dependentControls.Add((placement, () => s.OneClickClear));

        double initialOffset = Settings.ClearAnimationOffsetOption(s.ClearAnimationOffset);
        var offsetValue = new TextBlock
        {
            Text = $"{initialOffset:0} DIP",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typography.ApplyText(offsetValue);
        offsetValue.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");
        var offsetValueHost = new Grid
        {
            Width = 58,
            Height = ControlHeight,
            Margin = new Thickness(8, 0, 0, 0),
        };
        offsetValueHost.Children.Add(offsetValue);

        var offset = new Slider
        {
            Minimum = Settings.MinClearAnimationOffset,
            Maximum = Settings.MaxClearAnimationOffset,
            Value = initialOffset,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 10,
            Height = ControlHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = Strings.ClearAnimationOffsetTip,
        };
        offset.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSlider");
        AutomationProperties.SetName(offset, Strings.ClearAnimationOffsetShort);
        offset.ValueChanged += (_, e) =>
        {
            double value = Settings.ClearAnimationOffsetOption(e.NewValue);
            s.ClearAnimationOffset = value;
            offsetValue.Text = $"{value:0} DIP";
            _apply();
            _liveSaveTimer.Stop();
            _liveSaveTimer.Start();
        };
        var offsetControl = new Grid { Height = ControlHeight };
        offsetControl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offsetControl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offsetControl.Children.Add(offset);
        Grid.SetColumn(offsetValueHost, 1);
        offsetControl.Children.Add(offsetValueHost);
        offsetControl.ToolTip = Strings.ClearAnimationOffsetTip;
        AutomationProperties.SetName(offsetControl, Strings.ClearAnimationOffsetShort);
        _dependentControls.Add((offsetControl, () => s.OneClickClear));

        var picker = new Grid { Height = ControlHeight };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        picker.Children.Add(choose);
        Grid.SetColumn(restore, 1);
        picker.Children.Add(restore);
        _dependentControls.Add((picker, () => s.OneClickClear));

        var size = Segmented(
            new[]
            {
                ("32", Strings.AnimationSmall),
                ("48", Strings.AnimationStandard),
                ("64", Strings.AnimationLarge),
            },
            Settings.ClearAnimationSizeOption(s.ClearAnimationSize).ToString("0"),
            key =>
            {
                s.ClearAnimationSize = double.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                Changed();
            },
            HorizontalAlignment.Stretch,
            VerticalAlignment.Center,
            compact: true);
        _dependentControls.Add((size, () => s.OneClickClear));

        var group = new StackPanel();
        group.Children.Add(PairRow(
            new Grid { Children = { clearCheck } },
            new Grid { Children = { mirrored } }));
        group.Children.Add(PairRow(
            Row(Strings.ClearAnimationPlacementShort, placement, Strings.ClearAnimationPlacementTip),
            Row(Strings.ClearAnimationOffsetShort, offsetControl, Strings.ClearAnimationOffsetTip)));
        group.Children.Add(PairRow(
            Row(Strings.ClearAnimation, picker, Strings.ClearAnimationTip),
            Row(Strings.AnimationSizeShort, size)));
        return group;
    }

    private static string ClearAnimationName(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Strings.BuiltInAnimationName
            : Path.GetFileName(path);

    /// <summary>The master switch owns the first row; its two methods share a balanced second row.</summary>
    private FrameworkElement UniversalDropRow()
    {
        var s = Settings.Current;
        var parent = Check(Strings.HangAllShort, Strings.UniversalDropTip, s.UniversalDrop,
            v => s.UniversalDrop = v, () => s.UniversalDrop);
        parent.Margin = new Thickness(0);
        var zone = Check(Strings.DropZoneHangShort, Strings.DropZoneHangTip, s.DropZoneHang,
            v => s.DropZoneHang = v, () => s.DropZoneHang, () => s.UniversalDrop, child: true);
        var clipboard = Check(Strings.ClipboardAutoHangShort, Strings.ClipboardAutoHangTip, s.ClipboardAutoHang,
            v => s.ClipboardAutoHang = v, () => s.ClipboardAutoHang, () => s.UniversalDrop, child: true);
        zone.Margin = new Thickness(0);
        clipboard.Margin = new Thickness(0);

        var methods = new Grid { Margin = new Thickness(22, RowGap, 0, 0) };
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PairGap) });
        methods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        zone.Margin = new Thickness(0);
        Grid.SetColumn(zone, 0);
        Grid.SetColumn(clipboard, 2);
        methods.Children.Add(zone);
        methods.Children.Add(clipboard);

        var row = new Grid { Margin = new Thickness(0, 0, 0, RowGap) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(parent);
        Grid.SetRow(methods, 1);
        row.Children.Add(methods);
        return row;
    }

    /// <summary>One segmented control, shared by language, type scale and edge sensitivity.</summary>
    private FrameworkElement Segmented(
        (string key, string label)[] options,
        string selected,
        Action<string> select,
        HorizontalAlignment alignment,
        VerticalAlignment verticalAlignment,
        bool compact = false,
        Action<Button>? selectedButton = null,
        bool adaptive = false,
        TextAlignment textAlignment = TextAlignment.Center)
    {
        var track = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        var indicator = new Border
        {
            Height = ControlHeight - 4,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(ControlRadius - 3),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        indicator.SetResourceReference(BackgroundProperty, "AccentFill");
        // The pill sits outside the button templates, so it needs its own dimming:
        // a full-strength accent under a greyed label reads as "still switchable".
        indicator.Style = new Style(typeof(Border))
        {
            Triggers =
            {
                new Trigger
                {
                    Property = IsEnabledProperty,
                    Value = false,
                    Setters = { new Setter(OpacityProperty, 0.45) },
                },
            },
        };
        track.Children.Add(indicator);

        Panel row;
        if (adaptive)
        {
            row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
        }
        else
        {
            row = new System.Windows.Controls.Primitives.UniformGrid
            {
                Rows = 1,
                Columns = options.Length,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FlowDirection = FlowDirection.LeftToRight,
            };
        }
        if (!adaptive) row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.VerticalAlignment = VerticalAlignment.Stretch;
        var buttons = new List<(string key, Button button)>();
        Button? activeButton = null;
        double placedX = double.NaN;
        double placedWidth = double.NaN;
        bool indicatorPositioned = false;

        void PlaceIndicator(bool animate)
        {
            if (activeButton is null || track.ActualWidth <= 0 || activeButton.ActualWidth <= 0)
                return;
            Point position = activeButton.TranslatePoint(new Point(0, 0), track);
            double x = position.X + 2;
            double width = Math.Max(0, activeButton.ActualWidth - 4);
            if (!animate && Math.Abs(x - placedX) < 0.1 && Math.Abs(width - placedWidth) < 0.1)
                return;
            placedX = x;
            placedWidth = width;
            indicatorPositioned = true;
            UiMotion.MoveAndResize(
                indicator,
                x,
                width,
                animate);
        }

        void Paint(string active, bool animateIndicator)
        {
            foreach (var (key, button) in buttons)
            {
                bool isSelected = string.Equals(key, active, StringComparison.OrdinalIgnoreCase);
                if (isSelected)
                {
                    button.SetResourceReference(ForegroundProperty, "AccentText");
                    activeButton = button;
                }
                else
                {
                    button.SetResourceReference(ForegroundProperty, "MenuFg");
                }
            }

            PlaceIndicator(animateIndicator);
            if (selectedButton is not null && activeButton is not null) selectedButton(activeButton);
        }

        for (int index = 0; index < options.Length; index++)
        {
            var (key, label) = options[index];
            var segmentKey = key;
            bool dense = options.Length >= 5;
            var text = new TextBlock
            {
                Text = label,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = textAlignment,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (adaptive)
                Typography.BlockLineHeight(text, Typography.ControlLineHeight);
            var segment = new Button
            {
                Content = text,
                Padding = adaptive ? ButtonPadding : compact ? new Thickness(0, 4, 0, 4) : ButtonPadding,
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Height = ControlHeight - 2,
                MinHeight = ControlHeight - 2,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = dense ? Typography.SmallSize : Typography.ControlSize,
                FontWeight = FontWeights.Normal,
                Cursor = Cursors.Hand,
                FlowDirection = FlowDirection.LeftToRight,
                ToolTip = label,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            segment.SetResourceReference(StyleProperty, "SegmentedButton");
            segment.Click += (_, _) =>
            {
                if (string.Equals(segmentKey, selected, StringComparison.OrdinalIgnoreCase)) return;
                selected = segmentKey;
                select(segmentKey);
                Paint(segmentKey, animateIndicator: true);
            };
            buttons.Add((segmentKey, segment));
            row.Children.Add(segment);
        }

        track.Children.Add(row);
        track.Loaded += (_, _) => PlaceIndicator(animate: false);
        track.SizeChanged += (_, _) => PlaceIndicator(animate: false);
        track.LayoutUpdated += (_, _) =>
        {
            if (!indicatorPositioned) PlaceIndicator(animate: false);
        };

        Paint(selected, animateIndicator: false);
        var host = new Border
        {
            CornerRadius = new CornerRadius(ControlRadius),
            // The border is the whole control's frame; keeping it out of the
            // padding makes every segmented row exactly ControlHeight tall.
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            Child = track,
            HorizontalAlignment = alignment,
            VerticalAlignment = verticalAlignment,
            FlowDirection = FlowDirection.LeftToRight,
            Height = ControlHeight,
            MinHeight = ControlHeight,
            MaxHeight = ControlHeight,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        host.SetResourceReference(Border.BackgroundProperty, "SettingsCardBg");
        host.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        return host;
    }

    private static DataTemplate TrimmedTextTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        factory.SetValue(TextBlock.FontFamilyProperty, Typography.PathUi);
        factory.SetValue(FrameworkElement.LanguageProperty, Typography.PathLanguage);

        var tooltip = new FrameworkElementFactory(typeof(TextBlock));
        tooltip.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        tooltip.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        tooltip.SetValue(TextBlock.FontFamilyProperty, Typography.PathUi);
        tooltip.SetValue(FrameworkElement.LanguageProperty, Typography.PathLanguage);
        tooltip.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
        factory.SetValue(FrameworkElement.ToolTipProperty, tooltip);
        return new DataTemplate(typeof(string)) { VisualTree = factory };
    }

    private static TextBlock Footer(string format, string path)
    {
        string fullText = string.Format(CultureInfo.CurrentCulture, format, path);
        var block = new TextBlock
        {
            FontSize = Typography.CaptionSize,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = fullText,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextTertiary");
        Typography.ApplyText(block);
        Typography.BlockLineHeight(block, Typography.CaptionLineHeight);
        var parts = format.Split("{0}");
        block.Inlines.Add(new Run(parts[0]));
        if (parts.Length > 1)
        {
            var pathRun = new Run(path);
            Typography.ApplyPath(pathRun);
            block.Inlines.Add(pathRun);
            block.Inlines.Add(new Run(string.Join("{0}", parts.Skip(1))));
        }
        return block;
    }

    private static TextBlock PathTooltip(string? path)
    {
        var block = new TextBlock { Text = path ?? string.Empty, TextWrapping = TextWrapping.NoWrap };
        Typography.ApplyPath(block);
        return block;
    }

    private static TextBlock Text(
        string text,
        double size,
        double lineHeight,
        FontWeight? weight = null,
        string foregroundResource = "SettingsTextPrimary")
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        Typography.ApplyText(block);
        Typography.BlockLineHeight(block, lineHeight);
        return block;
    }

    private static (Grid section, StackPanel body) Section(
        string title,
        SettingsCategory category,
        SettingsGlyph glyph,
        FrameworkElement? aside = null)
    {
        var section = new Grid { Margin = new Thickness(0, 0, 0, SectionGap) };
        var panel = new Border
        {
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(1),
        };
        panel.SetResourceReference(Border.BackgroundProperty, "SettingsSectionBg");
        panel.SetResourceReference(Border.BorderBrushProperty, "SettingsSectionBorder");

        var card = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, SectionHeadingGap) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Orientation = Orientation.Horizontal };
        var iconChip = new Border
        {
            Width = IconSlotSize,
            Height = IconSlotSize,
            CornerRadius = new CornerRadius(IconSlotRadius),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconChip.SetResourceReference(Border.BackgroundProperty, CategorySurfaceKey(category));
        var icon = GlyphPath(glyph, 14, 1.4, CategoryIconKey(category));
        iconChip.Child = icon;
        headingText.Children.Add(iconChip);
        var caption = Text(title, Typography.HeadingSize, Typography.HeadingLineHeight, FontWeights.SemiBold);
        caption.TextWrapping = TextWrapping.NoWrap;
        caption.Margin = new Thickness(8, 0, 0, 0);
        caption.VerticalAlignment = VerticalAlignment.Center;
        headingText.Children.Add(caption);
        heading.Children.Add(headingText);
        if (aside is not null)
        {
            aside.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(aside, 2);
            heading.Children.Add(aside);
        }
        card.Children.Add(heading);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(body);

        // A whisper of light along the top edge. It reads as the card catching
        // the window light and is subtle in the light theme, clear in the dark.
        var sheen = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, CardRadius - 1)),
            Margin = new Thickness(1),
            IsHitTestVisible = false,
            Background = Theme.Freeze(new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.42),
                },
            }),
        };
        var surface = new Grid();
        surface.Children.Add(sheen);
        surface.Children.Add(card);
        panel.Child = surface;
        section.Children.Add(panel);
        return (section, body);
    }

    private static Grid Row(string label, UIElement control, string? tip = null, string? help = null, double? labelWidth = null)
    {
        var labelBlock = new StackPanel
        {
            MaxWidth = labelWidth ?? HelperLabelMaxWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = Text(label, Typography.BodySize, Typography.BodyLineHeight, FontWeights.Medium);
        labelBlock.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(help))
        {
            var detail = Text(
                help,
                Typography.SmallSize,
                Typography.SmallLineHeight,
                foregroundResource: "SettingsTextSecondary");
            detail.Margin = new Thickness(0, HelperGap, 0, 0);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            labelBlock.Children.Add(detail);
        }
        string? fullTip = string.IsNullOrWhiteSpace(help)
            ? tip
            : string.IsNullOrWhiteSpace(tip) ? help : $"{tip}\n{help}";
        labelBlock.ToolTip = fullTip;
        return SettingRow(labelBlock, control, fullTip, labelWidth);
    }

    private static Grid FieldRow(string label, UIElement control, string? tip = null, string? help = null) =>
        Row(label, control, tip, help);

    private static Grid PairRow(Grid left, Grid right)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, RowGap),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PairGap) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });

        left.Margin = new Thickness(0);
        right.Margin = new Thickness(0);
        row.Children.Add(left);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    private static Grid SettingRow(UIElement label, UIElement control, string? tip = null, double? labelWidth = null)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, RowGap),
            MinHeight = RowMinHeight,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(labelWidth ?? FieldLabelWidth),
            MinWidth = labelWidth ?? FieldLabelWidth,
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (label is FrameworkElement labelElement)
        {
            labelElement.VerticalAlignment = VerticalAlignment.Center;
            if (tip is not null) labelElement.ToolTip = tip;
        }
        row.Children.Add(label);

        if (control is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(LabelControlGap, 0, 0, 0);
        }
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private CheckBox Check(
        string label,
        string? tip,
        bool value,
        Action<bool> set,
        Func<bool> read,
        Func<bool>? enabledBy = null,
        bool child = false,
        string? help = null,
        bool compact = false)
    {
        var labelBlock = new StackPanel();
        var content = Text(label, Typography.BodySize, Typography.BodyLineHeight, FontWeights.Medium);
        content.TextWrapping = TextWrapping.NoWrap;
        content.TextTrimming = TextTrimming.CharacterEllipsis;
        labelBlock.Children.Add(content);
        if (!compact && !string.IsNullOrWhiteSpace(help))
        {
            var detail = Text(
                help,
                Typography.SmallSize,
                Typography.SmallLineHeight,
                foregroundResource: "SettingsTextSecondary");
            detail.Margin = new Thickness(0, HelperGap, 0, 0);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            labelBlock.Children.Add(detail);
        }
        string? fullTip = string.IsNullOrWhiteSpace(help)
            ? tip
            : string.IsNullOrWhiteSpace(tip) ? help : $"{tip}\n{help}";
        var box = new CheckBox
        {
            Content = labelBlock,
            IsChecked = value,
            IsEnabled = enabledBy?.Invoke() ?? true,
            MinHeight = compact ? ControlHeight : RowMinHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Margin = new Thickness(child ? 22 : 0, 0, 0, compact ? 0 : RowGap),
            ToolTip = fullTip,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _checks.Add((box, read, enabledBy));
        box.Checked += (_, _) =>
        {
            set(true);
            Changed();
            SyncCheckStates();
        };
        box.Unchecked += (_, _) =>
        {
            set(false);
            Changed();
            SyncCheckStates();
        };
        return box;
    }

    private void SyncCheckStates()
    {
        foreach (var (box, _, enabledBy) in _checks) box.IsEnabled = enabledBy?.Invoke() ?? true;
        SyncDependentStates();
    }

    private void SyncDependentStates()
    {
        foreach (var (element, enabledBy) in _dependentControls) element.IsEnabled = enabledBy();
    }

    private void SyncChecks()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(SyncChecks));
            return;
        }

        _building = true;
        try
        {
            foreach (var (box, read, enabledBy) in _checks)
            {
                box.IsChecked = read();
                box.IsEnabled = enabledBy?.Invoke() ?? true;
            }
            SyncDependentStates();
        }
        finally
        {
            _building = false;
        }
    }

    private ComboBox Choice((string key, string label)[] options, string current, Action<string> set)
    {
        var box = StandardField(new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(9, 0, 6, 0),
        });
        foreach (var (_, label) in options) box.Items.Add(label);
        int index = Array.FindIndex(options, o => o.key == current);
        box.SelectedIndex = index < 0 ? 0 : index;
        box.ToolTip = options[box.SelectedIndex].label;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            box.ToolTip = options[box.SelectedIndex].label;
            set(options[box.SelectedIndex].key);
            Changed();
        };
        return box;
    }

    private static T StandardField<T>(T field) where T : Control
    {
        field.Height = ControlHeight;
        field.MinHeight = ControlHeight;
        field.MaxHeight = ControlHeight;
        field.VerticalContentAlignment = VerticalAlignment.Center;
        return field;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush PegBodyBrush(string key) => key switch
    {
        "wood" => Brush(Color.FromRgb(0xB4, 0x7B, 0x45)),
        "metal" => Brush(Color.FromRgb(0xC9, 0xCE, 0xD4)),
        "red" => Brush(Color.FromRgb(0xE2, 0x4A, 0x4A)),
        "blue" => Brush(Color.FromRgb(0x3C, 0x8C, 0xE2)),
        "green" => Brush(Color.FromRgb(0x4C, 0xB8, 0x5C)),
        "yellow" => Brush(Color.FromRgb(0xF2, 0xC2, 0x30)),
        _ => MixedPegBrush(),
    };

    private static Brush PegBandBrush(string key) => key switch
    {
        "wood" => Brush(Color.FromRgb(0x75, 0x54, 0x35)),
        "metal" => Brush(Color.FromRgb(0x76, 0x7D, 0x85)),
        _ => Brush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
    };

    private static Brush MixedPegBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE2, 0x4A, 0x4A), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xC2, 0x30), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3C, 0x8C, 0xE2), 1));
        brush.Freeze();
        return brush;
    }

    private FrameworkElement QuickLookKeyControl()
    {
        _quickLookKey = StandardField(new TextBox
        {
            Text = Settings.QuickLookKeyOption(Settings.Current.QuickLookKey),
            IsReadOnly = true,
            ToolTip = Strings.QuickLookKeyTip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 0, 12, 0),
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        });
        _quickLookKey.PreviewKeyDown += OnQuickLookKeyPressed;
        return _quickLookKey;
    }

    private void OnQuickLookKeyPressed(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape)
            return;

        var text = key.ToString();
        if (!HotKey.Parse(text, out _, out _)) return;
        Settings.Current.QuickLookKey = text;
        _quickLookKey.Text = text;
        Changed();
    }

    private void OnHotKeyPressed(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) return;
        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0 && key is not (>= Key.F1 and <= Key.F24)) return; // a bare letter would steal typing everywhere
        parts.Add(key.ToString());
        var text = string.Join("+", parts);
        if (!HotKey.Parse(text, out _, out _)) return;
        Settings.Current.HotKey = text;
        _hotKey.Text = text;
        Changed();
        // The apply is debounced, so clear a message about the old key right away.
        SyncHotKeyError();
    }

    private void SyncHotKeyError()
    {
        if (_hotKeyError is null) return;
        var (shortcut, claimed) = AppController.LastHotKeyAttempt;
        // Only blame the shortcut that is actually in the field. A stale failure for a key
        // the user has since retyped would be worse than the silence it replaces.
        _hotKeyError.Visibility = !claimed &&
            string.Equals(shortcut, Settings.Current.HotKey, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Changed()
    {
        if (_building) return;
        Settings.Current.Save();
        // Coalesce rapid edits into one heavy refresh.
        _applyTimer.Stop();
        _applyTimer.Start();
    }
}

internal sealed record RopeColorOption(string Key, string Label, Brush Swatch, Brush Stroke);
internal sealed record PegStyleOption(string Key, string Label, Brush Body, Brush Band, Brush Stroke);
