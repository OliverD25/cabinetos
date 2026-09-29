## Report: hardening step 2, edge cases in the core

Context: CabinetOS core, hardening step 2 (edge cases, classes A to E, tests first, one class per commit), plus the three items folded in from the shell's report. All done. Commander Compact and 11a are next in my queue; the theme-collection test comes after 11a.

## Built — per class

| Class | Tests added (named below) | What failed before | What changed |
|---|---|---|---|
| A names beyond ASCII | 23 | Search (index, the core's walk, the CLI): an NFC `café` query did not find the NFD `café.txt`. A UTF-16 `cabinetos.json` (what Windows PowerShell 5.1's `>` writes) was refused. A case-only conflict named the source's spelling (`Report.txt`), not the file already there (`report.txt`). `cli describe` columns shifted after wide names. Run in parallel, two hydrators drawing icons made `SHGetFileInfoW` answer "no icon" (12 of 12 runs). | Search folding is now lowercase followed by NFC, done by Windows' `NormalizeString`. Config reads UTF-16 with a BOM and writes UTF-8 back; `config validate` does too. Conflict `destination` = on-disk spelling (`FindFirstFileExW`). `describe` prints the name last. The icon-drawing lock is process-wide. |
| B long paths | 9 | A delete to the Recycle Bin handed any path to the shell's silent `IFileOperation`. `open_path` on a long path gave `io` with an empty message. | A tree with a path of 260 or more UTF-16 units becomes a `path_too_long` conflict **before** the shell sees it. `open_path` answers `invalid_path` with the length and the limit. |
| C links | 14 | The listing did not say which kind of link. **A move across volumes with `follow_target` deleted the link target's own files** (each copied item was deleted by its path through the link). Copying a file symlink as a link failed under Developer Mode (error 1314). `open_path` on a broken link gave "unspecified error". | Link flags and the reparse tag are in the listing. What a followed link holds is only copied; the move removes only the link. A file link is copied by writing its reparse data when `CopyFileExW` lacks the privilege. A broken link gives `not_found` naming the target. |
| D cloud placeholders | 6 | No "not on this disk" mark. `describe_entries` gave an offline or cloud `.exe/.lnk/.ico` a `path:` key, so `get_icon` read the file, and reading downloads a cloud file. | New `FLAG_NOT_ON_DISK`. Such files get their `ext:` key and are never read to draw them. |
| E two cores | 6 | `set_value` from two cores lost a change in round 1 of 25. Two concurrent installs kept one entry in `installed.json`. | A lock file held from read to write: `.cabinetos.json.lock` next to the config, `.installed.json.lock` in the marketplace folder (`File::lock`, which is Windows `LockFileEx`; the OS frees it if a core dies). |
| Extra | 0 new, 2 updated | none | `window.new`: "Window: New Window", target `ui`, `ctrl+n` (it was free). The registry now has 52 commands. |

Named tests:
- **A**
  - fs `list_directory`: `names_beyond_ascii_come_back_unit_for_unit`, `names_beyond_ascii_sort_as_explorer_sorts_them`, `names_that_differ_only_by_case_are_two_rows_in_a_case_sensitive_folder`, `a_name_with_a_lone_surrogate_keeps_its_units_in_the_listing`
  - fs hydrate: `names_beyond_ascii_get_type_names_and_icons`
  - fs ops: `names_beyond_ascii_are_created_and_renamed`, `a_name_holds_255_utf16_units_and_a_surrogate_pair_counts_twice`
  - index: `folding_ignores_case_and_normal_form_beyond_ascii`, `names_beyond_ascii_are_found_without_case_or_normal_form`
  - core: `a_walk_finds_names_beyond_ascii_without_case_or_normal_form`
  - jobs: `names_beyond_ascii_are_copied_moved_and_deleted`, `a_case_only_twin_is_a_conflict_that_names_the_file_there`, `the_recycle_bin_takes_names_beyond_ascii` (CI only)
  - terminal: `names_beyond_ascii_are_quoted_like_any_other_character`, `cmd_follows_the_pane_into_a_folder_beyond_ascii`, `powershell_follows_the_pane_into_a_folder_beyond_ascii_when_installed`, `wsl_translates_a_folder_beyond_ascii_when_installed`
  - config: `paths_beyond_ascii_round_trip_through_the_file`, `set_value_takes_text_beyond_ascii`, `a_utf16_file_is_read_and_written_back_as_utf8`
  - cli `tests/edges.rs`: `ls_prints_names_beyond_ascii_unit_for_unit`, `describe_prints_the_name_last_so_wide_names_keep_the_columns`, `search_takes_a_query_beyond_ascii_and_prints_the_hits`
