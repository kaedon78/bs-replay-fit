# One zip per supported game version, laid out to drop straight into a Beat Saber folder.
#
# Built fresh rather than zipping whatever is lying in dist/: a package is the thing other
# people run, and shipping a stale DLL because a rebuild was skipped is the kind of mistake
# that is only ever found by the person who trusted it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'build-all.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$manifest = Get-Content (Join-Path $root 'src\ControllerAutoAdjust\manifest.json') -Raw | ConvertFrom-Json
$version = $manifest.version
$out = Join-Path $root 'dist\packages'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($game in @('1.40.5', '1.45.0')) {
    $stage = Join-Path $env:TEMP "caa-$game"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force (Join-Path $stage 'Plugins') | Out-Null

    Copy-Item (Join-Path $root "dist\$game\ControllerAutoAdjust.dll") (Join-Path $stage 'Plugins')
    Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'ControllerAutoAdjust-README.md')

    $zip = Join-Path $out "ControllerAutoAdjust-$version-bs$game.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $stage

    $size = [Math]::Round((Get-Item $zip).Length / 1KB)
    Write-Host ("{0}  ({1} KB)" -f (Split-Path $zip -Leaf), $size)
}
Write-Host "packages in $out"
