## Phase 1 report

Context: CabinetOS Phase 1, the Rust core scaffold. The repo is `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos`. The path in the prompt had lost its backslashes; this is the real one.

**Built:** A Cargo workspace at `core/` with 12 crates. Toolchain pinned to 1.98.1, edition 2024, resolver 3, `publish = false`. Workspace lints: `unsafe_code = deny`, `missing_docs = warn`, `clippy::all` + `pedantic` with 4 allowed lints, and `undocumented_unsafe_blocks`, which makes clippy fail on any unsafe block without a `// SAFETY:` comment (I proved it fires by removing one comment).
- **protocol:** envelopes and request IDs; the shared-memory structs are pinned by size and offset tests plus a compile-time assert; JSON Schema files are generated into `sdk/protocol/`, and a snapshot test fails when they go stale.
- **diag:** our own JSON Lines formatter, written by a background thread to a file that rolls over daily; a ring buffer of the last 256 lines; a panic hook that writes `crash-<ts>.json` and then flushes the log.
- **ipc:** 4-byte little-endian framing with a 16 MiB limit; a named pipe whose DACL allows only the current user (a test reads the DACL back from a live pipe); remote clients rejected; a client that retries while the pipe is busy and checks every reply ID; shared-memory sections with handle duplication; a parent-process watch. All unsafe code sits in three modules marked `#[allow(unsafe_code)]` in `ipc/src/lib.rs`.
- **core (lib + bin):** ping and shutdown, typed error replies, Ctrl+C, exit when the parent exits, and the hidden `--self-test-panic`.
- **cli:** `ping [--count N]` and `shutdown`.
- The other seven crates are documented stubs.
- Also: the CI workflow, a crate map in `docs/ARCHITECTURE.md`, `docs/diagnostics.md` and `core/README.md`. 73 tests.

**Commits** (all pushed to origin/main):
948a35a core: Cargo workspace skeleton with all twelve crates
a97f14d protocol: control-channel envelopes, request IDs and shared-memory layouts
01550d2 diag: JSON Lines logging, ring buffer and crash traces for every process
a6ac08d ipc: user-only named pipe, framing, shared memory and process watch
0b3687b core, cli: the core serves ping and shutdown; the CLI talks to it
08e3ae7 ci: run fmt, clippy, tests, release build and cargo-deny on every change
112f75d docs: crate map, diagnostics format and the core README
171b461 ipc: DACL test accepts SDDL aliases for the user's SID

**Checks** (from `core/`, final code):
- `cargo build --workspace`: pass. `Finished dev profile [unoptimized + debuginfo] target(s) in 1.82s`.
- `cargo test --workspace`: pass. 73 passed, 0 failed, 0 ignored.
  - By crate: protocol 17, diag 16+1, ipc 15+6, core 4 lib + 3 bin + 5 end-to-end, cli 5.
  - The end-to-end suite passed 15 extra repeat runs out of 15.
- `cargo clippy --workspace --all-targets -- -D warnings`: pass, exit 0.
- `cargo fmt --all -- --check`: pass, no diff.
- `cargo deny check`: pass. `advisories ok, bans ok, licenses ok, sources ok`. Only warnings remain: 5 allowed licenses that no dependency uses, and two `syn` versions in build-time macro crates.
- Also passing: `cargo build --workspace --release`, and the same five checks run from WSL via `cargo.exe`.
- **CI:** run 36361722710 failed in one test only. The runner runs as the built-in Administrator, whose SID Windows writes as the short alias `LA`. The DACL itself was correct. Fixed in 171b461. Run 36362106908 is green: both jobs pass, 73 tests. https://github.com/OliverD25/cabinetos/actions/runs/36362106908

**Live check** (core started in the background with `--pipe demo`):
```
pong id=01M3JP62MKWQQNAMJMXY6XDEY1 protocol=1 core=0.1.0 rtt=0.73ms
pong id=01M3JP62MMY4V00Q8NMWG918GS protocol=1 core=0.1.0 rtt=0.16ms
pong id=01M3JP62MM5KTVC7RG7WX8WREA protocol=1 core=0.1.0 rtt=0.15ms
ping exit code: 0
shutdown acknowledged id=01M3JP62NZSDRJ0PXH9X1FH9ZH
shutdown exit code: 0
```
The core's background task printed `cabinetos-core exited with code 0`.

Last 3 lines of `C:\Users\Admin\AppData\Local\CabinetOS\logs\core.2026-09-28.jsonl` (the file name carries the date; see Decided):
```
{"ts":"2026-09-28T00:21:08.416Z","level":"INFO","boundary":"engine","target":"cabinetos_core","message":"request handled","request_id":"01M3JP62NZSDRJ0PXH9X1FH9ZH","span":"request","fields":{"elapsed_us":114,"request":"shutdown"},"thread":"core-worker-4"}
{"ts":"2026-09-28T00:21:08.416Z","level":"INFO","boundary":"engine","target":"cabinetos_core","message":"shutdown requested by a client","request_id":"01M3JP62NZSDRJ0PXH9X1FH9ZH","span":"request","thread":"core-worker-4"}
{"ts":"2026-09-28T00:21:08.416Z","level":"INFO","boundary":"engine","target":"cabinetos_core","message":"core stopped","thread":"main"}
```
All three ping IDs appear in the same file as `request handled` lines with `boundary: "engine"`. The tests never wrote to that directory; it still holds only these 7 lines.

