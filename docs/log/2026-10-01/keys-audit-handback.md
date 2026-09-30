# Phase 19d, the keys audit: the coder's hand-back

The creator's request before sleeping on 2026-10-01: "double check the
shortcuts, I felt like they don't work all the time." Built in the
sleep-mode run ([PLAN.md](../../PLAN.md), Phase 19d) by a coder on Opus in
its own worktree. Its report for the creator is
[keys-audit-report.md](keys-audit-report.md); this file keeps the hand-back
as reported, with its decisions and undo lines. The planning session read
the proposals and decided the first one the same night: in a text box, a
bound key that types nothing runs its command, and the pane's own boxes
count as the pane (rules A and B), plus the small proposals P3, P5, P6 and
P7; P2 and P4 stay proposals. The same coder builds that as round two.

---

Phase 19d, the keys audit: done. Everything is pushed to main (f4eca09 to c70f06a). The report for the creator is docs/log/2026-10-01/keys-audit-report.md, and README.md in that folder has a row for it.

Commits on main:
- f4eca09: the keyboard works in dialogs. Also adds a new snapshot step, `key:<keys>`, and a `x_name` field in the `focus:` log line.
- 5336885: Tab gives the keyboard to a tool tab (Markdown Preview) in the other pane.
- 6db2c21: Tab no longer leaves the theme picker or the plugin list.
- 7487adb: holding a chord's first key down no longer breaks the chord.
- a0b5d44: a chord pressed where it does not apply now says where it works, instead of "not bound".
- 7c32a1b: a "keys" section in the live check, with the Ukrainian-layout step.
- c70f06a: the report and its README row.

Faults found, ranked by how often a user meets them:
1. P1, proposed: in any text box only the Immutable System Tier works. That covers the find box, address box, palette, Quick Open, Search field, marketplace, pattern box and rename box. It is the documented `textInput` rule and the biggest cause of the creator's report. The report gives rule A (keys that type nothing apply in a box) and rule B (the pane's own boxes also count as `filesView`), each size S.
2. F1, fixed: in every dialog only Esc worked. WinUI sends a dialog's keys through the window's root first, and the window held all of them.
3. F2, fixed: a chord worked or failed depending on how long Ctrl+K was held. The first repeat of Ctrl+K was read as the chord's second half.
4. F3, fixed: Tab did nothing while the other pane showed a tool. F5 then copied into the folder hidden under the preview.
5. P2, proposed (S): inside a tool page only Ctrl+Shift+P and Ctrl+` get out. Proposal: the page also hands back the tab keys.
6. F4, fixed: Tab left the theme picker or plugin list on screen with the keyboard on a sidebar row, a rail button or the pane.
7. F5, fixed: a bound chord was reported as "not bound" where its context does not hold.
8. P3, proposed (S): holding a toggle key (palette, terminal, sidebar) repeats it, so it flickers and ends open or shut by chance.
9. P4, proposed (S): overlays stack (the picker over the drive list), and Esc closes the wrong one first.
10. P5, proposed (XS): the permissions review lets Tab out, found by reading. Same one-line fix as the plugin list, not applied because no test can open the review.
11. P6, proposed (XS): Esc or Ctrl+Shift+P during a chord's wait is swallowed as "not bound".
12. P7, proposed (XS): after the palette opened from a tool page, Esc returns the keyboard to the pane, not the page.

Verified:
- Build with `-warnaserror`: passes.
- Window tests: 1042 total, 1041 passed. The one failure was CompactOverlayEndToEndTests' dock/terminal test, a 19c test run on a busy machine. The class passed 4/4 when run alone again.
- An earlier full run against the release core named in the handout had 4 failures, all in the FolderSizes and CompactOverlay end-to-end tests. That core (built 00:02) is older than the 19b/19c core commits. With a core built from current main (`core/target/release` in my worktree), both classes pass 6/6.
- New tests: KeysEndToEndTests 3/3; 4 new chord tests and 7 key-name tests. The 3 end-to-end tests and the 3 chord-repeat tests failed before their fixes; the notice test failed before its fix.
- Real keys: about 695 keys reached the window across two driver runs (494 ran a command). Every fix was re-checked with real keys.
- The live check's keys section, run alone on the Debug build in Windows PowerShell 5.1: 7 of 7 True. build/check-scripts.ps1 parses the scripts in PowerShell 7 and 5.1.
- Ukrainian layout (Enhanced, F0A80422): Ctrl+T, Ctrl+W, Ctrl+P, Ctrl+Shift+P, Ctrl+K Ctrl+T, Ctrl+`, Ctrl+, and Ctrl+\ all work, sent as virtual keys and as physical scan codes. The window matches by virtual key, which is right. The window's layout was restored to US each time.

