## Item 2: Phase 14a, the core foundations (protocol 13)

Agent: a9575be83f3b6b51f

Report: Item 2: Phase 14a, the core foundations (protocol 13)

Context: CabinetOS core, night run 2. Items 0 and 1 and all of item 2 (points 1–8) are built, checked and pushed to main. I stopped here, as the coordinator's budget message asked. Items 3 and 4 are NOT started.

=== DEVIATIONS FROM THE HANDOUT'S PROTOCOL TEXT (read first; the shell agent may need these) ===
1. Point 2: an extra request `open_preview { preview }` (reply `preview_opened`). It reopens a preview's listing after a pane closed its section.
2. Point 2: `preview_opened` is `{ preview, title, listing: OpenedListing }`. OpenedListing = `{ listing_id, section_handle, section_size, entry_count, generation, elapsed_us }`. The `preview_apply` reply is named `jobs_started { jobs: [u64] }`.
3. Point 2: the listing layout VERSION stays 2. Preview rows sit at `ListingHeader.preview_offset` (the old `reserved` field, offset 36), with `FLAG_PREVIEW = 1`. A normal listing is byte-identical to before.
4. Point 2: a new job kind `{"type":"steps","steps":[…]}` runs the preview rows (and undo). Neighbouring rows of the same kind share one job. The jobs run as a chain: each is checked only when the one before it completed, and a failed job cancels the rest.
5. Point 4: the manifest key is `name`, not `id` (every capability in plugin.json already uses `name`): `{ "name": "net", "hosts": [...], "secrets": [...], "reason": "..." }`.
6. Point 4: ADDED a `secrets` list to `net`. The core sends only a secret the manifest lists, so the review dialog shows which keys a plugin may use.
7. Point 4/5: the WIT package is now `cabinetos:plugin@0.2.0`. 0.1 plugins are refused ("apiVersion 0.1.0 does not match this core's plugin interface 0.2.0"). The live index has 41 themes and no plugins, so nothing published breaks.
8. Point 5: the changes reach the PLUGIN through a new export `on-event(name, payload)`, not the UI's `plugin_event`. The payload has an extra `overflow: bool`. There is a second event `folder-unwatched { path, message }`. Paths are Windows paths. Watching is not recursive. At most 16 watches per plugin, and 1,000 changes per message.
9. Point 6: `input` comes only from plugin.json (register-command in the WIT is unchanged). Both `title` and `placeholder` are optional.
10. Point 7: the journal has ONE line per job, with the entries inside it (not one line per entry). `undo_job { job? }`: without `job`, it undoes the newest job that is not an undo itself and was not undone yet. The reply is `undo_started { job_id, undoes, left: [{ path, reason }] }`. `reason` is one of `in_recycle_bin`, `deleted_for_good`, `not_saved`, `saved_copy_removed`, `put_back`. New error code `not_undoable`. A job missing from the journal answers `no_such_job`.
11. Point 8: install.ps1 ALREADY had `-AddToPath`, off by default. Only its help text and its final message now name `cab`.

=== COMMITS (git log --oneline, mine only, all pushed to origin/main) ===
4feb72f release: cab.exe, the command line under a short name to type
44f2c66 protocol 13: every job leaves an undo journal line, and undo_job reverses it
c6489a3 protocol 13: a plugin command may ask for a line of text before it runs
85d6cd5 protocol 13: plugins watch folders under their roots and hear each change
f91a5be diagnostics: heavy mode records http-request like the other host calls
94b0e33 protocol 13: plugins reach the network through the core, with named hosts and secrets they never see
942b82e protocol 13: secrets in the Windows Credential Manager, never seen by a plugin
b6bd317 protocol 13: previews, proposed changes shown as a listing before they run
46c644c protocol 13: window_state, so programs without a window see what the user sees
77c1169 config: ui.tabs saves each pane's tabs (the core half of Phase 12)   <- item 1
349a59f marketplace: the default index is the public one on GitHub Pages   <- item 0
The worktree branch worktree-agent-a9575be83f3b6b51f is at 4feb72f, which was origin/main when pushed. Every commit before 94b0e33 ends with "Co-Authored-By: Claude Opus 5". From 94b0e33 on, they end with "Claude Opus 5.5". The session's attribution rule sets that line, and it names the model that ran.
Rebases: I merged the Phase 15 agent's commits (heavy mode, bundles) and the shell agent's commits. The conflicts were only in the README test count, the ARCHITECTURE change log (both rows kept) and cabinetos-fs lib.rs exports (both kept). `http-request`, `watch-folder` and `unwatch-folder` now write heavy mode's `host call` line, like the other host calls.

