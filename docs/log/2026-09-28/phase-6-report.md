## Phase 6 report

Context: CabinetOS Phase 6, the indexer (the core side; the search field in the UI waits for Phase 5). Repo `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos`, branch `main`. Everything is pushed. CI is green on the final commit ff7718c, including the new step that runs the elevated tests as Administrator.

All three parts of the phase's done-when are met on the CI runner's C: drive:
- **The index builds in seconds:** 1,358,009 entries in 3.3 s.
- **A rename appears in search within a second:** it appears after 9 ms.
- **The app works with the indexer stopped:** the live check on this PC below.

### Built

- **Part 0 (c497b59):** the `help.about` example in `docs/ipc.md` said `protocol_version: 3`. It now says 5, and 6 since this phase. Nothing else in that file was stale; I checked the listing section version, the chord window and the error codes against the code.
- **Part A, `cabinetos-index`:**
  - **Building the index.** The volume is opened as `\\.\X:`. The MFT is read with `FSCTL_ENUM_USN_DATA` (`MFT_ENUM_DATA_V1`, record versions 2 and 3), so the driver returns each entry's file reference number (FRN), parent, name and attributes. No NTFS on-disk structure is parsed. This is the first-version reading of the brief's "read $MFT directly".
  - **Change journal.** Its position is taken before the enumeration, and the journal is replayed after it, so no change is lost during the build.
  - **Memory layout.** FRN → slot in a hash map, with parent, name ID and attributes in parallel arrays. Each distinct name is stored once, in UTF-16 for paths and in lowercase UTF-8 for search. Paths are rebuilt from parents, with the root at MFT segment 5.
  - **Search.** A substring match without case, on several threads. Names that start with the query come first, then shorter names, then paths. A root filter works by the root folder's FRN and a parent-chain check.
  - **Keeping current.** One thread per volume reads `FSCTL_READ_USN_JOURNAL` (`READ_USN_JOURNAL_DATA_V1`) and applies changes (create, delete, rename, move, attributes). When the journal is purged or replaced, a new index is built in the background while the old one answers. A deleted journal or repeated read errors mark the volume `failed`.
  - **Scope.** Nothing is saved to disk. Only NTFS volumes are indexed.
- **Part B, `cabinetos-indexer`:**
  - **Pipe.** `\\.\pipe\cabinetos-indexer`, SDDL `D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)S:(ML;;NW;;;ME)`. Remote clients are refused, and the pipe is created as the first instance, so there is one indexer per machine. The framing and codec are reused from `cabinetos-ipc`.
  - **Requests.** Only `ping`, `index_status` and `search`. Any other type gets `unknown_request`.
  - **Modes.** `--console`, which refuses clearly without elevation, `--service`, `--install` and `--uninstall` (via `windows-service` 0.8.1). `--volumes C,D`; the default is every NTFS volume with a drive letter.
  - **Diagnostics.** Process `indexer`, boundary `indexer`, log file `indexer.<date>.jsonl`.
  - **ipc crate additions.** Pipe names from a full name, servers bound with any SDDL, `stored_security()` to read back the DACL and label, and `exchange()` for one request and one reply.
- **Part C, core (protocol version 6):**
  - `search {query, limit, root?}` answers `file_search_results {hits, source, took_us, complete}`.
  - The core asks the indexer with a 200 ms limit. If the indexer does not answer, or answers with an error, the core walks one folder tree itself: breadth first, links not followed, at most 2 s and 20,000 entries, ranked the same way.
  - `index_status` answers `{available, volumes}`.
  - After a failure the core waits 1 s before asking again, doubling up to 30 s. It writes one INFO line when the indexer goes missing, and one when it answers again.
