# Phase 24: sorting by the column headings, and the dividers

Branch `worktree-agent-a4c5f0b65676ff081`, coder on Sonnet 5.5, 2026-10-03.
The handout is the plan's Phase 24 ([PLAN.md](../../PLAN.md)) with a longer
brief; nothing here was decided by the creator while the work ran.

## What I found first (the facts behind the unit, checked in the code)

- The pane's column headings have three grips for the widths, and a click on a
  heading did nothing: confirmed.
- Three dividers could be dragged, all through `SplitterBar`: the Tool Dock's
  two (`BottomSplitter`, `RightSplitter`) and the sidebar's (`SidebarSplitter`).
  Two could not: the gap between the panes (two `*` columns, no bar) and the
  sidebar's divider in the classic and right layouts (`SidebarSplitter` was
  shown only when `_railLayout` was on, and `SidebarWidthFor` ignored
  `ui.sidebarWidth` unless it was). Confirmed.
- **One premise of the handout is not true.** It says sorting exists as four
  palette commands that write `panes.sort.key` and `panes.sort.descending`.
  They do not. `view.sortByName`, `view.sortByExtension`, `view.sortByModified`
  and `view.sortBySize` set the pane's *own* order (`PaneModel.Sort`, per tab,
  sent with each listing); `panes.sort.*` is only the order of a pane that has
  none (`PaneModel.DefaultSort`). The sorted heading's chevron existed already,
  too. The handout's own rule decided it: "read how the sort keys are scoped
  today and keep that scope". See "Decided without the creator", 1.

## What I built

### 3.1 Sorting by the column headings

- A click on Name, Modified, Type or Size runs the key's own command on the
  pane clicked, with the trigger `heading`, so the headings, the palette and
  Ctrl+F3 to Ctrl+F6 are one path (`SortActiveAsync`, now through
  `SortPaneAsync`, which logs "pane sorted"). The same key again reverses.
  The chevron that was there follows the order.
- A click anywhere in a heading's cell counts: the header row's grid has a
  transparent background and takes the clicks, and `FilePane.ColumnAt` names the
  column from the pointer's x. A grip marks its clicks handled, so a grip is a
  resize and never a sort.
- Click against double-click: the first click sorts at once (a sort that waited
  for the double-click time would make every sort slow). `HeaderClicks` (Core,
  tested with a fake clock) ignores the second click of a double-click, and the
  double-click's own event takes the first sort back (the pane's own order from
  before, none included: `PaneModel.SortAsync` takes a null) and then fits the
  column as before.
- Files: `ui/CabinetOS.Core/Listing/HeaderClicks.cs`, `PaneSort.ForColumn`,
  `ui/CabinetOS.Core/Platform/DoubleClick.cs` (`GetDoubleClickTime`),
  `ui/CabinetOS/MainWindow.Headers.cs`, `Views/FilePane.xaml(.cs)`,
  `Views/ColumnGrip.cs`, `ViewModels/PaneModel.cs`.

### 3.2 The divider between the panes

- Core: `ui.paneSplit` (0.2 to 0.8 or `null`) in the config model, its range
  check in `parse.rs`, the schema, `view.equalPanes` ("View: Equal Panes", no
  key, no context) in the registry. Tests: the model, the parse, the store's
  round trip, the registry entry.
- Window: a `SplitterBar` in `PanesGrid` over the seam (`PaneSplitter`), the two
  columns as `share*` and `(1-share)*`. A drag writes the share once, a
  double-click on the divider and the command write `null`, the file's value
  applies at start and at `config_changed`. `PaneSplit` (Core, tested) decides
  the numbers: the least pane width is the design's column minimums (Name's
  minimum, 3 x 40 px, the gaps and the header's padding; 268 px in the default
  look), the share is held to it in the width at the moment and the setting is
  not changed by that. In single-pane mode there is no divider.
- Files: `core/crates/cabinetos-config/src/{model,parse,store,lib}.rs`,
  `core/crates/cabinetos-commands/src/registry.rs`,
  `core/crates/cabinetos-core/tests/config.rs` (the command count),
  `sdk/config/cabinetos.schema.json`, `ui/CabinetOS.Core/Presentation/PaneSplit.cs`,
  `Settings/UiSettings.cs` (`PaneSplitShare`), `Settings/SettingsWriter.cs`
  (`SetNumberAsync`, `SetNullAsync`), `ui/CabinetOS/MainWindow.PaneSplit.cs`,
  `MainWindow.xaml`, `Views/SplitterBar.cs` (`DoubleClicked`).

### 3.3 The sidebar's divider in every layout

- `SidebarSplitter` shows whenever the sidebar is open, and `SidebarWidthFor`
  gives the dragged width in every layout. A drag still writes
  `ui.sidebarWidth` and snaps shut under 150 px; a double-click on the divider
  writes `null` and gives the design's width back (`ResetSidebarWidth`).
