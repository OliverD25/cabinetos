# Architecture decision records

One file per decision. Each record holds the question, the choice, the reason,
and what follows from it. A record is never edited to say something else; a new
record supersedes it and both link to each other.

The Constitution outranks every record here. A record may refine the brief
([../ARCHITECTURE.md](../ARCHITECTURE.md)); it may not contradict an article.

| # | Decision | Date | Status |
|---|----------|------|--------|
| [0001](0001-frontend-language-csharp.md) | The WinUI 3 frontend is written in C# on .NET | 2026-09-28 | accepted |
| [0002](0002-separate-elevated-indexer.md) | Indexing runs in a separate elevated process; UI and core stay unelevated | 2026-09-28 | accepted |
| [0003](0003-license-mit.md) | The project is licensed under MIT | 2026-09-28 | accepted |
| [0004](0004-minimum-windows-11-22h2.md) | Minimum supported Windows is 11 22H2 (build 22621) | 2026-09-28 | accepted |
| [0005](0005-terminal-in-core-hidden.md) | The integrated terminal is core infrastructure, hidden by default | 2026-09-28 | accepted |
| [0006](0006-control-channel-json.md) | The control channel carries JSON | 2026-09-28 | accepted |
| [0007](0007-monorepo.md) | One repository holds core, UI, SDK and docs | 2026-09-28 | accepted |
| [0008](0008-brief-and-design-committed.md) | The brief and the design handout live in the repo; the brief is a living document | 2026-09-28 | accepted |
| [0009](0009-packaging.md) | Version 1 ships unpackaged, as a zip with an install script | 2026-09-28 | accepted; amended by 0014 and 0020 |
| [0010](0010-windows-app-sdk-license-exception.md) | The UI layer depends on Microsoft's Windows App SDK and WebView2 under their own license terms; a recorded exception | 2026-09-28 | accepted |
| [0011](0011-search-without-per-user-filtering.md) | Version 1 answers file names to any logged-on user, regardless of folder rights | 2026-09-28 | accepted |
| [0012](0012-marketplace-index-on-github-pages.md) | The marketplace index is served from GitHub Pages of a separate public repository | 2026-09-28 | accepted |
| [0013](0013-heavy-logging-may-wait.md) | In heavy logging mode only, the core's operations may wait for the log writer; the UI thread and the pipe never do | 2026-09-29 | accepted |
| [0014](0014-in-app-updates.md) | A per-user install updates itself from inside the app, with a swap it can roll back | 2026-09-30 | accepted; amended by 0018 |
| [0015](0015-user-programs-and-the-shell-menu.md) | The context menu comes from the config, starts only listed programs, and shows Windows' menu only on request | 2026-09-30 | accepted |
| [0016](0016-column-view.md) | A pane's tab can show its folder as columns (Miller columns), each column one watched listing, no preview column | 2026-10-01 | accepted |
| [0017](0017-file-tags.md) | File tags in the file's NTFS stream and in a catalog, a Tags section in the sidebar; written for the creator's decision, not built | 2026-10-01 | proposed |
| [0018](0018-setup-file-and-silent-updates.md) | A setup file (Inno Setup, per user) for the first install; a checked download installs itself and the app only asks for a restart | 2026-10-02 | accepted; decision 2 replaced by 0019 |
| [0019](0019-setup-installs-prerequisites-and-is-the-winget-package.md) | The setup file downloads and installs a missing prerequisite itself, and the winget package is the setup file | 2026-10-02 | accepted |
| [0020](0020-indexer-service-starts-by-itself.md) | The indexer service starts by itself after Windows restarts (automatic start, delayed) | 2026-10-02 | accepted |
| [0021](0021-symbols-in-their-own-zip.md) | The symbols (`.pdb` files) ship in a zip of their own, not in the release zip or the setup file | 2026-10-02 | accepted |
| [0022](0022-two-catalogues-extensions-and-themes.md) | The marketplace has two catalogues, extensions (`index.json`) and themes (`themes.json`), and the theme gallery is its own page | 2026-10-03 | accepted |
| [0023](0023-quick-view-viewer-contract.md) | Quick View is a panel of the window; every viewer is a Tool Extension page that claims kinds in `tool.json`, gets the file by web messages, and never takes the keyboard | 2026-10-03 | accepted |

## Template

```markdown
# ADR NNNN: <decision as a sentence>

- Status: proposed | accepted | superseded by NNNN
- Date: YYYY-MM-DD
- Decided by: the creator, asked in chat | the architect, as a default (say so)

## Context
What question came up and why it mattered.

## Decision
What was chosen.

## Consequences
What follows: what gets easier, what gets harder, what must be done because of it.
```
