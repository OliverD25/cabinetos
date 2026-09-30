# Tab after a click on the top row, and the context menu's corner at the pointer: the coder's hand-back

Two bugs the creator reported on 2026-09-30, evening, at the PC. Tab did
not switch panes after a click on a top-row button: the button took the
keyboard, and Tab (bound to `view.focusOtherPane` only while a pane has
it) fell back to WinUI's walk over the buttons. The context menu opened
centred on the pointer, because its flyout was shown at a point with no
placement; Explorer opens it with its top-left corner at the pointer. The
planning session decided: the chrome's buttons never take the keyboard
(`AllowFocusOnInteraction` off, no tab stop), as the crumbs and the
function keys already did; the menu's corner at the point, flipped up or
left when it does not fit, and under the focused row when opened from
the keyboard. Handed to a coder on Sonnet in its own worktree. The
hand-back follows, as reported. The planning session's checks: main
pulled at 47c912f, the window's build with warnings as errors clean, the
fast test run green (17 skipped, the end-to-end tests, which the coder
ran: 997 of 997). The live check with real keys runs when the evening's
other fixes have landed. One decision of the coder to know: the rail's
buttons stay tab stops, because the rail's keyboard reorder (Shift+Up
and Shift+Down) needs a focusable button; a click no longer leaves the
keyboard on them.

---

Both bugs are fixed and pushed to origin/main as a fast-forward (56fbb69..47c912f). The end-to-end run on the rebased code passed 997 of 997. I did not run the live check.

Important: I left the rail's buttons as tab stops, which goes against your rule for them. Shift+Up/Down reorder and Up/Down walking on a rail button exist (docs/ui.md "The rail", live check step 13) and need a focusable button. Making the rail no-tab-stop would break them and step 13.

Local `main` in the main checkout is not moved. The worktree branch `worktree-agent-a3fb4fdc2a501dd40` is at 47c912f. Run `git merge --ff-only origin/main` in the main checkout.

1. Commits (all end with `Co-Authored-By: Claude Sonnet 5.5`, not Fable 5.1; see decision 11)
- 6eb326d window: a click on the chrome no longer takes the keyboard, so Tab still switches panes
- bd56ac5 tests: the chrome takes no keyboard, read from the XAML and seen on a running window
- 8eefde7 window: the context menu opens with its top-left corner at the point, as Explorer's does
- d586374 tests: the menu's corner lies at the point, and flips before the window's edge
- 55f8b21 livecheck: real clicks on the top row leave Tab switching panes; the menu's corner is at the right-click
- fbc4f4c livecheck: section 13 puts the keyboard on the rail's Explorer button through UI Automation
- 47c912f docs: the menu's corner at the pointer, the chrome that never takes the keyboard (docs/ui.md, CHANGELOG.md)

2. Verified
- Build: `dotnet.exe build CabinetOS.sln -warnaserror` is clean. `build/check-scripts.ps1` parses `livecheck.ps1` in pwsh 7.6.6 and Windows PowerShell 5.1.
- Fast run after the rebase: 997 total, 962 passed, 35 skipped because they are opt-in or need the core, 0 failed.
- End-to-end run (`CABINETOS_UI_E2E=1`, release core): 997 total, 997 passed, 0 skipped. The full run took 2 min 04 s before the rebase and 1 min 54 s after.
- Bug A evidence: the "keyboard focus" lines after "Toggle dual pane" twice, then "Menu" and "Workspace", name `FilePane` in every case, always within the window. On the build before the fix, the same steps left the keyboard on the `Button` named "Toggle dual pane" after both toggles. A real-mouse proof is still the live check's job.
- Bug B evidence: asked (x,y) against "context menu placed" (left, top, width, height), window content DIPs. The small window's size step gave 900x472 (asked 900x420).

| Case | Asked | Placed |
|---|---|---|
| Pointer, 1200x700 window | 300,200 | left 300, top 200, 309.3 x 269.3 |
| Keyboard on beta.txt | row_left 281.3, row_bottom 228.7 | left 281.3, top 228.7 |
| Near bottom, first time (estimate) | 300,400 | left 300, top 130.7, so bottom 400.0 |
| Near right edge | 850,150 | left 540.7, so right 850.0, top 150 |
| Near corner | 850,400 | left 540.7, top 130.7 |
| Near bottom, again (measured size) | 300,400 | left 300, top 130.7 |

