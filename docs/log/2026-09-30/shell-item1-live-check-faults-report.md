## Item 1, the two open faults of the live check

Agent: a019b1b2d5c91c57a

Report: Item 1, the two open faults of the live check

Both faults are fixed, tested and pushed to main. Real-key live-check run 5 passed every check. As you instructed, I stopped here and did not start item 2.

## Fault 2: "7 selected, 29 B" for six marked files after Num *
- **Cause.** In the Windows selection style, the row the keyboard is on counts as selected. `SelectionModel.Invert` kept that row (the folder `docs`, where Home had put the cursor) together with the files it marked. So the status bar counted it, and F5 would have copied it too. `UpdateStatus` itself was correct.
- **Fix.** The cursor row selected alone is the cursor, not a mark.
  - New `SelectionModel.HasMarks`: the rule `MarkMemory.HasMarks` already had. `MarkMemory.HasMarks` now calls it.
  - New private `SelectionModel.DropCursorSelection()`, called by `Invert` and by `SetMarks(mark: true)`. So Num *, Num + and Alt+Num + no longer count the cursor row.
- **Tests.** Four new ones in `ui/CabinetOS.Tests/SelectionModelTests.cs`:
  - `Invert_in_the_windows_style_leaves_the_cursor_folder_unmarked_and_counts_only_the_files`: the cursor sits on an unmarked row.
  - `Invert_in_the_windows_style_marks_the_cursor_file_too_when_nothing_was_marked`
  - `Invert_keeps_real_marks_and_the_cursor_row_they_include`
  - `Marking_by_pattern_in_the_windows_style_does_not_count_the_cursor_row`

## Fault 1: keyboard lost after Ctrl+P with the Markdown Preview open
- **Diagnosis.** I added a report of which window gets the key presses (`WindowsPlatform.KeyboardFocus`, which asks Windows via `GetGUIThreadInfo`). A copy of the live check that keeps the preview open reproduced the fault with real keys:
  - XAML's focus was on the terminal's WebView2.
  - Windows sent the keys to WinUI's own input window (class `InputSiteWindowClass`), even 300 ms later.
  - The window ignores keys that XAML routes to a page, because the page normally passes them back itself. So every key was lost.
- **Cause.** I read WinUI's `WebView2.cpp` on GitHub main; your SDK version may differ.
  - WinUI moves the keys into a page only when XAML's focus arrives while the page's controller is visible.
  - A dock shown a moment ago becomes visible to that controller only at the next frame (`HandleRendered`).
  - When the focus arrives first, WinUI keeps the move pending and never makes it.
  - The failure is intermittent. It needs the hidden terminal shown again, plus timing; the preview being open made it more likely.
- **Fix, part 1: check and hand over again.**
  - New `ui/CabinetOS.Core/Presentation/PageKeyboard.cs` (`PageKeyboard`, `PageKeyboardStep`). The keys are in the page when the input window's class starts with `Chrome_`; they are not when it is WinUI's own input window.
  - New `ui/CabinetOS/MainWindow.Keyboard.cs` with `GiveKeysToPage` and `CheckPageKeysSoon`. 150 ms after each hand-over it checks which window has the keys.
  - If the page does not have them, `HandOverAgain` moves XAML's focus away for a moment and back, up to three times. It goes to a shown pane, else to the page header's close button (`ToolDock.HeaderStop`, `EditorPane.HeaderStop`).
- **Fix, part 2: keys the page never saw.** `HandleWindowKey` now calls `HandleKeyForPage` when XAML's focus is on a page:
  - If the page has the keys, the key is ignored as before.
  - If not, the page's ways out run in the window (Ctrl+Shift+P, Ctrl+`, `terminalFocus` bindings), found with the new `TerminalController.PassKeyCommand` or `_toolKeys`.
  - Any other key hands the keyboard to the page again.
- **Callers changed.**
  - `FocusTerminal` now calls `GiveKeysToPage`.
  - Tool pages go through the new `FocusEditorPage(pane)`, in `OpenInToolAsync` and `FocusPaneOrEditor`.
  - `ToolDock.FocusTerminalPage` is removed. `EditorPane.FocusPage` is replaced by `EditorPane.PageView`.
