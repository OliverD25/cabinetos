## Item 2, heavy mode in the core (cabinetos-diag)

Agent: a8ef741452d0b3317

Report: Item 2, heavy mode in the core (cabinetos-diag)

Context: CabinetOS Phase 15, diagnostics coder agent. Item 2 is done and pushed. Next I do the core part of item 4 (save_log_bundle, the crash zip, `cabinetos-cli log bundle`), then I stop.

COMMITS (on origin/main)
- 3f053d3 diagnostics: heavy logging mode in the core, a switch that records every operation
- bd36611 diagnostics: heavy mode masks the `secret` reply as well as `secret_*` requests. The core agent's protocol 13 secrets landed during my work. The reply to secret_get has the type `secret`, which the `secret_*` rule missed.

WHAT WAS BUILT, AS BUILT
Config: `LoggingConfig.heavy: bool`, default false. JSON key `logging.heavy`. sdk/config/cabinetos.schema.json regenerated.
- The core applies it at start and on every change, within a second (settings.rs `apply_heavy` calls `cabinetos_diag::set_heavy`).
- The environment variable CABINETOS_LOG_HEAVY=1 or 0 wins over the file, as CABINETOS_LOG does.
- The handout's chat named the key `diagnostics.level`. It lives in the existing `logging` section, as you asked.

cabinetos-diag public API (new):
- `set_heavy(on) -> bool`. It returns false before init, in a process without log files, or when CABINETOS_LOG_HEAVY decided.
- `heavy_enabled() -> bool`.
- `never_wait_for_heavy_log()`: marks the calling thread.
- Constants:
- HEAVY_QUEUE_CAP = 256 MiB
- HEAVY_NEVER_WAIT_EXTRA = 32 MiB
- HEAVY_FILE_BYTES = 256 MiB (the size of one part)
- HEAVY_DISK_CAP = 2 GiB
- HEAVY_CAP_CHECK_BYTES = 64 MiB
- HEAVY_FILE_PREFIX = "heavy-"
- HEAVY_TARGET_PREFIX = "heavy::"
- LOG_HEAVY_ENV = "CABINETOS_LOG_HEAVY"
- Masking: MASK = "***", PAYLOAD_CAP = 64 KiB, `mask_secrets(&mut Value)`, `masked_json(&Value, cap) -> (String, truncated)`, `masked_json_bytes(&[u8], cap)`, `cap_text`, `is_secret_env(name)`.

Files:
- The name is `heavy-<process>.<date>.jsonl`, for example heavy-core.2026-09-30.jsonl.
- When a file reaches 256 MiB, the next part starts: heavy-core.2026-09-30.1.jsonl, .2, and so on.
- The line format is the same as the normal file, with every level, TRACE included. The CABINETOS_LOG filter does not limit it.
- Writes are append-only, one batch of at most 1 MiB per write.
- The file is opened WITHOUT delete sharing (share_mode READ|WRITE).
- The writer thread is "cabinetos-heavy-log". It starts at the first switch-on.
- Switching off does three things in order: it logs "heavy logging is off" (the last heavy line), then flushes, then closes the file.
- Switching on logs "heavy logging is on" (with dir, queue_cap_bytes and disk_cap_bytes) in both files.

Queue and wait rule:
- The queue is counted in bytes.
- A thread that may wait waits (50 ms slices) while the queue holds more than 256 MiB. Afterwards the heavy file gets a WARN line, target cabinetos_diag::heavy, message "heavy log waited", field waited_ms, with the same trace and request ids.
- A thread marked never-wait never waits. Its lines may go up to 32 MiB over the cap. Beyond that they are dropped and counted. The next batch carries "heavy log dropped lines of threads that never wait" with field `dropped`.
- In the core, these threads are marked never-wait:
- the tokio worker threads, through `on_thread_park` and `on_thread_unpark` in main.rs (only workers park; blocking threads may wait);
- the main thread, which runs the pipe server (in `run()`).
- The IPC reader is a task on the workers, so it is covered.

Disk cap:
- It runs at every file open (a new day, a new part, or switch-on) and after every 64 MiB written.
- It measures every heavy-*.jsonl in the folder, for all processes, and deletes the oldest by (date, part) until the total is at most 2 GiB.
- It never deletes this process's current file. A file another process has open cannot be deleted, so the cap skips it.
- The normal log gets an INFO line "heavy log files deleted to keep the folder under its cap", with fields deleted, freed_bytes and cap_bytes.

