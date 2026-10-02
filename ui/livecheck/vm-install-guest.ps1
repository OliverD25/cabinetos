# The guest's side of vm-install-check.ps1 (docs/ui.md, "The live check in a virtual machine", the install check): runs
# inside the VirtualBox VM "CabinetOS-LiveCheck", started there by vm-install-check.ps1 on this PC through VirtualBox's
# guest control, in the VM user's desktop session. vm-install-check.ps1 copies this script and its arguments
# (install-args.json) into _io\install-check on this PC, which the VM sees as X:\_io\install-check, so the command that
# starts it stays short (a long guest command hangs guest control).
#
# Steps 2 to 5 of the install check, each recorded in vm-install-result-<tag>.json in the run's output folder:
#  2. the setup file runs silently (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG), and the install is checked: the
#     files in %LOCALAPPDATA%\Programs\CabinetOS, the Start Menu shortcut, the Settings > Apps entry CabinetOS_is1;
#  3. livecheck.ps1 runs against the installed CabinetOS.exe and its core (-Exe, -Core, -Virtual -Strict), from a copy
#     of the repository's tree on the VM's disk;
#  4. the installed CabinetOS starts with update.source on the staged next version (X:\_io\update-test): its daily
#     check finds it, downloads it and swaps it in by itself, the status bar's notice says so (the window's log line
#     "update notice shown"), "Restart now" is pressed through UI Automation, and the new window runs the new version
#     (cabinetos-cli update --json against its core, the state file's confirmed swap, the Apps entry);
#  5. Inno's uninstaller runs silently, and the install folder, the Apps entry and the shortcut are gone while the
#     user's data folder stays.
# Screenshots are taken inside the VM (VirtualBox's own screenshot of a headless VM is a stale frame), into the run's
# output folder. vm-install-progress.txt there says how far it got.
param([string]$ArgsFile = 'X:\_io\install-check\install-args.json')
$ErrorActionPreference = 'Continue'
$left = 36
while (-not (Test-Path -LiteralPath $ArgsFile) -and $left -gt 0) { Start-Sleep -Seconds 5; $left-- }
if (-not (Test-Path -LiteralPath $ArgsFile)) { exit 2 }
$a = Get-Content -Raw -LiteralPath $ArgsFile | ConvertFrom-Json
$tag = $a.tag
$io = $a.io
$progressFile = Join-Path $io "vm-install-progress.txt"
$resultFile = Join-Path $io "vm-install-result-$tag.json"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:Path = "$env:DOTNET_ROOT;$env:LOCALAPPDATA\Microsoft\PowerShell\7;$env:Path"
foreach ($name in 'CABINETOS_CONFIG', 'CABINETOS_LOG_DIR', 'CABINETOS_UPDATE_DIR', 'CABINETOS_CORE_EXE', 'CABINETOS_PIPE') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }

$install = Join-Path $env:LOCALAPPDATA 'Programs\CabinetOS'
$appsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CabinetOS.lnk'
$work = 'C:\cabinetos\install-check'
$steps = New-Object System.Collections.ArrayList
$shots = New-Object System.Collections.ArrayList

