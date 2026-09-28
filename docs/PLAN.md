# CabinetOS Development Plan

Status: draft v1, written 2026-09-28. This is a living document. Any session may
propose changes. A phase starts only when the creator says so.

How the project documents relate:

- [CONSTITUTION.md](../CONSTITUTION.md) is the law. It says *what* CabinetOS is. Protected; changes need the creator's approval.
- `docs/ARCHITECTURE.md` (the implementation brief, added in Phase 0) says *how* it is built.
- This plan says *in which order*, and records the decisions that were made along the way.
- `docs/decisions/` holds one short decision record (ADR) per decision: the question, the choice, the reason.

---

## 1. Decisions made on 2026-09-28

All eight were asked as questions and answered by the creator.

| # | Question | Decision | Reason in one line |
|---|----------|----------|--------------------|
| 1 | Language for the WinUI 3 frontend | **C# on .NET** | Mainstream WinUI 3 path, best tooling. `unsafe` C# reads shared memory without copying. The Dumb UI Rule keeps the garbage collector idle. |
| 2 | Administrator rights for MFT/USN indexing | **Separate elevated indexer** | UI and core run as a normal user. A small indexer process runs as admin and hands the index to the core. Without it, the core falls back to fast NT directory reading. |
| 3 | Open-source license | **MIT** | Shortest permissive license. Most common in the Rust and C# worlds. Matches the design prototype's badge. |
| 4 | Minimum Windows version | **Windows 11 22H2 (build 22621) or newer** | Mica, IoRing, Segoe Fluent Icons and Segoe UI Variable all exist. No fallback code paths. |
| 5 | Integrated terminal: core or extension | **Core, hidden by default** | Article 9 names the terminal as a system feature, so it is infrastructure. Article 4 hides it until `Ctrl+`` `. |
| 6 | Control-channel message format | **JSON** | Human-readable, easy to log and test by hand. Payloads are tiny; big data goes through shared memory. MessagePack is the upgrade path if ever needed. |
| 7 | Repository layout | **Monorepo** | `core/` (Rust), `ui/` (C#), `sdk/` (plugin interface), `docs/`. One commit can change both sides of the IPC contract. |
| 8 | The brief and the design handout | **Commit both; the brief is a living document** | Brief becomes `docs/ARCHITECTURE.md`; design bundle goes to `docs/design/`. Only the Constitution stays protected by the edit hook. |

## 2. Defaults chosen by the architect (say so to change any of them)

These did not need a question. Each has a conventional answer. They are listed
so nothing is hidden.

- **Name.** "FileForge" in the design bundle is the design codename only. The product, the executables, the config file and the title bar all say **CabinetOS**.
- **CPU architecture.** x64 first. An ARM64 build (Windows on ARM laptops) is added to CI as soon as the core compiles. It costs almost nothing early and a lot later.
- **Rust toolchain.** Stable channel, pinned in `rust-toolchain.toml`. Edition 2024. The minimum supported Rust version (MSRV) is the stable version at scaffold time (1.98).
- **Async runtime.** `tokio` (multi-threaded) for the pipe server, timers and job orchestration. Blocking disk I/O runs on dedicated threads, never on the async workers.
- **Process layout.** Three executables (see section 3). One core process per UI window process; the core exits when the UI process ends.
- **Config file.** One file, `%APPDATA%\CabinetOS\cabinetos.json`, as the brief says. Keybindings live inside it. A JSON Schema is published so editors can autocomplete it (Article 6).
- **Shared-memory strings.** File names in shared memory are stored as UTF-16. Windows APIs return UTF-16, and C# strings are UTF-16, so nobody converts anything.
- **Tool Extensions and crash isolation.** Third-party Tool Extensions run in WebView2, which is a separate process, so a crash cannot take the window down. Native WinUI controls are allowed only for first-party tools that live in this repo (the terminal). Reason: the brief requires the window to survive a Tool Extension crash, and an in-process control cannot promise that.
- **Log location.** `%LOCALAPPDATA%\CabinetOS\logs\` with one JSON Lines file per process and UTC day: `core.<date>.jsonl`, `ui.<date>.jsonl`, `indexer.<date>.jsonl` (the date is in the name because the appender rolls the file daily; see [diagnostics.md](diagnostics.md)).
- **Development workflow.** This chat plans and reviews. The `coder` agent implements from self-contained prompts. One commit per finished unit of work.

## 3. Architecture summary

Three processes, two trust levels:

```
 ┌──────────────────────────┐   named pipe: JSON commands + events (control channel)
 │ CabinetOS.exe            │◄──────────────────────────────────────────────────►┐
 │ C# · WinUI 3 · normal    │   shared memory: directory listings (data channel) │
 │ user                     │◄──────────────────────────────────────────────────►│
 │ draws pixels, captures   │                                                    │
 │ keystrokes, CommandRouter│                              ┌─────────────────────▼──────────────┐
 └──────────────────────────┘                              │ cabinetos-core.exe                 │
                                                           │ Rust · normal user · headless      │
 ┌──────────────────────────┐   named pipe: read-only      │ fs engine · job queues · config    │
 │ cabinetos-indexer.exe    │   index queries + change     │ command registry · plugin host     │
 │ Rust · Administrator     │◄─────────────────────────────┤ (wasmtime) · ConPTY terminal host  │
 │ MFT reader · USN tailer  │   notifications              │ diagnostics                        │
 │ Windows service or       │                              └────────────────────────────────────┘
 │ started on demand        │
 └──────────────────────────┘
```

- **Control channel.** Length-prefixed JSON messages over a named pipe. Every message carries a request ID (a ULID, a sortable unique ID) that the UI creates and the core logs, so one action can be traced across all boundaries (Article 12, brief section 8).
- **Data channel.** For a listing, the core creates a memory-mapped section, writes `#[repr(C)]` structs plus a UTF-16 name arena, and hands the UI a duplicated handle over the pipe. The UI reads it by pointer. No copy, no serialization.
- **Security.** The pipe is created with a DACL (an access list) that allows only the current user. The pipe name includes a random token that the UI passes to the core at launch. The indexer answers read-only queries and never performs file operations (least privilege).
- **Fallback.** If the indexer is not installed or not running, the core lists directories with the NT API directly (`NtQueryDirectoryFile` / `FindFirstFileExW` with large fetch). Search falls back to a walk. Non-NTFS drives (FAT32 sticks, network shares) always use this path.

## 4. Consistency check: brief and design vs. the Constitution

Places where the brief or the design handout disagree with the Constitution, or with each other. Each needs a decision at the phase where it matters.

| # | Where | Conflict | Proposed handling | Decide in |
|---|-------|----------|-------------------|-----------|
| A | Brief §8, first sentence | Says "telemetry"; the creator renamed Article 12 to "Diagnostics" because telemetry suggests data sent home | Done in Phase 0: `docs/ARCHITECTURE.md` says "diagnostics" and its change log records the edit. The creator's preference was already known from the Article 12 rename | Phase 0 (done) |
| B | Design §1 vs §3A | §1: first run is a single pane. §3A: dual pane by default. Article 5 makes dual-pane the primary paradigm; Article 4 wants a casual user at ease | Decided by the creator 2026-09-28: **dual pane on first start**, single pane one toggle away (`view.toggleDualPane`, Ctrl+Shift+D). `ui.dualPane` already defaults to `true` | Phase 5 (decided) |
| C | Brief §6 Layer 2 | "WinUI UserControls or WebView2" and "if they crash, the window must remain responsive". An in-process control cannot guarantee that | WebView2 for third-party Tool Extensions; native controls only first-party (section 2 default) | Phase 7 |
| D | Design sidebar "Tags" | File tagging is a feature, not navigation. Article 10 says features are opt-in | Tags become a first-party plugin (core plugin stores tags; UI hook draws the dot). Not in the core | Phase 7 |
| E | Design keybinding hint | Sidebar shows "Ctrl+K W"; the command list says "Ctrl+K then Ctrl+W" | Pick one when seeding the command registry | Phase 3 (done: `ctrl+k ctrl+w`, [keybindings.md](keybindings.md)) |
| F | Brief §2 "requiring Admin privileges" | Resolved by decision 2 (separate elevated indexer) | Done | — |
| G | Design: marketplace as a core view | Article 10 says zero supplementary tools, but Article 8 makes the marketplace the way to add everything else | Marketplace stays core: it is infrastructure, like the terminal (decision 5) | — |

## 5. Phases

Each phase lists its goal, what it produces, when it is done, and which
Constitution articles it serves. Phases 1–4 are core-only and testable from
the command line, before any UI exists. That is deliberate: the Dumb UI Rule
means the core must work on its own.

### Phase 0 — Repository foundation (docs and hygiene, no code) — done 2026-09-28

Goal: the repo holds every governing document and is ready for code.

Produces: `LICENSE` (MIT); `docs/ARCHITECTURE.md` (the brief, word for word); `docs/design/` (the design bundle, unzipped); `docs/decisions/0001`–`0008` (today's decisions); `.gitattributes` (fixed line endings per file type, so every machine gets the same bytes); `.editorconfig`; `.gitignore` for Rust and .NET; the monorepo folders `core/`, `ui/`, `sdk/`, `docs/`; an updated `README.md`; `CLAUDE.md` gains a section on layout, build and test commands, and the rule that the brief is living.

Done when: everything above is committed and pushed, and a fresh clone shows the same line endings on Windows and Linux.

Articles: 2 (license), 6 and 12 indirectly (documents exist to be followed).

### Phase 1 — Rust core scaffold (the first coding task) — done 2026-09-28

Goal: an empty but real core. Every crate exists with its responsibility written down, the diagnostics pipeline works, and a command-line client can talk to the core over the pipe. Detailed in section 6.

Done when: `cargo build`, `cargo test`, `cargo clippy -- -D warnings` and `cargo fmt --check` pass locally and in CI; `cabinetos-cli ping` sends a request with an ID and the same ID appears in `core.<date>.jsonl`.

Articles: 1 (non-blocking design from day one), 12 (diagnostics first).

### Phase 2 — Filesystem engine v1 — done 2026-09-28

Goal: list any directory fast, stream metadata afterwards, and notice changes.

Produces: NT-API directory enumeration (`NtQueryDirectoryFile`); the shared-memory listing format (ID, name, kind) with a versioned header; size, dates and attributes in the same pass as the names, because the kernel returns them with the listing (icon and type-name hydration moves to Phase 5); natural sort in the core, directories first; volume and physical-disk detection for a path; `ReadDirectoryChangesW` watching of open directories with `listing_refreshed` / `listing_lost` events ([ipc.md](ipc.md)); CLI `ls` that renders from shared memory; benchmarks with a 100,000-file fixture directory.

Done when: a 100,000-entry listing appears in shared memory in under 50 ms on this PC (first-cut target, to be measured and revised), and the benchmark compiles in CI (it runs locally).

Measured 2026-09-28 on this PC, warm cache, after the pipelined follow-up the creator approved: 100,000 entries reach shared memory in 46–49 ms (criterion medians of `list_and_publish` in three runs; 48–50 ms end to end through the CLI). **The follow-up's 55 ms goal is met, and the first-cut 50 ms target too in these runs, with little margin.** While the kernel fills the next buffer (about 64 KiB of names per call), one worker thread parses the previous one, builds its sort keys and sorts it; after the kernel's last call, one heap merge of the sorted runs (about 2 ms) gives the display order, and the section is written on four threads (1.4 ms instead of 3.8). The kernel's calls are the floor, about 80 % of the time, and they vary with the machine's load (the first call 4.5–10 ms, the other 169 calls 26–35 ms). The Phase 2 path stays behind `ListOptions::pipelined = false` for comparison: 60–68 ms in the same runs (75 ms when first measured). At 10,000 entries the pipelined path takes 5.2–6.2 ms against 5.3–7.4; below about 1,750 entries both paths run the same code (1,000 entries: 0.6–0.8 ms). Both write byte-identical sections (`core/crates/cabinetos-fs/tests/pipeline.rs`). The baseline `std::fs::read_dir` plus metadata, without sorting, takes 73–75 ms at 100,000 entries in these runs (61 ms in the first measurement). Showing the first screen within a few milliseconds would still need chunked publishing, which stays open design work.

Articles: 1, 5 (two independent pane sessions).

### Phase 3 — Config, commands and keybindings — done 2026-09-28

Goal: the command architecture exists before any button does.

Produces: `cabinetos.json` schema and defaults; file watcher with parse, validate, diff, and change events (Article 6 real-time sync); command registry seeded from the design's command list (id, category, name, default binding, source); chord semantics (1000 ms window) written as a spec shared by core and UI; the Immutable System Tier list (Article 7); keymap export to the UI.

Done when: editing the file by hand changes a binding in the running core within one second, and a test proves an immutable binding cannot be overridden.

Measured 2026-09-28 on this PC (debug build): a hand edit saved with `sed -i` reached a watching client 119 ms after the save; a broken save was reported as `config_error` 104 ms after it (`cabinetos-cli keys watch`). The end-to-end test `the_immutable_tier_cannot_be_rebound` proves the tier holds. Specs: [keybindings.md](keybindings.md), [config.md](config.md).

Articles: 6, 7.

### Phase 4 — Job engine (copy, move, delete) — done 2026-09-28

Goal: file operations that never block and never stall on one bad file.

Produces: `JobQueueManager` with per-physical-disk queues (HDD: one at a time; NVMe/SSD: several in parallel, detected through the storage seek-penalty property); `CopyFileExW` backend with the progress callback and unbuffered mode for huge files; progress coalesced to 30 updates per second; per-file conflict state machine (pause that file, report, continue the batch); pause, resume, cancel; move as rename on the same volume and copy-plus-delete across volumes; delete to the Recycle Bin through the shell API. IoRing is measured against `CopyFileExW` before it is adopted.

Done when: copying 10,000 small files plus one 20 GB file to the same disk shows the UI-side progress stream at 30 Hz, and an injected "file exists" conflict does not stop the other files.

Measured 2026-09-28 on this PC (E:, NVMe, release build): 10,000 files of 1–64 KiB plus one 20 GiB file copied in 9.7 s (2.3 GB/s); the progress stream peaked at 22 events per second (limit 30; Windows's 15.6 ms timer tick is why it is not closer to 30). A second run into the same destination skipped 10,001 existing files through conflicts while 1,000 new files were copied, in 1.5 s. A benchmark of `CopyFileExW` against a plain read/write loop is in [jobs.md](jobs.md); IoRing is still unmeasured. Details: [jobs.md](jobs.md).

Articles: 1, 5.

### Phase 5 — WinUI 3 shell v1 — built 2026-09-28, scrolling check pending

Goal: the first window a user can live in. Design views A and B.

Produces: the C# solution; process launcher and pipe client; shared-memory reader in `unsafe` C#; `CommandRouter` with the chord state machine; dual-pane grid with `ItemsRepeater` virtualization and instant single-pane toggle; breadcrumb address bar; status bar; Command Palette with fuzzy search and inline rebinding (design view B); Mica backdrop; design tokens from the handout; the first-run experience.

Done when: the core and the UI run together, a listing of 100,000 files scrolls without frame drops, and every visible action goes through `CommandRouter` and a command ID.

Built 2026-09-28; the scrolling check waits for an unlocked screen. Built: `ui/CabinetOS.sln` on .NET 10 and the Windows App SDK 2.5.1, unpackaged, using the Windows App Runtime installed on the machine. `CabinetOS.Core` holds everything that runs without a window: the pipe client, the protocol types (tested against `sdk/protocol` and `sdk/config`), the core launcher, `ListingView` (the only `unsafe` C#; it reads listings straight from the core's shared memory), keys and the chord state machine, the `CommandRouter`, and diagnostics in the core's log format with crash traces at the `frontend` boundary. `CabinetOS.exe` draws design view A (Mica title bar; back, forward and up; breadcrumbs that turn into an address box; pinned folders in the sidebar; two panes of virtualized rows with an instant single-pane toggle; a status bar that shows a waiting chord) and design view B (the palette: the core ranks the commands, each row shows its keys and chords, the Immutable System Tier shows a lock, and the pencil or F2 records new keys, which the core writes to `cabinetos.json`). Dual pane on first start (conflict B). Every button, key, crumb, sidebar row and palette row goes through `CommandRouter` with a ULID, and a folder opened by a command is listed under the same ID in the core's log. 158 tests, one of them against the real core. Measured 2026-09-28 on this PC (release builds; the screen was locked, so Windows drew about 33 frames per second): both panes shown 0.82–0.87 s after start; a folder of 100,000 entries drawn 82 ms after the command, 38 ms of it in the core; closing ends the core in 0.4 s. Not measured yet: scrolling that folder with PageDown held, which needs an unlocked screen. The core's side of the rest is built (protocol version 9): the shell's commands are in the core's registry with their keys, so every one of them shows in the palette and can be rebound (`go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind`, `file.rename`, `file.delete`, `file.deletePermanently`, `file.openInOtherPane`, `file.properties`, `edit.cut`, `edit.copy`, `edit.paste`, `edit.selectAll`, `edit.toggleSelection`, `search.focus`; F2 renames in a pane and records keys in the palette), and the shell's type names and icons come from the core: `describe_entries` gives each shown row the shell's type name (by extension, no disk access) and an icon key (`folder`, `ext:.txt`, `generic`, or `path:` for programs, icons and shortcuts, whose icon is their own), and `get_icon` gives that icon as a PNG of 16, 24, 32 or 48 pixels from the system image lists. Measured 2026-09-28 on this PC (release build): 10 entries of `C:\Windows` described in 7.5 ms on the first request and 1.5–1.7 ms after; an icon in 7–26 ms the first time, then from the core's cache.

**Core additions for the UI (protocol version 8), 2026-09-28.** `list_volumes` answers every drive letter's volume as `volume_info` describes it, in letter order; the drives are asked in parallel, and one that is not ready, fails or does not answer (a network drive within 200 ms, a local one within 2 s) is left out; a `subst` letter shows its folder's volume. `volumes_changed` brings the new list when a drive letter comes or goes: a USB stick, a card, a mapped share, a `subst` letter. The core hears Windows' `WM_DEVICECHANGE` broadcasts through a top-level window that is never shown. `get_value` and `set_value` read and change one setting by its dotted path; a change is checked as a saved file is, written atomically, and announced with `config_changed`. `ui.lastPaths` and `ui.pinned` are new settings. `open_path` opens a file or folder with its default application (the shell's `open` verb); this is navigation infrastructure, and viewers and editors stay extensions (Article 10). `create_directory` and `rename` (in place, never replacing) need no job, and a taken name is the new error code `already_exists`. CLI: `volumes`, `config get` and `config set`, `open`, `mkdir`, `rename`. CI now runs only the jobs a push touches, each in its own concurrency group. Measured on this PC (release build): `list_volumes` 2–5 ms for 11 volumes, 6 of them network shares; `set_value` about 3 ms; `open_path` of a text file 281 ms until Notepad took it.

**File operations in the window (Phase 5b, design views D and E), built 2026-09-28.** F5 and F6 copy or move the selection to the other pane and F7 makes a folder: the core lists these commands but answers `not_implemented`, so the router runs them in the UI, which starts the job itself (`start_job`, under the key press's ULID). Delete goes to the Recycle Bin, Shift+Delete deletes for good after a dialog that names the count, F2 renames in place, Enter opens a file with its default application (`open_path`), Ctrl+Enter opens a folder in the other pane, Alt+Enter shows Properties from the listing's metadata, and Ctrl+X/C/V work through an in-app clipboard that also puts the paths on Windows' clipboard as text. Selection follows Windows list rules plus Total Commander's Insert; the status bar says "12 selected, 1.4 MB". The transfer flyout draws the core's numbers (title, source → destination, the 40-sample speed graph, the progress bar, Pause/Resume, Cancel/Close), folds into the status-bar pill, and cycles through several jobs; a conflict opens a card in the flyout with the decisions that fit its kind and "Apply to all of this kind". The context menu has the icon strip, the file commands, the plugins' `filesView` commands and Properties. The panes' last folders, dual/single, the sidebar and pinned folders are kept in `cabinetos.json` through `set_value`. 207 tests, three of them against the real core (200 files copied with one conflict answered Skip, which still ends `completed`; a folder made and renamed). Checked 2026-09-28 with the window's own snapshots while the screen was locked: F5 on the 100,000-entry fixture shows the flyout at 12 % after 2.5 s (items, not bytes: the fixture's files are empty); snapshots in [log/2026-09-28/](log/2026-09-28/). The real-key run waits for an unlocked screen. Guide: [ui.md](ui.md). Open for the creator: the Windows App SDK ships under the Microsoft Software License Terms, which include a data-collection clause for the runtime; WinUI 3 (ADR 0001) cannot be used without it, so a recorded exception is needed. Guide: [ui.md](ui.md).

Articles: 3, 4, 5, 7.

### Phase 6 — Indexer — core side done 2026-09-28

Goal: instant search over whole volumes.

Produces: MFT reader into a compact in-memory tree keyed by file reference number; USN Journal tailing to keep it fresh; the elevated `cabinetos-indexer.exe` as a Windows service with an on-demand elevated mode; read-only query pipe; core fallback when absent; search field in the UI.

Done when: the index of the system drive builds in seconds, a rename on disk appears in search results within a second, and the app works unchanged with the indexer stopped.

Core side done 2026-09-28; the search field waits for the UI (Phase 5). Built: `cabinetos-index`, an in-memory index keyed by file reference number, built by enumerating the MFT through `FSCTL_ENUM_USN_DATA` (the NTFS driver returns each entry's FRN, parent, name and attributes, so no on-disk structure is parsed) and kept current by one thread per volume that follows `FSCTL_READ_USN_JOURNAL`, rebuilding in the background when the journal cannot continue; names interned; ranked substring search on several threads. `cabinetos-indexer.exe` runs it elevated, in a terminal (`--console`) or as a Windows service (`--install`, manual start, LocalSystem), and answers only `ping`, `index_status` and `search`, over `\\.\pipe\cabinetos-indexer` with a medium integrity label so the normal-rights core can ask. The core answers `search` from the indexer within 200 ms, or walks one folder tree itself (at most 2 s and 20,000 entries) and says `source: walk`. Measured 2026-09-28 on the GitHub Actions runner's `C:` (release build): 1,358,009 entries in 3.3 s, 87 bytes per entry, a 3-letter query in 12.5 ms, a rename visible to search after 9 ms. On this PC with the indexer stopped, `search` answered from a walk and `index status` said `available: no`; the index of this PC's own `C:` is still to be measured in an elevated run. Open question for the creator: should search hits be filtered by the asking user's folder rights (today any logged-on user can search all names, as the "Everything" tool does)? Guide: [indexer.md](indexer.md).

Articles: 1.

### Phase 7 — Plugin host and Tool Dock — core side done 2026-09-28

Goal: the two extension layers, with strict boundaries.

Produces: `wasmtime` with the Component Model; the WIT package for the plugin world; plugin manifest with capability requests (`fs:read`, `fs:write`, `cmd:register`, `process:run`, `net`, `credentials`); the permissions review flow (design view C dialog); one store and one thread per plugin with fuel and memory limits; trap handling that logs the plugin ID, kills the instance and keeps the core alive; the Tool Dock in the UI with WebView2 hosting; one sample core plugin and one sample Tool Extension (Markdown preview, as in the design).

Done when: a deliberately crashing plugin is logged and removed while the core keeps serving listings, and the sample extension renders in a dock pane.

Core side done 2026-09-28; the permissions review dialog, the Tool Dock and the sample Tool Extension wait for the UI (Phase 5). Built: the WIT package `cabinetos:plugin@0.1.0` in `sdk/wit/`; strict `plugin.json` manifests; capabilities with levels (the list above plus `config:read`, `events:emit` and `jobs:intercept`; `process:run`, `net` and `credentials` are declared but never granted yet), granted in the `plugins` section of `cabinetos.json` and applied without a restart; one store and one thread per plugin, with fuel (5×10⁹ per call), a 5 s deadline (500 ms for `before-job`) and 256 MiB of memory; trap handling that logs the plugin ID with a named WebAssembly backtrace, drops the instance, removes its commands and keeps the core serving, with a restart after 5 s unless the plugin crashed three times in ten minutes; the Rust sample `hello` and five test fixtures, committed as built components. Measured 2026-09-28 on this PC (debug build): `crashy.crash` answered `plugin_error` at once, and the same core listed `C:\Windows` (108 entries, 1.4 ms) right after; `spinner.spin` was stopped at 5.1 s. Guide: [plugins.md](plugins.md).

Articles: 8, 10, 11.

### Phase 8 — Integrated terminal — core side done 2026-09-28

Goal: a shell scoped to the active pane.

Produces: ConPTY host in the core with a byte pipe per session; shell profiles (pwsh, cmd, WSL) in config; the terminal pane in the UI, hidden until `Ctrl+`` `; "cwd follows the active pane". The rendering control is an open question (see section 7).

Core side done 2026-09-28; the terminal pane waits for the UI (Phase 5). Built: the crate `cabinetos-terminal`: each shell runs in a ConPTY pseudo-console, from a profile in `cabinetos.json` read at each open, with `TERM=xterm-256color` and `CABINETOS_SESSION`; each session's raw bytes travel on a pipe of their own (`\\.\pipe\cabinetos-term-<random>`), with the control pipe's access rules and one client at a time; sessions belong to the core, so a client may leave and attach again; output waits in a 1 MiB buffer that holds the shell back when full; closing is a hang-up, and a shell still running 2 s later is ended. Protocol version 7: `terminal_open`, `terminal_resize`, `terminal_close`, `terminal_sync_cwd`, `terminal_list` and the event `terminal_exited`. "cwd follows the active pane" is `terminal_sync_cwd`: it types the shell's own change-directory command, quoted so pwsh, cmd and WSL read the path literally. `cabinetos-cli term` runs a shell in a console window (Ctrl+] detaches). Measured 2026-09-28 on the development PC (debug build): a shell starts in 17 to 24 ms and closes at its prompt in 3 to 15 ms; 2.8 MB printed while no client read arrived complete. Guide: [terminal.md](terminal.md).

Articles: 4, 9.

### Phase 9 — Marketplace and theme engine

Goal: install and share extensions and themes.

Produces: the JSON theme format (accent, Mica tint, palette) with live apply; a marketplace index served as static files first; install, update and remove flows; publisher trust.

Articles: 2, 8.

### Phase 10 — Packaging and release

Goal: a stranger can install it.

Produces: MSIX or unpackaged decision; `winget` manifest; code signing; the indexer service installer step; release notes; the repo goes public.

Articles: 2, 3.

## 6. Phase 1 in detail — the Rust core scaffold

This is the first coding task. The creator will give the exact instruction; this
section is the proposal to refine.

### Crates in the `core/` Cargo workspace

| Crate | Kind | Responsibility | Unsafe code allowed? |
|-------|------|----------------|----------------------|
| `cabinetos-core` | binary | Process startup, pipe server, session lifetime, wiring of all libraries | no |
| `cabinetos-protocol` | library | The IPC contract: message types (serde), request IDs, `#[repr(C)]` shared-memory layouts with size and offset tests, protocol version | no |
| `cabinetos-diag` | library | `tracing` setup: non-blocking rolling JSON Lines appender, `boundary` and `request_id` fields, panic hook that writes a crash trace and flushes, ring buffer of recent events | no |
| `cabinetos-ipc` | library | Named pipe server with a user-only DACL, length-prefixed framing, shared-memory section creation and handle duplication | yes, isolated |
| `cabinetos-fs` | library | Directory enumeration, hydration, volume and disk detection, change watching | yes, isolated |
| `cabinetos-jobs` | library | `JobQueueManager`, copy/move/delete backends, progress throttling, conflict states | yes, isolated |
| `cabinetos-index` | library | In-memory volume index, MFT reader, USN tailer (shared by indexer and core query side) | yes, isolated |
| `cabinetos-indexer` | binary | The elevated service around `cabinetos-index` | no |
| `cabinetos-config` | library | `cabinetos.json` load, schema, defaults, watch, diff | no |
| `cabinetos-commands` | library | Command registry, keybinding model, immutable tier | no |
| `cabinetos-plugins` | library | `wasmtime` host, WIT bindings, capability policy, trap handling | no |
| `cabinetos-cli` | binary (dev tool) | Command-line client for the pipe: `ping`, later `ls`, `copy`. Lets the core be tested with no UI | no |

In Phase 1 every crate exists with a `lib.rs` or `main.rs` whose module comment states its responsibility and the Constitution articles it serves. Only `protocol`, `diag`, `ipc` (pipe part), `core` and `cli` have real code. The rest hold their public type names and `todo!()`-free stubs, so the shape is reviewable.

### Workspace-wide settings

- `rust-toolchain.toml`: stable, pinned. Edition 2024.
- Lints in `Cargo.toml` `[workspace.lints]`: `unsafe_code = "forbid"` by default, relaxed to `"deny"`-with-allow in the four crates that need it; every `unsafe` block carries a `// SAFETY:` comment; `clippy::pedantic` as warnings with a short allow-list; `missing_docs` on public items.
- `cargo-deny` config: licenses allowed (MIT, Apache-2.0, BSD, ISC, Unicode, Zlib), advisories checked.
- Dependencies (first set): `tokio`, `serde`, `serde_json`, `schemars`, `tracing`, `tracing-subscriber` (json), `tracing-appender`, `windows` (windows-rs), `ulid`, `thiserror`, `anyhow` (binaries only), `crossbeam-channel`, `dashmap`, `criterion` (dev), `insta` (snapshot tests, dev).
- CI: GitHub Actions on `windows-latest`: fmt, clippy with `-D warnings`, test, deny, build in release. GitHub runners run as Administrator, so later MFT and USN tests can run there.

### Definition of done for Phase 1

1. `cargo build --workspace` and `cargo test --workspace` pass; clippy and fmt are clean; CI is green on `main`.
2. `cabinetos-core` starts, creates the pipe with a user-only DACL, and exits cleanly when its parent handle is signalled or on `Ctrl+C`.
3. `cabinetos-cli ping` sends `{ "id": "<ulid>", "type": "ping" }`, receives `pong` with the same ID, and that ID appears in `%LOCALAPPDATA%\CabinetOS\logs\core.<date>.jsonl` with `boundary: "engine"`.
4. A forced panic in the core produces `crash-<timestamp>.json` with a backtrace and the last events from the ring buffer, and the log file is flushed before exit.
5. `cabinetos-protocol` has a test that pins the size and field offsets of every shared-memory struct, so a change is a deliberate, visible act.
6. `docs/ARCHITECTURE.md` gets a "Crate map" section linking each brief section to its crate.

### Explicitly not in Phase 1

Directory listing, shared-memory data (only the section-creation helper), file operations, config watching, WASM, terminal, any C#.

## 7. Open questions for later phases (nothing here blocks Phase 0 or 1)

1. **Terminal rendering control (Phase 8).** WinUI 3 has no public terminal control. Options: xterm.js inside WebView2 (fast to build, proven), or a custom text renderer (native look, months of work). Recommendation: xterm.js first.
2. **First-run layout (Phase 5).** Settled 2026-09-28 by the creator: dual pane on first start; see conflict B.
3. **Config comments (Phase 3).** Settled 2026-09-28: strict JSON for version 1 ([config.md](config.md)); every JSON tool can read it and there is one parser. JSONC stays possible later by stripping comments before parsing.
4. **Indexer install (Phase 6).** Windows service installed once with one UAC prompt, or elevated on demand every session? Service is smoother; on-demand is simpler to ship first.
5. **IoRing vs CopyFileExW (Phase 4).** Measure before adopting. IoRing's API surface in `windows-rs` must be checked.
6. **Packaging (Phase 10).** MSIX gives clean install and updates but complicates the elevated service; unpackaged is simpler.
7. **ARM64 test hardware.** CI can build ARM64 but not run it. A test device or a cloud ARM VM is needed before claiming support.

## 8. Risks

| Risk | Effect | Mitigation |
|------|--------|------------|
| MFT parsing edge cases (attribute lists, hard links, reparse points, very large volumes) | Wrong or missing entries in search | NTFS only for indexing; fixture volume images in tests; ReFS and others use the fallback path |
| .NET SDK and WinUI workload are not installed on this PC | Phase 5 cannot start | Closed 2026-09-28: the .NET 10 SDK is installed; command-line builds get the Windows App SDK from NuGet ([dev-setup.md](dev-setup.md)) |
| Garbage-collector pauses in the C# UI | Frame drops | Dumb UI Rule: the UI holds no data; listings are read from native memory |
| Shared-memory layout drift between Rust and C# | Silent corruption | Size and offset tests on both sides against one constants file; protocol version in the header |
| WASM plugin runs forever or eats memory | Core stalls | wasmtime fuel and epoch interruption, per-store memory limits, one thread per plugin |
| The elevated indexer becomes an attack surface | Privilege escalation | Read-only queries only; no file operations; pipe DACL; no plugin code inside the indexer |
| Scope creep into the core | Article 10 violated | Every new feature request is checked against Article 10 first; default answer is "extension" |
