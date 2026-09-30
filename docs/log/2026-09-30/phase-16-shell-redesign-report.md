# Phase 16, the shell redesign: the coder's hand-back (2026-09-30, coder on Opus)

The creator's handout `_io/SHELL_REDESIGN.md` (one top row, per-pane
breadcrumb rows, find in pane, Quick Open) was handed to a coder on Opus
at midday with the planning session's decisions ([PLAN.md](../../PLAN.md),
Phase 16). The hand-back, as reported, with the follow-up and the
planning session's own checks after it.

## The hand-back

Finished and pushed to main, ffcdea3..ff2f84d, one clean rebase onto eight
newer main commits. Commits: 8ff5f7b registry commands; 218b34d theme
metrics and colours; c12a7ae Core rules with tests; 81e2e10 top row,
breadcrumb rows, tab strips; 158072d Find in Pane; 241e9b2 Quick Open and
the pill's branch; 5515dc6 hamburger, workspace dropdown, Settings;
56340fc Ctrl+K W dropdown, "Open with" on the right; cc0fcdb live check
section 16 and the key updates; 2b5f534 docs; ff2f84d `claude-terminal.ps1`
presses Ctrl+Alt+P (the script arrived on main during the work).

Checks, all green: the five core checks (727 tests passed, 5 ignored);
the release core, the Debug `-warnaserror` and the Release UI builds; 898
window tests with the release core after the rebase. The live check was
not run by the coder; its section 16 is written and the script parses.

The handout's §7 acceptance, verified by the new `ShellEndToEndTests`:
924 px dual pane with no overlap (command center 314 px); below 640 px
(tested at 620) the command center hides and the rest stays; the
handout's path renders as `C: › … › Projects › fileforge`; a second tab
navigates away and switching back restores the first tab's path, cursor
and scroll; Ctrl+F filters only the active pane and Esc restores the full
list; Esc closes the hamburger and the workspace dropdown. Not verified by
the coder: a click outside a dropdown (the live check covers it); Tab and
Ctrl+Tab (unchanged code, live check section 12).

Decisions the handout did not cover (what — because — undo):

1. The workspace is the git repository holding the active folder, else
   that folder; it gives the pill's branch and Quick Open's search folder
   — workspaces do not exist yet — undo: return `Active.Path` in
   `WorkspaceRoot()` in `MainWindow.Shell.cs`.
2. The window read `.git\HEAD` itself (at most 1 KB, background thread),
   recorded as an exception in ARCHITECTURE.md's change log — the core had
   no request for it — replaced the same day by the follow-up below.
3. In the classic and right layouts Ctrl+Shift+F shows the Search view in
   the sidebar's place — the command bar's field was the only search
   there — undo: remove `_searchInSidebar` in `MainWindow.Rail.cs`.
4. Ctrl+K W (`workspace.switch`) opens the pill's dropdown; its "Open
   folder as workspace…" row shows a "later version" notice — the
   dropdown had no key otherwise — undo: remove that handler in
   `RegisterShellCommands`.
5. Quick Open's Enter on a file lists its folder with the file under the
   cursor and does not open the file; Ctrl+Enter makes the other pane
   active and turns dual on if needed — undo: `MainWindow.QuickOpen.cs`.
6. Ctrl+9 with fewer tabs does nothing; `tab.select` from the palette
   shows a notice.
7. Going to another folder closes the find; a tab keeps its find text and
   scroll position in memory, not in `cabinetos.json`.
8. After a no-match filter the cursor returns to its last matched row (a
   small `SelectionModel` change).
9. The hamburger shows the registry's titles ("Browse Plugins and Themes",
   "Open Keyboard Shortcuts"); plugins cannot add rows, since the registry
   has no menu marker.
10. The dropdowns reuse the context menu's surface; a click on the top
    row's empty drag area does not close them.
11. The window's minimum width went from 760 to 600 px, so the 640 px rule
    can show; the command center narrows between the clusters (at least
    120 px) instead of overlapping them.
12. The nine old theme metrics stay accepted but size nothing, so older
    themes stay valid; no `cabinetos.json` key served only the removed
    parts.
13. The top row, the breadcrumb row and the tab state landed in one commit,
    because `MainWindow.xaml` ties them together.

