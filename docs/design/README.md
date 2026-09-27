# Handoff: FileForge — Windows 11 developer-first file manager

## Overview
FileForge is a native Windows 11 file manager that combines Total Commander's dual-pane operations with VS Code's workspaces, integrated terminal, command palette and plugin system. This package contains the interactive design prototype plus the design handout and describes every screen, interaction, state and token needed to implement it.

## About the design files
The files in this bundle are **design references built in HTML**. They show intended look and behavior; they are not production code to copy. The task is to **recreate these designs in the target codebase's environment** (WinUI 3 / Windows App SDK is the intended target; if the project has no UI stack yet, choose WinUI 3 with C# or Rust via windows-rs) using its native controls, Mica/Acrylic materials and Fluent styling. Behavior described here should be implemented with the platform's real APIs (file system, ConPTY terminal, WebAssembly runtime for plugins).

Fonts: the prototype substitutes Open Sans and Fira Code because Segoe UI Variable and Cascadia Code are not web-loadable. **Use Segoe UI Variable and Cascadia Code in the real app.** Icons in the prototype are simple placeholder glyphs; use Fluent System Icons (Segoe Fluent Icons) in the real app.

## Fidelity
**High-fidelity.** Layout, spacing, colors, materials, typography scale and interactions are final intent. Match them closely using WinUI's native equivalents (ListView with Fluent selection visuals, CommandBar, ContentDialog, TeachingTip/Flyout, etc.). Where WinUI already provides an idiomatic control, prefer it over hand-drawing.

## Files
- `FileForge.dc.html` — the clickable prototype (single file; template + logic). Open in a browser. Tweaks: `layout` (classic | right | rail), `terminalOpen`, `accent`.
- `FileForge Handout.dc.html` + `doc-page.js` — the design handout (principles, tokens, views, keybindings, trust model, open questions). Printable.
- `support.js` — runtime for the prototype files; not part of the design.

---

## Design tokens (dark mode)

### Materials and surfaces
| Token | Value | Use |
|---|---|---|
| Window backdrop | Mica (prototype: rgba(32,32,32,.86) over wallpaper) | App window |
| Layer | rgba(255,255,255,.0512) fill, 1 px rgba(255,255,255,.0698) stroke | Panes, marketplace, cards, sidebar rows |
| Layer, active pane | stroke rgba(255,255,255,.18) | Focused pane |
| Control fill | rgba(255,255,255,.0605); hover rgba(255,255,255,.06–.08) | Buttons, text fields |
| Text field bottom stroke | rgba(255,255,255,.2) rest, accent 2 px focused | Address bar, search, palette input |
| Acrylic | rgba(44,44,44,.72) + blur 40 px + saturate 1.6, 1 px rgba(255,255,255,.1) stroke | Command palette, context menu, transfer flyout |
| Dialog | #2B2B2B body, #202020 footer, 1 px rgba(255,255,255,.1) stroke | Permissions review |
| Terminal | rgba(0,0,0,.35) | Terminal panel |
| Shadows | palette 0 24px 60px rgba(0,0,0,.5); menu 0 12px 32px rgba(0,0,0,.5); dialog 0 32px 64px rgba(0,0,0,.55) | |

### Color
| Token | Value |
|---|---|
| Accent (default) | #60CDFF (Windows dark accent light). Themes override: Nord #88C0D0, Catppuccin #CBA6F7, Rosé Pine #EBBCBA |
| On-accent text | #111111 |
| Text primary / secondary / tertiary / disabled | #FFFFFF / rgba(255,255,255,.786) / .544 / .363 |
| Close caption hover | #C42B1C |
| Folder icon | #F2C063 body, #F8D98A front |
| File-type strokes | md #60CDFF · rs #F0906C · toml #C9B6FF · exe/dll #6CCB5F · bin/pdf #E0705E · zip #F2C063 |
| Permission levels | low #6CCB5F · medium #F2C063 · high #F27A6C |
| Tags | Design #F2C063 · Urgent #E0705E · Review #60CDFF · Archive #C9B6FF |
| Status dot (running) | #6CCB5F |

### Typography (Segoe UI Variable; prototype uses Open Sans)
- Body 13 px / 1.4, weight 400. Semibold 600 for pane titles, card titles, buttons.
- Secondary 12 px; caption 11 px; section labels 11 px 600 uppercase, letter-spacing .06em, tertiary color.
- Title bar app name 12 px secondary. Marketplace heading 15 px 600; detail name 16 px 600; dialog title 18 px 600.
- Markdown preview: h1 26 px 600, h2 17 px 600 with 1 px rgba(255,255,255,.1) rule, body 13 px / 1.6 at rgba(255,255,255,.86).
- Mono (Cascadia Code; prototype Fira Code) 12 px for terminal, hex, paths, keycaps (11 px).