function Progress([string]$text) { "$(Get-Date -Format HH:mm:ss) $text" | Add-Content -LiteralPath $progressFile }
function Step([int]$number, [string]$name, [bool]$ok, [string[]]$details) {
  [void]$steps.Add([ordered]@{ step = $number; name = $name; ok = $ok; details = @($details) })
  Progress "step $number ($name): $(if ($ok) { 'passed' } else { 'FAILED' })"
}
function Save-Result([string]$state) {
  $result = [ordered]@{ tag = $tag; state = $state; machine = $env:COMPUTERNAME; ended = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); steps = @($steps); screenshots = @($shots) }
  $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultFile -Encoding UTF8
}
function Shot([string]$name) {
  try {
    Add-Type -AssemblyName System.Drawing, System.Windows.Forms
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $file = Join-Path $io "vm-install-$tag-$name.png"
    $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    [void]$shots.Add((Split-Path $file -Leaf))
  } catch { Progress "screenshot $name failed: $($_.Exception.Message)" }
}
# Every line of the files that match, read as their writer shares them (a running window keeps its log open).
function Read-Lines([string]$folder, [string]$pattern) {
  $lines = @()
  if (-not (Test-Path -LiteralPath $folder)) { return $lines }
  foreach ($file in Get-ChildItem -LiteralPath $folder -Filter $pattern | Sort-Object Name) {
    try {
      $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete')
      $reader = New-Object IO.StreamReader($stream)
      while ($null -ne ($line = $reader.ReadLine())) { $lines += $line }
      $reader.Dispose()
    } catch { }
  }
  $lines
}
# The log lines (as objects) whose message is $message.
function Log-Lines([string]$folder, [string]$pattern, [string]$message) {
  foreach ($line in Read-Lines $folder $pattern) {
    if ($line -notlike "*$message*") { continue }
    try { $parsed = $line | ConvertFrom-Json } catch { continue }
    if ($parsed.message -eq $message) { $parsed }
  }
}
function Wait-Until([scriptblock]$condition, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    if (& $condition) { return $true }
    Start-Sleep -Seconds 2
  }
  [bool](& $condition)
}
function Running-From([string]$folder) {
  @(Get-CimInstance Win32_Process -Filter "Name='CabinetOS.exe' OR Name='cabinetos-core.exe' OR Name='cabinetos-cli.exe' OR Name='cab.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($folder + '\', [StringComparison]::OrdinalIgnoreCase) })
}
function Stop-Leftovers {
  Get-Process CabinetOS, cabinetos-core, msedgewebview2, notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}
# Runs a program and waits at most $seconds for it; one that is still running then is ended with its children. In a
# VM that has just started, a call that takes seconds otherwise took minutes (2026-10-02), and a wait with no limit
# held the whole check.
function Run-Limited([string]$file, [string[]]$arguments, [int]$seconds, [string]$out = '') {
  $start = @{ FilePath = $file; ArgumentList = $arguments; PassThru = $true; NoNewWindow = $true }
  if ($out) { $start.RedirectStandardOutput = $out; $start.RedirectStandardError = "$out.err" }
  $process = Start-Process @start
  # Without the handle taken now, Windows PowerShell reports no exit code for a process started this way.
  $null = $process.Handle
  $ended = $process.WaitForExit($seconds * 1000)
  if (-not $ended) { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null; [void]$process.WaitForExit(10000) }
  [pscustomobject]@{ Ended = $ended; Code = $(if ($ended) { $process.ExitCode } else { -1 }) }
}
function Version-Of([string]$folder) {
  $file = Join-Path $folder 'release.json'
  if (Test-Path -LiteralPath $file) { (Get-Content -Raw -LiteralPath $file | ConvertFrom-Json).version } else { '' }
}

"install check $tag started $(Get-Date -Format HH:mm:ss) on $($env:COMPUTERNAME): $($a.version) from the setup, then $($a.next)" | Set-Content -LiteralPath $progressFile
Save-Result 'running'
Get-Process *OneDrive* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Leftovers

# An earlier run's install goes first, the way it came: the setup's uninstaller, or install.ps1's.
$before = @()
if (Test-Path -LiteralPath (Join-Path $install 'unins000.exe')) {
  $beforeLog = Join-Path $a.vmIo "uninstall-before-$tag.log"
  $removing = Get-Date
  $removed = Run-Limited (Join-Path $install 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$beforeLog") 600
  [void](Wait-Until { -not (Test-Path -LiteralPath $install) } 120)
  $before += "an earlier setup install was removed first in $([int]((Get-Date) - $removing).TotalSeconds) s (ended: $($removed.Ended); log $beforeLog): gone: $(-not (Test-Path -LiteralPath $install))"
}
if (Test-Path -LiteralPath (Join-Path $install '.cabinetos-install.json')) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $install 'uninstall.ps1') | Out-Null
  $before += "an earlier install.ps1 install was removed first: gone: $(-not (Test-Path -LiteralPath $install))"
}
if (Test-Path -LiteralPath $install) { $before += "the install folder is there before the setup runs: $(@(Get-ChildItem -LiteralPath $install -Force).Count) entries" }
foreach ($line in $before) { Progress $line }

