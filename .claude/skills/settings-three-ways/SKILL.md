---
name: settings-three-ways
description: The creator's rule of 2026-10-03: every preference of CabinetOS is reachable three ways, from the window, from the command palette (with a shortcut that can be bound) and from the settings file, and the three stay in sync. Use it when a unit adds, renames or removes a setting, a view or a layout; when a handout for a coder is written; when a coder's branch is reviewed; and for the audit of the settings that exist ("is this setting reachable three ways", "audit the settings", "add a setting", "the layout commands", "settings only in the file").
---

# Every preference, three ways

## 1. The rule

The creator's words of 2026-10-03: "There should be no settings in our system
that exist only in the file and can be changed only through editing the
settings files. We should be able to do these things three ways: from the UI,
from the command palette, from editing the files."

It follows from the Constitution. Article 6, Universal Configuration: every
preference is mirrored, a graphical settings menu and the raw file are two
views of the same value, and a change in one shows in the other at once.
Article 7, Absolute Keyboard Control: every action is a command with a
discrete name and a shortcut that can be bound. Article 4, Progressive
Disclosure: the window shows the simple form, the file holds everything.

The reason it is written down: on 2026-10-02 the creator could not find the
activity rail (the column of buttons at the window's left edge, the `rail`
layout) because `ui.layout` lives in the file only. A setting nobody can
reach from the window or the palette is a setting nobody finds.

## 2. What counts as a preference

- **A preference**: a value the user chooses and expects to keep. The layout,
  the theme, hidden files, the default editor, the terminal's default
  profile, the update channel. Three ways, always.
- **Remembered state**: what the window writes because the user did
  something in it. The last paths, the open tabs, a dragged width, a pane's
  sort, the pinned folders. The window and the file are its two ways. A
  command is needed only when the action itself is one (pinning is a command;
  dragging a divider is not).
- **A machine fact**: a path or an address the environment gives, such as
  the plugins folder or the log folder. Flags and environment variables, not
  settings. Not covered by the rule.

When a key does not fit cleanly, treat it as a preference.

## 3. The three ways, exactly

1. **The file.** A key in `cabinetos.json`, in the core's config model and
   schema, with a row in [docs/config.md](../../../docs/config.md): type,
   values, default, meaning. The core watches the file, and a change applies
   at once without a restart.
