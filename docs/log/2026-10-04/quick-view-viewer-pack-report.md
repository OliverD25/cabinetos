# The viewer pack, first two viewers (Phase 25, step 3, unit 1): the Image Viewer and the Media Viewer

Coder report, Sonnet 5.5, 2026-10-04. Branch `worktree-agent-a9b306b7f733d16e2`, from main's `13c3d55` (Quick View
merged at `6b4eaa3`). The creator was away; every decision the handout left open is below as a `what - because -
undo` line. Nothing opened a window on this PC. Every window run (the end-to-end tests, the live check, the media
probe) ran on the Omen laptop through the scripts, which ran the exact commit named in each result.

Constitution: Article 10 (Zero-Bloat Foundation) is why both viewers are Tool Extensions in `sdk/tools` and
CabinetOS ships neither; Article 11 (Bifurcated Extension Architecture) is why a viewer is a page that talks to the
window only; Article 8 (Sandboxed Extensibility) is why each page has a Content-Security-Policy that allows only its
own folder and the file hosts; Article 1 (Zero-Compromise Performance) is why the first picture is reported after the
first presented frame and nothing in a page can hold up a key.

## What was built

| Item | Commit | What it is | Its tests |
|---|---|---|---|
| The Image Viewer | `00e25e3` | `sdk/tools/image-viewer/`: `tool.json` (18 kinds, `accepts: []`), `index.html`, `quickview.html`, `.css`, `.js`, README. Browser formats from the file's `url` at full resolution, HEIC, HEIF, TIFF, JPEG XR, DNG, CR2, NEF and ARW through `quickview-render`; fit to the panel, zoom `+ - 0 1` and the wheel (25 % steps to 800 %), drag to pan, theme, 256 MB limit, `damaged` on a decode failure | the page's logic against a mocked window in headless Edge (every format, the keys, the wheel, a real drag, both themes, the render path, the limit, a damaged file); live check section 25 |
| The probe and its files | `0534157` | `ui/livecheck/media-probe.ps1` (a headless Edge plays each media file in a folder and says whether picture or sound was decoded and the clock moved) and `ui/livecheck/fixtures/media/`, one tiny file of each kind; `.gitattributes` marks media files binary | the laptop's run, below |
| The Media Viewer | `4813b7b` | `sdk/tools/media-viewer/` (13 kinds): a `<video>` fitted to the panel, reported at the first presented frame (`requestVideoFrameCallback`), or audio with a drawn symbol, the file name and `<audio>`, reported on `playing`; keys `K J L , . M + -` and `0` to `9`; a small badge says what a key did | the page against the mock (all 14 files, a damaged one, every key, both themes); `ToolTests` (2 cases): both manifests against the window's parser and `tool.schema.json` |
| The index | `84c8a50` | `build-index.ps1 -Viewers`: every folder of `sdk/tools` whose `tool.json` has `quickView` becomes a tool item (zip, SHA-256, `tool.json` as `manifest`), `minCoreVersion` `0.1.3`; it checks the kinds, the page and the folder name | the market's build-script test (now builds with `-Viewers`: two items, accepted by the core, offered to a 0.1.3 core by their kinds, to none of 0.1.2); a core test over the pipe: a built index offers the Image Viewer for `photo.jpg`, the Media Viewer for `Clip.MKV`, `no_item` for `notes.txt`, installs the Image Viewer through the real core and shows its 18 kinds in the table |
| The live check | `7f5dac2`, `74151d3` | section 25: 12 MP JPEG and PNG rows, a 1080p H.264 clip, a silent MP3, a TIFF (the render path), a HEIC when Windows has the HEIF codec, the fixture viewer's PNG rows in a second pass, every media kind once in the real panel, and the keys the viewers ask for | the five laptop runs below |
| A fault in the window and its test | `8eeb319`, `473dc8d`, `8280510` | the TIFF and HEIC rows failed with `damaged` (see "The one window change"); `TestTiff` and an end-to-end test with the real Image Viewer, red at `8eeb319`, green at `473dc8d`; `QuickViewHost` maps the core's folder of drawings before the page loads, and `WebViewHost.MapFolder(create: true)` makes that folder each time the mapping is set (`8280510`, after the whole suite found a race) | `QuickViewEndToEndTests` 7 of 7 on the laptop, and the whole suite 1512 of 1512 at `8280510` |
| Documents | the report's commit | `sdk/tools/README.md`, the two viewers' READMEs, `docs/marketplace.md`, `docs/tool-extensions.md` ("The viewer pack"), `docs/ui.md` (section 25, the drawing host), ADR 0024, `CHANGELOG.md` | |

