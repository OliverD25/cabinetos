## Changelog
- v2 · 2026-10-01 · pointer to SHELL_REDESIGN.md and COMPACT_THEME.md (they take precedence)
- v1 · initial build order and acceptance criteria

> **Update 2026-10-01:** the shell (top bar, pane tabs, toolbar/path rows, Find, Quick Open) is respecified in `SHELL_REDESIGN.md`, and a compact density theme in `COMPACT_THEME.md`. Where this file or README.md conflicts with those, they win. Reference build: `CabinetOS Compact.dc.html`.

# CabinetOS — Coder Agent Handout

Read this first. Then open `README.md` for the full per-screen spec.

## What you are building
A native Windows 11 file manager: Total Commander dual-pane operations + VS Code workspaces, integrated terminal, command palette, WebAssembly plugins, theme/plugin marketplace. Fluent design, Mica window, Acrylic transients. Dark mode first.

## Source of truth (in priority order)
1. `CabinetOS.dc.html` — clickable prototype. Open in a browser. It is the reference for layout, states and behavior. Tweaks: `layout` (classic | right | rail), `terminalOpen`, `accent`.
2. `README.md` — tokens, per-screen specs, keyboard map, state model, animations.
3. `CabinetOS Handout.dc.html` — design rationale and open questions (read once).
4. `CabinetOS Icons.dc.html` + `icons/` — app icon (final = section 3; concept 2e). `icons/cabinetos.svg` is the master; 16/24/32/48/256 SVG + PNG provided.

The `.dc.html` files are design references built in HTML. Do not port them. Recreate the design in the target stack.

## Target stack (default unless the repo says otherwise)
- WinUI 3 / Windows App SDK, C# (.NET 8+). Rust via windows-rs is acceptable if the repo is Rust.
- Backdrop: `MicaBackdrop` on the window; `DesktopAcrylicBackdrop` / `AcrylicBrush` for palette, context menu, transfer flyout.
- Fonts: Segoe UI Variable (UI), Cascadia Code (terminal, hex, keycaps). Icons: Segoe Fluent Icons.
- Terminal: ConPTY (`CreatePseudoConsole`) hosting pwsh/wsl; cwd follows the active pane.
- Plugins: WASM runtime (wasmtime) with a capability manifest; UI surfaces the declared capabilities before install.

## Build order (each step shippable)
1. Window shell: title bar with workspace tabs, command bar (back/up, breadcrumb, search, Dual/Single, terminal, marketplace, palette buttons), status bar. Mica backdrop. App icon from `icons/`.
2. Single pane file list: grid columns `minmax(120px,1fr) minmax(0,.55fr) minmax(0,.45fr) 64px`, 30 px rows, Fluent selection pill (3×16 accent) + rgba(255,255,255,.08) fill, type-colored file glyphs, double-click navigation, breadcrumb navigation.
3. Dual pane: second pane, active-pane border (.18 stroke), Tab to switch focus, Ctrl+Shift+D toggle. Collapsing to single closes any editor in pane 2.
4. Sidebar: Workspaces (dot, name, branch), Pinned, Drives (usage bar), Tags. Ctrl+B toggles. Rail layout variant: 44 px activity rail with 36 px buttons.
5. Integrated terminal: bottom (height clamp(120px,30%,240px)) or right (width clamp(220px,32%,380px)) placement from settings; tabs pwsh / wsl / +; Ctrl+` toggle; cwd syncs to active pane.
6. Command system: every action is a command `{id, category, name, keybinding[], pluginId?}`. All buttons and menus dispatch commands.
7. Command palette: Ctrl+Shift+P, 640 px Acrylic overlay at top 64 px, fuzzy filter, keycaps with chords rendered as "A then B", pencil-on-hover rebinding (record combos; 2nd combo within 1 s = chord; commit after 1 s idle; Esc cancels). Persist overrides.
8. Context menu: Acrylic, icon strip (cut/copy/paste/rename/delete), core items, "FROM PLUGINS" group with plugin badge per item, Properties.
9. Editors in panes: pane hosts either a folder list or an editor view (Markdown preview, Hex). Tab strip with provider label and close. `.md` opens in the other pane; `.exe/.dll/.bin` opens hex.
10. Transfer engine + flyout: background copy/move with progress, throughput samples (500 ms), pause/cancel; flyout bottom-right 380 px Acrylic; minimize to status-bar pill. UI must never block.
11. Marketplace: Discover / Plugins / Themes / Installed, search, card grid (minmax 230 px), detail column (340 px) with capabilities, permissions review dialog (low/medium/high), install. Themes apply instantly (accent + Mica tint).
12. Theme engine: accent, tint, terminal palette from a theme JSON; marketplace themes override; "Windows Default" follows system accent.

## Non-negotiables
- No web view, no Electron. Must feel first-party.
- Every interactive row/button has hover (rgba(255,255,255,.06)), pressed, focus-visible states.
- Main thread never blocks on I/O; all file operations async with cancellation.
- Keyboard: full map in README §Interactions. F5/F6 copy/move to other pane, Tab switches pane, Esc closes topmost transient.
- Min hit target 32 px; text ≥ 11 px; contrast per Fluent dark theme text ramps (#FFF, .786, .544, .363).
- Radii: controls 4, surfaces 8. Selection pill 3×16 accent. Layer fill rgba(255,255,255,.0512) + .0698 stroke.

## Key numbers
Title bar 40 · command bar 48 · status bar 26 · sidebar clamp(180px,20%,224px) · rail 44 · file row 30 · sidebar row 32 · palette row 36 · pane header 36 · body gutters/gaps 8 · palette 640 wide · context menu 260 · flyout 380 · marketplace detail 340 · review dialog 440.

## Colors
Accent #60CDFF (on-accent text #111). Folder #F2C063/#F8D98A. File types: md #60CDFF · rs #F0906C · toml #C9B6FF · exe/dll #6CCB5F · bin/pdf #E0705E · zip #F2C063. Permission levels: low #6CCB5F · medium #F2C063 · high #F27A6C. Close-button hover #C42B1C. Terminal bg rgba(0,0,0,.35). Acrylic rgba(44,44,44,.72) blur 40.

## Data model (minimum)
Workspace { id, name, rootPath, color, branch? } · Pane { path, selection[], mode: files|editor, editor? { pluginId, filePath } } · Command { id, category, title, defaultKeys[][], pluginId? } · KeybindingOverride { commandId, keys[][] } · Transfer { id, kind, from, to, items, bytesTotal, bytesDone, speedSamples[], state } · Plugin { id, name, author, verified, kind: plugin|theme, version, rating, reviews, installs, wasmSize, capabilities[{name, level, reason}], installed } · Theme { id, accent, micaTint, terminalPalette }.

## Acceptance
Each numbered build step is done when the corresponding view in `CabinetOS.dc.html` can be reproduced side by side with no visible layout or state difference at 1280×800 and 1920×1080, in dark mode, with the interactions listed in README working from keyboard and mouse.
