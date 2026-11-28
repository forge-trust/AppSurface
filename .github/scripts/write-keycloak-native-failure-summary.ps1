<#
.SYNOPSIS
Projects the failed Keycloak coverage collection into a small CI diagnostic.
.DESCRIPTION
Use the collection path selected by this run and the project from the native matrix.
Only the declared producer and project, a recognized execution status, and an integer
exit code are published. Missing or unreadable timings produce an unavailable summary.
#>
param(
    [Parameter(Mandatory)] [string] $CollectionDirectory,
    [Parameter(Mandatory)] [ValidateSet('darwin-arm64-keycloak', 'linux-arm64-keycloak')] [string] $ProducerId,
    [Parameter(Mandatory)] [ValidateLength(1, 256)] [string] $TestProject,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$summary = [ordered] @{
    schemaVersion = 1
    producerId = $ProducerId
    project = $TestProject
    executionStatus = 'unavailable'
    exitCode = $null
}

try {
    $item = Get-Item -LiteralPath (Join-Path $CollectionDirectory 'timings.json')
    if (!$item.PSIsContainer -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        $item.Length -le 1MB) {
        $timings = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 64
        $projects = @($timings['projects'] | Where-Object {
            $_ -is [Collections.IDictionary] -and $_['project'] -ceq $TestProject
        })
        if ($projects.Count -eq 1) {
            $status = $projects[0]['executionStatus']
            if ($status -is [string] -and $status -cin @('pending', 'running', 'completed', 'terminated', 'skipped-after-terminal')) {
                $summary.executionStatus = $status
            }
            $exitCode = $projects[0]['exitCode']
            if (($exitCode -is [int] -or $exitCode -is [long]) -and
                $exitCode -ge [int]::MinValue -and $exitCode -le [int]::MaxValue) {
                $summary.exitCode = [int] $exitCode
            }
        }
    }
} catch {
    # A failure can precede the timing report. Keep only fixed fallback values.
}

$summary | ConvertTo-Json -Compress | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
