using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using HangAll.Core;
using HangAll.Interop;
using Microsoft.Win32;
using Line = HangAll.Core.Line;
using Path = System.Windows.Shapes.Path;

namespace HangAll.UI;

/// <summary>
/// A paper card for editing the individual tasks on a sticky note.
/// Every row edits the same task objects that are persisted by <see cref="Notes"/>.
/// </summary>
public sealed class NoteWindow : Window
{
    private static readonly Dictionary<string, NoteWindow> Open = new(StringComparer.OrdinalIgnoreCase);
    private const double PageWidth = 400;
    private const double PageHeight = 420;
    private const double PageMinWidth = 320;
    private const double PageMinHeight = 320;
    private const double WorkAreaInset = 60;
    private const double PageMargin = 0;
    private const double TaskRowHeight = 42;

    private static readonly Color Ink = Color.FromRgb(0x3A, 0x30, 0x1E);
    private static readonly Color SoftInk = Color.FromArgb(0xB8, 0x4B, 0x40, 0x2C);
    private static readonly Color DoneFill = Color.FromRgb(0x57, 0x7D, 0x62);

    public static NoteWindow? Show(string path, Line line)
    {
        if (Open.TryGetValue(path, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var data = Notes.Get(path);
        if (data is null) return null;
        var window = new NoteWindow(path, data, line);
        Open[path] = window;
        window.Closed += (_, _) => Open.Remove(path);
        window.Show();
        window.Activate();
        return window;
    }

    public static NoteWindow? ShowBelowCard(string path, Line line, Display display, Rect cardPhysicalRect)
    {
        var data = Notes.Get(path);
        if (data is null) return null;

        if (!Open.TryGetValue(path, out var window))
        {
            window = new NoteWindow(path, data, line);
            Open[path] = window;
            window.Closed += (_, _) => Open.Remove(path);
        }

        window.PlaceBelowCard(display, cardPhysicalRect);
        if (!window.IsVisible) window.Show();
        window.Activate();
        return window;
    }

    internal void PlaceBelowCard(Display display, Rect cardPhysicalRect)
    {
        double scale = Math.Max(0.1, display.Scale);
        FitToDisplay(display);
        var work = new Rect(
            display.Work.Left / scale,
            display.Work.Top / scale,
            display.Work.Width / scale,
            display.Work.Height / scale);
        var card = new Rect(
            cardPhysicalRect.Left / scale,
            cardPhysicalRect.Top / scale,
            cardPhysicalRect.Width / scale,
            cardPhysicalRect.Height / scale);
        var position = AnchoredPosition(work, card, new Size(Width, Height));
        Left = position.Left;
        Top = position.Top;
    }

    public static Rect AnchoredPosition(Rect workArea, Rect card, Size window)
    {
        if (window.Width > workArea.Width || window.Height > workArea.Height)
            return workArea;

        double below = card.Bottom + 8;
        double above = card.Top - window.Height - 8;
        double top = below + window.Height <= workArea.Bottom ? below : above;
        double left = card.Left + (card.Width - window.Width) / 2;

        left = Math.Clamp(left, workArea.Left, workArea.Right - window.Width);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - window.Height);
        return new Rect(left, top, window.Width, window.Height);
    }

    /// <summary>Apply a new UI type scale to note editors that are already open.</summary>
    public static void RefreshTypography()
    {
        foreach (var window in Open.Values) window.ApplyTypography();
    }

    private readonly string _path;
    private readonly Line _line;
    private readonly NoteData _data;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly List<TaskRow> _rows = new();
    private readonly List<(Border swatch, string key)> _swatches = new();
    private readonly Path _paper = new();
    private readonly Border _header;
    private readonly TextBox _title = new();
    private readonly TextBlock _titlePlaceholder = new();
    private readonly StackPanel _taskPanel = new();
    private readonly ScrollViewer _scroll;
    private readonly Button _addTask;
    private readonly Button _done;
    private readonly Border _dragSurface;
    private string _color;
    private bool _dirty;
    private bool _updating;
    private bool _updatingTitle;
    private bool _syncing;
    private bool _skipSaveOnClose;

    internal string NotePath => _path;

    internal static void Detach(string path)
    {
        if (!Open.TryGetValue(path, out var window)) return;
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(new Action(window.CloseDetached));
            return;
        }
        window.CloseDetached();
    }