# ----- 2. The setup, silently -----
$setupLog = Join-Path $a.vmIo "setup-$tag.log"
Progress "step 2: $($a.setup) /VERYSILENT"
$setup = Start-Process -FilePath $a.setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$setupLog" -Wait -PassThru
$details = @($before) + @("exit code $($setup.ExitCode); log $setupLog")
$ok = $setup.ExitCode -eq 0
foreach ($file in 'CabinetOS.exe', 'cabinetos-core.exe', 'cabinetos-cli.exe', 'cab.exe', 'CabinetOS.ico', 'CabinetOS.pri', 'unins000.exe', 'unins000.dat', 'release.json') {
  $there = Test-Path -LiteralPath (Join-Path $install $file)
  if (-not $there) { $ok = $false; $details += "missing in the install folder: $file" }
}
if (Test-Path -LiteralPath (Join-Path $install 'install.ps1')) { $ok = $false; $details += 'install.ps1 was installed, which the swap never installs' }
$installed = Version-Of $install
if ($installed -ne $a.version) { $ok = $false }
$details += "release.json in $install names $installed"
$files = @(Get-ChildItem -LiteralPath $install -Recurse -File -Force -ErrorAction SilentlyContinue)
$details += "{0} files, {1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)
if (Test-Path -LiteralPath $shortcut) { $details += "Start Menu shortcut: $shortcut" } else { $ok = $false; $details += "no Start Menu shortcut at $shortcut" }
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CabinetOS.lnk'
$details += "desktop shortcut (unchecked by default): $(Test-Path -LiteralPath $desktop)"
if (Test-Path -LiteralPath $appsKey) {
  $entry = Get-ItemProperty -LiteralPath $appsKey
  foreach ($name in 'DisplayName', 'DisplayVersion', 'Publisher', 'InstallLocation', 'DisplayIcon', 'UninstallString', 'QuietUninstallString', 'EstimatedSize', 'MajorVersion', 'MinorVersion', 'NoModify', 'NoRepair') {
    if ($entry.PSObject.Properties[$name]) { $details += "Apps entry ${name}: $($entry.$name)" }
  }
  if ($entry.DisplayVersion -ne $a.version -or $entry.DisplayName -ne 'CabinetOS') { $ok = $false }
} else { $ok = $false; $details += "no Apps entry at $appsKey" }
Step 2 'the setup runs silently and installs' $ok $details
Save-Result 'running'
if (-not $ok) { Save-Result 'stopped'; exit 1 }

