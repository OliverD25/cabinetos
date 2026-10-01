# The live check's `-Panel` switch (2026-10-01, evening)

Context: the live check's scroll goal on the creator's Omen laptop. The creator
chose option (b) of [scroll-gaps-laptop.md](scroll-gaps-laptop.md): on a laptop
panel the goal is judged by the frames' own UI-thread work, and the gap numbers
are still printed. This report says what was built, how it was proven on the
laptop, and what was decided without the creator. Written by the Sonnet agent
in the worktree branch `worktree-agent-ad703d6c6a23eb660`, on the main PC, while
the creator worked at it (nothing here opened a window on this PC).

## Summary

- `livecheck.ps1 -Panel` (and `run-livecheck.ps1 -Panel`, which passes it on).
  The scroll goal line prints the gap numbers as before and answers "not judged
  on a panel". A second line, "panel goal (...) met: yes|no", judges the same two
  limits on the frames' UI-thread work. `-Strict` judges that line.
- The window's per-second `frame stats` line got two new fields,
  `busy_over_20ms` and `busy_over_33ms`, with a unit test. The field names are
  not the ones in the brief: see "Decided without you", number 1.
- The laptop's wrapper `run-livecheck-laptop.ps1` passes `-Panel` now.
- Two live checks on the laptop with the new window: panel goal met in both,
  exit code 0 in both. The first run had one False check (a key hand-over in
  section 11a that this change does not touch); the second run had none and
  matches the last run on main line for line.

## Commits (branch `worktree-agent-ad703d6c6a23eb660`, on top of 8310d21)

| Commit | Subject |
|---|---|
| 9dbd2ab | frame stats: count the frames whose UI-thread work passed 20 and 33 ms |
| 2502ae0 | live check: -Panel, so a laptop panel's frame gaps are reported and its UI work is judged |
| 651d2da | docs: -Panel in the live check section and the Scrolling section |

This report and its README row are the fourth commit.

## The change, per file

