# The scroll-gap investigation, 2026-09-30

Item 5 of the shell agent's night (agent a019b1b2d5c91c57a), measured from
01:40 to 05:50 on the development PC. The goal of Phase 5: while PageDown
is held on the 100,000-entry bench folder, no frame takes more than 33 ms,
and fewer than 5 % of the frames take more than 20 ms.

**Result.** The cause was when the key's work ran, not how much work there
was. Three changes, with nothing different on screen, took the gaps over
20 ms from 27 to 32 % to 0 % in the default theme with real keys: 1,207
frames in a 20 s hold, none over 20 ms, the worst 17.8 ms. They are in the
build (commit "ui: a held cursor key no longer makes frames late"). Two
things are still open: a single induced garbage collection of 20 to 30 ms
that sometimes lands near the start or the end of a hold, and the Commander
Compact theme, at 11.6 to 18.8 % over 20 ms.

## How it was measured

- **Real keys.** [scroll-keys.ps1](../../../ui/livecheck/scroll-keys.ps1)
  starts a fresh window on the bench folder and presses PageDown 30 times
  a second for 5 s with `SendInput`, as the live check does, with the
  sleeps at 1 ms timer resolution. It reads the window's `frame stats`
  lines the way `livecheck.ps1` does, so its table leaves out up to the
  first second of the hold and takes in up to 1 s after the last press.
  It lists every frame of 33 ms or more from the first press to 1 s after
  the last.
- **Outside the window**: the CPU time of each thread of the window's
  process, with the thread names Windows keeps; the desktop window
  manager's CPU (`\Process(dwm)`); the GPU's 3D engine (`\GPU Engine`).
- **Inside the window**: the frame monitor's parts (docs/ui.md,
  "Scrolling"), and, new tonight, the garbage collector's pause and
  collections per frame and per second. For some runs an experiment-only
  listener logged each collection's reason from .NET's own event source.
- **The display awake at 59 Hz.** A script asked Windows to keep the
  display on (`SetThreadExecutionState`, the call a video player makes);
  no setting was changed. Asleep, Windows paces frames at about 33 a
  second, and every gap counts as over 20 ms.
- **One change at a time**, each behind an environment variable in one
  build, so a run with the variable and a run without it compare the
  same code. The variables are not in the build; the patch is kept
  outside the repository, in `_io\scroll-experiments-2026-09-30.patch`.
- The machine was at about 10 to 13 % CPU. Release builds of the window and the
  core, main at 1a05a5c plus the change.
- **Not possible**: an ETW trace (Windows' own event recording, which
  would show the time inside WinUI by function). `wpr` needs an elevated
  prompt: "Failed to enable the policy to profile system performance",
  error 0xc5583000. The display's refresh rate was not changed either: it
  is a system setting.

## What costs what

Before the change, 5 s holds (runs B1 to B6, seven runs):

| Per second of the hold | ms |
|---|---|
| UI-thread work in all | 363 to 430 |
| of it WinUI's frames (layout and drawing inside a frame) | 220 to 261 |
| of it the rows' layout run outside a frame | about 150 |
| the rows' measure pass (inside and outside frames) | 133 to 159 |
| binding rows to entries | 16 to 21 |
| selection marks, status bar | about 2.5 and 1.7 |

A page of 30 new rows costs 13 to 15 ms of UI-thread work. Only 2 to 4
frames in 5 s had more than 16.7 ms of work, yet 27 to 32 % of the gaps
were over 20 ms.

CPU time per thread over the hold, ms per second:

| Thread | Before | After |
|---|---|---|
| the UI thread | 291 to 338 | 263 to 331 |
| .NET's tiered compilation (the runtime making faster code for the methods it runs often) | 156 to 188 in the first 5 s | 43 over a 20 s hold |
| WinUI's composition thread in the process ("DWM Compositor Thread") | 59 to 78 | 70 to 100 |

The desktop window manager used 7 to 12 % of one processor, and the GPU's
3D engine 0.2 to 0.4 % for this window, 1.2 to 1.9 % for all processes.
Neither is a limit.

**Garbage collections.** Every collection during a hold was a full
(generation 2) one, induced: reason 7 (`InducedNotForced`) or 1
(`Induced`). The window never asks for one, so WinUI asked .NET for
them. Some ran blocking and some in the background; each paused the UI
thread for 17 to 25 ms (up to 34 ms in Commander Compact), with a
managed heap of only 6 to 8 MB. With the old binding they came every 2
to 3 s of a held PageDown. The collector's `SustainedLowLatency` mode
made no difference: a collection that is asked for still runs.

## The cause