- **Logs** (target `cabinetos_ui::shell`): "a page has the keyboard" (with the page and `hand_overs`), "a page did not get the keyboard; handing it over again", "a page did not get the keyboard" (a warning, after three tries), "a key the page did not get", and "keyboard owner". The last one is written 300 ms after the terminal hides and at the snapshot aid's `focus:` step.
- **Tests.** New `ui/CabinetOS.Tests/PageKeyboardTests.cs`, 13 cases. The window glue has no unit test; run 5 exercised it (below).
- **Live check.**
  - The palette fallback is gone at both places: the end of the phase-5c section and after Ctrl+P in section 11a.
  - The preview now stays open in the right pane through 11a's Ctrl+P, and is closed through the palette right after that check.
  - New check lines: the terminal's page got the keyboard (with the number of hand-overs), Ctrl+` reached the window, how many keys the window had to take for the page, and where the keys go after Ctrl+` hides the terminal.
  - `run-livecheck.ps1` takes a new `-Io <folder>` switch. From a worktree, the default output folder would land inside the creator's checkout.

## Checks
- `dotnet build CabinetOS.sln -c Debug -warnaserror` and `-c Release -warnaserror`: Build succeeded, 0 Warning(s), 0 Error(s).
- `CABINETOS_UI_E2E=1 dotnet test --solution CabinetOS.sln --no-build`, with a fresh release core from main at 46c644c: "Test run summary: Passed! total: 650 failed: 0 succeeded: 650 skipped: 0". That is 633 + 17.
- On the way, one end-to-end test failed after rebasing: it expected protocol 12, and the core agent's 46c644c raised it to 13. I changed the expected version to 13 in commit 83138c3.
- **Live check run 5**, real keys, screen unlocked, release builds on main at 054a1fa. Output: `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\live-check\run-2026-09-30-item1-b.txt`. DONE.md, verbatim:
  - "- scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: no; 303 frames in 5 s, 88 over 20 ms (29.0 %), 2 over 33 ms, worst 47.9 ms; CPU during the hold: machine 12.4 %, this window 1.6 %, busiest others: OneDrive 0.8 %, AutoHotkey64 0.8 %, asus_framework 0.4 %"
  - "- Last stop line: STRICT: the scroll goal was not met"
  - "- Checks that answered False: none"
- Run 5's item-1 lines, verbatim:
  - "after Num *: 6 selected, 29 B (the 6 files expected, docs and photos not)"
  - "the terminal's page has the keyboard after Ctrl+P (hand-overs: 2), the preview open in the other pane: True"
  - "Ctrl+Backquote reached the window after Ctrl+P, the preview open in the other pane: True"
  - "keys the window had to take for the page (0 expected): 0"
  - "the preview closed: True"
  - "Ctrl+Backquote hid the terminal: True"
  - "then the keys go to FilePane, window, the pane: True"
  - "app exited: True, code 0"
- The fault happened in this run, and the repair fixed it. The log shows "a page did not get the keyboard; handing it over again {"page":"terminal","attempt":1,"window_class":"InputSiteWindowClass"}", then "a page has the keyboard {"page":"terminal","hand_overs":2}".
- An earlier full run (run-2026-09-30-item1-a.txt) did not hit the fault (hand-overs: 1); it too had no False check.
- Alt+F1 reached the window in run 5.
- I closed Notepad after each run. There were no foreground stops.

## Commits, all pushed to main
- b116346: "ui: the cursor row the Windows style selects is no mark for Num * and Num +"
- c8215f0: "ui: the terminal's page takes the keyboard when it shows, whatever else is open"
- 0992fd2: "docs: run 5 of the live check, with both shell findings of run 4 fixed"
- 83138c3: "ui tests: the real core speaks protocol 13 now"

One irregular step: my second `git pull --rebase` stopped on a README conflict, and the push chained after it pushed b116346 alone while the rebase was still open. It was a normal fast-forward of a complete commit, but you should know. I then resolved the conflict (core 618, ui 650 tests), finished the rebase, retested and pushed the rest.

## Docs changed
- `docs/ui.md`:
  - the live-check description, and `-Io`;
  - the Num * row of Total Commander's keys, and a new paragraph under "Marking";
  - a new bullet "Handing the keyboard to the page" under "The terminal";
  - the new log lines.
