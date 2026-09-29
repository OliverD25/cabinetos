# Runs the live check and, when it ends, writes a DONE file and opens it in Notepad, so whoever
# waits at the PC sees on screen that the keyboard and mouse are free again (docs/ui.md, "The live
# check"). The output goes to _io\live-check next to the repository, never into the repository.
# -Io names that folder when the repository is a git worktree somewhere else.
param([string]$Tag = (Get-Date -Format 'yyyy-MM-dd-HHmm'), [string]$Io = '')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$io = if ($Io) { $Io } else { Join-Path (Split-Path $repo -Parent) '_io\live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$out = "$io\run-$Tag.txt"
$started = Get-Date
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\livecheck.ps1" -Strict 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $out -Encoding UTF8
$code = $LASTEXITCODE
$ended = Get-Date
$lines = Get-Content $out
$goal = ($lines | Where-Object { $_ -like 'scroll goal*' } | Select-Object -Last 1)
$stop = ($lines | Where-Object { $_ -match 'STOP:|STRICT:' } | Select-Object -Last 1)
$falses = @($lines | Where-Object { $_ -match ': False$' })
$done = @(
  "# Live check finished: you can use the keyboard and mouse again",
  "",
  "Started $($started.ToString('HH:mm:ss')), ended $($ended.ToString('HH:mm:ss')), exit code $code.",
  "",
  "- $(if ($goal) { $goal } else { 'The scroll goal line was not printed (the run stopped before it).' })",
  "- $(if ($stop) { "Last stop line: $stop" } else { 'No stop line: the script ran to its end.' })",
  "- Checks that answered False: $(if ($falses.Count) { $falses.Count } else { 'none' })"
) + @($falses | ForEach-Object { "  - $_" }) + @(
  "",
  "Full output: $out"
)
$done | Set-Content -LiteralPath "$io\DONE.md" -Encoding UTF8
Start-Process notepad.exe -ArgumentList "`"$io\DONE.md`""
"finished with exit code $code; DONE.md written and opened"
