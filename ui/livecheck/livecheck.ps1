# The shell's live check with real keys (docs/ui.md, "The live check"). It starts the window
# with a configuration, logs and themes of its own, sends real key presses and mouse clicks
# with SendInput, and takes screenshots. Run it only on an unlocked screen you are watching:
# it stops the moment another window comes to the front, so no key reaches another program.
# Leaves two files in the Recycle Bin (cabinetos-live-check-delete-me.txt, the Delete check, and
# cabinetos-live-check-f8.txt, the F8 check), the edge-case fixture in
# %TEMP%\cabinetos-edge-live (sdk\fixtures\edge-fixture.ps1), and a file of its own on the clipboard
# (section 18 runs Windows' Copy and reads the clipboard back). F4 edits with a stand-in editor the
# run writes itself (files.editor: wscript.exe and a script that notes the file): never Notepad.
# Needs the release builds of the window and the core, and the 100,000-entry folder that
# `cargo bench -p cabinetos-fs --bench list_directory` makes in %TEMP%\cabinetos-bench.
# It prints the frame table of the 5 s PageDown in that folder and the line "scroll goal (no frame
# over 33 ms, under 5 % over 20 ms) met: yes|no", with the machine's CPU load during those seconds.
# With -Strict it exits 1 when the goal was not met (off by default: the numbers depend on the
# machine being quiet). docs/ui.md, "Scrolling".
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$ShotDir = "$env:TEMP\cabinetos-ui-test\live-shots",
  [string]$Run = "live",
  [string]$Tools = "$PSScriptRoot\..\..\sdk\tools",
  [switch]$Strict
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
foreach ($needed in $Exe, $Core) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing: build it first (docs/ui.md, 'The live check')"; exit 1 } }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class Live {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint data; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  // The keys of the cursor block (Page Up to Down, Insert, Delete) are sent as the extended keys they are: without the flag
  // they are the numeric keypad's, and with Num Lock on Windows takes Shift away from Shift+Down (and drops it from the key's message).
  static bool Extended(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }
  static INPUT Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.dwFlags = (up ? 2u : 0u) | (Extended(vk) ? 1u : 0u); return i; }
  static INPUT Unicode(char c, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wScan = c; i.u.ki.dwFlags = 4u | (up ? 2u : 0u); return i; }
  static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
  public static void Press(params ushort[] vks) {
    foreach (var vk in vks) Send(Key(vk, false));
    for (int i = vks.Length - 1; i >= 0; i--) Send(Key(vks[i], true));
  }
  public static void Type(string text) { foreach (var c in text) { Send(Unicode(c, false), Unicode(c, true)); Thread.Sleep(15); } }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); Thread.Sleep(120);
    var down = new INPUT { type = 0 }; down.u.mi.dwFlags = 0x0002;
    var up = new INPUT { type = 0 }; up.u.mi.dwFlags = 0x0004;
    Send(down); Thread.Sleep(40); Send(up);
  }
  // A right-click; with shift, Shift is held until the button is up, as a hand holds it for Windows' own menu.
  public static void RightClick(int x, int y, bool shift) {
    SetCursorPos(x, y); Thread.Sleep(120);
    if (shift) { Send(Key(0x10, false)); Thread.Sleep(40); }
    var down = new INPUT { type = 0 }; down.u.mi.dwFlags = 0x0008;
    var up = new INPUT { type = 0 }; up.u.mi.dwFlags = 0x0010;
    Send(down); Thread.Sleep(40); Send(up);
    if (shift) { Thread.Sleep(40); Send(Key(0x10, true)); }
  }
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
  // A real mouse move (an absolute move over the whole desktop, as a mouse's own report would be): SetCursorPos alone
  // moves the pointer without the pointer messages a drag needs to start.
  public static void MoveTo(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var move = new INPUT { type = 0 };
    move.u.mi.dx = (int)((long)(x - vx) * 65535 / (vw - 1));
    move.u.mi.dy = (int)((long)(y - vy) * 65535 / (vh - 1));
    move.u.mi.dwFlags = 0x0001 | 0x8000 | 0x4000;
    Send(move);
  }
  // Presses at (x1,y1), moves to (x2,y2) in ten steps, and lets go: a drag the way a hand makes one.
  public static void Drag(int x1, int y1, int x2, int y2) {
    MoveTo(x1, y1); Thread.Sleep(200);
    var down = new INPUT { type = 0 }; down.u.mi.dwFlags = 0x0002;
    var up = new INPUT { type = 0 }; up.u.mi.dwFlags = 0x0004;
    Send(down); Thread.Sleep(150);
    for (int i = 1; i <= 10; i++) { MoveTo(x1 + (x2 - x1) * i / 10, y1 + (y2 - y1) * i / 10); Thread.Sleep(60); }
    Thread.Sleep(300);
    Send(up);
  }
  public static void Front(IntPtr h) { Send(Key(0x12, false), Key(0x12, true)); ShowWindow(h, 9); SetForegroundWindow(h); }
  [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  // Busy and all processor time of the machine so far, over all logical processors (kernel time includes idle).
  public static long[] CpuTimes() { long idle, kernel, user; GetSystemTimes(out idle, out kernel, out user); return new long[] { kernel + user - idle, kernel + user }; }
}
"@
[void][Live]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$VK = @{ Ctrl = 0x11; Shift = 0x10; Alt = 0x12; P = 0x50; D = 0x44; B = 0x42; L = 0x4C; Esc = 0x1B; Tab = 0x09; Enter = 0x0D; Back = 0x08; Down = 0x28; PgDn = 0x22; F2 = 0x71;
  F5 = 0x74; F7 = 0x76; F10 = 0x79; Delete = 0x2E; Home = 0x24; Backquote = 0xC0; F = 0x46; K = 0x4B; V = 0x56;
  F1 = 0x70; F3 = 0x72; F4 = 0x73; F8 = 0x77; Space = 0x20; U = 0x55; Backslash = 0xDC; NumAdd = 0x6B; NumSubtract = 0x6D; NumMultiply = 0x6A;
  T = 0x54; Up = 0x26; End = 0x23; W = 0x57; X = 0x58; A = 0x41; E = 0x45; Left = 0x25; Right = 0x27 }
function Step($text) {
  # Keys must never reach another program: stop the run if the window lost the front.
  # A flyout (the drive list) is a window of its own, so the test is the process, not the window.
  if ($script:h -and [Live]::ForegroundPid() -ne [uint32]$script:p.Id) {
    $front = Get-Process -Id ([Live]::ForegroundPid()) -ErrorAction SilentlyContinue
    "{0:HH:mm:ss.fff} STOP: CabinetOS is not the foreground window before '{1}' (in front: {2} pid {3}, '{4}')" -f (Get-Date), $text, $front.ProcessName, $front.Id, $front.MainWindowTitle
    if ($script:p -and -not $script:p.HasExited) { [void]$script:p.CloseMainWindow(); [void]$script:p.WaitForExit(8000) }
    exit 1
  }
  "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
}
# A log line's timestamp as UTC. PowerShell 7 reads it from JSON as a date; Windows PowerShell 5.1 leaves the string.
function TsUtc($ts) {
  if ($ts -is [datetime]) { return $ts.ToUniversalTime() }
  return [datetime]::Parse([string]$ts, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]'AdjustToUniversal, AssumeUniversal')
}
# A key a dialog holds reaches no command: no "command executed" line of the window's log lies
# between a "dialog shown" and its "dialog closed" (the dialog takes the key; nothing logs it there).
function NothingRanUnderDialog($lines) {
  $open = $false; $ran = 0
  foreach ($line in $lines) {
    if ($line -match '"dialog shown"') { $open = $true }
    elseif ($line -match '"dialog closed"') { $open = $false }
    elseif ($open -and $line -match '"command executed"') { $ran++ }
  }
  $ran -eq 0
}
function Shot([IntPtr]$h, [string]$path) {
  $r = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Step "screenshot $path"
}
# What the status bar says about the selection ("3 selected, 1.4 MB"), as UI Automation reads it.
# Presses a key that opens the name box and types only once the window says the box is shown
# ("rename box shown" in its log), or after 5 s: the box takes the keyboard a moment after the key.
function PressForNameBox([scriptblock]$press) {
  $count = @(Get-Content "$root\logs\ui.*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ -match '"rename box shown"' }).Count
  & $press
  $deadline = (Get-Date).AddSeconds(5)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
    if (@(Get-Content "$root\logs\ui.*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ -match '"rename box shown"' }).Count -gt $count) { Start-Sleep -Milliseconds 300; return }
  }
  "the name box did not report itself within 5 s"
}

# The status bar's selection text, as the window logged it last ("selection shown", written when it
# changes): a search of the automation tree came back empty once web pages were in the window.
# Gives the keyboard to the left pane's list the way a user would, with a click into it (a third of
# the window's width in, just under the middle): the ground truth when a focus hand-over went wrong.
function ClickLeftPane {
  $rect = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($script:h, 9, [ref]$rect, 16)
  [Live]::Click([int]($rect.Left + ($rect.Right - $rect.Left) * 0.33), [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.45))
  Start-Sleep -Milliseconds 400
}

function SelectionText {
  $line = Get-Content "$root\logs\ui.*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ -match '"selection shown"' } | Select-Object -Last 1
  if (-not $line) { return "(the window has not reported a selection yet)" }
  $text = ($line | ConvertFrom-Json).fields.text
  if ($text -eq '') { return "(nothing selected)" }
  return $text
}

$root = "$env:TEMP\cabinetos-ui-test\$Run"
# A fresh configuration every run: saved last folders must not change where Enter lands.
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\config", "$root\logs", $ShotDir | Out-Null
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
# F4 (file.edit) opens files.editor: a stand-in that notes each file in stub-editor.js.log and
# shows no window. The core adds the file's path as the last argument.
$stub = "$root\stub-editor.js"
Set-Content -LiteralPath $stub -Encoding ASCII -Value @'
var fso = new ActiveXObject("Scripting.FileSystemObject");
var log = fso.OpenTextFile(WScript.ScriptFullName + ".log", 8, true, -1);
log.WriteLine(WScript.Arguments.length > 0 ? WScript.Arguments(0) : "(no file)");
log.Close();
'@
# Without a byte order mark (Windows PowerShell's UTF8 writes one): the core reads plain JSON.
# A marketplace index of this run's own (built here, nothing uploaded, nothing fetched): the fixture plugins and the shipped
# themes, and the Agent extension when the repository has it built (sdk\extensions\build-extensions.ps1).
$indexScript = "$PSScriptRoot\..\..\sdk\marketplace\build-index.ps1"
$agentPlugin = "$PSScriptRoot\..\..\sdk\extensions\agent\plugin\plugin.wasm"
$hasExtensions = (Test-Path -LiteralPath $indexScript) -and ((Get-Content -LiteralPath $indexScript -Raw) -match '\[switch\]\s*\$Extensions') -and (Test-Path -LiteralPath $agentPlugin)
$indexDir = "$root\index"
$indexArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $indexScript, '-OutDir', $indexDir)
if ($hasExtensions) { $indexArgs += '-Extensions' }
& powershell.exe @indexArgs | ForEach-Object { "index: $_" }
$editorJson = @{ version = 1; files = @{ editor = @{ command = "wscript.exe"; args = [string[]]@("//B", "//Nologo", $stub) } }; marketplace = @{ index = $indexDir } } | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($env:CABINETOS_CONFIG, $editorJson, (New-Object System.Text.UTF8Encoding $false))
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_UI_FRAMESTATS = "1"
$env:CABINETOS_CORE_EXE = $Core
# The core writes its themes when missing: into this run's folder, not the real app data.
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
# WebView2's user data (the terminal, the tools) into this run's folder too, not the real app data.
$env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
# The plugins, their data and the marketplace's own files too: section 14 installs the Agent through the marketplace.
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
# Tool Extensions from the repository (Markdown Preview), as --tools-dir would give them, and Quick Notes: a test
# tool with a sidebar page (fixtures\quick-notes), which section 13 needs and no other section notices. A copy in
# this run's folder, so the repository's tools folder stays as it is, and the sidebar's buttons are these two tools'.
$runTools = "$root\tools"
New-Item -ItemType Directory -Force $runTools | Out-Null
Copy-Item -LiteralPath (Join-Path ([System.IO.Path]::GetFullPath($Tools)) 'markdown-preview') -Destination $runTools -Recurse
Copy-Item -LiteralPath "$PSScriptRoot\fixtures\quick-notes" -Destination $runTools -Recurse
$env:CABINETOS_TOOLS_DIR = $runTools
"tools dir: $env:CABINETOS_TOOLS_DIR (exists: $(Test-Path -LiteralPath $env:CABINETOS_TOOLS_DIR))"
Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue

if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }
Step "start"
$p = Start-Process -FilePath $Exe -PassThru
"app pid: $($p.Id)"
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
[Live]::Front($h); Start-Sleep -Milliseconds 800
"foreground ok: $([Live]::GetForegroundWindow() -eq $h)"
Step "the window is in front"
$scale = [Live]::GetDpiForWindow($h) / 96.0
Shot $h "$ShotDir\phase-5-window.png"

