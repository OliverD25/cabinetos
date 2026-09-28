# The WinUI 3 shell

`CabinetOS.exe` is the window: design views A (the main workspace), B (the
command palette), D (the context menu) and E (the file operations flyout)
of [design/README.md](design/README.md). It draws pixels and captures
keystrokes, and nothing else (brief §1, the Dumb UI Rule). It starts its
own `cabinetos-core.exe`, asks it for every listing, setting, command and
file operation, and reads directory listings straight from the core's
shared memory. Constitution Articles 1 (the work runs in the core), 3
(native look), 4 (clean by default), 5 (dual pane), 6 (settings in the
file) and 7 (keyboard and palette).

The code is in `ui/`, a C# solution on .NET 10 and the Windows App SDK 2.5
([ADR 0001](decisions/0001-frontend-language-csharp.md)). The protocol is in
[ipc.md](ipc.md); keys and chords in [keybindings.md](keybindings.md).

## At a glance

Measured on 2026-09-28 on the development PC (Windows 11 25H2, release
builds of the UI and the core). The screen was locked during these runs,
and Windows then draws only about 33 frames per second, so a time that ends
at a drawn frame can be up to 30 ms shorter on an unlocked screen:

| What | Result |
|---|---|
| Start of `Main` to both panes shown (profile and Documents) | 0.82–0.87 s |
| Going to a folder of 100,000 entries, until its first rows are drawn | 82 ms: the core lists it in 38 ms, the reply arrives at 39 ms, the first row is made at 61 ms |
| Scrolling that folder with PageDown held for 5 s | not measured yet: it needs an unlocked screen |
| Closing the window, until the core has exited | 0.4 s |

## The solution

