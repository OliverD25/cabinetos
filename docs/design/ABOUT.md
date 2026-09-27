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

Where the handout disagrees with the Constitution or the brief, the
consistency check in [../PLAN.md](../PLAN.md) section 4 records the conflict
and the phase where it is decided.
