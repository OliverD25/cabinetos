# Terminal unit 3: the split mirror (2026-10-02, night)

Context: unit 3 of six of the Integrated Terminal Subsystem Sprint (Phase 21
in [PLAN.md](../../PLAN.md)). Ctrl+\ in the terminal splits the Tool Dock
under the two file panes: the left pane's sessions under the left pane, the
right pane's under the right, each half with its own header and tab row, and
the badges in the panes' colours. Written by the Sonnet coder agent in the
worktree branch `worktree-agent-ab43609202ad0fd61`, on the main PC, the night
of 2026-10-02. The window runs ran on this PC behind the creator's consent
file (`wait-for-pc.ps1` said "allowed until 07:30"); the laptop and the VM
were not used.

## Summary

| Item of the handout | Result |
|---|---|
| 1. The command | Done. `terminal.toggleSplit`, `ctrl+backslash`, `when: terminalFocus`, a core seed (116 commands). In a pane the same keys stay `go.root`. The top-row and rail buttons are still a plain toggle of the dock |
| 2. The halves | Done. Two halves side by side, each exactly as wide as its pane and lying under it (within 2 px, measured by the end-to-end tests), a header and a tab row for each, the panes' gap and a line between them. They follow the panes (the window, the sidebar, the second pane coming and going). One pane shown: one half across the dock; the right half returns with the second pane. An empty half shows "Ctrl+` starts a shell for this pane" |
| 3. Persistence | Done. `terminal.split` (camelCase, boolean, `false`) in `cabinetos.json`, written with `set_value` on a toggle, read at start, followed live (the window's own write does not flip it back) |
| 4. The keyboard | Done. Ctrl+` in a pane summons that pane's session and focuses its half; Alt+[ and Alt+] go round the tabs of the half that has the keyboard; Ctrl+Shift+W closes that half's shown tab (the last one leaves the hint, the setting stays); Ctrl+Shift+T opens in the half of the active pane; a click into a half's text gives it the keyboard (the page reports `focused`); nothing else moves it |
| 5. The badges | Done. `terminalLeftBadge` and `terminalRightBadge`, optional palette keys (theme format 3). Left: the accent. Right: the accent's hue turned by 150 degrees at the same lightness (`#60CDFF` gives `#FF607E`) |
| 6. The log | Done. "terminal split" and "terminal half focused" lines; the `terminal-state:` line carries `split`, each half's session, tabs, hint, badge colour and place, and the panes' places |
| Docs | terminal.md, ui.md, keybindings.md, config.md, themes.md, CHANGELOG.md, PLAN.md |

Commits on the branch, oldest first: see "Commits" below.

## How the split works

1. `terminal.toggleSplit` (Ctrl+\ with `terminalFocus`) flips `terminal.split`. The window applies it at once, writes it with `set_value`, and follows the file's `config_changed` (an own write coming back changes nothing, as for `ui.dualPane`).
2. The split holds only while the dock is under the panes (`ui.layout` `classic` or `rail`): `TerminalSplitLayout.Active`. Beside them the dock shows one view and Ctrl+\ says so.
3. `TerminalSplitLayout` (a pure class, 15 tests) decides the halves: one per shown pane, as wide as its pane, from the panes' measured places; with one pane shown, one half across the dock. A half shows the session its pane showed last (the tab's `LastShown`), or the hint.
4. The dock is still one WebView2. The controller sends the page a `view` message (each session on screen with its `x` and `width`, a hint text for an empty half) whenever the tabs or the layout change, and not again when it did not; the page positions each xterm with two CSS variables.
5. The dock's header became `TerminalHalfHeader`; the dock has one for the one view or one per half (a Grid with the left pane's column, the panes' gap and the right pane's column). A half's header has its pane's badge, its own tabs, "+", profiles, caption; the last header has the ×.
6. The window measures the panes (`LeftSide`, `RightSide`, `PanesGrid`) when their size changes and lays the halves out again, so they follow the panes.
7. Which half has the keyboard: the page tells the window which xterm got focus (`focused`); showing a tab (a click on it, Ctrl+`, a new session) sets it too. `TerminalController.Shown` is that half's tab, so Alt+[ and Alt+], Ctrl+Shift+W, paste and Ctrl+Alt+P act on it.
8. Ctrl+Shift+T opens for the active pane (`TerminalSplitLayout.NewTabPane`); a half's "+" and profile menu name their own pane. Closing the only tab of a half leaves its hint; the dock hides only when no session is left anywhere.
9. The badges: the theme's `terminalLeftBadge` (default the accent) and `terminalRightBadge` (default the accent's hue turned by 150 degrees) reach the window as two brushes.
10. Logs: "terminal split" (the halves and the panes in one measure), "terminal half focused", and the `terminal-state:` line with each half's session, tabs, hint, badge and place.

## Commits

| Commit | Subject |
|---|---|
| 4a9ee15 | core: terminal.toggleSplit on Ctrl+\ in the terminal, and the setting terminal.split |
| cd0f432 | window: TerminalSplitLayout, the rules of the split dock, with tests |
| 21f4ab9 | themes: terminalLeftBadge and terminalRightBadge, the panes' colours for the terminal's badges |
| b62de53 | window: the split mirror, the dock's two halves under the two panes, with their keys and badges |
| 9ef938c | tests: end-to-end, the split mirror under the panes, its keys, its setting and its badges |
| 8c755e2 | live check: section 21 splits the dock with a real Ctrl+\ and judges the halves against the panes |
| f72829d | core: rustfmt and clippy for the tests of terminal.split and the badge colours |
| (the commit of this report) | docs: the split mirror, the badge colours, terminal.split, Ctrl+\ in the terminal, and the report of unit 3 |

The handout listed the dock's halves, the keyboard and the badges as three
pieces. The badges came before the dock (the dock's headers use their
brushes), and the keyboard's changes sit in the same files as the halves
(`TerminalController`, `MainWindow.Terminal.cs`), so they are one commit: the
repository has no way to stage a file by hunks without an interactive tool.
The docs commit holds this report.

## Changes per file

Core (Rust):

- `core/crates/cabinetos-commands/src/registry.rs`: the seed `terminal.toggleSplit` (116 seeds), a test that the keys are shared with `go.root` by context; `search.rs`: the palette's "terminal" ranking test counts the new command.
- `core/crates/cabinetos-config/src/model.rs`: `TerminalConfig.split` (`false`), tests. `sdk/config/cabinetos.schema.json`: regenerated.
- `core/crates/cabinetos-core/tests/config.rs`, `shell_requests.rs`: 116 commands; `terminal.split` read, `set_value` and its `config_changed`.
- `core/crates/cabinetos-protocol/src/theme.rs`, `schema.rs`: `Palette.terminal_left_badge` and `terminal_right_badge` (optional), `THEME_FORMAT` 3; `core/crates/cabinetos-themes/src/lib.rs`: a test. `sdk/themes/theme.schema.json`, `sdk/protocol/event.schema.json`, `response.schema.json`: regenerated.

Window (C#):

- `ui/CabinetOS.Core/Terminal/TerminalSplitLayout.cs` (new): the rules. `TerminalPageMessages.cs`: `view`, `focus` with a session, `focused`.
- `ui/CabinetOS.Core/Settings/UiSettings.cs`: `TerminalSplit`. `Protocol/Replies.cs`, `Themes/ThemeLook.cs` (`Argb.RotateHue`), `Themes/ThemeMapper.cs`: the two badge brushes. `ui/CabinetOS/App.xaml`: their defaults.
- `ui/CabinetOS/Services/TerminalController.cs`: the split state, `Halves`, `ShownIn`, `SetSplit`, `PostView`, the half that has the keyboard, the tab cycle and the close in the split.
- `ui/CabinetOS/Views/TerminalHalfHeader.xaml(.cs)` (new, the header moved out of the dock), `ToolDock.xaml(.cs)`: one or two headers, the columns, the divider.
- `ui/CabinetOS/MainWindow.Terminal.cs`: the command, the setting, the layout, the summoning in the split, the logs and the state line; `MainWindow.xaml.cs`: one line in `ApplySettings`.
- `ui/CabinetOS/Assets/xterm/terminal.js`, `terminal.css`: `view`, the hint, `focused`, the padding as CSS variables.
- Tests: `TerminalSplitLayoutTests.cs` (new), `TerminalTests.cs` (page messages, the setting), `ThemeTests.cs` (badges, the hue turn), `ChromeKeyboardTests.cs` (the header file moved), `TerminalEndToEndTests.cs` (two new tests).
- Live check: `ui/livecheck/livecheck.ps1`, section 21 (7 new lines).

Docs: terminal.md ("The split mirror"), ui.md, keybindings.md, config.md, themes.md, CHANGELOG.md, PLAN.md.

## Tests and runs

| Check | Result |
|---|---|
| `cargo build --workspace` | passed |
| `cargo test --workspace` | 843 passed, 0 failed, 6 ignored (unit 2 ended at 841) |
| `cargo fmt --all -- --check` | clean (after `cargo fmt` on three test files of this unit, f72829d) |
| `cargo deny check` | advisories ok, bans ok, licenses ok, sources ok |
| `cargo clippy --workspace --all-targets -- -D warnings` | **fails on code this branch did not touch**, byte for byte main's: `cabinetos-terminal/src/report.rs` lines 55 to 61 (four `clippy::match_same_arms`, "these match arms have identical bodies") and `cabinetos-cli/tests/term.rs` (six `cloned_ref_to_slice_refs` and one `Err(_) matches all errors` at line 417). The two redundant closures of this unit's own theme test were fixed (f72829d). With `-A clippy::match_same_arms -A clippy::cloned_ref_to_slice_refs` the only error left is the `Err(_)` one. I did not change unit 2's files |
| `cargo build --release --workspace` | passed |
| `dotnet build CabinetOS.sln -warnaserror` (Debug and Release) | 0 warnings, 0 errors |
| the fast window tests (no window) | 1279 tests, 1226 passed, 0 failed, 53 end-to-end skipped (1237 at unit 1's end, 1254 at unit 2's) |
| the terminal end-to-end tests (`CABINETOS_UI_E2E=1`, this branch's release core) | 8 of 8 passed (2 min 53 s); the two new ones: the split, its setting, a second window, one pane, the hint, the badges; and the keys in the split |
| the full window suite with the end-to-end tests | 1279 of 1279 passed (3 min 29 s) |
| `build\check-scripts.ps1` | every script parses in PowerShell 7.6.6 and Windows PowerShell 5.1, no error |
| the live check, `run-unit3-0535.txt` (a fresh Release build, from Windows PowerShell, behind `wait-for-pc.ps1` and the countdown) | 223 True, 1 False, 8 min 13 s: "21: Ctrl+Shift+V pasted the command and Enter wrote the file: False". The window's log says "The clipboard could not be read" right after the page asked: another program held the clipboard. The seven new lines of section 21 all say True |
| the live check, `run-unit3-b.txt` (the same build, with the step's final form) | **224 True, 0 False, exit code 0, 8 min 8 s**, the scroll goal met (300 frames, none over 20 ms, worst 17.3 ms) |

The seven new lines of section 21 (`run-unit3-b.txt`): Ctrl+\ in the left pane
ran `go.root` and no `terminal.toggleSplit`; Ctrl+\ in the terminal ran
`terminal.toggleSplit` and split the dock; the halves sit under the panes as
the log says (halves `0:0:888.7|1:895.7:887.7`, panes `-1:888.7` and
`895.7:888.7`, in pixels from the page's left edge); the left half shows
session 1 and the right half session 2; each half has its own header in UI
Automation; the setting is in `cabinetos.json`; Ctrl+\ again showed the one
view and wrote false. The worktree has no built Agent extension, so section
14 prints WAITING and its lines are not counted (main's run of unit 2 had
223 True with it).

Also run by hand, to see the layout: a window with the snapshot steps
(`terminal:`, `key:ctrl+backslash`, `shot:`), once with the setting on at
start (which toggled it off, as it should) and once off: both halves with
their sessions, the badges in two colours, the hint in the empty half, the
cursor of the half without the keyboard hollow.

## Decided without you

Each line: what, because, how to undo.

- **One WebView2 for both halves, a `view` message to the page.** Because two WebView2s would double the keyboard hand-over (one page, one owner of the keys, the code the other coder was changing) and the crash and reload paths. Undo: a WebView2 per half in `ToolDock` and a second controller page; large.
- **The halves are exactly as wide as their panes, with the panes' gap and a 1 px line between them**, not a divider of their own. Because the brief says the divider follows the panes' and a half must sit under its pane (the tests measure 2 px). Undo: `TerminalSplitLayout.Halves`.
- **The split holds only under the panes.** With `ui.layout` `right` the dock shows one view, the setting stays, and Ctrl+\ says so and changes nothing. Because "under the two panes" cannot be beside them. The other way would be two halves stacked in the narrow dock; not done. Undo: `TerminalSplitLayout.Active` and `ToggleSplitAsync`.
- **One pane shown: one half, the left's, across the dock.** The right pane's sessions keep running and come back with the second pane. Because a single pane is always the left in this window (`ApplyDual` makes it active).
- **A half shows the session its pane showed last, an ended one too until its tab closes.** Because a half that switched at once to another session when a shell ended would move under the user's eyes. Undo: `TerminalSplitLayout.ShownIn` (filter on `Running`).
- **The half with the keyboard is where the xterm has focus** (the page's `focused`), and `Shown` is that half's tab. Because a click into a half's text has to give it the keyboard, and nothing else may move it (Zero-Hijack). Undo: `OnTerminalFocused`.
- **Ctrl+` in a pane whose half shows a session focuses that half** (`ShownPaneFor`), whichever half had the keyboard. Because the summoning rule's "the pane's session is shown" is true for the pane's own half.
- **Closing the last tab of a half leaves its hint and the dock stays**; the dock hides when no session is left anywhere, as in one view. Because the handout says the split setting stays and the half shows the hint.
- **Ctrl+Shift+T binds to the active pane**, as the handout says, also while the other half has the keyboard; a half's own "+" binds to its own pane. Undo: `NewTerminalAsync`.
- **A pane badge in each half's header**, and the tabs in a half leave out their own badge (every tab is the pane's). Because an empty half has no tab to carry the badge. In the one view each tab keeps its badge, in its pane's colour. Undo: `TerminalHalfHeader.BindToPane`.
- **The right badge is the accent turned by 150 degrees**, not a fixed colour of the design's palette. Because it fits any accent and cannot be close to it. For the default blue it is a pink-red, `#FF607E`; the creator may prefer another (a theme can name its own, or the constant `ThemeMapper.RightBadgeHueShift` changes).
- **The two tokens are optional palette keys, and the theme format is 3.** Because every palette key is required, so required new keys would refuse every existing theme; the format number says which cores know them. Undo: drop the two fields in `theme.rs` and the mapper's lines.
- **The hint is drawn by the page**, not by XAML over the WebView2. Because the page already owns that area and nothing has to lie over a WebView2.
- **`terminal.toggleSplit` refuses in the compact overlay** (`RefuseInCompact`), as the other terminal commands do.
- **`{"split": true|false}` as an argument** sets the split instead of flipping it (a palette or a script may want it). Not in the handout; two lines.
- **The dock and the keyboard are one commit** (see "Commits").
- **`ChromeKeyboardTests` now reads `TerminalHalfHeader.xaml(.cs)`** where it read `ToolDock.xaml.cs`: the buttons moved with the header.
- **The live check's step hands the keyboard to the pane with Ctrl+`**, not a click from the terminal, so it does not depend on the click-focus fix of the other branch.

