# Build log, 2026-10-01: the third sleep-mode run

The night of 2026-09-30/10-01. The creator went to sleep with "go on with
Files Type Sprint" and two requests: the shortcuts that do not always
work, and the app's speed. The plan's Phase 19 in [PLAN.md](../../PLAN.md)
holds the order the planning session chose and the decisions; this folder
holds the coders' reports, one per finished item, verbatim as handed back,
with every unattended decision as a `what — because — undo` line.

| Report | Agent | Item | Result |
|---|---|---|---|
| [phase-19b-folder-sizes-report.md](phase-19b-folder-sizes-report.md) | 19b, Sonnet | folder sizes for every folder of a listing: `panes.folderSizes`, `view.toggleFolderSizes`, cancel on leaving | d352be2 to 2c63758; 803 core, 1012 window with the end-to-end tests; the live check at the end of the night |
| [phase-19c-compact-overlay-report.md](phase-19c-compact-overlay-report.md) | 19c, Sonnet | the compact overlay: `view.toggleCompactOverlay` (Ctrl+Alt+Up), a small always-on-top drawer with one pane, its size saved as `ui.compactOverlay`, the layout restored on leaving | 501493c to 6addc78; 803 core, 1028 window with the end-to-end tests; the live check at the end of the night |
| [keys-audit-report.md](keys-audit-report.md) | 19d, Opus | the keys audit: every place the keyboard can be, the four kinds of keys, real keys, the Ukrainian layout; the state × key table, twelve faults ranked | f4eca09 to 7c32a1b; 5 fixed with a test that failed first (dialogs, a held chord key, Tab into a tool tab, Tab in the picker and plugin list, the chord notice), 7 proposed (the text-box rule first); 1042 window tests; the live check's new "keys" section 7 True alone. Round two, 02817cb to 1973fe6: P1 (both rules), P3, P5, P6 and P7 built, P2 and P4 still proposed; 1134 of 1135 window tests (one known flake); the keys section 14 of 14 alone |
| [keys-audit-handback.md](keys-audit-handback.md) | 19d, Opus | the keys audit's hand-back as reported: five fixes, seven proposals, the planning session's decision on the text-box rule (round two by the same coder) | f4eca09 to c70f06a; 1042 window tests; the live check's keys section 7 of 7 with real keys |
| [phase-19f-column-view-report.md](phase-19f-column-view-report.md) | 19f, Opus | the column view (Miller columns) as a mode of a pane's tab: `view.toggleColumns` (Ctrl+Alt+C), one watched listing per column, the mode saved in `ui.tabs`, the metric `columnViewWidth`; ADR 0016 | 2a600a7 to ac5b3c4; 804 core, 1054 window with the end-to-end tests; the live check at the end of the night |
| [speed-review.md](speed-review.md) | 19e, Opus | the speed review: start, listing, scrolling, tabs, themes, the context menu, Quick Open, Find in pane, the marketplace, request latencies, idle CPU and memory, measured on release builds with paired before and after runs; the findings ranked, four fixes, seven proposals | a6b6b94 to e83341f; the start 0.2 s shorter, Find in pane 2.5 times faster, the crash of the keyboard menu on a row out of view fixed |
