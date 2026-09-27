# ADR 0002: Indexing runs in a separate elevated process; UI and core stay unelevated

- Status: accepted
- Date: 2026-09-28
- Decided by: the creator, asked in chat

## Context

Reading the MFT (the NTFS master file table, the disk's own list of every
file) and the USN Journal (NTFS's change log) both need Administrator rights.
A normal application does not have them. The brief (§2) says "a background
worker requiring Admin privileges" without saying where that worker lives.

Options considered:

- **Separate elevated indexer.** The UI and the core run as a normal user. A
  small indexer program runs as admin (as a Windows service installed once, or
  started on demand with one UAC prompt) and hands the index to the core.
  Without it, the core falls back to fast NT directory reading. This is how
  the "Everything" search tool works.
- **Whole core elevated.** One UAC prompt at every start. Every file operation
  runs as admin, so a mistaken delete can hit system files, and Windows blocks
  drag-and-drop between elevated and normal windows.
- **No MFT indexing in v1.** Conflicts with Article 1 ("real-time NT-level
  file indexing") unless indexing is added before the first release.

## Decision

A separate elevated indexer process, `cabinetos-indexer.exe`.

## Consequences

- Three executables: `CabinetOS.exe` (UI), `cabinetos-core.exe`,
  `cabinetos-indexer.exe`. See PLAN.md section 3.
- The indexer answers read-only queries over its own named pipe and never
  performs file operations (least privilege). No plugin code runs inside it.
- The core must work unchanged when the indexer is absent: directory listing
  uses the NT API directly, search falls back to a walk.
- Service installation and on-demand elevation are Phase 6 work; the install
  method is an open question recorded in PLAN.md section 7.