Decisions not covered by the plan (what — because — undo):
- The `key:` step posts key messages to the window's own input window and sets the modifiers in the UI thread's key state — because WinUI then routes the key as a real one without the window in front — undo: remove PressKeysForSnapshotAsync, the `key` case, and WindowsPlatform.FindDescendant, PostKey, KeyState and SetKeyState, plus the 5 lines added to NativeMethods.txt.
- The `focus:` log line gets `x_name` — because a dialog's buttons have no accessible name to assert on — undo: drop the field.
- Tab into another pane's tool tab leaves the active pane unchanged — because a click into the page does the same, and after the palette the keyboard then returns to the folder — undo: call SetActive in FocusOtherPane.
- The picker swallows Tab, while the plugin list cycles Tab through its buttons (TabFocusNavigation Cycle) — because the list has buttons a keyboard user must reach — undo: revert those two lines.
- A repeat of a chord's first key restarts the chord window — because otherwise a long hold would time out — undo: drop the reset of `_pendingSince` in the repeat branch.
- The notice wording ("does not work while you type in a box. Esc leaves the box.", "works only in a file list.") is mine — undo: make ChordNotice.Text return the old sentence.
- The live check's keys section permanently deletes its own fixture b.txt with Tab and Enter — because a positive answer proves Enter pressed the button — undo: answer with Shift+Tab and Enter instead.
- The textInput rule, the keys tool pages hand back, repeated toggles and the other P items are left as proposals — because each changes a documented rule or lacks a test.

Could not do:
- Open the permissions review: no plugin that asks for a capability in the scratch setup.
- Remove a real drive: I renamed the folder under the pane instead.
- A real Alt+Tab, because it could land keys in another program: my driver took the front with its own window instead.
- About, the update questions, uninstall and the core-failure dialog were not opened. They use the same code as the dialogs I fixed and tested.

Side effects:
- Two of my Enter presses opened scratch files (alpha.txt, beta.txt) as two new tabs in the Notepad that was already running. Each holds "x"; they can be closed without saving.
- Other coders' test windows kept taking the front all night. My driver waited for its own window before every key and brought it back without pressing a key.

---

## Round two, as reported

Keys audit round two (Phase 19d) is done. Everything is pushed to main (1973fe6, then 95832ca). The worktree is clean.

