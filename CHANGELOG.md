# Changelog

All notable changes to CabinetOS are recorded in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The first version, built on 2026-09-28 and not released yet. It becomes
0.1.0 when the first release is published ([docs/release.md](docs/release.md)).

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
- An integrated terminal (Ctrl+`) with PowerShell, Command Prompt and WSL profiles, which follows the active pane's folder.
- Colour themes in JSON (accent, Mica tint, palette, terminal colours), applied live, with a theme picker (Ctrl+K Ctrl+T). Default, Nord, Catppuccin Mocha and Rose Pine Moon are built in; Default follows Windows' light or dark mode.
- A marketplace view that installs plugins, themes and tools from a static index and checks each download's SHA-256. No public index exists yet.
- `cabinetos-cli`, which reaches the core's functions from a terminal.
- One JSON Lines log per program and day, in which a request ID follows each action from the window through the core. A crash writes a trace that names where it happened: the window, the core or a plugin.
- A release zip with an install script (per user without administrator rights, or for all users; the Start Menu entry, the PATH entry and the indexer service only when asked), an uninstall script, and the license text of every third-party component.

[Unreleased]: https://github.com/OliverD25/cabinetos/commits/main
