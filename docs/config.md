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
      { "name": "pwsh", "command": "pwsh.exe", "args": ["-NoLogo"] },
      { "name": "cmd", "command": "cmd.exe", "args": [] },
      { "name": "wsl", "command": "wsl.exe", "args": [] }
    ]
  },
  "keybindings": [],
  "logging": {
    "level": "info"
  },
  "plugins": {},
  "marketplace": {
    "index": "https://marketplace.cabinetos.invalid/index.json",
    "allowInsecure": false
  }
}
```

(The core writes each profile over several lines; they are shortened here.)

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
| `ui.layout` | `classic`, `right`, `rail` | `classic` | Where sidebar, panes and terminal go: sidebar left with the terminal below, the terminal on the right, or a narrow activity rail instead of the sidebar |
| `ui.dualPane` | `true`, `false` | `true` | Two file panes side by side, or one |
| `ui.sidebar` | `true`, `false` | `true` | Show the sidebar |
| `ui.theme` | text | `default` | The colour theme's ID: `<id>.json` in the themes folder ([themes.md](themes.md)). `set_value` refuses a theme with no valid file; a hand edit that names one keeps the theme in effect and reports it with `config_error` |
| `ui.lastPaths` | list of folder paths | empty | The folders the panes showed last, left pane first; the UI opens them again at the next start. Empty: the UI picks. |
| `ui.pinned` | list of folder paths | empty | Folders the user pinned to the sidebar, in the sidebar's order |
| `ui.dockSize.bottom` | pixels, or `null` | `null` | The Tool Dock's height under the panes, as the user last dragged it; `null` gives the design's size. The window keeps it within the design's limits. |
| `ui.dockSize.right` | pixels, or `null` | `null` | The Tool Dock's width beside the panes, the same way |
| `panes.showHidden` | `true`, `false` | `false` | Also list hidden and system entries |
| `panes.sort.key` | `name`, `size`, `modified`, `kind` | `name` | The order of a listing ([ipc.md](ipc.md), "Listing a directory"); directories always come first |
| `panes.sort.descending` | `true`, `false` | `false` | Reverse the order |
| `terminal.defaultProfile` | a profile `name` | `pwsh` | The shell a new terminal starts with when the client names none; must name one of the profiles |
| `terminal.profiles` | list of `{ "name", "command", "args" }` | pwsh, cmd, wsl | The shells a terminal can run. Names must be unique; `args` may be left out. `command` is a full path, or a program name looked up in the `PATH` ([terminal.md](terminal.md)). |
| `keybindings` | list of `{ "command", "keys", "when" }` | empty | Changes to key bindings: [keybindings.md](keybindings.md) |
| `logging.level` | `trace`, `debug`, `info`, `warn`, `error` | `info` | The least important level the core writes to its log |
| `plugins.<id>.enabled` | `true`, `false` | `true` | Run the Core Plugin with this ID ([plugins.md](plugins.md)) |
| `plugins.<id>.granted` | list of capability names | empty | The capabilities the user granted it, such as `fs:read`. It runs only when it has every capability it asks for. Installing the plugin from the marketplace clears them. |
| `marketplace.index` | an `https:` URL, a `file:` URL, or the path of an `index.json` or of its folder | `https://marketplace.cabinetos.invalid/index.json`, a placeholder that never resolves | Where the marketplace index is ([marketplace.md](marketplace.md)). The core reads it only when a client asks. |
| `marketplace.allowInsecure` | `true`, `false` | `false` | Also accept a plain `http:` index and downloads, which anyone on the network could change on the way. For testing only. |

Who uses what:

- `panes`: the core, at once. They are the defaults of every
  `list_directory` request that leaves `include_hidden` or `sort` out; a
  request that sets them wins. Open listings keep the order they were opened
  with.
- `logging.level`: the core, at once. The environment variable
  `CABINETOS_LOG`, when set, wins over it ([diagnostics.md](diagnostics.md)).
- `keybindings`: the core compiles the keymap from them and sends it to the
  UI.
- `plugins`: the core, at once. A plugin that is not listed is on, with
  nothing granted. A changed entry starts, stops or restarts that plugin;
  an ID with no installed plugin is kept and noted in the log. Example:
  `"plugins": { "reader": { "granted": ["cmd:register", "fs:read"] } }`.
- `terminal`: the core, at each `terminal_open`. An edited profile applies
  to the next shell; running shells keep what they started with
  ([terminal.md](terminal.md)).
- `ui`: the UI (Phase 5). The core only checks, stores and announces it.

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

## The schema

`sdk/config/cabinetos.schema.json` is generated from the Rust types, and a
test fails when it is out of date. After changing a setting, regenerate it
and commit it (from `core/`, in bash):

```bash
CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config
```

The core carries a copy of the schema inside `cabinetos-core.exe` and writes
it next to the configuration file.
