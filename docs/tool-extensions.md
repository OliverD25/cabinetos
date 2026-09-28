# Tool Extensions

A Tool Extension is a web page the window shows beside the files: a
Markdown preview, and later viewers and small tools. It is the visual layer
of Constitution Article 11 (Bifurcated Extension Architecture); the other
layer, Core Plugins, has no UI and runs in the core ([plugins.md](plugins.md)).
Every tool is opt-in: CabinetOS ships none (Article 10, The Zero-Bloat
Foundation). A tool runs in WebView2, in a browser process of its own, with
no network, and it can change no files.

The window's side is in `ui/CabinetOS` (`ToolHost`, `EditorPane`) and
`ui/CabinetOS.Core/Tools` (the manifest, the catalog, the messages, all
tested). The first tool is [sdk/tools/markdown-preview](../sdk/tools/markdown-preview/README.md).

## At a glance

- A tool is a folder `<tools folder>\<id>\` with `tool.json` and the page
  it names (`index.html`), plus whatever the page loads from its folder.
- Enter on a file the tool accepts opens it in the tool: in the other pane
  when two are shown, else in the same pane, as the pane's editor tab
  (design view D).
- The page gets the file's path and a URL to read it from; it asks the
  window for things with commands.
- When the page's process ends, the pane says "The tool stopped" with
  Reload, and the rest of the window goes on.

## Where tools live

| Folder | When |
|---|---|
| `--tools-dir <folder>` of `CabinetOS.exe`, or the variable `CABINETOS_TOOLS_DIR` | Writing a tool: read first, so it wins over an installed copy with the same ID |
| `%LOCALAPPDATA%\CabinetOS\tools\` | Installed tools |

Installing a tool means copying its folder there; the window reads the
tools folders once, at start. A folder whose `tool.json` cannot be used is
left out, with a warning in the UI's log (`cabinetos_ui::tools`) that
names the file and the problem.

## The manifest: `tool.json`

```json
{
  "id": "markdown-preview",
  "name": "Markdown Preview",
  "version": "0.1.0",
  "author": "CabinetOS",
  "description": "Shows a Markdown file as formatted text, with its images, in a pane.",
  "entry": "index.html",
  "accepts": ["*.md", "*.markdown"],
  "placement": "pane"
}
```

| Key | Rule |
|---|---|
| `id` | 1 to 63 lower case letters, digits and `-`, starting with a letter. It must be the folder's name; it becomes the page's host name. |
| `name` | Not empty. The editor tab shows it beside a green dot. |
| `version` | `major.minor.patch`, numbers only. |
| `author`, `description` | Not empty. |
| `entry` | The page the tool starts from: an `.html` file inside the folder, without `..` or a drive. |
| `accepts` | The file names it opens, without case: `*.ext` or a whole name such as `README`. May be empty. |
| `placement` | `pane`: the pane's editor tab. `dock`: the Tool Dock; this version opens a dock tool in a pane too, until the dock has tabs for tools. |

The window reads the file strictly, like `plugin.json`: an unknown key, a
missing key or a bad value leaves the tool out. The JSON Schema is
[sdk/tools/tool.schema.json](../sdk/tools/tool.schema.json), for editors.

When several tools accept a name, the first one found opens it (the
`--tools-dir` folder first, then the installed ones in the order of their
folder names).

## The page

The window loads the entry page from `https://<id>.tool.cabinetos.example/`,
a virtual host that serves the tool's folder
(`SetVirtualHostNameToFolderMapping`; `.example` is a reserved name that
never reaches the network).

- **No network.** The page may load only from the window's virtual hosts.
  Every other request and navigation, `http` and `https` included, is
  refused and logged; so are new windows, downloads and every permission
  (camera, clipboard reading, notifications). A page should also carry a
  Content-Security-Policy of its own, as Markdown Preview does.
- **No developer tools** in Release builds. In Debug builds, the page's
  right-click menu has Inspect. F12 opens nothing in either: the
  browser's keys (F5, F12, Ctrl+F, Ctrl+P) go to the page.
- **One browser process per tool** (user-data folder
  `%LOCALAPPDATA%\CabinetOS\WebView2\tool-<id>`), so a tool that crashes
  takes neither the window nor another tool with it.
- **Dark or light.** The page's `prefers-color-scheme` follows the
  window's theme.
