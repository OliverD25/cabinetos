# The theme picker previews the highlighted theme: the coder's hand-back

The creator's request of 2026-09-30, evening: "make the theme change when
the cursor is hovering the theme, so the user can anticipate the theme
while not accepting it yet." The planning session decided the shape (the
preview follows the highlight by pointer and by keys, Article 7; a
preview is a `get_theme` painted by the window, nothing written; every way
out of the picker restores the theme in effect; 80 ms debounce with a
sequence guard; the picker's rows keep their sizes while a density preset
is previewed; the log lines "theme previewed" and "theme restored") and
handed it to a coder on Opus in its own worktree. The hand-back follows,
as reported. The planning session's checks: main pulled at 5eb14f7, the
window's build with warnings as errors clean, 957 of 974 tests passed in
the fast run (17 skipped: the end-to-end tests, which the coder ran, 974
of 974). The live check with real keys runs when the evening's other
fixes have landed.

---

Report: theme picker live preview (Ctrl+K Ctrl+T), 2026-09-30

1. Commits on main (pushed; origin/main went from 0697251 to 5eb14f7 by fast-forward, no rebase needed because origin/main had not moved)
- 7877bd1 theme picker model: the highlighted theme is previewed through get_theme
- b84515a theme picker model: choosing the theme in effect paints it back at once
- 8bd785f window: the theme picker's preview is painted, and every way out restores
- 7ae1c5f picker, guide, changelog: rows keep their sizes while a preset is previewed
- c6c84eb window test: the theme preview end to end, Esc restores and Enter keeps it
- 00b48ab live check: the theme picker's preview with real keys, before the compact steps
- 5eb14f7 window test: "theme chosen" and "theme applied" may come in either order

2. What was verified
- `dotnet.exe build CabinetOS.sln -warnaserror`: 0 warnings, 0 errors.
- Fast run (`dotnet.exe test --solution CabinetOS.sln --no-build`): 974 total, 941 passed, 33 skipped (the end-to-end tests).
- Full run with `CABINETOS_UI_E2E=1` and the release core (protocol 15): 974 total, 974 passed, 0 skipped, 1 min 34 s.
- The first full run failed once. The core's `theme_changed` arrived before its reply to `set_value`, so the order check was too strict. 5eb14f7 fixes the test. The test then passed 2 times on its own and in the full run.
- ThemePickerTests: 15 tests (6 old, 9 new). They passed on 5 runs in a row.
- Evidence lines from the end-to-end test (target cabinetos_ui::theme):
  - Esc run: theme applied default / theme previewed nord / theme restored default. There was no "theme chosen", and `cabinetos.json` names no theme.
  - Apply run: theme applied default / theme previewed nord / theme chosen nord / theme applied nord. There was no "theme restored", and `cabinetos.json` has ui.theme = nord.
- build/check-scripts.ps1: every script parses in PowerShell 7.6.6 and in 5.1. I ran the new live-check evidence lines against a fake log in both shells: True, True.

3. Decisions the plan did not cover (what — because — undo)
- LoadAsync schedules a preview like any other highlight change — because then one rule holds: the window shows the highlighted theme. In the normal case the highlight lands on the checked row, so no request goes out. With no checked row, row 0 is previewed — undo: remove `SchedulePreview()` from LoadAsync.
- Choosing the checked row with Enter or a click paints the theme in effect back inside ApplyAsync — because the core sends no `theme_changed` when ui.theme does not change (core themes.rs publishes only when the theme differs). Without this, Enter on the checked row within 80 ms of leaving a preview kept that preview on screen for good — undo: remove the IsCurrent block in ApplyAsync (b84515a).
- MarkCurrent also sets IsPreviewShown to false and drops any reply still on its way — because after `theme_changed` the window shows the theme in effect (the plan says "any preview state is dropped") — undo: remove those two lines.
- Apply, Preview and EndPreview raise `Applied` after they set Theme, Previewing and Current, not inside Paint — because the Applied handlers read Theme and Current (the marketplace's current theme, the terminal colours) — undo: move the invoke into Paint and set the fields before calling it.
- A preview that cannot be mapped reuses Paint's warning "a theme was not applied", and the screen stays as it was — undo: give Preview its own message.
- EndPreview does nothing while Theme is null, and Previewing keeps naming what is on screen — undo: clear Previewing there.
- A change of Windows' accent or mode repaints a preview through Preview(), so "theme previewed" is logged again (Apply already logs "theme applied" again in that case) — undo: add a paint path that does not log.
- HideThemePicker closes the view first and then ends the previews — so the repaint for the restore does not rebuild the picker's rows — undo: swap the two lines.
- CloseThemePicker got a `restore` parameter (default true). The apply path calls CloseThemePicker(restore: false) — because the apply path also needs CloseThemePicker's focus handling — undo: call FocusActivePane() plus HideThemePicker(false) directly.
- ThemePicker.Open() captures the metrics only when the picker is not already open — because a second Ctrl+K Ctrl+T during a Commander Compact preview would otherwise lock the rows at the preset's sizes — undo: capture on every Open.
- "theme previewed" and "theme restored" carry two fields, theme and mode. A failed preview logs "theme preview failed" at Debug with theme, code and error — undo: change the fields.
- `ThemeTests.Shipped` became `internal` so the picker tests can build a ColorTheme with the existing helper — undo: make it private again and copy it.
- The end-to-end test lives in its own file, ThemePickerEndToEndTests.cs, with its own copy of the small helpers, as each end-to-end file in the project does. It writes the theme log lines to the test output as evidence — undo: remove the WriteLine.
- The end-to-end test accepts "theme chosen" and "theme applied" in either order, as long as both come after the preview. The plan said "chosen, then applied" — because the core's event can arrive before its reply — undo: none safe, since the order is not guaranteed.
- docs/ui.md: the table of snapshot steps now lists `pick:<index>` — undo: remove the entry.
- The live check takes "the theme the run had before" from the last "theme applied" or "theme restored" line before its step — undo: hardcode default.

4. What I could not do, and why
- I did not run the live check with real keys (ui/livecheck), as the handout said. The planning session runs it.
- I did not fast-forward the local `main` branch. It is checked out in the main checkout, and this agent may run git only in its own worktree. origin/main is at 5eb14f7; pull it in the main checkout.

Out of scope, noticed: the `theme:<id>` snapshot step (SwitchThemeForSnapshotAsync) waits until `_themes.Current?.Id` equals the id. During a preview, Current is the previewed theme. So the step would return early if it ever ran while that same theme was previewed. No current script does this.

Files changed: ui/CabinetOS.Core/Themes/ThemePickerModel.cs, ui/CabinetOS/Services/ThemeApplier.cs, ui/CabinetOS/MainWindow.Themes.cs, ui/CabinetOS/MainWindow.xaml.cs, ui/CabinetOS/MainWindow.Market.cs, ui/CabinetOS/Views/ThemePicker.xaml.cs, ui/CabinetOS/Services/DevSnapshots.cs, ui/CabinetOS.Tests/ThemePickerTests.cs, ui/CabinetOS.Tests/ThemeTests.cs, ui/CabinetOS.Tests/ThemePickerEndToEndTests.cs (new), ui/livecheck/livecheck.ps1, docs/ui.md, CHANGELOG.md. No Rust change and no protocol change.