Step "palette, type dual"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("dual"); Start-Sleep -Milliseconds 900
Shot $h "$ShotDir\phase-5-palette.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 400

Step "single pane, then dual again"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.D); Start-Sleep -Milliseconds 700
Shot $h "$ShotDir\single.png"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.D); Start-Sleep -Milliseconds 500

Step "tab to the other pane and back"
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400

Step "enter the selected folder, then backspace"
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Back); Start-Sleep -Milliseconds 900

Step "rebind View: Toggle Sidebar through the pencil"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("toggle sidebar"); Start-Sleep -Milliseconds 900
$uiaRoot = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Change keybinding")
$pencil = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($pencil) {
  $pr = $pencil.Current.BoundingRectangle
  $pencilX = [int]($pr.X + $pr.Width / 2); $pencilY = [int]($pr.Y + $pr.Height / 2)
  Step "pencil found by UI Automation"
} else {
  $r = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
  $panelWidth = [Math]::Min(640, ($r.Right - $r.Left) / $scale - 32)
  $pencilX = [int]($r.Left + ((($r.Right - $r.Left) / $scale / 2) + ($panelWidth / 2) - 6 - 10 - 12) * $scale)
  $pencilY = [int]($r.Top + (64 + 52 + 18) * $scale)
  Step "pencil not found by UI Automation; using layout coordinates"
}
Step "click pencil at $pencilX,$pencilY (scale $scale)"
[Live]::Click($pencilX, $pencilY); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.B); Start-Sleep -Milliseconds 1600
Shot $h "$ShotDir\rebound.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 1200
# The mouse still rests where the pencil was: its tooltip must be gone with the palette.
Shot $h "$ShotDir\palette-closed-under-the-mouse.png"

