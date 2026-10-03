# Changelog

All notable changes to CabinetOS are recorded in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Two light themes ship with the core, Catppuccin Latte and GitHub Light, so a fresh install's theme picker has a light choice by name (the picker now lists seven themes). They moved from the theme collection: the collection no longer lists them, and a copy installed from the marketplace before stays as it is.
- 18 more themes in the theme collection for the marketplace (the theme gallery), all MIT licensed ports with every colour read from the theme's own repository: Everforest Dark and Light, Kanagawa, Rosé Pine, Rosé Pine Dawn, Nightfox, Dayfox, Vitesse Dark and Light, Flexoki Dark and Light, Oceanic Next, Spacegray, Moonlight, Poimandres, Andromeda, Quiet Light and Min Light. Modus Vivendi, Modus Operandi, Zenburn and Bluloco Light are left out because their licenses are GPL or LGPL. The collection has 52 themes; they reach users when the public `themes.json` is published.

### Fixed

- The command palette no longer fails in the background when its list changes while it scrolls to the highlighted command, as happened on its first showing when a second answer or a key came before the palette was drawn; the highlighted command still comes into view.
- A copy, move or delete no longer sends progress records with a stopped clock. When a job's work ends, the core writes the undo journal before it reports the job done. On a busy PC that took several hundred milliseconds, and every progress record sent in that time carried the same `elapsed_ms` while the time went on. Now the final record is the only one after the work ends. The limit of 30 progress records per second was kept all along; only the time stamps were wrong.

## [0.1.2] - 2026-10-03

### Added

- The theme gallery: the themes as colour tiles, each painted in the theme's own colours, with filters for dark, light, system themes, density presets and the installed ones (Windows' mode first), and a search. Selecting a tile previews the theme on the whole window with nothing installed or written, and Esc paints the theme in effect again. Enter or a double-click installs a theme and applies it. Open it with "Themes: Browse" in the command palette (`themes.browse`; bind it to keys if you like) or the new last row of the theme picker, "Browse more themes…". If the themes catalogue cannot be read, the gallery shows your installed themes and says so.
- Four settings that could be changed only by editing `cabinetos.json` now have a command and a control in the window. The layout (`ui.layout`): "View: Classic Layout", "View: Terminal on the Right" and "View: Activity Rail" in the palette, "View: Next Layout" on Ctrl+K Ctrl+L, and a "Layout" menu in the top row's menu that checks the current one. Hidden files (`panes.showHidden`): "View: Toggle Hidden Files" on Ctrl+K Ctrl+H, a "Show Hidden Files" row in the top row's menu, and the word "hidden" in the status bar while they are shown. The Explorer following the active pane (`ui.sidebarAutoReveal`): "Sidebar: Follow the Active Pane" in the palette, a pin in the Explorer view's header, and a "Follow the Active Pane" row in the top row's menu. Windows' own menu on Shift+right-click (`contextMenu.shellMenu`): "Menu: Toggle Windows' Shell Menu" in the palette and a check box at the end of "Edit Menu…". Each is the same change whichever way you make it: the file, the palette and the window follow each other at once. The palette's rows for them say "current", "on" or "off".
- A click on the Name, Modified, Type or Size heading of a file pane sorts that pane by the column, and a second click reverses it. The sorted heading shows a small arrow. It is the same sort as Ctrl+F3 to Ctrl+F6 and the palette's "View: Sort by ..." commands, so the three ways agree; the order is the pane's own, and Type sorts by extension. A double-click on a heading still fits the column, and leaves the order as it was.
- A divider between the two file panes: drag it to give the left pane more or less of the width. The share is kept in the new setting `ui.paneSplit` (0.2 to 0.8, `null` for equal), so it survives a resize of the window. A double-click on the divider and the new palette command "View: Equal Panes" (`view.equalPanes`, no key) make the panes equal again; editing `ui.paneSplit` in `cabinetos.json` moves the divider. A pane never gets narrower than its columns need.
- The sidebar's divider works in the classic layout and in the right layout too, not only in the rail layout. A drag still saves `ui.sidebarWidth`, and a double-click on the divider gives the design's width back.

### Fixed

