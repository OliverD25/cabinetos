# Which of the media files in a folder does this machine's browser engine play? The Media Viewer of Quick View
# (sdk\tools\media-viewer) claims only the kinds that play in WebView2, and WebView2 is the Edge engine with the
# machine's own codecs (H.264 and AAC come from Windows; HEVC needs the hardware or the Microsoft extension). This
# script starts Edge without a window on a page of its own, served from 127.0.0.1 by this script, and the page tries
# each file: it sets the file as the source of a video or audio element, waits for the first frame, plays it, and
# checks that the clock moved and that frames or sound were decoded. The page posts its findings back; the script
# prints one line per file and a verdict. No file is changed and nothing leaves this machine.
#
# The default folder is ui\livecheck\fixtures\media, one tiny file of each kind the viewer claims, and an HEVC one
# that is only for the report. It opens no window and presses no key, so it is safe on any machine; on the Omen
# laptop run it through remote-script.ps1 (the laptop's numbers decide, the creator's rule of 2026-10-02):
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\remote-script.ps1 -Script ui\livecheck\media-probe.ps1 -Branch <branch>
param(
  [string]$Folder = "$PSScriptRoot\fixtures\media",
  [int]$Port = 8097,
  [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $Folder).Path
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { "STOP: Microsoft Edge is not installed here"; exit 1 }
$webView = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv
"machine: $env:COMPUTERNAME; Edge $((Get-Item -LiteralPath $edge).VersionInfo.ProductVersion); WebView2 runtime $webView"
"graphics: $((Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }) -join '; ')"

$mime = @{
  '.mp4' = 'video/mp4'; '.m4v' = 'video/mp4'; '.mov' = 'video/quicktime'; '.webm' = 'video/webm'; '.mkv' = 'video/x-matroska'
  '.mp3' = 'audio/mpeg'; '.m4a' = 'audio/mp4'; '.aac' = 'audio/aac'; '.flac' = 'audio/flac'; '.wav' = 'audio/wav'
  '.ogg' = 'audio/ogg'; '.opus' = 'audio/ogg'; '.weba' = 'audio/webm'
}
$names = @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $mime.ContainsKey($_.Extension.ToLowerInvariant()) } | Sort-Object Name | ForEach-Object { $_.Name })
if ($names.Count -eq 0) { "STOP: no media file in $folder"; exit 1 }

