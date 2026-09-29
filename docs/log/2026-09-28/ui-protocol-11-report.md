## Report: the shell adopts protocol 11

Report: the shell adopts protocol 11 — done, 8 commits on main, 418 UI tests pass locally; the live-check findings task starts now.

## Built
1. Window commands from the registry. The 19 ui-target commands (plugins.list, editor.*, terminal.*, search.scope, transfer.*, conflict.resolve, sidebar.pin/unpin) lost their local stand-ins (own titles, default keys, RegisterWindowCommand, the palette's substring stopgap). The window keeps only handlers (RegisterUiHandler). Run from the palette or a key without arguments, each acts on what is in front. A plugin command from the palette or a key gets {path, paths} of the active pane (router hook PluginArgs).
2. ui.dockSize. Read at start and on config_changed: null = the design's size, any value clamped to the design's limits. Saved with set_value ui.dockSize.bottom / .right at the end of a drag, once, only when it changed, whole pixels.
3. Transfer flyout. A job without bytes graphs items_per_second (floor 10 items/s) and the pace says "412 items/s"; paused samples 0; no pace before the first one or after the end.
4. Marketplace. No more folding of versions. installedVersion fills the Installed tab and Uninstall, and brings "Update available" / an "Update" button when the offer is newer (numeric compare). A line under the buttons names the installed version, or says why the marketplace leaves a shipped/hand-copied extension alone.
5. Themes. list_themes' mica feeds the picker (one request, no get_theme per theme). Kind "system" follows Windows' mode: read from UISettings (Windows' app background colour), re-applied live on ColorValuesChanged. In light mode a system theme uses the window's own light colours (new SystemLight: Windows 11 Fluent light values, Windows palette colours for file types and levels, "One Half Light" terminal), keeping the theme's accent and Mica.
6. Type column. End-to-end test: .gitattributes gets a type name in words, not a program identifier. Snapshot shows "GITATTRIBUTES File" and "GITIGNORE File".
Plus docs/ui.md updated (commands, dock, flyout, marketplace, themes, type names, "Not in this version", "Known gaps"), and two dev-only snapshot steps: mode:light|dark|windows and click:<accessible name>.

## Commits (all on origin/main)
- 25f74b9 ui: the window's own commands come from the core's registry (protocol 11)
- 7dd0d46 ui: the transfer graph shows items per second for a job without bytes
- 2d6bb3a ui: the dock keeps its dragged size across restarts (ui.dockSize)
- 38d2eb6 ui: the marketplace follows the core's installedVersion
- 0879ff4 ui: a system theme follows Windows' light or dark mode, live
- e295022 ui: the snapshot aid presses a button by its accessible name
- 1073439 ui: an end-to-end test that .gitattributes gets a type name in words
- 923a120 docs: the shell on protocol 11, and three snapshots

## Checks
- dotnet build CabinetOS.sln -c Debug -warnaserror and -c Release -warnaserror: 0 warnings, 0 errors (after the last rebase onto 7506990).
- dotnet test: 418 passed, 0 failed, 0 skipped; 7 end-to-end tests against the real core (core debug and release rebuilt from main after the protocol 11 pull).
- CI: not started (account billing). No CI claim.
- Nothing under build/, CHANGELOG.md, docs/release.md, docs/decisions/0009*, .github/workflows/ or ui/Directory.Build.props was touched.

## Live check (release builds, locked screen, snapshot aid, scratch folders under %TEMP%\cabinetos-ui-test\p11-*)
- Dock after a restart: dock:330 wrote "bottom": 330; the next start opened the dock at about 330 px instead of 240.
- System theme: fresh themes folder via CABINETOS_THEMES_DIR (the core wrote default 1.1.0, kind system). Windows is in dark mode: window dark. mode:light turned panes, text, accent, terminal (One Half Light) and the open picker light; mode:windows turned it dark again. Log: "theme applied default kind system mode dark/light/dark". Picker's Default swatch sampled #202020 → #F3F3F3 → #202020. Windows' own setting was NOT changed, so a real ColorValuesChanged was not tried (it calls the same method).
- Installed tab: Paper 1.0.0 (scratch light theme) and Hello installed; Installed listed exactly those two, not the 4 shipped themes. Index then offering Paper 1.1.0: card "Update available", button "Update", line "Version 1.0.0 is installed. The update replaces its files.", Uninstall offered. Update installed 1.1.0 without applying it; status bar "Paper is updated to version 1.1.0.".
- Type column: .gitattributes "GITATTRIBUTES File", .gitignore "GITIGNORE File".
- The creator's theme copy: %LOCALAPPDATA%\CabinetOS on this PC has only logs and WebView2 (no themes folder); nothing there was read or changed.
- Snapshots committed: docs/log/2026-09-28/protocol-11-ui-light.png, protocol-11-ui-installed.png, protocol-11-ui-types.png.

## Decided
- A handler runs even when the registry does not list its command — because a core before protocol 11 has no entries for these and the window must keep working with it — undo: drop the _handlers fallback in CommandRouter.ExecuteAsync.
- No-argument meaning of each window command (transfer.* the flyout's job; conflict.resolve opens the flyout at the conflict or says none waits; sidebar.pin/unpin the active pane's folder; terminal.show shows the dock; terminal.close ends the shown shell; search.scope toggles "Whole volume"; editor.close/reload the editor with the keyboard, else the open one) — because the registry now makes them reachable from the palette and keys with no arguments — undo: per handler.
- Plugin commands from palette/keys get {path, paths} (focused row + selection, or the focused search hit) — because docs/plugins.md "What the shell passes" says so — undo: _router.PluginArgs = null.
- Item jobs graph from a 10 items/s floor, one decimal under 10 items/s — because a slow job must not fill the graph and the first record has no pace — undo: TransferText.GraphFloor/SpeedText.
- Each dock placement keeps its own size; a config_changed that echoes the window's own write is ignored, and nothing moves the dock during a drag — because the echo would fight the pointer — undo: _dockKnown/_dockDragging checks in ApplyStoredDockSize.
- "Installed" badge also for extensions present but not from the marketplace (no Uninstall for them) — because the core refuses to install over them (trust rule 7), so "Install" could only fail — undo: MarketplaceModel.ActionFor/IsPresent.
- A theme's update is installed, not applied — because an update is not a choice of theme (the core re-applies it if it is in effect) — undo: the "Theme when update" case in MainWindow.Market.cs.
- A plugin's update with capabilities opens the review before the download — because the core clears its grants (trust rule 1) and the new version may ask for more — undo: MainWindow.Market.cs InstallFromMarketAsync.
- Versions compare with System.Version; a pair that cannot be parsed offers no update — because a wrong "Update" could go back to an older version — undo: MarketplaceModel.IsNewer.
- Search replies do not update installed versions; refresh and this window's own install/uninstall do; tools_changed drops tools that left — because a search reply made before an install ended would undo it — undo: SearchNowAsync.
- Picker drops get_theme per theme; an older core shows plain swatches — because one request replaces N and the older-core case is cosmetic — undo: restore ReadTintAsync from 0879ff4^.
- Light mode of a system theme uses the window's own light colours (Fluent light values, One Half Light) — because the theme's palette is dark-mode only, themes.md leaves light to the window, and the design has no light tokens (Article 3: native look) — undo: SystemLight.cs and the mapper's followsMode && systemIsLight branch.
- The mapper's dialog-footer and flyout steps use light factors in light mode (#FCFCFC → #F3F3F3); this also affects kind "light" themes — because the dark factor made light footers mid-grey (#B7B7B7) — undo: the two isLight branches in ThemeMapper.
- Windows' mode from UISettings, not the registry — because it is the documented API and the same object already gives the accent and the change event — undo: ThemeApplier.SystemIsLight.
- Dev-only snapshot steps mode: and click: — because changing Windows' own setting on the creator's PC is a system setting change, and the marketplace tabs have no command — undo: remove the two cases in TakeSnapshotsAsync.
- The type test checks the rule, not the words "GITATTRIBUTES File" — because type names depend on the PC and its language; the snapshot shows the words — undo: EndToEndTests.
- Three snapshots committed to docs/log/2026-09-28 — because they show what tests cannot, and the creator's Windows is in dark mode — undo: git rm them.
- Fixed the stale ui.md row that said marketplace.browse and preferences.selectColorTheme "arrive in a later version".

## Needs the core
- install_finished could carry the installed version. Without it, an install by another client with --version is taken as the offered version until the next Refresh. Low priority.

## Needs the user
- Real-key re-run: a real drag of the dock splitter, Windows' mode switched while the window runs (Settings > Personalization > Colors), Tab/Enter to the Update button.
- Design review of the light look (protocol-11-ui-light.png): the design handout lists light mode as open; these colours are a first choice.
- On a PC that already has a themes folder with the old default.json 1.0.0 (maybe the Omen laptop), delete that file to get 1.1.0 (themes.md "Not yet"). This PC has none.

## Known gaps
- A real ColorValuesChanged was not exercised (setting not changed).
- CbGraphFillBrush stays #4D000000, so the transfer graph sits on a dark box in light mode.
- Menus and tooltips not opened in light mode; a dialog open during a mode change keeps the old mode.

## Noticed out of scope
- The marketplace card's author name is cut to "CabinetO…" when the state says "Update available" (narrow card).
- Scratch data left for inspection: %TEMP%\cabinetos-ui-test\p11-dock, p11-market, p11-theme, p11-theme2, p11-types, market-index-p11 (the coordinator's live-shots folders untouched).

## Ready text for docs/PLAN.md (under Phase 5, after the protocol 11 paragraph)
**The shell adopts protocol 11, 2026-09-28.** The window's own commands come from the core's registry, so the palette ranks them with everything else and each can be rebound; the window keeps only their handlers, and from the palette or a key each acts on what is in front. A plugin command from the palette or a key gets the active pane's `{"path", "paths"}`. A dragged Tool Dock size is saved once per drag in `ui.dockSize` and comes back after a restart, held to the design's limits. The transfer flyout graphs `items_per_second` for a job without bytes and says "items/s". The marketplace shows the core's one item per extension; `installedVersion` fills the Installed tab and brings "Update available" (a plugin's update is reviewed first, a theme's is not applied), and shipped themes show as there but offer no Uninstall. The `default` theme follows Windows' light or dark mode live, with Windows 11's own light colours in light mode, and the picker takes the Mica tints from `list_themes`. `.gitattributes` reads "GITATTRIBUTES File". 418 tests, seven against the real core. Checked on 2026-09-28 with snapshots (release builds, locked screen): the dock at about 330 px after a restart, the Installed tab with an update, light mode through a snapshot step (Windows' own setting was not changed). Snapshots in [log/2026-09-28/](log/2026-09-28/). Guide: [ui.md](ui.md).

## README
Line 29–30: "(`ui/`, 412 tests)" becomes "(`ui/`, 418 tests)".
