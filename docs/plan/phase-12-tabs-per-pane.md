# Phase 12 — Tabs per pane

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

**Built in the sleep-mode run of 2026-09-29/30 (77c1169 the core's
`ui.tabs`; 725328f, 4881f82, 3fec822, 07b5e8b, 837361a, cf08875,
80c204b the shell).** Each pane has a row of tabs above its list: a WinUI
`TabView`, `tabRow` high (32 px, 26 px in Commander Compact), hidden while
the pane has one tab. A tab holds a folder with its own history, order,
cursor, marks and lock, or a Tool Extension (the Markdown Preview opens as
a tab beside the folder's). The pane's model stays the live state of the
tab in front; a tab behind parks its state and lists its folder again
when it comes back, so a hidden tab does not refresh itself. The front
tab of the active pane has a 2 px accent line, and nothing is dimmed.
`tab.new` (Ctrl+T), `tab.close` (Ctrl+W), `tab.next` and `tab.previous`
(Ctrl+Tab, Ctrl+Shift+Tab), `tab.toggleLock` (palette only),
`tab.openFolderInNewTab` (Ctrl+Up) and `tab.moveToOtherPane` (Ctrl+K
Ctrl+Right, Ctrl+K Ctrl+Left, the second key naming the side) are in the
registry under "Tab", 90 commands now. In a locked tab, going into a
folder or up opens a new tab and Back and Forward refuse; the last folder
tab of a pane cannot be closed; a tool tab has no lock and does not move,
and swapping the panes refuses while one is open, since a tool's process
belongs to its pane. The window saves `ui.tabs` (at most once a second,
and at close; `ui.lastPaths` is still written) and sends `window_state` on
every change of tabs, active pane, cursor or marks, joined to one message
per 50 ms with the marks capped at 1,000 paths. Tool tabs are not saved,
since the config's schema holds `{path, locked}` and a tool's file may be
gone. Measured on the 100,000-entry folder (`scroll-bench.ps1`, release
builds, display awake, 11, 14 and 11 runs): UI-thread work per second of
scrolling, middle value 406 ms before the tabs, 418 ms with the row
hidden and 402 ms with three tabs shown, within the 5 % limit, so
`TabView` stays. 26 new UI tests (676 in all): the tab model, the
`ui.tabs` and `window_state` shapes against the schemas, and an
end-to-end test that opens three tabs (the last locked) in the left pane
and two in the right one, restarts the window and finds them all. The
live check's section "12: tabs" passed with real keys (Ctrl+T twice,
Ctrl+Tab, the lock through the palette, Enter on a folder in the locked
tab opened a fourth tab, Ctrl+W hid the row again, the last tab refused
to close). With the snapshot aid: five tabs left and three right open
independently, and F5 copies from the front tab of one pane into the
front tab of the other. Known gaps: a row shown in only one pane pushes
that pane's list down by the row's height; the editor's own header
repeats a tool tab's name; no drag to reorder and no "+" button (the
keyboard is complete, Article 7). Guide: [ui.md](../ui.md), "Tabs".
