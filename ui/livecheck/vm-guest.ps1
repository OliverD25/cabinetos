# The guest's side of vm-livecheck.ps1: runs inside the VirtualBox VM "CabinetOS-LiveCheck" (docs/ui.md, "The live
# check in a virtual machine"), started there by vm-livecheck.ps1 on this PC through VirtualBox's guest control.
# The VM sees the project's root folder on this PC as the shared folder X:, so this script is read from there
# (X:\cabinetos\ui\livecheck\vm-guest.ps1) and needs no copy of itself in the VM.
#
# What it does: copies the repository's tree at -Source (without .git and the build folders) and the Release window
# and release core built in it from X: onto the VM's own disk, C:\cabinetos\cabinetos, so the window runs from a
# local disk, as on a real machine; puts the Agent plugin in place from the committed fixture, as
# remote-livecheck.ps1 does for the laptop; makes the bench's 100,000-entry folder; then runs run-livecheck.ps1 of
# the copy with -Virtual (the frame-time checks are not judged in a VM), -NoCountdown (nobody is at the VM's
# keyboard) and -MinimizeOthers, with the output going straight to -Io, a folder on X:, which is a folder of this
# PC. The VM's per-user .NET runtime and PowerShell 7 are in the user's LocalAppData (the VM's setup of 2026-10-01,
# _io\vm\vm-setup.ps1). vm-progress.txt in -Io says how far the script got; it is the first thing to read when
# DONE.md does not come.
param([string]$Source = 'X:\cabinetos', [string]$Io = 'X:\_io\live-check')
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:Path = "$env:DOTNET_ROOT;$env:LOCALAPPDATA\Microsoft\PowerShell\7;$env:Path"
# The shared folder X: is mapped by VirtualBox's guest service some time after the logon; `net use` is no way to
# hurry it, since it asks for credentials and waits for an answer that never comes. Wait for it instead.
$left = 36
while (-not (Test-Path 'X:\cabinetos') -and $left -gt 0) { Start-Sleep -Seconds 5; $left-- }
if (-not (Test-Path 'X:\cabinetos')) { exit 2 }
$dst = 'C:\cabinetos\cabinetos'
$windowRel = 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
New-Item -ItemType Directory -Force $Io, $dst, "$dst\core\target\release" | Out-Null
$progress = "$Io\vm-progress.txt"
"sync started $(Get-Date -Format HH:mm:ss) on $(hostname) from $Source" | Set-Content $progress
& robocopy $Source $dst /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 /XD .git target obj bin node_modules .claude | Out-Null
& robocopy "$Source\$windowRel" "$dst\$windowRel" /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
Copy-Item "$Source\core\target\release\cabinetos-core.exe" "$dst\core\target\release\cabinetos-core.exe" -Force
$agentBuilt = "$dst\sdk\extensions\agent\plugin\plugin.wasm"
$agentFixture = "$dst\sdk\fixtures\plugins\agent\plugin.wasm"
if (-not (Test-Path $agentBuilt) -or (Get-Item $agentFixture).LastWriteTimeUtc -gt (Get-Item $agentBuilt).LastWriteTimeUtc) {
  New-Item -ItemType Directory -Force (Split-Path $agentBuilt) | Out-Null
  Copy-Item $agentFixture $agentBuilt -Force
}
"synced $(Get-Date -Format HH:mm:ss): window of $((Get-Item "$dst\$windowRel\CabinetOS.exe").LastWriteTime.ToString('HH:mm')), core of $((Get-Item "$dst\core\target\release\cabinetos-core.exe").LastWriteTime.ToString('HH:mm'))" | Add-Content $progress
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$dst\ui\livecheck\bench-folders.ps1" 2>&1 | ForEach-Object { "bench folders: $_" } | Add-Content $progress
# OneDrive's "Turn On Windows Backup" reminder comes back at every logon of a stock Windows 11; it is a window that
# -MinimizeOthers would minimize, closed here all the same so that nothing of it stays near the check's window.
Get-Process *OneDrive* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
# What a stopped run leaves: its window, its core, the WebView2 processes the window had (they outlive a killed
# window), the Notepad on DONE.md, and the run's folder, which those processes hold open; livecheck.ps1 cannot
# delete that folder then, and the run goes nowhere (the third run of 2026-10-01 stood for fifteen minutes).
Get-Process CabinetOS, cabinetos-core, msedgewebview2, notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$live = "$env:TEMP/cabinetos-ui-test/live"
$tries = 5
while ((Test-Path $live) -and $tries -gt 0) { Remove-Item $live -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $live) { Start-Sleep -Seconds 3 }; $tries-- }
"leftovers cleared $(Get-Date -Format HH:mm:ss): the run's folder gone: $(-not (Test-Path $live))" | Add-Content $progress
"run started $(Get-Date -Format HH:mm:ss)" | Add-Content $progress
& "$dst\ui\livecheck\run-livecheck.ps1" -NoCountdown -MinimizeOthers -Virtual -Io $Io
"run ended $(Get-Date -Format HH:mm:ss)" | Add-Content $progress
