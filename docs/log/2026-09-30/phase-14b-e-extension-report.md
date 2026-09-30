## Item 4, watch folders, tiers, undo, audit log (the plugin's side of the rest)

Agent: a5443c663a5ef6b5b

Report: Item 4, watch folders, tiers, undo, audit log (the plugin's side of the rest)

Context: final hand-back for the whole run on the CabinetOS Agent extension (Phase 14 b to e). Everything is pushed to main; the last commit is 9e8d8e5. This run pushed, in order: af0a104 (rail fields), 3887a4c (item 2b, core-request and settings), 99b83c0 (cabinetos-cli-args and preview events), 21d414a (the plugin), 85a2870 (the chat page), bf5c0b1 (packaging and the guide), aaa1fce (watch folders in the plugin, the wasm fixture, host tests) and 9e8d8e5 (the chat's Rules panel and the guide's watch section). The per-item reports for 2b, item 3 and item 4 were written earlier as plain text; this repeats the item 4 report and adds the run summary.

WHAT WAS BUILT IN ITEM 4
- Rules (rules.rs, watch.rs in sdk/extensions/agent/plugin/src). They come from plugins.agent.settings.rules and from agent.rule.add. A plugin cannot call set_value, so added rules are kept in rules.json in the plugin's own data folder. Both start watching when the agent starts and on every settings-changed event. One rule per folder, and a settings rule wins for its folder.
- Manifest: new capability fs:watch (same roots as fs:read, %USERPROFILE%), four new commands agent.rule.add, agent.rule.remove, agent.rule.resume, agent.rule.list, and new events agent.rules and badge.
- A new file: the plugin reads folder-changed, counts "created" files and files renamed from a temporary name (.crdownload and similar), and skips .part, .tmp, ~$ files, desktop.ini and Thumbs.db. It asks the model ONCE per file with ONE round; the message has the rule, the file's path, its describe line and the folder's names. The tier decides: tier 2 sends agent.preview (the window opens it, only the user applies it); tier 3 applies at once and sends an agent.notice; tier 1 leaves rules idle and tells the user once. Every run is in the audit log with source "rule".
- Limits: a rule is paused after 3 failures in a row (with a notice, agent.rule.resume restarts it); at most 10 files a minute per rule; the first event about each path the agent's own changes made is ignored (so a renaming rule does not loop); a rule never cancels the preview the user is looking at; a folder the core will not watch, or a watch that ends by itself, makes the rule "blocked" with the reason.
- Chat page: a Rules view beside the Log (state of each rule, Resume, Remove, add form); a rule's agent.reply no longer clears the busy state of the person's own request; the rail button spins (badge event) while a request or rule runs.
- Docs: docs/extensions/agent.md has a "Watch folders" section, the rule commands, the agent.rules and badge events, and the list of commands the window must let a page run.

CHECKS (verbatim, run from core/ at the end)
- cargo build --workspace: finished.
- cargo test --workspace: passed 722 failed 0 ignored 5. (717 at the start of this stretch; +2 host tests and +1 market test in item 3, +2 host tests in item 4.)
- cargo clippy --workspace --all-targets -- -D warnings: clean.
- cargo fmt --all -- --check: exit 0.
- cargo deny check: advisories ok, bans ok, licenses ok, sources ok.
- The plugin's own suite (cargo test in sdk/extensions/agent/plugin, separate Cargo project, not part of the five checks or CI): 90 passed; clippy -D warnings clean natively and for wasm32-wasip2; fmt clean.
- Two host tests run the built WebAssembly with the fake provider and a real watched folder: a new file becomes one model call and a preview_listing row (create of <root>\2026\) with no preview_apply; a rule added with agent.rule.add is refused outside the roots (nothing written), accepted inside them, writes rules.json, gets a "rule" reply for a new file, and is removed by agent.rule.remove. Earlier host tests cover agent.ask to a preview_listing and agent.undo after preview_apply (jobs 12 then 11 at tier 3).
- No real model API was called anywhere; every test uses the fake provider. Nothing was pushed to the marketplace repository. No file under ui/ was touched. Git status is clean.

DECISIONS (what — because — undo)
- Rules from settings plus rules.json, one rule per folder, settings win for a folder — a plugin cannot change the configuration, and the settings validator already allows one rule per folder — undo: wanted_rules() in watch.rs.
- A rule run has one round (Source::rounds) and the system prompt says so ({{rounds}} in prompt.md) — the handout says never more than one model call per file — undo: Source::rounds in agent.rs.
- Only a run that returns an error counts toward the 3-failure pause — the model's wrong commands are shown in its reply and fed back; only an unreachable service or bad settings should stop a rule — undo: run_rule() in watch.rs.
- At most 10 files a minute per rule — a full folder must not send hundreds of paid requests — undo: MAX_FILES_PER_MINUTE in rules.rs.
- Rule failures use agent.notice, not agent.error — the chat page treats agent.error as the end of the person's own request — undo: report_rule() in handler.rs.
- agent.rule.list takes quiet — the chat page fetches the list without a status-bar line each time — undo: remove the argument.
- fs:watch has the same roots as fs:read — a rule needs both — undo: plugin.json.
- (Item 3) The chat page lives at sdk/tools/agent-chat and sdk/extensions/agent/extension.json names it — the handout says both sdk/tools/agent-chat and sdk/extensions/*/tool — undo: move the folder to sdk/extensions/agent/tool and make build-index.ps1 read it there.
- (Item 3) The built plugin is committed as sdk/fixtures/plugins/agent (about 1 MB) — CI has no wasm toolchain and the host test must load the real component — undo: delete the folder and the host tests that use it.
- (Item 3) The default index leaves the agent fixture out; -Extensions offers it as the extension's item — the default index must not change (UI tests read it) — undo: remove the skip in build-index.ps1.
- (Item 3) tool.json has "sidebar": true — with accepts: [] a tool has no other way to open — undo: remove the key.
- (Item 3) The chat page always sends an "input" argument with agent.tier and the rule commands — the window prompts for text when a command that declares input gets none — undo: none needed.

NOT BUILT: watching subfolders (the core watches one folder level), acting on files that were already in a folder when the agent started.

PLAN TEXT FOR docs/PLAN.md (Phase 14, stages b to e; I did not edit that file)
"Built 2026-09-30: the Agent extension. sdk/extensions/agent/plugin is a Core Plugin (Article 11, engine layer) that reaches the core only through core-request and models only through http-request. It holds no key, runs no shell and writes no file itself. The model writes cab command lines, parsed by cabinetos-cli-args. Three tiers (tier 2 default), providers Anthropic, OpenAI-compatible (Ollama) and a fake for tests, an audit log, undo. The chat page sdk/tools/agent-chat is a Tool Extension with tier buttons, Undo, Log and Rules. Watch folders: rules from plugins.agent.settings.rules or agent.rule.add (kept in rules.json), one model call and one round per new file, tier decides what follows, paused after 3 failures in a row, at most 10 files a minute, the agent's own changes are ignored. Both parts install from the marketplace as agent and agent-chat (build-index.ps1 -Extensions). Tests: 722 core, 90 plugin. Left: the window must let a tool page run agent.*; publishing the two items is the creator's step."

WHAT IS LEFT / FOR THE PLANNING SESSION
- Window allow-list: ToolMessages.AllowedCommands (ui/CabinetOS.Core/Tools/ToolMessages.cs) must add agent.chat, agent.tier, agent.undo, agent.audit, agent.rule.add, agent.rule.remove, agent.rule.resume, agent.rule.list, or the chat page cannot run them (the palette command "Agent: Ask" already works). Shell agent's part. subscribe, unsubscribe, plugin-event, paths-dropped and the sidebar page are already on main; the page uses them.
- ui/livecheck/livecheck.ps1 step 14 will not work as written: it writes plugins.agent.provider = 'fake', but the setting is plugins.agent.settings.provider, and PluginSettings has deny_unknown_fields, so the config would be rejected. It also needs a canned reply file at <plugins-data dir>\agent\fake-replies.json (a JSON list of texts) holding exact commands, for example: rename "<dir>\photo1.jpg" vacation_1.jpg. The agent's fs:read root is %USERPROFILE%, so a test folder under it works. The marketplace card name "CabinetOS Agent" and author "CabinetOS" match what the script looks for.
- cargo test inside sdk/extensions/agent/plugin is not run by the five core checks or CI; a CI step (when CI is switched on again) would keep the 90 plugin tests from being forgotten.
- The committed wasm (sdk/fixtures/plugins/agent/plugin.wasm, 1,026,010 bytes, 271,758 bytes zipped) adds a new copy to git history at every rebuild (rebuilt three times in this run). opt-level "z" might shrink it; not tried.
- docs/dev-setup.md does not mention the wasm32-wasip2 target that sdk/extensions/build-extensions.ps1 needs (pinned in the plugin's rust-toolchain.toml; rustup installs it on first use).

RECAP: The Agent extension is finished and on main: the plugin, the chat page, the marketplace packaging and the guide, plus watch folders. All five core checks pass with 722 tests, and the plugin's own 90 tests pass. What is left is the shell agent's allow-list change, the live-check config fix noted above, and the creator's decision to publish the two items.
