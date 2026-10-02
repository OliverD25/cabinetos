# The third round of flaky end-to-end tests (2026-10-02, evening)

Context: the third round of the work in
[../2026-10-01/flakes-report.md](../2026-10-01/flakes-report.md) and
[e2e-flakes-2-report.md](e2e-flakes-2-report.md). Seven window tests failed by
turns on the creator's Omen laptop in full runs, and the second report left four
of them with unrun guesses. Written by the Sonnet coder agent in the worktree
branch `worktree-agent-a21066120fda85fae` (it starts at ce10d98; main has moved
on by three commits since; a trial merge with `git merge-tree` before the docs commit
was clean). Every proof against load ran on the laptop
(`remote-tests.ps1`, `repeat-tests.ps1 -Suite 2` through `remote-script.ps1`).
The creator came back to the PC in the last hour of the work and said "hold the PC", so
the last window runs are on the laptop only (see "Tests and runs").

## Summary

| Test | Cause | Change | Laptop, 5 runs: before, after |
|---|---|---|---|
| `ColumnViewEndToEndTests.Three_levels_open_close_...` | `column-view:` logged what is on screen the moment `column-open:` returned; XAML builds a column's rows and applies the strip's scroll on its next frames, which a busy machine draws a few hundred milliseconds late. A fixed 1.5 s before the test read `ui.tabs` | `column-view:` waits two frames and then until the view is laid out (`ColumnView.IsLaidOut`); `until:tabs-saved` is new and replaces the 1.5 s | `-Suite 2`: before 3 of 5 passed, after **5 of 5** in every batch |
| `ContextMenuEndToEndTests.The_menu_is_edited_inside_the_menu_and_saved_through_the_core` | Several fixed waits for events a busy machine raises late (see "Test 2"), and one fault in the product: a late GotFocus of a row that a rebuild had replaced moved the focus back, and a Delete took out the wrong row | the steps wait for the menu, the edit mode, the prompt's rows and their layout, the saved list and the keyboard's row (`until:menu-edit`, `until:menu-edit-idle`, `until:menu-edit-closed`, `until:file-menu-rows:<n>`); the product's GotFocus handler ignores a replaced row | `-Suite 2`: before **0 of 5**; after, 4 of 5 failed in batch 1 and 1 of 5 in batch 4 (the focus fault), **0 of 15** in batches 5 to 7 |
| `ContextMenuEndToEndTests.The_menu_follows_the_file_runs_a_program_and_shows_windows_own_menu` | Five causes in turn (see "Test 3"): `shellmenu:` waited a fixed 1.5 s for the core, the test wrote the configuration file three steps before it waited for it, `until:config` ended at the window's own configuration reads, the two recorded programs met at the log file, and the core gave up on Windows' menu after its 3 s | `until:windows-menu`, `until:shell-menu`, `until:command:<id>`, `shell:edit-now`; a recorder that retries a busy file; the first ask loads the shell's handlers; the step asks again when the core answers "took longer than 3 s" | `-Suite 2`: before **0 of 5**; after, 3, 1, 1 and 1 of 5 failed in batches 1, 2, 3 and 5 (the last was the core's 3 s), **0 of 10** in batches 6 and 7 |
| `ContextMenuEndToEndTests.The_menu_opens_with_its_corner_at_the_point_...` | Fixed waits (900 ms per menu, 800 ms for the window's size) for a log line written some low-priority turns after the menu opens, and a **fault in the product**: a menu's size was kept from a showing whose list popup was not drawn yet | `until:menu-placed`, `until:menu-closed`, `size:` waits until the content changed; `ContextMenuFlyout` reports a menu placed, and keeps its size, only once every button is inside a drawn popup | `-Suite 3`: before **0 of 5**, after **5 of 5** twice (beside two suites it failed 2 of 5 in batch 4, the product fault, and 0 of 15 after) |
| `KeptListingEndToEndTests.Switching_between_two_tabs_...` | The kept listing lived 3 s and a step between two switches took 3.5 s; the wait for its release was a fixed 4.5 s; "listing shown" is not logged when a newer listing is bound before the first frame | the test's lifetime is 10 s; `until:kept-released`, `until:listing-drawn` | `-Suite 3`: before 2 of 5 passed, after **5 of 5** twice (beside two suites it failed 1 of 5 in batch 3 and 0 of 20 after) |
| `RailEndToEndTests.The_rail_switches_views_keeps_the_last_web_page_warm_...` | The divider sets the column's width and `ActualWidth` follows at the next frame; fixed waits for WebView2's start, sleep and wake | every rail step ends with two frames; `until:sidebar-page:`, `until:page-suspended:`, `until:page-awake:`, `until:sidebar-view:`, `until:rail-saved`, `until:search` | `-Suite 2`: before 4 of 5 passed, after **5 of 5** in every batch |
| `RailEndToEndTests.Every_way_of_picking_a_folder_in_the_sidebar_runs_go_toPath_once` | `click:` logged "no shown button" and went on when the row was not drawn; each pick was followed by a fixed 300 ms; and the drive's "listing shown" line was dropped when the next pick came before its first frame (a frame took 4.6 s) | `click:` waits for its button; `until:pane-at:<folder>`, `until:listing-drawn` | `-Suite 3`: before **0 of 5**, after 4 of 5, then **5 of 5** (beside two suites 0 of 40 failed) |
| `ContextMenuEndToEndTests.The_first_menu_of_a_file_a_folder_and_the_space_finds_its_flyout_built_at_start` (not on the list; seen in the third batch) | The window builds the four menu shapes in idle slots after start, and the test right-clicked after a fixed 2 s | `until:menus-prepared` | `-Suite 2`: 1 of 5 failed in the batch that found it (3), 0 of 20 after |

The numbers of one batch are one run of `repeat-tests.ps1 -Times 5` with five
runs of the 15 tests of the four classes, each beside two full test runs
(`-Suite 2`), or of the three tests marked `-Suite 3` beside three. The batches
are in "Tests and runs".

## The method

As in the two earlier reports: a test waits a fixed time for something the
window announces through an event or a log line, and a busy machine raises the
event late. The cure is a snapshot step that waits for the event (new steps, in
[../../ui.md](../../ui.md), "Snapshot aid"), never a longer fixed wait.

1. **Make the failure readable first.** The four classes' failure messages
   print the window's log lines with their UTC times (`WindowLog`, in the test
   project's `Support`; frame lines left out), and a wait that runs out of time
   prints the last lines too (0b7ccd3, c7a4b41). `repeat-tests.ps1` prints the
   text of every test that failed in a run, not the first 60 lines (7b52da9).
   With that, each failure was read from the log of one failed run on the
   laptop, not guessed.
2. **Fix what the log shows, run the batch, read the next failure.** A batch is
   `repeat-tests.ps1 -Times 5 -Suite 2` on the 15 tests of the four classes.
   Seven batches, each one after a build of the commit named in "Tests and
   runs". Most failures hid behind the one before: the edit test failed five
   ways before it passed.
3. **Time limits are not waits.** A test that passes does not wait for its
   limit, so the limits were raised where a window needed more than the limit
   to start (6b8d18c).

## Test 1: the column view

Seen: "the keyboard's column is in view" read False, 2 of 5 runs. The log shows
`column-open:` returning and the state being logged in the same turn; the
column's rows and the strip's scroll come on XAML's next frames.
`column-view:` now waits two frames and then until `ColumnView.IsLaidOut` (every
column has a width, a column with rows has drawn one, the keyboard's column lies
whole in the strip), at most 20 s (9a2c05a). Window code is the snapshot aid's
only.

## Test 2: the menu's edit mode

Seen, in the order the batches showed them:

- "editing" read no edit mode: `menu-click:` ran 600 ms after the menu was asked
  for, the edit mode 500 ms after the click. WinUI opens and closes a flyout
  seconds late on a busy machine, and the edit mode comes through the flyout's
  `Closed`. `until:menu` and `until:menu-edit` wait for them.
- "dragged" read the old order: the rows are rebuilt by each change and measured
  at the next frame, and `DragForSnapshot` read a height of 0. It lays the rows
  out first.
- `type:` then `accept` took the highlighted row of the old text: XAML raises
  the box's text change on its next frame. `type:` waits until the prompt's rows
  follow the text (`PromptBox.ListFollows`); `until:menu-edit-idle` waits until
  no prompt is open for the edit mode and no save is out.
- The saved list was read before the core had written it: `until:config` ended at
  any read, and the window reads the configuration for its own writes too (the
  tabs it saves a second after the last folder it opened). `until:file-menu-rows:<n>`
  says what the test waits for (0fa3be0).
- A limit: "waited 60 s for the edit window's state saved" while the window was
  still at its first steps, 4 of 5 runs beside two suites (6b8d18c).
- **A fault in the product** (17325c8), found in the last batch before the fix:
  state "deleted" read "Open|Open in other pane|New folder" where the moved row
  "New folder" should have gone. A Delete took out "Copy to other pane". XAML
  raises a row's GotFocus a frame late on a busy machine. The Alt+Up came 19 ms
  after the add, the rows were rebuilt, and the GotFocus of the old row 3
  arrived afterwards and set the model's focus back to 3 (its handler used the
  index the row had). A row's GotFocus now counts only while that row is still
  the one in the list. A user who presses Alt+Up and Delete within a frame on a
  slow computer had the same fault. `menu-edit-key:` waits until the keyboard is
  on the row the key left the focus on and two frames more, not 300 ms.

