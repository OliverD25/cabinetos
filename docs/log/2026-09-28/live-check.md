# Live check with real keys, 2026-09-28, 23:35 to 23:41

The shell (Phases 5, 5b and 5c) and the protocol 11 core were checked with
real key presses and mouse clicks on the development PC, screen unlocked, by
the planning session while the creator watched. Two runs of a PowerShell
script (`livecheck.ps1` and `livecheck2.ps1`, written by the UI agent and the
planning session) started the window with a configuration, a log folder and a
themes folder of its own, sent keys with `SendInput`, took screenshots of the
window, and would have stopped the moment another window came to the front.
Build under test: the shell from `main` at 23:16 (the first half of the
protocol 11 adoption: the window's commands from the registry, the items
graph, the dock size) and the core at 9eea7cd (protocol 11), both copied to a
folder outside the repository. Transcripts:
[live-check-run1.log](live-check-run1.log),
[live-check-run2.log](live-check-run2.log).

## What passed

- The window: two panes, the sidebar with pinned folders and drives, the
  status bar ([live-window.png](live-window.png)).
- The palette: `Ctrl+Shift+P`, typing "dual" finds View: Toggle Dual Pane
  with its keys ([live-palette.png](live-palette.png)); `Ctrl+Shift+D` gives
  one pane and back; Tab switches panes; Enter opens the selected folder and
  Backspace goes up.
- Rebinding through the pencil: View: Toggle Sidebar rebound to `Ctrl+Alt+B`
  by a mouse click on the pencil and the key press. The palette shows the new
  keys ([live-rebound.png](live-rebound.png)), and the core wrote
  `{"command":"view.toggleSidebar","keys":"ctrl+alt+b"}` into the run's
  `cabinetos.json`.
- The 100,000-entry folder: Enter on it showed the listing 88 ms after the
  key press (the core's `listing_opened` after 65 ms; the first page's type
  names 12 ms later; the first icons after 49 to 139 ms, from the system
  image list). PageDown held for 5 s (about 150 presses) scrolled to row 3,031
  ([live-scrolled.png](live-scrolled.png)). The frame statistics of those
  seconds (`CABINETOS_UI_FRAMESTATS=1`):

  | Second (UTC) | Frames | Worst frame | Frames over 20 ms | Frames over 33 ms |
  |---|---|---|---|---|
  | 20:35:29 | 59 | 52.0 ms | 15 | 1 |
  | 20:35:30 | 59 | 43.5 ms | 22 | 2 |
  | 20:35:31 | 61 | 29.7 ms | 20 | 0 |
  | 20:35:32 | 53 | 77.7 ms | 20 | 4 |
  | 20:35:33 | 61 | 29.9 ms | 20 | 0 |
  | 20:35:34, after the keys stopped | 53 | 96.5 ms | 4 | 2 |

  No frame took longer than 100 ms. About a third of the frames during the
  scroll took between 20 and 33 ms.
- File keys, checked on disk: F7 made "Reports 2026", named in place; F2
  renamed notes.md to readme.md; Delete sent a file to the Recycle Bin; F5 on
  report.txt raised the conflict card ([live-conflict.png](live-conflict.png)),
  Skip left the destination's 12-byte file untouched, and the flyout ended
  with "Copy complete, 1 of 1 item".
- After Skip by a mouse click the keyboard is back in the pane: Shift+F10
  opens the row's context menu ([live-context-menu.png](live-context-menu.png))
  and Alt+Enter opens Properties ([live-properties.png](live-properties.png)).
- The terminal: Ctrl+` opens a pwsh shell in the active pane's folder. Typed
  with a keyboard's key events, `echo live-5c` answers `live-5c`
  ([live-terminal.png](live-terminal.png)). Ctrl+Shift+P inside the terminal
  reaches the window and opens the palette, which lists the 51 commands of the
  protocol 11 registry ([live-palette-from-terminal.png](live-palette-from-terminal.png));
  Esc gives the keyboard back to the shell; Ctrl+` once more returns the
  keyboard to the pane, and again hides the terminal.
- Search: Ctrl+F and "report" asks the core. Without the indexer the answer
  is a walk of the folder, marked incomplete at its 20,000-entry limit, with
  the hint to start the indexer ([live-search.png](live-search.png)); Esc
  returns to the folder.
- Markdown Preview: Enter on readme.md opens it in the other pane's editor
  tab ([live-markdown.png](live-markdown.png)).
- The window closes with exit code 0 and ends the core it started. A key
  press's request ID reaches the core's "job queued" line (protocol 11).

## Findings, for the UI agent

1. **Ctrl+K V on a file that Markdown Preview already shows says "This file
   could not be read: Failed to fetch"**
   ([live-markdown-chord-error.png](live-markdown-chord-error.png)); a second
   Ctrl+K V too. Enter on the file works. The UI log shows the chord's
   `editor.openMarkdownPreview` and "file opened in a tool", but no "tool
   ready" after it.
2. **Esc does not close the Properties dialog, and window commands still run
   from the keyboard while it is open.** After Esc the dialog stayed; Ctrl+`
   then opened the terminal under the dialog and took the typing, and Ctrl+L,
   Enter and Ctrl+K V all worked with the dialog still open (screenshots 2 to
   3c of run 2). Expected: Esc closes the dialog, and the router runs nothing
   while a dialog is open.
3. **Text injected as Unicode key events into the terminal page becomes one
   repeated character.** `echo unicode-5c` arrived as `ttttttttttttttt`
   ([live-terminal.png](live-terminal.png), second command); in run 1
   `echo live-5c` arrived as `eeeeeeeeeee`. A physical keyboard's key events
   work. Windows' touch keyboard, Voice Access and automation tools send
   Unicode key events; the panes' text boxes take them correctly.
4. **A conflict card button pressed through UI Automation (a screen reader,
   Voice Access) leaves the keyboard focus on the Back button.** In run 1,
   after Skip through `InvokePattern`, Shift+F10 opened nothing and Alt+Enter
   pressed Back. A mouse click on Skip returns the focus to the pane (run 2).
5. **The pencil's tooltip "Change keybinding (F2)" stays on screen after the
   palette closes** while the mouse rests where the pencil was
   ([live-scrolled.png](live-scrolled.png), above the right pane's title).

## Leftovers

- One test file in the Recycle Bin, `cabinetos-live-check-delete-me.txt`
  (3 bytes), from run 1.
- The runs' folders `%TEMP%\cabinetos-ui-test\live` and `live2`
  (configuration, logs, test files, full-size screenshots).
