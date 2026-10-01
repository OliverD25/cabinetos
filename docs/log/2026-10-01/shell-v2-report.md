# Shell redesign v2 (Phase 20): hand-back, coder on Opus, 2026-10-02 01:10

The coder's hand-back, word for word, saved by the planning session (the
coder's tools refused to write a report file). The work is on the branch
`shell-v2` (b8db193 on top of 17e4c83), pushed and not merged: the creator
asked to see a diff summary before it lands on main.

Context: v2 of the creator's shell specification, `docs/design/SHELL_REDESIGN.md` (2026-10-01). The reference build is `docs/design/CabinetOS Compact.dc.html`. All seven sections are built, tested and pushed on the branch `shell-v2`. Nothing is merged into main.

## Branch, commits, rebase, push

- Branch `shell-v2`. I rebased it twice onto origin/main, both times with no conflicts: first onto d64c8ec (the flat design folder), then onto 17e4c83, which brought 696fa3b (wait-for-pc.ps1) and the remote-script change.
- Pushed once: `git push -u origin shell-v2`, a new branch. No force-push. Main was not touched.
- Commits, oldest first, on top of 17e4c83:
  - 63eb9d2 feat: the theme metrics of shell redesign v2 (§6)
  - ffb9a97 feat: the quiet top row of shell redesign v2 (§1)
  - fa63c30 feat: the sidebar header switches the workspace (§2)
  - d4386d2 feat: each pane gets a toolbar row and a path row (§3)
  - 4c548b9 feat: the tab strip's v2 look (§4)
  - cde00e8 feat: the find widget drops from the pane's toolbar row (§5)
  - 1088920 feat: the layout snapshot step reports v2's sizes (§6)
  - 12982b2 test: the live check clicks v2's toolbar, drive chip, workspace row and chip
  - 5dd791f fix: the top row's title has one space each side of its dot
  - 3197e5a docs: ui.md, keybindings and the changelog describe shell redesign v2
  - 9ccad5d chore: comments and a log line name the workspace row, not the pill
  - be9d1f2 docs: the plan records Phase 20, shell redesign v2
  - b8db193 docs: the plan's Phase 20 names the commits after the second rebase
- Total against main: 42 files, +2142 −855.

## Diff per section

All paths are under the worktree `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.claude\worktrees\agent-ab05a53fb99de40bd`.

- **§6 metrics, 63eb9d2 (+206 −72).**
  - `core/crates/cabinetos-protocol/src/theme.rs` declares the new metrics.
  - The three generated schemas (`sdk/protocol/event.schema.json`, `response.schema.json`, `sdk/themes/theme.schema.json`) were regenerated.
  - `sdk/themes/commander-compact.json` is now 1.2.0.
  - Also changed: `ui/CabinetOS.Core/Themes/Metrics.cs`, `ui/CabinetOS.Tests/MetricsTests.cs` (88 to 93 metrics), `docs/themes.md`.
- **§1 top row, ffb9a97 + 5dd791f (+229 −250).**
  - `Shell/TopRowLayout.cs` holds the title, `TitleRoom` and `MinWindowWidth = 600`.
  - `MainWindow.xaml` has the title and the Quick Open chip; the pill and the command center are removed.
  - `MainWindow.Shell.cs` holds the title runs, the chip and the drag regions.
  - Also changed: `MainWindow.Metrics.cs`, `.Compact.cs`, `.Tabs.cs`, `.xaml.cs`, `FileContextMenu.xaml.cs` (an optional width), `ThemeMapper.cs`, `App.xaml`, `ShellTests.cs`, `ShellEndToEndTests.cs`.
- **§2 sidebar header, fa63c30 (+188 −16).**
  - `MainWindow.xaml` adds the `WorkspaceHeader` row.
  - `MainWindow.Shell.cs` adds the dropdown placement, the branch and the log lines.
  - Also changed: `MainWindow.Metrics.cs`, `ChromeKeyboardTests.cs`, `ChromeKeyboardEndToEndTests.cs`, `ShellEndToEndTests.cs`.