## Test 3: the program run and Windows' own menu

Seen, five causes one after the other (the test has two halves, the file's
program and Windows' menu, and failed 5 of 5 before):

1. `shellmenu:` waited a fixed 1.5 s for the core, and the core's first
   `shell_menu` answer took 1.6 s on the laptop (the shell's handlers load on
   the first call). `until:windows-menu` waits until no question for Windows'
   menu is out and the menu shown has logged where it is (fbb9c8a, 81af875).
2. The test wrote the configuration file when it saw the state "background",
   three steps before it waited for it, and `until:config`, which waits for a
   read after the step began, waited its whole 20 s for nothing. The script now
   logs the state "edit-now" in the turn that the wait starts, and the test
   writes the file then.
3. A read of the configuration that was not the test's file (the window's own
   tab write) ended the wait, and the menu was built from the old configuration,
   with no "Record Paths". `until:shell-menu` and `until:command:program.record`
   wait for what the test wrote (0fa3be0).
4. Two programs started a moment apart finish in the system's order, and the
   second `wscript` that found the log file open lost its line. The stand-in
   program retries every 50 ms, up to 10 s, the test compares the lines as a
   set, and a missing line is reported with what the file holds (a24c2e4).
5. The core gives up on Windows' menu after 3 s (`BUILD_TIMEOUT`,
   "Windows took longer than 3 s to build the menu"), 1 run in 5 beside two
   suites, once at the second ask too, when the first had loaded the handlers.
   The limit is the product's and stays: a user sees the notice and asks again.
   The test asks once first to load the handlers (aa76c15), and the `shellmenu`
   steps now ask again when the answer is an error, three times at most, and log
   "windows menu asked for again" (894d03b).

