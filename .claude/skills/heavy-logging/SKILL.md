---
name: heavy-logging
description: When and how to use CabinetOS's heavy logging mode (the setting logging.heavy) to find a fault the normal log does not show, and how to read what it records. Use it when a key did nothing or the wrong thing, a command ran twice or not at all, a job did not do what was asked, a plugin or the Agent extension acted in a way nobody expected, the window stalled or dropped frames, a process crashed, a request's or reply's content is in question, or a live check or end-to-end test needs evidence beyond the normal log. Also when someone says "heavy logging", "heavy mode", "trace id", "log trace", "log bundle", "why did that happen", "lost key", "crash zip". Turn it on, do the work, read the chain of one action with cabinetos-cli log trace, take a bundle if the evidence must be kept, turn it off.
---

# Heavy logging: find a fault the normal log does not show

CabinetOS writes one normal log per process and day. Heavy mode is a
switch that makes every process also write every operation, at every
level, into a second file, even when that slows an operation down. It
exists for the times when a problem has to be found, and it stays on
until someone turns it off. The creator decided it on 2026-09-29 (plan
Phase 15). The one place it may slow the core is the exception to
Constitution Article 12 that ADR 0013 records: in heavy mode, and only
there, a thread of the core may wait for the log writer so that no
operation goes unrecorded. The window's UI thread and the pipe never wait.

The full description is in
[docs/diagnostics.md](../../../docs/diagnostics.md), sections "Heavy
mode", "How an action's trace id travels" and "Bundles", and in
[docs/ui.md](../../../docs/ui.md), "Heavy logging". This skill is the
short form for an agent that has a fault to find.

## 1. When to use it, and when not

Use it when the normal log cannot answer the question:

- **A key did nothing, or the wrong thing.** Heavy mode logs every key
  that reaches the window, what had the keyboard, and the command it
  started, as one chain.
- **A command ran twice, or not at all.** Every command run and its end
  are logged with their trigger (key, palette, button, menu).
- **A job did not do what was asked.** One line per file, folder, rename
  or delete, with the outcome, the bytes and the time.
- **A plugin or the Agent extension did something unexpected.** Every
  host call of a plugin is logged with its arguments; every network
  request with its host, status and size (never headers or bodies).
- **The window stalled or dropped frames.** A frame table every second
  and one line per slow frame (33 ms or more), with the garbage
  collector's pauses.
- **A crash must be caught.** A crash in heavy mode writes a zip of the
  last 10 minutes of every log by itself.
- **A request's or reply's content is in question.** The core and the
  window both log every payload on the pipe (secrets masked, 64 KB cap).
