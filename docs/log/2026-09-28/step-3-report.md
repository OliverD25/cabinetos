## Report: hardening step 3, scrolling

Step 3 report (scrolling the 100,000-entry folder): UI-thread work per page fell by about a quarter, the scroll is measured without real keys, and livecheck.ps1 prints the goal line; the gap goal itself is unproven because the display slept all night. All six commits are on main (last 7d08cd9). I start 11a, the shell's part, now.

Context: the third of the creator's chosen next steps, in the WinUI shell (ui/). Nothing under core/ or sdk/ was touched. The link-flag adoption you allowed "sooner if step 3 is done" is in too (7d08cd9).

## Built
1. Measurement, on under CABINETOS_UI_FRAMESTATS=1:
   - FrameParts times each part of the UI thread's work a scroll causes: the rows' measure and arrange passes (RowLayout, WinUI's StackLayout with its passes timed), binding rows and the rows' own measure inside the measure pass; outside it, describe pages applied and requested, icons, selection marks, the status bar.
   - Each frame records the gap before it, WinUI's frame time (RenderedEventArgs.FrameDuration), and how much of the rows' layout ran inside that frame. WinUI also runs layout outside frames when the thread is idle. A frame's work = WinUI's frame time + the layout outside it + the other parts.
   - Snapshot-aid step scroll:<pages> presses PageDown 30 times a second by the clock. A press that falls due during a slow frame goes at the next frame, as queued key messages do. scroll:<pages>/<n> presses once every n frames. The run logs a "scroll run" line: the frame table, and the CPU load during the run (machine, this window, its core, the rest; from GetSystemTimes).
   - ui/livecheck/scroll-bench.ps1 repeats the run in a fresh window on the bench folder. It waits while the machine is above 30 %, and prints the idle frame clock, each run's table, and the best and worst run.
2. Fixes:
   - A row sets only the values that changed (Shown<T> in FileRow): texts, visibilities, the icon, its brush, the visual state. WinUI lays a text out again even when it gets the same string, and a recycled row often shows the same time, type or size. This is the main gain.
   - An icon's PNG is decoded from base64 into a stream on a worker thread (IconBytes.DecodeAsync); the UI thread only makes the bitmap. Small gain: icons 0.70 -> 0.59 ms per frame in System32.
3. Live check: livecheck.ps1 records CPU times around the 5 s PageDown hold, prints the frame table of the hold's seconds from the window's "frame stats" lines, and prints one line: "scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: yes|no; N frames in S s, X over 20 ms (P %), Y over 33 ms, worst W ms; CPU during the hold: machine M %, this window U %, busiest others: ...". -Strict (off by default) exits 1 when the goal is not met.
4. docs/ui.md: a new "Scrolling" section (the goal, how it is measured, where the time goes, what changed, what was tried and dropped, what remains); the glance row, the FRAMESTATS and scroll: rows, -Strict; "Links and cloud files" now says the kind comes from the flags.

## Commits (all on main)
- 93dd155 ui tests: count the shipped themes, and never end the run on a late post (your theme-pin fix, done first)
- 0c429b2 ui: a frame table for the scroll, measured without real keys
- 3b651b8 ui: a row sets only the values that changed
- bd02897 ui: an icon's PNG is decoded off the UI thread
- 4e96706 live check, docs: the scroll goal as a line to read, and where the time goes
- 7d08cd9 ui: links take their kind from the listing's flags

## Checks
- Debug and Release builds of CabinetOS.sln with -warnaserror: 0 errors, 0 warnings.
- UI tests: 501 in total. A normal run passes 500 and skips 1 (the two-window test); with CABINETOS_UI_E2E=1 all 501 pass. Run against a core rebuilt from main at 54a25e2 (with the N1-N31 seed rows).
- New tests: FrameTableTests (12: the summary counts, the goal, busy time with layout inside and outside the frame, the CPU load, ScrollStep parsing), ShownTests (2), IconBytesTests (2). For the flags: A_link_in_the_listing_is_of_the_kind_its_flags_name (it failed before the fix, every link came back Unknown) and the layout test's offsets and flag values.
- Live check with the real core and the flags: the fixture's links folder shows "Junction", "Symbolic link to a folder" and "Symbolic link to a file" in the Type column, and Shift+Delete on the junction asks "Delete the link permanently?" (cancelled).
- livecheck.ps1 and scroll-bench.ps1 parse without errors in PowerShell 7 and Windows PowerShell 5.1. livecheck.ps1's new lines were NOT run: they need real keys on an awake display.
- No CI claim.

## Live check (snapshot aid, no real keys)
Bench folder %TEMP%\cabinetos-bench\100000 (100,000 files, five types), release builds, 150 pages of 30 rows. The display was asleep: Windows then asks for frames about 33 times a second instead of 60.

| | before (3 runs) | after (3 runs) |
|---|---|---|
| UI-thread work per second of scrolling | 656, 731, 576 ms | 439, 439, 436 ms |
| per page of 30 rows | 19-24 ms | about 15 ms |
| frames whose work passed 16.7 ms | 66.8, 71.4, 54.4 % | 21.6, 20.8, 19.5 % |
| busiest frame | 55.6, 52.9, 54.0 ms | 48.7, 51.4, 47.0 ms (the first press) |
| machine CPU during the run | 25.0, 36.5, 16.1 % | 13.9, 14.3, 14.2 % |
| one press every second frame (-Rhythm 2, one run each) | 376 ms/s, 36.5 % (109 of 299 frames) | 230 ms/s, 3.7 % (11 of 299), busiest 46.4 ms |

- Best after-run: 436 ms/s, 19.5 %, busiest 47.0 ms. Worst after-run: 439 ms/s, 21.6 %, busiest 48.7 ms.
- Before-run 2 was at 36.5 % CPU during the run (the bench checks the load only before a run starts). The fair pair is the quietest: 576 ms/s at 16 % against 436-439 at 14 %.
- Where the milliseconds go, quietest after-run, per frame (154 frames): UI-thread work 14.18. Of it, WinUI's frame 13.97: the rows' measure pass 8.57 (binding rows 0.68 of that), arrange 0.42, the rest is drawing. Outside it: selection marks 0.12, status bar 0.03, describe pages applied 0.01, requests 0.03, icons 0. Rows made: 4,432 for 4,500 rows scrolled.
- The goal's own numbers with the display asleep: before 16, 17, 35 frames over 33 ms of 208, 196, 195; after 49, 36, 46 of 153, 159, 154. These measure the sleep clock, not the window: once the window keeps up, its frames come on the 33-a-second clock, 30-34 ms apart, and 86-90 % count as over 20 ms. They say nothing about 60 Hz.
- System32 (4,930 entries, 629 programs with their own icons), 140 pages: 593 ms of work per second.

## Decided
- The display stayed asleep; I did not wake it with input — because keys or mouse moves on the creator's desktop at night reach outside this task, and the gap goal needs a watched screen anyway — undo: run scroll-bench.ps1 or livecheck.ps1 with the display awake.
- "Fixed" is judged by UI-thread work per frame, not by gaps — because gaps with a sleeping display measure Windows' 33 Hz clock — undo: none needed, both are logged.
- -Rhythm 2 stands in for 60 Hz — because on the 33-a-second clock one press every second frame is the rhythm of 30 presses a second at 60 Hz (a frame with a new page, a frame without) — undo: -Rhythm 0.
- WinUI's StackLayout stays, with only a timing subclass (RowLayout) — because the two custom layouts I built (fixed-height rows; a layout that made the next page in the idle frame) cost more: WinUI draws rows only when they come into view, so drawing stays in the frame that shows them, and the extra rows added work of their own — undo: the layout element in FilePane.xaml.
- Describe pages are not held back until the scroll settles — because they cost 0.01-0.06 ms per frame — undo: none needed.
- Icons stay cached per key as before (the bench's 100,000 rows share five); only the decode moved — because the cache already worked — undo: IconCache decodes on the UI thread again.
- Text trimming off, another font, reading order from the text: dropped — because no gain beyond noise once equal texts are skipped — undo: none needed.
- The link kind is read as mount point first, then junction, then symbolic link — because Windows makes a mount point a junction to a volume, so a row with both flags must say "Mount point" — undo: the order in EntryFacts.LinkOf.
- The reparse tag is read (ListingView.ReparseTag) but shown nowhere — because no row or dialog needs it yet — undo: none needed.
- The cloud state still comes from the attributes, not from flag 16 — because the core sets the flag from the same attributes — undo: EntryFacts.IsNotOnDisk.

## Needs the core
Nothing for scrolling: describe pages cost 0.01-0.06 ms per frame, and no request sits on the scroll's path.

## Needs the user
Nothing. The planning session's real-key run with the display awake decides the gap goal (PowerShell, because it sends SendInput keys to a Windows window):
# PowerShell
powershell -ExecutionPolicy Bypass -File E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\ui\livecheck\livecheck.ps1 -Strict
(the release builds of the window and the core must exist first, as the script's header says.)

## Known gaps
- The gap goal (no frame over 33 ms, under 5 % over 20 ms) is unverified: the display was asleep. The rhythm-2 run, 3.7 % of frames' work over 16.7 ms, is the best stand-in I had.
- The first PageDown in a new window makes rows from the template (the pool is empty): one frame of 35-50 ms, once per window.
- About 15 ms of work per page remains, nearly all inside WinUI: about 100 µs to make or recycle a row, about 38 µs per changed text layout (timed on the name), about 5 ms of drawing per page. That fits a new page every second frame at 60 Hz, but not a faster key repeat or many more rows per page.
- The scroll: step calls the pane's move directly. It skips the keyboard path (KeyDown, the key map, the command router), which only the real-key run covers.
- CPU noise: other agents compiled during some runs. The bench waits for a quiet machine before each run, but cannot stop load that starts during one.

## Noticed out of scope
- Commander Compact's 20 px rows put about 45 rows on a page instead of 30. At about 0.5 ms per row that is about 22 ms of work per page, more than one 60 Hz frame. The scroll needs measuring again with the compact theme; I will do that as part of the Compact task.
- The window's layout outside the rows (the crumbs, the status bar's own layout) is not timed as a part; it shows only inside WinUI's frame time.

## Ready text for docs/PLAN.md, under Phase 10

**Hardening, step 3: scrolling, 2026-09-29.** The shell measures a scroll without real keys. With `CABINETOS_UI_FRAMESTATS=1` it times each part of the UI thread's work (the rows' layout, binding rows, type-name pages, icons, selection marks, the status bar), and the snapshot aid's `scroll:<pages>` presses PageDown 30 times a second and logs the frame table with the CPU load; `ui/livecheck/scroll-bench.ps1` repeats the run and names the best and worst of three. On the 100,000-entry bench folder the UI-thread work fell from 576-731 ms to 436-439 ms per second of scrolling, about 15 ms per page of 30 rows, because a row now sets only the values that changed (WinUI lays a text out again even when it gets the same string); icon PNGs are decoded off the UI thread. At one press every second frame, the rhythm of 30 presses a second on a 60 Hz display, 3.7 % of the frames' work passed 16.7 ms (36.5 % before). The first PageDown in a new window makes the rows, one frame of 35-50 ms. What remains is WinUI's own cost: row recycling, text layout and drawing. The gap goal (no frame over 33 ms, under 5 % over 20 ms) could not be measured at night, because a sleeping display paces frames at 33 a second; `ui/livecheck/livecheck.ps1` prints it as one line from a real-key run, and `-Strict` makes a miss fail. Details: [ui.md](ui.md), "Scrolling".

## Ready text for docs/PLAN.md, Phase 5's "Done when" (line 154), the new clause in brackets

Done when: the core and the UI run together, a listing of 100,000 files scrolls without frame drops (measured 2026-09-29 with the snapshot aid and the display asleep: about 15 ms of UI-thread work per page of 30 rows, and 3.7 % of frames over 16.7 ms at the rhythm of a held PageDown on a 60 Hz display; the gap goal is `livecheck.ps1`'s line from a real-key run), and every visible action goes through `CommandRouter` and a command ID.

## README
UI test count: 501 (one of them, the two-window test, runs only with CABINETOS_UI_E2E=1).
