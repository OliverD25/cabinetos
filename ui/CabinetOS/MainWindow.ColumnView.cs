using System.Globalization;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Tabs;
using CabinetOS.ViewModels;

namespace CabinetOS;

// The column view (Phase 19f, ADR 0016; docs/ui.md, "The column view"): a mode of a pane's folder tab. While the tab a pane
// holds is in the columns mode, the window keeps a PaneColumns for that pane and its FilePane shows it in the list's place.
// The deepest column's folder is the tab's path (the breadcrumb row, the title, ui.tabs); the keyboard's column is the pane's
// own listing, so every command acts on its rows, and window_state reports its folder with its cursor and marks.
public sealed partial class MainWindow
{
    private readonly PaneColumns?[] _columnViews = new PaneColumns?[2];

    private void RegisterColumnViewCommands() => _router.RegisterUiHandler("view.toggleColumns", ToggleColumns);

    // The active pane's front tab changes its mode; the tab keeps it, and ui.tabs saves it (Article 6).
    private void ToggleColumns(CommandInvocation invocation)
    {
        var pane = PaneOf(invocation);
        var strip = _strips[pane];
        var tab = strip.Active;
        if (tab.IsTool)
        {
            ShowNotice("A tab that shows a tool has no column view: go to a folder tab first.");
            return;
        }
        if (_held[pane] != tab || _panes[pane].View is null)
        {
            // The tab is still being shown, or its folder could not be listed: there is nothing to show as columns.
            ShowNotice("This tab shows no folder yet.");
            return;
        }
        if (_searchPane == pane)
        {
            EndSearch(focusPane: false);
        }
        tab.Mode = tab.Mode == TabMode.Columns ? TabMode.Files : TabMode.Columns;
        SyncColumnView(pane);
        strip.Touch();
    }

    // The folder the tab in front shows: the deepest column's in the column view, else the pane's own.
    private string TabFolder(int pane) => _columnViews[pane]?.Deepest ?? _panes[pane].Path;

    // How many listings a pane holds: one per column in the column view, one for the list.
    private int ListingCount(int pane) => _columnViews[pane]?.ListingCount ?? (_panes[pane].ListingId != 0 ? 1 : 0);

    /// <summary>
    /// Makes the pane's column view follow the tab it holds: one while that
    /// folder tab is in the columns mode and its folder is listed, none
    /// otherwise. Leaving it puts the deepest column's folder in the list.
    /// </summary>
    private void SyncColumnView(int pane)
    {
        var model = _panes[pane];
        var wanted = _held[pane] is { IsTool: false, Mode: TabMode.Columns } && model.View is not null;
        if (wanted == _columnViews[pane] is not null)
        {
            return;
        }
        if (wanted)
        {
            var columns = new PaneColumns(model, _session);
            columns.Changed += OnColumnsChanged;
            columns.Notice += text => ShowNotice(text, isError: true);
            _columnViews[pane] = columns;
            _paneViews[pane].ShowColumns(columns);
        }
        else
        {
            CloseColumnView(pane);
        }
        SyncTabFolder(model);
        UpdateCrumbs();
        UpdateNavigationButtons();
        ScheduleWindowState();
    }

    // The column view goes (the tab leaves the mode, or goes behind another tab): the deepest folder stays in the pane.
    private void CloseColumnView(int pane)
    {
        if (_columnViews[pane] is not { } columns)
        {
            return;
        }
        // Gone from the array first: the pane's folder changes while it closes, and the tab must follow the pane then.
        _columnViews[pane] = null;
        columns.Changed -= OnColumnsChanged;
        columns.Close();
        _paneViews[pane].ShowColumns(null);
    }

    private void OnColumnsChanged(PaneColumns columns, bool structural)
    {
        var pane = Array.IndexOf(_columnViews, columns);
        if (pane < 0)
        {
            return;
        }
        _paneViews[pane].SyncColumns(bringIntoView: structural);
        if (!structural)
        {
            return;
        }
        SyncTabFolder(columns.Pane);
        UpdateCrumbs();
        UpdateNavigationButtons();
        ScheduleWindowState();
        // Every change in the log, so a live check with real keys can follow the depth without the snapshot aid.
        Diag.Info(ColumnsTarget, "column view changed", [.. columns.Describe()]);
    }

    // ----- The snapshot aid's steps -----