Fixed along the way: the rail end-to-end test waited for a timer nothing
referenced, which the garbage collector removed (the field keeps it now);
the find box reopened after Hide because WinUI raises `TextChanged` later.
No key of the handout's table was taken. Worth doing later: `CrumbFit` in
`CabinetOS.Core/Presentation` is now unused (only `LongPathTests` call it).

## The follow-up: the git read moves into the core (2656e01)

Decision 2 broke the first Prime Directive of [ARCHITECTURE.md](../../ARCHITECTURE.md)
§1, "no file I/O or data processing in the UI"; a change-log row cannot
make an exception to it. The same coder moved the read into the core the
same afternoon: the request `workspace_info` (`path`) answers `root` and
`branch` (a branch ref, a detached hash's first 7 characters, a worktree
or submodule `gitdir:` pointer followed, no `.git` gives null; at most
1 KB read, never a git process; `invalid_path` for a relative path). The
window asks it from `UpdateWorkspaceAsync`; the window-side file read and
its eight C# tests are gone, the core's tests replace them (a branch ref,
a detached hash, a worktree and a submodule pointer, no `.git`, the 1 KB
cap, a pipe test). Core 735 tests, window 890. Decisions: the protocol
version stays 13 with `workspace_info` listed under it, as Phase 15 did
for `save_log_bundle` before the first release (undo: `PROTOCOL_VERSION`
14 and the three tests that expect 13); the reply always carries
`branch`, null when there is none; an older core's `unknown_request`
means the pill shows no branch, with no fallback to reading files.

## The planning session's checks

Main at ff2f84d on the main checkout: the five core checks green (727
tests), the release core built, the window's Debug build with warnings as
errors, 898 window tests with that core, the Release window built. Then
the follow-up pulled (2656e01) and Commander Compact bumped to 1.1.0
(508eeba), because the redesign gave the theme eleven metrics and the
public index would have offered changed content under the old version.

The live check with real keys stopped three times before it could run to
the end, each time through its own foreground guard: twice a coder's
window end-to-end tests opened real CabinetOS windows that took the
foreground, once the Claude desktop app came to the front when this
session's turn ended while the check ran in the background. Lesson,
recorded in memory: a real-key run goes in the foreground of one tool
call, and only while no coder runs window tests. Its first 46 checks were
True and the scroll goal was met in both partial runs (0.0 % and 0.3 % of
frames over 20 ms).

**The full run at 508eeba (14:58, `run-2026-09-30-1458.txt`):** exit code
0, 118 checks True, 6 False, all six in the new section 16, the scroll goal
met with no frame over 20 ms (worst 17.7 ms). Every section that existed
before the redesign passed on the redesigned window: the palette and the
pencil, the tabs, the terminal, the rail, the Agent's ask step, the
marketplace, Commander Compact and back. The six were read against the
window's log of the run:

- "the pane shows one row of two": the `find filtered` line says
  `matches 1, rows 1`; `rows` is the rows shown, not the folder's total
  (the `find closed` line says `rows 2`). The behaviour is right; the
  check compared the wrong field.
- "Quick Open showed" and "the menu showed": both lines were logged
  (`quick open shown` at 12:04:31.5 UTC, `menu shown` at 12:04:40.7) but
  were not yet in the file when the checks read it 2.1 s and 0.7 s later.
  (First read as a file buffer; wrong. The window's log writer takes lines
  through a lock-free queue on its own thread and writes them unbuffered;
  a line lands when that thread gets its turn, which a fixed sleep cannot
  promise.) Evidence from the log needs a wait, as the Claude Code probe
  does.
