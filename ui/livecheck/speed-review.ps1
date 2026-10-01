# The speed review's measurements (docs/log/2026-10-01/speed-review.md): each scenario starts a fresh release
# window on a scratch configuration, drives it with the snapshot steps (CABINETOS_UI_SNAPSHOT_STEPS), and reads
# the numbers from the window's and the core's logs. No key is pressed and no mouse is moved, so it can run on a
# locked screen; the frame numbers then come from a display that draws about 30 frames a second, and the
# scenario lines say which clock was seen. The folders are the ones speed-fixtures.ps1 makes.
#
# Scenarios (each -Runs times, a new window each time):
#   start-small   the process start to the first "listing shown" of both panes, two small folders
#   start-home    the same, with the left pane on a 3,000-entry folder (a real home folder's size)
#   list-files    listing 100,000 files, its first paint's frames, then scroll:20 with the frame table
#   list-folders  the same for 10,000 folders
#   tabs          tab:next between a tab on 100,000 files and one on 10,000 folders
#   theme         Commander Compact and back, over 100,000 files and 3,000 entries
#   picker        the theme picker's live preview moved over the shipped themes, over the same folders
#   menu          the context menu of a file, a folder and the background, the first and the second opening
#   quick-open    Quick Open on the 100,000-file folder with three texts
#   find          Find in pane on the 100,000-file listing, typed one letter at a time
#   market        the marketplace's first view on a local index (sdk/marketplace/build-index.ps1)
#   idle-start    a window on two small folders, nothing done: 60 s of idle CPU and the working sets
#   session       the above in one window without frame stats; then 60 s of idle CPU and the working sets
#
# Before each run it waits while another CabinetOS window runs (other agents' tests share the machine), up to
# -Tries minutes, and records the machine's CPU load; a run that had to share, or saw another window start before
# it ended, says so. Each run's logs are
# kept under <Out>\<scenario>-<n>\ and the numbers go to <Out>\results.json and the screen. Needs the release
# builds of the window and the core. Runs in Windows PowerShell 5.1 and PowerShell 7.
#
# -Variants compares builds of the window run by run, in turn, so a machine that gets busier or quieter treats
# them alike: "before=<exe>;after=<exe>", or "<window exe>|<core exe>" for a variant with a core of its own. Each
# variant's runs go under <Out>\<variant>\.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\speed-review.ps1 -Runs 3
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\speed-review.ps1 -Scenarios menu -Runs 5
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Root = "$env:TEMP\cabinetos-speed-review",
  [string[]]$Scenarios = @('start-small', 'start-home', 'list-files', 'list-folders', 'tabs', 'theme', 'picker', 'menu', 'quick-open', 'find', 'market', 'idle-start', 'session'),
  [int]$Runs = 3,
  [string]$Out = "",
  [int]$Tries = 40,
  [int]$IdleSeconds = 60,
  [int]$Resident = 10,
  [string]$Variants = ""
)
$ErrorActionPreference = 'Stop'
# powershell -File passes "a,b" as one text.
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
$fixtures = "$Root\fixtures"
foreach ($needed in $Exe, $Core, "$fixtures\files-100000.complete", "$fixtures\folders-10000.complete", "$fixtures\home-3000.complete", "$fixtures\small.complete") {
  if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing (speed-fixtures.ps1 makes the folders)"; exit 1 }
}
if (-not $Out) { $Out = "$Root\results\$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
[void](New-Item -ItemType Directory -Force $Out)
$small = "$fixtures\small"; $home3k = "$fixtures\home-3000"; $files = "$fixtures\files-100000"; $folders = "$fixtures\folders-10000"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpeedReviewCpu {
  [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  public static long[] Read() { long idle, kernel, user; GetSystemTimes(out idle, out kernel, out user); return new long[] { kernel + user - idle, kernel + user }; }
}
"@

function Measure-Load([int]$Milliseconds) {
  $a = [SpeedReviewCpu]::Read()
  Start-Sleep -Milliseconds $Milliseconds
  $b = [SpeedReviewCpu]::Read()
  [Math]::Round(100 * ($b[0] - $a[0]) / [double]($b[1] - $a[1]), 1)
}

# Waits while another CabinetOS window runs; true when the run has to share the machine after all. A window that has
# run for -Resident minutes already (one someone keeps open to work in) is not waited for, only counted as sharing.
function Wait-Alone {
  for ($try = 1; $try -le $Tries; $try++) {
    $others = @(Get-Process -Name CabinetOS -ErrorAction SilentlyContinue | Where-Object { ((Get-Date) - $_.StartTime).TotalMinutes -lt $Resident })
    if ($others.Count -eq 0) { break }
    Write-Host ("  {0} other CabinetOS window(s) run; waiting 60 s ({1} of {2})" -f $others.Count, $try, $Tries)
    Start-Sleep -Seconds 60
  }
  return (@(Get-Process -Name CabinetOS -ErrorAction SilentlyContinue).Count -gt 0)
}

function Build-MarketIndex {
  $index = "$Root\market-index"
  if (-not (Test-Path -LiteralPath "$index\index.json")) {
    $script = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\sdk\marketplace\build-index.ps1")
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -OutDir $index -Collection | Out-Null
  }
  "$index\index.json"
}

# One window: a fresh scratch configuration, the steps, the logs kept. Returns what the analysis needs.
function Invoke-Run([string]$Exe, [string]$CoreExe, [string]$Folder, [string]$Scenario, [int]$N, [string]$Steps, [hashtable]$Config, [bool]$FrameStats, [string]$Log = "", [bool]$Idle = $false) {
  $dir = "$Folder\$Scenario-$N"
  $work = "$Root\work"
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
  [void](New-Item -ItemType Directory -Force "$work\config", "$work\logs", "$work\shots", $dir)
  # Without a byte order mark, which Set-Content -Encoding UTF8 writes in Windows PowerShell.
  [System.IO.File]::WriteAllText("$work\config\cabinetos.json", ($Config | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
  $shared = Wait-Alone
  $load = Measure-Load 2000
  $env:CABINETOS_CORE_EXE = $CoreExe
  $env:CABINETOS_CONFIG = "$work\config\cabinetos.json"
  $env:CABINETOS_LOG_DIR = "$work\logs"
  foreach ($pair in @(('THEMES', 'themes'), ('UNDO', 'undo'), ('PLUGINS', 'plugins'), ('PLUGINS_DATA', 'plugins-data'), ('MARKETPLACE', 'marketplace'),
      ('WEBVIEW2', 'webview2'), ('TOOLS', 'tools'), ('UPDATE', 'update'))) {
    Set-Item -Path "env:CABINETOS_$($pair[0])_DIR" -Value "$work\$($pair[1])"
  }
  $env:CABINETOS_UI_SNAPSHOT = "$work\shots"
  $env:CABINETOS_UI_SNAPSHOT_STEPS = $Steps
  $env:CABINETOS_UI_FRAMESTATS = if ($FrameStats) { '1' } else { '' }
  $env:CABINETOS_LOG = $Log
  $p = Start-Process -FilePath $Exe -PassThru
  $startedUtc = $p.StartTime.ToUniversalTime()
  $deadline = (Get-Date).AddSeconds(180)
  while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath "$work\shots\done.png") -and -not $p.HasExited) { Start-Sleep -Milliseconds 200 }
  $done = Test-Path -LiteralPath "$work\shots\done.png"
  # Not $idle: PowerShell names ignore case, and that is the parameter $Idle.
  $idleNumbers = $null
  if ($Idle -and $done -and -not $p.HasExited) {
    $corePid = (Read-Log "$work\logs\ui.*.jsonl" | Where-Object { $_.message -eq 'core started' } | Select-Object -First 1).fields.pid
    $coreProcess = Get-Process -Id $corePid -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 10
    $p.Refresh(); $uiBefore = $p.TotalProcessorTime; $coreBefore = $coreProcess.TotalProcessorTime
    $machineBefore = [SpeedReviewCpu]::Read()
    Start-Sleep -Seconds $IdleSeconds
    $p.Refresh(); $coreProcess.Refresh(); $machineAfter = [SpeedReviewCpu]::Read()
    $idleNumbers = [ordered]@{
      seconds = $IdleSeconds
      ui_cpu_ms = [Math]::Round(($p.TotalProcessorTime - $uiBefore).TotalMilliseconds, 1)
      core_cpu_ms = [Math]::Round(($coreProcess.TotalProcessorTime - $coreBefore).TotalMilliseconds, 1)
      machine_percent = [Math]::Round(100 * ($machineAfter[0] - $machineBefore[0]) / [double]($machineAfter[1] - $machineBefore[1]), 1)
      ui_working_set_mb = [Math]::Round($p.WorkingSet64 / 1MB, 1)
      ui_private_mb = [Math]::Round($p.PrivateMemorySize64 / 1MB, 1)
      core_working_set_mb = [Math]::Round($coreProcess.WorkingSet64 / 1MB, 1)
      core_private_mb = [Math]::Round($coreProcess.PrivateMemorySize64 / 1MB, 1)
      ui_threads = $p.Threads.Count
      core_threads = $coreProcess.Threads.Count
    }
  }
  if (-not $p.HasExited) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(10000) }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  # Another window that came during the run shared the machine too.
  $shared = $shared -or (@(Get-Process -Name CabinetOS -ErrorAction SilentlyContinue).Count -gt 0)
  Start-Sleep -Milliseconds 500
  Copy-Item -Path "$work\logs\*.jsonl" -Destination $dir
  [pscustomobject]@{ Scenario = $Scenario; N = $N; Dir = $dir; StartedUtc = $startedUtc; Done = $done; Shared = $shared; LoadBefore = $load; Idle = $idleNumbers }
}

function TsUtc($ts) {
  if ($ts -is [datetime]) { return $ts.ToUniversalTime() }
  return [datetime]::Parse([string]$ts, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]'AdjustToUniversal, AssumeUniversal')
}

