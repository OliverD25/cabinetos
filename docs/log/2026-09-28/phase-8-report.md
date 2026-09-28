## Phase 8 report

Context: CabinetOS Phase 8, the integrated terminal, core side only (the ConPTY host). The terminal pane in the UI waits for Phase 5.

### Built
- **New crate `cabinetos-terminal`** (library; unsafe only in `conpty.rs` and `console.rs`, every block has a `// SAFETY:` comment).
  - `Terminals` runs each shell in a pseudo-console (`CreatePseudoConsole` with two pipes; `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`). It also resizes (`ResizePseudoConsole`) and closes (`ClosePseudoConsole`).
  - Each session has three threads: `term-<id>-out` (drains the output to EOF), `term-<id>-in` (writes keys) and `term-<id>-exit` (waits on the process handle). One async task serves the session's byte pipe.
  - Byte pipe `\\.\pipe\cabinetos-term-<16 hex>`: raw bytes both ways, user-only DACL, no remote clients, one attached client at a time. A client can reattach after leaving.
  - Output buffer of exactly 1 MiB with backpressure. The warning `terminal output backpressure` is logged once per session.
  - Limit of 32 sessions. Programs are found with `SearchPathW` over `PATH`. The environment is inherited, plus `TERM` and `CABINETOS_SESSION`.
  - The per-shell `cd` line and its escaping (`shell.rs`). The console helpers used by the CLI (`console.rs`).
- **cabinetos-ipc:** `byte_pipe(name, first)` and `stored_security(handle)`.
- **Protocol version 7:**
  - Requests `terminal_open`, `terminal_resize`, `terminal_close`, `terminal_sync_cwd`, `terminal_list`.
  - Replies `terminal_opened`, `terminal_sessions`. Event `terminal_exited`.
  - Error codes `no_such_session`, `unknown_profile`, `spawn_failed`. Types `TerminalSession`, `TerminalState`. Schemas regenerated.
- **Core:**
  - New module `terminal.rs`: starts the manager and resolves a profile from the settings at each open.
  - `Services.terminals`. Handlers in `connection.rs`: open, close and sync run on the blocking pool; resize and list answer at once.
  - `terminal_exited` goes to every connection that said hello, and is sent again for exited sessions when a connection falls behind on events.
  - All sessions are closed at shutdown.
- **CLI:**
  - `term [--profile] [--cwd]`: raw console mode, resize poll every 250 ms, `Ctrl+]` detaches, prints the exit code. With piped stdin it forwards the bytes as they are.
  - `term list`, `term close <id>`, `term cd <id> <path>`.
  - A filter drops `ESC[?9001h` and `ESC[?1004h` when stdout is a console.
- **Docs:** new `docs/terminal.md`. Updated `docs/ipc.md` (third channel, v7 messages), `docs/config.md`, `docs/diagnostics.md` (thread names), `docs/ARCHITECTURE.md` (crate map, §4 rows, change log) and `core/README.md`.

### Commits (all pushed to origin/main)
- cce4222 terminal: run shells in pseudo-consoles, each with its own byte pipe
- 7182746 terminal: compare PowerShell's folder by its long name (fixes CI; see Checks)
- a76acf1 core, cli: terminal sessions over the pipe, and `term` to use them
- 133a1a9 terminal: name the session in every log line (the log layer drops span fields other than request_id and plugin_id; found in the live check)
- a766901 terminal: let Windows write the attribute list through a mutable pointer (soundness fix: the pointer came from `Vec::as_ptr`; no behavior change)
- 1302776 docs: the integrated terminal, its messages and its byte pipes

### Checks
- All five checks pass locally on 1302776: build, clippy `-D warnings`, fmt `--check`, deny (advisories, bans, licenses, sources ok). Tests: 393 passed, 0 failed, 5 ignored by design (was 356; 37 are new).
- CI:
  - Run 36404919855 on cce4222 **failed** in `powershell_follows_the_pane_when_installed`. On the runner, `%TEMP%` is spelled with a short name (`RUNNER~1`) and PowerShell prints the long name, so the expected prompt did not match. The folder change itself worked. Fixed in 7182746.
  - Run 36406655183 on a76acf1: green.
  - **Final run 36409346970 on 1302776: green** (17m33s, all steps). https://github.com/OliverD25/cabinetos/actions/runs/36409346970
