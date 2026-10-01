using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Tabs;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

// Tabs per pane (Phase 12; docs/ui.md, "Tabs"). The window owns the tab state: each pane has a
// TabStrip; the pane's model holds the live state of the tab in front (listing, cursor, marks,
// history, order), and a tab behind holds its own until it comes to the front again. The window
// saves the tabs as ui.tabs and tells the core what it shows (window_state) on every change.
public sealed partial class MainWindow
{
    private const string TabsTarget = "cabinetos_ui::tabs";

    // The pane's tabs, and the tab whose state the pane's model holds now (a tool tab in front leaves it the last folder tab).
    private readonly TabStrip[] _strips = [new(new PaneTab("")), new(new PaneTab(""))];
    private readonly PaneTab?[] _held = new PaneTab?[2];
    private readonly Task[] _tabWork = [Task.CompletedTask, Task.CompletedTask];
    private readonly bool[] _rowLogged = new bool[2];
    private PaneTabs[] _tabViews = null!;
    private TabsConfig _savedTabs = TabsConfig.Empty;
    private DispatcherQueueTimer _tabsSaveTimer = null!;
    private DispatcherQueueTimer _stateTimer = null!;
    private string _tabsWritten = "";
    private bool _tabsStarted;
    private bool _stateUnavailable;

    private void SetUpTabs()
    {
        _tabViews = [LeftTabs, RightTabs];
        for (var i = 0; i < _tabViews.Length; i++)
        {
            var pane = i;
            _held[pane] = _strips[pane].Active;
            _strips[pane].Changed += OnTabsChanged;
            _tabViews[pane].PaneIndex = pane;
            _tabViews[pane].Model = _strips[pane];
            _tabViews[pane].RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
            _tabViews[pane].KeysOf = KeysOf;
            // A locked tab does not list another folder: the folder opens in a new tab beside it.
            _panes[pane].LockedNavigation = (path, requestId, selectName) => OpenFolderInNewTabAsync(pane, path, requestId, selectName);
        }
        // The tabs are saved at most once a second, and the core is told what the window shows at most every 50 ms.
        _tabsSaveTimer = DispatcherQueue.CreateTimer();
        _tabsSaveTimer.IsRepeating = false;
        _tabsSaveTimer.Interval = TimeSpan.FromSeconds(1);
        _tabsSaveTimer.Tick += (_, _) => _ = SaveTabsAsync();
        _stateTimer = DispatcherQueue.CreateTimer();
        _stateTimer.IsRepeating = false;
        _stateTimer.Interval = TimeSpan.FromMilliseconds(50);
        _stateTimer.Tick += (_, _) => _ = SendWindowStateAsync();
    }

