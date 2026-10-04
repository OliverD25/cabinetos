# Image Viewer

A Quick View viewer ([ADR 0023](../../../docs/decisions/0023-quick-view-viewer-contract.md)):
Space on a picture shows it in the floating panel, centred and fitted to the
panel, over the theme's background. It opens nothing in a pane (`accepts` is
empty, so Enter still opens a picture in its program), and it is not part of
CabinetOS itself (Constitution Article 10): install it from the marketplace,
or copy this folder to `%LOCALAPPDATA%\CabinetOS\tools\image-viewer\`, or start
CabinetOS with `--tools-dir <this repository>\sdk\tools` while working on it.

## Kinds

| Read from the file at full resolution (the browser decodes them) | Drawn by Windows (`quickview-render`) |
|---|---|
| `*.jpg`, `*.jpeg`, `*.png`, `*.gif`, `*.webp`, `*.avif`, `*.bmp`, `*.ico`, `*.svg` | `*.heic`, `*.heif`, `*.tif`, `*.tiff`, `*.jxr`, `*.dng`, `*.cr2`, `*.nef`, `*.arw` |

- A GIF animates (an `<img>` does it by itself). An SVG is shown as an
  `<img>` too, so no script inside it can run.
- **The render path.** WebView2 cannot decode HEIC, TIFF, JPEG XR or camera
  RAW. For these the page posts `quickview-render` with the panel's size in
  device pixels (`panel.width × panel.scale`, scaled down so the longer side
  is at most 2560). The window has the core draw the file with the shell's
  image factory, which uses the codecs Windows has, and answers with the
  `url` of a PNG; the page shows that. The bottom line says "Drawn by
  Windows" with the render's size. When Windows has no codec for the
  format (the HEIF Image Extensions and the RAW Image Extension of the Microsoft
  Store are not installed, for instance) the window answers
  `quickview-render-failed` and the page reports `quickview-failed`
  (`unsupported`); the panel keeps the thumbnail and says why.
- **What it cannot show.** A file over 256 MB (`too-large`, before any load).
  A file the browser cannot decode (a damaged file, or a format variant the
  browser lacks) is `damaged`. A picture drawn by Windows is only as sharp as
  the render: zooming in does not ask for a larger one, so 100 % there means
  100 % of the render, not of the file.

## Keys

The page never has the keyboard. It asks the window for these keys in
`quickview-shown`, and the window grants those no binding wants:

| Key | Does |
|---|---|
| `+` (the key `=`, with or without Shift) | zoom in one step of 25 %, up to 800 % |
| `-` | zoom out one step of 25 %, never below the fit |
| `0` | fit the picture to the panel again |
| `1` | 100 %: one picture pixel per screen pixel (an SVG: one per CSS pixel) |

The mouse wheel zooms around the pointer, and a drag moves a zoomed picture.
A small badge in the corner says the zoom for a moment. Far in (300 % and
more) a pixel is drawn as a square. A picture smaller than the panel is not
enlarged: fit means 100 % for it.

The panel's own keys stay as they are: Space and Esc close it, Up and Down
walk the folder, Enter opens the file in its program.

## Files

| File | What |
|---|---|
| `tool.json` | The manifest, with the `quickView` block |
| `index.html` | The page the tool's `entry` names: one line, since it opens nothing in a pane |
| `quickview.html`, `quickview.css`, `quickview.js` | The Quick View page. Its Content-Security-Policy allows scripts and styles from its own host and `img-src` and `media-src` from `https://*.cabinetos.example` and `data:` (rule 6 of the viewer contract) |

A fresh page loads for every file; nothing is kept between files.
