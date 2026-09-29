# The WinUI 3 shell

`CabinetOS.exe` is the window: design views A (the main workspace), B (the
command palette), C (the marketplace and the permissions review), D (the
context menu and the editor tabs) and E (the file operations flyout) of
[design/README.md](design/README.md). It draws pixels and captures
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
| Scrolling that folder with PageDown held for 5 s | measured on 2026-09-29 without keys, the display asleep: about 15 ms of UI-thread work per page of 30 rows, 19 to 24 ms before; the frame gaps need an awake display ("Scrolling") |
| Closing the window, until the core has exited | 0.4 s |

## The solution

| Project | What it is |
|---|---|
| `ui/CabinetOS.Core` | Everything that runs without a window, so it is unit-tested: the pipe client, the protocol types, the core launcher, `ListingView` (the shared-memory reader), the type-name and icon caches, the selection, keys and the chord state machine, the `CommandRouter`, the job client (`TransferCenter`, `ConflictQueue`), the terminal's byte pump, page protocol and folder-sync rule, the dock's size rules, the search model, the plugin list and review models, the Tool Extension manifest, catalog and messages, the theme mapper (with the light colours of a `system` theme) and the theme picker's model, the marketplace view's model and card texts, the in-app clipboard, the settings the window reads and writes, display formatting, diagnostics, and the parts of Total Commander's keys that need no window (the marking styles, the marks kept by name, a pane's sort, measured folder sizes, quick search, the prompts' lists and pattern history, the drive memory). |
| `ui/CabinetOS` | The WinUI 3 app, `CabinetOS.exe`: the window, the panes, the sidebar, the palette, the context menu, the transfer flyout, the Tool Dock with the terminal, the editor tabs of Tool Extensions, the plugin list and review, the theme applier (brushes, WinUI's accent, the tinted Mica backdrop) and the theme picker, the marketplace view, and `Assets/xterm` (the terminal page and xterm.js). |
| `ui/CabinetOS.Tests` | xunit v3 tests of `CabinetOS.Core`, including end-to-end runs against the real core. |

Shared settings: `ui/Directory.Build.props` (target
`net10.0-windows10.0.22621.0`, Windows 11 22H2 as the floor per
[ADR 0004](decisions/0004-minimum-windows-11-22h2.md), x64, warnings are
errors), `ui/Directory.Packages.props` (every package version in one
place) and `ui/global.json` (the .NET SDK, and the test mode of
`dotnet test`). Package versions: [dev-setup.md](dev-setup.md), "Phase 5".

The window references the Windows App SDK 2.5.1 as the four components it
uses, not the `Microsoft.WindowsAppSDK` metapackage: `WinUI` (with WebView2
through it), `Foundation` (the bootstrapper of an unpackaged app, and MRT
resources), `InteractiveExperiences` (windowing, input, composition, Mica)
and `Runtime` (the Windows App Runtime version the bootstrapper asks for).
The metapackage also brings the AI, ML, Search, Widgets and DWriteCore
components, which the window never touches (Constitution Article 10). The
versions are the metapackage's; the `Runtime` package's build checks that
they match it. A publish with the release script's flags went from 60 files
and 80.5 MiB to 45 files and 39.8 MiB (28.1 to 11.2 MB zipped): gone are
`onnxruntime.dll` (21 MB), `DirectML.dll` (18 MB), the Windows AI and
machine-learning libraries, `System.Numerics.Tensors` and eleven
projection assemblies. Foundation's own twenty small projections (about
1 MB: notifications, pickers and the like) stay, since Foundation is one
package.

Publishing: `dotnet publish CabinetOS\CabinetOS.csproj -c Release -r
win-x64 --self-contained false -o <folder>` from `ui\` is enough. The
project keeps the Windows App SDK's MSIX tooling on (`EnableMsixTooling`)
for its resource step: without it a publish had no `CabinetOS.pri`, the
compiled XAML, and the published window stopped at start ("Cannot locate
resource from 'ms-appx:///MainWindow.xaml'"). The app stays unpackaged
(`WindowsPackageType` `None`); the build and `dotnet run` are unchanged.
Checked on 2026-09-29: a plain publish held the same 45 files as one with
`-p:EnableMsixTooling=true`, and the published window started its core and
opened a terminal. [release.md](release.md) has the release build.

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
folder made and renamed; the type names and icon keys of a listing's rows
(a `.gitattributes` gets a type name in words, not a program identifier
such as `txtfile`), and the icon PNGs at every offered size; a `cmd`
session whose echo comes back through the terminal's byte pump; the theme
picker choosing Nord, whose `theme_changed` the mapper turns into Nord's
accent and Mica tint; and the marketplace model over the local index of
`sdk/marketplace/build-index.ps1` (read, search, install Hello with its
review's grant until it is active, the core's `installedVersion` after the
install and none after the uninstall). They run when
`CABINETOS_CORE_EXE` is set or the core is built in `core/target`, and
skip themselves otherwise (as in the CI job `ui`, which builds no core).

### The live check

The tests and the snapshot aid (below) press no keys. Two PowerShell
scripts in `ui/livecheck/` check the window with real key presses and
mouse clicks, sent with `SendInput`, and take screenshots of it:

- `livecheck.ps1`: the broad check. The palette, single and dual pane,
  rebinding through the pencil, PageDown held for 5 s in the
  100,000-entry folder (with `CABINETOS_UI_FRAMESTATS=1`; it prints the
  frame table of those seconds and the scroll goal's line, "Scrolling"), F7, F2, Delete
  and F5 with a conflict answered Skip through UI Automation, the context
  menu, Properties with Esc and a key that must not run under it, the
  terminal typed with Unicode key events, the palette from the terminal,
  search, and Markdown Preview with Enter and then Ctrl+K V. Then Total
  Commander's keys, Commander Compact chosen in the theme picker with its
  keys (the window's log must say 20 px rows; Tab must never land on a
  function key; F5 pressed through the bar's button by its accessible name
  must copy a file) and switched back, and the edge cases.
- `livecheck2.ps1`: the input paths. Skip by a real mouse click, then
  Properties with the same checks, the terminal typed with virtual-key
  events and with Unicode key events, and Ctrl+K V twice on the open
  preview.

`run-livecheck.ps1` runs `livecheck.ps1 -Strict`, keeps the output in
`_io\live-check\run-<time>.txt` next to the repository, and at the end writes
`DONE.md` there and opens it in Notepad: the sign, on a PC where someone is
waiting, that the keyboard and mouse are free again. The script types into a
name box only after the window's "rename box shown" line, reads the
selection from its "selection shown" line, stops when a window of another
process comes to the front (a flyout of the window's own process is fine),
and, where a global hotkey of another program takes Alt+F1 (as the Claude
desktop app's did on the development PC until it was changed), says so and
opens the drive list from the palette.

Run one only on an unlocked screen you are watching. It stops when the
screen is locked, and the moment another window comes to the front, so no
key reaches another program. Each run has its own configuration, logs and
themes under `%TEMP%\cabinetos-ui-test\<run>` and writes its screenshots to
`-ShotDir`. It needs the release builds of the window and the core (the
defaults point at `ui\CabinetOS\bin\x64\Release\…` and `core\target\release`),
and `livecheck.ps1` needs the 100,000-entry folder that
`cargo bench -p cabinetos-fs --bench list_directory` makes in
`%TEMP%\cabinetos-bench`. `livecheck.ps1` leaves one file in the Recycle
Bin, `cabinetos-live-check-delete-me.txt` (the Delete check).

```text
# PowerShell: the scripts use Windows' SendInput and UI Automation
powershell -ExecutionPolicy Bypass -File <repo>\ui\livecheck\livecheck.ps1 [-Strict]
powershell -ExecutionPolicy Bypass -File <repo>\ui\livecheck\livecheck2.ps1
```

With `-Strict`, `livecheck.ps1` exits 1 when the scroll goal was not met;
it is off by default, because the number depends on the machine being
quiet.

The record of the first runs is [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md).

### Environment variables

| Variable | Effect |
|---|---|
| `CABINETOS_CORE_EXE` | The core to start, as a full path (first in the launcher's order, below) |
| `CABINETOS_LOG_DIR` | Where `ui.<date>.jsonl` and crash traces go; the core it starts inherits it |
| `CABINETOS_LOG` | The level filter, in the core's syntax: `debug`, or `info,cabinetos_ui::pipe=trace` |
| `CABINETOS_CONFIG` | Not read by the UI; the core it starts inherits it and uses that `cabinetos.json` |
| `CABINETOS_THEMES_DIR` | Not read by the UI; the core it starts inherits it and reads the themes there ([themes.md](themes.md)) |
| `CABINETOS_PLUGINS_DIR`, `CABINETOS_MARKETPLACE_DIR` | Not read by the UI, except the plugins folder for the empty plugin list's hint; the core it starts inherits them and installs there ([marketplace.md](marketplace.md), "Folders"). Set both to a scratch folder to try installs without touching `%LOCALAPPDATA%\CabinetOS` |
| `CABINETOS_WEBVIEW2_DIR` | Where WebView2 keeps its user data (its cache and storage) for the terminal and the tool pages, one subfolder per host; by default `%LOCALAPPDATA%\CabinetOS\WebView2`. The two-window test and the live checks set it to their scratch folder, so they never write into the real one |
| `CABINETOS_UI_FRAMESTATS=1` | Logs one `frame stats` line per second: frames drawn, the longest gap between two, the gaps over 20 and 33 ms, the frames whose UI-thread work passed 16.7 ms, and the milliseconds of each timed part ("Scrolling"). The snapshot aid's `scroll:` step adds a `scroll run` line with the run's whole frame table and the machine's CPU load, and a `slow frame` line for each frame of 33 ms or more |
| `CABINETOS_UI_SNAPSHOT=<folder>` | Development aid: once the first folders are shown, runs the steps of `CABINETOS_UI_SNAPSHOT_STEPS` and renders the window to PNG files in that folder. It draws the window's own content, so it works when the screen is off or locked; Mica is not part of that content. An open dialog (the popup layer) is rendered on its own and laid over the image, without WinUI's dimming of the window under it. WebView2 pages (the terminal) draw outside that content: each one on screen is captured by WebView2 (`CapturePreviewAsync`) and laid over its place, which also works on a locked screen. The capture has no transparency, so the terminal's area shows `#202020` instead of the panel's colour. The image is laid over a stand-in for Mica, so it is opaque: `#202020` (`#F3F3F3` in light mode) with the theme's Mica tint over it. |
| `CABINETOS_UI_SNAPSHOT_STEPS` | The steps, separated by `;` (default `shot:window`): `cmd:<command> [json]` runs a command through the router and waits for it; `cmd-nowait:<command>` runs one that waits for the user (a dialog, a rename); `path:<folder>` goes there in the active pane; `pane:0` or `pane:1` makes a pane active; `select:<name>` selects a row; `selectall`; `menu:<name>` opens the context menu on a row (`menu:*` on the empty space); `rename:<text>` types into the rename box and presses Enter; `dismiss` closes a dialog; `type:<text>` types into the palette, or into the prompt in its frame when one is shown; `accept` presses Enter in that prompt; `drive:<letter>` presses a drive's letter in the open drive list; `quick:<text>` types letters into the active pane's quick search; `search:<text>` types into the search field; `open:<name>` presses Enter on a row (a file may open in a Tool Extension); `terminal:<text>` types into the shown shell (`{enter}` is Enter); `crash:terminal` or `crash:tool:<id>` ends that page's browser process; `dock:<pixels>` drags the dock's splitter to that size and saves it, as a drag does; `mode:light`, `mode:dark` or `mode:windows` makes the window take Windows as set to that mode (a `system` theme follows) without changing the PC's setting; `size:<width>x<height>` sizes the window's content in device-independent pixels; `fit:<pixels>` makes the window as high as gives the active pane's list that height; `theme:<id>` sets `ui.theme` as the picker does and waits until the theme is applied; `layout:<label>` writes "layout measured" into the log: each pane's list height, its whole rows, their height and the texts cut short, the function keys' widths, every corner radius over 3 px outside the overlays, the sizes the metrics set, and the keymap's fingerprint ("Metrics and chrome"); `click:<name>` presses the first shown button or menu item (of an open menu too) with that name as UI Automation reports it, the way assistive technology may press it: the keyboard moves to it, then its automation peer invokes it (`click:Installed` shows the marketplace's Installed tab, `click:Skip` answers a conflict, `click:Folders in between` opens the crumbs' "…" menu); `focus:<label>` writes where the keyboard is into the log ("keyboard focus", with the label); `tooltip:<name>` opens the tooltip of the first element with that accessible name, shown or not, as the end of a hover delay would, and logs whether it stayed open; `scroll:<pages>` presses PageDown in the active pane 30 times a second, as a held key repeats, and `scroll:<pages>/<n>` once every n frames (with `CABINETOS_UI_FRAMESTATS=1` each writes its frame table, "Scrolling"); `until:running`, `until:conflict`, `until:terminal`, `until:search` or `until:tool` waits for a job, a shell, an answer or a tool page; `wait:<ms>`; `shot:<name>` writes `<name>.png`, open dialogs and menus included. Example: `pane:0;select:report.txt;cmd:file.copyToOtherPane;until:conflict;shot:conflict`. A step's text cannot contain `;`, since that ends the step. One quirk: a check box always shows a dash there, checked or not (the bitmap draws the first frame of WinUI's animated check mark). |

