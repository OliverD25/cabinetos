# The speed review, 2026-10-01

Phase 19e of the plan. The creator's words before sleeping: "double check
the speed of the app, and where it could be improved if it could." A coder
on Opus measured the release build of the window and the core from 00:45 to
02:50 on the development PC, with the window's own log and frame statistics.

**The result.** Most of what a user does is fast. A folder of 100,000
files is on screen 80 ms after the request, and scrolling it keeps its
frames. A tab switch or a key in Find in pane answers in under 100 ms in
almost every run. The review found one crash and eight places a user can
feel, most of them small. The crash is fixed: Shift+F10 on a row scrolled
out of view ended the window. Three speed-ups are made: the start is about
0.2 s shorter, the first right-click about 20 ms shorter, and Find in pane
about 2.5 times faster. Seven larger changes are proposed with numbers. The
largest is a ReadyToRun build, which takes another 0.14 to 0.24 s off the
start and makes the download about 15 MB bigger.

## What a user feels, ranked

| # | Finding | Numbers (median, [lowest to highest]) | Status |
|---|---|---|---|
| 1 | Shift+F10 or the Menu key on a focused row that is scrolled out of view ended CabinetOS, with no crash trace | every run of the menu scenario on both builds; `0xc000027b` in CoreMessagingXP.dll | fixed, 0375e61 |
| 2 | The start: the process start to both folders on screen | medians 1.34 s (small folders) and 1.32 s (3,000 entries) before; 1.16 and 1.08 s after; 1.03 and 0.86 s with ReadyToRun | fixed in part, 1564a12; ReadyToRun proposed (A) |
| 3 | A theme change into or out of Commander Compact freezes one frame | 85 to 173 ms (the theme picker's preview of it: 112 to 135 ms); colour-only themes 20 to 54 ms | proposed (B) |
| 4 | The first right-click menu of a session | a 96 ms gap and two slow frames before; 75 ms after | fixed in part, a62fb98; the rest is WinUI's |
| 5 | The marketplace's first view | cards on screen 93 to 126 ms after the command, one frame of 74 to 95 ms of UI-thread work | proposed (C) |
| 6 | Quick Open without the indexer, in a folder of 100,000 files | it looks at the first 20,000 names only and says "0 results" for names after them | proposed (D) |
| 7 | A tab switch to a large folder lists it again | 44 to 148 ms to the tab (100,000 files), 9 to 56 ms (10,000 folders) | proposed (E) |
| 8 | Find in pane on 100,000 names, each key | 43 to 54 ms to the filtered rows before; 18 to 24 ms after | fixed, ec763c2 |
| 9 | Icons at the start of a session come after the rows | each new icon 25 ms in the core (p95 47 ms, max 171 ms), one at a time | proposed (F) |

Not felt, and left as they are: listing (100,000 files in 80 ms, 10,000
folders in 30 ms), scrolling (no frame over 33 ms in most runs), idle CPU
(0.05 to 0.5 % of one processor for the window, near zero for the core).
The window's memory is large (about 310 MB at start, most of it before
anything is done); section 11 says what is known.

## How it was measured

- **The builds.** Release builds of the window (`dotnet build -c Release`)
  and the core (`cargo build --release`), main at 5cc437a. "Before" is that
  build, copied aside. "After" is the same with this night's fixes. For the
  start, two more: `dotnet publish` as `build/release.ps1` makes it ("pub"),
  and the same with `-p:PublishReadyToRun=true` ("r2r").
- **The folders.** [speed-fixtures.ps1](../../../ui/livecheck/speed-fixtures.ps1)
  makes them in `%TEMP%\cabinetos-speed-review\fixtures`: `small` (24
  entries), `home-3000` (2,850 files and 150 folders, the size of a real home
  folder), `files-100000` and `folders-10000`. Names are mixed as a user's
  are, with Ukrainian and Japanese among them, and eight extensions.