Step "go to the bench folder"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$env:TEMP\cabinetos-bench"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 200
Step "enter 100000"
[Live]::Press($VK.Enter); Start-Sleep -Seconds 2
Step "pagedown held for 5 s"
$others = @{}
foreach ($proc in Get-Process) { try { $others[$proc.Id] = @($proc.ProcessName, $proc.TotalProcessorTime.Ticks) } catch { } }
$cpuBefore = [Live]::CpuTimes()
$holdStart = (Get-Date).ToUniversalTime()
$stop = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $stop) {
  if ([Live]::ForegroundPid() -ne [uint32]$p.Id) { Step "pagedown interrupted" }
  [Live]::Press($VK.PgDn); Start-Sleep -Milliseconds 33
}
$holdEnd = (Get-Date).ToUniversalTime()
$cpuAfter = [Live]::CpuTimes()
$allTicks = [double]($cpuAfter[1] - $cpuBefore[1])
$loads = foreach ($proc in Get-Process) { try { if ($others.ContainsKey($proc.Id)) { [pscustomobject]@{ Name = $proc.ProcessName; Id = $proc.Id; Percent = 100 * ($proc.TotalProcessorTime.Ticks - $others[$proc.Id][1]) / $allTicks } } } catch { } }
Step "pagedown done"
Start-Sleep -Milliseconds 1500
Shot $h "$ShotDir\scrolled.png"
# The frame table of the hold: the window's per-second lines that ended while the key was held.
$seconds = @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object { $_.message -eq 'frame stats' -and (TsUtc $_.ts) -gt $holdStart.AddSeconds(1) -and (TsUtc $_.ts) -le $holdEnd.AddSeconds(1) })
"| second (UTC) | frames | worst | over 20 ms | over 33 ms | UI work over 16.7 ms | UI work | rows' measure |"
"|---|---|---|---|---|---|---|---|"
foreach ($s in $seconds) { "| {0:HH:mm:ss} | {1} | {2} ms | {3} | {4} | {5} | {6} ms | {7} ms |" -f (TsUtc $s.ts), $s.fields.frames, $s.fields.worst_ms, $s.fields.gaps_over_20ms, $s.fields.gaps_over_33ms, $s.fields.busy_over_16ms, $s.fields.busy_ms, $s.fields.measure_ms }
$frames = ($seconds | ForEach-Object { $_.fields.frames } | Measure-Object -Sum).Sum
$over20 = ($seconds | ForEach-Object { $_.fields.gaps_over_20ms } | Measure-Object -Sum).Sum
$over33 = ($seconds | ForEach-Object { $_.fields.gaps_over_33ms } | Measure-Object -Sum).Sum
$worst = ($seconds | ForEach-Object { $_.fields.worst_ms } | Measure-Object -Maximum).Maximum
$share = if ($frames -gt 0) { 100.0 * $over20 / $frames } else { 100 }
$script:scrollGoal = $frames -gt 0 -and $over33 -eq 0 -and $share -lt 5
$mine = ($loads | Where-Object { $_.Id -eq $p.Id } | ForEach-Object { $_.Percent } | Measure-Object -Sum).Sum
$busiest = $loads | Where-Object { $_.Id -ne $p.Id -and $_.Name -ne 'Idle' } | Sort-Object Percent -Descending | Select-Object -First 3
"scroll goal (no frame over 33 ms, under 5 % over 20 ms) met: {0}; {1} frames in {2} s, {3} over 20 ms ({4:N1} %), {5} over 33 ms, worst {6} ms; CPU during the hold: machine {7:N1} %, this window {8:N1} %, busiest others: {9}" -f `
  $(if ($script:scrollGoal) { 'yes' } else { 'no' }), $frames, $seconds.Count, $over20, $share, $over33, $worst, (100 * ($cpuAfter[0] - $cpuBefore[0]) / $allTicks), $mine, (($busiest | ForEach-Object { '{0} {1:N1} %' -f $_.Name, $_.Percent }) -join ', ')

# ----- Phase 5b: the file keys, with real key presses, checked on disk -----
$files = "$root\files"; $src = "$files\src"; $dst = "$files\dst"
New-Item -ItemType Directory -Force $src, $dst | Out-Null
Set-Content -LiteralPath "$src\report.txt" -Value "new text" -NoNewline
Set-Content -LiteralPath "$dst\report.txt" -Value "older text!!" -NoNewline
Set-Content -LiteralPath "$src\notes.md" -Value "# notes" -NoNewline
# This one goes to the Recycle Bin (the Delete check); its name says where it came from.
Set-Content -LiteralPath "$src\cabinetos-live-check-delete-me.txt" -Value "bye" -NoNewline

Step "left pane: the source folder; right pane: the destination"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($src); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($dst); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400

Step "F7: a new folder, named in place"
PressForNameBox { [Live]::Press($VK.F7) }
[Live]::Type("Reports 2026"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
"F7 made the folder and named it: $(Test-Path -LiteralPath "$src\Reports 2026")"

# Rows now: Reports 2026, cabinetos-live-check-delete-me.txt, notes.md, report.txt.
Step "F2: notes.md becomes readme.md (only 'notes' is selected, as in Explorer)"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
PressForNameBox { [Live]::Press($VK.F2) }
[Live]::Type("readme"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
"F2 renamed it: $((Test-Path -LiteralPath "$src\readme.md") -and -not (Test-Path -LiteralPath "$src\notes.md"))"

Step "Delete: the test file goes to the Recycle Bin"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Delete); Start-Sleep -Milliseconds 3000
"Delete removed it: $(-not (Test-Path -LiteralPath "$src\cabinetos-live-check-delete-me.txt"))"

# Rows now: Reports 2026, readme.md, report.txt; the destination has a report.txt of its own.
Step "F5: copy report.txt; the conflict card answers Skip"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.F5); Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\phase-5b-conflict-live.png"
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Skip")
$skip = [System.Windows.Automation.AutomationElement]::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($skip) { ([System.Windows.Automation.InvokePattern]$skip.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Step "Skip pressed through UI Automation" } else { Step "no Skip button found" }
Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\phase-5b-flyout-live.png"
"F5 with Skip left the destination's file alone: $((Get-Content -LiteralPath "$dst\report.txt" -Raw) -eq 'older text!!')"

Step "Shift+F10: the context menu of the focused row"
[Live]::Press($VK.Shift, $VK.F10); Start-Sleep -Milliseconds 700
Shot $h "$ShotDir\phase-5b-context-menu-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 400

Step "Alt+Enter: Properties of report.txt (a dialog: the snapshot aid cannot draw it)"
[Live]::Press($VK.Alt, $VK.Enter); Start-Sleep -Milliseconds 900
Shot $h "$ShotDir\phase-5b-properties-live.png"
Step "Ctrl+Backquote while the dialog is open: nothing may run under it"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 800
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 700
Shot $h "$ShotDir\phase-5b-properties-closed.png"
$log = Get-Content "$root\logs\ui.*.jsonl"
"Esc closed the dialog: $([bool]($log | Where-Object { $_ -match '"dialog closed"' }))"
"no command ran while the dialog was open: $(NothingRanUnderDialog $log)"

# ----- Phase 5c: keys inside WebView2 pages need real key presses -----
Step "Ctrl+Backquote: the terminal opens and takes the keyboard"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Seconds 3
# Typed as Unicode key events (the touch keyboard's way): the shell must echo live-5c.
[Live]::Type("echo live-5c"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\phase-5c-terminal-live.png"

Step "Ctrl+Shift+P from the terminal: the page passes it to the window"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 700
Shot $h "$ShotDir\phase-5c-palette-from-terminal.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 600
Step "Esc closed the palette; the keyboard is back in the terminal"
[Live]::Type("echo back-in-the-shell"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
Shot $h "$ShotDir\phase-5c-terminal-after-palette.png"

Step "Ctrl+Backquote in the terminal: the keyboard goes back to the pane; again: the terminal hides"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 600
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 800
Shot $h "$ShotDir\phase-5c-terminal-hidden.png"

# Since Phase 16 Ctrl+F finds in the pane (section 16); the search through subfolders is the Search view, which the
# classic layout shows in the sidebar's place while it is asked for (Ctrl+Shift+F).
Step "Ctrl+Shift+F: the Search view; search 'report', Down to the hit, Enter to go there"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.F); Start-Sleep -Milliseconds 600
[Live]::Type("report"); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\phase-5c-search-live.png"
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 500
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
Shot $h "$ShotDir\phase-5c-search-opened.png"
Step "Ctrl+Shift+F, type, Esc: back to the folder, the sidebar shows its folders again"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.F); Start-Sleep -Milliseconds 600
[Live]::Type("readme"); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 600
Shot $h "$ShotDir\phase-5c-search-left.png"

Step "Enter on readme.md: Markdown Preview in the other pane (tools from sdk\tools)"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($src); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
# Rows: Reports 2026, readme.md, report.txt.
[Live]::Press($VK.Home); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Seconds 3
Shot $h "$ShotDir\phase-5c-markdown-live.png"
Step "Ctrl+K V on readme.md: the same file, in the open preview"
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.V); Start-Sleep -Milliseconds 1500
Shot $h "$ShotDir\phase-5c-markdown-chord.png"
$ready = @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"tool ready"' }).Count
"the preview said ready for each open (2 expected): $ready"

# The preview stays open in the right pane through sub-phase 11a's Ctrl+Alt+P: the terminal then shows
# while a web page is open in the other pane, where run 4 lost every key until a mouse click (the
# window left the keys in its own input window; docs/ui.md, "The terminal"). It closes right after
# that check, so the later sections find two file panes.
Shot $h "$ShotDir\phase-5c-preview-stays.png"

# ----- Sub-phase 11a: Total Commander's keys (docs/ui.md, "Total Commander's keys") -----
$tc = "$files\tc"
New-Item -ItemType Directory -Force "$tc\docs", "$tc\photos\2026" | Out-Null
[System.IO.File]::WriteAllBytes("$tc\photos\a.jpg", (New-Object byte[] 3000))
[System.IO.File]::WriteAllBytes("$tc\photos\2026\b.jpg", (New-Object byte[] 5000))
foreach ($name in "a.txt", "b.txt", "notes.md", "run.cmd", "cabinetos-live-check-f8.txt", "cabinetos-live-check-shift-f8.txt") { Set-Content -LiteralPath "$tc\$name" -Value "x" -NoNewline }
Set-Content -LiteralPath "$tc\run.cmd" -Value "echo this must never run" -NoNewline
# Rows: docs, photos, a.txt, b.txt, cabinetos-live-check-f8.txt, cabinetos-live-check-shift-f8.txt, notes.md, run.cmd.

Step "11a: the check folder"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($tc); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000

Step "11a: Num *: every file marked, the folders not"
[Live]::Press($VK.Home); [Live]::Press($VK.NumMultiply); Start-Sleep -Milliseconds 500
"after Num *: $(SelectionText) (the 6 files expected, docs and photos not)"
Step "11a: Ctrl+Num -: nothing marked"
[Live]::Press($VK.Ctrl, $VK.NumSubtract); Start-Sleep -Milliseconds 500
"after Ctrl+Num -: $(SelectionText)"

Step "11a: Num +: the pattern box, *.txt, Enter"
[Live]::Press($VK.NumAdd); Start-Sleep -Milliseconds 600
Shot $h "$ShotDir\11a-pattern-box-live.png"
[Live]::Type("*.txt"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 800
"after Num + *.txt: $(SelectionText) (the four .txt files expected)"
[Live]::Press($VK.Ctrl, $VK.NumSubtract); Start-Sleep -Milliseconds 300

Step "11a: Space on photos: marked in place and measured"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); [Live]::Press($VK.Space); Start-Sleep -Milliseconds 1200
"after Space on photos: $(SelectionText) (8 KB expected: its two files)"
Step "11a: Alt+Shift+Enter: every folder measured"
[Live]::Press($VK.Alt, $VK.Shift, $VK.Enter); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\11a-folder-sizes-live.png"
$measures = @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"request sent"' -and $_ -match '"measure_paths"' }).Count
"measure_paths sent (2 expected): $measures"

Step "11a: F3 on run.cmd: the status bar says no tool shows it; nothing runs it"
[Live]::Press($VK.End); Start-Sleep -Milliseconds 300
[Live]::Press($VK.F3); Start-Sleep -Milliseconds 800
"F3 said so: $([bool](Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"notice shown"' -and $_ -match 'No installed tool shows run.cmd' }))"

Step "11a: F4 on a.txt: the stand-in editor gets it, not Notepad"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.F4); Start-Sleep -Milliseconds 1500
Step "11a: Shift+F4: a new row, 'todo' over the selected stem, Enter"
PressForNameBox { [Live]::Press($VK.Shift, $VK.F4) }
Shot $h "$ShotDir\11a-new-text-file-live.png"
[Live]::Type("todo"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 2000
"Shift+F4 made todo.txt: $(Test-Path -LiteralPath "$tc\todo.txt")"
$edited = if (Test-Path -LiteralPath "$stub.log") { @(Get-Content -LiteralPath "$stub.log" -Encoding Unicode) } else { @() }
"the stand-in editor got (a.txt, then todo.txt, expected): $($edited -join ' | ')"
"no Notepad opened by F4 or Shift+F4: $(-not [bool](Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $p.StartTime }))"

Step "11a: quick search 'cabinetos-live-check-f', then F8: to the Recycle Bin"
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 1200
[Live]::Type("cabinetos-live-check-f"); Start-Sleep -Milliseconds 500
Shot $h "$ShotDir\11a-quick-search-live.png"
[Live]::Press($VK.F8); Start-Sleep -Milliseconds 2500
"F8 removed it: $(-not (Test-Path -LiteralPath "$tc\cabinetos-live-check-f8.txt"))"
Step "11a: Shift+F8 on the other one: the question, Delete permanently through UI Automation"
Start-Sleep -Milliseconds 1200
[Live]::Type("cabinetos-live-check-s"); Start-Sleep -Milliseconds 500
[Live]::Press($VK.Shift, $VK.F8); Start-Sleep -Milliseconds 1200
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Delete permanently")
$confirm = [System.Windows.Automation.AutomationElement]::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($confirm) { ([System.Windows.Automation.InvokePattern]$confirm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Step "Delete permanently pressed through UI Automation" } else { Step "no Delete permanently button found"; [Live]::Press($VK.Esc) }
Start-Sleep -Milliseconds 2500
"Shift+F8 removed it for good: $(-not (Test-Path -LiteralPath "$tc\cabinetos-live-check-shift-f8.txt"))"

Step "11a: Ctrl+Alt+P: the terminal shows with the folder typed at the prompt (Ctrl+P is Quick Open since Phase 16)"
function HandedToTerminal { @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"a page has the keyboard"' -and $_ -match '"page":"terminal"' }) }
$handedBefore = (HandedToTerminal).Count
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.P); Start-Sleep -Seconds 3
Shot $h "$ShotDir\11a-terminal-path-live.png"
"the path was typed: $([bool](Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"paths typed at the prompt"' -and $_ -match 'OkReply' }))"
# The preview is open in the right pane (phase 5c left it there). The window checks where Windows
# sends the keys after it gave the terminal the keyboard, and hands them over again when WinUI left
# them in the window ("a page has the keyboard" with the hand-overs it took).
$handed = HandedToTerminal | Select-Object -Skip $handedBefore | Select-Object -Last 1
"the terminal's page has the keyboard after Ctrl+Alt+P (hand-overs: $(if ($handed) { ($handed | ConvertFrom-Json).fields.hand_overs } else { 'none' })), the preview open in the other pane: $([bool]$handed)"
# Esc clears the typed line in pwsh; Ctrl+Backquote gives the keyboard back to the pane (the page
# passes it to the window, which runs view.toggleTerminal).
function ToggleCount { @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"command executed"' -and $_ -match 'view\.toggleTerminal' }).Count }
$lostBefore = @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"a key the page did not get"' }).Count
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 300
$toggles = ToggleCount
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 600
"Ctrl+Backquote reached the window after Ctrl+Alt+P, the preview open in the other pane: $((ToggleCount) -gt $toggles)"
"keys the window had to take for the page (0 expected): $(@(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"a key the page did not get"' }).Count - $lostBefore)"

Step "11a: close the preview, so the later sections find two file panes"
$closed = @(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"tool closed"' }).Count
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("Close Editor"); Start-Sleep -Milliseconds 700
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 800
"the preview closed: $(@(Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"tool closed"' }).Count -gt $closed)"

Step "11a: Ctrl+\: the drive's root"
[Live]::Press($VK.Ctrl, $VK.Backslash); Start-Sleep -Milliseconds 1200
Step "11a: Alt+F1: the drive list under the left pane"
[Live]::Press($VK.Alt, $VK.F1); Start-Sleep -Milliseconds 700
if ([Live]::ForegroundPid() -ne [uint32]$p.Id) {
  # On this PC another program takes Alt+F1 before the window sees it (the Claude desktop app's
  # global hotkey brought its window to the front). The key is right; the list is opened from the
  # palette instead, and the output says so.
  $taker = Get-Process -Id ([Live]::ForegroundPid()) -ErrorAction SilentlyContinue
  "Alt+F1 is taken by another program on this PC ($($taker.ProcessName), '$($taker.MainWindowTitle)'): it never reached the window; the drive list is opened from the palette instead"
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
  [void][Live]::SetForegroundWindow($h); Start-Sleep -Milliseconds 400
  [Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
  [Live]::Type("Choose Drive for Left"); Start-Sleep -Milliseconds 700
  [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 700
}
Shot $h "$ShotDir\11a-drive-list-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 400
Step "11a: Ctrl+U: the panes change places"
[Live]::Press($VK.Ctrl, $VK.U); Start-Sleep -Milliseconds 800
Shot $h "$ShotDir\11a-swapped-live.png"
# And back, so the sections after this one find the panes where they expect them.
[Live]::Press($VK.Ctrl, $VK.U); Start-Sleep -Milliseconds 800
# The keyboard is in the pane: Ctrl+Backquote hides the terminal Ctrl+Alt+P showed, and the pane keeps the keys.
$toggles = ToggleCount
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 800
$owner = Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"keyboard owner"' -and $_ -match 'terminal hidden' } | Select-Object -Last 1
$ownerFields = if ($owner) { ($owner | ConvertFrom-Json).fields } else { $null }
"Ctrl+Backquote hid the terminal: $((ToggleCount) -gt $toggles)"
"then the keys go to $(if ($ownerFields) { "$($ownerFields.element), $($ownerFields.keys_to)" } else { '(not logged)' }), the pane: $([bool]($ownerFields -and $ownerFields.element -eq 'FilePane' -and $ownerFields.keys_to -eq 'window'))"
$ran = Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"command executed"' } | ForEach-Object { ($_ | ConvertFrom-Json).fields.command }
foreach ($command in "file.delete", "file.deletePermanently", "edit.selectByPattern", "edit.invertSelection", "edit.unselectAll", "edit.toggleSelectionInPlace",
  "file.calculateAllFolderSizes", "go.root", "go.chooseDriveLeft", "view.swapPanes", "file.view", "file.edit", "file.newTextFile", "terminal.insertPath") {
  "  ran $command from a key: $($ran -contains $command)"
}

# ----- Commander Compact (docs/ui.md, "Metrics and chrome"): the density preset, switched live -----
# The picker lists the run's shipped themes by ID: catppuccin-mocha, commander-compact, default, nord,
# rose-pine-moon; its highlight starts on the theme in effect. The last "metrics applied" line of the
# window's log says what the window laid itself out with.
function LastChosenTheme {
  $line = Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"theme chosen"' } | Select-Object -Last 1
  if ($line) { ($line | ConvertFrom-Json).fields.theme } else { "(nothing chosen)" }
}
function LastMetrics { Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"metrics applied"' } | ForEach-Object { ($_ | ConvertFrom-Json).fields } | Select-Object -Last 1 }
$cc = "$files\compact"
New-Item -ItemType Directory -Force "$cc\src", "$cc\dst" | Out-Null
Set-Content -LiteralPath "$cc\src\cabinetos-live-check-f5.txt" -Value "copied by the function-key bar" -NoNewline

Step "compact: the source folder in the active pane, the destination in the other"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$cc\src"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$cc\dst"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400

# The picker previews its highlight live (docs/ui.md, "Themes"): "theme previewed" when the window paints
# the highlighted theme, "theme restored" when Esc paints the theme in effect back. Nothing is written.
function ThemeLog { Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match '"target":"cabinetos_ui::theme"' } | ForEach-Object { $_ | ConvertFrom-Json } }
Step "theme preview: Ctrl+K Ctrl+T, Down previews the next theme, Esc paints the theme in effect back"
$themeBefore = (ThemeLog | Where-Object { $_.message -eq 'theme applied' -or $_.message -eq 'theme restored' } | Select-Object -Last 1).fields.theme
[void][Live]::SetCursorPos(2, 2); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 1500
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 500
Shot $h "$ShotDir\theme-preview-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 800
$themeLines = @(ThemeLog)
$lastPreview = -1
for ($i = 0; $i -lt $themeLines.Count; $i++) { if ($themeLines[$i].message -eq 'theme previewed') { $lastPreview = $i } }
$previewed = if ($lastPreview -ge 0) { $themeLines[$lastPreview].fields.theme } else { '(nothing previewed)' }
$restored = if ($lastPreview -ge 0) { $themeLines | Select-Object -Skip ($lastPreview + 1) | Where-Object { $_.message -eq 'theme restored' } | Select-Object -First 1 } else { $null }
"theme preview: Down previewed $previewed, not the theme in effect ($themeBefore): $([bool]($lastPreview -ge 0 -and $previewed -ne $themeBefore))"
"theme preview: Esc painted $(if ($restored) { $restored.fields.theme } else { '(nothing)' }) back after it ($themeBefore expected): $([bool]($restored -and $restored.fields.theme -eq $themeBefore))"

Step "compact: Ctrl+K Ctrl+T, the theme picker; Home, Down to Commander Compact, Enter"
# The mouse goes to the corner first: a pointer left over the list would pull the highlight to its row.
[void][Live]::SetCursorPos(2, 2); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\compact-picker-live.png"
# The picker lists the themes by id (catppuccin-mocha, commander-compact, default, nord, rose-pine-moon)
# and highlights the current one; Home makes the walk independent of where it started. In the first
# real-key run a single Up from default chose catppuccin-mocha, and a Down from there chose it again.
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 2000
"picker chose: $(LastChosenTheme) (commander-compact expected)"
Shot $h "$ShotDir\compact-live.png"
$metrics = LastMetrics
"compact: the window laid itself out with $($metrics.theme): rows $($metrics.row_height) px (20 expected), function keys $($metrics.fkey_bar), stripes $($metrics.row_stripes), hairlines $($metrics.hairlines)"

Step "compact: Tab twice goes to the other pane and back, never to a function key"
$barNames = "F3 View", "F4 Edit", "F5 Copy", "F6 Move", "F7 Mkdir", "F8 Delete", "Alt+F1 Drv"
$onBar = 0
foreach ($i in 1..2) {
  [Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
  $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
  if ($focused -and $barNames -contains $focused.Current.Name) { $onBar++ }
}
"Tab never reached a function key: $($onBar -eq 0)"

# A click on the top row's chrome must not leave the keyboard on the button: the pane keeps it, so Tab (view.focusOtherPane,
# context filesView) still switches panes. The real mouse does what the snapshot aid's click: step cannot: it presses the button.
# Evidence: the window's log says Tab ran view.focusOtherPane (one more "command executed" line): had the click left the
# keyboard on the button, Tab would have walked WinUI's tab stops and run nothing. UI Automation adds that the focus is no
# Button; it cannot say which pane has it, because WinUI reports the focus at the window's input site (run of 2026-10-01).
function FocusOtherCount { @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match '"command executed"' -and $_ -match 'view\.focusOtherPane' }).Count }
# The leftmost element of a name: the top row's button, not a folder or a menu row of the same name.
function TopRowButton([string]$name) {
  $all = [System.Windows.Automation.AutomationElement]::FromHandle($script:h).FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
  @($all | Sort-Object { $_.Current.BoundingRectangle.Top }, { $_.Current.BoundingRectangle.Left })[0]
}
function ClickCentre($element) {
  $r = $element.Current.BoundingRectangle
  [Live]::Click([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2))
}
# Tab, then one evidence line; a second Tab brings the keyboard back to the source pane for the steps after this one.
function TabReachesOtherPane([string]$what) {
  $before = FocusOtherCount
  [Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
  $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
  $wr = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($script:h, 9, [ref]$wr, 16)
  $fr = $focused.Current.BoundingRectangle
  $isButton = $focused.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
  "compact: $what, Tab ran view.focusOtherPane: $((FocusOtherCount) -eq $before + 1)"
  "compact: $what, the keyboard is on no Button after Tab: $(-not $isButton) (focused: '$($focused.Current.Name)', $($focused.Current.ClassName), $($focused.Current.ControlType.ProgrammaticName), centre x $([int]($fr.Left + $fr.Width / 2)) of $($wr.Left)..$($wr.Right))"
  [Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
}

Step "compact: a real click on the top row's Toggle dual pane, twice, then Tab reaches the other pane"
$dualButton = TopRowButton "Toggle dual pane"
if ($dualButton) {
  ClickCentre $dualButton; Start-Sleep -Milliseconds 600
  ClickCentre $dualButton; Start-Sleep -Milliseconds 600
  TabReachesOtherPane "after two real clicks on Toggle dual pane"
} else { "compact: no Toggle dual pane button found: False" }

Step "compact: a real click on the hamburger (Menu), Esc, then Tab reaches the other pane"
$menuButton = TopRowButton "Menu"
if ($menuButton) {
  ClickCentre $menuButton; Start-Sleep -Milliseconds 400
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 300
  TabReachesOtherPane "after a real click on Menu and Esc"
} else { "compact: no Menu button found: False" }

Step "compact: F5 Copy pressed through the bar's button by its accessible name"
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "F5 Copy")
$f5 = [System.Windows.Automation.AutomationElement]::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($f5) { ([System.Windows.Automation.InvokePattern]$f5.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Step "F5 Copy pressed through UI Automation" } else { Step "no F5 Copy button found" }
Start-Sleep -Milliseconds 2500
Shot $h "$ShotDir\compact-f5-live.png"
"the bar's F5 copied the file: $(Test-Path -LiteralPath "$cc\dst\cabinetos-live-check-f5.txt")"
"the bar ran file.copyToOtherPane: $([bool](Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"command executed"' -and $_ -match 'file\.copyToOtherPane' -and $_ -match '"trigger":"fkeyBar"' }))"

Step "compact: Ctrl+K Ctrl+T, Home, Down, Down to Default, Enter"
[void][Live]::SetCursorPos(2, 2); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 2000
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 2000
"picker chose: $(LastChosenTheme) (default expected)"
Shot $h "$ShotDir\compact-back-live.png"
$metrics = LastMetrics
"compact: switched back to $($metrics.theme): rows $($metrics.row_height) px (30 expected), function keys $($metrics.fkey_bar)"

# ----- 12: tabs (docs/ui.md, "Tabs"): the strip above a pane's list, with real keys -----
# The window's log says what the tabs did: "tab shown" (the folder, the tab's place, how many tabs,
# whether it is locked), "tab row shown" and "tab row hidden", and the notices of the status bar.
# Since Phase 16 the strip shows from the first tab, so it is shown once at the start and never hidden.
function TabLog { Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match '"target":"cabinetos_ui::tabs"' } | ForEach-Object { $_ | ConvertFrom-Json } }
function LastTabShown { TabLog | Where-Object { $_.message -eq 'tab shown' -and $_.fields.pane -eq 0 } | Select-Object -Last 1 }
function NoticeCount([string]$pattern) { @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match '"notice shown"' -and $_ -match $pattern }).Count }
$tb = "$files\tabs12"
New-Item -ItemType Directory -Force "$tb\one\sub", "$tb\two" | Out-Null
Set-Content -LiteralPath "$tb\one\note.txt" -Value "one" -NoNewline
Set-Content -LiteralPath "$tb\one\sub\deep.txt" -Value "deep" -NoNewline
Set-Content -LiteralPath "$tb\two\other.txt" -Value "two" -NoNewline

Step "tabs: the left pane in tabs12\one, one tab, and its strip shows"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$tb\one"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
$rowShown = @(TabLog | Where-Object { $_.message -eq 'tab row shown' -and $_.fields.pane -eq 0 }).Count
"tabs: the strip is shown with one tab: $($rowShown -eq 1)"

Step "tabs: Ctrl+T twice, three tabs, the row shows"
[Live]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 900
$shown = LastTabShown
"tabs: three tabs after Ctrl+T twice, the last in front: $($shown.fields.tabs -eq 3 -and $shown.fields.index -eq 2)"
"tabs: the strip is still shown, once: $(@(TabLog | Where-Object { $_.message -eq 'tab row shown' -and $_.fields.pane -eq 0 }).Count -eq 1)"
Shot $h "$ShotDir\tabs-three-live.png"

Step "tabs: Ctrl+Tab goes on to the first tab"
[Live]::Press($VK.Ctrl, $VK.Tab); Start-Sleep -Milliseconds 900
$shown = LastTabShown
"tabs: Ctrl+Tab from the last tab came to the first: $($shown.fields.index -eq 0)"

Step "tabs: the palette, Toggle Tab Lock, Enter"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("toggle tab lock"); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 900
"tabs: the tab is locked, the status bar said so: $((NoticeCount 'is locked') -eq 1)"

Step "tabs: Enter on the folder sub in the locked tab opens a fourth tab"
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
$shown = LastTabShown
"tabs: a new tab shows sub, four tabs: $($shown.fields.tabs -eq 4 -and $shown.fields.path -eq "$tb\one\sub")"
Shot $h "$ShotDir\tabs-locked-live.png"

Step "tabs: Ctrl+W closes the new tab"
[Live]::Press($VK.Ctrl, $VK.W); Start-Sleep -Milliseconds 1200
$shown = LastTabShown
"tabs: three tabs again, the front tab is not in sub: $($shown.fields.tabs -eq 3 -and $shown.fields.path -ne "$tb\one\sub")"

Step "tabs: Ctrl+W twice more leaves one tab, and the strip stays"
[Live]::Press($VK.Ctrl, $VK.W); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Ctrl, $VK.W); Start-Sleep -Milliseconds 1200
$shown = LastTabShown
"tabs: one tab left: $($shown.fields.tabs -eq 1)"
"tabs: the strip was never hidden: $(@(TabLog | Where-Object { $_.message -eq 'tab row hidden' -and $_.fields.pane -eq 0 }).Count -eq 0)"
Shot $h "$ShotDir\tabs-hidden-live.png"

Step "tabs: Ctrl+W on the last tab is refused"
$before = NoticeCount 'keeps its last folder tab'
[Live]::Press($VK.Ctrl, $VK.W); Start-Sleep -Milliseconds 900
"tabs: the last tab stayed, the status bar said why: $((NoticeCount 'keeps its last folder tab') -eq $before + 1)"

# ui.tabs is written a second after the last change. If the tab that is left is the locked one,
# the palette unlocks it, so the later sections can go to other folders in this pane.
Start-Sleep -Milliseconds 1500
$saved = (Get-Content "$root\config\cabinetos.json" -Raw | ConvertFrom-Json).ui.tabs
"tabs: ui.tabs holds one tab in the left pane: $(@($saved.left.items).Count -eq 1)"
if (@($saved.left.items)[0].locked) {
  Step "tabs: the tab that is left is the locked one; the palette unlocks it"
  [Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
  [Live]::Type("toggle tab lock"); Start-Sleep -Milliseconds 900
  [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 900
  "tabs: unlocked again: $((NoticeCount 'is unlocked') -eq 1)"
}

# ----- 14: what plugins ask of the window (docs/ui.md, "What plugins ask of the window") -----
# Two parts. The drag of a pane's row onto a tool's page needs only the Markdown Preview. "ask" needs the
# agent extension (Phase 14) with its fake provider, which did not exist when this was written (2026-09-30):
# it is written against the protocol and waits until sdk\extensions\agent and build-index.ps1 -Extensions exist.
function PluginLog([string]$pattern) { @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match $pattern }) }
$drag = "$files\drag14"
New-Item -ItemType Directory -Force $drag | Out-Null
Set-Content -LiteralPath "$drag\a.txt" -Value "dragged" -NoNewline
Set-Content -LiteralPath "$drag\readme.md" -Value "# The tool's page" -NoNewline

Step "14: drag: tabs12's pane goes to the drag folder; the cursor on readme.md; Ctrl+K V opens it in the preview"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($drag); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 200
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.V); Start-Sleep -Milliseconds 2500
"14: the preview page is ready in the other pane: $([bool](PluginLog '"tool ready"' | Where-Object { $_ -match 'readme.md' }))"
Shot $h "$ShotDir\drag14-page-live.png"

Step "14: drag: the first row of the left pane (a.txt) over the right pane's page, and let go"
# Rows start about 15 % down the window and the row's name about 17 % across it; the page fills the right half.
$rect = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$rect, 16)
$w = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
$before = (PluginLog '"paths dropped on a tool"').Count
$startedBefore = (PluginLog '"row drag started"').Count
[Live]::Drag([int]($rect.Left + $w * 0.17), [int]($rect.Top + $ht * 0.149), [int]($rect.Left + $w * 0.72), [int]($rect.Top + $ht * 0.5))
Start-Sleep -Milliseconds 1200
"14: the row drag started in the pane: $((PluginLog '"row drag started"').Count -eq $startedBefore + 1)"
$dropped = @(PluginLog '"paths dropped on a tool"')
"14: the drop reached the tool's page as paths-dropped: $($dropped.Count -eq $before + 1)"
if ($dropped.Count -gt 0) { "14: it carried one path: $((($dropped | Select-Object -Last 1) | ConvertFrom-Json).fields.paths -eq 1)" }
Shot $h "$ShotDir\drag14-dropped-live.png"

# Ctrl+W would close a tab of the left pane (the keyboard is there); the page's tab closes through the palette.
Step "14: drag: the palette's Close Editor closes the page's tab, the keyboard is in the left pane again"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("close editor"); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
ClickLeftPane

if (-not $hasExtensions) {
  "14: ask: WAITING: the Agent extension is not built here (sdk\extensions\agent\plugin\plugin.wasm: run sdk\extensions\build-extensions.ps1, or copy sdk\fixtures\plugins\agent\plugin.wasm there); the steps below did not run"
} else {
  # What the extension's guide says (docs\extensions\agent.md, "Testing without a model"): the setting is
  # plugins.agent.settings.provider; the fake provider answers from fake-replies.json in the plugin's data folder,
  # a JSON list of texts, one for each model call, that hold exact cab command lines; the plugin reads only under
  # %USERPROFILE%, and this run's folder is under it. The marketplace card is "CabinetOS Agent, WASM plugin, by CabinetOS";
  # it comes from the run's own index (built with -Extensions at the start), and installs into the run's plugins folder.
  $ask = "$files\ask14"
  New-Item -ItemType Directory -Force $ask | Out-Null
  foreach ($n in 1..3) { Set-Content -LiteralPath "$ask\photo$n.jpg" -Value "x" -NoNewline }
  $fence = '```'
  $commands = (1..3 | ForEach-Object { "rename `"$ask\photo$_.jpg`" vacation_$_.jpg" }) -join "`n"
  $reply = "Renaming the three photos.`n$fence`n$commands`n$fence"
  New-Item -ItemType Directory -Force "$env:CABINETOS_PLUGINS_DATA_DIR\agent" | Out-Null
  [System.IO.File]::WriteAllText("$env:CABINETOS_PLUGINS_DATA_DIR\agent\fake-replies.json", '[' + ($reply | ConvertTo-Json) + ']', (New-Object System.Text.UTF8Encoding $false))

  function Uia([string]$name) {
    [System.Windows.Automation.AutomationElement]::FromHandle($script:h).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
  }
  function InvokeUia($element) { ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }

  Step "14: ask: the marketplace, the agent's card, Install, Allow and install through UI Automation"
  [Live]::Press($VK.Ctrl, $VK.Shift, $VK.X); Start-Sleep -Milliseconds 2500
  $card = Uia "CabinetOS Agent, WASM plugin, by CabinetOS"
  "14: ask: the agent's card is in the marketplace: $([bool]$card)"
  if ($card) { InvokeUia $card; Start-Sleep -Milliseconds 1200 }
  $install = Uia "Install"
  "14: ask: the detail has an Install button: $([bool]$install)"
  if ($install) { InvokeUia $install; Start-Sleep -Milliseconds 1500 }
  $allow = Uia "Allow and install"
  if ($allow) {
    Shot $h "$ShotDir\ask14-review-live.png"
    InvokeUia $allow; Start-Sleep -Milliseconds 6000
  } else { "14: ask: no Allow and install button found" }
  "14: ask: the plugin is installed in this run's folder: $(Test-Path -LiteralPath "$env:CABINETOS_PLUGINS_DIR\agent\plugin.wasm")"
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 800
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 800

  # The setting is written once the plugin is installed: the file already holds its entry then.
  $cfgPath = "$root\config\cabinetos.json"
  $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if (-not $cfg.plugins) { $cfg | Add-Member -NotePropertyName plugins -NotePropertyValue ([pscustomobject]@{}) -Force }
  if (-not $cfg.plugins.agent) { $cfg.plugins | Add-Member -NotePropertyName agent -NotePropertyValue ([pscustomobject]@{}) -Force }
  $cfg.plugins.agent | Add-Member -NotePropertyName settings -NotePropertyValue ([pscustomobject]@{ provider = 'fake' }) -Force
  [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
  Start-Sleep -Milliseconds 2000

  Step "14: ask: three files in the left pane, Ctrl+K Ctrl+A, 'rename these to vacation_*', Enter"
  ClickLeftPane
  [Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
  [Live]::Type($ask); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
  [Live]::Press($VK.Ctrl, $VK.A); Start-Sleep -Milliseconds 300
  [Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
  [Live]::Press($VK.Ctrl, $VK.A); Start-Sleep -Milliseconds 900
  "14: ask: the prompt box opened: $([bool](PluginLog '"prompt shown"' | Select-Object -Last 1))"
  Shot $h "$ShotDir\ask14-prompt-live.png"
  [Live]::Type("rename these to vacation_*"); Start-Sleep -Milliseconds 300
  [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 5000
  $shown = @(PluginLog '"preview shown"') | Select-Object -Last 1
  "14: ask: the preview shows in the other pane: $([bool]$shown)"
  if ($shown) { "14: ask: it has three rename rows: $((($shown | ConvertFrom-Json).fields.rows) -eq 3)" }
  Shot $h "$ShotDir\ask14-preview-live.png"
  [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 4000
  "14: ask: Enter renamed the files on disk: $(@(Get-ChildItem -LiteralPath $ask | Where-Object { $_.Name -like 'vacation_*' }).Count -eq 3)"
  "14: ask: the preview closed: $([bool](PluginLog '"preview applied"'))"
}

# ----- 13: the activity rail and the modular sidebar (docs/ui.md, "The activity rail and the sidebar") -----
# Every section before this one runs in the classic layout. This one writes ui.layout: rail into the run's
# configuration (the core sends the change on, as it does for an edit of the file by hand), drives the rail, the
# folder tree, the Search view, a tool's page (Quick Notes, fixtures\quick-notes) and the divider with real keys and
# the mouse, and writes classic back, so the sections after it find what they expect. The window's log says what
# happened: "the sidebar shows a view" (view, open, the width the divider sits at), "the tree shows a folder",
# "a rail button was pressed" and "a sidebar page started". Toggle Sidebar is on Ctrl+Alt+B here: the first section
# of this script rebound it.
function UiLines([string]$pattern) { @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern }) }
function LastFields([string]$pattern) {
  $line = UiLines $pattern | Select-Object -Last 1
  if ($line) { ($line | ConvertFrom-Json).fields }
}
function ConfigUi { (Get-Content "$root\config\cabinetos.json" -Raw -Encoding UTF8 | ConvertFrom-Json).ui }
function SetUiConfig([hashtable]$values) {
  $cfgPath = "$root\config\cabinetos.json"
  $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach ($key in $values.Keys) { $cfg.ui | Add-Member -NotePropertyName $key -NotePropertyValue $values[$key] -Force }
  [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
}
# A rail button, by its accessible name. The rail is the leftmost column: a folder or a hit of the same name lies to its right.
function RailButtonElement([string]$name) {
  $all = [System.Windows.Automation.AutomationElement]::FromHandle($script:h).FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
  @($all | Sort-Object { $_.Current.BoundingRectangle.Left })[0]
}
$railRects = @{}
function ClickRail([string]$name) {
  $r = $railRects[$name]
  [Live]::Click([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2))
}
function LockTreeFromPalette {
  [Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
  [Live]::Type("lock folder tree"); Start-Sleep -Milliseconds 900
  [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 900
}

$rl = "$files\rail13"
New-Item -ItemType Directory -Force "$rl\alpha\inner", "$rl\beta\sub", "$rl\gamma" | Out-Null
Set-Content -LiteralPath "$rl\alpha\zzreport13.txt" -Value "found" -NoNewline
Set-Content -LiteralPath "$rl\note.txt" -Value "note" -NoNewline
Set-Content -LiteralPath "$rl\beta\sub\s.txt" -Value "s" -NoNewline
$origUi = ConfigUi
$origSidebar = if ($null -ne $origUi.sidebar) { [bool]$origUi.sidebar } else { $true }
$warnBefore = (UiLines '"level":"(WARN|WARNING|ERROR)"').Count

Step "13: the configuration says ui.layout: rail; the rail and the folder tree appear"
$before = (UiLines '"the rail layout is on"').Count
SetUiConfig @{ layout = 'rail'; sidebar = $true }
Start-Sleep -Milliseconds 2500
"13: the window switched to the rail layout: $((UiLines '"the rail layout is on"').Count -gt $before)"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($rl); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1800
$f = LastFields '"the tree shows a folder"'
"13: the tree followed the left pane to rail13: $($f.path -eq $rl)"
$f = LastFields '"the tree drew rows"'
"13: the tree has rows inside the sidebar's window, before Ctrl+Shift+E: $([int]$f.visible -gt 0)"
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar is open on the Explorer: $($f.view -eq 'explorer' -and $f.open -eq $true)"
Shot $h "$ShotDir\rail13-explorer-live.png"

Step "13: the rail's five buttons, found by UI Automation"
$names = 'Explorer', 'Search', 'Marketplace', 'Terminal', 'Quick Notes'
$buttons = @{}
foreach ($n in $names) { $buttons[$n] = RailButtonElement $n; if ($buttons[$n]) { $railRects[$n] = $buttons[$n].Current.BoundingRectangle } }
"13: the rail has its five buttons: $(@($names | Where-Object { $buttons[$_] }).Count -eq 5)"
if ($buttons['Explorer'] -and $buttons['Search']) {
  $wr = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$wr, 16)
  $e = $buttons['Explorer'].Current.BoundingRectangle; $s = $buttons['Search'].Current.BoundingRectangle
  "13: a button is 36 px square: $([math]::Abs($e.Width - 36 * $scale) -le 2 -and [math]::Abs($e.Height - 36 * $scale) -le 2)"
  "13: the buttons are 40 px apart, top to top: $([math]::Abs(($s.Top - $e.Top) - 40 * $scale) -le 2)"
  "13: the column is 12 px in from the window's edge (8 px gutter, 4 px centring): $([math]::Abs($e.Left - ($wr.Left + 12 * $scale)) -le 3)"
}

Step "13: Ctrl+Shift+F: the Search view; 'zzreport13'; Down to the hit; Enter"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.F); Start-Sleep -Milliseconds 700
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar shows the Search view: $($f.view -eq 'search')"
[Live]::Type("zzreport13"); Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\rail13-search-live.png"
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1800
$f = LastFields '"listing shown"'
"13: Enter on the hit took the left pane to alpha: $($f.path -eq "$rl\alpha")"

Step "13: Ctrl+Shift+E: the tree opens alpha, where the pane is, and has the keyboard; Down, Enter: the pane goes to beta"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.E); Start-Sleep -Milliseconds 800
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar shows the Explorer: $($f.view -eq 'explorer')"
$f = LastFields '"the tree shows a folder"'
"13: the tree, shown again, followed the pane to alpha (it follows only while it shows): $($f.path -eq "$rl\alpha")"
[Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
$f = LastFields '"listing shown"'
"13: Enter in the tree took the left pane to beta: $($f.path -eq "$rl\beta")"

Step "13: Right opens beta, Right again goes to sub, Enter takes the pane there"
[Live]::Press($VK.Right); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Right); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
$f = LastFields '"listing shown"'
"13: the pane shows beta\sub: $($f.path -eq "$rl\beta\sub")"
Shot $h "$ShotDir\rail13-tree-keys-live.png"

# Enter in the tree has taken the keyboard to the pane (go.toPath, as the address box's Enter does). Ctrl+Shift+E puts it in the
# tree again; Esc must give it back: the window's own key handler takes Esc first, and says so when it moved the keyboard.
Step "13: Ctrl+Shift+E puts the keyboard in the tree; Esc gives it back to the pane; Backspace goes up"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.E); Start-Sleep -Milliseconds 800
$gave = (UiLines 'Esc gave the keyboard from the rail layout').Count
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 600
"13: Esc in the tree gave the keyboard back to the pane: $((UiLines 'Esc gave the keyboard from the rail layout').Count -eq $gave + 1)"
[Live]::Press($VK.Back); Start-Sleep -Milliseconds 1500
$f = LastFields '"listing shown"'
"13: Backspace went up to beta: $($f.path -eq "$rl\beta")"

Step "13: the palette, Lock Folder Tree; the pane goes to gamma; the tree stays; Alt+Shift+L finds gamma"
$locked = NoticeCount 'folder tree is locked'
LockTreeFromPalette
"13: the tree is locked, the status bar said so: $((NoticeCount 'folder tree is locked') -eq $locked + 1)"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$rl\gamma"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1800
"13: the locked tree did not follow to gamma: $(@(UiLines '"the tree shows a folder"' | Where-Object { $_ -match 'gamma' }).Count -eq 0)"
[Live]::Press($VK.Alt, $VK.Shift, $VK.L); Start-Sleep -Milliseconds 1500
"13: Alt+Shift+L showed gamma in the tree: $(@(UiLines '"the tree shows a folder"' | Where-Object { $_ -match 'gamma' }).Count -eq 1)"
Shot $h "$ShotDir\rail13-locate-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
$follows = NoticeCount 'follows the active folder again'
LockTreeFromPalette
"13: the tree is unlocked, the status bar said so: $((NoticeCount 'follows the active folder again') -eq $follows + 1)"

Step "13: the mouse on the Quick Notes button: the tool's own page shows in the sidebar"
$started = (UiLines '"a sidebar page started"').Count
ClickRail 'Quick Notes'
Start-Sleep -Milliseconds 5000
"13: the page started (once): $((UiLines '"a sidebar page started"').Count -eq $started + 1)"
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar shows quick-notes: $($f.view -eq 'quick-notes')"
$f = LastFields '"a rail button was pressed"'
"13: the click reached the rail as ShowView on quick-notes: $($f.button -eq 'quick-notes' -and $f.action -eq 'ShowView')"
Shot $h "$ShotDir\rail13-tool-page-live.png"

Step "13: Ctrl+Shift+F from inside the page: the page passes the key to the window"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.F); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: the Search view shows again: $($f.view -eq 'search')"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.E); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: Esc left the search field, and Ctrl+Shift+E shows the Explorer (a text box keeps every key but the immutable tier's): $($f.view -eq 'explorer')"

Step "13: the page again; Ctrl+Alt+B from inside it closes the sidebar and the keyboard is in the pane (Backspace goes up)"
ClickRail 'Quick Notes'; Start-Sleep -Milliseconds 1500
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.B); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar is closed: $($f.open -eq $false)"
[Live]::Press($VK.Back); Start-Sleep -Milliseconds 1500
$f = LastFields '"listing shown"'
"13: the pane had the keyboard: Backspace went up to rail13: $($f.path -eq $rl)"
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.B); Start-Sleep -Milliseconds 900
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.E); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar is open on the Explorer again: $($f.view -eq 'explorer' -and $f.open -eq $true)"

