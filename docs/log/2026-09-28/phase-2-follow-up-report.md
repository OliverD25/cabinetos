## Phase 2 follow-up report: the pipelined listing

Context: `core/crates/cabinetos-fs`, the listing engine. Repo root: `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos`.

**Result: goal met.** Criterion medians of `list_and_publish` (pipelined) on the 100k fixture were 48.8, 46.4 and 47.7 ms in three runs. The old path measured 63.5, 60.3 and 68.2 ms in the same runs (Phase 2 measured 74.6). Through the core and the CLI: 49.0 ms on the first run, then 47.7 and 49.9 ms warm. Tests prove the sections are byte-identical to the old path's. CI is green: https://github.com/OliverD25/cabinetos/actions/runs/36424224855

### Built
- **`...\core\crates\cabinetos-fs\src\pipeline.rs`** (new, no unsafe):
  - The calling thread reads and parses the first 4 kernel buffers itself, the way the serial path does. If the kernel ends the directory sooner, it sorts there.
  - After 4 buffers, one worker thread takes over. The calling thread then only makes `NtQueryDirectoryFile` calls. It hands each filled buffer to the worker over a bounded crossbeam channel (16 deep).
  - The worker parses each buffer, builds its sort keys and sorts it into a run. The buffer then returns through a free-buffer channel for reuse.
  - After the kernel's last call, the worker merges all runs once with a binary heap. The result is the display order: one (entry index, name offset) per place. The entries never move.
- **`...\src\lib.rs`**:
  - New `ListOptions::pipelined`, default `true`. `false` gives the Phase 2 path, kept for the comparison bench.
  - `Listing` keeps its entries in enumeration order plus the display order. `entries()` builds a display-order copy on first use (a `OnceLock`), so the public API and the order are unchanged.
- **`...\src\section.rs`**:
  - From 16,384 entries, `ListingWriter::write` splits the entries by display position across up to 4 threads.
  - Each thread writes its own slice of the entry array, the metadata array and the name arena. Name offsets are computed first. So every byte has exactly one writer, and `split_at_mut` makes the borrow checker enforce it.
  - New public `write_with_threads`. The writer follows the listing's display order when it has one.
- **`...\src\enumerate.rs`**: `open`, and a reusable `Buffer` (`fill` makes one kernel call; `bytes` reads the result). `read_directory` now uses them and behaves the same.
- **`...\src\sort.rs`**: `compare` is now generic over two small traits: `Packed` and `Ties`. So the serial sort and the pipeline use one comparison function. `NameKeys::extend` appends keys batch by batch.
- **`...\tests\pipeline.rs`** (new), plus unit tests: the byte-identity proofs, listed below.
- **`...\benches\list_directory.rs`**: runs 1k, 10k and 100k. Benches: `list_directory_serial` and `_pipelined`, `list_and_publish_serial` and `_pipelined`, and `std_read_dir_metadata`. The buffer-size group is kept.
- **`...\core\crates\cabinetos-jobs\tests\engine.rs`**: the time bound in `a_move_on_one_volume_is_a_rename` is now 1,000 ms (was 50).
- **`...\core\README.md`**: the Benchmarks paragraph now names the 3 sizes, both paths, and the new test's folders.
- **`...\core\Cargo.toml`, `Cargo.lock`, `crates\cabinetos-fs\Cargo.toml`**: added `crossbeam-channel = "0.5"`.

