# Column widths: drag, double-click fit, saved in the config: the coder's hand-back

The creator's request of 2026-09-30, evening: "the column widths should
be editable in any theme; the user can drag them with the mouse; also a
double click on the header makes the column as wide as the widest text
it contains below." The planning session decided: Modified, Type and
Size get a width in pixels each and Name takes the rest, never below the
theme's minimum; a grip on each divider, minimum 40 px; a double-click on
a grip or a heading fits that column, the Name heading fits the other
three; "widest text" means the rows on screen, because the pane
virtualizes and measuring a 100,000-row listing would stall the window;
one set of widths for both panes, saved as `ui.columns` in
`cabinetos.json` when the drag ends (Article 6), the user's widths winning
over any theme's weights; two commands, `view.fitColumns` and
`view.resetColumns` (Article 7). Handed to a coder on Opus in its own
worktree. The hand-back follows, as reported. The planning session's
checks on the main checkout: main pulled at 8615897; the five core checks
green (797 tests); the release core rebuilt; the window's build with
warnings as errors clean; 987 of 1007 tests in the fast run (20 skipped,
the end-to-end tests, which the coder ran: 1007 of 1007). The live check
with real keys follows in the same session. One decision of the coder to
know: the Modified|Type and Type|Size grips trade width with the column
to their right, because with Name taking the rest a divider that takes
its width from Name would stand still while the Name|Modified divider
moved instead; the planning session accepted it.

---

Column widths: done and pushed to origin/main (45668c8..8615897).

1. Commits on main
- 03e3a88 core: ui.columns and the two column commands, so the panes' column widths can be the user's
- f7db9dd window core: ColumnLayout, the column widths' rules without a window
- 50c9fe3 window: grips at the column dividers, double-click fits, and one saved set of widths for both panes
- 10f3fd4 tests: the column widths end to end, on a real window and core
- 907283d live check: section 19, the column grips with the real mouse
- 8615897 docs, changelog: column widths, the grips, the fit and ui.columns

2. What was verified
- Core, after the rebase: build ok; test 797 passed, 0 failed, 6 ignored; clippy -D warnings clean; fmt --check clean; deny: advisories, bans, licenses and sources ok. The schema was regenerated. The release core was rebuilt in the worktree.
- Window: `dotnet build -warnaserror` gave 0 warnings. The fast tests: 1007 total, 987 passed, 20 skipped. The full run with `CABINETOS_UI_E2E=1` and the worktree's release core: 1007 total, 1007 passed, 0 failed, 0 skipped. That covers every end-to-end class, `ColumnsEndToEndTests` included. build/check-scripts.ps1 parses livecheck.ps1 in PowerShell 7.6 and 5.1.
- End-to-end evidence, from the same steps run by hand. Display scale 1.5, sidebar hidden, list 557.3 px. Values are Name/Modified/Type/Size, and both panes showed the same:
  - Start: 246.7/135.3/111.3/64. The theme's split computes 246.65/135.66/110.99/64.
  - `column-drag:2|40`: 246/176/71.3/64. The rows equal the header. The grips sit 0 px from the dividers. "columns saved", and the file has {"modified":176,"type":71,"size":64}.
  - `column-fit:type`: the widest text was "Markdown Source File", 120 px plus 8 = 128. Type showed 128 and Name 189.3.
  - `cmd:view.resetColumns`: 246.7/135.3/111.3/64 again, and the file has `ui.columns: null`.
  - After a second drag, `theme:commander-compact`: 246/176/71.3/64. The user's widths were kept, and the list grew to 581.3.
  - A second window on that file showed 246/176/71.3/64 at start and logged "columns changed" how=config.

3. Decisions the plan did not cover
- Grips 2 and 3 trade width with their right-hand neighbour: the Modified|Type grip makes Modified +dx and Type −dx; the Type|Size grip makes Type +dx and Size −dx — because with Name taking the rest, "left edge stays", "Type keeps its width" and "the divider follows the pointer, nothing else moves" cannot all hold, and taking dx from Name would leave the dragged divider standing still — undo: in `ColumnLayout.Drag`, take dx from Name for those two cases. The creator should confirm this in the live check.
- With the theme's widths, the grids keep today's star weights, not four pixel widths — because that is exactly today's layout at every width, with no re-set on resize; `Resolve` gives the same split in numbers for the logs and the drag's start — undo: have `GridSizes` return pixels from `Resolve(available)`.
- A fit never leaves Name under its minimum; it caps the column, and `FitAll` shares the shortfall across the three — because otherwise Size would be pushed off the pane's edge — undo: drop the `room` cap in `Fit` and `FitAll`.
- Because of that cap, the end-to-end test hides the sidebar first — because at 1200 px and scale 1.5 the Type fit was capped — undo: remove `cmd:view.toggleSidebar` from the test.
- A heading's fit always counts the chevron's room (4 + 8 px), shown or not — because a later sort must not cut the heading — undo: `FilePane.MeasureColumn`.
- The Type cell has no link glyph in this version: a link's badge sits on the Name icon. Type's extra room is its 8 px text gap only.
- `UiSettings.Columns` is `ColumnWidths?` (Modified, Type, Size as double), not three nullable doubles — because the core stores all three or none — undo: `UiSettings.cs`.
- A column already under 40 px (a hand edit may set 24) keeps its width during a drag instead of jumping to 40 — because a column that jumps looks broken — undo: `Floor()` in `Drag`.
- A drag that moved nothing, and a fit or reset that changes nothing, do nothing and save nothing — because a double-click presses twice — undo: `OnGripDragCompleted` and `ChangeColumns`.
- Configuration is compared with the widths on screen, and it waits while a write is on its way or a drag is running — because otherwise the window's own save comes back as a "config" change — undo: `ApplyColumnSettings`.
- `column-drag:` and `column-fit:` do not wait, so an `until:config` right after them sees the save — because with a 300 ms wait, `until:config` always timed out after 20 s — undo: `RunColumnStep`.
- Logs: "columns changed" also carries `user`. A fit adds a "columns fitted" line with what it measured. The warning is "columns not saved" with the core's reason. For this, `SettingsWriter.SetOrRefusalAsync` was added, and `SetAsync` now wraps it.
- The grips sit inside the header grid's columns with a negative margin, not on an overlay canvas — because the layout then places them with no code; the "columns shown" line checks a 0 px offset — undo: `FilePane.xaml`.
- Two rows were added to docs/keybindings.md for the two commands. Live check section 19 comes after section 18 and ends with the palette's Reset Column Widths, so the file ends as the run found it.
- The commit trailers say "Claude Opus 5.5", not "Claude Fable 5.1": Opus 5.5 wrote these commits, and the session's attribution rule asks for that. The first commit was amended before any push to fix it.

4. What I could not do
- The local `main` is not fast-forwarded. It is checked out in the main checkout, git refuses to update it from a worktree, and this agent may not run git outside its worktree. origin/main has all six commits. From the main checkout, run `git merge --ff-only origin/main`.
- I did not run ui/livecheck, as instructed. A real pointer drag and a real double-click on a grip or a heading are verified by no test yet. The end-to-end test drives the grip's own drag steps and the fit method. That includes whether a grip's DoubleTapped still fires after it captured the pointer.
- Rebase conflicts in docs/ui.md (the snapshot steps row) and CHANGELOG.md ("Added") were resolved keeping both sides. FilePane.xaml.cs, MainWindow.xaml.cs and livecheck.ps1 merged on their own. All checks above were run after the rebase.
