# The pane tab strip as the design draws it: the coder's two hand-backs

The creator's request of 2026-09-30, evening: "ugly tabs, especially the
active tab is a broken design; look into our Claude Design project and fix
the tabs." The planning session fetched the design page "Pane Tabs
Options" from the creator's Claude Design session (read-only, into
`_io/design/claude-design/`, recorded in its FETCHED.md) and took option
1a, the chosen shell, as the truth: a flat tab the strip's full height,
padding 10 px, at most 160 px wide, a right hairline; the front tab of
the active pane with a white 8 % fill, a straight 2 px accent bar as its
top edge, bold white text and a plain × at 50 %; the front tab of the
other pane with a 6 % fill and a 30 % bar; the other tabs at 65 % with a
hover fill; no icons; a 24 px "+"; square corners. A screenshot of the
window before the change showed WinUI's TabView drawing its own
document-tab shape under our accent bar, which was the fault. Handed to a
coder on Sonnet in its own worktree; the planning session answered its
one blocker (the default of `tabRadius` lives in the core's metrics
table, so the change is a Rust one with the schemas regenerated) and
asked for the "+" to stay visible when the tabs overflow. The two
hand-backs follow, as reported. The planning session's checks on the
main checkout: main pulled at 8dbee0f; the five core checks green (795
tests); the release core rebuilt; the window's build with warnings as
errors clean; 978 of 997 tests in the fast run (19 skipped, the
end-to-end tests, which the coder ran: 975 of 975 for the strip, then
TabsEndToEndTests again after the metric change). The live check with
real keys runs when the evening's last fix has landed.

---

## The first hand-back

Report: pane tab strip redesign (the strip is done; the square default corner is NOT done, because the plan's "no Rust change" is wrong)

BLOCKER, needs your decision. The plan says change the `tabRadius` default from 8 to 0 and touch no Rust. But the default lives in the Rust table `core/crates/cabinetos-protocol/src/theme.rs:394` (`tab_radius "tabRadius" Px [0.0, 16.0] 8.0 => 3.0`). The JSON schemas are generated from that table, and Rust snapshot tests (`theme_schema_matches_sdk`, `event_schema_matches_sdk`, `response_schema_matches_sdk` in `cabinetos-protocol/src/schema.rs`) fail if `sdk/themes/theme.schema.json`, `sdk/protocol/event.schema.json` or `sdk/protocol/response.schema.json` differ from what the table generates. So editing only Metrics.cs plus theme.schema.json would break `cargo test`. Two readings, I picked neither, so the default is still 8 (the default theme has 8 px rounded top corners; Commander Compact 3 px):
 A) Change 8.0 to 0.0 in theme.rs, then `CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol` (rewrites the 3 schema files, only the "left out, 8." of tabRadius), plus Metrics.cs:240 `Px("tabRadius", 0, 16, 8)` to 0, docs/themes.md:224, and the "Corners follow tabRadius" bullet in docs/ui.md. I could not run cargo (you said not to), so A is unverified.
 B) Leave 8 and set `tabRadius: 0` some other way.
Message me (same agent) to do A. I checked the square look with a temporary local default of 0 (reverted, never committed): flat 8 % block, straight 2 px accent bar, no icon. PNGs below.

Commits, pushed to origin/main with `git push origin HEAD:main` (fast-forward after a clean rebase onto 2bd7425; `main` is checked out in the main checkout, so it still needs `git pull --ff-only` there):
- 319a497 window: the pane tab strip is the window's own (ui/CabinetOS/Views/PaneTabs.xaml and .xaml.cs; TabView, AccentLine, PositionAccent removed)
- df37a21 tests: the strip's markup has no TabView, nothing can take the keyboard (ui/CabinetOS.Tests/TabTests.cs)
- 56fbb69 docs, changelog (docs/ui.md "Tabs in the shell", "Tabs", scroll-cost note; CHANGELOG.md one line under Changed)
Not edited: docs/themes.md (waits on the blocker), MainWindow*.cs, Rust.

