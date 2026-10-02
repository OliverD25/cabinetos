# Two more flaky end-to-end tests (2026-10-02, morning of the night run)

Context: the second round of the work in
[../2026-10-01/flakes-report.md](../2026-10-01/flakes-report.md). Two window tests
failed in 2 of 3 full runs on the main PC, with and without an unrelated fix:
the Quick Open test and the out-of-view menu test. Written by the Sonnet
coder agent in the worktree branch `worktree-agent-a25ea556ea2aaf76b`. Every
proof against load ran on the creator's Omen laptop (`remote-script.ps1`,
`remote-tests.ps1`, `repeat-tests.ps1 -Suite 2`); the full suites ran on the
main PC after `wait-for-pc.ps1` said it was free.

## Summary

| Test | Cause | Change | Laptop, `-Suite 2`, 5 runs: before, after |
|---|---|---|---|
| `ShellEndToEndTests.Quick_Open_finds_...` | Fixed waits for events that a busy machine raises late. `quick-open:` waited 500 ms for the text to reach the search (XAML raises TextChanged on its next frame, the search goes out 80 ms later, the core answers after that), so the state "typed" had Quick Open open and no rows. The same fixed waits hid in `quick-open-key:enter` (400 ms for the folder to be listed) and in the two `wait:500` before the workspace states. A second fault, once the first was out of the way: a race in the product, the command palette applied a search answer after it had closed | `quick-open:` waits for the answer, `quick-open-key:enter` and `ctrl+enter` wait for the opening, `until:workspace` is new; `PaletteModel.SearchAsync` drops an answer for a closed palette | before: **0 of 5** (all at the state "typed"); after the first change alone: 2 of 5 and 3 of 5 (every failure the palette's error); after both: **5 of 5** |
| `ContextMenuEndToEndTests.The_keyboards_menu_for_a_row_out_of_view_...` | The log line "context menu placed" is written some low-priority dispatcher turns after WinUI opens the menu; a busy machine runs them late, and the test closed the menu after a fixed 800 ms, before they ran (a closed menu is never reported) | `until:menu-placed` is new and replaces the `wait:800` | before: **0 of 5** (no "context menu placed"); after: **5 of 5** |
| `MarketplaceCardsEndToEndTests` (3 tests; taken after the two above) | The cards come one slice per low-priority dispatcher turn and the view is laid out in the idle slot after start; a busy machine runs those turns late, and the tests read the cards after a fixed 2000 ms and 2500 ms | `until:market-prepared` and `until:market-complete` are new and replace the fixed waits | before: **0 of 5** (45 to 75 of 120 cards made at the read); after: **5 of 5** (all 3 tests each run) |

## What the first read of the brief got wrong

The brief and the earlier report said the menu test showed "no `context menu
shown` line". The log has that line in every failing run. The line that is
missing is `context menu placed`. The earlier message came from
`Assert.Single`, which names no line; the test's own check now names it
(`OnlyLine`).

## The method

As in the first report: a diagnostic run with the test's failure message
widened, then `repeat-tests.ps1 -Suite 2` to make it fail, then the cure.

- The Quick Open test's failed state check lists the Quick Open, workspace and
  state lines of the window's log with their times and the fields that matter
  (rows, branch, root, the panes' folders and cursors). The menu test lists
  the window's last 45 log lines (frame lines left out). Both test classes print
  the log's ERROR lines themselves when `FinishAsync` finds one (the collection
  assert printed the whole log as garbled text), and the Quick Open test prints
  an error's first 40 lines, so a stack shows (commits fdc8fbe, 1922065,
  2eb18d3).
- The load is the laptop's `-Suite 2`, as before. Each batch ends two test
  runs' windows by killing them, and every batch leaves a dead `CabinetOS.exe`
  with no thread in the clone it ran from (the first report's "Dead windows that
  lock a build"). The next build in that clone then fails with MSB3026 ("Could
  not copy CabinetOS.Core.dll"). So each build after a batch went into a new
  clone made from the main clone (`cabinetos-g`, `-h`, `-i`, with a copy of the
  release core built here). The clones are listed under "The laptop".

## Test 1: the Quick Open test

**What was seen.** Five of five runs beside `-Suite 2` failed the same way:

```
state "typed": Assert.StartsWith() Failure: String start does not match
String:         ""
Expected start: "notes.md (proj\\docs)"
... quick open shown        02:38:20.170
... shell state typed       02:38:21.000   quick_open=True rows=[]
```

The state came 0.6 to 0.9 s after Quick Open was shown, with the box open and
no rows. On the main PC the test failed in 2 of 3 full runs the same way (its
state "typed" had no row; the click-focus report). The first run on the quiet
laptop failed with an ERROR line in the window's log (its text was garbled in
that output; it is probably the second fault below); the next quiet run passed.

**Cause 1: fixed waits.** `quick-open:<text>` put the text in the box and
waited 500 ms. XAML raises the box's TextChanged on its next frame (a frame
every few hundred milliseconds beside two test runs), the window sends the
search 80 ms after that, and the core answers after that. The same pattern is in
three more places of the test: `quick-open-key:enter` and `ctrl+enter` ran
`OpenFromQuickOpenAsync` as a discarded task and waited 400 ms for the pane to
list the folder, and `wait:500` before the states "pill" and "no-repository"
waited for the core's `workspace_info` answer (the pill's branch). The text
`>tab`, which switches to the palette, had the same 500 ms.

**Change** (commit 37d65f1; `MainWindow.QuickOpen.cs`, `MainWindow.Shell.cs`,
`MainWindow.xaml.cs`, the test, `docs/ui.md`).

- `quick-open:<text>` keeps its 500 ms (the speed review types with it at that
  pace), then waits at most 20 s until the model's `Changed` arrives with the
  typed text as its `Query`, which is the answer, or until Quick Open closes (the
  `>` text). It logs "quick-open step ended" with how long it took.
  It subscribes before it types, so an answer inside the 500 ms is not missed.
  No product class changed for this: the model already says when it answered.
- `quick-open-key:enter` and `ctrl+enter` wait for the opening's task (the
  field `_quickOpenOpening` holds it; the handler threw it away before) and
  then two frames, before their 400 ms.
- `until:workspace` is new: no `workspace_info` question is out (a counter,
  `_workspaceAsking`, in `UpdateWorkspaceAsync`). The test uses it for the two
  states that followed a `wait:500`.

**Cause 2: a race in the product.** With cause 1 out of the way, 2 of 5 and then
3 of 5 runs failed with an ERROR line in the log that no run had shown before
(the old waits never reached it):

```
a background task failed and nobody observed it
System.Runtime.InteropServices.COMException (0x800F1000): No installed components were detected.
Element is already the child of another element.
   at ABI.Microsoft.UI.Xaml.Controls.IItemsRepeaterMethods.GetOrCreateElement(...)
   at CabinetOS.Views.CommandPalette.BringHighlightIntoView() in CommandPalette.xaml.cs:line 140
   at CabinetOS.ViewModels.PaletteModel.SetHighlight(Int32 index) in PaletteModel.cs:line 252
   at CabinetOS.ViewModels.PaletteModel.SearchAsync(String query, Boolean keepHighlight) in PaletteModel.cs:line 222
```

The test types `>tab` into Quick Open, which opens the palette with "tab", and
closes the palette a moment later. XAML raises the palette box's TextChanged a
frame late, so the palette's search started and was answered after the close;
`SearchAsync` dropped an answer for a newer search but not for a closed
palette, and applying it set the highlight on a list that was no longer shown.
The error is in the log and nothing shows on screen. A user who closes the
palette in the few milliseconds the core needs to answer could reach it. The
cure is one condition in `SearchAsync`: `!IsOpen` drops the answer (commit
2138100, `CHANGELOG.md` in be2626c). This is the only product change of the
round besides two behaviour-free ones (see "Decided without you").

**Proof (laptop, `-Suite 2`).**

| Run set | Code | Result |
|---|---|---|
| before | 1922065 (only the test's output changed; the old window code) | **0 of 5**, all at the state "typed" |
| after change 1 only | f9dc50e | 2 of 5: the three failures are the palette's ERROR line |
| after change 1 only | 2eb18d3 (more test output only) | 3 of 5: the two failures are the same ERROR line |
| after both changes | 2138100 | **5 of 5**, 53 to 97 s each |

## Test 2: the out-of-view menu test

**What was seen.** Five of five runs beside `-Suite 2` failed with
`Assert.Single() Failure: The collection did not contain any matching items`.
The new timeline shows that the log has "context menu shown" and "context menu
opened", the state "open" (the menu is on screen), and then, after the test
closed the menu, "context menu closed". It never has "context menu placed". The
frame lines around show gaps of 0.4 to 5 s between frames.

**Cause.** `ReportPlaced` (in `ContextMenuFlyout`) waits for the menu's buttons
to be laid out, one low-priority dispatcher turn at a time, at most ten, and
only for a menu that is still on screen. A busy machine runs low-priority turns
late. The test closed the menu 800 ms after asking for it. The menu was on screen
(the state "open" read true) but its place had not been reported, and a closed
menu is never reported.

**Change** (commit f9dc50e; `MainWindow.ContextMenu.cs`, `MainWindow.xaml.cs`,
the test, `docs/ui.md`). `until:menu-placed` waits (at most 20 s) until the menu
in front is settled (`until:menu`) and its place is logged; the window keeps a
flag, `_menuPlaced`, set by the `Placed` event and cleared when a menu is asked
for. The test uses it in place of the `wait:800`. The assertions are the same;
a failed one names the missing line.

**Proof (laptop, `-Suite 2`).** Before (test code of 1922065 and window code of
the main branch): **0 of 5**. After (2eb18d3, which has the change): **5 of 5**,
52 to 80 s each.

## Tests and runs

- Local build: `dotnet build CabinetOS.sln -warnaserror`, 0 warnings, 0 errors.
- Fast tests (no window), `dotnet test --solution CabinetOS.sln --no-build`:
  1259 total, 1208 passed, 51 skipped, 0 failed (9 s).
- The full suite with the end-to-end tests (`CABINETOS_UI_E2E=1`, this worktree's
  own release core), on the main PC, after `wait-for-pc.ps1` exited 0:
  - the code of f9dc50e: **1259 of 1259**, 2 min 7 s;
  - the final code of the two tests (2138100), two runs: **1259 of 1259**, 2 min 5 s
    and **1259 of 1259**, 2 min 22 s.
  A first run of the same chain (05:54 to 05:58) finished, but my script gave
  both runs one result file and the second run overwrote it, so that run is not
  counted.
- `build\check-scripts.ps1`: no script changed.
- Every changed `.cs` file is CRLF (checked with `file`); the `.md` files are LF.

## The third test: the marketplace class

Both assigned tests were done at 06:30, so I took the first of the "Needs you"
list: the marketplace tests, which failed in every full run on the laptop. I ran
the whole class (`MarketplaceCardsEndToEndTests`, 3 tests) beside `-Suite 2`.

**What was seen (before, clone `-i` at 2138100, 5 runs, none passed).**

| Test | Failed in | Failure |
|---|---|---|
| `A_120_item_index_...` | 5 of 5 | `market:complete` read `(75, False)`, `(60, False)`, `(45, False)` where `(120, True)` was expected: 45 to 75 of 120 cards made |
| `A_tab_chosen_while_the_slices_are_made_...` | 3 of 5 | `market:themes-done` read `(15, 40, False)` where `(40, 40, True)` was expected |
| `The_view_is_prepared_after_start_...` | 5 of 5 | "the window did not close" in 3 runs, 2 of 5 expected ids in 1, and an `Assert.Single` in 1 |

**Cause.** The cards come one slice at a time, each in a low-priority dispatcher
turn (`NextSlice`), and the view is laid out ahead in the idle slot after the
menu shapes (`PrepareMarketplaceWhenQuiet`). A busy machine runs low-priority
turns late. The tests read the cards after a fixed 2000 ms (and the prepared
test waited 2500 ms for the idle slot). It is the same cause as the menu test's
(a low-priority turn), and not the same as "the window did not close". I did not
find that one's cause; it did not appear in the 5 runs after the change.

**Change** (commit 7542320; `MarketplaceView.xaml.cs`, `MainWindow.xaml.cs`, the
test class, `docs/ui.md`). `until:market-prepared` waits until `PrepareLayout`
laid the view out ahead and logged it ("marketplace view prepared");
`until:market-complete` waits until every card of the set is made and
"marketplace cards complete" is logged. The view keeps two flags for them
(`WasPreparedAhead`, `CardsComplete`), set where the line is logged; a new set
clears the second. `LogAtFrame` got an optional callback for it. The tests use
them in place of `wait:2500` and of the `wait:2000` before the reads (in
`A_tab_chosen...` only the last one, where the second set has surely started).
The assertions are the same.

**Proof (laptop, `-Suite 2`).** Before: **0 of 5** (the numbers above). After,
clone `-j` at 7542320: **5 of 5**, all 3 tests in each run, 120 to 144 s.

## The third test's full suite

The final code (7542320) on the main PC, 3 runs of the full suite with the
end-to-end tests, in this order: **1258 of 1259** (2 min 39 s), then **1259 of
1259** (2 min 6 s) and **1259 of 1259** (2 min 6 s). The one failure of the first
run is
`ContextMenuEndToEndTests+Alone.Entering_the_edit_mode_over_100000_selected_rows_adds_no_slow_frame`
(a frame goal, with a 31 ms garbage collection in a frame). It is a frame
measurement; my own `dotnet build` ran on this PC while that run was going,
which is the likely reason, and it passed in the two later runs. It does not
touch this work.

## The laptop

- Signed-in desktop: yes (`quser` showed the session; the scripts say "nobody is
  signed in" otherwise).
- The first build there failed with MSB3026: the batch before it had left a dead
  `CabinetOS.exe` in the clone `C:\Dev\cabinetos\cabinetos`. I did not restart
  the laptop and did not look for the holder of the handles. I made three
  clones with `git clone` from the main clone, each with a copy of the release
  core built from this worktree: `C:\Dev\cabinetos\cabinetos-g`, `-h` and `-i`.
  Each holds the dead windows of its batches now (one or two per batch run);
  the older clones `-e` and `-f` hold last night's. `-j` was made for the
  marketplace batch (see "The third test").
- I copied this worktree's release core to
  `C:\Dev\cabinetos\cabinetos\core\target\release\cabinetos-core.exe` (the
  laptop's was older than the terminal unit 2 protocol). Nothing else outside
  the clones and `_io` was written, and no setting, service or task was changed.
- The output of `remote-tests.ps1` landed first in
  `cabinetos\.claude\worktrees\_io\test-runs` (a folder next to this worktree)
  because the script puts `_io` beside the repository's parent, and a
  worktree's parent is `worktrees`. I used `-Io` for the later runs. The runs'
  full outputs are in `_io\test-runs` and `_io\script-runs` (`tests-2026-10-02-05xx`
  to `-06xx`, `script-2026-10-02-0535` to `-0618`).

## Decided without you

Each line: what, because, undo.

1. **`quick-open:` keeps its 500 ms and then waits for the answer.** Because the
   speed review's `quick-open` scenario types with the step at that pace, as
   `find:` does. Undo: revert 37d65f1 (the test returns to its flake).
2. **`quick-open-key:enter` waits for a task that was discarded before.** The
   handler in `SetUpQuickOpen` stores the task in `_quickOpenOpening` instead of
   `_ =`. The product's behaviour is the same; an error in it is still not
   observed unless a snapshot step awaits it. Undo: revert 37d65f1.
3. **`UpdateWorkspaceAsync` counts its open questions** (try/finally). Same
   behaviour. Undo: revert 37d65f1.
4. **`PaletteModel.SearchAsync` drops the answer of a closed palette.** The race is
   in the product (see cause 2). One condition and a CHANGELOG line. Undo: revert
   2138100 and be2626c; then 2 of 5 runs of the Quick Open test beside the load
   fail again.
5. **`until:menu-placed` and its flag `_menuPlaced`.** Window code only the snapshot
   steps use, set by an event the menu already raised. Undo: revert f9dc50e.
6. **A new clone on the laptop for each build after a batch** (see above). Undo:
   delete `cabinetos-g`, `-h`, `-i` after a restart of the laptop, when the dead
   windows are gone.
7. **The two test classes print more on a failure** (log lines with times, ERROR
   lines, the stack of an error). The assertions are the same.
8. **The marketplace class was taken after the two assigned tests**, as the brief
   allowed before 06:45. Its change is `until:market-prepared`, `until:market-complete`
   and two flags and an optional callback in `MarketplaceView` (window code the
   snapshot steps use). Because the cause is the same kind as in the other two.
   Undo: revert 7542320.
9. **The `A_tab_chosen...` test keeps its first two `wait:2000`.** Because after a
   click the flag of the old set may still read "complete", so `until:` could end
   at once there; only the last wait follows a read that proves the new set began.
10. **Two product methods changed in behaviour-free ways** (decisions 2 and 3 above)
    and the marketplace's flag setting. The commit messages of 37d65f1 and 7542320
    say "paths unchanged"; strictly, 37d65f1 stores a task and counts questions in
    two product methods with the same behaviour. Undo: revert them.

## Needs you

- The `Needs you` list of the first report still holds for the tests I did not
  take (see "What is left").
- The dead `CabinetOS.exe` processes on the laptop: 5 in `-e`/`-f` from last
  night, and now one batch's worth in each of `-g`, `-h`, `-i` and the main clone.
  They lock builds in those clones. A restart clears them. I did not restart the
  laptop.
- `docs/PLAN.md` is not changed.

## What is left

Of the timing tests that the first report lists as failing in full runs on the
laptop, the marketplace tests are done (above). Not taken, for lack of time
(the window runs had to end at 07:00):

- the column view test (`ColumnViewEndToEnd...Three_levels_open_close...`);
- the kept-listing test (`KeptListingEnd...Switching_between_two_tabs...`);
- the context menu test `The_menu_follows_the_file_runs_a_program_and_shows_windows_own_menu`;
- the rail test `RailEndToEnd...The_rail_switches_views_keeps_the_last_web_page_warm...`.

What I read of them, without a run: every `RunRailStepAsync` step ends with a
fixed 400 ms, and `rail-state:wide` reads `SidebarColumn.ActualWidth`, which a
busy machine updates a frame or more later than the drag set it (the first
report saw 224 where 320 was expected); two frames of `SettleFramesAsync` after
the step would be the cure. The context menu test writes the configuration file
as soon as it sees the state "background", and its `until:config` takes the
count of reads when the step starts, so a read that came before the step began
is waited for in vain (20 s); it also waits a fixed 1.5 s in `shellmenu:` for
the core. Both are guesses to check with `repeat-tests.ps1 -Suite 2`.
Also similar in the same class as the out-of-view menu test:
`The_menu_opens_with_its_corner_at_the_point_and_flips_before_the_windows_edge`
asserts 6 "context menu placed" lines after `wait:900` each; `until:menu-placed`
would serve it. It was not in the lists and I did not run it.

One more thing seen and not chased: in 3 of 5 runs of the marketplace
"prepared" test before the change the window did not close within 20 s. It did
not happen in the 5 runs after. If it comes back, the window's last log lines
in the failure message (as the terminal test prints them) are the first step.

## Commits

| Commit | Subject |
|---|---|
| fdc8fbe | test: a failed check of the Quick Open test or the out-of-view menu test shows the window's log with times |
| 1922065 | test: a window test that fails on an ERROR line in the log prints that line |
| 37d65f1 | fix: the Quick Open snapshot steps wait for the core's answer and the opened folder, not for a fixed time |
| f9dc50e | fix: the out-of-view menu test waits for the menu's place to be logged, not for 800 ms |
| 2eb18d3 | test: the Quick Open test prints an error line of the window's log as an exception with its stack lines |
| 2138100 | fix: a command palette that closed while its search was out drops the answer (product code) |
| be2626c | docs: changelog entry for the command palette's late search answer |
| 7542320 | fix: the marketplace tests wait for the slices and the prepared view, not for 2 s and 2.5 s |
| (this report) | docs: report on the two flaky tests and the marketplace class |