## The one window change, and why

The handout expected none. The Image Viewer's TIFF and HEIC rows ended with `failed:damaged` in the first laptop run
(`ddf9`): the core drew the picture (a valid PNG, 22 ms) and the window posted `quickview-rendered`, but the page's
request for the `url` failed. The window had mapped the drawing's folder on a new host `r<n>` when the drawing was
ready, after the viewer's page had loaded, and WebView2's documentation of `SetVirtualHostNameToFolderMapping` says
that "changes to the mapping might not be applied to the current page and a reload of the page is needed". ADR 0023's
own Context says it of the file's host and maps that one before each load; decision 3 did not do the same for the
drawing's host. Nothing covered the render path: the fixture viewer never asks for a drawing.

I proved it before changing anything: an end-to-end test with the real Image Viewer and a hand-made TIFF was red at
`8eeb319` (state `failed`, the other six tests green) and is green at `473dc8d`. The fix is about 45 lines in
`ui/CabinetOS/Services/QuickViewHost.cs`: the core's folder of drawings (`cache\render`) is mapped on `r<n>` before
each load, and a drawing is `image.png` under its own folder there. The messages are as the ADR wrote them. A second
commit (`8280510`) closed a race the whole suite found: the core removes that folder once at its start, WebView2
refuses a mapping to a folder that is not there, and the first version made the folder only before the load, so a
removal between the load and the WebView2 start failed the start ("WebView2 refused a folder", in a test that never
asks for a drawing). `WebViewHost.MapFolder` has a new optional `create` that makes the folder each time the mapping
is set, at the call and at the start. The change is in its own commits and ADR 0024 records it, so the architect can
drop it or choose another way (`git revert 8280510 473dc8d` returns the old behaviour, and with it the broken TIFF and
HEIC rows).

## Measured on the laptop

The live check of `8280510`, the final code (run `2026-10-04-0244-3358`, ran commit `8280510`, exit 0, 11 min 51 s),
section 25, ten opens per file, the first apart, the 90th percentile of the nine warm ones (with nine values, that is
the largest):

| file | viewer | first: card / thumbnail / full (cold) | warm runs | card worst | thumbnail p90 | full view p90 |
|---|---|---|---|---|---|---|
| 1-big.png [real] | image-viewer | 30 / 144 / 572 ms (True) | 9 | 6 ms | 18 ms | 125 ms |
| 2-image.png [real] | image-viewer | 5 / 18 / 61 ms (False) | 9 | 7 ms | 20 ms | 145 ms |
| 3-page.qvtest [real] | quickview-fixture | 6 / none / 462 ms (True) | 9 | 7 ms | none (none) | 47 ms |
| 4-photo.jpg [real] | image-viewer | 6 / 34 / 68 ms (False) | 9 | 91 ms | 117 ms | 139 ms |
| 5-notes.txt [real] | none | 6 / none / none ms (False) | 9 | 7 ms | none (none) | no full view (none) |
| 6-unknown.xyz [real] | none | 3 / none / none ms (False) | 9 | 12 ms | none (none) | no full view (none) |
| 7-clip.mp4 [real] | media-viewer | 6 / 454 / 1051 ms (True) | 9 | 13 ms | 25 ms | 714 ms |
| 8-sound.mp3 [real] | media-viewer | 5 / none / 95 ms (False) | 9 | 6 ms | none (none) | 67 ms |
| 9-scan.tif [real] | image-viewer | 13 / 33 / 206 ms (False) | 9 | 12 ms | 27 ms | 87 ms |
| a-photo.heic [real] | image-viewer | 14 / 38 / 211 ms (False) | 9 | 14 ms | 26 ms | 157 ms |
| 1-big.png [fixture] | quickview-fixture | 6 / 20 / 124 ms (False) | 9 | 74 ms | 103 ms | 174 ms |
| 2-image.png [fixture] | quickview-fixture | 6 / 33 / 61 ms (False) | 9 | 6 ms | 18 ms | 121 ms |

- The goals of ADR 0023's table: the full view of the 12 MP JPEG within 1000 ms warm: 139 ms (68 ms in the run before).
  The video playing within 1000 ms: 714 ms warm. The first cold Space: 572 ms for the Image Viewer (the first of the
  session), 1051 ms for the Media Viewer, 462 ms for the fixture; the ceiling is 1500 ms: all met.
