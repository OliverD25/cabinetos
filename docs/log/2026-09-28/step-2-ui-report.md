## Report: hardening step 2, edge cases in the shell

Step 2 report (the shell's edge cases): classes A to E are done, tested first, one commit each, all pushed to main. The help.about handler is proven by a test and the docs row is fixed. I also sent this report by SendMessage.

Context: the fourth task of tonight, "edge cases in the shell, one class at a time, tests first", plus your later note about help.about.

## Built

| Class | Tests added | What failed before | What changed |
|---|---|---|---|
| A, names beyond ASCII (ce7a142) | NamesBeyondAsciiTests (14) | An_exact_name_is_found_before_one_that_differs_only_by_case; Properties_names_the_type_the_type_column_shows. The snapshots also showed `مستند.txt` as `txt.مستند`. | ListingView.IndexOfName finds the exact name first. DisplayFormat.RenameStem handles the stem. PropertiesText now shows the Type column's name. An implicit TextBlock style sets TextReadingOrder=UseFlowDirection. |
| B, long paths (70a74bd) | LongPathTests (11), ToolTests (3) | 8 of the 11 LongPathTests. The ToolTests failed because WebView2's refusal escaped as an exception ("UI command handler failed"), and a 272-character path was still offered to the page. | CrumbFit keeps the drive, a "…" whose menu lists the hidden folders, and the last crumbs that fit. It fits again on resize, and a name too wide for the bar ends in its own "…". DisplayFormat.ShortPath shortens the pane header's path. TransferText.ShortSubtitle shortens the flyout's line ("C:\…\deep file.txt → C:\…\Ґанок"). Crumbs understand the `\\?\` and `\\?\UNC\` forms. ToolFileSession.LongestPath = 259: a longer path is not offered to a tool, and the status bar says why. WebViewHost.StartAsync now closes cleanly and rethrows when a folder is refused. |
| help.about (1d57689) | CommandRouterTests (1), EndToEndTests (1, against the real core) | Nothing: the handler already existed. These tests confirm it. | docs/ui.md: the command row and the About section now say help.about is the window's command. |
| C, links and cloud files (9a53a7c) | LinksAndCloudFilesTests (22). 2 DisplayFormatTests cases now use the new link words. | 18 of the 22 | Links get a chain badge on the icon, and the badge stays after the shell icon arrives. DisplayFormat.RowType names the link in the Type column and in Properties. EntryFacts.IsNotOnDisk reads the attributes RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN and OFFLINE; such rows show a cloud mark. DisplayFormat.IconKeyFor never asks for a `path:` icon for such a row (it asks `ext:.exe` instead). DeleteText: "Delete the link permanently?", and it counts the links among several rows. |
| D, two windows (51a5f29) | TwoWindowsTests (2), DiagnosticsTests (1) | Two log writers on one file kept only 3,000 of 6,000 lines, because each wrote over the other. The WindowArgs test failed on its stub. | LogWriter opens the file with append-only rights (FILE_APPEND_DATA) and writes one batch per call. `window.new` starts a new window with `--path` set to the active folder; the new window does not inherit the snapshot aid's variables. A window that follows a setting written by another window logs it. |
| B follow-up: the core's path_too_long (9ef5923) | ConflictTests (1) | The card said "the path is too long for the destination" and offered no Delete permanently. | For a delete (no destination), the card says "has a path too long for the Recycle Bin" and offers Delete permanently, Skip and Retry. |
| E, the fixture in the live checks (f5903c0, 3d52d3a) | none (scripts) | The first scripted run found that Enter on the 339-character .txt opened Notepad; the core answered ok. | New ui/livecheck/edge-snapshots.ps1: a snapshot-aid run over the shared fixture, then disk and log checks. livecheck.ps1 gets an "Edge cases" real-key section (ASCII only; the Cyrillic is built from code points). The window logs "notice shown". ui/livecheck/edge-fixture.ps1 is deleted in favour of sdk/fixtures/edge-fixture.ps1. |

Files changed (all paths under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\):
- ui\CabinetOS.Core\Presentation: DisplayFormat.cs, CrumbFit.cs (new), PropertiesText.cs, DeleteText.cs (new).
- ui\CabinetOS.Core\Listing\EntryFacts.cs (new); ui\CabinetOS.Core\Platform\WindowArgs.cs (new).
- ui\CabinetOS.Core: Jobs\TransferText.cs, Jobs\ConflictText.cs, Tools\ToolFileSession.cs, Diagnostics\LogWriter.cs.
- ui\CabinetOS: MainWindow.xaml.cs, MainWindow.Tools.cs, App.xaml.cs.
- ui\CabinetOS\Services: WebViewHost.cs, ToolHost.cs, DevSnapshots.cs.
- ui\CabinetOS\Views: FilePane.xaml.cs, FileRow.xaml, FileRow.xaml.cs, TransferFlyout.xaml.cs.
- ui\CabinetOS.Tests: LongPathTests.cs, LinksAndCloudFilesTests.cs and TwoWindowsTests.cs (new); ToolTests.cs, CommandRouterTests.cs, EndToEndTests.cs, DiagnosticsTests.cs, ConflictTests.cs, DisplayFormatTests.cs.
- ui\livecheck: edge-snapshots.ps1 (new), livecheck.ps1; edge-fixture.ps1 deleted.
- docs\ui.md; docs\log\2026-09-28\edge-*.png (6 snapshots).

## Commits
ce7a142, 70a74bd, 1d57689, 9a53a7c, 51a5f29, 9ef5923, f5903c0, 3d52d3a. All pushed with `git pull --rebase origin main && git push origin HEAD:main`.

## Checks
- UI tests: 485 in total. `dotnet test` gives 484 passed and 1 skipped. The skipped one is the two-window end-to-end test, which opens windows on the desktop and runs only with CABINETOS_UI_E2E=1. With CABINETOS_UI_E2E=1, all 485 passed (41 s).
- `dotnet build CabinetOS.sln -warnaserror`, Debug and Release: 0 warnings, 0 errors.
- I rebuilt the core (debug and release) in core/target so it includes 2c80d5f and the core's class B commits. I edited no core sources.
- Both .ps1 scripts parse in PowerShell 7.6 and in Windows PowerShell 5.1.
- No CI claim.

## Live check
- edge-snapshots.ps1 (release builds, Windows PowerShell 5.1) answered yes to every check:
  - the Cyrillic rename happened on disk;
  - the search "звіт" returned 2 hits;
  - the pane listed the 325-character folder;
  - Enter on deep notes.md showed the preview's reason in the status bar;
  - Delete of the long file stopped at the Recycle Bin conflict, and Skip left the file;
  - Shift+Delete asked "Delete the link permanently?";
  - the junction is gone and kept 1.txt to kept 3.txt are still there;
  - every log line was whole.
- help.about, with the current core: "about" in the palette finds the row. Run through the router, it logs target ui, "about shown" and "dialog shown". The core's log has no line for it.
- window.new, from a pane at names\Ґанок: the new window opened with its left pane there and a core of its own. I closed it afterwards.
- Two windows by hand: both terminals ran in one WebView2 browser process (the same browser_pid).
- By mistake, the first edge run pressed Enter on deep file.txt and Notepad opened on the desktop. I closed it; nothing was changed. Neither script presses that key now.
- The real-key section of livecheck.ps1 was not run: that run is the planning session's, on a watched screen.
- Snapshots: docs/log/2026-09-28/edge-*.png.

## Decided
- A tool is not offered a file whose path has more than 259 characters — because WebView2 showed a 255-character path, failed to fetch a 272-character one, and refused a 333-character folder, even with long paths turned on in Windows — undo: remove the length check in ToolFileSession.OpenAsync.
- I tried the `\\?\` form for the folder mapping and reverted it — because WebView2 accepted that mapping and then failed every fetch, which is a worse message — undo: nothing to undo.
- The pane header's path also gets the middle "…". It was not on your list — because it lost the same meaningful end — undo: remove FitPathText in FilePane.
- The flyout's line budget is 52 characters, with the whole line in the tooltip — because that is what fits beside "N more" at 11 px — undo: change the number in TransferFlyout.
- The snapshot aid now draws open menus and can press menu items — because the "…" menu needed a snapshot — undo: revert those lines in DevSnapshots and MainWindow.
- Link words: "Link to a folder" and "Link to a file" while the kind is unknown; "Junction", "Mount point", "Symbolic link to a folder/file" once the listing names it. They replace "Folder link" and "Link" — because the shell's own name ("File folder") hid the link — undo: DisplayFormat.LinkTypeName.
- "Not on this disk" is read from the attributes the listing already carries — because the core's planned flag would say the same thing — undo: EntryFacts.IsNotOnDisk.
- window.new runs through a window handler before the core lists it, opens at the active folder, and does not pass the snapshot aid's variables on — because otherwise a snapshot run would start windows without end — undo: remove the handler line and OpenNewWindow.
- The two-window end-to-end test is opt-in (CABINETOS_UI_E2E=1) — because it opens two windows for about 30 s on every run — undo: remove its Skip.
- The WebView2 data folder stays shared between windows — because WebView2 supports sharing, closing one window leaves the other's page running, and a folder per window costs disk space — undo: make WebViewHost.DataFolder per process.
- The fixture scripts use Shift+Delete on the junction, not Delete — because that is the dangerous case, and it leaves nothing in the Recycle Bin — undo: change the step.
- The fixture lives outside the run folders — because Windows PowerShell's Remove-Item may follow a junction; only the fixture script (rmdir) removes it — undo: nothing to undo.

## Needs the core
1. A registry row for `window.new`: target ui, "Window: New Window", and a default key. Ctrl+N is free in the defaults and is Explorer's key. Until then there is no palette row and no key.
2. The link kind in the listing (junction, symbolic link to a file or a folder, mount point). The shell reads it in one place, EntryFacts.LinkOf; please tell me the field name.
3. The "not on this disk" flag's name, if it will differ from the attributes.
4. open_path of a long path: on this PC ShellExecute opened a 339-character .txt in Notepad and the core answered ok. So invalid_path appears only where the program refuses the path. ipc.md says "the shell refuses such a path"; "may refuse" would be more accurate.
5. Please confirm that the core's log writer appends atomically when two cores share one log folder.

## Needs the user
None tonight. Symbolic links and mount points need an administrator or Developer Mode, so they are tested only on fake listings.

## Known gaps
- Tools cannot show files with paths over 259 characters; the user gets only the message.
- A link's kind reads "Link to a folder/file" until the core names it.
- Cloud placeholders are not checked live, because there is no sync provider here.
- Both windows' terminals share one browser process, so a crash stops both. Each window brings its own back with Reload.
- window.new has no palette row and no key yet.
- The tooltips that hold long paths are not in the snapshots: the snapshot aid opens only ToolTip objects.

## Noticed out of scope
- Search hits carry no attributes, so a link or a placeholder among the hits shows no badge and no cloud.
- The crumbs' "…" button cannot be reached with the keyboard; Ctrl+L (the address box) is the keyboard route.
- Not started, and waiting for SendMessage in this order: step 3 (scrolling, not received yet), then 11a (received), then the Commander Compact shell part, then the two theme items (CbOnAccentBrush contrast and CABINETOS_WEBVIEW2_DIR).

## Ready text for docs/PLAN.md (under Phase 10)
**Hardening, step 2: edge cases in the shell, 2026-09-29.** The shell went through five classes of edge cases on a shared fixture (`sdk/fixtures/edge-fixture.ps1`), tests first, one commit per class. Names beyond ASCII: a name is found exactly before it is found in another case (a case-sensitive folder holds `Report.txt` and `report.txt`), right-to-left names keep their extension on the right, F2 selects the stem without splitting a surrogate pair, and Properties names the type the Type column shows. Long paths: the crumbs keep the drive, a "…" with a menu of the folders left out, and the last folders that fit; the pane header's path and the transfer flyout's line keep the drive and the last names; a file whose path has more than 259 characters is not offered to a tool, since WebView2 cannot read it, and the status bar says why; a delete to the Recycle Bin that the core stops at a long path offers Delete permanently. Links and cloud files: a link keeps a badge on its icon and names its kind in the Type column and Properties ("Link to a folder" until the listing names junctions, symbolic links and mount points), Shift+Delete on a link says that only the link goes, and a row whose data is not on this disk shows a cloud and is never read to draw its icon. Two windows: `window.new` starts another window with its own core at the active folder; a setting one window writes reaches the other through `config_changed`, which follows and writes nothing back; closing one window leaves the other; and the log writer now appends atomically (two windows had kept 3,000 of 6,000 lines). The fixture runs through the snapshot aid in `ui/livecheck/edge-snapshots.ps1` (every check passed; the files behind a deleted junction stayed) and with real keys in `ui/livecheck/livecheck.ps1`. 485 UI tests, one of them (two real windows) opt-in with `CABINETOS_UI_E2E=1`. Snapshots in [log/2026-09-28/](log/2026-09-28/).

## Ready text for the README
UI tests: 485 (one of them, two real windows, runs with `CABINETOS_UI_E2E=1`).

Recap: every class of the edge-case task is built, tested and pushed, and help.about is confirmed. Left for the planning session: the real-key run of livecheck.ps1's edge section. Left for the core: the registry row for window.new and the field for the link kind.
