# ADR 0016: A pane's tab can show its folder as columns, each column one watched listing

- Status: accepted
- Date: 2026-10-01
- Decided by: the planning session, in the sleep-mode run of 2026-10-01,
  on item 2 of the creator's card "New features I want to build Files Type
  Sprint" ([PLAN.md](../PLAN.md), Phase 19f). The creator asked for the
  card's features to be built and slept; every choice below is the
  planning session's and can be superseded by the creator's word.

## Context

The card asks for the column layout of macOS Finder, as the Files app
offers it: "double-clicking a folder opens its contents in a new column
to the right; you can see your entire navigation path at a glance and
jump between parent and subfolders without hitting Back."

What decides the shape:

- Article 5: the dual pane is the primary paradigm. A column view must
  not replace it; it is a way one pane shows one folder.
- Article 10: the core ships file navigation only. Columns are
  navigation; a preview of the selected file in the last column, as
  Finder draws one, is a viewer, so it is an extension's job.
- The core lists a folder into a shared-memory section and watches it
  (`list_directory` with `watch`); the window holds one `ListingView`
  per open listing and releases it when the folder goes. Several columns
  are several listings, which the core already allows (two panes, tabs,
  the search results).
- A pane's tab holds a path, a lock, history, cursor, marks, scroll and
  a find text (Phases 12 and 16). A mode belongs to the tab, so a tab
  keeps it, and the saved tabs (`ui.tabs`) keep it across a restart
  (Article 6).
- Every action is a named command with a key (Article 7).

## Decision

1. **A mode of the tab.** A pane's folder tab has a mode, `files` (the
   list as today) or `columns`. `view.toggleColumns` (Ctrl+Alt+C, unless
   the key is taken) switches the active pane's front tab; the mode is
   saved with the tab in `ui.tabs` as `"mode": "columns"` (absent means
   `files`). A tool tab has no mode.
2. **What a column is.** A column is one folder's listing, name only
   (the icon and the name; a folder row shows a chevron at its right),
   `columnViewWidth` wide (a theme metric: 220 px default, 180 in
   Commander Compact). The first column is the tab's folder. Opening a
   folder row (Enter, Right, a click) adds a column to its right with
   that folder and drops any columns that were to the right of the one
   the row is in; the earlier columns stay. Left moves the keyboard to
   the parent column and keeps the child column on screen until another
   row opens. The deepest column's folder is the tab's path: the
   breadcrumb row, the title, the saved tab and the commands that take
   "the pane's folder" follow it. Backspace and Up in the first column
   list the parent as the new first column, as in the list mode.
3. **One listing per column, watched, released with the column.** Each
   column asks `list_directory` with `watch` and holds its `ListingView`;
   a column that leaves the screen releases its listing at once, so the
   core watches only what is shown. A refresh of one column touches that
   column only. The pane in `columns` mode counts its listings in the
   window's "shell state" log line, so a test can see them released.
4. **Cursor and marks per column; commands act on the deepest column
   with the keyboard.** Each column keeps its own cursor and marks. The
   keyboard is in one column at a time (the one last opened or moved
   to). The file commands (F5, F6, F7, F8, rename, Properties, the
   context menu, the marks) act on that column's rows, as they act on
   the pane's list today; `window_state` reports that column's folder
   and marks as the pane's. The other pane is untouched: copy and move
   to the other pane work as they do.
5. **Sideways scroll, newest in view.** The columns sit in a horizontal
   scroller with no wrapping; opening a column scrolls it into view;
   the mouse wheel over the columns scrolls sideways.
6. **No preview column.** The last column holds a folder or nothing.
   A viewer of the selected file is an extension (Article 10). A Tool
   Extension may later ask for the selected file, as it does today.
7. **Find in pane, quick search and the sort** apply to the column that
   has the keyboard. Column widths are not draggable in this version;
   the theme's metric decides them.

## Consequences

- The `TabEntry` of the config gains `mode`; the schema is regenerated;
  a core that does not know the field is not a concern, since the window
  and the core ship together.
- The window gets a column view beside `FilePane`'s list, reusing the
  row's name cell, the selection model, the keys and the context menu
  of the list, so a fault fixed in one is fixed in both. The `FilePane`
  hosts either the list or the columns for its front tab.
- The core changes only its config model. Listing count, watch and
  release follow the rules the tabs already use.
- The theme metric `columnViewWidth` joins the metrics table and the
  schema; Commander Compact sets 180.
- Easier: seeing a deep path at once and moving up and down it without
  Back; a tree-like view without a tree control. Harder: a column view
  in a narrow pane shows two columns at most, and the dual pane halves
  the width; the mode is for the user who wants it (Article 4).
- Later, if wanted: draggable column widths (the column-widths work of
  Phase 19 could extend), a preview column as an extension, and a
  "columns" layout that spans the whole window.
