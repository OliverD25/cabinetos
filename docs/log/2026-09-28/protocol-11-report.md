## Report: the shell's nine small requests (protocol version 11)

Context: CabinetOS, the shell's nine small requests (protocol version 11). All nine are built, each in its own commit, and pushed to main.

## Built
1. **Registry entries.** 19 window commands are in the core's registry now, target `ui`, so the palette ranks them and the user can rebind them (51 commands in all):
   - with titles the UI already used: `plugins.list` (Plugins: Show Plugins) and `editor.openMarkdownPreview` (Editor: Open Markdown Preview, `ctrl+k v` in `filesView`);
   - without default keys: `terminal.new`, `terminal.show`, `terminal.close`, `terminal.reload`, `search.scope` (Search: Whole Volume), `editor.close`, `editor.reload`, `transfer.pause`, `transfer.resume`, `transfer.cancel`, `transfer.close`, `transfer.minimize`, `transfer.restore`, `transfer.next`, `conflict.resolve`, `sidebar.pin`, `sidebar.unpin`.
   - docs/keybindings.md table and notes updated.
2. **`ui.dockSize`**: `{ bottom, right }` in pixels, both `null` by default. It shows in the first-run file, and `set_value ui.dockSize.bottom 320` works. Config schema regenerated; docs/config.md updated.
3. **Type-name fallback.** When the shell answers a program identifier (a ProgID: no space, and either no capital letter or ending in "file"), the core answers `{EXT} File`. On this PC `.gitattributes` and `.gitignore` showed `txtfile`; now they show `GITATTRIBUTES File` and `GITIGNORE File`. It applies only to names looked up by extension. Tests: a pure-function test, and a shell test on a real `.gitattributes`.
4. **Request IDs and `items_per_second`.**
   - "job queued" was logged without the request ID: the core ran that work on Tokio's blocking pool, whose threads start with no span. A new helper, `blocking_in_span`, runs every request's blocking work inside the request's span (spawn_reply, start_job, volume_info, run_blocking), so install, theme and file-operation lines carry the ID too.
   - `job_progress` has `items_per_second`: files and folders per second, the same moving average as `speed_bps`, two decimals. It is absent in a job's first record, and 0 while paused and in the final record. docs/jobs.md updated.
   - An end-to-end test checks both. I confirmed it fails with the old code.
   - Protocol version 11 starts in this commit.
5. **Plugin command arguments** (docs only): docs/plugins.md, "What the shell passes": `{"path", "paths"}`, full paths; a missing key means "no selection"; reading the files still needs `fs:read`.
6. **Marketplace: what is installed.** New `Market::offers`: one item per extension, the newest version this core can run (an extension with no runnable version is left out), in the order the index first lists each. Each item carries `installedVersion` from the marketplace's own install record. `install_extension` still sees every version. `market refresh` prints "installed 0.1.0". docs/marketplace.md and ipc.md updated.
7. **`list_themes` gains `mica`** (tint and opacity, or `null`) per theme. `themes list` prints it.
8. **Theme kind `system`.** Shipped `default.json` is now `kind: "system"`, version 1.1.0. `dark` and `light` are unchanged; `theme_changed` carries the theme as is. Theme schema and protocol schemas regenerated; docs/themes.md updated; build-index.ps1 describes a system theme.
9. **`tools_changed` for a client that fell behind** is sent again after the other re-sent events. The tools folder is read on the blocking pool, not on the connection loop.

## Commits (all on main, in order)
- 01dac52 core: the window's own commands in the registry
- 7e26901 config: ui.dockSize keeps a dragged dock size across restarts
- 48bf516 core: a readable type name where the shell answers a ProgID
- 0993572 core: a job's log lines keep its request ID; job_progress gains items_per_second (protocol 11)
- 84127b7 docs: what a plugin command gets from the shell
- 06d9208 core: the marketplace offers one version per extension and says what is installed
- 0758f08 core: list_themes carries each theme's Mica tint
- ece3c14 core: a theme kind that follows Windows, for the default theme
- 9eea7cd core: a client that fell behind gets the tools list again

## Checks
- Local, on the final tree: build passes; tests: 482 passed, 0 failed, 5 ignored (477 before); clippy with `-D warnings`: 0 findings; fmt clean; `cargo deny check`: advisories, bans, licenses, sources ok. The release build also passes.
- UI tests: 412 passed, 0 failed, run after every protocol commit. The UI end-to-end test now pins version 11.
- CI: not started (account billing). Every push tonight stopped after 3 to 4 seconds with "The job was not started because recent account payments have failed or your spending limit needs to be increased."

## Live check
2026-09-28, debug build, isolated folders in the scratchpad, local index built by build-index.ps1.

