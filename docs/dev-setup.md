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
cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core && cargo build --workspace && cargo test --workspace && cargo clippy --workspace --all-targets -- -D warnings && cargo fmt --all -- --check && cargo deny check
```

(From a Windows shell, the same commands run from `core\` with `cargo`
on the PATH.)

## Phase 5 and later: the WinUI 3 frontend

Not installed on the main PC as of 2026-09-28 (checked: `dotnet --list-sdks`
returned nothing; Visual Studio has only the C++ workload).

- **.NET 8 SDK** (long-term support). Windows-only installer, so PowerShell:

  ```powershell
  # PowerShell — winget exists only on Windows
  winget install --id Microsoft.DotNet.SDK.8 --exact
  ```

- **Windows App SDK** workload for Visual Studio 2022: in the Visual Studio
  Installer, add "WinUI application development" (this brings the Windows App
  SDK C# templates and the XAML tooling). Or from PowerShell:

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

- Logs: `%LOCALAPPDATA%\CabinetOS\logs\` (`core.jsonl`, `ui.jsonl`,
  `indexer.jsonl`). Override with the `CABINETOS_LOG_DIR` environment
  variable.
- Config: `%APPDATA%\CabinetOS\cabinetos.json`. Override with
  `CABINETOS_CONFIG`.