- The WSL test passes on CI. Its "skipped" message is not visible there, because the test harness hides the printed output of passing tests. Locally it really ran, against WSL Ubuntu.
- Flakiness checks: the terminal suite passed 8 runs in a row, the reattach test 25 in a row, the core end-to-end tests 6, the CLI tests 5.
- A stress probe closed 20 sessions at the moment a client connected. Before the `let_go` fix, about 1 in 3 clients never got the end of the pipe; after it, 0 of 20.
- The `term` output filter is really covered: with the filter switched off, the in-console test fails.

### Live check
The core was started on pipe `live8` with its config, logs and plugin folders under `%TEMP%\cabinetos-term-test\live` (all removed afterwards). Debug build.

1. `printf 'Get-Location\r\nexit\r\n' | cabinetos-cli --pipe live8 term --profile pwsh --cwd 'E:\'`. Stdout was saved to a file and is shown with `cat -v`; stdout is not a console there, so the bytes are raw.
```
cli exit status: 0
--- stderr:
cabinetos-cli: session 1, pid 105692
cabinetos-cli: session 1 ended; exit code 0
--- stdout:
^[[?9001h^[[?1004h^[[?25l^[[2J^[[m^[[HPS E:\>^[[1C^[]0;C:\Program Files\PowerShell\7\pwsh.exe^G^[[?25h^[[?25l^[[93mGet-Location^[[m^M
^[[K^M
                                                                                ^[[2;1H^[[?25h^M
^[[?25l^[[32m^[[1mPath^M
----^[[m^M
E:\^[[7;1HPS E:\> ^[[92mexit^[[37m^M
>> ^M
^[[?25h^[[?9001l^[[?1004l^[[m
```
2. `term list` from a second CLI, while two `term` CLIs were attached (fed by timed piped input):
```
1 pwsh pid 105692 80x25 exited(0) detached E:\
2 cmd pid 101900 80x25 running attached C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\one
3 pwsh pid 86676 80x25 running attached C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\two
```
3. `term cd 2 "…\live\moved here"`, then session 2's own timed `cd`. Session 2's CLI output, with escape sequences removed:
```
session 2: cd C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\moved here
C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\one>cd /d "C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\moved here"
C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\moved here>cd
C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\moved here
C:\Users\Admin\AppData\Local\Temp\cabinetos-term-test\live\moved here>exit
cabinetos-cli: session 2 ended; exit code 0
```
- The core's log recorded `terminal_open` at 17 to 24 ms.
- After rebuilding with 133a1a9, the exit line carries the session: `{"level":"INFO",…,"message":"terminal shell exited","span":"terminal","fields":{"exit_code":2,"session_id":1},"thread":"term-1-exit"}`.
- Both live cores exited with code 0 after `shutdown`. No shell or console host from the checks was left running (verified with a process list).
- Nothing was added beyond the phase's list. There is no UI.