=== THE FIVE CHECKS (last run, on 4feb72f's tree, from core/) ===
build exit 0 | test exit 0: passed 700, failed 0, ignored 5 | clippy exit 0 | fmt exit 0 | deny exit 0 ("advisories ok, bans ok, licenses ok, sources ok").
The handout's baseline was 610 core tests and protocol 12. Now: 700 tests, protocol 13. README says 700 core tests and 766 UI tests (the shell agent's count). The build scripts parse in PowerShell 7.6.6 and in 5.1 (build\check-scripts.ps1).

=== ITEM 0 (349a59f) ===
DEFAULT_MARKETPLACE_INDEX = https://oliverd25.github.io/cabinetos-marketplace/index.json, in core/crates/cabinetos-config/src/model.rs. Also changed: the sdk/config schema, docs/config.md, docs/marketplace.md ("The public index"), ADR 0012 ("Done on 2026-09-30"), docs/ui.md, and the window's stale-placeholder message in ui/CabinetOS.Core/Market/MarketplaceModel.cs, with its test (UI test 4/4 passed).
Checked again tonight: the URL answers 200, application/json, 70,988 bytes, 41 items, all themes. A core started with no config file (default settings, every folder in a scratch folder) gave:
  cabinetos-cli config get marketplace.index -> "https://oliverd25.github.io/cabinetos-marketplace/index.json"
  cabinetos-cli market refresh -> "41 items from https://oliverd25.github.io/cabinetos-marketplace/index.json", then catppuccin-mocha 1.0.0 … horizon-bright 1.0.0 (all 41 themes), exit 0
  cabinetos-cli market search github -> "5 items from …": github-dark, github-light, ayu-dark, ayu-light, ayu-mirage, exit 0

=== ITEM 1 (77c1169) ===
The model is in cabinetos-config/src/model.rs:
  UiConfig.tabs: TabsConfig { left, right: PaneTabs }
  PaneTabs { items: Vec<TabEntry>, active: u32 }
  TabEntry { path: String (required), locked: bool (default false) }
All of them refuse unknown keys. parse.rs gives the error "ui.tabs.<pane>.active is N, but the <pane> pane has M tab(s); it counts from 0".
Tests: tabs_are_read_with_their_defaults_and_unknown_keys_refused, a_tab_index_past_the_end_names_the_pane, tabs_round_trip_through_the_file.

=== ITEM 2, AS BUILT ===
Protocol: PROTOCOL_VERSION = 13 (cabinetos-protocol/src/lib.rs). The request and reply types are in cabinetos-protocol/src/message.rs unless noted. All schemas are regenerated: sdk/protocol/request/response/event schemas, sdk/marketplace/index.schema.json.

Point 1, window_state (46c644c)
- Types (cabinetos-protocol/src/window.rs):
  WindowState { active_pane: "left"|"right", panes: { left, right: PaneState } }
  PaneState { tabs: [WindowTab], active: u32, cursor: string|null, marked: [string] }
  WindowTab { path, locked, tool: string|null }
