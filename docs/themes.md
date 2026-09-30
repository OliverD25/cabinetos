# Colour themes

A theme is one JSON file that sets the accent colour, the tint over the
Mica backdrop, the colours of the window and the colours of the terminal,
and may set the window's sizes and turn on three of its elements
("Metrics and chrome").
The core reads it and sends it whole to the UI, which applies it without a
restart. Constitution Articles 6 (Universal Configuration: a theme is a
file the user may edit, and a saved edit applies at once) and 8 (the
JSON-based theme engine).

The format is the crate `core/crates/cabinetos-protocol` (`Theme`); the
folder and the checks are `core/crates/cabinetos-themes`. The schema, for
editors: [sdk/themes/theme.schema.json](../sdk/themes/theme.schema.json).
How the window applies a theme, and its theme picker:
[ui.md](ui.md), "Themes".

## At a glance

- A theme is `<themes folder>\<id>.json`. `ui.theme` in `cabinetos.json`
  names the theme in effect; the default is `default`.
- Five themes ship with the core: `default`, its density preset
  `commander-compact`, `nord`, `catppuccin-mocha` and `rose-pine-moon`. At
  every start the core writes each one that is missing into the folder,
  and brings each copy the user never changed up to the version it ships.
- Change the theme with `set_value` on `ui.theme` (the settings UI, or
  `cabinetos-cli config set ui.theme nord`), or edit `cabinetos.json`.
- Every client that said `hello` gets `theme_changed` with the whole theme
  when the theme in effect changes, and when its file is saved.
- A theme that is not valid is never applied, not even in part: the last
  good theme stays, and a `config_error` event says why.
