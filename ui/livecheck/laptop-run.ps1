# What remote-livecheck.ps1, remote-tests.ps1 and remote-script.ps1 share: the one lock of the Omen laptop, the
# exact-commit checkout of its single clone, the wait for a run's own result, and the laptop's side of the three runs
# (the wrappers in ui\livecheck\laptop\). Dot-source it after these exist in the caller: $Machine, $sshConfig, $repo,
# $RemoteRepo. It defines Remote and Send, which the three scripts used to define each. Runs in Windows PowerShell 5.1
# and PowerShell 7 (docs/ui.md, "The live check on another machine").
#
# Why: the laptop has ONE clone, one desktop and three scheduled tasks, and several sessions use them at once. On
# 2026-10-02/03 two runs collided (a branch checked out under a running live check, a CS2012 file lock in a build, a wait
# loop that returned another run's result, a request taken by the wrong run), a branch that was not a descendant of the
# clone's branch failed at once, and a clone that was behind ran old code without a word.
#
# The lock is the file C:\Dev\cabinetos\_io\laptop.lock on the laptop. It is created with "create new", which only one
# run can win. It holds the run's id, who holds it, the request, the task, the time it was taken and the time it counts
# as dead: the task's own maximum run time (its ExecutionTimeLimit: 1 h for CabinetOS-LiveCheck, 2 h for the other two)
# plus 10 minutes for the sync before the task starts. A run takes the lock before it touches the clone and gives it back
# in a finally block, so an error or Ctrl-C releases it; only a killed PC process, a power cut or a crash leaves it, and
# then it dies by its time. A waiting run that finds a dead lock moves it aside (laptop.lock.dead-<id>, kept as the
# record), writes a line into _io\laptop-lock.log there and prints it, and takes the lock. The log also has every
# acquire and release. The laptop's own clock judges the time, so the PC's clock does not matter.
#
# Every run has an id (yyyy-MM-dd-HHmm-xxxx). The request file, the output and the DONE file all carry it in their
# names, the wrappers on the laptop read the id from the lock (the holder's is the only run that may be going), and a
# wait loop looks for the file with its own id and nothing else.

# Remote runs a command on the machine and returns its output lines. Its default shell is PowerShell.
function Remote([string]$command) {
  $out = & ssh -F $sshConfig -o BatchMode=yes $Machine $command 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -ne '' }
  if ($LASTEXITCODE -ne 0) { throw "ssh $Machine failed ($LASTEXITCODE): $($out -join ' ')" }
  $out
}
function Send([string]$local, [string]$remote) { & scp -q -r -F $sshConfig -o BatchMode=yes $local "${Machine}:$remote"; if ($LASTEXITCODE -ne 0) { throw "scp to $Machine failed for $local" } }

# Runs a script text on the machine as an encoded command, so it needs no quoting on a command line. Whatever it
# wants to hand back it writes as a line starting with the prefix the caller looks for; error text from a failed
# command can come back as noise, which the prefix keeps apart.
function Invoke-RemoteScript([string]$text) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ProgressPreference = 'SilentlyContinue'`r`n" + $text))
  Remote "powershell -NoProfile -NonInteractive -EncodedCommand $encoded"
}

# Where the machine's exchange folder is, and the same with forward slashes for scp.
$laptopIo = (Split-Path $RemoteRepo -Parent) + '\_io'
$laptopIoFwd = $laptopIo -replace '\\', '/'
$laptopLockPoll = 15

function New-RunId { (Get-Date -Format 'yyyy-MM-dd-HHmm') + '-' + ('{0:x4}' -f (Get-Random -Maximum 65536)) }

# Text that goes into the lock file and the log: plain characters only, so it needs no escaping anywhere.
function ConvertTo-PlainText([string]$text) { ($text -replace "[^A-Za-z0-9 _.,:;/()@+=#-]", '_') }

