# Run with pwsh -NoProfile -File tests/coverage-native-failure/contracts.ps1.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../../.github/scripts/write-keycloak-native-failure-summary.ps1'
$scratch = [IO.Directory]::CreateTempSubdirectory('native-failure-contract-').FullName
$project = 'Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj'
$timings = Join-Path $scratch 'timings.json'
$report = Join-Path $scratch 'summary.json'

function Assert-Summary($producer, $status, $exitCode) {
    & $script -CollectionDirectory $scratch -ProducerId $producer -TestProject $project -OutputPath $report
    $actual = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
    if ($actual.Count -ne 5 -or $actual.schemaVersion -ne 1 -or $actual.producerId -cne $producer -or
        $actual.project -cne $project -or $actual.executionStatus -cne $status -or $actual.exitCode -ne $exitCode) {
        throw 'Unexpected Keycloak collection failure summary.'
    }
}

try {
    $failed = @{
        project = $project
        executionStatus = 'completed'
        exitCode = 17
        log = '/private/arbitrary-child-output.log'
        coverageArtifactCause = 'arbitrary child message'
    }
    $other = @{ project = 'other.csproj'; executionStatus = 'completed'; exitCode = 42 }
    @{ projects = @($other, $failed); diagnostics = 'arbitrary payload' } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $timings
    foreach ($producer in @('darwin-arm64-keycloak', 'linux-arm64-keycloak')) {
        Assert-Summary $producer 'completed' 17
    }

    @{ projects = @($other) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $timings
    Assert-Summary 'linux-arm64-keycloak' 'unavailable' $null

    $failed.executionStatus = 'arbitrary payload'
    $failed.exitCode = '17'
    @{ projects = @($failed) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $timings
    Assert-Summary 'linux-arm64-keycloak' 'unavailable' $null

    'not JSON' | Set-Content -LiteralPath $timings
    Assert-Summary 'linux-arm64-keycloak' 'unavailable' $null

    (' ' * 1MB) | Set-Content -LiteralPath $timings
    Assert-Summary 'linux-arm64-keycloak' 'unavailable' $null

    Remove-Item -LiteralPath $timings
    Assert-Summary 'darwin-arm64-keycloak' 'unavailable' $null
    Write-Output 'Passed 7 Keycloak native failure summary checks.'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
