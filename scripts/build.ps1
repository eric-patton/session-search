[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [string[]] $ArgumentList = @()
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

Push-Location $projectRoot
try {
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @(
        'scripts/check-source.mjs',
        '--self-test'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @('scripts/check-source.mjs')
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'restore',
        'SessionSearch.slnx',
        '--locked-mode'
    )
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'format',
        'SessionSearch.slnx',
        '--verify-no-changes',
        '--no-restore'
    )
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'build',
        'SessionSearch.slnx',
        '--configuration',
        $Configuration,
        '--no-restore'
    )
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'test',
        '--solution',
        'SessionSearch.slnx',
        '--configuration',
        $Configuration,
        '--no-build',
        '--no-restore',
        '--no-ansi',
        '--no-progress',
        '--minimum-expected-tests',
        '150'
    )
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'list',
        'SessionSearch.slnx',
        'package',
        '--vulnerable',
        '--include-transitive',
        '--no-restore'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @(
        'scripts/validate.mjs',
        '--feature',
        'local-ai-session-search'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @(
        'scripts/assemble.mjs',
        '--check'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @(
        'scripts/dashboard.mjs',
        '--check'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @(
        'scripts/view.mjs',
        '--check'
    )
    Invoke-NativeCommand -FilePath 'node' -ArgumentList @('scripts/scan-artifacts.mjs')
}
finally {
    Pop-Location
}
