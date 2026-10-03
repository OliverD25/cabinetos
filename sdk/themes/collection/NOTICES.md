# Notices: the theme collection

Every theme in this folder is a port of someone else's colour theme. The
port itself (the mapping of a palette to CabinetOS's keys, and each JSON
file) is CabinetOS's work under the project's [MIT License](../../../LICENSE).
The colours come from the sources below, each under the license named
there. Each theme file repeats its source and license in `attribution`.

How the sources were read: on 2026-09-29, from each project's public
repository at the commit named below (a raw file at a fixed commit), and
from the npm package named for GitHub's colours. Each license file was read
at the same commit and checked to be the MIT License text; gruvbox has no
license file, and Shades of Purple adds one condition, as their rows say.
No value was written from memory, so nothing here is marked "to verify".

## Sources

| Theme files | Source (what was read) | Copyright notice, as the source gives it | License | Version and commit read |
|---|---|---|---|---|
| `github-dark`, `github-light` | [primer/github-vscode-theme](https://github.com/primer/github-vscode-theme): `src/theme.js` (which colour goes where) and `src/colors.js` (the theme's overrides); the colours themselves from [@primer/primitives](https://github.com/primer/primitives) 7.10.0, the version the theme pins: `dist/json/colors/dark.json` and `light.json` | Copyright (c) 2020 Primer; Copyright (c) 2018 GitHub Inc. | MIT (both) | 6.3.5, `cd78e5e4e7bcf132a6f428ae0f32264bb1b729cf`; @primer/primitives 7.10.0 from npm |
| `one-dark-pro` | [Binaryify/OneDark-Pro](https://github.com/Binaryify/OneDark-Pro): `themes/OneDark-Pro.json` | Copyright (c) 2013-2022 Binaryify | MIT | 3.20.2, `54c3280b29f2c2ed9751e5ca4e071380b7b42205` |
| `dracula` | [dracula/visual-studio-code](https://github.com/dracula/visual-studio-code): `src/dracula.yml` | Copyright (c) 2016 Dracula Theme | MIT | 2.25.1, `a08a206f2c8420ba3c05f0e8d01d43b2f933fdf8` |
| `material-theme` | [SublimeText/material-theme](https://github.com/SublimeText/material-theme), Mattia Astorino's Material Theme for Sublime Text: `sources/settings/specific/Material-Theme.json` and `schemes/Material-Theme.tmTheme` | Copyright (c) 2016 Mattia Astorino | MIT | 41.5.0, `134916bde95a275f56fa1808586baeaf0be28ab9` |
| `ayu-dark`, `ayu-mirage`, `ayu-light` | [ayu-theme/vscode-ayu](https://github.com/ayu-theme/vscode-ayu): `ayu-dark.json`, `ayu-mirage.json`, `ayu-light.json`, which that project generates from [ayu-theme/ayu-colors](https://github.com/ayu-theme/ayu-colors) | Copyright (c) 2016 Ike Kurghinyan; Copyright (c) Konstantin Pschera <me@kons.ch> (kons.ch) | MIT (both) | 1.4.0, `d676974ebb245fa5a7ae4444027f72801017f1b6`; ayu-colors 9.1.0, `b0fd979a1ddf050101b43311fa598a1a9c5f1bbc` (license only) |
| `monokai` | [microsoft/vscode](https://github.com/microsoft/vscode): `extensions/theme-monokai/themes/monokai-color-theme.json`, the classic Monokai scheme by Wimer Hazenberg as Visual Studio Code ships it | Copyright (c) 2015 - present Microsoft Corporation | MIT | 1.140.0 (development), `d082ab087d3eeccd2f97ce273ee2c1b8c1247a0d` |
| `night-owl` | [sdras/night-owl-vscode-theme](https://github.com/sdras/night-owl-vscode-theme): `themes/Night Owl-color-theme.json` | Copyright (c) 2018 Sarah Drasner | MIT | 2.1.1, `cc291eba7976b20d7c66bde6883c27b902196b07` |
| `one-monokai` | [azemoh/vscode-one-monokai](https://github.com/azemoh/vscode-one-monokai): `themes/OneMonokai-color-theme.json` | Copyright (c) 2018 Joshua Azemoh | MIT | 0.5.0, `42444827bc178f289285bb9acc79066ecff18f83` |
| `tokyo-night` | [enkia/tokyo-night-vscode-theme](https://github.com/enkia/tokyo-night-vscode-theme): `themes/tokyo-night-color-theme.json` | Copyright (c) 2018-present Enkia | MIT | 1.1.2, `7c0f11eaef322f293621ca7befe462214b7ea468` |
| `solarized-dark`, `solarized-light` | [altercation/solarized](https://github.com/altercation/solarized): `README.md`, "The Values" (the sixteen colours and their terminal numbers) | Copyright (c) 2011 Ethan Schoonover | MIT | no version, `62f656a02f93c5190a8753159e34b385588d5ff3` |
| `winter-is-coming` | [johnpapa/vscode-winteriscoming](https://github.com/johnpapa/vscode-winteriscoming): `themes/WinterIsComing-dark-blue-color-theme.json` | Copyright (c) 2015-2017 JohnPapa.net, LLC | MIT | 1.5.0, `260547834cb6ac37dd5b8bb5842cc1c8d3164946` |
| `gruvbox-dark`, `gruvbox-light` | [morhetz/gruvbox](https://github.com/morhetz/gruvbox): `colors/gruvbox.vim` (the palette, both modes, and `g:terminal_color_0` to `15`) | Pavel Pertsev. The repository has no license file: its `README.md` says "License: MIT/X11" and its `package.json` says `MIT` | MIT | 2.0.0, `ef8864bb42bf244f0295d1c5a403b27e3d139695` |
| `shades-of-purple` | [ahmadawais/shades-of-purple-vscode](https://github.com/ahmadawais/shades-of-purple-vscode): `themes/shades-of-purple-color-theme.json` | Copyright (c) 2015-∞ Ahmad Awais | MIT, with the author's added condition "Any thing you build with this should also be MIT licensed". CabinetOS and this port are MIT, so the condition is met | 7.3.6, `e8eb49f33e5db05ceba6677367b33ddb27ad821c` |
| `cobalt2` | [wesbos/cobalt2-vscode](https://github.com/wesbos/cobalt2-vscode): `theme/cobalt2.json` | Copyright (c) 2018 Wes Bos, Roberto Achar | MIT | 2.4.3, `c4e9574372b85afad1682ed0fdd1ac0411c62512` |
| `noctis`, `noctis-lux` | [liviuschera/noctis](https://github.com/liviuschera/noctis): `themes/noctis.json`, `themes/lux.json` | Copyright (c) 2018 Liviu Schera | MIT | 10.43.3, `4a82370b2c064e36726c23e94f27ed3782bd6ecd` |
| `catppuccin-latte`, `catppuccin-frappe`, `catppuccin-macchiato` | [catppuccin/palette](https://github.com/catppuccin/palette): `palette.json`; the terminal colours from [catppuccin/windows-terminal](https://github.com/catppuccin/windows-terminal): `latte.json`, `frappe.json`, `macchiato.json` | Copyright (c) 2021 Catppuccin | MIT (both) | palette 1.8.0, `07d02aa110ef9eb7e7427afca5c73ba9cf7f8ebd`; windows-terminal `4d8bb2f00fb86927a98dd3502cdec74a76d25d7b` |
| `panda` | Siamak Mokhtari's Panda Syntax: [siamak/atom-panda-syntax](https://github.com/siamak/atom-panda-syntax): `styles/colors.less`, `styles/syntax-variables.less`; the terminal colours from [siamak/hyperterm-panda](https://github.com/siamak/hyperterm-panda): `index.js` | Copyright (c) 2016 Siamak Mokhtari <hi@siamak.work>; Copyright (c) 2016 Siamak Mokhtari | MIT (both) | 0.18.0, `5f8da63f050a79682253630639a77aba93cb1430`; hyperterm-panda 0.0.2, `e61960eb096a19b47b524d0e0932040b43fdb04d` |
| `synthwave-84` | [robb0wen/synthwave-vscode](https://github.com/robb0wen/synthwave-vscode): `themes/synthwave-color-theme.json` | Copyright (c) 2019 Robb Owen | MIT | 0.1.20, `ecfa2fe1279f7233663fa3f98a96e6756000567b` |
| `atom-one-light` | [akamud/vscode-theme-onelight](https://github.com/akamud/vscode-theme-onelight): `themes/OneLight.json` | Copyright (c) 2015 Mahmoud Ali | MIT | 2.3.0, `5866e900db932d580e978a58db42f65cde07998b` |
| `darcula` | [rokoroku/vscode-theme-darcula](https://github.com/rokoroku/vscode-theme-darcula): `themes/darcula.json`, a community port of JetBrains' Darcula | Copyright (c) 2020 Youngrok Kim | MIT | 1.2.3, `657baba5dc1d632daefe8e6130abcca550f0ea1f` |
| `sublime-material` | [JarvisPrestidge/vscode-material-theme](https://github.com/JarvisPrestidge/vscode-material-theme) ("Sublime Material Theme"): `themes/Material-Theme.tmTheme`, its dark variant, a port of Mattia Astorino's Material Theme for Sublime Text | Copyright (c) 2016 Jarvis Prestidge | MIT | 1.0.1, `22ca870973c4dbe493942ba028fc7f42d93a3565` |
| `palenight` | [whizkydee/vscode-palenight-theme](https://github.com/whizkydee/vscode-palenight-theme): `themes/palenight.json` | Copyright (c) 2017-present Olaolu Olawuyi | MIT | 2.0.5, `63949af027921fd09466c1e6003bd2b994e14a9e` |
| `omni` | [getomni/visual-studio-code](https://github.com/getomni/visual-studio-code) (published by Rocketseat): `src/omni.yml` | Copyright (c) 2021 Omni Theme | MIT | 1.0.12, `ba964925b6661543b1855379fa95868a44a2945b` |
| `snazzy-light` | [loilo/vscode-snazzy-light](https://github.com/loilo/vscode-snazzy-light): `themes/Snazzy-Light-color-theme.json` | Copyright (c) 2020 Florian Reuschel | MIT | 1.4.1, `84f38e935ec0b43b81835b37dda515150e7b823f` |
| `matcha` | [lucafalasco/matcha](https://github.com/lucafalasco/matcha): `themes/matcha-color-theme.json` | Copyright (c) 2019 Luca Falasco | MIT | 0.0.12, `7d28a6be303d6cafae8e3f78f0b6f8771226dc7a` |
| `houston` | [withastro/houston-vscode](https://github.com/withastro/houston-vscode): `themes/houston.json` | Copyright (c) 2022 The Astro Technology Company | MIT | 0.1.2, `4d9923d077061e1696c32e4033edc43d9747be12` |
| `horizon-dark`, `horizon-bright` | [jolaleye/horizon-theme-vscode](https://github.com/jolaleye/horizon-theme-vscode): `themes/horizon.json`, `themes/horizon-bright.json` | Copyright (c) 2018 Jonathan Olaleye | MIT | 2.0.2, `5ae91b6d49bf291e0a34c0a1cb277d9738aadd90` |
| (the terminal colours SynthWave '84 and Horizon leave unset) | [microsoft/vscode](https://github.com/microsoft/vscode): `src/vs/workbench/contrib/terminal/common/terminalColorRegistry.ts`, Visual Studio Code's own defaults, which is what those themes show in Visual Studio Code | Copyright (c) 2015 - present Microsoft Corporation | MIT | `d082ab087d3eeccd2f97ce273ee2c1b8c1247a0d` |
| `everforest-dark`, `everforest-light` | [sainnhe/everforest](https://github.com/sainnhe/everforest): `autoload/everforest.vim` (the palette, dark and light, medium contrast) and `colors/everforest.vim` (which colours the terminal gets) | Copyright (c) 2019 sainnhe | MIT | no version, `85a86eb62409e3ec88713bff3d1b9d7374e112e4` |
| `kanagawa` | [rebelot/kanagawa.nvim](https://github.com/rebelot/kanagawa.nvim): `lua/kanagawa/colors.lua` (the palette), `lua/kanagawa/themes.lua` (the roles of the Wave theme) and `extras/windows-terminal/kanagawa.json` (the Wave terminal colours) | Copyright (c) 2021 Tommaso Laurenzi | MIT | no version, `bb85e4bfc8d89b0e62c8fa53ccdd13d12e2f77b3` |
| `rose-pine`, `rose-pine-dawn` | [rose-pine/palette](https://github.com/rose-pine/palette): `source/index.ts` (the palette with its highlight shades; the older `palette.json` of the same version still has Dawn's text as `#464261`, so it was not used); the terminal colours from [rose-pine/windows-terminal](https://github.com/rose-pine/windows-terminal): `rose-pine.scheme.json`, `rose-pine-dawn.scheme.json` | Copyright (c) mvllow (palette); Copyright (c) Rosé Pine (windows-terminal) | MIT (both) | palette 4.0.1, `92af52b465ab6e47437aca223c9b8d3009a2023b`; windows-terminal `89e1a277e4ac10e06f33b13cddabde3c42d8d411` |
| `nightfox`, `dayfox` | [EdenEast/nightfox.nvim](https://github.com/EdenEast/nightfox.nvim): `lua/nightfox/palette/nightfox.lua` and `dayfox.lua` (the palettes) and `extra/nightfox/windows_terminal.json` and `extra/dayfox/windows_terminal.json` (the terminal colours, which the project generates from them) | Copyright (c) 2021 James Simpson | MIT | 3.10.0 (its changelog), `4dacd3f0185a2227bdf3b6c0975a8f0bf87cac9a` |
| `vitesse-dark`, `vitesse-light` | [antfu/vscode-theme-vitesse](https://github.com/antfu/vscode-theme-vitesse): `themes/vitesse-dark.json`, `themes/vitesse-light.json` | Copyright (c) 2020 Primer; Copyright (c) 2021 Anthony Fu | MIT | 1.0.1, `2862595c3d5d05fabfd5aeb50bcbb79ea2f8d85f` |
| `flexoki-dark`, `flexoki-light` | [kepano/flexoki](https://github.com/kepano/flexoki): `README.md` (the colour tables), `vscode/Flexoki-Dark-color-theme.json` and `vscode/Flexoki-Light-color-theme.json` (which colour goes where) and `windows-terminal/flexoki-dark.json` and `flexoki-light.json` (the terminal colours) | Copyright (c) 2023 Steph Ango | MIT; its README asks every port to include attribution and a link to stephango.com/flexoki, and each theme file's `attribution` does | no version, `8d723bac4a9ac46adfdf99d42155286977aac72a` |

## What the port derives

The source colours are used as they are. What the port adds:

- **Alpha.** The layer, control, Acrylic and terminal-panel colours are
  source colours with the alpha the shipped themes use (for example
  `layerFill` at `80`). The Mica tint is the darkest of the source's
  editor, side bar and panel backgrounds, at opacity 0.9. The Catppuccin
  flavours map each palette role the way the shipped Catppuccin Mocha does.
- **`folderIconFront`** is the folder colour moved 35 % toward white, as in
  the shipped themes; Panda and Horizon Dark use the lighter shade their
  source has.
- **Text colours for contrast.** Where a theme's own text colour falls
  below 4.5:1 as the window draws it, the port uses another of the theme's
  text colours, the theme's main text at a lower alpha, or, where the
  theme has nothing brighter (darker, for a light theme), its main text
  moved toward white (black) by the least amount that reaches 4.5:1. Each
  case is listed in [README.md](README.md), "Contrast".
- **Terminal colours.** Atom One Light, the Darcula port, Material Theme
  and Sublime Material define no terminal colours; theirs are built from
  the theme's own syntax colours. Where SynthWave '84 and Horizon leave an
  ANSI colour unset, it is Visual Studio Code's default. Horizon Bright's
  cursor is its editor cursor colour, because its terminal cursor colour
  (`#F9CEC3B3`) cannot be seen on its background; Shades of Purple's red
  (`#EC3A37F5`) is used without its alpha.

## Not used, and why

- **Monokai Pro** is a commercial product: the classic Monokai that
  Visual Studio Code ships under the MIT License is used instead (file
  `monokai`), and its description says so.
- **JetBrains' own Darcula files** are not used; the colours are the MIT
  community port's, as its description says.
- **The Material Theme for Visual Studio Code**
  (`material-theme/vsc-material-theme`) now redirects to
  `vira-soft/vira-assets`, the assets of its commercial successor, with no
  license: Mattia Astorino's MIT Material Theme for Sublime Text is used.
- **The Panda theme for Visual Studio Code**
  (`tinkertrain/panda-syntax-vscode`) states no license: the same author's
  MIT Atom and Hyper themes are used.
- **City Lights** (Yummygum, `Yummygum/city-lights-syntax-vsc` 1.1.9 at
  `e9e299e981c95c953750d5029b4ec94248071a1c`) is licensed under Creative
  Commons Attribution-NonCommercial-NoDerivatives 4.0: a port is a
  derivative work, which that license forbids. Left out.
- **Dainty** (Alexander Teinum) is MIT (the fork `HotWordland/dainty-vscode`
  1.1.22 at `237f5d5ac2c4610818576805e407c2bbbae1da74` keeps the author's
  license), but its colours are generated at build time by the
  `dainty-shared` package, whose repository and package are no longer
  published, and no public file holds the generated colours. Left out
  rather than written from memory.
- **Nord**, **Catppuccin Mocha**, **Catppuccin Latte** and **GitHub Light**
  ship with the core; the collection does not repeat them. Latte and GitHub
  Light were files of this folder until 2026-10-03; their rows in the table
  above still name their sources and commits.

## The MIT License

Each source above grants its colours under the MIT License, with its own
copyright notice as given in the table. This is the license text they
share:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
