## Phase 9 report (core side)

Context: CabinetOS Phase 9, the core side — colour themes and the marketplace client (protocol version 10). All of it is built and pushed, and CI is green on the commit that contains it.

### Built

**Themes (Part A)**
- **The format** is one Rust type, `Theme`, in `cabinetos-protocol`. It is exported to `sdk/themes/theme.schema.json`, and a snapshot test keeps that file current.
- **Four shipped themes**, in `sdk/themes/`:
  - `default` uses the design tokens.
  - `nord`, `catppuccin-mocha` and `rose-pine-moon` use their published MIT palettes with the design's accents and Mica tints. Each has an attribution line.
- **The themes folder.** The new crate `cabinetos-themes` embeds the four themes with `include_str!`. At every start it writes any that are missing into `%LOCALAPPDATA%\CabinetOS\themes`, plus `theme.schema.json` for editors. `--themes-dir` and `CABINETOS_THEMES_DIR` pick another folder.
- **The core** (`src/themes.rs`) serves `list_themes` and `get_theme`.
  - When `ui.theme` changes (through `set_value` or a hand edit), clients get `config_changed` plus `theme_changed` with the whole theme.
  - The core watches the themes folder, so a saved edit of the theme in effect applies within a second.
  - A theme that is not valid never applies. `set_value` refuses it. A hand edit is reported with a `config_error` event, and the last good theme stays.
- **CLI:** `themes list` (marks the theme in effect with `*`) and `themes show [<id>]`.

**Marketplace (Part B)**
- **The index format** is `sdk/marketplace/index.schema.json`. The configuration has a new section, `marketplace: { index, allowInsecure }`.
- **The new crate `cabinetos-market`** does the work:
  - It reads the index from disk, or from the web with ureq on rustls. Certificates are checked against the Windows certificate store. The web index is cached with its ETag (a version tag from the server, so an unchanged index is not downloaded again).
  - Plain `http:` is refused unless `allowInsecure` is on.
  - An item that breaks the format is left out; the rest of the index still works.
  - An install downloads into a temporary file, checks the SHA-256, unpacks into a staging folder, checks the files against the index listing and `minCoreVersion`, and only then copies them into place.
  - It records exactly which files it placed. Uninstall removes only those, and never a plugin's own data folder.
- **The core** (`src/market.rs`) handles `marketplace_refresh`, `marketplace_search` (ranked like the palette), `install_extension`, `uninstall_extension` and `list_tools`. It sends the events `install_progress` (at most 30 a second), `install_finished` and `tools_changed`.
  - A plugin install clears the plugin's earlier grants first, then the plugin host reloads it, so it arrives as `needs_review`.
  - A theme that `ui.theme` names applies as soon as it is installed.
  - The new `PluginHost::remove` forgets a plugin before its files are deleted.
- **`sdk/marketplace/build-index.ps1`** builds a local index from `sdk/fixtures/plugins` (each plugin as a zip) and the four themes. It works in Windows PowerShell 5.1 and PowerShell 7.
- **CLI:** `market refresh | search <q> [--kind] | install <id> [--version] | uninstall <id> | tools`. `install` shows a progress line.

### Commits (all on `main`)
- `aee0a5d` — themes, protocol version 10.
- `0a9b980` — the marketplace client.
- `6bbaac6` — the core no longer reads `CABINETOS_TOOLS_DIR` (see Decided, item 11).
- `15eb2f0` — an install now fails, instead of silently skipping a file whose name it cannot record.
- `6b8c518` and `c1c014b` — a test that the HTTPS client sets up its TLS, then its formatting.
- `75a80d3` — CI: the core job's time limit raised from 45 to 60 minutes.
- `02c9028` — `docs/themes.md` now points to how the window applies themes.

### Checks
- **Local:** build passes; tests: 477 passed, 0 failed, 5 ignored (434 before tonight). Clippy with `-D warnings`, fmt and `cargo deny check` are clean. The release build passes. The UI tests (327) passed on the combined tree.
- **CI is green:** https://github.com/OliverD25/cabinetos/actions/runs/36463695243 (commit `133c2e7`; all four jobs passed; it contains all my commits).
- **Two CI problems on the way:**
  - The first core run after my new dependencies passed every step in 41 minutes. Saving the cache took 4 more, which hit the 45-minute limit, so the job counted as cancelled and no cache was saved. With 60 minutes, the green run took 48 minutes, and the cache is saved now.
  - Several runs were cancelled because newer pushes to `ci.yml` restart the core job.
