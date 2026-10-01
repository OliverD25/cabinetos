# Runs the live check with real keys on another Windows machine (the creator's Omen laptop, set up 2026-10-01), so
# the keyboard and mouse of this PC stay free (docs/ui.md, "The live check on another machine"). The machine
# needs: an OpenSSH server that this PC's key opens, a clone of the repository at -RemoteRepo made from a git
# bundle, a scheduled task -Task that starts run-livecheck.ps1 in the logged-in session (a process started over
# SSH gets no desktop, so the task is what gives it one), the .NET Desktop Runtime and the Windows App Runtime
# the window needs, and the Ukrainian keyboard layout. The machine must be logged in and unlocked.
#
# What it does, in order: sends the commits the machine does not have yet as a git bundle and fast-forwards its
# clone; copies this PC's Release window and release core into the clone's build paths (the machine builds
# nothing); makes the 100,000-entry folder of the core's bench there (bench-folders.ps1: the machine has no Rust);
# starts the task; waits for the run's DONE.md; copies the run's output into _io\live-check here with the machine's
# name in the file name; prints DONE.md. Needs the Release window and the release core built on this PC (docs/ui.md,
# "The live check"). Runs in Windows PowerShell 5.1 and PowerShell 7.
#
# -Branch names the branch to send (main when left out): a git worktree sends its own branch, which must be a
# fast-forward of what the machine's clone has. -Io names the folder for the output, for a worktree elsewhere.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-livecheck.ps1
#   remote-livecheck.ps1 -SkipBuilds      # the machine already has the current builds
#   remote-livecheck.ps1 -Machine omen -RemoteRepo C:\Dev\cabinetos\cabinetos
#   remote-livecheck.ps1 -Branch my-branch -Io E:\path\to\_io\live-check
param(
  [string]$Machine = 'omen',
  [string]$RemoteRepo = 'C:\Dev\cabinetos\cabinetos',
  [string]$Task = 'CabinetOS-LiveCheck',
  [int]$WaitMinutes = 20,
  [switch]$SkipBuilds,
  [string]$Io = '',
  [string]$Branch = 'main'
)
$ErrorActionPreference = 'Stop'
# Git's own ssh reads HOME for ~/.ssh, and a Git Bash points HOME elsewhere; the Windows user's folder is the one.
$env:HOME = $env:USERPROFILE
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$io = if ($Io) { $Io } else { Join-Path (Split-Path $repo -Parent) '_io\live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$window = Join-Path $repo 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
$remoteRepoFwd = $RemoteRepo -replace '\\', '/'
$remoteIo = (Split-Path $RemoteRepo -Parent) + '\_io\live-check'

# The SSH config of the Windows user, named outright: Git's own ssh reads HOME, which a Git Bash sets elsewhere.
$sshConfig = Join-Path $env:USERPROFILE '.ssh\config'
function Remote([string]$command) {
  $out = & ssh -F $sshConfig -o BatchMode=yes $Machine $command 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -ne '' }
  if ($LASTEXITCODE -ne 0) { throw "ssh $Machine failed ($LASTEXITCODE): $($out -join ' ')" }
  $out
}
function Send([string]$local, [string]$remote) { & scp -q -r -F $sshConfig -o BatchMode=yes $local "${Machine}:$remote"; if ($LASTEXITCODE -ne 0) { throw "scp to $Machine failed for $local" } }

"machine: $Machine, repo there: $RemoteRepo"
$name = (Remote 'hostname') | Select-Object -Last 1
if (-not $name) { throw "no answer from $Machine over ssh" }
"answered by $name"

# 1. The commits the machine lacks, as a bundle; its clone comes from a bundle too, so it has no GitHub login.
$theirs = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
$ours = (& git -C $repo rev-parse $Branch).Trim()
if ($theirs -ne $ours) {
  $bundle = Join-Path $env:TEMP 'cabinetos-remote.bundle'
  & git -C $repo bundle create $bundle "$theirs..$Branch" 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { & git -C $repo bundle create $bundle $Branch 2>&1 | Out-Null }
  Send $bundle "$remoteRepoFwd/../_io/inbox/cabinetos.bundle"
  Remote "git -C $RemoteRepo pull -q --ff-only ../_io/inbox/cabinetos.bundle $Branch; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
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

# 3. What the machine cannot build, since it has no Rust: the core bench's 100,000-entry folder, which the live check
# scrolls; the first time this takes a minute or two.
Remote "powershell -NoProfile -ExecutionPolicy Bypass -File '$RemoteRepo\ui\livecheck\bench-folders.ps1'" | ForEach-Object { "bench folders: $_" }

# 4. The run, in the machine's own session, and the wait for its DONE.md. The task stays "Running" while the
# Notepad that run-livecheck.ps1 opens on DONE.md is open, so a run from before is ended first, and the wait
# watches DONE.md, not the task's state.
$doneTicks = "if (Test-Path '$remoteIo\DONE.md') { (Get-Item '$remoteIo\DONE.md').LastWriteTimeUtc.Ticks } else { 0 }"
$before = (Remote $doneTicks) | Select-Object -Last 1
Remote "if ((Get-ScheduledTask -TaskName $Task).State -eq 'Running') { Stop-ScheduledTask -TaskName $Task; Start-Sleep -Seconds 2 }; Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
$deadline = (Get-Date).AddMinutes($WaitMinutes)
do {
  Start-Sleep -Seconds 15
  $now = (Remote $doneTicks) | Select-Object -Last 1
} while ($now -eq $before -and (Get-Date) -lt $deadline)
if ($now -eq $before) { throw "no new DONE.md on $Machine after $WaitMinutes minutes" }

# 5. The result, home.
$latest = (Remote "(Get-ChildItem '$remoteIo\run-*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1).Name") | Select-Object -Last 1
$local = Join-Path $io ($latest -replace '\.txt$', "-$($name.ToLower()).txt")
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$($remoteIo -replace '\\', '/')/$latest" $local
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$($remoteIo -replace '\\', '/')/DONE.md" (Join-Path $io "DONE-$($name.ToLower()).md")
Get-Content (Join-Path $io "DONE-$($name.ToLower()).md")
"full output: $local"
# The run's Notepad on DONE.md keeps the task alive; end it, so the next run can start.
Remote "Stop-ScheduledTask -TaskName $Task" | Out-Null
