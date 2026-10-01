---
name: design-handouts
description: How the design documents and pages of the creator's Claude Design session "CabinetOS System Manager Design" come into this project, and the standing sync rule of 2026-10-01 - before any design-driven work starts, fetch the session's folder design_handoff_cabinetos/, compare each .md file's "## Changelog" header with the copy in docs/design/, update docs/design/ and commit when a version is newer, and tell the creator what changed before touching app code. Use it when the creator says "take the new brief from design", "there is a new doc in Claude Design", "fetch the design handout", "sync the design", "what did the design session produce", names a doc of that session (README, AGENT_HANDOUT, SHELL_REDESIGN, COMPACT_THEME, ICON_HANDOFF, a .dc.html page), asks to compare our shell or themes with the design, or when a phase, a card or a coder handout cites a design doc. Read-only towards the design session. Also covers the one-time login the tool needs and what to do when a file is too large to read.
---

# Design handouts from Claude Design

The creator designs CabinetOS in a Claude Design session named
"CabinetOS System Manager Design". Its folder `design_handoff_cabinetos/`
tells this project what to build and how it should look, and
`docs/design/` in the repository is its committed mirror. This skill is the
path from that session into the repository and onto the desk, with no file
passing through the creator's hands. Decisions of the creator behind it:
2026-09-30, every kind of doc counts (text briefs, design pages, tokens), a
fetch happens on the creator's word, and if a direct read ever fails the
creator pastes a share link; 2026-10-01, the standing sync rule of section
3 (fetch before any design-driven work, compare the changelog headers,
update `docs/design/`, commit, report before touching app code).

## 1. What the session is, and the one rule

- Project id: `91867795-1bc6-4473-9396-ba1553f7583c`
  (`https://claude.ai/design/p/91867795-1bc6-4473-9396-ba1553f7583c`).
  It is a plain design project (type `PROJECT_TYPE_PROJECT`), not a
  design system.
- **Read-only.** Nothing is ever written into that session from here:
  no `finalize_plan`, no `write_files`, no `delete_files`. The design
  session tells us what to do; we do not tell it. A request to push
  something there is a question for the creator, not an action.
- **Only `design_handoff_cabinetos/` matters.** The session also holds
  copies of the pages and icons at its root, `uploads/`, `.thumbnail`
  and its own `CLAUDE.md` (the conventions below). None of those is
  pulled.
- The session's conventions (its `CLAUDE.md`, read 2026-10-01): every
  `.md` handoff doc starts with a `## Changelog` block, one line per
  version, `vN · YYYY-MM-DD · summary`, newest first, bumped on every
  edit; `SHELL_REDESIGN.md` and `COMPACT_THEME.md` override `README.md`
  and `AGENT_HANDOUT.md` where they conflict; spec text gives metrics as
  `default [compact]`; the `.dc.html` pages are visual references, never
  shipped code. When a spec detail is unclear, open
  `CabinetOS Compact.dc.html` in a browser.

## 2. The tool, and the login it needs

The `DesignSync` tool reads the session (load it with
`ToolSearch "select:DesignSync"` if it is not in the tool list). Its
read methods need no permission prompt once the login is done:

- `get_project` with `projectId`: the name and type. Use it once to
  confirm the id still points at the right session.
- `list_files` with `projectId`: every path in the session. It gives
  names only, no sizes and no dates, so "changed" is found by reading.
- `get_file` with `projectId` and `path`: one file's content, at most
  256 KiB; the result says `truncated: true` when it was cut, and
  `isBase64: true` for a binary file.

The login is one command per machine, in an interactive Claude Code
session of version 2.1.28x or newer (the desktop app's own session
cannot run it): `/design-login`, which opens the browser once. On this
PC it was done on 2026-09-30 with the copy at
`E:\codespace\.local\bin\claude.exe` (the copy in
`C:\Users\Admin\.local\bin` was older). If a call answers "DesignSync
needs design-system authorization", say so to the creator and give
them that command; do not look for another way in. Until the login
works, the creator's export zip (Claude Design's download of the
project) in `_io/design/` is the fallback source; say which source was
used.