2. **The palette.** A command in the registry
   (`core/crates/cabinetos-commands/src/registry.rs`) with a category and a
   discrete name. A boolean gets a toggle command whose row shows the current
   state ("View: Toggle Hidden Files"). A choice gets one command per value
   ("View: Classic Layout", "View: Terminal on the Right", "View: Activity
   Rail") or a picker, as the theme picker is. A text or a number gets a
   prompt. A default shortcut when the action is frequent, otherwise none;
   every command can be bound. The command writes the key through
   `set_value`, so the file and the window follow.
3. **The window.** A control a user finds without the palette, in the place
   where the thing is: the top row's menu (the hamburger), the Settings page
   once Phase 11c builds it, a toggle in a view's header, a chevron menu in
   the dock, an entry in the right-click menu. The control shows the current
   value and follows the file.

The key is the one source. The command and the control read and write it
through the core (`set_value`, `config_changed`); the window keeps no second
copy. A change made any of the three ways shows in the other two at once.

## 4. The checklist for a unit that adds or changes a setting

A handout names all three ways, and a review refuses a unit where one is
missing, unless section 2 says the key is remembered state.

- The config model, the schema and the `docs/config.md` row.
- The command: id, category, title, default shortcut or "none", in the
  registry; its row in `docs/keybindings.md`.
- The control in the window and where it lives; the exact text of the menu
  item or label.
- The round trip: the command and the control write through `set_value`; an
  edit of the file moves the control. One test for the registry entry, one
  window test that the control follows a file change.
- The CHANGELOG line names all three ways.
- For a removed or renamed key: the old key is read for one version, with a
  note in `docs/config.md`.

## 5. The audit of 2026-10-03

A first pass by the planning session from `docs/config.md`, the registry and
`docs/ui.md`. The "window" column is from the documents, not from a run; the
coder that fixes the gaps checks it. Keys of remembered state are listed so
the next audit does not ask again.

| Key | Palette | Window | Verdict |
|---|---|---|---|
| `ui.layout` (classic, right, rail) | `view.layoutClassic`, `view.layoutRight`, `view.layoutRail`, `view.cycleLayout` (Ctrl+K Ctrl+L) | the menu's "Layout" submenu | fine since Phase 23 (was gap 1) |
| `ui.dualPane` | `view.toggleDualPane`, Ctrl+Shift+D | top-row button | fine |
| `ui.sidebar` | `view.toggleSidebar`, Ctrl+B | the menu | fine |
| `ui.theme` | `preferences.selectColorTheme`, Ctrl+K Ctrl+T, `themes.browse` | the picker, the theme gallery | fine |
| `ui.sidebarAutoReveal` | `sidebar.toggleFollow` | the pin in the Explorer view's header, the menu's "Follow the Active Pane" | fine since Phase 23 (was gap 3) |
| `ui.sidebarView` | `view.showExplorer`, `view.showSearch` | the rail's buttons | remembered state, fine |
| `ui.rail` (the buttons' order) | none | dragging in the rail | remembered state, fine |
| `ui.sidebarWidth`, `ui.dockSize.*`, `ui.columns` | none | dragging | remembered state, fine |
| `ui.lastPaths`, `ui.tabs.*`, `panes.selection`, `terminal.tabs` | none | the panes and the dock | remembered state, fine |
| `ui.pinned` | `sidebar.pin`, `sidebar.unpin` | the sidebar | fine |
| `ui.compactOverlay` | `view.toggleCompactOverlay`, Ctrl+Alt+Up | to check | command fine; window to check |
| `panes.showHidden` | `view.toggleHiddenFiles`, Ctrl+K Ctrl+H | the menu's "Show Hidden Files", the word "hidden" in the status bar | fine since Phase 23 (was gap 2) |
| `panes.sort.*` | `view.sortBy*` | column headers (since Phase 24) | fine |
| `panes.folderSizes` | `view.toggleFolderSizes` | to check | command fine; window to check |
| `files.editor` | none (`file.edit` uses it) | the "Open with" picker is per file | **Gap 7.** "Preferences: Choose Editor" and a Settings entry |
| `contextMenu` (the menu's rows) | `menu.edit` | "Edit Menu…" in the menu | fine |
| `contextMenu.shellMenu` | `menu.toggleShellMenu` | the last row of "Edit Menu…" | fine since Phase 23 (was gap 4) |
| `terminal.defaultProfile` | none | the dock's "+" chooses per terminal | **Gap 5.** "Terminal: Default Profile" picker and a chevron entry |
| `terminal.profiles` | none | none | **Gap 8.** The Settings page (11c); until then "Terminal: Edit Profiles" opens the file at the key |
| `terminal.split` | `terminal.toggleSplit`, Ctrl+\ | the dock | fine |
| `terminal.restore` | none | none | **Gap 5.** A toggle command and a chevron entry |
| `terminal.defaultMode` | none (`terminal.setMode` is per session) | the Locked/Linked switch is per session | **Gap 5.** "Terminal: New Terminals Start Locked/Linked" |
| `logging.level` | none | none | **Gap 7.** "Diagnostics: Log Level" picker; Settings page |
| `logging.heavy` | `diagnostics.toggleHeavy` | to check | command fine; window to check |
| `marketplace.index`, `marketplace.themes`, `marketplace.allowInsecure` | none | none | **Gap 8.** The Settings page (advanced section) |
| `update.check` (the daily check on or off) | none (`update.check` checks now) | none | **Gap 6.** "Update: Check Automatically" toggle; the status bar's update pill |
| `update.autoInstall` | none | the notice's buttons act once | **Gap 6.** "Update: Install Automatically" toggle |
| `update.channel` | none | none | **Gap 6.** "Update: Channel" picker |
| `update.source`, `update.allowInsecure` | none | none | **Gap 8.** The Settings page (advanced section) |

The gaps, in the order to fix them: 1 the layout (the creator's first ask),
2 hidden files, 3 the Explorer view following the pane, 4 the shell menu,
5 the terminal's defaults, 6 the update settings, 7 the editor and the log
level, 8 the Settings page of Phase 11c for the rest. The desk card
"Settings reachable three ways" tracks them; gaps 1 to 4 fit one coder unit
and were closed in Phase 23 (the rows above say which command and which
control; the window tests are in `ThreeWaysEndToEndTests`).

## 6. How to run the audit again

1. The keys: the table in `docs/config.md` (every row that starts with a
   key in backticks).
2. The commands: the ids in the registry, and their titles and default keys
   in `docs/keybindings.md`.
3. The window: `docs/ui.md` for the menus, the dock's chevron, the views'
   headers, and the Settings page once it exists.
4. One row per key as in section 5; a key with a missing way is a gap unless
   section 2 says it is state. Write the table into the desk card and the
   gaps into the plan.
