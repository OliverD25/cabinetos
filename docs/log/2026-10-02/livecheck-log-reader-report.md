# The live check reads its log once (2026-10-02, step 3 of 3 of the creator's list)

Context: `ui/livecheck/livecheck.ps1` judges every step by the window's log.
Each helper used to read and decode the whole log again on every call, and
`WaitUi` did it every 50 ms. Now one reader keeps each file's read position,
reads only the new bytes, and serves every helper from its lines. Written by
the Sonnet agent in the branch `worktree-agent-a6cd3b859dde9d7a8`. The window
runs were on this PC (three runs, one invalid, see "Decisions") and on the
creator's Omen laptop (two runs). Nothing was pushed or merged.

## Summary

| Question | Answer |
|---|---|
| Do the judgments stay the same? | Yes. This PC: 203 True, 0 False before; 204 True, 0 False after. The one new line is the first step's. Every other True line is the same text; only two numbers that change on every run differ (a millisecond count, a core's process ID). The laptop: the same, 203 and 0 before, 204 and 0 after. |
| Is the run faster? | A little on a fast machine: this PC 7 min 51 s to 7 min 39 s (about 12 s), the laptop 7 min 53 s to 7 min 27 s (about 26 s). The check is mostly fixed waits and real work of the window, so the log reading was a small part there. One count of a log of the run's end size cost 51 ms before and 0.36 ms now (measured below); on a slow machine the old cost grows with the machine's slowness, the new one stays small. |
| Do both PowerShells run it? | The script parses in 5.1 and 7.6.6 (`build\check-scripts.ps1`). The reader passed 153 checks against `Get-Content` in both. The check itself runs in 5.1, which the run used. |
| Does `shell-v2` still merge? | Yes. `git merge-tree --write-tree` of `shell-v2` with this branch, and of `main` with this branch, both give a tree and no conflict. |

## 1. The commits

| Hash | Subject |
|---|---|
| a6ecf31 | livecheck: a log reader that takes only the bytes written since its last call |
| 8d82531 | livecheck: the named log helpers answer from the reader |
| e806c55 | livecheck: the inline reads of the log go through the reader |
| 599a57b | livecheck: the first step judges its Tabs by the log, and the run says how long it took |
| b90029b | docs: ui.md says how the live check reads the window's log |
| (this commit) | docs: the report of the log reader and the 2026-10-02 build log |

## 2. How the reader works

It is a small C# class, `LogReader`, compiled by `Add-Type` in `livecheck.ps1` (C# 5 syntax, so Windows PowerShell 5.1 can compile it). It sits in one block before `UiCount`.

