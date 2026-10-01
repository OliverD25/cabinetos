# The live check on the Omen laptop: the portability pass

Context: the live check with real keys (`ui/livecheck/livecheck.ps1` and
its helpers) must pass on a second machine, the creator's Omen laptop
(`omen`: Windows 11 Pro 24H2, a Ryzen 7 4800H, a 1920 x 1080 screen at
125 % and 144 Hz), so the creator's PC stays free. The laptop's run of
2026-10-01 13:21 (`_io/live-check/run-2026-10-01-1321-rd-omen-laptop.txt`,
its screenshots in `_io/live-check/laptop-shots-1321/`) had 97 True and
9 False and died before its end. This report covers the six faults of
that run, what caused each, what changed, and the runs after the changes.

## The runs

| Run (in `_io/live-check/`) | Code on the laptop | Builds | True | False | Scroll goal | Exit |
|---|---|---|---|---|---|---|
| `run-2026-10-01-1321-rd-omen-laptop.txt` (before) | 4ade5a7 | window 12:35, core 11:27 | 97 | 9 | not measured: the hold ran in a folder of one entry; the run died at the edge fixture | died |
| `run-2026-10-01-1424-rd-omen-laptop.txt` (first after) | 4bf1f1f | window 14:23, core 13:57 | 209 | 0 | not met: 341 frames in 5 s, 38 over 20 ms (11.1 %), 27 over 33 ms, worst 103.6 ms | 1 (`-Strict`, the goal) |
| `run-2026-10-01-1434-rd-omen-laptop.txt` (last) | 72f6b3d (with the trap and `-Virtual`) | window 14:23, core 13:57 | 209 | 0 | not met: 291 frames in 5 s, 47 over 20 ms (16.2 %), 31 over 33 ms, worst 109.5 ms | 1 (`-Strict`, the goal) |

Every section ran to its end in both runs after the changes, the scroll
goal line printed, and no check answered False. The exit code 1 is
`-Strict` judging the scroll goal, which does not hold on this machine
(fault 6). The shot of the scroll bar's throw in Commander Compact
(`compact-scrollbar-drag-live.png`, run of 14:24) was looked at: the list
is at its last rows, and every row has its name and size, with no empty
band.

## The faults, their causes and the fixes

### 1. The edge fixture died on the 255-unit name

**Cause.** The laptop has `LongPathsEnabled` 0, the Windows default. The
fixture's `names\` file with a 255-unit name has a full path of about 312
characters under `%TEMP%`. Windows PowerShell's .NET refuses a path of 260
characters or more in that case, so `File.WriteAllText` threw "Could not
find a part of the path" and the run ended at `livecheck.ps1` line 1278.