- 36 more themes, ported from the most popular editor themes, are in
  `sdk/themes/collection` for the marketplace, not in the core (see "The
  collection").

## The folder

| What | Default | Command-line flag of `cabinetos-core` | Environment variable |
|---|---|---|---|
| The themes | `%LOCALAPPDATA%\CabinetOS\themes\<id>.json` | `--themes-dir <path>` | `CABINETOS_THEMES_DIR` |

The flag wins over the variable. At every start the core creates the
folder, writes each shipped theme whose file is missing, and keeps
`theme.schema.json` next to the themes, so an editor completes and checks
the keys (a theme file starts with `"$schema": "./theme.schema.json"`). A
theme installed from the marketplace lands here too
([marketplace.md](marketplace.md)); it applies at once when `ui.theme`
names it.

**Shipped themes and edits.** A shipped theme follows the version this
core ships until the user edits it; an edit is never overwritten:

| The file in the folder | At the next start |
|---|---|
| Missing (never written, or deleted) | Written |
| Byte for byte a version the core wrote (an older one, say `default` 1.0.0) | Replaced by the version this core ships; the log says `updated an unedited shipped theme` |
| Anything else (the user edited it) | Kept; the log says `keeping the edited copy of a shipped theme` |

The core tells an unedited copy by its SHA-256. `.shipped.json` in the
folder records the hash of every shipped file the core wrote there, by
theme ID; versions that shipped before the record existed are known to
the core itself (`default` 1.0.0). An editor that saves a file unchanged
but with other line endings makes it an edit. Every file is replaced
through a temporary file and a rename, so the themes watcher never reads
half a theme.

## The format

```json
{
  "$schema": "./theme.schema.json",
  "id": "nord",
  "name": "Nord",
  "author": "CabinetOS",
  "attribution": "Colours from the Nord palette by Sven Greb and Arctic Ice Studio (https://www.nordtheme.com), used under the MIT License.",
  "version": "1.0.0",
  "kind": "dark",
  "accent": "#88C0D0",
  "mica": { "tint": "#2E3440", "opacity": 0.88 },
  "palette": {
    "textPrimary": "#ECEFF4",
    "textSecondary": "#D8DEE9",
    "textTertiary": "#D8DEE98B",
    "textDisabled": "#D8DEE95D",
    "layerFill": "#3B425280",
    "layerStroke": "#434C5E99",
    "layerStrokeActive": "#4C566A",
    "controlFill": "#3B4252B3",
    "controlFillHover": "#434C5EB3",
    "acrylicTint": "#2E3440B8",
    "terminalBackground": "#2E344099",
    "folderIcon": "#EBCB8B",
    "folderIconFront": "#F2DDB4",
    "fileTypeColors": { "md": "#88C0D0", "rs": "#D08770", "toml": "#B48EAD", "exe": "#A3BE8C",
                        "dll": "#A3BE8C", "bin": "#BF616A", "pdf": "#BF616A", "zip": "#EBCB8B" },
    "permissionLow": "#A3BE8C",
    "permissionMedium": "#EBCB8B",
    "permissionHigh": "#BF616A"
  },
  "terminal": {
    "foreground": "#D8DEE9",
    "background": "#2E3440",
    "cursor": "#D8DEE9",
    "ansi": ["#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
             "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"]
  }
}
```

| Key | Value |
|---|---|
| `$schema` | Optional. For editors; the core ignores it and never sends it. |
| `id` | 1 to 64 lower case letters, digits and `-`, starting with a letter. It must be the file's name without `.json`. |
| `name` | The name in the theme picker. Not empty. |
| `author` | Who made the theme. Not empty. |
| `attribution` | Optional. Where the colours come from and under which license, when they are someone else's. |
| `version` | `major.minor.patch`. |
| `kind` | `dark`, `light`, or `system`: follow Windows' light or dark mode. With `system` the window decides which, and changes when the user changes Windows; `theme_changed` carries the theme as it is. |
| `accent` | `#RRGGBB`, or `null` (or left out) to follow the Windows accent colour. |
| `mica` | `{ "tint": "#RRGGBB", "opacity": 0 to 1 }`, a tint laid over the Mica backdrop; `null` (or left out) for plain Mica. |
| `palette` | Every key below is required. |
| `terminal` | `foreground`, `background`, `cursor`, and `ansi`: exactly 16 colours, black, red, green, yellow, blue, magenta, cyan, white, then the bright ones in the same order. |
| `metrics` | Optional. The sizes that differ from the default theme's ("Metrics and chrome"). |
| `chrome` | Optional. Which of three elements the window shows: `fkeyBar`, `rowStripes`, `hairlines` ("Metrics and chrome"). |

A colour is `#RRGGBB`, or `#RRGGBBAA` with an alpha part (`00` clear, `FF`
solid) that is laid over what is under it. Letters may be in any case; the
core sends them in upper case.

| Palette key | Where the UI uses it |
|---|---|
| `textPrimary` | Names, titles and everything read first |
| `textSecondary` | Dates, types and sizes |
| `textTertiary` | Section labels, hints and paths |
| `textDisabled` | Commands that cannot run now |
| `layerFill`, `layerStroke` | Panes, cards and sidebar rows: fill and 1 px border |
| `layerStrokeActive` | The border of the focused pane |
| `controlFill`, `controlFillHover` | Buttons and text fields, at rest and under the mouse |
| `acrylicTint` | The palette, context menus and flyouts |
| `terminalBackground` | The terminal panel over the backdrop |
| `folderIcon`, `folderIconFront` | The folder icon: body and front flap |
| `fileTypeColors` | The stroke of file icons for `md`, `rs`, `toml`, `exe`, `dll`, `bin`, `pdf` and `zip` |
| `permissionLow`, `permissionMedium`, `permissionHigh` | The capability levels in the review dialog |

`terminal.background` is the colour scheme's background: reverse video, and
cells drawn where the panel is opaque. Over the translucent panel, cells
with the default background show `palette.terminalBackground`.

## Metrics and chrome

A theme may change colours, sizes, and whether some elements of the window
are there. It never changes a command, a key or the layout: the rule of the
Commander Compact handout
([design/compact/COMPACT_THEME.md](design/compact/COMPACT_THEME.md),
"Scope"). So besides its colours a theme may have two objects, both
optional:

- `metrics`: sizes, a flat object of named numbers. Each one is optional:
  a metric left out, like the whole object, keeps the default theme's
  value. A density preset such as Commander Compact sets them.
- `chrome`: which of three elements the window shows. Each one left out,
  like the whole object, is off, as in the default theme.

```json
"metrics": { "fontSize": 12, "lineHeight": 1.3, "rowHeight": 20, "sidebarWidthPercent": 17 },
"chrome": { "fkeyBar": true, "rowStripes": true, "hairlines": true }
```

Reading is strict, as for the colours. The theme is refused, with a message
that names the problem, for:

- an unknown name in either object (`unknown field `rowHight``);
- a value outside its metric's bounds (`metrics.rowHeight: 10 is not from
  14 to 80`);
- pixels with a fraction (`20.5`, and `20.0` too: pixels are written whole);
- a lower limit above its upper limit: `sidebarMinWidth` above
  `sidebarMaxWidth`, or `terminalDockMinHeight` above
  `terminalDockMaxHeight`. When a theme sets only one of the two, the
  other is the default theme's.

The core carries both objects as the file has them, in `get_theme` and
`theme_changed`. `list_themes` says `has_metrics: true` for a theme that
sets any metric, so the picker can mark the density presets. The shell
applies them live, as it applies the colours: each metric is one size of
the window, and each chrome key one element. [ui.md](ui.md), "Metrics and
chrome", says where each one lands, which of the handout's text choices
each switch carries, and the few sizes the window cannot take.

### Metrics

Every value of the handout's "Metrics" section has a name, in the
handout's order. `px` are whole device-independent pixels, written without
a fraction. A `number` may have one: a line height is a multiple of the
text size, an opacity goes from 0 to 1, and a column's weight is its share
against the other weighted columns, as `1.6*` is in a XAML grid. `%` is a
share of the window's width (the sidebar) or height (the bottom terminal
dock), kept between the metric's lower and upper limits in pixels.
"Default" is the default theme's value ([design/README.md](design/README.md)),
which a theme that leaves the metric out gets. "Compact" is Commander
Compact's. The default theme has no function-key bar; its sizes in
brackets are the handout's, for any theme that shows the bar.

| Metric | Unit | From | To | Default | Compact | What it sizes |
|---|---|---|---|---|---|---|
| `fontSize` | px | 8 | 32 | 13 | 12 | The base text size of the window. |
| `lineHeight` | number | 1 | 2.5 | 1.4 | 1.3 | The line height of the window's text, as a multiple of its size. |
| `backdropOpacity` | number | 0 | 1 | 0.86 | 0.94 | How much the window's backdrop covers the desktop behind it: 0.86 is plain Mica, the default look; more lets less of the desktop show through behind dense text. |
| `radiusControl` | px | 0 | 16 | 4 | 2 | The corner radius of buttons, fields and other inner controls. |
| `radiusSurface` | px | 0 | 16 | 8 | 0 | The corner radius of the panes, the sidebar, the terminal and the marketplace. |
| `gap` | px | 0 | 48 | 8 | 0 | The space between the window's surfaces: the sidebar, the panes and the docks. |
| `bodyPadding` | px | 0 | 64 | 8 | 0 | The space between the window's edges and its surfaces, at the sides and the bottom. |
| `hairlineOpacity` | number | 0 | 1 | 0.06 | 0.08 | The opacity of the 1 px lines under the top row, the tab strips and the breadcrumb rows, and between tabs, in the theme's text colour. |
| `topRowHeight` | px | 14 | 80 | 40 | 32 | The height of the top row: the menu, the workspace pill, the command center, the view buttons and the caption buttons. Never lower than 32 px on screen: Windows draws the caption buttons that high. |
| `topRowButtonSize` | px | 14 | 80 | 36 | 26 | The width and height of the top row's menu and view buttons. |
| `workspacePillHeight` | px | 14 | 80 | 24 | 22 | The height of the workspace pill. |
| `workspacePillRadius` | px | 0 | 16 | 4 | 2 | The corner radius of the workspace pill. |
| `commandCenterHeight` | px | 14 | 80 | 24 | 22 | The height of the command center. |
| `commandCenterRadius` | px | 0 | 16 | 4 | 3 | The corner radius of the command center. |
| `titleBarHeight` | px | 14 | 80 | 40 | 30 | Nothing since Phase 16: the height of the title bar, which the top row replaced. |
| `tabHeight` | px | 14 | 80 | 32 | 24 | Nothing since Phase 16: the height of the title bar's workspace tab. |
| `tabPaddingX` | px | 0 | 64 | 14 | 10 | Nothing since Phase 16: the space at each side of the workspace tab's text. |
| `tabMinWidth` | px | 40 | 240 | 96 | 80 | Nothing since Phase 16: the narrowest the workspace tab got. |
| `tabFontSize` | px | 8 | 32 | 12 | 11 | The text size of a tab in a pane's tab strip. |
| `tabRadius` | px | 0 | 16 | 0 | 3 | The radius of a tab's two top corners. The default look has square tabs. |
| `captionButtonWidth` | px | 24 | 96 | 46 | 40 | The width of the minimize, maximize and close buttons. |
| `commandBarHeight` | px | 14 | 80 | 48 | 32 | Nothing since Phase 16: the height of the command bar. |
| `iconButtonSize` | px | 14 | 80 | 32 | 26 | Nothing since Phase 16: an icon button in the command bar. |
| `fieldHeight` | px | 14 | 80 | 32 | 24 | Nothing since Phase 16: the address and search fields. |
| `toggleHeight` | px | 14 | 80 | 32 | 26 | Nothing since Phase 16: the dual and single pane toggle. |
| `sidebarMinWidth` | px | 100 | 600 | 180 | 150 | The narrowest the sidebar gets. |
| `sidebarWidthPercent` | % | 5 | 50 | 20 | 17 | The sidebar's width as a share of the window's, kept between its narrowest and widest. |
| `sidebarMaxWidth` | px | 100 | 600 | 224 | 190 | The widest the sidebar gets. |
| `sidebarHeaderFontSize` | px | 8 | 32 | 11 | 10 | The text size of a sidebar section's label. |
| `sidebarHeaderPaddingTop` | px | 0 | 64 | 14 | 8 | The space above a sidebar section's label. |
| `sidebarHeaderPaddingX` | px | 0 | 64 | 12 | 8 | The space at each side of a sidebar section's label. |
| `sidebarHeaderPaddingBottom` | px | 0 | 64 | 4 | 2 | The space below a sidebar section's label. |
| `sidebarRowHeight` | px | 14 | 80 | 34, 32 | 22 | The height of a workspace row (34 by default) and of a pinned folder's row (32) in the sidebar. |
| `sidebarRowInset` | px | 0 | 16 | 4 | 0 | The space between a sidebar row and the sidebar's edges; 0 makes the rows full width. |
| `sidebarRowRadius` | px | 0 | 16 | 4 | 0 | The corner radius of a sidebar row. |
| `selectionBarWidth` | px | 0 | 8 | 3 | 2 | The width of the accent bar at the left of a selected row, in the sidebar and in the file lists. |
| `driveRowPaddingY` | px | 0 | 64 | 6 | 3 | The space above and below a drive row's content in the sidebar. |
| `driveRowPaddingX` | px | 0 | 64 | 10 | 8 | The space at each side of a drive row's content in the sidebar. |
| `tagRadius` | px | 0 | 16 | 12 | 2 | The corner radius of a tag chip in the sidebar. |
| `tagFontSize` | px | 8 | 32 | 12 | 11 | The text size of a tag chip. |
| `paneHeaderHeight` | px | 14 | 80 | 36 | 24 | Nothing since Phase 16: the height of the pane's header, which the tab strip and the breadcrumb row replaced. |
| `tabRow` | px | 14 | 80 | 32 | 24 | The height of a pane's tab strip, at the top of the pane; it shows from the first tab. |
| `breadcrumbRowHeight` | px | 14 | 80 | 28 | 22 | The height of a pane's breadcrumb row, under its tab strip: Back, Forward, Up and the folder's path. |
| `navButtonSize` | px | 14 | 80 | 20 | 20 | The width and height of the breadcrumb row's Back, Forward and Up. |
| `columnHeaderPaddingY` | px | 0 | 64 | 4 | 2 | The space above and below the column headers' text. |
| `columnHeaderPaddingX` | px | 0 | 64 | 14 | 8 | The space at each side of the column headers' row. |
| `nameColumnWeight` | number | 0.1 | 10 | 1 | 1.6 | The Name column's share of a file list's width, against the other weighted columns. |
| `modifiedColumnWeight` | number | 0.1 | 10 | 0.55 | 0.9 | The Modified column's share, against the other weighted columns. |
| `typeColumnWeight` | number | 0.1 | 10 | 0.45 | 0.7 | The Type column's share, against the other weighted columns. |
| `nameColumnMinWidth` | px | 0 | 400 | 120 | 0 | The narrowest the Name column gets. |
| `sizeColumnWidth` | px | 32 | 160 | 64 | 60 | The width of the Size column. |
| `columnGap` | px | 0 | 48 | 0 | 8 | The space between a file list's columns. |
| `rowHeight` | px | 14 | 80 | 30 | 20 | The height of a row in a file list. |
| `rowPaddingX` | px | 0 | 64 | 10 | 8 | The space at each side of a file row's content. |
| `rowRadius` | px | 0 | 16 | 4 | 0 | The corner radius of a file row's selection and hover fill. |
| `rowIconGap` | px | 0 | 48 | 10 | 6 | The space between a file row's icon and its name. |
| `secondaryFontSize` | px | 8 | 32 | 12 | 11 | The text size of a file row's Modified, Type and Size columns. |
| `editorTabHeight` | px | 14 | 80 | 36 | 26 | The height of the editor's tab strip. |
| `markdownPaddingY` | px | 0 | 64 | 28 | 14 | The space above and below the Markdown preview's text. |
| `markdownPaddingX` | px | 0 | 64 | 40 | 20 | The space at each side of the Markdown preview's text. |
| `markdownLineHeight` | number | 1 | 2.5 | 1.6 | 1.5 | The Markdown preview's line height, as a multiple of its text size. |
| `hexRowHeight` | px | 14 | 80 | 22 | 18 | The height of a row in the hex view. |
| `hexColumnGap` | px | 0 | 48 | 18 | 14 | The space between the hex view's columns. |
| `terminalDockMinHeight` | px | 60 | 800 | 120 | 100 | The lowest the terminal gets when it is docked at the bottom. |
| `terminalDockHeightPercent` | % | 10 | 80 | 30 | 26 | The bottom-docked terminal's height as a share of the window's, kept between its lowest and highest. |
| `terminalDockMaxHeight` | px | 60 | 800 | 240 | 200 | The highest the terminal gets when it is docked at the bottom. |
| `terminalHeaderHeight` | px | 14 | 80 | 34 | 24 | The height of the terminal's header. |
| `terminalTabHeight` | px | 14 | 80 | 26 | 20 | The height of a terminal session's tab. |
| `terminalPaddingY` | px | 0 | 64 | 10 | 6 | The space above and below the terminal's text. |
| `terminalPaddingX` | px | 0 | 64 | 12 | 8 | The space at each side of the terminal's text. |
| `terminalLineHeight` | number | 1 | 2.5 | 1.6 | 1.45 | The terminal's line height, as a multiple of its text size. |
| `marketplaceTabHeight` | px | 14 | 80 | 32 | 24 | The height of a tab in the marketplace's side list. |
| `marketplaceTabRadius` | px | 0 | 16 | 4 | 0 | The corner radius of a marketplace tab. |
| `marketplaceCardGap` | px | 0 | 48 | 10 | 4 | The space between the marketplace's cards. |
| `marketplaceCardPaddingY` | px | 0 | 64 | 14 | 8 | The space above and below a marketplace card's content. |
| `marketplaceCardPaddingX` | px | 0 | 64 | 14 | 10 | The space at each side of a marketplace card's content. |
| `marketplaceCardRadius` | px | 0 | 16 | 8 | 2 | The corner radius of a marketplace card. |
| `paletteRowHeight` | px | 14 | 80 | 36 | 28 | The height of a row in the command palette. |
| `menuRowHeight` | px | 14 | 80 | 32 | 26 | The height of an item in a context menu. |
| `dropdownRowHeight` | px | 14 | 80 | 26 | 26 | The height of a row in the top row's dropdowns: the menu and the workspace pill's list. |
| `statusBarHeight` | px | 14 | 80 | 26 | 22 | The height of the status bar. |
| `statusBarPaddingX` | px | 0 | 64 | 14 | 8 | The space at each side of the status bar's content. |
| `statusBarGap` | px | 0 | 48 | 16 | 12 | The space between the status bar's items. |
| `fkeyBarHeight` | px | 14 | 80 | (24) | 24 | The height of the function-key bar, when `chrome.fkeyBar` shows it. |
| `fkeyBarGap` | px | 0 | 48 | (1) | 1 | The space between the function-key bar's buttons. |
| `fkeyButtonRadius` | px | 0 | 16 | (2) | 2 | The corner radius of a function-key button. |
| `fkeyBarFontSize` | px | 8 | 32 | (11) | 11 | The text size of the function-key bar. |

The ten metrics of the shell redesign (Phase 16, the creator's
`SHELL_REDESIGN.md`) come with the default look's value and Commander
Compact's, which the handout gives in brackets. The nine of the shell
before it (`titleBarHeight`, the four `tab…` of the workspace tab but
`tabFontSize` and `tabRadius`, `commandBarHeight`, `iconButtonSize`,
`fieldHeight`, `toggleHeight` and `paneHeaderHeight`) are still read, so a
theme written before stays valid, and size nothing. The handout lets a
theme set the top row's height and fill, the tab strip's and breadcrumb
row's heights, the radii, the hairlines' opacity and the accent; it may
not remove the tab strip, the breadcrumb row, the workspace pill or the
command center, and no theme changes a key.

The bounds, by kind: text sizes 8 to 32 px; line heights 1 to 2.5; the
heights of rows, bars and controls 14 to 80 px; corner radii 0 to 16 px;
gaps 0 to 48 px; paddings 0 to 64 px; column weights 0.1 to 10; the rest as
the table says. They keep a theme usable, not pretty: a theme may still set
a small text in a tall row.

The handout also changes things that are not sizes, and the theme does not
carry them:

- The hairlines', the stripes' and the function-key buttons' shades, and
  the active pane header's fill: the shell draws the chrome with the
  handout's shades when `chrome` turns it on.
- The Size column in Fira Code, the drive's shorter free-space label
  ("118 GB") and the status bar's "Commander compact ·" prefix: text and
  type choices, which are the shell's. The shell ties the first two to
  `hairlines` and writes the prefix with the preset's own name
  ([ui.md](ui.md), "Metrics and chrome").
- One colour, the overlays' acrylic tint: Commander Compact's palette has
  it (`acrylicTint` `#262626E6` instead of the default's `#2C2C2CB8`).

### Chrome

| Key | When `true` | Left out |
|---|---|---|
| `fkeyBar` | The function-key bar between the panes and the status bar: F3 View, F4 Edit, F5 Copy, F6 Move, F7 Mkdir, F8 Delete, Alt+F1 Drive. Its buttons run the commands those keys run; the theme only shows the bar, it never adds or changes a key. | No bar |
| `rowStripes` | Every other row of a file list a shade lighter | Plain rows |
| `hairlines` | 1 px separators between the window's surfaces instead of floating cards; with `gap` and `radiusSurface` at 0, the surfaces meet at the hairlines | Floating cards |

### The format's version

The schema's `$id` names the format's version: `urn:cabinetos:theme:2`.
Version 1 had the colours only; version 2 adds `metrics` and `chrome`.
Both are optional, so every version-1 theme is a valid version-2 theme,
and a program that knows only version 1 reads the colours as before. The
messages keep protocol version 11: the new keys and `has_metrics` are
optional ([ipc.md](ipc.md), "What changes the version").

## The shipped themes

| ID | Name | Accent | Mica tint | Source of the colours |
|---|---|---|---|---|
| `default` | Default | the Windows accent | plain Mica | The design tokens of [design/README.md](design/README.md); the terminal uses the Windows console's Campbell scheme. Its kind is `system` |
| `commander-compact` | Commander Compact | the Windows accent | plain Mica | The default theme's colours, with the handout's acrylic tint, and every metric and chrome element of [design/compact/COMPACT_THEME.md](design/compact/COMPACT_THEME.md). Its kind is `system` |
| `nord` | Nord | `#88C0D0` | `#2E3440` at 0.88 | The Nord palette (MIT) |
| `catppuccin-mocha` | Catppuccin Mocha | `#CBA6F7` | `#1E1E2E` at 0.9 | The Catppuccin Mocha palette (MIT) |
| `rose-pine-moon` | Rosé Pine Moon | `#EBBCBA` | `#232136` at 0.9 | The Rosé Pine Moon palette (MIT) |

The accents and tints are the design's. `default` leaves `accent` and
`mica` `null`: the design's `#60CDFF` is the Windows default accent in dark
mode and its `rgba(32,32,32,.86)` only imitates Mica in the prototype, so
the default theme follows the system instead. It follows Windows' light or
dark mode too (`kind: "system"`). Its palette holds the design's tokens,
which are dark-mode ones; what it shows in light mode is the window's
choice ([ui.md](ui.md), "Themes"). Each named theme keeps an attribution
line for the palette it uses; their palettes are dark.

`commander-compact` is the creator's own design, so it ships with the core;
the collection below is for other people's palettes. It keeps `mica`
`null`, as `default` does. The handout raises the backdrop's opacity from
.86 to .94, but the format's opacity belongs to a Mica tint (`mica.tint`
with `mica.opacity`), and a tint fixes a colour that Windows' light mode
cannot follow. So the .94 is the metric `backdropOpacity`, which the shell
applies to its backdrop.

