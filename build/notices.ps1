#Requires -Version 7.2
# Writes THIRD-PARTY-NOTICES.md for a release folder: every third-party
# component that ships in it, its license, and the full license texts, as
# the MIT, BSD and Apache licenses require of anyone who passes the
# software on.
#
# Where the components come from:
# - Rust crates: every crate that cabinetos-core, cabinetos-indexer and
#   cabinetos-cli are built from (normal and build dependencies for the
#   x86_64-pc-windows-msvc target, from `cargo metadata`), with the
#   LICENSE*, LICENCE*, COPYING*, NOTICE* and UNLICENSE* files of each
#   crate's folder. A crate that ships no license file gets the text of a
#   crate from the same repository with the same license; an Apache-2.0
#   crate, whose license text names no copyright holder, may take it from
#   any crate with the same license.
# - The Rust standard library, compiled into those three programs.
# - NuGet packages: every package in the release's CabinetOS.deps.json,
#   with the license and notice files at the package's root.
# - The .NET application host that is the start of CabinetOS.exe.
# - Bundled JavaScript: xterm.js and its fit add-on (the terminal page),
#   and marked (Markdown Preview).
#
# A text used by several components is printed once, with the list of its
# users. The script stops when a component has no license text, so a
# release never ships incomplete notices.
#
# release.ps1 calls it; to run it alone, in PowerShell 7:
#   pwsh -File <repo>\build\notices.ps1 -Release <release folder> -Version <x.y.z> -Out <file>

param(
    [Parameter(Mandatory = $true)] [string] $Release,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $Out
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repo = Split-Path $PSScriptRoot -Parent
$crateLicenseFile = '^(licen[cs]e|copying|notice|unlicense)'
$packageLicenseFile = '^(licen[cs]e|notice|third[-_ ]?party[-_ ]?notices?)'

$texts = [ordered]@{}
$rows = [System.Collections.Generic.List[object]]::new()

function Read-Text([string] $Path) {
    [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

# Runs a program and returns its standard output, read as UTF-8 whatever
# the console's code page is: crate metadata carries non-ASCII names.
function Invoke-Captured([string] $Program, [string[]] $Arguments, [string] $Directory) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Program)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $process = [System.Diagnostics.Process]::Start($info)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "$Program $($Arguments -join ' ') failed with exit code $($process.ExitCode): $($errors.Result)"
    }
    $output
}

function Get-Normalized([string] $Text) {
    $lines = $Text.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n") | ForEach-Object { $_.TrimEnd() }
    ($lines -join "`n").Trim("`n")
}

# Stores a license text once and returns its ID (T1, T2, ...).
function Add-Text([string] $Text, [string] $User) {
    $normal = Get-Normalized $Text
    if (-not $normal) { throw "the license text for $User is empty" }
    $key = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($normal)))
    if (-not $texts.Contains($key)) {
        $texts[$key] = [pscustomobject]@{
            Id    = "T$($texts.Count + 1)"
            Text  = $normal
            Users = [System.Collections.Generic.List[string]]::new()
        }
    }
    $entry = $texts[$key]
    if (-not $entry.Users.Contains($User)) { $entry.Users.Add($User) }
    $entry.Id
}

function Add-Row([string] $Section, [string] $Name, [string] $Version, [string] $License, [string[]] $Ids, [string] $Note) {
    $rows.Add([pscustomobject]@{
        Section = $Section
        Name    = $Name
        Version = $Version
        License = $License
        Ids     = @($Ids | Select-Object -Unique)
        Note    = $Note
    })
}

function Get-Repository($Crate) {
    if (-not $Crate.repository) { return $null }
    ($Crate.repository.ToLowerInvariant().TrimEnd('/') -replace '\.git$', '') -replace '/tree/.*$', ''
}

# --- Rust crates -------------------------------------------------------

$core = Join-Path $repo 'core'
$metadata = Invoke-Captured 'cargo' @('metadata', '--format-version', '1', '--filter-platform', 'x86_64-pc-windows-msvc', '--locked') $core |
    ConvertFrom-Json -AsHashtable
$packages = @{}
foreach ($package in $metadata.packages) { $packages[$package.id] = $package }
$nodes = @{}
foreach ($node in $metadata.resolve.nodes) { $nodes[$node.id] = $node }

$programs = 'cabinetos-core', 'cabinetos-indexer', 'cabinetos-cli'
$pending = [System.Collections.Generic.Stack[string]]::new()
foreach ($id in $metadata.workspace_members) {
    if ($packages[$id].name -in $programs) { $pending.Push($id) }
}
if ($pending.Count -ne $programs.Count) { throw "cargo metadata does not list $($programs -join ', ')" }
$closure = [System.Collections.Generic.HashSet[string]]::new()
while ($pending.Count -gt 0) {
    $id = $pending.Pop()
    if (-not $closure.Add($id)) { continue }
    foreach ($dependency in $nodes[$id].deps) {
        # Dev dependencies build only the tests; normal and build ones go into (or make) the programs.
        if ($dependency.dep_kinds | Where-Object { $null -eq $_.kind -or $_.kind -eq 'build' }) {
            $pending.Push($dependency.pkg)
        }
    }
}

