$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '../../.github/scripts/install-coverage-candidate.ps1'
. $installer

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-FailsWith {
    param([scriptblock]$Action, [string]$Message)
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -like "*$Message*") { return }
        throw
    }
    throw "Expected failure containing '$Message'."
}

$script:CoverageCandidateGitCalls = 0
$script:CoverageCandidateDotnetCalls = 0
$script:CoverageCandidateInstallCalls = 0
$script:CoverageCandidatePackage = $null
$script:CoverageCandidateFailGitCommand = $null
$script:CoverageCandidateExtraGitCommand = $null
$script:CoverageCandidateFailDotnetVersion = $false
$script:CoverageCandidateExtraDotnetVersionOutput = $false
$script:CoverageCandidateFailInstall = $false
$script:CoverageCandidateInstalledArchiveCount = 1
$script:CoverageCandidateAlterInstalledArchive = $false
$script:CoverageCandidateOmitCommand = $false

function git {
    $script:CoverageCandidateGitCalls++
    $command = $args -join ' '
    if ($command -eq 'rev-parse HEAD') { $output = $env:STUB_COVERAGE_COMMIT }
    elseif ($command -eq 'rev-parse HEAD^{tree}') { $output = $env:STUB_COVERAGE_TREE }
    else { throw "Unexpected stub git invocation: $command" }
    $global:LASTEXITCODE = if ($script:CoverageCandidateFailGitCommand -eq $command) { 1 } else { 0 }
    if ($script:CoverageCandidateExtraGitCommand -eq $command) { return @($output, 'unexpected extra line') }
    return $output
}

function dotnet {
    $script:CoverageCandidateDotnetCalls++
    $global:LASTEXITCODE = 0
    if ($args.Count -eq 1 -and $args[0] -eq '--version') {
        if ($script:CoverageCandidateFailDotnetVersion) { $global:LASTEXITCODE = 1 }
        if ($script:CoverageCandidateExtraDotnetVersionOutput) {
            return @($env:STUB_COVERAGE_SDK, 'unexpected extra line')
        }
        return $env:STUB_COVERAGE_SDK
    }
    if ($args.Count -lt 2 -or $args[0] -ne 'tool' -or $args[1] -ne 'install') {
        throw "Unexpected stub dotnet invocation: $($args -join ' ')"
    }
    if ($args -contains '--add-source' -or $args -like '--add-source=*') {
        throw 'The candidate install must use only the isolated NuGet configuration.'
    }

    $script:CoverageCandidateInstallCalls++
    if ($script:CoverageCandidateFailInstall) {
        $global:LASTEXITCODE = 1
        return
    }
    $toolPathIndex = [Array]::IndexOf([string[]]$args, '--tool-path')
    $configIndex = [Array]::IndexOf([string[]]$args, '--configfile')
    if ($toolPathIndex -lt 0 -or $configIndex -lt 0) { throw 'Stub install arguments are incomplete.' }
    $toolRoot = [string]$args[$toolPathIndex + 1]
    $toolConfig = [string]$args[$configIndex + 1]
    [xml]$config = Get-Content -Raw -LiteralPath $toolConfig
    if ($config.SelectNodes('/configuration/packageSources/clear').Count -ne 1 -or
        $config.SelectNodes('/configuration/fallbackPackageFolders/clear').Count -ne 1 -or
        $config.SelectNodes('/configuration/packageSources/add').Count -ne 1) {
        throw 'NuGet candidate configuration did not clear package and fallback sources.'
    }

    $archiveName = Split-Path -Leaf $script:CoverageCandidatePackage
    New-Item -ItemType Directory -Path (Join-Path $toolRoot '.store') -Force | Out-Null
    for ($index = 0; $index -lt $script:CoverageCandidateInstalledArchiveCount; $index++) {
        $store = Join-Path $toolRoot ".store/forgetrust.appsurface.cli/stub/$index"
        New-Item -ItemType Directory -Path $store -Force | Out-Null
        $installedArchive = Join-Path $store $archiveName
        Copy-Item -LiteralPath $script:CoverageCandidatePackage -Destination $installedArchive
        if ($script:CoverageCandidateAlterInstalledArchive) {
            [IO.File]::WriteAllText($installedArchive, 'altered installed archive')
        }
    }
    if (!$script:CoverageCandidateOmitCommand) {
        $commandName = if ($IsWindows) { 'appsurface.exe' } else { 'appsurface' }
        New-Item -ItemType File -Path (Join-Path $toolRoot $commandName) -Force | Out-Null
    }
}

function Set-TestManifest {
    param([hashtable]$Value, [string]$Path)
    $Value | ConvertTo-Json | Set-Content -LiteralPath $Path
}

