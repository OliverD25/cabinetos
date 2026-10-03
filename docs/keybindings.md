# Commands and keybindings

How CabinetOS names its actions, how keys are written, how chords work, and
which bindings nobody can change. This is the spec the core and the UI share:
the core compiles the keymap, the UI runs the key state machine described
here. Constitution Article 7 (Absolute Keyboard Control & Command Palette);
brief §7; the design's "Interactions and keyboard" and "Command palette"
sections ([design/README.md](design/README.md)).

The source of truth is the Rust crate `core/crates/cabinetos-commands`. The
user's changes live in `cabinetos.json` ([config.md](config.md)). The
messages are in [ipc.md](ipc.md).

## Commands

Every action is a command. A button, a menu item, the palette, a key binding
and a plugin all run commands by ID, so an action behaves the same however it
is started. A command has:

| Field | Example | Meaning |
|---|---|---|
| `id` | `view.toggleDualPane` | `category.verbObject`, unique |
| `category`, `title` | `View`, `Toggle Dual Pane` | What the palette shows: `View: Toggle Dual Pane` |
| `default_keys` | `delete`, `f8` | Its keys before the user changes anything, in the order the palette shows them; may be empty. Several keys share the command's `when` |
| `source` | `core` | Who provides it: the core, a plugin (`{"kind":"plugin","id":…}`), or an entry of `programs` in the configuration (`{"kind":"program","name":…}`) |
| `target` | `ui` | Who runs it: the UI, or the core |
| `when` | `filesView` | The context of its bindings; none means everywhere |
| `immutable` | `true` | Part of the Immutable System Tier (below) |

The core's commands, in palette order:

