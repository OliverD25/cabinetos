## Report: the Total Commander research note

(The research agent of 2026-09-29 worked in a git worktree of its own and left no transcript file; this is its final report as it reached the planning session. The note itself is `docs/research/total-commander.md`.)

Context: the two desk cards of 2026-09-29, "List of most user beloved features of Total Commander" and "List of most used Total Commander Shortcuts". The research and plan note is written, committed and pushed to main as `87effb4`. No code was changed.

## What I wrote
- `docs/research/total-commander.md`: 1,403 lines, about 85 KB, about 14,000 words. It has a summary table, the ten findings, and three parts.
  - Part 1: all 20 features, each with Today, Where it belongs, Size and Proposal.
  - Part 2: the key table for the card, plus a second table of other common Total Commander (TC) keys.
  - Part 3 (a): a ready "Phase 11 — Total Commander parity (proposed 2026-09-29)" section for PLAN.md, with sub-phases 11a to 11f.
  - Part 3 (b): the "small keys and commands" list: 3 prerequisites, 5 aliases, 31 new commands and quick search, plus the exact core requests they need.
  - The note ends with its decisions, the questions for the creator, and outside sources.
- `docs/research/README.md`: one paragraph saying the folder holds proposals, not decisions.
- All 36 rows quoted from docs/keybindings.md were checked by script and match exactly.
- PLAN.md and the README were not edited; the planning session pasted Part 3 (a) into PLAN.md the same night.

## Ten most important findings
1. The key grammar has no numeric keypad keys. The window drops the keypad's `+ - * /`, so Num +, Num - and Num * cannot be bound today.
2. A plain arrow key clears the marks that Insert made (Windows list rules). TC's way of marking breaks at the first arrow. A setting `panes.selection: commander` fixes it.
3. A command can have only one default key: the seed in `registry.rs` takes one string. Every alias (F8, Shift+F8, Alt+F7) needs that changed first.
4. Only one key on the card is taken: Ctrl+B, the design's Toggle Sidebar. Off the card, Ctrl+F (Find), Ctrl+L (Go to Path) and Ctrl+Enter (Open in Other Pane) are also taken.
5. Archives, FTP/SFTP and TC's Packer and File System plugins all wait on one missing core hook: a virtual file system that a plugin provides. It is the largest single piece of work.
6. Core Plugins can touch files only under fixed `roots`, and a call ends after 5 s. Split, checksums and text search need grants for the files the user chose, and long work run as core jobs.
7. Tools cannot start without a file, bring their own commands, or change files. Multi-Rename and Sync need a way to hand a plan to the window, which runs it as a core job after the user confirms.
8. Article 10 puts the Lister, archives, FTP, Multi-Rename, sync, split and text search in the marketplace. They need the public marketplace index of ADR 0012, which waits for the creator.
9. Article 6 promises a graphical settings menu, and none exists yet.
10. Alt+Enter shows CabinetOS's own Properties, not Windows' property sheet. A second command can open Windows' sheet.

## Decisions made (what, because, undo)
The note records 20 decisions (D1 to D20). The main ones:
- Ctrl+B stays with the sidebar; branch view gets Ctrl+K Ctrl+B — because the design binds Ctrl+B, and the core refuses two commands on one key — undo: swap the keys in the seed.
- F3 never runs anything: without a viewer tool, it shows a note instead of opening the file's default application — because the default application of an `.exe` is the program itself — undo: add a fallback in the handler.
- Editing and Windows properties are new requests (`edit_path`, `show_properties`), not a new field of `open_path` — because an older core ignores an unknown field and would open the file, which runs a program — undo: fold them in once no older core remains.
- Marking defaults to Windows rules; TC marking is one setting away — because Article 4 wants a casual user at ease — undo: change the default.
- Comparing the two panes is a core request; the sync tool is an extension — because Article 5 names "comparisons" as part of the dual pane — undo: move it into a plugin later.
- Windows property columns come from the core, off by default — because only the core can ask Windows; plugins are sandboxed — undo: take all extra columns from plugins.
- Backspace stays a fixed key of the pane, not a keymap binding — because a command has one context for all its default keys, so Backspace would also work in the marketplace — undo: give the seed a context per key.
- A new folder, `docs/research/` — because the note is a proposal, not a governing document — undo: move the file or remove the folder.

## Questions only the creator can answer
1. Ctrl+B: keep it for the sidebar, or give it to branch view as TC does?
2. Folder tabs above each pane, and the design's workspace tabs kept in the title bar: agreed?
3. Should new users get Windows marking or TC marking by default?
4. Under Article 10, may the core show Windows property columns (off by default), or must every extra column come from a plugin?
5. Text search inside files: a plugin (as proposed), or core, since TC users use it daily?
6. Should the first start offer a one-click "Commander pack", or only the marketplace?
7. May the plugin host grant `net` (never granted so far), narrowed to the one server the user connects to?
8. When will the public marketplace repository (ADR 0012) be created? Sub-phase 11e cannot reach users before it exists.
9. Should archives (11f, the largest sub-phase) move earlier than small-to-large order?
10. Should TC's copy dialog before F5 be on by default for the creator? The design copies at once, so the proposal puts the dialog behind a setting, off by default.
