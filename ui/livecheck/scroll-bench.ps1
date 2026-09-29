# Measures the scroll of the 100,000-entry folder (docs/ui.md, "Scrolling"). No key is pressed:
# the window's snapshot step scroll:<pages> pages the active pane 30 times a second, as a held
# PageDown does, and logs the run's frame table ("scroll run") with the machine's CPU load. Each
# run starts a fresh window on the folder that `cargo bench -p cabinetos-fs --bench list_directory`
# makes in %TEMP%\cabinetos-bench. Before each run the machine's CPU is read for 2 s: while it is
# busier than -MaxBusy percent (another agent compiling, say), the run waits 20 s and reads again,
# because frame times on a busy machine say little. Prints each run's table, then the best and the
# worst run. Needs the release builds of the window and the core. Runs in Windows PowerShell 5.1
# and PowerShell 7.
#
# The frame rate depends on the display: awake, Windows draws 60 frames a second; asleep or locked,
# about 30. Each run reports the window's idle frame rate before the scroll. With the display asleep
# the gaps between frames say little, and "frames whose UI-thread work passed 16.7 ms" (one 60 Hz
# frame) is the number to read; -Rhythm 2 then presses once every second frame, which is the rhythm
# of 30 presses a second on a 60 Hz display.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\scroll-bench.ps1 -Runs 3
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Folder = "$env:TEMP\cabinetos-bench\100000",
  [int]$Runs = 3,
  [int]$Pages = 150,
  [double]$MaxBusy = 30,
  [int]$Tries = 15,
  [int]$Rhythm = 0,
  [string]$Run = "scroll-bench"
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
foreach ($needed in $Exe, $Core, $Folder) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing"; exit 1 } }
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ScrollBenchCpu {
  [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  // Busy and all processor time of the machine so far, summed over its logical processors (kernel time includes idle).
  public static long[] Read() { long idle, kernel, user; GetSystemTimes(out idle, out kernel, out user); return new long[] { kernel + user - idle, kernel + user }; }
}
"@

# The machine's load over $Milliseconds: its total in percent of all processors, and the busiest process.
function Measure-Load([int]$Milliseconds) {
  $before = @{}
  foreach ($p in Get-Process) { try { $before[$p.Id] = @($p.ProcessName, $p.TotalProcessorTime.Ticks) } catch { } }
  $a = [ScrollBenchCpu]::Read()
  Start-Sleep -Milliseconds $Milliseconds
  $b = [ScrollBenchCpu]::Read()
  $all = [double]($b[1] - $a[1])
  $top = $null; $topTicks = 0
  foreach ($p in Get-Process) {
    try {
      if ($before.ContainsKey($p.Id)) {
        $delta = $p.TotalProcessorTime.Ticks - $before[$p.Id][1]
        if ($delta -gt $topTicks) { $topTicks = $delta; $top = $p.ProcessName }
      }
    } catch { }
  }
  [pscustomobject]@{ Total = [Math]::Round(100 * ($b[0] - $a[0]) / $all, 1); Top = $top; TopPercent = [Math]::Round(100 * $topTicks / $all, 1) }
}

function Row([string]$Label, $Line, [string]$Key) {
  "| {0,-34} | {1,10:N2} | {2,17:N2} | {3,11:N2} |" -f $Label, $Line."all_$Key", $Line."slow_$Key", $Line."worst_$Key"
}

function Show-Run($Line, $Settle, $Before) {
  "{0} pages, {1:N0} rows ({2} per page) in {3} s; CPU during the run: machine {4} % (this window {5} %, its core {6} %, others {7} %); before it: {8} % (busiest: {9} {10} %)" -f `
    $Line.pages, $Line.rows_moved, $Line.rows_per_page, $Line.seconds, $Line.cpu_total_percent, $Line.cpu_ui_percent, $Line.cpu_core_percent, $Line.cpu_others_percent, $Before.Total, $Before.Top, $Before.TopPercent
  "frames {0}; over 20 ms: {1} ({2} %); over 33 ms: {3}; median {4} ms; p95 {5} ms; worst {6} ms; goal (none over 33 ms, under 5 % over 20 ms) met: {7}" -f `
    $Line.frames, $Line.over_20ms, $Line.percent_over_20ms, $Line.over_33ms, $Line.median_ms, $Line.p95_ms, $Line.worst_ms, $Line.goal_met
  "frames whose UI-thread work passed 16.7 ms: {0} ({1} %); the busiest frame's work: {2} ms" -f $Line.busy_over_16ms, $Line.percent_busy_over_16ms, $Line.busiest_ms
  "rows bound: {0}; measure passes: {1}; type-name pages applied: {2}; requests: {3}" -f $Line.bind_calls, $Line.measure_calls, $Line.details_calls, $Line.requests_calls
  # Per second of the run, so runs with different frame counts compare (the frame clock halves when the display sleeps).
  "UI thread per second of scrolling: {4:N0} ms of work; WinUI's frames {0:N0} ms; the rows' measure {1:N0} ms and arrange {2:N0} ms (inside or outside the frames); our own work {3:N0} ms" -f `
    ($Line.all_work_ms * $Line.frames / $Line.seconds), ($Line.all_measure_ms * $Line.frames / $Line.seconds), ($Line.all_arrange_ms * $Line.frames / $Line.seconds), `
    (($Line.all_details_ms + $Line.all_requests_ms + $Line.all_icons_ms + $Line.all_selection_ms + $Line.all_status_ms) * $Line.frames / $Line.seconds), `
    ($Line.all_busy_ms * $Line.frames / $Line.seconds)
  "| {0,-34} | {1,10} | {2,17} | {3,11} |" -f "ms per frame", "all frames", "frames over 20 ms", "worst frame"
  "|{0}|{1}|{2}|{3}|" -f ('-' * 36), ('-' * 12), ('-' * 19), ('-' * 13)
  Row "gap between frames" $Line "gap_ms"
  Row "UI-thread work (frame + outside)" $Line "busy_ms"
  Row "WinUI's frame (its layout, drawing)" $Line "work_ms"
  Row "rows' measure pass" $Line "measure_ms"
  Row "  of it: rows bound to entries" $Line "bind_ms"
  Row "  of it: rows' own measure (texts)" $Line "row_measure_ms"
  Row "rows' arrange pass" $Line "arrange_ms"
  Row "type-name pages applied" $Line "details_ms"
  Row "type-name requests sent" $Line "requests_ms"
  Row "icons" $Line "icons_ms"
  Row "selection marks" $Line "selection_ms"
  Row "status bar" $Line "status_ms"
  if ($Settle) {
    "the second after the last press: frames {0}, over 20 ms {1}, over 33 ms {2}, worst {3} ms" -f $Settle.frames, $Settle.over_20ms, $Settle.over_33ms, $Settle.worst_ms
  }
}

$results = @()
$root = "$env:TEMP\cabinetos-ui-test\$Run"
for ($n = 1; $n -le $Runs; $n++) {
  $load = $null
  for ($try = 1; $try -le $Tries; $try++) {
    $load = Measure-Load 2000
    if ($load.Total -le $MaxBusy) { break }
    "run ${n}: the machine is busy ({0} %, busiest {1} at {2} %); waiting 20 s" -f $load.Total, $load.Top, $load.TopPercent
    Start-Sleep -Seconds 20
    $load = $null
  }
  if (-not $load) { "run ${n}: skipped, the machine stayed busy"; continue }

  foreach ($part in 'config', 'logs', 'shots', 'themes', 'plugins', 'plugins-data', 'marketplace') {
    if (Test-Path -LiteralPath "$root\$part") { Remove-Item -LiteralPath "$root\$part" -Recurse -Force }
  }
  New-Item -ItemType Directory -Force "$root\config", "$root\logs", "$root\shots" | Out-Null
  $env:CABINETOS_CORE_EXE = $Core
  $env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
  $env:CABINETOS_LOG_DIR = "$root\logs"
  $env:CABINETOS_THEMES_DIR = "$root\themes"
  $env:CABINETOS_PLUGINS_DIR = "$root\plugins"
  $env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
  $env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
  $env:CABINETOS_UI_FRAMESTATS = "1"
  $env:CABINETOS_UI_SNAPSHOT = "$root\shots"
  $scroll = if ($Rhythm -gt 0) { "scroll:$Pages/$Rhythm" } else { "scroll:$Pages" }
  $env:CABINETOS_UI_SNAPSHOT_STEPS = "path:$Folder;wait:3000;$scroll;wait:300;shot:done"
  $p = Start-Process -FilePath $Exe -PassThru
  $deadline = (Get-Date).AddSeconds(60 + $Pages / 30)
  while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath "$root\shots\done.png") -and -not $p.HasExited) { Start-Sleep -Milliseconds 250 }
  if (-not $p.HasExited) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000) }
  $lines = @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.target -eq 'cabinetos_ui::frames' })
  $line = $lines | Where-Object { $_.message -eq 'scroll run' -and $_.fields.label -like 'scroll:*' } | Select-Object -First 1
  $settle = $lines | Where-Object { $_.message -eq 'scroll settle' -or ($_.message -eq 'scroll run' -and $_.fields.label -eq 'scroll settle') } | Select-Object -First 1
  if (-not $line) { "run ${n}: no scroll run in the log"; continue }
  # Seconds with almost no work on the UI thread: how often the display asks for frames.
  $idle = @($lines | Where-Object { $_.message -eq 'frame stats' -and $_.fields.work_ms -lt 5 } | ForEach-Object { $_.fields.frames } | Sort-Object)
  $clock = if ($idle.Count -gt 0) { $idle[[int][Math]::Floor($idle.Count / 2)] } else { '?' }
  ""
  "run ${n} ($scroll; idle frame clock {0} frames a second: {1}):" -f $clock, $(if ($clock -ne '?' -and $clock -ge 50) { 'display awake' } elseif ($clock -ne '?') { 'display asleep or locked' } else { 'not seen' })
  Show-Run $line.fields $(if ($settle) { $settle.fields }) $load
  $slow = @($lines | Where-Object { $_.message -eq 'slow frame' })
  if ($slow.Count -gt 0) {
    "frames of 33 ms or more during the run:"
    $slow | ForEach-Object { "  {0} ms (WinUI {1} ms; measure {2}, rows' own measure {3}, bind {4}, details {5}, selection {6}, status {7})" -f $_.fields.gap_ms, $_.fields.work_ms, $_.fields.measure_ms, $_.fields.row_measure_ms, $_.fields.bind_ms, $_.fields.details_ms, $_.fields.selection_ms, $_.fields.status_ms }
  }
  $results += [pscustomobject]@{ Run = $n; Line = $line.fields; Settle = $(if ($settle) { $settle.fields }); Before = $load; Clock = $clock }
}

if ($results.Count -eq 0) { "no run measured"; exit 1 }
# With the display awake the gaps rank the runs; asleep, the frames are paced at about 30 a second
# whatever the window does, so the UI thread's work ranks them.
$awake = @($results | Where-Object { $_.Clock -ne '?' -and $_.Clock -ge 50 }).Count -eq $results.Count
$sorted = if ($awake) {
  @($results | Sort-Object { $_.Line.over_33ms }, { $_.Line.percent_over_20ms }, { $_.Line.worst_ms })
} else {
  @($results | Sort-Object { $_.Line.percent_busy_over_16ms }, { $_.Line.busiest_ms })
}
""
"best of {0}: run {1}; worst: run {2} (ranked by {3})" -f $results.Count, $sorted[0].Run, $sorted[-1].Run, $(if ($awake) { 'the gaps between frames' } else { 'the UI thread''s work: the display was asleep' })
"| run | idle clock | frames | over 20 ms | over 33 ms | median | p95 | worst | work over 16.7 ms | busiest work | CPU machine | CPU others | goal met |"
"|---|---|---|---|---|---|---|---|---|---|---|---|---|"
foreach ($r in $results) {
  "| {0} | {1}/s | {2} | {3} ({4} %) | {5} | {6} ms | {7} ms | {8} ms | {9} ({10} %) | {11} ms | {12} % | {13} % | {14} |" -f $r.Run, $r.Clock, $r.Line.frames, $r.Line.over_20ms, $r.Line.percent_over_20ms, $r.Line.over_33ms, $r.Line.median_ms, $r.Line.p95_ms, $r.Line.worst_ms, $r.Line.busy_over_16ms, $r.Line.percent_busy_over_16ms, $r.Line.busiest_ms, $r.Line.cpu_total_percent, $r.Line.cpu_others_percent, $r.Line.goal_met
}
