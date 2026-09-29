## Report: sub-phase 11a, the core's part

Report: sub-phase 11a, the core's part (CabinetOS, 2026-09-29). Protocol version is now **12** (one raise for the whole list); the UI agent already moved its pin to 12 in 8239bb6.

## Built
All eight groups of Part 3 (b) of docs/research/total-commander.md.
1. **P1, keypad keys.** `numpadadd`, `numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal` are in the key grammar. VS Code's `numpad_add` and the other four names are accepted as aliases. `numpad0` and `numpad_0` are refused (the keypad digits stay the plain digits).
2. **P2, several default keys per command.** `Seed.keys` is now a list. `list_commands` and the keymap export carry every key.
   - How the pencil (`keys.rebind`, `set_keybinding`) treats a command with several keys: it writes one override, which replaces all of that command's default keys; `reset_keybinding` brings them all back.
   - The Immutable System Tier is unchanged, and a test holds each of its commands to its one key.
3. **P3, two new settings.** `panes.selection` (`windows` by default, or `commander`) and `files.editor` (`null`, or `{command, args}`). An empty `command` is refused with a clear message. The schema is regenerated, and docs/config.md has both.
4. **Aliases A1 to A5.** F8, Shift+F8, Shift+F6, Alt+F7 and Ctrl+Num + are second default keys of existing commands.
5. **N1 to N31.** 31 seed rows, target `ui`, with the note's exact titles, keys and `when`. The registry now has 83 commands (was 52).
   - Key check: every new key was compared with all 38 existing bindings, in any context. No key is taken, no key is used twice, and no chord prefix clashes (`ctrl+k` is the only prefix in use).
   - Ctrl+P, Ctrl+U, Ctrl+R, Ctrl+D, Ctrl+\ and Ctrl+F3 to Ctrl+F6 were free, so no fallback key was needed.
6. **Sort key `extension`.** Files sort by extension ignoring case, then by name; files without an extension come first; folders keep name order. The extension rule is the Type column's (`.gitignore` has one).
7. **The six requests.** Each has unit tests, an end-to-end test through the CLI, a CLI verb, regenerated schemas and docs.
   - `create_file` (CLI `mkfile`): never opens or replaces a file; a folder with that name is `already_exists`.
   - `edit_path` (CLI `edit`): `files.editor`, else the file type's `edit` verb, else Notepad from the system folder. It never runs the file.
   - `show_properties` (CLI `props`): one path uses the `properties` verb; several use `SHMultiFileProperties`. The sheet belongs to the core's process.
   - `measure_paths` with `measure_started`, `measure_progress` (at most 30 a second), `measure_finished` and `cancel_measure` (CLI `measure`; Ctrl+C there sends the cancel). The walk is the Recycle Bin check's, moved from cabinetos-jobs to `cabinetos_fs::measure_tree`, and it no longer sorts each folder.
   - `match_entries` → `entry_matches` (CLI `match`): Total Commander's patterns, `[start, count]` ranges, `files_only`, and `first_from` for quick search.
   - `terminal_type_paths` (CLI `term type`): quoted per shell with `terminal_sync_cwd`'s rules, no Enter.
8. **Protocol 12.** Raised in the first commit that needed it (the new sort-key value); each later commit added its messages to the "Version 12 added…" text in ipc.md and to `PROTOCOL_VERSION`'s doc comment.

## Commits (all pushed)
- 15ef77e: P1
- 523056d: P2
- ab74ae8: P3
- 4103bf7: aliases
- 54a25e2: N1 to N31
- b8bd2f3: sort key `extension`, protocol 12
- 532c288: create_file
- c6f552c: edit_path
- 2d74098: fix of a racing test (see Known gaps)
- 08d2d92: show_properties
- 18a71cc: measure_paths
- 0dd9e04: match_entries
- 85e802d: terminal_type_paths

Group 7 got one commit per request instead of one commit for the group, following the "commit per unit" rule.

