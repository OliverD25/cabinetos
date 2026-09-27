# ADR 0007: One repository holds core, UI, SDK and docs

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

The project has three code parts: the Rust core, the C# WinUI 3 frontend and
the plugin SDK (the WIT interface definitions and plugin templates). They
share one IPC contract and one shared-memory layout.

Options considered:

- **Monorepo.** One repository with `core/`, `ui/`, `sdk/` and `docs/`. One
  commit can change both sides of the IPC contract. One CI pipeline.
- **Separate repositories.** `cabinetos-core`, `cabinetos-ui`, `cabinetos-sdk`.
  Cleaner boundaries and independent versioning, but every contract change
  needs coordinated commits and releases across repositories.

## Decision

Monorepo, in the existing `cabinetos` repository.

## Consequences

- Layout: `core/` is a Cargo workspace, `ui/` is a C# solution, `sdk/` holds
  the WIT package, the protocol schema and plugin templates, `docs/` holds
  the governing documents.
- A change to `cabinetos-protocol` and its C# mirror lands in one commit, and
  the CI job that compares struct layouts runs on every push.
- CI uses path filters later, so a docs-only change does not build the world.
- Release versioning is one version for the whole product; the SDK may get
  its own version once external plugin authors depend on it.
