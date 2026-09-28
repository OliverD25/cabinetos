# Releases: build, install, sign, publish

How a CabinetOS release is made, installed and removed, and the steps that
are still for the creator: signing and publishing. Why it ships this way:
[ADR 0009](decisions/0009-packaging.md). Commands are for the WSL bash
terminal unless marked `# PowerShell`, as in [dev-setup.md](dev-setup.md).

## What a release is

One folder, zipped as `CabinetOS-<version>-win-x64.zip`, with its files at
the zip's root:

| Part | What it is |
|---|---|
| `CabinetOS.exe` and the libraries next to it | The window: .NET 10, WinUI 3, published framework-dependent |
| `CabinetOS.pri` | The window's compiled XAML; without it the window cannot start |
| `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` | The Rust programs. The window's launcher finds the core next to `CabinetOS.exe` ([ui.md](ui.md)) |
| `*.pdb` | Symbols, so crash traces name file and line |
| `Assets\xterm\` | The terminal page |
| `extras\themes\` | Copies of the four built-in themes and their schema, as a start for your own. The core also writes them into `%LOCALAPPDATA%\CabinetOS\themes` |
| `extras\tools\markdown-preview\` | The Markdown Preview tool. Opt-in (Constitution Article 10): the window does not load it from here |
| `install.ps1`, `uninstall.ps1` | The installer and its undo |
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
  zip's SHA-256, checked with `winget validate` when winget is installed.

What it runs, in order:

1. `cargo build --release --locked -p cabinetos-core -p cabinetos-indexer -p cabinetos-cli`
   in `core\`. `core\.cargo\config.toml` links the C runtime into the
   programs (`+crt-static`), so they do not need the Visual C++
   Redistributable, which a clean Windows 11 lacks.
2. `dotnet publish CabinetOS\CabinetOS.csproj -c Release -r win-x64 --self-contained false -o <release folder>`
   in `ui\`. Framework-dependent: .NET 10 and the Windows App Runtime come
   from the machine. The project keeps the Windows App SDK's MSIX tooling on,
   which writes `CabinetOS.pri`, the compiled XAML, into the publish; without
   it the window stops at start. The app stays unpackaged. The script stops
   if the `.pri` file is missing.
3. Copies the three programs and their `.pdb` files, the themes, Markdown
   Preview, `LICENSE` and the two install scripts; writes `release.json`
   from the publish output and the Windows App SDK package (the minimum
   Windows App Runtime is the one the SDK's bootstrapper asks for).
4. `build\notices.ps1` writes `THIRD-PARTY-NOTICES.md`: every Rust crate the
   three programs are built from (from `cargo metadata`, normal and build
   dependencies for Windows x64), the Rust standard library, every NuGet
   package in the published `CabinetOS.deps.json`, the .NET application host
   and the bundled JavaScript, with their license files. It stops when a
   component has no license text.
5. Zips the folder, writes the hash, fills the winget manifests.

Switches:

- `-SyncVersion`: `ui\Directory.Build.props` must carry the Cargo version;
  without this switch the script stops when they differ, with this switch it
  writes the Cargo version there first (commit that change).
- `-PackageOnly`: builds nothing; zips the existing release folder again and
  writes a new hash and new manifests. For after signing (below).

The build warns when `core`, `ui`, `sdk` or `build` hold uncommitted
changes or new files, and `release.json` records the commit and whether
they were clean.

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
| `-AddToPath` | The install folder on your PATH (the machine's with `-AllUsers`), for `cabinetos-cli`; new terminals see it |
| `-Indexer` | Also installs and starts the indexer service (`cabinetos-indexer --install`). Needs `-AllUsers` and a folder inside Program Files: the service runs as LocalSystem, so its program must sit where only administrators can change it. The service starts manually: after a restart of Windows, start it with `Start-Service cabinetos-indexer` as administrator |
| `-SkipPrerequisiteCheck` | Installs even when a prerequisite looks missing |
| `-WhatIf` | Shows every step and changes nothing |

The script asks nothing. It records what it did in
`.cabinetos-install.json` in the install folder: the files, the shortcut,
the PATH entry, the service. Running it again over the same folder is an
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

It removes exactly what the record lists: the files, the shortcut, the
PATH entry, and the service (stopped first). Files someone else put into
the install folder stay, and so does the folder then. Settings, plugins,
themes and logs stay in `%APPDATA%\CabinetOS` and `%LOCALAPPDATA%\CabinetOS`;
`-RemoveData` deletes those two folders too, and `%ProgramData%\CabinetOS`
(the service's logs) for an install with the indexer. Other users' data
stays. An all-users install needs "Run as administrator" to remove.
`-Destination <folder>` names the install folder when the script runs from
somewhere else.

CabinetOS does not appear in Settings > Apps: that needs a registry entry
or a real installer (ADR 0009).

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
$release = 'E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\dist\CabinetOS-0.1.0-win-x64'; $signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'; $files = 'CabinetOS.exe', 'CabinetOS.dll', 'CabinetOS.Core.dll', 'cabinetos-core.exe', 'cabinetos-indexer.exe', 'cabinetos-cli.exe' | ForEach-Object { Join-Path $release $_ }; & $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <thumbprint> $files; & $signtool verify /pa /v $files; Set-AuthenticodeSignature -FilePath "$release\install.ps1", "$release\uninstall.ps1" -Certificate (Get-Item Cert:\CurrentUser\My\<thumbprint>) -HashAlgorithm SHA256 -TimestampServer http://timestamp.digicert.com
```

The timestamp keeps the signatures valid after the certificate expires.
Then zip the signed folder again; the hash and the winget manifests change
with it:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/release.ps1 -PackageOnly
```

## Publish (not done yet)

Written down, not run. Each step is outward-facing, so it is the
creator's.

1. **Choose the version.** Set `version` in `core/Cargo.toml`, run
   `pwsh.exe -NoProfile -File build/release.ps1 -SyncVersion`, rename
   `## [Unreleased]` in `CHANGELOG.md` to `## [0.1.0] - <date>`, and commit.
2. **Build and sign** as above.
3. **Make the repository public.** The release asset's address must work
   for strangers and for winget.
4. **Tag and publish the release** with the zip and its hash:

   ```bash
   cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && git tag v0.1.0 && git push origin v0.1.0 && gh release create v0.1.0 dist/CabinetOS-0.1.0-win-x64.zip dist/CabinetOS-0.1.0-win-x64.zip.sha256 --verify-tag --title "CabinetOS 0.1.0" --notes-file CHANGELOG.md
   ```

   A shorter notes file with only the version's section of `CHANGELOG.md`
   reads better than the whole file.
5. **Submit to winget.** The manifests in `dist\winget\0.1.0\` carry the
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
files next to it. A winget install gets no Start Menu entry and no indexer
service; `install.ps1` does those. The committed files carry 0.1.0 and a
zero hash; `release.ps1` fills in the real ones. `winget validate` accepts
them (checked 2026-09-28 with winget 1.29.380).

## Known gaps

- No automatic updates and no Settings > Apps entry (ADR 0009).
- The indexer service starts manually, so it is off after each restart of
  Windows until started again.
- x64 only; ARM64 is untested.
- The window's project references the whole Windows App SDK, so the
  release carries its AI and machine-learning libraries (about 40 MB
  unpacked) that CabinetOS does not use.
