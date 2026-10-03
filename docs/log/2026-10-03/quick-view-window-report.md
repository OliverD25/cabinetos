# Quick View, the window's part (Phase 25, step 2, items W1 to W9 of ADR 0023)

Coder report, Opus 5.5, 2026-10-03. Branch `worktree-agent-ad726eae3ae77782c`,
from main's `feb4df4` with the core's branch (`worktree-agent-a3d9117b76f4ae7e0`,
C1 to C6) and the laptop's lock branch (`worktree-agent-afa0d5852986f5d83`)
merged in. The creator was away; every decision the ADR left open is below as
a `what - because - undo` line. Nothing opened a window on this PC; the
window tests and the live check ran on the Omen laptop only.

Constitution: Article 1 (Zero-Compromise Performance) is why the panel never
waits on a page and the keyboard never leaves the list; Article 10
(Zero-Bloat Foundation) is why no viewer and no pack name is in the window;
Article 11 is why a viewer is a Tool Extension page that talks to the window
only; Article 7 is why every action is a command; Article 6 is why
`quickView.viewers` is reachable from the panel, the palette and the file.

## What was built

| Item | Commit | What it is | Its tests |
|---|---|---|---|
| W1, the logic | `6f6b3ea` | `CabinetOS.Core/QuickView/`: the matcher over the core's table (`QuickViewTable`), the token state machine with the 120 ms rest, the 3 s, 5 s and 30 s waits and the stop count (`QuickViewSession`), the pool of three (`QuickViewPool`), the key rule of decision 3 (`QuickViewKeys`); protocol 21's records in the window | `QuickViewTests` (22): a late report with an old token is dropped; a key bound in `filesView`, everywhere or as a chord's first half is never granted; Space, Esc, Enter, Up, Down, Tab, Ctrl, Alt, Win never are; three stops in 60 s turn a viewer off and spaced stops do not; `ProtocolTests` (66 requests, the new replies and the event against the core's schemas) |
| W2, the messages | `fd9492e` | `QuickViewMessages` in `ToolMessages.cs`: the page's `ready`, `quickview-shown`, `-failed`, `-keys`, `-render`, `-system-preview`, and the refusal of `command`, `subscribe`, `unsubscribe`; the window's `quickview-show`, `-theme`, `-keys-granted`, `-key`, `-rendered`, `-render-failed`, `-system-preview-failed` | `QuickViewMessageTests` (32): fields, limits (80, 200, 64 keys, 2560), malformed messages dropped, every written message valid against `sdk/tools/quickview-messages.schema.json` (the one copy; not rewritten) |
| W3 to W8 | `99d6dbf`, `81254b6`, `81ee78e`, then the laptop's fixes `e1fa915`, `4365f14`, `a01d888`, `66a6ed8` | The panel (`Views/QuickViewPanel.xaml`), the viewer host (`Services/QuickViewHost.cs`), and `MainWindow.QuickView.cs`: the keys and the overlay rule, the cursor following, the close rules, the folder card, the thumbnails with reads ahead, the offer and its install, the viewer choice; `--dev-tools-dir` to the core; the autoplay argument for every tool environment | `QuickViewEndToEndTests` (6, on the laptop): open and close with the keys and both themes; the walk, Shift+Space, Tab and Enter, the folder card; a PNG's thumbnail before its viewer and a `.xyz` card with no request left; a failing, a hanging and a crashed page; the offer's install; the viewer choice from the panel and the palette. All pass at `66a6ed8` |
| W9, the line and the live check | `99d6dbf`, `ae4af09`, `fbefd86` | `quick view shown` with `card_ms`, `thumbnail_ms`, `full_ms`, `cold`; live check section 25 and step 11a on Shift+Space | the live check on the laptop: both Quick View goals met |
| Documents | `0efefb8` and the report's commit | ui.md "Quick View", tool-extensions.md, diagnostics.md, CHANGELOG, the settings audit, ADR 0023's `plus` | |