Heavy-only lines (DEBUG, target `heavy::…`). A filter keeps them out of the normal file at every level:
- `request payload` and `reply payload`, target heavy::core. Field `payload` (masked JSON text, at most 64 KB), plus `truncated: true` when cut. Written inside the request span, so they carry trace_id and request_id. Code: connection.rs `log_payload`, called in handle_frame and in Outbox::reply.
- `entry done`, target heavy::jobs, one per piece of job work (jobs run.rs `entry_done`). Fields: job_id, kind (file, folder, rename, delete, recycle), from, to (the planned destination, empty for deletes), bytes, ms, outcome (done, skipped, failed, conflict, held, expanded, deferred, cancelled).
- `host call`, target heavy::plugins, for register-command, log, config-get and emit (plugins worker.rs `HostCall` drop guard). Fields: function, args (masked JSON, at most 4 KB), truncated, ms.
- `http request`, target heavy::market, for the index fetch and install downloads (market lib.rs `http_line`). Fields: host, method, status (0 when no answer came), bytes, ms. Never headers or bodies.
- Listings still log one line per listing or refresh, never one per entry.

Masking (mask.rs), in payloads and host call args:
- `value` of a message whose type is `secret` or starts with `secret_`;
- any field named secret, password, passphrase, api_key, apikey, token, access_token, refresh_token, client_secret, authorization or x-api-key, at any depth;
- the values of the headers authorization, proxy-authorization, x-api-key and cookie, in a `headers` object, in [name, value] pairs, or in {name, value} objects;
- `is_secret_env`: CABINETOS_* variables whose names contain KEY, TOKEN, SECRET or PASSWORD (for the bundle in item 4).

Indexer: gets the same heavy layer through init. It is switched only by CABINETOS_LOG_HEAVY.

Structure changes:
- format.rs split into `Around` (the ids and span name of the scope), `render_event` and `render_note`.
- The heavy layer uses its own filter, `HeavyFilter`. It enables everything while on, and always enables spans that declare ids, so a span made before the switch still gives its ids.
- New dependencies: crossbeam-channel in diag; cabinetos-diag in cabinetos-plugins.

Docs written (the next agent should not rewrite these):
- docs/diagnostics.md: a new section "Heavy mode" (switch, files, cap, wait rule with the Article 12 exception naming ADR 0013 without a link, what is recorded as a table, masking), a "Heavy log files" row in the file table, and a note in the "Never blocking" bullet.
- docs/config.md: `logging.heavy` in the example, the table row, and "Who uses what".
- The docs already say "the window follows the core's config_changed". Item 3 must make that true.

CHECKS (verbatim results)
- Core, after rebasing onto the core agent's protocol 13 commits b6bd317 and 942b82e: cargo build ok; cargo test --workspace "passed 664 failed 0"; clippy -D warnings clean; fmt --check clean; cargo deny "advisories ok, bans ok, licenses ok, sources ok".
- My delta is +13 tests:
- mask: a_secret_request_s_value_is_masked, fields_named_like_secrets_are_masked_at_any_depth, secret_headers_are_masked_in_every_shape, long_payloads_are_cut_at_a_character_boundary, cabinetos_variables_that_look_like_keys_are_secrets
- heavy: a_thread_waits_for_a_slow_writer_and_no_line_is_lost (a 2 KiB cap and a slow writer; it shows the wait and all 400 lines in order), a_thread_that_never_waits_drops_and_the_count_is_written, a_full_file_continues_in_the_next_part, the_oldest_heavy_files_go_first_and_the_open_one_stays (a tiny cap)
- lib: heavy_mode_from_the_environment_is_one_or_zero
- clock: formats_the_file_date
- diag tests/heavy.rs: heavy_mode_switches_while_the_process_runs (TRACE reaches the heavy file at level WARN; heavy-only lines stay out of the normal file; the file is closed after the switch and reopened on the next switch)
- core tests/heavy.rs: heavy_mode_records_payloads_and_job_entries_while_it_is_on (set_value logging.heavy on and off within a second; the request payload is masked with its trace; the reply payload is there; 2 entry done lines with the trace; the file is closed after the switch)
- UI: dotnet build -warnaserror "Build succeeded." with 0 warnings. With CABINETOS_UI_E2E=1 and my fresh core: "total: 654 failed: 1 succeeded: 653".
- The one failure is NOT from my work: ProtocolTests.Every_event_type_of_the_schema_is_known_as_an_event reports `Expected: "preview_applied"`. Commit b6bd317 (core agent, protocol 13 previews) added the event `preview_applied` to sdk/protocol/event.schema.json, but not to C# MessageCodec.EventTypes (ui/CabinetOS.Core/Protocol/MessageCodec.cs). The shell or core agent should add it.

