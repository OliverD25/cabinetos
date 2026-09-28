# The edge-case fixture through the snapshot aid (docs/ui.md, "Edge cases"). No key is pressed,
# so it also runs with the screen locked. It makes the shared fixture with
# sdk\fixtures\edge-fixture.ps1, adds a Markdown file at the end of the long path, starts the
# window on the fixture with the snapshot aid's steps, and checks the disk and the log after:
# both panes on the fixture, a Cyrillic rename, a Cyrillic search, Enter into the long path,
# Enter on a Markdown file there, Delete of a file there (the Recycle Bin cannot take its path:
# the card offers Delete permanently, Skip answers), and Shift+Delete of the junction, after
# which the files behind the junction must still be there. It does not press Enter on the long path's text
# file: Windows may open it with its program (Notepad did, 2026-09-29). Needs the release
# builds of the window and the core. Runs in Windows PowerShell 5.1 and PowerShell 7; this file is UTF-8
# with a byte order mark, so 5.1 reads its Cyrillic right.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\edge-snapshots.ps1
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Tools = "$PSScriptRoot\..\..\sdk\tools",
  [string]$Fixture = "$env:TEMP\cabinetos-edge",
  [string]$Run = "edge-snapshots",
  [int]$Timeout = 180
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
foreach ($needed in $Exe, $Core) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing: build it first"; exit 1 } }

# The fixture has links: only its own script removes it (rmdir, which never follows a link).
& "$PSScriptRoot\..\..\sdk\fixtures\edge-fixture.ps1" -Root $Fixture
$Fixture = [System.IO.Path]::GetFullPath($Fixture)
$deep = Join-Path $Fixture 'long'
while ($deep.Length -lt 300) { $deep = Join-Path $deep 'segment-of-a-long-path-0123456789' }
$parent = Split-Path $deep -Parent
[System.IO.File]::WriteAllText("\\?\$deep\deep notes.md", "# Deep notes`n`nA Markdown file more than 300 characters down.", (New-Object System.Text.UTF8Encoding $false))

# This run's own configuration, logs and shots, fresh each time (they hold no links).
$root = "$env:TEMP\cabinetos-ui-test\$Run"
foreach ($part in 'config', 'logs', 'shots', 'themes', 'plugins', 'plugins-data', 'marketplace') {
  if (Test-Path -LiteralPath "$root\$part") { Remove-Item -LiteralPath "$root\$part" -Recurse -Force }
}
New-Item -ItemType Directory -Force "$root\config", "$root\logs", "$root\shots" | Out-Null
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
$env:CABINETOS_TOOLS_DIR = [System.IO.Path]::GetFullPath($Tools)
$env:CABINETOS_UI_SNAPSHOT = "$root\shots"
$segment = 'segment-of-a-long-path-0123456789'
$env:CABINETOS_UI_SNAPSHOT_STEPS = @(
  "pane:1", "path:$deep", "pane:0", "path:$Fixture\names", "wait:2500", "shot:edge-both-panes",
  "select:Звіт 2026.txt", "cmd-nowait:file.rename", "wait:600", "rename:Звіт 2027.txt", "wait:1500", "shot:edge-renamed",
  "search:звіт", "until:search", "wait:800", "shot:edge-search", "cmd:overlay.close", "wait:600",
  "path:$parent", "wait:1500", "open:$segment", "wait:2000", "shot:edge-long",
  "open:deep notes.md", "wait:2500", "shot:edge-preview-refused",
  "select:deep file.txt", "cmd:file.delete", "until:conflict", "wait:600", "shot:edge-bin-conflict", "click:Skip", "wait:1500",
  "path:$Fixture\links", "wait:1500", "select:junction to target", "cmd-nowait:file.deletePermanently", "wait:1500",
  "shot:edge-delete-question", "click:Delete permanently", "wait:3000", "shot:edge-deleted",
  "path:$Fixture\link-target", "wait:1500", "shot:done"
) -join ';'

if (Test-Path -LiteralPath "$Fixture\names\Звіт 2027.txt") { "STOP: the fixture was not made fresh"; exit 1 }
$start = Get-Date
$p = Start-Process -FilePath $Exe -PassThru
$deadline = (Get-Date).AddSeconds($Timeout)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath "$root\shots\done.png") -and -not $p.HasExited) { Start-Sleep -Milliseconds 250 }
"done.png after {0:N1} s; the window ended early: {1}" -f ((Get-Date) - $start).TotalSeconds, $p.HasExited
Start-Sleep -Milliseconds 300
if (-not $p.HasExited) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000) }
Get-ChildItem -LiteralPath "$root\shots" | ForEach-Object { "shot: $($_.FullName)" }

$log = @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json })
function Check([string]$what, [bool]$ok) { "{0}: {1}" -f $what, $(if ($ok) { 'yes' } else { 'NO' }) }
Check "the Cyrillic file was renamed on disk" ((Test-Path -LiteralPath "$Fixture\names\Звіт 2027.txt") -and -not (Test-Path -LiteralPath "$Fixture\names\Звіт 2026.txt"))
Check "the Cyrillic search answered" ([bool]($log | Where-Object { $_.message -eq 'reply received' -and $_.fields.request -eq 'search' }))
Check "the pane listed the long path" ([bool]($log | Where-Object { $_.message -eq 'listing shown' -and $_.fields.path.Length -gt 300 }))
$notices = @($log | Where-Object { $_.message -eq 'notice shown' } | ForEach-Object { $_.fields.text })
$notices | ForEach-Object { "notice: $_" }
Check "Enter on the long Markdown file said why the preview cannot show it" ([bool]($notices | Where-Object { $_ -like 'Markdown Preview cannot show deep notes.md*' }))
Check "Delete of the long file stopped at a conflict, and Skip left the file" ([bool]($log | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'resolve_conflict' }) -and (Test-Path -LiteralPath "\\?\$deep\deep file.txt"))
Check "Shift+Delete asked about a link" ([bool]($log | Where-Object { $_.message -eq 'dialog shown' -and $_.fields.title -eq 'Delete the link permanently?' }))
Check "the junction is gone" (-not (Test-Path -LiteralPath "$Fixture\links\junction to target"))
$kept = @(Get-ChildItem -LiteralPath "$Fixture\link-target" | ForEach-Object { $_.Name })
Check "the files behind the junction stayed (kept 1.txt to kept 3.txt)" (($kept -join ',') -eq 'kept 1.txt,kept 2.txt,kept 3.txt')
Check "every log line is whole" (@(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8).Count -eq $log.Count)
