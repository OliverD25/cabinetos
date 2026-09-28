## Phase 2 report

**Built:** The filesystem engine and the listing protocol.
- **Listing:** `cabinetos-fs` reads a directory with `NtQueryDirectoryFile`: names, file IDs, sizes, times and attributes in one pass. It sorts in the core (directories first, then Explorer's natural order) and writes an exactly sized v2 section: 40-byte header, 16-byte entries, 40-byte `ListingMeta`, then a UTF-16 name arena. A safe, bounds-checked `ListingReader` reads it back.
- **Volumes:** volume and disk detection works without admin rights. Checked here: C/D/E/G are NVMe SSDs, H is a SATA HDD with seek penalty.
- **Watching:** one `ReadDirectoryChangesW` thread per watched folder, with a stop event and `CancelIoEx`. Watching never blocks deleting the folder.
- **Core:** the core serves `hello` (checked against the client's real PID), `list_directory` (optionally watched), `close_listing` and `volume_info`. It sends `listing_refreshed` and `listing_lost` events: 50 ms settling delay, at most 30 per second, the directory re-read into a new section each time.
- **Client:** `PipeClient` got a background reader. It matches replies by ID, sends events to a channel, and hands out received section handles through a safe `take_section`.
- **CLI:** `ls [--long --hidden --sort --desc --watch]` and `volume`.
- **Also:** docs (`docs/ipc.md` with a byte diagram, README, diagnostics, crate map), a criterion benchmark, and CI compiling the benchmarks.
- **Part A:** release line tables, a lock-free ring buffer, 14 kept log files, 4 async workers (`CABINETOS_WORKERS` overrides).

**Commits** (all pushed; `git status` is clean and in sync with `origin/main`):
fc87ee6 diag, core: lock-free ring buffer, 14 kept logs, fixed worker count
1a9f292 protocol v2: hello, directory listings, volume info, events, layout v2
f29a910 fs: NT-API enumeration, natural sort, listing sections, volumes, watching
19c4a62 ipc: split connections, a client with a reader task, owned section handles
70dc56a core: listings in shared memory, watched listings with events, volume info
4c306ce cli: ls from shared memory, ls --watch, volume
0b3a9a7 ci: compile the benchmarks on every change
b1ead4e docs: the IPC protocol and listing layout, Phase 2 in README and crate map

**Checks** (from `core/`, final code):
- `cargo build --workspace`: pass (`Finished dev profile`).
- `cargo test --workspace`: pass. 147 passed, 0 failed, 0 ignored. The junction test was not ignored: `mklink /J` works on this machine and on the runner.
- `cargo clippy --workspace --all-targets -- -D warnings`: pass, exit 0.
- `cargo fmt --all -- --check`: pass, exit 0.
- `cargo deny check`: `advisories ok, bans ok, licenses ok, sources ok`.
- The core end-to-end suites passed 8 extra repeat runs out of 8.
- CI is green on b1ead4e: https://github.com/OliverD25/cabinetos/actions/runs/36366568100. Both jobs pass and 147 tests ran on the runner. The earlier Phase 2 run 36366402904 was cancelled by the next push, not failed.

**Benchmarks** (criterion medians, warm, this PC, fixtures in `%TEMP%\cabinetos-bench\<n>\`):

| Files | Ours: list and sort | Ours: list, sort and write into shared memory | Baseline: `std::fs::read_dir` + metadata (no sort) |
|---|---|---|---|
| 1,000 | 0.68 ms | 0.79 ms | 0.60 ms |
| 100,000 | 71.7 ms | 74.6 ms | 61.0 ms |

- **Buffer size**, 100k files: 64 KiB 70.8 ms, 256 KiB 71.6 ms, 1 MiB 72.1 ms. No difference, because NTFS returns at most about 64 KiB per call (172 calls for 11 MB). I kept 256 KiB.
- **Phase breakdown** for 100k:
  - kernel enumeration 40–53 ms (the floor);
  - parsing 4 ms;
  - sort 14 ms: keys 3 ms on 8 threads, comparison sort 8.5 ms, reorder 1 ms;
  - section write about 3.5 ms (page faults included).

**Live check** (release binaries, core on pipe `live2`, log in a scratch folder):
```
== ls C:\Users\Admin\AppData\Local\Temp\cabinetos-bench\100000 (first run of this core)
core: enumerated 100000 entries in 80.7 ms
client: mapped and read in 19.8 ms
== warm run
core: enumerated 100000 entries in 80.6 ms
client: mapped and read in 19.8 ms
== warm run again
core: enumerated 100000 entries in 76.7 ms
client: mapped and read in 19.8 ms
== ls C:\Windows\System32 --long (first 10 lines)
d ----- 2025-03-15 22:40           <DIR> %userprofile%
d ----- 2024-04-01 19:28           <DIR> 0409
d ----- 2025-11-07 20:56           <DIR> 1028
d ----- 2025-11-07 20:56           <DIR> 1029
d ----- 2025-11-07 20:56           <DIR> 1031
d ----- 2025-11-07 20:56           <DIR> 1033
d ----- 2025-11-07 20:56           <DIR> 1036
d ----- 2025-11-07 20:56           <DIR> 1040
d ----- 2025-11-07 20:56           <DIR> 1041
d ----- 2025-11-07 20:56           <DIR> 1042
(timing lines: core: enumerated 4923 entries in 3.8 ms / client: mapped and read in 1.1 ms)
== ls <temp>\watched --watch, while creating new-file.txt, then second.txt
- existing.txt
core: enumerated 1 entries in 0.2 ms
client: mapped and read in 0.0 ms
refreshed: 2 entries, generation 2, reason changed
refreshed: 3 entries, generation 3, reason changed
== volume C:\
drive_letter: C
volume_guid_path: \\?\Volume{2ce13c93-6214-4396-8130-3984ca73b7f2}\
filesystem: NTFS
label: System Disk
total_bytes: 1999433101312 (1.82 TiB)
free_bytes: 1318446592000 (1.20 TiB)
disk.device_number: 1
disk.bus_type: NVMe
disk.seek_penalty: false
disk.media_type: SSD
== volume H:\
drive_letter: H
volume_guid_path: \\?\Volume{ff50b21c-1299-4d79-b658-b3b81ecdc062}\
filesystem: NTFS
label: HHD
total_bytes: 12000013840384 (10.91 TiB)
free_bytes: 10391482793984 (9.45 TiB)
disk.device_number: 0
disk.bus_type: SATA
disk.seek_penalty: true
disk.media_type: HDD
```
- Folder `%userprofile%` is a real folder in System32 on this PC.
- Only warm runs exist. Every "first run" was warm, because the file cache still held the fixture (a true cold run needs a cache flush or a reboot).
- The CLI's 20 ms client time is decoding 100,000 names into Rust strings. The UI will read by pointer instead.
- After `shutdown`, the core printed `cabinetos-core exited with code 0`. Its log holds `client said hello` and `listing opened` lines under their request IDs.

**Decided:**
- **Record class:** `FileIdFullDirectoryInformation` instead of `FileIdBothDirectoryInformation`.
  - Because: the "both" class adds the 8.3 short name, which NTFS looks up per file. Measured: 100 ms vs 46 ms for 100k files, and the extra field is never used.
  - Undo: swap the class and struct in `crates/cabinetos-fs/src/enumerate.rs` and the pinned offsets test.
- **Natural sort:** Windows sort keys (`LCMapStringEx` with `SORT_DIGITSASNUMBERS | NORM_IGNORECASE`, user locale), compared as bytes, instead of `StrCmpLogicalW` in the comparator.
  - Because:
    - 155 ms vs 41 ms on 100k mixed names;
    - a byte comparison is a strict total order, while Rust's sort may panic on a comparator that is not one;
    - a test checks the key order against `StrCmpLogicalW` on 630 names (the `StrCmpLogicalW` bindings are compiled for tests only).
  - Known differences: numbers longer than 19 digits, and non-ASCII digits (① ٣).
  - Undo: replace `compare()` in `sort.rs` with a `StrCmpLogicalW` call.
- **Sort speed-ups:** keys are built on up to 8 threads for listings of 4,096 entries or more, and each sort item carries a 16-byte key prefix.
  - Because: this cut the sort from 26 ms to 14 ms for 100k. A 32-byte prefix was measured and was slower.
  - Undo: `PARALLEL_FROM` and `SortItem` in `sort.rs`.
- **Link kind:** only name-surrogate reparse points (symlinks, junctions, WSL links) are kind `reparse_point`. The tag comes from `EaSize`.
  - Because: OneDrive placeholders and deduplicated files carry the reparse attribute but are ordinary files to the user.
  - Undo: `kind_of()` in `enumerate.rs`.
- **Hidden filter:** with `include_hidden: false`, entries with HIDDEN or SYSTEM are hidden (either attribute, not both).
  - Because: that is the plain reading of the spec.
  - Undo: `HIDDEN_OR_SYSTEM` test in `enumerate.rs`.
- **Sort edge rules:** directories always first. `descending` reverses the order within each group. Kind order is directory, file, link.
  - Because: Total Commander style, and stable for the UI.
  - Undo: `group_rank` and `kind_rank` in `sort.rs`.
- **Header size:** `ListingHeader` got a trailing `reserved: u32`, making it 40 bytes.
  - Because: the 8-byte-aligned entry array then starts right after it.
  - Undo: drop the field, set `entries_offset` to 40 with padding, and bump the layout tests.
- **Protocol version:** `PROTOCOL_VERSION` is now 2.
  - Because: messages were added, and the doc comment says to raise it then.
  - Undo: `protocol/src/lib.rs`.
- **Volume reply type:** `Response::VolumeInfo(VolumeDetails)` is a named struct in a newtype variant, not an inline struct variant.
  - Because: the wire form is identical and fs returns the same struct.
  - Undo: inline the fields.
- **Event schema:** added `sdk/protocol/event.schema.json`.
  - Because: C# needs the event shapes too.
  - Undo: remove `event_schema`.
- **Hello check:** `hello`'s `client_pid` must equal `GetNamedPipeClientProcessId`; otherwise `protocol_error`. A second `hello` is also refused.
  - Because: the core duplicates handles into that PID.
  - Undo: `Session::hello` in `connection.rs`.
- **Client section handles:** `SharedSection::from_raw_handle(handle, size)` is `unsafe` and takes the size (the spec had `from_raw_handle(u64)`). Clients use the safe `PipeClient::take_section(handle)`, which only accepts handle values received in `listing_opened` or `listing_refreshed`, once each. Untaken handles are closed on drop.
  - Because: a safe function that closes an arbitrary number is unsound, and the CLI must stay `forbid(unsafe_code)`.
  - Undo: `client.rs` and `shm.rs`.
- **Events accessor:** `PipeClient::events()` returns `Option<Receiver>` (only the first call gets it). The channel is unbounded.
  - Because: events carry handles, so dropping one would leak a handle. The 30 Hz rule bounds the growth.
  - Undo: `client.rs`.
- **Client error handling:**
  - a reply to an ID nobody waits for fails every waiting request;
  - an unparseable reply fails only its own request;
  - an unknown event is skipped.
  - Because: this stays forward-compatible with newer cores.
  - Undo: `read_loop` in `client.rs`.
- **Concurrent requests:** `list_directory` and `volume_info` run as tasks; everything else is answered inline.
  - Because: one slow directory must not block the next request (Article 5, two panes).
  - Undo: `Session` in `connection.rs`.
- **Ordering:** the listing reply is queued before the refresh task starts.
  - Because: the client must learn the listing ID before any event about it.
  - Undo: `listing_ready`.
- **Watch start order:** the watcher starts before the first read, and `DirectoryWatcher::start` returns only after the first request is with Windows.
  - Because: no change may be missed.
  - Undo: `open_listing` and `watch.rs`.
- **Refresh timing:** 50 ms settling delay (the first change starts a 50 ms window, then one refresh), plus a minimum 33.3 ms between refreshes. Overflow in a burst makes the reason `overflow`. Any watcher failure or re-read failure sends `listing_lost`.
  - Because: this is the spec's rule, and it guarantees progress under continuous change.
  - Undo: `listing.rs` constants and `refresh_loop`.
- **Listing IDs** are global across connections, not per connection.
  - Because: a listing ID in the log then names one listing.
  - Undo: `NEXT_LISTING_ID`.
- **Metadata in the same pass:** size, times and attributes arrive with the listing. The `Hydrator` placeholder stays, for icons and type names in Phase 5.
  - Because: `NtQueryDirectoryFile` returns them anyway.
  - Undo: none needed.
- **Publishing and refreshing:** a listing is published once, complete. A refresh re-reads the whole folder rather than patching.
  - Because: the spec says so; the numbers are above.
  - Undo: none.
- **Disk queries** open the volume GUID path, and query properties on `\\.\PhysicalDriveN` (falling back to the volume handle), instead of `\\.\X:`.
  - Because: letterless (mounted-folder) volumes work too.
  - Undo: `disk_identity()` in `volume.rs`.
- **Volume field values:**
  - `media_type` is "HDD" or "SSD", from the seek penalty;
  - `free_bytes` is the space free for this user (quotas applied);
  - `volume_guid_path` is empty for network shares;
  - `GetVolumePathNameW` gets the plain path (verbatim only over 260 characters), so a missing drive is `not_found`.
  - Undo: `volume.rs`.
- **Ring buffer design:** writers only touch a crossbeam `ArrayQueue` with `force_push`. Readers drain it into a reader-side archive, so `recent_events()` does not consume lines. The panic hook falls back to the queue alone if the archive is busy.
  - Because: the spec wants writers lock-free and the crash trace still needs older lines.
  - Undo: `ring.rs`.
- **Runtime threads** are renamed from `core-worker-N` to `core-rt-N`.
  - Because: Tokio uses one name function for workers and for the blocking pool.
  - Undo: `main.rs`.
- **CLI output:** all stdout output goes through `say()`, which stops quietly when stdout is closed.
  - Because: `ls … | head` panicked in `println!`.
  - Undo: `say` in `cli/src/main.rs`.
- **CLI request timeout** raised from 5 s to 60 s.
  - Because: large folders or network shares.
  - Undo: `REQUEST_TIMEOUT`.
- **Bench design:** fixture names are a realistic mix (numbered photos and documents, spaces and punctuation, Ukrainian, Japanese). A `<n>.complete` marker sits next to each folder, so it is never listed. `list_and_publish` writes into a real `SharedSection`.
  - Because: this is representative.
  - Undo: `benches/list_directory.rs`.
- **Part A details:**
  - release profile `debug = "line-tables-only"` (the `.pdb` is separate; the `.exe` stays 1.8 MB);
  - `KEPT_LOG_FILES = 14` pruning is tested; it leaves crash files and other processes' logs alone;
  - `CABINETOS_WORKERS` accepts 1–64 and warns in the log otherwise.

**Needs the user:**
- **`docs/PLAN.md` Phase 2 section and the Notion desk card.** I did not edit PLAN.md, because a PLAN change must be mirrored to the Notion desk card in the same session, and this run must not touch Notion. Ready to paste into the "Phase 2 — Filesystem engine v1" heading and Produces/Done-when lines:
  - heading: add `— done 2026-09-28`;
  - "asynchronous hydration (size, dates, attributes, icon key) in chunks" → "size, dates and attributes in the same pass as the names (NtQueryDirectoryFile); icon and type-name hydration moves to Phase 5";
  - "`DirectoryChanged` events" → "`listing_refreshed` / `listing_lost` events (docs/ipc.md)";
  - "and the benchmark runs in CI" → "and the benchmark compiles in CI (it runs locally)";
  - add the measured result: "100,000 entries: 72 ms list+sort, 75 ms into shared memory (kernel enumeration floor ≈ 45 ms); the 50 ms target needs the pipelined design, see the Phase 2 report".
  Then refresh the desk card "Development Plan (mirror of the repo plan)".

**Known gaps:**
- **The 50 ms target is not met.** A 100,000-entry listing reaches shared memory in 72–81 ms warm (bench median 74.6 ms). The kernel's own enumeration is about 45 ms (40–53 ms), which leaves about 5 ms for sorting and writing, and they take about 18 ms. Nothing is failing; the target is reported as asked.
- **The `ls --watch` Ctrl+C path is tested by hand only.** Tests cannot send Ctrl+C to a child process. The watch itself is covered end to end through `PipeClient`.

**Noticed, out of scope:**
- **Reaching about 50–55 ms** would take three changes:
  - build sort keys and sort each `NtQueryDirectoryFile` batch while the kernel produces the next one;
  - merge at the end;
  - write the section on several threads.
  Showing the first screenful in a few ms instead would need chunked publishing (the header `flags` field is free for a "complete" bit). Both are real design work.
- **ReFS:** 64-bit `FileId` may not be unique there; `FileIdExtdDirectoryInformation` gives 128-bit IDs if ReFS matters.
- **Paths are UTF-8 JSON strings**, so a folder whose name has unpaired UTF-16 surrogates cannot be requested by path. Listed names are fine, because they are UTF-16 in the section.
- **A test leaks two handles:** the core's refresh-loop unit test duplicates two section handles into the test process itself (documented in the test, harmless).
- **Accidental crash file:** during a manual check, `cabinetos-cli ls … | head` panicked on the closed pipe (the cause is now fixed, in 4c306ce). The panic hook wrote a crash file into the real `%LOCALAPPDATA%\CabinetOS\logs`. I moved it, not deleted it, to `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\accidental-crash\crash-20260928T012814883Z.json`. The real log folder now holds only the Phase 1 live-check file.

Key files:
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\ipc.md
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\src\enumerate.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\src\sort.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-core\src\connection.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-core\src\listing.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-ipc\src\client.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\benches\list_directory.rs
