# Terminal unit 2: a linked shell follows its pane by a prompt hook (2026-10-02, night)

Context: unit 2 of six of the Integrated Terminal Subsystem Sprint (Phase
21 in [PLAN.md](../../PLAN.md)). A linked PowerShell or WSL shell now
follows its pane. Each time the shell draws its prompt, a few lines added
to it at start (the prompt hook) ask the core for the pane's folder and
change to it. Nothing is ever typed into the shell. The same hook reports
the shell's folder, and the terminal's caption shows it. Written by the
Opus coder agent in the worktree branch
`worktree-agent-a1d5e9555812c7418`, on the main PC, the night of
2026-10-01/02. The window runs ran on this PC behind the creator's consent
file (`wait-for-pc.ps1` said "allowed until 07:30"); the laptop and the VM
were not used.

## Summary

| Item of the handout | Result |
|---|---|
| 1. The hook asks the core | Done. `cab term cwd` reads the session from `CABINETOS_SESSION` (or `--session`) and the pipe from `CABINETOS_PIPE` (or `--pipe`). `CABINETOS_PIPE` is new and set in every terminal: the window starts its core on a random pipe, and `cab` used to fall back to `dev`. The request `terminal_pane_folder` is answered from the window's last `window_state` in memory; no file is read; with no window there is no folder. Protocol 17; the schemas regenerated |
| 2. A hook per shell | Done. PowerShell: `-NoExit -Command <script>`, which wraps the user's own `prompt`; no profile file is touched. WSL: bash `PROMPT_COMMAND`, passed in through `WSLENV`. `terminal.profiles[].hook`: `true` (the default), `false`, or the user's own code. The hook is silent, never fails the prompt, and keeps `$?` and `$LASTEXITCODE` |
| 3. The folder comes back | Done. The hook prints OSC 9;9 after every prompt. `term-<id>-out` reads it (split reads, spaces, non-ASCII, malformed and endless sequences are tested) and sends `terminal_folder_changed` to every connection that said `hello`, only on a change. `terminal_list` reports `folder` |
| 4. The caption | Done. `TerminalTab.Folder`; the rule is `TerminalCaption` (a pure class, tested): "in docs" with the whole folder as the tooltip, "started in docs" before the first report |
| 5. The pace | Measured: `cab term cwd` takes 19 ms in the middle (median) and 65 ms at the 90th percentile. The goal was under 50 ms, so no faster start path was added |
| 6. The toggle | Linking makes the next prompt follow; locking stops it. Tested with real shells, end to end, and with real keys |
| Docs | terminal.md ("Profiles", "Panes and modes", a new section "The prompt hook"), ipc.md, config.md (`hook`), ui.md ("The terminal", the snapshot step, section 21), core/README.md, CHANGELOG.md, PLAN.md |

Commits on the branch, oldest first:

| Commit | Subject |
|---|---|
| 004cc26 | core, cli: a session's pane folder for the prompt hook (terminal_pane_folder, cab term cwd) |
| e9dd96f | core: the prompt hook, added to PowerShell and WSL shells when the core starts them |
| 7baa8b3 | core: the shell's folder report (OSC 9;9) becomes terminal_folder_changed |
| 4a46a05 | window: the terminal caption shows the shell's folder as its prompt hook reports it |
| 7fc0b39 | tests: end-to-end, a linked session follows its pane at its next prompt; a locked one stays |
| 14c2163 | docs: the prompt hook, terminal_pane_folder, the profile key hook and the live caption |
| 6e9b195 | live check: section 21 links the left session and watches its prompt follow the pane |

The core and unit tests of each piece are in the piece's own commit. The
handout's separate "tests" commit holds the window's end-to-end tests
(7fc0b39).

## How the hook works