- **The runs.** [speed-review.ps1](../../../ui/livecheck/speed-review.ps1)
  starts a fresh window for every run on a scratch configuration (every
  `CABINETOS_*` folder under one scratch folder, as the end-to-end tests
  do) and drives it with the snapshot steps. No key is pressed. It reads the
  numbers from the window's log (`ui.*.jsonl`) and the core's
  (`core.*.jsonl`). Each scenario ran three times per build, the start five
  times per build, and the builds took turns run by run, so a busier moment
  of the machine hit both alike. Tables give the median and the lowest and
  highest value.
- **Frames.** `CABINETOS_UI_FRAMESTATS=1`: one line a second with the
  frames, the worst gap and the gaps over 20 and 33 ms, and a "slow frame"
  line for every frame of 33 ms or more. The display was awake: the idle
  frame clock was 60 frames a second in every run.
- **The machine was shared.** Three other coders ran their end-to-end tests
  at the same time: up to eleven CabinetOS windows at once, and compiles.
  The runner waited while a window younger than ten minutes ran. One of
  them stayed open for 40 minutes; runs in that time are marked as
  sharing. The machine's CPU before a run was 9 to 96 %, mostly 10 to 35 %.
  So single numbers are noisy; the paired medians are what the findings
  use.
- **Not measured.** Real keys (the live check is the planning session's).
  The public marketplace index over the network (a local index from
  `sdk/marketplace/build-index.ps1 -Collection` instead: the network adds
  its own time). The indexer (it needs an elevated prompt), so Quick Open
  used the core's walk.

## The numbers

### 1. Start

The process start (`Process.StartTime`) to the log lines; "both shown" is
the second "listing shown" (both panes come in the same frame).

| Folder | Build | Core started | Hello answered in | Both folders shown |
|---|---|---|---|---|
| small, 5 runs | before | 907 ms [863 to 1,247] | 133 ms [128 to 148] | 1,336 ms [1,263 to 1,811] |
| | after | 97 ms [83 to 118] | 8.4 ms [7.5 to 8.8] | 1,160 ms [1,071 to 1,334] |
| | pub (after, as released) | 104 ms | 8.0 ms | 1,170 ms [1,052 to 1,758] |
| | r2r (after, ReadyToRun) | 107 ms | 6.8 ms | 1,031 ms [879 to 1,236] |
| home-3000 on the left, 5 runs | before | 896 ms [866 to 928] | 129 ms [128 to 137] | 1,315 ms [1,307 to 1,366] |
| | after | 94 ms [87 to 96] | 7.7 ms [7.4 to 8.5] | 1,076 ms [1,074 to 1,417] |
| | pub | 88 ms | 7.7 ms | 1,093 ms [1,052 to 1,187] |
| | r2r | 78 ms | 6.0 ms | 855 ms [833 to 884] |

An earlier batch of three runs each gave the same picture (before 1,246 to
1,725 ms, after 1,098 to 1,476 ms on the small folders). The core itself
answers `hello` in 0.15 ms in every build.

### 2. Listing a large folder

`path:` to the folder in a 1400 × 900 window; the "listing shown" line has
the core's time, the reply, the first row and the first frame.

| Folder | Core lists it | Reply in the window | First row | First frame | Frames in the second after |
|---|---|---|---|---|---|
| 100,000 files (6 runs) | 55 to 62 ms (one run 168) | 56 to 63 ms | 71 to 77 ms | 80 to 86 ms | worst gap 25 to 33 ms, none over 33 ms |
| 10,000 folders (6 runs) | 5.7 to 7 ms (one run 17) | 6.5 to 8 ms | 17 to 28 ms | 25 to 36 ms | worst gap 23 to 37 ms, one frame over 33 ms in two runs |

### 3. Scrolling

`scroll:20`: PageDown 30 times a second for 20 pages (440 rows, about
0.65 s, 37 to 39 frames). The snapshot aid presses at the start of a frame;
the earlier investigation ([2026-09-30/scroll-gaps.md](../2026-09-30/scroll-gaps.md))
explains why real keys are the harder test.

| Folder (6 runs) | Frames over 20 ms | Over 33 ms | p95 | Worst frame |
|---|---|---|---|---|
| 100,000 files | 0 to 3 (0 to 7.9 %) | 0 to 1 | 18 to 24 ms | 18.7 to 34.2 ms |
| 10,000 folders | 1 to 3 (2.6 to 8.1 %) | 0 to 1 | 18 to 31 ms | 26 to 41.5 ms |

### 4. A tab switch

`tab:new` beside a tab on 100,000 files, then `tab:next` four times. A tab
switch lists its folder again (`PaneModel.RestoreAsync`).

| To (12 switches) | Tab shown | Listing's first frame | Frames |
|---|---|---|---|
| 100,000 files | 51 to 60 ms [44 to 148] | 61 to 71 ms [53 to 161] | none over 33 ms |
| 10,000 folders | 11 ms [9 to 56] | 21 to 22 ms [19 to 69] | none over 33 ms |

### 5. A theme change, and the theme picker's preview

`theme:commander-compact` and back, twice a run, with 100,000 files and
3,000 entries on screen. The picker's preview (`pick:`) moved over the
shipped themes.

| Change (12 each) | `set_value` to "theme applied" | to "metrics applied" | The frozen frame |
|---|---|---|---|
| to Commander Compact | 5 to 15 ms | 14 to 29 ms | 85 to 173 ms (median 105 to 112), 44 to 88 ms of it measured UI work |
| back to Default | 5 to 7 ms | 14 to 18 ms | 81 to 121 ms (median 95), 26 to 33 ms measured UI work |
| picker preview of Commander Compact (4 runs) | | | 112 to 135 ms |
| picker preview of Nord, Rosé Pine Moon, Catppuccin Mocha (4 runs) | 2 to 20 ms to "theme previewed" | | 20 to 54 ms, one run at 406 ms on a busy machine |

### 6. The context menu

Keyboard menus (`select:` brings the row into view, then `menu:`) on
`home-3000`: a file, a folder, the pane's space, then the three again. With
the default menus a file and a folder have one shape, so the folder's first
menu is that shape's second. 3 runs per build.

| Opening | build_ms before → after | "placed" after "shown" | Worst gap before → after | Slow frames |
|---|---|---|---|---|
| first menu of the session (a file) | 36 [36 to 57] → 24 [22 to 34] | 147 → 145 ms | 96 [86 to 129] → 75 [75 to 113] ms | 2 both; 65 to 71 ms of UI work in one |
| a folder (same shape) | 7.4 → 7.1 | 37 → 39 ms | 28 → 32 ms | 0 |
| the pane's space (second shape) | 15 → 15 | 97 → 95 ms | 59 → 55 ms | 2 both, 43 to 45 ms of work |
| the file again, after the space | 11 [7 to 25] → 7.1 | 64 → 42 ms | 47 → 32 ms | 2 → 0 |
| the folder and the space again | 6 to 8 | 35 to 46 ms | 24 to 36 ms | 0 to 1 → 0 |

### 7. Quick Open

`quick-open:dat`, then `IMG77` and `фото9`, on the 100,000-file folder.
The box waits 80 ms after the last key, then asks the core (`search`). No
indexer ran, so the core walked (`source: walk`).

| Query (6 runs each) | The core | Rows on the window's side after the request | Rows | Frames |
|---|---|---|---|---|
| first (`dat`) | 43 to 64 ms | 47 to 69 ms | 50 | one 36 to 47 ms frame (the overlay's first showing) |
| `IMG77`, `фото9` | 40 to 55 ms | 40 to 55 ms | 0 | none over 33 ms |

The core's line says `visited: 20000, complete: false` for all three: the
walk stops after 20,000 entries (`WALK_LIMITS` in
`core/crates/cabinetos-core/src/search.rs`), and in this folder the names
that start with IMG or фото come after the first 20,000.

### 8. Find in pane

`find:r`, `re`, `rep`, `repo`, `repor` on the 100,000-file listing (16,668,
then 8,334 matches); each step asks the core `match_entries`.

| Per key, medians of 3 runs per build (same window, the core the only difference) | The core before → after | Rows filtered after the request, before → after | Frames |
|---|---|---|---|
| 100,000 names | 41 to 50 ms (up to 61) → 16.3 to 16.8 ms (up to 19) | 43 to 54 ms (up to 70) → 18 to 24 ms | none over 33 ms |
| closing the find (all rows back) | | | one frame of 36 to 53 ms, 13 to 19 ms of it UI work |

### 9. The marketplace's first view

`cmd:marketplace.browse` on a local index with 50 items.

| (6 runs) | Numbers |
|---|---|
| the view shown | 11 to 14 ms after the command |
| `marketplace_refresh` (a local file) | 3.6 to 13 ms |
| the first cards on screen ("marketplace cards shown", new) | 93 to 126 ms after the command |
| frames | 2 to 3 slow frames; the worst 78 to 120 ms, with 74 to 95 ms of UI-thread work making 50 cards |

### 10. Request latencies by type

From the 51 window logs of the before runs: the round trip the window
measured ("request sent" to "reply received") and the core's own time
("request handled"). Sorted by the window's p95.

| Request | Count | Median | p95 | Max | Core median | Core p95 | Core max |
|---|---|---|---|---|---|---|---|
| hello | 51 | 129.8 | 147.9 | 193.9 | 0.14 | 0.20 | 0.35 |
| list_directory | 183 | 3.6 | 59.3 | 169.1 | 1.2 | 58.8 | 168.7 |
| search | 13 | 47.4 | 55.6 | 69.0 | 44.1 | 55.0 | 63.7 |
| get_icon | 334 | 25.2 | 47.0 | 175.0 | 24.8 | 46.6 | 171.1 |
| match_entries | 19 | 41.8 | 44.5 | 45.4 | 40.4 | 43.5 | 44.1 |
| list_themes | 10 | 6.7 | 20.6 | 20.6 | 4.0 | 19.1 | 19.1 |
| describe_entries | 752 | 8.9 | 17.4 | 25.7 | 6.1 | 15.5 | 23.4 |
| marketplace_refresh | 7 | 3.6 | 13.0 | 13.0 | 1.1 | 9.8 | 9.8 |
| update_status | 51 | 6.5 | 7.7 | 9.9 | 0.03 | 0.04 | 0.06 |
| set_value | 171 | 4.7 | 7.4 | 29.6 | 3.7 | 5.8 | 29.2 |
| list_volumes | 51 | 5.9 | 7.4 | 77.4 | 3.8 | 4.9 | 75.0 |
| window_state | 299 | 0.31 | 5.0 | 6.7 | | | |
| get_config | 193 | 0.33 | 2.8 | 3.5 | 0.05 | 0.09 | 0.18 |
| get_theme | 63 | 2.1 | 2.6 | 3.1 | 0.05 | 0.26 | 0.28 |
| workspace_info | 161 | 1.0 | 2.4 | 10.6 | 0.67 | 0.93 | 1.1 |
| list_commands | 193 | 0.84 | 2.3 | 3.7 | 0.10 | 0.14 | 0.18 |
| others (shutdown, get_keymap, list_jobs, list_tools, list_plugins, close_listing) | | 0.2 to 1.5 | under 2.1 | under 9.2 | under 0.4 | | |

All in milliseconds. After the fixes, `hello` is 8.0 ms median (37 logs,
p95 9.7) and `match_entries` 16 to 17 ms in the core. The slow ones:
`hello` (fixed), `get_icon` (finding 9), `search` without the indexer
(finding 6), `match_entries` (fixed), and `list_directory` of the large
folders, which is the listing itself.

### 11. Idle CPU and memory

60 s with nothing happening, 10 s after the last step; CPU time from
`Get-Process` before and after. "Session" is one window through the
scenarios above (100,000 files, scrolling, a second tab on 10,000 folders,
Commander Compact and back, three menus, Quick Open, Find, the
marketplace) without frame statistics.

| (3 runs per build) | Window CPU in 60 s | Core CPU in 60 s | Window working set / private | Core working set / private | Window threads |
|---|---|---|---|---|---|
| at start, two small folders, before build | 109 to 125 ms | 0 to 16 ms | 307 to 308 / 250 to 253 MB | 28 / 11 MB | 101 to 102 |
| at start, two small folders, after build | 31 to 156 ms | 0 to 16 ms | 308 to 312 / 251 to 252 MB | 28 to 29 / 11 MB | 101 |
| after the session, before build | 78 to 328 ms | 0 to 47 ms | 315 to 324 / 263 to 272 MB | 32 to 35 / 14 MB | 103 |
| after the session, after build | 156 to 297 ms | 0 to 31 ms | 315 to 321 / 265 to 269 MB | 31 to 32 / 13 MB | 102 to 103 |

## The findings, with their causes

**1. The keyboard's menu for a row out of view ended the window (fixed,
0375e61).** In the menu scenario the window stopped at the second menu of
every run, in both builds. The list makes rows ahead of what it shows (two
screens and some rows more), and `FilePane.RowEdges` took such a row's
place as the menu's point: y 1,638 in a 900 px window. `MenuPlacement`
then put the menu above that point, still below the window. WinUI ended the
process with a stowed exception (`0xc000027b`, CoreMessagingXP.dll, seen in
Windows' Application log) before any of the window's crash hooks ran, so
there was no crash trace and the log just stopped. A user meets it after
scrolling with the wheel away from the cursor row and pressing Shift+F10 or
the Menu key. Fix: a made row outside the list's view counts as not on
screen (the menu opens near the top of the pane, as the code already did for
a row not made), and `MenuPlacement` counts a point outside the window as
the edge it is past. Tests: the placement test failed before the fix; an
end-to-end test opens the keyboard's menu on row 40 of 120 and reads that it
is on screen and inside the window.

**2. The start (fixed in part, 1564a12; ReadyToRun proposed).** Where the
1.3 s went before: 60 to 90 ms until the window's first log line, about
800 to 850 ms while WinUI and the window build the window, then the core
was started (it opens its pipe about 60 ms later), `hello` took 130 ms
although the core answered in 0.15 ms, and about 250 ms more to the first
frame with both folders. The 130 ms was the pipe's reader building
`MessageCodec`'s table: the source-generated JSON metadata of 57 reply and
event types, made at the first reply. Both waits now happen on background threads at process start,
beside WinUI's own start (`CoreSession.StartEarly`, `MessageCodec.Warm` in
`Program.Main`); the window still says hello once it is built. What is left
is the window's own build, and a large part of it is the JIT compiling the
window's code and WinUI's C# projection (`Microsoft.WinUI.dll` ships as IL,
7.3 MB): the ReadyToRun build shows it.