## 3. The sync: when and how

Run it in two cases: when the creator says a new doc is there or names
one, and **before any design-driven work starts**: a phase, a desk card
or a coder handout that cites a design doc, or a task that changes the
shell, a theme, the icon or anything the handout specifies. Never from a
hook or a schedule on its own: the creator chose "on my word" so that a
design in progress is not read half-done, and the rule above is their
word for the start of design work.

1. `list_files` the session; keep the paths under
   `design_handoff_cabinetos/`.
2. `get_file` every `.md` there. Read its `## Changelog` block (the first
   line is the current version). Compare with the same file in
   `docs/design/`: the same version means nothing to do for that file; a
   newer version, or no copy here, means an update.
3. For an update, write the file byte for byte into `docs/design/<the
   same name>` (the folder is flat; `icons/` is its one subfolder) with
   the Write tool, not through a heredoc (the Bash tool mangles
   backslashes). Fetch the `.dc.html` pages, `doc-page.js` and
   `support.js` the same way when they fit in 256 KiB; a page that comes
   back `truncated: true` is not kept: take it from the creator's export
   zip or ask for the page's share link. Icons come as base64 and are
   decoded into the files; fetch them when a changelog names them or a
   task needs them.
4. Add a row to the sync log at the end of `docs/design/ABOUT.md`: the
   time, each file's version and what changed. Commit `docs/design/` with
   a message that says which versions arrived ("design: SHELL_REDESIGN v2
   …"), push.
5. Tell the creator what changed, one line per file, with the version and
   whether it is new, changed or the same, **before touching app code**.
   Only then does the design-driven work start, and its handout cites
   the `docs/design/` paths.

The session's `.md` files reach the repository as they are, changelog
block included; `docs/design/ABOUT.md` is the only file of the folder
written here.

## 4. From a doc to work

- **A brief** (a text doc that says what to build): it becomes a desk
  card in Inbox with a first pass, as the desk-check skill describes:
  what exists, what the brief adds, Constitution notes by article,
  overlaps, size, the open questions for the plan stage. Quote the
  brief's key sentences into the card; link the file's path in
  `docs/design/`. The creator triages it; the plan stage asks the
  questions; a coder gets the doc's path in its handout, as the shell
  redesign did (Phase 16 from v1, Phase 20 from v2).
- **A design page** (`.dc.html`): the visual reference for a coder. It
  is not code to port (the session's own README says so): the design is
  recreated with WinUI's native controls. Hand the path to the coder
  with the brief.
- **Tokens** (colors, fonts, metrics in a README or a theme doc): compare
  with `sdk/themes/default.json` and `commander-compact.json`; a
  difference is a finding for the card, not a silent change to a theme.
- What the session has produced so far, and where it went: the README
  and AGENT_HANDOUT (the first shell, Phases 5 to 9), COMPACT_THEME (the
  Commander Compact theme, Phase 9), ICON_HANDOFF (the app icon),
  SHELL_REDESIGN v1 (Phase 16) and v2 (Phase 20), "Pane Tabs Options"
  (Phase 12's card; a root-level page, fetched once on 2026-09-30 into
  `_io/design/claude-design/`).

## 5. Rules

1. Read-only towards the session, always (section 1).
2. Fetch on the creator's word and before any design-driven work; never
   from a hook or a schedule.
3. `docs/design/` is the committed mirror of `design_handoff_cabinetos/`
   and nothing else lives there but `ABOUT.md`. `_io/design/claude-design/`
   holds the fetches before 2026-10-01 and their `FETCHED.md`, closed;
   the creator's earlier uploads stay where they are (`_io/SHELL_REDESIGN.md`,
   `_io/design`, `_io/design2`).
4. The sync log in `docs/design/ABOUT.md` is the record: every run adds a
   row, so the next session knows what is new.
5. A truncated or unreadable file is reported, never guessed at.
6. A newer design document is reported to the creator before any app
   code changes for it.
