## Report: Commander Compact, the core's part

Context: CabinetOS core, the Commander Compact task. The theme format now has optional `metrics` and `chrome`, and a fifth theme ships, built from the handout in `_io\design\Compact_Theme`. It is done and pushed as **9e4ce98**.

**Heads-up first.** Two of the shell's end-to-end tests pin the shipped themes at four, so they fail now. One of them also crashes the shell's test host. Details under "Needs the user / the shell". I left `ui/` untouched, as the task said.

## Built
- **Handout copied into the repository** (ADR 0008): `docs/design/compact/COMPACT_THEME.md` and `CabinetOS Compact.dc.html`, unchanged, byte for byte. `docs/design/ABOUT.md` has a one-line note about them.
- **The rule, recorded in docs/themes.md:** a theme may change colours, sizes and whether chrome elements are there. It never changes commands, keys or the layout.
- **Format (`cabinetos-protocol`).** `Theme` gains two optional objects at the end: `metrics: Option<Metrics>` and `chrome: Option<Chrome>`. When either is absent, the default look applies.
  - One table in `theme.rs` (a `metrics!` macro) declares the struct fields, the `METRICS` list (name, unit, min, max, default, compact value, description), the bounds check and the JSON Schema. The docs table is generated from the same table, so they cannot drift apart.
  - The new public items are `Metrics`, `Chrome`, `METRICS`, `MetricSpec`, `MetricUnit`, `Decimal` and `THEME_FORMAT = 2`.
- **Reading is strict (`cabinetos-themes`).** A theme is refused, with the reason, for:
  - an unknown metric or chrome name (`unknown field …`);
  - pixels written with a fraction (`20.5`, and `20.0` too);
  - a value outside its bounds, e.g. `metrics.rowHeight: 10 is not from 14 to 80`;
  - a lower limit above its upper limit. This covers `sidebarMinWidth` against `sidebarMaxWidth`, and `terminalDockMinHeight` against `terminalDockMaxHeight`. If a theme sets only one of a pair, the other side is the default theme's value.
- **Messages:**
  - `get_theme` and `theme_changed` carry both objects exactly as the file has them.
  - A whole number goes out as `17`, not `17.0`. The end-to-end test caught that difference before I fixed it.
  - `list_themes` items gain `has_metrics`. It is true only when the theme sets at least one metric; an empty `{}` counts as false.
  - Everything new is optional, so the **protocol stays at version 11**.
- **Schemas regenerated:** `sdk/themes/theme.schema.json` (now with `"$id": "urn:cabinetos:theme:2"`), `sdk/protocol/response.schema.json` and `sdk/protocol/event.schema.json`.
- **Fifth shipped theme:** `sdk/themes/commander-compact.json`. It is appended to `SHIPPED` (so `SHIPPED[1]` is still Nord), the constant is `COMMANDER_COMPACT`, and the core writes the file into the themes folder when it is missing, following the shipped-theme update policy. Its content:
  - `name` "Commander Compact", `kind` `system`, `accent` null, `mica` null;
  - the default theme's palette and terminal, except `acrylicTint` `#262626E6` (see Decided);
  - all 76 metrics at their compact values;
  - chrome with all three elements on.
- **CLI:** `cabinetos-cli themes list` shows a `compact` column. `themes show` prints the two objects.

## Field names and bounds (for the shell)
**Theme file (camelCase), both keys optional:**
```json
"metrics": { "<metric>": <number>, ... }
"chrome": { "fkeyBar": true, "rowStripes": true, "hairlines": true }
```

**`list_themes` item (snake_case, like every protocol field):** `has_metrics` (bool; absent means false).

**Chrome** (each absent means off):
- `fkeyBar`: the function-key bar between the panes and the status bar. Its keys are F3 View, F4 Edit, F5 Copy, F6 Move, F7 Mkdir, F8 Delete, Alt+F1 Drive. Its buttons run the commands those keys run.
- `rowStripes`: every other row of a file list a shade lighter.
- `hairlines`: 1 px separators between the surfaces instead of floating cards.

