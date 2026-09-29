# CabinetOS

**Modern System Commander**

An approachable, high-performance file manager for Windows 11 that marries the
raw dual-pane efficiency of Total Commander with the deep extensibility and
workspace architecture of VS Code.

- A **Rust core** does all the work: NT-level directory reading, drive-aware
  copy queues, a real-time volume index, a WebAssembly plugin host.
- A **WinUI 3 frontend** (C#) only draws pixels and captures keystrokes. It
  reads directory listings straight from shared memory, with no copying.
- Everything beyond file navigation is an **opt-in extension**: headless WASM
  Core Plugins in a capability sandbox, and visual Tool Extensions in
  dockable panes.
- **The mouse is optional.** Every action is a named command with a shortcut,
  reachable from a command palette, with chord keybindings.

Status: pre-alpha. Phases 0 to 4 and 6 to 9 of [the plan](docs/PLAN.md) are
done: the governing documents, and a Rust core that lists and watches
directories in shared memory, serves its configuration, commands and keymap,
runs copy, move and delete jobs on per-disk queues, runs sandboxed
WebAssembly plugins whose crashes it contains, searches whole NTFS volumes
through an elevated indexer (or walks folders without it), runs shells in
pseudo-consoles, applies JSON colour themes live, and installs plugins,
themes and tools from a static marketplace index with each download's
SHA-256 checked, all over a user-only named pipe, with the indexer behind a
read-only pipe of its own and each shell's bytes on a pipe of their own
(`core/`, 690 tests). Phase 5, the WinUI 3 shell, is built (`ui/`, 676
tests): two panes over the core's shared-memory listings with the shell's
type names and icons, breadcrumbs, a status bar, the command palette with
chord keys and inline rebinding, copy, move, delete, rename, new folder and
open with the transfer flyout and conflict decisions, file search, the
plugin list and permissions review, the integrated terminal in a Tool Dock,
Tool Extensions each in a WebView2 process of its own, with Markdown
Preview as the first (opt-in, in `sdk/tools`), colour themes applied live
with a theme picker, and the marketplace view, which installs plugins and
themes from an index through the core; checked with real keys on
2026-09-28, with five findings queued for the shell. Phase 10's buildable
parts are done: one command builds a release zip with an installer and
every third-party license; signing and publishing wait for the creator. 36
more colour themes, ported under their MIT licenses from 27 of the 30 most
popular editor themes, wait in `sdk/themes/collection` for the marketplace
instead of shipping with the core.

## Documents

| Document | What it is |
|---|---|
| [CONSTITUTION.md](CONSTITUTION.md) | The twelve principles. Protected; changes need the creator's approval. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | The implementation brief: how it is built. Living document. |
| [docs/PLAN.md](docs/PLAN.md) | The development plan: decisions, phases, risks. |
| [docs/decisions/](docs/decisions/README.md) | One record per architecture decision. |
| [docs/design/](docs/design/ABOUT.md) | The design handout and clickable prototype. |
| [docs/dev-setup.md](docs/dev-setup.md) | What to install to build and test. |
| [docs/ipc.md](docs/ipc.md) | The control-channel protocol and the shared-memory listing layout. |
| [docs/diagnostics.md](docs/diagnostics.md) | The JSON Lines log format and crash traces. |
| [docs/config.md](docs/config.md) | `cabinetos.json`: every key, its default, and how changes apply live. |
| [docs/keybindings.md](docs/keybindings.md) | Key grammar, chords, the Immutable System Tier, conflict rules. |
| [docs/jobs.md](docs/jobs.md) | Copy, move and delete jobs: scheduler, conflicts, progress. |
| [docs/plugins.md](docs/plugins.md) | Core Plugins: manifest, capabilities, sandbox, limits, crashes. |
| [docs/indexer.md](docs/indexer.md) | The indexer: how volumes are indexed and kept current, the read-only pipe, the service, search without it. |
| [docs/terminal.md](docs/terminal.md) | The integrated terminal: profiles, the byte pipe of each session, following the active pane, closing, the `term` CLI. |
| [docs/ui.md](docs/ui.md) | The WinUI 3 shell: the solution, running and debugging, the core launcher, listings in shared memory, keys and the command router, the palette, file operations, search, plugins, the terminal, Tool Extensions, themes, the marketplace view, what is not built yet. |
| [docs/tool-extensions.md](docs/tool-extensions.md) | Tool Extensions: `tool.json`, where tools live, the page's rules, the messages between the window and a tool, Markdown Preview. |
| [docs/themes.md](docs/themes.md) | Colour themes: the JSON format, the shipped themes, the themes folder, live editing. |
| [docs/marketplace.md](docs/marketplace.md) | The marketplace: the index format, where installs go, the trust rules, a local index for testing. |
| [docs/release.md](docs/release.md) | Releases: how the zip is built, installed, removed, signed and published. |
| [CHANGELOG.md](CHANGELOG.md) | What each version adds. |
| [docs/research/](docs/research/README.md) | Research notes and proposals, not decisions: the Total Commander gap analysis behind the proposed Phase 11. |
| [docs/log/](docs/log/2026-09-28/README.md) | The build log: one report per phase, with every decision and its undo. |

## Layout

| Folder | Contents |
|---|---|
| `core/` | Rust workspace: `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` and their libraries |
| `ui/` | C# WinUI 3 solution: `CabinetOS.exe` |
| `sdk/` | Plugin interface (WIT), protocol schema, templates, themes |
| `docs/` | Governing documents, plan, decisions, design |
| `build/` | Release scripts: `release.ps1`, the installer and uninstaller, the notices generator, the winget manifests |

## Requirements

- Windows 11 22H2 (build 22621) or newer, x64.
- To run it: the .NET 10 runtime, the Windows App Runtime 2.5.1 or newer
  (x64), and the WebView2 Runtime, which every Windows 11 has. The installer
  checks all three and prints the command that installs a missing one.
- To build it: the toolchains in [docs/dev-setup.md](docs/dev-setup.md).

## Install

No release is published yet; build the zip with `build/release.ps1`
([docs/release.md](docs/release.md)). With the zip in your Downloads folder,
in PowerShell:

```powershell
Unblock-File "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip"
Expand-Archive "$env:USERPROFILE\Downloads\CabinetOS-0.1.0-win-x64.zip" "$env:TEMP\CabinetOS-0.1.0" -Force
powershell -ExecutionPolicy Bypass -File "$env:TEMP\CabinetOS-0.1.0\install.ps1" -StartMenu -AddToPath
```

This installs CabinetOS for you alone, in `%LOCALAPPDATA%\Programs\CabinetOS`,
with no administrator rights; `-StartMenu` and `-AddToPath` are optional.
`-AllUsers` installs into Program Files instead (run as administrator), and
`-AllUsers -Indexer` adds the indexer service for instant search of whole
volumes. To remove CabinetOS:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\CabinetOS\uninstall.ps1"
```

Settings, plugins and logs stay unless you add `-RemoveData`. The release is
not signed yet, so Windows SmartScreen may ask before the first start.
Details: [docs/release.md](docs/release.md).

## License

[MIT](LICENSE). The WinUI 3 frontend builds on Microsoft's Windows App SDK,
which comes under Microsoft's own license terms
([ADR 0010](docs/decisions/0010-windows-app-sdk-license-exception.md)); the
core, the indexer and the CLI have no such dependency.