```
$ cabinetos-cli ping
pong id=01M3MS2QF9RWZQMYSACVTN2Y84 protocol=11 core=0.1.0 rtt=1.27ms
$ cabinetos-cli commands list        (51 lines; the new entries:)
sidebar.pin                    -                Sidebar: Pin Folder
sidebar.unpin                  -                Sidebar: Unpin Folder
search.scope                   -                Search: Whole Volume
editor.openMarkdownPreview     ctrl+k v         Editor: Open Markdown Preview
editor.close                   -                Editor: Close Editor
editor.reload                  -                Editor: Reload Editor
transfer.pause                 -                Transfer: Pause
transfer.resume                -                Transfer: Resume
transfer.cancel                -                Transfer: Cancel
transfer.minimize              -                Transfer: Minimize Panel
transfer.restore               -                Transfer: Show Panel
transfer.next                  -                Transfer: Show Next Job
transfer.close                 -                Transfer: Close Panel
conflict.resolve               -                Transfer: Resolve Conflict
plugins.list                   -                Plugins: Show Plugins
terminal.new                   -                Terminal: New Terminal
terminal.show                  -                Terminal: Show Terminal
terminal.close                 -                Terminal: Close Terminal
terminal.reload                -                Terminal: Reload Terminal
$ cabinetos-cli describe <folder with .gitattributes>
    0  .editorconfig                    EDITORCONFIG File            ext:.editorconfig
    1  .gitattributes                   GITATTRIBUTES File           ext:.gitattributes
    2  .gitignore                       GITIGNORE File               ext:.gitignore
    3  Cargo.lock                       LOCK File                    ext:.lock
    4  data.json                        JSON Source File             ext:.json
    5  main.rs                          RS File                      ext:.rs
    6  notes.txt                        Text Document                ext:.txt
    7  README.md                        Markdown Source File         ext:.md
    8  script.ps1                       Windows PowerShell Script    ext:.ps1
    9  settings.toml                    TOML File                    ext:.toml
10 of 10 entries described in 39.4 ms (round trip)
(before the change, the same core build showed "txtfile" for .gitattributes and .gitignore)
$ cabinetos-cli themes list
  catppuccin-mocha   Catppuccin Mocha   dark   accent #CBA6F7  mica #1E1E2E 0.9   1.0.0 by CabinetOS
* default            Default            system accent system   mica plain         1.1.0 by CabinetOS
  nord               Nord               dark   accent #88C0D0  mica #2E3440 0.88  1.0.0 by CabinetOS
  rose-pine-moon     Rosé Pine Moon     dark   accent #EBBCBA  mica #232136 0.9   1.0.0 by CabinetOS
$ cabinetos-cli market install hello
hello: 26.37 KiB of 26.37 KiB
installed hello 0.1.0 (plugin)
$ cabinetos-cli market refresh
10 items from C:\…\scratchpad\p11live\market\index.json
crashy             0.1.0    plugin Crashy by CabinetOS tests, 23.46 KiB
  …
hello              0.1.0    plugin Hello by CabinetOS, 26.37 KiB, installed 0.1.0
  The sample Core Plugin: a Say Hello command that answers, writes a log line and sends an event.
  asks for: cmd:register (low), events:emit (low)
  …
$ cabinetos-cli plugins list
hello 0.1.0 (Hello): needs_review (grant cmd:register events:emit)
$ cabinetos-cli market uninstall hello
uninstalled hello
$ cabinetos-cli market refresh | grep ^hello
hello              0.1.0    plugin Hello by CabinetOS, 26.37 KiB
```

On the wire the field is `installedVersion` (see Decided, item 7). The core exited with code 0.

## Decided
1. **Titles and categories.** Two titles come from the UI's own window commands; the rest are mine. `conflict.resolve` is in the Transfer category, so it groups with the panel. Transfer titles are short ("Transfer: Pause"). `ctrl+k v` stays the design's binding; it is the one chord whose second key goes without Ctrl (noted in keybindings.md).
2. **Palette test expectations changed.** With the new commands:
   - "sidebar" now ranks Sidebar: Pin/Unpin first and the toggle third;
   - "terminal" lists the Terminal-category commands first, then View: Toggle Integrated Terminal.
   The tests now check that all three sidebar commands lead and that "toggle sidebar" finds the toggle first.
3. **`ui.dockSize`:** any u32; the window keeps it within the design's limits. The nulls are written in the first-run file, so the setting is visible.
4. **ProgID rule:** no whitespace, and either no uppercase letter or ending in "file" (case-insensitive). It applies only to names looked up by extension, so "File folder" and "File" are untouched.
5. **Request spans:** I fixed every request path that runs blocking work, not only start_job. It was the same cause, and one small helper covers it.
6. **`items_per_second`** is a small `Rate` type that is never NaN, so the protocol's messages keep exact equality (like `Opacity` and `Stars`). Two decimals; absent in the first record; 0 when paused and at the end.
7. **Wire name `installedVersion`,** not `installed_version`: marketplace items are in the index file's camelCase format (like `minCoreVersion`). It is left out when nothing is installed (like `rating`). Only the marketplace's own record counts, so a shipped theme has none.
8. **`mica` in `themes`** is always present (`null` or an object), like `accent`.
9. **`default.json` version raised to 1.1.0,** because its content changed. Its palette keeps the design's dark-mode tokens; docs/themes.md says light mode is the window's choice.
10. **The tools re-send runs as a task,** so the connection loop does no disk work. The event comes after the other re-sent events.

