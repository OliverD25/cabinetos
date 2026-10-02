# Builds the window and runs its tests on another Windows machine (the creator's Omen laptop, set up 2026-10-01), so
# the end-to-end tests' windows open there, not on this PC (docs/ui.md, "The live check on another machine"). The
# machine needs what remote-livecheck.ps1 needs, plus the .NET SDK (installed per user there) and a scheduled task
# -Task that runs the wrapper C:\Dev\cabinetos\_io\run-tests-laptop.ps1 in the logged-in session: a test that opens
# a window needs a desktop, and a process started over SSH has none, so the task is what gives it one.
#
# What it does, in order: sends the commits the machine lacks as a git bundle of -Branch and checks that branch out
# there; copies this PC's release core into the clone when the machine has none (it builds no Rust); writes the
# request (the filter, whether the end-to-end tests run, the build configuration) into the machine's inbox; starts
# the task, which builds with warnings as errors and runs `dotnet test`; waits for the run's DONE-tests.md; copies
# the full output into the main checkout's _io\test-runs, also from a git worktree (paths.ps1). Runs in Windows
# PowerShell 5.1 and PowerShell 7.
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
$remoteIo = (Split-Path $RemoteRepo -Parent) + '\_io'
$remoteIoFwd = $remoteIo -replace '\\', '/'

function Remote([string]$command) {
  $out = & ssh -F $sshConfig -o BatchMode=yes $Machine $command 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -ne '' }
  if ($LASTEXITCODE -ne 0) { throw "ssh $Machine failed ($LASTEXITCODE): $($out -join ' ')" }
  $out
}
function Send([string]$local, [string]$remote) { & scp -q -r -F $sshConfig -o BatchMode=yes $local "${Machine}:$remote"; if ($LASTEXITCODE -ne 0) { throw "scp to $Machine failed for $local" } }

$name = (Remote 'hostname') | Select-Object -Last 1
# A window run needs a signed-in desktop there: after a Windows Update restart nobody is signed in (2026-10-02,
# 00:41), and the task would fail late. quser lists the sessions; an "Active" one is signed in, locked or not.
$signedIn = (Remote "(quser 2>&1 | Select-String ' Active ' | Out-String).Trim()") | Select-Object -Last 1
if (-not $signedIn) { throw "nobody is signed in on $Machine (quser shows no Active session): a window run needs a signed-in desktop; sign in there and run again" }
"machine: $name, repo there: $RemoteRepo, branch: $Branch"

# 1. The commits, as a bundle; the clone there comes from a bundle too, so it has no GitHub login.
if (-not $NoSync) {
  $ours = (& git -C $repo rev-parse $Branch).Trim()
  $theirs = (Remote "git -C $RemoteRepo rev-parse --verify -q $Branch; if (-not `$?) { 'none' }") | Select-Object -Last 1
  if ($theirs -ne $ours) {
    $bundle = Join-Path $env:TEMP 'cabinetos-tests.bundle'
    $base = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
    # A live check may have sent the commits already (it fast-forwards the clone's current branch), leaving only
    # the branch name to make there; an empty bundle makes git write to stderr, which 5.1 turns into a stop.
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $missing = & git -C $repo rev-list --count "$base..$Branch" 2>$null
    $ErrorActionPreference = $eap
    if ($LASTEXITCODE -ne 0) { $missing = 'unknown' }
    if ("$missing".Trim() -eq '0') {
      Remote "git -C $RemoteRepo checkout -q -B $Branch $ours; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
      $bundle = $null
    } elseif ($missing -eq 'unknown') { & git -C $repo bundle create $bundle $Branch 2>&1 | Out-Null }
    else { & git -C $repo bundle create $bundle "$base..$Branch" 2>&1 | Out-Null }
  }
  if ($bundle) {
    Remote "New-Item -ItemType Directory -Force '$remoteIo\inbox' | Out-Null" | Out-Null
    Send $bundle "$remoteIoFwd/inbox/cabinetos-tests.bundle"
    Remote "git -C $RemoteRepo fetch -q '$remoteIo\inbox\cabinetos-tests.bundle' '${Branch}:refs/remotes/bundle/$Branch'; git -C $RemoteRepo checkout -q -B $Branch refs/remotes/bundle/$Branch; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
  } else {
    Remote "git -C $RemoteRepo checkout -q $Branch; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
  }
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

# 2. The request the wrapper reads, then the task; the wait watches DONE-tests.md, and the task is ended afterwards.
$request = @("filter=$Filter", "e2e=$(if ($EndToEnd) { 'yes' } else { 'no' })", "repo=$RemoteRepo")
$local = Join-Path $env:TEMP 'tests-request.txt'
Set-Content -LiteralPath $local -Value $request -Encoding ASCII
Send $local "$remoteIoFwd/inbox/tests-request.txt"
$doneTicks = "if (Test-Path '$remoteIo\test-runs\DONE-tests.md') { (Get-Item '$remoteIo\test-runs\DONE-tests.md').LastWriteTimeUtc.Ticks } else { 0 }"
$before = (Remote $doneTicks) | Select-Object -Last 1
Remote "if ((Get-ScheduledTask -TaskName $Task).State -eq 'Running') { Stop-ScheduledTask -TaskName $Task; Start-Sleep -Seconds 2 }; Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
$deadline = (Get-Date).AddMinutes($WaitMinutes)
do {
  Start-Sleep -Seconds 20
  $now = (Remote $doneTicks) | Select-Object -Last 1
} while ($now -eq $before -and (Get-Date) -lt $deadline)
if ($now -eq $before) { throw "no new DONE-tests.md on $Machine after $WaitMinutes minutes" }

# 3. The result, home.
$latest = (Remote "(Get-ChildItem '$remoteIo\test-runs\tests-*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1).Name") | Select-Object -Last 1
$target = Join-Path $io ($latest -replace '\.txt$', "-$($name.ToLower()).txt")
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/test-runs/$latest" $target
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/test-runs/DONE-tests.md" (Join-Path $io "DONE-tests-$($name.ToLower()).md")
Get-Content (Join-Path $io "DONE-tests-$($name.ToLower()).md")
"full output: $target"
Remote "Stop-ScheduledTask -TaskName $Task" | Out-Null
