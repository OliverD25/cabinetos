# The fourth round of failing tests (2026-10-03, the night)

Context: the fourth round of the work in
[../2026-10-01/flakes-report.md](../2026-10-01/flakes-report.md),
[../2026-10-02/e2e-flakes-2-report.md](../2026-10-02/e2e-flakes-2-report.md) and
[../2026-10-02/e2e-flakes-3-report.md](../2026-10-02/e2e-flakes-3-report.md).
Five tests failed in a full run, none of them because of a product change; the
Phase 24 report listed them in "Known failures"
([header-sorting-and-pane-dividers-report.md](header-sorting-and-pane-dividers-report.md)).
This report is about four of them: the job progress rate in the core, the three
update tests of the window, and the hello time. (The fifth, the Quick Open
test, is the palette race; its report is
[palette-highlight-race-report.md](palette-highlight-race-report.md).)
Written by the Sonnet coder agent in the worktree branch
`worktree-agent-a86e614f4c9072097` (it starts at eb4bb52; main was merged into
it once, at 99273ec, before the last laptop run). Away mode: the creator was
asleep, nothing here was decided by them. The rule was "no test is loosened":
no time limit and no expected text was changed.

## Summary

| Test | Cause | Change | Proof |
|---|---|---|---|
| `jobs::ten_thousand_files_report_at_most_thirty_times_a_second` (core) | The rate itself was right: records were sent at least 34 ms apart, always. The **time stamp** was wrong. `finish()` stops the job's clock first and then writes the undo journal (10,021 entries in this test) before the job is marked done. The publisher kept sending progress records in that time, and each carried the stopped time in `elapsed_ms`. Under load the journal write takes hundreds of milliseconds, so many records shared one stamp, and the test counts records per second of that clock | The clock stops under the emitter's lock (`Job::stop_clock`), and `publish` sends nothing but the final record once it has stopped. A unit test shows it, and fails without the change. Test and limit unchanged. A line in the CHANGELOG (product behaviour: yes) and one paragraph in `docs/jobs.md` | alone 10 of 10; beside a release build of the workspace 10 of 10; the whole `cargo test --workspace` beside a release build exit 0 (see "Item 1") |
| `ShellEndToEndTests.With_auto_install_off_...`, `...With_auto_install_the_download_is_swapped_in...`, `...A_swap_that_fails_...` (window, laptop) | **Not a leftover.** The fixture wrote `release.json` "0.1.0" beside a copy of the core, but the core says it is 0.1.1 (and now 0.1.2) since the version bump (0d09c20, 2026-10-02 23:51). An install that holds another version than the running core counts as "swapped in by another window already": the updater answers `ready` at once ("CabinetOS 0.1.0 is installed; restart to use it") and checks for nothing | The fixture asks the core it copies for its version (`--version`) and writes that; the feed offers 99.0.0 (as the Rust update tests do), so a coming release number cannot make the offer "not newer". Every expected text is the same as before, built from the two values | A window-free copy of the fixture shows the answer from the real core; laptop, full suite: all three pass at f1026a7 |
| `StartEndToEndTests.The_core_is_started_while_the_window_is_built_and_answers_hello_at_once` (window, laptop) | The measurement is right (from the request's creation to the decoded reply: not the core's start, not the connect). The machine was shared: the test classes of a full run run in parallel, so the window under test shared the laptop with other test windows. Alone: 9.7 to 11.5 ms. Beside two other test runs: 44 to 263 ms | The test joins the collection of the speed measurements (`FrameTests`, no parallelization: it runs after the parallel tests, alone). The 60 ms limit stays | the laptop, full suite: passes at f1026a7 |

## Item 1: the job progress rate

**Seen.** One failure in a full `cargo test --workspace` under the load of
parallel builds (2026-10-03, about 00:05), three passes alone. The test asserts
that no second of the core's own clock (`elapsed_ms` of the records) holds more
than 30 progress records.

**The three guesses of the brief.**