Step "13: the mouse on the Marketplace button opens it; a second click closes it"
$shown = (UiLines '"marketplace shown"').Count
$closed = (UiLines '"marketplace closed"').Count
ClickRail 'Marketplace'; Start-Sleep -Milliseconds 2500
"13: the marketplace opened: $((UiLines '"marketplace shown"').Count -eq $shown + 1)"
Shot $h "$ShotDir\rail13-marketplace-live.png"
ClickRail 'Marketplace'; Start-Sleep -Milliseconds 1500
"13: the second click closed it: $((UiLines '"marketplace closed"').Count -eq $closed + 1)"

Step "13: the mouse on the active Explorer button closes the sidebar; Ctrl+Alt+B opens it"
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.E); Start-Sleep -Milliseconds 900
ClickRail 'Explorer'; Start-Sleep -Milliseconds 900
$f = LastFields '"a rail button was pressed"'
"13: the click on the active view was CloseSidebar: $($f.button -eq 'explorer' -and $f.action -eq 'CloseSidebar')"
$f = LastFields '"the sidebar shows a view"'
"13: the sidebar is closed: $($f.open -eq $false)"
Shot $h "$ShotDir\rail13-closed-live.png"

# A mouse press no longer leaves the keyboard on a chrome button (docs/ui.md, "The top row"); the rail's buttons stay tab stops,
# so UI Automation's SetFocus, as assistive technology does it, puts the keyboard on the Explorer button. Shift+Down moves it
# below Search, Shift+Up back.
Step "13: Shift+Down on the Explorer button moves it; Shift+Up moves it back (ui.rail)"
try { (RailButtonElement 'Explorer').SetFocus() } catch { "13: SetFocus on the Explorer button failed: $($_.Exception.Message)" }
Start-Sleep -Milliseconds 400
[Live]::Press($VK.Shift, $VK.Down); Start-Sleep -Milliseconds 1800
$order = @((ConfigUi).rail)
"13: ui.rail holds the new order: $(($order -join ',') -eq 'search,explorer,marketplace,terminal,quick-notes')"
Shot $h "$ShotDir\rail13-moved-live.png"
[Live]::Press($VK.Shift, $VK.Up); Start-Sleep -Milliseconds 1800
"13: ui.rail is empty again (the default order): $(@((ConfigUi).rail).Count -eq 0)"
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.B); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: Ctrl+Alt+B opened it again: $($f.open -eq $true)"

