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
`_io\live-check` here. The VM's runner proved itself the same afternoon after four attempts, each with a cause now handled or written down (a `net use` that waited for credentials nobody would type, a guest control that answered nothing after a probe during a run, the WebView2 processes a killed window leaves behind, which hold the run's folder: `vm-livecheck.ps1 -Restart`). Its first full run reached 71 True and 3 False (`run-2026-10-01-1505-cabinetos-vm.txt`): the three in the "ask" section, where the plugin's preview did not come within the five seconds the check allows and Enter then opened `photo1.jpg` in Photos, which stopped the run before section 13; two steps that parse the window's whole log took 8 and 12 minutes there (seconds on this PC), so a VM run is about 25 minutes, with the log parsing the time to cut next. While the VM stays this slow, the laptop is the better second machine. The four end-to-end tests that fail under load went to a coder on Sonnet in the afternoon (307cc82..ef0232b, the report in [log/2026-10-01/e2e-fixed-waits-report.md](log/2026-10-01/e2e-fixed-waits-report.md)): three fixed and proven under a second suite and a 40-process load (the rail tree's look now waits for the first row on the screen; the menu reopen waits for the menu on screen and judges the overlap by log events, not by 50 ms; the edit-mode frame test starts the window up to three times and waits for a calm machine, since a 32 ms garbage collection lands in the measured frame on a loaded one); the dock test could not be made to fail and got a defence and a clearer message; the window's own test aids changed with them (the rail's logged look, `ContextMenuFlyout.IsSettled`). The full suite: 1193 of 1193 on a quiet machine, 1192 of 1193 on the final code with the shell test `The_top_row_fits_924_px...` failing, a flaky test outside the four that failed in 8 of 12 loaded runs and is the next to look at. The laptop's scroll gaps were run down the same evening ([log/2026-10-01/scroll-gaps-laptop.md](log/2026-10-01/scroll-gaps-laptop.md), seven runs of `scroll-keys.ps1` there through the new `remote-script.ps1`): they are a wait of the laptop's display path, which sleeps between pages and wakes in about 80 ms (the gaps come with no key at all, and a second window redrawing the screen every 7 ms makes the same hold meet the goal in full: 637 frames, none over 33 ms, 1.3 % over 20 ms), not the window's drawing. The creator chose the second way the same evening, and a coder on Sonnet built `-Panel` (9dbd2ab..fa8188c, the report in [log/2026-10-01/panel-switch-report.md](log/2026-10-01/panel-switch-report.md)): the gap numbers are still printed, the goal is judged by the frames' UI work (`busy_over_20ms` and `busy_over_33ms` in the window's frame stats), the laptop's wrapper passes it, and two laptop runs met it (0 and 1 frame with UI work over 20 ms of 255 and 300, none over 33 ms) with the rest of the check as before. Still for the creator: whether to run the two settings tests (the panel at 60 Hz, CabinetOS on the GTX); and two product points from the laptop runs: Enter in the folder tree
runs `go.toPath` twice, and the tree stops at the nearest visible parent
folder when `panes.showHidden` is off.

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
