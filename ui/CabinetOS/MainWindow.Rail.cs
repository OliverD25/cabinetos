using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Sidebar;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI.Xaml;

namespace CabinetOS;

// The activity rail and the modular sidebar of the rail layout (docs/ui.md, "The activity rail and the
// sidebar"). The classic and right layouts keep the sidebar as it was: pinned folders and drives, no rail.
// The decisions are RailModel's, SidebarSizing's, WarmPages' and FolderTreeModel's (all in Core, all tested);
// this file lays them onto the window.
public sealed partial class MainWindow
{
    private const string RailTarget = "cabinetos_ui::rail";

    private readonly RailModel _rail = new([], []);
    private FolderTreeModel _tree = null!;
    private bool _railLayout;

    // The classic and right layouts have no rail: the Search view takes the sidebar's place while it is asked for
    // (view.showSearch), and the folders come back when the search is left (Phase 16 removed the command bar's field).
    private bool _searchInSidebar;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _treeDrawnTimer;

    // The view ui.sidebarView names, and the one that shows: a tool's ID names none until the tools are read.
    private string _wantedSidebarView = RailModel.Explorer;
    private string _sidebarView = RailModel.Explorer;

    // ui.sidebarWidth as the window knows it; null is the design's width.
    private double? _sidebarWidth;
    private double _windowWidth;
    private double _sidebarDragStart;
    private bool _sidebarDragging;

    // Whether the pointer moved this press: a click, or a double-click's half, writes no width.
    private bool _sidebarMoved;
    private bool _sidebarWillClose;
    private bool _treeLocked;
    private bool _autoReveal = true;
    private CancellationTokenSource? _reveal;

    // Writes on their way: the configuration the core sends meanwhile may still hold the old value and must not undo the click.
    private int _viewWrites;
    private int _railWrites;
    private int _widthWrites;

    private void SetUpRail()
    {
        _tree = new FolderTreeModel(new CoreFolderSource(_session));
        SidebarView.Tree.Tree = _tree;
        // The tree's pick is not subscribed here: the sidebar re-raises it as its own Navigate, which the window handles once
        // (SidebarView.Navigate). A second subscription ran go.toPath twice for one Enter or click in the tree.
        SidebarView.Tree.LockToggled += () => _ = _router.ExecuteAsync("sidebar.lock", trigger: "button");
        SidebarView.Tree.LocateRequested += () => _ = _router.ExecuteAsync("sidebar.locate", trigger: "button");
        SidebarView.Tree.EscapePressed += FocusPaneOrEditor;
        _sidebar.DrivesChanged += () =>
        {
            _tree.SetRoots(_sidebar.Drives.Select(d => (d.Path, d.Name)).ToList());
            FollowActiveFolder();
        };
        _sidebar.ActivePathChanged += _ => FollowActiveFolder();

        Rail.Bind(_rail, button => RailModel.IsActive(button, _sidebarOpen, _sidebarView, MarketView.IsOpen, _dockVisible));
        Rail.Clicked += OnRailClicked;
        Rail.MoveRequested += OnRailMoveRequested;

        SidebarSplitter.DragStarted += () =>
        {
            _sidebarDragging = true;
            _sidebarMoved = false;
            _sidebarDragStart = SidebarColumn.ActualWidth;
        };
        SidebarSplitter.Dragged += OnSidebarDragged;
        SidebarSplitter.DragCompleted += EndSidebarDrag;
        SidebarSplitter.DoubleClicked += ResetSidebarWidth;

        // The Search view's field drives the search (the command bar's field is gone since Phase 16).
        SearchPanelView.QueryChanged += SetSearchText;
        SearchPanelView.SearchNow += () => _ = SearchWhenDueAsync(now: true);
        // Esc leaves the field even when no search is running (the hit was chosen, or nothing was typed): the pane gets the keyboard.
        SearchPanelView.Cancelled += () =>
        {
            LeaveSidebarSearch();
            if (!EndSearch(focusPane: true))
            {
                FocusPaneOrEditor();
            }
        };
        SearchPanelView.HitChosen += hit =>
        {
            LeaveSidebarSearch();
            _ = GoToHitAsync(hit, null);
        };
        SearchPanelView.WholeVolumeChanged += wholeVolume =>
            _ = _router.ExecuteAsync("search.scope", CommandArgs.Object(("wholeVolume", wholeVolume)), "sidebar");
    }

