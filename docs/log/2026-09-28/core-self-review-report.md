## Self-review report: the core, and six leftovers

Context: CabinetOS core. A self-review pass over the core crates (Part A) and six small leftovers (Part B). All of it is pushed; `main` = `origin/main` at 27fba8f. Nothing was run on GitHub. The queued tasks come next: step 2 (edge cases, classes A–E), then Commander Compact.

## Built

### Part A: self-review. Six fixes and one docs correction
Each fix came with a new test that failed on the old code before the fix went in.

| Area | Finding | Commit | Test, and how it failed before |
|---|---|---|---|
| Shared-memory listings | The core duplicated a section handle into the client inside the blocking part of a listing or refresh, and announced it later. A refresh cancelled in between (the client closed a folder that was changing, which is common when navigating away from a busy folder) left a handle in the client that the client never learned of. That section and its memory then stayed until the client exited. Now the handle is duplicated (`Published::hand_to`) in the same synchronous step that queues `listing_opened` / `listing_refreshed`. | a4582ba | `listing::tests::publishing_hands_the_client_nothing_until_the_listing_is_announced`: the stand-in client had 166 handles instead of 165. The test uses the new `cabinetos_ipc::process::handle_count`. |
| Diagnostics: a panic on a thread that is not main | The panic hook writes the crash trace and closes the log writer. The core stopped after a panic in a connection task, and job threads abort, but a panic on any other thread of the core (a watcher, the config or themes debounce thread, the job progress thread, a terminal or plugin thread) only ended that thread. The core then ran on without it and without its log file. Now `cabinetos-diag` has `on_panic`, and the core uses it to cancel shutdown from a new `panic-stop` thread and exit 1 with the new `CoreError::Panicked`. A new hidden flag, `--self-test-thread-panic`, exists for the test. | dac92ac | `core_process::a_panic_on_any_thread_stops_the_core`: the old core was still running after 10 s. |
| Marketplace index cache | The cache was written in place, and rewritten even after a 304. An interrupted write left half a file, the server kept answering 304 to the saved tag, and every refresh failed until the index changed on the server. Now a 304 counts only if the cached copy reads back whole; otherwise the index is fetched again without the tag. Cache files are written to a temporary file and renamed, and a 304 rewrites only the tag file. | 7803ebe | `install::a_damaged_cached_index_is_fetched_again`: "EOF while parsing". |
| Config: a folder in place of the file | Windows reports reading a folder as "Access is denied", so the error pointed at permissions, and was reported again at every change in the directory. Now it says "it is a folder; the settings need a file there", once (like a deleted file), when the core opens the config, reloads it, and refuses a change. | d800e18 | `store::tests::a_folder_in_the_file_s_place_is_named_and_reported_once`: got "Access is denied. (os error 5)". |
| IPC: a client that leaves in the middle of a frame | This was logged as a WARN "malformed frame", which points at a protocol bug that is not there. An `UnexpectedEof` or `BrokenPipe` inside a frame is now INFO "the client left in the middle of a frame". | 8443173 | `core_process::a_client_that_leaves_in_the_middle_of_a_frame_is_logged_as_leaving`: found the WARN line. |
| Jobs: pausing after a disk-full pause | A job pauses itself for a full disk, and the decision on that conflict resumes it. If a client paused the job on purpose after that, the decision still resumed it. Now a client's `pause()` clears the disk-full mark, and the engine uses its own `pause_for_disk_full()`. | 15a6e87 | `job::tests::a_pause_a_client_asks_for_outlives_the_disk_full_pause`: the mark stayed set. |
| Search walk: the docs said more than the code does | indexer.md said the walk takes "at most 2 s". The limits are checked before each folder and every folder is read whole, so one huge folder, or a network folder whose server stopped answering, holds the walk longer. Docs only: no code change, so no test. | 93120d9 | — |

**Looked at and found nothing wrong:**
- **IPC framing:**
  - the 16 MiB limit is checked before anything is allocated;
  - an oversized frame gets `frame_too_large`, then the connection closes;
  - bad JSON gets an error reply and the connection survives;
  - `accept` replaces the pipe instance even when a connect fails.
- **Listing lifetime:**
  - closing a listing twice answers `no_such_listing`;
  - `describe_entries` keeps its own reference to the section while it reads;
  - a client that dies: Windows closes its handles.