**Metrics:** 76 in all, in the handout's order. Each line is `name` unit min-max: default -> compact.
- **Units:** px = whole pixels; num = a number that may have a fraction; % = a share of the window's width (the sidebar) or height (the terminal dock).
- **Global:** `fontSize` px 8-32: 13 -> 12; `lineHeight` num 1-2.5: 1.4 -> 1.3; `backdropOpacity` num 0-1: 0.86 -> 0.94; `radiusControl` px 0-16: 4 -> 2; `radiusSurface` px 0-16: 8 -> 0; `gap` px 0-48: 8 -> 0; `bodyPadding` px 0-64: 8 -> 0.
- **Title bar:** `titleBarHeight` px 14-80: 40 -> 30; `tabHeight` px 14-80: 32 -> 24; `tabPaddingX` px 0-64: 14 -> 10; `tabMinWidth` px 40-240: 96 -> 80; `tabFontSize` px 8-32: 12 -> 11; `tabRadius` px 0-16: 8 -> 3; `captionButtonWidth` px 24-96: 46 -> 40.
- **Command bar:** `commandBarHeight` px 14-80: 48 -> 32; `iconButtonSize` px 14-80: 32 -> 26; `fieldHeight` px 14-80: 32 -> 24; `toggleHeight` px 14-80: 32 -> 26.
- **Sidebar:** `sidebarMinWidth` px 100-600: 180 -> 150; `sidebarWidthPercent` % 5-50: 20 -> 17; `sidebarMaxWidth` px 100-600: 224 -> 190; `sidebarHeaderFontSize` px 8-32: 11 -> 10; `sidebarHeaderPaddingTop` px 0-64: 14 -> 8; `sidebarHeaderPaddingX` px 0-64: 12 -> 8; `sidebarHeaderPaddingBottom` px 0-64: 4 -> 2; `sidebarRowHeight` px 14-80: 34 (workspaces) / 32 (pinned) -> 22; `sidebarRowInset` px 0-16: 4 -> 0; `sidebarRowRadius` px 0-16: 4 -> 0; `selectionBarWidth` px 0-8: 3 -> 2 (sidebar rows and file rows); `driveRowPaddingY` px 0-64: 6 -> 3; `driveRowPaddingX` px 0-64: 10 -> 8; `tagRadius` px 0-16: 12 -> 2; `tagFontSize` px 8-32: 12 -> 11.
- **Panes:** `paneHeaderHeight` px 14-80: 36 -> 24; `columnHeaderPaddingY` px 0-64: 4 -> 2; `columnHeaderPaddingX` px 0-64: 14 -> 8; `nameColumnWeight` num 0.1-10: 1 -> 1.6; `modifiedColumnWeight` num 0.1-10: 0.55 -> 0.9; `typeColumnWeight` num 0.1-10: 0.45 -> 0.7; `nameColumnMinWidth` px 0-400: 120 -> 0; `sizeColumnWidth` px 32-160: 64 -> 60; `columnGap` px 0-48: 0 -> 8; `rowHeight` px 14-80: 30 -> 20; `rowPaddingX` px 0-64: 10 -> 8; `rowRadius` px 0-16: 4 -> 0; `rowIconGap` px 0-48: 10 -> 6; `secondaryFontSize` px 8-32: 12 -> 11.
- **Editors:** `editorTabHeight` px 14-80: 36 -> 26; `markdownPaddingY` px 0-64: 28 -> 14; `markdownPaddingX` px 0-64: 40 -> 20; `markdownLineHeight` num 1-2.5: 1.6 -> 1.5; `hexRowHeight` px 14-80: 22 -> 18; `hexColumnGap` px 0-48: 18 -> 14.
- **Terminal:** `terminalDockMinHeight` px 60-800: 120 -> 100; `terminalDockHeightPercent` % 10-80: 30 -> 26; `terminalDockMaxHeight` px 60-800: 240 -> 200; `terminalHeaderHeight` px 14-80: 34 -> 24; `terminalTabHeight` px 14-80: 26 -> 20; `terminalPaddingY` px 0-64: 10 -> 6; `terminalPaddingX` px 0-64: 12 -> 8; `terminalLineHeight` num 1-2.5: 1.6 -> 1.45.
- **Marketplace:** `marketplaceTabHeight` px 14-80: 32 -> 24; `marketplaceTabRadius` px 0-16: 4 -> 0; `marketplaceCardGap` px 0-48: 10 -> 4; `marketplaceCardPaddingY` px 0-64: 14 -> 8; `marketplaceCardPaddingX` px 0-64: 14 -> 10; `marketplaceCardRadius` px 0-16: 8 -> 2.
- **Overlays:** `paletteRowHeight` px 14-80: 36 -> 28; `menuRowHeight` px 14-80: 32 -> 26.
- **Status bar:** `statusBarHeight` px 14-80: 26 -> 22; `statusBarPaddingX` px 0-64: 14 -> 8; `statusBarGap` px 0-48: 16 -> 12.
- **Function-key bar:** `fkeyBarHeight` px 14-80: 24 -> 24; `fkeyBarGap` px 0-48: 1 -> 1; `fkeyButtonRadius` px 0-16: 2 -> 2; `fkeyBarFontSize` px 8-32: 11 -> 11. The default theme has no bar, so for these four the "default" is the handout's value.

