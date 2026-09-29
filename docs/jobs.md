# Jobs: copy, move, delete

How the core copies, moves and deletes files. File operations never block
the interface and never stall on one bad file. Constitution Article 1
(Zero-Compromise Performance) and Article 5 (Dual-Pane Foundation); brief
§3; the design's file operations flyout (view E in
[design/README.md](design/README.md)).

The source of truth is the Rust crate `core/crates/cabinetos-jobs`. The
messages are in [ipc.md](ipc.md), section "Jobs".

## The job model

A client starts a **job** with `start_job`:

| Field | Meaning |
|---|---|
| `kind` | `{"type":"copy"}`, `{"type":"move"}`, or `{"type":"delete","permanent":false}`. A delete goes to the Recycle Bin unless `permanent` is `true`. |
| `sources` | Absolute paths of the files and folders. |
| `destination` | For a copy or a move: the absolute path of the folder they go into. The core creates it when it does not exist. A delete has none. |
| `options.on_conflict` | What to do when a file already exists at the destination: `ask` (default), `overwrite`, `overwrite_if_newer`, `skip`, `rename`. |
| `options.copy_links` | `as_link` (default): a symbolic link or junction is copied as a link that points where the original points. `follow_target`: what it points to is copied instead. |
| `options.verify` | After each copy, compare the sizes and sampled bytes of the source and the copy. Default `false`. |
| `options.preserve_timestamps` | Give each copy the creation, last-access and last-write times of its source. Default `true`. |

