# The click-focus probe (docs/log/2026-10-02/terminal-click-focus-report.md). With real keys and the real mouse: the
# terminal takes the keyboard and a typed line runs; a half-typed line waits in the shell; a click goes into the left
# pane; Home, Enter and Backspace follow. It prints where Windows sends the keys after the click (the focus window of the
# window's thread, its class and process), whether the keys acted on the pane (the folder's listing came) or reached
# the shell (the half-typed line, which writes a file, ran), and the window's heavy log lines between the click and
# the last key: XAML's focus moves ("focus changed", with the window that gets the keys), the keys the window saw
# ("key pressed"), the page hand-over lines. Then a click into the terminal's text and a typed line, which the shell
# must run. About 40 s with a window of its own (its configuration, logs and WebView2 data in
# %TEMP%\cabinetos-ui-test\click-probe, heavy logging on). It takes the keyboard and the mouse: the countdown window
# first (CLAUDE.md, "Working rules"), a DONE.md in -Io at the end, opened in Notepad, and it stops the moment another
# window comes to the front. Start it only after ui\livecheck\wait-for-pc.ps1 said the PC is free.
# Runs in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\click-focus-probe.ps1 -Tag before -Io <_io\live-check>
param(
  [string]$Exe = "$PSScriptRoot\..\CabinetOS\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\CabinetOS.exe",
  [string]$Core = "$PSScriptRoot\..\..\core\target\release\cabinetos-core.exe",
  [string]$Tag = (Get-Date -Format 'yyyy-MM-dd-HHmm'),
  [string]$Io = '',
  [switch]$NoCountdown
)
$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Core = [System.IO.Path]::GetFullPath($Core)
foreach ($needed in $Exe, $Core) { if (-not (Test-Path -LiteralPath $needed)) { "STOP: $needed is missing: build it first"; exit 1 } }
. "$PSScriptRoot\paths.ps1"
$io = if ($Io) { $Io } else { Join-Path (Get-IoFolder) 'live-check' }
New-Item -ItemType Directory -Force $io | Out-Null
$out = "$io\probe-click-$Tag.txt"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class Probe {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint data; public uint dwFlags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize; public int flags; public IntPtr active; public IntPtr focus; public IntPtr capture; public IntPtr menuOwner; public IntPtr moveSize; public IntPtr caret; public RECT caretRect; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int size);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  // The window that gets the keys now: the focus window of the window's own thread, by class and process.
  public static string KeysTo(IntPtr h) {
    uint pid; uint thread = GetWindowThreadProcessId(h, out pid);
    var info = new GUITHREADINFO(); info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
    if (!GetGUIThreadInfo(thread, ref info) || info.focus == IntPtr.Zero) return "none";
    var name = new StringBuilder(256); GetClassName(info.focus, name, 256);
    uint focusPid; GetWindowThreadProcessId(info.focus, out focusPid);
    return name.ToString() + " (" + (focusPid == pid ? "the window's process" : "process " + focusPid) + ")";
  }
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
}
"@
[void][Probe]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$VK = @{ Ctrl = 0x11; Enter = 0x0D; Back = 0x08; Home = 0x24; Backquote = 0xC0; C = 0x43 }

$root = "$env:TEMP\cabinetos-ui-test\click-probe"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
$probe = "$root\files\probe"
New-Item -ItemType Directory -Force "$root\config", "$root\logs", "$root\tools", "$probe\inner" | Out-Null
Set-Content -LiteralPath "$probe\zz.txt" -Value 'probe'
$env:CABINETOS_CONFIG = "$root\config\cabinetos.json"
$config = @{ version = 1; ui = @{ lastPaths = [string[]]@($probe, $probe) } } | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($env:CABINETOS_CONFIG, $config, (New-Object System.Text.UTF8Encoding $false))
$env:CABINETOS_LOG_DIR = "$root\logs"
$env:CABINETOS_LOG_HEAVY = "1"
$env:CABINETOS_CORE_EXE = $Core
$env:CABINETOS_THEMES_DIR = "$root\themes"
$env:CABINETOS_UNDO_DIR = "$root\undo"
$env:CABINETOS_WEBVIEW2_DIR = "$root\webview2"
$env:CABINETOS_PLUGINS_DIR = "$root\plugins"
$env:CABINETOS_PLUGINS_DATA_DIR = "$root\plugins-data"
$env:CABINETOS_MARKETPLACE_DIR = "$root\marketplace"
$env:CABINETOS_TOOLS_DIR = "$root\tools"
Remove-Item Env:CABINETOS_UI_SNAPSHOT -ErrorAction SilentlyContinue
Remove-Item Env:CABINETOS_UI_SNAPSHOT_STEPS -ErrorAction SilentlyContinue

