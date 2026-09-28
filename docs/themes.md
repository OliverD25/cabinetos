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
  `rose-pine-moon`. The core writes each one that is missing into the
  folder at every start.
- Change the theme with `set_value` on `ui.theme` (the settings UI, or
  `cabinetos-cli config set ui.theme nord`), or edit `cabinetos.json`.
- Every client that said `hello` gets `theme_changed` with the whole theme
  when the theme in effect changes, and when its file is saved.
- A theme that is not valid is never applied, not even in part: the last
  good theme stays, and a `config_error` event says why.

## The folder

| What | Default | Command-line flag of `cabinetos-core` | Environment variable |
|---|---|---|---|
| The themes | `%LOCALAPPDATA%\CabinetOS\themes\<id>.json` | `--themes-dir <path>` | `CABINETOS_THEMES_DIR` |

The flag wins over the variable. At every start the core creates the
folder, writes each shipped theme whose file is missing, and keeps
`theme.schema.json` next to the themes, so an editor completes and checks
the keys (a theme file starts with `"$schema": "./theme.schema.json"`). A
shipped theme the user edited stays as it is; one the user deleted comes
back at the next start. A theme installed from the marketplace lands here
too ([marketplace.md](marketplace.md)); it applies at once when `ui.theme`
names it.

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
| `kind` | `dark` or `light`. |
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
| `default` | Default | the Windows accent | plain Mica | The design tokens of [design/README.md](design/README.md); the terminal uses the Windows console's Campbell scheme |
| `nord` | Nord | `#88C0D0` | `#2E3440` at 0.88 | The Nord palette (MIT) |
| `catppuccin-mocha` | Catppuccin Mocha | `#CBA6F7` | `#1E1E2E` at 0.9 | The Catppuccin Mocha palette (MIT) |
| `rose-pine-moon` | Rosé Pine Moon | `#EBBCBA` | `#232136` at 0.9 | The Rosé Pine Moon palette (MIT) |

The accents and tints are the design's. `default` leaves `accent` and
`mica` `null`: the design's `#60CDFF` is the Windows default accent in dark
mode and its `rgba(32,32,32,.86)` only imitates Mica in the prototype, so
the default theme follows the system instead. Each named theme keeps an
attribution line for the palette it uses.

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

## Not yet

- There are no light themes yet; `kind` is there for them.
- A new version of a shipped theme does not replace a copy already in the
  folder; delete the file to get the new one at the next start.
