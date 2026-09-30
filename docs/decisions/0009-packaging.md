# ADR 0009: Version 1 ships unpackaged, as a zip with an install script

- Status: accepted; its consequences "no automatic updates" and "not in
  Settings > Apps" are replaced by [ADR 0014](0014-in-app-updates.md)
- Date: 2026-09-28
- Decided by: the architect, as a default (the creator was asleep; the
  coordinating session recommended it). It settles open question 6 of
  [PLAN.md](../PLAN.md); say so to overturn it.

## Context

Phase 10 needs a way for a stranger to install CabinetOS. PLAN.md asked:
MSIX (a clean install and updates) or unpackaged (simpler)?

The facts that decide it:

- **The indexer is a Windows service** (ADR 0002). `cabinetos-indexer
  --install` registers it: manual start, LocalSystem, elevated. An MSIX
  package can carry a service only through restricted capabilities
  (`packagedServices`, and `localSystemServices` to run as LocalSystem). The
  Microsoft Store allows those only by exception, and the service then comes
  and goes with a machine-wide install of the whole app. The indexer is
  optional, so an MSIX app would still need a separate elevated installer for
  it.
- **The app is unpackaged today.** `ui/CabinetOS/CabinetOS.csproj` sets
  `WindowsPackageType=None`, and the Windows App Runtime comes from the
  machine.
- **The data folders are shared by several programs.** Plugins, tools,
  themes, the marketplace state and the logs live in
  `%LOCALAPPDATA%\CabinetOS`, the config in `%APPDATA%\CabinetOS`. MSIX
  redirects a packaged process's writes to AppData into the package's private
  folder, and the core started by the window would inherit that. A
  `cabinetos-cli` started from a normal terminal would then see other folders
  than the window.
- **MSIX installs only when signed** by a certificate the machine trusts. No
  certificate exists yet; an unpackaged folder runs unsigned (with a
  SmartScreen warning).

Options considered:

- **Unpackaged folder in a zip, installed by a PowerShell script.** Needs
  nothing beyond Windows PowerShell 5.1, which every Windows 11 has. The
  script can check prerequisites, register the service on request, and undo
  exactly what it did.
- **MSIX for the app, plus a separate elevated step for the indexer.** Clean
  install and removal, updates through App Installer or the Store; but the
  AppData redirection, the mandatory signature and the second installer
  come with it.
- **An MSI (WiX) or Inno Setup installer.** Both handle services, Start Menu
  entries and the Apps list natively, but add an installer toolchain and its
  license review to the build.

## Decision

Version 1 ships as one folder, zipped as `CabinetOS-<version>-win-x64.zip`,
with `install.ps1` and `uninstall.ps1` inside it:

- per user by default, into `%LOCALAPPDATA%\Programs\CabinetOS`, with no
  elevation;
- for all users with `-AllUsers`, into `%ProgramFiles%\CabinetOS`, elevated;
- a Start Menu shortcut (`-StartMenu`) and a PATH entry for `cabinetos-cli`
  (`-AddToPath`) only when asked;
- the indexer service only when asked (`-Indexer`), elevated, and only with
  `-AllUsers`.

MSIX is reconsidered when the indexer can ship as an optional component of
its own and the shared data folders have an answer under MSIX.

## Consequences

- **No Store listing and no automatic updates yet.** An update is the next
  zip, installed over the old one: `install.ps1` replaces the files it
  recorded. winget can carry the zip as a portable package
  (`build/winget/`), without the Start Menu shortcut and the service.
- **Prerequisites are checked, never installed, by the script:** Windows 11
  22H2 or newer (ADR 0004), the .NET 10 runtime (the window is published
  framework-dependent), the Windows App Runtime 2.5.1 or newer (x64), and the
  WebView2 Runtime. For each missing one the script prints the command that
  installs it. It downloads nothing itself.
- **The release build turns on the Windows App SDK's MSIX tooling**
  (`-p:EnableMsixTooling=true`) only to write `CabinetOS.pri`, the compiled
  XAML; without it the published window cannot load `MainWindow.xaml`. The
  app stays unpackaged.
- **The Rust programs link the C runtime statically** (`core/.cargo/config.toml`),
  so they run on a machine without the Visual C++ Redistributable.
- **Uninstalling is a script too**, and CabinetOS does not appear in
  Settings > Apps. An entry there needs a registry key or a real installer
  later.
- **Unsigned until the creator has a code-signing certificate.** SmartScreen
  warns before the first start of a downloaded build, and Smart App Control,
  where it is on, blocks unsigned programs outright. How to sign:
  [../release.md](../release.md).
- **The service runs as LocalSystem from the install folder**, so that
  folder must be one only administrators can change. A service program in a
  per-user folder would let any program running as the user replace it and
  gain LocalSystem rights at the next start. Hence `-Indexer` works only with
  `-AllUsers` into Program Files.
