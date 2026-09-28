# Total Commander parity: what CabinetOS has, and a plan for the rest

Research note, 2026-09-29. Written for the creator.

Context: the two desk cards of 2026-09-29. "List of most user beloved
features of Total Commander" asks which of 20 features CabinetOS has not
built, and for a development plan. "List of most used Total Commander
Shortcuts" asks which of the common Total Commander (TC) keys CabinetOS
lacks, and to add them.

This note is a proposal. Nothing in it is decided until the creator
accepts it into [PLAN.md](../PLAN.md). The planning session can paste
Part 3 (a) into PLAN.md. The coder agents can take Part 3 (b).

- **Part 1** goes through the 20 features: what exists today, where each
  belongs under the Constitution, its size, and a proposal.
- **Part 2** is the table of keys.
- **Part 3** is ready text: (a) a Phase 11 section for PLAN.md, and (b)
  the small keys and commands that can be added now.
- At the end: the decisions this note made, the questions only the
  creator can answer, and the outside sources.

**Sizes.** Small: about a day. Medium: about one phase. Large: several
phases.

**What this note is based on.** The repository at commit `aefe88b`
(2026-09-29): the [Constitution](../../CONSTITUTION.md) (Articles 4, 5, 7,
9, 10 and 11 above all), [ARCHITECTURE.md](../ARCHITECTURE.md) §1 (the
Prime Directives), [PLAN.md](../PLAN.md), [keybindings.md](../keybindings.md),
[ui.md](../ui.md), [jobs.md](../jobs.md), [plugins.md](../plugins.md),
[tool-extensions.md](../tool-extensions.md), [indexer.md](../indexer.md),
[terminal.md](../terminal.md), [marketplace.md](../marketplace.md),
[ipc.md](../ipc.md), [config.md](../config.md) and the design handout in
[design/](../design/ABOUT.md). Where the documents did not say enough, the
code was read: the key grammar and the command seed
(`core/crates/cabinetos-commands/src/keys.rs`, `registry.rs`), the window's
key names (`ui/CabinetOS.Core/Keys/KeyNames.cs`), the selection
(`ui/CabinetOS.Core/Listing/SelectionModel.cs`,
`ui/CabinetOS/Views/FilePane.xaml.cs`) and the job planner
(`core/crates/cabinetos-jobs/src/plan.rs`). Every "exists" below quotes
its row from keybindings.md.

## Summary

| # | Feature | Today | Layer, and the article that decides it | Size | Sub-phase |
|---|---|---|---|---|---|
| 1 | Dual-pane interface | Built | Core (Art. 5) | small (what is left) | 11a, 11b |
| 2 | Keyboard-driven workflow | Mostly built | Core (Art. 7) | small | 11a, 11b |
| 3 | Multi-Rename Tool | Not built | Tool Extension, with a core rename job (Art. 10, 11) | medium | 11d, 11e |
| 4 | Archives as folders | Not built | Core Plugin on a new core hook (Art. 10, 11) | large | 11f |
| 5 | FTP/SFTP client | Not built | Core Plugin on the same hook (Art. 8, 10, 11) | large | 11f |
| 6 | Universal Lister (F3) | Not built; one viewer tool exists | Tool Extensions (Art. 10, 11); F3 itself in the core | medium | 11a, 11d, 11e |
| 7 | Directory sync, file compare | Not built | Compare in the core (Art. 5); sync and diff tools (Art. 10, 11) | medium | 11b, 11d, 11e |
| 8 | Folder size | Not built | Core (Art. 1, 5) | small | 11a |
| 9 | Plugin ecosystem | Partly built | Hooks in the core (Art. 11), plugins in the marketplace (Art. 8, 10) | large | 11d, 11f |
| 10 | Advanced search | Partly built: names only | Filters in the core (Art. 1); text search as a plugin (Art. 10) | medium | 11a, 11b, 11c, 11e, 11f |
| 11 | Background transfer manager | Mostly built | Core (Art. 1) | medium | 11b |
| 12 | Branch view | Not built | Core (Art. 10: navigation) | medium | 11b |
| 13 | Copy path or name | Partly built | Core registry and shell (Art. 7) | small | 11a, 11b |
| 14 | Folder tabs | Not built | Core and shell (Art. 4, 5) | medium | 11b |
| 15 | Custom columns | Not built | Column framework in the core (Art. 6); fields from plugins (Art. 11) | medium | 11c, 11d, 11e |
| 16 | Integrated command line | Built as the terminal | Core terminal (Art. 9, ADR 0005) | small | 11a |
| 17 | Selection tools | Partly built | Core and shell (Art. 7) | small | 11a, 11b |
| 18 | Split and combine | Not built | Core Plugin (Art. 10, 11) | medium | 11d, 11e |
| 19 | UI customization | Partly built | Core and shell (Art. 6, 8) | medium | 11c |
| 20 | Android app | Not applicable | Nowhere (Art. 1, 3) | none | none |

## Main findings

1. **The key grammar has no numeric keypad keys.** Num +, Num - and
   Num * cannot be bound today. The window maps the keypad digits to the
   plain digits ("The grammar has no separate numpad digits",
   `KeyNames.cs`) and has no name at all for the keypad's `+ - * / .`, so
   those presses never reach the keymap.
2. **A plain arrow key clears the marks.** Selection "follows Windows list
   rules plus Total Commander's Insert" (PLAN.md, Phase 5b). After three
   presses of Insert, one Down arrow selects only the new row, so TC's way
   of working (mark with Insert, move, mark, then F5) stops at the first
   arrow. A setting for TC-style marking fixes it.
3. **A command can have only one default key.** The seed in
   `core/crates/cabinetos-commands/src/registry.rs` takes one key string
   per command ("Default keys; empty for none."). Every alias on the card
   (F8, Shift+F8, Alt+F7) needs the seed to take a list first.
4. **One key on the card is taken: Ctrl+B.** It is the design's Toggle
   Sidebar (TC: branch view). Every other key on the card is free. Three
   more common TC keys, not on the card, are taken the same way: Ctrl+F is
   Find (TC: FTP connect), Ctrl+L is Go to Path (TC: count sizes), and
   Ctrl+Enter is Open in Other Pane (TC: the name into the command line).
5. **One missing core hook blocks features 4, 5 and half of 9.** Archives,
   FTP/SFTP and TC's Packer and File System plugins all need a virtual file
   system: a folder-like view of something that is not a folder on disk,
   provided by a Core Plugin. It is the largest single piece of work.
6. **Plugins cannot work on the user's files.** A Core Plugin reads and
   writes only under fixed `roots`, and each call ends after 5 s. Split and
   combine, checksums and text search need grants for the files the user
   chose, and long work run as a core job.
7. **Tools cannot start without a file, register commands, or change
   files.** Multi-Rename and Sync need tool commands, and a way to hand a
   plan (renames, copies) to the window, which runs it as a core job after
   the user confirms.
8. **Article 10 sends most of TC's box to the marketplace.** The Lister,
   archives, FTP, Multi-Rename, sync, split and text search become opt-in
   extensions. A "Commander pack" can install them in one step. They need
   the public marketplace index ([ADR 0012](../decisions/0012-marketplace-index-on-github-pages.md)),
   which waits for the creator.
9. **Article 6 promises a graphical settings menu, and none exists yet.**
   Most settings can be changed only in `cabinetos.json` today. TC's "deep
   customization" belongs there.
10. **Alt+Enter is not Windows' property sheet.** CabinetOS shows its own
    dialog from the listing: "There is no shell property sheet" (ui.md). A
    second command can open Windows' own sheet.

---

## Part 1: the features

### 1. Dual-pane interface

**Today.** Built. Phase 5 produced a "dual-pane grid with `ItemsRepeater`
virtualization and instant single-pane toggle" (PLAN.md, Phase 5), and two
panes are the first-start layout (PLAN.md, conflict B). Operations go from
the active pane to the other one:

```text
| `view.toggleDualPane` | View: Toggle Dual Pane | `ctrl+shift+d` | | UI |
| `view.focusOtherPane` | View: Focus Other Pane | `tab` | `filesView` | UI |
| `file.copyToOtherPane` | File: Copy to Other Pane | `f5` | `filesView` | UI |
| `file.moveToOtherPane` | File: Move to Other Pane | `f6` | `filesView` | UI |
| `file.openInOtherPane` | File: Open in Other Pane | `ctrl+enter` | `filesView` | UI |
```

