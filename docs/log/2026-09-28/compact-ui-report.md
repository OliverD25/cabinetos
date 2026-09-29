## Report: Commander Compact, the shell's part

> Written by the planning session on 2026-09-29 from the agent's four
> commits, its progress notes in its transcript, and the documentation it
> wrote (`docs/ui.md`, "Metrics and chrome"). The agent's session ended on
> the account's model limit at 06:23, right after its last push and before
> its own test run and this report. "Decided" therefore lists what its
> commit messages and notes say it chose, in its words where they exist.

Context: CabinetOS shell (`ui/`), the shell's half of the Commander Compact
density preset, after the core's half (9e4ce98: the theme format's 76
`metrics` and three `chrome` switches, the shipped `commander-compact`
theme, `has_metrics` in `list_themes`). Nothing under `core/` or `sdk/`
touched.

## Built

- **The protocol mirror** carries a theme's `metrics` and `chrome` objects
  and `list_themes`' `has_metrics` (e037baa).
- **`MetricsMapper`** in `CabinetOS.Core`: one table with every metric's
  name, unit, bounds and the default look's value. A test holds the table
  equal to `sdk/themes/theme.schema.json` (the same 76 names and bounds,
  the default each description gives); another checks every value of
  Commander Compact. The mapper gives one value per name: the theme's, held
  to its bounds and rounded to whole pixels, else the default look's. A name
  that is not a metric changes nothing and is named in the log.
- **The window lays itself out from the metrics in effect** (fb0d9a9):
  `WindowMetrics`, one `ApplyMetrics` per view (the file panes and rows,
  the sidebar, the command bar, the title bar's tab, the terminal dock and
  its page, the marketplace's tabs and cards, the palette's and menus'
  rows, the status bar). `theme_changed` re-lays the whole window without a
  restart. A theme without metrics gives the default look's values, so
  switching back to `default` restores every size: the snapshot aid's
  layout line is identical after a switch back and at a fresh start.
- **The chrome elements**, each behind its switch: the function-key bar
  (its own control between the panes and the status bar; F3 View, F4 Edit,
  F5 Copy, F6 Move, F7 Mkdir, F8 Delete, Alt+F1 Drv; each button runs the
  command its key runs through the router with the trigger `fkeyBar`; never
  a Tab stop and never the keyboard's; collapsed with its row when off;
  accessible names "F5 Copy" and so on), striped rows with a stronger
  selection over them, and hairlines instead of floating cards.
- **The handout's text choices ride on `hairlines`**: the Size column in
  fixed-width figures (Fira Code where installed, else Cascadia Mono), a
  drive's free space without "free", dates older than a week without their
  time, and short types ("Folder", a link's kind, the extension in
  capitals). The status bar's layout item starts with the density preset's
  name. The picker marks density presets from `has_metrics`.
- **The shades of the chrome** come from the theme's `textPrimary` at the
  handout's alphas (nine new brushes), so a light theme gets dark lines.
- **The snapshot aid** gained `size:`, `fit:`, `theme:` and `layout:`,
  which measure the acceptance: whole rows and their height, texts cut
  short, the function keys' widths, every radius over 3 px outside the
  overlays, and the keymap's fingerprint. A fixture shaped like the
  handout's page (`fileforge` and its `src`, with the page's names, sizes
  and kinds of dates) makes the clipping check compare like with like.
- **The live check** (8385971): `livecheck.ps1` switches to Commander
  Compact with the picker's chord and keys, reads the row height and the
  bar from the window's log, checks that Tab never lands on a function key,
  presses F5 through the bar's button by its accessible name and checks the
  copy on disk, then switches back to 30 px rows.
