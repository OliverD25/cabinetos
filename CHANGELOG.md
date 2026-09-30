# Changelog

All notable changes to CabinetOS are recorded in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-09-30

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

### Changed

- Total Commander's "path to the command line" (Insert Folder Path) moves from Ctrl+P to Ctrl+Alt+P, because Ctrl+P is Quick Open.
- Ctrl+F and Alt+F7 open the pane's find widget. The search through subfolders is the Search view (Ctrl+Shift+F), which the classic and right layouts show in the sidebar's place.
- A pane's tab strip shows from the first tab, with a "+" button; a middle click closes any tab.
- Ctrl+K Ctrl+W opens the workspace pill's dropdown.

### Removed

- The title bar with its workspace tab, the command bar, the global address bar and the global search field, and the pane's header: the top row and each pane's tab strip and breadcrumb row replace them. No setting of `cabinetos.json` served only these parts; the theme metrics that sized them are still accepted and size nothing.

[Unreleased]: https://github.com/OliverD25/cabinetos/compare/v0.1.0...main
[0.1.0]: https://github.com/OliverD25/cabinetos/releases/tag/v0.1.0
