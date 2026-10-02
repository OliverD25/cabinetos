# Two small fixes from unit 6: short paths in the plugin host, and the icon of CabinetOS.exe (2026-10-02, afternoon)

Context: two findings that terminal unit 6 left open
([terminal-unit6-report.md](terminal-unit6-report.md), "Seen on the way"),
fixed in the worktree branch `worktree-agent-a85cb9ee10f883b8d` by the
Sonnet coder agent on the main PC, while the creator was away. Nothing was
pushed or merged. The window runs on this PC ran behind the consent file
(`wait-for-pc.ps1` said "allowed"); the laptop and the VM were not used.

## Summary

| Item | Result |
|---|---|
| 1. The plugin host compares paths as text | Fixed in commit 39705c8. A short 8.3 path and its long form are now one folder for `listing_opened` and for `fs:watch`; a root written short in `plugin.json` counts too. 10 new tests |
| 2. `CabinetOS.exe` has no icon | Fixed in commit 31babbe. `ui/CabinetOS/Assets/CabinetOS.ico` is committed and embedded (`ApplicationIcon`); `build/make-icon.ps1` makes it; `release.ps1` ships it |

## Fix 1: the cause and the fix

- **Cause.** `cabinetos-plugins` judged a path against a plugin's roots by lower-casing both and comparing the text (`listing_opened` in `lib.rs`, `under_a_root` in `watch.rs`). `C:\Users\CABINE~1\...` is not text under `C:\Users\cabinetos`, though it is the same folder.
- **Fix.** `cabinetos-fs` has a new function `long_path` ([long_path.rs](../../../core/crates/cabinetos-fs/src/long_path.rs); `GetLongPathNameW`, one `unsafe` block with its `// SAFETY:` comment, in a crate that talks to Windows). The plugin host keeps each root in its long form from the moment the plugin starts (`prepare`), and puts a path in its long form before it compares.
- **`listing_opened`.** The path as the pane gave it is tried first, so everything that worked before is unchanged. When only the long form lies under a root, the plugin is told the long form: it is the only one its sandbox can open, as the roots it is given are long. The lookup is skipped when no active plugin has a root (a default install has no plugin at all, Article 10).
- **`fs:watch`.** The watch's key is the long form in lower case, for the root check, for "a folder watched twice is one watch" and for `unwatch-folder`. The folder the plugin gets back in `folder-changed` is the one it named.
- **A path that does not exist yet.** The longest part that exists is resolved, the rest follows as written. A file a plugin is about to create under a short-named folder is judged by that folder; a path outside every root stays outside.

## Fix 2: the cause and the fix