### Decided
- `console.rs` (raw mode, console size, `ReadConsoleW`) lives in `cabinetos-terminal`, not in the CLI — because the CLI has `#![forbid(unsafe_code)]` and this phase keeps its unsafe code in the terminal crate — undo: move the module into the CLI and allow unsafe there.
- The byte pipe allows 2 kernel instances. Only one is listening or connected at any time, and the next is created before the last is dropped — because with 1 instance the name vanished between two clients, and a reattaching client got "not found" (a flaky test showed it) — undo: `max_instances(1)` in `byte_pipe`, and drop-then-create in `serve_pipe`.
- One attached client at a time; others get `ERROR_PIPE_BUSY` and retry — because the spec asks for it — undo: more instances plus output fan-out.
- A closing session first finishes a connect in flight (`let_go`: a client of its own opens the pipe, then the instance is dropped) — because tokio/mio keeps an instance open when its connect completes after the drop, and that client never saw the end of the pipe (about 1 in 3 racing closes before, 0 in 20 after) — undo: return straight from the `select!`.
- Exited sessions stay listed until `terminal_close`, and the limit of 32 counts them — because a restarted UI must still get the last output and the exit code — undo: remove the session in `watch_exit`.
- Backpressure applies while the shell runs. After it exits, output that does not fit is dropped — because Microsoft documents that `ClosePseudoConsole` can wait forever on unread output — undo: `Overflow::DropNew` in `Output::push`.
- The backpressure warning is logged once per session — because a stuck client would otherwise flood the log — undo: reset `waited` when the buffer drains.
- After the shell exits, the core waits until the output has been quiet for 100 ms (at most 1 s) before closing the pseudo-console — because the pseudo-console paints the last output a few ms after the shell writes it — undo: remove `wait_quiet` in `watch_exit`.
- `terminal_exited` is sent after the output reached EOF, also for sessions closed by request — because the event then means "all output is on the pipe", and every client learns the shell is gone — undo: publish right after `wait_for_exit`.
- Close is a hang-up (`ClosePseudoConsole`). After 2 s the shell is ended with `TerminateProcess`, then the core waits up to 1 s for it to be gone, and only then replies. Shutdown closes all sessions within one shared 2 s — because a shell closed in its first milliseconds never ends on its own (seen in 12 of 20 tries), and ending by force is asynchronous — undo: `CLOSE_GRACE` and `finish_close`.
- Profiles are read from the settings at each `terminal_open` — because of Article 6 (edits apply at once) — undo: resolve them once at startup.
- The program is an absolute path, or `SearchPathW` over `PATH` with `.exe` added. The current folder is not searched — because a stray `pwsh.exe` in the core's folder must never run — undo: pass NULL as the search path.
- Without `cwd`, the shell starts in `USERPROFILE`, else the core's folder — because the protocol doc says so — undo: `home_folder()`.
- The environment is the core's, plus `TERM=xterm-256color` and `CABINETOS_SESSION=<id>` — because ConPTY emits xterm VT, and scripts can detect the terminal — undo: the `environment_block` call in `start`.
- Standard handles are set to `INVALID_HANDLE_VALUE` with `STARTF_USESTDHANDLES` — because a shell could otherwise inherit a redirected parent's stdio (WezTerm does the same) — undo: remove the three lines in `spawn`.
- The shell family comes from the program's file stem (pwsh/powershell, cmd, wsl, other) — because a profile's name is free text — undo: `ShellKind::of`.
- Escaping of the cd line — because each shell must read the path literally — undo: `ShellKind::cd_line`:
  - pwsh: `Set-Location -LiteralPath '…'`, with `'` and the typographic quotes ‘ ’ ‚ ‛ doubled.
  - cmd: `cd /d "…"`, with each `%` moved out of the quotes as `"%^x"`. Tested with a folder named `to 100%CABINETOS_SESSION% & ^x (y)`, while that variable is defined.
  - wsl: `cd "$(wslpath -a '…')"`, with `'` written as `'\''`.
  - Other shells: `cd "…"`, unchanged.
- The line ends in `\r` — because that is what a terminal sends for Enter — undo: `cd_line`.
- `terminal_sync_cwd` checks the path (absolute, an existing folder) and answers with the general codes `invalid_path` or `not_found`. A bad `cwd` in open maps to `spawn_failed` — because the spec's three terminal codes do not cover a bad folder in sync — undo: the `folder()` mapping in `sync_cwd`.
- `sync_cwd` uses `try_send`; a full input queue answers `internal` — because `blocking_send` panics when called inside a runtime (a probe hit this) — undo: `blocking_send`.
- Sizes are clamped to 1..=32767 — because COORD is a signed 16-bit number and 0 is invalid — undo: `cells()`.
- The resize test uses `mode con` and reads "Columns: N" — because the spec asked to decide — undo: the tests.
- Test inputs `echo a^bc` and `echo fr^esh` — because the answer then never appears in the typed line — undo: the tests.
- CLI without `--cwd` uses its own folder, made absolute — because a CLI user expects the shell where they are — undo: `term_command`.
- The CLI's filter runs only when stdout is a console; redirected output stays raw — because win32-input-mode hides `Ctrl+]` (the test fails without the filter) — undo: `ModeFilter`.
- CLI status lines go to stderr; the shell's bytes go to stdout — undo: `note()`.
- With piped stdin, the CLI keeps printing after stdin ends, until the shell exits — because detaching or closing at EOF would orphan the session or cut the shell's work short — undo: the `Input::End` handling in `pump`.
- The CLI prints the exit code and exits 0 itself — because the spec says "printing" — undo: return the code from `main`.
- The interactive CLI path is tested by running `cabinetos-cli term` as the program of a pseudo-console owned by the test, which types the keys — because tests have no console — undo: remove the two `in_a_console_*` tests.
- Protocol version 7 is raised in the core commit, together with the requests, not in the library commit — so every commit builds on its own — n/a.

