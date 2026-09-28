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

## Phases 1–4, 6–8: the Rust core

- **Rust stable**, pinned by `core/rust-toolchain.toml` (rustup installs the
  pinned version on first build). Install rustup from https://rustup.rs if it
  is missing.
- **Visual Studio 2022 Build Tools** with the "Desktop development with C++"
  workload: the `x86_64-pc-windows-msvc` target needs the MSVC linker and the
  Windows SDK. Visual Studio Community 2022 with that workload also works.
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
  `Microsoft.WindowsAppSDK` NuGet package during restore; nothing to install.
  The Visual Studio workload is only needed for the XAML designer and the
  project templates inside Visual Studio. To add it, in the Visual Studio
  Installer choose "WinUI application development", or from PowerShell:

  ```powershell
  # PowerShell — the Visual Studio Installer exists only on Windows
  & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify --installPath "C:\Program Files\Microsoft Visual Studio\2022\Community" --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs --passive
  ```

- **WebView2 Runtime**: present on every Windows 11 machine; nothing to do.

## Phase 7: WASM plugins

- Rust targets and tools for building Component Model plugins:

  ```bash
  rustup target add wasm32-wasip2 && cargo install cargo-component wasm-tools --locked
  ```

## Phase 6: the indexer

- Tests that read the MFT or the USN Journal need an **elevated** terminal
  (run as Administrator) and an NTFS volume. They are marked `#[ignore]` and
  run with `cargo test -- --ignored` from an elevated shell. GitHub Actions
  runners run as Administrator, so CI runs them.

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
