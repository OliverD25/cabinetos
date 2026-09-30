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
| [keys-audit-report.md](keys-audit-report.md) | 19d, Opus | the keys audit: every place the keyboard can be, the four kinds of keys, real keys, the Ukrainian layout; the state × key table, twelve faults ranked | f4eca09 to 7c32a1b; 5 fixed with a test that failed first (dialogs, a held chord key, Tab into a tool tab, Tab in the picker and plugin list, the chord notice), 7 proposed (the text-box rule first); 1042 window tests; the live check's new "keys" section 7 True alone |