- **§3 toolbar and path rows, d4386d2 (+613 −178).**
  - New `Shell/PaneRows.cs`: the filter label, the drive label and the free space.
  - `Breadcrumbs.cs`: the collapse limits are 5 (dual) and 8 (single).
  - `Views/PaneCrumbs.xaml`/`.cs` are rewritten as the two rows.
  - `PaneTabs` loses Open with…
  - Also changed: `MainWindow.Find.cs` (`search.toggle`, and `search.focus` takes a pane), `MainWindow.Commander.cs` (the drive list opens under the chip), `MainWindow.Shell.cs`, `SidebarModel.Volumes`, `ThemeMapper.cs`, `App.xaml`, tests.
- **§4 tab look, 4c548b9 (+359 −134).**
  - New `Tabs/TabLook.cs`: the heights, the dividers, the × rule and the glyph.
  - `Views/PaneTabs.xaml`/`.cs` are rewritten as the band and the card.
  - Also changed: `ThemeMapper.cs` and `App.xaml` (9 new brushes), `MainWindow.Tabs.cs`, `TabTests.cs`, `TabsEndToEndTests.cs`, `ShellEndToEndTests.cs`.
- **§5 find widget, cde00e8 (+51 −14).** Changed: `FindWidget.xaml`/`.cs`, `MainWindow.xaml` (the find hangs from the toolbar row), `MainWindow.Metrics.cs`, `MainWindow.Shell.cs`, `ShellEndToEndTests.cs`.
- **§6 `layout:` step, 1088920 (+8 −1).** `MainWindow.Metrics.cs`.
- **Live check, 12982b2 (+86 −3).** `ui/livecheck/livecheck.ps1`, section 16 gets real clicks on the toolbar's Up, the drive chip (closed with Esc), the workspace row and the Quick Open chip.
- **Docs.**
  - 3197e5a (+343 −204): `docs/ui.md` "The shell" is rewritten, with the new subsections "The sidebar header", "The pane's rows", "Tabs in the shell" and "Find in pane". It also changes `docs/keybindings.md` (`search.focus` with a pane, `workspace.switch`) and `CHANGELOG.md` (Unreleased, Changed).
  - 9ccad5d (+11 −11): stale "pill" comments, one log line and the test step label "pill" renamed to "header".
  - be9d1f2 and b8db193 (+77 −1): `docs/PLAN.md` gets "Phase 20" in Phase 16's shape, with the status.
  - `docs/config.md` needed no change.

## §7 acceptance, item by item

1. **The top row at 924 px shows every control with no overlap; the toolbar shows nav, the drive chip, free space, Find and Open with… with no clipping.**
   - Passes, except Open with…: it stays hidden (decision 3).
   - Proof: `ShellEndToEndTests.The_top_row_fits_924_px_find_filters_one_pane_and_a_tab_keeps_its_place`, steps `start` (924 px) and `narrow` (620 px). `TopRowFits` checks title end < chip < view buttons < caption buttons. Each pane's `toolbar_items` is "back forward up drive free find", and an item counts only when it is shown whole.
2. **A five-part path is whole in a 440 px pane; a seven-part path collapses to `C: › … › parent › current`.**
   - Passes.
   - Proof: the same test, steps `five-parts` (pane width 438–442 px, `crumbs_fit`) and `seven-parts`. `ShellTests` covers the rule: 5 and 7 parts in dual mode, 8 and 9 in single, a root, a share.
3. **The tab in front and the toolbar share one fill; dividers appear between the other tabs, none next to the tab in front.**
   - Passes.
   - Proof: the same test, step `second`: `tab_fill == toolbar_fill` in both panes. `TabTests` checks `TabLook.Divider` and that no band lies under the card. `TabsEndToEndTests` reads `left_look` "folder,divider | folder | *lock,close,32".
4. **A second tab, then back: the first tab's path, cursor and scroll come back.**
   - Passes.
   - Proof: the same test, steps `scrolled`, `second` and `first-again` (scroll within 2 px).
5. **Ctrl+F filters only the active pane; the label reads `*query*`; Esc brings back the full list and `*.*`.**
   - Passes.
   - Proof: the same test, steps `found` and `closed`. The live check's section 16 does it with real keys.
