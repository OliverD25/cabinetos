# Builds a local marketplace index for development and tests: every plugin
# in sdk/fixtures/plugins (a zip of its plugin.json and plugin.wasm) and
# every theme in sdk/themes, each with its SHA-256, into one folder:
#
#   <OutDir>\index.json
#   <OutDir>\files\<id>-<version>.zip    (plugins)
#   <OutDir>\files\<id>-<version>.json   (themes)
#
# With -Collection, the index also offers the theme collection in
# sdk/themes/collection, one item per theme file, in the order of
# sdk/themes/collection/marketplace.csv, whose row gives the item's
# description, license and source link (docs/themes.md, "The collection").
# Without it the index is what it always was: the fixture plugins and the
# shipped themes, five since commander-compact (the UI's end-to-end test
# counted four until the shell adopts the fifth).
#
# With -ThemesOnly, the fixture plugins are left out: the public index
# (ADR 0012) offers themes only until a real plugin exists, and the test
# plugins Hello and friends are not for the public.
#
# With -Extensions, the index also offers the extensions of sdk/extensions:
# each folder there that has an extension.json gives a Core Plugin
# (<extension>\plugin: a zip of plugin.json and plugin.wasm, which
# sdk/extensions/build-extensions.ps1 builds) and, when it names one, a Tool
# Extension (a zip of the tool's folder), as two items that name each other
# in their long descriptions. The extension's plugin is also a fixture for
# the core's tests; the index offers it as the extension's item only, so
# without -Extensions it is not offered at all.
#
# Point the core at it with marketplace.index (the folder, or its
# index.json). Nothing is uploaded or published: the folder stays on this
# machine. The format: sdk/marketplace/index.schema.json and
# docs/marketplace.md.
#
# Run from anywhere, in Windows PowerShell or PowerShell 7:
#   powershell -ExecutionPolicy Bypass -File <repo>\sdk\marketplace\build-index.ps1 -OutDir <folder> [-Collection] [-ThemesOnly] [-Extensions]

param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir,

    # Also pack the theme collection of sdk/themes/collection.
    [switch] $Collection,

    # Leave the fixture plugins out (the public index).
    [switch] $ThemesOnly,

    # Also pack the extensions of sdk/extensions (plugin and tool).
    [switch] $Extensions
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

# One theme file as an index item. The long description adds the theme's
# attribution; $Url, when given, is where the colours come from.
function Add-ThemeItem($File, $Theme, [string] $Description, [string] $License, [string] $Url) {
    $name = "$($Theme.id)-$($Theme.version).json"
    $target = Join-Path $files $name
    Copy-Item -Path $File.FullName -Destination $target -Force
    $long = $Description
    if ($Theme.attribution) { $long = "$long $($Theme.attribution)" }
    $author = [ordered]@{ name = $Theme.author; verified = $false }
    if ($Url) { $author.url = $Url }
    [void]$items.Add([ordered]@{
        id             = $Theme.id
        kind           = 'theme'
        name           = $Theme.name
        author         = $author
        version        = $Theme.version
        description    = $Description
        long           = $long
        size           = (Get-Item $target).Length
        download       = [ordered]@{ url = "files/$name"; sha256 = (Get-Sha256 $target) }
        manifest       = [ordered]@{
            id      = $Theme.id
            name    = $Theme.name
            author  = $Theme.author
            version = $Theme.version
            kind    = $Theme.kind
            accent  = $Theme.accent
        }
        minCoreVersion = '0.1.0'
        license        = $License
    })
}

# One Core Plugin as an index item: a zip of its plugin.json and plugin.wasm.
function Add-PluginItem([string] $ManifestPath, [string] $Component, [string] $Long) {
    $manifest = Read-Json $ManifestPath
    $name = "$($manifest.id)-$($manifest.version).zip"
    $package = Join-Path $files $name
    if (Test-Path $package) { Remove-Item $package }
    $zip = [System.IO.Compression.ZipFile]::Open($package, 'Create')
    try {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $ManifestPath, 'plugin.json')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Component, 'plugin.wasm')
    }
    finally {
        $zip.Dispose()
    }
    $capabilities = @()
    if ($manifest.capabilities) { $capabilities = @($manifest.capabilities) }
    if (-not $Long) { $Long = $manifest.description }
    [void]$items.Add([ordered]@{
        id             = $manifest.id
        kind           = 'plugin'
        name           = $manifest.name
        author         = [ordered]@{ name = $manifest.author; verified = $false }
        version        = $manifest.version
        description    = $manifest.description
        long           = $Long
        size           = (Get-Item $package).Length
        download       = [ordered]@{ url = "files/$name"; sha256 = (Get-Sha256 $package) }
        manifest       = $manifest
        capabilities   = $capabilities
        minCoreVersion = $manifest.minCoreVersion
        license        = 'MIT'
    })
}