Commits (rebased onto 19f; rebase was clean):
- 02817cb P1: rule A (in a text box, a bound key that types nothing runs; the box keeps its typing and editing keys, in `TextInputKeys.StaysWithBox`). Rule B (the active pane's find box and address box also count as `filesView`, in `CurrentContexts`). Docs: keybindings.md "Contexts" → "Text boxes"; ui.md (the find box, the address box, Quick Open, the Search field, the compact overlay key).
- 0e6b24f P3: a key held down runs its command once (`KeyOutcome.Held`). The commands that repeat are in `ChordStateMachine.RepeatingCommands`. The page scripts (terminal.js, ToolKeyScript) pass a held key only once.
- a332407 P5: the review's card has `TabFocusNavigation="Cycle"`, applied by reading. No test opens the review (it needs a plugin that asks for a capability).
- bc36551 P6: during a chord's wait, Esc only ends the wait (`KeyOutcome.Cancelled`), and Ctrl+Shift+P ends it and opens the palette.
- c2a8d8a P7: `_paletteBackToPage` / `WayBackToToolPage`: when the palette closes, the keyboard goes back to the tool page it was opened from (a pane's tab or the sidebar).
- 0c0a8af live check: F5 is pressed only when the log shows both panes on the fixture. 1973fe6: escaped the backtick in the changelog (UpdateTests caught it). 95832ca: "Round two" section in keys-audit-report.md, plus the README row.
- P2 and P4 stay proposals. P4 is now easier to hit (found by reading): Ctrl+K Ctrl+T works in every box, and opening the theme picker does not close the palette, Quick Open or a prompt, so the picker opens on top of them.

Verification:
- Build with `-warnaserror`: 0 warnings.
- Fast tests: 1103 passed, 32 skipped, of 1135.
- Full end-to-end run, with a release core built from main after the rebase: 1134 of 1135. The one failure, `CompactOverlayEndToEndTests.The_dock_comes_back...`, passed 2 of 3 runs alone. It uses only `cmd:` steps (no keys) and was already flaky in round one.
- New unit tests: a table of 74 keys (26 run, 48 stay with the box), the pane's boxes, the held keys (`palette.show` and `view.toggleSidebar` ignored on repeat, `tab.next` kept), the repeat list, and Esc / Ctrl+Shift+P during a wait.
- New end-to-end tests:
  - Find box: a letter filters, F5 copies the cursor row, Ctrl+A leaves the pane's marks as they were, Ctrl+T and Ctrl+W open and close a tab.
  - Palette from the page: after Esc and after a chosen command, the keyboard is back on the page. This test fails with the fix switched off.
- Live check keys section, run alone with real keys in PowerShell 5.1: 14 of 14 True. check-scripts passes in PowerShell 7.6 and 5.1.

Decisions (what — because — undo):
- AltGr (Ctrl+Alt) with a character key stays with the box — many layouts type with it — `(true,true)=>false`. So Ctrl+Alt+P and 19f's new Ctrl+Alt+C do not run from a box. Ctrl+Alt+Up does.
- Alt with a digit stays — Alt with keypad digits types a character by its code — `(false,true)=>false`.
- Ctrl+Insert stays — the box copies with it — remove it from `CtrlEditing`.
- Plain or Shift keys not in your lists stay (Insert, PageUp, PageDown, keypad operators, Shift+Delete) — the rule runs only Ctrl/Alt/Win combinations and the function keys; Shift+Delete must never delete files from a find box.
- Other Ctrl combinations run (Ctrl+Up, Ctrl+PageDown, Ctrl+Enter, Ctrl+Space) — a one-line box does nothing with them — add them to `CtrlEditing`. Ctrl+Shift+X and Ctrl+Shift+C stay, because the rule keeps "Shift with any of those".
- Win with any key runs — a box types nothing with it.
- A chord is judged by its first half — once the wait starts, the second key belongs to the chord.
- Rule B covers only the active pane's boxes — F5 in the other pane's box would copy from the wrong pane.
- Rule A covers every box, including the rename box. So Alt+Left or Ctrl+L now runs while a name is typed in place.
- The commands that repeat: tab.next, tab.previous, edit.toggleSelection (Insert), go.back, go.forward, go.up — Insert moves the cursor down, and Back/Forward/Up move through folders the way the arrows move through rows. No scroll command is bound in the registry. Undo: edit `RepeatingCommands`.
- A page passes a held key once, whatever its command — the pages pass back only toggles and ways out.
- The tier's single keys win over a chord that ends with them — there is always a way out. Undo: check chords first in `OnKey`.
- P7 also covers sidebar tool pages. If the page is gone or hidden, the keyboard goes to the active pane.
- No DONE file for the real-key runs — it was a night run, and a Notepad window in front would stop other agents' checks.

Side effect: the first real-key run of the section alone had the right pane on `C:\Users\Admin\Documents` (a Tab did not switch panes). F5 copied `Documents\Ableton` (69 files, 470 KB) into `%TEMP%\cabinetos-ui-test\keys-section-2\files\keys20-other`. Documents was only read, nothing in it changed. The copy is still in %TEMP% and was not deleted, because deleting files is a hard stop. The step now uses Ctrl+Right and the log check above, and the second run was clean. (The planning session removed that copy under the creator's standing rule for stale test data, and logged it in `_io`.)

Not done: none of the requested items is missing. P5 has no test, as explained above.