| File | What changed |
|---|---|
| `ui/CabinetOS.Core/Diagnostics/FrameTable.cs` | `FrameSummary` has two new counts, `BusyOver20` and `BusyOver33`. `FrameTable.Summarize` fills them from `FrameTable.Busy` (the frame's UI-thread work, the measure that already gave `BusyOver16`). The limits are the gaps' own: over 20 ms is `> SlowMs` (20), over 33 ms is `>= DroppedMs` (33.4). No new measure. |
| `ui/CabinetOS/Services/FrameMonitor.cs` | The per-second `frame stats` line carries `busy_over_20ms` and `busy_over_33ms` after `busy_over_16ms`. The `scroll run` line is not changed: the live check reads the per-second lines, not that one. |
| `ui/CabinetOS.Tests/FrameTableTests.cs` | One new test, `Frames_whose_ui_thread_work_passes_20_and_33_ms_are_counted_with_the_limits_of_the_gaps`. It has two frames with an 80 ms gap and 8 ms of work (the laptop's case: the gaps count them, the work counts does not), and frames at exactly 20 ms, 33.3 ms, 33.4 ms and 50 ms. |
| `ui/livecheck/livecheck.ps1` | `[switch]$Panel`. The scroll goal line is unchanged except for the answer: "not measured in a VM" (with `-Virtual`, which wins), "not judged on a panel" (with `-Panel`), else yes or no. With `-Panel` (and no `-Virtual`) a line follows: `panel goal (no frame with UI work over 33 ms, under 5 % with UI work over 20 ms) met: yes\|no; <frames> frames in <s> s, <n> with UI work over 20 ms (<share> %), <n> over 33 ms, <n> over 16.7 ms`. `$script:scrollGoal` is the panel goal in that case, so `-Strict` judges it, and its message says "panel goal". The frame table is as it was. |
| `ui/livecheck/run-livecheck.ps1` | `[switch]$Panel`, passed on to `livecheck.ps1` like `-Virtual`. `DONE.md` has two new lines with `-Panel` (not with `-Virtual`): "Judged by the frames' UI work on a panel (-Panel), not by the gaps:" plus the panel goal line, and "Checks not judged on a panel (...; neither True nor False): N". |
| `docs/ui.md` | A paragraph "**A laptop panel**" after the "**A virtual machine**" paragraph. One sentence in the other-machine paragraph and one in the `-Strict` sentence, so they do not contradict it. One sentence in the first paragraph of "Scrolling". |
| `docs/diagnostics.md` | The `frame stats` row lists the two new fields. |
| `C:\Dev\cabinetos\_io\run-livecheck-laptop.ps1` (on the laptop, not in the repository) | `-Panel` added to the one call. See below. |

## The laptop's wrapper

Read with `ssh omen "Get-Content C:\Dev\cabinetos\_io\run-livecheck-laptop.ps1"`,
changed on this PC, copied back with `scp`. Only the last line changed (the file
keeps its CRLF line ends and has no BOM).

Before:

```text
& C:\Dev\cabinetos\cabinetos\ui\livecheck\run-livecheck.ps1 -MinimizeOthers
```

After:

```text
& C:\Dev\cabinetos\cabinetos\ui\livecheck\run-livecheck.ps1 -MinimizeOthers -Panel
```

The original file was copied to this PC's session scratchpad first
(`run-livecheck-laptop.original.ps1`); the line above is all of its change, so
removing ` -Panel` undoes it.

## Proof

| Check | Result |
|---|---|
| `build\check-scripts.ps1` in Windows PowerShell 5.1 and in PowerShell 7.6 | every script parses, exit 0 |
| `dotnet build CabinetOS.sln -warnaserror` | 0 warnings, 0 errors |
| `dotnet test --solution CabinetOS.sln --no-build`, without `CABINETOS_UI_E2E` | 1194 tests: 1136 passed, 0 failed, 58 skipped (the end-to-end tests, which the run does not turn on, and tests that need a core build in the worktree). The new test passes alone. |
| `dotnet build CabinetOS.sln -c Release -warnaserror` | 0 warnings, 0 errors |
| The scroll goal block of `livecheck.ps1`, run on invented per-second lines in both PowerShells | no switch: as before; `-Panel`: "not judged on a panel" and the panel goal line, yes or no by the counts (5.0 % over 20 ms of work is no, one frame over 33 ms of work is no); an old window without the counts: no, with a note; `-Panel -Virtual`: "not measured in a VM", no panel line |
| `run-livecheck.ps1` with its run replaced by a fixed output (a mock) | `-Panel` is passed on; `DONE.md` has the two new lines with `-Panel` only |

### The live check on the laptop

Both runs: the Release window built from this branch (19:51), the main
checkout's release core (13:57, copied into the worktree first), started by
`remote-livecheck.ps1 -Branch worktree-agent-ad703d6c6a23eb660 -Io <_io\live-check>`;
the laptop's task ran the wrapper above, so `-Panel` came from the wrapper.
The baseline is the last laptop run on main, `run-2026-10-01-1434-rd-omen-laptop.txt`.

| Run | Output file | Started | Scroll goal line (gaps, not judged) | Panel goal line | False | Exit code |
|---|---|---|---|---|---|---|
| 1 | `run-2026-10-01-1952-rd-omen-laptop.txt` | 19:52:58 | not judged on a panel; 300 frames in 5 s, 39 over 20 ms (13.0 %), 29 over 33 ms, worst 110.8 ms | met: yes; 300 frames, 1 with UI work over 20 ms (0.3 %), 0 over 33 ms, 5 over 16.7 ms | 1 | 0 |
| 2 | `run-2026-10-01-2002-rd-omen-laptop.txt` | 20:02:16 | not judged on a panel; 255 frames in 5 s, 50 over 20 ms (19.6 %), 33 over 33 ms, worst 119.4 ms | met: yes; 255 frames, 0 with UI work over 20 ms (0.0 %), 0 over 33 ms, 4 over 16.7 ms | 0 | 0 |

The gap numbers are the ones the earlier laptop runs gave (the display path's
80 to 120 ms waits); the panel goal reads the UI work and meets it with room.
The frames with UI work over 16.7 ms were 5 and 4, in the range 1 to 5 the
earlier runs showed.

Run 2's `DONE.md`, as the laptop wrote it:

```text
Started 20:02:16, ended 20:10:03, exit code 0.

- scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: not judged on a panel; 255 frames in 5 s, 50 over 20 ms (19.6 %), 33 over 33 ms, worst 119.4 ms; CPU during the hold: machine 5.9 %, this window 2.9 %, busiest others: powershell 0.3 %, uTorrent 0.1 %, powershell 0.0 %
- No stop line: the script ran to its end.
- Checks that answered False: none
- Judged by the frames' UI work on a panel (-Panel), not by the gaps: panel goal (no frame with UI work over 33 ms, under 5 % with UI work over 20 ms) met: yes; 255 frames in 5 s, 0 with UI work over 20 ms (0.0 %), 0 over 33 ms, 4 over 16.7 ms
- Checks not judged on a panel (they judge frame gaps; neither True nor False): 1
```

Run 2's frame table (UTC seconds of the hold):

```text
| 17:02:43 | 60 | 119.4 ms | 8 | 6 | 1 | 227.6 ms | 98.2 ms |
| 17:02:44 | 50 | 95.2 ms | 11 | 6 | 1 | 227.3 ms | 104.7 ms |
| 17:02:45 | 52 | 95.1 ms | 9 | 7 | 0 | 196.4 ms | 92.9 ms |
| 17:02:46 | 45 | 94.6 ms | 11 | 7 | 1 | 204.9 ms | 97.8 ms |
| 17:02:47 | 48 | 95.5 ms | 11 | 7 | 1 | 180.4 ms | 85.1 ms |
```

#### True and False against the baseline

The brief says the last run on main had "209 True and 0 False". I could not make
that number from the baseline file. Counting it two ways I get 203 lines that end
in `: True` and 210 lines that contain the word True (the second way also counts
`exists: True` and `foreground ok: True`). Both counts and the False count are
below, for each file, by the same two methods:

| Run | Lines ending in `: True` | Lines with the word True | Lines ending in `: False` |
|---|---|---|---|
| baseline, 1434 | 203 | 210 | 0 |
| run 1, 1952 | 202 | 209 | 1 |
| run 2, 2002 | 203 | 210 | 0 |

I also compared the check lines of each run to the baseline's, with the digits
replaced, so times and counts do not count. Run 2 has the same 203 check lines
as the baseline and no difference at all. Run 1 differs in exactly one line:

```text
the terminal's page has the keyboard after Ctrl+Alt+P (hand-overs: none), the preview open in the other pane: False
```

(The baseline has `hand-overs: 1 ... True`.) This is section 11a of the check.
The window's own log on the laptop for that second shows what happened: the
window logged `a page did not get the keyboard; handing it over again` (attempt
1, 160 ms after the path was typed) and no `a page has the keyboard` line after
it, so the check found no hand-over record. The next two checks of the same
section, `Ctrl+Backquote reached the window` and `keys the window had to take for
the page (0 expected): 0`, passed. This is the key hand-over of 2026-09-30
(`PageKeyboard`), not the frame monitor and not the scripts changed here. Run 2
passed it again. It is one timing flake in two runs on the laptop; I did not
look further, because it is outside this task (see "Needs you").

## Decided without you

Each line: what, because, undo.

1. **The new fields are `busy_over_20ms` and `busy_over_33ms`, not `work_over_20ms` and `work_over_33ms`.**
   Because in the window's log `work_ms` is only WinUI's own frame time, and the
   measure the brief asks for (the frame's UI-thread work: the frame time plus the
   layout outside it plus the other parts) is what the code and the log already
   call "busy" (`FrameTable.Busy`, `busy_ms`, `busy_over_16ms`). A field named
   `work_over_20ms` next to `work_ms` would have counted something else than
   `work_ms` sums. Undo: replace `busy_over_20ms` and `busy_over_33ms` by the
   `work_` names in `FrameMonitor.cs`, `livecheck.ps1`, `docs/diagnostics.md` and
   `docs/ui.md` (one search and replace; the C# properties `BusyOver20`,
   `BusyOver33` can stay).
2. **The limits are the gaps' own constants.** Over 20 ms means more than 20 ms
   (`SlowMs`), over 33 ms means 33.4 ms or more (`DroppedMs`), as for the gaps ("the
   same way the gap counts are made"). A frame of 33.3 ms of work is not counted.
   Undo: change the two lambdas in `FrameTable.Summarize`.
3. **Only the per-second `frame stats` line got the fields.** The live check's table
   comes from those lines. The `scroll run` line (snapshot aid) is unchanged.
   Undo: add `busy_over_20ms` and `busy_over_33ms` to `FrameMonitor.EndRun`, if the
   snapshot aid should print them.
4. **A window that logs no such counts cannot meet the panel goal.** The line says
   "no" and names the reason ("an older build"). Because a goal must not pass for
   want of data. Undo: delete `$haveWork` from `$panelGoal` in `livecheck.ps1`.
5. **With `-Panel` the `-Strict` message reads "the panel goal was not met".** Undo:
   restore the old text on the last line of `livecheck.ps1`.
6. **The panel goal line is printed only with `-Panel` and without `-Virtual`.**
   `-Virtual` wins, as the brief says. With both switches `DONE.md` shows only the
   VM lines.
7. **Docs beyond the two places named in the brief:** the `docs/diagnostics.md`
   field list, one sentence in the other-machine paragraph (it said the goal does
   not hold on the laptop and now says how the laptop judges it), and one clause in
   the `-Strict` sentence. Undo: revert those hunks of 651d2da and 9dbd2ab.
8. **A second live check on the laptop.** The first run had one False. I ran the
   check again, unchanged, to see whether it repeats. It did not. Both runs are in
   `_io\live-check`.
9. **A mistaken command, with no effect.** Once I started `remote-livecheck.ps1`
   with another worktree's branch name and cut its output after the first line. I
   checked after: the laptop's clone, its inbox and its task were unchanged.

## Needs you

1. **Merge this branch with a fast-forward, or reset the laptop's clone after.**
   `remote-livecheck.ps1` sends the commits the laptop's clone lacks and runs
   `git pull --ff-only` there. The clone now stands at 651d2da, the head of this
   branch (without this report's commit). If main takes this branch by a
   fast-forward or a merge, the next run from main works. If main takes it by a
   squash or a cherry-pick, the clone is no ancestor of main and the pull fails;
   then run `git -C C:\Dev\cabinetos\cabinetos reset --hard <main>` on the laptop
   (over `ssh omen`). I did not do this: it throws away commits there.
2. **The wrapper line lives only on the laptop.** If the laptop's task is made again
   from another wrapper, add `-Panel` to the call. The repository has no copy of the
   wrapper.
3. **The section 11a flake.** One False in two laptop runs: the terminal's key
   hand-over logged attempt 1 and no confirmation. A coder could look at it with
   `heavy-logging`. It did not stop the run.
4. **`docs/PLAN.md` and its desk card are not updated.** The plan's evening status
   still lists "how the live check judges the scroll goal on a laptop panel" as
   open for the creator. It is decided now (option b, built here). The planning
   session should edit that line and refresh the desk card (CLAUDE.md, "The desk").
5. **Not done, as the scroll-gaps report says:** the two settings tests (the panel
   at 60 Hz, CabinetOS on the GTX). They need your say-so.

## Out of scope, seen on the way

- `scroll-keys.ps1` and `scroll-bench.ps1` print their own tables from the `frame
  stats` lines and do not show the two new fields. Add them there if the panel
  goal should be judged in those tools too.
