# Phase 19c, the compact overlay: the coder's hand-back

Item 5 of the creator's card "New features I want to build Files Type
Sprint", built in the sleep-mode run of 2026-10-01 ([PLAN.md](../../PLAN.md),
Phase 19c) by a coder on Sonnet in its own worktree. The plan: the command
`view.toggleCompactOverlay` (Ctrl+Alt+Up) makes the window a small
always-on-top drawer with one pane, no sidebar and no dock, its size saved
as `ui.compactOverlay`; toggling back restores everything without writing
the layout settings. The hand-back follows, as reported. The planning
session's checks on the main checkout: main pulled at 6addc78; the core's
part was in the five checks of the 19b pull (803 tests); the window's build
with warnings as errors; the fast test run. The live check with real keys
runs at the end of the night.

---

Phase 19c, the compact overlay: built, verified and pushed to main (fetch and rebase onto origin/main, then `git push origin HEAD:main`; no force). Not verified: the live check step was written and parsed but not run (your rule), so the real-key path of Ctrl+Alt+Up is checked only through the chord machine.

COMMITS ON MAIN (oldest first)
- 501493c core: view.toggleCompactOverlay (View, UI, no context, `ctrl+alt+up`) and `ui.compactOverlay` (width/height 240..4000, null = 480x640). Schema regenerated; the command count went 109 to 110 and is 111 with 19b's toggle.
- b75d3f1 docs: config.md row and keybindings.md row with a note.
- 6a108b8 window: ui/CabinetOS/MainWindow.Compact.cs, CabinetOS.Core/Presentation/CompactOverlayLayout.cs, UiSettings.CompactOverlay, and small guards in Commander, Tabs, QuickOpen, Preview, Terminal, Rail, Metrics, xaml and xaml.cs.
- f013c79 tests: CompactOverlayTests (fast) and CompactOverlayEndToEndTests (opt-in).
- 65521b0 tests: the key path of ctrl+alt+up through KeyNames.ComboFor and ChordStateMachine.
- f93525d live check: compact overlay step, before `Step "close"`. Live gets `Topmost()` (GetWindowLongPtrW, WS_EX_TOPMOST).
- 04ffcb7 docs: ui.md "Compact overlay" after "What the window remembers", plus the remembered-state table row, the live-check list and the CHANGELOG line.
- 6addc78 tests: hand edit of the file while the drawer is on.
The rebase conflicts were UiSettings.cs and ui.md; I kept both sides.

VERIFIED
- Core, from core/: build, `cargo test --workspace` (83 suites, 803 passed, 0 failed), clippy -D warnings, fmt --check and cargo deny check all clean. The release core was rebuilt in the worktree.
- Window: `dotnet build CabinetOS.sln -warnaserror` gave 0 warnings and 0 errors. `dotnet test --no-build` gave 1028 total, 1002 passed, 26 skipped (the opt-in end-to-end tests).
- Whole suite with `CABINETOS_UI_E2E=1` and my release core: 1028 of 1028 passed, 0 skipped, 0 failed (1m 52s), frame-timing tests included. The 4 compact end-to-end tests were re-run alone after the last test change: 4 of 4 passed.
- Mutation check: with IsAlwaysOnTop not set, the topmost assertion failed in 3 tests. With the sidebar saved through `SetSidebarOpen(false)`, the test failed with "ui.sidebar while the drawer is on". Both faults were removed afterwards and the diff was clean.
- Evidence the tests assert:
  - Entered: width 480, height 640, saved false, previous size 1215x708 DIPs at pixel (384,188). Left: 1215x708 at (384,188). The window rectangle and the topmost style were read through EnumWindows, GetWindowRect and GetWindowLongPtr, not taken from the log. Topmost was true while on and false after.
  - Config with `compactOverlay 400x500`: entered 400x500 with saved true, and the window rectangle matched.
  - After a resize to 520x700 in steps: one "compact overlay size saved" line (520x700) and the file agreed. The second entry opened at 520x700. A hand edit to 440x560 made the window follow at once, with no second save.
  - The file's dualPane, sidebar and dockSize were read while the drawer was on and again after; they were unchanged.
  - With a terminal shown, the dock came back; the refused toggles and the terminal each gave a notice and wrote nothing.
