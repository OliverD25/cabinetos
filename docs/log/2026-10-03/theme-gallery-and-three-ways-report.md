# Phase 23: the theme gallery with two catalogue files, and the first four three-ways gaps

Coder report, Sonnet 5.5, 2026-10-03 (the night the creator slept). Branch
`worktree-agent-aeba340e544ea5221`, from main's `a8e45ae`. Plan:
[PLAN.md](../../PLAN.md), Phase 23. Decision record:
[ADR 0022](../../decisions/0022-two-catalogues-extensions-and-themes.md).
Nothing was pushed, merged or published.

## What was built

The work is in six parts, each its own commit, and then documents and checks.

| Part | Commit | What it is |
|---|---|---|
| 3.1 Two catalogue files | `b71aa8d` | `sdk/marketplace/build-index.ps1` writes `index.json` (plugins and tools) and `themes.json` (themes). A theme item has `appearance` (dark, light, system), `density` and `tile` (`background`, `text`, `accent`, each `#RRGGBB`). The setting `marketplace.themes` (default: the public `themes.json`) says where the themes file is. The core reads both files with the same cache, ETag and trust rules. The transition rule: while the themes address answers 404 (or a local folder has no `themes.json`), the core offers the theme items of `index.json`, with one log line; when `themes.json` exists, those items are ignored, with one log line. Protocol version 20 (`catalogue` on `marketplace_refresh` and `marketplace_search`). `cabinetos-cli market refresh --themes`. Rust tests build both files and read them with the core's own reader. |
| 3.2 Extensions page | `3a12b0e`, `3cdf2e8` | The old marketplace page is now "Extensions": rows Discover, Plugins, Tools, Installed; search "Search extensions"; no theme anywhere. The command keeps its id `marketplace.browse`; its palette title is "Marketplace: Browse Extensions"; the rail tooltip says "Extensions". |
| 3.3 Theme gallery | `3a12b0e`, `8e3c034`, `e4444d4` | `ThemeGalleryView` and `ThemeGalleryModel`: heading "Themes", search "Search themes", chips All, Dark, Light, System, Density presets, Installed (Windows' mode first), tiles painted in the catalogue's colours with name, author, marks and an "Applied" mark on the theme in effect. Arrow keys, Home, End, Enter, Delete, Tab (steps the chips), typing goes to the search; click selects, double-click installs or applies. The selected tile is previewed on the whole window, nothing installed (new request `preview_theme`); the status bar says "Previewing <name> · Esc restores"; Esc paints the theme in effect back. Install, Apply and Remove work from the tile. The picker's last row "Browse more themes…" and the palette row "Themes: Browse" (`themes.browse`) open it. A catalogue that cannot be read gives one line under the toolbar, never a dialog. |
| 3.4 Four three-ways gaps | `3a12b0e`, `7423d26` | `ui.layout`: `view.layoutClassic`, `view.layoutRight`, `view.layoutRail`, `view.cycleLayout` (Ctrl+K Ctrl+L), a "Layout" submenu in the top row's menu that checks the current layout, and "current" on the palette row. `panes.showHidden`: `view.toggleHiddenFiles` (Ctrl+K Ctrl+H), the menu item "Show Hidden Files", the word "hidden" in the status bar. `ui.sidebarAutoReveal`: `sidebar.toggleFollow`, a pin in the Explorer view's header (rail layout), "Follow the Active Pane" in the menu (classic layout). `contextMenu.shellMenu`: `menu.toggleShellMenu` and a check box as the last row of "Edit Menu…" ("Show Windows' own menu with Shift+right-click"). Each command writes its key with `set_value`; the window follows `config_changed`, so an edit of the file moves the control. |
| Documents | `b71aa8d`, `1253ddb`, `2e5e9e8`, `4c287dc`, the report's commit | `marketplace.md`, `themes.md`, `config.md`, `keybindings.md`, `ui.md` (Extensions page, gallery, picker row, menu items, Esc order, live-check sections), `ipc.md` (catalogues and `preview_theme`), ADR 0022 and its index row, the CHANGELOG (`Unreleased`: one line per way), one status line in `PLAN.md`. The audit table of the project skill `settings-three-ways` now shows gaps 1 to 4 as closed. |
| Live check and media | `91bcf6e`, `fe7c407`, `2e5e9e8`, `4c287dc` | Live check sections 22 (gallery) and 23 (layout) with real keys and the real mouse; `release-media.ps1 -LocalCatalogue` and five gallery and Extensions items. |

## Checks

Core, from `core/` (exit codes read one by one, not through a pipe):

| Command | Result |
|---|---|
| `cargo build --workspace` | exit 0 |
| `cargo test --workspace` | exit 0; 917 passed, 0 failed, 6 ignored |
| `cargo clippy --workspace --all-targets -- -D warnings` | exit 0 |
| `cargo fmt --all -- --check` | exit 0 |
| `cargo deny check` | exit 0 (advisories, bans, licenses and sources ok) |

The first test run failed, and both failures were mine and are fixed. Clippy
flagged `map(..).unwrap_or(..)` in the core's preview test (`market.rs`,
changed to `map_or`; the fmt check then asked for a different layout of it,
and that is committed). Three Rust tests of `build-index.ps1` failed because
cargo ran from a PowerShell 7 session: a Windows PowerShell 5.1 started from 7
inherits 7's module path and has no `Get-FileHash`. `build-index.ps1` now sets
the module path itself (the live check carries the same guard).

