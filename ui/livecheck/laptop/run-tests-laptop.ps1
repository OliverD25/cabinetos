# The laptop's side of ui\livecheck\remote-tests.ps1 (CabinetOS): run by the scheduled task CabinetOS-Tests in the
# logged-in session, so the end-to-end tests' windows have a desktop. The repository keeps this file in
# ui\livecheck\laptop\; remote-tests.ps1 copies it to C:\Dev\cabinetos\_io\run-tests-laptop.ps1 (under the laptop
# lock) when the copy there differs. It never edits the clone: the PC-side script has checked out the exact commit
# already.
#
# It reads the run's id from the lock file (_io\laptop.lock; the lock's holder is the one run that may be going), then
# the request _io\inbox\tests-request-<id>.txt (the filter, whether the end-to-end tests run, the commit that was sent),
# checks that the clone's HEAD is that commit (when it is not, nothing runs and the exit code is 4), builds the window
# with warnings as errors, runs the tests, and writes the output _io\test-runs\tests-<id>.txt and, last and in one step,
# DONE-tests-<id>.md with the lines "ran commit <hash>" and "exit code <n>" that the PC-side script reads. The exit
# code is the build's when the build failed, else the test run's. The per-user .NET runtime and SDK need the
# variables below.
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:Path = "$env:DOTNET_ROOT;$env:Path"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$io = 'C:\Dev\cabinetos\_io'
function Read-Pairs([string]$path) {
  $h = @{}
  if (Test-Path -LiteralPath $path) { foreach ($line in (Get-Content -LiteralPath $path)) { $k, $v = $line -split '=', 2; if ($k) { $h[$k] = $v } } }
  $h
}
function Write-Whole([string]$path, [string[]]$lines) { Set-Content -LiteralPath "$path.tmp" -Value $lines; Move-Item -LiteralPath "$path.tmp" -Destination $path -Force }
New-Item -ItemType Directory -Force "$io\test-runs" | Out-Null
$id = (Read-Pairs "$io\laptop.lock")['id']
if (-not $id) {
  Write-Whole "$io\test-runs\DONE-tests-nolock.md" @("# Tests not run on $(hostname)", '', 'There is no laptop lock, so there is no run id: start runs with remote-tests.ps1.', 'exit code 5')
  exit 5
}
$request = Read-Pairs "$io\inbox\tests-request-$id.txt"
$repo = if ($request['repo']) { $request['repo'] } else { 'C:\Dev\cabinetos\cabinetos' }
$out = "$io\test-runs\tests-$id.txt"
$done = "$io\test-runs\DONE-tests-$id.md"
$started = Get-Date
$head = "$(git -C $repo rev-parse HEAD)".Trim()
"ran commit $head" | Set-Content -LiteralPath $out
"request: id=$id filter=[$($request['filter'])] e2e=$($request['e2e']) repo=$repo commit=$($request['commit'])" | Add-Content -LiteralPath $out
if ($request['id'] -ne $id -or ($request['commit'] -and $request['commit'] -ne $head)) {
  "NOT RUN: the request is not this run's or the clone is not at the commit that was sent (HEAD $head)" | Add-Content -LiteralPath $out
  Write-Whole $done @("# Tests not run on $(hostname)", '', "The request id is [$($request['id'])] for the lock's [$id], and the clone is at $head, not at [$($request['commit'])].", "ran commit $head", 'exit code 4')
  exit 4
}
Push-Location "$repo\ui"
"== build at $(Get-Date -Format HH:mm:ss)" | Add-Content -LiteralPath $out
& dotnet build CabinetOS.sln -warnaserror 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $out
$buildCode = $LASTEXITCODE
if ($buildCode -eq 0) {
  if ($request['e2e'] -eq 'yes') { $env:CABINETOS_UI_E2E = '1'; $env:CABINETOS_CORE_EXE = "$repo\core\target\release\cabinetos-core.exe" }
  "== tests at $(Get-Date -Format HH:mm:ss) (e2e: $($request['e2e']))" | Add-Content -LiteralPath $out
  $testArgs = @('test', '--solution', 'CabinetOS.sln', '--no-build')
  if ($request['filter']) { $testArgs += @('--filter', $request['filter']) }
  & dotnet @testArgs 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $out
  $testCode = $LASTEXITCODE
} else { $testCode = -1 }
Pop-Location
$ended = Get-Date
$code = if ($buildCode -ne 0) { $buildCode } else { $testCode }
if ($null -eq $code) { $code = 1 }
$lines = Get-Content -LiteralPath $out
$summary = @($lines | Where-Object { $_ -match '^\s*(total|failed|succeeded|skipped|duration):' })
$failedTests = @($lines | Where-Object { $_ -match '^\s*failed ' })
Write-Whole $done (@(
  "# Tests finished on $(hostname)",
  '',
  "Started $($started.ToString('HH:mm:ss')), ended $($ended.ToString('HH:mm:ss')); build exit $buildCode, test exit $testCode.",
  ''
) + @($summary | ForEach-Object { "- $_" }) + @($failedTests | ForEach-Object { "- $_" }) + @('', "Full output: $out", "ran commit $head", "exit code $code"))
exit $code