Two cores that start at the same moment on an empty themes folder (two
windows opened together) both write the shipped themes. Each writes a
temporary file named with its process ID and renames it into place, so
every theme file is whole whichever rename comes last, and both write the
same bytes (tested five times over with two real cores).

## Live editing

The core watches the themes folder. When the file of the theme in effect is
saved, the core reads it again within a second:

- a valid file becomes the theme in effect, and every client gets
  `theme_changed` with it;
- a file with an error is reported with a `config_error` event that names
  the file and the problem (`line` and `column` of the event are `null`;
  the position is in the message), and the theme in effect stays.

The same happens when `ui.theme` changes: a theme with no valid file keeps
the last good theme and brings a `config_error` event. `set_value` refuses
such a theme outright, so the configuration file never names it because of
a client. When the core starts and `ui.theme` names a theme it cannot use,
the shipped default (kept in memory) is in effect and the log says why.
Each problem is reported once; once it is fixed, `theme_changed` follows.

The messages (`list_themes`, `get_theme`, `theme_changed`) are in
[ipc.md](ipc.md), "Colour themes".

## The command line

```text
cabinetos-cli themes list
cabinetos-cli themes show [<id>]
cabinetos-cli config set ui.theme nord
cabinetos-cli events watch
```

`themes list` marks the theme in effect with `*` and each density preset
(a theme that sets metrics) with `compact`. `themes show` prints a whole
theme as JSON: the one named, or the one in effect, with its `metrics` and
`chrome`.

