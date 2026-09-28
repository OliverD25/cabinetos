using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
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
/// alone), Insert toggles a row as in Total Commander, and the file keys run
/// commands through the router.
/// </summary>
public sealed partial class FilePane : UserControl
{
    private const string Target = "cabinetos_ui::pane";
    private const double RowHeight = 30;
    private const double ListPadding = 4;

    private readonly HashSet<FileRow> _realized = [];
    private readonly DispatcherQueueTimer _noteTimer;
    private int _preparedFirst = int.MaxValue;
    private int _preparedLast = -1;
    private bool _detailsQueued;
    private PaneModel? _model;
    private string? _shownPath;
    private NavigationTiming? _timing;
    private long _firstRowTicks;
    private bool _renderingHooked;
    private TaskCompletionSource<string?>? _rename;
    private bool _clearButtonHidden;
    private int _renameIndex = -1;
    private string _renameOriginal = "";
    private int _noteIndex = -1;
    private bool _searchShown;

    /// <summary>Creates the pane; <see cref="Model"/> gives it its content.</summary>
    public FilePane()
    {
        InitializeComponent();
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.Tapped += OnRowTapped;
        Repeater.DoubleTapped += OnRowDoubleTapped;
        Frame.RightTapped += OnRightTapped;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => Activated?.Invoke(this);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Focus(FocusState.Pointer)), handledEventsToo: true);
        Header.SizeChanged += (_, e) => PathText.MaxWidth = Math.Max(0, e.NewSize.Width * 0.45);
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
    }

    /// <summary>Raised when the pane gets the focus: it becomes the active pane.</summary>
    public event Action<FilePane>? Activated;

    /// <summary>
    /// Raised for a context menu: the row (-1 for the pane's empty space) and
    /// where the pointer was, in the window's coordinates (null from the keyboard).
    /// </summary>
    public event Action<FilePane, int, Point?>? ContextMenuRequested;

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
            UpdateHeader();
            UpdateActivity();
            ApplyRows();
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
        ScrollIntoView(index);
        UpdateLayout();
        RenameBox.Text = name;
        RenameBox.Visibility = Visibility.Visible;
        HideClearButton();
        PositionEditors();
        RenameBox.Focus(FocusState.Programmatic);
        var dot = selectStem ? name.LastIndexOf('.') : -1;
        RenameBox.Select(0, dot > 0 ? dot : name.Length);
        return pending.Task;
    }

    /// <summary>Stops an edit without renaming (Esc).</summary>
    public void CancelRename() => EndRename(commit: false);

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

    /// <summary>Where a context menu opened from the keyboard goes: under the row's name, in the window's coordinates.</summary>
    public Point RowAnchor(int index)
    {
        if (index >= 0 && Repeater.TryGetElement(index) is FileRow row)
        {
            return row.TransformToVisual(null).TransformPoint(new Point(40, RowHeight));
        }
        return Frame.TransformToVisual(null).TransformPoint(new Point(40, 80));
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
            case nameof(PaneModel.Path):
                UpdateHeader();
                break;
            case nameof(PaneModel.ShowsActiveStroke) or nameof(PaneModel.HasBrightTitle):
                UpdateActivity();
                MarkSelection();
                break;
            case nameof(PaneModel.Message):
                UpdateMessage();
                break;
            case nameof(PaneModel.Search):
                ApplySearch();
                break;
        }
    }

    private void UpdateHeader()
    {
        if (_model?.Search is { } search)
        {
            TitleText.Text = search.Header;
            PathText.Text = search.Scope;
            return;
        }
        TitleText.Text = _model?.FolderName ?? "";
        PathText.Text = _model?.Path ?? "";
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
                UpdateHeader();
                ApplyRows();
                ScrollToFocus();
            }
            return;
        }
        var entering = !_searchShown;
        _searchShown = true;
        EndRename(commit: false);
        HideRowNote();
        SearchBar.Visibility = Visibility.Visible;
        SecondHeading.Text = "Folder";
        UpdateHeader();
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

    private void UpdateMessage()
    {
        MessageText.Text = _model?.Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(_model?.Message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyRows()
    {
        if (_model?.Search is not null)
        {
            // The listing changed under the search results; it shows when the search is left.
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
                ScrollIntoView(model.FocusIndex);
            }
        }
        else if (IsRenaming)
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
        foreach (var row in _realized)
        {
            if (string.Equals(row.IconKey, key, StringComparison.Ordinal))
            {
                row.RefreshDetails();
            }
        }
    }

    /// <summary>Binds every row's type name and icon again (the screen's scale changed the icon size).</summary>
    public void RefreshDetails()
    {
        foreach (var row in _realized)
        {
            row.RefreshDetails();
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FileRow row)
        {
            _realized.Add(row);
            Mark(row, args.Index);
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
    }

    private void AskForDetails()
    {
        _detailsQueued = false;
        var (first, last) = (_preparedFirst, _preparedLast);
        _preparedFirst = int.MaxValue;
        _preparedLast = -1;
        // Search hits are not entries of the listing: their types come from what is known per extension.
        if (last >= first && _model is { Search: null } model)
        {
            model.EnsureDetails(first, last);
        }
    }

    private void OnDetailsArrived(int from, int count)
    {
        foreach (var row in _realized)
        {
            if (row.Index >= from && row.Index < from + count)
            {
                row.RefreshDetails();
            }
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FileRow row)
        {
            _realized.Remove(row);
            row.IsSelected = false;
            row.ShowsCursor = false;
        }
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
            new LogField("first_frame_ms", Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, now).TotalMilliseconds, 1)));
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
        foreach (var row in _realized)
        {
            Mark(row, row.Index);
        }
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
        // through the list, as in any Windows list.
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var mode = shift ? SelectMode.Extend : ctrl ? SelectMode.FocusOnly : SelectMode.Single;
        var focus = _model.CurrentSelection.Focus;
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Up:
                MoveFocus(focus - 1, mode);
                break;
            case VirtualKey.Down:
                MoveFocus(focus + 1, mode);
                break;
            case VirtualKey.Home:
                MoveFocus(0, mode);
                break;
            case VirtualKey.End:
                MoveFocus(_model.ShownCount - 1, mode);
                break;
            case VirtualKey.PageUp:
                MoveFocus(focus - RowsPerPage(), mode);
                break;
            case VirtualKey.PageDown:
                MoveFocus(focus + RowsPerPage(), mode);
                break;
            case VirtualKey.Back:
                // A file manager convention the keymap does not hold (go.up is Alt+Up there).
                _ = Run("go.up");
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
            ScrollIntoView(_model.CurrentSelection.Focus);
        }
    }

    private Task Run(string commandId, JsonElement? args = null, string trigger = "key") =>
        RunCommand?.Invoke(commandId, args, trigger) ?? Task.CompletedTask;

    private void MoveFocus(int index, SelectMode mode)
    {
        if (_model is null || _model.ShownCount == 0)
        {
            return;
        }
        _model.CurrentSelection.MoveTo(index, mode);
        ScrollIntoView(_model.CurrentSelection.Focus);
    }

    private int RowsPerPage() => Math.Max(1, (int)((Scroller.ViewportHeight - (2 * ListPadding)) / RowHeight) - 1);

    private void ScrollIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }
        var top = ListPadding + (index * RowHeight);
        var bottom = top + RowHeight;
        var offset = Scroller.VerticalOffset;
        var viewport = Scroller.ViewportHeight;
        if (top - ListPadding < offset)
        {
            Scroller.ChangeView(null, Math.Max(0, top - ListPadding), null, disableAnimation: true);
        }
        else if (bottom + ListPadding > offset + viewport)
        {
            Scroller.ChangeView(null, bottom + ListPadding - viewport, null, disableAnimation: true);
        }
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
            ContextMenuRequested?.Invoke(this, index, e.GetPosition(null));
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
        if (_rename is not null && _renameIndex >= 0)
        {
            var (left, top, width) = NameBox(_renameIndex);
            Canvas.SetLeft(RenameBox, left - 7);
            Canvas.SetTop(RenameBox, top + 2);
            RenameBox.Width = Math.Max(80, width + 14);
        }
        if (RowNote.Visibility == Visibility.Visible && _noteIndex >= 0)
        {
            var (left, top, width) = NameBox(_noteIndex);
            Canvas.SetLeft(RowNote, left - 7);
            Canvas.SetTop(RowNote, top + RowHeight);
            RowNote.MaxWidth = Math.Max(160, width + 120);
        }
    }

    // The name column of row `index` in the edit layer's coordinates: from the
    // row itself when it is on screen, else from the grid's own proportions.
    private (double Left, double Top, double Width) NameBox(int index)
    {
        var top = ListPadding + (index * RowHeight) - Scroller.VerticalOffset;
        if (Repeater.TryGetElement(index) is FileRow row && row.NameElement.ActualWidth > 0)
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
        var rowWidth = Math.Max(0, Scroller.ViewportWidth - (2 * ListPadding));
        return (40, top, Math.Max(120, (rowWidth - 84) / 2) - 26);
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
