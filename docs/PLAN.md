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

A phase that is done keeps here its goal, its decisions, its done-when list and
one status paragraph with the dates, the commits, the test counts and what was
found. The rest of what was written about it, day by day, is in [plan/](plan/),
one file per phase. Each status paragraph links to its file.

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

**Status.** Done 2026-09-28 (commits `1a9f292` to `3e22b30`; the pipelined
follow-up `126ca97` to `164286b`). With the follow-up the creator approved,
100,000 entries reach shared memory in 46–49 ms on this PC, so the 50 ms
first-cut target is met, with little margin. The kernel's own calls are about
80 % of that time. The first path (60–68 ms) stays behind
`ListOptions::pipelined = false`. Open: showing the first screen within a few
milliseconds needs chunked publishing. History:
[plan/phase-02-filesystem-engine.md](plan/phase-02-filesystem-engine.md)

Articles: 1, 5 (two independent pane sessions).

### Phase 3 — Config, commands and keybindings — done 2026-09-28

Goal: the command architecture exists before any button does.

Produces: `cabinetos.json` schema and defaults; file watcher with parse, validate, diff, and change events (Article 6 real-time sync); command registry seeded from the design's command list (id, category, name, default binding, source); chord semantics (1000 ms window) written as a spec shared by core and UI; the Immutable System Tier list (Article 7); keymap export to the UI.

Done when: editing the file by hand changes a binding in the running core within one second, and a test proves an immutable binding cannot be overridden.

**Status.** Done 2026-09-28 (commits `719e4cd` to `8b278af`). On this PC (debug
build) a hand edit of `cabinetos.json` reached a watching client 119 ms after
the save, and a broken save was reported as `config_error` 104 ms after it. The
test `the_immutable_tier_cannot_be_rebound` proves the Immutable System Tier
holds. Specs: [keybindings.md](keybindings.md), [config.md](config.md). History:
[plan/phase-03-config-commands-keybindings.md](plan/phase-03-config-commands-keybindings.md)

Articles: 6, 7.

### Phase 4 — Job engine (copy, move, delete) — done 2026-09-28

Goal: file operations that never block and never stall on one bad file.

Produces: `JobQueueManager` with per-physical-disk queues (HDD: one at a time; NVMe/SSD: several in parallel, detected through the storage seek-penalty property); `CopyFileExW` backend with the progress callback and unbuffered mode for huge files; progress coalesced to 30 updates per second; per-file conflict state machine (pause that file, report, continue the batch); pause, resume, cancel; move as rename on the same volume and copy-plus-delete across volumes; delete to the Recycle Bin through the shell API. IoRing is measured against `CopyFileExW` before it is adopted.

Done when: copying 10,000 small files plus one 20 GB file to the same disk shows the UI-side progress stream at 30 Hz, and an injected "file exists" conflict does not stop the other files.

**Status.** Done 2026-09-28 (commits `c25dd6e` to `d5b4160`). On this PC (E:,
NVMe, release build) 10,000 small files plus one 20 GiB file copied in 9.7 s
(2.3 GB/s). The progress stream peaked at 22 events per second against the limit
of 30; Windows's 15.6 ms timer tick is why it is not closer. A second run into
the same folder skipped 10,001 existing files through conflicts and copied 1,000
new ones in 1.5 s. Open: IoRing is still unmeasured. Details:
[jobs.md](jobs.md). History:
[plan/phase-04-job-engine.md](plan/phase-04-job-engine.md)

Articles: 1, 5.

### Phase 5 — WinUI 3 shell v1 — built 2026-09-28, checked with real keys 2026-09-28

Goal: the first window a user can live in. Design views A and B.

Produces: the C# solution; process launcher and pipe client; shared-memory reader in `unsafe` C#; `CommandRouter` with the chord state machine; dual-pane grid with `ItemsRepeater` virtualization and instant single-pane toggle; breadcrumb address bar; status bar; Command Palette with fuzzy search and inline rebinding (design view B); Mica backdrop; design tokens from the handout; the first-run experience.

Done when: the core and the UI run together, a listing of 100,000 files scrolls without frame drops (measured 2026-09-29 with the snapshot aid and the display asleep: about 15 ms of UI-thread work per page of 30 rows, and 3.7 % of frames over 16.7 ms at the rhythm of a held PageDown on a 60 Hz display; the gap goal is `livecheck.ps1`'s line from a real-key run: not met on 2026-09-29, met in the default theme on 2026-09-30 once cursor keys were made at the next frame, see step 3), and every visible action goes through `CommandRouter` and a command ID.

**Status.** Built 2026-09-28 and checked with real keys the same evening
(commits `4d6d403` to `120c262`, then `350391e`); real-key runs on 2026-09-29
and 2026-09-30 found six faults, all fixed (`2e2fb01`, `c1f5502`, `1fa6db0`,
`b116346`, `c8215f0`). 650 UI tests at the last count. A folder of 100,000
entries is drawn 82 ms after the command, and a held PageDown drew 53–61 frames
per second. The scroll gap goal was met in the default theme on 2026-09-30
(step 3, under Phase 10). Open for the creator: the light look is a first
choice, since the design handout leaves light mode open. History:
[plan/phase-05-winui-shell.md](plan/phase-05-winui-shell.md)

Decided 2026-09-28 ([ADR 0010](decisions/0010-windows-app-sdk-license-exception.md)): the Windows App SDK ships under the Microsoft Software License Terms, not an open-source license; WinUI 3 (ADR 0001) cannot be used without it, so the UI layer's dependence on it and on the Windows App Runtime is a recorded exception to Article 2, limited to Microsoft's freely redistributable platform components; the core has no such dependency.

Articles: 3, 4, 5, 7.

### Phase 6 — Indexer — done 2026-09-28

Goal: instant search over whole volumes.

Produces: MFT reader into a compact in-memory tree keyed by file reference number; USN Journal tailing to keep it fresh; the elevated `cabinetos-indexer.exe` as a Windows service with an on-demand elevated mode; read-only query pipe; core fallback when absent; search field in the UI.

Done when: the index of the system drive builds in seconds, a rename on disk appears in search results within a second, and the app works unchanged with the indexer stopped.

**Status.** Done 2026-09-28: the core side (commits `45a4c7a` to `82663f3`), and
the search field in the window (Phase 5c). On the GitHub Actions runner's `C:`
the index built 1,358,009 entries in 3.3 s, a 3-letter query took 12.5 ms, and a
rename was visible to search after 9 ms. With the indexer stopped, `search` on
this PC answered from a walk. Decided 2026-09-28: version 1 answers file names
to any logged-on user of the PC, as the "Everything" tool does
([ADR 0011](decisions/0011-search-without-per-user-filtering.md)). Open: the
index of this PC's own `C:` is still to be measured in an elevated run. Guide:
[indexer.md](indexer.md). History:
[plan/phase-06-indexer.md](plan/phase-06-indexer.md)

Articles: 1.

### Phase 7 — Plugin host and Tool Dock — done 2026-09-28

Goal: the two extension layers, with strict boundaries.

Produces: `wasmtime` with the Component Model; the WIT package for the plugin world; plugin manifest with capability requests (`fs:read`, `fs:write`, `cmd:register`, `process:run`, `net`, `credentials`); the permissions review flow (design view C dialog); one store and one thread per plugin with fuel and memory limits; trap handling that logs the plugin ID, kills the instance and keeps the core alive; the Tool Dock in the UI with WebView2 hosting; one sample core plugin and one sample Tool Extension (Markdown preview, as in the design).

Done when: a deliberately crashing plugin is logged and removed while the core keeps serving listings, and the sample extension renders in a dock pane.

**Status.** Done 2026-09-28: the core side (commits `0756825` to `0bf72d3`), and
in the window (Phase 5c) the permissions review, the Tool Dock with the
terminal, and Markdown Preview as the sample Tool Extension. A deliberately
crashing plugin (`crashy.crash`) answered `plugin_error` at once, and the same
core then listed `C:\Windows` (108 entries) in 1.4 ms; `spinner.spin` was
stopped at 5.1 s. Open: a tool that asks for the dock opens in a pane until the
dock has tabs for tools. Guide: [plugins.md](plugins.md). History:
[plan/phase-07-plugin-host.md](plan/phase-07-plugin-host.md)

Articles: 8, 10, 11.

### Phase 8 — Integrated terminal — done 2026-09-28

Goal: a shell scoped to the active pane.

Produces: ConPTY host in the core with a byte pipe per session; shell profiles (pwsh, cmd, WSL) in config; the terminal pane in the UI, hidden until `Ctrl+`` `; "cwd follows the active pane". The rendering control is xterm.js in WebView2 (section 7, question 1, settled).

**Status.** Done 2026-09-28: the core side (commits `cce4222` to `cc61eec`), and
the terminal pane in the window (Phase 5c). On the development PC (debug build)
a shell starts in 17–24 ms and closes at its prompt in 3–15 ms, and 2.8 MB
printed while no client read arrived complete. Each session has its own byte
pipe and belongs to the core, so a client may leave and attach again. Guide:
[terminal.md](terminal.md). History:
[plan/phase-08-integrated-terminal.md](plan/phase-08-integrated-terminal.md)

Articles: 4, 9.

### Phase 9 — Marketplace and theme engine — done 2026-09-28 (publisher trust still open; the public index decided, not yet published)

Goal: install and share extensions and themes.

Produces: the JSON theme format (accent, Mica tint, palette) with live apply; a marketplace index served as static files first; install, update and remove flows; publisher trust.

**Status.** Done 2026-09-28 on both sides (commits `aee0a5d` to `bcc9e66`), then
extended on 2026-09-29 and 2026-09-30: the theme collection (`ed904aa` to
`4e3166d`), Commander Compact (`9e4ce98`; the window's part `e037baa` to
`f95081e`) and the public index (`fef2e58`, `349a59f`). At the last count: 564
core and 633 UI tests. Themes apply live, the marketplace installs, updates and
removes, and the public index on GitHub Pages
([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)) serves 41
themes. The default theme follows the Windows accent. Open: publisher identities
are not built (`verified` is only shown). History:
[plan/phase-09-marketplace-themes.md](plan/phase-09-marketplace-themes.md)

Articles: 2, 8.

### Phase 10 — Packaging and release — buildable parts done 2026-09-28 (signing and publishing wait for the creator)

Goal: a stranger can install it.

Produces: MSIX or unpackaged decision; `winget` manifest; code signing; the indexer service installer step; release notes; the repo goes public.

**Status.** The buildable parts are done 2026-09-28 (commits `7506990`,
`b7acc12`, `b4dafb2` to `1c30c6f`) and checked again with a real install on
2026-09-29; nothing is signed or published. The history also holds hardening
steps 2 and 3; step 3, the scroll, was resolved 2026-09-30 (`61f044d`,
`58fa975`, `c5ed4c8`): 0 of 1,207 frames over 20 ms in a 20 s hold. The first
release, 0.1.0, was built on 2026-09-30 with the in-app updater inside (Phase
17): `dist\CabinetOS-0.1.0-win-x64.zip` with its hash, `dist\update\stable\latest.json`
and `notes-0.1.0.md`, all unsigned, and the CHANGELOG section is named
(`4b102b6`). Last counts: 913 UI and 772 core tests. Open for the creator: a
code-signing certificate, a public repository, `gh release create`, the
marketplace's `update/stable/` files, the winget submission, and whether the
`.pdb` files stay in the zip. Open for the scroll: a blocking collection WinUI
asks for, and Commander Compact (11.6 to 18.8 % of gaps over 20 ms). History:
[plan/phase-10-packaging-release.md](plan/phase-10-packaging-release.md)

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

**Status, 11a.** Done 2026-09-29: the core's part (commits `15ef77e` to
`c6adef1`; protocol 12, 83 commands, 610 core tests) and the shell's part
(`9660057` to `14ce5f3`; 614 UI tests, one opt-in), checked with real keys the
same day: every command of the desk card's list ran from its key. On this PC,
sorting 100,000 entries by extension takes 50 ms (41 ms by name) and matching a
pattern on 100,000 names takes 43 ms. 11b to 11f are proposed and have not
started. History:
[plan/phase-11-total-commander-parity.md](plan/phase-11-total-commander-parity.md)

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

#### 11g — The extension workbench (medium to large; proposed 2026-10-02)

Covers the desk card "Product & Experience Brief: The Agent-Driven Extension
Workbench": a user with an AI assistant builds an extension from a sentence,
tests it live in the running app, and shares it through the marketplace.
Decided with the creator on 2026-10-02 in three question rounds: for any
user, not only developers; all three kinds (Tool Extensions, themes, Core
Plugins); Claude Code in the integrated terminal is the assistant, and any
agent with a terminal works the same way; Core Plugins for non-developers
run as JavaScript in a script-host plugin inside the sandbox, and Rust stays
for developers; the marketplace ships only what it built from source, and
the index carries the commit, the hash and the publisher; publishing is a
pull request on the marketplace repository that the creator approves; the
publisher is the GitHub login that opened it, and `verified` is the
creator's badge (this closes Phase 22's unit 4); capabilities are granted
in the review dialog once per extension, and again only when the manifest
asks for more; Revert is a git snapshot per clean mount in the extension's
own folder; the first version is CLI only, and an opt-in Workbench page
comes later from the marketplace (Article 10).