- **Docs and snapshots** (f95081e): `docs/ui.md` "Metrics and chrome"
  (where each of the 76 metrics lands, the eight the window cannot take
  yet, the chrome elements, the acceptance as measured, where the window
  differs from the handout's page), the pointer in `docs/themes.md`, the
  snapshot aid's table with the new steps and the 11a steps it was
  missing, and seven half-size snapshots `docs/log/2026-09-28/compact-*.png`.

## Commits

- e037baa: ui: read a theme's metrics and chrome, and map every metric
- fb0d9a9: ui: the window takes a theme's sizes and chrome live, with the F-key bar
- 8385971: live check: Commander Compact, switched live with real keys
- f95081e: docs: metrics and chrome in the shell, with Commander Compact's snapshots

## Checks

- The agent's own, from its notes: 630 UI tests passed on a Debug build at
  03:12 (one skipped: the opt-in two-window test), the terminal tests
  after the `TerminalSpacing` test was added, and a Release build with
  warnings as errors, 0 warnings, at 03:22. Its clean Debug build and its
  run with the two-window test switched on never happened.
- The planning session's, in the agent's worktree at f95081e, 20:58 to
  21:00: `dotnet build -c Debug -warnaserror` 0 warnings, 0 errors;
  `dotnet test` with `CABINETOS_UI_E2E=1`: 631 passed, 0 failed, 0
  skipped, 43 s; `dotnet build -c Release -warnaserror` 0 warnings, 0
  errors. Accepted on that run.

## Live check

With the snapshot aid (release builds, the display asleep, a screen scale
of 1.5), from `docs/ui.md` "Checked":

- Dual pane at 924 px: rows 20 px high; no name, date, type or size cut
  short in either pane; the seven function keys 130 to 131 px wide, none
  cut short; no corner radius above 3 px outside the overlays. The default
  look at the same width cuts 19 dates and types short in the same folders.
- The list made 400 px high: 20 whole rows of 20 px.
- At 1600 px with the terminal open, in the marketplace and in its detail
  column: no radius above 3 px.
- The keymap: 67 bindings with the same fingerprint at a start in
  `default`, in `commander-compact`, and after switching back.
- Switching back to `default` live gave the same sizes, number for number,
  as a fresh start; the two images differ in 80 of 3.24 million pixels,
  all on the edges of rounded corners.
- F5 Copy pressed through its automation peer ran `file.copyToOtherPane`
  with the trigger `fkeyBar`, and the file was copied.
- The real-key run of the live check's new step waits for an unlocked
  screen.

## Decided

- `backdropOpacity` above plain Mica's 0.86 lays Mica's own base colour
  over Mica, up to opaque at 1 (Commander Compact's 0.94 is a tint opacity
  of 0.91 in dark mode); a theme's own `mica` tint wins. Because plain Mica
  cannot be made denser any other way. Undo: ignore the metric.
- The chrome's shades are theme tokens taken from the text colour, as the
  other overlays are, so a light theme gets dark hairlines. Undo: fixed
  white alphas.
- Short dates and short type names ride on `hairlines` although the
  handout's list does not name them: without them the shell's type names
  and long dates are cut short at 924 px, which the acceptance forbids.
  Undo: drop the two from the `hairlines` set.
- The title bar is never lower than 32 px: Windows draws the caption
  buttons that high, so Commander Compact's 30 becomes 32. Undo: none
  possible.
- The base text of the default look is the design's 13 px; it was WinUI's
  14 px. Undo: set `fontSize` 14 in the default theme.
- The status bar's prefix is the theme's name ("Commander Compact"), with
  a capital C and no git branch item (no such item exists). Undo: the
  handout's exact string.
- The overlays keep their 8 px corners (the palette's frame, the context
  menu, the transfer card, the dialogs): the acceptance excepts them.
- Snapshots at half the rendered pixels, the convention of the earlier
  ones. A fixture that mirrors the reference page's folders for the
  clipping check. A step that opens a marketplace card's detail column so
  its radii are audited too.
- The terminal page's own look (xterm.js's line height 1.25 and its
  paddings) stands for the design's values; a theme's values scale it by
  their share of those.

## Needs the user

- The real-key run of the new live-check step on an unlocked screen
  (`ui/livecheck/livecheck.ps1`).

## Known gaps

- Eight metrics have no place in the window yet: `captionButtonWidth`
  (Windows draws the caption buttons 46 px wide), `tagRadius` and
  `tagFontSize` (no tags), the three Markdown Preview sizes (the tool
  messages carry no sizes), `hexRowHeight` and `hexColumnGap` (no hex
  view).
- Where the window differs from the handout's page: the title bar's
  height and caption buttons; one "Default" tab and no Workspaces or Tags
  (those features do not exist); types as extensions where the page has
  words; the PC's short date instead of "2 Sep 2026"; Segoe UI Variable and
  Cascadia Mono for the page's web fonts; no list button at a pane header's
  right end.
- The two small items queued after this task (text on accent fills
  following the accent's lightness; `CABINETOS_WEBVIEW2_DIR`) were left
  when the session ended; the planning session did them.