- The plugin sandbox runs on wasmtime 49.0.2, which closes seven security advisories of 49.0.1 (RUSTSEC-2026-0321 to 0327: GC heap corruption through mis-typed tag imports or `try_call`, a stack overflow through an unvalidated callback result count, a fuel bypass and three WASI faults). None was reachable without a plugin written to exploit it.

### Changed

- The marketplace page is now the Extensions page (Discover, Plugins, Tools and Installed, "Search extensions") and lists no themes. Themes come from a catalogue of their own, `themes.json`, at `marketplace.themes` (the public address by default); `index.json` lists plugins and tools only. A server that has not published `themes.json` yet still works: until it does, the gallery shows the themes its `index.json` carries. The command keeps its ID and is now called "Marketplace: Browse Extensions"; the core's protocol version is 20, and `cabinetos-cli market` has `--themes` to ask for the themes catalogue.
- `cabinetos-core.exe`, `cabinetos-cli.exe` (and its copy `cab.exe`) and `cabinetos-indexer.exe` carry a Windows version resource: version, description, copyright and the CabinetOS icon. Explorer's Properties > Details page shows it, and the code signing service requires it before it signs a program. The release script stops when a program lacks it.
- The release zip and the setup file no longer hold the `.pdb` symbol files: they are a download of their own, `CabinetOS-<version>-win-x64-symbols.zip`. The release zip is 32 MB instead of 76 MB and the setup file 20 MB instead of 43 MB. A crash trace from an install names function, file and line once you unpack the symbols of the same version next to the programs; without them the Rust programs' trace shows `<unknown>` frames, though its `location` still names the line of the panic.
- `cabinetos-indexer --install` registers the service with an automatic, delayed start and starts it once, so the index is there after every restart of Windows with nobody starting the service (it was a manual start, off after each restart). `install.ps1 -AllUsers -Indexer` does the same. If you registered the service by hand before, run `cabinetos-indexer --uninstall` and then `--install` again.

## [0.1.1] - 2026-10-03

### Changed

- The setup file installs a missing prerequisite itself: it downloads Microsoft's installer for the Windows App Runtime, the WebView2 Runtime or the .NET 10 runtime, runs it, and checks again. Before, it named the winget command and stopped. The .NET runtime installs for the whole PC, so Windows asks for administrator rights for it; a silent setup without them still stops and names the winget command. The winget package is now this setup file instead of the zip.

## [0.1.0] - 2026-10-02

