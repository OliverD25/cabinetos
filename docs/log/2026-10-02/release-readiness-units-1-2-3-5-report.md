# Release readiness, units 1, 2, 3 and 5 (Phase 22; 2026-10-02, evening)

Context: Phase 22 in [PLAN.md](../../PLAN.md), "Release readiness", units 1, 2, 3
and 5, by a coder on Sonnet in the worktree branch
`worktree-agent-a939b8f590d28b771`, while the creator was at this PC but not
answering. Decisions: [ADR 0021](../../decisions/0021-symbols-in-their-own-zip.md)
(the symbols zip) and [ADR 0020](../../decisions/0020-indexer-service-starts-by-itself.md)
(the indexer service starts by itself). Nothing was pushed, merged, published
or signed.

## Summary

| Unit | Result |
|---|---|
| 1. No machine-learning libraries | **Already in place.** The window's project has referenced only WinUI, Foundation, InteractiveExperiences and Runtime (at the 2.5.1 package's versions) since 6fdc435 on 2026-09-29, so the handout's premise (the whole metapackage) was out of date and the three attempts were not needed. Added what was missing: `release.ps1` stops before it zips when its folder holds `*onnxruntime*`, `DirectML*`, `*.AI.*` or `*.MachineLearning.*` (tried with fake files, and on a real publish with the whole package: 11 of its 15 extra files are named); the docs say which components and why; the cost of the whole package is measured. |
| 2. Symbols out of the zip and the setup | Done. `CabinetOS-<version>-win-x64-symbols.zip` (+ `.sha256`) holds the five `.pdb` files; the release zip and the setup hold none; `setup.iss` excludes `*.pdb` besides; `release.md` has "The symbols" and the new sizes. Crash trace checked with and without the symbols (below): **without them the Rust programs' backtrace is a list of `<unknown>` frames**, not function names. |
| 3. The indexer service starts by itself | Code, test and docs done. `--install` registers automatic + delayed start and starts it once. **The real check in the VM is not verified**: guest control cannot elevate (UAC), so the install in the VM is one manual step; the scripts for it are written and their other parts were tried. |
| 5. The setup downloads no prerequisites | Recorded in `release.md` ("The setup file", known gaps) and in the header of `build/setup.iss`. |

## Sizes (`release.ps1`; 1 MB = 1,048,576 bytes)

| | Files | Folder | Zip | Setup | Symbols zip |
|---|---|---|---|---|---|
| Before (this branch's start, 2a71b7d) | 71 | 251.7 MB (263,931,660 B) | 75.7 MB (79,348,730 B) | 42.5 MB (44,550,486 B) | none |
| After (ae47c82, clean tree) | 66 | 100.5 MB (105,394,731 B) | 32.1 MB (33,676,632 B) | 20.0 MB (20,994,504 B) | 43.6 MB (45,690,602 B) |

The goal (zip under 60 MB, setup under 35 MB, no machine-learning library, the
symbols zip beside them) is met.

Unit 1's cost of the whole package, measured in a scratch copy of `ui/` (the
window's publish with the release script's flags): the whole package 60
files, 100.3 MB, 34.7 MB zipped; the components 45 files, 57.8 MB, 17.3 MB
zipped; so 15 files, 42.5 MB, 17.4 MB. The scratch copy is outside the
repository.

## Commits

| Commit | Subject |
|---|---|
| a99f73f | release: the symbols go into a zip of their own, and the folder is checked for machine-learning libraries (units 1 and 2: one step of the script, one sizes table) |
| 08978c1 | setup: record that it downloads no prerequisites, by decision (unit 5) |
| 7d85ca9 | indexer: --install registers the service as automatic with a delayed start, and starts it once (unit 3: code, test, texts, ADR 0020) |
| ae47c82 | live check: the indexer service's restart check for the VM, and the install check looks for .pdb files (unit 3's scripts and docs) |
| the report's commit | this file and its row |

## What changed, per file

