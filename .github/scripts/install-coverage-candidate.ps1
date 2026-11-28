<#
.SYNOPSIS
Verifies and installs the same-run coverage CLI candidate package.
#>

function Resolve-CoverageCliCommandPath {
    param(
        [Parameter(Mandatory)][string]$ToolRoot,
        [Parameter(Mandatory)][bool]$ForWindows
    )

    $name = if ($ForWindows) { 'appsurface.exe' } else { 'appsurface' }
    $command = Join-Path $ToolRoot $name
    if (!(Test-Path -LiteralPath $command -PathType Leaf)) {
        throw 'Installed appsurface command was not found.'
    }
    return $command
}

function Install-CoverageCandidate {
    $ErrorActionPreference = 'Stop'
    foreach ($name in @('RUNNER_TEMP', 'COVERAGE_SOURCE_COMMIT', 'COVERAGE_SDK_VERSION',
            'COVERAGE_CLI_CANDIDATE_VERSION', 'GITHUB_ENV')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Required coverage candidate environment variable is missing: $name"
        }
    }

    $feed = Join-Path $env:RUNNER_TEMP 'coverage-candidate-feed'
    $manifestPath = Join-Path $feed 'candidate.json'
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -ErrorAction Stop

    $commitOutput = @(& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $commitOutput.Count -ne 1) {
        throw 'Could not verify the checked-out coverage source commit.'
    }
    $commit = ([string]$commitOutput[0]).Trim()
    $treeOutput = @(& git rev-parse 'HEAD^{tree}')
    if ($LASTEXITCODE -ne 0 -or $treeOutput.Count -ne 1) {
        throw 'Could not verify the checked-out coverage source tree.'
    }
    $tree = ([string]$treeOutput[0]).Trim()
    $sdkOutput = @(& dotnet --version)
    if ($LASTEXITCODE -ne 0 -or $sdkOutput.Count -ne 1) {
        throw 'Could not verify the selected coverage SDK.'
    }
    $sdk = ([string]$sdkOutput[0]).Trim()

    $package = Join-Path $feed "ForgeTrust.AppSurface.Cli.$env:COVERAGE_CLI_CANDIDATE_VERSION.nupkg"
    if ($manifest.commit -ne $env:COVERAGE_SOURCE_COMMIT -or $manifest.commit -ne $commit -or
        $manifest.tree -ne $tree -or $manifest.sdk -ne $sdk -or
        $manifest.sdk -ne $env:COVERAGE_SDK_VERSION -or
        $manifest.version -ne $env:COVERAGE_CLI_CANDIDATE_VERSION -or
        !(Test-Path -LiteralPath $package -PathType Leaf)) {
        throw 'Candidate CLI package source, SDK, or pinned version does not match this lane.'
    }
    $downloadedHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $manifest.packageSha256) {
        throw 'Candidate CLI package hash does not match its same-run manifest.'
    }

    $toolRoot = Join-Path $env:RUNNER_TEMP 'coverage-cli'
    $toolConfig = Join-Path $feed 'candidate.nuget.config'
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    $configContent = @"
<configuration>
  <packageSources><clear /><add key="candidate" value="$escapedFeed" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
"@
    Set-Content -LiteralPath $toolConfig -Value $configContent

    & dotnet tool install ForgeTrust.AppSurface.Cli --tool-path $toolRoot `
        --version $env:COVERAGE_CLI_CANDIDATE_VERSION --configfile $toolConfig
    if ($LASTEXITCODE -ne 0) {
        throw 'Candidate CLI installation failed.'
    }

    $storeRoot = Join-Path $toolRoot '.store'
    if (!(Test-Path -LiteralPath $storeRoot -PathType Container)) {
        throw 'Installed CLI archive does not match the same-run candidate.'
    }
    $installedArchives = @(Get-ChildItem -LiteralPath $storeRoot -Recurse -File |
        Where-Object { $_.Name -ieq (Split-Path -Leaf $package) })
    if ($installedArchives.Count -ne 1) {
        throw 'Installed CLI archive does not match the same-run candidate.'
    }
    $installedHash = (Get-FileHash -LiteralPath $installedArchives[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installedHash -ne $manifest.packageSha256) {
        throw 'Installed CLI archive does not match the same-run candidate.'
    }

    $command = Resolve-CoverageCliCommandPath -ToolRoot $toolRoot -ForWindows ([bool]$IsWindows)
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "COVERAGE_CLI_PATH=$command"
}

if ($MyInvocation.InvocationName -ne '.') {
    Install-CoverageCandidate
}
