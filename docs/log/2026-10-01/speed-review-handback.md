# Phase 19e, the speed review: the coder's hand-back

The creator's request before sleeping on 2026-10-01: "double check the
speed of the app, and where it could be improved." Built in the sleep-mode
run ([PLAN.md](../../PLAN.md), Phase 19e) by a coder on Opus in its own
worktree. Its report with the measurements is
[speed-review.md](speed-review.md); this file keeps the hand-back as
reported, with its decisions and undo lines. The planning session's checks
on the main checkout: main pulled at a19cbd7; the five core checks; the
release core rebuilt; the window's build with warnings as errors; the fast
test run; the full end-to-end run and the live check with real keys at the
end of the night. The seven proposals A to G are the creator's to choose.

---

Phase 19e, the speed review, is finished and pushed to main as 66b6c8d..a19cbd7.

Commits on main:
- f85db52: fixture folders and a runner that measures the release window (ui/livecheck/speed-fixtures.ps1, speed-review.ps1).
- e0b4013: the window logs "marketplace cards shown", so the marketplace's first view can be measured.
- 1564a12: the core starts, and the protocol's tables are built, while WinUI builds the window. Start is about 0.2 s shorter.
- 0375e61: crash fix. Shift+F10 on a row scrolled out of view ended the window; now the menu opens inside it.
- a62fb98: the four common context-menu shapes are built at idle after start.
- ec763c2: core matches names without three new buffers per name. Find in pane is 2.5 times faster.
- a15411c: runner improvements (builds compared run by run, more scenarios).
- 633fa65, a19cbd7: the report docs/log/2026-10-01/speed-review.md and its row in that folder's README.md.

Top findings, ranked by what a user feels (medians on release builds, at least 3 runs each):
1. Crash: the keyboard's menu on a row out of view ended the window with no crash trace. Fixed (0375e61).
2. Start to both folders on screen: 1.34 s / 1.32 s (small / 3,000 entries) before, 1.16 / 1.08 s after. Fixed in part (1564a12). ReadyToRun gave 1.03 / 0.86 s: proposal A, small, makes the download about 15 MB bigger.
3. Commander Compact theme change freezes one frame of 85 to 173 ms. Proposal B: fewer rows made ahead; small, plus a live check.
4. First right-click: 36 -> 24 ms before WinUI's own work, gap to screen 96 -> 75 ms. Fixed in part (a62fb98). About 65 to 71 ms of WinUI's first-show frame remains.
5. Marketplace first view: cards on screen in 93 to 126 ms, with one UI frame of 74 to 95 ms. Proposal C: cards made in parts, half a day.
6. Quick Open without the indexer stops at 20,000 entries; it found 0 rows for names past that in the 100k tree. Proposal D, small.
7. Tab switch to 100k files: median about 51 to 60 ms (range 44 to 148), because the folder is listed again. Proposal E, half a day.
8. Find in pane on 100k names: core 41-50 -> 16.3-16.8 ms, rows on screen 43-54 -> 18-24 ms. Fixed (ec763c2).
9. Icons at start: get_icon p95 47 ms, max 175 ms. Proposal F, small.
- The rest is fast. A 100k listing is on screen in 80-86 ms, and 10k folders in 25-36 ms. Scrolling has 0-8 % of frames over 20 ms. Idle CPU over 60 s is 31-328 ms for the window and 0-47 ms for the core. Memory: the window holds about 307-324 MB, the core about 28-35 MB.
- Proposal G (small): a WinUI fail-fast leaves no trace; note an unclean previous exit at the next start.

Verified on the final tree, after the rebase onto 66b6c8d:
- dotnet build -warnaserror: 0 warnings, 0 errors.
- Window tests: 1,108 passed and 35 skipped without the end-to-end ones. With them, 1,143 of 1,143 passed.
- Core's five checks: all passed, 805 tests passed (6 ignored). They ran after the first rebase; origin's later commits did not touch core/.
- One earlier end-to-end run failed 13 tests. It crossed midnight UTC, so the windows wrote a second day's log file, and those tests read exactly one. The rerun passed all 1,143.
- Before/after numbers come from paired runs: the two builds ran in turn under the same load.

Decisions not covered by the plan (what — because — undo):
- Measured on a shared machine — other coders' windows ran almost all night, and paired runs still compare fairly — rerun speed-review.ps1 on a quiet machine.
- The runner ignores a window that has run over 10 minutes — another coder's window stayed open 40 minutes — -Resident in the script.
- Fixed the menu crash although it is not a speed item — it ends the window with no trace (Articles 1 and 12) — revert 0375e61.
- Menu shapes are prepared 0.75 s after start, one per low-priority turn — a build costs 12 to 14 ms, less than one frame — revert a62fb98.
- A prepared shape never pushes out a shape the user opened — the user's own shapes are the ones that come back — Prepare in ContextMenuFlyout.
- The core starts from Program.Main, before WinUI — it needs nothing from the window, and it ends itself if the window fails — revert 1564a12.
- The Find speed-up was done in the core — no result or protocol change, and it was measured before and after — revert ec763c2.
- ReadyToRun was measured but not switched on — the download size is the creator's decision — proposal A.
- The keyboard's menu for an out-of-view row opens near the top of the pane — the row's place is known only after the next layout pass — FilePane.RowEdges.
- Report fixes went into a new commit (a19cbd7), not an amend — the global rules ask before an amend — nothing to undo.
- Fixtures and build copies are deleted. The logs (9.9 MB) stay in %TEMP%\cabinetos-speed-review — they are the evidence for the tables — delete that folder when it is not wanted.

Could not do:
- No ETW traces (Windows' system-level event tracing): they need admin rights, which a night run does not have.
- Quick Open was measured without the indexer only.
- The marketplace was measured on a local index, not the public one.
- No real keys: ui/livecheck is for the planning session.
- No detailed memory breakdown: it needs VMMap (a Windows memory-inspection tool).

Noticed, out of scope: many end-to-end tests call .Single() on the ui.*.jsonl log files. Any run that crosses midnight UTC fails them, so the tests should read all of a run's log files.

Recap: the review is pushed. It made one crash fix and three speed-ups, and wrote seven proposals for the creator to choose from. Left for the planning session: the live check with real keys, and a decision on each proposal (A-G).
