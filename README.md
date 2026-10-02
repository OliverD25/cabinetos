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

## Status

Early. 0.1.0 is the first release: Windows 11, x64 only. The work follows a
written [plan](docs/PLAN.md), and [CHANGELOG.md](CHANGELOG.md) lists what each
version has. What works in 0.1.0:

- Two file panes with tabs, breadcrumbs, a column view, pinned folders and
  the drives; listings of 100,000 entries in about 50 ms.
- Copy, move, delete and rename on per-disk queues, with conflict decisions
  that never stop the rest of a job, and undo.
- Search by name: whole NTFS volumes through an optional elevated indexer,
  or a walk of the folders without it.
- The command palette, rebindable keys, chord keys, and a right-click menu
  defined in the settings file.
- An integrated terminal in a dock: PowerShell, Command Prompt or WSL, each
  linked to a pane.
- Colour themes in JSON, applied live; one settings file, `cabinetos.json`,
  that applies live too.
- Sandboxed WebAssembly plugins and Tool Extensions from a marketplace index,
  each download checked by its SHA-256. Markdown Preview and an AI agent are
  opt-in extensions, not part of the core.
- A setup file, and updates that install themselves for a per-user install.

Not there yet: code signing (Windows SmartScreen asks before the first
start), a graphical settings view (the plan's Phase 11), ARM64, and a winget
package.

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
| [docs/diagnostics.md](docs/diagnostics.md) | The JSON Lines log format, trace ids, heavy logging mode and crash traces. |
| [docs/config.md](docs/config.md) | `cabinetos.json`: every key, its default, and how changes apply live. |
| [docs/keybindings.md](docs/keybindings.md) | Key grammar, chords, the Immutable System Tier, conflict rules. |
| [docs/jobs.md](docs/jobs.md) | Copy, move and delete jobs: scheduler, conflicts, progress. |
| [docs/plugins.md](docs/plugins.md) | Core Plugins: manifest, capabilities, sandbox, limits, crashes. |
| [docs/indexer.md](docs/indexer.md) | The indexer: how volumes are indexed and kept current, the read-only pipe, the service, search without it. |
| [docs/terminal.md](docs/terminal.md) | The integrated terminal: profiles, the byte pipe of each session, following the active pane, closing, the `term` CLI. |
| [docs/ui.md](docs/ui.md) | The WinUI 3 shell: the solution, running and debugging, the core launcher, listings in shared memory, keys and the command router, the palette, file operations, search, plugins, the terminal, Tool Extensions, themes, the marketplace view, what is not built yet. |
| [docs/tool-extensions.md](docs/tool-extensions.md) | Tool Extensions: `tool.json`, where tools live, the page's rules, the messages between the window and a tool, Markdown Preview. |
| [docs/extensions/agent.md](docs/extensions/agent.md) | The Agent extension (a plugin and a chat page): models and keys, the three tiers, previews and undo, the audit log, building and packing it. |
| [docs/themes.md](docs/themes.md) | Colour themes: the JSON format, the shipped themes, the themes folder, live editing. |
| [docs/marketplace.md](docs/marketplace.md) | The marketplace: the index format, where installs go, the trust rules, a local index for testing. |
| [docs/release.md](docs/release.md) | Releases: how the zip and the setup file are built, installed, removed, updated, signed and published. |
| [CHANGELOG.md](CHANGELOG.md) | What each version adds. |
| [docs/research/](docs/research/README.md) | Research notes and proposals, not decisions: the Total Commander gap analysis behind the proposed Phase 11. |
| [docs/log/](docs/log/2026-09-28/README.md) | The build log: one report per phase, with every decision and its undo. |

## Layout

| Folder | Contents |
|---|---|
| `core/` | Rust workspace: `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` and their libraries |
| `ui/` | C# WinUI 3 solution: `CabinetOS.exe` |
| `sdk/` | Plugin interface (WIT), protocol schema, templates, themes, extensions |
| `docs/` | Governing documents, plan, decisions, design, build log |
| `build/` | Release scripts: `release.ps1`, the setup file, the installer and uninstaller, the notices generator, the winget manifests |

## Requirements

- Windows 11 22H2 (build 22621) or newer, x64.
- To run it: the .NET 10 runtime, the Windows App Runtime 2.5.1 or newer
  (x64), and the WebView2 Runtime, which every Windows 11 has. The setup file
  downloads and installs a missing one from Microsoft (for the .NET runtime
  Windows asks for administrator rights). The zip's installer script checks
  all three and names the command that installs a missing one.
- To build it: the toolchains in [docs/dev-setup.md](docs/dev-setup.md).

## Install

Releases are on the [Releases page](https://github.com/OliverD25/cabinetos/releases).
The simplest install is the setup file,
`CabinetOS-<version>-win-x64-setup.exe`: a double-click, for you alone, with
no administrator rights, and later versions install themselves. The zip,
`CabinetOS-<version>-win-x64.zip`, holds the same programs and an
`install.ps1`, which can also install for every user and add the indexer
service. Each file has a `.sha256` beside it. How to install, update and remove it,
and every switch: [docs/release.md](docs/release.md).

The release is not code-signed yet, so Windows SmartScreen may ask before
the first start.

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

## Build from source

With the toolchains of [docs/dev-setup.md](docs/dev-setup.md), in PowerShell:

```powershell
git clone https://github.com/OliverD25/cabinetos.git; cd cabinetos\core; cargo build --workspace; cd ..\ui; dotnet build CabinetOS.sln
```

The window started from `ui\` finds the core in `core\target\debug`
([docs/ui.md](docs/ui.md), "Starting the core"). The checks every change
passes, the core's five and the window's tests, are in
[docs/dev-setup.md](docs/dev-setup.md); the release zip and the setup file
come from `build\release.ps1` ([docs/release.md](docs/release.md)).

## Problems and ideas

This is the first release. Please report problems and ideas as
[issues](https://github.com/OliverD25/cabinetos/issues): what you did, what
happened, and the CabinetOS version (the palette's "Help: About CabinetOS"
shows it). A crash writes a `crash-*.json` trace into
`%LOCALAPPDATA%\CabinetOS\logs` ([docs/diagnostics.md](docs/diagnostics.md));
attaching it helps. For anything that does not fit an issue, write to
redrickcarter39@gmail.com.

## License

[MIT](LICENSE). The WinUI 3 frontend builds on Microsoft's Windows App SDK,
which comes under Microsoft's own license terms
([ADR 0010](docs/decisions/0010-windows-app-sdk-license-exception.md)); the
core, the indexer and the CLI have no such dependency. A release carries
the license of every third-party component in its `THIRD-PARTY-NOTICES.md`.
