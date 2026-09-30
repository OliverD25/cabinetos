# Build log, 2026-09-30: the second sleep-mode run

The night of 2026-09-29/30. The creator chose the whole list before
sleeping: the three cards of the evening (tabs per pane, the activity rail
and modular sidebar, the AI agent extension), the two open shell faults of
the live check, the scroll-gap investigation, and a late fourth idea, the
heavy logging mode, whose six design questions were answered in chat the
same night. The plan's Phases 12 to 15 in [PLAN.md](../../PLAN.md) hold
the decisions and the results; this folder holds the agents' reports, one
per finished item, verbatim as they were handed back, with every
unattended decision as a `what — because — undo` line.

Six coder agents worked in their own worktrees, routed by the
model-budget rule: Opus for the items where a mistake hides (protocol 13's
design, the focus fault, the heavy sink's wait rule, the crash hook, the
scroll analysis), Sonnet for the items with a concrete plan (tabs, the
window's parts, the extension, the docs). The planning session pulled main
after each report, ran the five core checks and the window's full test
suite against a release core, and accepted or sent fixes; every acceptance
is a plan paragraph with its commits.

| Report | Agent | Item | Result |
|---|---|---|---|
| [shell-item1-live-check-faults-report.md](shell-item1-live-check-faults-report.md) | shell, Opus | the two open faults of run 4 | both fixed; live check run 5 all True; 650 UI tests |
| [diag-item1-trace-id-report.md](diag-item1-trace-id-report.md) | diagnostics, Opus | Phase 15: one trace id per user action | 054a1fa, 687abd3, d60d82f |
| [diag-item2-heavy-mode-core-report.md](diag-item2-heavy-mode-core-report.md) | diagnostics, Opus | Phase 15: the heavy sink in the core | 3f053d3, bd36611 |
| [diag-item4-bundles-core-report.md](diag-item4-bundles-core-report.md) | diagnostics, Opus | Phase 15: log bundles, the core part; timing tests hardened | 840b22a |
| [diag-window-report.md](diag-window-report.md) | diagnostics, Sonnet | Phase 15: the window's half, docs, ADR 0013, the window's crash bundle | c6e826b to 0563e51; 766 UI tests |
| [phase-12-tabs-report.md](phase-12-tabs-report.md) | shell, Sonnet | Phase 12: tabs per pane | 725328f to 80c204b; 676 UI tests; live check section 12 all True |
| [phase-14a-core-report.md](phase-14a-core-report.md) | core, Opus | the public index as the default; `ui.tabs`; Phase 14a, protocol 13 | 349a59f, 77c1169, 46c644c to 4feb72f; 700 core tests |
| [phase-14-window-report.md](phase-14-window-report.md) | shell, Sonnet | Phase 14: the window's parts | 401d439 to d6e2de7; 767 UI tests; live check section 14's drag True |
| [phase-14b-e-extension-report.md](phase-14b-e-extension-report.md) | extension, Sonnet | the two core additions; Phase 14b to 14e, the Agent extension | 3887a4c to 9e8d8e5; 722 core tests, 90 plugin tests |
| [phase-13-rail-report.md](phase-13-rail-report.md) | shell, Sonnet | Phase 13: the activity rail and the modular sidebar; a tool page runs its plugin's commands; the live check's "ask" step | 68ee1f0 to 1a05a5c; 839 UI tests; whole-script live check 105 True, 0 False |

The extension agent's earlier per-item reports (item 2b, item 3) never
reached the planning session; its final hand-back above carries the run's
summary, and the commit messages and [extensions/agent.md](../../extensions/agent.md)
hold the rest. A request to send them again was refused twice by an API
safeguard, so the lesson is recorded: reports are handed back at the time,
never asked for again word for word.

Reports still to come when this file was last written: the rail and the
sidebar (Phase 13), and the scroll-gap investigation.

Other records of the night: the live-check runs in
[../2026-09-28/live-check.md](../2026-09-28/live-check.md) (run 5 and the
tabs and section-14 runs); the decision record
[ADR 0013](../../decisions/0013-heavy-logging-may-wait.md); the public
marketplace index, pushed at 167256f of `OliverD25/cabinetos-marketplace`
and served since the creator turned Pages on at about 00:05.