## Checks
- **Five checks** on 85e802d, from `core/`: build ok; tests 604 passed, 0 failed; clippy `-D warnings` clean; fmt clean; deny ok.
- **UI tests**, `dotnet build` + `dotnet test` in `ui/`: 602 in total, 601 passed, 0 failed, 1 skipped. The skipped one is the two-window desktop test, which runs only with `CABINETOS_UI_E2E=1`. No version-pin failure, because the UI agent had already moved the pin.
- **Nothing left open:** after the tests, no test core, Notepad or stub-editor process was running.

## Live check (CLI against a real core; scratch data only, under %TEMP%\cabinetos-core-test\live-11a)
**mkfile**
```
created C:\...\live-11a\check\New Text Document.txt        (0 bytes)
cabinetos-cli: C:\...\New Text Document.txt: already exists (already_exists)   ← the second call
```

**measure on %TEMP%\cabinetos-bench\100000** (release build)
```
measure 1 started
  counting C:\Users\Admin\AppData\Local\Temp\cabinetos-bench\100000: 100000 files, 0 folders, 0 bytes
C:\Users\Admin\AppData\Local\Temp\cabinetos-bench\100000: 100000 files, 0 folders, 0 bytes (0.00 B)
measure 1 finished
```
- The bench files really are empty; I checked with `find -size +0`.
- In the core's log the walk took about 65 ms. The debug build gave the same output.
- A flat folder is read in one go, so it gets one progress line at most.
- To show the progress limit, I also measured `C:\Windows\System32`: 6 progress lines in about 215 ms of walking (release), 14 in 600 ms (debug). The total was `30316 files, 1878 folders, 22508950870 bytes (20.96 GiB), 19 unreadable`; the 19 are folders a user without administrator rights may not list.

**match `*.txt;*.md|readme*` on the same folder**
```
19999 of 100000 entries match, in 19999 ranges (generation 1)
[3, 1] Backup-42.txt
[6, 1] Backup-78.txt
```
- There are 19,999 ranges because the bench folder's `.txt` files never sit next to each other in name order.
- `match_entries` took 43 ms in the core (release).
- `match 'backup-12*' --first-from 50000` answered `10: Backup-126.docx`: no match after index 50000, so it went round to the start (19 ms).

**term type**, three paths into a real cmd, then pwsh. What each shell showed, with the VT sequences removed:
```
cmd:  C:\...\live-11a\check>"C:\...\check\a b.txt" "C:\...\check\100"%^P"ATH"%^ "& x" "C:\...\check\Звіт 'проєкт'.md"
pwsh: 'C:\...\check\a b.txt' 'C:\...\check\100%PATH% & x' 'C:\...\check\Звіт ''проєкт''.md'
```
Nothing ran. (Before the typed text, pwsh also shows its own history suggestion; that comes from PSReadLine, not from the core.)

**Sort by extension, 100,000 entries:** 50 ms against 41 ms by name (release), so the extension key costs 9 ms.

## Decided
- **Extension sort: folders keep name order.** The Type column calls every folder a folder, and folders do not jump when the files are re-sorted.
  - Implementation: the extension's sort key is placed in front of the name's key in the arena, so the shared comparison and the pipelined merge did not change.
  - This works because Windows ends every sort key with its only 0 byte; a test checks that property.
  - To undo: remove the folder check in `NameKeys::extend`.
- **edit_path**
  - A `files.editor` whose program is found nowhere is `spawn_failed`, and nothing else is tried: the setting is wrong. The meaning of `spawn_failed` was widened to cover the editor; this is within version 12.
  - A folder is refused with `invalid_path`.
  - The editor starts through `ShellExecuteExW` with `SEE_MASK_NO_CONSOLE`. The reason: the window starts the core with `CreateNoWindow`, so a console editor such as Vim would otherwise share the core's console, which has no window. This path also runs `.cmd` wrappers such as `code.cmd` correctly.
