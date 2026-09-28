## Phase 4 report

Context: Phase 4 of CabinetOS (the job engine: copy, move, delete). Everything is pushed to `origin main` (HEAD `3835069`), CI is green, and the working tree is clean. All test and live-check data stayed under `%TEMP%\cabinetos-jobs-test\` or `E:\cabinetos-scratch\`, and both folders are gone again.

### Built
- **Part 0** (`1b81210`): `cabinetos-diag` now creates the log folder before it builds the appender, so a first start prints nothing to stderr. There is a new error variant `DiagError::LogDir`. A new test in `core_process.rs` starts the core with a missing nested log folder and checks that stderr stays empty; it fails without the fix.
- **`cabinetos-protocol` v4** (`PROTOCOL_VERSION = 4`), new module `job.rs`:
  - Types: `JobKind`, `JobRequest`, `JobOptions`, `ConflictPolicy`, `LinkPolicy`, `JobState`, `JobProgress`, `JobInfo`, `Conflict`, `ConflictKind`, `Resolution`, `JobAction`.
  - Requests: `start_job`, `list_jobs`, `job_control`, `resolve_conflict`. Replies: `job_started`, `jobs`. Events: `job_progress`, `job_conflict`, `job_state_changed`. Error codes: `no_such_job`, `no_such_conflict`.
  - The schemas are regenerated.
- **`cabinetos-jobs`** (it was a stub). Unsafe code is only in `win.rs` and `recycle.rs`, each block with a `SAFETY:` comment.
  - `scheduler.rs`: pure per-disk slots with fake-disk unit tests.
  - `plan.rs`: walks the sources with the Phase 2 enumeration.
  - `run.rs`: the executor:
    - folders are created first, in order;
    - then 1 file at a time, or 4 in flight when every disk is solid state;
    - conflicts are set aside, and the files under a waiting folder wait with it;
    - automatic decisions, merge on move, folder times;
    - emptied source folders of a move are removed.
  - `win.rs`:
    - `CopyFileExW` with a progress routine: pause blocks it; cancel returns `PROGRESS_CANCEL`;
    - `MoveFileExW`, `DeleteFileW`, `RemoveDirectoryW`, `SetFileTime`, `GetFileAttributesExW`;
    - directory links copied with `FSCTL_GET/SET_REPARSE_POINT`.
  - `recycle.rs`: `IFileOperation` in a single-threaded COM apartment on the job's own thread.
  - `progress.rs`: at most one event per 34 ms per job, sent only on change; the speed is an average over about 1 s; the time left appears after 2 s.
  - `lib.rs`: `JobQueueManager` with `start`, `list`, `open_conflicts`, `control`, `resolve`, `shutdown`; path checks; disk lookup (one lookup per source folder).
  - `cabinetos-fs` now exposes `verbatim_wide` publicly.
- **Core:**
  - New `events.rs`: one broadcast hub for configuration and job events. `Settings` now publishes through it.
  - A `Services` struct is shared by all connections.
  - Job requests: `start_job` runs its path checks on the blocking pool; the others answer at once.
  - After `hello`, the core sends every conflict that already waits, so a restarted UI can answer it.
  - A client that falls behind also gets every job's current progress.
  - On shutdown the core cancels all jobs and gives them 2 s.
- **CLI:**
  - `copy <src>... <dst>` and `move ...`, with `--on-conflict ask|overwrite|skip|rename|newer`, `--verify`, `--resolve overwrite|skip|rename`, `--stats`.
  - `delete <path>... [--permanent]`, `jobs`, `job pause|resume|cancel <id>`, `job resolve <job> <conflict> <overwrite|skip|rename|retry|cancel>`.
  - The progress line is rewritten in place on a terminal; into a file or pipe it prints one line per second.
- **Docs:** new `docs/jobs.md`. Updated: `docs/ipc.md` (job section, tables, codes, version 4), `core/README.md`, the `docs/ARCHITECTURE.md` crate map and change log.
- **Optional benchmark** `benches/copy_file.rs` (done, because everything else was green).

### Commits (all pushed)
- `1b81210` diag: log folder first
- `c25dd6e` protocol v4
- `142aea5` job engine
- `6578acd` test lint fix. My check command for `142aea5` piped clippy into `head`, which hid clippy's failure, so the commit went in with 5 lint findings in a test file. Fixed in this follow-up commit; there was no amend.
- `e8ca76f` core integration
- `1fbda08` CLI
- `eebd7bf` timestamp test, and the Recycle Bin test turned on in CI
- `b61d3fa` docs
- `3835069` benchmark

### Checks
- The five checks pass locally.
- **266 tests, 5 of 5 full `cargo test --workspace` runs green.** Two tests are ignored by design: the cross-volume move and the measurement.
- CI is green on `3835069`: https://github.com/OliverD25/cabinetos/actions/runs/36375774215 (both jobs succeeded). The CI log shows `CABINETOS_TEST_RECYCLE_BIN: 1` and `the_recycle_bin_takes_a_file ... ok`, so the `IFileOperation` path ran on the runner.
- **Timing-sensitive tests:** I widened none this phase. The new timing-sensitive tests:
  - A same-volume move of 1,000 files must take under 50 ms (the spec's number). The test also checks that the file IDs are unchanged, which proves the move was a rename.
  - Pause and cancel on a 256 MiB file try up to 3 times if the copy ends before the pause lands.
  - The core throttle test allows at most 30 events per second by the core's clock (strict) and at most 32 by arrival time (the pipe can bunch events).

### Live check (release build, E: NVMe; the core's config and log were under `%TEMP%\cabinetos-jobs-test\live-core`)
**Source:** 10,000 files of 1–64 KiB (332 MB) plus one 20 GiB file (21.5 GB), written in 64 MiB blocks. E: had 1.8 TB free.

**Run 1:** `cabinetos-cli --pipe live4 copy E:\cabinetos-scratch\src E:\cabinetos-scratch\dst --stats` (exit 0)
```
job 1 started
  0%  files 0/0  scanning
  0%  124.2 MB / 21.8 GB  77.2 MB/s  files 3767/10022
  1%  241.9 MB / 21.8 GB  101.0 MB/s  eta 214 s  files 7317/10022
  5%  1.2 GB / 21.8 GB  842.8 MB/s  eta 25 s  files 10021/10022
 18%  3.9 GB / 21.8 GB  2.1 GB/s  eta 9 s  files 10021/10022
 35%  7.8 GB / 21.8 GB  3.2 GB/s  eta 5 s  files 10021/10022
 52%  11.5 GB / 21.8 GB  3.5 GB/s  eta 3 s  files 10021/10022
 66%  14.6 GB / 21.8 GB  3.2 GB/s  eta 3 s  files 10021/10022
 79%  17.4 GB / 21.8 GB  2.9 GB/s  eta 2 s  files 10021/10022
 94%  20.5 GB / 21.8 GB  3.0 GB/s  eta 1 s  files 10021/10022