1. The core starts every shell with `CABINETOS_SESSION=<id>` and `CABINETOS_PIPE=<pipe token>`. PowerShell and WSL also get the hook, unless the profile says `"hook": false`.
2. PowerShell gets `-NoExit -Command <one line>` after the profile's own arguments. The line keeps the `prompt` function the user's profile left and defines a new global `prompt` around it.
3. At each prompt it saves `$?` and `$LASTEXITCODE` and runs `cabinetos-cli.exe term cwd` by the full path next to the core, through .NET's process API (UTF-8 output). When that printed a folder other than the one it followed last, it remembers it and runs `Set-Location -LiteralPath`, unless the shell is already there.
4. Then it gives back `$LASTEXITCODE` and `$?`, calls the user's prompt, and puts `ESC]9;9;<folder>ESC\` in front of the prompt's text. Every error is swallowed. A profile that already gives PowerShell a command or a script (`-Command`, `-File`, `-EncodedCommand`, a script name) gets no hook, and the core logs a warning.
5. WSL gets `PROMPT_COMMAND` (a bash line that defines `__cabinetos_prompt` at the first prompt and calls it at each), and `WSLENV` gains `CABINETOS_SESSION/u:CABINETOS_PIPE/u:PROMPT_COMMAND/u`.
6. At each bash prompt it keeps `$?`, runs the same Windows program (its path found once with `wslpath`) with input from `/dev/null`, drops the `\r`, follows by the same rule with `builtin cd` to the `wslpath -u` form, prints OSC 9;9 with the `wslpath -w` form of `$PWD` (worked out again only when `$PWD` changed), and returns the saved status.
7. `cab term cwd` waits at most 1 s for the pipe and 1 s for the reply. It asks `terminal_pane_folder` and prints the folder only for a linked session whose pane has a known folder. Exit code 1 for no session, no core or an unknown session.
8. The core answers from memory: the pane's tab in front, in the newest `window_state` any window sent, when it shows a folder by an absolute path.
9. `term-<id>-out` reads the reports from the bytes it already sees (a state machine that survives any split) and stores the folder; on a change it sends `terminal_folder_changed`.
10. The cost, measured on this PC with the release build, 30 runs each: `cab term cwd` median 19.3 ms (fastest 17.8, 90th percentile 65.5, slowest 66.6); through .NET's process API, the hook's way, median 18.8 ms (90th percentile 64.2). `cmd /c rem` takes about 21 ms here, so the start of a Windows process is most of it. One process start per prompt, locked or linked.

## Changes per file

Core (Rust):

- `core/crates/cabinetos-protocol/src/message.rs`: the request `terminal_pane_folder` (`session_id`; needs no `hello`), its reply (`session_id`, `pane`, `mode`, and `folder` or `null`, never left out), the event `terminal_folder_changed` (`session_id`, `folder`); a test of the wire form.
- `core/crates/cabinetos-protocol/src/terminal.rs`: `TerminalSession.folder` (left out when there is none).
- `core/crates/cabinetos-protocol/src/lib.rs`: `PROTOCOL_VERSION` 17, with its note.
- `sdk/protocol/*.schema.json`, `sdk/config/cabinetos.schema.json`: regenerated.
- `core/crates/cabinetos-core/src/window.rs`: `pane_folder`: the pane's tab in front, in the newest state any window sent, when it shows a folder by an absolute path; a test.
- `core/crates/cabinetos-core/src/connection.rs`: `terminal_pane_folder` answered at once; it and `window_state` are logged at debug level; a client that fell behind on events gets `terminal_folder_changed` for every session with a folder.
- `core/crates/cabinetos-core/src/terminal.rs`, `lib.rs`: the core passes its pipe token and the path of `cabinetos-cli.exe` next to it to the terminals (a warning when the file is missing: then the hook only reports); a profile's `hook` from the file; `linkable` by default only while the hook is on; a test.
- `core/crates/cabinetos-cli-args/src/lib.rs`: `term cwd [--session ID]`; `--pipe` falls back to `CABINETOS_PIPE`, then to `dev` (`Cli::pipe_token`); tests.
- `core/crates/cabinetos-cli/src/main.rs`, `term.rs`: `term cwd`, with its 1 s limits.
- `core/crates/cabinetos-config/src/model.rs`, `lib.rs`: `TerminalProfile.hook` (`true`, `false` or a string).
- `core/crates/cabinetos-terminal/src/hook.rs` (new): the hook's text per shell and how it is added (arguments for PowerShell, the environment for WSL); 6 unit tests.
- `core/crates/cabinetos-terminal/src/report.rs` (new): the reader of the OSC 9;9 reports, bounded to 98,301 bytes per report; 4 unit tests.
- `core/crates/cabinetos-terminal/src/session.rs`, `lib.rs`, `shell.rs`: the shell's start gets `CABINETOS_PIPE` and the hook; the output thread reads the reports; `Terminals::binding`; `hooked` in "terminal session opened"; the warning "the prompt hook was not added"; "terminal folder reported" at debug level.
- Tests: `cabinetos-core/tests/terminal.rs` (the pane folder from the newest `window_state`), `core_process.rs` (version 17), `cabinetos-terminal/tests/sessions.rs` (real PowerShell and WSL shells run a hook of the test's own at each prompt; `$LASTEXITCODE` 3 survives; the pipe token reaches the shell; a profile with its own `-Command` gets none), `cabinetos-cli/tests/term.rs` (`term cwd`; a linked pwsh, Windows PowerShell and WSL bash follow their pane at the next prompt with a real core and `cab`: a half-typed line runs as typed where its prompt was drawn, a `cd` of the user's own stays, keys typed while the hook runs arrive whole, a locked session stays, and each change of folder is reported once).

