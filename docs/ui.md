# The WinUI 3 shell

`CabinetOS.exe` is the window: design views A (the main workspace) and B
(the command palette) of [design/README.md](design/README.md). It draws
pixels and captures keystrokes, and nothing else (brief §1, the Dumb UI
Rule). It starts its own `cabinetos-core.exe`, asks it for every listing,
setting and command, and reads directory listings straight from the core's
shared memory. Constitution Articles 3 (native look), 4 (clean by default),
5 (dual pane) and 7 (keyboard and palette).

The code is in `ui/`, a C# solution on .NET 10 and the Windows App SDK 2.5
([ADR 0001](decisions/0001-frontend-language-csharp.md)). The protocol is in
[ipc.md](ipc.md); keys and chords in [keybindings.md](keybindings.md).

## At a glance

Measured on 2026-09-28 on the development PC (Windows 11 25H2, debug builds
of the UI and the core):

| What | Result |
|---|---|
| Start to both panes shown (profile and Documents) | see "Live check" in the Phase 5 report |
| Enter on a folder of 100,000 entries, until its first rows are drawn | see "Live check" in the Phase 5 report |
| Scrolling that folder with PageDown held for 5 s | see "Live check" in the Phase 5 report |
| Closing the window, until the core has exited | 0.4 s |

## The solution

| Project | What it is |
|---|---|
| `ui/CabinetOS.Core` | Everything that runs without a window, so it is unit-tested: the pipe client, the protocol types, the core launcher, `ListingView` (the shared-memory reader), keys and the chord state machine, the `CommandRouter`, the settings the window reads, display formatting, and diagnostics. |
| `ui/CabinetOS` | The WinUI 3 app, `CabinetOS.exe`: the window, the panes, the sidebar, the palette. |
| `ui/CabinetOS.Tests` | xunit v3 tests of `CabinetOS.Core`, including an end-to-end run against the real core. |

Shared settings: `ui/Directory.Build.props` (target
`net10.0-windows10.0.22621.0`, Windows 11 22H2 as the floor per
[ADR 0004](decisions/0004-minimum-windows-11-22h2.md), x64, warnings are
errors), `ui/Directory.Packages.props` (every package version in one
place) and `ui/global.json` (the .NET SDK, and the test mode of
`dotnet test`). Package versions: [dev-setup.md](dev-setup.md), "Phase 5".

## Building, running, testing

Everything below runs from a WSL terminal; `dotnet.exe` and `cargo.exe` are
the Windows tools ([dev-setup.md](dev-setup.md)).

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core && cargo.exe build -p cabinetos-core
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/ui && dotnet.exe run --project CabinetOS
```

The window needs the Windows App Runtime 2.5 (x64) on the machine; it is
used from there, not copied next to the program (see "Decisions" in the
Phase 5 report). The tests run from `ui/`, where `global.json` switches
`dotnet test` to the Microsoft.Testing.Platform mode that xunit v3 needs:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/ui && dotnet.exe build CabinetOS.sln && dotnet.exe test --solution CabinetOS.sln --no-build
```

The end-to-end test starts the real core. It runs when `CABINETOS_CORE_EXE`
is set or the core is built in `core/target`, and skips itself otherwise
(as in the CI job `ui`, which builds no core).

### Environment variables