Produces, in two milestones. Milestone 1, Tool Extensions and themes:
`cab ext new <kind> <name>` writes the folder (a git repository with a
`CLAUDE.md` generated from the SDK docs, so it never drifts from the real
API); `cab ext mount <folder>` loads it into the running app with a
"development" badge in the Installed list and reloads it on every file
change, at most once a second; `cab ext revert` returns to the last
snapshot; `cab ext pack` validates (manifest, declared capabilities, one
start in the sandbox) and opens the pull request page in the browser, where
the user clicks (an agent cannot); typed one-line errors from every
command; and in the marketplace repository one workflow that checks a pull
request and builds the index with the publisher's login. Milestone 2, Core
Plugins for everyone: the script-host plugin (JavaScript in the sandbox,
with the same capabilities, limits and review dialog), the scaffold for a
script plugin, and the build server for Rust plugins (the marketplace builds
them from source on the pull request).

Done when: the creator makes one page and one theme from one sentence each,
without typing code, and both reach the marketplace through a pull request
(milestone 1); a script plugin with a command and a file hook does the
same, and a Rust plugin's binary in the index is the one the marketplace
built (milestone 2); a crashing or looping mounted extension leaves the core
and the window as they are, and after three crashes in a row it stays off
until the user reloads it.

Needs: 11d first (the plugin interface 0.2 with per-request grants and
plugin jobs), and a workflow in the public marketplace repository (free
minutes on a public repository).

Articles: 8, 9, 10, 11, 12.

Not in Phase 11: the Android app (feature 20; Articles 1 and 3), and Total
Commander's own plugin DLLs, which are native code that cannot be sandboxed
(Article 8).

**Status (2026-10-02, 20:56): the rest starts.** The creator's word of the
evening: 11b to 11f run in order, one sub-phase at a time, each handed to
coders with an audit first of what Phases 12 to 21 already built (tabs per
pane, Find in pane, the Commander Compact function-key bar, the context
menu from the config, the files type sprint, the terminal sprint), so only
what is missing is built. 11b starts when the laptop is free of the third
flakes round; Phase 22 (release readiness) runs beside it.

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

**Status.** Built 2026-09-30 in the sleep-mode run (`77c1169` the core's
`ui.tabs`; `725328f`, `4881f82`, `3fec822`, `07b5e8b`, `837361a`, `cf08875`,
`80c204b` the shell). 676 UI tests. Each pane has a `TabView` row that hides
with one tab. `TabView` stays: on the 100,000-entry folder the scroll work per
second was 406 ms before and 402–418 ms after, within the 5 % limit. The live
check's section "12: tabs" passed with real keys. Known gaps: a row shown in one
pane only pushes that pane's list down; the editor's own header repeats a tool
tab's name; no drag to reorder and no "+" button. History:
[plan/phase-12-tabs-per-pane.md](plan/phase-12-tabs-per-pane.md)

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

**Status.** Built and verified 2026-09-30 in the sleep-mode run (`af0a104` the
config fields; `68ee1f0`, `84c6f76`, `4cd925e`, `a42c343`, `fd81d8b`, `15d9c29`,
`1a05a5c` the shell; `ca72efc` a start-up fix). 849 UI tests. `ui.layout: rail`
(the default stays `classic`) shows the 44 px rail with the Explorer, Search and
tool views. The live check's section "13: rail" passed with real keys, and the
whole live check ran to the end (106 answers True, none False). Open for the
creator: Ctrl+Shift+E inside the search field needs Esc first; Enter in the tree
hands the keyboard to the pane; whether the default layout becomes `rail`; a
screen reader on a tree of thousands of open rows may stall the window. History:
[plan/phase-13-activity-rail.md](plan/phase-13-activity-rail.md)

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

**Status.** Built 2026-09-30 in the sleep-mode run. Core foundations at
protocol 13 (14a): `46c644c` to `4feb72f`, 700 core tests. The window's parts:
`401d439` to `d6e2de7`, 767 UI tests. The extension (14b to 14e): `3887a4c` to
`9e8d8e5`, 722 core tests. Claude Code in the terminal: `2f0af70` to `72a4bbc`,
726 core and 852 window tests. With the fake provider the chain works, and
Claude Code runs in a terminal profile with no API key. A local model broke the
command format and the agent changed nothing. On 2026-09-30 the creator stored
a key and Claude (`claude-sonnet-5-5`) answered the first real request
correctly in 2.4 s: one `rename` line, a preview, the file renamed on apply and
back on undo ([log/2026-09-30/claude-code-in-the-terminal.md](log/2026-09-30/claude-code-in-the-terminal.md),
check 4). Open for the creator: check the Claude Code login's organization with
`/status`; publish the two extension items.
History: [plan/phase-14-agent-extension.md](plan/phase-14-agent-extension.md)

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

**Status.** Built 2026-09-30 in the sleep-mode run: trace ids (`054a1fa`,
`687abd3`, `d60d82f`), heavy mode in the core (`3f053d3`, `bd36611`), log
bundles (`840b22a`), heavy mode in the window (`c6e826b`, `2352d46`) and its
crash bundle (`566b690`). At the last count: 690 core and 766 UI tests.
`cabinetos-cli log trace <id>` prints one action from every process;
`logging.heavy` writes `heavy-<process>` files; a crash in heavy mode leaves a
zip of the last 10 minutes of every log. Open: the pill, the start notice and
"Open crash folder" had not been seen in a running window when the window's half
was written. The ADR 0013 paragraph below holds a proposal for the creator.
History: [plan/phase-15-heavy-logging.md](plan/phase-15-heavy-logging.md)

**ADR 0013 and the Constitution, 2026-09-30.** The exception the creator
chose is written as [ADR 0013](decisions/0013-heavy-logging-may-wait.md):
only in heavy mode, only for the core's operations, never the UI thread
or the pipe's reader, always logged, never silent. The Constitution is
not edited. The record proposes one sentence the creator could add to
Article 12 after "or the UI thread", for the creator to accept or refuse:
"The one exception is the opt-in heavy mode, where a thread of the core
may wait for the log writer so that no operation goes unrecorded (ADR
0013)." [ARCHITECTURE.md](ARCHITECTURE.md) gained the row for its §8.
The creator accepted the sentence on 2026-09-30, in chat, as that exact
text, and Article 12 carries it since that day: the one edit of the
Constitution so far, made with the per-change approval CLAUDE.md asks for.

### Phase 16 — Shell redesign: one top row, per-pane breadcrumbs, find in pane, Quick Open (the creator's handout `_io/SHELL_REDESIGN.md`, 2026-09-30; started the same day) — done 2026-09-30

The creator's specification of 2026-09-30 changes the shell's structure,
for every theme. The title bar and the command bar merge into one 40 px
top row (32 in Commander Compact): a hamburger menu of common commands
from the registry, the app icon, a workspace pill, a centered command
center that opens Quick Open, and the right cluster (Dual/Single,
Terminal, Marketplace, Palette, Settings) before the caption buttons. The
global address bar, search field and nav buttons go, and so does the pane
header row. Each pane gets, under its tab strip, a breadcrumb row with
Back, Forward and Up, long paths collapsed to `Drive › … › parent ›
current`, and Ctrl+L to edit the path in place. Find becomes a per-pane
widget on Ctrl+F that filters the current tab live; Quick Open on Ctrl+P
finds files and folders of the workspace in the palette's surface, `>`
switching to commands. Tabs per pane (Phase 12) stay and carry the rest of
the tab state (selection, scroll position, find query). Themes may set the
new metrics and may not remove the new elements. Reference build:
`_io/design/Compact_Theme/CabinetOS Compact.dc.html`.

