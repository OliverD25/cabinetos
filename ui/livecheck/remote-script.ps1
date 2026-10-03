# Runs one script of this repository on another Windows machine (the creator's Omen laptop, set up 2026-10-01), in
# the machine's own logged-in session, so a script that opens the window or presses real keys (scroll-keys.ps1,
# scroll-bench.ps1, speed-review.ps1) runs there and not on this PC (docs/ui.md, "The live check on another
# machine"). The machine needs what remote-livecheck.ps1 needs, plus a scheduled task -Task that runs the wrapper
# C:\Dev\cabinetos\_io\run-script-laptop.ps1 in the logged-in session: a process started over SSH has no desktop,
# and the task is what gives it one.
#
# What it does, in order: takes the laptop's one lock (laptop-run.ps1: a second run waits for it, polling every 15 s,
# up to -LockWaitMinutes, which is -WaitMinutes when left out, then stops with a message that names the holder); puts
# the wrapper run-script-laptop.ps1 of ui\livecheck\laptop\ in place there; makes the clone's HEAD the exact commit of
# -Branch, detached (the commits the clone lacks go over as a git bundle; no branch of the clone is moved, so its main
# is left alone, and a branch that is not a descendant of the clone's last one works); with -CopyBuilds, copies this
# PC's Release window and release core into the clone's build paths (the machine builds nothing); writes the request
# (this run's id, the commit, the script's path in the repository and its arguments) into the machine's inbox; starts
# the task, which runs the script with Windows PowerShell and keeps its output; waits for the run's own
# DONE-script-<id>.md (and no other); copies the output into the main checkout's _io\script-runs (also from a git
# worktree, paths.ps1) as script-<id>-<machine>.txt and prints its tail and the DONE file, which says "ran commit
# <hash>"; fails loudly when that is not the commit it sent; gives the lock back; and exits with the script's own exit
# code. Runs in Windows PowerShell 5.1 and PowerShell 7.
#
# -NoSync leaves the clone where it is: the run is then expected to run the clone's current commit, which is printed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-script.ps1 -Script ui\livecheck\scroll-keys.ps1 -Args "-Label laptop -Fine"
#   remote-script.ps1 -Script ui\livecheck\scroll-keys.ps1 -Args "-Theme commander-compact -Fine" -NoSync
param(
  [Parameter(Mandatory = $true)][string]$Script,
  [string]$Args = '',
  # Environment variables set for the script and the window it starts, as NAME=VALUE pairs joined by ';'
  # (for example "DOTNET_TieredCompilation=0" to run the window without the runtime's tiered compilation).
  [string]$Env = '',
  [string]$Machine = 'omen',
  [string]$RemoteRepo = 'C:\Dev\cabinetos\cabinetos',
  [string]$Task = 'CabinetOS-Script',
  [string]$Branch = 'main',
  [switch]$NoSync,
  [switch]$CopyBuilds,
  [int]$WaitMinutes = 20,
  [int]$LockWaitMinutes = -1,
  [int]$Tail = 60,
  [string]$Io = ''
)
$ErrorActionPreference = 'Stop'
# Git's own ssh reads HOME for ~/.ssh, and a Git Bash points HOME elsewhere; the Windows user's folder is the one.
$env:HOME = $env:USERPROFILE
$sshConfig = Join-Path $env:USERPROFILE '.ssh\config'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$PSScriptRoot\paths.ps1"
$io = if ($Io) { $Io } else { Join-Path (Get-IoFolder) 'script-runs' }
New-Item -ItemType Directory -Force $io | Out-Null
$window = Join-Path $repo 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
$remoteRepoFwd = $RemoteRepo -replace '\\', '/'
. "$PSScriptRoot\laptop-run.ps1"
$remoteIo = $laptopIo
$remoteIoFwd = $laptopIoFwd
$runId = New-RunId
$lockMinutes = if ($LockWaitMinutes -ge 0) { $LockWaitMinutes } else { $WaitMinutes }
if (-not (Test-Path -LiteralPath (Join-Path $repo $Script))) { throw "$Script is not in this repository" }