| ID | Title | Default keys | When | Runs in |
|---|---|---|---|---|
| `palette.show` | View: Show Command Palette | `ctrl+shift+p` | | UI |
| `overlay.close` | View: Close Overlay | `escape` | | UI |
| `keys.open` | Preferences: Open Keyboard Shortcuts | `ctrl+k ctrl+s` | | UI |
| `keys.rebind` | Preferences: Change Keys of Selected Command | `f2` | `paletteOpen` | UI |
| `view.toggleDualPane` | View: Toggle Dual Pane | `ctrl+shift+d` | | UI |
| `view.toggleTerminal` | View: Toggle Integrated Terminal | `ctrl+backquote` | | UI |
| `view.focusOtherPane` | View: Focus Other Pane | `tab` | `filesView` | UI |
| `view.toggleSidebar` | View: Toggle Sidebar | `ctrl+b` | | UI |
| `view.toggleCompactOverlay` | View: Toggle Compact Overlay | `ctrl+alt+up` | | UI |
| `view.layoutClassic` | View: Classic Layout | | | UI |
| `view.layoutRight` | View: Terminal on the Right | | | UI |
| `view.layoutRail` | View: Activity Rail | | | UI |
| `view.cycleLayout` | View: Next Layout | `ctrl+k ctrl+l` | | UI |
| `view.showExplorer` | View: Show Explorer | `ctrl+shift+e` | | UI |
| `view.showSearch` | View: Show Search | `ctrl+shift+f` | | UI |
| `sidebar.locate` | Sidebar: Locate Active Folder | `shift+alt+l` | | UI |
| `sidebar.lock` | Sidebar: Lock Folder Tree | | | UI |
| `sidebar.toggleFollow` | Sidebar: Follow the Active Pane | | | UI |
| `sidebar.pin` | Sidebar: Pin Folder | | | UI |
| `sidebar.unpin` | Sidebar: Unpin Folder | | | UI |
| `pane.openSelected` | Pane: Open Selected Item | `enter` | `filesView` | UI |
| `file.copyToOtherPane` | File: Copy to Other Pane | `f5` | `filesView` | UI |
| `file.moveToOtherPane` | File: Move to Other Pane | `f6` | `filesView` | UI |
| `file.newFolder` | File: New Folder | `f7` | `filesView` | UI |
| `file.rename` | File: Rename | `f2`, `shift+f6` | `filesView` | UI |
| `file.delete` | File: Delete to Recycle Bin | `delete`, `f8` | `filesView` | UI |
| `file.deletePermanently` | File: Delete Permanently | `shift+delete`, `shift+f8` | `filesView` | UI |
| `file.openInOtherPane` | File: Open in Other Pane | `ctrl+enter` | `filesView` | UI |
| `file.properties` | File: Properties | `alt+enter` | `filesView` | UI |
| `edit.cut` | Edit: Cut | `ctrl+x` | `filesView` | UI |
| `edit.copy` | Edit: Copy | `ctrl+c` | `filesView` | UI |
| `edit.paste` | Edit: Paste | `ctrl+v` | `filesView` | UI |
| `edit.selectAll` | Edit: Select All | `ctrl+a`, `ctrl+numpadadd` | `filesView` | UI |
| `edit.toggleSelection` | Edit: Toggle Selection | `insert` | `filesView` | UI |
| `go.back` | Go: Back | `alt+left` | | UI |
| `go.forward` | Go: Forward | `alt+right` | | UI |
| `go.up` | Go: Up One Level | `alt+up` | | UI |
| `go.toPath` | Go: Go to Path… | `ctrl+l` | | UI |
| `search.focus` | Search: Find in Pane | `ctrl+f`, `alt+f7` | | UI |
| `search.scope` | Search: Whole Volume | | | UI |
| `editor.openMarkdownPreview` | Editor: Open Markdown Preview | `ctrl+k v` | `filesView` | UI |
| `editor.close` | Editor: Close Editor | | | UI |
| `editor.reload` | Editor: Reload Editor | | | UI |
| `transfer.pause` | Transfer: Pause | | | UI |
| `transfer.resume` | Transfer: Resume | | | UI |
| `transfer.cancel` | Transfer: Cancel | | | UI |
| `transfer.minimize` | Transfer: Minimize Panel | | | UI |
| `transfer.restore` | Transfer: Show Panel | | | UI |
| `transfer.next` | Transfer: Show Next Job | | | UI |
| `transfer.close` | Transfer: Close Panel | | | UI |
| `conflict.resolve` | Transfer: Resolve Conflict | | | UI |
| `marketplace.browse` | Marketplace: Browse Extensions | `ctrl+shift+x` | | UI |
| `plugins.list` | Plugins: Show Plugins | | | UI |
| `workspace.switch` | Workspace: Switch Workspace… | `ctrl+k ctrl+w` | | UI |
| `preferences.selectColorTheme` | Preferences: Color Theme | `ctrl+k ctrl+t` | | UI |
| `themes.browse` | Themes: Browse | | | UI |
| `terminal.runTask` | Terminal: Run Task… | `ctrl+shift+b` | | UI |
| `terminal.new` | Terminal: New Terminal | `ctrl+shift+t` | `terminalFocus` | UI |
| `terminal.show` | Terminal: Show Terminal | | | UI |
| `terminal.close` | Terminal: Close Terminal | `ctrl+shift+w` | `terminalFocus` | UI |
| `terminal.reload` | Terminal: Reload Terminal | | | UI |
| `terminal.previousTab` | Terminal: Previous Terminal Tab | `alt+bracketleft` | `terminalFocus` | UI |
| `terminal.nextTab` | Terminal: Next Terminal Tab | `alt+bracketright` | `terminalFocus` | UI |
| `terminal.setMode` | Terminal: Lock or Link Terminal to Its Pane | | | UI |
| `terminal.toggleSplit` | Terminal: Split Terminal Under the Panes | `ctrl+backslash` | `terminalFocus` | UI |
| `go.root` | Go: Up to Root | `ctrl+backslash` | | UI |
| `go.chooseDriveLeft` | Go: Choose Drive for Left Pane… | `alt+f1` | | UI |
| `go.chooseDriveRight` | Go: Choose Drive for Right Pane… | `alt+f2` | | UI |
| `go.showInLeftPane` | Go: Show in Left Pane | `ctrl+left` | `filesView` | UI |
| `go.showInRightPane` | Go: Show in Right Pane | `ctrl+right` | `filesView` | UI |
| `go.pinnedFolders` | Go: Pinned Folders… | `ctrl+d` | | UI |
| `view.swapPanes` | View: Swap Panes | `ctrl+u` | `filesView` | UI |
| `view.refresh` | View: Refresh | `ctrl+r` | `filesView` | UI |
| `view.sortByName` | View: Sort by Name | `ctrl+f3` | `filesView` | UI |
| `view.sortByExtension` | View: Sort by Extension | `ctrl+f4` | `filesView` | UI |
| `view.sortByModified` | View: Sort by Date Modified | `ctrl+f5` | `filesView` | UI |
| `view.sortBySize` | View: Sort by Size | `ctrl+f6` | `filesView` | UI |
| `edit.toggleSelectionInPlace` | Edit: Toggle Selection in Place | `space` | `filesView` | UI |
| `edit.selectByPattern` | Edit: Select by Pattern… | `numpadadd` | `filesView` | UI |
| `edit.unselectByPattern` | Edit: Unselect by Pattern… | `numpadsubtract` | `filesView` | UI |
| `edit.selectSameExtension` | Edit: Select Same Extension | `alt+numpadadd` | `filesView` | UI |
| `edit.unselectSameExtension` | Edit: Unselect Same Extension | `alt+numpadsubtract` | `filesView` | UI |
| `edit.invertSelection` | Edit: Invert Selection | `numpadmultiply` | `filesView` | UI |
| `edit.unselectAll` | Edit: Unselect All | `ctrl+numpadsubtract` | `filesView` | UI |
| `edit.restoreSelection` | Edit: Restore Selection | `numpaddivide` | `filesView` | UI |
| `edit.copyFullPath` | Edit: Copy Full Path | `ctrl+shift+c` | `filesView` | UI |
| `edit.copyName` | Edit: Copy Name | `ctrl+k ctrl+n` | `filesView` | UI |
| `edit.copyFolderPath` | Edit: Copy Folder Path | `ctrl+k ctrl+p` | `filesView` | UI |
| `file.view` | File: View | `f3` | `filesView` | UI |
| `file.edit` | File: Edit | `f4` | `filesView` | UI |
| `file.newTextFile` | File: New Text File | `shift+f4` | `filesView` | UI |
| `file.windowsProperties` | File: Windows Properties | | `filesView` | UI |
| `file.calculateFolderSize` | File: Calculate Folder Size | | `filesView` | UI |
| `file.calculateAllFolderSizes` | File: Calculate All Folder Sizes | `shift+alt+enter` | `filesView` | UI |
| `terminal.insertPath` | Terminal: Insert Folder Path | `ctrl+alt+p` | `filesView` | UI |
| `terminal.insertSelectedPaths` | Terminal: Insert Selected Paths | `ctrl+shift+enter` | `filesView` | UI |
| `tab.new` | Tab: New Tab | `ctrl+t` | `filesView` | UI |
| `tab.close` | Tab: Close Tab | `ctrl+w` | `filesView` | UI |
| `tab.next` | Tab: Next Tab | `ctrl+tab` | `filesView` | UI |
| `tab.previous` | Tab: Previous Tab | `ctrl+shift+tab` | `filesView` | UI |
| `tab.toggleLock` | Tab: Toggle Tab Lock | | `filesView` | UI |
| `tab.openFolderInNewTab` | Tab: Open Folder in New Tab | `ctrl+up` | `filesView` | UI |
| `tab.moveToOtherPane` | Tab: Move Tab to Other Pane | `ctrl+k ctrl+right`, `ctrl+k ctrl+left` | `filesView` | UI |
| `tab.select` | Tab: Go to Tab | `ctrl+1` to `ctrl+9` | `filesView` | UI |
| `quickOpen.show` | Go: Quick Open… | `ctrl+p` | | UI |
| `menu.show` | View: Show Menu | | | UI |
| `settings.open` | Preferences: Open Settings | `ctrl+comma` | | UI |
| `menu.showShell` | File: Show Windows Context Menu | `ctrl+shift+f10` | `filesView` | UI |
| `menu.edit` | Preferences: Edit Context Menu… | | | UI |
| `menu.toggleShellMenu` | Menu: Toggle Windows' Shell Menu | | | UI |
| `window.new` | Window: New Window | `ctrl+n` | | UI |
| `help.about` | Help: About CabinetOS | | | UI |
| `diagnostics.toggleHeavy` | Diagnostics: Toggle Heavy Logging | | | UI |
| `diagnostics.openLogFolder` | Diagnostics: Open Log Folder | | | UI |
| `diagnostics.saveBundle` | Diagnostics: Save Log Bundle | | | UI |
| `update.check` | Update: Check for Updates | | | UI |
| `update.apply` | Update: Restart to Update | | | UI |
| `update.rollback` | Update: Roll Back to the Previous Version | | | UI |
| `update.showNotes` | Update: Show Release Notes | | | UI |
| `view.fitColumns` | View: Fit Columns to Content | | `filesView` | UI |
| `view.resetColumns` | View: Reset Column Widths | | `filesView` | UI |
| `view.equalPanes` | View: Equal Panes | | | UI |
| `view.toggleFolderSizes` | View: Toggle Folder Sizes | | | UI |
| `view.toggleHiddenFiles` | View: Toggle Hidden Files | `ctrl+k ctrl+h` | | UI |
| `view.toggleColumns` | View: Toggle Column View | `ctrl+alt+c` | `filesView` | UI |

