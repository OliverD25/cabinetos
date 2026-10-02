# Where the live-check scripts find the project's exchange folder _io. The project's root folder holds _io and the
# main checkout side by side, but a git worktree lives inside the main checkout (<main checkout>\.claude\worktrees\
# <name>), so "the repository's parent folder" is a stray folder there: three runs wrote their output into one on
# 2026-10-02. These functions ask git for the main checkout instead. Dot-source the file:
#   . "$PSScriptRoot\paths.ps1"
# A script's own -Io parameter still wins: call Get-IoFolder only when -Io is empty. Runs in Windows PowerShell 5.1
# and PowerShell 7.

# The repository's main checkout: the parent of the .git folder that every worktree of it shares. Where git is
# missing or fails, or the .git folder is not called .git (a separate git dir), it is the repository this file is
# in, which is where the scripts looked before.
function Get-MainCheckout {
  $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
  # The caller may run with 'Stop': a native command's error output must not end it here.
  $ErrorActionPreference = 'Continue'
  try {
    $common = "$(& git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir 2>$null)".Trim()
    if ($LASTEXITCODE -eq 0 -and $common) {
      $gitDir = [System.IO.Path]::GetFullPath($common)
      if ((Split-Path $gitDir -Leaf) -eq '.git') { return (Split-Path $gitDir -Parent) }
    }
  } catch { }
  $repo
}

# The exchange folder _io: the main checkout's parent folder plus _io, created when it is missing.
function Get-IoFolder {
  $folder = Join-Path (Split-Path (Get-MainCheckout) -Parent) '_io'
  New-Item -ItemType Directory -Force $folder | Out-Null
  $folder
}
