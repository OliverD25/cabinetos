## Item 5, the scroll-gap investigation

Agent: a019b1b2d5c91c57a

Report: Item 5, the scroll-gap investigation

Context: the CabinetOS shell (ui/). This item looked for why frames came late while PageDown was held on the 100,000-entry bench folder.

## Result
- **The cause was timing, not the amount of work.** A key message arrives at any moment between two frames. The pane moved the cursor at once, and WinUI then laid out the new page for 13 to 15 ms outside a frame. When that started late, the next frame had to wait for it.
- **The fix: three changes, nothing different on screen.** With real keys, gaps over 20 ms went from 27 to 32 % down to 0 % in the default theme. Over a 20 s hold: 1,207 frames, none over 20 ms, worst 17.8 ms.
- **Pushed to main:** 61f044d (code), 58fa975 (report, docs, probe script), c5ed4c8 (one doc line).
- **Still open:**
  - A single full garbage collection of 20 to 30 ms, which WinUI asks .NET for, sometimes lands near the start or end of a hold.
  - Commander Compact is still at 11.6 to 18.8 % of gaps over 20 ms.

## What was built
- `ui/CabinetOS.Core/Listing/PendingCursorKeys.cs` (new):
  - Up, Down, PageUp and PageDown wait in a queue.
  - `Target()` gives the same cursor move and selection mode as a key made at once.
- `ui/CabinetOS.Tests/PendingCursorKeysTests.cs` (new, 9 tests). One test checks that queued keys give the same focus, marks and anchor as keys made at once, in both the Windows and the Commander selection styles.
- `ui/CabinetOS/Views/FilePane.xaml.cs`:
  - Cursor keys are queued, and `ApplyCursorKeys` runs at `CompositionTarget.Rendering`, the start of the next frame.
  - Anything else that reads the cursor makes the waiting keys first: other keys, a click, a new model.
  - Rows are bound in `ElementPrepared`.
  - It asks `RowFactory` for 2 pages plus 4 rows ahead.
