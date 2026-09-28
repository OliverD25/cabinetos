## Phase 5 report

Context: CabinetOS Phase 5, the WinUI 3 shell v1 (design views A and B). I built it in the worktree `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.claude\worktrees\agent-a31a3838893f73dc5` and pushed it to `origin/main`.

Short version: everything is built, tested and pushed. CI is green for both the `ui` job and the `core` job. The live check is only partly done. The PC has been locked since about 15:07 local time, and on a locked screen key presses, mouse clicks and real screenshots do not work. I checked everything that works with the screen locked. The rest needs about 2 minutes on an unlocked PC, and the script for it is ready (see "Needs the user").

### Built
- `ui/CabinetOS.sln` has three projects. All target `net10.0-windows10.0.22621.0` and x64, treat warnings as errors, and take package versions from one file (Central Package Management).
  - `ui/CabinetOS.Core` runs without a window, so it is unit-tested. It contains:
    - Diagnostics: `ui.<date>.jsonl` in the core's format with `boundary: "frontend"`, a bounded lock-free queue with a background writer, a ring of the last 256 lines, and `crash-<ts>.json` traces.
    - The protocol v7 types, with source-generated System.Text.Json. Tests check them against `sdk/protocol/*.schema.json` and `sdk/config/cabinetos.schema.json`.
    - The pipe client: 4-byte length framing, a 16 MiB limit, ULID request IDs, events told apart from replies, and section handles wrapped the moment they arrive.
    - The core launcher: find the core, start it with `--pipe` and `--parent-pid`, connect, say `hello`, shut it down.
    - `ListingView`, the shared-memory reader. It is the only hand-written `unsafe` C#. It checks the header first: magic, version 2, bounds and alignment.
    - Keys, the keymap and the chord state machine: a 1000 ms window, the contexts `filesView`, `paletteOpen` and `textInput`, and the Immutable System Tier.
    - `CommandRouter`: UI handlers run commands with `target: ui`, and every other command goes to the core as `execute_command`. There are also a few UI-only commands. Each run gets its own ULID. The command list is read again after a keymap, config or plugin event.
    - Display formats, and the settings the window reads.
  - `ui/CabinetOS` builds `CabinetOS.exe`: unpackaged, with a custom `Program.Main`.
    - Design view A:
      - Mica, with the content drawn into the title bar and one static "Default" tab.
      - Back, forward and up; breadcrumbs that turn into an address box (Ctrl+L or a click); a disabled search box; the dual/single toggle; the sidebar toggle; the ⋮ palette button.
      - A sidebar with Pinned (Desktop, Downloads, Documents, the profile folder) and Drives. Drives stays hidden until the core answers `list_volumes`.
      - Two panes of virtualized `ItemsRepeater` rows (Name, Modified, Type, Size; 30 px per row), with an accent bar on the active pane.
      - A status bar: item count, selection, the waiting-chord message, notices, and the Ctrl+Shift+P keycap.
    - Design view B, the command palette:
      - A scrim, a 640 px panel with in-app Acrylic, and the design's 160 ms entrance.
      - The core searches, 30 ms after the last key. Each row shows its keys and chords, and the Immutable System Tier shows a lock.
      - The pencil or F2 records new keys, which go to the core as `set_keybinding`. A refusal is shown in the row.
    - Part D: the sidebar sends `list_volumes`. On `unknown_request` it hides Drives and logs one INFO line. I saw this line in the logs.
  - `ui/CabinetOS.Tests`: 158 xunit v3 tests. One of them is an end-to-end test against the real `cabinetos-core.exe`.
- CI: a `ui` job in `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.github\workflows\ci.yml`.
- Docs:
  - `docs/ui.md` (new).
  - The "Phase 5" section in `docs/dev-setup.md`, with exact versions and licenses.
  - A change-log row in `docs/ARCHITECTURE.md`.
  - The documents-table row in `README.md`.
  - `ui/README.md`.
- Not created: `sdk/csharp/`. The task says it is not a project, so the protocol types live in `ui/CabinetOS.Core/Protocol`.