- `docs/log/2026-09-28/live-check.md`: a new "Run 5" section, and the leftovers updated.
- `README.md`: 650 UI tests (and the core's 618 from main).

## Decisions (what — because — undo)
1. Num * in the Windows style drops the cursor row's own selection before it marks — because that row is the cursor, not a mark (the same rule Restore Selection already used), and Total Commander marks all files when nothing is marked — undo: remove the `DropCursorSelection()` call in `SelectionModel.Invert`.
2. Num + and Alt+Num + do the same — because after Home, Num + `*.txt` counted the cursor folder too (the same fault) — undo: remove the `if (mark) DropCursorSelection();` in `SetMarks`.
3. Fixed the keyboard by checking after the hand-over and handing over again — because it works whatever WinUI's internal order is, and it also catches other causes. I rejected deferring the first focus by one frame, and rejected toggling the WebView2's own visibility (it relies on WinUI internals) — undo: make `FocusTerminal` and `FocusEditorPage` call `view.Focus(Programmatic)` again.
4. A key that reaches the window while XAML's focus is on a page without the keys runs that page's ways out — because otherwise Ctrl+` and Ctrl+Shift+P are lost in the 150 ms before the check, or after it gives up — undo: restore the plain `return` in `HandleWindowKey`.
5. The page is recognised by the input window's class, not its process — because WebView2's input window belongs to the window's own process (the probe showed it) — undo: none needed.
6. The live check keeps the preview open through 11a's Ctrl+P and closes it before Ctrl+\, Alt+F1 and Ctrl+U — because the fault needs a page in the other pane, and swapping panes with an open editor was never checked — undo: move the "Close Editor" palette step back to the end of the 5c section.
7. Both palette fallbacks removed, and the check lines kept — the handout asked for this — undo: revert the livecheck.ps1 part of c8215f0.
8. `run-livecheck.ps1` got `-Io` — because from a worktree its default folder lands in `.claude\worktrees\_io` inside the creator's checkout — undo: drop the parameter.
9. "keyboard owner" is logged at Info level at the terminal's hide and at the snapshot `focus:` step — because the live check reads it — undo: remove `LogKeyboardSoon` in `HideDock`.
10. Updated the protocol version the end-to-end test expects, 12 → 13 — because the core agent's change failed it — undo: revert 83138c3.

## Ready plan text (for docs/PLAN.md, near "Real-key runs of the whole live check")
**The two shell findings of run 4, fixed on 2026-09-30.** After Num \*, the status bar counted the folder under the cursor with the marks ("7 selected, 29 B" for six files). In the Windows style that row's own selection is the cursor, not a mark; Num \*, Num + and Alt+Num + now drop it before they mark (`SelectionModel.HasMarks`). The keyboard was lost after Ctrl+P while the Markdown Preview was open. WinUI moves the keys into a page's browser only when XAML's focus arrives while the page's controller is visible, and a dock shown a moment before is visible to it only from the next frame. The window now checks 150 ms after each hand-over to a page (the terminal, a tool's editor tab) which window gets the keys, and hands them over again, up to three times. When a key still reaches the window instead, the window runs the page's ways out itself (`PageKeyboard`). Live check run 5 with real keys: every check True. The fault occurred in that run, and the second hand-over reached the page. The scroll goal is still not met: 29.0 % of the gaps over 20 ms, worst 47.9 ms. 650 UI tests.

## What is left, and notes for the agent taking items 2 to 4
- **Item 2 must use the new hand-over.** When a tool tab becomes active, its editor page goes from hidden to shown, which is exactly this fault's condition. Give it the keyboard through `FocusEditorPage(pane)` / `GiveKeysToPage`, never through `WebView2.Focus` directly. The same applies to sidebar web views in item 4.
- **Possible overlap with the Phase 15 agent.** My `MainWindow.Keyboard.cs` logs keyboard ownership at Info level. Their `MainWindow.Diagnostics.cs` plans focus logging at heavy level. There was no conflict at push time; they may want to reuse `WindowsPlatform.KeyboardFocus` and `KeyboardOwner()`.
- **Throwaway probe scripts.** They are in my scratchpad, not in the repo: `probe.ps1`, `probe-full.ps1`, `probe-fixed.ps1`.
- **Leftovers.** About eight more tiny test files are in the Recycle Bin (the Delete and F8 checks of four runs), and the folder `%TEMP%\cabinetos-ui-test\probe` remains.
- **Out of scope.** The WinUI behaviour (a pending focus move is never made when the controller becomes visible) looks like a WinUI bug worth reporting upstream. Only the creator can file such a report, since nothing outward-facing is allowed tonight.
- **Next for me.** Item 5, the scroll-gap investigation, waits for your message.
