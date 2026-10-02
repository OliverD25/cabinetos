# The guest's side of vm-indexer-check.ps1 (docs/release.md, "The indexer service"; docs/ui.md, "The live check in a
# virtual machine"): runs inside the VirtualBox VM "CabinetOS-LiveCheck". The script and its arguments
# (indexer-args.json) are in the project's root folder on the host, which the VM sees as the share \\VBoxSvr\cabinetos
# (the drive X: is that share, mapped for the user's own session only: a process with an elevated token does not see X:,
# so every path here goes through the share's UNC name).
#
# Three phases:
#  -Phase install    needs an ELEVATED process: copies the release folder to Program Files on the VM's own disk (a
#     service must run from a local disk), runs `cabinetos-indexer --install` there, and records what the service
#     manager says (`sc qc`, `sc query`, the registry's Start and DelayedAutostart). The service must be registered as
#     AUTO_START (DELAYED) and be RUNNING.
#  -Phase check      after the VM's Windows has restarted and nobody has started the service: waits for the service to be
#     RUNNING and records when it was first seen, how long after the boot began, that its process is younger than the
#     boot, that its pipe exists and what its log says. It reads only, so guest control can run it.
#  -Phase uninstall  needs an ELEVATED process: `cabinetos-indexer --uninstall`, the service must be gone, and the folder
#     of -Phase install is removed.
# Guest control (VBoxManage guestcontrol) gives the VM user's administrator token with UAC applied: medium integrity,
# the Administrators group "deny only". Installing a service needs the full token, and nothing can answer UAC's prompt
# there, so -Phase install and -Phase uninstall are run by a person in a PowerShell started with "Run as
# administrator" inside the VM (docs/release.md says how); -Phase check is run by vm-indexer-check.ps1.
#
# Each phase writes vm-indexer-result-<tag>-<phase>.json into the run's output folder (its state is `done`, `stopped`
# or `needs-elevation`); vm-indexer-progress.txt there says how far it got. A person at the VM sees the same lines.
param(
  [ValidateSet('install', 'check', 'uninstall')][string]$Phase = 'install',
  [string]$ArgsFile = '\\VBoxSvr\cabinetos\_io\indexer-check\indexer-args.json'
)
$ErrorActionPreference = 'Continue'
$share = '\\VBoxSvr\cabinetos'
# A path as the host wrote it (X:\...) in the share's UNC form, which every session reaches.
function Unc([string]$path) { if ($path -like 'X:*') { $share + $path.Substring(2) } else { $path } }
$ArgsFile = Unc $ArgsFile
$left = 36
while (-not (Test-Path -LiteralPath $ArgsFile) -and $left -gt 0) { Start-Sleep -Seconds 5; $left-- }
if (-not (Test-Path -LiteralPath $ArgsFile)) { Write-Host "cannot read $ArgsFile"; exit 2 }
$a = Get-Content -Raw -LiteralPath $ArgsFile | ConvertFrom-Json
$tag = $a.tag
$io = Unc $a.io
$release = Unc $a.release
$progressFile = Join-Path $io 'vm-indexer-progress.txt'
$resultFile = Join-Path $io "vm-indexer-result-$tag-$Phase.json"
$service = 'cabinetos-indexer'
$folder = Join-Path $env:ProgramFiles 'CabinetOS-indexer-check'
$exe = Join-Path $folder 'cabinetos-indexer.exe'
$details = New-Object System.Collections.ArrayList
$ok = $true