- **CI can no longer start jobs.** Since about 18:41 UTC, new runs fail at once with: "recent account payments have failed or your spending limit needs to be increased". So the UI agent's pushes after `133c2e7` have no CI result.

### Live check
Done on 2026-09-28 with the debug build. All folders were in the scratchpad, and the index was built there by `build-index.ps1`.

```
$ cabinetos-cli themes list
  catppuccin-mocha   Catppuccin Mocha   dark  accent #CBA6F7  1.0.0 by CabinetOS
* default            Default            dark  accent system   1.0.0 by CabinetOS
  nord               Nord               dark  accent #88C0D0  1.0.0 by CabinetOS
  rose-pine-moon     Rosé Pine Moon     dark  accent #EBBCBA  1.0.0 by CabinetOS
$ cabinetos-cli config set ui.theme nord
ui.theme = "nord"
$ cabinetos-cli market refresh
10 items from C:\…\scratchpad\live\market\index.json
hello              0.1.0    plugin Hello by CabinetOS, 26.37 KiB
  The sample Core Plugin: a Say Hello command that answers, writes a log line and sends an event.
  asks for: cmd:register (low), events:emit (low)
… (crashy, hog, reader, spinner, vetoer, and the four themes)
$ cabinetos-cli market install hello
hello: 26.37 KiB of 26.37 KiB
installed hello 0.1.0 (plugin)
$ cabinetos-cli plugins list
hello 0.1.0 (Hello): needs_review (grant cmd:register events:emit)
  cmd:register [low, NOT granted]: Adds the Say Hello command to the palette.
  events:emit [low, NOT granted]: Tells the window each time it said hello.
files after install: plugins/hello/plugin.json, plugins/hello/plugin.wasm
$ cabinetos-cli market uninstall hello
uninstalled hello
$ cabinetos-cli plugins list
no plugins are installed          (the plugins folder is empty again)
```

`events watch` printed the following during those commands. The `theme_changed` line is shortened here; the full event was 1149 bytes.

```
{"type":"config_changed","changed":["ui.theme"]}
{"type":"theme_changed","theme":{"id":"nord","name":"Nord","accent":"#88C0D0","mica":{"tint":"#2E3440","opacity":0.88},"palette":{…},"terminal":{…}}}
{"type":"install_progress","extension_id":"hello","bytes":0,"total":27003}
{"type":"install_progress","extension_id":"hello","bytes":27003,"total":27003}
{"type":"plugin_state_changed","plugin_id":"hello","state":{"type":"needs_review","missing":["cmd:register","events:emit"]}}
{"type":"install_finished","extension_id":"hello","ok":true,"message":"installed hello 0.1.0 (plugin)"}
```

The core's log had no warnings. Nothing touched the network: the web path was only tested against a server on 127.0.0.1.

### Decided
1. **The default theme follows Windows.** It has `accent: null` (the Windows accent) and `mica: null` (plain Mica). The design's `#60CDFF` is the Windows default dark accent, and its `rgba(32,32,32,.86)` only imitates Mica in the prototype.
2. **Colour mapping for the named themes.** Palette colours come from the published palettes; accents and tints come from the design. The text shades, the translucent surfaces and the folder's front flap (the palette's yellow mixed with 35 % white) are my own mapping.
3. **Two optional keys were added to the theme format:**
   - `attribution`, because JSON has no comments and the task asked for an attribution line;
   - `$schema`, for editors. It is never sent to clients.
   Colours are sent in upper case.
4. **Shipped themes are rewritten when missing, at every start.** An edited copy is kept; a deleted one comes back.
5. **`get_theme` without `theme_id` answers the theme in effect.** A client needs this at start, because `ui.theme` may name a theme that cannot be used.
6. **Unknown themes:**
   - `set_value` refuses them.
   - A hand edit is kept for the other settings, and the theme stays.
   - At start, the default theme in memory is used and the log says why.
   - Theme problems use `config_error` with `line` and `column` set to `null`; the file and the position are in the message.