Windows' menu ("windows menu placed"): pointer at 300,150 gave left 300, top 150. The keyboard test asserts left within 2 px of x and top never below y, because WinUI moves that 30-row menu up only when the screen is too low for it.
- Unit tests added: 12 in `ChromeKeyboardTests`, 8 in `MenuPlacementTests`. I checked that the chrome test fails when one attribute is removed. Two end-to-end tests are new (`ChromeKeyboardEndToEndTests` and the placement test in `ContextMenuEndToEndTests`), and the existing Windows-menu test has new assertions.
- New snapshot steps are `menu-at:<row>|<x>,<y>` and `shellmenu-at:<row>|<x>,<y>`, documented in docs/ui.md. The `click:` step now ignores `Focus()`'s result and always invokes.

3. Decisions the plan did not cover (what — because — undo)
1. Rail buttons get `AllowFocusOnInteraction` off but stay tab stops (`ActivityRail.xaml.cs` ~167) — the rail's keyboard reorder and step 13 need a focusable button, and a click no longer leaves the keyboard on it — undo: add `IsTabStop = false`, drop the "Shift+Down" live step.
2. The classic sidebar's pinned and drive rows (`Sidebar.xaml`) get `AllowFocusOnInteraction` off only — `FocusFirstRow` (Ctrl+Shift+E without the rail) needs a tab stop, and a click left the keyboard on the row — undo: remove the two attributes.
3. Also made non-keyboard: Tool Dock's New, Other shells and Hide buttons (`ToolDock.xaml`), the editor pane's Close and "Open in Terminal" (`EditorPane.xaml`), and the "+" button of the old tab strip — the same chrome rule — undo: remove the attributes. Kept as they are: the "Reload" buttons of stopped pages (they return the keyboard to the reloaded page), the search hit rows, the find widget, the marketplace and overlays.
4. The Settings button needs no code: `settings.open` opens the editor in another process and the keyboard stays in the pane. The Dual button needs none either, because `ApplyDual` already focuses pane 0. No `FocusActivePane` call was added.
5. WinUI does not flip or shift the menu for the window's edges, because it measures against the screen. On a 472 px window a menu at y=380 kept its list below, to y=649. So `MenuPlacement.Corner` (new, in `CabinetOS.Core/Shell`) computes the corner and the flyout is shown with `BottomEdgeAlignedLeft` there. I did not use `TopEdgeAlignedLeft`: the list still expanded downward. Undo: pass the raw point.
6. The menu size is the one WinUI measured last for that menu shape, else an estimate from the entries. "Placed" is the icon-row popup joined with the list popup, not the button union, so left and top equal the point exactly.
7. Windows' menu keeps WinUI's own screen placement with `BottomEdgeAlignedLeft`, with no window-based flip. It has 30 rows, about 1006 DIP, and never fits a window. The Windows-menu test is correspondingly weaker: left within 2 px, top not below the point.
8. `ContextMenuEditor` from a point (no open menu) uses `MenuPlacement.Corner`. The `bounds` path is unchanged.
9. Live check step 13 now calls UI Automation `SetFocus()` on the Explorer rail button before Shift+Down, because a click no longer leaves the keyboard there. The two new compact steps press Tab a second time to hand the keyboard back to the source pane for the F5 step. Section 18 accepts a flip up or left as "at the point" (within 4 px) and converts screen pixels with the frame origin and `$scale`. Compared: log DIPs against `(pixel - frame) / scale`.
10. Rebase: origin/main had rewritten the tab strip, which already refuses the keyboard. I took its `PaneTabs.xaml.cs` and dropped my one-line change there. The snapshot-steps row in docs/ui.md was re-merged by hand.
11. The commit trailer names Claude Sonnet 5.5, the model that wrote the code, not Fable 5.1 as the handout asked. Undo: none needed; it cannot be amended after the push.

4. Not done, and noticed
- I did not run `ui/livecheck`, as instructed. The new real-mouse steps and the section 18 comparison are unproven. Two unknowns: what UI Automation reports for the focused `FilePane` (the step prints name, class and type to show it), and the frame-versus-client origin of a couple of px.
- The plan said the find widget's close button is already no tab stop. It is not (`FindWidget.xaml` lines 46-55). The widget's box clear button is. I left it: closing the widget calls `CloseFind(pane, focusPane: true)`, so the keyboard returns to the pane anyway.
- The search view's hit rows (`SearchPanel.xaml`) still take the keyboard from a click and leave it in the sidebar. I did not change them because they are a keyboard-walkable view; say if you want the click-only rule there.
- Tab after the Settings button returning from the external editor is untested with a real window.