| Project | What it is |
|---|---|
| `ui/CabinetOS.Core` | Everything that runs without a window, so it is unit-tested: the pipe client, the protocol types, the core launcher, `ListingView` (the shared-memory reader), the type-name and icon caches, the selection, keys and the chord state machine, the `CommandRouter`, the job client (`TransferCenter`, `ConflictQueue`), the terminal's byte pump, page protocol and folder-sync rule, the dock's size rules, the search model, the in-app clipboard, the settings the window reads and writes, display formatting, and diagnostics. |
| `ui/CabinetOS` | The WinUI 3 app, `CabinetOS.exe`: the window, the panes, the sidebar, the palette, the context menu, the transfer flyout, the Tool Dock with the terminal, and `Assets/xterm` (the terminal page and xterm.js). |
| `ui/CabinetOS.Tests` | xunit v3 tests of `CabinetOS.Core`, including end-to-end runs against the real core. |

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
used from there, not copied next to the program (why, and how to install
it: [dev-setup.md](dev-setup.md), "Phase 5"). The tests run from `ui/`, where `global.json` switches
`dotnet test` to the Microsoft.Testing.Platform mode that xunit v3 needs:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/ui && dotnet.exe build CabinetOS.sln && dotnet.exe test --solution CabinetOS.sln --no-build
```

The end-to-end tests start the real core: a listing read from shared
memory; 200 files copied through `TransferCenter` with one conflict
answered Skip, which must end `completed` with that file untouched; a
folder made and renamed; the type names and icon keys of a listing's rows,
and the icon PNGs at every offered size; and a `cmd` session whose echo
comes back through the terminal's byte pump. They run when
`CABINETOS_CORE_EXE` is set or the core is built in `core/target`, and
skip themselves otherwise (as in the CI job `ui`, which builds no core).

### Environment variables

| Variable | Effect |
|---|---|
| `CABINETOS_CORE_EXE` | The core to start, as a full path (first in the launcher's order, below) |
| `CABINETOS_LOG_DIR` | Where `ui.<date>.jsonl` and crash traces go; the core it starts inherits it |
| `CABINETOS_LOG` | The level filter, in the core's syntax: `debug`, or `info,cabinetos_ui::pipe=trace` |
| `CABINETOS_CONFIG` | Not read by the UI; the core it starts inherits it and uses that `cabinetos.json` |
| `CABINETOS_UI_FRAMESTATS=1` | Logs one `frame stats` line per second: frames drawn and the longest gap between two |
| `CABINETOS_UI_SNAPSHOT=<folder>` | Development aid: once the first folders are shown, runs the steps of `CABINETOS_UI_SNAPSHOT_STEPS` and renders the window to PNG files in that folder. It draws the window's own content, so it works when the screen is off or locked; Mica and dialogs (a popup layer) are not part of that content. WebView2 pages (the terminal) draw outside that content: each one on screen is captured by WebView2 (`CapturePreviewAsync`) and laid over its place, which also works on a locked screen. |
| `CABINETOS_UI_SNAPSHOT_STEPS` | The steps, separated by `;` (default `shot:window`): `cmd:<command> [json]` runs a command through the router and waits for it; `cmd-nowait:<command>` runs one that waits for the user (a dialog, a rename); `path:<folder>` goes there in the active pane; `pane:0` or `pane:1` makes a pane active; `select:<name>` selects a row; `selectall`; `menu:<name>` opens the context menu on a row (`menu:*` on the empty space); `rename:<text>` types into the rename box and presses Enter; `dismiss` closes a dialog; `type:<text>` types into the palette; `search:<text>` types into the search field; `terminal:<text>` types into the shown shell (`{enter}` is Enter); `crash:terminal` ends the terminal page's browser process; `until:running`, `until:conflict`, `until:terminal` or `until:search` waits for a job, a shell or an answer; `wait:<ms>`; `shot:<name>` writes `<name>.png`. Example: `pane:0;select:report.txt;cmd:file.copyToOtherPane;until:conflict;shot:conflict`. One quirk: a check box always shows a dash there, checked or not (the bitmap draws the first frame of WinUI's animated check mark). |

### Logs and crashes

The UI writes `%LOCALAPPDATA%\CabinetOS\logs\ui.<UTC date>.jsonl` in the
format of [diagnostics.md](diagnostics.md), with `boundary: "frontend"`, on
a background thread with a bounded, lock-free queue; it keeps 14 daily
files, as the core does. Targets are `cabinetos_ui::<area>`: `app`,
`session`, `launcher`, `pipe`, `commands`, `keys`, `pane`, `palette`,
`jobs`, `settings`, `shell`, `frames`, `xaml`, `icons`, `terminal`,
`webview`, `snapshot`. The UI thread is named `ui`.

Every request the UI sends is logged under its request ID (`request sent`,
then `reply received` or `request failed`). A command started by a key or a
button keeps one ID from the key press into the core: the router logs
`command executed` under a new ULID, and the request the command makes
(`list_directory`, `start_job`, `open_path`, `rename`, `create_directory`,
`job_control`, `resolve_conflict`) carries that same ID, which the core
logs too. For a copy, the UI adds `job started` with the job's ID; the
job's own events carry that job ID from then on.

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
as there are rows on screen. Each row reads its name, kind, time and size
from shared memory when it is shown.

The Type column and the icons are the shell's, asked for through the core
(`describe_entries` and `get_icon`, protocol 9; [ipc.md](ipc.md), "Type
names and icons"):

- When the repeater makes rows, the pane asks for the pages of 128
  entries that cover them: one request per layout pass, each page once
  per listing and section generation. A refresh brings a new generation,
  and the pages are asked for again.
- Until a row's page arrives, it shows what the core said about the same
  extension before (both panes share that memory; not for `.exe`, `.ico`
  and `.lnk`, which have icons of their own), else the built-in
  `EXT File` and a Segoe Fluent glyph.
- Icons are kept by key and size for the window's life, one request per
  key. The size is the smallest the core offers that covers 16 px at the
  screen's scale: 16 at 100 %, 24 up to 150 %, 32 up to 200 %, 48 above.
  On a screen with another scale, rows ask for the new size and show the
  old one until it arrives.
- A core before protocol 9 answers `unknown_request`; the built-in text
  and glyphs then stay, until a restarted core is asked again.

Each pane lists with `watch: true`. A `listing_refreshed` swaps the view:
the new section is mapped, the rows are replaced, the selection follows its
entries by ID, and the old view is released. A name the pane waits for (a
new folder, a rename) is selected when a refresh lists it, by name, because
on a file system without file IDs the ID is a hash of the name and changes
with it. `listing_lost` moves the pane to the nearest parent the core can
list.

The core sorts; the columns are fixed in this version. `panes.showHidden`
and `panes.sort` apply because the UI leaves them out of `list_directory`;
when either changes in `cabinetos.json`, both folders are listed again.

The sidebar's Drives section shows `list_volumes`: each drive's name, free
space and a 3 px usage bar, in the core's order. `volumes_changed` replaces
the list when a drive letter comes or goes (a USB stick, a mapped share).
An older core that answers `unknown_request` leaves the section hidden.

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
  shortcut, and Esc and Ctrl+Shift+P always work. A text box inside a pane
  (the rename box) is text input, not `filesView`: F5 does not start a
  copy while a name is being typed.
- A key nobody bound goes on to the focused control. In a pane, that is
  the list keys every Windows list has (the rows marked "the pane" below);
  they are not commands.

| Keys in a pane | What they do | From |
|---|---|---|
| Up, Down, Home, End, PageUp, PageDown | Move the focus; only the new row is selected | the pane |
| the same with Shift | Select from the anchor to the new row | the pane |
| the same with Ctrl | Move the focus and keep the selection | the pane |
| Backspace | `go.up` | the pane |
| Shift+F10, the Menu key | The context menu of the focused row | the pane |
| Insert | `edit.toggleSelection`: select or unselect the focused row and move down (Total Commander) | keymap |
| Ctrl+A | `edit.selectAll` | keymap |
| Enter, double-click | `pane.openSelected`: a folder opens in the pane, a file in its default application | keymap |
| Ctrl+Enter | `file.openInOtherPane` (folders) | keymap |
| Alt+Enter | `file.properties` | keymap |
| Alt+Up, Alt+Left, Alt+Right | `go.up`, `go.back`, `go.forward` | keymap |
| Tab | `view.focusOtherPane` | keymap |
| F2 | `file.rename` (in the palette, F2 is `keys.rebind`) | keymap |
| Delete, Shift+Delete | `file.delete` to the Recycle Bin; `file.deletePermanently`, after a dialog | keymap |
| Ctrl+X, Ctrl+C, Ctrl+V | `edit.cut`, `edit.copy`, `edit.paste` | keymap |
| F5, F6, F7 | `file.copyToOtherPane`, `file.moveToOtherPane`, `file.newFolder` | keymap |

The keys marked "keymap" are the core's defaults; each can be rebound in
the palette or in `cabinetos.json`.

Click selects one row, Ctrl+Click adds or removes one, Shift+Click selects
a range. A right-click inside the selection keeps it; outside, it selects
that row. A command acts on the selected rows, or on the focused row when
none is selected. The status bar says "1 selected · name", or "12
selected, 1.4 MB": the bytes of the selected files, from the listing
(folders have no size there).

The `CommandRouter` is the one place every button, key, crumb, sidebar row,
palette row, menu row and flyout button goes through. A command the
registry marks `target: ui` runs its handler in the window; any other goes
to the core as `execute_command`, whose `command_result` is shown in a
dialog and whose error in the status bar. Each run is logged with its own
ULID. Since protocol 9 the registry lists the shell's own commands
(navigation, file, edit, search, `keys.rebind`) with `target: ui` and
their keys, so each of them is in the palette and can be rebound; the
window registers a handler for each one (`RegisterUiHandler`).
`file.properties` takes `{"scope": "folder"}` for the folder itself.

These commands exist only in the UI, because they belong to the window's
own controls: `transfer.pause`, `transfer.resume`, `transfer.cancel`,
`transfer.close`, `transfer.minimize`, `transfer.restore`,
`transfer.next`, `conflict.resolve`, `sidebar.pin`, `sidebar.unpin`, and
the terminal's `terminal.new` (`{"profile": …, "cwd": …}`),
`terminal.show` and `terminal.close` (`{"session": …}`) and
`terminal.reload`, and the search's `search.scope`
(`{"wholeVolume": true}`) (`RegisterLocal`). They run through the router
like the others, but they are not in the palette and cannot be rebound.

| Command | In this version |
|---|---|
| `palette.show`, `overlay.close` | Open and close the palette; Esc closes, in order, the palette, the context menu, a rename, the address box, the search results |
| `search.focus` | Puts the keyboard in the search field ("Search") |
| `keys.open` | Opens the palette: it lists every command with its keys and edits them |
| `view.toggleDualPane`, `view.toggleSidebar`, `view.focusOtherPane` | As named; the first two are saved in `cabinetos.json` |
| `go.toPath` | With `{"path": …}` goes there; without, turns the crumbs into a text box |
| `help.about` | Runs in the core; the result is shown in a dialog |
| `file.copyToOtherPane`, `file.moveToOtherPane`, `file.newFolder` | Run in the window: a job, or a folder (see below) |
| `view.toggleTerminal` | Shows the terminal, gives the keyboard back to the pane, or hides it ("The terminal") |
| `marketplace.browse`, `workspace.switch`, `preferences.selectColorTheme`, `terminal.runTask` | "arrives in a later version" in the status bar |

## File operations

Copy, move and delete are jobs in the core ([jobs.md](jobs.md)); the
window starts them with `start_job` under the command's ULID and follows
their events. The window never copies or deletes anything itself, and
never computes progress: it draws the numbers the core sends.

- F5 and F6 copy or move the selection into the other pane's folder. With
  one pane shown, the status bar says to press Ctrl+Shift+D. No dialog
  asks first: the flyout shows the job at once, and Cancel stops it. The
  core checks the paths and refuses, for example, a folder copied into
  itself; its message goes to the status bar.
- Delete sends the selection to the Recycle Bin. Shift+Delete deletes for
  good after a dialog that names the count ("Delete 12 items
  permanently?"), with Cancel as the default button.
- Enter opens a folder in the pane, and a file with its default
  application (`open_path`). Before asking, the window lets the core bring
  a window to the front (`AllowSetForegroundWindow` with the core's process
  ID), because the core is a background process (ipc.md, "Opening files").
- Ctrl+Enter opens the focused folder in the other pane, and shows two
  panes first when only one is shown.
- Alt+Enter shows Properties from the listing's metadata: for one entry its
  type, folder, size in bytes, created, modified and accessed times, and
  attributes; for several, how many files and folders and the files' total
  size; for the folder itself (the empty space's menu), what it contains.
  There is no shell property sheet.
- A request the core does not know (`unknown_request`, an older core) hides
  the feature: the menu entry greys out and the log has one line.

### The transfer flyout and the pill

The flyout (design view E) sits bottom right, 380 px, with in-app Acrylic
and the design's 200 ms slide-up. It shows one job:

- the title: "Copying 12,480 items", "Copy paused", "Copy complete", "Copy
  complete, 3 failed", "Copy cancelled", "Copy failed"; "Moving…" and
  "Deleting…" likewise; "Waiting to copy" while the job waits for its
  disks, "Preparing to copy" while the core counts the files;
- "source → destination", cut with an ellipsis (the full text in a
  tooltip); the folder of the sources when there are several; "→ Recycle
  Bin" or "→ deleted for good" for a delete; the core's reason when the job
  failed;
- the speed graph: 56 px, the last 40 samples of `speed_bps` taken every
  500 ms, as an accent line over an area at 18 %. Its scale follows the
  fastest sample, never below 1 MB/s. A job without bytes to count (a
  delete, a move on one volume, empty files) draws a flat line;
- the progress bar: 4 px, its width follows the core's numbers over 400 ms
  on the compositor (an implicit scale transition);
- the footer: the percent (rounded down, so 100 % means done), "1.2 GB of
  2.7 GB" or "8,412 of 10,001 items", the time left or "Paused",
  Pause/Resume, and Cancel, which turns into Close when the job ends.

The minimize button folds the flyout into the status-bar pill: an
80 × 4 px track and "Copying · 45%". A click on the pill opens the flyout
again. The flyout never takes the keyboard focus, so the panes keep
working while a job runs.

Several jobs: the flyout shows the newest one, and a "N more" link in its
header cycles through the others, oldest last, then around. A job that
ends quietly (completed or cancelled) out of sight leaves by itself; one
that ends with errors or fails stays until closed, and a failure while
minimized opens the flyout. A job that ended quietly and was already seen
gives way when a new job takes the flyout. Jobs belong to the core, so the
flyout also shows jobs another client started: they are learned with
`list_jobs` and appear in the pill without taking the screen. At start, a
running job of an earlier session shows the same way.

### Conflicts

A `job_conflict` opens a card inside the flyout, and the flyout opens on
that conflict's job. One conflict shows at a time; the others wait in
order ("1 of 3 waiting"). The card names the file and what happened:
"already exists", "access denied", "is open in another program", "the
path is too long for the destination", "the destination disk is full; the
job is paused", "the Recycle Bin cannot take it (21 GB)", or the Windows
error. For a file that exists it shows both sides, the new one and the one
already there, each as size and time.

It offers only the decisions that fit the kind ([jobs.md](jobs.md),
"Conflicts"): Overwrite, Skip, Rename (the core picks "name (2).ext"),
Retry and Cancel job for a file that exists; Overwrite (it clears the
read-only attribute), Skip, Retry and Cancel job for access denied; Delete
permanently and Skip for the Recycle Bin; Retry, Skip and Cancel job for
the others. "Apply to all of this kind" sends `apply_to_same_kind`, and the
job's other waiting conflicts of that kind leave the queue with it. The
decision goes out as `resolve_conflict` under the command's ULID; the card
moves on once the core took it, or when the core no longer knows the
conflict (another client decided). After `hello` the core sends every
waiting conflict again; the queue keys them by `conflict_id`, so none
shows twice. A job that ends takes its conflicts along.

### The clipboard

Ctrl+X and Ctrl+C put the selection's paths on an in-app clipboard, marked
cut or copied. Windows' clipboard gets the same paths as text, one per
line (`DataPackage.SetText`, with the requested operation Move or Copy), so
they can be pasted into a terminal or an editor. Ctrl+V in a pane starts a
copy job for copied paths, or a move job for cut ones, into that pane's
folder; cut paths are used once and then leave the clipboard. Before a
paste, the window reads Windows' clipboard: if something else was copied
since, anywhere, the in-app paths are dropped and nothing is pasted, as
with Windows' own clipboard. When Windows' clipboard was busy and never
got the paths, this check is skipped. Files copied in Explorer are not
pasted in this version.

### The context menu

A right-click on a row opens the menu (design view D) at the pointer,
kept inside the window: in-app Acrylic, 260 px, the design's 120 ms
entrance. Shift+F10 and the Menu key open it under the focused row. The
icon strip holds Cut, Copy, Paste, Rename and Delete. The rows: Open
(Enter), Open in other pane (Ctrl+Enter, folders), Copy to other pane (its
keys from the keymap, F5), Open in Terminal (a new shell in the row's
folder; no keys shown, because Ctrl+` toggles the terminal instead), then
"FROM PLUGINS" with every plugin command whose `when`
is `filesView`, with the plugin's name as a badge, then Properties
(Alt+Enter). A plugin command gets `{"path": …, "paths": […]}`: the
right-clicked entry and the selection, since plugins cannot read the
selection themselves yet. A click outside or Esc closes the menu.