- **B**
  - fs: `a_long_path_lists_the_same_in_the_plain_and_the_verbatim_form`, `watches_a_folder_deeper_than_260_characters`, `a_folder_deeper_than_260_characters_gets_type_names_and_icons`, `a_path_too_long_for_the_shell_is_refused_with_the_reason`
  - index: `paths_deeper_than_260_characters_are_assembled_whole`
  - core: `a_walk_reaches_files_deeper_than_260_characters`
  - jobs: `a_tree_deeper_than_260_characters_is_copied_moved_and_deleted`, `a_path_too_long_for_the_recycle_bin_waits_for_an_explicit_decision`
  - cli: `a_folder_deeper_than_260_characters_works_like_any_other`
- **C**
  - fs: `only_junctions_and_symbolic_links_get_a_flag`, `links_say_what_they_are`, `a_link_lists_what_it_points_to_and_a_broken_one_is_still_a_link`, `watching_a_junction_reports_changes_in_what_it_points_to`, `a_link_whose_target_is_gone_is_not_found_before_the_shell_is_asked`
  - index: `a_link_is_an_entry_of_its_own_and_is_never_entered`
  - core: `a_walk_never_follows_a_link_into_a_loop`
  - jobs: `a_move_that_follows_a_link_never_deletes_through_it` (plan level), `deleting_a_link_removes_the_link_and_never_what_it_points_to`, `a_link_given_as_the_source_is_copied_as_a_link_or_followed_and_moved_as_a_link`, `following_links_stops_at_a_link_back_into_the_copy`, `a_symbolic_link_to_a_file_is_copied_as_a_link_wherever_this_user_may_make_one`, `the_recycle_bin_takes_a_junction_and_not_what_it_points_to` (CI only)
  - cli: `ls_long_names_the_kind_of_each_link`
- **D**
  - fs: `data_elsewhere_is_marked_not_on_this_disk`, `a_file_not_on_this_disk_gets_its_icon_by_extension_and_is_never_read`, `a_file_whose_data_is_elsewhere_is_marked_not_on_this_disk`, `a_file_coming_back_to_this_disk_is_a_change_and_the_watcher_goes_on`
  - jobs: `a_copy_of_a_file_whose_data_is_elsewhere_reads_it_and_is_here`
  - cli: `ls_long_marks_a_file_whose_data_is_not_on_this_disk`
- **E** (core `tests/two_cores.rs`, two real core processes): `set_value_from_both_cores_at_once_keeps_every_change`, `both_cores_writing_the_shipped_themes_at_start_leave_whole_files`, `two_installs_at_once_keep_both_in_the_record`, `a_core_that_crashes_leaves_the_other_unharmed`, `the_indexer_serves_both_cores_at_once`, `two_cores_log_into_one_file_without_losing_a_line`

Some tests failed first because the test itself was wrong; I fixed the test, not the code. Three cases:
- the cmd quoting expectation (a space too many);
- the index length assertion (the path is 375 characters, I asserted over 400);
- the themes test did not know about `theme.schema.json`.

## Protocol fields for the shell (all additive: protocol version still 11, section layout still 2)

**Listing entry flags** (`ListingEntry.flags`, byte at offset 15):

