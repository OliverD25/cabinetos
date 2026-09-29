## Phase 12, tabs per pane

Agent: ac9d2107a90527bf1

Report: Phase 12, tabs per pane

Context: item 2 of the sleep-mode run of 2026-09-29/30, the shell's part of Phase 12 (the tab row above each pane). It is finished and pushed to main (tip 80c204b). Items 3 and 4 are NOT finished: item 3 is started but nothing of it is committed or pushed (see "What is left"). I stopped here because the handout says to hand back after each item. Please send me a message to continue with item 3 and then item 4.

## What was built (files)

Commits on main, oldest first (77c1169 is the core agent's `ui.tabs` schema; the rest are mine):
- 725328f themes: metric `tabRow` (32 px default, 26 in Commander Compact). Files: core/crates/cabinetos-protocol/src/theme.rs, sdk/themes/theme.schema.json (regenerated), sdk/themes/commander-compact.json, docs/themes.md, ui/CabinetOS.Core/Themes/Metrics.cs (`Px("tabRow",14,80,32)`, `TabRow`), ui/CabinetOS.Tests/MetricsTests.cs (77 names, mapper equals schema).
- 4881f82 commands: seven tab seeds, category Tab, context `filesView`: `tab.new` ctrl+t, `tab.close` ctrl+w, `tab.next` ctrl+tab, `tab.previous` ctrl+shift+tab, `tab.toggleLock` (no key), `tab.openFolderInNewTab` ctrl+up, `tab.moveToOtherPane` ctrl+k ctrl+right and ctrl+k ctrl+left. Files: core/crates/cabinetos-commands/src/registry.rs (SEED 90 rows, new test `the_tab_commands_are_seeded_with_their_keys`), core/crates/cabinetos-core/tests/config.rs (90), docs/keybindings.md.
- 3fec822 ui core: the tab model. ui/CabinetOS.Core/Tabs/{PaneTab,TabStrip,TabsConfig,TabRules,WindowStateBuilder}.cs; Protocol/Requests.cs (`WindowStateRequest` and its DTOs), ProtocolJson.cs; tests TabTests.cs (schema-validated), ProtocolTests.cs (48 request types).
- 07b5e8b ui: the window. New ui/CabinetOS/Views/PaneTabs.xaml(.cs) (WinUI TabView row, 2 px accent line over the front tab of the active pane only, hidden with one tab) and ui/CabinetOS/MainWindow.Tabs.cs (all tab logic, saving, `window_state`, the snapshot steps `tab:new|close|next|previous|lock|folder|move|select <i>` and `tabs:<label>`); edits to MainWindow.xaml, MainWindow.xaml.cs, MainWindow.Tools.cs, MainWindow.Metrics.cs, MainWindow.Search.cs, MainWindow.Commander.cs, MainWindow.Market.cs, ViewModels/PaneModel.cs (`IsLocked`, `LockedNavigation`, `CaptureInto`, `RestoreAsync`, `MarkedPaths`); ui/CabinetOS.Tests/TabsEndToEndTests.cs.
- 837361a docs and live check: docs/ui.md ("Tabs" section, the `ui.tabs` row in "What the window remembers", `tabRow` in "Where each metric goes", the snapshot steps, "Not in this version"), ui/livecheck/livecheck.ps1 section "12: tabs" (after the Commander Compact section, VK `W` added), ui/livecheck/scroll-bench.ps1 (new `-Before "<steps>"`), tab items take the theme's `tabRadius` (Commander Compact draws no corner over 3 px), log lines "tab row shown" and "tab row hidden".
- cf08875 docs: README test counts (ui 676, core 672), docs/tool-extensions.md (the tool's file is also a tab).
- 80c204b ui: the end-to-end test also keeps a lock and the right pane's tabs (docs/ui.md line).

Behaviour, as built: a pane holds folder tabs (folder, history, order, cursor, marks, lock) and tool tabs. Tab stays the pane switch; Alt+Left/Right stay Back/Forward. In a locked tab Enter on a folder, Backspace, a crumb or a pinned folder open a new tab beside it; Back/Forward refuse with a notice. The last folder tab cannot be closed (notice). `ui.tabs` is written at most once a second and at close (`ui.lastPaths` still written); read at start, a gone folder falls back to its parents, then to the first-start folder. `window_state` goes to the core on every change of tabs, active pane, cursor or marks, joined to one message per 50 ms (marks capped at 1,000 paths); it stops after one `unknown_request`.

## Results (verbatim)

Build: `dotnet build ui/CabinetOS.sln -c Debug -warnaserror` and `-c Release`: 0 Warning(s), 0 Error(s).

UI tests at the pushed state (cf08875 tree, then the e2e change 80c204b), with `CABINETOS_UI_E2E=1` and a fresh core: `total: 676 / failed: 0 / succeeded: 676 / skipped: 0` (baseline 650, so 26 new). `TabsEndToEndTests` alone: `total: 1 failed: 0 succeeded: 1`.
Core: `cargo test --workspace` sum of all "test result" lines: 665 passed, 0 failed (before main's newest core commits; the README says 672 = main's 671 + my one new test). `cargo fmt --all -- --check` exit 0; `cargo clippy --workspace --all-targets -- -D warnings` exit 0; `cargo deny check`: `advisories ok, bans ok, licenses ok, sources ok`.

Live check with real keys (run-tabs-3, `_io\live-check\run-tabs-3.txt`, started 01:21:36, ended 01:25:07). Section "12: tabs", every line True:
- `tabs: the row was never shown with one tab: True`
- `tabs: three tabs after Ctrl+T twice, the last in front: True` / `tabs: the row is shown: True`
- `tabs: Ctrl+Tab from the last tab came to the first: True`
- `tabs: the tab is locked, the status bar said so: True`
- `tabs: a new tab shows sub, four tabs: True` (Enter on a folder in the locked tab)
- `tabs: three tabs again, the front tab is not in sub: True` (Ctrl+W)
- `tabs: one tab left: True` / `tabs: the row is hidden again: True`
- `tabs: the last tab stayed, the status bar said why: True`
- `tabs: ui.tabs holds one tab in the left pane: True`; the section then unlocked the tab through the palette (`tabs: unlocked again: True`).
DONE.md: `Checks that answered False: none`. Exit code 1 only from -Strict: `scroll goal ... met: no; 299 frames in 5 s, 89 over 20 ms (29.8 %), 2 over 33 ms, worst 52.1 ms; CPU during the hold: machine 15.0 %`. The earlier run before my work (`run-2026-09-30-item1-b.txt`) says the same (88 over 20 ms, 29.0 %), so tabs did not change it; that is item 5's.
Two earlier attempts (run-tabs-1, run-tabs-2) stopped with a foreground STOP: a Rust test window `cabinetos_fs-...` and a CabinetOS.exe from the `planning` worktree were in front. I waited and reran, as the handout says; the third run passed. Notepad was closed after each run.

Scroll cost of the TabView (release builds, display awake, other agents building, so noisy; `scroll-bench.ps1`, "UI thread per second of scrolling, ms of work"; baseline window built from the commit before the window change in %TEMP%\cabinetos-baseline):
| Window | Runs | Lowest | Middle |
|---|---|---|---|
| Before tabs | 11 | 396 | 406 |
| Tabs, one tab (row hidden) | 14 | 402 | 418 |
| Tabs, three tabs (row shown, `-Before "tab:new;tab:new"`) | 11 | 388 | 402 |
At most +3 %, under the 5 % limit, so TabView stays (no ItemsRepeater).

Snapshot-aid checks (screens in %TEMP%\cabinetos-ui-test\{tabs1,tabs2,tools1,compact1,many1,accent1}\shots): five tabs left and three right open independently; F5 (`file.copyToOtherPane`) copied from the front tab of the left pane into the front tab of the right one; the accent line shows only in the active pane's row and moves with Tab (pixel scan: left 121 px wide when left is active, right 122 px when right is); Markdown Preview opens as a tool tab beside the folder tab, Ctrl+Tab goes back, `editor.close` closes it, Ctrl+U says "Close the tool tabs first"; Commander Compact draws the row 26 px high and `radius_over_3` is "none".

## Decisions (what — because — undo)

1. Kept WinUI TabView for the row — measured under 5 % (table above) — replace the TabView in PaneTabs.xaml by an ItemsRepeater strip; `TabStrip` stays.
2. "Park and restore": the pane's model stays the live state of the front folder tab; a tab behind keeps folder, history, order, cursor and marks in a `PaneTab` and lists its folder again when it comes to the front — a live listing per tab would mean a shared-memory section and a watcher per hidden tab, and every pane feature is written for one listing — one PaneModel per tab (large change to `_panes[]`). Consequence: a background tab does not refresh itself until it is shown.
3. `tab.new` copies the front tab's folder and order, with a fresh history and no marks — that is Total Commander's behaviour — set what `NewTabAsync` copies.
4. Locked tab: Enter on a folder, Backspace, a crumb and a pinned folder open a new tab; Back and Forward refuse — the lock must keep the folder, and history in a locked tab would break that — make `TabRules.OpensInNewTab` return false.
5. A tool tab has no lock and cannot move to the other pane; Ctrl+U (swap panes) refuses while a tool tab is open — the tool's process belongs to its pane's page — move the `ToolHost` with the tab.
6. One tool process per pane; tool tabs of one tool share it; showing a tab of another tool ends the first tool's process and starts the other (the first tool's tab stays and starts again when shown) — `ToolHost` is one per pane, so at most two WebView2 processes (Article 1) — one host per tool tab.
7. Tool tabs are not saved in `ui.tabs` (they are in `window_state`, field `tool`) — the core's schema for `ui.tabs` items is `{path, locked}` and a tool needs a file that may be gone — add `tool` to the schema items and to `TabsConfig`.
8. `marked` in `window_state` is capped at 1,000 paths per pane — a large selection would make every 50 ms message huge — raise `WindowStateBuilder.MaxMarked`.
9. Pane tab items use the theme's `tabFontSize` and `tabRadius`; `tabRow` is the only new metric (as the handout says) — Commander Compact must draw no corner over 3 px — add `paneTab*` metrics.
10. `tab.toggleLock` has no default key, and `tab.select` (a click on the row) is a window-only command (`RegisterLocal`, not in the palette) — the handout names no key for the lock, and a click is not a palette command — seed a key; register `tab.select`.
11. The second key of the chord names the side for `tab.moveToOtherPane` (`KeyArguments`: Ctrl+K Ctrl+Left sends to the left pane) — the handout binds both chords to one command — split into two commands.
12. `tab.openFolderInNewTab` on a file, or in an empty pane, opens the current folder — the key should always do something visible — refuse with a notice.
13. The row is hidden with one tab per pane; in single-pane mode only pane 0 has a row — the handout says "collapsed with one tab" — none needed.
14. New log lines "tab row shown" and "tab row hidden" — the live check reads the log, not the window's tree — remove the two Diag lines in `OnTabsChanged`.
15. The live-check section unlocks the tab if the locked one is the one left — a locked left tab would open new tabs in the later sections — none needed.
16. The scroll baseline was built from a `git archive` of the commit before the window change in %TEMP%\cabinetos-baseline, not from a second worktree — one worktree only — delete %TEMP%\cabinetos-baseline, %TEMP%\cabinetos-baseline.zip and %TEMP%\bench-out.
17. Commits end with `Co-Authored-By: Claude Sonnet 5.5`, the attribution line the harness gave me, not the "Claude Opus 5" line of the handout — the harness's line is the current rule — amend nothing; just so you know.
18. README core count 672 (main's 671 plus my one test) although my own sum was 665 on an older tree — my tree lacked main's newest core tests — set to your own count.

## Ready plan text (for docs/PLAN.md; I did not edit it)

**Phase 12, tabs per pane, built in the sleep-mode run of 2026-09-29/30.** Each pane has a row of tabs above its list: a WinUI `TabView`, `tabRow` high (32 px, 26 px in Commander Compact), hidden while the pane has one tab. A tab holds a folder with its own history, order, cursor, marks and lock, or a Tool Extension (Markdown Preview opens as a tab beside the folder's). The pane's model stays the live state of the tab in front; a tab behind parks its state and lists its folder again when it comes back. The front tab of the active pane has a 2 px accent line, and nothing is dimmed. `tab.new` (Ctrl+T), `tab.close` (Ctrl+W), `tab.next` and `tab.previous` (Ctrl+Tab, Ctrl+Shift+Tab), `tab.toggleLock` (palette), `tab.openFolderInNewTab` (Ctrl+Up) and `tab.moveToOtherPane` (Ctrl+K Ctrl+Right, Ctrl+K Ctrl+Left) are in the registry under "Tab". In a locked tab, going into a folder or up opens a new tab and Back and Forward refuse; the last folder tab of a pane cannot be closed. The window saves `ui.tabs` (at most once a second, and at close; `ui.lastPaths` is still written) and sends `window_state` on every change of tabs, active pane, cursor or marks, joined to 50 ms. Measured on the 100,000-entry folder (`scroll-bench.ps1`, release builds, display awake, 11, 14 and 11 runs): UI-thread work per second of scrolling, middle value 406 ms before the tabs, 418 ms with the row hidden (+3 %) and 402 ms with three tabs shown, so `TabView` stays. 26 new UI tests (676 in all): the tab model, the `ui.tabs` and `window_state` shapes against the schemas, and an end-to-end test that opens three tabs (the last locked) in the left pane and two in the right one, restarts the window and finds them all. The live check's section "12: tabs" passed with real keys (Ctrl+T twice, Ctrl+Tab, the lock through the palette, Enter on a folder in the locked tab opened a fourth tab, Ctrl+W hid the row again, the last tab refused to close). With the snapshot aid: five tabs left and three right open independently and F5 copies from the front tab of one pane into the front tab of the other. Known gaps: a row shown in only one pane pushes that pane's list down by 32 px; the editor's own header repeats a tool tab's name; tool tabs are not saved; no drag to reorder tabs and no "+" button (the keyboard is complete, Article 7).

## What is left

- Item 3 (the window's parts of Phase 14) and item 4 (Phase 13, rail and sidebar) are not finished. Item 3 is started and lives uncommitted in my worktree (E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.claude\worktrees\agent-ac9d2107a90527bf1, branch worktree-agent-ac9d2107a90527bf1): the protocol types (`PluginEventEvent` with the payload as TEXT, as event.schema.json says; `PreviewOpenedReply`, `JobsStartedReply`, `PreviewApplied/CancelledEvent`, `OpenPreviewRequest`, `PreviewApplyRequest`, `PreviewCancelRequest`, `CommandInput`, `hosts` on capabilities), the preview section reader in `ListingView` (flag, change, target), `PreviewSession`/`PreviewKeys`, the router's input prompt (`AskInput`, `CommandOutcomeKind.Cancelled`), tool-page `subscribe`/`plugin-event`/`paths-dropped` message builders, `agent.notice` and badge helpers, hosts in the review dialog, and 22 new tests (the Debug tree builds with 0 warnings and all 698 tests pass with a fresh core). Still to do for item 3: the window wiring (PromptBox for input, the preview pane and its keys before the keymap, `ToolHost` subscriptions, the drag of rows onto a tool page, the status-bar notice), docs, and the live-check section "14: ask". Nothing of it is pushed.
- Open question for item 3 that the core text does not answer: how a window hears of a preview a plugin proposed. docs/ipc.md says only "a window shows it when it hears of it", and the events list has no `preview_opened`. My plan, unless you tell me otherwise: the window opens a preview (`open_preview`) when a plugin command's `command_result` has `{ "preview": id }`, or when any plugin emits `preview.proposed { preview }`. Please check this against the core agent's plugin side.
- Out of scope, worth doing: the scroll goal is missed by the same numbers as before the tabs (item 5). The stale-core trap: `EndToEndTests.The_marketplace_reads_the_local_index_installs_a_reviewed_plugin...` times out ("waited 30 s for hello to become active") when `core/target/release/cabinetos-core.exe` is older than the tree's sdk/plugins; rebuild the core first. The EditorPane's own header row repeats the tool tab's name and close button; hiding that chip when a tab exists is a small follow-up.