## Test 4: the menu's place

Seen: 5 of 5 runs failed beside three suites, six "context menu placed" lines
wanted and five found: the line is written some low-priority dispatcher turns
after WinUI opens the menu, and a menu closed before them logs none. The test
closed each menu 900 ms after asking for it, and `size:` waited a fixed 800 ms
for the window's new size. `until:menu-placed` and `until:menu-closed` come
after each menu, and `size:` waits (up to 5 s after its 800 ms) until the
content has changed (d824017).

**A fault in the product** (998e4e2), found in a later batch (2 of 5 runs failed
with `Assert.InRange` 398 to 402, actual 192 and 668): the window reported "context
menu placed" when every button had a width, but a button keeps its width from
its last showing, so a showing whose list popup WinUI had not opened yet was
reported with the icon row alone (60 high, not 268) and its size kept as the
shape's measured size. The next menu of that shape then did not flip above the
point near the window's bottom edge: it opened at top 400 and ended at 668 in a
window 472 high. The report is complete now when every button lies inside the
joined popups, and it retries on the next dispatcher turns as before and then every
50 ms up to 5 s (a task's timer, since a `DispatcherQueueTimer` that nothing refers
to may be collected before it ticks, as the rail's tree look found). A counter of
the openings stops the retries of a gone menu from reporting the next showing a second
time.

## Test 5: the kept listing

Seen, beside three suites: 3 of 5 runs failed in two ways, both from the 3 s
that a kept listing lives in the test. A step between two tab switches took
3.5 s, the listing was let go before its tab came back, and the tab listed its
folder again; and the wait for the release was a fixed 4.5 s from the last
switch, 1.5 s more than the lifetime, for a timer on the thread pool whose
continuation runs on the UI thread. The test's lifetime is 10 s now (longer than
the longest step seen), and `until:kept-released` waits until no pane keeps a
listing for a tab behind (`PaneModel.HasKeptListing`) (d04057a). A second kind
was found in batch 3: "listing shown" is logged at the first frame after a
listing is bound, and a switch that comes before that frame replaces the pending
line (a frame every few hundred milliseconds beside the load, a switch every
300 ms), so 0 of 4 lines were written. `until:listing-drawn` follows each switch
(ca01259).

