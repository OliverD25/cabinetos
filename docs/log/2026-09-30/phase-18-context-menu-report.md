# Phase 18, step 1, the context menu from the config: the coder's hand-back (2026-09-30, coder on Opus)

The creator chose the desk card "High-Performance Context Menu" as the
next work after Phases 16 and 17 and answered its open questions in chat
(the eight decisions are on the card and in [PLAN.md](../../PLAN.md),
Phase 18). Step 1 went to a coder on Opus in the early evening. The
hand-back, as reported, with the planning session's checks after it.

## The hand-back

Finished and pushed to main, every commit with a normal push:

- 49ca8b7 config: `contextMenu` and `programs`, with today's menus as
  the defaults.
- cfb5dc0 core: programs become `program.<name>` commands the core
  starts.
- 2c879c1 core: Windows' own context menu on a thread of its own
  (`shell_menu`).
- 90804e7 ui: the context menu's pure model, and protocol 15 on the
  window's side.
- 541f351 window: the right-click menu from `cabinetos.json`, and
  Windows' own menu.
- 27a2d7d fs: Cut and Copy from Windows' menu reach the clipboard.
- e60f354, 426cfca live check: section 18, the context menu with the
  real mouse; the warning's full text waited for.
- f584e5d docs: the context menu, programs and Windows' menu.
- cc8d5b3 tests: protocol 15, the changelog's new section, the frame
  test alone.

Checks, as the coder ran them after the last core change: the five
core checks (795 tests passed, 0 failed, 6 ignored, the new clipboard
test being the sixth); the release build of the core, the CLI and the
indexer; the config and protocol schemas regenerated with no diff; the
window's build with warnings as errors (0 warnings); 938 of 938 window
tests with the end-to-end tests on (the first full run had three
failures, fixed in cc8d5b3); `check-scripts.ps1` in PowerShell 7.6 and
5.1. The live check's section 18 was written and not run, as the
handout says.

Verified beyond the tests: a snapshot-aid run with a flyout event log
showed that WinUI drops a `ShowAt` on a flyout that is still closing,
the cause of one end-to-end failure; a new menu now waits for the old
flyout's `Closed`. Another run showed Windows' menu "Copy" ran without
an error and changed nothing; the menu's thread now starts OLE and
flushes the clipboard, and afterwards the clipboard held the file while
the app ran and after it exited. The section 18 config as Windows
PowerShell 5.1 writes it, and both examples in config.md, pass
`cabinetos-cli config validate`. Frame costs: a new `CommandBarFlyout`
costs about 30 ms to build plus one 60 ms frame; a cached shape opens
in about 6 ms with no slow frame; Windows' menu is answered by the core
in 750 to 850 ms cold and built by the window in about 26 ms.

Decisions (what — because — undo):

1. An unknown command ID in `contextMenu` is left out with one warning,
   not a config error — plugin commands come and go — undo: check the
   IDs against the registry in `cabinetos-config`'s `parse.rs`.
2. Properties and `menu.edit` named in the file are skipped; they always
   sit at the end — a menu must never lose them — undo:
   `ContextMenuModel.Build`.
3. A plugin command the file lists is not repeated in FROM PLUGINS —
   undo: drop the `listed` filter in `Build`.
4. `{selection}` must be a whole argument; a brace that is not one of the
   three tokens is plain text; an unknown `{word}` is an error — undo:
   `parse_arg` in the core's `menu.rs`.
5. The window keeps one flyout per menu shape (up to six) and uses a
   pending show while the old flyout closes — the build cost above
   (Article 1), and WinUI drops a `ShowAt` on a closing flyout — undo:
   build a new flyout in `ContextMenuFlyout.Show` (the pending logic
   stays).
6. The shell menu's thread uses `OleInitialize` and flushes the
   clipboard after an item runs — without it Cut and Copy did nothing —
   undo: `Apartment::enter()` in `shellmenu.rs`.
