# Phase 15 — Heavy logging mode and trace ids

The history of this phase, moved from [PLAN.md](../PLAN.md) on 2026-09-30; the plan keeps the decisions and the done-when list.

**Trace ids, built 2026-09-30 (054a1fa, 687abd3, d60d82f).** Every
envelope on the pipe may carry `trace`, the ULID of the user action, next
to `id`; a bad value rejects the frame like a bad `id`, and the protocol
version stays, since an older peer ignores the field. The window makes one
trace per command run (`Diag.BeginTrace`; `CoreClient` fills every request
sent while the handler runs), the CLI one per run. The core logs every
line with `trace_id` and `request_id`, answers with the request's trace,
and gives it to everything the request starts: the job and its events,
the measures, the plugin calls and the events they emit, a
`config_changed` from `set_value`. A watcher's lines carry no trace, and
a request without one is its own action. The id spans are kept whatever
`logging.level` says, so the trace on the pipe does not depend on the log
level. `cabinetos-cli log trace <id>` reads every log file in the folder
and prints one action from every process in time order, once per event
even when the heavy file repeats the normal one; `log tail` prints a
process's newest lines. Tests: core +12 (the end-to-end ones in
`cabinetos-core/tests/traces.rs`), UI +4 (`TraceTests`), 654 UI tests.
Guide: [diagnostics.md](../diagnostics.md), "How an action's trace id
travels".

**Heavy mode in the core, built 2026-09-30 (3f053d3, bd36611).**
`logging.heavy` in `cabinetos.json` (or `CABINETOS_LOG_HEAVY=1`, which
wins, like `CABINETOS_LOG`) switches it within a second while the core
runs. Every event at every level, TRACE included, goes into
`heavy-<process>.<date>[.<part>].jsonl` next to the normal files: the
prefix, because the daily writer prunes every file whose name starts with
the process name; a new part every 256 MiB, so a file in use never grows
past the cap; 2 GiB in the folder across all processes, the oldest part
deleted first and named in the normal log, never a file another process
has open. The queue is counted in bytes: over 256 MiB a thread waits in
50 ms slices and the wait is logged afterwards ("heavy log waited",
with the trace); the core's async workers and its main thread, which
carry the pipe, never wait and get 32 MiB of extra room, then drop and
count. That is the exception the creator chose to Article 12, for heavy
mode only, recorded as [ADR 0013](../decisions/0013-heavy-logging-may-wait.md). Heavy-only lines carry a target
under `heavy::` and stay out of the normal file: the request and reply
payloads (masked, 64 KB per line, `truncated` when cut), one `entry done`
per piece of job work with its kind, paths, bytes, milliseconds and
outcome, every plugin host call with its arguments, and every marketplace
HTTP request's host, method, status, bytes and time. Masking covers the
secret messages' values, every field named like a secret at any depth,
and the authorization, x-api-key and cookie headers in any shape. A
listing still logs one line per listing, never one per entry. The
indexer follows the environment variable only, since it does not read the
user's config. Tests +13, among them a slow writer that makes a thread
wait without losing a line, a never-wait thread that drops and counts,
the parts and the cap with tiny limits, and the switch on and off while
the core runs. Guide: [diagnostics.md](../diagnostics.md), "Heavy mode".

**Log bundles, the core's part, built 2026-09-30 (840b22a).**
`save_log_bundle { minutes }` (1 to 1440, 10 by default) answers
`log_bundle { path }`: `bundle-<time>.zip` in the log folder with the last
minutes of every log file of every process, normal and heavy (each read
from its end, so a large file costs little), the crash traces of the last
24 hours, and `bundle.json` with the versions, the Windows build (read
from the registry by `cabinetos-fs`, since the diagnostics crate allows no
unsafe code), the `CABINETOS_*` variables and the config in effect, with
secrets masked. In heavy mode the crash hook writes the same as
`crash-<time>.zip` after the trace, on the panicking thread, without
waiting for any lock. `cabinetos-cli log bundle [--minutes 10]` prints the
path. Bundles are never deleted by CabinetOS. The heavy mode's timing
tests now poll for their condition with generous limits instead of fixed
sleeps, after one of them failed once under the load of four agents
building at the same time. Tests +8, 679 core tests. Guide:
[diagnostics.md](../diagnostics.md), "Bundles"; [ipc.md](../ipc.md), "Log
bundles". Left for the window's half: the C# crash bundle, the notice
"Open crash folder", the palette command, ADR 0013 and the window's own
heavy file.

**Heavy mode in the window, built 2026-09-30 (c6e826b, 2352d46).** The
window's `LogWriter` has a second, byte-counted queue for
`heavy-ui.<date>[.<part>].jsonl`, 64 MiB. A background thread waits above
that and writes `heavy log waited` afterwards. The UI thread, the pipe's
reader and the crash hook never wait: their lines are dropped and counted
8 MiB above the cap, and the count goes into the file and into the status
bar's `HEAVY LOG` pill ("HEAVY LOG, 1,204 lines lost"). The parts of
256 MiB, the 2 GiB cap over every process's heavy files and the
no-delete-sharing rule are the core's. `LogMask` masks payloads with the
core's rules, capped at 64 KB. The window follows `logging.heavy` at start
and on `config_changed` (`CABINETOS_LOG_HEAVY` wins). "Diagnostics: Toggle
Heavy Logging" writes the setting with `set_value`, so both processes
follow; "Open Log Folder" and "Save Log Bundle" are seeded too, without
keys (97 commands by the morning, with the rail's four). Heavy lines: every request and reply payload, every
key by name and modifiers with the element that has the keyboard (a key
typed into a text box is `text input` without the character, and AltGr
counts as typing), every command with source, trigger and arguments,
focus changes with where Windows sends the keys, the frame table every
second, and web page messages by name and size. A key press gets a ULID
that a command it starts takes as its trace, so the key, the command and
its requests are one chain. Tests: UI +68 (`HeavyLogTests`), 766 UI
tests; the core's five checks pass at 690 tests. Not yet seen in a running
window, since the keyboard belonged to another agent: the pill after a
toggle, the start notice, a key and a focus line in the heavy file, and
"Open crash folder" after a self-test crash with heavy mode on. Guide:
[ui.md](../ui.md), "Heavy logging".

**The window's crash bundle, built 2026-09-30 (566b690).** A crash of the
window while heavy mode is on writes `crash-<stamp>.zip` next to the crash
trace, in C# with the core's contents: the last 10 minutes of every log,
read from the end so a large heavy file is not read whole, the crash
traces of the last 24 hours, and `bundle.json` with the versions, the
Windows build, the `CABINETOS_*` variables and the last configuration,
secrets masked. A failed zip leaves the trace and no half zip. At the next
start the window compares the newest crash zip with its last start
(`ui.last-start` in the log folder) and shows "Open crash folder" in the
status bar until it is used. "Diagnostics: Save Log Bundle" asks the core
for a bundle of 10 minutes and opens the folder. Tests: UI +11
(`LogBundleTests`), including `save_log_bundle` against the real core.
