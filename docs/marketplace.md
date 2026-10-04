# The marketplace

The marketplace is how users find, install and remove extensions: Core
Plugins and Tool Extensions, and colour themes. Constitution Article 8
("users can write, install, and share extensions via a centralized
marketplace") and Article 2 (the marketplace infrastructure stays free and
open source): a catalogue is a static JSON file and a folder of downloads,
which any web server, or a folder on disk, can serve.

There are two catalogues, one file each, in one format
([ADR 0022](decisions/0022-two-catalogues-extensions-and-themes.md)):
`index.json` lists the extensions (kinds `plugin` and `tool`) and
`themes.json` lists the themes (kind `theme`). The window shows them on two
pages, the Extensions page and the theme gallery ([ui.md](ui.md), "The
marketplace" and "The theme gallery"), because a user who wants a colour
scheme does not want to look for it among WebAssembly plugins.

The client is the crate `core/crates/cabinetos-market`; the core serves it
to the UI (`core/crates/cabinetos-core/src/market.rs`). The format of both
files is [sdk/marketplace/index.schema.json](../sdk/marketplace/index.schema.json).

## At a glance

- `marketplace.index` in `cabinetos.json` says where the extensions' index
  is, and `marketplace.themes` where the themes catalogue is: each an
  `https:` URL, a `file:` URL, or the path of the file or of its folder. A
  plain `http:` address is refused unless `marketplace.allowInsecure` is
  `true` (for testing only).
- The core reads a catalogue only when a client asks (`marketplace_refresh`,
  or a search or an install before it was read). There is no background
  refresh. The two are read apart: a themes server that is down does not
  hide the extensions, and the other way round.
- While the site has no `themes.json` yet, the theme items of `index.json`
  stand in for it ("The two catalogues", below).
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
cache of a web catalogue (`index.json` and `index.meta.json` with its
`ETag`, and the same two for the themes, `themes.json` and
`themes.meta.json`), and `downloads\` and `staging\` while an install
runs; an install removes its own download and staging folder when it ends,
whether it worked or not.

## The two catalogues

| | Extensions | Themes |
|---|---|---|
| File | `index.json` | `themes.json` |
| Setting | `marketplace.index` | `marketplace.themes` |
| Default | `https://oliverd25.github.io/cabinetos-marketplace/index.json` | `https://oliverd25.github.io/cabinetos-marketplace/themes.json` |
| Holds | kinds `plugin` and `tool` | kind `theme` |
| Shown on | the Extensions page | the theme gallery |
| Cache | `index.json`, `index.meta.json` | `themes.json`, `themes.meta.json` |

Both are read with the same rules: the same cache and `ETag`, the same trust
rules, the same `marketplace.allowInsecure` rule, and the same limits. An
item of the wrong kind in a file (a theme in `index.json`, a plugin in
`themes.json`) is not offered from that file.

**The transition rule.** The public site served only `index.json`, with the
themes inside it, until the creator publishes `themes.json`. So that the
window keeps working in both states:

- When the themes address answers **404** (or, for a folder on this machine,
  has no `themes.json`), the core offers the theme items of `index.json`
  as the themes, and its log says so in one line: `themes.json is not
  there; the theme items of index.json are used instead`. Their downloads
  are found next to `index.json`.
- When `themes.json` **exists**, it is the themes. The theme items of
  `index.json` are ignored, with one log line (`themes.json is there; the
  theme items of index.json are ignored`), however many there are.
- Any other failure of the themes address (no network, a damaged file, a
  500) is an error for the themes catalogue only: the gallery shows the
  themes already installed and a one-line notice. It is not a reason to use
  `index.json`: only a 404 means "not published yet".

Once a release with the two files is published, the transition rule can go
in a later version.

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
| `items[].kind` | `plugin`, `theme` or `tool`. A plugin or a tool belongs in `index.json`, a theme in `themes.json`. |
| `items[].appearance` | Themes only, optional: `dark`, `light` or `system`, the theme file's `kind`. The gallery filters on it. |
| `items[].density` | Themes only, optional: `true` when the theme sets metrics, which makes it a density preset such as Commander Compact. |
| `items[].tile` | Themes only, optional: `{ "background", "text", "accent" }`, each `#RRGGBB`: the colours the gallery paints the theme's tile with ([themes.md](themes.md), "The gallery's tile"). A theme without it gets a plain tile. |
| `items[].name`, `description`, `long` | What the card and the detail view show. `long` may be left out. |
| `items[].author` | `name`, `verified` (default `false`) and `url` (optional). |
| `items[].version` | `major.minor.patch`. An index may list several versions of one ID. |
| `items[].rating`, `installs` | Optional: `{ "average": 0 to 5, "count" }` and a count. |
| `items[].size` | The download's size in bytes, more than 0. A download that grows beyond it is stopped and deleted. |
| `items[].download` | `url`: an absolute URL, or a path relative to the index; `sha256`: 64 hex digits. |
| `items[].manifest` | The extension's own manifest: the plugin's `plugin.json`, the tool's `tool.json`, or the theme's header (`id`, `name`, `author`, `version`, `kind`, `accent`). |
| `items[].capabilities` | For a plugin: the capabilities its `plugin.json` asks for (`name`, `reason`, `roots`, for `net` its `hosts` and `secrets`, and for `core:request` its `requests`). The core adds each one's `level` when it sends the item to a client. |
| `items[].minCoreVersion` | The oldest CabinetOS it runs on, `major.minor.patch`. |
| `items[].license` | Its license, for example `MIT`. |
| `items[].installedVersion` | Left out of an index. The core fills it in when it sends the item to a client: the version installed from the marketplace, if one is. |

An item that does not follow the format (an unknown `kind` or `appearance`
from a newer index, a missing key, a bad ID, version, hash or tile colour, a
second copy of one ID and version) is left out, and the core's log says why;
the other items stay.
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
   reaches the network only for `marketplace_refresh`, `marketplace_search`,
   `preview_theme`, `install_extension` and, since Quick View
   ([ADR 0023](decisions/0023-quick-view-viewer-contract.md), decision
   5.1), `quick_view_offer`. The last one is asked when the user presses
   Space on a file that no installed viewer claims, and the offer of a
   viewer cannot be made without the catalogue. It uses the catalogue read
   in the session, or the cached `index.json` while it is under seven days
   old; only an older or missing copy is read from the web, with its
   `ETag`, and at most once per core session. When that read fails, the
   panel shows no offer.
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

One install or uninstall runs at a time, also between two cores that share
the marketplace folder (two windows). Both read and write
`installed.json`, and without a lock the later write dropped the earlier
install from the record, so uninstalling it would not find it (a test with
two real cores installing two themes at the same moment showed it). Each
install and uninstall holds `.installed.json.lock` in the marketplace
folder (`LockFileEx`) from its first read of the record to its last write;
Windows releases it if a core ends.

## The public catalogues

`marketplace.index` defaults to the public index,
`https://oliverd25.github.io/cabinetos-marketplace/index.json`
([ADR 0012](decisions/0012-marketplace-index-on-github-pages.md)), and
`marketplace.themes` to `https://oliverd25.github.io/cabinetos-marketplace/themes.json`.
They are GitHub Pages of the separate public repository
[cabinetos-marketplace](https://github.com/OliverD25/cabinetos-marketplace),
which holds `index.json`, `themes.json` and the `files\` folder beside
them. Both files are built in this repository and committed in that one, by
one script:

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <cabinetos-marketplace checkout> -Collection -ThemesOnly -Viewers
```

`-ThemesOnly` leaves the fixture plugins out: they are test material, not
extensions for the public. `-Viewers` adds the two Quick View viewers of
`sdk/tools`, the Image Viewer and the Media Viewer, as tool items (see "A
local index"). So the public `index.json` has those two items and no plugin
yet (no real plugin exists), and `themes.json` offers 59 themes: the seven
shipped themes and the 52 of the collection (41 before the collection's
second round of 2026-10-03; the first index, 2026-09-29, offered 41 in
`index.json`). The viewers ask for CabinetOS 0.1.3, the first release with
Quick View, so older versions are offered none of them (a client is offered
only what it can run).
**Publishing this changes what older
versions see**: CabinetOS 0.1.0 and 0.1.1 read `index.json` only, so once
the new `index.json` replaces the old one they list no theme in their
marketplace until they update. The 59 themes stay installable by name from
the new versions, and files already installed are not touched.

A configuration file written before 2026-09-30 may still name the old
placeholder, `https://marketplace.cabinetos.invalid/index.json`, which can
never resolve. The window then says the line is stale; remove it and the
public index is used.

## A local index

`sdk/marketplace/build-index.ps1` builds both catalogues from the fixture
plugins in `sdk/fixtures/plugins` (each as a zip) and the shipped themes in
`sdk/themes`, with their hashes, into a folder of your choice. It checks
every item against the format before it writes, and stops with a message
that names the item when one is wrong:

```text
powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder> [-Collection] [-Extensions] [-Viewers]
cabinetos-cli config set marketplace.index "<folder>"
cabinetos-cli config set marketplace.themes "<folder>"
cabinetos-cli market refresh
cabinetos-cli market refresh --themes
cabinetos-cli market install hello
cabinetos-cli plugins list
cabinetos-cli market uninstall hello
```

Set both settings: `marketplace.themes` is the public address until you
change it, so a local `marketplace.index` alone leaves the themes coming
from the public site.

The shipped themes are in `themes.json` too, but installing one is refused
while its file is in the themes folder (trust rule 7). With `-Collection`
the catalogue also offers the 52 themes of `sdk/themes/collection`
([themes.md](themes.md), "The collection").

With `-Extensions` the index also offers the extensions of `sdk/extensions`:
each folder there with an `extension.json` gives its Core Plugin (a zip of
`plugin.json` and `plugin.wasm`, built first with
`sdk\extensions\build-extensions.ps1`) and, when it names one, its Tool
Extension (a zip of the tool's folder), as two items whose long descriptions
name each other. The first is the Agent, items `agent` and `agent-chat`
([extensions/agent.md](extensions/agent.md)). An extension's plugin is also a
fixture for the core's tests; the index offers it as the extension's item
only, so without `-Extensions` it is not offered at all. `-Extensions` and
`-ThemesOnly` are independent. Nothing is uploaded: the public index gets an
extension only when the creator publishes it.

With `-Viewers` the index also offers the Quick View viewers: every folder of
`sdk/tools` whose `tool.json` has a `quickView` block (ADR 0023) is packed as a
Tool Extension item, a zip of the folder with its SHA-256, whose `manifest`
is its `tool.json`. The panel's install offer finds a viewer for a file by the
`quickView.kinds` inside that manifest (trust rule 6, `quick_view_offer`), so
the viewer must be in the index as a tool item and nothing else is needed.
Today these are [image-viewer](../sdk/tools/image-viewer/README.md) (18 kinds:
the pictures the browser decodes, and HEIC, TIFF, JPEG XR and camera RAW
through Windows' image stack) and [media-viewer](../sdk/tools/media-viewer/README.md)
(13 kinds of video and sound). Each item sets `minCoreVersion` to `0.1.3`, the
first release with Quick View: an older window reads `tool.json` strictly and
would leave a tool with a `quickView` key out. The script checks each viewer's
kinds (1 to 512 patterns of the claim grammar), its Quick View page and its
folder name, and stops with a message when one is wrong. `-Viewers` is
independent of the other switches. The index is rebuilt at each release; the
public one gets the viewers only when the creator publishes it.

## The messages

`marketplace_refresh` and `marketplace_search` (each with an optional
`catalogue`, `extensions` or `themes`), `install_extension`,
`uninstall_extension`, `list_tools`, and the events `install_progress`,
`install_finished` and `tools_changed`: [ipc.md](ipc.md), "The
marketplace". `quick_view_offer`, which finds the Tool Extension whose
`quickView.kinds` claim a file's name: [ipc.md](ipc.md), "Quick View".

## The command line

```text
cabinetos-cli market refresh [--themes]
cabinetos-cli market search <query> [--kind plugin|theme|tool] [--themes]
cabinetos-cli market install <id> [--version <version>]
cabinetos-cli market uninstall <id>
cabinetos-cli market tools
```

`market refresh` and `market search` list the extensions; with `--themes`
they list the themes catalogue instead, and a theme's line says its
appearance (`dark`, `light` or `system`) and `density preset` when it is
one. `market search --kind theme` asks for the themes without `--themes`.
`market install` finds the ID in either catalogue.

`market install` shows the download's progress and then what the core did,
with the version installed now from `install_finished`
(`installed version: 0.1.0`); when an update fails, it says which version
stays installed.

## Not yet

- Publisher identities and signatures; until then `verified` is only shown.
- The marketplace view is UI work. The window reads the tools folders at
  its start, so a tool installed while it runs shows at its next start;
  `tools_changed` is there for when it follows the list live.
- The core checks only part of `tool.json`; a tool that installs but that
  the window cannot load is left out by the window, with a warning.
- An update is not atomic (see above), and there is no automatic update
  check.
