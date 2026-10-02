<#
.SYNOPSIS
Installs CabinetOS from this folder.

.DESCRIPTION
Checks the prerequisites, then copies this folder to
%LOCALAPPDATA%\Programs\CabinetOS for the current user (no administrator
rights needed), or with -AllUsers to %ProgramFiles%\CabinetOS. It asks
nothing: every choice is a switch. Running it again installs over the
copy there, for example to update it.

What it did is recorded in .cabinetos-install.json in the install folder;
uninstall.ps1 there removes exactly that. Settings, plugins, themes and
logs live in %APPDATA%\CabinetOS and %LOCALAPPDATA%\CabinetOS; the
install does not touch them.

CabinetOS appears in Settings > Apps, with its version and an Uninstall
button that runs uninstall.ps1: for the current user, or with -AllUsers
for every user. A per-user install then updates itself from inside the
app, which keeps that version current.

.PARAMETER AllUsers
Install for every user of this PC, into %ProgramFiles%\CabinetOS. Needs
a PowerShell started with "Run as administrator".

.PARAMETER Destination
Install into this folder instead. It must be new, empty, or an earlier
CabinetOS install, and not the root of a drive.

.PARAMETER StartMenu
Add CabinetOS to the Start Menu: yours, or every user's with -AllUsers.

