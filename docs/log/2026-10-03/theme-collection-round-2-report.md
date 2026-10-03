# Theme collection, round 2: two light themes in the core, 18 more in the collection

Coder report, Sonnet 5.5, 2026-10-03 (the night the creator slept). Branch
`worktree-agent-aadbcc81359c57db1`, from main's `358eaa2` (the 0.1.2 cut). The
desk card is "Theme collection, round 2". Constitution Article 3 (Native Modern
Aesthetics) is the reason for the two light themes in the core; Article 10
(Zero-Bloat Foundation) is the reason the 18 others stay in the collection.
Nothing was merged or published: the planning session publishes `themes.json`
after the merge.

## What was built

| Part | Commit | What it is |
|---|---|---|
| A. Two light themes in the core | `1151422` | Catppuccin Latte and GitHub Light moved from `sdk/themes/collection` to `sdk/themes`, and into `SHIPPED` of `core/crates/cabinetos-themes/src/lib.rs` (seven shipped themes). Tests, docs and the window's real-key scripts follow the new picker order. |
| B1. Everforest, Kanagawa, Rosé Pine | `a132c27` | 5 files: `everforest-dark`, `everforest-light`, `kanagawa`, `rose-pine`, `rose-pine-dawn` |
| B2. Nightfox, Vitesse, Flexoki | `cd56758` | 6 files: `nightfox`, `dayfox`, `vitesse-dark`, `vitesse-light`, `flexoki-dark`, `flexoki-light` |
| B3. Five dark themes | `e6ec478` | 5 files: `oceanic-next`, `spacegray`, `moonlight`, `poimandres`, `andromeda` |
| B4. Two light themes, and the themes left out | `4cd7fa7` | 2 files: `quiet-light`, `min-light`; NOTICES.md says why four candidates are out |
| C. Documents and this report | the report's commit | `docs/themes.md`, `docs/marketplace.md`, `sdk/README.md`, the collection's README and NOTICES, `CHANGELOG.md`, this report |

## Part A: where the shipped themes are listed

- The theme files: [catppuccin-latte.json](../../../sdk/themes/catppuccin-latte.json)
  and [github-light.json](../../../sdk/themes/github-light.json). `git mv` kept
  their history. Only `author` changed (see "Decided").
- The list the core embeds: `SHIPPED` in
  [lib.rs](../../../core/crates/cabinetos-themes/src/lib.rs), from 5 to 7 entries.
  The tests there check the two (kind `light`, accent, tint, the sorted list of
  seven IDs). `two_cores.rs` counts `SHIPPED.len()`, so it followed by itself.
- The core's integration test `core/crates/cabinetos-core/tests/themes.rs`: the
  seven IDs, their tints and the density marks (only Commander Compact).
- The window's tests: `ThemeTests.cs` (both themes map; the text-on-accent test
  reads them from the shipped folder now) and `ThemePickerEndToEndTests.cs` (Nord
  is row 5 of the picker, was row 3). The real-key scripts pick a theme by
  counting Down from Home, so `livecheck.ps1`, `showcase-shots.ps1` and
  `release-media.json` now press Down twice for Commander Compact and three times
  for Default (they were once and twice). None of these was run: no window opened.
- Not changed, because they do not list the shipped themes: `id.rs` and the
  marketplace `index.rs` tests use `rose-pine-moon` only as a sample ID.
- The collection lost the two files and their rows in `marketplace.csv`.

## The 20 candidates

