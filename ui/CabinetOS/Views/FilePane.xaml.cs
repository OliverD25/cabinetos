using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS.Views;

/// <summary>
/// One file pane (design view A): header, column headings, and the rows in a
/// virtualizing <c>ItemsRepeater</c>. Only the rows on screen exist. Keys the
/// window's keymap does not claim arrive here: the arrows move the focus and
/// the selection like in any Windows list (Shift extends, Ctrl moves the focus
/// alone), or keep the marks as in Total Commander (<c>panes.selection:
/// commander</c>, <see cref="SelectionModel.KeyMode"/>); Insert toggles a row
/// as in Total Commander, and the file keys run commands through the router.
/// </summary>
public sealed partial class FilePane : UserControl
{
    private const string Target = "cabinetos_ui::pane";

    // Screens of rows the list keeps made above and below its view: ItemsRepeater's VerticalCacheLength.
    private const double RowCacheScreens = 0.5;

    private readonly HashSet<FileRow> _realized = [];
    // The theme's rowHeight, and the list's inset: 4 px inside a card, none under hairlines.
    private double _rowHeight = 30;
    private double _listPadding = 4;
    private readonly DispatcherQueueTimer _noteTimer;
    private int _preparedFirst = int.MaxValue;
    private int _preparedLast = -1;
    private bool _detailsQueued;
    private PaneModel? _model;
    private string? _shownPath;
    private NavigationTiming? _timing;
    private long _firstRowTicks;
    private bool _renderingHooked;

    /// <summary>Whether a listing was bound and its first frame has not been drawn yet: "listing shown" is not logged until then (the snapshot aid's <c>until:listing-drawn</c>).</summary>
    internal bool TimingPending => _timing is not null;
    private TaskCompletionSource<string?>? _rename;
    private bool _clearButtonHidden;
    private int _renameIndex = -1;
    private bool _newRow;
    private string _renameOriginal = "";
    private int _noteIndex = -1;
    private bool _searchShown;
    private readonly RowFactory _rows;
    private int _rowsAhead;
    private readonly PendingCursorKeys _cursorKeys = new();
    private bool _cursorKeysHooked;
    private PaneColumns? _columns;