# ----- 3. The live check against the installed program -----
if ($a.skipLiveCheck) {
  Step 3 'the live check runs against the installed CabinetOS.exe' $true @('skipped (-SkipLiveCheck)')
} else {
  $tree = 'C:\cabinetos\install-tree'
  # The VM user's %TEMP% is in the short 8.3 form (C:\Users\CABINE~1\...), and the core compares the Agent extension's
  # paths with its fs:read root %USERPROFILE% (C:\Users\cabinetos) as text, so section 14's preview never showed in
  # the VM (2026-10-01 and 2026-10-02). The same folder in its long form:
  $env:TEMP = Join-Path $env:LOCALAPPDATA 'Temp'
  $env:TMP = $env:TEMP
  Progress "step 3: copying the repository's tree from $($a.source) to $tree"
  New-Item -ItemType Directory -Force $tree | Out-Null
  & robocopy $a.source $tree /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 /XD .git target obj bin node_modules .claude dist | Out-Null
  $agentBuilt = "$tree\sdk\extensions\agent\plugin\plugin.wasm"
  $agentFixture = "$tree\sdk\fixtures\plugins\agent\plugin.wasm"
  if (-not (Test-Path $agentBuilt) -and (Test-Path $agentFixture)) { New-Item -ItemType Directory -Force (Split-Path $agentBuilt) | Out-Null; Copy-Item $agentFixture $agentBuilt -Force }
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$tree\ui\livecheck\bench-folders.ps1" 2>&1 | ForEach-Object { "bench folders: $_" } | Add-Content -LiteralPath $progressFile
  Stop-Leftovers
  $live = "$env:TEMP\cabinetos-ui-test\live"
  $tries = 5
  while ((Test-Path $live) -and $tries -gt 0) { Remove-Item $live -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $live) { Start-Sleep -Seconds 3 }; $tries-- }
  # Nothing of this script may stand in front of the check's window: the live check stops at once when another
  # program's window is in front (seen 2026-10-02: this script's own Windows Terminal window, which MinimizeAll left).
  (New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep -Milliseconds 800
  if (-not ('VmInstall.Windows' -as [type])) {
    Add-Type -Namespace VmInstall -Name Windows -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);'
  }
  Get-Process WindowsTerminal, powershell, pwsh, conhost -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
    ForEach-Object { [void][VmInstall.Windows]::ShowWindow($_.MainWindowHandle, 6) }
  Start-Sleep -Milliseconds 800
  $runOut = Join-Path $io "run-$tag-install-vm.txt"
  Progress "step 3: livecheck.ps1 -Exe $install\CabinetOS.exe -Virtual -Strict, output $runOut"
  $started = Get-Date
  # A time limit, so a live check that hangs costs step 3 and not the update and the uninstall after it (seen
  # 2026-10-02: no line for half an hour after a real click in the compact section).
  $checkArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$tree\ui\livecheck\livecheck.ps1`" -Exe `"$install\CabinetOS.exe`" -Core `"$install\cabinetos-core.exe`" -Strict -Virtual"
  $check = Start-Process -FilePath powershell.exe -ArgumentList $checkArgs -NoNewWindow -PassThru -RedirectStandardOutput $runOut -RedirectStandardError "$runOut.err"
  $null = $check.Handle
  $limit = $a.liveCheckMinutes
  if (-not $limit) { $limit = 30 }
  $ended = $check.WaitForExit([int]$limit * 60000)
  $timedOut = @()
  if (-not $ended) {
    $timedOut = @("the live check did not end within $limit min; it was stopped")
    Shot 'livecheck-hung'
    $hungLogs = Join-Path $io "vm-install-$tag-livecheck-logs"
    & robocopy "$live\logs" $hungLogs /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    $timedOut += "the window's and the core's logs at that moment: $hungLogs"
    & taskkill.exe /PID $check.Id /T /F | Out-Null
    Stop-Leftovers
    [void]$check.WaitForExit(30000)
    Progress "step 3: $($timedOut[0])"
  }
  $code = if ($ended) { $check.ExitCode } else { -1 }
  $lines = @(Get-Content -LiteralPath $runOut) + @(Get-Content -LiteralPath "$runOut.err" -ErrorAction SilentlyContinue)
  $trues = @($lines | Where-Object { $_ -match ': True$' })
  $falses = @($lines | Where-Object { $_ -match ': False$' })
  $stop = $lines | Where-Object { $_ -match 'STOP:|STRICT:' } | Select-Object -Last 1
  $details = @("exit code $code after $([int]((Get-Date) - $started).TotalMinutes) min; $($trues.Count) True, $($falses.Count) False; output $runOut") + $timedOut
  if ($timedOut) { $details += "its last line: $($lines | Where-Object { $_.Trim() } | Select-Object -Last 1)" }
  if ($stop) { $details += "last stop line: $stop" }
  $details += @($falses | ForEach-Object { "False: $_" })
  $unmeasured = @($lines | Where-Object { $_ -match 'not measured in a VM' })
  $details += "not measured in a VM (frame times): $($unmeasured.Count)"
  Step 3 'the live check runs against the installed CabinetOS.exe' ($ended -and ($trues.Count -gt 0) -and ($falses.Count -eq 0) -and -not $stop) $details
  Shot 'livecheck-end'
  Stop-Leftovers
}
Save-Result 'running'

