# Makes the folders the speed review measures (docs/log/2026-10-01/speed-review.md), or removes them:
#
#   <Root>\small         20 files and 4 folders: a start on a small folder
#   <Root>\home-3000     2,850 files and 150 folders: a user's home folder of a real size
#   <Root>\files-100000  100,000 empty files
#   <Root>\folders-10000 10,000 empty folders
#
# The names are mixed as a user's are (numbered photos and documents, spaces and punctuation, Ukrainian and
# Japanese), with the same words as the core's list_directory bench, and eight extensions, so the type column
# has several names to look up. Each folder gets a "<name>.complete" marker next to it when it is whole; a
# folder with its marker is left as it is, so the script can run again after a stop. Creating 100,000 files
# takes one to three minutes. -Remove deletes the four folders and their markers (the fixtures only, never
# anything else under -Root). Runs in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\speed-fixtures.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\speed-fixtures.ps1 -Remove
param(
  [string]$Root = "$env:TEMP\cabinetos-speed-review\fixtures",
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$names = 'small', 'home-3000', 'files-100000', 'folders-10000'
if ($Remove) {
  foreach ($name in $names) {
    foreach ($path in "$Root\$name", "$Root\$name.complete") {
      if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force; "removed $path" }
    }
  }
  exit 0
}

$words = 'IMG', 'report', 'Photo', 'draft', 'invoice', 'notes', 'Backup', 'data', 'Звіт', 'фото', '資料', 'file'
$separators = '_', ' ', '-', ''
$extensions = 'jpg', 'txt', 'pdf', 'docx', 'png', 'json', 'log', 'zip'

function Name([int]$n, [string]$extension) {
  $word = $words[$n % $words.Count]
  $separator = $separators[[Math]::Floor($n / 3) % $separators.Count]
  if ($extension) { "$word$separator$n.$extension" } else { "$word$separator$n" }
}

function Make([string]$name, [int]$files, [int]$folders) {
  $dir = "$Root\$name"
  $marker = "$Root\$name.complete"
  if (Test-Path -LiteralPath $marker) { "$dir is there already"; return }
  [void](New-Item -ItemType Directory -Force $dir)
  $started = Get-Date
  for ($n = 0; $n -lt $folders; $n++) {
    [void][System.IO.Directory]::CreateDirectory("$dir\$(Name $n '')")
  }
  for ($n = 0; $n -lt $files; $n++) {
    $path = "$dir\$(Name $n $extensions[[Math]::Floor($n / 7) % $extensions.Count])"
    # A name made twice (the numbers make that impossible, but a rerun after a stop finds files) is kept.
    if (-not [System.IO.File]::Exists($path)) { [System.IO.File]::Create($path).Dispose() }
  }
  [System.IO.File]::Create($marker).Dispose()
  "{0}: {1:N0} files and {2:N0} folders in {3:N1} s" -f $dir, $files, $folders, ((Get-Date) - $started).TotalSeconds
}

Make 'small' 20 4
Make 'home-3000' 2850 150
Make 'folders-10000' 0 10000
Make 'files-100000' 100000 0