    /// <summary>Creates the pane; <see cref="Model"/> gives it its content.</summary>
    public FilePane()
    {
        InitializeComponent();
        _rows = new RowFactory((DataTemplate)Resources["RowTemplate"], DispatcherQueue);
        Repeater.ItemTemplate = _rows;
        // Half a screen of rows kept made above and below the view, not WinUI's two (speed review, finding 3): a density change
        // (Commander Compact's 20 px rows) measures every made row again, and a fast scroll-bar drag still finds rows to show.
        Repeater.VerticalCacheLength = RowCacheScreens;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.Tapped += OnRowTapped;
        Repeater.DoubleTapped += OnRowDoubleTapped;
        Frame.RightTapped += OnRightTapped;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => Activated?.Invoke(this);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            // A click acts where the waiting cursor keys leave the cursor.
            ApplyCursorKeys();
            Focus(FocusState.Pointer);
        }), handledEventsToo: true);
        Scroller.ViewChanged += (_, _) => PositionEditors();
        EditLayer.SizeChanged += (_, e) => EditLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameBox.LostFocus += (_, _) => EndRename(commit: true);
        WholeVolumeBox.Click += (_, _) =>
            _ = Run("search.scope", CommandArgs.Object(("wholeVolume", WholeVolumeBox.IsChecked == true)), "button");
        _noteTimer = DispatcherQueue.CreateTimer();
        _noteTimer.IsRepeating = false;
        _noteTimer.Interval = TimeSpan.FromSeconds(3);
        _noteTimer.Tick += (_, _) => HideRowNote();
        SetUpColumnGrips();
        ApplyMetrics();
    }

    /// <summary>
    /// Lays the pane out with the window's sizes and chrome now (docs/ui.md,
    /// "Metrics and chrome"): the frame's corners, the column headers, the
    /// rows and the scroll arithmetic, the text boxes over a row. Under
    /// hairlines the column headers are filled and the list has no inset.
    /// </summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        var hairlines = WindowMetrics.Chrome.Hairlines;
        _rowHeight = m.RowHeight;
        _listPadding = hairlines ? 0 : 4;
        FontSize = m.FontSize;
        Frame.CornerRadius = WindowMetrics.Corners(m.RadiusSurface);
        SearchBarGrid.Padding = new Thickness(m.ColumnHeaderPaddingX, 4, m.ColumnHeaderPaddingX, 6);
        SearchNoteText.LineHeight = SearchNoteText.FontSize * m.LineHeight;
        ColumnHeader.Background = hairlines ? ThemeResources.Brush("CbBarFillBrush") : null;
        ColumnHeader.BorderBrush = ThemeResources.Brush(hairlines ? "CbHairlineStrongBrush" : "CbDividerBrush");
        ColumnGrid.Padding = WindowMetrics.Pad(m.ColumnHeaderPaddingX, m.ColumnHeaderPaddingY);
        ColumnGrid.ColumnSpacing = m.ColumnGap;
        WindowMetrics.SetColumns(HeadNameColumn, HeadModifiedColumn, HeadTypeColumn, HeadSizeColumn);
        // Centred on the divider, in the gap before the column: the header's full height, the padding included.
        var gripMargin = new Thickness(-(m.ColumnGap / 2) - (ColumnGrip.HitWidth / 2), -m.ColumnHeaderPaddingY, 0, -m.ColumnHeaderPaddingY);
        foreach (var grip in Grips)
        {
            grip.Margin = gripMargin;
        }
        Scroller.Padding = new Thickness(_listPadding);
        MessageText.LineHeight = m.FontSize * m.LineHeight;
        NewRow.Height = m.RowHeight;
        NewRow.CornerRadius = WindowMetrics.Corners(m.RowRadius);
        // The name box covers the row's name: as high as the row leaves room for, but never lower than its text.
        RenameBox.FontSize = m.FontSize;
        RenameBox.Height = Math.Max(m.RowHeight - 4, Math.Ceiling(m.FontSize * 1.33) + 2);
        RenameBox.Padding = new Thickness(6, WindowMetrics.TextTop(RenameBox.Height, m.FontSize), 6, 0);
        RenameBox.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        RowNote.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        UpdateActivity();
        foreach (var row in _realized)
        {
            row.ApplyMetrics(rebind: true);
        }
        Repeater.InvalidateMeasure();
        ColumnsHost?.ApplyMetrics();
        PositionEditors();
    }

    // ----- The column view (ADR 0016; docs/ui.md, "The column view") -----

    /// <summary>
    /// Shows the front tab's folder as <paramref name="columns"/>, in the
    /// list's place, or the list again (null). The keyboard's column shows
    /// the model's own rows and selection, so the list's keys work there.
    /// </summary>
    internal void ShowColumns(PaneColumns? columns)
    {
        if (ReferenceEquals(_columns, columns))
        {
            return;
        }
        _columns = columns;
        if (columns is not null && ColumnsHost is null)
        {
            FindName(nameof(ColumnsHost));
            ColumnsHost!.RowTapped += (column, index) => _ = _columns?.ClickAsync(column, index, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift));
            ColumnsHost.RowDoubleTapped += (column, index, folder) => _ = OpenFromColumnAsync(column, index, folder);
            ColumnsHost.RowRightTapped += (column, index, point) => _ = MenuFromColumnAsync(column, index, point, IsDown(VirtualKey.Shift));
        }
        EndRename(commit: false);
        HideRowNote();
        if (ColumnsHost is { } host)
        {
            host.Columns = columns;
        }
        UpdateBody();
        ApplyRows();
    }

    /// <summary>The columns of the tab in front, when it shows them.</summary>
    internal PaneColumns? Columns => _columns;

    /// <summary>The column view on screen, or null (the snapshot aid's log).</summary>
    internal ColumnView? ColumnViewShown => ShowsColumnView ? ColumnsHost : null;

    /// <summary>
    /// The columns' rows, cursor and marks changed (a column opened, the
    /// keyboard moved, a column was listed again); with
    /// <paramref name="bringIntoView"/> the keyboard's column comes into view.
    /// </summary>
    internal void SyncColumns(bool bringIntoView)
    {
        if (ShowsColumnView)
        {
            ColumnsHost!.Sync(bringIntoView);
        }
    }

    // A search shows its hits in the list, over the columns too; the columns come back when it ends.
    private bool ShowsColumnView => _columns is not null && _model is { Search: null } && ColumnsHost is not null;

    private void UpdateBody()
    {
        var columns = ShowsColumnView;
        if (ColumnsHost is { } host)
        {
            host.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
        }
        ColumnHeader.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
        Scroller.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
        if (columns)
        {
            MessageText.Visibility = Visibility.Collapsed;
        }
    }

    // A double-click on a file opens it, as in the list; on a folder the click before it opened the column already.
    private async Task OpenFromColumnAsync(int column, int index, bool folder)
    {
        if (!folder && _columns is { } columns && await columns.PointAtAsync(column, index, keepSelection: false))
        {
            await Run("pane.openSelected", trigger: "mouse");
        }
    }

    // A right-click in a column: the keyboard goes there, then the menu opens for the row, or for the column's folder.
    private async Task MenuFromColumnAsync(int column, int index, Point point, bool shift)
    {
        Focus(FocusState.Pointer);
        if (_columns is { } columns && await columns.PointAtAsync(column, index, keepSelection: true))
        {
            (shift ? ShellMenuRequested : ContextMenuRequested)?.Invoke(this, index, point);
        }
    }

    /// <summary>Raised when the pane gets the focus: it becomes the active pane.</summary>
    public event Action<FilePane>? Activated;

    /// <summary>
    /// Raised for a context menu: the row (-1 for the pane's empty space) and
    /// where the pointer was, in the window's coordinates (null from the keyboard).
    /// </summary>
    public event Action<FilePane, int, Point?>? ContextMenuRequested;

    /// <summary>
    /// Raised for Windows' own menu (Shift+right-click, Phase 18): the row (-1 for the pane's
    /// empty space) and where the pointer was, in the window's coordinates.
    /// </summary>
    public event Action<FilePane, int, Point?>? ShellMenuRequested;

    /// <summary>Runs a command by ID through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Whether a name is being edited in place.</summary>
    public bool IsRenaming => _rename is not null;

    /// <summary>The pane's model.</summary>
    public PaneModel? Model
    {
        get => _model;
        set
        {
            // Keys pressed before the pane shows another model belong to the one it showed.
            ApplyCursorKeys();
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
                _model.DetailsArrived -= OnDetailsArrived;
            }
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
                _model.DetailsArrived += OnDetailsArrived;
            }
            UpdateActivity();
            ApplyRows();
            UpdateSortGlyphs();
        }
    }

    /// <summary>
    /// Edits row <paramref name="index"/>'s name in place (F2, or a new folder).
    /// Returns the new name, or null when the user cancelled or left it as it
    /// was. For a file only the part before the extension is selected, as in Explorer.
    /// </summary>
    public Task<string?> BeginRenameAsync(int index, string name, bool selectStem)
    {
        EndRename(commit: false);
        var pending = new TaskCompletionSource<string?>();
        _rename = pending;
        _renameIndex = index;
        _renameOriginal = name;
        ScrollIntoView(PositionOf(index));
        UpdateLayout();
        RenameBox.Text = name;
        RenameBox.Visibility = Visibility.Visible;
        HideClearButton();
        PositionEditors();
        FocusRenameBox(pending, name);
        RenameBox.Select(0, DisplayFormat.RenameStem(name, isFolder: !selectStem));
        return pending.Task;
    }

    /// <summary>
    /// Gives the name box the keyboard. Focus can be refused while the box,
    /// just shown, has not been laid out; then it is asked again after the
    /// next layout pass. The log says which it was ("rename box shown"), and
    /// the live check types only after that line.
    /// </summary>
    private void FocusRenameBox(TaskCompletionSource<string?> pending, string name)
    {
        var focused = RenameBox.Focus(FocusState.Programmatic);
        if (!focused)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_rename == pending && RenameBox.Visibility == Visibility.Visible)
                {
                    RenameBox.Focus(FocusState.Programmatic);
                }
            });
        }
        Diag.Info(Target, "rename box shown", new LogField("name", name), new LogField("focused", focused));
    }

    /// <summary>
    /// New Text File (Shift+F4): a name box over a row the listing does not
    /// have yet, at the top of the rows on screen, with the part of
    /// <paramref name="name"/> before its extension selected. Returns the name
    /// typed (the suggestion too), or null when the user cancelled.
    /// </summary>
    public Task<string?> BeginNewNameAsync(string name)
    {
        EndRename(commit: false);
        var pending = new TaskCompletionSource<string?>();
        _rename = pending;
        _renameIndex = -1;
        _newRow = true;
        // Nothing to compare with: the suggestion itself is a name to take.
        _renameOriginal = "";
        NewRow.Visibility = Visibility.Visible;
        RenameBox.Text = name;
        RenameBox.Visibility = Visibility.Visible;
        HideClearButton();
        UpdateLayout();
        PositionEditors();
        FocusRenameBox(pending, name);
        RenameBox.Select(0, DisplayFormat.RenameStem(name, isFolder: false));
        return pending.Task;
    }

    /// <summary>Stops an edit without renaming (Esc).</summary>
    public void CancelRename() => EndRename(commit: false);

    /// <summary>How far down the list is scrolled, in pixels: what a tab keeps when it goes behind.</summary>
    public double ScrollOffset => Scroller.VerticalOffset;

    /// <summary>Scrolls the list to <paramref name="offset"/> pixels once its rows are laid out (a tab that comes to the front).</summary>
    public void ScrollTo(double offset)
    {
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, offset), null, disableAnimation: true);
        if (Scroller.VerticalOffset + 0.5 < Math.Min(offset, Scroller.ScrollableHeight))
        {
            // The repeater lays the rows out a moment later: try again then.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => Scroller.ChangeView(null, Math.Max(0, offset), null, disableAnimation: true));
        }
    }

    // WinUI's text box shows a clear button (×) while it has the focus. A name edited in
    // place has none in the design, and a click on it would move the focus and end the edit.
    private void HideClearButton()
    {
        if (_clearButtonHidden)
        {
            return;
        }
        RenameBox.ApplyTemplate();
        if (FindPart(RenameBox, "DeleteButton") is Button clear)
        {
            clear.MaxWidth = 0;
            clear.IsTabStop = false;
            clear.IsHitTestVisible = false;
            _clearButtonHidden = true;
        }

        static DependencyObject? FindPart(DependencyObject parent, string name)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement { Name: var childName } && childName == name)
                {
                    return child;
                }
                if (FindPart(child, name) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
    }

    /// <summary>Types <paramref name="text"/> into the edit and presses Enter (development snapshots).</summary>
    public void CommitRename(string text)
    {
        if (IsRenaming)
        {
            RenameBox.Text = text;
            EndRename(commit: true);
        }
    }

    /// <summary>Shows a red note under row <paramref name="index"/> for 3 s: why a rename or a new folder failed.</summary>
    public void ShowRowNote(int index, string text)
    {
        _noteIndex = index;
        RowNoteText.Text = text;
        RowNote.Visibility = Visibility.Visible;
        PositionEditors();
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    /// <summary>
    /// Where a context menu opened from the keyboard hangs: the row's bottom-left, at the name column's left edge, in the
    /// window's coordinates. The menu's top-left corner goes there, as Explorer's does under the pointer.
    /// </summary>
    public Point RowAnchor(int index)
    {
        var (left, _, bottom) = RowEdges(index);
        return new Point(left, bottom);
    }

    /// <summary>
    /// The row's name column's left edge, and the row's top and bottom, in the window's coordinates, for a menu opened from
    /// the keyboard. A row that is not on screen gives a point near the top of the pane, twice: the list makes rows ahead
    /// of the view, and a made row scrolled out of it lies outside the window.
    /// </summary>
    public (double Left, double Top, double Bottom) RowEdges(int index)
    {
        if (ShowsColumnView && index >= 0 && PositionOf(index) is >= 0 and var shown && ColumnsHost!.KeyboardList?.RowAt(shown) is { } columnRow)
        {
            var rowTop = columnRow.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var nameLeft = columnRow.NameElement.TransformToVisual(null).TransformPoint(new Point(0, 0)).X;
            return (nameLeft, rowTop.Y, rowTop.Y + _rowHeight);
        }
        if (index >= 0 && PositionOf(index) is >= 0 and var position && Repeater.TryGetElement(position) is FileRow row)
        {
            var top = row.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var view = Scroller.TransformToVisual(null).TransformBounds(new Rect(0, 0, Scroller.ActualWidth, Scroller.ActualHeight));
            if (top.Y < view.Bottom && top.Y + _rowHeight > view.Top)
            {
                var name = row.NameElement.ActualWidth > 0
                    ? row.NameElement.TransformToVisual(null).TransformPoint(new Point(0, 0)).X
                    : top.X + 40;
                return (name, top.Y, top.Y + _rowHeight);
            }
        }
        var fallback = Frame.TransformToVisual(null).TransformPoint(new Point(40, 80));
        return (fallback.X, fallback.Y, fallback.Y);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneModel.Rows):
                ApplyRows();
                break;
            case nameof(PaneModel.Selection):
                MarkSelection();
                break;
            case nameof(PaneModel.ShowsActiveStroke) or nameof(PaneModel.HasBrightTitle):
                UpdateActivity();
                SyncColumns(bringIntoView: false);
                MarkSelection();
                break;
            case nameof(PaneModel.Message) or nameof(PaneModel.Find):
                UpdateMessage();
                break;
            case nameof(PaneModel.Search):
                ApplySearch();
                UpdateSortGlyphs();
                break;
            case nameof(PaneModel.EffectiveSort):
                UpdateSortGlyphs();
                break;
            case nameof(PaneModel.Sizes):
                foreach (var row in _realized)
                {
                    row.RefreshSize();
                }
                break;
        }
    }

    // A chevron on the column the listing is sorted by: up from A to Z (smallest, oldest), down the
    // other way. The design's plain headings stay for its default, name from A to Z, and for search hits.
    private void UpdateSortGlyphs()
    {
        var sort = _model is { Search: null } model ? model.EffectiveSort : null;
        if (sort is { Key: PaneSort.Name, Descending: false })
        {
            sort = null;
        }
        Show(NameSortGlyph, sort?.Key == PaneSort.Name);
        Show(ModifiedSortGlyph, sort?.Key == PaneSort.Modified);
        Show(TypeSortGlyph, sort?.Key is PaneSort.Extension or PaneSort.Kind);
        Show(SizeSortGlyph, sort?.Key == PaneSort.Size);
        ToolTipService.SetToolTip(TypeSortGlyph, sort?.Key == PaneSort.Extension ? "Sorted by extension" : "Sorted by kind");

        void Show(FontIcon glyph, bool shown)
        {
            glyph.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            glyph.Glyph = sort?.Descending == true ? "" : "";
        }
    }

    // Search mode (docs/ui.md, "Search"): the hits in the same rows, the search's title and
    // note in the header, "Folder" where a listing has "Modified". The listing waits underneath.
    private void ApplySearch()
    {
        if (_model is null)
        {
            return;
        }
        if (_model.Search is not { } search)
        {
            if (_searchShown)
            {
                _searchShown = false;
                SearchBar.Visibility = Visibility.Collapsed;
                SecondHeading.Text = "Modified";
                UpdateBody();
                ApplyRows();
                ScrollToFocus();
            }
            return;
        }
        var entering = !_searchShown;
        _searchShown = true;
        EndRename(commit: false);
        HideRowNote();
        UpdateBody();
        SearchBar.Visibility = Visibility.Visible;
        SecondHeading.Text = "Folder";
        // The search's title and what it searched, where the pane's header had them.
        SearchTitleText.Text = search.Header;
        ToolTipService.SetToolTip(SearchTitleText, search.Scope.Length > 0 ? search.Scope : null);
        SearchNoteText.Text = search.Note;
        SearchNoteText.Visibility = search.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WholeVolumeBox.IsChecked = search.WholeVolume;
        if (entering || !ReferenceEquals(Repeater.ItemsSource, search.Rows))
        {
            Repeater.ItemsSource = search.Rows;
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        var nothing = search.Rows is { Count: 0 };
        MessageText.Text = nothing ? "Nothing matches." : "";
        MessageText.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        MarkSelection();
    }

    private void UpdateActivity()
    {
        var state = _model switch
        {
            { ShowsActiveStroke: true } => "Active",
            { HasBrightTitle: true } => "OnlyPane",
            _ => "Inactive",
        };
        VisualStateManager.GoToState(this, state, false);
    }

    // "This folder is empty." says nothing while a find filter shows no row: the widget's count says so.
    private void UpdateMessage()
    {
        if (ShowsColumnView)
        {
            // Each column says it is empty itself.
            MessageText.Visibility = Visibility.Collapsed;
            return;
        }
        var message = _model is { Selection.IsFiltered: false } model ? model.Message : null;
        MessageText.Text = message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyRows()
    {
        if (_model?.Search is not null)
        {
            // The listing changed under the search results; it shows when the search is left.
            return;
        }
        if (ShowsColumnView)
        {
            // The keyboard's column shows the rows; the list shows them again, from the top, when the tab leaves the columns.
            if (_model is not null)
            {
                _model.PendingTiming = null;
            }
            Repeater.ItemsSource = null;
            _shownPath = null;
            UpdateMessage();
            if (!_columns!.IsSwapping)
            {
                ColumnsHost!.Sync(bringIntoView: false);
            }
            return;
        }
        _timing = _model?.PendingTiming;
        if (_model is not null)
        {
            _model.PendingTiming = null;
        }
        _firstRowTicks = 0;
        Repeater.ItemsSource = _model?.Rows;
        UpdateMessage();
        if (!string.Equals(_shownPath, _model?.Path, StringComparison.OrdinalIgnoreCase))
        {
            // Another folder starts at its top (a refresh keeps the scroll position).
            _shownPath = _model?.Path;
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
            EndRename(commit: false);
            HideRowNote();
            if (_model is { FocusIndex: > 0 } model)
            {
                ScrollIntoView(PositionOf(model.FocusIndex));
            }
        }
        else if (IsRenaming && !_newRow)
        {
            // A refresh while editing: the row may have moved, or gone.
            var index = _model?.View?.IndexOfName(_renameOriginal) ?? -1;
            if (index < 0)
            {
                EndRename(commit: false);
            }
            else
            {
                _renameIndex = index;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionEditors);
            }
        }
        if (_timing is not null && !_renderingHooked)
        {
            CompositionTarget.Rendering += OnFirstFrame;
            _renderingHooked = true;
        }
        MarkSelection();
    }

    /// <summary>Shows the icon of <paramref name="key"/> in the rows that wait for it.</summary>
    public void RefreshIcon(string key)
    {
        using var timed = FrameParts.Time(FramePart.Icons);
        foreach (var row in _realized)
        {
            if (string.Equals(row.IconKey, key, StringComparison.Ordinal))
            {
                row.RefreshDetails();
            }
        }
        ColumnsHost?.RefreshIcon(key);
    }

    /// <summary>Binds every row's type name and icon again (the screen's scale changed the icon size).</summary>
    public void RefreshDetails()
    {
        foreach (var row in _realized)
        {
            row.RefreshDetails();
        }
        ColumnsHost?.RefreshDetails();
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var started = FrameParts.Start();
        if (args.Element is FileRow row)
        {
            // The row is bound here, not in its own DataContextChanged: with that event, a held
            // PageDown had WinUI ask .NET for a full garbage collection every 2 to 3 s, pausing
            // the UI thread for 17 to 25 ms; without it, none in 20 s (docs/ui.md, "Scrolling").
            row.Show(row.DataContext);
            // Rows for two pages ahead, made while the window is idle (RowFactory).
            var ahead = 2 * (RowsPerPage() + 2);
            if (ahead > _rowsAhead)
            {
                _rowsAhead = ahead;
                _rows.MakeAhead(ahead);
            }
            _realized.Add(row);
            Mark(row, row.Index);
            row.DragStartingRow -= OnRowDragStarting;
            row.DragStartingRow += OnRowDragStarting;
            row.DropCompletedRow -= OnRowDropCompleted;
            row.DropCompletedRow += OnRowDropCompleted;
        }
        if (_timing is not null && _firstRowTicks == 0)
        {
            _firstRowTicks = Stopwatch.GetTimestamp();
        }
        // One describe_entries per layout pass for all the rows it made, not one per row.
        _preparedFirst = Math.Min(_preparedFirst, args.Index);
        _preparedLast = Math.Max(_preparedLast, args.Index);
        if (!_detailsQueued)
        {
            _detailsQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AskForDetails);
        }
        FrameParts.Stop(FramePart.Bind, started);
    }

    private void AskForDetails()
    {
        var started = FrameParts.Start();
        _detailsQueued = false;
        var (first, last) = (_preparedFirst, _preparedLast);
        _preparedFirst = int.MaxValue;
        _preparedLast = -1;
        // Search hits are not entries of the listing: their types come from what is known per extension.
        // Under a find filter the rows shown are not neighbours in the listing: the model maps them.
        if (last >= first && _model is { Search: null } model)
        {
            model.EnsureShownDetails(first, last);
        }
        FrameParts.Stop(FramePart.Requests, started);
    }

    private void OnDetailsArrived(int from, int count)
    {
        var started = FrameParts.Start();
        foreach (var row in _realized)
        {
            if (row.Index >= from && row.Index < from + count)
            {
                row.RefreshDetails();
            }
        }
        if (ShowsColumnView)
        {
            ColumnsHost!.RefreshKeyboardDetails(from, count);
        }
        FrameParts.Stop(FramePart.Details, started);
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        var started = FrameParts.Start();
        if (args.Element is FileRow row)
        {
            _realized.Remove(row);
            row.IsSelected = false;
            row.ShowsCursor = false;
        }
        FrameParts.Stop(FramePart.Bind, started);
    }

    // Logs "listing shown" at the first frame after the first row exists: from
    // the key press (or the start of navigation) to pixels, for Phase 5's check.
    private void OnFirstFrame(object? sender, object e)
    {
        if (_timing is not { } timing)
        {
            Unhook();
            return;
        }
        // A pane that is not drawn (under an editor or the marketplace) makes no rows; waiting
        // longer than 5 s would keep this per-frame callback, and the render loop, running.
        if (_firstRowTicks == 0 && (_model?.Count ?? 0) > 0 && Stopwatch.GetElapsedTime(timing.StartedTicks) < TimeSpan.FromSeconds(5))
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        Diag.Request(LogLevel.Info, timing.RequestId, Target, "listing shown",
            new LogField("path", timing.Path),
            new LogField("entries", timing.Entries),
            new LogField("core_us", timing.CoreUs),
            new LogField("reply_ms", Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, timing.RepliedTicks).TotalMilliseconds, 1)),
            new LogField("first_row_ms", _firstRowTicks == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, _firstRowTicks).TotalMilliseconds, 1)),
            new LogField("first_frame_ms", Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, now).TotalMilliseconds, 1)),
            new LogField("kept", timing.Kept));
        _timing = null;
        Unhook();

        void Unhook()
        {
            CompositionTarget.Rendering -= OnFirstFrame;
            _renderingHooked = false;
        }
    }

    private void MarkSelection()
    {
        var started = FrameParts.Start();
        foreach (var row in _realized)
        {
            Mark(row, row.Index);
        }
        if (ShowsColumnView && !_columns!.IsSwapping)
        {
            ColumnsHost!.MarkKeyboard();
        }
        FrameParts.Stop(FramePart.Selection, started);
    }

    private void Mark(FileRow row, int index)
    {
        if (_model is not { } model)
        {
            return;
        }
        var selection = model.CurrentSelection;
        var selected = selection.IsSelected(index);
        row.IsSelected = selected;
        // The outline marks the keyboard's row when the fill alone would not say which it is.
        row.ShowsCursor = index == selection.Focus && model.IsActive && (!selected || selection.SelectedCount > 1);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is null || e.OriginalSource is TextBox)
        {
            return;
        }
        // The file keys (Enter, F2, Delete, Ctrl+C, Insert, …) are bindings in the
        // core's keymap and run before this handler; what arrives here moves
        // through the list, as in any Windows list, or marks as Total Commander
        // does (panes.selection: commander).
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (CursorKeyOf(e.Key) is { } cursorKey)
        {
            // Made at the start of the next frame, so a page's new rows fit in it (PendingCursorKeys).
            _cursorKeys.Add(cursorKey, shift, ctrl);
            if (!_cursorKeysHooked)
            {
                _cursorKeysHooked = true;
                CompositionTarget.Rendering += OnFrameForCursorKeys;
            }
            e.Handled = true;
            return;
        }
        // Every other key sees the cursor where the keys before it left it.
        ApplyCursorKeys();
        var selection = _model.CurrentSelection;
        var focus = selection.Focus;
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Home:
                MoveFocus(selection.IndexAt(0), selection.KeyMode(shift, ctrl, toListEnd: true));
                break;
            case VirtualKey.End:
                MoveFocus(selection.IndexAt(_model.ShownCount - 1), selection.KeyMode(shift, ctrl, toListEnd: true));
                break;
            case VirtualKey.Back:
                // A file manager convention the keymap does not hold (go.up is Alt+Up there).
                _ = Run("go.up");
                break;
            // The column view: Left to the parent column, Right into the column of the cursor's folder or opening it.
            case VirtualKey.Left when ShowsColumnView && !shift && !ctrl:
                _ = _columns!.MoveLeftAsync();
                break;
            case VirtualKey.Right when ShowsColumnView && !shift && !ctrl:
                _ = _columns!.RightAsync(null);
                break;
            case VirtualKey.F10 when shift:
            case VirtualKey.Application:
                // Hits have no menu: Enter goes to one, Esc back to the folder.
                if (_model.Search is null)
                {
                    ContextMenuRequested?.Invoke(this, focus, null);
                }
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    /// <summary>Scrolls the focused row into view (after Insert moved it).</summary>
    public void ScrollToFocus()
    {
        if (_model is not null)
        {
            ScrollIntoView(PositionOf(_model.CurrentSelection.Focus));
        }
    }

    // Where listing row `index` is in the list shown: itself, or its place among a find filter's rows (-1 when hidden).
    private int PositionOf(int index) => _model is { } model && index >= 0 ? model.CurrentSelection.PositionOf(index) : index;

    private Task Run(string commandId, JsonElement? args = null, string trigger = "key") =>
        RunCommand?.Invoke(commandId, args, trigger) ?? Task.CompletedTask;

    private void MoveFocus(int index, SelectMode mode)
    {
        if (_model is null || _model.ShownCount == 0)
        {
            return;
        }
        _model.CurrentSelection.MoveTo(index, mode);
        ScrollIntoView(PositionOf(_model.CurrentSelection.Focus));
    }

    private int RowsPerPage() => ShowsColumnView && ColumnsHost!.KeyboardList is { } column
        ? Math.Max(1, (int)((column.ViewportHeight - (2 * column.ListPadding)) / _rowHeight) - 1)
        : Math.Max(1, (int)((Scroller.ViewportHeight - (2 * _listPadding)) / _rowHeight) - 1);

    private static CursorKey? CursorKeyOf(VirtualKey key) => key switch
    {
        VirtualKey.Up => CursorKey.Up,
        VirtualKey.Down => CursorKey.Down,
        VirtualKey.PageUp => CursorKey.PageUp,
        VirtualKey.PageDown => CursorKey.PageDown,
        _ => null,
    };

    private void OnFrameForCursorKeys(object? sender, object e) => ApplyCursorKeys();

    /// <summary>
    /// Makes the cursor keys that wait for the next frame now, in order. The
    /// window calls it before anything that reads the cursor: a command, a
    /// typed letter of a quick search; the pane before a click and its other keys.
    /// </summary>
    public void ApplyCursorKeys()
    {
        if (_cursorKeysHooked)
        {
            _cursorKeysHooked = false;
            CompositionTarget.Rendering -= OnFrameForCursorKeys;
        }
        while (_cursorKeys.TryTake(out var key))
        {
            if (_model is null)
            {
                continue;
            }
            var (index, mode) = PendingCursorKeys.Target(_model.CurrentSelection, key.Key, key.Shift, key.Ctrl, RowsPerPage());
            MoveFocus(index, mode);
        }
    }

    /// <summary>The snapshot aid's <c>layout:</c> step: the sizes the metrics set here, to compare two looks by.</summary>
    internal string SizeSignature() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"columns={ColumnHeader.ActualHeight:0.#}/{ColumnGrid.Padding.Left:0.#} radius={Frame.CornerRadius.TopLeft:0.#} inset={Scroller.Padding.Left:0.#} font={FontSize:0.#} row={_rowHeight:0.#}");

    /// <summary>
    /// The snapshot aid's <c>layout:</c> step: the list's height, how many rows
    /// it shows whole and how high they are laid out, and the texts of those
    /// rows cut short with "…" (a column too narrow for its text).
    /// </summary>
    internal (double Viewport, int WholeRows, double RowHeight, IReadOnlyList<string> Trimmed) MeasureRows()
    {
        var viewport = Scroller.ViewportHeight;
        var whole = 0;
        var height = 0.0;
        var trimmed = new List<string>();
        foreach (var row in _realized.OrderBy(r => r.Index))
        {
            var top = row.TransformToVisual(Scroller).TransformPoint(new Point(0, 0)).Y;
            if (row.ActualHeight <= 0 || top < -0.5 || top + row.ActualHeight > viewport + 0.5)
            {
                continue;
            }
            whole++;
            height = row.ActualHeight;
            trimmed.AddRange(row.TrimmedTexts());
        }
        return (viewport, whole, height, trimmed);
    }

    /// <summary>The rows one PageDown moves now (the snapshot aid's scroll run reports it).</summary>
    public int RowsPerPageNow => RowsPerPage();

    /// <summary>PageDown as the key does it: the focus moves a page, alone selected (the snapshot aid's <c>scroll:</c> step).</summary>
    public void PageDownForSnapshot()
    {
        if (_model is not null)
        {
            MoveFocus(PendingCursorKeys.Target(_model.CurrentSelection, CursorKey.PageDown, shift: false, ctrl: false, RowsPerPage()).Index, SelectMode.Single);
        }
    }

    private void ScrollIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }
        if (ShowsColumnView)
        {
            ColumnsHost!.ScrollKeyboardRowIntoView(index);
            return;
        }
        var top = _listPadding + (index * _rowHeight);
        var bottom = top + _rowHeight;
        var offset = Scroller.VerticalOffset;
        var viewport = Scroller.ViewportHeight;
        if (top - _listPadding < offset)
        {
            Scroller.ChangeView(null, Math.Max(0, top - _listPadding), null, disableAnimation: true);
        }
        else if (bottom + _listPadding > offset + viewport)
        {
            Scroller.ChangeView(null, bottom + _listPadding - viewport, null, disableAnimation: true);
        }
    }

    /// <summary>Rows of this pane are being dragged (true) or the drag ended (false): the window then shows where they may be dropped.</summary>
    public event Action<bool>? DragChanged;

    // The rows that go with a drag: the selection when the dragged row is part of it, else that row alone.
    // Their paths travel as text, one per line, so a drop needs nothing but the drag's own data.
    private void OnRowDragStarting(FileRow row, DragStartingEventArgs e)
    {
        if (_model is not { Search: null } model || row.Index < 0 || model.EntryAt(row.Index) is not { } entry)
        {
            e.Cancel = true;
            return;
        }
        var paths = model.CurrentSelection.IsSelected(row.Index)
            ? model.Targets().Select(t => t.Path).ToList()
            : [entry.Path];
        e.Data.SetText(string.Join("\r\n", paths));
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        Diag.Info(Target, "row drag started", new LogField("rows", paths.Count));
        DragChanged?.Invoke(true);
    }

    private void OnRowDropCompleted(FileRow row, Windows.ApplicationModel.DataTransfer.DataPackageOperation result)
    {
        Diag.Info(Target, "row drag ended", new LogField("result", result.ToString()));
        DragChanged?.Invoke(false);
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is not { Index: >= 0 } row || _model is null)
        {
            return;
        }
        if (IsDown(VirtualKey.Control))
        {
            _model.CurrentSelection.Toggle(row.Index);
        }
        else
        {
            _model.CurrentSelection.MoveTo(row.Index, IsDown(VirtualKey.Shift) ? SelectMode.Extend : SelectMode.Single);
        }
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { Index: >= 0 } row && _model is not null)
        {
            _model.CurrentSelection.MoveTo(row.Index, SelectMode.Single);
            _ = Run("pane.openSelected", trigger: "mouse");
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_model is null || e.OriginalSource is TextBox)
        {
            return;
        }
        e.Handled = true;
        Focus(FocusState.Pointer);
        var index = RowFrom(e.OriginalSource)?.Index ?? -1;
        var selection = _model.CurrentSelection;
        if (index >= 0)
        {
            // A right-click inside the selection keeps it (Explorer); outside, it selects that row.
            selection.MoveTo(index, selection.IsSelected(index) ? SelectMode.FocusOnly : SelectMode.Single);
        }
        if (_model.Search is null)
        {
            (IsDown(VirtualKey.Shift) ? ShellMenuRequested : ContextMenuRequested)?.Invoke(this, index, e.GetPosition(null));
        }
    }

    private FileRow? RowFrom(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != Repeater; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FileRow row)
            {
                return row;
            }
        }
        return null;
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                EndRename(commit: true);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                EndRename(commit: false);
                e.Handled = true;
                break;
        }
    }

    private void EndRename(bool commit)
    {
        if (_rename is not { } pending)
        {
            return;
        }
        _rename = null;
        // Windows cannot open a name that ends in a space; the core would refuse it.
        var text = RenameBox.Text.Trim();
        var result = commit && text.Length > 0 && !string.Equals(text, _renameOriginal, StringComparison.Ordinal) ? text : null;
        // The pane takes the focus back before the box collapses: a collapsing
        // focused box hands the focus to the next pane, which would become active.
        if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), RenameBox))
        {
            Focus(FocusState.Programmatic);
        }
        RenameBox.Visibility = Visibility.Collapsed;
        NewRow.Visibility = Visibility.Collapsed;
        _newRow = false;
        pending.TrySetResult(result);
    }

    private void HideRowNote()
    {
        _noteTimer.Stop();
        RowNote.Visibility = Visibility.Collapsed;
        _noteIndex = -1;
    }

    private void PositionEditors()
    {
        // The text box sits in the middle of the row's height.
        var boxTop = (_rowHeight - RenameBox.Height) / 2;
        if (_rename is not null && _newRow)
        {
            // Where the first row on screen is; its name starts after the row's padding, the icon and its gap.
            var m = WindowMetrics.Current;
            var width = Math.Max(0, EditLayer.ActualWidth - (2 * _listPadding));
            var left = _listPadding;
            if (ShowsColumnView && ColumnsHost!.KeyboardList is { } column)
            {
                // Over the top of the keyboard's column, where the new file will be listed.
                left = column.TransformToVisual(EditLayer).TransformPoint(new Point(0, 0)).X + column.ListPadding;
                width = Math.Max(0, column.ActualWidth - (2 * column.ListPadding));
            }
            Canvas.SetLeft(NewRow, left);
            Canvas.SetTop(NewRow, _listPadding);
            NewRow.Width = width;
            Canvas.SetLeft(RenameBox, left + m.RowPaddingX + 16 + m.RowIconGap - 7);
            Canvas.SetTop(RenameBox, _listPadding + boxTop);
            RenameBox.Width = ShowsColumnView ? Math.Max(100, width - m.RowPaddingX - 16 - m.RowIconGap) : Math.Max(160, (width - 84) / 2);
        }
        else if (_rename is not null && _renameIndex >= 0 && PositionOf(_renameIndex) >= 0)
        {
            var (left, top, width) = NameBox(PositionOf(_renameIndex));
            Canvas.SetLeft(RenameBox, left - 7);
            Canvas.SetTop(RenameBox, top + boxTop);
            RenameBox.Width = Math.Max(80, width + 14);
        }
        if (RowNote.Visibility == Visibility.Visible && _noteIndex >= 0 && PositionOf(_noteIndex) >= 0)
        {
            var (left, top, width) = NameBox(PositionOf(_noteIndex));
            Canvas.SetLeft(RowNote, left - 7);
            Canvas.SetTop(RowNote, top + _rowHeight);
            RowNote.MaxWidth = Math.Max(160, width + 120);
        }
    }

    // The name column of the row shown at `position` in the edit layer's coordinates: from the
    // row itself when it is on screen, else from the grid's own proportions.
    private (double Left, double Top, double Width) NameBox(int position)
    {
        if (ShowsColumnView && ColumnsHost!.KeyboardList is { } list)
        {
            // In the keyboard's column: the name's left edge, to the column's right edge.
            var listLeft = list.TransformToVisual(EditLayer).TransformPoint(new Point(0, 0));
            var rowTop = listLeft.Y + list.ListPadding + (position * _rowHeight) - list.VerticalOffset;
            if (list.RowAt(position) is { } columnRow && columnRow.NameElement.ActualWidth > 0)
            {
                var nameLeft = columnRow.NameElement.TransformToVisual(EditLayer).TransformPoint(new Point(0, 0)).X;
                return (nameLeft, rowTop, Math.Max(60, listLeft.X + list.ActualWidth - nameLeft - 12));
            }
            return (listLeft.X + 34, rowTop, Math.Max(60, list.ActualWidth - 46));
        }
        var top = _listPadding + (position * _rowHeight) - Scroller.VerticalOffset;
        if (Repeater.TryGetElement(position) is FileRow row && row.NameElement.ActualWidth > 0)
        {
            var name = row.NameElement;
            var origin = name.TransformToVisual(EditLayer).TransformPoint(new Point(0, 0));
            // The whole name column, not just the text: a new name may be longer.
            var width = name.ActualWidth;
            if (VisualTreeHelper.GetParent(name) is FrameworkElement column)
            {
                width = Math.Max(width, column.ActualWidth - name.TransformToVisual(column).TransformPoint(new Point(0, 0)).X);
            }
            return (origin.X, top, width);
        }
        var rowWidth = Math.Max(0, Scroller.ViewportWidth - (2 * _listPadding));
        return (40, top, Math.Max(120, (rowWidth - 84) / 2) - 26);
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    // ----- Column widths (docs/ui.md, "Column widths") -----

    private const string ColumnsTarget = "cabinetos_ui::columns";

    private ColumnSplit _dragStart;
    private ColumnWidths? _dragWidths;

    /// <summary>
    /// A grip is being dragged: the widths it makes, shared by both panes
    /// (<paramref name="done"/> false while the pointer moves, true once when
    /// the button is released). The window applies them and saves the last.
    /// </summary>
    public event Action<FilePane, ColumnWidths, bool>? ColumnsDragged;

    /// <summary>A double-click on a heading or a grip (or <c>view.fitColumns</c>) fitted columns to the texts on screen: the widths it makes.</summary>
    public event Action<FilePane, ColumnWidths>? ColumnsFit;

    private ColumnGrip[] Grips => [NameModifiedGrip, ModifiedTypeGrip, TypeSizeGrip];

    /// <summary>The width the header's four columns and their gaps share: its grid inside its padding.</summary>
    public double ColumnsAvailable => Math.Max(0, ColumnGrid.ActualWidth - ColumnGrid.Padding.Left - ColumnGrid.Padding.Right);

    private void SetUpColumnGrips()
    {
        NameModifiedGrip.Divider = ColumnDivider.NameModified;
        ModifiedTypeGrip.Divider = ColumnDivider.ModifiedType;
        TypeSizeGrip.Divider = ColumnDivider.TypeSize;
        foreach (var grip in Grips)
        {
            grip.DragStarted += OnGripDragStarted;
            grip.Dragged += OnGripDragged;
            grip.DragCompleted += OnGripDragCompleted;
            // The column at the grip's left; the first grip's is Modified, as Name has no width of its own.
            grip.FitRequested += g => FitColumns(g.Divider == ColumnDivider.TypeSize ? ListColumn.Type : ListColumn.Modified);
        }
        // DoubleTapped only: a single click on a heading is left for sorting by it.
        NameHeading.DoubleTapped += (_, e) => FitFromHeading(e, ListColumn.Name);
        ModifiedHeading.DoubleTapped += (_, e) => FitFromHeading(e, ListColumn.Modified);
        TypeHeading.DoubleTapped += (_, e) => FitFromHeading(e, ListColumn.Type);
        SizeHeading.DoubleTapped += (_, e) => FitFromHeading(e, ListColumn.Size);
    }

    private void FitFromHeading(DoubleTappedRoutedEventArgs e, ListColumn column)
    {
        e.Handled = true;
        FitColumns(column);
    }

    /// <summary>
    /// Lays the header and every row on screen out with the column widths in
    /// effect, and nothing else: a drag calls it at each pointer move.
    /// </summary>
    public void ApplyColumns()
    {
        WindowMetrics.SetColumns(HeadNameColumn, HeadModifiedColumn, HeadTypeColumn, HeadSizeColumn);
        foreach (var row in _realized)
        {
            row.ApplyColumns();
        }
    }

    private void OnGripDragStarted(ColumnGrip grip)
    {
        _dragStart = WindowMetrics.Columns.Resolve(ColumnsAvailable);
        _dragWidths = null;
    }

    private void OnGripDragged(ColumnGrip grip, double dx)
    {
        var widths = WindowMetrics.Columns.Drag(grip.Divider, _dragStart, dx);
        if (widths == (_dragWidths ?? WindowMetrics.UserColumns))
        {
            return;
        }
        _dragWidths = widths;
        ColumnsDragged?.Invoke(this, widths, false);
    }

    // A press and a release that moved nothing (half of a double-click) changes nothing and saves nothing.
    private void OnGripDragCompleted(ColumnGrip grip)
    {
        if (_dragWidths is { } widths)
        {
            _dragWidths = null;
            ColumnsDragged?.Invoke(this, widths, true);
        }
    }

    /// <summary>
    /// Fits <paramref name="column"/> to its widest text among the rows on
    /// screen (the realized rows: measuring every row of a large folder would
    /// stall the window), its cell's room and its heading with the chevron's;
    /// <see cref="ListColumn.Name"/> fits Modified, Type and Size together.
    /// Logs what it measured ("columns fitted") and raises <see cref="ColumnsFit"/>.
    /// </summary>
    public void FitColumns(ListColumn column)
    {
        var available = ColumnsAvailable;
        if (available <= 0)
        {
            Diag.Info(ColumnsTarget, "columns not fitted: the pane is not laid out", new LogField("column", Lower(column)));
            return;
        }
        var layout = WindowMetrics.Columns;
        var fields = new List<LogField> { new("column", Lower(column)), new("rows", _realized.Count), new("list", Math.Round(available, 1)) };
        ColumnWidths widths;
        if (column == ListColumn.Name)
        {
            var modified = MeasureColumn(ListColumn.Modified, fields);
            var type = MeasureColumn(ListColumn.Type, fields);
            var size = MeasureColumn(ListColumn.Size, fields);
            widths = layout.FitAll(available, modified, type, size);
        }
        else
        {
            widths = layout.Fit(layout.Resolve(available), available, column, MeasureColumn(column, fields));
        }
        // What the fit gives; less than a column's "_fit" where that would leave Name under its minimum.
        fields.Add(new("modified", widths.Modified));
        fields.Add(new("type", widths.Type));
        fields.Add(new("size", widths.Size));
        Diag.Info(ColumnsTarget, "columns fitted", [.. fields]);
        ColumnsFit?.Invoke(this, widths);
    }

    // The column's widest text among the rows on screen, measured in the fonts its cells use, with the room the cell keeps
    // beside it and the heading's text with the chevron's room (shown or not, so a later sort by it does not cut the heading).
    private ColumnMeasure MeasureColumn(ListColumn column, List<LogField> fields)
    {
        var m = WindowMetrics.Current;
        var figures = column == ListColumn.Size ? WindowMetrics.FiguresFont : null;
        var widest = 0.0;
        var widestText = "";
        foreach (var row in _realized)
        {
            var text = row.Index >= 0 ? row.ColumnText(column) : "";
            if (text.Length > 0 && TextWidth(text, m.SecondaryFontSize, figures) is var width && width > widest)
            {
                (widest, widestText) = (width, text);
            }
        }
        var (heading, glyph, panel) = column switch
        {
            ListColumn.Modified => (SecondHeading, ModifiedSortGlyph, ModifiedHeading),
            ListColumn.Type => (TypeHeadingText, TypeSortGlyph, TypeHeading),
            _ => (SizeHeadingText, SizeSortGlyph, SizeHeading),
        };
        var measure = new ColumnMeasure(widest, column == ListColumn.Size ? 0 : WindowMetrics.TextGap.Right,
            TextWidth(heading.Text, heading.FontSize, null) + panel.Spacing + glyph.FontSize);
        MeasureText.Text = "";
        var name = Lower(column);
        fields.Add(new($"{name}_text", widestText));
        fields.Add(new($"{name}_text_width", Math.Round(widest, 2)));
        fields.Add(new($"{name}_extra", measure.CellExtra));
        fields.Add(new($"{name}_heading", Math.Round(measure.Heading, 2)));
        fields.Add(new($"{name}_fit", measure.Width));
        return measure;
    }

    private double TextWidth(string text, double fontSize, FontFamily? family)
    {
        MeasureText.FontSize = fontSize;
        if (family is not null)
        {
            MeasureText.FontFamily = family;
        }
        else
        {
            MeasureText.ClearValue(TextBlock.FontFamilyProperty);
        }
        MeasureText.Text = text;
        MeasureText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return MeasureText.DesiredSize.Width;
    }

    private static string Lower(ListColumn column) => column.ToString().ToLowerInvariant();

    /// <summary>The snapshot aid's <c>column-drag:</c> step: drags a grip by <paramref name="dx"/> pixels through its own drag steps.</summary>
    internal void DragGripForSnapshot(ColumnDivider divider, double dx)
    {
        var grip = Grips[(int)divider - 1];
        grip.BeginDrag();
        grip.DragBy(dx);
        grip.EndDrag();
    }

    /// <summary>
    /// The snapshot aid's <c>columns:</c> step: the header's four column widths
    /// as laid out, the width they share, the first row on screen's four, and
    /// how far the grips' centres are from the dividers.
    /// </summary>
    internal (double Name, double Modified, double Type, double Size, double List, string Row, double GripOffset) ColumnsShown()
    {
        var row = _realized.Where(r => r.Index >= 0).OrderBy(r => r.Index).FirstOrDefault();
        var rowWidths = row?.ColumnActualWidths() is { } w
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{w.Name:0.#}/{w.Modified:0.#}/{w.Type:0.#}/{w.Size:0.#}")
            : "";
        var gap = ColumnGrid.ColumnSpacing;
        var divider = ColumnGrid.Padding.Left + HeadNameColumn.ActualWidth + (gap / 2);
        var offset = 0.0;
        foreach (var (grip, width) in new[] { (NameModifiedGrip, 0.0), (ModifiedTypeGrip, HeadModifiedColumn.ActualWidth + gap), (TypeSizeGrip, HeadTypeColumn.ActualWidth + gap) })
        {
            divider += width;
            var centre = grip.TransformToVisual(ColumnGrid).TransformPoint(new Point(grip.ActualWidth / 2, 0)).X;
            offset = Math.Max(offset, Math.Abs(centre - divider));
        }
        return (HeadNameColumn.ActualWidth, HeadModifiedColumn.ActualWidth, HeadTypeColumn.ActualWidth, HeadSizeColumn.ActualWidth, ColumnsAvailable,
            rowWidths, offset);
    }
}