What was built: Frame border, then a horizontal ScrollViewer (scrollbar hidden) holding a StackPanel of code-built tabs plus the "+" (XAML button AddButton, UIA name "New tab", tooltip with the key of tab.new), and the collapsed Open with button outside the scroller. A tab is an outer Border (1 px right hairline) around a face Border (fill, 2 px top border as the bar, top corners from tabRadius) around: optional 12 px lock/tool glyph, title (ellipsis, MaxWidth computed so a tab is at most 160 px), and a chromeless Button "×" (16x16, style `TabGlyphButtonStyle` in PaneTabs.xaml). Public members, commands, arguments, trigger "button", UIA names unchanged. The front tab scrolls into view when it or the width changes.

Verified:
- `dotnet build CabinetOS.sln -warnaserror`: clean, before and after the rebase.
- Fast tests: 975 total, 942 passed, 33 skipped (E2E opt-in), 0 failed (before the rebase 965/933/32).
- E2E (CABINETOS_UI_E2E=1, release core): 975/975 passed, 0 skipped, run before and after the rebase, including TabsEndToEndTests. No frame-timing failures.
- The new test fails as it should when AddButton gets IsTabStop="True" (checked, then restored).
- Real mouse on the Debug window: hover on an inactive tab (6 % fill, white text), on the lock tab, on the × (turns white), on the "+" (white), on the front tab of the other pane (× appears); left click selects a tab; middle click closes one; the × click closes the front tab; "+" adds a tab; right click shows Unlock/Move/Close with keys. Overflow in a 700 px window: the front tab is scrolled into view on select and on new tab.
- PNGs (folder C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\tabs-look\): run-final\shots\tabs.png and tabs-compact.png (as pushed, default 8 px radius); run-a2\shots\tabs.png, tabs-compact.png, zoom-a2-tabs.png (temporary default 0: square); montage-h3.png (hover, click and middle-click rows); montage-cp.png (× and "+" clicks); montage-ov.png (overflow); run-m1\shots\menu.png (context menu).

Decisions the plan did not cover (what, because, undo):
1. Front tab text in the other pane uses CbRowTextBrush (90 %), because it is nearest to 85 % (CbDialogTextBrush 80 % ties). Undo: the key in Paint().
2. × and "+" at rest use CbTextTertiaryBrush (55 %, nearest to 50 %), hover and press CbTextPrimaryBrush. × is 16 px wide with the tab's font size.
3. Every tab has a transparent 2 px top border, because otherwise inactive text sits 1 px higher than the front tab's (the page has that shift). Undo: top border 0 on inactive tabs.
4. The front tab keeps its 1 px right border, transparent, because tab widths should not change with the front. The page shows no hairline there.
5. The × of the other pane's front tab is Collapsed until hover, because the page's tab hugs its text. The tab grows about 18 px on hover and its right neighbours shift. Undo: Opacity instead of Visibility (keeps the space).
6. A left press on a non-front tab runs tab.select at once (press, not release). No separate pressed fill for tabs; the "+" and × turn white when pressed.
7. `TabGlyphButtonStyle` is in PaneTabs.xaml, not App.xaml, to avoid conflicts with the other coders. The × got the UIA name "Close tab" (new).
8. The test is in the existing TabTests.cs, not a new file.

Not done or unverified:
- The square default (blocker above). docs/ui.md says plainly that the default is still 8 px; change that bullet when the default changes.
- No mouse-wheel handler. Whether the vertical wheel scrolls the row is untested.
- Keyboard focus was checked only by markup and flags, not with real keys.
- One mouse test run had a script bug (PowerShell alias Move = Move-Item) and sent one left and one middle click at the real cursor's unknown position. I saw no effect in the tested window but cannot rule out a click elsewhere. The script then got a guard that clicks only when the cursor is over the test window.

Worth doing, out of scope:
- The "+" is inside the scroller, so it scrolls out of view when tabs overflow (Ctrl+T still works). Pinning it outside the scroller is an option.
- A click on the front tab of the inactive pane does not activate that pane (same as before).
- After a rebuild of the tabs under the pointer, hover shows only after the next pointer move.

## The second hand-back, after the planning session chose reading A and asked for the pinned "+"

Report: tab strip follow-up, reading A done plus the pinned "+"

Both parts are done and pushed to origin/main with `git push origin HEAD:main` (fast-forward cf11d54..8dbee0f, after a clean rebase; no conflicts). Earlier pushed commits from this task: 319a497, df37a21, 56fbb69. `main` is checked out in the main checkout, so it still needs `git pull --ff-only` there.

