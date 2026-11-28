# Run with pwsh -NoProfile -File tests/coverage-native-failure/contracts.ps1.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../../.github/scripts/write-keycloak-native-failure-summary.ps1'
$scratch = [IO.Directory]::CreateTempSubdirectory('native-failure-contract-').FullName
$project = 'Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj'
$coreProject = 'Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Keycloak.Tests.csproj'
$projects = @($project, $coreProject)
$timings = Join-Path $scratch 'timings.json'
$report = Join-Path $scratch 'summary.json'

function Assert-Summary($producer, $status, $exitCode, [string[]]$failedMethods = @()) {
    & $script -CollectionDirectory $scratch -ProducerId $producer -TestProject $project -OutputPath $report
    $actual = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
    if ($actual.Count -ne 6 -or $actual.schemaVersion -ne 2 -or $actual.producerId -cne $producer -or
        $actual.project -cne $project -or $actual.executionStatus -cne $status -or $actual.exitCode -ne $exitCode -or
        $actual.failedTestMethods -isnot [array] -or ($actual.failedTestMethods -join ',') -cne ($failedMethods -join ',')) {
        throw 'Unexpected Keycloak collection failure summary.'
    }
}

function Assert-MultiProjectSummary($producer, $expectedProject, $status, $exitCode, [string[]]$failedMethods = @()) {
    & $script -CollectionDirectory $scratch -ProducerId $producer -TestProject $projects -OutputPath $report
    $actual = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
    if ($actual.Count -ne 6 -or $actual.schemaVersion -ne 2 -or $actual.producerId -cne $producer -or
        $actual.project -cne $expectedProject -or $actual.executionStatus -cne $status -or $actual.exitCode -ne $exitCode -or
        $actual.failedTestMethods -isnot [array] -or ($actual.failedTestMethods -join ',') -cne ($failedMethods -join ',')) {
        throw 'Unexpected multi-project Keycloak collection failure summary.'
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

    $junit = 'junit-coverage-1-ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests-0123abcd.xml'
    $junitPath = Join-Path $scratch $junit
    $testClass = 'ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.AppSurfaceKeycloakSupervisedWorkflowTests'
    $result = @{ format = 'junit'; path = $junit; parserStatus = 'available' }
    $failed.testResults = @($result)
    $testCases = @"
<testsuites><testsuite>
  <testcase classname="$testClass" name="RunAsync(seedValue: hidden)"><failure message="private detail" /></testcase>
  <testcase classname="$testClass" name="RunAsync(another value)"><error>private detail</error></testcase>
  <testcase classname="$testClass" name="RejectsInvalidInput"><failure /></testcase>
  <testcase classname="$testClass" name="Passing" />
  <testcase classname="$testClass" name="custom display name"><failure /></testcase>
  <testcase classname="arbitrary.class" name="Other"><failure /></testcase>
</testsuite></testsuites>
"@
    Set-Content -LiteralPath $junitPath -Value $testCases
    @{ projects = @($failed) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Assert-Summary 'darwin-arm64-keycloak' 'completed' 17 @("$testClass.RunAsync", "$testClass.RejectsInvalidInput")

    $manyCases = (1..25 | ForEach-Object {
        "<testcase classname=`"$testClass`" name=`"Case$_`"><failure /></testcase>"
    }) -join ''
    Set-Content -LiteralPath $junitPath -Value "<testsuite>$manyCases</testsuite>"
    $firstTwenty = 1..20 | ForEach-Object { "$testClass.Case$_" }
    Assert-Summary 'darwin-arm64-keycloak' 'completed' 17 $firstTwenty

    $result.path = "./$junit"
    @{ projects = @($failed) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Assert-Summary 'darwin-arm64-keycloak' 'completed' 17
    $result.path = $junit
    @{ projects = @($failed) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Set-Content -LiteralPath $junitPath -Value "<!DOCTYPE testsuite [<!ENTITY ignored 'data'>]><testsuite>$manyCases</testsuite>"
    Assert-Summary 'darwin-arm64-keycloak' 'completed' 17

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

    $coreClass = 'ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests.InfrastructureTests'
    $coreMethod = "$coreClass.ReadsCoreConfiguration"
    $coreJunit = 'junit-coverage-1-ForgeTrust.AppSurface.Auth.Keycloak.Tests-89abcdef.xml'
    Set-Content -LiteralPath (Join-Path $scratch $coreJunit) -Value @"
<testsuite>
  <testcase classname="$coreClass" name="ReadsCoreConfiguration(seedValue: hidden)"><failure message="private detail" /></testcase>
  <testcase classname="arbitrary.class" name="Ignored"><failure /></testcase>
</testsuite>
"@
    $adapterEntry = @{ project = $project; executionStatus = 'completed'; exitCode = 0 }
    $coreEntry = @{
        project = $coreProject
        executionStatus = 'completed'
        exitCode = 17
        testResults = @(@{ format = 'junit'; path = $coreJunit; parserStatus = 'available' })
    }
    @{ projects = @($adapterEntry, $coreEntry) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Assert-MultiProjectSummary 'darwin-arm64-keycloak' $coreProject 'completed' 17 @($coreMethod)

    $adapterEntry.exitCode = 19
    @{ projects = @($adapterEntry, $coreEntry) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Assert-MultiProjectSummary 'darwin-arm64-keycloak' $project 'completed' 19

    $adapterEntry.exitCode = 0
    $coreEntry.exitCode = 0
    @{ projects = @($adapterEntry, $coreEntry) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $timings
    Assert-MultiProjectSummary 'darwin-arm64-keycloak' $project 'completed' 0

    Write-Output 'Passed 14 Keycloak native failure summary checks.'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
