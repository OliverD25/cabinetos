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
   comes when the click brings Windows' focus into WinUI's input window
   from another window: here the page's browser window. The probe of 04:54
   saw it once more, at its very first click, a moment after the script
   brought the window to the front with `SetForegroundWindow`; the keys
   there were global ones (Ctrl+`), so it did no harm. The live check
   clicks from pane to pane many times and the keys after those clicks
   work, so a click while WinUI's input window already has Windows' focus
   does not cause it. Why WinUI makes the move is not known: it is
   WinUI's code, not ours.
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

## The cause and the fix

The cause: WinUI's own second focus move, by the pointer, to the
ScrollViewer around the window's content, a few milliseconds after the
pane took XAML's focus. Windows' focus was right all along: the click
had given it to WinUI's input window.

The fix (`ui/CabinetOS/MainWindow.Keyboard.cs`, `OnGettingFocus`): the
window listens to `FocusManager.GettingFocus` and refuses the move
(`TryCancel`, else `TrySetNewFocusedElement` back to the old element)
when all of these hold (`ClickFocus.RefuseMove`, a pure rule in
`CabinetOS.Core`, five unit tests in `ClickFocusTests`):

- the move's focus state is `Pointer`;
- it goes to `RootGrid` or an element around it (the window's root,
  which holds none of the window's controls);
- XAML's focus is now on an element inside `RootGrid` (a control of the
  window);
- that element is not a `WebView2` (a page's keys are its browser's,
  and its own hand-over, `PageKeyboard`, decides there).

Each refusal is logged (Article 12): "a click's focus move to the
window's root refused" (target `cabinetos_ui::shell`) with `kept_on`,
`view` and `refused`. No Win32 focus call is made: Windows' focus is
already in WinUI's input window. Nothing moves the keyboard by itself
(Zero-Hijack): the control the user clicked keeps it, and nothing else
changes. The window's own code never focuses its root, so the rule
cannot undo a move the window made.

The heavy-mode line that found it stays: "focus moving" (from
`GettingFocus`: from, to, `within`, `state`, `device`, `direction`), and
"focus changed" got the field `within` (the window's view around the
element). The heavy `GettingFocus` handler is subscribed only while heavy
mode is on.

## The probe, before and after

| Run | Windows' keys after the click | XAML's focus after the click | Home and Enter opened `inner` | Backspace came back | A key reached the shell | The way back (a click into the text, a typed line ran) |
|---|---|---|---|---|---|---|
| before, 04:47 (`probe-click-before.txt`) | `InputSiteWindowClass` | `ScrollViewer` (the root) | False | False | False | True |
| before, 04:51 (`probe-click-before-2.txt`, with "focus moving") | `InputSiteWindowClass` | `ScrollViewer` (the root) | False | False | False | True |
| after, 04:55 (`probe-click-after.txt`) | `InputSiteWindowClass` | `FilePane LeftPane`; the move to the root refused | True | True | False | True |

After the fix the heavy lines read: the move to `FilePane LeftPane` by
the pointer; the move to `ScrollViewer` (`within: none`) by the pointer,
with no "focus changed" after it; "a click's focus move to the window's
root refused" (`kept_on: FilePane LeftPane`, `refused: true`); then
`key pressed` home, enter (`command run pane.openSelected`) and backspace
(`command run go.up`), each with `element: FilePane LeftPane`. The files
are in `_io\live-check`.

The run of 04:54 also logged a refusal at its very first click, before
the terminal was open, a moment after the script brought the window to
the front: the same move of WinUI's, whenever a click brings Windows'
focus into the window's input window from another window.

## The live check's section 21

The unit 1 step ("a click in the left pane and a folder opened there")
clicked from the terminal into the left pane and pressed Home, Enter and
Backspace, but checked only that no tab changed and no terminal request
went out. A new line now checks the keys: "21: the keys right after the
click acted on the pane: Home and Enter opened inner, Backspace came
back (inner, term21): True". Before the fix the same step took 11 s in
the runs of 02:25 and 03:46 (both folder waits of 5 s ran out), which is
the fault this line now reports as False.

The unit 2 step keeps Ctrl+` to give the keyboard back to the pane: it
checks `HandBackToPane`, a path the click does not take. Its comment no
longer calls the click broken.

## Checks

- **Window build:** `dotnet build CabinetOS.sln -warnaserror` (Debug) and
  `-c Release -warnaserror`: 0 warnings, 0 errors.
- **Fast tests:** `dotnet test --solution CabinetOS.sln --no-build`: 1259
  total, 1208 passed, 51 skipped (the end-to-end tests), 9 s.
- **Full suite with the end-to-end tests** (`CABINETOS_UI_E2E=1`, the
  worktree's release core), three runs with the fix:
  1. 1257 of 1259, 2 failed: `ShellEndToEndTests.Quick_Open_finds_in_the_repository_opens_here_or_in_the_other_pane_and_switches_to_commands`
     (its state "typed" had no Quick Open row) and one whose name was
     cut from the output;
  2. 1259 of 1259 (2 min 5 s);
  3. 1257 of 1259, 2 failed: the same Quick Open test and
     `ContextMenuEndToEndTests.The_keyboards_menu_for_a_row_out_of_view_opens_inside_the_window`
     (no "context menu shown" line).

  The two tests alone, three times with the fix: 2 of 2 each time. The
  same build without the fix (its one subscribing line removed for the
  comparison, then put back and rebuilt), the full suite twice: 1257 of
  1259 with the same two failures, then 1258 of 1259 with the context
  menu test failing. So both are flaky under the full suite's load
  (about six windows at once) with or without the fix, not caused by it.
  No test clicks with the mouse, and the new rule acts only on a move by
  the pointer.
- **Scripts:** `build\check-scripts.ps1`: every script parses in Windows
  PowerShell 5.1 (22; two are for PowerShell 7 only) and in PowerShell 7
  (24).
- **Live check** on a fresh Release build, through
  `run-livecheck.ps1 -MinimizeOthers`, started from Windows PowerShell:
  `run-2026-10-02-click-focus.txt`, 218 True, 0 False, 7 min 49 s, the
  scroll goal met. Unit 2's last run had 217 True; the one more is the
  new line of section 21.
- **Core:** untouched. The window runs used the worktree's own release
  core, built from the same commit as main (see the decisions).

## Decisions

- **The fix refuses WinUI's move instead of calling Win32's `SetFocus`**
  — because the probe showed Windows' focus already in WinUI's input
  window after the click; only XAML's focus was wrong — undo: revert the
  fix commit (`ClickFocus.cs`, `ClickFocusTests.cs`, `OnGettingFocus` in
  `MainWindow.Keyboard.cs` and its one line in `MainWindow.xaml.cs`).
- **The rule covers every control of the window, not only the panes,
  but never a page** — because the same move of WinUI's would take the
  keyboard from any control clicked from the terminal (the address box,
  the sidebar), and the window's own code never focuses its root; a page
  is left to its own hand-over — undo: in `OnGettingFocus`, replace
  `IsWithinContent(old)` with a check for a `FilePane` ancestor.
- **The window runs used the worktree's own release core, not the main
  checkout's** — because the main checkout's `cabinetos-core.exe` was
  built at 02:38, before unit 2's core commits (the prompt hook,
  `terminal_folder_changed`), which section 21 and unit 2's end-to-end
  tests need; the worktree's was built at 04:47 from this branch's base
  (`f94257e`, main's head), and `core/` is untouched. The first probe
  (04:47) used the main checkout's core; the focus does not depend on it
  — undo: nothing to undo.
- **Section 21's unit 2 step keeps Ctrl+`** — because it checks
  `HandBackToPane`, which the click does not; the click is now checked
  in the unit 1 step — undo: replace its Ctrl+` press with
  `ClickLeftPane` and drop the `HandBackToPane` condition.
- **No live check on a build without the fix to show the new line
  False** — because the probe ran the same keys and the same "listing
  shown" checks before the fix (False), and the earlier live check runs
  show the step's two folder waits running out; that spared 8 minutes of
  the PC — undo: nothing to undo.
- **The probe is committed** (`ui/livecheck/click-focus-probe.ps1`) —
  because it repeats the fault in 40 s and shows at once whether a WinUI
  update changes the move — undo: delete the file.
- **Heavy mode keeps "focus moving" and `within`** — because they told
  who moved the focus, which "focus changed" could not, and they cost
  nothing outside heavy mode — undo: revert the `MainWindow.Diagnostics.cs`
  part of the first commit, and the rows in `docs/diagnostics.md` and the
  heavy-logging skill.

## What is left

- Why WinUI makes the second move is not known (Windows App SDK 2.5.1,
  WinUI 2.3.9). If an update stops it, the rule never acts and the log
  line never comes; the probe shows which.
- A click on the chrome (the status bar, an empty part of the top row)
  while the terminal has the keyboard was not tried. The rule leaves
  that case alone (XAML's focus is still on the page, its last
  condition). If WinUI moves the focus to the root there too, the keys
  after such a click reach nothing, as they did before this fix. The
  probe can try it with one more click.
- Two end-to-end tests fail now and then in the full suite on this PC,
  with or without this fix: the Quick Open test (no row for "notes")
  and the out-of-view context menu test (no menu shown). They pass
  alone. Worth a look of their own.
- Nothing needs the creator.

## Commits

| Commit | Subject |
|---|---|
| 04e77f1 | live check: the click-focus probe, and heavy mode says who moves the focus |
| 77ffefe | window: a click from the terminal into a pane keeps the keyboard in the pane |
| f070a88 | live check: section 21 checks that the keys after a click from the terminal act on the pane |
| (this report's commit) | docs: what a click from the terminal does to the keyboard, the new log lines, the report |
