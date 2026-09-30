# Builds the Core Plugin of every extension (sdk/extensions/<name>/plugin) as a
# WebAssembly component: plugin.wasm next to plugin.json (git ignores it), and
# a copy with the plugin.json in sdk/fixtures/plugins/<id>/, which the core's
# tests load, so CI needs no WebAssembly toolchain. Commit the fixture when
# the plugin's code changes.
#
# Needs the Rust toolchain in the plugin's rust-toolchain.toml; rustup
# installs its wasm32-wasip2 target on first use. Windows stops the linker
# when a path is over about 260 characters: when the repository is deep in
# a folder tree, pass a short -TargetDir, for example C:\t\agent.
#
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File <repo>\sdk\extensions\build-extensions.ps1 [-TargetDir <folder>]

param(
    # Where cargo puts its build output; the plugin's own target folder when left out.
    [string] $TargetDir
)

$ErrorActionPreference = 'Stop'
$fixtures = Join-Path (Split-Path $PSScriptRoot -Parent) 'fixtures\plugins'

Get-ChildItem -Path $PSScriptRoot -Directory | ForEach-Object {
    $plugin = Join-Path $_.FullName 'plugin'
    if (-not (Test-Path (Join-Path $plugin 'Cargo.toml'))) { return }
    $manifest = Get-Content (Join-Path $plugin 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $package = [regex]::Match((Get-Content (Join-Path $plugin 'Cargo.toml') -Raw), '(?m)^name\s*=\s*"([^"]+)"').Groups[1].Value
    $saved = $env:CARGO_TARGET_DIR
    if ($TargetDir) { $env:CARGO_TARGET_DIR = $TargetDir }
    Push-Location $plugin
    try {
        cargo build --release --target wasm32-wasip2
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
        $built = if ($TargetDir) { $TargetDir } else { Join-Path $plugin 'target' }
    }
    finally {
        Pop-Location
        $env:CARGO_TARGET_DIR = $saved
    }
    $wasm = Join-Path $built "wasm32-wasip2\release\$package.wasm"
    if (-not (Test-Path $wasm)) { throw "$wasm was not built" }
    Copy-Item $wasm (Join-Path $plugin 'plugin.wasm') -Force
    $target = Join-Path $fixtures $manifest.id
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $wasm (Join-Path $target 'plugin.wasm') -Force
    Copy-Item (Join-Path $plugin 'plugin.json') (Join-Path $target 'plugin.json') -Force
    $size = (Get-Item (Join-Path $target 'plugin.wasm')).Length
    Write-Output ("{0,-8} {1,9:N0} bytes -> {2}" -f $manifest.id, $size, $target)
}