Code signing policy: see the [README](https://github.com/OliverD25/cabinetos#code-signing-policy).

The first version. How it is built, signed and published:
[docs/release.md](docs/release.md).

### Added

- Two file panes side by side, or one at a keystroke, with breadcrumbs, back, forward and up, pinned folders, and the drives with their free space.
- Folder listings read with the NT API and handed to the window through shared memory: 100,000 entries in about 50 ms.
- Open folders refresh when their contents change on disk, and drives appear and disappear as they are plugged in.
- Type names and icons as Windows Explorer shows them.
- Copy (F5), move (F6), new folder (F7), rename (F2), delete to the Recycle Bin, and delete for good (Shift+Delete) after a confirmation.
- One copy queue per physical disk: one job at a time on a hard disk, several on an SSD, with pause, resume, cancel and a live speed graph.
- A file conflict pauses only that file: skip it, replace it, or copy it under a new name, once or for every conflict of its kind.
- Open a file with its default program, a folder in the other pane, and Properties for any row.
- The command palette (Ctrl+Shift+P) finds every command by name, shows its keys, and changes them in place.
- Chord keys such as Ctrl+K then Ctrl+T, and a protected set of system keys that cannot be rebound away.
- Every setting lives in `cabinetos.json`; a saved change applies while CabinetOS runs.
- Search as you type, in the current folder or across a whole volume.
- An optional indexer service that reads the NTFS master file table and change journal, for instant search of whole volumes (`install.ps1 -AllUsers -Indexer`).
- Core Plugins: WebAssembly components in a sandbox, with only the capabilities granted in a review dialog; a plugin that crashes is stopped while CabinetOS keeps running.
- Tool Extensions: web pages in WebView2, each in a browser process of its own and without network access. Markdown Preview is the first, opt-in, in the release's `extras` folder.
- An integrated terminal (Ctrl+\`) with PowerShell, Command Prompt and WSL profiles, which follows the active pane's folder. A fourth profile, `claude`, runs Claude Code on the user's own login; it is not a shell, so `followsPane: false` keeps the folder sync from typing into it. `cabinetos-cli` (and `cab` in a release) is on the `PATH` of every terminal.
- Colour themes in JSON (accent, Mica tint, palette, terminal colours), applied live, with a theme picker (Ctrl+K Ctrl+T). Default, Nord, Catppuccin Mocha and Rose Pine Moon are built in; Default follows Windows' light or dark mode.
- A marketplace view that installs plugins, themes and tools from a static index and checks each download's SHA-256. No public index exists yet.
- `cabinetos-cli`, which reaches the core's functions from a terminal.
- One JSON Lines log per program and day, in which a request ID follows each action from the window through the core. A crash writes a trace that names where it happened: the window, the core or a plugin.
- A release zip with an install script (per user without administrator rights, or for all users; the Start Menu entry, the PATH entry and the indexer service only when asked), an uninstall script, and the license text of every third-party component.
- One top row that is also the window's drag area: a menu of common commands, the workspace pill with the git branch of the active folder, a command center, and the view and Settings buttons.
- Each pane has its own tab strip and breadcrumb row with Back, Forward and Up; long paths collapse to `Drive › … › parent › current`, and Ctrl+L types a path.
- Find in Pane (Ctrl+F): filters the active pane's list by name as you type, and Esc shows every row again.
- Quick Open (Ctrl+P): the files and folders of the workspace on the command palette's surface; `>` switches to the commands. Enter opens a row in the active pane, Ctrl+Enter in the other one.
- Ctrl+1 to Ctrl+9 bring a pane's tabs to the front, and Ctrl+, opens `cabinetos.json` for editing.
- Updates from inside the app for the per-user install: a check once a day (`update.check`, and `update.channel` for the stable or the preview channel), the download in the background with a pill in the status bar, the release notes in a dialog with Restart now and Later, and a rollback to the version before. A download whose SHA-256 does not match is deleted, and a swap that fails half way puts the old version back. `cabinetos-cli update` does the same from a terminal.
- CabinetOS appears in Settings > Apps, with its version and size, and uninstalls from there.

- "Toggle Compact Overlay" (Ctrl+Alt+Up) makes the window a small always-on-top drawer with one pane, no sidebar and no dock, 480 by 640 or the size you last gave it (`ui.compactOverlay`), and brings the window back as it was.
- The right-click menu comes from `contextMenu` in `cabinetos.json`: for the empty space, a file, a folder and a selection of several rows, each with its row of icons and its list, and rows shown only for some file extensions.
- "Edit Menu…" at the end of the right-click menu edits the menu inside the menu: drag a row or move it with Alt+Up and Alt+Down, remove it with its X or Delete, add a command (Insert, from a list like the palette's) or a separator, and Done (Ctrl+S) saves it to `cabinetos.json` through the core. Esc cancels, and a click outside does not close it. The row of icons, the extension filters and the programs are still edited in the file, which Ctrl+, opens.
- Programs of your own (`programs` in `cabinetos.json`) become commands (`program.<name>`) for the menu, a key or the palette. `{path}`, `{selection}` and `{cwd}` in their arguments stand for the cursor row, the selection and the folder.
- Windows' own context menu (Open with, Send to, what other programs add) with Shift+right-click or Ctrl+Shift+F10, when `contextMenu.shellMenu` is on.
- The theme picker (Ctrl+K Ctrl+T) previews the highlighted theme live, whether the keys or the pointer moved the highlight; Enter or a click keeps it, and Esc keeps the current theme.
- The file panes' column widths are yours, in any theme: drag the grip at a divider of the column headers, or double-click a heading (or a grip) to fit its column to the texts on screen; the Name heading fits the other three. Both panes share the widths, and they are saved in `cabinetos.json` as `ui.columns`. "Reset Column Widths" in the palette gives the theme's widths back.

- Folder sizes for every folder of a listing: turn on `panes.folderSizes` in `cabinetos.json`, or run "Toggle Folder Sizes" from the palette, and each folder's Size is counted when a listing opens, with no key. It is off by default, because counting costs disk time. A pane that leaves a folder stops its count.
- The column view: Ctrl+Alt+C ("Toggle Column View") shows a pane's tab as columns of names, as Finder does. Enter, Right or a click on a folder opens it in a column to the right, Left goes back up, the whole path stays on screen, and the tab keeps the mode across a restart.
- Each terminal belongs to a file pane. Its tab shows `[Left]` or `[Right]` and a Locked/Linked switch ("Lock or Link Terminal to Its Pane" in the palette). Command Prompt and Claude Code stay locked, and the tooltip says why.
- A linked PowerShell or WSL terminal follows its pane: each time the shell shows its prompt, it changes to the folder the pane shows, if the pane moved. Nothing is typed into the shell, so a half-typed line or a running program is never touched; the line runs where you typed it, and the next prompt is in the pane's folder. A `cd` of your own stays until the pane moves again. It costs about 20 ms per prompt. `"hook": false` on a profile in `cabinetos.json` turns it off, and a string there runs your own code at each prompt instead.
- The terminal's caption follows the shell: it says "in docs" for the folder the shell is in, after a `cd` of your own too, and its tooltip names the whole folder.
- `cab term cwd` prints the folder a linked terminal follows; in a CabinetOS terminal, `cab` reaches the window's own core without `--pipe`.
- Keys for the terminal's tabs while it has the keyboard: Ctrl+Shift+T opens a terminal for the active pane, Ctrl+Shift+W closes the one in front, and Alt+[ and Alt+] show the tab before or after it. They also work on the Ukrainian keyboard layout. Ctrl+Shift+C copies the selected text and Ctrl+Shift+V pastes.
- Ctrl+\ in the terminal splits the dock under the two panes ("Split Terminal Under the Panes"): the left pane's terminals sit under the left pane and the right pane's under the right, each half with its own tab row, header and caption, and the halves follow when the panes' width changes. A pane with no terminal shows "Ctrl+` starts a shell for this pane". Alt+[ and Alt+] go round the tabs of the half that has the keyboard, Ctrl+Shift+W closes that half's tab, Ctrl+Shift+T opens a tab in the half of the active pane, and Ctrl+` in a pane gives that pane's half the keyboard. In a file pane Ctrl+\ is still "Up to Root". The split is saved as `terminal.split` in `cabinetos.json`, off by default, and holds while the dock is under the panes.
- `cab` in a CabinetOS terminal gives the live GUI context: `cab pane` prints the folder of the active pane (`--left`, `--right`, or `--json` for the whole context), `cab selection` prints the active pane's selected paths, one per line (the marked rows, or the cursor row when none is marked), and `cab copy --selection --dest opposite_pane` (or `--dest <path>`) and `cab move --selection --dest opposite_pane` run the core's job on that selection and follow it. A job started this way skips what exists at the destination unless you give `--on-conflict`. The exit code is 0 when it worked, 1 for a failure (also a selection the window cut at 1,000 rows), and 2 when there is no window or nothing is selected. There are no `CABINET_*` environment variables: the commands ask the window's core each time, so the answer is never old.
- A theme may set `terminalLeftBadge` and `terminalRightBadge`, the colours of the `[Left]` and `[Right]` badges. Without them, the left badge is the accent and the right badge is the accent with its hue turned by 150 degrees. The theme format is now version 3.
- The terminal tabs come back after a restart. The window saves its terminal sessions in `cabinetos.json` (`terminal.tabs`: profile, folder, pane and mode of each, in the order of the tabs, and the tab in front), and the first time you show the dock after starting (Ctrl+\`, the terminal button, Ctrl+Shift+T), they start again as fresh shells in the folders they were left in, with the same tab in front. A profile, a folder or a link that is gone falls back (to the default profile, your profile folder, a locked session), and a session that cannot start is skipped while the rest come back. `terminal.restore: false` turns it off.
- `terminal.defaultMode` (`locked` or `linked`, `locked` by default) is the mode a new terminal starts in. A profile that cannot be linked stays locked.
- `cab copy|move --selection --dest opposite_pane` refuses while only one pane is shown ("the other pane is hidden; show both panes or name a path", exit code 1), as the window's own "copy to the other pane" does. The protocol is now version 19 (`dual` in `window_state` and `gui_context`).
- A setup file for the first install, `CabinetOS-<version>-win-x64-setup.exe`: a double-click installs CabinetOS for you alone, with no administrator rights, into `%LOCALAPPDATA%\Programs\CabinetOS`, with a Start Menu shortcut, a desktop shortcut if you tick it, and "Start CabinetOS" at the end. It checks the .NET 10 runtime, the Windows App Runtime and WebView2 first and names the winget command for a missing one. Settings > Apps removes it, and your settings and logs stay. `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=<file>` installs with no window. The zip and `install.ps1` stay as they were.
- Updates install themselves: once a newer version is downloaded and its SHA-256 checked, CabinetOS puts it in place in the background, and the status bar only says "CabinetOS 0.2.0 is installed; restart to use it", with Restart now and Later. The text opens the release notes. Later keeps your session; the next start runs the new version. If the update cannot be put in place, the status bar says so, the version you run stays whole, and the next daily check tries again. `update.autoInstall: false` in `cabinetos.json` brings back the dialog that asks before the update.

### Changed

- Total Commander's "path to the command line" (Insert Folder Path) moves from Ctrl+P to Ctrl+Alt+P, because Ctrl+P is Quick Open.
- Ctrl+F and Alt+F7 open the pane's find widget. The search through subfolders is the Search view (Ctrl+Shift+F), which the classic and right layouts show in the sidebar's place.
- A pane's tab strip shows from the first tab, with a "+" button; a middle click closes any tab.
- Ctrl+K Ctrl+W opens the workspace pill's dropdown.

- Ctrl+\` works for the pane you press it in: it shows that pane's terminal with the keyboard, or starts one in the pane's folder. Pressed in the other pane, it shows that pane's terminal and never hides the dock. In the terminal it gives the keyboard back to the pane, and pressed again right after that, it hides the dock.
- `cabinetos-cli update download` also puts the new version in place, unless `update.autoInstall` is `false`; `update apply` is for a download made with it off.
- The terminal no longer follows the active pane. Clicking a pane, switching panes or opening a folder never changes the terminal in front and never types into a shell; the shell stays where you take it, unless you link it to its pane. `followsPane` in `cabinetos.json` is ignored, and `cabinetos-cli term cd` is gone (`term mode` locks or links a terminal).

- The right-click menu is WinUI's command bar menu, as Windows 11's Explorer shows it: the icons in a row at the top, the keys on the right of each row.
- The shell follows v2 of the redesign. The top row is quieter: the title "CabinetOS · folder" follows the active pane's tab, and a small Quick Open chip (Ctrl+P) stands before the view buttons; the workspace pill and the centred command center are gone.
- The workspace switcher is the sidebar's first row: the workspace, its branch and a chevron. A click or Ctrl+K Ctrl+W opens the dropdown at the row's width, and picking the workspace goes to its root in the left pane. With the sidebar hidden, Ctrl+K Ctrl+W still opens it.
- Each pane has a toolbar row under its tabs: Back, Forward and Up, a drive chip that opens the drive list (as Alt+F1 and Alt+F2 do), the drive's free space, and Find, which opens and closes the pane's find. Under it a path row shows the folder's path, whole up to 5 parts with two panes (8 with one), and a filter label: `*.*`, or `*text*` while the find holds a text; a click on it opens the find. The find drops from the toolbar row.
- The tabs of a pane are a recessed band with the tab in front as a card in the toolbar's fill, so the tab and the toolbar read as one surface. The other tabs are lower, with dividers between them; every tab has a folder glyph (or its lock, or its tool's); the × shows on the tab in front while the pane has more than one tab; no tab has an accent line. The "Open with…" button sits in the toolbar now, hidden until a second editor exists; its Editor commands stay in the command palette.
- Themes may size the new rows: `toolbarRowHeight`, `pathRowHeight`, `workspaceHeaderHeight`, `quickOpenChipHeight` and `tabMaxWidth` are new metrics, and `tabRow` and `tabRadius` have new default values. Commander Compact is version 1.2.0 with all of them. The workspace pill's, the command center's and the breadcrumb row's metrics are still accepted and size nothing.
- CabinetOS shows its first folders about 0.2 s sooner: the core starts while the window is still being built.
- Find in pane, marking by a pattern and quick search go through the names of a large folder about 2.5 times faster: 100,000 names in about 17 ms instead of 40 to 50 ms.
- The first right-click menu of a session opens a little sooner: the menus are built while the window is idle after start.
- The marketplace shows its first cards without a pause: it makes the cards that fill the view at once and the rest a screenful at a time. The first opening held one frame for about 115 ms with 50 cards; the cards' frames now stay under about 40 ms, the longest frame of the first opening is about 65 ms, and a larger collection costs no more.
- The marketplace's first opening in a session holds the window a little less (its longest frame about 42 ms instead of 52): the window lays the marketplace out once while it is idle after start, after a second with no key or mouse input.
- Going back to the tab a pane showed just before does not list its folder again: the pane keeps that tab's listing for 30 seconds, with its cursor and marks. A tab on 100,000 files is back on screen in about 17 ms instead of 60 to 90 ms.
- The file lists keep half a screen of rows made above and below the view instead of two screens. Changing into or out of Commander Compact freezes the window for about 25 ms less (the frame 106 to 79 ms), and scrolling a large folder does less work.
- Icons come with the rows: the core draws the folder and file icons while the window starts, and the icons of a folder's file types right after it sends the listing, instead of one by one when the rows ask for them.
- The release build compiles the window ahead of time (ReadyToRun), so CabinetOS starts 0.14 to 0.24 s sooner. The download is about 5 MB bigger (the unpacked folder about 16 MB).

- Shortcuts work while a text box has the keyboard, when they type nothing there: Ctrl+B, Ctrl+Tab, Alt+Left, Ctrl+K Ctrl+T and the function keys run their command. The box keeps the keys that type or edit, such as letters, Enter, Tab, the arrows, Ctrl+A, Ctrl+C and Ctrl+V. In a pane's find box and address box the pane's keys work too: F5 copies the cursor row to the other pane, Ctrl+T opens a tab. Before, only Ctrl+Shift+P, Esc and Ctrl+K Ctrl+S worked in a box.
- Quick Open and the search of a folder, when no indexer answers, keep only the best hits while the core walks the folders, not every name that matches. A query that matches every name of a large walk no longer holds up to about 30 MB of hits for a moment, and a walk of 100,000 names answers about 10 ms sooner (about 100 ms instead of 111 to 114 ms). The hits and their order are the same.

### Removed

- The title bar with its workspace tab, the command bar, the global address bar and the global search field, and the pane's header: the top row and each pane's tab strip and breadcrumb row replace them. No setting of `cabinetos.json` served only these parts; the theme metrics that sized them are still accepted and size nothing.

### Fixed

- The right-click menu opens inside the window also on a busy computer. The window noted a menu's size when only its icon row had been drawn (60 high, not 268), and the next menu of that shape then opened without flipping above the pointer near the window's bottom edge, partly outside the window. It notes the size now once every button of the menu is drawn.
- In the edit mode of the right-click menu, Delete takes out the row that has the keyboard, also right after Alt+Up or an added command. On a busy computer the window's UI framework (WinUI) told the window about a row's focus a frame late, after the rows were rebuilt, and Delete then took out the row beside the one that was moved.
- The command palette ignores a search answer that comes after it closed. Closing it in the few milliseconds the core needs to answer could end with an error in the log ("a background task failed and nobody observed it"); nothing showed on screen.
- A click from the terminal into a file pane gives the pane the keyboard: the keys pressed right after it (Home, Enter, Backspace, any pane key) act on the pane. They used to do nothing, and did not reach the shell either: right after the click, the window's UI framework (WinUI) moved the keyboard a second time, to the window's background.
- A run of the window that ended without closing, as a native failure of WinUI does without a crash trace, is noted in the log at the next start: a WARN line, `previous run ended without closing`, with the time that run started. Nothing is shown on screen. The window now writes its clean end into `ui.last-start`, in the log folder.
- Quick Open and the search of a folder find names after the first 20,000 entries of a large folder, such as one of 100,000 files. The core's walk without the indexer now stops after 2 s or 200,000 entries, not 20,000. When it still stops early, Quick Open says "Not every name was searched" under its rows, also when there are none, instead of a bare "0 results".
- The command palette opened from a tool's page, such as a Markdown Preview, gives the keyboard back to that page when it closes, as it does for the terminal. It used to give it to the active pane's list.
- Esc while a chord waits for its second key ends the wait, and Ctrl+Shift+P there ends it and opens the command palette. Both used to be reported as a chord that is not bound, and nothing happened.
- Tab stays inside the permissions review while it is open, as in the plugin list, instead of moving the keyboard to the window under it.
- A shortcut held down runs once. Holding Ctrl+Shift+P, Ctrl+\` or Ctrl+B a moment too long made the palette, the terminal or the sidebar open and close again and again. Ctrl+Tab, Ctrl+Shift+Tab, Insert, Alt+Left, Alt+Right and Alt+Up still repeat while held.
- Tab switches panes again after a click on a button of the top row. The click left the keyboard on the button, and Tab walked from button to button. The buttons of the top row, the status bar, the breadcrumbs, the tab strips and the dock no longer take the keyboard from a click.
- The right-click menu opens with its top-left corner at the pointer, as Explorer's does, and hangs above or to the left of the pointer near the window's edge. It used to open centred on the pointer, over the row that was clicked. From the keyboard it hangs under the focused row.
- A chord pressed where it does not work says where it does, instead of "is not bound to a command": "Ctrl+K Ctrl+T does not work while you type in a box. Esc leaves the box." in the find box, the address box or the palette, and "Ctrl+K V works only in a file list." elsewhere.
- A chord works however long its first key is held: holding Ctrl+K until Windows repeats it no longer ends the wait with "Ctrl+K Ctrl+K is not bound", so Ctrl+K Ctrl+T, Ctrl+K V and the other chords work every time.
- Tab no longer takes the keyboard out of the theme picker or the plugin list while they stay open. Before, the next Enter or Space went to a sidebar row, a rail button or the file list under them.
- Tab gives the keyboard to a Markdown Preview (or another tool) shown in the other pane. Before, Tab did nothing while the other pane showed a tool, and the next keys still acted on the first pane's files.
- The keyboard now works in dialogs: Tab and the arrows move between the buttons, and Enter and Space press one. Before, only Esc worked in "Delete permanently?", Properties, About, the update questions and the other dialogs.
- Shift+F10 or the Menu key on a focused row that was scrolled out of view no longer closes CabinetOS without a word. The menu opens near the top of the pane instead, as it does for a row the list has not drawn.
- Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W, Ctrl+T and Ctrl+1 to 9 work while the keyboard is in a tool's page, such as a Markdown Preview or the agent's chat. Before, the page kept them, and only Ctrl+Shift+P and Ctrl+\` came out. They change the tabs of the pane that holds the page, and the keyboard goes to the tab that comes to the front. The terminal passes back Ctrl+Tab and Ctrl+Shift+Tab only: Ctrl+W and Ctrl+T are shell keys (delete word, transpose), so they stay with the shell.
- Only one overlay is open at a time. Opening the theme picker (Ctrl+K Ctrl+T), the command palette, Quick Open, a prompt (the drive list, Num +, Ctrl+D) or the plugin list closes the one that was open, and Esc closes the one on screen. Before, the picker opened over them, and the first Esc closed the one under it.
- The live-check scripts in `ui/livecheck/` write into the project's `_io` folder also when they run from a git worktree. They took it from the repository's parent folder, which for a worktree is a stray `.claude/worktrees/_io` inside the repository tree. `ui/livecheck/paths.ps1` now finds the main checkout through git, and `-Io <folder>` still names another folder. The application itself is not affected.
- A plugin's folders and the folders a pane opens are compared in their long form, so a short 8.3 spelling such as `C:\Users\CABINE~1\AppData\Local\Temp` is the same folder as `C:\Users\cabinetos\AppData\Local\Temp`. Before, the host compared the text: the Agent extension was never told about a pane that opened a folder in the short form (the preview did not show), and a plugin could not watch a folder under its roots by that name. A root written in the short form in `plugin.json` counts too, and a path that does not exist yet is judged by the folder it will be in.
- `CabinetOS.exe` has its own icon, the blue folder with the terminal badge, in the taskbar, in Explorer and in the Alt+Tab list. Before, Windows showed the generic program icon for the file itself; only the setup's shortcuts and the Settings > Apps entry had the icon. `ui/CabinetOS/Assets/CabinetOS.ico` is committed (made by `build/make-icon.ps1` from the design's size cuts) and the release ships that file.

[Unreleased]: https://github.com/OliverD25/cabinetos/compare/v0.1.2...main
[0.1.2]: https://github.com/OliverD25/cabinetos/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/OliverD25/cabinetos/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/OliverD25/cabinetos/releases/tag/v0.1.0
