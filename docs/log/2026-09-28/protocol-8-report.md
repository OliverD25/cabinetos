## Report: core additions for the UI (protocol version 8), plus the CI fix

Context: `core/` (the Rust core) and `.github/workflows/ci.yml`.

**Result: done.** All five parts are built and pushed, and the checks pass. CI run https://github.com/OliverD25/cabinetos/actions/runs/36433919598 (on 68fd710, which holds all my code) finished green and was not cancelled: all 4 jobs succeeded. The UI agent's next pushes show the CI fix working: in their runs the core and deny jobs were **skipped**.

### Built
- **Part 0, CI** (`.github/workflows/ci.yml`).
  - The workflow no longer has one concurrency group for the whole run. `core`, `ui` and `deny` each have their own group (`core-`, `ui-`, `deny-${{ github.ref }}`).
  - A new small `changes` job (on Linux) runs `git diff` against the previous push, or against the pull request's base. It sets two outputs, `core` and `ui`:
    - `core` is true for changes under `core/`, `sdk/protocol|config|wit|fixtures/`, or to `ci.yml`.
    - `ui` is true for changes under `ui/`, `sdk/protocol|config/`, or to `ci.yml`.
    - With nothing to compare against (a new branch, a force push), both are true.
  - `core` and `deny` run only when `core` is true; `ui` runs only when `ui` is true.
  - The ui job's steps are unchanged; it only gained `needs`, `if` and its group.
- **Part A, `list_volumes`** (reply `volumes { volumes: [VolumeDetails] }`), in `core/crates/cabinetos-core/src/volumes.rs`.
  - `cabinetos_fs::volume::drives()` lists the letters with `GetLogicalDrives` and `GetDriveTypeW`.
  - Each letter is asked on the blocking pool, all in parallel. The limit is 200 ms for a network drive and 2 s for a local one.
  - A drive that is not ready, fails, or runs out of time is left out. The rest are sorted by letter.
  - A query that ran out of time cannot be stopped. Its letter is skipped, without a new query, until that query ends. The timeout path closes the reply channel before its last check, so a query that ends at that same moment is not missed.
  - `drive_letter` is always the letter that was asked.
- **Part A, `volumes_changed { volumes }`** (fitted within the hour).
  - `cabinetos_fs::DriveWatcher` (`core/crates/cabinetos-fs/src/drives.rs`) owns a top-level window that is never shown, on its own thread with a message loop. It listens for `WM_DEVICECHANGE` with `DBT_DEVICEARRIVAL` or `DBT_DEVICEREMOVECOMPLETE` for volumes.
  - The core waits 500 ms for the rest of a burst, runs `list_volumes`, and sends the event only when the drives differ from the ones sent last. Free space is not compared.
- **Part B, `get_value { path }`, `set_value { path, value }` and the reply `value`.**
  - `set_value` goes through the existing `ConfigStore::set_value` and update: it reads the file on disk, validates, writes atomically, and sends `config_changed` to every hello'd client. The core's own write is not echoed (the hash rule).
  - Every refusal is `config_error` with the reason, and the file is not touched.
  - New settings `ui.lastPaths: []` and `ui.pinned: []`.
  - CLI: `config get <path>` and `config set <path> <json>`.
- **Part C, `open_path { path }`** (`core/crates/cabinetos-fs/src/open.rs`).
  - It calls `ShellExecuteExW` with the `open` verb and `SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI`, in an STA COM apartment that is left again afterwards, on the blocking pool.
  - Errors: `not_found`, `access_denied`, or `io` with the shell's error code (`os error N`).
  - `docs/ipc.md` records that this is core infrastructure, the last step of navigation, and that Article 10 still holds.
  - CLI: `open`.
- **Part D, `create_directory { path }` and `rename { path, new_name }`** (`core/crates/cabinetos-fs/src/ops.rs`).
  - `create_directory` uses `CreateDirectoryW`. `rename` uses `MoveFileExW` without `MOVEFILE_REPLACE_EXISTING`, so it never replaces anything.
  - Both return the new error code `already_exists` (`FsError::AlreadyExists`) for a taken name.
  - `new_name` is `invalid_path` when it is empty or contains a separator.
  - CLI: `mkdir` and `rename`.
