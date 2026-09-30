## Phase 13, the activity rail and the modular sidebar

Agent: ac9d2107a90527bf1

Report: Phase 13, the activity rail and the modular sidebar

Context: item 4 of the night run (the activity rail and modular sidebar, layout `rail`), plus the coordinator's two small items (general plugin-command rule; fix of the Agent steps in live-check section 14). All work is pushed to origin main. Tip is 1a05a5c. Worktree: E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.claude\worktrees\agent-ac9d2107a90527bf1

## 1. Result in one paragraph
Everything asked is built, tested and verified with real keys and mouse. The last whole-script live-check run (tabs, 13 rail, 14 drag and ask, all in one run, no other section broken) answered 105 True and 0 False. The script still exits 1 with `-Strict`, only because of the scroll goal ("met: no; 302 frames in 5 s, 85 over 20 ms (28.1 %), 2 over 33 ms, worst 51.3 ms"). That is item 5's, not mine.

## 2. Test and build results
- UI tests: 839 total. With `CABINETOS_UI_E2E=1` and the worktree's release core: total 839, failed 0, succeeded 839, skipped 0, duration 48 s. Without E2E: 833 pass, 6 skipped. Debug and Release builds of the window: 0 warnings, 0 errors.
- Core: not touched in this segment. I rebuilt the release core after the rebase; nothing to recompile.
- Live check, final run: `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\live-check\run-rail-5.txt` (311 lines; 105 answers True, 0 False; DONE.md says "Checks that answered False: none"; Notepad closed afterwards, no CabinetOS or core process left).

## 3. Commits (all on main now)
Earlier in the item: 68ee1f0 (Core: RailModel, SidebarSizing, WarmPages, FolderTree, manifest `sidebar`), 84c6f76 (window: rail, tree, search view, tool pages, docs), 4cd925e (general `notice` rule), c0b0e66/74510e7 (CABINETOS_UNDO_DIR), 5d6e82b (command seeds, 97 commands), 80d5ed8, 409a1b8, d6e2de7. In this segment:
- a42c343 ui: a sidebar page hands back the view keys, Esc leaves the search field, a page runs the commands of the plugin it follows
- fd81d8b live check: section 13 for the rail layout, and extended keys for the cursor block
- 15d9c29 ui: Esc in the rail layout's rail or sidebar gives the keyboard to the pane
- 1a05a5c live check: the Agent steps of section 14 run, and the run keeps to its own folders

## 4. The coordinator's two items
(1) General plugin-command rule (a42c343). `ToolMessages.MayRun(commandId, CommandSource? source, Func<string,bool> follows)` in `ui/CabinetOS.Core/Tools/ToolMessages.cs`. A tool page may run a command of the fixed list, or a command that (a) is listed with `source.Kind == "plugin"`, (b) has an id that starts with `<plugin id>.` and (c) belongs to a plugin the page follows (`subscribe`). No plugin name is in the window (Article 10). Following a name like `file` gains no core command, because the source of `file.delete` is the core. The window passes `_router.Find(id)?.Source` and `host.Subscriptions.Wants` (`RunToolCommand` in `ui/CabinetOS/MainWindow.Tools.cs`). Unit test: `A_page_that_follows_a_plugin_may_run_that_plugin_s_commands_and_no_other` in `ui/CabinetOS.Tests/ToolTests.cs` (unfollowed, followed, no source, core source, `agentx.`, `agent.`, `file.delete`, following `file`, unsubscribe). Documented in `docs/tool-extensions.md` (a new row and paragraph in the commands table).
(2) Section 14 "ask" (1a05a5c, `ui/livecheck/livecheck.ps1`). What was wrong: it looked for Install inside the card (the button is in the detail column), named the card "Extension" (it is "WASM plugin", so "CabinetOS Agent, WASM plugin, by CabinetOS"), wrote `plugins.agent.provider` (real: `plugins.agent.settings.provider`), gave the fake provider no replies, and would have installed into the real app data. Now: the run builds its own marketplace index at the start (`build-index.ps1 -Extensions` when the agent plugin is built) and puts it in the initial config, so the marketplace never reads the network. `CABINETOS_PLUGINS_DIR`, `CABINETOS_PLUGINS_DATA_DIR` and `CABINETOS_MARKETPLACE_DIR` point into the run's folder. The step selects the card, presses Install and "Allow and install" through UI Automation, writes `plugins.agent.settings.provider = fake` after the install, writes `<plugins-data>\agent\fake-replies.json` (one text: a fenced block of three `rename "<dir>\photoN.jpg" vacation_N.jpg` lines), asks with Ctrl+K Ctrl+A, and applies with Enter. All ask answers were True (see section 8).
- Note for the main checkout: `sdk/extensions/agent/plugin/plugin.wasm` is git-ignored. In my worktree I copied `sdk/fixtures/plugins/agent/plugin.wasm` there (same code). Without that file the ask steps print "WAITING ... did not run". `sdk\extensions\build-extensions.ps1` makes it.

