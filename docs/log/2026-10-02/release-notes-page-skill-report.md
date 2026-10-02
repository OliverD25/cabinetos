# The release-notes-page skill and its capture scripts: report

Date: 2026-10-02. Branch `worktree-agent-aee655ead68fb543d`. Task: a project
skill that makes the public "What's new in <version>" page of a CabinetOS
release, and the scripts that take its screenshots and recordings.

## What was built

| File | What it is |
|---|---|
| `.claude/skills/release-notes-page/SKILL.md` | The skill: the contract with the Luminart site (verbatim), the six steps, the demo-folder rule, the media limits, the three hard stops, the traps seen |
| `ui/livecheck/release-media.ps1` | The capture script. Reads a media list, makes `C:\Demo`, starts a fresh window per item, saves a PNG or a raw MP4 |
| `ui/livecheck/release-media.json` | The media list: `hero`, `command-palette` (image), `command-palette` (video, 6 s), `terminal`, `commander-compact`, `marketplace` |
| `ui/livecheck/release-media-convert.py` | Runs on this PC. PNG to WebP 1600 wide, raw MP4 to WebM 1280 wide, with the size limits |
| `CLAUDE.md` | One sentence for the skill in the "Project skills" bullet |

The handout put the scripts in `ui/livecheck/`, so the skill has no `scripts/`
folder of its own (the brief's first wording named one).

Nothing else in the repository changed. `CONSTITUTION.md` is untouched. No
Rust or C# file changed, so the five core checks were not run.

## What the capture script does

- `-CheckOnly` reads and checks the list, opens no window, writes no file.
  It ran on this PC (Windows PowerShell 5.1 and PowerShell 7) and rejects a
  video of 3 seconds, an unknown key name, a bad item name and an unknown step.
- A run makes the demo folder when it is missing: 60 harmless files in
  folders such as Documents, Photos and Projects, and
  `Projects\cabinetos-sample`, a git repository with three commits. It makes
  only what is missing.
- Every item starts a new window with a new configuration under `$env:TEMP`
  and the sidebar hidden, runs its steps, saves one file, and closes the
  window. Before each step it checks that the CabinetOS window is in front.
- A video item records the window's rectangle with ffmpeg (`gdigrab`, 15
  frames a second, no sound) while the steps run.
- Steps: `go`, `tab`, `up`, `down`, `palette`, `type` (with a pause per
  letter), `key` (any key or chord), `enter`, `esc`, `terminal`, `wait`,
  `theme`. The first comment of the script lists them with their fields.

## The dry run

The window needs the laptop. Before each run the laptop's tasks and
`CabinetOS.exe` were checked: both numbers were 0.