- The list is the design's `COMMANDS` array without its plugin commands
  (hex view, Git, compression), and the shell's own commands: moving
  between folders, the pane's file and edit keys, the search field, and
  the window's own buttons and menus (the sidebar's pins, the editor tabs,
  the transfer panel, the plugin list, the terminal tabs). Plugin commands
  arrive with their plugins and register themselves (Article 10). The
  palette, the overlay exit, the shortcut editor, a new window
  (`window.new`, `ctrl+n` as in Explorer) and About are added. The
  shell's commands are in the registry so that each of them ranks in the
  palette and can be rebound like any other (Article 7); the window's
  buttons run the same commands, with arguments (a session, a path) that
  the palette leaves out.
- The three `diagnostics.*` commands (Phase 15) have no default keys: the
  palette is their place, and the status bar's "HEAVY LOG" pill runs
  `diagnostics.toggleHeavy` too. What they do is in
  [diagnostics.md](diagnostics.md) ("Heavy mode", "Bundles") and
  [ui.md](ui.md) ("Heavy logging").
- The four `update.*` commands (Phase 17) have no default keys either:
  the palette, the top row's menu and the status bar's update pill are
  their places ([ui.md](ui.md), "Updates").
- `view.toggleCompactOverlay` (Phase 19c) makes the window a small
  always-on-top drawer with one pane, and back ([ui.md](ui.md),
  "Compact overlay"). Its key, `ctrl+alt+up`, is the one the Files app
  uses; no other command has it in any context.
- `view.fitColumns` and `view.resetColumns` set the file panes' column
  widths (`ui.columns`, [ui.md](ui.md), "Column widths"). The first makes
  Modified, Type and Size as wide as their texts on screen, as a
  double-click on the Name heading does; the second gives the theme's
  widths back. They have no default keys: the palette is their place.
