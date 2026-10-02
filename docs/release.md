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
unseen. Tried on the scratch publish above (the whole package), the check
names 11 of the 15 files: the ML ones; the Search, Widgets and imaging
projections and `System.Numerics.Tensors.dll` come along with them and are
small.

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
<<<<<<< HEAD
=======
- `dist\CabinetOS-<version>-win-x64-symbols.zip` and its `.sha256`, the
  `.pdb` files ("The symbols", below);
- `dist\winget\<version>\`, the winget manifests with this version and the
  zip's SHA-256, checked with `winget validate` when winget is installed;
>>>>>>> phase22-merge
- `dist\update\<channel>\latest.json` and `notes-<version>.md`, what the
  in-app updater reads ("Updates", below);
- `dist\CabinetOS-<version>-win-x64-setup.exe` and its `.sha256`, the
  setup file ("The setup file", below);
- `dist\winget\<version>\`, the winget manifests with this version and the
  setup file's address and SHA-256, checked with `winget validate` when
  winget is installed ("The winget package", below).

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
<<<<<<< HEAD
5. Zips the folder and writes the hash.
6. Writes the in-app update's two files for the channel (`-Channel`,
=======
5. Moves every `.pdb` file out of the release folder into the symbols
   zip and writes its hash ("The symbols"), then checks the folder: the
   script stops when it holds `onnxruntime`, `DirectML` or an AI or
   machine-learning library of the Windows App SDK ("Sizes"). Nothing is
   zipped before this check.
6. Zips the folder, writes the hash, fills the winget manifests.
7. Writes the in-app update's two files for the channel (`-Channel`,
>>>>>>> phase22-merge
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
   defines (the version, the runtimes, the installer addresses of the
   Windows App Runtime and of the .NET runtime that `release.json` names),
   into `dist\CabinetOS-<version>-win-x64-setup.exe`, and writes its
   SHA-256. Without Inno Setup the step is skipped with a warning that
   names the command that installs it, and the rest of the release is
   complete but for the winget manifests:
   `winget install --id JRSoftware.InnoSetup --exact --scope user`.
8. Fills the winget manifests of `build\winget` with the version, the
   setup file's address on the GitHub Release and its SHA-256, and the
   build's date, and checks them with `winget validate` when winget is
   installed. Without a setup file there are none, with a warning.

Switches:

- `-SyncVersion`: `ui\Directory.Build.props` must carry the Cargo version;
  without this switch the script stops when they differ, with this switch it
  writes the Cargo version there first (commit that change).
- `-PackageOnly`: builds nothing; zips the existing release folder again and
  writes a new hash, new manifests, new update files and a new setup file.
<<<<<<< HEAD
  For after signing (below).
- `-NoSetup`: no setup file (step 7), and so no winget manifests.
- `-WingetOnly`: builds nothing; only step 8, from the files already in
  `dist\`: the release folder's `release.json` (the version, and the date
  of `builtUtc`) and the setup file's `.sha256` (the hash, written in
  capitals as winget wants it). It stops with a message when one of them,
  or the setup file, is missing, or when the `.sha256` does not match the
  setup file. For a setup file that was signed after the build ("Sign",
  below).
=======
  For after signing (below). Step 5 still checks the folder, and the
  symbols zip of the build before stays as it is (signing does not change a
  `.pdb` file).
- `-NoSetup`: no setup file (step 8).
>>>>>>> phase22-merge
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
users: a double-click, no script, no administrator rights for CabinetOS
itself ([ADR 0018](decisions/0018-setup-file-and-silent-updates.md)), and
it installs a missing prerequisite itself
([ADR 0019](decisions/0019-setup-installs-prerequisites-and-is-the-winget-package.md)).
It is `build\setup.iss`, Inno Setup 6.7 around the release folder,
compiled by step 7 of the build. It is also the winget package ("The
winget package", below).

- **Where it installs.** For the current user only, into
  `%LOCALAPPDATA%\Programs\CabinetOS`, the folder `install.ps1` uses, so
  the install updates itself ("Updates", below). The folder cannot be
  chosen in the wizard.
- **What it asks.** Almost nothing: an unchecked "Create a desktop
  shortcut", then "Start CabinetOS", checked, at the end. A Start Menu
  shortcut is always made. The wizard follows Windows' light or dark mode.
- **The prerequisites** (Windows 11 22H2 or newer, x64; the .NET 10
  runtime; the Windows App Runtime 2.5.1 or newer; WebView2) are checked
<<<<<<< HEAD
  before the wizard, as `install.ps1` checks them. When one of the last
  three is missing, an interactive setup first says which ones, that it now
  downloads and installs them from Microsoft (a few minutes: the Windows
  App Runtime's installer alone is about 120 MB), and, for .NET, that
  Windows will ask for administrator rights; OK goes on, Cancel stops.
  Then, for each missing one in this order, it downloads Microsoft's
  installer into its temporary folder, runs it silently, waits, deletes it
  and checks again:

  | Prerequisite | Installer (a link to Microsoft's newest build) | Switches | Installs |
  |---|---|---|---|
  | Windows App Runtime | `https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe` | `--quiet` | for the user |
  | WebView2 Runtime | `https://go.microsoft.com/fwlink/p/?LinkId=2124703` | `/silent /install` | for the user (for the PC when the setup runs elevated) |
  | .NET 10 runtime | `https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe` | `/install /quiet /norestart` | for the whole PC, through Windows' administrator prompt (UAC) |

  A silent setup (`/SILENT` or `/VERYSILENT`) that is not elevated does not
  try the .NET runtime, because nobody is there to answer the
  administrator prompt: it stops and names the winget command. One still
  missing after its installer stops the setup with a message that names
  the winget command (and, for the Windows App Runtime, Microsoft's
  installer), and what the setup tried: the installer's exit code, the
  failed download, or why it did not try. No hash is pinned: the links'
  bytes change with each runtime release (ADR 0019).
