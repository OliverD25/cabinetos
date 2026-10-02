# The indexer service's restart check (docs/release.md, "The indexer service"; docs/ui.md, "The live check in a virtual
# machine"): shows in the VirtualBox VM "CabinetOS-LiveCheck" that `cabinetos-indexer --install` leaves a service that
# comes up by itself after Windows restarts, and writes DONE-indexer.md into the output folder (_io\live-check unless
# -Io says otherwise).
#
# Installing and removing a service need an elevated process, and VirtualBox's guest control gives the VM user's
# administrator token with UAC applied (medium integrity; seen 2026-10-02: Register-ScheduledTask with the highest
# run level answers "Access is denied", and a UAC prompt cannot be answered from outside). So the check is in two runs
# of this script with a manual step between them, which a person does once in the VM:
#
#  Run 1 (no switch): stages vm-indexer-guest.ps1 and its arguments in _io\indexer-check, starts the VM when it is off
#     and lets it settle, and tries `-Phase install` through guest control. In a session that is not elevated (the
#     expected case) that phase stops at once with state needs-elevation, and this script writes DONE-indexer.md with
#     the manual step and exits with code 2. (Where the session is elevated, the install happens and run 1 goes on
#     with the restart as run 2 does.)
#  The manual step, in the VM (docs/release.md, "The indexer service" says how to see the VM): a PowerShell started with
#     "Run as administrator", then
#       powershell -ExecutionPolicy Bypass -File \\VBoxSvr\cabinetos\_io\indexer-check\vm-indexer-guest.ps1 -Phase install
#     It copies the release folder to Program Files and runs `cabinetos-indexer --install`; the service must be
#     AUTO_START (DELAYED) and RUNNING.
#  Run 2 (-AfterInstall): reads the service's state through guest control (it must be RUNNING), restarts Windows inside
#     the VM (`shutdown /r`, a real restart), waits for the desktop and for the VM to settle, and runs `-Phase check`
#     (read-only): nobody starts the service, and the guest records when it was first RUNNING by itself, how long after
#     the boot began, that its process is younger than the boot, that its pipe exists and what its log says. It exits 1
#     when any of that fails.
#  Afterwards, in an elevated PowerShell in the VM: the same script with `-Phase uninstall` removes the service and
#     the folder (`cabinetos-indexer --uninstall`, the reverse of the install).
#
# The release folder is dist\CabinetOS-<version>-win-x64 of this repository (made by build\release.ps1), or -Release.
# The VM's user and password are in _io\vm\vm-user.txt, which this script reads and never prints; the password goes to
# VBoxManage through a temporary file. Only short commands go to the VM (a guest command over about 1,200 characters
# hangs guest control, seen 2026-10-01); the guest's script and its arguments travel through the shared folder, which
# the VM sees as \\VBoxSvr\cabinetos (and as X: in the user's own session). Nothing probes the VM while a phase runs.
# Runs in Windows PowerShell 5.1 and PowerShell 7. A VM that was off needs up to 30 minutes to settle after its
# start, and again after the restart (docs/ui.md).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-indexer-check.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-indexer-check.ps1 -AfterInstall
#   vm-indexer-check.ps1 -AfterInstall -SkipInstalledCheck   # tries the restart and the wait with no service in the VM
#   vm-indexer-check.ps1 -Restart ...                          # the VM restarted first: after a stuck guest control
param(
  [string]$VM = 'CabinetOS-LiveCheck',
  [string]$User = 'cabinetos',
  [string]$Release = '',
  [string]$Io = '',
  [switch]$AfterInstall,
  [switch]$SkipInstalledCheck,
  [switch]$Restart,
  [int]$SettleMinutes = 30,
  [int]$ServiceMinutes = 15,
  [int]$PhaseMinutes = 20
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$PSScriptRoot\paths.ps1"
# The project's root, the VM's shared folder X:: the first folder from the main checkout's parent upwards whose _io
# holds the VM's user file. The main checkout, not the repository: a git worktree lies under .claude\worktrees there.
$root = Split-Path (Get-MainCheckout) -Parent
while ($root -and -not (Test-Path -LiteralPath (Join-Path $root '_io\vm\vm-user.txt'))) { $root = Split-Path $root -Parent }
if (-not $root) { throw "no folder above the main checkout of $repo holds _io\vm\vm-user.txt, the VM user's file in the project's exchange folder (the VM's X:)" }
$io = if ($Io) { $Io } else { Join-Path $root '_io\live-check' }
$exchange = Join-Path $root '_io\indexer-check'
New-Item -ItemType Directory -Force $io, $exchange | Out-Null
$tag = Get-Date -Format 'yyyy-MM-dd-HHmm'
$vbm = Join-Path ${env:ProgramFiles} 'Oracle\VirtualBox\VBoxManage.exe'
if (-not (Test-Path -LiteralPath $vbm)) { throw "VirtualBox is not installed here ($vbm)" }
$userFile = Join-Path $root '_io\vm\vm-user.txt'
$utf8 = New-Object System.Text.UTF8Encoding $false
$started = Get-Date

if (-not $Release) {
  $section = [regex]::Match([IO.File]::ReadAllText((Join-Path $repo 'core\Cargo.toml')), '(?ms)^\[workspace\.package\][^\n]*\n(.*?)(?=^\[)')
  $version = [regex]::Match($section.Groups[1].Value, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
  $Release = Join-Path $repo "dist\CabinetOS-$version-win-x64"
}
foreach ($needed in 'cabinetos-indexer.exe', 'release.json') {
  if (-not (Test-Path -LiteralPath (Join-Path $Release $needed))) { throw "$Release has no ${needed}: build the release first (build\release.ps1)" }
}
$releaseVersion = (Get-Content -Raw -LiteralPath (Join-Path $Release 'release.json') | ConvertFrom-Json).version

# The shared folder X: is the project's root, so a path under it on this PC is the same path under X: in the VM.
function GuestPath([string]$local) {
  if (-not $local.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "$local is outside the shared folder $root" }
  'X:' + $local.Substring($root.Length)
}

# The timeline of this run, as lines for DONE-indexer.md.
$timeline = New-Object System.Collections.ArrayList
function Mark([string]$text) { $line = "$((Get-Date).ToString('HH:mm:ss')) $text"; [void]$timeline.Add($line); $line }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vm-indexer-guest.ps1') -Destination (Join-Path $exchange 'vm-indexer-guest.ps1') -Force
$guestArgs = [ordered]@{
  tag            = $tag
  release        = GuestPath $Release
  io             = GuestPath $io
  serviceMinutes = $ServiceMinutes
}
[IO.File]::WriteAllText((Join-Path $exchange 'indexer-args.json'), ($guestArgs | ConvertTo-Json), $utf8)
$progressFile = Join-Path $io 'vm-indexer-progress.txt'
if (Test-Path -LiteralPath $progressFile) { Remove-Item -LiteralPath $progressFile -Force }

# ----- The VM, running, with its desktop up (as vm-install-check.ps1 starts it) -----
function State { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'VMState=*' }) -replace 'VMState=|"', '' }
function RunLevel { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'GuestAdditionsRunLevel=*' }) -replace 'GuestAdditionsRunLevel=', '' }
if ($Restart -and (State) -eq 'running') {
  # The cure seen for a VM whose guest control answers "Error starting guest session" to everything (docs/ui.md): the
  # power button first, a power-off when the VM ignores it, and the VM's own process ended when it stays behind.
  Mark 'restarting the VM'
  try { & $vbm controlvm $VM acpipowerbutton 2>&1 | Out-Null } catch { }
  $deadline = (Get-Date).AddSeconds(90)
  while ((State) -ne 'poweroff' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
  if ((State) -ne 'poweroff') { try { & $vbm controlvm $VM poweroff 2>&1 | Out-Null } catch { }; Start-Sleep -Seconds 10 }
  Get-CimInstance Win32_Process -Filter "Name='VBoxHeadless.exe'" | Where-Object { $_.CommandLine -like "*$VM*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 5
}
$state = State
if ($state -ne 'running') {
  if ($state -eq 'saved' -or $state -eq 'poweroff' -or $state -eq 'aborted') { Mark "the VM is $state, starting it"; & $vbm startvm $VM --type headless | Out-Null }
  elseif ($state -eq 'paused') { & $vbm controlvm $VM resume | Out-Null }
  else { throw "the VM is $state" }
}
$deadline = (Get-Date).AddMinutes(3)
while ((RunLevel) -ne '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if ((RunLevel) -ne '3') { throw "the VM's desktop did not come up in 3 minutes (run level $(RunLevel))" }
if ($state -ne 'running') { Start-Sleep -Seconds 20 }
& $vbm controlvm $VM setvideomodehint 1920 1080 32 | Out-Null
Mark "VM ${VM}: running, desktop up (run level 3)"

$pwFile = Join-Path $env:TEMP 'cabinetos-vm-password.txt'
function GuestRun([string]$command, [int]$timeoutMs) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
  $lines = @()
  try {
    $lines = @(& $vbm guestcontrol $VM run --username $User --passwordfile $pwFile --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --wait-stdout --timeout $timeoutMs -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1 | ForEach-Object { "$_" })
  } catch { $lines = @("guest control failed: $($_.Exception.Message)") }
  $lines
}
# Waits until the VM sees the shared folder (mapped as X: some time after the logon), then until two readings of its
# CPU load in a row are under 20 %, at most $minutes in all.
function Wait-VmSettled([int]$minutes) {
  $deadline = (Get-Date).AddMinutes(3)
  do {
    $seen = GuestRun "Test-Path 'X:\_io\indexer-check\indexer-args.json'" 30000
    if ($seen -contains 'True') { break }
    Start-Sleep -Seconds 10
  } while ((Get-Date) -lt $deadline)
  if (-not ($seen -contains 'True')) { throw "the VM does not see the shared folder X: (guest control answered '$($seen -join ' ')')" }
  Mark 'the VM sees X:'
  if ($minutes -le 0) { return }
  $settling = Get-Date
  $deadline = $settling.AddMinutes($minutes)
  $calm = 0
  $load = ''
  do {
    $answer = GuestRun "[int]((Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 5 -MaxSamples 3).CounterSamples | Measure-Object CookedValue -Average).Average" 60000
    $load = $answer | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1
    if ($load -and [int]$load -lt 20) { $calm++ } else { $calm = 0 }
    if ($calm -ge 2) { break }
    Start-Sleep -Seconds 20
  } while ((Get-Date) -lt $deadline)
  Mark "the VM settled: $($calm -ge 2) after $([int]((Get-Date) - $settling).TotalMinutes) min (the last CPU load: $load %)"
}
# Starts a phase of the guest script, hidden, and waits for its result file; nothing is sent to the VM meanwhile.
function Invoke-Phase([string]$phase) {
  $resultFile = Join-Path $io "vm-indexer-result-$tag-$phase.json"
  if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile -Force }
  # Guest control often answers "Error starting guest session" for a while after a start or a restart of the VM (seen
  # 2026-10-02, also in vm-install-check.ps1's notes), so the start is tried again for up to 20 minutes.
  $retryUntil = (Get-Date).AddMinutes(20)
  $try = 0
  do {
    $try++
    $answer = GuestRun "Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','X:\_io\indexer-check\vm-indexer-guest.ps1','-Phase','$phase'; 'started'" 60000
    if ($answer -contains 'started') { break }
    Mark "guest control did not start the $phase phase (try $try): $($answer -join ' ' | ForEach-Object { $_.Substring(0, [Math]::Min($_.Length, 160)) })"
    Start-Sleep -Seconds 30
  } while ((Get-Date) -lt $retryUntil)
  if (-not ($answer -contains 'started')) { throw "the VM did not start the $phase phase in 20 minutes: guest control answered '$($answer -join ' ')'" }
  Mark "phase $phase started in the VM; its progress: $progressFile"
  $deadline = (Get-Date).AddMinutes($PhaseMinutes)
  $result = $null
  do {
    Start-Sleep -Seconds 10
    if (Test-Path -LiteralPath $resultFile) {
      try { $result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json } catch { $result = $null }
    }
  } while (-not ($result -and $result.state -ne 'running') -and (Get-Date) -lt $deadline)
  if (-not $result) { $result = [pscustomobject]@{ phase = $phase; state = 'no result'; ok = $false; details = @() } }
  if ($result.state -eq 'running') { $result.state = "still running after $PhaseMinutes minutes"; $result.ok = $false }
  Mark "phase $phase ended: $($result.state), ok: $($result.ok)"
  $result
}

$password = (Get-Content -LiteralPath $userFile | Where-Object { $_ -like 'password: *' } | Select-Object -First 1) -replace '^password: ', ''
if (-not $password) { throw "$userFile has no 'password: ' line" }
[IO.File]::WriteAllText($pwFile, $password)
$install = $null
$check = $null
$restartHow = 'not done'
$exitCode = 1
try {
  Wait-VmSettled $SettleMinutes
  $proceed = $false
  if ($AfterInstall) {
    # The manual step must have happened: the service is registered and running.
    $query = GuestRun "sc.exe query cabinetos-indexer" 30000
    $running = [bool]($query | Where-Object { $_ -match 'STATE\s*:\s*\d+\s+RUNNING' })
    Mark "sc query cabinetos-indexer before the restart: $(if ($running) { 'RUNNING' } else { ($query -join ' ') })"
    if ($running) { $proceed = $true; $install = [pscustomobject]@{ phase = 'install'; ok = $true; state = 'done by hand'; details = @("the service was RUNNING before the restart: $($query -join ' ')") } }
    elseif ($SkipInstalledCheck) {
      # For trying this script's own restart and wait with no service in the VM: the check phase then fails, as it should.
      $proceed = $true
      $install = [pscustomobject]@{ phase = 'install'; ok = $false; state = 'skipped'; details = @("-SkipInstalledCheck: the service is not RUNNING ($($query -join ' ')); the restart is tried anyway, and the check phase below must fail") }
    }
    else { $install = [pscustomobject]@{ phase = 'install'; ok = $false; state = 'not installed'; details = @("the service is not RUNNING in the VM, so the manual step was not done: $($query -join ' ')") }; $exitCode = 2 }
  } else {
    $install = Invoke-Phase 'install'
    if ($install.ok) { $proceed = $true }
    elseif ($install.state -eq 'needs-elevation') { $exitCode = 2 }
  }
  if ($proceed) {
    # A real restart of Windows from the inside. The guest session ends with it, so this call may report an error.
    Mark 'restarting Windows in the VM (shutdown /r)'
    $left = GuestRun "shutdown.exe /r /t 5 /c 'CabinetOS indexer check'; 'issued'" 30000
    $restartHow = 'shutdown /r inside the VM'
    $deadline = (Get-Date).AddSeconds(120)
    while ((RunLevel) -eq '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
    if ((RunLevel) -eq '3') {
      # shutdown.exe was refused: a reset of the VM is a hard restart.
      Mark "the VM did not restart ($($left -join ' ')); resetting it"
      & $vbm controlvm $VM reset | Out-Null
      $restartHow = 'controlvm reset (a hard restart; shutdown /r was refused)'
      $deadline = (Get-Date).AddSeconds(120)
      while ((RunLevel) -eq '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
    }
    Mark "the VM is restarting (run level $(RunLevel))"
    $deadline = (Get-Date).AddMinutes(15)
    while ((RunLevel) -ne '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
    if ((RunLevel) -ne '3') { throw "the VM's desktop did not come back in 15 minutes after the restart (run level $(RunLevel))" }
    Mark 'the VM is back (run level 3); nobody starts the service'
    Wait-VmSettled $SettleMinutes
    $check = Invoke-Phase 'check'
    $exitCode = $(if ($install.ok -and $check.ok) { 0 } else { 1 })
  }
} catch {
  # What was done so far still goes into DONE-indexer.md.
  Mark "STOPPED: $($_.Exception.Message)"
  $exitCode = 1
} finally {
  Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
}

# ----- DONE-indexer.md -----
$done = @(
  "# Indexer service restart check ($tag)",
  "",
  "Started $($started.ToString('HH:mm:ss')), ended $((Get-Date).ToString('HH:mm:ss')) on this PC. The release: $Release ($releaseVersion). The restart: $restartHow.",
  "",
  "## Timeline (this PC's clock)",
  ""
) + @($timeline | ForEach-Object { "- $_" })
foreach ($phase in @(@('install', $install, 'cabinetos-indexer --install in the VM: AUTO_START (DELAYED) and RUNNING'), @('check', $check, 'after the restart: the service runs by itself'))) {
  $done += ''
  if ($phase[1]) {
    $done += "## Phase $($phase[0]): $($phase[2]): $(if ($phase[1].ok) { 'passed' } else { 'FAILED' }) ($($phase[1].state))"
    $done += ''
    $done += @($phase[1].details | ForEach-Object { "- $_" })
  } else {
    $done += "## Phase $($phase[0]): $($phase[2]): not reached"
  }
}
if ($exitCode -eq 2) {
  $done += @(
    '',
    '## The manual step',
    '',
    'The install needs an elevated process and the VM''s guest control session is not one (UAC). In the VM, in a PowerShell started with "Run as administrator" (the VM''s window: stop the headless VM and start it with `VBoxManage startvm CabinetOS-LiveCheck --type separate`), run:',
    '',
    '    powershell -ExecutionPolicy Bypass -File \\VBoxSvr\cabinetos\_io\indexer-check\vm-indexer-guest.ps1 -Phase install',
    '',
    'Then run this script again with -AfterInstall on this PC. Afterwards, in the same kind of PowerShell in the VM, `-Phase uninstall` removes the service.'
  )
}
if (Test-Path -LiteralPath $progressFile) { $done += @('', '## The VM''s progress', '') + @(Get-Content -LiteralPath $progressFile | ForEach-Object { "    $_" }) }
$doneFile = Join-Path $io 'DONE-indexer.md'
[IO.File]::WriteAllText($doneFile, ($done -join "`r`n") + "`r`n", $utf8)
Copy-Item -LiteralPath $doneFile -Destination (Join-Path $io "DONE-indexer-$tag.md") -Force
$done
exit $exitCode
