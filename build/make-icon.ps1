# Makes CabinetOS.ico, the app's icon, from the design's size cuts in
# docs\design\icons (ICON_HANDOFF.md: 16, 24, 32, 48 and 256 px, each cut
# kept as the PNG it is, never a scaled master; Windows reads PNG at every
# size).
#
# ui\CabinetOS\Assets\CabinetOS.ico is the file this makes. It is committed,
# so the window's build (<ApplicationIcon> in ui\CabinetOS\CabinetOS.csproj)
# and release.ps1 need no script step. Run this again when the icons in
# docs\design\icons change, and commit the result. The same bytes come out
# every time.
#
# Run from anywhere, in PowerShell 7 or Windows PowerShell:
#   pwsh -NoProfile -File <repo>\build\make-icon.ps1 [-Path <file>]

param(
    # Where to write the .ico; the committed file when left out.
    [string] $Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Path) { $Path = Join-Path $repo 'ui\CabinetOS\Assets\CabinetOS.ico' }

$sizes = 16, 24, 32, 48, 256
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) { $images.Add([System.IO.File]::ReadAllBytes((Join-Path $repo "docs\design\icons\cabinetos-$size.png"))) }
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16] 0)
$writer.Write([uint16] 1)
$writer.Write([uint16] $sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    # 0 means 256 in an icon directory entry.
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte] $side)
    $writer.Write([byte] $side)
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] 32)
    $writer.Write([uint32] $images[$i].Length)
    $writer.Write([uint32] $offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

$folder = Split-Path -Parent $Path
if ($folder -and -not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
[System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
Write-Host "$Path ($($stream.Length) bytes: $($sizes -join ', ') px)"