- "The throttle measures from the previous send and a stall lets a burst
  through": no. `publish` checks the gap with `sent.elapsed()` and takes
  `last_emit` from an instant read after the check, so two sends are always at
  least 34 ms apart. On the client side the pipe bunches arrivals (gaps of 10
  to 25 ms were seen), which is why the test allows 2 more by arrival.
- "The clock the test reads is coarser than the throttle's": no. Both are
  milliseconds. A stall between the throttle's instant and the read of
  `elapsed_ms` can shift one stamp, but the live gaps between stamps were 34 to
  37 ms at the smallest in six loaded runs.
- "Two kinds of events counted together": no, the test counts progress records
  only.

**The cause**, found with a temporary print of every record's stamp (reverted):
`run::finish` called `final_elapsed_ms.set(...)` first. That freezes
`elapsed_ms`. Then it wrote the undo journal (`journal_ended`: one JSON line with
10,021 entries, a trim and a size check), and only then set the job's phase to
done. `publish` skipped a non-final record only when the phase was done. So for
the whole journal write the publisher went on sending a record every 34 ms or
more (the speed changes all the time, so every record differs), each with the
frozen stamp.

| This PC, `jobs.rs` test with the print | records with the final record's stamp | busiest second by stamp |
|---|---|---|
| 3 runs, nothing beside it | 2, 2, 2 | 22, 22, 23 |
| 6 runs beside 64 busy processes, before the fix | 10, 8, 9, 10, 9, 7 | 20, 21, 21, 21, 26, 21 |
| 6 runs beside 64 busy processes, after the fix | 1, 1, 1, 1, 1, 1 | 21, 21, 20, 22, 22, 22 |

A busiest second of 26 with 9 records on one stamp shows the way: a journal write
of about one second would reach 31. **I did not see the test fail** (a count over
30) on this PC; the proof of the cause is the pile-up of 7 to 10 records and the
fix that removes it.

**The change.** `Job::stop_clock` takes the emitter's lock and sets the frozen
time; `Job::clock_stopped` says it is set. `progress::publish` returns early for a
non-final record when the clock has stopped. Taking the lock first means a record
that is being sent is complete before the clock stops, so no record is stamped
after the final one's time. Unit test:
`progress::tests::nothing_but_the_final_record_goes_out_once_the_clock_has_stopped`
(checked: it fails without the change, "left: 2, right: 1").

**What is left in the stamps.** The final record waits out the 34 ms gap in real
time but carries the time the work ended, so its stamp can be less than 34 ms
after the one before it (gaps of 12, 17 and 33 ms were seen). In theory a second
could hold 31 records by stamp: 30 at exactly 34 ms plus the final one. The
records were 47 ms apart at the median here, because of the system timer's 15.6 ms
steps. `docs/jobs.md` says this in its "30 Hz rule" section. Making the final stamp
wait too would change the job's reported time for every job, so I did not.

**Proof.** `cargo test -p cabinetos-core --test jobs ten_thousand`, alone, 10 of 10
passed. The same test ten times in a row beside `cargo build --release --workspace`
in another process (its own target folder): 10 of 10. The whole `cargo test
--workspace --no-fail-fast` beside that build (the build ran for the first 3 min 20 s
and the test run for 2 min 39 s, ending at the same time): exit 0. The first such
attempt (without `--no-fail-fast`) stopped at another test, see "What is left".
After the merge of main, the five checks pass without load (see "Tests and runs").

## Item 2: the three update tests

**The brief's idea** was a staged update left over from an earlier run, kept
somewhere the tests do not isolate. The data says no:

- The updater's folder is `CABINETOS_UPDATE_DIR`
  ([docs/release.md](../../release.md), "Updates"); the test's `Run.Start`
  sets it to `<test folder>\update`, a new temp folder per test. The staged
  files and `state.json` live there.
- On the laptop (read-only look over SSH): no `%LOCALAPPDATA%\CabinetOS` at all, no
  `staging` or `previous` folder anywhere under `C:\Dev`, no `CABINETOS_*`
  variable at machine or user level. **Nothing was deleted there for this item.**