**3. Commander Compact freezes one frame (proposed).** Colour themes only
change brushes and cost one normal frame. Commander Compact changes the row
height (30 to 20 px) and the font size, so every row the lists have made is
measured again: `measure_ms` 45 to 49, `arrange_ms` 12 to 13 and `bind_ms`
5 to 7 in the frozen frame, and WinUI's own layout and drawing of the rest.
Each pane keeps rows made for about two screens around the view (WinUI's
default cache length) plus the rows made ahead since the scroll work, so a
switch measures several hundred rows again. The picker's live preview does the same
when the highlight passes Commander Compact.

**4. The first right-click menu (fixed in part, a62fb98).** The first menu
spent 36 ms before it returned (`build_ms`, which includes WinUI's
`ShowAt`); 12 to 14 ms of it was building the `CommandBarFlyout` and its
buttons. The window now builds the common shapes while it is idle
after start, so the first menu only shows. What stays is WinUI's work at a
flyout's first showing: its presenter and the buttons' templates, one frame
of 65 to 70 ms of UI work. It cannot be done ahead without showing a menu.

**5. The marketplace's first view (proposed).** `MarketplaceView.RenderCards`
makes all cards at once (a `Button` with a tile, a title, a description and
a footer each) in the frame after the index arrives: 74 to 95 ms of UI work
for 50 cards. The collection will grow; the time grows with it.

