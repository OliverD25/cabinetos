## the rail's tree at start

Agent: ac9d2107a90527bf1

Report: the rail's tree at start

Fixed and pushed to main (ca72efc). Started in the rail layout, the tree under FOLDERS now shows its rows with no key pressed.

Cause: your guess was right, with one detail. About 200 ms after the start, the reveal scrolled the tree (FolderTreeView.ScrollTo: GetOrCreateElement, then StartBringIntoView) before the list was laid out. A row that has only been asked for has no place yet, so the scroll went nowhere. The list then drew its 92 rows around the folder, thousands of pixels below the sidebar's window. The log lines all looked fine (4,488 rows in the model, "scrolled"). Ctrl+Shift+E worked because the list was laid out by then. I reproduced it with no keys: 4 of 4 real starts with the old code showed an empty tree.

Fix (ui/CabinetOS/Views/FolderTreeView.xaml.cs):
- The row is laid out (TreeList.UpdateLayout) before it is brought into view, which is the documented order for a repeater.
- If the scroll is asked for before the tree is shown and laid out in the sidebar's window, it waits for the next layout.
- MainWindow.Rail.cs logs "the tree drew rows" 600 ms after the scroll: rows in the model, rows drawn, rows inside the sidebar's window (visible). The rail-state line also gets tree_realized and tree_rows_visible.
- Cost: a scroll on a 4,300-row tree takes about 35 ms, against 2 ms before.

Test: The_tree_has_rows_on_the_screen_when_the_window_starts_in_the_rail_layout_before_any_key (ui/CabinetOS.Tests/RailEndToEndTests.cs). It starts the window the way a user does, with no snapshot aid, and puts the active folder under 80 sibling folders. The snapshot aid waits until the window is ready and never showed the fault, so it could not be used. With the old scroll the test fails with "the tree has its rows in the model and none on the screen"; with the fix it passed 3 of 3 runs.

Results:
- Six real starts after the fix showed 31 rows in view every time.
- UI tests: 849 of 849 pass, with the end-to-end tests on the real core (47 s). Debug and Release builds: 0 errors. README count updated to 849.
- Live check, whole script, real keys (run-tree-1.txt in _io\live-check): 106 answers True, 0 False, exit code 0. Section 13 has a new line, "the tree has rows inside the sidebar's window, before Ctrl+Shift+E: True". The scroll goal is also met in that run ("met: yes; 302 frames in 5 s, 0 over 20 ms").
- I closed Notepad afterwards. No CabinetOS or core process is left.

One thing I got wrong on the way: I first named the new log field tree_visible, which collided with the existing boolean of that name (three layout tests failed on it). It is now tree_rows_visible. The docs (docs/ui.md, tree part of the rail section) now say why the scroll waits and name the test.