- `SidebarSizing` itself did not need a change: its functions never knew the
  layout. Its tests got a case for each layout's design width and limits.
- The Tool Dock's two splitters: the code is untouched (`SplitterBar` only got
  an event). They still drag and save: see the checks below.

### 3.4 Documents

`docs/ui.md` (the headings, the double-click rule, the divider between the
panes, the sidebar's divider, the snapshot steps, the live check's section 24,
the remembered-state table; the "not in this version" row about heading clicks is
gone), `docs/config.md` (`ui.paneSplit`, notes at `ui.sidebarWidth` and
`panes.sort.*`), `docs/keybindings.md` (`view.equalPanes`), `CHANGELOG.md`
(Added: three lines), the audit row of `.claude/skills/settings-three-ways/SKILL.md`,
and one status line under Phase 24 in `docs/PLAN.md`.

## The checks

Commits on the branch, in order: `f3529d6` (core: `ui.paneSplit`,
`view.equalPanes`), `bcb120c` (window: headings and dividers), `7c6d32d` (docs and
the live check's section 24), `1b90505` (merge of `main`, so the laptop's clone
could take the branch as a fast-forward), `6802f2c` (the double-click fix below),
and the commit that adds this report, the plan line and the README row.

| Check | Where | Result |
|---|---|---|
| The core's five checks: `cargo build --workspace`, `cargo test --workspace`, `cargo clippy --workspace --all-targets -- -D warnings`, `cargo fmt --all -- --check`, `cargo deny check` | this PC, in the worktree, on the final tree | all five exit 0. `cargo test`: 900 passed, 0 failed, 6 ignored. |
| New Rust tests | same | 3 new: the range of `ui.paneSplit` in `parse.rs`, its round trip through the file in `store.rs`, and `view.equalPanes` in the registry. Two count checks went from 116 to 117 commands. |
| Window build, Debug, whole solution, `-warnaserror` | this PC and the laptop | 0 warnings, 0 errors on this PC. The build in the laptop's full run exited 0. |
| Window build, Release, `-warnaserror`, and the release core | this PC (the live check runs these builds) | 0 warnings, 0 errors. |
| Fast tests (`dotnet test --solution CabinetOS.sln --no-build`) | this PC | 1377 tests: 1314 passed, 63 skipped (the opt-in end-to-end tests), 0 failed. |
| New unit tests | this PC | `HeaderClicksTests` (8 tests and one with 4 rows), `PaneSplitTests` (7 tests and one with 9 rows), 2 in `SettingsTests`, and 2 in `SidebarTests` (one with 4 rows). |
| End-to-end tests, the whole suite | the laptop (not this PC: see below) | 1377 tests: 1372 passed, 5 failed. The 3 new ones in `DividersEndToEndTests` passed. The 5 failures are explained below. |
| End-to-end tests, the 2 failures that are not known ones, run alone | the laptop | 2 of 2 passed. |
| Live check, the real keyboard and mouse | the laptop, 02:05 to 02:15, the third run | ran to its end, no stop line, "Checks that answered False: none". All 13 results of section 24 are True. |
| Panel goal (no frame with UI work over 33 ms, under 5 % with UI work over 20 ms) | the laptop, same run | met: 273 frames in 5 s, 0 with UI work over 20 ms, 0 over 33 ms. |
| Live check, the real keyboard and mouse | this PC | **not run.** See "Needs the creator", 1. |
| End-to-end tests | this PC | **not run**, same reason. |

### The 5 failed end-to-end tests

None of them looks caused by this branch. Each one is recorded, and no test was
loosened.

- `ShellEndToEndTests.With_auto_install_off_a_download_opens_its_dialog_once...`,
  `...With_auto_install_the_download_is_swapped_in...` and
  `...A_swap_that_fails_says_so_in_the_status_bar...`: the three update tests.
  They fail in the same way on `main` on the laptop (checked earlier the same
  night). For example, the failed-swap test expects "CabinetOS 0.2.0 could not
  be installed" and gets "CabinetOS 0.1.0 is installed; restart to use it". I
  did not look for the cause. Not part of Phase 24.