- `ui/CabinetOS/Views/RowFactory.cs` (new): an `IElementFactory` that reuses rows. It makes rows ahead at the dispatcher's low priority, in 4 ms slices.
- `ui/CabinetOS/Views/FileRow.xaml.cs`: `Show()` replaces the `DataContextChanged` handler. With that handler, WinUI asked for a full collection every 2 to 3 s of scrolling; without it, none in 20 s.
- `MainWindow.xaml.cs` and `MainWindow.Commander.cs`: a command (the router's `Executing`), a plugin command's arguments and a quick-search letter all make the waiting keys first.
- `ui/CabinetOS/Services/FrameMonitor.cs`:
  - `frame stats` lines gain `gc_pause_ms` and `gcs`.
  - A `slow frame` line (a frame 33 ms or more after the one before) is now logged whenever frame stats are on. It carries `busy_ms`, the collector's fields, and heap size, survivors, generation and background flag after a pause. In heavy mode it goes to `heavy::frames`.
- `ui/livecheck/scroll-bench.ps1`: keeps only the run's own slow frames.
- `ui/livecheck/scroll-keys.ps1` (new): the real-key probe. It prints the frame table the way the live check reads it, the CPU time of each thread, the desktop window manager (DWM) and the GPU, and the slow frames.
- Docs:
  - `docs/log/2026-09-30/scroll-gaps.md`: the report.
  - `docs/ui.md`: the "Scrolling" section rewritten, the "At a glance" row, the FRAMESTATS row, and the heavy-log table row.
  - `docs/diagnostics.md`: the new fields and the `slow frame` line.
  - `README.md`: UI test count 848.

## Checks (verbatim)
- `dotnet build ui/CabinetOS.sln -c Debug -warnaserror`: 0 Warning(s), 0 Error(s). Same for `-c Release`.
- UI tests, with `CABINETOS_UI_E2E=1` and the worktree's release core: "Test run summary: Passed! total: 848 failed: 0 succeeded: 848 skipped: 0".
- Probe on the committed build (F8): "summary: 301 frames in 5 s; over 20 ms 0 (0.0 %); over 33 ms 0; worst 17.7 ms; UI work over 16.7 ms 0; per second: UI work 232 ms, WinUI frames 228 ms, rows' measure 127 ms; presses lost to another window: 0".
- 20 s hold (F7): "summary: 1207 frames in 20 s; over 20 ms 0 (0.0 %); over 33 ms 0; worst 17.8 ms".
- Heavy logging on (H1): 0.0 % over 20 ms, worst 17.6 ms.
- Live check run item5-a: "scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: no; 301 frames in 5 s, 1 over 20 ms (0.3 %), 1 over 33 ms, worst 47.9 ms". One other check was False: "13: Enter in the tree took the left pane to beta: False". It passed in run b, so it was a timing flake.
- Live check run item5-b: "met: no; 302 frames in 5 s, 1 over 20 ms (0.3 %), 1 over 33 ms, worst 36.7 ms; CPU during the hold: machine 13.0 %". "Checks that answered False: none".
  - The one frame came 350 ms after the last press. It had no UI work in it: a blocking full collection of 20 ms.
  - The live check counts it because its table ends with the per-second line that ends up to 1 s after the hold. The script's own comment says the table holds "lines that ended while the key was held".

## Experiments
- The full table is in the report. The key rows:

| Run | Change | Gaps over 20 ms | Worst frame | UI work per second |
|---|---|---|---|---|
| Baseline, real keys (7 runs) | none | 27.2 to 32.3 % | 44.7 to 50.9 ms | 363 to 430 ms |
| Snapshot aid, no keys | none | 1.4 to 3.1 % | — | — |
| Key moved at the next frame, alone | 1 of the 3 | 0.3 to 1.0 % | — | — |
| Rows made ahead, alone | 1 of the 3 | 29.2 % | — | — |
| Bound in `ElementPrepared`, alone | 1 of the 3 | 29.0 % | — | — |
| All three changes | 3 of 3 | 0 % | — | — |

- Overscan 0.5, simpler rows, and the fixed-height layout with the old key path: 25.9 to 29.1 %.
- The collector's `SustainedLowLatency` mode: no effect on the collections WinUI asks for.
- With the three changes:

| Change | Result |
|---|---|
| Overscan 0.5 (how far outside the view the list keeps rows ready) | 8 % less work |
| No type column | 16 % less work |
| Plain rows | 17 % less work |
| Fixed-height layout | worse, 9.6 % over 20 ms |
| Commander Compact | 11.6 to 18.8 % over 20 ms |
| Commander Compact with overscan 0.5 | 6.3, 1.0 and 5.3 % over 20 ms |

- CPU per thread:
  - UI thread: 260 to 340 ms a second.
  - .NET tiered compilation (the runtime making faster code for methods it runs often): 150 to 190 ms a second at first, 43 ms over 20 s.
  - WinUI's composition thread: 60 to 100 ms a second.
  - DWM: 7 to 12 % of one processor. GPU: 0.2 to 0.4 % for this window. Neither is a limit.
- Not done:
  - A `ListView` rewrite: the handout rules out a rewrite, and the cause was not the control.
  - A refresh-rate change: a system setting. The display runs at 59 Hz.
  - An ETW trace (Windows' event recording): `wpr` needs elevation and failed with "Failed to enable the policy to profile system performance", error 0xc5583000.

## Decisions (what — because — undo)
- The three changes are committed — they meet the goal with real keys, and nothing on screen changes — revert 61f044d.
- Anything that reads the cursor makes the waiting keys first: other keys, a click, a command, plugin arguments, quick search, a new model — so actions happen in the order the keys were pressed — part of 61f044d.
- Rows made ahead are 2 pages plus 4 rows, in 4 ms low-priority slices — the first PageDown needs about one page more than the view shows, and short slices let input and frames go first — change `ahead` in `FilePane.OnElementPrepared` or `SliceMs` in `RowFactory`.
- Slow-frame lines and GC fields are logged whenever frame stats are on, and go to the heavy log in heavy mode — this is how the collections were found — revert the `FrameMonitor.cs` hunk.
- `scroll-bench.ps1` shows only the run's own slow frames — the window now logs them outside runs too — revert that hunk.
- `ui/livecheck/scroll-keys.ps1` is committed — the report's numbers need real keys, and the whole live check takes 5 minutes for one scroll line — delete the file.
- Overscan 0.5 is not committed — the default theme is already at 0 %, Compact stays near the 5 % edge, and I did not check fast scroll-bar drags for empty rows — set `Repeater.VerticalCacheLength = 0.5` in `FilePane`'s constructor.
- The live check's table window is not changed — it is the measure the goal is judged by, so that is the creator's call — nothing to undo.
- The report is dated 2026-09-30, not 2026-09-29 as in the handout — your message named this date — move the file.
- `docs/diagnostics.md` is edited, although it is the diagnostics agent's doc — my change added the lines it describes — revert that hunk.
- Commit trailer is "Claude Opus 5.5", not "Claude Opus 5" — it names the model that wrote the commits, as the other agents' commits do — nothing to undo.
- The display was kept awake with `SetThreadExecutionState` during measurements; no setting was changed — an asleep display paces frames at about 33 a second — the script (pid 85116) is stopped.
- The experiments are saved outside the repo as `_io\scroll-experiments-2026-09-30.patch` and not committed — the handout says not to commit experiments — delete the file.
- The extra check used its own run folders under `%TEMP%\cabinetos-ui-test\heavy-check`, laid out like the live check's, not the live check's own `live` folder — this keeps the last live-check run's logs — nothing to undo.

## Ready plan text (for "Hardening, step 3: scrolling")
"2026-09-30, item 5 (61f044d, 58fa975, c5ed4c8): measured with real keys (`ui/livecheck/scroll-keys.ps1`). The gaps came from the key's work running between frames: a page's 13 to 15 ms of layout ran outside a frame and pushed the next one back. The fix, with nothing different on screen:
- cursor keys are made at the start of the next frame (`PendingCursorKeys`);
- rows are bound in `ElementPrepared` (the `DataContextChanged` handler made WinUI ask for a full collection every 2 to 3 s);
- rows are made ahead at idle (`RowFactory`).
Default theme: 0 of 1,207 frames over 20 ms in a 20 s hold, worst 17.8 ms; before, 27 to 32 %. Report: docs/log/2026-09-30/scroll-gaps.md. The live check still says 'met: no' because of one collection of 20 to 25 ms after the last press, which its table counts. Open: that collection; Commander Compact at 11.6 to 18.8 % (overscan 0.5 gave 1.0 to 6.3 %, not committed until fast scroll-bar drags are checked); whether the live check's table should end at the last press (creator's decision)."

## What is left
- Commander Compact: overscan 0.5 needs a check of fast scroll-bar drags.
- Compact's first PageDown is still one frame of 60 to 77 ms. The cause is not known.
- The occasional collection WinUI asks for: finding its trigger needs an ETW trace from an elevated prompt.
- Deciding the live check's table window: the creator's call.

## The extra check (heavy logging and the crash offer), what I saw
1. Fresh Release build, `CABINETOS_LOG_HEAVY=1`, rail layout, run folders `%TEMP%\cabinetos-ui-test\heavy-check\{config,logs,themes,undo,webview2,plugins,plugins-data,marketplace,tools}`.
2. The status bar shows the blue `HEAVY LOG` pill, and UI Automation finds it (`scratchpad\heavy\heavy-statusbar.png`, `heavy-window.png`).
3. The start notice "Heavy logging is on; the log folder grows to 2 GB." is in `ui.2026-09-30.jsonl` at 02:59:22.733. It was gone from the screen by the time of the screenshot, 7 s later.
4. Keys sent: Down, Down, PageDown, Home in the left pane, then Ctrl+Shift+E, Down, Down, Esc in the folder tree (`heavy-after-keys.png`). The window closed cleanly.
5. `logs\heavy-ui.2026-09-30.jsonl` (143,309 bytes) holds 11 `key pressed`, 4 `focus changed`, 11 `frame stats`, 2 `command run` and 8 `slow frame` lines (target `heavy::frames`). The key lines include "down on FilePane LeftPane" and "down on FolderTreeView TreeSection". `heavy-core.2026-09-30.jsonl` is there too.
6. `CabinetOS.exe --self-test-crash` exited with code -1073741189 (0xC000027B) and wrote `crash-20260930T025937399Z.json` (19,216 bytes) and `crash-20260930T025937399Z.zip` (85,598 bytes).
7. On the next start, the status bar shows a red "Open crash folder" next to `HEAVY LOG`, visible to UI Automation (`crash-offer-window.png`, `crash-offer-statusbar.png`). The notice: "The last run crashed while heavy logging was on: the logs around the crash are in a zip in the log folder."
8. No `heavy logging is off` line. That is expected: closing the window does not switch heavy mode off.
9. Outside my item: in the rail layout the folder tree under FOLDERS is empty at start in both starts (`heavy-window.png`, `crash-offer-window.png`). The log still says "the tree shows a folder … rows 4488". The rows appear after Ctrl+Shift+E (`heavy-after-keys.png`). This is for the Phase 13 (rail) owner.
10. The screenshots and `heavy-check.txt` are in `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\heavy\`. No CabinetOS or Notepad window is left open.

## Housekeeping
- The first try of the extra check stopped on a bug in my script and left its window open. I closed it (pid 32948, from my worktree) and ran the check again from a fresh folder.
- The worktree is clean and main is at c5ed4c8.

## Recap
Item 5 is done: the cause was the key's timing, and the fix is on main (61f044d, 58fa975, c5ed4c8). The default theme now meets the goal with real keys, and the heavy-log and crash-offer check passed. Left: one collection WinUI sometimes asks for near a hold's start or end, Commander Compact, and the creator's call on the live check's table window.
