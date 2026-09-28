# The UI–core bridge (IPC)

How a client (the WinUI 3 UI, or `cabinetos-cli` today) talks to
`cabinetos-core.exe`. Brief §4; format decision in
[ADR 0006](decisions/0006-control-channel-json.md). The Rust types in
`core/crates/cabinetos-protocol` are the source of truth; their JSON Schema is
in [sdk/protocol/](../sdk/protocol/) (`request`, `response` and `event`
schemas).

There are two channels:

- **The control channel**: a named pipe carrying small JSON messages
  (requests, replies, events).
- **The data channel**: shared-memory sections carrying directory listings,
  which the client reads by pointer, with no copy and no parsing.

## The pipe

- Name: `\\.\pipe\cabinetos-core-<token>`. The UI starts the core with
  `--pipe <random token> --parent-pid <its own PID>`; the core exits when that
  process exits. For development the token is `dev`.
- Only the user who started the core can open the pipe: its access list
  (DACL) has one entry, for that user. Remote (network) clients are refused.
- Framing: every message is a 4-byte little-endian length, then that many
  bytes of UTF-8 JSON. A frame may be at most 16 MiB; a longer one gets a
  `frame_too_large` error and the connection is closed. Large data belongs in
  shared memory.
- Every message is one flat JSON object with an `id` (a ULID) and a `type`:

  ```json
  {"id":"01M3JP62MKWQQNAMJMXY6XDEY1","type":"ping"}
  ```

  A reply carries the ID of its request. An event carries a fresh ID of its
  own. A client tells replies from events by `type`; no event type is also a
  reply type.

## The handshake

A client that wants listings or events says `hello` first, once per
connection:

```json
{"id":"01M…","type":"hello","client_pid":4242,"client_name":"CabinetOS"}
{"id":"01M…","type":"welcome","protocol_version":3,"core_version":"0.1.0"}
```

`client_pid` must be the process on the other end of the pipe; the core asks
Windows and refuses a mismatch with `protocol_error`. The core needs the PID
because it duplicates shared-memory handles into that process. A
`list_directory` before `hello` fails with `protocol_error`, message
`hello required`. From `hello` on, the connection also receives the
configuration events (`config_changed`, `config_error`, `keymap_changed`).
Every other request works without `hello`.

Protocol version 3 (Phase 3) added the configuration, command and keymap
messages, and made the `list_directory` options `include_hidden` and `sort`
optional.

## Requests and replies

| Request | Fields | Reply |
|---|---|---|
| `ping` | — | `pong` (`protocol_version`, `core_version`) |
| `shutdown` | — | `ok`, then the core exits with code 0 |
| `hello` | `client_pid`, `client_name` | `welcome` (`protocol_version`, `core_version`) |
| `list_directory` | `path`; `include_hidden` and `sort` (when left out, the `panes` settings of [config.md](config.md) decide: by default `false` and `{"key":"name","descending":false}`); `watch` (default `false`) | `listing_opened` |
| `close_listing` | `listing_id` | `ok` |
| `volume_info` | `path` (need not exist) | `volume_info` |
| `get_config` | — | `config` (`path`, `config`) |
| `get_keymap` | — | `keymap` |
| `list_commands` | — | `commands` |
| `search_commands` | `query`; `limit` (default 20) | `search_results` (`hits`) |
| `execute_command` | `command`; `args` (default `null`) | `command_result` or `command_routed` |
| `set_keybinding` | `command`, `keys` (`""` for none) | `keymap` |
| `reset_keybinding` | `command` | `keymap` |

Any request can instead get `error` with a `code` and a `message`:

