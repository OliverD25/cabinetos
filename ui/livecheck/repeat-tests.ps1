# Runs some of the window's end-to-end tests again and again, beside a CPU load, on the machine it runs on, and says for
# each run whether it passed. It is for a test that fails only on a busy machine: one run proves nothing, five runs
# beside a load do. It builds nothing: the build under ui\CabinetOS.Tests\bin must be of the commit under test
# (remote-tests.ps1 builds it; run that first with the same filter). The load is -Burn busy processes. Each loops at
# -BurnPriority (Normal, AboveNormal or High) until -BurnMinutes pass or this script ends. A thread that wakes after a
# wait gets a boost over busy threads of its own priority, so Normal busy processes slow a window less than a full test
# suite does; AboveNormal ones starve it the way a suite of windows does (32 of them on 16 logical processors starved
# everything, this script too: one run took 28 minutes). -Cover puts a black full-screen window in front of everything
# for the whole script: the test windows are then hidden behind it, as when someone works at the machine, and Windows
# stops drawing a window nobody sees. The exit code is the number of runs that did not pass.
#
# On the Omen laptop, through remote-script.ps1 (the windows need its desktop):
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-script.ps1 -Script ui\livecheck\repeat-tests.ps1 -Args '-Filter "FullyQualifiedName~The_top_row_fits" -Times 5 -Burn 24 -BurnPriority AboveNormal' -Branch <branch>
param(
  [Parameter(Mandatory = $true)][string]$Filter,
  [int]$Times = 5,
  [int]$Burn = 0,
  [ValidateSet('Normal', 'AboveNormal', 'High')][string]$BurnPriority = 'Normal',
  [int]$BurnMinutes = 30,
  [switch]$Cover
)
$ErrorActionPreference = 'Continue'
$ui = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $ui -Parent
$env:CABINETOS_UI_E2E = '1'
$core = Join-Path $repo 'core\target\release\cabinetos-core.exe'
if (Test-Path -LiteralPath $core) { $env:CABINETOS_CORE_EXE = $core }
$burners = @()
$coverWindow = $null
$failedRuns = 0
try {
  if ($Cover) {
    $form = 'Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; $f.FormBorderStyle = "None"; $f.WindowState = "Maximized"; $f.TopMost = $true; $f.BackColor = "Black"; $f.ShowInTaskbar = $false; $t = New-Object System.Windows.Forms.Timer; $t.Interval = ' + ($BurnMinutes * 60000) + '; $t.Add_Tick({ $f.Close() }); $t.Start(); [void]$f.ShowDialog()'
    $coverWindow = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($form))
    "cover: a full-screen window in front of the others"
    Start-Sleep -Seconds 3
  }
  $busy = "`$end = (Get-Date).AddMinutes($BurnMinutes); while ((Get-Date) -lt `$end) { }"
  for ($i = 0; $i -lt $Burn; $i++) {
    $burner = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command', $busy
    $burner.PriorityClass = $BurnPriority
    $burners += $burner
  }
  if ($burners.Count -gt 0) { "load: $($burners.Count) busy processes at $BurnPriority priority on $env:NUMBER_OF_PROCESSORS logical processors"; Start-Sleep -Seconds 5 }
  "filter: $Filter, $Times runs, at $(git -C $repo rev-parse --short HEAD)"
  Push-Location $ui
  for ($run = 1; $run -le $Times; $run++) {
    $started = Get-Date
    $lines = @(& dotnet test --solution CabinetOS.sln --no-build --filter $Filter 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $seconds = [int]((Get-Date) - $started).TotalSeconds
    $counts = @($lines | Where-Object { $_ -match '^\s*(total|failed|succeeded|skipped):' } | ForEach-Object { $_.Trim() }) -join ' '
    # A run that matched no test, or skipped them all (the opt-in off), proves nothing: it counts as not passed.
    $ran = ($counts -match 'succeeded: [1-9]') -or ($counts -match 'failed: [1-9]')
    if ($code -eq 0 -and $ran) {
      "run $run of ${Times}: passed in $seconds s ($counts)"
    } else {
      $failedRuns++
      "run $run of ${Times}: NOT PASSED in $seconds s, exit code $code ($counts)"
      $from = -1
      for ($n = 0; $n -lt $lines.Count; $n++) { if ($lines[$n] -match '^\s*failed ') { $from = $n; break } }
      if ($from -ge 0) { $shown = $lines[$from..([Math]::Min($lines.Count - 1, $from + 60))] } else { $shown = $lines | Select-Object -Last 30 }
      foreach ($line in $shown) { '    ' + $(if ($line.Length -gt 600) { $line.Substring(0, 600) + '...' } else { $line }) }
    }
  }
  Pop-Location
  "summary: $($Times - $failedRuns) of $Times runs passed"
} finally {
  foreach ($burner in $burners) { try { $burner.Kill() } catch { } }
  if ($coverWindow) { try { $coverWindow.Kill() } catch { } }
}
exit $failedRuns
