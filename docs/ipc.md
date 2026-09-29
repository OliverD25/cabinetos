# The UI–core bridge (IPC)

How a client (the WinUI 3 UI, or `cabinetos-cli` today) talks to
`cabinetos-core.exe`. Brief §4; format decision in
[ADR 0006](decisions/0006-control-channel-json.md). The Rust types in
`core/crates/cabinetos-protocol` are the source of truth; their JSON Schema is
in [sdk/protocol/](../sdk/protocol/) (`request`, `response` and `event`
schemas).

There are three channels:

- **The control channel**: a named pipe carrying small JSON messages
  (requests, replies, events).
- **The data channel**: shared-memory sections carrying directory listings,
  which the client reads by pointer, with no copy and no parsing.
- **The terminal pipes**: one pipe per terminal session, carrying the
  shell's raw bytes both ways ("Terminal sessions" below).

## The pipe

- Name: `\\.\pipe\cabinetos-core-<token>`. The UI starts the core with
  `--pipe <random token> --parent-pid <its own PID>`; the core exits when that
  process exits. For development the token is `dev`.
- Only the user who started the core can open the pipe: its access list
  (DACL) has one entry, for that user. Remote (network) clients are refused.
- Framing: every message is a 4-byte little-endian length, then that many
  bytes of UTF-8 JSON. A frame may be at most 16 MiB; a longer one gets a
  `frame_too_large` error and the connection is closed. Large data belongs in
  shared memory. A connection that ends inside a frame is closed too; the
  log says the client left in the middle of a frame.
- Every message is one flat JSON object with an `id` (a ULID) and a `type`:

  ```json
  {"id":"01M3JP62MKWQQNAMJMXY6XDEY1","type":"ping"}
  ```

  A reply carries the ID of its request. An event carries a fresh ID of its
  own. A client tells replies from events by `type`; no event type is also a
  reply type.
- Any message may also carry a `trace`, next to `id`: the ULID of the user
  action it belongs to ([diagnostics.md](diagnostics.md), "How an action's
  trace id travels"):

  ```json
  {"id":"01M3JP62MKWQQNAMJMXY6XDEY1","trace":"01M3JP5ZQ7D9WJ4QG6V1R8T2KA","type":"ping"}
  ```

  The window sends the trace of the command run with every request of that
  run; the CLI sends one per run. A request without one is an action of its
  own: the core uses its `id` as its trace. The core's reply carries the
  request's trace (its `id` when the request had none), and so do the
  events of what the request started: the job's `job_progress`,
  `job_state_changed` and `job_conflict`, a measure's events, a plugin's
  `plugin_event` emitted during the call, and a `config_changed` that a
  `set_value` caused. Events nobody's action caused (a watcher's
  `listing_refreshed`, `volumes_changed`) carry none. A `trace` that is not
  a ULID makes the message invalid, like a bad `id`. The field is optional
  and older peers ignore it, so it does not change the protocol version.

## The handshake

A client that wants listings or events says `hello` first, once per
connection:

```json
{"id":"01M…","type":"hello","client_pid":4242,"client_name":"CabinetOS"}
{"id":"01M…","type":"welcome","protocol_version":11,"core_version":"0.1.0"}
```

`client_pid` must be the process on the other end of the pipe; the core asks
Windows and refuses a mismatch with `protocol_error`. The core needs the PID
because it duplicates shared-memory handles into that process. A
`list_directory` before `hello` fails with `protocol_error`, message
`hello required`. From `hello` on, the connection also receives the
configuration events (`config_changed`, `config_error`, `keymap_changed`),
the job events (`job_progress`, `job_conflict`, `job_state_changed`),
the plugin events (`plugin_state_changed`, `plugin_crashed`,
`plugin_event`), `terminal_exited`, `volumes_changed`,
`theme_changed`, and the marketplace events (`install_progress`,
`install_finished`, `tools_changed`), and right after
`welcome` a `job_conflict` for every conflict that already waits for a
decision. Every other request works without `hello`.