## 5. What was built in item 4 (files, names, numbers)
- Layout `ui.layout: rail` next to classic/right (default stays classic). 44 px column, 36 px buttons 4 px apart, 3 px accent pill. Buttons: Explorer, Search, Marketplace, Terminal, one per tool with `"sidebar": true`. Order in `ui.rail` (empty list = default). A second press on the active view's button closes the sidebar. Badges by plugin event `badge {view, kind: dot|spinner|null}`.
- Sidebar host: Explorer (Pinned and Drives, then a lazy folder tree over `list_directory`, one request per opened node, cancelled on collapse; follows the active pane unless locked or `ui.sidebarAutoReveal` false), Search view (same model as the command bar box), tool pages (own WebView2, data folder `sidebar-<id>`; the last hidden page warm, others `TrySuspendAsync`).
- Commands: `view.showExplorer` Ctrl+Shift+E, `view.showSearch` Ctrl+Shift+F, `sidebar.locate` Alt+Shift+L, `sidebar.lock`; `view.toggleSidebar` unchanged. Divider: under 150 px it snaps shut and keeps the last width (`ui.sidebarWidth`); max 480. Config fields `ui.rail`, `ui.sidebarWidth`, `ui.sidebarView`, `ui.sidebarAutoReveal` (schema from the extension agent's core commit af0a104).
- Files (new): `ui/CabinetOS.Core/Sidebar/{RailModel,SidebarSizing,WarmPages,FolderTree}.cs`, `ui/CabinetOS/Views/{ActivityRail,FolderTreeView,SearchPanel}.xaml(.cs)`, `ui/CabinetOS/Services/CoreFolderSource.cs`, `ui/CabinetOS/MainWindow.Rail.cs`, `MainWindow.SidebarTools.cs`, `ui/livecheck/fixtures/quick-notes/*`, `ui/CabinetOS.Tests/{SidebarTests,RailEndToEndTests}.cs`. Many edits in `MainWindow.*.cs`, `ToolManifest.cs`, `UiSettings.cs`, `sdk/tools/tool.schema.json`.
- Docs: `docs/ui.md` (section "The activity rail and the sidebar", snapshot steps, live check), `docs/keybindings.md`, `docs/tool-extensions.md`, README (ui test count 839).
- Snapshot aid steps: `rail:<id>`, `rail-move:<id>|<1 or -1>`, `rail-state:<label>`, `divider:<px>`, `tree:<path>`, `plugin-event:`.
- Earlier verified: the pixel comparison of classic and right layouts against snapshots from before the phase: 19 and 11 pixels of 2.5 million differ (two runs of one build differ by 6).

## 6. What the real keys found and I fixed in this segment (all inside item 4, with tests or live-check answers)
1. A tool's page in the sidebar passed back only the ways out (palette, terminal), so once it had the keyboard the mouse was the only way to the Explorer or Search. Now `TerminalKeys.SidebarPageWays` (`view.showExplorer`, `view.showSearch`, `view.toggleSidebar`) also pass, under the user's own keys (`PassKeys(..., moreWays)`, `_sidebarPageKeys`, `HandleKeyForPage` uses the page's own map). Test: `A_tool_page_in_the_sidebar_also_hands_back_the_keys_that_change_the_sidebar` in `TerminalTests.cs`. Article 7.
2. Closing the sidebar while the page, tree or field has the keyboard now gives it to the pane (`ApplySidebar`, rail layout only; classic untouched).
3. Esc in the tree and in the Search view's field did nothing: the window's tunnelling `PreviewKeyDown` runs `overlay.close` before the tree's or field's own handler sees Esc. (The tree's Esc looked fine only because my old check used Backspace, which goes up from any focus.) `CloseOverlay` now has a rail-layout branch: keyboard on a rail button, in the tree, in the field or on a hit gives the keyboard to the pane, ends a running search, and logs "Esc gave the keyboard from the rail layout's sidebar back to the pane".
4. Live-check tool: cursor-block keys (arrows, Home, End, Page Up/Down, Insert, Delete) are now sent as extended keys. Without the flag they are the numeric keypad's, and with Num Lock on Windows drops Shift from Shift+Down (the window logged a bare Down), so the rail's Shift+Down walked instead of moving the button.
5. A "marketplace closed" log line (needed to check the second click on the Marketplace button).
6. The tree's reveal logs `reveal_ms` and `scroll_ms`: on a folder under %TEMP% with 4,283 rows (a huge ancestor) it is 74 to 213 ms and 2 ms; show/hide/switch of the views took no frame over 150 ms in a snapshot run.

## 7. Decisions made alone (what: because: undo)
- The tree follows the active folder only while the Explorer shows, and re-reveals when it shows again: rows of a hidden view cost work and the spec says "follows the active pane": call `FollowActiveFolder` without the view test in `MainWindow.Rail.cs`.
- Ctrl+Shift+E does NOT run while the Search view's text field has the keyboard: the existing rule (a text box keeps every key but the immutable tier's, `ChordStateMachine.Applies`) is kept; the way out is Esc, then Ctrl+Shift+E: to change it, add `view.showExplorer` to the core's immutable list (the creator's call, Article 7).
- Enter in the tree goes to the folder and the keyboard goes to the pane (the router's `go.toPath` does that, as for the address box): that is what a user expects from "goes there in the active pane": change `GoToPathAsync`.
- Sidebar-page keys (decision 1 above): because a hidden mouse-only exit breaks Article 7: revert a42c343's `SidebarPageWays`.
- The plugin-command rule needs source == plugin, the `<id>.` prefix and a follow: because a follow of "file" must not open core commands: revert a42c343's `MayRun` overload.
- The live check copies only Markdown Preview and Quick Notes into the run's tools folder: agent-chat now has `"sidebar": true` and would have made a sixth rail button and moved the expected order: change the copy line at the top of `livecheck.ps1`.
- The live check finds the rail buttons once and clicks their rectangles: a full UI Automation search over the window took about a minute while the tree had 4,000 rows and timed out once: restore `ClickElement (RailButtonElement ...)`.
- The live check's local marketplace index is in the initial config for every section: nothing in the run reads the network: remove `marketplace = @{ index = ... }` from `$editorJson`.
- Earlier in the item (kept): `ui.rail` stores an empty list for the default order; a tool whose id equals a built-in view id keeps to the pane; only Explorer and Search are the window's own views, all others are tool pages (Articles 10, 11); in-flight write counters stop config echoes undoing the user's own change; sidebar web pages get the keyboard through `GiveKeysToPage`, never `WebView2.Focus` (your rule 3).

## 8. Live-check lines, every section that matters (final run rail-5, all True)
tabs (12):
04:32:19.175 tabs: the left pane in tabs12\one, one tab, so no row / the row was never shown with one tab: True
04:32:23.469 tabs: Ctrl+T twice, three tabs, the row shows / three tabs after Ctrl+T twice, the last in front: True / the row is shown: True
04:32:25.441 tabs: Ctrl+Tab goes on to the first tab / came to the first: True
04:32:26.384 tabs: the palette, Toggle Tab Lock, Enter / the tab is locked, the status bar said so: True
04:32:29.204 tabs: Enter on the folder sub in the locked tab opens a fourth tab / a new tab shows sub, four tabs: True
04:32:31.172 tabs: Ctrl+W closes the new tab / three tabs again, the front tab is not in sub: True
04:32:32.414 tabs: Ctrl+W twice more leaves one tab, and the row hides / one tab left: True / the row is hidden again: True
04:32:34.773 tabs: Ctrl+W on the last tab is refused / the last tab stayed, the status bar said why: True / ui.tabs holds one tab in the left pane: True
04:32:37.248 tabs: the tab that is left is the locked one; the palette unlocks it / unlocked again: True
14 drag:
04:32:40.058 14: drag: ... Ctrl+K V opens it in the preview / the preview page is ready in the other pane: True
04:32:47.441 14: drag: the first row (a.txt) over the right pane's page, and let go / the row drag started in the pane: True / the drop reached the tool's page as paths-dropped: True / it carried one path: True
04:32:50.180 14: drag: the palette's Close Editor closes the page's tab, the keyboard is in the left pane again
14 ask:
04:32:54.092 14: ask: the marketplace, the agent's card, Install, Allow and install / the agent's card is in the marketplace: True / the detail has an Install button: True / the plugin is installed in this run's folder: True
04:33:09.272 14: ask: three files in the left pane, Ctrl+K Ctrl+A, 'rename these to vacation_*', Enter / the prompt box opened: True / the preview shows in the other pane: True / it has three rename rows: True / Enter renamed the files on disk: True / the preview closed: True
13 rail:
04:33:25.263 the configuration says ui.layout: rail / the window switched to the rail layout: True / the tree followed the left pane to rail13: True / the sidebar is open on the Explorer: True
04:33:32.952 the rail's five buttons, found by UI Automation / has its five buttons: True / 36 px square: True / 40 px apart, top to top: True / 12 px in from the window's edge: True
04:33:34.074 Ctrl+Shift+F: the Search view; 'zzreport13'; Down to the hit; Enter / shows the Search view: True / Enter on the hit took the left pane to alpha: True
04:33:39.492 Ctrl+Shift+E: the tree opens alpha, where the pane is, and has the keyboard; Down, Enter / shows the Explorer: True / the tree, shown again, followed the pane to alpha: True / Enter in the tree took the left pane to beta: True
04:33:42.219 Right opens beta, Right again goes to sub, Enter takes the pane there / the pane shows beta\sub: True
04:33:45.077 Ctrl+Shift+E puts the keyboard in the tree; Esc gives it back; Backspace goes up / Esc in the tree gave the keyboard back to the pane: True / Backspace went up to beta: True
04:33:48.116 the palette, Lock Folder Tree; the pane goes to gamma; Alt+Shift+L / the tree is locked: True / the locked tree did not follow to gamma: True / Alt+Shift+L showed gamma in the tree: True / the tree is unlocked: True
04:34:01.201 the mouse on the Quick Notes button / the page started (once): True / the sidebar shows quick-notes: True / the click reached the rail as ShowView on quick-notes: True
04:34:06.625 Ctrl+Shift+F from inside the page / the Search view shows again: True / Esc left the search field, and Ctrl+Shift+E shows the Explorer: True
04:34:09.027 the page again; Ctrl+Alt+B from inside it / the sidebar is closed: True / the pane had the keyboard: Backspace went up to rail13: True / the sidebar is open on the Explorer again: True
04:34:15.068 the mouse on the Marketplace button / opened: True / the second click closed it: True
04:34:19.684 the mouse on the active Explorer button / CloseSidebar: True / the sidebar is closed: True
04:34:21.874 Shift+Down on the Explorer button moves it; Shift+Up moves it back / ui.rail holds the new order: True / ui.rail is empty again: True / Ctrl+Alt+B opened it again: True
04:34:26.573 the divider: to 300 px, then under 150 px / ui.sidebarWidth near 300 (was 224): True / ui.sidebar is false, closed by the drag: True / ui.sidebarWidth still near 300: True / Ctrl+Alt+B opened it at the remembered width: True
04:34:33.931 ui.layout: classic again / back in the classic layout: True / no warning or error line in the window's log during this section: True
Runs before the last one, honestly: run 1 had 4 False (a check of mine that expected the tree to follow while the Search view showed; page keys; the rail move), run 2 had 3, run 3 had 1, run 4 had 1 (a wrong log pattern). Each was either a real defect fixed above or a fault in the check. One answer I could not explain: in run 2 "Alt+Shift+L showed gamma in the tree" was False (no log line at all for the key); it was True in runs 1, 3, 4, 5 and in two section-13-only runs.

## 9. Ready plan text for Phase 13 (I did not edit docs/PLAN.md)
Built and verified 2026-09-30 (sleep-mode run). `ui.layout: rail` (default stays `classic`) shows a 44 px activity rail: 36 px buttons, a 3 px accent pill on the view in use, buttons Explorer, Search, Marketplace, Terminal and one for each tool with `"sidebar": true`, in the order saved in `ui.rail` (Shift+Up and Shift+Down on a button move it; an empty list is the default order). A second press on the active view's button closes the sidebar. The sidebar holds its views: the Explorer (Pinned, Drives and a lazy folder tree over `list_directory`: one request for each folder opened, cancelled when it closes; it follows the active pane while it shows unless locked, `ui.sidebarAutoReveal`), the Search view (the field, its Whole volume box and the hits, over the same model as the command bar's box) and the pages of tools with a sidebar page (a WebView2 each; the page hidden last stays awake, the others are suspended). `view.showExplorer` Ctrl+Shift+E, `view.showSearch` Ctrl+Shift+F, `sidebar.locate` Alt+Shift+L, `sidebar.lock`, `view.toggleSidebar` still Ctrl+B (97 commands). The divider drags under 150 px to close and remembers the last width (`ui.sidebarWidth`; also `ui.sidebarView`); badges come from the plugin event `badge`. A tool page in the sidebar hands back the view keys and closing the sidebar gives the keyboard to the pane; Esc in the rail or sidebar does the same. A tool page may run the commands of a plugin it follows (a general rule, no name in the window). 839 UI tests pass (E2E on the real core, 48 s); the classic and right layouts differ from the snapshots taken before the phase by 19 and 11 pixels of 2.5 million (two runs of one build differ by 6). The live check's section "13: rail" passed with real keys and the mouse in a whole-script run (105 answers True, 0 False, with the tabs and the Agent's drag and ask in the same run). Known gaps: a text box (the Search view's field) keeps every key but the immutable tier's, so Esc first, then Ctrl+Shift+E; a tree with thousands of open rows is slow under UI Automation (see below). Guide: [ui.md](ui.md), "The activity rail and the sidebar".

## 10. Noticed, not done (out of scope or not mine)
- UI Automation cost: a whole-tree UIA walk (FindAll) while the tree had about 4,400 rows (a %TEMP% ancestor) took about a minute (one frame of 62 s, and one "Operation timed out"). With a UIA client attached I also saw 1.2 to 2.5 s stalls when the Marketplace opened and when the Explorer showed again (with no UIA client, in snapshot runs, no frame over 150 ms). A screen reader walking a huge tree could stall the window. Worth a look (fewer automation elements per row, or a virtualised automation tree); I did not change it.
- The scroll goal (`-Strict`) still exits 1: 302 frames in 5 s, 85 over 20 ms, worst 51.3 ms. Item 5's.
- The stray real journal `%LOCALAPPDATA%\CabinetOS\undo\journal.jsonl` from live-check run item3-1 (before CABINETOS_UNDO_DIR existed) is still there. I did not delete it (outside the worktree).
- `docs/PLAN.md` no longer says 93 commands (it says 97); nothing to fix there.
- The extension agent's doc `docs/extensions/agent.md` says the chat page needs the allow-list to be extended; that is now covered by the general rule (item 1 above), so its paragraph "For the chat page to work, that list needs ..." is out of date.
- The live check's Agent steps cannot run without `sdk/extensions/agent/plugin/plugin.wasm` (git-ignored): "WAITING" then.

## 11. What is left of item 4
Nothing I know of. Not verified with real keys: none of the listed keys; everything in the handout for the rail, tree, Search view, tool pages and divider was pressed or dragged in the final run. Left for the creator: the two choices in section 7 (Ctrl+Shift+E in the search field; where the keyboard goes after Enter in the tree), and whether `ui.layout` default should change from classic.