Window, from `ui/`:

| Command | Result |
|---|---|
| `dotnet build CabinetOS.sln -warnaserror` (Debug) | 0 warnings, 0 errors |
| `dotnet build CabinetOS.sln -warnaserror -c Release` | 0 warnings, 0 errors |
| `dotnet test --solution CabinetOS.sln --no-build` (fast tests) | 1387 tests: 1319 passed, 68 skipped (they open a window and need `CABINETOS_UI_E2E=1`), 0 failed |
| the whole suite with the window tests, on the Omen laptop at `6dffc42` (`remote-tests.ps1 -EndToEnd`) | 1384 of 1387 passed in 4 min 36 s; the 3 that failed are listed under "Needs the creator" |

New tests: `ThemeGalleryTests` (model, with a fake core), `ThreeWaysTests`
(the four settings' states and the layout names), additions to
`ThemePickerTests`, `MarketplaceTests` and `EndToEndTests` (the real core and
a local catalogue, no window: 41 tiles, a preview that installs nothing, an
install that applies, the core refusing to remove the theme in effect, Remove),
and eight window tests: `ThemeGalleryEndToEndTests` (the Extensions page shows
no theme; the gallery opens from the picker's last row and from the palette;
arrows and Esc restore; a clicked tile is previewed and installs nothing; a
double-click installs and applies, and the picker lists the theme; an
unreadable catalogue gives one line) and `ThreeWaysEndToEndTests` (each of the
four settings from its command, its control and a file edit, with the other
two ways showing it). The eight passed 8 of 8 on the laptop at `1253ddb`, and
the final full run above includes them. The first laptop run of them had
8 failures, all in my test code (log booleans are "true" and "false" as text;
the core writes `ui.theme` at start; one test used a missing `themes.json`,
which is the transition rule, instead of a broken one); fixed in `1253ddb`.

## The live check

| Machine | Result |
|---|---|
| Omen laptop, `4c287dc`, `-Panel` | 287 True, 0 False, 9 min 39 s, exit 0; "panel goal ... met: yes" (338 frames in 5 s, 0 with UI work over 20 ms, 0 over 33 ms); the gap goal "not judged on a panel" as always there. Output: `_io\live-check\run-2026-10-03-0308-rd-omen-laptop.txt` |
| This PC | **Not run.** The creator's installed `C:\Users\Admin\AppData\Local\Programs\CabinetOS\CabinetOS.exe` has run here since 2026-10-02 23:51, and `wait-for-pc.ps1` waits for no `CabinetOS.exe` (it timed out after 10 minutes). I did not stop it. The window tests and the live check ran on the laptop, which needs no consent. A run on the laptop alone proves the logic, not this PC's speed. |

Sections 22 and 23 are all True: the gallery opens from the picker's last row
(End, Enter); Home, Right and Down preview tiles, the status bar says what is
previewed, `ui.theme` and the themes folder do not change; Esc paints the
theme in effect back and empties the status line; the palette's "Themes:
Browse", "dracula", Down, Enter installs Dracula from the run's own
`themes.json` and applies it (file in the themes folder, `ui.theme` in the
file, the tile named "applied" through UI Automation); "nord" and a real
double-click on the tile found by its accessible name applies Nord; the
palette's "View: Activity Rail" writes `rail`; the menu's Layout, Classic
Layout writes `classic` (and the "Activity Rail" row was checked); Ctrl+K Ctrl+L
three times writes `right`, `rail`, `classic`; no warning or error line in the
log in either section.

The live check found two real faults before it passed. Both are fixed.

1. **The picker started on its new last row.** In the first two laptop runs
   seven lines were False: the theme picker opened with "Browse more themes…"
   highlighted, so Down did nothing and Enter opened the gallery instead of
   applying a theme. The cause: XAML replays the last pointer position as a
   `PointerMoved` when rows are drawn under a pointer that rests, and the new
   row sat under the point where the live check had clicked in the left pane.
   A row now takes the highlight only for a pointer event at a place other
   than the one before it (`ThemePicker.xaml.cs`, `OnRowPointerMoved`;
   `ui.md` says so). I first tried only parking the pointer in the live check
   (`fe7c407`); it did not help, so the fix is in the product (`4c287dc`), and
   `ParkMouse` stays as extra care.
2. **A check that clicked at the screen's corner.** In the second run the rail
   check did not find one of its five buttons (under load; which one was not
   printed), the click then went to the corner of the screen, Explorer came to
   the front, and the run stopped. The check now prints which button is
   missing and never clicks without a rectangle. Not seen again in the third
   run; I did not find out which button it was.

## Screenshots

In `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\phase-23-shots\`,
taken on the laptop by `release-media.ps1 -LocalCatalogue` over `C:\Demo` (sidebar
hidden). I looked at each with the image reader. None shows a personal folder
name (the window title says "Demo"; an earlier set from the live check showed
the pinned folder "Omen" in the sidebar, so I used these instead and deleted
the other set).

| File | Shows |
|---|---|
| `theme-gallery.png` | The gallery, 41 tiles in their own colours, "Applied" on Default, Dracula selected with Install, status bar "Previewing Dracula · Esc restores". |
| `theme-gallery-light.png` | Search "light": 7 tiles; the whole window painted in the previewed light theme (Ayu Light). |
| `theme-gallery-compact.png` | Search "compact": the density preset previewed, with its function-key bar. |
| `theme-gallery-applied.png` | After Dracula was installed and applied: the window in Dracula's colours, "Applied" on its tile, search cleared. |
| `extensions.png` | The Extensions page: Discover, Plugins, Tools, Installed; "Search extensions"; 11 results, plugins and one tool, no theme. |

The live check keeps its own shots on the laptop (`gallery-preview-live.png`,
`gallery-search-live.png`, `gallery-applied-live.png`, `layout-rail-live.png`,
`layout-menu-live.png`, `rail13-extensions-live.png`); they show the pinned
"Omen" folder and stay there.

## Decided without the creator

One line each: what, because, how to undo.

- **`preview_theme` is a new request in protocol 20, not a bump to 21.** The
  gallery must show a theme before it is installed; the other way is to
  install to look, which leaves a file for every rejected theme. 20 is not
  released yet. The core writes a temporary file in its own marketplace folder
  and deletes it (not "into memory", which my first wording said; `ui.md`,
  `ipc.md` and the code comment now say it). Undo: remove the request and let
  the gallery preview installed themes only. ADR 0022, point 6.
- **The tile's three colours** (`background`, `text`, `accent`) are computed by
  the build script from the theme file: `layerFill` laid over the Mica tint over
  plain Mica, `textPrimary`, the accent (or the default accent). The ADR has
  the exact rule. Undo: change the script; the window only draws what it gets.
- **The gallery stays open after Install or Apply**, so several themes can be
  tried. Undo: call `CloseGallery` at the end of `ActivateGalleryTileAsync`
  (`MainWindow.Gallery.cs`).
- **Tab steps through the chips**, because the gallery keeps the keyboard as
  the picker does (Tab never leaves the view). Undo: remove the Tab case in
  `ThemeGalleryView.OnKeyDown`.
- **Delete removes an installed theme, after a question** (the same
  confirmation the Extensions page uses). The core refuses the theme in effect.
  Undo: remove the Delete case.
- **Tile sizes are fixed** (200 px minimum, 10 px gap). A density preset that is
  previewed changes the window's metrics, and tiles that sized themselves with
  them would put another tile under the pointer. Undo: not advised.
- **The gallery and the picker close each other**, the gallery closes when the
  core stops (the preview is painted back), and the Extensions page and the
  gallery close each other. One screen's colours belong to one preview.
- **The palette shows "current", "on" or "off" on the rows of the new
  commands only** (`PaletteRow.StateText`), not on older toggles. Undo: remove
  `PaletteModel.StateOf`.
- **The "Layout" menu is a drill-down inside the menu** (a row with a back row),
  not a side flyout, because the top row's menu is a window overlay and not a
  native flyout. Undo: not advised.
- **The picker takes the highlight only from a pointer that moves** (the fault
  above). It also changes the existing rows. Undo: revert `OnRowPointerMoved`
  in `ThemePicker.xaml.cs` (the live check then fails the picker steps again
  unless the pointer is parked at the right place).
- **The live check builds both catalogues with `-Collection`** (41 themes) and
  sets `marketplace.themes`; its rail shot is renamed
  `rail13-extensions-live.png`. Undo: drop `-Collection`; section 22 then
  cannot install Dracula.
- **`release-media.ps1` gets `-LocalCatalogue` and five items** (`theme-gallery`,
  `theme-gallery-light`, `theme-gallery-compact`, `theme-gallery-applied`,
  `extensions`). A default run of the list now takes them too; `-Only` leaves
  them out. The public site has no `themes.json` yet and its old `index.json`
  has no tile colours, so a gallery screenshot needs a local catalogue. Undo:
  remove the items from `release-media.json`.
- **`docs/themes.md` no longer promises a "Source" button on the tile**, which
  does not exist. The item keeps the source link; the tile does not show it.
- **The shared laptop clone changed branches during my work.** My first
  `remote-tests.ps1` run checked my branch out in `C:\Dev\cabinetos\cabinetos`
  for about two minutes while the other session (Phase 24) ran its live check;
  I put `main` back at once, and waited for their run to end before I ran again.
  Later runs waited for a free laptop and, at the end, I put the clone back on
  the branch I found, `worktree-agent-a4c5f0b65676ff081`. The clone's Release
  folder now holds my window (built 03:00) and core; the other session's next
  run copies its own.
- **Deleted my own scratch files** in `_io\phase-23-shots`: the copies of the
  live-check shots (they show the "Omen" folder) and two copies of the window's
  log. The live check's own output files stay in `_io\live-check`.

## Needs the creator

1. **Publish `themes.json`.** Until it is on the public site the window works
   through the transition rule (the gallery shows the themes that the old
   `index.json` carries, as plain tiles without colours). After this branch is
   merged into main, run in a fresh terminal (the WSL terminal; the build
   script is PowerShell, so it is called through `powershell.exe`):

   ```bash
   cd /mnt/e/codespace/_claude_code/_rde/cabinetos-marketplace/cabinetos-marketplace && git switch main && git pull --ff-only && powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\sdk\marketplace\build-index.ps1' -OutDir 'E:\codespace\_claude_code\_rde\cabinetos-marketplace\cabinetos-marketplace' -Collection -ThemesOnly && git status --short && git add index.json themes.json files && git commit -m "Publish themes.json: the themes catalogue with appearance, density and tile colours; index.json lists extensions only" && git push
   ```

   This is the command of `docs/marketplace.md`, "The public catalogues", with
   the paths filled in. It must run from the main checkout's merged
   `build-index.ps1` (the path above), not from a worktree. It writes
   `index.json` (no item yet: no real plugin exists), `themes.json` (41 themes)
   and the theme files in `files\` (unchanged names). To look first, run the
   `powershell.exe` part alone with `-OutDir` set to an empty folder and open
   `themes.json`. **What changes for older versions:** CabinetOS 0.1.0 and
   0.1.1 read `index.json` only, so once it is replaced they list no theme in
   their marketplace until they update. The new versions read `themes.json`.
   After GitHub Pages deploys (a minute or two), check
   `https://oliverd25.github.io/cabinetos-marketplace/themes.json`.
2. **Three window tests fail, and not because of this branch.**
   `ShellEndToEndTests` `With_auto_install_off_a_download_opens_its_dialog_once_and_Later_keeps_the_pill_and_the_dot`,
   `With_auto_install_the_download_is_swapped_in_and_the_status_bar_only_asks_for_a_restart`
   and `A_swap_that_fails_says_so_in_the_status_bar_and_the_old_version_stays`
   expect an update to "0.2.0" and the window says "0.1.0". They failed the same
   way on a run of main's `a8e45ae` on the laptop earlier in this session, and
   they fail with this branch (laptop runs at `7423d26` and `6dffc42`). The
   update code is not touched here.
3. **Window runs on this PC need the installed `CabinetOS.exe` closed.** I ran
   nothing here. If you want the PC's own numbers for this branch, close it and
   run `ui\livecheck\run-livecheck.ps1` after `wait-for-pc.ps1`.
4. **The desk card** "Development Plan (mirror of the repo plan)" is not
   refreshed: I changed one line of `PLAN.md` and have no access to the desk.
5. **The merge.** `docs/log/2026-10-03/README.md` is new in this branch; another
   session that writes the same file will conflict there, in a table row only.

## Not done

- Nothing in the handout was left undone, except the live check and the window
  tests on this PC (above).
- The speed review (`speed-review.ps1`) was not changed. Its `market` scenario
  now measures the Extensions page, which has a few cards, not 41 tiles. There
  is no scenario that measures the gallery (see below).

## Noticed, out of scope

- **No speed measure of the gallery.** The gallery makes its first 12 tiles at
  once and the rest in slices of 8, as the old marketplace page did, but nothing
  measures the frames while the tiles are made. Article 1 asks for it; a
  `gallery` scenario in `speed-review.ps1` would do it.
- **A tile has no Source link.** The catalogue item keeps `source`, and the
  collection's CSV has it; the tile does not show it.
- **Settings gaps 5 to 8 of the audit remain** (the terminal's defaults, the
  update settings, the editor and the log level, and the Settings page for the
  advanced keys such as `marketplace.themes`).
- **`remote-livecheck.ps1` assumes its branch is checked out in the shared
  laptop clone** (`remote-tests.ps1` checks it out itself). Two sessions on
  one clone step on each other: this happened twice. A lock file, or a clone
  for each session, would stop it.
- **The `release-media.ps1` item `marketplace`** reads the public catalogue,
  which has no extension in it, so its Extensions page is empty until the
  first real plugin is published or the item uses `-LocalCatalogue`.
- **A live check line can click at the screen's corner**: `ClickRail` was the
  one; I fixed that one. Other helpers that take a rectangle from a search by
  name should be looked at the same way.
