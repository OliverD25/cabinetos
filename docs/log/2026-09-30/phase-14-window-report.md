## The window's parts of Phase 14 (the AI agent extension)

Agent: ac9d2107a90527bf1

Report: The window's parts of Phase 14 (the AI agent extension)

Context: item 3 of the night run (shell agent, worktree branch worktree-agent-ac9d2107a90527bf1). Everything is pushed to main; tip d6e2de7. Item 4 (rail and sidebar) is STARTED, not finished: only its four command seeds are in (see "What is left"). Resume me with SendMessage to continue with item 4.

## What was built (nothing in the window knows an agent, Article 10, except the one event name agent.notice, see decision 9)

Protocol (ui/CabinetOS.Core/Protocol/*): CommandInput/CommandInfo.Input {title?,placeholder?}, CapabilityInfo.Hosts and .Secrets, MarketCapability.Hosts and .Secrets, PreviewOpenedReply (with shared-memory section), JobsStartedReply, PluginEventEvent(plugin,name,payload text), PreviewAppliedEvent, PreviewCancelledEvent, requests PreviewListingRequest, OpenPreviewRequest, PreviewApplyRequest, PreviewCancelRequest, the three error codes.
Core logic (ui/CabinetOS.Core): Listing/ListingLayout+ListingView (preview flag, PreviewRow, Change, Target); Preview/PreviewModel.cs (PreviewLine, PreviewSession, PreviewKeys); Commands/PluginInput.cs + CommandRouter (AskInput, Cancelled); Tools/PluginEvents.cs, ToolMessages.cs (subscribe/unsubscribe/plugin-event/paths-dropped), ToolSubscriptions (max 16); Plugins/PluginModels.cs (CapabilityRow.HostsText, SecretsText, FullDetail); Jobs/TransferText.cs ("steps" job kind).
Window (ui/CabinetOS): Views/PreviewPane.xaml(.cs), MainWindow.Preview.cs, Views/EditorPane (drop catcher), Views/FileRow + FilePane (rows can be dragged, log "row drag started"/"row drag ended"), Services/ToolHost.cs (subscriptions, deliver, paths-dropped), Views/ReviewDialog + PluginsPanel (hosts and secret names), MainWindow.xaml/.xaml.cs/.Metrics.cs wiring, snapshot steps preview:rename|make, preview-key:, agent-event:, drop:, fake-command:.
Tests: CabinetOS.Tests/AgentPartsTests.cs (22 tests), ProtocolTests, CoreClientTests, ListingViewTests, Support/TestSections.cs, EndToEndTests (real core: rows, open by ID, apply, cancel).
Docs: docs/ui.md ("What plugins ask of the window", snapshot steps, env table row CABINETOS_UNDO_DIR), docs/tool-extensions.md, docs/keybindings.md, README counts (core 701, ui 767).
Live check: ui/livecheck/livecheck.ps1 section "14" (real mouse drag of a row onto the Markdown Preview page; the "ask" part is guarded and prints WAITING), Live.Drag/MoveTo helpers.
Commits (item 3 and the coordinator's follow-ups): 401d439, cc46f5f, 2dad54c, 74510e7 (test harness undo folder, its own commit as asked), 80d5ed8 (secrets in the review), 409a1b8 (drag fix), d6e2de7 (README). Item 4's seeds: 5d6e82b.

The coordinator's five facts: (1) net capability hosts AND secrets: done in 80d5ed8, the review shows "Can reach: ..." and "Can use the stored secrets: ..." (names only). (2) input title/placeholder: the prompt box uses them. (3) preview_apply -> jobs_started, open_preview -> preview_opened, preview_cancelled at expiry: the window closes the pane on preview_cancelled/preview_applied events from any cause. (4) CABINETOS_UNDO_DIR: set in EndToEndTests (2 places), TabsEndToEndTests, TwoWindowsTests, livecheck.ps1, livecheck2.ps1, edge-snapshots.ps1, scroll-bench.ps1; commit 74510e7. (5) agent.preview { preview }: rule 2 opens it (any event name with a string field "preview").

## Results (verbatim, all in this worktree, fresh builds)
- `dotnet build ui/CabinetOS.sln -c Release -warnaserror`: Build succeeded. 0 Warning(s) 0 Error(s).
- UI tests, Release, CABINETOS_UI_E2E=1, CABINETOS_CORE_EXE = worktree release core: "Test run summary: Passed! total: 767 failed: 0 skipped: 0".
- Rust: `cargo test --workspace`: every "test result" line has 0 failed; 701 tests passed in all; `cargo fmt --all -- --check` ok; `cargo clippy -p cabinetos-commands -p cabinetos-core --all-targets -- -D warnings` ok (I did not run the full workspace clippy or `cargo deny`).
- Live check with real keys and mouse, run item3-2 (DONE.md): "Started 02:30:20, ended 02:34:04, exit code 1. - scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: no; 298 frames in 5 s, 83 over 20 ms (27.9 %), 2 over 33 ms, worst 52.6 ms; CPU during the hold: machine 13.2 %, this window 1.4 % ... - Last stop line: STRICT: the scroll goal was not met - Checks that answered False: none". Exit code 1 is only the scroll goal under -Strict (item 5's). Section 14 lines: "14: the preview page is ready in the other pane: True", "14: the row drag started in the pane: True", "14: the drop reached the tool's page as paths-dropped: True", "14: it carried one path: True", "14: ask: WAITING: sdk\extensions\agent or build-index.ps1 -Extensions is not in the repository yet; the steps below did not run".
- The first run (item3-1) FAILED the drag (SetCursorPos does not start a WinUI drag) and, because Ctrl+W closed a left-pane tab instead of the page's tab, the edge-case section after it ran on the wrong panes (2 more False lines). Both fixed in 409a1b8; run item3-2 is clean.
- Also checked earlier with the snapshot aid against the real core: a 6-file preview rendered (create, 3 renames, delete tinted red, move, copy); Enter ran the 4 jobs it makes and the files matched; Esc changed nothing; preview opened by a plugin event; single-pane switch and restore; a fake command with input ran with the typed text; agent.notice reached the status bar; a drop reached the Markdown Preview page.
- NOT verified by real keys: the preview's Enter/Esc (only snapshot steps and unit tests); the review dialog's hosts/secrets lines (unit tests only, not looked at on screen); "14: ask" cannot run yet.

## Decisions (what - because - undo)
1. The preview covers the other pane's list (list and editor collapsed while it shows) - a translucent overlay showed the list through and rows behind could take keys - undo: PreviewPane in MainWindow.Preview.cs (QueueShow) keeps the list visible.
2. In single-pane mode the window switches to dual while a preview waits and back after - the preview needs the other pane - undo: drop `_previewMadeDual`, show it as a dialog.
3. Enter/Esc are handled in HandleWindowKey before the keymap, only when focus is in the preview or a file pane - Enter must not open the row under the other pane's cursor - undo: make preview.apply/preview.cancel commands with a `when`.
4. Move/copy rows show the target folder's name with a closing backslash, the whole path in the tooltip - narrow panes - undo: PreviewLine.From.
5. A create row of a folder gets its closing backslash back - the core drops it for kind Directory - undo: the branch in PreviewLine.From (or fix the core).
6. Dragged rows travel as text, one path per line, at most 1000 in paths-dropped - WebView2 gives a page only text/plain simply, and a limit keeps a message small - undo: ToolMessages.PathsDropped.
7. A catcher layer covers a tool page only while a drag from a pane runs - a WebView2 takes drops for itself - undo: EditorPane.SetDragging.
8. A command with `input` opens the prompt box titled by the input's title, else the command's title; Esc runs nothing (CommandOutcomeKind.Cancelled) - undo: PluginInput.Label.
9. The plugin event `agent.notice` shows its text in the status bar - the plan names it; QUESTION for you: it is the one place the window knows a word "agent" (Article 10); the general alternative is any plugin event with a string field "notice" - undo: PluginEvents.Notice.
10. Any plugin command result or plugin event with a string field `preview` opens a preview (your answer) - nothing agent-specific.
11. A tool page can follow at most 16 plugins - a page could flood the window with subscriptions - undo: ToolSubscriptions.
12. The review dialog and the plugin list show hosts and secret NAMES, never values - Article 8 - undo: CapabilityRow.
13. The live check drags with SendInput absolute moves and closes the page's tab with the palette's Close Editor - found by run item3-1 - undo: Live.MoveTo, section 14.
14. The "ask" part of section 14 is guarded by sdk\extensions\agent and `build-index.ps1 -Extensions` existing; it is an UNTESTED draft assuming agent.ask on Ctrl+K Ctrl+A, `plugins.agent.provider = "fake"` and the card name "CabinetOS Agent, Extension, by CabinetOS" - the extension did not exist - undo: delete the guarded branch.
15. sidebar.locate is seeded as `shift+alt+l` - the core writes modifiers in its own order (it rejected my `alt+shift+l` in its test); it is the same chord - undo: change the seed and the doc row.

## Ready plan text (Phase 14, the window's half)
"The window's parts of Phase 14 are built (ui, 767 tests): a plugin command with an `input` asks for its text in the prompt box first (Esc runs nothing); a preview a plugin proposes (a command result or any plugin event with a string field `preview`) opens with `open_preview` and shows in the other pane (in the right pane after a switch from single to dual), where Enter sends `preview_apply` and Esc `preview_cancel` before the keymap, and `preview_applied`/`preview_cancelled` close it when another client answered or after the 10-minute expiry; the plugin event `agent.notice` shows in the status bar; a tool page can follow plugins (`subscribe`, `plugin-event`) and receives rows dragged from a pane as `paths-dropped`; the permissions review shows a net capability's hosts and the names of its stored secrets. Checked with the real core (rows, open by ID, apply, cancel) and by a real mouse drag in the live check. Not yet run: the live check's `14: ask`, which waits for the agent extension."

## What is left
- Item 4 (Phase 13): only the four seeds are in (view.showExplorer ctrl+shift+e, view.showSearch ctrl+shift+f, sidebar.locate shift+alt+l, sidebar.lock; registry SEED is 97, docs/keybindings.md rows added). No window code yet. I will build it next: rail layout, sidebar host with Explorer (lazy folder tree over list_directory) and Search, sidebar tool pages, badges, warm/suspended web views, divider snap, snapshots, live-check section 13.
- ui.rail, ui.sidebarWidth and ui.sidebarView are not in the core's config schema (strict); I will keep them in memory and write them through set_value, which the core refuses until the schema knows them (the settings writer then logs it and the state stays in memory). Core schema needs: ui.rail (list of text), ui.sidebarWidth (number or null), ui.sidebarView (text), and a home for `sidebar.autoReveal` (bool, default true).

## Noticed, out of scope
- docs/PLAN.md line ~742 says "93 commands"; the registry now has 97 (yours to update).
- %LOCALAPPDATA%\CabinetOS\undo\journal.jsonl (1085 bytes, written 02:14-02:17 by live-check run item3-1, before the harness fix) holds test jobs; I did not delete it (outside the worktree).
- The `-Strict` live check exits 1 on the scroll goal every run (item 5).
