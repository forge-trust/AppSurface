using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage.NativeInventory;
using ForgeTrust.AppSurface.Testing;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class RepositoryEvidenceWorkflowContractTests
{
    private static readonly string[] ExpectedSharedCliSelectors =
    [
        "FullyQualifiedName~ForgeTrust.AppSurface.Cli.Tests.Evidence",
        "FullyQualifiedName~ForgeTrust.AppSurface.Cli.Tests.CoverageEvidence",
        "FullyQualifiedName~EvidenceTestProducerPlannerTests",
        "FullyQualifiedName~EvidenceArtifactFileValidationTests",
        "FullyQualifiedName~EvidenceRunTestProducerTests",
        "FullyQualifiedName~TestRunReconcilerTests",
        "FullyQualifiedName~DotnetCliTestRunSourceTests",
        "FullyQualifiedName~TestEvidenceProducerTests",
        "FullyQualifiedName~TestEvidenceProducerProcessTests",
        "FullyQualifiedName~VsTestRunnerProbeTests",
    ];

    private static readonly string[] ExpectedNativeCliOnlySelectors =
    [
        "FullyQualifiedName~CoverageFanInSolutionCoverageProducerTests",
        "FullyQualifiedName~CoverageArtifactMetadataDurabilityTests",
        "FullyQualifiedName~CoverageArtifactEnumerationDecisionTests",
    ];

    private static readonly string[] ExpectedCoverageSecurityOnlySelectors =
    [
        "FullyQualifiedName~CoverageRunArtifactReaderTests",
        "FullyQualifiedName~CoverageRunOutputGuardSecurityTests",
        "FullyQualifiedName~CoverageGateTests",
        "FullyQualifiedName~CoverageArtifact",
        "FullyQualifiedName~CoverageStorageCommand",
        "FullyQualifiedName~CoverageRunProcessContractTests",
        "FullyQualifiedName~CoverageRunCollectorVersionPreflightTests",
        "FullyQualifiedName~CoverageFanIn",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidenceHost",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidencePrepared",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidenceProducerKeyMaterialExecution",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidenceKeyMaterialFailure",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidenceReusePackageProofTests",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.EvidencePackedReuseJourneyTests",
        "FullyQualifiedName~ForgeTrust.AppSurface.Aspire.Tests.NativeEvidenceProgramTests",
    ];

    private readonly YamlMappingNode _workflow;
    private readonly YamlMappingNode _buildWorkflow;
    private readonly YamlMappingNode _packageWorkflow;

    public RepositoryEvidenceWorkflowContractTests()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        _workflow = LoadWorkflow(repository, "native-evidence-validation.yml");
        _buildWorkflow = LoadWorkflow(repository, "build.yml");
        _packageWorkflow = LoadWorkflow(repository, "package-gate.yml");
    }

    [Theory]
    [InlineData("code-quality.yml", "dotnet-format", "Build project")]
    [InlineData("package-gate.yml", "package-gate", "Run and verify scoped package evidence")]
    [InlineData("build.yml", "coverage-collection", "Verify Durable template release artifact transport")]
    [InlineData("package-artifacts.yml", "package-artifacts", "Verify package artifacts")]
    [InlineData("nuget-prerelease-publish.yml", "pack-and-verify", "Verify package artifacts")]
    [InlineData("nuget-stable-publish.yml", "pack-and-verify", "Verify package artifacts")]
    [InlineData("release-publish.yml", "publish-docs-archive", "Build AppSurface Docs exporter")]
    [InlineData("coverage-efficiency.yml", "capture-evidence", "Capture coverage efficiency evidence")]
    public void FullSolutionWorkersPrepareTheSdkPairedAspireBundleBeforeBuilding(string workflow, string job, string firstBuild)
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var document = LoadWorkflow(repository, workflow);
        var steps = document["jobs"].AsMapping()[job].AsMapping()["steps"].AsSequence();
        var prepare = FindStep(steps, "Prepare pinned Aspire CLI bundle");
        Assert.True(IndexOf(steps, prepare) < IndexOf(steps, FindStep(steps, firstBuild)));
        var appHost = XDocument.Load(TestPathUtils.PathUnder(repository, "examples", "aspire-apphost", "AspireAppHostExample.csproj"));
        var version = (string)appHost.Descendants("Sdk").Single(sdk => (string?)sdk.Attribute("Name") == "Aspire.AppHost.Sdk").Attribute("Version")!;
        Assert.Equal($"dnx --yes aspire.cli@{version} -- setup --install-path \"$HOME/.aspire\" --non-interactive", prepare["run"].AsScalar());
        Assert.False(prepare.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        if (workflow == "package-gate.yml")
            Assert.Equal("${{ steps.plan.outputs.requires_package_checks == 'true' }}", prepare["if"].AsScalar());
        if (workflow is "nuget-prerelease-publish.yml" or "nuget-stable-publish.yml")
            Assert.Equal(FindStep(steps, firstBuild)["if"].AsScalar(), prepare["if"].AsScalar());
    }

    [Fact]
    public void EveryDotnetWorkerInstallsThePinnedSdkOutsideThePreinstalledToolchain()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        using var global = JsonDocument.Parse(File.ReadAllText(TestPathUtils.PathUnder(repository, "global.json")));
        var sdk = global.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.402", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());

        var setups = 0;
        foreach (var path in Directory.EnumerateFiles(TestPathUtils.PathUnder(repository, ".github", "workflows"), "*.yml"))
        {
            var workflow = LoadWorkflow(repository, Path.GetFileName(path));
            foreach (var job in workflow["jobs"].AsMapping().Children.Values.OfType<YamlMappingNode>())
            {
                if (!job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps))
                    continue;
                foreach (var step in steps.AsSequence().Children.Cast<YamlMappingNode>())
                {
                    if (!step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses) ||
                        !uses.AsScalar().StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))
                        continue;
                    setups++;
                    var inputs = step["with"].AsMapping();
                    Assert.Equal("${{ runner.temp }}/appsurface-dotnet", step["env"].AsMapping()["DOTNET_INSTALL_DIR"].AsScalar());
                    Assert.Contains(inputs["dotnet-version"].AsScalar().Trim(), new[] { "10.0.402", "${{ env.COVERAGE_SDK_VERSION }}" });
                    if (inputs["dotnet-version"].AsScalar().Trim() == "${{ env.COVERAGE_SDK_VERSION }}")
                        Assert.Equal("10.0.402", workflow["env"].AsMapping()["COVERAGE_SDK_VERSION"].AsScalar());
                }
            }
        }
        Assert.True(setups > 0);
    }

    [Fact]
    public void NativeCoveragePreparesPackedReuseFeedAfterLockedRestoreAndBeforeCollection()
    {
        var job = _buildWorkflow["jobs"].AsMapping()["coverage-security-platform"].AsMapping();
        var securitySteps = job["steps"].AsSequence();
        var restoreStep = FindStep(securitySteps, "Restore coverage security tests (locked)");
        var preparationStep = FindStep(securitySteps, "Prepare packed reuse journey feed");
        Assert.Equal("${{ !matrix.keycloak }}", restoreStep["if"].AsScalar());
        Assert.Equal("${{ !matrix.keycloak }}", preparationStep["if"].AsScalar());
        Assert.True(IndexOf(securitySteps, restoreStep) < IndexOf(securitySteps, preparationStep));
        var prepare = preparationStep["run"].AsScalar();
        Assert.Contains("--operation pack-candidate-feed --mode reuse", prepare);
        Assert.Contains("--source-commit $env:COVERAGE_SOURCE_COMMIT", prepare);
        Assert.Contains("--no-restore true", prepare);
        const string publishFeed = "APPSURFACE_PACKED_JOURNEY_FEED=$feed";
        Assert.Contains(publishFeed, prepare);
        Assert.Contains("APPSURFACE_PACKED_JOURNEY_VERSION=$env:COVERAGE_CLI_CANDIDATE_VERSION", prepare);
        const string checkExit = "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }";
        Assert.Contains(checkExit, prepare);
        Assert.True(prepare.IndexOf(checkExit, StringComparison.Ordinal) < prepare.IndexOf(publishFeed, StringComparison.Ordinal));
        var collectorStep = FindStep(securitySteps, "Run coverage security contracts");
        Assert.True(IndexOf(securitySteps, preparationStep) < IndexOf(securitySteps, collectorStep));
    }

    [Fact]
    public void NativeCollectorFilterMatchesTheOrderedSelectorPolicy()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var job = _buildWorkflow["jobs"].AsMapping()["coverage-security-platform"].AsMapping();
        var securitySteps = job["steps"].AsSequence();
        var collectorStep = FindStep(securitySteps, "Run coverage security contracts");
        var collector = collectorStep["run"].AsScalar();
        var inventory = ReadSelectorInventory(repository);
        var selectors = inventory.Where(entry => entry.Scope is "native" or "shared-cli")
            .Select(entry => entry.Selector).ToArray();
        var expected = ExpectedCoverageSecurityOnlySelectors.Take(8)
            .Concat(ExpectedSharedCliSelectors.Take(2))
            .Concat(ExpectedCoverageSecurityOnlySelectors.Skip(8))
            .Concat(ExpectedSharedCliSelectors.Skip(2))
            .ToArray();

        Assert.Equal(expected, selectors);
        Assert.Equal(1437, System.Text.Encoding.UTF8.GetByteCount(string.Join("|", selectors)));
        var lockedRestore = FindStep(securitySteps, "Restore coverage security tests (locked)")["run"].AsScalar();
        const string loadProjects = "$projects = @(Get-Content -LiteralPath '.github/workflows/native-coverage-test-projects.txt')";
        Assert.Contains(loadProjects, lockedRestore);
        Assert.Contains("foreach ($project in $projects)", lockedRestore);
        Assert.Contains("dotnet restore $project --locked-mode", lockedRestore);
        const string collectionDirectory = "${{ runner.temp }}/coverage-native-results";
        Assert.Contains($"'--output', \"{collectionDirectory}\"", collector);
        var captureStep = FindStep(securitySteps, "Attest successful native coverage evidence");
        Assert.True(IndexOf(securitySteps, collectorStep) < IndexOf(securitySteps, captureStep));
        var capture = captureStep["run"].AsScalar();
        Assert.Contains($"'--collection-root', \"{collectionDirectory}\"", capture);
        Assert.Contains("$contractVersion = if ($env:GITHUB_EVENT_NAME -eq 'pull_request') { '3' } else { '2' }", capture);
        Assert.Contains("'--contract-version', $contractVersion", capture);
    }

    [Fact]
    public void NativeCliSelectorCompositionIncludesTheSharedAndLaneSpecificTests()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var inventory = ReadSelectorInventory(repository);
        var sharedSelectors = inventory.Where(entry => entry.Scope == "shared-cli")
            .Select(entry => entry.Selector).ToArray();
        Assert.Equal(ExpectedSharedCliSelectors, sharedSelectors);
        var lifecycle = FindStep(NativeJob["steps"].AsSequence(), "Test native lifecycle contracts for selected producers")["run"].AsScalar();
        var exclusiveSelectors = inventory.Where(entry => entry.Scope == "lifecycle-cli")
            .Select(entry => entry.Selector).ToArray();
        var composedSelectors = inventory.Where(entry => entry.Scope is "lifecycle-cli" or "shared-cli")
            .Select(entry => entry.Selector).ToArray();

        Assert.Equal(ExpectedNativeCliOnlySelectors, exclusiveSelectors);
        Assert.Equal(ExpectedNativeCliOnlySelectors.Concat(ExpectedSharedCliSelectors), composedSelectors);
        Assert.Contains("dotnet restore $dogfoodProject --locked-mode", lifecycle);
        Assert.Contains("dotnet build $dogfoodProject --configuration Release --no-restore", lifecycle);
        Assert.Contains("$cliFilter = & dotnet $dogfood --print-test-filter lifecycle-cli", lifecycle);
        Assert.Contains("dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj", lifecycle);
        Assert.Contains("--filter $cliFilter", lifecycle);
        Assert.Contains("if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }", lifecycle);
    }

    [Fact]
    public void NativeWindowsLaneRetainsBothSuitesWithItsBoundedExecutionAllowance()
    {
        Assert.Equal("${{ matrix.os == 'windows-latest' && 45 || 25 }}", NativeJob["timeout-minutes"].AsScalar());
        var command = FindStep(NativeJob["steps"].AsSequence(), "Test native lifecycle contracts for selected producers")["run"].AsScalar();
        Assert.Contains("dotnet test Aspire/ForgeTrust.AppSurface.Aspire.Tests/ForgeTrust.AppSurface.Aspire.Tests.csproj", command);
        Assert.Contains("dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj", command);
    }

    [Fact]
    public void NativeProducerInventoryIsClosedAndAddsExactlyTheTwoKeycloakArm64Lanes()
    {
        var baseline = NativeCoverageProducerInventory.Create(false);
        var keycloak = NativeCoverageProducerInventory.Create(true);

        Assert.Equal(3, baseline.Count);
        Assert.Equal(["linux", "windows", "darwin"], baseline.Select(producer => producer.Id));
        Assert.Equal(5, keycloak.Count);
        Assert.Equal(["linux", "windows", "darwin", "darwin-arm64-keycloak", "linux-arm64-keycloak"],
            keycloak.Select(producer => producer.Id));
        Assert.All(keycloak.Where(producer => producer.Keycloak), producer =>
        {
            Assert.Equal("Arm64", producer.OsArchitecture);
            Assert.Equal("Arm64", producer.ProcessArchitecture);
            Assert.NotEmpty(producer.Filter);
        });
        Assert.Equal(
            [NativeCoverageProducerInventory.KeycloakAdapterTestProject, NativeCoverageProducerInventory.KeycloakCoreTestProject],
            keycloak.Single(producer => producer.Id == "darwin-arm64-keycloak").TestProjects);
        Assert.Equal(
            "FullyQualifiedName~ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests|FullyQualifiedName~ForgeTrust.AppSurface.Auth.Keycloak.Tests",
            keycloak.Single(producer => producer.Id == "darwin-arm64-keycloak").Filter);
        Assert.Equal([NativeCoverageProducerInventory.KeycloakAdapterTestProject],
            keycloak.Single(producer => producer.Id == "linux-arm64-keycloak").TestProjects);
        Assert.Equal("FullyQualifiedName~AppSurfaceKeycloakLinuxArm64WorkerTests",
            keycloak.Single(producer => producer.Id == "linux-arm64-keycloak").Filter);

        var serialized = JsonDocument.Parse(NativeCoverageProducerInventory.SerializeWorkflowInventory(true));
        Assert.Equal(5, serialized.RootElement.GetProperty("cardinality").GetInt32());
        var matrix = serialized.RootElement.GetProperty("matrix").GetProperty("include").EnumerateArray().ToArray();
        Assert.Equal(keycloak.Count, matrix.Length);
        for (var index = 0; index < keycloak.Count; index++)
        {
            Assert.Equal(keycloak[index].Id, matrix[index].GetProperty("producerId").GetString());
            Assert.Equal(keycloak[index].Runner, matrix[index].GetProperty("runner").GetString());
            Assert.Equal(keycloak[index].TestProjects,
                matrix[index].GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()));
            Assert.Equal(keycloak[index].Filter, matrix[index].GetProperty("filter").GetString());
            Assert.Equal(keycloak[index].Keycloak, matrix[index].GetProperty("keycloak").GetBoolean());
        }
    }

    [Fact]
    public void ValidationBuildPrecedesEventBoundCollection()
    {
        var steps = NativeJob["steps"].AsSequence();
        var build = FindStep(steps, "Restore and build native validation");
        var collect = FindStep(steps, "Collect event-bound changed paths");

        Assert.True(IndexOf(steps, build) < IndexOf(steps, collect));
        Assert.Contains("steps.build.outcome == 'success'", collect["if"].AsScalar());
        var command = collect["run"].AsScalar();
        Assert.Contains("--operation collect", command);
        Assert.Contains("--repository $env:GITHUB_WORKSPACE", command);
        Assert.Contains("--event-file $env:GITHUB_EVENT_PATH", command);
        Assert.Contains("--event-name $env:GITHUB_EVENT_NAME", command);
        Assert.Contains("--workflow-sha $env:GITHUB_SHA", command);
        Assert.Contains("source-state.json", command);
        Assert.Contains("$sourceState.headCommit", command);
    }

    [Fact]
    public void PullRequestsCheckOutTheExactEventHeadWithFullHistory()
    {
        var checkout = FindStep(NativeJob["steps"].AsSequence(), "Checkout event source commit and full history");
        var with = checkout["with"].AsMapping();

        Assert.Equal("${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}", with["ref"].AsScalar());
        Assert.Equal("0", with["fetch-depth"].AsScalar());
        Assert.Equal("false", with["persist-credentials"].AsScalar());
    }

    [Fact]
    public void WorkflowUsesCSharpChangesAndRunsRequiredContractsForNonemptyPlans()
    {
        var steps = NativeJob["steps"].AsSequence();
        var source = string.Join("\n", steps.Select(step => step.AsMapping().Children.TryGetValue(new YamlScalarNode("run"), out var run) ? run.AsScalar() : ""));
        Assert.DoesNotContain("python", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence_changed_paths.py", source, StringComparison.Ordinal);

        foreach (var name in new[] { "Plan changed-path evidence", "Pack selected native producer input", "Run selected evidence producers", "Verify retained evidence manifest" })
        {
            var step = FindStep(steps, name);
            if (name != "Pack selected native producer input")
                Assert.Contains("changes/changed-paths.json", step["run"].AsScalar());
        }

        var lifecycle = FindStep(steps, "Test native lifecycle contracts for selected producers");
        var condition = lifecycle["if"].AsScalar();
        Assert.Contains("steps.plan.outputs.has_producers == 'true'", condition);
        var testCommand = lifecycle["run"].AsScalar();
        Assert.Contains("RepositoryGitChangesTests", testCommand);
        Assert.Contains("RepositoryEvidenceWorkflowContractTests", testCommand);
        Assert.Contains("LocalEvidenceWorkflowTests", testCommand);
        Assert.Contains("dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj", testCommand);
        Assert.Contains("--filter $cliFilter", testCommand);
        Assert.Contains("RepositoryEvidenceWorkflowTests", testCommand);
        Assert.Contains("SolutionCoverageHandoffTests", testCommand);

        var producer = FindStep(steps, "Run selected evidence producers");
        Assert.Contains("steps.plan.outcome == 'success'", producer["if"].AsScalar());
        var verify = FindStep(steps, "Verify retained evidence manifest");
        Assert.Contains("steps.build.outcome == 'success'", verify["if"].AsScalar());
        Assert.Contains("steps.plan.outcome == 'success'", verify["if"].AsScalar());
    }

    [Fact]
    public void NativeMatrixAndStableManifestGateRemainRequired()
    {
        var matrix = NativeJob["strategy"].AsMapping()["matrix"].AsMapping();
        Assert.Equal(["ubuntu-latest", "windows-latest", "macos-latest"],
            matrix["os"].AsSequence().Select(os => os.AsScalar()));

        var jobs = _workflow["jobs"].AsMapping();
        var gate = jobs["evidence-manifest-gate"].AsMapping();
        Assert.Equal("Evidence manifest gate", gate["name"].AsScalar());
        Assert.Equal(["native-evidence"], gate["needs"].AsSequence().Select(job => job.AsScalar()));
        Assert.Equal("${{ always() }}", gate["if"].AsScalar());
        var gateSteps = gate["steps"].AsSequence();
        Assert.Contains("needs.native-evidence.result != 'success'", FindStep(gateSteps, "Fail unless all three native lanes passed verification")["if"].AsScalar());

        Assert.Equal("${{ always() }}", FindStep(NativeJob["steps"].AsSequence(), "Preserve evidence inputs, plan, proof, and artifacts")["if"].AsScalar());
        Assert.DoesNotContain("continue-on-error", File.ReadAllText(Path.Join(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), ".github", "workflows", "native-evidence-validation.yml")), StringComparison.Ordinal);
        Assert.DoesNotContain("paths-ignore", File.ReadAllText(Path.Join(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), ".github", "workflows", "native-evidence-validation.yml")), StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWorkflowUsesNativeScopeForEveryRepositoryOperation()
    {
        var steps = NativeJob["steps"].AsSequence();
        foreach (var (name, operation) in new[]
        {
            ("Plan changed-path evidence", "plan"),
            ("Verify retained evidence manifest", "verify"),
        })
        {
            var command = FindStep(steps, name)["run"].AsScalar();
            Assert.Contains($"--operation {operation} --scope native", command);
        }
        Assert.Contains("'--operation', 'run', '--scope', 'native'", FindStep(steps, "Run selected evidence producers")["run"].AsScalar());
    }

    [Fact]
    public void BuildCoverageIsPlannedFromTheExactHeadAndGatedBySelection()
    {
        var jobs = _buildWorkflow["jobs"].AsMapping();
        var preflight = jobs["coverage-evidence-plan"].AsMapping();
        var preflightOutputs = preflight["outputs"].AsMapping();
        Assert.Equal("${{ steps.plan.outputs.requires_native_coverage }}", preflightOutputs["requires_native_coverage"].AsScalar());
        Assert.Equal("${{ steps.plan.outputs.requires_keycloak_native_coverage }}", preflightOutputs["requires_keycloak_native_coverage"].AsScalar());
        Assert.Equal("${{ steps.plan.outputs.native_coverage_matrix }}", preflightOutputs["native_coverage_matrix"].AsScalar());
        Assert.Equal("${{ steps.plan.outputs.native_coverage_inventory }}", preflightOutputs["native_coverage_inventory"].AsScalar());
        Assert.Equal("${{ steps.plan.outputs.diff_base }}", preflightOutputs["diff_base"].AsScalar());
        Assert.Equal("${{ steps.plan.outputs.v3_diff_base }}", preflightOutputs["v3_diff_base"].AsScalar());
        var checkout = FindStep(preflight["steps"].AsSequence(), "Checkout exact event head with full history");
        var checkoutWith = checkout["with"].AsMapping();
        Assert.Equal("${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}", checkoutWith["ref"].AsScalar());
        Assert.Equal("0", checkoutWith["fetch-depth"].AsScalar());

        var preflightPlan = FindStep(preflight["steps"].AsSequence(), "Collect and plan solution coverage");
        Assert.Equal("${{ github.event.pull_request.base.sha }}", preflightPlan["env"].AsMapping()["GITHUB_PULL_REQUEST_BASE_SHA"].AsScalar());
        var preflightRun = preflightPlan["run"].AsScalar();
        Assert.Contains("--operation collect", preflightRun);
        Assert.Contains("source.baseCommit", preflightRun);
        Assert.Contains("--operation plan --scope coverage", preflightRun);
        Assert.Contains("selection.requiresCoverage", preflightRun);
        Assert.Contains("selection.requiresNativeCoverage", preflightRun);
        Assert.Contains("selection.requiresKeycloakNativeCoverage", preflightRun);
        Assert.Contains("requires_native_coverage=$nativeRequired", preflightRun);
        Assert.Contains("requires_keycloak_native_coverage=$keycloakNativeRequired", preflightRun);
        Assert.Contains("$env:GITHUB_EVENT_NAME -eq 'pull_request' -and $keycloakNativeRequired -eq 'true'", preflightRun);
        Assert.Contains("--operation', 'native-coverage-inventory", preflightRun);
        Assert.Contains("$inventory.matrix | ConvertTo-Json", preflightRun);
        Assert.Contains("\"diff_base=$diffBase\"", preflightRun);
        Assert.Contains("$env:GITHUB_PULL_REQUEST_BASE_SHA", preflightRun);
        Assert.Contains("\"v3_diff_base=$v3DiffBase\"", preflightRun);
        Assert.Equal(1, preflightRun.Split("v3_diff_base=", StringSplitOptions.None).Length - 1);
        Assert.Contains("$v3DiffBase -cnotmatch '^[0-9a-f]{40}$'", preflightRun);
        Assert.Contains("ForgeTrust.AppSurface.Evidence.Validation.dll", preflightRun);
        Assert.DoesNotContain("python", preflightRun, StringComparison.OrdinalIgnoreCase);

        var coverageSecurity = jobs["coverage-security-platform"].AsMapping();
        Assert.Equal(["coverage-evidence-plan", "coverage-cli-candidate"], coverageSecurity["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", coverageSecurity["if"].AsScalar());
        Assert.Equal("${{ fromJSON(needs.coverage-evidence-plan.outputs.native_coverage_matrix) }}",
            coverageSecurity["strategy"].AsMapping()["matrix"].AsScalar());
        Assert.Contains("matrix.nativePlatform == 'Darwin'", coverageSecurity["timeout-minutes"].AsScalar());
        Assert.Equal("${{ matrix.keycloak }}",
            coverageSecurity["env"].AsMapping()["COVERAGE_KEYCLOAK_LANE"].AsScalar());
        Assert.Equal("${{ matrix.producerId }}", coverageSecurity["env"].AsMapping()["COVERAGE_KEYCLOAK_PRODUCER"].AsScalar());
        Assert.Equal("${{ matrix.runner }}", coverageSecurity["runs-on"].AsScalar());
        var securitySteps = coverageSecurity["steps"].AsSequence();
        var jobEnvironment = coverageSecurity["env"].AsMapping();
        Assert.Equal("${{ github.workspace }}/.nuget/packages", jobEnvironment["NUGET_PACKAGES"].AsScalar());
        Assert.All(jobEnvironment.Children.Values, value => Assert.DoesNotContain("runner.", value.AsScalar()));
        Assert.DoesNotContain(new YamlScalarNode("AppSurfaceKeycloakWorkerBuildRoot"), jobEnvironment.Children.Keys);
        var directories = FindStep(securitySteps, "Configure Keycloak coverage directories");
        Assert.Equal("${{ matrix.keycloak }}", directories["if"].AsScalar());
        Assert.Equal("pwsh", directories["shell"].AsScalar());
        var configureDirectories = directories["run"].AsScalar();
        Assert.Contains("Join-Path $env:RUNNER_TEMP 'coverage-keycloak-packages'", configureDirectories);
        Assert.Contains("Join-Path $env:RUNNER_TEMP 'coverage-keycloak-worker-build'", configureDirectories);
        Assert.Contains("Add-Content -LiteralPath $env:GITHUB_ENV", configureDirectories);
        Assert.Contains("NUGET_PACKAGES=$packages", configureDirectories);
        Assert.Contains("AppSurfaceKeycloakWorkerBuildRoot=$workerBuildRoot", configureDirectories);
        Assert.True(IndexOf(securitySteps, directories) < IndexOf(securitySteps, FindStep(securitySteps, "Setup .NET SDK")));
        var canonicalCheckout = FindStep(securitySteps, "Preserve committed bytes on Windows");
        Assert.Equal("${{ runner.os == 'Windows' }}", canonicalCheckout["if"].AsScalar());
        Assert.Equal("git config --global core.autocrlf false", canonicalCheckout["run"].AsScalar());
        Assert.True(IndexOf(securitySteps, canonicalCheckout) < IndexOf(securitySteps, FindStep(securitySteps, "Checkout code")));
        var captureStep = FindStep(securitySteps, "Attest successful native coverage evidence");
        Assert.Equal("pwsh", captureStep["shell"].AsScalar());
        Assert.DoesNotContain(new YamlScalarNode("continue-on-error"), captureStep.Children.Keys);
        var capture = captureStep["run"].AsScalar();
        Assert.Contains("'--operation', 'native'", capture);
        Assert.Contains("'--include-keycloak-native', 'true'", capture);
        Assert.Contains("'--native-producer', $producer, '--collection', $collection", capture);
        Assert.Matches("Test-Path -LiteralPath \\$collection[^\\n]*\\n\\s*Add-Content -LiteralPath \\$env:GITHUB_OUTPUT -Value \"collection-path=\\$collection\"", capture);
        Assert.Contains("'--collection-root'", capture);
        var restoreSteps = securitySteps
            .Where(step => step.AsMapping().Children.TryGetValue(new YamlScalarNode("run"), out var run)
                && run.AsScalar().Contains("ConvertFrom-Json -AsHashtable $env:COVERAGE_TEST_PROJECTS", StringComparison.Ordinal)
                && run.AsScalar().Contains("dotnet restore $project --locked-mode", StringComparison.Ordinal))
            .ToArray();
        var restore = Assert.Single(restoreSteps).AsMapping();
        Assert.Equal("Restore selected native coverage tests (locked)", restore["name"].AsScalar());
        Assert.Equal("pwsh", restore["shell"].AsScalar());
        Assert.Equal("${{ toJSON(matrix.testProjects) }}", restore["env"].AsMapping()["COVERAGE_TEST_PROJECTS"].AsScalar());
        Assert.Equal("${{ matrix.keycloak }}", restore["if"].AsScalar());
        var setupDotnetCache = FindStep(securitySteps, "Setup .NET SDK")["with"].AsMapping()["cache-dependency-path"].AsScalar();
        Assert.Contains("Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/packages.*.lock.json", setupDotnetCache);
        Assert.Contains("Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/packages.*.lock.json", setupDotnetCache);
        var sdkSelection = FindStep(securitySteps, "Select exact coverage SDK");
        Assert.True(IndexOf(securitySteps, sdkSelection) < IndexOf(securitySteps, restore));
        var bundle = FindStep(securitySteps, "Install pinned Aspire bundle for Keycloak coverage");
        Assert.Contains("aspire.cli@13.6.5", bundle["run"].AsScalar());
        Assert.True(IndexOf(securitySteps, restore) < IndexOf(securitySteps, bundle));
        Assert.True(IndexOf(securitySteps, bundle) < IndexOf(securitySteps, captureStep));
        Assert.Contains("if ($keycloakLane)", capture);
        Assert.Contains("$env:COVERAGE_INCLUDE_KEYCLOAK -eq 'true'", capture);
        Assert.Contains("!matrix.keycloak", FindStep(securitySteps, "Run coverage security contracts")["if"].AsScalar());
        Assert.Contains("!matrix.keycloak", FindStep(securitySteps, "Prepare generated docs stylesheet for coverage security tests")["if"].AsScalar());
        Assert.Contains("'--cli', $env:COVERAGE_CLI_PATH", capture);
        Assert.Contains("$contractVersion = if ($env:GITHUB_EVENT_NAME -eq 'pull_request') { '3' } else { '2' }", capture);
        Assert.Contains("'--contract-version', $contractVersion", capture);
        Assert.Contains("if ($contractVersion -eq '3') { $adapter += @('--diff-policy-id', 'appsurface-visual-baseline-png-v1') }", capture);
        Assert.Contains("$diffBase = if ($contractVersion -eq '3') { $env:COVERAGE_V3_DIFF_BASE } else { $env:COVERAGE_GATE_DIFF_BASE }", capture);
        Assert.Contains("$diffBase -cnotmatch '^[0-9a-f]{40}$'", capture);
        Assert.Equal(1, capture.Split("$env:COVERAGE_V3_DIFF_BASE", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, capture.Split("'--diff-base'", StringSplitOptions.None).Length - 1);
        Assert.Contains("/usr/bin/time -l dotnet @command", capture);
        Assert.Contains("$resourceLog = \"$env:COVERAGE_DOGFOOD_OUTPUT.time.log\"", capture);
        Assert.Contains("darwin-rss.json", capture);
        Assert.Contains("if ($rss.Count -ne 1) { throw \"Native coverage capture omitted the macOS maximum RSS.\" }", capture);
        var captureOutput = captureStep["env"].AsMapping()["COVERAGE_DOGFOOD_OUTPUT"].AsScalar();
        Assert.Equal("${{ runner.temp }}/coverage-native-${{ matrix.producerId }}-${{ github.run_id }}-${{ github.run_attempt }}", captureOutput);
        var nativeUpload = FindStep(securitySteps, "Upload attested native coverage")["with"].AsMapping();
        Assert.Equal("coverage-native-${{ matrix.producerId }}-${{ github.run_attempt }}", nativeUpload["name"].AsScalar());
        Assert.Equal("${{ steps.capture-native-coverage.outputs.capture-path }}", nativeUpload["path"].AsScalar());
        var summarizeFailure = FindStep(securitySteps, "Summarize failed Keycloak native collection");
        Assert.Equal("summarize-native-collection-failure", summarizeFailure["id"].AsScalar());
        Assert.Equal("${{ !cancelled() && matrix.keycloak && steps.capture-native-coverage.outcome == 'failure' && steps.capture-native-coverage.outputs.collection-path != '' }}",
            summarizeFailure["if"].AsScalar());
        Assert.Equal("pwsh", summarizeFailure["shell"].AsScalar());
        var summaryEnvironment = summarizeFailure["env"].AsMapping();
        Assert.Equal("${{ steps.capture-native-coverage.outputs.collection-path }}", summaryEnvironment["COVERAGE_NATIVE_COLLECTION"].AsScalar());
        Assert.Equal("${{ toJSON(matrix.testProjects) }}", summaryEnvironment["COVERAGE_TEST_PROJECTS"].AsScalar());
        Assert.Contains("write-keycloak-native-failure-summary.ps1 -CollectionDirectory $env:COVERAGE_NATIVE_COLLECTION", summarizeFailure["run"].AsScalar());
        Assert.Contains("-TestProject $projects", summarizeFailure["run"].AsScalar());
        Assert.Contains("summary-path=$summary", summarizeFailure["run"].AsScalar());
        Assert.True(IndexOf(securitySteps, captureStep) < IndexOf(securitySteps, summarizeFailure));
        var failureUpload = FindStep(securitySteps, "Upload failed native capture result");
        Assert.True(IndexOf(securitySteps, summarizeFailure) < IndexOf(securitySteps, failureUpload));
        Assert.Equal("${{ !cancelled() && steps.capture-native-coverage.outcome == 'failure' }}", failureUpload["if"].AsScalar());
        var failureArtifact = failureUpload["with"].AsMapping();
        Assert.Equal("coverage-capture-failure-${{ matrix.producerId }}-${{ github.run_attempt }}", failureArtifact["name"].AsScalar());
        Assert.Equal(captureOutput + "/native-result.json\n" + captureOutput + ".time.log\n"
            + "${{ steps.summarize-native-collection-failure.outputs.summary-path }}\n", failureArtifact["path"].AsScalar());
        Assert.Equal("ignore", failureArtifact["if-no-files-found"].AsScalar());
        var diagnosticsUpload = FindStep(securitySteps, "Upload coverage security diagnostics");
        Assert.Equal("${{ always() && !matrix.keycloak }}", diagnosticsUpload["if"].AsScalar());
        var diagnosticsArtifact = diagnosticsUpload["with"].AsMapping();
        Assert.Equal("coverage-security-diagnostics-${{ matrix.producerId }}", diagnosticsArtifact["name"].AsScalar());
        Assert.StartsWith("${{ runner.temp }}/coverage-native-results\n", diagnosticsArtifact["path"].AsScalar());
        Assert.Contains("capture-path=$env:COVERAGE_DOGFOOD_OUTPUT", capture);
        Assert.DoesNotContain("python", capture, StringComparison.OrdinalIgnoreCase);

        var build = jobs["build"].AsMapping();
        Assert.Contains("coverage-security-platform", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("packed-durable-consumers", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("coverage-cli-candidate", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("coverage-collection", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-collection.result == 'success'", build["if"].AsScalar());
        Assert.Contains("needs.coverage-cli-candidate.result == 'success'", build["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", build["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result == 'skipped'", build["if"].AsScalar());
        var steps = build["steps"].AsSequence();
        var download = FindStep(steps, "Download same-run native coverage evidence");
        Assert.Contains("github.event_name == 'pull_request'", download["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", download["if"].AsScalar());
        var nativeDownloadInputs = download["with"].AsMapping();
        Assert.Equal("coverage-native-*-${{ github.run_attempt }}", nativeDownloadInputs["pattern"].AsScalar());
        Assert.Equal("${{ runner.temp }}/coverage-native-inputs", nativeDownloadInputs["path"].AsScalar());

        var buildStepNames = string.Join("\n", steps.Select(step => step.AsMapping()["name"].AsScalar()));
        Assert.DoesNotContain("shadow", buildStepNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replay", buildStepNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COVERAGE_V3_SHADOW", buildStepNames, StringComparison.Ordinal);

        var run = FindStep(steps, "Run solution coverage and gate");
        Assert.DoesNotContain(new YamlScalarNode("if"), run.Children.Keys);
        var command = run["run"].AsScalar();
        Assert.Contains("--operation plan --scope coverage", command);
        Assert.Contains("--operation run --scope coverage", command);
        Assert.Contains("--operation verify --scope coverage", command);
        Assert.Contains("--native-coverage-directory", command);
        Assert.Contains("--native-coverage-contract-version 3", command);
        Assert.Contains("--coverage-cli \"$COVERAGE_CLI_PATH\"", command);
        Assert.Contains("--coverage-collection \"$RUNNER_TEMP/solution-coverage-collection\"", command);
        Assert.Contains("jq -r '.requiresCoverage'", command);
        Assert.Contains("jq -r '.requiresNativeCoverage'", command);
        Assert.Contains("GITHUB_EVENT_NAME" + "\" == \"pull_request\"", command);
        Assert.Equal(1, command.Split("--diff-base", StringSplitOptions.None).Length - 1);
        Assert.Contains("patch_base=\"$COVERAGE_V3_DIFF_BASE\"", command);
        Assert.Contains("--diff-base \"$patch_base\"", command);
        Assert.Contains("dotnet \"$validation\" \"${args[@]}\"", command);
        Assert.DoesNotContain("coverage-solution.sh", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--operation fanin", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Coverage.Dogfood.csproj", command, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD^1", command, StringComparison.Ordinal);
        var qualification = FindStep(steps, "Verify retained v3 fan-in qualification");
        Assert.True(IndexOf(steps, run) < IndexOf(steps, qualification));
        Assert.Equal("${{ needs.coverage-evidence-plan.outputs.native_coverage_inventory }}",
            qualification["env"].AsMapping()["COVERAGE_NATIVE_INVENTORY"].AsScalar());
        Assert.Contains("$fanin/shadow-proof.json", qualification["run"].AsScalar());
        Assert.Contains("darwin-rss.json", qualification["run"].AsScalar());
        Assert.Contains("Expected exactly $expected_count", qualification["run"].AsScalar());
        Assert.Contains(".producers[].id", qualification["run"].AsScalar());
        Assert.Contains("$actual_producers\" == \"$expected_sorted", qualification["run"].AsScalar());
        Assert.Contains(".host.platform == $platform", qualification["run"].AsScalar());
        Assert.Contains(".host.osArchitecture == $os_arch", qualification["run"].AsScalar());
        Assert.Contains(".host.processArchitecture == $process_arch", qualification["run"].AsScalar());

        foreach (var name in new[] { "Upload test-result diagnostics", "Upload coverage gate reports" })
            Assert.Contains("needs.coverage-evidence-plan.outputs.requires_coverage == 'true'", FindStep(steps, name)["if"].AsScalar());
        var unselectedCoverageUpload = FindStep(steps, "Upload unselected coverage manifest");
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_coverage != 'true'", unselectedCoverageUpload["if"].AsScalar());
        var unselectedCoveragePaths = unselectedCoverageUpload["with"].AsMapping()["path"].AsScalar();
        Assert.Contains("evidence-plan.json", unselectedCoveragePaths);
        Assert.Contains("selection.json", unselectedCoveragePaths);
        Assert.Contains("evidence-manifest.json", unselectedCoveragePaths);
        var coverageUpload = FindStep(steps, "Upload coverage reports to Codecov");
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_coverage == 'true'", coverageUpload["if"].AsScalar());
        var coverageUploadFiles = coverageUpload["with"].AsMapping()["files"].AsScalar();
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", coverageUploadFiles);
        Assert.Contains("coverage-fanin/composed/coverage.cobertura.xml", coverageUploadFiles);
        Assert.Contains("coverage/coverage.cobertura.xml", coverageUploadFiles);

        var manifestGate = jobs["coverage-manifest-gate"].AsMapping();
        Assert.Equal("${{ always() }}", manifestGate["if"].AsScalar());
        Assert.Equal(["coverage-evidence-plan", "coverage-security-platform", "packed-durable-consumers", "coverage-collection", "build"],
            manifestGate["needs"].AsSequence().Select(node => node.AsScalar()));
        var gateSteps = manifestGate["steps"].AsSequence();
        var failGate = FindStep(gateSteps, "Fail unless planning, selected native evidence, and the build passed");
        Assert.Contains("needs.build.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.packed-durable-consumers.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-collection.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result != 'skipped'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage != 'true'", FindStep(gateSteps, "Confirm the selected coverage evidence gate passed")["if"].AsScalar());
    }

    [Fact]
    public void InstalledCoverageCandidateAndConsumersUseTheSameEventHead()
    {
        var expected = "${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}";
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository, ".github", "scripts", "install-coverage-candidate.ps1")));
        var jobs = _buildWorkflow["jobs"].AsMapping();
        Assert.Equal(expected, _buildWorkflow["env"].AsMapping()["COVERAGE_SOURCE_COMMIT"].AsScalar());
        foreach (var name in new[] { "coverage-cli-candidate", "coverage-security-platform", "coverage-collection", "build" })
        {
            var job = jobs[name].AsMapping();
            var checkout = FindStep(job["steps"].AsSequence(), "Checkout code")["with"].AsMapping();
            Assert.Equal(expected, checkout["ref"].AsScalar());
            Assert.Equal("0", checkout["fetch-depth"].AsScalar());
            if (name == "coverage-cli-candidate")
            {
                Assert.Contains("requires_coverage == 'true'", job["if"].AsScalar());
                var pack = FindStep(job["steps"].AsSequence(), "Pack source and SDK bound candidate CLI")["run"].AsScalar();
                Assert.Contains("test \"$source_commit\" = \"$COVERAGE_SOURCE_COMMIT\"", pack);
            }
            else
            {
                Assert.Contains("coverage-cli-candidate", job["needs"].AsSequence().Select(node => node.AsScalar()));
                var steps = job["steps"].AsSequence();
                var install = FindStep(steps, "Verify and install candidate CLI");
                Assert.Equal("pwsh", install["shell"].AsScalar());
                Assert.Equal("./.github/scripts/install-coverage-candidate.ps1", install["run"].AsScalar());
                Assert.True(IndexOf(steps, FindStep(steps, "Download same-source CLI candidate package")) < IndexOf(steps, install));
            }
        }
        Assert.Equal("${{ needs.coverage-evidence-plan.outputs.diff_base }}",
            jobs["coverage-security-platform"].AsMapping()["env"].AsMapping()["COVERAGE_GATE_DIFF_BASE"].AsScalar());
    }

    [Fact]
    public void KeycloakCoverageWorkerRelocationStillExcludesDefaultGeneratedDirectories()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var props = XDocument.Load(TestPathUtils.PathUnder(repository, "Directory.Build.props"));
        var worker = Assert.Single(props.Descendants("PropertyGroup"), group =>
            ((string?)group.Attribute("Condition"))?.Contains("AppSurfaceKeycloakWorkerBuildRoot", StringComparison.Ordinal) == true);

        Assert.Contains("ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker", (string)worker.Attribute("Condition")!);
        Assert.Equal("$(AppSurfaceKeycloakWorkerBuildRoot)/bin/", (string?)worker.Element("BaseOutputPath"));
        Assert.Equal("$(AppSurfaceKeycloakWorkerBuildRoot)/obj/", (string?)worker.Element("BaseIntermediateOutputPath"));
        var project = XDocument.Load(TestPathUtils.PathUnder(repository, "Auth", "ForgeTrust.AppSurface.Auth.Aspire.Keycloak",
            "realm-ready", "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker.csproj"));
        var relocated = Assert.Single(project.Descendants("PropertyGroup"), group =>
            (string?)group.Attribute("Condition") == "'$(AppSurfaceKeycloakWorkerBuildRoot)' != ''");
        var exclusions = ((string?)relocated.Element("DefaultItemExcludes"))?.Split(';');
        Assert.NotNull(exclusions);
        Assert.Contains("$(DefaultItemExcludes)", exclusions);
        Assert.Contains("bin/**", exclusions);
        Assert.Contains("obj/**", exclusions);
    }

    [Fact]
    public void CoverageCandidateWorkflowContractsFailBeforePackingAndUploading()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository, "tests", "coverage-candidate", "install-coverage-candidate.tests.ps1")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository, "tests", "coverage-native-failure", "contracts.ps1")));

        var steps = _buildWorkflow["jobs"].AsMapping()["coverage-cli-candidate"].AsMapping()["steps"].AsSequence();
        var contracts = FindStep(steps, "Test coverage candidate workflow contracts");
        Assert.Equal("pwsh", contracts["shell"].AsScalar());
        Assert.False(contracts.Children.ContainsKey(new YamlScalarNode("if")));
        Assert.False(contracts.Children.ContainsKey(new YamlScalarNode("continue-on-error")));

        var command = contracts["run"].AsScalar();
        Assert.Contains("$ErrorActionPreference = 'Stop'", command);
        const string candidateTests = "& ./tests/coverage-candidate/install-coverage-candidate.tests.ps1";
        const string nativeFailureTests = "& ./tests/coverage-native-failure/contracts.ps1";
        var candidateTestsIndex = command.IndexOf(candidateTests, StringComparison.Ordinal);
        var nativeFailureTestsIndex = command.IndexOf(nativeFailureTests, StringComparison.Ordinal);
        Assert.True(candidateTestsIndex >= 0);
        Assert.True(nativeFailureTestsIndex > candidateTestsIndex);

        var pack = FindStep(steps, "Pack source and SDK bound candidate CLI");
        var upload = FindStep(steps, "Upload same-run candidate CLI package");
        Assert.True(IndexOf(steps, contracts) < IndexOf(steps, pack));
        Assert.True(IndexOf(steps, pack) < IndexOf(steps, upload));
        foreach (var step in new[] { pack, upload })
        {
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("if")));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        }
        Assert.Equal("error", upload["with"].AsMapping()["if-no-files-found"].AsScalar());
    }

    [Fact]
    public void KeycloakThemeEvidenceRunsOfflineWorkerConsumerBeforeLockedRestoreAndProof()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository, "tests", "keycloak-identity-feasibility", "verify-worker-consumer.py")));

        var steps = _buildWorkflow["jobs"].AsMapping()["keycloak-theme-evidence"].AsMapping()["steps"].AsSequence();
        var sdk = FindStep(steps, "Setup .NET SDK");
        var consumer = FindStep(steps, "Verify offline worker consumer upgrade and clean");
        var restore = FindStep(steps, "Restore focused Keycloak proof (locked)");
        var lifecycleProof = FindStep(steps, "Verify theme lifecycle contracts");
        var themeProof = FindStep(steps, "Prove the pinned sample theme image and disposable realm");

        Assert.Equal("10.0.402", sdk["with"].AsMapping()["dotnet-version"].AsScalar());
        Assert.Equal("bash", consumer["shell"].AsScalar());
        Assert.Equal("python3 -B tests/keycloak-identity-feasibility/verify-worker-consumer.py --dotnet \"$(command -v dotnet)\"",
            consumer["run"].AsScalar());
        Assert.False(consumer.Children.ContainsKey(new YamlScalarNode("if")));
        Assert.False(consumer.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        Assert.True(IndexOf(steps, sdk) < IndexOf(steps, consumer));
        Assert.True(IndexOf(steps, consumer) < IndexOf(steps, restore));
        Assert.True(IndexOf(steps, consumer) < IndexOf(steps, lifecycleProof));
        Assert.True(IndexOf(steps, consumer) < IndexOf(steps, themeProof));
    }

    [Fact]
    public void KeycloakThemeEvidenceRequiresThePackedSingleFileSmoke()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository, "tests", "keycloak-identity-feasibility", "verify-packed-worker-single-file.py")));

        var steps = _buildWorkflow["jobs"].AsMapping()["keycloak-theme-evidence"].AsMapping()["steps"].AsSequence();
        var lifecycleProof = FindStep(steps, "Verify theme lifecycle contracts");
        var packedWorkerProof = FindStep(steps, "Verify packed single-file worker resolution");
        var themeProof = FindStep(steps, "Prove the pinned sample theme image and disposable realm");

        Assert.Equal("bash", packedWorkerProof["shell"].AsScalar());
        Assert.Equal("python3 -B tests/keycloak-identity-feasibility/verify-packed-worker-single-file.py --dotnet \"$(command -v dotnet)\"",
            packedWorkerProof["run"].AsScalar());
        Assert.False(packedWorkerProof.Children.ContainsKey(new YamlScalarNode("if")));
        Assert.False(packedWorkerProof.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        Assert.True(IndexOf(steps, lifecycleProof) < IndexOf(steps, packedWorkerProof));
        Assert.True(IndexOf(steps, packedWorkerProof) < IndexOf(steps, themeProof));
    }

    [Fact]
    public void SelectedNativePackagesRetainInstalledCoverageAndMetadataRejectionProofs()
    {
        var steps = NativeJob["steps"].AsSequence();
        var pack = FindStep(steps, "Pack selected native producer input");
        var preparation = FindStep(steps, "Prepare shared neutral package feed");
        var proof = FindStep(steps, "Prove installed neutral coverage package and published application");
        var reuseProof = FindStep(steps, "Prove installed reuse tool and package-only prepared host");
        var rejection = FindStep(steps, "Reject case-varied engine version skew before proof output");
        Assert.True(IndexOf(steps, pack) < IndexOf(steps, preparation));
        Assert.True(IndexOf(steps, preparation) < IndexOf(steps, proof));
        Assert.True(IndexOf(steps, preparation) < IndexOf(steps, reuseProof));
        Assert.Contains("steps.pack.outcome == 'success'", preparation["if"].AsScalar());
        var preparationCommand = preparation["run"].AsScalar();
        var neutralFeedCommand = FindDotnetInvocation(preparationCommand, "--operation", "pack-candidate-feed");
        AssertCommandOption(neutralFeedCommand, "--mode", "neutral");
        AssertCommandOption(neutralFeedCommand, "--repository", "$env:GITHUB_WORKSPACE");
        AssertCommandOption(neutralFeedCommand, "--output", "$feed");
        AssertCommandOption(neutralFeedCommand, "--version", "$env:PACKAGE_VERSION");
        AssertCommandOption(neutralFeedCommand, "--source-commit", "$env:SOURCE_COMMIT");
        AssertCommandOption(neutralFeedCommand, "--package", "$env:PACKAGE_PATH");
        var preparationEnvironment = preparation["env"].AsMapping();
        Assert.Equal("${{ steps.diff.outputs.source_commit }}", preparationEnvironment["SOURCE_COMMIT"].AsScalar());
        Assert.Equal("${{ steps.pack.outputs.package }}", preparationEnvironment["PACKAGE_PATH"].AsScalar());
        Assert.Equal("${{ steps.pack.outputs.version }}", preparationEnvironment["PACKAGE_VERSION"].AsScalar());
        Assert.True(IndexOf(steps, proof) < IndexOf(steps, rejection));
        Assert.Contains("steps.pack.outcome == 'success'", rejection["if"].AsScalar());
        foreach (var step in new[] { proof, reuseProof })
        {
            Assert.Equal("${{ !cancelled() && steps.prepare-neutral-feed.outcome == 'success' }}", step["if"].AsScalar());
            var env = step["env"].AsMapping();
            Assert.Equal("${{ steps.prepare-neutral-feed.outputs.feed }}", env["NATIVE_PACKAGE_FEED"].AsScalar());
            Assert.Equal("${{ steps.prepare-neutral-feed.outputs.version }}", env["NATIVE_PACKAGE_VERSION"].AsScalar());
        }
        var neutralProofCommand = FindDotnetInvocation(proof["run"].AsScalar(), "--package-proof");
        AssertCommandOption(neutralProofCommand, "--feed", "$env:NATIVE_PACKAGE_FEED");
        AssertCommandOption(neutralProofCommand, "--version", "$env:NATIVE_PACKAGE_VERSION");

        var reuseCommand = reuseProof["run"].AsScalar();
        var extendFeedCommand = FindDotnetInvocation(reuseCommand, "--operation", "pack-candidate-feed");
        AssertCommandOption(extendFeedCommand, "--mode", "extend-neutral");
        AssertCommandOption(extendFeedCommand, "--repository", "$env:GITHUB_WORKSPACE");
        AssertCommandOption(extendFeedCommand, "--output", "$env:NATIVE_PACKAGE_FEED");
        AssertCommandOption(extendFeedCommand, "--version", "$env:NATIVE_PACKAGE_VERSION");
        AssertCommandOption(extendFeedCommand, "--source-commit", "$env:SOURCE_COMMIT");
        var reuseProofCommand = FindDotnetInvocation(reuseCommand, "--operation", "reuse-package-proof");
        AssertCommandOption(reuseProofCommand, "--feed", "$env:NATIVE_PACKAGE_FEED");
        AssertCommandOption(reuseProofCommand, "--version", "$env:NATIVE_PACKAGE_VERSION");
        AssertCommandOption(reuseProofCommand, "--source-commit", "$env:SOURCE_COMMIT");
        AssertCommandOption(reuseProofCommand, "--dotnet", "(Get-Command dotnet).Source");
        AssertCommandOption(reuseProofCommand, "--output", "$proof");
        Assert.True(reuseCommand.IndexOf(extendFeedCommand, StringComparison.Ordinal) < reuseCommand.IndexOf(reuseProofCommand, StringComparison.Ordinal));
        Assert.Equal("${{ steps.diff.outputs.source_commit }}", reuseProof["env"].AsMapping()["SOURCE_COMMIT"].AsScalar());
        Assert.Contains("$LASTEXITCODE -eq 0 -or (Test-Path $output)", rejection["run"].AsScalar());
        Assert.Contains("<repository type='git' commit='$candidateCommit'/>", rejection["run"].AsScalar());
        Assert.Contains("coverage.private-dependency-invalid", rejection["run"].AsScalar());
        var lifecycle = FindStep(steps, "Test native lifecycle contracts for selected producers")["run"].AsScalar();
        Assert.Contains("FullyQualifiedName~EvidenceReusePackageProofTests", lifecycle);
        var upload = FindStep(steps, "Preserve evidence inputs, plan, proof, and artifacts")["with"].AsMapping()["path"].AsScalar();
        Assert.Contains("native-coverage-library-proof/proof.json", upload);
        Assert.Contains("native-coverage-library-proof/published-proof/runs/**", upload);
        Assert.Contains("${{ runner.temp }}/native-coverage-library-feed/pack-logs/pack-*.log", upload);
        Assert.Contains("${{ runner.temp }}/native-evidence-reuse-package-proof/packed-progress.jsonl", upload);
    }

    [Fact]
    public void CollectionOverlapsNativeAndPackedChecksWhileFinalAcceptanceStillRequiresBoth()
    {
        var jobs = _buildWorkflow["jobs"].AsMapping();
        var collection = jobs["coverage-collection"].AsMapping();
        Assert.Equal(["coverage-evidence-plan", "coverage-cli-candidate"], collection["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_coverage == 'true'", collection["if"].AsScalar());
        Assert.Contains("needs.coverage-cli-candidate.result == 'success'", collection["if"].AsScalar());
        var steps = collection["steps"].AsSequence();
        var candidateInstall = FindStep(steps, "Verify and install candidate CLI");
        var collect = FindStep(steps, "Collect solution coverage");
        Assert.True(IndexOf(steps, candidateInstall) < IndexOf(steps, collect));
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_coverage == 'true'", candidateInstall["if"].AsScalar());
        Assert.Contains("requires_coverage == 'true'", collect["if"].AsScalar());
        Assert.Contains("--operation collect-coverage", collect["run"].AsScalar());
        Assert.Contains("--coverage-cli \"$COVERAGE_CLI_PATH\"", collect["run"].AsScalar());
        Assert.Equal("${{ needs.coverage-evidence-plan.outputs.requires_keycloak_native_coverage }}",
            collect["env"].AsMapping()["COVERAGE_INCLUDE_KEYCLOAK"].AsScalar());
        Assert.Contains("GITHUB_EVENT_NAME\" == \"pull_request\"", collect["run"].AsScalar());
        Assert.Contains("args+=(--include-keycloak-native true)", collect["run"].AsScalar());
        const string timingFile = "\"$RUNNER_TEMP/coverage-schedule/timings.json\"";
        Assert.Matches(
            $@"if \[\[ -f {Regex.Escape(timingFile)} \]\]; then\s+args\+=\(--coverage-schedule-timings {Regex.Escape(timingFile)}\)",
            collect["run"].AsScalar());
        Assert.DoesNotContain("--native-coverage-directory", collect["run"].AsScalar());
        var history = FindStep(steps, "Restore advisory coverage scheduling history");
        var contracts = FindStep(steps, "Test C# coverage scheduling history contracts");
        Assert.Equal("dotnet run --file tests/coverage-schedule-history/contracts.cs", contracts["run"].AsScalar());
        Assert.Equal($"dotnet run --file .github/scripts/coverage-schedule-history.cs -- restore --output {timingFile}", history["run"].AsScalar());
        Assert.True(IndexOf(steps, contracts) < IndexOf(steps, history));
        Assert.True(IndexOf(steps, history) < IndexOf(steps, collect));
        Assert.Equal("${{ github.token }}", history["env"].AsMapping()["GH_TOKEN"].AsScalar());
        Assert.Equal("${{ github.event.repository.default_branch }}", history["env"].AsMapping()["GITHUB_DEFAULT_BRANCH"].AsScalar());
        var upload = FindStep(steps, "Preserve same-attempt coverage collection");
        Assert.Contains("success()", upload["if"].AsScalar());
        Assert.Equal("solution-coverage-collection-${{ github.run_attempt }}", upload["with"].AsMapping()["name"].AsScalar());
        var build = jobs["build"].AsMapping();
        Assert.Contains("coverage-collection", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-collection.result == 'success'", build["if"].AsScalar());
        Assert.Contains("needs.packed-durable-consumers.result == 'success'", build["if"].AsScalar());
        var gateSteps = build["steps"].AsSequence();
        var download = FindStep(gateSteps, "Download same-attempt coverage collection");
        Assert.Equal(upload["with"].AsMapping()["name"].AsScalar(), download["with"].AsMapping()["name"].AsScalar());
        Assert.DoesNotContain(new YamlScalarNode("run-id"), download["with"].AsMapping().Children.Keys);
        Assert.Contains("--coverage-collection \"$RUNNER_TEMP/solution-coverage-collection\"",
            FindStep(gateSteps, "Run solution coverage and gate")["run"].AsScalar());
        var finalSteps = jobs["coverage-manifest-gate"].AsMapping()["steps"].AsSequence();
        var qualification = FindStep(gateSteps, "Verify retained v3 fan-in qualification");
        Assert.Equal("${{ needs.coverage-evidence-plan.outputs.requires_keycloak_native_coverage == 'true' && 'coverlet-collector-cobertura-async-v1' || 'coverlet-collector-cobertura-v1' }}",
            qualification["env"].AsMapping()["COVERAGE_EXPECTED_REPORT_PROFILE"].AsScalar());
        Assert.Equal(2, qualification["run"].AsScalar().Split("--arg profile \"$COVERAGE_EXPECTED_REPORT_PROFILE\"",
            StringSplitOptions.None).Length - 1);
        var failure = FindStep(finalSteps, "Fail unless planning, selected native evidence, and the build passed")["if"].AsScalar();
        Assert.Contains("needs.coverage-collection.result != 'success'", failure);
        Assert.Contains("needs.packed-durable-consumers.result != 'success'", failure);
        var timings = FindStep(steps, "Publish advisory coverage scheduling timings");
        Assert.Contains("github.event_name == 'push'", timings["if"].AsScalar());
        Assert.Contains("github.ref == 'refs/heads/main'", timings["if"].AsScalar());
    }

    [Fact]
    public void FullSolutionCollectionUsesTheSupportedPlaywrightUbuntuImage()
    {
        // Protects real browser execution when ubuntu-latest advances beyond the pinned Playwright host table.
        // The native security matrix still exercises its declared platforms; no test or gate is omitted.
        var collection = _buildWorkflow["jobs"].AsMapping()["coverage-collection"].AsMapping();

        Assert.Equal("ubuntu-24.04", collection["runs-on"].AsScalar());
    }

    [Fact]
    public void DocsExportOverlapsBuildButDeploymentWaitsForAllPreviouslyRequiredAcceptance()
    {
        var jobs = _buildWorkflow["jobs"].AsMapping();
        Assert.DoesNotContain(new YamlScalarNode("needs"), jobs["export-appsurface-docs"].AsMapping().Children.Keys);
        var deploy = jobs["deploy-appsurface-docs"].AsMapping();
        Assert.Equal(["export-appsurface-docs", "build", "coverage-manifest-gate"],
            deploy["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Equal("${{ github.ref == 'refs/heads/main' && (github.event_name == 'push' || github.event_name == 'workflow_dispatch') }}", deploy["if"].AsScalar());
    }

    [Fact]
    public void PackageGatePlansBeforeNodeAndUsesOnlyScopedPackageProducer()
    {
        var job = _packageWorkflow["jobs"].AsMapping()["package-gate"].AsMapping();
        var steps = job["steps"].AsSequence();
        var plan = FindStep(steps, "Collect and plan package checks");
        var planCommand = plan["run"].AsScalar();
        Assert.Contains("--operation collect", planCommand);
        Assert.Contains("--operation plan --scope packages", planCommand);
        Assert.Contains("requiresPackageChecks", planCommand);

        var pnpm = FindStep(steps, "Setup pnpm");
        var node = FindStep(steps, "Setup Node.js");
        var assets = FindStep(steps, "Verify web assets");
        foreach (var step in new[] { pnpm, node, assets })
            Assert.Contains("steps.plan.outputs.requires_package_checks == 'true'", step["if"].AsScalar());
        Assert.True(IndexOf(steps, plan) < IndexOf(steps, pnpm));
        Assert.True(IndexOf(steps, plan) < IndexOf(steps, node));

        var evidence = FindStep(steps, "Run and verify scoped package evidence");
        Assert.Contains("--operation run --scope packages", evidence["run"].AsScalar());
        Assert.Contains("--operation verify --scope packages", evidence["run"].AsScalar());
        var allCommands = string.Join("\n", steps.Select(step => step.AsMapping().Children.TryGetValue(new YamlScalarNode("run"), out var run) ? run.AsScalar() : ""));
        Assert.DoesNotContain("dotnet restore ForgeTrust.AppSurface.slnx", allCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build ForgeTrust.AppSurface.slnx", allCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("ForgeTrust.AppSurface.PackageIndex.csproj", allCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-packages", allCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("python", planCommand, StringComparison.OrdinalIgnoreCase);

        var diagnostics = FindStep(steps, "Upload package validation diagnostics");
        var diagnosticPaths = diagnostics["with"].AsMapping()["path"].AsScalar();
        Assert.Contains("${{ runner.temp }}/package-evidence-*/package-checks/artifacts/durable-template-consumer-proof.json", diagnosticPaths);
        Assert.DoesNotContain("${{ runner.temp }}/package-evidence-*/package-checks/**", diagnosticPaths);
        Assert.DoesNotContain("${{ runner.temp }}/package-evidence-*/package-checks/artifacts/**", diagnosticPaths);
        Assert.DoesNotContain("nuget-packages", diagnosticPaths);
    }

    private static YamlMappingNode LoadWorkflow(string repository, string name)
    {
        using var reader = File.OpenText(Path.Join(repository, ".github", "workflows", name));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents.Single().RootNode;
    }

    private static (string Scope, string Selector)[] ReadSelectorInventory(string repository)
    {
        var path = Path.Join(repository, ".github", "workflows", "native-coverage-test-selectors.txt");
        return File.ReadAllLines(path).Select(line =>
        {
            var entry = line.Split('\t', 2);
            Assert.Equal(2, entry.Length);
            Assert.Contains(entry[0], new[] { "native", "shared-cli", "lifecycle-cli" });
            Assert.StartsWith("FullyQualifiedName~", entry[1], StringComparison.Ordinal);
            return (entry[0], entry[1]);
        }).ToArray();
    }

    private YamlMappingNode NativeJob => _workflow["jobs"].AsMapping()["native-evidence"].AsMapping();

    private static YamlMappingNode FindStep(YamlSequenceNode steps, string name) =>
        steps.Select(step => step.AsMapping()).Single(step => step["name"].AsScalar() == name);

    private static int IndexOf(YamlSequenceNode steps, YamlMappingNode target) =>
        steps.Children.ToList().FindIndex(step => ReferenceEquals(step, target));

    private static string FindDotnetInvocation(string run, string option, string? value = null)
    {
        var argument = value is null ? Regex.Escape(option) : $@"{Regex.Escape(option)}\s+{Regex.Escape(value)}";
        return run.Split('\n').Single(line =>
            Regex.IsMatch(line, $@"^\s*dotnet(?=\s).*\s{argument}(?:\s|$)"));
    }

    private static void AssertCommandOption(string command, string option, string value) =>
        Assert.Matches($@"(?:^|\s){Regex.Escape(option)}\s+{Regex.Escape(value)}(?=\s|$)", command);
}

internal static class WorkflowYamlNodeExtensions
{
    public static YamlMappingNode AsMapping(this YamlNode node) => (YamlMappingNode)node;
    public static YamlSequenceNode AsSequence(this YamlNode node) => (YamlSequenceNode)node;
    public static string AsScalar(this YamlNode node) => ((YamlScalarNode)node).Value!;
}
