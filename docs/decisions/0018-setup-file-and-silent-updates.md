# ADR 0018: A setup file for the first install, and updates that install themselves and only ask for a restart

- Status: accepted; its decision 2 ("The setup downloads nothing") and its
  consequence "The setup does not install the prerequisites" are replaced
  by [ADR 0019](0019-setup-installs-prerequisites-and-is-the-winget-package.md)
- Date: 2026-10-02
- Decided by: the creator, on the desk card "Integrated Terminal Subsystem
  Sprint" (Phase 21, unit 6: "an installation file (a setup .exe for the
  first install) and updates through the built-in updater of ADR 0014 with
  no wizard"; "the installer is Inno Setup around the release folder,
  updates from the GitHub releases once the repository is public"; "tested
  in the VM"); the shape below by the planning session's handout; the
  points marked as defaults by the implementing session while the creator
  was away ([PLAN.md](../PLAN.md), Phase 21).
  It amends [ADR 0014](0014-in-app-updates.md) (the dialog before the swap)
  and adds one asset to [ADR 0009](0009-packaging.md). The handout named
  this record 0015; 0015 to 0017 were taken, so it is 0018.

## Context

A release is a zip with `install.ps1` (ADR 0009). A user has to unpack it,
start a script with `-ExecutionPolicy Bypass`, and choose switches. The
creator asked for one setup file to double-click for the first install,
and for updates after it without any wizard: the app says a newer version
exists, downloads and installs it in the background with the status bar's
progress pill, and asks only to restart.

ADR 0014's updater already does the hard part: the check, the download
checked by SHA-256, the staged unpack, the rename-first swap that puts
everything back on a failure, the rollback, the Apps entry and the state
file. What it did not do: swap without asking. After the download a dialog
with the release notes asked "Restart now / Later", and only Restart now
swapped.

What decides the shape:

- The updater may change only a release with `release.json` next to the
  core that the user may change without elevation (ADR 0014, decision 5):
  the per-user folder `%LOCALAPPDATA%\Programs\CabinetOS`.
