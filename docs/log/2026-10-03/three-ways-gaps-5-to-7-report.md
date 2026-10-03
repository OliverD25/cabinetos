# Settings reachable three ways: gaps 5, 6 and 7 of the audit

Coder report, Sonnet 5.5, 2026-10-03 (the night the creator slept). Branch
`worktree-agent-a4be948efece8a17e`, from main's `469acc3`. The rule and the audit:
the project skill `settings-three-ways`; the unit before this one closed gaps 1 to 4
([theme-gallery-and-three-ways-report.md](theme-gallery-and-three-ways-report.md)).
Nothing was merged or published. Gap 8 (the Settings page, Phase 11c) is not touched.

## What was built

Each of the eight settings follows the pattern of gaps 1 to 4: a command in the
registry (UI target, no default key), a control in the window, and the write through
the core's `set_value`, so the file, the palette and the window follow each other.
A command that chooses one of several values opens a pick list (the prompt in the
palette's frame, the value in effect highlighted) and takes its value as an argument
(`{"value": ...}`, for the editor `{"default": true}`, `{"program": name}`,
`{"choose": true}`), which is how a menu row runs it without the list. The palette's row
carries the state word (`SettingStates`).

| Gap | Key | Command (palette title) | Palette row says | Control in the window |
|---|---|---|---|---|
| 5 | `terminal.defaultProfile` | `terminal.chooseDefaultProfile` ("Terminal: Default Profile", pick list of `terminal.profiles`) | the profile | "Default Profile…" row in the dock chevron's menu (the button after "+", "Other shells"), same list |
| 5 | `terminal.restore` | `terminal.toggleRestore` ("Terminal: Toggle Restore Tabs on Start") | on / off | check row "Restore Tabs on Start" in that menu |
| 5 | `terminal.defaultMode` | `terminal.toggleDefaultMode` ("Terminal: New Terminals Start Locked or Linked") | locked / linked | check row "New Terminals Start Linked" in that menu |
| 6 | `update.check` | `update.toggleCheck` ("Update: Toggle Automatic Check") | on / off | "Check Automatically" in the update pill's flyout and in the top row menu's "Update Settings" |
| 6 | `update.autoInstall` | `update.toggleAutoInstall` ("Update: Toggle Automatic Install") | on / off | "Install Automatically" in the same two places |
| 6 | `update.channel` | `update.chooseChannel` ("Update: Channel", pick list stable / preview) | the channel | "Stable Channel" and "Preview Channel" in the same two places |
| 7 | `files.editor` | `preferences.chooseEditor` ("Preferences: Choose Editor", pick list: Windows' default, the `programs` entries, "Choose…" = file dialog for an .exe) | the editor's label | "Editor" submenu of the top row's menu, same rows, current one checked |
| 7 | `logging.level` | `diagnostics.chooseLogLevel` ("Diagnostics: Log Level", pick list of the five levels) | the level | "Log Level" submenu of the top row's menu |

`update.check` (check now) keeps its id and name. The dock's chevron button is always
shown now (it was hidden with one profile), since it holds settings.

The code is in `ui/CabinetOS.Core` (new `TerminalDockMenu`, `UpdateSettingsMenu`,
`PreferenceChoices.cs` with `LogLevels`, `EditorChoices` and `EditorSetting`; additions to
`UiSettings`, `SettingStates` and `ShellMenu.MoreSettings`) and in the window's new partial
files `MainWindow.Preferences.cs`, `MainWindow.UpdateSettings.cs` and `MainWindow.Editor.cs`.
Small edits elsewhere: `TerminalHalfHeader`, `ToolDock` (the chevron menu), `PromptBox`
(`DescribeRows`, for the tests' log), `MainWindow.ThreeWays.cs` (the menu rows),
`MainWindow.xaml.cs` (a few small hooks: the `settings-state` and `settings-do` steps,
`until:prompt`, and calls at each configuration read). Rust: eight seeds in
`registry.rs` (the seed array is 133 long; two tests that counted the commands moved
from 125 to 133, and the search test's terminal group grew by three).

Documents: `docs/config.md` (three-ways notes on the eight rows), `docs/keybindings.md`
(eight rows, three notes), `docs/ui.md` (the "Settings reachable three ways" table has
eight more rows, bullets for the dock menu, the update settings, the editor and the log
level, the snapshot steps), `CHANGELOG.md` (three lines under Unreleased, Added), the
audit table of the skill (all eight rows "fine since this unit (was gap N)").

## Tests

| What | Where |
|---|---|
| a registry test per gap (title, category, no key, target `ui`, no `when`, keymap compiles) | `cabinetos-commands` registry tests |
| rows, state words, reading from the file, the menus' rows and arguments, an older core, the editor's tokens and labels, equality of two readings | `ThreeWaysTests` (Core, 14 new) |
| a window test per setting: the command, the control (the menu or the dock, or the pill's flyout through its automation peer) and an edit of the file each change it, and the other ways show it; also a new terminal starts with the default shell and mode | `ThreeWaysEndToEndTests`, 8 new (3 + 3 + 2) |

The window tests run only on the laptop (`CABINETOS_UI_E2E=1`). New snapshot steps they use:
`settings-state:<label>` (log "settings state"), `settings-do:update-flyout|<row>` and
`settings-do:editor-file|<path>` (the answer of the next file dialog, which a test cannot
drive), `until:prompt`, and `until:setting:` names for the new settings.

## Checks (exit codes read one by one)

| Command | Result |
|---|---|
| `cargo build --workspace` | exit 0 |
| `cargo test --workspace` | exit 0; 924 passed, 0 failed, 6 ignored |
| `cargo clippy --workspace --all-targets -- -D warnings` | exit 0 |
| `cargo fmt --all -- --check` | exit 0 |
| `cargo deny check` | exit 0 |
| `dotnet build CabinetOS.sln -warnaserror` (Debug) | 0 warnings, 0 errors |
| `dotnet build CabinetOS.sln -warnaserror -c Release` | 0 warnings, 0 errors |
| `dotnet test --solution CabinetOS.sln --no-build` (fast tests) | 1449 tests: 1369 passed, 80 skipped (they open a window), 0 failed |
| laptop, `-Filter "FullyQualifiedName~ThreeWaysEndToEnd"` at `2999b28` | 12 of 12 passed (the 4 of gaps 1 to 4 and the 8 new), exit 0, "ran commit 2999b28404f80976d01834365097b6f72f9817e2" |
| laptop, whole suite at `d11c0fb` (the last code commit) | 1449 of 1449 passed in 6 min 32 s, exit 0, "ran commit d11c0fb7a651476b3d2b60e788be052683dda3e2" |

A first laptop run at `d314960` (gaps 5 and 6): the three gap 5 tests passed; the three gap 6
tests failed in my test code, not in the product: a submenu's text in the log marks only the
checked row with `[x]` (`FileContextMenu.OpenSubmenu`), and I had expected `[ ]` after the
others. Fixed in the tests (`2999b28`). A second run at `2999b28` failed to build on the laptop
because an orphaned `CabinetOS.exe` (pid 21796, started 20:43 by an earlier run of the
Quick View session, its parent process gone, no lock held) kept `CabinetOS.Core.dll` locked; I
stopped that one process by its id (the project's rule for dead windows there) and the run
after it passed.

The first whole-suite run at `2999b28` had 2 failures. One was mine: `ShellEndToEndTests`
`The_menus_come_from_the_registry...` lists the hamburger's rows and did not yet know the Editor, Update
Settings and Log Level rows; its expectation is fixed (`d11c0fb`). The other, `ContextMenuEndToEndTests+Alone`
`Opening_the_menu_over_100000_selected_rows_adds_no_slow_frame`, saw one slow frame (a 24 ms garbage collection and
a 40 ms frame while a right-click menu opened); it concerns the file context menu, which this unit does not touch,
and it passed in the whole-suite run after the fix.

## Decided without the creator

One line each: what, because, how to undo.

- **"The programs the Open with picker knows" is the user's `programs` list.** The toolbar's
  "Open with…" button is hidden and has no program list of its own; Windows' own Open with is the
  shell menu's. The `programs` entries (name, title, command, args) are what the window and the
  palette start programs from, so they are the editor's choices, besides "Windows' default"
  (`null`) and "Choose…". Undo: change `EditorChoices.Choices`.
- **A program becomes the editor with its arguments minus the tokens.** `files.editor` adds the
  file's path itself, so an argument with `{path}`, `{selection}` or `{cwd}` is left out and `--wait`
  stays (`EditorProgram.EditorArgs`). An editor set by hand that matches no program gets a row of its
  own, checked, named by its file name. Undo: write the program's args whole.
- **The update pill is not the only home of the update settings.** The pill is shown only while an
  update downloads or waits, so a flyout alone would hide them when nobody looks. The flyout is
  opened by a right-click on the pill (its left click stays "Restart to Update"), and the top row
  menu also has an "Update Settings" submenu, drawn from the same list (`UpdateSettingsMenu`).
  Undo: remove `SetUpUpdateSettings` or the `MoreSettings` row.
- **The default shell is a row that opens the pick list, not a submenu of the chevron menu.** A
  flyout's submenu items are not in the visual tree until it is opened, so the window tests could not
  press them; the same list as the palette's is also one less thing to keep in step.
- **"New Terminals Start Locked or Linked" is one toggle with the state words locked and linked** (the
  handout said "one toggle command with the state word"); the check row in the dock reads "New
  Terminals Start Linked". The per-session switch is still `terminal.setMode`.
- **The menus' submenus carry the values.** The top row menu's Editor, Update Settings and Log Level
  rows are submenus like Layout, in that order after the preference rows; their rows run the same
  commands with arguments, so there is no second code path that writes a key. The new submenu rows use
  the same parent icon as Layout (cosmetic; one icon per row is a small change in `MainWindow.ThreeWays.cs`).
- **The pick lists' commands accept their value as an argument.** Undo: remove the argument branches.
- **The log level's note about `CABINETOS_LOG`** (it wins over the file) is in `docs/config.md` and
  `docs/ui.md` only; the window cannot know the core's environment, so it does not say it in a notice.
- **Stopped one orphaned `CabinetOS.exe` on the laptop** (above), by its id, after reading that no
  lock was held and its parent was gone. Nothing was killed on this PC.

## Not done

- Gap 8 (the Settings page and the advanced keys: `terminal.profiles`, `marketplace.*`,
  `update.source`, `update.allowInsecure`). The skill's audit says so.
- No window opened on this PC and the live check (real keys and mouse) was not run: the handout asked
  for the window tests on the laptop only. The file dialog of "Choose…" has never been opened by a
  person in this unit (a test answers it through `settings-do:editor-file`); the code is
  `FileOpenPicker` with the window's handle (`PickProgramFileAsync`), the usual way for an unpackaged
  WinUI 3 app, so a live check of it is worth one look.
- The pill's flyout is tested through its row's automation peer, not by a right-click, because the pill
  exists only during an update; the right-click path is XAML's `ContextFlyout`.
- The desk card and `PLAN.md` are not changed.

## Noticed, out of scope

- The same few words ("Open with…") name a hidden toolbar button, Windows' own menu entry and, in the
  handout, a picker; a note in `docs/ui.md` would stop the next reader from looking for a picker.
- The laptop clone is shared by the sessions; a window process that outlives its test run locks the next
  build there (this time `CabinetOS.exe` pid 21796). The test wrapper could stop a `CabinetOS.exe` whose
  parent is gone before it builds.