## Needs the user
- GitHub Actions billing ("Billing & plans"): CI cannot start any job, so nothing tonight has a CI result. Only the creator can change this.

## Known gaps
- Item 9 has no automated test. A client only falls behind when 1024 events are waiting, which a test cannot cause reliably. The older re-sends (keymap, theme, jobs, plugins) have no test either.
- The creator's own `%LOCALAPPDATA%\CabinetOS\themes\default.json` still says `"kind": "dark"` and version 1.0.0: a new shipped version never replaces an existing copy. Deleting that file brings 1.1.0 at the next start.
- The UI still treats `system` as dark (`IsLight => Kind == "light"`). Following Windows' light/dark mode is the UI agent's part.
- The ProgID rule could also rename a real one-word type name that ends in "file" (for example a hypothetical "Profile" type).
- Now redundant in the UI (the UI agent's to remove):
  - its local stand-ins for `plugins.list` and `editor.openMarkdownPreview`, now that the core lists them;
  - its own folding of marketplace versions.

## Noticed out of scope
- `cabinetos-cli`'s job progress line could show items per second for deletes; it shows only bytes.
- Background work that no request started (watched-listing refresh, the drive watcher) still logs without a request ID. That is by design; I left it.

## Ready text for docs/PLAN.md
Under Phase 5, after the "Core additions for the UI (protocol version 8)" paragraph:

> **Core additions for the shell (protocol version 11), 2026-09-28.** The window's own commands are in the core's registry, so the palette ranks them and the user can rebind them: `plugins.list`, `editor.openMarkdownPreview` (`ctrl+k v` in the files view, the design's binding), `editor.close`, `editor.reload`, `terminal.new`, `terminal.show`, `terminal.close`, `terminal.reload`, `search.scope`, the transfer panel's `transfer.pause`, `transfer.resume`, `transfer.cancel`, `transfer.minimize`, `transfer.restore`, `transfer.next`, `transfer.close`, `conflict.resolve`, and `sidebar.pin` and `sidebar.unpin` (51 commands in all). `ui.dockSize` keeps a dragged Tool Dock size across restarts (`bottom` and `right`, in pixels; `null` for the design's size). A type name the shell answers as a program identifier (`txtfile` for `.gitattributes`) shows as `{EXT} File`, as in Explorer. `job_progress` has `items_per_second`, so a delete or a move on one volume shows its pace, and a job's log lines carry the ID of the `start_job` that queued it. A plugin command run from the context menu or the palette gets `{"path", "paths"}` ([plugins.md](plugins.md)). A client that fell behind on events gets `tools_changed` again.

Under Phase 9, after "The UI half of Phase 9" paragraph:

> **Additions (protocol version 11), 2026-09-28.** A client is offered one marketplace item per extension, the newest version this core can run, each with `installedVersion` from the marketplace's record of installs. `list_themes` carries each theme's Mica tint. A theme's `kind` may be `system` (follow Windows' light or dark mode); the shipped `default` theme has it (version 1.1.0).

## Ready text for the README status line
Change "(`core/`, 477 tests)" to "(`core/`, 482 tests)". The UI count stays 412.

## Files
- core/crates/cabinetos-commands/src/registry.rs, search.rs — the 19 entries; palette tests.
- core/crates/cabinetos-config/src/model.rs, lib.rs — `DockSize`.
- core/crates/cabinetos-fs/src/hydrate.rs — `readable_type_name`, two tests.
- core/crates/cabinetos-protocol/src/job.rs, lib.rs, market.rs, theme.rs, message.rs — `Rate` and `items_per_second`, version 11, `installed_version`, `ThemeInfo.mica`, `ThemeKind::System`, tests.
- core/crates/cabinetos-jobs/src/progress.rs, job.rs — the items meter.
- core/crates/cabinetos-market/src/install.rs, tests/install.rs — `Market::offers`, a test.
- core/crates/cabinetos-themes/src/lib.rs — shipped-theme test for `system`.
- core/crates/cabinetos-core/src/connection.rs — `blocking_in_span`, the tools re-send.
- core/crates/cabinetos-core/src/market.rs — offers in refresh and search.
- core/crates/cabinetos-core/tests/config.rs, shell_requests.rs, jobs.rs, market.rs, themes.rs, core_process.rs — updated and new checks; version 11.
- core/crates/cabinetos-cli/src/market.rs, themes.rs, jobs.rs — installed version, mica, `system`.
- sdk/themes/default.json, theme.schema.json; sdk/protocol/event and response schemas; sdk/config/cabinetos.schema.json; sdk/marketplace/index.schema.json, build-index.ps1.
- docs/keybindings.md, config.md, ipc.md, jobs.md, plugins.md, marketplace.md, themes.md; core/README.md.
- ui/CabinetOS.Tests/EndToEndTests.cs — version 11.

Recap: all nine requests are done and pushed, and all local checks pass. CI cannot run until the account billing is fixed. The UI agent still has to make `system` follow Windows' light or dark mode.
