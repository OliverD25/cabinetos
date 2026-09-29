## Report: the shell's part of Phase 10

Report: the shell's part of Phase 10 is done — lean Windows App SDK packages, the .pri on a plain publish, an About view, plus the tooltip fix and install_finished's version; 5 commits on main, 429 UI tests pass. Starting the edge-case sweep now.

## Built
1. Only the Windows App SDK components the window uses. ui/Directory.Packages.props and CabinetOS.csproj reference Microsoft.WindowsAppSDK.WinUI 2.3.9 (WebView2 comes through it), .Foundation 2.3.12 (the unpackaged bootstrapper, MRT resources), .InteractiveExperiences 2.1.9 (windowing, input, composition, Mica) and .Runtime 2.5.1 (the runtime version the bootstrapper asks for). These are the metapackage's own versions; the Runtime package's build checks that they match. Left out: AI, ML, Search, Widgets, DWrite. project.assets.json still names exactly one Microsoft.WindowsAppSDK.Runtime, which release.ps1 reads.
2. The .pri on a plain publish. CabinetOS.csproj now sets EnableMsixTooling true (WindowsPackageType stays None, so the app stays unpackaged). build/release.ps1 dropped -p:EnableMsixTooling=true and its comment; the .pri check stays.
3. About. "Help: About CabinetOS" shows:
   - Version (Program.Version), Core ("0.1.0, protocol 11", from ping);
   - Build: commit, build time and "with uncommitted changes" from release.json next to CabinetOS.exe, or "Development build (no release.json next to CabinetOS.exe)";
   - links to LICENSE and THIRD-PARTY-NOTICES.md through open_path, or "…come with a release; this development build has none";
   - the copyright line.
   The logic is in ReleaseFacts, ReleaseFolder and AboutText (CabinetOS.Core/Presentation, tested). It is a dialog like the others: Esc closes it and nothing runs under it. Dialogs now start with the keyboard on their default button; WinUI had put it on About's first link.