### Logs and crashes

The UI writes `%LOCALAPPDATA%\CabinetOS\logs\ui.<UTC date>.jsonl` in the
format of [diagnostics.md](diagnostics.md), with `boundary: "frontend"`, on
a background thread with a bounded, lock-free queue; it keeps 14 daily
files, as the core does. Targets are `cabinetos_ui::<area>`: `app`,
`session`, `launcher`, `pipe`, `commands`, `keys`, `pane`, `palette`,
`jobs`, `settings`, `shell`, `frames`, `xaml`, `icons`, `terminal`,
`webview`, `theme`, `market`, `snapshot`. The UI thread is named `ui`.

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
- Where the shell answers a program identifier instead of a name
  (`txtfile` for `.gitattributes` on the development PC), the core sends
  `EXT File` since protocol 11, as Explorer shows it: "GITATTRIBUTES File".
  The window shows what the core sends. Checked on 2026-09-28 in a
  snapshot (`.gitattributes` and `.gitignore` read "GITATTRIBUTES File"
  and "GITIGNORE File"), and an end-to-end test checks that the name is
  words, not an identifier.

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
| Up, Down, Home, End, PageUp, PageDown | Move the focus; only the new row is selected (with `panes.selection: commander`, the marks stay) | the pane |
| the same with Shift | Select from the anchor to the new row (commander: mark the rows passed over, "Marking") | the pane |
| the same with Ctrl | Move the focus and keep the selection | the pane |
| letters, digits, other characters no key takes | Quick search: the first name that starts with what was typed ("Quick search") | the pane |
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
| F8, Shift+F8, Shift+F6, Alt+F7, Ctrl+Num + | Total Commander's keys for delete, delete permanently, rename, search and select all, next to the ones above | keymap |
| Space, the keypad, F3, F4, Ctrl+F3 to Ctrl+F6, and the rest of Total Commander's keys | "Total Commander's keys", below | keymap |

The keys marked "keymap" are the core's defaults; each can be rebound in
the palette or in `cabinetos.json`. The keypad's operators are keys of
their own (`numpadadd` and the rest, shown as "Num +"); its digits are the
plain digits.

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

Since protocol 11 the registry also lists the commands of the window's own
controls, so they are in the palette too, ranked by the core, and can be
rebound: the transfer flyout's `transfer.pause`, `transfer.resume`,
`transfer.cancel`, `transfer.close`, `transfer.minimize`,
`transfer.restore`, `transfer.next` and `conflict.resolve`;
`sidebar.pin` and `sidebar.unpin`; the terminal's `terminal.new`
(`{"profile": …, "cwd": …}`), `terminal.show`, `terminal.close`
(`{"session": …}`) and `terminal.reload`; the search's `search.scope`
(`{"wholeVolume": true}`); `plugins.list`; and the editors'
`editor.openMarkdownPreview` (Ctrl+K V in a pane), `editor.close` and
`editor.reload` (`{"pane": 0 or 1}`). The window keeps only their handlers.
Run from the palette or a key, without arguments, each one acts on what is
in front:

- the transfer commands act on the job the flyout shows;
- `conflict.resolve` opens the flyout at the waiting conflict, or says that
  none waits;
- `sidebar.pin` and `sidebar.unpin` take the active pane's folder;
- `terminal.show` shows the dock, and `terminal.close` ends the shown shell;
- `search.scope` switches "Whole volume" on or off;
- `editor.close` and `editor.reload` take the editor that has the keyboard,
  else the one that is open.

With a core before protocol 11 they still run, because a handler runs when
the registry does not list its command; they are then not in the palette.

A few commands exist only in the UI, because they belong to a dialog or to
a view's buttons (`RegisterLocal`): the plugins' `plugins.review` and
`plugins.reload` (`{"id": …}`) and `plugins.grant`, the theme picker's
`theme.apply`, and the marketplace's `market.refresh`, `market.select`,
`market.install`, `market.uninstall` and `market.source` (`{"id": …}`).
They run through the router like the others, but they are not in the
palette and cannot be rebound.

A plugin's command run from the palette or a key gets `{"path", "paths"}`
from the active pane: the focused row and the selected rows, or the focused
search hit. From the context menu it gets the menu's rows
([plugins.md](plugins.md), "What the shell passes").

| Command | In this version |
|---|---|
| `palette.show`, `overlay.close` | Open and close the palette; Esc closes, in order, the palette, the theme picker, the plugin review, the plugin list, the marketplace (its detail column first), the context menu, a rename, the address box, the search results, and last folds the transfer flyout into the pill |
| `search.focus` | Puts the keyboard in the search field ("Search") |
| `keys.open` | Opens the palette: it lists every command with its keys and edits them |
| `view.toggleDualPane`, `view.toggleSidebar`, `view.focusOtherPane` | As named; the first two are saved in `cabinetos.json` |
| `go.toPath` | With `{"path": …}` goes there; without, turns the crumbs into a text box |
| `help.about` | The window's command (target `ui`): its handler shows About CabinetOS ("About", below). An older core that ran it itself answered `command_result`; the window shows the same view for that |
| `file.copyToOtherPane`, `file.moveToOtherPane`, `file.newFolder` | Run in the window: a job, or a folder (see below) |
| `view.toggleTerminal` | Shows the terminal, gives the keyboard back to the pane, or hides it ("The terminal") |
| `marketplace.browse`, `preferences.selectColorTheme` | Open the marketplace and the theme picker ("The marketplace", "Themes") |
| `go.root` to `terminal.insertSelectedPaths` | Total Commander's small commands, 31 of them, run in the window ("Total Commander's keys") |
| `workspace.switch`, `terminal.runTask` | "arrives in a later version" in the status bar |

### Dialogs

The window's dialogs are WinUI `ContentDialog`s (Properties, delete
permanently, uninstall, About, a command's result, the core that cannot
start) and the permissions review ("Plugins"). While one is open, nothing
under it reacts:

- **No command runs.** The router refuses every command that is not the
  dialog's own (`SetModal`, tested), whatever sent it: a key, a web page's
  passed key, a plugin's command, the snapshot aid. The log says "command
  refused: a dialog is open" with the command and the dialog. A
  `ContentDialog` has no commands of its own; the permissions review has
  `plugins.grant` (Allow) and `overlay.close` (Cancel, Esc).
- **Esc closes it, wherever the keyboard is.** In the dialog, Esc closes
  it even from a selectable text, which would otherwise keep Esc for
  itself. A key that reaches the window while the dialog is open (the
  keyboard was left under it) does nothing: Esc closes the dialog, and any
  other key puts the keyboard back into it.
- **The keyboard starts in it,** on the dialog's default button (else
  Close), even when the keyboard was left elsewhere.
- The log says "dialog shown" and "dialog closed" with the title and how
  it was closed, and "key held by a dialog" for a key that reached the
  window under it. A key pressed in the dialog is the dialog's own and is
  not logged; the live check proves that nothing ran by finding no
  "command executed" between "dialog shown" and "dialog closed".

