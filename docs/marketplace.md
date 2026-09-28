# The marketplace

The marketplace is how users find, install and remove extensions: Core
Plugins, colour themes and Tool Extensions. Constitution Article 8
("users can write, install, and share extensions via a centralized
marketplace") and Article 2 (the marketplace infrastructure stays free and
open source): an index is a static JSON file and a folder of downloads,
which any web server, or a folder on disk, can serve.

The client is the crate `core/crates/cabinetos-market`; the core serves it
to the UI (`core/crates/cabinetos-core/src/market.rs`). The index format is
[sdk/marketplace/index.schema.json](../sdk/marketplace/index.schema.json).

## At a glance

- `marketplace.index` in `cabinetos.json` says where the index is: an
  `https:` URL, a `file:` URL, or the path of an `index.json` or of its
  folder. A plain `http:` index is refused unless `marketplace.allowInsecure`
  is `true` (for testing only).
- The core reads the index only when a client asks (`marketplace_refresh`,
  or a search or an install before any index was read). There is no
  background refresh.
- An install downloads the item into a temporary file, checks its SHA-256
  against the index, unpacks it into a staging folder, checks it, and only
  then puts it in place. Nothing is run while installing.
- An installed plugin arrives as `needs_review`: it runs only after the user
  grants what it asks for ([plugins.md](plugins.md)).
- An uninstall removes exactly the files the install put in place, never
  the plugin's own data folder.

## Folders

| What | Default | Command-line flag of `cabinetos-core` | Environment variable |
|---|---|---|---|
| Plugins | `%LOCALAPPDATA%\CabinetOS\plugins\<id>\` | `--plugins-dir <path>` | `CABINETOS_PLUGINS_DIR` |
| Themes | `%LOCALAPPDATA%\CabinetOS\themes\<id>.json` | `--themes-dir <path>` | `CABINETOS_THEMES_DIR` |
| Tool Extensions | `%LOCALAPPDATA%\CabinetOS\tools\<id>\` | `--tools-dir <path>` | none (see below) |
| The marketplace's own files | `%LOCALAPPDATA%\CabinetOS\marketplace\` | `--marketplace-dir <path>` | `CABINETOS_MARKETPLACE_DIR` |

Tools go where the window reads installed tools. The core reads no
`CABINETOS_TOOLS_DIR`: in the window that variable names a folder of tools
in development, read first ([tool-extensions.md](tool-extensions.md)), and
the core, which the window starts, inherits the window's environment.

The marketplace folder holds `installed.json` (the record of installs:
each extension's kind, version, SHA-256, index, time and exact files), the
cache of a web index (`index.json` and `index.meta.json` with its `ETag`),
and `downloads\` and `staging\` while an install runs; an install removes
its own download and staging folder when it ends, whether it worked or not.

## The index

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-09-28T02:00:00Z",
  "items": [
    {
      "id": "hello",
      "kind": "plugin",
      "name": "Hello",
      "author": { "name": "CabinetOS", "verified": false, "url": "https://example.org" },
      "version": "0.1.0",
      "description": "The sample Core Plugin.",
      "long": "The sample Core Plugin: a Say Hello command that answers, writes a log line and sends an event.",
      "rating": { "average": 4.8, "count": 120 },
      "installs": 5400,
      "size": 27003,
      "download": { "url": "files/hello-0.1.0.zip", "sha256": "8818…cac3" },
      "manifest": { "id": "hello", "name": "Hello", "version": "0.1.0", … },
      "capabilities": [
        { "name": "cmd:register", "reason": "Adds the Say Hello command to the palette." },
        { "name": "events:emit", "reason": "Tells the window each time it said hello." }
      ],
      "minCoreVersion": "0.1.0",
      "license": "MIT"
    }
  ]
}
```

