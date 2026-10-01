# Claude Code in the window's terminal, with real keys (docs/terminal.md, "Profiles": the claude profile;
# docs/extensions/agent.md, "Claude Code in the terminal"). A fresh window on -Folder (a folder Claude Code
# already trusts, so no trust question comes first), a terminal tab with the claude profile opened through
# the core's pipe (terminal.new with {"profile":"claude","cwd":…}, as the dock's profile list would), then
# keys into Claude Code as a user types them: a long answer cut short with Esc, a prompt that makes it write
# a file (its own permission question gets Enter), a folder change in the pane (the sync must leave the
# tab alone: decision SkipProfile), Ctrl+Alt+P (the pane's path lands in its input), and /exit. Two short
# prompts go to the Claude subscription of whoever is logged in to Claude Code on this PC.
#
# It takes the keyboard and the mouse for about two minutes: run it on an unlocked, awake screen that
# nobody uses. It stops when another window comes in front. Each check prints "text: True/False";
# screenshots go to -ShotDir, and a DONE file into -Io (default: _io\live-check next to the repository)
# opens in Notepad at the end, the sign that the keyboard is free again. Exit code 0 when every check is
# True, 2 otherwise, 1 when it could not run.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\claude-terminal.ps1
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Cli = "$PSScriptRoot\..\..\core\target\release\cabinetos-cli.exe",
  # The folder the panes and Claude Code start in: the repository, which Claude Code trusts on the development PC.
  [string]$Folder = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
  [string]$ShotDir = "$env:TEMP\cabinetos-ui-test\claude-shots",
  [string]$Io = ''
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
$Cli = [System.IO.Path]::GetFullPath($Cli)
$Folder = [System.IO.Path]::GetFullPath($Folder)
foreach ($needed in $Exe, $Core, $Cli) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing: build it first"; exit 1 } }
$claude = Get-Command claude.exe -ErrorAction SilentlyContinue
if (-not $claude) { "STOP: claude.exe is not on the PATH: install Claude Code first"; exit 1 }
Add-Type -AssemblyName System.Drawing
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
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
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
  public static void Front(IntPtr h) { Send(Key(0x12, false), Key(0x12, true)); ShowWindow(h, 9); SetForegroundWindow(h); }
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
  // Milliseconds since the last key press or mouse move of the person at the PC.
  public static uint IdleMilliseconds() { var info = new LASTINPUTINFO { cbSize = 8 }; GetLastInputInfo(ref info); return (uint)Environment.TickCount - info.dwTime; }
}
"@
. "$PSScriptRoot\countdown.ps1"
if (-not (Show-InputCountdown -Seconds 5 -What 'The Claude Code probe')) { 'cancelled at the countdown: nothing ran'; exit 2 }
# The probe takes the keyboard only once nobody has touched the PC for 12 s (it waits up to 10 min), so a
# person reading the chat next to it is not interrupted mid-click.
$waited = 0
while ([Live]::IdleMilliseconds() -lt 12000 -and $waited -lt 600) { Start-Sleep -Seconds 2; $waited += 2 }
"idle for $([Live]::IdleMilliseconds()) ms after waiting $waited s"
[void][Live]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$VK = @{ Ctrl = 0x11; Shift = 0x10; Alt = 0x12; P = 0x50; Esc = 0x1B; Enter = 0x0D; Back = 0x08; Backquote = 0xC0 }
$script:falses = 0
function Check([string]$text, [bool]$ok) { "{0}: {1}" -f $text, $ok; if (-not $ok) { $script:falses++ } }
# A step line; before it, the window must be in front, or the keys would go elsewhere: then the run stops.
function Step([string]$text) {
  if ($script:h -and [Live]::ForegroundPid() -ne [uint32]$script:p.Id) {
    $front = Get-Process -Id ([Live]::ForegroundPid()) -ErrorAction SilentlyContinue
    "{0:HH:mm:ss.fff} STOP: CabinetOS is not the foreground window before '{1}' (in front: {2} pid {3}, '{4}')" -f (Get-Date), $text, $front.ProcessName, $front.Id, $front.MainWindowTitle
    if ($script:p -and -not $script:p.HasExited) { [void]$script:p.CloseMainWindow(); [void]$script:p.WaitForExit(8000) }
    Finish 1
  }
  "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
}
function Shot([IntPtr]$h, [string]$path) {
  $r = New-Object Live+RECT; [void][Live]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Step "screenshot $path"
}
function UiLines([string]$pattern) { @(Get-Content "$root\logs\ui.*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern }) }
function CoreLines([string]$pattern) { @(Get-Content "$root\logs\core.*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern }) }
# Waits until a log line matching the pattern appears beyond the first $before, up to $seconds.
function WaitLog([scriptblock]$lines, [int]$before, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) { if ((& $lines).Count -gt $before) { return $true }; Start-Sleep -Milliseconds 300 }
  return $false
}
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$io = if ($Io) { $Io } else { Join-Path (Split-Path $repo -Parent) '_io\live-check' }
function Finish([int]$code) {
  New-Item -ItemType Directory -Force $io | Out-Null
  $done = @(
    "# Claude Code probe finished: you can use the keyboard and mouse again",
    "",
    "Ended $((Get-Date).ToString('HH:mm:ss')), exit code $code, checks that answered False: $script:falses.",
    "Screenshots: $ShotDir"
  )
  $done | Set-Content -LiteralPath "$io\DONE.md" -Encoding UTF8
  Start-Process notepad.exe -ArgumentList "`"$io\DONE.md`""
  exit $code
}

$root = "$env:TEMP\cabinetos-ui-test\claude"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\config", "$root\logs", $ShotDir | Out-Null
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
# Both panes on -Folder, the claude profile as the default one (so Ctrl+` starts Claude Code in the active
# pane's folder, the way a user who set it in cabinetos.json gets it), and a dock tall enough for its screen.
$config = @{ version = 1; logging = @{ level = 'debug' }; terminal = @{ defaultProfile = 'claude' }; ui = @{ tabs = @{ left = @{ items = @(@{ path = $Folder; locked = $false }) }; right = @{ items = @(@{ path = $Folder; locked = $false }) } }; dockSize = @{ bottom = 420 } } } | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($env:CABINETOS_CONFIG, $config, (New-Object System.Text.UTF8Encoding $false))
$env:CABINETOS_LOG_DIR = "$root\logs"
# The window's "cwd sync" line (the decision for each folder change) is written at debug level.
$env:CABINETOS_LOG = 'debug'
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
$env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
# Claude Code refuses to start inside another Claude Code session, which it tells by these variables: a run started
# from such a session must not pass them on to the window, the core and the shell.
Get-ChildItem Env: | Where-Object { $_.Name -like 'CLAUDE*' } | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process') }
$target = "$root\claude-wrote-this.txt"

if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }
Step "start"
$p = Start-Process -FilePath $Exe -PassThru
"app pid: $($p.Id)"
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
[Live]::Front($h); Start-Sleep -Milliseconds 800
Check "the window is in front" ([Live]::GetForegroundWindow() -eq $h)
$started = UiLines '"core started"' | Select-Object -Last 1
$token = if ($started) { ($started | ConvertFrom-Json).fields.pipe_token } else { '' }
Check "the window logged its core's pipe token" ([bool]$token)
if (-not $token) { [void]$p.CloseMainWindow(); Finish 1 }

Step "Ctrl+Backquote: the terminal opens with the default profile, claude, in the active pane's folder"
$handed = (UiLines '"a page has the keyboard"').Count
[Live]::Press($VK.Ctrl, $VK.Backquote)
Check "the core opened a session with the claude profile" (WaitLog { CoreLines '"terminal session opened".*"profile":"claude"' } 0 20)
$opened = CoreLines '"terminal session opened".*"profile":"claude"' | Select-Object -Last 1
Check "the session started in the pane's folder" ($opened -and (($opened | ConvertFrom-Json).fields.cwd -eq $Folder))
Check "the terminal's page got the keyboard" (WaitLog { UiLines '"a page has the keyboard".*"page":"terminal"' } $handed 15)
Start-Sleep -Seconds 8
Shot $h "$ShotDir\claude-1-started.png"

Step "a long answer, cut short with Esc"
[Live]::Type("Count from 1 to 400, one number per line, with no other text."); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Seconds 6
[Live]::Press($VK.Esc); Start-Sleep -Seconds 2
Shot $h "$ShotDir\claude-2-after-esc.png"
Check "Claude Code is still running after Esc (its session has not exited)" ((CoreLines '"terminal shell exited"').Count -eq 0)

Step "a prompt that makes it write a file; Enter answers its permission question"
[Live]::Type("Create the file $target with the single line OK, then reply with the word done."); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter)
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $target)) {
  Start-Sleep -Seconds 4
  if (-not (Test-Path -LiteralPath $target)) { Step "Enter (for the permission question, if it is on screen)"; [Live]::Press($VK.Enter) }
}
Start-Sleep -Seconds 3
$wrote = (Test-Path -LiteralPath $target) -and ((Get-Content -LiteralPath $target -Raw) -match 'OK')
Check "Claude Code wrote the file it was asked for (keys reached it, the model answered, its tool ran)" $wrote
Shot $h "$ShotDir\claude-3-file-written.png"

