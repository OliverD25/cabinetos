# Colour themes

A theme is one JSON file that sets the accent colour, the tint over the
Mica backdrop, the colours of the window and the colours of the terminal.
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
- Four themes ship with the core: `default`, `nord`, `catppuccin-mocha` and
  `rose-pine-moon`. At every start the core writes each one that is
  missing into the folder, and brings each copy the user never changed up
  to the version it ships.
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

## The shipped themes

| ID | Name | Accent | Mica tint | Source of the colours |
|---|---|---|---|---|
| `default` | Default | the Windows accent | plain Mica | The design tokens of [design/README.md](design/README.md); the terminal uses the Windows console's Campbell scheme. Its kind is `system` |
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

`themes list` marks the theme in effect with `*`. `themes show` prints a
whole theme as JSON: the one named, or the one in effect.

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
