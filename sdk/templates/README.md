# sdk/templates — starter projects

Core Plugins are headless WebAssembly components that implement the
`core-plugin` world in [`../wit/plugin.wit`](../wit/plugin.wit). The guide is
[`../../docs/plugins.md`](../../docs/plugins.md): the capabilities, the
sandbox, the limits, and what happens when a plugin crashes.

Tool Extensions (the UI layer, Constitution Article 11) arrive with the Tool
Dock in Phase 5; there is no template for them yet.

## What is here

- `plugins/` — a Cargo workspace of Rust plugins, built with
  [`wit-bindgen`](https://crates.io/crates/wit-bindgen) for the
  `wasm32-wasip2` target. Rust writes a component for that target directly,
  so no `cargo-component` or `wasm-tools` is needed.
  - `hello` — **the template.** One command, `hello.say`, that answers with
    JSON, prints a line (the core logs it) and sends an event.
  - `crashy`, `spinner`, `hog`, `reader`, `vetoer`, `fetcher`, `watcher`, `requester` —
    test fixtures. Each one breaks a rule on purpose, or uses one capability, so
    the tests can prove the host contains it.
- `build-fixtures.ps1` — builds every plugin in `plugins/` and copies the
  component and its `plugin.json` to `../fixtures/plugins/<id>/`.

| Plugin | Commands | Capabilities | What it shows |
|---|---|---|---|
| `hello` | `hello.say` | `cmd:register`, `events:emit` | A working plugin: result, log line, event |
| `crashy` | `crashy.crash` | `cmd:register` | A panic is a trap: the plugin crashes, the core goes on |
| `spinner` | `spinner.spin`, `spinner.burn` | `cmd:register` | An endless loop hits the 5 s deadline (`spin`) or its fuel (`burn`) |
| `hog` | `hog.eat` | `cmd:register` | Asking for 1 GiB of memory hits the 256 MiB limit |
| `reader` | `reader.size`, `reader.write` | `cmd:register`, `fs:read` | Files are reachable only under granted folders, and only as granted |
| `vetoer` | none | `jobs:intercept` | `before-job` stops a job whose destination contains `forbidden` |
| `fetcher` | `fetcher.get` | `cmd:register`, `net` | `http-request` reaches only the named host, and the core adds the secret to the header without the plugin seeing it; the command asks for text (`input`), which it takes as the URL |
| `requester` | `requester.ask`, `requester.setting` | `cmd:register`, `config:read`, `events:emit`, `core:request` | `core-request` passes only the request types its manifest lists (it lists `hello` and `secret_get` on purpose: refused whatever a manifest says), and `config-get` shows only the plugin's own `settings` |
| `watcher` | `watcher.watch`, `watcher.unwatch` | `cmd:register`, `events:emit`, `fs:watch` | `watch-folder` works only under its roots; each `on-event` goes on through `emit`, so a test sees the changes as `plugin_event` |

The `reader` fixture reads `%TEMP%\cabinetos-plugins-test\reader`; the tests
create and remove that folder. The `fetcher` manifest names
`localhost:8090` and the secret `fetcher-test`; the tests start their own
web server on a free port of `127.0.0.1` and write that port into their
copy of the manifest, so a busy port 8090 never fails them. The `watcher`
manifest names `%TEMP%\cabinetos-plugins-test\watcher`; the tests point
their copy at a temporary folder of their own.

## Start a new plugin

1. Copy `plugins/hello` to `plugins/<your id>` and add the folder to
   `members` in `plugins/Cargo.toml`. The ID is lower case letters, digits
   and `-`, and starts with a letter.
2. In `plugin.json`, set `id`, `name`, `author` and `description`, and list
   the capabilities you need with a `reason` each, and your commands. Every
   command ID starts with `<your id>.`. The manifest format is in
   [`../../docs/plugins.md`](../../docs/plugins.md).
3. In `src/lib.rs`, return the same ID and version from `info`, register
   your commands in `activate`, and answer them in `on-command`.
4. Build: `cargo build --release` in `plugins/`. The component is
   `plugins/target/wasm32-wasip2/release/<your_id>.wasm` (Cargo turns `-`
   into `_`).
5. Install: copy it as `plugin.wasm`, with `plugin.json`, into
   `%LOCALAPPDATA%\CabinetOS\plugins\<your id>\`.
6. Grant what it asks for: `cabinetos-cli plugins grant <your id> <capability>...`.
   A plugin runs only once every capability it asks for is granted; until
   then `cabinetos-cli plugins list` shows it as `needs_review`.

The toolchain is the one in `plugins/rust-toolchain.toml`: the same Rust as
the core, plus the `wasm32-wasip2` target, which `rustup` installs on first
use.

## Rebuild the test fixtures

The core's tests load the committed files in `../fixtures/plugins`, so CI
needs no WebAssembly toolchain. After changing a plugin here, rebuild them
from any folder and commit the result:

```powershell
powershell -ExecutionPolicy Bypass -File <repo>\sdk\templates\build-fixtures.ps1
```
