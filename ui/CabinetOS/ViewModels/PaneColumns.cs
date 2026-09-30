using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tabs;

namespace CabinetOS.ViewModels;

/// <summary>
/// The column view of a pane's front tab (ADR 0016; docs/ui.md, "The column
/// view"): the stack of columns, the listings of the columns the keyboard is
/// not in, and the moves between them. The keyboard's column is the pane's
/// own listing (<see cref="PaneModel"/>), so the commands, the keys, the find
/// and the quick search act on it as they act on the list. Each column is
/// one watched listing, released the moment its column goes. The moves run
/// one after another, in the order they were asked for.
/// </summary>
public sealed class PaneColumns
{
    private const string Target = "cabinetos_ui::columns";

    private readonly ICoreChannel _core;
    private readonly ColumnStack<ColumnListing> _stack;
    private Task _work = Task.CompletedTask;
    // Counts the times the pane went to a folder by another way, so a column listed meanwhile is not added.
    private int _version;
    private bool _closed;

    /// <summary>
    /// The column view of the folder <paramref name="pane"/> shows: one column,
    /// the pane's own listing, with the keyboard.
    /// </summary>
    internal PaneColumns(PaneModel pane, ICoreChannel core)
    {
        Pane = pane;
        _core = core;
        _stack = new ColumnStack<ColumnListing>(pane.Path);
        pane.Navigated += OnNavigated;
        Diag.Info(Target, "column view entered", new LogField("pane", pane.Index), new LogField("path", pane.Path));
    }

    /// <summary>The pane whose front tab shows the columns.</summary>
    public PaneModel Pane { get; }

    /// <summary>How many columns are shown.</summary>
    public int Count => _stack.Count;

    /// <summary>The column the keyboard is in, from 0.</summary>
    public int Keyboard => _stack.Keyboard;

    /// <summary>The deepest column's folder: the tab's path, the breadcrumb row's and the saved tab's.</summary>
    public string Deepest => _stack.Deepest;

    /// <summary>The folders, from the first column to the deepest.</summary>
    public IReadOnlyList<string> Folders => _stack.Folders;

    /// <summary>Whether the view was left.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Whether the pane is taking another column's listing right now: its
    /// rows change before the columns do, so the view waits for
    /// <see cref="Changed"/> instead of showing that moment.
    /// </summary>
    public bool IsSwapping { get; private set; }

    /// <summary>
    /// How many listings the columns hold, the pane's own among them: one
    /// per column while every folder is listed. The "shell state" log line
    /// reports it, so a test sees a dropped column's listing go.
    /// </summary>
    public int ListingCount =>
        (Pane.ListingId != 0 ? 1 : 0) + Enumerable.Range(0, _stack.Count).Count(i => i != _stack.Keyboard && _stack.ItemAt(i) is { ListingId: not 0 });

    /// <summary>
    /// Raised after a change: true when the columns or the keyboard's place
    /// changed (the tab's path may have), false when a column's rows were
    /// listed again.
    /// </summary>
    public event Action<PaneColumns, bool>? Changed;

    /// <summary>Raised with what the user should read (a folder that could not be opened).</summary>
    public event Action<string>? Notice;

    /// <summary>Column <paramref name="column"/>'s folder.</summary>
    public string FolderAt(int column) => _stack.FolderAt(column);

    /// <summary>
    /// The listing of a column the keyboard is not in; null for the
    /// keyboard's (the pane holds it) and for a column whose folder was lost.
    /// </summary>
    public ColumnListing? ListingAt(int column) => column == _stack.Keyboard ? null : _stack.ItemAt(column);

    /// <summary>The cursor row's name in column <paramref name="column"/>, or null.</summary>
    public string? CursorAt(int column) => column == _stack.Keyboard ? Pane.FocusName : _stack.ItemAt(column)?.FocusName;

    /// <summary>
    /// Enter on a folder row (and a click, and Right where no column shows
    /// it yet): the folder opens in a new column right of the keyboard's, the
    /// columns that were right of it go, and the keyboard moves into it.
    /// </summary>
    public Task OpenFocusedAsync(string? requestId) => Queue(() =>
        Pane.EntryAt(Pane.FocusIndex) is { IsFolder: true } entry ? OpenAsync(entry.Path, requestId) : Task.CompletedTask);

    /// <summary>Left: the keyboard goes to the parent column; every column stays on screen.</summary>
    public Task MoveLeftAsync() => Queue(() =>
    {
        MoveKeyboard(_stack.Keyboard - 1);
        return Task.CompletedTask;
    });

    /// <summary>
    /// Right: the keyboard goes into the column on the right when that column
    /// shows the cursor row's folder; else a folder row opens as Enter does.
    /// </summary>
    public Task RightAsync(string? requestId) => Queue(() =>
    {
        var next = _stack.Keyboard + 1;
        if (Pane.EntryAt(Pane.FocusIndex) is not { IsFolder: true } entry)
        {
            return Task.CompletedTask;
        }
        if (next < _stack.Count && TabRules.SameFolder(entry.Path, _stack.FolderAt(next)) && MoveKeyboard(next))
        {
            return Task.CompletedTask;
        }
        return OpenAsync(entry.Path, requestId);
    });

