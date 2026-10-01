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
| Bundles | `bundle-<YYYYMMDDTHHMMSSmmmZ>.zip`, made on request, and `crash-<YYYYMMDDTHHMMSSmmmZ>.zip`, made by the panic hook (the window's crash hook too) while heavy mode is on ("Bundles", below). Never deleted by CabinetOS. |
| Last start | `ui.last-start`: the UTC time of the window's last start, its process ID and, once the window has closed, the time it did, one line each. The next start tells from it whether a crash bundle is new ("Bundles", below) and whether the last run ended without closing ("Crash traces", below). |
| Heavy log files | Only while heavy mode is on ("Heavy mode", below): `heavy-<process>.<date>.jsonl`, for example `heavy-core.2026-09-29.jsonl`, and `heavy-core.2026-09-29.1.jsonl` for the next part of the same day. At most 2 GiB for all of them together. |

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
  far behind, new lines are dropped rather than stalling the caller. Heavy
  mode is the one exception ("Heavy mode", below).
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

## Heavy mode

A switch for the times when a problem has to be found: every operation is
logged, even at the cost of speed. It stays on until someone turns it off.
Decided by the creator on 2026-09-29.

**The switch.** `logging.heavy` in `cabinetos.json` ([config.md](config.md)),
`false` by default. The core applies a change within a second, like
`logging.level`: `cabinetos-cli config set logging.heavy true` turns it on
for the core, and the window follows the core's `config_changed`. In the
window, "Diagnostics: Toggle Heavy Logging" in the palette writes the value
(`set_value`), and a `HEAVY LOG` pill in the status bar says it is on
([ui.md](ui.md), "Heavy logging"). The
environment variable `CABINETOS_LOG_HEAVY` (`1` or `0`) wins over the file
for the process it is set for; the indexer, which does not read the user's
`cabinetos.json`, is switched only by it. When heavy mode comes on, the
normal log says `heavy logging is on` (with the caps); when it goes off,
`heavy logging is off`, and that is also the heavy file's last line.

**The files.** Each process writes its own `heavy-<process>.<date>.jsonl`
next to its normal log: `heavy-core`, `heavy-indexer`, `heavy-ui`,
`heavy-cli` (the CLI only with `--log-dir`). The process name comes after
`heavy-`, not before, because the normal log's writer deletes old files by
the process name at the start of the file name. The same line format as
the normal file, with every event at every level, `TRACE` included, whatever
`logging.level` or `CABINETOS_LOG` say. A new file starts at midnight UTC,
and when a file reaches 256 MiB the next part starts
(`heavy-core.2026-09-29.1.jsonl`, `.2.`, …). Writes are append-only, one
batch per write, as in the normal file, so two cores can share one file.
Switching heavy mode off writes what is queued and closes the file.

**The cap.** After each new file and after each 64 MiB written, the core
measures every `heavy-*.jsonl` of every process in the folder, and deletes
the oldest (by date, then part) until they hold at most 2 GiB together. It
names the deleted files in its normal log (`heavy log files deleted to keep
the folder under its cap`). A heavy file is opened without delete sharing,
so the file another process is writing is never deleted; its turn comes
when that process moves on to its next file.

**The wait rule.** Normal mode never waits: a line that finds the writer
far behind is dropped. Heavy mode counts its queue in bytes. Up to 256 MiB a
thread that logs hands its line over and goes on. Above that, it waits
until the writer has written enough, so no operation goes unrecorded; the
operation is slower for that time. Afterwards the heavy file gets one line
`heavy log waited` with `waited_ms`, under the same trace and request, so a
slow operation explains itself. This is an exception to Constitution
Article 12 ("logging must never block the main I/O pipeline"), for heavy
mode only, chosen by the creator; it is recorded in ADR 0013. Threads that
must stay responsive never wait: in the core the async runtime's workers,
which read and answer the pipe (marked when they park), and the main
thread, which accepts connections; in the window the UI thread. Their lines
may go 32 MiB over the cap; beyond that they are dropped, and the next
batch carries `heavy log dropped lines of threads that never wait` with
the count. Jobs, plugin calls and every other blocking thread may wait.
The window's queue is 64 MiB, not 256 MiB (it has far less to log). Its UI
thread and the reader of its pipe never wait, and may go 8 MiB over the
cap before their lines are dropped. Every other thread of the window may
wait, and says so with the same `heavy log waited` line. The window counts
the lines it dropped since heavy mode came on and shows the count in the
status bar's pill (`HEAVY LOG, 1,204 lines lost`).

**What heavy mode records beyond the normal log.** Its own lines have
targets starting with `heavy::`, which the normal file leaves out at every
level.

| Line | Target | Fields | Written by |
|---|---|---|---|
| `request payload` | `heavy::core` | `payload`: the request as it came over the pipe; `truncated: true` when it was cut | the core, inside the request's span |
| `reply payload` | `heavy::core` | `payload`: the reply as sent; `truncated` | the core, inside the request's span |
| `entry done` | `heavy::jobs` | `job_id`, `kind` (`file`, `folder`, `rename`, `delete`, `recycle`), `from`, `to` (where the plan puts it; empty for deletes), `bytes`, `ms`, `outcome` (`done`, `skipped`, `failed`, `conflict`, `held`, …) | the job's threads, one line per piece of work, with the job's trace |
| `host call` | `heavy::plugins` | `function` (`register-command`, `log`, `config-get`, `emit`, `http-request`, `watch-folder`, `unwatch-folder`), `args` (JSON, at most 4 KB; for `http-request` the header names only, never their values, and the body's size, never the body), `truncated`, `ms` | the plugin's thread, with the caller's trace |
| `http request` | `heavy::market` | `host`, `method`, `status` (0: no answer), `bytes`, `ms`; never headers or bodies | the marketplace's index and download requests |
| `request payload`, `reply payload` | `heavy::pipe` | `payload` (masked, at most 64 KB), `truncated` | the window's pipe client, for each request it sends and each reply it reads, under the request's ID and trace; the same request is also in the core's `heavy::core` lines |
| `key pressed` | `heavy::keys` | `key` (the grammar's name: `f5`, `ctrl`, `vk_AD` for a key without one), `modifiers` (`ctrl+shift`), `element` (the type and name of what has the keyboard), `repeat` (a held key) | the window, for every key that reaches it. The line carries a ULID of its own as `trace_id`; a command the key starts takes that ULID as its trace |
| `text input` | `heavy::keys` | `modifiers`, `element` | the window, for a key that types a character while a text box has the keyboard: no key name, and never the text |
| `command run` | `heavy::commands` | `command`, `source` (`core`, `plugin:<id>`, `unlisted`), `target` (`ui` or `core`), `trigger` (`key`, `palette`, `button`, …), `args` (masked, at most 4 KB), `truncated` | the window's command router, for every run, under the run's trace |
| `command done` | `heavy::commands` | `command`, `outcome` (`RanInUi`, `CoreResult`, `Failed`, …), `elapsed_ms` | the router, when the run ended |
| `focus changed` | `heavy::focus` | `from`, `to` (each `Type name`), `keys_to` (`window`, `terminal`, `tool:<id>`, …: where Windows sends the keys) | the window, at each change of XAML's focus |
| `frame stats` | `heavy::frames` | `frames`, `worst_ms`, `gaps_over_20ms`, `gaps_over_33ms`, `busy_over_16ms`, `work_ms`, `busy_ms`, the milliseconds of each timed part, `gc_pause_ms` (the garbage collector's pauses in the second) and `gcs` (its collections as `gen0/gen1/gen2`) | the window, once a second while heavy mode is on (`CABINETOS_UI_FRAMESTATS=1` writes the same line into the normal log) |
| `slow frame` | `heavy::frames` | `gap_ms`, `work_ms`, `busy_ms`, `gc_pause_ms` and `gcs` since the frame before, the milliseconds of each timed part; after a pause also `gc_heap_mb`, `gc_promoted_mb`, `gc_generation`, `gc_concurrent` | the window, for each frame that came 33 ms or more after the one before (`CABINETOS_UI_FRAMESTATS=1` writes the same line into the normal log) |
| `page message` | `heavy::pages` | `host` (`terminal`, a tool), `direction` (`to_page`, `from_page`), `name` (the message's `type`), `bytes` | the window's web page hosts; never the content, which can hold what the user typed |

A payload is at most 64 KB; the rest is cut and the line gets
`truncated: true`. A listing writes one line per listing (`listing opened`,
`listing refreshed`), never one per entry.

**Masking.** Before a payload or a host call's arguments are written, the
secrets in them become `"***"`: the `value` and `secret` of any message
whose `type` is `secret` or starts with `secret_` (`secret_set` and the
reply `secret` to `secret_get`); every field named `secret`, `password`,
`passphrase`, `token`, `access_token`, `refresh_token`, `client_secret`,
`api_key`, `apikey`, `authorization` or `x-api-key`, at any depth; and the
values of the headers `Authorization`, `Proxy-Authorization`, `X-Api-Key`
and `Cookie` in a `headers` object, in `[name, value]` pairs, or in
`{"name", "value"}` objects. The `CABINETOS_*` environment variables whose
names contain `KEY`, `TOKEN`, `SECRET` or `PASSWORD` are masked wherever
the diagnostics write the environment. `cabinetos-diag` does all of it
(`mask_secrets`, `masked_json`). The window masks the same way with
`LogMask` (`ui/CabinetOS.Core/Diagnostics/LogMask.cs`), which has the same
field and header names and the same 64 KB cap, and is tested with the same
cases. Bytes that are not JSON are not written at all: a secret in them
could not be found. The window never writes the environment.

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
   carries none. The CLI uses one trace per run. In heavy mode a key press
   gets a ULID of its own before anything handles it, and a command the
   key starts takes that ULID as its trace (`ExecuteAsync`'s `traceId`), so
   the key press, the command and the command's requests are one chain; a
   key that becomes no command is a chain of one line. A key that types
   into a text box has no trace: it is not an action.
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

### A run that ended without closing

A native end of the window, such as a WinUI fail-fast (`0xc000027b` in
`CoreMessagingXP.dll`, which Windows' Application log records), runs none of
the window's crash hooks: there is no crash trace and the log just stops. So
the window notes its clean end, and the next start says when it is missing.
The marker `ui.last-start` ("Where the files are") has three lines: the
start's UTC time, `pid <process ID>`, and `closed <UTC time>`, which the
window writes when `Application.Start` has returned, as its last act before
`exited`. At the next start, a marker that has a pid and no `closed` line
means the last run did not close, unless that process is still running (a
second window; the process is told from a later process with the same ID by its
start time). The window then writes one WARN line, `previous run ended
without closing`, with `started_utc` as its field, and shows nothing on
screen: it is for the next look at the log, not a question for the user.
A marker an older window wrote has only the time and says nothing. A
window's close is written only over its own marker, so two windows at once
keep to the one that started last; if the other one ended without closing,
that is not noticed.

## Bundles

A bundle is one zip with what someone needs to find a problem: the last
minutes of every process's logs, the recent crash traces, and facts about
the machine. It is written into the log folder and never sent anywhere.

**Made on request.** The core's request `save_log_bundle` (`minutes`, 1 to
1,440, default 10) writes `bundle-<YYYYMMDDTHHMMSSmmmZ>.zip` and answers
`log_bundle` with its path ([ipc.md](ipc.md), "Log bundles").
`cabinetos-cli log bundle [--minutes 10]` prints the path.

**Made by a crash in heavy mode.** When a Rust process panics while heavy
mode is on, the panic hook writes the crash trace, flushes both log
writers, and then writes `crash-<YYYYMMDDTHHMMSSmmmZ>.zip` with the last 10
minutes. It works from the files on disk, on the panicking thread, and
waits for no lock: if another thread holds the configuration the bundle
leaves it out. If the zip fails, the crash trace is there anyway; stderr
says which was written. `CABINETOS_LOG_HEAVY=1 cabinetos-core
--self-test-panic --log-dir <folder>` shows one.

**Made by a crash of the window in heavy mode.** The window does the same
in C# (`LogBundle` and `LogWriter.WriteCrashBundle` in
`ui/CabinetOS.Core/Diagnostics`, with `System.IO.Compression`). After
`Diag.Crash` has written the crash trace and flushed the log, and while heavy
mode is on, the crashing thread writes `crash-<stamp of the trace>.zip` from
the files on disk, so the trace and the zip belong together by name. Its
`bundle.json` says `process: "ui"`, and has the versions (the window's, and
the protocol version the core spoke at `welcome`), the Windows build, the
`CABINETOS_*` variables and the configuration the core last sent, secrets
masked as in heavy mode. If anything fails, the trace is there anyway, and
half a zip is deleted so that it is not offered later.

**The offer at the next start.** At every start the window reads the time of
its last start from `ui.last-start`, a small file in the log folder, and
writes the new time (and its process ID: "A run that ended without closing",
above). If a `crash-*.zip` there is newer than the last start
(the core's zips count too), the status bar shows "Open crash folder" until it
is used, and the notice line says why; the core opens the folder with
`open_path`. When no last start is recorded, a zip of the last 24 hours counts.

**What is in it.**

| Entry | Contents |
|---|---|
| `<name>.jsonl` | Every log file of the folder (every process, normal and heavy files) cut to the lines stamped within the last `minutes`, under its own name. A file with no such line is left out. Each file is read from its end, a megabyte at a time, until a piece holds only older lines, so a large heavy file is not read whole. |
| `crash-*.json` | Every crash trace changed in the last 24 hours |
| `bundle.json` | `created`, `reason` (`asked` or `crash`), `process` (the process that wrote it), `minutes`, `since` (the oldest time a line may have), `versions` (`cabinetos`, `protocol`), `windows_build` (such as `10.0.26200.6899 (25H2)`, from the registry), `environment` (every `CABINETOS_*` variable, those that look like keys as `"***"`), `config` (the configuration in effect, secrets masked as in heavy mode), and `files` (each entry's `name`, `lines` and `bytes`) |

Zips are written with Deflate at its fastest level: a crash bundle is
written while the process goes down.
