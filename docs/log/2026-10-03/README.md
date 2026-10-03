# Build log, 2026-10-03: the theme gallery night

The night of 2026-10-02/10-03. The creator went to sleep after choosing
Phase 23 of [PLAN.md](../../PLAN.md): the theme gallery with its own catalogue
file, and the first four settings that could be changed in the file only. This
folder holds the coders' reports, one per finished item, with every unattended
decision as a `what — because — undo` line.

| Report | Agent | Item | Result |
|---|---|---|---|
| [theme-gallery-and-three-ways-report.md](theme-gallery-and-three-ways-report.md) | Phase 23, Sonnet | two catalogue files from one build script (`index.json` for plugins and tools, `themes.json` for themes with `appearance`, `density` and tile colours; `marketplace.themes`; protocol 20; a transition rule while the public site has no `themes.json`); the Extensions page without themes; the theme gallery (colour tiles, filters, live preview with the new request `preview_theme`, Install, Apply, Remove, reached from the picker's last row and "Themes: Browse"); the layout, hidden files, the Explorer following the pane and Windows' own menu each with a command and a control in the window; ADR 0022 | b71aa8d to 6dffc42 and the report's commit; core 917 passed, 6 ignored, clippy, fmt and deny green; window built with warnings as errors in Debug and Release, 1387 tests (1319 fast, 68 need a window), 1384 of 1387 with the window tests on the laptop (the 3 that fail are update tests that fail on main too); live check on the laptop 287 True, 0 False, panel goal met (the PC's own run was not possible: the installed CabinetOS is running); a picker fault found by the live check (a resting pointer took the highlight onto the new last row) is fixed; five screenshots in `_io\phase-23-shots`; the creator publishes `themes.json` after the merge |