| Code | Meaning |
|---|---|
| `unknown_request` | The envelope is valid, but this core does not know its `type` (a newer client). The reply keeps the request's ID. |
| `protocol_error` | Not a valid envelope (bad JSON, missing or malformed `id`, wrong fields), or out of order (`list_directory` before `hello`, a second `hello`, a wrong `client_pid`). |
| `frame_too_large` | The frame was over 16 MiB. The connection is closed after this reply. |
| `internal` | The core failed while handling a valid request. |
| `not_found` | The path does not exist. |
| `access_denied` | Windows denied access. |
| `invalid_path` | The path is malformed, or names a file where a directory is needed. |
| `no_such_listing` | No open listing on this connection has that `listing_id`. |
| `io` | Reading from the disk or the network failed. |
| `unknown_command` | No command has that ID. |
| `not_implemented` | The command exists, but the core cannot run it yet; it arrives in a later phase. |
| `invalid_keys` | The keys do not follow the key grammar ([keybindings.md](keybindings.md)). |
| `keybinding_conflict` | The keys are taken by another command in the same context, or a combination would be both a binding and the start of a chord. |
| `immutable_binding` | The change touches the Immutable System Tier. |
| `config_error` | The configuration file cannot be changed now: it has an error the user must fix first, or it cannot be written. |

Requests on one connection are independent: `list_directory`,
`volume_info`, `set_keybinding` and `reset_keybinding` run in the
background, so a slow directory does not hold up the next request, and their
replies may come in any order. Match replies to requests by `id`.

## Listing a directory

```json
{"id":"01M…","type":"list_directory","path":"C:\\Users\\me\\Pictures",
 "include_hidden":false,"sort":{"key":"name","descending":false},"watch":true}
{"id":"01M…","type":"listing_opened","listing_id":7,"section_handle":1188,
 "section_size":8484488,"entry_count":100000,"generation":1,"elapsed_us":67310}
```

1. The core reads the whole directory with `NtQueryDirectoryFile` (names,
   file IDs, sizes, times and attributes in one pass), sorts it, writes it
   into a new shared-memory section, and duplicates the section handle into
   the client's process.
2. `section_handle` is that handle's value, already valid in the client. The
   client owns it: it maps the section (read-only is enough), reads it, and
   closes the handle when done (`CloseHandle` in C#; dropping the section in
   Rust).
3. `section_size` is the number of bytes that hold the listing.
   `elapsed_us` is the core's time to read, sort and write it.
4. The listing is complete when the reply arrives. (Publishing in chunks
   while reading may come later, if measurements ask for it.)
5. `close_listing` releases the core's side: its handle to the section and,
   for a watched listing, the watcher. The client's handles stay valid until
   the client closes them.

Sorting happens in the core, never in the UI. Directories (anything with the
directory attribute, junctions included) always come first. Then, by `key`:

| Key | Order within each group |
|---|---|
| `name` | Explorer's natural order: case-insensitive, numbers by value (`file2` before `file10`) |
| `size` | size, then name |
| `modified` | last-write time, then name |
| `kind` | directory, file, link, then name |

`descending: true` reverses the order within each group; directories stay
first.

## The listing section

All numbers are little-endian. The layout is pinned by tests in
`cabinetos-protocol` (`shm` module); a change must raise the version.

```text
offset 0                                        the section
┌──────────────────────────────────────────────┐
│ ListingHeader (40 bytes)                      │
├──────────────────────────────────────────────┤ entries_offset (40)
│ ListingEntry[0] (16 bytes)                    │
│ ListingEntry[1]                               │
│ …                          entry_count records│
├──────────────────────────────────────────────┤ meta_offset
│ ListingMeta[0] (40 bytes)                     │  = entries_offset + 16 × entry_count
│ ListingMeta[1]                                │
│ …                  same order as the entries  │
├──────────────────────────────────────────────┤ name_arena_offset
│ name arena: UTF-16 code units, no terminators │  = meta_offset + 40 × entry_count
└──────────────────────────────────────────────┘ name_arena_offset + name_arena_len
                                                  = section_size
```

`ListingHeader` (40 bytes, 4-byte fields):

```text
 0  magic              "CBLS" = 0x534C4243
 4  version            2
 8  entry_count
12  name_arena_offset  bytes from the start of the section
16  name_arena_len     bytes
20  generation         1, then one more for each refresh
24  meta_offset        bytes from the start of the section
28  entries_offset     bytes from the start of the section
32  flags              none defined yet; 0
36  reserved           0; pads the header to 40 bytes
```

