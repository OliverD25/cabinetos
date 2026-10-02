# Developer setup

What a machine needs to build and test CabinetOS, phase by phase. Commands are
for the WSL bash terminal unless marked `# PowerShell`; PowerShell is used only
where the tool exists on Windows alone.

## Always

- **Windows 11 22H2 (build 22621) or newer** ([ADR 0004](decisions/0004-minimum-windows-11-22h2.md)).
  Check: `cmd.exe /c ver`.
- **Git** with the repository's line-ending rules. The repo ships
  `.gitattributes`, so no `core.autocrlf` setting is needed; a fresh clone
  gets LF for Rust and docs and CRLF for the C# files.

## CI, on demand

`.github/workflows/ci.yml` runs the core's five checks, the WinUI build and
tests, `cargo deny`, and a parse check of the `build/*.ps1` scripts, on
`windows-latest`. Since 2026-09-29 it no longer runs on every push: the
account's 2,000 free minutes a month ran out in September (Windows minutes
count double), and no money goes to CI. So:

- Every change is verified on the development PC first: the five checks
  and the UI tests below, and `build/check-scripts.ps1` for the scripts.
- CI runs once per accepted phase, started by hand, and on pull requests
  into `main`. A run started by hand runs every job; a pull request runs
  the jobs its files touch.

  ```bash
  gh workflow run ci.yml --repo OliverD25/cabinetos && gh run list --workflow ci.yml --repo OliverD25/cabinetos --limit 1
  ```

- To run CI on every push again, restore the `push` trigger in `ci.yml`
  (its git history has it).

