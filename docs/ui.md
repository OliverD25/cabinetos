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
| Scrolling that folder with PageDown held for 5 s | measured on 2026-09-30 with real keys, the display awake: none of about 300 frames more than 20 ms apart, the worst 17.8 ms, about 230 ms of UI-thread work per second; 27 to 32 % of the gaps over 20 ms before ("Scrolling") |
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
  Commander's keys, with the preview still open in the other pane until
  Ctrl+Alt+P has shown the terminal (whose page must then have the keyboard:
  "The terminal", "Handing the keyboard to the page"), Commander Compact chosen in the theme picker with its
  keys (the window's log must say 20 px rows; Tab must never land on a
  function key; F5 pressed through the bar's button by its accessible name
  must copy a file; the scroll bar's thumb thrown from the top to the bottom
  of the 100,000-entry folder must leave no empty rows in the screenshot) and
  switched back, the tabs ("Tabs"), what plugins ask of
  the window (a row dragged onto a tool's page; the Agent installed through
  the marketplace with its fake provider, asked to rename three files, the
  preview applied with Enter: "What plugins ask of the window"), section 13,
  the rail layout (below), the edge cases, section 16, the shell of
  Phase 16 ("The shell"), and section 18, the context menu: a program and a
  file menu written into the run's configuration, a right-click, a click on
  the program (the path it got is checked), Shift+F10 and Esc, the file
  menu edited inside the menu (a click outside, the program's X, Done, the
  core's log line and the file checked, then a second edit that puts the
  program back with Insert, the prompt, a drag with the real mouse,
  Alt+Down, Alt+Up and Ctrl+S), Shift+right-click with Windows' Copy (the
  clipboard is read back), and the empty space's menu ("The context menu",
  "Editing the menu"). Section 19, the column widths: the left pane's
  Modified|Type grip dragged 40 px with the real mouse, a real double-click
  on its Type heading, and the palette's Reset Column Widths ("Column
  widths"). Section 20, the column view: Ctrl+Alt+C, Enter on two
  folder rows, Left twice and Ctrl+Alt+C back, read from the window's
  "column view changed" lines ("The column view"). Step 19b sits with Total Commander's keys: the palette's
  Toggle Folder Sizes turns folder sizes on, a folder with two folders is
  opened and its count read from the window's log, and the same command
  turns them off ("Folder sizes"). Then the compact overlay: Ctrl+Alt+Up
  with real keys, the window's size and topmost style read from Windows,
  and the key again ("Compact overlay"). Last, "keys": the states the keys
  audit of 2026-10-01 found wrong, with real keys
  ([log/2026-10-01/keys-audit-report.md](log/2026-10-01/keys-audit-report.md)):
  Shift+Delete answered with Tab and Enter (the file must be gone), Tab
  into a Markdown Preview in the other pane (its page must have the
  keyboard), Tab and Enter in the theme picker (the theme applies, nothing
  opens in the pane), Ctrl+K held until Windows repeats it and then Ctrl+T
  (the picker opens), Ctrl+B held for six key-downs (the sidebar toggles
  once). Then the find box, with the other pane on the fixture's folder
  `sub` (Ctrl+Right shows it there; F5 is pressed only when the log shows
  both panes on the fixture): a letter filters, Enter finds, F5 copies the
  cursor row (the file must be in `sub`), Ctrl+A selects the box's text
  (the next letter replaces it), Ctrl+T and Ctrl+W open and close a tab,
  and Ctrl+K Ctrl+T opens the theme picker. Then the window's own keyboard layout goes to
  Ukrainian (`WM_INPUTLANGCHANGEREQUEST` to the window, never the
  system's default), Ctrl+T and Ctrl+W go as physical keys (scan codes,
  so the layout decides the virtual key) and must run `tab.new` and
  `tab.close`, and the layout goes back; a PC without the Ukrainian layout
  says "not installed" and skips it.
- `livecheck2.ps1`: the input paths. Skip by a real mouse click, then
  Properties with the same checks, the terminal typed with virtual-key
  events and with Unicode key events, and Ctrl+K V twice on the open
  preview.