## The collection

`sdk/themes/collection` holds 36 themes ported from 27 of the 30 most
popular editor themes (the creator's list of 2026-09-28): GitHub, One Dark
Pro, Dracula, Material Theme, Ayu, Monokai, Night Owl, Tokyo Night,
Solarized, Gruvbox, Catppuccin and more, with their well-known light and
dark variants. 27 are dark and 9 are light. The folder has:

- `<id>.json`: one theme per file, in the format above.
- [README.md](../sdk/themes/collection/README.md): the list, with each
  theme's kind and source, how a palette becomes a theme, and the contrast
  of each.
- [NOTICES.md](../sdk/themes/collection/NOTICES.md): the source, author,
  license and exact commit of every theme, what the port derives, and
  what was left out and why (City Lights, whose license forbids ports;
  Dainty, whose colours are not published anywhere).
- `marketplace.csv`: each theme's line in the marketplace: `id`,
  `license`, `source` and `description`.

**Why not in the core.** Constitution Article 10 (the Zero-Bloat
Foundation): the core ships the four themes above, and everything else is
opt-in through the marketplace. The core neither embeds these files nor
writes them into the themes folder. A user installs the themes they want,
one by one.

**Installing one.** In the marketplace view: the Themes tab, then "Install
and apply". From the command line:

```text
cabinetos-cli market install dracula
cabinetos-cli config set ui.theme dracula
```

A theme installed this way lands in the themes folder like any other and
can be uninstalled again (`cabinetos-cli market uninstall dracula`, once
another theme is in effect). The marketplace needs an index that offers
the collection. The public index does not exist yet
([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)); when
the creator publishes it, the collection goes with it. Until then,
`build-index.ps1` builds a local one with `-Collection`:

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder> -Collection
cabinetos-cli config set marketplace.index "<folder>"
cabinetos-cli market refresh
```

Without `-Collection` the script builds the index it always did: the
fixture plugins and the four shipped themes. With it, each collection
theme becomes one more item, in the order of `marketplace.csv`, whose row
gives the item's description, license and source link (the marketplace
view's Source button opens it). Copying a file by hand into the themes
folder works too; the marketplace then leaves that theme alone, as it
does a shipped one ([marketplace.md](marketplace.md), trust rule 7).

**How the colours were chosen.** Every colour comes from the theme's own
repository at a fixed commit, under an open license (all MIT). The same
mapping serves every theme: the backdrop's Mica tint is the darkest of the
source's editor, side bar and panel backgrounds; the panes are a step
lighter; the accent is the theme's signature colour; the terminal gets the
theme's terminal colours. Every theme reaches 4.5:1 for body text as the
window draws it: row names and details on a plain, hovered and selected
row, the status bar, menus, dialogs and the terminal. Where a theme's own
muted colours are too dim for that, the collection uses its main text at
a lower alpha instead; the README lists each case.

**Adding a theme.**

1. Take the colours only from a source under an open license (MIT, BSD,
   Apache-2.0, CC0, or terms of the theme's own that allow a port), and
   read its license file at a fixed commit. A theme whose license cannot
   be established stays out.
2. Write `sdk/themes/collection/<id>.json` in the format above: `version`
   `1.0.0`, `author` as "<the palette's author>, port by CabinetOS", and an
   `attribution` that names the source and its license. The ID must not
   be a shipped theme's.
3. Check it: a core with `CABINETOS_THEMES_DIR` at a scratch copy of the
   collection lists it (`cabinetos-cli themes list`) and applies it
   (`cabinetos-cli config set ui.theme <id>`), and its text reaches the
   contrast above.
4. Add its row to `NOTICES.md` (source, files read, copyright notice,
   license, version and commit) and to `README.md`, and its line to
   `marketplace.csv`. `build-index.ps1 -Collection` stops when a theme has
   no line there.

## Not yet

- No light theme ships with the core; the collection has nine.
- The collection can be installed only from a local index until the
  public one exists ([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)).