- **The log** (`/LOG=<file>`) names each step, for example:

  ```text
  downloading https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe
  downloaded dotnet-runtime-setup.exe: 30663104 bytes, SHA-256 <64 hex digits>
  running dotnet-runtime-setup.exe /install /quiet /norestart
  Microsoft's installer ended with exit code 0
  ```
=======
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
>>>>>>> phase22-merge
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
  began (a prerequisite still missing: the log says which, and why); 7
  means it refused the folder (an install.ps1 install there).
  `/SKIPPREREQUISITECHECK` installs even when a prerequisite looks missing,
  and downloads nothing. A silent setup does not start CabinetOS at the
  end. Elevated (from a PowerShell started with "Run as administrator"), a
  silent setup also installs a missing .NET runtime; CabinetOS itself
  still goes into that user's `%LOCALAPPDATA%`.
- **The download test.** `/PREREQTEST=1` only downloads the three
  installers, logs their sizes and hashes, deletes them and exits with
  code 1, having installed nothing, CabinetOS included. It checks the
  download code and Microsoft's links on any PC, this one too:

  ```powershell
  # PowerShell - the setup is a Windows program
  & "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\dist\CabinetOS-0.1.1-win-x64-setup.exe" /VERYSILENT /SUPPRESSMSGBOXES /LOG="$env:TEMP\cabinetos-prereqtest.log" /PREREQTEST=1; Select-String -Path "$env:TEMP\cabinetos-prereqtest.log" -Pattern 'PREREQTEST|download'
  ```

  The last line says "3 of 3 downloads succeeded; the setup exits without
  installing anything".
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
`CabinetOS.pdb` and `CabinetOS.Core.pdb` (the window's; 0.4 and 0.7 MB). It is
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
| Windows App Runtime | `Microsoft.WindowsAppRuntime.2` | Only 2.3.1, older than the 2.5.1 CabinetOS needs (still so on 2026-10-02). Until winget has 2.5, `install.ps1` also prints Microsoft's installer: https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe. The setup file runs that installer itself |
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
| `-Indexer` | Also installs and starts the indexer service (`cabinetos-indexer --install`). Needs `-AllUsers` and a folder inside Program Files: the service runs as LocalSystem, so its program must sit where only administrators can change it. The service has an automatic, delayed start: Windows starts it by itself after every restart ("The indexer service", below) |
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

## The indexer service

The indexer (`cabinetos-indexer.exe`, [indexer.md](indexer.md)) is optional:
without it, search walks one folder tree itself. It runs as a Windows
service named `cabinetos-indexer`, as LocalSystem, and only after someone
installed it: `install.ps1 -AllUsers -Indexer` does it for an all-users
install, and `cabinetos-indexer --install` does it by hand, from a terminal
started with "Run as administrator" (one UAC prompt,
[ADR 0009](decisions/0009-packaging.md)).

- **It starts by itself.** `--install` registers the service as automatic
  with a delayed start, and starts it once
  ([ADR 0020](decisions/0020-indexer-service-starts-by-itself.md)). After
  every restart of Windows it comes up on its own, a little after the other
  automatic services (about two minutes after the boot), so building the
  index does not slow down the start of Windows. `sc qc cabinetos-indexer`
  says `START_TYPE : 2 AUTO_START (DELAYED)`. Until 2026-10-02 the start
  was manual, and the service stayed off after each restart until someone
  ran `sc start`. A service an earlier build registered with a manual start
  is replaced with `--uninstall` and then `--install`.
- **`--uninstall` is the reverse**: it stops the service, waits up to 30
  seconds for it, and deletes it. `uninstall.ps1` does the same for an
  install made with `-Indexer`.
- **A service that does not run** after its first start makes `--install`
  say so, with the state it found, and exit with an error. The service
  stays registered, and its log in `%ProgramData%\CabinetOS\logs` says
  why.
- **Tests.** A unit test in the indexer crate reads what `--install` asks
  the service manager for (start type automatic, the delayed flag,
  LocalSystem, the command line) without touching it. The test that installs
  a real service (`tests/elevated.rs`, ignored, CI only) checks `sc qc` and
  `sc query` after `--install`, with no `sc start` in between.
- **Checked in the VM, by hand once.** `ui\livecheck\vm-indexer-check.ps1`
  shows in the VirtualBox VM that the service comes up by itself after
  Windows restarts (below).

**The restart check in the VM.** The VM's user is an administrator, but
VirtualBox's guest control gives that user's token with UAC applied
(medium integrity), and installing a service needs the full token. Seen
2026-10-02: `Register-ScheduledTask` with the highest run level answers
"Access is denied", and nothing can answer a UAC prompt from outside the
VM. So the check has one manual step, which a person does in the VM, and
the rest runs by script:

1. On this PC, with a release built (`build\release.ps1`):

   ```powershell
   # PowerShell - the VM is VirtualBox's
   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-indexer-check.ps1
   ```

   It starts the VM (headless) when it is off, lets it settle, tries the
   install through guest control, finds that it is not elevated, and writes
   `_io\live-check\DONE-indexer.md` with the manual step; exit code 2.
2. In the VM, in a Windows PowerShell started with "Run as administrator"
   (to see the VM, stop the headless one with
   `VBoxManage controlvm CabinetOS-LiveCheck acpipowerbutton` and start it
   with a window: `VBoxManage startvm CabinetOS-LiveCheck --type separate`;
   the user and password are in `_io\vm\vm-user.txt`):

   ```powershell
   powershell -ExecutionPolicy Bypass -File \\VBoxSvr\cabinetos\_io\indexer-check\vm-indexer-guest.ps1 -Phase install
   ```

   It copies the release folder to `C:\Program Files\CabinetOS-indexer-check`,
   runs `cabinetos-indexer --install` there, and says whether `sc qc`
   reads `AUTO_START (DELAYED)` and `sc query` reads `RUNNING`.
3. Back on this PC, with the VM running:

   ```powershell
   # PowerShell
   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-indexer-check.ps1 -AfterInstall
   ```

   It reads the service's state, restarts Windows inside the VM
   (`shutdown /r`), waits for the desktop and for the VM to settle, and then
   runs the read-only `-Phase check` in the VM: nobody starts the service,
   and the check records when it was first `RUNNING`, how long after the
   boot began, that its process is younger than the boot, that its pipe
   `\\.\pipe\cabinetos-indexer` exists, and the last lines of its log.
   `DONE-indexer.md` has the times. Exit code 0 when all of it holds.
4. To remove the service again, in an elevated PowerShell in the VM:
   `... vm-indexer-guest.ps1 -Phase uninstall`.

**What was seen on 2026-10-02, and what was not.** The guest session's
token was checked first: `whoami /groups` in a guest-control session says
medium integrity and "Group used for deny only" for Administrators
(`EnableLUA=1`, `ConsentPromptBehaviorAdmin=5`), so run 1 ends at its first
phase: "elevated: False", state `needs-elevation`, exit code 2 (at 21:53,
10 s after the phase started). The install phase itself, and so the proof
that the service is `RUNNING` after a restart, were **not run**: nobody was
at the VM to answer UAC, and nothing was changed in the VM's security
settings to avoid the prompt. The rest of the script was tried with no
service installed (`-AfterInstall -SkipInstalledCheck`), and found what the
real run will meet:

- `shutdown /r` through guest control works: at 21:59 the VM went down
  (run level 0 at 21:59:34) and was back at run level 3 at 22:00:00. But
  guest control often answers "Error starting guest session (current status
  is: starting)" for many minutes after a restart of the VM, and at 22:28
  `shutdown /r` could not even start for that reason. The script then resets
  the VM (`controlvm reset`; 22:30:59, back at run level 3 at 22:31:24),
  retries the start of a phase for up to 20 minutes, and writes
  `DONE-indexer.md` with what it did even when it stops. `-Restart` restarts
  the VM first, the one cure seen for a stuck guest control.
