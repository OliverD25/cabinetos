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
  `cabinetos:plugin@0.2.0`.
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
  "apiVersion": "0.2.0",
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
| `apiVersion` | The WIT version the plugin was built against. Its major and minor must match this core's (`0.2`). A `0.1` plugin is refused: 0.2 changed the interface (see "The WIT versions"). |
| `minCoreVersion` | The oldest core it runs on, `major.minor.patch`. |
| `capabilities` | Each known, listed once, with a `reason` the review dialog shows. `fs:read`, `fs:write` and `fs:watch` need `roots`. `net` needs `hosts` and may list `secrets` (see "The network"). `core:request` needs `requests` (see "Asking the core"). The others take none of these. |
| `commands` | Each ID starts with `<id>.`, then letters, digits, `.`, `_` or `-`; each has a `title` and a `category`; `defaultKeys` follow the key grammar ([keybindings.md](keybindings.md)). An optional `input` asks for a line of text first (see "Commands that ask for text"). Declaring commands needs `cmd:register`. |

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
| `config:read` | low | Read settings with `config-get`; of the `plugins` section only the plugin's own `settings` (see "Settings") |
| `events:emit` | low | Send events to the UI with `emit` |
| `fs:read` | medium | Read the files under its `roots` |
| `fs:write` | medium | Read and write the files under its `roots` |
| `fs:watch` | medium | Hear about changes in folders under its `roots` (`watch-folder`); no right to read them |
| `jobs:intercept` | medium | See every job before it starts (`before-job`), and stop it |
| `process:run` | medium | Start programs. **Never granted in this version.** |
| `net` | high | Ask the core for web requests (`http-request`), only to the `hosts` its manifest names, with only the `secrets` it names |
| `core:request` | high | Ask the core to run the request types its manifest lists, as if a client had sent them (`core-request`; see "Asking the core") |
| `credentials` | high | Read stored credentials. **Never granted in this version.** A plugin never reads a secret; `net` lets the core use one for it. |

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
- **A plugin that asks for `process:run` or `credentials` fails** with a
  message saying so. The sandbox has no safe way to allow them yet.
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
| Network (TCP, UDP, name lookup) | No. A plugin with `net` asks the core with `http-request` |
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

Time a call spends waiting for the network in `http-request` does not
count against its deadline: a command that waits 30 s for a model's answer
is not stopped at 5 s. The request has its own timeout (see "The
network"). Fuel is not used while the core makes the request.

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

### Commands that ask for text

A command that needs a line of text from the user, such as a question or
a URL, says so in `plugin.json`:

```json
{
  "id": "fetcher.get",
  "title": "Fetch",
  "category": "Fetcher",
  "defaultKeys": [],
  "input": { "title": "Fetch a URL", "placeholder": "http://localhost:8090/..." }
}
```

- `title` is the prompt's heading (the command's `title` when absent);
  `placeholder` is the grey text in the empty box. Both are optional, and
  1 to 200 characters when given; any other key fails the manifest.
- `list_commands` shows the prompt as the command's `input`
  ([ipc.md](ipc.md), "Configuration, commands and keybindings"), and `cabinetos-cli commands list` marks
  the command `(asks for text)`.
- The window asks, then runs the command with the text as `input`, next to
  `path` and `paths`: `{"input": "https://…", "path": "…", "paths": […]}`.
  Nothing runs when the user cancels. Another client may send `input`
  itself, or leave it out: a plugin must handle a missing `input` as it
  handles a missing `path`.

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

