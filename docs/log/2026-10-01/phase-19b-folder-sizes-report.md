# Phase 19b, folder sizes for every folder of a listing: the coder's hand-back

Item 6 of the creator's card "New features I want to build Files Type
Sprint", built in the sleep-mode run of 2026-10-01 ([PLAN.md](../../PLAN.md),
Phase 19b) by a coder on Sonnet in its own worktree. The plan: the setting
`panes.folderSizes` and the command `view.toggleFolderSizes`; while on,
every folder of a listing is measured through the existing `measure_paths`
path, folders counted already skipped; leaving the folder cancels. The
hand-back follows, as reported. The planning session's checks on the main
checkout: main pulled at 2c63758; the five core checks; the release core
rebuilt; the window's build with warnings as errors; the fast test run.
The live check with real keys runs at the end of the night.

---

Phase 19b (folder sizes for every folder of a listing) is done and pushed to main. The core checks, the fast tests and the whole end-to-end suite pass. I did not run the live check (ui/livecheck, real keys), as instructed.

COMMITS on main (b75d3f1..2c63758, pushed with `git push origin HEAD:main`, no force)
- d352be2 core: `panes.folderSizes` (PanesConfig.folder_sizes, default false), the command `view.toggleFolderSizes` (View, "Toggle Folder Sizes", UI, no key, no `when`), the regenerated schema, and config and registry tests.
- 0a4714a window: UiSettings.FolderSizes, FolderSizes.NotMeasured and RunningIds, PaneModel.Listed, CancelMeasures and the log lines, MainWindow.Commander.cs (the toggle, the measure on listing, turn-on and turn-off), plus fast tests.
- 2d7273a tests: ui/CabinetOS.Tests/FolderSizesEndToEndTests.cs (2 tests).
- 16b7075 live check: step 19b in ui/livecheck/livecheck.ps1, after the 11a measure lines.
- 8a7b434 and 2c63758 docs: config.md row, ui.md "Folder sizes" and the live check list, keybindings.md row and note, CHANGELOG line.
- 7a66fa1 core: command count 111 (see decision 9).

VERIFIED (run in my worktree, after the rebase onto the 19c core commits)
- Core: build, `cargo test --workspace` (803 passed, 0 failed, 6 ignored), clippy `-D warnings`, fmt and `cargo deny check` all clean. The schema regenerated with no drift after the rebase. The release core was built for the end-to-end run.
- Window: `dotnet.exe build CabinetOS.sln -warnaserror` gave 0 warnings and 0 errors. Fast tests: 1012 total, 990 passed, 22 skipped.
- Full run with `CABINETOS_UI_E2E=1` and the worktree release core: 1012 passed, 0 failed, 0 skipped (1 m 52 s). No frame-timing failure happened.
- `build/check-scripts.ps1` parses livecheck.ps1 in PowerShell 7.6 and Windows PowerShell 5.1.
- End-to-end evidence, test A (setting on in the file, no key):
  - asked pane=0 folders=3 why=listed, then counted folders=3 bytes=5500 cancelled=false.
  - The core logged "measure finished" counted=3 cancelled=false.
  - `path:` to C:\Windows: asked folders=75, then 22 ms later "folder sizes cancelled" measures=1 why="left the folder".
  - The core logged "measure cancel asked" and "measure finished" cancelled=true counted=10.
- End-to-end evidence, test B (setting off in the file):
  - Toggle: follow on=true, asked folders=3 why="setting on", counted 3 folders and 5500 bytes. The test read `"folderSizes": true` in the file.
  - Toggle again: follow on=false, and the file says false. `path:` to a folder with two folders asks nothing.
  - Hand edit of the file: follow on=true, asked folders=2, counted 2 folders and 300 bytes.
  - `path:` to C:\Windows, then the toggle: follow on=false, "folder sizes cancelled" measures=1 why="setting off". The core logged cancel asked and finished cancelled=true counted=11.

DECISIONS the plan did not cover (what — because — undo)
1. A watched re-list (`listing_refreshed`) also measures new folders (`Listed?.Invoke(this, null)` in PaneModel.ApplyRefresh) — "listed again" covers the watcher, and a new folder would show no size — remove that line.
2. "Counted already" uses the new `FolderSizes.NotMeasured`, which skips any folder with an entry. The old `NotCounting` would count Done folders again, and the plan says to skip them. A Ctrl+R therefore keeps the old sizes, and Space counts again — use NotCounting in `MeasureNewFoldersAsync`.
3. Turning the setting off cancels all running counts of both panes, those started by Space too, as the plan says. A `measure_started` reply that arrives after the setting went off is cancelled as well — the guard is in `MeasureNewFoldersAsync`.
4. Cancel on leaving the folder already existed (ForgetSizes sent cancel_measure). I added the log line and one fix: a count whose reply arrives after the pane left is cancelled at once, or the old folder's names would show in the new folder (guard in PaneModel.MeasureAsync) — undo by deleting the guard.
5. `Clear()` already returned the running IDs, but I added `RunningIds` as the plan asked, for the cancel on setting-off (it is unit-tested).
6. The auto count gets a fresh request ID, and the navigation's ID goes in the log field `listing_request` — CoreClient throws when one ID is in flight twice, and a command that lists both panes gives both navigations one ID.
7. New UI log line "folder sizes counted" (pane, folders, bytes, cancelled) in PaneModel.ApplyMeasure — the UI logged no measure end, and the end-to-end and live-check evidence needs bytes — removing it breaks those assertions.
8. A status-bar notice when the setting changes (on: counted when a listing opens; off: sizes stay) — same pattern as heavy logging — remove the two ShowNotice lines in `ApplyFolderSizes`.
9. The registry count went 109 to 110 in my edit and 19c made the same edit. The rebase merged them with no conflict and left 110, so I set 111 (SEED length, registry test, core/tests/config.rs). Commit d352be2 alone does not compile; it is fixed one commit later in 7a66fa1.
10. Live check step 19b has its own small helpers (FolderSizeLines, WaitFolderSizeLine), because ShellLines is defined further down the script. The fixture is `$files\foldersizes19b` (one\a.bin 4000 bytes, two\deeper\b.bin 6000 bytes). The step returns the left pane to the 11a folder and leaves the setting off.
11. End-to-end: the long walk that must be cancelled is C:\Windows, a read-only folder present on every Windows PC. The first folders come from `ui.lastPaths` (an empty folder), so the profile is not counted. `focus:<label>` is the marker step, and the hand edit is written as a temp file plus move.

NOT DONE
- I did not run ui/livecheck. The 19b step is only parse-checked. Its palette query "toggle folder sizes" is assumed to rank Toggle Folder Sizes first.
- 19c's window code was not on origin/main when I rebased, so conflicts in MainWindow.xaml.cs, UiSettings.cs, livecheck.ps1, ui.md and CHANGELOG.md fall to whoever lands second.

NOTICED, OUT OF SCOPE
- After a core restart, PaneModel.ForgetListing does not clear Sizes. Counts of the dead core never finish, so those rows say "counting" forever, and with the setting on they are skipped. Suggest `Sizes.Clear()` in ForgetListing (no cancel needed).
- One measure_paths request counts its folders in turn and sends totals only at `measure_finished`, so earlier folders show as counting until the whole request ends (also true for Calculate All). One request per folder, or chunks, would show totals sooner.
- A listing with thousands of folders sends one request with all paths (under the 16 MiB frame limit), and AllFolders walks the listing on the UI thread. A cap or chunks could be added if it is slow.
- My scratchpad helper ed.py was overwritten by another agent, because the scratchpad is shared. I moved my files to scratchpad\agent-19b.