Commits (new ones):
- 4cda2db theme metrics: a pane tab's top corners are square unless a theme rounds them. Changed: core/crates/cabinetos-protocol/src/theme.rs (tab_radius default 8.0 to 0.0, Commander Compact stays 3.0); the three schema files regenerated with CABINETOS_UPDATE_SCHEMA=1 (only tabRadius "left out, 8." became "left out, 0." in sdk/themes/theme.schema.json, sdk/protocol/event.schema.json, sdk/protocol/response.schema.json); ui/CabinetOS.Core/Themes/Metrics.cs:240 to 0; docs/themes.md:224 (default 0, "The default look has square tabs."); docs/ui.md bullet "Corners are square" (replaces the "still 8 px" note).
- 8dbee0f window: the tab strip's "+" stays at the strip's end when the tabs overflow. Changed: ui/CabinetOS/Views/PaneTabs.xaml (third grid column, slot Border PinnedAdd, Open with button now in column 2), PaneTabs.xaml.cs, docs/ui.md "+" bullet.

How the pin works: while the tabs plus the 24 px "+" fit in the strip, the button sits right after the last tab, inside the scroller, as the design draws it. When they do not fit, the same button moves into PinnedAdd at the strip's end, outside the scroller. The test "tabs + 24 px > room" uses only the tabs' widths and the room, not where the "+" is, so the two places cannot flip each other. The move is done after the layout pass (DispatcherQueue), not inside it. The old QueueScrollToFront is now QueueSettle(scroll): it places the "+" and, when asked (tab change, strip width change, loaded, metrics), scrolls the front tab into view. A size change of the tab row alone (for example the x appearing on hover) only re-places the "+", it does not scroll.

Verified:
- Core, from core/ in this worktree, all five commands, exit 0: cargo build --workspace; cargo test --workspace (795 passed, 0 failed, 6 ignored, 83 result lines); cargo clippy --workspace --all-targets -- -D warnings (clean); cargo fmt --all -- --check; cargo deny check (advisories, bans, licenses, sources ok; it prints duplicate-version warnings only). Also `cargo test -p cabinetos-protocol` with the update flag first: 62 passed, the four schema snapshot tests ok.
- UI, after the rebase: `dotnet.exe build CabinetOS.sln -warnaserror` clean; fast tests 997 total, 978 passed, 19 skipped, 0 failed (MetricsTests, which ties Metrics.cs to the schema's "left out, 0", included).
- TabsEndToEndTests with CABINETOS_UI_E2E=1 and CABINETOS_CORE_EXE=the release core built in this worktree (`cargo build --release -p cabinetos-core`, 2m06s): 1 of 1 passed. I did not repeat the whole E2E suite, as you said.
- Screenshots (folder C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\tabs-look\), all from the final build with the worktree release core: zoom-final2-tabs.png (default theme, now square: flat 8 % front block, straight 2 px accent bar, "+" right after the last tab); run-final2\shots\tabs.png and tabs-compact.png (both themes; Compact keeps its 3 px corners); montage-ov3.png is the overflow case in a 700 px window, three rows (front tab last, first tab selected, fourth selected): the "+" stays visible at the strip's end in all three, tabs cut before it. Its source PNGs are run-o3\shots\ov-last.png, ov-first.png, ov-mid.png.

Decisions not covered by the message (what, because, undo):
1. The "+" is one button moved between two parents, not two buttons, because one button keeps one UI Automation name, tooltip and hover state. Undo: two buttons toggled by Visibility.
2. The slot is a plain Border (PinnedAdd) in a third grid column, so the markup test (every control has IsTabStop and AllowFocusOnInteraction off) still passes unchanged.
3. A 0.5 px tolerance in the fit test, because widths are fractional at 1.5x scale.
4. The docs/ui.md "+" bullet went into the pin commit and the corners bullet into the metrics commit, so each commit's docs are true by themselves.

Could not do / notes:
- Nothing from the message was left undone. The pre-existing out-of-scope points stay as they were: a click on the front tab of the inactive pane does not activate that pane; hover after a tab rebuild shows only after the next pointer move; the mouse wheel over the strip is still untested.
- Other windows were running on the machine from another agent's worktree (agent-a79802767d762996e: a CabinetOS.exe and its release core). I left them alone; mine are all stopped.
- In this worktree git reported LF/CRLF warnings for .cs files (index is LF, .gitattributes says eol=crlf); I kept the working copies CRLF, and git's diffs showed only the intended lines.