- the CPU-load answer that ends the settle wait often does not come in time
  right after a boot (guest control is slow then), so the wait runs to its
  limit (`-SettleMinutes`, 30 by default).
- the read-only check phase works: it reported the boot time, the service
  "not installed" and `sc qc` 1060, polled for its minute and failed, as it
  must when no service exists.

The success branch (the service `RUNNING` with a process younger than the
boot, its pipe and its log) is therefore not yet shown in a real run. The
manual step above, done once by a person, will show it.

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
Then zip the signed folder again; the hash, the setup file and the winget
manifests change with it:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/release.ps1 -PackageOnly
```

Sign the new setup file with the same `signtool` line afterwards. Its
bytes change, so write its hash again and then the winget manifests, which
carry that hash (`-WingetOnly` stops when the two differ):

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/dist && sha256sum CabinetOS-0.1.1-win-x64-setup.exe > CabinetOS-0.1.1-win-x64-setup.exe.sha256 && cd .. && pwsh.exe -NoProfile -File build/release.ps1 -WingetOnly
```

### Apply to SignPath Foundation (the creator's steps)

Prepared on 2026-10-02 from the application form at
<https://signpath.org/apply> (sixteen fields and three consent boxes, read
that day) and from the conditions at <https://signpath.org/terms> (their
"Code of conduct", marked a draft on their side). The application is
outward-facing: the creator fills and submits it; no session does.

**What the program gives, and the two answers.** SignPath.io signs the
files that a GitHub Actions workflow built, with a certificate issued to
SignPath Foundation. Windows then shows "SignPath Foundation" as the
verified publisher in the SmartScreen and UAC dialogs and in the file's
properties; the product name in the file details stays "CabinetOS". Their
terms: "The code signing certificate is issued to SignPath Foundation. This
means that SignPath Foundation is the publisher of the OSS project."

