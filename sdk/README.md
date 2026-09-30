# sdk/ — the plugin and protocol SDK

This folder fills up in Phases 1, 3, 7 and 9 of
[../docs/PLAN.md](../docs/PLAN.md).

What lives here, or will:

- `protocol/` — the JSON Schema of the control-channel messages, exported from
  the Rust types in `core/crates/cabinetos-protocol` (Phase 1 onward), and of
  the indexer's read-only pipe (`indexer-request`, `indexer-response`,
  Phase 6). The C# side is validated against it.
- `config/` — the JSON Schema of `cabinetos.json`, exported from the Rust
  types in `core/crates/cabinetos-config` (Phase 3). The core writes a copy
  next to the configuration file for editors ([../docs/config.md](../docs/config.md)).
- `wit/` — the WebAssembly Component Model interface that Core Plugins
  implement: the package `cabinetos:plugin@0.2.0` and its world
  `core-plugin` (Phase 7). The capabilities a plugin may ask for, the
  sandbox and the limits are in [../docs/plugins.md](../docs/plugins.md).
- `templates/` — starter projects (Phase 7): Rust Core Plugins built with
  `wit-bindgen`, `hello` as the template and five test fixtures, and
  `build-fixtures.ps1` ([templates/README.md](templates/README.md)).
- `tools/` — Tool Extensions (Phase 5c): the JSON Schema of `tool.json`,
  `markdown-preview`, the first tool and the example to start a new
  one from, and `agent-chat`, the chat with the Agent ([tools/README.md](tools/README.md),
  [../docs/tool-extensions.md](../docs/tool-extensions.md)).
- `extensions/` — extensions that are a Core Plugin and a Tool Extension
  together, built to be installed from the marketplace: the Agent
  (`agent/plugin`, and its chat page in `tools/agent-chat`), the build script
  `build-extensions.ps1` and `extension.json`, which names the tool and the
  texts of the two marketplace items
  ([extensions/README.md](extensions/README.md),
  [../docs/extensions/agent.md](../docs/extensions/agent.md)).
- `fixtures/plugins/` — those plugins built as components, with their
  `plugin.json`. They are committed, so the core's tests and CI need no
  WebAssembly toolchain.
- `fixtures/edge-fixture.ps1` — makes the edge-case folder that the core's
  and the shell's live checks share: names beyond ASCII, a path over 300
  characters, a junction, symbolic links, and a junction that loops back
  to its own folder (`-Root <folder>`; Windows PowerShell 5.1 or
  PowerShell 7, no administrator rights).
- `themes/` — the JSON theme format (Phase 9): `theme.schema.json`,
  exported from the Rust types in `core/crates/cabinetos-protocol`, and the
  five themes that ship with the core (`default`, `commander-compact`,
  `nord`, `catppuccin-mocha`, `rose-pine-moon`), which the core embeds and
  writes into the themes folder when they are missing
  ([../docs/themes.md](../docs/themes.md)). `themes/collection/` holds 36
  more themes for the marketplace, which the core does not embed.
- `marketplace/` — the marketplace index format (Phase 9):
  `index.schema.json`, exported from the Rust types in
  `core/crates/cabinetos-protocol`, and `build-index.ps1`, which builds a
  local index from `fixtures/plugins` and `themes/` with their SHA-256
  hashes, for development and tests
  ([../docs/marketplace.md](../docs/marketplace.md)).

Extension architecture: Constitution Article 11 and brief §6.
