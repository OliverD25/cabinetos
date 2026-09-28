# CabinetOS core (Rust workspace)

The headless engine behind the CabinetOS window. The UI only draws pixels and
captures keystrokes (brief §1, the Dumb UI Rule); everything else happens
here, behind a named pipe. Which crate implements which part of the brief:
[../docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md), section "Crate map". The
protocol between UI and core, with the shared-memory layout:
[../docs/ipc.md](../docs/ipc.md). The configuration file:
[../docs/config.md](../docs/config.md). Commands, keys and chords:
[../docs/keybindings.md](../docs/keybindings.md).

## Crates

| Crate | Kind | Responsibility | Constitution articles |
|---|---|---|---|
| `cabinetos-core` | binary + library | `cabinetos-core.exe`: startup, the pipe server, session lifetime, wiring of all libraries, the settings service (configuration, commands, keymap events) | 1, 6, 7, 10, 12 |
| `cabinetos-protocol` | library | The IPC contract: message envelopes, request IDs, `#[repr(C)]` shared-memory layouts, JSON Schema export | 1, 12 |
| `cabinetos-diag` | library | JSON Lines logs, ring buffer of recent events, crash traces ([../docs/diagnostics.md](../docs/diagnostics.md)) | 12, 1 |
| `cabinetos-ipc` | library | Named pipe with a user-only DACL, length-prefixed framing, a client with events, shared-memory sections, process watch | 1, 12 |
| `cabinetos-fs` | library | Directory enumeration (NT API), sorting, the listing section writer and reader, volume and disk detection, change watching | 1, 5 |
| `cabinetos-cli` | binary | `cabinetos-cli.exe`: command-line client for the pipe, to test the core with no UI | 4, 12 |
| `cabinetos-jobs` | library (stub) | `JobQueueManager`: drive-aware copy, move and delete queues (Phase 4) | 1, 5 |
| `cabinetos-index` | library (stub) | In-memory volume index, MFT reader, USN Journal tailer (Phase 6) | 1 |
| `cabinetos-indexer` | binary (stub) | `cabinetos-indexer.exe`: the elevated indexer process (Phase 6, ADR 0002) | 1 |
| `cabinetos-config` | library | `cabinetos.json`: strict parsing with line and column errors, defaults, JSON Schema export, directory watch, diff, atomic rewrite ([../docs/config.md](../docs/config.md)) | 6 |
| `cabinetos-commands` | library | Command registry, key grammar and chords, keymap compilation with the Immutable System Tier, palette search ([../docs/keybindings.md](../docs/keybindings.md)) | 7, 4 |
| `cabinetos-plugins` | library (stub) | `wasmtime` host for Core Plugins, capability policy, trap handling (Phase 7) | 8, 10, 11 |

A stub holds only its crate documentation and the names of its future public
types, so the shape of the engine can be reviewed before the code exists.

**Unsafe code** is denied in every crate. Crates that will never need it
forbid it outright. Only the crates that call Windows APIs directly (`ipc` and
`fs` now; `jobs` and `index` later) allow it, and only in the module that
needs it; every `unsafe` block carries a `// SAFETY:` comment, which clippy
enforces.

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
temporary log directories and temporary configuration files; they never
write to the real log directory or the real `cabinetos.json`. The filesystem
tests use temporary folders (Unicode names, a path over 260 characters,
hidden files, a junction made with `mklink /J`). The CLI's own tests run
`cabinetos-core.exe` from the same target directory; `cargo test --workspace`
builds it, and after `cargo test -p cabinetos-cli` alone, build it first
with `cargo build -p cabinetos-core`.

**Benchmarks.** `cargo bench -p cabinetos-fs` measures listing 1,000 and
100,000 files (and the whole path into shared memory) against
`std::fs::read_dir`. The first run creates the fixture folders under
`%TEMP%\cabinetos-bench\` (100,000 empty files, created once); later runs
reuse them, and the bench never deletes them. CI only compiles the benchmarks.

**Schemas.** `sdk/protocol/*.schema.json` (the messages) and
`sdk/config/cabinetos.schema.json` (the configuration file) are generated
from the Rust types, and a test fails when a file is out of date. After
changing a message or a setting, regenerate and commit them (from `core/`,
in bash):

```bash
CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol
CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config
```

## Run the core and talk to it

Terminal 1, from `core/`: start the core on the pipe `demo`
(`\\.\pipe\cabinetos-core-demo`). Without `--pipe` it uses `dev`. It reads
and watches `%APPDATA%\CabinetOS\cabinetos.json`, and creates it with the
defaults on first run; `--config <path>` (or `CABINETOS_CONFIG`) picks
another file.

```text
cargo run -p cabinetos-core -- --pipe demo
```

Terminal 2, from `core/`:

```text
cargo run -p cabinetos-cli -- --pipe demo ping --count 3
cargo run -p cabinetos-cli -- --pipe demo ls C:\Windows\System32 --long
cargo run -p cabinetos-cli -- --pipe demo ls C:\Users --watch
cargo run -p cabinetos-cli -- --pipe demo volume C:\
cargo run -p cabinetos-cli -- --pipe demo keys list
cargo run -p cabinetos-cli -- --pipe demo keys set view.toggleSidebar "ctrl+alt+b"
cargo run -p cabinetos-cli -- --pipe demo keys watch
cargo run -p cabinetos-cli -- --pipe demo commands search "dual"
cargo run -p cabinetos-cli -- --pipe demo shutdown
```

- `ping` prints `pong id=<ulid> protocol=3 core=<version> rtt=<ms>ms`.
- `ls <path>` lists a directory the way the UI will: the core reads it into
  shared memory, the CLI maps the section and prints it. Options: `--long`
  (attributes, local modification time, size), `--hidden` (hidden and system
  entries too), `--sort name|size|modified|kind`, `--desc`, and `--watch`
  (print a line for each refresh until Ctrl+C). The last two lines are the
  core's time to read, sort and write the listing, and the CLI's time to map
  and decode it.
- `volume <path>` prints which volume and physical disk the path is on.
- `config path` and `config show` print the configuration file the core
  reads and the settings in effect; `config validate [file]` checks a file
  without a core.
- `commands list [--json]` prints every command with its keys, and
  `commands search <query>` ranks them as the palette does.
- `keys list` prints the keymap; `keys set <command> "<keys>"` and
  `keys reset <command>` change a binding (the core writes the file);
  `keys watch` prints each configuration and keymap change until Ctrl+C,
  with the time since the file was written.
- `shutdown` makes the core exit with code 0. The core also exits on Ctrl+C,
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
directory, gets a dedicated thread (one per watched listing, and two for the
configuration file: one waits for changes, one waits for them to settle and
reads the file). In the log, the runtime's threads are named `core-rt-N`,
listing watchers `watch-<listing id>`, and the configuration watcher's
threads `config-watch` and `config-debounce`.

Release builds keep line tables in a separate `.pdb` file next to each `.exe`,
so crash traces name file and line. Ship the `.pdb` with the `.exe`.

## Logs

`%LOCALAPPDATA%\CabinetOS\logs\core.<UTC date>.jsonl`, one JSON object per
line. Every line written while handling a request carries its `request_id`,
the same ID the CLI printed. Crash traces (`crash-<timestamp>.json`) go to the
same directory. The CLI writes a log (`cli.<date>.jsonl`) only when given
`--log-dir`. The format, the environment variables and the crash file:
[../docs/diagnostics.md](../docs/diagnostics.md).