4. Tooltip (your re-run's finding 5). The palette row's tooltips (the pencil, the lock, the keycaps' "+N") are ToolTip objects that close themselves when they open for an element that is not shown. That is the case when the hover delay ends after the palette closed.
5. Your finding about the dialog check. Both live-check scripts now check that no "command executed" lies between "dialog shown" and "dialog closed". The window also logs "key held by a dialog" when a key reaches it under a dialog.
6. install_finished's installed_version (the core's new field) is adopted: an install from elsewhere with --version shows its real version, and a failed update keeps the old one. This closes my "Needs the core" item from protocol 11.

## Commits (on origin/main)
- 6fdc435 ui: reference only the Windows App SDK components the window uses
- 1bba529 ui, build: a plain publish of the window carries CabinetOS.pri
- dcdead9 ui: a palette tooltip cannot open after the palette closed
- 6779d13 ui: About CabinetOS, with the versions, the build and the notices
- aefe88b ui: the marketplace takes install_finished's installed version

## Checks
- Debug and Release builds with -warnaserror: 0 warnings, 0 errors.
- dotnet test: 429 passed, 0 failed, 0 skipped. New tests: AboutTests (8 tests: release.json parsing, odd and damaged files, the folder, the lines) and An_install_from_elsewhere_takes_the_version_the_core_names, plus an install_finished protocol sample with installed_version.
- No CI claim. Only build/release.ps1 was touched under build/.

## Numbers (dotnet publish CabinetOS\CabinetOS.csproj -c Release -r win-x64 --self-contained false, from ui\)
| | files | unpacked | zipped (Optimal, as release.ps1 zips) |
|---|---|---|---|
| before (metapackage, with -p:EnableMsixTooling=true) | 60 | 84,392,326 B (80.5 MiB) | 28.1 MB |
| after (components, with the flag) | 45 | 41,682,005 B (39.8 MiB) | 11.2 MB |
| after (components, plain publish, no flag) | 45, same list | 41,682,005 B | 11.2 MB |
Gone: onnxruntime.dll 21.2 MB, DirectML.dll 18.3 MB, Microsoft.Windows.AI.MachineLearning.dll, Microsoft.ML.OnnxRuntime.dll, System.Numerics.Tensors.dll, and 10 projection DLLs (AI Text, Imaging, MachineLearning, Video, ContentSafety, AI, AI.Foundation, Search, Widgets, Graphics.Imaging). About 20 small Foundation projections stay (about 1 MB: notifications, pickers and the like), because Foundation is one package. I did not run release.ps1. From the window's part alone, the release zip should fall by about 17 MB (from 77 MB to about 60 MB); this is an estimate, not a measurement.

## Live check (release builds, snapshot aid)
- The plain publish (no flag) started: core ready (protocol 11), and a terminal opened (WebView2 works in the publish).
- The built window (bin) also starts with the MSIX tooling on.
- About, in a stand-in release folder (the publish plus a release.json in release.ps1's format, LICENSE and a stand-in notices file): Version 0.1.0; Core 0.1.0, protocol 11; commit, build time, "with uncommitted changes"; both links. The keyboard started on Close, view.toggleTerminal was refused while it was open, and the keyboard went back to the pane after. In the development build it said "Development build" and that the two documents come with a release. Snapshot: docs/log/2026-09-28/phase-10-ui-about.png. The snapshot aid now draws open dialogs into its images.
- The links were not clicked: that would open Notepad on the desktop. Enter on a file uses the same open_path.
- Tooltip: the new snapshot step tooltip:Change keybinding. With the palette shown it opened; after the palette closed it closed itself at once (owner_shown false, open false). Your next real-key run is the proof.

## Decided
- Four components, not a smaller set — because Foundation carries the bootstrapper and MRT, InteractiveExperiences windowing and Mica, and the Runtime check wants its versions — undo: back to the Microsoft.WindowsAppSDK PackageReference.
- The Foundation projections stay — because they come in one package, and removing files by hand could break the bootstrapper at run time for about 1 MB — undo: none needed.
- EnableMsixTooling true in the project, not a publish-only target — because it is Microsoft's setting for an unpackaged app that needs resources, and it gives the same files as the flag — undo: set it false and put the flag back in release.ps1.
- release.json is read in the window's process, off the UI thread, as the second exception after tool.json (brief §1, no file I/O in the UI) — because the core has no request for the release facts, and it is a few hundred bytes of the program's own metadata — undo: a core request in its place.
- The About links call open_path directly, not through the router — because the router runs nothing while a dialog is open, and the links are the dialog's own — undo: allow two local commands in SetModal for About.
- With a core that still runs help.about itself (it still does: target core in registry.rs), the window shows About on its command_result — because "your handler runs anyway" was not true for a listed core command — undo: remove the CoreResult case for help.about in OnCommandCompleted.
- Dialogs always start on their default button — because Enter on About's first link would open the license — undo: FocusDialog's early return.

## Needs the core
- help.about with target ui, as planned; the window works either way.

## Needs the user
- The next real-key run: the pencil's tooltip after the palette closes (palette-closed-under-the-mouse.png), and the About dialog with Esc and the two links.

## Known gaps
- About's links were not clicked in a check.
- The release zip size after the change was estimated, not measured with release.ps1.

## Noticed out of scope
- docs/release.md step 2 (lines 58–63) and docs/dev-setup.md (lines 63 and 84) still name -p:EnableMsixTooling=true and the Microsoft.WindowsAppSDK package; ready text below. ADR 0009 line 80 names the flag as how the .pri was made; as a record it can stay.
- Keymap.With (ui/CabinetOS.Core/Keys/Keymap.cs:81) has no caller except a test.

## Ready text: docs/release.md, step 2
"2. `dotnet publish CabinetOS\CabinetOS.csproj -c Release -r win-x64 --self-contained false -o <release folder>`, framework-dependent on .NET and on the Windows App Runtime of the machine. The project keeps the Windows App SDK's MSIX tooling on, which writes `CabinetOS.pri`, the compiled XAML, into the publish; without it the window stops at start. The app stays unpackaged. The script stops if the `.pri` file is missing."

## Ready text: docs/dev-setup.md
Line 63: replace "`Microsoft.WindowsAppSDK` NuGet package" with "Windows App SDK NuGet packages (the WinUI, Foundation, InteractiveExperiences and Runtime components)".
Line 84, the table row: "| `Microsoft.WindowsAppSDK.WinUI` 2.3.9, `.Foundation` 2.3.12, `.InteractiveExperiences` 2.1.9, `.Runtime` 2.5.1 (NuGet) | Windows App SDK 2.5.1 | Microsoft Software License Terms (redistributable; not an open-source license) | WinUI 3, Mica, the window |"

## Ready text: docs/PLAN.md, under Phase 10
**The shell's part of Phase 10, 2026-09-29.** The window references only the Windows App SDK components it uses (WinUI, Foundation, InteractiveExperiences and Runtime) instead of the metapackage, whose AI, ML, Search, Widgets and DWriteCore components it never touches (Article 10). The published window went from 60 files and 80.5 MiB to 45 files and 39.8 MiB (28.1 to 11.2 MB zipped), without onnxruntime.dll and DirectML.dll. The project keeps the Windows App SDK's MSIX tooling on for its resource step, so a plain `dotnet publish` carries `CabinetOS.pri` and the published window starts; `build/release.ps1` no longer needs `-p:EnableMsixTooling=true` and still checks for the file. "Help: About CabinetOS" shows the version, the core's version and protocol, the commit and build time from `release.json` (or "Development build"), and opens `LICENSE` and `THIRD-PARTY-NOTICES.md` with `open_path`; Esc closes it and nothing runs under it. 429 UI tests. Guide: [ui.md](ui.md), "The solution" and "About".

## README
The UI test count becomes 429.
