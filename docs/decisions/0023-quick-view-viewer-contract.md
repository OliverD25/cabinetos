# ADR 0023: Quick View is a panel of the window; every viewer is a Tool Extension page with a fixed contract

- Status: accepted
- Date: 2026-10-03
- Decided by: the creator decided the feature in chat on 2026-10-03 (the
  Phase 25 text in [PLAN.md](../PLAN.md): Space opens a floating panel, the
  thumbnail first, every viewer from the marketplace, a one-click offer).
  The contract below is the architect's (the design note on Opus, step 1 of
  Phase 25), written so that one coder can build the panel and another the
  first viewer without talking to each other. Every choice here can be
  superseded by the creator's word.

## Context

Phase 25 asks for a floating panel over the window, like macOS Quick Look.
Space on the cursor file opens it, Space or Esc closes it, Up and Down walk
the folder while it stays open, Enter opens the file in its program, and the
panes do not move. The panel shows Windows' thumbnail at once, then a
viewer's full view replaces it.

What shapes the contract:

- **Article 10, The Zero-Bloat Foundation.** CabinetOS ships no viewer. The
  application owns the key, the panel, the thumbnail and the hooks; every
  viewer is a Tool Extension from the marketplace. ("The core" in the plan's
  sentence means the application as opposed to its extensions. Below, "the
  core" is the Rust process `cabinetos-core.exe` and "the window" is the
  WinUI process.)
- **Article 11, Bifurcated Extension Architecture.** A viewer is a Tool
  Extension: a web page in WebView2, in its own browser process, with no
  network, that changes no files ([tool-extensions.md](../tool-extensions.md)).
  It never talks to the core; it talks to the window by web messages.
- **Article 1, Zero-Compromise Performance, and the Prime Directives.** The
  window reads no file and never waits on a page. The thumbnail comes from
  the core. A page that hangs or crashes cannot hold up a key.
- **Article 7, Absolute Keyboard Control & Command Palette.** Every action
  is a named command with a key that can be changed; the Immutable System
  Tier (`palette.show`, `overlay.close` on Esc, `keys.open`) stays as it is.
- **Article 3, Native Modern Aesthetics, and Article 4, Progressive
  Disclosure.** The panel is a Fluent card that looks like part of Windows,
  and a user who never presses Space never sees any of it.
- **Article 6, Universal Configuration.** The one new preference (which
  viewer shows a kind) is reachable from the panel, the palette and the
  file (the project skill `settings-three-ways`).

What exists today and what it means for the design:

- Space is bound: `edit.toggleSelectionInPlace` (`space`, `filesView`, Total
  Commander's Space: mark the cursor row in place, and measure a folder it
  marks). The creator's decision gives Space to Quick View, so marking in
  place needs another key (decision 6).
- A Tool Extension's `accepts` list means "Enter on such a file opens it in
  the tool". A viewer that put `*.png` there would change what Enter does on
  every PNG. Quick View needs its own claim (decision 1).
- A tool page loads again for every file, because a page that is already
  loaded cannot fetch from a host mapped after it loaded. The window maps
  the file's folder read-only on a new host `f<n>.<id>.cabinetos.example`
  for each file, and WebView2's browser process reads the file, never the
  window. The contract keeps this (decision 2).
- The window reads the tools folders once, at start. The core already lists
  tools (`list_tools`) and sends `tools_changed` after an install. Quick
  View must follow installs while the window runs, or the one-click offer
  would need a restart (decisions 1 and 5).
- The core already draws shell icons on its blocking threads (`get_icon`,
  `cabinetos-fs/src/hydrate.rs`) and hosts shell COM objects on threads of
  their own (`shell_menu`, ADR 0015). The thumbnail follows those patterns.

## Decision

### 1. How a viewer claims kinds

**1.1 The manifest.** A Tool Extension becomes a viewer by a new optional
key in `tool.json`:

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
| `quickView.kinds` | 1 to 512 patterns in the grammar of `accepts`: `*.ext` (an extension, which may have dots, such as `*.tar.gz`) or a whole name (`README`, `Dockerfile`), compared without case. No `*` alone and no other wildcards. |
| `quickView.entry` | Optional: the page Quick View loads, with the rules of `entry`. Without it, the tool's `entry` is used. |

A viewer that opens nothing in a pane gives `accepts` an empty list, as the
agent's chat page does. The JSON Schema `sdk/tools/tool.schema.json` gains
the `quickView` object, with `additionalProperties: false`.

Kinds are file names only. **No MIME types**, and no reading the file's
first bytes. Windows keeps no MIME type for a file: the registry's
`Content Type` is looked up by extension anyway, so a MIME list would be a
second spelling of the same thing. Reading the first bytes would mean a
disk read per Space and per arrow press before anything is shown. A folder
is never a kind (decision 4.6).

**1.2 Several viewers for one kind.** The order, first match wins:

1. **The user's choice**, the setting `quickView.viewers` in
   `cabinetos.json`: an object from a pattern to a tool ID, or to `"none"`
   for "no viewer, the thumbnail only". Example:
   `{"*.pdf": "pdf-viewer", "*.svg": "none"}`. Keys use the claim grammar.
2. **A whole-name claim before an extension claim**, and a longer extension
   before a shorter one (`*.tar.gz` before `*.gz`).
3. **Among equal claims, the viewer installed first.** Tools in the
   development folder (`--tools-dir` of the window) come first; then
   marketplace installs by their time in `installed.json`; then tools
   copied by hand, by folder name. Installing a second viewer therefore
   never takes a kind away from the first one without the user's word.