- **Cause.** Only `release.ps1` made a `.ico` (`Write-IconFile`), and only for the setup's shortcuts and the Settings > Apps entry. `CabinetOS.csproj` had no `ApplicationIcon`, so the file itself showed Windows' generic program icon.
- **Fix.** `Write-IconFile` moved into `build/make-icon.ps1` (the same method: the design's 16, 24, 32, 48 and 256 px cuts, each kept as the PNG it is). Its output is committed as `ui/CabinetOS/Assets/CabinetOS.ico` (52,892 bytes; the same bytes in PowerShell 7 and in Windows PowerShell 5.1). `CabinetOS.csproj` embeds it. `release.ps1` copies the committed file and calls `make-icon.ps1` only when the file is missing, before the build.
- **`.gitattributes`** already has `*.ico binary`; no change.

## Checks

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 896 passed, 0 failed, 6 ignored (886 before; the 10 new tests: 6 `long_path` in `cabinetos-fs`, 2 in `sandbox.rs`, 2 in `tests/host.rs`) |
| `cargo clippy --workspace --all-targets -- -D warnings` | passed |
| `cargo fmt --all -- --check` | passed |
| `cargo deny check` | advisories, bans, licenses and sources ok |
| `cargo build --release --workspace` | passed (2 min 46 s) |
| `dotnet build CabinetOS.sln -c Release -warnaserror` | 0 warnings, 0 errors |
| `dotnet build CabinetOS.sln -warnaserror` (Debug) | 0 warnings, 0 errors |
| `dotnet test --solution CabinetOS.sln --no-build` (fast) | 1339 total, 1279 passed, 60 skipped (the end-to-end tests), 0 failed |
| The end-to-end suite, `CABINETOS_UI_E2E=1` and `CABINETOS_CORE_EXE` set to this worktree's release core, behind the gate | 1339 of 1339 passed, 4 min 24 s |
| `build\check-scripts.ps1` in `pwsh` 7.6.6 and in Windows PowerShell 5.1 | exit 0 in both; `make-icon.ps1` parses in both |

The new host tests were checked to fail without the fix: with `long_form` made a no-op, both
`a_short_spelling_of_a_folder_is_the_folder_for_a_watch` and
`a_root_written_in_its_short_form_is_the_long_folder` failed with "is not under a folder plugin.json names for fs:watch", and the original
file was put back. The 8.3 tests need a volume that keeps short names (`%TEMP%` on C: here does); on a volume without them they print "skipped" and pass.

## What the icon looks like

- The icon inside the built Release `CabinetOS.exe`, read with `PrivateExtractIcons`: all five sizes (16, 24, 32, 48 and 256 px) come out, each with blue (the folder), dark (the terminal tile) and green (the prompt) pixels. `ExtractAssociatedIcon` gives a 32 px icon with 577 visible pixels; the generic icon of `cabinetos-core.exe` (no icon of its own) gives a different one with 768.
- The Release window, started once on this PC with the worktree's core and closed again (`CloseMainWindow`), shows in the taskbar as the blue folder with the dark terminal badge. The strip of the screen's bottom edge is `_io\live-check\icon-taskbar.png` (3840 by 120 px; the button is at about x = 2964), an enlarged crop of it is `_io\live-check\icon-taskbar-zoom.png`.

## Decisions (what — because — undo)

- **`long_path` is in `cabinetos-fs` with `GetLongPathNameW`, not `std::fs::canonicalize` in the plugin crate.** `canonicalize` follows links (a junction inside a root would move paths outside it, a change nobody asked for), returns `\\?\` and `\\?\UNC\` forms that need converting back, and fails for a path that does not exist yet. `GetLongPathNameW` only spells out short names. Undo: remove `long_path.rs`, its export, and the three calls in `sandbox.rs`/`lib.rs`.
- **A path that does not exist is resolved up to its longest existing part, and the rest keeps its text.** A text-only fallback would still refuse a file the plugin is about to create under a short-named folder. For a path with no short name the result is the text, as the handout asked. Undo: return the input when `GetLongPathNameW` fails (`long_path` in `long_path.rs`).
- **Case is left as written.** `GetLongPathNameW` does not change it (seen here), every caller compares without regard to case, and nothing a plugin sees changes except a short name becoming long. The doc says the case of the result is not promised.
- **Roots are kept in their long form (`prepare`).** The plugin sees a root as `/C:/...` in `activate` and opens files through that name, so a root written short in `plugin.json` must be shown long, and a path told to the plugin must be under it. Undo: push `root` as it was in `prepare`.
- **`listing_opened` tries the text first, the long form second, and tells the plugin the form that matched.** Every path that worked keeps working byte for byte; the plugin gets the long form only when the short one did not match. Undo: `told_path` in `sandbox.rs` and its call.
- **`listing_opened` skips the lookup when no active plugin has a root.** The long-form lookup costs a few directory reads and a default install runs no plugin (Constitution Article 10, Article 1). Undo: the `any_reader` block.
- **`fs:watch` keys by the long form but reports the folder as the plugin named it.** A plugin may keep its rules by the path it passed in; changing what comes back would break that. Undo: `key_of` in `watch.rs`.
- **`under_a_root` moved from `watch.rs` to `sandbox.rs`,** with its test, because two files now share it. Undo: move it back.
- **The host tests make short names with `cmd /c for %I in (...) do @echo %~sI`,** so `cabinetos-plugins` stays free of `unsafe` and of the `windows` crate. Undo: none needed.
- **`make-icon.ps1` runs in Windows PowerShell 5.1 as well,** with no `#Requires`, because it is small and `check-scripts.ps1` then parses it in both. Undo: add `#Requires -Version 7.2`.
- **`release.ps1` makes a missing icon before the build, not in step 5.** `ApplicationIcon` needs the file when `dotnet publish` runs, so a fallback in step 5 would come too late. Undo: the `$icon` block before "1-2. Build" and the copy in step 5.
- **The commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`,** the line this session's harness gave, where the handout said `Claude Sonnet 5`. Because the harness names the model that wrote them. Undo: none needed; an amend would rewrite history, which needs your yes.
- **Nothing pushed, merged or sent; the desk card was not refreshed.** `docs/PLAN.md` changed, so the card "Development Plan (mirror of the repo plan)" is out of date until a session with the desk refreshes it. That is a call to an outside service, which a coder may not make.
- **The window run did not touch another coder's window.** At 17:29 a Debug `CabinetOS.exe` of the worktree `agent-a21066120fda85fae` started; it was left alone.

## What is left, and seen on the way

- **`ui/livecheck/vm-install-guest.ps1` still gives the VM's live check `%TEMP%` in its long form.** The workaround is not needed after this fix; it is harmless and was left. Run the VM chain once without it to see section 14 pass with the short form, then remove the two lines.
- **The Agent extension's own code still compares text.** `is_under` in `sdk/extensions/agent/plugin/src/paths.rs` judges paths the model names against its roots as text, inside the plugin, so a model that names a short 8.3 path is refused there. The host now hands the plugin long paths (the listing, the watch events), so the plugin's own paths are long. Not changed: it is the extension's rule, and a rebuild of the plugin is a separate job.
- **`release.ps1` was parsed in both PowerShells but not run end to end** (it builds everything and needs Inno Setup for step 8). The icon steps are the committed file, one `Copy-Item` and the fallback, and the hash of the file `make-icon.ps1` writes equals the committed one. A real release would show the icon in the release folder's `CabinetOS.exe`; the icon read above is from the Release build.
- **`docs/PLAN.md`** has a sentence at the end of the last Phase 21 status paragraph saying both are fixed.