# The workspace's own crates are CabinetOS, covered by LICENSE.
$crates = @($closure | ForEach-Object { $packages[$_] } | Where-Object { $null -ne $_.source } |
    Sort-Object { $_.name }, { $_.version })

$crateFiles = @{}
foreach ($crate in $crates) {
    $folder = Split-Path $crate.manifest_path -Parent
    $files = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match $crateLicenseFile |
        Sort-Object Name | ForEach-Object { $files.Add($_.FullName) }
    $reuse = Join-Path $folder 'LICENSES'
    if (Test-Path -LiteralPath $reuse -PathType Container) {
        Get-ChildItem -LiteralPath $reuse -File | Sort-Object Name | ForEach-Object { $files.Add($_.FullName) }
    }
    if ($crate.license_file) {
        $declared = [System.IO.Path]::GetFullPath((Join-Path $folder $crate.license_file))
        if ((Test-Path -LiteralPath $declared -PathType Leaf) -and -not $files.Contains($declared)) { $files.Add($declared) }
    }
    $crateFiles[$crate.id] = $files
}

foreach ($crate in $crates) {
    $user = "$($crate.name) $($crate.version)"
    $source = $crate
    if ($crateFiles[$crate.id].Count -eq 0) {
        $repository = Get-Repository $crate
        $source = $crates | Where-Object {
            $crateFiles[$_.id].Count -gt 0 -and $_.license -eq $crate.license -and $repository -and (Get-Repository $_) -eq $repository
        } | Select-Object -First 1
        if (-not $source -and $crate.license -match '^Apache-2\.0( WITH LLVM-exception)?$') {
            $source = $crates | Where-Object { $crateFiles[$_.id].Count -gt 0 -and $_.license -eq $crate.license } |
                Select-Object -First 1
        }
        if (-not $source) {
            throw "$user ships no license file, and no crate of its repository with the same license has one; add its text to build\notices.ps1 by hand"
        }
    }
    $ids = foreach ($file in $crateFiles[$source.id]) { Add-Text (Read-Text $file) $user }
    $license = if ($crate.license) { $crate.license } else { 'see the text' }
    $note = if ($source.id -ne $crate.id) { "the crate ships no license file; this is the text of $($source.name), from the same project" } else { '' }
    Add-Row 'rust' $crate.name $crate.version $license $ids $note
}

# --- The Rust standard library ----------------------------------------

$rustVersion = ((Invoke-Captured 'rustc' @('--version') $core).Trim() -split ' ')[1]
$sysroot = (Invoke-Captured 'rustc' @('--print', 'sysroot') $core).Trim()
$apache = Join-Path $sysroot 'share\doc\rust\licenses\Apache-2.0.txt'
if (-not (Test-Path -LiteralPath $apache)) {
    # A minimal toolchain may lack its license folder. The Apache License
    # names no copyright holder, so any crate's copy is the same text.
    $apache = $crates | ForEach-Object { $crateFiles[$_.id] } | Where-Object { (Split-Path $_ -Leaf) -match '^LICENSE-APACHE' } |
        Select-Object -First 1
    if (-not $apache) { throw 'no Apache-2.0 text found for the Rust standard library' }
}
$id = Add-Text (Read-Text $apache) "Rust standard library $rustVersion"
Add-Row 'std' 'Rust standard library' $rustVersion 'MIT OR Apache-2.0 (Apache-2.0 chosen)' @($id) 'copyrights are held by its contributors'

# --- NuGet packages ---------------------------------------------------

$depsPath = Join-Path $Release 'CabinetOS.deps.json'
if (-not (Test-Path -LiteralPath $depsPath)) { throw "$depsPath is missing; publish the window first" }
$deps = Read-Text $depsPath | ConvertFrom-Json -AsHashtable
# The restore records the package folder it used.
$assets = Read-Text (Join-Path $repo 'ui\CabinetOS\obj\project.assets.json') | ConvertFrom-Json -AsHashtable
$nugetRoot = @($assets.packageFolders.Keys)[0]
$dotnetRoot = Split-Path (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source -Parent

foreach ($key in ($deps.libraries.Keys | Sort-Object)) {
    $library = $deps.libraries[$key]
    if ($library.type -notin 'package', 'runtimepack') { continue }
    $name, $packageVersion = $key -split '/', 2
    $name = $name -replace '^runtimepack\.', ''
    $folder = @((Join-Path $nugetRoot "$($name.ToLowerInvariant())\$packageVersion"), (Join-Path $dotnetRoot "packs\$name\$packageVersion")) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $folder) { throw "the NuGet package $name $packageVersion is not in $nugetRoot; restore the window's projects first" }
    $expression = $null
    $url = $null
    $nuspec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' | Select-Object -First 1
    if ($nuspec) {
        [xml] $spec = Read-Text $nuspec.FullName
        $license = $spec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        if ($license -and $license.GetAttribute('type') -eq 'expression') { $expression = $license.InnerText.Trim() }
        $address = $spec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        if ($address) { $url = $address.InnerText.Trim() }
    }
    $user = "$name $packageVersion"
    $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match $packageLicenseFile | Sort-Object Name)
    $note = ''
    if ($files.Count -gt 0) {
        $ids = foreach ($file in $files) { Add-Text (Read-Text $file.FullName) $user }
    }
    elseif (-not $expression -and $url) {
        # Microsoft's Windows SDK projection carries only the address of its terms.
        $ids = @(Add-Text "The package $name carries no license text. Its license is at $url" $user)
        $note = 'the package ships no license text, only its address'
    }
    else {
        throw "the NuGet package $user ships no license file; add its text to build\notices.ps1 by hand"
    }
    $licenseName = if ($expression) { $expression } else { 'its own terms (see the text)' }
    Add-Row 'nuget' $name $packageVersion $licenseName $ids $note
}

