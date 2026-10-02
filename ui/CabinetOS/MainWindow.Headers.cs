using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.ViewModels;
using CabinetOS.Views;

namespace CabinetOS;

// Sorting by the column headings (docs/ui.md, "A pane's order"). A click on Name, Modified, Type or Size runs the command of
// that column's key (view.sortByName and the others) for the pane clicked, so the headings, the palette and Ctrl+F3 to Ctrl+F6
// are one path (SortActiveAsync). The order is the pane's own, as the keys' is: panes.sort, the default of a pane with none,
// stays as it is. A double-click on a heading fits its column; its first click had sorted already, so the double-click takes
// that sort back first (HeaderClicks).
public sealed partial class MainWindow
{
    private const string HeaderTarget = "cabinetos_ui::headers";

    // The pane's own order before the last click on a heading changed it, for the double-click that undoes it.
    private (PaneModel Pane, SortSpec? Before)? _headerSort;

    // How many sorts of a pane's listing finished: the snapshot aid's until:sorted waits for the next one.
    private int _sortsDone;

    private void SetUpHeaderSort()
    {
        foreach (var view in _paneViews)
        {
            view.SortRequested += OnHeaderSortRequested;
            view.SortUndoRequested += OnHeaderSortUndo;
        }
    }

    private void OnHeaderSortRequested(FilePane view, ListColumn column)
    {
        var index = Array.IndexOf(_paneViews, view);
        // The press made this pane the active one, but XAML raises that on its next frame: the sort is of the pane clicked, now.
        SetActive(index);
        var pane = _panes[index];
        _headerSort = pane.Path.Length > 0 && pane.Search is null ? (pane, pane.Sort) : null;
        Diag.Info(HeaderTarget, "a heading was clicked", new LogField("pane", index), new LogField("column", ColumnName(column)));
        _ = _router.ExecuteAsync(SortCommandFor(column), trigger: "heading");
    }

    private void OnHeaderSortUndo(FilePane view, ListColumn column)
    {
        if (_headerSort is not { } undo || !ReferenceEquals(undo.Pane, _panes[Array.IndexOf(_paneViews, view)]))
        {
            return;
        }
        _headerSort = null;
        if (undo.Pane.Sort == undo.Before)
        {
            return;
        }
        Diag.Info(HeaderTarget, "the sort of a double-click's first click is taken back", new LogField("column", ColumnName(column)));
        ShowNotice("");
        _ = SortPaneAsync(undo.Pane, undo.Before, null);
    }

    private static string SortCommandFor(ListColumn column) => column switch
    {
        ListColumn.Modified => "view.sortByModified",
        ListColumn.Type => "view.sortByExtension",
        ListColumn.Size => "view.sortBySize",
        _ => "view.sortByName",
    };

    private static string ColumnName(ListColumn column) => column.ToString().ToLowerInvariant();

    // The pane's own order, or none when sort is null; logged for the tests and the live check, which read what the pane was sorted to.
    private async Task<bool> SortPaneAsync(PaneModel pane, SortSpec? sort, string? requestId)
    {
        var sorted = await pane.SortAsync(sort, requestId);
        if (sorted)
        {
            _sortsDone++;
            Diag.Info(Target, "pane sorted", new LogField("pane", Array.IndexOf(_panes, pane)), new LogField("key", sort?.Key ?? "default"),
                new LogField("descending", sort?.Descending ?? pane.EffectiveSort.Descending), new LogField("own", sort is not null));
        }
        return sorted;
    }

    // ----- The snapshot aid's steps -----

    // header-click:<column>[|<pane>] is a click on that column's heading (name, modified, type or size) in that pane (the active one
    // without a pane), through the click's own path; header-doubleclick:<column>[|<pane>] is what a real double-click raises (two
    // taps and the double tap); sort-state:<label> logs each pane's own order, its effective order and the chevron its header shows.
    private void RunHeaderStep(string kind, string argument)
    {
        if (kind == "sort-state")
        {
            LogSortState(argument);
            return;
        }
        var parts = argument.Split('|');
        if (!Enum.TryParse<ListColumn>(parts[0], ignoreCase: true, out var column) || !Enum.IsDefined(column) || int.TryParse(parts[0], out _)
            || (parts.Length > 1 && !(int.TryParse(parts[1], out var named) && named is 0 or 1)))
        {
            Diag.Info("cabinetos_ui::snapshot", $"{kind} needs name, modified, type or size, and optionally |0 or |1 for the pane", new LogField("step", argument));
            return;
        }
        var view = _paneViews[parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : _active];
        if (kind == "header-click")
        {
            view.ClickHeaderForSnapshot(column);
        }
        else
        {
            view.DoubleClickHeaderForSnapshot(column);
        }
    }

    private void LogSortState(string label)
    {
        static string Describe(SortSpec? sort) => sort is null ? "none" : $"{sort.Key}:{(sort.Descending ? "desc" : "asc")}";
        var fields = new List<LogField> { new("label", label), new("active", _active) };
        for (var i = 0; i < _panes.Length; i++)
        {
            fields.Add(new($"pane{i}_own", Describe(_panes[i].Sort)));
            fields.Add(new($"pane{i}_effective", Describe(_panes[i].EffectiveSort)));
            fields.Add(new($"pane{i}_arrow", _paneViews[i].SortArrowShown()));
        }
        Diag.Info(HeaderTarget, "sort state", [.. fields]);
    }
}
