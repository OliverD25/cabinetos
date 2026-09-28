## Phase 3 report

Context: Phase 3 of CabinetOS (config, commands and keybindings). The work is done, pushed to `origin main` (HEAD `1f9f75d`), and CI is green. The working tree is clean. The user's real `%APPDATA%\CabinetOS` was never created: tests and live checks used temporary config files.

### Built
- **`cabinetos-commands`** (new, `forbid(unsafe_code)`):
  - The key grammar, with a parser that ignores case and writes one normal form.
  - Chords of at most 2 combinations. `CHORD_WINDOW_MS = 1000` is sent to the UI in the keymap.
  - A registry of 16 core commands. Plugin commands are not seeded.
  - The Immutable System Tier: `palette.show`, `overlay.close`, `keys.open`.
  - Keymap compilation: overrides, `""` removes a binding, conflict and chord-prefix checks, warnings for unknown commands.
  - A fuzzy palette scorer.
- **`cabinetos-config`** (was a stub, `forbid(unsafe_code)`):
  - The model: camelCase keys, every setting has a default, unknown keys are errors.
  - Line and column for every error, in characters. Errors found after parsing (keymap, profiles, version) are placed with a JSON path locator.
  - `ConfigStore`:
    - On first run it creates the file with the defaults and `$schema`, and writes `cabinetos.schema.json` next to it.
    - While the file has an error, the last good settings stay in effect.
    - The core's own writes are recognized by a content hash, so they are not reported back as changes.
    - An error is reported once. A fix is always announced.
    - `set_keybinding`, `unset_keybinding` and `set_value` re-read the file first. They write a temp file, flush it, then rename it over the old file.
  - `ConfigWatcher` reuses the Phase 2 `DirectoryWatcher` on the file's directory. It reports after 100 ms of quiet, and at most 500 ms into a continuous burst.
  - `changed_paths` gives the dotted-path diff.
  - `parse_checked` backs `config validate`.
  - The schema is snapshot-tested at `sdk/config/cabinetos.schema.json`.
- **`cabinetos-protocol` v3:**
  - Requests: `get_config`, `get_keymap`, `list_commands`, `search_commands`, `execute_command`, `set_keybinding`, `reset_keybinding`.
  - Replies: `config`, `keymap`, `commands`, `search_results`, `command_routed`, `command_result`.
  - Events: `config_changed`, `config_error`, `keymap_changed`.
  - Six new error codes.
  - `list_directory` `include_hidden` and `sort` are now optional.
- **`cabinetos-diag`:** `set_level()` changes the level while the process runs (a reload layer). `CABINETOS_LOG` wins over it.
- **`cabinetos-core`:**
  - `--config` flag.
  - A new `settings.rs` service:
    - A watch-channel snapshot, so read requests never wait for the disk.
    - A broadcast of events to connections after `hello`.
    - Keybinding writes run on the blocking pool.
  - `list_directory` defaults come from `panes`, and `logging.level` applies live.
- **`cabinetos-cli`:** `config path|show|validate [file]`, `commands list [--json]`, `commands search <q>`, `keys list|set|reset|watch`. `keys watch` shows the diff of the keymap and the ms since the file was written.
- **Docs:**
  - New: `docs/keybindings.md` (the chord spec shared by core and UI) and `docs/config.md`.
  - Updated: `docs/ipc.md`, `core/README.md`, the `docs/ARCHITECTURE.md` crate map and change log, `docs/diagnostics.md`, `docs/dev-setup.md`, `sdk/README.md`.

### Commits (all pushed)
`dfb3291` test race and deadlines · `719e4cd` protocol v3 · `eb12296` commands · `7746a8d` config · `8a1e335` diag set_level · `11f7a09` core integration and e2e tests · `e68bd0d` CLI · `f305a12` docs · `dcdf791` diag test race fix · `1715eed` doc accuracy · `1f9f75d` CI paths add `sdk/config/**`

