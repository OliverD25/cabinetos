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
`58fa975`, `c5ed4c8`): 0 of 1,207 frames over 20 ms in a 20 s hold. Last counts:
848 UI and 554 core tests. Open for the creator: a code-signing certificate, a
public repository, `gh release create`, the winget submission, and whether the
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

### Phase 16 — Shell redesign: one top row, per-pane breadcrumbs, find in pane, Quick Open (the creator's handout `_io/SHELL_REDESIGN.md`, 2026-09-30; started the same day)

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

### Phase 17 — In-app updates (the creator's request of 2026-09-30, the desk card "In-app updates"; started the same day)

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


## 6. Phase 1 in detail — the Rust core scaffold

Moved whole on 2026-09-30 to [plan/phase-01-detail.md](plan/phase-01-detail.md):
the crates, the workspace settings, Phase 1's definition of done, and what it
leaves out.

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
