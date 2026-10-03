# The theme collection

34 colour themes, ported from 27 of the most popular editor themes, for the
marketplace. They are not part of the core: the core ships seven themes,
and everything else is opt-in (Constitution Article 10, the Zero-Bloat
Foundation). A user installs the ones they want from the marketplace view,
or with `cabinetos-cli market install <id>`.
[docs/themes.md](../../../docs/themes.md), "The collection", says how they
reach the index and how to add one.

- `<id>.json`: one theme per file, in the format of
  [theme.schema.json](../theme.schema.json), the same as the shipped
  themes. Each file's `attribution` names its source and license.
- [NOTICES.md](NOTICES.md): the source, author, license and exact commit of
  every theme, what the port derives, and what was left out and why.
- `marketplace.csv`: the marketplace's line for each theme (`id`,
  `license`, `source`, `description`), which
  [build-index.ps1](../../marketplace/build-index.ps1) reads with
  `-Collection`. Every theme needs a row.

## The themes

The number is the theme's place in the creator's list of the 30 most
popular themes (2026-09-28).

| # | ID | Name | Kind | Source |
|---|---|---|---|---|
| 1 | `github-dark` | GitHub Dark | dark | [primer/github-vscode-theme](https://github.com/primer/github-vscode-theme) |
| 1 | (ships with the core: `github-light`) | GitHub Light | light | |
| 2 | `one-dark-pro` | One Dark Pro | dark | [Binaryify/OneDark-Pro](https://github.com/Binaryify/OneDark-Pro) |
| 3 | `dracula` | Dracula | dark | [dracula/visual-studio-code](https://github.com/dracula/visual-studio-code) |
| 4 | `material-theme` | Material Theme | dark | [SublimeText/material-theme](https://github.com/SublimeText/material-theme) |
| 5 | `ayu-dark` | Ayu Dark | dark | [ayu-theme/vscode-ayu](https://github.com/ayu-theme/vscode-ayu) |
| 5 | `ayu-mirage` | Ayu Mirage | dark | [ayu-theme/vscode-ayu](https://github.com/ayu-theme/vscode-ayu) |
| 5 | `ayu-light` | Ayu Light | light | [ayu-theme/vscode-ayu](https://github.com/ayu-theme/vscode-ayu) |
| 6 | `monokai` | Monokai (classic, not Monokai Pro) | dark | [microsoft/vscode](https://github.com/microsoft/vscode/tree/main/extensions/theme-monokai) |
| 7 | `night-owl` | Night Owl | dark | [sdras/night-owl-vscode-theme](https://github.com/sdras/night-owl-vscode-theme) |
| 8 | `one-monokai` | One Monokai | dark | [azemoh/vscode-one-monokai](https://github.com/azemoh/vscode-one-monokai) |
| 9 | `tokyo-night` | Tokyo Night | dark | [enkia/tokyo-night-vscode-theme](https://github.com/enkia/tokyo-night-vscode-theme) |
| 10 | `solarized-dark` | Solarized Dark | dark | [altercation/solarized](https://github.com/altercation/solarized) |
| 10 | `solarized-light` | Solarized Light | light | [altercation/solarized](https://github.com/altercation/solarized) |
| 11 | (ships with the core: `nord`) | Nord | dark | |
| 12 | `winter-is-coming` | Winter is Coming (Dark Blue) | dark | [johnpapa/vscode-winteriscoming](https://github.com/johnpapa/vscode-winteriscoming) |
| 13 | `gruvbox-dark` | Gruvbox Dark | dark | [morhetz/gruvbox](https://github.com/morhetz/gruvbox) |
| 13 | `gruvbox-light` | Gruvbox Light | light | [morhetz/gruvbox](https://github.com/morhetz/gruvbox) |
| 14 | `shades-of-purple` | Shades of Purple | dark | [ahmadawais/shades-of-purple-vscode](https://github.com/ahmadawais/shades-of-purple-vscode) |
| 15 | `cobalt2` | Cobalt2 | dark | [wesbos/cobalt2-vscode](https://github.com/wesbos/cobalt2-vscode) |
| 16 | `noctis` | Noctis (cold) | dark | [liviuschera/noctis](https://github.com/liviuschera/noctis) |
| 16 | `noctis-lux` | Noctis Lux (warm) | light | [liviuschera/noctis](https://github.com/liviuschera/noctis) |
| 17 | (ships with the core: `catppuccin-latte`) | Catppuccin Latte | light | |
| 17 | `catppuccin-frappe` | Catppuccin Frappé | dark | [catppuccin/palette](https://github.com/catppuccin/palette) |
| 17 | `catppuccin-macchiato` | Catppuccin Macchiato | dark | [catppuccin/palette](https://github.com/catppuccin/palette) |
| 17 | (ships with the core: `catppuccin-mocha`) | Catppuccin Mocha | dark | |
| 18 | `panda` | Panda | dark | [siamak/atom-panda-syntax](https://github.com/siamak/atom-panda-syntax) |
| 19 | `synthwave-84` | SynthWave '84 | dark | [robb0wen/synthwave-vscode](https://github.com/robb0wen/synthwave-vscode) |
| 20 | (left out: City Lights's license forbids ports) | | | |
| 21 | `atom-one-light` | Atom One Light | light | [akamud/vscode-theme-onelight](https://github.com/akamud/vscode-theme-onelight) |
| 22 | `darcula` | Darcula | dark | [rokoroku/vscode-theme-darcula](https://github.com/rokoroku/vscode-theme-darcula) |
| 23 | `sublime-material` | Sublime Material | dark | [JarvisPrestidge/vscode-material-theme](https://github.com/JarvisPrestidge/vscode-material-theme) |
| 24 | `palenight` | Palenight | dark | [whizkydee/vscode-palenight-theme](https://github.com/whizkydee/vscode-palenight-theme) |
| 25 | `omni` | Omni | dark | [getomni/visual-studio-code](https://github.com/getomni/visual-studio-code) |
| 26 | `snazzy-light` | Snazzy Light | light | [loilo/vscode-snazzy-light](https://github.com/loilo/vscode-snazzy-light) |
| 27 | (left out: Dainty's colours are not published anywhere) | | | |
| 28 | `matcha` | Matcha | dark | [lucafalasco/matcha](https://github.com/lucafalasco/matcha) |
| 29 | `houston` | Houston | dark | [withastro/houston-vscode](https://github.com/withastro/houston-vscode) |
| 30 | `horizon-dark` | Horizon Dark | dark | [jolaleye/horizon-theme-vscode](https://github.com/jolaleye/horizon-theme-vscode) |
| 30 | `horizon-bright` | Horizon Bright | light | [jolaleye/horizon-theme-vscode](https://github.com/jolaleye/horizon-theme-vscode) |

28 of the 30 are here: 27 as files and Nord in the core. City Lights and
Dainty are left out ([NOTICES.md](NOTICES.md), "Not used, and why").

GitHub Light and Catppuccin Latte were files of this folder until
2026-10-03 and now ship with the core (`sdk/themes`), so a fresh install has
a light theme by name. Their measurements stay in the contrast table below.

## Round 2 (2026-10-03)

Themes added after the first 36 (the planning session's list of 2026-10-03). They have no number in the
list of 30 above.

| ID | Name | Kind | Source |
|---|---|---|---|
| `everforest-dark` | Everforest Dark | dark | [sainnhe/everforest](https://github.com/sainnhe/everforest) |
| `everforest-light` | Everforest Light | light | [sainnhe/everforest](https://github.com/sainnhe/everforest) |
| `kanagawa` | Kanagawa | dark | [rebelot/kanagawa.nvim](https://github.com/rebelot/kanagawa.nvim) |
| `rose-pine` | Rosé Pine | dark | [rose-pine/palette](https://github.com/rose-pine/palette) |
| `rose-pine-dawn` | Rosé Pine Dawn | light | [rose-pine/palette](https://github.com/rose-pine/palette) |
| `nightfox` | Nightfox | dark | [EdenEast/nightfox.nvim](https://github.com/EdenEast/nightfox.nvim) |
| `dayfox` | Dayfox | light | [EdenEast/nightfox.nvim](https://github.com/EdenEast/nightfox.nvim) |
| `vitesse-dark` | Vitesse Dark | dark | [antfu/vscode-theme-vitesse](https://github.com/antfu/vscode-theme-vitesse) |
| `vitesse-light` | Vitesse Light | light | [antfu/vscode-theme-vitesse](https://github.com/antfu/vscode-theme-vitesse) |
| `flexoki-dark` | Flexoki Dark | dark | [kepano/flexoki](https://github.com/kepano/flexoki) |
| `flexoki-light` | Flexoki Light | light | [kepano/flexoki](https://github.com/kepano/flexoki) |
| `oceanic-next` | Oceanic Next | dark | [voronianski/oceanic-next-color-scheme](https://github.com/voronianski/oceanic-next-color-scheme) |
| `spacegray` | Spacegray | dark | [kkga/spacegray](https://github.com/kkga/spacegray) |
| `moonlight` | Moonlight | dark | [atomiks/moonlight-vscode-theme](https://github.com/atomiks/moonlight-vscode-theme) |
| `poimandres` | Poimandres | dark | [drcmda/poimandres-theme](https://github.com/drcmda/poimandres-theme) |
| `andromeda` | Andromeda | dark | [EliverLara/Andromeda](https://github.com/EliverLara/Andromeda) |

## How a palette becomes a theme

The same mapping for every theme, after the shipped Nord, Catppuccin Mocha
and Rosé Pine Moon:

| CabinetOS key | From the source |
|---|---|
| `mica.tint` | The darkest of the editor, side bar and panel backgrounds, at opacity 0.9. The panes then show a step lighter, close to the editor's own background, as in the source's editor. |
| `layerFill`, `layerStroke`, `layerStrokeActive` | The next surface colour at `80`, the border or selection colour at `99`, and the theme's comment or border grey for the focused pane |
| `controlFill`, `controlFillHover` | The theme's hover and selection colours |
| `acrylicTint`, `terminalBackground` | The menu or widget background, and the terminal or panel background |
| `textPrimary` to `textDisabled` | The theme's text colours, checked for contrast (below) |
| `accent` | The theme's signature colour |
| `folderIcon`, `fileTypeColors`, `permission…` | The theme's yellow, blue, orange, purple, green and red |
| `terminal` | The theme's terminal colours; where it has none, its syntax colours |
| `kind` | `dark` or `light`, per variant |

Light themes turn the mapping round: the backdrop is the darker side-bar
colour, and the panes are the lighter editor colour.

## Contrast

Every theme reaches 4.5:1 for body text as the window draws it: names on
the panes, on a hovered row and on the selected row (at 90 % alpha), the
row details and the status bar (at 76 % of `textSecondary`), titles,
secondary text, the palette and menus, dialogs (at 80 %), and the
terminal's text. Tertiary text reaches 4.5:1 plain and 3:1 as a hint.
The backdrop is measured as the window's snapshot aid draws it: the Mica
tint over `#202020` (dark) or `#F3F3F3` (light).

| Theme | Lowest body text | Where | Tertiary text (plain, as a hint) |
|---|---|---|---|
| `andromeda` | 4.63:1 | row details on the selected row | 5.09:1, 3.96:1 |
| `atom-one-light` | 4.86:1 | row details on the selected row | 5.25:1, 3.69:1 |
| `ayu-dark` | 4.72:1 | row details on the hovered row | 5.34:1, 4.06:1 |
| `ayu-light` | 4.68:1 | row details on the hovered row | 5.35:1, 3.73:1 |
| `ayu-mirage` | 5.03:1 | row details on the selected row | 5.16:1, 4.00:1 |
| `catppuccin-frappe` | 4.54:1 | row details on the selected row | 4.92:1, 3.92:1 |
| `catppuccin-latte` | 4.66:1 | row details on the selected row | 4.73:1, 3.43:1 |
| `catppuccin-macchiato` | 4.98:1 | row details on the selected row | 5.84:1, 4.50:1 |
| `cobalt2` | 4.80:1 | row details on the selected row | 5.49:1, 4.27:1 |
| `darcula` | 4.90:1 | row details on the hovered row | 5.08:1, 3.94:1 |
| `dayfox` | 4.98:1 | row details on the hovered row | 6.52:1, 4.43:1 |
| `dracula` | 4.80:1 | row details on the selected row | 4.97:1, 3.89:1 |
| `everforest-dark` | 4.56:1 | row details on the hovered row | 5.21:1, 4.08:1 |
| `everforest-light` | 4.58:1 | row details on the selected row | 5.00:1, 3.55:1 |
| `flexoki-dark` | 5.38:1 | row details on the selected row | 8.80:1, 6.38:1 |
| `flexoki-light` | 4.66:1 | row details on the selected row | 6.83:1, 4.51:1 |
| `github-dark` | 5.33:1 | row details on the selected row | 4.90:1, 3.76:1 |
| `github-light` | 5.26:1 | row details on the selected row | 4.93:1, 3.50:1 |
| `gruvbox-dark` | 4.68:1 | row details on the selected row | 4.72:1, 3.74:1 |
| `gruvbox-light` | 4.86:1 | row details on the selected row | 5.74:1, 3.93:1 |
| `horizon-bright` | 8.31:1 | row details on the selected row | 7.11:1, 4.70:1 |
| `horizon-dark` | 5.73:1 | row details on the selected row | 5.88:1, 4.52:1 |
| `houston` | 4.94:1 | row details on the hovered row | 4.61:1, 3.61:1 |
| `kanagawa` | 5.24:1 | row details on the selected row | 8.72:1, 6.41:1 |
| `matcha` | 4.58:1 | row details on the hovered row | 5.46:1, 4.24:1 |
| `material-theme` | 5.17:1 | row details on the selected row | 6.78:1, 5.19:1 |
| `monokai` | 5.03:1 | row details on the selected row | 4.50:1, 3.54:1 |
| `moonlight` | 4.66:1 | row details on the selected row | 7.24:1, 5.46:1 |
| `night-owl` | 4.68:1 | row details on the selected row | 6.39:1, 4.80:1 |
| `nightfox` | 4.81:1 | row details on the selected row | 7.10:1, 5.32:1 |
| `noctis` | 4.68:1 | row details on the hovered row | 5.13:1, 3.98:1 |
| `noctis-lux` | 4.67:1 | row details on the selected row | 5.09:1, 3.62:1 |
| `oceanic-next` | 4.88:1 | row details on the selected row | 5.70:1, 4.42:1 |
| `omni` | 5.05:1 | row details on the selected row | 4.83:1, 3.72:1 |
| `one-dark-pro` | 4.87:1 | row details on the selected row | 5.82:1, 4.49:1 |
| `one-monokai` | 4.87:1 | row details on the selected row | 5.82:1, 4.49:1 |
| `palenight` | 4.73:1 | row details on the selected row | 4.81:1, 3.81:1 |
| `panda` | 4.53:1 | row details on the selected row | 5.93:1, 4.57:1 |
| `poimandres` | 6.65:1 | row details on the selected row | 4.83:1, 3.78:1 |
| `rose-pine` | 4.87:1 | row details on the selected row | 5.28:1, 4.04:1 |
| `rose-pine-dawn` | 4.58:1 | row details on the selected row | 4.91:1, 3.51:1 |
| `shades-of-purple` | 5.30:1 | row details on the selected row | 5.77:1, 4.44:1 |
| `snazzy-light` | 4.51:1 | row details on the selected row | 5.01:1, 3.53:1 |
| `solarized-dark` | 4.56:1 | row details on the selected row | 5.06:1, 3.94:1 |
| `solarized-light` | 4.51:1 | row details on the selected row | 4.84:1, 3.44:1 |
| `spacegray` | 4.92:1 | row details on the selected row | 6.59:1, 5.00:1 |
| `sublime-material` | 4.57:1 | row details on the selected row | 7.33:1, 5.56:1 |
| `synthwave-84` | 5.79:1 | row details on the selected row | 4.85:1, 3.76:1 |
| `tokyo-night` | 4.71:1 | row details on the selected row | 4.66:1, 3.58:1 |
| `vitesse-dark` | 4.79:1 | row details on the selected row | 9.35:1, 6.74:1 |
| `vitesse-light` | 5.09:1 | row details on the selected row | 7.71:1, 4.94:1 |
| `winter-is-coming` | 6.63:1 | row details on the selected row | 5.33:1, 4.11:1 |

What needed a text colour other than the theme's usual one:

- **Secondary and tertiary text, in most themes.** A theme's muted greys
  (comments, descriptions) fall to 2 to 4:1 once the window dims row
  details to 76 %. So `textSecondary` is the theme's main text, at `C8` or
  `E6` or opaque, the convention of the shipped Default and Rosé Pine
  Moon, and `textTertiary` is a brighter muted colour of the theme, or the
  main text at `8B` to `C8`.
- **The main text itself, where it was too dim even for names:**
  - `one-dark-pro`, `one-monokai`: `#ABB2BF` became `#D7DAE0`, the themes'
    own activity-bar text and ANSI white.
  - `palenight`: `#BFC7D5` became `#EEFFFF`, the theme's own bright
    foreground.
  - `solarized-dark`: base0 `#839496` became `#BDC3BB`, base1 moved 40 %
    toward base3; the terminal keeps base0 (4.75:1 there).
  - `solarized-light`: base00 `#657B83` became `#23464F`, base01 moved 60 %
    toward base03; the terminal's text is base01.
  - `catppuccin-latte`: text `#4C4F69` became `#313344` (35 % toward black);
    `catppuccin-frappe`: text `#C6D0F5` became `#D1D9F7` (20 % toward
    white).
  - `ayu-light`: `#5C6166` became `#3C3F42` (35 % toward black);
    `snazzy-light`: `#565869` became `#40424F` (25 %); `noctis-lux`:
    `#005661` became `#004952` (15 %).
  - `tokyo-night`: the terminal's text `#787C99` (4.40:1) became
    `#A9B1D6`, the theme's editor text.
