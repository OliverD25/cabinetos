using CabinetOS.Core.Commands;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;

namespace CabinetOS;

// Total Commander's small commands (sub-phase 11a; docs/research/total-commander.md, Part 3 (b);
// docs/ui.md, "Total Commander's keys"). The core's registry lists them with target ui; the
// window does the work.
public sealed partial class MainWindow
{
    private void RegisterCommanderCommands()
    {
        _router.RegisterUiHandler("go.root", invocation => GoRootAsync(invocation));
        _router.RegisterUiHandler("go.showInLeftPane", ListingOnly(invocation => ShowInPaneAsync(0, invocation)));
        _router.RegisterUiHandler("go.showInRightPane", ListingOnly(invocation => ShowInPaneAsync(1, invocation)));
        _router.RegisterUiHandler("view.swapPanes", _ => SwapPanes());
        _router.RegisterUiHandler("view.refresh", RefreshAsync);
        _router.RegisterUiHandler("view.sortByName", ListingOnly(invocation => SortActiveAsync(PaneSort.Name, invocation)));
        _router.RegisterUiHandler("view.sortByExtension", ListingOnly(invocation => SortActiveAsync(PaneSort.Extension, invocation)));
        _router.RegisterUiHandler("view.sortByModified", ListingOnly(invocation => SortActiveAsync(PaneSort.Modified, invocation)));
        _router.RegisterUiHandler("view.sortBySize", ListingOnly(invocation => SortActiveAsync(PaneSort.Size, invocation)));
    }

    // Ctrl+F3 to Ctrl+F6: the active pane's own order, sent with its listings; the same key again
    // reverses it. The other pane and panes.sort stay as they are.
    private async Task SortActiveAsync(string key, CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.Path.Length == 0)
        {
            return;
        }
        if (_session.ProtocolVersion < PaneSort.ProtocolFor(key))
        {
            ShowNotice($"Sorting by {key} needs a newer core.");
            return;
        }
        var sort = PaneSort.Next(pane.EffectiveSort, key);
        if (await pane.SortAsync(sort, invocation.RequestId))
        {
            ShowNotice($"Sorted by {PaneSort.Describe(sort)}.");
        }
    }

    // Ctrl+\: the drive's root, C:\, or the share's, \\server\share\.
    private async Task GoRootAsync(CommandInvocation invocation)
    {
        var root = DisplayFormat.Root(Active.Path);
        if (root.Length > 0 && !string.Equals(root.TrimEnd('\\'), Active.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            await Active.NavigateAsync(root, invocation.RequestId, NavigationKind.New, null);
        }
    }

    // Ctrl+Left, Ctrl+Right: the folder under the cursor, or the active pane's folder when the
    // cursor is on a file, shown in that pane. The right pane shows first when one pane was shown.
    private async Task ShowInPaneAsync(int target, CommandInvocation invocation)
    {
        var pane = Active;
        var folder = pane.EntryAt(pane.FocusIndex) is { IsFolder: true } entry ? entry.Path : pane.Path;
        if (folder.Length == 0)
        {
            return;
        }
        if (target == 1 && !_dual)
        {
            ApplyDual(true);
            _ = PersistAsync(ShellState.DualPaneKey, true);
        }
        await _panes[target].NavigateAsync(folder, invocation.RequestId);
    }

    // Ctrl+U: the two panes change places with everything they hold (folder, marks, history,
    // sort, search results). The keyboard stays on the same side, which shows the other folder.
    private void SwapPanes()
    {
        if (!_dual)
        {
            ShowNotice("Swapping needs two panes: press Ctrl+Shift+D.");
            return;
        }
        (_panes[0], _panes[1]) = (_panes[1], _panes[0]);
        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i].Index = i;
            _panes[i].IsActive = i == _active;
            _paneViews[i].Model = _panes[i];
        }
        if (_searchPane >= 0)
        {
            _searchPane = 1 - _searchPane;
        }
        UpdateStatus();
        UpdateNavigationButtons();
        UpdateCrumbs();
        _sidebar.SetActivePath(Active.Path);
        _terminal.SetActiveFolder(Active.Path);
        ScheduleToolContext();
        _paneViews[_active].Focus(FocusState.Programmatic);
    }

    // Ctrl+R: the folder listed again, for drives whose changes Windows does not report; the marks
    // stay. In search results, the search runs again.
    private Task RefreshAsync(CommandInvocation invocation) =>
        Active.Search is null ? Active.RefreshAsync(invocation.RequestId) : SearchWhenDueAsync(now: true);
}
