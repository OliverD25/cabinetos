# The configuration file

Every setting of CabinetOS lives in one JSON file, `cabinetos.json`. The core
owns it: it creates it, watches it, checks every change, and tells the UI.
An edit saved in any text editor takes effect within a second, and the
settings UI (Phase 5) writes the same file. Constitution Article 6
(Universal Configuration); brief §7.

The source of truth is the Rust crate `core/crates/cabinetos-config`. Its
JSON Schema is [sdk/config/cabinetos.schema.json](../sdk/config/cabinetos.schema.json).
Keybindings have their own page: [keybindings.md](keybindings.md).

## Where it is

| What | Where |
|---|---|
| The file | `%APPDATA%\CabinetOS\cabinetos.json` |
| Another file | `--config <path>` on `cabinetos-core`, or the environment variable `CABINETOS_CONFIG` with the full path. The flag wins over the variable. |
| The schema | `cabinetos.schema.json`, next to the file |

`cabinetos-cli config path` prints the file the running core reads.

## First run

When the file does not exist, the core writes it with every default, so the
file itself shows everything that can be set:

```json
{
  "$schema": "./cabinetos.schema.json",
  "version": 1,
  "ui": {
    "layout": "classic",
    "dualPane": true,
    "sidebar": true,
    "theme": "default",
    "lastPaths": [],
    "pinned": [],
    "dockSize": {
      "bottom": null,
      "right": null
    },
    "tabs": {
      "left": {
        "items": [],
        "active": 0
      },
      "right": {
        "items": [],
        "active": 0
      }
    }
  },
  "panes": {
    "showHidden": false,
    "sort": {
      "key": "name",
      "descending": false
    }
  },
  "terminal": {
    "defaultProfile": "pwsh",
    "profiles": [
      { "name": "pwsh", "command": "pwsh.exe", "args": ["-NoLogo"], "linkable": true },
      { "name": "cmd", "command": "cmd.exe", "args": [], "linkable": false },
      { "name": "wsl", "command": "wsl.exe", "args": [], "linkable": true },
      { "name": "claude", "command": "claude.exe", "args": ["--append-system-prompt", "…"], "linkable": false }
    ],
    "split": false,
    "restore": true,
    "defaultMode": "locked",
    "tabs": { "items": [], "shown": {} }
  },
  "keybindings": [],
  "logging": {
    "level": "info",
    "heavy": false
  },
  "plugins": {},
  "marketplace": {
    "index": "https://oliverd25.github.io/cabinetos-marketplace/index.json",
    "themes": "https://oliverd25.github.io/cabinetos-marketplace/themes.json",
    "allowInsecure": false
  },
  "update": {
    "check": true,
    "channel": "stable",
    "source": "https://oliverd25.github.io/cabinetos-marketplace/update",
    "allowInsecure": false,
    "autoInstall": true
  }
}
```

(The core writes each profile over several lines; they are shortened here,
and the `claude` profile's note is cut to `…`: the whole text is in
[terminal.md](terminal.md), "Profiles".)

The file is written only when it is missing. A `cabinetos.json` that exists
already keeps its own `terminal.profiles`: if it lists three profiles, it
gets no `claude`. Add the profile by hand
([terminal.md](terminal.md), "Profiles", has the exact JSON to paste).

It also writes `cabinetos.schema.json` next to the file, and brings it up to
date at every start. The `$schema` key points to it, so editors such as
VS Code complete the keys, show what each one means, and mark mistakes
while you type.

## The format

- **Strict JSON**: no comments and no trailing commas. (PLAN.md, open
  question 3, is settled this way for version 1: one parser, and every JSON
  tool can read the file.) UTF-8; a byte-order mark at the start is allowed.
- **Everything is optional.** A missing setting, or a whole missing section,
  gets its default. `{}` is a valid file.
- **Unknown keys are errors.** A typo such as `"dualPan"` must not silently
  do nothing, so the core refuses the file and names the key, the keys it
  expected, and the line and column.

