# Runs the live check and, when it ends, writes a DONE file and opens it in Notepad, so whoever
# waits at the PC sees on screen that the keyboard and mouse are free again (docs/ui.md, "The live
# check"). The output goes to _io\live-check next to the main checkout (also when the repository is a git
# worktree, paths.ps1), never into the repository. -Io names another folder.
# It first shows the countdown window (countdown.ps1) for 5 s, so whoever sits at the PC lets go of the
# keyboard and mouse in time; -NoCountdown skips it for a run that starts while nobody is there.
# -MinimizeOthers first minimizes every other window, for a machine that runs the check on its own (the
# remote live check): a window left in front there, a terminal for example, would stop the check at once.
# -Virtual passes on to livecheck.ps1, for a virtual machine: the checks that judge frame times answer "not measured in
# a VM", and DONE.md counts them apart, neither True nor False. -Panel passes on too, for a laptop whose display path
# sleeps between pages: the scroll goal is judged by the frames' UI work (the "panel goal" line) and the gap goal answers
# "not judged on a panel", which DONE.md counts apart too. -Virtual wins over -Panel.
# -RunId is the id of a run on the laptop (remote-livecheck.ps1): it becomes the tag, and the file written last is
# DONE-<id>.md instead of DONE.md, so a waiting script finds its own run's result and no other. Either way DONE.md
# ends with the lines "ran commit <hash>" (the commit of the repository this script is in: the code that ran) and
# "exit code <n>" (livecheck.ps1's own), and this script exits with that code.
param([string]$Tag = (Get-Date -Format 'yyyy-MM-dd-HHmm'), [string]$Io = '', [switch]$NoCountdown, [switch]$MinimizeOthers, [switch]$Virtual, [switch]$Panel, [string]$RunId = '')
. "$PSScriptRoot\paths.ps1"
if ($RunId) { $Tag = $RunId }
$io = if ($Io) { $Io } else { Join-Path (Get-IoFolder) 'live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$out = "$io\run-$Tag.txt"
if (-not $NoCountdown) {
  . "$PSScriptRoot\countdown.ps1"
  if (-not (Show-InputCountdown -Seconds 5 -What 'The live check')) { 'cancelled at the countdown: nothing ran'; exit 2 }
}
# After the countdown, not before: when its window closes, Windows brings the window that was active back to the
# front, and that one would cover the check's window.
if ($MinimizeOthers) { (New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep -Milliseconds 800 }
$started = Get-Date
$checkArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSScriptRoot\livecheck.ps1", '-Strict')
if ($Virtual) { $checkArgs += '-Virtual' }
if ($Panel) { $checkArgs += '-Panel' }
& powershell.exe @checkArgs 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $out -Encoding UTF8
$code = $LASTEXITCODE
$ended = Get-Date
$lines = Get-Content $out
$goal = ($lines | Where-Object { $_ -like 'scroll goal*' } | Select-Object -Last 1)
$stop = ($lines | Where-Object { $_ -match 'STOP:|STRICT:' } | Select-Object -Last 1)
$falses = @($lines | Where-Object { $_ -match ': False$' })
$unmeasured = @($lines | Where-Object { $_ -match 'not measured in a VM' })
$judgedByWork = $Panel -and -not $Virtual
$panelGoal = ($lines | Where-Object { $_ -like 'panel goal*' } | Select-Object -Last 1)
$unjudged = @($lines | Where-Object { $_ -match 'not judged on a panel' })
$commit = "$(& git -C $PSScriptRoot rev-parse HEAD 2>$null)".Trim()
$doneFile = if ($RunId) { "$io\DONE-$RunId.md" } else { "$io\DONE.md" }
$done = @(
  "# Live check finished: you can use the keyboard and mouse again",
  "",
  "Started $($started.ToString('HH:mm:ss')), ended $($ended.ToString('HH:mm:ss')), exit code $code.",
  "",
  "- $(if ($goal) { $goal } else { 'The scroll goal line was not printed (the run stopped before it).' })",
  "- $(if ($stop) { "Last stop line: $stop" } else { 'No stop line: the script ran to its end.' })",
  "- Checks that answered False: $(if ($falses.Count) { $falses.Count } else { 'none' })"
) + @($falses | ForEach-Object { "  - $_" }) + @(
  $(if ($Virtual) { "- Checks not measured in a VM (they judge frame times; neither True nor False): $($unmeasured.Count)" })
) + @(
  $(if ($judgedByWork) { "- Judged by the frames' UI work on a panel (-Panel), not by the gaps: $(if ($panelGoal) { $panelGoal } else { 'the panel goal line was not printed (the run stopped before it).' })" })
) + @(
  $(if ($judgedByWork) { "- Checks not judged on a panel (they judge frame gaps; neither True nor False): $($unjudged.Count)" })
) + @(
  "",
  "Full output: $out",
  "ran commit $commit",
  "exit code $code"
)
# Written whole in one step: a script that waits for the file never reads half of it.
$done | Set-Content -LiteralPath "$doneFile.tmp" -Encoding UTF8
Move-Item -LiteralPath "$doneFile.tmp" -Destination $doneFile -Force
Start-Process notepad.exe -ArgumentList "`"$doneFile`""
"finished with exit code $code; $(Split-Path $doneFile -Leaf) written and opened"
exit $code