6. **The workspace dropdown opens; a pick moves the left pane; the menus close on an outside click and on Esc.**
   - Passes.
   - Proof: `Quick_Open_finds_in_the_repository_...`. The dropdown opens at the row's left edge, bottom and width; Default puts the repository's root in pane 0; with the sidebar hidden it opens under the top row. Esc is checked in `The_menus_come_from_the_registry_...`.
   - The live check checks with the real mouse: the hamburger closes on an outside click and on Esc; the workspace dropdown (224 px wide) closes on Esc.
   - Limit: the outside click is tested for the hamburger only. The workspace dropdown uses the same light-dismiss surface.
7. **Tab switches panes; Ctrl+Tab cycles tabs.**
   - Passes.
   - Proof: `ChromeKeyboardEndToEndTests.The_keyboard_stays_in_the_pane_after_the_top_rows_buttons_and_the_workspace_row_are_pressed`, `KeysEndToEndTests`, and the live check's tabs section.

## Checks

- **Core five checks** (after the rebase): build, test, clippy and fmt pass, and `cargo deny` answers advisories, bans, licenses and sources ok. Tests: 813 passed, 0 failed, 6 ignored.
- **Window build** with `-warnaserror`, Debug and Release: 0 warnings, 0 errors.
- **Fast window tests** (after the second rebase): 1239 total, 1195 passed, 44 end-to-end skipped, 0 failed.
- **Full suite with the end-to-end tests:** 1239 of 1239 passed, 00:16:57 to 00:19:05.
  - This ran before the second rebase. That rebase brought only docs, `CLAUDE.md`, a skill and the livecheck remote scripts, so `ui/CabinetOS` code is the same.
  - The earlier full run at 23:47 had one failure, the title "CabinetOS  ·  data". 5dd791f fixed it.
- **Live check** on the final rebased Release build, `_io\live-check\run-2026-10-02-0055-shell-v2.txt`:
  - 209 True, 0 False, exit code 0.
  - Section 16 is all True, including the new clicks.
  - Scroll goal met: 301 frames, 0 over 20 ms, worst 18.1 ms.
- **`build/check-scripts.ps1`:** PowerShell 7.6 parsed 21 of 21. PowerShell 5.1 parsed 19, and skipped `notices.ps1` and `release.ps1` because they are for 7 only.

## Window runs and your messages

- The full suite ran at 00:16, inside your first window (from 00:15). Your message extending the hold to 00:55 came after that.
- My first live check start at 00:19 was cancelled at the countdown, and nothing ran. Someone at the PC cancelled it, most likely the creator.
- I stopped my waiting live-check job at 00:54:04, before it would have started. This was because of your wait-for-pc rule.
- The live check that counted ran through `wait-for-pc.ps1`. It exited 0 ("allowed until 07:30") at 00:55:11.

## Decisions (what — because — undo)

