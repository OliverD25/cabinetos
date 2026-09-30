# CabinetOS Development Plan

Status: draft v1, written 2026-09-28. This is a living document. Any session may
propose changes. A phase starts only when the creator says so.

How the project documents relate:

- [CONSTITUTION.md](../CONSTITUTION.md) is the law. It says *what* CabinetOS is. Protected; changes need the creator's approval.
- `docs/ARCHITECTURE.md` (the implementation brief, added in Phase 0) says *how* it is built.
- This plan says *in which order*, and records the decisions that were made along the way.
- `docs/decisions/` holds one short decision record (ADR) per decision: the question, the choice, the reason.

---

## 1. Decisions made on 2026-09-28

All eight were asked as questions and answered by the creator.

| # | Question | Decision | Reason in one line |
|---|----------|----------|--------------------|
| 1 | Language for the WinUI 3 frontend | **C# on .NET** | Mainstream WinUI 3 path, best tooling. `unsafe` C# reads shared memory without copying. The Dumb UI Rule keeps the garbage collector idle. |
| 2 | Administrator rights for MFT/USN indexing | **Separate elevated indexer** | UI and core run as a normal user. A small indexer process runs as admin and hands the index to the core. Without it, the core falls back to fast NT directory reading. |
| 3 | Open-source license | **MIT** | Shortest permissive license. Most common in the Rust and C# worlds. Matches the design prototype's badge. |
| 4 | Minimum Windows version | **Windows 11 22H2 (build 22621) or newer** | Mica, IoRing, Segoe Fluent Icons and Segoe UI Variable all exist. No fallback code paths. |
| 5 | Integrated terminal: core or extension | **Core, hidden by default** | Article 9 names the terminal as a system feature, so it is infrastructure. Article 4 hides it until `Ctrl+`` `. |
| 6 | Control-channel message format | **JSON** | Human-readable, easy to log and test by hand. Payloads are tiny; big data goes through shared memory. MessagePack is the upgrade path if ever needed. |
| 7 | Repository layout | **Monorepo** | `core/` (Rust), `ui/` (C#), `sdk/` (plugin interface), `docs/`. One commit can change both sides of the IPC contract. |
| 8 | The brief and the design handout | **Commit both; the brief is a living document** | Brief becomes `docs/ARCHITECTURE.md`; design bundle goes to `docs/design/`. Only the Constitution stays protected by the edit hook. |

## 2. Defaults chosen by the architect (say so to change any of them)

These did not need a question. Each has a conventional answer. They are listed
so nothing is hidden.

- **Name.** "FileForge" in the design bundle is the design codename only. The product, the executables, the config file and the title bar all say **CabinetOS**.
- **CPU architecture.** x64 first. An ARM64 build (Windows on ARM laptops) is added to CI as soon as the core compiles. It costs almost nothing early and a lot later.
- **Rust toolchain.** Stable channel, pinned in `rust-toolchain.toml`. Edition 2024. The minimum supported Rust version (MSRV) is the stable version at scaffold time (1.98).
- **Async runtime.** `tokio` (multi-threaded) for the pipe server, timers and job orchestration. Blocking disk I/O runs on dedicated threads, never on the async workers.
- **Process layout.** Three executables (see section 3). One core process per UI window process; the core exits when the UI process ends.
- **Config file.** One file, `%APPDATA%\CabinetOS\cabinetos.json`, as the brief says. Keybindings live inside it. A JSON Schema is published so editors can autocomplete it (Article 6).
- **Shared-memory strings.** File names in shared memory are stored as UTF-16. Windows APIs return UTF-16, and C# strings are UTF-16, so nobody converts anything.
- **Tool Extensions and crash isolation.** Third-party Tool Extensions run in WebView2, which is a separate process, so a crash cannot take the window down. Native WinUI controls are allowed only for first-party tools that live in this repo (the terminal). Reason: the brief requires the window to survive a Tool Extension crash, and an in-process control cannot promise that.
- **Log location.** `%LOCALAPPDATA%\CabinetOS\logs\` with one JSON Lines file per process and UTC day: `core.<date>.jsonl`, `ui.<date>.jsonl`, `indexer.<date>.jsonl` (the date is in the name because the appender rolls the file daily; see [diagnostics.md](diagnostics.md)).
- **Development workflow.** This chat plans and reviews. The `coder` agent implements from self-contained prompts. One commit per finished unit of work.

## 3. Architecture summary

Three processes, two trust levels:

```
 ┌──────────────────────────┐   named pipe: JSON commands + events (control channel)
 │ CabinetOS.exe            │◄──────────────────────────────────────────────────►┐
 │ C# · WinUI 3 · normal    │   shared memory: directory listings (data channel) │
 │ user                     │◄──────────────────────────────────────────────────►│
 │ draws pixels, captures   │                                                    │
 │ keystrokes, CommandRouter│                              ┌─────────────────────▼──────────────┐
 └──────────────────────────┘                              │ cabinetos-core.exe                 │
                                                           │ Rust · normal user · headless      │
 ┌──────────────────────────┐   named pipe: read-only      │ fs engine · job queues · config    │
 │ cabinetos-indexer.exe    │   index queries + change     │ command registry · plugin host     │
 │ Rust · Administrator     │◄─────────────────────────────┤ (wasmtime) · ConPTY terminal host  │
 │ MFT reader · USN tailer  │   notifications              │ diagnostics                        │
 │ Windows service or       │                              └────────────────────────────────────┘
 │ started on demand        │
 └──────────────────────────┘
```

- **Control channel.** Length-prefixed JSON messages over a named pipe. Every message carries a request ID (a ULID, a sortable unique ID) that the UI creates and the core logs, so one action can be traced across all boundaries (Article 12, brief section 8).
- **Data channel.** For a listing, the core creates a memory-mapped section, writes `#[repr(C)]` structs plus a UTF-16 name arena, and hands the UI a duplicated handle over the pipe. The UI reads it by pointer. No copy, no serialization.
- **Security.** The pipe is created with a DACL (an access list) that allows only the current user. The pipe name includes a random token that the UI passes to the core at launch. The indexer answers read-only queries and never performs file operations (least privilege).
- **Fallback.** If the indexer is not installed or not running, the core lists directories with the NT API directly (`NtQueryDirectoryFile` / `FindFirstFileExW` with large fetch). Search falls back to a walk. Non-NTFS drives (FAT32 sticks, network shares) always use this path.

## 4. Consistency check: brief and design vs. the Constitution

Places where the brief or the design handout disagree with the Constitution, or with each other. Each needs a decision at the phase where it matters.

| # | Where | Conflict | Proposed handling | Decide in |
|---|-------|----------|-------------------|-----------|
| A | Brief §8, first sentence | Says "telemetry"; the creator renamed Article 12 to "Diagnostics" because telemetry suggests data sent home | Done in Phase 0: `docs/ARCHITECTURE.md` says "diagnostics" and its change log records the edit. The creator's preference was already known from the Article 12 rename | Phase 0 (done) |
| B | Design §1 vs §3A | §1: first run is a single pane. §3A: dual pane by default. Article 5 makes dual-pane the primary paradigm; Article 4 wants a casual user at ease | Decided by the creator 2026-09-28: **dual pane on first start**, single pane one toggle away (`view.toggleDualPane`, Ctrl+Shift+D). `ui.dualPane` already defaults to `true` | Phase 5 (decided) |
| C | Brief §6 Layer 2 | "WinUI UserControls or WebView2" and "if they crash, the window must remain responsive". An in-process control cannot guarantee that | WebView2 for third-party Tool Extensions; native controls only first-party (section 2 default) | Phase 7 |
| D | Design sidebar "Tags" | File tagging is a feature, not navigation. Article 10 says features are opt-in | Tags become a first-party plugin (core plugin stores tags; UI hook draws the dot). Not in the core | Phase 7 |
| E | Design keybinding hint | Sidebar shows "Ctrl+K W"; the command list says "Ctrl+K then Ctrl+W" | Pick one when seeding the command registry | Phase 3 (done: `ctrl+k ctrl+w`, [keybindings.md](keybindings.md)) |
| F | Brief §2 "requiring Admin privileges" | Resolved by decision 2 (separate elevated indexer) | Done | — |
| G | Design: marketplace as a core view | Article 10 says zero supplementary tools, but Article 8 makes the marketplace the way to add everything else | Marketplace stays core: it is infrastructure, like the terminal (decision 5) | — |

## 5. Phases

Each phase lists its goal, what it produces, when it is done, and which
Constitution articles it serves. Phases 1–4 are core-only and testable from
the command line, before any UI exists. That is deliberate: the Dumb UI Rule
means the core must work on its own.

### Phase 0 — Repository foundation (docs and hygiene, no code) — done 2026-09-28

Goal: the repo holds every governing document and is ready for code.

Produces: `LICENSE` (MIT); `docs/ARCHITECTURE.md` (the brief, word for word); `docs/design/` (the design bundle, unzipped); `docs/decisions/0001`–`0008` (today's decisions); `.gitattributes` (fixed line endings per file type, so every machine gets the same bytes); `.editorconfig`; `.gitignore` for Rust and .NET; the monorepo folders `core/`, `ui/`, `sdk/`, `docs/`; an updated `README.md`; `CLAUDE.md` gains a section on layout, build and test commands, and the rule that the brief is living.

Done when: everything above is committed and pushed, and a fresh clone shows the same line endings on Windows and Linux.

Articles: 2 (license), 6 and 12 indirectly (documents exist to be followed).

### Phase 1 — Rust core scaffold (the first coding task) — done 2026-09-28

Goal: an empty but real core. Every crate exists with its responsibility written down, the diagnostics pipeline works, and a command-line client can talk to the core over the pipe. Detailed in section 6.

Done when: `cargo build`, `cargo test`, `cargo clippy -- -D warnings` and `cargo fmt --check` pass locally and in CI; `cabinetos-cli ping` sends a request with an ID and the same ID appears in `core.<date>.jsonl`.

Articles: 1 (non-blocking design from day one), 12 (diagnostics first).

### Phase 2 — Filesystem engine v1 — done 2026-09-28

Goal: list any directory fast, stream metadata afterwards, and notice changes.

Produces: NT-API directory enumeration (`NtQueryDirectoryFile`); the shared-memory listing format (ID, name, kind) with a versioned header; size, dates and attributes in the same pass as the names, because the kernel returns them with the listing (icon and type-name hydration moves to Phase 5); natural sort in the core, directories first; volume and physical-disk detection for a path; `ReadDirectoryChangesW` watching of open directories with `listing_refreshed` / `listing_lost` events ([ipc.md](ipc.md)); CLI `ls` that renders from shared memory; benchmarks with a 100,000-file fixture directory.

Done when: a 100,000-entry listing appears in shared memory in under 50 ms on this PC (first-cut target, to be measured and revised), and the benchmark compiles in CI (it runs locally).

Measured 2026-09-28 on this PC, warm cache, after the pipelined follow-up the creator approved: 100,000 entries reach shared memory in 46–49 ms (criterion medians of `list_and_publish` in three runs; 48–50 ms end to end through the CLI). **The follow-up's 55 ms goal is met, and the first-cut 50 ms target too in these runs, with little margin.** While the kernel fills the next buffer (about 64 KiB of names per call), one worker thread parses the previous one, builds its sort keys and sorts it; after the kernel's last call, one heap merge of the sorted runs (about 2 ms) gives the display order, and the section is written on four threads (1.4 ms instead of 3.8). The kernel's calls are the floor, about 80 % of the time, and they vary with the machine's load (the first call 4.5–10 ms, the other 169 calls 26–35 ms). The Phase 2 path stays behind `ListOptions::pipelined = false` for comparison: 60–68 ms in the same runs (75 ms when first measured). At 10,000 entries the pipelined path takes 5.2–6.2 ms against 5.3–7.4; below about 1,750 entries both paths run the same code (1,000 entries: 0.6–0.8 ms). Both write byte-identical sections (`core/crates/cabinetos-fs/tests/pipeline.rs`). The baseline `std::fs::read_dir` plus metadata, without sorting, takes 73–75 ms at 100,000 entries in these runs (61 ms in the first measurement). Showing the first screen within a few milliseconds would still need chunked publishing, which stays open design work.

Articles: 1, 5 (two independent pane sessions).

### Phase 3 — Config, commands and keybindings — done 2026-09-28

Goal: the command architecture exists before any button does.

Produces: `cabinetos.json` schema and defaults; file watcher with parse, validate, diff, and change events (Article 6 real-time sync); command registry seeded from the design's command list (id, category, name, default binding, source); chord semantics (1000 ms window) written as a spec shared by core and UI; the Immutable System Tier list (Article 7); keymap export to the UI.

Done when: editing the file by hand changes a binding in the running core within one second, and a test proves an immutable binding cannot be overridden.

Measured 2026-09-28 on this PC (debug build): a hand edit saved with `sed -i` reached a watching client 119 ms after the save; a broken save was reported as `config_error` 104 ms after it (`cabinetos-cli keys watch`). The end-to-end test `the_immutable_tier_cannot_be_rebound` proves the tier holds. Specs: [keybindings.md](keybindings.md), [config.md](config.md).

Articles: 6, 7.

### Phase 4 — Job engine (copy, move, delete) — done 2026-09-28

Goal: file operations that never block and never stall on one bad file.

Produces: `JobQueueManager` with per-physical-disk queues (HDD: one at a time; NVMe/SSD: several in parallel, detected through the storage seek-penalty property); `CopyFileExW` backend with the progress callback and unbuffered mode for huge files; progress coalesced to 30 updates per second; per-file conflict state machine (pause that file, report, continue the batch); pause, resume, cancel; move as rename on the same volume and copy-plus-delete across volumes; delete to the Recycle Bin through the shell API. IoRing is measured against `CopyFileExW` before it is adopted.

Done when: copying 10,000 small files plus one 20 GB file to the same disk shows the UI-side progress stream at 30 Hz, and an injected "file exists" conflict does not stop the other files.

Measured 2026-09-28 on this PC (E:, NVMe, release build): 10,000 files of 1–64 KiB plus one 20 GiB file copied in 9.7 s (2.3 GB/s); the progress stream peaked at 22 events per second (limit 30; Windows's 15.6 ms timer tick is why it is not closer to 30). A second run into the same destination skipped 10,001 existing files through conflicts while 1,000 new files were copied, in 1.5 s. A benchmark of `CopyFileExW` against a plain read/write loop is in [jobs.md](jobs.md); IoRing is still unmeasured. Details: [jobs.md](jobs.md).

Articles: 1, 5.

### Phase 5 — WinUI 3 shell v1 — built 2026-09-28, checked with real keys 2026-09-28

Goal: the first window a user can live in. Design views A and B.

Produces: the C# solution; process launcher and pipe client; shared-memory reader in `unsafe` C#; `CommandRouter` with the chord state machine; dual-pane grid with `ItemsRepeater` virtualization and instant single-pane toggle; breadcrumb address bar; status bar; Command Palette with fuzzy search and inline rebinding (design view B); Mica backdrop; design tokens from the handout; the first-run experience.

Done when: the core and the UI run together, a listing of 100,000 files scrolls without frame drops (measured 2026-09-29 with the snapshot aid and the display asleep: about 15 ms of UI-thread work per page of 30 rows, and 3.7 % of frames over 16.7 ms at the rhythm of a held PageDown on a 60 Hz display; the gap goal is `livecheck.ps1`'s line from a real-key run: not met on 2026-09-29, met in the default theme on 2026-09-30 once cursor keys were made at the next frame, see step 3), and every visible action goes through `CommandRouter` and a command ID.

Built 2026-09-28, and checked with real keys the same evening (below). Built: `ui/CabinetOS.sln` on .NET 10 and the Windows App SDK 2.5.1, unpackaged, using the Windows App Runtime installed on the machine. `CabinetOS.Core` holds everything that runs without a window: the pipe client, the protocol types (tested against `sdk/protocol` and `sdk/config`), the core launcher, `ListingView` (the only `unsafe` C#; it reads listings straight from the core's shared memory), keys and the chord state machine, the `CommandRouter`, and diagnostics in the core's log format with crash traces at the `frontend` boundary. `CabinetOS.exe` draws design view A (Mica title bar; back, forward and up; breadcrumbs that turn into an address box; pinned folders in the sidebar; two panes of virtualized rows with an instant single-pane toggle; a status bar that shows a waiting chord) and design view B (the palette: the core ranks the commands, each row shows its keys and chords, the Immutable System Tier shows a lock, and the pencil or F2 records new keys, which the core writes to `cabinetos.json`). Dual pane on first start (conflict B). Every button, key, crumb, sidebar row and palette row goes through `CommandRouter` with a ULID, and a folder opened by a command is listed under the same ID in the core's log. 158 tests, one of them against the real core. Measured 2026-09-28 on this PC (release builds; the screen was locked, so Windows drew about 33 frames per second): both panes shown 0.82–0.87 s after start; a folder of 100,000 entries drawn 82 ms after the command, 38 ms of it in the core; closing ends the core in 0.4 s. Measured with real keys on 2026-09-28 (release builds, unlocked screen): Enter on the 100,000-entry folder showed the listing 88 ms after the key press (65 ms of it in the core); PageDown held for 5 s drew 53–61 frames per second, with the worst frame at 78 ms and at most 4 frames over 33 ms in any second; a rebinding through the pencil wrote the new keys into `cabinetos.json` ([log/2026-09-28/live-check.md](log/2026-09-28/live-check.md)). The core's side of the rest is built (protocol version 9): the shell's commands are in the core's registry with their keys, so every one of them shows in the palette and can be rebound (`go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind`, `file.rename`, `file.delete`, `file.deletePermanently`, `file.openInOtherPane`, `file.properties`, `edit.cut`, `edit.copy`, `edit.paste`, `edit.selectAll`, `edit.toggleSelection`, `search.focus`; F2 renames in a pane and records keys in the palette), and the shell's type names and icons come from the core: `describe_entries` gives each shown row the shell's type name (by extension, no disk access) and an icon key (`folder`, `ext:.txt`, `generic`, or `path:` for programs, icons and shortcuts, whose icon is their own), and `get_icon` gives that icon as a PNG of 16, 24, 32 or 48 pixels from the system image lists. Measured 2026-09-28 on this PC (release build): 10 entries of `C:\Windows` described in 7.5 ms on the first request and 1.5–1.7 ms after; an icon in 7–26 ms the first time, then from the core's cache.

**Core additions for the UI (protocol version 8), 2026-09-28.** `list_volumes` answers every drive letter's volume as `volume_info` describes it, in letter order; the drives are asked in parallel, and one that is not ready, fails or does not answer (a network drive within 200 ms, a local one within 2 s) is left out; a `subst` letter shows its folder's volume. `volumes_changed` brings the new list when a drive letter comes or goes: a USB stick, a card, a mapped share, a `subst` letter. The core hears Windows' `WM_DEVICECHANGE` broadcasts through a top-level window that is never shown. `get_value` and `set_value` read and change one setting by its dotted path; a change is checked as a saved file is, written atomically, and announced with `config_changed`. `ui.lastPaths` and `ui.pinned` are new settings. `open_path` opens a file or folder with its default application (the shell's `open` verb); this is navigation infrastructure, and viewers and editors stay extensions (Article 10). `create_directory` and `rename` (in place, never replacing) need no job, and a taken name is the new error code `already_exists`. CLI: `volumes`, `config get` and `config set`, `open`, `mkdir`, `rename`. CI now runs only the jobs a push touches, each in its own concurrency group. Measured on this PC (release build): `list_volumes` 2–5 ms for 11 volumes, 6 of them network shares; `set_value` about 3 ms; `open_path` of a text file 281 ms until Notepad took it.

**Core additions for the shell (protocol version 11), 2026-09-28.** The window's own commands are in the core's registry, so the palette ranks them and the user can rebind them: `plugins.list`, `editor.openMarkdownPreview` (`ctrl+k v` in the files view, the design's binding), `editor.close`, `editor.reload`, `terminal.new`, `terminal.show`, `terminal.close`, `terminal.reload`, `search.scope`, the transfer panel's `transfer.pause`, `transfer.resume`, `transfer.cancel`, `transfer.minimize`, `transfer.restore`, `transfer.next`, `transfer.close`, `conflict.resolve`, and `sidebar.pin` and `sidebar.unpin` (51 commands in all then; `window.new` made it 52 on 2026-09-29). `ui.dockSize` keeps a dragged Tool Dock size across restarts (`bottom` and `right`, in pixels; `null` for the design's size). A type name the shell answers as a program identifier (`txtfile` for `.gitattributes`) shows as `{EXT} File`, as in Explorer. `job_progress` has `items_per_second`, so a delete or a move on one volume shows its pace, and a job's log lines carry the ID of the `start_job` that queued it. A plugin command run from the context menu or the palette gets `{"path", "paths"}` ([plugins.md](plugins.md)). A client that fell behind on events gets `tools_changed` again.

**The shell adopts protocol 11, 2026-09-28.** The window's own commands come from the core's registry, so the palette ranks them with everything else and each can be rebound; the window keeps only their handlers, and from the palette or a key each acts on what is in front. A plugin command from the palette or a key gets the active pane's `{"path", "paths"}`. A dragged Tool Dock size is saved once per drag in `ui.dockSize` and comes back after a restart, held to the design's limits. The transfer flyout graphs `items_per_second` for a job without bytes and says "items/s". The marketplace shows the core's one item per extension; `installedVersion` fills the Installed tab and brings "Update available" (a plugin's update is reviewed first, a theme's is not applied), and shipped themes show as there but offer no Uninstall. The `default` theme follows Windows' light or dark mode live, with Windows 11's own light colours in light mode, and the picker takes the Mica tints from `list_themes`. `.gitattributes` reads "GITATTRIBUTES File". 418 tests, seven against the real core. Checked on 2026-09-28 with snapshots (release builds, locked screen): the dock at about 330 px after a restart, the Installed tab with an update, light mode through a snapshot step (Windows' own setting was not changed). Snapshots in [log/2026-09-28/](log/2026-09-28/). Open for the creator: the light look is a first choice, since the design handout leaves light mode open. Guide: [ui.md](ui.md).

**File operations in the window (Phase 5b, design views D and E), built 2026-09-28.** F5 and F6 copy or move the selection to the other pane and F7 makes a folder: the core lists these commands but answers `not_implemented`, so the router runs them in the UI, which starts the job itself (`start_job`, under the key press's ULID). Delete goes to the Recycle Bin, Shift+Delete deletes for good after a dialog that names the count, F2 renames in place, Enter opens a file with its default application (`open_path`), Ctrl+Enter opens a folder in the other pane, Alt+Enter shows Properties from the listing's metadata, and Ctrl+X/C/V work through an in-app clipboard that also puts the paths on Windows' clipboard as text. Selection follows Windows list rules plus Total Commander's Insert; the status bar says "12 selected, 1.4 MB". The transfer flyout draws the core's numbers (title, source → destination, the 40-sample speed graph, the progress bar, Pause/Resume, Cancel/Close), folds into the status-bar pill, and cycles through several jobs; a conflict opens a card in the flyout with the decisions that fit its kind and "Apply to all of this kind". The context menu has the icon strip, the file commands, the plugins' `filesView` commands and Properties. The panes' last folders, dual/single, the sidebar and pinned folders are kept in `cabinetos.json` through `set_value`. 207 tests, three of them against the real core (200 files copied with one conflict answered Skip, which still ends `completed`; a folder made and renamed). Checked 2026-09-28 with the window's own snapshots while the screen was locked: F5 on the 100,000-entry fixture shows the flyout at 12 % after 2.5 s (items, not bytes: the fixture's files are empty); snapshots in [log/2026-09-28/](log/2026-09-28/). Checked with real keys on 2026-09-28: F7, F2, Delete and F5 with Skip confirmed on disk, the conflict card, and the context menu and Properties after a mouse click on Skip. Both findings of that check were fixed on 2026-09-29: no command runs while a dialog is open, Esc closes it wherever the keyboard is, and a decision on a conflict (also Minimize and Close) gives the keyboard back to the pane. Confirmed with real keys the same night (run 3 in [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md)): Esc closed the dialog, a key under it did nothing, and Skip through UI Automation gave the keyboard back. Guide: [ui.md](ui.md).

**The UI halves of Phases 6, 7 and 8 (Phase 5c), built 2026-09-28.** With protocol 9 the shell's own commands come from the core's registry and keymap, so every pane key can be rebound, and the Type column and the icons are the shell's (`describe_entries`, `get_icon`, asked a page of 128 rows at a time, icons at the screen's scale). The terminal (Ctrl+`, the command-bar button, "Open in Terminal") is the Tool Dock's first occupant, under the panes or beside them (`ui.layout`), behind a splitter: xterm.js 6.0 in WebView2, one tab per shell with the profiles of `terminal.profiles`, the core's byte pipes pumped at most 60 times a second, and the shown shell following the active pane after 300 ms unless a line is half typed or a full-screen program runs; a shell that ends shows its exit code for 3 s before the tab closes. The search field (Ctrl+F) sends `search` after 150 ms of quiet, limited to the active pane's folder unless "Whole volume" is on, and shows the hits in that pane with the core's source, time and completeness; Enter goes to a hit, Esc back to the folder. "Plugins: Show Plugins" lists the plugins with their capabilities and offers Review and Reload; the permissions review dialog (design view C) grants with `grant_capabilities` and opens by itself when a plugin newly waits for review. Tool Extensions run in WebView2, each in a browser process of its own and without network; Enter on a file a tool accepts opens it as the pane's editor tab (design view D). The first tool, Markdown Preview, lives in `sdk/tools` and is opt-in (Article 10). Crash isolation was checked by ending the terminal's and the preview's browser processes: the pane said so with Reload, the window went on, and Reload brought back the same shell and file. 327 tests, five against the real core. Measured 2026-09-28 (release builds, locked screen): the first Ctrl+` has a running pwsh after 0.48–0.54 s, later shells after 35–44 ms; a walk of `docs/` answered in 1.2–1.4 ms. Snapshots in [log/2026-09-28/](log/2026-09-28/). Checked with real keys on 2026-09-28: the terminal takes a keyboard's key events, Ctrl+Shift+P inside it reaches the window and Esc gives the keyboard back, the search shows the walk's incomplete answer, and Enter on readme.md opens Markdown Preview. Both findings of that check were fixed on 2026-09-29: every open loads the tool's page again, so Ctrl+K V on the file on screen and a link to another folder show the file (checked with the snapshot aid), and the terminal page types Unicode key events from their keypress; both confirmed with real keys the same night (run 3 in [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md)). Still open from that check: the pencil's tooltip outlives the palette while the mouse rests on its place. Guides: [ui.md](ui.md), [tool-extensions.md](tool-extensions.md).

**Real-key runs of the whole live check, 2026-09-29 evening.** Thirteen runs of `ui/livecheck/livecheck.ps1` with the creator at the PC, on a window rebuilt from main after each fix ([log/2026-09-28/live-check.md](log/2026-09-28/live-check.md), run 4). The first three ran a window built the evening before and count for nothing. The rest found four faults in the window, fixed the same evening: a name box shown and focused in one layout pass had its focus refused, so F7's name went to the pane as a quick search (2e2fb01); the pane's list, shown again when an editor closes, had the same refusal, so every pane key was lost after the preview closed (c1f5502); the theme picker's highlight snapped to a mouse resting over its list after every key, because the rows are built again at each change (1fa6db0); and the status bar's selection text was not reachable through UI Automation once web pages were in the window, so the window logs it ("selection shown", with 2e2fb01). The script gained as many fixes: the End key it never named, a wait for the window's "rename box shown" line instead of a fixed delay, a foreground guard by process instead of window (a flyout is a window of its own), the preview closed and the terminal hidden before the later sections, and a click into the left pane at each section's start. The last run passed every check. Open for the shell: with a Tool Extension page in the other pane, the keyboard after Ctrl+P was lost to a page in three runs, and neither Ctrl+` nor the palette chord reached the window until a click; and the count after Num *. On the development PC Alt+F1 belonged to the Claude desktop app's global hotkey until the creator freed it the same evening; a probe then showed the key opening the drive list.

**The two shell findings of run 4, fixed on 2026-09-30 (the sleep-mode run's first item).** After Num *, the status bar counted the folder under the cursor with the marks ("7 selected, 29 B" for six files). In the Windows style that row's own selection is the cursor, not a mark; Num *, Num + and Alt+Num + now drop it before they mark (`SelectionModel.HasMarks`). The keyboard was lost after Ctrl+P while the Markdown Preview was open. WinUI moves the keys into a page's browser only when XAML's focus arrives while the page's controller is visible, and a dock shown a moment before is visible to it only from the next frame. The window now checks 150 ms after each hand-over to a page (the terminal, a tool's editor tab) which window gets the keys, and hands them over again, up to three times; when a key still reaches the window instead, the window runs the page's ways out itself (`PageKeyboard`). Live check run 5 with real keys: every check True; the fault occurred in that run, and the second hand-over reached the page. The scroll goal is still not met: 29.0 % of the gaps over 20 ms, worst 47.9 ms. 650 UI tests. Commits b116346, c8215f0, 0992fd2, 83138c3; run 5 in [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md).

Decided 2026-09-28 ([ADR 0010](decisions/0010-windows-app-sdk-license-exception.md)): the Windows App SDK ships under the Microsoft Software License Terms, not an open-source license; WinUI 3 (ADR 0001) cannot be used without it, so the UI layer's dependence on it and on the Windows App Runtime is a recorded exception to Article 2, limited to Microsoft's freely redistributable platform components; the core has no such dependency.

Articles: 3, 4, 5, 7.

### Phase 6 — Indexer — done 2026-09-28

Goal: instant search over whole volumes.

Produces: MFT reader into a compact in-memory tree keyed by file reference number; USN Journal tailing to keep it fresh; the elevated `cabinetos-indexer.exe` as a Windows service with an on-demand elevated mode; read-only query pipe; core fallback when absent; search field in the UI.

Done when: the index of the system drive builds in seconds, a rename on disk appears in search results within a second, and the app works unchanged with the indexer stopped.

Done 2026-09-28: the core side, and the search field in the UI (Phase 5c). Built: `cabinetos-index`, an in-memory index keyed by file reference number, built by enumerating the MFT through `FSCTL_ENUM_USN_DATA` (the NTFS driver returns each entry's FRN, parent, name and attributes, so no on-disk structure is parsed) and kept current by one thread per volume that follows `FSCTL_READ_USN_JOURNAL`, rebuilding in the background when the journal cannot continue; names interned; ranked substring search on several threads. `cabinetos-indexer.exe` runs it elevated, in a terminal (`--console`) or as a Windows service (`--install`, manual start, LocalSystem), and answers only `ping`, `index_status` and `search`, over `\\.\pipe\cabinetos-indexer` with a medium integrity label so the normal-rights core can ask. The core answers `search` from the indexer within 200 ms, or walks one folder tree itself (at most 2 s and 20,000 entries) and says `source: walk`. Measured 2026-09-28 on the GitHub Actions runner's `C:` (release build): 1,358,009 entries in 3.3 s, 87 bytes per entry, a 3-letter query in 12.5 ms, a rename visible to search after 9 ms. On this PC with the indexer stopped, `search` answered from a walk and `index status` said `available: no`; the index of this PC's own `C:` is still to be measured in an elevated run. Decided 2026-09-28 ([ADR 0011](decisions/0011-search-without-per-user-filtering.md)): version 1 answers file names to any logged-on user of the PC regardless of folder rights, as the "Everything" tool does; a per-user filter stays possible later. Guide: [indexer.md](indexer.md).

Articles: 1.

### Phase 7 — Plugin host and Tool Dock — done 2026-09-28

Goal: the two extension layers, with strict boundaries.

Produces: `wasmtime` with the Component Model; the WIT package for the plugin world; plugin manifest with capability requests (`fs:read`, `fs:write`, `cmd:register`, `process:run`, `net`, `credentials`); the permissions review flow (design view C dialog); one store and one thread per plugin with fuel and memory limits; trap handling that logs the plugin ID, kills the instance and keeps the core alive; the Tool Dock in the UI with WebView2 hosting; one sample core plugin and one sample Tool Extension (Markdown preview, as in the design).

Done when: a deliberately crashing plugin is logged and removed while the core keeps serving listings, and the sample extension renders in a dock pane.

Done 2026-09-28: the core side, and in the UI (Phase 5c) the permissions review dialog, the Tool Dock with the terminal, and Markdown Preview as the sample Tool Extension. The sample renders in a pane's editor tab; a tool that asks for the dock opens in a pane until the dock has tabs for tools. Built: the WIT package `cabinetos:plugin@0.1.0` in `sdk/wit/`; strict `plugin.json` manifests; capabilities with levels (the list above plus `config:read`, `events:emit` and `jobs:intercept`; `process:run`, `net` and `credentials` are declared but never granted yet), granted in the `plugins` section of `cabinetos.json` and applied without a restart; one store and one thread per plugin, with fuel (5×10⁹ per call), a 5 s deadline (500 ms for `before-job`) and 256 MiB of memory; trap handling that logs the plugin ID with a named WebAssembly backtrace, drops the instance, removes its commands and keeps the core serving, with a restart after 5 s unless the plugin crashed three times in ten minutes; the Rust sample `hello` and five test fixtures, committed as built components. Measured 2026-09-28 on this PC (debug build): `crashy.crash` answered `plugin_error` at once, and the same core listed `C:\Windows` (108 entries, 1.4 ms) right after; `spinner.spin` was stopped at 5.1 s. Guide: [plugins.md](plugins.md).

Articles: 8, 10, 11.

### Phase 8 — Integrated terminal — done 2026-09-28

Goal: a shell scoped to the active pane.

Produces: ConPTY host in the core with a byte pipe per session; shell profiles (pwsh, cmd, WSL) in config; the terminal pane in the UI, hidden until `Ctrl+`` `; "cwd follows the active pane". The rendering control is xterm.js in WebView2 (section 7, question 1, settled).

Done 2026-09-28: the core side, and the terminal pane in the UI (Phase 5c). Built: the crate `cabinetos-terminal`: each shell runs in a ConPTY pseudo-console, from a profile in `cabinetos.json` read at each open, with `TERM=xterm-256color` and `CABINETOS_SESSION`; each session's raw bytes travel on a pipe of their own (`\\.\pipe\cabinetos-term-<random>`), with the control pipe's access rules and one client at a time; sessions belong to the core, so a client may leave and attach again; output waits in a 1 MiB buffer that holds the shell back when full; closing is a hang-up, and a shell still running 2 s later is ended. Protocol version 7: `terminal_open`, `terminal_resize`, `terminal_close`, `terminal_sync_cwd`, `terminal_list` and the event `terminal_exited`. "cwd follows the active pane" is `terminal_sync_cwd`: it types the shell's own change-directory command, quoted so pwsh, cmd and WSL read the path literally. `cabinetos-cli term` runs a shell in a console window (Ctrl+] detaches). Measured 2026-09-28 on the development PC (debug build): a shell starts in 17 to 24 ms and closes at its prompt in 3 to 15 ms; 2.8 MB printed while no client read arrived complete. Guide: [terminal.md](terminal.md).

Articles: 4, 9.

### Phase 9 — Marketplace and theme engine — done 2026-09-28 (publisher trust still open; the public index decided, not yet published)

Goal: install and share extensions and themes.

Produces: the JSON theme format (accent, Mica tint, palette) with live apply; a marketplace index served as static files first; install, update and remove flows; publisher trust.

Core side done 2026-09-28. Built: the JSON theme format as one Rust type (`Theme` in `cabinetos-protocol`), exported to `sdk/themes/theme.schema.json` and read strictly: `accent` (`null` follows the Windows accent), `mica` (tint and opacity, `null` for plain Mica), the palette of the design tokens, and 16 terminal colours. Four themes ship inside the core and are written into `%LOCALAPPDATA%\CabinetOS\themes` when missing: `default` (the design tokens, with the Windows accent and plain Mica), `nord`, `catppuccin-mocha` and `rose-pine-moon` (their MIT palettes with the design's accents and Mica tints). `ui.theme` names the theme in effect. Every client gets `theme_changed` with the whole theme when it changes or its file is saved. A theme that is not valid never applies: the last good theme stays, and `config_error` says why. The marketplace client `cabinetos-market` reads a static index (`sdk/marketplace/index.schema.json`) from a folder or over HTTPS (ureq on rustls, the Windows certificate store, a cache with ETag); plain `http:` only with `marketplace.allowInsecure`. Installs are checked by SHA-256 and `minCoreVersion`, unpacked in a staging folder, compared with the index, and recorded file by file, so an uninstall removes exactly those files. An installed plugin arrives as `needs_review`, even after an update. The core reaches the network only when a client asks. Protocol version 10. `sdk/marketplace/build-index.ps1` builds a local index from the fixture plugins and the shipped themes. Not built: publisher identities (`verified` is only shown), and a public index (`marketplace.index` defaults to a placeholder that cannot resolve). Checked 2026-09-28 on the development PC: `config set ui.theme nord` brought `theme_changed` with the whole theme; `market install hello` from a local index installed the plugin as `needs_review`, and `market uninstall hello` removed its two files. Decided 2026-09-28: the default theme follows the Windows accent, as built (Article 3, a native look); the design's #60CDFF stays in the shipped themes that name an accent. The public index is decided too ([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)): GitHub Pages of a separate public repository, which the creator creates; the placeholder stays until then. Guides: [themes.md](themes.md), [marketplace.md](marketplace.md).

**The UI half of Phase 9, built 2026-09-28.** Themes apply live. Every design token is one app-wide brush whose colour the theme changes in place, so `get_theme` at start and each `theme_changed` repaint the window without a restart: text, layers, controls, the Acrylic of the palette, menus and flyout, the terminal's 16 ANSI colours, the file glyphs, the two-tone folder and the capability levels. The accent becomes WinUI's too (`SystemAccentColor` and six computed shades); `accent: null` follows the Windows accent, `mica` tints a `MicaController`, and `kind: light` turns WinUI's own controls light. "Preferences: Color Theme" (Ctrl+K Ctrl+T) lists the themes with swatches and applies one with `set_value ui.theme`. The marketplace (design view C, Ctrl+Shift+X or the command bar's button) takes the main column's place: Discover, Plugins, Themes and Installed with counts, a search sent after 150 ms of quiet, the card grid and a 340 px detail column. A plugin gets the permissions review with the index's capabilities before anything is downloaded; "Allow and install" installs it and grants them, and the plugin starts. A theme installs and applies; uninstall asks first; Source opens only web pages. The empty states name `marketplace.index` and say when the index is the unpublished placeholder. The live check found that a light theme ended the window (WinUI's backdrop callback), and it was fixed. 395 tests, seven against the real core (Nord through the picker; the local index read, Hello installed, granted, active and removed). Checked live on 2026-09-28 (release builds, locked screen) with the local index of `sdk/marketplace/build-index.ps1` plus one light theme. Snapshots in [log/2026-09-28/](log/2026-09-28/). Open for the core: which extensions the marketplace installed, the Mica tint in `list_themes`, and a light or system default theme. Guide: [ui.md](ui.md), "Themes" and "The marketplace". A self-review pass over the whole shell followed (412 tests): a file probe moved off the UI thread, keyboard focus handed over before any overlay collapses, stale theme colours in open dialogs, conflicts for ended jobs, Esc for the transfer flyout, and the palette's "+N" keys.

**Additions (protocol version 11), 2026-09-28.** A client is offered one marketplace item per extension, the newest version this core can run, each with `installedVersion` from the marketplace's record of installs. `list_themes` carries each theme's Mica tint. A theme's `kind` may be `system` (follow Windows' light or dark mode); the shipped `default` theme has it (version 1.1.0).

**Theme collection, 2026-09-29.** The creator's card "Develop 30 most world popular theme for CabinetOS" is answered outside the core (Article 10): `sdk/themes/collection` holds 36 themes (27 dark, 9 light) ported from 27 of the 30 themes on the creator's list, with their well-known variants; Nord and Catppuccin Mocha already ship with the core. Every colour was read from the theme's own repository at a fixed commit under the MIT License; `NOTICES.md` names the source, author, license and commit of each. Monokai is the classic scheme Visual Studio Code ships (Monokai Pro is commercial), and Darcula comes from an MIT community port. City Lights (its CC BY-NC-ND license forbids ports) and Dainty (its colours are no longer published) are left out. Every theme reaches 4.5:1 for body text as the window draws it; the collection's README lists where a theme's own text colour had to change. `build-index.ps1 -Collection` packs the collection into the local index (without the switch the index is unchanged), and every theme installed, applied and uninstalled through the marketplace against the real core; the planning session checked that the core lists all 40 and applies them. A test in `cabinetos-themes` now holds the collection to its rules with the five checks: every file parses as a strict theme under its own name, none repeats a shipped theme, and `marketplace.csv` has exactly one row, with a license and a description, per file. It goes with the public index once that exists (ADR 0012). Snapshots: [log/2026-09-28/](log/2026-09-28/) `themes-*.png`. Guide: [themes.md](themes.md), "The collection".

**The public index, 2026-09-29 evening.** The creator created the public
repository `OliverD25/cabinetos-marketplace` ([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md))
and chose a first index of themes only: the test fixture plugins are not
for the public. `build-index.ps1` gained `-ThemesOnly` (fef2e58), and the
planning session built the index (41 themes: the five shipped and the 36
of the collection), added the collection's notices, the MIT license and a
README, and pushed it (167256f in that repository). Turning on GitHub Pages
is the creator's switch; the default address changes in the sleep-mode run
of 2026-09-29/30, the core agent's first item, and is checked against the
live address after the switch.
Done 2026-09-30 (349a59f): the creator turned Pages on at about 00:05,
the address answered thirty seconds later (41 items, an ETag, a ten-minute
cache rule), and the default changed to it. Checked in the planning
worktree from a fresh core with an empty configuration: `market refresh`
listed the 41 items, `market search github` found the two GitHub themes,
`market install dracula` downloaded 1.55 KiB, checked its SHA-256 and
wrote the theme, and `market uninstall dracula` removed exactly that file.

**Commander Compact, the core's part, 2026-09-29.** The creator's Commander Compact handout, now in `docs/design/compact/`, is a density preset of the default look: 20 px rows, hairlines instead of cards, striped lists and an F-key bar, in the same colours. Its rule is the format's rule: a theme may change colours, sizes and the presence of chrome elements, never commands, keys or the layout. The theme format gains two optional objects: `metrics`, 76 named sizes (one for every value of the handout's Metrics section, with bounds: text 8 to 32 px, rows and bars 14 to 80 px, and so on), and `chrome` (`fkeyBar`, `rowStripes`, `hairlines`). Reading stays strict: an unknown name, a value outside its bounds or pixels with a fraction refuse the theme with the reason. `get_theme` and `theme_changed` carry both objects as the file has them, and `list_themes` says `has_metrics`. All of it is optional, so protocol 11 stays; the schema's `$id` names the format, `urn:cabinetos:theme:2`. `commander-compact` ships as the fifth theme: the default theme's colours, the handout's acrylic tint, every metric and all three chrome elements; `cabinetos-cli themes list` marks it `compact`. The shell applies the objects in its half. Two of its end-to-end tests pin four shipped themes and wait for that half. 564 core tests.

**Commander Compact, the shell's part, 2026-09-29.** The window applies a theme's `metrics` and `chrome` live, as it does its colours. One table in `CabinetOS.Core` (`MetricsMapper`) names all 76 metrics with their units, bounds and the default look's values, and a test holds it equal to the schema; every view lays itself out from the metrics in effect, a `theme_changed` re-lays the window without a restart, and a theme without metrics gives the default look's sizes, so switching back to `default` restores every size number for number (the two images differ in 80 of 3.24 million pixels, all on the edges of rounded corners). The three chrome elements sit behind their switches: the function-key bar (its own control, never a Tab stop, each button running the command its key runs and showing the key the keymap has), striped rows, and hairlines instead of floating cards. On hairlines ride the handout's narrow-column choices (the Size column in fixed-width figures, a drive's free space without "free", dates older than a week without their time, short type names), and the status bar's layout item starts with the density preset's name; the picker marks density presets from `has_metrics`. The base text of the default look is now the design's 13 px (it was WinUI's 14 px). Measured on 2026-09-29 with the snapshot aid's new `size:`, `fit:`, `theme:` and `layout:` steps on folders shaped like the handout's page: at a 924 px window no name, date, type or size is cut short in either pane and every function key fits; 20 whole rows of 20 px in a 400 px list; no corner radius above 3 px outside the overlays; the keymap's 67 bindings unchanged; F5 pressed through the bar's automation peer copied the file. Two of the text choices go beyond the handout's list because the acceptance failed without them (the short dates and types); two things differ from its page and cannot follow it (the title bar is 32 px, not 30, since Windows draws the caption buttons that high; the fonts are Segoe UI Variable and Cascadia Mono where Fira Code is not installed). Eight metrics have no place in the window yet (the caption buttons' width, tags, the Markdown Preview's sizes, a hex view). Snapshots at half size are in `docs/log/2026-09-28/compact-*.png`, and `docs/ui.md` "Metrics and chrome" says where each metric lands. The shell agent's session ended on the account's model limit right after its last push, before its own test run; the planning session ran it (Debug and Release builds clean, 631 UI tests with the two-window test on). Checked with real keys on 2026-09-29 (run 4 of the live check, [log/2026-09-28/live-check.md](log/2026-09-28/live-check.md)): the picker's keys switched to the preset, the window logged 20 px rows with the function-key bar, stripes and hairlines, Tab never landed on a function key, F5 through the bar's button copied the file, and the picker's keys switched back to 30 px rows.

**Two small items after the collection, 2026-09-29.** Text on accent fills (`CbOnAccentBrush`) was a fixed near-black, below 4.5:1 on the dark accents of light themes (GitHub Light 3.64:1, Catppuccin Latte 3.49:1); the mapper now picks near-black or white by which contrasts more with the accent in effect, and a test pins both cases. `CABINETOS_WEBVIEW2_DIR` moves the WebView2 user-data folder of the terminal and the tool pages; the two-window test and the live checks set it to their scratch folder. Done by the planning session, since the shell agent's session had ended. 633 UI tests.

Articles: 2, 8.

### Phase 10 — Packaging and release — buildable parts done 2026-09-28 (signing and publishing wait for the creator)

Goal: a stranger can install it.

Produces: MSIX or unpackaged decision; `winget` manifest; code signing; the indexer service installer step; release notes; the repo goes public.

Built 2026-09-28; nothing published or signed. Unpackaged for version 1 ([ADR 0009](decisions/0009-packaging.md)): one folder, zipped, with a script installer; MSIX waits until the indexer can ship as an optional component. `build/release.ps1` (PowerShell 7) builds everything with one command: the three Rust programs, which now carry the C runtime inside them because a clean Windows 11 has no `VCRUNTIME140.dll`; the window, published framework-dependent on .NET 10 and the Windows App Runtime with `-p:EnableMsixTooling=true` (without it the published window cannot find its compiled XAML and stops at start); the programs' `.pdb` files; the four themes and Markdown Preview under `extras\` (opt-in); `LICENSE`; `THIRD-PARTY-NOTICES.md` (271 components, every license text); `install.ps1`, `uninstall.ps1` and `release.json`; then the zip, its SHA-256 and the winget manifests (a portable zip, accepted by `winget validate`). The version comes from `core/Cargo.toml` alone. `install.ps1` checks Windows 11 22H2+, the .NET 10 runtime, the Windows App Runtime 2.5.1+ and WebView2, and prints the install command for each missing one. It installs per user without elevation, or with `-AllUsers` into Program Files. The Start Menu shortcut, the PATH entry and the indexer service are opt-in. The service needs `-AllUsers`, because a LocalSystem service must not run a program the user can replace. `uninstall.ps1` removes exactly what the install recorded and keeps the data unless `-RemoveData`. A manual CI job builds the zip as an artifact; it has not run (GitHub Actions starts no jobs for this account). `CHANGELOG.md` lists the first version's capabilities. Measured 2026-09-28 on this PC: a 77 MB zip (83 files, 246 MB unpacked; 38 MB of the zip are Rust symbols, 17 MB Windows App SDK AI libraries CabinetOS does not use). Installed from the zip into `%TEMP%` in Windows PowerShell 5.1 and PowerShell 7, the window started its own core (protocol 11, version 0.1.0), and uninstall left nothing behind. Checked again on 2026-09-29 with a real per-user install from the zip on the development PC: `install.ps1 -StartMenu` placed the 83 files and the Start Menu shortcut, the installed window passed the whole real-key live check with the core found next to it, `uninstall.ps1` left nothing behind, and the scripted installer cases (an update over an existing install, six refusals, `-RemoveData`) all behaved ([log/2026-09-28/install-check.md](log/2026-09-28/install-check.md)). Still open for the creator: a code-signing certificate, making the repository public, `gh release create`, the winget submission (which waits until winget carries Windows App Runtime 2.5), and whether the `.pdb` files stay in the zip or move to a symbols zip of their own. Guide: [release.md](release.md).

**The shell's part of Phase 10, 2026-09-29.** The window references only the Windows App SDK components it uses (WinUI, Foundation, InteractiveExperiences and Runtime) instead of the metapackage, whose AI, ML, Search, Widgets and DWriteCore components it never touches (Article 10). The published window went from 60 files and 80.5 MiB to 45 files and 39.8 MiB (28.1 to 11.2 MB zipped), without onnxruntime.dll and DirectML.dll. The project keeps the Windows App SDK's MSIX tooling on for its resource step, so a plain `dotnet publish` carries `CabinetOS.pri` and the published window starts; `build/release.ps1` no longer needs `-p:EnableMsixTooling=true` and still checks for the file. "Help: About CabinetOS" shows the version, the core's version and protocol, the commit and build time from `release.json` (or "Development build"), and opens `LICENSE` and `THIRD-PARTY-NOTICES.md` with `open_path`; Esc closes it and nothing runs under it. The pencil's tooltip can no longer open after the palette closed (the last live-check finding). 429 UI tests. Guide: [ui.md](ui.md), "The solution" and "About".

**Self-review (core), 2026-09-29.** A reviewer's pass over the core crates, looking for untested edge cases, error paths that mislabel or swallow, resources not released, and docs that promise more than the code does. Six fixes, each with a test that failed before it. A listing's shared-memory handle is now given to the window only in the step that announces it: before, a refresh cancelled when a changing folder was closed left a handle, and its memory, in the window until it exited. A panic on any thread of the core now stops it (exit 1, the window starts a new core): before, a panic on a watcher's or a plugin's thread ended only that thread, and the core ran on without it and without its log file, which the crash hook closes. A cached marketplace index that cannot be read is fetched again: before, a half-written cache and the server's 304 kept every refresh failing. A folder in place of `cabinetos.json` is reported as a folder, once, not as "Access is denied". A window that leaves in the middle of a frame is logged as leaving, not as a malformed frame. A pause a client asks for is no longer undone by the decision on a full disk. The docs of the search walk now say that its 2 s limit is checked between folders, so one slow network folder can hold it longer. Also built: `install_finished` names the version installed after it (`installed_version`, optional, still protocol 11, with the rule for what raises the version written in ipc.md); `help.about` is a command of the window, for its About view; an unedited copy of a shipped theme follows the version the core ships (`.shipped.json` records what the core wrote; an edited copy is kept, and `default` 1.0.0 now becomes 1.1.0); the CLI shows items per second for a job that moves no bytes. CI runs only on demand and for pull requests, since the free minutes for September are used up: every change is verified on the development PC, and CI runs once per accepted phase with `gh workflow run ci.yml`; it also parses the `build/` scripts in both PowerShells. 496 core tests.

**Hardening, step 2: edge cases in the shell, 2026-09-29.** The shell went through five classes of edge cases on a shared fixture (`sdk/fixtures/edge-fixture.ps1`), tests first, one commit per class. Names beyond ASCII: a name is found exactly before it is found in another case (a case-sensitive folder holds `Report.txt` and `report.txt`), right-to-left names keep their extension on the right, F2 selects the stem without splitting a surrogate pair, and Properties names the type the Type column shows. Long paths: the crumbs keep the drive, a "…" with a menu of the folders left out, and the last folders that fit; the pane header's path and the transfer flyout's line keep the drive and the last names; a file whose path has more than 259 characters is not offered to a tool, since WebView2 cannot read it, and the status bar says why; a delete to the Recycle Bin that the core stops at a long path offers Delete permanently. Links and cloud files: a link keeps a badge on its icon and names its kind in the Type column and Properties ("Link to a folder" until the listing names junctions, symbolic links and mount points), Shift+Delete on a link says that only the link goes, and a row whose data is not on this disk shows a cloud and is never read to draw its icon. Two windows: `window.new` starts another window with its own core at the active folder; a setting one window writes reaches the other through `config_changed`, which follows and writes nothing back; closing one window leaves the other; and the log writer now appends atomically (two windows had kept 3,000 of 6,000 lines). The fixture runs through the snapshot aid in `ui/livecheck/edge-snapshots.ps1` (every check passed; the files behind a deleted junction stayed) and with real keys in `ui/livecheck/livecheck.ps1` (run 4 on 2026-09-29: F2 with a Cyrillic name renamed the file, the search took a Cyrillic query, the long path opened and the status bar said why the preview cannot show its file, and Shift+Delete on the junction removed the link and left the files behind it). 485 UI tests, one of them (two real windows) opt-in with `CABINETOS_UI_E2E=1`. Snapshots in [log/2026-09-28/](log/2026-09-28/).

**Hardening, step 2: edge cases in the core, 2026-09-29.** Five classes of edge cases, tests first, one commit per class, on the fixture the shell shares (`sdk/fixtures/edge-fixture.ps1`). Names beyond ASCII: search (the index and the core's walk) folds case and normal form, so `café` finds both spellings; a `cabinetos.json` saved as UTF-16 is read; a conflict names the file already there as the folder spells it; the listing keeps every name unit for unit in Explorer's order, with ties ordered by their units. Long paths: every file call takes any length, and answers keep the user's form; a delete to the Recycle Bin of an item with a path of 260 characters or more stops at a `path_too_long` conflict before the shell, whose silent flags could make it final, and `delete_permanently` answers it; `open_path` says when the shell refuses such a path. Links: the listing names junctions, symbolic links and mount points (`FLAG_JUNCTION`, `FLAG_SYMBOLIC_LINK`, `FLAG_MOUNT_POINT`, and the reparse tag in the field that was reserved); a move that follows links no longer deletes the files a link leads to; a symbolic link to a file copies as a link under Developer Mode; deleting a link never touches its target, and neither the walk nor the index enters one. Cloud files: `FLAG_NOT_ON_DISK` marks a file whose data is elsewhere, and its icon comes from its extension, so showing it never downloads it. Two cores on one configuration: a lock file next to `cabinetos.json` and next to `installed.json` keeps two windows from writing each other's changes away; themes, logs, the indexer and a crash of one core were already safe, and now tested. `window.new` (Ctrl+N) is in the registry. Additive: protocol version 11. 554 core tests.

**Hardening, step 3: scrolling, 2026-09-29.** The shell measures a scroll without real keys. With `CABINETOS_UI_FRAMESTATS=1` it times each part of the UI thread's work (the rows' layout, binding rows, type-name pages, icons, selection marks, the status bar), and the snapshot aid's `scroll:<pages>` presses PageDown 30 times a second and logs the frame table with the CPU load; `ui/livecheck/scroll-bench.ps1` repeats the run and names the best and worst of three. On the 100,000-entry bench folder the UI-thread work fell from 576-731 ms to 436-439 ms per second of scrolling, about 15 ms per page of 30 rows, because a row now sets only the values that changed (WinUI lays a text out again even when it gets the same string); icon PNGs are decoded off the UI thread. At one press every second frame, the rhythm of 30 presses a second on a 60 Hz display, 3.7 % of the frames' work passed 16.7 ms (36.5 % before). The first PageDown in a new window makes the rows, one frame of 35-50 ms. What remains is WinUI's own cost: row recycling, text layout and drawing. The gap goal (no frame over 33 ms, under 5 % over 20 ms) could not be measured at night, because a sleeping display paces frames at 33 a second; `ui/livecheck/livecheck.ps1` prints it as one line from a real-key run, and `-Strict` makes a miss fail. Measured with real keys on 2026-09-29 at 21:26 (unlocked screen; the machine's load was not recorded, because the script stopped right after the hold on a PowerShell 5.1 difference, fixed in 1c4cc52): PageDown held 5 s drew 59–61 frames per second, with 28.8 % of the gaps over 20 ms, 3 gaps over 33 ms and the worst gap 42.9 ms. The frame count holds; the gap goal is not met yet. Twelve more runs the same evening, on a machine at 9 to 19 % CPU, gave 28.7 to 29.9 % of the gaps over 20 ms, 2 to 6 gaps over 33 ms in 5 s and worst gaps of 46 to 53 ms, with 59 to 61 frames a second each time. Details: [ui.md](ui.md), "Scrolling".

**Step 3 resolved, 2026-09-30 (the sleep-mode run's last item; 61f044d,
58fa975, c5ed4c8).** Measured with real keys by a new probe
(`ui/livecheck/scroll-keys.ps1`, which prints the frame table, each
thread's CPU time, the desktop window manager's and the GPU's share, and
the slow frames). The cause was timing, not the amount of work: a key
message arrives at any moment between two frames, the pane moved the
cursor at once, and WinUI then laid out the new page for 13 to 15 ms
outside a frame, so the next frame waited for it. Three changes, with
nothing different on screen: cursor keys are queued and made at the
start of the next frame (`PendingCursorKeys`; anything else that reads
the cursor makes the waiting keys first, so actions keep the keys'
order); rows are bound in `ElementPrepared`, since the old
`DataContextChanged` handler made WinUI ask .NET for a full garbage
collection every 2 to 3 s of scrolling; and rows are made ahead at idle,
in 4 ms low-priority slices (`RowFactory`). Result in the default theme:
0 of 1,207 frames over 20 ms in a 20 s hold, worst 17.8 ms; before, 27 to
32 % of the gaps over 20 ms and worst frames of 45 to 51 ms. The frame
stats now carry the collector's pauses, and a `slow frame` line names
the cause of any frame 33 ms or more late. Each change alone did little
(one at a time: 0.3 to 1.0 %, 29.2 %, 29.0 %); together they meet the
goal. Also measured, not committed: overscan 0.5 saves 8 % of the work,
the type column 16 %, plain rows 17 %; a fixed-height layout is worse;
the collector's low-latency mode changes nothing. The live check still
prints "met: no" for one frame: a blocking collection of 20 to 25 ms
that WinUI asks for, which sometimes lands 350 ms after the last press,
and the script's table counts the per-second line that ends up to a
second after the hold. Open: that collection's trigger (an ETW trace
needs an elevated prompt, the creator's); Commander Compact at 11.6 to
18.8 % (overscan 0.5 gave 1.0 to 6.3 %, held back until fast scroll-bar
drags are checked for empty rows) and its first PageDown of 60 to 77 ms;
and whether the live check's table should end at the last press, the
creator's decision, since it is the measure the goal is judged by. 848
UI tests. Report: [log/2026-09-30/scroll-gaps.md](log/2026-09-30/scroll-gaps.md).

Articles: 2, 3.

### Phase 11 — Total Commander parity (proposed 2026-09-29)

Goal: a Total Commander user finds the features and keys they rely on, each
one in the layer the Constitution gives it. The research behind this phase,
feature by feature and key by key, is
[research/total-commander.md](research/total-commander.md). The opt-in rule
(Article 10) decides every item. Navigation, selection, keys and jobs go into
the core. Viewers, archives, remote servers, batch renaming, syncing,
splitting and text search become extensions in the marketplace, and the core
only gets the hooks they need (brief §1). The sub-phases go from small to
large. Each one starts only when the creator says so; 11a started on
2026-09-29 on the creator's desk card "List of most used Total Commander
Shortcuts" ("check what we lack, then do them"). The research note ends with
ten questions only the creator can answer (Ctrl+B, folder tabs, the default
marking rules, Windows property columns in the core, text search as plugin or
core, a "Commander pack" at first start, `net` grants for one server, the
public marketplace repository, the order of 11f, and a copy dialog before F5).

#### 11a — Keys and small commands (small: about a day per item)

Covers features 1, 2, 8, 13, 16 and 17 of the research note, and the F3 and
Alt+F7 keys of features 6 and 10.

Produces: in the core, keypad keys in the key grammar (`numpadadd`,
`numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal`); several
default keys per command in the registry seed; the aliases and new commands
of the note's list "Small keys and commands to add now"; the requests
`create_file`, `edit_path` (with the setting `files.editor`),
`show_properties`, `measure_paths` (with its events and cancel),
`match_entries` and `terminal_type_paths`; the sort key `extension`; the
setting `panes.selection` (`windows` or `commander`). In the shell: the
handlers, a drive list under each pane (Alt+F1, Alt+F2), the pattern box, the
list of pinned folders (Ctrl+D), quick search by typing in a pane, and
measured folder sizes in the Size column. In the marketplace: nothing.

Done when: every key on the desk card "List of most used Total Commander
Shortcuts" either works or names the sub-phase that brings it; the grammar,
registry and keymap tests cover the new keys (no conflict, no chord prefix,
the Immutable System Tier untouched); the UI tests cover the keypad key
names, the commander selection mode, invert and restore; and a live check
with real keys presses F8, Shift+F8, Num +, Num *, Space on a folder,
Alt+Shift+Enter, Ctrl+\, Alt+F1, Ctrl+U, F3, F4, Shift+F4 and Ctrl+P.

Articles: 1, 5, 7, 9.

**11a, the core's part, 2026-09-29.** The key grammar has the keypad's operators (`numpadadd`, `numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal`, and VS Code's `numpad_add` names), and a command may have several default keys; the palette's pencil still replaces all of them, and the Immutable System Tier keeps one key each. The seed has Total Commander's second keys (F8, Shift+F8, Shift+F6, Alt+F7, Ctrl+Num +) and the 31 commands N1 to N31, run by the window: 83 commands in all. None of the new keys was taken, so no fallback key was needed. `cabinetos.json` has `panes.selection` (`windows` or `commander`) and `files.editor`. The protocol is version 12: the sort key `extension` (files by extension, then by name; folders keep name order), `create_file`, `edit_path` (`files.editor`, else the type's `edit` verb, else Notepad; it never runs the file), `show_properties` (Windows' own sheet for one path or several), `measure_paths` with progress, finish and cancel (the Recycle Bin check's walk, moved into `cabinetos-fs`), `match_entries` (Total Commander's patterns, answered as ranges, and quick search with `first_from`), and `terminal_type_paths` (quoted for each shell, without Enter). `open_path` now gives a console program (a batch file, a script, a console `.exe`) a console window of its own, as Explorer does; before, it ran in the core's console, which has no window. The CLI has `mkfile`, `edit`, `props`, `measure`, `match` and `term type`. Measured in release on the development PC: sorting 100,000 entries by extension takes 50 ms against 41 ms by name; measuring the flat 100,000-entry bench folder takes 65 ms, and `C:\Windows\System32` (30,316 files, 1,878 folders) 215 ms with 6 progress events; matching `*.txt;*.md|readme*` on 100,000 names takes 43 ms. `edit_path` checks the path before it looks for the editor, so a folder is `invalid_path` and a missing file `not_found` even when `files.editor` names a program that is found nowhere. Left for the real-key run: whether Windows' property sheet comes to the front after the window's `AllowSetForegroundWindow`. 610 core tests.

**11a, the shell's part, 2026-09-29.** The window runs Total Commander's small commands through the core's protocol 12: Ctrl+\ to the root; Alt+F1 and Alt+F2 open a drive list under that pane, where a letter picks a drive and goes to the folder the pane last showed there; Ctrl+Left and Ctrl+Right show a folder in a pane; Ctrl+D lists the pinned folders; Ctrl+U swaps the panes with their marks and history; Ctrl+R lists again; Ctrl+F3 to Ctrl+F6 give a pane its own order. Marking: Space (a folder it marks is measured), Num + and Num − with a pattern box in the palette's frame (the last ten patterns, files only unless "Include folders"), Alt+Num + and Alt+Num − for the same extension, Num * to invert, Ctrl+Num − to unmark, Num / to restore what a file command or an unmark cleared; `panes.selection: commander` marks as Total Commander does. F3 shows a file in a Tool Extension and never runs it; F4 edits; Shift+F4 names, creates and edits a text file; Windows' own property sheet opens from the palette and from Properties; Shift+Alt+Enter measures every folder, and the Size column counts up, then shows the total; Ctrl+P and Ctrl+Shift+Enter type paths into the terminal; letters typed in a pane are a quick search; the keypad's operators are keys. The window reads no disk and scans no names for any of it. 614 UI tests (one opt-in), four of them new end-to-end tests against the real core; checked with real keys on 2026-09-29 (run 4 of the live check): every command of the list ran from its key, with the marks and the measured sizes in the status bar and the files on disk. During that run Alt+F1 was taken by the Claude desktop app's global hotkey on the development PC, so the drive list was opened from the palette; the creator freed the key the same evening, and a probe then showed Alt+F1 opening the drive list from the key. One observation for the shell: after Num * the status bar counts the folder under the cursor with the six marked files ("7 selected, 29 B").

#### 11b — Navigation depth (medium)

Covers features 12 (branch view), 14 (folder tabs), 11 (the transfer
queue), 13 (files to and from Explorer), 7 (comparing the two panes), 17
(quick filter), 10 (search hits as a listing), and the optional parts of 1
(the copy dialog) and 2 (the function-key bar). Folder tabs (feature 14)
moved to Phase 12 on 2026-09-29, on the creator's card.

Produces: in the core, listings built from a tree (branch view) and from a
list of paths (search hits), with a folder for each entry, which changes the
section layout's version; `compare_listings` for the two open panes;
`list_directory` with a name filter; `start_job` with `new_name` for a single
source, `options.max_bytes_per_second`, and the `job_control` actions
`set_speed_limit` and `move_to_front`. In the shell: a tab strip per pane
(Ctrl+T, Ctrl+W, Ctrl+Tab, locked tabs) saved as `ui.tabs`; branch view
(Ctrl+K Ctrl+B); the quick filter (Ctrl+S); file commands on search hits;
Shift+F2 marking the differences; the list of all jobs with speed limits;
the copy dialog behind `panes.confirmTransfers` (off by default); the
function-key bar behind `ui.functionKeys` (off by default); the Windows
clipboard's file list (`CF_HDROP`) in both directions, and drag and drop.
In the marketplace: nothing.

Done when: tabs and their locks come back after a restart; a branch view of
the 100,000-entry benchmark folder and of a deep tree shows with a Folder
column, and F5 copies from it; F5 copies a search hit; a copy with a speed
limit stays within 5 % of the limit (first-cut target, to be measured);
files copied in Explorer paste in CabinetOS, and the other way; Shift+F2
marks exactly the rows that differ in a prepared pair of folders.

Articles: 1, 4, 5, 6.

#### 11c — Columns, search filters and settings (medium)

Covers features 15 (the column framework and Windows property columns), 10
(search filters), 19 (settings view, fonts, command bar, user commands) and
16 (tasks for `terminal.runTask`).

Produces: in the core, `panes.columns` and sorting by any field of the
listing; Windows property fields in `describe_entries`, off by default; a
search query syntax with wildcards, regular expressions, and size, date and
attribute filters, on the index and on the walk; user commands from
`userCommands` in the registry; the settings the settings view needs. In the
shell: live column headers and a column chooser; the search filter flyout
(Alt+F7 opens it); the graphical settings view (Article 6); tasks for
`terminal.runTask`. In the marketplace: nothing.

Done when: a column added in the view appears in `cabinetos.json`, and one
added in the file appears in the view within a second; a click on a header
sorts by it; a search for `*.log size:>1mb modified:2026-09` answers from the
index and from the walk; a user command appears in the palette, takes a key
and runs with the selected paths.

Articles: 1, 3, 4, 6, 7, 9.

#### 11d — Extension platform, version 0.2 (medium to large)

Covers the core hooks of features 3, 6, 7, 9, 10, 15 and 18. The features
themselves come in 11e.

Produces: in the core, the plugin interface `cabinetos:plugin@0.2.0` with
per-request file grants (`fs:selection`), plugin work run as core jobs
(progress, pause, cancel), a content-field hook and a quick-input host
function; the `rename` job kind with an undo list; `compare_trees`; commands
in `tool.json`, tools opened without a file, and plans (renames, copies,
deletes) that a tool hands to the window, which asks the user and starts the
jobs; `open` with two files; the range-request spike, and a byte-range read
for tools if the spike needs it; marketplace packs. In the SDK: a template
and a documented sample for each hook. In the marketplace: nothing yet; the
samples are test fixtures.

Done when: a fixture plugin and a fixture tool prove each hook; a plugin is
refused a file it was not given; a 20 GB plan runs as a job and pauses and
cancels; a crashing plugin inside a job leaves the core serving listings.

Articles: 8, 10, 11.

#### 11e — First-party opt-in extensions (medium each)

Covers features 3, 6, 7, 10, 15 and 18.

Produces: in the marketplace only, nothing in the core, a viewer pack (text,
hex on Ctrl+K H, image, media, and Quick View on Ctrl+Q); Multi-Rename
(Ctrl+M); Sync and Text Compare; Split and Combine with checksum files; Text
Search; Media Fields (EXIF and MP3 tags); and a "Commander pack" that
installs them together.

Needs: the public marketplace index (ADR 0012), which waits for the creator
to create its repository. Until then the extensions install from a local
index (`sdk/marketplace/build-index.ps1`).

Done when: each extension installs from the index, passes its permissions
review, does its job, and uninstalls without leaving files; the core's size
and start time do not change with them installed.

Articles: 2, 8, 10, 11.

#### 11f — Virtual file systems: archives and servers (large: several phases)

Covers features 4 (archives), 5 (FTP and SFTP), the Packer and File System
kinds of feature 9, and search inside archives (feature 10).

Produces: in the core, the virtual file system hook (`vfs:provide`), whose
listings use the shared-memory format and whose paths lead into archives and
servers; jobs that read and write through a provider; running a file from an
archive through a temporary copy; dragging files in and out; `net` grants
narrowed to one server; passwords in Windows Credential Manager through the
core. In the marketplace: Archives (zip, 7z, the tar family; Alt+F5 packs,
Alt+F9 unpacks), and FTP, FTPS and SFTP, after an SSH spike.

Done when: a 10 GB archive lists, and extracts through a job that pauses and
resumes; a crashing provider leaves the core serving; an FTP plugin granted
one server cannot reach another address.

Articles: 1, 8, 10, 11.

Not in Phase 11: the Android app (feature 20; Articles 1 and 3), and Total
Commander's own plugin DLLs, which are native code that cannot be sandboxed
(Article 8).

### Phase 12 — Tabs per pane (the creator's card "Per Pane Tabs Design", 2026-09-29; started in the sleep-mode run of 2026-09-29/30)

Goal: each pane has its own row of tabs, hidden while the pane has one tab
(Article 4), so a user keeps several folders and tools open side by side
without a second window.

Decided 2026-09-29 with the creator, against the card's draft where they
differ: Tab stays the pane switch, and Alt+Left and Alt+Right keep Back and
Forward; the window owns the tab state and saves it as `ui.tabs`
(Article 6), and reports it to the core on each change so a command-line or
AI tool can ask for it (`window_state`, Phase 14); no Acrylic dimming of the
inactive pane, focus shows as an accent line over the active tab and the
active-pane header (Article 1, the drawing cost is already why the scroll
goal is missed); a tab may hold a Tool Extension as well as a folder. This
takes feature 14 (folder tabs) out of 11b.

Produces: in the core, the `ui.tabs` field of the config (per pane a list of
`{path, locked}` and the active index) and the `window_state` request. In
the shell, a tab row above each pane's list (`TabView`, or `ItemsRepeater`
if `TabView` costs frames on the bench folder) with `tab.new` (Ctrl+T),
`tab.close` (Ctrl+W), `tab.next` (Ctrl+Tab), `tab.previous`
(Ctrl+Shift+Tab), `tab.toggleLock`, `tab.openFolderInNewTab` (Ctrl+Up) and
`tab.moveToOtherPane` (Ctrl+K Ctrl+Right, Ctrl+K Ctrl+Left); each tab keeps
its folder, history, sort and lock; in a locked tab, going into a folder
opens a new tab, as in Total Commander; a tool's editor tab is a tab of the
same row; the row is collapsed with one tab; the tabs come back after a
restart from `ui.tabs`.

Done when: five tabs on the left and three on the right open independently;
closing all but one tab hides that pane's row; Tab switches panes with the
visual focus moving at once; F5 copies from the active tab of the active
pane to the active tab of the other pane; tabs and locks come back after a
restart; a scroll on the bench folder is no slower than before (frame
stats); a live-check section covers the keys.

Articles: 4, 5, 6, 7, 11.

**Built in the sleep-mode run of 2026-09-29/30 (77c1169 the core's
`ui.tabs`; 725328f, 4881f82, 3fec822, 07b5e8b, 837361a, cf08875,
80c204b the shell).** Each pane has a row of tabs above its list: a WinUI
`TabView`, `tabRow` high (32 px, 26 px in Commander Compact), hidden while
the pane has one tab. A tab holds a folder with its own history, order,
cursor, marks and lock, or a Tool Extension (the Markdown Preview opens as
a tab beside the folder's). The pane's model stays the live state of the
tab in front; a tab behind parks its state and lists its folder again
when it comes back, so a hidden tab does not refresh itself. The front
tab of the active pane has a 2 px accent line, and nothing is dimmed.
`tab.new` (Ctrl+T), `tab.close` (Ctrl+W), `tab.next` and `tab.previous`
(Ctrl+Tab, Ctrl+Shift+Tab), `tab.toggleLock` (palette only),
`tab.openFolderInNewTab` (Ctrl+Up) and `tab.moveToOtherPane` (Ctrl+K
Ctrl+Right, Ctrl+K Ctrl+Left, the second key naming the side) are in the
registry under "Tab", 90 commands now. In a locked tab, going into a
folder or up opens a new tab and Back and Forward refuse; the last folder
tab of a pane cannot be closed; a tool tab has no lock and does not move,
and swapping the panes refuses while one is open, since a tool's process
belongs to its pane. The window saves `ui.tabs` (at most once a second,
and at close; `ui.lastPaths` is still written) and sends `window_state` on
every change of tabs, active pane, cursor or marks, joined to one message
per 50 ms with the marks capped at 1,000 paths. Tool tabs are not saved,
since the config's schema holds `{path, locked}` and a tool's file may be
gone. Measured on the 100,000-entry folder (`scroll-bench.ps1`, release
builds, display awake, 11, 14 and 11 runs): UI-thread work per second of
scrolling, middle value 406 ms before the tabs, 418 ms with the row
hidden and 402 ms with three tabs shown, within the 5 % limit, so
`TabView` stays. 26 new UI tests (676 in all): the tab model, the
`ui.tabs` and `window_state` shapes against the schemas, and an
end-to-end test that opens three tabs (the last locked) in the left pane
and two in the right one, restarts the window and finds them all. The
live check's section "12: tabs" passed with real keys (Ctrl+T twice,
Ctrl+Tab, the lock through the palette, Enter on a folder in the locked
tab opened a fourth tab, Ctrl+W hid the row again, the last tab refused
to close). With the snapshot aid: five tabs left and three right open
independently, and F5 copies from the front tab of one pane into the
front tab of the other. Known gaps: a row shown in only one pane pushes
that pane's list down by the row's height; the editor's own header
repeats a tool tab's name; no drag to reorder and no "+" button (the
keyboard is complete, Article 7). Guide: [ui.md](ui.md), "Tabs".

### Phase 13 — The activity rail and the modular sidebar (the creator's card "Activity Bar & Modular Sidebar", 2026-09-29; last in the sleep-mode run of 2026-09-29/30, if time remains)

Goal: the design's rail layout: a 44 px strip of icons at the left edge
that swaps the sidebar between views, so the folder tree, search and
installed extension views share one place without touching the panes or
the terminal.

Decided 2026-09-29 with the creator: the rail is built as `ui.layout: rail`,
a choice next to `classic` and `right`, and the default is chosen after
use. Core views: the folder tree (Explorer) and search. Git, remote
environments, a drop zone, AI and an inspector are extensions (Article 10).
An extension's view is a Tool Extension page in WebView2, which already
runs in a browser process of its own; no new process model (Articles 8
and 11).

Produces: in the shell, the rail (Explorer, Search, Marketplace, Terminal,
and one button per installed sidebar tool, in a saved order); the sidebar
as a host of views that keep their state while hidden (native views kept;
the last web view kept warm, the others suspended); the Explorer view: the
folder tree over `list_directory`, with today's Pinned and Drives sections
above it, following the active pane (`sidebar.autoReveal`, on by default)
with a lock (`sidebar.lock`) and `sidebar.locate` (Alt+Shift+L); the Search
view with the file search of Phase 5c and its filters; `view.showExplorer`
(Ctrl+Shift+E), `view.showSearch` (Ctrl+Shift+F), `view.toggleSidebar`
stays Ctrl+B; dragging the divider under 150 px snaps the sidebar shut;
badges from plugin events; the width saved. In the core: nothing beyond
Phase 12's `window_state`. In the SDK: `tool.json` gains `sidebar` for a
tool that offers a sidebar view.

Done when: switching Explorer, Search, Explorer keeps the tree's expanded
nodes and scroll; the tree follows the active pane and stops following
when locked; Ctrl+B, Ctrl+Shift+E, Ctrl+Shift+F and Alt+Shift+L do what
they say; dragging the sidebar under 150 px closes it; `ui.layout: classic`
works exactly as today; a live-check section covers the keys.

Articles: 3, 4, 8, 10, 11.

**Built and verified 2026-09-30 in the sleep-mode run (af0a104 the
config fields; 68ee1f0, 84c6f76, 4cd925e, a42c343, fd81d8b, 15d9c29,
1a05a5c the shell).** `ui.layout: rail` (the default stays `classic`)
shows a 44 px activity rail: 36 px buttons, a 3 px accent pill on the
view in use, buttons Explorer, Search, Marketplace, Terminal and one for
each tool with `"sidebar": true` in its manifest, in the order saved in
`ui.rail` (Shift+Up and Shift+Down on a button move it; an empty list is
the default order). A second press on the active view's button closes the
sidebar. The sidebar holds its views: the Explorer (Pinned, Drives and a
lazy folder tree over `list_directory`, one request for each folder
opened and cancelled when it closes; it follows the active pane while it
shows unless locked, `ui.sidebarAutoReveal`), the Search view (the field,
its whole-volume box and the hits, over the same model as the command
bar's box) and the pages of tools with a sidebar page (a WebView2 each;
the page hidden last stays awake, the others are suspended).
`view.showExplorer` (Ctrl+Shift+E), `view.showSearch` (Ctrl+Shift+F),
`sidebar.locate` (Alt+Shift+L), `sidebar.lock`, and `view.toggleSidebar`
still on Ctrl+B. The divider drags under 150 px to close and remembers
the last width (`ui.sidebarWidth`, and `ui.sidebarView` the view shown
last); badges come from the plugin event `badge`. What real keys found
and the same night fixed: a tool page in the sidebar now hands back the
view keys, so the mouse is never the only way out (Article 7); closing
the sidebar, and Esc in the rail, the tree or the search field, give the
keyboard to the pane; the live check sends the cursor block as extended
keys, since Windows drops Shift from a keypad Down with Num Lock on. Also
in this item: a tool page may run the commands of a plugin it follows (the
command's source is that plugin and its id starts with the plugin's), a
general rule with no plugin name in the window, so the Agent's chat page
works; and the live check's "ask" step runs against a marketplace index
the run builds itself, so nothing in the run reads the network. 839 UI
tests (the end-to-end ones on the real core in 48 s); the classic and
right layouts differ from snapshots taken before the phase by 19 and 11
pixels of 2.5 million (two runs of one build differ by 6). The live
check's section "13: rail" passed with real keys and the mouse in a
whole-script run: 105 answers True, none False, with the tabs and the
Agent's drag and ask in the same run. Open for the creator: Ctrl+Shift+E
inside the search field needs Esc first, since a text box keeps every key
but the immutable tier's (a change would put `view.showExplorer` in that
tier); Enter in the tree goes to the folder and hands the keyboard to the
pane; whether the default layout becomes `rail`; and a UI Automation walk
over a tree of thousands of open rows stalls the window for seconds, so a
screen reader on a huge tree is a risk worth a later look. One fault
found the same day by the real-key check of the heavy-logging window and
fixed (ca72efc): started in the rail layout, the tree was empty until a
key opened it, because the reveal scrolled to a row before the list was
laid out and the rows were drawn far below the sidebar's window; the row
is laid out first now, or the scroll waits for the next layout, and a
test starts the window the way a user does and finds the rows on screen
before any key. 849 UI tests. The whole live check then ran with real
keys to the end with exit code 0: 106 answers True, none False, and the
scroll goal met in that run. Guide: [ui.md](ui.md), "The activity rail
and the sidebar".

### Phase 14 — The AI agent extension (the card "AI Agent & Intelligent Workflows — the decision", 2026-09-29, which combines the two drafts at the creator's command; built in the sleep-mode run of 2026-09-29/30)

Goal: an opt-in extension, CabinetOS Agent, that works the file manager
next to the user through the same command line a human uses, with a
preview before every change. Nothing AI-specific in the core (Article 10);
the extension is the flagship pair of Article 11: a Core Plugin (the
engine) and a Tool Extension (the chat).

Decided 2026-09-29 (the card): the model never gets a shell, only command
lines in `cab` syntax, which the plugin parses with the command line's own
definitions and turns into core requests; the window owns and reports its
state; the preview pane is a core listing built from proposed changes;
keys live in the Windows Credential Manager and never reach the plugin,
the core adds the header; the undo journal reverses renames and moves from
a log and keeps a copy only of overwritten files; the audit log is JSON
Lines next to the core's logs (Article 12); providers Anthropic and any
OpenAI-compatible endpoint (Ollama, LM Studio, OpenAI); the tiers Advisor,
Diff and approve (the default) and Autonomous, enforced by the plugin,
which is the only thing that issues commands.

Stages, in order:

- **14a, core foundations (protocol 13):** `window_state` and
  `cabinetos-cli state --json`; `preview_listing`, `preview_apply`,
  `preview_cancel`; `secret_set`, `secret_get`, `secret_delete`,
  `secret_list`; the plugin capability `net`, grantable with named hosts
  and served by the core's own HTTP client (`http-request`, with an
  optional secret the core inserts as a header); `fs:watch` for plugins;
  plugin commands with an `input` prompt; plugin events forwarded to tool
  pages; the undo journal, `undo_job` and `cabinetos-cli undo`; `cab.exe`
  as a second name of the CLI in the release folder.
- **14b, the extension:** `sdk/extensions/agent`: the plugin (Rust to
  WASM) with the two providers behind one trait and a fake for tests,
  settings under `plugins.agent`, the command parser shared with the CLI;
  the chat page `sdk/tools/agent-chat`.
- **14c, Ask:** `agent.ask` (Ctrl+K Ctrl+A) through the window's prompt
  box; the preview in the other pane; Enter applies, Esc cancels.
- **14d, the agent terminal:** the chat page in the dock or a pane's
  editor tab; paths dragged in from a pane; every command shown before it
  runs.
- **14e, the rest:** watch folders (`agent.rule.add`), the tiers, undo
  (`agent.undo`), the audit log and its list in the page; the two items
  in the marketplace index.

Done when: with the fake provider, "rename these to vacation_*" from Ask
shows the preview and Enter renames; the chat runs a read command and
shows a write command for approval; a rule on a test folder runs on a new
file; tier 1 blocks a write; `cab undo` reverses a rename; nothing calls a
real model until the creator stores a key (`cabinetos-cli secret set
anthropic`); the core's size and start time without the extension are
unchanged.

Articles: 1, 4, 7, 8, 10, 11, 12.

**14a, the core foundations, built 2026-09-30 (protocol 13; 46c644c,
b6bd317, 942b82e, 94b0e33, 85d6cd5, c6489a3, 44f2c66, 4feb72f).** Every
addition is general and works without AI. `window_state` keeps one state
per connection (a client is "<hello name>#N") and `get_window_state`
returns the newest or a named one; `cabinetos-cli state` prints it.
Previews: `preview_listing` answers `preview_opened` with the listing (a
normal listing with the header's preview flag and 12-byte preview rows
at the old reserved offset, so a folder listing is byte-identical to
before), `open_preview` reopens one a plugin proposed, `preview_apply`
runs the rows as chained `steps` jobs (neighbouring rows of one kind
share a job; a failed job cancels the rest) and answers `jobs_started`,
`preview_cancel` drops it; previews expire after 10 minutes, 20 per
client. Secrets live in the Windows Credential Manager through the new
crate `cabinetos-secrets` (`secret_set`, `secret_get`, `secret_delete`,
`secret_list`, `cabinetos-cli secret`); a value never prints, and a test
scans a trace-level log for it. A plugin reaches the network only
through the core's `http-request` (ureq on rustls with the Windows
certificate store): `https:` only except localhost, only the hosts and
the secrets its manifest names, no redirects, 8 MiB, 120 s, the secret
put into the named header by the core and never seen by the plugin, one
log line per request; the WIT package is 0.2.0, and a 0.1 plugin is
refused. `fs:watch` gives a plugin `watch-folder` and `unwatch-folder`
under its roots and the export `on-event` with `folder-changed` (200 ms
batches, an `overflow` flag, at most 16 watches). A plugin command may
declare `input` in its manifest, and `list_commands` says so. Every job
leaves one line in the undo journal (`%LOCALAPPDATA%\CabinetOS\undo`,
200 jobs, 256 MiB of saved copies, a replaced file moved aside rather
than copied); `undo_job` reverses a job or the newest one not yet
undone, names what cannot come back (a file in the Recycle Bin, a delete
for good), and `cabinetos-cli undo <job>|--last` follows it. The release
carries `cab.exe`, the command line under a short name. New fixture
plugins `fetcher` and `watcher`. 700 core tests. Left for the extension
and decided the same night: a plugin can make core requests only through
a new host function under an allow-listed capability, and a plugin may
read its own settings under `plugins.<id>.settings`; both go in with 14b.
Guides: [ipc.md](ipc.md), [plugins.md](plugins.md), [jobs.md](jobs.md)
("Chains and steps", "Undo").

**The window's parts of Phase 14, built 2026-09-30 (401d439, cc46f5f,
2dad54c, 74510e7, 80d5ed8, 409a1b8, d6e2de7).** Nothing in the window
knows an agent (Article 10); every piece is a general rule. A plugin
command with an `input` asks for its text in the prompt box first, titled
by the input's title, and Esc runs nothing. A preview a plugin proposes
opens with `open_preview` whenever a plugin command's result or any
plugin event carries a string field `preview`, and shows in the other
pane in place of its list (in single-pane mode the window switches to
dual while the preview waits, and back after): a create, a rename, a
move, a copy and a delete each with its target, a delete tinted red;
Enter sends `preview_apply` and Esc `preview_cancel` before the keymap,
and `preview_applied` or `preview_cancelled` from any cause, the
10-minute expiry included, closes it. Any plugin event carrying a string
field `notice` shows its text in the status bar. A tool page can follow
plugins (`subscribe`, at most 16, and `plugin-event`) and receives rows
dragged from a pane as `paths-dropped`, one path per line, at most 1,000,
through a catcher layer that covers the page only while a drag runs,
since a WebView2 takes drops for itself. The permissions review and the
plugin list show a `net` capability's hosts and the names of the stored
secrets it may use, never a value (Article 8). The UI test harness sets
`CABINETOS_UNDO_DIR`, so test jobs never write the real undo journal.
Checked with the real core (a six-row preview rendered, Enter ran the
four jobs it makes and the files matched, Esc changed nothing, a preview
opened by a plugin event, a fake command with input ran with the typed
text) and with a real mouse drag in the live check's new section 14,
whose "ask" part waits for the extension. Not yet seen with real keys:
the preview's Enter and Esc, the review dialog's hosts and secrets lines.
22 new UI tests, 767 in all. Guide: [ui.md](ui.md), "What plugins ask of
the window".

**14b to 14e, the extension, built 2026-09-30 (3887a4c, 99b83c0,
21d414a, 85a2870, bf5c0b1, aaa1fce, 9e8d8e5).** First the two core
additions decided the same night: the capability `core:request` (high),
whose manifest lists the request types a plugin may send through the
host function `core-request`, with a fixed list of requests no manifest
can allow (`hello`, `shutdown`, `set_value`, the secrets, the bundle,
`window_state`), and `plugins.<id>.settings`, an open object a plugin may
read through `config-get`, nothing else of the `plugins` section. Then
the extension. `sdk/extensions/agent/plugin` is a Core Plugin (Article
11, the engine layer): it reaches the core only through `core-request`
and a model only through `http-request`, holds no key, runs no shell and
writes no file itself except its audit log in its own data folder. The
model writes `cab` command lines, which the plugin parses with the
command line's own definitions, now the library crate `cabinetos-cli-args`
shared with `cabinetos-cli`; `ls` and `describe` are answered from the
plugin's own reads under its roots, `search` and `state` through the
core, and the write commands become a preview (tier 2, the default) or
run at once (tier 3). Providers: Anthropic, an OpenAI-compatible endpoint
(a local Ollama by default) and a fake for tests; settings under
`plugins.agent.settings`. `agent.ask` (Ctrl+K Ctrl+A) asks through the
window's prompt box and answers with a preview in the other pane;
`agent.undo` reverses the last applied preview's jobs in reverse order.
The chat page `sdk/tools/agent-chat` is a Tool Extension with tier
buttons, Undo, the audit Log and a Rules panel; it follows the plugin's
events and takes dropped rows. Watch folders: a rule per folder from the
settings or `agent.rule.add` (kept in `rules.json` in the plugin's data
folder, since a plugin cannot change the configuration), one model call
and one round per new file, temporary files skipped, the tier deciding
what follows, a rule paused after 3 failures in a row, at most 10 files a
minute, and the agent's own changes ignored so a renaming rule cannot
loop. Both parts install from the marketplace as `agent` and
`agent-chat` (`build-index.ps1 -Extensions`; the default index leaves
them out on purpose, and publishing them is the creator's step). The
built plugin is committed as the fixture `sdk/fixtures/plugins/agent`
(about 1 MB, a new copy in the history at each rebuild), so the host
tests load the real component without a WASM toolchain. Tests: 722 core
(two host tests run the built component with the fake provider through
`agent.ask` to a preview, `agent.undo` after an apply, and a rule on a
real watched folder) and the plugin's own suite of 90, which the five
checks do not run. No real model was called; the first real conversation
waits for the creator to store a key (`cabinetos-cli secret set
anthropic`). Left for the shell: a tool page may run the commands of a
plugin it follows (a general rule, so the chat page can send `agent.*`),
and the live check's "ask" step with the real settings key and a
canned-reply file. Guide: [extensions/agent.md](extensions/agent.md).

### Phase 15 — Heavy logging mode and trace ids (the creator's idea, 2026-09-29 late evening; built in the sleep-mode run of 2026-09-29/30)

Goal: a switch that makes CabinetOS record every operation, even at the
cost of speed, so a fault the normal log does not show (a lost key, a job
that did not do what was asked, a plugin or an AI agent's action) can be
followed from the key press to the disk; and, in both modes, one id per
user action that every log line of every process carries.

Decided 2026-09-29 with the creator, on the planning session's six
questions: (1) in heavy mode, when the writer cannot keep up, the queue
grows in memory to 256 MiB and then the core's operation waits for the
writer; the window's UI thread never waits (it drops and counts). This is
an explicit exception to Article 12's "never blocks the main I/O
pipeline", for heavy mode only, recorded as [ADR 0013](decisions/0013-heavy-logging-may-wait.md);
normal mode stays as today. (2) Heavy mode records every core request and
reply with full payloads (64 KB per line, secrets masked), every file a
job touches with bytes and timings, every key press by name, command,
focus change and the frame table every second in the window (never typed
text), every plugin host call and every network request's host, status,
size and time (never headers or bodies). (3) The switch is `logging.heavy`
in `cabinetos.json` (the question named `diagnostics.level`; the `logging`
section already exists), the palette command "Diagnostics: Toggle Heavy
Logging", and `cabinetos-cli config set logging.heavy true` for an agent;
on until turned off; a HEAVY LOG pill in the status bar and a notice at
start. (4) Separate heavy files per process (built as
`heavy-<process>.<date>[.<part>].jsonl`), 2 GB in total, oldest deleted
first; "Diagnostics: Open Log Folder". (5) Trace ids in
both modes: the window's ULID per action travels as `trace` on every
request, into the jobs and plugin calls the core starts for it, and onto
every line; `cabinetos-cli log trace <id>` prints the chain in time order.
(6) On a crash with heavy mode on, a zip with the crash trace and the last
10 minutes of every process's logs; "Open crash folder" at the next
start; the same bundle on demand, "Diagnostics: Save Log Bundle".

Produces: in `cabinetos-diag`, the heavy sink with the byte-counted queue,
the wait rule and the disk cap; the `trace` field of the protocol envelope
and `trace_id` on every span; per-entry job lines, host-call lines and
network lines at heavy level; `save_log_bundle` and the crash hook's zip;
`logging.heavy` in the config; `cabinetos-cli log trace | tail | bundle`.
In the shell, the heavy file with a 64 MiB never-wait queue, the trace on
every request, the key, command, focus and frame lines, the three
Diagnostics commands and the status-bar pill.

Done when: with heavy mode on, a copy of 1,000 files writes one line per
file and a slow writer makes the job wait rather than lose a line (test);
`log trace` of one F5 press prints the window's key and command lines, the
core's request and reply, the job's entries and its end, in time order; a
crash with heavy mode on leaves a zip with the last 10 minutes of all
logs; the folder never passes 2 GB of heavy files; normal mode's speed on
the 100,000-entry listing is unchanged.

Articles: 6, 12 (with the exception of ADR 0013).

**Trace ids, built 2026-09-30 (054a1fa, 687abd3, d60d82f).** Every
envelope on the pipe may carry `trace`, the ULID of the user action, next
to `id`; a bad value rejects the frame like a bad `id`, and the protocol
version stays, since an older peer ignores the field. The window makes one
trace per command run (`Diag.BeginTrace`; `CoreClient` fills every request
sent while the handler runs), the CLI one per run. The core logs every
line with `trace_id` and `request_id`, answers with the request's trace,
and gives it to everything the request starts: the job and its events,
the measures, the plugin calls and the events they emit, a
`config_changed` from `set_value`. A watcher's lines carry no trace, and
a request without one is its own action. The id spans are kept whatever
`logging.level` says, so the trace on the pipe does not depend on the log
level. `cabinetos-cli log trace <id>` reads every log file in the folder
and prints one action from every process in time order, once per event
even when the heavy file repeats the normal one; `log tail` prints a
process's newest lines. Tests: core +12 (the end-to-end ones in
`cabinetos-core/tests/traces.rs`), UI +4 (`TraceTests`), 654 UI tests.
Guide: [diagnostics.md](diagnostics.md), "How an action's trace id
travels".

**Heavy mode in the core, built 2026-09-30 (3f053d3, bd36611).**
`logging.heavy` in `cabinetos.json` (or `CABINETOS_LOG_HEAVY=1`, which
wins, like `CABINETOS_LOG`) switches it within a second while the core
runs. Every event at every level, TRACE included, goes into
`heavy-<process>.<date>[.<part>].jsonl` next to the normal files: the
prefix, because the daily writer prunes every file whose name starts with
the process name; a new part every 256 MiB, so a file in use never grows
past the cap; 2 GiB in the folder across all processes, the oldest part
deleted first and named in the normal log, never a file another process
has open. The queue is counted in bytes: over 256 MiB a thread waits in
50 ms slices and the wait is logged afterwards ("heavy log waited",
with the trace); the core's async workers and its main thread, which
carry the pipe, never wait and get 32 MiB of extra room, then drop and
count. That is the exception the creator chose to Article 12, for heavy
mode only, recorded as [ADR 0013](decisions/0013-heavy-logging-may-wait.md). Heavy-only lines carry a target
under `heavy::` and stay out of the normal file: the request and reply
payloads (masked, 64 KB per line, `truncated` when cut), one `entry done`
per piece of job work with its kind, paths, bytes, milliseconds and
outcome, every plugin host call with its arguments, and every marketplace
HTTP request's host, method, status, bytes and time. Masking covers the
secret messages' values, every field named like a secret at any depth,
and the authorization, x-api-key and cookie headers in any shape. A
listing still logs one line per listing, never one per entry. The
indexer follows the environment variable only, since it does not read the
user's config. Tests +13, among them a slow writer that makes a thread
wait without losing a line, a never-wait thread that drops and counts,
the parts and the cap with tiny limits, and the switch on and off while
the core runs. Guide: [diagnostics.md](diagnostics.md), "Heavy mode".

**Log bundles, the core's part, built 2026-09-30 (840b22a).**
`save_log_bundle { minutes }` (1 to 1440, 10 by default) answers
`log_bundle { path }`: `bundle-<time>.zip` in the log folder with the last
minutes of every log file of every process, normal and heavy (each read
from its end, so a large file costs little), the crash traces of the last
24 hours, and `bundle.json` with the versions, the Windows build (read
from the registry by `cabinetos-fs`, since the diagnostics crate allows no
unsafe code), the `CABINETOS_*` variables and the config in effect, with
secrets masked. In heavy mode the crash hook writes the same as
`crash-<time>.zip` after the trace, on the panicking thread, without
waiting for any lock. `cabinetos-cli log bundle [--minutes 10]` prints the
path. Bundles are never deleted by CabinetOS. The heavy mode's timing
tests now poll for their condition with generous limits instead of fixed
sleeps, after one of them failed once under the load of four agents
building at the same time. Tests +8, 679 core tests. Guide:
[diagnostics.md](diagnostics.md), "Bundles"; [ipc.md](ipc.md), "Log
bundles". Left for the window's half: the C# crash bundle, the notice
"Open crash folder", the palette command, ADR 0013 and the window's own
heavy file.

**Heavy mode in the window, built 2026-09-30 (c6e826b, 2352d46).** The
window's `LogWriter` has a second, byte-counted queue for
`heavy-ui.<date>[.<part>].jsonl`, 64 MiB. A background thread waits above
that and writes `heavy log waited` afterwards. The UI thread, the pipe's
reader and the crash hook never wait: their lines are dropped and counted
8 MiB above the cap, and the count goes into the file and into the status
bar's `HEAVY LOG` pill ("HEAVY LOG, 1,204 lines lost"). The parts of
256 MiB, the 2 GiB cap over every process's heavy files and the
no-delete-sharing rule are the core's. `LogMask` masks payloads with the
core's rules, capped at 64 KB. The window follows `logging.heavy` at start
and on `config_changed` (`CABINETOS_LOG_HEAVY` wins). "Diagnostics: Toggle
Heavy Logging" writes the setting with `set_value`, so both processes
follow; "Open Log Folder" and "Save Log Bundle" are seeded too, without
keys (97 commands by the morning, with the rail's four). Heavy lines: every request and reply payload, every
key by name and modifiers with the element that has the keyboard (a key
typed into a text box is `text input` without the character, and AltGr
counts as typing), every command with source, trigger and arguments,
focus changes with where Windows sends the keys, the frame table every
second, and web page messages by name and size. A key press gets a ULID
that a command it starts takes as its trace, so the key, the command and
its requests are one chain. Tests: UI +68 (`HeavyLogTests`), 766 UI
tests; the core's five checks pass at 690 tests. Not yet seen in a running
window, since the keyboard belonged to another agent: the pill after a
toggle, the start notice, a key and a focus line in the heavy file, and
"Open crash folder" after a self-test crash with heavy mode on. Guide:
[ui.md](ui.md), "Heavy logging".

**The window's crash bundle, built 2026-09-30 (566b690).** A crash of the
window while heavy mode is on writes `crash-<stamp>.zip` next to the crash
trace, in C# with the core's contents: the last 10 minutes of every log,
read from the end so a large heavy file is not read whole, the crash
traces of the last 24 hours, and `bundle.json` with the versions, the
Windows build, the `CABINETOS_*` variables and the last configuration,
secrets masked. A failed zip leaves the trace and no half zip. At the next
start the window compares the newest crash zip with its last start
(`ui.last-start` in the log folder) and shows "Open crash folder" in the
status bar until it is used. "Diagnostics: Save Log Bundle" asks the core
for a bundle of 10 minutes and opens the folder. Tests: UI +11
(`LogBundleTests`), including `save_log_bundle` against the real core.

**ADR 0013 and the Constitution, 2026-09-30.** The exception the creator
chose is written as [ADR 0013](decisions/0013-heavy-logging-may-wait.md):
only in heavy mode, only for the core's operations, never the UI thread
or the pipe's reader, always logged, never silent. The Constitution is
not edited. The record proposes one sentence the creator could add to
Article 12 after "or the UI thread", for the creator to accept or refuse:
"The one exception is the opt-in heavy mode, where a thread of the core
may wait for the log writer so that no operation goes unrecorded (ADR
0013)." [ARCHITECTURE.md](ARCHITECTURE.md) gained the row for its §8.


## 6. Phase 1 in detail — the Rust core scaffold

This is the first coding task. The creator will give the exact instruction; this
section is the proposal to refine.

### Crates in the `core/` Cargo workspace

| Crate | Kind | Responsibility | Unsafe code allowed? |
|-------|------|----------------|----------------------|
| `cabinetos-core` | binary | Process startup, pipe server, session lifetime, wiring of all libraries | no |
| `cabinetos-protocol` | library | The IPC contract: message types (serde), request IDs, `#[repr(C)]` shared-memory layouts with size and offset tests, protocol version | no |
| `cabinetos-diag` | library | `tracing` setup: non-blocking rolling JSON Lines appender, `boundary` and `request_id` fields, panic hook that writes a crash trace and flushes, ring buffer of recent events | no |
| `cabinetos-ipc` | library | Named pipe server with a user-only DACL, length-prefixed framing, shared-memory section creation and handle duplication | yes, isolated |
| `cabinetos-fs` | library | Directory enumeration, hydration, volume and disk detection, change watching | yes, isolated |
| `cabinetos-jobs` | library | `JobQueueManager`, copy/move/delete backends, progress throttling, conflict states | yes, isolated |
| `cabinetos-index` | library | In-memory volume index, MFT reader, USN tailer (shared by indexer and core query side) | yes, isolated |
| `cabinetos-indexer` | binary | The elevated service around `cabinetos-index` | no |
| `cabinetos-config` | library | `cabinetos.json` load, schema, defaults, watch, diff | no |
| `cabinetos-commands` | library | Command registry, keybinding model, immutable tier | no |
| `cabinetos-plugins` | library | `wasmtime` host, WIT bindings, capability policy, trap handling | no |
| `cabinetos-cli` | binary (dev tool) | Command-line client for the pipe: `ping`, later `ls`, `copy`. Lets the core be tested with no UI | no |

In Phase 1 every crate exists with a `lib.rs` or `main.rs` whose module comment states its responsibility and the Constitution articles it serves. Only `protocol`, `diag`, `ipc` (pipe part), `core` and `cli` have real code. The rest hold their public type names and `todo!()`-free stubs, so the shape is reviewable.

### Workspace-wide settings

- `rust-toolchain.toml`: stable, pinned. Edition 2024.
- Lints in `Cargo.toml` `[workspace.lints]`: `unsafe_code = "forbid"` by default, relaxed to `"deny"`-with-allow in the four crates that need it; every `unsafe` block carries a `// SAFETY:` comment; `clippy::pedantic` as warnings with a short allow-list; `missing_docs` on public items.
- `cargo-deny` config: licenses allowed (MIT, Apache-2.0, BSD, ISC, Unicode, Zlib), advisories checked.
- Dependencies (first set): `tokio`, `serde`, `serde_json`, `schemars`, `tracing`, `tracing-subscriber` (json), `tracing-appender`, `windows` (windows-rs), `ulid`, `thiserror`, `anyhow` (binaries only), `crossbeam-channel`, `dashmap`, `criterion` (dev), `insta` (snapshot tests, dev).
- CI: GitHub Actions on `windows-latest`: fmt, clippy with `-D warnings`, test, deny, build in release. GitHub runners run as Administrator, so later MFT and USN tests can run there.

### Definition of done for Phase 1

1. `cargo build --workspace` and `cargo test --workspace` pass; clippy and fmt are clean; CI is green on `main`.
2. `cabinetos-core` starts, creates the pipe with a user-only DACL, and exits cleanly when its parent handle is signalled or on `Ctrl+C`.
3. `cabinetos-cli ping` sends `{ "id": "<ulid>", "type": "ping" }`, receives `pong` with the same ID, and that ID appears in `%LOCALAPPDATA%\CabinetOS\logs\core.<date>.jsonl` with `boundary: "engine"`.
4. A forced panic in the core produces `crash-<timestamp>.json` with a backtrace and the last events from the ring buffer, and the log file is flushed before exit.
5. `cabinetos-protocol` has a test that pins the size and field offsets of every shared-memory struct, so a change is a deliberate, visible act.
6. `docs/ARCHITECTURE.md` gets a "Crate map" section linking each brief section to its crate.

### Explicitly not in Phase 1

Directory listing, shared-memory data (only the section-creation helper), file operations, config watching, WASM, terminal, any C#.

## 7. Open questions for later phases (nothing here blocks Phase 0 or 1)

1. **Terminal rendering control (Phase 8).** Settled 2026-09-28: xterm.js 6.0 in WebView2 (Phase 5c), with the DOM renderer and a browser process of its own; a native renderer stays possible later.
2. **First-run layout (Phase 5).** Settled 2026-09-28 by the creator: dual pane on first start; see conflict B.
3. **Config comments (Phase 3).** Settled 2026-09-28: strict JSON for version 1 ([config.md](config.md)); every JSON tool can read it and there is one parser. JSONC stays possible later by stripping comments before parsing.
4. **Indexer install (Phase 6).** Settled 2026-09-28 by what was built: both exist. `cabinetos-indexer --install` registers the service (manual start, one UAC prompt), `--console` runs it elevated for one session, and the installer's `-Indexer` switch registers the service ([ADR 0009](decisions/0009-packaging.md)).
5. **IoRing vs CopyFileExW (Phase 4).** Measure before adopting. IoRing's API surface in `windows-rs` must be checked.
6. **Packaging (Phase 10).** Settled 2026-09-28: version 1 ships unpackaged, as a zip with an install script ([ADR 0009](decisions/0009-packaging.md)); MSIX is reconsidered when the indexer can ship as an optional component.
7. **ARM64 test hardware.** CI can build ARM64 but not run it. A test device or a cloud ARM VM is needed before claiming support.

## 8. Risks

| Risk | Effect | Mitigation |
|------|--------|------------|
| MFT parsing edge cases (attribute lists, hard links, reparse points, very large volumes) | Wrong or missing entries in search | NTFS only for indexing; fixture volume images in tests; ReFS and others use the fallback path |
| .NET SDK and WinUI workload are not installed on this PC | Phase 5 cannot start | Closed 2026-09-28: the .NET 10 SDK is installed; command-line builds get the Windows App SDK from NuGet ([dev-setup.md](dev-setup.md)) |
| Garbage-collector pauses in the C# UI | Frame drops | Dumb UI Rule: the UI holds no data; listings are read from native memory |
| Shared-memory layout drift between Rust and C# | Silent corruption | Size and offset tests on both sides against one constants file; protocol version in the header |
| WASM plugin runs forever or eats memory | Core stalls | wasmtime fuel and epoch interruption, per-store memory limits, one thread per plugin |
| The elevated indexer becomes an attack surface | Privilege escalation | Read-only queries only; no file operations; pipe DACL; no plugin code inside the indexer |
| Scope creep into the core | Article 10 violated | Every new feature request is checked against Article 10 first; default answer is "extension" |
| GitHub Actions is paused: the private repository's free minutes ran out on 2026-09-28, and the creator decided on 2026-09-29 to spend no money on it | No independent second machine runs the checks; the manual release job cannot run | Every change is verified on the development PC before it is accepted (the five core checks, the UI tests, the live checks in [log/](log/2026-09-28/README.md)); CI returns at no cost when the repository goes public (Phase 10), or when the creator decides otherwise. Nobody asks the creator about billing before then. |
