# One plugin per supported game version.
#
# Each is compiled against that version's own reference set, so a member missing from it is a
# build error rather than a crash on someone else's install, and each carries a manifest naming
# the version it was built for.
#
# -t:Rebuild every time, deliberately: the manifest is an embedded resource, and an incremental
# build reuses the one already in the assembly. Switching version without it produces a DLL that
# builds clean and tells BSIPA it is for the version you built last.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ControllerAutoAdjust\ControllerAutoAdjust.csproj'

foreach ($version in @('1.40.5', '1.45.0')) {
    $out = Join-Path $root "dist\$version"
    Write-Host "building for $version -> $out"
    dotnet build -c Release $project -t:Rebuild `
        -p:BSGameVersion=$version -p:OutputPath="$out\" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed for $version" }
}
Write-Host 'done'
