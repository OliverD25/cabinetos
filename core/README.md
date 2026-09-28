# CabinetOS core (Rust workspace)

The headless engine behind the CabinetOS window. The UI only draws pixels and
captures keystrokes (brief §1, the Dumb UI Rule); everything else happens
here, behind a named pipe. Which crate implements which part of the brief:
[../docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md), section "Crate map". The
protocol between UI and core, with the shared-memory layout:
[../docs/ipc.md](../docs/ipc.md). The configuration file:
[../docs/config.md](../docs/config.md). Commands, keys and chords:
[../docs/keybindings.md](../docs/keybindings.md). Copy, move and delete:
[../docs/jobs.md](../docs/jobs.md). Core Plugins:
[../docs/plugins.md](../docs/plugins.md). The indexer and search:
[../docs/indexer.md](../docs/indexer.md). The terminal sessions:
[../docs/terminal.md](../docs/terminal.md).

## Crates

| Crate | Kind | Responsibility | Constitution articles |
|---|---|---|---|
| `cabinetos-core` | binary + library | `cabinetos-core.exe`: startup, the pipe server, session lifetime, wiring of all libraries, the settings service (configuration, commands, keymap events), the job manager, the plugin host, the event hub, search (through the indexer, or a bounded walk without it), and the terminal sessions | 1, 5, 6, 7, 8, 9, 10, 12 |
| `cabinetos-protocol` | library | The IPC contract: message envelopes, request IDs, `#[repr(C)]` shared-memory layouts, JSON Schema export | 1, 12 |
| `cabinetos-diag` | library | JSON Lines logs, ring buffer of recent events, crash traces ([../docs/diagnostics.md](../docs/diagnostics.md)) | 12, 1 |
| `cabinetos-ipc` | library | Named pipe with a user-only DACL, length-prefixed framing, a client with events, shared-memory sections, process watch, and the byte pipes of terminal sessions | 1, 12 |
| `cabinetos-fs` | library | Directory enumeration (NT API), sorting, the listing section writer and reader, volume and disk detection, change watching | 1, 5 |
| `cabinetos-cli` | binary | `cabinetos-cli.exe`: command-line client for the pipe, to test the core with no UI | 4, 12 |
| `cabinetos-jobs` | library | `JobQueueManager`: per-disk queues, copy (`CopyFileExW`), move, delete (Recycle Bin or permanent), per-file conflicts, progress throttled to 30 events per second ([../docs/jobs.md](../docs/jobs.md)) | 1, 5 |
| `cabinetos-index` | library | The in-memory index of NTFS volumes: built with `FSCTL_ENUM_USN_DATA`, kept current from the USN Journal, interned names, ranked substring search on several threads ([../docs/indexer.md](../docs/indexer.md)) | 1 |
| `cabinetos-indexer` | binary + library | `cabinetos-indexer.exe`: the elevated indexer (ADR 0002): console and service modes, the read-only pipe with a medium integrity label | 1, 12 |
| `cabinetos-config` | library | `cabinetos.json`: strict parsing with line and column errors, defaults, JSON Schema export, directory watch, diff, atomic rewrite ([../docs/config.md](../docs/config.md)) | 6 |
| `cabinetos-commands` | library | Command registry, key grammar and chords, keymap compilation with the Immutable System Tier, palette search ([../docs/keybindings.md](../docs/keybindings.md)) | 7, 4 |
| `cabinetos-plugins` | library | `PluginHost`: Core Plugins as WebAssembly components in `wasmtime`, strict manifests, capabilities, the WASI sandbox, fuel, deadline and memory limits per call, trap containment and restarts ([../docs/plugins.md](../docs/plugins.md)) | 8, 10, 11 |
| `cabinetos-terminal` | library | `Terminals`: shells in pseudo-consoles (ConPTY), a byte pipe per session for one client at a time, output bounded to 1 MiB with backpressure, the change-directory line of each shell, and the console side of `cabinetos-cli term` ([../docs/terminal.md](../docs/terminal.md)) | 9, 4, 1 |