Step "the pane goes up one folder; the sync must leave the claude tab alone"
$syncs = (UiLines '"cwd sync"').Count
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 600   # the keyboard back to the pane; the dock stays
[Live]::Press($VK.Back); Start-Sleep -Milliseconds 1500                  # up one folder
$seen = WaitLog { UiLines '"cwd sync"' } $syncs 5
$last = UiLines '"cwd sync"' | Select-Object -Last 1
$decision = if ($last) { ($last | ConvertFrom-Json).fields.decision } else { '(no cwd sync line)' }
"the sync's decision for the claude tab: $decision"
Check "the folder sync skipped the claude tab (decision SkipProfile)" ($seen -and $decision -eq 'SkipProfile')
Check "no sync ever decided to type into the claude tab (no cwd sync line with decision Sync)" ((UiLines '"cwd sync".*"decision":"Sync"').Count -eq 0)

# Ctrl+Alt+P is a pane key (terminal.insertPath; Ctrl+P until Phase 16, which gave Ctrl+P to Quick Open): pressed in
# the pane it shows the terminal, types the pane's folder at the prompt and hands the keyboard to the page.
Step "Ctrl+Alt+P from the pane: its folder typed into Claude Code's input, then removed"
$typed = (UiLines '"paths typed at the prompt"').Count
[Live]::Press($VK.Ctrl, $VK.Alt, $VK.P); Start-Sleep -Seconds 2
Check "the path was typed at Claude Code's prompt" (WaitLog { UiLines '"paths typed at the prompt"' } $typed 5)
Check "the terminal's page has the keyboard again after Ctrl+Alt+P" ((UiLines '"a page has the keyboard".*"page":"terminal"').Count -gt $handed)
Shot $h "$ShotDir\claude-4-path-typed.png"
# The typed path is quoted and the cursor sits after it: Backspace once per character, and a few more for safety.
$parent = Split-Path $Folder -Parent
for ($i = 0; $i -lt ($parent.Length + 12); $i++) { [Live]::Press($VK.Back); Start-Sleep -Milliseconds 10 }
Start-Sleep -Milliseconds 500

Step "/exit ends Claude Code; the tab closes"
$exited = (CoreLines '"terminal shell exited"').Count
[Live]::Type("/exit"); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter)
Check "Claude Code exited with code 0 after /exit" (WaitLog { CoreLines '"terminal shell exited".*"exit_code":0' } $exited 20)
Check "the window closed the tab (the core forgot the session)" (WaitLog { CoreLines '"terminal session closed"' } 0 10)
Start-Sleep -Seconds 1
Shot $h "$ShotDir\claude-5-exited.png"

Step "close the window"
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(10000)
if (-not $p.HasExited) { $p.Kill() }
"checks that answered False: $script:falses"
Finish $(if ($script:falses -eq 0) { 0 } else { 2 })
