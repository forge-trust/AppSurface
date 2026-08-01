[CmdletBinding()]
param(
    [int]$Port = 5055,
    [string]$EvidencePath = (Join-Path $PSScriptRoot "pwa-verify-v3.json"),
    [int]$StartupTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $repoRoot "examples/web-pwa-install/WebPwaInstallExample.csproj"
$cliProject = Join-Path $repoRoot "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = Split-Path -Parent $evidenceFullPath
$runId = [Guid]::NewGuid().ToString("N")
$stdoutPath = Join-Path ([IO.Path]::GetTempPath()) "appsurface-pwa-$runId.out.log"
$stderrPath = Join-Path ([IO.Path]::GetTempPath()) "appsurface-pwa-$runId.err.log"
$hostProcess = $null

function Stop-ChildHost {
    param([Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill($true)
            if (-not $Process.WaitForExit(5000)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

try {
    & dotnet build $project -p:UseSharedCompilation=false -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        throw "The example build failed with exit code $LASTEXITCODE."
    }

    $arguments = @(
        "run", "--project", $project, "--no-build", "--",
        "--environment", "Development",
        "--port", $Port
    )
    $hostProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList $arguments `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$Port/manifest.webmanifest" `
                -UseBasicParsing `
                -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                $ready = $true
                break
            }
        }
        catch {
            # The bounded loop owns startup retries; child logs explain terminal failure.
        }

        if ($hostProcess.HasExited) {
            break
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        if (Test-Path -LiteralPath $stdoutPath) {
            Write-Host "--- child stdout ---"
            Get-Content -LiteralPath $stdoutPath | Write-Host
        }
        if (Test-Path -LiteralPath $stderrPath) {
            Write-Host "--- child stderr ---"
            Get-Content -LiteralPath $stderrPath | Write-Host
        }
        throw "The example host did not become ready within $StartupTimeoutSeconds seconds."
    }

    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
    & dotnet run --project $cliProject -p:UseSharedCompilation=false -- pwa verify `
        --surface all `
        --base-url "http://127.0.0.1:$Port" `
        --entry-path /account/resume `
        --expect-start-url / `
        --expect-scope / `
        --expect-display standalone `
        --expect-theme-color '#2563eb' `
        --expect-background-color '#ffffff' `
        --expect-icon 192x192 `
        --expect-icon 512x512 `
        --expect-push enabled `
        --json > $evidenceFullPath
    $verifyExitCode = $LASTEXITCODE

    Write-Host "Wrote schema-v3 PWA verification evidence to $evidenceFullPath"
    if ($verifyExitCode -ne 0) {
        exit $verifyExitCode
    }
}
finally {
    Stop-ChildHost -Process $hostProcess
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
}