**Byte-identical sections (DoD 3):**
- `tests/pipeline.rs::the_100k_fixture_gives_the_same_section_both_ways` lists the 100k fixture in all 8 orders (4 keys × 2 directions). It compares each path's whole section byte by byte.
- `random_folders_give_the_same_section_both_ways` uses seeded random folders of 3,000 and 7,000 entries under `%TEMP%\cabinetos-fs-test\`.
  - The folders hold directories, hidden files, names that differ only after the 16-byte key prefix, and Ukrainian and Japanese names.
  - Sizes and times come from a few values, so many entries tie.
  - It runs with and without hidden entries, and with 256 KiB and 4 KiB buffers. The 4 KiB buffers give about 35 entries per batch, so there are hundreds of runs to merge.
- `pipeline::tests::batches_and_the_merge_give_the_serial_order` sorts synthetic listings that no folder could hold: exact duplicate names, names that differ only in case, links, hidden entries.
  - It uses all 8 orders, cuts the listings into random batches (also one entry per run), and compares against the serial sort.
  - `name_offsets_follow_the_display_order` checks the name offsets.
- `section::tests::any_number_of_threads_writes_the_same_bytes`: 1 to 12 writer threads give the same bytes, also with fewer entries than threads. This test found a bug in my first split: with 10 entries on 7 threads, the chunks ran past the end. I fixed it with the balanced split `t*count/threads` before committing.

### Commits (all pushed to origin/main)
- `932135b` cabinetos-jobs: let the rename test pass on a loaded machine
- `126ca97` cabinetos-fs: pipeline the listing behind the kernel's reads
- `150f736` cabinetos-fs: bench the serial and pipelined listings side by side
- `d823078` cabinetos-fs: merge the listing's sorted runs once, at the end
- `7a327c0` cabinetos-fs: say exactly when the listing's worker starts
- `f741eba` core README: the listing bench and test, both ways

The Phase 5 agent's UI commits sit between and after mine. I rebased onto them with no conflicts. I never staged `.claude/worktrees/`.

### Checks (local, on f741eba)
- `cargo build --workspace`: exit 0.
- `cargo test --workspace`: 398 passed, 0 failed, 5 ignored by design. Before this task: 393. I added 6 tests and later removed 1, together with the merge code it tested.
- `cargo clippy --workspace --all-targets -- -D warnings`: exit 0.
- `cargo fmt --all -- --check`: exit 0.
- `cargo deny check`: advisories ok, bans ok, licenses ok, sources ok.
- CI run 36424224855 on dd60ed0, which contains all my commits: all 3 jobs succeeded. The Test step shows 398 passed, 0 failed, 5 ignored. My own run on f741eba (36422581859) was cancelled by the UI agent's push (see Noticed).
- On CI, `tests/pipeline.rs` takes about 110 s, because a fresh runner must first create the 100,000 fixture files. Locally it takes 7 s.

### Benchmarks (DoD 2)
Criterion medians, warm, this PC. Three whole-bench runs on the final code. "Before" is the Phase 2 path (`_serial`, section written on one thread), measured in the same runs.

| Files | List+sort, before | List+sort, after | List+sort+shared memory, before | List+sort+shared memory, after | `read_dir`+metadata (no sort) |
|---|---|---|---|---|---|
| 1,000 | 0.69 / 0.67 / 0.56 ms | 0.70 / 0.71 / 0.53 | 0.76 / 0.79 / 0.64 | 0.78 / 0.75 / 0.61 | 0.64 / 0.65 / 0.65 |
| 10,000 | 6.89 / 6.87 / 4.91 | 5.64 / 5.66 / 3.97 | 7.41 / 7.42 / 5.33 | 6.18 / 6.16 / 5.16 | 6.29 / 6.02 / 7.45 |
| 100,000 | 56.3 / 60.8 / 59.2 | 43.9 / 46.5 / 45.0 | 63.5 / 60.3 / 68.2 | **48.8 / 46.4 / 47.7** | 74.9 / 73.5 / 73.4 |

- **Phase 2's first measurement:** at 1,000 entries, 0.68 / 0.79 ms, with `read_dir` at 0.60. At 100,000, 71.7 / 74.6 ms, with `read_dir` at 61.0. There was no 10,000 row then.
- **Speed drift:** the machine's speed changes between benchmarks within a run. In run 3, the 1k and 10k rows ran 20–30% faster than in runs 1 and 2. So compare numbers within one run only.
- **Interleaved check:** this removes the drift. In one process, the two paths and `read_dir` ran in alternating blocks: list, sort, publish and tear down. Medians of 160, 160 and 40 runs:

| Files | Serial | Pipelined | `read_dir` |
|---|---|---|---|
| 1,000 | 0.66 ms | 0.66 ms | 0.58 ms |
| 10,000 | 5.03 ms | 4.45 ms (−12%) | 5.51 ms |
| 100,000 | 61.7 ms | 47.6 ms (−23%) | 72.6 ms |

- **Buffer size**, pipelined `list_directory` at 100k, three runs:

| Buffer | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| 64 KiB | 43.2 ms | 43.3 ms | 43.4 ms |
| 256 KiB | 43.9 ms | 44.1 ms | 44.8 ms |
| 1 MiB | 47.3 ms | 45.8 ms | 48.1 ms |

  See Known gaps.

**Phase breakdown, 100,000 entries.** One probe session: 30 runs with the two paths alternating, in a quiet period. The probe was not committed. Each number is a median, so the parts do not add up exactly. The session measured lower than the criterion runs because the machine was quieter then.

Pipelined:

| Step | Time |
|---|---|
| Open the folder | 0.1 ms |
| Kernel's first call | 4.5 ms |
| Next 3 calls, and parsing 4 buffers on the calling thread | 0.4 + 0.2 ms |
| Start the worker | 0.04 ms |
| The other 169 kernel calls | 26.3 ms (156 µs each); whole feeding loop 27.1 ms |
| After the kernel's last call | 3.3 ms (worker's backlog, then the heap merge at 2.1 ms) |
| **= Listed** | **36.8 ms** |
| Section: create and map | 0.2 ms |
| Section: write on 4 threads | 1.4 ms |
| **= In shared memory** | **38.6 ms** |
| Teardown | 1.1 ms |

During those 169 calls, the worker parses (4.3 ms), builds sort keys with `LCMapStringEx` (13.0 ms) and sorts each buffer (6.6 ms). It is busy 23.9 of the 27.1 ms, which is 88%.

Serial, same session:

| Step | Time |
|---|---|
| Open, kernel calls and parse | 35.8 ms |
| Keys, sort and reorder | 14.1 ms |
| Section: create and map | 0.2 ms |
| Section: write on 1 thread | 3.8 ms |
| **= In shared memory** | **53.7 ms** |

**Where the time goes:**
- The kernel's calls take about 31 of the 38.6 ms, which is 80%.
- The rest of the critical path is:
  - the 3.3 ms after the last call;
  - the 1.4 ms section write;
  - about 0.5 ms of small steps.
- The kernel's share changes with the machine's load. Across sessions, the first call took 4.5–10 ms, and the other 169 calls took 26–35 ms in total. That is the gap between this session and the criterion runs.

### Live check (DoD 2)
- **Setup:** release binaries. The core ran on the test pipe `pipelined`. Its log, config and plugin folders were in my scratchpad (`--log-dir`, `--config`, `--plugins-dir`, `--plugins-data-dir`).
- **Shutdown:** clean, through `cabinetos-cli shutdown`; the core exited with code 0.

```
pong id=01M3KZGQ1SK9P8GHPMK7K7RN8T protocol=7 core=0.1.0 rtt=0.55ms
== ls C:\Users\Admin\AppData\Local\Temp\cabinetos-bench\100000 (first run of this core)
core: enumerated 100000 entries in 49.0 ms
client: mapped and read in 20.5 ms
== warm run 1
core: enumerated 100000 entries in 47.7 ms
client: mapped and read in 20.2 ms
== warm run 2
core: enumerated 100000 entries in 49.9 ms
client: mapped and read in 20.0 ms
== the listing starts: - Backup-6.jpg / - Backup-18.pdf / - Backup-30.png (natural order)
== ls C:\Windows\System32 (4,923 entries, past the switch to the worker), twice
core: enumerated 4923 entries in 3.4 ms
core: enumerated 4923 entries in 3.2 ms
```

- **Phase 2's live check** measured 80.7 / 80.6 / 76.7 ms, and 3.8 ms for System32.
- **"core: enumerated"** covers listing, sorting, creating the section and writing it.

### Decided
- **One worker thread, not a pool of 2–4.** Because a first version with 2 and 3 workers measured slower. The 100k list medians were 60.2 ms with one worker, 67.0 with two and 63.8 with three. Also, one worker taking the buffers in order makes each entry's array index equal to its enumeration position, so nothing has to be gathered at the end. Undo: per-worker arrays plus a gather step in `pipeline.rs` (`pipelined`, `work_on`).
- **The final order comes from one binary-heap merge of all runs, after the kernel's last call.** This is the design's k-way merge. Because of measurements in 8 interleaved sessions on the 100k fixture:
  - My first version (126ca97) merged runs as they arrived. It was 1.1–4.3 ms slower in total. The merges kept the worker so busy that it fell behind the kernel: the feeding loop took 35–40 ms, against 33–35 ms without them.
  - `sort_unstable` on the joined runs took 9.3 ms for the final step, against the heap's 1.5–2.4 ms. In total that was 43.2–43.7 ms, against 35.5–35.6 ms in the same quiet sessions.
  - A loser tree took 3.2 ms against the heap's 2.1. The heap needs only about 2 comparisons per entry, because NTFS returns names close to the display order.
  - Four parallel heap parts saved nothing in total.
  - Undo: revert d823078.
- **The worker starts after 4 kernel buffers, about 1,750 entries.** A directory that the kernel ends sooner is sorted on the calling thread, the old way. Because the worker costs about 0.2 ms: it measured +0.2 ms at 2,000 and 3,000 entries, and −20% at 5,000 and 10,000. Starting after 8 buffers (about 4,000 entries, near the suggested 4,096) measured +8% at 5,000 and −10% at 10,000. Undo: `SERIAL_BUFFERS` in `pipeline.rs`.
- **Buffers are reused through a free-buffer channel.** A new buffer is made only when none is free; a 100k listing makes 12. Because a fresh buffer per call made the feeding loop take 55.5 and 58.3 ms, against 42.7 and 46.0 ms with reuse (two sessions). Undo: not needed.
- **The section write uses up to 4 threads, from 16,384 entries.** Because 100k took 3.8 ms on 1 thread, 2.3 on 2, 1.35 on 4 and 1.42 on 8. Undo: `PARALLEL_WRITE_FROM` and `MAX_WRITE_THREADS` in `section.rs`.
- **`Listing` keeps enumeration order plus a display order, and `entries()` copies lazily.** Because moving 100,000 entries into order would sit on the critical path, and the section writer only needs the order. Undo: reorder `entries` in `work_on` and drop `order`.
- **The old path stays, behind `ListOptions::pipelined = false`.** The task says to remove it only if the new path wins at 1k, 10k and 100k. At 1k it only ties: below 4 buffers, the new path IS the old code, and the medians differ by ±0.04 ms either way. Undo: remove the flag and `enumerate::read_directory`.
- **Added `crossbeam-channel` 0.5.** It carries the bounded work queue and the free-buffer pool. MIT/Apache-2.0, from the same crossbeam project as the `crossbeam-queue` already in use; `cargo deny` is clean. Undo: `std::sync::mpsc::sync_channel`.
- **The rename test's bound is now 1,000 ms.** The unchanged file IDs are the real proof of a rename; the time is only a sanity bound.
  - Correction to my commit message 932135b: it says a copy "would still show up as far slower". But with 1,000 small files, a copy could take a few hundred ms and still pass 1 s. So the time bound does not reliably catch a copy.
  - Undo: edit the bound in `engine.rs`.
- **Test data.** I deleted my own extra fixtures `%TEMP%\cabinetos-bench\2000`, `3000` and `5000`, made for the switch-point measurement. The bench's `1000`, `10000` and `100000` stay. My probes lived only in my scratchpad and were never committed.

### Needs the user
- **Ready text for PLAN.md, Phase 2 "Measured".** It replaces the paragraph that starts "Measured 2026-09-28 on this PC, warm cache: 100,000 entries take 72 ms":

> Measured 2026-09-28 on this PC, warm cache, after the pipelined follow-up: 100,000 entries reach shared memory in 46–49 ms (criterion medians of `list_and_publish` in three runs; 48–50 ms end to end through the CLI). **The follow-up's 55 ms goal is met, and the first-cut 50 ms target too in these runs, with little margin.** While the kernel fills the next buffer (about 64 KiB of names per call), one worker thread parses the previous one, builds its sort keys and sorts it; after the kernel's last call, one heap merge of the sorted runs (about 2 ms) gives the display order, and the section is written on four threads (1.4 ms instead of 3.8). The kernel's calls are the floor, about 80% of the time, and they vary with the machine's load (the first call 4.5–10 ms, the other 169 calls 26–35 ms). The Phase 2 path stays behind `ListOptions::pipelined = false` for comparison: 60–68 ms in the same runs (75 ms when first measured). At 10,000 entries the pipelined path takes 5.2–6.2 ms against 5.3–7.4; below about 1,750 entries both paths run the same code (1,000 entries: 0.6–0.8 ms). Both write byte-identical sections ([tests/pipeline.rs](../core/crates/cabinetos-fs/tests/pipeline.rs)). The baseline `std::fs::read_dir` plus metadata, without sorting, takes 73–75 ms at 100,000 entries in these runs (61 ms in the first measurement). Showing the first screen within a few milliseconds would still need chunked publishing, which stays open design work.

- **README status line:** the core now has 398 tests; the line says 393.

### Known gaps
- **The result depends on the machine's load**, because the kernel's part does. All three criterion runs and the live check stayed under 50 ms. But in one heavy-load session, an earlier version of the pipeline measured 60 ms (the serial path measured 79 ms then).
- **1,750 to 4,000 entries:** here the pipelined path is up to 0.2 ms slower than the serial one (measured at 2,000 and 3,000).
- **The worker is 88% busy during the read at 100k.**
  - Sort keys (`LCMapStringEx`) take 13 of its 24 ms, on one thread. The serial path builds keys on 8 threads (3 ms).
  - On a loaded machine the worker falls behind, and the time after the last call grows (3.3 ms when quiet).
  - A second thread for keys would be the next step, if that matters.
- **Buffer size:** at 100k, 1 MiB buffers are 1.6–3.5 ms slower than 256 KiB, and 64 KiB is 0.7–1.4 ms faster. The reason: the pipeline allocates about 12 zeroed buffers per 100k listing. Reusing buffers across listings, or a smaller default for local disks, would remove this cost. I kept 256 KiB for network shares, which may fill more per call; I did not measure that.

### Noticed out of scope
- **CI cancellations.** ccf1cdc (Phase 5) added `ui/**` to the CI triggers, and the workflow has one concurrency group for `main` with `cancel-in-progress: true`. So every UI push now cancels a running core check. This happened to my run 36422581859. With two agents pushing to `main`, a core run may never finish. One group per job, or separate workflows, would avoid this.
- **Other callers now use the pipeline.** The folder-walk search (`cabinetos-core/src/search.rs`) and the job planner (`cabinetos-jobs/src/plan.rs`) build `ListOptions` with `..ListOptions::default()`. So they now use the pipelined path too. The results are the same, and large folders get faster.
- **CLI read time.** The CLI's "client: mapped and read" takes 20 ms for 100k, because it builds a String per name. Only the CLI does this.
- **`docs/ipc.md` example.** It shows an example reply with `"elapsed_us":67310` for 100,000 entries. It is only an example, so I left it.
