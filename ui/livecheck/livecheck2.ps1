# The second live check (docs/ui.md, "The live check"): the input paths a user has, with real
# keys and clicks. The same rules as livecheck.ps1: an unlocked screen you are watching; it stops
# the moment another window comes to the front.
#  1. Skip on the conflict card by a real mouse click: where does the keyboard focus go?
#     Then Properties: Esc closes it, and Ctrl+Backquote runs nothing while it is open.
#  2. Typing in the terminal with ordinary virtual-key events (a physical keyboard) versus Unicode injection.
#  3. Ctrl+K V on a file that Markdown Preview already shows.
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Tools = "$PSScriptRoot\..\..\sdk\tools",
  [string]$ShotDir = "$env:TEMP\cabinetos-ui-test\live-shots-2",
  [string]$Run = "live2"
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
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  static INPUT Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.dwFlags = up ? 2u : 0u; return i; }
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
}
"@
[void][Live]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$VK = @{ Ctrl = 0x11; Shift = 0x10; Alt = 0x12; Esc = 0x1B; Tab = 0x09; Enter = 0x0D; Down = 0x28; F5 = 0x74; F10 = 0x79; Home = 0x24; Backquote = 0xC0; L = 0x4C; K = 0x4B; V = 0x56 }
function Step($text) {
  if ($script:h -and [Live]::GetForegroundWindow() -ne $script:h) {
    "{0:HH:mm:ss.fff} STOP: CabinetOS is not the foreground window before '{1}'" -f (Get-Date), $text
    if ($script:p -and -not $script:p.HasExited) { [void]$script:p.CloseMainWindow(); [void]$script:p.WaitForExit(8000) }
    exit 1
  }
  "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
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
# Ordinary key events, as a physical keyboard sends them: letters, digits, space and hyphen only.
function TypeVk([string]$text) {
  foreach ($c in $text.ToCharArray()) {
    if ($c -match '[a-z]') { [Live]::Press([uint16][int][char]::ToUpper($c)) }
    elseif ($c -match '[0-9]') { [Live]::Press([uint16][int]$c) }
    elseif ($c -eq ' ') { [Live]::Press([uint16]0x20) }
    elseif ($c -eq '-') { [Live]::Press([uint16]0xBD) }
    else { throw "no virtual key for '$c'" }
    Start-Sleep -Milliseconds 30
  }
}
function Focused() {
  try {
    $e = [System.Windows.Automation.AutomationElement]::FocusedElement
    "focused element: '$($e.Current.Name)' ($($e.Current.ControlType.ProgrammaticName), class $($e.Current.ClassName))"
  } catch { "focused element: unknown ($($_.Exception.Message))" }
}

$root = "$env:TEMP\cabinetos-ui-test\$Run"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\config", "$root\logs", $ShotDir | Out-Null
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
$env:CABINETOS_TOOLS_DIR = [System.IO.Path]::GetFullPath($Tools)
Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }

$files = "$root\files"; $src = "$files\src"; $dst = "$files\dst"
New-Item -ItemType Directory -Force $src, $dst | Out-Null
Set-Content -LiteralPath "$src\readme.md" -Value "# notes" -NoNewline
Set-Content -LiteralPath "$src\report.txt" -Value "new text" -NoNewline
Set-Content -LiteralPath "$dst\report.txt" -Value "older text!!" -NoNewline

Step "start"
$p = Start-Process -FilePath $Exe -PassThru
"app pid: $($p.Id)"
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
[Live]::Front($h); Start-Sleep -Milliseconds 800
"foreground ok: $([Live]::GetForegroundWindow() -eq $h)"
Step "the window is in front"

Step "left pane: src; right pane: dst"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($src); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($dst); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Tab); Start-Sleep -Milliseconds 400

# Rows in src: readme.md, report.txt.
Step "1. F5 on report.txt, then Skip by a real mouse click"
[Live]::Press($VK.Home); [Live]::Press($VK.Down); Start-Sleep -Milliseconds 300
[Live]::Press($VK.F5); Start-Sleep -Milliseconds 2000
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Skip")
$skip = [System.Windows.Automation.AutomationElement]::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($skip) {
  $r = $skip.Current.BoundingRectangle
  [Live]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Step "Skip clicked with the mouse at $([int]($r.X + $r.Width / 2)),$([int]($r.Y + $r.Height / 2))"
} else { Step "no Skip button found" }
Start-Sleep -Milliseconds 2000
Focused
Shot $h "$ShotDir\1-after-skip-click.png"
"F5 with Skip left the destination's file alone: $((Get-Content -LiteralPath "$dst\report.txt" -Raw) -eq 'older text!!')"

Step "1b. Shift+F10 right after the decision: the context menu of the focused row?"
[Live]::Press($VK.Shift, $VK.F10); Start-Sleep -Milliseconds 800
Shot $h "$ShotDir\1b-context-menu-after-skip.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 500
Step "1c. Alt+Enter: Properties, or does it press the Back button?"
[Live]::Press($VK.Alt, $VK.Enter); Start-Sleep -Milliseconds 1000
Shot $h "$ShotDir\1c-alt-enter-after-skip.png"
Step "1d. Ctrl+Backquote with Properties open: nothing may run; then Esc closes it"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 800
Shot $h "$ShotDir\1d-properties-still-open.png"
[Live]::Press($VK.Esc); Start-Sleep -Milliseconds 700
Shot $h "$ShotDir\1e-properties-closed.png"
$log = Get-Content "$root\logs\ui.*.jsonl"
"Esc closed the dialog: $([bool]($log | Where-Object { $_ -match '"dialog closed"' }))"
"no command ran while the dialog was open: $(NothingRanUnderDialog $log)"

Step "2. Ctrl+Backquote, then typing with ordinary key events"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Seconds 3
TypeVk "echo live-5c"; [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\2-terminal-virtual-keys.png"
Step "2b. the same line typed with Unicode injection (as the touch keyboard sends it)"
[Live]::Type("echo unicode-5c"); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1200
Shot $h "$ShotDir\2b-terminal-unicode.png"
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 600
[Live]::Press($VK.Ctrl, $VK.Backquote); Start-Sleep -Milliseconds 800

Step "3. Enter on readme.md: Markdown Preview; then Ctrl+K V on the same file"
[Live]::Press($VK.Ctrl, $VK.L); Start-Sleep -Milliseconds 400
[Live]::Type($src); [Live]::Press($VK.Enter); Start-Sleep -Milliseconds 1000
[Live]::Press($VK.Home); Start-Sleep -Milliseconds 300
[Live]::Press($VK.Enter); Start-Sleep -Seconds 3
Shot $h "$ShotDir\3-markdown-enter.png"
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.V); Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\3b-markdown-chord.png"
Step "3c. Ctrl+K V again, 2 s later"
[Live]::Press($VK.Ctrl, $VK.K); Start-Sleep -Milliseconds 150
[Live]::Press($VK.V); Start-Sleep -Milliseconds 2000
Shot $h "$ShotDir\3c-markdown-chord-again.png"

Step "close"
$script:h = $null
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000)
"app exited: $($p.HasExited), code $($p.ExitCode)"
$ui = Get-Content "$root\logs\ui.*.jsonl"
"UI log lines about the preview tool and the chord:"
$ui | Where-Object { $_ -match 'webview|tool|editor\.openMarkdownPreview|virtual' } | Select-Object -Last 25
"UI log: warnings and errors:"
$ui | Where-Object { $_ -match '"level":"(WARN|ERROR)"' }
Step "done"
