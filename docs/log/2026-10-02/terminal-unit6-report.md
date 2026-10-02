# Terminal unit 6: the setup file and updates that install themselves (2026-10-02, late morning to afternoon)

Context: unit 6 of six of the Integrated Terminal Subsystem Sprint (Phase 21
in [PLAN.md](../../PLAN.md)), "the installer and the updates, tested in the
VM". A release now has a setup file for the first install, and an update
puts itself in place in the background and only asks for a restart, in one
line of the status bar. Written by the Opus coder agent in the worktree
branch `worktree-agent-ad007f05e563d93ab`, on the main PC, on 2026-10-02,
while the creator was away ("proceed, test and merge"). The decision record
is [ADR 0018](../../decisions/0018-setup-file-and-silent-updates.md). The
window runs on this PC ran behind the creator's consent file
(`wait-for-pc.ps1` said "allowed"); the VM "CabinetOS-LiveCheck" ran the
install chain; the laptop was not used. Main (unit 5, 791b52d) was merged
into the branch before the last checks.

## Summary

| Item of the handout | Result |
|---|---|
| 1. The setup file | Done. `build/setup.iss` (Inno Setup 6.7), compiled by step 8 of `build/release.ps1` (`-NoSetup` skips it; without Inno Setup a warning names the winget command) into `dist\CabinetOS-<version>-win-x64-setup.exe` and its `.sha256`. Per user, no elevation, `%LOCALAPPDATA%\Programs\CabinetOS`; Start Menu shortcut, unchecked desktop shortcut, "Start CabinetOS" checked; the version and the runtimes from `release.json`; publisher CabinetOS; `CabinetOS.ico` made from the design's icon cuts; Inno's own uninstaller removes the whole folder with what the updater added and nothing under `%APPDATA%\CabinetOS` or `%LOCALAPPDATA%\CabinetOS`; the three prerequisites checked in `[Code]`, a missing one stops the setup with its winget command; silent with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=<file>`. The zip and the two scripts stay. The updater keeps `CabinetOS_is1` current (`DisplayVersion`, `EstimatedSize`, `MajorVersion`, `MinorVersion`), with a test |
| 2. Updates with no wizard | Done. `update.autoInstall` (default `true`): the core swaps a checked download in, in the same step; the window shows "CabinetOS <version> is installed; restart to use it" with Restart now and Later in the status bar; the text opens the notes; Later keeps the session; a failed swap says so in the same place and the next daily check swaps again. `false` keeps ADR 0014's dialog. The daily check, the palette commands and the CLI stay. No protocol change (unit 5's 19 stays) |
| 3. The VM chain | Done: `ui/livecheck/vm-install-check.ps1` with its guest half `vm-install-guest.ps1`. **All five steps passed in one run** (`DONE-install-2026-10-02-1425.md`): the setup installs silently, the live check against the installed program 256 True and 0 False, the next version installs itself and the notice's Restart now runs it, the uninstaller removes everything and keeps the data. Eight runs in all; what the first seven found is below |
| Checks | Below |
| Docs | ADR 0018 and its row, ADR 0014's status line, release.md, config.md, ipc.md, ui.md, CHANGELOG.md, PLAN.md, this report |

## How it works

1. `release.ps1` builds the release folder as before, now with `CabinetOS.ico`, and compiles `build\setup.iss` around it with the facts of `release.json` as `/D` defines.
2. The setup checks Windows, the .NET 10 runtime (the registry and the usual folders), the Windows App Runtime (it asks Windows PowerShell's `Get-AppxPackage`, as `install.ps1` does) and WebView2 (EdgeUpdate's registry) before its first page, and stops with the winget command for a missing one.
3. It copies the folder into `%LOCALAPPDATA%\Programs\CabinetOS` (no elevation), makes the shortcuts, and writes Inno's Apps entry `CabinetOS_is1` and its uninstaller `unins000.exe`.
4. The installed CabinetOS's core runs the daily check as before (ADR 0014): `latest.json`, then the zip, checked by SHA-256 and unpacked into the update folder.
5. With `update.autoInstall` (the default) the same step swaps it in: the old files go to `previous\` by renames, the new ones are copied in, and `unins000.*`, `previous\` and `previous-old\` stay where they are. The states go `downloading`, `applying`, `ready`; nobody sees `downloaded`.
6. The core then writes the new `DisplayVersion`, `EstimatedSize`, `MajorVersion` and `MinorVersion` into every Apps entry that names this folder: `CabinetOS` (install.ps1's) and `CabinetOS_is1` (the setup's).
7. The window sees `ready` with a version that is not its own and shows the status-bar notice; its buttons never take the keyboard; the palette's "Update: Restart to Update" is the keyboard's way.
8. Restart now runs `update.apply`, which finds the swap done and restarts into the new `CabinetOS.exe`; Later closes the notice and leaves the pill "Update ready · Restart" and the menu's dot.
9. A swap that fails is rolled back by the existing code; the window shows "could not be installed" in the error colour with the reason as the tooltip; the download stays, and the next daily check swaps it.
10. Inno's uninstaller refuses while a CabinetOS program's file cannot be opened for writing (it runs), empties the folder but its own files just before its own removal, removes the folder also when it was there before the setup, and leaves the user's data.

## Commits

| Commit | Subject |
|---|---|
| 914c385 | build: a setup file around the release folder, compiled by release.ps1 |
| c87bac0 | update: swap a checked download in by itself, and keep the setup's entry and uninstaller |
| 2c829b5 | window: one quiet status-bar notice instead of the dialog when an update installed itself |
| eb9a336 | test: cabinetos-cli update with and without update.autoInstall |
| 89a3271 | docs: the setup and silent-update comments name ADR 0018, not 0015 |
| 5d878d6 | tests: end-to-end, an update that installs itself shows the status-bar notice; one that fails says so |
| c185a7a | Merge branch 'main' into worktree-agent-ad007f05e563d93ab (unit 5; no conflict; git's own message) |
| 1dc16ca | live check: the install check in the VM, and the setup's uninstaller fixed by what it found |
| de733cb | docs: the setup file, updates that install themselves, and the install check (ADR 0018) |

The report, its row and the plan's line are the last commit. The merge
commit kept git's default message (the merge ran without conflicts and
committed at once); I did not amend it, since that would rewrite history.
5d878d6 carries the trailer `Claude Opus 5.5` (the attribution reminder of
the session after a context reset); every other commit carries `Claude
Opus 5`, as the handout asked.

## Changes per file

Build:

- `.gitattributes`: `*.iss` is CRLF, as Inno's own editor writes it.
- `build/setup.iss` (new): the setup (above). `[UninstallDelete]` `dirifempty` for `{app}`. `[Code]`: the version compare, the three prerequisite checks, `/SKIPPREREQUISITECHECK`, the refusal of a folder that holds `.cabinetos-install.json` (exit code 7), `ProgramsInUse` (the five programs in the folder, `previous\` and `previous-old\`, opened for writing) for the uninstaller's refusal, and the uninstaller's emptying of the folder at `usUninstall`.
- `build/release.ps1`: `-NoSetup`; `Write-IconFile` (an ICO of the PNG cuts 16, 24, 32, 48 and 256 px from `docs\design\icons`); `Find-InnoCompiler` (the PATH, then Inno's per-user and all-users folders); step 8 compiles the setup and writes its hash; the summary names it.

Core (Rust):

- `cabinetos-fs/src/registry.rs`: `read_user_number` (a DWORD from the user's hive), with a test.
- `cabinetos-update/src/apps.rs`: `SETUP_APPS_KEY` (`...\Uninstall\CabinetOS_is1`), `apps_keys()` (both keys, or the `;`-separated test keys of `CABINETOS_UPDATE_APPS_KEY`), `refresh` also writes `MajorVersion` and `MinorVersion` when the entry has them.
- `cabinetos-update/src/version.rs`: `major()`, `minor()`.
- `cabinetos-update/src/swap.rs`: `stays_in_place` (`previous`, `previous-old`, `unins000.exe`, `.dat`, `.msg`) for the swap and the rollback.
- `cabinetos-update/src/lib.rs`: `Settings.auto_install`; `download` swaps at once with it (also for a download that was staged already); `run_daily` swaps a staged `downloaded`; `apply` and the automatic swap share `swap_staged`; `Paths.apps_keys`.
- `cabinetos-config/src/model.rs`: `update.autoInstall` (default `true`), with tests; `sdk/config/cabinetos.schema.json` regenerated (and again after the merge, with no change).
- `cabinetos-core/src/update.rs`: passes the setting and the Apps keys.
- `cabinetos-cli-args/src/lib.rs`: `update download`'s help says it installs too with the setting.
- Tests: `cabinetos-update/tests/update.rs` (the download swapped at once; a swap Windows refuses keeps the old version and the next daily check swaps; a setup install keeps `unins000.*` and its entry follows), `cabinetos-core/tests/update.rs` (the daily check installs by itself and both entries follow; the old test runs with the setting off), `cabinetos-cli/tests/update.rs` (a download put in place at once; the step-by-step test with the setting off).

Window (C#):

- `ui/CabinetOS.Core/Updates/UpdateModel.cs`: `UpdateNoticeView`; `UpdateModel.AutoInstall`, `Notice`, `CloseNotice()`; no dialog by itself with the setting on; the pill hides while the notice shows; `UpdateText.AutoInstallFrom` and `UpdateText.Notice`.
- `ui/CabinetOS/MainWindow.xaml`: the notice in the status bar (`UpdateNotice`: the text as a button, Restart now, Later), none of them a tab stop or a focus taker.
- `ui/CabinetOS/MainWindow.Update.cs`: the three handlers, `ApplyUpdateConfig`, the log lines "update notice shown" and "update notice closed"; a check that downloads accepts `ready` after it. `MainWindow.xaml.cs`: reads the setting with the configuration. `MainWindow.Shell.cs`: `update_notice` in the shell state.
- Tests: `UpdateTests.cs` (four new), `ShellEndToEndTests.cs` (two new end-to-end tests; the dialog test runs with the setting off).

VM check:

- `ui/livecheck/vm-install-check.ps1` (new): the host half. Step 1 (two real builds, the version files raised for one and written back byte for byte, the programs rebuilt and their version checked), the staged feed, the VM started or restarted and left to settle (`-SettleMinutes`, 30), the guest started hidden through X:, a wait that reads only the guest's files, `DONE-install.md`. `-SkipBuild`, `-SkipLiveCheck`, `-LiveCheckMinutes`, `-Restart`, `-SyncVersion`, `-WaitMinutes`.
- `ui/livecheck/vm-install-guest.ps1` (new): the guest half, steps 2 to 5 inside the VM, every outside program with a time limit (`Run-Limited`), `%TEMP%` in its long form for the live check.

Docs: ADR 0018 (new) and its row in the index, ADR 0014's status line, release.md ("The setup file", the outputs, the steps, the swap), config.md (`update.autoInstall`), ipc.md (Updates), ui.md (Updates, the notice, the install check in the VM), CHANGELOG.md, PLAN.md, this report and its row.

## Tests and runs

After the merge of main (unit 5) into the branch, on the final code:

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 886 passed, 0 failed, 6 ignored (874 before the merge) |
| `cargo clippy --workspace --all-targets -- -D warnings` | clean |
| `cargo fmt --all -- --check` | clean |
| `cargo deny check` | advisories ok, bans ok, licenses ok, sources ok |
| `cargo build --release --workspace` | passed |
| the schemas after the merge (`CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol` and `-p cabinetos-config`) | no change |
| `dotnet build CabinetOS.sln -warnaserror` (Debug and Release) | 0 warnings, 0 errors |
| the fast window tests (no window) | 1339 tests: 1279 passed, 0 failed, 60 end-to-end skipped (1313 before the merge) |
| the full window suite with the end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core, behind `wait-for-pc.ps1`) | first run 1338 of 1339 (3 min 56 s): `ContextMenuEndToEndTests.The_menu_opens_with_its_corner_at_the_point_and_flips_before_the_windows_edge`, "Expected: 6, Actual: 5", one of the known context-menu timing tests; its class alone 8 of 8; second full run **1339 of 1339** (3 min 56 s). Before the merge: 1313 of 1313 (3 min 44 s) |
| `build\check-scripts.ps1` | every script parses in Windows PowerShell 5.1 and PowerShell 7.6.6 |
| the live check on this PC (fresh Release build, Windows PowerShell, no `-MinimizeOthers`) | after the merge, with the VM's state saved (off): **248 True, 0 False, exit code 0, 8 min 42 s** (`run-unit6-d.txt`), the scroll goal met (300 frames, none over 20 ms, worst 17.4 ms); section 14 says WAITING (the worktree has no built Agent extension). Before the merge, with the VM running, three runs gave 244 True and 1 False each (`run-unit6-a` to `-c`, about 7 min 52 s, the scroll goal met each time): section 18's clipboard line, explained under "Seen on the way" |
| the VM install check | all five steps passed in one run, `DONE-install-2026-10-02-1425.md` (below) |

The unit's own tests: the update crate's three new tests and the core's
and the CLI's new tests (in the 886); the window's four model tests (in the
fast run); the three update end-to-end tests (the dialog with the setting
off, the notice after a swap by itself with Later, a swap Windows refuses),
all passing in each full run.

## The VM chain

The final run, 2026-10-02-1425, `-SkipBuild` on releases built from the
merged branch (`_io\live-check\DONE-install-2026-10-02-1425.md`):

| Step | Result |
|---|---|
| 1. The two releases on this PC | passed: 0.1.0 with its setup and 0.1.1, each folder's `cabinetos-cli --version` checked; the feed staged in `X:\_io\update-test\stable` |
| 2. The setup, silently | passed in 8 s: exit code 0, 72 files, 256.4 MB, the Start Menu shortcut, no desktop shortcut, `CabinetOS_is1` with DisplayName and Publisher CabinetOS, DisplayVersion 0.1.0, MajorVersion 0, MinorVersion 1, the icon, the quiet uninstall string |
| 3. The live check against the installed CabinetOS.exe | passed: 256 True, 0 False in 9 min (one line "not measured in a VM") |
| 4. The update by itself, the notice, the restart | passed: the window saw `unchecked > checking > available > downloading > applying > ready`; "CabinetOS 0.1.1 is installed; restart to use it"; the core's lines for the swap and the Apps entry; 0.1.1 in place and 0.1.0 in `previous\`, `unins000.exe` in place; the entry at 0.1.1; Restart now through UI Automation; the new core confirmed the swap; `cabinetos-cli update --json`: current 0.1.1, up_to_date, previous 0.1.0 |
| 5. The uninstall | passed in 3 s: the folder (with `previous\`), the entry and the shortcut gone, the data in `%LOCALAPPDATA%\CabinetOS` kept |

The runs before it, and what each found:

| Run | Result | Found, and the fix |
|---|---|---|
| 1 to 3 | stopped before the VM's part | the script's own root (`.claude\worktrees\_io` in a worktree) and Windows PowerShell's treatment of cargo's stderr; fixed in the script |
| 4 (11:17) | steps 1, 2 and 4 passed | step 3: the guest's own console window stood in front, so the live check stopped; the guest now starts hidden and minimizes. Step 5: the cleanup of what the updater added ran after Inno's removal and left an empty folder; it now runs at `usUninstall` |
| 5 (11:26) | steps 1 and 2 passed | step 3: no line after a real click in the compact section for 20 minutes; the check got a time limit (`-LiveCheckMinutes`), with a screenshot and the logs kept |
| 6 (12:05, after a VM restart) | step 2 passed | right after the restart an uninstall took 22 minutes, a window 11; guest control then stuck. The host now waits for the VM to settle after a start (29 minutes in run 7), the guest's waits have limits, and the uninstaller no longer asks WMI |
| 7 (13:13) | steps 1 and 2 passed | step 3: section 14's Agent preview never showed, because the VM's `%TEMP%` is `C:\Users\CABINE~1\...` and the core compares the extension's paths with `%USERPROFILE%` as text; the guest now gives `%TEMP%` in its long form. Step 4: the installed 0.1.0 reported 0.1.1, because cargo left the next version's programs in `target\release`; the builds now rebuild the three programs and check their version. Step 5: Inno removes only a folder it made, and the folder was there before the setup (left by run 4); `[UninstallDelete]` `dirifempty` |
| 8 (14:25) | **all five passed** | |

## Decided without you

Each line: what, because, how to undo.

- **The decision record is ADR 0018, not 0015.** 0015 to 0017 were taken by the time this unit wrote it. Undo: none.
- **No `.cabinetos-install.json` from the setup.** Inno's uninstaller is the install's record, and the updater lives without one already. So `install.ps1` refuses a setup's folder, and the setup refuses an `install.ps1` folder (exit code 7, naming its `uninstall.ps1`). Undo: write the record from `[Code]` and keep it in `carry_record` in `swap.rs`.
- **Inno's uninstaller stays in place through a swap and a rollback** (`stays_in_place` in `swap.rs`). Because it belongs to the install, not to a version; moved into `previous\`, Settings > Apps would point at nothing. Undo: remove the `unins` names from `stays_in_place`.
- **The updater keeps both Apps entries current, with `MajorVersion` and `MinorVersion`.** Each only when it names this install folder. Because Inno writes all four values and the handout asked for its entry to follow. Undo: `apps_keys` and `refresh` in `apps.rs`.
- **The AppId is `CabinetOS`, so the entry is `CabinetOS_is1`; the display name has no version.** Because the updater changes `DisplayVersion` and a name with the old version in it would be wrong. Undo: `AppId` in `setup.iss` and `SETUP_APPS_KEY` in `apps.rs`, together.
- **The uninstaller empties the folder itself, only one named `CabinetOS`, just before Inno's removal, and removes it also when it was there before the setup.** Run after Inno's removal, the cleanup left an empty folder; and Inno removes only a folder it made. Undo: `CurUninstallStepChanged` and `[UninstallDelete]` in `setup.iss`.
- **The uninstaller's "CabinetOS runs" check opens the five programs' files for writing instead of asking WMI.** Windows refuses that for a running program (checked on this PC with a running and a copied program); WMI waits for a service, and right after a restart the VM's uninstall took 22 minutes (a second once settled). Undo: `ProgramsInUse` in `setup.iss` back to a WMI query.
- **The Windows App Runtime check asks Windows PowerShell's `Get-AppxPackage`; when PowerShell gives no answer, the setup logs it and goes on.** Because a framework package cannot be read from the registry, and a broken PowerShell should not block an install that would work. `/SKIPPREREQUISITECHECK` skips all three checks. Undo: `AppRuntimeState` and `InitializeSetup` in `setup.iss`.
- **The setup leaves out `install.ps1`, as the updater's swap does.** It runs from the unpacked zip only. `uninstall.ps1` is in the folder, as after a swap, and refuses a setup's folder harmlessly. Undo: the `Excludes` in `setup.iss`.
- **The notice is in the status bar, not an info bar over the panes.** The status bar holds the update pill already, and nothing then covers the files. Undo: `UpdateNotice` in `MainWindow.xaml` and `UpdateText.Notice`.
- **The notice's text is a button that opens the release notes** (one click away, as asked). Undo: `UpdateNoticeNotes` in `MainWindow.xaml`.
- **Later sends no snooze.** The snooze is the dialog's rule; a version already in place has nothing to put off. Undo: send `update_snooze` in `CloseUpdateNotice`.
- **`cabinetos-cli update download` installs too with the setting.** Because the setting is the core's rule for every download. Undo: pass `auto_install: false` for that request in the core.
- **No protocol change.** `update_state` already had `applying`, `ready` and `failed`, and the window reads `update.autoInstall` from the configuration the core sends. So unit 5's protocol 19 stays. Undo: none.
- **The VM check builds a real next version** (the version raised in `core/Cargo.toml`, `core/Cargo.lock` and `ui/Directory.Build.props` for one build, written back byte for byte, refused when any of them has uncommitted changes). A copy of the zip with a new `release.json` would hold programs that report the old version, so the restart could not prove the new one runs. Undo: stage a copy of the zip in `vm-install-check.ps1`.
- **Before each build the check gives the three programs' `main.rs` a newer time and afterwards checks `cabinetos-cli --version`.** cargo keeps each version's build apart but copies a program into `target\release` only when it rebuilds it, so one release took the other version's programs. A newer time changes no file's content. Undo: `Invoke-Release` and `Test-BuiltVersion` in `vm-install-check.ps1`.
- **A VM the check starts or restarts settles first, up to 30 minutes, until two CPU readings under 20 %.** Right after a restart an uninstall took 22 minutes and a window 11. Undo: `-SettleMinutes 0`.
- **The guest gives the live check `%TEMP%` in its long form.** The VM user's short 8.3 form made section 14 fail (see "Seen on the way"). Undo: two lines in `vm-install-guest.ps1`.
- **The release script skips the setup with a warning when Inno Setup is missing.** The zip and `install.ps1` are a complete release without it. Undo: throw in step 8 of `release.ps1`.
- **`CabinetOS.ico` ships in the release folder,** so the uninstaller's icon in Settings > Apps and the shortcuts have a file to point at. Undo: `Write-IconFile` in `release.ps1` and the three icon lines of `setup.iss`.
- **The guest half of the VM check travels through `X:\_io\install-check` with a JSON file of its arguments,** and the guest command starts it hidden and returns at once. Because guest commands over about 1,200 characters hang VirtualBox's guest control. The host reads only the progress and result files while the VM works. Undo: none needed.
- **The VM check presses Restart now through UI Automation's Invoke,** in a process of its own with a time limit, not a mouse click. Undo: none needed.
- **For the merge, the uncommitted work went into a tagged stash and came back with `git stash apply` by its hash;** the one conflict (CHANGELOG.md, unit 5's three lines and this unit's two) kept both sides; the stash entry was dropped after the files were back. Undo: none.

## Needs you

- **Publishing.** A GitHub Release with the zip, the setup file and their `.sha256` files, and `latest.json` on the marketplace site, stay your steps ([release.md](../../release.md), "Publish").
- **Signing.** The setup file is unsigned, as the programs are; SmartScreen will warn until a certificate exists ([release.md](../../release.md), "Sign").
- **The setup installs no prerequisites.** It stops and names the winget command. Say if a later unit should download them.

## Seen on the way, not changed

- **Section 18's clipboard line on this PC.** "18: the clipboard holds the row's file (): False": after Windows' own Copy, run by the core from Windows' menu, the clipboard holds no file. It answered False in all of this unit's runs on this PC (`run-unit6-a` to `-c`, 244 True and 1 False each) and in unit 5's `run-unit5-a.txt` of 11:30; every run on this PC up to main's `run-2026-10-02-0951.txt` (10:00) said True, the laptop said True at 11:41, and the VM said True in run 8 (2026-10-02-1425). Ruled out: this branch (it touches no menu code, and unit 5's branch shows it too); the VM's shared clipboard switched off (`controlvm clipboard mode disabled`, run c: still False; switched back to bidirectional); Windows' Copy verb itself (a PowerShell test on this PC put the file on the clipboard, and it stayed). With the VM's state saved, so no VirtualBox process ran, the same line said True (`run-unit6-d.txt`, 14:38, 248 True and 0 False). So the running VM is the cause, even with its shared clipboard set to disabled while it runs; the VM stays saved after this unit (`vm-livecheck.ps1` and `vm-install-check.ps1` start a saved VM). A fix could switch the VM's clipboard off in its settings while it is off (`VBoxManage modifyvm CabinetOS-LiveCheck --clipboard-mode disabled`), which nothing in this unit needs; the creator may want it for working in the VM.
- **The core compares a plugin's paths with its roots as text** (`cabinetos-plugins`, `lib.rs` and `watch.rs`), so a path in the short 8.3 form (`C:\Users\CABINE~1\...`) is outside `%USERPROFILE%` (`C:\Users\cabinetos`) for the Agent extension. Section 14 failed in the VM for that reason (runs of 2026-10-01 15:05 and 2026-10-02 13:13) and passed once `%TEMP%` was long. A user whose `%TEMP%` or a folder path is short would meet the same refusal. A fix would make the paths long (`GetLongPathNameW`) before the comparison.
- **`.claude\worktrees\_io` exists.** `vm-livecheck.ps1`, the laptop scripts and `run-livecheck.ps1` take the `_io` folder as the repository's parent, which for a worktree is `.claude\worktrees\`; other sessions' laptop outputs are there (`script-runs`, `test-runs`), and my first VM runs left empty folders (`install-check`, `live-check`, `update-test\stable`, `vm`). `vm-install-check.ps1` looks for the folder that holds `_io\vm\vm-user.txt` instead. Left for the planning session, with my early logs `unit6-*.log` in `.claude\worktrees\`.
- **A VM right after a restart is slow for about half an hour** (22 minutes for an uninstall that takes a second later, 11 for a window to show). `vm-livecheck.ps1 -Restart` meets the same; it may want the same settling wait.
- **`CabinetOS.exe` has no icon of its own** (the setup uses `CabinetOS.ico` for the shortcuts and the Apps entry).
