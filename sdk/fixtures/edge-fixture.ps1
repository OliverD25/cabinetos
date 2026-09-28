# Makes the edge-case fixture that the core's live checks and the shell's
# share (docs/ui.md, "Edge cases"; docs/ipc.md): names beyond ASCII, a path
# longer than 300 characters, and links, under -Root. Needs no
# administrator rights; runs in Windows PowerShell 5.1 and PowerShell 7.
#
#   names\        Cyrillic, Ukrainian letters, Japanese, Chinese, an emoji,
#                 surrogate pairs, cafe with its accent composed (NFC) and
#                 decomposed (NFD) side by side, right-to-left, a 255-unit
#                 name, and in a case-sensitive folder two names that differ
#                 only by case (when Windows lets this user make one)
#   long\         folders nested until the path passes 300 characters, with
#                 a file at the end
#   links\        a junction to link-target\ (the Delete check reads its
#                 files afterwards), and symbolic links to it and to one of
#                 its files when Windows lets this user make them
#                 (Developer Mode, or an elevated prompt)
#   loop\         a junction that points back at loop\ itself: a walk that
#                 follows links never ends here
#   link-target\  the three files the links lead to
#
# Not made here: a volume mount point (only administrators may make one) and
# a cloud placeholder (it needs a sync provider such as OneDrive); the core's
# tests use stand-ins for both.
#
# -Root is removed and made again, but only when it is empty or holds this
# script's marker file, so a mistyped -Root cannot take a real folder with
# it. The removal is `rmdir /s`, which removes a link without following it.
#
#   pwsh -NoProfile -File sdk\fixtures\edge-fixture.ps1 -Root "$env:TEMP\cabinetos-edge"
param([Parameter(Mandatory = $true)][string]$Root)
$ErrorActionPreference = 'Stop'
$marker = '.cabinetos-edge-fixture'
$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
if (Test-Path -LiteralPath $Root) {
  $inside = @(Get-ChildItem -LiteralPath $Root -Force)
  if ($inside.Count -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $Root $marker))) {
    throw "$Root holds files that this script did not make; choose an empty or a new folder"
  }
  & cmd.exe /d /c "rmdir /s /q `"\\?\$Root`""
  if (Test-Path -LiteralPath $Root) { throw "cannot remove the old fixture at $Root" }
}
[void][System.IO.Directory]::CreateDirectory($Root)
$utf8 = New-Object System.Text.UTF8Encoding $false
function Put([string]$Folder, [string]$Name, [string]$Text = 'x') {
  [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Folder, $Name), $Text, $utf8)
}
function Astral([int[]]$CodePoints) { -join ($CodePoints | ForEach-Object { [char]::ConvertFromUtf32($_) }) }
Put $Root $marker 'made by sdk\fixtures\edge-fixture.ps1; the script may remove this folder'

$names = Join-Path $Root 'names'
[void][System.IO.Directory]::CreateDirectory($names)
$fraktur = Astral 0x1D518, 0x1D52B, 0x1D526, 0x1D520, 0x1D52C, 0x1D521, 0x1D522
foreach ($file in 'Звіт 2026.txt', 'Їжак і Єнот.md', '日本語のファイル.txt', "$fraktur.txt", 'مستند.txt') {
  Put $names $file
}
foreach ($folder in 'Ґанок', '中文文件夹', "$(Astral 0x1F4C1) photos") {
  [void][System.IO.Directory]::CreateDirectory((Join-Path $names $folder))
}
# The same name one folder down: copying names\Звіт 2026.txt into Ґанок\ meets
# a conflict.
Put (Join-Path $names 'Ґанок') 'Звіт 2026.txt' 'older'
Put $names "caf$([char]0x00E9).txt" 'NFC'
Put $names "cafe$([char]0x0301).txt" 'NFD'
$longest = ('a' * 251) + '.txt'
Put $names $longest
"255-unit name: $($longest.Length) units"

$case = Join-Path $names 'case'
[void][System.IO.Directory]::CreateDirectory($case)
& fsutil.exe file setCaseSensitiveInfo $case enable 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
  Put $case 'Report.txt' 'upper'
  Put $case 'report.txt' 'lower'
  'case-sensitive folder: yes'
} else {
  Put $case 'Report.txt' 'upper'
  'case-sensitive folder: no (fsutil refused), one Report.txt only'
}

# The \\?\ form: Windows PowerShell's .NET takes a path over 260 characters
# only in that form.
$deep = Join-Path $Root 'long'
$segment = 'segment-of-a-long-path-0123456789'
while ($deep.Length -lt 300) { $deep = Join-Path $deep $segment }
[void][System.IO.Directory]::CreateDirectory("\\?\$deep")
[System.IO.File]::WriteAllText("\\?\$deep\deep file.txt", 'deep', $utf8)
"long path: $($deep.Length + '\deep file.txt'.Length) characters"

$target = Join-Path $Root 'link-target'
[void][System.IO.Directory]::CreateDirectory($target)
foreach ($n in 1..3) { Put $target "kept $n.txt" "kept $n" }
$links = Join-Path $Root 'links'
[void][System.IO.Directory]::CreateDirectory($links)
New-Item -ItemType Junction -Path (Join-Path $links 'junction to target') -Target $target | Out-Null
try {
  New-Item -ItemType SymbolicLink -Path (Join-Path $links 'symlink to target') -Target $target -ErrorAction Stop | Out-Null
  New-Item -ItemType SymbolicLink -Path (Join-Path $links 'symlink to kept 1.txt') -Target (Join-Path $target 'kept 1.txt') -ErrorAction Stop | Out-Null
  'symbolic links: yes'
} catch {
  "symbolic links: no ($($_.Exception.Message))"
}

$loop = Join-Path $Root 'loop'
[void][System.IO.Directory]::CreateDirectory($loop)
Put $loop 'inside.txt' 'inside'
New-Item -ItemType Junction -Path (Join-Path $loop 'back to loop') -Target $loop | Out-Null
"fixture: $Root"