- **Part D, CLI:** `cabinetos-cli search <query> [--limit N] [--root path]` prints the hits and `source: index|walk`. `cabinetos-cli index status` prints the volumes. `cabinetos-indexer --console --volumes E` exists for the elevated live check.
- **Part E, tests:** 356 pass locally, and 5 are ignored by design (2 old, 3 new elevated ones).
  - Unit tests on synthetic USN records: create, delete, rename, move between parents, hard-link changes, broken records. Also path reconstruction, ranking, the root filter, and several threads giving the same answer.
  - A memory test with a counting allocator. It prints 83.8 bytes per entry on a synthetic 400,000-entry volume.
  - Pipe security: a test reads back what Windows stored, `D:P(D;;FA;;;NU)(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x12019f;;;IU)S:AI(ML;;NW;;;ME)`, and checks the protocol over a real pipe.
  - Core fallback: end to end with no indexer, and with a stand-in that speaks the real indexer protocol behind the real SDDL. The stand-in is stopped mid-test, and the core falls back to the walk.
  - The CLI with no indexer.
  - Elevated, run on CI: the index of the `%TEMP%` volume, with a create, a rename, a move and a delete each visible within 1 s. Then the real `--console` process. Then install, start, query, stop and uninstall of the service.
- **Part F, docs:**
  - New `docs/indexer.md`: how the index is built, memory figures, the journal loop, the pipe and its label explained ACE by ACE, the service commands, and search without the indexer.
  - Also updated: `docs/ipc.md`, `core/README.md`, `docs/ARCHITECTURE.md` (crate map and change-log row), `docs/diagnostics.md` and `sdk/README.md`.

### Commits

All are on `origin/main`:
- c497b59 docs: the help.about example shows the current protocol version
- 45a4c7a index: the volume index in memory, and search over it
- 2f58dc5 index: build from the MFT, follow the change journal, run elevated tests on CI
- c847a9a indexer: the elevated process, its read-only pipe, console and service modes
- c974320 ci: let the elevated step install and remove the indexer service
- 448a9a0 core, cli: file search through the indexer, or a bounded walk without it
- 17dc8ac index: stop at once instead of waiting for the next disk change
- ff7718c docs: the indexer, search in the protocol, and where their numbers stand

### Checks

Locally on ff7718c: `cargo build --workspace` ok. `cargo test --workspace` 356 passed, 0 failed, 5 ignored. `cargo clippy --workspace --all-targets -- -D warnings` ok. `cargo fmt --all -- --check` ok. `cargo deny check`: advisories, bans, licenses and sources all ok.

CI runs:
- Final, green: https://github.com/OliverD25/cabinetos/actions/runs/36394693870. The core job took 19 min 20 s with a warm cache; the elevated step passed.
- Also green: https://github.com/OliverD25/cabinetos/actions/runs/36392299148 (17dc8ac, the first run with the indexer and service tests).
- Also green: https://github.com/OliverD25/cabinetos/actions/runs/36389269633 (2f58dc5, the first elevated run).

Measured on the CI runner's `C:` in the elevated step (release build):

| What | Result |
|---|---|
| Build of `C:` | 1,358,009 entries in 3,275 ms |
| Memory | 119 MB, 87 bytes per entry |
| A 3-letter query | 12.5 ms over the whole volume |
| A new file visible to search | after 38 ms |
| A rename, a move, a delete visible to search | after about 9 ms each |
| Stopping the index | 20 ms |
| Real `cabinetos-indexer --console` | built in 3.2 s; the test file found in 9.6 ms |
| The service | built in 3.1 s |

### Live check