The scripts' check, in both PowerShells (install.ps1 and uninstall.ps1
must also parse in Windows PowerShell 5.1):

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos && pwsh.exe -NoProfile -File build/check-scripts.ps1 && powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/check-scripts.ps1
```

## Phases 1–4, 6–8: the Rust core

- **Rust stable**, pinned by `core/rust-toolchain.toml` (rustup installs the
  pinned version on first build). Install rustup from https://rustup.rs if it
  is missing.
- **Visual Studio 2022 Build Tools** with the "Desktop development with C++"
  workload: the `x86_64-pc-windows-msvc` target needs the MSVC linker and the
  Windows SDK, whose `rc.exe` the build also needs to embed the version
  resource of the three programs. Visual Studio Community 2022 with that
  workload also works.
- **cargo-deny** for the license and advisory check that CI runs:

  ```bash
  cargo install cargo-deny --locked
  ```

Build and test:

```bash
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core && cargo.exe build --workspace && cargo.exe test --workspace && cargo.exe clippy --workspace --all-targets -- -D warnings && cargo.exe fmt --all -- --check && cargo.exe deny check
```

Inside WSL the command is `cargo.exe`, the Windows cargo, on purpose: plain
`cargo` there is the Linux toolchain, which cannot build this Windows-only
code. From a Windows shell (PowerShell, Git Bash) the same commands run from
`core\` with plain `cargo`.

## Phase 5 and later: the WinUI 3 frontend

On the main PC the .NET 10 SDK (10.0.401) was installed on 2026-09-28; Visual
Studio still has only the C++ workload, which command-line builds do not need.

- **.NET 10 SDK** (long-term support until November 2028; .NET 8 leaves
  support in November 2026). Windows-only installer, so PowerShell; winget
  asks for elevation (a UAC prompt):

  ```powershell
  # PowerShell — winget exists only on Windows
  winget install --id Microsoft.DotNet.SDK.10 --exact
  ```

  A per-user install with no UAC prompt (for unattended sessions) uses
  Microsoft's install script instead:

  ```powershell
  # PowerShell — installs under %LOCALAPPDATA%\Microsoft\dotnet, no elevation
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"; & "$env:TEMP\dotnet-install.ps1" -Channel 10.0
  ```

- **Windows App SDK**: command-line builds (`dotnet build`) get it as the
  Windows App SDK NuGet packages (the WinUI, Foundation, InteractiveExperiences
  and Runtime components) during restore; nothing to install for building. Running the window needs the Windows App Runtime (see
  "Phase 5" below).
  The project names these components, not the `Microsoft.WindowsAppSDK`
  package as a whole, which also brings the AI, machine-learning, Search and
  Widgets components that CabinetOS never uses (42.5 MB unpacked and 17.4 MB
  zipped in a release; [release.md](release.md), "Sizes"; Constitution
  Article 10). What each one is for: WinUI is the window and, through it,
  the WebView2 host of the terminal; InteractiveExperiences is windowing,
  input, composition and Mica; Foundation is the bootstrapper of an
  unpackaged app and the MRT resources (`CabinetOS.pri`); Runtime is the
  Windows App Runtime version the bootstrapper asks for. WinUI pulls in
  Foundation, InteractiveExperiences and Base by itself, but one version
  lower for InteractiveExperiences (2.1.8) than the metapackage names, so
  `ui/Directory.Packages.props` pins all four at the versions the Windows App
  SDK 2.5.1 package names (WinUI 2.3.9, Foundation 2.3.12,
  InteractiveExperiences 2.1.9, Runtime 2.5.1); the Runtime package's build
  checks that they match. To move to another Windows App SDK release, read
  the `.nuspec` of its `microsoft.windowsappsdk` package in
  `%USERPROFILE%\.nuget\packages`, copy those four versions, and build a
  release: `build/release.ps1` stops when a machine-learning library
  (`onnxruntime`, `DirectML`, `*.AI.*`, `*.MachineLearning.*`) turns up in
  the release folder.
  The Visual Studio workload is only needed for the XAML designer and the
  project templates inside Visual Studio. To add it, in the Visual Studio
  Installer choose "WinUI application development", or from PowerShell:

  ```powershell
  # PowerShell — the Visual Studio Installer exists only on Windows
  & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify --installPath "C:\Program Files\Microsoft Visual Studio\2022\Community" --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs --passive
  ```

- **WebView2 Runtime**: present on every Windows 11 machine; nothing to do.

### Phase 5: what the shell was built with

Checked on 2026-09-28 on the main PC (Windows 11 25H2, build 26200).

| Tool or package | Version | License | Used for |
|---|---|---|---|
| .NET SDK | 10.0.401, pinned in `ui/global.json` (a newer 10.0 feature band is accepted) | MIT | building and testing |
| `Microsoft.WindowsAppSDK.WinUI` 2.3.9, `.Foundation` 2.3.12, `.InteractiveExperiences` 2.1.9, `.Runtime` 2.5.1 (NuGet) | Windows App SDK 2.5.1 | Microsoft Software License Terms (redistributable; not an open-source license) | WinUI 3, Mica, the window |
| Windows App Runtime | 2.5 (x64), installed on the machine | same as above | running `CabinetOS.exe` |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | `ObservableObject` in the view models |
| `Microsoft.Windows.CsWin32` | 0.3.335 (build time only) | MIT | generated calls to `MapViewOfFile`, `UnmapViewOfFile`, `CloseHandle`, `MessageBox` |
| `xunit.v3` | 4.0.1 | Apache-2.0 | the tests |
| `JsonSchema.Net` | 8.0.5 | MIT | checking messages against `sdk/protocol` |

- **The Windows App Runtime must be installed** to run the window: the
  build uses it from the machine instead of copying it next to
  `CabinetOS.exe` (38 MB of output instead of 152 MB). This PC has 2.5.1.
  Elsewhere, install it with Microsoft's installer (no winget package has
  2.5 yet):

  ```powershell
  # PowerShell — the installer is a Windows program
  Invoke-WebRequest https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe -OutFile "$env:TEMP\WindowsAppRuntimeInstall-x64.exe"; & "$env:TEMP\WindowsAppRuntimeInstall-x64.exe"
  ```

  To build a copy that needs no installed runtime, add
  `-p:WindowsAppSDKSelfContained=true` to `dotnet build`.
- **`JsonSchema.Net` stays on 8.0.5**: version 9.0 moved to a
  maintenance-fee license (the "Open Source Maintenance Fee" EULA), and the
  project takes only MIT, Apache or BSD packages.
- **Run the tests from `ui/`.** `ui/global.json` switches `dotnet test` to
  the Microsoft.Testing.Platform mode that xunit v3 needs on the .NET 10
  SDK; `dotnet` finds that file only in the current folder or above it.

  ```bash
  cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/ui && dotnet.exe build CabinetOS.sln && dotnet.exe test --solution CabinetOS.sln --no-build
  ```

  The end-to-end test needs the core, built first with
  `cargo.exe build -p cabinetos-core` in `core/`; without it the test skips
  itself.
- How to run and debug the window: [ui.md](ui.md).

## Phase 7: WASM plugins

- Rust targets and tools for building Component Model plugins:

  ```bash
  rustup target add wasm32-wasip2 && cargo install cargo-component wasm-tools --locked
  ```

- The Agent extension (Phase 14) is a Cargo project of its own,
  `sdk/extensions/agent/plugin`, outside the core workspace. Its
  `rust-toolchain.toml` pins the core's channel plus the `wasm32-wasip2`
  target, which rustup adds on first use. `cargo test` in that folder runs
  its own 90 tests natively with a fake host; the five core checks do not
  run them, and CI has a step for them. The component itself is built by
  `sdk/extensions/build-extensions.ps1`, which also refreshes the committed
  test copy in `sdk/fixtures/plugins/agent` ([extensions/agent.md](extensions/agent.md),
  "Building and packing").

## Phase 6: the indexer

- Tests that read the MFT or the USN Journal need an **elevated** terminal
  (run as Administrator) and an NTFS volume. They are marked `#[ignore]` and
  run with `cargo test -- --ignored` from an elevated shell. GitHub Actions
  runners run as Administrator, so CI runs them.

## Phase 10: release builds

- **PowerShell 7** (`pwsh`) runs `build/release.ps1` and
  `build/notices.ps1`. Windows only, so PowerShell:

  ```powershell
  # PowerShell - winget exists only on Windows
  winget install --id Microsoft.PowerShell --exact
  ```

- Everything else is what the core and the window already need. The Rust
  programs link the C runtime statically (`core/.cargo/config.toml`), so a
  full rebuild follows the first build after that file arrived.
- `build/release.ps1` also writes the symbols zip (the `.pdb` files) and
  stops when the release folder holds a machine-learning library of the
  Windows App SDK ([release.md](release.md), "The symbols" and "Sizes").
- How to build, install, sign and publish: [release.md](release.md).

## Logs and config while developing

- Logs: `%LOCALAPPDATA%\CabinetOS\logs\`, one file per process and UTC day
  (`core.<date>.jsonl`, `ui.<date>.jsonl`, `indexer.<date>.jsonl`). Override
  the directory with the `CABINETOS_LOG_DIR` environment variable, the level
  with `CABINETOS_LOG` (default `info`), and set `CABINETOS_LOG_STDERR=1` to
  also print to stderr. Format: [diagnostics.md](diagnostics.md).
- Config: `%APPDATA%\CabinetOS\cabinetos.json`, created with the defaults
  when the core first starts. Override with `--config <path>` on
  `cabinetos-core` or with `CABINETOS_CONFIG`. Format:
  [config.md](config.md).
