# The creator's consent for CabinetOS windows on this PC (their rule of 2026-10-02: a test window that pops up
# over their work makes the PC unusable). Nothing that opens a CabinetOS window on this PC (the end-to-end tests,
# the live check, the speed runner, a manual start) runs before this script says the PC is free. The planning
# session asks the creator in the chat; a yes, or no answer for one minute, is consent, a deliberate no is not;
# it records the answer in the consent file, and every window run calls this script first (CLAUDE.md, "Working
# rules"). The laptop and the VM need no consent and do not use this script.
#
# The consent file: %LOCALAPPDATA%\CabinetOS-dev\window-runs.txt, outside every checkout, so a worktree finds the
# same file. Its first line is "allowed" or "held", an optional "until yyyy-MM-dd HH:mm" (local time; "held until"
# turns into allowed at that time, "allowed until" into held), and a free note. A missing file means held.
#
#   wait-for-pc.ps1                       waits (20 s between looks, -Minutes at most, 30 by default) until the file
#                                         says allowed and no CabinetOS.exe runs; exit 0 when free, 1 after the wait
#   wait-for-pc.ps1 -Status               prints the state and exits 0
#   wait-for-pc.ps1 -Set held -Until "2026-10-02 00:55" -Note "the creator works at the PC"
#   wait-for-pc.ps1 -Set allowed -Note "silent consent, asked 00:55"
#
# Runs in Windows PowerShell 5.1 and PowerShell 7.
param(
  [int]$Minutes = 30,
  [switch]$Status,
  [ValidateSet('', 'allowed', 'held')][string]$Set = '',
  [string]$Until = '',
  [string]$Note = ''
)
$ErrorActionPreference = 'Stop'
$file = Join-Path $env:LOCALAPPDATA 'CabinetOS-dev\window-runs.txt'

function Line {
  if (Test-Path -LiteralPath $file) { (Get-Content -LiteralPath $file -TotalCount 1) } else { '' }
}

# The state now: "allowed" or "held", from the first line and its "until" time.
function State {
  $line = Line
  if (-not $line) { return 'held (no consent file)' }
  $state = if ($line -match '^\s*allowed') { 'allowed' } elseif ($line -match '^\s*held') { 'held' } else { 'held (unreadable line)' }
  if ($line -match 'until\s+(\d{4}-\d\d-\d\d \d\d:\d\d)') {
    $at = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm', $null)
    if ((Get-Date) -ge $at) { $state = if ($state -eq 'allowed') { 'held' } else { 'allowed' } }
    $state += " (the file says '$line')"
  } elseif ($line.Length -gt $state.Length) {
    $state += " ('$line')"
  }
  $state
}

function Busy { [bool](Get-Process -Name CabinetOS -ErrorAction SilentlyContinue) }

if ($Set) {
  if ($Until -and $Until -notmatch '^\d{4}-\d\d-\d\d \d\d:\d\d$') { throw "-Until wants 'yyyy-MM-dd HH:mm', not '$Until'" }
  New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null
  $line = $Set
  if ($Until) { $line += " until $Until" }
  if ($Note) { $line += " $Note" }
  Set-Content -LiteralPath $file -Value @($line, "written $(Get-Date -Format 'yyyy-MM-dd HH:mm')") -Encoding UTF8
  "window runs on this PC: $(State)"
  exit 0
}
if ($Status) {
  "window runs on this PC: $(State); CabinetOS.exe running: $(Busy)"
  exit 0
}
$deadline = (Get-Date).AddMinutes($Minutes)
while ($true) {
  $state = State
  $busy = Busy
  if ($state -like 'allowed*' -and -not $busy) { "pc free: $state"; exit 0 }
  if ((Get-Date) -ge $deadline) {
    "pc not free after $Minutes minutes: $state; CabinetOS.exe running: $busy"
    exit 1
  }
  Start-Sleep -Seconds 20
}
