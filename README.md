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

Status: pre-alpha. The repository holds the governing documents and the
development plan; code starts with the Rust core scaffold (Phase 1).

## Documents

| Document | What it is |
|---|---|
| [CONSTITUTION.md](CONSTITUTION.md) | The twelve principles. Protected; changes need the creator's approval. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | The implementation brief: how it is built. Living document. |
| [docs/PLAN.md](docs/PLAN.md) | The development plan: decisions, phases, risks. |
| [docs/decisions/](docs/decisions/README.md) | One record per architecture decision. |
| [docs/design/](docs/design/ABOUT.md) | The design handout and clickable prototype. |
| [docs/dev-setup.md](docs/dev-setup.md) | What to install to build and test. |

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