$page = @'
<!doctype html><meta charset="utf-8"><title>media probe</title><body><script>
const files = @@FILES@@;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function probe(file) {
  const audio = /\.(mp3|m4a|aac|flac|wav|ogg|opus|weba)$/i.test(file);
  const el = document.createElement(audio ? 'audio' : 'video');
  el.muted = true; el.preload = 'auto'; el.src = '/files/' + encodeURIComponent(file); document.body.append(el);
  return await new Promise((resolve) => {
    const timer = setTimeout(() => resolve({ state: 'timeout' }), 8000);
    el.addEventListener('error', () => { clearTimeout(timer); resolve({ state: 'error', code: el.error && el.error.code, message: el.error && el.error.message }); });
    el.addEventListener('loadeddata', async () => {
      let played = 'ok';
      try { await el.play(); } catch (e) { played = String(e.name); }
      await sleep(900);
      clearTimeout(timer);
      resolve({ state: 'ok', played, time: el.currentTime, width: el.videoWidth, height: el.videoHeight, duration: el.duration, videoBytes: el.webkitVideoDecodedByteCount, audioBytes: el.webkitAudioDecodedByteCount });
    });
  });
}
(async () => {
  const results = [];
  for (const file of files) { const r = await probe(file); r.file = file; results.push(r); }
  await fetch('/result', { method: 'POST', body: JSON.stringify(results) });
})();
</script>
'@
$page = $page.Replace('@@FILES@@', (ConvertTo-Json -Compress -InputObject @($names)))

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$profile = Join-Path $env:TEMP ('media-probe-' + [guid]::NewGuid().ToString('N'))
$result = $null
$proc = $null
try {
  $proc = Start-Process -FilePath $edge -PassThru -ArgumentList @('--headless=new', "--user-data-dir=$profile", '--autoplay-policy=no-user-gesture-required', '--mute-audio', '--no-first-run', '--disable-extensions', "http://127.0.0.1:$Port/probe.html")
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $pending = $listener.BeginGetContext($null, $null)
  while (-not $result -and (Get-Date) -lt $deadline) {
    if (-not $pending.AsyncWaitHandle.WaitOne(250)) { continue }
    $context = $listener.EndGetContext($pending)
    $pending = $listener.BeginGetContext($null, $null)
    $request = $context.Request; $response = $context.Response
    try {
      $path = $request.Url.AbsolutePath
      if ($path -eq '/probe.html') {
        $bytes = [Text.Encoding]::UTF8.GetBytes($page)
        $response.ContentType = 'text/html; charset=utf-8'; $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
      } elseif ($path -like '/files/*') {
        $name = [IO.Path]::GetFileName([Uri]::UnescapeDataString($path.Substring(7)))
        $file = Join-Path $folder $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $response.StatusCode = 404 }
        else {
          $bytes = [IO.File]::ReadAllBytes($file)
          $response.ContentType = $mime[[IO.Path]::GetExtension($name).ToLowerInvariant()]
          $response.AddHeader('Accept-Ranges', 'bytes')
          $start = 0; $end = $bytes.Length - 1
          if ($request.Headers['Range'] -match 'bytes=(\d*)-(\d*)') {
            if ($Matches[1] -ne '') { $start = [int]$Matches[1] }
            if ($Matches[2] -ne '') { $end = [Math]::Min([int]$Matches[2], $bytes.Length - 1) }
            $response.StatusCode = 206
            $response.AddHeader('Content-Range', "bytes $start-$end/$($bytes.Length)")
          }
          $response.ContentLength64 = $end - $start + 1
          $response.OutputStream.Write($bytes, $start, $end - $start + 1)
        }
      } elseif ($path -eq '/result' -and $request.HttpMethod -eq 'POST') {
        $reader = New-Object IO.StreamReader($request.InputStream, [Text.Encoding]::UTF8)
        $result = $reader.ReadToEnd() | ConvertFrom-Json
      } else { $response.StatusCode = 404 }
    } catch { } finally { try { $response.Close() } catch { } }
  }
} finally {
  $listener.Stop()
  # Only the Edge processes of this run's own profile folder: never a window of the user's.
  Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 700
  Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not $result) { "STOP: the page reported nothing within $TimeoutSeconds s"; exit 1 }

"| file | plays | first frame | video decoded | sound decoded | played | note |"
"|---|---|---|---|---|---|---|"
$plays = 0
foreach ($r in @($result)) {
  $isVideo = $r.file -notmatch '\.(mp3|m4a|aac|flac|wav|ogg|opus|weba)$'
  $ok = $r.state -eq 'ok' -and $r.time -gt 0.3 -and ((-not $isVideo) -or $r.videoBytes -gt 0) -and ($isVideo -or $r.audioBytes -gt 0)
  if ($ok) { $plays++ }
  $note = if ($r.state -eq 'error') { "error $($r.code): $($r.message)" } elseif ($r.state -eq 'timeout') { 'no first frame within 8 s' } else { '' }
  "| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f $r.file, $(if ($ok) { 'yes' } else { 'NO' }), $(if ($r.state -ne 'ok') { '-' } elseif ($r.width -gt 0) { "{0}x{1}, {2:N1} s" -f $r.width, $r.height, $r.duration } else { "no picture, {0:N1} s" -f $r.duration }), $r.videoBytes, $r.audioBytes, $r.played, $note
}
"media probe: $plays of $(@($result).Count) files play"
exit 0