Not there yet: swapping the panes (TC's Ctrl+U), showing a folder in the
left or the right pane (TC's Ctrl+Left and Ctrl+Right), and TC's copy
dialog. CabinetOS starts the job at once: "No dialog asks first: the
flyout shows the job at once, and Cancel stops it" (ui.md, "File
operations"). So a user cannot change the target folder or the name before
a copy. There is no Shift+F5 either (TC: copy in the same folder under a
new name).

**Where it belongs.** The core and the shell. Article 5 makes the dual
pane "the primary structural paradigm".

**Size.** Small, for what is left.

**Proposal.** In 11a the shell gets `view.swapPanes` (Ctrl+U) and
`go.showInLeftPane` and `go.showInRightPane` (Ctrl+Left, Ctrl+Right); they
need no new request. In 11b an optional copy dialog for F5 and F6 comes,
off by default, because the design's view E has none and Article 4 keeps
the first run simple. A new setting `panes.confirmTransfers` turns it on.
The dialog shows the target folder as an editable path and, for a single
source, an editable name. `file.duplicate` (Shift+F5) copies one item into
its own folder under a new name. For both, the core's `start_job` needs a
`new_name` for a single source, because today it refuses "a source that is
already in the destination folder" (jobs.md).

### 2. Keyboard-driven workflow

**Today.** Mostly built. F5, F6 and Tab are quoted under feature 1; the
other file and navigation keys are in the keymap too:

```text
| `palette.show` | View: Show Command Palette | `ctrl+shift+p` | | UI |
| `pane.openSelected` | Pane: Open Selected Item | `enter` | `filesView` | UI |
| `file.newFolder` | File: New Folder | `f7` | `filesView` | UI |
| `file.rename` | File: Rename | `f2` | `filesView` | UI |
| `file.delete` | File: Delete to Recycle Bin | `delete` | `filesView` | UI |
| `file.deletePermanently` | File: Delete Permanently | `shift+delete` | `filesView` | UI |
| `file.properties` | File: Properties | `alt+enter` | `filesView` | UI |
| `edit.toggleSelection` | Edit: Toggle Selection | `insert` | `filesView` | UI |
| `go.back` | Go: Back | `alt+left` | | UI |
| `go.forward` | Go: Forward | `alt+right` | | UI |
| `go.up` | Go: Up One Level | `alt+up` | | UI |
| `go.toPath` | Go: Go to Path… | `ctrl+l` | | UI |
```

Every command is in the palette and can be rebound, with chords and the
Immutable System Tier (Article 7). Backspace goes up one level, but as a
fixed key of the pane, not as a binding: "Backspace | `go.up` | the pane"
(ui.md, "Keys, contexts and commands").

The gaps:

- The key grammar has no keypad keys. Its keys are "the letters `a`–`z`,
  the digits `0`–`9`, `f1`–`f24`, and `escape`, `enter`, …"
  (keybindings.md, "Writing keys"). The window drops the keypad's
  `+ - * / .` before the keymap sees them.
- A command has at most one default key (finding 3).
- Moving the cursor selects: "Up, Down, Home, End, PageUp, PageDown | Move
  the focus; only the new row is selected" (ui.md). In the code, a plain
  arrow calls `SelectionModel.MoveTo` with `SelectMode.Single`, which clears
  every mark.
- Typing letters in a pane does nothing. TC's quick search and Explorer
  both jump to the first name that starts with the typed letters.
- There is no function-key bar. TC shows "F3 View … F8 Delete" at the
  bottom of its window, which teaches the keys.

**Where it belongs.** The core (the key grammar, the registry, the keymap)
and the shell (the selection and the key handling). Article 7.

**Size.** Small.

**Proposal.** In 11a: keypad key names in the grammar (`numpadadd`,
`numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal`), both
in `core/crates/cabinetos-commands/src/keys.rs` and in
`ui/CabinetOS.Core/Keys/KeyNames.cs`. Several default keys per command in
the seed, then the TC aliases. A setting `panes.selection` with two
values: `windows` (the default, as today) and `commander` (moving the
cursor never marks; marks stay while the cursor moves; commands act on the
marked rows, or on the cursor row when none is marked). Quick search in a
pane: the core finds the first matching name (`match_entries`, Part 3 (b)),
because the Dumb UI Rule keeps name matching out of the window. In 11b: an
optional function-key bar (`ui.functionKeys`, off by default per Article
4). Its buttons show what F3 to F8 are bound to in the keymap, and run
those commands.

### 3. Multi-Rename Tool (Ctrl+M)

**Today.** Not built. CabinetOS renames one item at a time: `file.rename`
(F2) and the core's `rename` request, which "gives a file or folder a new
name in the folder it is in" and never replaces anything (ipc.md, "Files
and folders").

```text
| `file.rename` | File: Rename | `f2` | `filesView` | UI |
```

The brief names this feature: "Power-user features (Regex renaming, chord
keybindings, terminal panels) should be hidden until invoked via the
Command Palette or config file" (ARCHITECTURE.md §1, Progressive
Disclosure).

**Where it belongs.** A Tool Extension in the marketplace, with the
renaming done by a core job. Article 10: a batch renamer is a
supplementary tool, so it is opt-in. Article 11: its dialog (masks,
counters, a preview grid) is visual, so it is a Tool Extension, and Tool
Extensions work "without touching the core I/O pipeline". The tool must
therefore not rename anything itself. Today "a tool may move around and
open things, never change files or grant rights" (tool-extensions.md).

**Size.** Medium.

**Proposal.** The core gets a `rename` job kind for `start_job`: a list of
`{path, new_name}` pairs. The core checks the whole list before anything
moves: valid names, no two items with the same new name, and chains or
swaps (a→b while b→a) done through temporary names. A taken name becomes a
conflict in the existing conflict flow. The finished job keeps an undo list
(the reverse pairs), so "Undo last rename" works, as in TC. The tool
platform gets three things in 11d. First, commands declared in `tool.json`,
so the tool registers "Multi-Rename…" with Ctrl+M (free) when it is
installed. Second, a tool that opens without a file and receives the
selection with each entry's size and times. Third, a new message from the
page to the window that hands over a rename plan. The window then shows
its own confirmation ("Rename 240 items?") and starts the job; the tool
never touches a file. The tool itself (11e) does TC's masks: `[N]` name,
`[E]` extension, `[C]` counter, `[Y]` `[M]` `[D]` date parts, search and
replace with regular expressions (text patterns), upper and lower case,
and a live preview. Fields from inside files, such as EXIF (the date and
camera data a photo carries) or MP3 tags (artist and title), come from the
content-field hook of feature 15.

### 4. Seamless archive handling

**Today.** Not built. The design shows archives only as a plugin: "Compress
to .7z [7-Zip]" in the context menu, and "Compress Selection" (Ctrl+Alt+Z)
from a "7-Zip Bridge" plugin (`COMMANDS` in
[FileForge.dc.html](../design/FileForge.dc.html)). The design handout
still lists "Pane types beyond folder, Markdown and hex (image, archive
contents, remote drives)" among its open questions. Two platform pieces are
missing. A pane can list only real folders: `list_directory` reads the
disk with `NtQueryDirectoryFile` (ipc.md). And drag and drop is not built:
"Pasting files copied in Explorer, drag and drop | The in-app clipboard
only" (ui.md, "Not in this version").

**Where it belongs.** A Core Plugin in the marketplace, on a new core hook.
Article 10: archive handling is advanced functionality, not bare
navigation, so it is opt-in. Article 11: reading and writing archives is
headless I/O, and Core Plugins "hook directly into the backend core to
intercept I/O". The brief says the same: "Build the *API hooks* for these
features, then build the features as separate, opt-in extensions"
(ARCHITECTURE.md §1).

**Size.** Large.

**Proposal.** In 11f the core gets a virtual file system (VFS) hook. A
Core Plugin declares the files it opens (`*.zip`, `*.7z`, …) and answers
"list this folder", "read these bytes" and, for packing, "write this
file". The core writes a provider's listing into the same shared-memory
section a disk listing uses, so the panes need little change. Copy, move
and delete run as jobs through the provider, with the same progress, pause
and conflicts. The plugin gets no general file access: the core hands it a
read handle to the one archive the user opened, and a write handle when it
packs (a new capability, `vfs:provide`, level medium). Enter on an archive
opens it like a folder. Running a file from inside an archive extracts it
to a temporary folder and opens it with `open_path`; a changed file is
offered back into the archive. Dragging files in and out needs basic drag
and drop first (11b). For the formats, a spike (a short trial build that
answers one question) compares two options: readers written in Rust and
compiled to WebAssembly, or libarchive (an open-source C library under the
BSD license) compiled to WebAssembly. Windows 11 itself reads RAR, 7z and
the tar family through libarchive since its October 2023 update (see
"Sources"). Asking the Windows shell's archive folders from the core is not
proposed: it would put an integration into the core (Article 10), and their
speed would be outside CabinetOS's control (Article 1). The plugin brings
TC's keys: Alt+F5 packs and Alt+F9 unpacks (both free).

### 5. Built-in FTP/SFTP client

**Today.** Not built. The capabilities it needs exist in name only:
"`process:run`, `net` and `credentials` are declared but never granted
yet" (PLAN.md, Phase 7), and the sandbox has no network ("Network (TCP,
UDP, name lookup) | No", plugins.md). The brief names this exact feature as
one that must not be built into the core: "Never hardcode "features" (like
a built-in markdown viewer or FTP client) into the core codebase"
(ARCHITECTURE.md §1). The design shows the intended form: a "Cloud Sync: S3
& OneDrive" plugin that "Presents remote storage as a drive. Uploads and
downloads show up in the transfer flyout like local copies", and asks for
Network access (high) and Credential store (high). A note on TC itself:
its built-in client does FTP and FTPS (FTP with TLS, the encryption HTTPS
uses); SFTP (file transfer over SSH) comes from a file-system plugin by
Ghisler.

**Where it belongs.** A Core Plugin in the marketplace, on the VFS hook of
feature 4. Article 10 (an opt-in integration), Article 11 (headless I/O),
and Article 8: it must run in the sandbox, so its network grant is narrow
and reviewed.

**Size.** Large.

**Proposal.** After the VFS hook (11f), the plugin host learns to grant
`net` safely. wasmtime can approve each address a plugin connects to
(`socket_addr_check` in its WASI context; see "Sources"), so the grant can
cover only the server the user connected to. `credentials` becomes a core
service: the plugin asks the core to store or read a password in Windows
Credential Manager (the Windows vault for saved passwords), under the
plugin's own name, and never sees other entries. A connection form needs
input from the user, and plugins have no UI. So the core gets a
quick-input host function (11d): the plugin asks for a line of text, a
choice or a password, and the shell shows it in the palette's frame, as
VS Code does for its extensions. Transfers are ordinary jobs through the
provider, so they run in the background, pause and resume like local
copies, and take the speed limit of feature 11. FTP and FTPS need plain
TCP plus TLS, which exists as Rust code that compiles to WebAssembly
(rustls). SFTP needs an SSH implementation that compiles to WebAssembly.
That is the main risk, and it gets a spike first. TC's Ctrl+F (connect) is
Find in CabinetOS, so the plugin proposes Ctrl+N (free) for a new
connection.

