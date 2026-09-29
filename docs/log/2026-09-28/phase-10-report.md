## Phase 10 report (core side: the buildable parts)

Context: CabinetOS Phase 10, the buildable parts (ADR, release build, installer, winget manifests, release notes, docs). Nothing was published, signed or requested. Everything is pushed; `main` = `origin/main` at ca02dfd.

## Built
- **ADR 0009, unpackaged** (your recommendation, kept): v1 ships as one folder zipped as `CabinetOS-<ver>-win-x64.zip`, with `install.ps1` and `uninstall.ps1` inside. MSIX is reconsidered when the indexer can ship as an optional component. The ADR records four reasons:
  - the service would need restricted MSIX capabilities;
  - MSIX redirects AppData writes, which would split the data folders between the window/core and `cabinetos-cli`;
  - MSIX will not install unsigned;
  - a script needs nothing beyond Windows PowerShell 5.1.
  It also records the consequences: no Store, no automatic updates, no Settings > Apps entry, prerequisites checked by the script, unsigned for now.
- **Static C runtime for the Rust programs** (`core/.cargo/config.toml`, `+crt-static`). dumpbin showed all three release exes imported `VCRUNTIME140.dll`, which a clean Windows 11 does not have. Now there are 0 C-runtime imports, at about 110–125 KB more per exe.
- **`build/release.ps1`** (PowerShell 7) is one command that builds everything:
  - `cargo build --release --locked -p cabinetos-core -p cabinetos-indexer -p cabinetos-cli`
  - `dotnet publish CabinetOS\CabinetOS.csproj -c Release -r win-x64 --self-contained false -p:EnableMsixTooling=true -o <folder>` (run from `ui\`).
  - **`-p:EnableMsixTooling=true` is required.** The plain publish has no `CabinetOS.pri`, and the published window crashed at start with "Cannot locate resource from 'ms-appx:///MainWindow.xaml'" (checked, crash trace written). With the flag it starts. The app stays unpackaged, and the script stops if the .pri is missing.
  - It then copies the 3 exes and their .pdb files next to CabinetOS.exe, puts the themes and schema in `extras\themes\` and Markdown Preview in `extras\tools\markdown-preview\` (opt-in; the window does not read either there), and adds LICENSE, THIRD-PARTY-NOTICES.md, the install scripts and `release.json`.
  - `release.json` holds the version, the commit, and the runtime minimums taken from the build: .NET from runtimeconfig; the Windows App Runtime 2.5.1.0 and its package family from the SDK's `WindowsAppSDK-VersionInfo.h`.
  - Finally it writes the zip (files at the zip root), its `.sha256`, and filled winget manifests in `dist\winget\<ver>\`, then runs `winget validate` on them.
  - Version: the only source is `core/Cargo.toml`. The script stops if `ui/Directory.Build.props` differs; `-SyncVersion` copies the Cargo version over. The props already hold 0.1.0, so `ui/` was not touched.
  - `-PackageOnly` re-zips the existing folder after signing.
  - `dist/` is in .gitignore.
- **`build/notices.ps1`**: cargo-about is not installed and no global installs are allowed, so this is my own generator. It covers:
  - `cargo metadata` (normal and build dependencies of the 3 programs, Windows x64);
  - every NuGet package in the published `CabinetOS.deps.json`;
  - the Rust standard library and the .NET app host;
  - xterm, addon-fit and marked.
  It dedupes identical texts and stops if any component has no text. Result: 271 components, 150 distinct texts, 1.24 MB. 15 wasmtime/cranelift crates ship no license file; they get their project's Apache-2.0-with-LLVM-exception text, and the row says so. `Microsoft.Windows.SDK.NET.Ref` ships only a license URL, which is recorded.
- **`build/install.ps1` / `build/uninstall.ps1`**: work in PowerShell 5.1 and 7, ASCII only, no prompts, `-WhatIf` supported.
  - Install checks Windows build ≥ 22621 and 64-bit, the .NET 10 runtime, the Windows App Runtime ≥ 2.5.1.0 x64 (`Get-AppxPackage`) and WebView2 (EdgeUpdate `pv`). For each missing one it prints the command. It downloads nothing.
  - Default target is `%LOCALAPPDATA%\Programs\CabinetOS`; `-AllUsers` means `%ProgramFiles%\CabinetOS` and needs elevation. Opt-in switches: `-StartMenu`, `-AddToPath` (registry edit that keeps `REG_EXPAND_SZ`, plus a WM_SETTINGCHANGE broadcast), `-Indexer`, `-SkipPrerequisiteCheck`.
  - It records everything in `.cabinetos-install.json`, written before the copy, so an interrupted copy is still recognized. An update removes files that are stale in the new version.
  - It refuses: a drive root, an overlap with the source folder, a folder holding other files, a program running from the target, and being started from the repo.
  - Uninstall removes exactly what the record lists. User files and the data folders stay unless `-RemoveData`.
- **`build/winget/`**: `OliverD25.CabinetOS.yaml` (the version manifest) plus `.installer.yaml` and `.locale.en-US.yaml`, schema 1.10.0.
  - Type: zip + portable nested installer + `ArchiveBinariesDependOnPath: true`. CabinetOS.exe needs the files next to it, so winget puts the folder on PATH instead of linking single files.
  - Dependencies: `Microsoft.DotNet.Runtime.10`, `Microsoft.WindowsAppRuntime.2` (MinimumVersion 2.5.1), `Microsoft.EdgeWebView2Runtime`.
  - InstallerUrl pattern: `https://github.com/OliverD25/cabinetos/releases/download/v0.1.0/CabinetOS-0.1.0-win-x64.zip`. The committed files carry a zero hash.
- **`.github/workflows/release.yml`**: `workflow_dispatch` only, read-only permissions, 60 min limit. It runs release.ps1 and uploads the zip, the .sha256 and the winget folder as an artifact (30 days, compression 0). It creates no GitHub Release.
- **`CHANGELOG.md`** (Keep a Changelog, one Unreleased section, one line per capability) and **`docs/release.md`**: build, contents, install, uninstall, the exact signtool/Set-AuthenticodeSignature commands, and the publish steps (tag, `gh release create`, winget submission), written down and not run. I also added a Phase 10 section to `docs/dev-setup.md` (PowerShell 7) and the installer's service step to `docs/indexer.md`.

## Commits
- 5ca40ad docs: ADR 0009
- cc6c315 core: static C runtime
- b7acc12 build: install/uninstall
- b4dafb2 build: winget manifests
- 7506990 build: release.ps1 + notices.ps1 + .gitignore
- a92fe6e ci: release job (you pushed my 25a6130; identical content)
- ab1db44 docs: CHANGELOG
- bd7bb7b docs: release.md, dev-setup, indexer
- ca02dfd build: the clean-tree check covers only core/ui/sdk/build/LICENSE, so untracked docs/log screenshots no longer mark a build dirty

## Checks
- **Five core checks on ca02dfd**: build, test (482 passed, 0 failed, 5 ignored), clippy `-D warnings`, fmt `--check`, `cargo deny check` (advisories, bans, licenses, sources ok). They also passed right after the crt-static change (full rebuild).
- **UI, locally** (`dotnet build CabinetOS.sln && dotnet test --solution CabinetOS.sln --no-build` from `ui/`): 418 passed, 0 failed, 0 skipped.
- **CI**: not started (account billing). release.yml was never run. actionlint is not installed, so the file got a strict YAML parse plus a structural script instead: manual trigger only, read-only permissions, time limit, step outputs exist. All action majors (checkout v7, rust-cache v2, setup-dotnet v6, cache v6, upload-artifact v7) were looked up with `gh api`.
- **Winget and parse checks**: `winget validate` succeeded on the committed manifests and on the generated ones (exit 0). The four scripts parse in PS 5.1.26100 and 7.6.6.

## Live check (final run on ca02dfd; release.json says `uncommittedChanges: false`)
Release: 83 files, 258,109,120 bytes unpacked. Zip `dist\CabinetOS-0.1.0-win-x64.zip`: 81,003,825 bytes (77.3 MiB). SHA-256 `9dca2cf0cc0ff976f8c92eba881ced0050e5f72e1abb62ff36f6271827d707b0`. In the zip, the Rust .pdb files take 37.7 MB and the unused Windows App SDK AI/ML libraries 16.7 MB. The listing, with bytes:
```
1,521 Assets\xterm\addon-fit\addon-fit.js | 1,103 Assets\xterm\addon-fit\LICENSE | 799 Assets\xterm\terminal.css | 672 Assets\xterm\terminal.html | 8,365 Assets\xterm\terminal.js | 1,261 Assets\xterm\xterm\LICENSE | 7,112 Assets\xterm\xterm\xterm.css | 488,663 Assets\xterm\xterm\xterm.js
4,436,480 cabinetos-cli.exe | 26,560,512 cabinetos-core.exe | 2,258,432 cabinetos-indexer.exe | 20,090,880 cabinetos_cli.pdb | 104,837,120 cabinetos_core.pdb | 14,168,064 cabinetos_indexer.pdb
1,000,960 CabinetOS.Core.dll | 484,404 CabinetOS.Core.pdb | 15,445 CabinetOS.deps.json | 415,232 CabinetOS.dll | 162,816 CabinetOS.exe | 188,424 CabinetOS.pdb | 56,544 CabinetOS.pri | 466 CabinetOS.runtimeconfig.json
184,136 CommunityToolkit.Mvvm.dll | 18,700,224 DirectML.dll | 21,659,280 onnxruntime.dll | 237,632 Microsoft.ML.OnnxRuntime.dll | 410,936 System.Numerics.Tensors.dll
1,551/1,348/1,527/1,547 extras\themes\{catppuccin-mocha,default,nord,rose-pine-moon}.json | 8,857 extras\themes\theme.schema.json
extras\tools\markdown-preview\: 914 index.html | 2,942 marked\LICENSE | 46,891 marked\marked.umd.js | 2,555 preview.css | 3,989 preview.js | 1,361 README.md | 282 tool.json
18,547 install.ps1 | 10,063 uninstall.ps1 | 1,097 LICENSE | 600 release.json | 1,270,078 THIRD-PARTY-NOTICES.md
26,086,944 Microsoft.Windows.SDK.NET.dll | 7,336,760 Microsoft.WinUI.dll | 528,944 WinRT.Runtime.dll | 160,368 WebView2Loader.dll | 793,704 Microsoft.Web.WebView2.Core.dll | 686,192 Microsoft.Web.WebView2.Core.Projection.dll | 394,040 Microsoft.WindowsAppRuntime.Bootstrap.dll | 16,696 Microsoft.WindowsAppRuntime.Bootstrap.Net.dll | 96,056 Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll | 1,621,272 Microsoft.InteractiveExperiences.Projection.dll | 903,464 Microsoft.Windows.AI.MachineLearning.dll
+ 30 more Microsoft.*.Projection.dll files of 21–190 KB each (AI.*, AppLifecycle, AppNotifications*, Background, BadgeNotifications, DynamicDependency, Resources, WindowsAppRuntime, Foundation, Graphics.Imaging, Management.Deployment, Media.Capture, PushNotifications, Search, Security.AccessControl, Security.Authentication.OAuth, Storage, Storage.Pickers, System, System.Power, Widgets)
```
Install and uninstall test, Windows PowerShell 5.1:
- Setup: unpacked the zip into `%TEMP%\cabinetos-install-test\ps51\`. Each case ran as a new process: `powershell.exe -ExecutionPolicy Bypass -File ...`.
- Install to `%TEMP%\...\ps51\CabinetOS`: exit 0, 83 files, 246.1 MB, record says scope user and 82 files.
- The installed CabinetOS.exe started with every data folder redirected to scratch. window.png was written, and the ui log shows `core started {"exe":"...\ps51\CabinetOS\cabinetos-core.exe"}` and `core ready {"protocol_version":11,"core_version":"0.1.0"}`. So the launcher found the core next to it.
- Update over the same folder: exit 0, and old-file.txt (listed in the old record only) was removed.
- `-StartMenu -AddToPath -WhatIf`: exit 0. It printed "Create the Start Menu shortcut ... CabinetOS.lnk", "Add ... the User PATH" and "What if: nothing was changed."
- Refused, each with exit 1:
  - `-Indexer` without `-AllUsers`;
  - `-AllUsers` without elevation;
  - `-Destination C:\ -WhatIf` ("C: is the root of a drive"). This is the one case where I passed a drive root, always together with -WhatIf, to prove the guard. It stopped before any step.
  - a folder holding other files;
  - started from `build\` in the repo;
  - prerequisites missing: I edited release.json in the temp copy to ask for .NET 99 and runtime 9.9.9.9. It printed the winget commands and the aka.ms installer URL. No target folder was created.
- Uninstall: exit 0, and the folder no longer exists.
- Reinstall, then `uninstall.ps1 -RemoveData` with LOCALAPPDATA and APPDATA pointed at scratch folders: exit 0, and both fake data folders were removed.
- The same cases in PowerShell 7.6.6 all passed. There the unpacked CabinetOS.exe had a Zone.Identifier stream (zone 3) added, and the installer printed its SmartScreen note with the `Unblock-File` command.
- Real state before and after (`%LOCALAPPDATA%\CabinetOS`, `%APPDATA%\CabinetOS`, ProgramData, the Programs folders, Start Menu shortcuts, SHA-256 of the user and machine PATH, the service, the uninstall key): **0 differences**, and `%TEMP%\cabinetos-install-test` is deleted.
- An earlier (non-final) comparison showed WebView2 cache files changing under `%LOCALAPPDATA%\CabinetOS\WebView2\{terminal,tool-markdown-preview}` at 20:36 and 20:41 UTC. That was your live-check runs: my window runs started no WebView2 and ran at 20:42.

## Decided
1. Unpackaged, per ADR 0009.
2. The `-p:EnableMsixTooling=true` publish flag (see Built).
3. Static C runtime.
4. The .pdb files ship in the zip. Phase 1 chose line tables so crash traces name file and line, and that needs the .pdb next to the exe. The cost is 38 of 77 MB zipped.
5. Files at the zip root, so Explorer's "Extract All" makes one folder and the winget RelativeFilePath is plain.
6. Themes and Markdown Preview under `extras\` (Article 10).
7. The installer also checks .NET 10, which the framework-dependent publish needs.
8. `-Indexer` needs `-AllUsers` and a target inside Program Files. A LocalSystem service whose exe sits in a user-writable folder is a privilege escalation.
9. `-Indexer` also runs `Start-Service` once, because the service is manual start.
10. The installer never strips the download mark (Zone.Identifier); it prints the `Unblock-File` command instead.
11. release.ps1 and notices.ps1 need PowerShell 7. install.ps1 and uninstall.ps1 also run in PowerShell 5.1.
12. Winget IDs, checked with `winget search` on 2026-09-28:
    - `Microsoft.EdgeWebView2Runtime` 154.0.4258.37
    - `Microsoft.DotNet.Runtime.10` 10.0.12 (not the DesktopRuntime: runtimeconfig names only Microsoft.NETCore.App)
    - `Microsoft.WindowsAppRuntime.2`: only 2.3.1, so the installer also prints https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe

## Needs the user
- A code-signing certificate and the signing itself (commands are in docs/release.md).
- Making the repository public, `gh release create`, and the winget submission. The submission is blocked until winget carries Windows App Runtime 2.5.
- Choose: .pdb files in the zip (77 MB) or a separate symbols zip (about 40 MB download).
- The existing open item: the Windows App SDK license exception.

## Known gaps
- Not run, by the rules: a real `-AllUsers` install, `-Indexer`, and the real shortcut and PATH writes. Those were checked with `-WhatIf` only.
- `release.yml` has never run.
- No Settings > Apps entry and no auto-update.
- ARM64 is untested.
- A real `winget install` of the portable package is untested; it needs the published URL.
- The runtime check looks only at packages registered for the current user.
- Unsigned, so SmartScreen warns and Smart App Control blocks.
- The task asked for the version in "the UI's About": no About view exists. The version shows only in the window's logs and crash traces (`Program.Version`, from the props file).

## Noticed out of scope
- **For the UI agent:**
  - `ui/CabinetOS.csproj` references the `Microsoft.WindowsAppSDK` metapackage. That ships about 40 MB unpacked (17 MB zipped) of AI/ML libraries CabinetOS does not use: onnxruntime.dll, DirectML.dll, Microsoft.Windows.AI.*, System.Numerics.Tensors (Article 10). Referencing only the component packages it needs would drop them.
  - The csproj could produce the .pri on publish itself, so the extra flag would go away.
  - The UI has no About view.
- The indexer service is manual start (a Phase 6 decision), so it is off after every reboot.
- CI does not parse-check `build/*.ps1`.
- THIRD-PARTY-NOTICES.md is mostly Microsoft notice files: the WindowsAppSDK NOTICE is 335 KB and the AI.MachineLearning notices are 325 KB.

## Ready text: PLAN.md
Replace lines 220–226 (the Phase 10 block) with:
```
### Phase 10 — Packaging and release — buildable parts done 2026-09-28 (signing and publishing wait for the creator)

Goal: a stranger can install it.

Produces: MSIX or unpackaged decision; `winget` manifest; code signing; the indexer service installer step; release notes; the repo goes public.

Built 2026-09-28; nothing published or signed. Unpackaged for version 1 ([ADR 0009](decisions/0009-packaging.md)): one folder, zipped, with a script installer; MSIX waits until the indexer can ship as an optional component. `build/release.ps1` (PowerShell 7) builds everything with one command: the three Rust programs, which now carry the C runtime inside them because a clean Windows 11 has no `VCRUNTIME140.dll`; the window, published framework-dependent on .NET 10 and the Windows App Runtime with `-p:EnableMsixTooling=true` (without it the published window cannot find its compiled XAML and stops at start); the programs' `.pdb` files; the four themes and Markdown Preview under `extras\` (opt-in); `LICENSE`; `THIRD-PARTY-NOTICES.md` (271 components, every license text); `install.ps1`, `uninstall.ps1` and `release.json`; then the zip, its SHA-256 and the winget manifests (a portable zip, accepted by `winget validate`). The version comes from `core/Cargo.toml` alone. `install.ps1` checks Windows 11 22H2+, the .NET 10 runtime, the Windows App Runtime 2.5.1+ and WebView2, and prints the install command for each missing one. It installs per user without elevation, or with `-AllUsers` into Program Files. The Start Menu shortcut, the PATH entry and the indexer service are opt-in. The service needs `-AllUsers`, because a LocalSystem service must not run a program the user can replace. `uninstall.ps1` removes exactly what the install recorded and keeps the data unless `-RemoveData`. A manual CI job builds the zip as an artifact; it has not run (GitHub Actions starts no jobs for this account). `CHANGELOG.md` lists the first version's capabilities. Measured 2026-09-28 on this PC: a 77 MB zip (83 files, 246 MB unpacked; 38 MB of the zip are Rust symbols, 17 MB Windows App SDK AI libraries CabinetOS does not use). Installed from the zip into `%TEMP%` in Windows PowerShell 5.1 and PowerShell 7, the window started its own core (protocol 11, version 0.1.0), and uninstall left nothing behind. Still open for the creator: a code-signing certificate, making the repository public, `gh release create`, and the winget submission, which waits until winget carries Windows App Runtime 2.5. Guide: [release.md](release.md).

Articles: 2, 3.
```
Replace line 280 (open question 6) with:
`6. **Packaging (Phase 10).** Settled 2026-09-28: unpackaged for version 1, a zip with a script installer; MSIX is reconsidered when the indexer can ship as an optional component ([ADR 0009](decisions/0009-packaging.md)).`

## Ready text: README.md
Status line: add after "...five findings queued for the shell.":
`Phase 10's buildable parts are done: one command builds a release zip with an installer and every third-party license; signing and publishing wait for the creator.`

Documents table, add these rows:
```
| [docs/release.md](docs/release.md) | Releases: how the zip is built, installed, removed, signed and published. |
| [CHANGELOG.md](CHANGELOG.md) | What each version adds. |
```
Layout table, add:
`| `build/` | Release scripts: `release.ps1`, the installer and uninstaller, the notices generator, the winget manifests |`

Replace "## Requirements" with the following, and add "## Install" after it:
````
## Requirements

- Windows 11 22H2 (build 22621) or newer, x64.
- To run it: the .NET 10 runtime, the Windows App Runtime 2.5.1 or newer (x64), and the WebView2 Runtime, which every Windows 11 has. The installer checks all three and prints the command that installs a missing one.
- To build it: the toolchains in [docs/dev-setup.md](docs/dev-setup.md).

## Install

No release is published yet; build the zip with `build/release.ps1` ([docs/release.md](docs/release.md)). With the zip in your Downloads folder, in PowerShell:

```powershell
Unblock-File "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip"
Expand-Archive "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip" "$env:TEMP\CabinetOS-0.1.0" -Force
powershell -ExecutionPolicy Bypass -File "$env:TEMP\CabinetOS-0.1.0\install.ps1" -StartMenu -AddToPath
```

This installs CabinetOS for you alone, in `%LOCALAPPDATA%\Programs\CabinetOS`, with no administrator rights; `-StartMenu` and `-AddToPath` are optional. `-AllUsers` installs into Program Files instead (run as administrator), and `-AllUsers -Indexer` adds the indexer service for instant search of whole volumes. To remove CabinetOS:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\CabinetOS\uninstall.ps1"
```

Settings, plugins and logs stay unless you add `-RemoveData`. The release is not signed yet, so Windows SmartScreen may ask before the first start. Details: [docs/release.md](docs/release.md).
````

## Files
Repo root: E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos

- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\decisions\0009-packaging.md`: new, ADR 0009.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\decisions\README.md`: the index row for 0009.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\.cargo\config.toml`: new, `+crt-static`.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\build\release.ps1`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\build\notices.ps1`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\build\install.ps1`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\build\uninstall.ps1`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\build\winget\OliverD25.CabinetOS.yaml`, `OliverD25.CabinetOS.installer.yaml`, `OliverD25.CabinetOS.locale.en-US.yaml`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.github\workflows\release.yml`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.gitignore`: `/dist/`.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\CHANGELOG.md`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\release.md`: new.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\dev-setup.md`: new Phase 10 section.
- `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\indexer.md`: the installer's service step.
- Build output, not in git: `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\dist\` (the folder, the zip, the .sha256, and `winget\0.1.0\`).
- Test scaffolding, in the scratchpad `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\`: `installtest.ps1`, `smoke.ps1`, `datastate.ps1`, and the logs `installtest-final-ps51.log`, `installtest-final-pwsh7.log`, `release-final.log`, `checks-final.log`, `ui-tests-final.log`.

Recap: Phase 10's buildable parts are done, tested and pushed. Left for the creator: signing, going public, the GitHub Release, and the winget submission (blocked by Windows App Runtime 2.5 in winget). Also left for them: the .pdb-in-zip choice. Left for the UI agent: the AI/ML library bloat, the .pri on publish, and an About view.