**6. Quick Open without the indexer (proposed).** The walk limits
(`WALK_LIMITS`: 2 s and 20,000 entries) are meant to keep a walk short.
Listing 100,000 names takes the core about 60 ms, so the entry limit, not
the time, ends the walk, and the names after the first 20,000 are never
seen. The window shows "0 results" and does not say the search stopped
early (the reply says `complete: false`).

**7. A tab switch lists the folder again (proposed).** `ShowFrontTabAsync`
restores a tab with `PaneModel.RestoreAsync`, which lists its folder again
(`NavigateAsync` with `NavigationKind.Reload`): a new `list_directory`, new
type names and a new section, and the other tab's listing is closed. For
100,000 files that is the 60 ms of a listing, each time.

**8. Find in pane (fixed, ec763c2).** `cabinetos_fs::match_entries` decoded
each whole entry (a `Vec<u16>` and a `String`) and folded its case into a
new `Vec<char>`: three buffers per name, 400 ns a name. It now reads the
name's bytes and the attributes only, and folds into one buffer: 16.5 ms
for 100,000 names. Marking by a pattern and quick search use the same pass.

**9. Icons at start (proposed).** The core draws each icon with the shell
(`SHGetFileInfoW`, then the image list), one at a time in the process (the
`DRAWING` lock in `cabinetos-fs/src/hydrate.rs`), and keeps the PNGs for its
lifetime. A new core draws every key again: about 25 ms each, so the first
folder's six or seven icon kinds appear over 50 to 200 ms after the rows.

