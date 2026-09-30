# Phase 17, in-app updates: the coder's hand-back (2026-09-30, coder on Opus)

The creator's request of 2026-09-30 (an Update button, the update running
inside the app with its progress and the release notes, never the install
menus again) became Phase 17 of [PLAN.md](../../PLAN.md) after two
question rounds in chat, and went to a coder on Opus at midday, in
parallel with the shell redesign; the window's part waited for Phase 16.
The hand-back, as reported, with the planning session's checks after it.

## The hand-back

Finished and pushed to main as 37c01bc..beb95db, without force. Rebased
twice; the first rebase conflicted with main's new `workspace_info`, both
sides kept and the schemas regenerated. `ProtocolTests` now counts 60
request types. Commits:

- 4e236f4 update: the `cabinetos-update` crate (check, download, SHA-256,
  rename-first swap, rollback, snooze, the Apps entry); the marketplace's
  download code moves into a shared `cabinetos_market::transfer`.
- 4259152 core, cli: protocol 14, the six update requests with
  `update_state` and the two events, the daily check, `cabinetos-cli
  update [status|check|download|apply|rollback|snooze] [--json]`, the
  `update.*` settings.
- dc15de0 build: `release.ps1 -Channel` writes `latest.json` and the
  notes; `install.ps1` and `uninstall.ps1` add and remove the Settings >
  Apps entry.
- 03239f4 docs: ADR 0014 with its index row; ipc.md, config.md and
  release.md (the Publish steps).
- b8e7aac ui: the four `update.*` commands, the pill, the dot on the menu
  button and in About, `UpdateDialog` with the notes rendered natively,
  the restart, tests. Also escapes a lone backtick in CHANGELOG
  ("Ctrl+`"), which broke half a line on GitHub and in the dialog.
- beb95db docs: ui.md "Updates", keybindings.md, the CHANGELOG entries,
  the crate maps in core/README and ARCHITECTURE (with a change-log row).

Checks after the last rebase, as the coder ran them: the five Cargo
checks exit 0 (772 tests passed, 5 ignored); regenerating the config and
protocol schemas changes nothing; the release build of the core and the
CLI passed; `dotnet build -warnaserror` with 0 warnings; 913 of 913 UI
tests with the end-to-end tests against the release core;
`check-scripts.ps1` passes in PowerShell 7.6.6 and 5.1.

The release script, run once in the worktree: `dist\update\stable\latest.json`
(0.1.0, the zip 71,928,129 bytes, its SHA-256 c567989b…bd44, requires the
Windows App Runtime 2.5 and .NET 10) and `notes-0.1.0.md` (the Unreleased
section, with a warning, since no `[0.1.0]` section existed yet). The
release folder: 70 files, 229.7 MB; the zip 68.6 MB; `winget validate`
passed. Link-definition stripping was added to the script afterwards and
not run again by the coder (the same regex worked in pwsh in the snapshot
run).

Checked beyond the tests:

- The snapshot aid: the dialog with the real CHANGELOG notes, the menu's
  "Restart to Update (0.2.0)" with its dot, and About's Update row all
  looked right.
- A real restart: the Debug window and the release core ran from a fake
  per-user install. Their files, the window's DLLs and resources included,
  moved into `previous\` while both ran; the window closed and the new
  `CabinetOS.exe` started from the install folder.
- A headless swap and rollback of the real release zip; the install and
  uninstall of the Apps entry in HKCU. No test keys were left behind.

Not verified by the coder: the live check with real keys (the handout
keeps it for the planning session); a restart into a truly newer build,
since both builds are 0.1.0, so the core only logged "the last swap is
not what runs" (the core's own tests cover the confirmation); that a
second window keeps the old version until it is closed. The all-users
path and elevation are not built. Nothing was uploaded or published.

Decisions (what — because — undo; the core's defaults are also listed in
[ADR 0014](../../decisions/0014-in-app-updates.md)):

1. The core sends the notes' text in `update_state` — the window does no
   file or network work (the first Prime Directive) — undo: send only
   `notes_url`.
2. A state `unchecked` — "up to date" before any check would be false —
   undo: fold it into `up_to_date`.
3. The field is `snoozed_until_ms` — every time on the wire ends in `_ms`
   — undo: rename it.
4. Two lock files in the update folder, and "restart" when another
   version is in place — two cores share that folder — undo: drop
   `STEP_LOCK` and `STATE_LOCK`.
5. Plugins may send only `update_status` — the other requests change the
   code that runs — undo: edit `NEVER_ALLOWED`.
6. The swap skips `install.ps1` and carries the install record over — the
   uninstall stays exact — undo: edit `NOT_INSTALLED` and `carry_record`.
7. The registry calls live in `cabinetos-fs` — unsafe code is allowed only
   there — undo: move them.
8. Notes links resolve to `blob/v<version>/`, only https and http links
   are clickable, underscores are never emphasis — safety, and names such
   as `update_status` stay intact — undo: change `ReleaseNotes`.
9. While an update waits, the menu shows "Restart to Update (version)"
   with a dot — as VS Code does — undo: drop `waitingUpdate` in
   `ShellMenu.Build`.
10. A pill click restarts at once — its label says Restart — undo: point
    it at `update.showNotes`.
11. A running transfer blocks the restart — the core stops with the window
    — undo: remove the check.
12. Rollback asks first — it restarts the app — undo: remove the dialog.
13. An explicit check opens the dialog even while snoozed — the user asked
    — undo: change `CheckForUpdatesAsync`.
14. The dialog opens by itself only for `downloaded`, once per version per
    run — no popup at every start — undo: change `UpdateText.OpensDialog`.
15. The restart goes through the close button's path (`CloseWindowAsync`,
    then `StartRestart`) — tabs are saved and two cores never overlap —
    undo: revert the `OnClosing` refactor.
16. No negative indent for list items — WinUI may refuse one at run time —
    undo: restore it in `UpdateDialog.Notes`.

Other notes from the coder: PLAN.md and the desk card were left to the
planning session; an early scratch file `msg1.txt` may have overwritten
one of the planning session's (later ones carry the `p17-` prefix).
