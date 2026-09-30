# Phase 19f, the column view (Miller columns): the coder's hand-back

Item 2 of the creator's card "New features I want to build Files Type
Sprint", built in the sleep-mode run of 2026-10-01 ([PLAN.md](../../PLAN.md),
Phase 19f; the design in [ADR 0016](../../decisions/0016-column-view.md))
by a coder on Opus in its own worktree. The hand-back follows, as
reported. The planning session's checks on the main checkout: main pulled
at ac5b3c4; the five core checks; the release core rebuilt; the window's
build with warnings as errors; the fast test run; the screenshot
`columns-three.png` in `_io/19f-column-view/` looked at. The live check
with real keys runs at the end of the night. One decision of the coder
departs from the ADR's wording and stands: the commands act on the
keyboard column's folder (the ADR said the deepest column's), because a
command's rows and its folder must be one folder; the crumbs, the title
and the saved tab still show the deepest.

---

Report: 19f, the column view (Miller columns) as a mode of a pane's tab

Commits on main (rebased onto 20c145f, pushed as `HEAD:main`, no force): 2a600a7 (core: TabEntry `mode` + TabMode, metric `columnViewWidth` 120–600, 220 → 180, both schemas regenerated), 06af125 (registry `view.toggleColumns`, Ctrl+Alt+C, filesView, count 112), 0a35e9d (window core: ColumnStack<T>, TabMode, `ui.tabs` mode, the FrontFolder for window_state, ColumnViewTests), 0a44908 (window: ColumnListing, PaneColumns, PaneModel.SwapListing/Navigated, ColumnView/ColumnList/ColumnRow, the FilePane hookup, MainWindow.ColumnView.cs with the steps), f9db0c4 (ColumnViewEndToEndTests), 3b60c19 (live check section 20), ac5b3c4 (docs: ui.md "The column view", config, keybindings, themes, ipc, CHANGELOG).
Rebase conflicts: MainWindow.xaml.cs, livecheck.ps1 and ui.md. I kept both sides each time. Section 20 now sits before the compact overlay and keys sections.

Verified:
- Core checks: build, test (804 passed, 0 failed), clippy -D warnings, fmt --check and deny all passed after the rebase. `cargo build --release -p cabinetos-core` passed.
- Window: `dotnet build -warnaserror` gave 0 warnings and 0 errors. Fast tests: 1024 passed, 30 skipped, 1054 total.
- Full run with `CABINETOS_UI_E2E=1` and the release core: 1053/1054 passed. `ContextMenuEndToEndTests.A_menu_asked_for_while_the_same_menu_closes_comes_on_screen` failed once. It is a flyout-timing test and passed when its class ran alone (4/4), so it is not caused by this change.
- The new end-to-end test passed. Evidence lines from the same steps rerun by hand:
  - "column view shown" three: depth=3, keyboard=3, listings=3, rows=`c>|b.txt|…`, shown=`a>|top.txt / b>|a.txt / c>|b.txt…`, keyboard_in_view=True, widths=220|220|220.
  - "shell state" three: pane0_listings=3.
  - After Left, Left: keyboard=1, depth=3, tab path still b.
  - Reopening a gave "column released" depth=3 b and depth=2 a. Then "two" had depth=2 and listings=2. Reopening b gave depth=3 and listings=3 (the count does not grow).
  - Leaving the view gave "column view left" b. "back" had mode=files, rows=`c>|b.txt…`, pane0_listings=1.
  - Every released listing_id has a core "listing closed" line (the test runs with CABINETOS_LOG=debug).
  - ui.tabs carries `"mode": "columns"`, and a second window starts in columns at b.
- Keys sent as key messages (the new `key:` step) worked end to end: Ctrl+Alt+C, Enter ×2, Down, Left ×2, Right, Backspace ×2 (parent prepended, depth 4), Ctrl+Alt+C back.
- Screenshots in `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\19f-column-view\`: `columns-three.png`, `columns-left.png`, `back.png`. They show three columns, names only, chevrons on folder rows, the keyboard's cursor row with the accent bar, and the crumbs at b.
- `build/check-scripts.ps1` parses in PowerShell 7.6 and 5.1.

Decisions (what — because — undo):
- Step `column-view:<label>` logs "column view shown" — `columns:` and "columns shown" already belong to the column widths — rename in MainWindow.ColumnView.cs and the test.
- Added `column-click:<depth>|<name>` — the plan's mouse path needed a step — drop it.
- Ctrl+Alt+C is the default key, and the program example key moved to Ctrl+Alt+E in registry.rs and keybindings.md — a user binding on a default key in the same context is a keymap conflict (a user who copied the old example gets that error) — seed the command with no keys and restore the example.
- The keyboard's column is the pane's own listing, and columns trade listings when the keyboard moves (SwapListing) — commands, find and quick search work unchanged, and no folder is listed twice — design, no simple undo.
- The pane's folder for actions (PaneModel.Path, the other pane's copy target, sidebar, terminal, window_state) is the keyboard column's. Crumbs, title and ui.tabs show the deepest. The two differ only after Left, and there the ADR's wording ("the pane's folder" follows the deepest) is not followed — a command's rows and folder must be one folder — make those callers use TabFolder().
- Enter and a click always drop and re-list, even when the next column shows that folder. Right steps into an existing child column without re-listing — this is the ADR's strict reading — change PaneColumns.OpenAsync.
- Column moves do not touch Back/Forward. Any other navigation (crumb, Back, sidebar, Ctrl+L, tab switch, lost folder) starts over with one column. A reload of the same folder keeps the columns — PaneColumns.OnNavigated.
- A tab that goes behind keeps its mode and deepest path, not its columns, and comes back with one column — ADR: "the first column is the tab's folder" — store the folders in PaneTab.
- New tabs inherit the front tab's mode, and Duplicate keeps it — FrontMode() and Duplicate.
- A locked tab still opens columns; the lock still redirects crumbs, Back and the rest — PaneColumns.
- The wheel scrolls a column's own rows when they overflow. Over a column whose rows fit, or with Shift, it scrolls sideways — otherwise long columns could only scroll by keys — ColumnList.OnWheel.
- A column the keyboard left keeps its cursor row lit with a quieter fill and no bar — ColumnRow "SelectedAway".
- A search shows its hits in the list and the columns return after it; a core restart starts the view over at the keyboard's folder; rows cannot be dragged out; the sideways scroll has no animation — FilePane and PaneColumns.
- Columns other than the keyboard's show icons guessed from the extension; describe_entries is asked only for the keyboard column — give ColumnListing its own details cache.

Not done or not verified:
- I did not run the live check (by instruction).
- The mouse wheel and real pointer clicks are untested. Clicks were tested through the same ClickAsync method a real click calls.
- No Commander Compact screenshot was taken; the 180 px value is covered by the metric tests only.
- PLAN.md and docs/log are not touched.