- **Part E.**
  - Unit tests are in the fs, config, protocol and CLI crates. The end-to-end tests are in `core/crates/cabinetos-core/tests/shell_requests.rs`. Their temp data goes under `%TEMP%\cabinetos-core-test\`.
  - The schemas in `sdk/protocol/*` and `sdk/config/cabinetos.schema.json` are regenerated.
  - `docs/ipc.md` covers version 8, the list and event text in the Volumes section, a new "Files and folders" section, and the value messages. `docs/config.md` covers the new keys and `set_value`. `core/README.md` is updated too.

### Commits (all pushed to origin/main)
- `a1ccf5d` ci: one concurrency group per job, and run only the jobs a push needs
- `b209f73` config: ui.lastPaths and ui.pinned, for the shell to remember
- `0a14bee` cabinetos-fs: new folder, rename, open with the default app, drive letters
- `e847634` core: list_volumes, protocol version 8
- `c53244e` core: get_value and set_value, one setting at a time
- `e9f723b` core: open_path, a file or folder with its default application
- `175bffe` core: create_directory and rename, without a job
- `d4d49dd` cabinetos-fs: a subst letter is on the volume of its folder
- `a1606e8` core: volumes_changed, when drive letters come or go
- `68fd710` core README: the protocol 8 commands of the CLI
- `794be05` docs: reflow two protocol 8 paragraphs in ipc.md

I rebased before every push. The UI agent pushed in between, and there were no conflicts.

### Checks
- Local, on 68fd710:
  - `cargo build --workspace`: exit 0.
  - `cargo test --workspace`: **421 passed**, 0 failed, 5 ignored. It was 398 before this task.
  - `cargo clippy --workspace --all-targets -- -D warnings`: exit 0.
  - `cargo fmt --all -- --check`: exit 0.
  - `cargo deny check`: advisories, bans, licenses and sources all ok.
- UI tests run locally after each protocol change: 158 pass. After the UI agent's 10dd189: 203 pass.
- CI run 36433919598 (all 4 jobs succeeded, not cancelled):
  - core Test step: 421 passed, 0 failed, 5 ignored;
  - ui: 202 passed, 1 skipped (the end-to-end test, which needs a core build);
  - deny: ok.
- The UI agent's UI-only runs, 36437084194 and 36437239459: `changes` succeeded, `core` and `deny` **skipped**, `ui` ran.

### Live check
- **Setup:** release build. The core ran on the test pipe `v8live`, with its config, logs and plugin folders in my scratchpad.

```
pong id=01M3M5Q8QGG7FK8E8CWA01TNWA protocol=8 core=0.1.0 rtt=0.57ms
== cabinetos-cli volumes
drive  filesystem        size        free  bus     media  disk  label
C:     NTFS          1.82 TiB    1.19 TiB  NVMe    SSD    1     System Disk
D:     NTFS          1.82 TiB    1.27 TiB  NVMe    SSD    2     SSD 1 Programs&Media
E:     NTFS          1.82 TiB    1.63 TiB  NVMe    SSD    4     SSD 2 Dev
G:     NTFS          3.64 TiB    1.07 TiB  NVMe    SSD    3     SSD 3 Media 4TB
H:     NTFS         10.91 TiB    9.45 TiB  SATA    HDD    0     HHD
M:     NTFS          5.24 TiB    2.63 TiB  -       -      -     _sync_music
P:  … R:  … W:  … X:  … Y:  (network shares, same shape, disk "-")
== config get ui.dualPane  → true
== config set ui.dualPane false  → ui.dualPane = false
== config get ui.dualPane  → false
== config set ui.dualPane yes → cabinetos-cli: ui.dualPane: invalid type: string "yes", expected a boolean (config_error)
== ls …\cabinetos-core-test\live-files      → - draft.txt
== mkdir "…\live-files\New folder"           → created …\New folder
== rename …\live-files\draft.txt final.txt   → renamed …\draft.txt to final.txt
== ls …\live-files --long
d ----- 2026-09-28 17:12           <DIR> New folder
- ---A- 2026-09-28 17:12               6 final.txt
== mkdir again → …\New folder: already exists (already_exists)
== rename other.txt → final.txt → …\other.txt: …\final.txt: already exists (already_exists)
== rename other.txt → 'sub\x.txt' → sub\x.txt: a name cannot contain `\` or `/` (invalid_path)
== open …\live-files\cabinetos-open-check.txt → opened …\cabinetos-open-check.txt
== open …\missing.txt → not found (not_found)
```

- **Timings from the core log:**

| Request | Time |
|---|---|
| `list_volumes` | 4.6 ms |
| `set_value` | about 3 ms (it writes the file) |
| `get_value` | about 0.03 ms |
| `create_directory` | about 0.3 ms |
| `rename` | about 0.4 ms |
| `open_path` | 281 ms (until Notepad took the file) |

- **What `open` showed:** Notepad was not running before. After `open`, two `notepad.exe` processes appeared, and one had the window title `cabinetos-open-check.txt - Notepad` (the .txt default here is the packaged Windows Notepad). I closed it with `CloseMainWindow()`, and both processes were gone. The temp files are deleted.
- **`volumes_changed` with a real broadcast** (debug core with `events watch`):
  - `subst T: <temp folder>` → one event listing `CDEGHMPRTWXY`;
  - `subst T: /D` → one event listing `CDEGHMPRWXY`;
  - `volumes` listed `T:` in between. No subst letter is left behind.

### Decided
- **CI change detection is my own `git diff` job, not a marketplace action** — because GitHub has no per-job path filters, and plain git needs no token permissions. Checked against real commits: a UI-only commit gave core=false ui=true, a core-only commit gave core=true ui=false, and a `ci.yml` change gave both. Undo: revert a1ccf5d.
- **Local drives get 2 s** (network drives 200 ms, as asked) — because an optical drive may spin up. Undo: `LOCAL_LIMIT` in `volumes.rs`.
- **Every failing drive is skipped, not only not-ready ones**, with a debug log line — because one bad drive (for example, a locked BitLocker drive) must not hide the others. Undo: the `Err` arm of `query`.
- **A timed-out letter is not asked again while its query still runs** — because a server that is gone would otherwise hold one more blocking thread with every request. Undo: `STILL_ASKING` in `volumes.rs`.
- **A subst letter now resolves to its folder's volume.** The live test found that `GetVolumePathNameW` refuses subst letters with error 87, so `volume_info` failed on them and `list_volumes` hid them. `info_for_path` now resolves the folder with `QueryDosDeviceW`. Undo: revert d4d49dd.
- **The drive watcher uses a hidden top-level window, not the suggested message-only window** — because message-only windows never receive broadcasts. The live subst test confirmed the hidden window does. Undo: revert a1606e8.
- **`volumes_changed` carries the full list**, like `keymap_changed` carries the keymap — because the UI then needs no second request. Undo: change the event in `message.rs`.
- **`set_value` now also runs the file's value checks** (version, terminal profiles). A bug I found: `terminal.defaultProfile = "fish"` passed, was written to the file, and would have made the next core start refuse the file. Undo: `check_values` in `store.rs`.
- **`get_value` and `set_value` paths use object keys only**, the same form `config_changed` uses. Unknown paths are `config_error`. Undo: `Settings::get_value`.
- **`open_path`, `create_directory` and `rename` need absolute paths** (otherwise `invalid_path`) — because the core's working folder means nothing to a client. **`open_path` also needs the path to exist as given** — because `ShellExecuteExW` would otherwise probe extensions or search the PATH (`C:\x\setup` could run `setup.exe`). Undo: `not_absolute` in `connection.rs`, and the attribute check in `open.rs`.
- **New names are checked before the call:** no trailing dot or space, none of `<>:"|?*` or control characters, no device name such as `CON` or `nul.txt`, and not `.` or `..` — because verbatim paths take names literally. Renaming to the same name is `ok`, and changing only the case works. Undo: `check_name` in `ops.rs`.
- **`ERROR_FILENAME_EXCED_RANGE` now maps to `invalid_path`**, and `ERROR_FILE_EXISTS` or `ERROR_ALREADY_EXISTS` to `already_exists`. Undo: `error.rs`.
- **`config set` takes JSON, or a bare word as text** (`config set ui.theme nord`). Undo: `json_or_text` in `settings.rs`.
- **Small refactors needed for clippy's 100-line limit:**
  - The core's `handle_frame` now hands requests to `settings_request` and `file_request`.
  - `write_keybinding` is renamed `write_setting`, since `set_value` uses it too.
  - The CLI's `execute` now hands off to `volume_command` and `file_command`.
  - Undo: none needed.
- **I edited three files in `ui/`, because the UI's contract tests pin the schema:**
  - `ProtocolTests.cs`: `list_volumes` is now checked against the schema like every other request, and the count went from 13 to 14 (the UI agent has since made it 20);
  - `EndToEndTests.cs`: version 7 → 8;
  - `MessageCodec.cs`: `EventTypes` gained `volumes_changed`, and the comment now says version 8. I kept its CRLF line endings.
  - The UI agent has since built on these changes: `311e364` makes the Drives section follow `volumes_changed`.
- **Two stale doc comments dropped "(now 3)"** in `message.rs` (pong and welcome).

### Needs the user
- **Ready text for `docs/PLAN.md`**, a note under Phase 5:
  > **Core additions for the UI (protocol version 8), 2026-09-28.** `list_volumes` answers every drive letter's volume as `volume_info` describes it, in letter order; the drives are asked in parallel, and one that is not ready, fails or does not answer (a network drive within 200 ms, a local one within 2 s) is left out; a `subst` letter shows its folder's volume. `volumes_changed` brings the new list when a drive letter comes or goes: a USB stick, a card, a mapped share, a `subst` letter. The core hears Windows' `WM_DEVICECHANGE` broadcasts through a top-level window that is never shown. `get_value` and `set_value` read and change one setting by its dotted path; a change is checked as a saved file is, written atomically, and announced with `config_changed`. `ui.lastPaths` and `ui.pinned` are new settings. `open_path` opens a file or folder with its default application (the shell's `open` verb); this is navigation infrastructure, and viewers and editors stay extensions (Article 10). `create_directory` and `rename` (in place, never replacing) need no job, and a taken name is the new error code `already_exists`. CLI: `volumes`, `config get` and `config set`, `open`, `mkdir`, `rename`. CI now runs only the jobs a push touches, each in its own concurrency group. Measured on this PC (release build): `list_volumes` 2–5 ms for 11 volumes, 6 of them network shares; `set_value` about 3 ms; `open_path` of a text file 281 ms until Notepad took it. Shell type names and icons are still to come.
- **README status line:** in the core sentence, change "(`core/`, 398 tests, CI green)" to "(`core/`, 421 tests, CI green)". You may also add "…and gives the shell its drives, one-setting reads and writes, and file actions (protocol 8)…". The UI count in that line is the UI agent's own: 203 at 10dd189.
- **For the UI agent:**
  - The comments in `ui/CabinetOS.Core/Protocol/Replies.cs` and `Requests.cs` still say `list_volumes` is "not in protocol version 7 yet".
  - Before `open_path`, the UI could call `AllowSetForegroundWindow(core pid)`, so the opened window comes to the front. Otherwise the core, running in the background, may open it behind the current window.

### Known gaps
- **CI concurrency has an ordering limit.** Two runs of the same commit appeared twice, one second apart; I do not know why. In both cases one run's jobs were cancelled, which is harmless for the same commit.
  - GitHub orders a job-level group by the time a job enters it, not by push order. If two pushes land within about 20 s, the older push's job can win and the newer one's be cancelled; re-run it then.
  - Splitting into `core.yml` and `ui.yml`, each with a workflow-level group, would order runs strictly by push. That would move the UI agent's job into a file of its own, so I did not do it.
- **The success path of `open_path` has no automated test**, because it would leave an application window open. The refusals are tested, and the success was checked live.
- **`volumes_changed` has no end-to-end test**, because that needs a real device change. The watcher is tested with messages sent to its window, and the whole path was checked live with subst.
- **A client that falls behind on events** gets no fresh `volumes_changed`, unlike the config, job and plugin events.
- **A local drive slower than 2 s** (an optical drive spinning up) is missing from that one reply.
- **`set_value` on `keybindings`** reports a conflict as `config_error`, not `keybinding_conflict`: every `set_value` refusal is `config_error`, as asked.
- **Shell type names and icons**, which PLAN lists as waiting for the core, were not part of this task.

### Noticed out of scope
- `execute_command` for `file.newFolder` and `file.rename` still answers `not_implemented`; the UI uses the new requests instead.
- The CLI's error prefix doubles paths for `rename` ("…\other.txt: …\final.txt: already exists"), because the message names the taken target.
- `set_value` fsyncs the file on every call, about 3 ms each. If the UI writes `ui.lastPaths` on every folder change, it may want to write less often.
