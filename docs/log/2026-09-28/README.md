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
| live check | [live-check.md](live-check.md) | Real keys and mouse clicks on the shell, screen unlocked: 100,000 entries shown 88 ms after Enter; PageDown held 5 s at 53–61 frames per second, worst frame 78 ms; the file keys checked on disk; the terminal, the palette from inside it, search and Markdown Preview; five findings for the shell. |
| install check | [install-check.md](install-check.md) | A real per-user install from the release zip on the development PC: the installed window passed the whole real-key live check with the core found next to it, the uninstall left nothing behind, and the twelve scripted installer cases behaved. |
| core for the UI, v11 | [protocol-11-report.md](protocol-11-report.md) | The window's own commands in the registry, `ui.dockSize`, readable type names, `items_per_second`, one marketplace item per extension with `installedVersion`, the Mica tint in `list_themes`, the `system` theme kind. 482 tests. |
| 10, core | [phase-10-report.md](phase-10-report.md) | Packaging: ADR 0009, `release.ps1`, the installer and uninstaller, the winget manifests, the changelog and the release guide; a 77 MB zip installed and removed in both PowerShells. 482 tests. |
| UI adopts v11 | [ui-protocol-11-report.md](ui-protocol-11-report.md) | The window's commands from the registry, `ui.dockSize`, items per second in the flyout, `installedVersion` in the marketplace, the `system` theme following Windows' mode. 418 UI tests. |
| UI, the five findings | [ui-findings-report.md](ui-findings-report.md) | The live check's five findings fixed: the preview chord, dialogs holding the keyboard, Unicode typing in the terminal, the focus after a decision pressed through UI Automation, tooltips. 420 UI tests. |
| 10, UI | [phase-10-ui-report.md](phase-10-ui-report.md) | Only the Windows App SDK components the window uses (the published window from 80 to 40 MiB), the resource file on a plain publish, the About dialog. 429 UI tests. |
| core self-review | [core-self-review-report.md](core-self-review-report.md) | Six fixes with failing-first tests (a leaked listing handle, a core running on after a thread panic, a poisoned index cache, three smaller ones), six leftovers, CI on demand. 496 tests. |
| step 2, UI | [step-2-ui-report.md](step-2-ui-report.md) | Edge cases in the shell, tests first: names beyond ASCII, long paths, links and cloud files, two windows, the fixture in the live checks. 485 UI tests. |
| step 2, core | [step-2-core-report.md](step-2-core-report.md) | Edge cases in the core, tests first: search folding, a UTF-16 config, long paths, link kinds and a move that no longer deletes through a link, cloud placeholders, lock files for two cores. 554 tests. |
| step 3 | [step-3-report.md](step-3-report.md) | Scrolling measured without real keys; a quarter less UI-thread work per page; the frame goal as one line of the live check, unproven until a real-key run on an awake display. 501 UI tests. |
| Compact, core | [compact-core-report.md](compact-core-report.md) | The theme format's 76 metrics and three chrome switches, and Commander Compact as the fifth shipped theme. 564 tests. |
| 11a, core | [phase-11a-core-report.md](phase-11a-core-report.md) | Protocol 12: keypad keys, several default keys per command, the 31 window commands of the Total Commander note, the six new requests. 604 tests. |
| core, last items | [core-last-items-report.md](core-last-items-report.md) | The theme-collection test, and a console window for console programs opened from a pane. 607 tests. |
| themes | [theme-collection-report.md](theme-collection-report.md) | 36 themes ported under their MIT licenses into the marketplace collection, with notices, contrast checks and snapshots; City Lights and Dainty left out. |
| research | [research-total-commander-report.md](research-total-commander-report.md) | The Total Commander gap analysis and the proposed Phase 11 (`docs/research/total-commander.md`), with ten questions for the creator. |

Phase 5 (the WinUI 3 shell) waited for the .NET SDK, which was installed in
the morning. The core phases were run in the order 0, 1, 2, 3, 4, 7, 6, 8.
From the morning on, two coder agents worked at the same time: one on the
core in the main checkout, one on the shell in a git worktree of its own.
The "core for the UI" rows are the small protocol additions the shell asked
for between its phases. GitHub Actions stopped starting jobs in the evening
(the account's billing), so the reports from Phase 9 on carry local results
only. The second night, 2026-09-29, followed the creator's chosen next steps
(a real install, an edge-case sweep with tests first, the scroll) and four
desk cards the creator filed (the theme collection, the Total Commander
research and its first sub-phase, the Commander Compact theme); a third
coder agent and a research agent worked in worktrees of their own for the
theme collection and the research note, and their reports here are the
copies the planning session received, since those agents left no
transcript file.

How to read a report: "Decided" lists choices made without the creator, each
as *what — because why — undo: how*. "Needs the user" lists what only the
creator could do; those items were carried into the plan or done in the
morning. "Known gaps" and "Noticed out of scope" are the honest leftovers.