100%  21.8 GB / 21.8 GB  0 B/s  files 10022/10022
completed: 10022 of 10022 files and folders, 0 skipped, 0 failed; 21.8 GB in 9.7 s, 2.3 GB/s on average
progress events: 209 in 9.7 s; at most 22 in one second as they arrived, at most 22 by the core's clock; per second: 22 21 22 21 22 21 21 22 21 16
```
**Result:** total 9.7 s, 2.3 GB/s on average, about 3 GB/s during the large file. **At most 22 progress events per second, which is under the limit of 30.**

**Run 2:** into the same destination, after I added 1,000 new files to `src`. Without them every file would conflict, and there would be no "rest" to go on. Command: `copy ... --on-conflict ask --resolve skip --stats` (exit 0). There were 10,001 conflict lines. Excerpt:
```
job 2 started
  0%  files 0/0  scanning
conflict 1: exists (21.5 GB here, 21.5 GB there): E:\cabinetos-scratch\src\large.bin -> E:\cabinetos-scratch\dst\src\large.bin
conflict 2: exists (38.3 kB here, 38.3 kB there): E:\cabinetos-scratch\src\group 19\file 000.bin -> E:\cabinetos-scratch\dst\src\group 19\file 000.bin
...   (line 6119, between conflict lines:)
 20%  33.3 MB / 163.3 MB  13.7 MB/s  files 7136/11023  conflicts 2
