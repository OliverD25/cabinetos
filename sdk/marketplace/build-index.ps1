# Builds the marketplace's two catalogues, for development and tests and for
# the public site: every plugin in sdk/fixtures/plugins (a zip of its
# plugin.json and plugin.wasm) and every theme in sdk/themes, each with its
# SHA-256, into one folder (ADR 0022):
#
#   <OutDir>\index.json                    (extensions: plugins and tools)
#   <OutDir>\themes.json                   (themes)
#   <OutDir>\files\<id>-<version>.zip      (plugins and tools)
#   <OutDir>\files\<id>-<version>.json     (themes)
#
# The two files have one format (sdk/marketplace/index.schema.json). A theme
# item also says its appearance (the theme file's kind: dark, light or
# system), its density (true when the theme sets metrics, like Commander
# Compact) and its tile: the three colours, #RRGGBB, that the theme gallery
# paints the theme's tile with. The script checks every item against the
# format before it writes (kinds per file, the hash, the version, the tile)
# and stops with a message when one is wrong.
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
# index.json) and marketplace.themes (the folder, or its themes.json).
# Nothing is uploaded or published: the folder stays on this machine. The
# format: sdk/marketplace/index.schema.json and docs/marketplace.md.
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
# A Windows PowerShell 5.1 started from PowerShell 7 inherits 7's module path and then lacks Get-FileHash.
if ($PSVersionTable.PSVersion.Major -lt 6) { $env:PSModulePath = "$PSHOME\Modules;$env:PSModulePath" }
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

# The extensions' items (index.json) and the themes' items (themes.json).
$items = New-Object System.Collections.ArrayList
$themeItems = New-Object System.Collections.ArrayList

# The colours of a theme's gallery tile (ADR 0022). The rows of the window
# are the theme's layerFill laid over its Mica tint, over plain Mica when the
# theme has no tint; their text is textPrimary; the selection pill and the
# check are the accent. A colour is #RRGGBB or #RRGGBBAA; the result is
# always #RRGGBB, so the tile can be painted opaque.
function ConvertFrom-ThemeColor([string] $Text) {
    $hex = $Text.TrimStart('#')
    $alpha = 1.0
    if ($hex.Length -eq 8) { $alpha = [Convert]::ToInt32($hex.Substring(6, 2), 16) / 255.0 }
    , @([Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16), $alpha)
}

# $Top laid over $Under, scaled by $Opacity as well: the result is opaque.
function Merge-ThemeColor($Top, $Under, [double] $Opacity = 1.0) {
    $a = $Top[3] * $Opacity
    $out = @(0, 0, 0)
    for ($i = 0; $i -lt 3; $i++) { $out[$i] = [int][Math]::Round($Top[$i] * $a + $Under[$i] * (1 - $a)) }
    , @($out[0], $out[1], $out[2], 1.0)
}

function ConvertTo-ThemeHex($Color) {
    '#{0:X2}{1:X2}{2:X2}' -f [int]$Color[0], [int]$Color[1], [int]$Color[2]
}

function Get-ThemeTile($Theme) {
    # Plain Mica: Windows 11's own backdrop colour. A system theme's palette is
    # its dark-mode look (docs/themes.md), so it is painted on dark Mica.
    $mica = ConvertFrom-ThemeColor '#202020'
    $defaultAccent = '#60CDFF'
    if ($Theme.kind -eq 'light') {
        $mica = ConvertFrom-ThemeColor '#F3F3F3'
        $defaultAccent = '#005FB8'
    }
    if ($Theme.mica) {
        $mica = Merge-ThemeColor (ConvertFrom-ThemeColor $Theme.mica.tint) $mica ([double]$Theme.mica.opacity)
    }
    $background = Merge-ThemeColor (ConvertFrom-ThemeColor $Theme.palette.layerFill) $mica
    $text = Merge-ThemeColor (ConvertFrom-ThemeColor $Theme.palette.textPrimary) $background
    $accent = $defaultAccent
    if ($Theme.accent) { $accent = $Theme.accent }
    [ordered]@{
        background = (ConvertTo-ThemeHex $background)
        text       = (ConvertTo-ThemeHex $text)
        accent     = (ConvertTo-ThemeHex (ConvertFrom-ThemeColor $accent))
    }
}

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
    $density = $false
    if ($Theme.metrics -and @($Theme.metrics.PSObject.Properties).Count -gt 0) { $density = $true }
    [void]$themeItems.Add([ordered]@{
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
        appearance     = $Theme.kind
        density        = $density
        tile           = (Get-ThemeTile $Theme)
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

# The format's rules, checked before anything is written (the core applies the
# same ones when it reads a file, and leaves out an item that breaks them).
function Assert-Catalogue([string] $File, $Items, [string[]] $Kinds) {
    $seen = @{}
    foreach ($item in $Items) {
        $where = "$File item '$($item.id)'"
        if ($item.id -notmatch '^[a-z][a-z0-9-]{0,63}$') { throw "${where}: the id is not 1 to 64 lower case letters, digits and -" }
        if ($item.version -notmatch '^\d+\.\d+\.\d+$') { throw "${where}: version '$($item.version)' is not major.minor.patch" }
        if ($Kinds -notcontains $item.kind) { throw "${where}: kind '$($item.kind)' does not belong in $File (it holds $($Kinds -join ', '))" }
        if ($seen.ContainsKey("$($item.id)@$($item.version)")) { throw "${where}: listed twice in $File" }
        $seen["$($item.id)@$($item.version)"] = $true
        if ($item.download.sha256 -notmatch '^[0-9a-f]{64}$') { throw "${where}: sha256 is not 64 hex digits" }
        if ($item.size -le 0) { throw "${where}: size is 0" }
        if (-not $item.name -or -not $item.author.name) { throw "${where}: name and author are needed" }
        if ($item.kind -eq 'theme') {
            if (@('dark', 'light', 'system') -notcontains $item.appearance) { throw "${where}: appearance '$($item.appearance)' is not dark, light or system" }
            if ($item.density -isnot [bool]) { throw "${where}: density must be true or false" }
            foreach ($key in 'background', 'text', 'accent') {
                if ($item.tile[$key] -notmatch '^#[0-9A-F]{6}$') { throw "${where}: tile.$key '$($item.tile[$key])' is not #RRGGBB" }
            }
        }
    }
}
Assert-Catalogue 'index.json' $items @('plugin', 'tool')
Assert-Catalogue 'themes.json' $themeItems @('theme')

function Write-Catalogue([string] $File, $Items) {
    $catalogue = [ordered]@{
        schemaVersion = 1
        generatedAt   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        items         = @($Items)
    }
    $path = Join-Path $OutDir $File
    [System.IO.File]::WriteAllText($path, ($catalogue | ConvertTo-Json -Depth 20), $utf8)
    foreach ($item in $Items) {
        Write-Output ("{0,-6} {1,-22} {2,-8} {3,8:N0} bytes  sha256 {4}" -f $item.kind, $item.id, $item.version, $item.size, $item.download.sha256)
    }
    Write-Output ("{0} items -> {1}" -f @($Items).Count, $path)
}
Write-Catalogue 'index.json' $items
Write-Catalogue 'themes.json' $themeItems
