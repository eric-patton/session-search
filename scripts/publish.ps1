[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$target = Join-Path $releaseRoot "SessionSearch-$Version-win-x64"
if (Test-Path -LiteralPath $target) {
    throw "Release target already exists: $target"
}

Push-Location $projectRoot
try {
    dotnet publish src/SessionSearch.App/SessionSearch.App.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --no-restore `
        --output $target `
        -p:Version=$Version `
        -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    node scripts/scan-artifacts.mjs $target
    if ($LASTEXITCODE -ne 0) {
        throw "The release artifact privacy scan failed."
    }

    Write-Output $target
}
finally {
    Pop-Location
}
