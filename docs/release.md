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
("The setup file", below). The zip has its files at its root:

| Part | What it is |
|---|---|
| `CabinetOS.exe` and the libraries next to it | The window: .NET 10, WinUI 3, published framework-dependent and compiled ahead of time (ReadyToRun) |
| `CabinetOS.pri` | The window's compiled XAML; without it the window cannot start |
| `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` | The Rust programs. The window's launcher finds the core next to `CabinetOS.exe` ([ui.md](ui.md)) |
| `cab.exe` | `cabinetos-cli.exe` once more, under a short name to type: `cab jobs`, `cab undo --last`. The same program, byte for byte; everything else keeps the name `cabinetos-cli` (the docs, the help text, the tests) |
| `*.pdb` | Symbols, so crash traces name file and line |
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

Measured on 2026-09-28 for 0.1.0: 83 files, 246 MB unpacked, a 77 MB zip.
The Rust `.pdb` files are 38 MB of the zip, and the Windows App SDK's AI and
machine-learning libraries (`onnxruntime.dll`, `DirectML.dll` and their
projections), which CabinetOS does not use, another 17 MB.

ReadyToRun (since 2026-10-01) makes the window's folder bigger and its start
shorter. The window's own files grow from 41 to 57 MB unpacked (45 files
either way; `Microsoft.WinUI.dll` from 7 to 16 MB, `CabinetOS.Core.dll` from
1.6 to 3.8 MB), and zipped that part grows from 11.8 to 17.0 MB. So the
release zip is about 5 MB bigger than the 77 MB above, and the unpacked
folder about 16 MB bigger. The start is 0.14 to 0.24 s shorter
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
3. Copies the three programs and their `.pdb` files, `cabinetos-cli.exe`
   once more as `cab.exe`, the themes, Markdown Preview, `LICENSE` and the
   two install scripts; copies the committed `CabinetOS.ico` (made from the
   design's PNG size cuts; see "The icon"); writes `release.json`
   from the publish output and the Windows App SDK package (the minimum
   Windows App Runtime is the one the SDK's bootstrapper asks for).
4. `build\notices.ps1` writes `THIRD-PARTY-NOTICES.md`: every Rust crate the
   three programs are built from (from `cargo metadata`, normal and build
   dependencies for Windows x64), the Rust standard library, every NuGet
   package in the published `CabinetOS.deps.json`, the .NET application host
   and the bundled JavaScript, with their license files. It stops when a
   component has no license text.
5. Zips the folder, writes the hash, fills the winget manifests.
6. Writes the in-app update's two files for the channel (`-Channel`,
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
7. Compiles `build\setup.iss` with Inno Setup 6.7 or newer (`ISCC.exe` on
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
  For after signing (below).
- `-NoSetup`: no setup file (step 7).
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
  App Runtime, Microsoft's installer). The setup downloads nothing.
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

## Publish

The steps that put 0.1.0 in front of people, in order. Each one is
outward-facing, so they are the creator's: no session runs them. Prepared on
2026-10-02 in Phase 22, unit 6
([report](log/2026-10-02/public-repository-preparation.md)); none of them
has run yet. Paste each block whole into the WSL terminal. A block stops at
its first failing command.

Signing is not a step for 0.1.0: no certificate exists, and code signing
(the SignPath card on the desk) comes after the repository is public. If a
certificate exists by then, sign between steps (b) and (c) as "Sign" above
says.

### Before the flip: the e-mail address in every commit

The scan of the whole history found no secret ([report](log/2026-10-02/public-repository-preparation.md)).
One private thing becomes public with the history: every commit (759 on
`main` on 2026-10-02) carries the author's e-mail address, `muzexp@gmail.com`.
Anyone who clones the repository, or opens a commit's `.patch` page on
GitHub, sees it.
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
gh repo edit OliverD25/cabinetos --visibility public --accept-visibility-change-consequences && gh repo view OliverD25/cabinetos --json visibility --jq .visibility && curl -s -o /dev/null -w 'without login: HTTP %{http_code}\n' https://github.com/OliverD25/cabinetos
```

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

The manifests in `dist\winget\0.1.0\` carry the zip's address and hash.
They go to the community repository `microsoft/winget-pkgs` as a pull
request under `manifests/o/OliverD25/CabinetOS/0.1.0/`. First check that
winget has the Windows App Runtime 2.5: the manifest depends on 2.5.1 or
newer, and winget-pkgs' checks fail without it (only 2.3.1 on 2026-09-28).

```bash
winget.exe show --id Microsoft.WindowsAppRuntime.2 --versions | head -8
```

Only when a 2.5 version is listed:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && winget.exe install --id Microsoft.WingetCreate --exact && wingetcreate.exe submit "$(wslpath -w dist/winget/0.1.0)"
```

What it changes: a pull request in your name at `microsoft/winget-pkgs`
(`wingetcreate` asks you to sign in to GitHub the first time). After
Microsoft's checks and a review, `winget install OliverD25.CabinetOS` works.
Check: the pull request's page, which `wingetcreate` prints. Undo: close the
pull request before it is merged.

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
- The window's project references the whole Windows App SDK, so the
  release carries its AI and machine-learning libraries (about 40 MB
  unpacked) that CabinetOS does not use.
- The setup file installs no prerequisite: it names the winget commands
  and stops. It is per user only (no all-users install, no indexer
  service, no PATH entry: `install.ps1` does those), and unsigned like the
  rest.
- The setup does not remove files an earlier version had and the new one
  lacks when it installs over an earlier setup install; the in-app update,
  which replaces the whole folder, does.
