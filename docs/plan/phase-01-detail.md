# Phase 01 — Rust core scaffold, in detail

Section 6 of [PLAN.md](../PLAN.md), "Phase 1 in detail", moved whole on 2026-09-30; the plan keeps Phase 1's goal and its done-when list.

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
