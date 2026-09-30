---
name: design-handouts
description: How a design brief, a design page or a set of design tokens travels from the creator's Claude Design session "CabinetOS System Manager Design" into this project without the creator downloading and pasting files. Use it when the creator says "take the new brief from design", "there is a new doc in Claude Design", "fetch the design handout", "what did the design session produce", names a doc of that session (README, AGENT_HANDOUT, SHELL_REDESIGN, COMPACT_THEME, ICON_HANDOFF, a .dc.html page), or asks to compare our shell or themes with the design. Read-only towards the design session; fetch only on the creator's word; land the files in _io/design/claude-design/; a brief then goes through the desk-check flow. Also covers the one-time login the tool needs and what to do when a file is too large to read.
---

# Design handouts from Claude Design

The creator designs CabinetOS in a Claude Design session named
"CabinetOS System Manager Design". Its documents tell this project what
to build and how it should look. This skill is the path from that
session to a desk card, with no file passing through the creator's
hands. It was set up on 2026-09-30 with these decisions of the creator:
every kind of doc counts (text briefs, design pages, tokens); a fetch
happens only on the creator's word; fetched files live in
`_io/design/claude-design/`, outside git; if a direct read ever fails,
the creator pastes a share link instead.

## 1. What the session is, and the one rule

- Project id: `91867795-1bc6-4473-9396-ba1553f7583c`
  (`https://claude.ai/design/p/91867795-1bc6-4473-9396-ba1553f7583c`).
  It is a plain design project (type `PROJECT_TYPE_PROJECT`), not a
  design system.
- **Read-only.** Nothing is ever written into that session from here:
  no `finalize_plan`, no `write_files`, no `delete_files`. The design
  session tells us what to do; we do not tell it. A request to push
  something there is a question for the creator, not an action.

## 2. The tool, and the login it needs

The `DesignSync` tool reads the session (load it with
`ToolSearch "select:DesignSync"` if it is not in the tool list). Its
read methods need no permission prompt once the login is done:

- `get_project` with `projectId`: the name and type. Use it once to
  confirm the id still points at the right session.
- `list_files` with `projectId`: every path in the session.
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
them that command; do not look for another way in.

## 3. On the creator's word: fetch

When the creator says a new doc is there, or names one:

1. `list_files` the session. Compare with `_io/design/claude-design/FETCHED.md`,
   which lists what was fetched before and when. New or changed paths
   are the candidates; the creator's words say which ones matter.
2. `get_file` each text doc (`.md`, `.json`, `.css`, small `.html`).
   Write it under `_io/design/claude-design/<the same path>` with the
   Write tool, byte for byte as it came. Add a dated entry to
   `FETCHED.md`: the paths fetched, their sizes, and which paths were
   left (binary, too large, not asked for).
3. A design page (`.dc.html`) over 256 KiB comes back truncated. Do not
   keep a truncated copy: note it in `FETCHED.md` and ask the creator
   for that page's share link (Claude Design's Share button); the
   Artifact tool or the browser reads the link. Icons and other binary
   files are fetched only when a task needs them (they come as base64;
   decode into the file with a small script).
4. Say in chat what arrived, in one line per file, with the size and
   whether it is new, changed or the same as before.

Never fetch on a hook's or a schedule's initiative: the creator chose
"on my word" so that a design in progress is not read half-done.

## 4. From a doc to work

- **A brief** (a text doc that says what to build): it becomes a desk
  card in Inbox with a first pass, as the desk-check skill describes:
  what exists, what the brief adds, Constitution notes by article,
  overlaps, size, the open questions for the plan stage. Quote the
  brief's key sentences into the card; link the file's path in `_io`.
  The creator triages it; the plan stage asks the questions; a coder
  gets the brief's path in its handout, as the shell redesign did
  (`_io/SHELL_REDESIGN.md`, Phase 16).
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
  SHELL_REDESIGN (Phase 16), "Pane Tabs Options" (Phase 12's card).

## 5. Rules

1. Read-only towards the session, always (section 1).
2. Fetch only on the creator's word; never from a hook or a schedule.
3. Fetched files go to `_io/design/claude-design/` and are never
   committed; a brief's substance goes into the card and the plan, which
   are committed.
4. `FETCHED.md` is the record: every fetch adds a dated entry, so the
   next session knows what is new.
5. A truncated or unreadable file is reported, never guessed at.
6. The creator's earlier manual uploads stay where they are
   (`_io/SHELL_REDESIGN.md`, `_io/design`, `_io/design2`); the skill's
   folder is the mirror from this day on.
