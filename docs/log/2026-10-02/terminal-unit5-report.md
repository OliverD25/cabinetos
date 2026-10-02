# Terminal unit 5: config and restoration (2026-10-02, late morning)

Context: unit 5 of six of the Integrated Terminal Subsystem Sprint (Phase 21
in [PLAN.md](../../PLAN.md)). After a restart, the terminal's tabs come back
as they were. Two keys, `terminal.restore` and `terminal.defaultMode`, say
whether and how. Two follow-ups of unit 4 are closed: `cab copy|move
--selection --dest opposite_pane` is refused while only one pane is shown,
and the default conflict policy `skip` is recorded as confirmed. Written by
the Sonnet coder agent in the worktree branch
`worktree-agent-a8a124e56be8f0285`, on the main PC, the late morning of
2026-10-02. The creator was away ("proceed, test and merge"); the window runs
on this PC ran behind the consent file (`wait-for-pc.ps1` said "allowed"),
the laptop runs were part of the checks, the VM was not used. Nothing was
pushed or merged.

## Summary

| Item of the handout | Result |
|---|---|
| Piece 1: `opposite_pane` with one pane shown | Done. `window_state` carries `dual` (the window sends it, the core keeps it, `gui_context` reports it), protocol 19, schemas regenerated. `cab copy\|move --selection --dest opposite_pane` ends with exit code 1 and one line, "the other pane is hidden; show both panes or name a path", and starts no job; a named path still works. A window that does not send `dual` (older) means both panes shown. Tests at the core (unit and process), the CLI (unit and process) and the window |
| Piece 1: the default `skip` | Recorded as confirmed in terminal.md ("The GUI context"), config.md needs nothing (it is a command's default, not a key) |
| Piece 2: `terminal.restore` | Done. Default `true`. Read by the window at the first show of the dock |
| Piece 2: `terminal.defaultMode` | Done. Default `locked`. The core reads it at each `terminal_open` that names no mode, so an edit applies to the next session; a profile that is not linkable stays `locked` |
| Piece 2: the re-audit of the `terminal.*` keys | Done, below |
| Piece 3: saving | Done. `terminal.tabs` in `cabinetos.json`, written by the window through the core's `set_value`, the way `ui.tabs` is. Profile, folder (the prompt hook's last report, else the start folder), pane, mode, tab order, the tab in front and each pane's front tab |
| Piece 3: restoring | Done. At the first show of the dock after a restart, with `terminal.restore` true and no session yet, the saved sessions start again as fresh shells. A gone folder falls back to the user's profile folder, a gone profile to `terminal.defaultProfile`, a link that cannot be made to `locked`; each with a log line. A session that still fails is skipped and the rest go on. One log line per session ("terminal session restored") and one for the whole ("terminal restored", with the count) |
| The pure rule class | `TerminalRestore` (`Decide`, `Plan`, `Retry`, `ShowOrder`) with 18 unit tests, one or more for every fallback; `TerminalLayout` (the saved layout's reading and writing) with 4 |
| Tests | Rule class and config unit tests; three end-to-end tests; CLI tests for the refusal; core tests for `gui_context` with and without `dual`: below |
| Live check | Section 21 has a save check (two lines True, and one after the window closes). It has no restoration check, since it never restarts its window |
| Docs | terminal.md ("Restoring the tabs"), config.md, ipc.md (`dual`, version 19), ui.md ("The terminal"), CHANGELOG.md, PLAN.md, this report |

## How the save and the restoration work

1. **Where it lives.** In `cabinetos.json`, under `terminal.tabs`: `items` (a list of `{profile, folder, pane, mode}`, in the order of the tabs), `front` (the index of the tab in front) and `shown.left` and `shown.right` (each pane's front tab). It is the same place and the same mechanism as `ui.tabs`: the window asks the core to `set_value`, the core checks and stores it, no code of the window touches the file.
2. **When it is written.** A second after any change of the tabs (a session opens or closes, its mode or its reported folder changes, another tab comes to the front), and once more when the window closes, in parallel with the window's other closing writes. A write that would change nothing is skipped. Nothing is written before the dock was first shown after the start, nor while a restoration runs, so an empty window cannot wipe the saved tabs. It is written whatever `terminal.restore` says, so turning the key on later brings back the last layout.
3. **When it is read.** At the window's start (`ReadConfigAsync`), kept in memory; used at the first show of the dock: Ctrl+Backquote, the terminal button, Ctrl+Shift+T. Once per start.
4. **What is decided.** `TerminalRestore.Decide` says one of three things. The core already runs sessions the window does not show: adopt those, restore nothing from the file (the core's own sessions win). `terminal.restore` false, or nothing saved: nothing. Else: restore.
5. **How a session is restored.** `Plan` turns each saved item into a step: a profile that is gone becomes `terminal.defaultProfile`, a missing folder stays missing (the profile folder), all with a recorded fallback. The window opens the sessions one by one with `terminal_open`. When the core refuses with `spawn_failed` and the folder was not the profile folder, `Retry` asks once more in the profile folder; with `not_linkable` for a linked session, once more locked. A session that still fails is logged as "terminal session not restored" and skipped.
6. **What comes to the front.** `ShowOrder`: each pane's own front tab first, then the saved front tab last, so the one view ends on the tab that was in front and the split dock shows each pane's own tab in its half. The terminal gets the keyboard, as a Ctrl+Backquote does.
7. **What does not come back.** The text a shell printed, its history, and the programs that ran in it. A restored session is a new shell that starts where the old one was.
8. **The logs.** "terminal restore" (the decision and why), "terminal restore fell back" (what, from, to), "terminal restore tries again", "terminal session restored" per session, "terminal session not restored" (warning), "terminal restored" (count, saved, skipped), and "terminal layout saved" on each write.

## Commits

| Commit | Subject |
|---|---|
| 61d4257 | Unit 5, piece 1: cab refuses "the other pane" while only one pane is shown |
| 85569ae | Unit 5, piece 2: terminal.restore and terminal.defaultMode |
| 55608be | Unit 5, piece 3: the terminal's sessions are saved in cabinetos.json as terminal.tabs |
| f36e872 | Unit 5, piece 4: the terminal tabs come back when the dock is first shown after a restart |
| 4a4380c | Unit 5: live check section 21 checks that the terminal's sessions are saved |
| the commit after these | Unit 5: the docs and the report (terminal.md, config.md, ipc.md, ui.md, CHANGELOG.md, PLAN.md, this report and its index row) |

The handout listed the save and the restoration as one piece. They are two
commits (the save, then the restoration), and the window's end-to-end tests
of both are in the second, since the repository has no way to stage a file by
hunks without an interactive tool.

## The re-audit of the existing `terminal.*` keys

Each key was checked in three places: the schema (`sdk/config/cabinetos.schema.json`,
generated from `model.rs`), `docs/config.md` (the table and "Who uses what"),
and whether it applies live.

| Key | Schema | config.md | Applied |
|---|---|---|---|
| `terminal.defaultProfile` | present; its doc comment now says it applies only when the client names no profile, must name a profile, and changes the next session | present, unchanged | by the core at each `terminal_open`: live for the next session (tested in `tests/terminal.rs`) |
| `terminal.profiles` | present; the doc comment now says names are unique and that a running shell keeps what it started with | present, unchanged | by the core at each `terminal_open`: live for the next session |
| `terminal.split` | present | present | by the window at once, when the file changes (except in the window that wrote it) |
| `terminal.restore` (new) | present, with its doc comment | new row | by the window, read at the first show of the dock |
| `terminal.defaultMode` (new) | present | new row | by the core at each `terminal_open` |
| `terminal.tabs` (new) | present | new row | by the window: written whole, read at the start only |

Found and fixed: the "Who uses what" paragraph of config.md named only the
profile keys and `split`; it now names every key of the section and who reads
it. The two profile keys' doc comments (which become the schema's
descriptions) did not say when an edit applies; they do now. No key was
missing from the schema, no key was documented but absent, and none was read
at the wrong time.

## Changes per file

Core (Rust):

- `core/crates/cabinetos-protocol/src/window.rs`: `WindowState.dual` (a missing field reads as true). `message.rs`: `Response::GuiContext.dual`, with a wire test. `lib.rs`: `PROTOCOL_VERSION` 19, with its note. `sdk/protocol/request.schema.json`, `response.schema.json`: regenerated.
- `core/crates/cabinetos-core/src/window.rs`: `gui_context` answers `dual`; a unit test. `tests/gui_context.rs`: with and without `dual`, through a real core process. `core_process.rs`: version 19. The other `WindowState` literals in the core's and the CLI's tests and in `programs.rs` got `dual: true`.
- `core/crates/cabinetos-cli/src/gui.rs`: `Context.dual`, the refusal in `destination`, `dual` in `--json`; a unit test. `tests/gui_context.rs`: the real `cab` with one pane shown is refused with exit 1 and starts no job, and a named path still works.
- `core/crates/cabinetos-config/src/model.rs`: `TerminalConfig.restore`, `default_mode`, `tabs`; the types `TerminalTabs`, `ShownTerminals`, `SavedTerminal`; two doc comments. `parse.rs`: `check_terminal_tabs` (an index must name a saved item; the error names the key and the count). `store.rs`: a round trip through the file. `lib.rs`: the re-exports. `sdk/config/cabinetos.schema.json`: regenerated.
- `core/crates/cabinetos-core/src/terminal.rs`: `default_mode(config, profile)`, `locked` for a profile that is not linkable. `connection.rs`: `terminal_open` with no mode takes it. Tests: `tests/terminal.rs` (a session with no mode starts in the default mode, with a live edit of the file), `tests/config.rs` (the defaults), `tests/shell_requests.rs` (`set_value` writes the three keys).

Window (C#):

- `ui/CabinetOS.Core/Protocol/Requests.cs`: `WindowStateRequest.Dual`. `Replies.cs`: `ErrorCodes.NotLinkable`. `Tabs/WindowStateBuilder.cs`: `dual`. `Settings/UiSettings.cs`: `TerminalRestore`.
- `ui/CabinetOS.Core/Terminal/TerminalLayout.cs` (new): the saved layout, read leniently from the config (an item with no profile is dropped and the indexes follow) and written as the JSON of `terminal.tabs`. `TerminalRestore.cs` (new): the pure rules.
- `ui/CabinetOS/Services/TerminalController.cs`: `Layout()`, `RestoreAsync`, `CoreSessionsAsync`, `AdoptAsync`, `OpenQuietlyAsync`. `MainWindow.Terminal.cs`: the save timer, `SaveTerminalLayoutSoon/Async`, `RestoreTerminalsAsync`, the first-show hook in `SummonAsync` and `NewTerminalAsync`, `ArmTerminalRestore`. `MainWindow.xaml.cs`: the read at the start, the write at the close, `until:terminal-restored`. `MainWindow.Tabs.cs`: the window sends `dual`, and a change of the layout sends the state.
- Tests: `TerminalRestoreTests.cs` (18), `TerminalLayoutTests.cs` (4), `TabTests.cs` (`dual` on the wire), `EndToEndTests.cs` (version 19), `TerminalEndToEndTests.cs` (three new tests).
- Live check: `ui/livecheck/livecheck.ps1`, section 21 (three new lines).

Docs: terminal.md, config.md, ipc.md, ui.md, CHANGELOG.md, PLAN.md, and this report with its row in the build log.

## Tests and runs

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 879 passed, 0 failed, 6 ignored (unit 4 ended at 867) |
| `cargo clippy --workspace --all-targets -- -D warnings` | clean |
| `cargo fmt --all -- --check` | clean |
| `cargo deny check` | advisories ok, bans ok, licenses ok, sources ok |
| `cargo build --release --workspace` | passed |
| `dotnet build CabinetOS.sln -warnaserror` (Debug and Release) | 0 warnings, 0 errors |
| the fast window tests (no window) | 1333 tests: 1275 passed, 0 failed, 58 end-to-end skipped (main after unit 4 had 1252 fast tests) |
| the three new end-to-end tests, alone | passed |
| the full window suite with the end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core), this PC | second run **1333 of 1333** (3 min 57 s). The first run had 3 failures: my CHANGELOG entry (a raw backtick that the changelog parser reads as code; fixed), and two tests that fail by turns under the whole suite's load and pass alone: the rail's "every way of picking a folder" and the out-of-view context menu. See "Seen on the way" |
| `build\check-scripts.ps1` | every script parses in Windows PowerShell 5.1.26100.9444 and PowerShell 7.6.6 |
| the live check, this PC, `run-unit5-a.txt` (a fresh Release build, from Windows PowerShell, behind `wait-for-pc.ps1` and the countdown) | **253 True, 1 False, exit code 0, 7 min 58 s**. The scroll goal is met: 300 frames in 5 s, 1 over 20 ms (0.3 %), 0 over 33 ms, worst 20.7 ms. The one False line: "18: the clipboard holds the row's file (): False". See below |
| the live check, the laptop (the Omen), `run-2026-10-02-1130-rd-omen-laptop.txt` (`remote-livecheck.ps1`, the branch sent as a git bundle, this PC's Release builds copied) | **262 True, 0 False, exit code 0, 8 min 57 s**. The panel goal, which judges the laptop's speed, is met: 267 frames in 5 s, 0 with UI work over 20 ms (0.0 %), 0 over 33 ms, 7 over 16.7 ms. The gaps between frames are not the verdict on the laptop (48 over 20 ms, 30 over 33 ms, worst 104.6 ms, machine CPU 5.2 %); one check is "not judged on a panel", neither True nor False. All three new lines of section 21 are True |
| the full window suite with the end-to-end tests, the laptop, `tests-2026-10-02-1140-rd-omen-laptop.txt` (`remote-tests.ps1 -EndToEnd`) | **1331 of 1333**, 4 min 38 s. The two failures are known timing tests that fail on this machine under the suite's load, and neither is in the terminal: `ColumnViewEndToEndTests.Three_levels_open_close_and_come_back_as_columns_and_each_dropped_column_releases_its_listing` (the flakes report lists it as not taken, "True" expected, "False" seen) and `ContextMenuEndToEndTests.The_menu_is_edited_inside_the_menu_and_saved_through_the_core` ("File menu" expected, "" seen). All three new terminal tests passed there |

The one False line of the run on this PC is in section 18, not in the
terminal: after a click on Windows' own "Copy" in the shell's menu the step
waits 500 ms and reads the clipboard's file list, and it was empty. The core
ran the item (the line before is True). It is the same kind of fault as the
paste check in unit 3's first run: another program held or had not yet
filled the clipboard (the VirtualBox shared clipboard runs on this PC). The
unit does not touch the menu, the clipboard or Windows' menu.

A second run on this PC, to see the False line go, was not made: `wait-for-pc.ps1`
twice refused to start it (2 and 5 minutes), because another agent's
CabinetOS window (unit 6's worktree) was running on this PC, which is what the
gate is for. The same step is True on the laptop (262 True, 0 False) and was
True in the unit 4 runs; the creator may run it again with `run-livecheck.ps1`.

The section 21 lines, all True: the window logged "terminal layout saved"
(11 times, the last with 2 sessions); `cabinetos.json` has `terminal.tabs`
with a session for each pane, the tab in front and the folders; and after
the window closed the layout was still in the file (2 sessions).

The new tests, in short:

- `TerminalRestoreTests` (18): restore or adopt or nothing for every setting of `terminal.restore`, window sessions, core sessions and saved layout; the plan for a gone profile (fallback and its record), a missing folder, more than 32 sessions; the retry after `spawn_failed` and `not_linkable` and no retry for other codes or when the folder already is the profile folder; the order in which tabs come to the front.
- `TerminalLayoutTests` (4): the reading from the config (lenient) and the writing as JSON (a null folder and an out-of-range index are left out).
- The core: the config's reading of `restore`, `defaultMode`, `tabs`; an index past the end is refused with the key's name; a round trip through the file; `set_value` writes the three keys; a session with no mode starts in the default mode and a non-linkable profile stays locked; `gui_context` with `dual` false and with it absent.
- The CLI: with one pane shown, `opposite_pane` is exit 1 with the one line, no job starts, and a named path copies.
- The window, end to end (`TerminalEndToEndTests`, real windows, real shells): (1) A first window opens a linked PowerShell in the left pane, a locked one in the right pane that goes to a folder of its own, and a cmd in the right pane, with the right pane's PowerShell in front; the file gets the layout, in the order of the tabs, and the window closes. A second window starts on the same file, and its first Ctrl+Backquote brings the three back as they were (profile, folder, pane, mode, order, the tab in front), with one "terminal session restored" each and one "terminal restored". (2) With `terminal.restore` false and a saved layout in the file, nothing comes back and the first Ctrl+Backquote starts one session in the `terminal.defaultMode` the file says. (3) A file with five saved sessions: a profile that is gone, a folder that is gone, a cmd saved as linked, one that is as it was, and one whose program is not there. The first three fall back (to the default profile, to the home folder, to locked), the fourth is as it was, the fifth is skipped with a warning, and the saved front tab is still the front tab.

## Decided without you

Each line: what, because, how to undo.

- **`dual` is true when the field is missing on the wire.** Because a window that does not send it (an older one, a test's played window) must not make `opposite_pane` fail. Undo: remove the `serde(default)` of `WindowState.dual` and the manual `Default` in `window.rs` of the protocol.
- **The refusal is the CLI's, in `Context::destination`; the core only reports `dual`.** Because `opposite_pane` is a word of the command line and the CLI resolves it already. Undo: none needed; a plugin that wants the rule reads `dual` itself.
- **The saved layout is the key `terminal.tabs`, with `items`, `front`, `shown`.** Because the handout said the same place and mechanism as the window's other restart state, and that is a key of `cabinetos.json` written by `set_value`. It sits in the `terminal` section rather than in `ui`, since it is the terminal's and the core must validate it. Undo: move the type to `UiConfig`; the schema regenerates.
- **The core checks `terminal.tabs` strictly (an index must name a saved item, unknown fields refused), the window reads it leniently (an item with no profile is dropped).** Because a hand edit must be told what is wrong with the key's name, but a file the core already accepted must never keep the window from starting. Undo: `check_terminal_tabs` in `parse.rs`.
- **The layout is saved also when `terminal.restore` is false.** Because turning the key on later then brings back the last layout, and the file is the user's to read. Undo: skip `SaveTerminalLayoutAsync` when `UiSettings.TerminalRestore` is false.
- **The save waits one second after a change, and nothing is written before the dock was first shown or while a restoration runs.** Because the tab changes come in bursts (a restoration opens several sessions), and an empty window would otherwise wipe the saved tabs before they were used. Undo: `_terminalSaveTimer` and the guards in `SaveTerminalLayoutSoon`.
- **The core's own sessions win over the file: when the core runs sessions the window does not show, the window shows those and restores nothing from the file.** The handout said "win" without saying how; this is the reading that never doubles sessions. Another reading is that the file fills in what the core lacks; I picked neither as the product's final word, only the one that cannot start a second set. Today the window always starts its own core and the core ends with it, so a core never outlives its window and this path has only a unit test (`Decide`). Undo: `TerminalRestore.Decide`.
- **A core that stops and is started again by the window re-arms the restoration.** Because the shells ended with the core and the first show after that should bring back the tabs the stopped core had. Undo: remove the `ArmTerminalRestore()` call in `OnCoreLostAsync`.
- **Two windows share `terminal.tabs`: the last write wins.** Because `ui.tabs` already works so, and a new window has its own core and its own sessions. Undo: none cheap; it would need a key per window.
- **The saved front tab wins over the pane the user pressed, at the first show.** The first Ctrl+Backquote comes from a pane, and the pane's own tab is not necessarily the saved front one. A pressed pane could take the keyboard to its own tab instead. I chose the saved layout, since a restart that changes the view looks like a loss. Undo: `TerminalRestore.ShowOrder`.
- **A gone folder is retried once in the user's profile folder after the core refuses with `spawn_failed`; the window reads no disk.** Because Prime Directive 1 (no file I/O in the UI) and Article 1 forbid a folder check on the UI thread, and the core already refuses a missing folder. The cost is one failed `terminal_open` per gone folder. Undo: `TerminalRestore.Retry`.
- **At most 32 sessions are restored.** Because it is the core's limit; the log says so. Undo: `TerminalRestore.MaxSessions`.
- **`terminal.defaultMode` is read by the core at each `terminal_open` that names no mode, and a non-linkable profile stays locked.** Because the handout said so, and because a default of `linked` must not fail a `cmd` session with `not_linkable`. The window's own paths (Ctrl+Shift+T, Ctrl+Backquote) name no mode, so they take it. Undo: `default_mode` in `terminal.rs`.
- **The default conflict policy `skip` for `--selection` stays**, recorded as confirmed in terminal.md. Undo: none; the handout confirmed it.
- **The live check has no restoration check.** Because it starts one window and never restarts it, which the handout allowed. The three end-to-end tests restart windows. Undo: none needed.
- **The unit's work is five commits and the docs, not one per handout piece.** The repository has no way to stage a file by hunks without an interactive tool, and the window's save and restoration share files. Undo: none.
- **The laptop runs are part of the checks**, because the creator's rule of 2026-10-02 says the laptop's numbers are the market's numbers.

## Needs you

- **The "core's own sessions win" reading** (above). Today it cannot happen, so it costs nothing; it matters if a later unit lets a core outlive its window (a background mode). Say which reading you want then.
- **The saved front tab against the pressed pane** at the first show (above). The creator may prefer the other.
- **The desk's card "Development Plan (mirror of the repo plan)" is not refreshed.** The project's CLAUDE.md asks for it when `docs/PLAN.md` changes, but it is a write to Notion, an outside service, which the handout's hard stops forbid, and PLAN.md changes only on this branch until it is merged. Refresh it after the merge.
- **Nothing in this unit needs a decision to merge.** Nothing was pushed or merged.

## Seen on the way

- Two end-to-end tests fail by turns under the whole suite's load and pass alone and in a second full run: `RailEndToEndTests.Every_way_of_picking_a_folder_in_the_sidebar_runs_go_toPath_once` ("snapshot click: no shown button has that name") and `ContextMenuEndToEndTests.The_menu_opens_with_its_corner_at_the_point_and_flips_before_the_windows_edge` (6 against 5, the second is one of the known flakes). They are not in the list of the flakes report; the first is new to me.
- `cab pane --right` with only one pane shown still prints the hidden pane's folder. That is true (it is the folder the right pane has), but a script that reads it may not expect it. Out of scope.
- `cab state`'s table does not show `dual`; the JSON does. Out of scope.
- The live check's section 18 step "the clipboard holds the row's file" reads the clipboard 500 ms after the click, with no wait for the clipboard to fill. It failed once here and the same way in unit 3's paste check; a wait loop (as the other steps have) would end it. Out of scope.