The user chooses in three ways, all writing `quickView.viewers` with
`set_value` (Article 6):

- **The panel:** when two or more viewers claim the shown file's kind, the
  panel's top bar has a drop-down button "Viewer: Image Viewer". Its list
  is the viewers that claim the kind, then "No viewer (thumbnail only)".
- **The palette:** `quickView.chooseViewer` ("View: Choose Quick View
  Viewer…") opens a prompt with the same list, for the shown file, or for
  the cursor file when the panel is closed.
- **The file:** `quickView.viewers` by hand. `sdk/config/cabinetos.schema.json`
  gains the key.

**1.3 The table: built by the core.** The core reads each tool's
`quickView` block while it lists tools (`cabinetos-market/src/tools.rs`)
and checks it strictly: a bad pattern or a bad `quickView.entry` leaves the
tool out of the table with a warning that names the file, and the tool's
pane use is not affected. It builds the table:

- at start;
- after a tool install or uninstall (where it sends `tools_changed` today);
- after a `config_changed` that touches `quickView.viewers`.

The window asks for it once after `hello` (`quick_view_table`) and gets it
again in the event `quick_view_table_changed` each time it changes:

```json
{"id":"01Q…","type":"quick_view_table"}
{"id":"01Q…","type":"quick_view_table",
 "viewers":[{"id":"image-viewer","name":"Image Viewer","version":"1.0.0",
             "dir":"C:\\Users\\a\\AppData\\Local\\CabinetOS\\tools\\image-viewer",
             "entry":"quickview.html"}],
 "kinds":[{"pattern":"*.jpg","viewers":["image-viewer","photo-pro"]},
          {"pattern":"*.svg","viewers":[],"off":true}]}
```

- `kinds[].viewers` is in the order of 1.2, the user's choice first. The
  window takes the first. `"off": true` is a kind the user set to `"none"`.
- The window matches the cursor file's name against `kinds` itself, with
  the matcher `accepts` already uses. This is a lookup in a table of at most
  a few hundred entries, no file is read, so Prime Directive 1 holds; a pipe
  round trip per arrow press would cost more of the 100 ms budget than it
  saves.
- The window starts a viewer page from the table's `dir` and `entry`. It
  does not read `tool.json` itself for Quick View, so a viewer installed
  while the window runs works at once.
- **The development folder.** The window passes its tools folder in
  development to the core with a new flag, `--dev-tools-dir <path>`, when
  it starts the core. The core lists it first, with the same rule as the
  window (it wins over an installed copy with the same ID). Without the
  flag the end-to-end tests could not use a fixture viewer.

### 2. The handover

**2.1 The channel: the existing Tool Extension page messages, extended.**
The page already speaks to the window by `chrome.webview.postMessage`, one
JSON object per message. Quick View adds message types with the prefix
`quickview-`. The page never talks to the core (Article 11). The new
messages get a schema file, `sdk/tools/quickview-messages.schema.json`,
written by the panel's unit, and their parsing and writing go into
`ui/CabinetOS.Core/Tools/ToolMessages.cs` beside the others.

**2.2 One file, one load.** For each file the window maps the file's
folder read-only on a new host `f<n>.<id>.cabinetos.example` and navigates
the viewer's WebView2 to the viewer's Quick View entry. The page posts
`{"type":"ready"}` (as every tool page does) and the window answers with
one `quickview-show`. The window sends no `context` and no `open` to a page
in the panel.

```json
{"type":"quickview-show","token":42,
 "path":"C:\\photos\\IMG_0412.jpg",
 "url":"https://f17.image-viewer.cabinetos.example/IMG_0412.jpg",
 "name":"IMG_0412.jpg","extension":".jpg","claim":"*.jpg",
 "size":4718230,"modified":"2026-09-30T14:02:11Z",
 "thumbnail":{"dataUrl":"data:image/png;base64,iVBORw0…","width":256,"height":192},
 "theme":{"appearance":"dark","background":"#202020","text":"#FFFFFF",
          "textSecondary":"#C5C5C5","accent":"#60CDFF","font":"Segoe UI Variable"},
 "panel":{"width":920,"height":610,"scale":1.5}}
```

| Field | Meaning |
|---|---|
| `token` | A number that grows with every file the panel shows, in this window. The page puts it in every report; the window drops a report with any other token. |
| `path` | The file's absolute path, for display only. The page reads the file from `url`. |
| `url` | Where the page reads the file: its folder, served read-only on a host of its own, new for every file. Range requests work, so a video can seek. |
| `name`, `extension` | The file's name, and its extension in lower case with its dot (`""` when it has none). |
| `claim` | The pattern of the viewer's `quickView.kinds` that matched. |
| `size`, `modified` | From the listing, in bytes and ISO 8601 UTC. The window reads them from the listing's shared memory, as it draws the row. |
| `thumbnail` | The thumbnail on screen now, so the page can show it while it loads and fade from it. `null` when the panel shows the icon card. |
| `theme` | The window's look: `appearance` (`dark` or `light`, which `prefers-color-scheme` also follows), four colours of the theme in effect, and the font. A `quickview-theme` message with the same object comes when the theme changes while the panel is open. |
| `panel` | The page's area in CSS pixels, and the screen's scale. Later sizes reach the page as ordinary `resize` events. |

**2.3 The next file while the page still loads the previous one: the newer
wins.** There is never more than one file in flight per panel:

- A move gives a new `token`. The window navigates the viewer's WebView2 to
  the entry again on the new file's host. WebView2 cancels the navigation
  still in progress by itself, so the old page never reports. A late report
  with the old token is dropped anyway.
- The thumbnail of the new file is asked for at once (decision 4).
- **The viewer waits for the arrow keys to rest.** After a move by Up or
  Down (or any move of the cursor), the viewer's navigation starts 120 ms
  after the last move, not at once, so a held arrow key (30 repeats a
  second) does not start 30 page loads a second. Space opens with no wait.
  The 120 ms fit inside the one-second budget.
- When the new file needs another viewer, the old viewer's WebView2 goes
  to `about:blank` and is hidden, and the new viewer's is shown.
- The old host mapping is cleared when the new navigation starts, so a page
  can never read an earlier folder.

Rejected: keeping the page loaded and serving each file through
`WebResourceRequested` on one fixed host. It would save the page's reload,
but the event comes on the UI thread and the window would open and stream
the file itself, which Prime Directive 1 forbids.

### 3. What the page reports back

All page messages carry the token of the `quickview-show` they answer.

```json
{"type":"quickview-shown","token":42,"keys":["left","right","plus","minus"],"details":"4032 × 3024"}
{"type":"quickview-failed","token":42,"reason":"unsupported","message":"This JPEG uses arithmetic coding, which the browser cannot read."}
{"type":"quickview-keys","token":42,"keys":["k","j","l"]}
{"type":"quickview-render","token":42,"width":1380,"height":915}
```

- **`quickview-shown`: ready.** The page has painted its full view. The
  window shows the page and hides the thumbnail with a 100 ms cross-fade.
  The page sends it after the first frame with content, not when loading
  starts: for an image after `img.decode()` resolves and two
  `requestAnimationFrame` callbacks; for a video at its first presented
  frame (`requestVideoFrameCallback`) with playback started. `details` is
  optional, at most 80 characters of plain text that the panel's bottom
  line shows beside the size and date ("4032 × 3024", "12 pages",
  "0:42 · 1920 × 1080"). `keys` is optional (below).
- **`quickview-failed`: keep the thumbnail, show why.** `reason` is one of
  `unsupported` (the page cannot read this file), `damaged`, `too-large` or
  `other`. `message` is at most 200 characters of plain text. The panel
  keeps the thumbnail (or the icon card) and shows under it:
  "Image Viewer cannot show this file. <message>". Nothing else changes:
  the arrows still walk, Enter still opens the file in its program.
- **`quickview-keys`: the page's own keys.** The keys a page wants while
  it shows this file, in the core's key grammar ([keybindings.md](../keybindings.md)).
  A page may send them in `quickview-shown` or later in `quickview-keys`
  (a video that ends may want other keys). The window answers with
  `{"type":"quickview-keys-granted","token":42,"keys":[…]}`, and then sends
  each press of a granted key as
  `{"type":"quickview-key","token":42,"key":"right","repeat":false}`.
- **`quickview-render`: Windows' image stack for a page.** A page that
  cannot decode a file in the browser (HEIC, TIFF, camera RAW, JPEG XR)
  asks for the shell's rendering at a size in device pixels, at most 2560
  on the longer side. The window asks the core (`render_image`, decision
  8), maps the folder the core wrote it to on a new host
  `r<n>.<id>.cabinetos.example`, and answers
  `{"type":"quickview-rendered","token":42,"url":"https://r4.image-viewer.cabinetos.example/image.png","width":1380,"height":1035}`
  or `{"type":"quickview-render-failed","token":42,"message":"…"}`. One
  render per token is in flight; a second waits for the first. This is the
  plan's "images (Windows' image stack)": formats the browser reads (JPEG,
  PNG, GIF, WebP, AVIF, BMP, ICO, SVG) are shown from `url` at full
  resolution, and the others through the codecs Windows has.