- The line "quick view goal ... met" says **no** in this run, and **yes** in the run before (`b449`, `74151d3`, the same
  viewers; the window code differs by the folder-creation fix only). The two rows over their goals here, the JPEG's card
  (91 ms) and thumbnail (117 ms) and the fixture PNG's card (74 ms) and thumbnail (103 ms), are single opens out of nine.
  See the table of runs below for why they are not the viewers'.
- The walk goal (Down held for 3 s over 50 images, now with the real Image Viewer): met, 398 frames, one with UI work
  over 20 ms, none over 33 ms, worst frame 15.3 ms. The panel goal: met. No `False` line in the whole run. Esc closed the
  panel and no warning or error line was logged in section 25.
- **The keys reach the viewers.** The Image Viewer asked for `plus, shift+plus, minus, 0, 1` and was granted all five;
  pressing `+`, `1`, `0` reached it as `plus, 1, 0`. The Media Viewer asked for 19 keys and was granted all 19;
  `K J L M` reached it as `k, j, l, m`.
- **Every kind the Media Viewer claims plays in the real panel**: 15 of 15 files, the 13 kinds, `clip-1080p.mp4`
  and `silence.mp3`; `hevc.mp4` also shows (the laptop has an NVIDIA GTX 1660 Ti, which decodes HEVC).
- **TIFF and HEIC** (Windows draws them through the core and the page shows the drawing): 87 ms and 157 ms warm,
  "Drawn by Windows · 1091 × 818" in the panel's bottom line. The HEIF codec is installed on the laptop, so the HEIC
  row ran; on a machine without it the check prints one line and skips it.
The five laptop runs of section 25, all `exit 0` (`ddf9` and `44f2` ran before the later fixes):

| Run | Commit | "quick view goal" | Why |
|---|---|---|---|
| `ddf9` | `7f5dac2` | no | the TIFF and HEIC rows `failed:damaged` (the window fault above); every time was within its goal |
| `44f2` | `473dc8d` | no | two single outliers in nine warm opens: `2-image.png` thumbnail 141 ms, `a-photo.heic` card 94 ms |
| `6270` | `74151d3` | no | one outlier: `2-image.png [fixture]` card 71 ms, thumbnail 96 ms |
| `b449` | `74151d3` | **yes** | no outlier; card worst 17 ms, thumbnail p90 35 ms, JPEG full view 68 ms, video 668 ms warm and 994 ms cold |
| `3358` | `8280510` | no | two single outliers, above |

The outliers are not the viewers' work. In the window's log of runs `6270` and `3358` each of the three outliers sits
on a slow frame with a generation-2 garbage collection of the window (`gc_pause_ms` 51.2, 52.2 and 57; heap 26 to 27 MB,
14 to 15 MB promoted), and the card and thumbnail times ran through the pause; two are on fixture rows and one on
the real JPEG row, and the pause is in the window whatever the page does. (The two of `44f2` were not examined.) With
nine warm values the 90th percentile is the largest value, so one hitch of the window fails the goal, and this section
now has twelve rows, not six; that is the section's statistic from the panel's unit, and I did not change it (see
"For the architect").

## Which kinds play: none was dropped

Before the list was committed, `media-probe.ps1` ran on the laptop (run `0029-9908`, `0534157`; machine
RD-OMEN-LAPTOP, Edge 154.0.4258.53, the WebView2 runtime there is 154.0.4258.53, AMD Radeon and NVIDIA GTX 1660 Ti):
14 of 14 files played, with a picture or sound decoded and the clock moving: H.264 in MP4, M4V, MOV and MKV, VP9 and
Opus in WebM, HEVC in MP4 (hardware), and MP3, M4A, AAC, FLAC, WAV, OGG, Opus and WebA. The same script on the final
silent files (run `0200-d717`, `74151d3`), 16 of 16:

| file | plays | first frame |
|---|---|---|
| clip-1080p.mp4 | yes | 1920x1080, 3.0 s |
| hevc.mp4 | yes | 160x120, 1.5 s |
| sample.mp4, .m4v, .mov, .mkv, .webm | yes | 160x120, 1.5 s |
| sample.aac, .flac, .m4a, .mp3, .ogg, .opus, .wav, .weba, silence.mp3 | yes | no picture, sound decoded |

So the Media Viewer claims all 13 kinds of the handout, and the Image Viewer all 18 (its browser formats are shown
at full resolution, the nine others by Windows). What can still fail is by the file, not by the kind: a codec the
PC lacks (HEVC without the hardware or the Microsoft extension, DTS or ProRes in a `.mkv` or `.mov`). The page then
reports `quickview-failed` (`unsupported` when the error says the source is not supported, else `damaged`) and the
panel keeps the thumbnail and says why. This is in the viewer's README.

