# sdk/extensions: extensions with a plugin and a page

An extension here is a Core Plugin (no UI, in the core's sandbox) and, when it
wants a page in the window, a Tool Extension, installed from the marketplace
as two items that name each other (Constitution Articles 10 and 11). None is
part of CabinetOS itself.

| Folder | What |
|---|---|
| [agent/](agent/plugin) | The Agent: the plugin `agent` (`agent/plugin`), and `agent/extension.json`, which names its chat page `sdk/tools/agent-chat` and the long texts of the two marketplace items. Guide: [../../docs/extensions/agent.md](../../docs/extensions/agent.md). |

## Layout of one extension

```text
sdk/extensions/<name>/
  extension.json      "id", "plugin" (the folder), "tool" (the tool's folder, optional), "long" (plugin, tool)
  plugin/             Cargo.toml, plugin.json, src/, and plugin.wasm once built (git ignores it)
```

## Build and pack

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\extensions\build-extensions.ps1 [-TargetDir <short folder>]
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder> -Extensions
```

`build-extensions.ps1` compiles every plugin here to `wasm32-wasip2`, puts
`plugin.wasm` beside `plugin.json`, and refreshes the committed test copy in
`sdk/fixtures/plugins/<id>`, which the core's tests load. Commit that copy
when the plugin's code changes. `build-index.ps1 -Extensions` packs the
plugin (a zip of `plugin.json` and `plugin.wasm`) and the tool (a zip of its
folder) into a local index ([../../docs/marketplace.md](../../docs/marketplace.md)).
Nothing is uploaded.

The plugin's own tests run on this machine: `cargo test` in
`agent/plugin`.
