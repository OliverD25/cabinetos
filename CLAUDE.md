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

## The desk

This project's task board (the desk) is bound in `.claude/notion-desk.json`. Its
card "Development Plan (mirror of the repo plan)" is a copy of
[docs/PLAN.md](docs/PLAN.md). The repo file is the source of truth. When
`docs/PLAN.md` changes, refresh that card from the file in the same session.

@CONSTITUTION.md