## Decided where the handout was open (what - because - undo)

- **The Image Viewer's 100 % is one picture pixel per device pixel (an SVG: one per CSS pixel); fit never enlarges a
  picture past 100 %, and zoom out never goes below the fit** - because a photo viewer's 100 % is true pixels, enlarging
  a small icon only blurs it, and a picture smaller than the panel gains nothing from zooming out - undo: `unit = 1`,
  and the `Math.min(unit, ...)` in `fitScale`.
- **It also asks for `shift+plus`** - because the plus sign on a US keyboard is Shift and the key `=`; the window maps
  `plus` to that key and sends `shift+plus` only when asked - undo: take it out of `KEYS`.
- **Far in (300 % and more) a pixel is drawn as a square; a badge in the corner says the zoom for a second** - small
  aids a photo viewer needs; undo: the `pixelated` class, the badge.
- **A drawn picture says "Drawn by Windows · <w> × <h>", the render's size** - because `quickview-rendered` carries
  only the render's size, not the file's; the true pixel size of a HEIC is unknown to the page. Zoom does not ask for a
  larger drawing, so 100 % means 100 % of the drawing - undo: none needed; see "For the architect".
- **`quickview-render-failed` becomes `quickview-failed` `unsupported`, "Windows has no codec for this file. <the
  window's text>"** - the handout's rule; the text is cut at 200 characters.
- **The Media Viewer reports `quickview-shown` also 2 s after `loadeddata` if no frame callback came** - because a
  paused first frame (autoplay refused) would otherwise leave the panel waiting for its 30 s limit - undo:
  `SHOWN_AFTER_LOADED_MS`.
- **A video file with no picture (sound only in an MP4 or MKV) is shown as audio** - because its `<video>` would be an
  empty box and `requestVideoFrameCallback` never fires - undo: the `videoWidth === 0` branch.
- **Nothing loops; `K` on an ended file plays it again from the start; digits `0` to `9` jump to that tenth** - the
  handout's keys; the replay is mine.
- **The Media Viewer's volume starts at full for each file** - because a fresh page loads for every file and keeps no
  state; undo: none without storage.