| Variable | Effect |
|---|---|
| `CABINETOS_CORE_EXE` | The core to start, as a full path (first in the launcher's order, below) |
| `CABINETOS_LOG_DIR` | Where `ui.<date>.jsonl` and crash traces go; the core it starts inherits it |
| `CABINETOS_LOG` | The level filter, in the core's syntax: `debug`, or `info,cabinetos_ui::pipe=trace` |
| `CABINETOS_CONFIG` | Not read by the UI; the core it starts inherits it and uses that `cabinetos.json` |
| `CABINETOS_UI_FRAMESTATS=1` | Logs one `frame stats` line per second: frames drawn and the longest gap between two |
| `CABINETOS_UI_SNAPSHOT=<folder>` | Development aid: once the first folders are shown, renders the window to `window.png` there; with `CABINETOS_UI_SNAPSHOT_QUERY=<text>` it then opens the palette with that text and renders `palette.png`. It draws the window's own content, so it works when the screen is off or locked; Mica is not part of that content and comes out transparent. |

### Logs and crashes

The UI writes `%LOCALAPPDATA%\CabinetOS\logs\ui.<UTC date>.jsonl` in the
format of [diagnostics.md](diagnostics.md), with `boundary: "frontend"`, on
a background thread with a bounded, lock-free queue; it keeps 14 daily
files, as the core does. Targets are `cabinetos_ui::<area>`: `app`,
`session`, `launcher`, `pipe`, `commands`, `keys`, `pane`, `palette`,
`shell`, `frames`, `xaml`. The UI thread is named `ui`.

Every request the UI sends is logged under its request ID (`request sent`,
then `reply received` or `request failed`). A command started by a key or a
button keeps one ID from the key press into the core: the router logs
`command executed` under a new ULID, and when the command lists a folder,
the `list_directory` request carries that same ID, which the core logs too.

A crash writes `crash-<timestamp>.json` in the core's format with
`boundary: "frontend"`, `process: "ui"` and the last 256 log lines, then
flushes the log. The hooks: `Application.UnhandledException`,
`AppDomain.UnhandledException`, and a catch around WinUI's own start,
because a failure there ends the process without raising either event.
`CabinetOS.exe --self-test-crash` throws on the UI thread after start-up,
to see one.

## Starting the core

The launcher looks for `cabinetos-core.exe` in this order:

1. `CABINETOS_CORE_EXE`;
2. next to `CabinetOS.exe`;
3. when running from the repository: walking up from `CabinetOS.exe` to the
   folder that holds `core/Cargo.toml`, then `core/target/debug/` and
   `core/target/release/`.

It starts the core with `--pipe <16 random hex digits> --parent-pid <its own
process ID>`, retries the pipe for up to 10 s (failing at once if the core
exits first), and says `hello` with its real process ID and
`client_name: "CabinetOS.exe"`. A core that cannot be found or started shows
a dialog with the places it looked and the core's last error lines, with
"Try again". If the core stops later (it stops on any panic), the window
starts a new one, at most three times a minute, and lists both folders
again.

Closing the window hides it at once, sends `shutdown`, closes the pipe
(the core waits up to 2 s for open connections before it exits, so keeping
it open would cost that), and waits up to 2 s for the process. A core that
stays is ended by its parent-process watch when the window's process ends.

## Listings in shared memory

`ListingView` maps the section handle of `listing_opened` or
`listing_refreshed` read-only and checks the header first: magic `CBLS`,
layout version 2, every part inside the section and aligned. Then, per
index: `Id`, `Kind`, `Flags`, `NameSpan(i)` (a `ReadOnlySpan<char>` straight
into the UTF-16 name arena, no copy), `Name(i)` (a string made on demand),
`Size`, `Modified`, `Created`, `Accessed` (UTC from FILETIME), `Attributes`,
`IsFolder`. The C# structs mirror the byte diagram of [ipc.md](ipc.md), and
a test pins every size and offset.

- The view owns the handle: `Dispose` unmaps and closes it. After that,
  every accessor returns an empty value instead of reading unmapped memory.
- The pipe client wraps each section handle the moment its message
  arrives, so a handle nobody takes is still closed; a listing whose
  request was cancelled is closed on both sides.
- It is the UI's only hand-written `unsafe` code, one short block per read,
  each with the reason it is in bounds.

A pane shows the rows in an `ItemsRepeater` with a virtualizing
`StackLayout`, 30 px per row, over `ListingRows`: a read-only list that
makes a small row object (the view and an index) only when the repeater
asks for that index. A folder of 100,000 entries has as many row objects
as there are rows on screen. Each row reads its name, kind, time, type and
size from shared memory when it is shown.

Each pane lists with `watch: true`. A `listing_refreshed` swaps the view:
the new section is mapped, the rows are replaced, the selection follows the
selected entry's ID, and the old view is released. `listing_lost` moves
the pane to the nearest parent the core can list.

The core sorts; the columns are fixed in this version. `panes.showHidden`
and `panes.sort` apply because the UI leaves them out of `list_directory`;
when either changes in `cabinetos.json`, both folders are listed again.

## Keys, contexts and commands

Every key press goes first to the window (`PreviewKeyDown`), then to the
`ChordStateMachine` with the keymap from `get_keymap`
([keybindings.md](keybindings.md), "Chords"):

- A binding runs its command through the router; the first half of a chord
  waits up to `chord_window_ms` and the status bar says so
  ("Ctrl+K was pressed. Waiting for the second key…"); a second half that
  completes nothing runs nothing and is reported.
- Contexts: `filesView` (a file pane has the focus), `paletteOpen`,
  `textInput` (a text box has the focus). A binding with a `when` that holds
  wins over one without.
- Text boxes come first: while one has the focus, a binding without `when`
  applies only if it is in the Immutable System Tier (`palette.show`,
  `overlay.close`, `keys.open`). So typing is never taken over by a
  shortcut, and Esc and Ctrl+Shift+P always work.
- A key nobody bound goes on to the focused control: the arrows, Home, End,
  PageUp and PageDown move the pane's selection like in any list.

The `CommandRouter` is the one place every button, key, crumb, sidebar row
and palette row goes through. A command the registry marks `target: ui`
runs its handler in the window; any other goes to the core as
`execute_command`, whose `command_result` is shown in a dialog and whose
error in the status bar. Each run is logged with its own ULID.

Five commands exist only in the UI, because the core's registry does not
list them yet: `go.back`, `go.forward`, `go.up`, `pane.openSelected` (Enter
or a double-click on a folder) and `keys.rebind` (the pencil). They run
through the router like the others, but they are not in the palette and
cannot be rebound until the core registers them.

| Command | In this version |
|---|---|
| `palette.show`, `overlay.close` | Open and close the palette; Esc also leaves the address box |
| `keys.open` | Opens the palette: it lists every command with its keys and edits them |
| `view.toggleDualPane`, `view.toggleSidebar`, `view.focusOtherPane` | As named |
| `go.toPath` | With `{"path": …}` goes there; without, turns the crumbs into a text box |
| `help.about` | Runs in the core; the result is shown in a dialog |
| `file.*` | Go to the core, which answers `not_implemented`; the status bar says so |
| `view.toggleTerminal`, `marketplace.browse`, `workspace.switch`, `preferences.selectColorTheme`, `terminal.runTask` | "arrives in a later version" in the status bar |

## The command palette

Ctrl+Shift+P, the ⋮ button and the status-bar keycap open it (design view
B): a transparent scrim over the whole window, a 640 px panel with in-app
Acrylic, and the design's 160 ms entrance. What the user types goes to the
core as `search_commands` after 30 ms of quiet; the UI ranks nothing
itself. Up and Down move the highlight, Enter or a click runs the command
through the router and closes the palette, a click on the scrim or Esc
closes it.

Rebinding: the pencil (or F2 on the highlighted row) starts recording. The
next key that is not a modifier forms a combination; a second one within
1000 ms makes a chord and ends the recording at once; otherwise it ends
after 1000 ms without a key. Esc cancels. The keys go to the core as
`set_keybinding`, which writes `cabinetos.json`; its answer is the new
keymap. A refusal (`immutable_binding`, `keybinding_conflict`, …) is shown
in the row. Commands of the Immutable System Tier show a lock instead of a
pencil.

## Not in this version

| What | Why |
|---|---|
| The search field | In place and disabled, with the design's placeholder; file search comes with its phase |
| The terminal pane and toggle | The toggle is disabled; Phase 8 has the core side |
| The marketplace button | Disabled; Phase 9 |
| Jobs (copy, move, delete) in the UI | `job_progress` is parsed only; the jobs UI comes later |
| Workspaces (title-bar tabs, sidebar section) and Tags | One static "Default" tab; both sidebar sections stay hidden (Article 4) |
| Drives in the sidebar | Built against `list_volumes`, which the core does not answer yet; the section stays hidden until it does |
| Opening files | Enter and double-click open folders only |
| Remembering the last folders and the dual/single state | Kept in memory: the core has no request that writes a setting other than a key binding |
| Sorting by a column | The column headers are static; the order is `panes.sort` from `cabinetos.json` |
| Shell type names and icons | The Type column shows `EXT File`; the core does not send shell type names yet |
