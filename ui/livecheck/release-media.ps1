# Takes the media of one CabinetOS release for its public "What's new" page: the screenshots (PNG) and the short
# recordings (raw MP4) that notes.md shows. One command, one media list: release-media.json names every item and the
# keys that bring the window to the state to show. The skill .claude/skills/release-notes-page/SKILL.md explains where
# this fits in a release.
#
# It presses real keys, so it runs on a machine whose desktop is free: the Omen laptop through remote-script.ps1, or
# this PC with the creator's consent (CLAUDE.md, "Nothing opens a CabinetOS window on this PC without the creator's
# consent"). -CheckOnly opens no window and presses no key: it only reads and checks the list.
#
# What it does, in order: reads and checks the list; makes the demo folder (default C:\Demo) when it is missing, with
# harmless files, a drawn photo and a short clip (Pictures), and a tiny git repository, and never changes what is there; then, for every item, starts a fresh
# window (a new configuration under $env:TEMP, the sidebar hidden), runs the item's steps, saves the shot or the
# recording into -OutDir, and closes the window. Each item has a window of its own, so nothing one item does (a theme,
# the terminal, a view) reaches the next. Only the files of the list's items are written in -OutDir.
#
# Why the demo folder and the hidden sidebar: the media must show no personal name. The window shows only the demo
# folder. The sidebar is hidden because its fixed "pinned" rows include the Windows user's own folder by name, and no
# setting removes that row.
#
# The list (JSON, ASCII only):
#   { "left": "C:\\Demo", "right": "C:\\Demo\\Projects",           (optional; the folders each item starts with)
#     "items": [ { "name": "hero", "kind": "image", "steps": [ { "do": "down", "count": 3 } ] },
#                { "name": "command-palette", "kind": "video", "seconds": 6, "steps": [ ... ] } ] }
# An image item saves <name>.png (the window's own frame). A video item saves <name>.raw.mp4 (15 frames a second, no
# sound) and "seconds" (5 to 12) is its length; the steps start about 0.7 s after the recording starts. Names are
# lower case letters, digits and "-". Optional on an item: "settleMs" (the wait before an image, default 1500), and
# "left" and "right" (the folders this item starts with, in place of the list's own; an item that needs another folder,
# such as C:\Demo\Pictures for Quick View, names it here, so the steps need no "go").
# The steps, in the order they run:
#   go (path)              Ctrl+L, the path, Enter, in the pane that has the keys
#   tab                    switch to the other pane
#   up / down (count)      the arrow key, count times (default 1)
#   palette (text)         open the command palette; type the text when there is one
#   type (text, delayMs)   type the text; delayMs is the pause after each letter (default 15)
#   key (key)              a key such as "Ctrl+Alt+C"; "Ctrl+K Ctrl+T" is a chord (keys in turn); names: Ctrl, Shift,
#                          Alt, A-Z, 0-9, F1-F12, Enter, Esc, Tab, Space, Backspace, Delete, Insert, Home, End,
#                          PageUp, PageDown, Left, Up, Right, Down, Backquote, Backslash, Comma, Period, Minus, Plus,
#                          Slash, LBracket, RBracket
#   enter / esc            the Enter or Esc key
#   terminal (wait)        Ctrl+Backquote; the first time it waits wait ms (default 6000) for the shell to start
#   wait (ms)              do nothing for ms milliseconds
#   theme (downs)          Ctrl+K Ctrl+T, Home, Down downs times, Enter: the theme picker lists the shipped themes by
#                          id (catppuccin-latte, catppuccin-mocha, commander-compact, default, github-light, nord,
#                          rose-pine-moon)
#
# -LocalCatalogue builds the marketplace's two catalogues into the run's own folder (build-index.ps1 -Collection, with
# -Extensions when the Agent's plugin is built) and points every window at them: the Extensions page and the theme
# gallery then show a catalogue with tile colours and the collection's themes, which the public site has not had since
# Phase 23 until its themes.json is published (the public index.json lists no theme, and an older one lists themes
# without tile colours). Without it every window reads the public catalogues, as a user's does.
#
# -Viewers makes a folder "viewers" in the run's own work folder, copies the Quick View viewers of sdk\tools into it
# (image-viewer and media-viewer) and starts every window with --tools-dir <that folder>. Without it no viewer is
# installed, as in a fresh install, so Quick View has no full view to show. It is for the whole run, not per item: the viewers
# change nothing in a window that never presses Space, so the other items look the same with it.
#
# A recording needs ffmpeg: -Ffmpeg, else C:\ffmpeg\bin\ffmpeg.exe, else C:\Dev\tools\ffmpeg\ffmpeg.exe (the laptop's).
# -Capture screen (default) records the rectangle of the window's frame from the screen. -Capture title records by the
# window's title, read from the process just before the recording starts. The title mode records only black for the
# CabinetOS window (measured on the Omen laptop, 2026-10-02: 5.9 of 6 s black), because WinUI draws on the GPU; it stays
# only for another machine or a later window that may behave differently. Always look at a frame of a recording.
# Windows PowerShell 5.1 and PowerShell 7.
#
#   remote-script.ps1 -Script ui\livecheck\release-media.ps1 -Args "-OutDir C:\Dev\cabinetos\_io\script-runs\release-media" -Branch <branch>
#   remote-script.ps1 -Script ui\livecheck\release-media.ps1 -Args "-OutDir C:\Dev\cabinetos\_io\script-runs\release-media -Only terminal,marketplace"
#   release-media.ps1 -CheckOnly
param(
  [string]$List = "$PSScriptRoot\release-media.json",
  [string]$OutDir = "$env:TEMP\cabinetos-release-media",
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Ffmpeg = 'C:\ffmpeg\bin\ffmpeg.exe',
  [string]$Demo = 'C:\Demo',
  [ValidateSet('screen', 'title')][string]$Capture = 'screen',
  # Only the items with these names (comma separated), for a second try of one or two of them.
  [string[]]$Only = @(),
  [switch]$LocalCatalogue,
  [switch]$Viewers,
  [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 6) { $env:PSModulePath = "$PSHOME\Modules;$env:PSModulePath" }
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
$List = [System.IO.Path]::GetFullPath($List)
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$Demo = [System.IO.Path]::GetFullPath($Demo).TrimEnd('\')

# ----- The keys -----
$KeyTable = @{
  ctrl = 0x11; shift = 0x10; alt = 0x12; enter = 0x0D; esc = 0x1B; escape = 0x1B; tab = 0x09; space = 0x20
  backspace = 0x08; delete = 0x2E; insert = 0x2D; home = 0x24; end = 0x23; pageup = 0x21; pagedown = 0x22
  left = 0x25; up = 0x26; right = 0x27; down = 0x28; backquote = 0xC0; backslash = 0xDC; comma = 0xBC; period = 0xBE
  minus = 0xBD; plus = 0xBB; slash = 0xBF; lbracket = 0xDB; rbracket = 0xDD
}
foreach ($c in 97..122) { $KeyTable[[string][char]$c] = $c - 32 }
foreach ($d in 0..9) { $KeyTable["$d"] = 0x30 + $d }
foreach ($f in 1..12) { $KeyTable["f$f"] = 0x6F + $f }

# ----- The list, checked before anything starts -----
function Opt($object, [string]$name, $default) {
  $property = $object.PSObject.Properties[$name]
  if ($property -and $null -ne $property.Value) { $property.Value } else { $default }
}
function Test-Combo([string]$combo, [string]$where) {
  foreach ($part in $combo.Split('+')) {
    if (-not $KeyTable.ContainsKey($part.Trim().ToLowerInvariant())) { throw "$where`: unknown key name '$part' in '$combo'" }
  }
}
function Test-Step($step, [string]$where) {
  $do = Opt $step 'do' ''
  switch ($do) {
    'go' { if (-not (Opt $step 'path' '')) { throw "$where`: 'go' needs a path" } }
    { $_ -in 'tab', 'enter', 'esc' } { }
    { $_ -in 'up', 'down' } { if ([int](Opt $step 'count' 1) -lt 1) { throw "$where`: 'count' must be 1 or more" } }
    'palette' { }
    'type' { if ($null -eq (Opt $step 'text' $null)) { throw "$where`: 'type' needs a text" } }
    'key' {
      $key = [string](Opt $step 'key' '')
      if (-not $key) { throw "$where`: 'key' needs a key" }
      foreach ($combo in ($key -split '\s+' | Where-Object { $_ })) { Test-Combo $combo $where }
    }
    'terminal' { }
    'wait' { if ([int](Opt $step 'ms' -1) -lt 0) { throw "$where`: 'wait' needs ms" } }
    'theme' { if ([int](Opt $step 'downs' -1) -lt 0) { throw "$where`: 'theme' needs downs" } }
    default { throw "$where`: unknown step '$do'" }
  }
}
if (-not (Test-Path -LiteralPath $List)) { "STOP: the list $List is missing"; exit 1 }
$doc = Get-Content -LiteralPath $List -Raw | ConvertFrom-Json
$items = @(if ($doc.PSObject.Properties['items']) { $doc.items } else { $doc })
$leftStart = [string](Opt $doc 'left' $Demo)
$rightStart = [string](Opt $doc 'right' "$Demo\Projects")
$seen = @{}
foreach ($item in $items) {
  $name = [string](Opt $item 'name' '')
  $kind = [string](Opt $item 'kind' '')
  if ($name -notmatch '^[a-z0-9][a-z0-9-]*$') { throw "item '$name': a name is lower case letters, digits and '-'" }
  if ($kind -notin 'image', 'video') { throw "item '$name': kind must be 'image' or 'video'" }
  if ($seen.ContainsKey("$name.$kind")) { throw "item '$name' ($kind) appears twice" }
  $seen["$name.$kind"] = $true
  if ($kind -eq 'video') {
    $seconds = [int](Opt $item 'seconds' 0)
    if ($seconds -lt 5 -or $seconds -gt 12) { throw "item '$name': a video needs seconds from 5 to 12" }
  }
  $n = 0
  foreach ($step in @(Opt $item 'steps' @())) { $n++; Test-Step $step "item '$name', step $n" }
}
if ($Only) {
  $wanted = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  foreach ($w in $wanted) { if (-not ($items | Where-Object { $_.name -eq $w })) { throw "-Only names '$w', which is not in the list" } }
  $items = @($items | Where-Object { $wanted -contains $_.name })
}
if ($items.Count -eq 0) { "STOP: the list has no item to run"; exit 1 }
"list ok: $($items.Count) item(s) from $List"
foreach ($item in $items) {
  "  $($item.name) ($($item.kind)$(if ($item.kind -eq 'video') { ", $($item.seconds) s" })): $(@(Opt $item 'steps' @()).Count) step(s)"
}
if ($CheckOnly) { "check only: no window, no key, no file written"; exit 0 }

# ----- What the run needs -----
foreach ($needed in $Exe, $Core) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing"; exit 1 } }
$needsVideo = [bool]($items | Where-Object { $_.kind -eq 'video' })
if ($needsVideo) {
  if (-not (Test-Path -LiteralPath $Ffmpeg)) {
    $alternative = 'C:\Dev\tools\ffmpeg\ffmpeg.exe'
    if (Test-Path -LiteralPath $alternative) { $Ffmpeg = $alternative } else { "STOP: ffmpeg is missing ($Ffmpeg and $alternative)"; exit 1 }
  }
  "ffmpeg: $Ffmpeg"
}
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ----- The demo folder: only what is missing is made, nothing that exists is changed -----
$script:newFolders = New-Object System.Collections.Generic.List[string]
function New-DemoDirectory([string]$folder) {
  if (Test-Path -LiteralPath $folder) { return }
  New-DemoDirectory (Split-Path $folder -Parent)
  New-Item -ItemType Directory -Force $folder | Out-Null
  $script:newFolders.Add($folder)
}
function New-DemoFile([string]$relative, [int]$bytes, [int]$daysOld) {
  $path = Join-Path $Demo $relative
  if (Test-Path -LiteralPath $path) { return }
  New-DemoDirectory (Split-Path $path -Parent)
  if ([System.IO.Path]::GetExtension($path) -in '.txt', '.md', '.csv', '.html', '.css', '.py') {
    $line = "Sample text for the CabinetOS release screenshots.`r`n"
    $text = New-Object System.Text.StringBuilder
    while ($text.Length -lt $bytes) { [void]$text.Append($line) }
    $data = [System.Text.Encoding]::ASCII.GetBytes($text.ToString().Substring(0, $bytes))
  } else {
    $data = New-Object byte[] $bytes
  }
  [System.IO.File]::WriteAllBytes($path, $data)
  (Get-Item -LiteralPath $path).LastWriteTime = (Get-Date).AddDays(-$daysOld).AddMinutes(-($bytes % 997))
}
function Invoke-Git([string]$folder, [string[]]$arguments) {
  $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try {
    & $script:gitExe -C $folder -c user.name=Demo -c user.email=demo@example.invalid -c commit.gpgsign=false @arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed in $folder ($LASTEXITCODE)" }
  } finally { $ErrorActionPreference = $previous }
}
# A real picture, so Quick View and the thumbnails have something to show: New-DemoFile writes filler, which no viewer can
# decode. Drawn at run time (a sky, a sun, two mountain ranges, a lake; no text, nothing personal) with a fixed seed, so
# every run draws the same picture.
function Write-DemoPhoto([string]$path) {
  Add-Type -AssemblyName System.Drawing
  $w = 2400; $h = 1600; $horizon = 1000
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    function C([int]$r, [int]$gr, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $gr, $b) }
    $vertical = [System.Drawing.Drawing2D.LinearGradientMode]::Vertical
    # The sky: deep blue at the top, through rose, to a warm glow at the horizon.
    $skyRect = New-Object System.Drawing.Rectangle 0, 0, $w, ($horizon + 1)
    $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush $skyRect, (C 30 50 110), (C 255 190 120), $vertical
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = [System.Drawing.Color[]]@((C 28 48 108), (C 96 90 160), (C 236 140 140), (C 255 200 130))
    $blend.Positions = [single[]]@(0.0, 0.45, 0.80, 1.0)
    $sky.InterpolationColors = $blend
    $g.FillRectangle($sky, $skyRect)
    # The sun: a soft glow, then the disc.
    $sx = 1980; $sy = 620; $sr = 100
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(($sx - 520), ($sy - 520), 1040, 1040)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterColor = (C 255 225 150 200)
    $glow.SurroundColors = [System.Drawing.Color[]]@((C 255 190 120 0))
    $g.FillPath($glow, $glowPath)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 244 205)), ($sx - $sr), ($sy - $sr), (2 * $sr), (2 * $sr))
    # Two ranges of mountains on the far shore: a pale one behind, a dark one in front. A fixed seed, so every run draws the same picture.
    $random = New-Object System.Random 7
    foreach ($range in @(@{ Base = 700.0; Swing = 150.0; Step = 90; Color = (C 120 98 150) }, @{ Base = 800.0; Swing = 130.0; Step = 70; Color = (C 52 44 82) })) {
      $points = New-Object System.Collections.Generic.List[System.Drawing.PointF]
      $points.Add((New-Object System.Drawing.PointF 0, $horizon))
      $y = $range.Base
      for ($x = 0; $x -le $w + $range.Step; $x += $range.Step) {
        $ridge = $range.Base - $range.Swing * [math]::Abs([math]::Sin($x / 520.0 + $range.Base)) - $range.Swing * 0.5 * [math]::Sin($x / 190.0)
        $y = $ridge + ($random.NextDouble() - 0.5) * 50
        $points.Add((New-Object System.Drawing.PointF $x, $y))
      }
      $points.Add((New-Object System.Drawing.PointF $w, $horizon))
      $g.FillPolygon((New-Object System.Drawing.SolidBrush $range.Color), $points.ToArray())
    }
    # The water: the horizon's colour darkening to deep teal toward the bottom.
    $waterRect = New-Object System.Drawing.Rectangle 0, $horizon, $w, ($h - $horizon)
    $water = New-Object System.Drawing.Drawing2D.LinearGradientBrush $waterRect, (C 244 170 130), (C 18 52 78), $vertical
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = [System.Drawing.Color[]]@((C 238 170 140), (C 90 100 140), (C 16 48 74))
    $blend.Positions = [single[]]@(0.0, 0.35, 1.0)
    $water.InterpolationColors = $blend
    $g.FillRectangle($water, $waterRect)
    # The sun's path on the water: bars that grow wider and fainter toward the viewer.
    for ($i = 0; $i -lt 22; $i++) {
      $bandY = $horizon + 8 + $i * $i * 1.3 + $i * 6
      $bandW = 150 + $i * 26
      $alpha = [int](200 - $i * 8)
      $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 226 160 $alpha)), ($sx - $bandW / 2 + ($random.NextDouble() - 0.5) * 30), $bandY, $bandW, (4 + $i * 0.9))
    }
    $jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $quality = New-Object System.Drawing.Imaging.EncoderParameters 1
    $quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]88)
    $bmp.Save($path, $jpeg, $quality)
  } finally { $g.Dispose(); $bmp.Dispose() }
}
function New-DemoPhoto([string]$relative, [int]$daysOld) {
  $path = Join-Path $Demo $relative
  if (Test-Path -LiteralPath $path) { return }
  New-DemoDirectory (Split-Path $path -Parent)
  Write-DemoPhoto $path
  (Get-Item -LiteralPath $path).LastWriteTime = (Get-Date).AddDays(-$daysOld)
}
# A real clip too: the committed 1080p fixture of the live check (a few seconds of picture, no sound, nothing personal).
function New-DemoClip([string]$relative, [int]$daysOld) {
  $path = Join-Path $Demo $relative
  if (Test-Path -LiteralPath $path) { return }
  $clip = Join-Path $PSScriptRoot 'fixtures\media\clip-1080p.mp4'
  if (-not (Test-Path -LiteralPath $clip)) { "WARN: $clip is missing: the demo clip is not made"; return }
  New-DemoDirectory (Split-Path $path -Parent)
  Copy-Item -LiteralPath $clip -Destination $path
  (Get-Item -LiteralPath $path).LastWriteTime = (Get-Date).AddDays(-$daysOld)
}
function New-DemoFolder {
  # name|bytes|days old. Enough rows to fill a good part of the two panes in the shots.
  $spec = @(
    'welcome.txt|612|20', 'Holiday plan.docx|48210|9', 'Budget 2026.xlsx|23552|4', 'Family recipes.pdf|318464|26',
    'Meeting agenda.docx|31744|2', 'Photo list.csv|9216|12', 'Receipts 2026.zip|884736|17', 'Slides.pptx|1048576|6',
    'Travel notes.md|3584|10', 'shopping-list.txt|420|1', 'Reading list.md|1740|23',
    'Documents\notes.txt|3120|2', 'Documents\budget.csv|14336|6', 'Documents\readme.md|2048|15', 'Documents\report.pdf|184320|7',
    'Documents\meeting-notes.md|5120|1', 'Documents\invoice-march.pdf|96256|31', 'Documents\todo.txt|940|0',
    'Downloads\archive.zip|1258291|3', 'Downloads\setup-guide.pdf|410624|11', 'Downloads\installer-notes.txt|1830|11',
    'Music\track-01.mp3|712704|40', 'Music\track-02.mp3|688128|40', 'Music\track-03.mp3|745472|37', 'Music\track-04.mp3|702464|33', 'Music\track-05.mp3|731136|33',
    'Photos\IMG_0001.jpg|524288|30', 'Photos\IMG_0002.jpg|610304|30', 'Photos\IMG_0003.jpg|398336|29', 'Photos\IMG_0004.jpg|873472|29',
    'Photos\IMG_0005.jpg|451584|21', 'Photos\IMG_0006.jpg|702464|21', 'Photos\IMG_0007.jpg|337920|8', 'Photos\IMG_0008.jpg|916480|5',
    'Archive\taxes-2025.pdf|254976|190', 'Archive\old-photos.zip|1572864|240',
    'Backups\backup-2026-08.zip|2097152|45', 'Backups\backup-2026-09.zip|2150400|14',
    'Travel\itinerary.pdf|143360|13', 'Travel\tickets.pdf|88064|13', 'Travel\packing-list.txt|860|12',
    'Work\Q3 summary.xlsx|40960|5', 'Work\project plan.docx|57344|9', 'Work\team notes.md|4096|3',
    'Projects\website\index.html|4096|3', 'Projects\website\style.css|2560|3', 'Projects\notes-app\main.py|1536|8',
    'Projects\notes-app\requirements.txt|64|8', 'Projects\blog\first-post.md|3300|19', 'Projects\blog\config.txt|512|19',
    'Projects\recipes-app\app.py|2750|27', 'Projects\game-jam\ideas.md|1900|34',
    'Projects\todo.md|730|2', 'Projects\ideas.txt|1980|14', 'Projects\timeline.csv|6144|5', 'Projects\release-checklist.md|2300|1',
    'Projects\roadmap.csv|5120|22'
  )
  foreach ($entry in $spec) { $part = $entry.Split('|'); New-DemoFile $part[0] ([int]$part[1]) ([int]$part[2]) }
  New-DemoPhoto 'Pictures\Lake-at-dawn.jpg' 3
  New-DemoClip 'Pictures\Harbour-clip.mp4' 3
  # A tiny git repository with three commits, so the terminal shot has a log to show.
  $repo = Join-Path $Demo 'Projects\cabinetos-sample'
  if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { "WARN: git is not installed here: the sample repository is not made, and the terminal shot has no log" }
    else {
      $script:gitExe = $git.Source
      New-DemoDirectory $repo
      Invoke-Git $repo @('init', '-q')
      Invoke-Git $repo @('symbolic-ref', 'HEAD', 'refs/heads/main')
      $commits = @(@('README.md', 1024, 'Add the README'), @('todo.md', 512, 'Add a to-do list'), @('notes.txt', 2048, 'Add the first notes'))
      foreach ($commit in $commits) {
        New-DemoFile "Projects\cabinetos-sample\$($commit[0])" $commit[1] 0
        Invoke-Git $repo @('add', $commit[0])
        Invoke-Git $repo @('commit', '-q', '-m', $commit[2])
      }
    }
  }
  # The folders made by this run get older dates, so the listings do not all say "Today". The deepest go first.
  foreach ($folder in ($script:newFolders | Sort-Object -Property Length -Descending)) {
    $days = ($folder.ToCharArray() | ForEach-Object { [int]$_ } | Measure-Object -Sum).Sum % 28
    (Get-Item -LiteralPath $folder).LastWriteTime = (Get-Date).AddDays(-$days).AddHours(-2)
  }
  "demo folder ready: $Demo ($($script:newFolders.Count) folder(s) made in this run)"
}
New-DemoFolder

