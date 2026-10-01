#Requires -Version 7.2
# Builds a CabinetOS release on this machine. It publishes nothing: the
# results stay in dist\, which git ignores.
#
#   dist\CabinetOS-<version>-win-x64\            the folder a user installs
#   dist\CabinetOS-<version>-win-x64.zip         that folder's contents, zipped
#   dist\CabinetOS-<version>-win-x64.zip.sha256  the zip's SHA-256
#   dist\winget\<version>\                       winget manifests with that hash
#   dist\update\<channel>\latest.json            what the in-app updater reads
#   dist\update\<channel>\notes-<version>.md     the release notes it shows
#
# The version has one source: `version` in [workspace.package] of
# core\Cargo.toml. ui\Directory.Build.props must carry the same <Version>
# (the window's version); the script stops when they differ, and
# -SyncVersion copies the Cargo version into the props file first.
#
# Steps:
#  1. cargo build --release of cabinetos-core, cabinetos-indexer and
#     cabinetos-cli (core\.cargo\config.toml links the C runtime in).
#  2. dotnet publish of ui\CabinetOS: Release, win-x64, framework-dependent
#     on .NET and on the Windows App Runtime of the machine, with ReadyToRun
#     (the IL compiled to native code ahead of time, so the start spends less
#     time in the JIT; the window's folder grows from 41 to 57 MB). The project
#     keeps the Windows App SDK's MSIX tooling on, which writes CabinetOS.pri,
#     the compiled XAML, into the publish; the app stays unpackaged.
#  3. The three programs and their .pdb files next to CabinetOS.exe, where
#     the window's launcher looks first; crash traces read the .pdb files
#     for file and line. cabinetos-cli.exe once more as cab.exe, the short
#     name to type (the same program; its .pdb serves both).
#  4. extras\: copies of the four built-in themes with their schema, and
#     the Markdown Preview tool, which is opt-in (Constitution Article 10).
#  5. LICENSE, THIRD-PARTY-NOTICES.md (build\notices.ps1), install.ps1,
#     uninstall.ps1, and release.json: the version, the commit, and the
#     runtimes the build needs, which install.ps1 checks.
#  6. The zip, its SHA-256, and the winget manifests of build\winget with
#     this version, URL and hash.
#  7. The in-app update's files for -Channel (stable unless it says
#     preview): latest.json with the zip's address on the GitHub Release,
#     its hash and size, the notes' address on the marketplace site and the
#     runtimes release.json names; and the notes, the CHANGELOG.md section
#     of this version (or the Unreleased section, with a warning, while the
#     version has none). docs\release.md, "Publish", says where they go.
#
# -PackageOnly skips steps 1-5 and zips the existing folder again, for
# example after signing its programs (docs\release.md).
#
# Run from anywhere, in PowerShell 7:
#   pwsh -File <repo>\build\release.ps1 [-SyncVersion] [-PackageOnly] [-Channel stable|preview]

