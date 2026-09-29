## Report: sub-phase 11a, the shell's part

Sub-phase 11a, the shell's part, is done: all 31 commands N1–N31, quick search Q1, the keypad keys P1 and commander marking P3 run in the window against the core's protocol 12, and everything is on main (last 5ae7859). I start Commander Compact, the shell's part, now.

Context: CabinetOS shell (ui/), built from docs/research/total-commander.md Part 3 (b). Nothing under core/ or sdk/ touched. The core's 11a requests landed one by one while I worked; each was adopted as it landed, so nothing is left "behind a name".

## Built
- **P1 keypad keys** (KeyNames): 0x6A/0x6B/0x6D/0x6E/0x6F → numpadmultiply/numpadadd/numpadsubtract/numpaddecimal/numpaddivide, shown "Num *", "Num +", "Num -", "Num .", "Num /"; VS Code's numpad_add spellings read; keypad digits stay plain digits.
- **P3 panes.selection: commander** (SelectionModel.KeyMode, MarkPassed, MarkThrough): moving keeps marks; Shift marks the rows the cursor leaves; the row at the list end is marked when the cursor cannot move; Shift+Home/End mark through; Shift over marked rows unmarks; a new listing starts unmarked; the mouse keeps Windows rules; switching keeps marks. Applied live from config.
- **N1, N4, N5, N7, N8**: go.root (DisplayFormat.Root, long-path prefixes and shares); showInLeft/RightPane; swapPanes (the two pane models change places: folder, marks, history, sort, search results; keyboard stays on its side); refresh (keeps cursor and marks by name; in search results re-runs the search).
- **N9–N12**: a sort per pane sent with every list_directory of that pane (PaneSort; same key reverses; size and date start largest/newest first; extension needs protocol 12); a small chevron on the sorted column heading, except for the default name A→Z.
- **N13, N18–N20**: Space (toggle in place; measures a folder it marks), Num * (files only), Ctrl+Num -, Num / (MarkMemory: marks kept by name, saved by copy/move/delete and unmarks).
- **N21–N23**: copy full path / name / folder path to Windows' clipboard, one per line, no quotes.
- **N24–N27**: F3 opens the first Tool Extension that shows the file, else a status-bar note, never the default program (on a folder: measures it, as TC's F3); F4 edit_path; Shift+F4 a name box over a new row → create_file → select → edit_path (a taken name opens that file; a folder's name is refused); Windows Properties via show_properties, also a "Windows Properties" button in the Properties dialog.
- **N6, N14–N17, N2, N3**: one PromptBox in the palette's frame: the pattern box (last pattern offered, ten remembered, "Include folders" off by default, match_entries ranges, an answer about an older generation is asked again), same extension (`*.ext`, `*.` for none; the core's extension rule), the pinned folders (Ctrl+D; typing narrows; last row pins the current folder), and the drive list anchored under the pane header without a box (a letter picks at once; goes to the folder this pane last showed on that drive, else its root; DriveMemory).
- **N28, N29**: measured folder sizes (FolderSizes): running total in the tertiary colour, then the total; status bar adds measured folders ("1 selected · WinSxS, 3.5 GB"); unreadable folders named in the status bar; forgotten and cancelled when the pane leaves the folder.
- **N30, N31**: Ctrl+P / Ctrl+Shift+Enter show the terminal (start the default shell if none), terminal_type_paths, keyboard to the terminal; the line counts as typed so the folder sync does not put a cd into it.
- **Q1 quick search**: CharacterReceived in a pane when no binding took the key; 1 s quiet starts a new search; Esc and another folder clear it; status bar shows "Quick search: rep"; match_entries with first_from (first letter from cursor+1, longer text from the cursor).
- **Protocol 12**: the E2E pin moved to 12; every 11a request/reply/event is mirrored and checked against the core's schemas (ProtocolTests, with an "agreed shape" step used while the core's schema did not have a type yet).
- **Snapshot aid**: steps `type:`/`accept` for the prompt, `drive:<letter>`, `quick:<text>`; `select:` now scrolls the row into view; popups the aid cannot draw are logged at debug level.
- **Docs**: docs/ui.md "Total Commander's keys" (table of all 31 + marking, prompts, a pane's order, folder sizes, new text file, quick search, checked), the pane key table, the command table, the Search section's note list, two "Not in this version" rows retired.
- **Live check**: ui/livecheck/livecheck.ps1 steps for Num *, Ctrl+Num -, Num + (*.txt), Space on a folder, Alt+Shift+Enter, F3 (on run.cmd), F4 and Shift+F4 (with a stand-in editor the script writes into files.editor: wscript.exe + a script that logs the file and shows no window; never Notepad), quick search + F8, Shift+F8, Ctrl+P, Ctrl+\, Alt+F1, Ctrl+U. Parses in PowerShell 7 and 5.1; not run by me (needs an unlocked, watched screen).

## Commits (all on main)
9660057 keypad keys · 8239bb6 protocol 12 pin · ee6e849 commander marking · b35ebf6 root/show in pane/swap/refresh · 84b6c78 sort per pane · 5ad2f30 Space, Num *, Ctrl+Num -, Num / · 5cae97d copy path/name/folder · 8136486 F3/F4/Shift+F4/Windows properties · 194a751 icon test thread-pool fix (a step-3 test that failed once by chance) · a6ea7d3 pattern box, same extension, pinned folders · 7a29804 show_properties checks · 0702a4b drive list · 9cda16b folder sizes · 526d452 match_entries checks · 4824441 quick search + terminal inserts · e5edf56 Space in the Windows style · d51dcdb live check steps · 5ae7859 docs + snapshots.

## Checks
- Debug and Release builds with -warnaserror: 0 errors, 0 warnings.
- UI tests: 614 total; a normal run passes 613 and skips 1 (the two-window test); with CABINETOS_UI_E2E=1 all 614 pass. Run against a core rebuilt from main at 13c7f9e.
- New tests: KeyTests (keypad), SelectionModelTests (both styles, Space, invert, unmark, SetMarks), MarkMemoryTests, PaneSortTests, DisplayFormatTests (root), ListingViewTests (names found again), PromptTests, DriveMemoryTests, FolderSizesTests, QuickSearchTests, TerminalTests (typed paths hold the line), ProtocolTests (all version-12 messages against the core's schemas), and CommanderEndToEndTests (4, real core): sort by extension; create_file + edit_path with an editor found nowhere + show_properties errors; measure_paths into FolderSizes; match_entries for the pattern box, same extension and quick search, and terminal_type_paths to a gone shell.
- Tests first: behaviour changes were run red first (keypad: 17 failing cases; the others did not compile without the new API).
- No CI claim.

## Live check (snapshot aid, no real keys, display asleep)
- Navigation: Ctrl+Right showed src in the right pane, Ctrl+U swapped with the left pane staying active, Ctrl+\ went to C:\, Ctrl+R relisted.
- Sort: size (largest first), extension (LICENSE first, then bin, dll, exe, json, md, pdf, ps1, toml, txt, zip), name Z→A; the other pane untouched; notices "Sorted by …".
- Marking (commander config): Space marked notes.md, Num * marked every file but it (folders untouched), Ctrl+Num - cleared, Num / restored.
- F3 on notes.md opened Markdown Preview; F3 on setup.exe gave the note and ran nothing; F4 and Shift+F4 handed the stand-in editor build.ps1, the new todo.txt (0 bytes) and the existing report.txt; the Properties button opened Windows' sheet (ok), which closed with the core.
- Pattern box: "Marked 4 files matching *.md" at the repo root; history row shown the second time. Same extension on report.txt: 1 file.
- Drive list under the right pane, E: highlighted; C picked the C: folder that pane had shown at start; E went back to E:\codespace.
- Folder sizes: Space on C:\Windows\WinSxS showed 2.3 GB while counting, 3.5 GB at the end; Shift+Alt+Enter at the repo root: core 27.7 GB, ui 168 MB, docs 12.1 MB in under a second.
- Quick search: "ma" → manual.pdf, "e" after a pause → engine.dll; status bar "Quick search: ma".
- Ctrl+Shift+Enter on notes.md started pwsh with 'C:\…\files\notes.md' on the prompt, no Enter.
- Snapshots: docs/log/2026-09-28/11a-drive-list.png, 11a-pattern-box.png, 11a-pinned-folders.png, 11a-folder-sizes.png, 11a-quick-search.png, 11a-new-text-file.png, 11a-terminal-path.png (half size).
- Clipboard (Ctrl+Shift+C etc.): every clipboard call, the old Ctrl+C's too, answered CLIPBRD_E_CANT_OPEN with the display asleep; the text itself is for the real-key run.

## Decided
- Commander Shift marks the rows the cursor leaves, not the one it lands on; the end row when it cannot move; Home/End through the last row; over marked rows it unmarks — because TC's Shift+Down marks one file per press and Shift takes marks back — undo: SelectionModel.MarkOnTheWay/KeyMode.
- Space in the Windows style keeps the cursor row selected when it is the only selection (and measures a folder) — because the arrow that moved there selected it, and toggling left nothing and measured nothing — undo: SelectionModel.MarkInPlace.
- The pattern box, the pinned folders and the drive list share one PromptBox in the palette's frame; the drive list is anchored under the pane header without a box — because the note puts two of them in the palette's frame, and a WinUI Flyout drew no pixels for the snapshot aid — undo: Views/PromptBox, FilePane.HeaderElement.
- Size and date sorts start with the largest and the newest — Total Commander's defaults — undo: PaneSort.Next.
- The sort chevron shows only when the order is not name A→Z — because the design's headings are plain for its default — undo: FilePane.UpdateSortGlyphs.
- Measured sizes belong to the pane's folder and are forgotten (running counts cancelled) when it leaves — because TC drops them on re-reading a panel and a stale size misleads — undo: PaneModel.ForgetSizes.
- F3 on a folder measures it — Total Commander's F3 on a directory shows its size — undo: MainWindow.Commander ViewAsync.
- Restore Selection works by name, saved by copy/move/delete and every unmark, not by navigation; the Windows-style cursor row alone is not a mark — undo: MarkMemory, RememberMarks calls.
- Ctrl+R keeps the marks by name — TC's refresh keeps the selection — undo: PaneModel.RefreshAsync.
- Quick search: first letter looks after the cursor, longer text at it; a key the window took (Space) types nothing into it; Ctrl or Alt alone is a shortcut, AltGr types — Explorer's cycling and layout-true characters — undo: QuickSearch.FirstFrom, OnPaneCharacter.
- Shift+F4 suggests "New Text Document.txt" even when it exists, since a taken name opens that file (TC) — undo: NewTextFileAsync.
- Ctrl+U swaps the pane models; an open editor and the dock stay by position — undo: MainWindow.Commander SwapPanes.
- Selection/file commands give the search note in results, including Ctrl+Left/Right and Ctrl+Shift+Enter; Ctrl+\, Ctrl+U, Ctrl+D, Alt+F1/F2, Ctrl+K Ctrl+P, Ctrl+P act on the pane — the note's rule — undo: the ListingOnly wraps.
- One selected measured folder shows its size in the status bar — undo: MainWindow.UpdateStatus.
- The live check writes its own stand-in editor into files.editor — so F4 can never open Notepad on the creator's desktop — undo: the config block of livecheck.ps1.
- Terminal inserts mark the prompt line as typed — so the folder sync does not type a cd behind the paths — undo: TypingTracker.OnPathsTyped.
- ProtocolTests pin a version-12 message's exact JSON until the core's schema has it, then check it against the schema (all are schema-checked now) — undo: AgreedRequests/AgreedIncoming.

## Needs the core
- Question, not blocking: edit_path resolves files.editor before its path checks, so with an editor that is found nowhere a folder or a missing file answers spawn_failed instead of invalid_path / not_found (CommanderEndToEndTests asserts only "an error" there).

## Needs the user
Nothing. The planning session's real-key run: `ui/livecheck/livecheck.ps1` (the 11a steps are new).

## Known gaps
- Nothing of 11a was pressed with real keys tonight (display asleep): the keymap route for the keypad, Space, F3/F4 and the chords is covered by unit tests of the keymap and by the live check's new steps, not by a real run.
- Clipboard copies unverified (CLIPBRD_E_CANT_OPEN with the display asleep).
- Whether Windows' property sheet comes to the front after AllowSetForegroundWindow (the core's note too).
- A quick search cannot hold a space (Space is bound) or ; and |.
- The pattern history, the drive memory and a pane's own sort last for the session only (tabs in 11b would save the sort).
- The drive list shows lettered volumes only.
- Measured sizes do not follow later changes inside a folder (as in TC).

## Noticed out of scope
- docs/log/2026-09-28/README.md has no rows for the step-3 and 11a images; the planning session keeps that file.

## Ready text for docs/PLAN.md (under Phase 11, after "11a, the core's part")
**11a, the shell's part, 2026-09-29.** The window runs Total Commander's small commands through the core's protocol 12: Ctrl+\ to the root; Alt+F1 and Alt+F2 open a drive list under that pane, where a letter picks a drive and goes to the folder the pane last showed there; Ctrl+Left and Ctrl+Right show a folder in a pane; Ctrl+D lists the pinned folders; Ctrl+U swaps the panes with their marks and history; Ctrl+R lists again; Ctrl+F3 to Ctrl+F6 give a pane its own order. Marking: Space (a folder it marks is measured), Num + and Num − with a pattern box in the palette's frame (the last ten patterns, files only unless "Include folders"), Alt+Num + and Alt+Num − for the same extension, Num * to invert, Ctrl+Num − to unmark, Num / to restore what a file command or an unmark cleared; `panes.selection: commander` marks as Total Commander does. F3 shows a file in a Tool Extension and never runs it; F4 edits; Shift+F4 names, creates and edits a text file; Windows' own property sheet opens from the palette and from Properties; Shift+Alt+Enter measures every folder, and the Size column counts up, then shows the total; Ctrl+P and Ctrl+Shift+Enter type paths into the terminal; letters typed in a pane are a quick search; the keypad's operators are keys. The window reads no disk and scans no names for any of it. 614 UI tests (one opt-in), four of them new end-to-end tests against the real core; the real-key steps are in `ui/livecheck/livecheck.ps1`.

## README
UI test count: 614 (one of them, the two-window test, runs only with CABINETOS_UI_E2E=1).