DECISIONS (what — because — undo)
- Files are named heavy-<process>.<date>[.<part>].jsonl — because tracing-appender deletes every file whose name starts with the process name, so `core-heavy` would count against the 14 normal files — undo: HEAVY_FILE_PREFIX and heavy_file_name in heavy.rs.
- A new part starts at 256 MiB — because with only daily rollover, today's in-use file could grow past 2 GiB, and a file in use cannot be deleted — undo: set HEAVY_FILE_BYTES to u64::MAX.
- Heavy files are opened without delete sharing — because then no process's cap deletes a file another process is writing — undo: drop share_mode in HeavyFiles::open. The window's heavy writer should use FileShare.ReadWrite without Delete, to match.
- The cap is also checked when a file opens at switch-on, not only at rollover — because old heavy files from earlier days may already be over the cap — undo: call enforce_cap only on rollover.
- Heavy-only lines use the target `heavy::<crate>` at DEBUG and are filtered out of the normal file — because at logging.level trace the normal file would otherwise get 64 KB payloads with a 14-day life and no cap — undo: remove the filter_fn in init.
- Never-wait in the core = tokio workers (park hooks) and the main thread — because the pipe reader, writer and session loop are tasks on the workers; blocking threads, job threads and plugin threads may wait — undo: remove the hooks in main.rs and the call in run().
- The handout's "small side queue" became 32 MiB of extra room, then drop-and-count — because a line of a thread that cannot wait must go somewhere bounded — undo: HEAVY_NEVER_WAIT_EXTRA.
- "heavy log waited" goes only to the heavy file — because tracing cannot log from inside a layer — undo: none possible.
- The indexer follows CABINETOS_LOG_HEAVY only — because it runs elevated or as a service and does not read the user's cabinetos.json — undo: add an indexer request later.
- CABINETOS_LOG_HEAVY wins over logging.heavy — because it matches CABINETOS_LOG — undo: remove the HEAVY_FROM_ENV check in set_heavy.
- The masking list is wider than the handout's (password, token, api_key, cookie, …), and the reply type `secret` is masked — because it is safer — undo: shorten SECRET_FIELDS and SECRET_HEADERS.
- `entry done` is logged after the first attempt of each piece of work, and `to` is the planned destination — because the real destination is decided deep inside transfer(); automatic conflict retries inside settle() get no line of their own — undo: log from transfer().
- Not built yet: the plugin host's `http-request` line. That function is not on main yet (core agent). When it lands, add `let _call = HostCall::start("http-request", || json!({…, "headers": …}));` (masking covers the headers) and a `tracing::debug!(target: "heavy::plugins", host, method, status, bytes, ms, "http request")`.

READY PLAN TEXT (docs/PLAN.md, Phase 15)
"Item 2, heavy mode in the core — done 2026-09-30 (3f053d3, bd36611). `logging.heavy` (or CABINETOS_LOG_HEAVY) writes every event at every level into heavy-<process>.<date>[.<part>].jsonl, 256 MiB per part, 2 GiB in the folder, oldest deleted first and named in the normal log. The queue is counted in bytes. Over 256 MiB a thread waits and the wait is logged. The core's async workers and main thread never wait (32 MiB extra, then drop-and-count): the Article 12 exception, ADR 0013. Heavy-only lines: request and reply payloads (masked, 64 KB), one `entry done` per job entry, plugin host calls, marketplace HTTP requests. Tests +13 (core e2e: cabinetos-core/tests/heavy.rs)."

WHAT IS LEFT FROM ITEM 2
- The plugin host's http-request heavy line (waits for the core agent's host function).
- The window half of heavy mode (item 3, another agent). Everything the window must mirror is listed above: file name heavy-ui.<date>[.<part>].jsonl, byte-counted queue, disk cap across all heavy-*.jsonl, no delete sharing, masking rules, the "heavy::" target convention, and the "heavy logging is on/off" lines.
