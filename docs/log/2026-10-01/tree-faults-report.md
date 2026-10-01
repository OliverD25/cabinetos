# Two faults of the folder tree (2026-10-01, evening)

Context: step 1 of 3. The live check on the creator's Omen laptop saw two
faults in the Explorer's folder tree (the sidebar's tree of folders). They are
in [live-check-portability-report.md](live-check-portability-report.md),
section "Seen on the way, not changed". The planning session asked for both to
be fixed, with thorough tests. Written by the Sonnet agent in the worktree
branch `worktree-agent-ad22025279127d09d`, on the main PC. Nothing here opened
a window on this PC: every window test ran on the laptop.

## Summary

| Fault | Cause | Change |
|---|---|---|
| 1. Enter or a click in the tree ran `go.toPath` twice | The window listened to the tree's pick twice: on the sidebar's `Navigate` event (the sidebar re-raises the tree's event) and on the tree's own event in `SetUpRail` | The second subscription is removed. The event's shape is not changed |
| 2. With `panes.showHidden` off the tree stopped at the nearest visible folder | The core leaves hidden folders out of the tree's reads, so the tree could not open a folder under a hidden one | The tree reads a folder again with the hidden entries when the active pane's path goes through a folder the first read did not list, and shows only that folder, as a dim row |

Commits on the branch, oldest first. The tests come before the fixes on
purpose: the laptop's "before" run needs the tests without the fixes.

| Commit | Subject |
|---|---|
| 41f2069 | test: a window test counts go.toPath runs for each way of picking a folder in the sidebar |
| 55bd832 | test: a window test for the tree following the pane under a hidden folder, and two test aids |
| ca0edc2 | fix: Enter or a click in the folder tree runs go.toPath once, not twice |
| 18f1905 | fix: the folder tree follows the active pane into a folder under a hidden one |
| 68f4628 | docs: ui.md says the tree opens hidden folders on the pane's path and a pick runs go.toPath once |
| (this report) | docs: report on the two faults of the folder tree |

## Fault 1: `go.toPath` twice

**Cause.** `Sidebar.xaml.cs` line 24 re-raises the tree's `Navigate` event as
the sidebar's own (`TreeSection.Navigate += path => Navigate?.Invoke(path)`).
`MainWindow.xaml.cs` line 148 subscribes to the sidebar's event, and
`MainWindow.Rail.cs` line 54 subscribed to the tree's event too. One pick in
the tree reached the window twice. The pinned folders and the drives raise only
the sidebar's event, so they ran the command once.

**Change.** The line in `SetUpRail` is gone, with a comment that says why the
tree is not subscribed there. The XML comment of `Sidebar.Navigate` now says it
is the only event the window listens to for a pick.

**Test.** `RailEndToEndTests.Every_way_of_picking_a_folder_in_the_sidebar_runs_go_toPath_once`.
It starts a window in the rail layout and picks a folder four ways: the system
drive's row, a pinned folder, a click on a tree row (`click:`), and Enter in the
tree (`cmd:view.showExplorer`, `key:Down`, `key:Enter`). A `rail-state` line
before each pick lets the test count the window's `command executed` lines for
`go.toPath` with the trigger `sidebar` between two lines. It also checks that
each pick took the pane where it was meant to go (the `listing shown` lines).
The run shows hidden entries, so the test does not depend on fault 2.

Before the fix, on the laptop (commit 55bd832, `tests-2026-10-01-2137`):

- The test failed at the click: 2 runs where 1 was expected.
- A second run with the log dumped (a throwaway branch, deleted) gave the whole
  picture: drive 1 run, pinned folder 1 run, click in the tree 2 runs (both at
  18:57:09.598), Enter in the tree 2 runs (18:57:10.475 and .476, 1 ms apart,
  as in the live check). So a click ran twice too, as the planning session said.

After the fix: 1, 1, 1, 1, in each of three laptop runs.

## Fault 2: the tree stops at the nearest visible folder

**Cause.** `CoreFolderSource` asks the core with `panes.showHidden` as it is.
With it off the core leaves hidden folders out. On a stock Windows `AppData` is
hidden, so the tree could not open anything under `%TEMP%` or `%APPDATA%`.
`RevealAsync` then marked the deepest folder it had (`C:\Users\Omen`), and said
nothing. On this PC `AppData` is not hidden, so the fault never showed here.

**Change.**

- `IFolderSource` has a second read, `ListIncludingHiddenAsync`. The window's
  `CoreFolderSource` sends the same `list_directory` request with
  `IncludeHidden` on. The core is not changed.
- `FolderTreeModel` remembers the path of the last reveal. When a read of a
  folder does not list the folder that the path goes through, it reads that
  folder once more with the hidden entries, keeps only the folder of the path,
  and adds it as a node with `IsHidden`, at the place the full listing has it.
  Every other hidden folder stays out. With the setting on, the first read has
  the folder, nothing is read twice and nothing is marked.
- The same step keeps the path's folders when the setting is switched off while
  they are open: the next read of their parent leaves them out, the second read
  finds them, and the open rows stay.
- A hidden row goes, with the rows under it, when the pane leaves its path. The
  next reveal drops it. A read of the parent drops it too. The mark and the
  cursor of a dropped row move as for any folder that is gone. A flag
  (`_mayHaveHidden`) keeps this free for a tree with no hidden row.
- `FolderTreeView` draws a hidden node's icon and name at 0.55 opacity
  (`NameOpacityOf`, bound in `FolderTreeView.xaml`).
- Test aids for the window tests: `FolderNode.IsHidden`, the snapshot steps
  `until:tree` (the tree marks the active pane's folder) and
  `tree-state:<label>|<folder>` (one log line about one folder of the tree), and
  `FolderTreeView.NameOpacity`.

**Failure cases.** A hidden folder that is not on the disk, a hidden read that
throws, and a hidden read that the source refuses all leave the nearest
existing folder marked, with no row and no error on it. A newer reveal stops an
older one from adding a hidden folder for a path the pane has left. Closing a
row cancels its hidden read too.

**Unit tests** (`SidebarTests.cs`, class `FolderTreeTests`, 23 tests before and
46 now). The fake source has hidden folders, a held and failing and refusing
second read, and its own request list. The 23 new tests cover: a path under a
hidden folder shows that folder, dim, and marks the folder of the pane; the
count of requests; a hidden sibling and a hidden sub-folder stay out; a path in
another case; a hidden folder as the pane's folder; leaving the path drops the
folder, its rows, the mark and the cursor; coming back; reading the parent again
while inside; the setting on; the setting switched off with the path open (the
folders stay and the row is marked, the same node and its rows); the setting
switched on again; two hidden folders in a row; two hidden folders side by side;
the drive root itself; a path with no hidden folder (no second read); a missing
hidden folder; a deleted one; one made after the last read; a failing read; a
refused read; a newer reveal; closing a row; a path on no drive; moving inside a
hidden folder.

One existing test changed: `A_folder_that_is_gone_after_a_new_read_loses_its_row_and_takes_the_marks_with_it`.
Its fake source now says the folder is gone for both kinds of read, since the
second read would otherwise find the folder and keep it as a hidden one. The
product behaviour for a folder that is really gone is the same as before.

With a part of the fix off (each run on this PC, then put back):

- the hidden read off: 16 of the 46 tree tests fail, the 23 old ones pass;
- the drop of hidden rows off: 6 of the 46 fail (the rest drop the row through
  the read of the parent, which is the second way it goes).

**Window test.** `RailEndToEndTests.The_tree_follows_the_pane_into_a_folder_under_a_hidden_one_and_shows_no_other_hidden_folder`.
It makes its own hidden folder (the hidden attribute) with a folder two levels
down and a hidden sibling, so this PC meets the fault too. `panes.showHidden`
is off. The pane goes into the deep folder, and the test reads the tree's
`tree state` lines: the tree marked the deep folder, the hidden folder has a row
that is hidden and drawn below full opacity, the hidden sibling has none. Then
the pane goes to `data`, and the hidden folder has no row.

Before the fix, on the laptop: this test failed (the root folder of the run had
no row), and so did the three older rail tests that start a window in a folder
under `%TEMP%` (`Each_layout_shows_the_rail_and_the_tree_or_leaves_them_out`
with `rail`, `The_tree_has_rows_on_the_screen_...` and
`The_rail_switches_views_...`): each expected `...\cabinetos-ui-test` and got
`C:\Users\Omen`. These are the symptom the live check saw, in the test suite.
After the fix all of them pass.

## Tests and runs

Fast run on this PC (no window; `dotnet build CabinetOS.sln -warnaserror`, 0
warnings, then `dotnet test --solution CabinetOS.sln --no-build` without
`CABINETOS_UI_E2E`): **1219 tests, 1159 passed, 60 skipped, 0 failed**. The
skipped ones are the end-to-end tests, which need a window and the core. The
run before this work had 1194 (23 new tree tests and 2 new end-to-end tests
make 25).

The laptop, `remote-tests.ps1 -Branch worktree-agent-ad22025279127d09d -Filter
"FullyQualifiedName~RailEndToEnd|FullyQualifiedName~Sidebar" -EndToEnd`. The
filter matches 35 tests (the rail end-to-end tests and `SidebarSettingsTests`).
It does not match the class `FolderTreeTests`, whose 46 tests run in the fast
run above. Output files are in `_io\test-runs\`.

| Run | Commit on the laptop | Result |
|---|---|---|
| before | 55bd832 (tests, no fixes) | 35 tests, 5 failed: the two new tests and three older rail tests (`tests-2026-10-01-2137-rd-omen-laptop.txt`) |
| after 1 | 18f1905 | 35 of 35 passed, 1 min 4 s (`...-2150-...`) |
| after 2 | 68f4628 | 35 of 35 passed, 1 min 3 s (`...-2152-...`) |
| after 3 | 68f4628 | 35 of 35 passed, 1 min 2 s (`...-2154-...`) |
| click log | a throwaway branch on 55bd832 | the dump described under fault 1 (`...-2156-...`) |

Runs 2 and 3 are on the docs commit, which changes no code.

## Decided without you

Each line: what, because, undo.

1. **Fault 1: the duplicate subscription in `SetUpRail` is removed, the
   event's shape is not changed.** Because the shape was not the fault, and
   the sidebar's one event already serves all four ways of picking a folder.
   Undo: revert ca0edc2 (the fault returns).
2. **Fault 2: the planning session's decision, as asked.** The tree shows the
   folders on the active pane's own path even when hidden, dim; every other
   hidden folder stays out. Because a user who is inside a folder must see where
   they are (Article 4, Progressive Disclosure). Undo: revert 18f1905 (the tree
   again stops at the nearest visible folder). The tests then need the same
   revert.
3. **The dim look is a new one: 0.55 opacity of the icon and the name.** The
   task said to use the look the panes give hidden entries. The panes have none:
   I searched the code (`IsHidden`, `Hidden`, the hidden attribute in
   `FileRow` and `ColumnRow`, the opacity in every view) and the design notes.
   A pane draws a hidden file as any other. I chose the look Windows Explorer
   uses (about half opacity) and one constant. Undo: change `HiddenOpacity` in
   `FolderTreeView.xaml.cs` (use 1 for no dimming).
4. **A hidden row goes at the next reveal, not only when its parent is read
   again.** The task said "when the pane leaves that path and the tree is
   refreshed". Waiting for a refresh would leave a dim branch the pane is not in
   until the user closes and opens its parent, which breaks "every other hidden
   folder stays out". Both ways are in, and both are tested. Undo: remove the
   `DropHiddenOffPath()` call in `RevealAsync`; the read of the parent still
   drops the row.
5. **The source's new method reads a whole folder with the hidden entries and
   the model keeps one folder; the source does not get "one named folder".** The
   core's `list_directory` already has `IncludeHidden`, so the core is not
   changed. It costs one more request, only when a read lacks the folder of the
   path. A path that is not on the disk costs one more read for each time its
   parent is read (twice in a reveal). Undo: part of revert 18f1905.
6. **With the setting on, a hidden folder is an ordinary row, not dim.** The
   tree does not know the setting, and the task said nothing changes then.
   Dimming it would need the source to say which names are hidden.
7. **The window test of fault 1 turns `panes.showHidden` on.** So that the
   test fails only for fault 1, even where AppData is hidden. Because of the
   same wish the drive and the pinned folder come first (their rows are drawn
   only near the top of the sidebar, and the tree scrolls away from them), and
   the test uses the system drive and a folder in the test's own temp folder.
8. **One existing unit test changed**, as described under fault 2.
9. **Test aids in the window** (`until:tree`, `tree-state`, `NameOpacity`,
   `IsHidden`) are product code that only tests use, as `rail-state` is. The
   fault 2 test commit (55bd832) adds `IsHidden` before anything sets it, so
   the test is red for the right reason on the laptop.
10. **`docs/ui.md`**: three sentences for the two faults and the two steps, and
    the live check's note on AppData now says the tree no longer needs
    `panes.showHidden`. The live check script itself is not changed.
11. **A throwaway branch `diag-click`** (a log dump of the pick test on the
    pre-fix code) was made here, run once on the laptop and deleted. It was
    never pushed or merged.

## Needs you

- Whether the hidden folder should be dim, and how much (decision 3). It is one
  constant.
- `docs/PLAN.md` still lists both faults as open (the last lines of the
  paragraph on the live check, section 5). I did not edit it, as the task did
  not ask; the card "Development Plan (mirror of the repo plan)" on the desk
  follows the file.
- Whether the live check's section 13 should drop its `panes.showHidden`
  switch. It still works. It no longer has to be there for the tree.
- Whether the panes should dim hidden entries too (they do not today). Then the
  tree and the panes would look the same.

## Seen on the way, not changed

- In this Git Bash `sed -i` removed the carriage returns from a `.cs` file
  once. `.cs`, `.xaml` and `.ps1` files are CRLF in the working tree
  (`.gitattributes`), `.md` files are LF. I did all later edits with a small
  Python helper that keeps each file's line endings, and checked each file.
- The `click:` step presses the first shown button with a name. A drive's
  button in DRIVES and the drive's row in the tree have the same name; DRIVES
  is drawn first, so it wins. The pick test relies on that, and says so in a
  comment.
- The remote test filter in the handout (`RailEndToEnd|Sidebar`) does not
  include the tree's unit tests. They are in the fast run.