- **The window's keys.** A focused page gets every key. The window puts a
  small script into every document of a tool (before the page's own
  scripts) that hands it back the keys of `palette.show` and
  `view.toggleTerminal`, so Ctrl+Shift+P and Ctrl+` work from a tool too.
  Every other key stays with the page.

## Messages

Both ways, one JSON object per message, as a string:
`chrome.webview.postMessage(JSON.stringify(message))` in the page, and the
`message` event of `chrome.webview` for what the window sends.

The page speaks first:

```json
{"type":"ready"}
```

once its listener is set. The window then sends the context and the file.
A message sent to a page before it is ready would be lost, so the window
waits for this; a page that reloads sends it again.

**Window to page.**

```json
{"type":"context","selection":["C:\\docs\\a.md","C:\\docs\\b.md"],"activeFolder":"C:\\docs"}
{"type":"open","path":"C:\\docs\\README.md","url":"https://f1.markdown-preview.cabinetos.example/README.md"}
```

- `context` says what the user has in front of them: the active pane's
  selected paths (at most 1,000; `"truncated": true` when there were
  more) and its folder (`null` before one is shown). It comes after
  `ready`, and again, at most every 100 ms, when the active pane, its
  folder or its selection changes.
- `open` names the file to show and the URL to read it from. The file's
  folder is served read-only on a host of its own,
  `f<n>.<id>.cabinetos.example`, new for every file, so the page can
  `fetch` the file and load what sits beside it (images) with relative
  URLs, and cannot keep reading an earlier folder. The page cannot
  navigate to that host, only request from it. For every file after the first, the page loads again, because a page
  that is already loaded cannot fetch from a host mapped after it loaded;
  it says `ready` again and then gets the `open`.

**Page to window.**

```json
{"type":"command","id":"editor.openMarkdownPreview","args":{"path":"C:\\docs\\ui.md"}}
```

A command runs through the window's command router, as if the user chose
it, logged with the trigger `tool:<id>`. A tool may move around and open
things, never change files or grant rights; the window runs only these
commands for a tool and refuses the rest, with a line in the status bar
and in the log:

| Command | What a tool may use it for |
|---|---|
| `go.toPath` (`{"path": …}`), `go.back`, `go.forward`, `go.up` | Show a folder in the active pane |
| `view.toggleDualPane`, `view.toggleSidebar`, `view.toggleTerminal`, `view.focusOtherPane` | The window's layout |
| `palette.show`, `search.focus`, `help.about` | Hand over to the user |
| `terminal.new` (`{"cwd": …}`) | A shell in a folder |
| `editor.openMarkdownPreview` (`{"path": …}`) | Open another Markdown file (a link) |

Anything else the page sends (other types, malformed JSON, a message over
64 KiB) is dropped.

## The editor tab

A tool that opens a file covers the pane's list with its editor tab
(design view D, 36 px): a 2 px accent edge, the file's glyph and name,
close (back to the folder), the tool's name beside a green dot, and "Open
in Terminal" (a shell in the file's folder). One tool is open per pane; a
tool that is open shows the next file where it is.

## Markdown Preview

The first tool ([sdk/tools/markdown-preview](../sdk/tools/markdown-preview/README.md))
opens `*.md` and `*.markdown`. It renders with marked 18.0.14 (MIT, copied
into the tool), in the design's typography: 28/40 px padding, at most
680 px wide, 13 px text with line height 1.6, h1 26 px, h2 17 px over a
rule, code on rgba(0,0,0,.35). Relative images load from the file's
folder; a link to another Markdown file opens it in the preview (the page
asks with `editor.openMarkdownPreview`); a link to a heading scrolls there;
web links do not open. marked does not remove HTML inside Markdown; the
page's Content-Security-Policy keeps it from running any script.

`editor.openMarkdownPreview` is also in the palette ("Editor: Open
Markdown Preview", Ctrl+K V in a pane): it opens the focused Markdown file
in the preview, or says that no Markdown tool is installed.

## Writing a tool

1. Make a folder named after the tool's ID with `tool.json` and the page.
2. Start CabinetOS with `--tools-dir` set to the folder that holds it.
3. In the page: listen for `message` on `chrome.webview`, post
   `{"type":"ready"}`, and act on `open` and `context`.
4. Load scripts and styles from the tool's own folder (no CDN: there is no
   network), and give the page a Content-Security-Policy.
5. In a Debug build of CabinetOS, right-click the page and choose Inspect
   for the developer tools.

## Not yet

- Tabs for dock tools in the Tool Dock (a `dock` tool opens in a pane).
- A tool that opens without a file (from a command of its own).
- Capabilities for tools, like plugins': a tool cannot change files, so
  none are needed yet.
- Reading the tools folders again while the window runs.
- The marketplace, which will install tools (Phase 9).
