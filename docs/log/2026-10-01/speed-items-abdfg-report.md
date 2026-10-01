# The speed items A, B, D, F and G: the coder's report

The creator read [speed-review.md](speed-review.md) and chose its proposals
A, B, D, F and G (the plan's Phase 19e follow-up). C and E were built by
another coder in another worktree. Built by the coder (Sonnet) in its own
worktree, one commit per item and more where an item had a core and a window
part. Numbers are medians from `ui/livecheck/speed-review.ps1` on release
builds with `-Variants`, the two builds taking turns run by run on a shared
machine (other coders' windows ran, and the runner waited for them). "Before"
is main's release build of 03:14 on 2026-10-01 (window and core); "after" is
this worktree's. Where an item needed only one of them to differ, both
variants ran the same window with their own core.

## A. ReadyToRun for the window's release publish

`build/release.ps1` passes `-p:PublishReadyToRun=true` to `dotnet publish`.
The runtime identifier (`win-x64`) was already set in
`ui/Directory.Build.props`, and the compiler's NuGet package was in the
local cache, so nothing else was needed.

- Size, from two publishes of the same tree: the window's folder 41.3 MB
  (45 files) to 57.2 MB (45 files); `Microsoft.WinUI.dll` 7.0 to 15.7 MB,
  `CabinetOS.Core.dll` 1.6 to 3.8 MB, `Microsoft.InteractiveExperiences.Projection.dll`
  1.5 to 3.4 MB. Zipped as the release script zips (Optimal): 11.8 to
  17.0 MB. So the release zip grows by about 5 MB, not the 15 MB the review
  expected from the unzipped folder.
- The publish still has `CabinetOS.pri`, and the published window starts and
  shows both folders (a `start-small` run of the R2R folder, 856 ms to both
  folders). The start gain, measured again with the runner on the final
  tree (6 runs each, a shared machine, the same core): `start-small`, process
  start to both folders shown, 1,068 ms [1,040 to 1,162] with the JIT build
  and 868 ms [821 to 1,163] with ReadyToRun, 200 ms shorter; `start-home`,
  1,222 ms [1,101 to 1,685] and 940 ms [877 to 1,082], 282 ms shorter (the
  JIT runs of that scenario were noisy: another window was busy). The review
  had 0.14 and 0.24 s. The `hello` answer is 8 and 6 ms.
- Not run: the whole `release.ps1` (it builds the three Rust programs, notices
  and zips, and publishes nothing). Only its `dotnet publish` step was run,
  into the scratch folder, with the same arguments.
- Written down in `docs/release.md` (what a release is, the measured sizes,
  build step 2) and in the CHANGELOG.

## B. Fewer rows made ahead in the panes' lists

`FilePane` sets `Repeater.VerticalCacheLength = 0.5` in its constructor, one
value for every theme (`RowCacheScreens`). WinUI's default is 2.

Runner, 4 runs each way, `theme` scenario (100,000 files and 3,000 entries on
screen, two changes each way per run, so n = 8) and `list-files`:

| | before | after |
|---|---|---|
| change to Commander Compact, the frozen frame | 105.8 ms [83.3 to 129.8] | 78.8 ms [64.2 to 113.1] |
| its measured UI work | 54.4 ms [44.5 to 70.4] | 34.5 ms [27.4 to 42.5] |
| change back to Default, the frozen frame | 89.0 ms [73.9 to 107.8] | 65.4 ms [60.6 to 91.3] |
| its measured UI work | 26.6 ms [25.1 to 32.2] | 16.2 ms [15.7 to 19.9] |
| 100,000 files, scroll:20, frames over 20 ms | 5.2 % [0 to 13.5] | 2.5 % [2.5 to 5.1] |
| the same, worst frame | 32.8 ms [19.7 to 35.6] | 22.6 ms [20.8 to 28] |

The list-files rows are 4 runs of the snapshot aid's PageDown, not real keys.

- No test: the test project references only `CabinetOS.Core`, not the WinUI
  project, so `FilePane` cannot be built in a unit test, and no end-to-end
  test reads a pane's repeater. The value is a named constant with a comment.
- The live check has a new step (`ui/livecheck/livecheck.ps1`, in the
  Commander Compact section, before the way back to Default). It goes to the
  100,000-file bench folder in Commander Compact, rests the pointer on the
  scroll bar of the left list, finds the thumb through UI Automation (else
  estimates it from the list's right edge and top), throws it from the top
  to the bottom with the real mouse in under 100 ms (`[Live]::Throw`), waits
  1 s, reads the list's scroll position back through UI Automation ("over
  90 expected"), takes `compact-scrollbar-drag-live.png` and prints its path.
  **It was not run.** The thumb's place is the weak point: a WinUI scroll bar
  widens only under the pointer, and its automation tree is empty until then
  (a probe on a running window, with no mouse, found the bar with an empty
  rectangle and no thumb). The line that prints the list's scroll percent
  says at once if the throw missed. Look at the screenshot for empty rows.
- I did not add a log line that counts rows made with no name: the names
  come from the listing's shared memory and are bound in `ElementPrepared`,
  so a row without a name does not exist; what a fast drag can leave is
  blank space where no row was made yet, which only a screenshot shows.

## D. Quick Open's walk

- Core: `WALK_LIMITS` is 2 s and 200,000 entries (was 20,000). `complete` is
  false when either limit ends a walk, as before; the existing tests cover
  both limits, and a new one makes 25,000 files and finds a name that sorts
  after them, with the old limit as the contrast (it stops, empty, `complete`
  false).
- Window: `QuickOpenModel.Note` and a line under the rows in the overlay
  say "Nothing found, but not every name was searched: the search stopped at
  its limit of 2 s or 200,000 entries. The indexer (docs/indexer.md) searches
  whole volumes." (or "Not every name was searched: ..." with rows; "a volume
  is still being indexed" for the index). The Search view's note takes the
  limits from the same `SearchNotes` class, so it says 200,000 too. Two
  window tests: the walk with no rows and with rows, a complete answer, the
  index, an error, blank text and a new Open.
- Docs: `docs/ui.md` (Search view, Quick Open), `docs/ipc.md` (`search`),
  `docs/indexer.md`, the CLI's help text, the CHANGELOG.

Runner, `quick-open`, 4 runs each way, on the 100,000-file folder (core time
of the three queries):

| query | before | after |
|---|---|---|
| `dat` | 66.2 ms, 50 rows, visited 20,000, complete false | 99.2 ms, 50 rows, visited 100,000, complete true |
| `IMG77` | 59.8 ms, 0 rows, complete false | 94.3 ms, 0 rows, visited 100,000, complete true |
| `фото9` | 56.9 ms, 0 rows, complete false | 92.0 ms, 50 rows, complete true |

The review said `IMG77` has no result because it comes after the first
20,000 names. It has none at all: with the whole folder searched the answer
is a true "no". The Ukrainian query is the one that was cut off.

## F. Icons drawn at the core's start

New: `core/crates/cabinetos-core/src/icons.rs`; `Hydrator::draw_ahead`,
`Hydrator::icon_keys_of` and `DrawnAhead` in `cabinetos-fs/src/hydrate.rs`.

- The core calls `icons::at_start` in `run`, before it serves: `folder` and
  `generic` are drawn on a blocking thread (`spawn_blocking`). After each
  `list_directory` reply is queued (`Session::listing_ready`, not for a
  plugin's in-process session) `icons::for_listing` reads the listing's
  section on a blocking thread and draws the kinds of its first entries:
  `folder`, `generic` and `ext:<ext>`, each once, at most 24, from the first
  5,000 entries, none for `.exe`, `.ico` and `.lnk` files that are on this
  disk (their `path:` key stands for one file). The reply is never held up.
- The sizes: the ones `get_icon` was asked for so far, else all four. Each
  key decides again when its turn comes, so once the window has asked for
  one size the rest of a batch stops drawing the other three.
- A `get_icon` that arrives meanwhile finds the PNG, or waits for the drawing
  in progress and goes first: `icon_png` counts itself as waiting while it
  takes the process-wide drawing lock, and the drawing ahead sleeps in 1 ms
  steps while that count is not zero. The one-at-a-time lock (`DRAWING`) is
  unchanged. A shutdown ends a batch (the core's token is asked before each
  icon).
- Logging: one debug line per batch that drew or failed to draw something,
  `icons drawn ahead`, with `why` (`core start` or `listing`), `keys`,
  `drawn`, `cached`, `failed` and `took_ms`, in the span of the action that
  caused it. A batch that found everything drawn says nothing.
- A finding on the way, from a temporary timing test (removed): the first
  drawing in a process costs about 35 ms (16 ms for the shell's icon index and
  20 ms for its image lists), later ones 1 to 2 ms on the same thread, and a
  type with a handler more: on a fresh thread per key, `.txt` took 34 ms,
  `.png` 44 ms and `.pdf` 7 ms. So drawing all four sizes before the window
  has asked for one costs little. The review's 25 ms per icon looks like the
  first-use cost of each type (and thread) plus the wait for the lock, not the
  drawing itself; that was seen in a unit test process, not in the core.

Runner, 5 runs each, both variants with the same window (the build of the D
tree) and their own core; the window logs each `get_icon` round trip:

| | before | after |
|---|---|---|
| `get_icon` round trip in the window, 85 requests: median, p95, max | 25.6, 45.6, 62.4 ms | 0.22, 1.7, 3.8 ms |
| the core's time for them, median | 25.3 ms | 0.04 ms |
| `start-small`: the last icon's reply after the first "listing shown" | 106 ms [100 to 122] | 64 ms [58 to 68] |
| `start-home`: the same | 109 ms [106 to 119] | 96 ms [85 to 101] |
| the first icon's reply after "listing shown", small / home | 79 / 82 ms | 62 / 66 ms |
| `start-small`, process start to both folders shown | 1,077 ms [1,025 to 1,149] | 1,118 ms [1,056 to 1,144] |
| `start-home`, the same | 1,077 ms [1,047 to 1,174] | 1,077 ms [1,059 to 1,114] |
| `list-files`, first frame of the 100,000 files | 83 ms [77 to 194] | 84 ms [82 to 85] |

What is left of the gap after the rows is the window's own work (its
`describe_entries` round trip of 4 to 17 ms, the pages of icon requests it
sends one layout pass later, and decoding each PNG on its side), not the
core's. The start itself did not get shorter, as expected: the icons are not
on the way to the first rows. The 41 ms difference in the `start-small`
median was inside the runs' spread; the final check (6 runs, below) has the
start at 1,077 ms before and 1,068 ms after on `start-small`, and 1,284 and
1,222 ms on `start-home`, so the drawing at the core's start does not slow it.

## G. Note an unclean exit of the window

The marker `ui.last-start` did not say whether a run closed (it held one
time), so the clean end is now written into it. `CrashNotice` in
`ui/CabinetOS.Core/Diagnostics`:

- The marker has up to three lines: the start's UTC time (as before), `pid
  <process ID>`, and `closed <UTC time>`. A marker an older window wrote has
  only the time and is read as "unknown", so the first start after this
  change says nothing.
- `Program.Main` calls `CrashNotice.MarkClosed` when `Application.Start` has
  returned, before it logs `exited`: only a run that gets there has closed.
  A native end of the window never reaches it. `MarkClosed` writes only over
  the window's own marker (same pid and no end yet).
- `MainWindow.OfferCrashBundleAsync` (off the UI thread, as before) gets a
  `StartCheck` from `CheckAtStart`: the crash bundle, as before, and the start
  time of a previous run that has a pid, no end, and is not running. If there
  is one, it logs one WARN line, `previous run ended without closing`, field
  `started_utc`. Nothing is shown on screen.
- A second window running at the same time has a marker with a pid and no
  end too, so the check asks whether that process is alive and started
  before the marker's time (a later process with the same ID is another
  one). That is what keeps two windows from reporting each other.
- Tests, unit (`LogBundleTests`): a start with no end and a gone process says
  so and records itself; a clean end says nothing; a live process says
  nothing, and a process that started after the marker's time (a reused ID)
  does; an older marker, no marker, broken text and a pid that is not a number
  say nothing; a close writes the end into its own marker only, three lines,
  and the next start reads it as clean. The six existing calls of
  `CheckAtStart` read `.CrashBundle` now. End to end
  (`CrashNoticeEndToEndTests`, `CABINETOS_UI_E2E=1`): three windows on one
  log folder: the first is closed and leaves its end, the second says nothing
  and is killed, the third logs the note once with the second's start time.
- Docs: `docs/diagnostics.md` ("Where the files are", "A run that ended
  without closing", the offer at the next start), `docs/ui.md`, the CHANGELOG.

## Decisions made alone

Each as what, because, undo.

- Quick Open's note does not say "narrow the text" as the handout's example
  did, because a walk that stopped goes the same way and stops at the same
  place, so more letters cannot reach the names after the limit; it names
  the indexer instead. Undo: the text in `QuickOpenModel.Note`.
- The note has no "first N entries", because the reply carries no count of
  entries visited, and the stop may be the time, not the entries. Undo: add a
  `visited` field to `file_search_results` (Rust, C#, schema, docs/ipc.md) and
  say it in the note.
- The core's walk keeps its 2 s and has no check of the clock inside a
  folder, because a folder is read whole and 200,000 entries are processed in
  tens of milliseconds; the time is checked before each folder, as before.
  Undo: n/a. Left as it is: a query that matches every name of a 200,000
  entry walk collects up to 200,000 hits before it ranks and cuts to the
  limit (about 30 MB for a moment); a bounded heap would avoid it.
- The window's walk limits are a text constant (`SearchNotes.WalkLimits`) that
  repeats `WALK_LIMITS` of the core, as the old text repeated 20,000,
  because the protocol does not carry them. Undo: carry them in the reply.
- `VerticalCacheLength` is 0.5 in every theme, one constant, as asked.
  Undo: `RowCacheScreens` in `FilePane`.
- The live check's scroll-bar step finds the thumb by UI Automation and
  falls back to an estimate, and reads the list's scroll position back, so
  that a miss shows in the output instead of as an empty screenshot. It was
  not run, as told. Undo: delete the step and `[Live]::Throw`.
- The icon batches log only when they drew or failed something, because a
  line for every listing whose icons are all drawn already says nothing.
  Undo: the `if` in `icons::spawn`.
- Drawing ahead uses all four sizes until the window has asked for one,
  and narrows after, because the core does not know the screen's scale and a
  draw costs 1 to 2 ms once the thread is warm. Undo: `Hydrator::sizes_to_draw`.
- A request for an icon goes before the drawing ahead through a counter of
  waiting requests, not a second lock, because the existing one-at-a-time
  lock must stay the only lock around the shell. Undo: `waiting` and
  `lock_drawing` in `hydrate.rs`.
- A listing's icon scan stops at 24 kinds and 5,000 entries, because the first
  screens decide what the window asks for first and the cache holds only
  2,000 PNGs (24 kinds at four sizes is 96 of them). Undo: the two constants.
- No notice in the status bar for an unclean end, because its one crash
  offer is for the heavy-mode bundle ("The last run crashed while heavy
  logging was on") and says something else, and a window ended from the Task
  Manager would raise it too. Undo: show a notice in `OfferCrashBundleAsync`.
- The marker got a process ID, because without it a second window running
  at the same time would report the first as ended without closing. A close
  is written only over the window's own marker; if two windows ran and the
  one that did not write the last marker ended without closing, that is not
  noticed. Undo: write one marker per process.
- `CheckAtStart` returns a `StartCheck` record instead of the bundle path, and
  its six calls in `LogBundleTests` read `.CrashBundle`, because one read of
  the marker must serve both answers. Undo: split the method in two.
- The ReadyToRun size in the release script's comment is the measured 41 to
  57 MB, not the review's 42 to 57, because that is what the two publishes
  of this tree gave.
- Commits were rebased onto origin/main three times (the other coder's
  CHANGELOG lines conflicted each time; both sides were kept), and a
  fix of F's module comment was folded into F's commit (`--autosquash`) before
  anything was pushed.

## The final check

On the tree that is on main (`38960c3` to `bc5a581`, on top of `5f55479`):

- Core, from `core/`: `cargo build --workspace`, `cargo test --workspace`
  (811 passed, 0 failed, 6 ignored), `cargo clippy --workspace --all-targets
  -- -D warnings`, `cargo fmt --all -- --check` and `cargo deny check` all
  pass. (An earlier run of the tests in a pipe through `head` was cut short
  by the pipe; it is not the run counted here, which wrote to a file.)
- Window, from `ui/`: `dotnet build CabinetOS.sln -warnaserror`, 0 warnings;
  the fast run 1,134 passed, 39 skipped; the full run with
  `CABINETOS_UI_E2E=1` and this worktree's release core 1,173 of 1,173 in
  2 min 10 s, the new end-to-end test included.
- `build/check-scripts.ps1` in both PowerShells: every script parses.
- The final measurement above: three variants, 6 runs of `start-small` and
  `start-home` each, taking turns: main's build of 03:14 ("before"), this
  tree's release build ("after", the JIT build, every item but A) and this
  tree's ReadyToRun publish.

## What I could not do

- Run the live check, as told. The new scroll-bar step was only parsed
  (`build/check-scripts.ps1`) and its automation lookups were tried on a
  running window without the mouse (the list is found, the thumb is not in
  the tree until the pointer is over the bar).
- Run all of `build/release.ps1`, see A.
- Measure item B with real mouse input. The numbers above are from the
  snapshot aid's frames; the empty-rows risk is the planning session's look
  at `compact-scrollbar-drag-live.png`.
- Add a test for B's value (see B).

## Left, and worth doing

- `FilePane` has no test place; a window-level test of the repeater's
  settings would need the WinUI project in a test, or a log line.
- Quick Open's walk collects every match of up to 200,000 names before it
  ranks and cuts to 50 (about 30 MB for a moment, for a query such as "."); a
  bounded heap in `search::walk` would keep the 50 best only.
- `file_search_results` could carry `visited`, so the note can say "searched
  the first N entries".
- The core draws no icons ahead for a refresh of a watched listing (a new
  extension that appears later is drawn when asked for, as before).
- Results of the runs: `%TEMP%\cabinetos-speed-review\results\abdfg-b`,
  `abdfg-d`, `abdfg-f`, `abdfg-final`: only `results.json` kept; the run logs
  were deleted. The fixtures under `%TEMP%\cabinetos-speed-review\fixtures`
  (110,000 scratch files) were made again and are left for the next runs;
  `speed-fixtures.ps1 -Remove` deletes them.