A right-click on the pane's empty space opens a shorter menu: Paste, New
folder, "Pin this folder to the sidebar" (when it is not pinned yet), and
Properties of the folder.

### New folder and rename

F7 makes "New folder", or "New folder (2)" and so on when the listing has
that name, with `create_directory`. When another program takes the name
first, the core answers `already_exists` and the next free name is tried.
The new folder is selected as soon as the watched listing shows it, and
its name opens for editing.

F2 puts a text box over the focused row's name; for a file, only the part
before the extension is selected, as in Explorer. Enter or a click
elsewhere commits it with `rename`; Esc cancels. The name is trimmed, and
an unchanged name renames nothing. A refusal from the core (the name is
taken, or Windows could not open it again, such as `CON` or a trailing
dot) shows as a red note under the row for 3 s. The refresh that lists the
new name selects it.

## What the window remembers

Like every setting, the window's own state lives in `cabinetos.json`
([config.md](config.md)), written by the core with `set_value`:

| Setting | Written when | Read |
|---|---|---|
| `ui.dualPane`, `ui.sidebar` | The user toggles them | At start, and on every `config_changed` |
| `ui.lastPaths` | The window closes (at most 1 s is spent on it) | At start: the left pane opens the first, the right pane the second; a folder that is gone falls back to the first-start folders |
| `ui.pinned` | "Pin this folder to the sidebar", and "Unpin from sidebar" on a pinned row | At start and on `config_changed` |