The core checks the request before it answers `job_started`. It refuses
with `invalid_path` a job with no sources, a relative path, a volume root
(`C:\`) as a source, a copy or move without a destination, a destination
that is a file, a folder copied into itself, a source that is already in
the destination folder, one source inside another, and a delete with a
destination. A source that does not exist gets `not_found`.

**States.** `job_state_changed` reports each change:

```text
queued ──► scanning ──► running ──► completed
   │           │           │    ├─► completed_with_errors  (some files failed)
   │           │           │    ├─► cancelled
   │           │           │    └─► failed {message}       (the job as a whole)
   └───────────┴─────┬─────┘
          paused ◄───┘  (pause, or a full disk; resume goes back)
```

- `queued`: waiting for its disks (see "The scheduler").
- `scanning`: walking the sources to count files and bytes.
- `running`: working. Files that wait for a conflict decision do not
  change the state; `conflicts_open` in the progress counts them.
- `failed`: an error that concerns the whole job, for example a
  destination folder that cannot be created, or a plugin that stopped the
  job before it started (`denied by plugin <id>: <reason>`). A failing file
  does not fail the job; it is counted in `files_failed`.

**Progress** (`job_progress`): `bytes_done` and `bytes_total`,
`files_done` and `files_total` (folders count as items too),
`files_skipped` and `files_failed` (both included in `files_done`),
`conflicts_open`, `current_path`, `speed_bps`, `items_per_second`,
`eta_seconds` and `elapsed_ms`. A skipped or failed file leaves `bytes_total`, so the
percentage still ends at 100. A delete and a move on one volume count
items, not bytes: their `bytes_total` is 0.

## The scheduler

Brief §3 asks for drive-aware queuing: copies on one spinning disk take
turns, and copies on different fast disks run together.

- **Disks.** When a job is queued, the core finds the physical disk under
  each source and under the destination, with the Phase 2 volume query:
  the disk number and its seek penalty (spinning or solid state). A disk
  that does not answer, such as a network share or some USB bridges,
  counts as spinning. Several sources in one folder are looked up once.
- **Slots.** A spinning disk runs one job at a time; a solid state disk
  runs up to four. A job takes a slot on every disk it touches, and it
  starts only when it can take all of them at once. A disk listed twice
  counts once.
- **Order.** Waiting jobs keep their order on each disk. A job that cannot
  start yet reserves a slot on each of its disks, so a later job cannot
  overtake it there and keep it waiting forever. Deletes and moves on one
  volume do little I/O, so they go ahead of copies on the same disk; within
  each group it is first come, first served.
- **Pause while waiting.** A queued job that is paused is passed over and
  reserves nothing. A running job that is paused keeps its slots.
- **Files in flight.** Inside one job, files go one at a time when the
  job touches a spinning or unknown disk. When all its disks are solid
  state, four files are copied at once. Measured on this PC (NVMe, 10,101
  files of 1–64 KiB, release build):

  | Files in flight | Time | Files per second |
  |---|---|---|
  | 1 | 6.2–6.8 s | 1,480–1,620 |
  | 2 | 4.4–4.6 s | 2,210–2,310 |
  | 4 | 3.3–3.7 s | 2,760–3,020 |
  | 8 | 3.4–3.7 s | 2,700–2,970 |

  Eight gave nothing more than four. The test is
  `measure_files_in_flight` in `cabinetos-jobs/tests/engine.rs`.

## Inside a job

A job runs on a thread of its own (`job-<id>`, and `job-<id>-<n>` for the
extra workers).

1. **Scanning.** The sources are walked with the Phase 2 enumeration
   (`NtQueryDirectoryFile`: names, sizes, times and attributes in one
   pass), hidden and system files included. A folder that cannot be read
   counts as one failed item, and the job goes on. Then every Core Plugin
   with the capability `jobs:intercept` sees the job with its totals
   (`before-job`, [plugins.md](plugins.md)); one refusal fails the job
   before anything is written, not even its destination folder. A plugin
   that crashes or does not answer within its 500 ms deadline counts as
   allowing it.
2. **Folders.** For a copy or a move across volumes, the destination
   folders are created first, in order. A folder that already exists is
   merged into: its files meet the conflict rules one by one.
3. **Files.** The workers take the files from the job's queue.
4. **Finishing.** Folders get their source's times, deepest first (their
   contents changed them until then). A move removes the source folders it
   emptied; a folder that still holds a skipped file stays.

**Copy** uses `CopyFileExW`. **A move on one volume** is a rename of each
source with `MoveFileExW`: a folder of 1,000 files moves in a few
milliseconds, and its files keep their file IDs. When a folder of that
name already exists at the destination and the decision is `overwrite`,
the move merges instead: the folder's files are renamed one by one, each
with its own conflict. **A move across volumes** copies a file, verifies
it when asked, and only then deletes the source file. A read-only source
is moved too.

**Delete** to the Recycle Bin uses the shell's `IFileOperation`, as
Explorer does, on the job's thread in a single-threaded COM apartment, one
source at a time (a folder is one operation). The shell, told not to ask,
would delete for good whatever its bin cannot take, without a word. So the
engine checks first: an item bigger than the volume's bin (its
`MaxCapacity`, or 5% of the volume when Windows has no size on record), an
item on a drive whose bin is turned off (`NukeOnDelete`), and an item on a
drive without a bin (removable and network drives) become a
`recycle_bin_too_small` conflict, and nothing is deleted until the user
answers `delete_permanently` or `skip`. A permanent delete removes
files with `DeleteFileW` and folders with `RemoveDirectoryW`, contents
before their folder. A link (symbolic link or junction) is removed, never
followed, so what it points to is safe. A read-only file is an
`access_denied` conflict unless the policy or the decision is `overwrite`,
which clears the read-only attribute first. A file that is already gone
counts as deleted. A folder that is not empty when its turn comes is tried
again at the end; if something in it was skipped, it stays and counts as
skipped.

## Conflicts

Brief §3: a file that hits a problem must not block the rest. It is set
aside, the clients hear about it, and the job goes on.

```text
worker                          core                            client (UI)
copy a.txt: FILE_EXISTS
  on_conflict is ask:
  set a.txt aside ──────────►  job_conflict {conflict_id 9} ──► shows "a.txt exists"
copy b.txt, c.txt, ...          the job stays running;
                                progress: conflicts_open 1 ───► shows the count
                                                          ◄──── resolve_conflict {9, overwrite}
a.txt back in the queue ◄─────
copy a.txt, replacing: done
                                job_progress, ... ────────────►
                                job_state_changed completed ──►
```

| Conflict kind | When | Notes |
|---|---|---|
| `file_exists` | The destination has that name. Carries both sizes and write times. | With `on_conflict` other than `ask`, the policy decides at once and no event is sent. |
| `access_denied` | Windows refused, for example a read-only file. | `overwrite` clears the read-only attribute, then tries again. |
| `sharing_violation` | Another program has the file open. | `retry` once it is closed. |
| `path_too_long` | The destination file system refuses the length. In a delete to the Recycle Bin: a path in the item has 260 characters or more (below). | In a delete to the Recycle Bin, `delete_permanently`, `skip` and `retry` answer it; `destination` is absent. |
| `disk_full` | The destination is full. | The whole job pauses: every other file would fail the same way. Any decision on this conflict resumes the job, unless a client paused the job itself after that: then only `resume` does. |
| `source_vanished` | The source disappeared after the scan. | No event: the file counts as failed. |
| `recycle_bin_too_small` | A Recycle Bin delete of an item the bin cannot take. Carries the item's `size`. | Nothing is deleted. Only `delete_permanently` or `skip` answer it (`retry` checks again, for example after the bin was made bigger); any other answer gets `invalid_resolution`. No policy answers it on its own; a rule made with `apply_to_same_kind` does. |
| `io` | Any other error, with the Windows code and text. | Also used when `verify` finds a difference (code 23); the bad copy is removed first. |

Decisions (`resolve_conflict`):

| Resolution | Effect |
|---|---|
| `overwrite` | Replace the existing file. For `access_denied`, clear the read-only attribute first. |
| `skip` | Leave the file out; it counts in `files_skipped`. A skipped folder skips everything in it. |
| `rename` | Copy or move under another name in the same folder: `new_name`, or without it a free name `name (2).ext`. |
| `retry` | Try once more, the same way. |
| `delete_permanently` | Delete for good what the Recycle Bin cannot take. Answers only `recycle_bin_too_small`, and `path_too_long` in a delete to the Recycle Bin. |
| `cancel_job` | Stop the whole job. |

### Links

A junction, a symbolic link or a mount point is one item to a job: the
link, never what it points to, unless `copy_links` says otherwise.

- **Delete** removes the link and leaves what it points to as it was, for
  a link given as the source and for one inside a folder being deleted
  (tested with a junction, a symbolic link to a folder and one to a file,
  each leading to a folder of files that stays whole).
- **Copy** with `as_link`, the default, makes a link that points where the
  original points, as robocopy does; with `follow_target` it copies what
  the link points to, as Explorer does for a junction. Following stops at
  a link that leads back into what is being copied: that link is copied as
  a link, so the job ends.
- **Move** on one volume renames the link; what it points to stays where
  it is. Across volumes the link is copied as a link, then removed. With
  `follow_target`, what the link points to is copied and then the link is
  removed; nothing reached through the link is ever deleted, so its target
  stays whole.
- **To the Recycle Bin** the shell takes the link. The test for that runs
  only where `CABINETOS_TEST_RECYCLE_BIN=1` (CI), like the other bin tests.

### Files not on this disk

A cloud file that is not downloaded (OneDrive's "online only") or a file
moved to other storage has its data elsewhere; the listing marks it (ipc.md,
"Not on this disk"). A copy reads the whole file with `CopyFileExW`, so the
sync provider downloads it while the job runs: that is what the user asked
for, and the job counts the file's full size, as the listing gives it. The
copy is an ordinary file whose data is here; it does not keep the offline
attribute. A move across volumes copies (downloading), then deletes the
source, which the provider may pass on to the cloud, as a move out of the
synced folder in Explorer does. Tested with the offline attribute standing
in for the cloud ones, which only a sync provider can set.

### The Recycle Bin and long paths

The shell's Recycle Bin takes paths shorter than 260 characters (UTF-16
units, as Windows counts them). For a longer one Explorer asks whether to
delete it permanently; with the silent flags the core passes, the shell
would answer that question itself, and a delete the user meant to undo
would be final. So before an item goes to the bin, the walk that sums its
size also measures its longest path (`measure_tree` in `cabinetos-fs`, the
walk the Size column's `measure_paths` uses too: ipc.md, "Folder sizes").
If that path has 260 characters or
more, the item waits on a `path_too_long` conflict and nothing is deleted:
`delete_permanently` deletes it for good (with `DeleteFileW`, which takes
any length), `skip` keeps it, `retry` measures again (after a folder on the
way was renamed, for example). A folder counts every path inside it, so a
deep `node_modules` asks too. This is checked before the bin's size.

Not tried: what the shell itself does with such a path. Trying it would
put a file into, or delete it past, the Recycle Bin of the machine running
the test.

- `apply_to_same_kind: true` also answers the job's other waiting
  conflicts of the same kind, and makes the decision a rule for later ones
  in that job. A rule from `rename` picks free names.
- A folder that waits for a decision holds its contents with it; they go
  on when the folder does.
- A job whose only remaining work is waiting files stays `running` with
  `conflicts_open` above 0 until the files are decided or the job is
  cancelled. Cancelling drops the waiting files.
- Waiting conflicts outlive the client that saw them: a client that says
  `hello` later gets every waiting conflict as a `job_conflict` event. It
  may get one twice (raised while it connected); conflicts are keyed by
  `conflict_id`.

### Names that differ only by case

Windows folders ignore case unless a folder was made case-sensitive
(`fsutil file setCaseSensitiveInfo`, WSL's folders). So `Report.txt` copied
into a folder that holds `report.txt` is a `file_exists` conflict, as it
would be with the same spelling, and the conflict's `destination` names the
file that is there as the folder spells it (`…\report.txt`), which may
differ from the name being copied. The answers work as for any conflict:

- **Overwrite** by a copy writes into the file that is there, which keeps
  its spelling (`report.txt`). Overwrite by a move on one volume is a
  rename that replaces it, so the moved file's spelling (`Report.txt`)
  takes its place.
- **Rename** (keep both) gives `Report (2).txt`, a name that is free in any
  case.

A copy does not carry a folder's case sensitivity: the folder it makes is
an ordinary one, like any new folder under an ordinary parent, as Explorer
and robocopy do. So the twins of a case-sensitive folder meet: the second
one is a `file_exists` conflict with the first, decided like any other.
Names beyond ASCII behave the same way (`ЗВІТ.txt` and `звіт.txt` are one
name in an ordinary folder), and a name is never normalized: `café`
composed and `café` decomposed are two names, so both copy side by side.

## The 30 Hz rule

Brief §3: progress must not flood the pipe. The copying threads only add
to atomic counters after each chunk. One publisher thread reads them on a
34 ms clock and sends a job's `job_progress` only when something changed
(the clock alone does not count). The gap between two events of one job is
never under 34 ms, so no one-second window holds more than 30 of them.
When a job ends, one final event with the final state always follows,
after the same gap, and then `job_state_changed`.

- **Speed** (`speed_bps`) is a moving average over about the last second:
  each reading weighs in by the time it covers. It drops to 0 while
  paused.
- **Pace** (`items_per_second`) is the same average over the files and
  folders handled, with two decimals, so a delete or a move on one
  volume, which move no bytes, shows its pace too. It is absent in a job's
  first record, and 0 while paused and in the final record.
- **Time left** (`eta_seconds`) is the remaining bytes divided by the
  speed. It is absent for the first two seconds, while the speed settles,
  and for jobs with no bytes to count.
- `cabinetos-cli copy ... --stats` prints how many events arrived in each
  second, by arrival and by the core's clock. A test copies 10,000 files
  and checks both counts.

## Backends and flags

| What | How |
|---|---|
| Copy a file | `CopyFileExW` with a progress routine. `COPY_FILE_FAIL_IF_EXISTS` unless the decision is to overwrite. `COPY_FILE_NO_BUFFERING` for files of 256 MiB and more: a huge copy does not push everything else out of the file cache. |
| Copy a link to a file | `CopyFileExW` with `COPY_FILE_COPY_SYMLINK`. That asks for the privilege to create symbolic links, which Developer Mode does not give; when it is missing, the link's reparse data is written into a new empty file, as for a link to a folder (Developer Mode allows that). |
| Copy a link to a folder | The reparse data is read with `FSCTL_GET_REPARSE_POINT` and written to a new folder with `FSCTL_SET_REPARSE_POINT`: the copy points where the original points. A junction needs no rights; a symbolic link needs the right to create one. |
| Pause | The progress routine blocks on a condition variable. The copying thread is the job's own, so blocking it stalls nothing else. |
| Cancel | The progress routine returns `PROGRESS_CANCEL`; Windows deletes the partial destination. |
| Times | `CopyFileExW` keeps the last-write time by itself (a test checks this); the copy's creation time is the moment of the copy. With `preserve_timestamps`, `SetFileTime` gives the copy the source's creation and last-access times too; folders get theirs at the end. |
| Verify | Sizes, then the whole content up to 1 MiB, or 16 samples of 64 KiB (first and last included) above. |
| Long paths | Every file call uses the verbatim form (`\\?\C:\...`), so any length works. The shell (Recycle Bin) takes plain paths shorter than 260 characters; a longer one waits on `path_too_long` ([above](#the-recycle-bin-and-long-paths)). |
| Recycle Bin | `IFileOperation` with `FOF_ALLOWUNDO`, `FOF_NOCONFIRMATION`, `FOF_NOERRORUI`, `FOF_SILENT` and `FOFX_RECYCLEONDELETE`. |

**Measured** (`cargo bench -p cabinetos-jobs --bench copy_file`, 2 GiB
file on C:, NVMe, 2026-09-28; nothing is adopted from it):

| Method | Median | Rate |
|---|---|---|
| `CopyFileExW`, buffered | 0.51 s | 4.2 GB/s |
| `CopyFileExW`, `COPY_FILE_NO_BUFFERING` | 1.37 s (spread 0.64–2.47 s) | 1.6 GB/s |
| `ReadFile`/`WriteFile`, 4 MiB, `FILE_FLAG_NO_BUFFERING` | 2.42 s | 0.89 GB/s |

The buffered figure mostly measures memory: the source was still in the
file cache, and Windows writes the copy out later. A plain synchronous
loop is the slowest, because it does not overlap reads and writes;
beating `CopyFileExW` would take overlapped I/O or IoRing, which PLAN.md
leaves to a later measurement. In the Phase 4 live check a 20 GiB file on
E: copied at about 3 GB/s with `COPY_FILE_NO_BUFFERING`.

## Jobs and clients

- Jobs belong to the core, not to the connection that started them. A UI
  that restarts finds its running copy with `list_jobs`, which also tells
  each job's kind, sources and destination.
- Job events go to every connection that said `hello`, like the
  configuration events.
- Job IDs and conflict IDs are unique for the life of the core. The core
  keeps the last 100 finished jobs for `list_jobs`.
- Pausing, resuming or cancelling a job that has ended does nothing and
  answers `ok`.
- When the core shuts down, it cancels every job and gives the running
  ones two seconds, so a cancelled copy removes its partial file.

## From the command line

```text
cabinetos-cli copy C:\photos D:\backup --stats
cabinetos-cli copy C:\photos D:\backup --on-conflict ask --resolve skip
cabinetos-cli move C:\inbox\report.pdf C:\archive
cabinetos-cli delete C:\old-folder --permanent
cabinetos-cli jobs
cabinetos-cli job pause 3
cabinetos-cli job resolve 3 9 overwrite
```

`copy` and `move` take the sources, then the folder they go into. The
CLI follows the job: on a terminal one progress line is rewritten in place
(`45%  1.2 GB / 2.7 GB  610.0 MB/s  eta 3 s  files 8412/10001
conflicts 1`); a job that moves no bytes, such as a delete, shows its pace
in items instead (`84%  412 items/s  files 8412/10001`). Into a file or a
pipe it prints one line per second.
Conflicts appear on their own lines, with the command that answers them;
`--resolve overwrite|skip|rename` answers them all automatically. Ctrl+C
stops following, not the job.