- Request `window_state` carries the state's fields at the top level: {"type":"window_state","active_pane":…,"panes":…}. Reply `ok`. It needs `hello` first; without it the reply is a protocol_error.
- Request `get_window_state { client: string|null }`. Reply `window_state { client, sent_at_ms, state }`. Error `no_window`.
- The core keeps one state per connection and drops it when the client disconnects (cabinetos-core/src/window.rs WindowStates). A client's id is "<hello name>#N" (connection.rs).
- CLI: `cabinetos-cli state [--json] [--client <ID>]` (cabinetos-cli/src/state.rs).
- Tests: the_wire_form_follows_the_protocol_text, the_newest_state_wins_and_a_named_client_is_found, a_window_state_is_kept_per_client_until_it_leaves (core e2e), the_table_names_the_panes_their_tabs_and_the_marks, state_prints_what_the_window_said (CLI e2e).

Point 2, previews (b6bd317)
- Requests:
  `preview_listing { title, rows: [{ path, kind: rename|move|copy|delete|create, to: string|null }] }` -> `preview_opened`
  `open_preview { preview }` -> `preview_opened`
  `preview_apply { preview }` -> `jobs_started { jobs }`
  `preview_cancel { preview }` -> `ok`
- Events: `preview_applied { preview, jobs }` and `preview_cancelled { preview }`, which also comes at the 10-minute expiry.
- Errors: `no_such_preview`, and `too_many_previews` (more than 20 per client).
- Code: cabinetos-protocol/src/preview.rs (ChangeKind, PreviewRow, OpenedListing). shm.rs PreviewRow is #[repr(C)], 12 bytes: to_offset u32, to_len u32, change u8, reserved [u8;3]. cabinetos-fs/src/preview.rs has PreviewWriter; ListingReader has is_preview() and preview_row(). cabinetos-core/src/preview.rs has Previews. cabinetos-jobs has start_chain, run_steps, and JobStep {rename, create_folder, create_file, recycle, restore}.
- Env CABINETOS_PREVIEW_TTL_MS (for tests).
- Tests: rows_have_the_documented_wire_form, preview_row_layout_is_pinned, steps_jobs_have_the_documented_wire_form, a_preview_reads_back_with_its_changes_and_targets, an_empty_preview_is_a_valid_listing, a_folder_listing_is_no_preview, a_steps_job_runs_its_steps_in_order_and_counts_what_failed, chained_jobs_wait_their_turn_and_a_cancel_takes_the_rest_along, rows_are_checked_and_their_targets_made_whole, neighbouring_rows_share_a_job_and_the_order_stays, and 4 core e2e tests: a_preview_is_a_listing_and_applies_its_rows_in_order, a_row_that_fails_cancels_the_rows_after_it, previews_are_cancelled_and_expire, the_twenty_first_preview_of_a_client_is_refused.

Point 3, secrets (942b82e)
- New crate core/crates/cabinetos-secrets. It uses CredWriteW, CredReadW, CredDeleteW and CredEnumerateW, CRED_TYPE_GENERIC and CRED_PERSIST_LOCAL_MACHINE, with target "CabinetOS/<name>". Names are 1–128 characters of [A-Za-z0-9-_.]. Values are 1–2560 bytes.
- Requests: `secret_set { name, value }` -> ok; `secret_get { name }` -> `secret { value }`; `secret_delete { name }` -> ok; `secret_list` -> `secret_names { names }`.
- Errors: `no_such_secret`, `secret_error`.
- A value never prints: SecretText has a hidden Debug, and the logs carry names only.
- Env CABINETOS_SECRETS_PREFIX (for tests).
- CLI: `secret set <name> [--value V]` (stdin otherwise, one line ending dropped), `secret get|delete <name>`, `secret list`.
- Tests: a_value_never_prints, names_are_plain, secrets_round_trip_through_the_credential_manager, the_text_travels_but_never_prints, one_line_ending_is_dropped, secrets_are_kept_by_windows_and_never_logged (core e2e, trace-level log scanned), secret_stores_reads_lists_and_removes (CLI e2e).

