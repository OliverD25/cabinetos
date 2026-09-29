<#
.SYNOPSIS
Removes CabinetOS.

.DESCRIPTION
Removes what install.ps1 put in place, as its record
.cabinetos-install.json in the install folder lists it: the program
files, the Start Menu shortcut, the PATH entry and the indexer service.
Settings, plugins, themes and logs stay (%APPDATA%\CabinetOS and
%LOCALAPPDATA%\CabinetOS) unless -RemoveData is given. It asks nothing.

.PARAMETER Destination
The install folder. Without it: the folder this script is in, if it holds
an install; else %LOCALAPPDATA%\Programs\CabinetOS; else
%ProgramFiles%\CabinetOS.

.PARAMETER RemoveData
Also delete your settings, plugins, themes, marketplace downloads and
logs: %APPDATA%\CabinetOS and %LOCALAPPDATA%\CabinetOS, and for an install
with the indexer service %ProgramData%\CabinetOS, where the service logs.
Other users' data stays.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\CabinetOS\uninstall.ps1"
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Destination,
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$markerName = '.cabinetos-install.json'
$serviceName = 'cabinetos-indexer'
$programNames = 'CabinetOS', 'cabinetos-core', 'cabinetos-cli', 'cab', 'cabinetos-indexer'

function Stop-Uninstall([string] $Message) {
    $Host.UI.WriteErrorLine("uninstall.ps1: $Message")
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

# Removes one folder from the user's or the machine's PATH, keeping every
# other entry and the value's kind as they are.
function Remove-PathEntry([string] $Scope, [string] $Entry) {
    if ($Scope -eq 'Machine') {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $true)
    }
    else {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    }
    try {
        if ($key.GetValueNames() -notcontains 'Path') { return }
        $kind = $key.GetValueKind('Path')
        $current = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $parts = @($current -split ';' | Where-Object { $_ })
        $kept = @($parts | Where-Object { $_.TrimEnd('\') -ne $Entry.TrimEnd('\') })
        if ($kept.Count -eq $parts.Count) { return }
        $key.SetValue('Path', ($kept -join ';'), $kind)
    }
    finally {
        $key.Dispose()
    }
    Send-EnvironmentChange
}

# --- Find the install, check, and change nothing until the checks pass --

if ($Destination) {
    $candidates = @($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination))
}
else {
    $candidates = @($PSScriptRoot, (Join-Path $env:LOCALAPPDATA 'Programs\CabinetOS'), (Join-Path $env:ProgramFiles 'CabinetOS'))
}
$target = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ $markerName) } | Select-Object -First 1
if (-not $target) {
    if ($Destination) { Stop-Uninstall "$($candidates[0]) holds no CabinetOS install: $markerName is missing there." }
    Stop-Uninstall "no CabinetOS install found in $($candidates -join ', '). Name the install folder with -Destination."
}
$target = $target.TrimEnd('\')
$markerPath = Join-Path $target $markerName
$record = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
if ($record.product -ne 'CabinetOS') { Stop-Uninstall "$markerPath is not a CabinetOS install record." }
$files = @($record.files)
foreach ($relative in $files) {
    if (-not (Test-SafeRelative $relative)) { Stop-Uninstall "$markerPath lists $relative, which is outside the install folder; nothing was removed." }
}

if (($record.scope -eq 'machine' -or $record.indexer) -and -not (Test-Elevated)) {
    Stop-Uninstall 'this install is for all users (or has the indexer service), so removing it needs administrator rights. Start PowerShell with "Run as administrator" and run this script again.'
}

$running = @(Get-Process -Name $programNames -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and (Test-Inside $_.Path $target) -and -not ($record.indexer -and $_.ProcessName -eq $serviceName) })
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    Stop-Uninstall "close CabinetOS first: $names running from $target."
}

# --- Remove -----------------------------------------------------------

$failed = New-Object System.Collections.ArrayList

if ($record.indexer) {
    $serviceProgram = Get-ServiceProgram $serviceName
    if ($serviceProgram -and (Test-Inside $serviceProgram $target)) {
        if ($PSCmdlet.ShouldProcess($serviceName, 'Stop and remove the Windows service')) {
            & (Join-Path $target 'cabinetos-indexer.exe') --uninstall
            if ($LASTEXITCODE -ne 0) { Stop-Uninstall "cabinetos-indexer --uninstall failed with exit code $LASTEXITCODE; nothing else was removed." }
        }
    }
    elseif ($serviceProgram) {
        Write-Warning "The $serviceName service runs $serviceProgram, which is not in $target, so it stays."
    }
}

$shortcut = $record.startMenuShortcut
if ($shortcut -and (Split-Path $shortcut -Leaf) -eq 'CabinetOS.lnk' -and (Test-Path -LiteralPath $shortcut)) {
    Remove-Item -LiteralPath $shortcut -Force
}

if ($record.path -and $PSCmdlet.ShouldProcess("the $($record.path.scope) PATH", "Remove $($record.path.entry)")) {
    Remove-PathEntry $record.path.scope $record.path.entry
}

# A folder that is some program's current directory cannot be removed.
if ((Get-Location).Provider.Name -eq 'FileSystem' -and (Test-Inside (Get-Location).ProviderPath $target)) {
    Set-Location -LiteralPath $env:USERPROFILE
}
foreach ($relative in $files) {
    $path = Join-Path $target $relative
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $path) { [void]$failed.Add($path) }
    }
}
$folders = New-Object 'System.Collections.Generic.HashSet[string]' -ArgumentList ([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $files) {
    $folder = Split-Path $relative -Parent
    while ($folder) {
        [void]$folders.Add($folder)
        $folder = Split-Path $folder -Parent
    }
}
foreach ($folder in ($folders | Sort-Object Length -Descending)) {
    $path = Join-Path $target $folder
    if ((Test-Path -LiteralPath $path) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path -Force }
}
if ($failed.Count -eq 0) {
    Remove-Item -LiteralPath $markerPath -Force
    if (@(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) { Remove-Item -LiteralPath $target -Force }
}

$data = @()
if ($RemoveData) {
    $data = @($env:LOCALAPPDATA, $env:APPDATA) | Where-Object { $_ -and [IO.Path]::IsPathRooted($_) } | ForEach-Object { Join-Path $_ 'CabinetOS' }
    if ($record.indexer -and $env:ProgramData -and [IO.Path]::IsPathRooted($env:ProgramData)) { $data += Join-Path $env:ProgramData 'CabinetOS' }
    foreach ($folder in $data) {
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $folder) { [void]$failed.Add($folder) }
    }
}

# --- Report -----------------------------------------------------------

if ($WhatIfPreference) {
    Write-Host 'What if: nothing was changed.'
    exit 0
}
if ($failed.Count -gt 0) {
    $Host.UI.WriteErrorLine('uninstall.ps1: these could not be removed (in use?). Close CabinetOS and run this script again:')
    foreach ($path in $failed) { $Host.UI.WriteErrorLine("  $path") }
    exit 1
}
Write-Host "CabinetOS $($record.version) is removed from $target."
if (Test-Path -LiteralPath $target) {
    Write-Host "  The folder stays: it holds files the install did not put there."
}
if ($RemoveData) {
    Write-Host "  Its data is removed too: $($data -join ', ')."
}
else {
    Write-Host "  Settings, plugins, themes and logs stay in $(Join-Path $env:APPDATA 'CabinetOS') and $(Join-Path $env:LOCALAPPDATA 'CabinetOS'); -RemoveData deletes them."
}