    /// <summary>
    /// Backspace and Up (<c>go.up</c>): in a column past the first, as Left;
    /// in the first, its parent is listed as the new first column, with the
    /// cursor on the folder it came from, and the other columns stay.
    /// </summary>
    public Task UpAsync(string? requestId) => Queue(async () =>
    {
        if (_stack.Keyboard > 0)
        {
            MoveKeyboard(_stack.Keyboard - 1);
            return;
        }
        var first = _stack.FolderAt(0);
        if (DisplayFormat.Parent(first) is not { } parent)
        {
            return;
        }
        var version = _version;
        var (column, error) = await ColumnListing.OpenAsync(_core, parent, Pane.Sort, Pane.KnownDetails, Pane.Selection.Style, requestId,
            DisplayFormat.FolderName(first));
        if (column is null)
        {
            CannotOpen(parent, error);
            return;
        }
        if (_closed || version != _version || _stack.Keyboard != 0)
        {
            column.Release(_core);
            return;
        }
        _stack.SetItem(0, Swap(column));
        _stack.Prepend(parent);
        Log("column opened", requestId, new("pane", Pane.Index), new("depth", 1), new("path", parent), new("listing_id", Pane.ListingId),
            new("columns", _stack.Count));
        Raise(true);
    });

    /// <summary>
    /// A click on row <paramref name="index"/> of column <paramref name="column"/>:
    /// the keyboard moves to that column and the row is selected as in the
    /// list (Ctrl toggles, Shift extends); a plain click on a folder opens it.
    /// </summary>
    public Task ClickAsync(int column, int index, bool ctrl, bool shift) => Queue(() =>
    {
        if (!MoveKeyboard(column) || (uint)index >= (uint)Pane.Selection.Count)
        {
            return Task.CompletedTask;
        }
        if (ctrl)
        {
            Pane.Selection.Toggle(index);
            return Task.CompletedTask;
        }
        Pane.Selection.MoveTo(index, shift ? SelectMode.Extend : SelectMode.Single);
        return !shift && Pane.EntryAt(index) is { IsFolder: true } entry ? OpenAsync(entry.Path, null) : Task.CompletedTask;
    });

    /// <summary>
    /// A right-click or a double-click: the keyboard moves to column
    /// <paramref name="column"/> and the cursor to row <paramref name="index"/>
    /// (-1: the column's empty space). A right-click inside the selection
    /// keeps it, as in the list. False when the column cannot take the keyboard.
    /// </summary>
    public Task<bool> PointAtAsync(int column, int index, bool keepSelection)
    {
        var done = new TaskCompletionSource<bool>();
        _ = Queue(() =>
        {
            var moved = MoveKeyboard(column);
            if (moved && (uint)index < (uint)Pane.Selection.Count)
            {
                Pane.Selection.MoveTo(index, keepSelection && Pane.Selection.IsSelected(index) ? SelectMode.FocusOnly : SelectMode.Single);
            }
            done.TrySetResult(moved);
            return Task.CompletedTask;
        });
        return done.Task;
    }