1. **The taskbar title stays "CabinetOS".** Only the top row shows "CabinetOS · folder". — Because the end-to-end tests and the compact drawer find the main window by its title. — Undo: set `AppWindow.Title` in `UpdateTitle` (`MainWindow.Shell.cs`) and update those window finders.
2. **The title's three runs are built in code.** — Because the line breaks between XAML `<Run>`s became extra spaces. — Undo: not needed.
3. **Open with… keeps its place after Find but stays hidden**, although §7 lists it. — Because of the creator's call of 2026-09-30: hidden until a second editor exists. — Undo: `OpenWithButton` visibility in `PaneCrumbs.xaml`.
4. **The toolbar's Find runs a window-only `search.toggle` with `{"pane": n}`.** It is not in the registry and has no key. — Because the button must toggle, while `search.focus` only opens or focuses, and a registry entry would mean a core change for a button. — Undo: register it in `registry.rs` and drop the `RegisterLocal` in `MainWindow.Find.cs`.
5. **`search.focus` accepts `{"pane": 0|1}`.** — Because a click on the filter label must open that label's pane's find. — Undo: drop the argument read in `MainWindow.Find.cs`.
6. **The workspace pick runs `go.toPath {"path": root, "pane": 0}`.** — Because the handout says "the left pane's current tab". — Undo: drop `pane`.
7. **The dropdown width is the row's width, at least 220 px.** With the sidebar hidden, it opens under the top row at the panes' left edge, at the menu's own width. — Because the handout gives no place for that case. — Undo: `WorkspaceMenuPlace` in `MainWindow.Shell.cs`.
8. **The workspace row's top corners follow `radiusSurface`**, as the panes' corners do. — Undo: `MainWindow.Metrics.cs`.
9. **The binding stays Ctrl+K Ctrl+W.** The handout writes "Ctrl+K W unchanged", and the existing binding is Ctrl+K Ctrl+W. — Undo: not needed.
10. **Tab heights come from `tabRow`.** `TabLook.Heights`: step = round((tabRow−20)/4); the tab in front is tabRow−step; the others are tabRow−2·step−2. This gives the handout's 32/26 at 36 px and 26/22 at 28 px. — Because it needs no extra metrics. — Undo: two explicit metrics.
11. **The band is drawn in pieces around the tab in front, never under it.** — Because a band under the translucent card would show the seam §7 forbids. — Undo: one band in `PaneTabs.xaml`.
12. **No soft shadow above the tab in front.** — Because WinUI 3 has no box shadow for a plain element. — Undo: a composition shadow on each card.
13. **Every folder tab has the folder glyph.** — Because v2 is newer than the creator's "no icon" call of 2026-09-30. — Undo: in `TabLook.Glyph`, give no glyph for a folder.
14. **The × keeps its place on every tab**, hidden where it does not show. — Because a tab's width must not jump when it comes to the front. — Undo: collapse instead of hide.
15. **The free space comes from the sidebar's `list_volumes`.** It changes when the volume list does, not after each copy. — Because the window reads no disk (Prime Directive 1). — Undo: ask `list_volumes` after a job ends.
16. **The old metrics stay accepted and size nothing:** `workspacePillHeight`, `workspacePillRadius`, `commandCenterHeight`, `commandCenterRadius`, `breadcrumbRowHeight`. — Because `deny_unknown_fields` would reject themes written before. — Undo: delete them from `theme.rs`.
17. **New default metrics:** `tabRow` 32→36 and `tabRadius` 0→8; compact 24→28 and 3→6. Other new defaults: toolbar 28/24, path 24/20, header 28/26, chip 24/22, tab maximum width 160/170. `navButtonSize` is unchanged at 20, and the buttons are 2 px wider than high. — Because these are the handout's values. — Undo: `theme.rs`.
18. **The path row's height includes its two lines.** The reference CSS adds the lines on top. — Because the handout says the row is 24 px. — Undo: add 2 px.
19. **The drive list opens under the drive chip, for Alt+F1 and Alt+F2 too.** — Because that is where the list belongs now. — Undo: `MainWindow.Commander.cs`.
20. **The compact drawer hides the Quick Open chip at its narrow widths**, as it hid the pill. Ctrl+P still works. — Undo: `MainWindow.Compact.cs`.
21. **The accent tint of v1's breadcrumb row is removed.** The active pane shows by its border and its toolbar's 9 % fill. — Because the handout says so.
22. **The five-part path test uses `%LOCALAPPDATA%`**, and the 440 px pane check uses a 1136 px window. — Because a test cannot create `C:\Users\dev`, and at 924 px with the sidebar shown a pane is narrower than 440 px. — Undo: not needed.
23. **The commit attribution is "Claude Opus 5"**, as the handout says. A system reminder suggested "Opus 5.5". — Undo: not needed.

## Skipped, and what you need to do

- **The report file and the day's README row were not written.** That is `docs/log/2026-10-01/shell-v2-report.md` and its row in `docs/log/2026-10-01/README.md`. My tool blocked writing report .md files ("Subagents should return findings as text"). This hand-back is the report. That folder keeps the hand-backs word for word, so save this text there, add the row, and commit it on main or on the branch.
- **The Phase 20 status in `PLAN.md` does not link to a report.** Add the link once the report file exists.
- **The Notion desk's plan card was not refreshed** after `docs/PLAN.md` changed. That is an outside service with a side effect, which my handout does not allow. The planning session should refresh it.
- **Nothing else was skipped.** No check failed three times.

## Seen on the way, not changed

- `docs/plan/phase-09-marketplace-themes.md:31` still says the handout is "in `docs/design/compact/`", and `docs/research/total-commander.md:266` links `../design/FileForge.dc.html`. Main's flattening of `docs/design/` made both stale.
- The live check's first step "tab to the other pane and back" presses Tab twice but prints no True/False line. Only later sections prove that Tab switches panes.