## Commits
- 9e4ce98 "themes: metrics and chrome, and Commander Compact as the fifth shipped theme". Pushed after `pull --rebase`.

## Checks
- **Five checks** on the committed state: build ok; tests **564 passed, 0 failed, 5 ignored** (554 before); clippy -D warnings ok; fmt ok; deny ok.
- **Tests written first, and how they failed before the code:**
  - They did not compile at first: `metrics`, `chrome`, `has_metrics`, `METRICS` and `Decimal` did not exist.
  - After the code: the schema snapshots failed until they were regenerated.
  - The wire-form test failed as intended, because `themes` items now carry `has_metrics`.
  - The end-to-end test showed that `17` went out as `17.0`; I fixed that.
- **New tests, by crate:**
  - protocol: `metrics_and_chrome_are_optional`, `metric_and_chrome_names_are_strict_and_pixels_are_whole`, `every_metric_is_named_once_with_bounds_that_hold_both_looks`, `values_outside_their_bounds_are_named`, `theme_info_says_whether_a_theme_sets_metrics`, `a_whole_decimal_is_written_without_a_fraction`, and in the schema module `the_theme_schema_carries_metrics_and_chrome_with_their_bounds`.
  - themes: `commander_compact_is_the_default_look_made_dense` and `a_theme_without_metrics_and_chrome_is_still_valid`. `a_bad_theme_is_refused_with_its_problem` gained 5 cases: an unknown metric, an out-of-bounds value, a fractional pixel value, a pair limit, an unknown chrome key.
  - core end-to-end: `commander_compact_brings_its_metrics_and_chrome`, plus the list test extended to five themes and `has_metrics`.
- **Independent schema check:** PowerShell 7's `Test-Json` accepts all five shipped files and a theme without either object. It rejects an unknown metric, rowHeight 10, rowHeight 20.5, lineHeight 3, and an unknown chrome key.
- **UI tests:** not all pass. See "Needs the user / the shell".

## Live check
Fresh core, empty themes folder:
```
  catppuccin-mocha   Catppuccin Mocha   dark            accent #CBA6F7  mica #1E1E2E 0.9   1.0.0 by CabinetOS
  commander-compact  Commander Compact  system compact  accent system   mica plain         1.0.0 by CabinetOS
* default            Default            system          accent system   mica plain         1.1.0 by CabinetOS
  nord               Nord               dark            accent #88C0D0  mica #2E3440 0.88  1.0.0 by CabinetOS
  rose-pine-moon     Rosé Pine Moon     dark            accent #EBBCBA  mica #232136 0.9   1.0.0 by CabinetOS
```
- `config set ui.theme commander-compact`, followed with `events watch`: first `config_changed ["ui.theme"]`, then `theme_changed: commander-compact | metrics: 76 | rowHeight 20 fontSize 12 lineHeight 1.3 backdropOpacity 0.94 sidebarWidthPercent 17 | chrome: fkeyBar, rowStripes, hairlines all true | acrylicTint #262626E6`.
- `themes show` gives the same. The folder now holds `commander-compact.json`.

## Decided
- **Every value in the handout's Metrics section got a name: 76 in all**, not only the 25 in the task's example list.
  - Two-number paddings became X/Y pairs. So the example `columnHeaderPadding` is `columnHeaderPaddingY` plus `columnHeaderPaddingX`.
  - Grid column weights and minimums are named too.
  - `sidebarRowHeight` is one name for workspace and pinned rows; the default theme has 34 and 32.
- **The two groups of bounds the task left to me:**
  - Lengths: widths and docks have the ranges listed above, and `selectionBarWidth` is 0-8.
  - Ratios: line heights are 1 to 2.5; column weights are 0.1 to 10.
- **`mica` stays null in Commander Compact; the handout's .94 is the metric `backdropOpacity`.** The format's opacity belongs to a Mica tint (`mica.tint` with `mica.opacity`). A tint fixes a colour, which Windows' light mode cannot follow.
- **Colours: the default theme's, with one exception, `acrylicTint` `#262626E6`.** The handout's own Overlays line asks for it. This is a small deviation from "the same colours as default".
- **What the theme does not carry.** These are not sizes:
  - the hairlines', stripes' and F-key buttons' shades, and the active pane header's fill: the shell draws them with the handout's shades when chrome turns them on;
  - the Size column in Fira Code, the shortened drive label and the status bar's "Commander compact ·" prefix: text and type choices, which belong to the shell.