    private void RegisterTabCommands()
    {
        _router.RegisterUiHandler("tab.new", invocation => NewTabAsync(TabTarget(invocation).Pane, folder: null, invocation.RequestId));
        _router.RegisterUiHandler("tab.close", invocation =>
        {
            var (pane, index) = TabTarget(invocation);
            return CloseTabAsync(pane, index, invocation.RequestId);
        });
        // A key a tool's page passes back names the pane of that page (PageKeyArguments); a key in a list means the active pane.
        _router.RegisterUiHandler("tab.next", invocation =>
        {
            var pane = TabPaneOf(invocation);
            return ActivateTabAsync(pane, _strips[pane].NextIndex(), invocation.RequestId);
        });
        _router.RegisterUiHandler("tab.previous", invocation =>
        {
            var pane = TabPaneOf(invocation);
            return ActivateTabAsync(pane, _strips[pane].PreviousIndex(), invocation.RequestId);
        });
        _router.RegisterUiHandler("tab.toggleLock", invocation =>
        {
            var (pane, index) = TabTarget(invocation);
            ToggleTabLock(pane, index);
        });
        _router.RegisterUiHandler("tab.openFolderInNewTab", ListingOnly(invocation =>
        {
            var pane = Active;
            var folder = pane.EntryAt(pane.FocusIndex) is { IsFolder: true } entry ? entry.Path : pane.Path;
            return folder.Length == 0 ? Task.CompletedTask : NewTabAsync(_active, folder, invocation.RequestId);
        }));
        _router.RegisterUiHandler("tab.moveToOtherPane", invocation =>
        {
            var (pane, index) = TabTarget(invocation);
            var to = CommandArgs.Text(invocation.Args, "to") switch { "left" => 0, "right" => 1, _ => 1 - pane };
            return MoveTabAsync(pane, index, to, invocation.RequestId);
        });
        // Go to Tab (Phase 16): Ctrl+1 to Ctrl+9 name the tab by its place (KeyArguments), a click on the strip by its
        // index. Without a tab to go to (the palette) there is nothing to choose; a place past the last tab does nothing.
        _router.RegisterUiHandler("tab.select", invocation =>
        {
            if (CommandArgs.Number(invocation.Args, "tab") is null)
            {
                ShowNotice(KeysOf("tab.select") is { Length: > 0 } keys
                    ? $"Go to Tab is a key per place: {keys} goes to the first tab, the next digits to the tabs after it."
                    : "Go to Tab is a key per place: bind it to a key that ends in a digit.");
                return Task.CompletedTask;
            }
            var (pane, index) = TabTarget(invocation);
            return ActivateTabAsync(pane, index, invocation.RequestId);
        });
    }

    // The pane and the tab a tab command concerns: the row's buttons name them, a key or the palette means the front tab of the active pane.
    private (int Pane, int Index) TabTarget(CommandInvocation invocation)
    {
        var pane = TabPaneOf(invocation);
        var index = CommandArgs.Number(invocation.Args, "tab") is { } tab ? (int)tab : _strips[pane].ActiveIndex;
        return (pane, index);
    }

    private int TabPaneOf(CommandInvocation invocation) =>
        CommandArgs.Number(invocation.Args, "pane") is { } named and <= 1 ? (int)named : _active;

    // A chord's key names the side: Ctrl+K Ctrl+Right sends the tab to the right pane, Ctrl+K Ctrl+Left to the left one.
    // A digit names the tab of Go to Tab: Ctrl+1 is the first, Ctrl+9 the ninth (Phase 16).
    private static JsonElement? KeyArguments(string command, KeySequence keys) => command switch
    {
        "tab.moveToOtherPane" when keys.Second is { Key: "left" or "right" } second => CommandArgs.With("to", second.Key),
        "tab.select" when (keys.Second ?? keys.First) is { Key: [>= '1' and <= '9'] digit } => CommandArgs.Object(("tab", digit[0] - '1')),
        _ => null,
    };

    private void InstallStrip(int pane, TabStrip strip)
    {
        _strips[pane].Changed -= OnTabsChanged;
        _strips[pane] = strip;
        strip.Changed += OnTabsChanged;
        _tabViews[pane].Model = strip;
    }

    // ----- Reconciling the pane with its front tab -----

    /// <summary>
    /// Makes the pane show the tab in front of its strip: a folder tab is
    /// listed again with its history, order, cursor and marks (the tab that
    /// was live parks its own), a tool tab shows in the pane's editor. Called
    /// after every change of the strip, queued per pane, so it always
    /// reconciles with the strip as it is when it runs.
    /// </summary>
    private Task QueueShow(int pane, bool giveKeys, string? requestId = null, bool reopenTool = false)
    {
        var previous = _tabWork[pane];
        return _tabWork[pane] = Work();

        async Task Work()
        {
            try
            {
                await previous;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Diag.Warn(TabsTarget, "an earlier tab change failed", new LogField("error", error.Message));
            }
            await ShowFrontTabAsync(pane, giveKeys, requestId, reopenTool);
        }
    }