- **What a page in the panel may not do.** It runs no commands and follows
  no plugins: `command`, `subscribe` and `unsubscribe` from a page in the
  panel are refused and logged with the trigger `quickview:<id>`. A link in
  a Markdown file therefore does nothing in Quick View. Anything else not
  listed here is dropped, as today.

**Keys, the rule when a key is both the panel's and the page's.** The
keyboard stays in the pane's list while the panel shows (decision 6), so the
window decides where each key goes, in this order:

1. A chord's wait takes the key first (the chord state machine as today).
2. A key the keymap binds in the current context runs its command. This
   includes the panel's own keys (Space for `quickView.toggle`, Esc for
   `overlay.close`, Enter for `pane.openSelected`) and every key the user
   bound.
3. A key the page was granted goes to the page.
4. Every other key is the list's own, as without the panel: Up, Down,
   PageUp, PageDown, Home, End move the cursor, a letter types into quick
   search.

So **the panel wins**, always. A page may ask only for keys from this set,
each alone or with Shift: Left, Right, PageUp, PageDown, Home, End, the
letters A to Z, the digits 0 to 9 of the top row, `plus`, `minus`, `comma`,
`period`. The window grants a key from the set unless the keymap binds it in
`filesView` or everywhere, or it starts a chord bound in either. Space, Esc,
Enter, Up, Down, Tab and every combination with Ctrl, Alt or Win can never
be granted. A video viewer therefore pauses with K and seeks with J and L
(the keys web players use), not with Space, which closes the panel as it
does in macOS Quick Look. The grant is checked again at each press, so a
binding the user adds while the panel is open wins at once. Granting
PageUp and PageDown to a PDF page means those keys turn its pages and do
not move the cursor while the panel shows; Up and Down still walk.