- **show_properties** with an empty `paths` is `protocol_error`.
- **measure**
  - Its events go only to the connection that asked, and need no `hello`.
  - `cancel_measure` always answers `ok`, because a cancel may cross the measure's end.
  - `results` lists only the paths counted to the end, in request order.
  - `folders` does not count the path itself; a file path gives `files: 1` and its size.
  - A link counts as the file or folder it looks like, is never entered, and adds no bytes.
  - No progress is sent for a count shorter than 1/30 s.
  - A path that went away after the start check gets `unreadable: 1`.
  - Closing the connection stops its measures.
- **match**
  - Two DOS endings are kept: `*.*` matches every name, and `X.*` also matches `X`; a trailing lone `.` means "no extension" (`*.`). Reason: the pattern box offers `*.*` first, and a plain glob would leave README unmarked.
  - With nothing before the `|`, every name is included; spaces around a pattern are dropped.
  - A `first_from` past the end starts at 0.
- **terminal_type_paths** refuses a path that contains a control character (`invalid_path`), because a line break would run the command. An empty list types nothing and answers `ok`. Paths are not checked on disk; they are only text.
- **Test fix, commit 2d74098.** The drive-watcher test in fs/drives.rs failed about one run in five. Cause: the `subst` test in volume.rs, in the same test process, makes Windows broadcast a real drive letter arriving and leaving, about 0.5 s later; I saw these broadcasts with a listener window.
  - The fix: the window test keeps only its positive checks, and the "not a change" checks now call `is_volume_change` directly. The watcher code itself is unchanged.
  - Result: 10 of 10 runs passed after the fix, against 8 of 10 before.

## Needs the user
Nothing.

## Known gaps
- **Property sheet in front:** whether the sheet comes to the front after the window calls `AllowSetForegroundWindow` can only be seen with the real window. It is left for the planning session's real-key run.
  - What is verified, in an fs test run four times: the sheet opens for one file and for several items, stays open after the thread that asked for it ended, and closes on `WM_CLOSE`.
  - The CLI's `props` test proves the request works; its sheets close when that test ends its core.
- **edit_path: two branches have no test that starts them,** because they would open real windows: the `edit` verb and Notepad. Only the choice between them is tested: `.bat` and `.cmd` have an `edit` verb, an unknown type has none, and the Notepad path is right.
  - The `files.editor` branch is tested end to end with a batch file as the editor. That batch file's console window shows for a moment during the test and closes by itself.
- **Measure progress comes per folder.** A single huge folder (for example on a slow network share) reports only when it has been read.
- **match on 100,000 names takes 43 ms,** because it decodes every entry of the section. Quick search with `first_from` stops at the first match.
- **A name that contains `;` cannot be matched as such.** Total Commander's quoting with `"` is not built.

## Noticed out of scope
- **`open_path` and console programs (likely, not verified).** `open_path` does not set `SEE_MASK_NO_CONSOLE`. So a `.bat`, `.cmd` or console `.exe` opened from the pane probably runs in the core's console, which has no window: the user would see nothing. Explorer gives such a program a console window. `edit_path` now sets the flag. I did not test this, because a test would have to start such a program.
- **docs/ui.md:474** still shows "0.1.0, protocol 11" as the example text of the About view. That is the UI agent's document.
- **The README's UI count** says 501 tests; I saw 602.