Point 4, net / http-request (94b0e33, f91a5be)
- WIT (sdk/wit/plugin.wit, host interface):
  record web-request { method: string, url: string, headers: list<tuple<string,string>>, body: option<list<u8>>, secret: option<string>, secret-header: option<string>, timeout-ms: u32 }
  record web-response { status: u16, headers: list<tuple<string,string>>, body: list<u8> }
  http-request: func(request: web-request) -> result<web-response, string>;
- Code: cabinetos-plugins/src/net.rs (ureq over rustls, with the Windows certificate store through the platform verifier) and manifest.rs (HostRule, check_hosts).
- Rules:
  - The URL must match a manifest host. `name` without a port matches only the scheme's default port.
  - https only; plain http only to localhost and 127.0.0.1.
  - No user or password in the URL. No redirects are followed.
  - Methods: GET, POST, PUT, PATCH, DELETE, HEAD.
  - The body is capped at 8 MiB. timeout-ms 0 or over 120000 means 120 s. Connecting may take 15 s.
  - The secret goes into `secret-header`: `Authorization` gets "Bearer <value>", other headers get the raw value.
  - An error status is still an answer (`ok`).
  - One INFO log line per request: host, method, status, bytes, elapsed_ms.
  - Time spent waiting for the network does not count against the call's deadline.
- HostServices gained `secret(name)`. The core's Bridge reads it from cabinetos-secrets.
- New fixture: sdk/templates/plugins/fetcher, committed as sdk/fixtures/plugins/fetcher. Its manifest names net hosts ["localhost:8090"] and secrets ["fetcher-test"].
- Tests: net_names_its_hosts_and_its_secrets, only_named_hosts_and_https_pass, a_secret_must_be_named_and_stored, net_puts_the_secret_into_the_header_and_never_into_the_plugin, net_refuses_other_hosts_unnamed_secrets_and_huge_answers, net_needs_its_grant_and_waiting_for_the_network_counts_against_no_deadline, a_plugin_reaches_its_host_with_a_secret_from_the_credential_manager (core e2e, trace log scanned for the value).

Point 5, fs:watch (85d6cd5)
- Capability `fs:watch` (medium). It takes `roots` and grants no reading.
- WIT:
  watch-folder: func(path: string) -> result<_, string>;
  unwatch-folder: func(path: string);
  export on-event: func(name: string, payload: string);
  activation gains watch-roots: list<string> (Windows paths).
- watch-folder takes `C:\a` or `/C:/a`, and refuses `.` and `..`.
- Payload of `folder-changed`: { path, changes: [{ kind: created|modified|removed|renamed, path, old_path }], overflow }. The changes of one folder are gathered for 200 ms into one message.
- Watches end with the instance. `on-event` has the same limits as on-listing-opened (5 s; at most 64 waiting).
- Code: cabinetos-fs/src/watch.rs has DirectoryWatcher::start_detailed, EntryChange, EntryChangeKind and DetailedChange; the old `start` is unchanged. cabinetos-plugins/src/watch.rs has Watches and Notifier. HostInner::notify and Call::Event are new.
- Every template implements on_event.
- New fixture: `watcher`. Its commands are watcher.watch and watcher.unwatch, and it passes each on-event on through emit.
- Tests: records_become_changes_and_renames_pair_up, detailed_watching_names_each_change, paths_come_in_either_form_and_stay_under_their_roots, a_batch_joins_paths_drops_repeats_and_says_when_it_is_full, a_watched_folder_s_changes_arrive_gathered_and_only_under_the_roots, watches_end_with_the_plugin, a_plugin_hears_about_changes_in_a_folder_it_watches (core e2e).

Point 6, input (c6489a3)
- plugin.json command: "input": { "title"?, "placeholder"? }. Each is 1–200 characters; unknown keys fail the manifest.
- Code: protocol CommandInput is in message.rs. CommandInfo.input is optional and is skipped on the wire when absent. It is threaded through CommandDeclaration.input -> PluginCommand.input -> registry Command.input -> command_info.
- CLI `commands list` marks a command "(asks for text)".
- plugin_event already carried plugin_id, so nothing needed to change there.
- The fetcher fixture's command has an input prompt, and it takes `input` as its URL.
- Tests: a_command_may_ask_for_a_line_of_text, a_command_that_asks_for_text_says_so, a_plugin_command_that_asks_for_text_says_so_in_the_list (core e2e).

