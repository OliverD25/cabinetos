# ADR 0019: The setup file installs a missing prerequisite itself, and the winget package is the setup file

- Status: accepted
- Date: 2026-10-02
- Decided by: the creator, in the chat of 2026-10-02 ("Make the setup file
  install the prerequisites itself (download and run the three installers
  when missing)"); the shape below by the planning session's handout; the
  points marked as defaults by the implementing session.
  It replaces decision 2 of [ADR 0018](0018-setup-file-and-silent-updates.md)
  ("The setup downloads nothing") and that record's consequence "The setup
  does not install the prerequisites", and the winget part of
  [ADR 0009](0009-packaging.md) (the zip as a portable winget package).

## Context

CabinetOS 0.1.0 was published on 2026-10-02 with a setup file that checks
its three prerequisites (the Windows App Runtime 2.5.1 or newer, the
WebView2 Runtime, the .NET 10 runtime) and, for a missing one, names the
winget command and stops (ADR 0018, decision 2).

Two things made that a problem:

- **The winget package could not be submitted.** It was the zip as a
  portable package (ADR 0009) with the Windows App Runtime 2.5.1 as a
  package dependency. winget's catalogue has only 2.3.1 of
  `Microsoft.WindowsAppRuntime.2` (checked 2026-09-28 and again on
  2026-10-02), and Microsoft adds new versions there about a month after
  the release. winget-pkgs' checks fail on a dependency the catalogue
  cannot meet.
- **A stranger's first install should just work.** On a PC without the
  Windows App Runtime 2.5 (most PCs: it is newer than what Windows and
  winget ship), a double-click on the setup ended in a message with
  commands to type, one of which winget could not even fulfil.

Microsoft publishes installers for all three under stable links that
always lead to the newest build:

| Prerequisite | Link | Silent switches | Installs |
|---|---|---|---|
| Windows App Runtime | `https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe` (the address `release.json` already carries) | `--quiet` | for the user, without administrator rights |
| WebView2 Runtime | `https://go.microsoft.com/fwlink/p/?LinkId=2124703` (the Evergreen Standalone Bootstrapper) | `/silent /install` | for the user when not elevated |
| .NET runtime | `https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe` | `/install /quiet /norestart` | for the whole PC: needs administrator rights |

Checked with `curl -sIL` on 2026-10-02: all three answer 200 after their
redirects. The Windows App Runtime's installer was 120 MB
(`WindowsAppRuntimeInstall-x64.exe`), the WebView2 bootstrapper 2 MB, the
.NET runtime 31 MB (`dotnet-runtime-10.0.12-win-x64.exe`).

The window is WinUI 3 on .NET 10. Its `CabinetOS.runtimeconfig.json` names
one framework, `Microsoft.NETCore.App`, which `release.json` records and the
setup's check looks for under `shared\Microsoft.NETCore.App`.
`Microsoft.WindowsDesktop.App` is the framework of WPF and Windows Forms,
which CabinetOS does not use. So the installer is the plain .NET runtime
(`dotnet-runtime-…`), 31 MB, not the desktop runtime (`windowsdesktop-runtime-…`,
60 MB).

## Decision

1. **The setup installs what is missing.** In `InitializeSetup`, before
   the wizard, the existing checks run as before. For each missing
   prerequisite, in the order Windows App Runtime, WebView2 Runtime, .NET
   runtime, the setup downloads Microsoft's installer with Inno Setup's
   `DownloadTemporaryFile` into its temporary folder, runs it silently,
   waits for it, deletes it, and runs the check again. A prerequisite still
   missing after that stops the setup with the message of ADR 0018 (the
   winget command, and for the Windows App Runtime Microsoft's installer
   address), plus one line with what the setup tried: the installer's exit
   code, the failed download, or why it did not try.
2. **The .NET runtime and administrator rights.** Its installer needs
   them. An interactive setup that is not elevated runs it through
   Windows' administrator prompt (UAC, `ShellExec` with the `runas` verb);
   an elevated setup runs it directly. A silent setup (`/SILENT` or
   `/VERYSILENT`) that is not elevated does not try: nobody is there to
   answer the prompt. It writes the reason to its log and stops with the
   winget command, as in decision 1.
3. **No pinned hashes.** The links are "latest" links: their bytes change
   with each runtime release, so a hash in the setup would break the
   download at Microsoft's next update. Instead, the log names every step:
   the URL, the file's size and SHA-256 (`GetSHA256OfFile`), and the
   installer's exit code. The trace shows afterwards exactly which file
   ran. The downloads come over HTTPS from Microsoft's own addresses, and
   Microsoft signs the installers.
4. **The exit code.** A setup stopped by a missing prerequisite exits with
   code 1, as `InitializeSetup` returning False gives, so winget and a
   script that calls the setup see the failure.
5. **The test switch.** `/PREREQTEST=1` downloads the three installers,
   logs their sizes and hashes, deletes them, and exits with code 1 without
   installing anything, CabinetOS included. It tests the download code on a
   PC that must stay as it is. `/SKIPPREREQUISITECHECK` stays: it checks
   nothing and downloads nothing.
6. **The winget package is the setup file**: `InstallerType: inno`,
   `Scope: user`, `UpgradeBehavior: install`, `ProductCode: CabinetOS_is1`
   (Inno's Apps entry, the one `SETUP_APPS_KEY` in `cabinetos-update`
   names), the setup's address on the GitHub Release and its SHA-256. It
   keeps `Microsoft.DotNet.Runtime.10` and `Microsoft.EdgeWebView2Runtime`
   as package dependencies: winget has both, and winget installs the .NET
   runtime with the administrator rights that a silent setup does not ask
   for (decision 2). The Windows App Runtime is no dependency any more: the
   setup installs it. No `Commands`: the setup adds no PATH entry.
7. **The manifests follow the setup.** `build/release.ps1` writes them as
   its last step, after the setup file exists, and `-WingetOnly` writes and
   validates them again from the files in `dist\` (the release folder's
   `release.json` for the version and the date, the setup's `.sha256` for
   the hash), for a setup file signed and hashed again after the build.
8. **A new version.** A setup file with other bytes under a published name
   would break the hashes people already checked, so this setup ships as
   0.1.1.

Decided by the implementing session as defaults (say so to change any):

- **An interactive setup asks once before it downloads.** When something
  is missing, a message box names the missing parts, says the setup now
  downloads and installs them from Microsoft and that this takes a few
  minutes, and, when the .NET runtime is among them, that Windows will ask
  for administrator rights. OK goes on; Cancel stops with the winget
  message. Without it a double-click showed nothing at all for the minutes
  the 120 MB download takes, and then an administrator prompt with no
  context. A silent setup does not ask. To undo: remove the message box
  from `InitializeSetup` in `setup.iss`.
- **`/SILENT` counts as silent for decision 2**, not only `/VERYSILENT`:
  both are "silent" to Inno (`WizardSilent`), and winget runs Inno setups
  with `/SILENT` in its default mode. To undo: test for `/VERYSILENT` in
  `InitializeSetup`.
- **The .NET installer's link follows `release.json`.** `release.ps1`
  passes it as the define `DotnetInstaller`, made from the framework's name
  and major version, as it already makes the winget id; `setup.iss` has the
  .NET 10 link as its default.
- **`-WingetOnly` also compares the `.sha256` with the setup file** and
  stops when they differ, so a manifest never carries a hash the file does
  not have.

## Consequences

- **A first install needs the internet when a prerequisite is missing**,
  and up to about 150 MB of downloads. On a PC that has all three, nothing
  changes: the setup downloads nothing.
- **Only the .NET runtime can still stop a setup by design**: in a silent
  setup that is not elevated. winget avoids it through its dependency; a
  script installs .NET first or runs the setup elevated.
- **The setup runs Microsoft's installers unchecked by hash.** Its trust
  rests on HTTPS to Microsoft's addresses and on Microsoft's signatures; the
  log records what ran.
- **winget can take CabinetOS now**, without waiting for the Windows App
  Runtime 2.5 in its catalogue. A winget install is the setup's install:
  the Start Menu shortcut, the Apps entry and in-app updates, no PATH entry
  for `cabinetos-cli` (the zip's `install.ps1 -AddToPath` adds one).
- **`install.ps1` does not change**: it still checks, names the commands
  and downloads nothing.
- **The release's manifests need the setup file**: a build without one
  (`-NoSetup`, or no Inno Setup) writes none, with a warning.