- `claude-terminal.ps1`: Claude Code in the terminal's `claude` profile
  ([terminal.md](terminal.md), "Profiles"). A fresh window on the
  repository folder with `terminal.defaultProfile: claude`; Ctrl+` starts
  Claude Code in the pane's folder; a long answer cut short with Esc; a
  prompt that makes it write a file, its permission question answered
  with Enter; Backspace in the pane, whose folder change the sync must
  skip (decision `SkipProfile`); Ctrl+Alt+P from the pane, which types the
  path into its input; `/exit`. Two short prompts go to the Claude login
  of whoever runs it. It waits until nobody has touched the PC for 12 s
  before it takes the keyboard, stops when another window comes in front,
  clears every `CLAUDE*` variable first (Claude Code refuses to start
  inside another Claude Code session), and opens `DONE.md` at the end.
  Every check True on 2026-09-30
  ([log/2026-09-30/claude-code-in-the-terminal.md](log/2026-09-30/claude-code-in-the-terminal.md)).

#### The live check on another machine

`remote-livecheck.ps1` runs the live check on another Windows machine over
SSH, so this PC's keyboard and mouse stay free: the creator's Omen laptop
(`omen` in this PC's SSH config, set up 2026-10-01). The machine holds a
clone of the repository made from a git bundle (it has no GitHub login), a
scheduled task `CabinetOS-LiveCheck` that starts `run-livecheck.ps1` in the
logged-in session (a process started over SSH gets no desktop; the task is
what gives it one), the .NET Desktop Runtime and the Windows App Runtime the
window needs, and the Ukrainian keyboard layout; it builds nothing. The
script sends the commits the clone lacks as a bundle, copies this PC's
Release window and release core into the clone's build paths, starts the
task, waits for the run's `DONE.md`, and copies the run's output into
`_io\live-check` here as `run-<time>-<machine>.txt`. The machine must be
logged in and unlocked; its own countdown window shows there first. The
frame numbers of such a run come from that machine's graphics card: on the
Omen laptop the integrated AMD Radeon, which drives its 144 Hz screen, not
the GTX 1660 Ti, and there the scroll goal does not hold (each page of new
rows reaches the screen about 90 ms after the UI thread's 8 ms of work; the
portability report has the numbers), so the laptop's run judges the goal by
the window's UI work (`-Panel`, below).
`-Branch <name>` sends another branch than `main` (a coder's worktree sends
its own; it must be a fast-forward of what the clone has), and `-Io <folder>`
names where the output lands.

The three remote scripts first make sure someone is signed in on the
machine (`quser` shows an Active session): a window run needs a signed-in
desktop, and after a Windows Update restart nobody is signed in (the laptop
restarted itself at 00:41 on 2026-10-02 for the September update). They stop
with "nobody is signed in" instead of starting a task that would fail late.

What else the machine needs, and what the script and the check do about it
(the portability pass of 2026-10-01,
[log/2026-10-01/live-check-portability-report.md](log/2026-10-01/live-check-portability-report.md)):

- **PowerShell 7** (`pwsh`): the terminal's default profile, which the
  terminal steps type into. The check itself runs in Windows PowerShell 5.1.
- **No long-path setting.** Windows' `LongPathsEnabled` may stay 0, the
  default: the edge fixture writes its 255-unit name through the `\\?\`
  form, which Windows PowerShell's .NET needs for a path of 260 characters
  or more when the setting is off.
- **The bench's 100,000-entry folder**, `%TEMP%\cabinetos-bench`, which
  `cargo bench` makes where Rust is: the script runs `bench-folders.ps1`
  there first, which makes the same folders with the same names (a minute
  or two the first time; a folder with its `.complete` marker is left
  alone). `livecheck.ps1` stops when the folder is missing, and checks that
  the PageDown hold runs in it.
- **The Agent plugin**, `sdk\extensions\agent\plugin\plugin.wasm`, which
  `build-extensions.ps1` builds where Rust is: the script copies the
  committed `sdk\fixtures\plugins\agent\plugin.wasm` there when the place
  is empty or the fixture is newer. It is the same plugin, with the same
  `plugin.json`.
- **AppData hidden or not.** A stock Windows hides AppData, so every folder
  of a run (they are under `%TEMP%`) is under a hidden folder. Since
  2026-10-01 the tree opens a hidden folder that lies on the active pane's
  path (drawn dim), so the run no longer needs `panes.showHidden` for the
  tree. Section 13 still turns that setting on while it runs and takes it
  out at its end, as it did before the tree could.
- **No window of an earlier run.** A run deletes its own folder,
  `%TEMP%\cabinetos-ui-test\live`, before it starts the window. A window
  that a stopped run left open holds files in that folder (its logs, its
  WebView2 data, its terminal's shell), so it must be closed first. A run
  that dies on an error closes its own window and core (a `trap` in
  `livecheck.ps1`), with a STOP line that `DONE.md` shows.

`remote-tests.ps1` runs the window's tests on the laptop the same way,
so the end-to-end tests' windows open there: it sends the commits as a
bundle, copies this PC's release core when the clone has none, writes the
request (a `--filter`, whether the end-to-end tests run) into the
laptop's `_io\inbox`, starts the task `CabinetOS-Tests` (its wrapper sets
the per-user .NET SDK's variables, builds with warnings as errors and runs
`dotnet test`), waits for `DONE-tests.md`, and copies the output into
`_io\test-runs` here as `tests-<time>-<machine>.txt`. First run
2026-10-01: `-Filter "FullyQualifiedName~KeysEndToEnd" -EndToEnd`, 8 of 8
passed there, the build and the tests in three minutes.

`remote-script.ps1` runs any script of the repository on the laptop the
same way, in its logged-in session, through the task `CabinetOS-Script`
and its wrapper `C:\Dev\cabinetos\_io\run-script-laptop.ps1`: a bundle of
the commits, the request (the script's path and its arguments) in the
laptop's inbox, the task, a wait for `DONE-script.md`, the output copied
home as `_io\script-runs\script-<time>-<machine>.txt`. `-Env
"NAME=VALUE;NAME=VALUE"` sets variables for the script and the window it
starts; `-CopyBuilds` sends this PC's Release window and core first. The
wrapper sets `CABINETOS_UI_FRAMESTATS=1`, so `scroll-keys.ps1` and
`scroll-bench.ps1` measure there as they do here. First use 2026-10-01:
the scroll gaps of the laptop
([log/2026-10-01/scroll-gaps-laptop.md](log/2026-10-01/scroll-gaps-laptop.md)).

**A virtual machine** (the VirtualBox VM, for example) runs the check with
`-Virtual` (`run-livecheck.ps1 -Virtual` passes it on to `livecheck.ps1`).
Its frames come from a virtual graphics card, so the checks that judge frame
times, today the scroll goal line ("no frame over 33 ms, under 5 % over
20 ms"), print their numbers and answer "not measured in a VM" instead of
yes or no; `-Strict` does not judge them, and `DONE.md` counts them apart,
neither True nor False. Every other check stays as strict as on a real
machine.

**A laptop panel** (the Omen laptop) runs the check with `-Panel`
(`run-livecheck.ps1 -Panel` passes it on to `livecheck.ps1`; the laptop's
wrapper, `C:\Dev\cabinetos\_io\run-livecheck-laptop.ps1`, which lives on the
laptop and not in the repository, passes it). The panel's display path sleeps
between pages and wakes in about 80 ms, so the gaps between frames there are
the display's wait and not the window's drawing
([log/2026-10-01/scroll-gaps-laptop.md](log/2026-10-01/scroll-gaps-laptop.md)).
With `-Panel` the scroll goal line still prints the gap numbers, exactly as
before, but answers "not judged on a panel" instead of yes or no. The line
after it, "panel goal (no frame with UI work over 33 ms, under 5 % with UI work
over 20 ms) met: yes|no", asks the same two limits of the frames' own UI-thread
work: the window's `busy_over_20ms` and `busy_over_33ms` counts in its
per-second `frame stats` lines, the same measure as the frame table's "UI work"
column. `-Strict` judges that line, `DONE.md` shows it, and `DONE.md` counts the
"not judged on a panel" line apart, neither True nor False. A window that logs
no such counts cannot meet the panel goal, so a goal never passes for want of
data. `-Virtual` wins when both switches are given. Every other check stays as
strict as on a desktop.

#### The live check in a virtual machine

`vm-livecheck.ps1` runs the live check inside the VirtualBox VM
"CabinetOS-LiveCheck" on this PC (made 2026-10-01: VirtualBox 7.2, Windows
11 Pro, 4 CPUs, 8 GB, a 1920x1080 screen, the user `cabinetos` logged in on
its own at start), so this PC's keyboard and mouse stay free: the VM has
its own, and a run there is a run on a stock Windows with nothing of this
PC's setup. The VM sees the project's root folder on this PC as the shared
folder `X:`, so it reads the repository from `X:\cabinetos` and writes the
run's output straight into `X:\_io\live-check`, which is `_io\live-check`
here; nothing is copied over a network and the VM holds no clone. The VM
has the per-user .NET runtime and PowerShell 7 in its user's LocalAppData,
the Ukrainian keyboard layout, no sleep, and no popups (`_io\vm\vm-setup.ps1`
did that once; `_io\vm\vm-user.txt` holds the user's password, which the
script reads and never prints).

What the script does: starts the VM when it is off (headless, so no window
on this PC) and waits for its desktop; through VirtualBox's guest control
(`VBoxManage guestcontrol run`), starts `vm-guest.ps1` of this repository
in the VM's own session (a process started by guest control runs on the
VM's desktop, so the VM needs no scheduled task, unlike the laptop);
`vm-guest.ps1` copies the repository's tree and its Release window and
release core from `X:` onto the VM's own disk, `C:\cabinetos\cabinetos`,
puts the Agent plugin and the bench's folders in place as the laptop script
does, and runs `run-livecheck.ps1` there with `-Virtual`, `-NoCountdown`
and `-MinimizeOthers`; this script waits for the run's `DONE.md` in
`_io\live-check` here, takes a screenshot of the VM's screen next to it
(`vm-screen-<time>.png`), and prints `DONE.md`. The output is
`run-<time>.txt` as for a run on this PC, and `vm-progress.txt` says how
far the VM got, the first thing to read when `DONE.md` does not come. A
git worktree runs its own tree: every checkout under the project's root is
under `X:` too, and the script hands the VM the path of the repository it
is in. The VM is a stock machine with no Rust and no .NET SDK, so the
builds of this PC are what it runs; build them first (the Release window
and the release core). A VM's frames come from a virtual graphics card,
which is what `-Virtual` is for (above). The first run in a VM makes the
bench's folders there, which took about seven minutes more than a later
run (the 100,000 files in 135 s on the virtual disk, 2026-10-01).
`-Restart` restarts the VM first (the power button, then a hard power-off
when the VM ignores it, as it did every time on 2026-10-01). It is the one
cure seen for two things: a guest control that answers "Error starting
guest session" to everything (it did so twice that day, each time after a
guest command that did not return), and the WebView2 processes a killed
window leaves behind, which cannot be ended, hold the run's folder, and
stop the next run at once with "The process cannot access the file".
The screenshot the script takes at the end is made inside the VM, from
its own desktop: VirtualBox's own screenshot of a headless VM with 3D
acceleration is a stale frame.

On this PC a window run (the end-to-end suite, the live check, the speed
runner, a manual start) also needs the creator's consent, their rule of
2026-10-02: the planning session asks in the chat, a yes or one minute of
silence is consent, a deliberate no is not, and the answer is recorded with
`wait-for-pc.ps1 -Set allowed` or `-Set held` (`-Until "yyyy-MM-dd HH:mm"`
for a time the creator named) in `%LOCALAPPDATA%\CabinetOS-dev\window-runs.txt`,
outside every checkout, so a worktree finds the same file. Every window run
calls `wait-for-pc.ps1` first; it waits, 20 s between looks and 30 minutes at
most (`-Minutes`), until the file says allowed and no `CabinetOS.exe` runs,
and exits 1 otherwise; `-Status` prints the state. The laptop and the VM
need no consent.

`run-livecheck.ps1` first shows the countdown window of `countdown.ps1`
(5 seconds, a sound, always on top: "The live check takes the keyboard and
mouse in 5"; Esc or its button cancels the run, `-NoCountdown` skips it for
a run that starts while nobody is there), the creator's rule of 2026-10-01
so nobody is caught typing; then runs `livecheck.ps1 -Strict`, keeps the output in
`_io\live-check\run-<time>.txt` next to the repository (`-Io <folder>`
names another folder, for a git worktree elsewhere), and at the end writes
`DONE.md` there and opens it in Notepad: the sign, on a PC where someone is
waiting, that the keyboard and mouse are free again. The script types into a
name box only after the window's "rename box shown" line; into the address
box and the palette only after their command's "command executed" line,
into the find box after "find opened", Quick Open after "quick open shown",
and the terminal after "a page has the keyboard"; and it presses the next
key after a change of folder only once the pane's "listing shown" line is
there. Each such wait is bounded and never shorter than the fixed sleep the
step had before, so a fast machine keeps its rhythm and a slower one waits.
It reads the
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
`%TEMP%\cabinetos-bench` (`ui\livecheck\bench-folders.ps1` makes the same
folders where there is no Rust); it stops when the folder is missing.
`livecheck.ps1` leaves one file in the Recycle
Bin, `cabinetos-live-check-delete-me.txt` (the Delete check), and one of
its own files on Windows' clipboard (section 18).

What a run keeps out of the real application data, all under its own folder:
the configuration, logs, themes, the undo journal, WebView2's data, the
plugins and their data (`CABINETOS_PLUGINS_DIR`, `CABINETOS_PLUGINS_DATA_DIR`),
the marketplace's files (`CABINETOS_MARKETPLACE_DIR`) and a copy of the tools
folder with Markdown Preview and the test tool Quick Notes. Its marketplace is a
local index the script builds (`sdk\marketplace\build-index.ps1`, with
`-Extensions` when `sdk\extensions\agent\plugin\plugin.wasm` is built), so the
marketplace never reads the network. Without that file the Agent steps say
"WAITING" and do not run; `sdk\extensions\build-extensions.ps1` makes it (a copy
of the committed `sdk\fixtures\plugins\agent\plugin.wasm` does too).

The keys of the cursor block (the arrows, Home, End, Page Up and Down, Insert,
Delete) are sent as extended keys, as a real keyboard's are. Sent plain they
are the numeric keypad's, and with Num Lock on Windows takes Shift away from
Shift+Down: the window saw a bare Down.

```text
# PowerShell: the scripts use Windows' SendInput and UI Automation
powershell -ExecutionPolicy Bypass -File <repo>\ui\livecheck\livecheck.ps1 [-Strict]
powershell -ExecutionPolicy Bypass -File <repo>\ui\livecheck\livecheck2.ps1
```

With `-Strict`, `livecheck.ps1` exits 1 when the scroll goal was not met
(with `-Panel`, when the panel goal was not met); it is off by default, because
the number depends on the machine being quiet.

The record of the first runs is [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md).

### Environment variables

| Variable | Effect |
|---|---|
| `CABINETOS_CORE_EXE` | The core to start, as a full path (first in the launcher's order, below) |
| `CABINETOS_LOG_DIR` | Where `ui.<date>.jsonl` and crash traces go; the core it starts inherits it |
| `CABINETOS_LOG` | The level filter, in the core's syntax: `debug`, or `info,cabinetos_ui::pipe=trace` |
| `CABINETOS_LOG_HEAVY` | `1` or `0`: heavy logging on or off, whatever `logging.heavy` says ("Heavy logging"). The window reads it, and so does the core it starts, which inherits it |
| `CABINETOS_CONFIG` | Not read by the UI; the core it starts inherits it and uses that `cabinetos.json` |
| `CABINETOS_THEMES_DIR` | Not read by the UI; the core it starts inherits it and reads the themes there ([themes.md](themes.md)) |
| `CABINETOS_UNDO_DIR` | Not read by the UI; the core it starts inherits it and writes the undo journal of every job there (by default `%LOCALAPPDATA%\CabinetOS\undo`). The tests and the live checks set it to their scratch folder, so a test job never writes the real journal |
| `CABINETOS_PLUGINS_DIR`, `CABINETOS_MARKETPLACE_DIR` | Not read by the UI, except the plugins folder for the empty plugin list's hint; the core it starts inherits them and installs there ([marketplace.md](marketplace.md), "Folders"). Set both to a scratch folder to try installs without touching `%LOCALAPPDATA%\CabinetOS` |
| `CABINETOS_WEBVIEW2_DIR` | Where WebView2 keeps its user data (its cache and storage) for the terminal and the tool pages, one subfolder per host; by default `%LOCALAPPDATA%\CabinetOS\WebView2`. The two-window test and the live checks set it to their scratch folder, so they never write into the real one |
| `CABINETOS_UI_PARKED_LISTING_MS` | For the tests: how long a pane keeps the listing of the tab that went behind last, in milliseconds, instead of 30 s ("Tabs") |
| `CABINETOS_UI_FRAMESTATS=1` | Logs one `frame stats` line per second: frames drawn, the longest gap between two, the gaps over 20 and 33 ms, the frames whose UI-thread work passed 16.7 ms, and the milliseconds of each timed part, with the garbage collector's pauses and collections in the second ("Scrolling"). A `slow frame` line for each frame of 33 ms or more, with the collector's pause in it. The snapshot aid's `scroll:` step adds a `scroll run` line with the run's whole frame table and the machine's CPU load |
| `CABINETOS_UI_SNAPSHOT=<folder>` | Development aid: once the first folders are shown, runs the steps of `CABINETOS_UI_SNAPSHOT_STEPS` and renders the window to PNG files in that folder. It draws the window's own content, so it works when the screen is off or locked; Mica is not part of that content. An open dialog (the popup layer) is rendered on its own and laid over the image, without WinUI's dimming of the window under it. WebView2 pages (the terminal) draw outside that content: each one on screen is captured by WebView2 (`CapturePreviewAsync`) and laid over its place, which also works on a locked screen. The capture has no transparency, so the terminal's area shows `#202020` instead of the panel's colour. The image is laid over a stand-in for Mica, so it is opaque: `#202020` (`#F3F3F3` in light mode) with the theme's Mica tint over it. |
| `CABINETOS_UI_SNAPSHOT_STEPS` | The steps, separated by `;` (default `shot:window`): `cmd:<command> [json]` runs a command through the router and waits for it; `cmd-nowait:<command>` runs one that waits for the user (a dialog, a rename); `path:<folder>` goes there in the active pane; `pane:0` or `pane:1` makes a pane active; `select:<name>` selects a row; `selectall`; `menu:<name>` opens the context menu on a row, selecting it first as a right-click does (`menu:*` on the empty space, `menu:` on the focused row), as the keyboard opens it, under the row; `menu-at:<name>|<x>,<y>` opens it as a right-click at that point does, in the window's content DIPs (`menu-at:alpha.txt|300,200`); `shellmenu:<name>` does the same as Shift+right-click (Windows' own menu with `contextMenu.shellMenu` on) and waits 1.5 s for the core, and `shellmenu-at:<name>|<x>,<y>` does it at a point; `menu-click:<title>` runs an entry of the open context menu, and `shellmenu-click:<text>` an item of Windows' menu (in a submenu too), as a click does; `menu-edit-key:<keys>` presses a key in the menu's edit mode as the window passes a real one (`alt+down`, `delete`, `insert`, `ctrl+s`, `escape`), and `menu-edit-drag:<title>|<title>` drops the first row on the second through the drag's own steps (not the pointer's events); `rename:<text>` types into the rename box and presses Enter; `dismiss` closes a dialog; `type:<text>` types into the palette, or into the prompt in its frame when one is shown; `accept` presses Enter in that prompt; `drive:<letter>` presses a drive's letter in the open drive list; `quick:<text>` types letters into the active pane's quick search; `search:<text>` types into the Search view's field (the pane shows the hits); `find:<text>`, `find-key:`, `quick-open:<text>`, `quick-open-key:` and `shell:<label>` drive and log the shell of Phase 16 ("The shell"); `open:<name>` presses Enter on a row (a file may open in a Tool Extension); `terminal:<text>` types into the shown shell (`{enter}` is Enter); `crash:terminal` or `crash:tool:<id>` ends that page's browser process; `dock:<pixels>` drags the dock's splitter to that size and saves it, as a drag does; `mode:light`, `mode:dark` or `mode:windows` makes the window take Windows as set to that mode (a `system` theme follows) without changing the PC's setting; `size:<width>x<height>` sizes the window's content in device-independent pixels; `fit:<pixels>` makes the window as high as gives the active pane's list that height; `theme:<id>` sets `ui.theme` as the picker does and waits until the theme is applied; `pick:<index>` puts the open theme picker's highlight on that row, as the pointer or a key moves it (its preview follows); `layout:<label>` writes "layout measured" into the log: each pane's list height, its whole rows, their height and the texts cut short, the function keys' widths, every corner radius over 3 px outside the overlays, the sizes the metrics set, and the keymap's fingerprint ("Metrics and chrome"); `click:<name>` presses the first shown button or menu item (of an open menu too) with that name as UI Automation reports it, the way assistive technology may press it: the keyboard moves to it when the button takes the keyboard (the window's chrome buttons refuse it, as under a real click), then its automation peer invokes it (`click:Installed` shows the marketplace's Installed tab, `click:Skip` answers a conflict, `click:Folders in between` goes to the folder a pane's "…" crumb stands for, `click:Menu` opens the top row's menu); `focus:<label>` writes where the keyboard is into the log ("keyboard focus", with the label, the element's type, accessible name and `x:Name`, and the overlays that are open as `overlays`: their names, comma-separated, empty for none; "One overlay at a time" in [keybindings.md](keybindings.md)); `key:<keys>` presses keys the way a real press arrives: key messages to the window's input window, which WinUI routes as it routes a real key (the window's `PreviewKeyDown`, the focused control, Tab's move between controls, a dialog's buttons), with the modifiers down in the UI thread's key state while they are handled, so the window need not be in front and no key reaches another program (`key:tab`, `key:shift+delete`, `key:ctrl+k ctrl+t` for a chord's two halves); while a web page (a tool's, the terminal) has the keyboard, the key goes into the page itself, through DevTools' `Input.dispatchKeyEvent`, because the window's input window drops a key as the page's, and the page's script passes it back as it does a real one (a key that closes the page, Ctrl+W on a tool's tab, does not hold the step up); `tooltip:<name>` opens the tooltip of the first element with that accessible name, shown or not, as the end of a hover delay would, and logs whether it stayed open; `preview:rename` proposes the active folder's files as a preview to the core and shows it (`preview:make` makes one and does not show it), `preview-key:enter` and `preview-key:escape` press its two keys, `plugin-event:<name>|<payload>` is a plugin's event (`$PREVIEW` stands for the made preview), `drop:<pane>` drops the cursor row on that pane's tool page, and `fake-command:x` adds a plugin command with an `input` (`demo.ask`) to the command list; `tab:new`, `tab:close`, `tab:next`, `tab:previous`, `tab:lock`, `tab:folder`, `tab:move` or `tab:select <index>` runs that tab command as its key would; `tabs:<label>` writes each pane's row into the log ("tabs shown": the titles, the front one marked with `*`, and the row's height); `market:<label>` writes the marketplace's cards into the log ("marketplace cards": how many are made, the set's `total`, whether it is `complete`, its `slices`, and the items' `ids` in the grid's order, which the keyboard follows; "The marketplace"); `scroll:<pages>` presses PageDown in the active pane 30 times a second, as a held key repeats, and `scroll:<pages>/<n>` once every n frames (with `CABINETOS_UI_FRAMESTATS=1` each writes its frame table, "Scrolling"); `rail:<id>` presses a rail button, `rail-move:<id>|<1 or -1>` moves it, `divider:<pixels>` drags the sidebar's divider to that width and lets go, `tree:<path>` opens that folder in the Explorer's tree, `rail-state:<label>` logs what the rail, the sidebar and the tree show, and `tree-state:<label>|<folder>` logs what the tree shows of one folder (whether it has a row, whether the row is a hidden folder's and how opaque its drawn name is, the names of the rows right under it, and the folder the tree marked) ("The activity rail and the sidebar"); `columns:<label>` logs each pane's four column widths as laid out, the width they share, the first row's widths and how far the grips are from the dividers ("columns shown"), `column-drag:<divider>|<pixels>` drags a grip of the active pane (1 Name|Modified, 2 Modified|Type, 3 Type|Size) by that many pixels through the grip's own drag steps and lets go, and `column-fit:<column>` fits as a double-click on that heading does (`name`, `modified`, `type` or `size`); the last two do not wait, so an `until:config` right after them sees the save ("Column widths"); `column-view:<label>` logs what the active pane shows ("column view shown": the mode, the columns' folders and cursors, the keyboard's column, its rows, the rows each column has on screen and the listings), `column-open:<name>` opens that row of the keyboard's column as Enter does, `column-key:<key>` presses `left`, `right`, `up`, `down`, `home`, `end` or `backspace` in the active pane, and `column-click:<depth>|<name>` clicks that row of that column, 1 being the first ("The column view"); `until:running`, `until:conflict`, `until:terminal`, `until:search`, `until:tool`, `until:tree`, `until:menu`, `until:menu-closed`, `until:config` or `until:config-error` waits for a job, a shell, an answer, a tool page (a sidebar page too), the folder tree to mark the active pane's folder, the right-click menu to be on screen (a newer one no longer waiting for the one before it to close) or to be gone, the next reading of the configuration (after an edit of the file) or the core's next report of an error in the file; `wait:<ms>`; `shot:<name>` writes `<name>.png`, open dialogs and menus included. Example: `pane:0;select:report.txt;cmd:file.copyToOtherPane;until:conflict;shot:conflict`. A step's text cannot contain `;`, since that ends the step. One quirk: a check box always shows a dash there, checked or not (the bitmap draws the first frame of WinUI's animated check mark). |

### Logs and crashes

The UI writes `%LOCALAPPDATA%\CabinetOS\logs\ui.<UTC date>.jsonl` in the
format of [diagnostics.md](diagnostics.md), with `boundary: "frontend"`, on
a background thread with a bounded, lock-free queue; it keeps 14 daily
files, as the core does. Targets are `cabinetos_ui::<area>`: `app`,
`session`, `launcher`, `pipe`, `commands`, `keys`, `pane`, `palette`,
`jobs`, `settings`, `shell`, `frames`, `xaml`, `icons`, `terminal`,
`webview`, `theme`, `market`, `snapshot`, `diag`. The UI thread is named `ui`.
The lines only heavy mode writes have targets `heavy::<area>` and are not
in this file ("Heavy logging").

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

### Heavy logging

Heavy mode records every operation, even at the cost of speed, for the times
when a problem has to be found ([diagnostics.md](diagnostics.md), "Heavy
mode"; the Article 12 exception it needs is
[ADR 0013](decisions/0013-heavy-logging-may-wait.md)). The window follows
`logging.heavy` as the core does, and adds what only the window sees.

**The switch.** The window reads `logging.heavy` with the rest of the
configuration at start and again on every `config_changed`, and turns its
own heavy log on or off with it. "Diagnostics: Toggle Heavy Logging"
(`diagnostics.toggleHeavy`, no default key) flips nothing itself: it writes
the opposite value with `set_value logging.heavy`, and both processes
follow the `config_changed` that comes back. The environment variable
`CABINETOS_LOG_HEAVY` wins over the setting in both processes; when it is
set, the command only says so in the notice line, and the pill cannot turn
heavy mode off. The other two commands are "Diagnostics: Open Log Folder"
(`diagnostics.openLogFolder`: the core opens the folder with `open_path`,
as it opens any file, so the window makes no shell call of its own) and
"Diagnostics: Save Log Bundle" (`diagnostics.saveBundle`: asks the core for
a zip of the last 10 minutes of every log with `save_log_bundle`, tells its
name in the notice line, and opens the folder; a core without the request
answers `unknown_request`, and the notice says the command needs a newer
core).

**The indicator.** While heavy mode is on, the status bar shows a small
accent-coloured `HEAVY LOG` pill in front of the transfer pill. A click on it
runs the toggle. When the window has dropped lines because its heavy queue
was full, the pill says how many: `HEAVY LOG, 1,204 lines lost` (the count
is read once a second and starts again at each switch on). A window that
starts with heavy mode on, and a switch on, show "Heavy logging is on; the
log folder grows to 2 GB." in the notice line. The pill's states come from
`HeavyLogSwitch` and `HeavyPillState` in `CabinetOS.Core`, which the tests
drive from a `logging.heavy` value.

**The file and the queue.** `heavy-ui.<date>.jsonl` (and `.1.`, `.2.`, … for
the next 256 MiB), next to `ui.<date>.jsonl` and the core's files, with the
core's cap of 2 GiB for all `heavy-*.jsonl` of all processes and the same
rule that the file a process writes is never deleted. `LogWriter` keeps a
second queue for it, counted in bytes: 64 MiB. Above that, a thread that logs
waits for the writer; the UI thread (`Program.Main` marks it) and the
reader of the pipe (`CoreClient.Dispatch`) never do, and drop their lines
instead once the queue is 8 MiB over, counting them. The next batch says
`heavy log dropped lines of threads that never wait`, with the count. The
switch itself, going on and off, is one line in both files.

**What the window records.** Lines with the target `heavy::<area>`, in the
heavy file only (the table with every field is in
[diagnostics.md](diagnostics.md)):

| What | Area | Notes |
|---|---|---|
| Every request and reply of the pipe | `pipe` | The whole JSON, secrets masked (`LogMask`, the core's rules), at most 64 KB. Under the request's ID and its action's trace |
| Every key that reaches the window | `keys` | By name and modifiers, with the type and name of the element that has the keyboard. A key that types a character into a text box (a name, the address, the palette's field, a search) is `text input` with no key and no character. Ctrl or Alt alone with a letter is a shortcut and is named; Ctrl and Alt together are AltGr and count as typing |
| Every command | `commands` | `command run` (the registry's source, `target`, `trigger`, the arguments masked and cut at 4 KB) and `command done` (outcome, milliseconds) |
| Every change of focus | `focus` | From and to, as element type and name, and where Windows sends the keys (`WindowsPlatform.KeyboardFocus`, the same probe as the "keyboard owner" lines) |
| The frame table, every second, and each frame of 33 ms or more (`slow frame`) | `frames` | `FrameMonitor` runs while heavy mode is on, as `CABINETOS_UI_FRAMESTATS=1` runs it (which the variable still does by itself). It keeps the window drawing while nothing changes: heavy mode's cost |
| Every message to and from a web page | `pages` | The terminal's and the tools' pages: the message's `type` and its size, never the content |

A key press that becomes a command shares one trace with it: the press gets
a ULID before anything handles it, and `CommandRouter.ExecuteAsync` takes
it as the run's ULID (`traceId`), so `cabinetos-cli log trace <id>` prints the
key, the command, its requests and what the core did for them in time
order. A key that becomes no command is a chain of one line.

**Crashes and bundles.** A crash of the window while heavy mode is on writes
the crash trace as always, then `crash-<stamp>.zip` next to it: the last 10
minutes of every log in the folder, the recent crash traces and a
`bundle.json` ([diagnostics.md](diagnostics.md), "Bundles"). The window keeps
what the bundle says about it (the protocol version at `welcome`, the
configuration of the last `config` reply, secrets masked) in `Diag`. At the
next start the window looks for a `crash-*.zip` newer than its last start
(`CrashNotice`, off the UI thread; the time of each start is in
`ui.last-start` in the log folder) and, if there is one, shows an "Open crash
folder" button in the status bar, in front of the pill, until it is clicked
(the button runs `diagnostics.openCrashFolder`, a command of the window's own
that the palette does not list, and the core opens the folder). The same
file says whether the last run closed: the window writes the end into it
when it exits, and a start that finds a run without that end, and no such
process running, logs one WARN line, `previous run ended without closing`
(a native failure of WinUI leaves no crash trace; [diagnostics.md](diagnostics.md),
"A run that ended without closing"). Nothing is shown on screen for it.

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

The first core is started as soon as the window's process starts, on a
background thread, while WinUI builds the window (about a second on the
development PC). The protocol's JSON tables are built on another
background thread at the same moment. So when the window is built, the
core has its pipe open and the first answer is not held up: the start to
both folders shown went from about 1.33 s to about 1.1 s (medians of five
release runs) in the speed review of 2026-10-01
([log/2026-10-01/speed-review.md](log/2026-10-01/speed-review.md)).
The window says `hello` once it is built, as before.

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
  old one until it arrives. The core has often drawn them already: it draws
  `folder` and `generic` when it starts, and the kinds of a listing right
  after that listing's reply ([ipc.md](ipc.md), "Type names and icons"), so
  a request mostly finds the PNG made.
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

The core sorts. The user sets the columns' widths with the mouse
("Column widths"). `panes.showHidden`
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
  completes nothing runs nothing and is reported. Esc during the wait only
  ends it; Ctrl+Shift+P ends it and opens the palette.
- Contexts: `filesView` (a file pane has the focus), `paletteOpen`,
  `textInput` (a text box has the focus). A binding with a `when` that holds
  wins over one without.
- A text box keeps the keys that type or edit: characters, Space, Enter,
  Esc, Backspace, Delete, the arrows, Tab, Ctrl+A, Ctrl+C, Ctrl+V and the
  other editing keys (the full list: [keybindings.md](keybindings.md),
  "Contexts"). A key that types nothing runs its binding there: Ctrl+B,
  Ctrl+Tab, Alt+Left, the function keys (`TextInputKeys`). So typing is
  never taken over by a shortcut, and a shortcut still works while a box
  has the keyboard. The Immutable System Tier works everywhere.
- The active pane's find box and address box are `filesView` as well as
  `textInput`, so F5 or Ctrl+T there acts on the pane. The rename box sits
  inside the pane but is text input only: F5 does not start a copy while a
  name is being typed.
- A key held down runs its command once: Windows' repeats run nothing,
  except for the commands meant to repeat (Ctrl+Tab, Ctrl+Shift+Tab,
  Insert, Alt+Left, Alt+Right, Alt+Up; keybindings.md, "Keys held down").
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
| Ctrl+Shift+F10 | `menu.showShell`: Windows' own menu of the focused row, with `contextMenu.shellMenu` on; else the context menu | keymap |
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
| `palette.show`, `overlay.close` | Open and close the palette; Esc closes, in order, the palette, Quick Open, the prompt, the theme picker, the plugin review, the plugin list, the marketplace (its detail column first), the context menu, Windows' menu and the top row's dropdowns, a rename, a quick search, a breadcrumb row's text box, the rail layout's or the Search view's focus, the find widget, the search results, and last folds the transfer flyout into the pill. Opening an overlay closes the others, so at most one of the palette, Quick Open, the prompt, the theme picker and the plugin list is open (the order matters for the review over the list and for the rest; [keybindings.md](keybindings.md), "One overlay at a time") |
| `search.focus` | Opens the active pane's find widget ("Find in pane") |
| `quickOpen.show`, `menu.show`, `settings.open` | Quick Open, the top row's menu, and `cabinetos.json` in the editor ("The shell") |
| `keys.open` | Opens the palette: it lists every command with its keys and edits them |
| `view.toggleDualPane`, `view.toggleSidebar`, `view.focusOtherPane` | As named; the first two are saved in `cabinetos.json` |
| `go.toPath` | With `{"path": …}` goes there; without, turns the active pane's breadcrumb row into a text box (`{"pane": 0 or 1}` names the pane) |
| `help.about` | The window's command (target `ui`): its handler shows About CabinetOS ("About", below). An older core that ran it itself answered `command_result`; the window shows the same view for that |
| `file.copyToOtherPane`, `file.moveToOtherPane`, `file.newFolder` | Run in the window: a job, or a folder (see below) |
| `view.toggleTerminal` | Shows the terminal, gives the keyboard back to the pane, or hides it ("The terminal") |
| `marketplace.browse`, `preferences.selectColorTheme` | Open the marketplace and the theme picker ("The marketplace", "Themes") |
| `go.root` to `terminal.insertSelectedPaths` | Total Commander's small commands, 31 of them, run in the window ("Total Commander's keys") |
| `workspace.switch` | The workspace pill's dropdown ("The shell"); its "Open folder as workspace…" says that workspaces arrive in a later version |
| `terminal.runTask` | "arrives in a later version" in the status bar |

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
- **Its keys are its own.** Tab and the arrows move between its buttons,
  and Enter and Space press the focused one. WinUI routes a dialog's keys
  through the window's root on their way in, so the window's key handler
  sees them first; it lets a key whose focus is in the dialog go on, and
  runs no binding for it. Until 2026-10-01 it held them all: only Esc
  worked in a dialog (the keys audit, [log/2026-10-01](log/2026-10-01/README.md)).
- The log says "dialog shown" and "dialog closed" with the title and how
  it was closed, and "key held by a dialog" for a key that reached the
  window under it. A key pressed in the dialog is the dialog's own and is
  not logged; the live check proves that nothing ran by finding no
  "command executed" between "dialog shown" and "dialog closed".
  `KeysEndToEndTests` presses Tab and Enter in "Delete permanently?" with
  the `key:` step and finds the file gone.

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
when the release says so; without that file, "Development build". The
"Update" row (Phase 17) says where the updater is: "0.2.0 is ready:
restart to run it" with an accent dot while a version waits, "Up to date
on the stable channel, checked Today 09:11", the reason a development
build does not update itself, and so on ("Updates"). Two
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

A right-click on a row opens that row's menu with its top-left corner at
the pointer, as Explorer does, flipped up or to the left when it would not
fit; a right-click on the pane's empty space opens the folder's menu the
same way. Shift+F10 and the Menu key open the focused row's menu under the
row, left-aligned with the row's name. A right-click inside the
selection keeps the selection, as in Explorer; outside it, it selects that
row first. The menu is WinUI's `CommandBarFlyout`, as Windows 11's Explorer
shows it (Article 3): a row of icons at the top (the quick actions, each
with its title and keys as a tooltip), then the list, each row with its
command's keys on the right. The window places it ("Where it opens", below);
the arrow keys move, Enter runs, Esc and a click outside close it. An entry runs after the menu has closed and the pane has the
keyboard back, so a command that takes the keyboard (Rename's text box)
keeps it.

**Where it opens.** The menu's top-left corner is at the point: the
pointer's, or for the keyboard the focused row's bottom-left, at the left
edge of the name column (`FilePane.RowAnchor`). It is shown with
`Placement = BottomEdgeAlignedLeft` at that corner. When the menu would not
fit below the point it hangs above it, its bottom at the point; when it
would not fit on the right it hangs to the left, its right edge at the
point; a menu that fits neither way sits against the far edge, 4 px in.
A focused row that is scrolled out of view gives the keyboard's menu a
point near the top of the pane, and a point outside the window counts as
the edge it is past: WinUI ended the whole process (a fault in
CoreMessagingXP.dll, with no crash trace) when a menu was shown outside the
window, which the speed review of 2026-10-01 found with Shift+F10 on a row
the list had made ahead of the view.
`MenuPlacement` (Core) holds this rule, in the window's content coordinates.
The window applies it and WinUI does not, because WinUI measures a
`CommandBarFlyout` against the screen, not the window: measured on
2026-09-30, a menu asked for near the window's bottom kept its list below
the point, past the window's edge, and one near the right edge was not
moved at all. The size the rule needs is the one WinUI drew the last time
for that menu shape; the first time it is an estimate from the entries (an
icon row is 60.7 px high, a row of the list 32, a divider 9.3, the width 309
with five icons), good to a pixel in the default theme. The edit mode that
opens from a point, not from an open menu, follows the same rule, so it
appears where the menu would have; chosen from an open menu it takes the
menu's own place, as before. Windows' own menu is a `MenuFlyout` of 20 to
40 rows, never short enough for a window: it has the same top-left corner,
and WinUI keeps it on the screen (it moves a menu up only when the screen is
too low for it).

**What is in it** comes from `contextMenu` in `cabinetos.json`
([config.md](config.md), "The context menu"). It has four targets:
`background` (the empty space), `file` (one file), `folder` (one folder)
and `multiSelect` (a row inside a selection of several rows). Each has
`quickActions`, the icon row, and `items`, the list. The defaults are the
menus of Phase 5:

- a row: the icons Cut, Copy, Paste, Rename and Delete; the list Open
  (Enter), Open in other pane (Ctrl+Enter, folders only), Copy to other
  pane (F5) and Open in Terminal (a new shell in the row's folder; no keys
  shown, because Ctrl+` toggles the terminal instead);
- the empty space: no icons; the list Paste, New folder and "Pin this
  folder to the sidebar" (hidden when the folder is pinned already).

After the target's list the window adds, in this order: the plugins'
commands whose `when` is `filesView`, under "FROM PLUGINS" (rows only; a
plugin command the file lists already stays where the file put it), each
with the plugin's name after its title and the plugin's coloured square as
its icon; Properties (Alt+Enter; on the empty space, the folder's
Properties); and "Edit Menu…" (`menu.edit`), which turns the menu into its
edit mode ("Editing the menu", below). Ctrl+, still opens `cabinetos.json`.
A list item may name `extensions`: the item shows only for
files with one of those extensions. With several rows selected, every
selected row must be a file with one of them.

The window builds the menu from the configuration it holds in memory, at
every opening: a saved edit of the file shows at the next right-click, and
the window reads no file (brief §1). A command ID that no command has (a
plugin that is off, a typo) is left out, with one warning in the log per ID
("context menu entry left out: no command has this ID"); after the next change of the file it is
reported again. Today's entries keep the menu's own titles and icons; any
other command shows its palette title and no icon. Paste is off while the
in-app clipboard is empty, Rename while several rows are selected, Open in
other pane for a file, and Copy to other pane in single-pane mode.

A plugin's command gets `{"path": …, "paths": […]}`: the right-clicked row
and the selection (the folder, on the empty space), since plugins cannot
read the selection themselves yet. The paths are read when the entry runs,
not when the menu opens, so a menu over 100,000 selected rows lists none of
them.

**Programs.** Each entry of `programs` in `cabinetos.json` is a command
`program.<name>`, in the palette's Programs group, that a menu lists like
any other: `{"command": "program.code"}`. The core starts the program with
the paths it needs ([config.md](config.md), "Programs"); the status bar then
says "<title>: started.", and a refusal shows as a notice. Before the
command goes to the core, the window sends its state, so `{path}` and
`{selection}` are the rows of this moment.

**Speed.** Building and drawing a `CommandBarFlyout` the first time takes
about 30 ms and then a 60 ms frame (measured 2026-09-30). So the window
keeps one flyout per menu shape (the same entries, titles, icons and keys),
up to six shapes. The same shape again only has its entries' enabled state
set: about 6 ms, with no frame over 33 ms. The first opening of a shape in
a window's run still costs one long frame, about 45 to 60 ms. So the
common shapes (a file, a folder, the pane's space, several rows) are built
0.75 s after the first folders are shown, one a dispatcher turn at low
priority ("context menu prepared" in the log, with its time), and the
first right-click only shows them: in the speed review of 2026-10-01 the
first menu's synchronous part went from 36 to 24 ms and its longest frame
gap from 96 to 75 ms. What stays is WinUI's own work at a flyout's first
showing (one frame of about 65 to 70 ms of UI-thread work), which cannot
be done ahead without showing the menu. "context menu shown" says with
`built` whether the opening had to build its shape. A menu asked
for while the previous one is still closing waits for it to be gone,
because WinUI ignores a flyout that is shown again while it is closing,
and then one turn of the window's dispatcher more: shown again from
inside its own `Closed`, at the place it had (the same menu on the same
row, as after Esc and a quick right-click), the flyout never came back,
and every later menu of that shape waited for it. The log says "context
menu opened" when WinUI has the menu on screen, not only asked for.

**Logs** (Article 12). "context menu shown" has the target, the quick
actions, the items, `keyboard`, `x` and `y` (the point asked for, in the
window's content DIPs), `build_ms` and `built` (whether this opening had
to build its shape's flyout); from the keyboard it also has
`row_left`, `row_top` and `row_bottom`, the focused row's name column and its
top and bottom in the same coordinates. "context menu placed" follows it a
few frames later, once WinUI has laid the menu out: `left`, `top`, `width`
and `height` of the icon row's popup and the list's popup joined, in the
same coordinates, so a menu's place reads against the point it was asked for
(`left` and `top` are the corner, or `left` + `width` and `top` + `height`
are the point when the menu flipped). "windows menu shown" has `x` and `y`
too, and "windows menu placed" has the four numbers of the menu's popup.

#### Windows' own menu

With `contextMenu.shellMenu: true`, Shift+right-click and `menu.showShell`
(Ctrl+Shift+F10) open Windows' own menu: the menu Explorer shows, with Open
with, Send to, and what other programs add. With it off (the default), both
open CabinetOS's menu. The core builds the menu (`shell_menu`,
[ipc.md](ipc.md), "Windows' context menu") on a thread of its own, for the
right-clicked file, for the selection it belongs to (at most 1,000 files,
all in one folder), or for the folder on the empty space. The window waits
for nothing: the menu shows when the core answers. When Windows takes
longer than 3 s to build it, a notice says so.

The window draws the items as a `MenuFlyout`, with submenus one level deep
(Send to, Open with). Items a program draws itself, items without text and
greyed items are left out. A click runs the item in the core
(`shell_menu_invoke`) once the menu has closed. The core keeps each menu for
30 s; an item chosen later gets `no_such_menu`. Cut and Copy put the files on
Windows' clipboard, as Explorer does; the window's own Ctrl+V does not paste
files from Windows' clipboard yet ("The clipboard"). A dialog that an item
opens (Properties, Open with) belongs to the core and has no owner window,
so it can open behind CabinetOS.

#### Editing the menu

"Edit Menu…" turns the open menu into its edit mode, for the target the
menu was opened for: the file, folder, empty space or selection menu.
The edit mode appears where the menu was, as wide (at least 280 px, since
each row gains a handle and an X), under a line such as "Editing: File
menu". It edits the target's `items`, the list:

- each row as the menu shows it, with a drag handle at the left and a red
  X at the right. A command no command has shows its ID, greyed, so it can
  be taken out; a row with `extensions` shows them on the right;
- drag a row to move it (the rows it passes make room); Alt+Up and
  Alt+Down move the focused row;
- the X or Delete removes a row;
- "Add Command…" or Insert opens the palette's prompt with the commands
  that may sit in a file pane's menu: the registry's with `when`
  `filesView`, the programs (`program.<name>`) and the plugins' commands,
  without the ones the list has and without Properties and "Edit Menu…".
  Typing narrows them; Enter adds the chosen one after the focused row, Esc
  goes back to the edit mode;
- "Add separator" adds a divider after the focused row.

The icon row, the `extensions` filters and `programs` are edited in the
file in this version: the edit mode shows the icon row as it is, with the
line "The icon row is edited in the file.", keeps a row's `extensions`
through every move, and has an "Open the file" link. The link leaves the
edit mode without saving and opens `cabinetos.json` (Ctrl+,), because the
file and the edit mode must not both change the list. The plugins' group,
Properties and "Edit Menu…" stay after the list, greyed: the window adds
them to every menu.

**Done** (or Ctrl+S) sends the list to the core as `set_value
contextMenu.<target>.items` (`file`, `folder`, `background` or
`multiSelect`), in the shape of the defaults: `{"command": …}`, with its
`extensions` when it has them, or `{"separator": true}`. The window writes
no file (brief §1). The core writes `cabinetos.json` and announces
`config_changed`, the status bar says "File menu saved.", and the next
right-click shows the new menu. When nothing changed, Done only closes.
When the core refuses (the file has an error to fix first, or the core
cannot write), the status bar says why and the edit mode stays open, so
nothing is lost. **Cancel** and Esc leave without saving.

The edit mode stays until Done, Cancel or Esc: a click outside it does
nothing. It is part of the window, not a flyout, so the click can be
swallowed, the palette's prompt can open over it, and the snapshot aid can
draw it. While it is open it holds the keyboard as a dialog does: no key
binding of the window runs, so Delete can never reach `file.delete`. The
keys (Article 7): Up and Down move between rows, Alt+Up and Alt+Down move
the row, Delete removes, Insert adds, Tab reaches every button, Enter on a
button presses it, Ctrl+S saves, Esc cancels. `menu.edit` from the palette,
or a key bound to it, edits the focused row's menu where Shift+F10 would
open it, since no menu is open then.

The log says what happened: "menu edit shown" (the target, whether it took
the menu's place, `build_ms`), "menu edit step" (remove, move, drag, add,
separator; the row and the rows after it), "menu edit saved", "menu edit
refused" (warn level, with the core's code) and "menu edit closed"
(saved or not). The surface is built at the first "Edit Menu…"; entering
it over the 100,000 selected rows of the bench folder adds no frame over
33 ms.

**Checks.** `ContextMenuTests` (Core): the defaults (the same as the core's
schema), the enabled states, a folder's Open in other pane and terminal
folder, several rows, the empty space, an unknown ID and its divider, the
places of Properties and "Edit Menu…", the extensions filter for one and for
several rows, plugins and their paths, a program's title, and the config
read per target. `ContextMenuEndToEndTests` (with `CABINETOS_UI_E2E=1`):
with a fresh configuration, the file menu, the empty space's menu, and
Shift+right-click opening the same menu while `shellMenu` is off. Then the
test edits the file while the window runs (`shellMenu` on, a program in the
file menu for `.md` files only, an unknown ID): the `.md` file's menu shows
the program and the `.txt` file's does not; a click on it gives the program
the `.md` file's path, and the same command from the palette the cursor
row's; `program.nosuch` is refused (`unknown_program`); the warning comes
once, though the menu opened twice; and Windows' menu shows. A second test
opens the menu over 100,000 selected rows of the bench folder: no frame over
33 ms. `ContextMenuEditTests` (Core): the edit model's names and settings per
target, the rows as the menu shows them, a move that keeps a row's
`extensions`, removal and insertion and where the focus goes, the value
`set_value` gets (valid against the schema, and the core's defaults for an
unchanged list), what "Add Command…" offers, the keys, and what stays
around the list. `ContextMenuEndToEndTests` (with `CABINETOS_UI_E2E=1`) also
edits the file menu on a real window and core: an X, Insert with the
prompt, Alt+Up, Delete, two drags and Done, then the file and the next
menu; a save
refused while the file has an error (the mode stays open, the notice
shows); Esc; and `menu.edit` from the palette. A third test closes the
file menu and asks for it again on the same row at once, twice: each
comes on screen (WinUI's `Opened`, `context_menu_on_screen` in the shell
state) within 1 s. With the frame tests, it
enters the edit mode over 100,000 selected rows: no frame over 33 ms. The
live check's section 18 does the same with the real mouse and keys, and
runs Windows' Copy. `MenuPlacementTests` (Core) pin the placement rule: it
fits, it flips up, it flips left, both, it fits neither way, it is bigger
than the window. Another end-to-end test opens a pointer menu at (300, 200)
and a keyboard menu on a real window, then menus of a small window near its
bottom, its right edge and its corner (the snapshot aid's `menu-at`), and
compares "context menu placed" with the point asked for and with the row:
left and top within 2 px, and in the small window the whole menu inside the
window, flipped, its bottom or right edge at the point. The test of Windows'
menu checks that its left edge is at the row's and its top is not below it.
The live check's section 18 compares the menu's place with the point of its
first real right-click (frame-relative pixels over the DPI scale, within 4
px).

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
| `ui.tabs` | A tab is opened, closed, moved, locked or changes its folder (at most once a second), and when the window closes | At start: each pane opens its tabs; a folder that is gone falls back as `ui.lastPaths` does ("Tabs") |
| `ui.pinned` | "Pin this folder to the sidebar", and "Unpin from sidebar" on a pinned row | At start and on `config_changed` |
| `ui.dockSize.bottom`, `ui.dockSize.right` | A drag of the dock's splitter ends (once per drag) | At start and on `config_changed`, except during a drag ("The terminal") |
| `ui.compactOverlay` | The drawer is resized, half a second after the last resize ("Compact overlay") | When the drawer starts, and while it is on, on `config_changed` |

The sidebar always shows Desktop, Downloads, Documents and the profile
folder; `ui.pinned` holds the folders the user added, shown after them,
and only those can be unpinned. While a toggle's own write is on its way,
a configuration the core sends meanwhile does not flip the view back. A
core without `set_value` answers `unknown_request`: the window then keeps
its state in memory and logs one line.

### Compact overlay

The command `view.toggleCompactOverlay` ("Toggle Compact Overlay", category
View) makes the window a small drawer that stays on top of other windows.
The same command brings the window back. Its default key is Ctrl+Alt+Up, the
key the Files app uses; no other command has it. The mode is off until the
user asks for it (Article 4), it has a command and a key (Article 7), and
its size is in the file (Article 6).

What the drawer is:

- **An ordinary window that is always on top.** It is the same window with
  the overlapped presenter's `IsAlwaysOnTop` turned on. It is not WinUI's
  `CompactOverlay` presenter kind: that one keeps the aspect ratio of a video
  and has a caption of its own, which does not fit a file drawer.
- **One pane, no sidebar, no dock.** The left pane stays, with its tabs. The
  sidebar and the dock are hidden through the same code that the toggles
  use, without their saves. The right pane is hidden, not closed: its tool
  tabs stay open for the way back (turning Dual Pane off closes them). The
  rail, the top row's buttons for the second pane and the terminal, and the
  workspace pill are hidden too: they would do nothing, or they would run
  under the buttons on the right. The command center hides under 640 px as
  always.
- **Its size is `ui.compactOverlay`.** `width` and `height` are the window's
  outer size in device-independent pixels (DIPs), each from 240 to 4000
  ([config.md](config.md)). `null`, the default, gives 480 by 640. The size
  is raised to the window's minimum and cut to the work area of the screen
  (`CompactOverlayLayout.Decide`). The minimum width is 360 DIPs while the
  mode is on (600 in the full window). The minimum height stays 480, so a
  saved height below that is raised to it. The drawer opens where the
  window's top-left corner was, and is moved only as far as keeps it on the
  work area.
- **A resize is saved.** Half a second after the last resize in the mode, the
  window writes the size with `set_value ui.compactOverlay`. A resize that
  is still waiting is saved when the user leaves the mode. An edit of the
  file while the drawer is on resizes it at once. The next entry opens at
  the saved size.
- **Leaving puts everything back.** The window returns to the size and place
  it had, with its minimum width, its dual pane, sidebar and dock as they
  were at entry, and the active pane. A maximized window comes back
  maximized. The mode is not a setting: it writes none of `ui.dualPane`,
  `ui.sidebar` and `ui.dockSize`. If `ui.dualPane` or `ui.sidebar` is edited
  while the drawer is on, the edit is kept for the way back.
- **What would undo the drawer is refused.** Toggle Dual Pane, Toggle
  Sidebar, Show Explorer, Show Search, the terminal, Open in Other Pane,
  moving a tab to the other pane, the right pane's drive list (Alt+F2),
  Ctrl+Right and Quick Open's other pane say "The compact overlay has no dual pane: Ctrl+Alt+Up
  leaves it" in the status bar and do nothing. A proposal from a plugin needs
  the second pane: entering the mode is refused while one waits, and one that
  arrives in the drawer makes the window leave it first.
- **The status bar names the mode and the key.** "Compact overlay · Ctrl+Alt+Up
  leaves it" takes the place of the layout's name ("Terminal: bottom"), with
  the key as it is bound now (with no key, "leave it from the command
  palette"). To make room in a status bar 345 to 465 px wide, the encoding
  text and the palette's keycap are hidden, the gaps are narrower, and the
  selection text is cut to 90 px (and hidden under 420 px).

The key works while a text box has the keyboard too: AltGr with an arrow
types nothing there (keybindings.md, "Contexts"). Until 2026-10-01 it did
nothing in a box. Some Intel graphics drivers use Ctrl+Alt with an
arrow key to turn the screen. If that happens, turn the driver's hotkeys off
or rebind the command in the palette.

The window logs, with the target `cabinetos_ui::shell` (Article 12):

| Message | Fields |
|---|---|
| `compact overlay entered` | `width`, `height` (DIPs), `saved` (the size came from the file), `previous_width`, `previous_height` (DIPs), `previous_left`, `previous_top` (pixels), `dual`, `sidebar`, `dock` (as they were) |
| `compact overlay left` | `width`, `height` (DIPs, as restored), `left`, `top` (pixels), `dual`, `sidebar`, `dock` (as restored), `maximized` |
| `compact overlay size saved` | `width`, `height` |
| `compact overlay size not saved` | `error` (the core's refusal, or that it cannot write settings) |
| `compact overlay follows the configuration` | `width`, `height`: the file was edited while the drawer was on |

`CompactOverlayTests` checks what needs no window: the size it opens at, the
place, the JSON of the saved size, the settings reading it and the key's path
through the chord machine. `CompactOverlayEndToEndTests` (opt-in, with
`CABINETOS_UI_E2E=1`) runs a real window: it reads the window's topmost style
and rectangle from Windows while the drawer is on, reads the file for the
three layout keys during the mode, resizes the window through Windows and
reads the saved size, starts the drawer again at it, and edits the file to see
the drawer follow. The live check's compact overlay section presses
Ctrl+Alt+Up with real keys and reads the same style and rectangle.

## The command palette

Ctrl+Shift+P, the ⋮ button and the status-bar keycap open it (design view
B): a transparent scrim over the whole window, a 640 px panel with in-app
Acrylic, and the design's 160 ms entrance. What the user types goes to the
core as `search_commands` after 30 ms of quiet; the UI ranks nothing
itself. Up and Down move the highlight, Enter or a click runs the command
through the router and closes the palette, a click on the scrim or Esc
closes it. A row shows the command's first binding as keycaps, and "+N"
when it has more; its tooltip lists them all.

Tab keeps the keyboard in the palette while it is open, and so it does in
Quick Open, the prompts in the palette's frame and the theme picker; in
the plugin list and in the permissions review it goes around their own
controls. Until 2026-10-01
Tab left the theme picker and the plugin list with the keyboard on a
sidebar row, a rail button or the pane, while they stayed on screen: the
next Enter or Space acted under them (the keys audit,
[log/2026-10-01](log/2026-10-01/README.md)). The permissions review had
the same fault by reading. It got the plugin list's fix by reading too
(`TabFocusNavigation="Cycle"`), and no test opens the review yet: that
needs a plugin that asks for a capability.

One overlay at a time. Opening the palette closes Quick Open, a prompt, the
theme picker and the plugin list, and opening any of them closes the palette
and the others (`OverlayRule`, `MainWindow.CloseOtherOverlays`;
[keybindings.md](keybindings.md), "One overlay at a time"). So Ctrl+K Ctrl+T in
the palette shows the picker alone, and the first Esc closes it with nothing
left open. Until 2026-10-01 the picker opened over the palette, and the first
Esc closed the palette under it.

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

## The shell

Phase 16 gave the window the shell of the creator's redesign
(`SHELL_REDESIGN.md`, received 2026-09-30): one top row instead of the
title bar and the command bar, and in each pane a tab strip and a
breadcrumb row instead of the pane's header. The global address bar and
the global search field are gone: each pane has its own path, finding a
name in the folder on screen is the pane's find widget (Ctrl+F), and
finding a file anywhere in the workspace is Quick Open (Ctrl+P).
Constitution Articles 3, 4, 5 and 7. What the shell decides without a
window is in `CabinetOS.Core/Shell` and tested there (`ShellTests`); the
window's part is `MainWindow.Shell.cs`, `MainWindow.Find.cs` and
`MainWindow.QuickOpen.cs`.

### The top row

One row, `topRowHeight` high (40 px; 32 in Commander Compact, never lower
than the 32 px Windows draws its caption buttons at). It is filled with the
bar colour and has a 1 px line under it. The whole row is the window's
drag area, except over its controls: those rectangles are handed to
Windows as pass-through regions (`InputNonClientPointerSource`), so a
click on them reaches the control and a drag anywhere else moves the
window. Left to right:

- **The menu** (the hamburger, `topRowButtonSize`, 36 px): `menu.show`.
  Its dropdown lists New Tab, New Folder, Find in Pane, Go to Path…,
  Toggle Sidebar, the marketplace, Keyboard Shortcuts and Check for
  Updates, each with the title and the first key the registry has now
  (`ShellMenu`), so a rebinding shows at once. While an update waits for
  a restart, the button carries an accent dot, and Check for Updates
  becomes "Restart to Update (0.2.0)" with the same dot ("Updates").
- **The app icon**, 16 px.
- **The workspace pill** (`workspacePillHeight` 24 px, `workspacePillRadius`
  4 px, the accent at 18 %): a dot, the workspace's name ("Default" until
  workspaces exist) and, in the mono font at 10 px, the branch of the git
  repository that holds the active pane's folder (below). A click, or
  Ctrl+K Ctrl+W (`workspace.switch`), opens its dropdown: the workspace
  with its branch, a line, and "Open folder as workspace…", which says that
  workspaces arrive in a later version.
- **The command center**, centred in the window: clamp(200 px, 34 %,
  380 px) wide, `commandCenterHeight` 24 px, with a search glyph, the
  workspace's name and "Ctrl+P". A click opens Quick Open. It never lies
  over the clusters beside it: where the centred place would, it moves and
  narrows into the room between them, down to 120 px, and hides below that
  and whenever the window is narrower than 640 px (`TopRowLayout`). Ctrl+P
  works when it is hidden. The window's least width is 600 px, so the
  640 px rule can show.
- **The view buttons**: Dual/Single, Terminal, Marketplace, the command
  palette and Settings. The first four are in the accent colour while
  what they show is on. Settings runs `settings.open` (Ctrl+,): the core
  says where `cabinetos.json` is (`get_config`), and the file opens with
  the program F4 uses (`edit_path`: `files.editor`, the type's edit verb,
  else Notepad). Then a 1 px divider and the space Windows' caption
  buttons take (`AppWindow.TitleBar.RightInset`).

The dropdowns use the acrylic surface the context menu had until Phase 18
under their trigger,
with `dropdownRowHeight` rows (26 px); a click outside or Esc closes them.
Opened from a key, the keyboard is on their first row.

**The chrome never takes the keyboard.** The buttons of the top row, the
status bar's pills and keycap, the crumb row (Back, Forward, Up and the
segments), the tab strips (the "+" and "Open with…"), the Tool Dock's header
and tabs, and the editor pane's header refuse it: `AllowFocusOnInteraction`
is off and they are no tab stop. So a click acts on the active pane and
leaves the keyboard in it, and Tab always switches panes
(`view.focusOtherPane`, bound in the context `filesView`, which holds only
while a pane has the keyboard). Until 2026-09-30 a click on a top-row
button left the keyboard on the button, Tab fell through to WinUI and
walked the buttons as in a dialog (Article 7: the mouse is optional, and a
key must mean the same thing whatever was clicked before). Two exceptions
keep a tab stop and refuse only the click: the rail's buttons, because Up,
Down and Shift+Up/Down work on a button the keyboard was walked to, and the
classic sidebar's rows, because Ctrl+Shift+E without the rail puts the
keyboard on the first one. The overlays and the full-column views (the
palette, Quick Open, the prompt, the theme picker, the plugin list, the
review dialog, the marketplace, the context menu and its edit mode, the
transfer flyout, the update dialog) are not chrome: Tab walks there. A new
chrome button follows the rule: `ChromeKeyboardTests` reads the XAML and the
code that builds buttons and fails on one that takes the keyboard;
`ChromeKeyboardEndToEndTests` presses the dual toggle, the hamburger and the
workspace pill on a real window and checks the keyboard focus line; the live
check does it with the real mouse ("compact: a real click on the top row's
Toggle dual pane…" and the hamburger step).

**The branch.** Until workspaces exist, the workspace is the git
repository that holds the active pane's folder: the nearest folder at or
above it with a `.git` folder or file. The window reads no files (brief §1),
so it asks the core (`workspace_info`, [ipc.md](ipc.md), "The workspace of
a folder") when the active folder changes and when the window comes to the
front, so a branch switched in a terminal shows. The branch is the name in
the repository's `HEAD`; a detached `HEAD` shows the commit's first 7
characters, and a worktree's `.git` file is followed to its own `HEAD`.
Outside a repository, or with a core that does not know the request, the
pill shows the name alone, and Quick Open searches the active folder.

### The breadcrumb row

Every pane has, top to bottom: its tab strip (`tabRow`, 32 px), its
breadcrumb row (`breadcrumbRowHeight`, 28 px), the column headers and the
list (or a tool). The pane's old header, with the folder's name and path,
is gone.

- **Back, Forward and Up** first, as `navButtonSize` (20 px) buttons.
  They run `go.back`, `go.forward` and `go.up` with the pane's index
  (`{"pane": 0 or 1}`), so a click acts on that pane and makes it the
  active one; a key or the palette acts on the active pane. A button with
  nowhere to go is drawn at 30 % and does nothing (`NavState`).
- **The crumbs**, in the mono font at 11 px, `›` between them at 35 %
  white; the last one white, the others at 70 %. A click goes there.
  Segments never shrink one by one: a path of more than 3 parts in dual
  mode (5 in single) collapses to `Drive › … › parent › current`
  (`Breadcrumbs`); the `…` goes to the folder it stands for, the deepest
  one it hides, and its tooltip is that path. A row still too narrow
  scrolls to its end.
- **Ctrl+L** (`go.toPath`) turns the active pane's row into a text box
  with the path selected; Enter goes there, Esc puts the crumbs back.
  The box is the pane's: the pane's keys that type nothing (F5, Ctrl+T,
  Ctrl+Tab) act on the pane from it, and the box keeps its typing and
  editing keys (keybindings.md, "Contexts").
- The active pane's row has an accent-tinted fill (6 %); the other pane's
  has none. The drive list (Alt+F1, Alt+F2) and the prompts open under the
  row.

### Tabs in the shell

Since Phase 16 the strip shows from the first tab (the handout's rule: a
theme may not remove it). Since 2026-09-30 it is the window's own row of
tabs, drawn as the creator's design page draws it (option 1a and the
handout's "Tab strip"), and not WinUI's `TabView`: the `TabView` gave a
grey rounded block, a bar floating over its corners, an icon on every tab
and a boxed close button.

- **The strip** is `tabRow` high. It has a white 3 % fill and a 1 px
  hairline under it. Every tab fills its full height.
- **A tab** has 10 px of space at each side and is at most 160 px wide.
  Its name is cut with an ellipsis, and its tooltip is the full path.
  There is no icon on a folder tab. A locked tab shows a 12 px lock before
  its name, and a tab that shows a tool shows the tool's glyph.
- **The tab in front of the active pane** has a white 8 % fill, a 2 px bar
  on top in the accent, white text in weight 600, and its ×. The × is a
  plain glyph with no box, at 55 % white. It turns white under the
  pointer, and its target is at least 16 x 16 px.
- **The tab in front of the other pane** has a white 6 % fill, the bar at
  white 30 %, weight 600 and text at 90 % white. Its × shows only while
  the pointer is over the tab.
- **The other tabs** have no fill, text at 65 % white and a 1 px hairline
  at the right. Under the pointer they get a white 6 % fill and white
  text. They close with a middle click or their menu.
- **Corners are square**, as the design draws them: `tabRadius` is 0 in
  the default look. A theme that wants round tabs sets it. It rounds the
  two top corners of a tab, and the 2 px bar follows the curve, because
  the bar is the tab's own top border. Commander Compact has 3 px.
- **The colours are the window's tokens** (`CbTabActiveFillBrush`,
  `CbHoverFillBrush`, `CbTabFrontBarInactiveBrush`,
  `CbTabInactiveTextBrush`, `CbTextPrimaryBrush` and `CbRowTextBrush` for
  the text, `CbTextTertiaryBrush` for the ×), so a theme's colours reach
  the strip.
- **Tabs do not wrap.** When they do not fit, the row scrolls sideways,
  and the tab in front scrolls into view when it changes.
- **The "+"** is 24 px wide and runs `tab.new` for that pane. It is right
  after the last tab while the tabs fit. When they do not fit, it stays
  at the end of the strip and does not scroll away.
- **The keyboard never rests on the strip.** Nothing in it takes focus, so
  a click leaves the keys with the pane.

The handout's "Open with…" button at the strip's right end, which opens the
palette on the Editor commands, is hidden since 2026-09-30 (the creator's
call: with Markdown Preview as the only editor its list was thin) and
returns when a second editor exists; the commands stay in the palette.
Ctrl+1 to Ctrl+9 run `tab.select` for the tab at that place ("Tabs"). A
tab keeps, besides its folder, history, order, cursor and marks, its
list's scroll position and its find text.

### The column view

Since Phase 19f a pane's folder tab has a mode: `files`, the list, or
`columns`, the column view of macOS Finder
([ADR 0016](decisions/0016-column-view.md)). The dual pane stays the base
(Article 5): either pane may show columns.

- **Switching.** `view.toggleColumns` (Ctrl+Alt+C; "Toggle Column View" in
  the palette) switches the active pane's front tab. A tab that shows a
  tool has no mode. The mode is saved with the tab in `ui.tabs` as
  `"mode": "columns"` ([config.md](config.md)), so the tab starts in
  columns after a restart. A new tab (Ctrl+T, or the new tab of a locked
  tab) starts in the mode of the tab in front.
- **A column** is one folder's listing, names only: the icon, the name,
  and a chevron at the right of a folder row. It is `columnViewWidth`
  wide: 220 px, 180 in Commander Compact. The widths cannot be dragged in
  this version. When the view starts, its one column is the tab's folder.
- **Opening.** Enter, Right or a click on a folder row lists that folder
  in a new column right of the row's column. The columns that were right
  of it go. The keyboard moves into the new column, onto its first row.
- **Moving.** Left moves the keyboard to the parent column. The columns
  right of it stay on screen until another row opens. Right moves the
  keyboard into the column on the right when that column shows the cursor
  row's folder; on another folder row it opens it, as Enter does. Up,
  Down, PageUp, PageDown, Home and End move the cursor in the keyboard's
  column. Backspace (and Alt+Up, and the Up button) in a column past the
  first is Left. In the first column it lists the parent as the new first
  column, with the cursor on the folder it came from; the other columns
  stay.
- **The path.** The deepest column's folder is the tab's path: the
  breadcrumb row, the tab's title and the saved tab show it. Going to a
  folder another way (a crumb, Back, the sidebar, Ctrl+L, another tab)
  starts the view over, with that folder as the one column. Ctrl+R and a
  new order list the keyboard's column again and keep the columns.
  Leaving the mode shows the deepest folder in the list.
- **Cursor, marks and commands.** Each column keeps its own cursor and
  marks. The keyboard is in one column at a time. There a selected row
  has the list's fill and accent bar. A column the keyboard left keeps its
  cursor row lit with a quieter fill, so the path shows at a glance. The
  file commands (F5, F6, F7, F8, F2, Properties, the context menu, the
  marks, find in pane, quick search, the order) act on the keyboard's
  column as they act on the list. A copy or move from the other pane goes
  to this pane's keyboard column's folder, which is the deepest one except
  after Left. `window_state` reports the keyboard column's folder, cursor
  and marks for the tab in front, so a program's `{cwd}`, `{path}` and
  `{selection}` name that column.
- **The mouse.** A click moves the keyboard to the clicked column and
  selects the row as in the list: Ctrl toggles it, Shift extends to it. A
  plain click on a folder opens it. A double-click on a file opens it. A
  right-click opens the context menu for the row, or for the column's
  folder on its empty space. A row of the column view cannot be dragged
  in this version.
- **Scrolling.** The columns sit side by side and do not wrap. The strip
  scrolls sideways, and the keyboard's column is kept whole on screen, so
  a new column comes into view. Over a column whose rows are longer than
  it, the wheel scrolls those rows. Over a column whose rows fit, or with
  Shift, the wheel scrolls the strip sideways.
- **No preview.** The last column holds a folder. A viewer of the
  selected file is an extension's job (Article 10).
- **Listings.** Each column is one `list_directory` with `watch`. The
  keyboard's column is the pane's own listing. It trades places with the
  column the keyboard moves to, and nothing is listed again. A column that
  goes releases its listing at once (`close_listing`), so the core
  watches only what is shown. A change in one folder refreshes that
  column only. A search in the pane shows its hits in the list, and the
  columns come back when it ends. After a restart of the core the view
  starts over with the keyboard's folder.
- **The log** (Article 12), target `cabinetos_ui::columns`: "column view
  entered" and "column view left" (the pane, the path); "column opened"
  (the pane, the depth, the path, the listing ID, the entries, the time);
  "column released" (the pane, the depth, the path, the listing ID);
  "column view started over" (another way to a folder); "column lost";
  and "column view changed" at every change of the columns or of the
  keyboard's column (the pane, the depth, the keyboard's column, the
  folders, each column's cursor, the deepest folder, the listings). The
  "shell state" line has `pane<i>_listings` (the listings the pane holds)
  and `pane<i>_columns`. "tabs saved" counts the tabs in the columns mode.
- **The code.** `ColumnStack` (in `CabinetOS.Core`) holds the columns'
  rules without a window. `PaneColumns` runs the moves and holds the other
  columns' listings (`ColumnListing`); `PaneModel.SwapListing` trades the
  pane's listing with a column's. `ColumnView`, `ColumnList` and
  `ColumnRow` draw them in `FilePane`, in the list's place.
- **Checks.** `ColumnViewTests` (Core): the column stack (open, drop, Left
  and Right, the deepest folder, what goes), a tab's mode in `ui.tabs`
  both ways, and the folder `window_state` reports.
  `ColumnViewEndToEndTests` (with `CABINETOS_UI_E2E=1`): three levels
  opened by Enter and by clicks, Left twice, a row opened again, the list
  back, every released listing closed in the core's log, and a second
  window that starts in columns. It uses the snapshot steps
  `column-view:`, `column-open:`, `column-key:` and `column-click:`
  (`CABINETOS_UI_SNAPSHOT_STEPS` above). The live check's section 20 does
  the keys for real.

### Find in pane

Ctrl+F (and Alt+F7, Total Commander's key; `search.focus`) opens the find
widget of the active pane's tab: an acrylic box that drops from the right
end of the breadcrumb row, with a search glyph, a 150 px box ("Find in
{folder}"), the count "n of m" and a close button.

- **Typing filters the pane's list** to the names that hold the text, case
  ignored. The core matches (`match_entries` with `*text*`), as for the
  pattern box, so the window scans no names; `PaneFind` asks and drops
  answers that came too late. The other pane is not touched.
- **Enter** puts the cursor on the first match and keeps the widget and
  the keyboard; Down gives the keyboard to the list. **Esc** closes the
  widget: every row shows again.
- **The pane's keys work from the box.** The box is the pane's: a key that
  types nothing runs the pane's binding, so F5 copies the cursor row to
  the other pane, Ctrl+F3 sorts, Ctrl+T opens a tab and Ctrl+W closes it.
  The box keeps its typing and editing keys: Ctrl+A selects its text, not
  the rows (keybindings.md, "Contexts"). Until 2026-10-01 only the
  Immutable System Tier worked in the box.
- **The selection survives.** Marks the filter hides are kept aside, out of
  every count and command, and come back when it ends (`SelectionModel`).
  A text with no match leaves the cursor nowhere; when the find ends, it
  comes back to the row it was on under the text before.
- "This folder is empty." does not show while a filter shows no row: the
  count says "0 of 0".
- Another folder closes the find; a refresh of the same folder asks again.
  A tab keeps its find text, and a tool tab in front hides the widget until
  the folder tab comes back. In search results, and over a tool, Ctrl+F
  says why it cannot find there.

### Quick Open

Ctrl+P (`quickOpen.show`) or a click on the command center lists the
workspace's files and folders on the command palette's surface. The core
finds and ranks them (`search` under the workspace, 50 names, from its
index or a walk); `QuickOpenModel` asks after 80 ms without a key, drops
late answers and keeps the highlight. A row shows the name and, in the
mono font, its folder from the workspace's own name down.

- Up, Down, PageUp and PageDown move the highlight. **Enter** opens the
  row in the active pane: a folder is listed, a file's folder is listed
  with the file under the cursor. **Ctrl+Enter** opens it in the other pane
  (dual mode is turned on first when needed), which becomes the active
  one. A click does the same.
- **`>`** typed first switches to the commands: the command palette opens
  with the rest of the text. **Backspace** in the palette's empty box comes
  back to Quick Open (`PaletteInput`). Ctrl+Shift+P still opens the
  commands at once.
- When the answer is not complete (`complete: false`: the core's walk
  stopped at its limit of 2 s or 200,000 entries, or a volume is still being
  indexed), a line under the rows says so, with rows or without:
  "Nothing found, but not every name was searched: the search stopped at its
  limit of 2 s or 200,000 entries. The indexer (docs/indexer.md) searches
  whole volumes." A bare "0 results" would say there is no such name. More
  letters do not help, because a walk that stopped goes the same way and
  stops at the same place; the indexer reaches further
  (`QuickOpenModel.Note`). Until 2026-10-01 the walk stopped after 20,000
  entries, so a name after the first 20,000 of a large folder was never
  found.
- Esc or a click outside closes it, and the keyboard goes back to the pane.
  Its box is a text box, so it keeps the keys that type or edit, and a key
  without a context that types nothing runs ("Keys, contexts and
  commands"): Ctrl+Shift+P shows the commands instead, and Ctrl+P closes
  Quick Open (`quickOpen.show` toggles it). Until 2026-10-01 Ctrl+P did
  nothing there.

### The shell's snapshot steps and checks

The snapshot aid's steps for the shell: `shell:<label>` writes "shell
state" into the log (the window's size, the top row's height, the command
center's place or `hidden`, the clusters' edges, the workspace and its
root and branch, whether Quick Open and the palette are open and Quick
Open's rows, an open menu's rows, the context menu's list and icon row
(`context_menu`, `context_quick`) and Windows' menu (`windows_menu`, a
submenu as `Send to›`), the active pane, and for each pane its
path, crumbs, the nav buttons' state, whether its row is a text box, its
shown and listed rows, marks, cursor, scroll position, find text and count,
tabs, and the tab strip's and breadcrumb row's heights); `find:<text>`
opens the active pane's find and types; `find-key:enter|down|esc`;
`quick-open:<text>` opens Quick Open and types; `quick-open-key:enter|
ctrl+enter|down|esc`.

- `ShellTests` (Core): the breadcrumb collapse (the handout's path at 380 px
  in dual and single mode, one part, a drive's root, a trailing
  backslash), the 640 px rule and the command center between the
  clusters, the nav buttons' state, the find filter (one pane only, marks
  kept aside and back, no match, Esc), the tab state, Quick Open (`>`,
  Backspace, the request, late answers), the hamburger's content from the
  registry, and the new default keys. The branch is the core's
  (`workspace_info`): its tests read a branch ref, a detached `HEAD`, a
  worktree's and a submodule's `gitdir:` file, and no `.git`.
- `ShellEndToEndTests` (with `CABINETOS_UI_E2E=1`): the top row at 924 px
  (all controls, the command center at least 200 px, no overlap) and at
  620 px (hidden); Ctrl+F filtering one pane, Enter, a text with no match,
  Esc; a second tab going elsewhere and the first one's folder, cursor and
  scroll coming back; closing the last tab; Ctrl+9 with one tab; the
  collapsed crumbs; Quick Open in a repository with Enter, Ctrl+Enter and
  `>`; the pill's branch and its absence; the hamburger by its accessible
  name and Esc; the pill's dropdown by a click and by Ctrl+K Ctrl+W; a
  click on a crumb, Back, Ctrl+L and Esc; and Settings with an editor that
  is not there.
- The live check's section 16 presses the keys and clicks for real: Ctrl+L
  and Enter, Ctrl+F with a text, Enter and Esc, Ctrl+P with Esc and with
  Enter, Alt+Left, the hamburger found by its accessible name and closed by
  a click outside and by Esc, and a click on a crumb. Its folder `shell16`
  holds a fake repository (a `.git\HEAD` with `ref: refs/heads/live-16`), so
  it is the workspace: Quick Open in `alpha` finds a file in `beta\deep`,
  Enter shows that folder, Alt+Left comes back, and the log line of Quick
  Open carries the branch `live-16`. The window's log writer works in its
  own thread, so the section reads each log line only after it has come
  (`WaitShellLines`, up to 5 s) and never right after a fixed sleep.

### Where the shell differs from the handout

- **The hamburger's rows are the registry's titles:** "Browse Plugins and
  Themes" and "Open Keyboard Shortcuts" where the handout says
  "Marketplace" and "Keyboard Shortcuts", so a rename in the registry shows
  in both places. Plugins cannot add rows yet: the registry has no mark
  for "in this menu", and the handout changes nothing in the plugin model.
- **Workspaces do not exist yet.** The pill and the command center say
  "Default", the workspace is the active folder's repository (above), and
  "Open folder as workspace…" says so.
- **The top row is the drag area only where Windows gets the pointer.** A
  click on its empty part while a dropdown is open moves the window and
  leaves the dropdown open; a click anywhere else closes it.
- **The command center moves off the centre** when the clusters leave less
  room there than its width, as at 924 px with the sidebar shown: it keeps
  at least 200 px between them instead of lying over the pill or the view
  buttons.
- **Ctrl+9** goes to the ninth tab and does nothing with fewer, as the
  handout's "tab n" says; browsers make it the last tab.

## Total Commander's keys

Sub-phase 11a gives Total Commander users the keys their hands know
([research/total-commander.md](research/total-commander.md), Part 3 (b)).
The core lists 31 new commands with target `ui` and their keys (protocol
12, [keybindings.md](keybindings.md)), and gave five existing commands a
Total Commander key next to their own: F8 and Shift+F8 delete, Shift+F6
renames, Alt+F7 finds (in the pane since Phase 16), Ctrl+Num + marks every row. The window's
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
| Num * | `edit.invertSelection` | The files' marks turn around; folders keep theirs. The cursor row the Windows style selects is no mark ("Marking") |
| Ctrl+Num - | `edit.unselectAll` | Nothing marked; a command then acts on the cursor row |
| Num / | `edit.restoreSelection` | The marks the last copy, move, delete or unmark cleared in this pane, found again by name; a name that is gone is skipped |
| Ctrl+Shift+C, Ctrl+K Ctrl+N, Ctrl+K Ctrl+P | `edit.copyFullPath`, `edit.copyName`, `edit.copyFolderPath` | The targets' paths, their names, or the pane's folder, on Windows' clipboard as text: one per line, no quotes. CabinetOS's own file clipboard (Ctrl+C) is not touched |
| F3 | `file.view` | The cursor file in the first installed Tool Extension that shows it; without one, a note in the status bar, never the file's default program (which for a program would run it). On a folder, its size, as Total Commander's F3 |
| F4 | `file.edit` | The cursor file opens for editing, and never runs: `files.editor`, else the type's edit verb, else Notepad; the core decides (`edit_path`) |
| Shift+F4 | `file.newTextFile` | A name box over a new row ("New text file") |
| (none) | `file.windowsProperties` | Windows' own property sheet for the targets, or the folder (`show_properties`); also the "Windows Properties" button of the Properties dialog |
| (none), Shift+Alt+Enter | `file.calculateFolderSize`, `file.calculateAllFolderSizes` | The marked folders, or the cursor folder; or every folder of the listing: measured ("Folder sizes") |
| Ctrl+Alt+P, Ctrl+Shift+Enter | `terminal.insertPath`, `terminal.insertSelectedPaths` | The terminal shows, its default shell started when none runs, with the pane's folder, or the targets' paths, typed at the prompt, quoted for that shell and without Enter (`terminal_type_paths`); the terminal gets the keyboard |

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

In the Windows style that cursor row, selected alone, is the cursor, not a
mark (`SelectionModel.HasMarks`, which Restore Selection also uses). Num *,
Num + and Alt+Num + drop it before they mark, so a cursor on a folder is
not counted with the files they mark: after Home and Num * in a folder of
two folders and six files, the status bar says "6 selected, 29 B" and the
folder under the cursor is unmarked. Before, it said "7 selected, 29 B"
(the live check of 2026-09-29), and F5 would have copied the folder too.
A selection of several rows, or of a row the cursor is not on, is marks,
and stays.

### Prompts in the palette's frame

The pattern box, the pinned folders and the drive list use one prompt,
`PromptBox`: the palette's Acrylic panel, entrance and rows, with a label,
a box, an optional check box, a few rows and a hint line. Up and Down move
the highlight, Enter takes it, Esc (`overlay.close`) or a click outside
cancels, and the keyboard goes back to the pane. A prompt closes the other
overlays as it opens (`PromptBox.Opening`: the palette, Quick Open, the theme
picker, the plugin list), so the drive list never stands under the theme picker
or the other way round.

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
  pane's breadcrumb row with the sidebar's `list_volumes` data (name, free space),
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

### Column widths

A file list has four columns: Name, Modified, Type and Size. Modified,
Type and Size have a width in pixels each. Name takes what is left of the
list's width, and never less than the theme's `nameColumnMinWidth`.
Until the user changes a width, the theme sets them: its weights
(`nameColumnWeight`, `modifiedColumnWeight`, `typeColumnWeight`) share the
space that Size (`sizeColumnWidth`) and the gaps leave, as before this
change. Once the user drags or fits a column, the three pixel widths are
the user's, and they win over every theme's weights and Size width. The
theme's `columnGap`, paddings and fonts still apply (`ColumnLayout`).

**The grips.** Each pane's column headers have a grip at each of the
three dividers: Name|Modified, Modified|Type and Type|Size. There is none
after Size, which ends at the pane's edge. A grip is 8 px wide and centred
on its divider. Over it the pointer is the resize cursor, and a 1 px line
in the divider colour shows while the pointer is over it or drags it.

**A drag.** The divider follows the pointer, and only the two columns
beside it change:

| Grip | Right | Left |
|---|---|---|
| Name\|Modified | Name wider, Modified narrower; Modified's right edge stays | Modified wider, Name narrower |
| Modified\|Type | Modified wider, Type narrower; Modified's left edge stays | Modified narrower, Type wider |
| Type\|Size | Type wider, Size narrower; Size keeps the pane's edge | Type narrower, Size wider |

Modified, Type and Size keep at least 40 px each; a column that is
narrower already (a hand edit may set 24) keeps what it has. Name keeps
at least its minimum, so a drag that would cut Name under it stops there.
While the pointer moves, the header and every row on screen follow at
once, in both panes; nothing is measured and nothing is written. When the
button is released, the widths are saved once. A press and release that
moved nothing changes nothing.

**A double-click.** On a grip, it fits the column at the grip's left:
Modified for the first two grips, Type for the third. On a heading's
text, it fits that heading's column. The Name heading fits Modified, Type
and Size together, so Name gets the most room; Name has no width of its
own. A fit makes a column as wide as the widest text of that column among
the rows on screen, plus the room its cell keeps beside the text (the
default look's 8 px after the Modified and Type texts), or as wide as its
heading with the room of the sort chevron, whichever is wider; never less
than 40 px. "The rows on screen" are the rows the pane has made, which
are the rows in view and a few beyond: the pane makes only those, and
measuring every row of a folder of 100,000 entries would stall the
window. The texts are measured in the fonts the cells use (the Size
column's fixed-width figures under hairlines). A fit never leaves Name
under its minimum either: a column that would, gets what leaves Name its
minimum, and its text is cut short with "…". The Name heading's fit gives
each of the three the same share of the shortfall. A single click on a
heading does nothing yet: sorting by it comes later, and only the
double-click is taken.

**Saved and shared.** The widths are one set for both panes: a drag or a
fit in one pane changes the other at once. The window saves them in
`cabinetos.json` as `ui.columns`, `{ "modified", "type", "size" }` in
whole pixels, through `set_value`, when a drag ends or a fit happens
(Article 6; [config.md](config.md)). An edit of the file by hand applies
at the next `config_changed`, live, as the other `ui.*` settings do, and
so does another window's save. `null` or no `ui.columns` gives the
theme's widths. The core refuses a width that is not a whole number from
24 to 2000. When a save is refused, the widths stay on screen and the log
says why. A theme change keeps the user's widths.

**Commands.** `view.fitColumns` ("View: Fit Columns to Content") fits
the active pane's Modified, Type and Size, as the Name heading's
double-click does. `view.resetColumns` ("View: Reset Column Widths")
gives the theme's widths back and writes `ui.columns: null`. Both run in
the window, in the file panes (`filesView`), and have no keys: the
palette is their place (Article 7).

**Logs.** The target is `cabinetos_ui::columns`. "columns changed" says
`how` (`drag`, `fit`, `reset`, `config` or `theme`), the `modified`,
`type` and `size` widths, `name` (the Name width they leave in the active
pane) and `user` (whether the widths are the user's). A drag logs it once,
when the button is released. "columns fitted" says what a fit measured
for each column: the widest text (`<column>_text`), its width, the cell's
room, the heading's width, the fit, and the widths it gives. "columns
saved" follows a write the core made; "columns not saved" is a warning
with the core's reason.

**Snapshot steps.** `columns:<label>` logs "columns shown": each pane's
four column widths as laid out, the width they share, the first row's
four widths (the rows must follow the header) and how far the grips are
from the dividers. `column-drag:<divider>|<pixels>` drags a grip of the
active pane (1 is Name|Modified, 2 Modified|Type, 3 Type|Size) by that
many pixels through the grip's own drag steps, and lets go.
`column-fit:<column>` fits as a double-click on that heading does
(`name`, `modified`, `type` or `size`). The last two do not wait, so an
`until:config` right after them sees the save. The end-to-end test
`ColumnsEndToEndTests` uses them; section 19 of the live check drags and
double-clicks with the real mouse.

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

**Every folder of a listing.** The setting `panes.folderSizes` (default
`false`) and the command `view.toggleFolderSizes` (Toggle Folder Sizes, in
the palette, no default key) measure folders without a key. While the
setting is on, a pane asks the core to count the folders of a listing when
it opens or is listed again. It uses the same `measure_paths` request as
Shift+Alt+Enter, with the folders in the listing's order. A folder that has
a size, or is being counted, is not asked about again. So a watched folder
that is listed again counts only its new folders. The Size column shows the
running totals, and the status bar the sum, as for a count by key. A pane
that leaves its folder cancels the count still running (`cancel_measure`),
so the core does not go on walking a large tree for a folder nobody looks
at. A count whose `measure_started` reply comes after the pane left is
cancelled too. The core walks on its blocking threads, and the window only
asks and shows. The setting is off by default because the walk costs disk
time (Article 4). A listing with thousands of folders sends one request
with all their paths.

The toggle asks the core to write `panes.folderSizes` with the opposite
value. The window then follows `config_changed`, as it does for an edit of
the file by hand. Turning the setting on counts both panes' listings at
once. Turning it off cancels the counts still running in both panes,
those started by key too, and starts none. The sizes already shown stay
until the folder changes. Space, Calculate Folder Size and Shift+Alt+Enter
work as before, with the setting on or off.

The log says what happened (Article 12). "folder sizes asked" has `pane`,
`folders`, `why` (`listed` or `setting on`) and `listing_request`. "folder
sizes counted" comes when a count ends, with `pane`, `folders`, `bytes` and
`cancelled`. "folder sizes cancelled" has `pane`, `measures` and `why`
(`left the folder` or `setting off`). "folder sizes follow the
configuration" has `on`. The end-to-end test `FolderSizesEndToEndTests`
checks all of it on a real window and core. The live check does the same
with the palette.

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
  and F8, Shift+F8, Ctrl+Alt+P (Ctrl+P until Phase 16), Ctrl+\, Alt+F1, Ctrl+U; the planning session
  runs it.

## Tabs

Each pane can hold several tabs (Phase 12, Article 5: the dual pane stays
the base, and a tab is a second folder in one of its panes). The strip sits
at the top of the pane. Until Phase 16 it was hidden while the pane had one
tab; the shell redesign shows it from the first tab, `tabRow` high (32 px;
24 px in Commander Compact), with a "+" (the handout's "Open with…" is
hidden until a second editor exists; "The shell", "Tabs in the shell").
It is the window's own row of tabs (`PaneTabs`, "Tabs in the shell"),
with no drag and no reorder, so every change goes through a command.

- **What a tab holds:** a folder with its own history, order, cursor and
  marks, and a lock. Or a Tool Extension (a Markdown Preview, say). A
  tool tab shows the tool in the pane and keeps no folder of its own.
- **The pane's model stays the live state of the tab in front.** A tab
  behind holds its own state (`PaneTab`) and gives it back when it comes
  to the front. The window makes the pane match the front tab in one
  queue per pane (`QueueShow`), so two fast changes cannot cross each other.
- **The tab that went behind last keeps its listing for 30 s** (since
  2026-10-01, the speed review's proposal E). Its listing stays open in
  shared memory and the core keeps watching its folder, so a switch back
  shows its rows again, with its cursor and marks, without a new
  `list_directory`. For 100,000 files the first frame comes about 17 ms
  after the switch instead of 60 to 90 ms. A pane keeps one such listing,
  about 10 MB for 100,000 entries: the next tab that goes behind takes its
  place, and the listing kept before is closed. The core closes a kept
  listing ("listing closed" in its log) when its 30 s are up, when its
  folder changed (`listing_refreshed`) or is gone (`listing_lost`), when
  its tab is closed or moved to the other pane, and when it no longer fits
  its tab (the order changed meanwhile through `panes.sort`). The tab then
  lists its folder again, as before. A restart of the core forgets the
  kept listing without closing it, since the new core never had it. A tab
  in the column view keeps nothing ("The column view"). `ParkedListing`
  in `CabinetOS.Core` holds the rules; `CABINETOS_UI_PARKED_LISTING_MS`
  shortens the 30 s for the tests. The log (target `cabinetos_ui::tabs`)
  says "tab listing kept", "tab listing taken back", "tab listing
  released" with why, and "tab listing forgotten"; the "listing shown" of
  a listing taken back has `kept: true`.
- **The front tab has a 2 px bar**: the accent in the active pane, white
  at 30 % in the other. Nothing is dimmed (the handout's decision: the
  design has no Acrylic on the inactive pane).
- **Tab is still the pane switch, and Alt+Left and Alt+Right are still
  Back and Forward.** In a locked tab, Back and Forward stay on the
  folder and say so in the status bar. When the other pane's front tab is
  a tool (a Markdown Preview), Tab gives the keyboard to the tool's page,
  as a click into it does; the active pane stays the one with the folder.
  Until 2026-10-01 Tab did nothing then: the tool covers the pane's list,
  which cannot take the keyboard, so the keys went on acting on the
  folder in the first pane.
- **A locked tab opens a new tab** when you go into another folder
  (Enter on a folder, a crumb, a pinned folder, Backspace). A tab that
  shows a tool has no lock.
- **The last folder tab cannot be closed.** The window says so in the
  status bar. A pane always has a folder to show.

| Command | Key | What it does |
|---|---|---|
| `tab.new` | Ctrl+T | A new tab at the same folder, next to the front tab |
| `tab.close` | Ctrl+W | Closes the front tab |
| `tab.next`, `tab.previous` | Ctrl+Tab, Ctrl+Shift+Tab | The next tab, the one before it; both go around |
| `tab.toggleLock` | (palette) | Locks or unlocks the front tab |
| `tab.openFolderInNewTab` | Ctrl+Up | The folder under the cursor opens in a new tab (the current folder when the cursor is on a file) |
| `tab.moveToOtherPane` | Ctrl+K Ctrl+Right, Ctrl+K Ctrl+Left | Sends the front tab to the right or to the left pane, with its history and marks |
| `tab.select` | Ctrl+1 to Ctrl+9 | The tab at that place comes to the front; a place past the last tab does nothing. The strip's clicks run it with the tab's index. From the palette, without a place, it says which keys to press |

All of them are in the palette in the category "Tab" and work while a
pane's list has the keyboard (`filesView`). `tab.select` is in the registry
since Phase 16; one command has the nine keys, and the key's digit names
the tab. A tool tab
cannot move to the other pane (its process belongs to its pane's page),
and Ctrl+U (`view.swapPanes`) says "Close the tool tabs first" while one
is open.

**What is saved.** The window writes `ui.tabs` with `set_value`, at most
once a second, and once more when it closes (this shares the one second
that closing may use with `ui.lastPaths`). At start it reads `ui.tabs`;
a folder that is gone falls back to its parent folders, then to the
first-start folder, as `ui.lastPaths` does. `ui.lastPaths` is still
written, so a core or a window of the older kind finds its folders. Tool
tabs are not saved: a tool page needs a file and a process. With two
windows open, the last write wins, as for `ui.lastPaths`.

**What the core hears.** The window sends `window_state` (protocol 13)
on every change of the tabs, the active pane, the cursor or the marks,
joined into one message every 50 ms. It has `active_pane` and for each
pane `tabs` (`path`, `locked`, `tool`), `active`, `cursor` and `marked`
(at most 1,000 paths). The core only stores it (`get_window_state`); nothing
in the window depends on the reply. A core that answers `unknown_request`
makes the window stop sending, with one log line.

**Cost of scrolling.** Checked on 2026-09-30 with
`ui/livecheck/scroll-bench.ps1` on the 100,000-entry folder (release
builds, display awake, other builds running on the PC, so the runs are
noisy). UI-thread work per second of scrolling, lowest and middle value
of all runs:

| Window | Runs | Lowest | Middle |
|---|---|---|---|
| Before tabs | 11 | 396 ms | 406 ms |
| With tabs, one tab (row hidden) | 14 | 402 ms | 418 ms |
| With tabs, three tabs (row shown) | 11 | 388 ms | 402 ms |

That is at most 3 % more, under the handout's 5 % limit. (The strip was
a WinUI `TabView` when this was measured; it is the window's own row
since. The `ItemsRepeater` row the handout names as the fallback was
never needed.) `scroll-bench.ps1 -Before "tab:new;tab:new"` repeats the
third row.

Tests: `TabTests` (the model in `CabinetOS.Core`: strip, lock rule,
`ui.tabs`, `window_state`), the snapshot steps `tab:new`, `tab:close` and
the others in the list of the snapshot aid, and `TabsEndToEndTests`, which
opens three tabs in the left pane (the last one locked) and two in the
right pane of a real window, closes it, starts it again and finds them all,
the locks and the tabs in front too (with `CABINETOS_UI_E2E=1`). The kept
listing: `ParkedListingTests` (taken back by its own tab only, one per
pane, gone when its time is up, at the core's events, with its tab, and
when it no longer fits; forgotten without a close after a restart of the
core) and `KeptListingEndToEndTests` (with `CABINETOS_UI_E2E=1`: four
switches between two tabs of a real window ask the core for no listing,
the first tab comes back with its cursor, and with the lifetime set to 3 s
the kept listing is closed in the core and the tab lists its folder again).
The live check has a section "12: tabs" with real keys; its step "Ctrl+Shift+Tab and
Ctrl+Tab" checks that the two tabs take their kept listings back.

## Search

The Search view's field (Ctrl+Shift+F, `view.showSearch`; placeholder
"Search {folder}") finds files and folders by name through the core
([ipc.md](ipc.md), "Search"; [indexer.md](indexer.md)). The core searches
and ranks; the window shows the hits in the core's order and filters
nothing.

- Typing sends `search` with the text, `limit: 100` and `root`: the
  active pane's folder. It goes out once 150 ms pass without another key.
  Enter sends it at once; Down moves the keyboard to the view's first hit
  (until Phase 16 the command bar's field sent the keyboard to the pane's
  hits).
- The hits replace the folder in the active pane, in the same rows: the
  name with its type's icon, the folder the hit is in (where a listing
  shows "Modified"; from the searched folder's name down, `docs\log`, so
  the part that tells hits apart is not cut off; the whole path for a
  whole-volume search), and the type name. The reply has paths only, so the
  type and icon are what the core said about that extension in a listing
  before, and there is no size or time. The bar above the hits reads
  "Search: {query} · {n} hits · {index or walk} · {time}", and its tooltip
  names what was searched.
- Under the title, a note says whether the answer is complete: complete,
  or incomplete because a walk stopped at its limit (2 s or 200,000
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
  Alt+F1, Alt+F2, Ctrl+K Ctrl+P and Ctrl+Alt+P act on the pane, not the hits,
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
  reason, the folders for `fs:read` and `fs:write`, and the hosts and the
  stored secrets' names for `net`), the box "Trust
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

### What plugins ask of the window

Phase 14 (an AI agent as an extension) needs a few general things from the
window. None of them knows an agent (Article 10): a plugin of any kind can
use them. The core side is in [ipc.md](ipc.md), "Commands" and "Previews",
and [plugins.md](plugins.md).

- **A command that asks for text.** A plugin's command may have an `input`
  in `list_commands` (an optional `title` and `placeholder`, from
  `plugin.json`). Run from a key or the palette, it first shows the
  palette's prompt box: the label is the input's `title` (the command's own
  title when it has none), the box has the placeholder, and the hint says
  "Enter runs the command · Esc cancels". The command then runs with
  `{ "input": text, "path", "paths" }`: `path` and `paths` are the active
  pane's files, as for every plugin command. Esc, or an empty answer, runs
  nothing (the outcome is `Cancelled`, and the log says "command cancelled:
  its prompt was closed"). A caller that gives `input` itself, such as a
  tool page, is not asked. The code is `PluginInput` and the router's
  `AskInput` hook in `CabinetOS.Core/Commands`.
- **The preview pane.** A preview is a set of proposed changes that the
  core keeps until the user answers ([ipc.md](ipc.md), "Previews"). The
  window opens one (`open_preview`, and `preview_opened` answers with the
  listing) by two generic rules: a plugin command's `command_result` with a
  string field `preview` (rule 1), or a plugin event of any name whose
  payload has a string field `preview` (rule 2). It shows in the other pane
  (the inactive one; in single-pane mode the right pane, which then shows
  for as long as the preview waits): a bar with the title, the number of
  changes and "Enter applies · Esc cancels", over one row for each change.
  A row has the verb (Rename, Move, Copy, Delete, Create), the item's name,
  "→" and the target in the accent colour (the new name of a rename; the
  folder's name, with a closing backslash, for a move or a copy; its
  tooltip has the whole path), and the item's folder dim at the end, where a
  long path may be cut without losing the name. A delete has a red tint
  and a red verb. The pane under the preview is hidden while it shows.
  - **Enter** sends `preview_apply`; the reply `jobs_started` closes the
    preview, and the jobs come as events, so the transfer pill and flyout
    show them ("Changing 5 items"; a preview's renames and creates run as
    one job of steps). An error reply, such as a first job that cannot
    start, keeps the preview and says why under the rows. **Esc** sends
    `preview_cancel`. Both keys belong to the preview before the keymap
    while the keyboard is in the preview or in a list (not in a text box,
    the palette or a dialog), so Enter does not open the row under a cursor
    and Esc does not close something else while changes wait. The arrow
    keys, PageUp, PageDown, Home and End scroll the rows. The events
    `preview_applied` and `preview_cancelled` close a preview that another
    window applied or cancelled, or that expired after ten minutes.
  - A second proposal replaces the one on show (the first is cancelled, not
    applied). Turning dual pane off while a preview shows cancels it.
  - The rows come from the listing's section once (`PreviewSession.From`
    reads the change and target of each row), and the window closes the
    listing at once: nothing keeps the core's listing open.
- **Plugin events.** `plugin_event` carries the plugin's ID, a name and
  the payload as the text the plugin wrote, usually JSON. The window reads
  three rules, whichever plugin sends them and whatever the event is
  called (Article 10): an event whose payload has a string `notice` shows
  it in the status bar like the window's own notices (cut at 300
  characters); an event whose payload has a string `preview` opens that
  preview; and the event `badge` with `{ "view": …, "kind": "dot" |
  "spinner" | null }` marks a view's button in the activity rail.
- **Tool pages.** A page sends `{"type":"subscribe","plugin":"<id>"}` and
  gets that plugin's events as `{"type":"plugin-event","plugin","name",
  "payload"}` ([tool-extensions.md](tool-extensions.md), "Messages"); a
  page hears only the plugins it asked for, at most 16, and asks again
  after it loads again. Rows dragged from a pane over an open tool page
  are sent to it as `{"type":"paths-dropped","paths":[…]}`. While rows are
  dragged, a transparent layer lies over each open tool page (a WebView2
  takes drops for itself), with a 2 px accent edge and the caption "Send to
  {tool}" where a drop is possible. The drag carries the paths as text, one
  per line; the selection goes with it when the dragged row is part of it.
- **A `net` capability's hosts and secrets.** The permissions review shows
  a line under the reason, "Can reach: api.anthropic.com, localhost:11434",
  and, when the manifest names stored secrets, a second one, "Can use the
  stored secrets: anthropic", in the normal text colour; the plugin list has
  both in the tooltip. Only the names show, never a value. The user reads
  where a plugin may connect, and which secrets it may use there, before
  allowing it (Article 8).

Checked with the snapshot aid against the real core: a preview of six files
shows the rows (create, three renames, a delete tinted red, a move and a
copy); Enter ran the four jobs it makes and the files were as the rows said
(`Archive\` made, three renamed, the delete to the Recycle Bin, one moved,
one copied); Esc dropped a preview and changed nothing; a preview named by
an event opened; in single-pane mode the window showed two panes while the
preview waited and went back after; a fake plugin command with an `input`
showed its prompt and ran with the text; an event with a `notice` reached the
status bar; a drop reached a Markdown Preview page. `EndToEndTests` runs the
rows, open by ID, apply and cancel against the real core, and
`AgentPartsTests` the rest with fakes.

## The terminal

Ctrl+` (`view.toggleTerminal`), the terminal button in the top row,
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
  `terminal.profiles` (by default pwsh, cmd, wsl and claude). The caption says whether the shell follows the
  active pane: "cwd synced to active pane · {folder}", "cwd not synced: a
  command is being typed", "cwd not synced: a full-screen program runs",
  "cwd not synced: the profile does not follow the pane", or "pwsh
  exited with code 0" (shorter on the right: "not synced: typing", "not
  synced: a program runs", "not synced: profile"). The × at the end
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
  alternate screen: vim, less, a TUI would get the line), when the
  session's profile has `followsPane: false` (a program that is not a
  shell, such as the `claude` profile: the line would be its input, and
  Claude Code does not use the alternate screen, so the rule before would
  not catch it; the caption says "cwd not synced: the profile does not
  follow the pane"), when the shell is in that folder already, or when it
  ended. A new tab of such a profile starts with that caption, not with
  "synced", since it will never sync. Ctrl+Alt+P and Ctrl+Shift+Enter still
  type paths at such a prompt: that is the user's own request. A skipped sync is not tried
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
  chords such as Ctrl+K … stay in the shell, which needs them. Ctrl+Tab and Ctrl+Shift+Tab (`tab.next`,
  `tab.previous`) go to the window too, since no shell uses them, and the
  keyboard goes to the pane's tab that comes to the front; Ctrl+W and Ctrl+T
  stay with the shell (delete word, transpose; [keybindings.md](keybindings.md),
  "Tool pages and the terminal"). The page
  hands the window's keys over as messages, and a key that also reaches
  the window through XAML is ignored there, so none runs twice (unless the
  page never got it: next point). The palette opened from the terminal
  gives the keyboard back to it.
- **Handing the keyboard to the page.** XAML's focus on the terminal's
  WebView2 is not enough. WinUI moves the keys into a page's browser only
  when XAML's focus arrives while the page's controller is visible, and a
  dock shown a moment ago becomes visible to it only at the next frame
  (WinUI's `WebView2.HandleRendered`). When the focus came first, WinUI
  kept the move pending and never made it: XAML's focus sat on the page,
  Windows sent the keys to the window's own input window, and the window
  ignored them as the page's. The real-key runs of 2026-09-29 lost every
  key that way after Ctrl+P, in three of thirteen runs, each with the
  Markdown Preview open in the other pane, until a mouse click. Now,
  150 ms after it gives a page the keyboard (the terminal, and a tool's
  editor tab the same way), the window looks which window gets the keys
  (`PageKeyboard`, tested; `WindowsPlatform.KeyboardFocus`): Chromium's
  input window (`Chrome_WidgetWin_*`) means the page has them; WinUI's
  (`InputSiteWindowClass`) means it has not, and the window moves the
  focus away for a moment (to a shown pane, else to the close button of
  the page's own header) and back to the page, up to three times: WinUI
  makes the move only when XAML's focus arrives anew.
  A key that reaches the window while XAML's focus is on a page that
  does not have the keys is the window's to act on: the page's ways out
  (Ctrl+Shift+P, Ctrl+`, a `terminalFocus` binding) run, and any other
  key hands the keyboard to the page again. Hiding the terminal gives the
  keys back to the pane. Checked with real keys on 2026-09-30 (a copy of
  the live check without the scroll): after Ctrl+P, with the preview open
  in the other pane, the first check found the keys in the window
  (`InputSiteWindowClass`), the second hand-over reached the page, and
  Ctrl+` then reached the window.
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
  WebView2 process failed" with the kind and reason. Target
  `cabinetos_ui::shell`: "a page has the keyboard" with the page and the
  hand-overs it took, "a page did not get the keyboard; handing it over
  again" with the window that had the keys, "a page did not get the
  keyboard" (a warning, after three), "a key the page did not get" with
  the key and the command it ran, and "keyboard owner" (XAML's focused
  element and the page or window that gets the keys) 300 ms after the
  terminal hides and at the snapshot aid's `focus:` step.

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
  `view.toggleTerminal`, as the terminal does, and the tab keys (`tab.next`,
  `tab.previous`, `tab.close`, `tab.new`, `tab.select`: Ctrl+Tab, Ctrl+Shift+Tab,
  Ctrl+W, Ctrl+T, Ctrl+1 to 9). A tab key from a page in a pane's tab changes
  that pane's tabs, also when the other pane is the active one, and the keyboard
  goes to the tab that comes to the front (a folder tab's list, or the tool's
  page); from the sidebar's page it changes the active pane's tabs. Tab and Esc
  stay the page's. Until 2026-10-01 the page kept the tab keys, and only the
  mouse left a preview for another tab ([keybindings.md](keybindings.md), "Tool
  pages and the terminal"). The palette opened from a
  tool's page (in a pane's tab or in the sidebar) gives the keyboard back
  to that page when it closes: on Esc, and after a chosen command that
  does not take the keyboard itself. A page that is gone or hidden by then
  sends it to the active pane. Until 2026-10-01 it always went to the
  active pane, even when the page sat in the other one.
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

## The activity rail and the sidebar

Phase 13. The rail layout (`ui.layout: rail`, a choice beside `classic` and
`right`; the default stays `classic`) puts a column of buttons at the
window's left edge, and the sidebar becomes a host that shows one view at a
time ([design/README.md](design/README.md), "Activity rail"). The classic
and right layouts are not changed: no rail, no folder tree, no divider, the
sidebar as it was (pinned folders and drives). Article 4 (a user who never
sets the layout never sees the rail), Article 7 (each view has a command and
a key), Articles 10 and 11 (the window's own views are Explorer and Search;
every other view is a Tool Extension's page).

The decisions live in Core, with tests (`ui/CabinetOS.Core/Sidebar/`):
`RailModel` (buttons, order, clicks, badges), `SidebarSizing` (the divider),
`WarmPages` (which hidden web page stays awake) and `FolderTreeModel` (the
tree). The window (`MainWindow.Rail.cs`, `MainWindow.SidebarTools.cs`,
`Views/ActivityRail`, `Views/FolderTreeView`, `Views/SearchPanel`) lays them
onto XAML.

### The rail

- A 44 px column with 36 px buttons, 4 px apart, and a 3 px accent pill on
  the button whose view or panel is on show; its glyph is white then, and
  dimmer on the others. The buttons are Explorer, Search, Marketplace and
  Terminal, then one for each installed tool with `"sidebar": true` (the
  first two letters of its name on it; the name is the tooltip).
- **A click** on the button of the view on show closes the sidebar
  (`ui.sidebar` false); a click on another view's button shows that view,
  opens the sidebar if it was closed, and puts the keyboard into the view
  (the tree, the search field, or the tool's page). A click never leaves
  the keyboard on the button itself (the chrome rule, "The top row"): when
  no view takes it, it stays in the pane. Marketplace and
  Terminal run `marketplace.browse` and `view.toggleTerminal`; while the
  marketplace is open, a view button brings the panes back first, as the
  design's Explorer button does.
- **The order** is `ui.rail` (a list of button IDs; empty is the default
  order). Up and Down walk the buttons; Shift+Up and Shift+Down on a
  focused button move it. The keyboard reaches a button by Tab (the buttons
  are tab stops) or from assistive technology (UI Automation's `SetFocus`),
  not by a click. IDs the list does not name follow in the default
  order, so a newly installed tool's button appears at the end. A tool
  whose ID is `explorer`, `search`, `marketplace` or `terminal` gets no
  button: those IDs are the window's.
- **Badges.** A plugin event `badge` with `{ "view": "<button ID>", "kind":
  "dot" | "spinner" | null }` marks a button: a dot, a spinner, or none.
  The view is a button's ID (`explorer`, `search`, `marketplace`,
  `terminal` or a tool's ID). At most 64 badges are kept.

### The sidebar

- One column shows one view: **Explorer**, **Search**, or the page of a tool.
  The native views (Explorer, Search) are kept alive while hidden, so the
  tree and the hits are as they were when the view comes back.
- **Width.** The design's `clamp(180 px, 20 %, 224 px)` until the user
  drags the divider (the 8 px gap between the sidebar and the panes; an
  accent line shows under the pointer). The width follows the pointer, at
  most half the window and 480 px. **Under 150 px** the column fades
  while the pointer is there, and letting go closes the sidebar
  (`ui.sidebar` false) and keeps the last width above 150 for the next time
  it opens. The width is `ui.sidebarWidth` (whole pixels, `null`: the
  design's). `Ctrl+B` (`view.toggleSidebar`) works as before.
- **What is saved**, through `set_value`: `ui.sidebarView` (the view shown
  last: `explorer`, `search` or a tool's ID; the Explorer shows for an ID
  that is not installed), `ui.sidebarWidth`, `ui.rail`, and `ui.sidebar`
  as before. `ui.sidebarAutoReveal` (default `true`) is read only: the
  Explorer follows the active pane's folder or does not. A configuration
  the core sends while a write of the window's own is on its way does not
  undo it. The core keeps these four in its strict schema
  ([config.md](config.md)).

### Explorer

Pinned and Drives as before, and under them **FOLDERS**: the drives, and
under each the folders the user opened.

- **Lazy.** A drive's or folder's sub-folders are read when its row opens:
  one `list_directory` request for each open row (`watch` off, sorted by
  name whatever the panes are sorted by, hidden entries as
  `panes.showHidden` says, except for the folder on the active pane's path:
  see "It follows the active pane"). The names are read from the shared-memory
  listing off the UI thread and the listing is closed at once. A row that
  closes before the answer comes abandons its request, and so do the rows
  under it (`CoreClient` closes a listing that arrives for nobody). Opening
  a row again shows what it had at once and reads the folder once more; the
  rows that were open under it stay open, a new folder gets a row, a folder
  that is gone loses it. A folder with thousands of sub-folders opens with
  one change of the list, not one for each row. A folder the core refuses
  stays closed and its tooltip says why.
- **It follows the active pane** (`ui.sidebarAutoReveal`): each time the
  active pane's folder, or the active pane, changes, the tree opens the
  folders down to it (rows beside the path are not touched, and no row is
  closed), marks its row with the pill and the fill, and scrolls to it. A
  folder made after the last read is found by reading its parent once more;
  a path on no drive (a network path) marks nothing. A newer reveal stops an
  older one that still waits for a folder. With `panes.showHidden` off the
  tree still opens a hidden folder that lies on the pane's path, drawn dim
  (`IsHidden`, 0.55 opacity), so it can always follow the pane (Article 4:
  nothing is hidden from a user who is already inside the folder); it reads
  the parent once more with the hidden entries to find it
  (`IFolderSource.ListIncludingHiddenAsync`), shows no other hidden folder,
  and drops the hidden row, with the rows under it, when the pane leaves its
  path. The tree does this only while
  the Explorer shows. The scroll waits until the tree is laid out in the
  sidebar's window, and the row is laid out before it is brought into view
  (`FolderTreeView.ScrollTo`). Asked earlier, at the start of the window, the
  list drew its rows around the folder, far from the window, and FOLDERS
  showed none until Ctrl+Shift+E (found with real keys, 2026-09-30). The log's
  line "the tree drew rows" says how many rows are in the model, drawn, and
  inside the window (`visible`). The look is made 600 ms after the scroll;
  one that finds no row inside the window is made again every 200 ms for
  10 s, and only the last is logged (`looked_after_ms` says when), since a
  machine under load lays the list out late. The end-to-end test
  `The_tree_has_rows_on_the_screen_when_the_window_starts_in_the_rail_layout_before_any_key`
  starts the window as a user does, without the snapshot aid (which waits
  until the window is ready and would not show this) and reads it.
- **The lock** button in the header of FOLDERS (`sidebar.lock`, no default
  key) stops the following, and shows in the accent colour while it holds;
  the status bar says so. **Locate** (`sidebar.locate`, `Alt+Shift+L`, and
  the button beside the lock) shows the Explorer, reveals the active
  folder now, locked or not, and puts the keyboard on its row.
- **Going there.** A click on a row, or Enter on the cursor row, runs
  `go.toPath` in the active pane, once: the window listens to the sidebar's
  `Navigate` event only, which re-raises the tree's (a second subscription
  to the tree's own event ran it twice until 2026-10-01; the window test
  `Every_way_of_picking_a_folder_in_the_sidebar_runs_go_toPath_once` counts the runs
  for a drive, a pinned folder, a click and Enter). The chevron opens or
  closes the row.
- **Keys** while the tree has the keyboard: Up, Down, PageUp, PageDown,
  Home and End move the cursor (an outline on the row; it is shown only
  while the tree has the keyboard); Right opens the row, and on an open
  one goes to its first folder; Left closes it, and on a closed one goes to
  the folder above; Space opens or closes; Enter goes there; Esc gives the
  keyboard back to the pane.

### Search

The view holds the search field, its "Whole volume" box and the hits
([Search](#search) above); since Phase 16 removed the command bar's field it
is the only one. Typing searches (a 150 ms wait), the pane shows the same
hits, Esc leaves the search. The classic and right layouts have no rail:
there Ctrl+Shift+F shows the view in the sidebar's place until the search
is left (Esc, a hit chosen, or Ctrl+Shift+E). The list shows each hit's name and folder; a click or Enter on
one goes to it in the active pane with the hit selected, as Enter on a hit in
the pane does. Down in the field moves to the first hit.

The field is a text box, so it keeps the keys that type or edit, and a key
that types nothing runs its binding ([keybindings.md](keybindings.md),
"Contexts"): Ctrl+Shift+E shows the Explorer from the field. Until
2026-10-01 it did nothing there. Esc leaves the field. The
window takes Esc before the field's own handler sees it, so `overlay.close`
does the work: in the rail or in the sidebar (a rail button, the tree, the
field, a hit) it gives the keyboard to the pane and ends a search that is
running, and writes "Esc gave the keyboard from the rail layout's sidebar
back to the pane" into the log. The tree's Esc and the field's Esc are the
same.

### Tool pages

A tool with `"sidebar": true` in its `tool.json`
([tool-extensions.md](tool-extensions.md), "The sidebar page") has a button
and a page. The page is the tool's `entry`, in a WebView2 of its own (its
own browser process, data folder `sidebar-<id>`), started when its button
is first pressed, with no file: it gets `ready` and `context`, never
`open`. It gets the window's keys and the panes' context like a pane's
tool, and its commands run through the same check. The keyboard goes into
the page through `GiveKeysToPage` (never `WebView2.Focus`), with the same
hand-over check as a pane's tool.

- **Warm and suspended.** The page hidden last stays awake; the pages hidden
  before it are suspended with `CoreWebView2.TrySuspendAsync` (the frame is
  collapsed first, and the call waits 300 ms for WebView2 to hide the
  page), and wake with `Resume` when their button is pressed (`WarmPages`,
  tested). Closing the sidebar hides the page on show, which then becomes
  the warm one. A suspended page gets no messages until it wakes; the
  window then sends the context again.
- **When its process ends** the sidebar covers the page with "{name}
  stopped" and a Reload button, and the window goes on.

### Commands

| Command | Default key | What it does |
|---|---|---|
| `view.showExplorer` | `Ctrl+Shift+E` | Shows the Explorer and puts the keyboard in the tree. Without the rail it opens the sidebar and puts the keyboard on its first folder. |
| `view.showSearch` | `Ctrl+Shift+F` | Shows the Search view and selects its field. Without the rail it is `search.focus`. |
| `view.toggleSidebar` | `Ctrl+B` | As before. |
| `sidebar.locate` | `Alt+Shift+L` | Reveals the active folder in the tree. Without the rail the status bar says the tree is in the rail layout. |
| `sidebar.lock` | none | Locks or unlocks the tree. Same without the rail. |

### Snapshot steps and checks

The snapshot aid has `rail:<id>` (presses a button), `rail-move:<id>|<1 or
-1>`, `divider:<pixels>` (drags the divider to that width and lets go),
`tree:<path>` (opens the folder in the tree and the ones on the way),
`tree-state:<label>|<folder>` (one log line, "tree state", about one folder
of the tree) and `rail-state:<label>`, which writes one log line, "rail
state": the layout,
whether the rail, the tree and the divider show, the sidebar's width and
view, the buttons in order, the ones wearing the pill, the badges, the
Search view's text and hits, and the tree's rows, requests and current
folder. `RailEndToEndTests` runs a window in each of the three layouts on
the real core (the rail layout, the two others without a rail, a tree or a
divider, and the same 224 px sidebar in a 1400 px window), and one in the
rail layout that switches views, starts two tool pages, checks that the
page hidden first is suspended and wakes, drags the divider to 320 px and
under 150 px, and starts a second window on the saved configuration, which
finds the width, the view and the order again. A pixel comparison of the
classic and right layouts against snapshots taken before this phase found
the same images: 19 and 11 pixels of 2.5 million differ, and two runs of
one build differ by 6.

The live check has the section "13: rail" (`ui/livecheck/livecheck.ps1`).
The layout is switched to `rail` in the run's configuration (the sections
before it run in the classic layout, and the one after it gets it back), and
a test tool with a sidebar page (`ui/livecheck/fixtures/quick-notes`) is in
the run's own tools folder. Real keys and the mouse then check:

- the rail's five buttons (found with UI Automation) are 36 px square, 40 px
  apart and 12 px in from the window's edge;
- Ctrl+Shift+F shows the Search view, a query finds a file two folders down,
  Down goes to the hit and Enter takes the pane there; Ctrl+Shift+E shows the
  Explorer with the tree opened down to the pane's folder;
- in the tree: Down and Enter go to a folder, Right opens it and goes into it,
  Esc gives the keyboard back to the pane (the window says so in its log);
- "Lock Folder Tree" from the palette keeps the tree where it is while the
  pane goes to another folder, and Alt+Shift+L finds it again;
- the mouse on the Quick Notes button starts the tool's page once and shows
  it; Ctrl+Shift+F, Esc and Ctrl+Shift+E from the page reach the window, and
  the toggle key closes the sidebar and gives the keyboard to the pane;
- the mouse on the Marketplace button opens it, and a second click closes it;
  the mouse on the active Explorer button closes the sidebar;
- Shift+Down on the Explorer button, focused through UI Automation as
  assistive technology does (a click leaves the keyboard in the pane), moves
  it, Shift+Up moves it back (`ui.rail` holds the order, then an empty
  list);
- the divider dragged to 300 px is `ui.sidebarWidth`; dragged under 150 px it
  closes the sidebar (`ui.sidebar` false) and keeps 300; the toggle key opens
  it at 300;
- the log has no warning or error line during the section.

The check leaves the Search view's field with Esc before Ctrl+Shift+E. It
was written when a text box kept every key but the immutable tier's; Ctrl+Shift+E
works from the field since 2026-10-01, and the Esc does no harm.

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
  check on the theme in effect. Up, Down, Home and End move the highlight,
  and so does the pointer resting on a row. The window shows the
  highlighted theme live, as a preview: 80 ms after the highlight stops,
  the picker asks the core for that theme with `get_theme` and paints it
  as it would apply it (colours, Mica, sizes, the terminal). Nothing is
  written: `cabinetos.json` and the theme in effect stay as they were.
  Highlighting the checked row paints the theme in effect again, with no
  request. A reply for a row the highlight has already left is dropped,
  so a pointer that sweeps down the list sends one request, not ten.
  Enter or a click applies the highlighted theme with `set_value
  ui.theme`. The picker closes once the core accepts, and `theme_changed`
  then makes the preview the theme in effect, so the window does not
  flash back in between. A refusal shows in red in the footer, and the
  preview stays. Esc or a click outside closes the picker and paints the
  theme in effect again at once; so does the palette or the marketplace
  opening over it. A pointer that leaves the list keeps the highlight and
  its preview, as in VS Code. The picker's own rows keep the sizes they
  had when it opened: a density preset previewed changes the rest of the
  window, but a row that shrank under the pointer would put another row
  under it. A `theme_changed` from elsewhere while the picker is open
  becomes the theme in effect, and the check moves to it.
  The tints come with `list_themes` (protocol 11), so opening it is one
  request; with an older core every swatch shows plain Mica. An open
  picker draws its swatches again when Windows' accent or mode changes.
- **A theme that cannot be used.** The core checks every theme before it
  sends one. If a colour still cannot be read, the mapper names its key in
  the log and nothing of that theme is applied; the last theme stays.
- **Logs.** Target `cabinetos_ui::theme`: "theme applied" with the ID, the
  kind, the mode it was drawn in (`light` or `dark`), the accent, and the
  Mica tint and opacity; "theme chosen"; "theme previewed" with the ID
  and the mode when the picker's preview is painted; "theme restored"
  with the ID and the mode when the theme in effect is painted back after
  a preview; "a theme token could not be set" (once per token) if a brush
  refuses a colour.

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
([design/COMPACT_THEME.md](design/COMPACT_THEME.md),
"Scope"). Constitution Articles 3, 6 and 8.

- **How.** `MetricsMapper` (in `CabinetOS.Core`, tested) is one table:
  every metric's name, unit and bounds, and the default look's value. A
  test holds the table equal to `sdk/themes/theme.schema.json`: the same
  88 names, the same bounds, and the default that each name's description
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
  the row height, the text size, the top row's, the breadcrumb row's and
  the tab strip's heights, the three chrome switches, and the names it
  ignored.

### Where each metric goes

| Metrics | What they size in the window |
|---|---|
| `fontSize` | The base text: names in the file lists, the sidebar's rows, the breadcrumb row's text box, the Search view's field, the rows of the palette, the menus and the prompts, and the rename box. The default look draws the design's 13 px; before this, these texts were WinUI's own 14 px. |
| `lineHeight` | Wrapped text: a pane's message ("This folder is empty.") and the search note. Single lines sit in rows of a fixed height, so it does not change them. |
| `backdropOpacity` | Mica. At 0.86 and below, plain Mica (the default look). Above it, Mica's own base colour is laid over Mica, up to opaque at 1: Commander Compact's 0.94 is a tint opacity of 0.91 in dark mode. A theme with a `mica` tint of its own keeps it. |
| `radiusControl` | Buttons, fields, the rows of the palette and the menus, the terminal's and the editor's buttons, and the marketplace's search field and buttons. WinUI's own controls made from then on take it too (`ControlCornerRadius`). |
| `radiusSurface` | The panes, the editor, the terminal and the marketplace. Icon tiles and info boxes (the app tile, the marketplace's tiles and stat boxes) keep the design's radius in proportion to it, and never less than `radiusControl`. |
| `gap`, `bodyPadding` | The space between the sidebar and the panes, between the two panes, and between the panes and the terminal, which is also the splitter; with no gap, the splitter keeps a 6 px handle laid over the edges it joins. The space at the window's sides and bottom. |
| `topRowHeight`, `topRowButtonSize` | The top row, never lower than 32 px: Windows draws the minimize, maximize and close buttons that high; its menu and view buttons ("The shell"). |
| `workspacePillHeight`, `workspacePillRadius`, `commandCenterHeight`, `commandCenterRadius` | The workspace pill and the command center. |
| `hairlineOpacity` | The 1 px lines under the top row, the tab strips and the breadcrumb rows, and between tabs. |
| `tabFontSize`, `tabRadius` | The tabs of a pane's strip. |
| `tabRow` | The height of a pane's tab strip (32 px; 24 px in Commander Compact). It shows from the first tab ("Tabs"). |
| `breadcrumbRowHeight`, `navButtonSize` | A pane's breadcrumb row and its Back, Forward and Up. |
| `dropdownRowHeight` | The rows of the top row's menu and the workspace dropdown. |
| `titleBarHeight`, `tabHeight`, `tabPaddingX`, `tabMinWidth`, `commandBarHeight`, `iconButtonSize`, `fieldHeight`, `toggleHeight`, `paneHeaderHeight` | Nothing since Phase 16: the title bar, its workspace tab, the command bar and the pane's header are gone. A theme may still set them, so themes written before stay valid. |
| `captionButtonWidth` | Nothing: Windows draws the caption buttons 46 px wide, and a window cannot change that. |
| `sidebarMinWidth`, `sidebarWidthPercent`, `sidebarMaxWidth` | The sidebar's width. |
| `sidebarHeaderFontSize`, `sidebarHeaderPaddingTop`, `sidebarHeaderPaddingX`, `sidebarHeaderPaddingBottom` | The PINNED and DRIVES labels. The first label sits 8 px higher, as the design's first one does. |
| `sidebarRowHeight`, `sidebarRowInset`, `sidebarRowRadius` | The pinned folders' rows, and the inset and corners of every sidebar row. The default look's pinned rows are 32 px; its 34 px are for workspace rows, which do not exist yet. Between the rows, and above and below the sections, the space follows the inset. |
| `selectionBarWidth` | The accent bar of a selected or highlighted row: in the sidebar, the file lists, the palette, the prompts and the theme picker. |
| `driveRowPaddingY`, `driveRowPaddingX` | The drive rows. The pinned rows take the same side padding. |
| `tagRadius`, `tagFontSize` | Nothing yet: the sidebar has no tags. |
| `columnHeaderPaddingY`, `columnHeaderPaddingX` | A pane's column headers. |
| `nameColumnWeight`, `modifiedColumnWeight`, `typeColumnWeight`, `nameColumnMinWidth`, `sizeColumnWidth`, `columnGap` | The columns, in the column headers and in every row alike. The default look keeps 8 px after the Modified and Type texts; a theme that sets `columnGap` spaces the columns by it instead. Once the user drags or fits a column, the user's widths (`ui.columns`) win over the three weights and `sizeColumnWidth`; `nameColumnMinWidth` and `columnGap` still apply ("Column widths"). |
| `columnViewWidth` | Each column of the column view ("The column view"). |
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
  cards. (The top row, the tab strips and the breadcrumb rows always have
  their fill and their line, at `hairlineOpacity`, since Phase 16.) The
  sidebar has a line on its right (.08). With no gap, the two
  panes' borders overlap into one line. The column headers
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

- Since Phase 16 the top row takes the place of the page's title bar and
  command bar: 32 px high in Commander Compact, not 30, and its caption
  buttons are 46 px wide, not 40: Windows draws them.
- The workspace pill shows one workspace, "Default"; the page has three
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
- A pane has no header since Phase 16, so no list button at its right end.
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
  (`marketplace.browse`, Ctrl+Shift+X) or the top row's store button
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
- **Cards in parts** (since 2026-10-01, the speed review's proposal C). A
  new set of items (the index read, a tab, a search) makes at once only
  the cards that fill the view: the grid's columns times the rows down to
  the view's bottom, with the height of a card laid out, else of the
  design's card. The rest come a screenful at a time, one slice per turn
  of the window's dispatcher at low priority, so input and frames come
  between them (`CardSlices` in `CabinetOS.Core`). They are made in the
  index's order, so Tab still reaches them in that order. A new set
  started while slices are still to come stops them: a generation number
  tells a slice of the old set from the new one's. A new set starts at
  the top of the grid; the index read again (Refresh, the same items in
  the same order) keeps the grid where it was scrolled, and the cards down
  to there are made at once. A card made before is kept while its item
  stays the same object, so a tab or a search that brings an item back
  reuses its card. When the index comes before the view's own first
  layout (the marketplace was shown a moment ago), the cards wait for the
  frame after it: the view's layout and the cards then do not share one
  frame, and the view's size says how many cards fill it. Measured on
  2026-10-01 with `speed-review.ps1` (scenario `market`, 50 items, 7
  paired runs): 50 cards in one go made a frame of 98 to 128 ms; now the
  first screenful (15 cards in a 1400 × 900 window) is made in 12 to 16 ms
  and no frame of the cards is longer than about 40 ms. The longest frame
  of a first opening was then the view's own first layout, 54 to 81 ms,
  before any card exists; a second opening has no frame over 33 ms
  ([the report](log/2026-10-01/speed-items-ce-report.md)). That layout is
  now done ahead (the next point).
- **The view prepared ahead** (since 2026-10-01, the follow-up to
  proposal C). The window makes and lays out the marketplace view once,
  hidden, while it is idle after start: in the idle slot of the context
  menu shapes (the context menu's **Speed**), one low-priority dispatcher
  turn after the last shape, so never before the first folders are on
  screen. It runs only after a second with no key, click, pointer move or
  wheel over the window (`InputQuiet` in `CabinetOS.Core`); input in that
  second puts it off until a quiet second follows, so it does not take a
  frame from someone who scrolls or types. Input inside a tool's web view
  (WebView2) does not reach the window and does not count. It makes the
  nav, the toolbar, the notice, the cards' scroller and one sample card (an
  item that names no real extension), lays them out in that one turn, and
  hides the view again before the next frame: nothing is drawn, the index
  is not read (the core reads it at the first look only), and no card of
  the index is made. It takes about 20 ms in a Release build and fits in
  one frame. "marketplace view prepared" in the log, with `ms`. The sizes
  the hidden view kept may be another window size's, so an opening counts
  its first cards only after its own first layout. Measured on 2026-10-01
  with `speed-review.ps1` (scenario `market`, 7 paired runs against the
  build before it): the opening's longest frame went from 51.6 ms (44.6 to
  63.6) to 42.4 ms (37.8 to 47.7), and the first 15 cards are made in 8 ms
  instead of 12. One or two frames of 34 to 48 ms stay: the opening's own
  turn (the command, the panes hidden, the view shown, the index's reply)
  and the frame that lays out and draws the first 15 real cards for the
  first time ([the report](log/2026-10-01/speed-items-ce-report.md),
  "Follow-up").
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
  - "The marketplace index setting is stale.", for the old placeholder
    address (a host ending in `.invalid`) that a file written before the
    public index existed may hold, with the advice to remove the line so
    the public index is used ([marketplace.md](marketplace.md), "The
    public index");
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
  "marketplace cards shown" at the first frame after an opening's first
  cards were laid out (`cards` made at once, the set's `total`, and `ms`
  since the opening), "marketplace cards complete" at the first frame
  after the last slice of a set was laid out (`cards`, `slices`, and `ms`
  since the opening, or since the set started when it was not an
  opening's); both have `make_ms`, the time spent making the cards (the
  first slice's, all slices'), "marketplace view prepared" once a run,
  while the window is idle after start (`ms`, the time the preparation
  took), "marketplace index read" with the source and the number of items,
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

Tests of the cards in parts: `CardSlicesTests` (an index of 120 items made
a screenful at once and the rest in slices, in the index's order; a tab
chosen during the slices leaves only its cards; a screen's columns and
rows) and `MarketplaceCardsEndToEndTests` (with `CABINETOS_UI_E2E=1`: a
real window on a local index of 120 items logs its first screenful, then
all 120 in the index's order, and Tab goes from a card to the next item's;
the Themes tab clicked while Discover's slices are still to come leaves the
40 themes only; the view is prepared after start, after the first folders
and the menu shapes, before the marketplace's command and without asking
the core for the index, and the first opening afterwards still logs its
first screenful and then all 120 in order). `InputQuietTests`: the quiet
second before the preparation, put off again by each key or pointer event.

## Updates

In-app updates (Phase 17, [ADR 0014](decisions/0014-in-app-updates.md)).
The core does the work: it reads the channel's `latest.json`, downloads
the release's zip, checks its SHA-256, unpacks it, and swaps it into the
install folder ([ipc.md](ipc.md), "Updates"; [release.md](release.md),
"Updates"). The window shows where the updater is, shows the release
notes, and restarts itself into the new version. It reads no file and
fetches nothing itself (brief §1): the notes come in `update_state`. The
rules below are in `CabinetOS.Core.Updates` (`UpdateModel`, `UpdateText`,
`ReleaseNotes`), with tests; the window's part is `MainWindow.Update.cs`
and `Views/UpdateDialog.cs`.

- **The state.** After every start of a core the window asks
  `update_status`, which the core answers from memory; then it follows
  `update_state_changed` and `update_progress`. A core before protocol 14
  answers `unknown_request`: the window then shows no pill and no dot, and
  the commands say that updates need a newer core.
- **The commands** (category Update, target `ui`, no default keys: the
  palette, the menu and the pill are their places):

  | Command | What it does |
  |---|---|
  | `update.check` "Check for Updates" | Asks `update_check`. A newer version downloads at once (`update_download`) with the pill, and its dialog opens when the download is complete, even after Later, because the user asked just now. Otherwise a notice: "CabinetOS 0.1.0 is the newest version on the stable channel.", why a development build or an all-users install does not update itself, or why the check failed. While a version waits, it opens that version's dialog instead. |
  | `update.apply` "Restart to Update" | Swaps the downloaded version in (`update_apply`), then restarts. A swap done already (state `ready`, after `cabinetos-cli update apply` or in another window) restarts at once. It refuses while a transfer runs, because the core stops with the window and the transfer with it. |
  | `update.rollback` "Roll Back to the Previous Version" | Asks first (Cancel is the default button), then `update_rollback`, then restarts into the version kept in `previous\`. |
  | `update.showNotes` "Show Release Notes" | Opens the dialog of the newest version the core knows, at any time. |

- **The pill** in the status bar, in the transfer pill's style: while a
  download runs, the 80 × 4 px track and "Update · 45% · 4.2 MB/s" (the
  tooltip has the bytes, "34.3 MB of 76.3 MB"); "Update · installing"
  during the swap; "Update ready · Restart" while a version waits, and a
  click on it runs `update.apply`. No pill in any other state.
- **The dialog** (`UpdateDialog`, a ContentDialog like the others, so no
  command runs while it is open): "CabinetOS 0.2.0 is ready", "You have
  0.1.0. Published 2026-10-01 on the stable channel.", the notes in a
  scrolling area of at most 360 px, a "Full changelog" link, and Restart
  now / Later. Before the download is complete it shows the notes with
  Close only. The notes are the CHANGELOG.md section the release script
  publishes as `notes-<version>.md`, rendered natively in a RichTextBlock
  (no WebView2): headings, bullet and numbered lists with nesting,
  paragraphs, fenced code, bold, italic, code spans and links. A relative
  link points into the repository at the version's tag
  (`https://github.com/OliverD25/cabinetos/blob/v<version>/`), where
  CHANGELOG.md sits at the root, and so does "Full changelog". Only
  `https:` and `http:` links can be clicked; any other link shows as its
  text. Underscores never mark emphasis, so a name such as `update_status`
  outside a code span stays as written. When the core could not read the
  notes, the dialog says so and links them.
- **The snooze rule.** The dialog opens by itself when a download is
  complete (`update_state_changed` with `downloaded`), once per version in
  a run, and not while `snoozed_until_ms` is still ahead. Later, Esc and
  every other way of closing the dialog of a downloaded version send
  `update_snooze`: no dialog by itself for a day. The pill, the dot and the
  commands stay. A dialog already open is not pushed aside.
- **The dot.** While a version waits (`downloaded`, or `ready` after a
  swap), the menu button carries a 6 px accent dot, the menu's Check for
  Updates becomes "Restart to Update (0.2.0)" with the dot
  (`ShellMenu.Build`), and About's "Update" row starts with the dot.
- **The restart.** The window closes the way the close button closes it:
  the tabs and the last folders are saved, and the core stops. Then it
  starts `CabinetOS.exe` in `install_dir` from `update_state`, without
  `CABINETOS_CORE_EXE` and the snapshot aid's variables, so the new window
  starts the new core next to it. That core confirms the swap at its start.
  Until then the old window, core and command line run on from
  `previous\`.

Checked by the tests: the notes of CHANGELOG.md's own Unreleased section
(every heading, item, link and code span), the dialog in each state, the
pill's texts, the snooze rule, the dot, the menu entry, About's row, and
the six requests, `update_state` and the two events against the schemas.
The end-to-end tests run against the release core with protocol 14, and
one of them (`ShellEndToEndTests`) runs the window with a copy of that
core in a folder with `release.json` 0.1.0: Check for Updates downloads
"0.2.0" from a local feed, the dialog opens by itself once, Esc snoozes
it, the pill and the dot stay, and nothing is swapped. The shell state
line logs `update_pill` and `update_dot` for such checks.

Checked once with the snapshot aid (2026-09-30): the dialog with the real
Unreleased section as its notes, the menu with "Restart to Update (0.2.0)"
and its dot, and About's row with the dot looked as described. A real
restart: the Debug window and the release core ran from a fake per-user
install; Restart to Update moved their files (the window's DLLs and
resources among them) into `previous\` while both ran, copied the new
ones in, closed the window and started the `CabinetOS.exe` in the install
folder, which came up with a core of its own. Not checked: real keys (the
GUI live check was not run in Phase 17), and a restart into a build whose
version really differs, so the new core's confirmation of the swap was not
seen in that run (the core's own tests cover it).

## Scrolling

Phase 5's goal: while PageDown is held in the 100,000-entry folder, no
frame takes more than 33 ms, and fewer than 5 % take more than 20 ms, on
a quiet machine. A held key repeats about 30 times a second, and each
PageDown shows a new page of rows (30 at the default window size). On a
laptop panel whose display path sleeps between pages the goal is judged by the
frames' UI work instead of their gaps (`-Panel`, see "The live check on another
machine").

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

**Only real keys give the true gaps.** The snapshot aid presses PageDown
at the start of a frame; a real key arrives at any moment between two
frames, and that difference was the whole problem of 2026-09-30 ("With
real keys" below). `ui/livecheck/scroll-keys.ps1` measures the scroll
with real keys: a fresh window on the bench folder, PageDown pressed 30
times a second for 5 s with `SendInput`, as the live check presses keys.
It prints the frame table the way `livecheck.ps1` reads it, the CPU time
of each thread of the window's process, the desktop window manager's CPU
and the GPU's use, and each frame of 33 ms or more from the first press
to 1 s after the last. It takes the keyboard and the mouse for about
15 s, so it needs an unlocked, awake screen that nobody uses:

```text
# PowerShell
powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\ui\livecheck\scroll-keys.ps1 -Label after -Fine [-Theme commander-compact] [-Heavy]
```

The `frame stats` line also carries the garbage collector's pauses in
the second (`gc_pause_ms`) and its collections by generation (`gcs`, as
`gen0/gen1/gen2`). Whenever the frame stats are on, each frame of 33 ms
or more gets a `slow frame` line: the gap, the UI-thread work
(`busy_ms`), the parts, the collector's pause since the frame before,
and after a pause the heap's size, what survived, the generation and
whether the collection ran in the background.

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

### With real keys

Measured on 2026-09-30 with `scroll-keys.ps1`: the display awake at
59 Hz, release builds, the bench folder, 30 presses a second. The full
report, with every experiment: [log/2026-09-30/scroll-gaps.md](log/2026-09-30/scroll-gaps.md).

Before the change, 27 to 32 % of the gaps were over 20 ms, but only 2 to
4 frames in 5 s had more than 16.7 ms of UI-thread work. The UI thread
worked 363 to 430 ms a second, and WinUI's frames held only 220 to
261 ms of it. The rest, about 150 ms a second, was the rows' layout run
outside a frame. The reason: a key message arrives at any moment between
two frames. The pane moved the cursor and scrolled at once, and WinUI
laid out the new page's rows right after, for 13 to 15 ms. When that
began late in the 16.7 ms between two frames, the next frame waited for
it.

CPU time per thread over a 5 s hold: the UI thread 260 to 340 ms a
second; .NET's tiered compilation (the runtime making faster code for
the methods it runs often) 150 to 190 ms, about 40 ms over a 20 s hold;
WinUI's composition thread in the process 60 to 100 ms. The desktop
window manager used 7 to 12 % of one processor, and the GPU's 3D engine
0.2 to 0.4 % for this window. The GPU and the compositor are not the
limit.

The frames over 33 ms held full garbage collections that paused the UI
thread for 17 to 25 ms, with a managed heap of only 6 to 8 MB. They were
induced: WinUI asked .NET for them; the window never asks for one. With
the row bound in its own `DataContextChanged`, a held PageDown had one
every 2 to 3 s.

### What changed

On 2026-09-30, for real keys (nothing on screen changes):

- **The cursor keys wait for the next frame** (`PendingCursorKeys`,
  `FilePane.ApplyCursorKeys`). Up, Down, PageUp and PageDown are queued
  and made in order when the next frame starts
  (`CompositionTarget.Rendering`), so a page's layout runs inside that
  frame. Anything else that reads the cursor makes the waiting keys
  first: the pane's other keys, a click, a command (the router's
  `Executing`), a plugin command's arguments, a typed letter of the
  quick search, and a new model in the pane. Alone, this took the gaps
  over 20 ms from 29 % to 0.3 to 1 %.
- **A row is bound when the repeater prepares it** (`FileRow.Show`,
  called from `ElementPrepared`), not in the row's own
  `DataContextChanged`. With that event, WinUI asked for a full
  collection every 2 to 3 s of scrolling; without it, none in a 20 s
  hold.
- **Rows are made ahead** (`RowFactory`, the pane's `IElementFactory`):
  two pages and four rows, made at the dispatcher's low priority in
  slices of 4 ms once the first row is shown. The first PageDown no
  longer makes a page of rows from the template in one frame of 60 to
  77 ms. The rows the repeater lets go wait in the factory for reuse.

Together, 20 s at 30 presses a second: 1,207 frames, none over 20 ms,
the worst 17.8 ms, 258 ms of UI-thread work a second. With heavy logging
on (5 s): none over 20 ms, the worst 17.6 ms.

Since 2026-10-01 the list keeps half a screen of rows made above and below
its view (`Repeater.VerticalCacheLength` 0.5 in `FilePane`; WinUI's default
is 2), in every theme. A change of density, Commander Compact's 20 px rows
against the default's 30, measures every made row again in one frame, and
fewer made rows cost less there (the speed review, finding 3). Measured
2026-10-01 with `speed-review.ps1`, 4 runs each way over 100,000 files and
3,000 entries, medians before and after: the frozen frame of the change to
Commander Compact 106 to 79 ms (its UI work 54 to 35 ms), of the change back
to Default 89 to 65 ms (27 to 16 ms of work); the scroll of the 100,000-file
folder, frames over 20 ms 5.2 to 2.5 %. What a fast drag of the scroll
bar's thumb shows is checked by the live check, which throws the thumb from
the top to the bottom over the 100,000-entry folder in Commander Compact and
takes a screenshot (`compact-scrollbar-drag-live.png`); the rows in it must
all have their texts.

On 2026-09-29, without keys:

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
- On 2026-09-30, with real keys: fewer rows kept outside the view (the
  repeater's overscan at 0.5 or 0 screens instead of 2), rows without
  their icon or their type column, the fixed-height layout again, the
  collector's `SustainedLowLatency` mode, and one full collection while
  idle after the rows ahead are made. With the old key path, the smaller
  overscan, the plain rows and the fixed layout left the gaps at 25.9 to
  29.1 % over 20 ms. The `SustainedLowLatency` mode did not stop the
  induced collections. With the three changes the gaps were already 0 %:
  a smaller overscan and simpler rows cut the work per second by up to
  17 %, the fixed layout made the gaps worse (9.6 %), and the idle
  collection gave no clear result.

### What remains

- About 15 ms of UI-thread work per page of 30 rows, nearly all of it in
  WinUI: making or recycling a row costs about 100 µs, laying out a text
  that changed about 38 µs (timed on the name), and drawing the rows
  that came into view about 5 ms per page. It fits in a frame now that
  it starts at one.
- **One induced full collection of 20 to 30 ms still comes now and
  then** near a hold's start or end. In 5 of 8 probe runs of the final
  build, one frame of 36 to 42 ms in the first second of the hold,
  before the seconds the table counts. In both live-check runs of
  2026-09-30, one frame just after the last press: 47.9 ms, and 36.7 ms
  350 ms after it with no UI work in it. `livecheck.ps1` counts that frame, because its
  table ends with the per-second line that ends up to 1 s after the
  hold; so its line said "met: no" with 0.3 % over 20 ms.
- **Commander Compact** (20 px rows, 48 a page): 11.6 to 18.8 % of the
  gaps over 20 ms, none over 33 ms, the worst 23 ms. A page of 48 rows
  takes more than 16.7 ms in more than half the frames that show one.
  Its first PageDown still takes one frame of 60 to 77 ms, 10 to 42 ms
  of it the rows' own measure; why the rows made ahead do not cover it
  there is not known yet. An overscan of 0.5 screens took it to 1.0 to
  6.3 % in three runs; it is in the build since 2026-10-01 (above).
- No ETW trace (Windows' own event recording, which would show where
  inside WinUI the time goes): `wpr` needs an elevated prompt. The
  display's refresh rate was not changed; it runs at 59 Hz.

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

- **The crumbs keep the drive, a "…", and the last folders** (checked
  2026-09-29 with the address bar's pixel fit, `CrumbFit`). Since Phase 16
  each pane's breadcrumb row counts parts instead (`Breadcrumbs`: 3 whole
  in dual mode, 5 in single), its "…" goes to the deepest folder it hides,
  and a row still too narrow scrolls to its end ("The breadcrumb row").
- **The transfer flyout's line keeps the drive (and until Phase 16 the
  pane header's path kept it too) and the last names around "…"**
  (`DisplayFormat.ShortPath`), never
  cutting a name: `C:\…\deep file.txt → C:\…\edge\names\Ґанок`. Before,
  the flyout cut the line's end, which lost the file and the destination.
  The whole text is in the tooltip. The crumbs and `ShortPath` read
  `\\?\C:\…` as the drive and `\\?\UNC\server\share\…` as the share.
- A breadcrumb row's text box (Ctrl+L) shows the whole path, selected and
  scrolled to its end, as the address box did.
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
  window to close writes `ui.lastPaths` and `ui.tabs`.
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
| Tools in the Tool Dock; a tool opened without a file, except its sidebar page | The dock holds the terminal; tools open as editor tabs, and a tool with a sidebar page ("The activity rail and the sidebar") has that one page without a file ([tool-extensions.md](tool-extensions.md), "Not yet") |
| Reattaching to shells after the UI restarts | The UI starts its own core, and the core closes its shells when it stops, so there is nothing to reattach to (`terminal_list` is ready for it) |
| Light-mode tokens from the design | The design has none yet; a `system` theme in light mode uses Windows 11's own light colours ("Themes") |
| Workspaces (the pill's list, a sidebar section) and Tags | One "Default" workspace, the active folder's repository; both sidebar sections stay hidden (Article 4) |
| Sorting by a click on a column heading | A single click on a heading does nothing yet (a double-click fits its column, "Column widths"); Ctrl+F3 to Ctrl+F6 sort a pane ("A pane's order"), and sub-phase 11c brings the headings |
| Pasting files copied in Explorer, drag and drop | The in-app clipboard only |
| Dragging tabs to reorder them or to the other pane | `tab.moveToOtherPane` does the work, and the keyboard is complete (Article 7); the "+" came with Phase 16 |
| Rows that plugins add to the top row's menu | The registry has no mark for "in this menu" yet ("The shell") |
| The metrics `captionButtonWidth`, `tagRadius`, `tagFontSize`, `markdownPaddingY`, `markdownPaddingX`, `markdownLineHeight`, `hexRowHeight` and `hexColumnGap`, and the nine of the shell before Phase 16 | Windows draws the caption buttons; there are no tags yet, the tool messages carry no sizes, there is no hex view, and the title bar, the command bar and the pane's header are gone ("Metrics and chrome") |

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
- **WinUI's clear button (×).** The palette's input, Quick Open's box and
  the Search view's field show it while they have the keyboard; the design
  has none there. The rename box hides it, since a click on it would end
  the edit, and so does the find widget's box, whose own close button
  stands beside it.
- **Esc and the transfer flyout.** The flyout never takes the keyboard, so
  Esc reaches it only last, after everything else Esc closes.
