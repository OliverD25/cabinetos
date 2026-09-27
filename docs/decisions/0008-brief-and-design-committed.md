# ADR 0008: The brief and the design handout live in the repo; the brief is a living document

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

The creator delivered two documents outside the repository: the
implementation brief (as chat text) and the design handout (a zip in the
`_io/design` folder next to the repository). Future sessions and contributors
cannot see either unless they are committed.

Options considered:

- **Commit both; the brief is living.** The brief becomes
  `docs/ARCHITECTURE.md` and is updated as decisions are made, each decision
  also recorded as an ADR in `docs/decisions/`. The design bundle goes to
  `docs/design/`. Only the Constitution stays protected by the edit hook.
- **Commit both; protect the brief too.** Same, but every change to the brief
  needs the creator's approval through the same hook. More ceremony for a
  document full of implementation details that will change.
- **Do not commit them.** Nothing to maintain, no history, invisible to
  everyone else.

## Decision

Commit both. The brief is a living document; the Constitution stays the only
protected file.

## Consequences

- `docs/ARCHITECTURE.md` keeps the creator's text word for word, plus a table
  of the decisions that refine it and a change log at the end. Every edit to
  the brief adds a change-log row that says why.
- `docs/design/` holds the handout unchanged, with `ABOUT.md` explaining that
  "FileForge" is the design codename and that the files are references, not
  code.
- New decisions get a new ADR file and a row in `docs/decisions/README.md`.
- The Constitution's edit hook is unchanged; it does not cover the brief.