**Decided:**
- **Log file names:** `<process>.<UTC date>.jsonl` (for example `core.2026-09-28.jsonl`), not `core.jsonl`.
  - Because: tracing-appender's daily rotation always puts the date in the name, and its `latest_symlink` option makes init fail on Windows without Developer Mode or admin rights.
  - Undo: use `Rotation::NEVER` in `init()` in `core/crates/cabinetos-diag/src/lib.rs`. The name then becomes `core.jsonl` but the file never rolls over. The tests search `core.*.jsonl`, so they pass either way.
- **cargo-deny runs as a second CI job on ubuntu-latest.**
  - Because: `EmbarkStudios/cargo-deny-action` is a Docker action, and GitHub runs Docker actions only on Linux. `deny.toml` limits the dependency graph to the Windows targets, so the answer is the same.
  - Undo: in the Windows job, replace it with a step that runs `cargo install cargo-deny --locked`, then `cargo deny check`.
- **"Warn on unmaintained":** `unmaintained = "all"` in `deny.toml`, plus `--warn unmaintained` in CI.
  - Because: cargo-deny 0.20 has no warn level for unmaintained crates in its config file; every advisory in scope is an error (I checked its source). As a result, a plain local `cargo deny check` fails on an unmaintained crate, while CI only warns.
  - Undo: set `unmaintained = "workspace"` or `"none"` and drop `command-arguments` in `ci.yml`.
- **CI jobs have `timeout-minutes` (30 and 10) and use `actions/checkout@v7`** (the latest major tag).
  - Because: a hung test on this private repo would burn paid Windows minutes.
  - Undo: delete the two lines.
- **Added `core/clippy.toml`** with `doc-valid-idents = ["CabinetOS","NVMe","WinUI",".."]`.
  - Because: the pedantic `doc_markdown` lint flags the product name in every doc comment.
  - Undo: delete the file and add `doc_markdown = "allow"` to the workspace lints.
- **Pedantic lints turned off:** `missing_errors_doc`, `missing_panics_doc`, `module_name_repetitions`, `must_use_candidate`.
  - Because: they add noise without catching real problems in this code.
  - Undo: edit `[workspace.lints.clippy]` in `core/Cargo.toml`.
- **`publish = false` for all crates.**
  - Because: these are private binaries; this blocks an accidental `cargo publish`.
  - Undo: remove the line from `[workspace.package]`.
- **The protocol crate lists itself as a dev-dependency with the `schema` feature.**
  - Because: the schema snapshot test then runs under a plain `cargo test --workspace`, and release builds do not compile schemars.
  - Undo: make `schema` a default feature and drop the dev-dependency.
- **Schema titles are `RequestEnvelope` and `ResponseEnvelope`.**
  - Because: schemars titled both files "Envelope", and C# code generators turn the title into a class name.
  - Undo: remove `titled()` in `schema.rs` and regenerate.
- **`RequestId` keeps the ULID text exactly as received.** It is validated, including the 128-bit limit that the `ulid` crate does not check.
  - Because: a reply must echo the ID byte for byte, even when a client sends lower case.
  - Undo: store `ulid::Ulid` inside instead.
- **`EntryKind` is a `#[repr(u8)]` enum** (0 unknown, 1 file, 2 directory, 3 reparse point) with `from_raw`/`to_raw`. The struct field `kind` stays `u8`.
  - Because: reading an enum straight from shared memory is undefined behaviour when the byte holds an unknown value.
  - Undo: replace it with `pub const` u8 values.
- **Error-reply rules:**
  - `unknown_request`: the envelope and its ID are valid, but `type` is not in `Request::TYPES`.
  - `protocol_error`: everything else.
  - `frame_too_large`: the core sends the reply, then closes the connection.
  - A frame with no usable ID is answered, and logged, under a new ID.
  - Undo: `decode_request()` in `core/crates/cabinetos-core/src/lib.rs`.
- **`DiagConfig` got a fourth field, `log_file: bool`** (plus `DiagConfig::new`).
  - Because: the CLI must run with no log file unless it gets `--log-dir`, and `dir: None` already means "use the default directory".
  - Undo: remove the field and let the CLI skip `diag::init`.
- **New environment variable `CABINETOS_LOG`** (level filter, default `info`; a bad value falls back to `info` with a warning).
  - Because: "connection tasks log at debug" needs a way to switch debug on.
  - Undo: drop `parse_filter` in `diag::init`.
- **CLI output rules:**
  - Its boundary is `frontend`, because it stands in for the UI.
  - When stderr is its only output, stderr shows only warnings and errors.
  - It logs one INFO "reply received" line per request, so with `--log-dir` its log can be matched with the core's log by `request_id`.
  - It prints a failure once.
  - Undo: `cabinetos-cli/src/main.rs` and `stderr_level` in `diag::init`.