Step "13: the divider: drag it to 300 px; drag it under 150 px (it snaps shut and remembers 300)"
$f = LastFields '"the sidebar shows a view"'
$w0 = [int]$f.width
$dr = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$dr, 16)
$dy = [int](($dr.Top + $dr.Bottom) / 2)
# The divider is the 8 px strip at the sidebar's right edge: 8 px gutter + 44 px rail + 8 px gap, then the width, then 4 px to its middle.
[Live]::Drag([int]($dr.Left + (64 + $w0) * $scale), $dy, [int]($dr.Left + (64 + 300) * $scale), $dy)
Start-Sleep -Milliseconds 1800
"13: ui.sidebarWidth is near 300 (was $w0): $([math]::Abs([double](ConfigUi).sidebarWidth - 300) -le 4)"
Shot $h "$ShotDir\rail13-divider-live.png"
$f = LastFields '"the sidebar shows a view"'
$w1 = [int](ConfigUi).sidebarWidth
[Live]::Drag([int]($dr.Left + (64 + $w1) * $scale), $dy, [int]($dr.Left + (64 + 100) * $scale), $dy)
Start-Sleep -Milliseconds 1800
"13: ui.sidebar is false, the drag under 150 px closed it: $((ConfigUi).sidebar -eq $false)"
"13: ui.sidebarWidth still near 300: $([math]::Abs([double](ConfigUi).sidebarWidth - 300) -le 4)"
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.B); Start-Sleep -Milliseconds 900
$f = LastFields '"the sidebar shows a view"'
"13: Ctrl+Alt+B opened it at the remembered width: $($f.open -eq $true -and [math]::Abs([int]$f.width - 300) -le 4)"