    private NoteWindow(string path, NoteData data, Line line)
    {
        _path = path;
        _line = line;
        _data = data;
        _color = data.Color;
        Notes.Normalize(_data);

        Title = WindowCaption;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        Icon = Brand.Icon;
        Topmost = true;
        Width = PageWidth;
        Height = PageHeight;
        MinWidth = PageMinWidth;
        MinHeight = PageMinHeight;
        MaxWidth = PageWidth;
        MaxHeight = PageHeight;
        FlowDirection = Strings.Flow;
        Typography.Apply(this);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, Strings.IsEastAsian ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.Manual;

        var display = Displays.UnderPointer();
        FitToDisplay(display);
        double scale = Math.Max(0.1, display.Scale);
        Left = (display.Work.Left + (display.Work.Width - Width * scale) / 2) / scale;
        Top = (display.Work.Top + (display.Work.Height - Height * scale) / 2) / scale;

        var root = new Grid { Background = Brushes.Transparent };
        Content = root;

        var paperLayer = new Grid
        {
            Margin = new Thickness(PageMargin),
            IsHitTestVisible = false,
        };
        paperLayer.Children.Add(_paper);
        root.Children.Add(paperLayer);

        _dragSurface = new Border
        {
            Margin = new Thickness(PageMargin + 8, PageMargin + 8, PageMargin + 8, PageMargin + 8),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
        };
        _dragSurface.MouseLeftButtonDown += BeginDrag;
        root.Children.Add(_dragSurface);

        var content = new Grid
        {
            Margin = new Thickness(24, 18, 22, 18),
        };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.Children.Add(content);

        _header = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
        };
        _header.MouseLeftButtonDown += BeginDrag;
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var grip = new Canvas
        {
            Width = 12,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        for (int i = 0; i < 3; i++)
        {
            var gripLine = new Border
            {
                Width = 12,
                Height = 1.5,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(WithAlpha(0x68, Ink)),
            };
            Canvas.SetTop(gripLine, 2 + i * 7);
            grip.Children.Add(gripLine);
        }
        headerGrid.Children.Add(grip);
        _title.Text = _data.Title;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(Ink);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.VerticalContentAlignment = VerticalAlignment.Center;
        _title.HorizontalContentAlignment = HorizontalAlignment.Left;
        _title.AcceptsReturn = false;
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _title.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _title.Background = Brushes.Transparent;
        _title.BorderThickness = new Thickness(0);
        _title.Padding = new Thickness(0);
        _title.Margin = new Thickness(0);
        _title.Cursor = Cursors.IBeam;
        _title.FocusVisualStyle = null;
        _title.CaretBrush = new SolidColorBrush(Ink);
        _title.SelectionBrush = new SolidColorBrush(WithAlpha(0x55, Ink));
        _title.ToolTip = _data.Title.Length > 0 ? _data.Title : Strings.EditNote;
        _title.TextChanged += (_, _) => OnTitleChanged();
        _title.GotKeyboardFocus += (_, _) => UpdateTitlePlaceholder();
        _title.LostKeyboardFocus += (_, _) => UpdateTitlePlaceholder();

        var titleHost = new Grid();
        titleHost.Children.Add(_title);
        _titlePlaceholder.Text = Strings.EditNote;
        _titlePlaceholder.FontWeight = FontWeights.SemiBold;
        _titlePlaceholder.Foreground = new SolidColorBrush(WithAlpha(0x78, Ink));
        _titlePlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _titlePlaceholder.TextTrimming = TextTrimming.CharacterEllipsis;
        _titlePlaceholder.IsHitTestVisible = false;
        titleHost.Children.Add(_titlePlaceholder);
        Grid.SetColumn(titleHost, 1);
        headerGrid.Children.Add(titleHost);
        _header.Child = headerGrid;
        content.Children.Add(_header);

        var divider = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(WithAlpha(0x30, Ink)),
            IsHitTestVisible = false,
        };
        Grid.SetRow(divider, 0);
        content.Children.Add(divider);