### 4. The thumbnail-first rule

**4.1 Who asks: the core, never the UI thread.** The window sends
`get_thumbnail`; the core asks the shell's image factory
(`IShellItemImageFactory::GetImage`, the thumbnails Explorer shows) on
threads of its own:

```json
{"id":"01Q…","type":"get_thumbnail","path":"C:\\photos\\IMG_0412.jpg","size":256,"ahead":false}
{"id":"01Q…","type":"thumbnail","path":"C:\\photos\\IMG_0412.jpg","size":256,
 "width":256,"height":192,"png_base64":"iVBORw0KGgo…"}
{"id":"01Q…","type":"thumbnail","path":"C:\\photos\\notes.xyz","size":256,
 "png_base64":null,"reason":"none"}
```

- **Size: 256 pixels on the longer side**, aspect kept
  (`SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY`). 256
  is Explorer's "extra large icons" size, so the shell's own thumbnail
  cache (thumbcache) most often has it already and the answer is a cache
  read. The panel draws it scaled to fit, at most twice its size, centred:
  a soft picture for a moment, not a sharp one. `size` takes 96, 256 or 768
  (the cache's sizes); the panel uses 256.
- `SIIGBF_THUMBNAILONLY` means the shell answers with a real thumbnail or
  nothing, never a generic icon. `png_base64: null` comes with a `reason`:
  `none` (the shell has no thumbnail for this file), `timeout`, `busy`,
  `cloud` or `superseded` (below). Errors that are not about the thumbnail
  (`not_found`, `access_denied`, `invalid_path`) are the usual error replies.
- **A file not on this disk** (a cloud placeholder: the listing's "not on
  this disk" flag) is asked with `SIIGBF_INCACHEONLY` only, so Space never
  starts a download. Nothing in the cache gives `reason: "cloud"`.
- **Threads.** Two dedicated threads in the core, each in a COM
  single-threaded apartment, take requests from a queue. A request that the
  shell has not answered in 2 s gets `reason: "timeout"`; its thread is
  left to finish and a new one takes its place. When four threads are
  stuck, requests get `reason: "busy"` until one comes back, and the log
  says which file each stuck thread is on. Most thumbnail handlers run in a
  surrogate process of Windows; one that opted out of that runs inside the
  core, which is the risk ADR 0015 accepted for the shell menu.
- **Newest first, older dropped.** Per connection the queue holds at most
  one request with `ahead: false`; a newer one replaces a queued one, which
  gets `reason: "superseded"`. A request already in the shell runs to its
  end, and its picture goes into the cache. `ahead: true` requests (at most
  four queued per connection, the oldest dropped as superseded) run only
  when no `ahead: false` request waits.
- **Warm start.** After the first `list_directory` reply, the core starts
  the two threads and has the shell load its image factory once on the
  listed folder, on a blocking thread, as the icons are drawn ahead
  (`icons.rs`). The first Space of a session then does not pay the shell's
  start (about 35 ms for the icons).

**4.2 The cache.** The core keeps the last 128 thumbnail PNGs in memory,
by the lower-case path, the last-write time, the file size and the size
asked for. A changed file is a new entry. Walking back and forth is a
memory read. There is no disk cache of CabinetOS's own: the shell's
thumbcache is the disk cache.

**4.3 Read ahead.** While the panel is open and shows file *i*, the window
asks `ahead: true` for the files next to it: first the one in the direction
of the last move, then the other.

**4.4 What shows when the shell has none: the icon card.** At the moment
Space is handled, before any answer, the panel draws the icon card: the
row's icon at 48 pixels (96 on a 200 % screen) from the icons the window
already holds, the file's name, its type name, size and date. It costs no
request, so something of the file is on screen at the next frame. The
thumbnail replaces the card when it comes; a `null` thumbnail leaves the
card. When the viewer reports `quickview-shown`, the page replaces whichever
of the two shows.

**4.5 The time budget, and how it is measured.**

| Moment | Goal on the laptop |
|---|---|
| The icon card on screen, from the key press | 50 ms, every time |
| The thumbnail on screen, from the key press | 100 ms, for a file whose thumbnail the shell has cached |
| The full view (`quickview-shown` and its frame), from the key press | 1,000 ms for a 12-megapixel JPEG, and a video playing within 1,000 ms |
| The window's frames while Down is held for 3 s over 50 images | no frame with UI work over 33 ms |

- **The clock.** Zero is the moment the window handles the key (the trace
  of the command run, Article 12). Each moment ends at the
  `CompositionTarget.Rendering` after the change is made: the card set, the
  thumbnail's `ImageOpened`, the page made visible after `quickview-shown`.
- **The log.** The window writes one info line per file shown,
  `quick view shown`, with the trace, the viewer (or none), `card_ms`,
  `thumbnail_ms` (or `thumbnail: none`), `full_ms` (or `failed` with the
  reason, or `skipped` when the user moved on first), and `cold: true` for
  the first load of that viewer in this window. The core logs one debug
  line per thumbnail with `took_ms`, `cached` and the reason when it has
  none.
- **The live check.** A new section of `ui/livecheck/livecheck.ps1` opens
  Quick View on a fixture folder: a 12 MP JPEG, a PNG, a HEIC, a 10 s 1080p
  H.264 MP4, a text file and a `.xyz` file no viewer claims. It presses
  Space ten times on each and judges the 90th percentile of the warm runs
  against the table. The first, cold run is printed apart. It runs on this
  PC and on the laptop (`ui/livecheck/remote-livecheck.ps1`), and the
  laptop's numbers decide the goals (the creator's rule of 2026-10-02).
- **The cold load.** A viewer's browser process starts on the first Space
  that needs it in a window session (decision 7). On the laptop that start
  may cost more than the budget. The cold run is measured and reported,
  with 1.5 s as its ceiling. If the laptop misses that ceiling, the next
  step is to start the image viewer's process when the window is idle after
  its first listing, only for a user who installed a viewer. It is not
  built before the measurement says it is needed.

**4.6 Folders.** Space on a folder shows the folder card: the shell's
folder thumbnail (or the folder icon), the name, and its size, measured at
once as `file.calculateFolderSize` measures it (the row shows the size
too). No viewer shows folders in this version. So Total Commander's "Space
shows a folder's size" still works through Quick View. Space on the `..`
row does nothing.

### 5. The install offer

**5.1 How the panel knows what to offer.** No pack's name is written into
CabinetOS (Article 10). The marketplace already knows: an index item of
kind `tool` carries the tool's `tool.json` as its `manifest`, and so its
`quickView.kinds`. When the shown file matches no viewer (and the user did
not set the kind to `"none"`), the window asks:

```json
{"id":"01Q…","type":"quick_view_offer","name":"report.pdf"}
{"id":"01Q…","type":"quick_view_offer","item":{"id":"document-viewer","name":"Document Viewer",
 "version":"1.0.0","size":1240000,"author":{"name":"CabinetOS","verified":false},
 "description":"Shows PDF and Office documents in Quick View."}}
{"id":"01Q…","type":"quick_view_offer","item":null,"reason":"no_item"}
```

- The core matches the name against the `quickView.kinds` of the
  extensions catalogue's tool items, with the rules of decision 1.2, and
  answers the first match in the index's order that this core can run and
  that is not installed. The index's order is the marketplace repository's,
  which the creator curates, so the first-party pack is offered first.
- **The catalogue.** The core uses its cached `index.json` when it is under
  seven days old. Otherwise it reads it, with its `ETag`, at most once per
  core session. This adds `quick_view_offer` to the requests that may reach
  the network (trust rule 6 of [marketplace.md](../marketplace.md)): the user
  pressed Space to see a file, and the offer cannot be made without the
  catalogue. When the catalogue cannot be read, the answer is
  `item: null, reason: "offline"`, and the panel shows no offer.
- The viewer pack's items set `minCoreVersion` to the first release with
  Quick View: older windows read `tool.json` strictly, so they would leave
  a tool with a `quickView` key out.

**5.2 What the panel shows.** Under the thumbnail or the icon card, a bar
of the panel (not a dialog):

- With an item: "No viewer for .pdf files is installed." and the button
  "Install Document Viewer", with the line "Free · 1.2 MB · from the
  CabinetOS marketplace" under it.
- While it installs: "Installing Document Viewer… 45 %" (from
  `install_progress`), with no button.
- When the install fails: "The install failed: <message>." and the button
  "Try again".
- Without an item: "No viewer for .xyz files." and nothing else.

The offer shows every time such a file is shown, as a quiet bar, so the
first Space on the kind already shows it and nothing is remembered.

**5.3 The one click.** The button (or the palette's
`quickView.installViewer`, "View: Install Quick View Viewer", for the
keyboard) sends the marketplace's existing `install_extension` with the
item's ID. A tool needs no permissions review (it can change no files).
When the install ends, the core sends `tools_changed` and then
`quick_view_table_changed`; if the panel still shows a file of that kind,
it starts the new viewer at once. A failed install changes nothing else.

### 6. The keys in the command registry

| ID | Palette | Default keys | When | Runs in |
|---|---|---|---|---|
| `quickView.toggle` | View: Toggle Quick View | `space` | `filesView` | UI |
| `quickView.chooseViewer` | View: Choose Quick View Viewer… | | | UI |
| `quickView.installViewer` | View: Install Quick View Viewer | | | UI |
| `edit.toggleSelectionInPlace` | Edit: Toggle Selection in Place | `shift+space` (was `space`) | `filesView` | UI |

- **The keyboard stays in the pane's list while the panel shows.** The
  panel and its page never take the keyboard. When a click puts the focus
  in the page, the window moves it back to the list at once (the click
  itself still reaches the page, so a video's controls work by mouse). So a
  hung page can never take a key (decision 7), and the panel needs no key
  of its own beyond Space:
  - **Space** is `quickView.toggle`: it opens the panel on the cursor row,
    and closes it when it is open.
  - **Esc** is `overlay.close`, unchanged. Quick View is an overlay under
    the rule "One overlay at a time" ([keybindings.md](../keybindings.md)):
    Esc closes it, and opening the palette, Quick Open, a prompt, the theme
    picker or another overlay closes it first. No new command takes Esc,
    which the Immutable System Tier's rule 2 forbids anyway.
  - **Up and Down** are not commands. They are the list's own cursor keys,
    and the panel follows the pane's cursor (as do PageUp, PageDown, Home,
    End, quick search and a click on a row, unless the page was granted the
    key). In the column view the panel follows the cursor of the column
    that has the keyboard. When the panel closes, the cursor stays on the
    last file shown.
  - **Enter** is `pane.openSelected`, unchanged: the panel closes and the
    cursor row opens as Enter opens it in the pane. For a file, that is its
    program. For a file that a pane Tool Extension `accepts`, that is the
    tool, because Enter keeps one meaning everywhere.
  - Every other key of the pane works under the panel (F5, Delete,
    Shift+Space): after a Delete the cursor moves to the next row and the
    panel follows it.
- **The panel closes** on Space, Esc, Enter, another overlay, the pane's
  folder changing, or the keyboard leaving the pane's list (Tab, a click in
  the other pane, a tool tab coming to the front).