A `steps` job (a preview's renames and creates, or an undo) has no WIT
kind of its own: the plugin sees `delete` when any step goes to the
Recycle Bin, else `move` when any renames or restores, else `copy`
([jobs.md](jobs.md), "Chains and steps").

A plugin that crashes while judging, or does not answer in time, counts as
allowing the job: a broken plugin must not block the user's file work. The
crash itself is handled as above.

## Listings: `on-listing-opened`

When a pane opens a folder under one of a plugin's `fs:read` or `fs:write`
roots, the plugin gets `on-listing-opened(path, entry-count)`, with the path
as the plugin sees it. It is a notification: the listing does not wait for
it. At most 64 notifications wait for one plugin; more are dropped.

## Watching folders: `fs:watch`

A plugin with `fs:watch` may watch folders under its `roots` and hear
about each change in them, for example to act on a new file in an inbox
folder. Watching gives the names of what changed, not the files: reading
them still needs `fs:read` for that folder.

```wit
watch-folder: func(path: string) -> result<_, string>;
unwatch-folder: func(path: string);
export on-event: func(name: string, payload: string);
```

- `watch-folder` takes a Windows path (`C:\Users\me\Inbox`) or its
  sandbox form (`/C:/Users/me/Inbox`). It must be one of the roots or a
  folder under one, with no `.` or `..` in it; anything else is an `err`
  that says why. It watches that folder, not its subfolders.
- Watching a folder twice is one watch. At most 16 folders at once per
  plugin. `unwatch-folder` stops one; it does nothing for a folder that
  is not watched.
- Every watch ends with the plugin's instance: when it stops, crashes, is
  turned off or reloaded. A plugin watches again in `activate`.
- The folders stay free: a watched folder can still be renamed or deleted.
- `activate` gets the expanded roots in `watch-roots`, as Windows paths.

The changes arrive through the export `on-event`, on the plugin's thread,
like `on-listing-opened`: a notification, with the same limits (5 s, and
at most 64 waiting; more are dropped with a warning in the log). The core
gathers the changes of one folder for 200 ms and sends them as one
message, so a burst of changes is at most one message per 200 ms per
folder:

```json
{"path": "C:\\Users\\me\\Inbox",
 "changes": [
   {"kind": "created", "path": "C:\\Users\\me\\Inbox\\a.txt", "old_path": null},
   {"kind": "renamed", "path": "C:\\Users\\me\\Inbox\\b.txt", "old_path": "C:\\Users\\me\\Inbox\\a.txt"}
 ],
 "overflow": false}
```

| `on-event` name | Payload |
|---|---|
| `folder-changed` | `path` (the watched folder), `changes` in the order Windows reported them, and `overflow` |
| `folder-unwatched` | `path` and `message`: the watch ended by itself, for example because the folder was deleted |
| `settings-changed` | `{}`: the plugin's own `settings` changed (see "Settings") |

- `kind` is `created`, `modified`, `removed` or `renamed`; `old_path` is
  set only for `renamed`. A file moved in from another folder is
  `created`; moved away, `removed`. The paths are Windows paths.
- One write often makes Windows report several `modified` changes; the
  same change twice in a row is sent once.
- `overflow` is `true` when changes were lost: Windows had too many at
  once, or the message reached 1,000 changes. The plugin should then read
  the folder again instead of trusting `changes`.

## The network: `net` and `http-request`

A plugin's sandbox has no network. A plugin with `net` asks the core to
make a web request for it. The core checks the request against the
manifest, makes it, and hands back the answer. The manifest names every
host the plugin may reach, and the review dialog shows them:

```json
{
  "name": "net",
  "hosts": ["api.anthropic.com", "localhost:11434"],
  "secrets": ["anthropic"],
  "reason": "Sends your question to the model you chose."
}
```

| Key | Rule |
|---|---|
| `hosts` | At least one. A host name or an IPv4 address, with an optional `:port`; no scheme, path, user or wildcard. Without a port, only the scheme's own port matches (443 for `https`, 80 for `http`). `localhost` and `127.0.0.1` are different hosts. |
| `secrets` | Optional. The names of the stored secrets the plugin may have the core send (letters, digits, `-`, `_`, `.`). |

The function, in the WIT package:

```wit
record web-request {
    method: string,                          // GET, POST, PUT, PATCH, DELETE or HEAD
    url: string,
    headers: list<tuple<string, string>>,
    body: option<list<u8>>,
    secret: option<string>,                  // a name from the manifest's `secrets`
    secret-header: option<string>,           // the header that carries it
    timeout-ms: u32,                         // 0: the longest, 120 s
}
record web-response {
    status: u16,
    headers: list<tuple<string, string>>,
    body: list<u8>,
}
http-request: func(request: web-request) -> result<web-response, string>;
```

- **Only named hosts.** The URL's host and port must match a `hosts`
  entry. Anything else is refused before any connection.
- **`https:` only**, except plain `http:` to `localhost` and `127.0.0.1`
  (a local model server). Certificates are checked against the Windows
  certificate store, with the marketplace's HTTP stack (`ureq` over
  `rustls`). A URL with a user name or password is refused.