- **`build-index.ps1 -Viewers` packs every `sdk/tools` folder with a `quickView` block** (the handout's rule), by folder
  name order, so the Image Viewer comes before the Media Viewer; the long description lists the kinds - because the
  detail view should say what Space will show - undo: drop `$long`.
- **The index schema is unchanged** - `manifest` is free-form there, so no field was missing.
- **The core's offer test changes `minCoreVersion` of the built items to this build's own version** - because the core
  under test is 0.1.2 and the items ask for 0.1.3, the first release with Quick View; the same test first asserts that
  they ask for 0.1.3. The market's test checks that a 0.1.3 core is offered them and a 0.1.2 core none. One consequence:
  a development build of the window says `no_item` for a photo until the core's version is 0.1.3 - undo: none; that is
  the rule.
- **The market's existing build-script test now builds with `-Viewers`** (its first assertion was "the public index is
  empty") - because the public build is `-Collection -ThemesOnly -Viewers` - undo: drop the switch and restore the
  assertion.
- **Live check: `image-viewer` shows `*.png` by its folder name coming first in the development folder (decision 1.2); a
  second pass writes `quickView.viewers {"*.png": "quickview-fixture"}` for the fixture's two PNG rows and takes it out
  again; the window tests' folder `sdk/fixtures/tools` is untouched** - because the setting is the one a user has and
  it exercises the table's change at run time, and one run then measures the fixture and the real viewer on the same
  files - undo: delete the second pass (the fixture then keeps only `*.qvtest`).
- **The media fixtures are silent, 596 KB in all, committed; the 1080p clip is 455 KB, committed (under 2 MB); the HEIC is
  3.9 KB, committed** - because the laptop runs unattended but may have a person at it, and ffmpeg and the HEIC encoder
  are not on the laptop - undo: delete `ui/livecheck/fixtures`, and the rows go.
- **The HEIC row runs only when WPF's decoder opens `photo.heic`, which asks the same codecs the core's drawing uses;
  otherwise one line says it was skipped** - the handout's rule; undo: none.
- **Nothing was installed globally.** ffmpeg was already on this PC (`C:\ffmpeg`). The one HEIC file was made with
  Pillow and pillow-heif in a virtual environment in the scratchpad, outside the repository.
- **`WebViewHost.MapFolder` has an optional `create`, used only for the drawings' folder** - because WebView2 refuses
  a folder that is not there and the core removes this one at its start (the race above) - undo: drop the parameter and
  its two uses; the drawing host then has the race back.
- **An ADR, 0024, records the window change; ADR 0023's status line and the index row say it is amended** - because
  CLAUDE.md says a new decision gets a record and an old one is amended in its status, never rewritten - undo: delete
  the file, the row and the status line with the revert of `473dc8d`.

## Checks

On this PC (`74151d3`; the documents' commit changes no code):

- Core, from `core/`: `cargo build --workspace` 0, `cargo test --workspace` 0 (963 passed, 7 ignored), `cargo clippy
  --workspace --all-targets -- -D warnings` 0, `cargo fmt --all -- --check` 0, `cargo deny check` 0. All five were
  read by their exit codes.
- Window, from `ui/`: `dotnet build CabinetOS.sln -warnaserror` 0 (and `-c Release` 0, which the live check copied to
  the laptop), the fast tests 0: 1512 tests, 1425 passed, 87 need a window and were skipped.
- Laptop, end-to-end tests: `-Filter QuickView` at `7f5dac2`: 60 of 60. `-Filter QuickViewEndToEnd` at `8eeb319`: 6 of 7
  (the new test red, on purpose); at `473dc8d`: 7 of 7. **The whole suite at `74151d3`: 1511 of 1512**, the failure the
  PNG thumbnail test with the error line "WebView2 refused a folder" (the race of `8280510`), and it left a
  `CabinetOS.exe` (pid 12344, started 02:18) on the laptop that locked the next build (MSB3026); I ended it with
  `taskkill /F /IM CabinetOS.exe` while no run held the lock. **The whole suite at `8280510`: 1512 of 1512**
  (build 0, tests 0).
- Laptop, live check: the five runs above. Laptop, media probe: 14 of 14 and 16 of 16.
- Screenshots: `_io\quick-view-viewer-pack-shots` (the video, the audio, the JPEG and the TIFF in the real panel from the
  laptop, and the viewers' pages in headless Edge: fit, 800 %, light theme, video, audio in both themes).

## For the architect (not done, and why)

- **Publishing is the creator's.** The index with the viewers is built into a scratch folder only. The public build is
  `powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <cabinetos-marketplace checkout> -Collection -ThemesOnly -Viewers`,
  then a commit and push in the marketplace repository. The viewers need CabinetOS 0.1.3: until the version is raised
  and released, no window offers them.
- **`quickview-rendered` could carry the file's own size.** The schema says only the drawing's `width` and `height`, so
  the Image Viewer cannot say "4032 × 3024" for a HEIC photo, and zoom stays at the drawing's resolution (at most 2560
  on the longer side, the panel's size). An optional `sourceWidth` and `sourceHeight`, and a larger drawing on zoom,
  would need the schema and `render_image` to change. I did not change the schema.
- **Every alive viewer goes through resume, `about:blank` and suspend on each panel close, not only the one that showed
  the file.** In the log of run `6270` the panel of the fixture viewer closes and `tool-image-viewer` and
  `tool-media-viewer` are resumed and, 230 ms later, suspended. With three viewers alive that is three sets of calls on
  the UI thread per Space; it is one more reason for hitches than the pack of one viewer had. `QuickViewHost.SleepAsync`
  for the viewers that did not show the file is the place.
- **ADR 0023's Context and decision 3 disagreed** about hosts mapped after a load; ADR 0024 settles it for drawings.
  The page contract (rule 7) and the messages are unchanged.
- **A viewer page can now read every drawing in the render cache**, the last 16, not only its own (ADR 0024, Consequences).
- **The window computes the core's render folder from `CABINETOS_CACHE_DIR` and `%LOCALAPPDATA%`**, as the core does;
  if the core's `cache_dir()` changes, `QuickViewHost.RenderRoot` must follow.
- **The window pauses for 50 to 57 ms in a generation-2 garbage collection about twice in 110 opens**, which fails "quick
  view goal" in four runs of five (`ddf9` failed for the fault above). The pause is the window's, not a
  viewer's (heap 26 MB with 15 MB promoted at each pause; I did not chase what allocates: a thumbnail is a base64
  string sent to the page and decoded on every open, which may be over the 85 KB limit of the large object heap, but
  that is a guess). A statistic over more than nine warm opens (for example 20,
  where the 90th percentile tolerates one hitch per row), or the thumbnail through shared memory, which ADR 0023 left
  for when the live check asks, would remove the noise.
- `docs/PLAN.md` and `CONSTITUTION.md` were not touched, as asked.