    private void RegisterRailCommands()
    {
        _router.RegisterUiHandler("view.showExplorer", _ => ShowExplorer());
        _router.RegisterUiHandler("view.showSearch", _ => ShowSearchView());
        _router.RegisterUiHandler("sidebar.locate", _ => LocateActiveFolderAsync());
        _router.RegisterUiHandler("sidebar.lock", _ => ToggleTreeLock());
    }

    // ----- Layout -----

    // ui.layout is rail (or was): the rail shows, the Explorer has its tree, the divider works, and the width is the user's.
    private void ApplyRailLayout(bool rail)
    {
        _railLayout = rail;
        _searchInSidebar = false;
        Rail.Visibility = rail && _compact is null ? Visibility.Visible : Visibility.Collapsed;
        SidebarView.ShowTree = rail;
        ReapplyWidths();
        UpdateSidebarChrome();
        Diag.Info(RailTarget, rail ? "the rail layout is on" : "the rail layout is off");
    }

    private void ReapplyWidths() => UpdateWidths(_windowWidth > 0 ? _windowWidth : RootGrid.ActualWidth);

    // The sidebar's width, in every layout: the design's clamp, or the width the user dragged the divider to.
    private double SidebarWidthFor(double windowWidth) =>
        SidebarSizing.Effective(_sidebarWidth, WindowMetrics.Current.SidebarWidth(windowWidth), windowWidth);

    // What shows in the sidebar's column, and what the rail says, after any change of the layout, the open state or the view.
    private void UpdateSidebarChrome()
    {
        SidebarSplitter.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        var explorer = _railLayout ? _sidebarView == RailModel.Explorer : !_searchInSidebar;
        SidebarView.Visibility = explorer ? Visibility.Visible : Visibility.Collapsed;
        var search = _railLayout ? _sidebarView == RailModel.Search : _searchInSidebar;
        SearchPanelView.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        ShowSidebarPage(_railLayout && _sidebarOpen && _rail.Find(_sidebarView) is { Kind: RailKind.Tool } ? _sidebarView : null);
        UpdateRail();
        // The live check reads this line: it is where it learns the width the divider sits at.
        Diag.Info(RailTarget, "the sidebar shows a view",
            new LogField("view", _sidebarView),
            new LogField("open", _sidebarOpen),
            new LogField("width", (int)Math.Round(SidebarWidthFor(_windowWidth > 0 ? _windowWidth : RootGrid.ActualWidth))));
        if (_railLayout && _sidebarOpen && explorer)
        {
            FollowActiveFolder();
        }
    }

    // The pills follow what shows: the view on show, the marketplace, the terminal.
    private void UpdateRail() => Rail.Refresh();

    // ----- Views -----

    private void OnRailClicked(RailButton button)
    {
        var click = RailModel.Click(button, _sidebarOpen, _sidebarView, MarketView.IsOpen);
        Diag.Info(RailTarget, "a rail button was pressed", new LogField("button", button.Id), new LogField("action", click.Action.ToString()));
        switch (click.Action)
        {
            case RailAction.ShowView:
                if (click.LeaveMarketplace)
                {
                    CloseMarket(focusPane: false);
                }
                ShowSidebarView(click.View!, focus: true);
                break;
            case RailAction.CloseSidebar:
                SetSidebarOpen(false);
                break;
            case RailAction.ToggleMarketplace:
                _ = _router.ExecuteAsync("marketplace.browse", trigger: "rail");
                break;
            case RailAction.ToggleTerminal:
                _ = _router.ExecuteAsync("view.toggleTerminal", trigger: "rail");
                break;
        }
    }

    // ui.sidebar, as view.toggleSidebar writes it.
    private void SetSidebarOpen(bool open)
    {
        ApplySidebar(open);
        _ = PersistAsync(ShellState.SidebarKey, open);
    }

    private void ShowSidebarView(string id, bool focus)
    {
        if (RefuseInCompact("sidebar"))
        {
            return;
        }
        if (!_rail.IsSidebarView(id))
        {
            id = RailModel.Explorer;
        }
        var changed = id != _sidebarView || id != _wantedSidebarView;
        _sidebarView = _wantedSidebarView = id;
        if (_sidebarOpen)
        {
            UpdateSidebarChrome();
        }
        else
        {
            SetSidebarOpen(true);
        }
        if (changed)
        {
            _ = PersistSidebarViewAsync(id);
        }
        if (focus)
        {
            FocusSidebarView();
        }
    }

