# CabinetOS · Commander Compact theme

## Changelog
- v1 · 2026-10-01 · Commander Compact density preset, F-key bar, row stripes


Density preset for CabinetOS modelled on Total Commander: hairline separators instead of floating cards, 20 px file rows, striped lists, an F-key bar. Same Fluent palette, fonts and behaviour as the default theme; only metrics, radii and separators change. Reference build: `CabinetOS Compact.dc.html`.

## Scope

A theme in CabinetOS may change colors, metrics and the presence of chrome elements (F-key bar, stripes). It may not change commands, keybindings or layout takes. Everything below applies on top of the default theme documented in README.md; anything not listed is inherited.

## Metrics (default → compact)

Global
- Base font size: 13 → 12 px, line-height 1.4 → 1.3
- Backdrop opacity: .86 → .94 (less Mica bleed behind dense text)
- Corner radius on inner controls: 4 → 2 px; panes, sidebar, terminal, marketplace: 8 → 0 px
- Container gaps: 8 → 0 px; body padding 0 8 8 → 0

Title bar
- Height: 40 → 30 px
- Workspace tabs: 32 → 24 px tall, padding 0 14 → 0 10, min-width 96 → 80, font 12 → 11 px, radius 8 8 0 0 → 3 3 0 0
- Caption buttons: 46 → 40 px wide

Command bar
- Height: 48 → 32 px; hairline top and bottom (rgba(255,255,255,.06)); fill rgba(255,255,255,.03)
- Icon buttons: 32 → 26 px square
- Address and search fields: 32 → 24 px tall, radius 2 px
- Dual/Single toggle: 32 → 26 px tall

Sidebar
- Width: clamp(180px, 20%, 224px) → clamp(150px, 17%, 190px); 1 px right hairline (.08)
- Section headers: 11 → 10 px, padding 14 12 4 → 8 8 2
- Rows (workspaces, pinned): 34/32 → 22 px, full-bleed (no 4 px inset), radius 0
- Selection bar: 3 → 2 px wide
- Drive rows: padding 6 10 → 3 8; free-space label shortened ("118 GB")
- Tag chips: pills → 2 px radius, 11 px

Panes
- No gap between panes; adjacent borders overlap by 1 px (`margin-left:-1px`)
- Pane header: 36 → 24 px; active pane header fill rgba(255,255,255,.06)
- Column header: padding 4 14 → 2 8; fill rgba(255,255,255,.03); bottom hairline .10
- Grid: `minmax(0,1.6fr) minmax(0,.9fr) minmax(0,.7fr) 60px`, column-gap 8 px
- File rows: 30 → 20 px, padding 0 8, radius 0, icon gap 10 → 6 px
- Secondary columns: 12 → 11 px; Size column in Fira Code with tabular figures
- Row stripes: odd rows rgba(255,255,255,.025) (toggleable)
- Selected row: rgba(255,255,255,.12) fill + 2 px accent bar

Editors
- Editor tab strip: 36 → 26 px
- Markdown padding: 28 40 → 14 20, line-height 1.6 → 1.5
- Hex rows: 22 → 18 px, column gap 18 → 14

Terminal
- Bottom dock: clamp(120px, 30%, 240px) → clamp(100px, 26%, 200px); top hairline only
- Right dock: left hairline only
- Header: 34 → 24 px; session tabs 26 → 20 px
- Body padding 10 12 → 6 8, line-height 1.6 → 1.45

Marketplace
- Sidebar tabs: 32 → 24 px, radius 0
- Card grid gap: 10 → 4 px; card padding 14 → 8 10, radius 2 px

Overlays
- Palette rows: 36 → 28 px; context-menu rows: 32 → 26 px
- Acrylic tint: rgba(44,44,44,.72) → rgba(38,38,38,.9)

Status bar
- Height: 26 → 22 px; padding 0 14 → 0 8; gap 16 → 12
- Layout label prefixed "Commander compact · "

## F-key bar (new)

Sits between the body and the status bar. 24 px tall, 1 px gap between buttons, top hairline, fill rgba(255,255,255,.03). Buttons stretch equally (`flex:1`), fill rgba(255,255,255,.05), hover .12, radius 2 px, 11 px text. Key in Fira Code and accent color, label in white at .8.

| Key | Label | Command |
|---|---|---|
| F3 | View | Open selected file in the other pane (`editor.open`) |
| F4 | Edit | Open in external editor |
| F5 | Copy | `file.copyToOtherPane` |
| F6 | Move | `file.moveToOtherPane` |
| F7 | Mkdir | `file.newFolder` |
| F8 | Delete | Send selection to Recycle Bin |
| Alt+F1 | Drv | Switch drive in active pane |

Labels are kept short so the bar fits at ~900 px. Alt+F4 Exit is deliberately omitted; the caption button covers it.

## Theme settings

Exposed in the theme manifest and the Tweaks panel:
- `fkeyBar` boolean, default true
- `rowStripes` boolean, default true
- Inherits `accent`, `layout`, `terminalOpen`

## Acceptance

- Dual pane at 924 px: no filename, date or type value clips; F-key labels fully visible
- Row height exactly 20 px; 20 rows visible in a 400 px-tall pane
- No element uses radius above 3 px except the palette and context-menu surfaces
- Keyboard map unchanged from README.md