- **The signer shown to users is "SignPath Foundation": accepted** (the
  planning session's decision of 2026-10-02, on the creator's word to
  decide it). The free program has no other form, and the alternatives
  cost money: Azure Trusted Signing, about 10 US dollars a month after an
  identity check, or an OV certificate on a hardware token, 100 to 400 US
  dollars a year, which must earn SmartScreen's trust first. Undo: switch
  to an own certificate later; only the signing step changes.
- **A second approver: none is needed.** The terms ask for roles, not for a
  head count: "each signing request must be approved by a team member
  trusted by the entire team to decide if a certain release can be code
  signed". Nothing forbids one person holding all three roles (Authors,
  Reviewers, Approvers). The creator, `OliverD25`, holds all three. If
  SignPath asks for a second person during the review, it must be a human
  with a GitHub account and two-factor authentication; a Claude session
  cannot be an approver.

**The conditions that matter for CabinetOS** (from the terms):

- "Released: The project must already be released in the form that should
  be signed." No GitHub Release exists yet, so the application comes after
  steps (b) and (c) of "Publish" below.
- Reputation. The form's "Reputation" field is required, and the terms
  say: "For executable programs that may be downloaded and executed based
  on our signature, we require a certain verifiable reputation." On
  2026-10-02 the repository had been public for one day, with no star and
  no release. An application without any sign of use may be refused; a
  refusal costs nothing but a new application later. The recommendation:
  apply after the 0.1.0 release and the first public posts about it (a
  "Show HN" post, a Reddit post, a blog article), and name them in the
  field.
- "All team members must use multi-factor authentication for both
  SignPath and source code repository access (e.g. GitHub)." Step 1.
- A "Code signing policy" with fixed wording on the home page and the
  download page. Step 3.
- "All signed binaries must have metadata attributes set and enforced":
  the product name "CabinetOS" and one version in every signed file.
  `CabinetOS.exe` and its libraries have them (`ui/Directory.Build.props`);
  the four Rust programs (`cabinetos-core.exe`, `cabinetos-indexer.exe`,
  `cabinetos-cli.exe`, `cab.exe`) carry no version resource yet. A session
  adds one per program (a `build.rs` with a version resource) before the
  first signing request.
- The rest holds already: MIT without dual licensing; no proprietary
  component (the Microsoft libraries in the zip are signed upstream
  binaries, which the terms allow in signed packages); an uninstaller; the
  indexer service's install asks before it changes the system; the
  program's function is described in the README and in the release notes.