Window (C#):

- `ui/CabinetOS.Core/Protocol/Events.cs`, `MessageCodec.cs`, `ProtocolJson.cs`, `Replies.cs`: `TerminalFolderChangedEvent`; `TerminalSessionInfo.Folder`.
- `ui/CabinetOS.Core/Terminal/TerminalCaption.cs` (new): the caption rule and `IsFolder`; `TerminalHeader.Caption` moved there.
- `ui/CabinetOS/Services/TerminalController.cs`: `TerminalTab.StartFolder` and `Folder`; `OnFolderChanged` logs "terminal folder changed".
- `ui/CabinetOS/Views/ToolDock.xaml.cs`: the caption's tooltip.
- `ui/CabinetOS/MainWindow.Terminal.cs`, `MainWindow.xaml.cs`: the event's dispatch; "terminal state" logs `folder`; the snapshot step `until:terminal-folder:<path>`.
- Tests: `TerminalTests.cs` (the caption rule, `IsFolder`), `ProtocolTests.cs` (the event, `folder`), `EndToEndTests.cs` (version 17), `TerminalEndToEndTests.cs` (a linked Windows PowerShell follows its pane at the next prompt and the caption says so; a locked one stays and its caption follows the user's own `Set-Location`).

Live check: `ui/livecheck/livecheck.ps1`, section 21: the prompt hook step (7 new lines).

## Tests and runs

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 841 passed, 0 failed, 6 ignored (unit 1 ended at 821) |
| `cargo clippy --workspace --all-targets -- -D warnings` | clean |
| `cargo fmt --all -- --check` | clean |
| `cargo deny check` | advisories ok, bans ok, licenses ok, sources ok |
| `cargo build --release --workspace` | passed (3 min 42 s) |
| `dotnet build CabinetOS.sln -warnaserror` (Debug and Release) | 0 warnings, 0 errors |
| the fast window tests | 1254 tests: 1203 passed, 0 failed, 51 end-to-end skipped |
| the terminal end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core) | 6 of 6 passed, 1 min 5 s |
| the full window suite with the end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core) | 1254 of 1254 passed, 2 min 4 s |
| `build\check-scripts.ps1` | every script parses in 5.1 and 7.6.6 (`notices.ps1` and `release.ps1` are for 7 only, as before) |

The real-shell tests ran pwsh 7, Windows PowerShell 5.1 and WSL (Ubuntu)
on this PC. CI has no WSL; those tests skip with a message there.

### The live check

Both runs on the fresh Release build of this branch, from Windows
PowerShell, behind `wait-for-pc.ps1`.

