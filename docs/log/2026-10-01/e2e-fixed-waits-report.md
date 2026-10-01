# End-to-end tests that fail under load, and the fixed waits in them (2026-10-01)

Context: the window's end-to-end tests in `ui/CabinetOS.Tests/*EndToEndTests.cs`
(run with `CABINETOS_UI_E2E=1`). Four of them failed on a loaded machine and
passed on a quiet one. This report says why each failed, what each waits for
now, and what is still open. Written by the Sonnet agent in the worktree
branch `worktree-agent-aee21d0e5f43035a9`, on the main PC, while the creator
was away.

## Summary

| Test | Cause under load | Result |
|---|---|---|
| `RailEndToEndTests.The_tree_has_rows_on_the_screen_when_the_window_starts_in_the_rail_layout_before_any_key` | The window looked at its tree once, 600 ms after the scroll. A loaded machine lays the list out later (rows on screen at 1.8 s, not 0.6 s). | Fixed and proven (307cc82) |
| `ContextMenuEndToEndTests.A_menu_asked_for_while_the_same_menu_closes_comes_on_screen` | Two causes: a fixed 600 or 1000 ms wait for a flyout that WinUI opened up to 3 s late, and a 50 ms limit on a time that is the UI thread's own work. | Fixed and proven (5aa09e4) |
| `ContextMenuEndToEndTests+Alone.Entering_the_edit_mode_over_100000_selected_rows_adds_no_slow_frame` | A blocking garbage collection of 21 to 33 ms lands in the measured frame. The machine decides which side of 33 ms it lands on. | Fixed and proven (3c8186b, ef0232b) |
| `CompactOverlayEndToEndTests.The_dock_comes_back_with_its_terminal_and_the_commands_that_would_undo_the_drawer_are_refused` | **Not found.** I could not make it fail here. | Defensive change only (e6dea5b). See "Needs you". |

Commits on the branch, oldest first:

| Commit | Subject |
|---|---|
| 307cc82 | rail test: the tree's look waits for the first row on the screen |
| 5aa09e4 | menu reopen test: wait for the menu on screen, and judge the overlap by events |
| 3c8186b | edit-mode frame test: a slow frame must come in every fresh start to fail |
| e6dea5b | compact overlay tests: wait for the shell and the window's style, and say why a check failed |
| ef0232b | edit-mode frame test: wait until the machine is calm before each start |
| df00ff4 | edit-mode frame test: correct the comment on how busy a calm machine is (comment only) |

## The load I used

The machine has 32 logical processors. I made load in three ways and say which
one each proof used:

- **One full suite beside the test** ("suite"): `dotnet test --solution CabinetOS.sln --no-build` with the end-to-end tests on, started in the background, then the test run in the foreground. A suite uses 40 to 55 % of the CPU in its first minute and about 20 % after that.
- **Two full suites** ("2 suites"): the same, twice. This is heavier than the brief asked for; a third of the other end-to-end tests fail in it, so it is only used where it reproduced a failure.
- **40 busy processes** ("burner"): `scratchpad\burn.py 40 <seconds>` (a Python script that starts 40 processes, each looping at normal priority, and stops by itself). The CPU is 100 % busy. I used it with one suite for the final proofs.

I did not run `cargo bench`. I did not touch the Omen laptop, VirtualBox or the VM.
The release core is a copy of `core\target\release\cabinetos-core.exe` from the
main checkout (built 13:57), put in the worktree's ignored `core\target\release\`.

## 1. The rail tree test