`ListingEntry` (16 bytes, 8-byte aligned):

```text
 0  id           u64  NTFS file reference number, or a name hash (see flags)
 8  name_offset  u32  bytes from the start of the name arena
12  name_len     u16  UTF-16 code units, not bytes
14  kind         u8   0 unknown, 1 file, 2 directory, 3 link (symlink or junction)
15  flags        u8   bit 0: id is a hash of the upper-cased name, top bit set
                      (for file systems without file IDs)
```

`ListingMeta` (40 bytes, 8-byte aligned), same index as the entry:

```text
 0  size        u64  bytes; 0 for directories
 8  modified    i64  FILETIME ticks (100 ns since 1601-01-01 UTC)
16  created     i64  FILETIME ticks
24  accessed    i64  FILETIME ticks
32  attributes  u32  FILE_ATTRIBUTE_* bits
36  reserved    u32  0
```

A reader checks `magic` and `version` before anything else, then that every
part lies inside `section_size`. Unknown `kind` values read as unknown.
Links are only name-surrogate reparse points (symbolic links, junctions, WSL
links); cloud placeholders such as OneDrive files keep kind file or
directory, with the reparse-point bit in `attributes`.

## Watched listings and events

With `"watch": true` the core also watches the directory (not its
subdirectories) with `ReadDirectoryChangesW`, on a thread of its own. The
watch starts before the first read, so no change is missed.

When the directory changes, the core waits 50 ms for the rest of the burst,
reads the whole directory again into a **new** section, and sends an event
on the same connection:

```json
{"id":"01M…","type":"listing_refreshed","listing_id":7,"section_handle":1204,
 "section_size":8484544,"entry_count":100001,"generation":2,"reason":"changed"}
```

- `reason` is `changed`, or `overflow` when there were too many changes for
  Windows to report one by one. Either way the new section is complete.
- The client owns the new handle, as with `listing_opened`. After sending
  the event, the core closes its own handle to the previous section; the
  previous section stays valid for as long as the client keeps its handle.
- **The 30 Hz rule:** a client never receives more than 30 refreshes per
  second for one listing. After a change the core waits 50 ms, and it never
  refreshes a listing sooner than 1/30 s after the previous refresh, whatever
  the disk does.
- The core reads the directory again rather than patching the old listing:
  reading takes tens of milliseconds even for 100,000 entries. Patching can
  come later, if measurements ask for it.

When a watched listing can no longer be kept current (its directory was
deleted, or reading it fails), the core sends one last event:

```json
{"id":"01M…","type":"listing_lost","listing_id":7,"message":"C:\\gone: not found"}
```

No more events follow for that listing; `close_listing` still answers `ok`.
The `listing_opened` reply always reaches the client before any event about
that listing. When a connection closes, the core ends all its listings.

## Volumes and disks

```json
{"id":"01M…","type":"volume_info","path":"H:\\Media"}
{"id":"01M…","type":"volume_info","drive_letter":"H",
 "volume_guid_path":"\\\\?\\Volume{ff50b21c-…}\\","filesystem":"NTFS","label":"HHD",
 "total_bytes":12000013840384,"free_bytes":10391482793984,
 "disk":{"device_number":0,"bus_type":"SATA","seek_penalty":true,"media_type":"HDD"}}
```

`disk` describes the physical disk under the volume. Two paths with the same
`device_number` compete for one disk, so the job engine (Phase 4) copies
between them one at a time. `seek_penalty` is `true` for spinning disks.
Every disk field is best effort: when Windows does not say, the field is
`null` (or `disk` itself is, for example for network shares). Nothing here
needs administrator rights.

## Configuration, commands and keybindings

The core owns `cabinetos.json` ([config.md](config.md)), the command
registry and the keymap ([keybindings.md](keybindings.md)). The UI asks for
them and never reads the file itself.

```json
{"id":"01M…","type":"get_config"}
{"id":"01M…","type":"config","path":"C:\\Users\\me\\AppData\\Roaming\\CabinetOS\\cabinetos.json",
 "config":{"version":1,"ui":{"layout":"classic","dualPane":true,…},…}}
```