**Memory.** The window's working set is about 310 MB (about 250 MB private)
right after start on two small folders, and it grows only a little in the
session (to about 320 MB). The managed heap is 7 to 10 MB (the frame lines'
`gc_heap_mb`), so almost all of it is native. One look at a window 10 s
after start (the after build): 311 MB working set, of it 180 MB private working set;
164 modules, whose images are 431 MB in all, and the two largest are the
graphics driver's (`nvgpucomp64.dll` 106 MB and `nvwgf2umx.dll` 86 MB),
then `Microsoft.Windows.SDK.NET.dll` 25 MB and WinUI's `Microsoft.UI.Xaml.dll`
15 MB; 113 threads. The graphics driver's own allocations for the window's
DirectX surfaces count as the process's private memory too. What else is
in the 250 MB of private memory needs a memory tool (VMMap, or a trace
from an elevated prompt), which this review did not have.

## Proposals, with an estimate

| Proposal | Gain | Cost and risk | Estimate |
|---|---|---|---|
| A. Publish the window with ReadyToRun (`-p:PublishReadyToRun=true` in `build/release.ps1`) | start 0.14 s (small folders) to 0.24 s (3,000 entries) shorter, on top of 1564a12 | the window's folder grows from 42 to 57 MB before zipping; a decision on the download size | small |
| B. Commander Compact: fewer rows made ahead (`Repeater.VerticalCacheLength` 0.5 instead of 2, as scroll-gaps.md proposed for Compact's scrolling) | fewer rows to measure again at a density change, and 8 % less work while scrolling (measured 2026-09-30) | a fast drag of the scroll bar may show empty rows for a frame; needs a check with real mouse input | small, plus a live check |
| C. Make the marketplace's cards in parts: the first screenful at once, the rest in slices at low priority (or an `ItemsRepeater` with a uniform grid) | the first view's frozen frame from 74 to 95 ms to about 20 ms | the card's keyboard order and selection must stay | half a day |
| D. Quick Open's walk: raise the entry limit (the 2 s limit stays), and say "searched the first N entries" when the reply is not complete | finds names past the first 20,000 in a large folder | a walk of a huge tree takes longer, up to the 2 s limit | small |
| E. Keep the listing of the last tab of a pane alive for a while, so switching back does not list again | a tab switch to 100,000 files from about 60 ms to about 20 ms | a watched listing and its section (about 10 MB for 100,000 entries) stay open | half a day |
| F. The core draws the icons of `folder` and `generic` and of the first listing's extensions right after its start, on its blocking threads | the first folder's icons come with its rows | a little core work at start | small |
| G. A native end of the window (a WinUI fail-fast) leaves no crash trace: note at the next start that the previous run ended without closing, from the marker the window already writes (`ui.last-start`) | the next such crash is at least visible in the log (Article 12) | none | small |

## What was fixed tonight, and how it was checked

- 1564a12: the core started and the codec's tables built at process start.
  Unit test: warming the codec beside a first decode changes nothing it
  decodes. End-to-end test: "core started" comes before the window's
  "Starting the core…", and `hello` answers in under 60 ms.
- 0375e61: the keyboard's menu for a row out of view. The placement test
  failed first; the end-to-end test opens the menu on a row the list made
  but does not show.
- a62fb98: the common menu shapes built at idle. End-to-end test: the first
  menu on a file, a folder and the pane's space has `built: false`.
- ec763c2: the name matching without buffers per name. Core unit test: the
  pass matches exactly what the decoded names match, beyond ASCII.
- e0b4013: "marketplace cards shown" in the log, to measure section 9.
- f85db52, a15411c: the fixture and runner scripts.

The checks on the final tree, after the rebase onto 66b6c8d, all passed.
The window builds with warnings as errors. The window's 1,143 tests pass
with the end-to-end ones; without them 1,108 pass and 35 are skipped. The
core's five checks pass, with 805 tests (6 ignored). One earlier end-to-end
run lost 13 tests because it crossed midnight UTC. The windows then wrote a
second day's log file, and those tests read exactly one. The next run
passed all 1,143.

## Decisions made alone

Each as what — because — undo.

- Measured on a shared machine instead of waiting for it to be free — other
  coders' test windows ran almost all night, and paired runs of both builds
  in turn compare fairly under the same load — rerun `speed-review.ps1`
  on a quiet machine.
- The runner does not wait for a window that has run ten minutes — one
  window of another coder stayed open 40 minutes — `-Resident` in the script.
- The context menu's crash was fixed although it is not a speed item — it
  ends the window without a trace, which Article 1 ("never freeze") and
  Article 12 rule out, and the fix changes only where a menu that crashed
  appears — revert 0375e61.
- The menu shapes are prepared 0.75 s after the first folders, one per
  dispatcher turn at low priority, four shapes (file, folder, space, several
  rows) — the first right-click comes later than that in practice, and a
  build costs 12 to 14 ms, under a frame — `PrepareMenusAfter` and the list
  in `PrepareContextMenusSoonAsync`, or revert a62fb98.
- A prepared shape never pushes a shape the user opened out of the six kept
  — the user's own shapes are the ones that come back — `Prepare` in
  `ContextMenuFlyout`.
- The core is started before WinUI starts, from `Program.Main` — its start
  needs nothing of the window, and the core's parent-process watch ends it
  if the window fails to start — revert 1564a12.
- The find speed-up was made in the core although the plan's example was a
  window fix — it changes no result and no protocol, and it was measured
  before and after with the core as the only difference — revert ec763c2.
- ReadyToRun was measured but not switched on — it grows the download by
  about 15 MB, which is the creator's decision — proposal A.
- A ReadyToRun publish restored the crossgen compiler package from NuGet —
  the same kind of download as any package restore, needed to measure
  proposal A — nothing to undo.
- The keyboard's menu for a row out of view opens near the top of the pane,
  not under the row after scrolling it into view — the row's place is known
  only after the next layout pass, and near the top is what the code already
  did for a row the list had not made — change `FilePane.RowEdges`.
- The walk limit of Quick Open, the cards of the marketplace, the tab
  listings, the icons and ReadyToRun are proposals, not fixes — each changes
  what a user sees or gets, or has a cost the creator weighs — the table of
  proposals.
- The marketplace was measured on a local index, not the public one — a
  network call for a measurement was not needed — rerun with
  `marketplace.index` left at its default.
- The fixtures and the copies of the builds were removed at the end; the
  runs' logs (10 MB) stay in `%TEMP%\cabinetos-speed-review\results` —
  the fixtures are 110,000 files of scratch data, the logs are the evidence
  behind the tables — `speed-fixtures.ps1` makes the folders again in about
  40 s; delete the results folder when it is not wanted.
