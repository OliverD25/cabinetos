# The laptop's side of ui\livecheck\remote-script.ps1 (CabinetOS): run by the scheduled task CabinetOS-Script in the
# logged-in session, so a script that opens the window or presses real keys has a desktop. The repository keeps this
# file in ui\livecheck\laptop\; remote-script.ps1 copies it to C:\Dev\cabinetos\_io\run-script-laptop.ps1 (under the
# laptop lock) when the copy there differs. It never edits the clone: the PC-side script has checked out the exact
# commit already.
#
# It reads the run's id from the lock file (_io\laptop.lock; the lock's holder is the one run that may be going), then
# the request _io\inbox\script-request-<id>.txt (the script's path in the repository, its arguments, the commit that
# was sent), checks that the clone's HEAD is that commit (when it is not, nothing runs and the exit code is 4), runs the
# script with Windows PowerShell from the repository's folder, and writes the output _io\script-runs\script-<id>.txt and,
# last and in one step, DONE-script-<id>.md with the lines "ran commit <hash>" and "exit code <n>" that the PC-side
# script reads. The per-user .NET runtime needs the variables below; CABINETOS_UI_FRAMESTATS=1 is what the scroll
# measurements need, and it costs nothing else.
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:Path = "$env:DOTNET_ROOT;$env:Path"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:CABINETOS_UI_FRAMESTATS = '1'
$io = 'C:\Dev\cabinetos\_io'
function Read-Pairs([string]$path) {
  $h = @{}
  if (Test-Path -LiteralPath $path) { foreach ($line in (Get-Content -LiteralPath $path)) { $k, $v = $line -split '=', 2; if ($k) { $h[$k] = $v } } }
  $h
}
function Write-Whole([string]$path, [string[]]$lines) { Set-Content -LiteralPath "$path.tmp" -Value $lines; Move-Item -LiteralPath "$path.tmp" -Destination $path -Force }
New-Item -ItemType Directory -Force "$io\script-runs" | Out-Null
$id = (Read-Pairs "$io\laptop.lock")['id']
if (-not $id) {
  Write-Whole "$io\script-runs\DONE-script-nolock.md" @("# Script not run on $(hostname)", '', 'There is no laptop lock, so there is no run id: start runs with remote-script.ps1.', 'exit code 5')
  exit 5
}
$request = Read-Pairs "$io\inbox\script-request-$id.txt"
$repo = if ($request['repo']) { $request['repo'] } else { 'C:\Dev\cabinetos\cabinetos' }
$out = "$io\script-runs\script-$id.txt"
$done = "$io\script-runs\DONE-script-$id.md"
$started = Get-Date
$head = "$(git -C $repo rev-parse HEAD)".Trim()
"ran commit $head" | Set-Content -LiteralPath $out
"request: id=$id script=$($request['script']) args=[$($request['args'])] env=[$($request['env'])] repo=$repo commit=$($request['commit']) on $(hostname)" | Add-Content -LiteralPath $out
if ($request['id'] -ne $id -or ($request['commit'] -and $request['commit'] -ne $head)) {
  "NOT RUN: the request is not this run's or the clone is not at the commit that was sent (HEAD $head)" | Add-Content -LiteralPath $out
  Write-Whole $done @("# Script not run on $(hostname)", '', "The request id is [$($request['id'])] for the lock's [$id], and the clone is at $head, not at [$($request['commit'])].", "ran commit $head", 'exit code 4')
  exit 4
}
$script = Join-Path $repo $request['script']
# Environment variables the request asks for (NAME=VALUE pairs joined by ';'), for the script and the window.
foreach ($pair in ($request['env'] -split ';')) { if ($pair -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') } }
Push-Location $repo
# MinimizeAll first: a window left in front (a terminal, a Notepad) would stop a real-key script at once.
(New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep -Milliseconds 800
# "powershell -Command" ends with 1 for any script that ended with another code ("exit 3"), so the command passes the
# script's own code on: its last native command's, or 1 when the script's last statement failed.
$command = "& '$script' $($request['args']); `$ok = `$?; `$c = `$LASTEXITCODE; if (`$c -is [int] -and `$c -ne 0) { exit `$c }; if (-not `$ok) { exit 1 }; exit 0"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $command 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $out
$code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }
Pop-Location
$ended = Get-Date
Write-Whole $done @(
  "# Script finished on $(hostname)",
  '',
  "$($request['script']) $($request['args'])",
  "Started $($started.ToString('HH:mm:ss')), ended $($ended.ToString('HH:mm:ss')); exit $code.",
  '',
  "Full output: $out",
  "ran commit $head",
  "exit code $code"
)
exit $code