**Step 1. Two-factor authentication on GitHub.** Open
<https://github.com/settings/security>. Under "Two-factor authentication"
it must say that it is enabled. If not: "Enable two-factor authentication",
with an authenticator app (Microsoft Authenticator, or the password
manager's one), and keep the recovery codes. A session cannot read this
status: the `gh` token lacks the scope for it.

**Step 2. The 0.1.0 release.** Steps (b) and (c) of "Publish", after Phase
22's units 1 to 3 are on `main`. The release page
<https://github.com/OliverD25/cabinetos/releases> is the form's "Download
URL".

**Step 3. The "Code signing policy" section.** Before the form is
submitted, the README gets the section below, and the release notes of
0.1.0 get one line, "Code signing policy: see the README", so that the
download page carries the term too (the terms: "Use the term 'Code signing
policy' on your project's home page and download/release pages"). A
session adds both on the creator's word; it is a commit to the public
repository. If SignPath refuses the application, one commit removes them.

```markdown
## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io),
certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [OliverD25](https://github.com/OliverD25)
- Approvers: [OliverD25](https://github.com/OliverD25)

Privacy: this program sends no information about the user or the system
anywhere. Its one automatic network request is the daily update check,
which downloads `latest.json` from the marketplace site and carries nothing
else; `update.check: false` in the settings turns it off, and "Update:
Check for Updates" in the palette checks on demand.
```

**Step 4. The form**, <https://signpath.org/apply>, field by field (an
asterisk marks a required field). Everything that is not a link or a name
is a proposal; change it freely.

1. Project Name\*: `CabinetOS`. (Their note: "A Google search for this
   name should clearly identify your project.")
2. Repository URL\*: `https://github.com/OliverD25/cabinetos`.
3. Homepage URL\*: `https://github.com/OliverD25/cabinetos`. The
   repository page is allowed ("This can be a dedicated website or the
   repository page"); the landing page replaces it once it exists.
4. Download URL: `https://github.com/OliverD25/cabinetos/releases`.
   Their note: "This page must mention that the project uses the SignPath
   Foundation for code signing" (step 3's line in the release notes).
5. Privacy Policy URL:
   `https://github.com/OliverD25/cabinetos#code-signing-policy` (step 3's
   section; the program collects no user data).
6. Wikipedia URL: empty.
7. Tagline\*: `A fast dual-pane file manager for Windows 11 with a Rust
   core, a WinUI 3 window, WebAssembly plugins and a command palette.`
8. Description\* (their note: a short paragraph, no version-specific
   features): `CabinetOS is an open-source (MIT) file manager for Windows
   11 in the dual-pane tradition of Total Commander, built for the
   keyboard: every action is a named command in a searchable command
   palette and can be bound to a key or a key chord. A Rust core does all
   file work off the UI thread; a WinUI 3 window with Fluent Design and
   Mica shows it. Extensions are WebAssembly plugins in capability
   sandboxes, installed from a marketplace, and the core ships without
   them. A single maintainer develops it in the open, with the plan, the
   decisions and the daily build log in the repository.`
9. Reputation\*: only what is true on the day. A frame: `The project is
   new: public on GitHub since 2026-10-02, first release 0.1.0 on <date>.
   Signs of use so far: <stars and forks>, <release download counts>,
   <links to posts or articles>. The repository holds the full history
   (over 750 commits), the governing documents and the build log.`
10. Maintainer Type: `Individual maintainer(s)`. (The options: Independent
    community project (no formal organization); Non-profit foundation or
    research/educational institution; For-profit company or
    corporate-backed project; Individual maintainer(s); Other.)
11. Build System\*: `GitHub Actions`. (The only other option is GitLab
    CI/CD.)
12. First Name\* and Last Name\*: the creator's real name; SignPath creates
    the user account with it.
13. Email\*: the address for the SignPath account; the review's e-mails go
    there.
14. Company Name: empty.
15. Primary Discovery Channel\*: whatever is true (Organic search; AI /
    LLM tools; Developer platforms (e.g. GitHub); Community platforms;
    Social media; Events; Referral; Direct contact; Other), and the
    optional "Please specify the exact source".
16. The three boxes: "I have read and agree to the SignPath Foundation
    Code of Conduct ..." (required; it links to the terms, read them
    once), "I agree to receive other communications from SignPath"
    (optional; leave it empty), "I agree to allow SignPath to store and
    process my personal data" (required). Then the reCAPTCHA and Submit.

**Step 5. After the submission.** SignPath reviews by hand; their site
names no time. Questions and the answer come to the e-mail of the form. On
acceptance: an invitation to <https://app.signpath.io>, two-factor
authentication there, and the organization with the project appears. Then
the session's part, on the creator's word (the SignPath card on the desk):
the trusted build system "GitHub.com" linked to the project; an artifact
configuration for the release zip with metadata restrictions (product name
CabinetOS, one version); a release-signing policy with manual approval and
a test-signing policy; an API token stored as the repository secret
`SIGNPATH_API_TOKEN`; the release job in `release.yml` with
`actions/upload-artifact` and `signpath/github-action-submit-signing-request`;
the version resources of the four Rust programs; and the updater's
Authenticode check (ADR 0014). Every release signing needs the creator's
approval in SignPath's web UI: "Every release needs manual approval for
signing."

## Publish

The steps that put 0.1.0 in front of people, in order. Each one is
outward-facing, so they are the creator's: no session runs them. Prepared on
2026-10-02 in Phase 22, unit 6
([report](log/2026-10-02/public-repository-preparation.md)). Step (a) ran on
2026-10-02 at 21:36, and the creator's word of the same evening ("do this by
yourself") handed steps (b) and (c) to the planning session, which runs them
once Phase 22's units 1 to 3 are on `main`; (d) and (e) stay the creator's
until they say otherwise. Paste each block whole into the WSL terminal. A
block stops at its first failing command.

Signing is not a step for 0.1.0: no certificate exists, and code signing
(the SignPath card on the desk) comes after the repository is public. If a
certificate exists by then, sign between steps (b) and (c) as "Sign" above
says.

### Before the flip: the e-mail address in every commit

The scan of the whole history found no secret ([report](log/2026-10-02/public-repository-preparation.md)).
One private thing becomes public with the history: every commit (759 on
`main` on 2026-10-02) carries the author's personal e-mail address, the
one the git settings on this PC and in WSL give. Anyone who clones the
repository, or opens a commit's `.patch` page on GitHub, sees it.
Only a rewrite of the whole history removes it, and that would change every
commit id the plan and the build log cite. The report recommends keeping the
history and giving new commits GitHub's private address:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && git config user.email 197441449+OliverD25@users.noreply.github.com && git config user.email
```

What it changes: the repository's own git settings (`.git/config`, which the
Windows git, the WSL git and every worktree on this PC share), so new
commits made here carry the private address. Old commits keep theirs. The
laptop's and the homelab's clones have settings of their own. Check: the
block prints the new address. Undo: `git config --unset user.email` in the
same folder.

### (a) Make the repository public

```bash
(gh repo edit OliverD25/cabinetos --visibility public --accept-visibility-change-consequences 2>/dev/null || gh repo edit OliverD25/cabinetos --visibility public) && gh repo view OliverD25/cabinetos --json visibility --jq .visibility && curl -s -o /dev/null -w 'without login: HTTP %{http_code}\n' https://github.com/OliverD25/cabinetos
```

The first form is for `gh` 2.48 and newer, which refuse to change the
visibility without that flag; an older `gh` (Ubuntu's 2.45 in WSL on
2026-10-02) refuses the flag instead, so the second form runs then.

What it changes: anyone can read the repository: the code, the whole
history of `main` (the only branch on GitHub), and the Actions runs with
their logs. Anyone can fork it, open issues and propose pull requests.
GitHub Actions on GitHub's own runners costs no minutes for a public
repository, so CI can run again for free. Nothing starts by itself: `ci.yml`
runs only on a pull request into `main` that touches code, or by hand;
`release.yml` only by hand; no workflow runs on a push or on a schedule.
Undo: the same command with `--visibility private`; copies made in between
stay with whoever made them.

Check: the block prints `PUBLIC` and `without login: HTTP 200`. In a private
browser window, https://github.com/OliverD25/cabinetos shows the README and
"MIT license".

Optional, any time after (a): the line under the repository's name, the
topics GitHub's search uses, and a "Report a vulnerability" button in the
Security tab, so security problems reach you privately instead of in a
public issue:

```bash
gh repo edit OliverD25/cabinetos --description "Modern System Commander: a fast dual-pane file manager for Windows 11, with a Rust core, a WinUI 3 window, WebAssembly plugins and a command palette." --add-topic file-manager,dual-pane,orthodox-file-manager,total-commander,windows,windows-11,winui3,fluent-design,rust,csharp,dotnet,webassembly,wasmtime,plugins,command-palette,keyboard-driven && gh api -X PUT repos/OliverD25/cabinetos/private-vulnerability-reporting && gh repo view OliverD25/cabinetos --json description,repositoryTopics
```

Undo: `--remove-topic <topic>`, and
`gh api -X DELETE repos/OliverD25/cabinetos/private-vulnerability-reporting`.

### (b) After Phase 22's other units: the release notes and the build

Wait until units 1 to 5 of Phase 22 are on `main` (the plan's Phase 22
status says so). 0.1.0 was never published, so everything under
`## [Unreleased]` in `CHANGELOG.md` belongs to it. The first block moves
those lines into the `## [0.1.0]` section, under its `### Added`,
`### Changed`, `### Removed` and `### Fixed` headings, dates the section
today, and leaves an empty `## [Unreleased]` above it. It changes only
`CHANGELOG.md` on this PC, and stops without a change when the working tree
is not clean (it lists what is not) or Unreleased is empty.

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && git switch main && git pull --ff-only && { test -z "$(git status --porcelain)" || { git status --short; false; }; } && RELEASE_DATE=$(date +%F) python3 - <<'EOF' && git --no-pager diff --stat && git --no-pager diff CHANGELOG.md | head -60
import os, pathlib, re
p = pathlib.Path("CHANGELOG.md"); t = p.read_text(encoding="utf-8")
m = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)^## \[0\.1\.0\][^\n]*\n(.*?)(?=^## \[|^\[Unreleased\]: |\Z)", t, re.M | re.S)
if not m: raise SystemExit("CHANGELOG.md: no ## [Unreleased] above ## [0.1.0]; nothing changed")
def parts(body):
    intro, *rest = re.split(r"^### ", body, flags=re.M); secs = {}
    for r in rest:
        head, _, text = r.partition("\n"); secs.setdefault(head.strip(), []).append(text.strip())
    return intro.strip(), secs
ui, us = parts(m.group(1)); vi, vs = parts(m.group(2))
if not us: raise SystemExit("CHANGELOG.md: ## [Unreleased] is empty; nothing changed")
order = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"]
heads = [h for h in order if h in vs or h in us] + [h for h in [*vs, *us] if h not in order]
out = ["## [Unreleased]", "", "## [0.1.0] - " + os.environ["RELEASE_DATE"], ""]
for x in (vi, ui):
    if x: out += [x, ""]
for h in dict.fromkeys(heads):
    out += ["### " + h, ""] + [y for x in vs.get(h, []) + us.get(h, []) if x for y in (x, "")]
p.write_text(t[:m.start()] + "\n".join(out) + "\n" + t[m.end():], encoding="utf-8", newline="\n")
print("CHANGELOG.md: Unreleased moved into ## [0.1.0] - " + os.environ["RELEASE_DATE"])
EOF
```

Read the diff. The `### Changed` and `### Fixed` items describe changes
made since 2026-09-30, before anyone had the program; delete the ones that
mean nothing to a first user, if you like, before the next block. Undo
before the commit: `git restore CHANGELOG.md`.

The second block commits and pushes the CHANGELOG and builds the release
from that commit:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && git add CHANGELOG.md && git commit -m "CHANGELOG: everything since 2026-09-30 is part of 0.1.0, the first version that is published" && git push origin main && pwsh.exe -NoProfile -File build/release.ps1 && grep -E '"(version|commit|uncommittedChanges)"' dist/CabinetOS-0.1.0-win-x64/release.json && git rev-parse HEAD && ls -l dist/*.zip dist/*.exe dist/*.sha256 && head -8 dist/update/stable/notes-0.1.0.md
```

What it changes: one commit on GitHub's `main`, and the release files in
`dist\` (ignored by git). Check: `release.json` names the commit the block
printed last and `"uncommittedChanges": false`; the build printed no warning
that the notes are the Unreleased section; `dist\` holds the zip, the setup
file and the symbols zip, each with its `.sha256` (Phase 22's goals: the zip
under 60 MB, the setup file under 35 MB); the notes start with "The first
version." Undo: `git revert HEAD && git push origin main` for the commit;
`dist\` is only local.

### (c) Tag and publish the GitHub Release

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && ls dist/CabinetOS-0.1.0-win-x64.zip dist/CabinetOS-0.1.0-win-x64.zip.sha256 dist/CabinetOS-0.1.0-win-x64-setup.exe dist/CabinetOS-0.1.0-win-x64-setup.exe.sha256 dist/CabinetOS-0.1.0-win-x64-symbols.zip dist/CabinetOS-0.1.0-win-x64-symbols.zip.sha256 && grep -q "$(git rev-parse HEAD)" dist/CabinetOS-0.1.0-win-x64/release.json && test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" && git tag -a v0.1.0 -m "CabinetOS 0.1.0" && git push origin v0.1.0 && gh release create v0.1.0 dist/CabinetOS-0.1.0-win-x64.zip dist/CabinetOS-0.1.0-win-x64.zip.sha256 dist/CabinetOS-0.1.0-win-x64-setup.exe dist/CabinetOS-0.1.0-win-x64-setup.exe.sha256 dist/CabinetOS-0.1.0-win-x64-symbols.zip dist/CabinetOS-0.1.0-win-x64-symbols.zip.sha256 --repo OliverD25/cabinetos --verify-tag --title "CabinetOS 0.1.0" --notes-file dist/update/stable/notes-0.1.0.md
```

The first three commands stop the block when a file is missing, when the
build is not of the current commit, or when that commit is not on GitHub's
`main`. The symbols zip's name is the one Phase 22's unit 2 plans; when
`ls` says it is missing, compare with `ls dist/` and fix the two names.

What it changes: the tag `v0.1.0` on GitHub, and the release page
https://github.com/OliverD25/cabinetos/releases/tag/v0.1.0 with the notes
and the six files, marked Latest: the first thing anyone can download.
Undo: `gh release delete v0.1.0 --repo OliverD25/cabinetos --cleanup-tag --yes && git tag -d v0.1.0`.
Downloads made in between stay out there, so a fixed build gets a new
version (0.1.1), never the same name with other bytes: installs check the
hash.

Check that the address `latest.json` will point at serves the zip that was
built (the two hashes must be the same):

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && gh release view v0.1.0 --repo OliverD25/cabinetos --json assets --jq '.assets[].name' && curl -sL https://github.com/OliverD25/cabinetos/releases/download/v0.1.0/CabinetOS-0.1.0-win-x64.zip | sha256sum && cat dist/CabinetOS-0.1.0-win-x64.zip.sha256
```

### (d) Tell the installed copies: the marketplace site

The update file lives on the marketplace site: the public repository
`OliverD25/cabinetos-marketplace`, which GitHub Pages serves from the root
of its `main` branch at https://oliverd25.github.io/cabinetos-marketplace/
(checked 2026-10-02; `update/stable/latest.json` answered 404 there). It is
not cloned on this PC (checked 2026-10-02 under `E:\codespace`). The block
clones it, when it is missing, into a product folder of its own,
`E:\codespace\_claude_code\_rde\cabinetos-marketplace\cabinetos-marketplace`
(the standard layout, with `_io` beside it). Run (c) first: `latest.json`
points at the release's zip.

```bash
mkdir -p /mnt/e/codespace/_claude_code/_rde/cabinetos-marketplace/_io && cd /mnt/e/codespace/_claude_code/_rde/cabinetos-marketplace && { test -d cabinetos-marketplace/.git || gh repo clone OliverD25/cabinetos-marketplace; } && cd cabinetos-marketplace && git switch main && git pull --ff-only && mkdir -p update/stable && cp /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/dist/update/stable/latest.json /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/dist/update/stable/notes-0.1.0.md update/stable/ && git add update/stable && git commit -m "update: CabinetOS 0.1.0 on the stable channel, so installed copies find it" && git push
```

What it changes: one commit on the marketplace repository. GitHub Pages
publishes it within a minute or two, and from then on every per-user
install finds 0.1.0 at its next daily check. Undo: `git revert HEAD && git push`
in that folder. A wrong `latest.json` is taken back by pushing the previous
one; installs that already downloaded the bad version keep it until they
roll back.

Check (it waits up to 5 minutes for Pages): the version, the zip's hash,
which must match the `.sha256` printed after it, the zip's address, and
`notes: HTTP 200`:

```bash
for i in $(seq 30); do curl -sf -o /dev/null https://oliverd25.github.io/cabinetos-marketplace/update/stable/latest.json && break; sleep 10; done; curl -s https://oliverd25.github.io/cabinetos-marketplace/update/stable/latest.json | python3 -c 'import json, sys; d = json.load(sys.stdin); print(d["version"], d["zip"]["sha256"], d["zip"]["url"])' && cat /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/dist/CabinetOS-0.1.0-win-x64.zip.sha256 && curl -s -o /dev/null -w 'notes: HTTP %{http_code}\n' https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.1.0.md
```

Then the updater's own view, on a machine that does not run this PC's
development build (the VM, or any other Windows 11 PC): download the setup
file from the release page, install it, start CabinetOS, open its terminal
with Ctrl+`, and run:

```powershell
# PowerShell - in CabinetOS's own terminal, where cab reaches the window's core
cab update check
```

It prints "CabinetOS 0.1.0, stable channel: up to date" and a "last check"
line with the current time: the updater read the published `latest.json`.
"the last step failed: ..." says what went wrong instead.

### (e) Optional, last: winget

The winget package is the setup file
([ADR 0019](decisions/0019-setup-installs-prerequisites-and-is-the-winget-package.md)),
and winget starts with 0.1.1: 0.1.0's setup file does not install the
Windows App Runtime, which winget's catalogue cannot supply in the version
CabinetOS needs. No check of winget's catalogue is needed any more; the
package depends only on the .NET runtime and WebView2, which winget has.

The manifests in `dist\winget\0.1.1\` carry the setup file's address and
hash. They go to the community repository `microsoft/winget-pkgs` as a pull
request under `manifests/o/OliverD25/CabinetOS/0.1.1/`. Run (c) first:
winget-pkgs' checks download the setup file from the release page. The
block writes the manifests again from the files in `dist\` (`-WingetOnly`,
so a setup file signed after the build carries its new hash), stops when
the release page serves other bytes than that hash, installs `wingetcreate`
when it is missing, and submits:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/release.ps1 -WingetOnly && test "$(curl -sL https://github.com/OliverD25/cabinetos/releases/download/v0.1.1/CabinetOS-0.1.1-win-x64-setup.exe | sha256sum | cut -d' ' -f1)" = "$(cut -d' ' -f1 dist/CabinetOS-0.1.1-win-x64-setup.exe.sha256)" && { command -v wingetcreate.exe >/dev/null || winget.exe install --id Microsoft.WingetCreate --exact; } && wingetcreate.exe submit "$(wslpath -w dist/winget/0.1.1)"
```

What it changes: the manifests in `dist\` (local only), and a pull request
in your name at `microsoft/winget-pkgs` (`wingetcreate` asks you to sign in
to GitHub the first time). After Microsoft's checks and a review,
`winget install OliverD25.CabinetOS` works: winget installs the .NET
runtime and WebView2 first when they are missing, then runs the setup
silently for the user, and the setup installs the Windows App Runtime when
it is missing. Check: the pull request's page, which `wingetcreate` prints.
Undo: close the pull request before it is merged.

### A later version

The same steps with the new number, and three differences. Set `version` in
`core/Cargo.toml`, run `pwsh.exe -NoProfile -File build/release.ps1 -SyncVersion`
and commit `ui/Directory.Build.props` with it. In (b), rename
`## [Unreleased]` to `## [<version>] - <date>` by hand instead of the
merge, put a new empty `## [Unreleased]` above it, and add the version's
link line at the end of the file (`[Unreleased]` then compares with the new
tag). A preview (a version such as `0.2.0-preview.1`) builds with
`-Channel preview`, gets `--prerelease` in (c), and uses
`dist/update/preview/` and `update/preview/` in (d).

### The winget package

`build/winget/` holds the three manifests winget expects: the version
(`OliverD25.CabinetOS.yaml`), the installer and the `en-US` locale, in
manifest schema 1.10.0. The package is the setup file
([ADR 0019](decisions/0019-setup-installs-prerequisites-and-is-the-winget-package.md)):
`InstallerType: inno`, `Scope: user`, `UpgradeBehavior: install` (a new
version installs over the old one, as the setup does), and
`ProductCode: CabinetOS_is1`, the setup's Settings > Apps entry, by which
winget recognises an installed CabinetOS. A winget install is the setup's
install: the Start Menu shortcut, the Apps entry and in-app updates; no
PATH entry for `cabinetos-cli` and no indexer service (`install.ps1` does
those). The package depends on `Microsoft.DotNet.Runtime.10` and
`Microsoft.EdgeWebView2Runtime`: winget installs the .NET runtime with the
administrator rights that a silent setup does not ask for. The Windows App
Runtime is no dependency, because the setup installs it. The committed
files carry 0.1.0 and a zero hash; `release.ps1` fills in the version, the
setup file's address and SHA-256 and the build's date, after the setup
file is built, and `-WingetOnly` does that again alone. `winget validate`
accepts them (checked 2026-10-02 with winget 1.29.380 on 0.1.1; it notes
that it does not check the two dependencies).

## Known gaps

- An all-users install does not update itself: it keeps the installer or
  winget (ADR 0014). The in-app update replaces the files, not the indexer
  service, which only an all-users install has.
- x64 only; ARM64 is untested.
<<<<<<< HEAD
- The window's project references the whole Windows App SDK, so the
  release carries its AI and machine-learning libraries (about 40 MB
  unpacked) that CabinetOS does not use.
- The setup file needs the internet when a prerequisite is missing (up to
  about 150 MB from Microsoft), and a silent setup without administrator
  rights cannot install the .NET runtime: it stops and names the winget
  command. It is per user only (no all-users install, no indexer service,
  no PATH entry: `install.ps1` does those), and unsigned like the rest.
=======
- The setup file installs no prerequisite, by decision (Phase 22, unit 5):
  it names the winget commands and stops. It is per user only (no
  all-users install, no indexer service, no PATH entry: `install.ps1` does
  those), and unsigned like the rest.
>>>>>>> phase22-merge
- The setup does not remove files an earlier version had and the new one
  lacks when it installs over an earlier setup install; the in-app update,
  which replaces the whole folder, does.
