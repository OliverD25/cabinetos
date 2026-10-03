# Media Viewer

A Quick View viewer ([ADR 0023](../../../docs/decisions/0023-quick-view-viewer-contract.md)):
Space on a video or a sound file opens the floating panel and the file plays
at once, with the browser's own controls. It opens nothing in a pane
(`accepts` is empty, so Enter still opens a file in its program), and it is
not part of CabinetOS itself (Constitution Article 10): install it from the
marketplace, or copy this folder to `%LOCALAPPDATA%\CabinetOS\tools\media-viewer\`,
or start CabinetOS with `--tools-dir <this repository>\sdk\tools` while
working on it.

## Kinds

| Video | Audio |
|---|---|
| `*.mp4`, `*.m4v`, `*.mov`, `*.webm`, `*.mkv` | `*.mp3`, `*.m4a`, `*.aac`, `*.flac`, `*.wav`, `*.ogg`, `*.opus`, `*.weba` |

- **Video** is a `<video>` fitted to the panel, over the theme's background.
  The panel's bottom line says "0:42 · 1920 × 1080". `quickview-shown` is
  posted at the first presented frame (`requestVideoFrameCallback`; without
  it, after `loadeddata` and two animation frames).
- **Audio** shows a drawn note symbol in the theme's accent colour, the file
  name and an `<audio>` with controls. `quickview-shown` comes when it plays,
  and the bottom line says the length ("3:21"). A video file with no picture
  in it is shown the same way.
- The window allows media to play without a click in a tool's page, so both
  start by themselves, with sound. Nothing loops. The window sends the page to
  `about:blank` when the panel closes, which stops the playback.
- Seeking works on the file's `url` (range requests).

### Which kinds play: none was dropped

The list was checked before it was committed, on the Omen laptop, with
`ui/livecheck/media-probe.ps1` (a headless Edge 154 on a Quick View-like
page, one tiny file per kind from `ui/livecheck/fixtures/media`). All 13
kinds played, picture or sound decoded and the clock moving, with the
laptop's Edge 154.0.4258.53 (the WebView2 runtime there is the same
version). `docs/log/2026-10-04/quick-view-viewer-pack-report.md` has the
table. The live check's Quick View section plays the same files through the
real panel.

What can still fail, by the file and not by its kind: a codec the browser
engine lacks on that PC. The H.264 and AAC decoders are Windows', HEVC needs
the PC's hardware or the Microsoft HEVC extension (it played on the laptop
and on the developer's PC), and a `.mkv` or `.mov` may hold a codec nobody
ships (DTS audio, ProRes video). The window then gets `quickview-failed`
(`unsupported` when the error says the source is not supported, `damaged`
for any other media error) and the panel keeps the thumbnail and says why.

## Keys

The page never has the keyboard. It asks the window for these keys in
`quickview-shown`, and the window grants those no binding wants. **Space is
the panel's and closes it**, so it is never asked for (a video pauses with `K`,
as on the web).

| Key | Does |
|---|---|
| `K` | play or pause (from the end, plays again from the start) |
| `J`, `L` | back and forward 10 s |
| `,` `.` (`comma`, `period`) | back and forward 1 s |
| `M` | mute and unmute |
| `+` (the key `=`, with or without Shift), `-` | volume up and down by 10 % (up also unmutes) |
| `0` to `9` | jump to that tenth of the length (`0` is the start, `5` the middle) |

A small badge in the corner says what a key did ("Paused", "+10 s · 0:52",
"Volume 80 %").

## Files

| File | What |
|---|---|
| `tool.json` | The manifest, with the `quickView` block |
| `index.html` | The page the tool's `entry` names: one line, since it opens nothing in a pane |
| `quickview.html`, `quickview.css`, `quickview.js` | The Quick View page. Its Content-Security-Policy allows scripts and styles from its own host and `img-src` and `media-src` from `https://*.cabinetos.example` and `data:` (rule 6 of the viewer contract) |

A fresh page loads for every file; nothing is kept between files, so the
volume starts at full each time.
