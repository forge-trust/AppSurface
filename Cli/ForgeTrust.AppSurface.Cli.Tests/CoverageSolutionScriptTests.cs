using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageSolutionScriptTests
{
    [Fact]
    public void Script_ShouldDelegateDefaultLaneToSourceCliWithManagedEvidence()
    {
        var script = ReadScript();

        Assert.Contains("CLI_PROJECT=\"$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj\"", script, StringComparison.Ordinal);
        Assert.Contains("coverage", script, StringComparison.Ordinal);
        Assert.Contains("run", script, StringComparison.Ordinal);
        Assert.DoesNotContain("COVERAGE_RUNNER_PROJECT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  --include ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  --exclude ", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Config.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project AuthAspNetCoreDevAuthExample.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project AuthWebRazorWireProofExample.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.RazorWire.Cli.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--test-results junit", script, StringComparison.Ordinal);
        Assert.Contains("--slow-test-diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("--logger \"GitHubActions;report-warnings=false\"", script, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_GATE_DIFF_BASE", script, StringComparison.Ordinal);
        Assert.Contains("coverage\n  gate", script, StringComparison.Ordinal);
        Assert.Contains("--min-line 95", script, StringComparison.Ordinal);
        Assert.Contains("--min-branch 85", script, StringComparison.Ordinal);
        Assert.Contains("--diff-base \"$COVERAGE_GATE_DIFF_BASE\"", script, StringComparison.Ordinal);
        Assert.Contains("--min-patch-line 95", script, StringComparison.Ordinal);
        Assert.Contains("--min-patch-branch 85", script, StringComparison.Ordinal);
        Assert.Contains("--patch-line-mode codecov", script, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_REQUIRE_NON_SANDBOX:-true", script, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(script, "--no-restore"));
        Assert.Contains("if [[ -n \"$COVERAGE_GATE_DIFF_BASE\" ]]; then", script, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredWrapperInputs))]
    public async Task Script_ShouldRejectRetiredInputsBeforeStartingDotnetOrCreatingOutput(
        string input,
        string[] arguments,
        string? environmentName,
        string? environmentValue,
        string expectedReplacement)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync(arguments, environmentName, environmentValue);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(input, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(expectedReplacement, result.StandardError, StringComparison.Ordinal);
        Assert.False(result.DotnetInvoked, "A rejected wrapper form must not invoke dotnet.");
        Assert.False(result.CoverageOutputCreated, "A rejected wrapper form must not create coverage output.");
    }

    [Fact]
    public void BuildWorkflow_ShouldUseCoverageSolutionScriptForDefaultLane()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("timeout-minutes: 45", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_CONFIGURATION: Release", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_NO_RESTORE: true", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PARALLELISM: 2", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_GATE_DIFF_BASE: ${{ github.event_name == 'pull_request' && 'HEAD^1' || '' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/coverage-solution.sh", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "files: 'TestResults/coverage-merged/coverage.cobertura.xml'\n          disable_search: true",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("coverage run \\", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Gate PR coverage with AppSurface CLI", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Gate baseline coverage with AppSurface CLI", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorkflow_ShouldCaptureCoverageSecurityHangDiagnosticsBeforeTheJobTimeout()
    {
        var workflow = ReadWorkflow();

        Assert.Contains(
            """
              coverage-security-platform:
                name: Coverage security contracts (${{ matrix.os }})
                runs-on: ${{ matrix.os }}
                timeout-minutes: 10
            """,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            """
                      --logger "console;verbosity=detailed"
                      --blame-hang
                      --blame-hang-timeout 2m
                      --blame-hang-dump-type mini

                  - name: Upload coverage security diagnostics
                    if: ${{ always() }}
                    uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
                    with:
                      name: coverage-security-diagnostics-${{ matrix.os }}
                      path: Cli/ForgeTrust.AppSurface.Cli.Tests/TestResults
                      if-no-files-found: warn
                      retention-days: 7
            """,
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageEfficiencyWorkflow_ShouldCaptureCompleteReadOnlyEvidenceWithoutChangingPrValidation()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "coverage-efficiency.yml");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));

        Assert.Contains("name: Coverage Efficiency Evidence", workflow, StringComparison.Ordinal);
        Assert.Single(yaml.Documents);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("cache-state:", workflow, StringComparison.Ordinal);
        Assert.Contains("inputs['cache-state']", workflow, StringComparison.Ordinal);
        Assert.Contains("type: choice", workflow, StringComparison.Ordinal);
        Assert.Contains("- cold", workflow, StringComparison.Ordinal);
        Assert.Contains("- warm", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Contains("Require a trusted evidence ref", workflow, StringComparison.Ordinal);
        Assert.Contains("refs/heads/$DEFAULT_BRANCH", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_CONFIGURATION: Release", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_NO_RESTORE: true", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PARALLELISM: 2", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_GATE_DIFF_BASE: ''", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("COVERAGE_REQUIRE_NON_SANDBOX: false", workflow, StringComparison.Ordinal);
        Assert.Contains("pnpm_store_path=\"$(pnpm store path)\"", workflow, StringComparison.Ordinal);
        Assert.Contains("rm -rf -- \"$pnpm_store_path\"", workflow, StringComparison.Ordinal);
        Assert.Contains("time.monotonic_ns()", workflow, StringComparison.Ordinal);
        Assert.Contains("docker version > \"$evidence_root/docker-version.txt\"", workflow, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlTestContainerImage.cs", workflow, StringComparison.Ordinal);
        Assert.Contains("Directory.Packages.props", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("postgresql_image=\"postgres:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("playwright_package_version=\"1.60.0\"", workflow, StringComparison.Ordinal);
        Assert.Contains("docker image inspect \"$postgresql_image\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-version.txt\" 2>&1 || true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("postgresql-image.json\" 2>&1 || true", workflow, StringComparison.Ordinal);
        Assert.Contains("coverage-exit-code=$coverage_exit_code", workflow, StringComparison.Ordinal);
        Assert.Contains("runtime-capture-exit-code=$runtime_capture_exit_code", workflow, StringComparison.Ordinal);
        Assert.Contains("chromium-executable-unavailable", workflow, StringComparison.Ordinal);
        Assert.Contains("-name chrome -o -name chrome-headless-shell", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing managed JUnit log", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing managed JUnit log path.", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing project test log path.", workflow, StringComparison.Ordinal);
        Assert.Contains("Timings evidence could not be read for per-project validation.", workflow, StringComparison.Ordinal);
        Assert.Contains("Fail incomplete runtime evidence capture", workflow, StringComparison.Ordinal);
        Assert.Contains("environment-manifest.json", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnetSdkVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("dockerServerVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("postgresqlImage", workflow, StringComparison.Ordinal);
        Assert.Contains("playwrightPackageVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("evidence-completeness.json", workflow, StringComparison.Ordinal);
        Assert.Contains("timings.json", workflow, StringComparison.Ordinal);
        Assert.Contains("resolved-serial-set.json", workflow, StringComparison.Ordinal);
        Assert.Contains("precedingParallelBatch", workflow, StringComparison.Ordinal);
        Assert.Contains("barrierCriticalPathRationale", workflow, StringComparison.Ordinal);
        Assert.Contains("junit-coverage-*.xml", workflow, StringComparison.Ordinal);
        Assert.Contains("slow-test-diagnostics.json", workflow, StringComparison.Ordinal);
        Assert.Contains("coverage-normalization.log", workflow, StringComparison.Ordinal);
        Assert.Contains("max_artifact_file_bytes", workflow, StringComparison.Ordinal);
        Assert.Contains("Refusing symlinked evidence source", workflow, StringComparison.Ordinal);
        Assert.Contains("find \"$coverage_root\" -maxdepth 1 -type f -name 'junit-coverage-*.xml'", workflow, StringComparison.Ordinal);
        Assert.Contains("find \"$coverage_root/projects\" -type f", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("shopt -s globstar", workflow, StringComparison.Ordinal);
        Assert.Contains("coverage-efficiency-evidence", workflow, StringComparison.Ordinal);
        Assert.Contains("path: ${{ runner.temp }}/coverage-efficiency-evidence", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("path: TestResults/coverage-merged", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
        Assert.Contains("retention-days: 14", workflow, StringComparison.Ordinal);
        Assert.Contains("coverageStep", workflow, StringComparison.Ordinal);
        Assert.Contains("durationSeconds", workflow, StringComparison.Ordinal);
        Assert.Contains("Fail unsuccessful coverage capture", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageEfficiencyEvidenceTemplates_ShouldDocumentScopeInventoryAndCeilingContract()
    {
        var scope = ReadRepositoryFile("artifacts", "issue-728-test-efficiency", "scope-and-baseline.md");
        var inventory = ReadRepositoryFile("artifacts", "issue-728-test-efficiency", "candidate-inventory.md");
        var results = ReadRepositoryFile("artifacts", "issue-728-test-efficiency", "results.md");

        Assert.Contains("High-resolution monotonic duration", scope, StringComparison.Ordinal);
        Assert.Contains("actual serial set is authoritative", scope, StringComparison.Ordinal);
        Assert.Contains("401 seconds", scope, StringComparison.Ordinal);
        Assert.Contains("relative spread", scope, StringComparison.Ordinal);
        Assert.Contains("manual workflow runs", scope, StringComparison.Ordinal);
        Assert.Contains("with the wrapper's non-sandbox guard enabled", scope, StringComparison.Ordinal);
        Assert.Contains("screening-only, never CI or issue-claim evidence", scope, StringComparison.Ordinal);
        Assert.Contains("Testcontainers and externally configured paths separately", inventory, StringComparison.Ordinal);
        Assert.Contains("aggregate failures only after every cleanup attempt", inventory, StringComparison.Ordinal);
        Assert.Contains("high-resolution monotonic coverage-wrapper duration", results, StringComparison.Ordinal);
        Assert.Contains("at least 15% and at least five seconds", results, StringComparison.Ordinal);
        Assert.Contains("2027-02-11", results, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageEfficiencyCapture_ShouldPreserveCoverageFailureWhenDockerEvidenceFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var captureScript = ExtractWorkflowRunScript("Capture coverage efficiency evidence")
            .Replace("${{ inputs['cache-state'] }}", "warm", StringComparison.Ordinal);
        using var workspace = TemporaryDirectory.Create("appsurface-coverage-efficiency-capture-");
        var binDirectory = Path.Join(workspace.Path, "bin");
        var evidenceRoot = Path.Join(workspace.Path, "evidence");
        var outputPath = Path.Join(workspace.Path, "github-output");
        Directory.CreateDirectory(binDirectory);

        await WriteExecutableAsync(Path.Join(workspace.Path, "scripts", "coverage-solution.sh"), "#!/usr/bin/env bash\nexit 47\n");
        await WriteExecutableAsync(Path.Join(binDirectory, "docker"), "#!/usr/bin/env bash\nprintf '%s\\n' 'Docker unavailable' >&2\nexit 1\n");
        await WriteExecutableAsync(Path.Join(binDirectory, "dotnet"), "#!/usr/bin/env bash\nif [[ \"$1\" == \"--version\" ]]; then echo 10.0.100; exit 0; fi\necho '.NET SDK information'\n");
        await WriteExecutableAsync(Path.Join(binDirectory, "pnpm"), "#!/usr/bin/env bash\necho 11.1.3\n");
        await WriteExecutableAsync(Path.Join(binDirectory, "node"), "#!/usr/bin/env bash\necho v24.0.0\n");

        var scriptPath = Path.Join(workspace.Path, "capture.sh");
        await File.WriteAllTextAsync(scriptPath, captureScript);
        var postgreSqlImageSource = Path.Join(
            workspace.Path,
            "Durable",
            "ForgeTrust.AppSurface.Durable.PostgreSql.Tests",
            "PostgreSqlTestContainerImage.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(postgreSqlImageSource)!);
        await File.WriteAllTextAsync(
            postgreSqlImageSource,
            "internal static class PostgreSqlTestContainerImage { internal const string Reference = \"postgres:17.5@test\"; }\n");
        await File.WriteAllTextAsync(
            Path.Join(workspace.Path, "Directory.Packages.props"),
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.Playwright\" Version=\"1.60.0\" /></ItemGroup></Project>\n");

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["PATH"] = string.Concat(binDirectory, Path.PathSeparator, Environment.GetEnvironmentVariable("PATH"));
        startInfo.Environment["RUNNER_TEMP"] = evidenceRoot;
        startInfo.Environment["GITHUB_OUTPUT"] = outputPath;
        startInfo.Environment["GITHUB_SERVER_URL"] = "https://github.com";
        startInfo.Environment["GITHUB_REPOSITORY"] = "forge-trust/AppSurface";
        startInfo.Environment["GITHUB_RUN_ID"] = "123";
        startInfo.Environment["GITHUB_SHA"] = "abc123";
        startInfo.Environment["GITHUB_REF"] = "refs/heads/test";
        startInfo.Environment["RUNNER_OS"] = "Linux";
        startInfo.Environment["RUNNER_ARCH"] = "X64";
        startInfo.Environment["RUNNER_NAME"] = "test-runner";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start capture script.");
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardErrorTask);

        Assert.True(process.ExitCode == 0, await standardErrorTask);
        Assert.Contains("coverage-exit-code=47", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
        Assert.Contains("runtime-capture-exit-code=1", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);

        var manifest = await File.ReadAllTextAsync(Path.Join(evidenceRoot, "coverage-efficiency-evidence", "environment-manifest.json"));
        Assert.Contains("\"exitCode\": 47", manifest, StringComparison.Ordinal);
        Assert.Contains("\"captureExitCode\": 1", manifest, StringComparison.Ordinal);
        Assert.Contains(
            "Docker unavailable",
            await File.ReadAllTextAsync(Path.Join(evidenceRoot, "coverage-efficiency-evidence", "docker-version.txt")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageEfficiencyAssembly_ShouldWriteCompletenessAndRejectMissingSuccessfulCoverageEvidence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var assemblyScript = ExtractWorkflowRunScript("Assemble coverage efficiency evidence");
        using var workspace = TemporaryDirectory.Create("appsurface-coverage-efficiency-assembly-");
        var runnerTemp = Path.Join(workspace.Path, "runner-temp");
        var evidenceRoot = Path.Join(runnerTemp, "coverage-efficiency-evidence");
        Directory.CreateDirectory(evidenceRoot);

        foreach (var fileName in new[]
                 {
                     "environment-manifest.json",
                     "dotnet-info.txt",
                     "docker-version.txt",
                     "postgresql-image.json",
                     "pnpm-version.txt",
                     "node-version.txt",
                     "playwright-browser-inventory.txt",
                 })
        {
            await File.WriteAllTextAsync(Path.Join(evidenceRoot, fileName), "fixture evidence\n");
        }

        var scriptPath = Path.Join(workspace.Path, "assemble.sh");
        await File.WriteAllTextAsync(scriptPath, assemblyScript);

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["RUNNER_TEMP"] = runnerTemp;
        startInfo.Environment["COVERAGE_EXIT_CODE"] = "0";
        startInfo.Environment["RUNTIME_CAPTURE_EXIT_CODE"] = "0";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start assembly script.");
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardErrorTask);

        Assert.NotEqual(0, process.ExitCode);
        var standardError = await standardErrorTask;

        var completenessPath = Path.Join(evidenceRoot, "evidence-completeness.json");
        Assert.True(File.Exists(completenessPath), standardError);
        var completeness = await File.ReadAllTextAsync(completenessPath);
        Assert.Contains("\"captureStatus\": \"failed\"", completeness, StringComparison.Ordinal);
        Assert.Contains("\"artifactContractComplete\": false", completeness, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageEfficiencyAssembly_ShouldEmitCompleteManifestForValidSuccessfulCoverageEvidence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var assemblyScript = ExtractWorkflowRunScript("Assemble coverage efficiency evidence");
        using var workspace = TemporaryDirectory.Create("appsurface-coverage-efficiency-assembly-success-");
        var runnerTemp = Path.Join(workspace.Path, "runner-temp");
        var evidenceRoot = Path.Join(runnerTemp, "coverage-efficiency-evidence");
        Directory.CreateDirectory(evidenceRoot);

        foreach (var fileName in new[]
                 {
                     "environment-manifest.json",
                     "dotnet-info.txt",
                     "docker-version.txt",
                     "postgresql-image.json",
                     "pnpm-version.txt",
                     "node-version.txt",
                     "playwright-browser-inventory.txt",
                 })
        {
            await File.WriteAllTextAsync(Path.Join(evidenceRoot, fileName), "fixture evidence\n");
        }

        var coverageRoot = Path.Join(workspace.Path, "TestResults", "coverage-merged");
        Directory.CreateDirectory(Path.Join(coverageRoot, "projects", "fixture"));
        await File.WriteAllTextAsync(
            Path.Join(coverageRoot, "timings.json"),
            """
            {
              "schedule": { "strategy": "fixture" },
              "projects": [
                {
                  "project": "Fixture.Tests.csproj",
                  "originalIndex": 0,
                  "executionIndex": 0,
                  "exclusive": true,
                  "scheduleReason": "fixture",
                  "executionStatus": "completed",
                  "log": "projects/fixture/dotnet-test.log",
                  "coverageCleanupLog": null,
                  "testResults": [ { "format": "junit", "path": "junit-coverage-fixture.xml" } ]
                }
              ]
            }
            """);
        foreach (var fileName in new[]
                 {
                     "slow-test-diagnostics.md",
                     "slow-test-diagnostics.json",
                     "coverage.cobertura.xml",
                     "summary.txt",
                     "coverage-gate.json",
                     "coverage-gate.md",
                     "junit-coverage-fixture.xml",
                 })
        {
            await File.WriteAllTextAsync(Path.Join(coverageRoot, fileName), "fixture coverage output\n");
        }
        await File.WriteAllTextAsync(Path.Join(coverageRoot, "projects", "fixture", "dotnet-test.log"), "fixture test log\n");

        var scriptPath = Path.Join(workspace.Path, "assemble.sh");
        await File.WriteAllTextAsync(scriptPath, assemblyScript);

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["RUNNER_TEMP"] = runnerTemp;
        startInfo.Environment["COVERAGE_EXIT_CODE"] = "0";
        startInfo.Environment["RUNTIME_CAPTURE_EXIT_CODE"] = "0";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start assembly script.");
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardErrorTask);

        var standardError = await standardErrorTask;
        Assert.True(process.ExitCode == 0, standardError);
        Assert.True(string.IsNullOrWhiteSpace(standardError), standardError);

        var completeness = await File.ReadAllTextAsync(Path.Join(evidenceRoot, "evidence-completeness.json"));
        Assert.Contains("\"captureStatus\": \"complete\"", completeness, StringComparison.Ordinal);
        Assert.Contains("\"artifactContractComplete\": true", completeness, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(evidenceRoot, "coverage-output", "resolved-serial-set.json")));
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldContainExistingProductionSources()
    {
        Assert.True(RazorWireCoverageProofSources.All.Count >= 2);

        foreach (var source in RazorWireCoverageProofSources.All)
        {
            Assert.True(File.Exists(FindRepositoryFile(source.Split('/'))), $"Expected RazorWire proof source '{source}' to exist.");
        }
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldSelectConfiguredCoveredSourceInPreferenceOrder()
    {
        var secondSource = RazorWireCoverageProofSources.All[1];

        Assert.Equal(
            secondSource,
            RazorWireCoverageProofSources.SelectCoveredSource([secondSource.Replace('/', '\\')]));
        Assert.Equal(
            RazorWireCoverageProofSources.All[0],
            RazorWireCoverageProofSources.SelectCoveredSource([secondSource, RazorWireCoverageProofSources.All[0]]));
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldRejectWhenNoConfiguredSourceIsCovered()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorWireCoverageProofSources.SelectCoveredSource(["Web/Unrelated/NotCovered.cs"]));

        Assert.Equal(
            $"Merged Cobertura did not contain a maintained RazorWire proof source. Expected one of: {string.Join(", ", RazorWireCoverageProofSources.All)}.",
            exception.Message);
    }

    [Fact]
    public void LegacyCoverageRunnerSources_ShouldNotExist()
    {
        var repositoryRoot = Path.GetDirectoryName(FindRepositoryFile("ForgeTrust.AppSurface.slnx"))!;

        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner", "ForgeTrust.AppSurface.CoverageRunner.csproj")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner", "Program.cs")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner.Tests", "ForgeTrust.AppSurface.CoverageRunner.Tests.csproj")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner.Tests", "CoverageRunnerApplicationTests.cs")));
    }

    [Theory]
    [InlineData("origin/main", true)]
    [InlineData("", false)]
    public async Task Script_ShouldForwardPatchGateArgumentsOnlyWhenDiffBaseIsNonEmpty(string diffBase, bool expectsPatchGate)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], "COVERAGE_GATE_DIFF_BASE", diffBase, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        var invocations = result.DotnetInvocations;
        Assert.Equal(2, invocations.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(argument => argument == "coverage"));
        Assert.Contains("run", invocations, StringComparison.Ordinal);
        Assert.Contains("gate", invocations, StringComparison.Ordinal);

        if (expectsPatchGate)
        {
            Assert.Contains("--diff-base\norigin/main\n--min-patch-line\n95\n--min-patch-branch\n85\n--patch-line-mode\ncodecov", invocations, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("--diff-base", invocations, StringComparison.Ordinal);
            Assert.DoesNotContain("--min-patch-line", invocations, StringComparison.Ordinal);
            Assert.DoesNotContain("--min-patch-branch", invocations, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Script_ShouldDefaultPatchGateToOriginMainWhenDiffBaseIsUnset()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], null, null, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--diff-base\norigin/main\n--min-patch-line\n95\n--min-patch-branch\n85",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ShouldForwardNoRestoreToCoverageRunWhenRequested()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], "BUILD_NO_RESTORE", "true", dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--logger\nGitHubActions;report-warnings=false\n--no-restore",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    [InlineData("0", true)]
    [InlineData("off", true)]
    [InlineData("no", true)]
    [InlineData("False", true)]
    [InlineData("invalid", true)]
    public async Task Script_ShouldRequireNonSandboxByDefaultWithExplicitRestrictedRunEscapeHatch(
        string? requireNonSandbox,
        bool expectsRequirement)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync(
            [],
            "COVERAGE_REQUIRE_NON_SANDBOX",
            requireNonSandbox,
            dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        if (expectsRequirement)
        {
            Assert.Contains("--require-non-sandbox", result.DotnetInvocations, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("--require-non-sandbox", result.DotnetInvocations, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Script_ShouldForwardResourceIntensiveSuitesAsExclusiveCoverageProjects()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], null, null, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--exclusive-test-project\nForgeTrust.AppSurface.Config.Tests.csproj\n--exclusive-test-project\nAuthAspNetCoreDevAuthExample.Tests.csproj\n--exclusive-test-project\nAuthWebRazorWireProofExample.Tests.csproj\n--exclusive-test-project\nForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj\n--exclusive-test-project\nForgeTrust.RazorWire.Cli.Tests.csproj\n--exclusive-test-project\nForgeTrust.AppSurface.Web.Tailwind.Tests.csproj",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> RetiredWrapperInputs()
    {
        yield return new object[] { "arguments", new[] { "custom.slnx", "custom-output" }, null!, null!, "appsurface coverage run --solution" };
        yield return new object[] { "--group", new[] { "--group", "web" }, null!, null!, "appsurface coverage run --test-project" };
        yield return new object[] { "--list-groups", new[] { "--list-groups" }, null!, null!, "appsurface coverage run --help" };
        yield return new object[] { "--merge-only", new[] { "--merge-only", "coverage-shards" }, null!, null!, "appsurface coverage merge --source" };
        yield return new object[] { "arguments", new[] { "--output", "custom-output" }, null!, null!, "appsurface coverage run --solution" };
        yield return new object[] { "TEST_GROUP", Array.Empty<string>(), "TEST_GROUP", "all", "appsurface coverage run --test-project" };
        yield return new object[] { "INCLUDE_FILTER", Array.Empty<string>(), "INCLUDE_FILTER", "[Sample]*", "appsurface coverage run --include" };
        yield return new object[] { "EXCLUDE_FILTER", Array.Empty<string>(), "EXCLUDE_FILTER", "[Generated]*", "appsurface coverage run --exclude" };
        yield return new object[] { "BUILD_SOLUTION", Array.Empty<string>(), "BUILD_SOLUTION", "true", "appsurface coverage run --no-build" };
    }

    private static string ReadScript()
        => ReadRepositoryFile("scripts", "coverage-solution.sh");

    private static string ReadWorkflow()
        => ReadRepositoryFile(".github", "workflows", "build.yml");

    private static string ExtractWorkflowRunScript(string stepName)
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "coverage-efficiency.yml");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var captureEvidence = (YamlMappingNode)jobs.Children[new YamlScalarNode("capture-evidence")];
        var steps = (YamlSequenceNode)captureEvidence.Children[new YamlScalarNode("steps")];

        var step = steps.Children
            .OfType<YamlMappingNode>()
            .Single(candidate => string.Equals(
                ((YamlScalarNode)candidate.Children[new YamlScalarNode("name")]).Value,
                stepName,
                StringComparison.Ordinal));

        return ((YamlScalarNode)step.Children[new YamlScalarNode("run")]).Value
            ?? throw new InvalidOperationException($"Workflow step '{stepName}' has no run script.");
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Executable script fixtures require Unix file permissions.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }

    private static async Task<ScriptResult> RunScriptAsync(
        IReadOnlyList<string> arguments,
        string? environmentName,
        string? environmentValue,
        int dotnetExitCode = 97)
    {
        using var workspace = TemporaryDirectory.Create("appsurface-coverage-script-");
        var scriptPath = Path.Join(workspace.Path, "scripts", "coverage-solution.sh");
        var binDirectory = Path.Join(workspace.Path, "bin");
        var dotnetInvocationMarker = Path.Join(workspace.Path, "dotnet-invoked");
        var coverageOutputDirectory = Path.Join(workspace.Path, "TestResults", "coverage-merged");
        var dotnetStub = Path.Join(binDirectory, "dotnet");

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        Directory.CreateDirectory(binDirectory);
        await File.WriteAllTextAsync(scriptPath, ReadScript());
        await File.WriteAllTextAsync(
            dotnetStub,
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" >> \"$DOTNET_INVOCATION_MARKER\"\nexit \"$DOTNET_STUB_EXIT_CODE\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                dotnetStub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var retiredEnvironmentName in new[] { "TEST_GROUP", "INCLUDE_FILTER", "EXCLUDE_FILTER", "BUILD_SOLUTION" })
        {
            startInfo.Environment.Remove(retiredEnvironmentName);
        }

        startInfo.Environment.Remove("COVERAGE_GATE_DIFF_BASE");

        startInfo.Environment["DOTNET_INVOCATION_MARKER"] = dotnetInvocationMarker;
        startInfo.Environment["DOTNET_STUB_EXIT_CODE"] = dotnetExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["PATH"] = string.Concat(binDirectory, Path.PathSeparator, Environment.GetEnvironmentVariable("PATH"));
        if (environmentName is not null)
        {
            startInfo.Environment[environmentName] = environmentValue;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start bash.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardOutputTask, standardErrorTask);

        return new ScriptResult(
            process.ExitCode,
            await standardErrorTask,
            File.Exists(dotnetInvocationMarker),
            File.Exists(dotnetInvocationMarker) ? await File.ReadAllTextAsync(dotnetInvocationMarker) : string.Empty,
            Directory.Exists(coverageOutputDirectory));
    }

    private static string ReadRepositoryFile(params string[] paths)
        => File.ReadAllText(FindRepositoryFile(paths));

    private static string FindRepositoryFile(params string[] paths)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var file = Path.Join([directory.FullName, .. paths]);
            if (File.Exists(file))
            {
                return file;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Join(paths)} from the test working directory.");
    }

    private sealed record ScriptResult(
        int ExitCode,
        string StandardError,
        bool DotnetInvoked,
        string DotnetInvocations,
        bool CoverageOutputCreated);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), string.Concat(prefix, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