- **The panic hook writes no tracing event and takes no lock it would have to wait for.**
  - Details: `recent_events` go into the crash file as JSON objects. If two processes crash in the same millisecond, the second file gets `-<pid>` added. The hook is crate-private; `init` installs it.
  - Because: a panic that happens while a lock is held must not hang the dying process.
  - Undo: `cabinetos-diag/src/panic.rs`.
- **A panicked connection task stops the core with exit code 1.**
  - Because: the panic hook has already closed the log writer, so running on would mean running without logs. The UI is expected to restart the core.
  - Undo: remove the `join_next` branch in `serve()`.
- **Core timing and naming constants:**
  - Open connections get 2 s to finish at shutdown.
  - A failed accept logs a warning and retries after 100 ms; it is never fatal.
  - Unknown request types are echoed in errors up to 64 characters.
  - Worker threads are named `core-worker-N`.
  - Undo: the constants in core `lib.rs` and `main.rs`.
- **`PipeServer::accept` replaces the pipe instance even when connect fails.**
  - Because: otherwise one client that leaves early would break every later accept.
  - Undo: not needed.
- **`IpcError::Io` and `IpcError::Windows` are `#[error(transparent)]`.**
  - Because: the CLI printed the OS error message twice.
  - Undo: restore the prefixed messages.
- **`SharedSection::duplicate_for` uses `DUPLICATE_SAME_ACCESS`**, so the target process gets read and write access.
  - Because: nothing reads sections yet; a read-only duplicate belongs with Phase 2's reader.
  - Undo: pass `FILE_MAP_READ` and add a read-only `map`.
- **`cabinetos-indexer` also has `#![forbid(unsafe_code)]`**, although the prompt's table left it out.
  - Because: your rule "crates that never need unsafe forbid it", and PLAN §6 says the indexer needs no unsafe.
  - Undo: remove the attribute.
- **Commit order: protocol before diag** (the prompt listed diag first).
  - Because: diag's `span_for_request` needs `RequestId`.
- **Smaller dependency choices:** `PipeName::random()` uses `rand` 0.10 (its OS-seeded ChaCha generator), and timestamps are formatted by hand from the `time` crate. Both crates were already in the dependency tree, so no new crates were added.

**Needs the user:**
- **Log file names in the docs.**
  - What: `docs/PLAN.md` lines 43, 108 and 232, and `docs/dev-setup.md` lines 78–79, still say `core.jsonl`.
  - Why it needs you: CLAUDE.md says a PLAN.md change must be mirrored to the Notion desk card in the same session, and this run must not touch Notion.
  - Ready: replace `core.jsonl` with `core.<date>.jsonl` on PLAN.md lines 108 and 232. Change PLAN.md line 43 to "one JSON Lines file per process and UTC day: `core.<date>.jsonl`, `ui.<date>.jsonl`, `indexer.<date>.jsonl`". Apply the same wording to dev-setup.md lines 78–79. Then refresh the desk card "Development Plan (mirror of the repo plan)".
- **The WSL build command in `docs/dev-setup.md` line 32.**
  - What: inside WSL, `cargo` is the Linux cargo (`/home/rde/.cargo/bin/cargo`), which cannot build this Windows-only code.
  - Why it needs you: this file was not in the Phase 1 list, so I did not edit it.
  - Ready (I ran this form from WSL; all five checks pass):
    `cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core && cargo.exe build --workspace && cargo.exe test --workspace && cargo.exe clippy --workspace --all-targets -- -D warnings && cargo.exe fmt --all -- --check && cargo.exe deny check`

**Known gaps:** None. Every definition-of-done item holds, and git status is clean and in sync with origin/main at 171b461.

**Other actions I took:** I installed cargo-deny 0.20.2 with `cargo install cargo-deny --locked`. One `rustup toolchain install` call only checked for a rustup self-update; rustup is still 1.29.1.

**Noticed, out of scope:**
- The status line in the root `README.md` ("code starts with the Rust core scaffold") is out of date.
- Old daily log files are never deleted. tracing-appender's `max_log_files` could cap them.
- Release builds have no debug info, so crash backtraces from release binaries show no file and line. `[profile.release] debug = "line-tables-only"` would fix this.
- Article 12 asks for a lock-free logging pipeline. The ring buffer uses the `Mutex` the prompt specified, a short lock on every event. crossbeam's `ArrayQueue::force_push` would make it lock-free.
- Phase 6: an elevated indexer's pipe gets a High integrity label by default, and Windows then blocks writes from the normal-rights core. That pipe will need an explicit medium label.
- `MappedView::as_slice` cannot see writes that arrive through another view of the same memory. Phase 2 must keep one writer at a time; this is documented on the type.
- PLAN §2 plans an ARM64 CI build; I did not add it, because the prompt said to add nothing else to CI.
- The tokio runtime starts one worker thread per CPU, 32 on this PC. A small fixed number may be enough for a mostly idle core.

Key files:
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\Cargo.toml
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\deny.toml
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\README.md
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\diagnostics.md
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.github\workflows\ci.yml
