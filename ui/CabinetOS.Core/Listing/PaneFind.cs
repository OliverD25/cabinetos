using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Listing;

/// <summary>
/// The find widget of a pane's tab (Ctrl+F; docs/ui.md, "Find in pane"; the
/// creator's SHELL_REDESIGN.md §3): the text typed, and the rows of the
/// listing whose names contain it. The core matches the names
/// (<c>match_entries</c>), as for the pattern box and quick search, so the
/// window never scans them; this class asks, drops answers that came too
/// late, and hands the rows to the pane's <see cref="SelectionModel"/>,
/// which hides the others. The other pane has a find of its own.
/// </summary>
public sealed class PaneFind(ICoreChannel core, SelectionModel selection)
{
    private int _asked;

    /// <summary>The widget's text; null while it is closed. An empty text filters nothing.</summary>
    public string? Query { get; private set; }

    /// <summary>Whether the widget is open.</summary>
    public bool IsOpen => Query is not null;

    /// <summary>Whether the list is filtered now.</summary>
    public bool IsFiltering => selection.IsFiltered;

    /// <summary>How many rows match: the filter's rows, 0 without a filter.</summary>
    public int Matches => selection.IsFiltered ? selection.ShownCount : 0;

    /// <summary>Raised after the query or the rows it shows changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The pattern the core matches for <paramref name="query"/>: the name
    /// contains it, case ignored. <c>;</c> and <c>|</c> are the pattern
    /// syntax's own, so each stands for any one character there; a name that
    /// holds them is still found. Null for a query of nothing but spaces.
    /// </summary>
    public static string? Pattern(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }
        return "*" + query.Replace(';', '?').Replace('|', '?') + "*";
    }

    /// <summary>
    /// The match count as the widget shows it: "n of m", where n is the
    /// cursor row's place among the m matches; "0 of 0" when nothing matches.
    /// Empty while nothing is typed.
    /// </summary>
    public static string CountText(int position, int matches, bool filtering) =>
        !filtering ? "" : matches == 0 ? "0 of 0" : $"{Math.Clamp(position + 1, 1, matches):N0} of {matches:N0}";

    /// <summary>The widget's count for the pane now.</summary>
    public string Count => CountText(selection.PositionOf(selection.Focus), Matches, IsFiltering);

    /// <summary>Opens the widget with <paramref name="query"/> (empty: nothing typed yet); the list is not filtered until the text asks for it.</summary>
    public void Open(string query = "")
    {
        Query ??= query;
        Changed?.Invoke();
    }

    /// <summary>Closes the widget: the text goes and every row shows again, with the selection as it was.</summary>
    public void Close()
    {
        _asked++;
        Query = null;
        selection.SetVisible(null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Filters the listing <paramref name="listingId"/> of generation
    /// <paramref name="generation"/> (<paramref name="count"/> rows) by
    /// <paramref name="query"/>. An empty query shows every row. Returns
    /// false when a newer query or listing made the answer useless, or the
    /// core refused; the list then stays as it was.
    /// </summary>
    public async Task<bool> SetQueryAsync(string query, ulong listingId, uint generation, int count, string? requestId = null)
    {
        Query = query;
        var asked = ++_asked;
        if (Pattern(query) is not { } pattern || listingId == 0)
        {
            selection.SetVisible(null);
            Changed?.Invoke();
            return true;
        }
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new MatchEntriesRequest(listingId, pattern) { Id = requestId ?? "" });
        }
        catch (IOException)
        {
            return false;
        }
        if (asked != _asked || reply is not EntryMatchesReply matches || matches.ListingId != listingId || matches.Generation != generation)
        {
            return false;
        }
        selection.SetVisible(EntryRanges.Rows(matches.Ranges, count));
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The listing changed under the widget (a refresh, another folder): an
    /// answer on its way is for the old one and is dropped. The caller asks
    /// again with <see cref="SetQueryAsync"/> for the new listing.
    /// </summary>
    public void Forget() => _asked++;
}