### 6. Universal Lister (F3)

**Today.** Not built as a Lister. One viewer exists: Markdown Preview, a
Tool Extension that "lives in `sdk/tools` and is opt-in (Article 10)"
(PLAN.md, Phase 5c). Enter opens a file in an installed tool that accepts
it, and:

```text
| `editor.openMarkdownPreview` | Editor: Open Markdown Preview | `ctrl+k v` | `filesView` | UI |
```

F3 is not bound. The design shows a hex viewer as a plugin ("Open as Hex",
Ctrl+K H, "Hex Editor Pro") and an "Image Toolkit" plugin. A tool reads its
file through a read-only virtual host in WebView2. Microsoft's
documentation warns that "media files accessed using virtual host name can
be very slow to load". It does not say whether that host answers range
requests (a request for one part of a file), which a viewer of huge files
needs.

**Where it belongs.** Tool Extensions in the marketplace. Article 10 makes
"specialized viewers" opt-in, and Article 11 names "hex editors, markdown
previews" as Tool Extensions. The F3 key itself is a command in the core
registry that routes to a tool, as `editor.openMarkdownPreview` does today.

**Size.** Medium.

**Proposal.** In 11a, `file.view` (F3) opens the focused file in the first
installed tool that accepts it, as Enter does. Without such a tool it says
so in the status bar and points to the marketplace. It never falls back to
the file's default application, because the default application of an
`.exe` is the program itself, and "view" must never run anything. In 11d, a
spike tests range requests through the virtual host. If they fail, the
core gets a read-only byte-range request that a tool asks for through the
window, for the file it was opened with. In 11e, a first-party viewer pack
goes to the marketplace: a text viewer (encodings, huge files read in
parts, line wrap, search), a hex viewer (on the design's Ctrl+K H), an
image viewer, and audio and video through WebView2's own decoders. TC's
Quick View (Ctrl+Q, free) is the same viewer in the other pane, following
the cursor. The window already sends `context` on every selection change
(at most every 100 ms); Quick View needs an `open` for the focused file at
each move as well.

### 7. Directory synchronization and file compare

**Today.** Not built.

**Where it belongs.** In two parts. Comparing the two panes is core,
because Article 5 says the dual-pane layout is there for "optimizing file
transfers, comparisons, and navigation efficiency". The comparing walk is
disk I/O, which the Dumb UI Rule keeps out of the window. The
synchronization tool (the result grid, the choice per file, saved setups)
and the side-by-side text compare are supplementary tools, so they are
Tool Extensions in the marketplace (Articles 10 and 11). The copying they
cause runs as ordinary core jobs.

**Size.** Medium.

**Proposal.** In 11b, a core request compares the two open listings by
name, size and time, and answers which rows differ. `compare.selectDifferences`
(TC's Shift+F2, free) then marks the differing rows in both panes. In 11d,
a core request `compare_trees` walks two folder trees on a core thread and
streams the differences: only on the left, only on the right, newer on one
side, a different size, and optionally different content (by hashing). It
can be cancelled. In 11e, the Sync tool shows these differences, lets the
user choose per file, and hands a plan of copy and delete jobs to the
window, which asks once and starts them. A Text Compare tool opens with two
files (the focused file in each pane) and does the line-by-line compare in
its own page. For that, the window's `open` message needs to carry two
files (11d).

### 8. Folder size calculation

**Today.** Not built. A folder row has no size: the status bar counts "the
bytes of the selected files, from the listing (folders have no size
there)" (ui.md). The core already sums a folder tree in one place, the
Recycle Bin check (`tree_size` in `core/crates/cabinetos-jobs/src/plan.rs`),
and that walk can be cancelled. No client can ask for it.

**Where it belongs.** The core. A folder's size is navigation information,
and the walk is disk I/O, which the Dumb UI Rule keeps out of the window.
Articles 1 and 5.

**Size.** Small.

**Proposal.** In 11a, a core request `measure_paths` walks the given
folders on a core thread with the NT enumeration. It sends running totals
(files, folders, bytes) as events, at most 30 a second, then the final
totals per path, and it can be cancelled. Space on a folder marks it and
measures it, as in TC. `file.calculateAllFolderSizes` (Alt+Shift+Enter,
TC's key) measures every folder of the listing. The pane shows the result
in the Size column until it leaves the folder, and the status bar's
selected size counts measured folders. Instant sizes for whole drives,
like the Everything tool's, would need file sizes in the index.
`FSCTL_ENUM_USN_DATA` does not return sizes, and reading the raw MFT (which
would) stays "a later optimization" (ARCHITECTURE.md, change log for Phase
6). That is not proposed now.

### 9. Extensive plugin ecosystem

**Today.** Partly built. CabinetOS has both extension layers and a
marketplace (PLAN.md, Phases 7 and 9). Core Plugins run in a WebAssembly
sandbox with capabilities, commands, `before-job` and `on-listing-opened`.
Tool Extensions run in WebView2. Themes, and installs, updates and
uninstalls with hash checks and a permissions review, are in place.

```text
| `marketplace.browse` | Marketplace: Browse Plugins and Themes | `ctrl+shift+x` | | UI |
| `plugins.list` | Plugins: Show Plugins | | | UI |
```

Against TC's four plugin kinds:

| TC plugin kind | What it does | CabinetOS today | What it needs |
|---|---|---|---|
| Packer (WCX) | Archives: list, pack, unpack | Nothing | The VFS hook, with writing (feature 4) |
| File system (WFX) | Devices, servers and clouds as drives | Nothing | The VFS hook, `net` and credentials (feature 5) |
| Lister (WLX) | Viewers inside F3 | Tool Extensions with `accepts` | Viewer tools (feature 6) |
| Content (WDX) | Extra fields for columns, search and renaming | Nothing | A content-field hook (feature 15) |

What stops more plugins today: "Plugins cannot open listings, start jobs
or read the selection yet" (plugins.md, "Not yet"); file access exists only
under fixed `roots`; a call ends after 5 s (500 ms for `before-job`); `net`,
`process:run` and `credentials` are never granted; and a tool cannot open
without a file or bring its own commands (tool-extensions.md, "Not yet").

**Where it belongs.** The hooks go into the core: Article 11 says Core
Plugins "hook directly into the backend core", and the brief says to build
"the *API hooks*" in the core. The plugins go into the marketplace
(Articles 8 and 10). TC's existing plugins belong nowhere. They are native
DLLs, native code cannot be sandboxed, and Article 8 requires "strict
capability sandboxes … ensuring bad code never crashes the file manager".

**Size.** Large.

**Proposal.** A new minor version of the plugin interface,
`cabinetos:plugin@0.2.0`, adds in 11d: per-request file grants
(`fs:selection`, level medium: a command may read the files the user gave
it, and create new files in the target folder, nothing else); long work as
core jobs (the plugin plans the work, or does it in steps within its time
limit, and the core shows progress, pause and cancel); a content-field hook
(declare fields, answer their values for one file through a read handle);
a quick-input host function; and commands in `tool.json` for tools. In
11f: the VFS hook (`vfs:provide`) and narrow `net` and `credentials`
grants. The marketplace gets "packs": one item that installs several
extensions. A "Commander pack" then installs the TC-like set in one step,
as one explicit choice, with nothing preinstalled (Articles 4 and 10).

### 10. Advanced file search

**Today.** Partly built: names only. "The core answers `search` from the
indexer within 200 ms, or walks one folder tree itself (at most 2 s and
20,000 entries) and says `source: walk`" (PLAN.md, Phase 6). The match is
"a case-insensitive substring match on names" (indexer.md).

```text
| `search.focus` | Search: Find Files… | `ctrl+f` | | UI |
| `search.scope` | Search: Whole Volume | | | UI |
```

Missing: wildcards and regular expressions, filters on size, dates and
attributes, text inside files, and search inside archives. Hits cannot be
worked on: "In search results, the file commands (F5, F6, F7, Delete, F2,
Ctrl+X, Ctrl+C, Ctrl+V, Alt+Enter, Insert, Ctrl+A) only say "These are
search results: Enter goes to a hit, Esc back to the folder."" (ui.md,
"Search"). TC can put its results into a panel and copy or delete them
there ("Feed to listbox").

**Where it belongs.** Finding files by name and metadata is core: Article 1
asks for "real-time NT-level file indexing", and Phase 6 put search into
the core. Searching the text inside files is advanced functionality, so it
is opt-in (Article 10), as a Core Plugin (Article 11). Searching inside
archives comes with the VFS hook (feature 4).

**Size.** Medium.

**Proposal.** In 11a, Alt+F7 becomes a second key of `search.focus`. In
11b, search hits become a real listing (the listing of paths of feature
12), so every file command works on them. In 11c, the core parses a query
syntax in the style of the Everything search tool: `*.log`, `regex:`,
`size:>10mb`, `modified:2026-09`, `attrib:h`, combined with spaces. A small
filter flyout writes it for users who do not want to type it (Article 4),
and Alt+F7 opens the search with the flyout open. The index holds "names,
parents and attributes, never file contents" (indexer.md), with no sizes or
times. So the core applies size and date filters to the name hits by
reading their metadata, up to a limit; the walk has the metadata anyway. In
11e, a text-search plugin (encodings, regular expressions) works on the
per-request read grants of 11d: the core walks the candidates, and the
plugin judges each file. In 11f, "search in archives" goes through the VFS
hook.

### 11. Background transfer manager

**Today.** Mostly built. Phase 4 built "`JobQueueManager` with
per-physical-disk queues (HDD: one at a time; NVMe/SSD: several in
parallel, detected through the storage seek-penalty property); … progress
coalesced to 30 updates per second; per-file conflict state machine (pause
that file, report, continue the batch); pause, resume, cancel" (PLAN.md).
The flyout shows one job at a time: "Several jobs: the flyout shows the
newest one, and a "N more" link in its header cycles through the others"
(ui.md).

```text
| `transfer.pause` | Transfer: Pause | | | UI |
| `transfer.resume` | Transfer: Resume | | | UI |
| `transfer.cancel` | Transfer: Cancel | | | UI |
| `transfer.next` | Transfer: Show Next Job | | | UI |
| `conflict.resolve` | Transfer: Resolve Conflict | | | UI |
```

Missing next to TC: a list of all jobs at once, a way to change their
order ("Waiting jobs keep their order on each disk", jobs.md), and a speed
limit (TC calls it a bandwidth limit).

**Where it belongs.** The core, with the shell's transfer panel. Article 1
("The interface must never freeze during heavy file operations") and the
brief's §3.

**Size.** Medium.

**Proposal.** In 11b the core gets `options.max_bytes_per_second` for
`start_job`, and two new `job_control` actions: `set_speed_limit`, and
`move_to_front` (the job takes the first place on its disks). The copy's
progress routine waits while the job is ahead of its limit, in the same
place where pause already blocks it. The flyout gets an expanded list of
every job: state, progress, limit, and "move to front". New commands
`transfer.showAll` and `transfer.setSpeedLimit` come without default keys.
The same limit serves FTP and SFTP later (feature 5).

### 12. Flat view / branch view (Ctrl+B)

**Today.** Not built, and TC's key is taken:

```text
| `view.toggleSidebar` | View: Toggle Sidebar | `ctrl+b` | | UI |
```

The design binds Ctrl+B to the sidebar ("Ctrl+B | Toggle sidebar",
[design/README.md](../design/README.md), "Interactions and keyboard"), as
VS Code does. TC's Ctrl+Shift+B (the branch of the selected folders) is
taken too:

```text
| `terminal.runTask` | Terminal: Run Task… | `ctrl+shift+b` | | UI |
```

**Where it belongs.** The core. A listing of a tree is navigation (Article
10: the core is a "file navigation engine"), and the walk must be fast
(Article 1).

**Size.** Medium.

**Proposal.** In 11b `list_directory` gains a branch mode: the core walks
the tree with the NT enumeration and writes one section with every file
and folder in it. Each entry needs its folder, so the section gets a
folder table (for example, the folder's index in `ListingMeta`'s reserved
field, and the folders' paths after the names). That changes the section
layout's version. The pane shows a Folder column, and every file command
works, since each row has a full path. A branch listing is watched with a
recursive watch and refreshed within the 30 Hz rule. A very large tree (a
system drive holds over a million entries) needs a count while walking and
a limit with a note, until chunked publishing exists (it "stays open design
work", PLAN.md, Phase 2). The same listing of paths serves search hits
(feature 10) and compare results (feature 7). Keys: `view.branchView` on
Ctrl+K Ctrl+B (free), and `view.branchViewSelected` without a default key.

### 13. Quick file and path copying

**Today.** Partly built. Ctrl+C already puts the full paths on Windows'
clipboard as text: "Windows' clipboard gets the same paths as text, one per
line (`DataPackage.SetText`, with the requested operation Move or Copy), so
they can be pasted into a terminal or an editor" (ui.md, "The clipboard").

```text
| `edit.copy` | Edit: Copy | `ctrl+c` | `filesView` | UI |
```

Missing: copying names only, copying the folder's path, copying paths
without also preparing a paste in CabinetOS, and exchanging files with
Explorer. "Files copied in Explorer are not pasted in this version"
(ui.md), and Explorer cannot paste files copied in CabinetOS either,
because only text goes onto the clipboard.

**Where it belongs.** The core registry and the shell (Article 7: every
action is a named command). The clipboard is the window's business, and no
file is read.

**Size.** Small.

**Proposal.** In 11a, three shell commands: `edit.copyFullPath`
(Ctrl+Shift+C, the key of Windows 11 Explorer's "Copy as path"),
`edit.copyName` (Ctrl+K Ctrl+N) and `edit.copyFolderPath` (Ctrl+K Ctrl+P).
In 11b, real file exchange with Explorer: the Windows clipboard's file list
format (`CF_HDROP`) in both directions, and drag and drop.

### 14. Folder tabs

**Today.** Not built. The design has tabs of another kind: workspace tabs
in the title bar, which "sit in the title bar and drive the sidebar, the
git branch in the status bar and the terminal's starting directory"
(design handout, §3 A). Those are not built either: "Workspaces (title-bar
tabs, sidebar section) and Tags | One static "Default" tab" (ui.md, "Not in
this version").

```text
| `workspace.switch` | Workspace: Switch Workspace… | `ctrl+k ctrl+w` | | UI |
```

`workspace.switch` says it "arrives in a later version" in the status bar.
The editor tab of a tool covers the pane's list, one tool per pane.

**Where it belongs.** The core and the shell. A tab is the navigation state
(Article 10) of a pane (Article 5). Its strip stays hidden while a pane has
one tab (Article 4), and the tabs are saved in `cabinetos.json` (Article 6).

**Size.** Medium.

**Proposal.** In 11b, a tab strip above each pane's list. Each tab has its
folder, its history (back and forward), its sort and a lock. In a locked
tab, going into a folder opens a new tab, as in TC. The tabs are saved as
`ui.tabs` (one list per pane) when the window closes, like `ui.lastPaths`
today. Commands: `tab.new` (Ctrl+T), `tab.close` (Ctrl+W), `tab.next`
(Ctrl+Tab), `tab.previous` (Ctrl+Shift+Tab), `tab.toggleLock` (no key) and
`tab.openFolderInNewTab` (Ctrl+Up). All these keys are free in the keymap.
Ctrl+Up is today one of the pane's list keys (it moves the focus without
selecting), so the binding takes it away from the list. The design's
title-bar tabs stay for workspaces; a workspace can later save both panes'
tabs.

### 15. Custom columns

**Today.** Not built. "The core sorts; the columns are fixed in this
version" (ui.md): Name, Modified, Type and Size. "Sorting by a column | The
column headers are static; the order is `panes.sort` from `cabinetos.json`"
(ui.md, "Not in this version"). The listing already carries more than the
window shows: the created and accessed times and the attributes (ipc.md,
"The listing section").

**Where it belongs.** The column framework (choosing, ordering, sizing and
sorting columns) is core and saved in `cabinetos.json` (Articles 6 and 7).
Fields that Windows already knows come from the Windows property system:
image dimensions, the length of a song or a video, the bitrate (the amount
of data per second of sound or picture) and the frame rate. This is the
same Windows shell that already gives CabinetOS its type names and icons,
and only the core can ask it, because plugins run in a sandbox. Fields
Windows does not know come from Core Plugins through a content-field hook
(Article 11), installed from the marketplace (Article 10).

**Size.** Medium.

**Proposal.** In 11c the column header becomes live: a click sorts, and
the core does the sorting, since the window does no data processing.
Columns can be chosen, ordered and resized, `panes.columns` saves them, and
named column sets can be switched per pane. The listing's own fields come
first: created, accessed, attributes and extension. Windows property
columns arrive through `describe_entries`, asked only for the rows on
screen, as type names are, and are off by default. Sorting by such a
column needs a value for every row, which the core reads in the
background. One risk: a property handler from a third party runs inside
the core's process, so a broken one can end the core (the window then
starts a new core). In 11d comes the content-field hook, and in 11e a
media fields plugin (EXIF, MP3 tags) for what Windows does not show.

### 16. Integrated command line

**Today.** Built, as the integrated terminal (Phases 8 and 5c): shells in
pseudo-consoles (ConPTY), hidden until Ctrl+`, with "the shown shell
following the active pane after 300 ms unless a line is half typed or a
full-screen program runs" (PLAN.md, Phase 5c).

```text
| `view.toggleTerminal` | View: Toggle Integrated Terminal | `ctrl+backquote` | | UI |
| `terminal.new` | Terminal: New Terminal | | | UI |
```

TC's command line is a single line that is always shown. The TC keys that
put names into it (Ctrl+P, Ctrl+Enter, Ctrl+Shift+Enter) have no
counterpart in CabinetOS.

**Where it belongs.** The core terminal. Article 9 asks for "integrated
terminals scoped to the active directory", and ADR 0005 put the terminal
into the core, hidden by default (Article 4). A second, TC-style command
line would duplicate it (Article 10), so the terminal plays that part.

**Size.** Small.

**Proposal.** In 11a, two commands. `terminal.insertPath` (Ctrl+P, TC's
key) types the active pane's folder. `terminal.insertSelectedPaths`
(Ctrl+Shift+Enter, TC's key) types the selected entries' full paths. Both
show the terminal when it is hidden, type the paths at the prompt without
pressing Enter, and give the terminal the keyboard. The core types them,
quoted for the shell, with the rules `terminal_sync_cwd` already uses for
pwsh, cmd and WSL (a new request, `terminal_type_paths`). TC's Ctrl+Enter
(the name into the command line) cannot be taken: it is
`file.openInOtherPane`. The shell keeps its own command history (the Up
arrow, PSReadLine), so TC's Alt+F8 is not needed. A lower dock than
today's minimum of 120 px can come later if it is wanted.

### 17. Advanced selection tools

**Today.** Partly built. "Selection follows Windows list rules plus Total
Commander's Insert" (PLAN.md, Phase 5b).

```text
| `edit.selectAll` | Edit: Select All | `ctrl+a` | `filesView` | UI |
| `edit.toggleSelection` | Edit: Toggle Selection | `insert` | `filesView` | UI |
```

Missing: marking and unmarking by pattern (Num +, Num -), inverting (Num
*), the same extension (Alt+Num +), unmarking everything, restoring the
previous marks (Num /), and TC's quick filter (Ctrl+S: show only the names
that match). The keypad keys cannot be bound (finding 1), and a plain arrow
key clears the marks (finding 2).

**Where it belongs.** The core and the shell (Article 7). Matching
patterns against a listing runs in the core, because the window does no
data processing (brief §1, the Dumb UI Rule). Flipping marks only changes
the window's own state, so the shell does it.

**Size.** Small.

**Proposal.** In 11a the core gets `match_entries`: it answers which rows
of a listing match TC-style patterns, such as `*.txt;*.md`, where a `|`
starts the patterns to leave out. The commands of Part 3 (b) use it: mark
and unmark by pattern (a small input box that offers the last pattern),
mark and unmark the same extension, invert, unmark all, and restore. In
search results they give the same note as the existing selection commands.
In 11b comes the quick filter (Ctrl+S, free): `list_directory` gains a name
filter, and the pane shows only the matching rows until Esc.

### 18. Split and combine files

**Today.** Not built.

**Where it belongs.** A Core Plugin in the marketplace. Article 10 (a
supplementary tool) and Article 11 (headless work on bytes).

**Size.** Medium, for the platform part. The plugin itself is small.

**Proposal.** The plugin interface lacks two things, both added in 11d.
First, file access: `fs:write` covers only fixed `roots`, but a split
writes next to the source or into the other pane's folder. The
per-request grant `fs:selection` lets a command read the files it was given
and create new files in the target folder, and nothing else. Second, time:
a plugin call ends after 5 s, and splitting 20 GB takes longer. So the
plugin returns a plan (these byte ranges of the source into `name.001`,
`name.002`, …; or these files joined in this order), and the core runs the
plan as a job with progress, pause, cancel and conflicts. The plugin (11e)
also writes TC's check file (a CRC, a short number computed from the
original's bytes) and checks it when combining. The same plugin can create
and verify SHA-256 checksum files, another TC favourite.

### 19. Deep UI customization

**Today.** Partly built. Colours: a JSON theme engine with live apply;
"Four themes ship inside the core" (PLAN.md, Phase 9), and more come from
the marketplace. Keys: every command can be rebound in the palette (the
pencil or F2) or in `cabinetos.json`. Layout: `ui.layout` (`classic` or
`right`; `rail` is accepted, but the window logs "the rail layout arrives
in a later phase; showing the sidebar"), one or two panes, the sidebar, and
the dock's size.

```text
| `preferences.selectColorTheme` | Preferences: Color Theme | `ctrl+k ctrl+t` | | UI |
| `keys.open` | Preferences: Open Keyboard Shortcuts | `ctrl+k ctrl+s` | | UI |
```

Missing: a graphical settings view, although Article 6 says "A user can
configure the application entirely through a beautiful graphical settings
menu". Also missing: font and row-density settings, the command bar's
buttons, and TC's user commands (buttons and menu entries that run a
program with the selected files).

**Where it belongs.** The core and the shell. Article 6 requires the
settings view, Article 8 makes themes the colour layer, and Article 7 makes
user commands palette commands like any other.

**Size.** Medium.

**Proposal.** In 11c: a settings view in the design's style, one page per
section of `cabinetos.json`, every change through `set_value`, so the file
and the view always agree. Font and density settings (`ui.fontSize`,
`ui.rowHeight`) within Article 3's native typography. `ui.commandBar` as a
list of command IDs. User commands in `cabinetos.json` (`userCommands`: an
ID, a title, a program, its arguments with placeholders for the selected
paths and the two folders, and whether it runs in a new terminal tab or on
its own). The core adds them to the registry, so they appear in the
palette and take keys. The same mechanism can give `terminal.runTask`
(Ctrl+Shift+B, which "arrives in a later version") its tasks (Article 9).

### 20. The Android app

**Today.** Not applicable.

**Where it belongs.** Nowhere. The Constitution makes CabinetOS a Windows
11 application: Article 3 ("indistinguishable from a first-party Windows 11
application") and Article 1 (a Rust core and a WinUI 3 frontend, NT-level
indexing), and PLAN.md decision 4 sets Windows 11 22H2 or newer. The core
is built on APIs that exist only on Windows: the NT directory API, the USN
journal, ConPTY and the shell. An Android app would be another product.

**Size.** None.

**Proposal.** None.

---

## Part 2: the shortcuts

Keys are written in the grammar's normal form (keybindings.md, "Writing
keys"). "filesView" means the binding works while a file pane has the
keyboard. The keybindings.md rows behind every "exists" are quoted in Part
1, under the feature the key belongs to.

### The keys on the card

| TC key | Action | CabinetOS today | Verdict | Proposed CabinetOS binding |
|---|---|---|---|---|
| F3 | View the file (Lister) | None. Enter opens a file in a tool that accepts it; `editor.openMarkdownPreview` is `ctrl+k v` | add, small (the viewers: feature 6) | `f3` → `file.view` (filesView) |
| F4 | Edit the file in the text editor | None | add, small | `f4` → `file.edit` (filesView) |
| F5 | Copy to the other pane | `file.copyToOtherPane`, `f5` (filesView) | exists | `f5`, unchanged |
| F6 | Move to the other pane, or rename | `file.moveToOtherPane`, `f6` (filesView); renaming is `file.rename`, `f2` | exists | `f6`, unchanged; rename in place also on `shift+f6` (TC's key, an alias) |
| F7 | New folder | `file.newFolder`, `f7` (filesView) | exists | `f7`, unchanged |
| F8 | Delete to the Recycle Bin | `file.delete` has only `delete` | add as an alias | `f8` → `file.delete` (filesView) |
| Del | Delete to the Recycle Bin | `file.delete`, `delete` (filesView) | exists | `delete`, unchanged |
| Shift+F8 | Delete permanently | `file.deletePermanently` has only `shift+delete` | add as an alias | `shift+f8` → `file.deletePermanently` (filesView) |
| Tab | Switch panes | `view.focusOtherPane`, `tab` (filesView) | exists | `tab`, unchanged |
| Backspace | Up one level | The pane's own Backspace runs `go.up`; it is not a keymap binding (`go.up` is `alt+up`) | exists | Backspace stays a fixed key of the pane (decision D20) |
| Ctrl+\ | Jump to the drive's root | None | add, small | `ctrl+backslash` → `go.root` |
| Alt+F1 | Drive list for the left pane | None (the drives are in the sidebar) | add, small | `alt+f1` → `go.chooseDriveLeft` |
| Alt+F2 | Drive list for the right pane | None | add, small | `alt+f2` → `go.chooseDriveRight` |
| Ctrl+U | Swap the panes | None | add, small | `ctrl+u` → `view.swapPanes` (filesView) |
| Ctrl+T | New folder tab | None | needs feature 14 | `ctrl+t` → `tab.new` (filesView). Free: `ctrl+t` appears only as the second key of `ctrl+k ctrl+t` |
| Ctrl+W | Close the tab | None | needs feature 14 | `ctrl+w` → `tab.close` (filesView). Free: the design uses Ctrl+W only as the second key of `ctrl+k ctrl+w` (Switch Workspace), and a second key does not block the same keys on their own |
| Space | Mark or unmark; on a folder, count its size | None | add, small | `space` → `edit.toggleSelectionInPlace` (filesView) |
| Insert | Mark and move down | `edit.toggleSelection`, `insert` (filesView) | exists | `insert`, unchanged. The marks survive the arrow keys only with `panes.selection: commander` (finding 2) |
| Num + | Mark by pattern | None: the grammar has no keypad keys | add, small | `numpadadd` → `edit.selectByPattern` (filesView) |
| Num - | Unmark by pattern | None | add, small | `numpadsubtract` → `edit.unselectByPattern` (filesView) |
| Num * | Invert the marks | None | add, small | `numpadmultiply` → `edit.invertSelection` (filesView) |
| Alt+F7 | Find files, with options | `search.focus`, `ctrl+f` (names only) | add as an alias | `alt+f7` → `search.focus`; the filters come with feature 10 |
| Ctrl+M | Multi-Rename Tool | None | needs feature 3 | `ctrl+m` → the Multi-Rename tool's own command (filesView), registered when the tool is installed |
| Ctrl+B | Branch view | None. `ctrl+b` is `view.toggleSidebar` (the design's binding) | needs feature 12 | `ctrl+k ctrl+b` → `view.branchView`. TC's Ctrl+B cannot be taken while the sidebar has it (decision D1, question 1) |
| Alt+Enter | Windows' property sheet | `file.properties`, `alt+enter` (filesView): CabinetOS's own dialog, from the listing | exists | `alt+enter`, unchanged. Windows' own sheet as `file.windowsProperties`, without a key, and as a button in Properties |
| Shift+F4 | New text file, opened in the editor | None | add, small | `shift+f4` → `file.newTextFile` (filesView) |
| Ctrl+P | The current path into the command line | None. There is no command line; the terminal plays that part | add, small | `ctrl+p` → `terminal.insertPath` (filesView) |

### Other TC keys checked

These are not on the card, but they are common in TC and were checked the
same way. The small ones are in Part 3 (b).

| TC key | Action | CabinetOS today | Verdict | Proposed CabinetOS binding |
|---|---|---|---|---|
| Shift+F6 | Rename in place | `file.rename`, `f2` (filesView) | add as an alias | `shift+f6` → `file.rename` |
| Shift+F5 | Copy into the same folder under a new name | None; the core refuses a source that is already in the destination folder | needs feature 1 | `shift+f5` → `file.duplicate` (11b) |
| Alt+Shift+Enter | Count the size of every folder | None | add, small | `shift+alt+enter` → `file.calculateAllFolderSizes` |
| Ctrl+Num + | Mark all | `edit.selectAll`, `ctrl+a` (filesView) | add as an alias | `ctrl+numpadadd` → `edit.selectAll` |
| Ctrl+Num - | Unmark all | None | add, small | `ctrl+numpadsubtract` → `edit.unselectAll` |
| Alt+Num + | Mark the same extension | None | add, small | `alt+numpadadd` → `edit.selectSameExtension` |
| Alt+Num - | Unmark the same extension | None | add, small | `alt+numpadsubtract` → `edit.unselectSameExtension` |
| Num / | Restore the marks | None | add, small | `numpaddivide` → `edit.restoreSelection` |
| Ctrl+F3 to Ctrl+F6 | Sort by name, extension, time, size | None; `panes.sort` exists only in the file | add, small | `ctrl+f3` to `ctrl+f6` → `view.sortByName`, `view.sortByExtension`, `view.sortByModified`, `view.sortBySize` |
| Ctrl+R | Read the folder again | None; folders are watched | add, small | `ctrl+r` → `view.refresh` |
| Ctrl+D | Directory hotlist | None; the sidebar has pinned folders | add, small | `ctrl+d` → `go.pinnedFolders` |
| Ctrl+Left, Ctrl+Right | Show a folder in the left or right pane | `file.openInOtherPane`, `ctrl+enter` (the other pane only) | add, small | `ctrl+left` → `go.showInLeftPane`, `ctrl+right` → `go.showInRightPane` |
| Ctrl+Shift+Enter | Full paths into the command line | None | add, small | `ctrl+shift+enter` → `terminal.insertSelectedPaths` |
| Ctrl+Enter | The name into the command line | `file.openInOtherPane` has it | not applicable: the key is CabinetOS's Open in Other Pane (design view D) | none |
| Ctrl+Q | Quick View | None | needs feature 6 | `ctrl+q` → the viewer pack's Quick View command |
| Ctrl+S | Quick filter | None | needs feature 17 | `ctrl+s` → `view.quickFilter` |
| Ctrl+Tab, Ctrl+Shift+Tab | Next and previous tab | None | needs feature 14 | `ctrl+tab` → `tab.next`, `ctrl+shift+tab` → `tab.previous` |
| Ctrl+Up | The folder under the cursor in a new tab | None; Ctrl+Up is a list key of the pane | needs feature 14 | `ctrl+up` → `tab.openFolderInNewTab` (it takes the list key) |
| Shift+F2 | Compare the two file lists | None | needs feature 7 | `shift+f2` → `compare.selectDifferences` |
| Alt+F5, Alt+F9 | Pack, unpack | None | needs feature 4 | `alt+f5`, `alt+f9`, from the archive plugin |
| Ctrl+F | Connect to an FTP server | `search.focus`, `ctrl+f` | not applicable: Ctrl+F is Find (Explorer, the design) | The FTP plugin proposes `ctrl+n` |
| Ctrl+L | Count the size of the selection | `go.toPath`, `ctrl+l` | not applicable: Ctrl+L is Go to Path (Explorer, browsers, the design) | Space and Alt+Shift+Enter count sizes |
| Ctrl+Shift+B | Branch view of the selected folders | `terminal.runTask`, `ctrl+shift+b` | needs feature 12 | none (palette: `view.branchViewSelected`) |
| Alt+F8 | Command-line history | None | not applicable: the shell in the terminal keeps its own history (the Up arrow, PSReadLine) | none |
| Ctrl+PageUp, Ctrl+PageDown | Parent folder; open a folder or an archive | List keys of the pane (move by a page, keep the selection) | not applicable while they are list keys: Backspace and Enter do the same | none |

### How the keys were checked

- Every proposed key was compared with the keys and contexts of the 51
  commands in keybindings.md. None repeats a key in the same context,
  which the core refuses ("two commands get the same keys in the same
  context").
- The only chord prefix in use is `ctrl+k`. Every new chord starts with
  it, and no new single key is `ctrl+k`, so no key both starts a chord and
  runs a command on its own.
- The Immutable System Tier (`ctrl+shift+p`, `escape`, `ctrl+k ctrl+s`) is
  untouched: no proposal uses those keys, and nothing binds `ctrl+k`
  alone. The core compares whole key sequences for the tier
  (`check_immutable_keys` in `keymap.rs`), so `ctrl+s`, `ctrl+t` and
  `ctrl+w` alone are allowed.
- Keys that act on a pane get `when: filesView`, so text boxes, the
  terminal and tool pages keep their keys. In the terminal, Ctrl+D, Ctrl+U
  and Ctrl+R still reach the shell, because only `terminalFocus` bindings
  and the palette and terminal keys leave it (ui.md, "The terminal").
  `go.*` commands have no `when`, like `go.up`: the marketplace closes
  itself before a `go.*` command runs (ui.md, "The marketplace").
- The design's plugin keys stay free for their plugins: Ctrl+K H (hex),
  Ctrl+K Ctrl+C (Git commit), Ctrl+K Ctrl+H (Git history) and Ctrl+Alt+Z
  (compress).
- The keypad keys need the grammar change (item P1 in Part 3 (b)) before
  anything can be bound to them.
- Keys depend on the keyboard layout's virtual keys. Ctrl+\ is the key
  above Enter on most layouts; where that key is something else, TC offers
  Ctrl+<, and a CabinetOS user rebinds (Article 7).

---

## Part 3: ready text

### (a) A section for PLAN.md

To paste at the end of PLAN.md section 5, after Phase 10. The links are
relative to `docs/`, where PLAN.md lives.

```markdown
### Phase 11 — Total Commander parity (proposed 2026-09-29)

Goal: a Total Commander user finds the features and keys they rely on, each
one in the layer the Constitution gives it. The research behind this phase,
feature by feature and key by key, is
[research/total-commander.md](research/total-commander.md). The opt-in rule
(Article 10) decides every item. Navigation, selection, keys and jobs go into
the core. Viewers, archives, remote servers, batch renaming, syncing,
splitting and text search become extensions in the marketplace, and the core
only gets the hooks they need (brief §1). The sub-phases go from small to
large. Each one starts only when the creator says so.

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

#### 11b — Navigation depth (medium)

Covers features 12 (branch view), 14 (folder tabs), 11 (the transfer
queue), 13 (files to and from Explorer), 7 (comparing the two panes), 17
(quick filter), 10 (search hits as a listing), and the optional parts of 1
(the copy dialog) and 2 (the function-key bar).

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
```

### (b) Small keys and commands to add now

For the coder agents. Each item fits in about a day and needs no new
feature. Every new command is a row in `SEED` in
`core/crates/cabinetos-commands/src/registry.rs` with target UI, like the
51 existing ones. The window registers its handler with
`RegisterUiHandler`. keybindings.md's command table and ui.md's key table
get the new rows. In search results, every new selection and file command
gives the same note as the existing ones (ui.md, "Search").

**Prerequisites.**

| # | What | Built in | What it does |
|---|---|---|---|
| P1 | Keypad keys in the key grammar: `numpadadd`, `numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal`; VS Code's `numpad_add` and the like accepted as aliases | Core: `NAMED_KEYS` and `KEY_ALIASES` in `keys.rs`. Shell: `KeyNames.cs` maps the virtual keys 0x6B, 0x6D, 0x6A, 0x6F and 0x6E, and shows "Num +", "Num -", "Num *", "Num /" and "Num ." | Lets the keypad's operators be bound; today the window drops them before the keymap. The keypad digits stay the same as the plain digits |
| P2 | Several default keys per command | Core: `Seed.keys` becomes a list of key strings | Lets aliases such as F8 sit next to Delete. The palette already shows the first key and "+N"; the pencil still replaces all of a command's keys |
| P3 | Setting `panes.selection`: `windows` (default) or `commander` | Core: the config crate and its schema. Shell: `SelectionModel` and `FilePane` | In `commander`, keys that move the cursor keep the marks, Shift with them marks the rows passed over, and a new listing starts with nothing marked. Commands act on the marked rows, or on the cursor row when none is marked. The mouse keeps the Windows rules |

**Aliases for existing commands.**

| # | Command ID | Palette title | New key | `when` | Built in | What it does |
|---|---|---|---|---|---|---|
| A1 | `file.delete` | File: Delete to Recycle Bin | `f8` | filesView | Core: seed | F8 deletes to the Recycle Bin, as Delete does |
| A2 | `file.deletePermanently` | File: Delete Permanently | `shift+f8` | filesView | Core: seed | Shift+F8 deletes for good after the same dialog as Shift+Delete |
| A3 | `file.rename` | File: Rename | `shift+f6` | filesView | Core: seed | Shift+F6 renames in place, as F2 does |
| A4 | `search.focus` | Search: Find Files… | `alt+f7` | none | Core: seed | Alt+F7 puts the keyboard in the search field; the filters come in 11c |
| A5 | `edit.selectAll` | Edit: Select All | `ctrl+numpadadd` | filesView | Core: seed | Ctrl+Num + marks every row, as Ctrl+A does |

**New commands.**

| # | Command ID | Palette title | Keys | `when` | Built in | What it does |
|---|---|---|---|---|---|---|
| N1 | `go.root` | Go: Up to Root | `ctrl+backslash` | none | Shell | Shows the root of the active pane's folder: `C:\`, or `\\server\share\` |
| N2 | `go.chooseDriveLeft` | Go: Choose Drive for Left Pane… | `alt+f1` | none | Shell | Opens the drive list under the left pane; a letter or Enter goes to that drive |
| N3 | `go.chooseDriveRight` | Go: Choose Drive for Right Pane… | `alt+f2` | none | Shell | The same for the right pane; it shows two panes first when one is shown |
| N4 | `go.showInLeftPane` | Go: Show in Left Pane | `ctrl+left` | filesView | Shell | Shows the folder under the cursor in the left pane, or the active pane's folder when the cursor is on a file |
| N5 | `go.showInRightPane` | Go: Show in Right Pane | `ctrl+right` | filesView | Shell | The same for the right pane |
| N6 | `go.pinnedFolders` | Go: Pinned Folders… | `ctrl+d` | none | Shell | Lists the sidebar's folders in the palette's frame; Enter goes there; the last row pins the current folder |
| N7 | `view.swapPanes` | View: Swap Panes | `ctrl+u` | filesView | Shell | Swaps the two panes' folders, with their marks and history |
| N8 | `view.refresh` | View: Refresh | `ctrl+r` | filesView | Shell | Lists the active pane's folder again, for drives whose changes Windows does not report |
| N9 | `view.sortByName` | View: Sort by Name | `ctrl+f3` | filesView | Shell | Sorts the active pane by name; the same key again reverses the order |
| N10 | `view.sortByExtension` | View: Sort by Extension | `ctrl+f4` | filesView | Core: sort key `extension`. Shell | Sorts the active pane by extension, then by name |
| N11 | `view.sortByModified` | View: Sort by Date Modified | `ctrl+f5` | filesView | Shell | Sorts the active pane by the time of the last change |
| N12 | `view.sortBySize` | View: Sort by Size | `ctrl+f6` | filesView | Shell | Sorts the active pane by size |
| N13 | `edit.toggleSelectionInPlace` | Edit: Toggle Selection in Place | `space` | filesView | Shell (and N28 for folders) | Marks or unmarks the cursor row without moving the cursor; a folder it marks is measured, as TC's Space does |
| N14 | `edit.selectByPattern` | Edit: Select by Pattern… | `numpadadd` | filesView | Core: `match_entries`. Shell: the pattern box | Asks for a pattern (the last one offered) and marks the matching rows |
| N15 | `edit.unselectByPattern` | Edit: Unselect by Pattern… | `numpadsubtract` | filesView | Core: `match_entries`. Shell | Asks for a pattern and unmarks the matching rows |
| N16 | `edit.selectSameExtension` | Edit: Select Same Extension | `alt+numpadadd` | filesView | Core: `match_entries`. Shell | Marks every file with the cursor file's extension |
| N17 | `edit.unselectSameExtension` | Edit: Unselect Same Extension | `alt+numpadsubtract` | filesView | Core: `match_entries`. Shell | Unmarks every file with the cursor file's extension |
| N18 | `edit.invertSelection` | Edit: Invert Selection | `numpadmultiply` | filesView | Shell | Marks every unmarked file and unmarks every marked one; folders stay as they are |
| N19 | `edit.unselectAll` | Edit: Unselect All | `ctrl+numpadsubtract` | filesView | Shell | Unmarks every row |
| N20 | `edit.restoreSelection` | Edit: Restore Selection | `numpaddivide` | filesView | Shell | Brings back the marks the last file command or unmark cleared in this pane |
| N21 | `edit.copyFullPath` | Edit: Copy Full Path | `ctrl+shift+c` | filesView | Shell | Puts the full paths of the targets on Windows' clipboard, one per line, without quotes; the in-app file clipboard is not changed |
| N22 | `edit.copyName` | Edit: Copy Name | `ctrl+k ctrl+n` | filesView | Shell | Puts the targets' names on the clipboard, one per line |
| N23 | `edit.copyFolderPath` | Edit: Copy Folder Path | `ctrl+k ctrl+p` | filesView | Shell | Puts the active pane's folder path on the clipboard |
| N24 | `file.view` | File: View | `f3` | filesView | Shell | Opens the cursor file in the first installed tool that accepts it; without one, the status bar says so and names the marketplace. It never opens the default application |
| N25 | `file.edit` | File: Edit | `f4` | filesView | Core: `edit_path`, `files.editor`. Shell | Opens the cursor file for editing (see `edit_path` below) |
| N26 | `file.newTextFile` | File: New Text File | `shift+f4` | filesView | Core: `create_file`, `edit_path`. Shell | Opens a name box over a new row ("New Text Document.txt", the name part selected), creates the empty file, then opens it for editing. A name that exists opens that file, as in TC |
| N27 | `file.windowsProperties` | File: Windows Properties | none | filesView | Core: `show_properties`. Shell: also a button in Properties | Shows Windows' own property sheet for the targets |
| N28 | `file.calculateFolderSize` | File: Calculate Folder Size | none | filesView | Core: `measure_paths`. Shell | Measures the marked folders, or the cursor folder, and shows the sizes in the Size column |
| N29 | `file.calculateAllFolderSizes` | File: Calculate All Folder Sizes | `shift+alt+enter` | filesView | Core: `measure_paths`. Shell | Measures every folder of the listing |
| N30 | `terminal.insertPath` | Terminal: Insert Folder Path | `ctrl+p` | filesView | Core: `terminal_type_paths`. Shell | Shows the terminal (starting the default shell when none runs), types the active pane's folder at the prompt without Enter, and gives the terminal the keyboard |
| N31 | `terminal.insertSelectedPaths` | Terminal: Insert Selected Paths | `ctrl+shift+enter` | filesView | Core: `terminal_type_paths`. Shell | The same with the targets' full paths, separated by spaces |

**Not a command.**

| # | What | Built in | What it does |
|---|---|---|---|
| Q1 | Quick search in a pane | Core: `match_entries` with `first_from`. Shell: typed characters in a pane | Letters typed in a pane (no key in the keymap takes them) jump to the first name that starts with them; the typed text shows in the status bar; after 1 s without a key a new search starts, as in Explorer; Esc clears it |

**What the core needs for this list.** Each new request raises the
protocol version (ipc.md, "What changes the version"); one raise covers
the whole list.

- **`create_file`** `{path}` → `ok`. Creates an empty file and never
  replaces one (`already_exists`). The same name checks and error codes as
  `create_directory` (`check_name` in `cabinetos-fs/src/ops.rs`). CLI:
  `cabinetos-cli mkfile <path>`.
- **`edit_path`** `{path}` → `ok`. Runs `files.editor` when it is set. Else
  the shell's `edit` verb for the file's type. Else `notepad.exe` (TC's
  default editor). `files.editor` is a new setting, `null` by default, or
  `{"command": …, "args": […]}` like a terminal profile. The file's path is
  added as the last argument, and the program is found the way terminal
  profiles find theirs (a full path, or a name on the PATH, never the
  current folder). It is a new request, not a `verb` field on `open_path`:
  an older core would ignore an unknown field and *open* the file, which
  for an `.exe` runs the program (decision D7).
- **`show_properties`** `{paths}` → `ok`. Shows Windows' property sheet.
  For one path it uses the `properties` verb of `ShellExecuteExW`; for
  several, the shell's combined sheet (`SHMultiFileProperties`). The sheet
  belongs to the core's process, which lives as long as the window. The
  first build must verify that the sheet stays open and comes to the front
  after the window calls `AllowSetForegroundWindow`, as for `open_path`.
- **`measure_paths`** `{paths}` → `measure_started {measure_id}`, then the
  events `measure_progress {measure_id, path, files, folders, bytes}` (at
  most 30 a second) and `measure_finished {measure_id, results: [{path,
  files, folders, bytes, unreadable}], cancelled}`. `cancel_measure
  {measure_id}` → `ok`. The walk is `tree_size`'s (`cabinetos-jobs/src/plan.rs`),
  moved where both can use it and given progress: the NT enumeration, links
  not followed, hidden and system files counted, a folder that cannot be
  read counted in `unreadable`. It runs on a core thread; several measures
  may run at once.
- **`match_entries`** `{listing_id, patterns, files_only, first_from}` →
  `entry_matches {listing_id, generation, ranges}`. `patterns` uses TC's
  syntax: `*` and `?`, several patterns separated by `;`, and a `|` before
  the patterns to leave out. Matching ignores case. `ranges` lists the
  matching indexes of the section as `[start, count]` pairs, which keeps
  "all 100,000 rows" short. With `first_from`, only the first match at or
  after that index (wrapping to the start) is sent, for quick search. The
  core reads the names from its own section, as `describe_entries` does.
- **`terminal_type_paths`** `{session_id, paths}` → `ok`. Types the paths
  at the prompt, each quoted for that session's shell with the rules of
  `terminal_sync_cwd` (terminal.md, "Following the active pane"), separated
  by spaces, without Enter.
- **Sort key `extension`** for `list_directory` and `panes.sort.key`: by
  extension, ignoring case, then by name; folders first, as always.
- **`panes.selection`** and **`files.editor`** in `cabinetos.json`, with
  the schema regenerated (`CABINETOS_UPDATE_SCHEMA=1 cargo test -p
  cabinetos-config`).

**What the shell needs for this list.**

- The drive list (N2, N3): a small flyout under the pane's header with the
  data the sidebar already has from `list_volumes`. A letter jumps to its
  drive; Enter goes to the drive's root, or to the folder this pane last
  showed on that drive in this session.
- The pattern box (N14, N15): one line in the palette's frame. It offers
  the last pattern (`*.*` the first time) and keeps the last ten in memory.
  It has an "Include folders" check box, off by default; while it is off,
  the request carries `files_only: true`.
- The sort commands (N9 to N12): the shell keeps a sort per pane and sends
  it with `list_directory`, which already takes `sort`. `panes.sort` stays
  the default for a pane that has none. Folder tabs (11b) save the sort
  with each tab.
- Measured sizes (N13, N28, N29): the Size column shows the running total
  in the tertiary text colour while the core counts, then the total. The
  status bar's selected size counts measured folders.
- Tests: the registry test counts 51 commands today
  (`seeds_the_design_commands_but_not_plugin_ones`), so the count and the
  key checks change; the keymap of all defaults must compile without a
  conflict; the UI tests cover `KeyNames` for the keypad codes and
  `SelectionModel` in both modes; end-to-end tests run `create_file`,
  `measure_paths` and `match_entries` against the real core.

---

## Decisions made in this note

Each one is written as what, because, and how to undo it.

- **D1. Ctrl+B stays with the sidebar; branch view gets Ctrl+K Ctrl+B.**
  Because the design binds Ctrl+B to Toggle Sidebar (as VS Code does), and
  the core refuses two commands on one key in one context. Undo: swap the
  two default keys in the seed, or rebind in the palette (question 1).
- **D2. Ctrl+F, Ctrl+L and Ctrl+Enter keep their CabinetOS meaning.**
  Because Find, Go to Path and Open in Other Pane are the design's and
  Explorer's keys and are built. TC's functions for them get other keys:
  Ctrl+N for an FTP connection, Space and Alt+Shift+Enter for sizes,
  Ctrl+Shift+Enter for paths. Undo: rebind in `cabinetos.json`.
- **D3. Keypad keys are named `numpadadd` and so on, with VS Code's
  `numpad_add` accepted too.** Because the grammar writes a key name as one
  lower-case word (`bracketleft`, `pagedown`). Undo: rename them in
  `keys.rs` and `KeyNames.cs` before a release; no user file uses them yet.
- **D4. `panes.selection` defaults to `windows`.** Because Article 4 wants
  a casual user at ease on the first run, and the design follows the
  Windows 11 list selection. A TC user switches once. Undo: change the
  default in the config crate (question 3).
- **D5. F3 never runs anything.** Without a viewer tool it shows a note
  instead of opening the file with its default application. Because the
  default application of an `.exe` is the program itself. Undo: add a
  fallback in the `file.view` handler.
- **D6. F4 opens `files.editor`, else the shell's edit verb, else
  Notepad.** Because TC's default editor is Notepad, and Windows' edit verb
  already picks an editor for some types (for example Notepad for batch
  files). Undo: change the order in `edit_path`.
- **D7. Editing and Windows properties are new requests (`edit_path`,
  `show_properties`), not a new field of `open_path`.** Because a core that
  does not know a new field ignores it, and would then open the file,
  which runs a program. A new request gets `unknown_request` instead. Undo:
  fold them into `open_path` once no older core can meet the window.
- **D8. Space marks without moving, and measures a folder it marks.**
  Because that is TC's Space, and Insert already marks and moves down.
  Undo: bind Space to `edit.toggleSelection` instead.
- **D9. Pattern marking and invert act on files; a check box adds
  folders.** Because a pattern such as `*.txt` is about files, and TC
  offers the same choice. Undo: change the check box's default, or add a
  setting.
- **D10. Paths are copied with Ctrl+Shift+C, Ctrl+K Ctrl+N and Ctrl+K
  Ctrl+P, without quotes, one per line.** Because TC has no default keys
  for these, Ctrl+Shift+C is Windows 11 Explorer's "Copy as path", the
  chords stay in the existing Ctrl+K family without touching the design's
  plugin chords, and Ctrl+C already copies paths as plain lines. Undo:
  rebind, or add quotes in the handler.
- **D11. Ctrl+P types into the terminal and moves the keyboard there.**
  Because the terminal is CabinetOS's command line (ADR 0005), and after
  TC's Ctrl+P the user goes on typing the command. Undo: keep the keyboard
  in the pane in the handler.
- **D12. Sorting is per pane, held by the shell.** Because TC sorts each
  panel on its own, and `list_directory` already takes a sort. Undo: write
  `panes.sort` with `set_value` instead, which sorts both panes.
- **D13. Folder tabs go above each pane; the title bar keeps the design's
  workspace tabs.** Because the design reserved the title bar for
  workspaces, and TC's tabs belong to a panel. Undo: this is a design
  choice for the creator (question 2).
- **D14. Archives, FTP and SFTP, viewers, Multi-Rename, sync, split, text
  search and media fields go to the marketplace; the core gets only their
  hooks.** Because of Article 10 and the brief's §1, which names "FTP
  client" as its example. Undo: an exception needs the creator's decision.
- **D15. Comparing the panes and two trees is a core request; the sync tool
  is an extension.** Because Article 5 names "comparisons" as part of the
  dual-pane foundation, and the walk must run in the core (the Dumb UI
  Rule). Undo: move `compare_trees` into a plugin once plugins can walk
  trees.
- **D16. Windows property columns come from the core, off by default.**
  Because only the core can ask the Windows property system (plugins are
  sandboxed), and the core already asks the same shell for type names and
  icons. Undo: leave them out, and take every extra column from plugins
  (question 4).
- **D17. Tools register their own commands in `tool.json`; Ctrl+M arrives
  with Multi-Rename.** Because under Article 10 an extension's command
  arrives with the extension, as plugin commands do.
  `editor.openMarkdownPreview` stays in the core registry until tools can
  do this. Undo: seed the tool commands in the core, as
  `editor.openMarkdownPreview` is.
- **D18. No always-visible command line.** Because Article 4 hides the
  terminal until it is asked for, ADR 0005 put it in the core, and a second
  command line would duplicate it (Article 10). Undo: a lower, one-line
  dock mode later.
- **D19. A new folder `docs/research/` holds this note, with a one-paragraph
  README.** Because the note is a proposal, not a governing document;
  PLAN.md and the ADRs stay the binding texts. Undo: move the file into
  `docs/log/`, or remove the folder.
- **D20. Backspace stays a fixed key of the pane.** Because a command has
  one `when` for all its default keys, and `go.up` has none. Backspace as a
  second default key of `go.up` would work in every context, the
  marketplace's card grid included. Undo: give the seed a context per key,
  then add Backspace.

## Questions only the creator can answer

1. **Ctrl+B.** Keep it for the sidebar (the design, VS Code) and put branch
   view on Ctrl+K Ctrl+B? Or give Ctrl+B to branch view (TC) and move the
   sidebar to another key?
2. **Tabs.** The design has workspace tabs in the title bar (a workspace is
   a project: its folders, terminal and Git branch). TC has folder tabs
   above each panel. The proposal keeps both: workspace tabs in the title
   bar, folder tabs above each pane. Is that right?
3. **Marking.** Should the first run use Windows marking (a plain arrow
   selects, as in Explorer) with TC marking one setting away, or TC marking
   by default?
4. **Extra columns.** Is it acceptable under Article 10 that the core shows
   Windows' own property columns (image size, song length), off by default?
   Or should every extra column come from a marketplace plugin?
5. **Text search inside files.** TC users use it daily. The proposal makes
   it a plugin under Article 10. Should it be core instead?
6. **The Commander pack.** Should the first start offer it (one question,
   answered once), or should it wait in the marketplace until a user looks?
   Article 4 argues for the marketplace; a TC user may prefer the offer.
7. **Network for plugins.** FTP and SFTP need the `net` capability, which
   has never been granted. May the host grant it, narrowed to the one
   server the user connects to, after a review that marks it high?
8. **The public marketplace index.** Sub-phase 11e cannot reach users until
   the public repository of ADR 0012 exists. When should it be created?
9. **Order.** The plan goes from small to large: 11a, 11b, 11c, then the
   extension platform (11d, 11e) and archives and servers last (11f). Should
   archives come earlier, since they are the most visible gap?
10. **The copy dialog.** TC asks before F5 and lets the user edit the
    target. The design copies at once. The proposal adds the dialog behind
    a setting, off by default. Should it be on for you by default?

## Sources outside the repository

- Microsoft Learn, [CoreWebView2.SetVirtualHostNameToFolderMapping](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping):
  the note that media files through a virtual host "can be very slow to
  load" (feature 6).
- BleepingComputer, [Windows 11 adds support for 11 file archives, including 7-Zip and RAR](https://www.bleepingcomputer.com/news/microsoft/windows-11-adds-support-for-11-file-archives-including-7-zip-and-rar/):
  Windows 11 22H2 and later read RAR, 7z and the tar family through
  libarchive, since update KB5031455 of October 2023 (feature 4).
- wasmtime documentation, [WasiCtxBuilder](https://docs.wasmtime.dev/api/wasmtime_wasi/p2/struct.WasiCtxBuilder.html):
  `socket_addr_check` approves or refuses each address a WebAssembly guest
  connects to (feature 5).