Protocol version 3 (Phase 3) added the configuration, command and keymap
messages, and made the `list_directory` options `include_hidden` and `sort`
optional. Version 4 (Phase 4) added the jobs. Version 5 (Phase 7) added
the Recycle Bin conflict (`recycle_bin_too_small`, `delete_permanently`,
`invalid_resolution`) and the Core Plugins: four requests, the `plugins`
reply, three events, the error codes `no_such_plugin` and `plugin_error`,
and the plugin's `name` in a command's `source`. Version 6 (Phase 6) added
file search (`search`, `file_search_results`) and `index_status`. Version 7
(Phase 8) added the terminal sessions: five requests, the replies
`terminal_opened` and `terminal_sessions`, the event `terminal_exited`, and
the error codes `no_such_session`, `unknown_profile` and `spawn_failed`.
Version 8 (for the shell of Phase 5) added `list_volumes` with its reply
`volumes` and the event `volumes_changed`, `get_value` and `set_value`
with the reply `value`, `open_path`, `create_directory` and `rename`, and
the error code `already_exists`. Version 9 added the shell's type names and
icons: `describe_entries` with its reply `entry_details`, and `get_icon`
with its reply `icon`. Version 10 (Phase 9) added the colour themes:
`list_themes` with its reply `themes`, `get_theme` with its reply `theme`,
the event `theme_changed` and the error code `no_such_theme`; and the
marketplace: `marketplace_refresh` and `marketplace_search` with the reply
`marketplace_index`, `install_extension`, `uninstall_extension`,
`list_tools` with its reply `tools`, the events `install_progress`,
`install_finished` and `tools_changed`, and the error codes
`no_such_extension`, `marketplace_error`, `hash_mismatch` and
`incompatible`. Version 11 added what the shell asked for after Phases 5
and 9: `items_per_second` in `job_progress`; one `marketplace_index` item
per extension, with `installedVersion`; each theme's `mica` in `themes`;
and the theme kind `system` (follow Windows' light or dark mode), which
the shipped `default` theme now has; and `tools_changed` sent again to a
client that fell behind on events. Later, still version 11:
`installed_version` in `install_finished`; `help.about` became a
command of the UI (its About view), so the core answers it with
`command_routed` instead of a `command_result` with its versions; the
listing's link and "not on this disk" flags and its `reparse_tag`; and a
theme's optional `metrics` and `chrome`, with `has_metrics` in `themes`.
Version 12 (sub-phase 11a, Total Commander's keys and small commands)
added the sort key `extension`, `create_file`, `edit_path`,
`show_properties`, `measure_paths` with its reply `measure_started`, the
events `measure_progress` and `measure_finished`, and `cancel_measure`;
`match_entries` with its reply `entry_matches`; and
`terminal_type_paths`. Version 13 (sub-phase 14a, the foundations of
Phase 14, each general and useful without AI) added `window_state` and
`get_window_state`, with the reply `window_state` and the error code
`no_window` ("What the window shows"); the previews: `preview_listing` and
`open_preview` with the reply `preview_opened`, `preview_apply` with the
reply `jobs_started`, `preview_cancel`, the events `preview_applied` and
`preview_cancelled`, the error codes `no_such_preview` and
`too_many_previews`, the listing header's preview flag and its preview
rows ("Previews"); the job kind `steps` ("Jobs"); and secrets:
`secret_set`, `secret_get` with the reply `secret`, `secret_delete`,
`secret_list` with the reply `secret_names`, and the error codes
`no_such_secret` and `secret_error` ("Secrets").

**What changes the version.** A new message, a new value of an existing
kind or code, a new required field, or a changed meaning raises the
version: a client built for the old one may not read them. An optional
field added to an existing message keeps it: every client ignores a
field it does not know, and a client that reads the new field treats it
as absent from an older core.

## Requests and replies

| Request | Fields | Reply |
|---|---|---|
| `ping` | — | `pong` (`protocol_version`, `core_version`) |
| `shutdown` | — | `ok`, then the core exits with code 0 |
| `hello` | `client_pid`, `client_name` | `welcome` (`protocol_version`, `core_version`) |
| `list_directory` | `path`; `include_hidden` and `sort` (when left out, the `panes` settings of [config.md](config.md) decide: by default `false` and `{"key":"name","descending":false}`); `watch` (default `false`) | `listing_opened` |
| `close_listing` | `listing_id` | `ok` |
| `describe_entries` | `listing_id`, `from`, `count` (at most 512) | `entry_details` (`listing_id`, `generation`, `from`, `details`) |
| `match_entries` | `listing_id`, `patterns`; `files_only` (default `false`); `first_from` | `entry_matches` (`listing_id`, `generation`, `ranges`) |
| `get_icon` | `key`, `size` (16, 24, 32 or 48) | `icon` (`key`, `size`, `png_base64`) |
| `volume_info` | `path` (need not exist) | `volume_info` |
| `list_volumes` | — | `volumes` (`volumes`) |
| `open_path` | `path` (absolute) | `ok`; a console program gets a console window of its own |
| `edit_path` | `path` (absolute, a file) | `ok` |
| `show_properties` | `paths` (absolute, at least one) | `ok` |
| `create_directory` | `path` (absolute; the parent must exist) | `ok` |
| `create_file` | `path` (absolute; the folder must exist) | `ok` |
| `rename` | `path` (absolute), `new_name` (a name, without a folder) | `ok` |
| `measure_paths` | `paths` (absolute) | `measure_started` (`measure_id`) |
| `cancel_measure` | `measure_id` | `ok` |
| `get_config` | — | `config` (`path`, `config`) |
| `get_value` | `path` (a dotted path, such as `ui.dualPane`) | `value` (`value`) |
| `set_value` | `path`, `value` | `ok` |
| `get_keymap` | — | `keymap` |
| `list_commands` | — | `commands` |
| `search_commands` | `query`; `limit` (default 20) | `search_results` (`hits`) |
| `execute_command` | `command`; `args` (default `null`) | `command_result` or `command_routed` |
| `set_keybinding` | `command`, `keys` (`""` for none) | `keymap` |
| `reset_keybinding` | `command` | `keymap` |
| `start_job` | `kind`, `sources`; `destination` (copy and move); `options` (every field has a default) | `job_started` (`job_id`) |
| `list_jobs` | — | `jobs` |
| `job_control` | `job_id`, `action` (`pause`, `resume`, `cancel`) | `ok` |
| `resolve_conflict` | `job_id`, `conflict_id`, `resolution`; `apply_to_same_kind` (default `false`) | `ok` |
| `list_plugins` | — | `plugins` |
| `reload_plugin` | `plugin_id` | `ok` |
| `set_plugin_enabled` | `plugin_id`, `enabled` | `ok` |
| `grant_capabilities` | `plugin_id`, `capabilities` | `ok` |
| `search` | `query`; `limit` (default 100, at most 1,000); `root` | `file_search_results` |
| `index_status` | — | `index_status` (`available`, `volumes`) |
| `terminal_open` | `cols`, `rows`; `profile` (default `terminal.defaultProfile`); `cwd` (default the user's profile folder) | `terminal_opened` (`session_id`, `pipe`, `pid`) |
| `terminal_resize` | `session_id`, `cols`, `rows` | `ok` |
| `terminal_close` | `session_id` | `ok`, once the shell has ended |
| `terminal_sync_cwd` | `session_id`, `path` | `ok` |
| `terminal_type_paths` | `session_id`, `paths` | `ok` |
| `terminal_list` | — | `terminal_sessions` (`sessions`) |
| `list_themes` | — | `themes` (`themes`) |
| `get_theme` | `theme_id` (without it: the theme in effect) | `theme` (`theme`) |
| `list_tools` | — | `tools` (`tools`) |
| `marketplace_refresh` | — | `marketplace_index` (`items`, `source`, `fetched_at_ms`) |
| `marketplace_search` | `query`; `kind` (`plugin`, `theme` or `tool`) | `marketplace_index` |
| `install_extension` | `extension_id`; `version` (without it: the newest this core runs) | `ok`, once it is in place |
| `uninstall_extension` | `extension_id` | `ok` |
| `window_state` | `active_pane`, `panes` (after `hello`) | `ok` |
| `get_window_state` | `client` (without it: the client that spoke last) | `window_state` (`client`, `sent_at_ms`, `state`) |
| `preview_listing` | `title`, `rows` (after `hello`) | `preview_opened` (`preview`, `title`, `listing`) |
| `open_preview` | `preview` (after `hello`) | `preview_opened` |
| `preview_apply` | `preview` | `jobs_started` (`jobs`) |
| `preview_cancel` | `preview` | `ok` |
| `secret_set` | `name`, `value` | `ok` |
| `secret_get` | `name` | `secret` (`value`) |
| `secret_delete` | `name` | `ok` |
| `secret_list` | — | `secret_names` (`names`) |

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
| `already_exists` | Something with that name is already there. Also an extension the marketplace did not install, in the place an install would use. |
| `no_such_listing` | No open listing on this connection has that `listing_id`. |
| `io` | Reading from the disk or the network failed. |
| `unknown_command` | No command has that ID. |
| `not_implemented` | The command exists, but the core cannot run it yet; it arrives in a later phase. |
| `invalid_keys` | The keys do not follow the key grammar ([keybindings.md](keybindings.md)). |
| `keybinding_conflict` | The keys are taken by another command in the same context, or a combination would be both a binding and the start of a chord. |
| `immutable_binding` | The change touches the Immutable System Tier. |
| `config_error` | The configuration file cannot be changed now: it has an error the user must fix first, or it cannot be written. Also a `set_value` of `ui.theme` to a theme with no valid file, and `get_theme` of a file that is not a valid theme. |
| `no_such_job` | No job has that `job_id`. |
| `no_such_conflict` | The job has no waiting conflict with that `conflict_id`. |
| `invalid_resolution` | The resolution does not fit the conflict, such as `delete_permanently` for a file that exists. |
| `no_such_plugin` | No plugin with that ID is installed, or the plugin host is not running. |
| `plugin_error` | A plugin's command failed: the plugin answered with an error or with text that is not JSON, crashed, or is not running. Also a grant the core refuses: an unknown capability, or one it never grants. |
| `no_such_session` | No terminal session has that `session_id`; or, for `terminal_resize`, `terminal_sync_cwd` and `terminal_type_paths`, its shell has exited. |
| `unknown_profile` | No profile in `terminal.profiles` has that name. The message lists the names. |
| `spawn_failed` | A program could not start. A terminal's shell: its program is not on the `PATH`, the folder is not an absolute path to a folder, 32 sessions exist already, or Windows refused. The editor of `files.editor` (`edit_path`): its program is neither a file nor a program on the `PATH`. |
| `no_such_theme` | No theme with that ID is in the themes folder, or the ID cannot name a theme file. |
| `no_such_extension` | The index has no extension with that ID (or not that version); or, for `uninstall_extension`, the marketplace did not install one with that ID. |
| `marketplace_error` | The index cannot be read or is refused (plain `http:` without `marketplace.allowInsecure`, another `schemaVersion`), a download failed or is not what its kind needs, the files cannot be put in place, or the theme to uninstall is in effect. |
| `hash_mismatch` | The download's SHA-256 is not the one the index gives. It was deleted, and nothing was installed. |
| `incompatible` | The extension needs a newer CabinetOS (`minCoreVersion`). |
| `no_window` | No client told the core what its window shows (`window_state`), or not the client named. |
| `no_such_preview` | No preview has that ID: it was applied, cancelled or expired, or never made. |
| `too_many_previews` | The client (or plugin) has 20 previews alive; apply or cancel one first. |
| `no_such_secret` | No secret has that name. |
| `secret_error` | The secret's name is not 1 to 128 letters, digits, `-`, `_` and `.`; its value is empty or longer than 2,560 bytes; or the Credential Manager refused. |

Requests on one connection are independent: `list_directory`,
`describe_entries`, `match_entries`, `get_icon`, `volume_info`, `list_volumes`, `open_path`, `edit_path`,
`show_properties`, `create_directory`, `create_file`, `rename`, `measure_paths`,
`set_value`, `set_keybinding`, `reset_keybinding`, `start_job`,
`reload_plugin`, `set_plugin_enabled`, `grant_capabilities`,
`execute_command` for a plugin's command, `search`, `index_status`,
`terminal_open`, `terminal_close`, `terminal_sync_cwd`, `list_themes`,
`get_theme` of a named theme, `list_tools`, the marketplace requests,
`preview_listing`, `open_preview`, `preview_apply` and the secret requests
run in the background, so a slow directory, plugin, search or shell does not hold up
the next request, and their replies may come in any order. Match replies to
requests by `id`.

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
   Rust). The core duplicates a handle only in the step that sends the
   message naming it, so every handle a client holds was announced to it:
   a listing closed while its refresh is still reading leaves nothing
   behind in the client.
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
| `name` | Explorer's natural order: case-insensitive, numbers by value (`file2` before `file10`); see below for names Explorer calls equal |
| `size` | size, then name |
| `modified` | last-write time, then name |
| `kind` | directory, file, link, then name |
| `extension` | files: the extension (after the last dot) ignoring case, then name, files without one first; directories: name |

`descending: true` reverses the order within each group; directories stay
first.

The natural order is `StrCmpLogicalW`'s, in the user's locale. Names it
calls equal are ordered by their UTF-16 units, so the order is the same on
every run: `Report.txt` before `report.txt` (in a folder that tells case
apart), and `café` spelled decomposed (`e` and U+0301) before `café`
composed (U+00E9). For the edge-case fixture's `names\` folder
(`sdk/fixtures/edge-fixture.ps1`) that gives `case`, `Ґанок`, `📁 photos`,
`中文文件夹`, then the 255-unit name, the two `café.txt` (decomposed first),
`Звіт 2026.txt`, `Їжак і Єнот.md`, `مستند.txt`, `𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt`,
`日本語のファイル.txt`. The client shows rows in the section's order and
never sorts.

Names are the file system's UTF-16 units, unchanged: nothing is normalized,
so `café` composed and `café` decomposed are two entries with two names.
NTFS also takes names that are not valid UTF-16 (a lone surrogate); the
section's name arena holds them exactly, but a JSON message carries text,
where such a unit becomes U+FFFD, so a request cannot name that file yet.

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
32  flags              bit 0 (1): a preview of proposed changes (below)
36  preview_offset     in a preview: bytes from the start of the section to
                       the preview rows; 0 otherwise (and pads the header
                       to 40 bytes)
```

`ListingEntry` (16 bytes, 8-byte aligned):

```text
 0  id           u64  NTFS file reference number, or a name hash (see flags)
 8  name_offset  u32  bytes from the start of the name arena
12  name_len     u16  UTF-16 code units, not bytes
14  kind         u8   0 unknown, 1 file, 2 directory, 3 link (symlink or junction)
15  flags        u8   bit 0 (1): id is a hash of the upper-cased name, top bit
                             set (for file systems without file IDs)
                      bit 1 (2): a junction
                      bit 2 (4): a symbolic link (to a folder when the
                             attributes have FILE_ATTRIBUTE_DIRECTORY)
                      bit 3 (8): a mount point (a junction to a volume)
                      bit 4 (16): not on this disk: reading the data
                             fetches it (a cloud file, an offline file)
```

`ListingMeta` (40 bytes, 8-byte aligned), same index as the entry:

```text
 0  size        u64  bytes; 0 for directories
 8  modified    i64  FILETIME ticks (100 ns since 1601-01-01 UTC)
16  created     i64  FILETIME ticks
24  accessed    i64  FILETIME ticks
32  attributes   u32  FILE_ATTRIBUTE_* bits
36  reparse_tag  u32  IO_REPARSE_TAG_* of a reparse point; 0 otherwise
```

A reader checks `magic` and `version` before anything else, then that every
part lies inside `section_size`. Unknown `kind` values read as unknown, and
unknown `flags` bits are ignored.

**Links.** Kind 3 is a name-surrogate reparse point: an entry that stands
for another path. The flags say which kind: a junction (`IO_REPARSE_TAG_MOUNT_POINT`
to a folder), a symbolic link (`IO_REPARSE_TAG_SYMLINK`, to a file or a
folder), or a mount point (the junction tag again, with a volume as its
target; the core reads the link's reparse data to tell, one open per
junction in the folder). A link of another name-surrogate kind (a WSL link,
`IO_REPARSE_TAG_LX_SYMLINK`) has kind 3, no kind flag, and its tag in
`reparse_tag`. Other reparse points are not links: OneDrive files, Windows'
compressed system files (`IO_REPARSE_TAG_WOF`) and the like keep kind file
or directory, with the reparse-point bit in `attributes` and their tag in
`reparse_tag`, so a client can name them without opening them.

**Not on this disk.** Flag bit 4 marks an entry whose data is elsewhere:
the attributes hold `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (a cloud file
not downloaded, OneDrive's "online only"), `FILE_ATTRIBUTE_RECALL_ON_OPEN`
(a cloud folder whose list of files is not fetched yet) or
`FILE_ATTRIBUTE_OFFLINE` (a file moved to other storage). The attributes
say the same; the flag saves every client the test. Listing such an entry
reads nothing of it, `describe_entries` and `get_icon` never read it
(below), and opening, copying or moving it fetches it, which is what the
user asked for. A downloaded cloud file loses the mark with its
attributes; the folder's watcher reports that as a change.

The flags and the tag are additive: `reparse_tag` was a reserved field,
always 0, and clients ignore flag bits they do not know, so the layout
version stays 2. A link whose target is gone is still listed as a link;
listing its path is `not_found`.

**Previews.** A listing whose header has flag bit 0 is a preview of
proposed changes (`preview_listing`, "Previews" below), not a folder. Its
entries are the rows in their order; each entry's name is the row's full
path (a pane shows it whole, since rows may come from several folders),
its `id` is the row's index from 0, and its kind, size, times and
attributes are what the disk says now (a row whose path does not exist has
kind 0 and zeros; a created item has the kind it will have). After the
name arena, at `preview_offset` (4-byte aligned), one `PreviewRow` per
entry, in the same order:

```text
 0  to_offset  u32  bytes from the start of the name arena to the target
 4  to_len     u32  the target's UTF-16 code units; 0 when the row has none
 8  change     u8   1 rename, 2 move, 3 copy, 4 delete, 5 create; 0 or
                    another value reads as unknown
 9  reserved   3 bytes, 0
```

The target is the new full path of a rename, and the folder of a move or a
copy; the targets sit in the name arena after the names, so
`name_arena_len` counts them too. The preview flag and `preview_offset`
are additive in the same way (`preview_offset` was the reserved field,
always 0), so the layout version stays 2: a client that ignores them reads
a preview as a list of paths.

## Type names and icons

A listing carries names, sizes, times and attributes, but not what
Explorer shows beside a name: the type ("Text Document") and the icon. The
UI asks for those for the rows it shows, a screenful at a time.

```json
{"id":"01M…","type":"describe_entries","listing_id":7,"from":0,"count":3}
{"id":"01M…","type":"entry_details","listing_id":7,"generation":1,"from":0,"details":[
 {"type_name":"File folder","icon_key":"folder"},
 {"type_name":"Text Document","icon_key":"ext:.txt"},
 {"type_name":"Application","icon_key":"path:396bbcd455199596"}]}
```

- `details` has one element per entry, from index `from` of the listing's
  current section on, in section order: `count` of them (at most 512), or
  fewer at the end, or none past it. `generation` is the generation of the
  section the entries were read from; after a refresh the UI asks again
  for rows whose details carry an older one.
- `type_name` is the shell's name for the type, in the user's language:
  `SHGetFileInfoW` with `SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES`, by the
  extension and the folder attribute only, so no file is read. The core
  keeps it per extension. When the shell answers a program's internal name
  for the type instead (no space, and either no capital letter or ending in
  `file`, such as `txtfile` for `.gitattributes`), the core answers
  `{EXT} File` (`GITATTRIBUTES File`), as Explorer names a type nobody
  named.
- `icon_key` names an icon for `get_icon`. Every folder has `folder`. A
  file has `ext:` and its extension in lower case with its dot
  (`ext:.txt`, and `ext:.gitignore` for `.gitignore`); a file without one
  has `generic`. `.exe`, `.ico` and `.lnk` files carry their own icon, so
  each has `path:` and 16 hex digits, unless its data is not on this disk:
  reading its icon would download a cloud file, so it gets the `ext:` key
  of its extension, and its row is drawn without reading it. The
  `path:` digits are FNV-1a (64 bits) of the lower-case
  full path, the last-write time and the size. A changed file gets a new
  key, so a client may keep icons by key for as long as it likes. The
  core remembers which file a `path:` key stands for (the last 16,384 of
  them); a key it forgot, or whose file is gone, is `not_found`, and
  describing the folder again brings it back.
- An unknown `listing_id` is `no_such_listing`; `count` over 512 is
  `protocol_error`.

```json
{"id":"01M…","type":"get_icon","key":"ext:.txt","size":32}
{"id":"01M…","type":"icon","key":"ext:.txt","size":32,"png_base64":"iVBORw0KGgo…"}
```

- The icon is a PNG of `size` by `size` pixels with an alpha channel,
  base64-encoded. Sizes are 16, 24, 32 and 48 (`protocol_error` for
  others). The pixels come from the system image lists: `SHIL_SMALL` for
  16, `SHIL_LARGE` for 32 and `SHIL_EXTRALARGE` for 48. The 24-pixel icon
  is the 48-pixel one halved (each pixel the alpha-weighted average of
  four), which is sharper than enlarging the 16-pixel one; a UI on a
  high-DPI screen asks for 24 or 48 where it draws 16 or 32 at 150 % or
  200 %.
- A `path:` key's icon is read from its file (`SHGetFileInfoW` without
  `SHGFI_USEFILEATTRIBUTES`). A shortcut's icon is its target's, without
  the arrow Explorer draws over it.
- The core keeps the last 2,000 PNGs by key and size. The first icon of a
  program may take long, as the shell loads it from the file (up to about
  a second, measured in a debug build); icons are drawn one at a time.
- An unknown key is `not_found`; a failure of the shell is `io` with its
  message.

## Matching names

The window's pattern box (Num + and Num −, marking and unmarking rows by
pattern) and its quick search (letters typed in a pane) ask the core which
entries match: the names are in the core's own copy of the section, as
for `describe_entries`, so the window never scans them.

```json
{"id":"01M…","type":"match_entries","listing_id":7,"patterns":"*.txt;*.md|readme*","files_only":true}
{"id":"01M…","type":"entry_matches","listing_id":7,"generation":1,"ranges":[[1,2],[4,1]]}
{"id":"01M…","type":"match_entries","listing_id":7,"patterns":"rep*","first_from":12}
{"id":"01M…","type":"entry_matches","listing_id":7,"generation":1,"ranges":[[40,1]]}
```

- `patterns` is Total Commander's syntax: `*` stands for any run of
  characters and `?` for one; `;` separates patterns, and the patterns
  after a `|` leave names out; case is ignored (each character with a
  single upper-case form is compared in it, as Windows compares names).
  Spaces around a pattern are dropped. With no pattern before the `|`,
  every name is in, so `|*.bak` is everything but backups, and an empty
  `patterns` matches every name. Two endings keep DOS's meaning, as in
  Total Commander and cmd: `.*` also matches a name without an extension,
  so `*.*` matches every name (it is what the pattern box offers first),
  and a lone `.` matches only names without a dot (`*.`). A name that
  contains `;` cannot be matched as such.
- `ranges` lists the matching indexes of the current section as
  `[start, count]` pairs in order, which keeps "all 100,000 rows" short.
  `generation` is the section's, as in `entry_details`; after a refresh
  the window asks again.
- `files_only: true` leaves folders out (links to folders too): the
  pattern box's "Include folders" check box is off by default (decision
  D9 of [research/total-commander.md](research/total-commander.md)).
