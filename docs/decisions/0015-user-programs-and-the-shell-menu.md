# ADR 0015: The context menu comes from the config, starts only listed programs, and shows Windows' menu only on request

- Status: accepted
- Date: 2026-09-30
- Decided by: the creator, on the open questions of the card "High-Performance
  Context Menu" in chat on 2026-09-30; the planning session for the parts
  [PLAN.md](../PLAN.md), Phase 18, names as its own; and, where marked, the
  implementing session as a default while the creator was away.

## Context

Until Phase 18 the right-click menu was written into the window's code: the
same five icons and four rows for every file, and three rows for the empty
space. The creator's card asked for a menu the user shapes in
`cabinetos.json`, for the user's own programs in it, and for Windows' own
menu (Open with, Send to, what other programs add) within reach.

What decides the shape:

- Article 6: every setting is in the file. Article 7: every action is a
  named command with keys. Article 10: nothing heavy by default.
- The window may not read files or build command lines (brief §1). A
  program started from the menu is a process started with the user's
  rights, so what may start must be visible in one place.
- Windows' menu comes from COM objects (`IContextMenu`) and a menu handle
  that must stay on the thread that made them, and a shell extension may
  take its time to answer. Plugins cannot reach that interface, and must
  not: its items run other programs' code outside any sandbox.

## Decision

1. **The menu is `contextMenu` in the config** (the creator). Four targets:
   `background`, `file`, `folder`, `multiSelect`, each with `quickActions`
   (the icon row, at the top as in Windows 11's Explorer) and `items` (the
   rows, each a command ID, optionally with `extensions`, or a divider). The
   defaults are the Phase 5 menus, so an update changes nothing. After the
   file's rows come a divider, the plugins' group, Properties, and "Edit
   Menu…" (`menu.edit`), in that order.
2. **Every entry is a command** (the planning session). A user's program is
   the command `program.<name>` of an entry in `programs`, so the menu has
   one kind of entry, and each program is in the palette and can have keys.
3. **Only listed programs start** (the creator). `programs` gives each
   program's `command` and `args`; the tokens `{path}`, `{selection}` and
   `{cwd}` are filled in by the core from the window's last `window_state`.
   Nothing else starts from the menu. A refusal (`unknown_program`,
   `program_refused`, `command_line_too_long`, `no_window`) is logged at warn
   level with the program's name. The brief's `{selection_file}` and
   `--selection` are not built: `window_state` already gives a command the
   marked files (the planning session).
4. **Windows' menu is opt-in** (the creator). `contextMenu.shellMenu`, off by
   default, turns Shift+right-click and `menu.showShell` (Ctrl+Shift+F10)
   into Windows' own menu. The core builds it in `cabinetos-fs`, one thread
   per menu in its own apartment, and answers within 3 s or fails; the menu
   lives 30 s or until one item runs. Protocol 15 adds `shell_menu` and
   `shell_menu_invoke` (the planning session: 0.1.0 is built with 14).
5. **The control is WinUI's `CommandBarFlyout`** (the planning session,
   Article 3). The Phase 16 dropdowns keep their own surface.
6. **Defaults the implementing session chose**, each with how to undo it:
   - A command ID no command has is left out of the menu with one warning,
     not refused as an error of the file, because a plugin's commands come
     and go with the plugin. Undo: check the IDs in `cabinetos-config`'s
     `check` against the registry.
   - Properties and `menu.edit` named in the file are skipped: they always
     have their own places at the end, so a menu can never lose them. Undo:
     let `ContextMenuModel.Build` list them where the file puts them.
   - A plugin command the file lists is not repeated in the plugins' group.
   - `{selection}` must be a whole argument and becomes one argument per
     path; `{path}` and `{cwd}` may sit inside an argument; a brace that does
     not make one of the three words is plain text.
   - The window keeps one flyout per menu shape (up to six), because
     building one costs about 30 ms and a 60 ms frame (Article 1). A menu
     asked for while the one before it is still closing waits for its
     `Closed`: WinUI ignores a flyout that is shown again while it closes.
     Undo: build a new flyout at each opening in `ContextMenuFlyout.Show`.
   - Windows' menu leaves out owner-drawn, greyed and textless items and
     reads submenus one level deep; the window sends at most 1,000 paths.
   - The menu's thread starts OLE (not only COM) and flushes the clipboard
     after an item runs: without it, Windows' Cut and Copy ran without an
     error and left the clipboard as it was.
   - Plugins may never send `shell_menu` or `shell_menu_invoke`
     (`NEVER_ALLOWED`). Undo: remove them from the list in
     `cabinetos-plugins/src/policy.rs`.

## Consequences

- A user can shape every menu, and add programs, without code; a saved
  edit shows at the next right-click.
- `programs` is the one list to review for what the menu can start. A
  program runs with the user's rights, as a double-click would.
- Windows' menu costs nothing unless it is turned on, and even then the
  window never waits for it: the core answers on its blocking pool.
- A dialog that one of Windows' items opens (Properties, Open with) belongs
  to the core and has no owner window, so it may open behind CabinetOS.
- The window's Ctrl+V does not paste files that Windows' Copy put on the
  clipboard yet; that is a change to "The clipboard" in [ui.md](../ui.md).
- Step 2 of the card, editing the menu inside the menu, builds on the same
  `contextMenu` and is not part of this decision.