- **Not in the Immutable System Tier.** Space in Quick View is a normal,
  rebindable key. The tier exists so that nobody can lock themselves out,
  and Esc, which is in the tier, always closes the panel. Putting Space in
  the tier would forbid every other command on Space in every context
  (rule 2), and take Space from a Total Commander user who wants it back
  for marking.
- **Marking in place moves to Shift+Space.** It was free in every context,
  and a text box keeps Shift+Space for typing, so nothing changes there.
  Insert still marks and moves down. A user who wants Total Commander's
  Space binds `space` to `edit.toggleSelectionInPlace` and gives
  `quickView.toggle` another key, in the keyboard shortcuts view, the
  palette or the file, as for any command. The change needs a CHANGELOG
  line.
- **Chord safety.** The core already refuses a keymap where a combination
  is both a binding and the first half of a chord, so `space` cannot start
  a chord while it is bound. A page's granted keys never include a bound
  key or a chord's first half, and the chord's wait takes a key before the
  page could (decision 3, the order of keys).

### 7. The failure path and the sandbox

A viewer page cannot block the window (Article 1, and Article 8, Sandboxed
Extensibility: bad code never crashes the file manager).

- **Nothing waits on a page.** The panel draws the card before any answer;
  every call to a page is a posted message or an async call whose result
  the window does not wait for; the keyboard is never in the page.
