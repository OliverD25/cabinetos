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
| §7 "terminal panels" | Terminal in core or as extension | [ADR 0005](decisions/0005-terminal-in-core-hidden.md): core, hidden by default |

## Crate map

The Rust core is one Cargo workspace in `core/`, with every crate under
`core/crates/`. This table shows which crate implements which section of the
brief. Responsibilities, Constitution articles and build commands per crate:
[core/README.md](../core/README.md).

| Brief section | Crate | State |
|---|---|---|
| §2 Rust Core Engine | `cabinetos-fs`: directory enumeration, metadata hydration, volume and disk detection, change watching | stub until Phase 2 |
| §2 Rust Core Engine | `cabinetos-index`: in-memory volume index, MFT reader, USN Journal tailer | stub until Phase 6 |
| §2 Rust Core Engine | `cabinetos-indexer`: the elevated indexer process ([ADR 0002](decisions/0002-separate-elevated-indexer.md)) | stub until Phase 6 |
| §3 Copy/Move Queues | `cabinetos-jobs`: `JobQueueManager`, copy/move/delete backends, progress throttling, conflict states | stub until Phase 4 |
| §4 UI-Core Bridge (IPC) | `cabinetos-protocol`: message envelopes, request IDs, shared-memory layouts, JSON Schema export to `sdk/protocol/` | Phase 1 |
| §4 UI-Core Bridge (IPC) | `cabinetos-ipc`: named pipe with a user-only DACL, length-prefixed framing, shared-memory sections, process watch | Phase 1 |
| §5 WinUI 3 Frontend | no crate: the C# solution in `ui/` | Phase 5 |
| §6 Extension Architecture | `cabinetos-plugins`: Layer 1, the `wasmtime` host for Core Plugins | stub until Phase 7 |
| §7 Config, Keyboard & Command Palette | `cabinetos-config`: `cabinetos.json` load, schema, defaults, watch, diff | stub until Phase 3 |
| §7 Config, Keyboard & Command Palette | `cabinetos-commands`: command registry, keybindings, immutable tier | stub until Phase 3 |
| §8 Logging & Crash Diagnostics | `cabinetos-diag`: JSON Lines logs, ring buffer, crash traces ([diagnostics.md](diagnostics.md)) | Phase 1 |

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