# --- The .NET application host ----------------------------------------

$sdkVersion = (Invoke-Captured 'dotnet' @('--version') (Join-Path $repo 'ui')).Trim()
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
if (-not (Test-Path -LiteralPath $dotnetLicense)) { throw "$dotnetLicense is missing" }
$id = Add-Text (Read-Text $dotnetLicense) ".NET application host (SDK $sdkVersion)"
Add-Row 'dotnet' '.NET application host, the start of CabinetOS.exe' "SDK $sdkVersion" 'Microsoft .NET Library License' @($id) ''

# --- Bundled JavaScript -----------------------------------------------

$bundled = @(
    @{ Name = '@xterm/xterm'; Readme = 'ui\CabinetOS\Assets\xterm\README.md'; License = 'ui\CabinetOS\Assets\xterm\xterm\LICENSE' },
    @{ Name = '@xterm/addon-fit'; Readme = 'ui\CabinetOS\Assets\xterm\README.md'; License = 'ui\CabinetOS\Assets\xterm\addon-fit\LICENSE' },
    @{ Name = 'marked'; Readme = 'sdk\tools\markdown-preview\README.md'; License = 'sdk\tools\markdown-preview\marked\LICENSE' }
)
foreach ($item in $bundled) {
    # The README's table names the copied version, for example "marked 18.0.14, MIT".
    $match = [regex]::Match((Read-Text (Join-Path $repo $item.Readme)), [regex]::Escape($item.Name) + ' (\d+\.\d+\.\d+\S*), ([A-Za-z0-9.\-]+)')
    if (-not $match.Success) { throw "$($item.Readme) does not name the version of $($item.Name)" }
    $id = Add-Text (Read-Text (Join-Path $repo $item.License)) "$($item.Name) $($match.Groups[1].Value)"
    Add-Row 'js' $item.Name $match.Groups[1].Value $match.Groups[2].Value @($id) ''
}

# --- The file ---------------------------------------------------------

$sections = [ordered]@{
    rust   = @('Rust crates', 'Compiled into `cabinetos-core.exe`, `cabinetos-indexer.exe` and `cabinetos-cli.exe`.', 'Crate')
    std    = @('Rust standard library', 'Compiled into the same three programs.', 'Component')
    nuget  = @('NuGet packages', 'The libraries next to `CabinetOS.exe`.', 'Package')
    dotnet = @('.NET', 'The program that starts the .NET runtime for `CabinetOS.exe` is part of that file.', 'Component')
    js     = @('JavaScript', 'The terminal page (`Assets\xterm`) and Markdown Preview (`extras\tools\markdown-preview`).', 'Library')
}
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Third-party notices')
$lines.Add('')
$lines.Add("CabinetOS $Version is released under the MIT license (``LICENSE``). It")
$lines.Add('includes the third-party components below. Each is listed with its')
$lines.Add('license and a link to the full text; every distinct text follows once,')
$lines.Add('with the components that use it.')
$lines.Add('')
$lines.Add('This file is generated by `build/notices.ps1` for each release.')
foreach ($section in $sections.Keys) {
    $title, $what, $column = $sections[$section]
    $lines.Add('')
    $lines.Add("## $title")
    $lines.Add('')
    $lines.Add($what)
    $lines.Add('')
    $lines.Add("| $column | Version | License | Text |")
    $lines.Add('|---|---|---|---|')
    foreach ($row in ($rows | Where-Object Section -EQ $section)) {
        $links = ($row.Ids | ForEach-Object { "[$_](#$($_.ToLowerInvariant()))" }) -join ', '
        if ($row.Note) { $links += " ($($row.Note))" }
        $lines.Add("| $($row.Name) | $($row.Version) | $($row.License) | $links |")
    }
}
$lines.Add('')
$lines.Add('## License texts')
foreach ($entry in $texts.Values) {
    $lines.Add('')
    $lines.Add("### $($entry.Id)")
    $lines.Add('')
    $lines.Add("Used by: $($entry.Users -join ', ').")
    $lines.Add('')
    $lines.Add('````text')
    $lines.Add($entry.Text)
    $lines.Add('````')
}
[System.IO.File]::WriteAllText($Out, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
"THIRD-PARTY-NOTICES.md: $($rows.Count) components, $($texts.Count) distinct license texts, $([math]::Round((Get-Item -LiteralPath $Out).Length / 1KB)) KB"