The six items W3 to W8 share one session object and one file, so they landed
in one commit with a message that names each.

## The three things the core coder left for this merge

- **Step 11a and the live check.** Step 11a now presses Shift+Space and says
  so; `docs/ui.md`'s Total Commander table has Shift+Space; section 25 is new
  (ADR 4.5: ten opens per file, the first apart, the 90th percentile of the
  warm runs judged).
- **`plus`.** Mapped to the grammar's `equal` (VK_OEM_PLUS), with `shift+plus`
  as `shift+equal`; a press reaches the page as `plus`. One sentence under
  ADR 0023's decision 3 says so - because `equal` is the very key Windows calls
  VK_OEM_PLUS - undo: leave `plus` out of `QuickViewKeys.NamedKeys`.
- **The schema** stays the one copy in `sdk/tools/`; the window's tests read
  it, nothing wrote a second one.

## Decided where the ADR was open (what - because - undo)

- **An off kind's chooser lists every viewer, and the chooser shows when the
  kind is off** - because a user who turned a kind off needs a way back from
  the panel itself (Article 6) - undo: `QuickViewTable.Match` lists only the
  kind's viewers.
- **A key name outside the grammar does not spoil a `quickview-keys` message;
  it is just not granted. A message over a size limit is dropped whole** -
  because a viewer built for a newer window should still get the keys this
  window knows - undo: `QuickViewMessages.Parse` drops on any unknown name.
- **A second `ready` while loading sends the file again** - because a page
  that reloads itself (a crash of its renderer) must not stay blank - undo:
  `QuickViewSession.OnReady` ignores it.
- **"Did not start" still accepts a late `ready`, and becomes "did not
  finish" at 30 s** - because a slow first start on the laptop should still
  end in a picture - undo: drop the late branch in `OnReady`.
- **The `quick view shown` line carries the trace of the command that opened
  the panel** - because the heavy-logging chain of one action then reaches
  it - undo: drop `traceId` in `WriteQuickViewLines`.
- **`quickView.installViewer` and `quickView.chooseViewer` from the palette
  act on the file under the cursor** (the palette closes the panel first) -
  because the overlay rule allows one overlay - undo: hide them while the
  panel is closed.
- **In search results the key shows a notice and opens nothing** - because a
  result row has no folder listing to walk - undo: allow it in
  `ToggleQuickView`.
- **Focus that falls to the window's root goes back to the list** (a
  crashed page's WebView2 leaving the tree drops it there) - because the
  panel closed by itself in the laptop's first run - undo: the root branch
  of `OnQuickViewFocus`.
- **The browser argument `--autoplay-policy=no-user-gesture-required` is set
  for every tool environment, the pane's too** - because the panel and the
  pane share `tool-<id>`, and WebView2 refuses a second environment with
  other options - undo: remove `ToolBrowserArguments` from both hosts.
- **The card's icon is the held icon, plus one 48 px icon request per icon
  key** - the ADR says no request; the held 16 px icon looked blurred at
  card size - undo: drop the request in `ShowQuickViewEntry`.
- **Test-only variables `CABINETOS_QUICKVIEW_LIMITS` (rest, ready, still,
  give-up) and `CABINETOS_CORE_TOOLS_DIR`** - because the waits and the
  offer's install must run in seconds in a test - undo: remove both reads.
- **The Enter test uses a folder** - because opening a file starts a program
  on the laptop - undo: none needed.
- **The crash test kills the page's browser process** (the snapshot step the
  pane and terminal tests use), not the fixture's endless loop - because the
  kill is certain and quick.
- **The read ahead (ADR 4.3) waits until the keys rest for 120 ms** - because
  three thumbnails per step of a held key made the laptop pause 50 to 70 ms
  in full garbage collections - undo: call `ReadQuickViewAhead` from
  `ShowQuickViewEntry` again.