- `run-unit2-0346.txt` (the first version of the step): 214 lines end in
  True, 2 checks False, both on the new step:
  - "21: Enter's prompt followed the left pane to inner, as the window's
    log says (, session ): False"
  - "21: the caption says 'in inner': False"

  The reason: the step moved the pane with a click into it, as the unit 1
  step does, and then Home and Enter opened nothing (no listing in 5 s;
  the screenshot shows the pane still in `term21`). The pane did not move,
  so the shell rightly stayed. The hook itself worked in that run: the
  prompt drawn after Ctrl+C followed the pane from `src` to `term21`, and
  the half-typed line ran in `term21`. See "Seen on the way" for the click.
- `run-unit2-0359.txt` (the step moves the pane with Ctrl+` and the
  pane's own keys): **218 lines end in True, 0 checks False, 7 min 57 s.**
  The word False appears 5 times, all as values inside information lines
  ("function keys False", "cancelled False", "saved False", "topmost
  False", "dock False"); the merged run `run-2026-10-02-0225.txt` has the
  same 5. The 7 new lines of the step all say True: the toggle linked the
  session; Ctrl+` gave the keyboard back to the left pane and Home and
  Enter took it to `inner`; the pane's move sent the shell nothing; the
  half-typed line ran in `term21`; Enter's prompt followed to `inner`
  (the window's "terminal folder changed" line); the caption says "in
  inner"; the toggle locked it again.

## Decided without you

Each line: what, because, how to undo.

- **`CABINETOS_PIPE` next to `CABINETOS_SESSION`.** The session id was there already; the pipe was not. Because the window starts its core on a random pipe token, and `cab` without `--pipe` went to `dev`. Undo: drop `PIPE_ENV` and `Cli::pipe_token`, and put `--pipe <token>` into the hook's text.
- **`--pipe` falls back to `CABINETOS_PIPE` for every command, not only `term cwd`.** Because `cab` typed in a CabinetOS terminal should reach that window's core (unit 4, the GUI context for `cab`, needs the same). Undo: read the variable only in `hook_session` in `cabinetos-cli/src/main.rs`.
- **The reply has the request's name, `terminal_pane_folder`, and carries the pane and the mode too.** Because the hook needs the mode on every prompt, and one round trip is cheaper than two. Undo: rename the `Response` variant in `message.rs` and regenerate the schemas.
- **The newest `window_state` of any window decides.** Because two windows may share one core, and the one the user touched last sent last. Undo: `WindowStates::pane_folder` in `cabinetos-core/src/window.rs`, keyed by connection.
- **A pane shows a folder only when its tab in front shows a folder by an absolute path.** A tool tab, or no window, gives `null`, and the hook leaves the shell where it is. Because there is no folder to change to. Undo: the same function.
- **Follow on a change only.** The hook remembers the folder it followed last and changes only when the pane's folder differs from it. Because otherwise a `cd` of the user's own would be undone at the very next prompt. Undo: compare with the shell's current folder instead, in `builtin_follow` (`cabinetos-terminal/src/hook.rs`).
- **A half-typed line runs where its prompt was drawn.** The handout's test expected it to run in the pane's new folder. A hook that runs when the prompt is drawn cannot move the shell after the line is started without typing into the shell, which Zero-Hijack forbids. The line runs as typed in the old folder, and the prompt after it is in the new one; the tests assert exactly that. Undo: not a small change. A PSReadLine Enter handler that follows before it accepts the line would do it for PowerShell only.
- **The PowerShell hook is a `-Command` line, not a script file.** Because Windows PowerShell's default execution policy refuses script files, and nothing has to be written to disk. Undo: write the script into the core's data folder and pass `-File`, in `hook.rs`.
- **A PowerShell profile that already runs a command or a script gets no hook, and a warning.** Because a second `-Command` would break the user's own. Undo: `powershell_conflict` in `hook.rs`.
- **WSL: `cab` runs with input from `/dev/null`, and its Linux path is found once.** Because WSL hands a Windows program the terminal's input, and keys typed while the hook ran were lost (seen in the tests). Undo: remove `</dev/null` in `builtin_follow`.
- **`hook` in a profile: `true`, `false` or a string; `linkable` by default only while the hook is on.** Because a session without a hook cannot follow. Undo: `profile()` in `cabinetos-core/src/terminal.rs`.
- **One event per change, the last report of each output chunk, `folder` in `terminal_list`, and a resend after lag.** Because the hook reports after every prompt and most prompts do not change the folder. Undo: `reported()` in `cabinetos-terminal/src/session.rs`.
- **The caption says "in <folder's name>" with the whole folder in its tooltip, and "started in <name>" before the first report.** Because the header is narrow. Undo: `TerminalCaption.Decide`.
- **The tests that follow with a real core, `cab` and shell live in the CLI crate (`cabinetos-cli/tests/term.rs`).** Because they need all three; the terminal crate's tests run a hook of the test's own. Undo: move them.
- **`cab term list` prints no folder.** Because its columns are unit 1's and the handout did not ask for one; `terminal_list` carries it for any client. Undo: add a column in `cabinetos-cli/src/term.rs`.
- **Without `cabinetos-cli.exe` next to the core, the hook only reports, with a warning.** Because a core started by `cargo run -p cabinetos-core` may lack the built CLI. Undo: `command_line()` in `cabinetos-core/src/terminal.rs`.
- **`terminal_pane_folder` and `window_state` are logged at debug level.** Because the hook asks once per prompt; at INFO every Enter would add a line. Undo: `log_handled` in `cabinetos-core/src/connection.rs`.
- **No faster start path for `cab term cwd`.** Because the median, 19 ms, is under the 50 ms goal. Undo: not needed.
- **The live check step moves the pane with Ctrl+`, not with a click.** Because after a click from the terminal into a pane, Home and Enter opened nothing ("Seen on the way"). Undo: `ClickLeftPane` in place of the Ctrl+` lines of the step.
- **The commits from 14c2163 on end with `Co-Authored-By: Claude Opus 5.5`.** The handout asked for Opus 5; the session's own attribution changed to Opus 5.5 half way. The earlier commits keep Opus 5. Undo: none needed; history is not rewritten.

## Needs you

- **The half-typed line.** It runs in the folder its prompt was drawn in, and only the next prompt follows ("Decided without you"). Please confirm this is what "linked" should mean.
- **Follow on a change only.** A `cd` of your own stays until the pane moves again. Please confirm.
- **The caption's words**, "in docs" and "started in docs".

## Seen on the way, not changed

- **A click from the terminal into a pane leaves the keys nowhere.** In section 21's unit 1 step ("a click in the left pane and a folder opened there"), the click makes the left pane active (the window logs "selection shown"), but Home, Enter and Backspace then do nothing: no listing comes, and the step waits out both folder changes (11 s; 11.5 s in the merged run `run-2026-10-02-0225.txt` too). Its check only says that no other tab was shown and no terminal request was sent, so it passes. The keys did not reach the shell either: in the first version of the prompt hook step, the same click, Home and Enter left the half-typed line unrun. Either the live check's real click leaves Windows' keyboard focus in the terminal's web page while XAML's focus moves, or the window has a fault there. The end-to-end test of unit 1 drives keys through the window's own key path, so it cannot see this. Worth a look with the `heavy-logging` skill.
- **An orphan `cabinetos-cli.exe term cwd`** stayed alive once after a WSL experiment in which the core was killed while the hook waited. It held the exe locked, and a later test build failed until it was stopped. Normal runs end it within the 1 s limits; a WSL interop child seems to be able to outlive its parent when killed hard.
- **The branch is one commit behind main** (f770f4b, the live check's `PSModulePath` fix, which touches `livecheck.ps1` and `ui.md` in other places). Not rebased: the planning session merges.

## Notes for the next units

- **Unit 4 (the GUI context for `cab`).** `cab` in a terminal already reaches the window's own core through `CABINETOS_PIPE`. `WindowStates` in `cabinetos-core/src/window.rs` has what the window shows (both panes' tabs, the active pane); `pane_folder` is the pattern for more such questions.
- **Unit 5 (restoration).** `TerminalSession.folder` is the shell's last reported folder: restoring a session there, rather than in its start folder, needs no new message.
- **The hook's text** lives in one place, `cabinetos-terminal/src/hook.rs`; its unit tests pin the PowerShell and bash text, and `tests/sessions.rs` runs it in real shells.
