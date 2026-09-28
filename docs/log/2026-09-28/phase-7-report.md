## Phase 7 report

Context: CabinetOS Phase 7, the Core Plugin host (the core side only). Repo `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos`, branch `main`. Everything is pushed. The final commit 54f8171 has green CI.

The goal holds. A plugin that crashes is logged and removed, and the core keeps serving listings. The end-to-end test proves it, and so does the live check below.

### Built

- **Part 0: Recycle Bin (263437b).** A file too big for the Recycle Bin is never deleted permanently without asking. The core reads the bin's size (Windows API and registry; 5% of the volume when nothing is set). Such a file raises the conflict `recycle_bin_too_small { size }`. Only the new answer `delete_permanently` or `skip` resolves it; any other answer gets the new error `invalid_resolution`. The test runs on CI with `CABINETOS_TEST_RECYCLE_BIN=1`, and `docs/jobs.md` describes it.
- **Part A: the interface.** `sdk/wit/plugin.wit` holds the package `cabinetos:plugin@0.1.0` and its world `core-plugin`.
  - Exports: `info`, `activate(ctx)`, `deactivate`, `on-command`, `before-job` (answers `allow` or `deny(reason)`), `on-listing-opened`.
  - Imports, interface `host`: `register-command`, `log`, `config-get`, `emit`.
  - Manifest: `plugin.json`, read strictly. An unknown key or a bad value gives a message that names the file and the problem.
- **Part B: the host, crate `cabinetos-plugins`.**
  - One shared wasmtime `Engine` with fuel and epoch interruption.
  - One `Store` per plugin, owned by the plugin's own thread, which takes calls from a channel.
  - Each call gets fresh fuel, a deadline and a memory limit.
  - Sandbox: WASI Preview 2 with clocks and random numbers. No network, no environment variables, no arguments. Folders only as granted, plus the plugin's own folder `/data`. stdout and stderr go into the core's log line by line.
  - What a trap does:
    - an ERROR log line with `plugin_id` and a WebAssembly backtrace that names functions;
    - the instance is dropped and the state becomes `crashed`;
    - the event `plugin_crashed`, and the plugin's commands leave the registry;
    - `execute_command` then answers `unknown_command` and names the crashed plugin.
  - Wiring in the core:
    - `list_plugins`, `reload_plugin`, `set_plugin_enabled` and `grant_capabilities`; the last two write the new `plugins` section of `cabinetos.json`;
    - a grant or an on/off edit saved by hand applies without a restart;
    - plugin commands carry `source {kind: plugin, id, name}`;
    - `before-job` is a gate in `cabinetos-jobs` after the scan;
    - `on-listing-opened` goes to plugins whose folders contain the opened folder;
    - flags `--plugins-dir` and `--plugins-data-dir`, and variables `CABINETOS_PLUGINS_DIR` and `CABINETOS_PLUGINS_DATA_DIR`.
- **Log format.** A log line with a `plugin_id` gets boundary `plugin`. A `plugin_id` event field outside a plugin span moves to the top-level key.
- **Part C: sample and fixtures.** Rust plugins built with wit-bindgen live in `sdk/templates/plugins`: `hello` (the template), `crashy`, `spinner`, `reader`, `vetoer`, plus `hog` for the memory limit test. The built components are committed under `sdk/fixtures/plugins/<id>/`, together with `sdk/templates/README.md` and `build-fixtures.ps1`. The tests load the committed files, so CI needs no WebAssembly toolchain.
- **Part D: CLI.** `plugins list|reload|enable|disable|grant`, `commands exec <id> [json-args]` and `events watch`. `commands list` and `commands search` show `[plugin name]` after a plugin's commands.
- **Part E: tests.** 307 in total.
  - Host tests against the fixtures: manifest errors, an undeclared command failing activation, capability gating (the reader works inside its folder, fails outside it, and cannot write), a crash with auto-restart, three crashes staying stopped, the timeout, fuel, the memory limit and the job gate.
  - Real-core end-to-end tests: the goal, spinner stopped in under 8 s, grants through the protocol and by a hand-edited file, the job veto, stdout and stderr in the log with boundary `plugin`, listing notifications.
  - One CLI end-to-end test.
- **Part F: docs.** New `docs/plugins.md`, and updates to `docs/ipc.md`, `docs/config.md`, `docs/jobs.md`, `docs/diagnostics.md`, `core/README.md`, `sdk/README.md` and the crate map and change log in `docs/ARCHITECTURE.md`.

