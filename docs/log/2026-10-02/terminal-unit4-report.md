# Terminal unit 4: the GUI context for cab (2026-10-02, morning)

Context: unit 4 of six of the Integrated Terminal Subsystem Sprint (Phase 21
in [PLAN.md](../../PLAN.md)). From a shell in a CabinetOS terminal, `cab`
now gives the live GUI context (the active, left and right folders and the
active pane's selection), and `cab copy|move --selection --dest
opposite_pane|<path>` runs the core's job on that selection. Written by the
Sonnet coder agent in the worktree branch `worktree-agent-a391e4e33aea0d55d`,
on the main PC, the morning of 2026-10-02. The window runs ran on this PC
behind the creator's consent file (`wait-for-pc.ps1` said "allowed" each
time; the creator had said at 08:03 that they were away); the laptop and the
VM were not used.

## Summary

| Item of the handout | Result |
|---|---|
| 1. The core request | Done. `gui_context` (reply `gui_context`; no `hello`) is answered from the newest `window_state` in memory: `active`, the front tab's folder of each pane (`null` for a tool or no window), the active pane's `selection` (marked rows, else the cursor row), `selection_total` and the `cursor`. Without a window: `no_window`. Protocol 18, schemas regenerated. `window_state` already carried the cursor and the marks as full paths; the one thing it lacked was a count for a cut list, so a pane got the optional `marked_total` |
| 2. The CLI commands | Done. `cab pane [--left\|--right\|--json]`, `cab selection [--json]`, `cab copy\|move --selection --dest opposite_pane\|<path>` with `--on-conflict` (default `skip` with `--selection`), `--resolve`, `--verify`, `--stats`. Exit 0, 1 (a failure) or 2 (no window, no selection, no folder). The commands connect with a 1 s limit and start no window |
| 3. The pipe | Kept: every command falls back to `CABINETOS_PIPE` |
| 4. Logging | The request is a debug line (`request handled`, and `gui context answered` with the numbers); the job is the core's usual `job queued` at info level; the CLI logs "starting a job from the selection" with `--log-dir` |
| Tests | Core, CLI, window and end-to-end, live check: below |
| Docs | terminal.md ("The GUI context"), core/README.md, ipc.md ("The GUI context", `marked_total`, version 18), jobs.md, ui.md, CHANGELOG.md, PLAN.md |

## How cab gets its context and runs a job

1. The window already sends `window_state` on every change (joined into one message every 50 ms): the active pane, each pane's tabs, the cursor row and the marked rows, as full paths. Nothing new is read from a disk anywhere.
2. The core keeps the newest state of any window in memory (unit 2 reads it for the prompt hook). `gui_context` works the answer out of it: `WindowStates::gui_context` in `cabinetos-core/src/window.rs`.
3. The selection is the rule the window's own file commands use (`SelectionModel.Targets`, and the core's `{selection}` for programs): the marked rows, or the cursor row when none is marked. A tool tab has none.
4. The window lists at most 1,000 marked paths. When more are marked it also sends `marked_total` (counted only then, from the selection's own count), and the answer's `selection_total` is more than `selection`.
5. `cab` (in a CabinetOS terminal the pipe is `CABINETOS_PIPE`) connects with a 1 s limit and asks `gui_context` with a 5 s limit. No window is exit 2.
6. `cab pane` and `cab selection` print what the answer says; a pane with no folder, or no selection, is exit 2. A cut selection is exit 1 with nothing printed.
7. `cab copy|move --selection` takes the selection as the job's sources and the destination from `--dest` (`opposite_pane` is the other pane's folder in the answer; a path is made absolute against the shell's own folder by the CLI).
8. It prints "copying 3 selected items of the left pane to D:\x" and hands the job to the same follower `cab copy` uses (`jobs::run`): `start_job`, one progress line, conflicts, a summary, exit code from the job's end.
9. The core runs it as any `start_job`: the checks, the scheduler, `job queued` at info level, events to every window, which shows it in its transfer flyout.
10. A conflict is decided by the policy (default `skip`), so nothing waits for a shell; `--on-conflict ask` waits and the window's flyout shows it.

## Commits

| Commit | Subject |
|---|---|
| e867060 | core: gui_context, the live GUI context for cab (protocol 18) |
| 77c05ad | window: window_state says how many rows are marked when it lists only 1000 |
| 69f9f5f | cli: pane, selection, and copy/move --selection --dest on the live GUI context |
| 00e3666 | tests: cab pane, selection, copy and move against a real core and a played window |
| 0db4337 | tests: end-to-end, cab in a real shell sees the window's selection and copies and moves it |
| 05abab6 | live check: section 21 types cab pane, selection and copy --selection into the shell |
| aace507 | window: the count behind a cut list of marks is read, not walked |
| 9a5a290 | tests: end-to-end, cab refuses a selection the window cut at a thousand rows |
| a45cc75 | live check: section 21 no longer asks the log whether a click gave the terminal the keyboard |
| (the live check commit after it) | live check: section 21 types its commands one short line at a time, once more when they are not all answered |
| (the docs commit) | docs: the GUI context for cab, protocol 18, and the report of unit 4 |

The handout listed the CLI commands and the job from the CLI as two pieces.
They share the argument definitions and `gui.rs`, and the repository has no
way to stage a file by hunks without an interactive tool, so they are one
commit. The core's own tests are in the core commit; the Rust CLI tests
have a commit of their own.

## Changes per file

Core (Rust):

- `core/crates/cabinetos-protocol/src/message.rs`: the request `GuiContext` and the reply `GuiContext` (`active`, `left`, `right`, `selection`, `selection_total`, `cursor`), their tags and a wire-form test. `window.rs`: `PaneState.marked_total` (optional, left out of the wire when absent). `lib.rs`: `PROTOCOL_VERSION` 18, with its note. `sdk/protocol/request.schema.json`, `response.schema.json`: regenerated.
- `core/crates/cabinetos-core/src/window.rs`: `WindowStates::gui_context` and `front_folder` (shared with `pane_folder`); eight unit tests. `connection.rs`: the request is answered at once; `gui_context` is logged at debug level like `terminal_pane_folder`.
- `core/crates/cabinetos-core/tests/gui_context.rs` (new): a real core process; no window, a window that leaves, marks, a cursor only, an empty folder, a tool tab, a cut selection, the debug line (and no info line), and a job started from the answer (copy with skip, move with rename). `core_process.rs`: version 18. Four test files got `marked_total: None` in their `PaneState` literals.
- `core/crates/cabinetos-cli-args/src/lib.rs`: `Command::Pane`, `Command::Selection`, `--selection` and `--dest` on `TransferArgs`, `--on-conflict` defaulting to `skip` with `--selection`; one test.
- `core/crates/cabinetos-cli/src/gui.rs` (new): the commands, `NothingToUse` (exit code 2), the refusal of a cut selection; four unit tests. `main.rs`: the dispatch, the 1 s connect, `connect` and `connect_wait` taken out of `execute` (it had reached the 100-line limit), `transfer_options` shared by both forms.
- `core/crates/cabinetos-cli/tests/gui_context.rs` (new): the real `cab` against a real core and a played window.
- `sdk/extensions/agent/plugin/src/cmdline.rs`: the Agent extension names `--selection` and `--dest` among the options it refuses (its previews cannot carry them); two lines in a test; its 90 tests pass on this machine.

Window (C#):

- `ui/CabinetOS.Core/Protocol/Requests.cs`: `WindowPaneState.MarkedTotal`. `Tabs/WindowStateBuilder.cs`: `PaneSnapshot.MarkedTotal` and the rule that writes it only when the list is cut. `ui/CabinetOS/ViewModels/PaneModel.cs`: `MarkedCount()`. `ui/CabinetOS/MainWindow.Tabs.cs`: the snapshot asks for it only when 1,000 marks were listed.
- Tests: `TabTests.cs` (the total on the wire, and absent for a whole list), `EndToEndTests.cs` (version 18), `TerminalEndToEndTests.cs` (two new tests).
- Live check: `ui/livecheck/livecheck.ps1`, section 21 (8 new lines).

Docs: terminal.md, core/README.md, ipc.md, jobs.md, ui.md, CHANGELOG.md, PLAN.md, and this report with its row in the build log.

## Tests and runs

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 867 passed, 0 failed, 6 ignored (unit 3 ended at 843) |
| `cargo clippy --workspace --all-targets -- -D warnings` | clean (the failure unit 3 saw in unit 2's code was fixed on main, d767ab9) |
| `cargo fmt --all -- --check` | clean |
| `cargo deny check` | advisories ok, bans ok, licenses ok, sources ok |
| `cargo build --release --workspace` | passed |
| the Agent extension's own tests (`cargo test --lib` in `sdk/extensions/agent/plugin`) | 90 passed |
| `dotnet build CabinetOS.sln -warnaserror` (Debug and Release) | 0 warnings, 0 errors |
| the fast window tests (no window) | 1287 tests: 1232 passed, 0 failed, 55 end-to-end skipped (main had 1284) |
| the new end-to-end test alone | passed, 8 s |
| the full window suite with the end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core) | first run 1286 of 1287 (3 min 48 s): `Quick_Open_finds_in_the_repository...` crashed on a palette race, "Element is already the child of another element", in `CommandPalette.BringHighlightIntoView` after a core event; it passed 3 of 3 alone. Second run **1287 of 1287** (4 min 20 s) |
| `build\check-scripts.ps1` | every script parses in Windows PowerShell 5.1 and PowerShell 7.6.6 |
| the live check, `run-unit4-d.txt` (a fresh Release build, from Windows PowerShell, behind `wait-for-pc.ps1` and the countdown, this PC) | **231 True, 0 False, exit code 0, 8 min 30 s**, the scroll goal met (300 frames, none over 20 ms, worst 18.8 ms). Its new lines all say True. Three earlier runs are explained below |

The four live check runs, all in `_io\live-check` (`run-unit4-a` to
`run-unit4-d`), each on a fresh Release build:

| Run | Result | Cause of the False lines |
|---|---|---|
| a, 08:46 | 231 True, 1 False | "21: the terminal had the keyboard when the commands were typed: False". My check asked the window's log for "a page has the keyboard" after a click into the shell's text; the window logs that line only for its own hand-overs (Ctrl+Backquote, a new tab), and Windows hands a click to the page itself. Every answer the typed line produced was True, so the keys had arrived. A fault of my check; the line is gone (a45cc75) |
| b, 08:56 | 230 True, 1 False | "21: Ctrl+Shift+V pasted the command and Enter wrote the file: False". The window's log says "The clipboard could not be read": another program held the clipboard, as in unit 3's first run. Not this unit's code; the cab lines were all True |
| c, 09:05 | 225 True, 6 False | All six cab lines False, no file written. The screenshot shows the typed line stopped at PowerShell's `>>` continuation prompt: one character was lost from the very long line the step typed. The step now types seven short lines one at a time, and once more when the copy's text and the exit code are not both there (it prints how many times it typed, `1` in run d) |
| d, 09:15 | **231 True, 0 False** | |

The CLI's own tests (`cabinetos-cli/tests/gui_context.rs`, 6 tests): `cab
pane` follows the keyboard from one pane to the other and works with only
`CABINETOS_PIPE`; `--json` has the protocol's words; a tool tab is exit 2;
`cab selection` prints marks, else the cursor row, and `[]` with exit 2 for
an empty folder; a selection cut at 1,000 rows is exit 1 with nothing on
standard output; copy skips what exists, overwrites with `--on-conflict
overwrite` and renames with `rename`, and with the keyboard in the right
pane the opposite pane is the left; a move takes its source; a relative
`--dest` is read against the shell's folder and a destination that does not
exist is made; a destination that is a file is exit 1 and nothing moves;
no window is exit 2 and starts no job; with no core all three commands end
in exit 1 within four seconds.

The end-to-end tests (`TerminalEndToEndTests`) use the real window, a real
Windows PowerShell started by its core, and the real `cabinetos-cli` found
on that shell's PATH. The first marks three files, runs `cab selection` and
`cab copy --selection --dest opposite_pane` (the three files are in the
right pane's folder, the output says "copying 3 selected items of the left
pane to ...", exit codes 0), selects one row, runs `cab selection` (that
row) and `cab move --selection --dest <path>` (it moves), and reads the
core's log (a copy of three sources, a move of one). The second selects
1,100 files: both commands exit 1 with "1100 rows are selected", nothing is
copied, no job is queued. The shell waits for the window's state to catch
up by looping on `cab selection` itself and says it is done by changing
folder, which the prompt hook reports: no fixed waits.

## Decided without you

Each line: what, because, how to undo.

- **The request is `gui_context`, with the reply of the same name; it needs no `hello`.** Because the handout offered the name and `terminal_pane_folder` and `get_window_state` are the pattern; `cab` sends no `hello`. Undo: rename the variants in `message.rs` and regenerate the schemas.
- **The core works the answer out; the CLI does not read `window_state`.** `cab state` and `get_window_state` already give the raw state, but the selection rule (marked, else cursor, nothing in a tool tab) belongs to the window's side of the protocol, and a plugin or the Agent extension may want it too. Undo: compute it in `gui.rs` from `get_window_state`.
- **The selection is the marked rows, else the cursor row, as the window's `Targets()` and the core's `{selection}` do.** Because it is what F5 would copy. Undo: `WindowStates::gui_context`.
- **`window_state` got an optional `marked_total`, and a cut selection is refused with exit 1.** Because the window sends at most 1,000 marked paths and a select-all in a folder of 5,000 files would have copied 1,000 and reported success. The window counts only when the list is cut (the selection's own count, not a walk). This raised the protocol to 18 together with `gui_context`. Undo: leave the field out; the commands then act on the first 1,000, which is the trap.
- **`opposite_pane` is the other pane's folder in the newest state, also when only one pane is shown.** The state does not say whether the window is in dual-pane mode; the window itself refuses "copy to the other pane" then. The first line the command prints names the destination, so nothing goes unseen. Undo: add `dual` to `window_state` (a window change) and refuse when it is false.
- **A pane with no folder (a tool tab in front) is `null` and the opposite pane with none is exit 1, not exit 2.** Because exit 2 is "nothing to act on" (no window, no selection) and a destination that cannot be named is a failure of the command. Undo: `Context::destination` in `gui.rs`.
- **Exit code 2 for "nothing to act on", also for `cab pane` and `cab selection`.** Because the handout set it for the job commands and a script wants the same answer from the commands that ask. `cab selection` with nothing selected prints nothing (`[]` with `--json`) and exits 2. the argument parser's own usage errors (clap) also exit 2; I did not change that, so the message tells the two apart. Undo: `NOTHING_TO_USE_EXIT` in `gui.rs`.
- **The default `--on-conflict` is `skip` with `--selection`, `ask` as before without it; every policy is still allowed.** Because the core's default for a job is `ask` and a shell cannot answer; the window answers conflicts in its flyout, which a script that waits cannot count on. Done with the argument parser's `default_value_if`, so an explicit `--on-conflict ask` stays. Undo: remove `default_value_if` in `cabinetos-cli-args`.
- **The context commands connect with a 1 s limit, as the prompt hook does, and wait 5 s for the answer.** Because a person at a shell is waiting. Undo: `connect_wait` in `main.rs`.
- **`gui_context` is a debug line; I added no info line.** The handout said debug, as `terminal_pane_folder` is. The live check does not read the core's debug level, so it reads the shell's own answers (files the shell writes) and the core's info line `job queued` for the job instead. Undo: add an `info!` in `WindowStates::gui_context` and read it in section 21.
- **The live check types `cabinetos-cli`, not `cab`.** Because the run's build has only `cabinetos-cli.exe` next to the core; a release's `cab.exe` is the same file. Undo: none needed.
- **The end-to-end test uses the shell as the caller, not the test's own `cab` on the core's pipe.** Because the steps cannot pause for an outside caller and the window closes the core when the steps end; the shell is also the user's real path (`CABINETOS_PIPE`, PATH). The handout's "the test knows the pipe" part is covered by the CLI's tests, which play the window. Undo: none.
- **The end-to-end tests wait by the shell itself (a loop on `cab selection`, a `Set-Location` to a folder that only marks the end, which the shell's prompt hook reports to the window).** Because the repository is removing fixed waits from these tests (the flakes reports). Undo: none.
- **The Agent extension refuses `--selection` and `--dest`.** Because its previews carry paths, and the selection is the user's, not the agent's. Undo: two lines in `cmdline.rs`.
- **`cab pane --json` carries `selection_total`.** Because it is the cheap way for a script to see a cut. Undo: `Context::json`.
- **The CLI's tests are a file of their own (`tests/gui_context.rs`) with their own small helpers.** Because the `Window` helper of `tests/term.rs` is private to that file and the repository's test files each carry theirs. Undo: none.
- **Commit trailers say `Claude Sonnet 5`, as the handout asked.** The session's own attribution reminder says 5.5. Undo: none; history is not rewritten.

## Needs you

- **One pane shown.** `cab copy --selection --dest opposite_pane` then copies into the right pane's folder, which the user cannot see; the window itself would refuse. Say if the command should refuse too (a `dual` field in `window_state`).
- **The default `skip`.** A copy from a shell never overwrites unless asked. Please confirm this is the policy you want, or say `ask` (a shell then waits for the flyout) or `rename`.
- **Exit code 2 is shared with the argument parser's usage errors.** A script that wants to tell "no selection" from a typo reads the message.
- **How the run looks.** The live check keeps `21-cab-live.png` (`%TEMP%\cabinetos-ui-test\live-shots`): the panes after the copy.

## Seen on the way, not changed

- **The Quick Open end-to-end test crashed the window once in the first full run** ("Element is already the child of another element", `CommandPalette.BringHighlightIntoView` called from `PaletteModel.SearchAsync` after a core event). It passed 3 of 3 alone and the second full run passed. It looks like another race of the palette's list, of the kind commit 2138100 fixed; not related to this unit.
- **`{selection}` of the user's programs has the same cut.** `programs::Targets::from_state` takes `marked` as sent, so a program started on a select-all of more than 1,000 rows gets the first 1,000. It could use `marked_total` and refuse. Out of the handout.
- **The worktree has no built Agent extension** (`sdk\extensions\agent\plugin\plugin.wasm`), so section 14 of the live check prints WAITING, as in the two units before.
