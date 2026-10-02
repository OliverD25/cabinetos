# A click from the terminal into a pane: the keys after it reached nothing (2026-10-02, night)

Context: a product fault two coders saw in the live check's section 21 on
2026-10-02. The terminal had the keyboard, a real click went into the left
pane, and Home, Enter and Backspace pressed right after did nothing: no
folder opened in the pane, and nothing reached the shell either. Written by
the Opus coder agent in the worktree branch
`worktree-agent-aa76847f45adf775b`, on the main PC, the night of
2026-10-01/02. Every window run waited for the creator's consent file first
(`wait-for-pc.ps1` said "allowed until 07:30"); the laptop and the VM were
not used.

## The probe

`ui/livecheck/click-focus-probe.ps1` repeats the fault with real keys and
the real mouse in about 40 s, in a window of its own with heavy logging on
(`CABINETOS_LOG_HEAVY=1`). It shows the countdown first and writes a
`DONE.md` at the end. Its steps:

1. The left pane in a fixture folder `probe` with one folder `inner` and
   one file.
2. Ctrl+` opens the terminal for the left pane; a typed line that writes a
   file must run (the shell had the keyboard).
3. A half-typed line waits in the shell. It writes a file `leak.txt` when
   Enter reaches the shell, so the file shows whether a key leaked there.
4. A real click into the left pane, then Home, Enter (must open `inner`)
   and Backspace (must come back).
5. A click into the terminal's text and a typed line, which the shell must
   run (the way back).

After the click and after the keys it prints where Windows sends the keys:
the focus window of the window's thread (`GetGUIThreadInfo`), its class and
its process. It also prints the window's heavy lines between the click and
the last key.

## The chain of the fault (the heavy log, before the fix)

The run of 04:51 (`_io\live-check\probe-click-before-2.txt`), with the new
heavy line "focus moving" (below):

1. The click. XAML's focus moves from the terminal's `WebView2` to
   `FilePane LeftPane`, by the pointer (`state: Pointer`, `device: Mouse`):
   this is the pane's own `PointerPressed` handler, `Focus(FocusState.Pointer)`.
2. At that moment Windows already sends the keys to the window
   (`keys_to: window`): WinUI's input window `InputSiteWindowClass` has
   Windows' focus. The suspicion of the handout (Windows' focus stays in the
   WebView2's window) is ruled out: the probe read `InputSiteWindowClass`
   400 ms after the click and again after the keys.
3. 7 to 39 ms later (three runs), a second focus move, again by the pointer:
   from `FilePane LeftPane` to a `ScrollViewer` that is in none of the
   window's views (`within: none`). It is the ScrollViewer that WinUI puts
   around a window's content (its root). The window's code never focuses
   it (no code of the window names it), so the move is WinUI's own. It
   comes only when the click brings Windows' focus back into WinUI's input
   window from another window (here the page's browser window): a click
   from pane to pane never shows it. Why WinUI makes it is not known; it
   is WinUI's code, not ours.
4. Home, Enter and Backspace reach the window's `PreviewKeyDown` ("key
   pressed", `element: ScrollViewer`). No pane holds that element, so the
   key context `filesView` does not hold, and Enter (`pane.openSelected`)
   and Backspace (`go.up`) run no command. Home is the pane's own key
   (`FilePane.OnKeyDown`), and the root ScrollViewer is not in the pane.
5. No "listing shown" line comes; each folder wait of 5 s ran out. That is
   the 11 s of the live check's step.
6. The keys did not reach the shell: `leak.txt` was never written. The page
   had lost Windows' focus at the click.
7. The way back works: a click into the terminal's text moves XAML's focus
   to the `WebView2` (`keys_to: a page`), and the shell runs the next line.

The same chain without the new heavy line: the run of 04:47
(`_io\live-check\probe-click-before.txt`), "focus changed" from
`FilePane LeftPane` to `ScrollViewer` 39 ms after the click.

The end-to-end tests could not see it. Their `key:` steps post key
messages to the window's input window and never click, so WinUI's second
move never happens.
