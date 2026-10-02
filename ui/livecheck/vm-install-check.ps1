# The install check (docs/ui.md, "The live check in a virtual machine"; docs/release.md, "The setup file"): proves
# the whole chain of a release in the VirtualBox VM "CabinetOS-LiveCheck" with nobody at either keyboard, and writes
# DONE-install.md into the output folder (_io\live-check unless -Io says otherwise) with each step's result and the
# screenshots the VM took of its own screen.
#
#  1. On this PC: the next patch version is built first as a real release (build\release.ps1 -NoSetup) with the
#     version files raised for the build and put back byte for byte after it, because only a real build reports its
#     version in pong and the updater's state; then this version's release with its setup file (build\release.ps1).
#     The next version's zip is staged as an update feed in _io\update-test\stable (latest.json for a file: source,
#     its notes, the zip), and the setup file is copied to _io\install-check.
#  2-5. In the VM, through guest control, vm-install-guest.ps1 runs in the VM user's desktop session: the setup runs
#     silently with its log in _io\vm, the live check runs against the installed CabinetOS.exe (-Exe, -Virtual), the
#     installed CabinetOS finds the staged version, installs it by itself and says so in its status bar, the notice's
#     Restart now runs the new version, and Inno's uninstaller removes the install. See vm-install-guest.ps1.
#
# The VM's user and password are in _io\vm\vm-user.txt, which this script reads and never prints; the password goes
# to VBoxManage through a temporary file. Only short commands go to the VM: a guest command over about 1,200
# characters hangs guest control (seen 2026-10-01), so the guest's script and its arguments travel through X:. Nothing
# probes the VM while the guest script runs: this script reads only what it writes to X:. -Restart restarts the VM
# first, the one cure seen for a stuck guest control or WebView2 processes a killed window left behind.
#
# Runs in Windows PowerShell 5.1 and PowerShell 7; the builds need PowerShell 7 (pwsh) and everything
# docs\release.md's "Build" needs, Inno Setup 6.7 included. The builds take about 15 minutes, the VM's part about 30
# with the live check (-SkipLiveCheck leaves it out; -LiveCheckMinutes, 30 by default, stops one that hangs, and step 3
# fails then while steps 4 and 5 still run). A VM this script starts or restarts first gets up to -SettleMinutes (30)
# to finish what Windows does after a start: right after a restart on 2026-10-02 an uninstall took 22 minutes and a
# window 11, against seconds on a VM that had settled.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\vm-install-check.ps1
#   vm-install-check.ps1 -SkipBuild        # the release, the setup and the next version from an earlier run
#   vm-install-check.ps1 -Restart -SkipLiveCheck
param(
  [string]$VM = 'CabinetOS-LiveCheck',
  [string]$User = 'cabinetos',
  [string]$Io = '',
  [int]$WaitMinutes = 90,
  [int]$LiveCheckMinutes = 30,
  [int]$SettleMinutes = 30,
  [switch]$SkipBuild,
  [switch]$SkipLiveCheck,
  [switch]$SyncVersion,
  [switch]$Restart
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# The project's root, the VM's shared folder X:: the first folder above the repository whose _io holds the VM's
# user file, also for a git worktree under .claude\worktrees (where other scripts have made an _io of their own).
$root = Split-Path $repo -Parent
while ($root -and -not (Test-Path -LiteralPath (Join-Path $root '_io\vm\vm-user.txt'))) { $root = Split-Path $root -Parent }
if (-not $root) { throw "no folder above $repo holds _io\vm\vm-user.txt, the VM user's file in the project's exchange folder (the VM's X:)" }
$io = if ($Io) { $Io } else { Join-Path $root '_io\live-check' }
$vmIo = Join-Path $root '_io\vm'
$exchange = Join-Path $root '_io\install-check'
$feed = Join-Path $root '_io\update-test'
New-Item -ItemType Directory -Force $io, $vmIo, $exchange, (Join-Path $feed 'stable\files') | Out-Null
$tag = Get-Date -Format 'yyyy-MM-dd-HHmm'
$vbm = Join-Path ${env:ProgramFiles} 'Oracle\VirtualBox\VBoxManage.exe'
if (-not (Test-Path -LiteralPath $vbm)) { throw "VirtualBox is not installed here ($vbm)" }
$userFile = Join-Path $vmIo 'vm-user.txt'
if (-not (Test-Path -LiteralPath $userFile)) { throw "$userFile is missing: it holds the VM user's password" }
$utf8 = New-Object System.Text.UTF8Encoding $false
$started = Get-Date
$buildLines = New-Object System.Collections.ArrayList

# The shared folder X: is the project's root, so a path under it on this PC is the same path under X: in the VM.
function GuestPath([string]$local) {
  if (-not $local.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "$local is outside the shared folder $root" }
  'X:' + $local.Substring($root.Length)
}

# ----- 1. The two releases, on this PC -----
$cargoToml = Join-Path $repo 'core\Cargo.toml'
$cargoLock = Join-Path $repo 'core\Cargo.lock'
$props = Join-Path $repo 'ui\Directory.Build.props'
$section = [regex]::Match([IO.File]::ReadAllText($cargoToml), '(?ms)^\[workspace\.package\][^\n]*\n(.*?)(?=^\[)')
$version = [regex]::Match($section.Groups[1].Value, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(-.*)?$') { throw "core\Cargo.toml's version '$version' is not x.y.z" }
$next = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
$dist = Join-Path $repo 'dist'
$setup = Join-Path $dist "CabinetOS-$version-win-x64-setup.exe"
$nextZip = Join-Path $dist "CabinetOS-$next-win-x64.zip"
$nextNotes = Join-Path $dist "update\stable\notes-$next.md"
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { throw 'PowerShell 7 (pwsh) is needed for build\release.ps1' }

# A native program's lines on stderr (cargo's progress, git's warnings) are errors to Windows PowerShell 5.1 under
# ErrorActionPreference Stop; around a native call the preference is Continue, and the exit code decides.
function Native([scriptblock]$call) {
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & $call } finally { $ErrorActionPreference = $saved }
}

# The version a release folder's programs report (cabinetos-cli --version), against the one its name says.
function Test-BuiltVersion([string]$expected) {
  $cli = Join-Path $dist "CabinetOS-$expected-win-x64\cabinetos-cli.exe"
  $said = if (Test-Path -LiteralPath $cli) { (Native { & $cli --version 2>&1 }) -join ' ' } else { 'no cabinetos-cli.exe' }
  if ($said -notmatch "\b$([regex]::Escape($expected))$") { throw "the release folder of $expected holds programs of another version ($said); build without -SkipBuild" }
}

# cargo keeps each version's build apart, but it copies a program that is up to date for this version back into
# target\release only when it rebuilds it: after the next version's build, this version's release took the next
# version's programs (2026-10-02, the installed 0.1.0 said 0.1.1). A newer time on the three programs' main.rs
# makes cargo build them again, and Test-BuiltVersion checks the result.
function Invoke-Release([string[]]$switches, [string]$what, [string]$expected) {
  $log = Join-Path $io "vm-install-$tag-build-$what.txt"
  $watch = [Diagnostics.Stopwatch]::StartNew()
  foreach ($program in 'cabinetos-cli', 'cabinetos-core', 'cabinetos-indexer') {
    (Get-Item -LiteralPath (Join-Path $repo "core\crates\$program\src\main.rs")).LastWriteTime = Get-Date
  }
  $arguments = @('-NoProfile', '-File', (Join-Path $repo 'build\release.ps1')) + @($switches)
  $process = Start-Process -FilePath $pwsh -ArgumentList $arguments -RedirectStandardOutput $log -RedirectStandardError "$log.err" -NoNewWindow -Wait -PassThru
  $code = $process.ExitCode
  [void]$buildLines.Add("build\release.ps1 $($switches -join ' ') ($what): exit code $code in $([int]$watch.Elapsed.TotalMinutes) min; output $log")
  if ($code -ne 0) { throw "build\release.ps1 $($switches -join ' ') failed with exit code $code; see $log and $log.err" }
  Test-BuiltVersion $expected
}

if ($SkipBuild) {
  [void]$buildLines.Add("skipped (-SkipBuild): $setup and $nextZip from an earlier build")
} else {
  # The next version: the three files that carry the version are raised for this build only, then written back byte
  # for byte. They must be as committed first, so nothing of anyone's is ever written over.
  $dirty = @(Native { & git -C $repo status --porcelain -- core/Cargo.toml core/Cargo.lock ui/Directory.Build.props 2>$null })
  if ($dirty.Count -gt 0) { throw "core\Cargo.toml, core\Cargo.lock or ui\Directory.Build.props have uncommitted changes; commit them first: $($dirty -join '; ')" }
  $metadata = (Native { & cargo metadata --no-deps --format-version 1 --offline --manifest-path $cargoToml 2>$null }) -join ''
  $members = @(($metadata | ConvertFrom-Json).packages | Where-Object { $_.version -eq $version } | ForEach-Object { $_.name })
  if ($members.Count -eq 0) { throw "cargo metadata named no workspace package of version $version" }
  $saved = @{}
  foreach ($file in $cargoToml, $cargoLock, $props) { $saved[$file] = [IO.File]::ReadAllBytes($file) }
  try {
    $toml = [IO.File]::ReadAllText($cargoToml)
    $toml = $toml.Substring(0, $section.Index) + [regex]::Replace($section.Value, '(?m)^(version\s*=\s*)"[^"]+"', "`${1}`"$next`"", 1) + $toml.Substring($section.Index + $section.Length)
    [IO.File]::WriteAllText($cargoToml, $toml, $utf8)
    $lock = [IO.File]::ReadAllText($cargoLock)
    foreach ($member in $members) {
      $lock = $lock.Replace("name = `"$member`"`nversion = `"$version`"`n", "name = `"$member`"`nversion = `"$next`"`n")
    }
    [IO.File]::WriteAllText($cargoLock, $lock, $utf8)
    $propsText = [IO.File]::ReadAllText($props)
    [IO.File]::WriteAllText($props, $propsText.Replace("<Version>$version</Version>", "<Version>$next</Version>"), $utf8)
    Invoke-Release @('-NoSetup') "next-$next" $next
  } finally {
    foreach ($file in $saved.Keys) { [IO.File]::WriteAllBytes($file, $saved[$file]) }
  }
  foreach ($file in $saved.Keys) {
    $now = [IO.File]::ReadAllBytes($file)
    if ([Convert]::ToBase64String($now) -ne [Convert]::ToBase64String($saved[$file])) { throw "$file was not put back as it was" }
  }
  [void]$buildLines.Add("the version files are back as committed: $(@(Native { & git -C $repo status --porcelain -- core/Cargo.toml core/Cargo.lock ui/Directory.Build.props 2>$null }).Count -eq 0)")
  $staged = Join-Path $exchange "notes-$next.md"
  Copy-Item -LiteralPath $nextNotes -Destination $staged -Force
  $switches = @()
  if ($SyncVersion) { $switches += '-SyncVersion' }
  Invoke-Release $switches "this-$version" $version
}
foreach ($needed in $setup, $nextZip) { if (-not (Test-Path -LiteralPath $needed)) { throw "$needed is missing: build without -SkipBuild" } }
Test-BuiltVersion $version
Test-BuiltVersion $next

# The feed: latest.json as build\release.ps1 writes it, with the zip next to it, for a file: source (ADR 0014).
$feedZip = Join-Path $feed "stable\files\CabinetOS-$next-win-x64.zip"
Copy-Item -LiteralPath $nextZip -Destination $feedZip -Force
$notesFile = Join-Path $exchange "notes-$next.md"
if (Test-Path -LiteralPath $notesFile) { Copy-Item -LiteralPath $notesFile -Destination (Join-Path $feed "stable\notes-$next.md") -Force }
else { [IO.File]::WriteAllText((Join-Path $feed "stable\notes-$next.md"), "### Changed`n`n- The install check's next version.`n", $utf8) }
$requires = (Get-Content -Raw -LiteralPath (Join-Path $dist "CabinetOS-$next-win-x64\release.json") | ConvertFrom-Json).requires
$runtime = [version] ($requires.windowsAppRuntime.version)
$dotnet = [version] (($requires.dotnet | Select-Object -First 1).version -split '-')[0]
$latest = [ordered]@{
  schemaVersion = 1
  channel       = 'stable'
  version       = $next
  published     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
  zip           = [ordered]@{ url = "files/CabinetOS-$next-win-x64.zip"; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $feedZip).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $feedZip).Length }
  notes         = [ordered]@{ url = "notes-$next.md" }
  requires      = [ordered]@{ windowsAppRuntime = "$($runtime.Major).$($runtime.Minor)"; dotnet = "$($dotnet.Major).$($dotnet.Minor)" }
}
[IO.File]::WriteAllText((Join-Path $feed 'stable\latest.json'), ($latest | ConvertTo-Json -Depth 4), $utf8)
$guestSetup = Join-Path $exchange (Split-Path $setup -Leaf)
Copy-Item -LiteralPath $setup -Destination $guestSetup -Force
[void]$buildLines.Add("staged: $(GuestPath $feed)\stable (latest.json names $next, its zip $([math]::Round((Get-Item -LiteralPath $feedZip).Length / 1MB, 1)) MB); the setup file $(GuestPath $guestSetup)")
$buildLines | ForEach-Object { "step 1: $_" }

# The guest's script and its arguments travel through X:, so the command that starts it stays short.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vm-install-guest.ps1') -Destination (Join-Path $exchange 'vm-install-guest.ps1') -Force
$guestArgs = [ordered]@{
  tag           = $tag
  version       = $version
  next          = $next
  setup         = GuestPath $guestSetup
  feedUrl       = 'file:///' + ((GuestPath $feed) -replace '\\', '/')
  source        = GuestPath $repo
  io            = GuestPath $io
  vmIo          = GuestPath $vmIo
  skipLiveCheck = [bool]$SkipLiveCheck
  liveCheckMinutes = $LiveCheckMinutes
}
[IO.File]::WriteAllText((Join-Path $exchange 'install-args.json'), ($guestArgs | ConvertTo-Json), $utf8)

# ----- The VM, running, with its desktop up (as vm-livecheck.ps1 starts it) -----
function State { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'VMState=*' }) -replace 'VMState=|"', '' }
function RunLevel { (& $vbm showvminfo $VM --machinereadable | Where-Object { $_ -like 'GuestAdditionsRunLevel=*' }) -replace 'GuestAdditionsRunLevel=', '' }
if ($Restart -and (State) -eq 'running') {
  "restarting the VM"
  try { & $vbm controlvm $VM acpipowerbutton 2>&1 | Out-Null } catch { }
  $deadline = (Get-Date).AddSeconds(90)
  while ((State) -ne 'poweroff' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
  if ((State) -ne 'poweroff') { try { & $vbm controlvm $VM poweroff 2>&1 | Out-Null } catch { }; Start-Sleep -Seconds 10 }
  Get-CimInstance Win32_Process -Filter "Name='VBoxHeadless.exe'" | Where-Object { $_.CommandLine -like "*$VM*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 5
}
$state = State
if ($state -ne 'running') {
  if ($state -eq 'saved' -or $state -eq 'poweroff' -or $state -eq 'aborted') { "the VM is $state, starting it"; & $vbm startvm $VM --type headless | Out-Null }
  elseif ($state -eq 'paused') { & $vbm controlvm $VM resume | Out-Null }
  else { throw "the VM is $state" }
}
$deadline = (Get-Date).AddMinutes(3)
while ((RunLevel) -ne '3' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if ((RunLevel) -ne '3') { throw "the VM's desktop did not come up in 3 minutes (run level $(RunLevel))" }
if ($state -ne 'running') { Start-Sleep -Seconds 20 }
& $vbm controlvm $VM setvideomodehint 1920 1080 32 | Out-Null
"VM ${VM}: running, desktop up"

$pwFile = Join-Path $env:TEMP 'cabinetos-vm-password.txt'
function GuestRun([string]$command, [int]$timeoutMs) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
  $lines = @()
  try {
    $lines = @(& $vbm guestcontrol $VM run --username $User --passwordfile $pwFile --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --wait-stdout --timeout $timeoutMs -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1 | ForEach-Object { "$_" })
  } catch { $lines = @("guest control failed: $($_.Exception.Message)") }
  $lines
}
$password = (Get-Content -LiteralPath $userFile | Where-Object { $_ -like 'password: *' } | Select-Object -First 1) -replace '^password: ', ''
if (-not $password) { throw "$userFile has no 'password: ' line" }
[IO.File]::WriteAllText($pwFile, $password)
$resultFile = Join-Path $io "vm-install-result-$tag.json"
try {
  $deadline = (Get-Date).AddMinutes(3)
  do {
    $seen = GuestRun "Test-Path 'X:\_io\install-check\install-args.json'" 30000
    if ($seen -contains 'True') { break }
    Start-Sleep -Seconds 10
  } while ((Get-Date) -lt $deadline)
  if (-not ($seen -contains 'True')) { throw "the VM does not see the shared folder X: (guest control answered '$($seen -join ' ')')" }
  "the VM sees X:"
  if ($state -ne 'running' -and $SettleMinutes -gt 0) {
    $settling = Get-Date
    $deadline = $settling.AddMinutes($SettleMinutes)
    $calm = 0
    $load = ''
    do {
      $answer = GuestRun "[int]((Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 5 -MaxSamples 3).CounterSamples | Measure-Object CookedValue -Average).Average" 60000
      $load = $answer | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1
      if ($load -and [int]$load -lt 20) { $calm++ } else { $calm = 0 }
      if ($calm -ge 2) { break }
      Start-Sleep -Seconds 20
    } while ((Get-Date) -lt $deadline)
    "the VM settled after its start: $($calm -ge 2) after $([int]((Get-Date) - $settling).TotalMinutes) min (the last CPU load: $load %)"
  }
  # Hidden: a console of the guest's script in front of the window would stop the live check of step 3.
  $answer = GuestRun "Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','X:\_io\install-check\vm-install-guest.ps1'; 'started'" 60000
  if (-not ($answer -contains 'started')) { throw "the VM did not start the install check: guest control answered '$($answer -join ' ')'" }
  "the install check runs in the VM; its progress: $(Join-Path $io 'vm-install-progress.txt')"

  # The wait: only the files the guest writes to X: are read; no guest command until it is done.
  $deadline = (Get-Date).AddMinutes($WaitMinutes)
  $result = $null
  do {
    Start-Sleep -Seconds 20
    if (Test-Path -LiteralPath $resultFile) {
      try { $result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json } catch { $result = $null }
    }
  } while (-not ($result -and $result.state -ne 'running') -and (Get-Date) -lt $deadline)
  if (-not $result) { $result = [pscustomobject]@{ state = 'no result'; steps = @(); screenshots = @() } }
  if ($result.state -eq 'running') { $result.state = "still running after $WaitMinutes minutes" }
  GuestRun "Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force" 30000 | Out-Null
} finally {
  Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
}

# ----- DONE-install.md -----
$progressFile = Join-Path $io 'vm-install-progress.txt'
$names = @{ 2 = 'the setup runs silently and installs'; 3 = 'the live check runs against the installed CabinetOS.exe'; 4 = 'the next version installs itself and the restart runs it'; 5 = 'the uninstaller removes the install' }
$done = @(
  "# Install check finished ($tag)",
  "",
  "Started $($started.ToString('HH:mm:ss')), ended $((Get-Date).ToString('HH:mm:ss')) on this PC; the VM's run: $($result.state). $version from the setup, $next as the update.",
  "",
  "## Step 1: the releases, on this PC: passed",
  ""
) + @($buildLines | ForEach-Object { "- $_" })
foreach ($number in 2, 3, 4, 5) {
  $step = @($result.steps | Where-Object { $_.step -eq $number }) | Select-Object -First 1
  $done += ''
  if ($step) {
    $done += "## Step ${number}: $($step.name): $(if ($step.ok) { 'passed' } else { 'FAILED' })"
    $done += ''
    $done += @($step.details | ForEach-Object { "- $_" })
  } else {
    $done += "## Step ${number}: $($names[$number]): not reached"
  }
}
$done += @('', '## Screenshots, taken inside the VM', '') + @($result.screenshots | ForEach-Object { "- $_" })
if (Test-Path -LiteralPath $progressFile) { $done += @('', '## The VM''s progress', '') + @(Get-Content -LiteralPath $progressFile | ForEach-Object { "    $_" }) }
$doneFile = Join-Path $io 'DONE-install.md'
[IO.File]::WriteAllText($doneFile, ($done -join "`r`n") + "`r`n", $utf8)
Copy-Item -LiteralPath $doneFile -Destination (Join-Path $io "DONE-install-$tag.md") -Force
$done
$failed = @($result.steps | Where-Object { -not $_.ok }).Count
if ($result.state -ne 'done' -or $failed -gt 0) { exit 1 }
