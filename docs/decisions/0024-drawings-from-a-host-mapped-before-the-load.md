# ADR 0024: A drawing for a Quick View page is served from a host mapped before the page loads

- Status: accepted (proposed by the coder of the viewer pack, step 3 of Phase 25, while the creator was away; it
  stands until the creator says otherwise)
- Date: 2026-10-04
- Decided by: the coder of the viewer pack, as the smallest change that makes decision 3 of
  [ADR 0023](0023-quick-view-viewer-contract.md) work. It amends the way decision 3 serves a drawing; the messages
  between the window and a page are not changed.

## Context

Decision 3 of ADR 0023 says a page that cannot decode a file posts `quickview-render`, the window has the core draw
it (`render_image`), maps the folder the core wrote it to "on a new host `r<n>.<id>.cabinetos.example`" and answers
`quickview-rendered` with a `url` on that host. The window did exactly that: it mapped the host when the drawing was
ready, after the viewer's page had loaded.

That did not work. The Image Viewer's TIFF and HEIC rows in the live check on the Omen laptop (2026-10-04) ended with
`failed:damaged`: the core had drawn the picture (a valid PNG, 22 ms), the window had posted `quickview-rendered`,
and the page's request for the `url` failed. WebView2's own documentation for `SetVirtualHostNameToFolderMapping`
says why: "the resource loaders for the current page might have already been created and running, changes to the
mapping might not be applied to the current page and a reload of the page is needed". ADR 0023's Context says the
same of the file's host ("a page that is already loaded cannot fetch from a host mapped after it loaded") and maps
that one before each load, but decision 3 did not apply the rule to the drawing's host. Nothing covered it: the
fixture viewer never asks for a drawing, and the core's test stops at the file the core writes. An end-to-end test
with the real Image Viewer on a TIFF (`A_picture_the_browser_cannot_decode_is_drawn_by_Windows_and_shown_by_the_Image_Viewer`)
failed before the change and passes after it.

## Decision

The window maps the core's folder of drawings, `cache\render` (cabinetos-core's `cache_dir()`: `CABINETOS_CACHE_DIR`,
else `%LOCALAPPDATA%\CabinetOS\cache`), read-only on the host `r<n>.<id>.cabinetos.example` before each page load,
together with the file's host `f<n>`. `n` is the serial of the load, so it changes with every file, as `f<n>` does.
A drawing is `image.png` in its own folder under that host, and `quickview-rendered` carries
`https://r<n>.<id>.cabinetos.example/<drawing's folder name>/image.png`, which the page uses as it used the other
url. A drawing in another folder (a core started with another cache folder than the window computes) is mapped when
it is ready, as before.

## Consequences

- A page's TIFF, HEIC, JPEG XR and camera RAW now work. The messages are as ADR 0023 wrote them.
- A viewer page can read every drawing in the render cache (the last 16, of files the user opened in Quick View),
  not only its own. It can already read every file in the shown file's folder, and it has no network, so this adds
  no new kind of exposure; the core's cache keeps only 16 drawings and empties at its start.
- The window knows where the core keeps its drawings, from the same environment the core reads. If the core's
  `cache_dir()` changes, `QuickViewHost.RenderRoot` must follow; the fallback keeps a drawing elsewhere working in
  the case WebView2 allows.
- The window creates the render folder each time it sets the mapping (before a load, and when a viewer's WebView2
  starts and sets its mappings again), because WebView2 refuses a folder that does not exist ("The system cannot
  find the path specified") and the core removes the folder once, at its start, which can come between the load
  and the start. The first version of the change created it only before the load, and the whole end-to-end suite
  on the laptop found the race: "WebView2 refused a folder" in a test that never asks for a drawing. A mapping holds
  the path, not the folder, so the core making it again at a drawing needs nothing.