## Report: the order of `edit_path`'s checks

## Built
- **The path is now checked before the editor.** Before, with a `files.editor` that is found nowhere, F4 on a folder or on a missing file answered `spawn_failed`. The cause: the core resolved `files.editor` before `cabinetos-fs` checked the path.
- **New function:** the path checks of `cabinetos_fs::edit_path` are now their own public function, `check_editable`. It passes when the file exists as given (a link whose target is gone does not) and is not a folder.
- **New order in the core:** first `check_editable`, then the program of `files.editor`, then the launch.
- `edit_path` still calls `check_editable` itself, so it stays safe for any other caller; the second look costs two file-system calls.
- **Three new tests,** each against a real core whose `files.editor` names a program found nowhere; nothing starts in any of them:
  - a folder answers `invalid_path`;
  - a missing file answers `not_found`;
  - a file that exists answers `spawn_failed`, and the message names the setting and the program.
  - With the new check taken out for one run, the first two failed with `spawn_failed`; with it back, all three pass.
- **docs/ipc.md:** the `edit_path` paragraph now says the order: the path is checked first (absolute, exists, not a folder), and those answers come whatever `files.editor` names; then the editor is chosen.

## Commits
- 2159e61: core, fs: edit_path checks the path before it looks for the editor

## Checks
Five checks, run from `core/`: build ok; tests 610 passed, 0 failed; clippy clean; fmt clean; deny ok. UI tests were not rerun: the protocol and its messages did not change, only the order of answers the window already handles.

## Live check
None needed: the three tests use the real core.

## Decided
A public `check_editable`, called twice, instead of a new two-step type in the fs API. It is the smallest change that puts the path first, and `edit_path` stays complete on its own.

## Needs the user
Nothing.

## Known gaps
None.

## Ready text for docs/PLAN.md (one sentence, for the 11a paragraph)
`edit_path` now checks the path before it looks for the editor, so a folder is `invalid_path` and a missing file `not_found` even when `files.editor` names a program that is found nowhere.

## Ready text for the README
Replace the core test count with "(`core/`, 610 tests)".

## Files
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\src\open.rs: `check_editable`, which `edit_path` now calls.
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-fs\src\lib.rs: its export.
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-core\src\connection.rs: the path check before the editor is resolved.
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\core\crates\cabinetos-core\tests\commander.rs: the three tests.
- E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos\docs\ipc.md: the order, in the `edit_path` paragraph.

My queue is empty.
