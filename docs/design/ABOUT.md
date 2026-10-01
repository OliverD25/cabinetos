# About this folder

This is the design handout for CabinetOS, received from the creator on
2026-09-28 as `Windows 11 File Manager Design.zip` and unpacked here unchanged.

- **"FileForge" is the design codename only.** The product, the executables, the
  config file and the title bar all say **CabinetOS**.
- The files are **design references built in HTML**, not code to copy. Recreate
  them with native WinUI 3 controls, Mica and Acrylic materials, Segoe UI
  Variable and Segoe Fluent Icons. [README.md](README.md) in this folder lists
  every token, screen, state and keybinding.
- `FileForge.dc.html` is the clickable prototype. Open it in a browser. Its
  inline script holds the seed command list (`COMMANDS`) and default bindings
  that Phase 3 of [../PLAN.md](../PLAN.md) uses for the command registry.
- `FileForge Handout.dc.html` is the printable handout: principles, tokens,
  views, keybindings, plugin trust model, open questions.
- `doc-page.js` and `support.js` are the prototype's runtime, not part of the
  design.
- `compact/` is the Commander Compact handout (received 2026-09-29, unchanged):
  `COMPACT_THEME.md`, a density preset of the same design, and its reference
  page `CabinetOS Compact.dc.html`; the theme that ships from it is
  [../themes.md](../themes.md), "Metrics and chrome".

- [SHELL_REDESIGN.md](SHELL_REDESIGN.md) is the shell redesign, **v2 of
  2026-10-01**, fetched from the creator's Claude Design session the same
  evening. It replaces this handout's title bar, command bar and pane
  header with one quiet top row, a workspace header in the sidebar, and
  per pane a tab strip, a toolbar row and a path row. Its v1 of
  2026-09-30 (a workspace pill and a command center in the top row, the
  nav buttons in the breadcrumb row) was built as Phase 16 and is kept
  in `_io` next to the repository. `compact/CabinetOS Compact.dc.html`
  is v2's reference build (replaced the same evening; the 2026-09-29
  page is in `_io/design/Compact_Theme/`). Where the window differs from
  the redesign: [../ui.md](../ui.md), "The shell".

Where the handout disagrees with the Constitution or the brief, the
consistency check in [../PLAN.md](../PLAN.md) section 4 records the conflict
and the phase where it is decided.
