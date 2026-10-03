# The laptop's side of ui\livecheck\remote-livecheck.ps1 (CabinetOS): run by the scheduled task CabinetOS-LiveCheck in the
# logged-in session. The repository keeps this file in ui\livecheck\laptop\; remote-livecheck.ps1 copies it to
# C:\Dev\cabinetos\_io\run-livecheck-laptop.ps1 (under the laptop lock) when the copy there differs. It never edits the
# clone: the PC-side script has checked out the exact commit already.
#
# It reads the run's id from the lock file (_io\laptop.lock; the lock's holder is the one run that may be going), then
# the request _io\inbox\livecheck-request-<id>.txt (the commit that was sent), checks that the clone's HEAD is that
# commit (when it is not, nothing runs and the exit code is 4), and starts run-livecheck.ps1 of the clone with the
# laptop's switches (-MinimizeOthers, -Panel) and the id as its tag. run-livecheck.ps1 writes the output
# _io\live-check\run-<id>.txt and, last and in one step, DONE-<id>.md with the lines "ran commit <hash>" and
# "exit code <n>" that the PC-side script reads. The per-user .NET runtime: the task sets the variables the window
# needs to find it.
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:Path = "$env:DOTNET_ROOT;$env:Path"
$io = 'C:\Dev\cabinetos\_io'
function Read-Pairs([string]$path) {
  $h = @{}
  if (Test-Path -LiteralPath $path) { foreach ($line in (Get-Content -LiteralPath $path)) { $k, $v = $line -split '=', 2; if ($k) { $h[$k] = $v } } }
  $h
}
function Write-Whole([string]$path, [string[]]$lines) { Set-Content -LiteralPath "$path.tmp" -Value $lines; Move-Item -LiteralPath "$path.tmp" -Destination $path -Force }
New-Item -ItemType Directory -Force "$io\live-check" | Out-Null
$id = (Read-Pairs "$io\laptop.lock")['id']
if (-not $id) {
  Write-Whole "$io\live-check\DONE-nolock.md" @("# Live check not run on $(hostname)", '', 'There is no laptop lock, so there is no run id: start runs with remote-livecheck.ps1.', 'exit code 5')
  exit 5
}
$request = Read-Pairs "$io\inbox\livecheck-request-$id.txt"
$repo = if ($request['repo']) { $request['repo'] } else { 'C:\Dev\cabinetos\cabinetos' }
$head = "$(git -C $repo rev-parse HEAD)".Trim()
if ($request['id'] -ne $id -or ($request['commit'] -and $request['commit'] -ne $head)) {
  Write-Whole "$io\live-check\DONE-$id.md" @("# Live check not run on $(hostname)", '', "The request id is [$($request['id'])] for the lock's [$id], and the clone is at $head, not at [$($request['commit'])].", "ran commit $head", 'exit code 4')
  exit 4
}
& "$repo\ui\livecheck\run-livecheck.ps1" -MinimizeOthers -Panel -Tag $id -RunId $id
exit $LASTEXITCODE
