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

Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done, and
the core side of Phase 7: the governing documents, and a Rust core that lists
and watches directories in shared memory, serves its configuration, commands
and keymap, runs copy, move and delete jobs on per-disk queues, and runs
sandboxed WebAssembly plugins whose crashes it contains, all over a user-only
named pipe (`core/`, 307 tests, CI green). Phase 5, the WinUI 3 shell, needs
the .NET SDK; the core-side parts of Phases 6 and 8 (indexer, terminal host)
continue meanwhile.

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

## Layout

| Folder | Contents |
|---|---|
| `core/` | Rust workspace: `cabinetos-core.exe`, `cabinetos-indexer.exe`, `cabinetos-cli.exe` and their libraries |
| `ui/` | C# WinUI 3 solution: `CabinetOS.exe` |
| `sdk/` | Plugin interface (WIT), protocol schema, templates, themes |
| `docs/` | Governing documents, plan, decisions, design |

## Requirements

Windows 11 22H2 (build 22621) or newer. See
[docs/dev-setup.md](docs/dev-setup.md) for the toolchains.

## License

[MIT](LICENSE).