### Commits (all on origin/main)
- 4d6d403 ui: the half of the shell that runs without a window
- 329f4c7 ui: the window, design views A and B
- d6b4d8c ui: a new folder starts at its top, and the palette keeps its keys
- ccf1cdc ci: build and test the WinUI shell on every change to ui/
- dd60ed0 docs: the WinUI shell, how to run it, and what it was built with
- e687679 ui: a crash in an async handler leaves a trace; start errors reach the dialog
- 9cfb94a ui: the router reads its command list again when keys, config or plugins change
- a64b385 ui: snapshots follow a list of steps, so any view can be checked with the screen locked
- 34ef8c6 docs: the shell's measured start, listing and close times

Nothing under `core/` changed. My commits touch only `ui/**`, `docs/ui.md`, `docs/dev-setup.md`, `docs/ARCHITECTURE.md`, `README.md` and `.github/workflows/ci.yml`.

### Checks
- Build: `dotnet build CabinetOS.sln -warnaserror`, Debug and Release, gave 0 warnings and 0 errors. The last run was at origin/main 0a14bee, with the newest schemas.
- Tests on this PC: 158 passed, 0 skipped. The end-to-end test ran against `core/target/debug`.
- CI run 36426544858 on a64b385: WinUI shell ✓ 2m0s, Rust core ✓ 19m10s, cargo-deny ✓. https://github.com/OliverD25/cabinetos/actions/runs/36426544858
  - In the `ui` job, 157 tests passed and 1 was skipped. The skipped one is the end-to-end test: that job builds no core, so the test skips itself.