function Reset-InstallerStubs {
    $script:CoverageCandidateGitCalls = 0
    $script:CoverageCandidateDotnetCalls = 0
    $script:CoverageCandidateFailGitCommand = $null
    $script:CoverageCandidateExtraGitCommand = $null
    $script:CoverageCandidateFailDotnetVersion = $false
    $script:CoverageCandidateExtraDotnetVersionOutput = $false
    $script:CoverageCandidateFailInstall = $false
    $script:CoverageCandidateInstalledArchiveCount = 1
    $script:CoverageCandidateAlterInstalledArchive = $false
    $script:CoverageCandidateOmitCommand = $false
    $script:CoverageCandidateInstallCalls = 0
    Remove-Item -LiteralPath (Join-Path $env:RUNNER_TEMP 'coverage-cli') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $env:GITHUB_ENV -Force -ErrorAction SilentlyContinue
}

function Assert-InstallerRejects {
    param([string]$Message, [int]$ExpectedInstallCalls = 0)
    $script:CoverageCandidateInstallCalls = 0
    Assert-FailsWith { Install-CoverageCandidate } $Message
    Assert-Equal $ExpectedInstallCalls $script:CoverageCandidateInstallCalls 'Candidate rejection install-call count differs.'
    Assert-Equal $false (Test-Path -LiteralPath $env:GITHUB_ENV) 'Rejected candidate must not be exported.'
}

