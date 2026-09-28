# Builds a local marketplace index for development and tests: every plugin
# in sdk/fixtures/plugins (a zip of its plugin.json and plugin.wasm) and
# every theme in sdk/themes, each with its SHA-256, into one folder:
#
#   <OutDir>\index.json
#   <OutDir>\files\<id>-<version>.zip    (plugins)
#   <OutDir>\files\<id>-<version>.json   (themes)
#
# Point the core at it with marketplace.index (the folder, or its
# index.json). Nothing is uploaded or published: the folder stays on this
# machine. The format: sdk/marketplace/index.schema.json and
# docs/marketplace.md.
#
# Run from anywhere, in Windows PowerShell or PowerShell 7:
#   powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder>

param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sdk = Split-Path $PSScriptRoot -Parent
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$files = Join-Path $OutDir 'files'
New-Item -ItemType Directory -Force -Path $files | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function Read-Json([string] $Path) {
    # -Encoding UTF8: Windows PowerShell would otherwise read the ANSI code page.
    Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

$items = New-Object System.Collections.ArrayList

Get-ChildItem -Path (Join-Path $sdk 'fixtures\plugins') -Directory | ForEach-Object {
    $manifestPath = Join-Path $_.FullName 'plugin.json'
    $component = Join-Path $_.FullName 'plugin.wasm'
    if (-not (Test-Path $manifestPath) -or -not (Test-Path $component)) { return }
    $manifest = Read-Json $manifestPath
    $name = "$($manifest.id)-$($manifest.version).zip"
    $package = Join-Path $files $name
    if (Test-Path $package) { Remove-Item $package }
    $zip = [System.IO.Compression.ZipFile]::Open($package, 'Create')
    try {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $manifestPath, 'plugin.json')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $component, 'plugin.wasm')
    }
    finally {
        $zip.Dispose()
    }
    $capabilities = @()
    if ($manifest.capabilities) { $capabilities = @($manifest.capabilities) }
    [void]$items.Add([ordered]@{
        id             = $manifest.id
        kind           = 'plugin'
        name           = $manifest.name
        author         = [ordered]@{ name = $manifest.author; verified = $false }
        version        = $manifest.version
        description    = $manifest.description
        long           = $manifest.description
        size           = (Get-Item $package).Length
        download       = [ordered]@{ url = "files/$name"; sha256 = (Get-Sha256 $package) }
        manifest       = $manifest
        capabilities   = $capabilities
        minCoreVersion = $manifest.minCoreVersion
        license        = 'MIT'
    })
}

Get-ChildItem -Path (Join-Path $sdk 'themes') -Filter '*.json' |
    Where-Object { $_.Name -ne 'theme.schema.json' } |
    ForEach-Object {
        $theme = Read-Json $_.FullName
        $name = "$($theme.id)-$($theme.version).json"
        $file = Join-Path $files $name
        Copy-Item -Path $_.FullName -Destination $file -Force
        $long = "A $($theme.kind) colour theme."
        if ($theme.attribution) { $long = "$long $($theme.attribution)" }
        [void]$items.Add([ordered]@{
            id             = $theme.id
            kind           = 'theme'
            name           = $theme.name
            author         = [ordered]@{ name = $theme.author; verified = $false }
            version        = $theme.version
            description    = "A $($theme.kind) colour theme."
            long           = $long
            size           = (Get-Item $file).Length
            download       = [ordered]@{ url = "files/$name"; sha256 = (Get-Sha256 $file) }
            manifest       = [ordered]@{
                id      = $theme.id
                name    = $theme.name
                author  = $theme.author
                version = $theme.version
                kind    = $theme.kind
                accent  = $theme.accent
            }
            minCoreVersion = '0.1.0'
            license        = 'MIT'
        })
    }

$index = [ordered]@{
    schemaVersion = 1
    generatedAt   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    items         = @($items)
}
$indexPath = Join-Path $OutDir 'index.json'
[System.IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 20), $utf8)
foreach ($item in $items) {
    Write-Output ("{0,-6} {1,-18} {2,-8} {3,8:N0} bytes  sha256 {4}" -f $item.kind, $item.id, $item.version, $item.size, $item.download.sha256)
}
Write-Output ("{0} items -> {1}" -f $items.Count, $indexPath)