function Progress([string]$text) { $line = "$(Get-Date -Format HH:mm:ss) [$Phase] $text"; $line | Add-Content -LiteralPath $progressFile; Write-Host $line }
function Note([string]$text) { [void]$details.Add($text); Progress $text }
function Fail([string]$text) { $script:ok = $false; Note "FAILED: $text" }
function Save-Result([string]$state) {
  $result = [ordered]@{ tag = $tag; phase = $Phase; state = $state; ok = $ok; machine = $env:COMPUTERNAME; ended = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); details = @($details) }
  $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultFile -Encoding UTF8
}
function Test-Elevated {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
# Runs a program and waits at most $seconds for it; one that is still running then is ended with its children.
function Run-Limited([string]$file, [string[]]$arguments, [int]$seconds, [string]$out) {
  $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
  $null = $process.Handle
  $ended = $process.WaitForExit($seconds * 1000)
  if (-not $ended) { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null; [void]$process.WaitForExit(10000) }
  $text = @((Get-Content -LiteralPath $out -ErrorAction SilentlyContinue), (Get-Content -LiteralPath "$out.err" -ErrorAction SilentlyContinue)) | ForEach-Object { $_ } | Where-Object { $_ }
  [pscustomobject]@{ Ended = $ended; Code = $(if ($ended) { $process.ExitCode } else { -1 }); Text = ($text -join ' | ') }
}
# What `sc.exe` prints, as lines without blanks. Not named Sc: `sc` is PowerShell's alias of Set-Content, and an alias
# wins over a function, so a call of Sc waited for input in the hidden window (the dry run of 2026-10-02 hung there).
function Invoke-Sc([string[]]$arguments) { @(& sc.exe @arguments 2>&1 | ForEach-Object { "$_".TrimEnd() } | Where-Object { $_ }) }
function Service-State {
  $line = Invoke-Sc @('query', $service) | Where-Object { $_ -match 'STATE' } | Select-Object -First 1
  if ($line) { ($line -replace '^\s*STATE\s*:\s*\d+\s+', '').Trim() } else { 'not installed' }
}
function Describe-Service {
  foreach ($line in (Invoke-Sc @('qc', $service))) { Note "sc qc: $($line.Trim())" }
  foreach ($line in (Invoke-Sc @('query', $service) | Where-Object { $_ -match 'STATE|WIN32_EXIT' })) { Note "sc query: $($line.Trim())" }
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$service"
  if (Test-Path -LiteralPath $key) {
    $entry = Get-ItemProperty -LiteralPath $key
    Note "registry: Start=$($entry.Start) DelayedAutostart=$($entry.DelayedAutostart) ObjectName=$($entry.ObjectName) ImagePath=$($entry.ImagePath)"
  }
}

if (-not (Test-Path -LiteralPath $progressFile)) { "indexer check $tag on $($env:COMPUTERNAME)" | Set-Content -LiteralPath $progressFile }
Save-Result 'running'
$elevated = Test-Elevated
Note "elevated: $elevated ($(& whoami.exe))"
if ($Phase -ne 'check' -and -not $elevated) {
  Fail "-Phase $Phase needs an elevated process, and this one is not (UAC applies to the user's token). Start Windows PowerShell with ""Run as administrator"" in the VM and run this script again (docs/release.md, ""The indexer service"")"
  Save-Result 'needs-elevation'
  exit 3
}

if ($Phase -eq 'install') {
  # An earlier run's service and folder go first.
  if ((Service-State) -ne 'not installed') {
    if (Test-Path -LiteralPath $exe) { Run-Limited $exe @('--uninstall') 90 (Join-Path $env:TEMP 'indexer-uninstall-before.txt') | Out-Null }
    Note "an earlier $service service was removed first: $((Service-State) -eq 'not installed')"
  }
  if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }

  Progress "copying $release to $folder"
  & robocopy $release $folder /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
  if ($LASTEXITCODE -ge 8 -or -not (Test-Path -LiteralPath $exe)) { Fail "the release folder did not reach $folder (robocopy $LASTEXITCODE)"; Save-Result 'stopped'; exit 1 }
  Note "the release is in ${folder}: $((Get-Content -Raw -LiteralPath (Join-Path $folder 'release.json') | ConvertFrom-Json).version), $(@(Get-ChildItem -LiteralPath $folder -Recurse -File).Count) files"

  $watch = [Diagnostics.Stopwatch]::StartNew()
  $run = Run-Limited $exe @('--install') 120 (Join-Path $env:TEMP 'indexer-install.txt')
  Note "cabinetos-indexer --install: exit code $($run.Code) in $([int]$watch.Elapsed.TotalSeconds) s; it said: $($run.Text)"
  if ($run.Code -ne 0) { Fail 'the install did not succeed' }
  Describe-Service
  $state = Service-State
  Note "the service's state right after the install: $state"
  if ($state -ne 'RUNNING') { Fail "the install left the service $state, not RUNNING" }
  if ((Invoke-Sc @('qc', $service)) -join ' ' -notmatch 'AUTO_START\s+\(DELAYED\)') { Fail 'sc qc does not say AUTO_START (DELAYED)' }
  Save-Result $(if ($ok) { 'done' } else { 'stopped' })
  exit $(if ($ok) { 0 } else { 1 })
}

