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
| 2, follow-up | [phase-2-follow-up-report.md](phase-2-follow-up-report.md) | The pipelined listing: enumeration, sort and the shared-memory write overlap. 100,000 entries in 46–49 ms (target 50 met). |
| 5 | [phase-5-report.md](phase-5-report.md) | WinUI 3 shell v1: two panes over the shared-memory listings, breadcrumbs, the command palette with chords and inline rebinding, Mica. 158 UI tests. Both panes 0.82–0.87 s after start. |
| core for the UI, v8 | [protocol-8-report.md](protocol-8-report.md) | `list_volumes` and `volumes_changed`, `get_value` and `set_value`, `open_path`, `create_directory`, `rename`; CI runs only the jobs a push touches. |
| 5b | [phase-5b-report.md](phase-5b-report.md) | File operations in the window: copy, move, delete, rename, new folder, open with; the transfer flyout, conflicts, the context menu, the in-app clipboard. 207 UI tests. |
| core for the UI, v9 | [protocol-9-report.md](protocol-9-report.md) | The shell's commands in the core's registry with their keys; `describe_entries` and `get_icon` for type names and icons. |
| 5c | [phase-5c-report.md](phase-5c-report.md) | The UI halves of Phases 6, 7 and 8: the search field, the plugin list and the permissions review, the terminal in the Tool Dock, Tool Extensions with Markdown Preview. 327 UI tests. |
| 9, core | [phase-9-core-report.md](phase-9-core-report.md) | Themes as one Rust type with four shipped themes and live apply; the marketplace client with SHA-256-checked installs. 477 tests. |
| 9, UI | [phase-9-ui-report.md](phase-9-ui-report.md) | Themes applied live, the theme picker, the marketplace view. 395 UI tests. A light theme ended the window; fixed. |
| UI self-review | [ui-self-review-report.md](ui-self-review-report.md) | A review pass over the whole shell: 13 fixes (a file probe off the UI thread, focus order in overlays, stale colours, Esc in the flyout). 412 UI tests. |
| core for the UI, v11 | [protocol-11-report.md](protocol-11-report.md) | The window's own commands in the registry, `ui.dockSize`, readable type names, `items_per_second`, one marketplace item per extension with `installedVersion`, the Mica tint in `list_themes`, the `system` theme kind. 482 tests. |

Phase 5 (the WinUI 3 shell) waited for the .NET SDK, which was installed in
the morning. The core phases were run in the order 0, 1, 2, 3, 4, 7, 6, 8.
From the morning on, two coder agents worked at the same time: one on the
core in the main checkout, one on the shell in a git worktree of its own.
The "core for the UI" rows are the small protocol additions the shell asked
for between its phases. GitHub Actions stopped starting jobs in the evening
(the account's billing), so the reports from Phase 9 on carry local results
only.

How to read a report: "Decided" lists choices made without the creator, each
as *what — because why — undo: how*. "Needs the user" lists what only the
creator could do; those items were carried into the plan or done in the
morning. "Known gaps" and "Noticed out of scope" are the honest leftovers.
