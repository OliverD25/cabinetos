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
| [scroll-item5-report.md](scroll-item5-report.md) | shell, Opus | the scroll-gap investigation; the heavy-logging window and the crash offer seen with real keys | 61f044d, 58fa975, c5ed4c8; 848 UI tests; the default theme at 0 % of frames over 20 ms; the report [scroll-gaps.md](scroll-gaps.md) |
| [rail-tree-at-start-report.md](rail-tree-at-start-report.md) | shell, Sonnet | the follow-up: the rail's folder tree empty at start | ca72efc; 849 UI tests; the whole live check with real keys exit 0, 106 True, 0 False, the scroll goal met |
| [claude-code-in-the-terminal.md](claude-code-in-the-terminal.md) | daytime: a coder on Sonnet, the planning session's checks | the `claude` terminal profile, `followsPane`, the core's folder on every session's `PATH`; Claude Code checked headless and with real keys; the Agent extension against a local model | 2f0af70 to 72a4bbc; 726 core, 852 window; the probe `claude-terminal.ps1` every check True |
| [phase-16-shell-redesign-report.md](phase-16-shell-redesign-report.md) | daytime: a coder on Opus, the planning session's checks, a coder on Sonnet for the live check's evidence | Phase 16, the shell redesign: one top row, per-pane breadcrumbs, find in pane, Quick Open; the git read moved into the core; the live check's section 16 reads log lines after they arrive | ffcdea3 to ff2f84d, 2656e01, 7fdd634; 735 core, 890 window; the live check 118 True, 6 False in the new section 16 (evidence faults), then 127 True, 0 False after the fix; the Claude Code probe every check True |
| [phase-18-context-menu-report.md](phase-18-context-menu-report.md) | evening and night: two coders on Opus, the planning session's checks | Phase 18: the context menu from `cabinetos.json`, programs as `program.<name>` commands, Windows' menu opt-in through the core on a COM thread, protocol 15, the `CommandBarFlyout` in the window, ADR 0015 (step 1); the edit mode inside the menu (step 2); a step 1 flyout fault found and fixed with a test | 49ca8b7 to cc8d5b3, acc0e30 to 4e20903, 9046741 to 5d34050; 795 core, 964 window with the end-to-end tests; the live check 159 True, 0 False, section 18 all 32 True with a real drag; earlier runs 143 True 2 False (the section's own reading) and 144 True 1 False |
| [phase-17-in-app-updates-report.md](phase-17-in-app-updates-report.md) | daytime: a coder on Opus, the planning session's checks | Phase 17, in-app updates: the `cabinetos-update` crate, protocol 14, `cabinetos-cli update`, `latest.json` and the notes from the release script, the Settings > Apps entry, the pill, the dialog and the restart in the window; the CHANGELOG became 0.1.0 and the first release was built | 4e236f4 to beb95db, 4b102b6; 772 core, 913 window with the end-to-end tests; the release 0.1.0 built unsigned (the zip 68.7 MB, `latest.json`, the notes); the live check 127 True, 0 False |
| [theme-picker-preview-report.md](theme-picker-preview-report.md) | evening, the creator at the PC: a coder on Opus, the planning session's checks | the theme picker previews the highlighted theme live (by keys or hover) through `get_theme`, nothing written until Enter; Esc and every other way out restore; the picker's rows keep their sizes while a density preset is previewed | 7877bd1 to 5eb14f7; 974 window tests with the end-to-end tests; the live check waits for the evening's other fixes |

The extension agent's earlier per-item reports (item 2b, item 3) never
reached the planning session; its final hand-back above carries the run's
summary, and the commit messages and [extensions/agent.md](../../extensions/agent.md)
hold the rest. A request to send them again was refused twice by an API
safeguard, so the lesson is recorded: reports are handed back at the time,
never asked for again word for word.

Every item of the night is reported above, including the one follow-up
that ran after the list was complete: in the rail layout the folder tree
was empty at start until a key opened it, found by the real-key check of
the heavy-logging window and fixed the same day (the last row). The night
ended with the whole live check passing with real keys and the mouse,
exit code 0, and main at 849 window tests and 722 core tests.

Other records of the night: the live-check runs in
[../2026-09-28/live-check.md](../2026-09-28/live-check.md) (run 5 and the
tabs and section-14 runs); the decision record
[ADR 0013](../../decisions/0013-heavy-logging-may-wait.md); the public
marketplace index, pushed at 167256f of `OliverD25/cabinetos-marketplace`
and served since the creator turned Pages on at about 00:05.