`config` is the whole configuration in effect, defaults included, in the
file's own format.

```json
{"id":"01M…","type":"get_keymap"}
{"id":"01M…","type":"keymap","chord_window_ms":1000,
 "bindings":[{"keys":"ctrl+shift+p","command":"palette.show"},
             {"keys":"f5","command":"file.copyToOtherPane","when":"filesView"},…],
 "immutable":["palette.show","overlay.close","keys.open"]}
```

`bindings` holds every binding in effect, grouped by command in registry
order; `when` is absent for a binding that applies everywhere. The UI runs
the chord state machine with `chord_window_ms` (keybindings.md, "Chords").

```json
{"id":"01M…","type":"list_commands"}
{"id":"01M…","type":"commands","commands":[{"id":"view.toggleDualPane",
 "category":"View","title":"Toggle Dual Pane","keys":["ctrl+shift+d"],
 "default_keys":["ctrl+shift+d"],"source":{"kind":"core"},"target":"ui",
 "immutable":false},…]}
{"id":"01M…","type":"search_commands","query":"dual","limit":5}
{"id":"01M…","type":"search_results","hits":[{"id":"view.toggleDualPane","score":208}]}
```

`source` is `{"kind":"core"}` or `{"kind":"plugin","id":"…"}`. The palette
shows `category: title` and the `keys`; the ranking is in keybindings.md,
"Palette search". `score` only orders the hits.

```json
{"id":"01M…","type":"execute_command","command":"help.about"}
{"id":"01M…","type":"command_result","result":{"name":"CabinetOS",
 "core_version":"0.1.0","protocol_version":3,"config_path":"C:\\…\\cabinetos.json"}}
{"id":"01M…","type":"execute_command","command":"view.toggleSidebar"}
{"id":"01M…","type":"command_routed","target":"ui"}
```

The field is `command`, not `id`, because `id` is already the request's own
ID in the same object. The core runs its own commands (`target` `core`) and
answers `command_result`; a UI command comes back as `command_routed`, for
the UI to run.

```json
{"id":"01M…","type":"set_keybinding","command":"view.toggleSidebar","keys":"Ctrl+Alt+B"}
{"id":"01M…","type":"keymap","chord_window_ms":1000,"bindings":[…],"immutable":[…]}
```

`set_keybinding` and `reset_keybinding` write the file and answer with the
new keymap. The keys are normalized (`ctrl+alt+b`). Refusals come as `error`
with `invalid_keys`, `unknown_command`, `keybinding_conflict`,
`immutable_binding` or `config_error`; the file is then not changed.

**Events.** Every connection that said `hello` receives these, whoever caused
the change: a text editor, another client, or this one. The core sends them
within a second of the file being saved.

```json
{"id":"01M…","type":"config_changed","changed":["keybindings"]}
{"id":"01M…","type":"keymap_changed","keymap":{"chord_window_ms":1000,"bindings":[…],"immutable":[…]}}
{"id":"01M…","type":"config_error","line":3,"column":13,
 "message":"unknown field `dualPan`, expected one of `layout`, `dualPane`, `sidebar`, `theme`"}
```

- `config_changed` comes for every change that took effect. `changed` lists
  the settings as dotted paths (`ui.layout`, `panes.sort.descending`); an
  array such as `keybindings` counts as one setting. It is empty when a
  broken file was fixed back to the settings already in effect: the event
  then says the error is gone.
- `keymap_changed` follows `config_changed` when the compiled keymap
  differs, with the whole new keymap.
- `config_error` means the file cannot be used; the settings in effect stay.
  `line` and `column` count from 1 (columns in characters) and are `null`
  when unknown, for example when the file was deleted.

## Trying it by hand

`cabinetos-cli` speaks this protocol: `ls` maps the section and prints it,
`ls --watch` prints each `listing_refreshed`, `volume` prints `volume_info`,
and `config`, `commands` and `keys` cover the messages above (`keys watch`
prints the events). See [core/README.md](../core/README.md).