| Key | Value |
|---|---|
| `schemaVersion` | `1`. An index with another version is refused whole. |
| `generatedAt` | When the index was built, ISO 8601 in UTC. |
| `items[].id` | 1 to 64 lower case letters, digits and `-`, starting with a letter; not a name Windows keeps for a device (`con`, `nul`, `com1`, …). It becomes a file or folder name. |
| `items[].kind` | `plugin`, `theme` or `tool`. |
| `items[].name`, `description`, `long` | What the card and the detail view show. `long` may be left out. |
| `items[].author` | `name`, `verified` (default `false`) and `url` (optional). |
| `items[].version` | `major.minor.patch`. An index may list several versions of one ID. |
| `items[].rating`, `installs` | Optional: `{ "average": 0 to 5, "count" }` and a count. |
| `items[].size` | The download's size in bytes, more than 0. A download that grows beyond it is stopped and deleted. |
| `items[].download` | `url`: an absolute URL, or a path relative to the index; `sha256`: 64 hex digits. |
| `items[].manifest` | The extension's own manifest: the plugin's `plugin.json`, the tool's `tool.json`, or the theme's header (`id`, `name`, `author`, `version`, `kind`, `accent`). |
| `items[].capabilities` | For a plugin: the capabilities its `plugin.json` asks for (`name`, `reason`, `roots`). The core adds each one's `level` when it sends the item to a client. |
| `items[].minCoreVersion` | The oldest CabinetOS it runs on, `major.minor.patch`. |
| `items[].license` | Its license, for example `MIT`. |
| `items[].installedVersion` | Left out of an index. The core fills it in when it sends the item to a client: the version installed from the marketplace, if one is. |

An item that does not follow the format (an unknown `kind` from a newer
index, a missing key, a bad ID, version or hash, a second copy of one ID and
version) is left out, and the core's log says why; the other items stay.
Keys the core does not know are ignored, so a newer index still works.

A client is offered one item per extension: the newest version this core
can run, in the order the index first lists each extension. An extension
none of whose versions this core can run is not offered.
`install_extension` still sees every version: it can install an older one
by `version`, and says `incompatible` for one this core cannot run. Each
offered item carries `installedVersion` when the marketplace installed
that extension; an extension that is there but was not installed from the
marketplace (a shipped theme) has none.

What a download is, by kind:

- **plugin**: a zip with `plugin.json` and `plugin.wasm` at its root (more
  files may come along), or the `plugin.wasm` alone, whose `plugin.json` is
  then the item's `manifest`. It lands in `plugins\<id>\`.
- **theme**: the theme's JSON file ([themes.md](themes.md)). It lands as
  `themes\<id>.json`.
