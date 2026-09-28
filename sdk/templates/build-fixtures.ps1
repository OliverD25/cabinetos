# Rebuilds the plugin test fixtures: compiles every plugin in
# sdk/templates/plugins to a WebAssembly component and copies it, with its
# plugin.json, to sdk/fixtures/plugins/<id>/. The core's tests load those
# committed files, so CI needs no WebAssembly toolchain.
#
# Needs the Rust toolchain in sdk/templates/plugins/rust-toolchain.toml;
# rustup installs its wasm32-wasip2 target on first use.
#
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File <repo>\sdk\templates\build-fixtures.ps1

$ErrorActionPreference = 'Stop'
$plugins = Join-Path $PSScriptRoot 'plugins'
$fixtures = Join-Path (Split-Path $PSScriptRoot -Parent) 'fixtures\plugins'

Push-Location $plugins
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$built = Join-Path $plugins 'target\wasm32-wasip2\release'
Get-ChildItem -Path $plugins -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'plugin.json') } | ForEach-Object {
    $manifest = Get-Content (Join-Path $_.FullName 'plugin.json') -Raw | ConvertFrom-Json
    $id = $manifest.id
    $wasm = Join-Path $built "$($_.Name).wasm"
    if (-not (Test-Path $wasm)) { throw "$wasm was not built" }
    $target = Join-Path $fixtures $id
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $wasm (Join-Path $target 'plugin.wasm') -Force
    Copy-Item (Join-Path $_.FullName 'plugin.json') (Join-Path $target 'plugin.json') -Force
    $size = (Get-Item (Join-Path $target 'plugin.wasm')).Length
    Write-Output ("{0,-8} {1,8:N0} bytes -> {2}" -f $id, $size, $target)
}
