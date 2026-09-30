using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.ViewModels;

/// <summary>
/// One column of a pane's column view that the keyboard is not in (ADR
/// 0016; docs/ui.md, "The column view"): a folder's listing in shared
/// memory, watched by the core, with its own cursor and marks. The column
/// with the keyboard is the pane's own listing (<see cref="PaneModel"/>), so
/// every command acts on it; the two trade places when the keyboard moves
/// (<see cref="PaneModel.SwapListing"/>), without asking the core again.
/// </summary>
public sealed class ColumnListing
{
    private const string Target = "cabinetos_ui::columns";

    private ListingView? _view;

    internal ColumnListing(string path, ListingView view, ulong listingId, IRowDetails details, SelectionStyle style,
        int focus, int anchor, IEnumerable<int> selected)
    {
        Path = path;
        _view = view;
        ListingId = listingId;
        Details = details;
        Selection.SetStyle(style);
        Selection.Restore(view.Count, selected, focus, anchor);
        Rows = new ListingRows(view, details);
    }

    /// <summary>The folder the column shows.</summary>
    public string Path { get; }

    /// <summary>The listing in shared memory, or null once it was handed on or released.</summary>
    public ListingView? View => _view;

    /// <summary>The listing's ID in the core, for its events; 0 once released or forgotten.</summary>
    public ulong ListingId { get; private set; }

    /// <summary>Where the rows' type names and icons come from: what the core said per extension.</summary>
    public IRowDetails Details { get; }

    /// <summary>The column's cursor and marks.</summary>
    public SelectionModel Selection { get; } = new();

    /// <summary>The rows; a new object when the core lists the folder again.</summary>
    public ListingRows? Rows { get; private set; }

    /// <summary>The name of the cursor's row, or null.</summary>
    public string? FocusName => _view is { } view && (uint)Selection.Focus < (uint)view.Count ? view.Name(Selection.Focus) : null;

    /// <summary>Raised when the core listed the folder again: new rows, the cursor and marks found again by ID.</summary>
    public event Action<ColumnListing>? RowsChanged;

    /// <summary>
    /// Lists <paramref name="path"/> for a new column, watched, in
    /// <paramref name="sort"/>, with the cursor on <paramref name="selectName"/>
    /// (or the first row). Returns the column, or why it could not be listed.
    /// </summary>
    internal static async Task<(ColumnListing? Column, string? Error)> OpenAsync(ICoreChannel core, string path, SortSpec? sort,
        IRowDetails details, SelectionStyle style, string? requestId, string? selectName = null)
    {
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new ListDirectoryRequest(path) { Watch = true, Id = requestId ?? "", Sort = sort });
        }
        catch (IOException error)
        {
            return (null, error.Message);
        }
        if (reply is ErrorReply refused)
        {
            return (null, PaneModel.Describe(refused));
        }
        if (reply is not ListingOpenedReply opened || opened.TakeSection() is not { } section)
        {
            return (null, $"unexpected reply {reply.GetType().Name}");
        }
        ListingView view;
        try
        {
            view = ListingView.Open(section, opened.SectionSize);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            _ = CloseAsync(core, opened.ListingId);
            return (null, failure.Message);
        }
        var select = selectName is null ? -1 : view.IndexOfName(selectName);
        var focus = Math.Max(0, select);
        return (new ColumnListing(path, view, opened.ListingId, details, style, focus, focus, style == SelectionStyle.Windows && view.Count > 0 ? [focus] : []), null);
    }

    /// <summary>
    /// Gives the listing to the pane, which shows it as its own: the column
    /// then holds nothing, and releasing it closes nothing.
    /// </summary>
    internal (ListingView View, ulong ListingId) HandOver()
    {
        var handed = (_view!, ListingId);
        _view = null;
        ListingId = 0;
        Rows = null;
        return handed;
    }

    /// <summary>
    /// Shows a refreshed listing of the folder (<c>listing_refreshed</c>): the
    /// cursor and the marks follow their entries by ID. False when the event
    /// is not for this column.
    /// </summary>
    public bool ApplyRefresh(ListingRefreshedEvent refreshed)
    {
        if (ListingId == 0 || refreshed.ListingId != ListingId || _view is not { } old || refreshed.Generation <= old.Generation)
        {
            return false;
        }
        if (refreshed.TakeSection() is not { } section)
        {
            return true;
        }
        ListingView view;
        try
        {
            view = ListingView.Open(section, refreshed.SectionSize);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            Diag.Warn(Target, "a refreshed column could not be read", new LogField("listing_id", ListingId), new LogField("error", failure.Message));
            return true;
        }
        var focusId = (uint)Selection.Focus < (uint)old.Count ? old.Id(Selection.Focus) : (ulong?)null;
        var anchorId = (uint)Selection.Anchor < (uint)old.Count ? old.Id(Selection.Anchor) : (ulong?)null;
        var selectedIds = Selection.AllSelected.Where(i => (uint)i < (uint)old.Count).Select(old.Id).ToList();
        var previousFocus = Selection.Focus;
        _view = view;
        Rows = new ListingRows(view, Details);
        var focus = focusId is { } id ? view.IndexOfId(id) : -1;
        var anchor = anchorId is { } anchorEntry ? view.IndexOfId(anchorEntry) : -1;
        Selection.Restore(view.Count, selectedIds.Select(view.IndexOfId).Where(i => i >= 0), focus >= 0 ? focus : previousFocus, anchor);
        old.Dispose();
        RowsChanged?.Invoke(this);
        return true;
    }

    /// <summary>The column goes: its listing is let go here and in the core, which stops watching the folder.</summary>
    internal void Release(ICoreChannel core)
    {
        _view?.Dispose();
        _view = null;
        Rows = null;
        if (ListingId != 0)
        {
            _ = CloseAsync(core, ListingId);
        }
        ListingId = 0;
    }

    /// <summary>
    /// The core was started again (or stopped): its listing IDs mean nothing
    /// now and must not be closed, since the new core may have given one to
    /// another listing.
    /// </summary>
    internal void Forget()
    {
        _view?.Dispose();
        _view = null;
        Rows = null;
        ListingId = 0;
    }

    private static async Task CloseAsync(ICoreChannel core, ulong listingId)
    {
        try
        {
            await core.RequestAsync(new CloseListingRequest(listingId));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot close a column's listing", new LogField("listing_id", listingId), new LogField("error", error.Message));
        }
    }
}
