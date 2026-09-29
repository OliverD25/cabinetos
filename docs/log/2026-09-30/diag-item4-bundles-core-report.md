## Item 4, bundles (core part)

Agent: a8ef741452d0b3317

Report: Item 4, bundles (core part)

Context: CabinetOS Phase 15 (heavy logging mode, trace ids, log bundles). This is the last report from the coder agent in worktree agent-a8ef741452d0b3317. As the coordinator told me, I stopped after the core part of item 4. I did not do item 3, the window part of item 4, item 5 or item 6.

## Commits (all pushed to origin/main; the worktree is clean)
- Item 1, trace id: 054a1fa (the protocol envelope change, pushed first), 687abd3, d60d82f
- Item 2, heavy sink in the core: 3f053d3, bd36611 (bd36611 masks the protocol-13 `secret` reply type)
- Item 4, core part, plus the hardened timing tests: 840b22a "diagnostics: log bundles on request and on a crash in heavy mode; timing tests hardened". It sits on top of f91a5be and 94b0e33 from the other agents.

## What item 4 built
- **New file `core/crates/cabinetos-diag/src/bundle.rs`.** Its public API:
  - `save_bundle(minutes) -> Result<PathBuf>`
  - `set_bundle_config(&serde_json::Value)`
  - `set_bundle_windows_build(String)`
  - the constants `BUNDLE_MINUTES = 10` and `MAX_BUNDLE_MINUTES = 1440`
- **What a zip holds:**
  - every `*.jsonl` in the log folder (all processes, normal and heavy files), cut to the lines stamped within the last `minutes`;
  - every `crash-*.json` changed in the last 24 h;
  - `bundle.json` with these fields: `created`, `reason` (`asked` or `crash`), `process`, `minutes`, `since`, `versions` {`cabinetos`, `protocol`}, `windows_build`, `environment` (the CABINETOS_* variables, key-like values shown as `"***"`), `config` (secrets masked), and `files` [{`name`, `lines`, `bytes`}].