- **A page put to sleep stays awake when the panel shows its viewer again
  within 400 ms** - because a suspend just before the next load held a warm
  thumbnail's frame to 138 ms - undo: drop the `idle` check in `SleepAsync`.
- **A crashed viewer's host is closed without clearing its folder mappings,
  and for 500 ms a focus move out of the list goes back to it** - because
  the cleared mapping threw on the dead browser and WinUI moved the focus to
  the other pane - undo: none advised.

## Measured on the laptop

The live check of `66a6ed8` (run 2026-10-03-2330-d188, ran commit
`66a6ed8`, exit 0), section 25, ten opens per file, the 90th percentile of
the nine warm ones:

| file | viewer | first: card / thumbnail / full (cold) | card worst | thumbnail p90 | full view p90 |
|---|---|---|---|---|---|
| 1-big.png (12 MP) | fixture | 41 / 169 / 601 ms (cold) | 15 ms | 27 ms | 135 ms |
| 2-image.png | fixture | 6 / 6 / 62 ms | 15 ms | 27 ms | 138 ms |
| 3-page.qvtest | fixture | 6 / none / 61 ms | 7 ms | none | 59 ms |
| 4-photo.jpg (12 MP) | none | 9 / 81 / none ms | 16 ms | 30 ms | no viewer |
| 5-notes.txt | none | 5 / none / none ms | 9 ms | none | no viewer |
| 6-unknown.xyz | none | 5 / none / none ms | 14 ms | none | no viewer |

- The first Space of the session with a viewer: 601 ms (ceiling 1500 ms).
- Quick View goal (card 50 ms, thumbnail 100 ms, full view 1 s): met.
- Walk goal (Down held 3 s over 50 images): met; 401 frames, none with UI
  work over 33 ms, one over 20 ms, worst 24 ms; 49 files passed under the
  panel.
- Panel goal: met; 262 frames, none with UI work over 20 ms. Checks that
  answered False: none. The run of `fbefd86` gave the same verdicts.

Before `fbefd86` the same section missed both goals; the three causes and
their fixes are in that commit's message and in the decisions above.

## Checks

At `66a6ed8` unless said otherwise:

- Core, from `core/` (this branch changes nothing there): build 0, clippy
  0, fmt 0, deny 0. `cargo test --workspace` was 101 once: the core test
  `plugins.rs` `a_plugin_proposes_a_preview_and_undoes_it_through_core_requests`
  ("no plugin requester") failed. It passed alone (0) and in a second
  whole run (0), so it is a timing flake of the core, not of this branch.
- Window, from `ui/`: `dotnet build CabinetOS.sln -warnaserror` 0, the same
  with `-c Release` 0, the fast tests 0 (1487 total, 1409 passed, 78 need
  a window).
- Laptop, `remote-tests.ps1 -EndToEnd`: ran commit `66a6ed8`, exit 0, 1487
  of 1487 passed. The run before it (`fbefd86`) had one failure, the crash
  test, fixed by `66a6ed8`.
- Laptop, `remote-livecheck.ps1`: see "Measured on the laptop".
- On the laptop two leftover CabinetOS.exe processes were ended with
  `taskkill /F /IM CabinetOS.exe` while no run held its lock: one from
  another agent's run (pid 13796), one from this agent's run that the
  Claude Code restart cut off (pid 6164).

## Not done, and why

- **The Office or preview-handler viewer is not built, and no hook for it
  either.** A test of a hanging preview handler needs a COM local server
  registered on the test machine; that was not written. A page that asks
  `quickview-system-preview` gets `quickview-system-preview-failed` at once.
- **The full view of a JPEG, a HEIC photo and an H.264 video is not
  measured.** No viewer for JPEG files exists yet (the viewer pack is step 3
  of Phase 25), and the laptop has no encoder for the other two.
- Nothing ran on this PC that opens a window.