The sidebar always shows Desktop, Downloads, Documents and the profile
folder; `ui.pinned` holds the folders the user added, shown after them,
and only those can be unpinned. While a toggle's own write is on its way,
a configuration the core sends meanwhile does not flip the view back. A
core without `set_value` answers `unknown_request`: the window then keeps
its state in memory and logs one line.

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

## Search

The field in the command bar (Ctrl+F, `search.focus`; placeholder
"Search {folder}") finds files and folders by name through the core
([ipc.md](ipc.md), "Search"; [indexer.md](indexer.md)). The core searches
and ranks; the window shows the hits in the core's order and filters
nothing.

- Typing sends `search` with the text, `limit: 100` and `root`: the
  active pane's folder. It goes out once 150 ms pass without another key.
  Enter sends it at once and moves the keyboard to the hits; Down moves the
  keyboard there without waiting.
- The hits replace the folder in the active pane, in the same rows: the
  name with its type's icon, the folder the hit is in (where a listing
  shows "Modified"), and the type name. The reply has paths only, so the
  type and icon are what the core said about that extension in a listing
  before, and there is no size or time. The pane's title reads
  "Search: {query} · {n} hits · {index or walk} · {time}", and its right
  side names what was searched.
- Under the title, a note says whether the answer is complete: complete,
  or incomplete because a walk stopped at its limit (2 s or 20,000
  entries) or a volume is still being indexed. With 100 hits it adds that
  only the first 100 are shown. When the source is `walk`, it adds that the
  index is not running, and that docs/indexer.md, "Running it", says how to
  start it.
