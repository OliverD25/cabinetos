# ADR 0014: A per-user install updates itself from inside the app, with a swap it can roll back

- Status: accepted; amended by [ADR 0018](0018-setup-file-and-silent-updates.md) for the swap without a dialog
- Date: 2026-09-30
- Decided by: the creator, in two question rounds in chat on 2026-09-30
  (every answer the recommended option: the source, the flow, the channels,
  the scope, the notes, Later, the order, the Apps entry); the rest by the
  planning session and, where marked, by the implementing session as a
  default while the creator was away ([PLAN.md](../PLAN.md), Phase 17).
  It replaces two consequences of [ADR 0009](0009-packaging.md): "no
  automatic updates" and "CabinetOS does not appear in Settings > Apps".

## Context

The creator asked for updates from inside the app: an Update button, the
update running in the app with its progress shown, the release notes shown
to the user, and never the install menus again. Version 1 ships as a zip
with `install.ps1` (ADR 0009): an update meant downloading the next zip and
running the installer over the old folder, and CabinetOS had no entry in
Settings > Apps.

What decides the shape:

- The core already fetches a file over HTTPS with an `ETag`, downloads a
  zip, checks its SHA-256 and unpacks it safely: the marketplace
  ([marketplace.md](../marketplace.md)). The marketplace's site on GitHub
  Pages is an address the app already trusts (ADR 0012).
- Windows lets a running program's files be renamed on the same volume,
  but not deleted or overwritten. A folder whose files belong to running
  programs can be emptied by renames only.
- The per-user install (`%LOCALAPPDATA%\Programs\CabinetOS`) belongs to the
  user; an all-users install in Program Files needs elevation, and only
  that install may carry the indexer service (ADR 0009).
- A development build runs from `core\target\release`, next to the
  source. It must never overwrite itself.

## Decision

1. **The source.** The zip and its SHA-256 are assets of the public
   repository's GitHub Release. Each channel publishes one small
   `latest.json` on the marketplace site,
   `https://oliverd25.github.io/cabinetos-marketplace/update/<channel>/latest.json`:
   the newest version, the zip's address, hash and size, the address of the
   notes, and the runtimes it needs
   ([sdk/update/latest.schema.json](../../sdk/update/latest.schema.json)).
   HTTPS only; plain `http:` with `update.allowInsecure` for testing; a
   `file:` URL or a folder for tests. The hash in `latest.json` is the
   guard until the creator signs releases; an Authenticode check comes
   with the certificate.
2. **Channels.** `update.channel`: `stable` (default) or `preview`, each
   with its own `latest.json`. Versions compare as semantic versions; the
   stable channel never offers a pre-release.
3. **The flow.** A quiet check once a day (10 seconds after the start,
   then hourly whether a day has passed) when `update.check` is on, and on
   demand. A newer version downloads in the background with a status-bar
   pill; the downloaded zip is checked and unpacked into a staging folder.
   A dialog shows the version and its release notes with Restart now and
   Later. Restart now swaps and restarts the window. Full zips, no delta
   downloads.
4. **The swap, rename first.** Every file of the install folder moves into
   `previous\` inside it, then the new files are copied in. A move or a
   copy that fails half way puts everything back, so the old version stays
   whole. `previous\` stays until the next swap or a rollback: the
   rollback command is the same swap the other way. The version after a
   swap is confirmed at the next start, in the updater's state file.
5. **Scope.** Only a release (with `release.json` next to the core) that
   the user may change without elevation updates itself: the per-user
   install. An all-users install keeps the installer or winget, and the
   app says so with the release page's address. A development build
   never checks.
6. **Notes.** The CHANGELOG section of the version, published next to
   `latest.json` as `notes-<version>.md` by the release script: one source
   of truth. The window renders it natively (headings, lists, bold, links,
   code spans), with no WebView2.
7. **Later.** Snoozes a day; a dot with the waiting version on About (and
   the menu); the update commands in the palette any time; no popup at
   every start.
8. **The Apps entry.** The per-user install registers in Settings > Apps
   under the user's hive (no elevation); the all-users install under the
   machine's. The updater keeps the version and size current after a
   swap, and changes only an entry that names its own folder.
9. **Where it lives.** A small crate in the core (`cabinetos-update`),
   reusing the marketplace's code for addresses, downloads, hashes and
   zips (`cabinetos_market::transfer`); one dialog and one pill in the
   window.

Decided by the implementing session as defaults (say so to change any):

- The core reads the release notes and hands their text to the window in
  `update_state`, because the window does no network or file work of its
  own (brief §1). To undo: send only the address and let a Tool Extension
  show it.
- A state `unchecked` beside the planned ones, because "up to date" before
  any check would be untrue. To undo: fold it into `up_to_date` in
  `resting_phase` (`cabinetos-update`) and in the window's texts.
- The field is `snoozed_until_ms`, not `snoozed_until`, because every time
  on the wire ends in `_ms`. To undo: rename it in `UpdateStatus` and the
  window's model.
- Two cores (two windows) share the update folder, so a step holds a lock
  file there, `state.json` is read and changed under a second one, and a
  core that finds another version in place than the one it runs says
  "restart" instead of updating again. To undo: drop `STEP_LOCK` and
  `STATE_LOCK` in `cabinetos-update`.
- Plugins may read `update_status`, but never send the other update
  requests: like `install_extension`, they change the code the core runs.
  To undo: remove them from `NEVER_ALLOWED` in `cabinetos-plugins`.
- The swap copies every file of the release except `install.ps1` (the
  installer runs from the unpacked zip only, as it does today) and
  carries `.cabinetos-install.json` over with the new version and files,
  so `uninstall.ps1` removes what is there; it also removes `previous\`
  and `previous-old\`. To undo: `NOT_INSTALLED` and `carry_record` in
  `swap.rs`.

## Consequences

- **Version one can update itself** only if it is cut with the updater
  inside: the first release is built after this phase.
- **Publishing a release gains steps** ([release.md](../release.md),
  "Publish"): the zip and its hash go to the GitHub Release as before, and
  `latest.json` with the notes goes to the marketplace repository under
  `update/<channel>/`. Both are the creator's steps.
- **A running program's files move.** The window, the core and the command
  line keep running from `previous\` after a swap until the window
  restarts itself. A second window of the same install runs the old
  version until it is closed; its core says "restart" from then on.
- **A file held open without delete sharing blocks the swap,** which then
  puts everything back and reports the file. Nothing in a release is held
  that way by CabinetOS itself as far as the tests show (a copy of
  `cmd.exe` running from the folder moves); the window's own files under a
  real restart are checked in a live run, not in the unit tests.
- **The indexer service is not replaced,** because a per-user install has
  none; an all-users install with the service keeps the installer. The
  elevation branch the creator accepted is not built until an installer
  allows a per-user service.
- **ADR 0009's gaps close in part:** updates and the Apps entry exist for
  the per-user install; the all-users install gets the Apps entry, and its
  updates stay with the installer and winget.
