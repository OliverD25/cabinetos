## Phase 5c report

Context: CabinetOS Phase 5c. This phase built the UI halves of Phases 6, 7 and 8 in `ui/`:
- the protocol 9 registry and keymap, with the shell's type names and icons;
- the terminal pane;
- file search;
- the plugin list and the permissions review dialog;
- Tool Extensions, with Markdown Preview as the first one.

Everything is pushed to `origin/main` (HEAD 41dade9). The worktree is clean.

### Built

**Part 0: protocol 9**
- **Registry and keymap.** The shell's commands come from the core's registry and keymap (`RegisterUiHandler`). The local overrides are gone.
  - `RegisterLocal` is left only for the window's own controls: transfer, conflict, sidebar, plus the new terminal, search, plugin and editor commands.
  - Pane keys now come from the core's keymap, except the list keys: arrows, Home, End, PgUp, PgDn, Backspace, Shift+F10 and the Menu key.
- **Type column and icons, from the shell.**
  - The pane asks `describe_entries` for pages of 128 rows: one request per layout pass, and each page once per listing generation.
  - A row whose page has not come yet shows what the core said about the same extension. This memory is shared by both panes, and never used for .exe, .ico or .lnk.
  - `get_icon` results are cached per key and size. The size follows the screen's scale: 16, 24, 32 or 48 px. On a scale change, rows show the old size until the new one arrives.
  - A core that answers `unknown_request` leaves the built-in text and glyphs in place.

**Part A: the terminal** (`MainWindow.Terminal.cs`, `TerminalController`, `ToolDock`, `SplitterBar`, `WebViewHost`, `Assets/xterm/*`)
- **Rendering.** xterm.js 6.0.0 with the fit addon 0.11.0, copied unchanged with their MIT licenses and SHA-256 hashes. Served from `https://terminal.cabinetos.example/`; no CDN. One WebView2 holds every session.
- **Placement.**
  - Bottom for `ui.layout` classic or rail: clamp 120 px / 30 % / 240 px.
  - Right for `right`: clamp 220 px / 32 % / 380 px.
  - The 8 px gap before the dock is a draggable splitter.
- **Header, 34 px.**
  - One tab per session: a green dot while it runs, the profile name, and ×.
  - "+" starts the default profile. An arrow lists the profiles from `get_config`.
  - Caption: "cwd synced to active pane · {folder}" (shorter on the right), or why it is not synced, or the exit code.
  - A close button hides the dock.
- **Byte pump.**
  - The window opens `terminal_opened.pipe` and reads it on a background task.
  - Output goes to the page as base64 through `PostWebMessageAsString`, at most every 16 ms per session and at most 192 KiB per message.
  - Keys come back through `WebMessageReceived`. The fit addon's cols and rows go to the core as `terminal_resize`.
- **Folder sync.** `terminal_sync_cwd` goes out after 300 ms without another folder change. The rule is in "Decided".
- **Exit.** On `terminal_exited`, the shell's last output stays, then a dim "[exited with code N]" line, a grey dot and the code in the caption. The tab closes after 3 s. When the last tab closes, the dock hides.
- **Opening.** Ctrl+` (`view.toggleTerminal`), the command-bar button (accent colour while the dock is open), and "Open in Terminal" in the context menu, which opens a new session in the row's folder.
- **Keys.** Ctrl+` in the terminal gives the keyboard back to the pane. The palette opened from the terminal gives the keyboard back to the terminal.
- **Crash isolation.**
  - The terminal has its own browser process. When it fails, the dock shows "The terminal stopped" with Reload.
  - Reload re-creates each session in the page and makes the pseudo-console repaint: a resize by one column and back.

**Part B: search** (`SearchModel`, `MainWindow.Search.cs`, `SearchRows`, pane search mode)
- **Request.** Ctrl+F (`search.focus`) focuses the field. Typing sends `search {query, limit: 100, root: active pane folder}` after 150 ms of quiet.
- **Results.** The hits replace the folder in the active pane, in the same rows.
  - The Folder column shows the hit's folder from the searched folder's name down (`docs\log\2026-09-28`).
  - The title reads "Search: {q} · {n} hits · {index|walk} · {took}".
  - A note line says whether the answer is complete or not, adds "only the first 100 hits" when the limit was hit, and for a walk says the index is not running, pointing to docs/indexer.md "Running it".