$name = (Remote 'hostname') | Select-Object -Last 1
# A window run needs a signed-in desktop there: after a Windows Update restart nobody is signed in (2026-10-02,
# 00:41), and the task would fail late. quser lists the sessions; an "Active" one is signed in, locked or not.
$signedIn = (Remote "(quser 2>&1 | Select-String ' Active ' | Out-String).Trim()") | Select-Object -Last 1
if (-not $signedIn) { throw "nobody is signed in on $Machine (quser shows no Active session): a window run needs a signed-in desktop; sign in there and run again" }
"machine: $name, repo there: $RemoteRepo, branch: $Branch, script: $Script $Args$(if ($Env) { " (env $Env)" }), run $runId"

$runExit = 1
Enter-LaptopLock -RunId $runId -Task $Task -What "script $Script of branch $Branch" -Minutes $lockMinutes
try {
  Install-LaptopWrappers

  # 1. The exact commit, detached; the clone's branches stay where they are.
  if (-not $NoSync) {
    $sent = Sync-LaptopCommit -Branch $Branch -RunId $runId | Select-Object -Last 1
  } else {
    $sent = Get-LaptopHead
    "no sync: the run is expected to run the clone's commit $sent"
  }
  if ($CopyBuilds) {
    foreach ($needed in $window, $core) { if (-not (Test-Path -LiteralPath $needed)) { throw "$needed is missing: build it first" } }
    Remote "New-Item -ItemType Directory -Force '$RemoteRepo\ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0', '$RemoteRepo\core\target\release' | Out-Null" | Out-Null
    Send $window "$remoteRepoFwd/ui/CabinetOS/bin/x64/Release/net10.0-windows10.0.22621.0/"
    Send $core "$remoteRepoFwd/core/target/release/cabinetos-core.exe"
    "builds copied: the window of $((Get-Item (Join-Path $window 'CabinetOS.exe')).LastWriteTime.ToString('HH:mm')), the core of $((Get-Item $core).LastWriteTime.ToString('HH:mm'))"
  }

  # 2. The request the wrapper reads, then the task; the wait watches this run's DONE-script-<id>.md, and the task is
  # ended afterwards.
  $request = @("id=$runId", "commit=$sent", "script=$Script", "args=$Args", "repo=$RemoteRepo", "env=$Env")
  $local = Join-Path $env:TEMP "script-request-$runId.txt"
  Set-Content -LiteralPath $local -Value $request -Encoding ASCII
  Remote "New-Item -ItemType Directory -Force '$remoteIo\inbox' | Out-Null" | Out-Null
  Send $local "$remoteIoFwd/inbox/script-request-$runId.txt"
  Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
  Remote "if ((Get-ScheduledTask -TaskName $Task).State -eq 'Running') { Stop-ScheduledTask -TaskName $Task; Start-Sleep -Seconds 2 }; Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
  Wait-LaptopFile -RemoteFile "$remoteIo\script-runs\DONE-script-$runId.md" -Task $Task -Minutes $WaitMinutes -PollSeconds 15

  # 3. The result, home.
  $target = Join-Path $io ("script-$runId-$($name.ToLower()).txt")
  & $scpExe -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/script-runs/script-$runId.txt" $target
  if ($LASTEXITCODE -ne 0) { throw "scp from $Machine failed for script-$runId.txt" }
  $runExit = Receive-LaptopResult -RemoteDone "$remoteIo\script-runs\DONE-script-$runId.md" -LocalDone (Join-Path $io "DONE-script-$runId-$($name.ToLower()).md") -Sent $sent
  "--- the output's last $Tail lines:"
  Get-Content $target | Select-Object -Last $Tail
  "full output: $target"
  Remote "Stop-ScheduledTask -TaskName $Task" | Out-Null
} finally {
  Exit-LaptopLock -RunId $runId
}
"exit code of the script: $runExit"
exit $runExit