### Commits

All are on `origin/main`:
- 263437b jobs: never delete silently what the Recycle Bin cannot take
- 0756825 sdk: the Core Plugin interface, the sample plugin and the test fixtures
- 9111823 jobs: a gate every job passes after its scan
- 8bbaded diag: log lines inside a plugin span have the plugin boundary
- c586082 jobs: format the gate code as rustfmt wants it (9111823 went out without `cargo fmt`)
- fdf3cf3 plugins: the Core Plugin host, wired into the core
- 1e79138 cli, tests: drive the plugins from the command line, prove the goal end to end
- 6b0c9f1 docs: the Core Plugin guide, and the plugins in the protocol, config and job docs
- da897c8 plugins: crash logs name the panic's place, its functions and its plugin (three fixes found by the live check)
- 0cb985d tests: prove a plugin's stderr reaches the core's log
- 54f8171 ci: give the core job 45 minutes; wasmtime made a cold run take 33

### Checks

On this PC, on the final tree:
- `cargo build --workspace`: ok.
- `cargo test --workspace`: 307 passed, 0 failed, 2 ignored (the same two as before, by design).
- `cargo clippy --workspace --all-targets -- -D warnings`: ok.
- `cargo fmt --all -- --check`: ok.
- `cargo deny check`: advisories, bans, licenses and sources all ok. It warns about duplicate crate versions that wasmtime brings; these are warnings only.

CI is green on 54f8171: https://github.com/OliverD25/cabinetos/actions/runs/36384053826

The run for 0cb985d passed every step up to the release build. It then hit the old 30-minute job limit while compiling the benchmarks. The cause: without a cache, wasmtime is built twice (debug and release). A timed-out job does not save its cache, so every later run would also have timed out. That is why commit 54f8171 raises the limit to 45 minutes. The cold run took 35 minutes 46 seconds, and this time it saved its cache.

### Live check

Real core, debug build, on pipe `phase7live`, with `--plugins-dir <repo>\sdk\fixtures\plugins`. The configuration, log and data folders were in `E:\cabinetos-scratch\phase7-live\`. The starting configuration granted only `crashy` and `spinner`.

```text
$ cabinetos-cli plugins list
crashy 0.1.0 (Crashy): active
  cmd:register [low, granted]: Adds the Crash command.
  commands: crashy.crash
hello 0.1.0 (Hello): needs_review (grant cmd:register events:emit)
  cmd:register [low, NOT granted]: Adds the Say Hello command to the palette.
  events:emit [low, NOT granted]: Tells the window each time it said hello.
hog 0.1.0 (Hog): needs_review (grant cmd:register)
reader 0.1.0 (Reader): needs_review (grant cmd:register fs:read)
  fs:read %TEMP%\cabinetos-plugins-test\reader [medium, NOT granted]: Reads the size of files in its test folder.
spinner 0.1.0 (Spinner): active
  commands: spinner.spin, spinner.burn
vetoer 0.1.0 (Vetoer): needs_review (grant jobs:intercept)
$ cabinetos-cli plugins grant hello cmd:register events:emit
hello: active
$ cabinetos-cli commands search hello
 1. score  184  hello.say                      -                Hello: Say Hello  [Hello]
