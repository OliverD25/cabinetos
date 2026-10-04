---
name: release-notes-page
description: How the public "What's new in <version>" page of a CabinetOS release is made and published on the Luminart company site (https://cabinetos.luminart.app/releases/<version>/). The changelog becomes notes.md, a script takes clean screenshots and recordings of the real window over a demo folder, and the page is built and checked on this PC before anything goes live. Use it when the creator says "release notes page", "what's new page", "publish the notes for 0.2.0", "update the release page", "take the release screenshots", "make the media for the release", "write the notes for the release", or when a release is being cut and its page does not exist yet. Publishing needs the creator's word in the chat; the skill never deploys on its own.
---

# The release notes page of a CabinetOS version

Every CabinetOS release has one public page, "What's new in 0.2.0", in the
style of the VS Code release notes. The page is grouped by feature area. It
has one short paragraph per feature, and a picture or a short looping video
for every feature that can be seen. The creator asked for it on 2026-10-02.

The page is part of the Luminart company site, not of this repository. This
skill makes the two source parts of the page, `notes.md` and `media/`, and
brings them live. The site's own build turns them into HTML.

## 1. The three hard stops

These never bend, in any mode, also when the creator is away.

1. **No deploy without the creator's word in the chat.** Step 5 is the only
   step that changes the public site. Only a clear request ("publish the
   notes for 0.2.0") starts it. A request to write the notes, or to finish
   the release, is not that request.
2. **Nothing overwrites an earlier release's folder without saying so.**
   Before step 3 writes into `sites/cabinetos/releases/<version>/`, look
   whether the folder exists. If it does, tell the creator what is in it and
   that it will be replaced, and wait for a yes. The folder of any other
   version is never touched.
3. **Never a screenshot or a recording that shows personal data.** That means
   a real user folder name, the drive label of a real machine, a network
   share, an e-mail address or the name of a private file. Section 4 says how
   the capture script avoids it. Look at every file yourself before it goes
   into the site's folder (step 2).

The project rules apply too. **No CabinetOS window opens on this PC without
the creator's consent** (`CLAUDE.md`, rule of 2026-10-02). The media are
taken on the Omen laptop. No outside action happens without the creator
(no GitHub release, no push in the Luminart repository, no message).

## 2. The contract with the Luminart site

This part is fixed by the other repository. Do not invent another format.

The site repository is `E:\codespace\_claude_code\_rde\luminart.app\luminart.app`
(WSL: `/mnt/e/codespace/_claude_code/_rde/luminart.app/luminart.app`), GitHub
`OliverD25/luminart.app`. It already builds and serves
`https://cabinetos.luminart.app/releases/<version>/` from one source folder
per release:

```
sites/cabinetos/releases/<version>/notes.md
sites/cabinetos/releases/<version>/media/   images and short videos
```

`notes.md` starts with front matter, then markdown:

```
---
version: 0.2.0
date: 2026-10-20
summary: One sentence that says what this release is about.
download: CabinetOS-0.2.0-win-x64-setup.exe
highlights:
  - Three to six short lines, one feature each
  - ...
---

## Panes and navigation

### Column view

One or two sentences in plain English. What it does, which key opens it.

![The column view, three folders deep](media/column-view.webm)
```

Rules of the contract: the keys `version`, `date` (YYYY-MM-DD), `summary`,
`download` (the exact release asset file name) and `highlights` are
required. `##` is a feature area, `###` is one feature. A media line
`![caption](media/<file>)` becomes a framed figure with the caption; `.webm`
and `.mp4` play as silent looping videos, `.png`, `.webp`, `.gif` and `.jpg`
show as images. The build in that repo (`npm run build:releases`) renders the
HTML, the release list at `/releases/` and the sitemap; `README.md` there,
section "Release notes", is the reference. The page's download button links
to `https://github.com/OliverD25/cabinetos/releases/download/v<version>/<download>`,
so the GitHub release with that file must exist before the page goes live.

What the build checks, learned from `scripts/build-releases.mjs` (read it
when in doubt): the file must start with the `---` line. `version` must be
`MAJOR.MINOR.PATCH` and equal the folder name. `highlights` needs 3 to 6
lines. A media line stands alone, with an empty line before and after, and
its file must exist in `media/`. The first `.png`, `.webp` or `.jpg` of the
notes is the page's link preview image. Only the keys above and the optional
`title` are allowed in the front matter.

## 3. The steps

### Step 1. Collect what changed

Read the version's section of `CHANGELOG.md`. At release time the
`[Unreleased]` section becomes the version's section
([docs/release.md](../../../docs/release.md), "Publish", step (b)), so
before the release is cut, read `[Unreleased]`.

- **Added.** Every bullet becomes one `###` feature under a `##` area. Reuse
  the areas of the earlier pages when they fit: Panes and navigation, File
  operations, Search, Commands and keys, Terminal, Themes and the settings
  file, Plugins tools and the marketplace, Install updates and diagnostics.
  Make a new area only when none fits.
- **Wording.** Keep the changelog's words. No marketing words ("powerful",
  "seamless", "blazing"). A long bullet may be cut to its first sentences,
  never reworded. The `###` title is a short name for the bullet's subject.
- **Changed and Removed.** They become a last section, `## Changed and
  removed`, with a `**Changed**` list and a `**Removed**` list.
- **Fixed.** Not named in the creator's brief. The rule used so far: a last
  section `## Fixed` after it, one bullet per fix, only for fixes of
  something the earlier release had. A fix of a feature that never shipped
  is left out.
- **The first release has no earlier public version.** Its Changed, Removed
  and Fixed bullets that describe builds nobody had are folded into the
  feature text or left out. Keep only what a Total Commander user needs to
  know, such as a key that moved. Say in an HTML comment at the top of the
  page what was folded or left out, and remove the comment before the page
  goes live.
- **Highlights.** Three to six short lines, one feature each, the ones a new
  user cares about most. No full stop at the end.
- **The front matter.** `summary` is one sentence. `date` is the date of the
  changelog section. `download` is the release asset's exact file name,
  `CabinetOS-<version>-win-x64-setup.exe`
  ([docs/release.md](../../../docs/release.md), "The setup file").
- **Media.** A feature that can be seen gets a media line, and one item in
  the media list of step 2. A feature that cannot be seen (a setting, a
  command-line tool) gets none.

Write the draft into the work folder in `_io`
(`_io\release-notes-<version>\notes.md`), not yet into the site. Use the
Write tool for text. Never put markdown in a shell-quoted string: an
apostrophe or a backtick ends the quote and the rest runs as commands (it
happened twice on 2026-10-02).

### Step 2. Take the media on a clean profile

Two scripts do it, both in `ui/livecheck/`:

- `release-media.ps1` reads a media list (`release-media.json`), makes the
  demo folder `C:\Demo` when it is missing, starts the real window for each
  item, presses the keys of the item, and saves a PNG or a raw MP4. Its first
  comment lists every step an item can use.
- `release-media-convert.py` runs on this PC. It turns every PNG into a
  WebP 1600 pixels wide and every raw MP4 into a WebM 1280 pixels wide, and
  warns when a file is over its limit (section 5).

**a. The list.** Edit `ui/livecheck/release-media.json`. One item per
picture or recording: `name` is the file's base name (`column-view`), `kind`
is `image` or `video`, `seconds` (5 to 12) is for a video, and `steps` are
the keys that bring the window to what must be shown. Optional on an item:
`settleMs` (the wait before an image, default 1500) and `left` and `right` (the
folders the item starts with, in place of the list's own; `quick-view` uses
`C:\Demo\Pictures`, where the demo folder keeps a drawn photo and a clip).
A feature that needs a viewer or another installed tool needs `-Viewers` on the
run (below): it installs the two Quick View viewers of `sdk/tools` in every
window of the run. Name every file after its feature. The screenshots are taken again for every release, because the
look of the window changes. Add an item for each new feature that can be
seen. The file in the repository is the working example: the item
`command-palette` with `kind` `video` types "copy" letter by letter.

Check the list without opening a window:

```powershell
# PowerShell (the script is a PowerShell script)
cd "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos"
powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\release-media.ps1 -CheckOnly
```

**b. Use the build of the release.** The window on the laptop is the build in
its clone, `C:\Dev\cabinetos\cabinetos`. The media must show the release,
so build the release on this PC first ([docs/release.md](../../../docs/release.md),
"Build") and add `-CopyBuilds` to the run below, or check on the laptop that
its Release build is of the release's commit.

**c. Run it on the laptop.** The laptop may be in use by another session.
Check first. Both numbers must be 0. If they are not, wait and retry every 5
minutes, up to 60 minutes, then stop and say so. A dead `CabinetOS.exe` left
by a crashed run ends with `taskkill /F /IM CabinetOS.exe` over SSH.

```powershell
# PowerShell (the SSH key and remote-script.ps1 are on the Windows side)
cd "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos"
ssh -F "$env:USERPROFILE\.ssh\config" -o BatchMode=yes omen "(Get-ScheduledTask -TaskName CabinetOS-LiveCheck,CabinetOS-Tests,CabinetOS-Script | Where-Object State -eq 'Running' | Measure-Object).Count; (tasklist | findstr /i CabinetOS.exe | Measure-Object).Count"
```

`remote-script.ps1` sends the commits of a branch to the laptop. So commit
the changed list first, and name the branch (the default is `main`):

```powershell
# PowerShell
cd "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos"
powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-script.ps1 -Script "ui\livecheck\release-media.ps1" -Args "-OutDir C:\Dev\cabinetos\_io\script-runs\release-media" -Branch main
```

For Quick View add `-Viewers` to the `-Args` (for example `-Viewers -Only
quick-view,quick-view-video`); without it no viewer is installed in the windows.
A run of six items takes about 2.5 minutes: each item starts a fresh
window. `-Args "... -Only terminal,marketplace"` runs only those items, for a
second try. The output text comes back to the project's `_io\script-runs\`
and ends with one line per file and its size. The script prints `FAILED:`
and exits with 1 when an item failed.

**d. Fetch and convert.**

```powershell
# PowerShell (the SSH key is on the Windows side; Pillow and ffmpeg are installed here)
$io = "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\_io\release-notes-<version>"
New-Item -ItemType Directory -Force "$io\raw", "$io\media" | Out-Null
scp -r -F "$env:USERPROFILE\.ssh\config" -o BatchMode=yes "omen:C:/Dev/cabinetos/_io/script-runs/release-media/*" "$io\raw\"
cd "E:\codespace\_claude_code\_rde\_cabinetos_windows_system_manager\cabinetos"
python ui\livecheck\release-media-convert.py --in "$io\raw" --out "$io\media"
```

**e. Look at every file.** Read each `.webp` with the Read tool, which shows
images. For each `.webm`, take a frame near the start, the middle and the end
(`ffmpeg -ss 3 -i <file>.webm -frames:v 1 <frame>.png`) and read those. Check
that the picture is not black, shows what the caption says, and shows no
personal data (hard stop 3). A recording is black when the converter says
"looks black". Fix the list and run again; never publish a file you have not
looked at.

The raw files stay in `_io`. They are not committed anywhere.

### Step 3. Write the release folder

Check hard stop 2 first: does `sites/cabinetos/releases/<version>/` exist?
Then copy the checked `notes.md` and the `media` files into that folder, in
the Luminart site repository. Use the Write tool for `notes.md`. Copy only
the media files that `notes.md` uses. Do not commit in that repository yet.

These files are the **only copy** of the page. The CabinetOS repository keeps
no mirror of `notes.md` or the media: the changelog is the source of the text,
and the raw media can be taken again.

### Step 4. Build and look

Run in the site repository, from the Windows side (its `node_modules` holds
the Windows build of wrangler):

```powershell
# PowerShell (the site's node_modules is the Windows build)
cd "E:\codespace\_claude_code\_rde\luminart.app\luminart.app"
npm run build:releases
npm run dev:cabinetos
```

`npm run dev:cabinetos` serves `http://localhost:8788/`. Use `localhost`,
never `127.0.0.1`. Port 8080 is forbidden on this PC (a video program holds
it). Open `/releases/<version>/`. Take a screenshot of the page at 1440 and at
375 pixels wide and read both back before calling the page done. The global
skill `local-site-screenshot` explains the three ways to take it. Look for: a
broken figure, a video that does not play, a caption that does not match the
picture, a heading that is missing from "On this page".

Stopping `wrangler dev` can leave `workerd.exe` holding the port. Find it
with `netstat -ano | findstr :8788` and end it with `taskkill /PID <pid> /F`.

### Step 5. Publish, only when the creator asks for it in the chat

Hard stop 1 applies. Load the global `luminart-publish` skill before the first
deploy of a session: it holds the account facts and the pitfalls.

1. The GitHub release with the setup file must exist first, because the
   page's download button points at it:

   ```bash
   gh release view v<version> --repo OliverD25/cabinetos
   ```

   If it does not exist, stop and tell the creator. Do not make it here.
2. In the site repository, from the Windows side (the Cloudflare login is
   there, not in WSL): `npm run deploy:cabinetos`.
3. Then `npm run verify`. Every line must say `PASS`.
4. Then commit and push that repository, with a message that says why.
5. Open `https://cabinetos.luminart.app/releases/<version>/` once in a
   browser, and the list at `/releases/`.

### Step 6. Report in the chat

The page's URL. The list of media with their sizes. What could not be
captured, and why. The output of `npm run verify` when step 5 ran. If step 5
did not run, say what is left: the GitHub release, and the creator's word.

## 4. Why a demo folder, and the "no personal names" rule

The public page must not show anything personal. The first screenshots of the
product page (`showcase-shots.ps1`, 2026-10-02) ran over the repository on the
laptop. They show the folder `Omen`, the name of the laptop's Windows user, in
the sidebar, and the screenshots in `docs/log` show the creator's real
drives. None of them could be used for a public page. So the capture script:

- shows only the demo folder `C:\Demo` (default; `-Demo` names another). The
  script makes it when it is missing: folders such as Documents, Photos and
  Projects with harmless files, and `Projects\cabinetos-sample`, a tiny git
  repository with three commits, so the terminal can run `git log`. It makes
  only what is missing and never changes or deletes what is there.
- starts the window with a fresh configuration under `$env:TEMP`, so the
  laptop's own settings and history do not show.
- hides the sidebar (`ui.sidebar` is `false`). The four "pinned" rows of the
  sidebar are fixed in the window's code: Desktop, Downloads, Documents and
  the user's own folder, named after the Windows user. No setting removes
  that last row. The consequence: the shots show no sidebar, and a caption
  must not mention one.
- starts a new window for every item, so no theme, terminal or view of one
  item reaches the next.

Check that the demo folder is still harmless before a run: it is a real
folder on the laptop, and anyone may have put a file in it.

## 5. Limits for the media

| Kind | Format | Limit |
|---|---|---|
| Image | WebP, 1600 pixels wide | about 300 KB at most |
| Video | WebM (VP9), 1280 pixels wide, no sound | under 3 MB, 5 to 12 seconds |
| GIF | only as a last resort, when a WebM cannot be made | |

The converter enforces them: it tries lower WebP quality when an image is too
big, a higher compression when a video is too big, and warns when a file is
still over. A short video of a window is small (a 6 second clip of the command
palette is about 110 KB), so the limits seldom bind.

## 6. Where things are

| What | Where |
|---|---|
| The capture script, with the list of steps | `ui/livecheck/release-media.ps1` |
| The media list (the working example) | `ui/livecheck/release-media.json` |
| The converter | `ui/livecheck/release-media-convert.py` |
| Running a script on the laptop | `ui/livecheck/remote-script.ps1` |
| Raw and converted media, the draft notes | `_io\release-notes-<version>\` (outside git) |
| The 0.1.0 dry run: a draft `notes.md`, the raw files and the converted files | `_io\release-notes-dry-run\` (outside git) |
| The page's files, the only copy | the site repository, `sites/cabinetos/releases/<version>/` |
| The build that renders the page | the site repository, `scripts/build-releases.mjs` |

## 7. Traps seen

- **The laptop is busy.** Check its tasks and processes before every run
  (step 2c). Another session may be using it.
- **ffmpeg on the laptop.** It lives at `C:\Dev\tools\ffmpeg\ffmpeg.exe`,
  copied from this PC's `C:\ffmpeg\bin\ffmpeg.exe` on 2026-10-02. If it is
  gone, copy it again with `scp`. The script falls back to that path when
  `C:\ffmpeg\bin\ffmpeg.exe` is missing.
- **A black recording.** `-Capture title` (ffmpeg's record-by-window-title)
  records only black for the CabinetOS window, because WinUI draws on the
  GPU. The script's default, `-Capture screen`, records the window's
  rectangle from the screen and works. Always look at a frame.
- **The first window handle never shows.** The script waits six seconds, then
  brings the window to the front. Do not shorten that wait.
- **The marketplace shot needs the network.** The page reads the real index.
- **Old screenshots.** Never reuse the pictures of `showcase-shots.ps1` or of
  `docs/log` on a public page: they show the `Omen` folder (section 4).
- **A wrong date or a wrong file name** in the front matter breaks the
  build with the file name and line. Read the message.
