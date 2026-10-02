# Takes showcase screenshots of the CabinetOS window for the landing page: the two panes over the repository, the
# command palette, the terminal, the marketplace and the Commander Compact theme. It presses real keys, so it runs on a
# machine whose desktop is free: the Omen laptop through remote-script.ps1, or this PC with the creator's consent
# (CLAUDE.md, "Nothing opens a CabinetOS window on this PC without the creator's consent"). The window starts with a
# fresh configuration under $env:TEMP, so the machine's real settings stay as they are, and the marketplace page reads
# the real index from the site. Each shot is the window's own frame (DWM's extended frame bounds), nothing around it.
# Windows PowerShell 5.1 and PowerShell 7.
#
#   remote-script.ps1 -Script ui\livecheck\showcase-shots.ps1 -Args "-ShotDir C:\Dev\cabinetos\_io\script-runs\showcase -Left C:\Dev\cabinetos\cabinetos"
#   showcase-shots.ps1 -ShotDir E:\...\_io\landing-shots -Left E:\...\cabinetos
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$ShotDir = "$env:TEMP\cabinetos-showcase",
  [string]$Left = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
  [string]$Right = '',
  [string]$TerminalCommand = 'cab pane'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 6) { $env:PSModulePath = "$PSHOME\Modules;$env:PSModulePath" }
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
$Left = [System.IO.Path]::GetFullPath($Left)
if (-not $Right) { $Right = Join-Path $Left 'docs' }
foreach ($needed in $Exe, $Core, $Left, $Right) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing"; exit 1 } }
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class Show {
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
  public static void Front(IntPtr h) { Send(Key(0x12, false), Key(0x12, true)); ShowWindow(h, 9); SetForegroundWindow(h); }
}
"@
[void][Show]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$VK = @{ Ctrl = 0x11; Shift = 0x10; P = 0x50; L = 0x4C; K = 0x4B; T = 0x54; Esc = 0x1B; Tab = 0x09; Enter = 0x0D; Down = 0x28; Home = 0x24; Backquote = 0xC0 }
function Step($text) {
  if ($script:h -and [Show]::ForegroundPid() -ne [uint32]$script:p.Id) {
    "{0:HH:mm:ss.fff} STOP: CabinetOS is not the foreground window before '{1}'" -f (Get-Date), $text
    if ($script:p -and -not $script:p.HasExited) { [void]$script:p.CloseMainWindow(); [void]$script:p.WaitForExit(8000) }
    exit 1
  }
  "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
}
function Shot([string]$name) {
  $r = New-Object Show+RECT; [void][Show]::DwmGetWindowAttribute($script:h, 9, [ref]$r, 16)
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $path = Join-Path $ShotDir "$name.png"
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Step "screenshot $path ($($bmp.Width)x$($bmp.Height))"
}
function GoPath([string]$path) {
  [Show]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 700
  [Show]::Type($path); Start-Sleep -Milliseconds 300
  [Show]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
}
function Palette([string]$text) {
  [Show]::Press($VK.Ctrl, $VK.Shift, $VK.P); Start-Sleep -Milliseconds 800
  [Show]::Type($text); Start-Sleep -Milliseconds 1000
}

$root = "$env:TEMP\cabinetos-showcase-run"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\config", "$root\logs", $ShotDir | Out-Null
Get-ChildItem -LiteralPath $ShotDir -Filter *.png | Remove-Item -Force
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
[System.IO.File]::WriteAllText($env:CABINETOS_CONFIG, '{"version":1}', (New-Object System.Text.UTF8Encoding $false))
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
$env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }

Step "start $Exe"
$p = Start-Process -FilePath $Exe -PassThru
"app pid: $($p.Id)"
trap {
  "{0:HH:mm:ss.fff} STOP: {1} (line {2})" -f (Get-Date), $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber
  if ($script:p -and -not $script:p.HasExited) { [void]$script:p.CloseMainWindow(); if (-not $script:p.WaitForExit(8000)) { $script:p.Kill() } }
  exit 1
}
# As the live check does: the window needs a few seconds before its main handle is the real one (an early handle is a
# window that never shows); then it is brought to the front, repeated until its process is in front, then maximized.
Start-Sleep -Seconds 6
$deadline = (Get-Date).AddSeconds(40)
do { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { "STOP: no window within 46 s"; exit 1 }
$h = $p.MainWindowHandle
foreach ($try in 1..8) {
  [Show]::Front($h); Start-Sleep -Milliseconds 800
  if ([Show]::ForegroundPid() -eq [uint32]$p.Id) { break }
  "front try $try failed; in front: pid $([Show]::ForegroundPid())"
}
[void][Show]::ShowWindow($h, 3); Start-Sleep -Milliseconds 600
[Show]::Front($h); Start-Sleep -Milliseconds 1500
"foreground ok: $([Show]::ForegroundPid() -eq [uint32]$p.Id)"

Step "1: the two panes, $Left and $Right"
GoPath $Left
[Show]::Press($VK.Tab); Start-Sleep -Milliseconds 500
GoPath $Right
[Show]::Press($VK.Tab); Start-Sleep -Milliseconds 500
foreach ($i in 1..3) { [Show]::Press($VK.Down); Start-Sleep -Milliseconds 150 }
Start-Sleep -Milliseconds 1500
Shot '01-dual-pane'

Step "2: the command palette"
Palette 'copy'
Shot '02-command-palette'
[Show]::Press($VK.Esc); Start-Sleep -Milliseconds 700

Step "3: the terminal"
[Show]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 6000
[Show]::Type($TerminalCommand); [Show]::Press($VK.Enter); Start-Sleep -Milliseconds 3000
Shot '03-terminal'
[Show]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 1000

Step "4: the marketplace"
Palette 'marketplace'
[Show]::Press($VK.Enter); Start-Sleep -Milliseconds 7000
Shot '04-marketplace'
Palette 'explorer'
[Show]::Press($VK.Enter); Start-Sleep -Milliseconds 1500

Step "5: Commander Compact (Ctrl+K Ctrl+T, Home, Down, Enter)"
[Show]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 200
[Show]::Press($VK.Ctrl, $VK.T); Start-Sleep -Milliseconds 1000
[Show]::Press($VK.Home); Start-Sleep -Milliseconds 200
[Show]::Press($VK.Down); Start-Sleep -Milliseconds 500
[Show]::Press($VK.Enter); Start-Sleep -Milliseconds 2500
Shot '05-commander-compact'

Step "close"
[void]$p.CloseMainWindow()
if (-not $p.WaitForExit(10000)) { $p.Kill() }
"done: $(@(Get-ChildItem -LiteralPath $ShotDir -Filter *.png).Count) shots in $ShotDir"
