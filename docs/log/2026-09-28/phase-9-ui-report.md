## Phase 9 report

Phase 9, UI half: themes applied live, the theme picker, and the marketplace view (design view C). All of it is on `main` and pushed.

### Built

**Part A: themes**
- Each design token (`Cb…Brush` in App.xaml) is now one app-wide brush. The theme changes its colour in place, so the window repaints at once without a restart.
- A `ThemeMapper` (CabinetOS.Core, tested) turns a theme into the colour of every token. The `default` theme maps to exactly the design's values, and a test holds them equal.
- A `ThemeApplier` sets the brushes and WinUI's accent: `SystemAccentColor`, six computed shades, and WinUI's accent brushes.
- The Mica tint goes through a `MicaController` in a custom `TintedMicaBackdrop`, because `MicaBackdrop` has no tint.
- `kind: light` sets the root's `RequestedTheme` to Light. The caption buttons and dialogs follow.
- The terminal gets the theme's foreground, cursor, selection and 16 ANSI colours. The design's two-tone folder glyph uses `folderIcon` and `folderIconFront`, and the file glyph colours and capability-level colours follow the theme.
- `accent: null` follows the Windows accent, including a live change in Windows settings. `mica: null` is plain Mica.
- Theme picker: `preferences.selectColorTheme` (Ctrl+K Ctrl+T). It uses the palette's frame; each row has a swatch (accent dot on the Mica tint), name, author, a "light" label and a check. Enter or a click applies with `set_value ui.theme`, and Esc closes.