Checked on 2026-09-29 with the snapshot aid (release builds): Properties
opened with the keyboard on its button, `view.toggleTerminal` and `go.up`
were refused while it was open, and after it closed the keyboard was back
in the pane and the terminal opened. The permissions review refused
`view.toggleTerminal` and `palette.show`, and closed with `overlay.close`.
With real keys (`ui/livecheck/livecheck.ps1`, 2026-09-29): Esc closed
Properties, and Ctrl+` pressed while it was open ran nothing.

Starting on the default button means Enter does what the dialog offers
first (Close, Cancel, Try again); WinUI would give the keyboard to the
first focusable part, such as a link in About.

### About

"Help: About CabinetOS" (`help.about`) opens a dialog with the product's
version (the window's, the same as the core's in a release), the core's
version and protocol from `ping`, and the build: the commit and the build
time from `release.json` next to `CabinetOS.exe` (written by
[build/release.ps1](../build/release.ps1)), with "with uncommitted changes"
when the release says so; without that file, "Development build". Two
links open `LICENSE` and `THIRD-PARTY-NOTICES.md` next to the program with
`open_path` (the core opens them with their default application); a
development build, which has neither, says so instead. The copyright line
comes from the program's assembly. It is a dialog like the others: Esc
closes it, and no command runs while it is open ("Dialogs"). Its links are
the dialog's own and do not go through the router.

`release.json` is the one file besides the tools' `tool.json` that the
window's process reads itself (brief §1 keeps file I/O out of the UI): a
few hundred bytes next to the program, read off the UI thread when the
view opens (`ReleaseFolder`, tested). A file that cannot be read shows as
such.

The core lists `help.about` for the window (target `ui`, and answers
`command_routed` if asked to run it), so the palette row, a key and the
menu all reach the window's handler through the router
(`MainWindow.RegisterAboutCommand`). A router test checks that the
handler opens the view and the core is asked nothing, and an end-to-end
test checks the same against the real core's own list. An older core ran
it itself (target `core`) and answered `command_result`; the window shows
this view for that result too. Checked with the snapshot aid and the
current core (2026-09-29): "about" in the palette finds "Help: About
CabinetOS", and `help.about` run through the router logged target `ui`,
"about shown" and "dialog shown", while the core's log has no line for
it.

Checked on 2026-09-29 (release builds, snapshot aid): in a published folder
with a stand-in `release.json`, `LICENSE` and notices, the view showed
"0.1.0", "0.1.0, protocol 11", the commit, the build time and both links;
in the development build it said "Development build" and that the two
documents come with a release. The keyboard started on Close, and
`view.toggleTerminal` was refused while it was open. The links were not
clicked in the check (that would open Notepad on the desktop); Enter on a
file uses the same `open_path`.

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
  delete, a move on one volume, empty files) graphs `items_per_second`
  instead (protocol 11), never below 10 items/s. The pace at the graph's
  top right names its unit: "412 items/s" where a copy says "610 MB/s". A
  paused job samples 0, and the pace disappears once the job ends;
- the progress bar: 4 px, its width follows the core's numbers over 400 ms
  on the compositor (an implicit scale transition);
- the footer: the percent (rounded down, so 100 % means done), "1.2 GB of
  2.7 GB" or "8,412 of 10,001 items", the time left or "Paused",
  Pause/Resume, and Cancel, which turns into Close when the job ends.

The minimize button folds the flyout into the status-bar pill: an
80 × 4 px track and "Copying · 45%". A click on the pill opens the flyout
again. The flyout never keeps the keyboard, so the panes keep working
while a job runs: a click on its buttons does not take the focus, and
when a screen reader, Voice Access or Tab put the keyboard on one, a
decision on a conflict, Minimize and Close give it back to the pane
before the part with the focus closes (else the keyboard fell to the Back
button: live check, 2026-09-28). Esc folds it into the pill (an ended job
closes) when nothing else is open or being edited and no search is shown.

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
| `ui.dockSize.bottom`, `ui.dockSize.right` | A drag of the dock's splitter ends (once per drag) | At start and on `config_changed`, except during a drag ("The terminal") |

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
closes it. A row shows the command's first binding as keycaps, and "+N"
when it has more; its tooltip lists them all.

Rebinding: the pencil (or F2 on the highlighted row) starts recording. The
next key that is not a modifier forms a combination; a second one within
1000 ms makes a chord and ends the recording at once; otherwise it ends
after 1000 ms without a key. Esc cancels. The keys go to the core as
`set_keybinding`, which writes `cabinetos.json`; its answer is the new
keymap. A refusal (`immutable_binding`, `keybinding_conflict`, …) is shown
in the row. Commands of the Immutable System Tier show a lock instead of a
pencil.

When the palette closes, the tooltips on screen close with it. WinUI
closes a tooltip when the pointer leaves its button, and a button that
collapses under a resting mouse is never left: the pencil's "Change
keybinding (F2)" stayed on screen in the live check of 2026-09-28. The
theme picker, the plugin list, the permissions review, the context menu
and the marketplace do the same when they close. A tooltip can also open
after its overlay closed, from the hover delay WinUI started while the
mouse was on the button (the re-run of 2026-09-29 showed it). So the
row's tooltips (the pencil, the lock, the keycaps' "+N") close themselves
when they open for a button that is not shown (`OpenToolTips.Set`).
Checked with the snapshot step `tooltip:Change keybinding`: open with the
palette shown, and closed at once after the palette closed.

## Total Commander's keys

Sub-phase 11a gives Total Commander users the keys their hands know
([research/total-commander.md](research/total-commander.md), Part 3 (b)).
The core lists 31 new commands with target `ui` and their keys (protocol
12, [keybindings.md](keybindings.md)), and gave five existing commands a
Total Commander key next to their own: F8 and Shift+F8 delete, Shift+F6
renames, Alt+F7 finds files, Ctrl+Num + marks every row. The window's
handlers are in `MainWindow.Commander.cs`; what they share without a
window is in `CabinetOS.Core` and tested there.

| Keys | Command | What the window does |
|---|---|---|
| Ctrl+\ | `go.root` | The drive's root (`C:\`), or the share's (`\\server\share\`) |
| Alt+F1, Alt+F2 | `go.chooseDriveLeft`, `go.chooseDriveRight` | The drive list under that pane ("The drive list") |
| Ctrl+Left, Ctrl+Right | `go.showInLeftPane`, `go.showInRightPane` | The folder under the cursor, or the pane's own folder when the cursor is on a file, shown in that pane; the right pane appears first when one pane is shown |
| Ctrl+D | `go.pinnedFolders` | The sidebar's folders in the palette's frame ("Prompts in the palette's frame") |
| Ctrl+U | `view.swapPanes` | The panes change places with all they hold: folder, marks, history, sort, search results. The keyboard stays on its side |
| Ctrl+R | `view.refresh` | The folder listed again, for drives whose changes Windows does not report; the cursor and the marks stay, found again by name |
| Ctrl+F3 to Ctrl+F6 | `view.sortByName`, `view.sortByExtension`, `view.sortByModified`, `view.sortBySize` | The pane's own order ("A pane's order") |
| Space | `edit.toggleSelectionInPlace` | Marks or unmarks the cursor row, and the cursor stays; a folder it marks is measured |
| Num +, Num - | `edit.selectByPattern`, `edit.unselectByPattern` | The pattern box ("Prompts in the palette's frame") |
| Alt+Num +, Alt+Num - | `edit.selectSameExtension`, `edit.unselectSameExtension` | Every file with the cursor file's extension: `*.txt`, and `*.` (no dot) for a name without one |
| Num * | `edit.invertSelection` | The files' marks turn around; folders keep theirs |
| Ctrl+Num - | `edit.unselectAll` | Nothing marked; a command then acts on the cursor row |
| Num / | `edit.restoreSelection` | The marks the last copy, move, delete or unmark cleared in this pane, found again by name; a name that is gone is skipped |
| Ctrl+Shift+C, Ctrl+K Ctrl+N, Ctrl+K Ctrl+P | `edit.copyFullPath`, `edit.copyName`, `edit.copyFolderPath` | The targets' paths, their names, or the pane's folder, on Windows' clipboard as text: one per line, no quotes. CabinetOS's own file clipboard (Ctrl+C) is not touched |
| F3 | `file.view` | The cursor file in the first installed Tool Extension that shows it; without one, a note in the status bar, never the file's default program (which for a program would run it). On a folder, its size, as Total Commander's F3 |
| F4 | `file.edit` | The cursor file opens for editing, and never runs: `files.editor`, else the type's edit verb, else Notepad; the core decides (`edit_path`) |
| Shift+F4 | `file.newTextFile` | A name box over a new row ("New text file") |
| (none) | `file.windowsProperties` | Windows' own property sheet for the targets, or the folder (`show_properties`); also the "Windows Properties" button of the Properties dialog |
| (none), Shift+Alt+Enter | `file.calculateFolderSize`, `file.calculateAllFolderSizes` | The marked folders, or the cursor folder; or every folder of the listing: measured ("Folder sizes") |
| Ctrl+P, Ctrl+Shift+Enter | `terminal.insertPath`, `terminal.insertSelectedPaths` | The terminal shows, its default shell started when none runs, with the pane's folder, or the targets' paths, typed at the prompt, quoted for that shell and without Enter (`terminal_type_paths`); the terminal gets the keyboard |

Snapshots of 2026-09-29 in [log/2026-09-28/](log/2026-09-28/):
`11a-drive-list.png`, `11a-pattern-box.png`, `11a-pinned-folders.png`,
`11a-folder-sizes.png`, `11a-quick-search.png`, `11a-new-text-file.png`
and `11a-terminal-path.png`.

### Marking

`panes.selection` ([config.md](config.md)) says how the keyboard marks.
`windows`, the default, is Explorer's: a key that moves the cursor
selects the row it moves to. `commander` is Total Commander's
(`SelectionModel.KeyMode`, tested in both styles):

- A key that moves the cursor keeps the marks, and a new listing starts
  with nothing marked. A command acts on the marked rows, or on the cursor
  row when none is marked; the cursor row has an outline.
- Shift with a key that moves the cursor marks the rows it leaves, not the
  one it lands on, so Shift+Down marks one row per press. When the cursor
  cannot move (the end of the list), the row it stays on is marked. Shift
  with Home or End marks through the first or the last row. If the row the
  cursor leaves was marked, the rows passed over are unmarked instead.
- The mouse keeps the Windows rules in both styles: a click selects one
  row, Ctrl+Click toggles one, Shift+Click selects a range.

A change of the setting applies at once and keeps the marks; the Windows
style then selects the cursor row when nothing is selected. Space in the
Windows style on the cursor row, selected alone by the key that moved
there, keeps it selected (and measures a folder) instead of leaving
nothing selected.

### Prompts in the palette's frame

The pattern box, the pinned folders and the drive list use one prompt,
`PromptBox`: the palette's Acrylic panel, entrance and rows, with a label,
a box, an optional check box, a few rows and a hint line. Up and Down move
the highlight, Enter takes it, Esc (`overlay.close`) or a click outside
cancels, and the keyboard goes back to the pane.

- **The pattern box** (Num +, Num -) offers the last pattern, `*.*` the
  first time, selected, and lists the last ten of this session; Up and
  Down put one in the box. Total Commander's syntax: `*` and `?`, `;`
  between patterns, `|` before the ones to leave out (`*.*|*.bak`). The
  core matches (`match_entries`), case ignored; the window never reads
  the names for it. "Include folders" is off by default, so a pattern
  marks files. The answer is a list of row ranges of the section the core
  read; an answer about an older listing is asked again, so no mark lands
  on the wrong row. The status bar says how many rows matched.
- **The pinned folders** (Ctrl+D) are the sidebar's; typing narrows them
  by name or path, Enter goes there in the active pane, and the last row
  pins the pane's folder when it is not pinned yet.
- **The drive list** (Alt+F1, Alt+F2) has no box: it opens under that
  pane's header with the sidebar's `list_volumes` data (name, free space),
  the pane's own drive highlighted. A drive's letter picks it at once, as
  in Total Commander; the arrows and Enter, or a click, too. A drive goes
  to the folder this pane last showed on it in this session, else to its
  root (`DriveMemory`). A WinUI `Flyout` was tried first: the snapshot aid
  could not draw it (its presenter gave `RenderTargetBitmap` no pixels),
  and the prompt is part of the window.

### A pane's order

Ctrl+F3 to Ctrl+F6 sort the active pane by name, extension, date modified
or size, and the same key again reverses the order. A pane keeps its
order and sends it with each of its listings; the core sorts (the key
`extension` is protocol 12), the other pane and `panes.sort` stay as they
are. A new key starts in its own direction, the largest and the newest
first for size and time, as in Total Commander (`PaneSort`). The cursor
and the marks stay. The sorted column's heading has a small chevron, up
from A to Z (smallest, oldest), down the other way; the design's plain
headings stay for its default, name from A to Z. The status bar says the
order: "Sorted by size, largest first."

### Folder sizes

A folder's Size is empty until it is measured: Space on a folder,
Calculate Folder Size (the marked folders, or the cursor folder), Shift+
Alt+Enter (every folder of the listing), or F3 on a folder. The core
counts (`measure_paths`); the Size column shows its running total in the
tertiary colour, then the total. The status bar's selected size adds the
measured folders, and one selected measured folder says its size ("1
selected · WinSxS, 3.5 GB"); folders that could not be read are named
there, and are not in the size. Sizes belong to the folder the pane
shows (`FolderSizes`): leaving it forgets them and cancels a count still
running. A folder being counted is not asked about again. Checked on
2026-09-29: Space on `C:\Windows\WinSxS` showed 2.3 GB while the core
counted and 3.5 GB at the end; Shift+Alt+Enter on the repository counted
`core\` at 27.7 GB in under a second.

### New text file

Shift+F4 shows a name box over a new row at the top of the rows on
screen, "New Text Document.txt" with the name part selected. Enter asks
the core to create the empty file (`create_file`, which never replaces
one); the row is selected once the watcher lists it, and the file opens
for editing as F4 does. A name that is taken opens that file instead, as
in Total Commander; a folder's name is refused in the status bar. Esc
cancels.

### Quick search

Letters typed in a pane, where no key of the keymap takes them, jump to
the first name that starts with them; the status bar shows "Quick search:
rep". After a second without a key a new search starts, as in Explorer;
Esc clears it, and so does another folder. The core finds the name
(`match_entries` with `first_from`): a search's first letter looks from
the row after the cursor, so the same letter again goes on to the next
name; a longer text looks from the cursor. The characters come from
`CharacterReceived`, so the keyboard layout decides (Cyrillic works), and
Ctrl or Alt alone makes a shortcut, not a letter; AltGr types. A key the
window took first, Space among them, types nothing into the search, so a
search cannot hold a space; `;` and `|` find nothing, since the pattern
syntax reads them.

### Checked

- Unit tests: `SelectionModelTests` (both styles, Space, invert,
  unmark), `MarkMemoryTests`, `PaneSortTests`, `FolderSizesTests`,
  `QuickSearchTests`, `PromptTests` (the pattern history, the pick list,
  ranges, the same extension), `DriveMemoryTests`, `KeyTests` (the keypad
  names), `DisplayFormatTests` (the root), `TerminalTests` (paths typed
  hold the line) and `ProtocolTests` (the version 12 messages against the
  core's schemas).
- End-to-end against the real core (`CommanderEndToEndTests`): the sort by
  extension; `create_file` and `edit_path` with an editor found nowhere,
  so none starts; `show_properties` errors; `measure_paths` into
  `FolderSizes`; `match_entries` for the pattern box, the same extension
  and quick search; `terminal_type_paths` for a shell that is gone.
- The snapshot aid (steps `type:` and `accept` for a prompt, `drive:`,
  `quick:`), with a stand-in editor that notes each file and shows no
  window (`wscript.exe` and a script, as `files.editor`): F3 on a program
  said no tool shows it, F4 and Shift+F4 handed it the file, the Windows
  Properties button opened the sheet, which closed with the core.
- `ui/livecheck/livecheck.ps1` presses the keys for real: Num *, Ctrl+Num
  -, Num + with `*.txt`, Space on a folder, Shift+Alt+Enter, F3 on a
  program, F4 and Shift+F4 with its own stand-in editor, a quick search
  and F8, Shift+F8, Ctrl+P, Ctrl+\, Alt+F1, Ctrl+U; the planning session
  runs it.

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
  shows "Modified"; from the searched folder's name down, `docs\log`, so
  the part that tells hits apart is not cut off; the whole path for a
  whole-volume search), and the type name. The reply has paths only, so the
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
  selection is out of sight, so they must not act on it. So do Total
  Commander's selection and file commands: Space, Num +, Num -,
  Alt+Num +, Alt+Num -, Num *, Ctrl+Num -, Num /, Ctrl+F3 to Ctrl+F6,
  Ctrl+Shift+C, Ctrl+K Ctrl+N, F3, F4, Shift+F4, Windows Properties from
  a key, Calculate Folder Size, Shift+Alt+Enter, Ctrl+Left, Ctrl+Right and
  Ctrl+Shift+Enter. Ctrl+R runs the search again; Ctrl+\, Ctrl+U, Ctrl+D,
  Alt+F1, Alt+F2, Ctrl+K Ctrl+P and Ctrl+P act on the pane, not the hits,
  and work. Hits have no context menu and no quick search.
- An answer to an older request, or one that arrives after the search was
  left, is dropped (`SearchModel`, tested). The status bar counts the hits
  and shows the focused hit's path.

Measured on 2026-09-28 without the indexer: the core walked `docs/` in
1.4 ms (its `took_us`), and the reply reached the window 4.6 ms after the
request went out.

## Plugins

Core Plugins run in the core, in a WebAssembly sandbox
([plugins.md](plugins.md)); the window lists them, reviews what they ask
for, and says what they do (Constitution Article 8).

- **The list.** "Plugins: Show Plugins" (`plugins.list`) in the palette
  opens it over the window, like the palette: every installed plugin with
  its version and ID, its state in words ("Active: 1 command", "Waits for
  your review: fs:read", "Crashed at 12:03:04: …", "Cannot start: …"), and
  each capability it asks for, with a dot and LEVEL in the level's color
  (low green, medium yellow, high red; docs/design/README.md) and whether
  it is allowed. A plugin that waits for grants offers "Review
  permissions"; one that crashed or could not start offers "Reload"
  (`reload_plugin`, which reads its folder again). Esc or a click outside
  closes the list.
- **The permissions review** (design view C): 440 px on a 40 % black
  scrim. The plugin's tile (its initials on a color taken from its ID,
  until plugins bring icons of their own), "Review permissions", "{name}
  by {author}", "This plugin runs in a WebAssembly sandbox. It can only do
  what you allow here.", one row per capability (level dot, name, LEVEL,
  reason, and the folders for `fs:read` and `fs:write`), the box "Trust
  {author} for future updates", disabled with the tooltip "Publisher
  identities come in a later version; until then every update is
  reviewed.", and Cancel / "Allow and install". Allow sends
  `grant_capabilities` with what the core says is missing; the core writes
  it to `cabinetos.json` and starts the plugin. Opened from the
  marketplace, the same dialog shows what the index lists, and Allow
  installs first ("The marketplace", below).
  Cancel, Esc and the scrim close the dialog. The keyboard starts on
  Cancel, so Enter does not grant by accident. A refusal (for example a
  `cabinetos.json` with an error) shows in red in the dialog.
- **When it opens by itself.** When a plugin newly waits for review
  (`plugin_state_changed` to `needs_review`, for example after
  `reload_plugin` found a plugin copied in meanwhile), once per plugin per
  session, so a Cancel holds until the next start; the list offers the
  review any time. Plugins that already wait when the window starts are
  not put in front of the user: the status bar says "Hello waits for your
  review: run "Plugins: Show Plugins" in the palette."
- **The status bar** says "{name} is active." when a plugin becomes active
  (after a grant, a reload or a restart), and "{name} crashed: …" in red
  when one crashes. Every `plugin_state_changed` and `plugin_crashed` makes
  the window read `list_plugins` again and compare (`PluginWatch`,
  tested); an open list follows.
- `plugins.list` is in the core's registry since protocol 11, so the
  palette ranks it with everything else and it can be rebound; the window
  only runs it.

Checked on 2026-09-28 with the `hello` fixture of `sdk/fixtures/plugins`
(`CABINETOS_PLUGINS_DIR`): the status bar named it at start, the review
granted `cmd:register` and `events:emit` into `plugins.hello.granted`, the
status bar said "Hello is active.", and the list showed "Active: 1
command". Copied in while the window ran and reloaded, the plugin's review
opened by itself.

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
  of height (320 px of width). The end of a drag saves the size in
  `ui.dockSize.bottom` or `ui.dockSize.right` (`set_value`, once per drag,
  in whole pixels), so each placement keeps its own size across restarts.
  At start, and when the file changes, `null` means the sizes above, and a
  saved size is held to the same limits in the window as it is then. A
  change in the file applies at once, except during a drag, and the
  window's own save coming back from the core moves nothing. Checked on
  2026-09-28 with the snapshot step `dock:330`: the file got
  `"bottom": 330`, and after a restart the dock opened at about 330 px
  (measured in the snapshot) instead of the design's 240 px.
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
- **Characters sent as Unicode key events** (the touch keyboard, Voice
  Access, automation tools: `VK_PACKET`) are typed from their `keypress`,
  not their `keydown`. The `keydown` can carry the character of an earlier
  packet, which xterm.js typed instead: the live check of 2026-09-28 got
  `ttttttttttttttt` for `echo unicode-5c`. Keys of a physical keyboard are
  not affected. Confirmed with real input on 2026-09-29: `echo live-5c`
  typed as Unicode key events answered `live-5c`.
- **The core stops.** Its shells end with it, and the tabs close.

How it is built:

- One WebView2 holds every session, each an xterm.js terminal: the DOM
  renderer, Cascadia Code 12 px, line height 1.25 (the design's 1.6 is for
  the prototype's static lines and would cost a third of the rows; a
  theme's `terminalLineHeight` scales it, "Metrics and chrome"), 5,000
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
  in Debug builds (the right-click menu's Inspect). `WebViewHost` does all
  of this, for Tool Extensions too.
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

## Tool Extensions

Tool Extensions are web pages that show a file in a pane
([tool-extensions.md](tool-extensions.md) has the format and the
messages; Constitution Articles 10 and 11). None ships with CabinetOS: the
first, Markdown Preview, is in `sdk/tools/markdown-preview` and is
installed by copying it to `%LOCALAPPDATA%\CabinetOS\tools\`, or used from
the repository with `CabinetOS.exe --tools-dir <repo>\sdk\tools` (or the
variable `CABINETOS_TOOLS_DIR`).

- **Opening.** Enter (`pane.openSelected`) on a file that an installed
  tool accepts opens it in that tool, in the other pane when two are
  shown, else in the same pane; without such a tool, the file opens in its
  default application as before. "Editor: Open Markdown Preview"
  (`editor.openMarkdownPreview`, in the palette, Ctrl+K V in a pane) opens
  the focused Markdown file, or says that no Markdown tool is installed.
- **The editor tab** (design view D) covers the pane's list: a 36 px strip
  with a 2 px accent edge, the file's glyph and name, close (back to the
  folder, the tool's process ends), the tool's name beside a green dot,
  and "Open in Terminal" (a shell in the file's folder). One tool per
  pane; a tool that is open shows the next file where it is. Each open,
  the same file again (Ctrl+K V on the file on screen) or another one (a
  link in the preview), serves the file's folder on a new host and loads
  the tool's page again; the page asks for the file with a new `ready`.
  A page that is already loaded cannot fetch from a host mapped after it
  loaded: the live check of 2026-09-28 got "This file could not be read:
  Failed to fetch" there (`ToolFileSession`, tested). Showing one
  pane closes the other pane's editor. When the editor covers the active
  pane (one pane shown), the keyboard that would go back to the pane (after
  the palette, a menu, the terminal) goes to the tool's page, and typing a
  search closes the tool first, because the hits need the pane.
- **Reading tools.** The window reads `<folder>\<id>\tool.json` of each
  tools folder once, at start, on a background thread. This is the one
  file the UI process reads itself (brief §1 forbids file I/O in the UI;
  the core has no request that lists tools yet), and a tool that cannot
  be used is left out with a warning in the log. The file a tool shows is
  read by the tool's own page, through a read-only virtual host.
- **Keys.** A tool's page gets every key; a script of the window in every
  tool page hands back the keys of `palette.show` and
  `view.toggleTerminal`, as the terminal does.
- **Commands from a tool** run through the router with the trigger
  `tool:<id>`, and only the navigation, view, terminal and preview
  commands of [tool-extensions.md](tool-extensions.md), "Messages"; any
  other is refused with a line in the status bar and the log.
- **Crash isolation.** Each tool has a browser process of its own
  (`%LOCALAPPDATA%\CabinetOS\WebView2\tool-<id>`). When it ends, the pane
  says "The tool stopped" with Reload, and the window goes on; Reload
  starts a new one with the same file. Checked on 2026-09-28 by ending the
  Markdown Preview's browser process (`BrowserProcessId`, the snapshot
  step `crash:tool:markdown-preview`): the pane said so, the other pane
  went on navigating, and Reload showed the README again.
- **The Tool Dock** holds the terminal, under or beside the panes. A tool
  with `placement: dock` opens in a pane in this version.

## Themes

A theme from the core changes the window's colours at once, without a
restart: the accent, the Mica tint, text, fills and strokes, the Acrylic of
the palette and menus, the terminal, the file icons and the capability
levels. [themes.md](themes.md) has the format and the shipped themes
(Constitution Articles 3, 6 and 8). A theme's sizes and its three chrome
elements apply the same way ("Metrics and chrome").

- **When.** At start the window asks `get_theme` for the theme in effect.
  Each `theme_changed` applies the theme it carries: a choice in the
  picker, `set_value ui.theme` from any client, or a saved edit of the
  theme's file. A core before protocol 10 answers `unknown_request`, and
  the design's colours stay.
- **How.** Each design token (`Cb…Brush` in `App.xaml`) is one brush for
  the whole app. The theme changes the brush's colour in place, so
  everything drawn with it repaints, open views included. `ThemeMapper`
  (in `CabinetOS.Core`, tested) turns a theme into the colour of each
  token; `ThemeApplier` sets them. The `default` theme maps to exactly the
  design's values; a test compares the two.
- **Where the palette keys go.** `textPrimary`, `textSecondary`,
  `textTertiary` and `textDisabled` are the four text levels. The design's
  other white-on-dark tokens (dividers, the selected row, keycaps, badges,
  the pressed fill, tracks) are `textPrimary` at the design's alpha, so
  they follow the text. `layerFill` and `layerStroke` are the panes, cards
  and sidebar rows; `layerStrokeActive` is the focused pane's border;
  `controlFill` and `controlFillHover` are buttons and fields.
  `acrylicTint` is the Acrylic of the palette, the context menu and the
  transfer flyout, and the dialog's fill. `terminalBackground` is the
  terminal panel. `folderIcon` and `folderIconFront` are the two parts of
  the folder icon (the design's two-tone folder, drawn until the shell's
  icon for that folder arrives, and in the sidebar). `fileTypeColors` is the
  stroke of the file glyphs by extension. The three `permission…` colours
  are the capability levels, and `permissionHigh` is also the colour of
  error text.
- **The accent.** The theme's `accent` also becomes WinUI's accent:
  `SystemAccentColor` and its six shades, and the accent brushes of
  WinUI's own controls. So buttons, toggles and text selection agree with
  the design's accent (the focus edge, the highlight pill, the drive bars,
  the app tile). The shades are made from the accent: for a dark theme the
  accent is Light2, the shade WinUI uses on dark (the design's `#60CDFF` is
  Windows' default Light2); for a light theme it is Dark1. `accent: null`
  uses the Windows accent and its shades, and follows a change of it in
  Windows settings while the window runs.