$ cabinetos-cli commands exec hello.say
{
  "message": "hello from Hello"
}
$ cabinetos-cli commands exec crashy.crash
cabinetos-cli: after 0.0 s: crashy.crash: the plugin crashy crashed while running crashy.crash: wasm trap: wasm `unreachable` instruction executed; it said: panicked at crashy\src\lib.rs:32:9: crashy was asked to crash (plugin_error)
$ cabinetos-cli ls C:\Windows        (exit code 0)
core: enumerated 108 entries in 1.4 ms
$ cabinetos-cli commands exec crashy.crash
cabinetos-cli: after 0.0 s: crashy.crash: no command `crashy.crash` is registered: its plugin crashy is crashed: wasm trap: ... (unknown_command)
$ cabinetos-cli plugins reload crashy
crashy: active
$ cabinetos-cli commands exec spinner.spin
cabinetos-cli: after 5.1 s: spinner.spin: the plugin spinner crashed while running spinner.spin: it did not finish within 5000 ms (wasm trap: interrupt) (plugin_error)
$ cabinetos-cli ping
pong id=01M3K7AD9HHCWAKR247BY6NNMC protocol=5 core=0.1.0 rtt=0.57ms
--- `events watch` printed:
{"type":"plugin_crashed","plugin_id":"crashy","message":"wasm trap: wasm `unreachable` instruction executed; it said: panicked at crashy\\src\\lib.rs:32:9: crashy was asked to crash"}
{"type":"plugin_state_changed","plugin_id":"crashy","state":{"type":"crashed","message":"...","at_ms":1790572830360}}
{"type":"plugin_state_changed","plugin_id":"crashy","state":{"type":"loading"}}
{"type":"plugin_state_changed","plugin_id":"crashy","state":{"type":"active"}}
{"type":"plugin_crashed","plugin_id":"spinner","message":"it did not finish within 5000 ms (wasm trap: interrupt)"}
{"type":"plugin_state_changed","plugin_id":"spinner","state":{"type":"crashed","message":"...","at_ms":1790572835974}}
```

- The core's log for this run has the crash at ERROR, with boundary `plugin`, `plugin_id` `crashy`, and a WebAssembly backtrace in `fields.details`. It also has `hello`'s stdout line and its `log` call, both with boundary `plugin`.
- After rebuilding the fixtures with function names kept, one more crash showed frame 11 as `crashy.wasm!<crashy::Crashy as crashy::Guest>::on_command`.
- In an earlier run, `crashy` restarted by itself 5 s after its crash; the events showed `loading`, then `active`.
- No `cabinetos-core` process is left running.
- `%APPDATA%\CabinetOS` does not exist. `%LOCALAPPDATA%\CabinetOS` still holds only its old `logs` folder: no test created a plugins or data folder there.

### Decided

- Part 0 detects the bin's size and raises a conflict, instead of using the shell's "delete permanently?" warning — because that warning is a dialog, and it would block the headless core's job thread with nobody there to answer — undo: revert 263437b.
- The capabilities and their levels:
  - low: `cmd:register`, `config:read`, `events:emit`;
  - medium: `fs:read`, `fs:write`, `jobs:intercept`, `process:run`;
  - high: `net`, `credentials`.
  `jobs:intercept` is new — because `before-job` can stop the user's jobs, and that power must be granted like any other. `credentials` comes from PLAN.md's list — undo: `Capability` in `cabinetos-plugins/src/manifest.rs`, and `docs/plugins.md`.
- `fs:read` is always medium. The task suggested low for the data folder and workspaces — because a plugin's own folder needs no capability at all, and workspaces do not exist yet — undo: `Capability::level`.
- A plugin that asks for `process:run`, `net` or `credentials` ends in `failed`, not `needs_review` — because no grant could make it run, so waiting for a review would mislead the user — undo: `never_granted()` in `manifest.rs`.
- A plugin runs only when every capability it asks for is granted. There is no partial start — undo: `evaluate()` in `cabinetos-plugins/src/lib.rs`.
- Limits per call: fuel 5×10⁹; deadline 5 s (500 ms for `before-job`, 1 s for `deactivate`); epoch tick 10 ms; memory 256 MiB, enforced by a custom limiter whose trap names the limit; 32 instances, tables and memories each; 100,000 table elements. "1 instance" is too few — because a Rust component instantiates several core modules (its module, the WASI adapter, the glue code) — undo: `HostConfig::new` and the constants in `sandbox.rs`.
- The epoch ticker runs only while a plugin call runs — because an idle core should not wake 100 times a second — undo: `tick()`.
- Crash policy: restart 5 s after a crash. Three crashes within 10 minutes keep the plugin crashed until `reload_plugin`, which also clears the count — because a one-off crash should heal itself, but a plugin that crashes on every call must not loop — undo: `CRASH_WINDOW`, `CRASHES_BEFORE_GIVING_UP` and `restart_delay`.
- A command still running 2 s after its deadline is stuck inside a host function. The host gives up on that instance (`crashed`) and abandons its thread — because epoch interruption cannot stop a host call, and Rust cannot kill a thread — undo: `STUCK_GRACE`.
- `before-job`: the first refusal wins. A plugin that crashes, or does not answer within its 500 ms deadline plus a 1 s margin, counts as allowing the job — because a broken plugin must not block the user's file work — undo: `PluginHost::before_job`.
- The gate sits after the scan, before the destination folder is created — because the plugin then sees real totals, and a refused job leaves nothing behind — undo: move `ask_gate` in `cabinetos-jobs/src/run.rs`.
- `on-listing-opened` goes only to plugins whose `fs:read` or `fs:write` folders contain the opened folder. The path is given in sandbox form. At most 64 notifications wait per plugin — because telling a plugin about folders it may not read would leak what the user browses — undo: `listing_opened`.
- Sandbox paths: `C:\a\b` is `/C:/a/b`, and the plugin's own folder is `/data`. Job paths stay Windows paths — because the plugin only judges them — undo: `sandbox::guest_path`.
- `register-command` works only during `activate`, only for declared IDs, and only with `cmd:register`. Breaking a rule fails the start; a call after `activate` crashes the plugin — undo: `register_command` in `worker.rs`.
- A plugin's default keys that clash with a binding in use are dropped, with a warning; the command stays without keys. A plugin command whose ID equals a core command's is left out — because a plugin must never take keys or commands from the core or the user — undo: `add_plugin_command` in `cabinetos-core/src/settings.rs`.
- `config-get` hides the `plugins` section — because one plugin should not learn what others were granted — undo: `Bridge::config_value` in `cabinetos-core/src/plugins.rs`.
- The `plugin_event` payload is the plugin's own text, not parsed — because the core does not know plugin event formats — undo: the protocol type and `emit`.
- A command request without arguments passes `{}` to the plugin. A result that is not JSON becomes `plugin_error` — undo: `execute_command` in `connection.rs`.
- Config section: `plugins: { "<id>": { "enabled": true, "granted": [...] } }`. A plugin not listed is on, with nothing granted. IDs listed for plugins that are not installed are kept and logged — undo: `cabinetos-config/src/model.rs`.
- `grant_capabilities` only adds; there is no revoke request — because the task names none — undo: add a request later.
- Added the flag `--plugins-data-dir` and the variable `CABINETOS_PLUGINS_DATA_DIR`. Every test harness points both plugin folder variables at its temp folder — because tests and the live check must never read or write `%LOCALAPPDATA%` — undo: remove the flag and the env lines.
- The plugins crate reuses `cabinetos_config::PluginSettings` — because one type from the file to the host means no copying — undo: give it its own type.
- The protocol version stays 5 — because version 5 was introduced in this same phase (Part 0), and no client shipped in between — undo: set `PROTOCOL_VERSION` to 6 and regenerate the schemas.
- The crash message is the trap, then "; it said:" and the last stderr line, including the panic's place. Rust 1.98's new panic header form `thread '..' (1) panicked at` is read too — because the trap alone ("unreachable executed") does not say why — undo: `worker::summary` and `sandbox::remember`.
- If wasmtime cannot start, the core logs an ERROR and runs without plugins: `list_plugins` answers empty, and the other plugin requests answer `no_such_plugin` — because plugins are opt-in (Article 10) and must never stop the core from starting — undo: `plugins::start`.
- Template: Rust with wit-bindgen 0.61.1 for `wasm32-wasip2`. That target writes components directly, so `cargo-component` and `wasm-tools` were not installed. The release profile uses `opt-level "s"`, LTO and `strip = "debuginfo"`, which keeps function names for backtraces at about 20% more size — undo: `sdk/templates/plugins/Cargo.toml`.
- Toolchain addition: the `wasm32-wasip2` target for Rust 1.98.1. rustup installed it from `sdk/templates/plugins/rust-toolchain.toml` — undo: `rustup target remove wasm32-wasip2 --toolchain 1.98.1`.
- Added the extra fixture `hog`, for the memory-limit test — undo: delete `sdk/templates/plugins/hog` and `sdk/fixtures/plugins/hog`.
- wasmtime 49.0.1 with default features off. On: component model, Cranelift, runtime, std, backtrace, addr2line, demangle, parallel compilation. wasmtime-wasi uses Preview 2 only. No compiled-code cache, no GC — undo: `core/Cargo.toml` and `core/crates/cabinetos-plugins/Cargo.toml`.
- The CI core job limit went from 30 to 45 minutes. Reason: a run without cache took 35 min 46 s, and a timed-out job never saves its cache — undo: `.github/workflows/ci.yml`.

### Needs the user

1. **Remove the live-check scratch folder.** It holds 48 KB: the configuration, the log, the event and listing transcripts, and three empty plugin data folders. I created it in this phase; it did not exist before. A built-in safety check blocks `rm -rf` on a folder at the root of a drive, and I did not try to get around it. Command (WSL bash, runs from any folder):

   ```bash
   rm -rf /mnt/e/cabinetos-scratch
   ```

2. **Ready text for `docs/PLAN.md`, Phase 7.** Change the heading to `### Phase 7 — Plugin host and Tool Dock — core side done 2026-09-28`. Add after the "Done when:" paragraph:

   > Core side done 2026-09-28; the permissions review dialog, the Tool Dock and the sample Tool Extension wait for the UI (Phase 5). Built: the WIT package `cabinetos:plugin@0.1.0` in `sdk/wit/`; strict `plugin.json` manifests; capabilities with levels (the list above plus `config:read`, `events:emit` and `jobs:intercept`; `process:run`, `net` and `credentials` are declared but never granted yet), granted in the `plugins` section of `cabinetos.json` and applied without a restart; one store and one thread per plugin, with fuel (5×10⁹ per call), a 5 s deadline (500 ms for `before-job`) and 256 MiB of memory; trap handling that logs the plugin ID with a named WebAssembly backtrace, drops the instance, removes its commands and keeps the core serving, with a restart after 5 s unless the plugin crashed three times in ten minutes; the Rust sample `hello` and five test fixtures, committed as built components. Measured 2026-09-28 on this PC (debug build): `crashy.crash` answered `plugin_error` at once, and the same core listed `C:\Windows` (108 entries, 1.4 ms) right after; `spinner.spin` was stopped at 5.1 s. Guide: [plugins.md](plugins.md).

   Rows C (Tool Extensions in WebView2) and D (Tags as a first-party plugin) of the consistency table still say "Phase 7". Both are still open: C belongs to the UI part, and D is not started.