    private async Task ShowFrontTabAsync(int pane, bool giveKeys, string? requestId, bool reopenTool = false)
    {
        var strip = _strips[pane];
        var tab = strip.Active;
        // A listing kept for a tab that was closed or went to the other pane has no tab to come back to.
        _panes[pane].ReleaseKeptUnlessAmong(strip.Tabs);
        if (_searchPane == pane)
        {
            EndSearch(focusPane: false);
        }
        if (tab.IsTool)
        {
            if (!await ShowToolTabAsync(pane, tab, giveKeys, reopenTool) && strip.IndexOf(tab) >= 0)
            {
                // The tool could not open the file: its tab goes, and the folder tab beside it shows.
                strip.Remove(strip.IndexOf(tab));
                await ShowFrontTabAsync(pane, giveKeys, requestId);
                return;
            }
            // The folder tab's find waits behind the tool.
            UpdateFind(pane);
            return;
        }
        var covered = _editorViews[pane].IsOpen;
        var hadKeys = giveKeys || covered && _editorViews[pane].HasFocus;
        _editorViews[pane].Hide();
        var view = _paneViews[pane];
        view.Visibility = Visibility.Visible;
        if (_held[pane] != tab)
        {
            // The columns belong to the tab that goes behind: it keeps its deepest folder and its mode, not its columns.
            CloseColumnView(pane);
            PaneTab? leaving = null;
            if (_held[pane] is { IsTool: false } old && strip.IndexOf(old) >= 0)
            {
                _panes[pane].CaptureInto(old);
                // The list's scroll position is the view's, not the model's: the tab keeps it too (Phase 16).
                old.ScrollOffset = view.ScrollOffset;
                // Its listing is kept for a while, so coming back does not list the folder again; a tab in the
                // column view keeps none (ADR 0016).
                leaving = old.Mode == TabMode.Files ? old : null;
            }
            _held[pane] = tab;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!await _panes[pane].RestoreAsync(tab, [profile, @"C:\"], requestId, leaving))
            {
                Diag.Warn(TabsTarget, "a tab could not show its folder", new LogField("pane", pane), new LogField("path", tab.Path));
            }
            else if (tab.ScrollOffset > 0)
            {
                view.ScrollTo(tab.ScrollOffset);
            }
            Diag.Info(TabsTarget, "tab shown", new LogField("pane", pane), new LogField("path", tab.Path), new LogField("index", strip.ActiveIndex),
                new LogField("tabs", strip.Count), new LogField("locked", tab.Locked));
        }
        // A tab in the columns mode starts its column view at its folder (ADR 0016).
        SyncColumnView(pane);
        UpdateNavigationButtons();
        UpdateFind(pane);
        ScheduleToolContext();
        if (hadKeys)
        {
            view.UpdateLayout();
            if (!view.Focus(Microsoft.UI.Xaml.FocusState.Programmatic))
            {
                DispatcherQueue.TryEnqueue(() => view.Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
            }
        }
    }

    // ----- Commands -----

    // A tab in front of another: the pane changes what it shows.
    private Task ActivateTabAsync(int pane, int index, string? requestId = null)
    {
        var strip = _strips[pane];
        if ((uint)index >= (uint)strip.Count)
        {
            return Task.CompletedTask;
        }
        strip.Select(index);
        return QueueShow(pane, giveKeys: true, requestId);
    }

    private Task NewTabAsync(int pane, string? folder, string? requestId)
    {
        var model = _panes[pane];
        var path = folder ?? TabFolder(pane);
        if (path.Length == 0)
        {
            return Task.CompletedTask;
        }
        // Same folder (or the one asked for), the pane's order and the front tab's mode, a history of its own, not locked.
        _strips[pane].Add(new PaneTab(path, mode: FrontMode(pane)) { Sort = model.Sort });
        return QueueShow(pane, giveKeys: true, requestId);
    }

    // What a locked tab does when it is asked for another folder: the folder opens in a new tab beside it.
    private async Task<bool> OpenFolderInNewTabAsync(int pane, string folder, string? requestId, string? selectName)
    {
        var tab = new PaneTab(folder, mode: FrontMode(pane)) { Sort = _panes[pane].Sort, CursorName = selectName };
        _strips[pane].Add(tab);
        await QueueShow(pane, giveKeys: true, requestId);
        return _held[pane] == tab && _panes[pane].Path.Length > 0;
    }

    // The mode a new tab of the pane starts in: the folder tab's it holds.
    private TabMode FrontMode(int pane) => _held[pane] is { IsTool: false } tab ? tab.Mode : TabMode.Files;

    private async Task CloseTabAsync(int pane, int index, string? requestId)
    {
        var strip = _strips[pane];
        if ((uint)index >= (uint)strip.Count)
        {
            return;
        }
        if (!strip.CanClose(index))
        {
            ShowNotice("A pane keeps its last folder tab.");
            return;
        }
        var tab = strip.Tabs[index];
        strip.Remove(index);
        if (tab.IsTool && strip.IndexOfTool(tab.Tool!) < 0 && _toolHosts[pane]?.Tool.Manifest.Id == tab.Tool)
        {
            EndToolHost(pane);
        }
        await QueueShow(pane, giveKeys: true, requestId);
    }

    private void ToggleTabLock(int pane, int index)
    {
        var strip = _strips[pane];
        if ((uint)index >= (uint)strip.Count || strip.Tabs[index].IsTool)
        {
            ShowNotice("A tab that shows a tool has no lock.");
            return;
        }
        var tab = strip.Tabs[index];
        var locked = strip.ToggleLock(index);
        if (_held[pane] == tab)
        {
            _panes[pane].IsLocked = locked;
        }
        UpdateNavigationButtons();
        ShowNotice(locked
            ? $"{tab.Title} is locked: a folder opened there opens in a new tab."
            : $"{tab.Title} is unlocked.");
    }

    private async Task MoveTabAsync(int from, int index, int to, string? requestId)
    {
        if (from == to)
        {
            ShowNotice($"This tab is in the {(to == 0 ? "left" : "right")} pane already.");
            return;
        }
        var strip = _strips[from];
        if ((uint)index >= (uint)strip.Count)
        {
            return;
        }
        var tab = strip.Tabs[index];
        if (tab.IsTool)
        {
            ShowNotice("A tab that shows a tool stays in its pane: close it and open the file in the other pane.");
            return;
        }
        if (!strip.CanClose(index))
        {
            ShowNotice("A pane keeps its last folder tab: open another tab first (Ctrl+T).");
            return;
        }
        if (to == 1 && !EnsureDual())
        {
            return;
        }
        // The pane's own state goes with the tab: its history, order, cursor and marks (of its deepest column in the column view).
        if (_held[from] == tab)
        {
            CloseColumnView(from);
            _panes[from].CaptureInto(tab);
        }
        TabStrip.Move(strip, index, _strips[to]);
        SetActive(to);
        await Task.WhenAll(QueueShow(from, giveKeys: false), QueueShow(to, giveKeys: true, requestId));
    }

    // A tool was removed (from the marketplace, or by hand): its tabs close, and so does its editor.
    private void ForgetTool(string toolId)
    {
        for (var pane = 0; pane < _strips.Length; pane++)
        {
            var strip = _strips[pane];
            var removed = false;
            for (var i = strip.Count - 1; i >= 0; i--)
            {
                if (strip.Tabs[i].Tool == toolId)
                {
                    strip.Remove(i);
                    removed = true;
                }
            }
            if (_toolHosts[pane]?.Tool.Manifest.Id == toolId)
            {
                EndToolHost(pane);
            }
            if (removed)
            {
                _ = QueueShow(pane, giveKeys: false);
            }
        }
    }

    // Back and Forward in a locked tab: it stays on its folder.
    private Task StayInLockedTab()
    {
        ShowNotice("This tab is locked: it stays on its folder.");
        return Task.CompletedTask;
    }

    // One pane with a tool in it, and a search typed: the hits need the pane's list, so its folder tab comes to the front.
    private void ShowFolderTabBehindTool(int pane)
    {
        var strip = _strips[pane];
        var tabs = strip.Tabs.ToList();
        var folder = tabs.FindIndex(t => !t.IsTool && t == _held[pane]);
        if (folder < 0)
        {
            folder = tabs.FindIndex(t => !t.IsTool);
        }
        strip.Select(folder);
        _ = QueueShow(pane, giveKeys: false);
    }

    // ----- Rows, saving, and telling the core -----

    private void OnTabsChanged()
    {
        for (var i = 0; i < _strips.Length; i++)
        {
            // A strip shown or hidden is in the log for the live check, which cannot see the window's tree.
            // Since Phase 16 a pane's strip shows from its first tab on.
            if (_strips[i].ShowsRow != _rowLogged[i])
            {
                _rowLogged[i] = _strips[i].ShowsRow;
                Diag.Info(TabsTarget, _rowLogged[i] ? "tab row shown" : "tab row hidden", new LogField("pane", i), new LogField("tabs", _strips[i].Count));
            }
        }
        UpdateTabRows();
        SaveTabsSoon();
        ScheduleWindowState();
    }

    // The active pane's tab in front has the brighter fill, as its toolbar row; a tab may move only while the other pane
    // shows. The top row's title follows the active pane's tab in front.
    private void UpdateTabRows()
    {
        for (var i = 0; i < _tabViews.Length; i++)
        {
            _tabViews[i].IsActivePane = _dual && i == _active || !_dual && i == 0;
            _tabViews[i].CanMoveToOtherPane = _dual;
        }
        UpdateTitle();
    }

    // The tab in front follows its pane's folder, whatever moved the pane (a click, Back, a lost folder); in the column view
    // it follows the deepest column (ADR 0016).
    private void SyncTabFolder(PaneModel pane)
    {
        var index = Array.IndexOf(_panes, pane);
        if (index >= 0 && _held[index] is { IsTool: false } tab && TabFolder(index) is var folder && tab.Path != folder)
        {
            tab.Path = folder;
            _strips[index].Touch();
        }
    }

    private void SaveTabsSoon()
    {
        if (_tabsStarted && !_tabsSaveTimer.IsRunning)
        {
            _tabsSaveTimer.Start();
        }
    }

    private TabsConfig CurrentTabs() => new(_strips[0].ToConfig(), _strips[1].ToConfig());

    /// <summary>Writes <c>ui.tabs</c> when it differs from what the file has; at most once a second, and once more when the window closes.</summary>
    private async Task SaveTabsAsync(CancellationToken cancellationToken = default)
    {
        _tabsSaveTimer.Stop();
        var tabs = CurrentTabs();
        // A pane whose folder is not known yet has nothing to save.
        if (!_tabsStarted || tabs.Left.Items.Any(i => i.Path.Length == 0) || tabs.Right.Items.Any(i => i.Path.Length == 0))
        {
            return;
        }
        var value = tabs.ToJson();
        var text = value.GetRawText();
        if (text == _tabsWritten)
        {
            return;
        }
        if (await _settingsWriter.SetAsync(TabsConfig.Key, value, cancellationToken))
        {
            _tabsWritten = text;
            Diag.Info(TabsTarget, "tabs saved", new LogField("left", tabs.Left.Items.Count), new LogField("right", tabs.Right.Items.Count),
                new LogField("columns", tabs.Left.Items.Concat(tabs.Right.Items).Count(i => i.Mode == TabMode.Columns)));
        }
    }

    private void ScheduleWindowState()
    {
        if (_tabsStarted && !_stateUnavailable && !_stateTimer.IsRunning)
        {
            _stateTimer.Start();
        }
    }

    // window_state: the active pane, each pane's tabs, cursor and marks. The core only stores it, so nothing in the window depends on it.
    private async Task SendWindowStateAsync()
    {
        _stateTimer.Stop();
        if (_stateUnavailable || _closing)
        {
            return;
        }
        var request = WindowStateBuilder.Build(_active, Snapshot(0), Snapshot(1));
        try
        {
            if (await _session.RequestAsync(request) is ErrorReply { Code: ErrorCodes.UnknownRequest })
            {
                _stateUnavailable = true;
                Diag.Info(TabsTarget, "the core does not answer window_state; the window stops telling it what it shows");
            }
        }
        catch (IOException error)
        {
            Diag.Debug(TabsTarget, "cannot send the window state", new LogField("error", error.Message));
        }
    }

    private PaneSnapshot Snapshot(int index)
    {
        var strip = _strips[index];
        var pane = _panes[index];
        // A tool in front has no cursor and no marks; a search shows hits, not the listing's rows.
        var listing = !strip.Active.IsTool && pane.Search is null && _held[index] == strip.Active;
        return new PaneSnapshot(
            strip.Tabs,
            strip.ActiveIndex,
            listing ? pane.EntryAt(pane.FocusIndex)?.Path : null,
            listing ? pane.MarkedPaths(WindowStateBuilder.MaxMarked) : [],
            // In the column view the cursor and the marks are the keyboard's column's rows: its folder goes with them.
            listing && _columnViews[index] is not null ? pane.Path : null);
    }

    // ----- Start and close -----

    // The saved tabs of a pane (ui.tabs): the tab in front is listed, a folder that is gone falls back to its parents and then to the pane's first-start folder.
    private async Task OpenSavedTabsAsync(int pane, TabStrip strip, string fallback)
    {
        InstallStrip(pane, strip);
        _held[pane] = strip.Active;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await _panes[pane].RestoreAsync(strip.Active, [fallback, profile, @"C:\"]);
        // A tab saved in the columns mode starts with its folder as the one column.
        SyncColumnView(pane);
    }

    private async Task FlushTabsAsync(CancellationToken cancellationToken)
    {
        if (_tabsStarted)
        {
            await SaveTabsAsync(cancellationToken);
        }
    }

    // ----- The snapshot aid -----

    // tab:new, tab:close, tab:next, tab:previous, tab:lock, tab:folder, tab:move, tab:select <index>: the command as its key would run it.
    private async Task RunTabStepAsync(string argument)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries);
        var (command, args) = parts[0] switch
        {
            "new" => ("tab.new", (JsonElement?)null),
            "close" => ("tab.close", null),
            "next" => ("tab.next", null),
            "previous" => ("tab.previous", null),
            "lock" => ("tab.toggleLock", null),
            "folder" => ("tab.openFolderInNewTab", null),
            "move" => ("tab.moveToOtherPane", null),
            "select" when parts.Length > 1 && int.TryParse(parts[1], out var index) => ("tab.select", CommandArgs.Object(("pane", _active), ("tab", index))),
            _ => ("", null),
        };
        if (command.Length == 0)
        {
            Diag.Info("cabinetos_ui::snapshot", "tab: no such step", new LogField("step", argument));
            return;
        }
        await _router.ExecuteAsync(command, args, "snapshot");
        await Task.Delay(300);
    }

    // tabs:<label>: what each pane's row shows, in the log, for the checks that read it.
    private void LogTabsForSnapshot(string label) =>
        Diag.Info("cabinetos_ui::snapshot", "tabs shown", new LogField("label", label), new LogField("left", _tabViews[0].Describe()),
            new LogField("right", _tabViews[1].Describe()), new LogField("left_look", _tabViews[0].DescribeLook()),
            new LogField("right_look", _tabViews[1].DescribeLook()), new LogField("left_row", Math.Round(_tabViews[0].RowHeight, 1)),
            new LogField("right_row", Math.Round(_tabViews[1].RowHeight, 1)), new LogField("active_pane", _active));
}
