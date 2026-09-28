# Core Plugins

A Core Plugin is a headless WebAssembly component that the core runs in a
sandbox. It can add commands, judge jobs before they start, hear about
folders the user opens, write to the log and send events to the UI. It has
no UI of its own: that is the other layer, Tool Extensions, which arrive
with the Tool Dock in Phase 5. Constitution Articles 8 (Sandboxed
Extensibility), 10 (The Zero-Bloat Foundation) and 11 (Bifurcated Extension
Architecture); brief §6 and §8 ("Plugin Trap Handling").

The host is the crate `core/crates/cabinetos-plugins`. The interface is
[sdk/wit/plugin.wit](../sdk/wit/plugin.wit). How to write and build a
plugin: [sdk/templates/README.md](../sdk/templates/README.md).

## At a glance

- A plugin is a folder `<plugins folder>\<id>\` with two files:
  `plugin.json` (the manifest) and `plugin.wasm` (the component).
- It implements the world `core-plugin` of the WIT package
  `cabinetos:plugin@0.1.0`.
- It runs in `wasmtime`, in its own sandbox, on its own thread, with a time,
  fuel and memory budget for every call.
- It starts only when it is turned on and the user granted every
  capability it asks for.
- When it crashes, the core logs it, drops the instance, removes its
  commands and goes on.

## Folders

| What | Default | Command-line flag of `cabinetos-core` | Environment variable |
|---|---|---|---|
| The plugins | `%LOCALAPPDATA%\CabinetOS\plugins\<id>\` | `--plugins-dir <path>` | `CABINETOS_PLUGINS_DIR` |
| Each plugin's own folder | `%LOCALAPPDATA%\CabinetOS\plugins-data\<id>\` | `--plugins-data-dir <path>` | `CABINETOS_PLUGINS_DATA_DIR` |

The flag wins over the variable. The core reads the plugins folder, and
writes it only to install or uninstall from the marketplace. It creates a
plugin's own folder the first time the plugin starts. A plugin is installed
from the marketplace (`install_extension`, [marketplace.md](marketplace.md)),
which checks its SHA-256 and leaves it waiting for review; or by copying
its folder into the plugins folder, and the core finds it at its next
start, or at once with `reload_plugin`.

## The manifest: `plugin.json`

```json
{
  "id": "reader",
  "name": "Reader",
  "version": "0.1.0",
  "author": "CabinetOS tests",
  "description": "Reads the size of files in one folder.",
  "apiVersion": "0.1.0",
  "minCoreVersion": "0.1.0",
  "capabilities": [
    { "name": "cmd:register", "reason": "Adds the Size command." },
    {
      "name": "fs:read",
      "roots": ["%TEMP%\\cabinetos-plugins-test\\reader"],
      "reason": "Reads the size of files in its test folder."
    }
  ],
  "commands": [
    { "id": "reader.size", "title": "File Size", "category": "Reader", "defaultKeys": [] }
  ]
}
```

| Key | Rule |
|---|---|
| `id` | 1 to 64 lower case letters, digits and `-`, starting with a letter. It must be the folder's name. |
| `name`, `author`, `description` | Not empty. `name` is the badge on the plugin's commands. |
| `version` | `major.minor.patch`, numbers only. |
| `apiVersion` | The WIT version the plugin was built against. Its major and minor must match this core's (`0.1`). |
| `minCoreVersion` | The oldest core it runs on, `major.minor.patch`. |
| `capabilities` | Each known, listed once, with a `reason` the review dialog shows. `fs:read` and `fs:write` need `roots`; the others take none. |
| `commands` | Each ID starts with `<id>.`, then letters, digits, `.`, `_` or `-`; each has a `title` and a `category`; `defaultKeys` follow the key grammar ([keybindings.md](keybindings.md)). Declaring commands needs `cmd:register`. |

The core reads the manifest strictly. An unknown key, a missing key or a
bad value stops the plugin in state `failed`, with a message that names the
file and the problem, for example:

```text
C:\…\plugins\hello\plugin.json: unknown field `colour`, expected one of `id`, `name`, …
```

A root may use environment variables as `%NAME%`, for example
`%USERPROFILE%\Documents`. After that it must be an absolute path, and the
folder must exist when the plugin starts.

## Capabilities

| Capability | Level | What it allows |
|---|---|---|
| `cmd:register` | low | Register the commands declared in `plugin.json`, during `activate` |
| `config:read` | low | Read settings with `config-get` (except the `plugins` section) |
| `events:emit` | low | Send events to the UI with `emit` |
| `fs:read` | medium | Read the files under its `roots` |
| `fs:write` | medium | Read and write the files under its `roots` |
| `jobs:intercept` | medium | See every job before it starts (`before-job`), and stop it |
| `process:run` | medium | Start programs. **Never granted in this version.** |
| `net` | high | Reach the network. **Never granted in this version.** |
| `credentials` | high | Read stored credentials. **Never granted in this version.** |

The level is the color of the dot in the design's permissions review dialog
(view C): low green, medium yellow, high red.

- **A plugin runs only with every capability it asks for.** Until the user
  grants them all, its state is `needs_review` and it lists what is
  missing. There is no partial start: a plugin cannot be ready for a
  capability it may not have.
- **Grants live in the configuration file**, in the `plugins` section
  ([config.md](config.md)). `grant_capabilities` (the review dialog, or
  `cabinetos-cli plugins grant`) writes them there; an edit saved by hand
  works the same way. Either takes effect at once, without a restart.
- **A plugin that asks for `process:run`, `net` or `credentials` fails**
  with a message saying so. The sandbox has no safe way to allow them yet.
- Without a capability, a host function does nothing: `config-get` answers
  none, `emit` is dropped. `register-command` without `cmd:register` stops
  the start (see "Commands").

A plugin's own folder is always readable and writable, with no capability:
it is where the plugin keeps its state.

## The sandbox

Each instance has its own WASI Preview 2 context:

| What | The plugin gets |
|---|---|
| Clocks and random numbers | Yes |
| Network (TCP, UDP, name lookup) | No |
| Environment variables | None |
| Command-line arguments | None |
| Standard input | Empty |
| Standard output and error | Captured into the core's log, one line at a time |
| Files | Only these folders: `/data` (its own folder, read and write), each `fs:write` root (read and write), each `fs:read` root (read only) |

Inside the sandbox a Windows path `C:\a\b` is `/C:/a/b`. `activate` passes
the plugin its folders in that form, and `on-listing-opened` passes paths
in it too. A job's paths (`before-job`) stay Windows paths, because the
plugin only judges them.

**Output.** Each line the plugin writes to stdout goes into the core's log
at INFO, and each line to stderr at WARN, with `fields.stream` `stdout` or
`stderr`. Like every line the plugin causes, they carry its `plugin_id` and
the boundary `plugin` ([diagnostics.md](diagnostics.md)). A line longer
than 16 KiB is split.

## Limits

Every call into a plugin gets a fresh budget:

| Limit | Value | When it runs out |
|---|---|---|
| Wall-clock deadline | 5 s for starting (`info`, `activate`), `on-command` and `on-listing-opened`; 500 ms for `before-job` | The call traps (`interrupt`); the plugin crashes |
| Fuel | 5,000,000,000 units per call, about one per WebAssembly instruction | The call traps (`all fuel consumed`); the plugin crashes |
| Linear memory | 256 MiB per instance | The call traps with the amount it asked for; the plugin crashes |
| Instances, tables and memories | 32 of each per component | Instantiation fails |
| Table elements | 100,000 per table | The table does not grow |

`deactivate` gets 1 s; the plugin stops either way. A trap while the
plugin starts makes it `failed`, not `crashed`: it never ran, so it is not
started again on its own.

The deadline uses `wasmtime`'s epoch interruption: a ticker thread advances
the engine's epoch every 10 ms while any plugin call runs, and sleeps when
none does. Fuel bounds work the same way on every machine; the deadline
catches what fuel cannot, such as a loop that waits on the clock. A
command still running 2 s after its deadline is stuck inside a host
function (the epoch stops only running WebAssembly); the host gives up on
that instance, marks it crashed and lets its thread end on its own.

A component needs several core instances (its module, the canonical ABI
glue, and for Rust the WASI adapter), so the instance limit is 32, not 1.

## Crashes

A trap in any call, including running out of time, fuel or memory, or an
error raised by a host function (such as `register-command` called after
`activate`), crashes the plugin:

1. The core writes an ERROR line with the `plugin_id`, the message, and in
   `fields.details` the full error with the WebAssembly backtrace. The
   frames have names when the component keeps its name section, as the
   template's release profile does (`strip = "debuginfo"`), for example
   `crashy.wasm!<crashy::Crashy as crashy::Guest>::on_command`.
2. It drops the instance: its store, its memory and its thread go.
3. The plugin's state becomes `crashed`, with the message and the time.
4. Every client that said `hello` gets `plugin_crashed`, then
   `plugin_state_changed`.
5. Its commands leave the registry. `execute_command` for one of them
   answers `unknown_command`, with the reason:

   ```text
   no command `crashy.crash` is registered: its plugin crashy is crashed: …
   ```

6. The call that crashed answers `plugin_error`. The core goes on.

The message says what happened and what the plugin last wrote to stderr; a
Rust panic writes its reason there:

```text
wasm trap: wasm `unreachable` instruction executed; it said: panicked at src/lib.rs:30:9: crashy was asked to crash
```

**Restarts.** 5 s after a crash the core starts the plugin again. A plugin
that crashed three times within ten minutes stays crashed until
`reload_plugin`, which also forgets its crashes. A plugin that crashes at
every call therefore costs three short outages, not a loop.

## States

| State | Meaning |
|---|---|
| `loading` | Being compiled and started |
| `active` | Running; its commands are registered |
| `disabled` | Turned off in the configuration |
| `needs_review` | Asks for capabilities not granted yet (`missing` lists them) |
| `failed` | Cannot start: a bad manifest, a missing or broken component, a never-granted capability, a missing root folder, or `activate` failed or trapped |
| `crashed` | A call trapped; the instance is gone (`message`, `at_ms`) |

Every change is announced with `plugin_state_changed`. `reload_plugin`
reads `plugin.json` and `plugin.wasm` from disk again; use it after
changing either file.

## Commands

- A plugin declares its commands in `plugin.json` and registers them with
  `register-command` during `activate`. Registering a command it did not
  declare, or without `cmd:register`, fails the start; registering after
  `activate` crashes the plugin.
- Its commands appear in `list_commands` and the palette with
  `source` `{"kind":"plugin","id":"reader","name":"Reader"}`; the name is
  the badge.
- A default key that clashes with a binding in use is dropped with a
  warning in the log; the command stays, without keys. The user can bind it
  in `keybindings` like any command.
- A plugin whose ID equals a core category (`view`, `file`, …) cannot shadow
  a core command: a clashing ID is left out with a warning.
- `execute_command` runs `on-command` on the plugin's thread. `args` go in
  as JSON (`{}` when the request has none). The plugin's `ok` text must be
  JSON; it comes back as `command_result`. Its `err` text comes back as
  `plugin_error`: `reader.size failed: …`.

### What the shell passes

When the user runs a plugin's command from the window, the shell sends
the files it concerns as `args`:

```json
{"path": "C:\\Users\\me\\report.pdf", "paths": ["C:\\Users\\me\\report.pdf", "C:\\Users\\me\\notes.txt"]}
```

- `path` is the entry the user right-clicked, when the command comes from
  the context menu, or the focused row of the active pane, when it comes
  from the palette or a key.
- `paths` is the selection of that pane, in its order.
- Both are full Windows paths. Either key may be missing: an empty folder
  has no focused row, and nothing may be selected. A plugin must treat a
  missing key as "no selection" and answer sensibly (with an `err` that
  says what to select, for example), not fail on it.
- A command run by another client, such as `cabinetos-cli commands exec`,
  gets whatever that client sends, often `{}`.

The paths reach the plugin as text; reading the files still needs
`fs:read` for their folder.

## Jobs: `before-job`

A plugin with `jobs:intercept` sees every job after the scan has counted
its files and bytes, and before anything is written:

```wit
record job-summary {
    job-id: u64,
    kind: job-kind,          // copy, move, delete, delete-permanently
    sources: list<string>,   // Windows paths
    destination: option<string>,
    files-total: u64,
    bytes-total: u64,
}
```

`deny(reason)` stops the job before it creates anything, not even its
destination folder. The job ends `failed` with the message
`denied by plugin <id>: <reason>`. When several plugins judge a job, the
first refusal wins.

A plugin that crashes while judging, or does not answer in time, counts as
allowing the job: a broken plugin must not block the user's file work. The
crash itself is handled as above.

## Listings: `on-listing-opened`

When a pane opens a folder under one of a plugin's `fs:read` or `fs:write`
roots, the plugin gets `on-listing-opened(path, entry-count)`, with the path
as the plugin sees it. It is a notification: the listing does not wait for
it. At most 64 notifications wait for one plugin; more are dropped.

## Host functions

| Function | Needs | What it does |
|---|---|---|
| `register-command(id, title, category, default-keys)` | `cmd:register` | Registers a declared command; only during `activate` |
| `log(level, message)` | — | Writes a line to the core's log with the plugin's ID |
| `config-get(path)` | `config:read` | A setting as JSON by dotted path, such as `ui.theme`; none for a path that does not exist, and for the `plugins` section |
| `emit(name, payload)` | `events:emit` | Sends `plugin_event` with the plugin's ID to every client that said `hello` |

## The protocol

`list_plugins`, `reload_plugin`, `set_plugin_enabled`,
`grant_capabilities`, and the events `plugin_state_changed`,
`plugin_crashed` and `plugin_event`: [ipc.md](ipc.md), "Plugins".

## The command line

```text
cabinetos-cli plugins list [--json]
cabinetos-cli plugins grant <id> <capability>...
cabinetos-cli plugins enable <id>
cabinetos-cli plugins disable <id>
cabinetos-cli plugins reload <id>
cabinetos-cli commands exec <command> [json-args]
cabinetos-cli events watch
```

`grant`, `enable`, `disable` and `reload` wait until the plugin has left
`loading` and print its state. `events watch` prints every event as one
JSON line until Ctrl+C.

## Not yet

- The permissions review dialog and the Tool Dock are UI work (Phase 5);
  today the CLI grants. The design's "Trust {author} for future updates"
  needs publisher identities (Phase 9) and is not built.
- There is no way to revoke one capability except editing `granted` in the
  file.
- `process:run`, `net` and `credentials` are never granted.
- Plugins cannot open listings, start jobs or read the selection yet; the
  WIT grows with the features that need it, as a new minor version.