# One Tool Extension as an index item: a zip of the tool's folder, with
# tool.json at its root.
function Add-ToolItem([string] $Folder, [string] $Long) {
    $manifest = Read-Json (Join-Path $Folder 'tool.json')
    $name = "$($manifest.id)-$($manifest.version).zip"
    $package = Join-Path $files $name
    if (Test-Path $package) { Remove-Item $package }
    $zip = [System.IO.Compression.ZipFile]::Open($package, 'Create')
    try {
        $root = (Get-Item -LiteralPath $Folder).FullName.TrimEnd('\') + '\'
        Get-ChildItem -LiteralPath $Folder -Recurse -File | ForEach-Object {
            $entry = $_.FullName.Substring($root.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry)
        }
    }
    finally {
        $zip.Dispose()
    }
    if (-not $Long) { $Long = $manifest.description }
    [void]$items.Add([ordered]@{
        id             = $manifest.id
        kind           = 'tool'
        name           = $manifest.name
        author         = [ordered]@{ name = $manifest.author; verified = $false }
        version        = $manifest.version
        description    = $manifest.description
        long           = $Long
        size           = (Get-Item $package).Length
        download       = [ordered]@{ url = "files/$name"; sha256 = (Get-Sha256 $package) }
        manifest       = $manifest
        minCoreVersion = '0.1.0'
        license        = 'MIT'
    })
}

# The extensions of sdk/extensions: a folder with an extension.json, which
# names its plugin folder and, when it has one, its tool folder (relative to
# the extension), and the long descriptions that make the two items name each
# other.
$extensionFolders = @()
$extensionsRoot = Join-Path $sdk 'extensions'
if (Test-Path $extensionsRoot) {
    $extensionFolders = @(Get-ChildItem -Path $extensionsRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'extension.json') })
}
$extensionPluginIds = @{}
foreach ($folder in $extensionFolders) {
    $extension = Read-Json (Join-Path $folder.FullName 'extension.json')
    $extensionPluginIds[$extension.id] = $true
}

if (-not $ThemesOnly) {
    Get-ChildItem -Path (Join-Path $sdk 'fixtures\plugins') -Directory | ForEach-Object {
        $manifestPath = Join-Path $_.FullName 'plugin.json'
        $component = Join-Path $_.FullName 'plugin.wasm'
        if (-not (Test-Path $manifestPath) -or -not (Test-Path $component)) { return }
        # An extension's plugin is offered as the extension's item, or not at all.
        if ($extensionPluginIds.ContainsKey((Read-Json $manifestPath).id)) { return }
        Add-PluginItem $manifestPath $component $null
    }
}

if ($Extensions) {
    foreach ($folder in $extensionFolders) {
        $extension = Read-Json (Join-Path $folder.FullName 'extension.json')
        $plugin = Join-Path $folder.FullName $extension.plugin
        $manifestPath = Join-Path $plugin 'plugin.json'
        $component = Join-Path $plugin 'plugin.wasm'
        if (-not (Test-Path $component)) {
            throw "$component is not built; run sdk\extensions\build-extensions.ps1 first"
        }
        Add-PluginItem $manifestPath $component $extension.long.plugin
        if ($extension.tool) {
            Add-ToolItem (Join-Path $folder.FullName $extension.tool) $extension.long.tool
        }
    }
}

$shipped = @{}
Get-ChildItem -Path (Join-Path $sdk 'themes') -Filter '*.json' |
    Where-Object { $_.Name -ne 'theme.schema.json' } |
    ForEach-Object {
        $theme = Read-Json $_.FullName
        $description = "A $($theme.kind) colour theme."
        if ($theme.kind -eq 'system') { $description = "A colour theme that follows Windows' light or dark mode." }
        Add-ThemeItem $_ $theme $description 'MIT' $null
        $shipped[$theme.id] = $true
    }

if ($Collection) {
    $folder = Join-Path $sdk 'themes\collection'
    $catalog = Join-Path $folder 'marketplace.csv'
    $themeFiles = @{}
    Get-ChildItem -Path $folder -Filter '*.json' | ForEach-Object { $themeFiles[$_.BaseName] = $_ }
    $packed = @{}
    foreach ($row in @(Import-Csv -Path $catalog -Encoding UTF8)) {
        if ($packed.ContainsKey($row.id)) { throw "marketplace.csv lists '$($row.id)' twice" }
        $file = $themeFiles[$row.id]
        if (-not $file) { throw "marketplace.csv lists '$($row.id)', but there is no $($row.id).json in $folder" }
        if ($shipped.ContainsKey($row.id)) { throw "'$($row.id)' ships with the core; the collection must not repeat it" }
        if (-not $row.description -or -not $row.license) { throw "marketplace.csv: '$($row.id)' needs a description and a license" }
        $theme = Read-Json $file.FullName
        if ($theme.id -ne $row.id) { throw "$($file.Name) names the id '$($theme.id)'; it must be its file's name" }
        Add-ThemeItem $file $theme $row.description $row.license $row.source
        $packed[$row.id] = $true
    }
    $missing = @($themeFiles.Keys | Where-Object { -not $packed.ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0) { throw "marketplace.csv has no row for: $($missing -join ', ')" }
}

$index = [ordered]@{
    schemaVersion = 1
    generatedAt   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    items         = @($items)
}
$indexPath = Join-Path $OutDir 'index.json'
[System.IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 20), $utf8)
foreach ($item in $items) {
    Write-Output ("{0,-6} {1,-22} {2,-8} {3,8:N0} bytes  sha256 {4}" -f $item.kind, $item.id, $item.version, $item.size, $item.download.sha256)
}
Write-Output ("{0} items -> {1}" -f $items.Count, $indexPath)
