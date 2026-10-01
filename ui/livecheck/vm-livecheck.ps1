# Runs the live check with real keys inside the VirtualBox VM "CabinetOS-LiveCheck" on this PC (docs/ui.md, "The
# live check in a virtual machine"), so this PC's keyboard and mouse stay free: the VM has its own. The VM was made
# 2026-10-01: Windows 11 Pro, the user "cabinetos" logged in on its own at start, the project's root folder on this
# PC as the shared folder X: (so the VM reads the repository and writes its output there, and nothing is copied over
# a network), the per-user .NET runtime and PowerShell 7, the Ukrainian keyboard layout, no sleep and no popups
# (_io\vm\vm-setup.ps1). The VM's user and password are in _io\vm\vm-user.txt, which this script reads and never
# prints.
#
# What it does, in order: starts the VM when it is off (headless: no window on this PC) and waits for its desktop;
# asks for a 1920x1080 screen; through VirtualBox's guest control, starts vm-guest.ps1 of this repository in the
# VM's own session (a process started by guest control runs on the VM's desktop, so no scheduled task is needed
# there, unlike the laptop); vm-guest.ps1 copies this repository's tree and builds from X: onto the VM's disk and
# runs run-livecheck.ps1 there with -Virtual (docs/ui.md says what that changes); this script waits for the run's
# DONE.md in _io\live-check, which the VM writes straight into this PC's folder, takes a screenshot of the VM's
# screen next to it, renames the run's output to run-<time>-cabinetos-vm.txt (a run on this PC has no suffix, the
# laptop's has its name), keeps a copy of DONE.md as DONE-cabinetos-vm.md, closes the Notepad the run opened in the
# VM, and prints DONE.md. vm-progress.txt says how far the VM got. Needs the Release window and the release core
# built in this repository (docs/ui.md, "The live check"). Runs in Windows PowerShell 5.1 and PowerShell 7.
#
# A git worktree runs its own tree: the shared folder is the project's root, so every checkout under it is visible
# to the VM, and the script hands the VM the path of the repository it is in.
#
# The first run in a VM makes the bench's folders there (bench-folders.ps1): the 100,000 empty files took 135 s on
# the virtual disk, and the first run was about seven minutes longer in all (2026-10-01). A later run finds the
# folders made and runs the check at its normal length, about ten minutes. vm-progress.txt says where a run is.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-livecheck.ps1
#   vm-livecheck.ps1 -WaitMinutes 40
#   vm-livecheck.ps1 -Restart          # the VM restarted first: after a stuck guest control or a run that left its window
param(
  [string]$VM = 'CabinetOS-LiveCheck',
  [string]$User = 'cabinetos',
  [int]$WaitMinutes = 30,
  [string]$Io = '',
  [switch]$Restart
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Split-Path $repo -Parent
$io = if ($Io) { $Io } else { Join-Path $root '_io\live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$window = Join-Path $repo 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
foreach ($needed in $window, $core) { if (-not (Test-Path -LiteralPath $needed)) { throw "$needed is missing: build it first" } }
$vbm = Join-Path ${env:ProgramFiles} 'Oracle\VirtualBox\VBoxManage.exe'
if (-not (Test-Path -LiteralPath $vbm)) { throw "VirtualBox is not installed here ($vbm)" }
$userFile = Join-Path $root '_io\vm\vm-user.txt'
if (-not (Test-Path -LiteralPath $userFile)) { throw "$userFile is missing: it holds the VM user's password" }

# The shared folder X: is the project's root, so a path under it on this PC is the same path under X: in the VM.
function GuestPath([string]$local) {
  if (-not $local.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "$local is outside the shared folder $root" }
  'X:' + $local.Substring($root.Length)
}
$guestRepo = GuestPath $repo
$guestIo = GuestPath $io

# 1. The VM, running, with its desktop up.
function State { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'VMState=*' }) -replace 'VMState=|"', '' }
function RunLevel { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'GuestAdditionsRunLevel=*' }) -replace 'GuestAdditionsRunLevel=', '' }
if ($Restart -and (State) -eq 'running') {
  # A restart is the one cure seen (2026-10-01) for a VM whose guest control is stuck ("Error starting guest
  # session"), or whose earlier run left WebView2 processes behind: they outlive a killed window, cannot be ended,
  # and hold the run's folder, so the next run stops at once. The power button first; a hard power-off when the
  # VM ignores it (it did every time), which leaves the VM's own process behind, and the next start fails on it.
  "restarting the VM"
  try { & $vbm controlvm $VM acpipowerbutton 2>&1 | Out-Null } catch { }
  $deadline = (Get-Date).AddSeconds(90)
  while ((State) -ne 'poweroff' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
  if ((State) -ne 'poweroff') { try { & $vbm controlvm $VM poweroff 2>&1 | Out-Null } catch { }; Start-Sleep -Seconds 10 }
  Get-CimInstance Win32_Process -Filter "Name='VBoxHeadless.exe'" | Where-Object { $_.CommandLine -like "*$VM*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 5
}
$state = State
if ($state -ne 'running') {
  if ($state -eq 'saved' -or $state -eq 'poweroff' -or $state -eq 'aborted') { "the VM is $state, starting it"; & $vbm startvm $VM --type headless | Out-Null }
  elseif ($state -eq 'paused') { & $vbm controlvm $VM resume | Out-Null }
  else { throw "the VM is $state" }
}
$deadline = (Get-Date).AddMinutes(3)
while ((RunLevel) -ne '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if ((RunLevel) -ne '3') { throw "the VM's desktop did not come up in 3 minutes (run level $(RunLevel))" }
if ($state -ne 'running') { Start-Sleep -Seconds 20 }
& $vbm controlvm $VM setvideomodehint 1920 1080 32 | Out-Null
"VM ${VM}: running, desktop up"

# 2. The run, started in the VM's session. The password goes to VBoxManage through a file, not the command line,
# and the file is removed at the end. The guest process started by guest control is a short PowerShell that starts
# vm-guest.ps1 on its own (Start-Process) and returns, because a process started by guest control ends with the
# guest session, and the run must outlive this VBoxManage call.
$pwFile = Join-Path $env:TEMP 'cabinetos-vm-password.txt'
# Runs one PowerShell command in the VM's session and returns its output lines; a failure of guest control itself
# (a stuck guest session, a timeout) returns an empty list instead of throwing, because a run's result is on X: and
# a screenshot or a Notepad left open must not turn it into an error. Guest control gets stuck now and then (two
# times on 2026-10-01); a VM restart is the only cure seen.
function GuestRun([string]$command, [int]$timeoutMs) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
  $lines = @()
  try {
    $lines = @(& $vbm guestcontrol $VM run --username $User --passwordfile $pwFile --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --wait-stdout --timeout $timeoutMs -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1 | ForEach-Object { "$_" })
  } catch { $lines = @("guest control failed: $($_.Exception.Message)") }
  $lines
}
$password = (Get-Content -LiteralPath $userFile | Where-Object { $_ -like 'password: *' } | Select-Object -First 1) -replace '^password: ', ''
if (-not $password) { throw "$userFile has no 'password: ' line" }
[IO.File]::WriteAllText($pwFile, $password)
try {
  # The shared folder X: is mapped in the VM's session some time after the logon (a minute seen); the run needs it,
  # and the answer also proves that guest control works.
  $deadline = (Get-Date).AddMinutes(3)
  do {
    $seen = GuestRun "Test-Path 'X:\cabinetos'" 30000
    if ($seen -contains 'True') { break }
    Start-Sleep -Seconds 10
  } while ((Get-Date) -lt $deadline)
  if (-not ($seen -contains 'True')) { throw "the VM does not see the shared folder X: (guest control answered '$($seen -join ' ')')" }
  "the VM sees X:"
  $doneFile = Join-Path $io 'DONE.md'
  $before = if (Test-Path -LiteralPath $doneFile) { (Get-Item -LiteralPath $doneFile).LastWriteTimeUtc.Ticks } else { 0 }
  $guestScript = "$guestRepo\ui\livecheck\vm-guest.ps1"
  $inner = "Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '$guestScript', '-Source', '$guestRepo', '-Io', '$guestIo'; 'started'"
  $answer = GuestRun $inner 60000
  if (-not ($answer -contains 'started')) { throw "the VM did not start the run: guest control answered '$($answer -join ' ')'" }
  "run started in the VM; its tree: $guestRepo; its output: $io"

  # 3. The wait for DONE.md, which the VM writes into this PC's folder through X:.
  $deadline = (Get-Date).AddMinutes($WaitMinutes)
  do {
    Start-Sleep -Seconds 15
    $now = if (Test-Path -LiteralPath $doneFile) { (Get-Item -LiteralPath $doneFile).LastWriteTimeUtc.Ticks } else { 0 }
  } while ($now -eq $before -and (Get-Date) -lt $deadline)
  # The screenshot is taken inside the VM, from its own desktop: VBoxManage's screenshotpng of a headless VM with
  # 3D acceleration gives a stale frame (seen 2026-10-01: a frame an hour old).
  $shot = Join-Path $io "vm-screen-$(Get-Date -Format 'yyyy-MM-dd-HHmm').png"
  $guestShot = GuestPath $shot
  $shotCommand = "Add-Type -AssemblyName System.Drawing, System.Windows.Forms; `$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; `$bmp = New-Object System.Drawing.Bitmap `$b.Width, `$b.Height; `$g = [System.Drawing.Graphics]::FromImage(`$bmp); `$g.CopyFromScreen(`$b.Location, [System.Drawing.Point]::Empty, `$b.Size); `$bmp.Save('$guestShot', [System.Drawing.Imaging.ImageFormat]::Png)"
  GuestRun $shotCommand 30000 | Out-Null
  if (-not (Test-Path -LiteralPath $shot)) { $shot = "none (guest control gave no screenshot)" }
  if ($now -eq $before) {
    $progress = Join-Path $io 'vm-progress.txt'
    throw "no new DONE.md from the VM after $WaitMinutes minutes; the VM's screen is $shot; progress: $(if (Test-Path -LiteralPath $progress) { (Get-Content -LiteralPath $progress) -join ' | ' } else { 'none' })"
  }
  # 4. The result, under the VM's name, and the VM's Notepad on DONE.md closed, so the next run finds a clean desktop.
  $suffix = '-cabinetos-vm'
  $doneText = Get-Content -LiteralPath $doneFile
  $full = ($doneText | Where-Object { $_ -like 'Full output: *' } | Select-Object -Last 1) -replace '^Full output: ', ''
  $runLocal = if ($full) { Join-Path $io (Split-Path $full -Leaf) } else { '' }
  if ($runLocal -and (Test-Path -LiteralPath $runLocal) -and $runLocal -notlike "*$suffix.txt") {
    $renamed = $runLocal -replace '\.txt$', "$suffix.txt"
    Move-Item -LiteralPath $runLocal $renamed -Force
    $runLocal = $renamed
  }
  Copy-Item -LiteralPath $doneFile (Join-Path $io "DONE$suffix.md") -Force
  GuestRun "Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force" 30000 | Out-Null
  $doneText
  "full output: $runLocal"
  "the VM's screen at the end: $shot"
} finally {
  Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
}
