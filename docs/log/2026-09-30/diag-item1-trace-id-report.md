## Item 1, the trace id (protocol envelope, core, jobs, plugins, CLI, window)

Agent: a8ef741452d0b3317

Report: Item 1, the trace id (protocol envelope, core, jobs, plugins, CLI, window)

Context: CabinetOS Phase 15, diagnostics coder agent. Item 1 is done and pushed to origin/main. I go on with item 2 (heavy sink in the core), then the core part of item 4, as you asked.

COMMITS (all on origin/main)
- 054a1fa protocol: an optional `trace` next to `id` in every envelope (pushed first; the core agent's protocol 13 commit 46c644c is on top of it)
- 687abd3 diagnostics: one trace id per user action, through the core, its jobs and plugin calls
- d60d82f ui, docs: a command run is one action; its requests carry its trace

WHAT WAS BUILT
Protocol (core/crates/cabinetos-protocol/src/message.rs, envelope only):
- `Envelope<T> { id: RequestId, trace: Option<RequestId>, body }`, serde `default` + `skip_serializing_if = none`. Wire: {"id":"…","trace":"…","type":"ping"}.
- `Envelope::new(id, body)` = no trace; new `Envelope::traced(id, trace, body)`; `envelope.trace_or_id()`.
- A `trace` that is not a ULID makes the envelope invalid (same rule as `id`).
- sdk/protocol/*.schema.json regenerated (all five envelopes gained optional `trace`).
- PROTOCOL_VERSION not raised (optional field, older peers ignore it).

cabinetos-diag:
- Every log line has a new key `trace_id`. Key order now: ts, level, boundary, target, message, trace_id, request_id, plugin_id, span, fields, thread.
- `span_for_action(id, trace)` = info_span "request" with fields trace_id and request_id. `span_for_request(id)` still exists and means trace = id.
- `current_trace() -> Option<RequestId>`: the trace_id of the innermost span around the caller (reads the Registry through the global dispatcher). Returns None outside traced spans and before init.
- `log_dir(explicit: Option<PathBuf>) -> PathBuf`: explicit, else CABINETOS_LOG_DIR, else %LOCALAPPDATA%\CabinetOS\logs.
- init() now uses per-layer filters. The SpanIds layer keeps every span that declares trace_id, request_id or plugin_id at any log level (`is_id_span`). File, stderr and ring layers share one reloadable level filter. Reason: the trace on the pipe must not depend on logging.level. `set_level` works as before.

Core (cabinetos-core):
- handle_frame builds the span from `envelope.trace_or_id()`.
- `Outbox::reply` sends `Envelope::traced(id, current_trace(), …)`, so every reply carries the request's trace. An untraced request gets its own id back as `trace`.
- New `Outbox::event(event)`: measure_progress and measure_finished carry the trace.
- `EventHub::publish` takes the trace from `current_trace()`. So job events, plugin_event, and a config_changed caused by set_value carry the trace.
- A rejection is logged under the frame's trace when the core can read it (`Rejection.trace`).
- The list_directory reply keeps the trace (TaskDone::ListingReady carries it).
- The watched listing's refresh span is now a root span (`parent: None`), so a watcher's lines carry no trace.
- search.rs sends the trace to the indexer, and the indexer's span uses it.

Jobs (cabinetos-jobs):
- `Job.cause: tracing::Span`, taken from `Span::current()` in Job::new (the start_job request span).
- The job thread and each worker thread enter it.
- `Engine::emit(job, event)` and `progress::publish` send inside `job.cause`.
- Result: every job line and every job_* event carries the trace. Job lines also carry the start_job request_id.

Plugins (cabinetos-plugins):
- `Call::Command`, `BeforeJob` and `Listing` gained `cause: tracing::Span` (the caller's current span).
- The worker runs each call in `info_span!(parent: cause, "plugin", plugin_id)`, so the plugin's log, stdout and emit during a call carry the trace.
- The plugin's start lines carry none.
- `#[allow(clippy::too_many_lines)]` on worker::run: the function is at the lint limit.

IPC client and CLI:
- `PipeClient::set_trace(Option<RequestId>)` and `trace()`. Requests carry it, and received envelopes keep their trace.
- The CLI sets one trace per run.
- New module core/crates/cabinetos-cli/src/logs.rs. It needs no core.
- `cabinetos-cli log trace <ID> [--dir PATH] [--json]`
- `cabinetos-cli log tail [--process core] [--heavy] [-n 20] [--follow] [--json] [--dir PATH]`
- `log trace` reads every *.jsonl in the folder. It keeps the lines whose trace_id or request_id equals ID (any case) and sorts them by ts.
- It prints them readable: `boundary(8) label(13) ts LEVEL target: message request_id=… plugin_id=… fields… [thread]`. The label is the file-name part before the first dot: core, ui, heavy-core.
- The same event in a process's normal and heavy file (same key, ts within 10 ms) is printed once.
- `log tail` picks the newest file of the label by date, then by part number (`heavy-core.2026-09-29.3.jsonl`). It reads from the end of the file.

Window (ui/CabinetOS.Core), how it sets the trace:
- `CoreRequest.Trace` (string?, JSON `trace`, written between id and type; null is left out).
- `Diag.CurrentTrace` (an AsyncLocal) and `Diag.BeginTrace(traceId)`, which returns an IDisposable that restores the previous trace.
- `CommandRouter.ExecuteAsync` wraps the whole run in `Diag.BeginTrace(requestId)`. `CommandInvocation.RequestId` is the trace.
- `CoreClient.RequestAsync` does `request.Trace ??= Diag.CurrentTrace`.
- Reply lines use the pending request's trace (`Diag.Traced(level, traceId, requestId, target, message, fields)`). Event lines use the event's `trace`.
- `Diag.Log(…, traceId = null)` defaults to CurrentTrace. `LogWriter.Write(…, traceId = null)`.
- `LogLine.Format` has a new 9-argument overload with traceId before requestId. The 8-argument one still works.
- `IncomingMessage.Trace` (init property) and `MessageCodec.Peek(frame) -> (Id, Trace, Type)`. PeekIdAndType is kept.

Docs written: docs/diagnostics.md, section "How an action's trace id travels" (replaces "How a request ID travels"), plus the `trace_id` row in the line format table and the example line. docs/ipc.md, "The pipe": the `trace` field. The next agent does not need to write these.

CHECKS (verbatim results)
- Core, after the last rebase: cargo build ok; cargo test --workspace "passed 630 failed 0" (summed over all test binaries); cargo clippy --workspace --all-targets -D warnings clean; cargo fmt --check clean; cargo deny check "advisories ok, bans ok, licenses ok, sources ok".
- My Rust delta is +12 tests:
- protocol: the_trace_sits_next_to_the_id_and_may_be_left_out
- diag format: a_request_span_carries_its_trace_and_what_it_starts_inherits_it, current_trace_is_the_innermost_trace_around_the_caller, only_spans_that_declare_an_id_are_id_spans (plus trace assertions added to tests/log_file.rs and tests/set_level.rs; the second checks that the trace survives at WARN level)
- cli unit: millis_counts_from_1970, stamps_order_by_date_then_part, a_readable_line_starts_with_boundary_and_process
- cli tests/logs.rs: log_trace_prints_one_action_from_every_process_in_time_order, log_trace_says_so_when_nothing_matches_or_the_id_is_not_a_ulid, log_tail_prints_the_newest_lines_of_the_newest_file
- core tests/traces.rs (new file): a_job_carries_the_trace_of_the_request_that_started_it, a_plugin_call_and_the_event_it_emits_carry_the_trace
- UI: dotnet build ui/CabinetOS.sln -c Debug -warnaserror → "Build succeeded." with 0 warnings. CABINETOS_UI_E2E=1 with CABINETOS_CORE_EXE = my fresh debug core: "total: 654 failed: 0 succeeded: 654 skipped: 0". My delta is +4, in the new file ui/CabinetOS.Tests/TraceTests.cs: Every_request_a_command_s_handler_sends_carries_the_command_s_trace, A_trace_scope_nests_and_gives_the_previous_trace_back, A_line_s_trace_comes_before_its_request_id, An_event_s_trace_is_read_and_a_request_s_is_written_next_to_its_id.

DECISIONS (what — because — undo)
- `trace` is Option<RequestId> and a bad value rejects the frame — because it is the same rule as `id` — undo: make it Option<String> and ignore bad values.
- PROTOCOL_VERSION not raised — because the field is optional and ignored by older peers — undo: add `trace` to the version 13 note in protocol lib.rs.
- `trace_id` comes before `request_id` in the line — because the trace is the wider scope — undo: swap the two fields in format.rs `Line` and in LogLine.cs.
- A reply to an untraced request carries trace = its id — because the core treats that request as its own action — undo: have Outbox::reply pass the envelope's own trace instead of current_trace().
- The trace reaches replies and events through spans (current_trace), not new function parameters — because connection.rs helper signatures stay as they are, where the core agent works — undo: pass the trace explicitly.
- cabinetos-diag uses per-layer filters, and id spans are always created — because otherwise at logging.level warn the request span is disabled and the pipe loses the trace — undo: restore the global reload filter in init().
- The refresh span of a watched listing is a root span; its lines lose the list_directory request_id they had before — because the handout says a watcher's lines carry no trace — undo: drop `parent: None` in connection.rs listing_ready.
- Job lines carry the start_job request_id as well as trace_id — because the job thread enters the request span itself — undo: enter a span that carries only trace_id.
- A plugin's start and activate lines carry no trace, even when a request caused the restart — because the plugin thread's own span is a root span — undo: pass the cause span into worker::run.
- `log trace` prints an event once when it is in both the normal and heavy file (key match, ts within 10 ms) — because the heavy file repeats every normal line — undo: remove `without_twins` in logs.rs.
- `log trace` refuses an ID that is not a ULID — because it catches typos early — undo: remove the parse check in main.rs log_command.
- Not done here: "a key press that becomes no command gets its own ULID at heavy level" — because it is heavy-mode window logging, and you moved the window's heavy half (item 3) to another agent.

READY PLAN TEXT (for docs/PLAN.md, Phase 15)
"Item 1, trace ids — done 2026-09-30 (054a1fa, 687abd3, d60d82f). Every envelope may carry `trace` (the user action's ULID). The window makes one per command run (Diag.BeginTrace, CoreClient fills requests). The CLI makes one per run. The core logs every line with trace_id and request_id, sends replies and the events of what a request started with its trace (jobs, measures, plugin calls and their emits, config_changed from set_value), and sends none for watchers. `cabinetos-cli log trace <id>` prints one action from every process's files in time order; `log tail` prints a process's newest lines. Tests: core +12 (e2e in cabinetos-core/tests/traces.rs), UI +4 (TraceTests)."

WHAT IS LEFT FROM ITEM 1
- The heavy-level key press ULID in the window: belongs with item 3.
- The docs of `log tail --heavy` refer to heavy file names. Item 2 defines them: `heavy-<process>.<date>[.<part>].jsonl`. The name does not start with the process name, because tracing-appender prunes every file whose name starts with the process name.