if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "STOP: the screen is locked"; exit 1 }
if (-not $NoCountdown) {
  . "$PSScriptRoot\countdown.ps1"
  if (-not (Show-InputCountdown -Seconds 5 -What 'The click-focus probe')) { 'cancelled at the countdown: nothing ran'; exit 2 }
}

$script:p = $null
$script:h = [IntPtr]::Zero
$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$text) { $lines.Add($text); $text }
function Step([string]$text) {
  if ($script:h -ne [IntPtr]::Zero -and [Probe]::ForegroundPid() -ne [uint32]$script:p.Id) {
    $front = Get-Process -Id ([Probe]::ForegroundPid()) -ErrorAction SilentlyContinue
    Say ("{0:HH:mm:ss.fff} STOP: CabinetOS is not the foreground window before '{1}' (in front: {2})" -f (Get-Date), $text, $front.ProcessName)
    throw 'another window came to the front'
  }
  Say ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text)
}
function LogLines([string]$glob) {
  @(Get-ChildItem -LiteralPath "$root\logs" -Filter $glob -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
      $stream = New-Object System.IO.FileStream($_.FullName, 'Open', 'Read', 'ReadWrite, Delete')
      $reader = New-Object System.IO.StreamReader($stream)
      try { while ($null -ne ($line = $reader.ReadLine())) { $line } } finally { $reader.Dispose() }
    })
}
function Ui([string]$message) { @(LogLines 'ui.*.jsonl' | Where-Object { $_ -match "`"$message`"" } | ForEach-Object { $_ | ConvertFrom-Json }) }
function WaitUi([string]$message, [int]$before, [int]$seconds, [scriptblock]$until = $null) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ($true) {
    $found = @(Ui $message | Select-Object -Skip $before | Where-Object { -not $until -or (& $until $_) })
    if ($found.Count -gt 0) { return $found[$found.Count - 1] }
    if ((Get-Date) -ge $deadline) { return $null }
    Start-Sleep -Milliseconds 100
  }
}
function KeysNow([string]$moment) { Say ("{0:HH:mm:ss.fff}   Windows sends the keys ({1}) to: {2}" -f (Get-Date), $moment, [Probe]::KeysTo($script:h)) }
function ClickLeftPane {
  $rect = New-Object Probe+RECT; [void][Probe]::DwmGetWindowAttribute($script:h, 9, [ref]$rect, 16)
  [Probe]::Click([int]($rect.Left + ($rect.Right - $rect.Left) * 0.33), [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.45))
}
function TerminalText([string]$session) {
  $tab = [System.Windows.Automation.AutomationElement]::FromHandle($script:h).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "pwsh [Left], session $session")))
  if (-not $tab) { return $false }
  $r = $tab.Current.BoundingRectangle
  [Probe]::Click([int]($r.Left + 200 * $script:scale), [int]($r.Bottom + 90 * $script:scale))
  return $true
}

try {
  Step "start ($Exe, core $Core)"
  $script:p = Start-Process -FilePath $Exe -PassThru
  Start-Sleep -Seconds 6
  $script:h = $script:p.MainWindowHandle
  [Probe]::Front($script:h); Start-Sleep -Milliseconds 800
  $script:scale = [Probe]::GetDpiForWindow($script:h) / 96.0
  Step "the window is in front; the left pane in $probe"
  [void](WaitUi 'listing shown' 0 5 { param($line) $line.fields.path -eq $probe })
  ClickLeftPane; Start-Sleep -Milliseconds 500

  Step "Ctrl+Backquote: the terminal opens for the left pane and takes the keyboard"
  $handed = @(Ui 'a page has the keyboard').Count
  [Probe]::Press($VK.Ctrl, $VK.Backquote)
  $opened = WaitUi 'terminal session opened' 0 10
  $hasKeys = WaitUi 'a page has the keyboard' $handed 10
  Start-Sleep -Milliseconds 2000
  KeysNow 'after Ctrl+Backquote'
  $session = $opened.fields.session_id
  Say "terminal: session $session opened, the window says the page has the keyboard: $([bool]$hasKeys)"
  [Probe]::Type("Set-Content -LiteralPath '$root\typed-1.txt' -Value 1"); [Probe]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
  Say "terminal: the shell ran a typed line: $(Test-Path -LiteralPath "$root\typed-1.txt")"

  Step "a half-typed line in the shell, then a click into the left pane"
  $leak = "$root\leak.txt"
  [Probe]::Type("Set-Content -LiteralPath '$leak' -Value 1"); Start-Sleep -Milliseconds 400
  $uiBefore = @(LogLines 'ui.*.jsonl').Count
  $heavyBefore = @(LogLines 'heavy-ui.*.jsonl').Count
  $selections = @(Ui 'selection shown').Count
  ClickLeftPane
  Start-Sleep -Milliseconds 400
  KeysNow '400 ms after the click'
  Say "click: the window logged a selection after it: $(@(Ui 'selection shown').Count -gt $selections)"

  Step "Home, Enter: the pane should open inner"
  $listings = @(Ui 'listing shown').Count
  [Probe]::Press($VK.Home); Start-Sleep -Milliseconds 200
  [Probe]::Press($VK.Enter)
  $inner = WaitUi 'listing shown' $listings 5 { param($line) $line.fields.path -eq "$probe\inner" }
  KeysNow 'after Home and Enter'
  Say "keys after the click: Home and Enter opened inner in the pane: $([bool]$inner)"
  $listings = @(Ui 'listing shown').Count
  [Probe]::Press($VK.Back)
  $back = WaitUi 'listing shown' $listings 5 { param($line) $line.fields.path -eq $probe }
  Say "keys after the click: Backspace went back to probe: $([bool]$back)"
  Start-Sleep -Milliseconds 800
  Say "keys after the click: the half-typed line ran in the shell (the keys reached it): $(Test-Path -LiteralPath $leak)"

  Say "the window's log from the click on:"
  @(LogLines 'ui.*.jsonl' | Select-Object -Skip $uiBefore) | Where-Object { $_ -match '"(a page|a click|keyboard owner|selection shown|listing shown|terminal summoned|a key the page)' } |
    ForEach-Object { $o = $_ | ConvertFrom-Json; Say ("  ui    {0} {1} {2}" -f $o.ts, $o.message, ($o.fields | ConvertTo-Json -Compress)) }
  @(LogLines 'heavy-ui.*.jsonl' | Select-Object -Skip $heavyBefore) | Where-Object { $_ -match '"(focus changed|focus moving|key pressed|text input|command run)"' } |
    ForEach-Object { $o = $_ | ConvertFrom-Json; Say ("  heavy {0} {1} {2}" -f $o.ts, $o.message, ($o.fields | ConvertTo-Json -Compress)) }

  Step "a click into the terminal's text, then a typed line: the shell should run it"
  $heavyBefore = @(LogLines 'heavy-ui.*.jsonl').Count
  $uiBefore = @(LogLines 'ui.*.jsonl').Count
  $clicked = TerminalText $session
  Start-Sleep -Milliseconds 700
  KeysNow '700 ms after the click into the terminal'
  # Whatever the half-typed line left on the prompt goes; Ctrl+C with nothing selected is the shell's.
  [Probe]::Press($VK.Ctrl, $VK.C); Start-Sleep -Milliseconds 600
  [Probe]::Type("Set-Content -LiteralPath '$root\typed-2.txt' -Value 2"); [Probe]::Press($VK.Enter); Start-Sleep -Milliseconds 1500
  Say "terminal: the click found the terminal's tab ($clicked), and the shell ran the line typed after it: $(Test-Path -LiteralPath "$root\typed-2.txt")"
  @(LogLines 'ui.*.jsonl' | Select-Object -Skip $uiBefore) | Where-Object { $_ -match '"(a page|a click|keyboard owner|a key the page)' } |
    ForEach-Object { $o = $_ | ConvertFrom-Json; Say ("  ui    {0} {1} {2}" -f $o.ts, $o.message, ($o.fields | ConvertTo-Json -Compress)) }
  @(LogLines 'heavy-ui.*.jsonl' | Select-Object -Skip $heavyBefore) | Where-Object { $_ -match '"(focus changed|focus moving)"' } |
    ForEach-Object { $o = $_ | ConvertFrom-Json; Say ("  heavy {0} {1} {2}" -f $o.ts, $o.message, ($o.fields | ConvertTo-Json -Compress)) }
} catch {
  Say ("{0:HH:mm:ss.fff} STOP: {1} (line {2})" -f (Get-Date), $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber)
} finally {
  if ($script:p -and -not $script:p.HasExited) {
    $script:h = [IntPtr]::Zero
    [void]$script:p.CloseMainWindow()
    if (-not $script:p.WaitForExit(8000)) { $script:p.Kill() }
  }
  Say "closed: $(if ($script:p) { $script:p.HasExited } else { 'never started' })"
  $lines | Set-Content -LiteralPath $out -Encoding UTF8
  $falses = @($lines | Where-Object { $_ -match ': False$' -or $_ -match 'STOP:' })
  @(
    "# The click-focus probe finished: you can use the keyboard and mouse again",
    "",
    "Ended $((Get-Date).ToString('HH:mm:ss')). Lines that answered False or stopped: $(if ($falses.Count) { $falses.Count } else { 'none' })"
  ) + @($falses | ForEach-Object { "- $_" }) + @("", "Full output: $out") | Set-Content -LiteralPath "$io\DONE.md" -Encoding UTF8
  Start-Process notepad.exe -ArgumentList "`"$io\DONE.md`""
}
