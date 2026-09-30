# CabinetOS — Modern System Commander

## The Constitution comes first

[CONSTITUTION.md](CONSTITUTION.md) is the highest authority in this project. It is
loaded into every session below. Every design, architecture and feature decision
must be consistent with it.

- **Never edit CONSTITUTION.md without explicit approval from the creator in chat.**
  This covers wording, formatting, order and content. Approval is per change.
  Propose the change as an exact before/after text, then wait for a clear yes.
- **If a request conflicts with a principle, stop and say so.** Name the article
  by number and title (for example "Article 10, Zero-Bloat Foundation"), explain
  the conflict in one or two sentences, and ask how to proceed. Do not quietly
  work around a principle.
- **Cite articles when they drive a decision.** When a design choice follows from a
  principle, say which one. This keeps the link between code and principles visible.
- **When in doubt about scope, default to Article 10.** A feature that is not core
  file navigation belongs in an extension, not in the core application.

## The other governing documents

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) is the implementation brief. Its
  §1 **Prime Directives** bind every session: no file I/O or data processing in
  the UI, never block the UI thread, no features hardcoded into the core, clean
  UI by default. It is a living document: edit it when a decision changes it,
  and add a row to its change log saying why.
- [docs/PLAN.md](docs/PLAN.md) is the development plan: decisions, phases with
  done-when criteria, open questions, risks. A phase starts only when the
  creator says so.
- [docs/decisions/](docs/decisions/README.md) holds one ADR per decision. A new
  decision gets a new file and a row in the index; an old record is superseded,
  never rewritten.
- [docs/design/](docs/design/ABOUT.md) is the design handout. "FileForge" in it
  is the design codename; the product is CabinetOS.

## Repository layout

| Folder | Contents |
|---|---|
| `core/` | Rust Cargo workspace (all crates under `core/crates/`) |
| `ui/` | C# WinUI 3 solution (Phase 5 onward) |
| `sdk/` | WIT interface, protocol schema, plugin templates, themes |
| `docs/` | Governing documents |

## Build and test

Rust core (from `core/`):

```bash
cargo build --workspace && cargo test --workspace && cargo clippy --workspace --all-targets -- -D warnings && cargo fmt --all -- --check && cargo deny check
```

CI runs the same five commands on `windows-latest`. A change is not done until
they pass locally. Toolchains and setup: [docs/dev-setup.md](docs/dev-setup.md).

## Working rules

- Commit after each finished unit of work; the message says why. Push after
  each commit.
- Unsafe Rust only in the crates that talk to Windows (`ipc`, `fs`, `jobs`,
  `index`), every `unsafe` block with a `// SAFETY:` comment.
- Project skills live in `.claude/skills/`. `heavy-logging` says when and
  how to use the heavy logging mode to find a fault (a key that did
  nothing, a job that did not do what was asked, a stall, a crash) and how
  to read the chain of one action; read it before chasing such a fault.
- Implementation goes to the `coder` agent once the plan is concrete; small
  fixes are done directly.

## The desk

This project's task board (the desk) is bound in `.claude/notion-desk.json`. Its
card "Development Plan (mirror of the repo plan)" is a copy of
[docs/PLAN.md](docs/PLAN.md). The repo file is the source of truth. When
`docs/PLAN.md` changes, refresh that card from the file in the same session.

After a unit of work lands, the creator's global Stop hook (desk-watch, in
the global Claude config) asks the session for one short look at the desk:
a new card in an actionable column that can be worked on with nothing
blocking it is announced in a line and started (the creator's rule of
2026-09-30). At most once in 30 minutes.

@CONSTITUTION.md