| Candidate | Result | Why |
|---|---|---|
| Everforest Dark | ported `everforest-dark` | MIT; vim palette, medium contrast |
| Everforest Light | ported `everforest-light` | MIT |
| Kanagawa | ported `kanagawa` (Wave) | MIT; Lua palette and Windows Terminal scheme |
| Rosé Pine | ported `rose-pine` | MIT; palette source and Windows Terminal scheme |
| Rosé Pine Dawn | ported `rose-pine-dawn` | MIT |
| Nightfox | ported `nightfox` | MIT; Lua palette and the generated terminal file |
| Dayfox | ported `dayfox` | MIT |
| Vitesse Dark | ported `vitesse-dark` | MIT; VS Code theme JSON |
| Vitesse Light | ported `vitesse-light` | MIT |
| Flexoki Dark | ported `flexoki-dark` | MIT; README colour tables, VS Code theme, terminal scheme |
| Flexoki Light | ported `flexoki-light` | MIT |
| Modus Vivendi | left out | GPL 3 or later (GNU Emacs files); the collection takes no GPL theme |
| Modus Operandi | left out | the same |
| Oceanic Next | ported `oceanic-next` | MIT as the README declares ("MIT Licensed"); no license file; palette in the README |
| Spacegray | ported `spacegray` | MIT; Base16 Ocean Dark colours of the Sublime Text theme |
| Zenburn | left out | GNU GPL (README and scheme header); no license file; the collection takes no GPL theme |
| Moonlight | ported `moonlight` | MIT; the palette and UI map the repository builds its themes from |
| Poimandres | ported `poimandres` | MIT; VS Code theme JSON |
| Andromeda | ported `andromeda` | MIT; VS Code theme JSON (four unset ANSI colours are VS Code's defaults) |
| Bluloco Light | left out | LGPL 3; the collection takes no (L)GPL theme |
| Quiet Light | ported `quiet-light` | MIT as Visual Studio Code ships it (origin Colorsublime-Themes, MIT) |
| Min Light | ported `min-light` | MIT; VS Code theme JSON |

The table has 22 rows because the list names Everforest and Flexoki once, and
each has a dark and a light file. As entries the list has 20: 16 ported (18
files) and 4 left out (Modus Vivendi, Modus Operandi, Zenburn, Bluloco Light).
Every license file was read at the commit named in NOTICES.md and compared with
the standard MIT text (13 of 13 license files are plain MIT text, with no extra
condition; Oceanic Next has none, see "Decided"). Every colour of every file was compared with the source files: a
script lists any colour of a theme that is in none of them, and it listed none
once the derived colours (listed below) were allowed.

## Checks

From `core/`, then `ui/` (exit codes read one by one, not through a pipe):

| Command | Result |
|---|---|
| `cargo build --workspace` | exit 0 |
| `cargo test --workspace` | exit 0; 920 passed, 0 failed, 6 ignored |
| `cargo clippy --workspace --all-targets -- -D warnings` | exit 0 |
| `cargo fmt --all -- --check` | exit 0 |
| `cargo deny check` | exit 0 (advisories, bans, licenses and sources ok) |
| `dotnet build CabinetOS.sln -warnaserror` (Debug) | exit 0, 0 warnings |
| `dotnet build CabinetOS.sln -warnaserror -c Release` | exit 0, 0 warnings |
| `dotnet test --solution CabinetOS.sln --no-build` | exit 0; 1425 tests: 1354 passed, 0 failed, 71 skipped (the tests that open a window) |
| `build-index.ps1 -Collection -ThemesOnly` into a scratch folder | exit 0; `themes.json` has 59 items |

`themes.json`: 59 items = 7 shipped + 52 of the collection; 41 dark, 16 light,
2 system. All 20 themes this work adds or moves (the 18 new ones and the 2 light
ones now in the core) are in it, each with `appearance` (dark or light),
`density` false and three tile colours `#RRGGBB`: `catppuccin-latte`,
`github-light`, `everforest-dark`, `everforest-light`, `kanagawa`, `rose-pine`,
`rose-pine-dawn`, `nightfox`, `dayfox`, `vitesse-dark`, `vitesse-light`,
`flexoki-dark`, `flexoki-light`, `oceanic-next`, `spacegray`, `moonlight`,
`poimandres`, `andromeda`, `quiet-light`, `min-light`. The collection's own
tests (`cabinetos-themes/tests/collection.rs`, `cabinetos-market/tests/build_script.rs`)
pass for the count of 59. No window opened, nothing ran on the laptop, and no
process was stopped.

## Contrast

The window's text rule (4.5:1 for body text as the window draws it; tertiary text
4.5:1 plain and 3:1 as a hint) was measured with a script that composites the way
`ThemeMapper` does. Before it was used on the new themes it was checked against
the 36 themes already in the collection: the lowest body-text value and the place
it comes from match the README's table for all 36 (values within 0.05:1). The
tertiary column matches for 31 of the 36, measured on the pane for dark themes
and on the backdrop for light ones. For the new themes the tertiary check is the
stricter of the two surfaces. All 18
reach 4.5:1 (lowest 4.56:1, `everforest-dark`). The rows are in the collection's
README, and the changes it needed are listed there under "Round 2".

## Decided (what, because, undo)