- **Format version:** the schema's `$id` is `urn:cabinetos:theme:2`. Version 1 is colours only. Every version-1 theme is valid in version 2.
- **The dev index keeps packing every shipped theme,** so it offers five now. Only the comment in `sdk/marketplace/build-index.ps1` changed.

## Needs the user / the shell
The two UI end-to-end tests below pin the four shipped themes. The UI agent should update both when it builds its half.

1. **`EndToEndTests.Choosing_Nord_in_the_picker_brings_theme_changed_and_the_mapper_reports_its_accent`** (EndToEndTests.cs:321). It expects the four IDs and now gets `commander-compact` at index 1.
2. **`EndToEndTests.The_marketplace_reads_the_local_index_installs_a_reviewed_plugin_that_starts_and_removes_it`** (EndToEndTests.cs:369). It expects `Assert.Equal(4, market.CountOf(MarketTabs.Themes))` and now gets 5.
   - **This failure crashes the test host.** The event pump then posts to a finished `UiThread` (`Support/UiThread.cs:38`: "The collection has been marked as complete"). The run then reports only 458 of 485 tests.
   - With test 2 excluded (`-- --filter-not-method "*The_marketplace_reads_the_local_index*"`), the run gives 484 total: 482 passed, 1 skipped (the opt-in two-windows test), 1 failed (test 1).
3. **For the F-key bar buttons:**
   - Already in the registry: F5 `file.copyToOtherPane`, F6 `file.moveToOtherPane`, F7 `file.newFolder`.
   - The handout's F3 "`editor.open`" does not exist; `file.openInOtherPane` is the nearest.
   - F4 Edit, the F8 alias and Alt+F1 Drive arrive with sub-phase 11a.

## Known gaps
- The shell's half (applying the metrics and chrome) is not built. docs/themes.md points to docs/ui.md "for once it is".
- `backdropOpacity` below 0.86 (plain Mica) is allowed by its 0-1 bounds. How to draw a value below that is the shell's choice.

## Noticed out of scope
- The shell's `UiThread.Post` should tolerate a finished queue. Otherwise one failing end-to-end assertion aborts the whole UI run.
- `docs/PLAN.md` line 160 still says 51 commands; there are 52.

## Ready text
**docs/PLAN.md**, under Phase 9:

**Commander Compact, the core's part, 2026-09-29.** The creator's Commander Compact handout, now in `docs/design/compact/`, is a density preset of the default look: 20 px rows, hairlines instead of cards, striped lists and an F-key bar, in the same colours. Its rule is the format's rule: a theme may change colours, sizes and the presence of chrome elements, never commands, keys or the layout. The theme format gains two optional objects: `metrics`, 76 named sizes (one for every value of the handout's Metrics section, with bounds: text 8 to 32 px, rows and bars 14 to 80 px, and so on), and `chrome` (`fkeyBar`, `rowStripes`, `hairlines`). Reading stays strict: an unknown name, a value outside its bounds or pixels with a fraction refuse the theme with the reason. `get_theme` and `theme_changed` carry both objects as the file has them, and `list_themes` says `has_metrics`. All of it is optional, so protocol 11 stays; the schema's `$id` names the format, `urn:cabinetos:theme:2`. `commander-compact` ships as the fifth theme: the default theme's colours, the handout's acrylic tint, every metric and all three chrome elements; `cabinetos-cli themes list` marks it `compact`. The shell applies the objects in its half. Two of its end-to-end tests pin four shipped themes and wait for that half. 564 core tests.

**README:** in "(`core/`, 554 tests)", change 554 to **564**. The UI count stays 485.

## Files
Under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\ unless the path is given in full.
- Created:
  - docs\design\compact\COMPACT_THEME.md
  - docs\design\compact\CabinetOS Compact.dc.html
  - sdk\themes\commander-compact.json
- Changed code:
  - core\crates\cabinetos-protocol\src\theme.rs, lib.rs, schema.rs, message.rs (the wire-form test)
  - core\crates\cabinetos-themes\src\lib.rs
  - core\crates\cabinetos-cli\src\themes.rs
  - core\crates\cabinetos-core\tests\themes.rs
- Regenerated schemas:
  - sdk\themes\theme.schema.json
  - sdk\protocol\response.schema.json, event.schema.json
- Changed docs and scripts:
  - docs\themes.md (a new "Metrics and chrome" section, the shipped-themes row, the CLI mark)
  - docs\ipc.md (`has_metrics`, the objects in `theme`, the version-11 list)
  - docs\design\ABOUT.md, sdk\README.md
  - sdk\marketplace\build-index.ps1 (comment only; CRLF kept)

Next in my queue: sub-phase 11a, the core's part, then the theme-collection test.