    // column-view:<label> logs what the active pane shows ("column view shown"); column-open:<name> opens that row of the
    // keyboard's column as Enter does; column-key:<left|right|up|down|home|end|backspace> is that key in the active pane;
    // column-click:<depth>|<name> is a click on that row of that column (1 is the first column).
    private async Task RunColumnViewStepAsync(string kind, string argument)
    {
        var columns = _columnViews[_active];
        var pane = Active;
        switch (kind)
        {
            case "column-view":
                LogColumnView(argument);
                break;
            case "column-open":
                var index = pane.View?.IndexOfName(argument) ?? -1;
                if (index < 0)
                {
                    Diag.Info("cabinetos_ui::snapshot", "no such row in the keyboard's column", new LogField("name", argument));
                    break;
                }
                pane.Selection.MoveTo(index, SelectMode.Single);
                await _router.ExecuteAsync("pane.openSelected", trigger: "snapshot");
                break;
            case "column-key":
                await ColumnKeyForSnapshotAsync(argument, columns, pane);
                break;
            case "column-click":
                var parts = argument.Split('|', 2);
                if (columns is null || parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var depth)
                    || depth < 1 || depth > columns.Count)
                {
                    Diag.Info("cabinetos_ui::snapshot", "column-click needs a shown column's depth and a name, as 2|b", new LogField("step", argument));
                    break;
                }
                var view = depth - 1 == columns.Keyboard ? pane.View : columns.ListingAt(depth - 1)?.View;
                var row = view?.IndexOfName(parts[1]) ?? -1;
                if (row >= 0)
                {
                    await columns.ClickAsync(depth - 1, row, ctrl: false, shift: false);
                }
                break;
        }
    }

    private async Task ColumnKeyForSnapshotAsync(string key, PaneColumns? columns, PaneModel pane)
    {
        var selection = pane.Selection;
        switch (key)
        {
            case "left" when columns is not null:
                await columns.MoveLeftAsync();
                break;
            case "right" when columns is not null:
                await columns.RightAsync(null);
                break;
            case "up" or "down" or "home" or "end" when selection.ShownCount > 0:
                var target = key switch
                {
                    "up" => Math.Max(0, selection.Focus - 1),
                    "down" => selection.Focus + 1,
                    "home" => 0,
                    _ => selection.Count - 1,
                };
                selection.MoveTo(target, selection.KeyMode(shift: false, ctrl: false, toListEnd: key is "home" or "end"));
                _paneViews[_active].ScrollToFocus();
                break;
            case "backspace":
                await _router.ExecuteAsync("go.up", trigger: "snapshot");
                break;
            default:
                Diag.Info("cabinetos_ui::snapshot", "column-key needs left, right, up, down, home, end or backspace", new LogField("key", key));
                break;
        }
    }

    // What the active pane shows, for the checks that read the log: the mode, the columns with their folders and cursors,
    // the keyboard's column and its rows (a folder with ">"), the rows each column has on screen, the strip's scroll, and
    // the listings the pane holds.
    private void LogColumnView(string label)
    {
        var pane = _active;
        var model = _panes[pane];
        var fields = new List<LogField>
        {
            new("label", label),
            new("mode", _columnViews[pane] is null ? "files" : "columns"),
            new("tab_path", _held[pane]?.Path ?? ""),
            new("crumbs", _crumbViews[pane].Text),
            new("rows", RowNames(model)),
            new("tab_mode", _held[pane]?.Mode.ToString().ToLowerInvariant() ?? ""),
        };
        if (_columnViews[pane] is { } columns)
        {
            fields.AddRange(columns.Describe());
            if (_paneViews[pane].ColumnViewShown is { } view)
            {
                var (offset, viewport, extent) = view.StripScroll;
                fields.Add(new("shown_rows", string.Join(" / ", view.DescribeRows())));
                fields.Add(new("widths", view.Widths()));
                fields.Add(new("strip", string.Create(CultureInfo.InvariantCulture, $"{offset:0.#}/{viewport:0.#}/{extent:0.#}")));
                fields.Add(new("keyboard_in_view", view.KeyboardInView()));
            }
        }
        else
        {
            fields.Add(new("pane", pane));
            fields.Add(new("depth", 0));
            fields.Add(new("path", model.Path));
            fields.Add(new("listings", ListingCount(pane)));
        }
        Diag.Info(ColumnsTarget, "column view shown", [.. fields]);

        static string RowNames(PaneModel model) =>
            model.View is { } view
                ? string.Join("|", Enumerable.Range(0, Math.Min(view.Count, 40)).Select(i => view.IsFolder(i) ? view.Name(i) + ">" : view.Name(i)))
                : "";
    }
}