- With `first_from`, only the first match at or after that index is
  sent, going round to the start, as `[index, 1]`, or no range: quick
  search. An index past the end starts at 0.
- An unknown `listing_id` is `no_such_listing`.

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

```json
{"id":"01M…","type":"list_volumes"}
{"id":"01M…","type":"volumes","volumes":[
 {"drive_letter":"C","volume_guid_path":"\\\\?\\Volume{…}\\","filesystem":"NTFS","label":"System Disk",
  "total_bytes":2000381014016,"free_bytes":1308375261184,
  "disk":{"device_number":1,"bus_type":"NVMe","seek_penalty":false,"media_type":"SSD"}},
 {"drive_letter":"M","volume_guid_path":"","filesystem":"NTFS","label":"_sync_music",
  "total_bytes":5761394913280,"free_bytes":2891714670592,"disk":null}]}
```

`list_volumes` answers with the volume behind every drive letter, in letter
order, each exactly as `volume_info` describes it; the sidebar's Drives
section shows them. `drive_letter` is always the letter asked for, even
when the volume has another one too (a `subst` letter shows its folder's
volume). The drives are asked in parallel, and a drive that does not
answer is left out instead of failing the request: a drive that is not
ready (an empty card reader or optical drive), one that fails, a network
drive whose server does not answer within 200 ms, or a local drive that
takes more than 2 s. A drive left out for time is not asked again until
its first query has ended, so a share whose server is gone holds one
thread in the core, not one per request.