function Read-Log([string]$Pattern) {
  @(Get-Content -Path $Pattern -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object {
      $line = $_ | ConvertFrom-Json
      $line | Add-Member -NotePropertyName at -NotePropertyValue (TsUtc $line.ts) -PassThru
    })
}

function Pct($values, [double]$p) {
  $sorted = @($values | Sort-Object)
  if ($sorted.Count -eq 0) { return $null }
  $sorted[[int][Math]::Min($sorted.Count - 1, [Math]::Floor($p * ($sorted.Count - 1) + 0.5))]
}

function Ms($from, $to) { [Math]::Round(($to - $from).TotalMilliseconds, 1) }

function Total($lines, [string]$Field) { $sum = 0.0; foreach ($l in $lines) { $sum += [double]$l.fields.$Field }; $sum }
function Most($lines, [string]$Field) { $most = $null; foreach ($l in $lines) { if ($null -eq $most -or [double]$l.fields.$Field -gt $most) { $most = [double]$l.fields.$Field } }; $most }

# The frames of a span, from the per-second lines that end inside it (or up to a second after it) and the slow frames in it.
function Frames($ui, $from, $to) {
  $seconds = @($ui | Where-Object { $_.message -eq 'frame stats' -and $_.at -gt $from -and $_.at -le $to.AddSeconds(1) })
  $slow = @($ui | Where-Object { $_.message -eq 'slow frame' -and $_.at -ge $from -and $_.at -le $to.AddMilliseconds(500) })
  [ordered]@{
    frames = Total $seconds 'frames'
    over_20ms = Total $seconds 'gaps_over_20ms'
    over_33ms = Total $seconds 'gaps_over_33ms'
    worst_ms = Most $seconds 'worst_ms'
    slow = @($slow | ForEach-Object { "{0} ms (work {1}, gc {2})" -f $_.fields.gap_ms, $_.fields.work_ms, $_.fields.gc_pause_ms })
    worst_work_ms = Most $slow 'busy_ms'
  }
}