.PARAMETER AddToPath
Add the install folder to your PATH (the machine's with -AllUsers), so
cabinetos-cli, and cab (the same program under a short name), run in any
new terminal. Off unless given.

.PARAMETER Indexer
Also install and start the indexer service, for instant search of whole
NTFS volumes. Needs -AllUsers and a folder inside Program Files: the
service runs as LocalSystem, so its program must sit where only
administrators can change it. The service is registered with an automatic,
delayed start and started once, so it comes up by itself after every
restart of Windows.

.PARAMETER SkipPrerequisiteCheck
Install even when a prerequisite looks missing.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\install.ps1 -StartMenu -AddToPath

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\install.ps1 -AllUsers -StartMenu -Indexer

From a PowerShell started with "Run as administrator".
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $AllUsers,
    [string] $Destination,
    [switch] $StartMenu,
    [switch] $AddToPath,
    [switch] $Indexer,
    [switch] $SkipPrerequisiteCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$markerName = '.cabinetos-install.json'
$appsKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS'
$serviceName = 'cabinetos-indexer'
$programNames = 'CabinetOS', 'cabinetos-core', 'cabinetos-cli', 'cab', 'cabinetos-indexer'

function Stop-Install([string] $Message) {
    $Host.UI.WriteErrorLine("install.ps1: $Message")
    exit 1
}

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Inside([string] $Path, [string] $Folder) {
    ($Path.TrimEnd('\') + '\').StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeRelative([string] $Path) {
    $Path -and -not [IO.Path]::IsPathRooted($Path) -and $Path -notmatch ':' -and
        -not ($Path -split '[\\/]' | Where-Object { $_ -eq '..' })
}

# The program a service runs, from its command line ("C:\x\y.exe" --args).
function Get-ServiceProgram([string] $Name) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $line = [string] (Get-Item -LiteralPath $key).GetValue('ImagePath')
    if (-not $line) { return $null }
    $line = $line.Trim()
    if ($line.StartsWith('"')) { return $line.Substring(1, $line.IndexOf('"', 1) - 1) }
    ($line -split ' ')[0]
}

# Tells running programs, Explorer first, that the environment changed, so
# programs started afterwards see the new PATH.
function Send-EnvironmentChange {
    if (-not ('CabinetOSSetup.NativeMethods' -as [type])) {
        Add-Type -Namespace CabinetOSSetup -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
'@
    }
    $result = [UIntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG, at most 5 s.
    [void][CabinetOSSetup.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref] $result)
}

# Adds a folder to the user's or the machine's PATH in the registry. The
# value is read without expanding %VARIABLES% and written back with its
# own kind, so entries such as %USERPROFILE%\bin keep working.
function Add-PathEntry([string] $Scope, [string] $Entry) {
    if ($Scope -eq 'Machine') {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $true)
    }
    else {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    }
    try {
        $current = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        if ($key.GetValueNames() -contains 'Path') { $kind = $key.GetValueKind('Path') }
        $parts = @($current -split ';' | Where-Object { $_ })
        if ($parts | Where-Object { $_.TrimEnd('\') -eq $Entry.TrimEnd('\') }) { return }
        $key.SetValue('Path', (($parts + $Entry) -join ';'), $kind)
    }
    finally {
        $key.Dispose()
    }
    Send-EnvironmentChange
}

function Find-DotnetRoots {
    $roots = New-Object System.Collections.ArrayList
    foreach ($name in 'DOTNET_ROOT_X64', 'DOTNET_ROOT') {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { [void]$roots.Add($value) }
    }
    $setup = 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64'
    if (Test-Path -LiteralPath $setup) {
        $location = (Get-Item -LiteralPath $setup).GetValue('InstallLocation')
        if ($location) { [void]$roots.Add($location) }
    }
    [void]$roots.Add((Join-Path $env:ProgramFiles 'dotnet'))
    $roots | Select-Object -Unique
}

# Returns one entry per missing prerequisite: what it is, and the commands
# that install it.
function Get-MissingPrerequisites($Requires) {
    $missing = New-Object System.Collections.ArrayList

    $windows = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $build = [int] $windows.GetValue('CurrentBuildNumber')
    if ($build -lt $Requires.windowsBuild -or -not [Environment]::Is64BitOperatingSystem) {
        [void]$missing.Add(@{
            What     = "64-bit Windows 11, build $($Requires.windowsBuild) (22H2) or newer; this PC has build $build"
            Commands = @('Settings > Windows Update installs newer versions of Windows 11.')
        })
    }

    foreach ($framework in $Requires.dotnet) {
        $wanted = [version] $framework.version
        $found = $false
        foreach ($root in Find-DotnetRoots) {
            $shared = Join-Path $root "shared\$($framework.name)"
            if (-not (Test-Path -LiteralPath $shared)) { continue }
            foreach ($entry in Get-ChildItem -LiteralPath $shared -Directory) {
                $installed = $null
                if ([version]::TryParse(($entry.Name -split '-')[0], [ref] $installed) -and $installed.Major -eq $wanted.Major -and $installed -ge $wanted) {
                    $found = $true
                }
            }
        }
        if (-not $found) {
            $id = switch ($framework.name) {
                'Microsoft.WindowsDesktop.App' { "Microsoft.DotNet.DesktopRuntime.$($wanted.Major)" }
                'Microsoft.AspNetCore.App' { "Microsoft.DotNet.AspNetCore.$($wanted.Major)" }
                default { "Microsoft.DotNet.Runtime.$($wanted.Major)" }
            }
            [void]$missing.Add(@{
                What     = ".NET $($wanted.Major) runtime ($($framework.name) $($framework.version) or a newer $($wanted.Major).x)"
                Commands = @("winget install --id $id --exact", "or: https://dotnet.microsoft.com/download/dotnet/$($wanted.Major).0")
            })
        }
    }

    $runtime = $Requires.windowsAppRuntime
    $packageName = $runtime.packageFamily.Substring(0, $runtime.packageFamily.LastIndexOf('_'))
    try {
        $wanted = [version] $runtime.version
        $packages = @(Get-AppxPackage -Name $packageName | Where-Object { "$($_.Architecture)" -eq 'X64' -and [version] $_.Version -ge $wanted })
        if ($packages.Count -eq 0) {
            [void]$missing.Add(@{
                What     = "Windows App Runtime $($runtime.version) or newer (x64)"
                Commands = @(
                    "winget install --id $packageName --exact",
                    "If winget offers a version older than $($runtime.version), use Microsoft's installer instead:",
                    $runtime.installer)
            })
        }
    }
    catch {
        Write-Warning "Could not check the Windows App Runtime ($($_.Exception.Message)). CabinetOS needs version $($runtime.version) or newer."
    }

    $client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $webView = @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$client", "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$client") |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { (Get-Item -LiteralPath $_).GetValue('pv') } |
        Where-Object { $_ -and $_ -ne '0.0.0.0' }
    if (-not $webView) {
        [void]$missing.Add(@{
            What     = 'Microsoft Edge WebView2 Runtime'
            Commands = @('winget install --id Microsoft.EdgeWebView2Runtime --exact')
        })
    }
    $missing
}

# --- Checks: nothing is changed until all of them pass ----------------

$source = $PSScriptRoot
$releasePath = Join-Path $source 'release.json'
if (-not (Test-Path -LiteralPath $releasePath) -or -not (Test-Path -LiteralPath (Join-Path $source 'CabinetOS.exe'))) {
    Stop-Install 'run this script from the unpacked release folder, the one with CabinetOS.exe and release.json in it. build\release.ps1 makes that folder from the source code.'
}
$release = Get-Content -Raw -Encoding UTF8 -LiteralPath $releasePath | ConvertFrom-Json

if ($Destination) {
    $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
}
elseif ($AllUsers) {
    $target = Join-Path $env:ProgramFiles 'CabinetOS'
}
else {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\CabinetOS'
}
$target = $target.TrimEnd('\')
$root = [IO.Path]::GetPathRoot($target)
if (-not $root -or $target -eq $root.TrimEnd('\')) {
    Stop-Install "$target is the root of a drive; choose a folder inside it."
}
if ((Test-Inside $target $source) -or (Test-Inside $source $target)) {
    Stop-Install "the install folder $target overlaps this release folder $source. Unpack the release elsewhere, or choose another -Destination."
}

$elevated = Test-Elevated
if ($Indexer -and -not $AllUsers) {
    Stop-Install '-Indexer needs -AllUsers. The indexer service runs as LocalSystem, so its program must sit in a folder only administrators can change, such as Program Files.'
}
if ($Indexer -and -not (Test-Inside $target $env:ProgramFiles)) {
    Stop-Install "-Indexer needs the install folder inside $env:ProgramFiles, which only administrators can change; $target is not."
}
if ($AllUsers -and -not $elevated) {
    Stop-Install 'installing for all users (and the indexer service) needs administrator rights. Start PowerShell with "Run as administrator" and run this script again.'
}

$markerPath = Join-Path $target $markerName
$previous = $null
if (Test-Path -LiteralPath $markerPath) {
    $previous = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
    if ($previous.product -ne 'CabinetOS') { Stop-Install "$markerPath is not a CabinetOS install record." }
    foreach ($relative in @($previous.files)) {
        if (-not (Test-SafeRelative $relative)) { Stop-Install "$markerPath lists $relative, which is outside the install folder; nothing was changed." }
    }
}
elseif ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
    Stop-Install "$target already holds files that are not a CabinetOS install. Choose a new or empty folder with -Destination."
}

$running = @(Get-Process -Name $programNames -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Test-Inside $_.Path $target) })
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    Stop-Install "close CabinetOS first: $names running from $target. The indexer service stops with: Stop-Service $serviceName"
}

$serviceProgram = Get-ServiceProgram $serviceName
if ($Indexer -and $serviceProgram -and -not (Test-Inside $serviceProgram $target)) {
    Stop-Install "a $serviceName service already exists and runs $serviceProgram. Remove it first: & '$serviceProgram' --uninstall"
}

if (-not $SkipPrerequisiteCheck) {
    # Get-AppxPackage's module sets aliases while it loads; under -WhatIf
    # each would print a "What if" line.
    $whatIf = $WhatIfPreference
    $WhatIfPreference = $false
    Import-Module Appx -ErrorAction SilentlyContinue
    $WhatIfPreference = $whatIf
    $missing = @(Get-MissingPrerequisites $release.requires)
    if ($missing.Count -gt 0) {
        $Host.UI.WriteErrorLine('install.ps1: CabinetOS needs these first:')
        foreach ($item in $missing) {
            $Host.UI.WriteErrorLine("  - $($item.What)")
            foreach ($command in $item.Commands) { $Host.UI.WriteErrorLine("      $command") }
        }
        $Host.UI.WriteErrorLine('Install them, then run this script again (or add -SkipPrerequisiteCheck to install anyway).')
        exit 1
    }
}

# --- Copy -------------------------------------------------------------

$files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force |
    ForEach-Object { $_.FullName.Substring($source.Length).TrimStart('\') } |
    Where-Object { $_ -ne 'install.ps1' -and $_ -ne $markerName })
$previousFiles = @()
if ($previous) { $previousFiles = @($previous.files) }

function Save-Record([string[]] $Files, $Shortcut, $PathEntry, [bool] $WithIndexer, $AppsEntry) {
    $record = [ordered]@{
        product           = 'CabinetOS'
        version           = $release.version
        installedUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        scope             = $(if ($AllUsers) { 'machine' } else { 'user' })
        files             = @($Files)
        startMenuShortcut = $Shortcut
        path              = $PathEntry
        indexer           = $WithIndexer
        appsEntry         = $AppsEntry
    }
    Set-Content -LiteralPath $markerPath -Value ($record | ConvertTo-Json -Depth 4) -Encoding UTF8
}

$shortcut = $null
$pathEntry = $null
$withIndexer = $false
$appsEntry = $null
if ($previous) {
    $shortcut = $previous.startMenuShortcut
    $pathEntry = $previous.path
    $withIndexer = [bool] $previous.indexer
    if ($previous.PSObject.Properties['appsEntry']) { $appsEntry = $previous.appsEntry }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
# Recorded before copying: an interrupted copy leaves a folder that the
# next install and uninstall.ps1 still recognize.
Save-Record (@($files) + @($previousFiles | Where-Object { $files -notcontains $_ })) $shortcut $pathEntry $withIndexer $appsEntry
foreach ($relative in $files) {
    $to = Join-Path $target $relative
    $parent = Split-Path $to -Parent
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $to -Force
}
foreach ($relative in @($previousFiles | Where-Object { $files -notcontains $_ })) {
    $old = Join-Path $target $relative
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force }
}

# --- Opt-in steps -----------------------------------------------------

if ($StartMenu) {
    $menu = if ($AllUsers) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
    $shortcut = Join-Path $menu 'CabinetOS.lnk'
    if ($PSCmdlet.ShouldProcess($shortcut, 'Create the Start Menu shortcut')) {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
        $link.TargetPath = Join-Path $target 'CabinetOS.exe'
        $link.WorkingDirectory = $target
        $link.Description = 'CabinetOS file manager'
        $link.Save()
    }
}

if ($AddToPath) {
    $scope = if ($AllUsers) { 'Machine' } else { 'User' }
    if ($PSCmdlet.ShouldProcess("the $scope PATH", "Add $target")) { Add-PathEntry $scope $target }
    $pathEntry = [ordered]@{ scope = $scope; entry = $target }
}

if ($Indexer) {
    if ($serviceProgram) {
        Write-Host "The $serviceName service is already installed from this folder."
    }
    elseif ($PSCmdlet.ShouldProcess($serviceName, 'Install and start the Windows service')) {
        # --install registers the service (automatic, delayed start) and starts it once.
        & (Join-Path $target 'cabinetos-indexer.exe') --install
        if ($LASTEXITCODE -ne 0) { Stop-Install "cabinetos-indexer --install failed with exit code $LASTEXITCODE." }
    }
    $withIndexer = $true
}

# Settings > Apps: the entry Windows lists, with the uninstaller as its
# Uninstall button. Under the user's own hive for a per-user install (no
# administrator rights needed), under the machine's for -AllUsers.
$appsHive = if ($AllUsers) { 'HKLM:' } else { 'HKCU:' }
$appsKey = "$appsHive\$appsKeyPath"
if ($PSCmdlet.ShouldProcess($appsKey, 'List CabinetOS in Settings > Apps')) {
    $bytes = ($files | ForEach-Object { (Get-Item -LiteralPath (Join-Path $source $_) -Force).Length } | Measure-Object -Sum).Sum
    New-Item -Path $appsKey -Force | Out-Null
    $texts = [ordered]@{
        DisplayName     = 'CabinetOS'
        DisplayVersion  = [string] $release.version
        Publisher       = 'CabinetOS'
        InstallLocation = $target
        DisplayIcon     = Join-Path $target 'CabinetOS.exe'
        UninstallString = "powershell -ExecutionPolicy Bypass -File `"$(Join-Path $target 'uninstall.ps1')`""
    }
    foreach ($entry in $texts.GetEnumerator()) {
        New-ItemProperty -LiteralPath $appsKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
    }
    $numbers = [ordered]@{ NoModify = 1; NoRepair = 1; EstimatedSize = [int] [math]::Ceiling($bytes / 1KB) }
    foreach ($entry in $numbers.GetEnumerator()) {
        New-ItemProperty -LiteralPath $appsKey -Name $entry.Key -Value $entry.Value -PropertyType DWord -Force | Out-Null
    }
    $appsEntry = $appsKey
}

Save-Record $files $shortcut $pathEntry $withIndexer $appsEntry

# --- Report -----------------------------------------------------------

if ($WhatIfPreference) {
    Write-Host 'What if: nothing was changed.'
    exit 0
}
$who = if ($AllUsers) { 'every user of this PC' } else { $env:USERNAME }
Write-Host "CabinetOS $($release.version) is installed for $who in $target"
Write-Host "  Start it:  & '$(Join-Path $target 'CabinetOS.exe')'$(if ($shortcut) { ', or from the Start Menu' })"
if ($pathEntry) { Write-Host '  cabinetos-cli, and cab for short, are on the PATH of terminals opened from now on.' }
if ($Indexer) { Write-Host "  The indexer service runs now, and starts by itself after every restart of Windows (automatic, delayed start)." }
Write-Host "  Remove it: Settings > Apps, or powershell -ExecutionPolicy Bypass -File '$(Join-Path $target 'uninstall.ps1')'"
if (Get-Item -LiteralPath (Join-Path $source 'CabinetOS.exe') -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue) {
    Write-Host ''
    Write-Host 'These files carry the mark Windows gives downloads, and CabinetOS is not signed yet,'
    Write-Host 'so SmartScreen may warn before the first start. If you trust this download, remove the mark:'
    Write-Host "  Get-ChildItem -LiteralPath '$target' -Recurse -File | Unblock-File"
}
