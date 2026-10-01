# CabinetOS · Shell redesign: single-row top bar, per-pane tabs, pane toolbar, scoped find

## Changelog
- v2 · 2026-10-01 · quiet top bar (menu · app name · tools), workspace switcher in sidebar header, redesigned tabs, pane toolbar row + path row, filter label
- v1 · 2026-10-01 · single-row top bar with workspace pill and centered command center, per-pane tabs, breadcrumb row with nav, Find-in-pane, Quick Open


Applies to every theme. Themes still own metrics and colors; this document changes the shell's structure. Metrics below are given for the default theme with the Commander Compact value in brackets where it differs. Reference build: `CabinetOS Compact.dc.html` (compact metrics).

## Summary of changes

1. Title bar and command bar merge into one quiet top row: menu, app name, tools.
2. Global workspace tabs are removed. The workspace switcher becomes the sidebar header (dropdown).
3. Each pane gets its own tab strip; every tab holds an independent path, selection and history.
4. The global address bar and nav buttons are removed. Each pane gets a toolbar row (nav, drive, free space, find, open-with) and a separate full-width path row below it.
5. Search stops being a global field. Find is a per-pane widget invoked on demand (Ctrl+F). Quick Open (Ctrl+P) is an overlay for workspace-wide file search and commands.
6. A hamburger menu and a Settings button are added.

Nothing changes in the command set, plugin model, terminal, marketplace, editors or transfer flyout except where noted.

## 1 · Top row

Height 40 px [32]. Doubles as the window drag region except over interactive controls. Fill: theme "bar" color; 1 px bottom hairline. Nothing in this row is pane- or path-specific; it stays calm so the pane tabs beneath read as the primary navigation.

Left → right:
- Hamburger (36 px [26]). Opens a dropdown of common commands with shortcut hints: New Tab, New Folder, Find in Pane, Go to Path…, Toggle Sidebar, Marketplace, Keyboard Shortcuts. Content comes from the command registry, so plugins may contribute.
- App icon, 16 px.
- App title: "CabinetOS · <active folder name>" (title 600 weight, folder at 60 % white). Updates with the active pane's tab.
- Flexible space (drag region).
- Quick Open hint chip: 24 px [22] tall, 1 px border white 8 %, search glyph + "Ctrl+P" in the mono font. Click or Ctrl+P opens the Quick Open overlay (§3). No persistent search field.
- Icon buttons, 36 px [26]: Dual/Single toggle, Terminal, Marketplace, Command Palette, Settings. Active state uses the accent color.
- 1 px divider, then the three 46 px [40] caption buttons.

The hamburger dropdown is an acrylic surface anchored below its trigger, 26 px rows, closes on outside click or Esc.

### Sidebar header (workspace switcher)

The sidebar's first row (28 px [26]) is the workspace control: colored dot, workspace name (600), current branch in mono 10 px, chevron. Fill: accent at 18 % over the sidebar; 1 px bottom hairline. Click opens a dropdown anchored to the row's full width: list of workspaces (dot, name, branch), divider, "Open folder as workspace…". Shortcut Ctrl+K W unchanged. When the sidebar is collapsed (Ctrl+B) the switcher is reachable via Ctrl+K W and the command palette.

## 2 · Pane structure

Every pane, top to bottom:
1. Tab strip, 36 px [28]
2. Toolbar row, 28 px [24]
3. Path row, 24 px [20]
4. Column header
5. File list (or an embedded editor when a tab is in editor mode)

The pane header row (folder name + path) from the previous shell is removed.

### Tab strip

- Recessed band: fill black 18 %, 1 px bottom hairline white 8 %, 4 px side padding, tabs bottom-aligned.
- Active tab: 32 px [26] tall card, radius 8 8 0 0 [6], fill white 9 % when the pane is active (5 % when inactive), top highlight inset 0 1 white 12 %, soft shadow 0 -1 4 black 20 %. No bottom edge: the toolbar row below shares the same fill so tab and toolbar read as one surface. Text white, 600. Folder glyph 14 px. Close × (8 px glyph in a 16 px hover target) shown on the active tab only when the pane has more than one tab.
- Inactive tabs: 26 px [22] tall, bottom margin 3 px, transparent, text white 60 %, folder glyph at 60 %, hover fill white 5 %. 1 px vertical dividers (white 10 %, inset 6 px) between inactive tabs; no divider adjacent to the active tab.
- Max tab width 160 px [170], name ellipsized; tooltip shows the full path. Middle-click closes any tab.
- "+" button (24 px, rounded 4) after the last tab creates a tab at the current path.
- Tabs overflow by horizontal scroll; no wrapping. Minimum one tab per pane; closing the last tab is a no-op.
- No accent line on the tab. The accent is reserved for the selected row and the active-pane border.

Tab state: `{ path, selection, history[], historyIndex, mode: files|md|hex, file, findQuery|null }`. Switching tabs restores all of it. Navigation within a tab rewrites its path and pushes history.

Shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab cycle tabs in the active pane, Ctrl+1…9 jump to tab n. Tab still switches the active pane.

### Toolbar row

Fill: same as the active tab (white 9 % active pane / 5 % inactive). No hairline between tab strip and toolbar. Buttons 22 × 20 px [22 × 20], radius 2, hover white 10 %.

Left → right:
- Back, Forward, Up. Forward disabled at 30 % when history has no forward entry. Alt+Left / Alt+Right / Backspace.
- 1 px divider.
- Drive chip: drive glyph + letter ("C:") + chevron, mono 11 px. Click opens a drive list; Alt+F1 / Alt+F2 cycle drives in the left / right pane as in Total Commander.
- Flexible space.
- Free space on the current drive, mono 11 px at 50 % white.
- 1 px divider.
- Find (toggles the find widget, accent when open), Open with… (existing editor picker).
- Plugins may contribute buttons after Open with…

### Path row

- Fill white 3 %, 1 px hairline above (white 6 %) and below (white 8 %). Mono 11 px. Padding 0 8 px.
- Breadcrumbs: one segment per path part, `›` separators at white 35 %. Last segment white, others white 70 %, hover fill white 10 %. Clicking a segment navigates the pane. Segments never shrink individually. Collapse rule: more than 5 parts in dual mode (8 in single) → `Drive › … › parent › current`; the `…` segment navigates to the folder it hides and shows the full path in its tooltip.
- Right side: filter label, white 35 %. `*.*` when no filter is active; `*query*` while Find is active. Clicking it focuses the find widget.
- Ctrl+L turns the row into an editable path field (Enter navigates, Esc cancels).

## 3 · Find and Quick Open

### Find in pane (Ctrl+F)

Scoped to the active pane's current tab. A floating widget, VS Code find style, drops from the toolbar row's right edge, overlapping the path row:
- Container: acrylic, 1 px border white 14 %, radius 0 0 4 4 [2], shadow 0 4 12 black 40 %.
- Contents: search glyph, text input (150 px, placeholder "Find in <folder>"), match count "n of m", close ×.
- Filters the list live by filename substring. Enter selects the first match and keeps the widget open; Esc closes and clears the filter. Invoked by Ctrl+F, the toolbar Find button, or the hamburger entry; no persistent search field remains.
- Selection state survives the filter; the empty-folder message is suppressed while a filter is active.

### Quick Open (Ctrl+P)

Workspace-wide overlay centered under the top row (same surface as the command palette); typing `>` switches to commands (Ctrl+Shift+P opens directly in command mode). Results: files and folders in the workspace with their parent path in the mono font; Enter opens in the active pane's current tab, Ctrl+Enter in the other pane.

## 4 · Removed and moved

Removed: global workspace tab row; separate command bar; global address bar; global search field; nav buttons in the command bar.

Moved: Back/Forward/Up → pane toolbar row. Drive switching → pane toolbar chip. Workspace switching → sidebar header dropdown. Open with… → pane toolbar. Search → Ctrl+F (pane) and Ctrl+P (workspace overlay). Command palette → keeps its icon and shortcut.

## 5 · Keyboard additions

| Shortcut | Command |
|---|---|
| Ctrl+P | Quick Open |
| Ctrl+F | Find in Pane |
| Ctrl+T | New Tab |
| Ctrl+W | Close Tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+1…9 | Tab n |
| Alt+Left / Alt+Right | Back / Forward |
| Alt+F1 / Alt+F2 | Drive list, left / right pane |
| Backspace | Up |
| Ctrl+, | Settings |

Existing bindings (Ctrl+Shift+P, Ctrl+Shift+D, Ctrl+`, Ctrl+Shift+X, Ctrl+B, Ctrl+L, Ctrl+K W, F-keys) are unchanged.

## 6 · Theme contract

Themes may set: top-row height and fill, tab strip / toolbar / path row heights, radii, hairline opacity, accent. Themes may not remove the tab strip, toolbar row, path row or sidebar workspace header, or change any keybinding.

## 7 · Acceptance

- Window at 924 px, dual pane: top row shows all controls without overlap; pane toolbar shows nav, drive chip, free space, Find and Open with… with no clipping.
- Path `C:\Users\dev\Projects\fileforge` renders in full in a 440 px pane; a 7-part path in dual mode collapses to `C: › … › parent › current` with no per-segment ellipsis.
- Active tab and toolbar row share one fill with no visible seam; inactive tabs show dividers except next to the active tab.
- Opening a second tab, navigating, switching back: first tab's path, selection and scroll position restored.
- Ctrl+F filters only the active pane; the path row's filter label reads `*query*`; Esc restores the full list and `*.*`.
- Sidebar header opens the workspace dropdown; picking a workspace navigates the left pane's current tab. Hamburger menu and dropdown close on outside click and Esc.
- Tab key still toggles the active pane; Ctrl+Tab cycles tabs.