Consistency: Article 3 (the single top row is the Windows 11 shape),
Article 4 (find and Quick Open appear on demand), Article 5 (the panes are
untouched), Article 7 (every new control is a command with a key; the
immutable tier stays), Article 10 (Quick Open is a view over the core's
existing `search`; the branch in the pill is one read of `.git\HEAD`, no
git process). Decisions made where the handout was silent: Ctrl+P moves
from `terminal.insertPath` (Total Commander's key) to Quick Open, as the
handout asks, and the terminal's path key becomes Ctrl+Alt+P
(Ctrl+Shift+Enter for the selected paths stays); Ctrl+F stays
`search.focus`, which opens the pane's find widget; the window keeps its
one workspace, no workspace model is built; a key of the handout's table
already taken by another command stays with that command and is reported.
Done when the handout's §7 acceptance list passes: window tests for the
collapse rule, the tab state restore, the find filter's scope, Quick Open,
the hamburger, the keys, the 640 px rule and the nav buttons; the live
check updated for the removed elements, with a section 16 for the new
keys; the guides (ui.md, keybindings.md, config.md, the themes) and the
CHANGELOG. Handed to a coder on Opus on 2026-09-30; the first release
(Phase 10) waits for it.

**Status (2026-09-30, afternoon): done.** Landed as ffcdea3..ff2f84d by a
coder on Opus. The window's own read of `.git\HEAD` (its decision 2)
broke the first Prime Directive and moved into the core the same
afternoon as `workspace_info` (2656e01); Commander Compact is 1.1.0 for
the eleven new metrics (508eeba); the live check's section 16 had six
evidence faults of its own (a wrong field, log lines read before the
writer's thread had written them, a fixture that was no workspace), fixed
by a coder on Sonnet (7fdd634). The checks: the five core checks green
(735 tests), 890 then 913 window tests, the Claude Code probe every check
True, and the live check with real keys on the Release build of 15:42
with Phase 17 inside: 127 True, 0 False, section 16 all nineteen True.
The thirteen decisions the handout did not cover, with their undo, are in
[log/2026-09-30/phase-16-shell-redesign-report.md](log/2026-09-30/phase-16-shell-redesign-report.md);
the one to know: the workspace is the git repository holding the active
folder, else that folder, until a workspace model exists. Ctrl+P is
Quick Open and the terminal's path key is Ctrl+Alt+P.

### Phase 17 — In-app updates (the creator's request of 2026-09-30, the desk card "In-app updates"; started the same day) — done 2026-09-30 (publishing waits for the creator)

The creator asked for updates from inside the app: an Update button, the
update running in the app with progress shown, the release notes of the
version shown to the user, and never the install menus again. Today there
is nothing for it: Phase 10 ships a zip with `install.ps1`, and "no
automatic updates and no Settings > Apps entry" is a known gap (ADR 0009).
Two question rounds on 2026-09-30, every answer the recommended one:

1. **Source.** The zip and its SHA-256 on the public repository's GitHub
   Releases; a small `latest.json` per channel on the marketplace site
   (`oliverd25.github.io/cabinetos-marketplace/update/<channel>/`), an
   address the app already trusts, with the newest version, the zip's
   address, its hash, the notes' address and the runtimes it needs.
2. **Flow.** A quiet check once a day at start and on demand; the zip
   downloads in the background with a status-bar pill; a dialog shows the
   version and its release notes with Restart now / Later; the swap
   happens at that restart; the previous version stays for a rollback
   command.
3. **Channels.** `update.channel`: `stable` (default) or `preview`, each
   with its own `latest.json`.
4. **Scope.** The per-user install (`%LOCALAPPDATA%\Programs\CabinetOS`)
   is what the in-app update serves; an all-users install keeps winget or
   the installer, and the app says so. The installer allows the indexer
   service only with the all-users install, so a per-user install has no
   service to replace; the elevation branch the creator accepted is not
   needed until an installer allows a per-user service.
5. **Notes.** The CHANGELOG section of the version, published as
   `notes.md` next to the zip and rendered in the dialog, with a link to
   the full changelog: one source of truth.
6. **Later.** Snoozes a day; a dot with the waiting version on the menu
   and in About; the palette's update command any time; no popup at every
   start.
7. **Order.** The updater is built first, in parallel with Phase 16; the
   first release is cut with it inside, so version one can update itself.
8. **Apps entry.** The per-user install registers in Settings > Apps
   (version, publisher, uninstall) under the user's hive, no elevation,
   and the updater keeps the version current.

Decided by the planning session: full zips, no delta downloads; HTTPS
only and the hash from `latest.json` as the guard, an Authenticode check
when the creator signs; the swap is rename-first (the running files are
moved to `previous\`, the new ones copied in, the old ones kept one
version back), so a failed copy leaves the old version intact; the
updater refuses to touch a folder without `release.json` next to the
core, so a development build never overwrites itself. Consistency:
Article 6 (`update.*` in cabinetos.json), Article 7 (`update.check`,
`update.apply`, `update.rollback`, `update.showNotes` are commands),
Article 10 (product infrastructure like the marketplace, small, in the
core; one dialog and one pill in the window), Article 12 (every step
logged with the trace id), Article 2 (the source is the public
repository). Done when: `latest.json` and `notes.md` come out of the
release script with a schema and a test; the core checks, downloads with
progress, verifies, stages, swaps and rolls back, all against a local
`file:` source in tests, including a swap while a program of the install
runs; the CLI has `update`; the window shows the pill, the dialog with the
rendered notes, the badge and the snooze; the Apps entry appears and
disappears with install and uninstall; ADR 0014 and the guides
(release.md with the publish steps, config.md, ipc.md, ui.md) are
written. Handed to a coder on Opus on 2026-09-30, the window's part last,
after Phase 16 lands.

**Status (2026-09-30, afternoon): done; publishing waits for the
creator.** Landed as 4e236f4..beb95db by a coder on Opus: the
`cabinetos-update` crate (check, download with progress, SHA-256,
rename-first swap, rollback, snooze, the Apps entry in the user's hive),
protocol 14 with six `update_*` requests and two events, `cabinetos-cli
update`, the `update.*` settings, `release.ps1 -Channel` writing
`latest.json` and the notes, the pill, the dot, the dialog with natively
rendered notes and the restart in the window, ADR 0014 and the guides.
The sixteen unattended decisions with their undo are in
[log/2026-09-30/phase-17-in-app-updates-report.md](log/2026-09-30/phase-17-in-app-updates-report.md);
the ones to know: the core sends the notes' text, so the window does no
file or network work; plugins may only ask for the update status; a
running transfer blocks the restart; rollback asks first; the dialog
opens by itself only once per downloaded version. Checked on the main
checkout: the five core checks (772 tests), 913 of 913 window tests with
the end-to-end tests on, the release script (0.1.0: the zip 68.7 MB,
`latest.json`, `notes-0.1.0.md` from the version's section, winget
validation), and the live check with real keys, 127 True, 0 False. The
CHANGELOG's Unreleased section became `## [0.1.0] - 2026-09-30`
(4b102b6), so the first release is built, unsigned, in `dist\`. Not
verified: a restart into a truly newer build, since every build is
0.1.0; the first real update, 0.1.0 to 0.1.1, is that proof. Needs the
creator, each step outward-facing, the commands in [release.md](release.md)
under "Publish": sign, make the repository public, tag `v0.1.0` and
publish the GitHub Release, copy `latest.json` and the notes into the
marketplace repository's `update/stable/` and push, and push the
marketplace clone that holds the Agent extension and Commander Compact
1.1.0.

### Phase 18 — The context menu from the config (the creator's card "High-Performance Context Menu", 2026-09-30; started the same day, after Phases 16 and 17)

Goal: the right-click menu comes from `cabinetos.json`, so a user shapes
it in the file or, in step 2, inside the menu itself; every entry is a
command with a name and a key; a user's own programs join the menu from
a named list; and the Windows shell menu is one opt-in gesture away.

Decided 2026-09-30 with the creator, on the card's open questions: the
Windows shell menu on Shift+right-click is an opt-in setting, off by
default, served by the core on a background thread, never an extension
(a plugin cannot reach the shell interface today; Articles 1 and 10);
menu entries may start only programs named in a `programs` list of the
config, with the tokens `{path}`, `{selection}` and `{cwd}`, anything
else refused and logged (Article 6, and the config is the one place to
review); the quick-actions icon row sits at the top, as in Windows 11
Explorer; the default menu is today's menu written into the config
defaults, so nothing changes on the update; the list order is the
config's entries, a divider, the FROM PLUGINS group, Properties last;
the in-menu edit mode is step 2 of the same card, after step 1 is
verified with real keys. Decided by the planning session: each program
becomes a command `program.<name>` in the registry (Article 7), so the
menu has one entry kind; the brief's `{selection_file}` and
`--selection` tokens are not built, since `window_state` already gives
a command the marked files; the menu control is WinUI's
`CommandBarFlyout` as the brief asks (Article 3); the protocol goes to
15 for `shell_menu` and `shell_menu_invoke`, because 0.1.0 is built with
14; the Phase 16 dropdowns keep their surface.

Produces, step 1: in the core, `contextMenu` (four targets with
`quickActions` and `items`, `shellMenu`) and `programs` in the config
with today's menu as the defaults and the schema regenerated; the
dynamic `program.<name>` commands, `menu.showShell` (Ctrl+Shift+F10) and
`menu.edit`; the tokens resolved from the window state and the program
started through the `ShellExecuteExW` path of `cabinetos-fs`; the shell
menu built on a COM background thread in `cabinetos-fs` and answered
within 3 s; protocol 15. In the window, a pure menu model, tested
without XAML; the row and background menus as a `CommandBarFlyout` with
the icon row, the list, the plugin group with badges, Properties and
"Edit Menu…"; the shell menu as a second flyout. Tests on both sides, an
end-to-end test, the live check's section 18, the guides, the CHANGELOG
and ADR 0015. Step 2: the in-menu edit mode.

Done when, step 1: a fresh config shows today's menu unchanged; an
entry added to `contextMenu.file` in the file shows at the next
right-click without a restart; an entry with `extensions` shows for a
matching file and not for another; a `programs` entry runs the program
with the focused path and appears in the palette as `program.<name>`;
a program not in the list is refused and logged; with `shellMenu` on,
Shift+right-click shows the Windows menu of the file and a click runs
its item, with the window's frames unaffected; with it off, the gesture
opens the normal menu; the five core checks, the window's tests with
the end-to-end tests on, and a live check with real keys (section 18,
plus every earlier section) pass.

Articles: 1 (the shell query off the UI thread), 3 (the native flyout),
6 (the config mirrors the menu), 7 (every entry a named command), 10
(the shell menu opt-in). Handed to a coder on Opus on 2026-09-30.

**Status (2026-09-30, night): done.** Step 1 landed as 49ca8b7..cc8d5b3
and step 2 as acc0e30..4e20903, both by a coder on Opus: `contextMenu`
and `programs` in the config with today's menus as the defaults, the
`program.<name>` commands, Windows' menu on a COM thread of its own in
`cabinetos-fs`, protocol 15, the pure menu model and the
`CommandBarFlyout` in the window, ADR 0015; then the edit mode inside
the menu (an in-window overlay: reorder by drag or Alt+arrows, remove,
Insert or "Add Command…" through the prompt, Done or Ctrl+S saving
`contextMenu.<target>.items` through the core, Cancel or Esc). The same
coder fixed a step 1 fault it found (a menu of a shape whose flyout was
still closing never came back, because the cached flyout was re-shown
inside its own Closed handler; now shown one dispatcher turn later,
with a test that failed before: 9046741, b7fdf19) and proved with a
test that Windows' menu cannot meet it, since the core's answer always
comes after the old menu has closed. Checked on the main checkout: the
five core checks (795 tests), the window's build with warnings as
errors, 964 of 964 window tests with the end-to-end tests on, and the
live check with real keys on the Release build of 21:57
(`run-2026-09-30-2209.txt`): 159 True, 0 False, section 18 all 32 True,
the edit mode included with a real drag; every earlier section True;
the scroll goal met. Earlier runs the same evening: 143 True and 2
False (the section's own reading, fixed), one stopped by the creator's
own use of the PC, then 144 True and 1 False. Windows' menu answers 30
items in about 760 to 820 ms and its Copy reaches the clipboard. The
coders' decisions with their undo (fourteen for each step) are in
[log/2026-09-30/phase-18-context-menu-report.md](log/2026-09-30/phase-18-context-menu-report.md);
the ones to know: the edit mode holds the keyboard like a dialog; Done
with no change writes nothing; unknown IDs show greyed so they can be
removed; quick actions, `extensions` filters and `programs` are edited
in the file only. Left for later: the first opening of each menu shape
costs one frame of 45 to 60 ms (a pre-build while the window is idle
would remove it); Ctrl+V does not yet paste files that Windows' Copy
put on the clipboard; a dialog that one of Windows' items opens may
open behind the window.


### Phase 19 — Files Type Sprint, the keys audit and the speed review (the creator's card "New features I want to build Files Type Sprint" and two requests of 2026-10-01 00:35; built in the sleep-mode run of 2026-10-01)

The card lists five features of the Files app and asks for what CabinetOS
does not have. The creator went to sleep with "go on with Files Type
Sprint", then added two requests: "double check the shortcuts, I felt like
they don't work all the time", and "double check the speed of the app, and
where it could be improved". The planning session decided the order alone
(the creator's rule for the night: decide and record): the audits first,
because a shortcut that sometimes does nothing and a slow spot touch every
feature; the two small items of the card next; the column view after them;
the tags planned now and built only if the night has room, because their
storage format is a decision the creator will live with. Item 4 needs no
work.

- **19a, item 4, real-time filtering: done by Phase 16.** Find in pane
  (Ctrl+F) filters the current tab live as the user types, through the
  core's `match_entries`, with no index ("Find in pane" in ui.md). Closed
  on the card with that reference.
- **19b, item 6, folder sizes for every folder of a listing.** Today a
  folder is measured on demand (Space, Calculate Folder Size, Calculate
  All Folder Sizes; Phase 11a). New: the setting `panes.folderSizes`
  (default `false`, Article 6) and the command `view.toggleFolderSizes`
  (Article 7, no default key). While it is on, every folder of a listing
  is measured when the listing opens or is listed again, through the same
  `measure_paths` path, in the listing's order, folders counted already
  skipped; the Size column shows the running totals and the status bar
  the sum, as today. A pane that leaves the folder cancels its running
  measure (`cancel_measure`), so a walk of a large tree does not go on for
  a folder nobody looks at. The core does the walking on its blocking
  threads (Article 1); the setting is opt-in because the walk costs disk
  time (Article 4). Done when: with the setting on, a fresh listing shows
  every folder's size without a key; the toggle writes the file and a
  hand edit applies live; leaving the folder cancels; the core's checks,
  the window's tests with the end-to-end tests, and a live check step
  pass; config.md, ui.md ("Folder sizes"), keybindings.md and the
  CHANGELOG say it.
- **19c, item 5, compact overlay.** The command `view.toggleCompactOverlay`
  ("Toggle Compact Overlay", Ctrl+Alt+Up as Files binds it, unless the key
  is taken) makes the window a small always-on-top drawer: the overlapped
  presenter's always-on-top flag, one pane, no sidebar, no dock, the size
  saved as `ui.compactOverlay` (`width`, `height`, default 480 by 640
  DIPs) when the user resizes it in that mode, the minimum width lowered
  to 360 for the mode. Toggling back restores the previous size and place
  and the sidebar, dual pane and dock as they were, without writing those
  three to the file: the mode is not a setting. The status bar names the
  mode and the key to leave it. Not the `CompactOverlay` presenter kind:
  it keeps a video-like aspect and its own caption, which does not fit a
  file drawer. Articles 4 (opt-in), 6 (the size in the file), 7 (a
  command with a key). Done when: the command toggles both ways with the
  size, place and layout restored; the saved size returns at the next
  entry; the end-to-end test reads the window's "compact overlay entered"
  and "left" lines and the topmost flag; a live check step presses the
  key with real keys and reads the window's size and topmost style.
- **19d, the keys audit.** The creator's report: shortcuts do not always
  work. A coder on Opus reads the whole key path (MainWindow.Keyboard.cs,
  the chord machine, the keymap's contexts `filesView`, `textInput`,
  `terminalFocus`, `paletteOpen`), lists every place the keyboard can be
  (the list, a breadcrumb box, the find widget, Quick Open, the palette,
  a prompt, the theme picker, the plugin list, the review, the
  marketplace, an open context menu and the edit mode, the terminal, a
  tool page, the sidebar's tree and rows, the rail, a dialog, a rename
  box, a quick search, the drive list, the moment after a menu, flyout or
  dialog closes, after Alt+Tab, and a WebView2 page), and for each says
  which keys of the four kinds (pane keys, global keys, chords, the
  Immutable System Tier) reach their command, by reading and by driving a
  real window with real keys. Every fault found is ranked by how often a
  user meets it; the clear ones are fixed with a test that failed first;
  the rest are proposals. A second input language is tried, because the
  creator types Ukrainian: a key named after a Latin letter must work on
  the Ukrainian layout. Produces the report in the build log, the fixes,
  and live check steps for the states that were wrong.
- **19e, the speed review.** A coder on Opus measures, on the release
  build, with the window's own log and frame statistics: the start (the
  process start to the first "listing shown"), listing a folder of 100,000
  files and one of 10,000 folders, scrolling both, a tab switch, a theme
  change, the first and later opening of the context menu, Quick Open and
  Find in pane on a large tree, the marketplace's first view, the core's
  request latencies by type from a real session's log, the idle CPU of
  the window and the core over a minute, and the working set after the
  above. Produces `speed-review.md` in the build log: a table of numbers
  with the method for each, the findings ranked by what a user feels,
  the fixes that are safe now implemented with tests (for example a
  pre-built context menu while the window is idle, the known 45 to 60 ms
  first frame), and the larger ones as proposals with an estimate.
- **19f, item 2, the column view (Miller columns).** A pane's tab gets a
  second mode, `columns`: the folder's entries in a narrow name-only
  column; opening a folder (Enter, Right, a click) adds a column to its
  right with that folder, the earlier columns staying, so the whole path
  is on screen; Left goes to the parent column; the breadcrumb row and
  the tab's path follow the deepest column; columns scroll sideways and
  the newest is kept in view; each column is one `list_directory` with
  `watch`, released when its column goes; marks and the cursor are per
  column, and the commands act on the deepest column with the keyboard.
  The last column shows no preview: a viewer is an extension (Article
  10). The dual pane stays the base (Article 5): either pane may be in
  columns. `view.toggleColumns` (Ctrl+Alt+C unless taken) switches the
  active pane's tab; the mode is saved with the tab (`ui.tabs`). Column
  width is the theme metric `columnViewWidth` (220 px default, 180 in
  Commander Compact). Done when: the modes switch both ways with the path
  kept; three levels open and close with the keys and the mouse; the
  watch of a released column ends (the core's listing count); the tests
  and a live check section with real keys pass; ADR 0016 records it.
  Handed out after 19b lands, because both change the pane.
- **19g, item 3, native file tagging: planned, built last if the night
  has room.** The decisions the planning session would take: a tag is a
  name and a colour from a small fixed set; the tags of a file live in an
  NTFS alternate data stream `:CabinetOS.tags` (UTF-8 JSON) so they travel
  with the file when the core's own copy and move keep streams, and in a
  catalog in `%LOCALAPPDATA%\CabinetOS\tags\` keyed by path, so "every
  file with this tag" answers at once without the indexer; the catalog
  drops a path that no longer exists when it is read; the sidebar gets a
  Tags section (the design's `tagRadius` and `tagFontSize` metrics finally
  apply) whose click lists the tagged files in the pane as search results
  do; commands `tags.add`, `tags.remove`, `tags.manage` through prompts in
  the palette's frame, and the context menu's file target gets "Tags…";
  the Properties dialog shows the tags. Open for the creator: whether
  tags belong in the core at all or in a core plugin plus a tool pane
  (Article 10), and the colour set. Built only after 19a to 19f, with
  ADR 0017.

Consistency: Articles 1, 4, 5, 6, 7 and 10 as named above. Coders: 19b
and 19c on Sonnet (concrete plans), 19d, 19e and 19f on Opus (faults that
hide; a new component). One live check with real keys at the end covers
all of it, and one after 19f.

**Status (2026-10-01, night): done, the tags excepted.** 19a was closed
on the card with Phase 16's Find in pane. 19b landed as d352be2..2c63758
and 19c as 501493c..6addc78, both by a coder on Sonnet: `panes.folderSizes`
with `view.toggleFolderSizes`, every folder of a listing measured through
`measure_paths` and the walk cancelled on leaving; `view.toggleCompactOverlay`
(Ctrl+Alt+Up) with the size in `ui.compactOverlay` and the layout restored
on the way back. 19d, by a coder on Opus, in two rounds: f4eca09..7c32a1b
fixed five faults with a test that failed first (keys in dialogs, a held
chord key, Tab into a tool tab and inside the picker and the plugin list,
the chord notice) and wrote the state-by-key table with seven proposals in
[log/2026-10-01/keys-audit-report.md](log/2026-10-01/keys-audit-report.md);
the planning session decided the first proposal the same night, and round
two (02817cb..1973fe6) built it: in a text box a bound key that types
nothing runs its command, and the pane's own boxes count as the pane (the
cause of the creator's "they don't work all the time"); a held key runs
its command once; Esc and Ctrl+Shift+P keep their tier's meaning during a
chord's wait; the palette gives the keyboard back to the tool page it was
opened from. P2 (a tool page hands back the tab keys) and P4 (overlays
stack, and Esc closes the wrong one first) were built in the afternoon
(109435f, bb6fa2a; the evidence in round three of the audit, 4dfca43),
with the terminal keeping Ctrl+W and Ctrl+T and Tab staying the page's. 19e, by a coder
on Opus (f85db52..a15411c, the numbers in
[log/2026-10-01/speed-review.md](log/2026-10-01/speed-review.md)): one
crash fixed (the keyboard's menu on a row out of view ended the window),
the start about 0.2 s shorter (the core starts while WinUI builds the
window), the four common menu shapes built while the window is idle, Find
in pane 2.5 times faster; seven proposals A to G for the creator
(ReadyToRun at about 15 MB more download, the compact theme's relayout,
marketplace cards in parts, Quick Open's cap of 20,000 entries, a tab
switch that lists again, icons at start, a note of an unclean exit). 19f,
by a coder on Opus (2a600a7..ac5b3c4, ADR 0016 accepted): the column view
as a mode of a pane's tab, `view.toggleColumns` (Ctrl+Alt+C), one watched
listing per column, released when the column goes, the mode saved in
`ui.tabs`, the metric `columnViewWidth`; one departure from the ADR's
wording stands: the commands act on the keyboard column's folder, because
a command's rows and its folder must be one folder. 19g was planned, not
built: [ADR 0017](decisions/0017-file-tags.md) is proposed for the
creator's decision. The planning session's own fixes: 29d719f (a core
restart forgets the folder sizes it was counting) and b2b4305 (the live
check counted one log line as none in PowerShell 5.1, and held a key that
an earlier step had rebound). Checked on the main checkout: the five core
checks (805 tests), the window's build with warnings as errors, 1143 of
1143 window tests with the end-to-end tests on, and the live check with
real keys on the Release build of 03:14 (`run-2026-10-01-0314.txt`): 195
True, 2 False, both faults of the check, fixed in b2b4305; the run after
the fix (`run-2026-10-01-0330.txt`): 197 True, 0 False, no stop line, the scroll goal met with no frame over 20 ms, exit code 0. The coders' decisions
with their undo are in the hand-backs listed in
[log/2026-10-01/README.md](log/2026-10-01/README.md). Left for the
creator: the seven speed proposals, P2 and P4, and ADR 0017. Noticed and
left: many end-to-end tests read exactly one day's log file, so a run
that crosses midnight UTC fails them.

**19h, the speed proposals (decided 2026-10-01, morning).** The creator
answered the seven proposals of the speed review with "do as you
recommend". The planning session recommended all seven, each a measured
gain with a small or contained cost (Article 1): A ReadyToRun (a start
0.14 to 0.24 s shorter for about 15 MB more download, on a 69 MB zip),
B fewer rows made ahead (no frozen frame at a density change, 8 % less
scroll work; the live check must show no empty rows on a fast drag),
C the marketplace's cards in parts (the first view's frame from about
80 ms to about 20 ms), D Quick Open's walk limit raised with a notice
when a search stopped early, E the last tab's listing kept alive for
30 s (a tab switch on 100,000 files from 60 to 20 ms, for 10 MB kept per
pane), F the icons of the first listing drawn at the core's start, G a
log line when the previous run ended without closing (Article 12).
Handed to two coders in parallel: A, B, D, F and G on Sonnet (small,
concrete), C and E on Opus (a list made in slices and a listing's
lifetime, where a mistake hides). Each coder measures before and after
with `ui/livecheck/speed-review.ps1` and reports in `log/2026-10-01/`.
The status follows.

**Status (2026-10-01, midday): done; two goals partly met.** All seven
landed, A, B, D, F and G as 38960c3..96390b6 by a coder on Sonnet, C and
E as 1dca09d..68c999f and the C follow-up as cd3e597..8c0cafd by a coder
on Opus; the numbers are paired runs of `speed-review.ps1` against the
morning's build, medians. A: the start 200 to 280 ms shorter (868 ms to
two small folders, 940 ms to a 3,000-entry folder); the window's folder
41 to 57 MB, the zip about 5 MB larger, not 15. B: the Commander Compact
change's frozen frame 106 to 79 ms, and 2.5 % of frames over 20 ms in a
100,000-file scroll instead of 5.2 %. C: 15 cards made at once instead of
50 and the rest in slices; the opening's longest frame 116 to 52 ms, then
42 ms once the view is laid out hidden while the window is idle; the
goal of no frame over 33 ms is not met, because the window's own opening
work and the first cards' frame each stay near 40 ms (two ideas left in
the report: a first slice of one row, or the index reply read a turn
later). D: the walk visits 200,000 entries (was 20,000), a Ukrainian query
on the 100,000-file fixture finds 50 rows instead of none, and Quick Open
says when a search stopped early. E: a tab switch to 100,000 files shows
the tab in 6 ms instead of 76, with no new listing; the kept listing is
released on a refresh, a loss, a core restart, a close or after 30 s.
F: an icon request answers in 0.2 ms instead of 26 (median of 85), and the
first folder's last icon arrives 64 ms after its rows instead of 106.
G: a killed window is reported once at the next start, proved by a test
that kills one. Checked on the main checkout: the five core checks (811
tests), the window's build with warnings as errors, 1175 of 1177 window
tests with the end-to-end tests on, the two failures the known flaky
context-menu tests that passed 8 of 8 alone, and the live check with real
keys on the Release build of 12:35: 202 True, 0 False, the scroll goal met with no frame over 20 ms, the Commander Compact scroll bar's thumb thrown from the top to the bottom with the real mouse took the list to 100 % and left no empty row one second later (the screenshot `compact-scrollbar-drag-live.png` among the live shots), both tabs took their kept listings back, exit code 0 (`run-2026-10-01-1240.txt`). The coders' decisions
with their undo are in
[log/2026-10-01/speed-items-abdfg-report.md](log/2026-10-01/speed-items-abdfg-report.md)
and
[log/2026-10-01/speed-items-ce-report.md](log/2026-10-01/speed-items-ce-report.md);
the ones to know: the Quick Open note points to the indexer, not to a
longer text; a `listing_refreshed` for a kept listing releases it; the
marketplace's preparation runs only after one second with no input.
Noticed and left: Quick Open's walk collects every match before it cuts
to 50 (a bounded heap would fix it); several end-to-end tests wait a fixed
time and fail under load; the sidebar-tree test at a rail start failed in
every full run on the loaded machine and passed on the quiet one.

**Status (2026-10-01, afternoon): the machines for the live check, and
the leftovers closed.** Quick Open's walk now keeps only the best 50
while it walks (f308b71, a bounded heap). Two rules in CLAUDE.md, from the
creator's morning at a PC whose keyboard the coders' runs kept taking: a
countdown window shows five seconds before any run takes the keyboard and
mouse (`ui/livecheck/countdown.ps1`, cfe3b42), and while the creator
works at this PC nothing that opens CabinetOS windows runs here; such runs
go to another machine, or wait for the night or for the creator's word
that they are away (e17cfc4, 5b13a24). Two other machines run the live
check now ([ui.md](ui.md), "The live check on another machine" and "The
live check in a virtual machine"). The creator's Omen laptop, over SSH:
`remote-livecheck.ps1` sends the commits as a bundle and this PC's builds,
starts a scheduled task there (a process started over SSH has no desktop)
and brings the output home; a portability pass by a coder on Opus
(7bd88ff..38cff8f, the report in
[log/2026-10-01/live-check-portability-report.md](log/2026-10-01/live-check-portability-report.md))
made the check run on a stock machine with no Rust: the Agent plugin from
the committed fixture, the bench folders by `bench-folders.ps1`, the edge
fixture's long name through `\\?\`, hidden AppData folders, the drag by
UI Automation, a `trap` that closes the run's own window on a thrown
command (d0279e3). The laptop's run of 14:34 on 72f6b3d: 209 True,
0 False, every section to its end; the scroll goal not met there (16.2 %
of frames over 20 ms, worst 109.5 ms, a gap of about 90 ms every 150 ms
after the UI thread's work, which matches this PC's), because the
laptop's 144 Hz screen is driven by its integrated Radeon, not the GTX.
`remote-tests.ps1` (d1e2d60) runs the window's tests there the same way.
A VirtualBox VM "CabinetOS-LiveCheck" on this PC (Windows 11 Pro, a stock
user, the project's root as its shared folder X:, set up through guest
control only): `vm-livecheck.ps1` starts `vm-guest.ps1` on the VM's
desktop, which copies the tree and the builds from X: and runs the check
with `-Virtual` (72f6b3d: the frame-time checks answer "not measured in a
VM", neither True nor False), the output landing straight in
`_io\live-check` here. The VM's runner proved itself the same afternoon after four attempts, each with a cause now handled or written down (a `net use` that waited for credentials nobody would type, a guest control that answered nothing after a probe during a run, the WebView2 processes a killed window leaves behind, which hold the run's folder: `vm-livecheck.ps1 -Restart`). Its first full run reached 71 True and 3 False (`run-2026-10-01-1505-cabinetos-vm.txt`): the three in the "ask" section, where the plugin's preview did not come within the five seconds the check allows and Enter then opened `photo1.jpg` in Photos, which stopped the run before section 13; two steps that parse the window's whole log took 8 and 12 minutes there (seconds on this PC), so a VM run is about 25 minutes, with the log parsing the time to cut next. While the VM stays this slow, the laptop is the better second machine. The four end-to-end tests that fail under load went to a coder on Sonnet in the afternoon (307cc82..ef0232b, the report in [log/2026-10-01/e2e-fixed-waits-report.md](log/2026-10-01/e2e-fixed-waits-report.md)): three fixed and proven under a second suite and a 40-process load (the rail tree's look now waits for the first row on the screen; the menu reopen waits for the menu on screen and judges the overlap by log events, not by 50 ms; the edit-mode frame test starts the window up to three times and waits for a calm machine, since a 32 ms garbage collection lands in the measured frame on a loaded one); the dock test could not be made to fail and got a defence and a clearer message; the window's own test aids changed with them (the rail's logged look, `ContextMenuFlyout.IsSettled`). The full suite: 1193 of 1193 on a quiet machine, 1192 of 1193 on the final code with the shell test `The_top_row_fits_924_px...` failing, a flaky test outside the four that failed in 8 of 12 loaded runs and is the next to look at. The laptop's scroll gaps were run down the same evening ([log/2026-10-01/scroll-gaps-laptop.md](log/2026-10-01/scroll-gaps-laptop.md), seven runs of `scroll-keys.ps1` there through the new `remote-script.ps1`): they are a wait of the laptop's display path, which sleeps between pages and wakes in about 80 ms (the gaps come with no key at all, and a second window redrawing the screen every 7 ms makes the same hold meet the goal in full: 637 frames, none over 33 ms, 1.3 % over 20 ms), not the window's drawing. The creator chose the second way the same evening, and a coder on Sonnet built `-Panel` (9dbd2ab..fa8188c, the report in [log/2026-10-01/panel-switch-report.md](log/2026-10-01/panel-switch-report.md)): the gap numbers are still printed, the goal is judged by the frames' UI work (`busy_over_20ms` and `busy_over_33ms` in the window's frame stats), the laptop's wrapper passes it, and two laptop runs met it (0 and 1 frame with UI work over 20 ms of 255 and 300, none over 33 ms) with the rest of the check as before. Still for the creator: whether to run the two settings tests (the panel at 60 Hz, CabinetOS on the GTX); The two product points from the laptop runs were fixed the same evening by a coder on Sonnet (41f2069..2e60f73, [log/2026-10-01/tree-faults-report.md](log/2026-10-01/tree-faults-report.md)): a pick in the folder tree ran `go.toPath` twice because the window listened to the tree's event twice, once through the sidebar and once directly; and the tree now follows the active pane into a folder under a hidden one, showing the path's hidden folders dimmed and no other hidden folder (the planning session's call, Article 4; the creator may change the dim's strength, one constant), with 23 new unit tests and two end-to-end tests, 35 of 35 rail and sidebar tests on the laptop in three runs. Step 2 of the list, the two test flakes, landed the night of 2026-10-01/02 by a coder on Sonnet (a55f14e..f061537 after a rebase, [log/2026-10-01/flakes-report.md](log/2026-10-01/flakes-report.md)): the shell test's `find:` and `pane:` steps wait for the framework's events and the filter instead of a fixed 300 ms (under load XAML raised them 0.5 to 0.8 s late); the terminal page's keyboard hand-over check waited on a timer nothing held (collected before it ticked) and ran before the first frame, so it now waits on a task's timer and for two frames, with every check ending in a log line; a new `repeat-tests.ps1` runs a test again and again beside a load, and only two full test runs beside it (`-Suite 2`) showed the flakes; a crash at window close under load (the idle row-making step running while XAML is taken down, `RowFactory`) was fixed on the way, outside the brief and kept. On the laptop beside the load: the shell test 4 of 5, the terminal test 100 of 100 rounds; on this PC the full suite 1233 of 1233 (2 min 5 s). Step 3 landed at 02:30 by a coder on Sonnet (a6ecf31..43a4bb1, on main as ..6bafaa7 after a rebase, [log/2026-10-02/livecheck-log-reader-report.md](log/2026-10-02/livecheck-log-reader-report.md)): a `LogReader` class compiled inside `livecheck.ps1` reads only the bytes appended since its last call, matches each line once per pattern and parses JSON on demand; 18 helpers and 18 inline reads moved onto it (the terminal unit's one new read followed, 02:40); the first step judges its two Tab presses by the log; the run prints its total time. Before and after, on this PC and the laptop: identical True/False lines plus the new one, 7 min 51 s to 7 min 39 s here and 7 min 53 s to 7 min 27 s on the laptop. The log's share of a run is small on these machines (100 counts of a line: 5.2 s to 36 ms); the VM, where two steps took 8 and 12 minutes, was not run. All three steps of the list are done. A second round of the same kind landed at 07:02 as the merge b76d722 by a coder on Sonnet ([log/2026-10-02/e2e-flakes-2-report.md](log/2026-10-02/e2e-flakes-2-report.md)): the Quick Open test, the out-of-view context menu test and the three marketplace card tests waited fixed times for the core's answer, the menu's placing and the marketplace's slices, and now wait for the log lines and events (new snapshot steps `until:workspace`, `until:menu-placed`, `until:market-prepared`, `until:market-complete`); one race was in the product, a command palette that closed while its search was out set its highlight on a list no longer shown (2138100). On the laptop with `repeat-tests.ps1 -Suite 2` each went from 0 of 5 to 5 of 5; the full suite here 1284 of 1284 after the merge. A third round, on the evening of 2026-10-02 by a coder on Sonnet ([log/2026-10-02/e2e-flakes-3-report.md](log/2026-10-02/e2e-flakes-3-report.md)), took the four that were left (the column view's three levels, the kept listing's tab switch, the context menu's program run, the rail's view switch) and three more (the context menu's edit test and its place test, the rail's folder picks), and found one on the way (the first-menu test): each waited a fixed time for something the window announces by an event or a log line, and now waits for it (new snapshot steps, listed in `docs/ui.md`). Two faults were in the product and are fixed with a CHANGELOG line each: the context menu kept its size from a showing whose list popup was not drawn yet, so the next menu of that shape did not flip at the window's bottom edge, and in the menu's edit mode a late GotFocus of a replaced row moved the focus back, so a Delete after an Alt+Up took out the wrong row. On the laptop with `repeat-tests.ps1 -Times 5` the 15 tests went from 0 of 5 runs passed (beside two full test runs) to 5 of 5 in the last two batches, and the three that fail only beside three runs from 0 of 5 to 5 of 5; the full suite there is 1338 of 1339 twice, the one failure each time outside these tests (a speed test of the core's first reply, and a COMException from the command palette's list, a product fault that is open), and here 1339 of 1339 before the PC was held. One more flake was seen once and is not fixed: the menu-reopen test, when another test window takes the activation and WinUI closes its flyout. Phase 20, the shell redesign v2, was built the same night on the branch `shell-v2` ([log/2026-10-01/shell-v2-report.md](log/2026-10-01/shell-v2-report.md)) and merged into main the next morning, 2026-10-02 at 08:42, on the creator's word to proceed without them: the merge's own checks and two live checks are in its section below and in [log/2026-10-02/shell-v2-merge-report.md](log/2026-10-02/shell-v2-merge-report.md).

### Phase 20 — Shell redesign v2: the quiet top row, the sidebar header, the pane toolbar and path rows (the creator's `docs/design/SHELL_REDESIGN.md` v2, 2026-10-01)

The creator's v2 of the shell specification replaces v1 (Phase 16) where
the two differ, for every theme. The top row gets quiet: the menu, the app
icon, the title "CabinetOS · folder" (the active pane's tab in front), the
empty drag space, a small Quick Open chip (Ctrl+P) and the view buttons;
the workspace pill and the centred command center go. The workspace
switcher becomes the sidebar's first row (a dot, the name, the branch, a
chevron), its dropdown at the row's width, Ctrl+K Ctrl+W unchanged. Each
pane's breadcrumb row splits in two: a toolbar row (Back, Forward, Up, a
drive chip that opens the drive list, the drive's free space, Find, Open
with…) and a path row (crumbs that collapse after 5 parts in dual mode and
8 in single, a filter label `*.*` or `*query*`, Ctrl+L). The tab strip
becomes a recessed band, the tab in front a card in the toolbar's fill so
the two read as one surface, dividers between the other tabs, a folder
glyph on every tab, no accent line. The find widget drops from the toolbar
row's right end over the path row. Themes may set the new heights, and
Commander Compact sets the values the handout gives in brackets. Reference
build: `docs/design/CabinetOS Compact.dc.html` (Commander Compact's
metrics); the handout: [design/SHELL_REDESIGN.md](design/SHELL_REDESIGN.md).

Consistency: Article 3 (a calm title row as Windows 11 apps have, the tab
and its toolbar one surface), Article 4 (the find, the drive list and the
workspace dropdown appear on demand; the filter label only shows a state),
Article 5 (the dual panes are untouched; each pane's rows are its own),
Article 6 (every new size is a theme metric, in the schema and in the
JSON), Article 7 (every new control runs a command with a key or the
pane's own command; Tab still switches panes because the new chrome
refuses the keyboard; no binding changes), Article 10 (no new feature in
the core: the free space is the sidebar's `list_volumes`, the branch the
core's `workspace_info`; the core gets five metric declarations, nothing
else). Decisions made where the handout was silent: the taskbar's title
stays "CabinetOS" (the tests and the compact drawer find the window by
it); Open with… keeps its place in the toolbar but stays hidden, by the
creator's call of 2026-09-30, until a second editor exists; the toolbar's
Find runs a window-only `search.toggle` with the pane's index, and the
filter label runs `search.focus`, which now takes `{"pane": n}`; picking
the workspace goes to its root in the left pane (`go.toPath` with
`{"pane": 0}`); with the sidebar hidden, the dropdown opens under the top
row at the panes' left edge; the heights of the tab in front and the other
tabs follow `tabRow` (`TabLook.Heights`: 32 and 26 px at 36, the
handout's numbers, 26 and 22 at 28); the band is drawn around the tab in
front, never under it, so no seam shows; no soft shadow above the tab
(WinUI 3 has no box shadow for a plain element); the free space changes
with the volume list, not with each copy; the old metrics of the pill,
the command center and the breadcrumb row are still accepted and size
nothing, so themes written before stay valid. Done when the handout's §7
acceptance list passes with a test for each item; the window tests for
the collapse rule, the filter label, the drive chip, the title's room, the
tab look; the end-to-end tests for the top row at 924 and 620 px, the
toolbars, the five- and seven-part paths, the shared fill, the find's
place, the workspace row and its dropdown; the live check's section 16
clicking the toolbar's Up, the drive chip, the workspace row and the
Quick Open chip with the real mouse; the guides (ui.md, themes.md,
keybindings.md) and the CHANGELOG. Handed to a coder on Opus on
2026-10-01, evening.

**Status (2026-10-02, 01:05): built and checked, not merged.** On the
branch `shell-v2`, 63eb9d2..9ccad5d and this plan's commit be9d1f2, rebased on
main's 17e4c83, by a coder on Opus (its hand-back:
[log/2026-10-01/shell-v2-report.md](log/2026-10-01/shell-v2-report.md),
saved on main as 0877fa4); merging into main is the planning
session's step. The checks: the five core checks green (813 passed,
6 ignored); the window built with warnings as errors, 0 warnings; 1239
window tests, 1195 passed and 44 end-to-end tests skipped in the fast
run after the rebase, and 1239 of 1239 passed in the full run with the
end-to-end tests (00:16 to 00:19, before the last rebase, which brought
docs and scripts only); the live check with real keys on the Release
build of 00:55 (`run-2026-10-02-0055-shell-v2.txt`): 209 True, 0 False,
section 16's new real clicks (the toolbar's Up, the drive chip and Esc,
the workspace row and its dropdown at 224 px, the Quick Open chip) all
True, the scroll goal met. The decisions the handout did not cover, with
their undo, are in the coder's hand-back; the ones to know: the taskbar's
title stays "CabinetOS", Open with… stays hidden, every folder tab has
the folder glyph (v2 is newer than the call of 2026-09-30), and the old
metrics of the pill, the command center and the breadcrumb row are still
accepted and size nothing.

**Status (2026-10-02, 08:45): merged into main on 2026-10-02** after a
merge of `shell-v2` (never rebased, since it is pushed) into the main of
15ac6e1, on the branch `shell-v2-merge` by a coder on Opus (68b2b05, the
fix-ups 3248e57 and 02a25ea, and the report:
[log/2026-10-02/shell-v2-merge-report.md](log/2026-10-02/shell-v2-merge-report.md)).
Two text conflicts, `docs/ui.md`'s snapshot steps and the Quick Open
end-to-end test, kept both sides (main's waits, the branch's v2 steps);
main's text that still named the pill names the sidebar's workspace row.
The checks on this PC: the five core checks green (843 passed, 6 ignored)
and the release build; the window with warnings as errors, Debug and
Release, 0 warnings; 1304 window tests, 1251 passed and 53 end-to-end
tests skipped in the fast run, and 1304 of 1304 with the end-to-end tests
(3 min 31 s); both script parsers clean; the live check on a fresh
Release build, 245 True, 0 False with the scroll goal met
(`run-2026-10-02-0817-shell-v2-merge.txt`, the Agent extension not built
there), and again with the extension, 253 True, 0 False
(`run-2026-10-02-0828-shell-v2-merge-agent.txt`: main's 239 checks and
section 16's 14 new clicks; its scroll goal missed under 88 % machine CPU
from another build). Sections 16 and 21 all True in both runs.

### Phase 21 — Integrated Terminal Subsystem Sprint (the creator's card of 2026-10-01 20:34; started the same evening)

The creator's technical design brief for the terminal: shells in their own
processes under ConPTY (as today), and a strict wall between the file
panes and the shells. Every terminal session belongs to a pane, left or
right, and has a mode: **Locked** (the default: nothing the panes do
reaches the shell) or **Linked** (the shell follows its pane's folder, but
only through a prompt hook that runs when the shell draws its prompt, so a
running command or a half-typed line is never touched). The **Zero-Hijack
principle**: clicking or moving between panes never swaps the shown
terminal, never takes the keyboard from it, never writes to its input.
**Active summoning**: Ctrl+` in a pane shows and focuses that pane's
session, starting one in the pane's folder when it has none; in the
terminal it hands the keyboard back. **Split mirror**: Ctrl+\ in the
terminal splits the dock under the two panes, the left session under the
left pane, the right under the right, with badges in the panes' colours.
**GUI context for `cab`**: from a shell, `cab` gives the active, left and
right folders and the active pane's selection, and `cab move|copy
--selection --dest opposite_pane|<path>` runs the job on it. Keys in the
terminal's context: Ctrl+Shift+T, Ctrl+Shift+W, Alt+[ and Alt+],
Ctrl+Shift+C and Ctrl+Shift+V. Config under `terminal.*` in camelCase.
After a restart, the first Ctrl+` restores the tabs as they were (profile,
folder, pane, mode) with fresh shells. As the result the creator asked for
an installation file (a setup .exe for the first install) and updates
through the built-in updater of ADR 0014 with no wizard: the app says a
newer version exists, downloads and installs it in the background with the
status bar's progress pill, and asks only to restart.

The creator's decisions (the desk card "Integrated Terminal Subsystem
Sprint" holds them): drawing stays xterm.js in WebView2, a native control
is measured after the sprint; all four parts of the brief are in; the
restoration happens when the dock is first shown after a restart; following
only by the prompt hook, nothing typed, cmd.exe stays Locked; a new session
is Locked; the installer is Inno Setup around the release folder, updates
from the GitHub releases once the repository is public. The planning
session's: camelCase keys; `cab` is the live source of the GUI context, no
`CABINET_*` environment variables beyond the session's own; Ctrl+\ splits
only in the terminal's context, "Up to Root" keeps it in a pane.

Consistency: Article 9 (the terminal integration is the subject), Article
7 (every new action is a command with a key, in the terminal's context),
Article 11 (the terminal stays a Tool Extension in the dock; the core only
tracks sessions), Article 1 and the first Prime Directive (nothing typed
into a shell by a timer; the hook runs in the shell itself), Article 12
(every hand-over of the keyboard ends in a log line).

Six units, each with its tests, in order: 1. sessions bound to panes,
modes, summoning, the tab keys; 2. Linked by prompt hook; 3. the split
mirror; 4. the GUI context for `cab`; 5. config and restoration; 6. the
installer and the updates, tested in the VM.

**Status (2026-10-02, 03:00): unit 1 done.** Built the night of
2026-10-01/02 by a coder on Opus, 0fc1d4b..411f9e1 after a rebase
([log/2026-10-01/terminal-unit1-report.md](log/2026-10-01/terminal-unit1-report.md)):
protocol 16 (`terminal_open` takes the pane and the mode,
`terminal_set_mode`, `terminal_mode_changed`, `not_linkable`, `linkable`
in profiles; `terminal_sync_cwd` and the typed `cd` are gone), the CLI's
`term --pane`, `term mode` and `term list` with pane and mode; the window's
tab header with the `[Left]`/`[Right]` badge, the Locked/Linked toggle and
the caption; `TerminalSummoning` for Ctrl+`; the Zero-Hijack rule (reading
A: the window never moves the keyboard by itself, a user's own click in a
pane does move it there); Ctrl+Shift+T/W, Alt+[ and Alt+], Ctrl+Shift+C
and Ctrl+Shift+V (the paste goes through the window, since WebView2's
browser keys are off); live check section 21. Checks: core 821 passed;
window 1237 tests; the live check 209 True, 0 False (its two product faults,
the second Ctrl+` after a pane switch and the paste, found and fixed on
the way). The planning session's checks after the rebase: the five core checks green (821 passed, 6 ignored), the window build with warnings as errors, 1251 window tests (1202 passed, 49 end-to-end skipped), the full suite with the end-to-end tests 1251 of 1251 (2 min 6 s), and the live check on the rebased Release build 215 True, 0 False (`run-2026-10-02-0225.txt`); merged as 0fc1d4b..411f9e1.
**Status (2026-10-02, 04:40): unit 2 done and merged** (8f7ccfc..7dc6d22 after a rebase; the planning session's checks on the rebased branch: the five core checks green with 841 tests, 1254 window tests with the end-to-end suite 1254 of 1254, the live check 223 True, 0 False in 7 min 57 s, `run-2026-10-02-0430.txt`). Two coders started at 04:40: unit 3, the split mirror, on Sonnet, and on Opus an investigation of the fault both terminal coders saw with the real mouse (keys pressed right after a click from the terminal into a pane reach nothing; the end-to-end tests cannot see it, since they post keys to the window). The fault was found and fixed by 05:25 by that coder on Opus (04e77f1..0b717ef, [log/2026-10-02/terminal-click-focus-report.md](log/2026-10-02/terminal-click-focus-report.md)): Windows' focus was right after the click, but WinUI itself made a second focus move by the pointer, from the clicked pane to the window's root ScrollViewer, so the keys reached an element no pane holds; the window now refuses that move (`ClickFocus`, `OnGettingFocus`) and logs the refusal; a 40 s real-key probe (`ui/livecheck/click-focus-probe.ps1`) shows the fault and the fix, the live check's section 21 checks the keys after the click, 218 True, 0 False. Two end-to-end tests the full suite fails by turns (Quick Open; the out-of-view context menu) went to a coder on Sonnet at 05:30 with the flakes report's method. The unit 2 coder's own status follows.
Built the night of 2026-10-01/02 by a coder on Opus, 004cc26..6e9b195 and
the report's commit on `worktree-agent-a1d5e9555812c7418`
([log/2026-10-02/terminal-unit2-report.md](log/2026-10-02/terminal-unit2-report.md)):
protocol 17 (`terminal_pane_folder`, answered from the window's last
`window_state` in memory; the event `terminal_folder_changed`; `folder`
in `terminal_list`), `cab term cwd` and `CABINETOS_PIPE` (every `cab` in a
terminal reaches its window's core); the prompt hook for PowerShell
(`-NoExit -Command`, wrapping the user's own prompt, no profile file
touched) and WSL bash (`PROMPT_COMMAND` through `WSLENV`), with
`terminal.profiles[].hook` (`true`, `false` or the user's own code); the
OSC 9;9 folder report read by the output thread; the caption "in <folder>"
(`TerminalCaption`); live check section 21's prompt hook step. A linked
shell follows when it draws its next prompt; a half-typed line runs where
its prompt was drawn, and a `cd` of the user's own stays until the pane
moves again (both for the creator to confirm). Cost: one process start per
prompt, 19 ms median, 65 ms at the 90th percentile. Checks: core 841
passed, 6 ignored, clippy, fmt and deny clean; window 1254 tests (1203
passed, 51 end-to-end skipped); the full window suite with the end-to-end tests 1254 of 1254 (2 min 4 s); the live check 218 True, 0 False
(`run-unit2-0359.txt`). Seen on the way: in section 21's unit 1 step, the
keys after a click from the terminal into a pane open nothing, and its
check does not test the folder change (the report's "Seen on the way").
**Status (2026-10-02, 06:20): unit 3 done and merged** (on main as ..6af9fb6 after two rebases; the planning session's checks on the rebased branch: the five core checks green with 843 tests, clippy, fmt and deny clean, 1284 window tests with the end-to-end suite 1284 of 1284 in 3 min 29 s, the live check 231 True, 0 False in 8 min 0 s, `run-2026-10-02-0609.txt`; a clippy failure in unit 2's folder report that the planning session's check of unit 2 had missed was fixed on main first, d767ab9). The coder's own status follows. The split
mirror, by a coder on Sonnet, 4a9ee15..f72829d and the docs on the branch
`worktree-agent-ab43609202ad0fd61`
([log/2026-10-02/terminal-unit3-report.md](log/2026-10-02/terminal-unit3-report.md)):
Ctrl+\ in the terminal (`terminal.toggleSplit`, `when: terminalFocus`; in a
pane it stays Up to Root) splits the Tool Dock under the two panes, each
half with its own header and tab row, as wide as its pane and following
it; a pane with no session shows the hint "Ctrl+` starts a shell for this
pane"; one pane shown gives one half. `terminal.split` in `cabinetos.json`
(off by default, applied live, saved on a toggle). Alt+[ and Alt+], Ctrl+Shift+W
and Ctrl+Shift+T act on the half that has the keyboard or the active pane's
half; Ctrl+` in a pane focuses its half. The dock stays one WebView2, so the
keyboard hand-over is unchanged (the page gets a `view` message and tells
which terminal got the keyboard). The `[Left]` and `[Right]` badges have
colours of their own: the theme's `terminalLeftBadge` and `terminalRightBadge`
(optional, theme format 3), by default the accent and the accent's hue turned
by 150 degrees. The rules are a pure class, `TerminalSplitLayout`. Checks:
core 843 passed, 6 ignored, `cargo deny` clean; clippy `-D warnings` fails on
code of unit 2 that this branch does not touch and that is byte for byte
main's (`cabinetos-terminal` `report.rs`, four `match_same_arms`;
`cabinetos-cli` `tests/term.rs`, six `cloned_ref_to_slice_refs` and an
`Err(_)`); with those lints allowed the rest of the workspace is clean, and
`cargo fmt --check` passes; window
1279 tests, all 1279 with the end-to-end tests (3 min 29 s; 8 of 8 terminal
end-to-end tests, two of them new); the live check on a fresh Release build
224 True, 0 False (`run-unit3-b.txt`, 8 min 8 s; its first run, 223 True and
one False, was the paste check meeting a clipboard another program held).
**Status (2026-10-02, 09:25): unit 4 built, on its branch, not merged.** The
GUI context for `cab`, by a coder on Sonnet, e867060..a45cc75, the live check's typing fix and the docs on
the branch `worktree-agent-a391e4e33aea0d55d`
([log/2026-10-02/terminal-unit4-report.md](log/2026-10-02/terminal-unit4-report.md)):
protocol 18 (`gui_context`, answered from the newest `window_state` in
memory: the folders of both panes, the active pane's selection, the marked
rows or else the cursor row, and the cursor; `marked_total`, which the
window adds to a pane of `window_state` when it lists only its first 1,000
marked rows); in the shell `cab pane [--left|--right|--json]`, `cab
selection [--json]` and `cab copy|move --selection --dest
opposite_pane|<path>`, which runs the core's own job on the selection and
follows it as `cab copy` does (conflict policy `skip` unless asked), with
exit code 0 for success, 1 for a failure (also a selection the window cut,
which is refused, not copied in part) and 2 for no window or nothing
selected. No `CABINET_*` variables: each command asks the window's core.
Checks: core 867 passed, 6 ignored, clippy, fmt and deny clean; window 1287
tests; the full window suite with the end-to-end tests 1287 of 1287 in the
second run (the first had 1286: the Quick Open test hit a palette list race
unrelated to this unit; 3 of 3 alone), with two new end-to-end tests that
run the real `cabinetos-cli` in a real PowerShell of the window; the live
check 231 True, 0 False in 8 min 30 s (`run-unit4-d.txt`; section 21 types
`cab` into the shell). Open for the
creator: `opposite_pane` with one pane shown, the default `skip`.

**Status (2026-10-02, 10:21): unit 4 merged into main** (the merge 8b98ceb
onto the shell v2 main, with one text conflict, the day's log index, where
both rows are kept; main at 412bd22) after the planning session's own
checks: the five core checks green (867 passed, 6 ignored), the window
built with warnings as errors, 1252 fast tests; on this PC the end-to-end
suite 1307 of 1307 (3 min 51 s) and the live check 251 True, 0 False
(`run-2026-10-02-0951.txt`, the scroll goal met with no frame over 20 ms);
on the laptop the end-to-end suite 1305 of 1307 (the two context-menu
tests of the known flakes, `tests-2026-10-02-1003-rd-omen-laptop.txt`)
and the live check 259 True, 0 False (`run-2026-10-02-1010-rd-omen-laptop.txt`, judged on the panel: 1 frame of 319 with UI work over 20 ms, none over 33 ms). The creator's
rule of the morning, that the laptop's numbers are the market's numbers,
made the laptop runs part of the checks, and they found three faults in
the laptop scripts, fixed on main the same morning: an empty bundle
stopped `remote-tests.ps1` when a live check had sent the commits first
(c80f25c); neither script sent `cabinetos-cli.exe`, which the terminal's
shell needs next to the core, so section 21 answered False nine times
(6ec4e7c); and a window left alive by a stopped run held the Release files
against the next copy (cc99de9). Still open for the creator:
`opposite_pane` with one pane shown, the default `skip`.

**Status (2026-10-02, 12:00): unit 5 built, on its branch, not merged.**
Config and restoration, by a coder on Sonnet, 61d4257..4a4380c and the docs
on the branch `worktree-agent-a8a124e56be8f0285`
([log/2026-10-02/terminal-unit5-report.md](log/2026-10-02/terminal-unit5-report.md)).
The two open questions of unit 4 are answered as the creator decided:
`cab copy|move --selection --dest opposite_pane` is refused with exit 1
("the other pane is hidden; show both panes or name a path") while only one
pane is shown, for which `window_state` and the `gui_context` answer carry
`dual` (protocol 19; a window that does not send it means both panes), and
the default `skip` is recorded as confirmed. `terminal.restore` (on by
default) and `terminal.defaultMode` (`locked` by default; a profile that
cannot be linked stays locked) are new keys, and the re-audit of the
`terminal.*` keys found them all in the schema and in config.md, and the two
profile keys' doc comments now say when an edit applies. The window saves
its terminal sessions in `cabinetos.json` as `terminal.tabs` through the
core's `set_value`, as it saves `ui.tabs` (profile, folder, pane, mode and the
order of the tabs, the tab in front and each pane's front tab; a second after
any change and when it closes). At the first show of the dock after a
restart, with `terminal.restore` true and no session yet, the saved sessions
start again as fresh shells: a gone profile falls back to
`terminal.defaultProfile`, a gone folder to the home folder (one retry after
the core's `spawn_failed`: the window reads no disk), a link the profile
cannot have to locked; a session that cannot start is skipped; every step
has a log line. The rules are a pure class, `TerminalRestore`, with its own
tests. Sessions the core itself already runs win over the file (today a core
never outlives its window, so only the rule has a test). Checks: core 879
passed, 6 ignored, clippy, fmt and deny clean; window 1333 tests (1275 passed
and 58 end-to-end skipped in the fast run); the full window suite with the
end-to-end tests on this PC 1333 of 1333 in 3 min 57 s (a first run had a
changelog test of mine, since fixed, and two tests that fail by turns under
the suite's load and pass alone); the live check on this PC 253 True, 1
False in 7 min 58 s (`run-unit5-a.txt`; the False line is section 18's
clipboard step after Windows' own Copy, not the terminal) and on the laptop
262 True, 0 False in 8 min 57 s (`run-2026-10-02-1130-rd-omen-laptop.txt`;
the panel goal is met: 267 frames, none with UI work over 20 ms or 33 ms);
the end-to-end suite on the laptop 1331 of 1333 in 4 min 38 s
(`tests-2026-10-02-1140-rd-omen-laptop.txt`; the two failures are known
timing tests, the column view test and the context menu edit test, and none
is in the terminal). Open for the creator: which reading of "the core's own
sessions win" is wanted when a later unit lets a core outlive its window,
and whether the saved front tab or the pane pressed should win at the first
show.

**Status (2026-10-02, 11:58): unit 5 merged into main** (a fast-forward to
2ee3399, since main had not moved) after the planning session's own
checks on the branch: the five core checks green (879 passed, 6 ignored),
the window built with warnings as errors, 1275 fast tests; the coder's own
end-to-end suites and live checks on both machines stand as the window
runs (above). The two readings left open for the creator are the
defaults until they say otherwise. Noticed by the coder and left: `cab
pane --right` with one pane shown still prints the hidden pane's folder;
the `cab state` table does not show `dual`; the live check's section 18
reads the clipboard after a fixed 500 ms (the flake's cause); the rail
test `Every_way_of_picking_a_folder_in_the_sidebar_runs_go_toPath_once`
failed once under suite load here and is not in the flakes report yet.

**Status (2026-10-02, 14:55): unit 6 built, on its branch, not merged.**
The setup file and the updates that install themselves, by a coder on
Opus, 914c385..de733cb and the report on the branch
`worktree-agent-ad007f05e563d93ab`, with main (unit 5) merged in as
c185a7a ([log/2026-10-02/terminal-unit6-report.md](log/2026-10-02/terminal-unit6-report.md),
[ADR 0018](decisions/0018-setup-file-and-silent-updates.md)).
`build/setup.iss` (Inno Setup 6.7), compiled by the new step 8 of
`release.ps1` into `CabinetOS-<version>-win-x64-setup.exe`: per user with
no elevation into `%LOCALAPPDATA%\Programs\CabinetOS`, the .NET 10,
Windows App Runtime and WebView2 checks before the first page (a missing
one stops it with its winget command), silent with `/VERYSILENT`, and
Inno's own uninstaller, which removes the whole folder with what the
updater added and never the user's data. The updater keeps the setup's
Apps entry `CabinetOS_is1` current and its uninstaller in place.
`update.autoInstall` (on by default): a download whose SHA-256 is right is
swapped in at once, and the status bar says "CabinetOS <version> is
installed; restart to use it" with Restart now and Later; off brings back
ADR 0014's dialog. No protocol change. `ui/livecheck/vm-install-check.ps1`
proves the chain in the VM: all five steps passed in one run
(`DONE-install-2026-10-02-1425.md`: the setup in 8 s, the live check
against the installed program 256 True and 0 False, the update by itself
and the notice's Restart now into 0.1.1, the uninstall in 3 s); the seven
runs before it found three faults of the uninstaller (an empty folder left
behind twice, a running-program check through WMI that waited 22 minutes
after a VM restart), fixed, and taught the check to let a restarted VM
settle and to rebuild each version's programs. Checks after the merge: the
five core checks green (886 passed, 6 ignored), the window built with
warnings as errors, 1339 tests (1279 fast, 60 end-to-end skipped), the
full suite with the end-to-end tests 1339 of 1339 in its second run (the
first had one known context-menu timing test, 8 of 8 alone), the live
check on this PC 248 True, 0 False (`run-unit6-d.txt`, 8 min 42 s, no
frame over 20 ms). Found and left: section 18's clipboard line answered
False on this PC whenever the VM ran (also in unit 5's `run-unit5-a.txt`),
True with the VM's state saved; the core compares a plugin's paths with
its roots as text, so a short 8.3 path (`C:\Users\CABINE~1\...`) is outside
`%USERPROFILE%` for the Agent extension. Open for the creator: publishing
the setup file with the zip and `latest.json`, signing, and whether the
setup should download missing prerequisites.

**Status (2026-10-02, 15:00): unit 6 merged into main, and with it the sprint's
six units are all on main** (a fast-forward to 9352bcb; the branch had
merged main's unit 5 already) after the planning session's own checks: the
five core checks green (886 passed, 6 ignored), the window built with
warnings as errors, 1279 fast tests; the coder's end-to-end suite 1339 of
1339 and live check 248 True, 0 False on this PC, its VM chain of five steps
in one run (`DONE-install-2026-10-02-1425.md`), and the planning session's
live check on the laptop 262 True, 0 False (`run-2026-10-02-1449-rd-omen-laptop.txt`, the panel goal met: no frame of 266 with UI work over 20 ms). Two findings of the unit
wait for a decision or a later unit: section 18's clipboard check fails on
this PC whenever the VirtualBox VM runs (the VM's clipboard sharing; off
with `VBoxManage modifyvm CabinetOS-LiveCheck --clipboard-mode disabled`
while the VM is off), and the plugin host compares a plugin's paths with its
roots as text, so an 8.3 short path is taken as outside the user's profile.
Still for the creator: the GitHub Release with the zip, the setup file and
`latest.json`, the signing, and whether the setup should download missing
prerequisites. Both small findings of the unit are fixed afterwards
([short-paths-and-icon-report.md](log/2026-10-02/short-paths-and-icon-report.md)):
the plugin host compares a plugin's paths with its roots in their long form
(`cabinetos_fs::long_path`, `GetLongPathNameW`), so a short 8.3 path is the
same folder as its long one, and `CabinetOS.exe` has an icon of its own
(`ui/CabinetOS/Assets/CabinetOS.ico`, made by `build/make-icon.ps1`).

### Phase 22 — Release readiness (the creator's word of 2026-10-02, 20:56: "do all except the Linux core")

A short phase that makes 0.1.0 a clean first release, from the open items
of Phases 9, 10 and 21.

Produces, as six units: 1. a release without the Windows App SDK's AI and
machine-learning libraries that CabinetOS never uses (`onnxruntime.dll`,
`DirectML.dll` and their projections, about 40 MB unpacked and 17 MB
zipped; Article 10): the window's project references only the SDK
components it needs, and `release.ps1` stops when one of those libraries
turns up in the folder; 2. the `.pdb` symbols out of the release zip and
the setup file, into `CabinetOS-<version>-win-x64-symbols.zip` beside them
(38 MB less in the zip; a crash trace still names file and line when the
symbols are unpacked next to the programs, and `release.md` says how): the
planning session's recommendation, taken as the decision until the creator
says otherwise; 3. the indexer service starts by itself after a Windows
restart when it is installed (`cabinetos-indexer --install` registers it as
automatic with a delayed start; the installer's `-Indexer` keeps its one
UAC prompt); 4. publisher identities in the marketplace (Phase 9's open
item: `verified` is only shown): a design note first, saying what a
publisher is, how `verified` is earned and what the index carries, then
the smallest build that makes `verified` mean something, with ADR 0012's
index as the source; 5. the setup file downloads no prerequisites (decided
2026-10-02 on the planning session's recommendation: it keeps stopping with
the winget command); 6. the public-repository preparation, on the creator's
word only: a scan of the whole git history for secrets and private paths,
the README and the licence files read as a first visitor would, the release
notes, and the exact publish commands ready to run; the session publishes
nothing. Not in it: CI and the SignPath step (the desk card, after the
repository is public), winget's submission (the creator's, after the
release).

Done when: `release.ps1` makes a zip under 60 MB and a setup file under
35 MB with no machine-learning library inside and the symbols zip beside
them; the live check passes against the trimmed Release build on the
laptop and in the VM; a restart of the VM brings an installed indexer
service up by itself; the marketplace shows a publisher per package with
its identity from the index; the preparation report lists no secret.

Articles: 2, 3, 10, 12.

**Status (2026-10-02, 20:56): started.** Units 1, 2, 3 and 5 handed to a coder
on Sonnet; unit 4's design note goes to a coder on Opus after them; unit 6
waits for the creator's word on the public repository.

**Status (2026-10-02, evening): unit 6: prepared, waiting for the flip.**
The creator gave the word for the public repository. The scan of the whole
history found no secret; the one decision before the flip is the creator's
e-mail address in every commit's metadata, and the rest is private but
harmless ([report](log/2026-10-02/public-repository-preparation.md)). The
README is rewritten for a first visitor, and the publish commands, from the
flip to winget, are in [release.md](release.md), "Publish".

**Status (2026-10-02, 21:36): the repository is public.** The creator ran
the sheet's first two blocks: new commits from this PC carry GitHub's
private e-mail address, and `gh repo edit --visibility public` (in its
old form; Ubuntu's gh 2.45 refuses the new flag) answered PUBLIC; an
anonymous request for the repository and its README gets HTTP 200. From
here the SignPath card and CI can start, and the release steps (b) to (e)
wait for units 1 to 3.

**Status (2026-10-02, 21:44): unit 1 done, and it was in place already.** The
window's project has referenced only the Windows App SDK components it uses
(WinUI, Foundation, InteractiveExperiences and Runtime, at the 2.5.1
package's versions) since 2026-09-29 (6fdc435), so the unit's premise, the
whole `Microsoft.WindowsAppSDK` package, was out of date: the release folder
of 2026-10-02 held no `onnxruntime.dll`, `DirectML.dll` or AI or
machine-learning library, and the three attempts the handout allows for the
per-component route were not needed. What the unit added: `release.ps1`
stops before it zips when its folder holds one (tested with fake files), the
docs say which components and why ([dev-setup.md](dev-setup.md),
[release.md](release.md), "Sizes"), and the cost is measured, in a scratch
copy of the project: the window's publish with the whole package is 60
files, 100.3 MB, 34.7 MB zipped; with the components 45 files, 57.8 MB,
17.3 MB zipped (15 files, 42.5 MB and 17.4 MB less).

**Status (2026-10-02, 21:44): unit 2 done.** `release.ps1` moves every
`.pdb` file out of the release folder into
`CabinetOS-<version>-win-x64-symbols.zip` (43.6 MB, with its `.sha256`);
`setup.iss` excludes `*.pdb` besides; the updater's swap needed no change
([ADR 0021](decisions/0021-symbols-in-their-own-zip.md)). Release folder
71 files and 251.7 MB before, 66 and 100.5 MB now; zip 75.7 MB before, 32.1
MB now; setup file 42.5 MB before, 20.0 MB now: the goal (a zip under 60 MB
and a setup file under 35 MB, no machine-learning library inside) is met.
A crash trace, measured on a copy of the release folder: with the symbols
unpacked next to the programs every frame names function, file and line; without
them the Rust programs' `backtrace` is a list of `<unknown>` frames (the
panic's `location` and the log lines stay), and the window's names its methods
with no file and line. So a trace without symbols still names functions for
the window only, not for the Rust programs ([release.md](release.md), "The
symbols").

**Status (2026-10-02, 21:46): unit 5 done.** Recorded where the decision
lives: [release.md](release.md), "The setup file" (and its known gaps), and
the header comment of `build/setup.iss`. The setup file downloads no
prerequisite; it keeps stopping with the winget command (and, for the
Windows App Runtime, Microsoft's installer address), as `install.ps1` does.
Nothing else changed.

**Status (2026-10-02, 22:13): the public repository's description, topics and
private vulnerability reporting are set** (the optional block after step (a)
of the publish sheet, run by the planning session on the creator's word at
21:58: `gh repo view` shows the description and sixteen topics, and the
private-reporting switch reads true). The SignPath application has its
sheet in `docs/release.md`, "Sign", "Apply to SignPath Foundation": the
form's sixteen fields with proposed values, the two answers the creator left
to the session (the signer shown to users is "SignPath Foundation",
accepted; no second approver is needed), the conditions that matter (a
release must exist first, verifiable reputation, two-factor authentication,
a "Code signing policy" section, version resources for the four Rust
programs), and the order: the 0.1.0 release, then the README section, then
the form.

**Status (2026-10-02, 23:25): unit 3 built; its real check in the VM waits for
one manual step.** `cabinetos-indexer --install` registers the service as
automatic with a delayed start and starts it once (waiting up to 20 s for
Running; otherwise it exits with an error naming the state and the log
folder); `--uninstall` stays the reverse; `install.ps1 -Indexer` no longer
calls `Start-Service`. Made with `windows-service`'s
`set_delayed_auto_start` (`ChangeServiceConfig2W`, no `unsafe`), not the
`windows` crate the handout named. A unit test reads the registration, and
[ADR 0020](decisions/0020-indexer-service-starts-by-itself.md) records the
decision (it amends ADR 0009's manual start). In the VM: guest control gives
the VM user's token with UAC applied (medium integrity), `Register-ScheduledTask`
with the highest run level answers "Access is denied", and no UAC prompt can
be answered from outside, so the service could not be installed there by
script and nothing in the VM's security settings was changed to get round
it. `ui/livecheck/vm-indexer-check.ps1` and `vm-indexer-guest.ps1` do the
rest: run 1 stops with exit code 2 and the manual step; after one elevated
`-Phase install` in the VM, `-AfterInstall` restarts Windows there, waits,
and records when the service came up by itself. Tried with no service
installed: the restart (`shutdown /r`, or `controlvm reset` when guest control
is stuck), the settle wait and the read-only check phase all ran; the trial
found and fixed a hang (the guest helper `Sc` was shadowed by `sc`, the alias of
`Set-Content`). Not shown yet: the service `RUNNING` after a restart
([release.md](release.md), "The indexer service").


**Status (2026-10-02, 23:28): 0.1.0 is published, and the release-notes-page skill
is on main.** The creator ran the publish sheet's steps (b), (c) and (d)
themselves at 23:04 to 23:10: the CHANGELOG cut (9fcb599), `release.ps1`,
the tag `v0.1.0` and the GitHub Release with the zip, the setup file and
their hashes (no symbols zip: unit 2 was not on main yet), and the
marketplace site's `latest.json` for the stable channel. So 0.1.0 is the
state of main at 9fcb599, before units 1, 2 and 3 of this phase; they land
afterwards and need the creator's decision: a 0.1.1 soon after, or the next
version. The release-notes-page skill (merge 1863cc0, a coder on Sonnet from
the creator's brief): `.claude/skills/release-notes-page/SKILL.md`, the
capture script `ui/livecheck/release-media.ps1` with its list and the
converter `release-media-convert.py`; its dry run on the laptop made the six
clean media files of 0.1.0 over a `C:\Demo` folder, in
`_io/release-notes-dry-run/media`, for the Luminart site's page.

**Status (2026-10-03, 00:33): units 1, 2, 3 and 5 are on main** (merge c822076,
with the coder's branch verified again by the planning session: the five
core checks, both window builds, 1279 fast tests, and the live check on
the laptop). Three things came with the merge. `cargo deny` found seven new
advisories against wasmtime 49.0.1, the sandbox's runtime (RUSTSEC-2026-0321
to 0327); the merge raises it to 49.0.2. While the verification ran,
another Claude session committed 0.1.1 on main from the same checkout and
published it on GitHub at 00:20 (tag `v0.1.1`), with its own ADR 0019: the
setup file installs a missing prerequisite itself. That replaces unit 5's
decision that the setup downloads nothing; the creator's later action
wins, the merge keeps it, and the coder's symbols ADR moves to the free
number 0021. So 0.1.1 carries neither the symbols zip, nor the indexer's
automatic start, nor the wasmtime fix: they are the first content of
0.1.2. And one core test, the job progress rate over 10,000 files, failed
once under the load of the full run and passed three times alone: a timing
flake to make robust. Unit 3's claim, the service up by itself after a
restart, is still not shown in the VM: guest control cannot answer a UAC
prompt, so the install there is one elevated step by hand
([release.md](release.md), "The indexer service"). Unit 4 is answered by
the 11g decisions (the publisher is the pull request's GitHub login;
`verified` is the creator's badge).

**Status (2026-10-03, 04:17): 0.1.2 is published.** Built from 358eaa2 by
`release.ps1` on this PC (the zip 32.4 MB, the setup file 20.2 MB, the
symbols zip apart; the four programs carry their version resource;
release.json names the commit with no uncommitted change); the live check
of that build ran on the laptop (no check answered False; the panel goal
met: 334 frames, none with UI work over 20 ms); tag `v0.1.2` and the GitHub
release with six files at 04:14; `latest.json` and `notes-0.1.2.md` on the
marketplace site, read back with the zip's hash; `themes.json` published
there at 04:00, so 0.1.2's gallery reads the public catalogue. All on the
creator's word of 03:00 ("release by yourself", "do it by yourself"). The
release page for the Luminart site is built and committed there, not
deployed: the release-notes-page rule leaves the deploy to the creator.
The winget submission waits too: the 0.1.1 pull request at
microsoft/winget-pkgs is still open and the tool asks for a sign-in.

**Status (2026-10-03, 06:50): three merges after 0.1.2, the first content of
0.1.3.** The command palette's COMException under load has its cause and
fix (merge 99273ec, Opus): before the palette's first layout, a second
answer or a key re-added a recycled row to the list; the palette now waits
for the list's Loaded before it scrolls to the highlight, seven unawaited
tasks on its paths log a failure with its place, and an end-to-end test
reproduces the fault on purpose. The theme collection's round 2 is in
(merge be90a0d): Catppuccin Latte and GitHub Light ship with the core
(seven themes in the picker), sixteen ports join the collection (52
themes; Modus Vivendi, Modus Operandi, Zenburn and Bluloco Light left out,
GPL or LGPL), and the public `themes.json` lists 59 themes since 06:23.
The fourth round of test fixes (merge 071a620): the job clock's stopped time
stamp (a core fix: the clock stops under the emitter's lock and only the
final record follows), the update tests' fixture (it wrote an install of
0.1.0 beside a newer core; it reads the core's version now), and the hello
test's place (it runs in the collection that runs alone; the 60 ms limit
stays); the coder's end-to-end run on the laptop passed 1427 of 1427, the
first full green run there. Each merge verified on main: the five core
checks, both window builds, the fast tests, and the laptop's live check
(no check answered False; the panel goal met). The flakes merge's first
laptop run had one frame of 285 with UI work over 33 ms, so the panel goal
was not met; the rerun had none of 273, so the first was noise, and both
numbers are kept here. Open: the laptop's single
clone and the shared inbox of the remote scripts still let two sessions'
runs collide (a lock or one clone per branch), and one core test of the
GUI context read the log before the log thread wrote it, once.

### Phase 23 — The theme gallery and the first three-ways gaps (the creator's word of 2026-10-03: "do gaps 1 to 4 with the theme gallery")

Covers the desk cards "Themes get their own catalogue and gallery (split
from the extensions)" and "Settings reachable three ways" (gaps 1 to 4 of
the audit in the `settings-three-ways` skill). Before 11b, by the creator's
choice of 2026-10-02.

Produces: 1. two catalogue files from one build script, `index.json` for
plugins and tools and `themes.json` for themes, where a theme entry carries
`appearance` (dark, light or system), `density` and three tile colours,
read by the core with the same cache, ETag and trust rules
(`marketplace.themes`, default the public address); 2. the Extensions page:
today's marketplace page without themes (Discover, Plugins, Tools,
Installed; "Search extensions"); 3. the theme gallery: colour tiles, each
painted in its theme's background, text and accent, with name, author and a
dark or light mark; selecting a tile previews the theme on the whole window,
Esc restores the one that was applied, Install downloads it to the themes
folder and applies it; filters for dark, light and density presets, the
system's mode first; reached from the theme picker's last row "Browse more
themes" and the palette's "Themes: Browse" (`themes.browse`), while
`marketplace.browse` and the rail's button open Extensions; 4. the
three-ways gaps 1 to 4: "View: Classic Layout", "View: Terminal on the
Right" and "View: Activity Rail" with the current one marked, a "Layout"
item in the top row's menu and one chord that cycles; "View: Toggle Hidden
Files" with a menu item; "Sidebar: Follow the Active Pane" with a toggle in
the Explorer view's header; "Menu: Toggle Windows' Shell Menu" with a row
in "Edit Menu…"; each writes its key through `set_value`, and each way has a
test; 5. the documents: `marketplace.md`, `themes.md`, `config.md`,
`keybindings.md`, an ADR for the two catalogues, CHANGELOG lines that name
the three ways.

Done when: the marketplace site serves both files and the window shows the
Extensions page with no theme and the gallery with every theme of the
collection; a tile's selection previews live and Esc restores; a theme
installs from the gallery and the picker lists it; each of the four
settings changes the same way from the file, the palette and the window,
with a test per way; the live check passes on the laptop.

Articles: 3, 4, 6, 7, 8, 10.

**Status (2026-10-03, 03:35): built and checked on the branch `worktree-agent-aeba340e544ea5221`, waiting for the merge; after it the creator publishes `themes.json` ([report](log/2026-10-03/theme-gallery-and-three-ways-report.md)).**

**Status (2026-10-03, 03:51): merged into main as 18ef423, after Phase 24.** The
planning session merged the coder's branch over Phase 24 (eight conflicts:
both sides' CHANGELOG lines, the command count 125, the config crate's
re-exports, the audit's rows, config.md, the log README and ui.md's step
row) and verified the merge: the five core checks, both window builds and
1354 fast tests, all green. The coder's own live check on the laptop was
green at 4c287dc (287 True, 0 False, the panel goal met). The merge's own
laptop live check runs on the 0.1.2 release commit, which carries it: the
laptop's clone sat on another coder's branch at that moment and the remote
script only fast-forwards. The desk cards "Themes get their own catalogue
and gallery" and gaps 1 to 4 of "Settings reachable three ways" are done.
Next, by the creator's word of 03:00 ("do it by yourself", "release by
yourself"): `themes.json` goes to the marketplace site, and 0.1.2 is cut
from this commit with Phase 24, the version resources, the symbols zip,
the indexer's automatic start and the wasmtime fix.

### Phase 24 — Sorting by the column headers, and the pane dividers (small; the desk cards of 2026-10-02)

Covers the desk cards "Clickable column headers" and item 1 of "New
features" (the dragging of frames). Checked on 2026-10-03: a pane's column
headers have grips for the widths but a click sorts nothing, and of the
window's dividers only the Tool Dock's two and the rail layout's sidebar
divider can be dragged; the two panes always share the width equally, and
the classic layout's sidebar has no divider.

Produces: a click on Name, Modified, Type or Size sorts the active pane by
that column, a second click reverses it, and the sorted header shows an
arrow; the sort goes through `panes.sort.*` and `set_value`, so the headers,
the palette commands and the file agree; a grip click is a resize, never a
sort. A splitter between the two panes: `ui.paneSplit`, the left pane's
share of the width (0.2 to 0.8, `null` for equal), saved once per drag as
the dock's size is, double-click for equal, and "View: Equal Panes"
(`view.equalPanes`) for the keyboard; the sidebar's divider works in every
layout with `ui.sidebarWidth`, double-click for the design's width. Docs:
`ui.md`, `config.md`, `keybindings.md`, CHANGELOG lines that name the ways.

Done when: the header click and its reversal, the pane divider and the
sidebar divider each have a window test and an end-to-end test; the live
check drags both dividers and reads the saved keys; the laptop's panel goal
holds.

Articles: 3, 4, 6, 7.

**Status (2026-10-03, 00:46): handed to a coder on Sonnet, beside Phase 23.**

**Status (2026-10-03, 03:00): built on the branch `worktree-agent-a4c5f0b65676ff081`, not merged yet.**
The headings sort the pane through the key's own command, the divider between
the panes (`ui.paneSplit`, `view.equalPanes`) and the sidebar's divider in every
layout work and are saved. The core's five checks, the fast tests, the
end-to-end tests and the live check with the real mouse passed on the laptop,
and its panel goal holds. One premise above was wrong: `panes.sort.*` is the
order of a pane that has none of its own, and `view.sortBy*` never wrote it, so
the headings keep the per-pane order. Details and the list of known failures:
[the report](log/2026-10-03/header-sorting-and-pane-dividers-report.md).

**Status (2026-10-03, 03:10): merged into main as f0e042c.** The planning
session verified the branch again: the five core checks, both window
builds, 1314 fast tests, and the live check on the laptop (no check
answered False; the panel goal holds: 276 frames, none with UI work over
20 ms). The desk card "Clickable column headers" is done, and item 1 of
"New features" with it. Just before it, the three Rust programs got their
Windows version resource (merge a8e45ae; SignPath requires it before it
signs, and Explorer's Details page shows it). Still open from the coder's
report: three update tests of `ShellEndToEndTests` fail the same way on
main on the laptop (not caused by this phase; the flake unit after this
phase looks at them), and the laptop's single clone is shared by two
coders' runs, which collided once (one clone per branch, or a lock, would
stop it).

### Phase 25 — Quick View (the "New features" card, item 2; decided 2026-10-03)

A floating panel over the window, like macOS: Space on a selected file
opens it, Space or Esc closes it, Up and Down move to the next or previous
file while it stays open, Enter opens the file in its program; the panes
stay where they are. The panel shows Windows' thumbnail of the file at once
(the shell's image factory, the thumbnails Explorer shows), then the
viewer's full view replaces it. By Article 10 the core owns only Space and
the panel; every viewer is a Tool Extension from the marketplace, the
viewer pack of 11e pulled forward: images (Windows' image stack), video and
audio (Windows' media stack, with play, pause, seek and volume), text, code
and Markdown (the Markdown Preview tool reused), PDF and Office documents
(WebView2 for PDF; Windows' preview handlers, the ones Explorer's preview
pane uses, where they are installed). The first Space on a kind with no
viewer installed offers to install the free pack with one click.

Produces, in order: 1. a design note on Opus: the viewer contract between
the panel and a Tool Extension page (which kinds a page claims, how the file
is handed over, what the page reports back, the thumbnail-first rule, the
keys), as an ADR; 2. the panel in the core with Space, the keys, the
thumbnail and the offer to install; 3. the viewer pack in the marketplace:
images and media first, then text and Markdown, then PDF and Office.

Done when: Space on an image shows the thumbnail within 100 ms and the full
image within a second on the laptop; a video plays within a second; Up and
Down walk the folder; Esc and Space close; a kind without a viewer offers
the pack; the panel never blocks the window (Article 1); the live check
covers it on the laptop.

Articles: 1, 3, 4, 7, 10, 11.

**Status (2026-10-03, 00:46): planned, to start after Phase 23.**

**Status (2026-10-03, 08:06): the design note is ADR 0023 (branch `worktree-agent-a5904c72cd71567c9`), waiting for the merge.**

## 6. Phase 1 in detail — the Rust core scaffold

Moved whole on 2026-09-30 to [plan/phase-01-detail.md](plan/phase-01-detail.md):
the crates, the workspace settings, Phase 1's definition of done, and what it
leaves out.

## 7. Open questions for later phases (nothing here blocks Phase 0 or 1)

1. **Terminal rendering control (Phase 8).** Settled 2026-09-28: xterm.js 6.0 in WebView2 (Phase 5c), with the DOM renderer and a browser process of its own; a native renderer stays possible later.
2. **First-run layout (Phase 5).** Settled 2026-09-28 by the creator: dual pane on first start; see conflict B.
3. **Config comments (Phase 3).** Settled 2026-09-28: strict JSON for version 1 ([config.md](config.md)); every JSON tool can read it and there is one parser. JSONC stays possible later by stripping comments before parsing.
4. **Indexer install (Phase 6).** Settled 2026-09-28 by what was built: both exist. `cabinetos-indexer --install` registers the service (one UAC prompt; the start type was manual, and is automatic with a delayed start since 2026-10-02, [ADR 0020](decisions/0020-indexer-service-starts-by-itself.md)) and starts it once, `--console` runs it elevated for one session, and the installer's `-Indexer` switch registers the service ([ADR 0009](decisions/0009-packaging.md)).
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