- **tool**: a zip of the tool's folder, with `tool.json` at its root. It
  lands in `tools\<id>\`.

`tool.json` is the window's format ([tool-extensions.md](tool-extensions.md)).
The core checks `id` (the folder's name, at most 63 characters, since the
window makes it a host name), `name`, `version`, `author` and
`description`; the window checks the rest (`entry`, `accepts`,
`placement`) when it loads the tool, and leaves out one it cannot use.

## Where downloads come from

| The index is | A download may be |
|---|---|
| On this machine | a path relative to the index, an absolute path, a `file:` URL, or an `https:` URL |
| On the web | an `https:` URL, absolute or relative to the index's URL; never a local file |

Plain `http:`, for the index or a download, needs
`marketplace.allowInsecure`. A redirect from `https:` to `http:` is refused
too. Certificates are checked against the Windows certificate store. A web
index is kept in the cache with its `ETag`, so the next refresh of an
unchanged index is one `304 Not Modified`. The cache files are replaced
through a temporary file and a rename, and a cached copy that cannot be
read anyway (a disk error) is fetched again whole, without the tag.

## Trust rules

1. **Nothing runs before the review.** An installed plugin arrives as
   `needs_review`, however it was installed before: the core clears the
   plugin's earlier grants (`plugins.<id>.granted`) before the new files
   are in place, so an update waits for the review too. A plugin that asks
   for no capability has nothing to review and starts at once; with no
   capability it can only compute within its limits.
2. **The hash decides.** The download's SHA-256 must be the one the index
   gives. A mismatch deletes the download and installs nothing
   (`hash_mismatch`).
3. **The index must tell the truth.** A plugin whose `plugin.json` asks for
   other capabilities than the index lists, or has another version, is not
   installed. So is a theme or a tool whose own file names another ID or
   version.
4. **`minCoreVersion` is checked.** Without a version, the newest version
   this core can run is installed; a version that needs a newer CabinetOS
   is refused (`incompatible`).
5. **No code runs while installing.** The core only copies, hashes, unpacks
   and reads manifests. A zip entry that points outside its folder, a link,
   more than 1,000 entries or more than 256 MiB unpacked stop the install.
6. **The core fetches only when asked.** No background refresh; the core
   reaches the network only for `marketplace_refresh`, `marketplace_search`
   and `install_extension`.
7. **The marketplace replaces only what it installed.** An extension that
   is there already, and was not installed from the marketplace (a shipped
   theme, a plugin copied by hand), is left alone (`already_exists`).
8. **`verified` is shown, not checked.** Publisher identities, and the
   review dialog's "Trust {author} for future updates", come in a later
   phase.

## Install, update, uninstall

- `install_extension` of an ID that the marketplace installed before is an
  update: the new files replace the old ones, and old files the new
  version does not have are removed. An update that fails half-way can
  leave the extension broken; install it again.
- An install that fails leaves no files behind. For a plugin, the grants
  were already cleared, so an old version stays but waits for review.
- `uninstall_extension` removes exactly the recorded files, then the
  folders they leave empty. A file the user added to the folder stays, and
  so does the folder. A plugin is stopped and forgotten first; its own data
  folder (`plugins-data\<id>\`) and its entry in `cabinetos.json` stay. The
  theme in effect cannot be uninstalled; choose another theme first.

After an install: a plugin is loaded (and waits for review); a theme that
`ui.theme` names applies at once (`theme_changed`); a tool install or
uninstall sends `tools_changed`.

## A local index

`sdk/marketplace/build-index.ps1` builds an index from the fixture plugins
in `sdk/fixtures/plugins` (each as a zip) and the shipped themes in
`sdk/themes`, with their hashes, into a folder of your choice:

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder>
cabinetos-cli config set marketplace.index "<folder>"
cabinetos-cli market refresh
cabinetos-cli market install hello
cabinetos-cli plugins list
cabinetos-cli market uninstall hello
```

The shipped themes are in the index too, but installing one is refused
while its file is in the themes folder (trust rule 7).

## The messages

`marketplace_refresh`, `marketplace_search`, `install_extension`,
`uninstall_extension`, `list_tools`, and the events `install_progress`,
`install_finished` and `tools_changed`: [ipc.md](ipc.md), "The
marketplace".

## The command line

```text
cabinetos-cli market refresh
cabinetos-cli market search <query> [--kind plugin|theme|tool]
cabinetos-cli market install <id> [--version <version>]
cabinetos-cli market uninstall <id>
cabinetos-cli market tools
```

`market install` shows the download's progress and then what the core did.

## Not yet

- There is no public index yet: `marketplace.index` defaults to a
  placeholder, `https://marketplace.cabinetos.invalid/index.json`, which can
  never resolve. Where the real one will live is decided
  ([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)): GitHub
  Pages of a separate public repository, at
  `https://oliverd25.github.io/cabinetos-marketplace/index.json`. Once the
  creator has created it, the default changes here, in the config schema
  and in the shell's empty state.
- Publisher identities and signatures; until then `verified` is only shown.
- The marketplace view is UI work. The window reads the tools folders at
  its start, so a tool installed while it runs shows at its next start;
  `tools_changed` is there for when it follows the list live.
- The core checks only part of `tool.json`; a tool that installs but that
  the window cannot load is left out by the window, with a warning.
- An update is not atomic (see above), and there is no automatic update
  check.