- **No redirects.** A `3xx` answer comes back to the plugin as it is: a
  redirect could lead to a host the manifest does not name.
- **Secrets stay in the core.** When `secret` is set, the core reads that
  secret from the Windows Credential Manager ([ipc.md](ipc.md), "Secrets")
  and puts it into the header `secret-header` names: as it is for a header
  such as `x-api-key`, and as `Bearer <value>` for `Authorization`. The
  plugin never sees the value. A secret the manifest does not list is
  refused; a listed secret that is not stored is an error that says how to
  store it (`cabinetos-cli secret set <name>`). A header the plugin sets
  with the same name is dropped; so are `host` and `content-length`, which
  the core sets.
- **Limits.** The answer's body is at most 8 MiB; a longer one is an
  error. `timeout-ms` is capped at 120 s, and connecting may take 15 s.
  There is no streaming in this version: the plugin gets the whole answer
  at once.
- **Any status is an answer.** A `404` or a `500` comes back as
  `ok(web-response)`; `err` means the request did not happen or did not
  finish, with the reason as text.
- **One log line per request**, at INFO, with the plugin's ID: the host,
  the method, the status, the body's size in bytes and the time in
  milliseconds (`message` `web request`). Never a header, a body or a
  secret.

Without `net`, `http-request` answers `err` and makes no request.

## Asking the core: `core:request` and `core-request`

A plugin may ask the core to run a request as if a client had sent it: to
read what the window shows, to propose changes as a preview, to apply a
preview, to undo a job, to search. The manifest lists the request types,
and the review dialog shows the list under the capability's reason:

```json
{
  "name": "core:request",
  "requests": ["get_window_state", "preview_listing", "preview_apply", "preview_cancel", "undo_job", "search"],
  "reason": "Proposes the changes you asked for and shows them before anything changes."
}
```

```wit
core-request: func(json: string) -> result<string, string>;
```

- **The request** is one JSON object in the protocol's own form
  ([ipc.md](ipc.md)), `{ "type": "preview_listing", "title": …, "rows": … }`,
  without `id` and `trace`: the core sets both, and the trace is the one of
  the action the plugin's call belongs to, so `cabinetos-cli log trace`
  shows what the plugin asked inside the user's action. An `id` or `trace`
  the plugin writes is dropped. At most 1 MiB.
- **Only listed types pass.** `requests` needs at least one entry, each a
  request type this core has, each once. A type the manifest does not list
  is refused with an error that names it. These are never allowed,
  whatever the manifest lists (the list is `NEVER_ALLOWED` in
  `cabinetos-plugins/src/policy.rs`, with a test):

  | Request | Why never |
  |---|---|
  | `hello`, `window_state` | They make a plugin a window |
  | `shutdown` | Ends the core |
  | `set_value`, `grant_capabilities` | They write the settings, which include the grants: a plugin could give itself what the user did not |
  | `secret_set`, `secret_get`, `secret_delete`, `secret_list` | A plugin never sees a secret; `net` lets the core use one for it |
  | `save_log_bundle` | Writes the user's logs into a zip |
  | `execute_command` | Runs any command; one of the plugin's own would wait for the plugin's thread, which waits for the answer |
  | `install_extension`, `uninstall_extension` | Change the code the core runs |

  A manifest that lists one of these still loads (the core logs a warning),
  and the request is refused when the plugin sends it.
- **The core runs it in the plugin's name.** The client is `plugin:<id>`;
  previews are counted per client (at most 20 alive), and a preview a plugin
  proposes belongs to it. The plugin does not need `hello`.
- **The answer** is the core's reply as JSON, as a client would read it,
  `id` and `trace` included. An `error` reply is an answer, like an HTTP
  status: `{"type":"error","code":"no_window",…}`. `err` means the request
  was refused, was not valid, or did not finish (the core answers within
  30 s), with the reason as text.
