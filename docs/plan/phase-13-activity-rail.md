# Phase 13 — The activity rail and the modular sidebar

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

**Built and verified 2026-09-30 in the sleep-mode run (af0a104 the
config fields; 68ee1f0, 84c6f76, 4cd925e, a42c343, fd81d8b, 15d9c29,
1a05a5c the shell).** `ui.layout: rail` (the default stays `classic`)
shows a 44 px activity rail: 36 px buttons, a 3 px accent pill on the
view in use, buttons Explorer, Search, Marketplace, Terminal and one for
each tool with `"sidebar": true` in its manifest, in the order saved in
`ui.rail` (Shift+Up and Shift+Down on a button move it; an empty list is
the default order). A second press on the active view's button closes the
sidebar. The sidebar holds its views: the Explorer (Pinned, Drives and a
lazy folder tree over `list_directory`, one request for each folder
opened and cancelled when it closes; it follows the active pane while it
shows unless locked, `ui.sidebarAutoReveal`), the Search view (the field,
its whole-volume box and the hits, over the same model as the command
bar's box) and the pages of tools with a sidebar page (a WebView2 each;
the page hidden last stays awake, the others are suspended).
`view.showExplorer` (Ctrl+Shift+E), `view.showSearch` (Ctrl+Shift+F),
`sidebar.locate` (Alt+Shift+L), `sidebar.lock`, and `view.toggleSidebar`
still on Ctrl+B. The divider drags under 150 px to close and remembers
the last width (`ui.sidebarWidth`, and `ui.sidebarView` the view shown
last); badges come from the plugin event `badge`. What real keys found
and the same night fixed: a tool page in the sidebar now hands back the
view keys, so the mouse is never the only way out (Article 7); closing
the sidebar, and Esc in the rail, the tree or the search field, give the
keyboard to the pane; the live check sends the cursor block as extended
keys, since Windows drops Shift from a keypad Down with Num Lock on. Also
in this item: a tool page may run the commands of a plugin it follows (the
command's source is that plugin and its id starts with the plugin's), a
general rule with no plugin name in the window, so the Agent's chat page
works; and the live check's "ask" step runs against a marketplace index
the run builds itself, so nothing in the run reads the network. 839 UI
tests (the end-to-end ones on the real core in 48 s); the classic and
right layouts differ from snapshots taken before the phase by 19 and 11
pixels of 2.5 million (two runs of one build differ by 6). The live
check's section "13: rail" passed with real keys and the mouse in a
whole-script run: 105 answers True, none False, with the tabs and the
Agent's drag and ask in the same run. Open for the creator: Ctrl+Shift+E
inside the search field needs Esc first, since a text box keeps every key
but the immutable tier's (a change would put `view.showExplorer` in that
tier); Enter in the tree goes to the folder and hands the keyboard to the
pane; whether the default layout becomes `rail`; and a UI Automation walk
over a tree of thousands of open rows stalls the window for seconds, so a
screen reader on a huge tree is a risk worth a later look. One fault
found the same day by the real-key check of the heavy-logging window and
fixed (ca72efc): started in the rail layout, the tree was empty until a
key opened it, because the reveal scrolled to a row before the list was
laid out and the rows were drawn far below the sidebar's window; the row
is laid out first now, or the scroll waits for the next layout, and a
test starts the window the way a user does and finds the rows on screen
before any key. 849 UI tests. The whole live check then ran with real
keys to the end with exit code 0: 106 answers True, none False, and the
scroll goal met in that run. Guide: [ui.md](../ui.md), "The activity rail
and the sidebar".