- `build/release.ps1`: step 5b. Every `.pdb` moves into the symbols zip (entries at its root) and its hash is written; the folder is checked for machine-learning libraries and the script throws before zipping; the summary prints the symbols zip. `-PackageOnly` runs the check and keeps the symbols zip of the build before.
- `build/setup.iss`: `Excludes: "install.ps1,*.pdb"`; header comment records the no-download decision.
- `build/install.ps1`: `-Indexer` no longer calls `Start-Service` (`--install` starts it); its texts say the service starts by itself.
- `core/crates/cabinetos-indexer/src/service.rs`: `Registration` and `registration()` (pure: automatic, delayed flag, LocalSystem, command line), `install()` creates the service with START and QUERY_STATUS access, sets the delayed start through `windows-service`'s `set_delayed_auto_start`, starts it, waits up to 20 s for Running; new error `NotRunning(state)`; a unit test reads the registration. `main.rs`: help and message texts. `tests/elevated.rs` (ignored, CI only): checks `sc qc` (`AUTO_START  (DELAYED)`) and `sc query` (`RUNNING`) instead of calling `sc start`.
- `ui/livecheck/vm-indexer-check.ps1` and `vm-indexer-guest.ps1` (new): the VM check; `vm-install-guest.ps1`: step 2 fails when the setup installed a `.pdb`.
- Docs: `release.md` (what a release is, "Sizes", "The symbols", the build steps, the setup file, the install table, "The indexer service", known gaps, publish command with the symbols zip), `dev-setup.md` (the components), `diagnostics.md` (the crash file's `backtrace` row), `indexer.md`, `ui.md`, `PLAN.md` (a status line per unit, open question 4), `CHANGELOG.md` (two lines), ADRs 0019 and 0020, the ADR index, ADR 0009's status line.

## Crash trace, with and without the symbols (unit 2)

Measured on a copy of the release folder, `cabinetos-core.exe --self-test-panic`:

- **Without the `.pdb`:** `backtrace` is 14 `<unknown>` frames (only `BaseThreadInitThunk` and `RtlUserThreadStart` have names). `location` (file, line, column of the panic), the message, the thread and the recent log lines are still in `crash-*.json`. The Rust programs record their `.pdb` by file name alone (checked in the binary), so only a file next to the `.exe` counts.
- **With the symbols zip unpacked next to the programs:** 25 frames with function, file and line, for example `cabinetos_core::self_test_panic at .\core\crates\cabinetos-core\src\main.rs:154`.
- **The window** (an exception thrown inside `CabinetOS.Core.dll`, in a pwsh .NET 10 host): without the `.pdb` the methods are named, with no file and line, and `location` is null; with it, file and line.

So the handout's expectation that a trace without symbols still names the function holds for the window only. The window itself was not crashed on purpose (no window runs on this PC).

## Checks, and where they ran

| Check | Result | Machine |
|---|---|---|
| `cargo build --workspace`, `cargo test --workspace` (897 passed, 0 failed, 6 ignored; 896 before plus my new test), `cargo clippy --workspace --all-targets -- -D warnings`, `cargo fmt --all -- --check`, `cargo deny check` (advisories, bans, licenses, sources ok), `cargo build --release --workspace` | all green | this PC |
| `dotnet build CabinetOS.sln -warnaserror`, Debug and Release | 0 warnings, 0 errors | this PC |
| fast window tests | 1339 tests: 1279 passed, 60 end-to-end skipped, 0 failed | this PC |
| `build\check-scripts.ps1` | every script parses in PowerShell 7.6.6 and Windows PowerShell 5.1 | this PC |
| full `build\release.ps1` (PowerShell 7) | ran three times (before, after, and on the clean tree); sizes above; no `.pdb` and no machine-learning file in the release zip (checked by listing it) | this PC |
| the guard, with fake `onnxruntime.dll`, `Microsoft.Windows.AI.Text.Projection.dll` and `sub\DirectML.dll` in the folder | `release.ps1 -PackageOnly` stopped with the message, the zip was not touched | this PC |
| end-to-end suite (`remote-tests.ps1 -EndToEnd`, this branch) | run 1: 1338 of 1339 (4 min 30 s), the failure `ContextMenuEndToEndTests.The_menu_is_edited_inside_the_menu_and_saved_through_the_core`; run 2: 1337 of 1339 (4 min 28 s), failures that test and `ColumnViewEndToEndTests.Three_levels_open_close_...`. Alone, the context-menu class passed 8 of 8 and the column-view class 1 of 1. Both are the known laptop timing tests (terminal-unit5 report); no end-to-end run on this PC | laptop |
| live check (`remote-livecheck.ps1`, this branch) | attempt 1 stopped at section 19b ("CabinetOS is not the foreground window ... in front: OpenWith", 12 True, 0 False, a stray Open with window); attempt 2: 262 True, 0 False, exit 1 only because the strict panel goal missed by one frame (1 of 270 with UI work over 33 ms); **attempt 3: 262 True, 0 False, exit 0, panel goal met (332 frames, 0 over 33 ms), 8 min 56 s** | laptop |
| live check in the VM (`vm-livecheck.ps1`) | **not verified.** Attempt 1 stopped after 11 s: "CabinetOS is not the foreground window ... in front: WindowsTerminal 'Windows PowerShell'" (the window itself started and showed its panes in the VM's screenshot). Attempt 2, after a VM restart (`-Restart`): the run started at 23:03 and moved in bursts (3 True, 0 False by 23:19, VM clock) with 10 to 15 minutes of silence between lines, guest control answering "Error starting guest session (current status is: starting)"; stopped by the planning session's instruction after about 40 minutes. The VM was powered off cleanly (the power button was ignored, so `poweroff` after 75 s). | VM |
| `vm-install-check.ps1` | not run (the VM was never healthy enough; the same VM state would stop it) | none |

## The VM check of unit 3, attempts

1. Guest control's session token: medium integrity, Administrators "deny only", `EnableLUA=1`, `ConsentPromptBehaviorAdmin=5`. `Register-ScheduledTask -RunLevel Highest`: "Access is denied". (Two probes, the first timed out while the VM was busy.)
2. `vm-indexer-check.ps1` run 1: install phase answered `needs-elevation`, exit code 2, `DONE-indexer.md` with the manual step (21:53).
3. Dry run with no service installed (`-AfterInstall -SkipInstalledCheck`): the restart worked at 21:59 (down 21:59:34, back at run level 3 at 22:00:00), then the check phase hung: the helper `Sc` was shadowed by PowerShell's `sc` alias (Set-Content) and waited for input. Fixed (renamed `Invoke-Sc`).
4. Two more dry runs: guest control stuck ("Error starting guest session"); the script got a retry (20 minutes), a catch that still writes the DONE file, `-Restart`, and a `controlvm reset` fallback for `shutdown /r` (22:30:59 reset, back at run level 3 at 22:31:24). With the helper fixed, the guest's check phase ran and failed as it must with no service (22:57 to 22:58).

The install phase and the success branch (service `RUNNING` after a restart, process younger than the boot, pipe, log) never ran. What was not done on purpose: no change to the VM's UAC or security settings, no scheduled-task trick (it was refused anyway), no keystrokes into the UAC prompt.

## Decided without you (what; because; undo)

- **Unit 1 is the guard, the docs and the measurement, not a change of references.** Because 6fdc435 already did the per-component references, and release folders since had no ML library. Undo: remove the guard block of step 5b.
- **The guard also names `*onnxruntime*`** (not only `onnxruntime*`), because the whole package brings `Microsoft.ML.OnnxRuntime.dll` too. Undo: the pattern in step 5b.
- **Units 1 and 2 are one commit.** Because both are the same step of the script and one sizes table. Units 3 and 5 are separate commits. Undo: none needed.
- **Every `.pdb`, also the window's two small managed ones, goes into the symbols zip, and the setup also excludes `*.pdb`.** Because the handout said all `.pdb` files, and one rule is easier than two. Undo: step 3 and 5b of `release.ps1`, `Excludes` in `setup.iss`.
- **`-PackageOnly` leaves the symbols zip of the build before as it is** (signing does not change a `.pdb`). Undo: `release.ps1` step 5b.
- **`windows-service`'s `set_delayed_auto_start` instead of the `windows` crate with `unsafe` blocks.** Because the indexer already uses `windows-service`, which calls the same `ChangeServiceConfig2W` with `SERVICE_CONFIG_DELAYED_AUTO_START_INFO`; the request to the service manager is the same, and no `unsafe` or new dependency is needed. Undo: call `ChangeServiceConfig2W` through `windows` with a `// SAFETY:` block in `install()`.
- **`--install` waits up to 20 s for Running and exits with an error when it is not Running.** Because "starts it once" should not report success for a service that stopped at once. The service stays registered. Undo: drop the loop in `install()`.
- **`--install` does not migrate an existing manual-start service** (it fails as before; `--uninstall` then `--install`). Because no released version had one. Undo: change the service's config when it exists.
- **`install.ps1 -Indexer` no longer calls `Start-Service`.** Because `--install` starts the service now. Undo: put the line back.
- **The VM check has a manual elevated step** instead of making UAC weaker. Because the handout said to document it when guest control cannot elevate. Undo: n/a.
- **ADR numbers 0019 and 0020; ADR 0018's sentence "a later unit may add the downloads" is left as it is** (an old record is not rewritten, and the handout said nothing else for unit 5). Undo: n/a.
- **Two laptop reruns of the live check and a rerun of the failing end-to-end classes** (the stop was a stray Open with window; the end-to-end failures are known timing tests). Because a failure that passes alone is not my change (no UI code changed). Undo: n/a.
- **The shared scratchpad:** another agent overwrote my helper `crlf.py` there at about 23:10; I moved to a private folder `scratchpad\a939_phase22`. No repository file was touched by it.
- **The VM was powered off at the end** (it was off when I started). `cabinetos-vm-password.txt` in `%TEMP%` (left by the stopped live check) was deleted.

## Needs you

- **The VM restart check of unit 3.** One manual step in the VM, then one command here: [release.md](../../release.md), "The indexer service". Until then "a restart of the VM brings an installed indexer service up by itself" is not shown in a real run. The VM live check of the trimmed build and `vm-install-check.ps1` also have to run on a settled VM (both stopped on the VM's state).
- **Publishing:** the GitHub Release should carry the symbols zip and its hash beside the zip and the setup (the publish command in `release.md` has them).
- **The desk card** "Development Plan (mirror of the repo plan)" must be refreshed from `docs/PLAN.md` (I did not touch the desk).
- **`CHANGELOG.md`:** `release.ps1` takes the notes from the `## [0.1.0]` section when it exists, and today's lines are in `## [Unreleased]`; the "Publish" step 1 of `release.md` covers the rename before publishing.

## Seen on the way, not changed

- **A Rust crash trace is unreadable without the symbols zip**, and the `<unknown>` frames carry no addresses. Recording the module and each frame's offset in `crash-*.json` would let the symbols resolve it later. Also `cabinetos_core.pdb` is 116 MB; split debug information or fewer crates with line tables may shrink the zip a lot.
- **`Microsoft.Windows.SDK.NET.dll` is 26 MB (6.7 MB zipped)**, the biggest managed file of the release; a trimmed Windows SDK projection may remove most of it.
- **`vm-livecheck.ps1 -Restart` has no settle wait** (unit 6's report said so): a live check right after a restart crawls and then hangs. `vm-indexer-check.ps1`'s settle wait often ends at its limit because the CPU-load answer comes too late right after a boot.
- **Guest control and the live check in the VM were unstable all evening** ("Error starting guest session", a Windows Terminal window in front); a VM restart is the only cure, and it makes the next run slow.
- **The laptop live check stopped once on a stray "Open with" window** (section 19b) and once missed the strict panel goal by one frame while uTorrent ran there.
