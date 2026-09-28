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
| [0009](0009-packaging.md) | Version 1 ships unpackaged, as a zip with an install script | 2026-09-28 | accepted |
| [0010](0010-windows-app-sdk-license-exception.md) | The UI layer depends on Microsoft's Windows App SDK and WebView2 under their own license terms; a recorded exception | 2026-09-28 | accepted |
| [0011](0011-search-without-per-user-filtering.md) | Version 1 answers file names to any logged-on user, regardless of folder rights | 2026-09-28 | accepted |
| [0012](0012-marketplace-index-on-github-pages.md) | The marketplace index is served from GitHub Pages of a separate public repository | 2026-09-28 | accepted |

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