# ----- The window and the keys -----
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class Show {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint data; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  static bool Extended(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }
  static INPUT Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.dwFlags = (up ? 2u : 0u) | (Extended(vk) ? 1u : 0u); return i; }
  static INPUT Unicode(char c, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wScan = c; i.u.ki.dwFlags = 4u | (up ? 2u : 0u); return i; }
  static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
  public static void Press(params ushort[] vks) {
    foreach (var vk in vks) Send(Key(vk, false));
    for (int i = vks.Length - 1; i >= 0; i--) Send(Key(vks[i], true));
  }
  public static void Type(string text) { foreach (var c in text) { Send(Unicode(c, false), Unicode(c, true)); Thread.Sleep(15); } }
  public static void Front(IntPtr h) { Send(Key(0x12, false), Key(0x12, true)); ShowWindow(h, 9); SetForegroundWindow(h); }
}
"@
[void][Show]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$script:p = $null
$script:h = $null
$script:terminalShown = $false
$runRoot = "$env:TEMP\cabinetos-release-media-run"
$script:catalogue = $null
$script:viewersDir = $null

# Every action checks first that the window is in front, so no key goes to another program.
function Step($text) {
  if ($script:h -and [Show]::ForegroundPid() -ne [uint32]$script:p.Id) {
    throw "CabinetOS is not the foreground window before '$text' (in front: pid $([Show]::ForegroundPid()))"
  }
  "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
}
function Keys([string]$combo) {
  $vks = foreach ($part in $combo.Split('+')) { [uint16]$KeyTable[$part.Trim().ToLowerInvariant()] }
  [Show]::Press([uint16[]]$vks)
}
function Get-Frame {
  $r = New-Object Show+RECT
  [void][Show]::DwmGetWindowAttribute($script:h, 9, [ref]$r, 16)
  $r
}
function Save-Shot([string]$name) {
  $r = Get-Frame
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $path = Join-Path $OutDir "$name.png"
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $size = "$($bmp.Width)x$($bmp.Height)"; $g.Dispose(); $bmp.Dispose()
  Step "screenshot $path ($size)"
}
function GoPath([string]$path) {
  Keys 'Ctrl+L'; Start-Sleep -Milliseconds 700
  [Show]::Type($path); Start-Sleep -Milliseconds 300
  Keys 'Enter'; Start-Sleep -Milliseconds 1500
}
function ChooseTheme([int]$downs) {
  Keys 'Ctrl+K'; Start-Sleep -Milliseconds 200
  Keys 'Ctrl+T'; Start-Sleep -Milliseconds 1000
  Keys 'Home'; Start-Sleep -Milliseconds 200
  for ($i = 0; $i -lt $downs; $i++) { Keys 'Down'; Start-Sleep -Milliseconds 300 }
  Keys 'Enter'; Start-Sleep -Milliseconds 2500
}
function Invoke-Action($s) {
  switch (Opt $s 'do' '') {
    'go' { Step "go $($s.path)"; GoPath $s.path }
    'tab' { Step 'tab'; Keys 'Tab'; Start-Sleep -Milliseconds 500 }
    { $_ -in 'up', 'down' } {
      $count = [int](Opt $s 'count' 1)
      Step "$($s.'do') x$count"
      $key = if ($s.'do' -eq 'up') { 'Up' } else { 'Down' }
      for ($i = 0; $i -lt $count; $i++) { Keys $key; Start-Sleep -Milliseconds 150 }
    }
    'palette' {
      Step "palette '$(Opt $s 'text' '')'"
      Keys 'Ctrl+Shift+P'; Start-Sleep -Milliseconds 800
      if (Opt $s 'text' '') { [Show]::Type([string]$s.text); Start-Sleep -Milliseconds 1000 }
    }
    'type' {
      Step "type '$($s.text)'"
      $delay = [int](Opt $s 'delayMs' 15)
      if ($delay -le 15) { [Show]::Type([string]$s.text) }
      else { foreach ($c in ([string]$s.text).ToCharArray()) { [Show]::Type([string]$c); Start-Sleep -Milliseconds $delay } }
    }
    'key' {
      Step "key $($s.key)"
      foreach ($combo in ([string]$s.key -split '\s+' | Where-Object { $_ })) { Keys $combo; Start-Sleep -Milliseconds 250 }
      Start-Sleep -Milliseconds 400
    }
    'enter' { Step 'enter'; Keys 'Enter'; Start-Sleep -Milliseconds 500 }
    'esc' { Step 'esc'; Keys 'Esc'; Start-Sleep -Milliseconds 700 }
    'terminal' {
      Step 'terminal'
      Keys 'Ctrl+Backquote'
      if (-not $script:terminalShown) { Start-Sleep -Milliseconds ([int](Opt $s 'wait' 6000)); $script:terminalShown = $true }
      else { Start-Sleep -Milliseconds 1000 }
    }
    'wait' { Step "wait $($s.ms) ms"; Start-Sleep -Milliseconds ([int]$s.ms) }
    'theme' { Step "theme: $($s.downs) down in the picker"; ChooseTheme ([int]$s.downs) }
  }
}