    // The view ui.sidebarView names, once the tools are known: a tool that is not installed shows the Explorer.
    private void ResolveSidebarView()
    {
        _sidebarView = _rail.IsSidebarView(_wantedSidebarView) ? _wantedSidebarView : RailModel.Explorer;
        UpdateSidebarChrome();
    }

    // The keyboard goes into the view on show: the tree, the search field, or the tool's page.
    private void FocusSidebarView()
    {
        if (!_railLayout || !_sidebarOpen)
        {
            return;
        }
        SidebarHost.UpdateLayout();
        switch (_sidebarView)
        {
            case RailModel.Explorer:
                SidebarView.Tree.FocusTree();
                break;
            case RailModel.Search:
                SearchPanelView.FocusQuery();
                break;
            default:
                FocusSidebarPage(_sidebarView);
                break;
        }
    }

    private void ShowExplorer()
    {
        if (RefuseInCompact("sidebar"))
        {
            return;
        }
        if (!_railLayout)
        {
            // The other layouts have the sidebar only: open it, and put the keyboard on its first folder.
            LeaveSidebarSearch();
            if (!_sidebarOpen)
            {
                SetSidebarOpen(true);
            }
            SidebarHost.UpdateLayout();
            SidebarView.FocusFirstRow();
            return;
        }
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: false);
        }
        ShowSidebarView(RailModel.Explorer, focus: true);
    }

    private Task ShowSearchView()
    {
        if (RefuseInCompact("sidebar"))
        {
            return Task.CompletedTask;
        }
        if (!_railLayout)
        {
            // Without a rail the Search view shows in the sidebar's place until the search is left.
            _searchInSidebar = true;
            if (_sidebarOpen)
            {
                UpdateSidebarChrome();
            }
            else
            {
                SetSidebarOpen(true);
            }
            SidebarHost.UpdateLayout();
            SearchPanelView.FocusQuery();
            return Task.CompletedTask;
        }
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: false);
        }
        ShowSidebarView(RailModel.Search, focus: true);
        return Task.CompletedTask;
    }

    // The classic and right layouts: the sidebar shows its folders again.
    private void LeaveSidebarSearch()
    {
        if (!_railLayout && _searchInSidebar)
        {
            _searchInSidebar = false;
            UpdateSidebarChrome();
        }
    }

    // ----- The folder tree -----

    private void ToggleTreeLock()
    {
        if (!_railLayout)
        {
            ShowNotice("The folder tree is in the rail layout (ui.layout: rail).");
            return;
        }
        _treeLocked = !_treeLocked;
        SidebarView.Tree.IsLocked = _treeLocked;
        ShowNotice(_treeLocked ? "The folder tree is locked: it stays as it is." : "The folder tree follows the active folder again.");
        if (!_treeLocked)
        {
            FollowActiveFolder();
        }
    }

    private async Task LocateActiveFolderAsync()
    {
        if (RefuseInCompact("sidebar"))
        {
            return;
        }
        if (!_railLayout)
        {
            ShowNotice("The folder tree is in the rail layout (ui.layout: rail).");
            return;
        }
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: false);
        }
        ShowSidebarView(RailModel.Explorer, focus: false);
        await RevealActiveFolderAsync(force: true, focus: true);
    }

    // The tree follows the active pane's folder while it shows, is not locked and sidebar.autoReveal is on.
    private void FollowActiveFolder()
    {
        if (_railLayout && _sidebarOpen && _sidebarView == RailModel.Explorer)
        {
            _ = RevealActiveFolderAsync(force: false, focus: false);
        }
    }

    // Opens the folders down to the active pane's folder (rows beside the path stay as they are) and scrolls to it.
    // A newer reveal stops an older one that still waits for a folder.
    private async Task RevealActiveFolderAsync(bool force, bool focus)
    {
        if (!_railLayout || (!force && (_treeLocked || !_autoReveal)))
        {
            return;
        }
        var path = _sidebar.ActivePath;
        if (path.Length == 0)
        {
            return;
        }
        _reveal?.Cancel();
        var cancellation = _reveal = new CancellationTokenSource();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var node = await _tree.RevealAsync(path, cancellation.Token);
        var revealMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        if (node is null)
        {
            if (force)
            {
                ShowNotice("This folder is not on a drive the tree knows.");
            }
            return;
        }
        Diag.Info(RailTarget, "the tree shows a folder", new LogField("path", node.Path), new LogField("rows", _tree.Rows.Count),
            new LogField("reveal_ms", Math.Round(revealMs, 1)));
        // The rows the reveal opened are laid out before the list scrolls to the one it marked.
        DispatcherQueue.TryEnqueue(() =>
        {
            var scrolling = System.Diagnostics.Stopwatch.GetTimestamp();
            SidebarView.Tree.ScrollTo(node, () =>
            {
                Diag.Info(RailTarget, "the tree scrolled to a folder", new LogField("scroll_ms", Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(scrolling).TotalMilliseconds, 1)));
                LogTreeDrawnSoon();
            });
            if (focus)
            {
                _tree.SetCursor(node);
                SidebarView.Tree.FocusTree();
            }
        });
    }

    // Once the layout after the reveal has settled: how many rows the list has drawn. The tests read it, since a tree with all
    // its rows in the model and none on the screen looks full in every other line of the log. The look is made 600 ms after the
    // scroll; one that finds no row inside the window is made again every 200 ms for 10 s, and only the last is logged. A machine
    // under load lays the list out late (1.8 s in a test run beside a full suite), and a look at a fixed time called a tree that
    // was only slow an empty one.
    private void LogTreeDrawnSoon()
    {
        // A newer reveal replaces the look of an older one.
        _treeDrawnTimer?.Stop();
        // Held in a field: a timer nothing refers to may be collected before it ticks.
        var timer = _treeDrawnTimer = DispatcherQueue.CreateTimer();
        var scrolled = System.Diagnostics.Stopwatch.GetTimestamp();
        timer.IsRepeating = true;
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) =>
        {
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(scrolled);
            if (waited < TimeSpan.FromMilliseconds(500))
            {
                return;
            }
            var visible = SidebarView.Tree.VisibleRowCount();
            if (visible == 0 && waited < TimeSpan.FromSeconds(10))
            {
                return;
            }
            timer.Stop();
            Diag.Info(RailTarget, "the tree drew rows", new LogField("rows", _tree.Rows.Count),
                new LogField("drawn", SidebarView.Tree.RealizedRowCount), new LogField("visible", visible),
                new LogField("looked_after_ms", Math.Round(waited.TotalMilliseconds)));
        };
        timer.Start();
    }

    // ----- The divider -----

    // The width follows the pointer; under 150 px the column fades to say that letting go closes the sidebar.
    private void OnSidebarDragged(double delta)
    {
        // A move of under a pixel before the first real one is the pointer's jitter, not a drag.
        if (!_sidebarDragging || (!_sidebarMoved && Math.Abs(delta) < 1))
        {
            return;
        }
        ResizeSidebar(_sidebarDragStart + delta);
    }

    private void ResizeSidebar(double proposed)
    {
        _sidebarMoved = true;
        var drag = SidebarSizing.Drag(proposed, _windowWidth);
        _sidebarWillClose = drag.Close;
        SidebarColumn.Width = drag.Width;
        SidebarColumn.Opacity = drag.Close ? 0.4 : 1;
    }

    private void EndSidebarDrag()
    {
        _sidebarDragging = false;
        SidebarColumn.Opacity = 1;
        if (!_sidebarMoved)
        {
            return;
        }
        _sidebarMoved = false;
        if (_sidebarWillClose)
        {
            // Snapped shut: the width it had before stays for the next time it opens.
            _sidebarWillClose = false;
            SetSidebarOpen(false);
            ReapplyWidths();
            return;
        }
        var width = SidebarColumn.Width;
        _sidebarWidth = width;
        _ = PersistSidebarWidthAsync(width);
    }

    // A double-click on the divider: the design's width again, and null in the file. One that changes nothing writes nothing.
    // The double-click's event comes while the second press is still down, before its release: that press is no drag, so the drag
    // ends here, or its release would write the width that was just taken away.
    private void ResetSidebarWidth()
    {
        _sidebarDragging = false;
        _sidebarMoved = false;
        _sidebarWillClose = false;
        SidebarColumn.Opacity = 1;
        if (_sidebarWidth is null)
        {
            return;
        }
        _sidebarWidth = null;
        ReapplyWidths();
        _ = PersistSidebarWidthResetAsync();
    }

    // ----- What the rail keeps in the configuration (ui.rail, ui.sidebarWidth, ui.sidebarView) -----

    private void OnRailMoveRequested(string id, int delta)
    {
        if (_rail.Move(id, delta))
        {
            _ = PersistRailAsync();
            Rail.FocusButton(id);
        }
    }

    // The default order is stored as an empty list.
    private async Task PersistRailAsync()
    {
        _railWrites++;
        try
        {
            await _settingsWriter.SetAsync("ui.rail", _rail.IsDefaultOrder ? [] : _rail.Order);
        }
        finally
        {
            _railWrites--;
        }
    }

    private async Task PersistSidebarViewAsync(string view)
    {
        _viewWrites++;
        try
        {
            await _settingsWriter.SetAsync("ui.sidebarView", view);
        }
        finally
        {
            _viewWrites--;
        }
    }

    private async Task PersistSidebarWidthAsync(double width)
    {
        _widthWrites++;
        try
        {
            await _settingsWriter.SetAsync("ui.sidebarWidth", SidebarSizing.ToSetting(width));
        }
        finally
        {
            _widthWrites--;
        }
    }

    private async Task PersistSidebarWidthResetAsync()
    {
        _widthWrites++;
        try
        {
            await _settingsWriter.SetNullAsync("ui.sidebarWidth");
        }
        finally
        {
            _widthWrites--;
        }
    }

    // The rail's four settings from the configuration, at start and after an edit of the file. A write of the
    // window's own that is still on its way is not undone by the configuration sent before it landed.
    private void ApplyRailSettings(UiSettings settings, UiSettings previous, bool firstStart)
    {
        _autoReveal = settings.SidebarAutoReveal;
        if ((firstStart || !(settings.Rail ?? []).SequenceEqual(previous.Rail ?? [])) && _railWrites == 0)
        {
            _rail.SetOrder(settings.Rail ?? []);
        }
        if ((firstStart || settings.SidebarWidth != previous.SidebarWidth) && _widthWrites == 0 && !_sidebarDragging)
        {
            _sidebarWidth = settings.SidebarWidth;
            ReapplyWidths();
        }
        if ((firstStart || settings.SidebarView != previous.SidebarView) && _viewWrites == 0 && settings.SidebarView is { Length: > 0 } view)
        {
            _wantedSidebarView = view;
            ResolveSidebarView();
        }
        if (!_autoReveal)
        {
            return;
        }
        if (firstStart || !previous.SidebarAutoReveal)
        {
            FollowActiveFolder();
        }
    }

    // ----- Plugin events and tools -----

    // The plugin event badge { view, kind }: a dot or a spinner on a button, or none (docs/ui.md).
    private void OnBadgeEvent(PluginEventEvent pluginEvent)
    {
        if (PluginEvents.BadgeOf(pluginEvent.Name, pluginEvent.Payload) is { } badge && _rail.SetBadge(badge.View, badge.Kind))
        {
            Diag.Info(RailTarget, "a badge changed", new LogField("plugin", pluginEvent.PluginId), new LogField("view", badge.View),
                new LogField("kind", badge.Kind ?? "none"));
        }
    }

    // The tools are read: the ones with a sidebar page get their buttons.
    private void OnToolsLoadedForRail()
    {
        _rail.SetTools(_tools.Tools.Where(t => t.Manifest.Sidebar).Select(t => new RailTool(t.Manifest.Id, t.Manifest.Name)).ToList());
        ResolveSidebarView();
    }

    // ----- The snapshot aid's steps for the rail -----

    // What the three layouts show, in one line: the tests read it, and so does anyone comparing a layout with what it should be.
    private void LogRailState(string label) => Diag.Info(RailTarget, "rail state",
        new LogField("label", label),
        new LogField("layout", _settings.Layout),
        new LogField("rail_visible", Rail.Visibility == Visibility.Visible),
        new LogField("tree_visible", SidebarView.ShowTree),
        new LogField("splitter_visible", SidebarSplitter.Visibility == Visibility.Visible),
        new LogField("sidebar_open", _sidebarOpen),
        new LogField("sidebar_width", (int)Math.Round(SidebarColumn.ActualWidth)),
        new LogField("view", _sidebarView),
        new LogField("buttons", string.Join(",", _rail.Order)),
        new LogField("active", string.Join(",", _rail.Buttons
            .Where(b => RailModel.IsActive(b, _sidebarOpen, _sidebarView, MarketView.IsOpen, _dockVisible)).Select(b => b.Id))),
        new LogField("badges", string.Join(",", _rail.Badges.Select(b => $"{b.Key}={b.Value}"))),
        new LogField("search_text", SearchPanelView.Query),
        new LogField("search_hits", SearchPanelView.HitCount),
        new LogField("tree_rows", _tree.Rows.Count),
        new LogField("tree_realized", SidebarView.Tree.RealizedRowCount),
        new LogField("tree_rows_visible", SidebarView.Tree.VisibleRowCount()),
        new LogField("tree_requests", _tree.Requests),
        new LogField("tree_current", _tree.Current?.Path ?? ""),
        new LogField("tree_locked", _treeLocked));

    // The row the tree marks is the folder the active pane shows (until:tree).
    private bool TreeFollowsActiveFolder() =>
        _tree.Current is { } current
        && string.Equals(FolderTreeModel.Normalize(current.Path), FolderTreeModel.Normalize(_sidebar.ActivePath), StringComparison.OrdinalIgnoreCase);

    // tree-state:<label>|<folder> writes what the tree shows of a folder: whether it has a row, whether it is a hidden folder
    // and how opaque its drawn name is, and the names of the rows right under it. "current" is the folder the tree marked.
    private void LogTreeState(string label, string folder)
    {
        var node = _tree.Find(folder);
        var below = new List<string>();
        if (node is not null)
        {
            for (var i = _tree.Rows.IndexOf(node) + 1; i > 0 && i < _tree.Rows.Count && _tree.Rows[i].Depth > node.Depth; i++)
            {
                if (_tree.Rows[i].Depth == node.Depth + 1)
                {
                    below.Add(_tree.Rows[i].Name);
                }
            }
        }
        Diag.Info(RailTarget, "tree state",
            new LogField("label", label),
            new LogField("folder", folder),
            new LogField("found", node is not null),
            new LogField("hidden", node?.IsHidden ?? false),
            new LogField("name_opacity", node is null ? null : SidebarView.Tree.NameOpacity(node)),
            new LogField("children", string.Join("|", below)),
            new LogField("current", _tree.Current?.Path ?? ""));
    }

    // rail:<id> presses that button; rail-move:<id>|<1 or -1> moves it (Shift+Down, Shift+Up);
    // divider:<pixels> drags the divider to that width and lets go; tree:<path> opens the folder in the tree
    // (and the ones on the way); rail-state:<label> writes what the rail, the sidebar and the tree show into the log;
    // tree-state:<label>|<folder> writes what the tree shows of one folder.
    private async Task RunRailStepAsync(string kind, string argument)
    {
        switch (kind)
        {
            case "rail" when _rail.Find(argument) is { } button:
                OnRailClicked(button);
                break;
            case "rail-move" when argument.Split('|') is [var id, var delta] && int.TryParse(delta, out var step):
                OnRailMoveRequested(id, step);
                break;
            case "rail-state":
                // What the sidebar's column, the rows and the views measure is laid out on XAML's next frames.
                await SettleFramesAsync();
                LogRailState(argument);
                break;
            case "tree-state" when argument.Split('|') is [var stateLabel, var stateFolder]:
                LogTreeState(stateLabel, stateFolder);
                break;
            case "divider" when double.TryParse(argument, System.Globalization.CultureInfo.InvariantCulture, out var width):
                _sidebarDragging = true;
                _sidebarDragStart = SidebarColumn.ActualWidth;
                ResizeSidebar(width);
                EndSidebarDrag();
                break;
            case "tree":
                if (await _tree.RevealAsync(argument) is { } node)
                {
                    await _tree.ExpandAsync(node);
                    SidebarView.Tree.ScrollTo(node);
                }
                break;
        }
        await Task.Delay(400);
        // A new sidebar width, a view shown or a row drawn is laid out on XAML's next frames, which a busy machine draws late:
        // "rail-state" read the old width (224 where 320 was set) after the 400 ms.
        await SettleFramesAsync();
    }
}