7. The clipboard test is `#[ignore]` — it replaces the user's clipboard
   — run it with `--ignored`.
8. Windows' menu leaves out owner-drawn, greyed and textless items, reads
   submenus one level deep, and takes at most 1,000 paths.
9. Plugins may never send `shell_menu` or `shell_menu_invoke`
   (`NEVER_ALLOWED`, now 20 entries) — undo: `policy.rs`.
10. A program whose ID another command already has is left out, with a
    warning.
11. A client with no window state (the CLI) uses the newest state of any
    window.
12. Before `execute_command` of a `program.*`, the window stops its state
    timer and sends `window_state` first, on the same pipe.
13. Test fixes in cc8d5b3: the end-to-end greeting expects protocol 15;
    `UpdateTests` checks every section it renders the same way, and the
    0.1.0 paragraph, links and code on 0.1.0 only; the 100,000-row frame
    test moved into its own non-parallel collection, since it saw one
    35 ms frame while other test windows ran beside it and none alone.
14. ARCHITECTURE.md's crate map and core/README.md: `cabinetos-fs` also
    builds Windows' menu and starts programs, with a change-log row.

Left as found or out of scope: the first opening of each menu shape in
a process still costs one frame of 45 to 60 ms (WinUI loads the
template; the flyouts could be pre-built while the window is idle);
the window's Ctrl+V does not paste files that Windows' Copy put on the
clipboard (documented in ui.md); dialogs that Windows' items open
(Properties, Open with) belong to the core, have no owner window, and
may open behind CabinetOS; step 2, the in-menu edit mode, is not
started.

## The planning session's checks (step 1)

Main pulled at f584e5d, then cc8d5b3, on the main checkout. The five
core checks: the first chain reported "TEST DONE" without having run
the tests, because cargo could not relink the debug core while the
window's unit tests held that file, and the grep behind the pipe hid
cargo's exit code; rerun alone with the pipe status checked: 795
passed, 0 failed, 6 ignored; clippy, fmt and deny green. The release
core, CLI and indexer built; the window's Debug build with warnings as
errors (0 warnings); 938 of 938 window tests with `CABINETOS_UI_E2E=1`,
against the release core. Two test fixes the planning session made in
parallel (the greeting's protocol 15, the changelog test on the frozen
0.1.0 section) were dropped in favour of the coder's own cc8d5b3, which
does the same.

The live check with real keys, the Release window of 20:36 and the
release core of 20:34 (`run-2026-09-30-2038.txt`): exit code 0, 143
True, 2 False, both in the new section 18 and both the section's own
reading: "the left pane is in menu18" read the cursor row before the
listing had arrived (and expects a file name where the first row is the
folder `bg18`), while the next check found `row-12.txt` under the
pointer; "the program got the row's path" read the recorder's file
before its line was complete, while the printed value was the right
path. Every earlier section passed on the new menu; the scroll goal was
met with no frame over 20 ms; Windows' menu showed 30 items, answered
by the core in 817 ms, and its Copy put the row's file on the
clipboard. A second run (20:49) was stopped by the foreground guard at
its edge section when the creator, back at the PC, switched to Chrome;
the third (20:57, after 45 s of idle input) gave 144 True and 1 False:
the program check passes with its wait, and the pane check's pattern
now accepts the folder's name, confirmed by the next full run.

Step 2, the edit mode inside the menu, went to a coder on Opus at 20:56,
since step 1's window behaviour was verified with real keys by the
20:38 run.

## Step 2, the edit mode inside the menu: the coder's hand-back (coder on Opus, 21:42)

Commits, all pushed with normal pushes after a rebase: acc0e30 ui: the
context menu's edit model, pure and tested; 1e65d4e window: "Edit Menu…"
turns the right-click menu into its edit mode (the surface, its keys and
Done and Cancel share two files, so one commit); 6bbf78c tests: the edit
mode end to end, and over 100,000 selected rows; fbe0736 live check:
section 18 edits the file menu inside the menu; db7d0f3 docs;
4e20903 window: the edit mode's drop does not hang on the order of two
pointer events.