# ----- 4. The update: found, downloaded and swapped in by itself; the notice; the restart -----
Progress "step 4: the installed CabinetOS with update.source $($a.feedUrl)"
foreach ($folder in "$work\logs", "$work\update") { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue } }
New-Item -ItemType Directory -Force $work, "$work\logs" | Out-Null
$config = [ordered]@{ version = 1; update = [ordered]@{ source = $a.feedUrl } } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText("$work\cabinetos.json", $config, (New-Object Text.UTF8Encoding $false))
$env:CABINETOS_CONFIG = "$work\cabinetos.json"
$env:CABINETOS_LOG_DIR = "$work\logs"
$env:CABINETOS_UPDATE_DIR = "$work\update"
$details = @()
$ok = $true
$window = Start-Process -FilePath "$install\CabinetOS.exe" -WorkingDirectory $install -PassThru
$details += "started $install\CabinetOS.exe (pid $($window.Id)); nothing asked for an update: the daily check runs 10 s after the start"
$noticed = Wait-Until { @(Log-Lines "$work\logs" 'ui.*.jsonl' 'update notice shown').Count -gt 0 } 300
$notice = @(Log-Lines "$work\logs" 'ui.*.jsonl' 'update notice shown') | Select-Object -First 1
Start-Sleep -Seconds 2
Shot 'notice'
if (-not $noticed) {
  $ok = $false
  $states = @(Log-Lines "$work\logs" 'ui.*.jsonl' 'update state' | ForEach-Object { "$($_.fields.state)" }) -join ' > '
  $details += "no 'update notice shown' line within 5 minutes; the window's update states: $states"
} else {
  $details += "the window's log: update notice shown: '$($notice.fields.text)' (failed: $($notice.fields.failed))"
  if ($notice.fields.failed -or $notice.fields.version -ne $a.next) { $ok = $false; $details += "reason: $($notice.fields.reason)" }
}
$states = @(Log-Lines "$work\logs" 'ui.*.jsonl' 'update state' | ForEach-Object { "$($_.fields.state)" })
$details += "the window saw: $($states -join ' > ')"
if ($states -contains 'downloaded') { $ok = $false; $details += 'the window saw downloaded: something waited for a click before the swap' }
foreach ($message in 'update.autoInstall: the swap follows the download', 'the new version is in place', 'the Apps entry names the new version') {
  $seen = @(Log-Lines "$work\logs" 'core.*.jsonl' $message).Count -gt 0
  $details += "core log '$message': $seen"
  if (-not $seen) { $ok = $false }
}
$inPlace = Version-Of $install
$kept = Version-Of (Join-Path $install 'previous')
$details += "release.json in the install folder: $inPlace; in previous\: $kept; unins000.exe in place: $(Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))"
if ($inPlace -ne $a.next -or $kept -ne $a.version -or -not (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))) { $ok = $false }
$entry = Get-ItemProperty -LiteralPath $appsKey -ErrorAction SilentlyContinue
$details += "Apps entry after the swap: DisplayVersion $($entry.DisplayVersion), MajorVersion $($entry.MajorVersion), MinorVersion $($entry.MinorVersion), EstimatedSize $($entry.EstimatedSize)"
if ($entry.DisplayVersion -ne $a.next) { $ok = $false }