Point 7, undo journal (44f2c66)
- Where: %LOCALAPPDATA%\CabinetOS\undo\journal.jsonl. Env CABINETOS_UNDO_DIR overrides it (core lib.rs UNDO_DIR_ENV). EngineConfig.undo_dir: Option<PathBuf> (None means no journal).
- Line format: { job, kind, state, ended_ms, undoes?, truncated?, entries: [ {op: created|moved|overwritten|recycled|deleted|removed_folder|restored, …} ] }. The line is written before the job's end is announced.
- Saved copies: a replaced file is MOVED to undo\<job>\<n>, and put back if the replace fails. A file over 256 MiB is not saved.
- Limits: 200 jobs, and 256 MiB of saved copies (oldest removed first). A job with more than 100,000 entries cannot be undone.
- Job IDs continue after the journal's newest ID (cabinetos_jobs::continue_job_ids_after).
- Code: cabinetos-jobs/src/journal.rs (Journal, UndoEntry, plan_undo, refusal), JobQueueManager::undo, and the recording in run.rs.
- CLI: `cabinetos-cli undo <job>` and `undo --last` (cabinetos-cli/src/undo.rs). It waits for the undo job and prints a "stays:" line for each thing it cannot bring back.
- All 21 core/CLI test harnesses that set CABINETOS_THEMES_DIR now also set CABINETOS_UNDO_DIR. The real %LOCALAPPDATA%\CabinetOS\undo still does not exist after the full test run.
- Tests: an_undo_reverses_newest_first_and_folders_come_back_first, what_cannot_come_back_is_named_and_a_delete_is_refused_with_advice, the_oldest_saved_copies_go_first, the_journal_line_has_its_documented_form, a_copy_over_files_is_undone_and_the_replaced_file_comes_back, a_move_goes_back_and_the_last_job_is_the_default, a_delete_cannot_be_undone_and_says_what_to_do, the_journal_keeps_200_jobs_and_new_ids_follow_it, a_job_is_undone_over_the_pipe_and_its_journal_line_stays (core e2e), undo_reverses_the_last_job_or_a_named_one_and_refuses_a_delete (CLI e2e).

Point 8, cab.exe (4feb72f)
- build/release.ps1 copies cabinetos-cli.exe as cab.exe.
- install.ps1 and uninstall.ps1 list `cab` among the programs that must not be running.
- The winget installer manifest lists cab.exe.
- docs/release.md says so.
- release.ps1 was NOT run end to end; the scripts were only parsed.

Docs updated: ipc.md, plugins.md (network, watching folders, commands that ask for text, the WIT versions table), jobs.md (Chains and steps, Undo), diagnostics.md (host call list), marketplace.md, release.md, sdk/README.md, sdk/templates/README.md, and ARCHITECTURE.md change-log rows (net granted, undo journal, cabinetos-secrets). I did not edit docs/PLAN.md.