- "Whole volume", beside the note, drops the root: the indexer searches
  every indexed volume (a walk starts at the folder listed last, or the
  profile folder), and the search runs again at once.
- Enter or a double-click on a hit opens its folder in the pane with the
  hit selected. Esc, from the field or the pane, leaves the search and
  shows the folder again. Going elsewhere in that pane (Backspace, a crumb)
  leaves it too. Typing while the other pane is active moves the search
  there.
- In search results, the file commands (F5, F6, F7, Delete, F2, Ctrl+X,
  Ctrl+C, Ctrl+V, Alt+Enter, Insert, Ctrl+A) only say "These are search
  results: Enter goes to a hit, Esc back to the folder." The listing's
  selection is out of sight, so they must not act on it. Hits have no
  context menu.
- An answer to an older request, or one that arrives after the search was
  left, is dropped (`SearchModel`, tested). The status bar counts the hits
  and shows the focused hit's path.

Measured on 2026-09-28 without the indexer: the core walked `docs/` in
1.4 ms (its `took_us`), and the reply reached the window 4.6 ms after the
request went out.

## The terminal

Ctrl+` (`view.toggleTerminal`), the terminal button in the command bar,
and "Open in Terminal" in a row's context menu open the terminal: the
first occupant of the Tool Dock (Constitution Articles 9 and 11). The core
runs the shells ([terminal.md](terminal.md)); the window draws them with
xterm.js in WebView2 and sends the keys back. Nothing starts before the
terminal is first shown: no WebView2 and no shell (Article 4).

Measured on 2026-09-28 (release builds, locked screen, four runs): the
first Ctrl+` has a running pwsh 0.48–0.54 s after the key, of which
0.35–0.41 s is creating WebView2. Once the page is loaded, opening a shell
(`terminal_open` and its tab) takes 35–44 ms.