**Cause.** The window logged "the tree drew rows" once, 600 ms after it
scrolled the tree to the active folder. The test judged the tree by that one
line (`visible` rows inside the sidebar's window). In a failed run, a repeated
look showed 0 visible rows at 0.6 s and 1.2 s and 31 at 1.8 s, and 31 after.
The tree was slow, not empty. Before the change the test failed in every full
run on a loaded machine (the other sessions' logs `e2e-full`, `e2e-control1`,
`e2e-kept1`, `e2e-kept2`, `e2e-kept7`, and my own first run), and in 2 of 5 runs of the test alone beside one suite.

**What waits for what now.** `MainWindow.Rail.cs` (`LogTreeDrawnSoon`): the
first look is still at 600 ms. A look that finds no row inside the window is
made again every 200 ms for up to 10 s, and only the last look is logged, with
`looked_after_ms`. A quiet machine logs the same line at the same time as
before. A tree that stays empty still fails, 10 s after the scroll, and the
message says how long the window waited. A newer reveal now stops the look of an
older one. `docs/ui.md` says so. The test itself changed in its comment and its
message only.

**Proof.** Before: failed in 4 of 4 full runs beside a second activity, 2 of 5 alone beside one suite.
After: 8 of 8 beside one suite, 6 of 6 beside 2 suites, 5 of 5 beside the burner and one suite (runs of 10 to 14 s, against 3 s on a quiet machine).

## 2. The menu reopen test

**Cause 1.** The script waited a fixed 600 or 1000 ms after each menu and then
read "on screen". The next menu waits for WinUI's `Closed` of the one before.
Beside 2 suites that `Closed` came up to 3 s after the `Hide` (the log of a failed
run: close at 25.708 s, the old flyout's "closed" at 29.092 s), so the state was
read while a menu was still pending.

**Cause 2.** The test asserted that the next "context menu shown" line came
within 50 ms of the "overlay.close" line. That time is the UI thread's work for
the menu: 15 ms on a quiet machine, 52 and 53 ms in two failed runs. What the
50 ms stood for is "asked for while the flyout is still closing".

**What waits for what now.** New snapshot steps `until:menu` (the menu in front
is on screen and no newer menu waits for the one before it) and
`until:menu-closed`, with the same 20 s limit as the other `until:` steps. The
script uses them in place of the four `wait:600/1000` steps. The overlap is now
judged by events: no "context menu closed" line lies between the close and the
next request (the window logs that line only when no newer menu waits for the
`Closed`). Changed: `ContextMenuFlyout.cs` (`IsSettled`), `MainWindow.xaml.cs`
(two `until:` cases), `docs/ui.md`, the test.

**Proof.** Before: failed in 2 of 8 runs alone beside 2 suites, and once in a full run (53 ms).
After: 10 of 10 beside 2 suites (the load faded after the third run), 6 of 6 and 5 of 5 beside the burner and one suite, and the test takes 6 s on a quiet machine instead of 9 s.

## 3. The edit-mode frame test

**Cause.** In a failed run the one slow frame is 35 to 41 ms of UI-thread work
that is a blocking generation 2 collection of the garbage collector
(`gc_pause_ms` 31 to 33, a 12 MB heap), in the frame after the edit mode opens.
Every failed run had the same numbers. The collection happens in every run;
how long it takes depends on the machine (21 to 33 ms under load). The window
does not force the collection (`GC.Collect` is not called).

**What waits for what now.** The test makes up to three fresh starts (each with
its own warm-up opening) and fails only when all three have a frame over 33 ms
of UI-thread work. A cost of the window's own on 100,000 rows is in every
start; a pause of the machine's is not. Before each start the test waits until
the machine is under 35 % busy over one second (`CpuLoad` from the window's core
library; the main PC idles at 13 to 19 %), at most 30 s, and then measures anyway.
The failure message lists the frames and the log of every start and says how busy
the machine was before and during it. The constants are `Attempts` and the 35 / 30 in `WaitForCalmAsync`.

**Proof.** Before: failed in 2 of 8 runs alone beside the burner and one suite, and in 3 full runs.
Three starts alone were not enough beside a second full suite (all three had a slow frame in one run, 46 s), so I added the calm wait. After the calm wait: passed in 2 full runs beside a second suite, and 5 of 5 alone beside the burner (the machine is never calm there, so each start waited 30 s: runs of 21 to 114 s; the 114 s run used three starts).
Before the calm wait: 8 of 8 beside the burner and one suite (runs of 24 to 51 s, so some used a second start).
The cost: a regression of the window's own is found after three starts, about 1 to 3 minutes.

## 4. The dock test (not explained)

**What was seen.** In the full-run logs of other sessions (`e2e-final`,
`e2e-kept7`, and the keys audit's hand-back) it failed three times at the same
line: `Assert.True(NativeWindow.IsTopmost(window))` right after the
"compact overlay entered" line, 7.8 to 8.1 s into the test.

**What I tried.** 19 runs on a quiet machine; 41 runs beside one suite, 2 suites,
the burner, or the burner and a suite. All passed, and it was not in the failure list of any of the
about 14 full runs whose results I read. A probe that read the
topmost style every millisecond showed it 31 to 46 ms before the "entered" line is
in the file (quiet and loaded), so the line does not come before the style. The
process has one visible window, "CabinetOS", with the style `0x108` (topmost).
(Two hidden IME windows of the process also carry the topmost bit.)

**What I changed (a defence, not a proven fix).** The script waits for the shell
(`until:terminal`) instead of a fixed 3 s. The test waits up to 10 s for the topmost
style, and for its going away after the drawer, before it judges. When the check
fails anyway the message now says how many "entered" and "left" lines the window
logged, whether its process has exited (and with what code), and every window of
the process with its style. The next failure will explain itself.

**Hypotheses, none checked.** (a) Something toggled the drawer between the log line and the
test's read: the drawer's key is Ctrl+Alt+Up, and a real-key check (the live check or a
keys probe) that ran at the same time as the suite sends it to whichever
CabinetOS window has the keyboard. The new message shows it as a "left" line already in the log.
(b) The window's process died (a crashed window has no style: `GetWindowLongPtr` returns 0, which reads as "not topmost").
The new message shows it as "process has exited". (c) A ghost window (Windows' stand-in for a window that does not answer) with the same title
was found first.

## Every fixed wait in the end-to-end tests

Line numbers are those of the brief (before my changes).

| Where | Wait | What it is for | What became of it |
|---|---|---|---|
| `CompactOverlayEndToEndTests.cs:67` | `Task.Delay(1000)` then `AssertLayoutUntouched` | Proves nothing is written to the file while the drawer is on. | **Stays**: a wait to prove that nothing happens. |
| `CompactOverlayEndToEndTests.cs:193` | `Task.Delay(1500)` then `AssertLayoutUntouched` (dock test) | The same. | **Stays.** |
| `CompactOverlayEndToEndTests.cs:252` | `Task.Delay(120)` between three `NativeWindow.Resize` calls | Paces a simulated drag; the window saves 500 ms after the last resize. | **Stays**: pacing, not a wait for something. It would need a 380 ms stall of the test thread to matter; it did not in 8 loaded runs. |
| `CompactOverlayEndToEndTests.cs:275` | `Task.Delay(300)` after the "follows the configuration" line | Windows finishing a resize the window had only started. | **Now a wait for the condition**: the drawer is 440 by 560 on screen, 10 s limit. |
| `CompactOverlayEndToEndTests.cs:322` | `Thread.Sleep(100)` in `ReadFile`'s retry | Retry when the read meets the core's swap of the file. | **Interval stays**; the limit grew from 10 tries (1 s) to 100 (10 s). |
| `CompactOverlayEndToEndTests.cs:548` | `Task.Delay(100)` in `WaitForAsync` | Poll interval. | **Stays.** |
| `EndToEndTests.cs:282` | `Task.Delay(OutputCoalescer.FrameMilliseconds)` | Poll interval of the echo loop (20 s limit). | **Stays.** |
| `EndToEndTests.cs:655` | `Task.Delay(20)` in `WaitUntilAsync` | Poll interval (30 s limit). | **Stays.** |
| 20 lines `Task.Delay(200)` in 15 files | in `WaitForAsync` or a loop with a deadline | Poll intervals. I read the lines around each. | **Stay.** |
| 24 lines `WaitForExit(15_000 / 20_000 / 120_000)` | the window or script ends | A wait for the condition with a timeout. | **Stay.** |
| `Support/Repo.cs:54, 58` `Thread.Sleep(100)` | retry when a folder is in use | Cleanup retry. | **Stays.** |
| Script steps `wait:<ms>` (194 in the end-to-end tests) | inside the window's snapshot script | Fixed times inside the script; most hold a state long enough for the test to read it, or let a frame pass. | **Four converted** in the menu reopen test (to `until:menu`, `until:menu-closed`) and one in the dock test (`wait:3000` to `until:terminal`). **The other 189 are untouched**: that is a bigger job and I did not see them fail. |

## Full-suite results

All with `CABINETOS_UI_E2E=1`, the release core copied from main, 1193 tests.

| Time | Code | Beside | Result |
|---|---|---|---|
| four runs, afternoon | before my changes (two of them with my temporary logging in the rail test) | my own test runs, one or two suites | 2 to 3 failures each: the rail test in all four (a bug of mine in the logging made it fail in two of them, so those two do not count), the shell test in 3, the edit-mode frame test in 2 |
| 16:24 | after 307cc82 to e6dea5b | nothing | **1193 of 1193 passed**, 2 min 37 s |
| 16:27 | same | a second suite | 1185 of 1193: 8 failures, one of them the edit-mode frame test (three starts, all slow) |
| 16:34 | after the calm wait | a second suite | 1188 of 1193: 5 failures, none of the four (compact resize, kept listing, shell, columns, marketplace) |
| about 16:39 | same | a second suite | 1191 of 1193: shell test and `The_menu_is_edited_inside_the_menu_and_saved_through_the_core` |
| 16:47 | final code (ef0232b) | nothing | **1192 of 1193**: the shell test `The_top_row_fits_924_px_find_filters_one_pane_and_a_tab_keeps_its_place` failed; 2 min 8 s |

The build `dotnet build CabinetOS.sln -warnaserror` has 0 warnings and 0 errors on the final code. I touched no `.ps1`, so `build\check-scripts.ps1` was not run.
The known noise (a marketplace test printing a frame over 33 ms) did not show as a failure.

The last run is **not clean**: one test outside my four failed. It is the shell test below, which also failed in about 10 of the 13 loaded full runs I read and passed in the 16:24 quiet run of the same code but for the frame test's calm wait, so I take it for an existing flake, but I could not prove it, because the creator's time limit stopped further runs at 16:52.

## Decided without you

| Choice | Reason | Undo |
|---|---|---|
| I changed window code, not only tests: the rail tree's logged look (`MainWindow.Rail.cs`), two `until:` steps and `ContextMenuFlyout.IsSettled`. | The fixed time was in the window's own test aid (the single look at 600 ms; the script's `wait:`). A test cannot wait for a condition the window never says. All three are log and snapshot-aid code; the product's behaviour did not change (the tree's look also stops an older look now). | `git revert 307cc82` and `git revert 5aa09e4` (the two commits are separate; each touches its own files and `docs/ui.md`). |
| Replaced the 50 ms limit by "no `context menu closed` line between the close and the next request". | The time was the UI thread's work, not the overlap. The new check is the claim itself. It would fail where the old one passed if WinUI's `Closed` ever came before the second request (a quiet machine has about 8 ms of room: the request 15 ms after the close, the `Closed` about 23 ms); I saw it in none of about 30 runs. | The old two `Assert.InRange` lines are in the diff of 5aa09e4. |
| Three starts and a calm wait for the frame test (not a larger loosening such as ignoring slow frames with a collection pause). | A collection pause is a real cost of the window; only the fresh start and the quiet machine make the measurement fair. Three starts and 30 s are my choice. | Set `Attempts` to 1 and delete `WaitForCalmAsync` (commits 3c8186b, ef0232b). |
| The dock test got a defence, not a fix. | I could not reproduce the failure and will not call it fixed. | `git revert e6dea5b` (it also holds the resize wait and the file-read limit; revert only parts by hand if you want to keep those). |
| I did not apply the same three starts to the sibling `Opening_the_menu_over_100000_selected_rows_adds_no_slow_frame`. | Not asked. It did not fail in my runs. | Copy the loop of the edit-mode test. |
| I deleted nothing of the project's. I threw away my own uncommitted diagnostic edits (the temporary logging in the rail test, the window and the compact test) with `git checkout -- <file>` three times, and stopped one orphan `CabinetOS.exe` left by my own suite run (pid 133140). | They were my throwaway instruments. | Nothing to undo. |

## Needs you

1. **The dock test is open.** When it fails again, take the new message ("the drawer is topmost; the window logged N "entered" and M "left" lines; ...") and the window's log (`logs-dock\ui.*.jsonl` in its test folder). It tells which of the three hypotheses holds. Note whether a live check, a keys probe or any real-key script was running at the time. To try it on purpose, in a terminal:
   `cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/.claude/worktrees/agent-aee21d0e5f43035a9/ui && CABINETOS_UI_E2E=1 dotnet test --solution CabinetOS.sln --no-build --filter "FullyQualifiedName~The_dock_comes_back"`
2. **Merge and check.** The main session merges the branch `worktree-agent-aee21d0e5f43035a9`. After the merge the commits need the window's Debug build (`dotnet build CabinetOS.sln -warnaserror`) before the end-to-end tests run, since two of them change the window.
3. **Seen, not touched** (outside the four):
   - `ShellEndToEndTests.The_top_row_fits_924_px_find_filters_one_pane_and_a_tab_keeps_its_place` failed in about 10 of 13 loaded full runs and in 1 of 3 quiet ones, at `ShellEndToEndTests.cs:97`: after `find:zzz` the pane shows 2 rows, not 0. The script's `find:` step does not wait for the filter to apply.
   - Beside 2 suites these also failed at least once: `CompactOverlay...Resizing_the_drawer_saves_its_size...`, `KeptListing...Switching_between_two_tabs...`, `Columns...A_drag_a_fit_and_a_reset...`, `MarketplaceCards...A_120_item_index...`, `Rail...The_rail_switches_views...`, `Tabs...Tabs_and_locks_of_both_panes...`, `ContextMenu...The_menu_is_edited_inside_the_menu...`, `The_menu_opens_with_its_corner_at_the_point...`, `The_first_menu_of_a_file...`, `The_keyboards_menu_for_a_row_out_of_view...`. I did not look at them. Two suites at once is more load than the creator's machine had.
   - The 189 `wait:` steps I did not convert.
   - `Alone.Opening_the_menu_over_100000_selected_rows_adds_no_slow_frame` has the same exposure as the edit-mode frame test.
   - The same "assert right after the log line" pattern is in the other three compact tests (topmost and size read straight after "entered"); they did not fail.
   - The suite leaves a `CabinetOS.exe` window behind in some run under load (one found, started 16:11 by a suite run).