- Screenshots viewed at 480x640 and 360x480: one pane, no overlap, and the status bar reads "Compact overlay · Ctrl+Alt+Up leaves it". `build/check-scripts.ps1` parses in PowerShell 7.6.6 and in Windows PowerShell 5.1. The Live C# block was compiled in both.
- No window of mine is left on the desktop.

DECISIONS THE PLAN DID NOT COVER (what — because — undo)
1. The drawer opens at the window's current top-left, moved only as far as keeps it on the work area (`CompactOverlayLayout.Place`). The plan names no place, and this shrinks the window toward its corner. Undo: change the two Place calls in EnterCompactOverlay.
2. width/height are the window's outer size in DIPs (AppWindow.Size / scale). The saved value then equals what a resize shows and round-trips exactly. Undo: use client size.
3. The minimum height stays 480; only the width drops to 360 as planned. A saved height under 480 is raised to it, although the core accepts 240..4000. Undo: lower PreferredMinimumHeight in EnterCompactOverlay and `CompactOverlayLayout.MinHeight`.
4. Commands that would bring back a pane, the sidebar or the dock are refused with a status-bar notice, because otherwise they would write ui.dualPane or ui.sidebar. They are: Toggle Dual Pane, Toggle Sidebar, Show Explorer, Show Search, sidebar.locate, terminal show and new, Open in Other Pane, Alt+F2 on the right pane, Ctrl+Right, moving a tab to the other pane, and Quick Open in the other pane. One helper, `EnsureDual()`, replaced five copies of "show pane 2 and save it". Undo: remove the `RefuseInCompact` and `EnsureDual` calls.
5. A plugin proposal (preview) needs the second pane. Entering is refused while one waits, and one that arrives in the drawer makes the window leave the mode first. Undo: drop the two guards.
6. The right pane is hidden, not closed (`ApplyDual(false, keepTools: true)`), so its tool tabs survive. Toggling Dual Pane off still closes them. Undo: drop the parameter.
7. A maximized or minimized window is restored before entry and maximized again on leaving. Undo: delete those lines in Enter and Leave.
8. The rail, the Dual and Terminal top-row buttons and the workspace pill are hidden in the drawer. The pill ran under the right-hand buttons at 360 px. Undo: ApplyCompactLayout.
9. In the status bar the mode text replaces the layout name, with the key as bound now. The UTF-8 text and the palette keycap are hidden, the gaps are 6 px, and the selection text is cut to 90 px (hidden under 420 px), so the text fits 345 to 465 px. I added `x:Name="EncodingText"` in MainWindow.xaml. Undo: ApplyCompactLayout and FitCompactChrome.
10. An edit of the file while the drawer is on resizes it at once (Article 6). A resize still waiting is saved when leaving. A difference of 1 DIP or less counts as our own echo. Undo: `ApplyCompactSettings`.
11. Extra log fields: entered has previous_width, previous_height, previous_left, previous_top, dual, sidebar and dock; left has left, top, dual, sidebar, dock and maximized. Also new lines: "size saved", "size not saved", "follows the configuration". This lets the test compare the numbers without hard-coding 1200x700, because `size:` sizes the content and the outer size is larger.
12. The live check section has no number (19b also adds one). The evidence lines are the two asked for, plus four: the status bar text, the layout restored, the file's layout keys, and that a key press really produced an "entered" line.

NOT DONE, AND KNOWN LIMITS
- I did not run ui/livecheck. The real Ctrl+Alt+Up delivery is unverified. The rule in ChordStateMachine that an ordinary key does nothing while a text box has the keyboard applies, so press Esc first; this is in the docs. Some Intel graphics drivers use Ctrl+Alt+arrows to turn the screen; the docs say to rebind in that case.
- A resize made within 500 ms before the window is closed is not saved, because CloseWindowAsync has no flush. Leaving the mode does flush.
- Refusal notices are mostly cut off at 465 px because the notice column is narrow. The full text is in the log.
- The dock end-to-end test needs pwsh and WebView2 (opt-in only). A theme change while the drawer is on resets the status-bar gap to the theme's.

WORTH DOING LATER (out of scope)
- A notice that stays readable in a narrow window, for example by taking the layout text's room while it shows.
- Keeping the window's place per monitor when the drawer is dragged to a screen with another scale.