```json
{"id":"01M…","type":"volumes_changed","volumes":[{"drive_letter":"C",…},…]}
```

When a drive letter appears or goes away (a USB stick, a card put into a
reader or taken out, a network share mapped or unmapped, a `subst`
letter), every connection that said `hello` gets `volumes_changed` with
the list `list_volumes` would answer now. Windows announces such changes
with `WM_DEVICECHANGE` broadcasts, which reach only top-level windows, so
the core keeps a top-level window that is never shown, on a thread of its
own. It waits 500 ms for the rest of a burst (one stick may bring two
volumes, and Windows may repeat a message) and sends the event only when
the drives differ from the ones it sent last (free space does not count).

## Files and folders

### Paths of any length

A path in a request is absolute, in the form the user knows (`C:\work\…`,
`\\server\share\…`) or in the verbatim form (`\\?\C:\work\…`,
`\\?\UNC\server\share\…`). The core adds the verbatim prefix itself right
before each Windows file call that takes a path, so no length limit
applies and the machine's long-path policy (off by default) plays no part:
listing, watching, type names and icons, copy, move, delete, a new folder,
a new file, a rename and the search walk all work on paths of 300
characters and more.
Answers carry paths in the form the client used: the core never adds the
prefix to a path it answers, and the index builds its hits' paths from
their folders, without it.

Three things go through the shell, which keeps Windows' old limit of 259
characters:

- `open_path` hands the file to its program by `ShellExecuteExW`, which
  may refuse a longer path; it depends on the program. On one PC a
  339-character `.txt` opened in Notepad, while a 311-character `.cmd` was
  refused, in its short 8.3 form too (the shell turns that back into the
  long one). When the shell refuses a path of 260 characters or more, the
  answer is `invalid_path`, with the path's length and the limit in the
  message; nothing runs.