Checks: the window's build with warnings as errors (0 warnings); 963 of
963 window tests with the end-to-end tests on (1 min 26 s, started when
no CabinetOS window ran); the context-menu classes alone 50 of 50;
`check-scripts.ps1` in both PowerShells, also after the rebase onto the
live-check fix. No core change, so the five core checks were not run;
the release core was built for the end-to-end tests.

Beyond the tests: the Debug window driven with the snapshot aid (the
edit mode opens where the menu was, the prompt opens over it, the
empty-space menu works too); a real save (the core wrote
`contextMenu.file.items` and logged the change); a refused save (the
file broken and restored from a script: the notice showed, the mode
stayed open); the first "Edit Menu…" builds in 20 to 22 ms, later ones
in about 5 ms. Not verified by the coder: the real mouse and keys, the
real pointer drag, the light theme.

Decisions (what — because — undo):

1. The edit surface is an in-window overlay (`Views\ContextMenuEditor`,
   a transparent scrim), not a flyout — a click outside must be
   swallowed, the prompt must open over it, and the snapshot aid cannot
   draw a WinUI flyout — undo: the same content in a Flyout with its
   Closing cancelled.
2. Placed at the chosen menu button's bounds, width the larger of the
   menu's and 280 px, the pointer or the row as the fallback anchor —
   each row gains a handle and an X — undo: `MinPanelWidth`, `Show()`.
3. The 120 ms entrance of the Phase 16 dropdowns, no jiggle — undo: the
   Storyboard in the XAML.
4. `menu.edit` from the palette or a key edits the focused row's menu at
   its Shift+F10 place — no menu is open then — undo: `EditMenu()` in
   `MainWindow.ContextMenu.cs`.
5. Done with no change closes without `set_value` — undo: `DoneAsync`.
6. "Open the file" leaves the mode unsaved and runs `settings.open` —
   the file and the mode must not both change the list — undo: the
   `OpenFile` lambda.
7. Every entry of `items` is a row, unknown IDs greyed with "no command
   has this ID" — so they can be removed — undo: the model's constructor.
8. "Add Command…" offers the files-view commands, `program.*` and every
   plugin command, without Properties, Edit Menu… and rows already
   listed, sorted by title, the prompt row showing the ID — undo:
   `Addable()`.
9. While open, the mode holds the keyboard like a dialog: no window
   binding runs, so Delete never reaches `file.delete`; arrows,
   Alt+arrows and Delete act only with the focus in the rows — undo: the
   block in `HandleWindowKey`.
10. An edit of the same list in the file while the mode is open is
    overwritten by Done — undo: compare with `_menuConfig` before
    sending.
11. A refused save logs "menu edit refused" at warn; the notice reads
    "The menu was not saved: <the core's message>".
12. A drop commits on whichever comes first, `PointerReleased` or
    `PointerCaptureLost` with the button up.
13. New snapshot-aid steps `menu-edit-key:<keys>`,
    `menu-edit-drag:<title>|<title>`, `until:config-error`; the shell
    state line gains `menu_edit` and `menu_edit_target`.
14. The live check puts the program back with a second edit (Insert,
    the prompt, Enter, a real drag, Alt+Down and Alt+Up, Ctrl+S), not
    with the CLI — so the real keyboard and the real drag are covered.

Found on the way, a step 1 fault: a menu of a shape whose flyout is
still closing never comes on screen again, and every later menu of that
shape stays pending, because `ContextMenuFlyout.OnClosed` re-shows the
same cached flyout inside its own Closed handler, which WinUI drops
(checked with UI Automation). The coder's own end-to-end test waited
600 ms between menus to avoid it. Sent back to the same coder with a
test to reproduce it first. Also left: the comment in the core's
registry near `menu.edit` still says it opens the file.