- **How files are read:** from their end, 1 MiB at a time. Reading stops at the first piece that holds only older lines. The zip uses Deflate level 1 (zip 8.6, feature `deflate-flate2-zlib-rs`, added to diag's Cargo.toml).
- **File names:** `bundle-<YYYYMMDDTHHMMSSmmmZ>.zip` on request, `crash-<YYYYMMDDTHHMMSSmmmZ>.zip` from the panic hook. Both are written in the process's own log folder.
- **Crash hook, `diag/src/panic.rs`:** `write_crash_bundle(process)` runs after the crash trace and after both log writers are flushed. It runs only when `heavy_enabled()` is true. It runs on the panicking thread and never waits for a lock: it uses try_lock on the config and build facts and leaves out whatever is locked. stderr says which files were written.
- **Windows build, new file `core/crates/cabinetos-fs/src/system.rs`:** `pub fn windows_build() -> Option<String>` reads the registry. The result looks like "10.0.26200.6899 (25H2)". The module is `#[allow(unsafe_code)] mod system`, with SAFETY comments.
- **Protocol, `core/crates/cabinetos-protocol/src/message.rs`:**
  - Request `Request::SaveLogBundle { minutes: u32 }`. Its wire name is `"save_log_bundle"`, and `minutes` defaults to 10.
  - Reply `Response::LogBundle { path: String }`. Its wire name is `"log_bundle"`.
  - The schemas in sdk/protocol were regenerated.
- **Core:**
  - `core/crates/cabinetos-core/src/connection.rs`: `save_log_bundle(&mut self, id, span, kind, minutes)` runs on the blocking pool. It answers `protocol_error` when minutes is outside 1..=1440, and `internal` when the zip fails.
  - `core/crates/cabinetos-core/src/lib.rs`: new `prepare_diagnostics()`. It marks the main thread as never-wait and registers the Windows build. It sits next to the core agent's `warn_about_workers()`; the rebase conflict kept both functions.
  - `core/crates/cabinetos-core/src/settings.rs`: `bundle_config(&Config)` gives the bundle the config in effect.
- **CLI:**
  - `core/crates/cabinetos-cli/src/main.rs`: new subcommand `cabinetos-cli log bundle [--minutes 10]`. It prints the zip's path. New helpers `without_core()` and `LogAction::minutes()`.
  - `core/crates/cabinetos-cli/src/logs.rs`: new `bundle(client, minutes)`.

## New tests (+8)
- cabinetos-protocol: `a_log_bundle_asks_for_ten_minutes_unless_told`
- cabinetos-diag, bundle.rs: `a_bundle_holds_the_last_minutes_of_every_log_and_the_recent_crashes`, `keys_in_the_environment_are_masked`, `a_large_file_is_read_from_its_end`
- cabinetos-fs: `the_build_of_this_windows_is_read`
- cabinetos-core tests/core_process.rs: `a_panic_in_heavy_mode_also_writes_a_crash_bundle`
- cabinetos-core tests/heavy.rs: `a_log_bundle_is_saved_on_request`
- cabinetos-cli tests/logs.rs: `log_bundle_asks_the_core_and_prints_the_zip_s_path`

## Hardened timing tests (the coordinator's note)
Each of these now polls for its condition up to a generous limit. None of them depends on a fixed sleep any more.
- `cabinetos-diag/src/heavy.rs::a_thread_waits_for_a_slow_writer_and_no_line_is_lost`: the fake writer holds its first batch until a producer thread is really waiting. A new `HeavyQueue.waiting: AtomicUsize` counter shows that. So the wait happens on any machine speed. The flush limit is PATIENCE, 2 min.
- `cabinetos-diag/src/heavy.rs::a_thread_that_never_waits_drops_and_the_count_is_written`: the gate and the flush use PATIENCE (2 min). The "it never waited" check is now < 1 min, where it used to be < 5 s.
- `cabinetos-diag/tests/heavy.rs::heavy_mode_switches_while_the_process_runs`: the check that the file is closed now retries `remove_file` for up to 2 min.
- `cabinetos-core/tests/heavy.rs`: `within_a_second` became `eventually` (1 min). STARTUP_DEADLINE went from 10 s to 60 s, and JOB_DEADLINE from 60 s to 120 s.
- `cabinetos-core/tests/traces.rs`: STARTUP_DEADLINE went from 10 s to 60 s, and SETTLE_DEADLINE from 60 s to 120 s.
- `cabinetos-cli/tests/logs.rs`, the log bundle test: it waits up to 1 min.
- `cabinetos-core/tests/core_process.rs`, the crash bundle test: it waits up to 2 min for the process to exit.

## Checks, verbatim results
- Core after the rebase onto f91a5be: `cargo test --workspace --no-fail-fast` gave "passed 679 failed 0". Clippy with `-D warnings` was clean. `cargo fmt --all -- --check` was clean. `cargo deny check` gave "advisories ok, bans ok, licenses ok, sources ok". The build was ok.
- One earlier full run failed once, in `capabilities_gate_the_file_system` (cabinetos-plugins tests/host.rs:334, "inside.txt: No such file or directory"). That test uses a fixed folder under %TEMP%, and concurrent agents race on it. It is not my code, and the rerun passed. It may also be the unnamed failure the coordinator saw. It is worth a separate fix: give that test a unique temp folder.
- UI: `dotnet build ui/CabinetOS.sln -c Debug -warnaserror` gave "Build succeeded." with 0 warnings. `CABINETOS_UI_E2E=1` with my debug core gave "total: 654 failed: 0 succeeded: 654".

## Decisions (what — because — undo)
1. The reply type is named `log_bundle` — because the handout did not name it — undo: rename it in message.rs and regenerate the schema.
2. `minutes` must be 1..=1440, otherwise `protocol_error` — because the handout gave no range — undo: widen the check in connection.rs `save_log_bundle`.
3. The Windows build is read in `cabinetos-fs::windows_build`, and the core registers it with diag — because cabinetos-diag forbids unsafe code — undo: delete fs/src/system.rs and its call in `prepare_diagnostics`.
4. Crash bundles written by the indexer or the CLI have no windows_build and no config — because only the core registers these facts — undo: register them in those processes too.
5. Crash traces are chosen by modified time within 24 h — undo: parse the stamp in the file name instead.
6. A log file with no line in the time window is left out of the zip — undo: include empty entries.
7. Lines without a `ts` stamp are kept — undo: drop them in `keep_recent`.
8. bundle.json includes the CABINETOS_* environment, with key-like values masked — because the handout's masking list names these variables — undo: remove the `environment` field.
9. The crash zip is written on the panicking thread, only in heavy mode, with no lock waits — as the handout asks.
10. Deflate level 1 — because a crash bundle is written while the process goes down — undo: raise the level in bundle.rs.
11. CabinetOS never deletes bundles — undo: add a cleanup rule.
12. The bundle includes heavy files that other processes still have open. They can be read because the heavy writer shares READ.

## Docs done (the next agent writes only the rest)
- docs/diagnostics.md: the "Bundles" section and the "Bundles" row in the file table. Also, from items 1 and 2: "How an action's trace id travels", the `trace_id` row, and "Heavy mode".
- docs/ipc.md: the `save_log_bundle` row in the request table, the "Log bundles" section, and the version 13 note (save_log_bundle and trace). Also, from item 1: the envelope `trace` field.
- docs/config.md: `logging.heavy`, from item 2.
- Not done: docs/ui.md, README test counts, ADR 0013. diagnostics.md mentions ADR 0013 without a link, because the file does not exist yet.

## Ready plan text for docs/PLAN.md (I did not edit PLAN.md)
"Item 4, bundles (core part) — done 2026-09-30 (840b22a). `save_log_bundle {minutes}` → `log_bundle {path}`: bundle-<time>.zip with the last minutes of every *.jsonl (all processes, normal and heavy), crash traces of 24 h, bundle.json (versions, Windows build, CABINETOS_* env and config, secrets masked). In heavy mode the panic hook writes crash-<time>.zip. `cabinetos-cli log bundle [--minutes 10]`. Timing tests of heavy mode hardened. Core tests 679 passing."

## Short recap of items 1 and 2 (the full reports went to main earlier)
- **Envelope.** It gains `trace: Option<RequestId>` (a ULID). It is left out of the JSON when it is None. `Envelope::traced(id, trace, body)` builds one, and `trace_or_id()` reads one. The window sets `CoreRequest.Trace`. CoreClient fills it from `Diag.CurrentTrace` when it is empty. CommandRouter opens `Diag.BeginTrace(requestId)` for each command.
- **Span fields.** A request span is `info_span!("request", trace_id, request_id)`, made with `span_for_action(id, trace)`. `span_for_request(id)` uses trace = id. A plugin span is `info_span!(parent: cause, "plugin", plugin_id)`. A job keeps `cause: Span`. Log lines carry `trace_id` before `request_id`. `cabinetos_diag::current_trace()` gives the innermost trace around the caller.
- **Config field.** `logging.heavy: bool`. The environment variable `CABINETOS_LOG_HEAVY` also turns it on.
- **Heavy files and limits:**
  - name `heavy-<process>.<date>[.<part>].jsonl`;
  - a file part is at most 256 MiB;
  - the folder holds at most 2 GiB of heavy files, checked every 64 MiB written;
  - the queue holds at most 256 MiB, plus 32 MiB for threads that never wait;
  - payloads are cut at 64 KiB, and secrets are shown as `***`;
  - heavy-only lines use `heavy::*` targets and never reach the normal file;
  - files are opened without delete sharing.
- **CLI:** `log trace <ID> [--dir] [--json]`, `log tail [--process core] [--heavy] [-n 20] [--follow] [--json] [--dir]`, `log bundle [--minutes 10]`.

## Left for other agents
- Item 3, the window's heavy mode: the heavy-ui file names, masking, and no delete sharing, matching the core's rules above.
- The window part of item 4: the C# `Diag.Crash` zip and the "Open crash folder" notice.
- Item 5: docs/ui.md and the README test counts.
- Item 6: ADR 0013, then link it from diagnostics.md.
- The key-press ULID at heavy level.
- Out of scope, worth a separate fix: the plugins test `capabilities_gate_the_file_system` uses a shared %TEMP% folder and fails when agents run at the same time.

Recap: items 1, 2 and the core part of item 4 are built, tested and pushed. The last commit is 840b22a, and the timing tests are hardened. What is left: the window's half (item 3 and the window part of item 4), docs item 5 and ADR 0013.