3. **Ready text for the README status line:**

   > Status: pre-alpha. Phases 0 to 4 of [the plan](docs/PLAN.md) are done, and the core side of Phase 7: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, runs copy, move and delete jobs on per-disk queues, and runs sandboxed WebAssembly plugins whose crashes it contains, all over a user-only named pipe (`core/`, 307 tests, CI green). Phase 5, the WinUI 3 shell, needs the .NET SDK; the core-side parts of Phases 6 and 8 (indexer, terminal host) continue meanwhile.

   New row for the README documents table:

   > | [docs/plugins.md](docs/plugins.md) | Core Plugins: manifest, capabilities, sandbox, limits, crashes. |

### Known gaps

- UI work waits for Phase 5: the permissions review dialog, the Tool Dock, and the sample Tool Extension (Markdown preview).
- The design's "Trust {author} for future updates" checkbox needs publisher identities (Phase 9); it is not built.
- There is no revoke request. To take a grant back, edit `granted` in the configuration file.
- `process:run`, `net` and `credentials` are never granted in this version.
- The stuck-plugin check covers commands only. A `before-job` or listing call stuck inside a host function is treated as allowed or dropped. Its thread stays until the host call returns, because a thread cannot be killed.
- Components are compiled at every start; there is no compiled-code cache. About 140 ms per plugin in a debug build on this PC.
- `list_plugins` does not show the crash count or the next restart time.
- The WIT parameter of `emit` is named `name`; the task text said `event-name`. This is naming only.
- There is only a Rust template.
- The `reader` fixture's folder is fixed at `%TEMP%\cabinetos-plugins-test\reader`, because manifest folders are static. The tests create and remove it.
- The Tags plugin (PLAN.md row D) is not started.

### Noticed out of scope

- The `help.about` example in `docs/ipc.md` still shows `"protocol_version":3`.
- CI cancels a running job when a new push arrives, so in a burst of pushes only the last commit gets a full CI result. The formatting slip in 9111823 was therefore never reported by CI; c586082 fixed it.
- `cargo deny` warns about duplicate crate versions from wasmtime (hashbrown, syn, windows-sys, and others). It also warns about licenses in `deny.toml` that no crate uses any more.
- The CI core job takes about 36 minutes when the cache is cold. If runs stay long with the cache, a separate job for the release build would run it in parallel.
- The test harnesses create `%TEMP%\cabinetos-jobs-test` and never remove it. It is empty at the end. After this phase's runs I removed the empty folder with `rmdir`.