- The text itself says it: "CabinetOS **0.1.0** is installed". `UpdateText.Notice`
  builds it from the state `ready` with `installed` = 0.1.0 and `current` = the
  running core's version.

**The cause.** `Updater::check`, `download` and `apply` begin with
`begin(needs_current = true)`. It reads the install folder's `release.json`
(0.1.0 in the fixture) and compares it with the core's own version
(`CARGO_PKG_VERSION`, 0.1.1 since 0d09c20). If they differ it publishes `ready`
with `installed` = the file's version and refuses the step: "CabinetOS 0.1.0 is in
place already; restart CabinetOS to run it". So `update.check` never found 0.2.0,
the dialog never opened, and the failed-swap test never tried a swap.

**A window-free copy of the fixture** (a script: a copy of the core in an install
folder with `release.json`, a feed with 0.2.0, then `cabinetos-cli update check`
against the real core of this branch), on this PC:

| install `release.json` | `update check` | state |
|---|---|---|
| 0.1.0 (the fixture) | exit 1, "CabinetOS 0.1.0 is in place already; restart CabinetOS to run it" | `ready`, installed 0.1.0, current 0.1.1 |
| 0.1.1 (the core's own) | exit 0 | `available`, latest 0.2.0 |

The brief says the tests pass on this PC. I could not run a window here tonight,
and by this reproduction they cannot pass with any core that is not 0.1.0: they
passed before the bump at 23:51 (the third flake report's full suite on this PC, evening of 2026-10-02: 1339 of 1339), and the laptop's
release core is 0.1.1 (0.1.2 since main moved).

**The change** (`ShellEndToEndTests.cs`): `VersionOf(core)` runs `cabinetos-core.exe
--version` and takes the last word; `UpdateSetup` writes that as the install's
version, returns it, and the feed offers `Offered = "99.0.0"` instead of the
literal 0.2.0. The expected texts use `Offered` and the install's version. The
fixture also keeps working at the next release (0.1.2 passed the same way).

**The laptop.** The release core there was 0.1.1 before the merge, no update state,
nothing to clean. Laptop, commit 647e0b1, the three tests and the unit test with the
same name prefix: 4 of 4 passed. Full suite at f1026a7: all three pass.

## Item 3: the hello time

**What the 60 ms covers.** The window logs `elapsed_us` of a reply from the moment
`CoreClient.RequestAsync` creates the pending request (before the JSON is encoded)
until `Dispatch` has decoded the reply on the pipe's reader. So: encoding, the
"request sent" log line, the pipe write, the core's handling of `hello`, the pipe
read, the decoding. It does **not** include the core's process start or the pipe
connect (the log says `waited_ms` 50 to 75 ms for that, before the hello), and not
the 780 ms the window needs to build itself before it asks. The previous test's
window is not in it either: `FinishAsync` waits for the window's exit.

**What the full run adds.** The test classes without a `[Collection]` run in
parallel in one test host (xUnit), so the window under test shared the laptop with
the windows of other classes, each starting WinUI in a Debug build. That the test
itself is fine alone is shown by a scratch probe: a copy of the test that always
fails and prints its numbers, built and run on the laptop by one script
(`scratch-hello-probe`, never merged, local only):

| Laptop | `hello` reply, ms | connect wait, ms |
|---|---|---|
| alone, 9 runs (one single run, then 8 in a row) | 10.3, 11.5, 10.9, 11.1, 9.7, 9.8, 10.1, 10.5, 10.0 | 51 to 75 |
| beside two other full test runs, 5 runs | 44.3, 78.6, 92.2, 250.8, 263.1 | 481 to 974 |

The three failures of the full runs (68.5, 69.7 and 159.8 ms) fit the second row.
So the number measures what it claims, and the product is not slow: it answers in
10 ms on the laptop. A speed goal on a machine shared with other windows says
nothing, the same reason the frame measurements run alone.

**The change.** `[Collection(FrameTests.Name)]` on `StartEndToEndTests`. That
collection (`DisableParallelization = true`, defined in
`ContextMenuEndToEndTests.cs`) runs after the parallel tests, one test at a time.
The 60 ms limit stays. Both doc comments say why. This is what the third flake
report already proposed ("it belongs in the collection that runs alone").

**Proof.** Laptop, full suite at f1026a7: the hello test passes (no run showed the
value, since a passing test prints none; the probe above is the evidence for the
numbers). I did not repeat the full suite: it takes about 5 minutes of a laptop
that other coders also use.

## Tests and runs

- This PC, in the worktree, on the tree after the merge of main (f1026a7):
  - `cargo build --workspace`: exit 0.
  - `cargo test --workspace`: exit 0, 921 passed, 0 failed, 6 ignored.
  - `cargo clippy --workspace --all-targets -- -D warnings`: exit 0.
  - `cargo fmt --all -- --check`: exit 0.
  - `cargo deny check`: exit 0.
  - `dotnet build CabinetOS.sln -warnaserror` (Debug): exit 0, 0 warnings, 0 errors.
  - `dotnet build CabinetOS.sln -c Release -warnaserror`: exit 0, 0 warnings, 0 errors.
  - `dotnet test --solution CabinetOS.sln --no-build`: exit 0, 1427 total, 1355
    passed, 72 skipped (the opt-in end-to-end tests), 0 failed.
- No window opened on this PC. The consent file said held; nothing waited on it.
- The laptop, `remote-tests.ps1 -EndToEnd -Branch worktree-agent-a86e614f4c9072097`:
  - `-Filter "FullyQualifiedName~With_auto_install|FullyQualifiedName~A_swap_that_fails"`
    at 647e0b1: 4 of 4 passed.
  - the whole suite at f1026a7 (05:08 to 05:14): **1427 of 1427 passed**, 0 failed,
    0 skipped, 4 min 43 s. Full output `_io\test-runs\tests-2026-10-03-0508-rd-omen-laptop.txt`.
  - the probe runs, on the scratch branch: `_io\script-runs\script-2026-10-03-0457-rd-omen-laptop.txt`
    and `_io\test-runs\tests-2026-10-03-0422-rd-omen-laptop.txt`.
- The load for item 1 was `cargo build --release --workspace` in another process
  with its own target folder (`C:\Users\Admin\AppData\Local\Temp\cabrel` and `cabrel2`:
  my own build output, left there; a longer path made `link.exe` fail with
  LNK1104), and for the stamps 64 busy Python processes.
- `.md`, `.rs` files LF; `.cs` files CRLF (checked with `git ls-files --eol`; every
  edit to a `.cs` file was made with a script that reads and writes the file as it
  is, never `sed -i`).

## The laptop

- Shared with other coders' runs and the planning session's merges. Two of my
  runs were overtaken: the run that started at 03:52:24 was another coder's request
  (a palette test, their branch), not my probe, and its build failed with `CS2012`
  on a file locked by the XAML compiler; and at 04:14 my script's wait loop returned another coder's
  full run (their branch `worktree-agent-a182f2e3a435f9ed5`) as if it were mine.
  `remote-tests.ps1` waits for any change of `DONE-tests.md` and copies the newest
  `tests-*.txt`, so two requests at the same time give one the other's result.
  I made a gate (my own script, not part of the repository): it waits until no
  task runs, no `CabinetOS.exe`, no `dotnet`, `MSBuild` or `testhost` of the clone,
  and no request was written in the last 2 minutes, four checks in a row, and only
  then starts. The runs after it went through. I cannot prove that my 04:14 start
  did not disturb their run (their run ended with a full result, 14 failures, three
  of them the update tests that fail on any branch without this fix).
- `remote-tests.ps1` copies this PC's release core over the laptop's when the
  hashes differ. My first run (03:50) copied my worktree's release core (0.1.1)
  over the laptop's; later the laptop held 0.1.2, so other coders copied theirs.
  After the merge of main I rebuilt my release core (0.1.2) before the last run, so
  it copied the current one and not a stale protocol.
- Two `CabinetOS.exe` processes with 0 threads (PIDs 27752 and 30092, started 04:24
  and 04:25 by runs that were not mine) kept every gate from going idle. I ended them
  with `taskkill /F /IM CabinetOS.exe` over SSH at 04:34, as `CLAUDE.md` allows
  for dead processes there, after checking that no task ran and that both had no
  thread. **Files deleted on the laptop: none.**

## Decided without you

Each line: what, because, undo.

1. **The progress clock stops for the publisher too (a product change), not only the
   test's measurement.** Because the records really did carry a wrong time while the
   job's journal was written; a client that shows "elapsed" saw it stand still while
   records still came. Undo: revert 345e2e8; the test flakes again under load.
2. **The final record keeps the time the work ended** and can be closer than 34 ms to
   the one before it by stamp. Because the other choice changes the elapsed time of
   every job by up to 34 ms. Undo: none needed; it is the old behaviour.
3. **The update feed offers 99.0.0, not 0.2.0.** Because the offered number must stay
   above the product's: 0.2.0 would turn "newer" into "equal" at the next minor
   release and break the three tests the same way. The Rust update tests use
   99.0.0 for the same reason. Undo: revert 647e0b1 (and the tests fail on any core
   that is not 0.1.0).
4. **The hello test is moved, not loosened.** Because the numbers show the product
   answers in about 10 ms and the failures come from a shared machine. Undo: revert
   89c9c89.
5. **A scratch branch `scratch-hello-probe` (two commits, local only, never pushed)
   made the probe.** Because a passing test prints no number. It also holds
   `ui/livecheck/probe-hello.ps1`. Undo: delete the branch; nothing refers to it.
6. **The CHANGELOG line and the `docs/jobs.md` paragraph.** Because the throttle fix
   changes what a client receives (fewer records at the end of a job). The merge of
   main put the line in the released 0.1.2 section; I moved it to Unreleased
   (f1026a7).
7. **Two dead processes on the laptop were ended** (see "The laptop"). Because the
   project rule allows it and no run could start otherwise.

## What is left

- **A new flake, not fixed (out of scope):**
  `gui_context::the_context_is_the_newest_state_with_marks_a_cursor_a_tool_an_empty_folder_and_a_cut`
  failed once, in the first `cargo test --workspace` under the build load, with
  "one line for each answer: left 0, right 5"
  (`core/crates/cabinetos-core/tests/gui_context.rs:333`). It reads the core's log file
  right after the answers, and the log is written by another thread, so on a busy
  machine the five lines are not there yet. It passed in every other run (the
  `--no-fail-fast` run under load, the five checks). The cure is the same as in the
  other log-reading tests: wait until the file holds the lines, up to a deadline.
- **The stamps of the final record** (see item 1): theoretical 31 in one second.
- **The brief's claim that the update tests pass on this PC** was not checked; no window
  ran here. By the reproduction above they could not have passed on a core of 0.1.1 or
  newer.
- **The shared inbox of `remote-tests.ps1` and `remote-script.ps1`** can mix up two
  coders' requests (see "The laptop"). A lock file in the inbox, taken by the script
  before it writes its request and released when it ends, would stop it. Not done:
  it is a change to the scripts that others use at the same moment.
- Two big build folders of mine in `%TEMP%` (`cabrel`, `cabrel2`) and the scratch
  branch are left for you to delete.

## Commits

| Commit | Subject |
|---|---|
| 345e2e8 | fix: a job sends no progress record after its clock has stopped |
| 647e0b1 | test: the update fixture takes its install version from the core it copies |
| 89c9c89 | test: the hello-time test runs alone, in the collection of the speed measurements |
| 008a388 | Merge remote-tracking branch 'origin/main' into worktree-agent-a86e614f4c9072097 |
| f1026a7 | docs: the progress-clock fix is a line of Unreleased, not of 0.1.2 |
| (docs) | docs: the report of the fourth round of failing tests and its row in the night's log |