7. **Field names:** `theme_id` and `extension_id` (not `id`, which is the request's own ID). `fetched_at_ms` instead of `fetched_at`, following the protocol's `_ms` rule for times.
8. **Added requests and error codes.** `list_tools` was added, because clients need the tool list at start, not only through `tools_changed`. New error codes: `no_such_theme`, `no_such_extension`, `marketplace_error`, `hash_mismatch`, `incompatible`.
9. **The HTTP and zip stack:**
   - ureq 3.4 with the features `rustls-no-provider`, `_ring` and `platform-verifier`: 17 new crates on Windows. `webpki-roots` (ureq's default certificate list) is left out, because its license (CDLA-Permissive-2.0) is not on the allow list.
   - `zip` 8.6 with deflate only.
   - `sha2`, which was already in the dependency tree.
10. **The default index** is `https://marketplace.cabinetos.invalid/index.json`. `.invalid` is a name that can never resolve, so nothing is fetched until a real index exists.
11. **Folders.** `--marketplace-dir` / `CABINETOS_MARKETPLACE_DIR` hold the index cache, downloads in progress and `installed.json`. For tools, only the flag `--tools-dir` exists, with no environment variable. Reason: the UI agent's `docs/tool-extensions.md` uses `CABINETOS_TOOLS_DIR` for a development folder, and the core inherits the window's environment.
12. **Trust rules** (recorded in `docs/marketplace.md`):
    - every plugin install, updates too, waits for review;
    - a download whose hash does not match is deleted and nothing is installed;
    - a plugin whose `plugin.json` differs from the index (capabilities or version) is refused;
    - `minCoreVersion` is checked;
    - no code runs while installing;
    - zip entries that point outside their folder, links, and oversized archives stop the install;
    - the core fetches only when a client asks;
    - it never replaces something it did not install (`already_exists`), and that includes the four shipped themes;
    - `verified` is shown but not checked.
13. **Update and uninstall.** An install over an earlier marketplace install is an update. Uninstall refuses the theme in effect, and leaves the plugin's data folder and its entry in the configuration.
14. **`tool.json` checks.** The core checks `id` (at most 63 characters), `name`, `version`, `author` and `description`. The window checks the rest.
15. **`build-index.ps1` requires `-OutDir`**, so it never writes into the repository by default.
16. **Every test that starts a core** points the themes folder at its temp folder: 13 Rust harnesses and 2 spots in the UI tests.
17. **Shared code instead of copies.** The new `ConfigWatcher::watch_dir` reuses the configuration file's watcher for the themes folder. The new `cabinetos_commands::rank` gives marketplace search the palette's scoring.

### Needs the user
- **GitHub Actions billing** ("Billing & plans" in the GitHub settings). New CI runs no longer start. I changed nothing there.
- **Where to publish a real marketplace index.** Publishing is outward-facing, and nothing was published tonight.
- **Optional:** confirm that the default theme should follow the Windows accent, rather than always using `#60CDFF`.

### Known gaps
- **Not built:** publisher identities and signatures, and "Trust {author} for future updates".
- **HTTPS against a real server was not exercised**, as the rules required. The TLS setup itself is tested against a local port.
- **Fragile feature name:** `_ring` is an internal ureq feature name. The TLS test would catch it if a ureq update removed it.
- **Updates are not atomic.** An update that fails half-way can leave the extension broken. A failed plugin update leaves the old version waiting for review, because its grants were already cleared.
- **Redirects:** relative download paths are resolved against the index's first address, not the address after a redirect.
- **No-capability plugins:** a plugin that asks for no capability starts right after install, because it has nothing to review.
- **`size` is only an upper limit:** a smaller download with the right hash is accepted.
- **Lagging clients:** `tools_changed` is not re-sent to a client that fell behind on events; it should call `list_tools`.
- **Shipped theme updates:** a new version of a shipped theme does not replace an existing copy.
- **No light themes** yet.

### Noticed out of scope
- `docs/tool-extensions.md` (the UI agent's file) may still say the marketplace "will install tools (Phase 9)". I did not check it after their marketplace commit.
- A push that edits `ci.yml` restarts the core job and cancels a running one, which costs about 48 minutes each time. The CI change detection may be worth splitting.
- The UI agent's commit notes that `plugins.list` is not in the core's command registry yet.
- The older plugin tests write under `%TEMP%\cabinetos-jobs-test\`.
- cargo-deny warns about duplicate crate versions: base64, getrandom, cpufeatures.

### Ready text for `docs/PLAN.md`, Phase 9
Change the heading to `### Phase 9 — Marketplace and theme engine — core side done 2026-09-28`. Add after the "Produces:" paragraph:

> Core side done 2026-09-28. Built: the JSON theme format as one Rust type (`Theme` in `cabinetos-protocol`), exported to `sdk/themes/theme.schema.json` and read strictly: `accent` (`null` follows the Windows accent), `mica` (tint and opacity, `null` for plain Mica), the palette of the design tokens, and 16 terminal colours. Four themes ship inside the core and are written into `%LOCALAPPDATA%\CabinetOS\themes` when missing: `default` (the design tokens, with the Windows accent and plain Mica), `nord`, `catppuccin-mocha` and `rose-pine-moon` (their MIT palettes with the design's accents and Mica tints). `ui.theme` names the theme in effect. Every client gets `theme_changed` with the whole theme when it changes or its file is saved. A theme that is not valid never applies: the last good theme stays, and `config_error` says why. The marketplace client `cabinetos-market` reads a static index (`sdk/marketplace/index.schema.json`) from a folder or over HTTPS (ureq on rustls, the Windows certificate store, a cache with ETag); plain `http:` only with `marketplace.allowInsecure`. Installs are checked by SHA-256 and `minCoreVersion`, unpacked in a staging folder, compared with the index, and recorded file by file, so an uninstall removes exactly those files. An installed plugin arrives as `needs_review`, even after an update. The core reaches the network only when a client asks. Protocol version 10. `sdk/marketplace/build-index.ps1` builds a local index from the fixture plugins and the shipped themes. Not built: publisher identities (`verified` is only shown), and a public index (`marketplace.index` defaults to a placeholder that cannot resolve). Checked 2026-09-28 on the development PC: `config set ui.theme nord` brought `theme_changed` with the whole theme; `market install hello` from a local index installed the plugin as `needs_review`, and `market uninstall hello` removed its two files. Guides: [themes.md](themes.md), [marketplace.md](marketplace.md).

The UI side landed tonight from the UI agent (the theme picker and applying themes: `630ba28`, `4fe16cd`; the marketplace view: `75fea66`). Please merge their part in.

### Ready text for the README status line (core half)
> Status: pre-alpha. Phases 0 to 4 and 6 to 8 of [the plan](docs/PLAN.md) are done, and Phase 9 on the core side: the governing documents, and a Rust core that lists and watches directories in shared memory, serves its configuration, commands and keymap, runs copy, move and delete jobs on per-disk queues, runs sandboxed WebAssembly plugins whose crashes it contains, searches whole NTFS volumes through an elevated indexer (or walks folders without it), runs shells in pseudo-consoles, applies JSON colour themes live, and installs plugins, themes and tools from a static marketplace index with each download's SHA-256 checked, all over a user-only named pipe, with the indexer behind a read-only pipe of its own and each shell's bytes on a pipe of their own (`core/`, 477 tests, CI green).

Keep the UI half as the UI agent reports it. Its test count has grown past 327.

### Files
- `core/crates/cabinetos-protocol/src/theme.rs`, `market.rs` — new: the theme and marketplace wire types.
- `core/crates/cabinetos-protocol/src/message.rs`, `id.rs`, `lib.rs`, `schema.rs` — the new messages, the extension ID rule, version 10, and the two new schema exports.
- `core/crates/cabinetos-themes/` — new crate.
- `core/crates/cabinetos-market/` — new crate: `index.rs`, `install.rs`, `tools.rs`, `tests/install.rs`.
- `core/crates/cabinetos-config/src/watch.rs`, `model.rs`, `lib.rs` — `watch_dir`, and the `marketplace` section.
- `core/crates/cabinetos-commands/src/search.rs`, `lib.rs` — `rank`.
- `core/crates/cabinetos-plugins/src/lib.rs` — `PluginHost::remove`.
- `core/crates/cabinetos-core/src/themes.rs`, `market.rs` — new.
- `core/crates/cabinetos-core/src/connection.rs`, `events.rs`, `lib.rs`, `main.rs`, `settings.rs` — wiring and flags.
- `core/crates/cabinetos-core/tests/themes.rs`, `market.rs` — new; 13 test harnesses now set `CABINETOS_THEMES_DIR`; `core_process.rs` pins version 10.
- `core/crates/cabinetos-cli/src/themes.rs`, `market.rs` — new; `main.rs` wires them in.
- `core/Cargo.toml`, `core/Cargo.lock` — the new crates and dependencies.
- `sdk/themes/*`, `sdk/marketplace/*` — new; `sdk/protocol/*.schema.json` and `sdk/config/cabinetos.schema.json` regenerated.
- `docs/themes.md`, `docs/marketplace.md` — new.
- `docs/ipc.md`, `docs/config.md`, `docs/plugins.md`, `core/README.md`, `sdk/README.md` — updated.
- `.github/workflows/ci.yml` — trigger paths for `sdk/themes` and `sdk/marketplace`; core job limit 60 minutes.
- `ui/CabinetOS.Core/Protocol/MessageCodec.cs` — the four new event types.
- `ui/CabinetOS.Tests/EndToEndTests.cs` — version 10, and a temp themes folder.

**Recap:** both parts are built, pushed and green in CI. The live check matches the Definition of Done. Still open: the GitHub billing block that stops new CI runs, choosing where to publish a real index, and publisher trust.