**Fix** (e35176b). The fixture's `Put` writes through the `\\?\` form
when the path has 248 characters or more; that form works whatever the
setting says. The long path under `long\` already used it. No script
reads the long-named file back; the app under test lists it in `names\`,
and the edge section walks past it to the Ukrainian report. Since then
the fixture builds on the laptop, and every edge check is True.

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
reading whatever came last after a fixed sleep. All 41 checks of the
section are True on the laptop.

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
plain arrow key) stay. No wait ran out in the runs after the change: no
"did not report itself" or "logged no listing" line.

### 3. Commander Compact: "no scrollable list found"

**Cause.** Not UI Automation and not the 125 % scale: the folder was not
there. The step types `%TEMP%\cabinetos-bench\100000`, and the window said
"Cannot open …\cabinetos-bench\100000: it does not exist" (10:24:08.485).
The laptop had no `%TEMP%\cabinetos-bench` at all: `cargo bench` makes
it, and the laptop has no Rust.

**Fix** (474ee28), shared with fault 6. `ui/livecheck/bench-folders.ps1`
makes the bench's three folders (1,000, 10,000 and 100,000 empty files)
with the bench's own names and `.complete` markers; a folder with its
marker is left alone, and nothing is removed. Its names were compared with
this PC's folder made by `cargo bench`: 100,000 of 100,000 equal. On the
laptop it took 49 s for the 100,000 files. `remote-livecheck.ps1` runs it
before each run. `livecheck.ps1` stops before the window starts when
`cabinetos-bench\100000.complete` is missing, and a new check says whether
the PageDown hold runs in that folder (True on the laptop: 100,000
entries). With the folder there, the list lookup held at 125 % unchanged.
The thumb itself is not in the automation tree on either machine (this
PC's run of 12:40 said the same at 150 %), so the throw starts at the
estimated point; it took the list from 0 % to 100 %.

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
output says which one was used. On the laptop UI Automation found it (a
Text element; the press at 603,284), the drag started, and the drop
reached the page with one path. The drop point (72 % across, half way
down) lies in the right pane's page on both windows and stayed. Ctrl+K V
before it now waits for the preview's "tool ready" line.

### 5. Section 14 "ask": WAITING

**Cause.** The laptop builds nothing, so `sdk\extensions\agent\plugin\plugin.wasm`
was not there.

**Fix** (74cec53). `remote-livecheck.ps1` copies
`sdk\fixtures\plugins\agent\plugin.wasm` into that place when it is empty
or the fixture is newer. The fixture satisfies the section:
`build-extensions.ps1` copies every build of the plugin there, with a
`plugin.json` identical to the extension's own (compared byte for byte),
and the fake provider the section uses exists since the plugin's first
commit (21d414a). The committed fixture is a newer build than this PC's
own `plugin.wasm` of 2026-09-30 (it includes the watch-folders commit,
aaa1fce). On the laptop all eight "ask" checks are True: the card, the
install, the prompt, the preview with three rows, the renames on disk.

### 6. The scroll goal on the laptop

**The 13:21 numbers were void.** "709 frames in 5 s, 2 over 20 ms, 1 over
33 ms, worst 89.3 ms" came from the wrong folder: the step typed
`%TEMP%\cabinetos-bench`, the window said it does not exist
(10:21:49.518), and Down, Down, Enter opened `C:\Users\Omen\.cache`, a
folder of one entry (10:21:51.144). The one slow frame there had no work
on the UI thread.

**The real numbers, in the 100,000-entry folder** (run of 14:24): 341
frames in 5 s, 38 over 20 ms (11.1 %), 27 over 33 ms, worst 103.6 ms;
the machine's CPU 5.4 %, the window's 2.7 %. **The goal does not hold on
this machine, and it is not a one-off or the first frame of the hold.**
The window's log has 27 "slow frame" lines in those seconds, and all 27
look the same: a gap of 77 to 104 ms (median 91 ms) between two frames,
of which the UI thread worked 7 to 10 ms (median 8.4 ms), with no garbage
collection. They come about every 150 ms from the first second to the
last: each page of new rows reaches the screen about 90 ms after the UI
thread finished it. The UI thread's work per second is the same as on
this PC (217 to 252 ms against 234 to 276 ms in this PC's run of 12:40,
where every frame came within 17.4 ms at 60 Hz). So the time goes after
the UI thread: in drawing and presenting the frame. The run of 14:34 shows
the same: 31 frames over 33 ms, gaps of median 90.9 ms, the UI thread's
work median 8 ms, one with a garbage collection.

On this laptop that is the integrated graphics. The 144 Hz screen is
driven by the Ryzen's AMD Radeon graphics; the GTX 1660 Ti has no display
of its own, and CabinetOS has no GPU preference in Windows' graphics
settings, so it runs on the integrated GPU. The frame numbers of a laptop
run are those of the Radeon, not of the GTX 1660 Ti. The goal was not
changed. What the creator may want to decide: whether the scroll goal
must hold on an integrated GPU (then this is a performance fault of the
window's drawing, Article 1, worth its own investigation), and whether to
try the run once with CabinetOS set to "High performance" in Windows'
graphics settings on the laptop (a setting of the creator's machine, so
not changed here).

## The trap and -Virtual (the coordinator's additions)

- **d0279e3**: a `trap` in `livecheck.ps1`. When a command throws, the run
  writes a STOP line (DONE.md shows it), closes its own window as the end
  of the run does, ends it after 8 s if it is still there, ends a core of
  that window still running a second later, and exits 1. With the
  script's `ErrorActionPreference` of Stop the same errors end the run as
  before; only the cleanup is new. Tested with a throwing function in both
  PowerShells (the STOP line, exit code 1); a real throw did not happen in
  the laptop runs.
- **72f6b3d**: `-Virtual`, for the VirtualBox VM. `run-livecheck.ps1
  -Virtual` passes it on. The scroll goal line, the only check that judges
  frame times, then prints its numbers and answers "not measured in a VM";
  `-Strict` does not judge it, and DONE.md counts such lines apart,
  neither True nor False. Every other check stays as strict.
  Documented in docs/ui.md next to the other-machine section. Not run in a
  VM here.

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
- **The scroll goal stays as it is; the laptop's run exits 1 under
  `-Strict`** — because the handout forbids changing the goal and the
  evidence points at the machine's graphics, which is the creator's
  question — undo: none.
- **Each laptop run follows a push to main** — because the laptop's clone
  must only fast-forward; a commit sent before a rebase would leave the
  clone off main — undo: none needed.
- **The window and core left by the 13:21 run were not stopped by me** —
  because the session's permission check refused it; the coordinator
  closed them — undo: none.

## What does not hold on the laptop by nature

- **The scroll goal** (fault 6): the frames of a page change take about
  90 ms to reach the screen on the integrated Radeon at 144 Hz, while the
  UI thread's share is about 8 ms.
- **Symbolic links in the edge fixture**: the task's session may not make
  them ("Administrator privilege required"; Windows' Developer Mode is
  off there), so the fixture makes the junctions only, and says so. No
  check needs a symbolic link; this PC's run of 12:40 said the same.

## What the check now tolerates between the two machines

| Difference | The creator's PC | The Omen laptop | How the check copes |
|---|---|---|---|
| Long paths in Windows | `LongPathsEnabled` 1 | 0 (the default) | the fixture writes the long name through `\\?\` |
| AppData | not hidden | hidden (the default) | section 13 shows hidden entries while it runs |
| The bench folder | made by `cargo bench` | no Rust | `bench-folders.ps1`, run by `remote-livecheck.ps1`; a STOP when it is missing |
| The Agent plugin | built by `build-extensions.ps1` | nothing built | the committed fixture copied into place |
| The window's size and scale | larger, 150 % | 1520 x 828 at 125 % | the dragged row is found by UI Automation |
| Speed of the window's answers | fast | as fast in these runs | the keys wait for the window's log lines, never shorter than the old sleeps |
| Graphics and refresh rate | 60 Hz | integrated Radeon, 144 Hz | nothing: the scroll goal judges it, and it fails there |

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
- The handout's line for commits named Claude Fable 5.1; the commits name
  the model that wrote them, Claude Opus 5.5.