- `view.equalPanes` (Phase 24) makes the two file panes equal in width and
  writes `ui.paneSplit: null` ([ui.md](ui.md), "The divider between the
  panes"). A drag of the divider and a double-click on it are the window's
  ways to the same setting. It has no default key: the palette is its place.
  It works from anywhere, so its context is empty.
- `view.toggleFolderSizes` (Phase 19b) switches the setting
  `panes.folderSizes`: while it is on, every folder of a listing is
  measured when the listing opens ([ui.md](ui.md), "Folder sizes"). It has
  no default key: the palette is its place. It works from anywhere, so its
  context is empty.
- **Four settings that were in the file only** (Phase 23, the project skill
  `settings-three-ways`): each has a command that writes its key with
  `set_value`, and a control in the window ([ui.md](ui.md), "Settings
  reachable three ways"). `view.layoutClassic`, `view.layoutRight` and
  `view.layoutRail` choose `ui.layout`, and `view.cycleLayout`
  (`ctrl+k ctrl+l`, a chord under `ctrl+k` like `ctrl+k ctrl+s`) goes to the
  next one: classic, right, rail. `view.toggleHiddenFiles` flips
  `panes.showHidden`; its chord `ctrl+k ctrl+h` is free in every context.
  `sidebar.toggleFollow` flips `ui.sidebarAutoReveal` and
  `menu.toggleShellMenu` flips `contextMenu.shellMenu`; neither has a key.
  The palette's row of each says "current" or "on" or "off".
- `themes.browse` (Phase 23) opens the theme gallery ([ui.md](ui.md), "The
  theme gallery"). It has no key; the theme picker's last row opens it too.
- `view.toggleColumns` (Phase 19f) shows the active pane's front tab as
  columns, or as the list again ([ui.md](ui.md), "The column view"). Its
  key, `ctrl+alt+c`, is the one ADR 0016 names; no other command has it by
  default. Inside the column view Left and Right are the pane's own keys,
  not commands, as Up and Down are: Left goes to the parent column, Right
  into the column of the cursor's folder.
- The rows from `go.root` to `terminal.insertSelectedPaths` are Total
  Commander's small commands (sub-phase 11a): their titles, keys and
  contexts are the research note's
  ([research/total-commander.md](research/total-commander.md), Part 3 (b),
  N1 to N31), and what each does is the window's ([ui.md](ui.md)). None of
  their keys is used by another command in any context, except `ctrl+backslash`
  (below), and none starts a chord; `ctrl+k ctrl+n` and `ctrl+k ctrl+p` are
  chords under `ctrl+k`, like `ctrl+k ctrl+s`.
- **Ctrl+\ is two commands, by where the keyboard is** (terminal unit 3,
  2026-10-02). In a file pane it is `go.root` (Up to Root, Total Commander's
  key). In the terminal it is `terminal.toggleSplit`, bound with
  `when: terminalFocus`, which splits the Tool Dock under the two panes or
  joins it again ([ui.md](ui.md), "The terminal"). Two commands share the keys
  in two contexts, as `f2` does ("Contexts" below): the binding with a context
  is the more specific one, so in the terminal the split wins, and in a pane
  `go.root` runs. The palette lists the command as "Terminal: Split Terminal
  Under the Panes"; it joins the halves again when the dock is split.
- The rows from `tab.new` to `tab.moveToOtherPane` are Phase 12's: tabs per
  pane ([ui.md](ui.md), "Tabs"). The window owns the tab state, so every one
  runs in the window. `ctrl+up` moves the cursor without selecting in a
  file list, as every Windows list does; as `tab.openFolderInNewTab` it now
  opens the folder under the cursor in a new tab, as Total Commander's does,
  and the plain cursor key goes (Home, End and the other arrows stay).
  `tab.moveToOtherPane` has two keys and one command: the key says which
  pane the tab goes to (`ctrl+k ctrl+right`: the right pane,
  `ctrl+k ctrl+left`: the left one); from the palette it goes to the other
  pane. A tab that has a tool (Markdown Preview) in it is a tab like the
  others. Tab and Alt+Left / Alt+Right keep their old jobs: the pane switch,
  Back and Forward.
- The rows from `tab.select` to `settings.open` are Phase 16's, the shell
  redesign ([ui.md](ui.md), "The shell"). `tab.select` has nine keys and one
  command: the key's digit names the tab's place (`ctrl+1` the first), and a
  place past the last tab does nothing; the tab strip's clicks run it with
  the tab's index. `quickOpen.show` takes `ctrl+p`, so Total Commander's
  `terminal.insertPath` moved to `ctrl+alt+p`. `search.focus` keeps
  `ctrl+f` and `alt+f7` and now opens the pane's find widget; its title is
  "Find in Pane". With `{"pane": 0 or 1}` (the path row's filter label)
  it opens that pane's find; the toolbar's Find button runs the window's
  own `search.toggle` with the pane's index, which no key has.
  `settings.open` opens `cabinetos.json` with the program
  F4 uses, and `menu.show` (the top row's hamburger) has no key. The ID
  test allows a camelCase category, as `quickOpen` is one. None of these
  keys was bound before in the same context, and the Immutable System Tier
  is unchanged.
- `menu.showShell` and `menu.edit` are Phase 18's, the context menu from the
  configuration ([ui.md](ui.md), "The context menu"). `menu.showShell`
  opens Windows' own menu of the focused row when `contextMenu.shellMenu` is
  on, and CabinetOS's menu when it is off, as Shift+right-click does;
  `ctrl+shift+f10` was bound to nothing before. `menu.edit` is the menu's
  last row, "Edit Menu…", and turns the menu into its edit mode (Phase 18,
  step 2; [ui.md](ui.md), "Editing the menu"); from the palette it edits the
  focused row's menu. It has no key; `settings.open` (Ctrl+,) still opens
  the file. Inside the edit mode the keys are its own, not the keymap's:
  Up and Down, Alt+Up and Alt+Down (move the row), Delete, Insert (Add
  Command…), Ctrl+S (Done) and Esc (Cancel). The mode holds the keyboard as
  a dialog does, so no binding runs under it: Alt+Up is not `go.up` there,
  and Delete is not `file.delete`.
- Each entry of `programs` in the configuration ([config.md](config.md),
  "Programs") adds the command `program.<name>`: category `Programs`, its
  `title`, no default keys, context `filesView`, run by the core. A binding
  in `keybindings` gives it keys like any other command:
  `{"command": "program.code", "keys": "ctrl+alt+e"}`. When the entry
  goes, its command goes, and a binding to it is left out with a warning.
- In the rail layout ([ui.md](ui.md), "The activity rail and the sidebar")
  `view.showExplorer` (`ctrl+shift+e`) and `view.showSearch`
  (`ctrl+shift+f`) show the views of the sidebar and put the keyboard in
  them, and `sidebar.locate` (`shift+alt+l`, the design's Alt+Shift+L) reveals
  the active folder in the tree; `sidebar.lock` has no key. In the classic
  and right layouts the first opens the sidebar, the second shows the Search
  view in the sidebar's place until the search is left, and the other two
  say that the tree belongs to the rail layout. Inside a view the keys
  are the view's own and are not commands: in the tree Up, Down, PageUp,
  PageDown, Home, End, Right, Left, Space, Enter and Esc; on a rail button
  Up and Down walk the buttons and Shift+Up / Shift+Down move the button
  (`ui.rail`). `shift+alt+l` is the core's own spelling of the chord.
- While a preview of proposed changes shows in a pane (a plugin proposed
  it; [ui.md](ui.md), "What plugins ask of the window"), **Enter applies it
  and Esc cancels it**, before the keymap: the window takes these two keys
  when the keyboard is in the preview or in a list, so `pane.openSelected`
  (Enter) and `overlay.close` (Esc) do not run then. They are not commands
  and cannot be rebound; the preview says "Enter applies · Esc cancels" in
  its bar. Every other key works as always. A plugin's command that asks
  for text (an `input` in its manifest) shows a prompt box when a key or the
  palette runs it; Enter runs it and Esc cancels.
- The second keys of `file.rename` (`shift+f6`), `file.delete` (`f8`),
  `file.deletePermanently` (`shift+f8`), `edit.selectAll`
  (`ctrl+numpadadd`) and `search.focus` (`alt+f7`) are Total Commander's
  keys for the same actions (sub-phase 11a;
  [research/total-commander.md](research/total-commander.md), Part 2).
- The Markdown preview is a Tool Extension that the window opens
  ([tool-extensions.md](tool-extensions.md)), so its command,
  `editor.openMarkdownPreview`, is the window's. It keeps the design's
  binding, `ctrl+k v`: the one chord whose second key goes without Ctrl.
- The design wrote the workspace switcher as "Ctrl+K W" in one place and
  "Ctrl+K then Ctrl+W" in another (PLAN.md, conflict E). The registry uses
  `ctrl+k ctrl+w`: Ctrl stays held for the second key, like every other chord
  here. The UI shows it as "Ctrl+K Ctrl+W". Since Phase 16 it opens the
  workspace dropdown, since v2 of the shell redesign under the sidebar's
  workspace row ([ui.md](ui.md), "The sidebar header").
- `f2` has two commands in two contexts: in a file pane it renames
  (`file.rename`), in the open palette it records new keys for the chosen
  command (`keys.rebind`). Two commands may share keys only this way
  ("Contexts" below).
- Every command here runs in the UI. The core runs only the plugins'
  commands and the programs' (`program.<name>`); asking it to run a UI command (`execute_command`) returns
  `command_routed`, and the core does nothing. The file commands are the
  shell's too: it starts their jobs itself (`start_job`) and makes a folder
  with `create_directory`. `help.about` is the window's About view, which
  takes the versions from `welcome` and the configuration file's path
  from `get_config`; until 2026-09-29 the core answered it with those
  values.

## Writing keys

A binding is one **combination** (`ctrl+shift+p`) or a **chord** of two
combinations separated by a space (`ctrl+k ctrl+s`). A combination is any
number of modifiers and exactly one key, joined with `+`.

- **Modifiers:** `ctrl`, `shift`, `alt`, `win`. Also accepted: `control` for
  `ctrl`, `meta` for `win`. A modifier may appear once.
- **Keys:** the letters `a`–`z`, the digits `0`–`9`, `f1`–`f24`, and
  `escape`, `enter`, `tab`, `space`, `backspace`, `delete`, `insert`, `home`,
  `end`, `pageup`, `pagedown`, `up`, `down`, `left`, `right`, `backquote`,
  `comma`, `period`, `slash`, `minus`, `equal`, `bracketleft`,
  `bracketright`, `backslash`, `semicolon`, `quote`.
- **The keypad's operators:** `numpadadd` (Num +), `numpadsubtract` (Num -),
  `numpadmultiply` (Num *), `numpaddivide` (Num /) and `numpaddecimal`
  (Num .), keys of their own. The keypad's digits are the plain digits
  `0`–`9`, as they are on Windows with Num Lock on: there is no `numpad0`.
- **Also accepted for keys:** `esc`, `return`, `del`, `ins`, `pgup`, `pgdn`,
  VS Code's names of the keypad's operators (`numpad_add`,
  `numpad_subtract`, `numpad_multiply`, `numpad_divide`, `numpad_decimal`),
  and the characters themselves: `` ` `` `,` `.` `/` `-` `=` `[` `]` `\` `;`
  `'`. The `+` key cannot be written as `+`, because `+` joins the parts;
  write `shift+equal`, or `numpadadd` for the keypad's.
- **At most two combinations.** Two already multiply the number of free
  shortcuts, and a longer chord would make the UI's state machine harder to
  follow.

Keys are read without regard to case or to the order of the parts, and
stored in one **normal form**: lower case, modifiers in the order
`ctrl+shift+alt+win`, then the key, one space between the two combinations of
a chord. So `Shift+Ctrl+P` becomes `ctrl+shift+p`, and `Ctrl+K  Ctrl+S` (two
spaces) becomes `ctrl+k ctrl+s`. There is no space inside a combination. The
core writes, sends and compares only the normal form.

Keys that break these rules are refused with an error that names the problem,
for example `` `ctrl+nope` is not a valid key binding: `nope` is not a key
name ``: in a request as the error code `invalid_keys`, in the file as a
`config_error` with the line.

## Chords

The keymap the core sends has `chord_window_ms` (1000). The UI's key state
machine works like this:

1. **Idle.** When a combination is pressed (pressing a modifier alone does
   not count):
   - if it is the first half of a chord bound in the current context, the UI
     waits for the second half (step 2) and says so in the status bar, for
     example "Ctrl+K was pressed. Waiting for the second key…";
   - otherwise, if it is bound on its own in the current context, the UI
     runs that command;
   - otherwise the key goes on to whatever has focus (typing, for example).
2. **Waiting.** The next combination, if it comes within `chord_window_ms`,
   ends the wait:
   - if it is a single key of the Immutable System Tier, the tier keeps
     it: Esc only ends the wait (nothing runs, no notice), and
     Ctrl+Shift+P ends it and opens the palette. They win over a chord
     that ends with them. Until 2026-10-01 both were reported as a chord
     that is not bound;
   - if it completes a chord that starts with the first half, the UI runs
     that command;
   - otherwise nothing runs, and the key is not passed on. The status bar
     says the chord is not bound; when the two keys are bound in another
     context, it says where they work instead: "Ctrl+K V works only in a
     file list", or "G G does not work while you type in a box. Esc
     leaves the box." (a chord without `when` whose first key types in
     the text box that has the keyboard, "Contexts" below).
     Until 2026-10-01 it called such a chord not bound.
   If no combination comes within `chord_window_ms`, the wait ends and
   nothing runs.
   A first half held down is not a second half: Windows repeats a held
   key after its delay (250 to 1000 ms), and each repeat of the first
   half keeps the wait and starts `chord_window_ms` again. Until
   2026-10-01 the first repeat ended the wait as "Ctrl+K Ctrl+K is not
   bound", so a chord worked or failed by how long Ctrl+K was held.
3. Back to idle.

No combination is ever both the first half of a chord and a binding on its
own (the core refuses such a keymap, below). So in step 1 the UI never has to
guess whether to run a command now or wait.

**Keys held down.** Windows repeats a key that is held down. A bound key
runs its command once per press, and its repeats run nothing and go
nowhere else. So holding Ctrl+Shift+P, Ctrl+` or Ctrl+B a moment too long
no longer opens and closes the palette, the terminal or the sidebar again
and again. Six commands are meant to repeat, and they run on every repeat:
`tab.next` and `tab.previous` (Ctrl+Tab, Ctrl+Shift+Tab), `edit.toggleSelection`
(Insert marks the row and moves the cursor down), and `go.back`,
`go.forward` and `go.up` (Alt+Left, Alt+Right, Alt+Up move through folders
as the arrows move through rows). The list is `ChordStateMachine.RepeatingCommands`
in the UI. A key nobody bound repeats as before: a letter typed into a box,
an arrow in the list. A key a web page passes back to the window (the
terminal, a tool) is passed once, whatever its command. This is the rule
since 2026-10-01.

**Contexts.** A binding with a `when` applies only while that context holds;
a binding without one applies everywhere. When both kinds match the same
keys, the one with the `when` wins: it is the more specific. The core treats
`when` values as plain names and never evaluates them; the UI decides which
hold. The names in use: `filesView` (a file pane has focus), `paletteOpen`
(the command palette is open), `textInput` (a text box has focus),
`terminalFocus` (the terminal has focus).

**Text boxes.** While a text box has the keyboard (`textInput`), the box
keeps the keys that type or edit. A key that types nothing there runs its
binding. This is the rule since 2026-10-01; before it, only the Immutable
System Tier worked in a box.

- **The box keeps these keys.** Letters, digits and the other characters,
  alone or with Shift. Space, Enter, Esc, Backspace, Delete, Insert, Home,
  End, PageUp, PageDown, the arrows and Tab, alone or with Shift. The
  box's own editing keys: Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+Z, Ctrl+Y,
  Ctrl+Insert, Ctrl+Backspace, Ctrl+Delete, Ctrl+Left, Ctrl+Right,
  Ctrl+Home and Ctrl+End, each also with Shift. Ctrl+Alt with a character
  key, because Ctrl+Alt is AltGr, and many layouts type characters with it.
  Alt with a digit, because Alt with the keypad's digits types a character
  by its code, and the grammar names those digits like the top row's.
- **Every other key runs its binding.** These are the function keys, with
  any modifiers, and the other combinations with Ctrl, Alt or Win: Ctrl+B,
  Ctrl+T, Ctrl+Tab, Ctrl+Enter, Ctrl+Up, Alt+Left, Alt+Enter, Ctrl+Alt+Up.
  A text box types nothing with them.
- **The pane's own boxes count as the pane.** The active pane's find box
  (Ctrl+F) and its address box (Ctrl+L) are `filesView` as well as
  `textInput`. So the pane's keys that type nothing act on the pane from
  there: F5 copies the cursor row to the other pane, Ctrl+F3 sorts, Ctrl+T
  opens a tab. The other boxes are text input only: a name typed in place
  (F2), the palette's field, Quick Open, the prompts, the Search view's
  field, the marketplace's search and the other pane's boxes. F5 there
  does nothing.
- A binding for another context that holds around a box (`paletteOpen`,
  F2 in the palette) runs as it always did.
- **A chord goes by its first half.** Ctrl+K types nothing, so Ctrl+K
  Ctrl+T works in any box, and Ctrl+K V in the find box. The second half
  belongs to the chord once the wait has started.
- The Immutable System Tier works everywhere, as before.

**Tool pages and the terminal.** A web page in the window (a tool's page, the
terminal) gets every key. The window's script in the page passes a key back
as a message only when its combination is in the list the window sends it
(`passKeys`, from `TerminalKeys.PassKeys`), so every other key stays with the
page. The list comes from the keymap, so a key the user rebound follows.
This is the rule since 2026-10-01 (round three of the keys audit; before it
no tab key came out of a page):

| The page | The keys it passes back |
|---|---|
| a tool in a pane's tab (Markdown Preview) | the ways out (`palette.show`, `view.toggleTerminal`) and the tab keys: `tab.next`, `tab.previous`, `tab.close`, `tab.new`, `tab.select` (Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W, Ctrl+T, Ctrl+1 to 9) |
| a tool in the sidebar (the agent's chat) | the same, and `view.showExplorer`, `view.showSearch`, `view.toggleSidebar` |
| the terminal | the ways out, the keys bound with `when: terminalFocus` (by default the terminal's tab keys: Ctrl+Shift+T, Ctrl+Shift+W, Alt+[ and Alt+], and Ctrl+\ for the split), and `tab.next` and `tab.previous` (Ctrl+Tab, Ctrl+Shift+Tab) |

- **Why the tab keys come out.** They are `filesView` bindings, and a page in
  a pane's tab has the keyboard as that pane's list has it. With the page
  keeping them, the mouse was the only way from a preview to another tab.
- **Whose tabs.** A tab key from a tool in a pane's tab is about that pane's
  tabs, also when the other pane is the active one (a preview opens in the
  other pane): the page's key names its pane. From the sidebar's page or the
  terminal it is about the active pane, as from a list.
- **Where the keyboard goes.** It follows the tab that comes to the front, as
  after a tab command from a list: a folder tab gets it in its list, another
  tab of the tool in its page. Ctrl+W on the preview's tab closes it, and the
  keyboard goes to the list of the tab that is left, as Close Editor does. A
  page keeps the keyboard only while its tab is still the front one. From the
  terminal, Ctrl+Tab moves the keyboard to the pane.
- **What stays with the page.** Tab, Esc and every character key stay with a
  tool's page: it uses them for its links and fields. Chords never pass back,
  because their first key may be the page's. So the way from a page to the pane
  is Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1 to 9, or closing the tab.
- **What stays with the shell.** Ctrl+Tab and Ctrl+Shift+Tab are no shell's
  keys, so the terminal passes them back. Ctrl+W (readline's delete word, vim's
  window prefix), Ctrl+T (readline's transpose, fzf's file picker) and
  Ctrl+3 to Ctrl+8 (xterm.js sends them to the shell as control codes, ESC to
  DEL, so `tab.select` stays whole) are shell keys, so `tab.close`, `tab.new`
  and `tab.select` stay with the shell. Ctrl+C with
  a selection and Ctrl+V are the terminal's own, as before, and so are
  Ctrl+Shift+C (copy the selection) and Ctrl+Shift+V (paste): the page
  handles them itself, so the window's keymap never sees them there, and
  in a pane Ctrl+Shift+C stays `edit.copyFullPath`.
- **The terminal's own tabs.** Ctrl+Shift+T, Ctrl+Shift+W, Alt+[ and Alt+]
  are `terminalFocus` bindings of `terminal.new`, `terminal.close`,
  `terminal.previousTab` and `terminal.nextTab` (since 2026-10-01). A
  shortcut is matched by the key's position (its virtual key), not by the
  character it types, so Alt+[ and Alt+] also work on the Ukrainian
  layout, where those two keys type х and ї ([ui.md](ui.md), "The
  terminal"). While the dock is split, Alt+[ and Alt+] go round the tabs
  of the half that has the keyboard, Ctrl+Shift+W closes that half's tab,
  and Ctrl+Shift+T opens a tab in the half of the active pane.
- A key held down is passed once (the rule above), so Ctrl+Tab held in a page
  does not repeat; it repeats in a pane's list.

**One overlay at a time.** The palette, Quick Open, a prompt (the drive list,
the pattern box, the pinned folders, a plugin's question), the theme picker and
the plugin list are the overlays. Opening one closes every other one first, so
at most one is open and Esc closes the one on screen. The Extensions page and the
theme gallery, which cover the panes, close them all as they open (and the
gallery closes the picker with the theme in effect painted back); the gallery's Esc
paints the theme in effect back too. Each closes as Esc closes it:
the keyboard goes to the pane, then the new overlay takes it. `OverlayRule`
says what closes (tested), and `MainWindow.CloseOtherOverlays` is the one
step every opening calls. Ctrl+K Ctrl+T works from any box ("Text boxes"), so
from the palette, Quick Open or the drive list it closes that overlay and shows
the picker, and the first Esc closes the picker and leaves nothing open.
The permissions review is not one of them: it is a dialog of the window (the
router refuses every command but its own while it shows), and it opens over the
plugin list or the marketplace that asked for it, or by itself when a plugin
newly waits for a review, which does not close what the user is doing. The
rules of the audit's round two stay: a key that types nothing runs from a box,
and Esc during a chord's wait only ends the wait. Until 2026-10-01 the picker
opened over the palette, Quick Open, a prompt and the drive list, the plugin
list opened over the palette and the prompts, and the first Esc closed the
overlay under the picker.

## The Immutable System Tier

Three commands keep their keys whatever the configuration says:

| Command | Keys | Why |
|---|---|---|
| `palette.show` | `ctrl+shift+p` | The way to every other command |
| `overlay.close` | `escape` | The way out of any overlay |
| `keys.open` | `ctrl+k ctrl+s` | The way to repair a broken keymap |

With these three, nobody can configure themselves out of the application.
The rules:

1. Their bindings cannot be changed or removed.
2. No other command may use their keys.
3. No other command may be bound to a combination that starts one of their
   chords: `ctrl+k` alone would make `ctrl+k ctrl+s` unreachable.

A `set_keybinding` request that breaks a rule gets the error code
`immutable_binding`, and the file is not changed. A configuration file that
breaks one is refused as a whole: the core keeps the settings it had and sends
`config_error` with the line of the offending entry. The same holds when the
core starts with such a file: it uses the defaults until the file is fixed.

## Changing bindings

The user's changes are the `keybindings` list in `cabinetos.json`. It lists
only what differs from the defaults:

```json
"keybindings": [
  { "command": "view.toggleSidebar", "keys": "ctrl+alt+b" },
  { "command": "view.toggleTerminal", "keys": "" },
  { "command": "file.newFolder", "keys": "ctrl+shift+n", "when": "filesView" }
]
```

- An entry replaces **all** default keys of its command. Several entries for
  one command give it several bindings.
- `"keys": ""` leaves the command without keys.
- Without `when`, the entry keeps the command's own context. So rebinding
  `file.copyToOtherPane` does not make it fire in a name typed in place or
  in the palette's field ("Text boxes" under "Contexts").
- An entry for a command nobody registered is ignored, with a warning in the
  core's log: the plugin that registered it may have been removed.

The core compiles the defaults and these entries into the keymap, and refuses
the whole configuration when:

- an entry breaks the Immutable System Tier (above);
- two commands get the same keys in the same context: "ctrl+b is bound to
  both view.toggleSidebar and go.toPath";
- a combination is bound on its own and also starts a chord, in any context.
  With `go.toPath` on `ctrl+g ctrl+p` and `view.toggleSidebar` on `ctrl+g`:
  "ctrl+g starts the chord ctrl+g ctrl+p of go.toPath, so it cannot also run
  view.toggleSidebar on its own".

The same keys in different contexts are allowed (see "Contexts" above).

**From the UI or the command line.** `set_keybinding` binds a command to new
keys and `reset_keybinding` gives it its defaults back; both write the file
and answer with the new keymap. `set_keybinding` replaces the user's earlier
entries for that command with one entry without `when`. So for a command
with several default keys (`file.delete` has `delete` and `f8`), the
palette's pencil (`keys.rebind`) replaces all of them with the one new key,
and `reset_keybinding` brings all of them back. A second key of the user's
own is a second entry in the file. From a terminal:

```text
cabinetos-cli keys list
cabinetos-cli keys set view.toggleSidebar "ctrl+alt+b"
cabinetos-cli keys reset view.toggleSidebar
cabinetos-cli keys watch
```

`keys watch` prints every change the core announces, with the time since the
file was written.

## Palette search

The palette sends what the user typed (`search_commands`); the core ranks the
commands, since the UI does no data processing (brief §1). A command matches
when the typed letters appear in order, not necessarily together, in
`Category: Title` or in its ID. Case and spaces in the query are ignored.
Among all the ways a command matches, the best one counts:

| For | Points |
|---|---|
| each matched letter | +16 |
| a matched letter that starts a word (after a space, `:`, `.`, `-`, `_`, `/`, or at a lower-to-upper case change as in `toggleDualPane`) | +24 |
| a matched letter right after the previous matched one | +20 |
| each letter skipped between two matched ones | −3, at most −30 per gap |
| each letter skipped before the first match | −1, at most −15 |

The highest score comes first; a tie goes to the shorter text, then to the
registry order. An empty query lists every command in registry order.
Examples: `dual` finds `view.toggleDualPane` first, `new fold` finds
`file.newFolder`, `tdp` finds `view.toggleDualPane` by its word starts.