if ($Phase -eq 'uninstall') {
  $run = Run-Limited $exe @('--uninstall') 120 (Join-Path $env:TEMP 'indexer-uninstall.txt')
  Note "cabinetos-indexer --uninstall: exit code $($run.Code); it said: $($run.Text)"
  $gone = (Service-State) -eq 'not installed'
  Note "the service is gone: $gone"
  if (-not $gone) { Fail '--uninstall left the service registered' }
  Start-Sleep -Seconds 2
  Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
  Note "the folder $folder is gone: $(-not (Test-Path -LiteralPath $folder))"
  Save-Result $(if ($ok) { 'done' } else { 'stopped' })
  exit $(if ($ok) { 0 } else { 1 })
}

# ----- Phase check: after the restart, read-only -----
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Note "this boot began at $($boot.ToString('HH:mm:ss')); the check phase started at $((Get-Date).ToString('HH:mm:ss')) ($([int]((Get-Date) - $boot).TotalSeconds) s after it)"
Describe-Service
$deadline = (Get-Date).AddMinutes([int]$a.serviceMinutes)
$seen = ''
$firstRunning = $null
do {
  $state = Service-State
  if ($state -ne $seen) { Note "service state at $((Get-Date).ToString('HH:mm:ss')): $state"; $seen = $state }
  if ($state -eq 'RUNNING') { $firstRunning = Get-Date; break }
  Start-Sleep -Seconds 10
} while ((Get-Date) -lt $deadline)
if ($firstRunning) {
  Note "the service was running $([int]($firstRunning - $boot).TotalSeconds) s after the boot began, and nobody had started it"
  $process = Get-CimInstance Win32_Process -Filter "Name='cabinetos-indexer.exe'" | Select-Object -First 1
  if ($process) {
    $afterBoot = [int]($process.CreationDate - $boot).TotalSeconds
    Note "its process $($process.ProcessId) started at $($process.CreationDate.ToString('HH:mm:ss')), $afterBoot s after the boot began"
    if ($process.CreationDate -lt $boot) { Fail 'the process is older than this boot' }
  } else { Fail 'the service is RUNNING but no cabinetos-indexer process was found' }
  $pipes = @([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*cabinetos-indexer*' })
  Note "its pipe: $(if ($pipes.Count -gt 0) { $pipes -join ', ' } else { 'none' })"
  if ($pipes.Count -eq 0) { Fail 'the service runs but its pipe is not there' }
  $log = Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'CabinetOS\logs') -Filter 'indexer.*.jsonl' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if ($log) {
    Note "its log $($log.FullName), the last lines:"
    foreach ($line in (Get-Content -LiteralPath $log.FullName -Tail 4 -ErrorAction SilentlyContinue)) { Note "    $line" }
  }
} else {
  Fail "the service was not RUNNING $([int]$a.serviceMinutes) minutes after the check began (last state: $seen)"
}
Describe-Service
Note 'the service stays installed: remove it with -Phase uninstall in an elevated PowerShell in the VM'
Save-Result $(if ($ok) { 'done' } else { 'stopped' })
exit $(if ($ok) { 0 } else { 1 })