Step "13: the configuration says ui.layout: classic again; the rail goes"
$off = (UiLines '"the rail layout is off"').Count
SetUiConfig @{ layout = 'classic'; sidebar = $origSidebar; sidebarWidth = $null; sidebarView = 'explorer' }
Start-Sleep -Milliseconds 2500
"13: the window is back in the classic layout: $((UiLines '"the rail layout is off"').Count -gt $off)"
Shot $h "$ShotDir\rail13-classic-back-live.png"
ClickLeftPane
"13: no warning or error line in the window's log during this section: $((UiLines '"level":"(WARN|WARNING|ERROR)"').Count -eq $warnBefore)"

# ----- Edge cases (docs/ui.md, "Edge cases"): the shared fixture, with real keys -----
# The fixture has links, so it lives outside $root: only its own script removes it (rmdir, which
# never follows a link). Its Cyrillic names are built from code points, so this file stays ASCII.
$edge = "$env:TEMP\cabinetos-edge-live"
& "$PSScriptRoot\..\..\sdk\fixtures\edge-fixture.ps1" -Root $edge | ForEach-Object { "fixture: $_" }
$deep = Join-Path $edge 'long'
while ($deep.Length -lt 300) { $deep = Join-Path $deep 'segment-of-a-long-path-0123456789' }
[System.IO.File]::WriteAllText("\\?\$deep\deep notes.md", "# Deep notes", (New-Object System.Text.UTF8Encoding $false))
$zvit = -join ([char[]](0x0417, 0x0432, 0x0456, 0x0442))
$zvitLower = -join ([char[]](0x0437, 0x0432, 0x0456, 0x0442))

Step "edge: the fixture's names on the left, its long path on the right"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$edge\names"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($deep); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
Shot $h "$ShotDir\edge-both-panes-live.png"

# The core's order in names\: case, the Ukrainian folder, the emoji folder, the Chinese folder,
# the 255-unit name, the two cafe.txt, then the Ukrainian report: seven rows down from the first.
Step "edge: F2 on the Ukrainian report, typed Cyrillic, Enter (only the stem is selected)"
# In one run the Tabs of the setup left the long path's pane active, and F2 renamed deep notes.md
# (a Tab right after Enter in the address box did not change the pane): the names pane is checked
# by its first row before the keys go there.
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
if ((SelectionText) -notmatch 'case') { "the names pane was not active after the setup (status: $(SelectionText)); Tab once more"; [Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400; [Live]::Press($VK.Home); Start-Sleep -Milliseconds 300 }
foreach ($i in 1..7) { [Live]::Press($VK.Down) }; Start-Sleep -Milliseconds 300
PressForNameBox { [Live]::Press($VK.F2) }
[Live]::Type("$zvit 2027"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
Shot $h "$ShotDir\edge-renamed-live.png"
"F2 renamed the Cyrillic file: $((Test-Path -LiteralPath "$edge\names\$zvit 2027.txt") -and -not (Test-Path -LiteralPath "$edge\names\$zvit 2026.txt"))"

Step "edge: Ctrl+F, a Cyrillic query in the find widget: the pane keeps the names that hold it"
[Live]::Press($VK.Ctrl, $VK.F); Start-Sleep -Milliseconds 400
[Live]::Type($zvitLower); Start-Sleep -Milliseconds 1500
Shot $h "$ShotDir\edge-search-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 600

Step "edge: Enter into the long path"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type((Split-Path $deep -Parent)); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Home); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\edge-long-live.png"
# Not Enter on deep file.txt: Windows may open it with its program (Notepad did, 2026-09-29).
Step "edge: Enter on deep notes.md: the status bar says why the preview cannot show it"
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
if ((SelectionText) -notmatch 'deep') { "the long path's pane was not active (status: $(SelectionText)); Tab once more"; [Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400; [Live]::Press($VK.Home); Start-Sleep -Milliseconds 300 }
[Live]::Press($VK.Down); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
Shot $h "$ShotDir\edge-preview-refused-live.png"
$notice = Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match '"notice shown"' -and $_ -match 'deep notes.md' }
"the status bar said why: $([bool]$notice)"

Step "edge: Shift+Delete on the junction, Delete permanently through UI Automation"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$edge\links"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Shift, $VK.Delete); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\edge-delete-link-question-live.png"
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Delete permanently")
$confirm = [System.Windows.Automation.AutomationElement]::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($confirm) { ([System.Windows.Automation.InvokePattern]$confirm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Step "Delete permanently pressed through UI Automation" } else { Step "no Delete permanently button found"; [Live]::Press($VK.Esc) }
Start-Sleep -Milliseconds 3000
Shot $h "$ShotDir\edge-deleted-live.png"
"the junction is gone: $(-not (Test-Path -LiteralPath "$edge\links\junction to target"))"
"the files behind it stayed: $((@(Get-ChildItem -LiteralPath "$edge\link-target" | ForEach-Object { $_.Name }) -join ',') -eq 'kept 1.txt,kept 2.txt,kept 3.txt')"

# ----- 16: the shell (docs/ui.md, "The top row", "The breadcrumb row", "Find in pane", "Quick Open") -----
# The creator's SHELL_REDESIGN.md with real keys and clicks. The window's log says what happened: "find opened",
# "find filtered" (how many rows match), "find closed", "quick open shown", "menu shown", and "command executed"
# with each command and what started it. The hamburger and a crumb are found by their accessible names, as a
# screen reader finds them, and clicked with the real mouse.
function ShellLines([string]$message) { @(Get-Content "$root\logs\ui.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match "`"$message`"" } | ForEach-Object { $_ | ConvertFrom-Json }) }
# The window's log writer works in its own thread: a line can reach the file some time after the window logged it, longer
# than a fixed sleep. A check therefore reads a line only after it has come. This polls every 200 ms until there are more
# lines of the message than $before (the count taken before the key or click) and gives the last one, or $null when none
# came within $seconds. $until (optional) says which last line is the wanted one, for a message the window logs once per
# key and may skip when a newer key comes (find filtered); when it never holds, the last line is still given, so the
# check shows what the window said.
function WaitShellLines([string]$message, [int]$before, [int]$seconds = 5, [scriptblock]$until = $null) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    $lines = @(ShellLines $message)
    $last = if ($lines.Count -gt $before) { $lines[$lines.Count - 1] } else { $null }
    if ($last -and (-not $until -or (& $until $last))) { return $last }
    if ((Get-Date) -ge $deadline) { return $last }
    Start-Sleep -Milliseconds 200
  }
}
function ShellElement([string]$name) {
  [System.Windows.Automation.AutomationElement]::FromHandle($script:h).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}
function ClickElement($element) {
  $r = $element.Current.BoundingRectangle
  [Live]::Click([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2))
}
# An element by its accessible name in any top-level window of the app (the main window and any window of its own that a
# flyout or a popup makes), waiting up to $seconds for it, since a dropdown opens with an animation. The desktop's children
# are picked by the app's process ID first, so the search never walks another program's UI tree.
function AppElement([string]$name, [double]$seconds = 0) {
  $ae = [System.Windows.Automation.AutomationElement]
  $mine = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, [int]$script:p.Id)
  $named = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    foreach ($window in $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
      $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $named)
      if ($found) { return $found }
    }
    if ((Get-Date) -ge $deadline) { return $null }
    Start-Sleep -Milliseconds 100
  }
}
# True when no such element is left in the app, waiting up to $seconds for it to go.
function AppElementGone([string]$name, [double]$seconds = 1.5) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    if (-not (AppElement $name)) { return $true }
    if ((Get-Date) -ge $deadline) { return $false }
    Start-Sleep -Milliseconds 100
  }
}
$sh = "$files\shell16"
# The workspace is the git repository that holds the active folder, else that folder, and Quick Open searches the workspace.
# A fake repository (a .git folder with a HEAD file, no git process; the core reads that file) makes shell16 the workspace:
# from alpha, Quick Open then finds target-16.md in beta\deep, and the pill shows the branch.
New-Item -ItemType Directory -Force "$sh\alpha", "$sh\beta\deep", "$sh\.git" | Out-Null
[System.IO.File]::WriteAllText("$sh\.git\HEAD", "ref: refs/heads/live-16`n")
Set-Content -LiteralPath "$sh\alpha\notes-16.txt" -Value "x" -NoNewline
Set-Content -LiteralPath "$sh\alpha\other-16.txt" -Value "x" -NoNewline
Set-Content -LiteralPath "$sh\beta\deep\target-16.md" -Value "x" -NoNewline