    /// <summary>Shows a refreshed listing of one of the columns (<c>listing_refreshed</c>); false when none of them has it.</summary>
    public bool ApplyRefresh(ListingRefreshedEvent refreshed)
    {
        for (var i = 0; i < _stack.Count; i++)
        {
            if (i != _stack.Keyboard && _stack.ItemAt(i) is { } column && column.ApplyRefresh(refreshed))
            {
                Raise(false);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A column's folder is gone (<c>listing_lost</c>): a column right of the
    /// keyboard's goes with the ones after it; one left of it stays empty,
    /// and the pane's own folder, inside it, is lost next and starts the view over.
    /// </summary>
    public bool ApplyLost(ListingLostEvent lost)
    {
        for (var i = 0; i < _stack.Count; i++)
        {
            if (i == _stack.Keyboard || _stack.ItemAt(i) is not { } column || column.ListingId != lost.ListingId)
            {
                continue;
            }
            Diag.Info(Target, "column lost", new LogField("pane", Pane.Index), new LogField("depth", i + 1), new LogField("path", column.Path));
            if (i > _stack.Keyboard)
            {
                Release(_stack.DropAfter(i - 1));
            }
            else
            {
                Release([new DroppedColumn<ColumnListing>(i, column.Path, column)]);
                _stack.SetItem(i, null);
            }
            Raise(true);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The core was started again: the columns' listing IDs mean nothing now
    /// and are not closed. The view starts over from the keyboard's folder,
    /// which the pane lists again.
    /// </summary>
    public void ForgetListings()
    {
        for (var i = 0; i < _stack.Count; i++)
        {
            if (i != _stack.Keyboard)
            {
                _stack.ItemAt(i)?.Forget();
            }
        }
        _stack.Reset(Pane.Path);
        _version++;
        Raise(true);
    }

    /// <summary>
    /// Leaves the column view (the tab goes back to the list, or behind
    /// another tab): the deepest column's listing becomes the pane's, so the
    /// list shows the tab's folder, and every other column's is released.
    /// </summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }
        MoveKeyboard(_stack.Count - 1);
        _closed = true;
        Pane.Navigated -= OnNavigated;
        Release(_stack.Reset(Pane.Path));
        _version++;
        Diag.Info(Target, "column view left", new LogField("pane", Pane.Index), new LogField("path", Pane.Path));
    }

    /// <summary>The window closes: the listings are let go without asking the core, which is stopping.</summary>
    public void Abandon()
    {
        _closed = true;
        Pane.Navigated -= OnNavigated;
        foreach (var dropped in _stack.Reset(Pane.Path))
        {
            dropped.Item?.Forget();
        }
    }

    /// <summary>What the view shows, as log fields: the depth, the folders, each column's cursor, the keyboard's column and the listings.</summary>
    public IReadOnlyList<LogField> Describe() =>
    [
        new("pane", Pane.Index),
        new("depth", _stack.Count),
        new("keyboard", _stack.Keyboard + 1),
        new("folders", string.Join("|", _stack.Folders)),
        new("cursors", string.Join("|", Enumerable.Range(0, _stack.Count).Select(i => CursorAt(i) ?? ""))),
        new("path", _stack.Deepest),
        new("listings", ListingCount),
    ];

    // The keyboard goes to `column`, trading listings with the pane; true when it is there.
    private bool MoveKeyboard(int column)
    {
        if (column == _stack.Keyboard)
        {
            return true;
        }
        if ((uint)column >= (uint)_stack.Count || _stack.ItemAt(column) is not { View: not null } incoming)
        {
            return false;
        }
        var from = _stack.Keyboard;
        var outgoing = Swap(incoming);
        _stack.SetItem(column, null);
        _stack.SetItem(from, outgoing);
        _stack.MoveTo(column);
        Diag.Debug(Target, "column keyboard moved", new LogField("pane", Pane.Index), new LogField("from", from + 1), new LogField("to", column + 1),
            new LogField("path", incoming.Path));
        Raise(true);
        return true;
    }

    // The pane takes `incoming`'s listing and hands back its own; the stack is set right after, then Changed shows both.
    private ColumnListing? Swap(ColumnListing incoming)
    {
        IsSwapping = true;
        try
        {
            return Pane.SwapListing(incoming);
        }
        finally
        {
            IsSwapping = false;
        }
    }

    private async Task OpenAsync(string path, string? requestId)
    {
        var version = _version;
        var started = Stopwatch.GetTimestamp();
        var (column, error) = await ColumnListing.OpenAsync(_core, path, Pane.Sort, Pane.KnownDetails, Pane.Selection.Style, requestId);
        if (column is null)
        {
            CannotOpen(path, error);
            return;
        }
        if (_closed || version != _version)
        {
            // The view was left, or the pane went elsewhere, while the core listed the folder.
            column.Release(_core);
            return;
        }
        var entries = column.View?.Count ?? 0;
        var from = _stack.Keyboard;
        _stack.SetItem(from, Swap(column));
        Release(_stack.Open(path));
        Log("column opened", requestId, new("pane", Pane.Index), new("depth", _stack.Count), new("path", path), new("listing_id", Pane.ListingId),
            new("entries", entries), new("ms", Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)));
        Raise(true);
    }

    private void CannotOpen(string path, string? error)
    {
        Diag.Info(Target, "cannot open a column", new LogField("pane", Pane.Index), new LogField("path", path), new LogField("error", error ?? ""));
        Notice?.Invoke($"Cannot open {path}: {error}");
    }

    // A crumb, Back, the sidebar, a tab or a lost folder took the pane elsewhere: the view starts over there.
    // A reload of the same folder (Ctrl+R, a new order) keeps the columns.
    private void OnNavigated(PaneModel pane, NavigationKind kind, bool samePlace)
    {
        if (_closed || samePlace)
        {
            return;
        }
        Release(_stack.Reset(Pane.Path));
        _version++;
        Diag.Info(Target, "column view started over", new LogField("pane", Pane.Index), new LogField("path", Pane.Path), new LogField("how", kind.ToString()));
        Raise(true);
    }

    private void Release(IReadOnlyList<DroppedColumn<ColumnListing>> dropped)
    {
        foreach (var column in dropped)
        {
            if (column.Item is not { } listing)
            {
                continue;
            }
            var listingId = listing.ListingId;
            listing.Release(_core);
            Diag.Info(Target, "column released", new LogField("pane", Pane.Index), new LogField("depth", column.Column + 1), new LogField("path", column.Folder),
                new LogField("listing_id", listingId));
        }
    }

    private void Raise(bool structural) => Changed?.Invoke(this, structural);

    private static void Log(string message, string? requestId, params LogField[] fields)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            Diag.Info(Target, message, fields);
        }
        else
        {
            Diag.Request(LogLevel.Info, requestId, Target, message, fields);
        }
    }

    private Task Queue(Func<Task> operation)
    {
        var previous = _work;
        return _work = Run();

        async Task Run()
        {
            try
            {
                await previous;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Diag.Warn(Target, "an earlier column change failed", new LogField("error", error.Message));
            }
            if (!_closed)
            {
                await operation();
            }
        }
    }
}
