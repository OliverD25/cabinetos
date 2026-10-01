# The live check on the Omen laptop: the portability pass

Context: the live check with real keys (`ui/livecheck/livecheck.ps1` and
its helpers) must pass on a second machine, the creator's Omen laptop
(`omen`, Windows 11 Pro 24H2, 1920 x 1080 at 125 %, a 144 Hz screen, a GTX
1660 Ti), so the creator's PC stays free. The laptop's run of 2026-10-01
13:21 (`_io/live-check/run-2026-10-01-1321-rd-omen-laptop.txt`, its
screenshots in `_io/live-check/laptop-shots-1321/`) had 97 True and 9
False and died before its end. This report covers the six faults of that
run, what caused each, and what changed.

## Status

Every change is committed and pushed (7bd88ff to e6f6eb3, then the docs
commit). **No laptop run has verified them yet.** The window and the core
of the 13:21 run are still open on the laptop (`CabinetOS.exe` pid 16648
and `cabinetos-core.exe` pid 16764, both started 13:21:29): that run died
at the fixture, before its own close step. A new run deletes its folder,
`%TEMP%\cabinetos-ui-test\live`, before it starts its window, and those
two processes hold files in it (their logs, the WebView2 data, the
terminal's shell, whose current folder is inside it). My request to stop
them was refused by the session's permission check, so I did not touch
them and did not start a run. Once they are closed, one
`remote-livecheck.ps1 -SkipBuilds` run checks all of this.

Laptop run files made in this pass: none yet (see above).

## The faults, their causes and the fixes

### 1. The edge fixture died on the 255-unit name

**Cause.** The laptop has `LongPathsEnabled` 0, the Windows default. The
fixture's `names\` file with a 255-unit name has a full path of about 312
characters under `%TEMP%`. Windows PowerShell's .NET refuses a path of 260
characters or more in that case, so `File.WriteAllText` threw "Could not
find a part of the path" and the run ended at `livecheck.ps1` line 1278.

**Fix** (e35176b). The fixture's `Put` writes through the `\\?\` form
when the path has 248 characters or more; that form works whatever the
setting says. The long path under `long\` already used it. Checked over
SSH on the laptop: the fixed script built the whole fixture into a scratch
folder (the 255-unit name, 347-character long path, case-sensitive folder,
symbolic links), which was then removed. No script reads the long-named
file back; the app under test still lists it in `names\`, and the edge
section counts it among the rows it walks.

### 2. Section 13: six checks of the tree

**The handout's guess was not the cause.** The window's log of the 13:21
run shows that every Ctrl+L opened the address box (a `go.toPath` from a
key) and every typed path arrived through it (a `go.toPath` from the
address box), 2.3 to 2.7 s later. The left pane did reach `rail13`
(10:24:59.004).

**Cause.** Every folder of a run lies under `%TEMP%`, so under
`C:\Users\Omen\AppData`, and a stock Windows hides AppData (attributes
`Hidden, Directory` on the laptop). The tree lists hidden folders only
while `panes.showHidden` is on, which a run leaves off. So the tree could
not open AppData and marked `C:\Users\Omen` instead of `rail13` ("the tree
shows a folder", path `C:\Users\Omen`, at 10:24:58.988 and 10:25:06.956).
Then Down and Enter in the tree went to `C:\Users\Omen\.aitk`, Right,
Right and Enter to `.aitk\evals` (the screenshot `rail13-tree-keys-live.png`),
Backspace to `.aitk`, and Alt+Shift+L could not show `gamma`. The
creator's PC passed only because its AppData has lost the Hidden attribute
(`Directory, Archive, Encrypted` there).

**Fix** (944bc9d). Section 13 writes `panes.showHidden: true` into the
run's configuration before it switches to the rail layout, waits for the
window's "configuration changed" line for it, and takes the setting out
again at its end, so the sections after it see the configuration they
saw before. The checks are the same; the section's reads of "the tree
shows a folder", "the tree drew rows" and "listing shown" now wait for the
line they need (bounded, never shorter than the old sleep) instead of
reading whatever came last after a fixed sleep.

**The waits the handout asked for** (same commit). The window logs no
"address box shown" or "palette shown". It logs "command executed" for
`go.toPath` (from a key) and `palette.show` just before their handlers
open the box, on the same call of the UI thread, so the box has the
keyboard once that line is there. New helpers at the top of the script:
`UiCount`, `WaitUi` (polls the log every 50 ms, bounded, with an optional
test on the new lines, and never returns before a minimum time), `GoPath`
(Ctrl+L, waits for the box, types, Enter, waits for the folder's "listing
shown" or "cannot list a folder" line), `PressUntil`, `OpenPalette` and
`PressToFolder`. They replace the fixed sleeps at all 21 Ctrl+L steps, the
12 palette openings, the find box (three places, "find opened"), Quick Open
(two, "quick open shown"), the terminal ("a page has the keyboard" for the
terminal, up to 15 s), the Search and Explorer views (their
`view.showSearch` and `view.showExplorer` lines), the theme picker (four
places, its `list_themes` reply), the Markdown Preview (two, "tool
ready"), the search's results in section 13, and Ctrl+Right in the keys
section. Sleeps that are rhythm (between the two keys of a chord, after a
plain arrow key) stay.

### 3. Commander Compact: "no scrollable list found"

**Cause.** Not UI Automation and not the 125 % scale: the folder was not
there. The step types `%TEMP%\cabinetos-bench\100000`, and the window said
"Cannot open …\cabinetos-bench\100000: it does not exist" (10:24:08.485).
The laptop has no `%TEMP%\cabinetos-bench` at all: `cargo bench` makes it,
and the laptop has no Rust.

**Fix** (474ee28), shared with fault 6 below. `ui/livecheck/bench-folders.ps1`
makes the bench's three folders (1,000, 10,000 and 100,000 empty files)
with the bench's own names and `.complete` markers; a folder with its
marker is left alone, and nothing is removed. Its names were compared with
this PC's folder made by `cargo bench`: 100,000 of 100,000 equal.
`remote-livecheck.ps1` runs it on the machine before each run.
`livecheck.ps1` now stops before the window starts when
`cabinetos-bench\100000.complete` is missing, and a new check line says
whether the PageDown hold runs in the 100,000-entry folder. Whether the
list lookup then holds at 125 % is for the first laptop run to show; the
lookup itself was not changed.

### 4. Section 14: the row drag did not start

**Cause.** The laptop's window is 1520 x 828 pixels (not maximized), at
125 %. The drag pressed at 17 % across and 14.9 % down the window: x 258,
y 123, which is in the sidebar, beside "Omen" under Pinned. The row a.txt
was at about x 350 to 550, y 192 (the screenshot `drag14-page-live.png`).
On the creator's larger window the same fractions land on the row. The
scale and the drag's timing were not the problem.

**Fix** (e6f6eb3). The row is found by UI Automation, by its name a.txt,
the leftmost element of that name on the screen; the press is at its
middle (at most 40 px in). The old fractions stay as the fallback, and the
output says which one was used. The drop point (72 % across, half way
down) lies in the right pane's page on both windows and stayed. Ctrl+K V
before it now waits for the preview's "tool ready" line.

### 5. Section 14 "ask": WAITING

**Cause.** The laptop builds nothing, so `sdk\extensions\agent\plugin\plugin.wasm`
is not there.

**Fix** (74cec53). `remote-livecheck.ps1` copies
`sdk\fixtures\plugins\agent\plugin.wasm` into that place when it is empty
or the fixture is newer. The fixture satisfies the section:
`build-extensions.ps1` copies every build of the plugin there, with a
`plugin.json` identical to the extension's own (compared byte for byte),
and the fake provider the section uses exists since the plugin's first
commit (21d414a). The committed fixture is a newer build than this PC's
own `plugin.wasm` of 2026-09-30 (it includes the watch-folders commit,
aaa1fce).

### 6. The scroll goal on the laptop

**Cause.** The 13:21 numbers ("709 frames in 5 s, 2 over 20 ms, 1 over
33 ms, worst 89.3 ms") were not measured in the 100,000-entry folder. The
step typed `%TEMP%\cabinetos-bench`, the window said it does not exist
(10:21:49.518), and Down, Down, Enter then opened `C:\Users\Omen\.cache`,
a folder of one entry (10:21:51.144). PageDown was held there. The one
frame over 33 ms (10:21:55.987, a gap of 89.3 ms) had no work on the UI
thread (`work_ms` 0, `busy_ms` 0, no garbage collection): a pause in
presenting frames, not the window's work, in a folder where nothing
scrolled. It says nothing about the scroll goal.

**Fix**: fault 3's. The goal was not changed. The real number comes with
the first laptop run that has the bench folder.

## Decisions

- **Section 13 turns `panes.showHidden` on for its own time** — because the
  run's folders are under the hidden AppData on a stock Windows and the
  tree, by its documented rule, lists hidden folders only with that
  setting; the checks stay as they were, and the setting is taken out at
  the section's end — undo: remove the two `SetPanesConfig` steps of
  section 13 (944bc9d).
- **A wait never ends sooner than the sleep it replaced** — because this
  PC passed with those sleeps and cannot be run here now; where the line
  comes at once, the timing is as before — undo: lower the `$minMs`
  arguments.
- **The address box and the palette are ready at their "command executed"
  line** — because the window logs no "shown" line for them and its
  handler opens them right after that line, on the same UI-thread call —
  undo: none needed; a later "shown" line in the window can replace the
  pattern.
- **The bench folder is made by a script, with the bench's exact names** —
  because the laptop has no Rust and the scroll numbers must be comparable
  between the machines — undo: delete `ui/livecheck/bench-folders.ps1` and
  its step in `remote-livecheck.ps1`.
- **`livecheck.ps1` stops when the bench folder is missing** — because
  without it the hold measures some other folder and the scroll goal line
  is meaningless — undo: remove the STOP line near the top.
- **The Agent plugin is copied when missing or older than the fixture** —
  because the laptop cannot build it and a stale copy would test an old
  plugin — undo: remove the copy step in `remote-livecheck.ps1`.
- **The drag finds its row by UI Automation, the old fractions as fallback**
  — because fractions of the window depend on its size — undo: revert
  e6f6eb3.
- **Each laptop run follows a push to main** — because the laptop's clone
  must only fast-forward; a commit sent before a rebase would leave the
  clone off main — undo: none needed.
- **The window and core left by the 13:21 run were not stopped** — because
  the session's permission check refused it — undo: none; they are still
  open on the laptop.

## What does not hold on the laptop by nature

Nothing found so far: every fault of the 13:21 run came from the check or
its fixtures, not from the machine. This list is to be completed by the
first verified run (the scroll goal at 144 Hz on the GTX 1660 Ti, and the
Commander Compact throw at 125 %).

## What the check now tolerates between the two machines

| Difference | The creator's PC | The Omen laptop | How the check copes |
|---|---|---|---|
| Long paths in Windows | `LongPathsEnabled` 1 | 0 (the default) | the fixture writes the long name through `\\?\` |
| AppData | not hidden | hidden (the default) | section 13 shows hidden entries while it runs |
| The bench folder | made by `cargo bench` | no Rust | `bench-folders.ps1`, run by `remote-livecheck.ps1`; a STOP when it is missing |
| The Agent plugin | built by `build-extensions.ps1` | nothing built | the committed fixture copied into place |
| The window's size | larger | 1520 x 828 at 125 % | the dragged row is found by UI Automation |
| Speed of the window's answers | fast | not slower in this run, but untested | the keys wait for the window's log lines |

## Seen on the way, not changed

- **Enter in the folder tree runs `go.toPath` twice**, 1 ms apart, on both
  machines (laptop 10:25:08.147 and .148; this PC's run of 12:40,
  09:44:31.919 and .920). It looks harmless (the same folder twice), but a
  key that runs its command twice is the kind of fault the keys audit
  hunted.
- **With `panes.showHidden` off, the tree cannot follow the active pane
  into a folder under a hidden one**, and it marks the nearest folder it
  can show (`C:\Users\Omen`) without saying so. On a stock Windows that is
  everything under `%TEMP%` and `%APPDATA%`. Whether the tree should open
  a hidden folder on the way to the active one is a product question.
- **A live check that dies on an error leaves its window and core open**
  (`livecheck.ps1` closes the window at its STOP lines and at its end, not
  when a command throws). That is what blocks the next laptop run now. A
  `trap` that closes the run's own window would prevent it.
- The handout's line for commits named Claude Fable 5.1; the commits name
  the model that wrote them, Claude Opus 5.5.
