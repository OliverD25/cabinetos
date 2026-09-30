# Measures the scroll of the 100,000-entry folder with real keys (docs/ui.md, "Scrolling"): a fresh
# window on the folder, PageDown pressed 30 times a second for -Seconds with SendInput (as the live
# check presses keys), and the frame table of those seconds from the window's frame stats
# (CABINETOS_UI_FRAMESTATS=1), read the way livecheck.ps1 reads them. It adds what the window cannot
# see itself: the CPU time of each thread of the window's process (with Windows' thread names), the
# desktop window manager's CPU and the GPU's use during the hold. It lists each frame of 33 ms or more
# from the first press to 1 s after the last, with the garbage collector's pause in it; the table
# leaves out up to the first second of the hold, as the live check's does. scroll-bench.ps1 presses
# no key and gives the UI thread's work; only real keys give the true gaps between frames.
#
# It takes the keyboard and the mouse for about 15 s: run it on an unlocked, awake screen that nobody
# uses. It stops when the screen is locked or another window is in front. The folder is the one
# `cargo bench -p cabinetos-fs --bench list_directory` makes in %TEMP%\cabinetos-bench. Environment
# variables the caller sets (DOTNET_* for the runtime, say) reach the window and are printed. Runs in
# Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\scroll-keys.ps1 -Label "after" -Fine
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Folder = "$env:TEMP\cabinetos-bench\100000",
  [string]$Label = "run",
  # Where the mouse rests during the hold: a corner of the screen, over the list, or over the title bar.
  [ValidateSet('corner', 'list', 'title')][string]$Mouse = 'corner',
  # updown: a press and a release every -Rate ms, as the live check does; repeat: key-down messages
  # only, as a held key's repeats arrive.
  [ValidateSet('updown', 'repeat')][string]$Mode = 'updown',
  [int]$Seconds = 5,
  [int]$Rate = 33,
  # The theme for the run, for example commander-compact (20 px rows, more rows per page).
  [string]$Theme = '',
  # Seconds of PageDown before the measured hold, then Home.
  [int]$Warmup = 0,
  # Sleeps between presses at 1 ms timer resolution, so 33 ms is 33 ms and not the default clock's 31 or 47.
  [switch]$Fine,
  # Heavy logging on (CABINETOS_LOG_HEAVY=1).
  [switch]$Heavy,
  # A screenshot of the window after the hold.
  [string]$Shot = '',
  # A file the report is added to.
  [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
foreach ($needed in $Exe, $Core, $Folder) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing"; exit 1 } }
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ScrollKeys {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint data; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr h, out IntPtr text);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
  [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
  public static string ThreadName(int id) {
    var h = OpenThread(0x0800, false, (uint)id);
    if (h == IntPtr.Zero) return "";
    try { IntPtr text; if (GetThreadDescription(h, out text) < 0 || text == IntPtr.Zero) return ""; var s = Marshal.PtrToStringUni(text); LocalFree(text); return s ?? ""; }
    finally { CloseHandle(h); }
  }
  // The cursor block's keys are extended keys, as a real keyboard sends them.
  static INPUT Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.dwFlags = (up ? 2u : 0u) | ((vk >= 0x21 && vk <= 0x28) ? 1u : 0u); return i; }
  static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
  public static void Press(ushort vk) { Send(Key(vk, false)); Send(Key(vk, true)); }
  public static void Down(ushort vk) { Send(Key(vk, false)); }
  public static void Up(ushort vk) { Send(Key(vk, true)); }
  // A tap of Alt lets this process bring another window to the front.
  public static void Front(IntPtr h) { Send(Key(0x12, false), Key(0x12, true)); ShowWindow(h, 9); SetForegroundWindow(h); }
}
"@
[void][ScrollKeys]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }

$root = "$env:TEMP\cabinetos-ui-test\scroll-keys"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\config", "$root\logs" | Out-Null
$config = @{ version = 1 }
if ($Theme) { $config.ui = @{ theme = $Theme } }
[System.IO.File]::WriteAllText("$root\config\cabinetos.json", ($config | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_UI_FRAMESTATS = "1"
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
$env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
if ($Heavy) { $env:CABINETOS_LOG_HEAVY = '1' } else { Remove-Item Env:CABINETOS_LOG_HEAVY -ErrorAction SilentlyContinue }
Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue
$runtime = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'DOTNET_*' } | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
if ($Fine) { [void][ScrollKeys]::timeBeginPeriod(1) }

$p = Start-Process -FilePath $Exe -ArgumentList '--path', "`"$Folder`"" -PassThru
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
[ScrollKeys]::Front($h); Start-Sleep -Milliseconds 800
$r = New-Object ScrollKeys+RECT; [void][ScrollKeys]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
switch ($Mouse) {
  'corner' { [void][ScrollKeys]::SetCursorPos(2, 2) }
  'list' { [void][ScrollKeys]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) * 0.33), [int]($r.Top + ($r.Bottom - $r.Top) * 0.5)) }
  'title' { [void][ScrollKeys]::SetCursorPos([int]($r.Left + ($r.Right - $r.Left) * 0.5), [int]($r.Top + 12)) }
}
Start-Sleep -Milliseconds 1500
if ([ScrollKeys]::ForegroundPid() -ne [uint32]$p.Id) { $front = Get-Process -Id ([ScrollKeys]::ForegroundPid()) -ErrorAction SilentlyContinue; "STOP: CabinetOS is not in front ($($front.ProcessName) '$($front.MainWindowTitle)')"; [void]$p.CloseMainWindow(); exit 1 }
if ($Warmup -gt 0) {
  $w = (Get-Date).AddSeconds($Warmup)
  while ((Get-Date) -lt $w) { [ScrollKeys]::Press(0x22); Start-Sleep -Milliseconds $Rate }
  Start-Sleep -Milliseconds 500; [ScrollKeys]::Press(0x24); Start-Sleep -Seconds 2
}

# CPU per thread of the window's process, and the desktop window manager's and the GPU's use, over the hold.
function ThreadTimes { $t = @{}; foreach ($th in (Get-Process -Id $p.Id).Threads) { try { $t[$th.Id] = $th.TotalProcessorTime.TotalMilliseconds } catch { } }; $t }
$job = Start-Job -ArgumentList $p.Id, ($Seconds + 1) { param($id, $n) Start-Sleep -Milliseconds 300
  Get-Counter '\Process(dwm)\% Processor Time', '\GPU Engine(*engtype_3D)\Utilization Percentage' -SampleInterval 1 -MaxSamples $n -ErrorAction SilentlyContinue |
    ForEach-Object { $s = $_.CounterSamples
      [pscustomobject]@{ Dwm = ($s | Where-Object { $_.Path -like '*process(dwm)*' } | Measure-Object CookedValue -Sum).Sum
        GpuMine = ($s | Where-Object { $_.InstanceName -like "pid_$($id)_*" } | Measure-Object CookedValue -Sum).Sum
        GpuAll = ($s | Where-Object { $_.Path -like '*gpu engine*' } | Measure-Object CookedValue -Sum).Sum } } }
Start-Sleep -Milliseconds 1200
$before = ThreadTimes
$holdStart = (Get-Date).ToUniversalTime()
$stop = (Get-Date).AddSeconds($Seconds)
$lost = 0
$presses = 0
if ($Mode -eq 'updown') {
  while ((Get-Date) -lt $stop) { if ([ScrollKeys]::ForegroundPid() -ne [uint32]$p.Id) { $lost++ }; [ScrollKeys]::Press(0x22); $presses++; Start-Sleep -Milliseconds $Rate }
} else {
  while ((Get-Date) -lt $stop) { if ([ScrollKeys]::ForegroundPid() -ne [uint32]$p.Id) { $lost++ }; [ScrollKeys]::Down(0x22); $presses++; Start-Sleep -Milliseconds $Rate }
  [ScrollKeys]::Up(0x22)
}
$holdEnd = (Get-Date).ToUniversalTime()
$after = ThreadTimes
if ($Shot) {
  Add-Type -AssemblyName System.Drawing
  Start-Sleep -Milliseconds 800
  $rr = New-Object ScrollKeys+RECT; [void][ScrollKeys]::DwmGetWindowAttribute($h, 9, [ref]$rr, 16)
  $bmp = New-Object System.Drawing.Bitmap ($rr.Right - $rr.Left), ($rr.Bottom - $rr.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($rr.Left, $rr.Top, 0, 0, $bmp.Size)
  $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}
$counters = @(Receive-Job -Job $job -Wait -AutoRemoveJob)
Start-Sleep -Milliseconds 1500
$threads = foreach ($id in $after.Keys) { $d = $after[$id] - $(if ($before.ContainsKey($id)) { $before[$id] } else { 0 }); if ($d -gt 20) { [pscustomobject]@{ Id = $id; Name = [ScrollKeys]::ThreadName($id); Ms = $d } } }
$threads = @($threads | Sort-Object Ms -Descending)
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000)

function TsUtc($ts) {
  if ($ts -is [datetime]) { return $ts.ToUniversalTime() }
  return [datetime]::Parse([string]$ts, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]'AdjustToUniversal, AssumeUniversal')
}
$log = @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json })
$table = @($log | Where-Object { $_.message -eq 'frame stats' -and (TsUtc $_.ts) -gt $holdStart.AddSeconds(1) -and (TsUtc $_.ts) -le $holdEnd.AddSeconds(1) })
$sum = { param($name) ($table | ForEach-Object { $_.fields.$name } | Measure-Object -Sum).Sum }
$frames = & $sum 'frames'
$over20 = & $sum 'gaps_over_20ms'
$over33 = & $sum 'gaps_over_33ms'
$busy16 = & $sum 'busy_over_16ms'
$worst = ($table | ForEach-Object { $_.fields.worst_ms } | Measure-Object -Maximum).Maximum
$n = [Math]::Max(1, $table.Count)
$share = if ($frames -gt 0) { 100.0 * $over20 / $frames } else { 100 }
$held = [Math]::Round(($holdEnd - $holdStart).TotalSeconds, 1)
$report = @(
  ""
  "## $Label ($Mode every $Rate ms$(if ($Fine) { ' at 1 ms timer resolution' }), $presses presses in $held s, mouse $Mouse$(if ($Theme) { ", theme $Theme" })$(if ($Warmup) { ", after a $Warmup s warm-up" })$(if ($Heavy) { ", heavy logging on" })$(if ($runtime) { "; $runtime" }))"
  "| second (UTC) | frames | worst | over 20 ms | over 33 ms | UI work over 16.7 ms | UI work | WinUI frames | rows' measure | bind | GC pause | GCs 0/1/2 |"
  "|---|---|---|---|---|---|---|---|---|---|---|---|"
) + @($table | ForEach-Object { "| {0:HH:mm:ss} | {1} | {2} ms | {3} | {4} | {5} | {6} ms | {7} ms | {8} ms | {9} ms | {10} ms | {11} |" -f (TsUtc $_.ts), $_.fields.frames, $_.fields.worst_ms, $_.fields.gaps_over_20ms, $_.fields.gaps_over_33ms, $_.fields.busy_over_16ms, $_.fields.busy_ms, $_.fields.work_ms, $_.fields.measure_ms, $_.fields.bind_ms, $_.fields.gc_pause_ms, $_.fields.gcs }) + @(
  "summary: {0} frames in {1} s; over 20 ms {2} ({3:N1} %); over 33 ms {4}; worst {5} ms; UI work over 16.7 ms {6}; per second: UI work {7:N0} ms, WinUI frames {8:N0} ms, rows' measure {9:N0} ms; presses lost to another window: {10}" -f $frames, $table.Count, $over20, $share, $over33, $worst, $busy16, ((& $sum 'busy_ms') / $n), ((& $sum 'work_ms') / $n), ((& $sum 'measure_ms') / $n), $lost
  "CPU per thread of the window's process over the {0} s hold (ms per second): {1}" -f $held, (($threads | Select-Object -First 8 | ForEach-Object { "{0} {1:N0}" -f $(if ($_.Name) { $_.Name } else { "tid $($_.Id)" }), ($_.Ms / $held) }) -join '; ')
  "DWM CPU {0:N1} % of one processor; GPU 3D engine: this window {1:N1} %, all processes {2:N1} %" -f ($counters | Measure-Object Dwm -Average).Average, ($counters | Measure-Object GpuMine -Average).Average, ($counters | Measure-Object GpuAll -Average).Average
)
$slow = @($log | Where-Object { $_.message -eq 'slow frame' -and (TsUtc $_.ts) -gt $holdStart -and (TsUtc $_.ts) -le $holdEnd.AddSeconds(1) })
foreach ($s in $slow) {
  $at = (TsUtc $s.ts)
  $when = if ($at -gt $holdEnd) { "{0:N0} ms after the last press" -f ($at - $holdEnd).TotalMilliseconds } else { "{0:N1} s into the hold" -f ($at - $holdStart).TotalSeconds }
  $report += "slow frame, {0}: gap {1} ms, UI work {2} ms (WinUI's frame {3} ms; measure {4}, rows' own {5}, bind {6}), GC pause {7} ms ({8})" -f $when, $s.fields.gap_ms, $s.fields.busy_ms, $s.fields.work_ms, $s.fields.measure_ms, $s.fields.row_measure_ms, $s.fields.bind_ms, $s.fields.gc_pause_ms, $s.fields.gcs
}
$report
if ($Out) { $report | Out-File -LiteralPath $Out -Append -Encoding UTF8 }