        _taskPanel.Margin = new Thickness(0, 4, 0, 0);
        _scroll = new ScrollViewer
        {
            Content = _taskPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Padding = new Thickness(0, 0, 2, 0),
        };
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);

        _addTask = PaperButton($"+  {Strings.AddTask}", false);
        _addTask.HorizontalContentAlignment = HorizontalAlignment.Left;
        _addTask.Margin = new Thickness(0, 4, 0, 0);
        _addTask.Padding = new Thickness(4, 0, 4, 0);
        _addTask.Click += (_, _) => AddTaskAfter(_data.Tasks.Count - 1);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var colours = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var key in Notes.Colors)
        {
            string colour = key;
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Notes.Paper(colour)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(WithAlpha(0x55, Ink)),
                Cursor = Cursors.Hand,
                ToolTip = Strings.NoteName(colour),
            };
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _color = colour;
                Paint();
                Touch();
            };
            _swatches.Add((swatch, colour));
            colours.Children.Add(swatch);
        }
        footer.Children.Add(colours);

        _done = PaperButton(Strings.Done, true);
        _done.Width = 82;
        _done.Height = 34;
        _done.HorizontalAlignment = HorizontalAlignment.Right;
        _done.VerticalAlignment = VerticalAlignment.Center;
        _done.Click += (_, _) => Close();
        Grid.SetColumn(_done, 1);
        footer.Children.Add(_done);
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        var footerLine = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(WithAlpha(0x28, Ink)),
            IsHitTestVisible = false,
        };
        Grid.SetRow(footerLine, 2);
        content.Children.Add(footerLine);

        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closing += (_, e) =>
        {
            _save.Stop();
            if (!_skipSaveOnClose) Save();
            if (e.Cancel) return;
            Notes.TasksChanged -= OnTasksChanged;
            Strings.LanguageChanged -= ApplyLanguage;
        };

        // Kept alive by the static theme event and by this window's Closed event.
        _ = new ThemeBinder(this, ApplyTypography, hwnd =>
        {
            FullScreen.Own.Add(hwnd);
            // Forgetting the handle on close keeps the static set from growing.
            Closed += (_, _) => FullScreen.Own.Remove(hwnd);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        });
        Notes.TasksChanged += OnTasksChanged;
        Strings.LanguageChanged += ApplyLanguage;
        Paint();
        ApplyTypography();
        RebuildRows();
        UpdateTitlePlaceholder();
        Loaded += (_, _) =>
        {
            if (_rows.Count > 0)
            {
                var text = _rows[0].Text;
                text.Focus();
                text.CaretIndex = text.Text.Length;
            }
        };
    }

    private void Paint()
    {
        var inset = PageMargin * 2;
        double width = Math.Max(1, Width - inset);
        double height = Math.Max(1, Height - inset);
        _paper.Data = Notes.SheetGeometry(width, height);
        _paper.Fill = Notes.PaperBrush(_color, width, height, _path + "|" + _data.Title);
        _paper.Stroke = null;
        _paper.IsHitTestVisible = false;

        _done.Background = new SolidColorBrush(DoneFill);
        _done.Foreground = Brushes.White;
        foreach (var (swatch, key) in _swatches)
        {
            bool selected = string.Equals(key, _color, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(selected ? 2.5 : 1);
            swatch.BorderBrush = new SolidColorBrush(selected ? Ink : WithAlpha(0x55, Ink));
        }
    }

    private TaskRow BuildTaskRow(NoteTask task, int index)
    {
        var row = new Grid
        {
            MinHeight = TaskRowHeight,
            Margin = new Thickness(0, 0, 0, 3),
            Background = Brushes.Transparent,
            AllowDrop = true,
            ToolTip = Strings.AttachmentDropHint,
        };
        row.PreviewDragOver += (_, e) => PreviewAttachmentDragOver(row, e);
        row.PreviewDragLeave += (_, e) =>
        {
            var point = e.GetPosition(row);
            if (point.X < 0 || point.Y < 0 || point.X >= row.ActualWidth || point.Y >= row.ActualHeight)
                row.Background = Brushes.Transparent;
        };
        row.PreviewDrop += (_, e) => DropAttachments(row, index, task, e);
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var check = new CheckBox
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = task.Completed,
            ToolTip = Strings.ToggleTaskStatus,
            Style = TryFindResource("NoteTaskCheckBox") as Style,
        };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);

        var textHost = new Grid();
        var text = new TextBox
        {
            Text = task.Text,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 6, 0),
            FontFamily = Typography.ForText(task.Text),
            FontSize = Typography.BodySize,
            Foreground = new SolidColorBrush(Ink),
            CaretBrush = new SolidColorBrush(Ink),
            VerticalContentAlignment = VerticalAlignment.Center,
            FlowDirection = TextFlow(task.Text),
            TextAlignment = TextFlow(task.Text) == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left,
            ToolTip = task.Text.Length > 0 ? task.Text : null,
        };
        Typography.ApplyText(text);
        text.TextChanged += (_, _) =>
        {
            if (_updating) return;
            task.Text = text.Text;
            var direction = TextFlow(text.Text);
            text.FlowDirection = direction;
            text.TextAlignment = direction == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
            text.ToolTip = text.Text.Length > 0 ? text.Text : null;
            Touch();
        };
        text.PreviewKeyDown += (_, e) => OnTaskKey(text, index, e);
        textHost.Children.Add(text);
        var strike = new Border
        {
            Height = 1,
            Margin = new Thickness(6, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(WithAlpha(0x9A, Ink)),
            IsHitTestVisible = false,
            Opacity = task.Completed ? 1 : 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(task.Completed ? 1 : 0.2, 1),
        };
        textHost.Children.Add(strike);
        Grid.SetColumn(textHost, 1);
        row.Children.Add(textHost);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var attach = PaperButton("", false);
        attach.Width = 28;
        attach.Height = 30;
        attach.Padding = new Thickness(0);
        attach.ToolTip = Strings.AddAttachment;
        attach.Content = PaperclipGlyph(Ink);
        attach.Click += (_, _) => AddAttachments(index, task);
        actions.Children.Add(attach);

        var delete = PaperButton("", false);
        delete.Width = 28;
        delete.Height = 30;
        delete.Padding = new Thickness(0);
        delete.ToolTip = Strings.DeleteTask;
        delete.Content = TrashGlyph(Ink);
        delete.Click += (_, _) => DeleteTask(index, true);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        var attachments = new StackPanel
        {
            Margin = new Thickness(32, 0, 32, 6),
            Visibility = task.Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        var removers = new List<Button>();
        foreach (string attachment in task.Attachments)
        {
            var attachmentRow = BuildAttachmentRow(index, task, attachment, out var remove);
            removers.Add(remove);
            attachments.Children.Add(attachmentRow);
        }
        Grid.SetRow(attachments, 1);
        Grid.SetColumnSpan(attachments, 3);
        row.Children.Add(attachments);

        var result = new TaskRow(task, check, text, strike, attach, delete, removers, row);
        check.Checked += (_, _) => SetCompletion(result, true);
        check.Unchecked += (_, _) => SetCompletion(result, false);
        UpdateCompletionVisual(result, false);
        return result;
    }

    private FrameworkElement BuildAttachmentRow(
        int taskIndex,
        NoteTask task,
        string attachment,
        out Button remove)
    {
        var row = new Grid
        {
            Height = 26,
            Background = Brushes.Transparent,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var icon = PaperclipGlyph(WithAlpha(0xC0, Ink));
        icon.Width = 12;
        icon.Height = 12;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = System.IO.Path.GetFileName(attachment) is { Length: > 0 } file ? file : attachment,
            ToolTip = attachment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(SoftInk),
            FontFamily = Typography.ForText(attachment),
            FontSize = Typography.SmallSize,
        };
        Typography.ApplyText(name);
        name.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenAttachment(attachment);
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        remove = PaperButton("", false);
        remove.Width = 22;
        remove.Height = 24;
        remove.Padding = new Thickness(0);
        remove.ToolTip = Strings.RemoveAttachment;
        remove.Content = CloseGlyph(WithAlpha(0xD0, Ink));
        remove.Click += (_, _) =>
        {
            if (!task.Attachments.Remove(attachment)) return;
            Touch();
            RebuildRows(taskIndex);
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    private void AddAttachments(int index, NoteTask task)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.AddAttachment,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var accepted = Notes.FilterExistingAttachments(dialog.FileNames, task.Attachments);
        if (accepted.Count == 0)
        {
            ShowInvalidAttachmentNotice();
            return;
        }
        task.Attachments.AddRange(accepted);
        Touch();
        RebuildRows(index);
    }

    private void PreviewAttachmentDragOver(Grid row, DragEventArgs e)
    {
        if (!HasDroppedFiles(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
        row.Background = new SolidColorBrush(WithAlpha(0x1F, Ink));
        e.Handled = true;
    }

    private void DropAttachments(Grid row, int index, NoteTask task, DragEventArgs e)
    {
        row.Background = Brushes.Transparent;
        if (!HasDroppedFiles(e.Data)) return;
        e.Handled = true;

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var accepted = Notes.FilterExistingAttachments(paths, task.Attachments);
        if (accepted.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            ShowInvalidAttachmentNotice();
            return;
        }

        e.Effects = DragDropEffects.Copy;
        task.Attachments.AddRange(accepted);
        Touch();
        RebuildRows(index);
    }

    private static bool HasDroppedFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) &&
        data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void ShowInvalidAttachmentNotice()
    {
        MessageBox.Show(
            this,
            Strings.AttachmentDropInvalid,
            Strings.AddAttachment,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenAttachment(string path)
    {
        if (System.IO.File.Exists(path) && Shell.Open(path)) return;
        MessageBox.Show(
            this,
            Strings.AttachmentOpenFailed(path),
            Strings.EditNote,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RebuildRows(int? focusIndex = null, int? caret = null)
    {
        Notes.Normalize(_data);
        _updating = true;
        _taskPanel.Children.Clear();
        _rows.Clear();
        for (int i = 0; i < _data.Tasks.Count; i++)
        {
            var row = BuildTaskRow(_data.Tasks[i], i);
            _rows.Add(row);
            _taskPanel.Children.Add(row.Root);
        }
        _taskPanel.Children.Add(_addTask);
        _updating = false;

        if (focusIndex is not int focus || focus < 0 || focus >= _rows.Count) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (focus >= _rows.Count) return;
            var text = _rows[focus].Text;
            text.Focus();
            text.CaretIndex = Math.Clamp(caret ?? text.Text.Length, 0, text.Text.Length);
        }), DispatcherPriority.Input);
    }

    private void SetCompletion(TaskRow row, bool completed)
    {
        if (_updating || _syncing) return;
        row.Task.Completed = completed;
        UpdateCompletionVisual(row, true);
        Touch();
    }

    private static void UpdateCompletionVisual(TaskRow row, bool animate)
    {
        bool completed = row.Task.Completed;
        row.Text.BeginAnimation(OpacityProperty, null);
        row.Strike.BeginAnimation(OpacityProperty, null);
        if (row.Strike.RenderTransform is ScaleTransform scale)
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        row.Text.Opacity = completed ? 0.64 : 1;
        row.Strike.Opacity = completed ? 1 : 0;
        if (row.Strike.RenderTransform is ScaleTransform restScale)
            restScale.ScaleX = completed ? 1 : 0.2;

        if (!animate) return;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        row.Text.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(completed ? 1 : 0.64, completed ? 0.64 : 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        if (!completed) return;

        row.Strike.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        if (row.Strike.RenderTransform is ScaleTransform activeScale)
        {
            activeScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                });
        }
    }

    private void OnTaskKey(TextBox text, int index, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _data.Tasks[index].Text = text.Text;
            AddTaskAfter(index);
            return;
        }

        if (e.Key == Key.Back && text.Text.Length == 0)
        {
            e.Handled = true;
            DeleteTask(index, true);
        }
    }

    private void AddTaskAfter(int index)
    {
        Notes.Normalize(_data);
        int at = Math.Clamp(index + 1, 0, _data.Tasks.Count);
        _data.Tasks.Insert(at, new NoteTask());
        Touch();
        RebuildRows(at, 0);
    }

    private void DeleteTask(int index, bool moveFocus)
    {
        if (index < 0 || index >= _data.Tasks.Count) return;
        if (!Notes.DeleteTask(_data, index)) return;
        Touch();
        if (_data.Tasks.Count == 1)
        {
            RebuildRows(0, 0);
            return;
        }

        int focus = moveFocus ? Math.Max(0, index - 1) : Math.Min(index, _data.Tasks.Count - 1);
        RebuildRows(focus);
    }

    private void FitToDisplay(Display display)
    {
        double scale = Math.Max(0.1, display.Scale);
        double availableWidth = Math.Max(1, display.Work.Width / scale - WorkAreaInset);
        double availableHeight = Math.Max(1, display.Work.Height / scale - WorkAreaInset);
        MinWidth = Math.Min(PageMinWidth, availableWidth);
        MinHeight = Math.Min(PageMinHeight, availableHeight);
        Width = Math.Min(PageWidth, availableWidth);
        Height = Math.Min(PageHeight, availableHeight);
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.Source is TextBoxBase or ButtonBase ||
            e.OriginalSource is TextBoxBase or ButtonBase) return;
        e.Handled = true;
        DragMove();
    }

    private string WindowCaption =>
        string.IsNullOrWhiteSpace(_data.Title) ? Strings.EditNote : _data.Title;

    private void OnTitleChanged()
    {
        if (_updatingTitle) return;
        string normalized = Notes.NormalizeTitle(_title.Text);
        if (!string.Equals(_title.Text, normalized, StringComparison.Ordinal))
        {
            _updatingTitle = true;
            int caret = Math.Min(_title.CaretIndex, normalized.Length);
            _title.Text = normalized;
            _title.CaretIndex = caret;
            _updatingTitle = false;
        }

        _data.Title = normalized;
        _title.ToolTip = normalized.Length > 0 ? normalized : Strings.EditNote;
        var direction = TextFlow(normalized);
        _title.FlowDirection = direction;
        _title.TextAlignment = direction == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
        Title = WindowCaption;
        UpdateTitlePlaceholder();
        Paint();
        Touch();
    }

    private void UpdateTitlePlaceholder()
    {
        _titlePlaceholder.Visibility = _title.Text.Length == 0 && !_title.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTasksChanged(string path)
    {
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnTasksChanged(path)));
            return;
        }

        var fresh = Notes.Get(_path);
        if (fresh is null) return;
        _syncing = true;
        try
        {
            for (int i = 0; i < _rows.Count && i < fresh.Tasks.Count; i++)
            {
                bool changed = _rows[i].Task.Completed != fresh.Tasks[i].Completed;
                _rows[i].Task.Completed = fresh.Tasks[i].Completed;
                _rows[i].Check.IsChecked = fresh.Tasks[i].Completed;
                UpdateCompletionVisual(_rows[i], changed);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyLanguage()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyLanguage));
            return;
        }

        Title = WindowCaption;
        FlowDirection = Strings.Flow;
        TextOptions.SetTextFormattingMode(this, Strings.IsEastAsian ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        _titlePlaceholder.Text = Strings.EditNote;
        _title.ToolTip = _data.Title.Length > 0 ? _data.Title : Strings.EditNote;
        _title.FlowDirection = TextFlow(_data.Title);
        _title.TextAlignment = _title.FlowDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
        _addTask.Content = $"+  {Strings.AddTask}";
        _done.Content = Strings.Done;
        foreach (var row in _rows)
        {
            row.Attach.ToolTip = Strings.AddAttachment;
            row.Delete.ToolTip = Strings.DeleteTask;
            row.Check.ToolTip = Strings.ToggleTaskStatus;
            row.Root.ToolTip = Strings.AttachmentDropHint;
            foreach (var remove in row.AttachmentRemovers) remove.ToolTip = Strings.RemoveAttachment;
        }
        foreach (var (swatch, key) in _swatches) swatch.ToolTip = Strings.NoteName(key);
        RebuildRows();
        ApplyTypography();
        UpdateTitlePlaceholder();
        Paint();
    }

    private void ApplyTypography()
    {
        Typography.Apply(this);
        _title.FontFamily = Typography.Ui;
        _title.FontSize = Typography.WindowTitleSize;
        _title.FontWeight = FontWeights.SemiBold;
        Typography.BlockLineHeight(_title, Typography.WindowTitleLineHeight);
        _titlePlaceholder.FontFamily = Typography.Ui;
        _titlePlaceholder.FontSize = Typography.WindowTitleSize;
        _titlePlaceholder.FontWeight = FontWeights.SemiBold;
        Typography.BlockLineHeight(_titlePlaceholder, Typography.WindowTitleLineHeight);
        _addTask.FontFamily = Typography.Ui;
        _addTask.FontSize = Typography.ControlSize;
        Typography.BlockLineHeight(_addTask, Typography.ControlLineHeight);
        _done.FontFamily = Typography.Ui;
        _done.FontSize = Typography.ControlSize;
        Typography.BlockLineHeight(_done, Typography.ControlLineHeight);
        foreach (var row in _rows)
        {
            row.Text.FontFamily = Typography.ForText(row.Text.Text);
            row.Text.FontSize = Typography.BodySize;
            Typography.ApplyText(row.Text);
        }
    }

    private void Touch()
    {
        if (_updating || _syncing) return;
        _dirty = true;
        _save.Stop();
        _save.Start();
    }

    private void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Notes.Update(_path, _data.Title, _data.Tasks, _color);
            _line.ReloadThumbnail(_path);
        }
        catch (Exception e)
        {
            _dirty = true;
            Log.Error($"Could not save the note: {e.Message}");
        }
    }

    private void CloseDetached()
    {
        _save.Stop();
        _dirty = false;
        _skipSaveOnClose = true;
        Close();
    }

    private static Button PaperButton(string label, bool accent)
    {
        var button = new Button
        {
            Content = label,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(accent ? Colors.White : Ink),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = true,
        };
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.Name = "Surface";
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        root.AppendChild(surface);
        var focus = new FrameworkElementFactory(typeof(Border));
        focus.Name = "Focus";
        focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        focus.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        focus.SetValue(Border.BorderBrushProperty, new SolidColorBrush(WithAlpha(0xB0, Ink)));
        focus.SetValue(UIElement.OpacityProperty, 0d);
        focus.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        root.AppendChild(focus);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(
            FrameworkElement.HorizontalAlignmentProperty,
            new System.Windows.Data.Binding(nameof(Control.HorizontalContentAlignment))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            });
        presenter.SetBinding(
            FrameworkElement.VerticalAlignmentProperty,
            new System.Windows.Data.Binding(nameof(Control.VerticalContentAlignment))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            });
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding(nameof(ContentControl.ContentTemplate))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        root.AppendChild(presenter);
        template.VisualTree = root;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(accent
            ? Color.FromRgb(0x4A, 0x70, 0x56)
            : WithAlpha(0x16, Ink)), "Surface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "Surface"));
        template.Triggers.Add(pressed);
        var keyboard = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        keyboard.Setters.Add(new Setter(UIElement.OpacityProperty, 1d, "Focus"));
        template.Triggers.Add(keyboard);
        button.Template = template;
        return button;
    }

    private static FrameworkElement TrashGlyph(Color ink)
    {
        var grid = new Grid { Width = 15, Height = 16, IsHitTestVisible = false };
        var brush = new SolidColorBrush(ink);
        grid.Children.Add(Morphicons.CreatePath(Morphicon.Trash2, 15, 1.25, brush));
        return grid;
    }

    private static Grid PaperclipGlyph(Color ink)
    {
        var grid = new Grid { Width = 15, Height = 16, IsHitTestVisible = false };
        grid.Children.Add(Morphicons.CreatePath(
            Morphicon.Paperclip,
            15,
            1.3,
            new SolidColorBrush(ink)));
        return grid;
    }

    private static Grid CloseGlyph(Color ink)
    {
        var grid = new Grid { Width = 12, Height = 12, IsHitTestVisible = false };
        grid.Children.Add(Morphicons.CreatePath(
            Morphicon.X,
            12,
            1.25,
            new SolidColorBrush(ink)));
        return grid;
    }

    private static FlowDirection TextFlow(string text) =>
        text.Any(c => c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F')
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    private static Color WithAlpha(byte alpha, Color color) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private sealed class TaskRow
    {
        public TaskRow(
            NoteTask task,
            CheckBox check,
            TextBox text,
            Border strike,
            Button attach,
            Button delete,
            IReadOnlyList<Button> attachmentRemovers,
            Grid root)
        {
            Task = task;
            Check = check;
            Text = text;
            Strike = strike;
            Attach = attach;
            Delete = delete;
            AttachmentRemovers = attachmentRemovers;
            Root = root;
        }

        public NoteTask Task { get; }
        public CheckBox Check { get; }
        public TextBox Text { get; }
        public Border Strike { get; }
        public Button Attach { get; }
        public Button Delete { get; }
        public IReadOnlyList<Button> AttachmentRemovers { get; }
        public Grid Root { get; }
    }
}
