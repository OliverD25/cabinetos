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
- A tool may also have a page in the sidebar of the rail layout
  ("The sidebar page" below).

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
| `quickView` | Optional: the tool is a Quick View viewer ("Quick View" below). |
| `sidebar` | Optional, `false` when it is left out. `true`: the tool also has a page in the sidebar, with a button of its own in the activity rail of the rail layout. Give `accepts` an empty list when the tool opens no file. |

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
  `view.toggleTerminal`, so Ctrl+Shift+P and Ctrl+` work from a tool too,
  and, since 2026-10-01, the tab keys: Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W,
  Ctrl+T and Ctrl+1 to 9 (`tab.next`, `tab.previous`, `tab.close`,
  `tab.new`, `tab.select`, under the user's own keys). A tab key from a page
  in a pane's tab changes that pane's tabs, and the keyboard goes to the
  tab that comes to the front. Every other key stays with the page, Tab and
  Esc included ([keybindings.md](keybindings.md), "Tool pages and the
  terminal"). The page's own script needs nothing for this: it passes on the
  combinations the window sends it (`passKeys`).

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
| Any command of a plugin the page follows | A plugin's own page (the agent's chat page) runs the plugin's commands ("Plugin events and dropped rows" below) |

The last row is a rule, not a list, so no plugin's name is in the window
(Constitution Article 10). A page may run a command when three things hold:
the page follows the plugin (`subscribe`, below), the command is listed with
that plugin as its source, and its id starts with `<plugin id>.`. A page that
follows a name like `file` gets none of the core's commands by it: their
source is the core, not that plugin. The page loses the right when it
unfollows, or loads again (it must follow again after each `ready`).

**Plugin events and dropped rows** (Phase 14). A page may follow a plugin:

```json
{"type":"subscribe","plugin":"agent"}
{"type":"unsubscribe","plugin":"agent"}
```

The window then sends each event of that plugin, and nothing else:

```json
{"type":"plugin-event","plugin":"agent","name":"agent.notice","payload":{"notice":"Renamed 3 files"}}
```

`payload` is what the plugin wrote: JSON when it is JSON (an object, a
list, a number), else a string. A page follows at most 16 plugins, and
asks again each time it says `ready`, since a page that loads again has
forgotten. The window forwards only once the page is ready. When the user
drags rows from a pane over the page and drops them, the page gets:

```json
{"type":"paths-dropped","paths":["C:\\photos\\a.jpg","C:\\photos\\b.jpg"]}
```

(at most 1,000 paths; `"truncated": true` when there were more). A drop
reaches the window, not the page's browser, so a page needs no drag and
drop code of its own.

Anything else the page sends (other types, malformed JSON, a message over
64 KiB) is dropped.

## The editor tab

A tool that opens a file covers the pane's list with its editor tab
(design view D, 36 px): a 2 px accent edge, the file's glyph and name,
close (back to the folder), the tool's name beside a green dot, and "Open
in Terminal" (a shell in the file's folder). One tool is open per pane; a
tool that is open shows the next file where it is.

Since Phase 12 the open file is also a tab in the pane's tab row
([ui.md](ui.md), "Tabs"): the folder's own tab stays beside it, and
Ctrl+Tab or a click goes back to the folder while the tool keeps its file.
Closing the tool's tab (Ctrl+W, or the close button) is what
`editor.close` did. The tab cannot move to the other pane, and it is not
saved in `ui.tabs`.

## The sidebar page

A tool with `"sidebar": true` also has a page in the sidebar of the rail
layout (`ui.layout: rail`; [ui.md](ui.md), "The activity rail and the
sidebar"). Its button is in the activity rail after the terminal's, with
the first letters of the tool's name on it and the name as its tooltip;
the user can move it (Shift+Up and Shift+Down on the button; the order is
`ui.rail`). Pressing the button shows the page; pressing it again while it
shows closes the sidebar. In the classic and right layouts the tool has no
sidebar page and works as before.

- **The same `entry` page**, in a WebView2 of its own (its own browser
  process, as every tool page has), started when the button is first
  pressed. It gets `ready` and `context` like any tool page, and no
  `open`: there is no file. The page has the sidebar's width and height.
- **Messages are the same**: `command`, `subscribe`, `unsubscribe`, keys
  passed back to the window. A page cannot tell that it is in the sidebar,
  except that it never gets a file.
- **Keys.** A page in the sidebar also hands back the keys that change what
  the sidebar shows: `view.showExplorer` (Ctrl+Shift+E), `view.showSearch`
  (Ctrl+Shift+F) and `view.toggleSidebar`, under whatever keys the user gave
  them, beside the ways out (`palette.show`, `view.toggleTerminal`) and the
  tab keys of every tool page. Without them the mouse would be the only way
  out of the page (Constitution Article 7). A tab key from the sidebar's
  page changes the active pane's tabs. When the sidebar closes while the page has the
  keyboard, the keyboard goes to the pane.
- **A badge.** A plugin marks the button with the event `badge` and the
  payload `{ "view": "<tool id>", "kind": "dot" | "spinner" | null }`
  (`null` takes it away). The tool's own page cannot set it.
- **Hidden pages.** The page keeps running while another view shows. The
  page hidden last stays awake; the ones hidden before it are suspended
  (`CoreWebView2.TrySuspendAsync`) and wake up when their button is
  pressed again, so a tool that no one looks at costs no processor time.
- **When it stops** (the page's process ends), the sidebar says so, and
  pressing its button loads the page again.
- **A tool id that is a built-in view's ID** (`explorer`, `search`,
  `marketplace`, `terminal`) gets no button: those IDs belong to the window.

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

The second tool, [sdk/tools/agent-chat](../sdk/tools/agent-chat/README.md),
is the chat with the Agent plugin ([extensions/agent.md](extensions/agent.md)).
It opens no file (`accepts` is empty), has a page in the sidebar, and is the
example of a page that follows a plugin: it sends `subscribe` and runs the
plugin's commands, and the plugin answers with events. It needs the window to
let a page run the agent's commands: `agent.chat`, `agent.tier`, `agent.undo`,
`agent.audit` and the four `agent.rule.*` ones.

`editor.openMarkdownPreview` is also in the palette ("Editor: Open
Markdown Preview", Ctrl+K V in a pane): it opens the focused Markdown file
in the preview, or says that no Markdown tool is installed.

## Quick View

Space on a file opens the floating Quick View panel
([ADR 0023](decisions/0023-quick-view-viewer-contract.md)). The panel shows
Windows' thumbnail at once; a viewer, which is a Tool Extension, then shows
the full view. CabinetOS ships no viewer (Constitution Article 10). A tool
becomes a viewer with the `quickView` key of its `tool.json`:

```json
{
  "id": "image-viewer",
  "name": "Image Viewer",
  "version": "1.0.0",
  "author": "CabinetOS",
  "description": "Shows images in Quick View.",
  "entry": "index.html",
  "accepts": [],
  "placement": "pane",
  "quickView": {
    "kinds": ["*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp", "*.heic"],
    "entry": "quickview.html"
  }
}
```

| Key | Rule |
|---|---|
| `quickView.kinds` | 1 to 512 patterns in the grammar of `accepts`: `*.ext` (an extension, which may have dots, such as `*.tar.gz`) or a whole name (`README`, `Dockerfile`), compared without case. No `*` alone, no `?` and no other wildcards; no MIME types. |
| `quickView.entry` | Optional: the page Quick View loads, with the rules of `entry`. Without it, the tool's `entry` is used. |

- `accepts` keeps its meaning (Enter opens such a file in the tool), so a
  viewer that opens nothing in a pane gives it an empty list.
- **The core reads the block**, not the window: it builds the Quick View
  table at start, after a tool install or uninstall, and after a change of
  `quickView.viewers`, and sends it to the window
  ([ipc.md](ipc.md), "Quick View"). So a viewer installed while the window
  runs works at once. A block that is wrong (a bad pattern, more than 512,
  an unknown key, a bad or missing page) leaves the tool out of Quick View
  with a warning in the core's log that names the file; its pane use is not
  touched.
- **Several viewers for one kind.** The user's choice
  (`quickView.viewers` in [config.md](config.md)) first; then the viewer
  installed first: the tools of the window's development folder
  (`--tools-dir`, which the window passes to the core as
  `--dev-tools-dir`), then marketplace installs by their time in
  `installed.json`, then tools copied by hand, by folder name. Installing a
  second viewer therefore never takes a kind from the first.
- The page's messages in the panel (`quickview-show` and the reports) are
  in ADR 0023, decisions 2 and 3. The test viewer
  [sdk/fixtures/tools/quickview-fixture](../sdk/fixtures/tools/quickview-fixture/README.md)
  does what a `.qvtest` file's first line says.
- The marketplace item of a viewer sets `minCoreVersion` to the first
  release with Quick View: an older window reads `tool.json` strictly and
  would leave a tool with a `quickView` key out.

### A viewer page in the panel

What the window does with a viewer's page ([ui.md](ui.md), "Quick View";
the messages' one description is
[sdk/tools/quickview-messages.schema.json](../sdk/tools/quickview-messages.schema.json)):

- **One file, one load.** For each file the window serves the file's
  folder read-only on a new host `f<n>.<id>.cabinetos.example` and loads
  the page's Quick View entry again (from the core's table, never from
  `tool.json`). The page posts `{"type":"ready"}` once its listener is set
  and gets one `quickview-show` (the token, the path, the `url` to read the
  file from, the name, the extension, the matching `claim`, the size and
  date, the thumbnail on screen as a data URL or `null`, the window's
  `theme`, and the `panel` area in CSS pixels with the screen's scale). It
  gets no `context` and no `open`. A newer file gives a new token and a new
  load; a report with an older token is dropped.
- **Reports.** `quickview-shown` after the first frame with content (with
  optional `details`, at most 80 characters, and `keys`);
  `quickview-failed` with `reason` (`unsupported`, `damaged`, `too-large`,
  `other`) and `message` (at most 200 characters); `quickview-keys` for
  keys wanted later; `quickview-render` (`width`, `height`, at most 2560)
  for Windows' drawing of a file the browser cannot decode, answered with
  `quickview-rendered` (a `url` on a host `r<n>.<id>.cabinetos.example`) or
  `quickview-render-failed`, one at a time; `quickview-system-preview` is
  answered with `quickview-system-preview-failed` in this version. A
  message over a limit, or malformed, is dropped.
- **Keys.** The page never has the keyboard. It may ask for Left, Right,
  PageUp, PageDown, Home, End, the letters, the top row's digits, `plus`,
  `minus`, `comma` and `period`, alone or with Shift; the window answers
  `quickview-keys-granted` with those no binding wants, and sends each press
  as `quickview-key` (`key`, `repeat`). `plus` is the key the core's
  grammar calls `equal`. A video viewer pauses with K and seeks with J and
  L; Space always closes the panel.
- **Limits.** No `ready` within 3 s, no report within 30 s, and three
  crashes within a minute are the panel's to handle; the page cannot hold
  up a key. `command`, `subscribe` and `unsubscribe` from a page in the
  panel are refused and logged with the trigger `quickview:<id>`. Media
  plays without a click in every tool environment
  (`--autoplay-policy=no-user-gesture-required`).
- **Look.** `theme` has `appearance` (`dark` or `light`, which
  `prefers-color-scheme` also follows), `background`, `text`,
  `textSecondary`, `accent` (each `#RRGGBB`) and `font`; a
  `quickview-theme` with the same object comes when the theme changes.
- **Its own folder only.** The page loads everything from its folder, with
  a Content-Security-Policy that allows `img-src` and `media-src` from
  `https://*.cabinetos.example` and `data:`, as the fixture viewer does.

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
- A tool that opens without a file from a command of its own (its
  sidebar page, with `"sidebar": true`, has no file and is the one
  exception).
- Capabilities for tools, like plugins': a tool cannot change files, so
  none are needed yet.
- Reading the tools folders again while the window runs.
- The marketplace, which will install tools (Phase 9).