- CI run 36424224855 on dd60ed0: all jobs green. https://github.com/OliverD25/cabinetos/actions/runs/36424224855
- CI run 36428606893 on b209f73 (another agent's change to `sdk/config`): WinUI shell ✓. So the UI tests also pass against the new config schema.
- My last push (34ef8c6) changed only docs, so it started no CI run.

### Live check
The screenshots `docs/log/2026-09-28/phase-5-window.png` and `phase-5-palette.png` are **not taken yet**, because the screen is locked.

For a look now, there are renders of the window's own content in `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\phase5-locked\`. They have no Mica and no caption buttons, and they sit on a dark fill:
- `window.png`: view A at first start.
- `palette.png`: view B with "dual" typed.
- `single.png`: single-pane mode.
- `bench.png`: the folder of 100,000 entries.

What I checked with the screen locked. I used release builds of the UI and the core, a fresh temp config, and the snapshot steps, which run the commands through the router:
- Dual pane on first start: yes. The left pane shows `C:\Users\Admin` (84 entries). The right pane shows `Documents` (2 entries).
- From the start of `Main` to both panes shown: 0.82–0.87 s over 3 runs.
- `palette.show` and then typing "dual" gives one row: "View: Toggle Dual Pane", keycap Ctrl+Shift+D, with the pencil. UI Automation finds the pencil by its name, "Change keybinding".
- `view.toggleDualPane` switches to a single pane, and running it again goes back to dual.
- A folder of 100,000 entries (`%TEMP%\cabinetos-bench\100000`, opened through `go.toPath`):
  - The core takes 38 ms and the reply arrives at 39 ms.
  - The first row is made at 61 ms, and the first frame is drawn at 82 ms.
  - A locked screen draws about 33 frames per second, so the last step can be up to 30 ms faster on an unlocked screen.
- Drives: `list_volumes` gets `unknown_request`, so the section stays hidden, with one INFO line.
- Close: the app exited in 412 ms with code 0. The core it started exited with code 0, and none of its own `cabinetos-core.exe` processes were left.
- One `list_directory` has the same request ID in both logs. This run was `dotnet run` with debug builds:
```
{"ts":"2026-09-28T12:59:42.959Z","level":"INFO","boundary":"frontend","target":"cabinetos_ui::pipe","message":"request sent","request_id":"01M3M1K2FDM0CR1M33N01RHXJK","span":"request","fields":{"request":"list_directory","bytes":98},"thread":"ui"}
{"ts":"2026-09-28T12:59:42.961Z","level":"INFO","boundary":"engine","target":"cabinetos_core::connection","message":"listing opened","request_id":"01M3M1K2FDM0CR1M33N01RHXJK","span":"request","fields":{"elapsed_us":1249,"entries":84,"listing_id":1,"path":"C:\\Users\\Admin","watched":true},"thread":"core-rt-3"}
{"ts":"2026-09-28T12:59:42.961Z","level":"INFO","boundary":"engine","target":"cabinetos_core::connection","message":"request handled","request_id":"01M3M1K2FDM0CR1M33N01RHXJK","span":"request","fields":{"elapsed_us":1951,"request":"list_directory"},"thread":"core-rt-3"}
{"ts":"2026-09-28T12:59:42.962Z","level":"INFO","boundary":"frontend","target":"cabinetos_ui::pipe","message":"reply received","request_id":"01M3M1K2FDM0CR1M33N01RHXJK","span":"request","fields":{"request":"list_directory","reply":"listing_opened","elapsed_us":5585},"thread":".NET TP Worker"}
{"ts":"2026-09-28T12:59:43.034Z","level":"INFO","boundary":"frontend","target":"cabinetos_ui::pane","message":"listing shown","request_id":"01M3M1K2FDM0CR1M33N01RHXJK","span":"request","fields":{"path":"C:\\Users\\Admin","entries":84,"core_us":1249,"reply_ms":13.5,"first_row_ms":40.7,"first_frame_ms":77.4},"thread":"ui"}
```

Not done yet, because these need an unlocked screen:
- the two screenshots;
- real key presses: Ctrl+Shift+D, Tab, Enter, Backspace, Ctrl+Shift+P;
- the rebinding through the pencil, and the `keybindings` it leaves in `cabinetos.json`;
- Enter on the 100,000-entry folder at a real frame rate;
- the worst frame time with PageDown held for 5 s. This is the Phase 5 "no frame drops" criterion.

### Decided
**Build and packages**
- Target `net10.0-windows10.0.22621.0`, x64 only, warnings as errors — because the task asked for .NET 10, ADR 0004 sets 22H2 as the floor, and PLAN §2 is x64 only — undo: `ui/Directory.Build.props`.
- Windows App SDK 2.5.1, framework-dependent (the Windows App Runtime comes from the machine; this PC has had 2.5.1 since 2026-09-17) — because the output is 38 MB instead of 152 MB — undo: `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` in `ui/CabinetOS/CabinetOS.csproj`.
- `JsonSchema.Net` pinned to 8.0.5 — because 9.x moved to the "Open Source Maintenance Fee" EULA, which is not MIT, Apache or BSD — undo: raise it in `ui/Directory.Packages.props` after a license check.
- xunit v3 4.0.1 on Microsoft.Testing.Platform, switched on in `ui/global.json` (`"test": {"runner": "Microsoft.Testing.Platform"}`) — because xunit v3 4.x does not run under VSTest on the .NET 10 SDK — undo: remove the `test` block (then the tests do not run).
- Central Package Management — because one file then holds every version — undo: move the versions into the csproj files.
- CsWin32 for MapViewOfFile, UnmapViewOfFile, CloseHandle and MessageBox — because generated signatures are checked and hand-written P/Invoke is not — undo: `ui/CabinetOS.Core/NativeMethods.txt` and the package.
- CommunityToolkit.Mvvm 8.4.2, used only for `ObservableObject` — because it is MIT and saves writing property-changed code by hand — undo: remove the package.
- Installed `dotnet-stack` 10.0.745401 as a global .NET tool, to read the UI thread's stack during a start-up hang — because the task allows `dotnet tool install -g` — undo: `dotnet tool uninstall -g dotnet-stack`.

**Pipe and core lifetime**
- My own `SectionHandle : SafeHandle` for section handles — because `SafeMemoryMappedFileHandle` has no public constructor that takes a raw handle — undo: none needed (it is internal).
- A reply completes its waiting code inline when the caller is on the UI thread, and asynchronously when the caller has no SynchronizationContext — because the UI must apply a reply before the next event from the pipe, and code with no context must not run on the pipe's reader thread — undo: `CoreClient.RequestAsync`.
- `hello` sends the real process ID and `client_name: "CabinetOS.exe"` — because the core logs the client by that name (the ipc.md example uses "CabinetOS"; see "Noticed") — undo: `CoreLauncher`.
- The launcher looks in this order: `CABINETOS_CORE_EXE`, then next to `CabinetOS.exe`, then `core/target/debug` and `core/target/release`, which it finds by walking up to `core/Cargo.toml` — because `dotnet run` inside the repository must work without setup — undo: `CoreLauncher.Candidates`.
- The launcher retries the pipe for up to 10 s, and fails at once if the core exits — undo: `CoreLauncher.StartAsync`.
- A core that stops is restarted at most three times a minute, and both folders are listed again — because a crash loop must end in a dialog, not in a window that keeps restarting — undo: `MainWindow.OnCoreLostAsync`.
- On close, the window hides at once, sends `shutdown`, closes the pipe right after the `ok`, and waits up to 2 s — because the core waits up to 2 s for open connections, so keeping the pipe open made closing take 2.0 s instead of 0.4 s — undo: `CoreClient.ShutdownCoreAsync`.
- The core's stderr is read, and its last lines are shown in the "core could not start" dialog with the places the launcher looked — because a failed start must say why — undo: `CoreLauncher`.

**Keys and commands**
- While a text box has the focus, a binding without `when` applies only if it is in the Immutable System Tier — because typing must never start a shortcut, and Esc and Ctrl+Shift+P must always work (Article 7) — undo: `ChordStateMachine.Applies`.
- Five UI-only commands run through the router with their own IDs: `go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind` — because every visible action must go through `CommandRouter` with a command ID, and the core's registry does not list them — undo: the `RegisterLocal` calls in `MainWindow.RegisterCommands`.
- `keys.open` opens the palette — because the palette lists every command with its keys and edits them, and v1 has no separate keys editor — undo: its handler in `RegisterCommands`.
- `palette.show` toggles, so a second Ctrl+Shift+P closes the palette — undo: its handler.
- F2 on the highlighted row starts a rebinding, like the pencil — because the mouse is optional (Article 7) — undo: `CommandPalette` key handling.
- Recording: the first key that is not a modifier makes a combination. A second key within 1000 ms makes a chord and ends the recording at once; otherwise it ends after 1000 ms. Esc cancels — because this matches the chord window in keybindings.md — undo: `PaletteModel`.
- A palette row shows only the first key of its command — because the design's row has room for one keycap — undo: `PaletteRowView`.
- `search_commands` asks for up to 1000 results, 30 ms after the last key — because the core ranks and the registry is small — undo: `PaletteModel`.
- A folder opened by a command is listed under that command's ULID — because one ID then traces a key press into the core's log — undo: `PaneModel.NavigateAsync`.
- Status bar messages — because every outcome of a command must be visible — undo: `MainWindow.OnCommandCompleted`:
  - a UI command with no handler says "<name> arrives in a later version.";
  - a core error shows in red;
  - a `command_result` opens a dialog.

**Look and layout**
- The accent is the system accent (`AccentFillColorDefaultBrush`) — because a first-party Windows 11 app follows the user's accent (Article 3) — undo: `CbAccentBrush` in `App.xaml`.
- The light theme maps the design's tokens to WinUI's own light resources — because the design defines only dark — undo: the ThemeDictionaries in `App.xaml`.
- The palette panel uses an in-app `AcrylicBrush`: tint #2C2C2C, tint opacity 0.15, luminosity opacity 0.72, fallback #F22C2C2C — because this matches the design's panel over Mica — undo: `CbPaletteAcrylicBrush` in `App.xaml`.
- No drop shadow under the palette — because `ThemeShadow.Receivers.Add` made WinUI end the process at start (0xC000027B) — undo: add it again when WinUI allows it.
- The title bar uses the standard caption height. There is no app icon (the design handout has none), no "+" tab button and no view-mode buttons, because workspaces and view modes are not in v1 — undo: the `MainWindow` constructor and XAML.
- The toolbar search box is shown disabled, with the design's placeholder ("Search <folder>") — because the layout keeps its place and file search comes in its own phase — undo: `MainWindow.xaml`.
- The status bar names `ui.layout` ("Terminal: bottom", "Terminal: right", "Activity rail"). `rail` shows the sidebar for now and logs one line — undo: `MainWindow.ApplySettings`.
- Formats — because the design writes them this way and dates should follow the user's region — undo: `DisplayFormat`:
  - sizes as the design writes them: B, then KB rounded up like Explorer, then MB and GB;
  - times in the user's regional format, with "Today", "Yesterday" and the weekday within a week;
  - the type as "EXT File".

**First start and state**
- First start: the left pane shows the profile folder, and the right pane shows Documents if it is a folder in the profile, else `C:\` — because view A shows two folders, a casual user starts at home (Article 4), and conflict B says dual pane — undo: `MainWindow.OpenFirstFoldersAsync`.
- Pinned folders: Desktop, Downloads, Documents, and the profile under its own folder name. Downloads comes from `Windows.Storage.UserDataPaths.GetDefault().Downloads`, with `%USERPROFILE%\Downloads` as the fallback — because .NET has no special-folder value for Downloads — undo: `MainWindow.SetPinnedFolders`.
- The last folders and the dual/single state live only in memory — because the core has no request that writes a setting other than a key binding — undo: when a settings-write request exists.
- `listing_refreshed` keeps the selection by entry ID. `listing_lost` moves the pane to the nearest parent the core can list. A change to `panes.showHidden` or `panes.sort` lists both folders again — undo: `PaneModel` and `MainWindow.ApplySettings`.

**Diagnostics**
- The UI log follows diagnostics.md (Article 12) — undo: `Diag` and `LogWriter`:
  - `ui.<UTC date>.jsonl` with the same key order as the core;
  - targets `cabinetos_ui::<area>`, and the UI thread is named `ui`;
  - a bounded lock-free queue with a background writer, and 14 daily files.
- Crash traces come from four places. Each process writes at most one trace — because in some failures WinUI ends the process without raising either unhandled-exception event — undo: `Program.cs` and `Services/UiSynchronizationContext.cs`:
  - `Application.UnhandledException`;
  - `AppDomain.UnhandledException`;
  - a catch around `Application.Start`;
  - a UI SynchronizationContext, which writes the trace for an exception in an async handler before the process ends.
- XAML binding and resource failures are logged as WARN — undo: `App.xaml.cs`.
- `app.manifest` has `maxversiontested 10.0.26200.0`, the supportedOS ID and PerMonitorV2 — because Microsoft recommends this for unpackaged WinUI apps — undo: remove the file.
- A custom `Program.Main` (`DISABLE_XAML_GENERATED_MAIN`) names the UI thread, starts diagnostics before WinUI, and shows a message box below the ADR 0004 Windows version — undo: remove the constant and `Program.cs`.

**CI and tools**
- The `ui` job — undo: remove the job from `ci.yml`:
  - runs on windows-latest with a 15-minute limit;
  - gets the .NET SDK from `ui/global.json` and caches NuGet on the package files;
  - builds Release with `-warnaserror` and runs the tests.
  - Note: a1ccf5d, by another agent, later added per-job concurrency groups and a "which parts changed" job. My job's steps are unchanged.
- Development aids, all off by default — undo: `Services/FrameMonitor.cs`, `Services/DevSnapshots.cs`, `Program.cs`:
  - `CABINETOS_UI_FRAMESTATS=1` logs frame stats every second;
  - `CABINETOS_UI_SNAPSHOT` with `CABINETOS_UI_SNAPSHOT_STEPS` renders the window to PNG files, and works with the screen locked;
  - `--self-test-crash` throws on the UI thread after start-up, to show a crash trace.

### Needs the core
1. `list_volumes` in the protocol.
   - The UI sends `{"type":"list_volumes"}` and reads `{"type":"volumes","volumes":[…]}`. Each item has the `volume_info` fields: `drive_letter`, `volume_guid_path`, `filesystem`, `label`, `total_bytes`, `free_bytes`, `disk`.
   - The Drives rows need at least the letter, the label, and total and free bytes, for the design's "X free" line and usage bar.
   - 0a14bee added `volume::drives` in cabinetos-fs, with letters and drive types only. If the reply will carry only those, send me the shape and I will adapt the rows.
   - Until then, each start logs one WARN "request rejected" in the core log.
2. A request that writes a setting. b209f73's commit message calls it `set_value`, but it is not in `docs/ipc.md` yet.
   - The keys `ui.lastPaths` and `ui.pinned` exist since b209f73. The UI does not read them yet, because they arrived after this work.
   - With the request in place, the UI would write `ui.lastPaths`, `ui.dualPane`, `ui.sidebar` and `ui.pinned`, and read them at start.
3. The core registry should list `go.back`, `go.forward`, `go.up`, `pane.openSelected` and `keys.rebind` with `target: ui`. Then they appear in the palette and can be rebound (Article 7).
   - The keys the UI uses today: Alt+Left, Alt+Right, Alt+Up and Backspace, Enter.
   - Enter and Backspace should have `when: "filesView"`.
4. Shell type names and icons. PLAN Phase 2 moved this hydration to Phase 5, but it needs a core request. The UI shows "EXT File" and a small icon set until the core sends them.

### Needs the user
1. **The rest of the live check.** Unlock the PC. Then do not touch the keyboard or mouse for about 2 minutes, and send this coder agent the message "run the live check".
   - The script does all of this:
     - takes both screenshots and commits them to `docs/log/2026-09-28/`;
     - presses Ctrl+Shift+D twice, Tab twice, then Enter, Backspace and Ctrl+Shift+P;
     - rebinds "View: Toggle Sidebar" to Ctrl+Alt+B through the pencil;
     - shows the `keybindings` of the temp `cabinetos.json`;
     - measures Enter on the 100,000-entry folder and the worst frame time with PageDown held for 5 s;
     - confirms that no core is left, then updates `docs/ui.md` and reports the numbers.
   - The script stops at once if the screen is locked, or if the CabinetOS window is not the foreground window before any step. So no key can reach another program. I tested the locked-screen stop.
   - Script: `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\livecheck.ps1`. It uses the Release builds in the worktree. If another agent runs it instead, it must copy `phase-5-window.png` and `phase-5-palette.png` from `%TEMP%\cabinetos-ui-test\live-shots\` into the repository and commit them.
2. **A license exception to confirm.**
   - `Microsoft.WindowsAppSDK` 2.5.1 and the Windows App Runtime ship under the Microsoft Software License Terms. That is not MIT, Apache or BSD.
   - These terms include a data-collection clause: Microsoft may collect usage data from the runtime.
   - WinUI 3 (ADR 0001) cannot be used without this package. `docs/dev-setup.md` already lists the license.
   - Please confirm the exception. Also say whether it should be recorded, for example as a line in ADR 0001 or as a new ADR.

### Known gaps
- The interactive part of the live check (see above). All timing numbers so far come from a locked screen, at about 33 frames per second.
- Nothing is kept between starts: the last folders, the dual/single state, sidebar visibility and pinned folders. `ui.lastPaths` and `ui.pinned` are not read yet.
- Drives stays hidden until `list_volumes` exists.
- The Type column shows "EXT File", and icons come from a small built-in set.
- Files do not open: Enter and double-click open folders only.
- The column headers are static, so there is no click-to-sort.
- There is no drop shadow under the palette and no app icon.
- The light theme is not checked by eye, because the design is dark only.
- A palette row shows one key per command.
- The search box, the terminal toggle and the marketplace button are disabled placeholders.
- The end-to-end test does not run on CI, because the `ui` job builds no core.

### Noticed out of scope
- `docs/keybindings.md:62` says file commands answer `not_implemented` "until Phase 4". Phase 4 is done, and they still answer that.
- The `hello` example at `docs/ipc.md:46` uses `client_name: "CabinetOS"`. The UI sends "CabinetOS.exe".
- `core/crates/cabinetos-core/src/connection.rs:236` logs a rejected request at WARN. So a normal check for a missing feature (the UI's `list_volumes`) leaves one WARN in the core log at every start. INFO may fit better.
- After `shutdown`, the core waits up to 2 s for open connections. The UI avoids this by closing its pipe at once. The connection that asked for the shutdown could be left out of that wait.
- PLAN Phase 2 says icon and type-name hydration "moves to Phase 5", but no core request for it exists yet.
- 0a14bee adds `open_path`, `create_directory` and `rename` in cabinetos-fs. When they reach the protocol, the UI can open files and offer New folder and Rename. These are not part of v1 by the task.
- The earlier CI problem, where one agent's push cancelled another agent's run, is already fixed by a1ccf5d. Nothing to do.

### Ready text: docs/PLAN.md, Phase 5
Use this heading now. After the live check passes, change the heading to "— done 2026-09-28", and replace the "Not measured yet" sentence with the result.
```markdown
### Phase 5 — WinUI 3 shell v1 — built 2026-09-28, scrolling check pending
```
Paragraph to insert after the "Done when:" line:
```markdown
Built 2026-09-28; the scrolling check waits for an unlocked screen. Built: `ui/CabinetOS.sln` on .NET 10 and the Windows App SDK 2.5.1, unpackaged, using the Windows App Runtime installed on the machine. `CabinetOS.Core` holds everything that runs without a window: the pipe client, the protocol types (tested against `sdk/protocol` and `sdk/config`), the core launcher, `ListingView` (the only `unsafe` C#; it reads listings straight from the core's shared memory), keys and the chord state machine, the `CommandRouter`, and diagnostics in the core's log format with crash traces at the `frontend` boundary. `CabinetOS.exe` draws design view A (Mica title bar; back, forward and up; breadcrumbs that turn into an address box; pinned folders in the sidebar; two panes of virtualized rows with an instant single-pane toggle; a status bar that shows a waiting chord) and design view B (the palette: the core ranks the commands, each row shows its keys and chords, the Immutable System Tier shows a lock, and the pencil or F2 records new keys, which the core writes to `cabinetos.json`). Dual pane on first start (conflict B). Every button, key, crumb, sidebar row and palette row goes through `CommandRouter` with a ULID, and a folder opened by a command is listed under the same ID in the core's log. 158 tests, one of them against the real core. Measured 2026-09-28 on this PC (release builds; the screen was locked, so Windows drew about 33 frames per second): both panes shown 0.82–0.87 s after start; a folder of 100,000 entries drawn 82 ms after the command, 38 ms of it in the core; closing ends the core in 0.4 s. Not measured yet: scrolling that folder with PageDown held, which needs an unlocked screen. Waiting for the core: `list_volumes` (the Drives section stays hidden), a request that writes settings (the last folders, the pane mode and the pinned folders stay in memory), and shell type names and icons. Guide: [ui.md](ui.md).
```

### Ready text: README status line
This replaces the whole "Status:" paragraph. After the live check passes, change "Phases 0 to 4" to "Phases 0 to 5", and drop the last clause.
```markdown
Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done, and
the core sides of Phases 6, 7 and 8: the governing documents, and a Rust
core that lists and watches directories in shared memory, serves its
configuration, commands and keymap, runs copy, move and delete jobs on
per-disk queues, runs sandboxed WebAssembly plugins whose crashes it
contains, searches whole NTFS volumes through an elevated indexer (or walks
folders without it), and runs shells in pseudo-consoles for the terminal
pane, all over a user-only named pipe, with the indexer behind a read-only
pipe of its own and each shell's bytes on a pipe of their own (`core/`, 398
tests, CI green). Phase 5, the WinUI 3 shell, is built (`ui/`, 158 tests,
CI green): two panes over the core's shared-memory listings, breadcrumbs, a
status bar, and the command palette with chord keys and inline rebinding;
its scrolling check is still to run.
```

### State left behind
- The worktree is clean, and HEAD equals origin/main (0a14bee after a fast-forward; my last commit is 34ef8c6).
- `%TEMP%\cabinetos-ui-test\` is deleted, and my background waits are stopped. The `%TEMP%\cabinetos-bench` fixture is not mine and is untouched.

Recap: Phase 5 is built, pushed and green in CI, and the core is untouched. What is left is the interactive live check: 2 minutes on an unlocked PC, then a message to this agent. After that, add the PLAN and README text above.
