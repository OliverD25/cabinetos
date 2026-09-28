# Makes the shell's edge-case fixture (docs/ui.md, "Edge cases"): names beyond ASCII, a path
# longer than 260 characters, and links, under -Root. The shell's own until the core's fixture
# script under sdk\fixtures\ covers the same; nothing here needs administrator rights.
#   names\   Cyrillic, Ukrainian letters, Japanese, Chinese, an emoji, surrogate pairs, café in
#            NFC and in NFD side by side, right-to-left, a 255-unit name, and names that differ
#            only by case (in a case-sensitive folder, when Windows lets this user make one)
#   long\    folders nested until the path passes 300 characters, with a file at the end
#   links\   a junction to a folder with files (the Delete check reads them afterwards), and a
#            symbolic link when Windows lets this user make one (Developer Mode)
param([Parameter(Mandatory = $true)][string]$Root)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
$names = New-Item -ItemType Directory -Force -Path (Join-Path $Root 'names')
$utf8 = New-Object System.Text.UTF8Encoding $false
function Put([string]$Folder, [string]$Name, [string]$Text = 'x') { [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Folder, $Name), $Text, $utf8) }

foreach ($file in 'Звіт 2026.txt', 'Їжак і Єнот.md', '日本語のファイル.txt', "$([char]::ConvertFromUtf32(0x1D518))$([char]::ConvertFromUtf32(0x1D52B))$([char]::ConvertFromUtf32(0x1D526))$([char]::ConvertFromUtf32(0x1D520))$([char]::ConvertFromUtf32(0x1D52C))$([char]::ConvertFromUtf32(0x1D521))$([char]::ConvertFromUtf32(0x1D522)).txt", 'مستند.txt') {
  Put $names $file
}
foreach ($folder in 'Ґанок', '中文文件夹', "$([char]::ConvertFromUtf32(0x1F4C1)) photos") { New-Item -ItemType Directory -Force -Path (Join-Path $names $folder) | Out-Null }
# The same name in a folder of its own: F5 from names\ into Ґанок\ raises the conflict card.
Put (Join-Path $names 'Ґанок') 'Звіт 2026.txt' 'older'
Put $names "caf$([char]0x00E9).txt" 'NFC'
Put $names "cafe$([char]0x0301).txt" 'NFD'
$long = ('a' * 251) + '.txt'
Put $names $long
"255-unit name: $($long.Length) units"

$case = New-Item -ItemType Directory -Force -Path (Join-Path $names 'case')
& fsutil.exe file setCaseSensitiveInfo $case.FullName enable 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
  Put $case 'Report.txt' 'upper'
  Put $case 'report.txt' 'lower'
  "case-sensitive folder: yes"
} else {
  Put $case 'Report.txt' 'upper'
  "case-sensitive folder: no (fsutil refused), one Report.txt only"
}

$deep = Join-Path $Root 'long'
$segment = 'segment-of-a-long-path-0123456789'
while ($deep.Length -lt 300) { $deep = Join-Path $deep $segment }
[void][System.IO.Directory]::CreateDirectory($deep)
Put $deep 'deep file.txt' 'deep'
"long path: $($deep.Length) characters"

$links = New-Item -ItemType Directory -Force -Path (Join-Path $Root 'links')
$target = New-Item -ItemType Directory -Force -Path (Join-Path $Root 'link-target')
foreach ($n in 1..3) { Put $target.FullName "kept $n.txt" "kept $n" }
New-Item -ItemType Junction -Path (Join-Path $links 'junction to target') -Target $target.FullName | Out-Null
try {
  New-Item -ItemType SymbolicLink -Path (Join-Path $links 'symlink to target') -Target $target.FullName -ErrorAction Stop | Out-Null
  New-Item -ItemType SymbolicLink -Path (Join-Path $links 'symlink to kept 1.txt') -Target (Join-Path $target.FullName 'kept 1.txt') -ErrorAction Stop | Out-Null
  "symbolic links: yes"
} catch { "symbolic links: no ($($_.Exception.Message))" }
"fixture: $Root"