- **Whole volume.** A checkbox in the note row drops the root and searches again at once.
- **Keys.** Enter in the field searches at once and moves the keyboard to the hits. Enter on a hit opens its folder with the hit selected. Esc leaves the search (last in the Esc order).
- **Other.** Stale answers are dropped. The UI ranks and filters nothing.

**Part C: plugins** (`PluginModels`, `MainWindow.Plugins.cs`, `PluginsPanel`, `ReviewDialog`)
- **The list.** "Plugins: Show Plugins" (`plugins.list`) shows each plugin's name, version, ID and state in words. Each capability has a level dot and LEVEL in the design's colours (low #6CCB5F, medium #F2C063, high #F27A6C), and says whether it is allowed. The list offers "Review permissions" and, for crashed or failed plugins, "Reload" (`reload_plugin`).
- **The review dialog (design view C).**
  - 440 px on a 40 % scrim.
  - Tile, "Review permissions", "{name} by {author}", and the sandbox sentence.
  - One row per capability: dot, name, LEVEL, reason, and the folders for fs:*.
  - "Trust {author} for future updates" is disabled, with the tooltip "Publisher identities come with the marketplace."
  - Cancel and "Allow and install" (`grant_capabilities`). Esc or the scrim cancels.
- **Status bar.** "{name} is active." when a plugin becomes active, and "{name} crashed: …" when one crashes.

**Part D: Tool Extensions** (`ToolManifest`, `ToolCatalog`, `ToolMessages`, `ToolKeyScript`, `ToolHost`, `EditorPane`, `MainWindow.Tools.cs`)
- **Format.** `tool.json` is read strictly, like `plugin.json`. Schema: `sdk/tools/tool.schema.json`.
- **Where tools live.** `--tools-dir` or `CABINETOS_TOOLS_DIR` is read first, then `%LOCALAPPDATA%\CabinetOS\tools\<id>\`.
- **Isolation and security.**
  - Each tool gets one WebView2 and its own browser process, with user-data folder `WebView2\tool-<id>`.
  - Page host: `<id>.tool.cabinetos.example`.
  - The file's folder is mapped read-only to a new host `f<n>.<id>.cabinetos.example` for every open. The page may request from it but cannot navigate there.
  - Every other navigation, frame, window, download, permission or request (http and https included) is refused and logged. There are no developer tools in Release.
- **Messages.** The page posts `{"type":"ready"}`. The window then sends `{"type":"context","selection":[…],"activeFolder":…}` (again on changes, at most every 100 ms) and `{"type":"open","path":…,"url":…}`. From the page, `{"type":"command","id":…,"args":{…}}` goes through the `CommandRouter` if the allow-list permits it.
- **Editor tab (design view D).** 36 px, 2 px accent edge, file glyph and name, close ×, the tool's name with a green dot, and "Open in Terminal".
  - With two panes shown, the tool opens in the other pane; with one, in the same pane.
  - A crashed tool shows "The tool stopped" with Reload.
- **Markdown Preview** (`sdk/tools/markdown-preview`, opt-in).
  - marked 18.0.14 (MIT), bundled.
  - The design's typography.
  - A CSP stops HTML inside Markdown from running.
  - Relative images load; links to .md files open in the preview; heading anchors scroll; web links do not open.
- **Opening Markdown.** Enter on a .md file opens it in the tool when one is installed; otherwise it opens in the default app as before. `editor.openMarkdownPreview` is in the palette with Ctrl+K V.

**Part E**
- **Snapshots** in `docs/log/2026-09-28/`: `phase-5c-terminal.png` (`git log` in pwsh), `phase-5c-search.png`, `phase-5c-review-dialog.png` (the hello fixture), `phase-5c-markdown.png` (the repo README in the other pane).
- **Snapshot aid.** It now draws WebView2 pages: `CapturePreviewAsync` output is laid over the window's own render, and this works on the locked screen. New steps: `terminal:`, `search:`, `open:`, `crash:terminal`, `crash:tool:<id>`, `until:terminal|search|tool`.
- **Docs.** See the file list below.

### Commits (all pushed to origin/main)
- 12a6d51 ui: the shell's commands come from the core's registry and keymap
- 8b27a67 ui core: the terminal's byte pump, the folder-sync rule, and the terminal, search and plugin messages
- cd51ea8 ui: type names and icons from the shell, through the core
- 2769f2f ui: the terminal pane, xterm.js in WebView2 over the core's byte pipes
- 9cc5d53 ui: file search from the command bar, with the results in the pane
- 54d3fa4 ui: the plugin list and the permissions review dialog
- b5c3460 ui, sdk: Tool Extensions in WebView2, and Markdown Preview as the first one
- f65c29c ui, ci: search hits show their folder from the searched one down; four snapshots
- 8ba065c ui: the keyboard goes to a tool page that covers the active pane
- 41dade9 docs: where the keyboard goes when a tool covers the only pane

The core agent's aee0a5d (themes, protocol 10) arrived in between. It edited `MessageCodec.cs` and `EndToEndTests.cs`. I rebuilt the core, and all UI tests pass against protocol 10.

### Files (new or changed)

**Core project** (`ui/CabinetOS.Core/`)
- `Protocol/Requests.cs`, `Replies.cs`, `Events.cs`, `ProtocolJson.cs`, `MessageCodec.cs`: terminal, search, index, plugin, `describe_entries` and `get_icon` messages; the typed `plugin_state_changed`; new error codes, including `Io`.
- `Listing/EntryDetails.cs` (new): the per-generation details cache, the extension memory, and the icon sizes.
- `Terminal/OutputCoalescer.cs`, `TerminalPipe.cs`, `CwdSync.cs`, `TerminalPageMessages.cs`, `TerminalProfiles.cs`, `TerminalKeys.cs` (new): the byte pump, the pipe client, the sync rule, the debouncer, page messages, profiles, and the keys a page passes back.
- `Presentation/DockLayout.cs` (new): placement and the size clamps. `Presentation/DisplayFormat.cs`: `FolderUnder`.
- `Search/SearchModel.cs` (new): debounce, request, stale-drop, header and note texts.
- `Plugins/PluginModels.cs` (new): list rows, `PermissionReview`, `PluginWatch`, tiles, level colours.
- `Tools/ToolManifest.cs` (manifest and catalog), `ToolMessages.cs` (protocol, allow-list, file URLs), `ToolKeyScript.cs` (all new).
- `Commands/CommandRouter.cs`: the overrides were removed; `RegisterWindowCommand` and `WindowCommands` are new.
- `Keys/Keymap.cs`: `With` (the window's own binding, only when it is free). `Keys/KeyNames.cs`: `VirtualKeyNames`.

**Tests** (`ui/CabinetOS.Tests/`)
- New: `EntryDetailsTests.cs`, `TerminalTests.cs`, `SearchTests.cs`, `PluginTests.cs`, `ToolTests.cs`.
- Changed:
  - `ProtocolTests.cs`, `CommandRouterTests.cs`, `DisplayFormatTests.cs`.
  - `EndToEndTests.cs`: the registry keys, types and icons from the real core, and a cmd echo through the byte pump.
  - `Support/Repo.cs`, `Support/Schemas.cs`: the tools folder and its schema.

**App** (`ui/CabinetOS/`)
- `CabinetOS.csproj`: `Assets\xterm\**` as Content. `App.xaml`: terminal, dialog and scrim tokens, and `CbDockTabButtonStyle`. `App.xaml.cs`: `--tools-dir`.
- `MainWindow.xaml`: the main column with two splitters and the dock, the editor panes, the overlays, the enabled search box and the terminal button.
- `MainWindow.xaml.cs`: wiring, `ApplyKeymap`, Esc order, file commands guarded in search mode, snapshot steps, `FocusActivePane`.
- `MainWindow.Terminal.cs`, `MainWindow.Search.cs`, `MainWindow.Plugins.cs`, `MainWindow.Tools.cs` (new).
- `Services/`:
  - `IconCache.cs`, `WebViewHost.cs`, `TerminalController.cs`, `ToolHost.cs` (new);
  - `DevSnapshots.cs`: WebView2 compositing.
- `ViewModels/`:
  - `SearchRows.cs` (new);
  - `ListingRows.cs`: `IRowDetails`;
  - `PaneModel.cs`: details, and the search state and selection;
  - `PaletteModel.cs`: window commands after the core's hits; they are not rebindable.
- `Views/`:
  - `ToolDock.xaml(.cs)`, `SplitterBar.cs`, `PluginsPanel.xaml(.cs)`, `ReviewDialog.xaml(.cs)`, `EditorPane.xaml(.cs)` (new);
  - `FileRow.xaml(.cs)`: icon image, details, hits, glyph constants;
  - `FilePane.xaml(.cs)`: detail pages, search mode, current selection;
  - `CommandPalette.xaml.cs`: the F2 case was removed.
- `Assets/xterm/` (new): `terminal.html`, `terminal.css`, `terminal.js`, `README.md`, `xterm/{xterm.js, xterm.css, LICENSE}`, `addon-fit/{addon-fit.js, LICENSE}`.

**SDK and docs**
- `sdk/tools/README.md`, `tool.schema.json`, `markdown-preview/{tool.json, index.html, preview.css, preview.js, README.md, marked/marked.umd.js, marked/LICENSE}` (new).
- `docs/ui.md`: sections Search, Plugins, The terminal, Tool Extensions; the pane key table from the keymap; type names and icons; env and steps; Not in this version.
- `docs/tool-extensions.md` (new).
- `docs/ARCHITECTURE.md`: a change-log row.
- `README.md`: the ui.md row and a new tool-extensions.md row.
- `sdk/README.md`: the tools entry.
- `docs/log/2026-09-28/phase-5c-*.png`: 4 new snapshots.
- `.github/workflows/ci.yml`: `sdk/tools/**` added to the triggers and the ui filter.

### Checks
- `dotnet build CabinetOS.sln -warnaserror` (Debug and Release): clean.
- `dotnet test`: **327 passed, 0 skipped** (207 at the end of 5b).
  - This includes 5 end-to-end tests against the real core (protocol 10): listing, 200-file copy with Skip, folder make and rename, type names and icons with PNG sizes, and a cmd echo through the byte pump.
  - The shipped `tool.json` is validated against `tool.schema.json`.
- CI `ui` job green:
  - b5c3460: https://github.com/OliverD25/cabinetos/actions/runs/36452744738/job/109031352597
  - 8ba065c (final code): https://github.com/OliverD25/cabinetos/actions/runs/36454046748/job/109035794773
  - f65c29c: `ui` job green too. Its core job was still running at hand-off; that run only changed `ci.yml` path filters and no core code.

### Live check (release builds, locked screen, the window's own snapshot steps)
- **Type names and icons.** Real shell names ("Compressed (zipped) Folder", "Windows PowerShell Script") and icons, including `dotnet.exe`'s own icon.
- **Terminal.**
  - The first Ctrl+` gives a running pwsh after 0.48–0.54 s, of which 0.35–0.41 s is creating WebView2. Later shells take 35–44 ms.
  - Folder sync typed `Set-Location -LiteralPath 'C:\Windows'`. A half-typed line blocked the sync, and the caption said so.
  - `exit` showed the exit-code line; the tab closed after 3 s and the dock hid.
  - The right placement works.
- **Crash isolation, terminal.** Checked by killing `CoreWebView2.BrowserProcessId` (step `crash:terminal`). The dock said "The terminal stopped"; the other pane went on navigating; Reload repainted the same pwsh, which took new input.
- **Search.** A walk of `docs/` answered in 1.2–1.4 ms (`took_us`), with the reply 4.6 ms after the request. "Nothing matches." shows for no hits. Esc returns to the folder, and Enter on a hit goes there.
- **Plugins, with the hello fixture** (`CABINETOS_PLUGINS_DIR`).
  - At start the status bar named the waiting plugin.
  - The review granted `cmd:register` and `events:emit` into `plugins.hello.granted`.
  - "Hello is active." appeared, and the list showed "Active: 1 command".
  - Copying the plugin in while running, then `reload_plugin`, opened the review by itself.
- **Markdown Preview.**
  - The README opened in the other pane; in single mode it opened in the same pane.
  - Crash isolation, checked by killing the tool's `BrowserProcessId` (step `crash:tool:markdown-preview`): "The tool stopped"; the other pane went on; Reload showed the README again.
  - Typing a search over a covering preview closed it and showed the hits.
- **Not run (the screen stayed locked).** Real key presses inside the WebView2 pages. See "Needs the user".

### Decided
- **Protocol 9 commands.** Registry commands use `RegisterUiHandler`; `RegisterLocal` is only for the window's own controls — because protocol 9 lists the shell's commands with keys — undo: `git revert 12a6d51`.
- **Detail requests.** Pages of 128 entries, one request per layout pass, each page once per listing generation — because the core takes up to 512 and 128 covers a few screens without over-asking — undo: `EntryDetailsCache.PageSize`.
- **Guessed type names.** A row without its details shows what the core said about the same extension (not for .exe, .ico, .lnk) — because a refresh would otherwise flash "TXT File" — undo: drop the `_known.Guess` fallback in `PaneModel.Detail`.
- **Icon size.** The smallest offered size that covers 16 DIP at the screen's scale; the old size is shown while a new one loads — because icons stay sharp at 150 % and 200 % — undo: `IconSizes.For`, and `_anySize` in `IconCache`.
- **Terminal renderer.** xterm.js 6.0.0 with the DOM renderer; no WebGL addon — because the task said xterm.js first and listed only the fit addon — undo: add `@xterm/addon-webgl`.
- **One WebView2 for all sessions.** User-data folder `%LOCALAPPDATA%\CabinetOS\WebView2\terminal`; virtual host under the reserved `.example` domain — because it gives one browser process for the terminal and a name that never resolves — undo: `TerminalController.PageHost` and `WebViewHost.Domain`.
- **Line height 1.25 instead of the design's 1.6** — because the design's value is for its static lines and would cost a third of the rows — undo: `lineHeight` in `terminal.js`.
- **Folder-sync rule.**
  - Only the shown tab follows, and only while the dock is visible.
  - The sync is skipped when a line is half typed (keys since the last Enter or Ctrl+C), when the alternate screen is on (vim, less), when the shell is already in that folder, or when it ended.
  - A skipped sync is not retried on Enter.
  - Because the core types a `cd` line, which would join a half-typed line or go to a full-screen program; and the line finished may be the user's own `cd`.
  - Undo: `CwdSyncRule.Decide` and `TerminalController.QueueSync`.
- **Reading of "terminal_exited closes the tab and shows the exit code for 3 s".** The last output, the exit line and the grey dot stay for 3 s, then the tab closes; the last tab closing hides the dock — because the user should see how the shell ended — undo: the timer in `TerminalController.OnExitedAsync`.
- **Keys in the terminal.**
  - Only single-combination bindings with `when: terminalFocus`, and the bindings of `palette.show` and `view.toggleTerminal`, go to the window.
  - Esc, Tab and chords stay in the shell.
  - Ctrl+C with a selection copies; Ctrl+V pastes through the browser's paste event.
  - A key that also reaches XAML while a WebView2 has focus is ignored there.
  - Because shells need Esc and Ctrl+K, while the ways out must always work and no key may run twice.
  - Undo: `TerminalKeys.PassKeys`, `terminal.js` `onKey`, and the WebView2 guard in `OnPreviewKeyDown`.
- **Ctrl+` semantics.** Hidden: show it with the keyboard. Terminal focused: keyboard back to the pane. Pane focused: hide it. `{"visible": false}` hides it (the close button) — because this matches the task and is predictable — undo: `ToggleTerminalAsync`.
- **Dock size.**
  - Default clamps as designed. Dragging keeps at least 120/220 px and leaves the panes 160/320 px.
  - The dragged size is not saved — because there is no setting for it, and the UI may not add config keys (the core validates `ui.*`) — undo: `DockLayout.Clamp`.
- **Placement.** `right` puts the dock right; `classic` and `rail` put it at the bottom — because that is what the prototype does — undo: `DockLayout.PlacementFor`.
- **"Open in Terminal" shows no key.** It opens a new shell in the row's folder, or a file's own folder — because Ctrl+` does something else, and showing it would mislead — undo: the `RowMenu` entry.
- **After a crash of the terminal page, output that arrived while it was down is dropped.** The repaint shows the current screen — because there is then no unbounded buffer — undo: buffer in `TerminalController.Flush` when the page is not ready.
- **A core restart closes the tabs.** No reattach through `terminal_list` — because the window starts its own core, which closes its shells when it stops — undo: call `terminal_list` in `LoadAsync`.
- **Search results replace the folder in the active pane,** with their own selection; the listing waits underneath — because the task asked for a pane mode — undo: `PaneModel.Search` and `FilePane.ApplySearch`.
- **Enter in the search field** searches at once and moves the keyboard to the hits; Down does the same — because "Enter on a hit" needs the keyboard on the hits — undo: `OnSearchKeyDown`.
- **"Whole volume" checkbox in the pane's note row,** not in the command bar — because of Article 4 (clean by default) — undo: move it to `MainWindow.xaml`.
- **File commands in search mode only show a notice** — because the listing's selection is out of sight, and F5 or Delete would act on files the user cannot see — undo: remove the `ListingOnly(...)` wraps.
- **Hits have no context menu** — undo: in `FilePane.OnRightTapped`, drop the check for search mode (`_model.Search is null`).
- **Search follows the pane it lives in.** Typing while the other pane is active moves the search there; a pane that navigates leaves its search; Esc leaves it (last in the Esc order) — undo: `OnSearchTextChanged` and `OnPaneChanged`.
- **Search text details.** The typed text is trimmed. "searching…" shows only before the first answer — because typing would otherwise flicker the title — undo: `SearchModel.SetText` and `SearchIfDueAsync`.
- **Hit folders read from the searched folder's name down** (`docs\log`); whole-volume searches show the whole path — because the right-cut full path hid the useful part — undo: `DisplayFormat.FolderUnder`.
- **`plugins.list` and `editor.openMarkdownPreview` are window commands.**
  - They are listed after the core's hits, matched by a plain substring, and cannot be rebound. The registry's entry wins once the core has one.
  - Because the palette must list them and the core's registry does not yet — this is a stopgap.
  - Undo: `RegisterWindowCommand` → `RegisterLocal`.
- **Automatic review.**
  - The review dialog opens by itself only when a plugin newly enters `needs_review`, once per plugin per session.
  - Plugins waiting at start get a status-bar line instead.
  - Keyboard focus starts on Cancel.
  - Because a Cancel must hold, a start should not ambush the user, and Enter must not grant by accident.
  - Undo: `PluginWatch` and `ReviewDialog.Open`.
- **Reload is also offered for `failed` plugins** — because `reload_plugin` reads a fixed manifest again — undo: `PluginRow.CanReload`.
- **What Allow grants.** The core's `missing` list, else the not-granted capabilities — undo: `PermissionReview.ToGrant`.
- **Tools are WebView2 only,** each in its own browser process (`WebView2\tool-<id>`) — because of the brief's §6 crash rule — undo: none needed.
- **A `ready` handshake was added to the tool protocol** — because messages posted before the page's listener exists are lost — undo: send on `NavigationCompleted` instead (`ToolHost`).
- **Tool command allow-list.** `go.*`, `view.toggle*`, `view.focusOtherPane`, `palette.show`, `search.focus`, `help.about`, `terminal.new`, `editor.openMarkdownPreview`. Everything else is refused and logged — because a web page must not change files or grant rights — undo: `ToolMessages.AllowedCommands`.
- **A host key script is put into every tool page** — because Ctrl+Shift+P and Ctrl+` must work while a tool has focus — undo: remove `HostScript` in `ToolHost`.
- **The UI reads `tool.json` once at start, on a background thread.** This is a bounded exception to the brief's §1 no-file-I/O rule, recorded in ARCHITECTURE — because the core has no request that lists tools — undo: switch to a core `list_tools` request when one exists (see "Needs the core").
- **`placement: dock` tools open as a pane's editor tab** in this version — because the task says "other tools open as a pane's editor tab" and the dock holds the terminal — undo: add tool tabs to `ToolDock`.
- **One tool per pane.** An open tool is reused for the next file. Showing one pane closes the right pane's editor — undo: `OpenInToolAsync` and `ApplyDual`.
- **Enter on a file an installed tool accepts opens the tool;** without one, the default app (Article 10) — undo: the tool branch in `OpenAsync`.
- **Ctrl+K V is added to the keymap only when it takes nothing** — undo: `WithWindowBindings`.
- **Markdown Preview does not sanitize HTML; a CSP stops it from running** (no inline script, scripts only from the tool) — undo: add a sanitizer.
- **Developer tools in Debug through the right-click menu (Inspect)** — because F12 goes to the page with the other browser keys — undo: `WebViewHost.Configure`.
- **The context message** is debounced to 100 ms, with at most 1,000 selected paths and `truncated: true` — undo: `_contextTimer` and `ToolMessages.MaxSelection`.
- **The keyboard that would go back to the pane goes to a tool page that covers it** (one pane shown). **A search into a covered pane closes the tool first.** Undo: `FocusActivePane`, and the `CloseEditor` call in `OnSearchTextChanged`.
- **Snapshots** are raw RGBA, like 5b's, with WebView2 pixels laid over them by `CapturePreviewAsync` — because the snapshot aid then works on the locked screen — undo: none needed.
- **CI: the `ui` job also runs for `sdk/tools/**`** — because its tests validate the shipped `tool.json` — undo: revert those lines in `ci.yml`.

### Needs the core
- **Register the window's commands in the registry** (target ui), so they are ranked, listed and rebindable: `plugins.list`, `editor.openMarkdownPreview` (default keys `ctrl+k v`, when filesView), `terminal.new`, `terminal.show`, `terminal.close`, `terminal.reload`, `search.scope`, `editor.close`, `editor.reload`. Once they are there, the window-command stopgap goes.
- **A `list_tools` request** (the core reads `<tools>\<id>\tool.json`), so the UI stops reading files.
- **A setting for the dock's size**, for example `ui.dockSize`, so a dragged size survives a restart.
- **`describe_entries` names `.gitattributes` and `.gitignore` "txtfile"**, which is the ProgID and not a friendly name. A fallback to "{EXT} File" would read better.

### Needs the user
- **The unlocked-screen run.** It proves the keys inside WebView2 pages and that the keyboard returns after the palette, which the locked screen cannot prove.
  - Script: `C:\Users\Admin\AppData\Local\Temp\claude\E--codespace--claude-code--rde--cabinetos-windows-system-manager-cabinetos\eacd1e24-041a-4dd4-93ae-c968cae9eca0\scratchpad\livecheck.ps1`. It now also covers:
    - Ctrl+` both ways, and typing in pwsh;
    - Ctrl+Shift+P from inside the terminal, and the keyboard coming back after Esc;
    - Ctrl+F, Enter, Enter; and Ctrl+F then Esc;
    - Enter on readme.md opening Markdown Preview, and Ctrl+K V.
  - It uses a temp themes folder and the repo's `sdk\tools`.
  - It needs PowerShell, because it drives the Windows window with SendInput.
  - Run it from an unlocked, idle desktop: `powershell -ExecutionPolicy Bypass -File "<that path>"`.
- **Carried over from 5b, unchanged:** the Windows App SDK license question, and the two Recycle Bin files.

### Known gaps
- Real keys inside the terminal page and the tool page are not verified. The keyboard coming back after the palette, and non-US layouts, are not verified either.
- **No network, as far as it goes.** A third-party tool without its own CSP could still open a WebSocket, because `WebResourceRequested` does not see WebSocket handshakes. Navigation and normal requests are blocked. Possible hardening: browser arguments such as `--host-resolver-rules="MAP * ~NOTFOUND"` in `WebViewHost`. This is not done, because it is untested.
- In the light theme the terminal uses xterm.js's default ANSI palette, which is tuned for dark backgrounds.
- Tools are read only at start; a new tool needs a restart.
- Dock tools and tools opened without a file do not exist yet.
- Folder sync cannot see a `cd` typed by the user: there is no shell integration (OSC 7).
- In snapshots, check boxes always show a dash. The bitmap draws the first frame of WinUI's animated check mark; it is not a state bug. This is noted in ui.md.
- Only a walk was tested live, because the indexer does not run here. The `index` source is covered by unit tests only.
- My runs created `%LOCALAPPDATA%\CabinetOS\WebView2\terminal` and `tool-markdown-preview`. These are the app's normal data folders; I left them. My `%TEMP%\cabinetos-ui-test\` folders were removed.

### Noticed out of scope
- At shutdown the UI log shows "cannot read the configuration: the core is not running". A `config_changed` event arrives after the core stopped, from the `ui.lastPaths` save. It is harmless noise; the UI could skip reading while closing.
- Plugins waiting at start produce no `plugin_state_changed` for a new connection, so the UI uses `list_plugins`.
- The core's themes (protocol 10) are not applied by the UI, as instructed ("no themes").

### Ready text for PLAN.md

Add after the Phase 5b paragraph in the Phase 5 section:

**The UI halves of Phases 6, 7 and 8 (Phase 5c), built 2026-09-28.** With protocol 9 the shell's own commands come from the core's registry and keymap, so every pane key can be rebound, and the Type column and the icons are the shell's (`describe_entries`, `get_icon`, asked a page of 128 rows at a time, icons at the screen's scale). The terminal (Ctrl+`, the command-bar button, "Open in Terminal") is the Tool Dock's first occupant, under the panes or beside them (`ui.layout`), behind a splitter: xterm.js 6.0 in WebView2, one tab per shell with the profiles of `terminal.profiles`, the core's byte pipes pumped at most 60 times a second, and the shown shell following the active pane after 300 ms unless a line is half typed or a full-screen program runs; a shell that ends shows its exit code for 3 s before the tab closes. The search field (Ctrl+F) sends `search` after 150 ms of quiet, limited to the active pane's folder unless "Whole volume" is on, and shows the hits in that pane with the core's source, time and completeness; Enter goes to a hit, Esc back to the folder. "Plugins: Show Plugins" lists the plugins with their capabilities and offers Review and Reload; the permissions review dialog (design view C) grants with `grant_capabilities` and opens by itself when a plugin newly waits for review. Tool Extensions run in WebView2, each in a browser process of its own and without network; Enter on a file a tool accepts opens it as the pane's editor tab (design view D). The first tool, Markdown Preview, lives in `sdk/tools` and is opt-in (Article 10). Crash isolation was checked by ending the terminal's and the preview's browser processes: the pane said so with Reload, the window went on, and Reload brought back the same shell and file. 327 tests, five against the real core. Measured 2026-09-28 (release builds, locked screen): the first Ctrl+` has a running pwsh after 0.48–0.54 s, later shells after 35–44 ms; a walk of `docs/` answered in 1.2–1.4 ms. Snapshots in [log/2026-09-28/](log/2026-09-28/); the real-key run (keys inside the terminal and the preview) waits for an unlocked screen. Guides: [ui.md](ui.md), [tool-extensions.md](tool-extensions.md).

Phase 6:
- Replace "Core side done 2026-09-28; the search field waits for the UI (Phase 5)." with "Done 2026-09-28: the core side, and the search field in the UI (Phase 5c)."
- In the heading, "core side done" → "done".

Phase 7:
- Replace "Core side done 2026-09-28; the permissions review dialog, the Tool Dock and the sample Tool Extension wait for the UI (Phase 5)." with "Done 2026-09-28: the core side, and in the UI (Phase 5c) the permissions review dialog, the Tool Dock with the terminal, and Markdown Preview as the sample Tool Extension. The sample renders in a pane's editor tab; a tool that asks for the dock opens in a pane until the dock has tabs for tools."
- In the heading, "core side done" → "done".

Phase 8:
- Replace "The rendering control is an open question (see section 7)." with "The rendering control is xterm.js in WebView2 (section 7, question 1, settled)."
- Replace "Core side done 2026-09-28; the terminal pane waits for the UI (Phase 5)." with "Done 2026-09-28: the core side, and the terminal pane in the UI (Phase 5c)."
- In the heading, "core side done" → "done".

Section 7, question 1: "**Terminal rendering control (Phase 8).** Settled 2026-09-28: xterm.js 6.0 in WebView2 (Phase 5c), with the DOM renderer and a browser process of its own; a native renderer stays possible later."

### Ready text for the README status line

The core test count is the core agent's; I did not measure it after the themes commit.

Status: pre-alpha. Phases 0 to 4 and 6 to 8 of [the plan](docs/PLAN.md) are done: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, runs copy, move and delete jobs on per-disk queues, runs sandboxed WebAssembly plugins whose crashes it contains, searches whole NTFS volumes through an elevated indexer (or walks folders without it), and runs shells in pseudo-consoles, all over a user-only named pipe, with the indexer behind a read-only pipe of its own and each shell's bytes on a pipe of their own (`core/`, CI green). Phase 5, the WinUI 3 shell, is built (`ui/`, 327 tests, CI green): two panes over the core's shared-memory listings with the shell's type names and icons, breadcrumbs, a status bar, the command palette with chord keys and inline rebinding, copy, move, delete, rename, new folder and open with the transfer flyout and conflict decisions, file search, the plugin list and permissions review, the integrated terminal in a Tool Dock, and Tool Extensions each in a WebView2 process of its own, with Markdown Preview as the first (opt-in, in `sdk/tools`); its scrolling check and the real-key run still wait for an unlocked screen.