- **Jobs:**
  - cancel while a conflict waits: cancel wakes the workers' condition variable, and finishing clears waiting conflicts and `conflicts_open`;
  - pause at the last file: the job completes; a pause inside the copy blocks in the copy callback;
  - a source that vanishes mid-job counts as failed, alone;
  - a destination folder deleted mid-job gives an I/O conflict (retry or skip);
  - a panic on a job thread aborts the process, so disks are never left locked.
- **Plugins:**
  - an error from a host function traps the guest and goes down the crash path;
  - fuel and the deadline are set fresh for every call;
  - a call stuck in a host function is given up after the call timeout plus a grace period, and the instance counts as crashed;
  - restarts are bounded: 3 crashes in 10 minutes stops them, with 5 s between tries; a failed start is not retried;
  - `before-job` waits with its own deadline.
- **Terminal:**
  - closing while output is buffered discards it;
  - a client that never reads gets back-pressure through the 1 MiB buffer;
  - after the shell exits, output that does not fit is dropped so the pseudo-console can close.
- **Marketplace:**
  - staging is always cleaned; a stale staging folder of the same ID goes at the next install;
  - a download longer than the index says is stopped once it passes that size;
  - a shorter or different download fails the hash check;
  - an item whose download points at the index fails the hash or kind check;
  - zip entries are refused when they escape the folder, are links, exceed 1,000, or unpack beyond 256 MiB.
- **Config watcher:**
  - a half-written save is reported as `config_error`, then resolved by the complete save; a burst within 100 ms is read once.
- **Ring buffer:**
  - writers never take a lock;
  - the hook reads the ring without waiting, with a fallback to the queue.
- **Indexer:**
  - a journal that wraps triggers a rebuild, and the old index answers searches meanwhile;
  - a volume removed during a build fails with a reason; during follow-up it fails after repeated read errors.

**Left as is, with reasons:**
- **The per-connection queue of outgoing messages is unbounded.** For a frozen client it grows by about 43 MB/hour at 30 Hz job progress. The core ends when its window ends, and a cap would have to drop the connection and lose its state.
- **A removed volume keeps its old index.** Its hits come back with `complete: false`. A volume that is plugged in again is not indexed until the indexer restarts.
- **A journal that wraps faster than a build takes rebuilds again.** Each round is bounded by the build time.

### Part B: the six leftovers
1. **`install_finished.installed_version`** (c1030cd).
   - Type: `Option<String>`, JSON `installed_version`, left out when absent.
   - On success: the version just installed. On a failed update: the version from before. Absent when nothing is installed.
   - It is read from the record of installs.
   - The event schema is regenerated.
   - The CLI prints `installed version: 0.1.0`; after a failed update it prints `<id> <version> stays installed`.
   - The UI agent has already adopted it (aefe88b), so the name stays.