=== DECISIONS (what — because — undo) ===
- client id "<hello name>#N" — two windows may share a name — connection.rs NEXT_CLIENT
- window_state needs hello; a client's state is dropped on disconnect — the id comes from hello, and a closed window shows nothing — connection.rs
- open_preview added — a pane that closed a preview's section must reopen it — remove the request
- preview rows run as chained steps jobs, with neighbours of the same kind batched — later rows may depend on earlier ones — core preview.rs job_requests
- listing layout VERSION stays 2 (preview_offset plus a flag) — normal listings stay byte-identical — bump shm VERSION
- a separate crate, cabinetos-secrets — the Credential Manager is not the file system, and its unsafe code stays in one crate — merge into cabinetos-fs
- test prefix env vars (CABINETOS_SECRETS_PREFIX, CABINETOS_PREVIEW_TTL_MS, CABINETOS_UNDO_DIR) — tests must never touch real secrets or the real journal — remove the env reads
- capability key `name`, not `id` — the manifest format is one format — accept `id` as an alias
- the `secrets` allow-list on net — stops a plugin sending any stored key to its hosts — drop the check in net.rs secret_header
- WIT 0.2.0, 0.1 refused — on-event is a new export every plugin must have — keep a second bindgen world
- no redirects — a redirect could leave the named hosts — set max_redirects
- network time excluded from the deadline — model answers take longer than 5 s — worker.rs network_credit
- tests use a random port and rewrite their manifest copy (the fixture still says localhost:8090) — 8090 may be busy, and runs happen in parallel — bind 8090
- heavy line for http-request logs header names and body size only — a plugin's own tokens may be in values — log the full args
- fs:watch events go to the plugin through on-event, in Windows paths, with overflow and folder-unwatched — the plugin must hear changes, lost changes and the end of a watch — use the payload shape only
- input comes from plugin.json only — no WIT change, and the review shows it — add a parameter to register-command
- one journal line per job — "keep 200 jobs" is then "keep 200 lines" — per-entry lines
- replaced files are moved, not copied — instant on the same volume — copy
- job IDs continue after the journal — `undo 12` must survive a restart — remove continue_job_ids_after
- undo_job with no job means the last one not undone — this is `undo --last`, and repeating it walks back — make job required
- only the outermost `created` path is recycled — a folder takes its contents along — recycle every entry
- -AddToPath left as it was (it already existed) — the handout asked for exactly that — none
- cab.exe added to the winget NestedInstallerFiles — winget should know the command — remove the line

=== GAPS FOR ITEM 3 (the agent extension), not built ===
1. A plugin has no host function to call core requests (get_window_state, preview_listing, preview_apply, undo_job, search, set_value). The agent plugin needs one, for example `core-request: func(json: string) -> result<string, string>` under a new capability with a request allow-list.
2. A plugin cannot read its own `plugins.<id>` settings: config-get blocks the `plugins` section.
3. PluginSettings uses deny_unknown_fields, so extra per-plugin settings keys are refused.
4. A preview applies as several chained jobs, and undo reverses one job at a time. A whole-preview undo would need a group id.
5. The UI's C# tests that start a real core should set CABINETOS_UNDO_DIR. If not, their jobs write to the real %LOCALAPPDATA%\CabinetOS\undo. This is the shell agent's harness.
6. One test failed once from a %TEMP% race between two worktrees (capabilities_gate_the_file_system). Another agent fixed it in 2d06f2a.

=== READY PLAN TEXT (for docs/PLAN.md, Phase 14a) ===
Done 2026-09-30 (Phase 14a, the core foundations, protocol 13). Built:
- window_state / get_window_state and `cabinetos-cli state`;
- previews: preview_listing, open_preview, preview_apply -> jobs_started, preview_cancel, the events preview_applied and preview_cancelled, a 10-minute expiry, 20 per client, the listing header's preview flag with 12-byte preview rows, and the `steps` job kind run as job chains;
- secrets in the Windows Credential Manager (crate cabinetos-secrets; secret_set, secret_get, secret_delete, secret_list; `cabinetos-cli secret`);
- `net` granted, through the host function http-request, only to the hosts and secrets plugin.json names (WIT 0.2.0);
- `fs:watch` with watch-folder, unwatch-folder and the export on-event (folder-changed, 200 ms);
- command `input` prompts in plugin.json and list_commands;
- the undo journal (%LOCALAPPDATA%\CabinetOS\undo: 200 jobs, 256 MiB of saved copies) with undo_job and `cabinetos-cli undo <job>|--last`;
- `cab.exe` in the release.
700 core tests; all five checks pass. Not started: item 3 (the agent extension, which first needs a host function for core requests) and item 4.

What is left: items 3 and 4 (not started, as instructed). Everything above is pushed; nothing is uncommitted in the worktree.
