# Builds the window and runs its tests on another Windows machine (the creator's Omen laptop, set up 2026-10-01), so
# the end-to-end tests' windows open there, not on this PC (docs/ui.md, "The live check on another machine"). The
# machine needs what remote-livecheck.ps1 needs, plus the .NET SDK (installed per user there) and a scheduled task
# -Task that runs the wrapper C:\Dev\cabinetos\_io\run-tests-laptop.ps1 in the logged-in session: a test that opens
# a window needs a desktop, and a process started over SSH has none, so the task is what gives it one.
#
# What it does, in order: takes the laptop's one lock (laptop-run.ps1: a second run waits for it, polling every 15 s,
# up to -LockWaitMinutes, which is -WaitMinutes when left out, then stops with a message that names the holder); puts
# the wrapper run-tests-laptop.ps1 of ui\livecheck\laptop\ in place there; makes the clone's HEAD the exact commit of
# -Branch, detached (the commits the clone lacks go over as a git bundle; no branch of the clone is moved, so its main
# is left alone, and a branch that is not a descendant of the clone's last one works); copies this PC's release core
# into the clone when the machine's differs (it builds no Rust); writes the request (this run's id, the commit, the
# filter, whether the end-to-end tests run) into the machine's inbox; starts the task, which builds with warnings as
# errors and runs `dotnet test`; waits for the run's own DONE-tests-<id>.md (and no other); copies the full output
# into the main checkout's _io\test-runs, also from a git worktree (paths.ps1); prints DONE, which says "ran commit
# <hash>"; fails loudly when that is not the commit it sent; gives the lock back; and exits with the run's own exit
# code (the build's when the build failed, else the tests'). Runs in Windows PowerShell 5.1 and PowerShell 7.
#
# -NoSync leaves the clone where it is: the run is then expected to run the clone's current commit, which is printed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-tests.ps1 -EndToEnd
#   remote-tests.ps1 -Filter "FullyQualifiedName~KeysEndToEnd" -EndToEnd
#   remote-tests.ps1 -Branch worktree-agent-xyz          # a worktree's branch instead of main
param(
  [string]$Machine = 'omen',
  [string]$RemoteRepo = 'C:\Dev\cabinetos\cabinetos',
  [string]$Task = 'CabinetOS-Tests',
  [string]$Branch = 'main',
  [string]$Filter = '',
  [switch]$EndToEnd,
  [switch]$NoSync,
  [int]$WaitMinutes = 40,
  [int]$LockWaitMinutes = -1,
  [string]$Io = ''
)
$ErrorActionPreference = 'Stop'
# Git's own ssh reads HOME for ~/.ssh, and a Git Bash points HOME elsewhere; the Windows user's folder is the one.
$env:HOME = $env:USERPROFILE
$sshConfig = Join-Path $env:USERPROFILE '.ssh\config'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$PSScriptRoot\paths.ps1"
$io = if ($Io) { $Io } else { Join-Path (Get-IoFolder) 'test-runs' }
New-Item -ItemType Directory -Force $io | Out-Null
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
$remoteRepoFwd = $RemoteRepo -replace '\\', '/'
. "$PSScriptRoot\laptop-run.ps1"
$remoteIo = $laptopIo
$remoteIoFwd = $laptopIoFwd
$runId = New-RunId
$lockMinutes = if ($LockWaitMinutes -ge 0) { $LockWaitMinutes } else { $WaitMinutes }

$name = (Remote 'hostname') | Select-Object -Last 1
# A window run needs a signed-in desktop there: after a Windows Update restart nobody is signed in (2026-10-02,
# 00:41), and the task would fail late. quser lists the sessions; an "Active" one is signed in, locked or not.
$signedIn = (Remote "(quser 2>&1 | Select-String ' Active ' | Out-String).Trim()") | Select-Object -Last 1
if (-not $signedIn) { throw "nobody is signed in on $Machine (quser shows no Active session): a window run needs a signed-in desktop; sign in there and run again" }
"machine: $name, repo there: $RemoteRepo, branch: $Branch, run $runId"

