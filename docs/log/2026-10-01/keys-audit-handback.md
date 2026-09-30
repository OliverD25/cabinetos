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