- **Its own process.** A viewer in the panel uses its tool's WebView2
  user-data folder (`tool-<id>`), so it shares a browser process with the
  same tool's pane page and with no other tool. The rules of every tool page
  hold: no network, no downloads, no new windows, no permissions, no
  developer tools in Release. The window allows media to play without a
  user gesture in the tool environments (`--autoplay-policy=no-user-gesture-required`),
  so a video plays when it is shown.
- **Timeouts.** No `ready` within 3 s of the navigation: the panel keeps
  the thumbnail and says "Image Viewer did not start." `ready` but no
  `quickview-shown` or `quickview-failed` within 5 s: "Still loading…"
  under the thumbnail. Nothing after 30 s: "Image Viewer did not finish.",
  as a failure; a `quickview-shown` that still comes with the current token
  is shown.
- **A crash or a hang.** On `CoreWebView2.ProcessFailed` (the page's
  process ended, or WebView2 reports it unresponsive) the panel keeps the
  thumbnail and says "Image Viewer stopped." The window closes that
  WebView2 and makes a new one at the next file or the next Space, as the
  pane's Reload does. After three stops of one viewer within 60 s it is off
  for the rest of the window session: "Image Viewer stopped 3 times. It is
  off until CabinetOS restarts." The arrows, Esc, Space and Enter keep
  working throughout, because the keyboard is in the list.
- **When the panel closes,** the viewer's WebView2 goes to `about:blank`
  at once (a video stops, its decoder is freed) and is suspended
  (`TrySuspendAsync`), as hidden sidebar pages are. The window keeps the
  WebView2s of at most three viewers alive; the one used longest ago is
  closed when a fourth is needed.
- **The thumbnail side** has its own limits (decision 4.1: 2 s per request,
  stuck threads replaced, `busy` after four).
- **Windows' preview handlers** (the Office viewer of the pack's last
  step). A web page cannot host a preview handler, which draws into a
  native window. The hook is the window's, used only on a page's request:
  a page posts `{"type":"quickview-system-preview","token":42}`, and the
  window hosts the handler registered for the file's extension over the
  page's area. Out of process only (`CLSCTX_LOCAL_SERVER`, the surrogate
  Windows runs them in for Explorer); a handler that can run only inside
  the window is refused with `quickview-system-preview-failed`. Its calls
  run on a thread of their own with a 3 s limit. A native child window of
  another process shares the window's input queue, so a hung handler could
  freeze the window: the unit that builds this hook must test it with a
  handler that hangs on purpose before the Office viewer ships, and record
  what it found in an ADR of its own. Nothing of this is in the panel's
  first unit.

### 8. What the core adds and what the window adds

Protocol version 20 becomes 21: four requests, their replies, one event and
one config key. Each item names its test; "the five checks" are the core's
commands in CLAUDE.md.

**The core** (Rust, `core/`):