## Test 6: the rail's view switch

Seen: "the sidebar was 224 px wide where the test expected 320", 1 of 5 runs.
The divider sets the column's width and `ActualWidth` follows at XAML's next
frame. Every rail step ends with two frames now, and `rail-state:` waits two
first. The test's other fixed waits were for WebView2: a sidebar page starts,
goes to sleep (a 300 ms timer and `TrySuspend`) and wakes when WebView2 says so;
they became `until:sidebar-page:<tool>`, `until:page-suspended:<tool>`,
`until:page-awake:<tool>` and `until:sidebar-view:<id>`, and `until:rail-saved`
waits until the order, the width and the view have been written to the core,
which the test reads from the file after the window closed (43001b0).

## Test 7: the sidebar's picks

Seen: 5 of 5 failed beside three suites; once on the main PC under a full
suite with "snapshot click: no shown button has that name". `click:` logged that
line and went on when the sidebar's row was not drawn yet; it waits for the
button now, 20 s at most. Each pick is followed by `until:pane-at:<folder>`
instead of 300 ms, so the "rail state" line comes after the pick's `go.toPath`,
and `until:tree` comes before the keys (43001b0). A last failure, 1 of 5 beside three suites at
894d03b: "the pane never showed C:\". The same dropped line as in the kept-listing
test: the drive's listing was bound, and the pinned folder was clicked 100 ms
later, before the first frame (4.6 s late in that run). Each pick waits for
`until:listing-drawn` (9b3baa5). The failure message now lists the picks and
listings of the whole run, not only the last lines.

## Tests and runs

- Local build: `dotnet build CabinetOS.sln -warnaserror`, 0 warnings, 0 errors.
- Fast tests (no window), `dotnet test CabinetOS.sln --no-build`: 1339 total,
  1263 passed, 76 skipped, 0 failed (6 s).
- `build\check-scripts.ps1`: `repeat-tests.ps1` changed; it passed in both
  PowerShells (Windows PowerShell 5.1 and PowerShell 7).