- **Where it goes.** Under the panes when `ui.layout` is `classic` or
  `rail`: 30 % of the main column's height, at least 120 and at most
  240 px. Beside them when it is `right`: 32 % of the width, 220 to
  380 px. The 8 px gap before the dock is a splitter: a drag sets the
  size, at least the minimum above and at most what leaves the panes 160 px
  of height (320 px of width). The dragged size lasts until the window
  closes; there is no setting for it yet.
- **The header, 34 px.** One tab per shell: a green dot while it runs,
  the profile's name, and × to end it. "+" starts the default profile
  (`terminal.defaultProfile`); the arrow next to it lists every profile of
  `terminal.profiles`. The caption says whether the shell follows the
  active pane: "cwd synced to active pane · {folder}", "cwd not synced: a
  command is being typed", "cwd not synced: a full-screen program runs",
  or "pwsh exited with code 0" (shorter on the right). The × at the end
  hides the dock; the shells go on running.
- **Ctrl+`.** In a pane, it shows the terminal and gives it the keyboard;
  the first time, it starts the default profile in the active pane's
  folder. In the terminal, it gives the keyboard back to the pane. In a
  pane while the terminal is shown, it hides the terminal.
- **Open in Terminal** starts another shell in the row's folder (a file's
  own folder for a file).
- **Following the active pane.** When the active pane's folder changes,
  by navigation or by switching panes, the shown shell gets
  `terminal_sync_cwd` once 300 ms pass without another change. The core
  types the shell's own `cd` command and Enter, so the rule is about what
  that typing would break. The sync is skipped when a line is half typed
  (keys went in since the last Enter or Ctrl+C: the command would be
  added to that line), when a full-screen program runs (xterm.js shows the
  alternate screen: vim, less, a TUI would get the line), when the shell
  is in that folder already, or when it ended. A skipped sync is not tried
  again when the line is finished, because that line may be the user's own
  `cd`; the next change of the pane's folder tries again. Only the shown
  tab follows, and only while the dock is shown; when it is shown again,
  it catches up.
- **When a shell ends** (`terminal_exited`), its last output stays and a
  dim line "[exited with code N]" follows, the dot turns grey, and the
  caption names the code. After 3 s the tab closes (`terminal_close`);
  when the last tab closes, the dock hides.
- **Keys in the terminal** go to the shell, with these exceptions: a
  single combination bound with `when: terminalFocus`, and the keys of
  `palette.show` and `view.toggleTerminal` (the ways out), go to the
  window; Ctrl+C with text selected copies it (as in Windows Terminal),
  and Ctrl+V pastes (bracketed when the shell asked for it). Esc, Tab and
  chords such as Ctrl+K … stay in the shell, which needs them. The page
  hands the window's keys over as messages, and a key that also reaches
  the window through XAML is ignored there, so none runs twice. The
  palette opened from the terminal gives the keyboard back to it.
- **The core stops.** Its shells end with it, and the tabs close.

How it is built:

- One WebView2 holds every session, each an xterm.js terminal: the DOM
  renderer, Cascadia Code 12 px, line height 1.25 (the design's 1.6 is for
  the prototype's static lines and would cost a third of the rows), 5,000
  lines of scrollback, and `windowsPty` set for ConPTY with the Windows
  build number (so xterm.js reflows and scrolls the way ConPTY expects).
- The page is `ui/CabinetOS/Assets/xterm/terminal.html`, served from
  `https://terminal.cabinetos.example/`, a virtual host mapped to the
  folder next to `CabinetOS.exe`. `@xterm/xterm` 6.0.0 and
  `@xterm/addon-fit` 0.11.0 are copied there unchanged, with their MIT
  licenses and SHA-256 hashes ([the folder's
  README](../ui/CabinetOS/Assets/xterm/README.md)). Nothing comes from a
  CDN. xterm.js 6 does not implement win32-input-mode, so it ignores the
  pseudo-console's request for it, and Enter reaches the shell as `\r`.