- "Enter opened the row in the pane" and "Alt+Left ran go.back from a
  key": the section's fixture puts `target-16.md` in `shell16\beta\deep`
  while the pane, and so the workspace (a plain folder, no repository),
  is `shell16\alpha`; Quick Open searches the workspace and cannot find
  the file, Enter does nothing, and Alt+Left has no history ("the pane is
  back in alpha" passed for the wrong reason). The fixture gets a fake
  repository (`shell16\.git\HEAD`), which also makes the pill's branch
  testable.
- "its first row is New Tab": read as a flyout, a window of its own, which
  a search of the main window's automation tree never sees. Wrong too: the
  coder's hand-back below has the real cause, the same still-open Quick
  Open.

The six fixes went to a coder on Sonnet the same afternoon. The Claude
Code probe (`claude-terminal.ps1`) ran on the same build at 15:08 with the
new Ctrl+Alt+P: every check True.

## The evidence fixes: the coder's hand-back (7fdd634, coder on Sonnet)

One commit, `ui/livecheck/livecheck.ps1` and a paragraph in
[ui.md](../../ui.md); the window untouched, so no test counts. The coder
did not run the live check: the script parses in PowerShell 7.6 and 5.1,
and the new helpers were run against a fake log in both shells. The
planning session added `ui\livecheck` to `build/check-scripts.ps1` the
same hour (6b6daac), so the parse check is no longer done by hand.

What each check measures now:

- "the pane shows one row while the text is there": the last `find
  filtered` line has `matches` 1 and `rows` 1, and its wait targets the
  line for the whole text (`query_length` 5), since the window logs one
  line per key and skips stale ones.
- "Quick Open showed", "the menu showed" and every other log check of the
  section: `WaitShellLines` polls up to 5 s for a line that was not there
  before the key or click.
- "Enter opened the row in the pane" and "Alt+Left ran go.back from a key":
  `shell16` now holds a fake repository, `.git\HEAD` with
  `ref: refs/heads/live-16`, so it is the workspace and Quick Open finds
  `target-16.md` from `alpha`. The Back check needs `command` `go.back` and
  `trigger` `key`. A new check reads the branch `live-16` from the `quick
  open shown` line, so the pill's branch is covered too.
- "its first row is New Tab": `AppElement 'New Tab' 2` searches every
  window of the app, found by process ID, for up to 2 s; the two close
  checks count only when the row was seen open first (`AppElementGone`),
  so they cannot pass on a menu that never opened.

Two readings of the planning session were wrong, and the hand-back
replaces them:

- Alt+Left did go through the router. Quick Open was still open because
  Enter had found nothing; focus was in its text box, and there a binding
  runs only when its command is in the Immutable System Tier
  (`ChordStateMachine.Applies`). `go.back` is not, so nothing ran. That is
  by design (Article 7), not a fault.
- The hamburger is not a flyout window; it is a `Canvas` overlay inside
  the main window (`FileContextMenu.xaml`). The run's log shows the first
  click on Menu never opened it: the still-open Quick Open closes on a
  click outside and swallows that click. So "the menu showed: False" and
  "New Tab: False" were true findings with one cause, the fixture, which
  the fake repository removes. The search across the app's windows stays,
  for a window of its own if one ever exists.

Decisions (what — because — undo):

- `AppElement` takes the desktop's children with this app's process ID and
  searches each window's descendants by name — one AND-condition over the
  whole desktop would walk every other program's UI tree — undo:
  `RootElement.FindFirst(Descendants, AndCondition(Name, ProcessId))`.
- `WaitShellLines` has an optional `-until` scriptblock on the last line —
  a line count cannot say when the whole text is in for a message logged
  once per key; when `-until` never holds the last line is still returned,
  so the check shows what the window said — undo: remove it and use
  `$filters + 4` as `$before`.
- The crumb check's regex is `\.git|alpha|beta` — `.git` sorts first in
  `shell16` — undo: `alpha|beta`.

Left as found: `SelectionText` still reads the pane right after a fixed
sleep, so "Enter opened the row in the pane" and "the pane is back in
alpha" could be late in the same way; and the earlier "Quick Open showed:
False" has no explanation yet, since the line was in the log and the file
is unbuffered. If it fails again with the fixture fixed, that read is the
place to look.

## The live check after the fixes (15:42, `run-2026-09-30-1542.txt`)

Main at 4b102b6, the release core of 15:37 and the Release window the
release script built at 15:42 (Phase 17 inside): exit code 0, 127 checks
True, 0 False; section 16 all nineteen True, the new branch check
included ("the workspace pill shows the branch, live-16"); the scroll
goal met with no frame over 20 ms (worst 17.5 ms). Every section of the
old shell and the new one passed on one build, so Phase 16 is done. The
"Quick Open showed" check that had no explanation passed with the wait
in place.
