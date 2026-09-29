# CabinetOS: Advanced Agent Implementation Brief

> **Living document.** This is the creator's implementation brief of 2026-09-28,
> copied word for word, with the edits listed in the change log at the end. It
> says *how* CabinetOS is built. [CONSTITUTION.md](../CONSTITUTION.md) says *what*
> it is and wins on any conflict. [PLAN.md](PLAN.md) says in which order. Each
> architecture decision has a record in [decisions/](decisions/README.md).

**Project:** CabinetOS (The Programmable System Commander)
**Goal:** Build an approachable, zero-bloat file workspace marrying Total Commander's speed with VS Code's extensibility.

## 1. The Prime Directives (Agent Operating Rules)

As the AI developer for this project, you must strictly adhere to the following constitutional rules. **Any architectural decision that compromises speed for convenience is instantly rejected.**

* **The Dumb UI Rule:** You are FORBIDDEN from writing file I/O, heavy sorting, or data processing logic in the WinUI 3 (C#/C++) client. The UI exists *only* to draw pixels and capture keystrokes.
* **The Non-Blocking Core Rule:** You are FORBIDDEN from blocking the main UI thread. All communication to the Rust core must be asynchronous.
* **The Zero-Bloat Foundation:** Never hardcode "features" (like a built-in markdown viewer or FTP client) into the core codebase. Build the *API hooks* for these features, then build the features as separate, opt-in extensions.
* **Progressive Disclosure:** Design UIs that are clean by default. Power-user features (Regex renaming, chord keybindings, terminal panels) should be hidden until invoked via the Command Palette or config file.

---

## 2. Deep-Dive: Rust Core Engine (The Headless Server)

The core engine must be written in Rust, leveraging `windows-rs` for bare-metal NT API access.

* **Volume Indexing (NTFS MFT):** Do not use `std::fs::read_dir`. Architect a background worker requiring Admin privileges to read `C:\$MFT` directly into a custom, memory-efficient Radix tree or Hash Map.
* **State Synchronization:** Implement an event loop utilizing `FSCTL_READ_USN_JOURNAL`. The core must constantly poll the NTFS USN Journal to update the in-memory tree without rescanning the disk.
* **Asynchronous Hydration:** When the UI requests a directory with 100,000 files, the Rust core must instantly return a lightweight array of structs `(ID, String: Name, Enum: Type)`. A background Rust thread will fetch heavier metadata (File Size, Modified Date, Icons) and stream it to the UI in chunks.
* **Memory Management:** Use lock-free data structures (like `dashmap` or `crossbeam` channels) to prevent the search indexer from blocking the directory reader.

---

## 3. Deep-Dive: High-Performance Copy/Move Queues

File operations must rival or exceed native Windows Explorer and TeraCopy in reliability and speed.

* **Drive-Aware Queuing:** The Rust core must implement a smart `JobQueueManager`. If the user initiates multiple large file copies to the *same* physical hard drive, the manager must automatically sequence them to prevent I/O thrashing. If they are to *different* NVMe drives, it should parallelize them.
* **Bare-Metal APIs:** Utilize `CopyFileExW` (via `windows-rs`) or Windows 11 `IoRing` for asynchronous, high-throughput transfers.
* **Throttled IPC Progress:** When copying a 50GB file, do NOT send an IPC message to the UI for every megabyte copied. The Rust core must throttle progress updates (Bytes Copied, Current Speed, ETA) to a maximum of 30 or 60 frames per second over the Named Pipe to prevent UI stuttering.
* **Non-Blocking Conflict Resolution:** If a copy operation hits an error (e.g., "File Already Exists" or "Access Denied"), the Rust thread MUST NOT block the entire queue. It must pause that specific file, push a "Conflict Resolution Required" state to the UI, and immediately continue copying the rest of the valid files in the batch.

---

## 4. Deep-Dive: The UI-Core Bridge (IPC)

The communication layer must support maximum throughput with zero deserialization overhead for large payloads.

* **Control Channel (Named Pipes):** Use standard Named Pipes for the Command Palette and UI interactions. Payloads should be lightweight JSON or Protobuf (e.g., `{"command": "delete", "target": ["file_id_123"]}`).
* **Data Channel (Shared Memory):** For massive directory listings, allocate a Shared Memory block (Memory-Mapped File). Rust writes an array of C-compatible structs into this memory. The WinUI 3 client reads this memory via pointer arithmetic/unsafe C# to achieve zero-copy rendering.

---

## 5. Deep-Dive: WinUI 3 Frontend (The Visual Layer)

The frontend must be completely decoupled from the filesystem.

* **UI Virtualization:** You MUST use WinUI 3's `ItemsRepeater` or highly optimized `ListView` controls with UI virtualization enabled.
* **The Dual-Pane Grid:** The root layout is a responsive Grid. It defaults to a Dual-Pane layout (Left/Right) but must support an instant toggle to Single-Pane.
* **Command Routing:** Every clickable button, context menu item, and keystroke must map to a central `CommandRouter` in the UI, which translates the action into a string payload and fires it down the Named Pipe to Rust.

---

## 6. Deep-Dive: Bifurcated Extension Architecture

CabinetOS features two distinct extension layers. You must architect strict boundaries between them.

**Layer 1: Core Plugins (Headless WASM)**

* **Implementation:** Embed `wasmtime` into the Rust core.
* **Capabilities:** Define a strict WebAssembly Component Model (WIT) interface. Plugins can request capabilities like `fs:read` or `cmd:register`.
* **Execution:** Plugins run in isolated background threads. They can intercept operations but have absolutely no UI access.

**Layer 2: Tool Extensions (Visual Panes)**

* **Implementation:** Architect a "Tool Dock" area in the WinUI 3 XAML (bottom and side panels).
* **Hosting:** Tool extensions are either compiled WinUI UserControls or WebView2 instances.
* **Communication:** They receive standard context updates from the core (e.g., "The user highlighted file X"). If they crash, the main WinUI window must remain perfectly responsive.

---

## 7. Deep-Dive: Config, Keyboard & Command Palette

The application is entirely keyboard-driven.

* **The Command Palette:** Implement a global overlay invoked via `Ctrl+Shift+P`. It filters available commands using fuzzy matching against the active context.
* **Chord Keybindings:** Implement a keyboard state machine. If `Ctrl+K` is pressed, wait. If `Ctrl+C` is pressed within 1000ms, execute the command.
* **Config Sync:** The configuration is a single `cabinetos.json` file. The Rust core watches this file. If edited externally, Rust parses it and pushes state updates to the UI in real-time.

---

## 8. Deep-Dive: Zero-Latency Logging & Crash Diagnostics

Given the IPC and WASM boundaries, structured, non-blocking diagnostics are mandatory.

* **The Rust Core (Tracing):** Use the `tracing` ecosystem. Configure a non-blocking, rolling file appender on a background thread. Do not use synchronous print macros.
* **Unified Structured Format:** All logs must be written in JSON Lines (`.jsonl`). Trace a single request ID across the WinUI frontend, the Named Pipe, and into the Rust core.
* **Plugin Trap Handling:** When a WASM plugin panics, the `wasmtime` runtime throws a Trap. Rust MUST catch this trap, log the exact Plugin ID, terminate that specific WASM instance, and keep the main Rust core alive.
* **Crash Dumps:** Implement `std::panic::set_hook` in Rust and `UnhandledException` in WinUI. Upon a fatal crash, these must force-flush the asynchronous log buffer to the disk before termination.

---

## Decisions that refine this brief

The brief leaves some choices open or states them in a way the Constitution
does not. These records settle them. The brief text above is not rewritten to
match; the records are the source of truth for each point.

| Brief section | Open point | Settled by |
|---|---|---|
| §1, §5 "C#/C++" | Frontend language | [ADR 0001](decisions/0001-frontend-language-csharp.md): C# on .NET |
| §2 "requiring Admin privileges" | Where elevation lives | [ADR 0002](decisions/0002-separate-elevated-indexer.md): a separate indexer process; UI and core run unelevated |
| §4 "JSON or Protobuf" | Control-channel format | [ADR 0006](decisions/0006-control-channel-json.md): JSON |
| §3 "IoRing" | Minimum Windows version | [ADR 0004](decisions/0004-minimum-windows-11-22h2.md): Windows 11 22H2 or newer |
| §6 Layer 2 "UserControls or WebView2" | Crash isolation of Tool Extensions | [PLAN.md](PLAN.md) section 2: WebView2 for third-party tools; native controls only for first-party ones |
| §1 "terminal panels" | Terminal in core or as extension | [ADR 0005](decisions/0005-terminal-in-core-hidden.md): core, hidden by default |
| §8 "non-blocking diagnostics" | Whether heavy logging mode may wait for the log writer | [ADR 0013](decisions/0013-heavy-logging-may-wait.md): in heavy mode only, the core's operations may; the UI thread and the pipe never do |

## Crate map

The Rust core is one Cargo workspace in `core/`, with every crate under
`core/crates/`. This table shows which crate implements which section of the
brief. Responsibilities, Constitution articles and build commands per crate:
[core/README.md](../core/README.md).

| Brief section | Crate | State |
|---|---|---|
| §2 Rust Core Engine | `cabinetos-fs`: directory enumeration (NT API) with metadata in the same pass, sorting, listing sections, volume and disk detection, change watching; icon and type-name hydration comes in Phase 5 | Phase 2 |
| §2 Rust Core Engine | `cabinetos-index`: the in-memory index of NTFS volumes, built with `FSCTL_ENUM_USN_DATA` and kept current from the USN Journal, and ranked name search ([indexer.md](indexer.md)) | Phase 6 |
| §2 Rust Core Engine | `cabinetos-indexer`: the elevated indexer process ([ADR 0002](decisions/0002-separate-elevated-indexer.md)): console and service modes, a read-only pipe with a medium integrity label | Phase 6 |
| §3 Copy/Move Queues | `cabinetos-jobs`: `JobQueueManager` with per-disk slots, `CopyFileExW`, `MoveFileExW` and Recycle Bin backends, per-file conflicts, progress throttled to 30 events per second ([jobs.md](jobs.md)) | Phase 4 |
| §4 UI-Core Bridge (IPC) | `cabinetos-protocol`: message envelopes, request IDs, events, shared-memory layouts, JSON Schema export to `sdk/protocol/` ([ipc.md](ipc.md)) | Phase 1; listings and events in Phase 2; configuration, command and keymap messages in Phase 3; jobs in Phase 4; search and the indexer's messages in Phase 6; plugins in Phase 7; terminal sessions in Phase 8 |
| §4 UI-Core Bridge (IPC) | `cabinetos-ipc`: named pipe with a user-only DACL, length-prefixed framing, a client with events, shared-memory sections, process watch | Phase 1; events in Phase 2; the byte pipes of terminal sessions in Phase 8 |
| §5 WinUI 3 Frontend | no crate: the C# solution in `ui/` | Phase 5 |
| §6 Extension Architecture | `cabinetos-secrets`: named secrets in the Windows Credential Manager (`CabinetOS/<name>`), which the core adds to a plugin's web request so that no plugin ever holds a key ([ipc.md](ipc.md), "Secrets") | Phase 14a |
| §6 Extension Architecture | `cabinetos-plugins`: Layer 1, the `wasmtime` host for Core Plugins: the WIT interface in `sdk/wit/`, manifests and capabilities, the WASI sandbox, per-call limits, trap containment ([plugins.md](plugins.md)) | Phase 7 (the core side; Layer 2, the Tool Dock, comes with the UI in Phase 5) |
| §7 Config, Keyboard & Command Palette | `cabinetos-config`: `cabinetos.json` parsing with line and column errors, defaults, JSON Schema export to `sdk/config/`, directory watch, diff, atomic rewrite ([config.md](config.md)) | Phase 3 |
| §7 Config, Keyboard & Command Palette | `cabinetos-commands`: command registry, key grammar and chords, keymap compilation with the Immutable System Tier, palette search ([keybindings.md](keybindings.md)) | Phase 3 |
| §8 Logging & Crash Diagnostics | `cabinetos-diag`: JSON Lines logs, ring buffer, crash traces ([diagnostics.md](diagnostics.md)) | Phase 1 |
| §1 "terminal panels", with [ADR 0005](decisions/0005-terminal-in-core-hidden.md) | `cabinetos-terminal`: shells in pseudo-consoles (ConPTY), a raw byte pipe per session, profiles from `cabinetos.json`, the folder sync ([terminal.md](terminal.md)) | Phase 8 (the core side; the pane comes with the UI in Phase 5) |

Two crates serve every section rather than one: `cabinetos-core` builds
`cabinetos-core.exe`, which starts the process and wires the libraries
together, and `cabinetos-cli` is the command-line client that tests the core
without a UI.

## Change log

| Date | Change | Why |
|---|---|---|
| 2026-09-28 | Copied from the creator's brief. | First version in the repo (ADR 0008). |
| 2026-09-28 | §8, first sentence: "telemetry" → "diagnostics". | Article 12 of the Constitution was renamed by the creator on 2026-09-28 to avoid the word "telemetry", which suggests data sent to the developer. The brief describes only local logs and crash traces. Recorded in PLAN.md, consistency check row A. |
| 2026-09-28 | Added the "Crate map" section. | Phase 1 created the Rust workspace. The map links each brief section to the crate that implements it (PLAN.md §6, definition of done item 6). |
| 2026-09-28 | Crate map: `cabinetos-fs` is real (Phase 2); the §4 rows note the Phase 2 listings and events and link [ipc.md](ipc.md). | Phase 2 built the filesystem engine and the listing protocol. Size, dates and attributes arrive with the listing itself, because the NT API returns them in the same pass, so the §2 "asynchronous hydration" stream is kept for icons and shell type names (Phase 5). |
| 2026-09-28 | Crate map: `cabinetos-config` and `cabinetos-commands` are real (Phase 3) and link [config.md](config.md) and [keybindings.md](keybindings.md); the §4 protocol row notes the Phase 3 messages. | Phase 3 built the configuration file handling, the command registry and the keymap. §7 says the core "pushes state updates to the UI"; it does so as `config_changed`, `keymap_changed` and `config_error` events. The palette's fuzzy matching (§7) also runs in the core, because the UI does no data processing (§1). |
| 2026-09-28 | Crate map: `cabinetos-jobs` is real (Phase 4) and links [jobs.md](jobs.md); the §4 protocol row notes the job messages. | Phase 4 built the job engine. §3's `CopyFileExW` or IoRing: `CopyFileExW` is used; IoRing is to be measured before it is adopted (PLAN.md). §3's "30 or 60 frames per second": 30, with at least 34 ms between two progress events of one job. |
| 2026-09-28 | Crate map: `cabinetos-plugins` is real (Phase 7) and links [plugins.md](plugins.md); the §4 protocol row notes the plugin messages. | Phase 7 built the Core Plugin host. §6 "capabilities like `fs:read` or `cmd:register`": the full list with levels is in plugins.md; `process:run`, `net` and `credentials` are declared but never granted in this version. §6 "They can intercept operations": the capability `jobs:intercept` lets a plugin stop a job before it writes anything. §8 "Plugin Trap Handling": a trap is logged with the plugin ID and the WebAssembly backtrace, the instance is dropped, the core goes on; the plugin starts again after 5 s, unless it crashed three times in ten minutes. |
| 2026-09-28 | Crate map: `cabinetos-index` and `cabinetos-indexer` are real (Phase 6) and link [indexer.md](indexer.md); the §4 protocol row notes search and the indexer's messages. | Phase 6 built the indexer. §2 "read `C:\$MFT` directly": version 1 reads every MFT entry through `FSCTL_ENUM_USN_DATA`, which returns FRN, parent, name and attributes from the NTFS driver, so no on-disk structure is parsed (1.36 million entries in 3.5 s on the CI runner); raw `$MFT` parsing stays a later optimization. §2 "Radix tree or Hash Map": a hash map keyed by FRN with interned names (87 bytes per entry); a radix tree or trigram index waits until the linear scan (11.5 ms for 1.36 million entries) proves too slow. §2 "constantly poll the USN Journal": a thread per volume waits on `FSCTL_READ_USN_JOURNAL` and applies each change within about 10 ms. §2 "lock-free data structures": searches and updates share the index through a reader-writer lock, and updates hold it well under a millisecond per batch. |
| 2026-09-28 | Crate map: `cabinetos-terminal` is real (Phase 8) and links [terminal.md](terminal.md); the §4 rows note the terminal messages and the byte pipes. | Phase 8 built the terminal host. The brief names terminals only in §1 ("terminal panels", hidden until invoked); ADR 0005 put the host in the core. §4 names two channels; terminal output travels on a third, one raw byte pipe per session: the output is frequent and binary, so JSON frames would only add cost, and a shared-memory section does not fit a stream. The byte pipe keeps the control pipe's access rules. |
| 2026-09-28 | The WinUI 3 shell exists (Phase 5): §5 is realized in `ui/` ([ui.md](ui.md)). | Phase 5 built design views A and B. §5 "`ItemsRepeater` … with UI virtualization": an `ItemsRepeater` with a virtualizing `StackLayout` over a list that makes a row object only for the rows on screen. §5 "instant toggle to Single-Pane": `view.toggleDualPane` (Ctrl+Shift+D); dual pane on first start (PLAN.md, check B). §5 "every clickable button … and keystroke must map to a central `CommandRouter`": every button, key, crumb, sidebar row and palette row goes through `CommandRouter` by command ID; five navigation commands (`go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind`) exist only in the UI until the core registers them. §4 "reads this memory via pointer arithmetic/unsafe C#": `ListingView`, the UI's only hand-written unsafe code. §7: the palette's fuzzy matching runs in the core (`search_commands`); the chord state machine runs in the UI with the core's keymap. §8 "`UnhandledException` in WinUI": crash traces from `Application.UnhandledException`, `AppDomain.UnhandledException` and a catch around WinUI's own start, which ends the process without raising either. |
| 2026-09-28 | The UI halves of Phases 6, 7 and 8 exist (Phase 5c): the terminal pane, file search, the plugin list and review, and Tool Extensions ([ui.md](ui.md), [tool-extensions.md](tool-extensions.md)). | §6 Layer 2 "compiled WinUI UserControls or WebView2 instances": WebView2 only, each tool in a browser process of its own, so a crash leaves the window responsive (checked by ending the process). The terminal, first-party, is xterm.js in WebView2 too, not the native control PLAN.md section 2 expected for first-party tools: WinUI has no terminal control, and the isolation is the same. §6 "context updates from the core": the window sends them (`context`), because the selection lives in the UI. §6 "Tool Dock … bottom and side panels": the dock holds the terminal under or beside the panes (`ui.layout`); tools open as a pane's editor tab (design view D). §1 Dumb UI Rule: one bounded exception, the UI reads each tool's `tool.json` once at start on a background thread, until the core can list tools; the file a tool shows is read by the tool's own page through a read-only virtual host. |
| 2026-09-30 | Crate map: `cabinetos-secrets` (Phase 14a). | Phase 14 lets a plugin call a web service with the user's key without holding it: the key lives in the Windows Credential Manager, and the core adds it to the plugin's request (Article 8). A crate of its own keeps the Credential Manager's unsafe calls out of the others, as for every crate that talks to Windows. |
| 2026-09-30 | §6 capabilities: `net` is granted from WIT 0.2.0 ([plugins.md](plugins.md), "The network"). | The 2026-09-28 row said `net` was never granted. A plugin still has no network in its sandbox: it asks the core with `http-request`, the core reaches only the hosts `plugin.json` names, over `https:` (plain `http:` only to `localhost` and `127.0.0.1`), and adds a stored secret to a header without the plugin seeing it (Article 8). `process:run` and `credentials` stay never granted. |
| 2026-09-30 | Decisions table: §8 "non-blocking diagnostics" is refined by ADR 0013 (heavy logging mode, Phase 15). | The creator chose a switchable heavy mode that logs every operation, at every level, with full payloads, even at the cost of speed. When its writer is far behind, the core's operations wait for it once 256 MiB are queued, so that no operation goes unrecorded; the UI thread and the pipe never wait. Normal mode still drops lines and counts them. §8's wording is not rewritten; the record is the source of truth for this point ([diagnostics.md](diagnostics.md), "Heavy mode"). |
| 2026-09-30 | §3: the job engine keeps an undo journal ([jobs.md](jobs.md), "Undo"). | Phase 14 lets the user (and later an agent) reverse a finished job. `cabinetos-jobs` writes one line per job to `%LOCALAPPDATA%\CabinetOS\undo\journal.jsonl` and saves a file before replacing it (256 MiB in all); `undo_job` turns a line back into a steps job. The line is written on the job's own thread, before the job's end is announced, so no request waits on the disk. |