- The Recycle Bin: a delete to the bin of an item with a path of 260
  characters or more anywhere in it stops at a `path_too_long` conflict
  before the shell sees it
  ([jobs.md](jobs.md#the-recycle-bin-and-long-paths)).
- A program's own icon (a `path:` key) is read by the shell from the plain
  path; that works at 300 characters and more (tested).

A new folder, a new file and a rename touch one name and finish at once,
so they need no job; copy, move and delete are jobs ("Jobs" below).

```json
{"id":"01M…","type":"create_directory","path":"D:\\work\\New folder"}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"create_file","path":"D:\\work\\New Text Document.txt"}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"rename","path":"D:\\work\\draft.txt","new_name":"final.txt"}
{"id":"01M…","type":"ok"}
```

- `create_directory` creates one folder. Its parent must exist
  (`not_found` otherwise), and nothing may have its name yet
  (`already_exists`, also for a file of that name).
- `create_file` creates one empty file, with the same rules: its folder
  must exist, and a taken name, a folder's too, is `already_exists`. It
  never opens or replaces a file, so the window's New Text File (Shift+F4)
  opens the file that has the name instead, as Total Commander does.
- `rename` gives a file or folder a new name in the folder it is in.
  `new_name` is one name: empty, `.`, `..` or a name with `\` or `/` is
  `invalid_path`. Nothing is replaced: a taken name is `already_exists`,
  and the message names it. Changing only the case of letters works.
- All three take any length of path, and so refuse (`invalid_path`,
  with the reason) a name that Windows could not open again later: one
  that ends in a dot or a space, contains `<>:"|?*` or a control
  character, or is a device name such as `CON`, `NUL` or `COM1` (with any
  extension).
- Their paths must be absolute (`invalid_path`). Other failures are
  `access_denied`, or `io` with the Windows error in the message (a file
  in use cannot be renamed, for example).
- A watched listing of the folder refreshes through its watcher, as for
  any other change (`listing_refreshed`).

```json
{"id":"01M…","type":"open_path","path":"C:\\Users\\me\\notes.txt"}
{"id":"01M…","type":"ok"}
```

`open_path` opens a file or folder with its default application, as a
double-click in Explorer does: the shell's `open` verb
(`ShellExecuteExW`), without error dialogs. The reply comes once the shell
has handed the file over, not when the application ends. A console
program (a batch file, a script, a console `.exe`) gets a console window
of its own, as from Explorer (`SEE_MASK_NO_CONSOLE`): the window starts
the core without a console window, and a program that shared the core's
console would run where nobody sees it. Which
application that is, is the user's choice in Windows: an editor or viewer
for a document, Explorer for a folder, the program itself for an `.exe`.
The path must be absolute (`invalid_path` otherwise) and must exist as
given (`not_found` otherwise): the core never lets the shell look further,
so `notepad` in a folder without such a file runs nothing. Other errors
are `access_denied`, or `io` with the shell's error code in the message
(`os error 1155`: no application is associated with the file). A path of
260 characters or more that the shell refuses is `invalid_path` with the
reason ([Paths of any length](#paths-of-any-length)). A link opens what it points
to, as a double-click on it in Explorer does; a link whose target is gone
is `not_found`, and the message names the target, before the shell is
asked (the shell would say only "unspecified error").

```json
{"id":"01M…","type":"edit_path","path":"C:\\Users\\me\\build.cmd"}
{"id":"01M…","type":"ok"}
```

`edit_path` opens a file for editing (the window's F4, `file.edit`), and
never runs it. The path is checked first: it must be absolute
(`invalid_path`) and exist (`not_found`, also for a link whose target is
gone, as for `open_path`), and a folder is `invalid_path`. These answers
come whatever `files.editor` names. Then the first of these that applies
is used:

1. `files.editor` ([config.md](config.md)): its `command`, found as a
   terminal profile finds its shell (a full path, or a name on the
   `PATH` with `.exe` added when it has no extension, never the current
   folder), started with its `args` and the file's path last, quoted the
   way the C runtime reads a command line. A `command` that is found
   nowhere is `spawn_failed`, and nothing else is tried: the setting is
   wrong, and the message says so.
2. The `edit` verb of the file's type, when the type has one with a
   command (`AssocQueryStringW`): a batch file's opens Notepad, where its
   `open` would run it.
3. Notepad, from the system folder, Total Commander's default editor.

It is its own request, not a field of `open_path`, because a core that
does not know a field ignores it, and would then open the file, which for
an `.exe` runs the program; an unknown request is `unknown_request`
instead (decision D7 of
[research/total-commander.md](research/total-commander.md)). The editor
starts through `ShellExecuteExW` with a console of its own
(`SEE_MASK_NO_CONSOLE`), so a console editor such as Vim gets a window:
the core's own console has none. `files.editor` is read at each request.

```json
{"id":"01M…","type":"show_properties","paths":["C:\\Users\\me\\notes.txt"]}
{"id":"01M…","type":"ok"}
```

`show_properties` shows Windows' own property sheet (the window's
Windows Properties command, `file.windowsProperties`): for one path the
`properties` verb of `ShellExecuteExW`, for several the shell's combined
sheet (`SHMultiFileProperties`), which shows what they have in common.
The sheet belongs to the core's process: the shell runs it on a thread of
its own there, so it stays open after the reply, until the user closes it
or the core ends (the core lives as long as the window). Every path must
be absolute (`invalid_path`) and exist (`not_found`); an empty `paths` is
`protocol_error`.

Opening and editing a file, and its property sheet, are core
infrastructure: they are the last step of navigation, and they hand the
file to what Windows or the user has for it. Article 10 still holds: the
core has no viewer or editor of its own, and those come as extensions.

The core runs in the background, and Windows may then open the
application's window behind the current one, flashing in the taskbar. The
usual cure is for the UI, the foreground process, to call
`AllowSetForegroundWindow` with the core's process ID before `open_path`,
`edit_path` or `show_properties`; the core does not depend on it.

## Folder sizes

```json
{"id":"01M…","type":"measure_paths","paths":["C:\\Users\\me\\Pictures","C:\\Users\\me\\notes.txt"]}
{"id":"01M…","type":"measure_started","measure_id":4}
{"id":"01N…","type":"measure_progress","measure_id":4,"path":"C:\\Users\\me\\Pictures",
 "files":1200,"folders":31,"bytes":4500000000}
{"id":"01N…","type":"measure_finished","measure_id":4,"results":[
 {"path":"C:\\Users\\me\\Pictures","files":5310,"folders":120,"bytes":18400000000,"unreadable":1},
 {"path":"C:\\Users\\me\\notes.txt","files":1,"folders":0,"bytes":1200,"unreadable":0}],
 "cancelled":false}
{"id":"01M…","type":"cancel_measure","measure_id":4}
{"id":"01M…","type":"ok"}
```

`measure_paths` counts what is under each path, for the Size column (the
window's Space, Calculate Folder Size and Calculate All Folder Sizes).
The walk is the one the job engine's Recycle Bin check uses
(`measure_tree` in `cabinetos-fs`): each folder read with the NT
enumeration and not sorted; hidden and system entries counted; a link
counted as the file or folder it looks like, never entered, and adding
no bytes; a folder that cannot be read counted in `unreadable`, without
what is in it.

- Every path must be absolute (`invalid_path`) and exist (`not_found`).
  Both are checked before `measure_started`, and a refused measure sends
  nothing more. An empty `paths` starts and ends at once.
- The paths are counted one after another on a thread of the core, so
  several measures run at once, and none holds up another request. The
  events go only to the connection that asked, and need no `hello`;
  `measure_started` always comes before them.
- `measure_progress` carries the totals so far of the path being
  counted, after each folder: at most 30 per second per measure, and none
  for a measure that ends sooner. A single folder of 100,000 files is one
  read, so its progress comes only at its end.
- `measure_finished` ends every measure that started. `results` has one
  entry per path counted to the end, in the order of `paths`. A file has
  `files: 1` and its size, and `folders` never counts the path itself. A
  path that went away after the check has zeros and `unreadable: 1`.
- `cancel_measure` stops a measure of this connection before its next
  folder; `measure_finished` then has `cancelled: true` and the results
  of the paths counted before. The answer is `ok` also for a measure that
  has ended (a cancel may cross the end) or is not this connection's.
  Closing the connection stops its measures.

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
{"id":"01M…","type":"get_value","path":"ui.dualPane"}
{"id":"01M…","type":"value","value":true}
{"id":"01M…","type":"set_value","path":"ui.dualPane","value":false}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"config_changed","changed":["ui.dualPane"]}
```

`get_value` and `set_value` read and change one setting, named by the
dotted path that `config_changed` uses: object keys only, such as
`ui.lastPaths` or `panes.sort` (an array is one setting and is replaced
whole). The value has the file's own format. `set_value` goes through the
same steps as a keybinding change ("When the core writes the file" in
[config.md](config.md)): the core reads the file as it is on disk, checks
the new value as it checks a saved file (the type, the version, the
terminal profiles, the keybindings), rewrites the file atomically, and
tells every connection that said `hello`, the one that asked too, with
`config_changed`. A setting that does not exist, or a value it cannot
hold, is refused with `config_error` and the reason; so is any change
while the file on disk has an error. The file is not touched then. A
value equal to the one in effect is `ok` and changes nothing.

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
{"id":"01M…","type":"search_results","hits":[{"id":"view.toggleDualPane","score":137}]}
```

`source` is `{"kind":"core"}` or
`{"kind":"plugin","id":"reader","name":"Reader"}`. The palette shows
`category: title` and the `keys`, and a plugin's `name` as a badge; the
ranking is in keybindings.md, "Palette search". `score` only orders the
hits.

```json
{"id":"01M…","type":"execute_command","command":"hello.say"}
{"id":"01M…","type":"command_result","result":{"message":"hello from Hello"}}
{"id":"01M…","type":"execute_command","command":"view.toggleSidebar"}
{"id":"01M…","type":"command_routed","target":"ui"}
```

The field is `command`, not `id`, because `id` is already the request's own
ID in the same object. The core runs a plugin's command and answers
`command_result` with what the plugin returned; a UI command, which every
command of the core's own registry is, comes back as `command_routed`, for
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
 "message":"unknown field `dualPan`, expected one of `layout`, `dualPane`, `sidebar`, `theme`, `lastPaths`, `pinned`"}
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
  when unknown, for example when the file was deleted. It also reports a
  theme that cannot be used ("Colour themes" below).

## Jobs

Copy, move and delete run as jobs in the core ([jobs.md](jobs.md) has the
model, the scheduler and the conflict rules). A job belongs to the core,
not to the connection that started it: it goes on when the client
disconnects, and `list_jobs` from any connection shows it.

```json
{"id":"01M…","type":"start_job","kind":{"type":"copy"},
 "sources":["C:\\photos"],"destination":"D:\\backup",
 "options":{"on_conflict":"ask","copy_links":"as_link","verify":false,"preserve_timestamps":true}}
{"id":"01M…","type":"job_started","job_id":7}
```

- `kind` is `{"type":"copy"}`, `{"type":"move"}`,
  `{"type":"delete","permanent":false}`, or (protocol 13)
  `{"type":"steps","steps":[…]}`: simple steps in order, which
  `preview_apply` and `undo_job` start ([jobs.md](jobs.md), "Chains and
  steps"). A step is `{"op":"rename","from","to"}`,
  `{"op":"create_folder","path"}`, `{"op":"create_file","path"}`,
  `{"op":"recycle","path"}` or `{"op":"restore","saved","to"}`; a steps
  job's `sources` are its steps' paths. Paths are absolute. `options` and
  each of its fields may be left out; the defaults are shown above.
- `job_started` means the paths were checked and the job is queued. The
  work itself is reported by events.

```json
{"id":"01M…","type":"job_state_changed","job_id":7,"state":{"type":"running"}}
{"id":"01M…","type":"job_progress","job_id":7,"state":{"type":"running"},
 "bytes_done":1200000000,"bytes_total":2700000000,"files_done":8412,"files_total":10001,
 "files_skipped":0,"files_failed":0,"conflicts_open":1,"current_path":"C:\\photos\\big.raw",
 "speed_bps":610000000,"items_per_second":412.5,"eta_seconds":3,"elapsed_ms":2400}
{"id":"01M…","type":"job_conflict","conflict_id":9,"job_id":7,
 "kind":{"type":"file_exists","source_size":10,"source_modified":133000000000000000,
         "dest_size":12,"dest_modified":132000000000000000},
 "source":"C:\\photos\\a.jpg","destination":"D:\\backup\\photos\\a.jpg"}
{"id":"01M…","type":"resolve_conflict","job_id":7,"conflict_id":9,
 "resolution":{"type":"overwrite"},"apply_to_same_kind":false}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"job_state_changed","job_id":7,"state":{"type":"completed"}}
```

- `state` is an object tagged by `type`: `queued`, `scanning`, `running`,
  `paused`, `completed`, `completed_with_errors`, `cancelled`, or `failed`
  with a `message`. After a final state no more events follow for the job.
- `job_progress` comes at most 30 times per second per job, and only when
  something changed; the last one of a job carries its final state and
  comes just before the final `job_state_changed`. `current_path` and
  `eta_seconds` are absent when there is nothing to say.
- `job_conflict`: `kind` is `file_exists` (with both sizes and write times
  as FILETIME ticks), `access_denied`, `sharing_violation`,
  `path_too_long`, `disk_full`, `source_vanished`, `recycle_bin_too_small`
  (with the item's `size`) or `io` (with `code` and `message`). The file
  waits; the job goes on.
- `resolution` is `{"type":"overwrite"}`, `{"type":"skip"}`,
  `{"type":"rename"}` (a free name) or `{"type":"rename","new_name":"b.txt"}`,
  `{"type":"retry"}`, `{"type":"delete_permanently"}` (only for
  `recycle_bin_too_small`) or `{"type":"cancel_job"}`. A bad `new_name`
  gets `invalid_path`; an answer that does not fit the conflict gets
  `invalid_resolution`.

```json
{"id":"01M…","type":"list_jobs"}
{"id":"01M…","type":"jobs","jobs":[{"kind":{"type":"copy"},"sources":["C:\\photos"],
 "destination":"D:\\backup","job_id":7,"state":{"type":"completed"},"bytes_done":…}]}
```

Each entry of `jobs` is a `job_progress` plus the job's `kind`, `sources`
and `destination`, so a restarted UI can show what a running job does.

## Plugins

The core runs Core Plugins ([plugins.md](plugins.md)): WebAssembly
components that do only what the user granted.

```json
{"id":"01M…","type":"list_plugins"}
{"id":"01M…","type":"plugins","plugins":[{"id":"reader","name":"Reader","version":"0.1.0",
 "author":"CabinetOS tests","description":"Reads the size of files in one folder.",
 "state":{"type":"needs_review","missing":["fs:read"]},
 "capabilities":[{"name":"cmd:register","level":"low","granted":true,"reason":"Adds the Size command."},
                 {"name":"fs:read","level":"medium","granted":false,"reason":"Reads the size of files in its test folder.",
                  "roots":["%TEMP%\\cabinetos-plugins-test\\reader"]}],
 "commands":[]}]}
```

- `state` is an object tagged by `type`: `loading`, `active`, `disabled`,
  `needs_review` (with `missing`), `failed` (with `message`), or `crashed`
  (with `message` and `at_ms`, milliseconds since 1970-01-01 UTC).
- `capabilities` lists what the plugin asks for, for the permissions
  review dialog: `level` is `low`, `medium` or `high`, and `roots` appears
  only for `fs:read` and `fs:write`, as the manifest wrote them. For
  `net`, `hosts` lists the hosts the plugin may reach and `secrets` the
  stored secrets the core may send for it; each appears only when not
  empty ([plugins.md](plugins.md), "The network").
- `commands` holds the IDs the plugin registered; it is empty unless the
  plugin is `active`.

```json
{"id":"01M…","type":"grant_capabilities","plugin_id":"reader","capabilities":["fs:read"]}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"set_plugin_enabled","plugin_id":"reader","enabled":false}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"reload_plugin","plugin_id":"crashy"}
{"id":"01M…","type":"ok"}
```

- `grant_capabilities` adds to `plugins.<id>.granted` in the configuration
  file, and `set_plugin_enabled` sets `plugins.<id>.enabled`. Both answer
  `ok` once the file is written and the plugin is being started or stopped;
  it may still be `loading`, so follow `plugin_state_changed`. An unknown
  plugin gets `no_such_plugin`; an unknown capability, or one the core
  never grants (`process:run`, `net`, `credentials`), gets `plugin_error`; a
  file with an error to fix gets `config_error`.
- `reload_plugin` reads the plugin's folder again and starts it. It also
  brings back a plugin that stays crashed after three crashes in ten
  minutes, and finds a plugin installed since the core started.

A plugin's commands run like any other:

```json
{"id":"01M…","type":"execute_command","command":"reader.size","args":{"path":"C:\\data\\a.txt"}}
{"id":"01M…","type":"command_result","result":{"size":5}}
{"id":"01M…","type":"execute_command","command":"crashy.crash"}
{"id":"01M…","type":"error","code":"plugin_error",
 "message":"the plugin crashy crashed while running crashy.crash: wasm trap: …"}
```

The reply waits for the plugin: at most its 5 s deadline, and 2 s more for
a plugin stuck in a host function. Other requests on the connection go on
meanwhile. After a crash, the plugin's commands are gone: `execute_command`
answers `unknown_command` and says which plugin crashed.

**Events.**

```json
{"id":"01M…","type":"plugin_crashed","plugin_id":"crashy","message":"wasm trap: …"}
{"id":"01M…","type":"plugin_state_changed","plugin_id":"crashy",
 "state":{"type":"crashed","message":"wasm trap: …","at_ms":1790553600000}}
{"id":"01M…","type":"plugin_event","plugin_id":"hello","name":"hello.said",
 "payload":"{\"greeting\":\"hello\"}"}
```

- `plugin_state_changed` comes for every change of a plugin's state. A
  client that fell behind on events gets one for every plugin.
- `plugin_crashed` comes when an instance trapped or ran out of time, fuel
  or memory, just before its `plugin_state_changed` to `crashed`. Its
  commands have left `list_commands` by then.
- `plugin_event` carries what a plugin sent with `emit` (capability
  `events:emit`): a `name` the plugin chose, and the `payload` text as the
  plugin wrote it, usually JSON.

## Search

Files and folders by name, across whole volumes when the elevated indexer
runs, and in one folder tree when it does not ([indexer.md](indexer.md)).

```json
{"id":"01M…","type":"search","query":"budget","limit":20,"root":"C:\\Users\\me"}
{"id":"01M…","type":"file_search_results","hits":[
 {"path":"C:\\Users\\me\\Budget-2026.xlsx","kind":"file","frn":1407374883553540},
 {"path":"C:\\Users\\me\\old\\budget","kind":"directory","frn":1407374883553541}],
 "source":"index","took_us":1210,"complete":true}
```

- The match is a substring of the name, without case. Hits come best first:
  names that start with the query, then shorter names, then paths in
  order.
- `root` limits the hits to one folder (at any depth). Without it the
  indexer searches every indexed volume, and a walk starts at the folder
  this connection listed last, else at the user's profile folder.
- `source` is `index` (the indexer answered) or `walk` (the core walked
  folders: no indexer answered within 200 ms, or it cannot search under
  that root). A walk stops after 2 s or 20,000 entries.
- `complete` is `false` when the search could not cover everything: a
  walk stopped at a limit, or a volume is still being indexed.
- `kind` is `file` or `directory`; `frn` is the NTFS file reference number, a
  64-bit unsigned integer (it can exceed 2^53), absent when unknown.
- An empty query gets `protocol_error`; a root that does not exist gets
  `not_found`.

```json
{"id":"01M…","type":"index_status"}
{"id":"01M…","type":"index_status","available":true,"volumes":[{"letter":"C",
 "state":{"type":"ready"},"entries":1357918,"built_in_ms":3480,"journal_lag":0}]}
```

`available` is `false` (and `volumes` empty) when no indexer answers.
`state` is an object tagged by `type`: `building`, `ready`, `rebuilding`, or
`failed` with a `message`. `journal_lag` is the change-journal bytes the
index has not applied yet.

The core and the indexer speak the same framing on the indexer's own pipe,
with read-only requests only; that protocol is in [indexer.md](indexer.md).

## Terminal sessions

Shells that the core runs in pseudo-consoles, for the terminal pane
([terminal.md](terminal.md) has the profiles, the byte pipe in detail and
the folder sync). A session belongs to the core, not to the connection
that opened it: a client that leaves, or restarts, finds its sessions in
`terminal_list` and attaches again.

```json
{"id":"01M…","type":"terminal_open","profile":"pwsh","cwd":"E:\\work","cols":120,"rows":30}
{"id":"01M…","type":"terminal_opened","session_id":3,
 "pipe":"\\\\.\\pipe\\cabinetos-term-9f3c01a2b4d5e6f7","pid":4242}
```

- `profile` names one of `terminal.profiles`; without it,
  `terminal.defaultProfile`. `cwd` must be an absolute path to a folder;
  without it, the user's profile folder. `cols` and `rows` are the size in
  character cells, from 1 to 32,767.
- The shell's bytes travel on `pipe`, not on this channel: raw bytes, no
  framing, both ways. The client reads the shell's output (UTF-8 text with
  VT sequences) and writes keys (text, `\r` for Enter, VT sequences for the
  other keys). One client at a time: while one is attached, opening the
  pipe fails with `ERROR_PIPE_BUSY`. The pipe has the control pipe's access
  list and refuses network clients.
- Output made while no client is attached waits, up to 1 MiB; then the
  shell waits too, until a client reads.
- When the shell exits, the client reads the last output and then the end
  of the pipe, and every connection that said `hello` gets:

  ```json
  {"id":"01M…","type":"terminal_exited","session_id":3,"exit_code":0}
  ```

  The session stays listed, as exited, until `terminal_close`. A client
  that fell behind on events gets one for every exited session.

```json
{"id":"01M…","type":"terminal_list"}
{"id":"01M…","type":"terminal_sessions","sessions":[{"session_id":3,"profile":"pwsh",
 "cwd":"E:\\work","cols":120,"rows":30,"pid":4242,"state":{"type":"running"},
 "pipe":"\\\\.\\pipe\\cabinetos-term-9f3c01a2b4d5e6f7","attached":true}]}
```

`sessions` come oldest first. `state` is `{"type":"running"}` or
`{"type":"exited","code":3}`. `cwd` is the folder the session started in
or was last synced to; the shell may have moved since. `attached` says
whether a client holds the pipe.

```json
{"id":"01M…","type":"terminal_resize","session_id":3,"cols":100,"rows":30}
{"id":"01M…","type":"terminal_sync_cwd","session_id":3,"path":"D:\\docs"}
{"id":"01M…","type":"terminal_type_paths","session_id":3,"paths":["D:\\docs\\a b.txt"]}
{"id":"01M…","type":"terminal_close","session_id":3}
```

Each answers `ok`. `terminal_sync_cwd` types the shell's own
change-directory command, followed by Enter, so the terminal follows the
active pane; the path must be an absolute path to a folder (`invalid_path`
or `not_found` otherwise). `terminal_type_paths` types paths at the
prompt without Enter, each quoted as `terminal_sync_cwd` quotes its
folder, separated by spaces (the window's Ctrl+P and Ctrl+Shift+Enter;
[terminal.md](terminal.md), "Typing paths"); a path with a control
character is `invalid_path`. `terminal_close` closes the pseudo-console,
which the shell sees as a hang-up, and forgets the session; the reply
comes once the shell has ended (a shell still running 2 s later is ended
by force). When the core stops, it closes every session.

## Colour themes

Themes are JSON files in the themes folder ([themes.md](themes.md) has the
format, the folder and the shipped themes). `ui.theme` in the
configuration names the theme in effect.

```json
{"id":"01M…","type":"list_themes"}
{"id":"01M…","type":"themes","themes":[{"id":"catppuccin-mocha","name":"Catppuccin Mocha",
 "author":"CabinetOS","version":"1.0.0","kind":"dark","accent":"#CBA6F7",
 "mica":{"tint":"#1E1E2E","opacity":0.9},"has_metrics":false},…]}
{"id":"01M…","type":"get_theme"}
{"id":"01M…","type":"theme","theme":{"id":"nord","name":"Nord","author":"CabinetOS",…,
 "accent":"#88C0D0","mica":{"tint":"#2E3440","opacity":0.88},"palette":{…},"terminal":{…}}}
```

- `list_themes` lists every valid theme in the folder, by ID, with what a
  picker shows of it: its accent and its Mica tint. A file that is not a
  valid theme is left out; the core's log says why. `accent` is `null` for
  a theme that follows the Windows accent colour, and `mica` for one that
  shows plain Mica. `has_metrics` is `true` for a theme that sets any
  metric: a density preset such as `commander-compact`, which a picker may
  mark. A core from before the field leaves it out; read that as `false`.
- `get_theme` without `theme_id` answers the theme in effect; with it, that
  theme's file, read now. An ID with no file is `no_such_theme`; a file that
  is not a valid theme is `config_error` with the reason. `theme` is the
  file's object without its `$schema` key, with colours in upper case. A
  theme's optional `metrics` and `chrome` come as the file has them, and
  are left out when it has none ([themes.md](themes.md), "Metrics and
  chrome"). Both, and `has_metrics`, are optional, so protocol version 11
  stays ("What changes the version", above).

A client changes the theme with `set_value` on `ui.theme`:

```json
{"id":"01M…","type":"set_value","path":"ui.theme","value":"nord"}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"config_changed","changed":["ui.theme"]}
{"id":"01M…","type":"theme_changed","theme":{"id":"nord",…}}
```

- `theme_changed` carries the whole theme, so a client applies it without
  asking. Every connection that said `hello` gets it whenever the theme in
  effect changes: `ui.theme` names another theme (through `set_value` or an
  edit of the file), or the file of the theme in effect was saved.
  `config_changed` and `theme_changed` may come in either order.
- `set_value` refuses a theme with no valid file with `config_error`; the
  file is not touched.
- A hand edit that names a theme with no valid file takes effect for the
  other settings; the theme in effect stays, and a `config_error` event
  (with `line` and `column` `null`) says why. A saved edit that makes the
  theme in effect invalid is reported the same way: a theme is never applied
  half-way. Each problem is reported once; when it is fixed (the file
  appears, or is corrected), `theme_changed` follows.
- A client that fell behind on events gets a `theme_changed` with the theme
  in effect.

## The marketplace

Extensions come from an index ([marketplace.md](marketplace.md) has the
format, the folders and the trust rules). `marketplace.index` in the
configuration says where it is; the core reads it only when a client asks.

```json
{"id":"01M…","type":"marketplace_refresh"}
{"id":"01M…","type":"marketplace_index","source":"C:\\market\\index.json","fetched_at_ms":1790000000000,
 "items":[{"id":"hello","kind":"plugin","name":"Hello","author":{"name":"CabinetOS","verified":false},
 "version":"0.1.0","description":"…","long":"…","size":27003,
 "download":{"url":"files/hello-0.1.0.zip","sha256":"8818…cac3"},"manifest":{…},
 "capabilities":[{"name":"cmd:register","reason":"…","level":"low"},…],
 "minCoreVersion":"0.1.0","license":"MIT"},…]}
{"id":"01M…","type":"marketplace_search","query":"nord","kind":"theme"}
```

- `items` are in the index file's own format (camelCase keys); the core
  adds each capability's `level`, and `installedVersion` when the
  marketplace installed that extension. There is one item per extension:
  the newest version this core can run (one it cannot run is left out).
  After `marketplace_refresh` they come in index order; after
  `marketplace_search`, best first, ranked as the palette ranks commands,
  by name, ID and publisher. An empty `query` keeps every item.
- `marketplace_search` searches the index read last; it reads the index
  first when there is none yet, or when `marketplace.index` changed.
- `source` is the index's file or URL; `fetched_at_ms` is when it was read
  or confirmed unchanged, in milliseconds since 1970-01-01 UTC.

```json
{"id":"01M…","type":"install_extension","extension_id":"hello"}
{"id":"01M…","type":"install_progress","extension_id":"hello","bytes":27003,"total":27003}
{"id":"01M…","type":"install_finished","extension_id":"hello","ok":true,"message":"installed hello 0.1.0 (plugin)","installed_version":"0.1.0"}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"plugin_state_changed","plugin_id":"hello","state":{"type":"needs_review","missing":["cmd:register","events:emit"]}}
```

- The field is `extension_id`, not `id`, because `id` is the request's own
  ID in the same object.
- The reply comes once the extension is in place (or with the error).
  Every connection that said `hello` gets `install_progress` (at most 30 a
  second, and always one when the download is complete) and
  `install_finished`, whichever client asked. `install_finished` and the
  reply travel apart, so either may come first.
- `installed_version` is the version installed from the marketplace once
  the install ended, as the record of installs has it: the new version
  when it worked; after a failed update, the version from before. It is
  absent when none is installed, and from cores older than the field. A
  client that did not ask (another window, the CLI with a `version`)
  updates the item's `installedVersion` from it without a refresh.
- A plugin then waits in `needs_review`; a theme that `ui.theme` names
  brings `theme_changed`; a tool brings `tools_changed`.

```json
{"id":"01M…","type":"uninstall_extension","extension_id":"hello"}
{"id":"01M…","type":"ok"}
{"id":"01M…","type":"list_tools"}
{"id":"01M…","type":"tools","tools":[{"id":"md-preview","name":"Markdown Preview",
 "version":"1.0.0","author":"CabinetOS","description":"…","dir":"C:\\…\\tools\\md-preview"}]}
{"id":"01M…","type":"tools_changed","tools":[…]}
```

- `uninstall_extension` removes exactly the files the install put in
  place. A plugin leaves `list_plugins`; there is no event for it.
- `tools` lists every tool in the tools folder with a valid `tool.json`,
  by ID; `dir` is its folder. `tools_changed` carries the same list after
  a tool install or uninstall. A client that fell behind on events gets a
  `tools_changed` with the tools as they are, after the other events sent
  again.

## Previews

A preview shows proposed changes as a listing, with nothing done on disk
until the user applies it. Any client may propose one (an extension's
tool, the command line), and so may a plugin; a window shows it in a pane
like a folder.

```json
{"id":"01M…","type":"preview_listing","title":"Sort the photos","rows":[
 {"path":"C:\\Users\\me\\Pictures\\Beach\\","kind":"create","to":null},
 {"path":"C:\\Users\\me\\Pictures\\IMG_1.jpg","kind":"rename","to":"2026-09-30 beach.jpg"},
 {"path":"C:\\Users\\me\\Pictures\\2026-09-30 beach.jpg","kind":"move","to":"C:\\Users\\me\\Pictures\\Beach"}]}
{"id":"01M…","type":"preview_opened","preview":"preview-3","title":"Sort the photos",
 "listing":{"listing_id":9,"section_handle":1188,"section_size":736,"entry_count":3,
            "generation":1,"elapsed_us":210}}
```

- Each row has an absolute `path` (not a volume root), a `kind` and a
  `to`:

  | `kind` | `to` | What applying does |
  |---|---|---|
  | `rename` | the new name, or the new full path in the same folder | renames it, never replacing anything |
  | `move` | the folder it goes into | moves it there, as a move job |
  | `copy` | the folder it goes into | copies it there, as a copy job |
  | `delete` | `null` | puts it into the Recycle Bin, as a delete job |
  | `create` | `null` | creates it: a folder when `path` ends with `\`, else an empty file (never replacing anything) |

- The core checks every row before it keeps anything, and refuses the
  whole preview with `invalid_path` (a relative path, a root, a `rename`
  without a valid name or to another folder, a `move` or `copy` without an
  absolute folder, a `delete` or `create` with a `to`) or
  `protocol_error` (no rows). Whether the paths exist is checked when the
  rows run, not before: a row may name what an earlier row makes.
- `preview_listing` needs `hello`: the reply's `listing` is a listing in
  shared memory, handed to the client's process as for `list_directory`,
  with the preview flag and the preview rows ("The listing section").
  `describe_entries`, `match_entries` and `close_listing` work on it; it is
  never refreshed. The core reads each row's metadata (one read per path)
  to fill it.
- `open_preview { preview }` opens the listing of a preview that exists
  already: one a plugin proposed, which a window shows when it hears of
  it, or one another window made. It answers `preview_opened` too.
- A preview lives until it is applied or cancelled, or for 10 minutes. A
  client (or a plugin) may keep at most 20 alive: the 21st is refused
  with `too_many_previews`. A preview does not end with the connection
  that made it.

```json
{"id":"01N…","type":"preview_apply","preview":"preview-3"}
{"id":"01N…","type":"jobs_started","jobs":[12,13]}
{"id":"01P…","type":"preview_applied","preview":"preview-3","jobs":[12,13]}
{"id":"01Q…","type":"preview_cancel","preview":"preview-4"}
{"id":"01Q…","type":"ok"}
{"id":"01R…","type":"preview_cancelled","preview":"preview-4"}
```

- `preview_apply` runs the rows in order, as jobs ([jobs.md](jobs.md),
  "Chains and steps"). Neighbouring rows that fit one job share it:
  renames and creates become one `steps` job; moves or copies into one
  folder one move or copy job; deletes one delete. Each job starts only
  when the one before it has completed; a job that does not complete
  (failed, cancelled, or a row that failed) cancels the jobs after it.
  The reply lists the jobs in order; their events follow as for any job.
  The preview is gone afterwards (`no_such_preview` for a second apply).
  When the first job is refused (its source does not exist, say), the
  reply is that error and the preview stays.
- `preview_applied` goes to every client that said `hello`, with the
  jobs; `preview_cancelled` too, when a preview is cancelled or when it
  expires unapplied.
- For tests, the environment variable `CABINETOS_PREVIEW_TTL_MS` sets the
  life of a preview in milliseconds.

## Secrets

Secrets, such as an API key a plugin's web requests need, live in the
Windows Credential Manager: generic credentials named `CabinetOS/<name>`,
kept for this user on this machine (`CRED_PERSIST_LOCAL_MACHINE`); the
Credential Manager in the Control Panel shows them under "Windows
Credentials", with the user name `CabinetOS`. The core reads one only to
put it into a request it makes for a plugin (`http-request` with `secret`,
[plugins.md](plugins.md)), so no plugin ever sees a value.

```json
{"id":"01M…","type":"secret_set","name":"anthropic","value":"sk-ant-…"}
{"id":"01M…","type":"ok"}
{"id":"01N…","type":"secret_list"}
{"id":"01N…","type":"secret_names","names":["anthropic"]}
{"id":"01P…","type":"secret_get","name":"anthropic"}
{"id":"01P…","type":"secret","value":"sk-ant-…"}
{"id":"01Q…","type":"secret_delete","name":"anthropic"}
{"id":"01Q…","type":"ok"}
```

- A name is 1 to 128 letters, digits, `-`, `_` and `.`; a value 1 to
  2,560 bytes of UTF-8 (the Credential Manager's limit). `secret_set`
  replaces a secret of the same name.
- Only a pipe client sends these: a window (its settings) or the command
  line. The pipe admits only this user's processes; a plugin has no way to
  send them, and none of its host functions returns a value.
- **Never logged.** The core logs the name of each secret it stores,
  reads for a client, or removes, never a value; in the protocol crate a
  value is a `SecretText`, which prints as `<hidden>` wherever a message
  is formatted for a log. A test sets secrets with the log at `trace` and
  searches every log file for the value.
- `secret_list` answers the names, sorted, never the values.
- For tests, the environment variable `CABINETOS_SECRETS_PREFIX` replaces
  the `CabinetOS/` prefix, so a test never touches the user's own secrets.

`cabinetos-cli secret set <name>` reads the value from standard input
(one line ending at its end is dropped), which keeps it out of the shell's
history; `--value <text>` gives it on the command line instead. `secret get
<name>` prints the value, `secret delete <name>` removes it, and `secret
list` prints the names.

## What the window shows

The window owns its panes, their tabs, the cursor and the marks (Phase 12:
tabs per pane). It tells the core on each change, so a program without a
window of its own (the command line, a plugin) can ask what the user looks
at. The core only stores it.

```json
{"id":"01M…","type":"window_state","active_pane":"left","panes":{
 "left":{"tabs":[{"path":"C:\\Users\\me","locked":false,"tool":null},
                 {"path":"E:\\work\\README.md","locked":false,"tool":"md-preview"}],
         "active":0,"cursor":"C:\\Users\\me\\notes.txt","marked":["C:\\Users\\me\\notes.txt"]},
 "right":{"tabs":[{"path":"D:\\","locked":true,"tool":null}],"active":0,"cursor":null,"marked":[]}}}
{"id":"01M…","type":"ok"}
```

- `active_pane` is `left` or `right`: the pane that has the keyboard.
- Each pane has its `tabs`, left to right (`path`; `locked`; `tool`, the
  ID of the Tool Extension the tab shows, or `null` for a folder), the
  index of the tab in front (`active`, from 0), the full path of the cursor
  row (`cursor`, `null` when there is none) and the full paths of the
  marked rows (`marked`, in the pane's order). `locked`, `tool`, `cursor`
  and `marked` may be left out.
- It needs `hello`: the core keeps the last state per client, named by
  its `hello` name and a number the core gives the connection, such as
  `CabinetOS#2`. When the connection ends, its state goes.
- A window may send it at every change: the core only stores it. The log
  has a debug line for it, not an info line.

```json
{"id":"01N…","type":"get_window_state"}
{"id":"01N…","type":"window_state","client":"CabinetOS#2","sent_at_ms":1790000000000,
 "state":{"active_pane":"left","panes":{…}}}
```

`get_window_state` answers the state of the client that sent one last, or
of the client named in `client`; the state exactly as it was sent, and
when the core received it (`sent_at_ms`, milliseconds since 1970-01-01
UTC). With no state, or none from that client, it answers `no_window`.
It needs no `hello`.

`cabinetos-cli state` prints it as a table (who sent it and when, each
pane with its tabs, the tab in front marked `*`, the cursor and the number
of marked rows); `--json` prints the state as it was received, and
`--client <id>` asks for another window than the newest.

## Trying it by hand

`cabinetos-cli` speaks this protocol: `ls` maps the section and prints it,
`ls --watch` prints each `listing_refreshed`, `volume` prints `volume_info`,
`describe` prints `entry_details` beside the names, `icon` writes an
`icon` to a PNG file, `volumes` prints `volumes`, `open`, `mkdir` and
`rename` send
`open_path`, `create_directory` and `rename`, `config get` and
`config set` send `get_value` and `set_value`,
`config`, `commands` and `keys` cover the configuration messages (`keys
watch` prints their events), `copy`, `move`, `delete`, `jobs` and `job`
cover the jobs, `plugins`, `commands exec` and `events watch` cover the
plugins, `search` and `index status` cover file search, `term` covers
the terminal sessions, `themes list` and `themes show` send
`list_themes` and `get_theme`, and `market` covers the marketplace. See
[core/README.md](../core/README.md).