- `StartEndToEndTests.The_core_is_started_while_the_window_is_built_and_answers_hello_at_once`:
  the test wants the first `hello` reply in under 60 ms. It took 68.5 ms in the
  full run (159.8 ms in the Phase 23 coder's run on the same laptop). It passed
  when run alone. A timing limit on a busy laptop.
- `ShellEndToEndTests.Quick_Open_finds_in_the_repository...`: the window's log
  had one error line, an unobserved `COMException` ("Element is already the
  child of another element") from `CommandPalette.BringHighlightIntoView`
  (`CommandPalette.xaml.cs:140`). It passed when run alone. This branch does not
  touch the palette (`git diff main` shows no change in it).

### How the live check went (three runs)

1. Run 1 did not test section 24 at all. The remote script only fast-forwards the
   laptop's clone, and my branch was not a descendant of the clone's commit, so
   the laptop ran the old code. I merged `main` into the branch and ran again.
2. Run 2 found three faults, all of them real. They are in the next list.
3. Run 3 (after `6802f2c`) is clean, and it is the run in the table above.

## Findings about the dividers

- **The dock's two splitters still drag and save.** The bottom one: the live
  check dragged it with the real mouse, up 60 px and then down 40 px, and
  `ui.dockSize.bottom` was 234 px, then 194 px (True). The right one (the layout
  `right`, where the dock is beside the panes): the end-to-end test drags it
  with the window's own steps, `dock:340`, and `ui.dockSize.right` is 340 in the
  file. `dock:330` does the same for the bottom one. Both leave the other
  dividers' keys alone. I did not change their code. `SplitterBar` only got one
  new event.
- **A double-click on a divider used to write the value back.** WinUI raises
  `DoubleTapped` while the second press is still down, before the release. The
  divider's release ends the drag and saves what the drag left. So "double-click
  for equal" cleared the share and then the release wrote it back. The real
  mouse also moves a fraction of a pixel between press and release, which made
  it worse. Only the real mouse showed this: the window's own test steps did
  not. Fix: a press that moved under 1 px is no drag, and the double-click ends
  the drag that it is part of (`_paneSplitMoved`, `_sidebarMoved`). The test
  steps `pane-divider-reset` and `sidebar-divider-reset` now send the events in
  the real order (press, a 0.3 px move, the double-click, the release). The live
  check also reads the file 1.5 s after the double-click.
- **The sidebar's width now applies in every layout.** Before, `ui.sidebarWidth`
  was read only in the rail layout, so a width saved there did nothing in
  the classic and right layouts. Now one width is shared. See "Decided
  without the creator", 5.
- **Where the sidebar's divider is in the classic layout.** It is the 8 px
  strip at the left pane's left edge. The live check finds it from the Name
  heading (19 px before the heading's text), because a log line of an
  earlier section is no good for that: it said the width section 13 left, not
  the one now.
- **Windows PowerShell 5.1 reads a number with decimals from JSON as a
  `[decimal]`, not a `[double]`.** The first version of the live check used
  `-is [double]`, which was False for a correct value. It now casts. No other
  check in the script uses `-is [double]`.
- **The headings.** A click in the heading's cell sorts, not only on the text:
  the header row's grid had no background, so a click on empty cell space did
  not reach it. It has a transparent one now. A grip click is marked handled,
  and the live check's double-click on Type fits the column (2 fits, 1
  before) and leaves the last sort as the first click set it.
- **Section 24, the lines that passed** (laptop, 02:12 to 02:15): the Size
  heading sorts the left pane by size, largest first, as its own order; the same
  heading again reverses; Name gives A to Z as the pane's own order; the right
  pane is not sorted by any of it; a double-click on Type fits the column and
  takes the first click's sort back; the divider dragged 120 px moved the Size
  heading 119.2 px and wrote 0.624, the same share the log has; a double-click on
  the divider wrote null and the heading came back; Equal Panes from the palette
  after a second drag wrote null; the sidebar dragged 80 px wrote 304 and moved
  the left pane 80 px; its double-click wrote null and the pane came back; no
  warning or error line in the log; the dock's bottom splitter saved.

## Decided without the creator

Each line says what I did, why, and how to undo it.

1. **The headings keep the pane's own order, and do not write `panes.sort.*`.**
   The handout (and the plan) say the sort commands write `panes.sort.key` and
   `panes.sort.descending`. They do not: `view.sortBy*` set the order of one pane
   (per tab), and `panes.sort.*` is only the order a pane has when it has none.
   The handout also says to read how the keys are scoped and keep that scope, so
   I kept it: a click on a heading runs the same `view.sortBy*` command as the
   palette and Ctrl+F3 to Ctrl+F6. Undo: in `MainWindow.Headers.cs`, make
   `OnHeaderSortRequested` write `panes.sort.*` with `SettingsWriter` instead of
   running the command. Then one click would change the default of every pane,
   which is a bigger change than the creator's card asked for.
2. **A click sorts at once, and a double-click takes that sort back.** The other
   way is to wait for the system's double-click time before sorting. That makes
   every sort feel slow. `HeaderClicks` (tested with a fake clock) ignores the
   second click of a double-click, and the double-click event undoes the first
   click's sort and then fits the column, as before. Undo: delay the sort in
   `FilePane.OnHeaderTapped` by `DoubleClick.Time` and drop `HeaderClicks`.
3. **`ui.paneSplit` is a share of the width (0.2 to 0.8), not pixels, and the
   window's clamp does not rewrite it.** The least width of a pane is the
   design's column minimums (268 px in the default look). In a narrow window
   the panes are held to that and the file keeps the share, so the share comes
   back when the window grows. Undo: remove the key from the model, the schema
   and `docs/config.md`.
4. **A drag saves once, on release, rounded to three decimals.** Same rule as the
   dock's size. A move under 1 px at the start is no drag (see the findings).
5. **`ui.sidebarWidth` applies in every layout, and its divider shows in every
   layout.** One width for all layouts, because a second key for the classic
   layout would be a setting nobody asked for. A creator who saved a width in the
   rail layout will see the same width in the classic layout. Undo: in
   `MainWindow.Rail.cs`, `SidebarWidthFor` and `SidebarSplitter`'s visibility
   go back to the `_railLayout` test.
6. **The sorted heading shows an arrow for Name A to Z when the pane has its own
   order.** Before, no arrow showed for that order. With the pane's default order
   (nothing clicked) it still shows none. Undo: `UpdateSortGlyphs` in
   `FilePane.xaml.cs`.
7. **`ui.paneSplit` is a `PaneSplit(f64)` type with a hand-written `Eq`.** The
   config types derive `Eq`, and `f64` has none. The value cannot be NaN: JSON
   has no NaN, and the range is checked when the file is read. Undo: make the
   field an integer of per mille and change the three places that read it.
8. **I merged `main` into the branch** (`1b90505`) so the laptop's clone, which
   only fast-forwards, could take it. Nothing was merged into `main`. Undo: the
   merge commit is on the branch only.
9. **The end-to-end tests ran on the laptop and not on this PC.** The creator's
   rule needs the consent file, and it never said "allowed" (see below). The
   project's rules allow the laptop for the suite when this PC is held or busy.
10. **I did not refresh the desk card** "Development Plan (mirror of the repo
    plan)". The rule asks for it when `docs/PLAN.md` changes, but the change is
    on an unmerged branch, and the desk is an outside service. It should be
    refreshed after the merge.

## Needs the creator

1. **The live check and the end-to-end tests on this PC did not run.**
   `wait-for-pc.ps1` waited its 30 minutes and stopped: the consent file did not
   say "allowed", and the creator's own installed `CabinetOS.exe` was running.
   No window opened on this PC. The laptop ran both. If a run on this PC is
   wanted, say "allowed" in the chat and the creator's session can run
   `wait-for-pc.ps1 -Set allowed` and the suite.
2. **Merge the branch** `worktree-agent-a4c5f0b65676ff081` into `main` (I did not).
   Then refresh the desk card from `docs/PLAN.md`.
3. **Look at the divider.** The live check saved screenshots on the laptop (the
   pane divider dragged and equal again, and the sidebar dragged:
   `24-panes-dragged-live.png`, `24-panes-equal-live.png`,
   `24-sidebar-dragged-live.png` in `_io/live-check`). The divider uses
   the same `SplitterBar` as the dock: an invisible strip that shows a 2 px line
   when the pointer is over it or it is dragged. Whether that look is right in
   the design is the creator's eye to judge.
4. **Decision 1 above**, if the card meant for the headings to change the default
   for all panes. It is a small change, but it is a different product choice.

## Out of scope, and noticed

- **Two agents shared the laptop's clone and their runs collided.** The Phase 23
  coder and this one both run `remote-tests.ps1` and `remote-livecheck.ps1` into
  the same clone, `C:\Dev\cabinetos\cabinetos`, and each checks out its own
  branch there. At 02:23 my test run failed to build because a process from the
  other run still held a file (`CS2012`, locked by `Microsoft.UI.Xaml.Markup.Compiler`).
  I waited until the laptop had no test processes and ran again. A lock file or a
  clone per branch would stop this. It is outside this phase.
- **The remote scripts fast-forward only.** A branch that is not a descendant of
  the laptop clone's commit silently stays on the old code, and the run looks
  fine (this is how live check run 1 passed without testing section 24). The
  script could stop with a message when the clone is not at the branch's tip
  after the pull.
- **A race in `CommandPalette.BringHighlightIntoView`** (the `COMException`
  above). It came up once in the full run and not alone, so it is rare. It is in
  Phase 23's neighbourhood, so I left it.
- **The three update tests** fail on `main` too (see above). The cause is not
  known to me.
- **The `StartEndToEndTests` limit of 60 ms** for the first `hello` is tight for a
  laptop that runs other work. It was not loosened.
- **The plan's Phase 24 text still says** the sort "goes through `panes.sort.*`
  and `set_value`". The status line under it says why that is not true. The
  text could be corrected when the creator reads this.
