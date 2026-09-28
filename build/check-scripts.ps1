# Parses every PowerShell script under build\ with the running PowerShell's
# own parser, so a syntax error is caught before a release or an install
# meets it. Runs nothing. install.ps1 and uninstall.ps1 must parse in
# Windows PowerShell 5.1 as well (every Windows 11 has it); a script that
# starts with "#Requires -Version 7" is for PowerShell 7 only and is left
# out there. CI runs this in both PowerShells.
#
# Run from anywhere:
#   pwsh -NoProfile -File <repo>\build\check-scripts.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\build\check-scripts.ps1

$ErrorActionPreference = 'Stop'

$version = $PSVersionTable.PSVersion
$failed = 0
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Filter '*.ps1' | Sort-Object FullName) {
    $first = Get-Content -LiteralPath $script.FullName -TotalCount 1
    if ($version.Major -lt 7 -and $first -match '^#Requires\s+-Version\s+7') {
        Write-Host "$($script.Name): for PowerShell 7 only; not parsed in $version"
        continue
    }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref] $tokens, [ref] $errors)
    if ($errors) {
        $failed += 1
        foreach ($problem in $errors) {
            # GitHub Actions shows this form as an annotation on the line.
            Write-Host "::error file=$($script.FullName),line=$($problem.Extent.StartLineNumber)::$($problem.Message)"
        }
    }
    else {
        Write-Host "$($script.Name): parses in PowerShell $version"
    }
}
if ($failed -gt 0) {
    Write-Host "$failed script(s) do not parse"
    exit 1
}