| # | Work | Tests |
|---|---|---|
| C1 | Protocol 21 in `cabinetos-protocol`: `get_thumbnail` / `thumbnail`, `render_image` / `rendered_image`, `quick_view_table` / `quick_view_table`, `quick_view_offer` / `quick_view_offer`, the event `quick_view_table_changed`; the schemas in `sdk/protocol/` (request, response, event); [ipc.md](../ipc.md) gets a section "Quick View". | Round-trip and schema tests as for every message today. |
| C2 | The thumbnail service: `cabinetos-fs/src/thumbnail.rs` (the `unsafe` COM calls, each with `// SAFETY:`), `cabinetos-core/src/quickview.rs` (the queue, the 2 s limit, the stuck threads, the 128-entry cache, the warm start after the first listing). | Unit tests on fixture files in a temporary folder: a PNG gives a picture with the right aspect; a `.xyz` file gives `none`; a missing file `not_found`; a second request comes from the cache; a queued request is superseded by a newer one; a stub of the shell call that never returns gives `timeout` and then `busy` after four. |
| C3 | `render_image` (`path`, `max_size` up to 2560): the shell's image factory at that size, written as a PNG to `%LOCALAPPDATA%\CabinetOS\cache\render\<random>\image.png`; the reply has the folder, `width`, `height`. The cache folder is emptied at start and keeps the last 16 renders. 5 s limit. | Unit test: a PNG fixture rendered at 512 gives a file of that size; the 17th render removes the first folder. |
| C4 | The table: `quickView` in `cabinetos-market/src/tools.rs` checked strictly; the order of decision 1.2 (installed time from `installed.json`); `--dev-tools-dir`; the setting `quickView.viewers` in `cabinetos-config` and `sdk/config/cabinetos.schema.json`; rebuilt and sent on start, `tools_changed` and the setting's change. | Unit tests: two viewers on one kind give the earlier install first; a whole name beats an extension and `*.tar.gz` beats `*.gz`; the user's choice comes first; `"none"` gives `off`; a bad pattern leaves the viewer out and keeps its pane use; the dev folder wins. A core test installs a fixture viewer from a local index and sees `quick_view_table_changed`. |
| C5 | `quick_view_offer` in `cabinetos-market`: the match over the catalogue's tool items, the seven-day cache rule, once per session, `offline`. [marketplace.md](../marketplace.md)'s trust rule 6 gets the new request. | Unit tests with a local index: the first matching item in index order; an installed one is skipped; an item that needs a newer core is skipped; no catalogue gives `offline`. |
| C6 | The commands in `cabinetos-commands`: `quickView.toggle`, `quickView.chooseViewer`, `quickView.installViewer`; `edit.toggleSelectionInPlace` to `shift+space`; [keybindings.md](../keybindings.md) updated. | The registry's tests (IDs, keys, no conflict, the tier unchanged); a keymap test that `space` can still be bound back to `edit.toggleSelectionInPlace`. |

**The window** (C#, `ui/`):

| # | Work | Tests |
|---|---|---|
| W1 | The panel's logic in `ui/CabinetOS.Core/QuickView/`, with no XAML: the matcher over the table, the token state machine (card, thumbnail, loading, shown, failed, stopped, off, offer), the 120 ms rest after moves, the timeouts, the crash count, the key routing of decision 3 (grant and press). | Unit tests in `CabinetOS.Tests` for each state change and each rule: a late report with an old token is dropped; a key bound in `filesView` is never granted; Space, Esc, Enter, Up and Down never are; three stops in 60 s turn the viewer off. |
| W2 | The new web messages in `ToolMessages.cs` (parse and write) and `sdk/tools/quickview-messages.schema.json`; the panel page's refusal of `command` and `subscribe`. | Unit tests beside the current `ToolMessages` tests: each message's fields, the size limits, malformed messages dropped. |
| W3 | The panel's view: `MainWindow.QuickView.cs` and a XAML card in `Views/`. Centred, 72 % of the window's width and 80 % of its height, at least 480 × 360, inside a 24 px margin (in the compact overlay, the window less 8 px). A 40 px top bar (icon, name, "3 of 120", the viewer button when there are two viewers, Open, Close), the content, a 28 px bottom line (size, date, `details`). Acrylic over the panes, 8 px corners, a theme shadow, a 120 ms scale-and-fade that follows Windows' animation setting. No scrim: the panes stay visible and do not move. | End-to-end tests with new snapshot steps `quickview:<label>` (log what the panel shows: state, file, viewer, token, the line under the thumbnail) and `until:quickview-shown`, `until:quickview-thumbnail`; screenshots in dark and light. |
| W4 | The viewer host: one WebView2 per viewer in the panel, its tool's user-data folder, the per-file host mapping, `about:blank` and suspend on close, at most three alive, the autoplay argument, focus moved back to the list, the process-failure path. | End-to-end tests with the fixture viewer (below): shown, failed, hang (no `shown`: "Still loading…", then failed at 30 s, with a test clock), crash (`crash:quickview:<id>`, a new step: the panel says it stopped and the next file works), and Esc closing the panel while the page hangs. |
| W5 | Space, the overlay rule, the close rules, Enter, following the cursor in the list and in the column view, the folder card with its measure. | End-to-end: Space opens and closes; Esc closes; Down and Up walk five files and the log shows five tokens; Enter opens a fixture file and closes the panel; Tab closes it; Shift+Space marks in place. |
| W6 | Thumbnails: `get_thumbnail` on open and on each move, read ahead both ways, the icon card first, the scale limit. | End-to-end: a PNG shows a thumbnail before the viewer; a `.xyz` file shows the card with no request left open. |
| W7 | The offer bar and the install with progress, through `quick_view_offer` and `install_extension`; the new viewer starting when the table changes. | End-to-end with a local index that holds the fixture viewer: Space on its kind shows the offer, the install runs, the viewer shows the file without a restart. |
| W8 | The viewer choice: the panel's drop-down and the palette prompt, both writing `quickView.viewers`. | End-to-end: choosing the second viewer writes the setting (`until:config`) and the next Space uses it; `"none"` leaves the thumbnail only. |
| W9 | The `quick view shown` log line and the live-check section of decision 4.5 on this PC and the laptop. | The live check itself; its numbers go into the merge's report. |

**The fixture viewer.** `sdk/fixtures/tools/quickview-fixture/` claims
`*.qvtest` and `*.png`. A `.qvtest` file's first line says what the page
does: `shown`, `shown-keys left right`, `failed`, `hang` (posts `ready` and
nothing more), `crash` (an endless loop), `slow 4000` (reports after 4 s).
The panel's tests need nothing from the viewer pack.

**The first viewer's contract**, for the coder of the pack (step 3 of the
phase; images and media first). A viewer:

1. adds `quickView` to its `tool.json`, with the kinds it shows, and sets
   the item's `minCoreVersion` to the first release with Quick View;
2. posts `ready` once its listener is set, and expects a fresh load for
   every file;
3. on `quickview-show` loads from `url` and posts `quickview-shown` after
   its first frame with content, or `quickview-failed` with a reason;
4. asks for keys only from the set of decision 3, and acts on
   `quickview-key`;
5. follows `theme` and `quickview-theme`;
6. loads everything from its own folder, with a Content-Security-Policy
   that allows `img-src` and `media-src` from `https://*.cabinetos.example`
   and `data:`;
7. for formats the browser cannot decode, asks `quickview-render`.

## Consequences

- Quick View costs a user who never presses Space two idle threads in the
  core and nothing in the window. A user without viewers still gets the
  thumbnail, the folder size and the offer.
- The window follows tool installs live for Quick View. Pane tools still
  load at start; making them follow `quick_view_table`'s way is a later
  step.
- **Space no longer marks in place by default; Shift+Space does.** This
  changes a Total Commander habit and needs a CHANGELOG line and a line in
  the release notes. Space on a folder still shows its size, through the
  folder card.
- Trust rule 6 of the marketplace gains `quick_view_offer`, and
  [marketplace.md](../marketplace.md) must say so when C5 lands.
- Protocol version 21. The window and the core ship together, so no older
  peer meets the new messages in a release.
- The preview-handler hook carries a known risk (a hung handler can freeze
  the window's input) that must be tested before the Office viewer ships;
  it may change this ADR's decision 7 through an ADR of its own.
- The documents the units update: [tool-extensions.md](../tool-extensions.md)
  (the `quickView` key and the messages), [ipc.md](../ipc.md),
  [keybindings.md](../keybindings.md), [ui.md](../ui.md) ("Quick View"),
  [config.md](../config.md) (`quickView.viewers`),
  [marketplace.md](../marketplace.md), and the `settings-three-ways`
  skill's audit (the new setting, reachable three ways).

## Options considered

- **Viewers in the window (an image control, a media player).** The fastest
  path to a picture. Rejected by the plan's decision and by Article 10: the
  core ships no viewer.
- **The page takes the keyboard.** A page would hear every key and pass the
  panel's keys back, as a tool tab does today. Rejected: a page that hangs
  holds the keyboard, so Space and Esc would stop working at the moment
  they are needed (Article 1), and every page would have to pass the right
  keys back correctly.
- **The page's keys win over the panel's** (Space pauses a video).
  Rejected: Space would mean "close" for some files and "pause" for
  others. The panel's keys mean the same for every file.
- **Space in the Immutable System Tier.** Rejected (decision 6): Esc
  already guarantees the way out, and the tier's rule 2 would take Space
  away everywhere.
- **Space by row: Quick View on a file, marking on a folder.** Keeps Total
  Commander's folder habit. Rejected: one key with two meanings by row
  type is hard to learn and to rebind. The folder card keeps the folder's
  size on Space.
- **Viewers claim kinds through `accepts`.** No new key. Rejected:
  `accepts` decides what Enter does, so installing an image viewer would
  stop Enter from opening photos in their program.
- **MIME types, or reading the file's first bytes.** Rejected (decision
  1.1): Windows has no MIME type per file beyond the extension, and a read
  per arrow press costs time before anything shows.
- **A catch-all `*` claim** (a text viewer for every unknown file).
  Rejected: every kind would then have a viewer that fails on it, and the
  offer for the right viewer would never appear.
- **The window resolves viewers by reading `tool.json` itself.** Rejected:
  it is file reading in the window, and it would not see installs while it
  runs. The core reads and checks the manifests already.
- **One fixed host and `WebResourceRequested`, so the page stays loaded.**
  Rejected (decision 2.3): the window would read the file on the UI
  thread's event.
- **The window asks Windows for the thumbnail** (`StorageFile.GetThumbnailAsync`).
  Rejected: file access in the window (Prime Directive 1), and a hang in a
  thumbnail handler would land in the window's process.
- **The thumbnail through shared memory instead of base64 PNG.** Rejected
  for now: a 256-pixel PNG is about 100 KB, base64 and parsing take under
  a millisecond, and `get_icon` works this way. To revisit if the live
  check shows the pipe in the 100 ms.
- **Start every installed viewer's browser process at window start.**
  Rejected: a browser process for every user who installed the pack,
  whether they use Quick View or not (Article 10's "lightweight for
  purists"). Measured instead (decision 4.5), with a narrower fallback.
- **A pack ID written into the window for the offer.** Rejected: no
  extension's name is in the core. The index's manifests say which item
  claims a kind.
- **Remember that the offer was shown, and show it once per kind.**
  Rejected: it needs stored state and a way to reset it. A quiet bar on
  every file of the kind is honest and costs nothing.
- **Walking the marked files instead of the folder.** macOS walks a
  selection of several files. Not in this version: the panel follows the
  cursor. It can come later as a separate choice.