### Checks
- All five checks pass locally: build, test, clippy `-D warnings`, fmt `--check`, `cargo deny check`.
- **226 tests**, green in 5 of 5 full `cargo test --workspace` runs.
- New tests:
  - commands: 25
  - config: 33 unit and 4 watcher-integration tests (an in-place save and a rename-into-place save, each in effect within 1 s; the store's own writes not reported)
  - core e2e `tests/config.rs`: 9, among them the edit → `keymap_changed` within 1 s test and `the_immutable_tier_cannot_be_rebound`
  - CLI e2e `tests/settings.rs`: 4 (`keys set` changes the file and the running core; immutable refusal; search; `config validate`)
  - plus diag `set_level` and new CLI unit tests
- CI green on `1f9f75d`: https://github.com/OliverD25/cabinetos/actions/runs/36370921560 (both jobs succeeded). Earlier runs show "cancelled" because each newer push replaced them.

**Timing-sensitive tests** (the coordinator asked):
- Widened: `cabinetos-fs/tests/watch.rs`, stop bound 0.5 s → 2 s and drop bound 0.1 s → 1 s. `core_process.rs` `EXIT_DEADLINE` 5 s → 10 s.
- Fixed real races:
  - `listings.rs`: drain events after `close_listing`.
  - `cabinetos-diag` `format::tests` (`capture_raw`): `Arc::try_unwrap` failed once in a full run. The cause: `tracing` briefly upgrades weak dispatcher references while any thread registers a callsite. It now copies the lines instead. This is likely your unnamed Phase 2 flake.
- The new 1 s edit deadlines are the phase's own promise and stay at 1 s. The measured time is about 100–150 ms.

### Live check (debug build, scratch `--config`)
`cabinetos-cli --pipe live3 keys list` (default keymap):
```
ctrl+shift+p       palette.show                   (immutable)
escape             overlay.close                  (immutable)
ctrl+k ctrl+s      keys.open                      (immutable)
ctrl+shift+d       view.toggleDualPane
ctrl+backquote     view.toggleTerminal
tab                view.focusOtherPane            when filesView
ctrl+b             view.toggleSidebar
f5                 file.copyToOtherPane           when filesView
f6                 file.moveToOtherPane           when filesView
f7                 file.newFolder                 when filesView
ctrl+l             go.toPath
ctrl+shift+x       marketplace.browse
ctrl+k ctrl+w      workspace.switch
ctrl+k ctrl+t      preferences.selectColorTheme
ctrl+shift+b       terminal.runTask
15 bindings; the second key of a chord must follow within 1000 ms
```
A hand edit from Bash, with `keys watch` running:
```
$ sed -i 's/"keybindings": \[\]/"keybindings": [ { "command": "view.toggleSidebar", "keys": "ctrl+alt+b" } ]/' cabinetos.json
--- keys watch output:
config_changed (119 ms after the file was written): keybindings
keymap_changed (119 ms after the file was written)
  - ctrl+b view.toggleSidebar
  + ctrl+alt+b view.toggleSidebar
```
Then:
```
$ cabinetos-cli --pipe live3 keys set palette.show "ctrl+p"
cabinetos-cli: palette.show belongs to the Immutable System Tier; its keys cannot be changed (the tier: palette.show, overlay.close, keys.open) (immutable_binding)
exit code 1
$ cabinetos-cli --pipe live3 commands search "dual"
 1. score  137  view.toggleDualPane            ctrl+shift+d     View: Toggle Dual Pane
$ cabinetos-cli --pipe live3 commands search "term"
 1. score  148  terminal.runTask               ctrl+shift+b     Terminal: Run Task…
 2. score  137  view.toggleTerminal            ctrl+backquote   View: Toggle Integrated Terminal
 3. score    8  marketplace.browse             ctrl+shift+x     Marketplace: Browse Plugins and Themes
```
A typo saved in place, `{ "ui": { "dualPan": false } }`, showed in `keys watch` as:
```
config_error (104 ms after the file was written): line 2, column 19: unknown field `dualPan`, expected one of `layout`, `dualPane`, `sidebar`, `theme`
```

### Decided
**Protocol**
- `execute_command` names its field `command`, not `id` — because `id` already is the envelope's field in the same flat object — undo: rename in `cabinetos-protocol/src/message.rs` and regenerate the schemas.
- `PROTOCOL_VERSION` is 3 — because messages were added and `list_directory` fields became optional — undo: revert `719e4cd`.
- `list_directory` `include_hidden` and `sort` are optional. When absent, `panes` decides. Open watched listings keep their order — because the spec says `panes` holds the defaults for `ListDirectory` — undo: make the fields required and remove the lookup in `connection.rs::list_directory`.
- Extra request `get_config` → `config { path, config }` — because the UI must not read files (Dumb UI Rule), and `config path|show` need it — undo: remove the variant.
- Reply shapes: `keymap` is flat (`chord_window_ms`, `bindings[{keys, command, when?}]`, `immutable`); `search_results { hits: [{id, score}] }`; `command_routed { target }`; `command_result { result }` — because the spec named these replies but not their fields — undo: rename in `message.rs`.
- Extra error codes `not_implemented`, `invalid_keys`, `keybinding_conflict`, `config_error`, next to `unknown_command` and `immutable_binding` — because the UI must tell these refusals apart — undo: map them to `protocol_error`.
- `config_error` `line` and `column` are optional (`null` when unknown, for example a deleted file) — undo: make them required.
- `config_changed` is sent for every applied change, keybinding changes included. `keymap_changed` follows when the compiled keymap differs. Your spec's wording could mean only one of the two for keybinding changes — because one "settings changed" stream is simpler for a settings UI — undo: in `settings.rs::apply`, skip `ConfigChanged` when `changed == ["keybindings"]`.
- `config_changed` with an empty `changed` list means "the error is gone" (the file was fixed back to the settings in effect) — because otherwise a UI never learns that a reported error was fixed — undo: in `ConfigStore::reload`, return `Unchanged` in that case.
- Config events go only to connections after `hello`. The config, command and keymap requests work without `hello` — because the CLI's one-shot commands need no handshake — undo: subscribe in `handle_connection`.
- `set_keybinding` with `keys: ""` removes the command's binding — because an empty `keys` means the same in the file — undo: treat `""` as `invalid_keys`.
- `reset_keybinding` for an unregistered command works when the file has entries for it; otherwise it gets `unknown_command` — because entries of a removed plugin must be removable — undo: `settings.rs::reset_keybinding`.
- `set_keybinding` replaces the user's earlier entries for that command with one entry without `when` — because it keeps the file simple — undo: `ConfigStore::set_keybinding`.
- `help.about` returns `{name, core_version, protocol_version, config_path}`. `file.*` commands answer `not_implemented` until Phase 4. `args` is accepted and ignored — undo: `settings.rs::execute`.
- A client that falls behind on events (broadcast buffer of 256) gets `config_changed` with every top-level section, plus the current keymap — undo: `connection.rs::forward_event`.

**Commands and keys**
- The command list: the design's `COMMANDS` array without its plugin commands, plus `palette.show`, `overlay.close`, `keys.open` and `help.about` — because Article 10 makes the plugin commands extensions — undo: `SEED` in `registry.rs`.
- Conflict E: `ctrl+k ctrl+w` — because every chord keeps Ctrl held, as in VS Code — undo: `SEED`.
- Key aliases `control`/`meta`, `esc`/`return`/`del`/`ins`/`pgup`/`pgdn`, and the punctuation characters. Only the normal form is written — because they make hand editing easier — undo: `KEY_ALIASES` and `MODIFIER_ALIASES` in `keys.rs`.
- An override without `when` keeps the command's own `when` — because rebinding F5 must not make it fire in text boxes — undo: `keymap.rs::compile`.
- The chord-prefix rule applies in all contexts. A prefix conflict that involves the tier gets `immutable_binding` (for example `ctrl+k` alone) — because the UI must never have to wait to decide — undo: `check_chord_prefixes`.
- An entry for an unregistered command is a warning in the log, not an error — because a removed plugin must not break the whole config — undo: `compile`.
- UI rule written in `keybindings.md`: a binding whose `when` holds wins over one without `when` — because otherwise same keys in different contexts would be ambiguous — undo: edit the doc.
- Search constants: +16 per letter, +24 word start, +20 consecutive, −3 per skipped letter (at most −30), −1 per leading letter (at most −15). It matches against `Category: Title` and the ID. Ties go to the shorter label, then registry order. The default limit is 20 — undo: `search.rs`.

**Config**
- `$schema` is `./cabinetos.schema.json`, and the core writes the schema next to the file at every start (embedded with `include_str!`) — because it works offline and matches the installed version — undo: `SCHEMA_REFERENCE` and `write_schema`.
- `std::fs::rename` replaces the file, not a direct `MoveFileExW` call — because the config crate forbids unsafe code, and std's rename replaces the target in one step on Windows — undo: add the `windows` dependency and call `MoveFileExW` in `write_atomically`.
- Strict JSON for v1 (PLAN open question 3), with a byte-order mark accepted — because every JSON tool can read it and there is one parser — undo: strip comments before parsing.
- The core rewrites the whole file: 2-space indent, struct order, normalized keys — because strict JSON has no comments to lose — undo: n/a.
- A keymap error rejects the whole file, and the last good settings stay — because a half-applied config would make the UI and the file disagree — undo: `ConfigStore::adopt`.
- A deleted file gives `config_error`. The watcher does not recreate it; a later `set_keybinding` does — undo: `reload`.
- Updates refuse to write while the file has an unfixed error — because overwriting would destroy the user's unfinished edit — undo: `ConfigStore::update`.
- Reads retry a sharing violation 10 times, 20 ms apart — because editors may hold the file open while saving — undo: `store.rs::read`.
- The watcher sends a report at most 500 ms after the first change of a continuous burst — because a directory that never goes quiet must still be read — undo: `MAX_DELAY` in `watch.rs`.
- The watcher lives in `cabinetos-config`, on two threads (`config-watch`, `config-debounce`) — because the spec puts watching under Part A — undo: move it into the core.
- The `pwsh` profile has `-NoLogo` — undo: `TerminalConfig::default`.
- `logging.level` applies live, and `CABINETOS_LOG` wins — undo: `settings.rs::apply_log_level`.
- The config path comes from `--config` (made absolute), then `CABINETOS_CONFIG`, then `%APPDATA%`, then the temp directory — undo: `resolve_path`.
- CI `paths` now includes `sdk/config/**` — because that schema is snapshot-tested and compiled into the core — undo: revert `1f9f75d`.
- The CLI e2e test runs `cabinetos-core.exe` found next to `cabinetos-cli.exe` — because cargo cannot give one package another package's binary — undo: move the test into `cabinetos-core/tests` and find the CLI binary there the same way.

### Needs the user
Ready text for `docs/PLAN.md` (I did not edit it):
- Phase 3 heading: `### Phase 3 — Config, commands and keybindings — done 2026-09-28`
- After its "Done when" line add: `Measured 2026-09-28 on this PC (debug build): a hand edit saved with sed -i reached a watching client 119 ms after the save; a broken save was reported as config_error 104 ms after it (cabinetos-cli keys watch). The end-to-end test the_immutable_tier_cannot_be_rebound proves the tier holds. Specs: [keybindings.md](keybindings.md), [config.md](config.md).`
- Conflict E row, "Decide in" cell: `Phase 3 (done: ctrl+k ctrl+w, keybindings.md)`
- Open question 3: `**Config comments (Phase 3).** Settled 2026-09-28: strict JSON for version 1 (config.md).`

### Known gaps
- `when` names are not validated. A typo in `when` silently never matches.
- The JSON Schema cannot express the rules across fields (unique profile names, `defaultProfile` must exist, the key grammar). Only the core checks them.
- Syntax and value errors point just after the text at fault (serde's convention). Keybinding errors point at the start of the entry.
- The watcher is not restarted if the config directory itself is deleted. The core reports `config_error`, and edits apply after a restart.
- `set_keybinding` drops a custom `when` of that command's earlier entries.
- The event-lag path (`every_section`) has no test.
- `CommandSource::Plugin` is not used until Phase 7.
- `cargo test -p cabinetos-cli` alone may run an old `cabinetos-core.exe` (documented in `core/README.md`).

### Noticed out of scope
- `README.md` status line is stale: it says "Phase 2 … is next". Ready text: `Status: pre-alpha. Phases 0 to 3 of [the plan](docs/PLAN.md) are done: the governing documents, and a Rust core that lists directories into shared memory, watches them, and serves its configuration file, command registry and keymap over a user-only named pipe (core/, 226 tests, CI green). Phase 4, the job engine, is next.`
- A Phase 1 behavior: on the first start, when the log directory does not exist yet, `tracing-appender` prints `Error reading the log directory/files: The system cannot find the path specified. (os error 3)` to stderr. The directory is still created and logging works. Creating the directory before building the appender would fix it.