- **A live check or end-to-end test needs evidence** that the normal log
  does not hold (the window's normal log has no key lines).

Do not use it:

- As a default in a test fixture, a handout, or a committed
  `cabinetos.json`. It is an investigation tool, on for one investigation.
- To measure the speed of normal mode. Heavy mode costs time and disk;
  measure with it off.
- As a substitute for a test. Once the fault is found, a test proves the
  fix; the heavy log only found it.

## 2. Turn it on and off

Three switches. The file is the one to prefer; the others exist for a
process that reads no file, or for a hand at the keyboard.

- **The setting.** `logging.heavy` in `cabinetos.json`, `false` by
  default. The core applies a change within a second; the window follows
  the core's `config_changed`. From a terminal, with a core running:

  ```bash
  cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && ./core/target/release/cabinetos-cli.exe config set logging.heavy true
  ```

  The same with `false` turns it off. The normal log then says `heavy
  logging is on` (with the caps) or `heavy logging is off`, and the heavy
  file's last line is that `off` line.
- **The palette.** "Diagnostics: Toggle Heavy Logging"
  (`diagnostics.toggleHeavy`, no default key). It writes the opposite
  value into the file; both processes follow. A `HEAVY LOG` pill in the
  status bar says the mode is on, and counts the lines the window had to
  drop (`HEAVY LOG, 1,204 lines lost`).
- **The environment variable.** `CABINETOS_LOG_HEAVY=1` (or `0`) for one
  process, set before it starts. It wins over the file for that process
  and cannot be turned off from the pill. It is the only switch for the
  indexer, which reads no `cabinetos.json`, and the right switch for a
  process a test or a script starts: set it in that process's environment
  together with `CABINETOS_LOG_DIR`, so the heavy file lands in the run's
  own folder.

`CABINETOS_LOG=debug` and `logging.level` are a different switch: they
set the normal log's level. The Claude Code probe uses that one. Heavy
mode writes every level regardless of them.

## 3. Where the files are

The log folder is `%LOCALAPPDATA%\CabinetOS\logs\`, or `CABINETOS_LOG_DIR`
when set (every test harness and the live check set it per run). In it:

| File | Written by |
|---|---|
| `heavy-core.<date>.jsonl` | the core |
| `heavy-ui.<date>.jsonl` | the window |
| `heavy-indexer.<date>.jsonl` | the indexer (only with `CABINETOS_LOG_HEAVY=1`) |
| `heavy-cli.<date>.jsonl` | the command line (only with `--log-dir`) |

Dates are UTC; a new file starts at midnight, and a new part
(`.1.jsonl`, `.2.jsonl`) when a file reaches 256 MiB. The core keeps all
heavy files together under 2 GiB by deleting the oldest, and says which
in its normal log. The same line format as the normal file: JSON Lines,
one object per line, with `trace_id` and `request_id` on every line that
belongs to an action.

## 4. Read what it recorded

**Find the action's trace id.** Every user action has one ULID (a
sortable unique id) that every line of every process carries. In heavy
mode a key press gets one before anything handles it. The quickest ways:

- the newest key lines of the window:

  ```bash
  cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && ./core/target/release/cabinetos-cli.exe log tail --process ui --heavy -n 40
  ```

  Add `--follow` to keep printing while you press the key; add
  `--dir <folder>` for a run's own log folder; `--json` prints the lines
  as they are in the file, with the ids.
- a line you already have (a warning, a notice, a request id): its
  `trace_id`, or its `request_id` when the trace is missing.

**Print the chain.** One command prints every line of that action from
every process, normal and heavy files, oldest first:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && ./core/target/release/cabinetos-cli.exe log trace 01K6ABCDEFGHJKMNPQRSTVWXYZ
```

A request id works too; `--dir <folder>` reads another log folder. Read
it top to bottom: the key (`key pressed`), the command (`command run`),
the requests the window sent (`request payload` from the window's pipe
client, then the same request in the core), what the core did (job
entries, plugin calls, network requests), the reply, and `command done`
with the elapsed time.

**What the heavy lines are.** The message and the `target` say the kind:

| Message | Target | What it tells |
|---|---|---|
| `key pressed`, `text input` | `heavy::keys` | the key's name and modifiers, what had the keyboard, whether it was a held key; a key typed into a text box has no name and no text, ever |
| `command run`, `command done` | `heavy::commands` | the command, where it came from, its trigger and arguments; then its outcome and elapsed time |
| `focus changed` | `heavy::focus` | which element had the keyboard before and after, and where Windows sends the keys (window, terminal, a tool page) |
| `request payload`, `reply payload` | `heavy::pipe` (window), `heavy::core` (core) | the message as sent or received, secrets masked, at most 64 KB (`truncated: true` when cut) |
| `entry done` | `heavy::jobs` | one piece of a job: kind, from, to, bytes, milliseconds, outcome |
| `host call` | `heavy::plugins` | a plugin's call into the core: the function, the arguments (4 KB), the time |
| `http request` | `heavy::market` | host, method, status, bytes, time; never headers or bodies |
| `frame stats`, `slow frame` | `heavy::frames` | the frame table of the last second; each frame 33 ms or more after the one before, with the garbage collector's share |
| `page message` | `heavy::pages` | a message to or from a web page host (the terminal, a tool): its name and size, never its content |
| `heavy log waited` | any | a thread of the core (or a non-UI thread of the window) waited for the writer: `waited_ms` explains a slow operation |
| `heavy log dropped lines of threads that never wait` | any | the UI thread or the pipe had to drop lines; the count is in the line and in the pill |

**Secrets and typed text.** Secrets in payloads are masked as `"***"`
(passwords, tokens, keys, the `Authorization` and `Cookie` headers, the
`secret_*` messages). Typed text is never written: a key that types a
character is logged as `text input` without the character. Say so when
someone asks whether a heavy file is safe to read; it still holds paths
and names, so it is not something to paste into a public place.

## 5. Keep the evidence: a bundle

A bundle is one zip in the log folder with the last minutes of every
log, the recent crash traces, and facts about the machine (versions, the
Windows build, the `CABINETOS_*` variables with keys masked, the
configuration in effect). It never leaves the PC by itself.

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && ./core/target/release/cabinetos-cli.exe log bundle --minutes 10
```

It prints the zip's path. From the window: "Diagnostics: Save Log
Bundle" (`diagnostics.saveBundle`) does the same and opens the folder
("Diagnostics: Open Log Folder" opens it without a bundle). A crash of
the core, the indexer or the window while heavy mode is on writes
`crash-<stamp>.zip` by itself, next to the crash trace of the same name;
at the next start the status bar offers "Open crash folder". A bundle
made after an investigation is the thing to attach to a report in `_io`,
not the raw heavy files.

## 6. The costs, so you can decide

- **Speed.** Every payload, every job entry, every key. On a fast disk
  the cost is small; on a slow one, a job may wait: above a queue of 256
  MiB in the core (64 MiB in the window) the logging thread waits for the
  writer, and says so afterwards with `heavy log waited`. The UI thread
  and the pipe never wait; their lines are dropped and counted instead.
- **Disk.** Up to 2 GiB of heavy files, then the oldest go. A long
  session of copying large trees fills it fast.
- **Nothing is silent.** The pill, the notice at start, the `on` and
  `off` lines in both logs. A user who never turns it on has Article 12
  as written.

## 7. Rules for an agent

1. Turn it on for the investigation, and off when the fault is found.
   Check `cabinetos-cli config get logging.heavy` before handing back.
2. Never commit `logging.heavy: true` in a default, a fixture or a test
   config; a test that needs heavy lines sets `CABINETOS_LOG_HEAVY=1` in
   the environment of the process it starts.
3. Never put a heavy file or a bundle into the repository or into a chat
   message. Quote the lines that matter, name the file's path, and leave
   the file in the log folder or in `_io`.
4. Read the chain with `log trace`, not by scrolling the files: the
   chain crosses processes, and the command sorts it for you.
5. A finding from the heavy log becomes a test or a live-check line
   before the fix is called done. The report says which line proved
   what.
6. Measure normal-mode speed with heavy mode off; say in the report
   which mode a measurement was taken in.

## 8. A worked example: "F5 did nothing"

1. With the window and the core running, turn heavy mode on:
   `cabinetos-cli config set logging.heavy true`. The pill appears.
2. Press F5 on the file. Nothing happens, as reported.
3. `cabinetos-cli log tail --process ui --heavy -n 40 --json`: find the
   `key pressed` line with `"key":"f5"` and copy its `trace_id`.
4. `cabinetos-cli log trace <that id>`: read what followed the key. A
   chain of one line means the key reached the window and started no
   command: look at `element` (what had the keyboard) and at the last
   `focus changed` line before it. A chain with `command run` but no
   `start_job` request means the command ran in the window and refused:
   its `command done` line has the outcome. A chain with `start_job` and
   an error reply names the reason from the core.
5. Write the finding as a window test or a live-check line, fix it, run
   the tests, turn heavy mode off.
