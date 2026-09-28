# Build log, 2026-09-28: the first night

The creator asked for the plan to be executed as far as it could go while
they slept ("sleep mode"). The planning session handed each phase to a coder
agent with a self-contained specification, verified every result on the
development PC (build, tests, clippy, fmt, cargo-deny) and recorded the
outcome in [../../PLAN.md](../../PLAN.md). The coder's own reports are kept
here word for word, because each one lists every decision it made alone,
with the reason and the way to undo it.

| Phase | Report | Result |
|---|---|---|
| 0 | (planning session, no coder report) | License, hygiene, governing documents, decision records. Commits 6f0285a, 6208882. |
| 1 | [phase-1-report.md](phase-1-report.md) | Rust core scaffold, diagnostics, pipe, CLI ping. 73 tests. |
| 2 | [phase-2-report.md](phase-2-report.md) | Filesystem engine: listings in shared memory, watching, volumes. 147 tests. 100,000 entries in 75 ms (target 50). |
| 3 | [phase-3-report.md](phase-3-report.md) | Config file, command registry, keymap, chords, immutable tier. 226 tests. Hand edit live in 119 ms. |
| 4 | [phase-4-report.md](phase-4-report.md) | Copy, move, delete jobs on per-disk queues, conflicts, progress. 266 tests. 20 GiB + 10,000 files in 9.7 s. |
| 7 | [phase-7-report.md](phase-7-report.md) | Plugin host: WIT, manifests, capabilities, sandbox limits, crash containment. 307 tests. |
| 6 | [phase-6-report.md](phase-6-report.md) | Indexer: MFT enumeration, USN journal, elevated process, search fallback. 356 tests. 1.36 M entries in 3.3 s on CI. |
| 8 | [phase-8-report.md](phase-8-report.md) | Terminal host: ConPTY sessions, byte pipes, folder sync. 393 tests. |

Phase 5 (the WinUI 3 shell) waited for the .NET SDK, which was installed in
the morning. The phases were run in the order 0, 1, 2, 3, 4, 7, 6, 8.

How to read a report: "Decided" lists choices made without the creator, each
as *what — because why — undo: how*. "Needs the user" lists what only the
creator could do; those items were carried into the plan or done in the
morning. "Known gaps" and "Noticed out of scope" are the honest leftovers.