A key message arrives at any moment between two frames. The pane moved
the cursor and scrolled at once, and WinUI laid out the new page's rows
right after: 13 to 15 ms, outside a frame. When that began late in the
16.7 ms between two frames, the next frame waited for it, a gap of 20 to
30 ms. The snapshot aid presses PageDown at the start of a frame, so the
same page fits: it showed 1.4 to 3.1 % over 20 ms where real keys showed
29 %. With the key's move made at the start of the next frame, the UI
work inside frames rose to match the work in all (234 of 238 ms a
second), and the gaps went.

## The experiments

"Old path": the key moved the cursor at once, as before. "The three": the
key waits for the next frame, rows are bound in `ElementPrepared`, rows
are made ahead. Each 5 s at 30 presses a second unless noted.

| Run | Change | Gaps over 20 ms | Over 33 ms | Worst | UI work a second | Candidate |
|---|---|---|---|---|---|---|
| B1 to B6 (7) | none: the build before | 27.2 to 32.3 % | 1 to 2 | 44.7 to 50.9 ms | 363 to 430 ms | the baseline |
| scroll-bench (3), B4 | none, the snapshot aid instead of keys | 1.4 to 3.1 % | 1 to 3 | 47.2 to 63.4 ms | 397 to 434 ms | shows why keys are needed |
| E1a to E1c (5) | the key's move at the next frame | 0.3 to 1.0 % | 1 | 34.1 to 41.3 ms | 238 to 276 ms | yes: the cause |
| E2c | E1 and the `SustainedLowLatency` mode | 0.7 % | 1 | 36.8 ms | 244 ms | no |
| E3c | rows made ahead, alone | 29.2 % | 1 | 51.0 ms | 379 ms | only with E1 |
| E4b | bound in `ElementPrepared`, alone | 29.0 % | 0 | 29.0 ms | 362 ms | only with E1 |
| E3b, 10 s | E1 and rows made ahead | 1.2 % | 4 | 46.2 ms | 270 ms | a collection every 2 to 3 s |
| E4c, 10 s | E1 and bound in `ElementPrepared` | 0.0 % | 0 | 17.7 ms | 257 ms | the first press 64.5 ms |
| E4a, 10 s; T0 | the three | 0.0 % | 0 | 17.6, 17.9 ms | 249, 229 ms | yes |
| X1a | overscan 0.5 screens (WinUI's default: 2), old path | 29.1 % | 2 | 42.0 ms | 355 ms | no |
| X1b | overscan 0.5, with the three | 0.0 % | 0 | 17.3 ms | 211 ms (−8 %) | for Compact (X1d) |
| X1c | overscan 0, with the three | 0.0 % | 0 | 17.4 ms | 214 ms (−7 %) | no gain over 0.5 |
| X2a | plain rows (no icon, no type column), old path | 28.4 % | 2 | 40.6 ms | 310 ms | no |
| X2b | no icons, with the three | 0.0 % | 0 | 17.5 ms | 230 ms (0 %) | no |
| X2c | no type column, with the three | 0.0 % | 0 | 17.4 ms | 192 ms (−16 %) | no: visible |
| X2d | plain rows, with the three | 0.0 % | 0 | 17.5 ms | 189 ms (−17 %) | no: visible |
| X3a | a layout of fixed-height rows, old path | 25.9 % | 19 | 87.5 ms | 847 ms | no |
| X3b | fixed-height rows, with the three | 9.6 % | 0 | 30.7 ms | 347 ms | no |
| X5a | Commander Compact, old path | 28.4 % | 2 | 64.3 ms | 588 ms | |
| X5b, F6, F6b (5) | Commander Compact, with the three | 11.6 to 18.8 % | 0 | 22.7 to 23.1 ms | 351 to 365 ms | goal not met |
| X1d (3) | Commander Compact, overscan 0.5, with the three | 1.0 to 6.3 % | 0 to 1 | 21.1 to 33.4 ms | 323 to 343 ms | the next candidate |
| G1 (3) | one full collection while idle, after the rows ahead | 0.0 % | 0 | 17.6 to 18.0 ms | 225 to 231 ms | unclear |
| F1 (3), F4, F5, F7, F8 | the committed build (F4 the live check's rhythm, F5 10 s, F7 20 s) | 0.0 % | 0 | 17.5 to 17.8 ms | 218 to 258 ms | committed |
| H1 | the committed build, heavy logging on | 0.0 % | 0 | 17.6 ms | 228 ms | |

Not run:

- **A plain `ListView` with `ItemsStackPanel`.** The cause turned out to be
  the time the key's work ran, not the control, and the goal was met
  without it. Swapping the control means rewriting the pane's selection,
  keys, recycling and row binding, which is the rewrite this item rules
  out.
- **Another refresh rate.** The display runs at 59 Hz; its adapter allows
  up to 143 Hz. Changing it is a system setting and was not done.

## The whole live check

Two runs of `run-livecheck.ps1` with the change, both 5 s holds inside the
full script:

- run 2026-09-30-item5-a: "scroll goal (no frame over 33 ms, under 5 % over
  20 ms) met: no; 301 frames in 5 s, 1 over 20 ms (0.3 %), 1 over 33 ms,
  worst 47.9 ms". The one frame came after the last press, with a
  collector's pause of 25 ms. One other check answered False, section
  13's tree step, and passed in the next run.
- run 2026-09-30-item5-b: "met: no; 302 frames in 5 s, 1 over 20 ms
  (0.3 %), 1 over 33 ms, worst 36.7 ms". Every other check True. The
  frame: a gap of 36.7 ms 350 ms after the last press, no UI work in it,
  a blocking full collection of 20 ms (heap 7.8 MB).

In both, the frame was outside the hold. It counts because the table ends
with the per-second line that ends up to 1 s after the last press. The
comment in `livecheck.ps1` says the table holds "the window's per-second
lines that ended while the key was held"; the filter takes in one more.

## What remains

- **One induced full collection of 20 to 30 ms, now and then**, near the
  start or the end of a hold: in 5 of 8 probe runs of the committed build,
  one frame of 36 to 42 ms in the first second of the hold (before the
  seconds the table counts); in both live-check runs, one frame after the
  last press. In a steady hold there were none in 20 s. Which count makes
  WinUI ask for them is not known; an ETW trace from an elevated prompt,
  with the .NET collector's events and their call stacks, would show it.
- **Commander Compact** (20 px rows, 48 a page): a page takes more than
  16.7 ms in more than half the frames that show one. Its first PageDown
  still takes one frame of 60 to 77 ms, 10 to 42 ms of it the rows' own
  measure; why the rows made ahead do not cover it there is not known
  yet.
- **The cursor keys' contract.** Code that reads a pane's cursor must make
  the waiting keys first (`FilePane.ApplyCursorKeys`). Today that is the
  pane's other keys, a click, a command, a plugin command's arguments,
  the quick search's typed letters and a new model. A new path that reads
  the cursor, and forgets, would act one frame behind the keys.

## Recommendation

1. **Keep the three changes** (in the build). Cost to the design: none on
   screen. A cursor key's move is made at the start of the next frame, the
   same frame that would have shown it; the cost is the contract above.
2. **For Commander Compact, an overscan of 0.5 screens** next
   (`Repeater.VerticalCacheLength = 0.5`): 1.0 to 6.3 % over 20 ms in three
   runs, and 8 % less work in the default theme. Check first that a fast
   drag of the scroll bar or a fast mouse wheel shows no empty rows; that
   was not checked tonight, so it is not in the build.
3. **Decide what the live check's table should cover**: the seconds of the
   hold only, or up to 1 s after it as now. That is the creator's decision,
   because it changes the measure the goal is judged by.
4. Not recommended: the fixed-height layout (worse), rows without the type
   column or the icon (visible, and not needed for the goal), a
   `ListView` rewrite.

## Decisions made alone

Each as what — because — undo.

- The three changes are committed — together they meet the goal with real
  keys and nothing on screen changes — revert the commit "ui: a held
  cursor key no longer makes frames late".
- Anything that reads the cursor makes the waiting keys first (the list
  above) — a command or a click must act where the keys left the cursor,
  in the order they were pressed — part of the same commit.
- Rows made ahead: two pages and four rows, in 4 ms slices at the
  dispatcher's low priority — the first PageDown needs about one page
  more than the view shows, and short slices let input and frames go
  first — change `ahead` in `FilePane.OnElementPrepared` or `SliceMs` in
  `RowFactory`.
- The frame monitor keeps the collector's fields, and logs a slow frame
  whenever frame stats are on (to the heavy log in heavy mode) — that is
  how the collections were found — revert the `FrameMonitor.cs` hunk.
- `scroll-bench.ps1` shows only the run's own slow frames — the monitor
  now logs them outside runs too — revert that hunk.
- `ui/livecheck/scroll-keys.ps1` is committed — the numbers here need real
  keys, and the whole live check takes 5 minutes for one scroll line —
  delete the file.
- The overscan of 0.5 is not committed — the default theme is already at
  0 %, Compact stays near the goal's edge, and fast scroll-bar drags were
  not checked — set `Repeater.VerticalCacheLength = 0.5` in `FilePane`'s
  constructor.
- No `ListView` experiment, no refresh-rate change, no ETW trace — the
  reasons above — nothing to undo.
- The live check's table was not changed — it is the measure the goal is
  judged by — nothing to undo.
- This report is dated 2026-09-30, not 2026-09-29 as the handout had it —
  the planning session's message named this date, the day of the work —
  move the file.
- `docs/diagnostics.md` names the new fields and the `slow frame` line in
  the heavy log — the change put them there — revert that hunk.
