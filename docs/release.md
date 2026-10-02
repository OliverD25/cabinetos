# Releases: build, install, sign, publish

How a CabinetOS release is made, installed and removed, and the steps that
are still for the creator: signing and publishing. Why it ships this way:
[ADR 0009](decisions/0009-packaging.md), and for the setup file and the
updates that install themselves
[ADR 0018](decisions/0018-setup-file-and-silent-updates.md). Commands are
for the WSL bash terminal unless marked `# PowerShell`, as in
[dev-setup.md](dev-setup.md).

## What a release is

One folder, zipped as `CabinetOS-<version>-win-x64.zip`, and the same
folder wrapped in a setup file, `CabinetOS-<version>-win-x64-setup.exe`
("The setup file", below). The symbols, the `.pdb` files, are in neither:
they are a third file, `CabinetOS-<version>-win-x64-symbols.zip` ("The
symbols", below). The zip has its files at its root:

| Part | What it is |
|---|---|
| `CabinetOS.exe` and the libraries next to it | The window: .NET 10, WinUI 3, published framework-dependent and compiled ahead of time (ReadyToRun). Of the Windows App SDK only the components the window uses (WinUI, Foundation, InteractiveExperiences, Runtime), so the folder holds no machine-learning library; `release.ps1` stops when one turns up ("Sizes", below) |
| `CabinetOS.pri` | The window's compiled XAML; without it the window cannot start |
| `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` | The Rust programs. The window's launcher finds the core next to `CabinetOS.exe` ([ui.md](ui.md)) |
| `cab.exe` | `cabinetos-cli.exe` once more, under a short name to type: `cab jobs`, `cab undo --last`. The same program, byte for byte; everything else keeps the name `cabinetos-cli` (the docs, the help text, the tests) |
| `Assets\xterm\` | The terminal page |
| `extras\themes\` | Copies of the four built-in themes and their schema, as a start for your own. The core also writes them into `%LOCALAPPDATA%\CabinetOS\themes` |
| `extras\tools\markdown-preview\` | The Markdown Preview tool. Opt-in (Constitution Article 10): the window does not load it from here |
| `install.ps1`, `uninstall.ps1` | The installer and its undo |
| `CabinetOS.ico` | The app's icon, a copy of the committed `ui/CabinetOS/Assets/CabinetOS.ico` (see "The icon" below): the setup file's icon, its shortcuts' and its Settings > Apps entry's. `CabinetOS.exe` carries the same icon inside it |
| `release.json` | The version, the commit, and the runtime versions `install.ps1` checks |
| `LICENSE`, `THIRD-PARTY-NOTICES.md` | The MIT license, and the license text of every third-party component |

The version has one source: `version` in `[workspace.package]` of
`core/Cargo.toml`. The core reports it in `pong`, the window in its log
lines and crash traces (from `<Version>` in `ui/Directory.Build.props`,
which must match), and it names the zip.

### Sizes

Measured on 2026-10-02 for 0.1.0 (`release.ps1` prints MB as 1 MB =
1,048,576 bytes, and so do the figures here):

| Build | Files | Folder | Zip | Setup | Symbols zip |
|---|---|---|---|---|---|
| The first release build, 2026-09-28: the whole Windows App SDK, no ReadyToRun, symbols in the zip | 83 | 246 MB | 77 MB | none yet | none |
| Before Phase 22: only the SDK components the window uses and ReadyToRun, symbols still in the zip and the setup | 71 | 251.7 MB | 75.7 MB (79,348,730 bytes) | 42.5 MB (44,550,486 bytes) | none |
| Now: the symbols in a zip of their own | 66 | 100.5 MB | 32.1 MB | 20.0 MB | 43.6 MB |

The release is under 60 MB zipped and its setup file under 35 MB (Phase 22's
goal). The second row's zip and setup carried 43.6 MB of symbols; the
symbols zip holds them now ("The symbols", below).

**No machine-learning libraries.** The window's project once referenced the
whole `Microsoft.WindowsAppSDK` package, which also brings the AI,
machine-learning, Search and Widgets components that CabinetOS never uses
(Constitution Article 10). Since 2026-09-29 it references only the
components it needs, at the same Windows App SDK version (2.5.1;
[dev-setup.md](dev-setup.md), "Phase 5 and later"). Measured on
2026-10-02, a publish of the window alone with the release script's flags
against a scratch copy of the project that names the whole package:

| Window's publish | Files | Unpacked | Zipped |
|---|---|---|---|
| The whole package | 60 | 100.3 MB | 34.7 MB |
| The components the window uses | 45 | 57.8 MB | 17.3 MB |
| What the whole package adds | 15 | 42.5 MB | 17.4 MB |

The 15 files are `onnxruntime.dll` (20.7 MB), `DirectML.dll` (17.8 MB),
`Microsoft.ML.OnnxRuntime.dll`, `System.Numerics.Tensors.dll`, eight
`Microsoft.Windows.AI.*` libraries, `Microsoft.Graphics.Imaging.Projection.dll`
and the Search and Widgets projections. `release.ps1` stops, before it
zips anything, when its release folder holds a file named `*onnxruntime*`,
`DirectML*`, `*.AI.*` or `*.MachineLearning.*`, and says that the project
must not reference the whole package or an AI or machine-learning
component. So a package that brings them back cannot reach a release
unseen.

ReadyToRun (since 2026-10-01) makes the window's folder bigger and its start
shorter. The window's own files grow from 41 to 57 MB unpacked (45 files
either way; `Microsoft.WinUI.dll` from 7 to 16 MB, `CabinetOS.Core.dll` from
1.6 to 3.8 MB), and zipped that part grows from 11.8 to 17.0 MB. The start
is 0.14 to 0.24 s shorter
([speed review](log/2026-10-01/speed-review.md), proposal A).

## Build

Needs everything in [dev-setup.md](dev-setup.md) for the core and the
window, plus PowerShell 7 (`pwsh`). One command:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/release.ps1
```

It writes, into `dist\` (ignored by git):

- `dist\CabinetOS-<version>-win-x64\`, the release folder;
- `dist\CabinetOS-<version>-win-x64.zip` and its `.zip.sha256`;
- `dist\CabinetOS-<version>-win-x64-symbols.zip` and its `.sha256`, the
  `.pdb` files ("The symbols", below);
- `dist\winget\<version>\`, the winget manifests with this version and the
  zip's SHA-256, checked with `winget validate` when winget is installed;
- `dist\update\<channel>\latest.json` and `notes-<version>.md`, what the
  in-app updater reads ("Updates", below);
- `dist\CabinetOS-<version>-win-x64-setup.exe` and its `.sha256`, the
  setup file ("The setup file", below).

What it runs, in order:

1. `cargo build --release --locked -p cabinetos-core -p cabinetos-indexer -p cabinetos-cli`
   in `core\`. `core\.cargo\config.toml` links the C runtime into the
   programs (`+crt-static`), so they do not need the Visual C++
   Redistributable, which a clean Windows 11 lacks.
2. `dotnet publish CabinetOS\CabinetOS.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o <release folder>`
   in `ui\`. Framework-dependent: .NET 10 and the Windows App Runtime come
   from the machine. ReadyToRun compiles the window's IL to native code ahead
   of time, so the start spends less time in the JIT; the first publish
   restores the compiler's NuGet package once. The project keeps the Windows App SDK's MSIX tooling on,
   which writes `CabinetOS.pri`, the compiled XAML, into the publish; without
   it the window stops at start. The app stays unpackaged. The script stops
   if the `.pri` file is missing.
3. Copies the three programs and their `.pdb` files (step 5 takes the
   `.pdb` files out again), `cabinetos-cli.exe` once more as `cab.exe`,
   the themes, Markdown Preview, `LICENSE` and the two install scripts;
   copies the committed `CabinetOS.ico` (made from the
   design's PNG size cuts; see "The icon"); writes `release.json`
   from the publish output and the Windows App SDK package (the minimum
   Windows App Runtime is the one the SDK's bootstrapper asks for).
4. `build\notices.ps1` writes `THIRD-PARTY-NOTICES.md`: every Rust crate the
   three programs are built from (from `cargo metadata`, normal and build
   dependencies for Windows x64), the Rust standard library, every NuGet
   package in the published `CabinetOS.deps.json`, the .NET application host
   and the bundled JavaScript, with their license files. It stops when a
   component has no license text.
5. Moves every `.pdb` file out of the release folder into the symbols
   zip and writes its hash ("The symbols"), then checks the folder: the
   script stops when it holds `onnxruntime`, `DirectML` or an AI or
   machine-learning library of the Windows App SDK ("Sizes"). Nothing is
   zipped before this check.
6. Zips the folder, writes the hash, fills the winget manifests.
7. Writes the in-app update's two files for the channel (`-Channel`,
   `stable` unless it says `preview`): `latest.json`, with the zip's
   address on the GitHub Release
   (`https://github.com/OliverD25/cabinetos/releases/download/v<version>/CabinetOS-<version>-win-x64.zip`),
   its SHA-256 and size, the notes' address on the marketplace site, and
   the runtimes `release.json` names (major.minor), checked against
   [sdk/update/latest.schema.json](../sdk/update/latest.schema.json); and
   `notes-<version>.md`, the text of the version's section in
   `CHANGELOG.md` (between `## [<version>]` and the next `## [`). While the
   version has no section yet, the notes are the `## [Unreleased]` section,
   and the script says so. The stable channel refuses a version with a
   pre-release tag.
8. Compiles `build\setup.iss` with Inno Setup 6.7 or newer (`ISCC.exe` on
   the PATH, or where Inno's installer puts it, per user or for every user)
   around the release folder, with the facts of its `release.json` as
   defines (the version, the runtimes, the Windows App Runtime's installer
   address), into `dist\CabinetOS-<version>-win-x64-setup.exe`, and writes
   its SHA-256. Without Inno Setup the step is skipped with a warning that
   names the command that installs it, and the rest of the release is
   complete:
   `winget install --id JRSoftware.InnoSetup --exact --scope user`.

Switches:

- `-SyncVersion`: `ui\Directory.Build.props` must carry the Cargo version;
  without this switch the script stops when they differ, with this switch it
  writes the Cargo version there first (commit that change).
- `-PackageOnly`: builds nothing; zips the existing release folder again and
  writes a new hash, new manifests, new update files and a new setup file.
  For after signing (below). Step 5 still checks the folder, and the
  symbols zip of the build before stays as it is (signing does not change a
  `.pdb` file).
- `-NoSetup`: no setup file (step 8).
- `-Channel stable|preview`: which channel's `latest.json` to write;
  `stable` by default. A version such as `0.2.0-preview.1` needs
  `preview`.

The build warns when `core`, `ui`, `sdk` or `build` hold uncommitted
changes or new files, and `release.json` records the commit and whether
they were clean.

### The icon

`ui/CabinetOS/Assets/CabinetOS.ico` is the app's icon, and it is committed.
`CabinetOS.csproj` embeds it in `CabinetOS.exe` (`<ApplicationIcon>`), so the
taskbar, Explorer and the Alt+Tab list show it, and step 3 copies the same
file into the release folder as `CabinetOS.ico`, for the setup file and its
shortcuts. `build\make-icon.ps1` makes the file from the design's PNG size
cuts in `docs/design/icons` (16, 24, 32, 48 and 256 px, each kept as the PNG
it is, never a scaled master; [ICON_HANDOFF.md](design/ICON_HANDOFF.md)).
When those icons change, run it again and commit the result:

```powershell
pwsh -NoProfile -File <repo>\build\make-icon.ps1
```

The same cuts always give the same bytes. `release.ps1` makes the file
itself, with a warning, only when it is missing.

### In GitHub Actions

`.github/workflows/release.yml` runs the same script on a clean
`windows-latest` runner and keeps the zip, its hash and the manifests as a
workflow artifact for 30 days. It starts only by hand, and it publishes
nothing (an artifact is not a GitHub Release):

```bash
gh workflow run release.yml --repo OliverD25/cabinetos && gh run list --workflow release.yml --repo OliverD25/cabinetos --limit 1
```

When it has finished, download the artifact of the newest run:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && gh run download "$(gh run list --workflow release.yml --repo OliverD25/cabinetos --limit 1 --json databaseId --jq '.[0].databaseId')" --repo OliverD25/cabinetos --dir dist/ci
```

It has not run yet: on 2026-09-28 GitHub Actions started no jobs for this
account (a billing setting).

## The setup file

`CabinetOS-<version>-win-x64-setup.exe` is the first install for most
users: a double-click, no script, no administrator rights
([ADR 0018](decisions/0018-setup-file-and-silent-updates.md)). It is
`build\setup.iss`, Inno Setup 6.7 around the release folder, compiled by
the build's last step.

- **Where it installs.** For the current user only, into
  `%LOCALAPPDATA%\Programs\CabinetOS`, the folder `install.ps1` uses, so
  the install updates itself ("Updates", below). The folder cannot be
  chosen in the wizard.
- **What it asks.** Almost nothing: an unchecked "Create a desktop
  shortcut", then "Start CabinetOS", checked, at the end. A Start Menu
  shortcut is always made. The wizard follows Windows' light or dark mode.
- **The prerequisites** (Windows 11 22H2 or newer, x64; the .NET 10
  runtime; the Windows App Runtime 2.5.1 or newer; WebView2) are checked
  before the wizard, as `install.ps1` checks them. A missing one stops the
  setup with a message that names the winget command (and, for the Windows
  App Runtime, Microsoft's installer). The setup downloads nothing, and
  that is decided (Phase 22, unit 5, 2026-10-02, on the planning session's
  recommendation): it keeps stopping with the winget command, as
  `install.ps1` does, and nobody should add a download without a new
  decision.
- **No symbols.** The setup holds no `.pdb` file: `release.ps1` takes them
  out of the folder first, and `setup.iss` excludes `*.pdb` besides ("The
  symbols"). It is 20.0 MB.
- **Over an install.ps1 install** in the same folder the setup stops and
  names that install's `uninstall.ps1`: the two keep separate records and
  Apps entries and must not mix. `install.ps1` likewise refuses the setup's
  folder. A setup over an earlier setup install is an update: the files
  are replaced (a running CabinetOS is closed first, through Windows'
  Restart Manager).
- **Settings > Apps** lists it as "CabinetOS" (publisher CabinetOS, with
  its version, size and icon) under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS_is1`,
  Inno's own entry. The in-app updater keeps its version and size current
  after each update.
- **Silent**, for scripts and the VM check:

  ```powershell
  # PowerShell - the setup is a Windows program
  & "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64-setup.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="$env:TEMP\cabinetos-setup.log"
  ```

  Exit code 0 is a finished install; 1 means the setup stopped before it
  began (a missing prerequisite: the log says which); 7 means it refused
  the folder (an install.ps1 install there). `/SKIPPREREQUISITECHECK`
  installs even when a prerequisite looks missing. A silent setup does not
  start CabinetOS at the end.
- **Uninstall**: Settings > Apps > CabinetOS > Uninstall, or
  `unins000.exe` in the install folder (silently:
  `"%LOCALAPPDATA%\Programs\CabinetOS\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`).
  It refuses to start while CabinetOS runs from the folder. It removes the
  whole install folder, with what the updater added (`previous\`,
  `previous-old\`, a later version's files), the shortcuts and the Apps
  entry. Settings, plugins, themes, logs and the update state stay in
  `%APPDATA%\CabinetOS` and `%LOCALAPPDATA%\CabinetOS`, as with
  `uninstall.ps1`; delete those two folders by hand to remove them too.
- **Checked in the VM.** `ui\livecheck\vm-install-check.ps1` installs the
  setup silently in the VirtualBox VM, runs the live check against the
  installed program, lets it update itself to a real next version, restarts
  it through the notice and uninstalls it ([ui.md](ui.md), "The live check
  in a virtual machine"). Its report is `_io\live-check\DONE-install.md`.

## The symbols

`CabinetOS-<version>-win-x64-symbols.zip`, with its `.sha256`, holds the
`.pdb` files of the release: `cabinetos_core.pdb`, `cabinetos_cli.pdb` and
`cabinetos_indexer.pdb` (the Rust programs'; 116, 26 and 16 MB unpacked) and
`CabinetOS.pdb` and `CabinetOS.Core.pdb` (the window's; 0.4 and 0.8 MB). It is
43.6 MB zipped, and neither the release zip nor the setup file carries it:
nobody needs symbols to run CabinetOS, and they were more than half of the
zip ([ADR 0019](decisions/0019-symbols-in-their-own-zip.md)). `release.ps1`
makes it and puts the files at its root.

**What they are for.** When a program crashes, it writes a crash trace
(`crash-<time>.json` for the core, the indexer and the CLI, the window's own
for the window; [diagnostics.md](diagnostics.md)). A trace shows the way to
the crash in full only with the symbols next to the programs. What a trace
holds without them and with them, checked on 2026-10-02 on a copy of the
release folder (`cabinetos-core --self-test-panic`, and an exception
thrown inside `CabinetOS.Core.dll`):

| | The Rust programs | The window |
|---|---|---|
| Without the symbols | `backtrace` is a list of `<unknown>` frames: only Windows' own `BaseThreadInitThunk` and `RtlUserThreadStart` have names. The panic's `location` (file, line and column), the message, the thread, the boundary and the last log lines are still in the file | `backtrace` names every method, with no file and no line; `location` is `null` |
| With the symbols next to the programs | every frame has its function and its file and line, for example `cabinetos_core::self_test_panic at .\core\crates\cabinetos-core\src\main.rs:154` | every method has its file and line, and `location` holds the first one |

So a crash from a release with no symbols still says which process failed
(`boundary`, Constitution Article 12) and, for a Rust panic, the line of the
panic, but not the way to it. For that, use the symbols of the same
version; `.pdb` files of another build do not match. The Rust programs
record their `.pdb` by its file name alone, not by a path on the build
machine, so only a file next to the `.exe` counts (checked in the binary).

**How to use them.** Download the symbols zip of the version that crashed,
and unpack it into the install folder, next to the programs, then make the
crash again (or read a trace written later):

```powershell
# PowerShell - the symbols next to the programs of a per-user install
Expand-Archive "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64-symbols.zip" "$env:LOCALAPPDATA\Programs\CabinetOS" -Force
```

Nothing else needs a setting. A trace written before the symbols were there
stays as it was written. An in-app update moves every file of the install
folder into `previous\` before it copies the next version in, so the old
version's symbols go there too: unpack the new version's symbols again.
`uninstall.ps1` removes what it installed and leaves the symbols you added
(and the folder with them); the setup's uninstaller removes the whole
folder.

## Install

What a user needs: Windows 11 22H2 (build 22621) or newer, x64; the .NET 10
runtime; the Windows App Runtime 2.5.1 or newer (x64); the WebView2 Runtime.
`install.ps1` checks all four before it changes anything, and for each
missing one prints the command that installs it, for example
`winget install --id Microsoft.DotNet.Runtime.10 --exact`. It downloads
nothing itself.

The winget packages, checked with `winget search` on 2026-09-28:

| Prerequisite | winget package | Note |
|---|---|---|
| .NET 10 runtime | `Microsoft.DotNet.Runtime.10` | 10.0.12 |
| Windows App Runtime | `Microsoft.WindowsAppRuntime.2` | Only 2.3.1, older than the 2.5.1 CabinetOS needs. Until winget has 2.5, `install.ps1` also prints Microsoft's installer: https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe |
| WebView2 Runtime | `Microsoft.EdgeWebView2Runtime` | 154.0.4258.37; every Windows 11 has it already |

Unblock the downloaded zip first: then Windows does not mark every
unpacked file as downloaded, and the unsigned programs start without a
SmartScreen question. Windows PowerShell does not run scripts by default,
so start the installer with `-ExecutionPolicy Bypass`; that applies to
this one command only.

```powershell
# PowerShell - the installer is a Windows script
Unblock-File "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip"; Expand-Archive "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip" "$env:TEMP\CabinetOS-0.1.0" -Force; powershell -ExecutionPolicy Bypass -File "$env:TEMP\CabinetOS-0.1.0\install.ps1" -StartMenu -AddToPath
```

Switches (the script's own help lists them too: `Get-Help <folder>\install.ps1 -Detailed`
in a PowerShell started with `-ExecutionPolicy Bypass`):

| Switch | Effect |
|---|---|
| (none) | Copies the folder to `%LOCALAPPDATA%\Programs\CabinetOS`, for the current user, with no administrator rights |
| `-AllUsers` | Copies to `%ProgramFiles%\CabinetOS` instead. Needs "Run as administrator" |
| `-Destination <folder>` | Another folder: new, empty, or an earlier CabinetOS install; never a drive's root |
| `-StartMenu` | A Start Menu shortcut: yours, or every user's with `-AllUsers` |
| `-AddToPath` | The install folder on your PATH (the machine's with `-AllUsers`), for `cabinetos-cli` and `cab`; new terminals see it. Off unless given |
| `-Indexer` | Also installs and starts the indexer service (`cabinetos-indexer --install`). Needs `-AllUsers` and a folder inside Program Files: the service runs as LocalSystem, so its program must sit where only administrators can change it. The service starts manually: after a restart of Windows, start it with `Start-Service cabinetos-indexer` as administrator |
| `-SkipPrerequisiteCheck` | Installs even when a prerequisite looks missing |
| `-WhatIf` | Shows every step and changes nothing |

The script asks nothing. It lists CabinetOS in Settings > Apps, with its
version, `CabinetOS` as the publisher, its size, and an Uninstall button
that runs `uninstall.ps1`: under the user's registry hive
(`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS`),
or with `-AllUsers` under the machine's (`HKLM\…`). It records what it did
in `.cabinetos-install.json` in the install folder: the files, the
shortcut, the PATH entry, the service, the Apps entry. Running it again over the same folder is an
update: it copies the new files and removes the ones the old version had
and the new one lacks. It refuses a folder that holds other files, a
folder that overlaps the release folder, and a folder with CabinetOS still
running from it.

For an all-users install with the indexer, from a PowerShell started with
"Run as administrator":

```powershell
# PowerShell - run as administrator; the service and Program Files need it
powershell -ExecutionPolicy Bypass -File "$env:TEMP\CabinetOS-0.1.0\install.ps1" -AllUsers -StartMenu -AddToPath -Indexer
```

## Uninstall

```powershell
# PowerShell - the uninstaller is a Windows script
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\CabinetOS\uninstall.ps1"
```

Settings > Apps > CabinetOS > Uninstall runs the same script. It removes
exactly what the record lists: the files, the shortcut, the PATH entry,
the service (stopped first) and the Apps entry; and the in-app updater's
copies in the install folder, `previous\` and `previous-old\`. Files someone else put into
the install folder stay, and so does the folder then. Settings, plugins,
themes and logs stay in `%APPDATA%\CabinetOS` and `%LOCALAPPDATA%\CabinetOS`;
`-RemoveData` deletes those two folders too, and `%ProgramData%\CabinetOS`
(the service's logs) for an install with the indexer. Other users' data
stays. An all-users install needs "Run as administrator" to remove.
`-Destination <folder>` names the install folder when the script runs from
somewhere else.

## Updates

A per-user install updates itself from inside the app
([ADR 0014](decisions/0014-in-app-updates.md)); the protocol is in
[ipc.md](ipc.md), "Updates", and the settings, `update.*`, in
[config.md](config.md).

- **The check.** Once a day (10 seconds after the start, then hourly
  whether a day has passed) the core reads
  `<update.source>/<channel>/latest.json`, by default
  `https://oliverd25.github.io/cabinetos-marketplace/update/stable/latest.json`.
  "Update: Check for Updates" in the palette checks at any time. With
  `update.check: false` only the command checks.
- **The download.** A newer version downloads in the background, with a
  status-bar pill. Its SHA-256 must be the one `latest.json` gives; it is
  unpacked into `%LOCALAPPDATA%\CabinetOS\update\staging\<version>\`
  (`CABINETOS_UPDATE_DIR` or the core's `--update-dir` name another
  folder), and its `release.json` must name the same version.
- **The swap, by itself** (`update.autoInstall`, the default;
  [ADR 0018](decisions/0018-setup-file-and-silent-updates.md)). Right
  after the download the core moves the install's files into `previous\`
  inside it, copies the new ones in and updates the Apps entry, while
  CabinetOS runs on from `previous\`. The setup file's uninstaller stays
  where it is. The status bar then says "CabinetOS 0.2.0 is installed;
  restart to use it", with Restart now and Later; nothing else asks.
  Restart now closes the window and starts the new `CabinetOS.exe`. Later
  keeps the session; the next start runs the new version. A copy that
  fails half way puts everything back, the status bar says so, and the
  next daily check tries the swap again.
- **The swap, at the user's word** (`update.autoInstall: false`, as in
  ADR 0014). A dialog shows the version and its release notes, with
  Restart now and Later. Restart now swaps as above, starts the new
  `CabinetOS.exe` and closes the window. Later waits a day.
- **The rollback.** "Update: Roll Back to the Previous Version" brings
  `previous\` back the same way, until the next update replaces it.
- **Who updates.** Only a release with `release.json` next to the core
  that the user may change without administrator rights: the per-user
  install. An all-users install says to use the installer or winget, and a
  development build never checks.
- **The state** lives in `%LOCALAPPDATA%\CabinetOS\update\state.json`: the
  last check, the `ETag`, the snooze, the staged version, and the last swap
  with the start that confirmed it.
- **From a terminal:** `cabinetos-cli update` prints the state; `update
  check`, `download`, `apply`, `rollback` and `snooze` run the steps
  (`download` also swaps with `update.autoInstall`); `--json` prints the
  core's reply.

## Sign (not done yet)

Nothing is signed: no certificate exists. Until one does, SmartScreen asks
before the first start of a downloaded build, and Smart App Control, where
it is on, blocks the programs outright.

What signing needs:

- **A code-signing certificate** from a certificate authority Windows
  trusts. Since June 2023 its private key must sit on a hardware token or
  in a cloud key store, so `signtool` reaches it through the token's entry
  in the Windows certificate store or through the signing service's plugin.
  A new certificate still meets SmartScreen's warning until it has built
  reputation with Microsoft.
- **`signtool`**, from the Windows SDK. On this PC:
  `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`.

Sign CabinetOS's own programs and scripts in the release folder, then zip
it again. The Microsoft libraries next to them are signed by Microsoft
already. With the certificate in the store (its SHA-1 thumbprint in place
of `<thumbprint>`):

```powershell
# PowerShell - signtool and the certificate store are Windows-only
$release = 'E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\dist\CabinetOS-0.1.0-win-x64'; $signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'; $files = 'CabinetOS.exe', 'CabinetOS.dll', 'CabinetOS.Core.dll', 'cabinetos-core.exe', 'cabinetos-indexer.exe', 'cabinetos-cli.exe', 'cab.exe' | ForEach-Object { Join-Path $release $_ }; & $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <thumbprint> $files; & $signtool verify /pa /v $files; Set-AuthenticodeSignature -FilePath "$release\install.ps1", "$release\uninstall.ps1" -Certificate (Get-Item Cert:\CurrentUser\My\<thumbprint>) -HashAlgorithm SHA256 -TimestampServer http://timestamp.digicert.com
```

The timestamp keeps the signatures valid after the certificate expires.
Then zip the signed folder again; the hash, the winget manifests and the
setup file change with it (sign the new setup file with the same
`signtool` line afterwards, and write its hash again):

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/release.ps1 -PackageOnly
```

## Publish (not done yet)

Written down, not run. Each step is outward-facing, so it is the
creator's.

1. **Choose the version.** Set `version` in `core/Cargo.toml`, run
   `pwsh.exe -NoProfile -File build/release.ps1 -SyncVersion`, rename
   `## [Unreleased]` in `CHANGELOG.md` to `## [0.1.0] - <date>`, and commit.
   Done for 0.1.0 on 2026-09-30: both versions were 0.1.0 already, and the
   section is `## [0.1.0] - 2026-09-30`, with an empty `## [Unreleased]`
   above it for what comes next.
2. **Build and sign** as above.
3. **Make the repository public.** The release asset's address must work
   for strangers and for winget.
4. **Tag and publish the release** with the zip, the setup file and their
   hashes. The notes are the version's section of `CHANGELOG.md`, which
   the build wrote as `dist/update/stable/notes-0.1.0.md`:

   ```bash
   cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && git tag v0.1.0 && git push origin v0.1.0 && gh release create v0.1.0 dist/CabinetOS-0.1.0-win-x64.zip dist/CabinetOS-0.1.0-win-x64.zip.sha256 dist/CabinetOS-0.1.0-win-x64-setup.exe dist/CabinetOS-0.1.0-win-x64-setup.exe.sha256 --verify-tag --title "CabinetOS 0.1.0" --notes-file dist/update/stable/notes-0.1.0.md
   ```

   For a preview (a version such as `0.2.0-preview.1`, built with
   `-Channel preview`), add `--prerelease` and use `dist/update/preview/`.
5. **Tell the installed copies.** Copy `latest.json` and the notes into a
   checkout of the marketplace repository under `update/<channel>/`, and
   push; GitHub Pages serves them within a minute or two, and every
   per-user install finds the version at its next daily check. Publish the
   GitHub Release first: `latest.json` points at its zip.

   ```bash
   cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager && test -d cabinetos-marketplace/.git && mkdir -p cabinetos-marketplace/update/stable && cp cabinetos/dist/update/stable/latest.json cabinetos/dist/update/stable/notes-0.1.0.md cabinetos-marketplace/update/stable/ && cd cabinetos-marketplace && git add update/stable && git commit -m "update: CabinetOS 0.1.0 on the stable channel" && git push
   ```

   The first command stops the block when the marketplace checkout is not
   there (`gh repo clone OliverD25/cabinetos-marketplace` makes it). A
   wrong `latest.json` is taken back by pushing the previous one: installs
   that already downloaded the bad version keep it until they roll back.
6. **Submit to winget.** The manifests in `dist\winget\0.1.0\` carry the
   zip's address and hash. They go to the community repository
   `microsoft/winget-pkgs` as a pull request, under
   `manifests/o/OliverD25/CabinetOS/0.1.0/`, for example with
   `wingetcreate submit` (`winget install --id Microsoft.WingetCreate --exact`).
   Blocked today: the manifest depends on `Microsoft.WindowsAppRuntime.2`
   2.5.1 or newer, and winget carries 2.3.1, so the repository's checks
   would fail until Microsoft publishes 2.5 there.

### The winget package

`build/winget/` holds the three manifests winget expects: the version
(`OliverD25.CabinetOS.yaml`), the installer and the `en-US` locale, in
manifest schema 1.10.0. The package is the zip as a portable app
(`InstallerType: zip`, `NestedInstallerType: portable`, with
`ArchiveBinariesDependOnPath: true`): winget unpacks it into its own
folder and puts that folder on the PATH, because `CabinetOS.exe` needs the
files next to it; `cabinetos-cli.exe` and `cab.exe` are listed as its
commands. A winget install gets no Start Menu entry and no indexer
service; `install.ps1` does those. The committed files carry 0.1.0 and a
zero hash; `release.ps1` fills in the real ones. `winget validate` accepts
them (checked 2026-09-28 with winget 1.29.380).

## Known gaps

- An all-users install does not update itself: it keeps the installer or
  winget (ADR 0014). The in-app update replaces the files, not the indexer
  service, which only an all-users install has.
- The indexer service starts manually, so it is off after each restart of
  Windows until started again.
- x64 only; ARM64 is untested.
- The setup file installs no prerequisite, by decision (Phase 22, unit 5):
  it names the winget commands and stops. It is per user only (no
  all-users install, no indexer service, no PATH entry: `install.ps1` does
  those), and unsigned like the rest.
- The setup does not remove files an earlier version had and the new one
  lacks when it installs over an earlier setup install; the in-app update,
  which replaces the whole folder, does.
