# CabinetOS core (Rust workspace)

The headless engine behind the CabinetOS window. The UI only draws pixels and
captures keystrokes (brief §1, the Dumb UI Rule); everything else happens
here, behind a named pipe. Which crate implements which part of the brief:
[../docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md), section "Crate map".

## Crates

| Crate | Kind | Responsibility | Constitution articles |
|---|---|---|---|
| `cabinetos-core` | binary + library | `cabinetos-core.exe`: startup, the pipe server, session lifetime, wiring of all libraries | 1, 10, 12 |
| `cabinetos-protocol` | library | The IPC contract: message envelopes, request IDs, `#[repr(C)]` shared-memory layouts, JSON Schema export | 1, 12 |
| `cabinetos-diag` | library | JSON Lines logs, ring buffer of recent events, crash traces ([../docs/diagnostics.md](../docs/diagnostics.md)) | 12, 1 |
| `cabinetos-ipc` | library | Named pipe with a user-only DACL, length-prefixed framing, shared-memory sections, process watch | 1, 12 |
| `cabinetos-cli` | binary | `cabinetos-cli.exe`: command-line client for the pipe, to test the core with no UI | 4, 12 |
| `cabinetos-fs` | library (stub) | Directory enumeration, metadata hydration, volume and disk detection, change watching (Phase 2) | 1, 5 |
| `cabinetos-jobs` | library (stub) | `JobQueueManager`: drive-aware copy, move and delete queues (Phase 4) | 1, 5 |
| `cabinetos-index` | library (stub) | In-memory volume index, MFT reader, USN Journal tailer (Phase 6) | 1 |
| `cabinetos-indexer` | binary (stub) | `cabinetos-indexer.exe`: the elevated indexer process (Phase 6, ADR 0002) | 1 |
| `cabinetos-config` | library (stub) | `cabinetos.json` load, schema, defaults, watch, diff (Phase 3) | 6 |
| `cabinetos-commands` | library (stub) | Command registry, keybindings, immutable system tier (Phase 3) | 7, 4 |
| `cabinetos-plugins` | library (stub) | `wasmtime` host for Core Plugins, capability policy, trap handling (Phase 7) | 8, 10, 11 |

A stub holds only its crate documentation and the names of its future public
types, so the shape of the engine can be reviewed before the code exists.

**Unsafe code** is denied in every crate. Crates that will never need it
forbid it outright. Only the crates that call Windows APIs directly (`ipc` now;
`fs`, `jobs` and `index` later) allow it, and only in the module that needs
it; every `unsafe` block carries a `// SAFETY:` comment, which clippy enforces.

## Build and test

Toolchains: [../docs/dev-setup.md](../docs/dev-setup.md). The toolchain version
is pinned in `rust-toolchain.toml`.

From `core/` in a Windows terminal (PowerShell or cmd), the same five checks
that CI runs:

```text
cargo build --workspace
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --all -- --check
cargo deny check
```

The code talks to Windows APIs, so it builds only for Windows. From a WSL
terminal, call the Windows toolchain by its `.exe` name, for example
`cargo.exe test --workspace` (a Linux `cargo` cannot build it).

The tests start real `cabinetos-core.exe` processes on random pipe names with
temporary log directories; they never write to the real log directory.

**Protocol schema.** `sdk/protocol/*.schema.json` is generated from the Rust
types, and a test fails when the files are out of date. After changing a
message, regenerate and commit them (from `core/`, in bash):

```bash
CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol
```

## Run the core and ping it

Terminal 1, from `core/`: start the core on the pipe `demo`
(`\\.\pipe\cabinetos-core-demo`). Without `--pipe` it uses `dev`.

```text
cargo run -p cabinetos-core -- --pipe demo
```

Terminal 2, from `core/`:

```text
cargo run -p cabinetos-cli -- --pipe demo ping --count 3
cargo run -p cabinetos-cli -- --pipe demo shutdown
```

Each ping prints `pong id=<ulid> protocol=1 core=<version> rtt=<ms>ms`.
`shutdown` makes the core exit with code 0. The core also exits on Ctrl+C,
and, when started with `--parent-pid <pid>`, as soon as that process exits.
`cabinetos-cli --help` and `cabinetos-core --help` list every option.

## Threads in the core

The core runs a fixed number of async worker threads: 4 by default, or the
value of the environment variable `CABINETOS_WORKERS` (a whole number from 1
to 64; anything else falls back to 4 with a warning in the log). The workers
only route messages and wait for events.

Disk work never runs on a worker (Article 1: nothing may stall the pipe). Work
that finishes on its own, such as reading a directory, goes through Tokio's
`spawn_blocking` pool. Work that waits indefinitely, such as watching a
directory, gets a dedicated thread. In the log, the runtime's threads are
named `core-rt-N`.

Release builds keep line tables in a separate `.pdb` file next to each `.exe`,
so crash traces name file and line. Ship the `.pdb` with the `.exe`.

## Logs

`%LOCALAPPDATA%\CabinetOS\logs\core.<UTC date>.jsonl`, one JSON object per
line. Every line written while handling a request carries its `request_id`,
the same ID the CLI printed. Crash traces (`crash-<timestamp>.json`) go to the
same directory. The CLI writes a log (`cli.<date>.jsonl`) only when given
`--log-dir`. The format, the environment variables and the crash file:
[../docs/diagnostics.md](../docs/diagnostics.md).