- **Latte and GitHub Light were moved, not copied.** The brief said the
  collection keeps its own copy. But `build-index.ps1` stops with "ships with the
  core; the collection must not repeat it", and `collection.rs` fails the same
  way, and Nord is handled like this (a file in `sdk/themes` only, "ships with the
  core" in the collection's README). Undo: `git mv` the two files back, put their
  two CSV rows back, remove them from `SHIPPED`.
- **Their `author` is now `CabinetOS`**, like Nord, Mocha and Moon; the
  `attribution` still names the palette. Undo: restore "Catppuccin, port by
  CabinetOS" and "GitHub (Primer), port by CabinetOS".
- **Licenses.** The accepted list is MIT, BSD, Apache 2.0, ISC, Unlicense, CC0,
  and a GPL or MPL the collection already takes; the collection takes none, so
  the GPL and LGPL candidates are out although their colours are published.
  Undo (a decision for the creator): port the four and add the license to the list.
- **Oceanic Next** is ported on the README's "MIT Licensed" line alone, with the
  colours of its README palette, because that is the project's own statement and
  its colours are public. NOTICES.md says there is no license file. Undo: delete
  the file and its three rows (CSV, README, NOTICES).
- **Quiet Light** comes from Visual Studio Code's copy (MIT), the way Monokai
  did: it is a public, MIT licensed theme file. VS Code's third-party notices
  give its origin, Colorsublime-Themes, the MIT License too.
- **Variants.** Only the variants named in the list: Kanagawa Wave (not Dragon or
  Lotus), Everforest at medium contrast, Vitesse (not Soft or Black), Poimandres
  (not Storm or White), Andromeda (not Bordered or Italic), Moonlight (its UI
  map is shared by Moonlight II). Undo: none needed; they can be added later.
- **Rosé Pine's palette source.** `source/index.ts` of the palette project, not
  its `palette.json`, which is older and still has Dawn's text as `#464261`
  (the Windows Terminal scheme and `index.ts` agree on `#575279`).
- **Accents.** Where a theme has several signature colours: Min Light `#1976D2`
  (its link blue), Kanagawa `#7E9CD8`, Flexoki `#3AA99F` and `#24837B` (the
  colour of its VS Code buttons), Spacegray `#EBCB8B` (the accent of its colour
  scheme), Everforest the green of its status line. Undo: edit `accent`.
- **Terminal colours.** From the theme's own scheme where it has one. Oceanic
  Next and Spacegray (Base16 palettes) and Quiet Light have none, so theirs are
  built from the palette in the usual Base16 order, with `base01` for black;
  Andromeda's four unset colours are VS Code's defaults; Min Light's ANSI blue
  `#E0E0E0` is unreadable on white, so its bright blue is used. All in NOTICES.md.
- **Colours the port derives** (everything else is read from a source): the
  folder icon's front colour (35 % toward white, as in every theme), and the
  main text of `everforest-dark` (`#D8CDB4`), `everforest-light` (`#374044`) and
  `rose-pine-dawn` (`#403D5A`), moved by the least amount that reaches 4.5:1.
- **A text colour the theme itself has instead of moving one:** Poimandres uses
  its own `#E4F0FB` (variable and hover text) before any move.
- **`docs/PLAN.md` and ADR 0022 were not edited.** They say "41 themes" as
  history; the planning session owns the plan and its desk card.

## Noticed, not done

- `ui/livecheck/speed-review.ps1` walks the picker with `pick:1` to `pick:4` and
  `pick:0`. The picker has seven rows now, so the step covers fewer of them. It
  still works.
- The picker's order is by theme ID, so the two light themes sit at the top
  (Latte) and in the middle (GitHub Light) of the list; a grouping by dark and
  light would be a design change.
- A user who installed Latte or GitHub Light from a published `themes.json` before
  this change keeps that file (the core keeps a file it did not write). After the
  merge, `cabinetos-cli market uninstall catppuccin-latte` should be tried once,
  because the ID is now a shipped theme (trust rule 7).
- The picker order and the new themes' look are not seen on a screen: the live check
  is the planning session's, at the merge.

## Files

- Created: 18 theme files in `sdk/themes/collection/` (`everforest-dark.json` to
  `min-light.json`); this report.
- Moved: `catppuccin-latte.json`, `github-light.json` from `sdk/themes/collection/`
  to `sdk/themes/`.
- Modified: `core/crates/cabinetos-themes/src/lib.rs`,
  `core/crates/cabinetos-core/tests/themes.rs`,
  `core/crates/cabinetos-market/tests/build_script.rs`,
  `sdk/themes/collection/{marketplace.csv,README.md,NOTICES.md}`,
  `docs/themes.md`, `docs/marketplace.md`, `sdk/README.md`, `CHANGELOG.md`,
  `ui/CabinetOS.Tests/ThemeTests.cs`, `ui/CabinetOS.Tests/ThemePickerEndToEndTests.cs`,
  `ui/livecheck/{livecheck.ps1,showcase-shots.ps1,release-media.ps1,release-media.json}`,
  `docs/log/2026-10-03/README.md`.
