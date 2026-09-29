# Diagnostics

How CabinetOS processes write logs, how one user action can be followed
across processes, and what a process writes when it crashes. Constitution Article 12
(Unified, Zero-Latency Diagnostics & Logging); brief §8.

The Rust processes (core, indexer, CLI) get all of this from the
`cabinetos-diag` crate. The C# UI (Phase 5) must write the same format.

## Where the files are

| What | Where |
|---|---|
| Directory | `%LOCALAPPDATA%\CabinetOS\logs\` |
| Log files | One per process and UTC day: `core.2026-09-28.jsonl`, `indexer.<date>.jsonl`, `ui.<date>.jsonl` (Phase 5). The CLI writes `cli.<date>.jsonl` only when it is given `--log-dir`. The indexer running as a service writes to `%ProgramData%\CabinetOS\logs` instead ([indexer.md](indexer.md)). |
| Crash traces | `crash-<YYYYMMDDTHHMMSSmmmZ>.json` in the same directory, for example `crash-20260928T010203004Z.json` |

- **Other directory.** `--log-dir <path>` on `cabinetos-core` and
  `cabinetos-cli`, or the environment variable `CABINETOS_LOG_DIR`. The flag
  wins over the variable.
- **Daily files.** A new file starts at midnight UTC. The date is part of the
  file name because the rolling writer (`tracing-appender`) always names its
  files that way. Each process keeps its newest 14 daily files, today's
  included; older files of that process are deleted when it starts and at each
  daily rollover. Crash traces are never deleted.
- **Level.** `info` by default. For the core, `logging.level` in
  `cabinetos.json` sets it, and a change applies while the core runs
  ([config.md](config.md)). The environment variable `CABINETOS_LOG` wins
  over both: it sets a filter, for example `debug` or
  `info,cabinetos_ipc=trace`.
- **Terminal.** `CABINETOS_LOG_STDERR=1` also prints every event to stderr in a
  readable multi-line form. The CLI prints only warnings and errors to stderr
  unless `CABINETOS_LOG` asks for more.
- **Never blocking.** The calling thread formats the line and hands it to a
  background writer thread; it never waits for the disk. If that thread falls
  far behind, new lines are dropped rather than stalling the caller.
- **Two cores, one file.** Two windows are two cores that write the same
  `core.<date>.jsonl`. The writer opens it for appending
  (`FILE_APPEND_DATA`), so each write lands at the end of the file, and it
  writes each line in one write, so two cores' lines never mix or overwrite
  each other. Tested with two cores answering 300 requests each: every
  line is whole JSON and every request has its one line.
- **Lock-free.** The calling thread takes no lock. Besides going to the file,
  every line goes into the ring buffer of recent events: a bounded lock-free
  queue of the last 256 lines that drops its oldest line when full. Reading
  the ring (for a crash trace) moves the queued lines into a reader-side copy;
  only readers ever wait for each other, never a thread that logs.

## Log line format

One JSON object per line, with the keys always in this order:

| Key | Type | Present | Meaning |
|---|---|---|---|
| `ts` | string | always | UTC time, RFC 3339 with milliseconds: `2026-09-28T01:02:03.004Z` |
| `level` | string | always | `TRACE`, `DEBUG`, `INFO`, `WARN` or `ERROR` |
| `boundary` | string | always | The part of the system that wrote the line: `frontend`, `engine`, `plugin`, `indexer` or `ipc`. The process decides it: the core writes `engine`, the indexer `indexer`, the CLI (standing in for the UI) `frontend`. A line with a `plugin_id` is `plugin`: it was written by a plugin (its `log` calls, its stdout and stderr) or by the core on its behalf (docs/plugins.md). |
| `target` | string | always | The Rust module that logged the event, for example `cabinetos_core` |
| `message` | string | always | The event text |
| `trace_id` | string | inside an action | The ULID of the user action the line belongs to, taken from the innermost enclosing span that has a `trace_id` field ("How an action's trace id travels", below). A request without a trace is its own action, so its `trace_id` equals its `request_id` |
| `request_id` | string | inside a request | The ULID of the request being handled, taken from the innermost enclosing span that has a `request_id` field |
| `plugin_id` | string | inside a plugin call | The plugin that caused the event, same rule as `request_id`. Every line a plugin's own thread writes has it. A line the core writes about a plugin elsewhere (a restart, a reload) names it in a `plugin_id` field, which moves here. |
| `span` | string | inside a span | The name of the innermost span, for example `request` |
| `fields` | object | when there are any | The event's other fields |
| `thread` | string | always | The thread's name (`main`, `core-rt-3`), or its ID when it has no name. In the core, `core-rt-N` are the async runtime's threads, workers and blocking threads alike, `watch-<listing id>` threads watch directories, and `term-<session id>-out`, `-in` and `-exit` serve a terminal session, whose lines carry its `session_id` in `fields` (docs/terminal.md). |

Example, one line from `core.<date>.jsonl`:

```json
{"ts":"2026-09-28T00:16:18.959Z","level":"INFO","boundary":"engine","target":"cabinetos_core","message":"request handled","trace_id":"01M3JNX7ZQ2B0V3C6H8K1N4P5R","request_id":"01M3JNX80F5HE9R5F65SBDGNWS","span":"request","fields":{"elapsed_us":57,"request":"ping"},"thread":"core-rt-1"}
```

## How an action's trace id travels

One user action makes several requests: a key press becomes a command, the
command sends `start_job`, the job sends events for minutes, a plugin is
asked about the job. Each request has its own ID; the action has one trace
id, a ULID, that all of them carry. Both appear in every log line
(`trace_id`, `request_id`).

1. **The window creates the trace.** Every command run gets a ULID
   (`CommandInvocation.RequestId`); it is the run's trace. While the
   handler runs, `Diag.BeginTrace` makes it the current trace
   (`Diag.CurrentTrace`, an `AsyncLocal`, so it follows the handler across
   `await`): every line the window logs meanwhile carries it as
   `trace_id`, and every request the window sends meanwhile carries it as
   `trace` (`CoreClient` fills it in). A request sent outside a command
   carries none. The CLI uses one trace per run.
2. **The pipe carries it.** `{"id":"01M…","trace":"01M…","type":"start_job",…}`
   ([ipc.md](ipc.md), "The pipe"). Each request still has its own `id`.
3. **The core logs under it.** The core handles each request inside a span
   with `request_id` and `trace_id`; a request without a trace gets its own
   ID as its trace. Every line written while handling the request carries
   both, and so does the reply.
4. **What the request starts carries it on.** A job keeps the span it was
   started in: its threads enter it, and its events are sent inside it, so
   every job line and every `job_*` event carries the trace. A plugin call
   runs in a span under the caller's, so what the plugin logs or `emit`s
   during the call carries the trace; so does a `before-job` call on the
   job's thread. A measure's events and a `config_changed` caused by
   `set_value` carry it too. A request the core sends to the indexer for a
   search carries it on, and the indexer logs under it.
5. **Nobody's action carries none.** A directory watcher's lines and
   `listing_refreshed` events, the drive watcher, the configuration file
   watcher, a plugin's start: no trace.
6. **The trace does not depend on logging.** Spans that declare an ID are
   kept whatever the log level, so replies and events carry their trace
   even at `logging.level: "error"`.
7. **Following one action.** `cabinetos-cli log trace <id>` reads every
   `*.jsonl` file in the log folder (every process, normal and heavy files),
   keeps the lines whose `trace_id` or `request_id` is `<id>`, and prints
   them oldest first, one readable line each, boundary and process first:

   ```text
   frontend ui            2026-09-29T10:00:00.001Z INFO  cabinetos_ui::commands: command executed request_id=01M… command=files.copy [main]
   engine   core          2026-09-29T10:00:00.010Z INFO  cabinetos_jobs: job queued request_id=01M… job_id=4 [core-rt-2]
   ```

   `--json` prints the lines as they are; `--dir <folder>` reads another
   folder than the default. The same event in a process's normal file and
   its heavy file is printed once. Given a request ID instead of a trace,
   it prints that one request. `cabinetos-cli log tail [--process core]
   [--heavy] [-n 20] [--follow]` prints the newest lines of one process's
   newest file, and with `--follow` the lines added after them.

A frame the core cannot parse has no usable ID. The core then answers with a
new ID and logs the rejection under that new ID (and under the frame's
trace, when that one could be read), so the client can still find the
matching log line.

## Crash traces

When a Rust process panics, the panic hook runs before the process dies:

1. It writes `crash-<timestamp>.json` into the log directory.
2. It flushes the background log writer, so the lines logged just before the
   crash are on disk.
3. It runs the default panic handler, which prints the usual message to
   stderr. The hook also prints the path of the crash file.

The hook takes no lock it would have to wait for and writes no log events,
so a panic that happens while a lock is held cannot hang the process.

The core treats a panic on any of its threads or tasks as fatal: after the
hook has run it stops with exit code 1, whichever thread panicked (a
connection, a directory watcher, the job progress thread, a plugin's
thread). Running on would mean running without the thread that panicked
and without a log writer. The UI is expected to restart it. A job's own
thread ends the process at once instead (`abort`), so the job's disks are
never left locked. `cabinetos-core --self-test-thread-panic` runs a normal
core and panics one of its threads a second after the start.

Crash file format:

| Key | Type | Meaning |
|---|---|---|
| `boundary` | string | The boundary of the process that crashed, as in log lines |
| `process` | string | `core`, `indexer` or `cli` |
| `version` | string | The version of the crashed program, for example `0.1.0` |
| `message` | string | The panic message |
| `location` | object | `file`, `line` and `column` of the panic |
| `thread` | string | The thread that panicked |
| `backtrace` | string | The full backtrace, captured whatever `RUST_BACKTRACE` says. Release builds carry line tables, so frames name file and line as long as the `.pdb` file sits next to the `.exe` |
| `recent_events` | array | The last log lines (at most 256), oldest first, each as a JSON object in the log line format above |

To see one without a real bug:
`cabinetos-core --self-test-panic --log-dir <some directory>`. It logs
`about to panic (self-test)` and then panics with `self-test panic`.
