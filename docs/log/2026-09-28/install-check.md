# Install check from the release zip, 2026-09-29, 00:12 to 00:20

Step 1 of the next steps the creator chose: a stranger's install, for real,
on the development PC. The zip was the one `build/release.ps1` built from
ca02dfd (77 MB; its SHA-256 matched the `.sha256` file and the core agent's
report). Everything ran in Windows PowerShell 5.1, the way the README tells
a stranger to. Logs: [install-check-real.txt](install-check-real.txt) (the
real install), [live-check-installed.txt](live-check-installed.txt) (the live
check on the installed copy), [install-check-cases.txt](install-check-cases.txt)
(the scripted cases).

## The real install

- `Expand-Archive` of the zip into `%TEMP%\CabinetOS-0.1.0`: 83 files.
- `install.ps1 -StartMenu` (no PATH entry, no service): exit 0. 83 files,
  258,095,664 bytes, in `%LOCALAPPDATA%\Programs\CabinetOS`; the install
  record `.cabinetos-install.json` lists them; the Start Menu shortcut
  `CabinetOS.lnk` points at the installed `CabinetOS.exe` with that folder
  as its start folder; `cabinetos-core.exe` sits next to the exe;
  `extras\tools\markdown-preview\tool.json` is there.

## The live check on the installed copy

The same script as run 1 of [live-check.md](live-check.md), against the
installed program, with no `CABINETOS_CORE_EXE` set, so the launcher had to
find the core by itself. Every step passed, exit code 0:

- the launcher found the core next to the installed exe (`core started`
  names `%LOCALAPPDATA%\Programs\CabinetOS\cabinetos-core.exe`), and that
  core exited with the window;
- the rebinding through the pencil was written (`view.toggleSidebar` to
  `ctrl+alt+b`);
- the 100,000-entry listing: the core answered in 39 ms (the release build
  with the static C runtime);
- F7, F2, Delete and F5 with Skip confirmed on disk; the delete key's
  request ID appears on six log lines, from the key press to the core's
  "job queued";
- the terminal, the palette from inside it, search, Markdown Preview from
  `extras\tools`, and a clean exit.

The zip predates the fixes of the five findings (it is from ca02dfd), so
the pencil's tooltip is still visible in the scrolled screenshot and the
terminal shows the Unicode-injection symptom. That is expected.

The frame statistics of the 5-second PageDown were worse than in the first
live check: 37 to 61 frames per second, the worst frame 120 ms, three to six
frames over 33 ms in a second. Both coder agents were compiling on this PC
at that moment (the core's review build and the shell's Release build), so
the number is not comparable. The scroll's own cost is the subject of step
3, measured on a quiet machine.

The full-size screenshots are in `%TEMP%\cabinetos-ui-test\live-shots-installed`
and are not committed, since they repeat those of live-check.md.

## Uninstall

`uninstall.ps1` without `-RemoveData`: exit 0. The program folder is gone,
the shortcut is gone, `%LOCALAPPDATA%\CabinetOS` still holds only `logs`
and `WebView2` (as before the test), and `%APPDATA%\CabinetOS` is untouched.

## The scripted cases

The core agent's driver (`installtest.ps1`), re-run by the planning session
in Windows PowerShell 5.1 against the same zip, into
`%TEMP%\cabinetos-install-test\mine51`, with the downloaded-zip mark (a
`Zone.Identifier` stream) on the unpacked exe and a launch of the installed
program:

- install: exit 0, with the SmartScreen note and the `Unblock-File` command
  printed because of the mark;
- the installed program started with every data folder redirected to
  scratch: `window.png` written, `core started` from the temp install's own
  `cabinetos-core.exe`, `core ready` with protocol 11 and version 0.1.0;
- an update over the same folder: exit 0, the stale `old-file.txt` removed;
- `-StartMenu -AddToPath -WhatIf`: exit 0, nothing changed;
- refused with exit 1: `-Indexer` without `-AllUsers`; `-AllUsers` without
  administrator rights; a drive root; a folder holding other files; a start
  from the repository; missing prerequisites (`release.json` edited to ask
  for .NET 99 and Windows App Runtime 9.9.9.9), with no folder created;
- uninstall: exit 0, the folder gone; install again and `uninstall.ps1
  -RemoveData` with the data folders pointed at scratch: both scratch data
  folders removed.

## Leftovers

- One more test file in the Recycle Bin (`cabinetos-live-check-delete-me.txt`,
  3 bytes); four tiny files in total now.
- `%TEMP%\CabinetOS-0.1.0` (the unpacked zip), `%TEMP%\cabinetos-ui-test\liveinst`
  and `%TEMP%\cabinetos-install-test\mine51` remain for inspection.

## Not covered

- A PC without the .NET runtime, the Windows App Runtime or WebView2: the
  missing-prerequisite path was proven only by editing `release.json`. A
  clean Windows 11 machine is still the real test.
- `-AllUsers`, `-Indexer` and `-AddToPath`: dry runs only, by the rules.