2. **CLI items per second** (de411e5, then 27fba8f).
   - A job without bytes shows `84%  412 items/s  files 8412/10001` (one decimal below 10).
   - The live check showed two leftovers, now fixed:
     - the final line printed `0.0 items/s` (the protocol's 0 in the final record); a pace of 0 is now left out;
     - the summary printed `0 B/s on average`; it now reads `in 4.4 s, 6825 items/s on average`.
3. **CI parses `build/*.ps1`** (07ca269; the job's YAML went into ca2367c).
   - `build/check-scripts.ps1` parses every script with the running PowerShell's parser and runs nothing.
   - Errors are printed as GitHub annotations.
   - Scripts that start with `#Requires -Version 7` are skipped in Windows PowerShell 5.1.
   - The `scripts` job runs it in pwsh and in powershell 5.1 when `build/**` changes.
4. **Shipped theme updates** (bf9c758).
   - `.shipped.json` in the themes folder records the SHA-256 of every shipped file the core wrote, per theme ID.
   - Versions that shipped before the record existed are known to the code: `default` 1.0.0 = `789d478e…`. Test data: `testdata/default-1.0.0.json`, taken from git.
   - An unedited copy is replaced atomically; an edited copy is kept; the log says which.
   - The listing skips dotfiles.
5. **`help.about`** (2c80d5f).
   - It already existed, with target `core`: the core answered with its versions and config path. It is now target `ui`, with no default key.
   - The core answers it with `command_routed`, and the core runs none of its own registry commands any more.
   - The About view can get the versions from `welcome` and the config path from `get_config`.
6. **CI on demand** (ca2367c).
   - Triggers are now only `workflow_dispatch` and `pull_request` into `main`. Path selection and concurrency groups are unchanged.
   - A run started by hand runs every job, because it has no earlier commit to compare with.
   - `docs/dev-setup.md` has a new "CI, on demand" section.

## Commits
a4582ba, dac92ac, 7803ebe, d800e18, 8443173, 15a6e87, 93120d9, c1030cd, de411e5, 2c80d5f, bf9c758, ca2367c, 07ca269, 27fba8f.

## Checks
- **The five checks on 27fba8f:** 496 passed, 0 failed, 5 ignored. clippy with `-D warnings` is clean, fmt is clean, and `cargo deny` says advisories, bans, licenses and sources are ok.
- **UI:** 429 passed, 0 failed, 0 skipped (`dotnet build` and `dotnet test` from `ui/`). Its end-to-end tests ran against this core.
- **CI:** not started. It is on demand now and the account's minutes are used up.
- **ci.yml:** actionlint is not installed, so I checked it by a YAML parse and a structure check. I also ran the `changes` job's shell script locally:
  - a manual run turned on all three jobs;
  - a commit that touches only `build/` turned on only `scripts`;
  - a commit that touches only `core/` turned on only `core`.
- **check-scripts.ps1:** it passes on the five real scripts in both PowerShells, and exits 1 with line annotations on a broken script.

## Live check
Debug core and CLI; every folder was under `%TEMP%\cabinetos-core-test\review-live` and was removed afterwards.
```
== 3. themes folder before the start: default 1.0.0 (unedited 1.0.0), nord named 'My Nord' (edited)
== 3. after the core's first start
   default.json is version 1.1.0, kind system
   nord.json is named 'My Nord'
   .shipped.json: { "catppuccin-mocha": [ "c98480a5…" ], "default": [ "d244b033…" ], "rose-pine-moon": [ "cef51eef…" ] }
> cabinetos-cli themes list
   * default            Default            system accent system   mica plain         1.1.0 by CabinetOS
     nord               My Nord            dark   accent #88C0D0  mica #2E3440 0.88  1.0.0 by CabinetOS   (+ the other two)
== 1. > cabinetos-cli market install hello
   hello: 26.37 KiB of 26.37 KiB
   installed hello 0.1.0 (plugin)
   installed version: 0.1.0
== 2. > cabinetos-cli delete --permanent <30,000 files in 30 folders>
    22%  4508 items/s  files 6677/30031
    48%  6475 items/s  files 14522/30031
    70%  6441 items/s  files 21287/30031
    93%  6500 items/s  files 28042/30031
   100%  0.0 items/s  files 30031/30031        <- before 27fba8f; now left out
   completed: 30031 of 30031 files and folders, 0 skipped, 0 failed; 0 B in 4.4 s, 0 B/s on average   <- now "in 4.4 s, 6825 items/s on average"
== 3. after an edit of default and a second start
   default.json is named 'My Default', version 1.1.0
   log: "updated an unedited shipped theme…" (default, 1st start), "keeping the edited copy of a shipped theme…" (nord; default on the 2nd start)
```

## Decided
- **The protocol version rule** (written into ipc.md): an optional field added to an existing message keeps the version, because every client ignores a field it does not know (the shell decodes with the default of System.Text.Json). A new message, a new value, a new required field or a changed meaning raises it. So `installed_version` stays in version 11. Undo: set `PROTOCOL_VERSION` to 12, and the shell's end-to-end test pin with it.
- **`help.about` goes to `ui`.** Undo: set its seed target back to `CORE` and restore the arm in `Settings::execute`.
- **The core stops on any panic.** Undo: remove `stop_on_panic` in `cabinetos-core/src/lib.rs`.
- **Section hand-off** at the moment the message is queued; the index cache refetch; the shipped-theme record. Undo for the themes: go back to writing a shipped theme only when it is missing.
- **CI on demand.** Undo: restore the `push` trigger.
- **The walk limit is documented, not changed.**

## Needs the user
Nothing.

## Known gaps
- The Part A "left as is" list above.
- Two cores share `installed.json` with a lock inside one process only; this is Class E of the next task.
- The disk-full fix is proven by a unit test on `Control`; no disk was actually filled.

## Noticed out of scope (for the UI agent)
- **Register a UI handler for `help.about`.** The core now routes it to the window; until the handler exists, the palette row finds none.
- **docs/ui.md is now out of date for `help.about`.** Its row says "Runs in the core; the result is shown in a dialog". That doc is the UI agent's.

## Ready text: PLAN.md, a paragraph under Phase 10
```
**Self-review (core), 2026-09-29.** A reviewer's pass over the core crates, looking for untested edge cases, error paths that mislabel or swallow, resources not released, and docs that promise more than the code does. Six fixes, each with a test that failed before it. A listing's shared-memory handle is now given to the window only in the step that announces it: before, a refresh cancelled when a changing folder was closed left a handle, and its memory, in the window until it exited. A panic on any thread of the core now stops it (exit 1, the window starts a new core): before, a panic on a watcher's or a plugin's thread ended only that thread, and the core ran on without it and without its log file, which the crash hook closes. A cached marketplace index that cannot be read is fetched again: before, a half-written cache and the server's 304 kept every refresh failing. A folder in place of `cabinetos.json` is reported as a folder, once, not as "Access is denied". A window that leaves in the middle of a frame is logged as leaving, not as a malformed frame. A pause a client asks for is no longer undone by the decision on a full disk. The docs of the search walk now say that its 2 s limit is checked between folders, so one slow network folder can hold it longer. Also built: `install_finished` names the version installed after it (`installed_version`, optional, still protocol 11, with the rule for what raises the version written in ipc.md); `help.about` is a command of the window, for its About view; an unedited copy of a shipped theme follows the version the core ships (`.shipped.json` records what the core wrote; an edited copy is kept, and `default` 1.0.0 now becomes 1.1.0); the CLI shows items per second for a job that moves no bytes. CI runs only on demand and for pull requests, since the free minutes for September are used up: every change is verified on the development PC, and CI runs once per accepted phase with `gh workflow run ci.yml`; it also parses the `build/` scripts in both PowerShells. 496 core tests.
```
README: the core test count `482` becomes `496`. The UI count was 429 on my run.

## Files
All paths are under `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\`.

- Code:
  - `core\crates\cabinetos-core\src\listing.rs`
  - `core\crates\cabinetos-core\src\connection.rs`
  - `core\crates\cabinetos-core\src\lib.rs`
  - `core\crates\cabinetos-core\src\main.rs`
  - `core\crates\cabinetos-core\src\market.rs`
  - `core\crates\cabinetos-core\src\settings.rs`
  - `core\crates\cabinetos-core\src\search.rs`
  - `core\crates\cabinetos-ipc\src\process.rs`
  - `core\crates\cabinetos-diag\src\panic.rs`
  - `core\crates\cabinetos-diag\src\lib.rs`
  - `core\crates\cabinetos-market\src\index.rs`
  - `core\crates\cabinetos-config\src\store.rs`
  - `core\crates\cabinetos-jobs\src\job.rs`
  - `core\crates\cabinetos-jobs\src\run.rs`
  - `core\crates\cabinetos-protocol\src\message.rs`
  - `core\crates\cabinetos-cli\src\market.rs`
  - `core\crates\cabinetos-cli\src\jobs.rs`
  - `core\crates\cabinetos-commands\src\registry.rs`
  - `core\crates\cabinetos-themes\src\lib.rs`
  - `core\crates\cabinetos-themes\Cargo.toml`
- New test data: `core\crates\cabinetos-themes\testdata\default-1.0.0.json`.
- Tests:
  - `core\crates\cabinetos-core\tests\core_process.rs`
  - `core\crates\cabinetos-core\tests\market.rs`
  - `core\crates\cabinetos-core\tests\config.rs`
  - `core\crates\cabinetos-market\tests\install.rs`
- Schemas: `sdk\protocol\event.schema.json`, `sdk\protocol\request.schema.json`.
- CI and scripts: `.github\workflows\ci.yml`, `build\check-scripts.ps1` (new).
- Docs:
  - `docs\ipc.md`
  - `docs\diagnostics.md`
  - `docs\marketplace.md`
  - `docs\config.md`
  - `docs\jobs.md`
  - `docs\indexer.md`
  - `docs\keybindings.md`
  - `docs\themes.md`
  - `docs\dev-setup.md`

Recap: the self-review found six real problems and fixed them with failing-first tests. The six leftovers are done and the live checks pass, with 496 core and 429 UI tests green. Next in the queue: step 2 (edge cases A–E), then Commander Compact.