## Needs you

- **How it looks.** The line between the halves, the hint's colour and place, the pane badge at the start of each half's header, and the pink-red right badge of the default theme. The live check keeps a screenshot of the split, `21-split-live.png`, in its shots folder (`%TEMP%\cabinetos-ui-test\live-shots`).
- **Stacked halves beside the panes** (`ui.layout` `right`)? Today it shows one view there.
- **A visible mark for the half that has the keyboard** (the cursor of the other half is a hollow box now; nothing else shows it). Not added.

## Seen on the way, not changed

- **Clippy `-D warnings` fails on unit 2's code** (the table above has the files, lines and lints). It is byte for byte main's, so main fails it too with the toolchain that runs here (rustc 1.98.1 of 2026-09-01, clippy 0.1.98); why unit 2 and the planning session saw it clean, I do not know. The fix looks small (`#[expect]` or merged arms in the scanner's match; the lints' own suggestions in the CLI test); I left it, since it is unit 2's code and out of the handout.
- **The live check's Ctrl+Shift+V check** failed once in the first run (`run-unit3-0535.txt`): the window's log says "The clipboard could not be read" (another program held the clipboard); the window's paste code is untouched. The second run is in the table above.
- **The worktree has no built Agent extension** (`sdk\extensions\agent\plugin\plugin.wasm`), so section 14 prints WAITING and a few lines are not there, as in the unit 2 report's run (218 lines there against 223 on main).
- **`docs/log/2026-10-02/README.md` and `docs/PLAN.md`** also changed on main (the click-focus fix): this branch's row and status line go at the same places, so the merge will need a hand to keep both.
