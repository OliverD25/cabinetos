# Release media for Quick View: two items for the 0.1.3 notes page

Coder report, Sonnet 5.5, 2026-10-04. Branch `worktree-agent-a7e7b99a52ad4b1ee`, from main's `09e5695`. The creator was
away. Nothing opened a window on this PC; the one window run went to the Omen laptop (`remote-script.ps1`, run
`2026-10-04-0354-c820`, which ran the exact commit `1f12e03`). Files of the run are in
`_io/release-media-qv/` (not in the repo).

## What was built

| Commit | What |
|---|---|
| `42619ee` | `New-DemoFolder` makes `Pictures\Lake-at-dawn.jpg` (2400 x 1600, drawn with System.Drawing: sky, sun, two mountain ranges, a lake; fixed seed; no text) and copies the live check's clip `fixtures/media/clip-1080p.mp4` to `Pictures\Harbour-clip.mp4`; each only when missing |
| `e66790d` | `-Viewers` switch (copies `image-viewer` and `media-viewer` into `viewers` in the run's work folder, starts every window with `--tools-dir`), and an optional `left` and `right` on an item |
| `fdbd328`, `1f12e03` | the items `quick-view` (image) and `quick-view-video` (video, 6 s) in `release-media.json` (the second commit fixes a path escaped twice) |

## Result on the laptop

- `quick-view.png` (1920 x 1020): the Image Viewer's full view of the drawn photo in the panel, cursor on
  `Lake-at-dawn.jpg`, footer "146 KB, 2400 x 1600". Not a grey card, not the thumbnail.
- `quick-view-video.raw.mp4` (6.0 s, 1920 x 1020, 2.9 MB): the Media Viewer playing the clip (its clock and the picture
  move between 3 s and 5.3 s). `-Capture screen` (the default) is not black. Frames read at 3 s and 5.3 s.
- Look of the clip: the committed fixture is a colour-bar test card with a burnt-in time code, so the recording is
  correct but not pretty. The clip is only 3 s long, so it has ended or looped by the end of the 6 s.

## Decisions

- `-Viewers` is for the whole run, not per item - a per-item flag would need a new key in the list and the viewers
  change nothing in a window that never presses Space - undo: drop the switch from the `-Args`, nothing else changes.
- The two items get their folder from a new optional per-item `left` (and `right`), not a `go` step - the handout
  preferred it, and the pane then starts in `Pictures` with no extra Ctrl+L round - undo: put a `go` step first and
  delete the key from the two items.
- The window gets the viewers with `--tools-dir "<folder>"`, not the environment variable - the handout named the flag
  - undo: set `CABINETOS_TOOLS_DIR` in `Start-App` instead.
- The photo is drawn at run time, not committed - the handout said so, and it keeps a binary out of the repo - undo:
  commit a JPEG and copy it as the clip is copied.
- The cursor is moved with `down 1` for the photo and not at all for the clip - the pane starts on its first row and
  names sort `Harbour-clip.mp4`, `Lake-at-dawn.jpg` - undo: none needed; a renamed file changes the count.
- The new `Pictures` folder shows in the root listing of every other shot taken on a new demo folder - it sorts after
  `Photos`, so the hero's `down 5` still lands on `Photos` - undo: remove the two calls in `New-DemoFolder`.
- The laptop's `C:\Demo` already existed, so this run added only `Pictures` there. The script never overwrites, so a
  new clip needs the laptop's `C:\Demo\Pictures\Harbour-clip.mp4` deleted first.

## Not done, and a suggestion

- A nicer clip. A 4 s pan over the lake photo made with ffmpeg would suit a public page better than the test card. It
  needs ffmpeg in `New-DemoFolder` (the laptop has it) or a committed clip; the handout said to copy the fixture, so
  that was not done. The creator decides.
- `remote-script.ps1` printed "exit code of the script: 1" although the script ended with its `done:` line and the
  two files exist. Not chased.