$runExit = 1
Enter-LaptopLock -RunId $runId -Task $Task -What "tests of branch $Branch$(if ($EndToEnd) { ' (end-to-end)' })$(if ($Filter) { " filter $Filter" })" -Minutes $lockMinutes
try {
  Install-LaptopWrappers

  # 1. The exact commit, detached; the clone's branches stay where they are.
  if (-not $NoSync) {
    $sent = Sync-LaptopCommit -Branch $Branch -RunId $runId | Select-Object -Last 1
  } else {
    $sent = Get-LaptopHead
    "no sync: the run is expected to run the clone's commit $sent"
  }
  # The core goes over when the machine's differs from this PC's (a stale core there fails the protocol-version test),
  # and cabinetos-cli.exe with it: the shell the core starts finds the CLI next to the core.
  $remoteCore = "$RemoteRepo\core\target\release\cabinetos-core.exe"
  $theirCore = (Remote "if (Test-Path '$remoteCore') { (Get-FileHash -LiteralPath '$remoteCore' -Algorithm SHA256).Hash } else { 'none' }") | Select-Object -Last 1
  if (-not (Test-Path -LiteralPath $core)) {
    if ($theirCore -eq 'none') { throw "$core is missing here and on ${Machine}: build it first" }
    "the machine keeps its own release core (none built here)"
  } elseif ($theirCore -ne (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash) {
    Remote "New-Item -ItemType Directory -Force '$RemoteRepo\core\target\release' | Out-Null" | Out-Null
    Send $core "$remoteRepoFwd/core/target/release/cabinetos-core.exe"
    $cli = Join-Path $repo 'core\target\release\cabinetos-cli.exe'
    if (Test-Path -LiteralPath $cli) { Send $cli "$remoteRepoFwd/core/target/release/cabinetos-cli.exe" }
    "release core copied (the CLI with it)"
  } else {
    $cli = Join-Path $repo 'core\target\release\cabinetos-cli.exe'
    $theirCli = (Remote "Test-Path '$RemoteRepo\core\target\release\cabinetos-cli.exe'") | Select-Object -Last 1
    if ($theirCli -ne 'True' -and (Test-Path -LiteralPath $cli)) { Send $cli "$remoteRepoFwd/core/target/release/cabinetos-cli.exe"; "the CLI copied next to the core" }
  }

  # 2. The request the wrapper reads, then the task; the wait watches this run's DONE-tests-<id>.md, and the task is
  # ended afterwards.
  $request = @("id=$runId", "commit=$sent", "filter=$Filter", "e2e=$(if ($EndToEnd) { 'yes' } else { 'no' })", "repo=$RemoteRepo")
  $local = Join-Path $env:TEMP "tests-request-$runId.txt"
  Set-Content -LiteralPath $local -Value $request -Encoding ASCII
  Remote "New-Item -ItemType Directory -Force '$remoteIo\inbox' | Out-Null" | Out-Null
  Send $local "$remoteIoFwd/inbox/tests-request-$runId.txt"
  Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
  Remote "if ((Get-ScheduledTask -TaskName $Task).State -eq 'Running') { Stop-ScheduledTask -TaskName $Task; Start-Sleep -Seconds 2 }; Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
  Wait-LaptopFile -RemoteFile "$remoteIo\test-runs\DONE-tests-$runId.md" -Task $Task -Minutes $WaitMinutes -PollSeconds 20

  # 3. The result, home.
  $target = Join-Path $io ("tests-$runId-$($name.ToLower()).txt")
  & $scpExe -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/test-runs/tests-$runId.txt" $target
  if ($LASTEXITCODE -ne 0) { throw "scp from $Machine failed for tests-$runId.txt" }
  $runExit = Receive-LaptopResult -RemoteDone "$remoteIo\test-runs\DONE-tests-$runId.md" -LocalDone (Join-Path $io "DONE-tests-$runId-$($name.ToLower()).md") -Sent $sent
  "full output: $target"
  Remote "Stop-ScheduledTask -TaskName $Task" | Out-Null
} finally {
  Exit-LaptopLock -RunId $runId
}
"exit code of the tests: $runExit"
exit $runExit