- A running program's files can be renamed, so the swap works while
  CabinetOS runs; the running version goes on from `previous\` until the
  restart.
- Inno Setup writes its own uninstaller (`unins000.exe` and its log
  `unins000.dat`) into the install folder and its own Apps entry,
  `HKCU\...\Uninstall\<AppId>_is1`.

## Decision

1. **The setup file.** `build/setup.iss`, compiled by `build/release.ps1`
   (step 8, Inno Setup 6.7 or newer) around the release folder, into
   `dist\CabinetOS-<version>-win-x64-setup.exe` with its `.sha256`. Per
   user with no elevation (`PrivilegesRequired=lowest`), into
   `%LOCALAPPDATA%\Programs\CabinetOS`, the folder `install.ps1` uses, so
   the updater's rules hold. A Start Menu shortcut always, a desktop
   shortcut as an unchecked option, "Start CabinetOS" checked at the end.
   The version, the runtimes and the installer address come from the
   folder's `release.json`. The publisher is "CabinetOS"; the icon is
   `CabinetOS.ico`, made by the release script from the design's size cuts
   ([design/ICON_HANDOFF.md](../design/ICON_HANDOFF.md)) and shipped in
   the release folder.
2. **The prerequisites** (the .NET 10 runtime, the Windows App Runtime
   2.5.1 or newer, WebView2) are checked in the setup's `[Code]` the way
   `install.ps1` checks them; a missing one stops the setup with the winget
   command that installs it. The setup downloads nothing.
3. **Silent.** `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=<file>`
   installs with no window; `/SKIPPREREQUISITECHECK` installs even when a
   prerequisite looks missing.
4. **The uninstall is Inno's own**, listed in Settings > Apps as
   `CabinetOS_is1`. It removes the whole install folder, with what the
   updater adds later (`previous\`, `previous-old\`, the files a later
   version brought), and nothing under `%APPDATA%\CabinetOS` or
   `%LOCALAPPDATA%\CabinetOS` (the settings, the logs, the update state),
   as `uninstall.ps1` leaves them.
5. **The zip, `install.ps1` and `uninstall.ps1` stay** (ADR 0009). The
   setup file is one more release asset.
6. **Updates with no wizard.** A new setting `update.autoInstall` (default
   `true`): a download whose SHA-256 is right is swapped in at once, in the
   same step, so no client sees `downloaded` in between (the states go
   `downloading`, `applying`, `ready`). The window then says, in one quiet
   line of the status bar, "CabinetOS <version> is installed; restart to
   use it", with Restart now and Later; the text opens the release notes,
   and About keeps its dot and the notes. Later keeps the session running;
   the next start is the new version anyway, confirmed in the state file as
   before. A swap that fails puts everything back, says so in the same
   place, keeps the download, and the next daily check swaps again. With
   `update.autoInstall: false` the flow of ADR 0014 stays: the dialog
   before the swap.
7. **Nothing else changes.** The daily check and its snooze, the palette's
   "Update: Check for Updates" and "Roll Back to the Previous Version",
   `cabinetos-cli update ...`, and the source (`latest.json` on the
   marketplace site naming the zip on the GitHub Release) stay as ADR 0014
   has them. The protocol does not change.
8. **Tested in the VM.** `ui/livecheck/vm-install-check.ps1` builds the
   release with its setup and a real next patch version, installs the setup
   silently in the VM "CabinetOS-LiveCheck", runs the live check against
   the installed program, lets the installed program update itself from a
   staged `file:` feed, restarts it through the notice's Restart now, proves
   the new version runs, and uninstalls it.

Decided by the implementing session as defaults (say so to change any):

- **No install record from the setup.** The setup writes no
  `.cabinetos-install.json`: Inno's uninstaller is the install's record,
  and the updater already lives without one (an install by hand or by
  winget has none). Two consequences: `install.ps1` refuses a setup's folder
  ("holds files that are not a CabinetOS install"), and the setup refuses a
  folder that `install.ps1` installed, naming its `uninstall.ps1`; the two
  never mix in one folder. To undo: write the record from `[Code]` and let
  `carry_record` in `swap.rs` keep it.
- **Inno's uninstaller stays in place through a swap and a rollback**
  (`stays_in_place` in `swap.rs`: `unins000.exe`, `.dat`, `.msg`), because
  it belongs to the install, not to a version; moved into `previous\`, the
  Apps entry's Uninstall button would point at nothing. To undo: remove
  those names from `stays_in_place`.
- **The updater keeps both Apps entries current**, `CabinetOS`
  (install.ps1's) and `CabinetOS_is1` (the setup's), each only when it names
  this install folder, and also `MajorVersion` and `MinorVersion`, which
  Inno writes. `CABINETOS_UPDATE_APPS_KEY` may name several test keys,
  separated by `;`. To undo: `apps_keys` and `refresh` in `apps.rs`.
- **The AppId is the readable `CabinetOS`**, so the entry is
  `CabinetOS_is1`; `UninstallDisplayName` is `CabinetOS` without the
  version, because the updater changes `DisplayVersion` after a swap and a
  name with the old version in it would be wrong. To undo: `AppId` in
  `setup.iss` and `SETUP_APPS_KEY` in `apps.rs`, together (another AppId is
  another program to Windows).
- **The uninstaller empties the folder only when it is named `CabinetOS`**,
  never a drive's root. It removes everything but its own `unins*` files
  just before Inno's own removal starts, so Inno then removes an empty
  folder (run after Inno's removal, it left the empty folder behind in the
  VM). An install folder that was there before the setup goes too when it
  is empty (`[UninstallDelete]`, `dirifempty`): Inno removes only a folder
  it made itself, and in the VM an empty folder that one faulty uninstall
  left stayed through every later install and uninstall. It refuses to
  start while a CabinetOS program runs from the folder
  (also from `previous\` and `previous-old\`): Windows refuses to open a
  running program's file for writing, and the uninstaller tries that on
  the five programs. WMI, which can name the processes, was the first
  choice; but right after a restart the VM's uninstall took 22 minutes,
  and WMI was the one call in it that waits for a service (on a settled VM
  the same uninstall took a second), so the uninstall no longer asks it.
  To undo: `ProgramsInUse`, `CurUninstallStepChanged`,
  `InitializeUninstall` and `[UninstallDelete]` in `setup.iss`.
- **The notice is in the status bar, not an info bar over the panes**,
  because the status bar already holds the update pill and the notices,
  and nothing then covers the files. Its buttons never take the keyboard
  (the Zero-Hijack rule of Phase 21); "Update: Restart to Update" in the
  palette is the keyboard's way. While the notice shows, the pill hides
  (the notice holds Restart now); after Later, the pill "Update ready ·
  Restart" and the menu's dot stay. To undo: `UpdateNotice` in
  `MainWindow.xaml` and `UpdateText.Notice`.
- **Later sends no snooze.** The snooze is the dialog's rule (no dialog by
  itself for a day after Later); a version already in place has nothing to
  snooze. Later closes the notice for this run; the next start runs the new
  version. To undo: send `update_snooze` in `CloseUpdateNotice`.
- **`cabinetos-cli update download` installs too** with
  `update.autoInstall`, because the setting is the core's rule for every
  download, not the window's. `update apply` stays for a download made
  with the setting off. To undo: pass `auto_install: false` for the
  request in `connection.rs`.
- **The VM check builds a real next version.** The handout proposed a copy
  of the zip whose `release.json` names the next patch; but the programs
  in it would still report the old version, the updater would never confirm
  the swap, and `pong` could not prove the new version runs. The check
  raises the version in `core/Cargo.toml`, `core/Cargo.lock` and
  `ui/Directory.Build.props` for one `release.ps1 -NoSetup` build and
  writes the three files back byte for byte after it (it refuses to start
  when any of them has uncommitted changes). To undo: stage a copy of the
  zip in `vm-install-check.ps1` instead, and drop the restart's proof.
- **The release script skips the setup with a warning when Inno Setup is
  missing** (with the winget line), rather than failing the whole release,
  because the zip and `install.ps1` are a complete release without it. To
  undo: throw in step 8 of `release.ps1`.

## Consequences

- **A release has one more asset**, `CabinetOS-<version>-win-x64-setup.exe`
  and its hash, for the GitHub Release ([release.md](../release.md),
  "Publish"). Publishing stays the creator's step.
- **The build needs Inno Setup 6.7** on the machine that makes a release
  with a setup (`winget install --id JRSoftware.InnoSetup --exact --scope
  user`). GitHub's `windows-latest` image carries an Inno Setup; its
  version must be 6.7 or newer for `setup.iss`.
- **Nothing is signed yet.** The setup file meets SmartScreen as the
  programs do until a certificate exists ([release.md](../release.md),
  "Sign").
- **An update changes the files while the user works.** The running
  window, core and command line go on from `previous\`; a second window of
  the old version runs on until it is closed. This was true after Restart
  now already; now it happens without the click.
- **The setup does not install the prerequisites.** A later unit may add
  the downloads (the Windows App Runtime's installer, the .NET runtime) to
  the setup's `[Code]`.
- **The setup adds no PATH entry and no indexer service**, which
  `install.ps1 -AddToPath` and `-AllUsers -Indexer` do; the setup is the
  per-user install only.