A stub holds only its crate documentation and the names of its future public
types, so the shape of the engine can be reviewed before the code exists.

**Unsafe code** is denied in every crate. Crates that will never need it
forbid it outright. Only the crates that call Windows APIs directly (`ipc`,
`fs`, `jobs`, `index` and `terminal`) allow it, and only in the modules that need it; every `unsafe` block carries a `// SAFETY:` comment, which clippy
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

The plugin tests load the committed components in
`../sdk/fixtures/plugins` (built from `../sdk/templates/plugins` with
`../sdk/templates/build-fixtures.ps1`), so they need no WebAssembly
toolchain. Every test that starts a core points `CABINETOS_PLUGINS_DIR` and
`CABINETOS_PLUGINS_DATA_DIR` at its temporary folder, so no test sees the
plugins installed on the machine. The `reader` fixture reads
`%TEMP%\cabinetos-plugins-test\reader`, which its tests create and remove.

The indexer's tests need Administrator rights to read the MFT and the
change journal, so they are ignored and, when run without the rights, skip
with a message. CI runs them: its runners are Administrators. From an
elevated terminal, `cargo test --release -p cabinetos-index -p
cabinetos-indexer -- --ignored --nocapture` runs them and prints the build
time, memory per entry, search time and change latency; the service test
also needs `CABINETOS_TEST_SERVICE=1`, because it installs and removes a
Windows service. They write only under `%TEMP%\cabinetos-index-test\`, as do
the search tests, and every test that starts a core points
`CABINETOS_INDEXER_PIPE` at a pipe of its own, so no test talks to an
indexer that runs on the machine.

The terminal tests (`cabinetos-terminal`, the core's `tests/terminal.rs`
and the CLI's `tests/term.rs`) run real shells in pseudo-consoles: cmd
always, pwsh, Windows PowerShell and WSL when they are installed, each
skipped with a message otherwise. The shells run only `echo`, `cd`, `mode
con`, `Get-Location`, `pwd` and `exit`, in folders under
`%TEMP%\cabinetos-term-test\`, which the tests remove.

The job tests (copy, move, delete) write only under
`%TEMP%\cabinetos-jobs-test\` and remove what they wrote. Two of them run
only on request: the move across volumes needs
`CABINETOS_TEST_SECOND_VOLUME` (a folder on another volume) and
`--ignored`; the Recycle Bin test needs `CABINETOS_TEST_RECYCLE_BIN=1`,
because it puts a real file into the Recycle Bin (CI sets it; its runner is
thrown away). `cargo test -p cabinetos-jobs --release --test engine --
--ignored --nocapture measure` repeats the files-in-flight measurement.

**Benchmarks.** `cargo bench -p cabinetos-fs` measures listing 1,000,
10,000 and 100,000 files (and the whole path into shared memory) both
ways, `_serial` (the Phase 2 path) and `_pipelined` (the default), against
`std::fs::read_dir`. The first run creates the fixture folders under
`%TEMP%\cabinetos-bench\` (empty files, created once); later runs reuse
them, and the bench never deletes them. The test `tests/pipeline.rs`, which
checks that both ways write the same section bytes, lists the same
100,000-file folder (creating it when missing) and seeded random folders
under `%TEMP%\cabinetos-fs-test\`, which it removes. CI only compiles the
benchmarks.

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
another file. It loads the Core Plugins from
`%LOCALAPPDATA%\CabinetOS\plugins`; `--plugins-dir <path>` (or
`CABINETOS_PLUGINS_DIR`) picks another folder, and `--plugins-data-dir`
(or `CABINETOS_PLUGINS_DATA_DIR`) the folder of the plugins' own folders.

```text
cargo run -p cabinetos-core -- --pipe demo
```

To try the sample and test plugins with a configuration that is not your
own (in cmd; in PowerShell write `$env:TEMP` for `%TEMP%`):

```text
cargo run -p cabinetos-core -- --pipe demo --plugins-dir ..\sdk\fixtures\plugins --plugins-data-dir %TEMP%\cabinetos-demo\plugins-data --config %TEMP%\cabinetos-demo\cabinetos.json
```

Terminal 2, from `core/`:

```text
cargo run -p cabinetos-cli -- --pipe demo ping --count 3
cargo run -p cabinetos-cli -- --pipe demo ls C:\Windows\System32 --long
cargo run -p cabinetos-cli -- --pipe demo ls C:\Users --watch
cargo run -p cabinetos-cli -- --pipe demo volume C:\
cargo run -p cabinetos-cli -- --pipe demo volumes
cargo run -p cabinetos-cli -- --pipe demo describe C:\Windows --count 10
cargo run -p cabinetos-cli -- --pipe demo icon ext:.txt --size 32 --out txt.png
cargo run -p cabinetos-cli -- --pipe demo config get ui.dualPane
cargo run -p cabinetos-cli -- --pipe demo config set ui.dualPane false
cargo run -p cabinetos-cli -- --pipe demo mkdir "D:\work\New folder"
cargo run -p cabinetos-cli -- --pipe demo rename D:\work\draft.txt final.txt
cargo run -p cabinetos-cli -- --pipe demo open D:\work\final.txt
cargo run -p cabinetos-cli -- --pipe demo keys list
cargo run -p cabinetos-cli -- --pipe demo keys set view.toggleSidebar "ctrl+alt+b"
cargo run -p cabinetos-cli -- --pipe demo keys watch
cargo run -p cabinetos-cli -- --pipe demo commands search "dual"
cargo run -p cabinetos-cli -- --pipe demo copy C:\Users\me\Pictures D:\backup --stats
cargo run -p cabinetos-cli -- --pipe demo jobs
cargo run -p cabinetos-cli -- --pipe demo plugins list
cargo run -p cabinetos-cli -- --pipe demo plugins grant hello cmd:register events:emit
cargo run -p cabinetos-cli -- --pipe demo commands exec hello.say
cargo run -p cabinetos-cli -- --pipe demo events watch
cargo run -p cabinetos-cli -- --pipe demo search budget
cargo run -p cabinetos-cli -- --pipe demo search budget --root D:\work --limit 10
cargo run -p cabinetos-cli -- --pipe demo index status
cargo run -p cabinetos-cli -- --pipe demo term --profile cmd
cargo run -p cabinetos-cli -- --pipe demo term list
cargo run -p cabinetos-cli -- --pipe demo term cd 1 D:\work
cargo run -p cabinetos-cli -- --pipe demo term close 1
cargo run -p cabinetos-cli -- --pipe demo shutdown
```

For searches over whole volumes, start the indexer from an elevated
terminal first (Run as administrator), in `core/`:

```text
cargo run --release -p cabinetos-indexer -- --console --volumes C
```

- `ping` prints `pong id=<ulid> protocol=9 core=<version> rtt=<ms>ms`.
- `ls <path>` lists a directory the way the UI will: the core reads it into
  shared memory, the CLI maps the section and prints it. Options: `--long`
  (attributes, local modification time, size), `--hidden` (hidden and system
  entries too), `--sort name|size|modified|kind`, `--desc`, and `--watch`
  (print a line for each refresh until Ctrl+C). The last two lines are the
  core's time to read, sort and write the listing, and the CLI's time to map
  and decode it.
- `volume <path>` prints which volume and physical disk the path is on;
  `volumes` prints every drive letter's volume as a table, with each
  disk's bus and media type.
- `config path` and `config show` print the configuration file the core
  reads and the settings in effect; `config get <path>` prints one setting
  and `config set <path> <value>` changes one (the value is JSON, or a
  bare word as text; the core writes the file); `config validate [file]`
  checks a file without a core.
- `describe <path> [--from N] [--count M]` lists a folder and prints each
  entry's index, name, the shell's type name and its icon key, then the
  round trip; `icon <key> [--size N] --out <file.png>` writes that icon
  (16, 24, 32 or 48 pixels) and prints its size from the PNG's header.
- `mkdir <path>` creates a folder, `rename <path> <new name>` renames a
  file or folder in place (never replacing anything), and `open <path>`
  opens a file or folder with its default application, as a double-click
  in Explorer does.
- `commands list [--json]` prints every command with its keys, and
  `commands search <query>` ranks them as the palette does.
- `keys list` prints the keymap; `keys set <command> "<keys>"` and
  `keys reset <command>` change a binding (the core writes the file);
  `keys watch` prints each configuration and keymap change until Ctrl+C,
  with the time since the file was written.
- `copy <src>... <dst>`, `move <src>... <dst>` and `delete <path>...
  [--permanent]` start a job and follow it: one progress line, conflicts
  as they come (`--on-conflict`, `--resolve`), a summary at the end, and
  with `--stats` the progress events per second. `jobs` lists the jobs;
  `job pause|resume|cancel <id>` and `job resolve <job> <conflict>
  <overwrite|skip|rename|retry|delete-permanently|cancel>` control them.
- `plugins list [--json]` prints every plugin with its state, capabilities
  and commands; `plugins grant <id> <capability>...`, `plugins enable|disable
  <id>` (the core writes the file) and `plugins reload <id>` change one and
  print its state once it has settled. `commands exec <command>
  [json-args]` runs a command and prints its JSON result, and `events watch`
  prints every event as one JSON line until Ctrl+C.
- `search <query> [--limit N] [--root path]` prints the hits (`f` file, `d`
  folder), then how many, `source: index` or `source: walk`, the time, and
  whether the search was complete; `index status` prints whether an indexer
  answers and each volume's state.
- `term [--profile NAME] [--cwd PATH]` runs a shell in the core, attached
  to this console: keys go to the shell as typed, the size follows the
  window, `Ctrl+]` detaches (the shell keeps running), and when the shell
  exits the CLI prints its exit code. With input from a pipe
  (`printf 'dir\r\nexit\r\n' | … term`) it forwards the bytes and waits
  for the shell to exit. `term list` prints every session, `term cd <id>
  <path>` types the shell's own change-directory command, and `term close
  <id>` ends a session.
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
listing watchers `watch-<listing id>`, the configuration watcher's
threads `config-watch` and `config-debounce`, each running job `job-<id>`
(and `job-<id>-<n>` for its extra copy workers), the progress
publisher of all jobs `job-progress`, each running plugin `plugin-<id>`,
the plugins' deadline ticker `plugin-epoch` (it sleeps while no plugin call
runs), and `plugin-<id>-restart` for the 5 s wait before a crashed plugin
starts again. A search that walks folders runs on the blocking pool. Each
terminal session has three threads: `term-<id>-out` reads the shell's
output, `term-<id>-in` writes its keys, and `term-<id>-exit` waits for the
shell to exit. In the indexer, each volume has a thread `index-<letter>`
that builds its index and then follows its change journal.

Release builds keep line tables in a separate `.pdb` file next to each `.exe`,
so crash traces name file and line. Ship the `.pdb` with the `.exe`.

## Logs

`%LOCALAPPDATA%\CabinetOS\logs\core.<UTC date>.jsonl`, one JSON object per
line. Every line written while handling a request carries its `request_id`,
the same ID the CLI printed. Crash traces (`crash-<timestamp>.json`) go to the
same directory. The CLI writes a log (`cli.<date>.jsonl`) only when given
`--log-dir`. The indexer writes `indexer.<date>.jsonl` (boundary `indexer`)
there too when run with `--console`, and in `%ProgramData%\CabinetOS\logs`
as a service. The format, the environment variables and the crash file:
[../docs/diagnostics.md](../docs/diagnostics.md).
