# The scroll gaps on the Omen laptop: the display path sleeps between pages (2026-10-01, evening)

Context: the live check's scroll goal ("no frame over 33 ms, under 5 % over
20 ms" while PageDown is held in the 100,000-entry folder) holds on this
PC and fails on the creator's Omen laptop (the portability report, fault 6:
a gap of about 90 ms every 150 ms, with the UI thread's work at 8 ms of
it). The creator asked to go on with it. This is what the evening's runs on
the laptop found, with `ui/livecheck/scroll-keys.ps1` run there through the
new `ui/livecheck/remote-script.ps1` (a script of the repository run in the
laptop's own session, see below). The window was the Release build of
14:23, the core of 13:57, the code at 69d24e7. Every run: a fresh window on
the bench folder, PageDown pressed for 5 s (the laptop managed about 21
presses a second, not 30: 48 ms between presses), `-Fine`.

## The runs

| Run (in `_io/script-runs/`) | What was different | Frames in 5 s | Over 20 ms | Over 33 ms | Worst | UI work over 16.7 ms |
|---|---|---|---|---|---|---|
| 1719 `laptop-baseline` | nothing | 424 | 30 (7.1 %) | 17 | 105.4 ms | 2 |
| 1721 `laptop-no-tiering` | `DOTNET_TieredCompilation=0` | 272 | 47 (17.3 %) | 30 | 107.3 ms | 9 |
| 1723 `laptop-heavy` | heavy logging, 3 s | the chain below | | 20 in the hold | | |
| 1726 `laptop-rate100` | 10 presses a second (9 made) | 426 | 32 (7.5 %) | 20 | 104.3 ms | 1 |
| 1734 `laptop-mouse-list` | the mouse over the list | 225 (4 s) | 37 (16.4 %) | 23 | 106.1 ms | 3 |
| 1735 `laptop-mouse-title` | the mouse over the title bar | 268 | 49 (18.3 %) | 31 | 111.1 ms | 5 |
| 1736 `laptop-busy-screen` | **a second window redrawing 60 times a second in a corner** | **562** | **24 (4.3 %)** | **8** | **70.1 ms** | 1 |
| 1738 `laptop-busy-screen-7ms` | **the second window redrawing every 7 ms** | **637** | **8 (1.3 %)** | **0** | **32 ms** | 1 |

In every run without the busy window: the GPU's 3D engine at 1 to 3 %,
the desktop window manager at 6 to 9 % of one processor, the UI thread's
work 150 to 300 ms a second (the same as on this PC), no garbage
collection in the slow frames. The machine is idle during a gap.

## What the heavy log says (run 1723)

`_io/script-runs/laptop-heavy-logs/heavy-ui.2026-10-01.jsonl`, read with
`scratchpad/heavy-gap.py` of the planning session:

- A gap is a wait, not work. In a 93 ms gap the UI thread works 11 ms;
  every request of the window (`window_state`, the cursor's report to the
  core, 30 in the hold; `describe_entries`, 6) is answered within 1 ms.
- Every second key press waits about 48 ms before the pane acts on it:
  the time from `key pressed` to the status bar's `selection shown` goes
  5, 1, 64, 16, 3, 3, 49, 1, 6, 48, 9 ms. The window applies a cursor key
  at the next frame tick (the change of 2026-09-30), and the ticks stop
  for about 80 ms after a page.
- **The ticks stop with no key at all.** For a whole second before the
  first press (14:23:46, the window idle after its start, no UI work), the
  frames came every 80.5 ms instead of every 7 ms: ten gaps in a row of
  80 to 88 ms, then 144 frames a second again. The window ticks at the
  display's 144 Hz when idle (the frame monitor keeps it ticking), but it
  presents nothing while nothing changes.

So the gaps are not the window's drawing. They are a wait of the
display path: after a short time with nothing new on the screen, the
laptop's display goes into an idle state, and the next frame that
changes something waits about 80 ms for it to wake. A held PageDown
changes the screen every 48 ms here: page N wakes the display (80 ms),
page N+1 arrives while it is awake and shows at once, then 48 ms of
nothing new put it to sleep again, and page N+2 waits. That is the
"every second key" pattern, and it is why the gaps are the same at 10
presses a second (every page waits then) and why they do not depend on
the mouse, on the runtime's tiered compilation, or on the rows' work.

The busy-screen runs are the proof: with another window changing the
screen 60 times a second, the same hold gives 562 frames instead of 272
to 424, 8 frames over 33 ms instead of 17 to 31, and 4.3 % over 20 ms;
with it changing the screen every 7 ms, the display never sleeps, and
**the hold meets the goal in full: 637 frames, none over 33 ms, 1.3 %
over 20 ms, the worst frame 32 ms.** The window did the same work in
every run (the UI thread 150 to 320 ms a second).

The laptop's screen is an LG Display panel at 1920x1080 and 144 Hz,
driven by the Ryzen 7 4800H's integrated Radeon (the GTX 1660 Ti has no
display of its own; CabinetOS has no GPU preference set in Windows, other
apps on the laptop do). Laptop panels on the integrated graphics have
panel self refresh and the driver's idle clock gating; this PC's desktop
monitor at 60 Hz has neither, and the goal holds there with every frame
under 17.4 ms.

## What this means

- **The scroll goal measures the window's drawing on a desktop
  display, and the display's power saving on this laptop.** The frames
  the window owns are fine on both: the UI thread's work per second
  matches this PC's, and only 1 to 5 frames in 5 s have UI work over
  16.7 ms on the laptop.
- The window cannot stop a panel from sleeping between pages without
  keeping the screen changing, which is not something a file manager
  should do (Article 1 asks for speed, not for a window that never rests).
- Two runs that need the creator's say-so would complete the picture
  and are left undone: the panel at 60 Hz for one run (the idle state's
  entry may need more static vblanks at 60 Hz than at 144 Hz), and
  CabinetOS on the GTX ("High performance" in Windows' graphics
  settings; the Radeon still drives the panel, so the gaps are expected
  to stay, which would confirm the display path over the renderer).

## For the creator to decide

1. **How the live check judges the scroll goal on a laptop panel.** Three
   ways: (a) leave it as it is, and a laptop run exits 1 under `-Strict`
   with the reason known; (b) judge the goal by the frames' UI work on a
   machine with a panel (a `-Panel` switch like `-Virtual`: the gaps are
   printed, the UI-work counts are judged); (c) keep the screen busy
   during the hold on such a machine, which measures what the window can
   do but not what the user sees there.
2. Whether to run the two settings tests above.

## The runner

`ui/livecheck/remote-script.ps1` runs any script of the repository on the
laptop in its logged-in session through the new task `CabinetOS-Script`
and its wrapper `C:\Dev\cabinetos\_io\run-script-laptop.ps1` (the same
shape as the test runner: a bundle of the commits, a request file in the
laptop's inbox, the task, a wait for `DONE-script.md`, the output copied
home as `_io/script-runs/script-<time>-<machine>.txt`). `-Env
"NAME=VALUE;NAME=VALUE"` sets variables for the script and the window
(the no-tiering run used it); `-CopyBuilds` sends this PC's Release
window and core first. The wrapper sets `CABINETOS_UI_FRAMESTATS=1`, so
scroll measurements work without more. The busy-screen driver
`_io/scroll-with-busy.ps1` (outside the repository, a diagnostic) starts
the small redrawing window, runs `scroll-keys.ps1`, and closes it.