# The lock's two laptop-side scripts. @@ID@@ and the like are replaced before they are sent.
$laptopLockTake = @'
$io = '@@IO@@'
$lock = "$io\laptop.lock"
function Say([string]$text) { "LOCK:$text" }
function Write-LockLog([string]$text) { Add-Content -LiteralPath "$io\laptop-lock.log" -Value ('{0:yyyy-MM-dd HH:mm:ss}Z {1}' -f [DateTime]::UtcNow, $text) }
function Read-Lock {
  $h = @{}
  foreach ($line in (Get-Content -LiteralPath $lock -ErrorAction Stop)) { $k, $v = $line -split '=', 2; if ($k) { $h[$k] = $v } }
  $h
}
function Describe([hashtable]$h) { "$($h['holder']), since $($h['started']), request: $($h['request']), counts as dead after $($h['expires'])" }
$lines = $null
function New-Lock {
  try {
    $stream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.Encoding]::ASCII.GetBytes(($lines -join "`r`n") + "`r`n"); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Close() }
    return $true
  } catch [IO.IOException] { return $false }
}
try {
  New-Item -ItemType Directory -Force $io | Out-Null
  $now = [DateTime]::UtcNow
  $limit = [TimeSpan]::FromHours(2)
  try { $limit = [Xml.XmlConvert]::ToTimeSpan((Get-ScheduledTask -TaskName '@@TASK@@').Settings.ExecutionTimeLimit) } catch { }
  if ($limit -le [TimeSpan]::Zero) { $limit = [TimeSpan]::FromHours(2) }
  $expires = $now + $limit + [TimeSpan]::FromMinutes(10)
  $lines = @('id=@@ID@@', 'holder=@@HOLDER@@', 'request=@@REQUEST@@', 'task=@@TASK@@', ('started=' + $now.ToString('s') + 'Z'), ('expires=' + $expires.ToString('s') + 'Z'))
  $tookOver = $null
  $created = New-Lock
  if (-not $created) {
    Start-Sleep -Milliseconds 300
    $held = $null
    try { $held = Read-Lock } catch { }
    $dead = $false
    if ($held) {
      $deadAt = [DateTime]::MinValue
      if ($held['expires'] -and [DateTime]::TryParse($held['expires'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$deadAt)) { $dead = $now -gt $deadAt }
      elseif ((Get-Item -LiteralPath $lock -ErrorAction SilentlyContinue).LastWriteTimeUtc -lt $now.AddMinutes(-5)) { $dead = $true }
    }
    if ($held -and $dead) {
      $before = Get-Content -LiteralPath $lock -Raw
      $aside = "$lock.dead-@@ID@@"
      $moved = $false
      try { [IO.File]::Move($lock, $aside); $moved = $true } catch { }
      if ($moved) {
        if ((Get-Content -LiteralPath $aside -Raw) -ne $before) {
          # Another run had already taken the dead lock over and this was its new one: put it back.
          try { [IO.File]::Move($aside, $lock) } catch { }
        } else {
          $tookOver = Describe $held
          Write-LockLog "dead lock taken over by @@ID@@ (@@HOLDER@@): was $tookOver"
          $created = New-Lock
        }
      }
    }
  }
  if ($created) {
    Write-LockLog 'acquired by @@ID@@ (@@HOLDER@@), request: @@REQUEST@@'
    if ($tookOver) { Say "TOOKOVER|$tookOver" }
    Say 'ACQUIRED'
  } else {
    $h = $null
    try { $h = Read-Lock } catch { }
    if ($h) { Say ('HELD|' + (Describe $h)) } else { Say 'HELD|the lock file is there but could not be read just now' }
  }
} catch { Say ('ERROR|' + $_.Exception.Message) }
'@

$laptopLockGive = @'
$io = '@@IO@@'
$lock = "$io\laptop.lock"
try {
  $h = @{}
  if (Test-Path -LiteralPath $lock) { foreach ($line in (Get-Content -LiteralPath $lock)) { $k, $v = $line -split '=', 2; if ($k) { $h[$k] = $v } } }
  if ($h['id'] -eq '@@ID@@') {
    Remove-Item -LiteralPath $lock -Force
    Add-Content -LiteralPath "$io\laptop-lock.log" -Value ('{0:yyyy-MM-dd HH:mm:ss}Z released by @@ID@@' -f [DateTime]::UtcNow)
    'LOCK:RELEASED'
  } elseif ($h['id']) { 'LOCK:NOTMINE|now held by ' + $h['holder'] } else { 'LOCK:NOTMINE|no lock file' }
} catch { 'LOCK:ERROR|' + $_.Exception.Message }
'@

# Takes the lock of the laptop for this run, or waits for it: polls every 15 s, up to $Minutes (the caller's
# -WaitMinutes), then stops with a message that names the holder. Call it right before the try block whose finally
# gives the lock back.
function Enter-LaptopLock([string]$RunId, [string]$Task, [string]$What, [int]$Minutes) {
  $holder = ConvertTo-PlainText "$env:COMPUTERNAME/$env:USERNAME, $What, pid $PID"
  $text = $laptopLockTake.Replace('@@IO@@', $laptopIo).Replace('@@ID@@', $RunId).Replace('@@TASK@@', $Task).Replace('@@HOLDER@@', $holder).Replace('@@REQUEST@@', (ConvertTo-PlainText $What))
  $deadline = (Get-Date).AddMinutes($Minutes)
  $polls = 0
  while ($true) {
    $answer = @(Invoke-RemoteScript $text | Where-Object { $_ -like 'LOCK:*' }) | Select-Object -Last 1
    if (-not $answer) { throw "the lock check on $Machine gave no answer" }
    if ($answer -like 'LOCK:ERROR|*') { throw "the lock check on $Machine failed: $($answer.Substring(11))" }
    if ($answer -like 'LOCK:TOOKOVER|*') { "the laptop's lock was dead and is taken over (recorded in _io\laptop-lock.log there): $($answer.Substring(14))"; $answer = 'LOCK:ACQUIRED' }
    if ($answer -eq 'LOCK:ACQUIRED') { "laptop lock taken: run $RunId ($What)"; return }
    $info = $answer.Substring(10)
    if ((Get-Date) -ge $deadline) { throw "the laptop stayed busy for $Minutes minutes, so nothing was started and nothing was changed there. Held by: $info. Run again later, or give -WaitMinutes (or -LockWaitMinutes) a larger number." }
    if ($polls % 4 -eq 0) { "the laptop is busy, waiting for its lock (checking every $laptopLockPoll s, for up to $Minutes min). Held by: $info" }
    $polls++
    Start-Sleep -Seconds $laptopLockPoll
  }
}

# Gives the lock back if it is still this run's, and removes this run's own files from the machine's inbox.
function Exit-LaptopLock([string]$RunId) {
  try {
    Remote "Get-ChildItem '$laptopIo\inbox' -Filter '*$RunId*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue" | Out-Null
    $answer = @(Invoke-RemoteScript $laptopLockGive.Replace('@@IO@@', $laptopIo).Replace('@@ID@@', $RunId) | Where-Object { $_ -like 'LOCK:*' }) | Select-Object -Last 1
    if ($answer -eq 'LOCK:RELEASED') { "laptop lock given back: run $RunId" }
    else { "WARNING: the laptop lock was not given back by this run ($answer); a lock counts as dead after its task's maximum run time" }
  } catch { "WARNING: the laptop lock could not be given back ($($_.Exception.Message)); a lock counts as dead after its task's maximum run time" }
}

# The wrappers on the laptop (ui\livecheck\laptop\*.ps1) live in its _io folder, which no git commit reaches; the copy
# there is made the same as the repository's, under the lock, so a task never runs a wrapper older than the script that
# sent the request.
function Install-LaptopWrappers {
  $names = @('run-livecheck-laptop.ps1', 'run-script-laptop.ps1', 'run-tests-laptop.ps1')
  $list = ($names | ForEach-Object { "'$_'" }) -join ','
  $theirs = @{}
  Remote "foreach (`$n in $list) { if (Test-Path ('$laptopIo\' + `$n)) { `$n + '=' + (Get-FileHash -LiteralPath ('$laptopIo\' + `$n) -Algorithm SHA256).Hash } else { `$n + '=none' } }" | ForEach-Object { $k, $v = $_ -split '=', 2; $theirs[$k] = $v }
  foreach ($name in $names) {
    $local = Join-Path $PSScriptRoot "laptop\$name"
    if ((Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -ne $theirs[$name]) { Send $local "$laptopIoFwd/$name"; "wrapper copied to the laptop: $name" }
  }
}

# Makes the clone's HEAD the exact commit of $Branch: the commits the clone lacks go over as a git bundle (it has no
# GitHub login), the commit is fetched from it and checked out DETACHED. No branch of the clone is moved or made, so
# its main is left alone and a branch that is not a descendant of whatever was there before works as well as one that
# is. The clone's HEAD is read back and must be that commit. Returns the full hash.
function Sync-LaptopCommit([string]$Branch, [string]$RunId) {
  $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $sent = "$(& git -C $repo rev-parse --verify "$Branch^{commit}" 2>$null)".Trim()
  $code = $LASTEXITCODE
  $ErrorActionPreference = $eap
  if ($code -ne 0 -or $sent -notmatch '^[0-9a-f]{40}$') { throw "$Branch is not a branch, tag or commit of this repository" }
  $has = (Remote "git -C $RemoteRepo cat-file -e $sent 2>&1 | Out-Null; if (`$LASTEXITCODE -eq 0) { 'yes' } else { 'no' }") | Select-Object -Last 1
  if ($has -ne 'yes') {
    $head = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
    $bundle = Join-Path $env:TEMP "cabinetos-$RunId.bundle"
    # Only what the clone's HEAD does not have; when this PC does not know that HEAD, or the bundle is empty for
    # git, everything of the branch. git writes to stderr either way, which 5.1 turns into a stop.
    $ErrorActionPreference = 'Continue'
    & git -C $repo bundle create $bundle "$head..$Branch" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { & git -C $repo bundle create $bundle $Branch 2>&1 | Out-Null }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $eap
    if ($code -ne 0) { throw "git could not make a bundle of $Branch" }
    Remote "New-Item -ItemType Directory -Force '$laptopIo\inbox' | Out-Null" | Out-Null
    Send $bundle "$laptopIoFwd/inbox/cabinetos-$RunId.bundle"
    Remove-Item -LiteralPath $bundle -Force -ErrorAction SilentlyContinue
    Remote "git -C $RemoteRepo fetch -q '$laptopIo\inbox\cabinetos-$RunId.bundle' $Branch" | Out-Null
  }
  Remote "git -C $RemoteRepo checkout -q --detach $sent" | Out-Null
  $now = (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1
  if ($now -ne $sent) { throw "the clone on $Machine is at $now after the checkout, not at $sent ($Branch): nothing was run" }
  Write-Host "clone at $sent (detached; $Branch as sent here)"
  $sent
}

# The clone's current commit, for a run that does not sync (-NoSync): what the run is expected to run.
function Get-LaptopHead { (Remote "git -C $RemoteRepo rev-parse HEAD") | Select-Object -Last 1 }

# Waits until $RemoteFile (the run's own DONE file, written whole in one step) exists on the machine. The task must stay
# Running meanwhile (the live check's Notepad keeps it so); a task that has been something else for four checks in a row
# without a DONE file died, and the wait says so instead of running to its limit.
function Wait-LaptopFile([string]$RemoteFile, [string]$Task, [int]$Minutes, [int]$PollSeconds) {
  $deadline = (Get-Date).AddMinutes($Minutes)
  $idle = 0
  while ($true) {
    $state = (Remote "if (Test-Path '$RemoteFile') { 'done' } else { 'wait ' + (Get-ScheduledTask -TaskName $Task).State }") | Select-Object -Last 1
    if ($state -eq 'done') { return }
    if ($state -eq 'wait Running') { $idle = 0 } else { $idle++ }
    if ($idle -ge 4) { throw "the task $Task on $Machine is $($state -replace '^wait ','') and left no ${RemoteFile}: the run ended without a result" }
    if ((Get-Date) -ge $deadline) { throw "no $RemoteFile on $Machine after $Minutes minutes" }
    Start-Sleep -Seconds $PollSeconds
  }
}

# Copies the run's DONE file home, prints it, and judges it: the commit the run says it ran must be the commit sent,
# and the run's own exit code is returned (the caller exits with it). A mismatch stops the script loudly. It prints
# with Write-Host, so that what it returns is the exit code alone.
function Receive-LaptopResult([string]$RemoteDone, [string]$LocalDone, [string]$Sent) {
  & scp -q -F $sshConfig -o BatchMode=yes "${Machine}:$($RemoteDone -replace '\\', '/')" $LocalDone
  if ($LASTEXITCODE -ne 0) { throw "scp from $Machine failed for $RemoteDone" }
  $text = @(Get-Content -LiteralPath $LocalDone)
  $text | ForEach-Object { Write-Host $_ }
  $ran = ($text | Where-Object { $_ -match '^ran commit [0-9a-f]{7,40}\s*$' } | Select-Object -Last 1)
  $exit = ($text | Where-Object { $_ -match '^exit code -?\d+\s*$' } | Select-Object -Last 1)
  if (-not $ran) { throw "the run's DONE file has no 'ran commit' line, so it cannot be known which code ran" }
  $hash = ($ran -replace '^ran commit ', '').Trim()
  if (-not $Sent.StartsWith($hash)) { throw "WRONG CODE RAN: the laptop ran commit $hash, but $Sent was sent. The result is not for this commit." }
  Write-Host "ran commit $hash (the commit sent)"
  if (-not $exit) { throw "the run's DONE file has no 'exit code' line" }
  [int]($exit -replace '^exit code ', '').Trim()
}
