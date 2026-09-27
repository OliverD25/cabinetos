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
- **Log location.** `%LOCALAPPDATA%\CabinetOS\logs\` with one JSON Lines file per process: `core.jsonl`, `ui.jsonl`, `indexer.jsonl`.
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
| B | Design §1 vs §3A | §1: first run is a single pane. §3A: dual pane by default. Article 5 makes dual-pane the primary paradigm; Article 4 wants a casual user at ease | Recommend dual by default with single one toggle away; confirm before the UI phase | Phase 5 |
| C | Brief §6 Layer 2 | "WinUI UserControls or WebView2" and "if they crash, the window must remain responsive". An in-process control cannot guarantee that | WebView2 for third-party Tool Extensions; native controls only first-party (section 2 default) | Phase 7 |
| D | Design sidebar "Tags" | File tagging is a feature, not navigation. Article 10 says features are opt-in | Tags become a first-party plugin (core plugin stores tags; UI hook draws the dot). Not in the core | Phase 7 |
| E | Design keybinding hint | Sidebar shows "Ctrl+K W"; the command list says "Ctrl+K then Ctrl+W" | Pick one when seeding the command registry | Phase 3 |
| F | Brief §2 "requiring Admin privileges" | Resolved by decision 2 (separate elevated indexer) | Done | — |
| G | Design: marketplace as a core view | Article 10 says zero supplementary tools, but Article 8 makes the marketplace the way to add everything else | Marketplace stays core: it is infrastructure, like the terminal (decision 5) | — |

## 5. Phases

Each phase lists its goal, what it produces, when it is done, and which
Constitution articles it serves. Phases 1–4 are core-only and testable from
the command line, before any UI exists. That is deliberate: the Dumb UI Rule
means the core must work on its own.

### Phase 0 — Repository foundation (docs and hygiene, no code)

Goal: the repo holds every governing document and is ready for code.

Produces: `LICENSE` (MIT); `docs/ARCHITECTURE.md` (the brief, word for word); `docs/design/` (the design bundle, unzipped); `docs/decisions/0001`–`0008` (today's decisions); `.gitattributes` (fixed line endings per file type, so every machine gets the same bytes); `.editorconfig`; `.gitignore` for Rust and .NET; the monorepo folders `core/`, `ui/`, `sdk/`, `docs/`; an updated `README.md`; `CLAUDE.md` gains a section on layout, build and test commands, and the rule that the brief is living.

Done when: everything above is committed and pushed, and a fresh clone shows the same line endings on Windows and Linux.

Articles: 2 (license), 6 and 12 indirectly (documents exist to be followed).

### Phase 1 — Rust core scaffold (the first coding task)

Goal: an empty but real core. Every crate exists with its responsibility written down, the diagnostics pipeline works, and a command-line client can talk to the core over the pipe. Detailed in section 6.

Done when: `cargo build`, `cargo test`, `cargo clippy -- -D warnings` and `cargo fmt --check` pass locally and in CI; `cabinetos-cli ping` sends a request with an ID and the same ID appears in `core.jsonl`.

Articles: 1 (non-blocking design from day one), 12 (diagnostics first).

### Phase 2 — Filesystem engine v1

Goal: list any directory fast, stream metadata afterwards, and notice changes.

Produces: NT-API directory enumeration; the shared-memory listing format (ID, name, kind) with a versioned header; asynchronous hydration (size, dates, attributes, icon key) in chunks; volume and physical-disk detection for a path; `ReadDirectoryChangesW` watching of open directories with `DirectoryChanged` events; CLI `ls` that renders from shared memory; benchmarks with a 100,000-file fixture directory.

Done when: a 100,000-entry listing appears in shared memory in under 50 ms on this PC (first-cut target, to be measured and revised), and the benchmark runs in CI.

Articles: 1, 5 (two independent pane sessions).

### Phase 3 — Config, commands and keybindings

Goal: the command architecture exists before any button does.

Produces: `cabinetos.json` schema and defaults; file watcher with parse, validate, diff, and change events (Article 6 real-time sync); command registry seeded from the design's command list (id, category, name, default binding, source); chord semantics (1000 ms window) written as a spec shared by core and UI; the Immutable System Tier list (Article 7); keymap export to the UI.

Done when: editing the file by hand changes a binding in the running core within one second, and a test proves an immutable binding cannot be overridden.

Articles: 6, 7.

### Phase 4 — Job engine (copy, move, delete)

Goal: file operations that never block and never stall on one bad file.

Produces: `JobQueueManager` with per-physical-disk queues (HDD: one at a time; NVMe/SSD: several in parallel, detected through the storage seek-penalty property); `CopyFileExW` backend with the progress callback and unbuffered mode for huge files; progress coalesced to 30 updates per second; per-file conflict state machine (pause that file, report, continue the batch); pause, resume, cancel; move as rename on the same volume and copy-plus-delete across volumes; delete to the Recycle Bin through the shell API. IoRing is measured against `CopyFileExW` before it is adopted.

Done when: copying 10,000 small files plus one 20 GB file to the same disk shows the UI-side progress stream at 30 Hz, and an injected "file exists" conflict does not stop the other files.

Articles: 1, 5.

### Phase 5 — WinUI 3 shell v1

Goal: the first window a user can live in. Design views A and B.

Produces: the C# solution; process launcher and pipe client; shared-memory reader in `unsafe` C#; `CommandRouter` with the chord state machine; dual-pane grid with `ItemsRepeater` virtualization and instant single-pane toggle; breadcrumb address bar; status bar; Command Palette with fuzzy search and inline rebinding (design view B); Mica backdrop; design tokens from the handout; the first-run experience.

Done when: the core and the UI run together, a listing of 100,000 files scrolls without frame drops, and every visible action goes through `CommandRouter` and a command ID.

Articles: 3, 4, 5, 7.

### Phase 6 — Indexer

Goal: instant search over whole volumes.

Produces: MFT reader into a compact in-memory tree keyed by file reference number; USN Journal tailing to keep it fresh; the elevated `cabinetos-indexer.exe` as a Windows service with an on-demand elevated mode; read-only query pipe; core fallback when absent; search field in the UI.

Done when: the index of the system drive builds in seconds, a rename on disk appears in search results within a second, and the app works unchanged with the indexer stopped.

Articles: 1.

### Phase 7 — Plugin host and Tool Dock

Goal: the two extension layers, with strict boundaries.

Produces: `wasmtime` with the Component Model; the WIT package for the plugin world; plugin manifest with capability requests (`fs:read`, `fs:write`, `cmd:register`, `process:run`, `net`, `credentials`); the permissions review flow (design view C dialog); one store and one thread per plugin with fuel and memory limits; trap handling that logs the plugin ID, kills the instance and keeps the core alive; the Tool Dock in the UI with WebView2 hosting; one sample core plugin and one sample Tool Extension (Markdown preview, as in the design).

Done when: a deliberately crashing plugin is logged and removed while the core keeps serving listings, and the sample extension renders in a dock pane.

Articles: 8, 10, 11.

### Phase 8 — Integrated terminal

Goal: a shell scoped to the active pane.

Produces: ConPTY host in the core with a byte pipe per session; shell profiles (pwsh, cmd, WSL) in config; the terminal pane in the UI, hidden until `Ctrl+`` `; "cwd follows the active pane". The rendering control is an open question (see section 7).

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
3. `cabinetos-cli ping` sends `{ "id": "<ulid>", "type": "ping" }`, receives `pong` with the same ID, and that ID appears in `%LOCALAPPDATA%\CabinetOS\logs\core.jsonl` with `boundary: "engine"`.
4. A forced panic in the core produces `crash-<timestamp>.json` with a backtrace and the last events from the ring buffer, and the log file is flushed before exit.
5. `cabinetos-protocol` has a test that pins the size and field offsets of every shared-memory struct, so a change is a deliberate, visible act.
6. `docs/ARCHITECTURE.md` gets a "Crate map" section linking each brief section to its crate.

### Explicitly not in Phase 1

Directory listing, shared-memory data (only the section-creation helper), file operations, config watching, WASM, terminal, any C#.

## 7. Open questions for later phases (nothing here blocks Phase 0 or 1)

1. **Terminal rendering control (Phase 8).** WinUI 3 has no public terminal control. Options: xterm.js inside WebView2 (fast to build, proven), or a custom text renderer (native look, months of work). Recommendation: xterm.js first.
2. **First-run layout (Phase 5).** Dual or single pane on first start; see conflict B.
3. **Config comments (Phase 3).** Strict JSON, or JSON with comments (JSONC, as VS Code uses)? Strict JSON keeps tooling simple; comments help hand editing.
4. **Indexer install (Phase 6).** Windows service installed once with one UAC prompt, or elevated on demand every session? Service is smoother; on-demand is simpler to ship first.
5. **IoRing vs CopyFileExW (Phase 4).** Measure before adopting. IoRing's API surface in `windows-rs` must be checked.
6. **Packaging (Phase 10).** MSIX gives clean install and updates but complicates the elevated service; unpackaged is simpler.
7. **ARM64 test hardware.** CI can build ARM64 but not run it. A test device or a cloud ARM VM is needed before claiming support.

## 8. Risks

| Risk | Effect | Mitigation |
|------|--------|------------|
| MFT parsing edge cases (attribute lists, hard links, reparse points, very large volumes) | Wrong or missing entries in search | NTFS only for indexing; fixture volume images in tests; ReFS and others use the fallback path |
| .NET SDK and WinUI workload are not installed on this PC | Phase 5 cannot start | Install .NET 8 SDK and the Windows App SDK before Phase 5; note it in `docs/dev-setup.md` in Phase 0 |
| Garbage-collector pauses in the C# UI | Frame drops | Dumb UI Rule: the UI holds no data; listings are read from native memory |
| Shared-memory layout drift between Rust and C# | Silent corruption | Size and offset tests on both sides against one constants file; protocol version in the header |
| WASM plugin runs forever or eats memory | Core stalls | wasmtime fuel and epoch interruption, per-store memory limits, one thread per plugin |
| The elevated indexer becomes an attack surface | Privilege escalation | Read-only queries only; no file operations; pipe DACL; no plugin code inside the indexer |
| Scope creep into the core | Article 10 violated | Every new feature request is checked against Article 10 first; default answer is "extension" |
