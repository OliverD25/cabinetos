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

A client that wants listings says `hello` first, once per connection:

```json
{"id":"01M…","type":"hello","client_pid":4242,"client_name":"CabinetOS"}
{"id":"01M…","type":"welcome","protocol_version":2,"core_version":"0.1.0"}
```

`client_pid` must be the process on the other end of the pipe; the core asks
Windows and refuses a mismatch with `protocol_error`. The core needs the PID
because it duplicates shared-memory handles into that process. A
`list_directory` before `hello` fails with `protocol_error`, message
`hello required`. `ping`, `shutdown` and `volume_info` work without it.

## Requests and replies

| Request | Fields | Reply |
|---|---|---|
| `ping` | — | `pong` (`protocol_version`, `core_version`) |
| `shutdown` | — | `ok`, then the core exits with code 0 |
| `hello` | `client_pid`, `client_name` | `welcome` (`protocol_version`, `core_version`) |
| `list_directory` | `path`; `include_hidden` (default `false`); `sort` (default `{"key":"name","descending":false}`); `watch` (default `false`) | `listing_opened` |
| `close_listing` | `listing_id` | `ok` |
| `volume_info` | `path` (need not exist) | `volume_info` |

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

Requests on one connection are independent: `list_directory` and
`volume_info` run in the background, so a slow directory does not hold up
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

## Trying it by hand

`cabinetos-cli` speaks this protocol: `ls` maps the section and prints it,
`ls --watch` prints each `listing_refreshed`, and `volume` prints
`volume_info`. See [core/README.md](../core/README.md).