| Flag | Value | Meaning |
|---|---|---|
| `FLAG_ID_IS_NAME_HASH` | 1 | existing |
| `FLAG_JUNCTION` | 2 | a junction |
| `FLAG_SYMBOLIC_LINK` | 4 | a symbolic link; a folder link when `FILE_ATTRIBUTE_DIRECTORY` is set |
| `FLAG_MOUNT_POINT` | 8 | a mount point |
| `FLAG_NOT_ON_DISK` | 16 | not on this disk: the attributes include `RECALL_ON_DATA_ACCESS` 0x400000, `RECALL_ON_OPEN` 0x40000 or `OFFLINE` 0x1000 |

- `EntryFacts.LinkOf` can map these flags directly: junction flag to `Junction`, symbolic-link flag to `SymbolicLink`, mount-point flag to `MountPoint`. Kind 3 with none of these flags stays `Unknown`; a WSL link is one example.

**Listing metadata** (`ListingMeta`):
- The u32 at offset 36 is now `reparse_tag`. It was `reserved` (always 0) before; the C# field `ListingMeta.Reserved` can become `ReparseTag`.
- It holds the entry's `IO_REPARSE_TAG_*` value, or 0 when the entry is not a reparse point.
- Cloud, WOF and AppExecLink reparse points stay kind file or folder, but their tag is here.

**Jobs:**
- A `path_too_long` conflict in a delete to the Recycle Bin has no `destination`.
- It accepts `delete_permanently`, `skip` and `retry`.
- In every other job, `delete_permanently` is still `invalid_resolution`.

**Answers:**
- `open_path` of a path the shell refuses at 260 or more characters: `invalid_path`. The message is "the shell refused to open it; its path has N characters, and many programs take only paths shorter than 260; …".
- `open_path` of a link whose target is gone: `not_found`, and the path in the message is the target's.
- A conflict's `destination` now uses the spelling the folder has on disk.

**Command:** `window.new`.

## Commits (all pushed)
- 4b74a31 — A.
- da97b62 — B, but it holds **only the file rename** (names.rs to edges.rs). Its `git add` named the old path and stopped, so nothing else was staged. I did not amend, because it was already pushed.
- 656a903 — the B content that da97b62's message describes; its own message explains this.
- 1eb3059 — C.
- 55af756 — D.
- 8c66593 — E.
- ca8f5c3 — `window.new`.