100%  33.3 MB / 33.3 MB  0 B/s  files 11023/11023
completed: 11023 of 11023 files and folders, 10001 skipped, 0 failed; 33.3 MB in 1.5 s, 21.9 MB/s on average
progress events: 34 in 1.5 s; at most 22 in one second as they arrived, at most 22 by the core's clock; per second: 22 12
```
The conflicts arrived and were answered while the 1,000 new files were copied.

**Cross-volume move:** `CABINETOS_TEST_SECOND_VOLUME=E:\cabinetos-scratch\second cargo test -p cabinetos-jobs --test engine -- --ignored a_move_across_volumes` → `test result: ok. 1 passed`. The test moved 200 files and a read-only file from `%TEMP%` (C:, disk 1) to E: (disk 4) with `verify` on. The sources are gone.

**Cleanup:** `cabinetos-cli --pipe live4 delete E:\cabinetos-scratch --permanent --stats` (exit 0)
```
100%  files 22049/22049
completed: 22049 of 22049 files and folders, 0 skipped, 0 failed; 0 B in 4.4 s, 0 B/s on average
progress events: 82 in 4.4 s; at most 22 in one second as they arrived, at most 22 by the core's clock; per second: 22 21 17 13 9
$ ls E:/cabinetos-scratch → No such file or directory
```
Afterwards the core was shut down (exit code 0), and `%TEMP%\cabinetos-jobs-test` was removed.

**Optional benchmark** (2 GiB file on C:, NVMe; nothing adopted):

| Method | Median | Rate |
|---|---|---|
| `CopyFileExW`, buffered | 0.51 s | 4.2 GB/s |
| `CopyFileExW` + `COPY_FILE_NO_BUFFERING` | 1.37 s (spread 0.64–2.47 s) | 1.6 GB/s |
| `ReadFile`/`WriteFile`, 4 MiB, unbuffered | 2.42 s | 0.89 GB/s |

The buffered figure mostly measures memory: the source was still in the file cache. The plain loop is the slowest because it never overlaps a read with a write. These numbers are also in `docs/jobs.md`.

**Files in flight** (10,101 files of 1–64 KiB, release build; stored in `docs/jobs.md`):

| In flight | Time |
|---|---|
| 1 | 6.2–6.8 s |
| 2 | 4.4–4.6 s |
| 4 | 3.3–3.7 s — kept |
| 8 | 3.4–3.7 s |

**Timestamps (the spec asked me to check):** `CopyFileExW` keeps the last-write time by itself. The copy's creation time is the moment of the copy. `preserve_timestamps` adds the creation and last-access times through `SetFileTime`. The test `copyfile_keeps_the_write_time_and_the_option_adds_the_creation_time` keeps this checked.

### Decided
**Test data and safety**
- The second volume for the cross-volume test and the live check was `E:\cabinetos-scratch\second`, with the sources in `%TEMP%` on C:. The spec said `D:\cabinetos-scratch` — because the operating rule allows job data only in `%TEMP%\cabinetos-jobs-test\` and `E:\cabinetos-scratch\`, and C: to E: is already a cross-volume move (disks 1 and 4) — undo: run the test with `CABINETOS_TEST_SECOND_VOLUME` pointing to a D: folder.
- The Recycle Bin test runs only when `CABINETOS_TEST_RECYCLE_BIN=1`, which CI sets — because it puts a real file into the user's Recycle Bin, outside the allowed folders; the CI runner is thrown away after each run — undo: remove the guard in `engine.rs` and the `env` lines in `ci.yml`.

**Protocol**
- Nested enums (`kind`, `state`, conflict `kind`, `resolution`) are objects tagged by `"type"` — because the C# side can then use one discriminator pattern everywhere — undo: change the serde attributes in `protocol/src/job.rs` and regenerate the schemas.
- `list_jobs` returns `JobInfo` (the progress plus `kind`, `sources`, `destination`), not bare `JobProgress` — because a restarted UI must be able to say what a running job does — undo: return only the progress.
- `Resolution::Rename { new_name }` has an optional `new_name`; when absent the core picks a free name `name (2).ext` — because `--resolve rename` and rules made with `apply_to_same_kind` need a rename without a name — undo: make it required.
- After `hello` the core sends every waiting conflict as a `job_conflict` event; a client may get one twice and should key conflicts by ID — because otherwise a conflict of a running job could never be answered after a UI restart — undo: remove the loop in `connection.rs::hello`.

**Scheduler**
- The order is queued → scanning → running. The walk happens while the job holds its disk slots, and the disk set comes from the source roots and the destination — because a walk on a spinning disk during another job's copy makes the heads jump, and first-in-first-out order needs the disks before the walk — undo: plan before `submit`.
- A solid state disk runs 4 jobs at once — undo: `EngineConfig::solid_state_jobs`.
- 4 files in flight in a job whose disks are all solid state (measured above) — undo: `EngineConfig::solid_state_files_in_flight`.
- A waiting job reserves a slot on each of its disks, so no later job can overtake it there. Deletes and same-volume moves go ahead of copies; within each group it is first come, first served — undo: `scheduler.rs`.
- A paused queued job is passed over and reserves nothing. A paused running job keeps its slots — undo: `scheduler.rs` (`hold`, `release`).
- A disk of unknown kind (network share, some USB) counts as a spinning disk; a share is keyed by its volume or root, so jobs on different shares do not block each other — undo: `disk_of`.

**Copy details**
- Files of 256 MiB and more are copied with `COPY_FILE_NO_BUFFERING` — because a huge copy then does not push everything else out of the file cache — undo: `EngineConfig::unbuffered_from`.
- File links are copied with `COPY_FILE_COPY_SYMLINK`; folder links (junctions, directory symlinks) with `FSCTL_GET/SET_REPARSE_POINT` — because `CopyFileExW` does not copy folders — undo: `win.rs`, `run.rs`.
- With `follow_target`, a folder link that leads back into what is being copied is copied as a link instead — because following it would never end — undo: `plan.rs::Walk`.
- `verify` compares the whole file up to 1 MiB and 16 samples of 64 KiB above. A mismatch removes the bad copy and raises an `io` conflict with code 23 — undo: `run.rs`.
- Without `preserve_timestamps` the engine does not call `SetFileTime` (the write time is kept anyway) — undo: `run.rs`.

**Progress**
- The gap between two progress events is 34 ms, not 33.3 ms — because with 33.3 ms one 1-second window could hold 31 events — undo: `MIN_PROGRESS_GAP`.
- Events are sent only when a value changed; the final one always goes out, after the gap, and is followed by `job_state_changed` — undo: `progress::publish`.
- No time left for jobs with no bytes (deletes, same-volume moves) — undo: `progress::eta`.
- A skipped or failed file leaves `bytes_total`, so the percentage ends at 100. Folders count as items in `files_total` — undo: `run.rs::count`.
- `elapsed_ms` starts when scanning starts (after queued) — undo: `run.rs` (`started`).

**Conflicts**
- A job whose only remaining work is waiting files stays `running` with `conflicts_open` above 0; there is no separate state — undo: add a state.
- `apply_to_same_kind` also answers the conflicts of that kind that already wait, and a rule made from rename picks free names — undo: `JobQueueManager::resolve`.
- Overwrite clears a read-only attribute only when it answers an `access_denied` conflict. For a permanent delete, the policy `overwrite` also clears it (spec) — because a read-only file signals protection, so the user agrees twice — undo: `Work::may_clear_read_only`.
- Any decision on a `disk_full` conflict resumes the job — undo: `resolve`.
- `source_vanished`: no event, counted as failed (spec). In a delete job, a file that is already gone counts as deleted — undo: `run.rs::remove`.

**Move and delete**
- A same-volume move onto an existing folder with overwrite merges file by file (a rename per file) — undo: `expand_merge`.
- A cross-volume move also moves read-only sources. A source that cannot be deleted after its copy counts as failed and stays — undo: `delete_moved_source`.
- The Recycle Bin delete runs one `IFileOperation` per source; progress moves per source — undo: `run.rs::recycle`.

**Requests and the job model**
- The destination is always a folder, created when missing. The core refuses: a volume root as source, nested sources, a source already in the destination (no copy in place), relative paths, a delete with a destination — undo: `validate`.
- Pause, resume or cancel of an ended job answers `ok` and does nothing — undo: `control`.
- The core keeps the last 100 finished jobs — undo: `EngineConfig::finished_jobs_kept`.
- A panic in a job thread aborts the process, after the panic hook has written the crash trace — because the core's rule is fail fast, and a dead job thread would otherwise hold its disk slots forever — undo: the `catch_unwind` branch in `admit`.
- On shutdown the core cancels all jobs with a 2 s grace — undo: `JOBS_GRACE`.
- One event hub (1024 events) for configuration and jobs — undo: `events.rs`.

**CLI and live check**
- The CLI exits 0 only for `completed`. Ctrl+C stops following, not the job. Into a file it prints one line per second — undo: `cli/src/jobs.rs`.
- Live check: the large file is 20 GiB (21.5 GB), because E: had more than 100 GB free. Run 2 added 1,000 new files so the rest had work.

### Needs the user
- **`docs/PLAN.md`, Phase 4 heading**: `### Phase 4 — Job engine (copy, move, delete) — done 2026-09-28`. After its "Done when" line: `Measured 2026-09-28 on this PC (E:, NVMe, release build): 10,000 files of 1–64 KiB plus one 20 GiB file copied in 9.7 s (2.3 GB/s); the progress stream peaked at 22 events per second (limit 30). A second run into the same destination skipped 10,001 existing files through conflicts while 1,000 new files were copied, in 1.5 s. Details: [jobs.md](jobs.md).`
- **README status line**: `Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, and runs copy, move and delete jobs on per-disk queues, over a user-only named pipe (core/, 266 tests, CI green). Phase 5, the WinUI 3 shell, is next.`
- **Optional, if you want the cross-volume test on D: as first written.** It writes test data to `D:\cabinetos-scratch\`, which the operating rules did not allow me. From `core/`: `CABINETOS_TEST_SECOND_VOLUME='D:\cabinetos-scratch\second' cargo test -p cabinetos-jobs --test engine -- --ignored a_move_across_volumes`. Afterwards remove the empty folder `D:\cabinetos-scratch`.
- **Optional, to see the Recycle Bin test on this PC.** It sends one small file to your Recycle Bin. From `core/`: `CABINETOS_TEST_RECYCLE_BIN=1 cargo test -p cabinetos-jobs --test engine the_recycle_bin`

### Known gaps
- **Progress rate:** it peaks at about 22 events per second, not 30. Windows timers tick every 15.6 ms, so a 34 ms wait lasts about 47 ms. A high-resolution waitable timer for the publisher would reach about 29.
- **Recycle Bin, too-big files:** with `FOF_NOCONFIRMATION` (the spec's flags), Windows deletes a file too big for the Recycle Bin permanently, without asking. `FOF_WANTNUKEWARNING` plus a conflict would be safer.
- **Recycle Bin, granularity:** progress, pause and cancel act per source root, not per file.
- **Plans in memory:** a plan is fully in memory (a few hundred bytes per item), so millions of files need hundreds of MB.
- **Unreadable folders:** a folder that cannot be read while planning counts as failed and is not offered for retry.
- **No persistence:** jobs and waiting conflicts live only in memory and are lost when the core restarts.
- **Security descriptors:** `CopyFileExW` does not copy them; the copy gets the destination folder's permissions.
- **Symbolic links need rights:** copying a symbolic link as a link needs the symlink right or Developer Mode; junctions need none.
- **Recycle Bin paths:** the shell takes plain paths, so very long paths may fail there.
- **Vanished sources are silent:** `source_vanished` sends no event, so a UI sees only `files_failed` and the log.
- **Move leftovers:** a move whose source cannot be deleted after its copy leaves the file in both places (counted as failed).
- **Test folder:** each test run leaves an empty `%TEMP%\cabinetos-jobs-test\` folder behind (each test removes only its own subfolder).

### Noticed out of scope
- **Small-file speed:** copying small files (about 3,000 files per second with 4 in flight) is probably limited by real-time antivirus scanning. I cannot measure this without turning scanning off.
- **README documents table:** it does not list `ipc.md`, `config.md`, `keybindings.md` or `jobs.md`.
