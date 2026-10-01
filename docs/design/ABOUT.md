# About this folder

`docs/design/` is a flat mirror of the folder `design_handoff_cabinetos/` in
the creator's Claude Design session "CabinetOS System Manager Design". That
session is the source of truth for CabinetOS design; this folder is its
committed copy, plus this file. The session's own conventions, which the
copy follows:

- Every `.md` handoff doc starts with a `## Changelog` block: one line per
  version, `vN · YYYY-MM-DD · summary`, newest first. The version in that
  block is what the sync compares.
- Where documents conflict, `SHELL_REDESIGN.md` and `COMPACT_THEME.md` win
  over `README.md` and `AGENT_HANDOUT.md`.
- The `.dc.html` pages are visual references built in HTML, never code to
  ship. The design is recreated with WinUI's native controls, Mica and
  Acrylic, Segoe UI Variable and Segoe Fluent Icons. When a detail of the
  text is unclear, open `CabinetOS Compact.dc.html` in a browser.

**The sync rule (the creator's standing rule of 2026-10-01).** Before any
design-driven work starts, the session's folder is fetched and each `.md`
file's changelog header is compared with the copy here. A newer version
updates this folder, is committed, and is reported to the creator before
any app code changes. The procedure is in
`.claude/skills/design-handouts/SKILL.md`.

## The files

- `README.md` (v1): the first design in full: tokens, every screen, state,
  keybinding and animation.
- `AGENT_HANDOUT.md` (v2): the coder's build order and acceptance; v2 points
  at the two docs below as taking precedence.
- `SHELL_REDESIGN.md` (v2, 2026-10-01): the shell as the window should be:
  one quiet top row, the workspace switcher in the sidebar's header, per
  pane a tab strip, a toolbar row and a path row, Find in pane, Quick Open.
  Its v1 (a workspace pill and a command center in the top row, the nav
  buttons in the breadcrumb row) was built as Phase 16 of
  [../PLAN.md](../PLAN.md); v2 is Phase 20.
- `COMPACT_THEME.md` (v1): the Commander Compact density preset. It ships as
  the theme of that name ([../themes.md](../themes.md), "Metrics and
  chrome").
- `ICON_HANDOFF.md` (v1) and `icons/`: the app icon, its size cuts and its
  geometry.
- `CabinetOS.dc.html`: the clickable prototype of the first design (its
  inline `COMMANDS` seeded the command registry in Phase 3).
  `CabinetOS Handout.dc.html`: the printable design handout.
  `CabinetOS Icons.dc.html`: the icon concepts. `CabinetOS Compact.dc.html`:
  the reference build of the shell redesign in Commander Compact metrics.
  `doc-page.js` and `support.js` are the pages' runtime, not design.

Where the window differs from the design on purpose: [../ui.md](../ui.md),
"The shell". Where the handout disagrees with the Constitution or the brief,
the consistency check in [../PLAN.md](../PLAN.md) section 4 records the
conflict and the phase where it is decided.

## History

- 2026-09-28: the first bundle, `Windows 11 File Manager Design.zip`, under
  the design codename "FileForge" (the product was always CabinetOS). Its
  pages `FileForge.dc.html` and `FileForge Handout.dc.html` differ from
  `CabinetOS.dc.html` and `CabinetOS Handout.dc.html` only by the name and
  the icon; git history before 2026-10-01 has them.
- 2026-09-29: `compact/` with the Commander Compact handout.
- 2026-09-30: the shell redesign v1, kept in `_io` next to the repository
  and built as Phase 16.
- 2026-10-01: the session gained `SHELL_REDESIGN.md` v2, the Compact page
  with the v2 shell and the changelog headers; the creator set the sync
  rule, and this folder became the flat mirror.

## Sync log

| When | What the session had, and what changed here |
|---|---|
| 2026-10-01 23:10 | First run of the sync rule. `SHELL_REDESIGN.md` v2 (its changelog block replaced the "Status: v2" line; the spec is the one fetched at 22:56). `AGENT_HANDOUT.md` v2 (new here; the "Update 2026-10-01" note and the changelog). `COMPACT_THEME.md` v1 (the changelog block added; moved up from `compact/`). `ICON_HANDOFF.md` v1 (new here). `README.md` v1 (replaced the FileForge-era copy: the name, the app icon paragraph, the changelog). The pages `CabinetOS.dc.html`, `CabinetOS Handout.dc.html`, `CabinetOS Icons.dc.html` and `icons/` came from the creator's export of 22:46 (`_io/design/CabinetOS System Manager Design.zip`), `CabinetOS Compact.dc.html` moved up from `compact/`; `doc-page.js` and `support.js` were already identical. |