- **Mica.** `mica` sets the tint colour and opacity of the window's Mica
  (a `MicaController` in the app's own backdrop class, since `MicaBackdrop`
  has no tint). The tint is also the fallback colour for when Mica is off,
  for example with battery saver. `mica: null` is plain Mica.
- **Light and dark.** `kind: light` sets the window's `RequestedTheme` to
  Light, so WinUI's own controls and the dialogs draw their light forms;
  `dark` sets Dark. The caption buttons follow, and Tool Extension pages
  get it as `prefers-color-scheme`. `kind: system` (protocol 11; the
  shipped `default` theme has it) follows Windows' mode for apps: the
  window reads it from `UISettings` (Windows' background colour for apps,
  white in light mode) when it applies the theme, and applies the theme
  again when `ColorValuesChanged` brings another mode, while it runs.
- **Light mode of a `system` theme.** Its palette and terminal are its
  dark-mode colours ([themes.md](themes.md): the light look is the
  window's choice). In light mode the window uses its own light colours
  instead (`SystemLight`, in `CabinetOS.Core`, tested): Windows 11's own
  light-mode values from WinUI's Fluent resources, the Windows colour
  palette for the file types and levels, and Windows Terminal's
  "One Half Light" scheme (MIT) for the terminal. The theme's accent and
  Mica stay the theme's, so `default` shows the Windows accent's
  light-mode shade on plain light Mica (Constitution Article 3). The
  design has no light tokens yet (its handout lists light mode as an open
  question), so these are a first choice, not the design's.
- **The terminal.** The xterm.js page gets the theme's foreground, cursor
  and 16 ANSI colours, a selection in the accent at 30 %, and the scheme's
  background with alpha 0. Cells with the default background then show the
  panel (`terminalBackground`) over Mica, and reverse video uses the
  scheme's background, which xterm.js draws opaque.
- **The picker.** "Preferences: Color Theme" (`preferences.selectColorTheme`,
  Ctrl+K Ctrl+T) opens a list in the palette's frame, one row per theme:
  a swatch (the accent as a dot on the Mica tint; a theme without its own
  accent shows the Windows accent, and plain Mica shows light or dark Mica
  as the theme would now), the name, the author, "light" for a light
  theme or "light or dark, as Windows is set" for a `system` one, and a
  check on the theme in effect. Up, Down, Home and End move; Enter or a
  click applies it with `set_value ui.theme`. The picker closes once the
  core accepts, and `theme_changed` then repaints the window. A refusal
  shows in red in the footer. Esc or a click outside closes the picker.
  The tints come with `list_themes` (protocol 11), so opening it is one
  request; with an older core every swatch shows plain Mica. An open
  picker draws its swatches again when Windows' accent or mode changes.
- **A theme that cannot be used.** The core checks every theme before it
  sends one. If a colour still cannot be read, the mapper names its key in
  the log and nothing of that theme is applied; the last theme stays.
- **Logs.** Target `cabinetos_ui::theme`: "theme applied" with the ID, the
  kind, the mode it was drawn in (`light` or `dark`), the accent, and the
  Mica tint and opacity; "theme chosen"; "a theme token could not be set"
  (once per token) if a brush refuses a colour.

Checked on 2026-09-28 (release builds, locked screen): Nord named in
`cabinetos.json` was applied at start ("theme applied nord accent
#FF88C0D0 mica #FF2E3440 at 0.88"). The picker listed the four shipped
themes with their swatches and applied Rosé Pine Moon live: panes, the
accent, the terminal and the caption buttons changed at once. In pwsh under
Catppuccin Mocha, SGR 31, 32, 36, 94 and 95 drew the theme's red, green,
cyan, bright blue and bright magenta.

Checked on 2026-09-28 for protocol 11 (release builds, a fresh themes
folder through `CABINETOS_THEMES_DIR`, so the core wrote `default` 1.1.0
with `kind: system`). Windows was in dark mode, and the window drew dark
("theme applied default kind system mode dark"). The snapshot step
`mode:light` then made the window take Windows as light, through the same
method a change in Windows calls: panes, text, the accent, the terminal
(One Half Light) and the open theme picker turned light, and the log said
"mode light". `mode:windows` asked Windows again, and the window turned
dark. Windows' own setting was not changed for the check, so a real
change there (`ColorValuesChanged`) was not tried; it calls the same
method.

What a theme does not change:

- Four tokens stay as the design has them: the text on the accent
  (`CbOnAccentBrush`), the transfer graph's fill (`CbGraphFillBrush`), the
  red hover of close buttons (`CbDangerHoverFillBrush`) and the 40 % black
  scrim of dialogs (`CbScrimBrush`).
- Plugin tiles and the badges of plugin menu entries take their colour
  from the plugin's ID.
- Tool Extension pages get light or dark only. The tool messages have no
  theme colours yet ([tool-extensions.md](tool-extensions.md)).
- No light theme ships; `default` is light only in Windows' light mode. A
  light one from a scratch index turned the window, WinUI's own controls,
  the marketplace and the picker light at once (2026-09-28). Menus and
  tooltips were not opened in light mode. Dialogs take the window's light
  or dark when they open, so one that is open while the mode changes keeps
  the old one.

## Metrics and chrome

A theme may also change the window's sizes and show three more elements:
its `metrics` and its `chrome` ([themes.md](themes.md), "Metrics and
chrome"). They apply live, as the colours do: a `theme_changed` lays the
window out again, without a restart. A theme without `metrics` gives the
default look's sizes, so switching back to `default` restores every size.
A theme never changes a command, a key or where things are: the rule of
the Commander Compact handout
([design/compact/COMPACT_THEME.md](design/compact/COMPACT_THEME.md),
"Scope"). Constitution Articles 3, 6 and 8.

- **How.** `MetricsMapper` (in `CabinetOS.Core`, tested) is one table:
  every metric's name, unit and bounds, and the default look's value. A
  test holds the table equal to `sdk/themes/theme.schema.json`: the same
  76 names, the same bounds, and the default that each name's description
  gives. Another test checks every value of Commander Compact. The mapper
  gives one value for each name: the theme's, held to its bounds and
  rounded to whole pixels, or else the default look's. A name that is not
  a metric changes nothing, and the log names it. `ThemeMapper` adds the
  sizes and the chrome to the theme's look. The window keeps them in
  `WindowMetrics`, and each view lays itself out from them
  (`ApplyMetrics`). A row that the list reuses for another entry checks
  whether the sizes changed since it was made.
- **The log.** Target `cabinetos_ui::theme`: "metrics applied" with the
  theme, whether it is a density preset (`preset`: it sets any metric),
  the row height, the text size, the pane header's height, the three
  chrome switches, and the names it ignored.

### Where each metric goes

| Metrics | What they size in the window |
|---|---|
| `fontSize` | The base text: names in the file lists, pane titles, the sidebar's rows, the crumbs, the address and search fields, the rows of the palette, the menus and the prompts, and the rename box. The default look draws the design's 13 px; before this, these texts were WinUI's own 14 px. |
| `lineHeight` | Wrapped text: a pane's message ("This folder is empty.") and the search note. Single lines sit in rows of a fixed height, so it does not change them. |
| `backdropOpacity` | Mica. At 0.86 and below, plain Mica (the default look). Above it, Mica's own base colour is laid over Mica, up to opaque at 1: Commander Compact's 0.94 is a tint opacity of 0.91 in dark mode. A theme with a `mica` tint of its own keeps it. |
| `radiusControl` | Buttons, fields, the rows of the palette and the menus, the terminal's and the editor's buttons, and the marketplace's search field and buttons. WinUI's own controls made from then on take it too (`ControlCornerRadius`). |
| `radiusSurface` | The panes, the editor, the terminal and the marketplace. Icon tiles and info boxes (the app tile, the marketplace's tiles and stat boxes) keep the design's radius in proportion to it, and never less than `radiusControl`. |
| `gap`, `bodyPadding` | The space between the sidebar and the panes, between the two panes, and between the panes and the terminal, which is also the splitter; with no gap, the splitter keeps a 6 px handle laid over the edges it joins. The space at the window's sides and bottom. |
| `titleBarHeight` | The title bar, never lower than 32 px: Windows draws the minimize, maximize and close buttons that high. |
| `tabHeight`, `tabPaddingX`, `tabMinWidth`, `tabFontSize`, `tabRadius` | The workspace tab in the title bar. |
| `captionButtonWidth` | Nothing: Windows draws the caption buttons 46 px wide, and a window cannot change that. |
| `commandBarHeight`, `iconButtonSize`, `fieldHeight`, `toggleHeight` | The command bar; its icon buttons; the address and search fields (the crumbs inside are at most 4 px lower than the field); the Dual/Single toggle. |
| `sidebarMinWidth`, `sidebarWidthPercent`, `sidebarMaxWidth` | The sidebar's width. |
| `sidebarHeaderFontSize`, `sidebarHeaderPaddingTop`, `sidebarHeaderPaddingX`, `sidebarHeaderPaddingBottom` | The PINNED and DRIVES labels. The first label sits 8 px higher, as the design's first one does. |
| `sidebarRowHeight`, `sidebarRowInset`, `sidebarRowRadius` | The pinned folders' rows, and the inset and corners of every sidebar row. The default look's pinned rows are 32 px; its 34 px are for workspace rows, which do not exist yet. Between the rows, and above and below the sections, the space follows the inset. |
| `selectionBarWidth` | The accent bar of a selected or highlighted row: in the sidebar, the file lists, the palette, the prompts and the theme picker. |
| `driveRowPaddingY`, `driveRowPaddingX` | The drive rows. The pinned rows take the same side padding. |
| `tagRadius`, `tagFontSize` | Nothing yet: the sidebar has no tags. |
| `paneHeaderHeight`, `columnHeaderPaddingY`, `columnHeaderPaddingX` | A pane's header and its column headers. The header's sides take the column headers' padding. |
| `nameColumnWeight`, `modifiedColumnWeight`, `typeColumnWeight`, `nameColumnMinWidth`, `sizeColumnWidth`, `columnGap` | The columns, in the column headers and in every row alike. The default look keeps 8 px after the Modified and Type texts; a theme that sets `columnGap` spaces the columns by it instead. |
| `rowHeight`, `rowPaddingX`, `rowRadius`, `rowIconGap`, `secondaryFontSize` | A file row; the scrolling arithmetic (PageDown, keeping the focused row in view); and the rename box over a row, as high as the row allows but never lower than its text. |
| `editorTabHeight` | A pane's editor tab strip. |
| `markdownPaddingY`, `markdownPaddingX`, `markdownLineHeight` | Nothing yet: the Markdown Preview is a Tool Extension page, and the tool messages carry no sizes ([tool-extensions.md](tool-extensions.md)). |
| `hexRowHeight`, `hexColumnGap` | Nothing yet: there is no hex view. |
| `terminalDockMinHeight`, `terminalDockHeightPercent`, `terminalDockMaxHeight` | The bottom terminal dock's height, and the least a drag leaves it. |
| `terminalHeaderHeight`, `terminalTabHeight` | The dock's header and its session tabs. |
| `terminalPaddingY`, `terminalPaddingX`, `terminalLineHeight` | The terminal page, through its `theme` message. The page's own look (xterm.js's line height 1.25, and 8, 4, 4 and 12 px around the text) stands for the design's 1.6 and 10 by 12 px; a theme's values scale it by their share of those. |
| `marketplaceTabHeight`, `marketplaceTabRadius`, `marketplaceCardGap`, `marketplaceCardPaddingY`, `marketplaceCardPaddingX`, `marketplaceCardRadius` | The marketplace's tabs and cards. |
| `paletteRowHeight`, `menuRowHeight` | The rows of the palette and the theme picker; the rows of the context menu and of the prompts in the palette's frame (the pattern box, the drive list, the pinned folders). |
| `statusBarHeight`, `statusBarPaddingX`, `statusBarGap` | The status bar. |
| `fkeyBarHeight`, `fkeyBarGap`, `fkeyButtonRadius`, `fkeyBarFontSize` | The function-key bar, when `chrome.fkeyBar` shows it. |

### The chrome elements

- **`fkeyBar`: the function-key bar**, a row between the panes and the
  status bar: F3 View, F4 Edit, F5 Copy, F6 Move, F7 Mkdir, F8 Delete,
  Alt+F1 Drv. Each button runs the command its key runs (`file.view`,
  `file.edit`, `file.copyToOtherPane`, `file.moveToOtherPane`,
  `file.newFolder`, `file.delete`, `go.chooseDriveLeft`) through the
  router, with the trigger `fkeyBar`. The keys stay as the keymap has
  them: a button shows its handout key while the command is still bound to
  it, else the command's first key, else no key. The bar is never a Tab
  stop and never takes the keyboard, so a click acts on the active pane's
  selection, as the key would. A button's accessible name is its key and
  label ("F5 Copy"). When the bar is off, it and its row are gone. The key
  is in the accent colour, in Fira Code where it is installed and else in
  Cascadia Mono; the label is white at .8; a button is white at .05, and
  .12 under the mouse.
- **`rowStripes`**: every other row of a file list at .025 white, and a
  selected row at .12 over the stripes (.08 without them).
- **`hairlines`**: 1 px lines between the surfaces instead of floating
  cards. The command bar is filled (.03) with a line above and below it
  (.06). The sidebar has a line on its right (.08). With no gap, the two
  panes' borders overlap into one line. The active pane's header is filled
  (.06; a single pane's always is) over a line (.08). The column headers
  are filled (.03) over a line (.10). The list has no 4 px inset inside
  its pane: a card's inset goes with the card. The terminal dock has a line
  only on the edge that meets the panes (.10). The marketplace has no frame
  of its own.
- **The handout's text choices ride on `hairlines`**, which brings the
  narrow columns they are for: the Size column in fixed-width figures
  (Fira Code where installed, else Cascadia Mono); a drive's free space
  without "free" ("1.2 TB"); dates older than a week without their time
  ("2026-09-02"); and short types: "Folder", a link's kind, or the
  extension in capitals ("MD", "EXE"; "File" without one). The last two
  are not in the handout's list: its page shows short dates and types, and
  without them the shell's type names and long dates are cut short at
  924 px.
- **The shades** come from the theme's `textPrimary` at the handout's
  alphas, in `ThemeMapper`, so a light theme gets dark lines:
  `CbHairlineBrush`, `CbHairlineStrongBrush`, `CbBarFillBrush`,
  `CbHeaderActiveFillBrush`, `CbRowStripeBrush`,
  `CbStripedSelectedFillBrush`, `CbFkeyFillBrush`, `CbFkeyHoverFillBrush`
  and `CbFkeyLabelBrush`.
- **The preset's name.** When the theme sets any metric, the status bar's
  layout item starts with the theme's name: "Commander Compact · Terminal:
  bottom".
- **The picker** marks a theme whose `list_themes` item says
  `has_metrics` as a "density preset".
- **The overlays keep their corners** (8 px): the palette's frame (the
  palette, the prompts, the theme picker), the context menu, the transfer
  card and the dialogs.

### Checked

On 2026-09-29, with release builds, the display asleep, a screen scale of
1.5, the snapshot aid (its `size:`, `fit:`, `theme:` and `layout:` steps),
the shipped `commander-compact`, and folders shaped like the handout's
page (`fileforge` and its `src`, with the page's names, sizes and kinds
of dates):

- Dual pane at 924 px: rows 20 px high; no name, date, type or size cut
  short in either pane; the seven function keys 130 to 131 px wide, none
  cut short; no corner radius above 3 px outside the overlays.
- The list made 400 px high (`fit:400`): 20 whole rows of 20 px.
- At 1600 px, with the terminal open (a 200 px dock), in the marketplace
  and in its detail column: no corner radius above 3 px.
- The keymap: 67 bindings with the same fingerprint (a hash of every
  binding) at a start in `default`, in `commander-compact`, and after
  switching back.
- Switching back to `default` live gave the same sizes, number for
  number, as a fresh start in `default`: the title bar and its tab, the
  command bar and its controls, the body, the sidebar, both panes, their
  headers, rows and corners, and the status bar. The two images differ in
  80 of 3.24 million pixels, all on the edge of a rounded corner.
- F5 Copy pressed through its automation peer (the way UI Automation
  presses it) ran `file.copyToOtherPane` with the trigger `fkeyBar`, and
  the file was copied.
- For comparison, the default look at 924 px cuts 19 dates and types
  short in the same folders.

The snapshots, at half size:
[the dual pane at 924 px](log/2026-09-28/compact-dual-924.png),
[the list at 400 px](log/2026-09-28/compact-list-400.png),
[the dual pane at 1600 px](log/2026-09-28/compact-dual-1600.png),
[the terminal](log/2026-09-28/compact-terminal.png),
[the palette](log/2026-09-28/compact-palette.png),
[the marketplace](log/2026-09-28/compact-marketplace.png) and
[the theme picker](log/2026-09-28/compact-picker.png).
`livecheck.ps1` also switches to Commander Compact and back with the
picker's keys, and presses F5 through the bar's button by its accessible
name ("The live check").

### Where the window differs from the handout's page

- The title bar is 32 px high, not 30, and its caption buttons are 46 px
  wide, not 40: Windows draws them.
- The title bar has the app tile and one "Default" tab; the page has three
  workspace tabs and "+". The sidebar has no Workspaces and no Tags. Those
  features do not exist yet.
- Types are extensions ("MD", "LOCK", "EXE") where the page has words
  ("Markdown", "Lock file", "Application"): the window has only the
  shell's names, which do not fit. Old dates use the PC's short date
  ("2026-09-02"), not the page's "2 Sep 2026".
- The fonts are Segoe UI Variable and Cascadia Mono: the page's Open Sans
  and Fira Code are web fonts. Fira Code is used where it is installed.
- The status bar's prefix is the theme's name, "Commander Compact", with
  a capital C, and there is no git branch item.
- A pane's header has no list button at its right end.
- The terminal's padding and line height scale xterm.js's own values
  (about 5, 3, 2 and 8 px around the text, and a line height of 1.13)
  rather than the page's CSS (6 by 8 px, 1.45).
- The marketplace's icon tiles and stat boxes have 2 px corners, where the
  page keeps 8 and 6 px: the acceptance allows no radius above 3 px
  outside the palette and the context menu.
- The snapshots show a flat stand-in for Mica; the page draws
  `rgba(32,32,32,.94)` over a picture.
- The page's Markdown and hex views have no counterpart here: the Markdown
  Preview gets no sizes, and there is no hex view.

## The marketplace

The marketplace view (design view C) lists the extensions of the index the
core reads, and installs and removes them through the core.
[marketplace.md](marketplace.md) has the index format, the folders and the
trust rules (Constitution Articles 2, 8 and 10). The core does every
download, hash check and file operation; the view only asks and follows
the core's events.

- **Opening.** "Marketplace: Browse Plugins and Themes"
  (`marketplace.browse`, Ctrl+Shift+X) or the command bar's store button
  puts the view in place of the main column. The sidebar and the status
  bar stay, and the button turns to the accent colour. The same keys or
  button close the view.
- **Reading the index.** The first look asks the core to read it
  (`marketplace_refresh`); the Refresh button asks again. When
  `marketplace.index` changes in `cabinetos.json`, the next look reads the
  new index, or at once while the view is shown. The core goes to the
  network only for these requests (trust rule 6).
- **The nav** (180 px): Discover (everything), Plugins (Core Plugins and
  Tool Extensions, since both add function; the chip on each card tells
  them apart), Themes, and Installed, each with its count. Installed lists
  what the marketplace installed: the items that carry `installedVersion`
  (protocol 11), the core's record of its installs. A shipped theme or a
  plugin copied in by hand is not listed there.
- **Search.** The field ("Search plugins and themes") sends
  `marketplace_search` once typing pauses for 150 ms. Only the newest
  text's hits show, best first, narrowed by the tab. The caption says
  "{n} results · WebAssembly, sandboxed". An empty field shows the whole
  index again without asking the core.
- **Cards** (the design's grid: as many 230 px columns as fit, sharing the
  width, 10 px apart): the 40 px tile, the name with the verified check
  when the index says so, the author, two lines of description, the star
  rating and its count (or "No ratings"), the installs when the index
  knows them, and the kind chip ("WASM plugin", "Theme", "Tool"). The top
  right corner says "Installing 45%", "Update available", "Installed" or
  "Applied". "Installed" also marks an extension that is in its folder but
  did not come from the marketplace (`list_plugins`, `list_themes`,
  `list_tools`), because the core would refuse to install it (trust rule
  7). The tile shows the first letters of the name on a colour: a theme's
  own accent, else a colour taken from the ID. The core offers one item per
  extension, the newest version it can run (protocol 11), so the view shows
  the items as they come.
- **The detail column** (340 px, slides in over 180 ms) of the selected
  card: the 52 px tile, the name, "author · v{version}", the primary
  button, Source, Uninstall for what the marketplace installed, a line
  about the installed version or about who put the extension there, three
  tiles (rating, installs, download size), the long description, and for
  a plugin "Requested capabilities" with level dots and reasons (the core
  adds the levels). × or Esc closes it.
- **The primary button** says "Install", or "Install and apply" for a
  theme. While installing it says "Installing…", and a 2 px bar under the
  words follows `install_progress`. Installed, it says "Installed", or
  "Applied" for the theme in effect, and is disabled.
- **Updates.** When the offered version is newer than `installedVersion`
  (compared as numbers, so 0.10.0 is newer than 0.2.0), the card says
  "Update available", the button says "Update", and the line under the
  buttons says "Version 1.0.0 is installed. The update replaces its
  files." A plugin's update is reviewed first, as a first install is,
  because the core clears the plugin's grants (trust rule 1). A theme's
  update is not applied, since an update is not a choice of theme; the
  core applies it again when it is the theme in effect. The status bar
  then says "Paper is updated to version 1.1.0.". An installed version
  newer than the offer is kept: the button says "Installed", and the line
  names both versions. The view learns the installed versions at each
  refresh, from its own installs and uninstalls, and from
  `install_finished`, whichever client installed: its `installed_version`
  is the record's version after the install (an older one asked for with
  `cabinetos-cli market install --version`, or the one kept after a failed
  update); a core without that field leaves the offered version. The
  versions in a search reply are not taken, because a reply made before an
  install ended would undo it.
- **Installing a plugin.** The review dialog of "Plugins" opens first,
  with what the index says the plugin asks for. Nothing is downloaded
  before "Allow and install" (trust rule 1). Allow closes the dialog and
  sends `install_extension`. Once the core answers `ok`,
  `grant_capabilities` grants exactly those capabilities, so the core
  starts the plugin, and the status bar says "Hello is active.". The core
  installs every plugin waiting for review; this review counts as that
  review, so no second one opens. A plugin that asks for nothing installs
  without a review. Cancel or Esc installs nothing.
- **Installing a theme** installs it and makes it the theme in effect
  (`set_value ui.theme`); `theme_changed` then repaints the window.
- **Installing a tool.** The window reads its tools again at
  `tools_changed`, so the new tool opens files at once. An editor whose
  tool was removed closes.
- **Failures** show in red under the buttons and in the status bar, in the
  core's words: a download whose SHA-256 is not the index's, a CabinetOS
  that is too old, or something already there that the marketplace did not
  install.
- **Uninstall** is offered only for what the marketplace installed. It
  asks first ("Uninstall Hello?", with Cancel as the default button), then
  sends `uninstall_extension`. The theme in effect cannot be removed; its
  button is disabled and its tooltip says why. An extension the
  marketplace did not install (a shipped theme, a plugin copied by hand)
  has no Uninstall; the line under the buttons says why the marketplace
  leaves it alone. A tool removed elsewhere (the command line) leaves the
  Installed tab at the next `tools_changed`.
- **Source** opens the author's web page in the default browser with
  `Launcher.LaunchUriAsync`. It is the one call the view makes outside the
  core (a launch, not file I/O). Only an `http` or `https` address is
  opened, never a file or another scheme from an index. Without one, the
  button is disabled.
- **Empty and error states** say what happened and what to do:
  - "No marketplace index is set.", with the key `marketplace.index`;
  - "The marketplace index is not published yet.", for the placeholder
    address (a host ending in `.invalid`), with a hint to build a local
    index with `sdk/marketplace/build-index.ps1`;
  - "Cannot read the marketplace index.", with the core's reason;
  - "This core has no marketplace yet.", for a core before protocol 10;
  - "Nothing was installed from the marketplace yet.", which adds that
    shipped themes and extensions copied in by hand are not listed there,
    and "No results for …".
- **Keys.** The keyboard starts in the search field; Tab reaches the tabs
  and the cards, and Enter or Space selects one. Esc closes the detail
  column, then the view. A command that works on the panes (a sidebar
  folder, `go.*`, `pane.*`, `file.*`, `edit.*`, `search.*`, `terminal.*`,
  `editor.*`, Ctrl+`, Ctrl+Shift+D) closes the view first, so that what it
  does can be seen.
- **Logs.** Target `cabinetos_ui::market`: "marketplace shown",
  "marketplace index read" with the source and the number of items,
  "marketplace index not read" with the reason, "plugin installed and
  granted" with its version, "source page opened".

Checked on 2026-09-28 (release builds, locked screen) with the local index
of `build-index.ps1` and one light theme added to it for the check. The
grid listed the 6 fixture plugins and 5 themes; the 4 shipped themes showed
as installed, Default as applied. Hello's review listed `cmd:register` and
`events:emit`; "Allow and install" installed and granted it, and the status
bar said "Hello is active.". "Install and apply" of the light theme turned
the window light at once. The default placeholder index showed "not
published yet". Esc closed the detail column, then the view, and `go.toPath`
closed the view and went to the folder. An end-to-end test does the same
against the real core without a window: it builds the local index, reads
and searches it, installs Hello with its review's grant, waits until the
plugin is active, and uninstalls it.

Checked on 2026-09-28 for protocol 11 (release builds, scratch folders):
Paper 1.0.0 (a light theme added to the local index for the check) and
Hello were installed; the Installed tab listed exactly those two, and not
the four shipped themes. With the index then offering Paper 1.1.0 and the
Default theme in effect, Paper's card said "Update available", its button
"Update", and the line under it "Version 1.0.0 is installed. The update
replaces its files."; Uninstall was offered. Update installed 1.1.0
without applying it, the card and the button went back to "Installed",
and the status bar said "Paper is updated to version 1.1.0.". The
snapshot step `click:Installed` showed the tab.

## Scrolling

Phase 5's goal: while PageDown is held in the 100,000-entry folder, no
frame takes more than 33 ms, and fewer than 5 % take more than 20 ms, on
a quiet machine. A held key repeats about 30 times a second, and each
PageDown shows a new page of rows (30 at the default window size).

### How it is measured

With `CABINETOS_UI_FRAMESTATS=1` the window times the parts of the UI
thread's work a scroll causes (`FrameParts`): the rows' measure and
arrange passes (`RowLayout`, WinUI's `StackLayout` with its passes timed),
and inside the measure pass the rows bound to their entries and the rows'
own measure; outside it, the core's type-name pages applied
(`describe_entries`) and asked for, icons, the selection marks and the
status bar. Each frame is recorded with the gap before it, WinUI's own
time for it (`RenderedEventArgs.FrameDuration`), and how much of the
rows' layout ran inside that frame: WinUI also runs layout outside a
frame when the thread is otherwise idle. A frame's UI-thread work is
WinUI's frame time, the layout that ran outside it, and the other parts.

The snapshot aid's `scroll:150` presses PageDown 150 times, 30 a second;
a press that falls due during a slow frame is made at the next frame, as
queued key messages are. `scroll:150/2` presses once every second frame.
The run's frame table goes to the log (`scroll run`), with the machine's
CPU load during the run (all of it, this window, its core and the rest,
from `GetSystemTimes`). `ui/livecheck/scroll-bench.ps1` repeats the run
on the bench folder in a fresh window, waits while the machine is busier
than 30 %, and prints each run's table and the best and worst run:

```text
# PowerShell
powershell -ExecutionPolicy Bypass -File <repo>\ui\livecheck\scroll-bench.ps1 -Runs 3 [-Rhythm 2] [-Folder <folder>]
```

**The display must be awake for the gaps to mean anything.** When it
sleeps (15 minutes without input on this PC) or the screen is locked,
Windows asks for frames about 33 times a second, so a window that keeps
up shows gaps of about 31 to 33 ms, and every one counts as over 20 ms.
Each run therefore reports the window's idle frame clock (33 a second
asleep, 60 awake), and the frames whose UI-thread work passed 16.7 ms
(one frame at 60 Hz), which do not depend on the display. `-Rhythm 2`
presses once every second frame: on the 33-a-second clock that is the
rhythm of 30 presses a second on a 60 Hz display, one frame with a new
page and one without.

### Where the time goes

Measured on 2026-09-29 on the bench folder (`%TEMP%\cabinetos-bench\100000`,
five file types), release builds, 150 pages of 30 rows, the display
asleep. "Before" is the row as it was; the machine was at 16 to 37 % for
those runs and at 14 % for the runs after:

| | before (3 runs) | after (3 runs) |
|---|---|---|
| UI-thread work per second of scrolling | 576, 656, 731 ms | 436, 439, 439 ms |
| per page of 30 rows | 19 to 24 ms | 15 ms |
| frames whose work passed 16.7 ms | 54 %, 67 %, 71 % | 20 %, 21 %, 22 % |
| the same at one press every second frame | 36.5 % | 3.7 % |
| the busiest frame | 53 to 56 ms | 47 to 51 ms (the first press) |

The frame of the quietest run after the change, in milliseconds per
frame (all 154 frames): the UI-thread work 14.2, of it WinUI's frame 14.0
(the rows' measure pass 8.6, of which binding rows 0.7, the arrange pass
0.4, and the drawing), selection marks 0.12, the status bar 0.03,
type-name pages 0.01, requests 0.03, icons 0. The rows made: 4,432 for
4,500 rows scrolled, each once.

In `C:\Windows\System32` (4,930 entries, 629 programs, each with its own
icon), 140 pages: 593 ms of work per second of scrolling; icons took
0.70 ms per frame, 0.59 ms after their PNGs were decoded off the UI thread.

### What changed

- **A row sets only the values that changed** (`Shown<T>` in `FileRow`):
  a text, a visibility, the icon, its brush and the visual state. WinUI
  lays a text out again even when it gets the same string, and a
  recycled row usually shows the same time, type or size as before. This
  cut about a quarter of the work (the quietest runs: 576 to 437 ms per
  second). How much depends on the folder: the bench folder's files
  share their time and size.
- **An icon's PNG is decoded off the UI thread** (`IconBytes`): the base64
  text becomes a stream on a worker thread; the UI thread only makes the
  bitmap.

Tried and dropped, each measured the same way:

- A layout of fixed-height rows that made the page ahead in the idle
  frame, so a held PageDown would find its page ready. It cost more:
  WinUI draws rows only when they come into view, so the drawing, the
  larger part, stays in the frame that shows them, and the extra rows
  kept made added work of their own.
- Text trimming off (no change beyond noise once equal texts are
  skipped), another font, the reading order detected from the text (no
  change).
- Asking for type-name pages only after the scroll settles: not needed,
  they cost 0.01 to 0.06 ms per frame. The icons were already kept per
  key; the bench folder's 100,000 rows share five.

### What remains

About 15 ms of UI-thread work per page of 30 rows, nearly all of it in
WinUI: making or recycling a row costs about 100 µs, laying out a text
that changed about 38 µs (timed on the name), and drawing the rows that
came into view about 5 ms per page. At one press every second frame,
3.7 % of the frames' work passed 16.7 ms. The first PageDown after the
window starts makes rows from the template (the pool is empty), a frame
of 35 to 50 ms once. Whether no frame passes 33 ms and fewer than 5 %
pass 20 ms with the display awake is `livecheck.ps1`'s line, run with
real keys on a watched screen.

## Edge cases

What the shell does with the names, paths, links and windows that break
file managers, one class at a time, each with its tests
(`ui/CabinetOS.Tests`) and snapshots of a fixture. The fixture is the
one the core's checks use too: `sdk/fixtures/edge-fixture.ps1 -Root
<folder>` (Windows PowerShell or PowerShell 7; it removes and remakes only
a folder it made). `ui/livecheck/edge-snapshots.ps1` makes it, adds
`deep notes.md` at the end of the long path, and runs the window over it
with the snapshot aid (no keys, so the screen may be locked): both panes
on the fixture, a Cyrillic rename, a Cyrillic search, Enter into the long
path and on its Markdown file, and Shift+Delete of the junction; then it
checks the disk and the log. `ui/livecheck/livecheck.ps1` does the same
with real keys ("The live check"). Snapshots of 2026-09-29 in
[log/2026-09-28/](log/2026-09-28/): `edge-a-names.png` (the crumbs, a
conflict, the search and the terminal line with Cyrillic names),
`edge-b-crumbs.png` and `edge-b-lines.png` (long paths),
`edge-c-links.png` (a junction), `edge-e-fixture.png` and
`edge-e-bin-conflict.png` (the scripted run).

### Names beyond ASCII

The fixture's `names\` folder holds `Звіт 2026.txt`, `Ґанок`,
`Їжак і Єнот.md`, `日本語のファイル.txt`, `中文文件夹`, `📁 photos`,
`𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt` (surrogate pairs), `café.txt` in NFC and in NFD side by
side, `مستند.txt` (right to left), a 255-unit name, and in a
case-sensitive folder `Report.txt` and `report.txt`.

- **The order is the core's.** The core sorts in Explorer's natural order
  (case-insensitive, digits as numbers, the user's locale), folders first;
  the shell shows the rows exactly in that order and never sorts. For the
  fixture: `case`, `Ґанок`, `📁 photos`, `中文文件夹`, then the 255-unit
  name, the two `café.txt`, `Звіт 2026.txt`, `Їжак і Єнот.md`,
  `مستند.txt`, `𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt`, `日本語のファイル.txt`.
- **A name is its units.** The listing's names come back unit for unit,
  so NFC and NFD `café.txt` are two rows and two names, and a name is
  found exactly before it is found in another case: in a case-sensitive
  folder, selecting `report.txt` after a rename no longer picks
  `Report.txt` (`ListingView.IndexOfName`).
- **Right-to-left names keep their extension on the right.** Every text
  of the window takes the window's reading order, left to right, as
  Explorer does; WinUI's default took it from the first letter and showed
  `مستند.txt` as `txt.مستند` (an implicit `TextBlock` style in
  `App.xaml`).
- **F2 selects the stem**: the name up to its last dot, not a leading one
  (`Їжак і Єнот` of `Їжак і Єнот.md`, `cafe` with its combining accent,
  all of `.gitignore`), in UTF-16 units, so it never ends inside a
  surrogate pair (`DisplayFormat.RenameStem`).
- **Properties names the type the Type column shows** ("Markdown Source
  File"); it used the built-in "MD File" before (`PropertiesText`).
- The crumbs, the address box, the pane titles, the status bar, the
  conflict card, the search field and its hits, and the terminal's folder
  line showed every fixture name whole (snapshots, 2026-09-29). A Cyrillic
  query in the palette finds no command and says "0 commands".
- Typing Cyrillic with Unicode key events (the touch keyboard's way) into
  the address box, the search field and the rename box goes to WinUI's
  own text boxes, which took such events correctly in the live check; the
  real-key check is `ui/livecheck/livecheck.ps1`'s.

Tests: `NamesBeyondAsciiTests` (14).

### Long paths

The fixture's `long\` folder nests `segment-of-a-long-path-0123456789`
until the path passes 300 characters, with `deep file.txt` (and
`deep notes.md`, which the shell's checks add) at the bottom. Under
`%TEMP%\cabinetos-edge` on this PC the folder has 325 characters and its
files 339; the first snapshots used a fixture with a 333-character
folder.

- **The crumbs keep the drive, a "…", and the last folders that fit**
  (`CrumbFit`, in pixels), so the bar never grows and the folder shown is
  always there. The "…" opens a menu of the folders left out, the drive's
  end first; each goes there. A single name wider than the bar ends in a
  "…" of its own. The fit is made again when the window changes size.
  Before, the bar scrolled to its end: the drive and the start of the path
  were cut off with no sign.
- **The pane header's path and the transfer flyout's line keep the drive
  and the last names around "…"** (`DisplayFormat.ShortPath`), never
  cutting a name: `C:\…\deep file.txt → C:\…\edge\names\Ґанок`. Before,
  the flyout cut the line's end, which lost the file and the destination.
  The whole text is in the tooltip. The crumbs and `ShortPath` read
  `\\?\C:\…` as the drive and `\\?\UNC\server\share\…` as the share.
- The address box shows the whole path, selected and scrolled to its end.
  Properties wraps the Location. The status bar names the selected row,
  not its path. The search's note wraps the searched folder, and the
  hits' Folder column starts at the searched folder's name. All unchanged.
- **In and out:** Enter on the last folder goes into the long path; Up,
  Back and a folder of the "…" menu go out and come back.
- **A tool is not offered a file whose path has more than 259
  characters.** WebView2 showed a 255-character path from a 250-character
  folder, failed to fetch a 272-character path from the same folder, and
  refused to serve the 333-character folder at all, with Windows' long
  paths turned on (2026-09-29). So the status bar says "Markdown Preview
  cannot show deep notes.md: its path has 339 characters, and WebView2
  reads none longer than 259." (`ToolFileSession.LongestPath`). Before,
  Enter on `deep notes.md` left the preview empty and logged "UI command
  handler failed". A folder WebView2 refuses for another reason (one
  deleted since the listing) ends the open the same way, with WebView2's
  reason.
- Enter on `deep file.txt` asks the core's `open_path` to start its
  program. On this PC (Windows 11, long paths turned on) the shell took
  the 339-character path and Notepad opened the file (2026-09-29). Where
  the shell refuses a path of 260 characters or more, the core answers
  `invalid_path` with the length and the limit, and the status bar says
  "Cannot open deep file.txt: …". The scripted checks do not press Enter
  on it, so they start no program.
- **Delete (to the Recycle Bin) of a long path** stops at a
  `path_too_long` conflict: the core measures the item's longest path
  first, since Windows' shell would delete it for good without asking
  (docs/jobs.md, "The Recycle Bin and long paths"). The card says "has a
  path too long for the Recycle Bin" and offers Delete permanently, Skip
  and Retry, as for a bin too small; in a copy or a move the kind keeps
  "the path is too long for the destination" and its own answers
  (`ConflictText.Options`).
- Tooltips that hold a whole path (the flyout's line and the conflict's
  name, the "…" menu's folders, the editor tab's file) wrap at WinUI's
  tooltip width; they are not in the snapshots, since the snapshot aid
  opens only the window's own tooltip objects.

Tests: `LongPathTests` (11), three in `ToolTests` for a path or a folder
WebView2 cannot serve, and one in `ConflictTests` for the Recycle Bin's
long-path conflict.

### Links and cloud files

The fixture's `links\` folder holds `junction to target`, a junction to
`link-target\` and its files `kept 1.txt` to `kept 3.txt`, and two
symbolic links, `symlink to target` and `symlink to kept 1.txt`, when
Windows lets the user make them (an administrator or Developer Mode). A
mount point needs an administrator, so the fixture has none. Their words
are tested on fake listings; so are cloud placeholders, which need a sync
provider such as OneDrive. `loop\` holds a junction back to its own
folder, for the core's walks.

- **A link says so.** Its icon carries a small chain badge on the lower
  left, where Explorer draws its arrow, and it stays when the shell's icon
  arrives. The Type column and Properties name the link, not its target:
  the core names a type by the folder attribute, so a junction was "File
  folder" once its details came (after "Folder link" before them). Now it
  is "Junction", "Mount point", "Symbolic link to a folder" or "Symbolic
  link to a file", from the flags the listing gives a link (kind 3):
  junction 2, symbolic link 4, mount point 8 (docs/ipc.md, "The listing
  section"). `EntryFacts.LinkOf` is the one place that reads them. A link
  whose kind no flag names, such as a WSL link, is "Link to a folder" or
  "Link to a file" (`DisplayFormat.RowType`). The entry's reparse tag (the
  `IO_REPARSE_TAG_*` value, `ListingView.ReparseTag`) is read but shown
  nowhere yet.
- **Shift+Delete on a link names it**: "Delete the link permanently?" and
  "“junction to target” is a link. Only the link is deleted; the folder it
  points to keeps its files." Several rows with links among them end with
  "1 of them is a link: only the link is deleted, not what it points to."
  The question never counted the items behind a row (`DeleteText`). What
  the delete does to the files is the core's (its jobs delete a link as a
  link); the live check deletes the fixture's junction and looks at
  `link-target\` on disk afterwards.
- **A row not on this disk** (the attributes `RECALL_ON_DATA_ACCESS`,
  `RECALL_ON_OPEN` or `OFFLINE`: OneDrive's "online only", a folder whose
  list is still in the cloud, an offline file; the core sets the entry's
  flag 16 from the same attributes, so the shell reads the attributes)
  shows a cloud after its
  name, "Not on this disk: opening it downloads it". Its Size is the
  listing's, which is the file's logical size, not the space it takes.
  "Always keep on this device" and a file downloaded once carry none of
  these, and show no cloud.
- **Showing such a row reads nothing from it.** The only request of a row
  that reads its file is `get_icon` with a `path:` key (programs, icons and
  shortcuts carry their own icon). For a row not on this disk the shell
  asks for its extension's icon instead (`ext:.exe`, drawn without opening
  a file), so the core is never asked to read a placeholder to draw it
  (`DisplayFormat.IconKeyFor`). The type names are the core's by extension
  and read no file either (docs/ipc.md, "Type names and icons").

Tests: `LinksAndCloudFilesTests` (22).

### Two windows

A second window is a second `CabinetOS.exe` with a core of its own (PLAN.md,
"Process layout"). Both read and write one `cabinetos.json`, one log
folder and WebView2's data folders.

- **`window.new`** starts another window at the active pane's folder
  (`--path <folder>`; `WindowArgs`), with this window's `--tools-dir`. The
  new window does not inherit the snapshot aid's variables, which would
  make it run this window's steps again. The core does not list
  `window.new` yet, so the palette has no row and no key for it; the
  window's handler runs it all the same (from a tool, a plugin or the
  snapshot aid) and will serve the row when the core adds it.
- **Settings follow, and nothing fights.** A setting one window writes
  (`set_value`) reaches the other as `config_changed`; the other applies
  it and writes nothing back, and says so in its log ("dual pane follows
  the configuration", "the sidebar follows the configuration"). The last
  window to close writes `ui.lastPaths`.
- **Closing one leaves the other**: its core ends with it, and the other
  window, its core and its terminal go on.
- **One log file, every line whole.** Each window's log writer holds
  today's `ui.<date>.jsonl` with the right to append only, so Windows puts
  every write at the file's end, and each batch of lines is one write.
  Before, each writer wrote where it thought the end was: two writers kept
  3,000 of 6,000 lines, the rest written over (`LogWriter`).
- **One WebView2 data folder per page kind, shared**: both windows'
  terminals run in one browser process (the same `browser_pid` in the log).
  WebView2 supports this, and closing one window leaves the other's page
  running. A crash of that browser process stops the terminal in both
  windows; each brings its own back with Reload.

Tests: `TwoWindowsTests` (2) and one in `DiagnosticsTests` (two writers
on one file). The end-to-end test starts two real windows for about half
a minute: B, then A, both opening the terminal; A turns dual pane off and
closes; B follows, keeps its core, and runs a command after A is gone. It
opens windows on the desktop, so it runs only with `CABINETOS_UI_E2E=1`
(and skips otherwise). Checked with the snapshot aid: `window.new` in a
pane at `names\Ґанок` started a window whose left pane opened there, with
its own core.

### The fixture in the live checks

`ui/livecheck/edge-snapshots.ps1` ran on 2026-09-29 (release builds,
Windows PowerShell 5.1) and answered yes to each of its checks:

- both panes on the fixture: `names\` in the core's order on the left,
  the 325-character folder on the right;
- F2 on `Звіт 2026.txt` and `Звіт 2027.txt` typed in: the file on disk
  has the new name;
- the search "звіт" found `Звіт 2027.txt` in `names` and `Звіт 2026.txt`
  in `names\Ґанок`;
- Enter on the last folder went into the long path, and Enter on
  `deep notes.md` there put the preview's reason in the status bar;
- Delete of `deep file.txt` there stopped at "has a path too long for the
  Recycle Bin" with Delete permanently, Skip and Retry; Skip left the file;
- Shift+Delete on `junction to target` asked "Delete the link
  permanently?"; after Delete permanently the junction was gone and
  `kept 1.txt` to `kept 3.txt` were still in `link-target\`;
- every line of the window's log was whole.

The window now logs what the status bar says ("notice shown"), which the
checks read. `ui/livecheck/livecheck.ps1` has the same steps with real
keys (a section "Edge cases" before it closes the window); its run is the
planning session's, on a screen someone watches.

## Not in this version

| What | Why |
|---|---|
| Publisher identities, "Trust {author}", rating an extension, update checks in the background | Not in Phase 9 ([marketplace.md](marketplace.md), "Not yet"); an update shows when the index is read (Refresh) |
| A virtualized card grid | Every card of the tab is made; fine for an index of hundreds |
| Tools in the Tool Dock, and tools opened without a file | The dock holds the terminal; tools open as editor tabs ([tool-extensions.md](tool-extensions.md), "Not yet") |
| Reattaching to shells after the UI restarts | The UI starts its own core, and the core closes its shells when it stops, so there is nothing to reattach to (`terminal_list` is ready for it) |
| Light-mode tokens from the design | The design has none yet; a `system` theme in light mode uses Windows 11's own light colours ("Themes") |
| Workspaces (title-bar tabs, sidebar section) and Tags | One static "Default" tab; both sidebar sections stay hidden (Article 4) |
| Sorting by a click on a column heading | The headings are static; Ctrl+F3 to Ctrl+F6 sort a pane ("A pane's order"), and sub-phase 11c brings the headings |
| Pasting files copied in Explorer, drag and drop | The in-app clipboard only |
| The metrics `captionButtonWidth`, `tagRadius`, `tagFontSize`, `markdownPaddingY`, `markdownPaddingX`, `markdownLineHeight`, `hexRowHeight` and `hexColumnGap` | Windows draws the caption buttons; there are no tags yet, the tool messages carry no sizes, and there is no hex view ("Metrics and chrome") |

## Known gaps

What is built but not finished, as of the self-review of 2026-09-28 and
the protocol 11 work that night:

- **Real keys: checked once, and some fixes wait for the re-run.** The
  live check of 2026-09-28 ([log/2026-09-28/live-check.md](log/2026-09-28/live-check.md))
  passed the window, the palette and rebinding, scrolling 100,000 entries
  with PageDown held, the file keys, the conflict card, the terminal's keys
  and search. Its five findings are fixed. A re-run of `ui/livecheck/`
  on 2026-09-29 confirmed four with real input (Ctrl+K V on the open
  preview, Esc in a dialog and a key under it, Unicode key events in the
  terminal, the keyboard after Skip through UI Automation); the pencil's
  tooltip still opened after the palette closed, which the guarded
  tooltips answer and the next run checks. Not checked with
  real keys yet: Ctrl+Shift+X, Ctrl+K Ctrl+T and Tab through the
  marketplace, the uninstall confirmation, a real drag of the dock's
  splitter, keys inside the Markdown Preview, menus and tooltips in light
  mode, the window following a real change of Windows' light or dark
  mode, and Commander Compact chosen in the picker with Tab and the
  function-key bar (the live check has the step; its snapshot-aid
  counterpart passed, "Metrics and chrome").
- **The icon cache has no limit.** Every icon the core sent stays until
  the window closes. They are 16 to 32 px bitmaps, so a session that shows
  thousands of program icons holds a few MB.
- **WinUI's clear button (×).** The palette's input and the search fields
  show it while they have the keyboard; the design has none there. The
  rename box hides it, since a click on it would end the edit.
- **Esc and the transfer flyout.** The flyout never takes the keyboard, so
  Esc reaches it only last, after everything else Esc closes.