param(
    [switch] $SyncVersion,
    [switch] $PackageOnly,
    [ValidateSet('stable', 'preview')]
    [string] $Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repo = Split-Path $PSScriptRoot -Parent
$cargoToml = Join-Path $repo 'core\Cargo.toml'
$props = Join-Path $repo 'ui\Directory.Build.props'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Invoke-Native([string] $Program, [string[]] $Arguments, [string] $Directory) {
    Write-Host "> $Program $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location -LiteralPath $Directory
    try {
        & $Program @Arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

function Get-WorkspaceValue([string] $Key) {
    $text = [System.IO.File]::ReadAllText($cargoToml)
    $section = [regex]::Match($text, '(?ms)^\[workspace\.package\][^\n]*\n(.*?)(?=^\[)')
    if (-not $section.Success) { throw 'core\Cargo.toml has no [workspace.package] section' }
    $value = [regex]::Match($section.Groups[1].Value, "(?m)^$Key\s*=\s*`"([^`"]+)`"")
    if (-not $value.Success) { throw "core\Cargo.toml has no $Key in [workspace.package]" }
    $value.Groups[1].Value
}

# --- The version ------------------------------------------------------

$version = Get-WorkspaceValue 'version'
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "core\Cargo.toml's version '$version' is not x.y.z or x.y.z-tag" }
if ($Channel -eq 'stable' -and $version.Contains('-')) {
    throw "The stable channel never offers a pre-release, and $version is one. Build it with -Channel preview."
}
$propsText = [System.IO.File]::ReadAllText($props)
$propsVersions = [regex]::Matches($propsText, '<Version>([^<]*)</Version>')
if ($propsVersions.Count -ne 1) { throw 'ui\Directory.Build.props must hold exactly one <Version>' }
$uiVersion = $propsVersions[0].Groups[1].Value
if ($uiVersion -ne $version) {
    if (-not $SyncVersion) {
        throw "The versions differ: core\Cargo.toml says $version, ui\Directory.Build.props says $uiVersion. Run again with -SyncVersion to write $version into the props file, and commit it."
    }
    [System.IO.File]::WriteAllText($props, $propsText.Replace("<Version>$uiVersion</Version>", "<Version>$version</Version>"), $utf8)
    Write-Host "ui\Directory.Build.props: <Version> $uiVersion -> $version (commit this change)"
}
if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "version=$version" }

$name = "CabinetOS-$version-win-x64"
$dist = Join-Path $repo 'dist'
$folder = Join-Path $dist $name
$zip = "$folder.zip"
$wingetOut = Join-Path $dist "winget\$version"

if (-not $PackageOnly) {
    $commit = ''
    $dirty = $false
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $commit = (git -C $repo rev-parse HEAD).Trim()
        # Only the folders the build reads; untracked files count, because
        # the C# projects compile every .cs file in their folders.
        $dirty = [bool](git -C $repo status --porcelain -- core ui sdk build LICENSE)
        if ($dirty) { Write-Warning 'The working tree has uncommitted changes, and this build includes them.' }
    }
    foreach ($old in $folder, $zip, "$zip.sha256", $wingetOut) {
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    }

    # 1-2. Build.
    Invoke-Native 'cargo' @('build', '--release', '--locked', '-p', 'cabinetos-core', '-p', 'cabinetos-indexer', '-p', 'cabinetos-cli') (Join-Path $repo 'core')
    Invoke-Native 'dotnet' @('publish', 'CabinetOS\CabinetOS.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true', '-o', $folder) (Join-Path $repo 'ui')
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'CabinetOS.pri'))) {
        throw 'The publish has no CabinetOS.pri, so the window could not load its XAML.'
    }

    # 3. The core's programs.
    $binaries = Join-Path $repo 'core\target\release'
    foreach ($program in 'cabinetos-core', 'cabinetos-indexer', 'cabinetos-cli') {
        Copy-Item -LiteralPath (Join-Path $binaries "$program.exe") -Destination $folder
        Copy-Item -LiteralPath (Join-Path $binaries "$($program.Replace('-', '_')).pdb") -Destination $folder
    }
    Copy-Item -LiteralPath (Join-Path $binaries 'cabinetos-cli.exe') -Destination (Join-Path $folder 'cab.exe')

    # 4. Extras.
    $themes = New-Item -ItemType Directory -Force -Path (Join-Path $folder 'extras\themes')
    Copy-Item -Path (Join-Path $repo 'sdk\themes\*.json') -Destination $themes
    $tools = New-Item -ItemType Directory -Force -Path (Join-Path $folder 'extras\tools')
    Copy-Item -LiteralPath (Join-Path $repo 'sdk\tools\markdown-preview') -Destination $tools -Recurse

    # 5. Documents, scripts, and the facts install.ps1 checks.
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $folder
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $folder
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $folder

    $options = (Get-Content -Raw -LiteralPath (Join-Path $folder 'CabinetOS.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions
    $frameworks = if ($options.PSObject.Properties['frameworks']) { @($options.frameworks) } else { @($options.framework) }
    $assets = Get-Content -Raw -LiteralPath (Join-Path $repo 'ui\CabinetOS\obj\project.assets.json') | ConvertFrom-Json -AsHashtable
    $runtimeKey = @($assets.libraries.Keys | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' })
    if ($runtimeKey.Count -ne 1) { throw 'project.assets.json does not name one Microsoft.WindowsAppSDK.Runtime package' }
    $header = [System.IO.File]::ReadAllText((Join-Path @($assets.packageFolders.Keys)[0] "$($assets.libraries[$runtimeKey[0]].path)\include\WindowsAppSDK-VersionInfo.h"))
    $define = { param($Name) [regex]::Match($header, "#define $Name\s+(`"?)([^`"\s]+)\1").Groups[2].Value }
    $build = [version] ([regex]::Match($propsText, '<SupportedOSPlatformVersion>([^<]+)<').Groups[1].Value)
    $facts = [ordered]@{
        product            = 'CabinetOS'
        version            = $version
        commit             = $commit
        uncommittedChanges = $dirty
        builtUtc           = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        requires           = [ordered]@{
            windowsBuild      = $build.Build
            dotnet            = @($frameworks | ForEach-Object { [ordered]@{ name = $_.name; version = $_.version } })
            windowsAppRuntime = [ordered]@{
                version       = (& $define 'WINDOWSAPPSDK_RUNTIME_VERSION_DOTQUADSTRING')
                packageFamily = (& $define 'WINDOWSAPPSDK_RUNTIME_PACKAGE_FRAMEWORK_PACKAGEFAMILYNAME')
                installer     = "https://aka.ms/windowsappsdk/$(& $define 'WINDOWSAPPSDK_RELEASE_MAJOR').$(& $define 'WINDOWSAPPSDK_RELEASE_MINOR')/latest/windowsappruntimeinstall-x64.exe"
            }
            webView2          = $true
        }
    }
    [System.IO.File]::WriteAllText((Join-Path $folder 'release.json'), ($facts | ConvertTo-Json -Depth 6) + "`n", $utf8)

    & (Join-Path $PSScriptRoot 'notices.ps1') -Release $folder -Version $version -Out (Join-Path $folder 'THIRD-PARTY-NOTICES.md')
}
elseif (-not (Test-Path -LiteralPath $folder)) {
    throw "$folder does not exist; build it first (without -PackageOnly)"
}

# --- 6. The zip, its hash, the winget manifests -----------------------

$built = (Get-Content -Raw -LiteralPath (Join-Path $folder 'release.json') | ConvertFrom-Json).version
if ($built -ne $version) { throw "$folder holds version $built, not $version" }
Add-Type -AssemblyName System.IO.Compression.ZipFile
foreach ($old in $zip, "$zip.sha256", $wingetOut) {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}
# The files sit at the zip's root: Explorer's "Extract All" then makes one
# folder named after the zip, not a folder inside a folder.
[System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
[System.IO.File]::WriteAllText("$zip.sha256", "$($hash.ToLowerInvariant())  $name.zip`n", $utf8)

$repository = (Get-WorkspaceValue 'repository').TrimEnd('/')
New-Item -ItemType Directory -Force -Path $wingetOut | Out-Null
foreach ($manifest in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'winget') -Filter '*.yaml') {
    $text = [System.IO.File]::ReadAllText($manifest.FullName)
    $text = $text -replace '(?m)^(\s*PackageVersion:\s*).*$', "`${1}$version"
    $text = $text -replace '(?m)^(\s*InstallerUrl:\s*).*$', "`${1}$repository/releases/download/v$version/$name.zip"
    $text = $text -replace '(?m)^(\s*InstallerSha256:\s*).*$', "`${1}$hash"
    $text = $text -replace '(?m)^(\s*ReleaseNotesUrl:\s*).*$', "`${1}$repository/releases/tag/v$version"
    $text = $text -replace '(?m)^(\s*ReleaseDate:\s*).*$', "`${1}$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))"
    [System.IO.File]::WriteAllText((Join-Path $wingetOut $manifest.Name), $text, $utf8)
}
if (Get-Command winget -ErrorAction SilentlyContinue) {
    Invoke-Native 'winget' @('validate', '--manifest', $wingetOut, '--disable-interactivity') $repo
}
else {
    Write-Host 'winget is not installed here; the manifests were not validated.'
}

# --- 7. The in-app update: latest.json and the notes ------------------

$updateOut = Join-Path $dist "update\$Channel"
New-Item -ItemType Directory -Force -Path $updateOut | Out-Null
$site = 'https://oliverd25.github.io/cabinetos-marketplace/update'
$notesName = "notes-$version.md"

# The notes: the CHANGELOG.md text under "## [<version>]", up to the next
# "## [" heading; while the version has no section yet, the Unreleased one.
$changelog = [System.IO.File]::ReadAllText((Join-Path $repo 'CHANGELOG.md')).Replace("`r`n", "`n")
function Get-ChangelogSection([string] $Heading) {
    $match = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($Heading))\][^\n]*\n(.*?)(?=^## \[|\z)")
    if ($match.Success) { $match.Groups[1].Value.Trim() } else { $null }
}
$notes = Get-ChangelogSection $version
if (-not $notes) {
    $notes = Get-ChangelogSection 'Unreleased'
    if (-not $notes) { throw "CHANGELOG.md has neither a ## [$version] nor a ## [Unreleased] section to publish as the release notes" }
    Write-Warning "CHANGELOG.md has no ## [$version] section yet, so the notes are the Unreleased section. Rename it to ## [$version] - <date> before publishing (docs\release.md, Publish)."
}
# The link definitions of the version headings ("[0.1.0]: https://…") belong
# to the whole file, not to the section.
$notes = ($notes -replace '(?m)^\[(Unreleased|\d+\.\d+\.\d+[^\]]*)\]:[ \t]+\S+[ \t]*$', '').Trim()
[System.IO.File]::WriteAllText((Join-Path $updateOut $notesName), $notes + "`n", $utf8)

# The runtimes, major.minor, from the release's own facts.
$requires = (Get-Content -Raw -LiteralPath (Join-Path $folder 'release.json') | ConvertFrom-Json).requires
$majorMinor = { param($Text) $parsed = [version] ($Text -split '-')[0]; "$($parsed.Major).$($parsed.Minor)" }
$dotnet = @($requires.dotnet | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }) + @($requires.dotnet) | Select-Object -First 1
$latest = [ordered]@{
    schemaVersion = 1
    channel       = $Channel
    version       = $version
    published     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    zip           = [ordered]@{
        url    = "$repository/releases/download/v$version/$name.zip"
        sha256 = $hash.ToLowerInvariant()
        size   = (Get-Item -LiteralPath $zip).Length
    }
    notes         = [ordered]@{ url = "$site/$Channel/$notesName" }
    requires      = [ordered]@{
        windowsAppRuntime = (& $majorMinor $requires.windowsAppRuntime.version)
        dotnet            = (& $majorMinor $dotnet.version)
    }
}
$latestPath = Join-Path $updateOut 'latest.json'
[System.IO.File]::WriteAllText($latestPath, ($latest | ConvertTo-Json -Depth 4) + "`n", $utf8)
$schema = Join-Path $repo 'sdk\update\latest.schema.json'
if (-not (Test-Json -LiteralPath $latestPath -SchemaFile $schema)) {
    throw "$latestPath does not follow $schema"
}

$files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
Write-Host ''
Write-Host ("Release folder: {0} ({1} files, {2:N1} MB)" -f $folder, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
Write-Host ("Zip:            {0} ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
Write-Host "SHA-256:        $($hash.ToLowerInvariant())"
Write-Host "winget:         $wingetOut"
Write-Host "Update ($Channel): $latestPath and $notesName"