# Restart now, as a click would press it: UI Automation's Invoke on the notice's button, in a process of its own with a
# time limit, because a UI Automation call can wait for minutes on a busy window (the run of 2026-10-02 12:05 never
# came back from it).
$restarted = $false
if ($noticed) {
  $press = @"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
`$root = [Windows.Automation.AutomationElement]::RootElement
`$byPid = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $($window.Id))
`$top = `$root.FindFirst([Windows.Automation.TreeScope]::Children, `$byPid)
`$byName = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Restart now')
`$button = `$top.FindFirst([Windows.Automation.TreeScope]::Descendants, `$byName)
`$button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
'invoked'
"@
  $pressOut = Join-Path $work 'press-restart.txt'
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($press))
  $pressed = Run-Limited 'powershell.exe' @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) 90 $pressOut
  $answer = (@(Get-Content -LiteralPath $pressOut -ErrorAction SilentlyContinue) + @(Get-Content -LiteralPath "$pressOut.err" -ErrorAction SilentlyContinue)) -join ' '
  if ($pressed.Ended -and $answer -match 'invoked') {
    $details += 'Restart now invoked through UI Automation'
    $restarted = $true
  } else { $ok = $false; $details += "Restart now could not be pressed (ended: $($pressed.Ended)): $answer" }
}
if ($restarted) {
  $closed = Wait-Until { $window.HasExited } 60
  $details += "the old window closed: $closed"
  $newCore = Wait-Until { @(Log-Lines "$work\logs" 'ui.*.jsonl' 'core started').Count -ge 2 } 90
  $confirmed = Wait-Until { @(Log-Lines "$work\logs" 'core.*.jsonl' 'the swap is confirmed: this start runs its version').Count -gt 0 } 60
  $details += "a new window started its core: $newCore; the core confirmed the swap: $confirmed"
  if (-not ($closed -and $newCore -and $confirmed)) { $ok = $false }
  $token = (@(Log-Lines "$work\logs" 'ui.*.jsonl' 'core started') | Select-Object -Last 1).fields.pipe_token
  Start-Sleep -Seconds 5
  $env:CABINETOS_PIPE = $token
  $cliOut = Join-Path $work 'cli-update.txt'
  $asked = Run-Limited "$install\cabinetos-cli.exe" @('update', '--json') 60 $cliOut
  $reply = (@(Get-Content -LiteralPath $cliOut -ErrorAction SilentlyContinue) + @(Get-Content -LiteralPath "$cliOut.err" -ErrorAction SilentlyContinue)) -join "`n"
  if (-not $asked.Ended) { $reply = "no answer within 60 s. $reply" }
  Remove-Item Env:CABINETOS_PIPE -ErrorAction SilentlyContinue
  try {
    $status = $reply | ConvertFrom-Json
    $details += "cabinetos-cli update --json: current $($status.current), state $($status.state), previous $($status.previous)"
    if ($status.current -ne $a.next) { $ok = $false }
  } catch { $ok = $false; $details += "cabinetos-cli update --json gave no state: $reply" }
  $saved = Get-Content -Raw -LiteralPath "$work\update\state.json" -ErrorAction SilentlyContinue | ConvertFrom-Json
  $details += "state.json: swap to $($saved.swap.to), confirmedAtMs $($saved.swap.confirmedAtMs)"
  if ($saved.swap.to -ne $a.next -or -not $saved.swap.confirmedAtMs) { $ok = $false }
  Start-Sleep -Seconds 3
  Shot 'restarted'
}
Step 4 'the next version installs itself, the notice asks for a restart, and the restart runs it' $ok $details
Save-Result 'running'
Get-Process CabinetOS -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
[void](Wait-Until { (Running-From $install).Count -eq 0 } 30)
Running-From $install | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
foreach ($name in 'CABINETOS_CONFIG', 'CABINETOS_LOG_DIR', 'CABINETOS_UPDATE_DIR') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3

# ----- 5. Inno's uninstaller, silently -----
$data = Join-Path $env:LOCALAPPDATA 'CabinetOS'
New-Item -ItemType Directory -Force $data | Out-Null
$marker = Join-Path $data 'vm-install-check-marker.txt'
"the user's data must outlive the uninstall ($tag)" | Set-Content -LiteralPath $marker
$uninstallLog = Join-Path $a.vmIo "uninstall-$tag.log"
$details = @("previous\ before the uninstall: $(Test-Path -LiteralPath (Join-Path $install 'previous'))")
$running = Running-From $install
if ($running.Count -gt 0) { $details += "still running from the install: $(($running | ForEach-Object { $_.Name }) -join ', ')" }
$removing = Get-Date
$uninstaller = Run-Limited (Join-Path $install 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog") 600
$gone = Wait-Until { -not (Test-Path -LiteralPath $install) -and -not (Test-Path -LiteralPath $appsKey) } 120
$details += "exit code $($uninstaller.Code) after $([int]((Get-Date) - $removing).TotalSeconds) s (ended: $($uninstaller.Ended)); log $uninstallLog"
$details += "install folder gone: $(-not (Test-Path -LiteralPath $install)); Apps entry gone: $(-not (Test-Path -LiteralPath $appsKey)); Start Menu shortcut gone: $(-not (Test-Path -LiteralPath $shortcut)); the user's data stays: $(Test-Path -LiteralPath $marker)"
if (Test-Path -LiteralPath $install) { $details += "left in the install folder: $((Get-ChildItem -LiteralPath $install -Force -Recurse | Select-Object -First 10 | ForEach-Object { $_.FullName }) -join ', ')" }
Step 5 'the uninstaller removes the install, its entry and its shortcut, and keeps the data' ($gone -and -not (Test-Path -LiteralPath $shortcut) -and (Test-Path -LiteralPath $marker)) $details
Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
Shot 'uninstalled'
Save-Result 'done'
Progress 'done'
