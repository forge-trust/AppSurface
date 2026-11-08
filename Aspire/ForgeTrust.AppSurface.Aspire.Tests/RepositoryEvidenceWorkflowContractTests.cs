using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Testing;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class RepositoryEvidenceWorkflowContractTests
{
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

    [Fact]
    public void NativeCollectorFilterMatchesTheAdapterDeclaration()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var adapter = File.ReadAllLines(Path.Join(repository, "tools",
            "ForgeTrust.AppSurface.Coverage.Dogfood", "Program.cs"));
        var declaration = Assert.Single(adapter, line =>
            line.TrimStart().StartsWith("internal const string NativeFilter = ", StringComparison.Ordinal));
        var declaredFilter = JsonSerializer.Deserialize<string>(declaration[(declaration.IndexOf('=') + 1)..]
            .Trim().TrimEnd(';'));
        var job = _buildWorkflow["jobs"].AsMapping()["coverage-security-platform"].AsMapping();
        var collector = FindStep(job["steps"].AsSequence(), "Run coverage security contracts")["run"].AsScalar();
        var filter = Assert.Single(Regex.Matches(collector, "--filter\\s+\"([^\"]+)\"").Cast<Match>());

        Assert.False(string.IsNullOrWhiteSpace(declaredFilter));
        Assert.Equal(declaredFilter, filter.Groups[1].Value);
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
        Assert.Contains("RepositoryEvidenceWorkflowTests", testCommand);

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
        var checkout = FindStep(preflight["steps"].AsSequence(), "Checkout exact event head with full history");
        var checkoutWith = checkout["with"].AsMapping();
        Assert.Equal("${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}", checkoutWith["ref"].AsScalar());
        Assert.Equal("0", checkoutWith["fetch-depth"].AsScalar());

        var preflightRun = FindStep(preflight["steps"].AsSequence(), "Collect and plan solution coverage")["run"].AsScalar();
        Assert.Contains("--operation collect", preflightRun);
        Assert.Contains("source.baseCommit", preflightRun);
        Assert.Contains("--operation plan --scope coverage", preflightRun);
        Assert.Contains("selection.requiresCoverage", preflightRun);
        Assert.Contains("selection.requiresNativeCoverage", preflightRun);
        Assert.Contains("requires_native_coverage=$nativeRequired", preflightRun);
        Assert.Contains("ForgeTrust.AppSurface.Evidence.Validation.dll", preflightRun);
        Assert.DoesNotContain("python", preflightRun, StringComparison.OrdinalIgnoreCase);

        var coverageSecurity = jobs["coverage-security-platform"].AsMapping();
        Assert.Equal(["coverage-evidence-plan", "coverage-cli-candidate"], coverageSecurity["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", coverageSecurity["if"].AsScalar());
        var capture = FindStep(coverageSecurity["steps"].AsSequence(), "Attest successful native coverage evidence")["run"].AsScalar();
        Assert.Contains("'--operation', 'native'", capture);
        Assert.Contains("'--cli', $env:COVERAGE_CLI_PATH", capture);
        Assert.DoesNotContain("python", capture, StringComparison.OrdinalIgnoreCase);

        var build = jobs["build"].AsMapping();
        Assert.Contains("coverage-security-platform", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("packed-durable-consumers", build["needs"].AsSequence().Select(node => node.AsScalar()));
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", build["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result == 'skipped'", build["if"].AsScalar());
        var steps = build["steps"].AsSequence();
        var download = FindStep(steps, "Download same-run native coverage evidence");
        Assert.Contains("github.event_name == 'pull_request'", download["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", download["if"].AsScalar());
        var run = FindStep(steps, "Run solution coverage and gate");
        Assert.DoesNotContain(new YamlScalarNode("if"), run.Children.Keys);
        var command = run["run"].AsScalar();
        Assert.Contains("--operation plan --scope coverage", command);
        Assert.Contains("--operation run --scope coverage", command);
        Assert.Contains("--operation verify --scope coverage", command);
        Assert.Contains("--native-coverage-directory", command);
        Assert.Contains("--coverage-cli \"$COVERAGE_CLI_PATH\"", command);
        Assert.Contains("jq -r '.requiresCoverage'", command);
        Assert.Contains("jq -r '.requiresNativeCoverage'", command);
        Assert.Contains("GITHUB_EVENT_NAME" + "\" == \"pull_request\"", command);
        Assert.DoesNotContain("coverage-solution.sh", command, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD^1", command, StringComparison.Ordinal);

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
        Assert.Equal(["coverage-evidence-plan", "coverage-security-platform", "build"],
            manifestGate["needs"].AsSequence().Select(node => node.AsScalar()));
        var gateSteps = manifestGate["steps"].AsSequence();
        var failGate = FindStep(gateSteps, "Fail unless planning, selected native evidence, and the build passed");
        Assert.Contains("needs.build.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage == 'true'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result != 'success'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-security-platform.result != 'skipped'", failGate["if"].AsScalar());
        Assert.Contains("needs.coverage-evidence-plan.outputs.requires_native_coverage != 'true'", FindStep(gateSteps, "Confirm the selected coverage evidence gate passed")["if"].AsScalar());
    }

    [Fact]
    public void InstalledCoverageCandidateAndConsumersUseTheSameEventHead()
    {
        var expected = "${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}";
        var jobs = _buildWorkflow["jobs"].AsMapping();
        Assert.Equal(expected, _buildWorkflow["env"].AsMapping()["COVERAGE_SOURCE_COMMIT"].AsScalar());
        foreach (var name in new[] { "coverage-cli-candidate", "coverage-security-platform", "build" })
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
                var install = FindStep(job["steps"].AsSequence(), "Verify and install candidate CLI")["run"].AsScalar();
                Assert.Contains("$manifest.commit -ne $env:COVERAGE_SOURCE_COMMIT", install);
                Assert.Contains("$manifest.commit -ne $commit", install);
                Assert.Contains("$manifest.tree -ne $tree", install);
            }
        }
        Assert.Equal("${{ needs.coverage-evidence-plan.outputs.diff_base }}",
            jobs["coverage-security-platform"].AsMapping()["env"].AsMapping()["COVERAGE_GATE_DIFF_BASE"].AsScalar());
    }

    [Fact]
    public void SelectedNativePackagesRetainInstalledCoverageAndMetadataRejectionProofs()
    {
        var steps = NativeJob["steps"].AsSequence();
        var pack = FindStep(steps, "Pack selected native producer input");
        var proof = FindStep(steps, "Prove installed neutral coverage package and published application");
        var rejection = FindStep(steps, "Reject case-varied engine version skew before proof output");
        Assert.True(IndexOf(steps, pack) < IndexOf(steps, proof));
        Assert.True(IndexOf(steps, proof) < IndexOf(steps, rejection));
        foreach (var step in new[] { proof, rejection })
            Assert.Contains("steps.pack.outcome == 'success'", step["if"].AsScalar());
        Assert.Contains("--package-proof", proof["run"].AsScalar());
        Assert.Contains("/p:RepositoryCommit=$env:SOURCE_COMMIT", proof["run"].AsScalar());
        Assert.Contains("$LASTEXITCODE -eq 0 -or (Test-Path $output)", rejection["run"].AsScalar());
        var upload = FindStep(steps, "Preserve evidence inputs, plan, proof, and artifacts")["with"].AsMapping()["path"].AsScalar();
        Assert.Contains("native-coverage-library-proof/proof.json", upload);
        Assert.Contains("native-coverage-library-proof/published-proof/runs/**", upload);
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

    private YamlMappingNode NativeJob => _workflow["jobs"].AsMapping()["native-evidence"].AsMapping();

    private static YamlMappingNode FindStep(YamlSequenceNode steps, string name) =>
        steps.Select(step => step.AsMapping()).Single(step => step["name"].AsScalar() == name);

    private static int IndexOf(YamlSequenceNode steps, YamlMappingNode target) =>
        steps.Children.ToList().FindIndex(step => ReferenceEquals(step, target));
}

internal static class WorkflowYamlNodeExtensions
{
    public static YamlMappingNode AsMapping(this YamlNode node) => (YamlMappingNode)node;
    public static YamlSequenceNode AsSequence(this YamlNode node) => (YamlSequenceNode)node;
    public static string AsScalar(this YamlNode node) => ((YamlScalarNode)node).Value!;
}