$previousEnvironment = @{}
foreach ($name in @('RUNNER_TEMP', 'COVERAGE_SOURCE_COMMIT', 'COVERAGE_SDK_VERSION',
        'COVERAGE_CLI_CANDIDATE_VERSION', 'GITHUB_ENV', 'STUB_COVERAGE_COMMIT',
        'STUB_COVERAGE_TREE', 'STUB_COVERAGE_SDK')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('coverage-candidate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $version = '0.1.0-ci.123.1'
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $tree = '89abcdef0123456789abcdef0123456789abcdef'
    $sdk = '10.0.402'
    $env:RUNNER_TEMP = $testRoot
    $env:COVERAGE_SOURCE_COMMIT = $commit
    $env:COVERAGE_SDK_VERSION = $sdk
    $env:COVERAGE_CLI_CANDIDATE_VERSION = $version
    $env:STUB_COVERAGE_COMMIT = $commit
    $env:STUB_COVERAGE_TREE = $tree
    $env:STUB_COVERAGE_SDK = $sdk

    $feed = Join-Path $testRoot 'coverage-candidate-feed'
    New-Item -ItemType Directory -Path $feed | Out-Null
    $package = Join-Path $feed "ForgeTrust.AppSurface.Cli.$version.nupkg"
    [IO.File]::WriteAllBytes($package, [Text.Encoding]::UTF8.GetBytes('stub candidate archive'))
    $script:CoverageCandidatePackage = $package
    $packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{
        schemaVersion = 1
        commit = $commit
        tree = $tree
        sdk = $sdk
        version = $version
        packageSha256 = $packageHash
    }
    $manifestPath = Join-Path $feed 'candidate.json'
    $githubEnvPath = Join-Path $testRoot 'github-env'
    $env:GITHUB_ENV = $githubEnvPath
    Set-TestManifest -Value $manifest -Path $manifestPath

    Reset-InstallerStubs
    Remove-Item Env:GITHUB_ENV
    Assert-FailsWith { Install-CoverageCandidate } 'environment variable is missing: GITHUB_ENV'
    Assert-Equal 0 $script:CoverageCandidateGitCalls 'Missing environment must fail before git.'
    $env:GITHUB_ENV = $githubEnvPath

    $invalidManifestCases = @(
        @{ field = 'commit'; value = 'fedcba9876543210fedcba9876543210fedcba98'; message = 'source, SDK, or pinned version' },
        @{ field = 'tree'; value = 'fedcba9876543210fedcba9876543210fedcba98'; message = 'source, SDK, or pinned version' },
        @{ field = 'sdk'; value = '10.0.403'; message = 'source, SDK, or pinned version' },
        @{ field = 'version'; value = '0.1.0-wrong'; message = 'source, SDK, or pinned version' }
    )
    foreach ($invalid in $invalidManifestCases) {
        $manifest.commit = $commit
        $manifest.tree = $tree
        $manifest.sdk = $sdk
        $manifest.version = $version
        $manifest.packageSha256 = $packageHash
        $manifest[$invalid.field] = $invalid.value
        Set-TestManifest -Value $manifest -Path $manifestPath
        Reset-InstallerStubs
        Assert-InstallerRejects $invalid.message
    }

    $manifest.commit = $commit
    $manifest.tree = $tree
    $manifest.sdk = $sdk
    $manifest.version = $version
    $manifest.packageSha256 = $packageHash
    Set-TestManifest -Value $manifest -Path $manifestPath

    $sourceCommit = $env:COVERAGE_SOURCE_COMMIT
    $env:COVERAGE_SOURCE_COMMIT = 'fedcba9876543210fedcba9876543210fedcba98'
    $manifest.commit = $env:COVERAGE_SOURCE_COMMIT
    Set-TestManifest -Value $manifest -Path $manifestPath
    Reset-InstallerStubs
    Assert-InstallerRejects 'source, SDK, or pinned version'
    $env:COVERAGE_SOURCE_COMMIT = $sourceCommit

    $manifest.commit = $commit
    $manifest.sdk = '10.0.403'
    $env:STUB_COVERAGE_SDK = $manifest.sdk
    Set-TestManifest -Value $manifest -Path $manifestPath
    Reset-InstallerStubs
    Assert-InstallerRejects 'source, SDK, or pinned version'
    $manifest.sdk = $sdk
    $env:STUB_COVERAGE_SDK = $sdk
    Set-TestManifest -Value $manifest -Path $manifestPath

    foreach ($failingGitCommand in @('rev-parse HEAD', 'rev-parse HEAD^{tree}')) {
        Reset-InstallerStubs
        $script:CoverageCandidateFailGitCommand = $failingGitCommand
        $message = if ($failingGitCommand -eq 'rev-parse HEAD') {
            'Could not verify the checked-out coverage source commit.'
        } else {
            'Could not verify the checked-out coverage source tree.'
        }
        Assert-InstallerRejects $message
    }

    foreach ($extraGitCommand in @('rev-parse HEAD', 'rev-parse HEAD^{tree}')) {
        Reset-InstallerStubs
        $script:CoverageCandidateExtraGitCommand = $extraGitCommand
        $message = if ($extraGitCommand -eq 'rev-parse HEAD') {
            'Could not verify the checked-out coverage source commit.'
        } else {
            'Could not verify the checked-out coverage source tree.'
        }
        Assert-InstallerRejects $message
    }

    Reset-InstallerStubs
    $script:CoverageCandidateFailDotnetVersion = $true
    Assert-InstallerRejects 'Could not verify the selected coverage SDK.'
    Reset-InstallerStubs
    $script:CoverageCandidateExtraDotnetVersionOutput = $true
    Assert-InstallerRejects 'Could not verify the selected coverage SDK.'

    Remove-Item -LiteralPath $package
    Set-TestManifest -Value $manifest -Path $manifestPath
    Reset-InstallerStubs
    Assert-InstallerRejects 'source, SDK, or pinned version'
    [IO.File]::WriteAllBytes($package, [Text.Encoding]::UTF8.GetBytes('stub candidate archive'))

    $manifest.packageSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
    Set-TestManifest -Value $manifest -Path $manifestPath
    Reset-InstallerStubs
    Assert-InstallerRejects 'package hash'
    $manifest.packageSha256 = $packageHash
    Set-TestManifest -Value $manifest -Path $manifestPath

    Reset-InstallerStubs
    $script:CoverageCandidateFailInstall = $true
    Assert-FailsWith { Install-CoverageCandidate } 'Candidate CLI installation failed.'
    Assert-Equal 1 $script:CoverageCandidateInstallCalls 'Install failure should be reported after one attempt.'
    Assert-Equal $false (Test-Path -LiteralPath $env:GITHUB_ENV) 'Failed install must not be exported.'

    Reset-InstallerStubs
    $script:CoverageCandidateInstalledArchiveCount = 0
    Assert-InstallerRejects 'Installed CLI archive does not match the same-run candidate.' 1

    Reset-InstallerStubs
    $script:CoverageCandidateInstalledArchiveCount = 2
    Assert-InstallerRejects 'Installed CLI archive does not match the same-run candidate.' 1

    Reset-InstallerStubs
    $script:CoverageCandidateAlterInstalledArchive = $true
    Assert-InstallerRejects 'Installed CLI archive does not match the same-run candidate.' 1

    Reset-InstallerStubs
    $script:CoverageCandidateOmitCommand = $true
    Assert-InstallerRejects 'Installed appsurface command was not found.' 1

    Reset-InstallerStubs
    Install-CoverageCandidate
    Assert-Equal 1 $script:CoverageCandidateInstallCalls 'Valid candidate should install once.'
    $actualCommand = if ($IsWindows) { Join-Path $testRoot 'coverage-cli/appsurface.exe' } `
        else { Join-Path $testRoot 'coverage-cli/appsurface' }
    Assert-Equal "COVERAGE_CLI_PATH=$actualCommand" (Get-Content -Raw -LiteralPath $env:GITHUB_ENV).Trim() `
        'Installed command path should be exported.'

    foreach ($forWindows in @($true, $false)) {
        $simulatedRoot = Join-Path $testRoot ('simulated-' + [string]$forWindows)
        New-Item -ItemType Directory -Path $simulatedRoot | Out-Null
        $simulatedName = if ($forWindows) { 'appsurface.exe' } else { 'appsurface' }
        $simulatedCommand = Join-Path $simulatedRoot $simulatedName
        New-Item -ItemType File -Path $simulatedCommand | Out-Null
        Assert-Equal $simulatedCommand (Resolve-CoverageCliCommandPath -ToolRoot $simulatedRoot -ForWindows $forWindows) `
            "Command path resolution should match simulated Windows=$forWindows."
    }

    $missingCommandRoot = Join-Path $testRoot 'missing-command'
    New-Item -ItemType Directory -Path $missingCommandRoot | Out-Null
    Assert-FailsWith {
        Resolve-CoverageCliCommandPath -ToolRoot $missingCommandRoot -ForWindows ([bool]$IsWindows)
    } 'Installed appsurface command was not found'
    Write-Output 'Coverage candidate installer behavioral tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
}