- The full suite with the end-to-end tests (`CABINETOS_UI_E2E=1`,
  `CABINETOS_CORE_EXE` the main checkout's release core, protocol 19):
  - **this PC**, after `wait-for-pc.ps1` exited 0, at 894d03b: **1339 of 1339**,
    3 min 58 s. The creator said "hold the PC" after that; the two last commits
    (9b3baa5 and the docs) changed one test script and documents, and were not
    run on this PC;
  - **the laptop** (`remote-tests.ps1 -EndToEnd -Branch ...`), at 9b3baa5: **1338 of 1339** in two runs (4 min 25 s each), with one failure each time, both outside the seven tests (see "What is left"): the start test's hello took 69.7 ms against its 60 ms limit, and the Quick Open test found an ERROR line, a COMException from the command palette's list.
- The 15 tests with nothing beside them on the laptop (the first run on each
  build): 15 of 15 at 43001b0, 6b8d18c, 0fa3be0, 81af875 and 17325c8; 14 of 15 at ca01259 (the
  Windows menu test, fixed by 81af875) and at 894d03b (see "What is left", the menu
  reopen test); 15 of 15 at 9b3baa5.
- The batches of `repeat-tests.ps1 -Times 5 -Suite 2` on the 15 tests (a run is
  the 15 tests; "passed" is a run with 0 failures):

| Batch | Build | Runs passed | Failed tests (runs) |
|---|---|---|---|
| before | ce10d98 and the test messages | **0 of 5** | edit 5, follows 5, column 2, rail views 1 |
| 1 | 43001b0 | 1 of 5 | edit 4, follows 3 |
| 2 | 6b8d18c | 4 of 5 | follows 1 |
| 3 | 0fa3be0 | 2 of 5 | first menu 1, follows 1, kept listing 1 |
| 4 | 81af875 | 2 of 5 | menu place 2, edit 1 |
| 5 | 17325c8 | 4 of 5 | follows 1 |
| 6 | 894d03b | **5 of 5** | none |
| 7 | 9b3baa5 | **5 of 5** | none |

- The batches of the three tests that did not fail beside two suites
  (`-Suite 3`, the kept listing, the sidebar's picks, the menu's place):

| Batch | Build | Runs passed | Failed tests (runs) |
|---|---|---|---|
| before | ce10d98 and the test messages | **0 of 5** | sidebar picks 5, menu place 5, kept listing 3 |
| after 1 | 894d03b | 4 of 5 | sidebar picks 1 |
| after 2 | 9b3baa5 | **5 of 5** | none |

- Every changed `.cs`, `.xaml` and `.ps1` file is CRLF and the `.md` files LF
  (checked with `file`).

## The laptop

- The batches ran one at a time; nothing else of mine ran there. Signed-in
  desktop: yes. Before each build I ended the `CabinetOS.exe` processes that my
  earlier batches had left (4 at one time), with `taskkill /F /IM CabinetOS.exe`
  over SSH, as the project's `CLAUDE.md` allows; so the builds went into the
  main clone `C:\Dev\cabinetos\cabinetos` and no new clone was needed. No
  setting, service or task was changed there. The runs' full outputs are in
  `_io\script-runs` and `_io\test-runs` (`script-2026-10-02-1718` to `-2052`, `tests-2026-10-02-1957` to `-2111`).

## Decided without you

Each line: what, because, undo.

1. **The new `until:` conditions, the waits in the `click:`, `type:`,
   `size:`, `column-view:`, `menu-edit-key:`, `shellmenu:` and `rail` steps, and
   the flags they read** (`_menuPlaced`-like fields, `PaneModel.HasKeptListing`,
   `FilePane.TimingPending`, `PromptBox.ListFollows`, `ColumnView.IsLaidOut`,
   `ContextMenuEditor.IsIdle` and `FocusSettled`, `_menusPrepared`,
   `_windowsMenuAsking`, `_windowsMenuFailures`). Because the rounds before decided the same way:
   the cure for a fixed wait is an event. They are the snapshot aid's, they run
   only in the test's window and change nothing for the user. Undo: revert the
   commit of the test (9a2c05a, 97dbf32, fbb9c8a, d824017, d04057a, 43001b0,
   0fa3be0, ca01259, 81af875, 894d03b, 9b3baa5); the test then flakes again.
2. **`click:` waits up to 20 s for its button and then logs the old line.**
   Because a click on a button that is not drawn yet was the cause of the picks
   test's first failure. A script that clicks a button that is not there now
   takes 20 s to say so. Undo: revert 43001b0.
3. **The kept-listing test keeps its listing 10 s, not 3 s.** Because a step
   between two switches took 3.5 s. The product's default is not changed; only
   the test's setting. Undo: revert d04057a.
4. **The time limits of the tests are longer** (the menu test's state wait 60 to
   180 s, last snapshot 120 to 240 s; the rail and column tests 90 to 240 s and
   60 to 180 s; a window's closing 15 and 20 to 60 s). Because a limit is not a
   wait, and a window needed more than the limit to start beside two suites.
   Undo: revert 6b8d18c.
5. **Two changes of product code with a user-visible effect, found by the tests:**
   `ContextMenuFlyout` (998e4e2) and `ContextMenuEditor` (17325c8), both written
   up above, both with a CHANGELOG line under Fixed. There is no unit test for
   either: they need WinUI's popups and focus, so the end-to-end tests are their
   tests (the menu place test and the edit test). The report's retry costs a
   task timer of 50 ms for at most 5 s and only while a menu is open and not yet
   drawn (Article 1: nothing waits on the UI thread). Undo: revert the commit;
   the two tests then fail again, 2 of 5 and 1 of 5 beside the load.
6. **The core's 3 s limit for Windows' menu is not changed.** Because it is the
   product's decision that a menu that takes longer is an error with a notice.
   The snapshot aid asks again, as a user would. Undo: revert 894d03b.
7. **The first-menu test was taken though it is not on the list.** Because it
   failed in the batch of my own and has the same kind of cause (`until:menus-prepared`).
   Undo: revert ca01259.
8. **A speculative change was not applied:** a retry in the Windows menu's own
   `ReportPlaced` (`ShellMenuFlyout`), which logs nothing when the presenter is
   not laid out at `Opened`. Because no run showed it fail in 40 loaded runs.
9. **The batches ran in this order of tests and the fixes in this order** because
   each failure hid behind the one before; the commit list below is the order.

## Needs you

- Merge the branch `worktree-agent-a21066120fda85fae`. After the merge, rebuild
  the window (`dotnet build CabinetOS.sln -warnaserror`) before the end-to-end
  tests run, since the commits change the window's own code (the snapshot aid
  and the two product files).
- The PC full suite ran at 894d03b. If you want it at the last commit, say so
  when the PC is free; it is a test script and documents that changed after.

## What is left

- **`A_menu_asked_for_while_the_same_menu_closes_comes_on_screen` failed once**,
  in the quiet run of 15 tests at 894d03b (state "first": "the first menu is
  not on screen"). The log: "context menu shown", 13 ms later "context menu closed",
  no "opened"; and two `workspace_info` requests, which the window sends when it
  is activated, 3 ms and 11 s after the close. So the window lost the
  activation and got it back, and WinUI closes a flyout when its window
  loses it: another test window of the same suite run (the end-to-end
  classes run in parallel) took the activation. It did not fail in the 40 loaded
  runs of the batches (five before, 35 after) and failed in 1 of the 8 quiet runs. Not fixed. A defence would ask for the menu again in
  `until:menu` when the menu closed without opening, and the test's counts of
  three "shown" lines would have to follow; or the end-to-end classes of the
  menus could run in one collection. The same cause was seen once in the Windows
  menu test (81af875 made it harmless there).
- **A fault in the product, seen once in the laptop's second full suite and not
  fixed (out of scope):** `ShellEndToEndTests.Quick_Open_finds_...` failed
  on an ERROR line of the window's log, "a background task failed and nobody
  observed it": a `COMException` (0x800F1000, "Element is already the child of
  another element") from `ItemsRepeater.GetOrCreateElement` in
  `CommandPalette.BringHighlightIntoView` (`CommandPalette.xaml.cs:140`), reached
  through `PaletteModel.SetHighlight` (`PaletteModel.cs:255`), `PaletteModel.SearchAsync`
  (`:225`) and `MainWindow.RefreshCommandsAsync` (`MainWindow.xaml.cs:1329`): the
  palette is refreshed when the core reports a changed command list, and asks the
  list for an element while it is in a layout pass. Nothing shows on screen, the
  log says it. It is the kind of thing round 2 found in the same palette
  (2138100); the next step is a catch around the call, or asking for the element
  in the next layout pass. The full output is in
  `_io\test-runs\tests-2026-10-02-2117-rd-omen-laptop.txt`.
- **A speed test, seen once in the laptop's first full suite:**
  `StartEndToEndTests.The_core_is_started_while_the_window_is_built_and_answers_hello_at_once`
  failed with "hello took 69.7 ms" against its limit of 60 ms
  (`StartEndToEndTests.cs:83`). It measures a reply, not a wait, so no step
  can serve it; like the frame tests of round 1 it belongs in the collection that
  runs alone, or its limit on the laptop is a decision for you.
- Not on this list and not run: `Tabs...Tabs_and_locks_of_both_panes...`,
  `Columns...A_drag_a_fit_and_a_reset...` and `CompactOverlay...Resizing_the_drawer...`
  (the first report's "seen, not touched" list, beside two suites).
- `ShellMenuFlyout.ReportPlaced` has no retry (decision 8).
- The Windows menu's 3 s limit in the core (decision 6) is a decision for the
  creator if Windows' menu should wait longer on a slow machine.
- The 189 `wait:` steps the first report counted are not converted, only those
  of the seven tests above.

## Commits

| Commit | Subject |
|---|---|
| 0b7ccd3 | test: a failed check of the column, menu, kept-listing and rail window tests shows the window's log with times |
| 7b52da9 | test: repeat-tests.ps1 prints every failed test of a run, not only the first 60 lines |
| 9a2c05a | fix: the column view's snapshot step waits until the view is drawn before it logs what is on screen |
| 97dbf32 | fix: the menu edit test waits for the menu, the edit mode, the prompt's rows and the rows' layout, not for fixed times |
| fbb9c8a | fix: the Windows menu test waits for the core's answer and writes the config file when the script says so |
| d824017 | fix: the menu placement test waits for each menu's place and for the window's new size, not for 900 ms and 800 ms |
| c7a4b41 | test: a window test that runs out of time waiting for a state or a snapshot shows the window's last log lines |
| d04057a | fix: the kept-listing test keeps its listing for 10 s and waits until it is let go, not for 4.5 s |
| 43001b0 | fix: a click step waits for its button, and the rail tests wait for the page, the pick and the layout, not for fixed times |
| a24c2e4 | test: the Windows menu test's recorder program retries a busy file, and a missing line is reported with what the file holds |
| 6b8d18c | test: the time limits of the menu, rail, column view and kept-listing tests are longer than a busy machine's start |
| 0fa3be0 | fix: the menu tests wait for what the file holds, not for "a read of the configuration" |
| aa76c15 | test: the Windows menu test asks for Windows' menu once to load the shell's handlers, then checks the second |
| ca01259 | fix: the first-menu test waits until the menu shapes are built, and the kept-listing test waits for each switch's frame |
| 81af875 | fix: shellmenu: waits for the core's answer instead of a fixed 1.5 s |
| 998e4e2 | fix: the context menu is reported and its size kept only once every button is inside a drawn popup (product code) |
| 17325c8 | fix: the menu's edit mode ignores a late GotFocus of a row that a rebuild replaced, and the key step waits for the focus (product code) |
| 894d03b | test: the shellmenu steps ask again when the core answers "took longer than 3 s" |
| 9b3baa5 | test: the sidebar pick test waits until each pick's listing is drawn, and a failure shows the picks of the whole run |
| (this report) | docs: the snapshot steps, the changelog, the plan and the report of the third round |