- It takes a folder and a glob (`ui.*.jsonl` or `core.*.jsonl`). Each call to `Refresh()` lists the files, sorts them by name (as `Get-Content` with a wildcard gave them), and for each file compares its length with the bytes already taken.
- It reads only the new bytes (`FileShare.ReadWrite | Delete`, so the window's writer is not blocked), cuts them at the last newline, decodes them as UTF-8 and splits them into lines. A line without its newline stays in the file for the next call.
- If the files stop being a continuation (a file went away or got shorter, a new file sorts in front of a read one, or a file in the middle grows after a later file has lines), it clears everything and reads again from the first byte. That gives the order `Get-Content` would give.
- A pattern's matching lines are found once per line, with `Regex(IgnoreCase | CultureInvariant)`, which are the rules of `-match`. The reader keeps a list of matching line numbers per pattern.
- A line's JSON is parsed by `ConvertFrom-Json` when a helper first asks for the parsed object, and the object is kept. A line nobody asks for is never parsed, so a bad line that no helper reads cannot stop the run.
- The PowerShell side: `LogOf 'ui'|'core'` refreshes and returns the reader. `UiCount`, `UiLines`, `UiLast` (the last matching line), `UiAll` and `UiObjects` (parsed objects, optional skip) answer from it. `WaitUi` refreshes in each 50 ms loop and takes the new objects from the `$before`-th match on, as before.
- Every helper calls `LogOf` first, so it sees what the file holds at its call. The "before" counts, the compared counts and `Select-Object -Last 1` keep their meaning.

Moved onto the reader (37 `Get-Content` lines of the log are gone; 14 `Get-Content` lines are left and read other files):

- 18 helper functions: `UiCount`, `WaitUi`, `UiLines` (moved up to the reader's block, because the sites before section 13 need it), `SelectionText`, `PressForNameBox` (two reads), `FolderSizeLines`, `HandedToTerminal`, `ToggleCount`, `LastChosenTheme`, `LastMetrics`, `ThemeLog`, `FocusOtherCount`, `TabLog`, `NoticeCount`, `ListRequests`, `PluginLog`, `ShellLines`, `CoreMenuChanges` (the core's log).
- 18 inline sites: the trap's list of cores, the frame table, the dialog check, the counts of ready previews, `measure_paths`, the F3 notice, the typed path, lost keys, closed tools (two), the keyboard owner, the commands that ran, the F-key bar's copy, the edge notice, and at the end the cores the window started, the job's lines in the UI log and in the core log.
- Left on `Get-Content` on purpose: the 14 reads of the configuration file, the stand-in editor's and recorder's logs, `report.txt`, and the index script. They are not the window's log. `livecheck2.ps1` was not changed: it reads the log twice in its whole run (once each at lines 166 and 198), no helper and no polling, so a reader there would only add code.

## 3. Before and after

Runs (output files in `_io\live-check\`, outside the repository):

| Run | Script | Time | True / False |
|---|---|---|---|
| this PC, before: `run-2026-10-02-log-reader-before-2.txt` | `f061537` (main's) | 7 min 51 s (01:55:51 to 02:03:42); first to last Step line 469.2 s | 203 / 0 |
| this PC, after: `run-2026-10-02-log-reader-after.txt` | `599a57b` | 7 min 39 s (the run's own line: 459 s); first to last Step line 457.8 s | 204 / 0 |
| laptop, before: `run-2026-10-02-0204-rd-omen-laptop.txt` | the clone's script at `7dbead7`, which is main's but for one 11a wait (see "Decisions", the laptop) | 7 min 53 s (02:04:33 to 02:12:26); Step lines 467.8 s | 203 / 0 |
| laptop, after: `run-2026-10-02-0213-rd-omen-laptop.txt` | `599a57b` | 7 min 27 s (the run's own line: 448 s); Step lines 444.8 s | 204 / 0 |
| laptop, last run before tonight: `run-2026-10-01-2002-rd-omen-laptop.txt` | older main | 7 min 45 s (20:02:18 to 20:10:03) | 203 / 0 |

The window and the core were the same in every run of tonight: the Release window built in this worktree at 01:27 and main's release core.

**Are the True and False lines the same?** `compare-runs.py` (in the scratchpad, not committed) takes the lines that end in `: True` or `: False`, in order. The diff of the raw lines, this PC:

```
+tab to the other pane and back: Tab twice ran view.focusOtherPane twice: True      (the new line)
-18: Windows' menu showed, 30 items, the core answered in 779.5 ms: True
+18: Windows' menu showed, 30 items, the core answered in 1070.5 ms: True
-core 120980 exited with the window: True
+core 120908 exited with the window: True
```

With every number replaced by `#` only the new line is left. The laptop's diff is the same shape (27 items, 1728.8 ms to 1420.4 ms; core 33660 to 22144). So no judgment changed and no line was lost.

**The five slowest steps** (seconds from one Step line to the next; this PC, then the laptop):

| Step | PC before | PC after | Laptop before | Laptop after |
|---|---|---|---|---|
| edge: the fixture's names on the left, its long path on the right | 17.2 | 16.6 | 16.4 | 16.1 |
| edge: Enter into the long path | 12.1 | 11.9 | 11.5 | 11.5 |
| 13: the palette, Lock Folder Tree; the pane goes to gamma; Alt+Shift+L finds gamma | 10.0 | 9.6 | 10.0 | 9.5 |
| the Agent step's screenshot (it holds a 6 s fixed sleep) | 9.7 | 9.7 | 9.7 | 9.7 |
| keys: the find box, a letter filters, Enter finds, F5 copies | 9.6 | 9.1 | 9.9 | 8.9 |

The slow steps are slow for their own work (the fixture with a 300-character path, the Agent install, fixed waits), not for the log. The biggest gains of any step were 0.6 s on this PC and 1.0 s on the laptop (the keys section's find-box steps).

**What the reading costs**, measured on this PC in Windows PowerShell 5.1 on a log of the run's end size (4,900 lines, 1.35 MB; `bench-reader.ps1`, scratchpad):

| Work | Before (reads the file) | After (the reader) |
|---|---|---|
| the frame table: parse the log, keep `frame stats` | 450 ms | 81 ms the first time asked |
| `$ran`: parse the `command executed` lines | 85 ms | 30 ms |
| `ThemeLog` | 65 ms | 10 ms |
| 100 calls of `UiCount` (or 100 polls of `WaitUi`) | 5,160 ms | 36 ms |
| the reader's first call (reads the whole file once) | | 34 ms |
| the three parses above asked a second time (parsed lines are kept) | | 10 ms together |

On this PC one old call costs about 50 ms; the 50 ms poll of `WaitUi` therefore ran at half its speed. The reader makes one call cost 0.4 ms. I could not measure the VM (the task says never use it), so I cannot confirm the 8 and 12 minutes the creator saw there. The shape of the cost says why they happened: each of those steps read and parsed the whole log, and on a slow disk or CPU that cost is paid again in every poll.

**The first step.** The run's output now has this line, which is True on both machines: `tab to the other pane and back: Tab twice ran view.focusOtherPane twice: True`. The `Step` lines already started with `HH:mm:ss.fff`; the last line of a run is now `the run took 00:07:39 (459 s)`.

## 4. The checks

- `build\check-scripts.ps1`: every script parses in Windows PowerShell 5.1.26100.9444 and in PowerShell 7.6.6 (run after the reader commit and after the last script commit).
- The reader against `Get-Content`, `test-reader.ps1` (scratchpad): 153 checks pass in 5.1 and in 7.6.6. Each of nine patterns is compared on a log that grows in steps: the lines, the count, the last line, and the parsed objects of `command executed`, with and without a skip; all lines; a half-written last line stays unread (141 against 142); a second file (a run across midnight); a file that sorts in front; a file in the middle that grows; the core log; `ui.last-start` is not taken as a log.
- The window runs: see section 3. All five runs of tonight that reached the end had no False line and no STOP line.
- `dotnet build` and `dotnet test` were not run: no C# file of the window was touched. The reader's C# lives inside a PowerShell script.
- `git merge-tree --write-tree` of `shell-v2` and of `main` with this branch: no conflict.

## 5. Decisions (what, because, undo)

- The reader is C# inside the script, not PowerShell: because a 50 ms poll must not cost a PowerShell loop over thousands of lines, and `Add-Type` is already used there. Undo: revert a6ecf31 to e806c55.
- A pattern's matches are found incrementally and cached by pattern, and JSON is parsed lazily: because parsing every line would parse lines no helper reads and a bad line would stop the run; lazy parsing keeps the old behaviour (only matched lines were parsed). Undo: parse in the append loop.
- Every read decodes UTF-8: because the 23 sites without `-Encoding` read ANSI in 5.1, which turns the "·" of the status text into two wrong characters. No judgment uses it (all patterns are ASCII); only the printed `after Num *: ...` lines change. Undo: none needed.
- The frame table asks for the `"frame stats"` lines before it parses: because it parsed all 4,900 lines to keep the ones whose `message` is `frame stats`; every such line contains that text, and its own filter is unchanged. Undo: use `UiObjects '.'` there.
- The first step counts `view.focusOtherPane` with the same pattern shape the other steps use and waits up to 2 s for the second line (the log writer has its own thread): because moving `FocusOtherCount` up would put a hunk next to `shell-v2`'s changes. Undo: delete the five lines of the step.
- No change to the `Step` lines' time: because they already carry it. Only the total was added.
- `livecheck2.ps1` unchanged: because it reads the log twice in a run. Undo: none.
- The commit lines end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, not the `Claude Sonnet 5` of the handout: because the session's own attribution rule names the model I am. Undo: the planning session can change it when it merges; I did not rewrite anything.
- A directory junction `core\target` in this worktree pointed to the main checkout's `core\target`, and the Agent plugin fixture was copied to the git-ignored `sdk\extensions\agent\plugin\plugin.wasm`: because `run-livecheck.ps1` has no `-Core` argument and looks for the core next to the worktree, and because without the plugin the check skips the Agent steps (one "WAITING" line, and 8 True lines fewer). I removed the junction with `rmdir` (the link only; main's `core\target` is intact). The `plugin.wasm` is still in the worktree, git-ignored. Undo: delete that file.
- The laptop's clone: it was on the branch `worktree-agent-ab6db70e81cc65518` (7dbead7), which is not an ancestor of this branch. `remote-livecheck.ps1` pulls with `--ff-only` and `-q`, so the first attempt failed without a word, and the laptop ran the clone's own script with my window build. I kept that as the laptop's "before" (its script differs from main's only in the 11a wait). For the second run I fetched my branch from the bundle the script had sent into a new branch of the clone, `worktree-agent-a6cd3b859dde9d7a8`, checked it out, ran, and checked the earlier branch out again. The new branch is still in the laptop clone: undo: `git -C C:\Dev\cabinetos\cabinetos branch -D worktree-agent-a6cd3b859dde9d7a8` (I did not delete it: hard stop 1).

## 6. Skipped, left, and what needs attention

- **Two invalid before-runs.** The first (195 True) and the second (stopped at 01:45:31) ran with no marketplace index: the check's Windows PowerShell 5.1 was started from a PowerShell 7 tool and inherited its `PSModulePath`, so `build-index.ps1` said `Get-FileHash` was not a cmdlet. Without the index the Agent install steps failed, Enter then opened `photo1.jpg` in the Photos app, and the foreground guard stopped the run. I closed that Photos window (the one titled `photo1.jpg`; the creator's own Photos window stayed) and ran again with `PSModulePath` reset to the 5.1 value. The two files are in `_io\live-check\` as `run-2026-10-02-log-reader-before.txt` and `run-2026-10-02-log-reader-before-agent.txt`; do not use them. A planning session that starts `run-livecheck.ps1` from a PowerShell 7 shell will meet this.
- **A run of main today gives 203 True, not 216** (this PC, the laptop yesterday and the laptop today agree). I do not know where "about 216" comes from.
- **Eight `CabinetOS.exe` processes are alive on the laptop**, started between 00:46 and 01:11 by other coders' test runs. I did not touch them; the live check passed beside them.
- **Worth doing, not done:** `remote-livecheck.ps1` should say when its `git pull --ff-only` fails (it hides the error and goes on with an old script). And `run-livecheck.ps1` could pass `-Core` on, which a worktree needs.
- Nothing needs the creator tonight. The merge is the planning session's: five commits touch `ui/livecheck/livecheck.ps1` and `docs/ui.md`, and `docs/log/2026-10-02/`.
