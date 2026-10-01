# Runs the live check with real keys on another Windows machine (the creator's Omen laptop, set up 2026-10-01), so
# the keyboard and mouse of this PC stay free (docs/ui.md, "The live check on another machine"). The machine
# needs: an OpenSSH server that this PC's key opens, a clone of the repository at -RemoteRepo made from a git
# bundle, a scheduled task -Task that starts run-livecheck.ps1 in the logged-in session (a process started over
# SSH gets no desktop, so the task is what gives it one), the .NET Desktop Runtime and the Windows App Runtime
# the window needs, and the Ukrainian keyboard layout. The machine must be logged in and unlocked.
#
# What it does, in order: sends the commits the machine does not have yet as a git bundle and fast-forwards its
# clone; copies this PC's Release window and release core into the clone's build paths (the machine builds
# nothing); starts the task; waits for the run's DONE.md; copies the run's output into _io\live-check here with
# the machine's name in the file name; prints DONE.md. Needs the Release window and the release core built on
# this PC (docs/ui.md, "The live check"). Runs in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-livecheck.ps1
#   remote-livecheck.ps1 -SkipBuilds      # the machine already has the current builds
#   remote-livecheck.ps1 -Machine omen -RemoteRepo C:\Dev\cabinetos\cabinetos
param(
  [string]$Machine = 'omen',
  [string]$RemoteRepo = 'C:\Dev\cabinetos\cabinetos',
  [string]$Task = 'CabinetOS-LiveCheck',
  [int]$WaitMinutes = 20,
  [switch]$SkipBuilds,
  [string]$Io = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$io = if ($Io) { $Io } else { Join-Path (Split-Path $repo -Parent) '_io\live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$window = Join-Path $repo 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
$remoteRepoFwd = $RemoteRepo -replace '\\', '/'
$remoteIo = (Split-Path (Split-Path $RemoteRepo -Parent) -Parent) + '\_io\live-check'

function Remote([string]$command) { & ssh -o BatchMode=yes $Machine $command 2>&1 | ForEach-Object { "$_" } }
function Send([string]$local, [string]$remote) { & scp -q -r -o BatchMode=yes $local "${Machine}:$remote"; if ($LASTEXITCODE -ne 0) { throw "scp to $Machine failed for $local" } }

"machine: $Machine, repo there: $RemoteRepo"
$name = (Remote 'hostname') | Select-Object -Last 1
if (-not $name) { throw "no answer from $Machine over ssh" }

# 1. The commits the machine lacks, as a bundle; its clone comes from a bundle too, so it has no GitHub login.
$theirs = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
$ours = (& git -C $repo rev-parse main).Trim()
if ($theirs -ne $ours) {
  $bundle = Join-Path $env:TEMP 'cabinetos-remote.bundle'
  & git -C $repo bundle create $bundle "$theirs..main" 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { & git -C $repo bundle create $bundle main 2>&1 | Out-Null }
  Send $bundle "$remoteRepoFwd/../_io/inbox/cabinetos.bundle"
  Remote "git -C $RemoteRepo pull -q --ff-only ../_io/inbox/cabinetos.bundle main; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
} else {
  "the clone is at $ours already"
}

# 2. The builds: the machine runs what this PC built.
if (-not $SkipBuilds) {
  foreach ($needed in $window, $core) { if (-not (Test-Path -LiteralPath $needed)) { throw "$needed is missing: build it first" } }
  $remoteWindow = "$remoteRepoFwd/ui/CabinetOS/bin/x64/Release/net10.0-windows10.0.22621.0"
  Remote "New-Item -ItemType Directory -Force '$RemoteRepo\ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0', '$RemoteRepo\core\target\release' | Out-Null" | Out-Null
  Send $window "$remoteWindow/"
  Send $core "$remoteRepoFwd/core/target/release/cabinetos-core.exe"
  "builds copied: the window of $((Get-Item (Join-Path $window 'CabinetOS.exe')).LastWriteTime.ToString('HH:mm')), the core of $((Get-Item $core).LastWriteTime.ToString('HH:mm'))"
}

# 3. The run, in the machine's own session, and the wait for its DONE.md.
$before = (Remote "if (Test-Path '$remoteIo\DONE.md') { (Get-Item '$remoteIo\DONE.md').LastWriteTimeUtc.Ticks } else { 0 }") | Select-Object -Last 1
Remote "Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
$deadline = (Get-Date).AddMinutes($WaitMinutes)
do {
  Start-Sleep -Seconds 15
  $now = (Remote "if (Test-Path '$remoteIo\DONE.md') { (Get-Item '$remoteIo\DONE.md').LastWriteTimeUtc.Ticks } else { 0 }") | Select-Object -Last 1
  $state = (Remote "(Get-ScheduledTask -TaskName $Task).State") | Select-Object -Last 1
} while ($now -eq $before -and $state -eq 'Running' -and (Get-Date) -lt $deadline)
if ($now -eq $before) { throw "no new DONE.md on $Machine after $WaitMinutes minutes (task state $state)" }

# 4. The result, home.
$latest = (Remote "(Get-ChildItem '$remoteIo\run-*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1).Name") | Select-Object -Last 1
$local = Join-Path $io ($latest -replace '\.txt$', "-$($name.ToLower()).txt")
& scp -q -o BatchMode=yes "${Machine}:$($remoteIo -replace '\\', '/')/$latest" $local
& scp -q -o BatchMode=yes "${Machine}:$($remoteIo -replace '\\', '/')/DONE.md" (Join-Path $io "DONE-$($name.ToLower()).md")
Get-Content (Join-Path $io "DONE-$($name.ToLower()).md")
"full output: $local"