**Part B: marketplace**
- `marketplace.browse` (Ctrl+Shift+X, or the command bar's store button, now enabled and accent-coloured when open) replaces the main column.
- Nav (180 px): Discover, Plugins (plugins and tools), Themes, Installed, each with its count and the accent pill.
- Search field: `marketplace_search` after 150 ms of quiet; only the newest text's hits are shown. The caption says "{n} results · WebAssembly, sandboxed".
- Refresh button, and a card grid with auto-fill at a 230 px minimum and a 10 px gap. Each card has the 40 px tile (a theme's own accent colour, or a colour taken from the ID), name, verified check, author, two-line description, ★ rating, installs, and the kind chip. The top-right corner says "Installing N%", "Installed" or "Applied".
- Detail column (340 px, slides in over 180 ms): tile, name, "author · v{version}", the primary button, Source, Uninstall, three stat tiles, the long description, and "Requested capabilities" with level dots.
- Install flows:
  - A plugin opens the 5c review dialog with the index's capabilities before anything is downloaded. "Allow and install" sends `install_extension`, then `grant_capabilities`, so the plugin starts. No second automatic review opens.
  - A theme installs and applies.
  - A tool installs as it is, and the window re-reads its tools at `tools_changed`.
- `install_progress` shows as a 2 px bar on the button; `install_finished` and the reply both update the card.
- Uninstall asks first (a ContentDialog).
- Source opens only an `http`/`https` URL, with `Launcher.LaunchUriAsync`.
- Empty and error states:
  - no index set, naming the key `marketplace.index`;
  - the placeholder (a host ending in `.invalid`): "The marketplace index is not published yet.", with a hint to use `build-index.ps1`;
  - an unreadable index;
  - a core without a marketplace (`unknown_request`);
  - nothing installed, and no results.
- Esc closes the detail column, then the view. A command that works on the panes (a sidebar folder, `go.*`, `pane.*`, `file.*`, `edit.*`, `search.*`, `terminal.*`, `editor.*`, Ctrl+`, Ctrl+Shift+D) closes the view first. This uses a new `CommandRouter.Executing` event.
- The protocol-10 mirror: `list_tools`, `marketplace_refresh`, `marketplace_search`, `install_extension`, `uninstall_extension`, the replies `tools` and `marketplace_index`, the events `install_progress`, `install_finished` and `tools_changed`, and four error codes.

**Part C: tests, snapshots, docs**
- Tests: 327 → 395. Seven run against the real core.
- Snapshots in docs/log/2026-09-28: `phase-9-theme-nord.png` (the window under Nord), `phase-9-picker.png` (under Catppuccin Mocha), `phase-9-marketplace.png` (the local index, Hello selected, the detail column open).
- docs/ui.md: new sections "Themes" and "The marketplace", plus the intro, the solution table, the test list, the environment variables, the log targets, and "Not in this version".
- README documents table: added rows for docs/themes.md and docs/marketplace.md, and updated the docs/ui.md row.

Files (all under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\.claude\worktrees\agent-a31a3838893f73dc5\):
- New in ui\CabinetOS.Core: Themes\ThemeLook.cs, Themes\ThemeMapper.cs, Themes\ThemePickerModel.cs, Market\MarketplaceModel.cs, Market\MarketText.cs.
- New in ui\CabinetOS: MainWindow.Themes.cs, MainWindow.Market.cs, Services\ThemeApplier.cs, Services\ThemeBrushes.cs, Services\TintedMicaBackdrop.cs, Views\ThemePicker.xaml(.cs), Views\MarketplaceView.xaml(.cs), Views\CardGrid.cs, Views\FolderGlyph.xaml(.cs).
- New tests in ui\CabinetOS.Tests: ThemeTests.cs, ThemePickerTests.cs, MarketplaceTests.cs.
- Modified in ui\CabinetOS.Core: Protocol\Requests.cs, Replies.cs, Events.cs, ProtocolJson.cs and MessageCodec.cs (the protocol-10 types); Plugins\PluginModels.cs (`PermissionReview.ForInstall`, a `PluginTile` overload, level colours from the theme, the Trust tooltip text); Commands\CommandRouter.cs (the `Executing` event); Terminal\TerminalPageMessages.cs (the ANSI colours).
- Modified in ui\CabinetOS:
  - App.xaml (tokens as plain resources, new tokens) and App.xaml.cs (Dark from the start).
  - MainWindow.xaml, MainWindow.xaml.cs, MainWindow.Plugins.cs, MainWindow.Tools.cs, MainWindow.Terminal.cs (the wiring).
  - Services\DevSnapshots.cs (a stand-in backdrop colour).
  - Views\CommandPalette.xaml, FileRow.xaml(.cs), Sidebar.xaml.
  - Assets\xterm\terminal.js (the ANSI keys).
- Modified tests: ui\CabinetOS.Tests\ProtocolTests.cs, EndToEndTests.cs.
- Other: docs\ui.md, README.md (documents table only), .github\workflows\ci.yml (the `ui` filter now includes `sdk/themes`), and the three docs\log\2026-09-28\phase-9-*.png snapshots.

### Commits
- 630ba28 ui: themes from the core apply live, and a picker chooses one
- c67d059 docs: themes in the window, and two snapshots
- 4fe16cd ui: a light theme no longer ends the window; the picker keeps every tint
- 75fea66 ui, ci: the marketplace view (design view C) over the core's index
- 133c2e7 docs: the marketplace view, and its snapshot
- 4770f6f ui: the marketplace nav keeps the keyboard while install events come

### Checks
- `dotnet build CabinetOS.sln -warnaserror`: 0 warnings and 0 errors, in both Release and Debug.
- `dotnet test --solution CabinetOS.sln -c Release`: 395 passed, 0 skipped, on the final commit 4770f6f.
- The seven end-to-end tests ran against a core rebuilt from `main` with the marketplace client, in both debug and release builds:
  - Nord chosen through the picker → `theme_changed` → the mapper reports accent #FF88C0D0 and Mica #FF2E3440 at 0.88;
  - the marketplace model builds the local index with `sdk/marketplace/build-index.ps1`, reads and searches it, installs Hello with its review's grant, waits for `plugin_state_changed` to active, sees `install_progress` complete and `install_finished`, then uninstalls it.
- CI, `ui` job:
  - Part A: success, https://github.com/OliverD25/cabinetos/actions/runs/36459565647/job/109054479612
  - Part B (4fe16cd, 75fea66, 133c2e7): success, https://github.com/OliverD25/cabinetos/actions/runs/36463695243/job/109068414984
  - 4770f6f (the nav fix): CI did not start. GitHub's message: "The job was not started because recent account payments have failed or your spending limit needs to be increased." It passed locally (see above).
  - The core job of run 36463695243 was still running when I finished. It started because 75fea66 changed ci.yml.

### Live check
Release builds of the app and the core, on a locked screen, using the snapshot aid. Plugins, plugin data, the marketplace folder and themes were redirected to a temporary folder.
- Nord from the config applied at start: "theme applied nord accent #FF88C0D0 mica #FF2E3440 at 0.88".
- The picker listed 4 themes with swatches and applied Rosé Pine Moon live.
- In pwsh under Catppuccin, SGR 31, 32, 36, 94 and 95 drew the theme's colours.
- Marketplace, with the local index plus one scratch light theme "Paper":
  - the grid listed 11 items (Discover 11, Plugins 6, Themes 5, Installed 4), with the shipped themes shown as Installed and Default as Applied;
  - Hello's review listed cmd:register LOW and events:emit LOW;
  - "Allow and install" logged install_extension ok, then grant_capabilities ok, then "plugin installed and granted", and the status bar said "Hello is active.";
  - "Install and apply" of Paper turned the whole window light live: WinUI's text boxes and buttons, the marketplace and the picker. Status bar: "Paper is installed and applied.";
  - the default placeholder index showed "The marketplace index is not published yet.";
  - the first Esc closed the detail column and the second closed the view; `go.toPath` closed the view and navigated.
- A bug found by this check, and fixed: applying the first light theme **crashed the window**. WinUI's `SystemBackdrop.OnDefaultSystemBackdropConfigurationChanged` base version threw "The parameter is incorrect". TintedMicaBackdrop now overrides it (4fe16cd). Without the fix, any light theme, from the picker too, would end the window.
- Not seen live:
  - the progress bar (a local install takes about 18 ms);
  - the uninstall confirmation (it is in the popup layer, outside snapshots);
  - Source (it would open a browser);
  - menus and tooltips under a light theme;
  - the real tinted Mica (snapshots draw a stand-in colour).

### Decided
- Tokens are plain app resources, one brush per token, changed in place; there are no ThemeDictionaries — because a theme must repaint every view live, and the theme (not Windows) decides light or dark — undo: restore the ThemeDictionaries block of App.xaml from 329f4c7.
- The window is now dark even when Windows is in light mode, because `default.json` says `kind: dark`. Before Phase 9 it fell back to WinUI's light brushes — because the design has no light tokens and the theme is the source of truth — undo: make ThemeApplier treat `default` as following Windows (see Needs the core).
- WinUI's accent comes from the theme: SystemAccentColor and 6 shades computed from the accent (for a dark theme the accent is Light2, for a light theme Dark1), and WinUI's accent brushes changed in its theme dictionaries — because buttons, toggles and text selection must match the design's accent — undo: remove `ApplyAccent` in ThemeApplier.cs.
- The Mica tint goes through a custom SystemBackdrop that drives a MicaController — because MicaBackdrop has no tint — undo: `SystemBackdrop = new MicaBackdrop()` in MainWindow.SetUpWindow.
- TintedMicaBackdrop handles `OnDefaultSystemBackdropConfigurationChanged` itself and never calls the base — because the base throws when the window turns light, which ended the window — undo: remove the override (not advised).
- The terminal's xterm background is the scheme background at alpha 0, and the selection is the accent at 30 % — because the panel (`terminalBackground`) is translucent over Mica, and xterm draws reverse video with the opaque colour — undo: ThemeMapper.Terminal.
- The design's two-tone folder glyph (FolderGlyph) replaces the one-colour font glyph — because both theme colours must show — undo: FileRow and Sidebar back to the font glyph.
- Four tokens stay unthemed (CbOnAccentBrush, CbGraphFillBrush, CbDangerHoverFillBrush, CbScrimBrush) — because the palette has no key for them — undo: add palette keys in the core, then map them.
- The new CbRatingStarBrush follows `permissionMedium` — because the design's star is the same #F2C063 as its medium level — undo: make it a fixed brush and list it as unthemed in ThemeTests.
- Dialogs take `RootGrid.ActualTheme` — because the popup layer does not inherit the root's RequestedTheme — undo: remove `RequestedTheme` from the 4 ContentDialogs.
- The picker reads each theme with `get_theme` when it opens, and sets all tints once every reply has come — because `list_themes` has no tint, and the core now answers on its own threads, so updating one row per reply could lose a tint — undo: ThemePickerModel.LoadAsync.
- The snapshot aid lays the image over a stand-in for Mica (#202020, or #F3F3F3 for a light theme, with the theme's tint) — because Mica is not part of the window's content — undo: pass no backdrop to DevSnapshots.RenderAsync.
- The Plugins tab shows plugins and tools — because the design has only Plugins and Themes tabs, and the chip tells the two apart — undo: `InTab` in MarketplaceModel.
- "Installed" means present in `list_plugins`, `list_themes` or `list_tools`, whoever installed it — because no request says what the marketplace installed — undo: once the core adds that (see Needs the core).
- One card per ID, showing the newest version — because an index may list several versions — undo: `MarketplaceModel.Newest`.
- The plugin review happens before the download. Allow installs, then grants every capability the index lists, and PluginWatch's automatic review is suppressed with MarkReviewed. A plugin with no capabilities, and a tool, install with no review — because of trust rule 1, and because the grant is what lets the plugin run — undo: InstallReviewedAsync and InstallFromMarketAsync.
- Esc closes the detail column first, then the marketplace — because the design's Esc order lists the detail and the task says Esc closes the marketplace; two presses do both — undo: CloseMarketLevel.
- A command that works on the panes closes the marketplace first — because otherwise a sidebar click or Ctrl+` would change hidden panes with nothing visible — undo: the `Executing` handler in SetUpMarket.
- The index is read on the first open and on Refresh, and again when `marketplace.*` changes — because the core reaches the network only when asked (trust rule 6) — undo: OnMarketIndexChanged.
- Source opens only http and https addresses — because an index is untrusted input; the launch is a UI-side call, not file I/O (brief §1) — undo: MarketText.SourceUri.
- The window re-reads its tools at `tools_changed` and closes an editor whose tool was removed — because a tool installed from the marketplace should work without a restart — undo: the ToolsChangedEvent case in OnCoreEvent.
- Uninstall is offered for any installed item; the core refuses what it did not install, and the view shows that refusal — because the UI cannot tell which items the marketplace installed — undo: hide the button once the core says it.
- The third stat tile says "download", not the design's "wasm size" — because `size` is the download (a zip for plugins) — undo: MarketplaceView.xaml.
- The review's Trust tooltip now reads "Publisher identities come in a later version; until then every update is reviewed." — because the marketplace came without identities — undo: `PermissionReview.TrustNotYet`.
- The `ui` CI job also runs on `sdk/themes` changes — because the shell's theme tests read the shipped themes — undo: the `ui` regex in ci.yml.
- I added README rows for docs/themes.md and docs/marketplace.md — because the documents table missed the core's two new docs — undo: delete the two rows.

### Needs the core
- A way to tell which extensions the marketplace installed, and in which version. For example, an `installed_version` field on each `marketplace_index` item, or a `list_installed` request. Then Uninstall and a future "Update" are offered only where they work.
- The Mica tint in `list_themes`, so the picker does not send one `get_theme` per theme.
- A `kind: "system"` (or a light default theme), so the default theme can follow Windows' light mode as it did before Phase 9.

### Needs the user
- GitHub Actions billing. The run for 4770f6f did not start with the message "recent account payments have failed or your spending limit needs to be increased". The fix is in Billing & plans at https://github.com/settings/billing (a web setting, not a command).
- The real-key run on an unlocked screen. It is carried over from 5c and now also covers:
  - Ctrl+Shift+X and Ctrl+K Ctrl+T;
  - Tab through the marketplace;
  - the uninstall confirmation;
  - menus and tooltips under a light theme;
  - how the real tinted Mica looks.
- Publishing a real marketplace index is the creator's decision (docs/marketplace.md, "Not yet").

### Known gaps
- The terminal's area in snapshots is #202020, because WebView2's capture flattens transparency. The live window is not affected.
- A plugin list or review dialog that is open during a theme change keeps its old level colours until it is opened again.
- Tool Extension pages get only light or dark (`prefers-color-scheme`), not theme colours. The tool messages have no theme message.
- A newer version of an installed extension still shows "Installed".
- The card grid is not virtualized. That is fine for hundreds of items.
- In the picker, the Default row's dot uses the accent shade of the current theme's light or dark, not of the row's theme. This is cosmetic.

### Noticed out of scope
- Every window close logs two INFO lines, "cannot read the configuration" and "cannot read the command list: the core is not running". The reason: the `ui.lastPaths` write on close brings a `config_changed` after `shutdown`. A `_closing` check in the ConfigChanged case would stop it. This was already there before Phase 9.
- This agent environment sets `NO_COLOR=1`, which the terminal's pwsh inherits in snapshot runs, so program output shows without colours there. My scratch snapshot script removes it.

### Ready text for docs/PLAN.md (Phase 9 section, the UI half)
**The UI half of Phase 9, built 2026-09-28.** Themes apply live. Every design token is one app-wide brush whose colour the theme changes in place, so `get_theme` at start and each `theme_changed` repaint the window without a restart: text, layers, controls, the Acrylic of the palette, menus and flyout, the terminal's 16 ANSI colours, the file glyphs, the two-tone folder and the capability levels. The accent becomes WinUI's too (`SystemAccentColor` and six computed shades); `accent: null` follows the Windows accent, `mica` tints a `MicaController`, and `kind: light` turns WinUI's own controls light. "Preferences: Color Theme" (Ctrl+K Ctrl+T) lists the themes with swatches and applies one with `set_value ui.theme`. The marketplace (design view C, Ctrl+Shift+X or the command bar's button) takes the main column's place: Discover, Plugins, Themes and Installed with counts, a search sent after 150 ms of quiet, the card grid and a 340 px detail column. A plugin gets the permissions review with the index's capabilities before anything is downloaded; "Allow and install" installs it and grants them, and the plugin starts. A theme installs and applies; uninstall asks first; Source opens only web pages. The empty states name `marketplace.index` and say when the index is the unpublished placeholder. The live check found that a light theme ended the window (WinUI's backdrop callback), and it was fixed. 395 tests, seven against the real core (Nord through the picker; the local index read, Hello installed, granted, active and removed). Checked live on 2026-09-28 (release builds, locked screen) with the local index of `sdk/marketplace/build-index.ps1` plus one light theme. Snapshots in [log/2026-09-28/](log/2026-09-28/). Open for the core: which extensions the marketplace installed, the Mica tint in `list_themes`, and a light or system default theme. Guide: [ui.md](ui.md), "Themes" and "The marketplace".

### Ready text for the README status line (the Phase 5 sentence)
Phase 5, the WinUI 3 shell, is built (`ui/`, 395 tests, CI green): two panes over the core's shared-memory listings with the shell's type names and icons, breadcrumbs, a status bar, the command palette with chord keys and inline rebinding, copy, move, delete, rename, new folder and open with the transfer flyout and conflict decisions, file search, the plugin list and permissions review, the integrated terminal in a Tool Dock, Tool Extensions each in a WebView2 process of its own, with Markdown Preview as the first (opt-in, in `sdk/tools`), colour themes applied live with a theme picker, and the marketplace view, which installs plugins and themes from an index through the core; its scrolling check and the real-key run still wait for an unlocked screen.

(If the core side of Phase 9 is accepted too, "Phases 0 to 4 and 6 to 8" in the first sentence becomes "Phases 0 to 4 and 6 to 9". "CI green" holds for 133c2e7; the run for 4770f6f did not start because of billing.)

Scratch folder %TEMP%\cabinetos-ui-test was removed; no test processes left running.