## Ready text for docs/PLAN.md (a paragraph under Phase 11, after 11a's "Articles" line)
**11a, the core's part, 2026-09-29.** The key grammar has the keypad's operators (`numpadadd`, `numpadsubtract`, `numpadmultiply`, `numpaddivide`, `numpaddecimal`, and VS Code's `numpad_add` names), and a command may have several default keys; the palette's pencil still replaces all of them, and the Immutable System Tier keeps one key each. The seed has Total Commander's second keys (F8, Shift+F8, Shift+F6, Alt+F7, Ctrl+Num +) and the 31 commands N1 to N31, run by the window: 83 commands in all. None of the new keys was taken, so no fallback key was needed. `cabinetos.json` has `panes.selection` (`windows` or `commander`) and `files.editor`. The protocol is version 12: the sort key `extension` (files by extension, then by name; folders keep name order), `create_file`, `edit_path` (`files.editor`, else the type's `edit` verb, else Notepad; it never runs the file), `show_properties` (Windows' own sheet for one path or several), `measure_paths` with progress, finish and cancel (the Recycle Bin check's walk, moved into `cabinetos-fs`), `match_entries` (Total Commander's patterns, answered as ranges, and quick search with `first_from`), and `terminal_type_paths` (quoted for each shell, without Enter). The CLI has `mkfile`, `edit`, `props`, `measure`, `match` and `term type`. Measured in release on the development PC: sorting 100,000 entries by extension takes 50 ms against 41 ms by name; measuring the flat 100,000-entry bench folder takes 65 ms, and `C:\Windows\System32` (30,316 files, 1,878 folders) 215 ms with 6 progress events; matching `*.txt;*.md|readme*` on 100,000 names takes 43 ms. Left for the real-key run: whether Windows' property sheet comes to the front after the window's `AllowSetForegroundWindow`.

## Ready text for the README
In the summary paragraph, replace "(`core/`, 564 tests)" with "(`core/`, 604 tests)".

## Files (all under E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\)
- **core\crates\cabinetos-commands\src\**
  - keys.rs: keypad keys and their aliases
  - registry.rs: `Seed.keys` as a list, the aliases, the N1 to N31 rows, tests
  - search.rs: the palette ranking expectation for the new commands
- **core\crates\cabinetos-config\src\**
  - model.rs, parse.rs, store.rs, lib.rs: `panes.selection` and `files.editor`
- **core\crates\cabinetos-fs\**
  - src\sort.rs: the extension key
  - src\pipeline.rs and tests\pipeline.rs: the pipelined order tested with the extension key
  - src\ops.rs: `create_file`
  - src\open.rs: one `ShellExecuteExW` helper, `edit_path`, `show_properties`
  - src\measure.rs (new): the shared walk
  - src\pattern.rs (new): `NamePatterns` and `match_entries`
  - src\drives.rs: the test fix
  - src\lib.rs: the exports
  - Cargo.toml: two `windows` features added (Win32_System_SystemInformation, Win32_UI_Shell_Common)
- **core\crates\cabinetos-jobs\src\**
  - plan.rs and run.rs: the Recycle Bin check now uses `cabinetos_fs::measure_tree`
- **core\crates\cabinetos-terminal\**
  - src\conpty.rs: `find_program` made public
  - src\lib.rs: its export, and `Terminals::type_paths`
  - src\session.rs: one input helper shared by `sync_cwd` and `type_paths`
  - src\shell.rs: `typed_paths`
  - tests\sessions.rs: the test of paths typed into a real cmd
- **core\crates\cabinetos-protocol\src\**
  - message.rs: every new message, `MeasureResult`, the `extension` sort key, the wider meaning of `spawn_failed`
  - lib.rs: `PROTOCOL_VERSION` 12
- **core\crates\cabinetos-core\**
  - src\connection.rs: routing for every new request, measures per connection, dispatch helpers
  - src\measure.rs (new): the path check and the counting loop
  - src\lib.rs: the new module
  - tests\commander.rs (new): measure and match against the real core
  - tests\config.rs: the command count, 83
  - tests\core_process.rs: the version pin, 12
- **core\crates\cabinetos-cli\**
  - src\main.rs: the verbs `mkfile`, `edit`, `props`, `measure`, `match`, `term type`, and `--sort extension`
  - src\measure.rs and src\patterns.rs (new): the `measure` and `match` verbs
  - src\describe.rs: its names reader is shared with `match`
  - tests\commander.rs (new) and tests\term.rs: the end-to-end tests
- **docs\**
  - ipc.md: the version text, the table rows, and sections on files, folder sizes, matching names and typed paths
  - config.md: the two new settings and the new sort key
  - keybindings.md: key grammar, several default keys, the aliases and the N rows
  - jobs.md: the shared walk
  - terminal.md: "Typing paths"
- **sdk\**
  - config\cabinetos.schema.json and protocol\request, response and event schemas, regenerated

## Next
The queued theme-collection test in `cabinetos-themes` (one commit, one sentence of ready text) has not started yet. I will start it when resumed.