### Needs the user
Nothing needs Administrator rights, and nothing outward-facing is pending. Ready text for the coordinator:
- **docs/PLAN.md.** Heading: `### Phase 8 — Integrated terminal — core side done 2026-09-28`. Insert before `Articles: 4, 9.`:
  "Core side done 2026-09-28; the terminal pane waits for the UI (Phase 5). Built: the crate `cabinetos-terminal`: each shell runs in a ConPTY pseudo-console, from a profile in `cabinetos.json` read at each open, with `TERM=xterm-256color` and `CABINETOS_SESSION`; each session's raw bytes travel on a pipe of their own (`\\.\pipe\cabinetos-term-<random>`), with the control pipe's access rules and one client at a time; sessions belong to the core, so a client may leave and attach again; output waits in a 1 MiB buffer that holds the shell back when full; closing is a hang-up, and a shell still running 2 s later is ended. Protocol version 7: `terminal_open`, `terminal_resize`, `terminal_close`, `terminal_sync_cwd`, `terminal_list` and the event `terminal_exited`. "cwd follows the active pane" is `terminal_sync_cwd`: it types the shell's own change-directory command, quoted so pwsh, cmd and WSL read the path literally. `cabinetos-cli term` runs a shell in a console window (Ctrl+] detaches). Measured 2026-09-28 on the development PC (debug build): a shell starts in 17 to 24 ms and closes at its prompt in 3 to 15 ms; 2.8 MB printed while no client read arrived complete. Guide: [terminal.md](terminal.md)."
- **README.md status paragraph** (replaces it whole):
  "Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done, and the core sides of Phases 6, 7 and 8: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, runs copy, move and delete jobs on per-disk queues, runs sandboxed WebAssembly plugins whose crashes it contains, searches whole NTFS volumes through an elevated indexer (or walks folders without it), and runs shells in pseudo-consoles for the terminal pane, all over a user-only named pipe, with the indexer behind a read-only pipe of its own and each shell's bytes on a pipe of their own (`core/`, 393 tests, CI green). Phase 5, the WinUI 3 shell, needs the .NET SDK."
- **README.md Documents table**, a new row after `docs/indexer.md`:
  `| [docs/terminal.md](docs/terminal.md) | The integrated terminal: profiles, the byte pipe of each session, following the active pane, closing, the `term` CLI. |`

### Known gaps
- Closing a shell in its first milliseconds takes 2 s. Windows leaves such a shell running, so the core ends it by force after the grace.
- Child processes of the shell that ignore the hang-up are not ended. No job object is used; the shell itself always is.
- `terminal_sync_cwd` types a line, with these limits:
  - Text already on the prompt line stays in front of the typed line.
  - A program running in the shell receives the line instead of the shell.
  - cmd with delayed expansion (`!`) is not handled.
  - WSL assumes a POSIX shell (bash or zsh). "Other" shells get the path quoted with no escaping.
- A client that disconnects in the middle of a write may lose at most one 64 KiB chunk that was already on its way. Everything still in the buffer goes to the next client.
- A reattaching client gets the buffered bytes, not a picture of the screen. A resize to another size makes the pseudo-console paint the screen again (noted in `terminal.md`).
- In piped mode, Ctrl+C ends the CLI and the session keeps running. `term close <id>` ends it.
- The `Get-Location\r\nexit\r\n` check shows a `>>` continuation prompt: PSReadLine reads the `\n`. This is cosmetic; the exit code is 0.
- The interactive `term` was driven by the automated in-console tests, not by a person in a Windows Terminal or conhost window. An optional manual try needs PowerShell, because WSL runs Windows programs with pipes instead of a console: `cabinetos-core.exe --pipe demo` in one window, `cabinetos-cli.exe --pipe demo term --profile pwsh` in another.
- A WSL session ran only locally (CI has no WSL).

### Noticed out of scope
- `cabinetos-jobs` test `a_move_on_one_volume_is_a_rename` asserts `elapsed_ms < 50`. It failed once during a full local workspace run under load. It passed 3 of 3 alone and in the full re-run. It is sensitive to machine load.
- The decisions table in `docs/ARCHITECTURE.md` cites `§7 "terminal panels"`, but that phrase is in §1 (Progressive Disclosure). My new crate-map row cites §1.
- The core's and the indexer's accept loops drop a pipe instance with a connect in flight at shutdown: the same pattern that `let_go` fixes. It is harmless there, because the process exits.
- CI hides the printed output of passing tests, so skip messages (such as WSL's) never show in CI logs. `cargo test -- --show-output` for the terminal tests would make them visible; that is a CI change.