| Setting | Values | Default | Meaning |
|---|---|---|---|
| `$schema` | text | `./cabinetos.schema.json` | For editors only |
| `version` | `1` | `1` | The file format. Other values are refused. |
| `ui.layout` | `classic`, `right`, `rail` | `classic` | Where sidebar, panes and terminal go: sidebar left with the terminal below, the terminal on the right, or a narrow activity rail instead of the sidebar. Three ways: this key; the commands `view.layoutClassic`, `view.layoutRight`, `view.layoutRail` and `view.cycleLayout` (palette: "View: Classic Layout", "View: Terminal on the Right", "View: Activity Rail", "View: Next Layout", Ctrl+K Ctrl+L); and the Layout submenu of the top row's menu, which checks the current one |
| `ui.dualPane` | `true`, `false` | `true` | Two file panes side by side, or one |
| `ui.sidebar` | `true`, `false` | `true` | Show the sidebar |
| `ui.theme` | text | `default` | The colour theme's ID: `<id>.json` in the themes folder ([themes.md](themes.md)). `set_value` refuses a theme with no valid file; a hand edit that names one keeps the theme in effect and reports it with `config_error` |
| `ui.lastPaths` | list of folder paths | empty | The folders the panes showed last, left pane first; the UI opens them again at the next start. Empty: the UI picks. |
| `ui.pinned` | list of folder paths | empty | Folders the user pinned to the sidebar, in the sidebar's order |
| `ui.dockSize.bottom` | pixels, or `null` | `null` | The Tool Dock's height under the panes, as the user last dragged it; `null` gives the design's size. The window keeps it within the design's limits. |
| `ui.dockSize.right` | pixels, or `null` | `null` | The Tool Dock's width beside the panes, the same way |
| `ui.rail` | list of button IDs | empty | The buttons of the activity rail (`ui.layout: rail`), in the order the user put them: `explorer`, `search`, `marketplace`, `terminal`, or the ID of a tool with a sidebar view. Empty: the default order. The window owns it. |
| `ui.sidebarWidth` | pixels, or `null` | `null` | The sidebar's width as the user last dragged it, in every layout since Phase 24; `null` gives the design's width, and a double-click on the divider writes it |
| `ui.sidebarView` | `explorer`, `search`, or a tool's ID | `explorer` | The view the sidebar showed last; the window falls back to `explorer` for one it does not know |
| `ui.sidebarAutoReveal` | `true`, `false` | `true` | The Explorer view follows the active pane's folder. Three ways: this key; the command `sidebar.toggleFollow` (palette: "Sidebar: Follow the Active Pane"); and the pin in the Explorer view's header (rail layout) or the top row menu's "Follow the Active Pane" |
| `ui.columns` | `{ "modified", "type", "size" }` in pixels, or `null` | `null` | The widths of the file panes' Modified, Type and Size columns, as the user last dragged or fitted them ([ui.md](ui.md), "Column widths"). Both panes share them; the Name column takes the rest of the list's width. Each width is a whole number from 24 to 2000, and all three are needed. `null` gives the theme's widths. They win over every theme's column weights and Size width. The window writes them when a drag ends or a fit happens; an edit of the file applies at once. |
| `ui.paneSplit` | a number from 0.2 to 0.8, or `null` | `null` | The left pane's share of the width the two panes have, as the user last dragged the divider between them ([ui.md](ui.md), "The divider between the panes"); `null` makes the panes equal. A share, not pixels, so a resize keeps it; the window holds it to the least width a pane needs for its columns without changing the number. A double-click on the divider and the command `view.equalPanes` write `null`. The core refuses a number outside 0.2 to 0.8 |
| `ui.compactOverlay` | `{ "width", "height" }` in device-independent pixels, or `null` | `null` | The size of the compact overlay (`view.toggleCompactOverlay`, [ui.md](ui.md), "Compact overlay"), as the user last resized it in that mode; the drawer opens that size again. `null` gives 480 by 640. Each side is a whole number from 240 to 4000, and both are needed. The window writes it half a second after the last resize; an edit of the file resizes the drawer at once while it is on. The mode itself is not a setting: nothing in the file says that the window is in it, and it writes no `dualPane`, `sidebar` or `dockSize`. |
| `ui.tabs.left.items`, `ui.tabs.right.items` | list of `{ "path", "locked", "mode" }` | empty | Each pane's tabs, left to right, as the window last saved them; the next start opens them again. A tab's scroll position and find text are kept while the window runs and are not saved. `path` is the folder the tab shows (in the column view, the deepest column's); `locked` (default `false`) keeps the tab on its folder, so opening another folder there opens a new tab. `mode` is `files` (the list; the default, and left out when written) or `columns` (the column view, [ui.md](ui.md) "The column view"); the tab starts in that mode, with its folder as the one column. Empty: the pane opens one tab from `ui.lastPaths`, or as the window decides. |
| `ui.tabs.left.active`, `ui.tabs.right.active` | a number from 0 | `0` | The tab in front, counting from 0. It must name one of the pane's tabs (with none, only `0`): a number past the end is an error that names the pane, for example `ui.tabs.right.active is 3, but the right pane has 2 tabs; it counts from 0`. |
| `panes.showHidden` | `true`, `false` | `false` | Also list hidden and system entries; the panes list again when it changes. Three ways: this key; the command `view.toggleHiddenFiles` (palette: "View: Toggle Hidden Files", Ctrl+K Ctrl+H); and the top row menu's "Show Hidden Files", with the word "hidden" in the status bar while it is on |
| `panes.sort.key` | `name`, `size`, `modified`, `kind`, `extension` | `name` | The order of a listing ([ipc.md](ipc.md), "Listing a directory"); directories always come first. It is the order of a pane that has none of its own: a click on a column heading and Ctrl+F3 to Ctrl+F6 sort a pane by an order of its own, which goes with the tab and does not write this key ([ui.md](ui.md), "A pane's order") |
| `panes.sort.descending` | `true`, `false` | `false` | Reverse the order |
| `panes.selection` | `windows`, `commander` | `windows` | How the keyboard marks rows. `windows`: as in Explorer, a key that moves the cursor selects the row it moves to. `commander`: as in Total Commander, keys that move the cursor keep the marks, Shift with them marks the rows passed over, a new listing starts with nothing marked, and commands act on the marked rows, or on the cursor row when none is marked. The mouse keeps the Windows rules in both |
| `panes.folderSizes` | `true`, `false` | `false` | Measure every folder of a listing when it opens or is listed again, so the Size column shows their sizes without a key ([ui.md](ui.md), "Folder sizes"). Off by default: the walk costs disk time, and a large tree takes a while. A folder that has a size keeps it until the pane leaves the folder. The command `view.toggleFolderSizes` writes it, and an edit of the file applies at once |
| `files.editor` | `null`, or `{ "command", "args" }` | `null` | The program `file.edit` (F4) opens a file with; the file's path is added as the last argument. `null`: Windows' own edit verb for the file's type, else Notepad. `command` is a full path, or a program name found on the `PATH`, as for a terminal profile, never the current folder; it may not be empty. `args` may be left out |
| `programs` | list of `{ "name", "title", "command", "args" }` | empty | Programs of your own that the context menu, a key or the palette starts, each as the command `program.<name>` ("Programs" below) |
| `contextMenu.shellMenu` | `true`, `false` | `false` | Shift+right-click and `menu.showShell` (Ctrl+Shift+F10) open Windows' own menu (Open with, Send to, what other programs add). `false`: they open CabinetOS's menu ("The context menu" below). Three ways: this key; the command `menu.toggleShellMenu` (palette: "Menu: Toggle Windows' Shell Menu"); and the last row of "Edit Menu…", "Show Windows' own menu with Shift+right-click" |
| `contextMenu.<target>.quickActions` | list of command IDs | the menus of Phase 5 | The icon row at the top of the menu, left to right. `<target>` is `background`, `file`, `folder` or `multiSelect` |
| `contextMenu.<target>.items` | list of `{ "command", "extensions" }` or `{ "separator": true }` | the menus of Phase 5 | The menu's rows, top to bottom |
| `terminal.defaultProfile` | a profile `name` | `pwsh` | The shell a new terminal starts with when the client names none; must name one of the profiles |
| `terminal.profiles` | list of `{ "name", "command", "args", "linkable", "hook" }` | pwsh, cmd, wsl, claude | The programs a terminal can run. Names must be unique; `args`, `linkable` and `hook` may be left out. `command` is a full path, or a program name looked up in the `PATH` ([terminal.md](terminal.md)). `linkable`: whether a session of the profile may be linked to its pane ([terminal.md](terminal.md), "Panes and modes"); left out, `true` for PowerShell and WSL (while `hook` is not `false`) and `false` for any other program; `false` for cmd and `claude`, where no prompt hook can be added. `hook`: the prompt hook, the few lines the shell runs each time it draws its prompt ([terminal.md](terminal.md), "The prompt hook"); PowerShell and WSL (bash) only, ignored for any other program. `true` (the default when left out): CabinetOS's own, which makes a linked session follow its pane (one run of `cab term cwd` per prompt) and reports the shell's folder for the header's caption. `false`: none; the session does not follow and reports nothing. A string: your own code, in the shell's language (PowerShell, or bash for WSL), run at each prompt in place of the follow step; the folder report stays, and errors are swallowed. It may use `$env:CABINETOS_SESSION` and `$env:CABINETOS_PIPE` (`$CABINETOS_SESSION` and `$CABINETOS_PIPE` in bash) and `cab term cwd`. A change applies to the next session. `followsPane`, the key of the folder sync before 2026-10-01, is ignored: a file that has it still loads, and the core no longer writes it. |
| `terminal.split` | `true` or `false` | `false` | Whether the Tool Dock is split under the two panes: the left pane's sessions under the left pane, the right pane's under the right (the split mirror, [ui.md](ui.md), "The terminal"). `terminal.toggleSplit` (Ctrl+\ in the terminal, or the palette) flips it; the window writes it when the user toggles, and follows a change of the file at once. It holds only while the dock is under the panes (`ui.layout` `classic` or `rail`): beside them (`right`) the dock shows one view and the setting waits. With one pane shown, the split shows one half, the left's. |
| `terminal.restore` | `true` or `false` | `true` | Whether the terminal tabs come back after a restart: the first time the dock is shown after the window starts (the first Ctrl+Backquote, the terminal button, or Ctrl+Shift+T), the sessions the last run had are started again as fresh shells, each with its profile, folder, pane and mode ([terminal.md](terminal.md), "Restoring the tabs"). Nothing a shell printed comes back. `false`: the window still saves the sessions, and Ctrl+Backquote starts one session as it always did. Read when the dock is first shown, so an edit of the file applies at once. |
| `terminal.defaultMode` | `locked` or `linked` | `locked` | The mode a new session starts in when its client names none (Ctrl+Shift+T, Ctrl+Backquote, the dock's "+", `cabinetos-cli term`): `linked` makes the shell follow its pane through the prompt hook. A profile that is not linkable (`cmd`, `claude`, or a profile that says `"linkable": false`) stays `locked` whatever this says, and a mode the client names wins. The core reads it at each `terminal_open`, so an edit applies to the next session; a running session keeps its mode. |
| `terminal.tabs` | `{ "items", "front", "shown" }` | nothing saved | The terminal's sessions as the window last saved them, so the first show of the dock after a restart can bring them back (`terminal.restore`). The window owns it and writes it whole a second after any change of its tabs (and when it closes); the core only checks and stores it. `items` is a list of `{ "profile", "folder", "pane", "mode" }` in the order of the tabs: the profile's `name` (a profile that is gone falls back to `terminal.defaultProfile`), the shell's folder (the one its prompt hook reported last, else the one it started in; left out: the user's profile folder; a folder that is gone falls back to the user's profile folder), `left` or `right` (default `left`), and `locked` or `linked` (default `locked`). `front` is the index in `items` of the tab in front, and `shown.left` and `shown.right` each pane's own front tab, which the split dock shows in that pane's half; each is left out when none, and each must name one of `items` (an index past the end is an error that names the key, for example `terminal.tabs.front is 2, but 1 terminal tab is saved; it counts from 0`). Only running sessions are saved. An edit of the file while the window runs is not read until the next start. |
| `keybindings` | list of `{ "command", "keys", "when" }` | empty | Changes to key bindings: [keybindings.md](keybindings.md) |
| `logging.level` | `trace`, `debug`, `info`, `warn`, `error` | `info` | The least important level the core writes to its log |
| `logging.heavy` | `true`, `false` | `false` | Heavy logging: every operation is also written, at every level, into `heavy-<process>.<date>.jsonl` files next to the logs, at most 2 GB in all, even when that slows an operation down. On until turned off ([diagnostics.md](diagnostics.md), "Heavy mode") |
| `plugins.<id>.enabled` | `true`, `false` | `true` | Run the Core Plugin with this ID ([plugins.md](plugins.md)) |
| `plugins.<id>.settings` | object | empty | The plugin's own settings: any keys and values, which only the plugin knows. It reads them with `config-get` (`plugins.<id>.settings`, and paths under it); nothing else of `plugins` is readable to a plugin. `cabinetos-cli config set plugins.agent.settings.provider anthropic` writes one, and makes the plugin's entry and the objects on the way when they are missing (the one place a `config set` path may be new). A change is told to the running plugin as the event `settings-changed`; it does not restart it ([plugins.md](plugins.md), "Settings"). The Agent's keys (`provider`, `model`, `base`, `maxTokens`, `tier`, `fakeReplies`): [extensions/agent.md](extensions/agent.md), "Settings". |
| `plugins.<id>.granted` | list of capability names | empty | The capabilities the user granted it, such as `fs:read`. It runs only when it has every capability it asks for. Installing the plugin from the marketplace clears them. |
| `marketplace.index` | an `https:` URL, a `file:` URL, or the path of an `index.json` or of its folder | `https://oliverd25.github.io/cabinetos-marketplace/index.json`, the public index ([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)) | Where the extensions' index is: Core Plugins and Tool Extensions ([marketplace.md](marketplace.md)). The core reads it only when a client asks. A file written before 2026-09-30 may still hold the old placeholder `https://marketplace.cabinetos.invalid/index.json`, which never resolves; remove the line and the public index is used. |
| `marketplace.themes` | an `https:` URL, a `file:` URL, or the path of a `themes.json` or of its folder | `https://oliverd25.github.io/cabinetos-marketplace/themes.json`, the public themes catalogue ([ADR 0022](decisions/0022-two-catalogues-extensions-and-themes.md)) | Where the themes catalogue is, which the theme gallery reads ([marketplace.md](marketplace.md), "The two catalogues"). The core reads it only when a client asks. While the address answers 404 (or the folder has no `themes.json`), the theme items of `index.json` are used instead. A local `marketplace.index` leaves this one at the public address: set both. Like `marketplace.index`, it is an advanced setting: the window has no control for it until the Settings page of Phase 11c, and `cabinetos-cli config set marketplace.themes <address>` writes it. |
| `marketplace.allowInsecure` | `true`, `false` | `false` | Also accept a plain `http:` index and downloads, which anyone on the network could change on the way. For testing only. |
| `update.check` | `true`, `false` | `true` | Look for a newer CabinetOS once a day (10 seconds after the start, then hourly whether a day has passed) and download it in the background ([release.md](release.md), "Updates"). The update command checks at any time either way. Only a per-user install of a release updates itself; a development build and an all-users install never check |
| `update.channel` | `stable`, `preview` | `stable` | Which releases: `stable`, or `preview`, which also offers versions with a pre-release tag such as `0.2.0-preview.1` |
| `update.source` | an `https:` URL, a `file:` URL, or a folder path | `https://oliverd25.github.io/cabinetos-marketplace/update` | The folder that holds one folder per channel, each with its `latest.json` ([sdk/update/latest.schema.json](../sdk/update/latest.schema.json)) |
| `update.allowInsecure` | `true`, `false` | `false` | Also accept a plain `http:` source and download, which anyone on the network could change on the way. For testing only |
| `update.autoInstall` | `true`, `false` | `true` | Install a downloaded version at once, in the background, once its SHA-256 is checked; the window then only says "CabinetOS <version> is installed; restart to use it" in the status bar, with Restart now and Later ([ADR 0018](decisions/0018-setup-file-and-silent-updates.md)). `cabinetos-cli update download` installs too. `false`: the download waits, and a dialog with the release notes asks Restart now or Later before the swap ([ADR 0014](decisions/0014-in-app-updates.md)) |

A pane with tabs, as the window saves it:

```json
"ui": {
  "tabs": {
    "left": {
      "items": [
        { "path": "C:\\Users\\me\\Documents", "locked": false },
        { "path": "E:\\work", "locked": true }
      ],
      "active": 1
    },
    "right": { "items": [], "active": 0 }
  }
}
```

Who uses what:

- `panes`: the core, at once. They are the defaults of every
  `list_directory` request that leaves `include_hidden` or `sort` out; a
  request that sets them wins. Open listings keep the order they were opened
  with.
- `logging.level`: the core, at once. The environment variable
  `CABINETOS_LOG`, when set, wins over it ([diagnostics.md](diagnostics.md)).
- `logging.heavy`: the core, within a second, and the window, which follows
  the core's `config_changed`: its status bar shows a `HEAVY LOG` pill while
  it is on, and the palette command "Diagnostics: Toggle Heavy Logging"
  writes it ([ui.md](ui.md), "Heavy logging"). The environment variable
  `CABINETOS_LOG_HEAVY` (`1` or `0`), when set, wins over it. The chat that
  asked for heavy mode named the key `diagnostics.level`; it lives in the
  existing `logging` section instead.
- `keybindings`: the core compiles the keymap from them and sends it to the
  UI.
- `plugins`: the core, at once. A plugin that is not listed is on, with
  nothing granted. A changed entry starts, stops or restarts that plugin;
  an ID with no installed plugin is kept and noted in the log. A change
  to only a plugin's `settings` does not restart it: the plugin is told.
  Example:
  `"plugins": { "reader": { "granted": ["cmd:register", "fs:read"] } }`.
- `terminal`: the core, at each `terminal_open`: `defaultProfile`,
  `profiles` and `defaultMode`. An edited profile or default applies to
  the next shell; running shells keep what they started with
  ([terminal.md](terminal.md)). `terminal.split` is the window's, at once:
  it splits or joins the dock when the file changes, except in the window
  that wrote it ([ui.md](ui.md), "The terminal"). `terminal.restore` is
  the window's, read when the dock is first shown, and `terminal.tabs` is
  the window's too: it writes it and reads it at start only
  ([terminal.md](terminal.md), "Restoring the tabs").
- `files.editor`: the core, at each `edit_path` ([ipc.md](ipc.md), "Files
  and folders").
- `programs`: the core, at once. Each entry becomes the command
  `program.<name>`, which the palette lists and a key can be bound to; the
  core starts the program when the command runs, with the paths of the
  window's state at that moment. A removed entry's command goes; a key
  bound to it is then left out of the keymap with a warning, as for any
  command that does not exist.
- `contextMenu`: the UI, at the next right-click; it builds the menu from
  the settings it holds and reads no file. `contextMenu.shellMenu` is also
  read by the core, which refuses `shell_menu` with `shell_menu_off` while
  it is off ([ipc.md](ipc.md), "Windows' context menu").
- `panes.selection`: the UI, at once; the core only checks, stores and
  announces it.
- `ui`: the UI (Phase 5). The core only checks, stores and announces it.

### The context menu

`contextMenu` says what the right-click menu shows (Phase 18, [ADR
0015](decisions/0015-user-programs-and-the-shell-menu.md); [ui.md](ui.md),
"The context menu"). It has one entry per target: `background` (the pane's
empty space), `file` (one file), `folder` (one folder) and `multiSelect` (a
row inside a selection of several rows). Each target has:

- `quickActions`: command IDs for the icon row at the top, left to right.
  Each shows its icon, with its title and keys as a tooltip.
- `items`: the rows, top to bottom. A row is `{"command": "<id>"}`, or
  `{"separator": true}` for a divider line. A command row may add
  `"extensions": [".md", ".txt"]`: it then shows only for files with one of
  those extensions (each with its dot; case does not matter). With several
  rows selected, every selected row must be such a file.

A target or a list that is left out keeps its default, so adding a row to
`items` keeps the icon row; a list that is written is exactly that list
(an empty list is empty). The defaults
are the menus of Phase 5:

```json
"contextMenu": {
  "shellMenu": false,
  "background": { "quickActions": [], "items": [
    { "command": "edit.paste" }, { "command": "file.newFolder" }, { "command": "sidebar.pin" } ] },
  "file": { "quickActions": ["edit.cut", "edit.copy", "edit.paste", "file.rename", "file.delete"], "items": [
    { "command": "pane.openSelected" }, { "command": "file.openInOtherPane" },
    { "command": "file.copyToOtherPane" }, { "command": "terminal.new" } ] }
}
```

`folder` and `multiSelect` have the same lists as `file`. After the rows the
window always adds the plugins' file commands (on rows only), Properties,
and "Edit Menu…" (`menu.edit`), so a menu cannot lose them.

"Edit Menu…" edits a target's `items` inside the menu ([ui.md](ui.md),
"Editing the menu"): rows are moved, removed and added, then "Done" has the
core set `contextMenu.<target>.items` to the new list, the same as
`cabinetos-cli config set` would. It writes only that list, in the shape
above: `{"command": "<id>"}`, `{"command": "<id>", "extensions": […]}` with
the row's own extensions untouched, or `{"separator": true}`. The icon row
(`quickActions`), the `extensions` of a row and `programs` are edited here,
in the file. A command ID is not checked against the commands when the file
is read, because a plugin's commands come and go with the plugin: an ID no
command has is left out of the menu, with a warning in the window's log.
The core checks the shape: each row is a command or a divider, not both;
a divider has no `extensions`; an extension is a dot and a name with no
other dot, no folder separator and no spaces around it. A mistake is
reported with its line and where it is, for example
``contextMenu.file.items[1]: `md` is not an extension``.

### Programs

`programs` lists programs of your own, for example an editor:

```json
"programs": [
  { "name": "code", "title": "Open in VS Code", "command": "C:\\Users\\me\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe", "args": ["{selection}"] },
  { "name": "diff", "title": "Compare", "command": "C:\\Tools\\WinMerge\\WinMergeU.exe", "args": ["{selection}"] }
],
"contextMenu": {
  "file": { "items": [ { "command": "pane.openSelected" }, { "command": "program.code" } ] },
  "multiSelect": { "items": [ { "command": "program.diff" } ] }
}
```

- `name`: a lower-case letter, then lower-case letters, digits and `-`.
  It must be unique. The program is the command `program.<name>`, in the
  palette's Programs group; a key can be bound to it in `keybindings`
  ([keybindings.md](keybindings.md)). It has no default keys.
- `title`: what the menu and the palette show; the `name` when it is left
  out or empty.
- `command`: a full path, or a program name found on the `PATH`, as for
  `files.editor` and the terminal profiles; never the current folder. It
  may not be empty.
- `args`: the arguments, each one argument however many spaces it has.
  Three tokens stand for what the window shows when the command runs:
  `{path}` is the active pane's cursor row, `{selection}` its marked rows
  (or the cursor row when none is marked), and `{cwd}` the active pane's
  folder. `{path}` and `{cwd}` may be part of an argument
  (`"--file={path}"`); `{selection}` must be a whole argument, and becomes
  one argument per path. A brace that does not make one of these three
  words is plain text; another word in braces, such as `{file}`, is an
  error of the file.

The core starts the program, never the window: the window sends what it
shows (`window_state`), and the core fills in the tokens from that, finds
the program, and starts it in the active pane's folder through Windows, as
`files.editor` is started. Nothing but a listed program can start this way.
A command line longer than 30,000 characters (Windows takes 32,767; many
selected files make it long) is refused with `command_line_too_long`, and
a token with nothing to stand for (no cursor row, nothing selected) with
`program_refused`. Each refusal is in the core's log with the program's
name ([ipc.md](ipc.md), "Configuration, commands and keybindings").

## Editing by hand

The core watches the directory of the file. When something in it changes,
the core waits until the directory has been quiet for 100 ms, reads the file
and checks it: the JSON, the keys, the values, the terminal profiles, and the
keybindings against the command registry. Then:

- **The file is good.** Its settings take effect, and every client that said
  `hello` gets `config_changed` with the settings that changed, as dotted
  paths such as `ui.layout` or `panes.sort.descending` (an array, such as
  `keybindings`, is one setting). If the keymap changed, `keymap_changed`
  follows with the whole new keymap.
- **The file has an error.** The settings in effect stay as they were, and
  the clients get `config_error` with the line, the column and the message.
  One saved mistake is reported once. When the file is fixed, the new
  settings take effect with a `config_changed`. Its list of changed settings
  is empty when the fix only restores what was already in effect; that
  `config_changed` still tells the UI the error is gone.

Examples of errors, as `cabinetos-cli config validate` prints them:

```text
line 3, column 13: unknown field `dualPan`, expected one of `layout`, `dualPane`, `sidebar`, `theme`, `lastPaths`, `pinned`
line 3, column 24: unknown variant `diagonal`, expected one of `classic`, `right`, `rail`
line 3, column 60: `ctrl+nope` is not a valid key binding: `nope` is not a key name
line 3, column 5: ctrl+b is bound to both view.toggleSidebar and go.toPath
line 3, column 5: palette.show belongs to the Immutable System Tier; its keys cannot be changed (the tier: palette.show, overlay.close, keys.open)
```

A syntax or value error points just after the text at fault; a keybinding
error points at the start of its entry.

The file is read as UTF-8, with or without a byte-order mark (Notepad may
write one), or as UTF-16 with its byte-order mark, which is what Windows
PowerShell 5.1's `>` and `Out-File` write. Any other encoding, such as an
ANSI code page (Windows PowerShell 5.1's `Set-Content` writes that), is an
error that names the first byte that is not UTF-8. When the core writes the
file, it writes UTF-8 without a byte-order mark, and text beyond ASCII as
the characters themselves (`"E:\\Звіт 2026"`, not `\u0417…`), so the file
stays readable. Paths keep their exact characters: `café` spelled
decomposed stays decomposed through `ui.lastPaths`, `ui.pinned` and
`set_value`.

It does not matter how the editor saves. Writing the file in place and
writing a temporary file that then replaces it (as many editors do) are both
seen. A file that is deleted is reported as an error and not recreated; the
settings in effect stay until it is back, or until a `set_keybinding` writes
it anew with those settings. A folder in the file's place is reported once,
as a folder ("it is a folder; the settings need a file there"), and every
change is refused until a file is back.

When the core starts with a file that has an error, it logs the error and
uses the defaults until the file is fixed.

`cabinetos-cli config validate [file]` checks a file the same way without a
core, for example before copying it into place. `cabinetos-cli config show`
prints the settings in effect, `cabinetos-cli config get ui.dualPane` one of
them, and `cabinetos-cli keys watch` prints each change as the core
announces it, with the time since the file was written.

## When the core writes the file

`set_value` (the settings UI and the shell's own state, such as
`ui.lastPaths`, or `cabinetos-cli config set ui.dualPane false`),
`set_keybinding` and `reset_keybinding` (the settings UI, or
`cabinetos-cli keys set` and `keys reset`), and `grant_capabilities` and
`set_plugin_enabled` (the permissions review dialog, or
`cabinetos-cli plugins grant`, `enable` and `disable`) change the file
through the core:

- The core reads the file as it is on disk right now, so an edit saved a
  moment ago is kept. If that edit has an error, the core writes nothing and
  answers with the error code `config_error`: overwriting it would lose the
  user's unfinished work.
- It checks the result as it checks a saved file. A value `set_value`
  cannot put there (a wrong type, a setting that does not exist, a
  `terminal.defaultProfile` no profile has) is refused with
  `config_error`, and the file stays as it was.
- It writes the whole file: two-space indentation, the settings in the fixed
  order shown above, keys in their normal form. Comments cannot be lost,
  because strict JSON has none.
- It writes a temporary file in the same directory, flushes it to the disk,
  and renames it over the old file in one step. No reader ever sees half a
  file.
- It does not report its own write back as a change: it remembers a hash of
  what it wrote, and the watcher finds the same content.

### Two cores on one file

Two windows are two cores, and they share `cabinetos.json`. A change by one
reaches the other as a change on disk: its watcher reloads the file and
its clients get `config_changed`, as for a hand edit.

Two changes at the same moment must not lose one another. Each change
reads the file, applies itself and writes the result; a core that read
before the other one wrote would write the other's change away. So the
core locks `.cabinetos.json.lock`, next to the file, from the read to the
write (`LockFileEx` through Rust's `File::lock`), and the other core waits
for it; the wait is as long as one write. Windows releases the lock when a
process ends, even in a crash, so a core that dies cannot leave the other
waiting. The lock file stays, empty. Tested with two real cores setting
two settings at the same moment, 25 times in a row: before the lock, the
first round already lost one core's change; with it, every round keeps
both, and each core hears the other's.

A hand edit takes no lock; the atomic write above means an editor never
reads half a file, and the rule that the core writes over the file as it is
on disk right now keeps an edit saved a moment before a change.

## The schema

`sdk/config/cabinetos.schema.json` is generated from the Rust types, and a
test fails when it is out of date. After changing a setting, regenerate it
and commit it (from `core/`, in bash):

```bash
CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config
```

The core carries a copy of the schema inside `cabinetos-core.exe` and writes
it next to the configuration file.
