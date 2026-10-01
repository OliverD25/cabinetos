# Makes the folders of the core's list_directory bench (core/crates/cabinetos-fs/benches/list_directory.rs) on a
# machine without Rust: %TEMP%\cabinetos-bench\<n>\ with 1,000, 10,000 and 100,000 empty files, named as the bench
# names them, each with a "<n>.complete" marker next to it once it is whole. The live check scrolls the 100,000-entry
# folder and reaches it with Down, Down, Enter from cabinetos-bench, so all three must be there (docs/ui.md, "The live
# check"); remote-livecheck.ps1 runs this on the other machine before each run. A folder with its marker is left as it
# is, and a file already there is kept, so a run after a stop finishes the job. It removes nothing. Creating the
# 100,000 files takes a minute or two. Runs in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ui\livecheck\bench-folders.ps1
param([string]$Root = "$env:TEMP\cabinetos-bench")
$ErrorActionPreference = 'Stop'

# The bench's words, separators and extensions, in its order. The Ukrainian and Japanese words are built from their
# code points, so this file stays ASCII.
$words = @('IMG', 'report', 'Photo', 'draft', 'invoice', 'notes', 'Backup', 'data',
  (-join [char[]](0x0417, 0x0432, 0x0456, 0x0442)), (-join [char[]](0x0444, 0x043E, 0x0442, 0x043E)),
  (-join [char[]](0x8CC7, 0x6599)), 'file')
$separators = @('_', ' ', '-', '')
$extensions = @('jpg', 'txt', 'pdf', 'docx', 'png')

foreach ($count in 1000, 10000, 100000) {
  $dir = Join-Path $Root $count
  $marker = Join-Path $Root "$count.complete"
  if (Test-Path -LiteralPath $marker) { "$dir is there already"; continue }
  [void][System.IO.Directory]::CreateDirectory($dir)
  $started = Get-Date
  for ($n = 0; $n -lt $count; $n++) {
    $name = $words[$n % 12] + $separators[[int][Math]::Floor($n / 3) % 4] + $n + '.' + $extensions[[int][Math]::Floor($n / 7) % 5]
    $path = Join-Path $dir $name
    if (-not [System.IO.File]::Exists($path)) { [System.IO.File]::Create($path).Dispose() }
  }
  [System.IO.File]::Create($marker).Dispose()
  "{0}: {1:N0} files in {2:N1} s" -f $dir, $count, ((Get-Date) - $started).TotalSeconds
}
