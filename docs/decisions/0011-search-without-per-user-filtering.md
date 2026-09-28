# ADR 0011: Version 1 answers file names to any logged-on user, regardless of folder rights

- Status: accepted
- Date: 2026-09-28
- Decided by: the architect's recommendation, taken on the creator's
  instruction in chat on 2026-09-28 ("where you can choose your recommended
  option, do this"). The creator can overturn it with a new record.

## Context

The indexer ([ADR 0002](0002-separate-elevated-indexer.md)) reads every
volume's Master File Table as LocalSystem, because that is the only way to
read it. It therefore knows every file name on the volume, including names
inside folders the asking user may not open. Phase 6 asked the creator
whether search hits should be filtered by the asking user's folder rights.

What weighs on it:

- **A per-hit check costs.** Filtering means an access check against each
  hit's NTFS permissions, with the asking user's token, before the answer
  goes out. A query that matches thousands of names would pay thousands of
  checks. The core's fallback walk (without the indexer) already sees only
  what the user can see, because it runs as the user.
- **The tool "Everything" answers names to everyone** and is the tool most
  users of a dual-pane file manager compare against. Its users expect names,
  not content: the index never holds file contents, and opening a hit still
  goes through NTFS, which refuses what the user may not read.
- **CabinetOS is a desktop tool for one person's PC.** Shared PCs with
  several accounts exist, but they are not the design centre.
- **The indexer's pipe is reachable by every local user** (medium integrity
  label, [indexer.md](../indexer.md)), so a filter would have to live in the
  indexer, keyed by the caller's identity, not in the core.

## Decision

Version 1 answers file names from the index to any user logged on to the
PC, regardless of that user's rights on the folders the names sit in. The
answer is names and locations only, never contents; opening a hit is checked
by NTFS as always.

## Consequences

- [indexer.md](../indexer.md) says so plainly, in a "What the index reveals"
  note: anyone who can log on to the PC can search all names on indexed
  volumes; a user who needs otherwise runs without the indexer (the
  fallback walk sees only what they may see) or does not install it.
- A later version may add an opt-in filter in the indexer (an access check
  per hit with the caller's token, taken from the pipe connection). That is a
  new record, not an edit of this one.
- Nothing in the core or the UI changes now.