## Checks
- **Five checks** after each class, all green. Final: build ok; tests **554 passed, 0 failed, 5 ignored** (496 before step 2); clippy -D warnings ok; fmt ok; deny ok.
- **UI tests** (`dotnet build` + `dotnet test`): 485 total, 484 passed, 1 skipped (the shell's own opt-in test), 0 failed.
- **Not run here, by the project's rule:** the three Recycle Bin tests that are gated by `CABINETOS_TEST_RECYCLE_BIN=1` (CI only): `the_recycle_bin_takes_names_beyond_ascii`, `the_recycle_bin_takes_a_junction_and_not_what_it_points_to`, and the earlier one.

## Live check
All runs used `cabinetos-cli` against `sdk/fixtures/edge-fixture.ps1 -Root %TEMP%\cabinetos-core-test\edge-fixture`. The script ran in both Windows PowerShell 5.1 and PowerShell 7. It reported: 255-unit name; case-sensitive folder yes; long path 323 characters; symbolic links yes in 7, "Administrator privilege required" in 5.1.

**A — names beyond ASCII:**
- `ls names`: `case, Ґанок, 📁 photos, 中文文件夹, aaa…(255).txt, café(NFD), café(NFC), Звіт 2026.txt, Їжак і Єнот.md, مستند.txt, 𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt, 日本語のファイル.txt`. This is exactly docs/ui.md's order. `StrCmpLogicalW` calls the NFC/NFD pair and the case pair equal, and the UTF-16 units break the tie.
- `search café` and `search cafe+U+0301` each gave 2 hits; `search звіт` gave 2; `ҐАНОК` gave 1.
- Copying the case-sensitive `case\` folder into an ordinary folder: the second twin became a conflict whose card names `…\case\Report.txt`, the file already there.
- Copy, move, permanent delete, `mkdir` and a Cyrillic case-only rename all worked.
- `config set ui.pinned` with Cyrillic, emoji and Arabic paths round-tripped; a UTF-16 copy of the file passed `validate`.
- The console path: the CLI ran inside a pseudo-console through `cli term --profile cmd`, and all 12 names rendered exactly (checked strictly as UTF-8).

**B — long paths:**
- At a 309-character folder, `ls`, `describe` and `search` worked.
- Delete to the Recycle Bin: "a path in it is too long for the Recycle Bin; nothing was deleted". `skip` kept the tree; `delete-permanently` removed it.
- `open` of a 316-character `.cmd`: `invalid_path` with the reason; its marker file was not written.

**C — links:**
- `ls --long links` shows `[junction]` and `[symbolic link]` ×2; `loop` shows `back to loop [junction]`.
- The walk of the whole fixture completed (`kept`: 4 hits, `inside`: 1).
- The copy of `links\` hit the 1314 error before the fix; the tests prove the fix.
- Deleting the copied junction for good left `link-target` with all 3 files.

**D — not on this disk:**
- `ls --long` shows `online only.exe [not on this disk]`.
- `describe`: that file gets `ext:.exe`, while its on-disk twin `tool.exe` keeps its `path:` key.
- The copy is an ordinary file.

**E — two cores:**
- Two cores on one folder, 20 rounds of simultaneous `config set` (one setting from each core): 0 rounds lost a change.
- Each core sees the other's latest value, and `.cabinetos.json.lock` is present.

## Decided
- **Sort:** Explorer's natural order (`StrCmpLogicalW`, user locale, folders first). Names it calls equal are ordered by UTF-16 units: capitals first, NFD before NFC. The shell never sorts.
- **Search folding:** Unicode lowercase, then NFC (Windows `NormalizeString`). Accents are kept (`cafe` does not find `café`). Compatibility forms are not folded (`𝔘` is not `U`).
- **Paths:**
  - A request may give the user's form or the `\\?\` form.
  - The core adds the prefix itself right before each Windows call, and answers in the client's form.
  - The shell calls keep the old limit: the Recycle Bin (checked before the shell sees the path) and `open_path` ("may refuse": the UI agent saw Notepad open 339 characters; I saw cmd refuse 311, also in its 8.3 form).
- **Links:**
  - Copy default `as_link` (as robocopy does); `follow_target` copies what the link points to (as Explorer does).
  - A move never deletes through a link.
  - A mount point is a junction whose target is a volume; the core reads each junction's reparse data once to tell.
- **Not on this disk:** a copy downloads the file, which is right; the copy is an ordinary file.
- **Two cores:** lock files as above. Themes are already safe: temporary names carry the process ID, and the rename is atomic. Logs are already safe: `FILE_APPEND_DATA`, one write per line.
- **Case-only conflicts:** overwrite by copy keeps the existing file's spelling; overwrite by a move on one volume takes the moved file's spelling. A copy does not carry a folder's case sensitivity.
- **Fixture:** `sdk/fixtures/edge-fixture.ps1`. Same layout as the shell's script, plus `loop\`. It refuses a `-Root` it did not make, and cleans up with `rmdir /s` (which never follows links).
- **From the shell's report:**
  - (1) `window.new` added.
  - (2) Wording changed to "may refuse", in ipc.md and in the answer text. Note: my 656a903 message still says "refuses"; history is not rewritten.
  - (3) The core's log appends are atomic, now proven by `two_cores_log_into_one_file_without_losing_a_line`.

## Needs the user
Nothing blocking. Optionally, one CI run with `CABINETOS_TEST_RECYCLE_BIN=1` would run the 2 new Recycle Bin tests. That is your call, since no workflow is run without it.

## Known gaps
- **A name with a lone surrogate:** the section keeps its exact units, but JSON carries text, so no request can name that file yet.
- **Stand-ins instead of the real thing:**
  - Real cloud placeholders: stand-ins are used, because a real one needs a sync provider.
  - Real volume mount points: a stand-in reparse point to a volume that does not exist, because `mklink /J` refuses such a target and `mountvol` needs Administrator rights.
  - The shell's own Recycle Bin behaviour for long paths was not tried, because it would touch this PC's Recycle Bin.
- **Documented, not tested:** the walk entering a `RECALL_ON_OPEN` folder makes the sync provider fetch its list of names.

## Noticed out of scope
- The palette's and the marketplace's search (`cabinetos-commands` `rank`) fold per character, with no NFC step. The index's folding could be reused there.
- `terminal_open` with a folder deeper than 260 characters: `CreateProcess` limits the current directory to 260. `terminal_sync_cwd` into such a folder: cmd and Windows PowerShell cannot enter it.
- Theme files are read as UTF-8 only; the config file now also accepts UTF-16.
- `cli ls --long` has no letter for the offline attribute.
- The shell's `ui/livecheck/edge-fixture.ps1` clears its root with `Remove-Item -Recurse`. `sdk/fixtures/edge-fixture.ps1` refuses a folder it did not make and never follows links.
- docs/PLAN.md line 160 says "51 commands"; there are 52 now.

## Ready text

**docs/PLAN.md** (under Phase 10):

**Hardening, step 2: edge cases in the core, 2026-09-29.** Five classes of edge cases, tests first, one commit per class, on the fixture the shell shares (`sdk/fixtures/edge-fixture.ps1`). Names beyond ASCII: search (the index and the core's walk) folds case and normal form, so `café` finds both spellings; a `cabinetos.json` saved as UTF-16 is read; a conflict names the file already there as the folder spells it; the listing keeps every name unit for unit in Explorer's order, with ties ordered by their units. Long paths: every file call takes any length, and answers keep the user's form; a delete to the Recycle Bin of an item with a path of 260 characters or more stops at a `path_too_long` conflict before the shell, whose silent flags could make it final, and `delete_permanently` answers it; `open_path` says when the shell refuses such a path. Links: the listing names junctions, symbolic links and mount points (`FLAG_JUNCTION`, `FLAG_SYMBOLIC_LINK`, `FLAG_MOUNT_POINT`, and the reparse tag in the field that was reserved); a move that follows links no longer deletes the files a link leads to; a symbolic link to a file copies as a link under Developer Mode; deleting a link never touches its target, and neither the walk nor the index enters one. Cloud files: `FLAG_NOT_ON_DISK` marks a file whose data is elsewhere, and its icon comes from its extension, so showing it never downloads it. Two cores on one configuration: a lock file next to `cabinetos.json` and next to `installed.json` keeps two windows from writing each other's changes away; themes, logs, the indexer and a crash of one core were already safe, and now tested. `window.new` (Ctrl+N) is in the registry. Additive: protocol version 11. 554 core tests.

**README:** in the line "(`core/`, 496 tests)", 496 becomes **554**. The UI count stays 485.

## Files
Created:
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\sdk\fixtures\edge-fixture.ps1
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\src\link.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-cli\tests\edges.rs
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-core\tests\two_cores.rs

Changed code (under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\):
- cabinetos-protocol\src\shm.rs
- cabinetos-fs\src\enumerate.rs, lib.rs, section.rs, hydrate.rs, ops.rs, open.rs
- cabinetos-fs\tests\list_directory.rs, watch.rs
- cabinetos-index\Cargo.toml, src\names.rs, search.rs, win.rs
- cabinetos-core\src\search.rs, tests\config.rs
- cabinetos-jobs\src\lib.rs, plan.rs, run.rs, win.rs, tests\engine.rs
- cabinetos-terminal\src\shell.rs, tests\sessions.rs
- cabinetos-config\src\lib.rs, parse.rs, store.rs
- cabinetos-market\src\install.rs
- cabinetos-cli\src\describe.rs, jobs.rs, ls.rs, settings.rs
- cabinetos-commands\src\registry.rs

Changed docs (under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\):
- docs\ipc.md, jobs.md, indexer.md, config.md, terminal.md, marketplace.md, themes.md, diagnostics.md, keybindings.md
- sdk\README.md