### Shape and spacing
- Radii: controls 4 px, surfaces/cards/menus/palette 8 px, rail buttons 6 px, drive bars 3 px, tag chips 12 px.
- Row height 30 px (file rows), 32 px (sidebar rows, menu items), 34 px (workspace rows), 36 px (palette rows, pane headers, editor tab strip).
- Selection indicator: 3 px × 16 px accent pill on the left edge, vertically centered (top = (rowHeight − 16)/2), plus rgba(255,255,255,.08) row fill.
- Outer gutters 8 px around body; 8 px gap between sidebar, panes and terminal.
- Caption buttons 46 × 40 px. Command bar buttons 32 × 32 px, 4 px radius. Title bar 40 px, command bar 48 px, status bar 26 px.

---

## Screens / views

### 1. Window chrome
- **Title bar (40 px)**: app icon 18 px (accent→#8A5CF6 gradient, 5 px radius), "FileForge" 12 px secondary, then **workspace tabs** (32 px tall, 8 px top radii, active rgba(255,255,255,.08) white text, inactive .6 text, 8 px color dot per workspace, min-width 96 px, "+" button). Right: minimize / maximize / close caption buttons (46 px wide, hover rgba(255,255,255,.08), close hover #C42B1C).
- **Command bar (48 px)**: back, forward (disabled .363), up buttons; **breadcrumb address bar** (flex 1, 32 px, control fill, each crumb hoverable, chevron separators, clicking a crumb navigates); **search field** (width clamp(120px,22%,240px), placeholder "Search {folder}"); divider; **Dual/Single toggle** (icon + label, accent when dual); **terminal toggle** (accent when open); **marketplace button** (accent when in marketplace); **palette button** (⋮ glyph).
- **Status bar (26 px)**: "{n} items", "1 selected · {name}", spacer, transfer pill (see §6), git branch of active workspace, "UTF-8", layout label, "Ctrl+Shift+P" keycap (clickable → palette).

### 2. Main workspace (view A)
Body = flex row with 8 px gaps: [activity rail 44 px, rail layout only] [sidebar clamp(180px,20%,224px)] [main column flex 1] [terminal column, right layout only].

**Sidebar** sections (11 px uppercase labels): Workspaces (rows with color dot, name, branch in mono; active row has pill + .06 fill; label row shows "Ctrl+K W" hint), Pinned (Desktop, Downloads, Documents, Projects — folder icon, pill when it is the active path), Drives (name, free space, 3 px usage bar in accent: C: 76 %, D: 31 %), Tags (chips with dot, name, count).

**Panes**: 1 or 2 equal-width columns (flex 1, 8 px gap), each a Layer surface, 8 px radius, overflow hidden.
- Header 36 px: folder name (600), full path in mono 11 px tertiary right-aligned, a "list" icon button that opens the palette filtered to "editor".
- Column header row: Name / Modified / Type / Size, 11 px tertiary. Grid columns: `minmax(120px,1fr) minmax(0,.55fr) minmax(0,.45fr) 64px`.
- Rows 30 px, 4 px radius, 10 px horizontal padding, hover rgba(255,255,255,.06). Folder rows show folder icon and "Folder" type; file rows show a file glyph stroked in the type color. Optional 7 px tag dot after the name. Modified/Type/Size 12 px secondary, size tabular numerals right-aligned.
- Active pane: stroke rgba(255,255,255,.18) and white title; inactive title rgba(255,255,255,.7). In single-pane mode the one pane is always active.
- Empty folder: centered "This folder is empty." in tertiary.

**Integrated terminal** (rgba(0,0,0,.35), 8 px radius):
- Bottom placement: height clamp(120px,30%,240px). Header 34 px with tabs ("pwsh" active with green dot, "wsl: Ubuntu", "+"), right-aligned caption "cwd synced to active pane · {folder}", close button.
- Right placement: width clamp(220px,32%,380px), header shows "pwsh" and "synced to {folder}".
- Body: mono 12 px / 1.6, scrollable lines, prompt line `PS {activePath}>` in accent + inline input. Supports `ls`/`dir`, `cd <dir>`, `cd ..`, `pwd`, `clear`, `cargo`, `git`; unknown commands print the PowerShell "not recognized" message.

**Activity rail** (rail layout): 44 px column, 36 px buttons (Explorer, Marketplace, Git, Terminal) with 3 px accent pill when active. Explorer toggles the sidebar; Marketplace switches view; Terminal toggles the panel; Git runs "Show file history".

### 3. Command palette (view B)
- Trigger: Ctrl+Shift+P, toolbar ⋮ button, or the status-bar keycap. Full-window scrim (transparent, click closes). Panel: 640 px wide (max 100 % − 32 px), centered, top 64 px, Acrylic, 8 px radius, entrance animation 160 ms (opacity 0→1, translateY −6 px, scale .98→1).
- Input row: ">" prefix in mono, text field with 2 px accent bottom border, autofocus; right caption "{n} commands".
- List (max-height 400 px): rows 36 px, "{Category}:" in tertiary 12 px, command name, optional plugin badge (10 px, rgba(255,255,255,.08) pill), then **keycaps**: each chord segment as a keycap (mono 11 px, rgba(255,255,255,.08) fill, 1 px stroke .1 with .22 bottom), chords separated by the word "then". Then a **pencil button** 24 px: opacity .25 at rest, 1 on the highlighted row, hover fill rgba(255,255,255,.12).
- Highlight: arrow keys or mouse hover move a single highlight (pill + .08 fill). Enter or click runs the command and closes the palette.
- **Rebinding**: clicking the pencil puts that row into recording mode: keycaps are replaced by pulsing accent text "Press keys…" plus "Esc to cancel". Each non-modifier keydown appends a combo (Ctrl/Alt/Shift + key). A second combo within 1000 ms forms a chord; recording commits after 1000 ms of inactivity or immediately on the second combo. Esc cancels. New binding persists for the session and re-renders in the row.
- Footer hints: "↑↓ navigate · ↵ run · hover a row and click the pencil to rebind".
- Command list and default bindings: see handout §4 and `COMMANDS` in the prototype logic.

### 4. Marketplace (view C)
Replaces the main column (sidebar and status bar remain). Layer surface, 8 px radius.
- Left nav 180 px: "Marketplace" 15 px 600; rows Discover / Plugins / Themes / Installed with counts and accent pill.
- Toolbar: search field (max 420 px, placeholder "Search plugins and themes") and caption "{n} results · WebAssembly, sandboxed".
- Grid `repeat(auto-fill, minmax(230px,1fr))`, 10 px gap. **Card**: 14 px padding, 8 px radius, rgba(255,255,255,.04) fill, hover .07, accent stroke when selected. Contents: 40 px icon tile (color + 2-letter glyph, #111 text), name 600 + verified check (accent circle, 13 px), author 11 px, description 12 px (min-height 34 px), footer row: ★ (#F2C063) rating, "(reviews)", "· {installs} installs", spacer, kind label chip ("Theme" or "WASM plugin").
- **Detail column** (340 px, slides in 180 ms): 52 px icon, name 16 px, "author · v{version}", primary button (accent fill, #111 text: "Install" / "Install and apply" for themes; installed state: .06 fill, .7 text, "Installed"/"Applied"), secondary "Source" button, 3 stat tiles (rating, installs, wasm size), long description, "Requested capabilities" list (dot colored by level, name 600, reason 11 px).
- **Permissions review dialog** (440 px, centered on 40 % black scrim): icon + "Review permissions" 18 px + "{name} by {author}"; sentence "This plugin runs in a WebAssembly sandbox. It can only do what you allow here."; capability rows (dot, name, LEVEL label in level color uppercase 10 px, reason); checkbox "Trust {author} for future updates"; footer (#202020): "Cancel" secondary, "Allow and install" accent primary. Esc or scrim click cancels.
- Installing a theme sets it active immediately: accent and window tint change app-wide (Nord: tint rgba(46,52,64,.88); Catppuccin rgba(30,30,46,.9); Rosé Pine rgba(35,33,54,.9); Default restores system accent and Mica).

### 5. Editors and context menu (view D)
- **Opening files**: double-click (or context "Open") on `.md` opens Markdown Preview in the *other* pane in dual mode (same pane in single mode); `.exe/.dll/.bin` opens Hex Editor. Other files "open with default app" (prototype echoes to terminal).
- **Editor pane**: tab strip 36 px; one tab with 2 px accent top edge, file glyph, filename 12 px, close ×; right side: provider label with green dot ("Markdown Preview+" / "Hex Editor Pro") and an "Open in Terminal" button. Close returns the pane to its folder listing.
- Markdown preview: 28/40 px padding, max-width 680 px, selectable text, badges row, code block rgba(0,0,0,.35).
- Hex editor: mono 12 px grid `76px 1fr 150px` (Offset in accent, 16 bytes, ASCII), 22 px rows with hover, footer 26 px: "PE32+ executable · Offset 0x0000 · Little-endian · Hex Editor Pro · plugin".
- **Context menu** (right-click a row; also selects it and focuses the pane): Acrylic, 260 px, positioned at cursor, clamped to the window (x ≤ innerWidth − 270, y ≤ innerHeight − 380), 120 ms entrance. Top icon strip: Cut, Copy, Paste, Rename, Delete (36 × 32 px). Items 32 px: Open (Enter), Open in other pane (Ctrl+Enter), Copy to other pane (F5), Open in Terminal (Ctrl+`); separator; header "FROM PLUGINS"; Show file history [Git Lens] (Ctrl+K Ctrl+H), Compress to .7z [7-Zip], Open as hex [Hex Pro] (Ctrl+K H); separator; Properties (Alt+Enter). Plugin items carry a 14 px colored icon square and an accent-tinted plugin badge (rgba(96,205,255,.15) fill). Click outside or Esc closes.

### 6. File operations flyout (view E)
- Trigger: F5, palette "Copy/Move to Other Pane", context "Copy to other pane" or the Copy icon.
- Flyout: absolute bottom-right (right 16 px, bottom 36 px), 380 px, Acrylic, 14/16 px padding, 200 ms slide-up. Header: title ("Copying 12,480 items" / "Copy paused" / "Copy complete" / "Copy cancelled"), sub "{source} → {destination}" ellipsized, minimize button.
- Speed graph: 56 px tall, rgba(0,0,0,.3), SVG area+line in accent (area at 18 % opacity) over the last 40 samples (500 ms each), current "{n} MB/s" mono top-right.
- Progress bar 4 px accent, width transitions 400 ms linear. Footer: "{pct}%" 13 px 600, "{done} of 48.6 GB", ETA ("{m} min {s} s left" / "Paused"), spacer, Pause/Resume button, Cancel (hover rgba(196,43,28,.6)); after completion Cancel becomes "Close".
- Minimize shows a **status-bar pill**: 80 px × 4 px progress track + "Copying · {pct}%"; click restores the flyout. The rest of the UI stays fully interactive throughout.

---

## Interactions and keyboard (global)
| Key | Action |
|---|---|
| Ctrl+Shift+P | Toggle command palette |
| Esc | Close review dialog → palette → context menu → marketplace detail (first open one) |
| Ctrl+` | Toggle terminal |
| Ctrl+Shift+D | Toggle dual/single pane (collapsing closes an editor in pane 2) |
| Ctrl+Shift+X | Toggle marketplace |
| Ctrl+B | Toggle sidebar |
| Tab | Focus other pane (files view) |
| F5 | Start copy to other pane |
| Palette open: ↑/↓/Enter | Move highlight / run |

Shortcuts are ignored while focus is in a text input (terminal, search), except the palette's own keys.

## State model
- `view`: 'files' | 'market'
- `dual` (bool), `activePane` (0|1), `sidebarOpen`, `termOpen`
- `panes[2]`: { path, sel (selected name), mode: 'files'|'md'|'hex', file }
- `palette`: { open, q, idx, hover, recording (commandId|null), combos[] }; `shortcuts`: { commandId: [[keys]...] } overrides
- `ctx`: { x, y, item, pane } | null
- `term`: { lines[{t,color}], input }
- `transfer`: { pct, speeds[40], paused, done, min, from, to } | null; tick every 500 ms
- `mk`: { tab, selected (id), review (id), installed {id:true}, q }; `themeId`; `workspace` index
- Layout preference: 'classic' | 'right' | 'rail' (user setting)

## Animations
- Palette / menu / detail column: 120–180 ms ease, opacity + 6 px translateY + scale .98.
- Transfer flyout: 200 ms ease slide-up 10 px. Recording indicator: 1.2 s opacity pulse .5↔1.
- Progress bar width: 400 ms linear.

## Assets
No bitmap assets. Icons in the prototype are inline placeholder SVGs; replace with Segoe Fluent Icons (folder, document, chevrons, search, split view, terminal, store, more, edit/pencil, close, minimize, maximize, git branch). Workspace/plugin icons are colored tiles with 1–2 letter glyphs; plugins should supply their own icons.