function Clock($ui) {
  $idle = @($ui | Where-Object { $_.message -eq 'frame stats' -and $_.fields.work_ms -lt 5 } | ForEach-Object { $_.fields.frames } | Sort-Object)
  if ($idle.Count) { $idle[[int][Math]::Floor($idle.Count / 2)] } else { $null }
}

function Pair($ui, [string]$RequestId) {
  $reply = $ui | Where-Object { $_.message -eq 'reply received' -and $_.request_id -eq $RequestId } | Select-Object -First 1
  if ($reply) { [Math]::Round($reply.fields.elapsed_us / 1000.0, 2) } else { $null }
}

function Handled($core, [string]$RequestId) {
  $line = $core | Where-Object { $_.message -eq 'request handled' -and $_.request_id -eq $RequestId } | Select-Object -First 1
  if ($line) { [Math]::Round($line.fields.elapsed_us / 1000.0, 2) } else { $null }
}

function Analyze($run) {
  $ui = Read-Log "$($run.Dir)\ui.*.jsonl"
  $core = Read-Log "$($run.Dir)\core.*.jsonl"
  $r = [ordered]@{ scenario = $run.Scenario; run = $run.N; done = $run.Done; shared = $run.Shared; machine_cpu_before = $run.LoadBefore; clock = (Clock $ui) }
  switch -Wildcard ($run.Scenario) {
    'start-*' {
      $first = $ui | Select-Object -First 1
      $shown = @($ui | Where-Object { $_.message -eq 'listing shown' })
      $r.first_log_line_ms = Ms $run.StartedUtc $first.at
      $r.core_started_ms = Ms $run.StartedUtc ($ui | Where-Object { $_.message -eq 'core started' } | Select-Object -First 1).at
      $connected = $ui | Where-Object { $_.message -eq 'connected to the core' } | Select-Object -First 1
      $r.connected_ms = Ms $run.StartedUtc $connected.at
      $hello = $ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'hello' } | Select-Object -First 1
      $r.hello_round_trip_ms = Pair $ui $hello.request_id
      $r.hello_core_ms = Handled $core $hello.request_id
      if ($shown.Count -ge 2) {
        $r.left_shown_ms = Ms $run.StartedUtc $shown[0].at
        $r.both_shown_ms = Ms $run.StartedUtc $shown[1].at
        $r.left = "{0}: {1} entries, core {2} ms, reply {3} ms, first frame {4} ms" -f (Split-Path -Leaf $shown[0].fields.path), $shown[0].fields.entries, ($shown[0].fields.core_us / 1000), $shown[0].fields.reply_ms, $shown[0].fields.first_frame_ms
        $r.right = "{0}: {1} entries, core {2} ms, reply {3} ms, first frame {4} ms" -f (Split-Path -Leaf $shown[1].fields.path), $shown[1].fields.entries, ($shown[1].fields.core_us / 1000), $shown[1].fields.reply_ms, $shown[1].fields.first_frame_ms
      }
    }
    'list-*' {
      $path = if ($run.Scenario -eq 'list-files') { $files } else { $folders }
      $shown = $ui | Where-Object { $_.message -eq 'listing shown' -and $_.fields.path -eq $path } | Select-Object -First 1
      if ($shown) {
        $sent = $ui | Where-Object { $_.message -eq 'request sent' -and $_.request_id -eq $shown.request_id } | Select-Object -First 1
        $r.entries = $shown.fields.entries
        $r.core_list_ms = [Math]::Round($shown.fields.core_us / 1000.0, 1)
        $r.core_handled_ms = Handled $core $shown.request_id
        $r.round_trip_ms = Pair $ui $shown.request_id
        $r.reply_ms = $shown.fields.reply_ms
        $r.first_row_ms = $shown.fields.first_row_ms
        $r.first_frame_ms = $shown.fields.first_frame_ms
        $from = if ($sent) { $sent.at } else { $shown.at.AddMilliseconds(-$shown.fields.first_frame_ms) }
        $r.first_paint = Frames $ui $from $shown.at.AddMilliseconds(1000)
      }
      $scroll = $ui | Where-Object { $_.message -eq 'scroll run' -and $_.fields.label -like 'scroll:*' } | Select-Object -First 1
      if ($scroll) {
        $f = $scroll.fields
        $r.scroll = [ordered]@{ frames = $f.frames; over_20ms = $f.over_20ms; percent_over_20ms = $f.percent_over_20ms; over_33ms = $f.over_33ms; median_ms = $f.median_ms; p95_ms = $f.p95_ms; worst_ms = $f.worst_ms; busy_over_16ms = $f.busy_over_16ms; busiest_ms = $f.busiest_ms; rows_moved = $f.rows_moved }
      }
    }
    'tabs' {
      $switches = @()
      foreach ($command in @($ui | Where-Object { $_.message -eq 'command executed' -and $_.fields.command -eq 'tab.next' })) {
        $shown = $ui | Where-Object { $_.message -eq 'tab shown' -and $_.at -ge $command.at } | Select-Object -First 1
        # The next switch comes 1.8 s after this one.
        $listing = $ui | Where-Object { $_.message -eq 'listing shown' -and $_.at -ge $command.at -and $_.at -le $command.at.AddMilliseconds(1500) } | Select-Object -First 1
        $lists = @($ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'list_directory' -and $_.at -ge $command.at -and $_.at -le $command.at.AddMilliseconds(1500) }).Count
        $switches += [ordered]@{
          to = if ($shown) { Split-Path -Leaf $shown.fields.path } else { $null }
          tab_shown_ms = if ($shown) { Ms $command.at $shown.at } else { $null }
          listing_first_frame_ms = if ($listing) { $listing.fields.first_frame_ms } else { $null }
          # The pane kept the tab's listing (docs/ui.md, "Tabs"): the rows were bound again, the core was not asked.
          kept = if ($listing) { $listing.fields.kept } else { $null }
          list_requests = $lists
          frames = Frames $ui $command.at $command.at.AddMilliseconds(1000)
        }
      }
      $r.switches = $switches
    }
    'theme' {
      $changes = @()
      # Each theme step after the start's: from the set_value that asked for it to the later of its two lines.
      foreach ($theme in @($ui | Where-Object { $_.message -eq 'theme applied' } | Select-Object -Skip 1)) {
        $sent = $ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'set_value' -and $_.at -le $theme.at -and $_.at -ge $theme.at.AddSeconds(-2) } | Select-Object -Last 1
        if (-not $sent) { continue }
        $metrics = $ui | Where-Object { $_.message -eq 'metrics applied' -and $_.at -ge $sent.at -and $_.at -le $theme.at.AddSeconds(1) } | Select-Object -First 1
        $end = if ($metrics -and $metrics.at -gt $theme.at) { $metrics.at } else { $theme.at }
        $changes += [ordered]@{
          theme = $theme.fields.theme
          metrics_applied_ms = if ($metrics) { Ms $sent.at $metrics.at } else { $null }
          theme_applied_ms = Ms $sent.at $theme.at
          frames = Frames $ui $sent.at $end.AddMilliseconds(500)
        }
      }
      $r.changes = $changes
    }
    'picker' {
      $previews = @()
      foreach ($shown in @($ui | Where-Object { $_.message -eq 'theme previewed' })) {
        $sent = $ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'get_theme' -and $_.at -le $shown.at -and $_.at -ge $shown.at.AddSeconds(-1) } | Select-Object -Last 1
        $from = if ($sent) { $sent.at } else { $shown.at }
        $previews += [ordered]@{
          theme = $shown.fields.theme
          previewed_ms = if ($sent) { Ms $sent.at $shown.at } else { $null }
          frames = Frames $ui $from $shown.at.AddMilliseconds(500)
        }
      }
      $r.previews = $previews
    }
    'menu' {
      $opens = @()
      foreach ($shown in @($ui | Where-Object { $_.message -eq 'context menu shown' })) {
        $placed = $ui | Where-Object { $_.message -eq 'context menu placed' -and $_.at -ge $shown.at } | Select-Object -First 1
        $opens += [ordered]@{
          target = $shown.fields.target
          build_ms = $shown.fields.build_ms
          placed_ms = if ($placed) { Ms $shown.at $placed.at } else { $null }
          frames = Frames $ui $shown.at.AddMilliseconds(-$shown.fields.build_ms) $(if ($placed) { $placed.at } else { $shown.at.AddMilliseconds(500) })
        }
      }
      $r.opens = $opens
    }
    'quick-open' {
      $queries = @()
      foreach ($sent in @($ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'search' })) {
        $results = $ui | Where-Object { $_.message -eq 'quick open results' -and $_.at -ge $sent.at } | Select-Object -First 1
        $walk = $core | Where-Object { $_.request_id -eq $sent.request_id -and $_.message -like 'searched*' } | Select-Object -First 1
        $queries += [ordered]@{
          round_trip_ms = Pair $ui $sent.request_id
          core_handled_ms = Handled $core $sent.request_id
          results_on_ui_ms = if ($results) { Ms $sent.at $results.at } else { $null }
          rows = if ($results) { $results.fields.rows } else { $null }
          source = if ($results) { $results.fields.source } else { $null }
          core_line = if ($walk) { ($walk.fields | ConvertTo-Json -Compress) } else { $null }
          frames = Frames $ui $sent.at $sent.at.AddMilliseconds(500)
        }
      }
      $r.queries = $queries
    }
    'find' {
      $keys = @()
      foreach ($sent in @($ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'match_entries' })) {
        $filtered = $ui | Where-Object { $_.message -eq 'find filtered' -and $_.at -ge $sent.at } | Select-Object -First 1
        $keys += [ordered]@{
          round_trip_ms = Pair $ui $sent.request_id
          core_handled_ms = Handled $core $sent.request_id
          filtered_ms = if ($filtered) { Ms $sent.at $filtered.at } else { $null }
          query_length = if ($filtered) { $filtered.fields.query_length } else { $null }
          matches = if ($filtered) { $filtered.fields.matches } else { $null }
          frames = Frames $ui $sent.at $(if ($filtered) { $filtered.at.AddMilliseconds(100) } else { $sent.at.AddMilliseconds(300) })
        }
      }
      $closed = $ui | Where-Object { $_.message -eq 'find closed' } | Select-Object -First 1
      if ($closed) { $r.close_frames = Frames $ui $closed.at.AddMilliseconds(-100) $closed.at.AddMilliseconds(400) }
      $r.keys = $keys
    }
    'market' {
      $command = $ui | Where-Object { $_.message -eq 'command executed' -and $_.fields.command -eq 'marketplace.browse' } | Select-Object -First 1
      $shownView = $ui | Where-Object { $_.message -eq 'marketplace shown' } | Select-Object -First 1
      $refresh = $ui | Where-Object { $_.message -eq 'request sent' -and $_.fields.request -eq 'marketplace_refresh' } | Select-Object -First 1
      $cards = $ui | Where-Object { $_.message -eq 'marketplace cards shown' } | Select-Object -First 1
      # Since the cards come in parts (docs/ui.md, "The marketplace"): the last slice; a build before that has no such line.
      $complete = $ui | Where-Object { $_.message -eq 'marketplace cards complete' } | Select-Object -First 1
      $r.view_shown_ms = if ($command -and $shownView) { Ms $command.at $shownView.at } else { $null }
      $r.refresh_round_trip_ms = if ($refresh) { Pair $ui $refresh.request_id } else { $null }
      $r.refresh_core_ms = if ($refresh) { Handled $core $refresh.request_id } else { $null }
      $r.cards_shown_ms = if ($command -and $cards) { Ms $command.at $cards.at } else { $null }
      $r.cards = if ($cards) { $cards.fields.cards } else { $null }
      $r.first_make_ms = if ($cards) { $cards.fields.make_ms } else { $null }
      $r.cards_complete_ms = if ($command -and $complete) { Ms $command.at $complete.at } else { $null }
      $r.cards_complete = if ($complete) { $complete.fields.cards } else { $null }
      $r.slices = if ($complete) { $complete.fields.slices } else { $null }
      $r.make_ms = if ($complete) { $complete.fields.make_ms } else { $null }
      $last = if ($complete) { $complete } else { $cards }
      if ($command) { $r.frames = Frames $ui $command.at $(if ($last) { $last.at } else { $command.at.AddMilliseconds(1500) }) }
    }
    'idle-start' { $r.idle = $run.Idle }
    'session' { $r.idle = $run.Idle }
  }
  $r
}