- **A reply that carries a shared-memory listing** (`preview_opened`,
  `listing_opened`) comes back with its description only, `section_handle`
  0: the plugin cannot map the section and does not need to; the window
  opens a preview with `open_preview` when it sees the preview's ID.
  `list_directory` from a plugin never watches.
- **Waiting counts against no deadline**, like waiting for the network in
  `http-request`: a search may take seconds. Fuel is not used meanwhile.
- **Heavy mode** logs each call as a `core-request` host call at
  `heavy::plugins`, with the request (secrets masked).

Without `core:request`, `core-request` answers `err` and asks nothing.

## Settings

A plugin's entry in the `plugins` section of `cabinetos.json` has an open
object, `settings`, for what the plugin wants the user to set
([config.md](config.md)):

```json
"plugins": { "agent": { "granted": ["cmd:register", "config:read"], "settings": { "provider": "anthropic", "tier": 2 } } }
```

- **The core does not know the keys.** It checks only that `settings` is an
  object. The schema says so (`additionalProperties: true`); `enabled` and
  `granted` keep their strict checks.
- **The plugin reads them with `config-get`** (capability `config:read`):
  `plugins.<its id>.settings` answers the object (`{}` when there is none)
  and `plugins.<its id>.settings.provider` one value, as JSON. Nothing else
  of the `plugins` section is readable to a plugin: not its own `granted`
  or `enabled`, and not another plugin's entry (`policy::may_read_config`).
  A list is one value; there is no path into it by index.
- **The user writes them** by hand, in the settings screen, or with
  `cabinetos-cli config set plugins.agent.settings.provider anthropic`.
  `set_value` makes what is missing on the way: the plugin's entry, its
  `settings` and objects under it. Nowhere else may a path be new.
- **A change is told to the plugin.** When only `settings` changed, the
  running plugin keeps running (its state stays) and gets
  `on-event("settings-changed", "{}")`; it reads what it needs again. A
  change to `enabled` or `granted` starts, stops or restarts it as before.
- A plugin cannot write its settings (`set_value` is never allowed for it).
  What it wants to keep, it keeps in its own folder.

## Host functions

| Function | Needs | What it does |
|---|---|---|
| `register-command(id, title, category, default-keys)` | `cmd:register` | Registers a declared command; only during `activate` |
| `log(level, message)` | — | Writes a line to the core's log with the plugin's ID |
| `config-get(path)` | `config:read` | A setting as JSON by dotted path, such as `ui.theme`; none for a path that does not exist, and for the `plugins` section except the plugin's own `settings` (see "Settings") |
| `emit(name, payload)` | `events:emit` | Sends `plugin_event` with the plugin's ID to every client that said `hello` |
| `http-request(request)` | `net` | Makes a web request to a host the manifest names, and answers with its status, headers and body (see "The network") |
| `core-request(json)` | `core:request` | Runs one request as if a client had sent it, in the plugin's name; only the types the manifest lists (see "Asking the core") |
| `watch-folder(path)` | `fs:watch` | Starts watching a folder under the plugin's roots; changes come to `on-event` (see "Watching folders") |
| `unwatch-folder(path)` | `fs:watch` | Stops watching a folder |

### The WIT versions

| Version | What changed |
|---|---|
| `0.1.0` | The first interface: commands, `log`, `config-get`, `emit`, `before-job`, `on-listing-opened` |
| `0.2.0` | The manifest's `input` for commands (no WIT change). `core-request` and the capability `core:request`, and the event `settings-changed`, were added to `0.2.0` after it first shipped: a function added to the host interface is compatible, since a component built before does not import it and still loads, so the version stayed (checked: the fixtures built before it run on this core unchanged). A plugin that imports `core-request` needs a core that has it; `minCoreVersion` says so. `http-request` and its records; `watch-folder`, `unwatch-folder`, `watch-roots` in `activation`, and the export `on-event`, which every plugin now implements (an empty body is fine). A `0.1` plugin is refused with `apiVersion 0.1.0 does not match this core's plugin interface 0.2.0`; rebuild it against `sdk/wit` and set `apiVersion` to `0.2.0`. |

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
- `process:run` and `credentials` are never granted.
- `http-request` has no streaming: a model's answer arrives whole.
- Plugins cannot open listings, start jobs or read the selection yet; the
  WIT grows with the features that need it, as a new minor version.