Command (from the worktree, in Windows PowerShell; the second and final run):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-script.ps1 -Script "ui\livecheck\release-media.ps1" -Args "-OutDir C:\Dev\cabinetos\_io\script-runs\release-media" -Branch worktree-agent-aee655ead68fb543d
```

The laptop ran it at 23:11:07 to 23:13:36 (2 minutes 29 seconds), exit 0. The
end of its output:

```
--- files in C:\Dev\cabinetos\_io\script-runs\release-media:
file C:\Dev\cabinetos\_io\script-runs\release-media\hero.png  278 KB
file C:\Dev\cabinetos\_io\script-runs\release-media\command-palette.png  456 KB
file C:\Dev\cabinetos\_io\script-runs\release-media\command-palette.raw.mp4  1,062 KB  6.0 s 1920x1020
file C:\Dev\cabinetos\_io\script-runs\release-media\terminal.png  208 KB
file C:\Dev\cabinetos\_io\script-runs\release-media\commander-compact.png  178 KB
file C:\Dev\cabinetos\_io\script-runs\release-media\marketplace.png  320 KB
done: 6 file(s) in C:\Dev\cabinetos\_io\script-runs\release-media
```

The full output is in `_io\script-runs\script-2026-10-02-2311-rd-omen-laptop.txt`.
The first run (23:06) had a thinner demo folder (eight rows in a pane), so the
demo folder was filled out and the recording's keys now start 0.7 s after
ffmpeg instead of 1.5 s; then the run above was made.

Conversion, on this PC:

```
python ui\livecheck\release-media-convert.py --in <dry-run>\raw --out <dry-run>\media
image  command-palette.webp  1600x850  quality 85  67 KB
image  commander-compact.webp  1600x850  quality 85  51 KB
image  hero.webp  1600x850  quality 85  63 KB
image  marketplace.webp  1600x850  quality 85  103 KB
image  terminal.webp  1600x850  quality 85  47 KB
video  command-palette.webm  1280x680  6.0 s  crf 33  113 KB
done: 5 image(s), 1 video(s) into ...\_io\release-notes-dry-run\media, 443 KB in all
```

Every file is under its limit (300 KB for an image, 3 MB for a video). No
warning was printed.

The files are in `E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\release-notes-dry-run\`:
`raw\` (the six raw files and a `command-palette.ffmpeg.log`), `media\` (the six
converted files) and `notes.md`.

What was looked at (with the Read tool): the five PNGs of the first run, all
five converted WebP files of the final run, three frames of the first raw
recording, and two frames of the final WebM (at 0.2 s the panes, at 3.5 s the
palette with "copy" typed). Seen: the title says
"CabinetOS . Demo" (the real title has a middle dot), every path starts with
`C:\Demo`, the terminal prompt says `PS C:\Demo\Projects\cabinetos-sample>`,
there is no sidebar, and the only drive text is the chip `C:` with "229 GB
free". No user name, no real drive label, no network share. The marketplace
page shows the theme cards of the real index, with no personal data.

The renderer of the site accepted the dry run's `notes.md`: it was built in a
temporary copy of the site's build script and `marked` (not in the site
repository), which printed `Built 1 release page(s): 0.1.0.`. The page was
served from that copy on `localhost` and looked at at 1440 and 375 pixels
wide (top of the page, then the full page at 1440). It renders: the heading,
the summary, the download button, the highlights, the "On this page" list.
The server was stopped.

## What could not be done, or did not work

- **Recording by window title gives only black.** The handout asked for
  `gdigrab` by title. On the laptop it recorded 5.9 of 6 seconds of black (a
  file of 8.7 KB), because WinUI draws on the GPU. The script's default is
  therefore `-Capture screen`: the rectangle of the window's frame, from the
  desktop. It records the real picture at 1920x1020. The title mode stays
  as an option, with a warning in the script and in the skill. The window's
  Win32 title is just "CabinetOS", not "CabinetOS . Demo".
- **No sidebar in any shot.** See the decisions below.
- **The final script differs from the one that ran.** After the last run, only
  comments and the text of one error message changed (the message now says
  that a tiny recording is a black one). The file parses in both PowerShell
  versions. It was not run again.
- **Not shown in the dry run:** the column view, the compact overlay, the
  copy queue and other features of `[Unreleased]` have no item. The handout
  fixed the dry run's list to five pictures and one video.

## Decided without the creator

One line per decision, with the undo.

1. **Sidebar hidden in all shots** (`ui.sidebar: false` in the fresh
   configuration, not a key press). The four pinned rows (Desktop, Downloads,
   Documents, and the user's folder named after the Windows user, here
   `Omen`) are fixed in `MainWindow.xaml.cs` (`SetPinnedFolders`), so a
   setting cannot unpin the user folder. Undo: remove `"ui":{"sidebar":false}`
   in `Start-App` of `release-media.ps1`, but then the user's name shows.
2. **Capture mode `screen` is the default, not `title`.** See above. Undo:
   `-Capture title`.
3. **A fresh window for each item**, not one window for the list. Costs about 15
   seconds per item, but no state (theme, terminal, view) leaks. Undo:
   rewrite `Invoke-MediaItem`.
4. **The list file is an object with `left`, `right` and `items`**; a bare array
   of items is also accepted.
5. **`hero` replaces `dual-pane`** in the list, and `command-palette` exists
   twice (an image and a video), on the coordinator's message.
6. **The hero's caption does not mention a sidebar.** The caption in the dry
   run's `notes.md` is "Two panes side by side over a demo folder, each with
   its tabs, toolbar and path row". The old caption says "the sidebar with
   pinned folders and drives". It must change before the new picture is used.
7. **ffmpeg was copied to the laptop**: `C:\Dev\tools\ffmpeg\ffmpeg.exe`
   (148,636,160 bytes, the same file as this PC's `C:\ffmpeg\bin\ffmpeg.exe`),
   with `scp`. Undo: `Remove-Item C:\Dev\tools\ffmpeg -Recurse` over SSH.
8. **`C:\Demo` was created on the laptop** by the script (60 files, 18.3 MB,
   plus the sample git repository). Undo: `Remove-Item C:\Demo -Recurse` over
   SSH.
9. **The laptop's clone was put back on `main`** after the runs
   (`remote-script.ps1` had checked out my branch there). It was clean.
10. **A `Fixed` section is optional in the skill**, only for fixes of something an
    earlier release had. The brief names only Added, Changed and Removed.
11. **First-release rule in the skill:** for a first release, "Changed" and
    "Fixed" bullets that describe builds nobody had are folded into the
    features or left out. The dry run's `notes.md` follows it and lists what was
    folded or left out in an HTML comment at the top of the body.
12. **The dry run's `notes.md` uses the video** `command-palette.webm` on the
    page, and does not use `command-palette.webp` (it is the still of the
    same view, if wanted).
13. **The date in the dry run's front matter is 2026-10-02**, the day of the
    draft. The release day replaces it.
14. **The converter lowers the WebP quality in steps** (85, 78, 70, 62) when an
    image is over 300 KB, and raises the VP9 crf from 33 to 40 when a video
    is over 3 MB. It warns when a file is still over.
15. **The skill's commands that touch the laptop or the site are PowerShell**
    blocks, each with its reason: the SSH key and `remote-script.ps1` are on
    the Windows side, and the site's `node_modules` holds the Windows build
    of wrangler.

## Needs the creator

- **Publishing.** Nothing was deployed, committed or pushed in the Luminart
  repository, and no GitHub release was made. To publish 0.1.0 (the
  planning session's steps): check the GitHub release exists; copy the dry
  run's `media\` files and a reviewed `notes.md` into
  `sites/cabinetos/releases/0.1.0/`; build and look; deploy; verify; commit
  and push. See the skill, steps 3 to 5.
- **The old media in the site repository show a personal name.** The five
  `.webp` files that exist in `sites/cabinetos/releases/0.1.0/media/` (commit
  `8e5d4ea`) came from the earlier `showcase-shots.ps1` run. The hero shows
  the pinned folder `Omen` (the laptop user) and the repository's real paths.
  The handout said that folder was empty; it is not. They must be replaced by
  the files in the dry run's `media\` before publishing. The site's own
  `notes.md` of 0.1.0 also changed since the handout (commit `07f6fa7`, an
  Undo feature, date 2026-10-02); the dry run's `notes.md` is made from the
  changelog alone and does not include it.
- **Consent for runs on this PC** is not needed: every window run was on the
  laptop. If the creator ever wants the capture on this PC, the rule of
  `CLAUDE.md` applies.

## Noticed, out of scope

- `ui/livecheck/showcase-shots.ps1` still shows the user's folder name in its
  shots (sidebar). It could say so in its header, or hide the sidebar too.
- `docs/release.md` does not mention the skill. A line in "Publish" could point
  to it, so a release cut also makes the page.
- The Playwright tool wrote four files into the main checkout's untracked
  folder `cabinetos\.playwright-mcp\` (screenshots of the dry run's page). They
  are mine; the task did not allow deleting outside the worktree. They show in
  `git status` of the main checkout as `?? .playwright-mcp/`.
- The laptop keeps a test folder `C:\Dev\cabinetos\_io\script-runs\release-media-title-test`
  (the black title-mode recording and one PNG), and the output folder
  `...\release-media` of the dry run.