# A fresh window: a new configuration (sidebar hidden), the environment of the showcase script, the real window handle
# (the first one is a window that never shows: six seconds, then the front call until the window is in front), maximized.
function Start-App([string]$label) {
  $root = Join-Path $runRoot $label
  New-Item -ItemType Directory -Force "$root\config", "$root\logs" | Out-Null
  $env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
  $config = if ($script:catalogue) {
    $folder = $script:catalogue | ConvertTo-Json
    '{"version":1,"ui":{"sidebar":false},"marketplace":{"index":' + $folder + ',"themes":' + $folder + '}}'
  } else { '{"version":1,"ui":{"sidebar":false}}' }
  [System.IO.File]::WriteAllText($env:CABINETOS_CONFIG, $config, (New-Object System.Text.UTF8Encoding $false))
  $env:CABINETOS_LOG_DIR = "$root\logs"
  $env:CABINETOS_CORE_EXE = $Core
  $env:CABINETOS_THEMES_DIR = "$root\themes"
  $env:CABINETOS_UNDO_DIR = "$root\undo"
  $env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
  $env:CABINETOS_PLUGINS_DIR = "$root\plugins"
  $env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
  $env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
  Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
  Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue
  $script:h = $null
  $script:terminalShown = $false
  Step "start $Exe"
  $script:p = if ($script:viewersDir) { Start-Process -FilePath $Exe -ArgumentList "--tools-dir `"$($script:viewersDir)`"" -PassThru }
              else { Start-Process -FilePath $Exe -PassThru }
  "app pid: $($script:p.Id)"
  Start-Sleep -Seconds 6
  $deadline = (Get-Date).AddSeconds(40)
  do { $script:p.Refresh(); if ($script:p.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
  if ($script:p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'no window within 46 s' }
  $handle = $script:p.MainWindowHandle
  foreach ($try in 1..8) {
    [Show]::Front($handle); Start-Sleep -Milliseconds 800
    if ([Show]::ForegroundPid() -eq [uint32]$script:p.Id) { break }
    "front try $try failed; in front: pid $([Show]::ForegroundPid())"
  }
  # Maximized last: the front call restores a window, which would undo it.
  [void][Show]::ShowWindow($handle, 3); Start-Sleep -Milliseconds 1500
  $script:h = $handle
  if ([Show]::ForegroundPid() -ne [uint32]$script:p.Id) { throw 'the window did not come to the front' }
  "foreground ok: True"
}
function Stop-App {
  if ($script:p -and -not $script:p.HasExited) {
    [void]$script:p.CloseMainWindow()
    if (-not $script:p.WaitForExit(10000)) { $script:p.Kill() }
  }
  $script:h = $null
  Start-Sleep -Milliseconds 1500
}

# ----- Recordings -----
function Get-VideoInfo([string]$path) {
  $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $text = (& $Ffmpeg -hide_banner -i $path 2>&1 | ForEach-Object { "$_" }) -join "`n" } finally { $ErrorActionPreference = $previous }
  $info = ''
  if ($text -match 'Duration: (\d+):(\d+):([\d.]+)') { $info += ('{0:N1} s' -f ([int]$Matches[1] * 3600 + [int]$Matches[2] * 60 + [double]$Matches[3])) }
  if ($text -match 'Video: [^\r\n]*?, (\d{2,5})x(\d{2,5})') { $info += " $($Matches[1])x$($Matches[2])" }
  $info
}
function Start-Recording([string]$raw, [int]$seconds, [string]$log) {
  $r = Get-Frame
  $width = ($r.Right - $r.Left); $height = ($r.Bottom - $r.Top)
  $width -= $width % 2; $height -= $height % 2
  if ($Capture -eq 'title') {
    $script:p.Refresh()
    $source = "-i `"title=$($script:p.MainWindowTitle)`""
  } else {
    $source = "-offset_x $($r.Left) -offset_y $($r.Top) -video_size ${width}x${height} -i desktop"
  }
  $arguments = "-hide_banner -nostdin -y -f gdigrab -framerate 15 -draw_mouse 0 $source -t $seconds -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p -an `"$raw`""
  Write-Host "ffmpeg $arguments"
  # DPI aware, so the numbers of the window's frame (physical pixels) are the numbers it records.
  $env:__COMPAT_LAYER = 'HighDpiAware'
  try {
    $ff = Start-Process -FilePath $Ffmpeg -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardError $log
  } finally { Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue }
  $null = $ff.Handle
  $ff
}

# ----- One item -----
$produced = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]
function Invoke-MediaItem($item) {
  $name = [string]$item.name
  $kind = [string]$item.kind
  Start-App "$name-$kind"
  try {
    GoPath ([string](Opt $item 'left' $leftStart))
    Keys 'Tab'; Start-Sleep -Milliseconds 500
    GoPath ([string](Opt $item 'right' $rightStart))
    Keys 'Tab'; Start-Sleep -Milliseconds 500
    if ($kind -eq 'image') {
      foreach ($s in @(Opt $item 'steps' @())) { Invoke-Action $s }
      Start-Sleep -Milliseconds ([int](Opt $item 'settleMs' 1500))
      Save-Shot $name
      $produced.Add((Join-Path $OutDir "$name.png"))
    } else {
      $seconds = [int]$item.seconds
      $raw = Join-Path $OutDir "$name.raw.mp4"
      $log = Join-Path $OutDir "$name.ffmpeg.log"
      if (Test-Path -LiteralPath $raw) { Remove-Item -LiteralPath $raw -Force }
      $ff = Start-Recording $raw $seconds $log
      Start-Sleep -Milliseconds 700
      if ($ff.HasExited) { throw "ffmpeg ended at once (exit $($ff.ExitCode)): $(Get-Content -LiteralPath $log -Tail 3 -ErrorAction SilentlyContinue)" }
      $start = Get-Date
      foreach ($s in @(Opt $item 'steps' @())) { Invoke-Action $s }
      $took = ((Get-Date) - $start).TotalSeconds
      if ($took -gt ($seconds - 0.7)) { "WARN: the steps took $([math]::Round($took, 1)) s, longer than the $($seconds - 0.7) s the recording leaves for them: the end is cut" }
      if (-not $ff.WaitForExit(($seconds + 20) * 1000)) { $ff.Kill(); throw "ffmpeg did not stop within $($seconds + 20) s" }
      if (-not (Test-Path -LiteralPath $raw) -or (Get-Item -LiteralPath $raw).Length -lt 10000) {
        throw "the recording is missing or almost empty (a black picture is this small; -Capture title records black for this window, use -Capture screen): $(Get-Content -LiteralPath $log -Tail 3 -ErrorAction SilentlyContinue)"
      }
      $produced.Add($raw)
    }
  } finally { Stop-App }
}

if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }
if ($LocalCatalogue) {
  $indexScript = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\sdk\marketplace\build-index.ps1")
  $indexArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $indexScript, '-OutDir', "$runRoot\catalogue", '-Collection')
  if (Test-Path -LiteralPath "$PSScriptRoot\..\..\sdk\extensions\agent\plugin\plugin.wasm") { $indexArgs += '-Extensions' }
  & powershell.exe @indexArgs | ForEach-Object { "catalogue: $_" }
  if ($LASTEXITCODE -ne 0) { "STOP: the local catalogue could not be built"; exit 1 }
  $script:catalogue = "$runRoot\catalogue"
}
if ($Viewers) {
  $viewersSource = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\sdk\tools")
  $script:viewersDir = Join-Path $runRoot 'viewers'
  New-Item -ItemType Directory -Force $script:viewersDir | Out-Null
  foreach ($viewer in 'image-viewer', 'media-viewer') {
    if (-not (Test-Path -LiteralPath (Join-Path $viewersSource $viewer))) { "STOP: $viewersSource\$viewer is missing"; exit 1 }
    Copy-Item -LiteralPath (Join-Path $viewersSource $viewer) -Destination $script:viewersDir -Recurse
  }
  "viewers: $($script:viewersDir)"
}
foreach ($item in $items) {
  "=== $($item.name) ($($item.kind))"
  try { Invoke-MediaItem $item }
  catch {
    "{0:HH:mm:ss.fff} FAILED {1}: {2} (line {3})" -f (Get-Date), $item.name, $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber
    $failed.Add("$($item.name) ($($item.kind))")
  }
}
if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }

"--- files in ${OutDir}:"
foreach ($file in $produced) {
  $length = (Get-Item -LiteralPath $file).Length
  $extra = if ($file.EndsWith('.mp4')) { Get-VideoInfo $file } else { '' }
  "file {0}  {1:N0} KB  {2}" -f $file, ($length / 1KB), $extra
}
if ($failed.Count -gt 0) { "FAILED: $($failed -join ', ')"; exit 1 }
"done: $($produced.Count) file(s) in $OutDir"