Step "16: Ctrl+L, a path, Enter: the left pane in shell16\alpha"
ClickLeftPane
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type("$sh\alpha"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
"16: Ctrl+L and Enter went there: $((SelectionText) -match 'notes-16|other-16')"

Step "16: Ctrl+F, 'other', Enter, Esc: one row while the text is there, the cursor on it, every row after Esc"
$opened = (ShellLines 'find opened').Count
$filters = (ShellLines 'find filtered').Count
[Live]::Press($VK.Ctrl, $VK.F); Start-Sleep -Milliseconds 500
[Live]::Type("other"); Start-Sleep -Milliseconds 800
# "rows" is the rows shown after the filter (the folder has two); "matches" is how many names hold the text.
$filtered = WaitShellLines 'find filtered' $filters -until { param($line) $line.fields.query_length -eq 5 }
"16: the find opened: $([bool](WaitShellLines 'find opened' $opened))"
"16: the pane shows one row while the text is there: $($filtered.fields.matches -eq 1 -and $filtered.fields.rows -eq 1)"
Shot $h "$ShotDir\shell16-find-live.png"
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 500
"16: Enter put the cursor on the match: $((SelectionText) -match 'other-16')"
$closes = (ShellLines 'find closed').Count
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 600
$closed = WaitShellLines 'find closed' $closes
"16: Esc closed the find and every row shows: $($closed.fields.rows -eq 2)"
"16: the cursor stayed on the match: $((SelectionText) -match 'other-16')"

Step "16: Ctrl+P, type, Esc: Quick Open shows and goes, the pane stays"
$shown = (ShellLines 'quick open shown').Count
[Live]::Press($VK.Ctrl, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("target-16"); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\shell16-quick-open-live.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
$quickOpen = WaitShellLines 'quick open shown' $shown
"16: Quick Open showed: $([bool]$quickOpen)"
"16: the workspace pill shows the branch, live-16: $($quickOpen.fields.branch -eq 'live-16')"
"16: Esc left the pane where it was: $((SelectionText) -match 'other-16')"

Step "16: Ctrl+P, type, Enter: the file's folder in the pane, the file under the cursor"
[Live]::Press($VK.Ctrl, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("target-16"); Start-Sleep -Milliseconds 1200
[Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
"16: Enter opened the row in the pane: $((SelectionText) -match 'target-16')"

Step "16: Alt+Left: Back in the left pane"
$commands = (ShellLines 'command executed').Count
[Live]::Press($VK.Alt, $VK.Left); Start-Sleep -Milliseconds 1000
$back = WaitShellLines 'command executed' $commands
"16: Alt+Left ran go.back from a key: $($back.fields.command -eq 'go.back' -and $back.fields.trigger -eq 'key')"
"16: the pane is back in alpha: $((SelectionText) -match 'notes-16|other-16')"

Step "16: the hamburger, clicked by its accessible name; a click outside closes it; again, and Esc closes it"
$menu = ShellElement 'Menu'
"16: the top row has a button named Menu: $([bool]$menu)"
if ($menu) {
  $menus = (ShellLines 'menu shown').Count
  ClickElement $menu; Start-Sleep -Milliseconds 700
  "16: the menu showed: $([bool](WaitShellLines 'menu shown' $menus))"
  $row = AppElement 'New Tab' 2
  "16: its first row is New Tab: $([bool]$row)"
  Shot $h "$ShotDir\shell16-menu-live.png"
  ClickLeftPane
  # Each close counts only when the row was there before it: a row that was never found would "close" at once.
  "16: a click outside closed it: $([bool]$row -and (AppElementGone 'New Tab'))"
  ClickElement $menu; Start-Sleep -Milliseconds 700
  $again = AppElement 'New Tab' 2
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
  "16: Esc closed it: $([bool]$again -and (AppElementGone 'New Tab'))"
}

Step "16: a click on the shell16 crumb takes the pane there"
$crumb = ShellElement $sh
"16: the breadcrumb row has a crumb for shell16: $([bool]$crumb)"
if ($crumb) {
  ClickElement $crumb; Start-Sleep -Milliseconds 1000
  "16: the pane is in shell16: $((SelectionText) -match '\.git|alpha|beta')"
  Shot $h "$ShotDir\shell16-crumb-live.png"
}

# ----- 18: the context menu (docs/ui.md, "The context menu") -----
# The right-click menu is contextMenu of cabinetos.json. This section writes a program (a copy of the F4 stand-in, which
# notes the path it gets in stub-program.js.log), a file menu that shows it, an ID no command has, and shellMenu: true
# into the run's configuration, then drives the menus with the real mouse and keys. The window's log says what
# happened: "context menu shown" (target, keyboard, quick_actions), "context menu closed", "context menu entry left
# out: no command has this ID", "windows menu shown" (items) and "windows menu item run". A menu's rows are found by their accessible names and
# clicked with the real mouse. Windows' Copy is the one item of Windows' menu that runs: it changes nothing on disk,
# and the run reads the clipboard back. The menu's edit mode (step 2) logs "menu edit shown" (target, in_menu_place),
# "menu edit step" (step, row, rows) and "menu edit closed" (saved); the core logs "configuration changed" when Done
# or Ctrl+S writes contextMenu.file.items. The edit mode removes the program and a second edit puts it back, in the
# place the section wrote it, so the checks after it find the file menu as before.
Add-Type -AssemblyName System.Windows.Forms
function LeftPanePoint {
  $rect = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($script:h, 9, [ref]$rect, 16)
  @([int]($rect.Left + ($rect.Right - $rect.Left) * 0.33), [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.45))
}
# A menu item by its accessible name: Windows' menu has a Copy, and the window may have other things called Copy.
function MenuItemElement([string]$name, [double]$seconds = 2) {
  $ae = [System.Windows.Automation.AutomationElement]
  $condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, [int]$script:p.Id)),
    (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    $found = $ae::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($found -or (Get-Date) -ge $deadline) { return $found }
    Start-Sleep -Milliseconds 100
  }
}
function GoLeftPane([string]$path) {
  ClickLeftPane
  [Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
  [Live]::Type($path); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
}
# The core's "configuration changed" lines for the file menu's list, and a wait for the next one (the core logs through a
# writer thread of its own, as the window does).
function CoreMenuChanges { @(Get-Content "$root\logs\core.*.jsonl" -Encoding UTF8 | Where-Object { $_ -match '"configuration changed"' -and $_ -match 'contextMenu\.file\.items' }) }
function WaitCoreMenuChange([int]$before, [int]$seconds = 5) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    $lines = @(CoreMenuChanges)
    if ($lines.Count -gt $before) { return $lines[$lines.Count - 1] }
    if ((Get-Date) -ge $deadline) { return $null }
    Start-Sleep -Milliseconds 200
  }
}
function FileMenuItems { @((Get-Content -LiteralPath "$root\config\cabinetos.json" -Raw -Encoding UTF8 | ConvertFrom-Json).contextMenu.file.items | ForEach-Object { if ($_.separator) { '-' } else { $_.command } }) }
$VK['S'] = 0x53
$VK['Insert'] = 0x2D
$m18 = "$files\menu18"
New-Item -ItemType Directory -Force "$m18\bg18" | Out-Null
foreach ($i in 1..40) { Set-Content -LiteralPath ("$m18\row-{0:D2}.txt" -f $i) -Value "x" -NoNewline }
Set-Content -LiteralPath "$m18\bg18\only-18.txt" -Value "x" -NoNewline
$recorder = "$root\stub-program.js"
Copy-Item -LiteralPath $stub -Destination $recorder
$editMenu = "Edit Menu$([char]0x2026)"

Step "18: the configuration gets a program, a file menu and Windows' menu"
$changes = (ShellLines 'configuration changed').Count
$cfgPath = "$root\config\cabinetos.json"
$cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$program = @{ name = 'live18'; title = 'Live 18 Recorder'; command = 'wscript.exe'; args = [string[]]@('//B', '//Nologo', $recorder, '{path}') }
$cfg | Add-Member -NotePropertyName programs -NotePropertyValue @($program) -Force
$cfg | Add-Member -NotePropertyName contextMenu -NotePropertyValue @{
  shellMenu = $true
  file = @{
    quickActions = [string[]]@('edit.copy', 'file.rename')
    items = @(@{ command = 'program.live18' }, @{ command = 'nothing.here18' }, @{ separator = $true }, @{ command = 'pane.openSelected' })
  }
} -Force
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
"18: the window read the change: $([bool](WaitShellLines 'configuration changed' $changes))"
GoLeftPane $m18
# The listing arrives a moment after the path is typed; the selection's text says when. The cursor lands on the first
# row, which is the folder bg18 (folders sort first), not a row-NN.txt file.
$deadline = (Get-Date).AddSeconds(3)
while (-not ((SelectionText) -match 'bg18|row-\d+\.txt') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
"18: the left pane is in menu18: $((SelectionText) -match 'bg18|row-\d+\.txt')"

Step "18: a right-click on a row: the file menu, the program in it; a click on the program runs it with the row's path"
$x, $y = LeftPanePoint
[Live]::Click($x, $y); Start-Sleep -Milliseconds 400
$row = if ((SelectionText) -match 'row-\d+\.txt') { $Matches[0] } else { '' }
$shown = (ShellLines 'context menu shown').Count
$warned = (ShellLines 'context menu entry left out: no command has this ID').Count
$placedBefore = (ShellLines 'context menu placed').Count
[Live]::RightClick($x, $y, $false)
$menu = WaitShellLines 'context menu shown' $shown
"18: the row under the pointer, $row, got the file menu: $($row -ne '' -and $menu.fields.target -eq 'File' -and -not $menu.fields.keyboard)"
# Explorer's rule: the menu's top-left corner is at the pointer (above it, or to its left, when the menu would not fit). The
# log's numbers are the window's content DIPs; the point is in screen pixels, and the window's frame is the content's origin
# (step 13 measures the rail from it the same way), so the point in DIPs is (pixel - frame) / scale, good to a few pixels.
# The wanted line is the one after this right-click's "context menu shown": a count alone once handed back the line of an
# earlier, keyboard-opened menu (run of 2026-10-01), so the line's time must not be before that menu's.
$shownAt = if ($menu) { $menu.ts } else { '' }
$placed = WaitShellLines 'context menu placed' $placedBefore 5 { param($line) $line.ts -ge $shownAt }
$wr = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$wr, 16)
$askedX = ($x - $wr.Left) / $scale; $askedY = ($y - $wr.Top) / $scale
$nearX = $placed -and ([math]::Abs($placed.fields.left - $askedX) -le 4 -or [math]::Abs($placed.fields.left + $placed.fields.width - $askedX) -le 4)
$nearY = $placed -and ([math]::Abs($placed.fields.top - $askedY) -le 4 -or [math]::Abs($placed.fields.top + $placed.fields.height - $askedY) -le 4)
"18: the menu's corner is at the right-click's point, not centred on it (placed left $($placed.fields.left) top $($placed.fields.top) width $($placed.fields.width) height $($placed.fields.height); point $([math]::Round($askedX, 1)),$([math]::Round($askedY, 1)); flipped up or left only near the window's edge; within 4 px): $([bool]($nearX -and $nearY))"
"18: its icon row is the file's two quick actions: $($menu.fields.quick_actions -eq 2)"
$entry = AppElement 'Live 18 Recorder' 2
"18: the program is in the menu: $([bool]$entry)"
"18: so is $editMenu, last: $([bool](AppElement $editMenu))"
$leftOut = WaitShellLines 'context menu entry left out: no command has this ID' $warned
"18: the ID no command has is left out, with a warning: $($leftOut.fields.command -eq 'nothing.here18')"
Shot $h "$ShotDir\18-file-menu-live.png"
if ($entry) {
  $closes = (ShellLines 'context menu closed').Count
  ClickElement $entry
  "18: the menu closed: $([bool](WaitShellLines 'context menu closed' $closes))"
  # The recorder writes its line a moment after the menu closes, and the file exists before the line is complete: the
  # check reads until the row's path is in it, or 5 s have passed.
  $deadline = (Get-Date).AddSeconds(5)
  $ran = @()
  while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath "$recorder.log") { $ran = @(Get-Content -LiteralPath "$recorder.log" -Encoding Unicode | Where-Object { $_ -ne '' }) }
    if (@($ran | Where-Object { $_ -like "*\menu18\$row" }).Count -ge 1) { break }
    Start-Sleep -Milliseconds 200
  }
  "18: the program got the row's path ($($ran -join ' | ')): $($ran.Count -eq 1 -and $ran[0] -like "*\menu18\$row")"
  "18: the status bar says it started: $((NoticeCount 'Live 18 Recorder: started') -gt 0)"
}

Step "18: Shift+F10: the same menu from the keyboard; Esc closes it"
$shown = (ShellLines 'context menu shown').Count
[Live]::Press($VK.Shift, $VK.F10)
$menu = WaitShellLines 'context menu shown' $shown
$again = AppElement 'Live 18 Recorder' 2
"18: Shift+F10 opened the file menu, from the keyboard: $($menu.fields.target -eq 'File' -and $menu.fields.keyboard -and [bool]$again)"
$closes = (ShellLines 'context menu closed').Count
[Live]::Press($VK.Esc)
"18: Esc closed it: $([bool](WaitShellLines 'context menu closed' $closes) -and (AppElementGone 'Live 18 Recorder'))"

Step "18: Edit Menu... on the file menu: the edit mode in the menu's place; a click outside leaves it open"
$shown = (ShellLines 'context menu shown').Count
[Live]::RightClick($x, $y, $false)
[void](WaitShellLines 'context menu shown' $shown)
$edit = AppElement $editMenu 2
$editShown = (ShellLines 'menu edit shown').Count
$closes = (ShellLines 'menu edit closed').Count
if ($edit) { ClickElement $edit }
$editing = WaitShellLines 'menu edit shown' $editShown
"18: $editMenu turned the file menu into its edit mode, where the menu was: $($editing.fields.target -eq 'File' -and $editing.fields.in_menu_place)"
"18: the edit header names the file menu: $([bool](AppElement 'Editing: File menu' 2))"
Shot $h "$ShotDir\18-edit-mode-live.png"
# Low in the right pane, away from the edit mode: the click is swallowed, and nothing closes.
$rect = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($script:h, 9, [ref]$rect, 16)
[Live]::Click([int]($rect.Left + ($rect.Right - $rect.Left) * 0.85), [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.85)); Start-Sleep -Milliseconds 600
"18: a click outside left it open: $((ShellLines 'menu edit closed').Count -eq $closes -and [bool](AppElement 'Editing: File menu'))"