$marketIndex = Build-MarketIndex
$compactOn = "theme:commander-compact;wait:1000;theme:default;wait:1000"
# Each row is brought into view first (select:), as the cursor keys bring it: a keyboard menu for a row out of view ended
# the window before d5ce4e5.
$menus = "select:IMG_0.jpg;wait:400;menu:;wait:1000;cmd:overlay.close;wait:600;select:IMG_0;wait:400;menu:;wait:1000;cmd:overlay.close;wait:600;menu:*;wait:1000;cmd:overlay.close;wait:600"
$plans = @{
  'start-small'  = @{ Steps = 'wait:2500;shot:done'; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $false }
  'start-home'   = @{ Steps = 'wait:2500;shot:done'; Config = @{ ui = @{ lastPaths = @($home3k, $small) } }; Frames = $false }
  'list-files'   = @{ Steps = "size:1400x900;pane:0;wait:1000;path:$files;wait:4000;scroll:20;wait:1500;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'list-folders' = @{ Steps = "size:1400x900;pane:0;wait:1000;path:$folders;wait:4000;scroll:20;wait:1500;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'tabs'         = @{ Steps = "size:1400x900;pane:0;path:$files;wait:3000;tab:new;path:$folders;wait:3000;tab:next;wait:1500;tab:next;wait:1500;tab:next;wait:1500;tab:next;wait:1500;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'theme'        = @{ Steps = "size:1400x900;pane:1;path:$home3k;pane:0;path:$files;wait:3000;$compactOn;$compactOn;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'picker'       = @{ Steps = "size:1400x900;pane:1;path:$home3k;pane:0;path:$files;wait:3000;cmd:preferences.selectColorTheme;wait:1500;pick:1;wait:800;pick:2;wait:800;pick:3;wait:800;pick:4;wait:800;pick:0;wait:800;cmd:overlay.close;wait:800;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'menu'         = @{ Steps = "size:1400x900;pane:0;path:$home3k;wait:2500;$menus;$menus;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'quick-open'   = @{ Steps = "size:1400x900;pane:0;path:$files;wait:3000;quick-open:dat;wait:1500;quick-open:IMG77;wait:1500;quick-open:фото9;wait:1500;cmd:overlay.close;wait:500;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true; Log = 'info,cabinetos_ui::quick_open=debug' }
  'find'         = @{ Steps = "size:1400x900;pane:0;path:$files;wait:3000;find:r;find:re;find:rep;find:repo;find:repor;wait:1000;cmd:overlay.close;wait:1000;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $true }
  'market'       = @{ Steps = "size:1400x900;wait:2000;cmd:marketplace.browse;wait:3000;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) }; marketplace = @{ index = $marketIndex } }; Frames = $true }
  'idle-start'   = @{ Steps = 'wait:2500;shot:done'; Config = @{ ui = @{ lastPaths = @($small, $small) } }; Frames = $false; Idle = $true }
  'session'      = @{ Steps = "size:1400x900;pane:0;path:$files;wait:3000;scroll:20;wait:500;tab:new;path:$folders;wait:3000;tab:next;wait:1500;tab:next;wait:1000;$compactOn;pane:1;path:$home3k;wait:1500;select:IMG_0.jpg;wait:300;menu:;wait:800;cmd:overlay.close;select:IMG_0;wait:300;menu:;wait:800;cmd:overlay.close;menu:*;wait:800;cmd:overlay.close;pane:0;tab:next;wait:1500;quick-open:dat;wait:1000;cmd:overlay.close;find:rep;wait:1000;cmd:overlay.close;cmd:marketplace.browse;wait:2000;cmd:marketplace.browse;wait:1000;shot:done"; Config = @{ ui = @{ lastPaths = @($small, $small) }; marketplace = @{ index = $marketIndex } }; Frames = $false; Idle = $true }
}

$builds = @()
if ($Variants) {
  foreach ($pair in ($Variants -split ';' | Where-Object { $_ })) {
    $name, $path = $pair -split '=', 2
    $window, $ownCore = $path -split '\|', 2
    $builds += [pscustomobject]@{ Name = $name; Exe = [System.IO.Path]::GetFullPath($window); Core = $(if ($ownCore) { [System.IO.Path]::GetFullPath($ownCore) } else { $Core }); Folder = "$Out\$name" }
  }
} else {
  $builds += [pscustomobject]@{ Name = ''; Exe = $Exe; Core = $Core; Folder = $Out }
}
$all = @()
foreach ($scenario in $Scenarios) {
  $plan = $plans[$scenario]
  if (-not $plan) { "no scenario $scenario"; continue }
  for ($n = 1; $n -le $Runs; $n++) {
    foreach ($build in $builds) {
      "{0} run {1} of {2} {3}" -f $scenario, $n, $Runs, $build.Name
      $run = Invoke-Run $build.Exe $build.Core $build.Folder $scenario $n $plan.Steps $plan.Config $plan.Frames $(if ($plan.Log) { $plan.Log } else { '' }) ([bool]$plan.Idle)
      try { $result = Analyze $run } catch { $result = [ordered]@{ scenario = $scenario; run = $n; error = "$_" } }
      $result.variant = $build.Name
      $all += $result
      ($result | ConvertTo-Json -Depth 8 -Compress)
    }
  }
}
($all | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath "$Out\results.json" -Encoding UTF8

foreach ($build in $builds) {
# Request latencies by type over every run's window log: the round trip the window measured, and the core's own time.
$trips = @{}
$handled = @{}
foreach ($file in Get-ChildItem -Path $build.Folder -Recurse -Filter 'ui.*.jsonl') {
  foreach ($line in (Read-Log $file.FullName | Where-Object { $_.message -eq 'reply received' })) {
    $type = [string]$line.fields.request
    if (-not $trips.ContainsKey($type)) { $trips[$type] = New-Object System.Collections.Generic.List[double] }
    $trips[$type].Add($line.fields.elapsed_us / 1000.0)
  }
}
foreach ($file in Get-ChildItem -Path $build.Folder -Recurse -Filter 'core.*.jsonl') {
  foreach ($line in (Read-Log $file.FullName | Where-Object { $_.message -eq 'request handled' })) {
    $type = [string]$line.fields.request
    if (-not $handled.ContainsKey($type)) { $handled[$type] = New-Object System.Collections.Generic.List[double] }
    $handled[$type].Add($line.fields.elapsed_us / 1000.0)
  }
}
$latency = foreach ($type in (@($trips.Keys) + @($handled.Keys) | Sort-Object -Unique)) {
  $t = $trips[$type]; $h = $handled[$type]
  [pscustomobject]@{
    request = $type
    count = if ($t) { $t.Count } else { 0 }
    median_ms = if ($t) { [Math]::Round((Pct $t 0.5), 2) } else { $null }
    p95_ms = if ($t) { [Math]::Round((Pct $t 0.95), 2) } else { $null }
    max_ms = if ($t) { [Math]::Round((Pct $t 1), 2) } else { $null }
    core_count = if ($h) { $h.Count } else { 0 }
    core_median_ms = if ($h) { [Math]::Round((Pct $h 0.5), 2) } else { $null }
    core_p95_ms = if ($h) { [Math]::Round((Pct $h 0.95), 2) } else { $null }
    core_max_ms = if ($h) { [Math]::Round((Pct $h 1), 2) } else { $null }
  }
}
($latency | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath "$($build.Folder)\latency.json" -Encoding UTF8
""
"request latencies {0}(window round trip / the core's own time), ms:" -f $(if ($build.Name) { "of $($build.Name) " } else { '' })
"| request | count | median | p95 | max | core count | core median | core p95 | core max |"
"|---|---|---|---|---|---|---|---|---|"
foreach ($l in $latency) { "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f $l.request, $l.count, $l.median_ms, $l.p95_ms, $l.max_ms, $l.core_count, $l.core_median_ms, $l.core_p95_ms, $l.core_max_ms }
}
""
"results: $Out\results.json and latency.json; each run's logs under $Out"
