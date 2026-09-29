## Report: the five live-check findings, fixed

Report: the five live-check findings are fixed — 5 commits on main, 420 UI tests pass; the real-key parts wait for a re-run of ui/livecheck/. Starting the release/About task now.

## Built
1. Ctrl+K V on an open file (finding 1). Cause: each open maps the file's folder to a new virtual host, and a page that is already loaded cannot fetch from a host mapped after it loaded. Now every open maps the new host and loads the tool page again; the page asks with "ready" and then gets "open". A link to a file in another folder works the same way. The sequencing is in a new ToolFileSession (CabinetOS.Core/Tools) with a test.
2. Dialogs (finding 2). The router refuses every command that is not the open dialog's own (CommandRouter.SetModal, new outcome Refused, raised before Executing), whatever sent it: keys, a web page's passed keys, plugins, the snapshot aid. ContentDialogs: Esc closes them even from a selectable text (dialog-level PreviewKeyDown), the keyboard goes to the default button when a dialog opens without it, and a key that reaches the window under a dialog does nothing (Esc hides the dialog, other keys put the keyboard back into it). The permissions review keeps plugins.grant and overlay.close. Dialogs are logged ("dialog shown", "dialog closed").
3. Unicode key events in the terminal (finding 3). terminal.js leaves a keydown with keyCode 231 (VK_PACKET) to the keypress that follows. xterm.js takes ev.key at keydown when keyCode >= 48 (checked in the bundled xterm.js), and that key was stale.
4. Skip through UI Automation (finding 4). A decision, Minimize and Close give the keyboard back to the pane before the part with the focus closes.
5. The pencil's tooltip (finding 5). The palette, theme picker, plugin list, permissions review, context menu and marketplace close open tooltips when they close (Views/OpenToolTips.cs).
6. The live-check scripts are in the repository: ui/livecheck/livecheck.ps1 and livecheck2.ps1, with defaults from the repo instead of one machine's folders, and new checks for the fixes (see "Needs a re-run"). They pass the PowerShell parser; I did not run them (they send real input).

## Commits (on origin/main)
- b5044ec ui: a tool shows a file opened again instead of "Failed to fetch"
- 2d6e246 ui: the terminal types Unicode key events from their keypress
- ed0188c ui: tooltips close with the overlay that showed them
- 5021c14 ui: dialogs hold the keyboard, and the flyout gives it back (findings 2 and 4 together: both live in MainWindow.xaml.cs, and partial staging is not possible here)
- e548db8 ui, docs: the live check's scripts join the repository

## Checks
- Debug and Release builds with -warnaserror: 0 warnings, 0 errors (after the rebase onto cd76411).
- dotnet test: 420 passed, 0 failed, 0 skipped. New tests: While_a_dialog_is_open_only_its_own_commands_run, A_file_opened_again_in_a_loaded_page_gets_a_new_host_after_the_page_loads_again.
- No CI claim (CI paused).

## Live check (snapshot aid, release builds, locked screen)
- Finding 1: reproduced first ("This file could not be read: Failed to fetch"). After the fix, notes.md opened again shows "notes", and src\README.md from another folder shows "x"; "tool ready" follows each open.
- Finding 2: Properties opened with the keyboard on its button (inside the dialog); view.toggleTerminal and go.up were refused ("command refused: a dialog is open"); after it closed, the keyboard was back in the pane and the terminal opened. The permissions review refused view.toggleTerminal and palette.show and closed with overlay.close; the keyboard went back to the marketplace's search field.
- Finding 4: the new snapshot step click:Skip moves the keyboard to the button and invokes it through its automation peer, as assistive technology may. Before the fix the keyboard ended on "Back"; after it, on the pane (new focus: step logs it).

## Needs a real-key re-run (the planning session's)
- Finding 2: Esc on Properties, and Ctrl+` while it is open. Both scripts now print "Esc closed the dialog: True" and "the terminal key was refused while it was open: True" from the window's log.
- Finding 3: livecheck.ps1 types "echo live-5c" and livecheck2.ps1 "echo unicode-5c" with Unicode key events; screenshots show whether the shell echoes them right.
- Finding 5: livecheck.ps1 saves palette-closed-under-the-mouse.png after Esc with the mouse on the pencil.
- Finding 1 with real keys: livecheck.ps1 prints "the preview said ready for each open (2 expected)".
- Finding 4 with a real out-of-process UI Automation invoke: livecheck.ps1 still presses Skip with InvokePattern, then Shift+F10 and Alt+Enter.

## Decided
- Every open loads the tool page again, and the host stays new for every file — because a loaded page cannot fetch from a host mapped later, and one host per folder would change the documented "new for every file" and bring cache questions — undo: ToolFileSession.OpenAsync posts open at once when ready.
- The router refuses all but the dialog's own commands — because keys, web pages and plugins all reach the window through the router — undo: remove the SetModal calls in MainWindow.UpdateModal.
- The permissions review allows only plugins.grant and overlay.close, so Ctrl+Shift+P no longer opens the palette over it — because the review is modal and Esc or Cancel close it — undo: the list in UpdateModal.
- A key that reaches the window under a ContentDialog only brings the keyboard back (that key is lost) — because nothing under a dialog may react — undo: the _openDialog branch in OnPreviewKeyDown.
- The keyboard hand-back also for Minimize and Close, not only decisions — because they close the same way — undo: the two handlers.
- Tooltips close with every overlay, not only the palette — same cause.
- The scripts belong in the repository (ui/livecheck/, documented in docs/ui.md "The live check") — because the snapshot aid presses no keys and these re-runs need them; they stop on a locked screen and when another window comes to the front — undo: git rm -r ui/livecheck.
- The click: snapshot step now moves the keyboard first; a focus:<label> step logs the keyboard — dev only — undo: the two cases in TakeSnapshotsAsync.

## Known gaps
- A real out-of-process screen reader was not tried; the in-process path reproduced the Back-button focus and shows the fix.
- docs/tool-extensions.md still says "A page that is open gets the next file with another open". Ready text below; that file is not mine tonight.

## Noticed out of scope
- Keymap.With (ui/CabinetOS.Core/Keys/Keymap.cs:81) has no caller except a test since protocol 11 moved the window's own chord into the registry. It could be removed.

## Ready text: docs/tool-extensions.md, "Messages", the `open` bullet
Replace "A page that is open gets the next file with another `open`." with: "For every file after the first, the page loads again, because a page that is already loaded cannot fetch from a host mapped after it loaded; it says `ready` again and then gets the `open`."

## Ready text: docs/PLAN.md
Phase 5b note — replace the sentence starting "Open from that check: Esc does not close the Properties dialog" with:
"Both findings of that check were fixed on 2026-09-29: no command runs while a dialog is open, Esc closes it wherever the keyboard is, and a decision on a conflict (also Minimize and Close) gives the keyboard back to the pane. The snapshot aid confirmed both; Esc and a real screen reader's Skip wait for a re-run of `ui/livecheck/` ([log/2026-09-28/live-check.md](log/2026-09-28/live-check.md))."
Phase 5c note — replace the sentence starting "Open from that check: Ctrl+K V on a file the preview already shows" with:
"Both findings of that check were fixed on 2026-09-29: every open loads the tool's page again, so Ctrl+K V on the file on screen and a link to another folder show the file (checked with the snapshot aid), and the terminal page types Unicode key events from their keypress (waits for a re-run of `ui/livecheck/`) ([log/2026-09-28/live-check.md](log/2026-09-28/live-check.md))."

## README
Line 29–30: "(`ui/`, 418 tests)" becomes "(`ui/`, 420 tests)".