Step "18: the program's X, then Done: the core writes the list, and the next right-click has no program"
$steps = (ShellLines 'menu edit step').Count
$remove = AppElement 'Remove Live 18 Recorder' 2
if ($remove) { ClickElement $remove }
$step = WaitShellLines 'menu edit step' $steps
"18: the X removed the program's row ($($step.fields.rows)): $($step.fields.step -eq 'remove' -and $step.fields.row -eq 'Live 18 Recorder')"
$changes18 = @(CoreMenuChanges).Count
$done = AppElement 'Done' 2
if ($done) { ClickElement $done }
$closed = WaitShellLines 'menu edit closed' $closes
"18: Done saved and closed the edit mode: $($closed.fields.saved -eq $true)"
"18: the core logged the change of contextMenu.file.items: $([bool](WaitCoreMenuChange $changes18))"
"18: the file's list lost the program ($((FileMenuItems) -join ', ')): $(((FileMenuItems) -join '|') -eq 'nothing.here18|-|pane.openSelected')"
$shown = (ShellLines 'context menu shown').Count
[Live]::RightClick($x, $y, $false)
$menu = WaitShellLines 'context menu shown' $shown
$lastRow = AppElement $editMenu 2
"18: the next right-click shows the file menu without the program: $($menu.fields.target -eq 'File' -and [bool]$lastRow -and -not (AppElement 'Live 18 Recorder'))"

Step "18: a second edit puts the program back: Insert, the prompt, Enter, a drag with the real mouse, Alt+Down, Alt+Up, Ctrl+S"
$editShown = (ShellLines 'menu edit shown').Count
if ($lastRow) { ClickElement $lastRow }
[void](WaitShellLines 'menu edit shown' $editShown)
$prompts = (ShellLines 'prompt shown').Count
[Live]::Press($VK.Insert)
"18: Insert opened Add Command...'s prompt: $([bool](WaitShellLines 'prompt shown' $prompts))"
Start-Sleep -Milliseconds 300
$steps = (ShellLines 'menu edit step').Count
[Live]::Type('Live 18'); Start-Sleep -Milliseconds 500
[Live]::Press($VK.Enter)
$added = WaitShellLines 'menu edit step' $steps
"18: Enter added the program after the focused row ($($added.fields.rows)): $($added.fields.step -eq 'add' -and $added.fields.rows -eq 'nothing.here18|Live 18 Recorder|-|Open')"
$from = AppElement 'Live 18 Recorder' 1
$onto = AppElement 'nothing.here18' 1
if ($from -and $onto) {
  $a = $from.Current.BoundingRectangle; $b = $onto.Current.BoundingRectangle
  $steps = (ShellLines 'menu edit step').Count
  [Live]::Drag([int]($a.Left + 8), [int]($a.Top + $a.Height / 2), [int]($a.Left + 8), [int]($b.Top + $b.Height / 2))
  $dragged = WaitShellLines 'menu edit step' $steps
  "18: a drag put the program first ($($dragged.fields.rows)): $($dragged.fields.step -eq 'drag' -and $dragged.fields.rows -eq 'Live 18 Recorder|nothing.here18|-|Open')"
} else {
  "18: the rows to drag were found: False"
}
$steps = (ShellLines 'menu edit step').Count
[Live]::Press($VK.Alt, $VK.Down)
$down = WaitShellLines 'menu edit step' $steps
[Live]::Press($VK.Alt, $VK.Up)
$up = WaitShellLines 'menu edit step' ($steps + 1)
"18: Alt+Down moved the focused row down and Alt+Up back: $($down.fields.rows -eq 'nothing.here18|Live 18 Recorder|-|Open' -and $up.fields.rows -eq 'Live 18 Recorder|nothing.here18|-|Open')"
$closes = (ShellLines 'menu edit closed').Count
$changes18 = @(CoreMenuChanges).Count
[Live]::Press($VK.Ctrl, $VK.S)
$closed = WaitShellLines 'menu edit closed' $closes
"18: Ctrl+S saved and closed it, and the core logged the change: $($closed.fields.saved -eq $true -and [bool](WaitCoreMenuChange $changes18))"
"18: the file's list is as the section wrote it ($((FileMenuItems) -join ', ')): $(((FileMenuItems) -join '|') -eq 'program.live18|nothing.here18|-|pane.openSelected')"
Shot $h "$ShotDir\18-after-edit-live.png"

Step "18: Shift+right-click on the row: Windows' own menu; its Copy puts the file on the clipboard"
[System.Windows.Forms.Clipboard]::SetText("cabinetos live check 18")
$windows = (ShellLines 'windows menu shown').Count
[Live]::RightClick($x, $y, $true)
# The core builds Windows' menu: the first one takes the shell's handlers a moment to load.
$windowsMenu = WaitShellLines 'windows menu shown' $windows 8
"18: Windows' menu showed, $($windowsMenu.fields.items) items, the core answered in $($windowsMenu.fields.reply_ms) ms: $($windowsMenu.fields.items -gt 5)"
$copy = MenuItemElement 'Copy'
"18: it has Copy: $([bool]$copy)"
Shot $h "$ShotDir\18-windows-menu-live.png"
if ($copy) {
  $runs = (ShellLines 'windows menu item run').Count
  ClickElement $copy
  "18: the core ran the item: $([bool](WaitShellLines 'windows menu item run' $runs))"
  Start-Sleep -Milliseconds 500
  $dropped = @([System.Windows.Forms.Clipboard]::GetFileDropList())
  "18: the clipboard holds the row's file ($($dropped -join ' | ')): $($dropped.Count -eq 1 -and $dropped[0] -like "*\menu18\$row")"
} else {
  [Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
}

Step "18: a right-click on the empty space: the folder's menu"
GoLeftPane "$m18\bg18"
$shown = (ShellLines 'context menu shown').Count
[Live]::RightClick($x, $y, $false)
$menu = WaitShellLines 'context menu shown' $shown
$newFolder = AppElement 'New folder' 2
"18: the empty space got the folder menu, with New folder: $($menu.fields.target -eq 'Background' -and [bool]$newFolder)"
Shot $h "$ShotDir\18-background-menu-live.png"
$closes = (ShellLines 'context menu closed').Count
[Live]::Press($VK.Esc)
"18: Esc closed it: $([bool](WaitShellLines 'context menu closed' $closes) -and (AppElementGone 'New folder'))"

# ----- 19: column widths (docs/ui.md, "Column widths") -----
# A real drag of the left pane's Modified|Type grip by 40 px, then a real double-click on its Type heading, with the real
# mouse. The grip is 8 px wide and centred on the divider, and the default look has no gap between the columns, so the grip
# is where the Type heading's text starts: the drag presses 1 px left of that. The headings are found by UI Automation (text
# elements named "Modified" and "Type"; the left pane's are the ones furthest left). The window's log says what happened:
# "columns changed" (how: drag, fit or reset; the widths) and "columns saved". The palette's Reset Column Widths puts the
# theme's widths back, so the file ends as the run found it.
function HeadingElement([string]$name) {
  $ae = [System.Windows.Automation.AutomationElement]
  $condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  $mine = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, [int]$script:p.Id)
  $found = foreach ($window in $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
    $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 }
  }
  $found | Sort-Object { $_.Current.BoundingRectangle.Left } | Select-Object -First 1
}
function ColumnsChanged([string]$how, [int]$before, [int]$seconds = 5) {
  $line = WaitShellLines 'columns changed' $before $seconds { param($line) $line.fields.how -eq $how }
  if ($line -and $line.fields.how -eq $how) { $line }
}
$c19 = "$files\columns19"
New-Item -ItemType Directory -Force "$c19\a folder" | Out-Null
foreach ($name in 'notes.txt', 'readme.md', 'data.json', 'script.ps1') { Set-Content -LiteralPath "$c19\$name" -Value "x" -NoNewline }

Step "19: the left pane in a folder of four files; the Modified|Type grip dragged 40 px right with the real mouse"
GoLeftPane $c19
$modifiedHeading = HeadingElement 'Modified'
$typeHeading = HeadingElement 'Type'
if ($modifiedHeading -and $typeHeading) {
  $m0 = $modifiedHeading.Current.BoundingRectangle; $t0 = $typeHeading.Current.BoundingRectangle
  $oldModified = ($t0.Left - $m0.Left) / $scale
  $gripX = [int]($t0.Left - 1); $gripY = [int]($t0.Top + $t0.Height / 2)
  Step "19: Modified is $([Math]::Round($oldModified, 1)) px wide; the grip at $gripX,$gripY"
  $changes = (ShellLines 'columns changed').Count
  $saves = (ShellLines 'columns saved').Count
  [Live]::Drag($gripX, $gripY, [int]($gripX + 40 * $scale), $gripY)
  $dragged = ColumnsChanged 'drag' $changes
  $saved = WaitShellLines 'columns saved' $saves
  "19: the drag changed the columns ($($dragged.fields.modified)/$($dragged.fields.type)/$($dragged.fields.size), Name $($dragged.fields.name)): $([bool]$dragged)"
  "19: Modified is 40 px wider ($([Math]::Round($oldModified, 1)) -> $($dragged.fields.modified)): $($dragged -and [Math]::Abs($dragged.fields.modified - $oldModified - 40) -le 2)"
  "19: the widths were saved ($($saved.fields.columns)): $([bool]$saved)"
  $t1 = (HeadingElement 'Type').Current.BoundingRectangle
  "19: the Type heading moved with the divider ($([Math]::Round(($t1.Left - $t0.Left) / $scale, 1)) px): $([Math]::Abs(($t1.Left - $t0.Left) / $scale - 40) -le 2)"
  Shot $h "$ShotDir\19-dragged-live.png"

  Step "19: a double-click on the Type heading fits Type to its widest text on screen"
  $changes = (ShellLines 'columns changed').Count
  $x19 = [int]($t1.Left + $t1.Width / 2); $y19 = [int]($t1.Top + $t1.Height / 2)
  [Live]::Click($x19, $y19); [Live]::Click($x19, $y19)
  $fit = ColumnsChanged 'fit' $changes
  $fitted = ShellLines 'columns fitted' | Select-Object -Last 1
  "19: the double-click fitted Type ('$($fitted.fields.type_text)', $($fitted.fields.type_text_width) px, fit $($fitted.fields.type_fit)) to $($fit.fields.type) px: $([bool]$fit -and $fitted.fields.column -eq 'type')"
  Shot $h "$ShotDir\19-fitted-live.png"
} else {
  "19: the Modified and Type headings were found by UI Automation: False"
}

Step "19: the palette's Reset Column Widths gives the theme's widths back"
$changes = (ShellLines 'columns changed').Count
$saves = (ShellLines 'columns saved').Count
[Live]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 500
[Live]::Type("reset column widths"); Start-Sleep -Milliseconds 700
[Live]::Press($VK.Enter)
$reset = ColumnsChanged 'reset' $changes
$saved = WaitShellLines 'columns saved' $saves
"19: the reset ran ($($reset.fields.modified)/$($reset.fields.type)/$($reset.fields.size)), and the file has no widths: $([bool]$reset -and [bool]$saved -and $null -eq (ConfigUi).columns)"
Shot $h "$ShotDir\19-reset-live.png"

Step "close"
$script:h = $null
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000)
Start-Sleep -Milliseconds 500
$corePids = Get-Content "$root\logs\ui.*.jsonl" | Where-Object { $_ -match '"core started"' } | ForEach-Object { ($_ | ConvertFrom-Json).fields.pid }
"app exited: $($p.HasExited), code $($p.ExitCode); cores this app started: $($corePids -join ',')"
foreach ($c in $corePids) { "core $c exited with the window: $(-not [bool](Get-Process -Id $c -ErrorAction SilentlyContinue))" }
"all cabinetos-core processes now (other agents may run their own): '$((Get-Process cabinetos-core -ErrorAction SilentlyContinue).Id -join ',')'"
$config = Get-Content "$root\config\cabinetos.json" -Raw | ConvertFrom-Json
"config keybindings:"; $config.keybindings | ConvertTo-Json -Compress
"config ui.lastPaths: $($config.ui.lastPaths -join ' | '); ui.dualPane: $($config.ui.dualPane)"
$ui = Get-Content "$root\logs\ui.*.jsonl"
$job = $ui | Where-Object { $_ -match '"job started"' } | Select-Object -First 1
if ($job) {
  $id = ($job | ConvertFrom-Json).request_id
  "one start_job, request_id $id, in the UI log:"; $ui | Where-Object { $_ -match $id }
  "and in the core log:"; Get-Content "$root\logs\core.*.jsonl" | Where-Object { $_ -match $id }
}
Step "done"
if ($Strict -and -not $script:scrollGoal) { "STRICT: the scroll goal was not met"; exit 1 }
