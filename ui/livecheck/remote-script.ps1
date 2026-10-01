# Runs one script of this repository on another Windows machine (the creator's Omen laptop, set up 2026-10-01), in
# the machine's own logged-in session, so a script that opens the window or presses real keys (scroll-keys.ps1,
# scroll-bench.ps1, speed-review.ps1) runs there and not on this PC (docs/ui.md, "The live check on another
# machine"). The machine needs what remote-livecheck.ps1 needs, plus a scheduled task -Task that runs the wrapper
# C:\Dev\cabinetos\_io\run-script-laptop.ps1 in the logged-in session: a process started over SSH has no desktop,
# and the task is what gives it one.
#
# What it does, in order: sends the commits the machine lacks as a git bundle of -Branch and checks that branch out
# there; with -CopyBuilds, copies this PC's Release window and release core into the clone's build paths (the
# machine builds nothing); writes the request (the script's path in the repository and its arguments) into the
# machine's inbox; starts the task, which runs the script with Windows PowerShell and keeps its output; waits for
# the run's DONE-script.md; copies the output into _io\script-runs here as script-<time>-<machine>.txt and prints
# its tail. Runs in Windows PowerShell 5.1 and PowerShell 7.
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
  [int]$Tail = 60,
  [string]$Io = ''
)
$ErrorActionPreference = 'Stop'
# Git's own ssh reads HOME for ~/.ssh, and a Git Bash points HOME elsewhere; the Windows user's folder is the one.
$env:HOME = $env:USERPROFILE
$sshConfig = Join-Path $env:USERPROFILE '.ssh\config'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$io = if ($Io) { $Io } else { Join-Path (Split-Path $repo -Parent) '_io\script-runs' }
New-Item -ItemType Directory -Force $io | Out-Null
$window = Join-Path $repo 'ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
$remoteRepoFwd = $RemoteRepo -replace '\\', '/'
$remoteIo = (Split-Path $RemoteRepo -Parent) + '\_io'
$remoteIoFwd = $remoteIo -replace '\\', '/'
if (-not (Test-Path -LiteralPath (Join-Path $repo $Script))) { throw "$Script is not in this repository" }

function Remote([string]$command) {
  $out = & ssh -F $sshConfig -o BatchMode=yes $Machine $command 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -ne '' }
  if ($LASTEXITCODE -ne 0) { throw "ssh $Machine failed ($LASTEXITCODE): $($out -join ' ')" }
  $out
}
function Send([string]$local, [string]$remote) { & scp -q -r -F $sshConfig -o BatchMode=yes $local "${Machine}:$remote"; if ($LASTEXITCODE -ne 0) { throw "scp to $Machine failed for $local" } }

$name = (Remote 'hostname') | Select-Object -Last 1
"machine: $name, repo there: $RemoteRepo, branch: $Branch, script: $Script $Args$(if ($Env) { " (env $Env)" })"

# 1. The commits, as a bundle; the clone there comes from a bundle too, so it has no GitHub login.
if (-not $NoSync) {
  $ours = (& git -C $repo rev-parse $Branch).Trim()
  $theirs = (Remote "git -C $RemoteRepo rev-parse --verify -q $Branch; if (-not `$?) { 'none' }") | Select-Object -Last 1
  if ($theirs -ne $ours) {
    $bundle = Join-Path $env:TEMP 'cabinetos-script.bundle'
    $base = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
    & git -C $repo bundle create $bundle "$base..$Branch" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { & git -C $repo bundle create $bundle $Branch 2>&1 | Out-Null }
    Remote "New-Item -ItemType Directory -Force '$remoteIo\inbox' | Out-Null" | Out-Null
    Send $bundle "$remoteIoFwd/inbox/cabinetos-script.bundle"
    Remote "git -C $RemoteRepo fetch -q '$remoteIo\inbox\cabinetos-script.bundle' '${Branch}:refs/remotes/bundle/$Branch'; git -C $RemoteRepo checkout -q -B $Branch refs/remotes/bundle/$Branch; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
  } else {
    Remote "git -C $RemoteRepo checkout -q $Branch; git -C $RemoteRepo log --oneline -1" | Select-Object -Last 1
  }
}
if ($CopyBuilds) {
  foreach ($needed in $window, $core) { if (-not (Test-Path -LiteralPath $needed)) { throw "$needed is missing: build it first" } }
  Remote "New-Item -ItemType Directory -Force '$RemoteRepo\ui\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0', '$RemoteRepo\core\target\release' | Out-Null" | Out-Null
  Send $window "$remoteRepoFwd/ui/CabinetOS/bin/x64/Release/net10.0-windows10.0.22621.0/"
  Send $core "$remoteRepoFwd/core/target/release/cabinetos-core.exe"
  "builds copied: the window of $((Get-Item (Join-Path $window 'CabinetOS.exe')).LastWriteTime.ToString('HH:mm')), the core of $((Get-Item $core).LastWriteTime.ToString('HH:mm'))"
}

# 2. The request the wrapper reads, then the task; the wait watches DONE-script.md, and the task is ended afterwards.
$request = @("script=$Script", "args=$Args", "repo=$RemoteRepo", "env=$Env")
$local = Join-Path $env:TEMP 'script-request.txt'
Set-Content -LiteralPath $local -Value $request -Encoding ASCII
Remote "New-Item -ItemType Directory -Force '$remoteIo\inbox' | Out-Null" | Out-Null
Send $local "$remoteIoFwd/inbox/script-request.txt"
$doneTicks = "if (Test-Path '$remoteIo\script-runs\DONE-script.md') { (Get-Item '$remoteIo\script-runs\DONE-script.md').LastWriteTimeUtc.Ticks } else { 0 }"
$before = (Remote $doneTicks) | Select-Object -Last 1
Remote "if ((Get-ScheduledTask -TaskName $Task).State -eq 'Running') { Stop-ScheduledTask -TaskName $Task; Start-Sleep -Seconds 2 }; Start-ScheduledTask -TaskName $Task; (Get-ScheduledTask -TaskName $Task).State" | Select-Object -Last 1 | ForEach-Object { "task started: $_" }
$deadline = (Get-Date).AddMinutes($WaitMinutes)
do {
  Start-Sleep -Seconds 15
  $now = (Remote $doneTicks) | Select-Object -Last 1
} while ($now -eq $before -and (Get-Date) -lt $deadline)
if ($now -eq $before) { throw "no new DONE-script.md on $Machine after $WaitMinutes minutes" }

# 3. The result, home.
$latest = (Remote "(Get-ChildItem '$remoteIo\script-runs\script-*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1).Name") | Select-Object -Last 1
$target = Join-Path $io ($latest -replace '\.txt$', "-$($name.ToLower()).txt")
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/script-runs/$latest" $target
& scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$remoteIoFwd/script-runs/DONE-script.md" (Join-Path $io "DONE-script-$($name.ToLower()).md")
Get-Content (Join-Path $io "DONE-script-$($name.ToLower()).md")
"--- the output's last $Tail lines:"
Get-Content $target | Select-Object -Last $Tail
"full output: $target"
Remote "Stop-ScheduledTask -TaskName $Task" | Out-Null