- **The byte pump.** The window opens each session's byte pipe (the
  `pipe` of `terminal_opened`); a background task reads it into a buffer
  (`OutputCoalescer`). The UI thread sends that buffer to the page as
  base64 with `PostWebMessageAsString`, at most once every 16 ms per
  session (60 a second) and at most 192 KiB per message, so a shell that
  prints a million lines costs 60 messages a second. Keys come back
  through `WebMessageReceived`, as text (`input`) or base64 (`binary`,
  mouse reports), and go into the pipe in order. The fit addon measures
  the cells; a new size goes to the core as `terminal_resize`.
- **Messages** (`TerminalPageMessages`, tested): window to page `create`,
  `output`, `show`, `close`, `exited`, `focus`, `passKeys`, `theme`; page
  to window `ready`, `input`, `binary`, `resize`, `buffer` (the alternate
  screen came or went), `key`. Anything malformed, unknown or over 1 MiB
  is dropped.
- **Safety.** The page loads only from its virtual host. Every other
  navigation, frame, new window, download, permission and request, http
  and https included, is refused and logged. Browser keys (F5, Ctrl+F,
  Ctrl+P) go to the shell, not to the browser. Developer tools exist only
  in Debug builds. `WebViewHost` does all of this, for Tool Extensions too.
- **Crash isolation.** The terminal has a browser process of its own
  (user-data folder `%LOCALAPPDATA%\CabinetOS\WebView2\terminal`). When
  its page or browser process ends, the dock shows "The terminal stopped"
  with Reload, and the rest of the window goes on. Reload starts a new
  WebView2, draws each session again and has the pseudo-console repaint
  its screen (a resize by one column and back), so the screen and
  everything the shell did meanwhile come back. Output that arrived while
  the page was down is not replayed; the repaint shows the current screen.
  Checked on 2026-09-28 by ending the browser process
  (`CoreWebView2.BrowserProcessId`, with the snapshot step
  `crash:terminal`): the dock said so, the other pane went on navigating,
  Reload brought back the screen of the same pwsh, and it took new input.
- **Logs.** Target `cabinetos_ui::terminal`: "terminal session opened",
  "terminal shell exited", "cwd sync" with its decision (debug level),
  "terminal tab closed". Target `cabinetos_ui::webview`: "WebView2
  started" with its browser process ID, every blocked request, and "a
  WebView2 process failed" with the kind and reason.

## Not in this version

| What | Why |
|---|---|
| The marketplace button | Disabled; Phase 9 |
| Reattaching to shells after the UI restarts | The UI starts its own core, and the core closes its shells when it stops, so there is nothing to reattach to (`terminal_list` is ready for it) |
| Saving the dock's dragged size | No setting for it yet |
| Workspaces (title-bar tabs, sidebar section) and Tags | One static "Default" tab; both sidebar sections stay hidden (Article 4) |
| Sorting by a column | The column headers are static; the order is `panes.sort` from `cabinetos.json` |
| Pasting files copied in Explorer, drag and drop | The in-app clipboard only |
| A shell property sheet | Properties shows the listing's metadata |