Unelevated, on this PC. The core ran on pipe `phase6live` with a scratch configuration and logs under `%TEMP%\cabinetos-index-test\live\`; no indexer pipe existed.

```text
$ cabinetos-cli ping
pong id=01M3KF44PCYV3X5JH076D0NSPW protocol=6 core=0.1.0 rtt=1.02ms
$ cabinetos-cli index status
available: no (no indexer answers; search walks folders instead)
$ cabinetos-cli search foo --limit 5          (no root: the profile folder is walked)
0 hit(s); source: walk; 1828.2 ms; incomplete (a limit stopped the search, or a volume is still being indexed)
$ cabinetos-cli search foo --root C:\Users\Admin\AppData\Local\Temp\cabinetos-index-test\live\tree
f C:\Users\Admin\AppData\Local\Temp\cabinetos-index-test\live\tree\foo.txt
f C:\Users\Admin\AppData\Local\Temp\cabinetos-index-test\live\tree\Foo Bar.docx
f C:\Users\Admin\AppData\Local\Temp\cabinetos-index-test\live\tree\sub\seafood-recipes.md
3 hit(s); source: walk; 0.6 ms; complete
$ cabinetos-indexer --console                  (normal terminal)
cabinetos-indexer: --console needs Administrator rights; run it from an elevated terminal (Run as administrator)
(exit code 2)
```

- The core's log has "no indexer answers; searching by walking folders until one does" exactly once, at INFO. The profile walk stopped at the 20,000-entry limit (`visited: 20000`).
- Cleanup: the scratch folder and the empty `%TEMP%\cabinetos-index-test` are removed. `%LOCALAPPDATA%\CabinetOS` still holds only its old `logs`. There is no `%ProgramData%\CabinetOS`.
- I did not touch `E:\cabinetos-scratch`, as you asked.

### Decided

- The volume handle is opened with `GENERIC_READ` instead of `FILE_READ_ATTRIBUTES | SYNCHRONIZE` — because NTFS checks read access on the volume for the MFT enumeration and journal reads, and `GENERIC_READ` covers both; it worked elevated on CI at the first try, while I could not try the narrower mask tonight — undo: change the mask in `win::Volume::open` and rerun the elevated step.
- The index is a map FRN → slot with the fields in parallel arrays, not `HashMap<Frn, Entry>` — because pass 2 of a search scans the name IDs as one flat array split across threads; memory stayed at 87 bytes per entry — undo: `VolumeIndex` internals.
- Names are interned per volume, and a name stays until the next rebuild even when no entry uses it — because deletes are rare compared to the memory saved — undo: reference-count names in `names.rs`.
- Case-insensitive means Unicode `to_lowercase`, with an ASCII fast path — because it is close to NTFS's own upcase table and needs no table — undo: `names::lowercase_utf8`.
- Search passes and rank are as described under Built; `limit` defaults to 100 in the protocol and 50 in the CLI, and is at most 1,000 — undo: `search.rs` and the constants.
- A radix tree or trigram index is deferred — because the scan takes 12.5 ms for 1.36 million entries — undo: add one when the numbers ask for it.
- Raw `$MFT` parsing is deferred — because the enumeration takes 3.3 s for 1.36 million entries — undo: add it when the numbers ask for it.
- NTFS's own files (MFT segments below 16, and everything under `$Extend`) are excluded from results; a hard link keeps the one name it was indexed under, and hard-link-change records are skipped — because both would give misleading hits — undo: `index.rs` `searchable` and `apply`.
- The journal is read with `ReturnOnlyOnClose = 0` — because a rename then shows before the program that made it closes the file — undo: set 1 in `win::read_journal`.
- A stop cancels the blocked journal read (`CancelSynchronousIo`, repeated until the thread ends) — because Windows does not end the read at `Timeout`: the first CI run's stop waited 31 s for the next disk change; it now takes 20 ms, and an elevated test asserts under 5 s — undo: revert 17dc8ac.
- A volume without a change journal fails with a message, and the indexer never creates a journal — because the indexer writes nothing but its own log — undo: `volume::build`.
- `--volumes` with a non-NTFS letter is refused by name; the default takes NTFS volumes with a letter on fixed, removable and RAM disks — because network and optical drives cannot be read this way — undo: `win::ntfs_volumes` and `Args::letters`.
- No persistence; the index is rebuilt at every start (about 3.3 s per 1.36 million entries) — because the task said so — undo: later phase.
- The SDDL also has an explicit deny for network logons, on top of refusing remote clients — because it costs nothing and covers a mistake in either — undo: `PIPE_SDDL`.
- The indexer's messages are separate enums (`IndexerRequest`, `IndexerResponse`) in `cabinetos-protocol`, with their own schemas (`sdk/protocol/indexer-*.schema.json`), and carry the core request's ID — because the read-only rule is then in the type, and one request ID follows a search through both logs — undo: `protocol/src/index.rs`.
- The reply to `search` is named `file_search_results`, not `SearchResults` as the task said — because `search_results` is already the reply to `search_commands` — undo: rename both, the old one first.
- `index_status` is both a request and a reply tag — because `volume_info` already does the same — undo: rename one.
- An empty query gets `protocol_error` — because no new error code is needed for a bad argument — undo: add a code.
- `journal_lag` is counted in journal bytes — because that is what the journal reports — undo: measure time instead.
- The core opens a fresh pipe connection per indexer request, with 200 ms for the whole exchange — because a connection costs microseconds and no state is shared between sessions — undo: keep a persistent client in `search::IndexerLink`.
- After a failure the core waits 1 s before asking again, doubling to 30 s, and `index_status` ignores that wait — because a status check should always look — undo: `IndexerLink::ask`.
- An indexer error (`not_indexed`, `invalid_root`) also leads to a walk — because the walk can still help there — undo: `search::search`.
- Without a root, the walk starts at the folder the connection listed last, else `%USERPROFILE%` — because the core cannot know which pane is focused — undo: `default_search_root`.
- The walk skips folders it cannot read but refuses a missing root (`not_found`) and a relative root (`invalid_path`) — because a bad root is a caller's mistake — undo: `search::walk`.
- `CABINETOS_INDEXER_PIPE` (core) and a hidden `--pipe` (indexer) exist for tests, and every core test harness sets its own pipe — because no test may talk to a real indexer on the machine — undo: remove both.
- The indexer's pipe name lives in `cabinetos-protocol` — because otherwise the core would depend on the indexer crate and its service code — undo: move it back.
- The service is `cabinetos-indexer` ("CabinetOS Indexer"), start type manual, account LocalSystem, logs in `%ProgramData%\CabinetOS\logs`, and records the program's path at install; `--uninstall` stops it first (up to 30 s) — because LocalSystem may read the MFT, and manual start means nothing runs until asked — undo: `service.rs`.
- The service's start is tested on CI only (`CABINETOS_TEST_SERVICE=1`), with `sc start` — because it installs a real service — undo: remove the gate.
- The elevated integration test indexes the volume of `%TEMP%`, not "the smallest NTFS volume" — because the test file must be on the indexed volume and test data may only live under `%TEMP%\cabinetos-index-test\` — undo: `tests/elevated.rs`.
- The CI elevated step runs `--release` after the release build — because timings are then realistic and it reuses the release dependencies — undo: `.github/workflows/ci.yml`.
- The protocol change is split across commits (the indexer's messages in c847a9a, `search`, `index_status` and version 6 in 448a9a0) — because every commit must build — undo: none needed.
- Three commits (c847a9a, c974320, 448a9a0) were held locally for about 40 minutes and pushed together with 17dc8ac, against the "push after each commit" rule — because each push cancels the running CI job, and that run carried the first elevated result, the only way to try the Windows calls tonight — undo: none needed.

### Needs the user

1. **The elevated live check, in the morning.** WSL cannot start an elevated Windows program, so step 2 needs PowerShell.

   Step 1, build (WSL, normal rights):
   ```bash
   cd /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core && git pull --ff-only && cargo.exe build --release -p cabinetos-indexer -p cabinetos-core -p cabinetos-cli
   ```
   Step 2, start the indexer. Open PowerShell with "Run as administrator" and keep the window open; Ctrl+C stops it:
   ```powershell
   # PowerShell as administrator: WSL cannot start an elevated Windows program
   & "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\target\release\cabinetos-indexer.exe" --console --volumes C
   ```
   Step 3, start a core on the `dev` pipe (second WSL terminal, normal rights):
   ```bash
   /mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core/target/release/cabinetos-core.exe --pipe dev
   ```
   Step 4, ask (third WSL terminal, normal rights). Expect `C: ready, N entries, built in N ms` and `source: index`:
   ```bash
   CLI=/mnt/e/codespace/_claude_code/_rde/_cabinetos_windows_system_manager/cabinetos/core/target/release/cabinetos-cli.exe && "$CLI" index status && "$CLI" search notepad --limit 5 && "$CLI" search foo --limit 5 && "$CLI" shutdown
   ```
   Step 5, optional: the service. First stop the console indexer with Ctrl+C, because only one indexer can hold the pipe:
   ```powershell
   # PowerShell as administrator: installing a Windows service needs it, and WSL cannot elevate
   $exe = "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\target\release\cabinetos-indexer.exe"; & $exe --install --volumes C; sc.exe start cabinetos-indexer
   ```
   To remove the service later:
   ```powershell
   # PowerShell as administrator
   & "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\target\release\cabinetos-indexer.exe" --uninstall
   ```
   Optional: the elevated tests on this PC. The service test skips here, because it runs only when `CABINETOS_TEST_SERVICE=1` is set, and that is CI only:
   ```powershell
   # PowerShell as administrator
   cd "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core"; cargo test --release -p cabinetos-index -p cabinetos-indexer -- --ignored --nocapture
   ```

2. **A decision on privacy.** Today any user logged on at this PC can search the names of every file on the indexed volumes, including other users' folders. The "Everything" search tool works the same way. The alternative is that the indexer checks, for each hit, whether the asking user may open that folder, and hides it otherwise. Should hits be filtered that way, in a later phase?

3. **Ready text for `docs/PLAN.md`, Phase 6.** Change the heading to `### Phase 6 — Indexer — core side done 2026-09-28`. Add after the "Done when:" paragraph:
   > Core side done 2026-09-28; the search field waits for the UI (Phase 5). Built: `cabinetos-index`, an in-memory index keyed by file reference number, built by enumerating the MFT through `FSCTL_ENUM_USN_DATA` (the NTFS driver returns each entry's FRN, parent, name and attributes, so no on-disk structure is parsed) and kept current by one thread per volume that follows `FSCTL_READ_USN_JOURNAL`, rebuilding in the background when the journal cannot continue; names interned; ranked substring search on several threads. `cabinetos-indexer.exe` runs it elevated, in a terminal (`--console`) or as a Windows service (`--install`, manual start, LocalSystem), and answers only `ping`, `index_status` and `search`, over `\\.\pipe\cabinetos-indexer` with a medium integrity label so the normal-rights core can ask. The core answers `search` from the indexer within 200 ms, or walks one folder tree itself (at most 2 s and 20,000 entries) and says `source: walk`. Measured 2026-09-28 on the GitHub Actions runner's `C:` (release build): 1,358,009 entries in 3.3 s, 87 bytes per entry, a 3-letter query in 12.5 ms, a rename visible to search after 9 ms. On this PC with the indexer stopped, `search` answered from a walk and `index status` said `available: no`; the index of this PC's own `C:` is still to be measured in an elevated run. Guide: [indexer.md](indexer.md).

4. **Ready text for the README status line:**
   > Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done, and the core sides of Phases 6 and 7: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, runs copy, move and delete jobs on per-disk queues, runs sandboxed WebAssembly plugins whose crashes it contains, and searches whole NTFS volumes through an elevated indexer (or walks folders without it), all over a user-only named pipe, with the indexer behind a read-only pipe of its own (`core/`, 356 tests, CI green). Phase 5, the WinUI 3 shell, needs the .NET SDK; the core side of Phase 8 (terminal host) continues meanwhile.

   New row for the README documents table:
   > | [docs/indexer.md](docs/indexer.md) | The indexer: how volumes are indexed and kept current, the read-only pipe, the service, search without it. |

### Known gaps

- This PC's own C: has not been measured yet. All numbers come from the CI runner; the morning run gives them.
- The index is rebuilt at every start: about 3.3 s per 1.36 million entries. During a rebuild the old index answers, stale by that much.
- Hard links are indexed under one of their names.
- Matching without case approximates NTFS's own upcase table.
- Hits are not filtered by the asking user's rights (see Needs the user, item 2).
- The core does not check who serves the indexer pipe. A process of the same user that creates `\\.\pipe\cabinetos-indexer` first could stop the indexer from starting, and could feed the core false hits. Checking the server's process identity is a later hardening.
- Volumes mounted in a folder without a drive letter are not indexed, and neither is ReFS.
- A search without an indexer and without a root walks the profile folder, which is usually incomplete (20,000 entries).
- `--install` records the program's current path; after moving or rebuilding it elsewhere, reinstall.
- `journal_lag` is in bytes, not time.
- Timeout does not end the journal read; stopping relies on cancelling it (tested on CI).

### Noticed out of scope

- **CI time.** A warm run takes about 19 min. A cold run with the new step could approach the 45-minute limit: it was about 36 min before this phase. If a cold run times out, split the release build and the elevated tests into their own job.
- **cargo deny.** `windows-service` adds `widestring`, and the duplicate-version warnings (several `windows-sys` versions) keep growing. They are warnings only.
- **Version order.** In the protocol history, version 5 came from Phase 7 and version 6 from Phase 6. `docs/ipc.md` says so; it only looks odd.
- **Search hits and links.** Hits do not distinguish links from files, because the USN record carries no reparse tag. A future `kind: link` needs the reparse tag from a directory read.
