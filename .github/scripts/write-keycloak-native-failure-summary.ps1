<#
.SYNOPSIS
Projects the failed Keycloak coverage collection into a small CI diagnostic.
.DESCRIPTION
Use the collection path selected by this run and the declared project or projects from
the native matrix. The summary contains the declared producer and selected project, a
recognized execution status, an integer exit code, and up to twenty distinct failing
Keycloak test methods from that project's managed JUnit result. When no nonzero exit is
observed, the first declared project remains selected. Theory arguments and failure
text stay local. Missing or unreadable inputs leave the corresponding diagnostic fields
empty.
#>
param(
    [Parameter(Mandatory)] [string] $CollectionDirectory,
    [Parameter(Mandatory)] [ValidateSet('darwin-arm64-keycloak', 'linux-arm64-keycloak')] [string] $ProducerId,
    [Parameter(Mandatory)] [ValidateCount(1, 2)]
    [ValidateSet(
        'Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj',
        'Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Keycloak.Tests.csproj',
        IgnoreCase = $false
    )]
    [string[]] $TestProject,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$summary = [ordered] @{
    schemaVersion = 2
    producerId = $ProducerId
    project = $TestProject[0]
    executionStatus = 'unavailable'
    exitCode = $null
    failedTestMethods = @()
}

function Get-FailedTestMethods($Project, [string] $ProjectPath) {
    $projectName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $optionalTestsSubnamespace = if ($projectName -ceq 'ForgeTrust.AppSurface.Auth.Keycloak.Tests') { '(?:\.Tests)?' } else { '' }
    $classPattern = '\A' + [regex]::Escape($projectName) + $optionalTestsSubnamespace +
        '\.[A-Za-z_][A-Za-z0-9_]{0,95}\z'
    $reportPattern = '\Ajunit-coverage-[0-9]{1,4}-' + [regex]::Escape($projectName) + '-[a-f0-9]{8}\.xml\z'
    $reports = @($Project['testResults'] | Where-Object {
        $_ -is [Collections.IDictionary] -and $_['format'] -ceq 'junit' -and $_['parserStatus'] -cin @('available', 'parsed') -and
        $_['path'] -is [string] -and $_['path'] -cmatch $reportPattern
    })
    if ($reports.Count -ne 1) { return }
    $file = Get-Item -LiteralPath (Join-Path $CollectionDirectory $reports[0]['path'])
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $file.Length -gt 2MB) { return }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 2MB
    $reader = [Xml.XmlReader]::Create($file.FullName, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.Load($reader)
    } finally {
        $reader.Dispose()
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $document.SelectNodes('/testsuites/testsuite/testcase[failure or error] | /testsuite/testcase[failure or error]')) {
        $className = $case.GetAttribute('classname')
        if ($className -cnotmatch $classPattern) { continue }
        if ($case.GetAttribute('name') -cmatch '\A([A-Za-z_][A-Za-z0-9_]{0,95})(?:\(|\z)') {
            $method = "$className.$($Matches[1])"
            if ($seen.Add($method)) { $method }
            if ($seen.Count -eq 20) { return }
        }
    }
}

try {
    $item = Get-Item -LiteralPath (Join-Path $CollectionDirectory 'timings.json')
    if (!$item.PSIsContainer -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        $item.Length -le 1MB) {
        $timings = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 64
        $timingProjects = @($timings['projects'])
        $primaryProject = $TestProject[0]
        $primaryEntry = $null
        $selectedProject = $null
        $selectedEntry = $null
        foreach ($declaredProject in $TestProject) {
            $entries = @($timingProjects | Where-Object {
                $_ -is [Collections.IDictionary] -and $_['project'] -ceq $declaredProject
            })
            if ($entries.Count -ne 1) { continue }

            $entry = $entries[0]
            if ($declaredProject -ceq $primaryProject) {
                $primaryEntry = $entry
            }
            $exitCode = $entry['exitCode']
            if (($exitCode -is [int] -or $exitCode -is [long]) -and
                $exitCode -ge [int]::MinValue -and $exitCode -le [int]::MaxValue -and $exitCode -ne 0) {
                $selectedProject = $declaredProject
                $selectedEntry = $entry
                break
            }
        }
        if ($null -eq $selectedEntry) {
            $selectedProject = $primaryProject
            $selectedEntry = $primaryEntry
        }
        if ($null -ne $selectedEntry) {
            $summary.project = $selectedProject
            $status = $selectedEntry['executionStatus']
            if ($status -is [string] -and $status -cin @('pending', 'running', 'completed', 'terminated', 'skipped-after-terminal')) {
                $summary.executionStatus = $status
            }
            $exitCode = $selectedEntry['exitCode']
            if (($exitCode -is [int] -or $exitCode -is [long]) -and
                $exitCode -ge [int]::MinValue -and $exitCode -le [int]::MaxValue) {
                $summary.exitCode = [int] $exitCode
                if ($summary.exitCode -ne 0) {
                    $summary.failedTestMethods = @(Get-FailedTestMethods $selectedEntry $selectedProject)
                }
            }
        }
    }
} catch {
    # A failure can precede either report. Retain only the available bounded fields.
}

$summary | ConvertTo-Json -Compress | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
