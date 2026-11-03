using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection("Native evidence program")]
public sealed class RepositoryEvidenceWorkflowTests : IDisposable
{
    private const string StoragePath = "tools/ForgeTrust.AppSurface.Evidence.Validation/PackedStorageProducer.cs";
    private const string ConsumerPath = "tools/ForgeTrust.AppSurface.Evidence.Validation/InstalledConsumerProducer.cs";
    private const string PolicyPath = ".github/evidence.policy.json";
    private readonly string _repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
    private readonly string _temporaryRoot = Directory.CreateTempSubdirectory("repository-evidence-workflow-tests-").FullName;
    private string CheckedInPolicyPath => TestPathUtils.PathUnder(_repository, PolicyPath);

    public static TheoryData<string[], string, string[]> SelectionCases => new()
    {
        { ["docs/installation.md"], "no-evidence", [] },
        { ["docs/README.md"], "no-evidence", [] },
        { ["guides/README.md"], "no-evidence", [] },
        { ["tests/README.md"], "coverage", ["solution-coverage"] },
        { ["Aspire/Example.Tests/README.md"], "coverage", ["solution-coverage"] },
        { [StoragePath], "storage", ["packed-storage", "solution-coverage"] },
        { [ConsumerPath], "consumer", ["installed-consumer", "solution-coverage"] },
        { [StoragePath, ConsumerPath], "combined-", ["installed-consumer", "packed-storage", "solution-coverage"] },
        { ["src/new-feature.cs"], "code", ["solution-coverage", "package-checks"] },
        { ["Web/Example.Tests/ExampleTests.cs"], "coverage", ["solution-coverage"] },
        { ["Web/Example.Tests/README.md"], "coverage", ["solution-coverage"] },
        { ["Web/Example/README.md"], "packages", ["package-checks"] },
        { ["Web/Example/Example.cs"], "code", ["solution-coverage", "package-checks"] },
    };

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task Plan_WritesSelectionForCheckedInPolicy(string[] paths, string expectedProfile, string[] expectedProducers)
    {
        var planned = await PlanAsync(paths);

        if (expectedProfile.EndsWith("-", StringComparison.Ordinal)) Assert.StartsWith(expectedProfile, planned.Plan.Profile.Id);
        else Assert.Equal(expectedProfile, planned.Plan.Profile.Id);
        Assert.Equal(expectedProducers.OrderBy(id => id, StringComparer.Ordinal),
            planned.Plan.Profile.Producers.Select(producer => producer.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(planned.Plan.PlanDigest,
            EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(planned.Output, "evidence-plan.json"))).PlanDigest);
        var requiresPackage = expectedProducers.Any(id => id is "packed-storage" or "installed-consumer");
        using var selection = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(planned.Output, "selection.json")));
        Assert.Equal(requiresPackage, selection.RootElement.GetProperty("requiresPackage").GetBoolean());
        var expectedNativeCoverage = expectedProducers.Contains("solution-coverage", StringComparer.Ordinal)
            && expectedProducers.Any(id => id != "solution-coverage");
        Assert.Equal(expectedNativeCoverage, selection.RootElement.GetProperty("requiresNativeCoverage").GetBoolean());
    }

    [Theory]
    [InlineData("tests/README.md", true, false)]
    [InlineData("docs/README.md", false, false)]
    [InlineData("src/new-feature.cs", true, true)]
    public async Task CoverageScopeSelection_RequiresNativeFanInOnlyWhenRootPlanHasAnotherProducer(
        string path,
        bool expectedCoverage,
        bool expectedNativeCoverage)
    {
        var planned = await PlanAsync([path], scope: "coverage");
        using var selection = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(planned.Output, "selection.json")));

        Assert.Equal(expectedCoverage, selection.RootElement.GetProperty("requiresCoverage").GetBoolean());
        Assert.Equal(expectedNativeCoverage, selection.RootElement.GetProperty("requiresNativeCoverage").GetBoolean());
    }

    [Theory]
    [InlineData("coverage", new[] { "solution-coverage" })]
    [InlineData("packages", new[] { "package-checks" })]
    [InlineData("native", new[] { "installed-consumer", "packed-storage" })]
    public async Task ScopedPlans_AcceptLongChangedPathsAndRetainCanonicalInventory(string scope, string[] expectedProducers)
    {
        const string path = "Durable/ForgeTrust.AppSurface.Durable.Templates/content/durable-worker/tests/AppSurfaceDurableWorker.Tests/FirstDurableWorkTests.cs";
        Assert.True(path.Length > 128);

        var planned = await PlanAsync([path, PolicyPath], scope: scope);

        Assert.Equal(expectedProducers, planned.Plan.Profile.Producers.Select(producer => producer.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, planned.Plan.ChangedPaths.Count);
        Assert.Contains(planned.Plan.ChangedPaths, changed => changed.Path == path);
        Assert.Equal(planned.Plan.PlanDigest, new EvidencePlanner().Resolve(planned.Plan.PolicySnapshot!, planned.Plan.ChangedPaths).PlanDigest);
    }

    [Theory]
    [InlineData(".github/evidence.policy.json")]
    [InlineData("tools/ForgeTrust.AppSurface.Evidence.Validation/RepositoryEvidenceWorkflow.cs")]
    public async Task PolicyAndWorkflowEdits_UseConservativeProfile(string path)
    {
        var planned = await PlanAsync([path]);

        Assert.Equal("native-acceptance", planned.Plan.Profile.Id);
        Assert.Contains(path == ".github/evidence.policy.json" ? "conservative:native-acceptance" : "evidence-tool", planned.Plan.MatchedRuleIds);
        Assert.Equal(["installed-consumer", "package-checks", "packed-storage", "solution-coverage"],
            planned.Plan.Profile.Producers.Select(producer => producer.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RenameFromCodeToDocs_RetainsCodeEvidence()
    {
        var paths = await WritePathsAsync([
            new NormalizedDiffPath("docs/feature-guide.md", "renamed", "src/feature.cs"),
        ]);
        var output = NewPath("rename-plan");
        var options = PlanOptions(paths, CheckedInPolicyPath, output);

        await RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None);

        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-plan.json")));
        Assert.Equal(["package-checks", "solution-coverage"], plan.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains("source-code", plan.MatchedRuleIds);
    }

    [Fact]
    public async Task TestOnlyCodeSelectsCoverageAndMixedCoveragePackageUnionIsDeterministic()
    {
        var test = await PlanAsync(["Aspire/ForgeTrust.AppSurface.Aspire.Tests/ExampleTests.cs"]);
        Assert.Equal(["solution-coverage"], test.Plan.Profile.Producers.Select(producer => producer.Id));

        var first = await PlanAsync(["src/new-feature.cs", "docs/guide.md"]);
        var second = await PlanAsync(["docs/guide.md", "src/new-feature.cs"]);
        Assert.Equal(first.Plan.Profile.Id, second.Plan.Profile.Id);
        Assert.Equal(first.Plan.PlanDigest, second.Plan.PlanDigest);
        Assert.Equal(["package-checks", "solution-coverage"], first.Plan.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnknownPathAndPolicySelfChangeSelectAllFourProducers()
    {
        var unknown = await PlanAsync(["assets/new-format.bin"]);
        Assert.Equal(["installed-consumer", "package-checks", "packed-storage", "solution-coverage"],
            unknown.Plan.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
        var rootPolicy = await PlanAsync([PolicyPath]);
        Assert.Equal(unknown.Plan.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            rootPolicy.Plan.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ScopeProjectionSelectsOnlyRequestedLaneAndKeepsConservativeFallback()
    {
        var policy = ReadCheckedInPolicy();
        var paths = new[] { new NormalizedDiffPath("src/new-feature.cs") };
        var coverage = RepositoryEvidenceWorkflow.ResolveScope(policy, paths, "coverage");
        var packages = RepositoryEvidenceWorkflow.ResolveScope(policy, paths, "packages");
        var native = RepositoryEvidenceWorkflow.ResolveScope(policy, paths, "native");
        Assert.Equal(["solution-coverage"], coverage.Profile.Producers.Select(p => p.Id));
        Assert.Equal(["package-checks"], packages.Profile.Producers.Select(p => p.Id));
        Assert.Empty(native.Profile.Producers);
        Assert.False(RepositoryEvidenceWorkflow.RequiresPackage(coverage.Profile));
        Assert.False(RepositoryEvidenceWorkflow.RequiresPackage(packages.Profile));
        Assert.False(RepositoryEvidenceWorkflow.RequiresPackage(native.Profile));
        var unknown = RepositoryEvidenceWorkflow.ResolveScope(policy, [new NormalizedDiffPath("unknown/input.bin")], "native");
        Assert.Equal(["installed-consumer", "packed-storage"], unknown.Profile.Producers.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.True(RepositoryEvidenceWorkflow.RequiresPackage(unknown.Profile));
    }

    [Theory]
    [InlineData("native", new[] { "installed-consumer", "packed-storage" })]
    [InlineData("coverage", new[] { "solution-coverage" })]
    [InlineData("packages", new[] { "package-checks" })]
    public async Task PolicyFileEditsRetainMandatoryProducersInEveryScope(string scope, string[] expected)
    {
        var policy = ReadCheckedInPolicy();
        var plan = RepositoryEvidenceWorkflow.ResolveScope(policy, [new NormalizedDiffPath(PolicyPath)], scope);

        Assert.Equal(expected, plan.Profile.Producers.Select(producer => producer.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PolicyPathCannotSelectANarrowerProfile_ThroughCurrentOrPreviousPath()
    {
        var policy = ReadCheckedInPolicy();
        var currentPolicyDowngrade = policy with
        {
            Rules = policy.Rules
                .Select(rule => rule.Id == "consumer-producer" ? rule with { ProfileId = "storage" } : rule)
                .Append(new EvidencePolicyRule("policy-self-selection", PolicyPath, "storage"))
                .ToArray(),
        };
        var currentPaths = new[]
        {
            new NormalizedDiffPath(PolicyPath),
            new NormalizedDiffPath(ConsumerPath),
        };
        Assert.Equal("storage", new EvidencePlanner().Resolve(currentPolicyDowngrade, currentPaths).Profile.Id);
        var currentPolicy = await WritePolicyAsync(currentPolicyDowngrade);
        var currentPathFile = await WritePathsAsync(currentPaths);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(currentPathFile, currentPolicy, NewPath("policy-current-plan")), CancellationToken.None));

        var previousPolicyDowngrade = policy with
        {
            Rules = policy.Rules.Append(new EvidencePolicyRule("policy-self-selection", PolicyPath, "no-evidence")).ToArray(),
        };
        var previousPolicy = await WritePolicyAsync(previousPolicyDowngrade);
        var previousPathFile = await WritePathsAsync([
            new NormalizedDiffPath("docs/moved-guide.md", "renamed", PolicyPath),
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(previousPathFile, previousPolicy, NewPath("policy-previous-plan")), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyChangedPaths_AreRejected()
    {
        var paths = await WritePathsAsync([]);

        await Assert.ThrowsAsync<EvidencePlanningException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(paths, CheckedInPolicyPath, NewPath("empty-plan")), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidPolicyAndProducerDeclarations_AreRejected()
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath("src/new-feature.cs")]);

        var emptyConservative = await WritePolicyAsync(ReadCheckedInPolicy() with { ConservativeProfileId = "no-evidence" });
        await Assert.ThrowsAsync<EvidencePlanningException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(paths, emptyConservative, NewPath("invalid-policy-plan")), CancellationToken.None));

        var unknownProducer = RenameProducerEverywhere(ReadCheckedInPolicy(), "packed-storage", "future-native-producer");
        var unknownProducerPath = await WritePolicyAsync(unknownProducer);
        await Assert.ThrowsAsync<ArgumentException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(paths, unknownProducerPath, NewPath("unknown-producer-plan")), CancellationToken.None));

        var policy = ReadCheckedInPolicy();
        var weakenedProfiles = policy.Profiles.Select(profile => profile.Id == "storage"
            ? profile with { Producers = profile.Producers.Select(producer => producer.Id == "packed-storage" ? producer with { TimeoutSeconds = 599 } : producer).ToArray() }
            : profile).ToArray();
        var weakened = await WritePolicyAsync(policy with { Profiles = weakenedProfiles });
        var storagePaths = await WritePathsAsync([new NormalizedDiffPath(StoragePath)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(storagePaths, weakened, NewPath("weakened-plan")), CancellationToken.None));
    }

    [Fact]
    public async Task ConservativeProfileMustContainBothRegisteredProducers()
    {
        var policy = ReadCheckedInPolicy();
        var storage = policy.Profiles.Single(profile => profile.Id == "storage");
        var narrowedProfiles = policy.Profiles.Select(profile => profile.Id == policy.ConservativeProfileId
            ? storage with { Id = policy.ConservativeProfileId }
            : profile).ToArray();
        var narrowedPolicy = await WritePolicyAsync(policy with { Profiles = narrowedProfiles });
        var paths = await WritePathsAsync([new NormalizedDiffPath("src/new-feature.cs")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.RunAsync(
            PlanOptions(paths, narrowedPolicy, NewPath("narrowed-conservative-plan")), CancellationToken.None));
    }

    [Fact]
    public async Task NoEvidencePlanRunAndVerify_SucceedWithoutPackageOrDotnet_AndRejectDrift()
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath("README.md")]);
        var planned = await PlanAsync(paths, CheckedInPolicyPath);
        Assert.Equal("no-evidence", planned.Plan.Profile.Id);

        var runOutput = NewPath("no-evidence-run");
        var runOptions = WorkflowOptions("run", paths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--output", runOutput));
        Assert.Equal(0, await RepositoryEvidenceWorkflow.RunAsync(runOptions, CancellationToken.None));
        var manifestPath = Path.Join(runOutput, "evidence-manifest.json");
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Empty(manifest.ProducerResults);

        var verifyOptions = WorkflowOptions("verify", paths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--manifest", manifestPath),
            ("--artifact-root", Path.Join(runOutput, "artifacts")));
        Assert.Equal(0, await RepositoryEvidenceWorkflow.RunAsync(verifyOptions, CancellationToken.None));

        var changedPaths = await WritePathsAsync([new NormalizedDiffPath("docs/another-guide.md")]);
        var mismatchedPlanOptions = WorkflowOptions("run", changedPaths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--output", NewPath("changed-diff-run")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryEvidenceWorkflow.RunAsync(mismatchedPlanOptions, CancellationToken.None));

        var changedPolicyPath = await WritePolicyAsync(ReadCheckedInPolicy() with { Version = "1.0.1" });
        var driftedPolicyOptions = WorkflowOptions("verify", paths, changedPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--manifest", manifestPath),
            ("--artifact-root", Path.Join(runOutput, "artifacts")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryEvidenceWorkflow.RunAsync(driftedPolicyOptions, CancellationToken.None));
    }

    [Fact]
    public async Task Run_RegistersCoverageAndPackageProducersForMinimalRepository()
    {
        var repository = Path.Join(_temporaryRoot, "minimal-repository");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Join(repository, "ForgeTrust.AppSurface.slnx"),
            "<Solution><Project Path=\"tests/Fixture.Tests/Fixture.Tests.csproj\" /></Solution>");
        var paths = await WritePathsAsync([new NormalizedDiffPath("src/feature.cs")]);
        var planned = await PlanAsync(paths, CheckedInPolicyPath);
        Assert.Equal(["package-checks", "solution-coverage"],
            planned.Plan.Profile.Producers.Select(producer => producer.Id).Order(StringComparer.Ordinal));
        var output = NewPath("minimal-repository-run");
        var options = WorkflowOptions("run", paths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--output", output),
            ("--repository", repository),
            ("--dotnet", ResolveDotnetHost()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));

        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(output, "evidence-manifest.json")));
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(["package-checks", "solution-coverage"],
            manifest.ProducerResults.Select(result => result.ProducerId).Order(StringComparer.Ordinal));
        Assert.All(manifest.ProducerResults, result => Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome));
        Assert.All(manifest.ProducerResults, result => Assert.NotEmpty(result.Artifacts ?? []));
    }

    [Fact]
    public async Task Verify_RejectsDigestValidPlanWithPolicyInconsistentProfile()
    {
        var policy = ReadCheckedInPolicy();
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("README.md")]);
        var forged = plan with
        {
            Profile = policy.Profiles.Single(profile => profile.Id == "storage"),
            PlanDigest = string.Empty,
        };
        forged = forged with { PlanDigest = EvidenceDigest.CanonicalSha256(forged) };
        var manifest = EvidenceManifestBuilder.Build(forged, []);

        Assert.True(EvidenceManifestBuilder.Verify(forged, manifest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            forged, manifest, NewPath("unused-artifacts"), CancellationToken.None));
    }

    [Theory]
    [InlineData(StoragePath)]
    [InlineData(ConsumerPath)]
    public async Task InvalidPackageRun_RetainsFailedManifest(string sourcePath)
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath(sourcePath)]);
        var planned = await PlanAsync(paths, CheckedInPolicyPath, "native");
        var output = NewPath("invalid-package-run");
        var options = WorkflowOptions("run", paths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")),
            ("--output", output),
            ("--scope", "native"),
            ("--package", Path.Join(_temporaryRoot, "missing-candidate.nupkg")),
            ("--version", "1.0.0-test"),
            ("--source-commit", "test-source"),
            ("--dotnet", ExistingProcessPath()),
            ("--repository", _repository),
            ("--scratch-parent", _temporaryRoot));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));

        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(output, "evidence-manifest.json")));
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(manifest.ProducerResults).Outcome);
    }

    [Fact]
    public async Task Verify_AcceptsSyntheticDeclaredArtifactsWrittenThroughArtifactWriter()
    {
        var evidence = await CreateSyntheticEvidenceAsync();

        Assert.Equal(EvidenceClaimKind.TargetedComplete, evidence.Manifest.ClaimKind);
        await RepositoryEvidenceWorkflow.VerifyAsync(
            evidence.Plan, evidence.Manifest, evidence.ArtifactRoot, CancellationToken.None);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("truncated")]
    public async Task Verify_RejectsMissingOrAlteredArtifactBytes(string change)
    {
        var evidence = await CreateSyntheticEvidenceAsync();
        if (change == "missing")
        {
            File.Delete(evidence.ResultPath);
        }
        else if (change == "truncated")
        {
            await File.WriteAllBytesAsync(evidence.ResultPath, [0x7B]);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(evidence.ResultPath);
            bytes[0] ^= 1;
            await File.WriteAllBytesAsync(evidence.ResultPath, bytes);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            evidence.Plan, evidence.Manifest, evidence.ArtifactRoot, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_RejectsSymlinkedArtifact()
    {
        if (OperatingSystem.IsWindows()) return;
        var evidence = await CreateSyntheticEvidenceAsync();
        var external = NewPath("outside-artifact");
        await File.WriteAllBytesAsync(external, Encoding.UTF8.GetBytes("{\"synthetic\":true}"));
        File.Delete(evidence.ResultPath);
        File.CreateSymbolicLink(evidence.ResultPath, external);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            evidence.Plan, evidence.Manifest, evidence.ArtifactRoot, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_RejectsNoneObservationOnlyAndCleanupFailureClaims()
    {
        var evidence = await CreateSyntheticEvidenceAsync();
        var passedResult = Assert.Single(evidence.Manifest.ProducerResults);
        var failedResult = passedResult with
        {
            Outcome = EvidenceProducerOutcome.Failed,
            SatisfiedAssertionIds = [],
        };
        var none = EvidenceManifestBuilder.Build(evidence.Plan, [failedResult]);
        var observation = EvidenceManifestBuilder.Build(evidence.Plan, [passedResult], observationOnly: true);
        var cleanupFailure = EvidenceManifestBuilder.Build(evidence.Plan, [passedResult],
            metrics: new EvidenceExecutionMetrics(CleanupCompleted: false, CleanupDiagnostic: "cleanup failed"));

        foreach (var manifest in new[] { none, observation, cleanupFailure })
        {
            Assert.True(EvidenceManifestBuilder.Verify(evidence.Plan, manifest));
            Assert.NotEqual(string.Empty, manifest.ManifestDigest);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
                evidence.Plan, manifest, evidence.ArtifactRoot, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownOperationAndUnsupportedOption()
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath("README.md")]);
        var invalidOperation = WorkflowOptions("publish", paths, CheckedInPolicyPath);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RepositoryEvidenceWorkflow.RunAsync(invalidOperation, CancellationToken.None));

        var unsupported = PlanOptions(paths, CheckedInPolicyPath, NewPath("unsupported-option-plan"));
        unsupported["--unexpected"] = "value";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RepositoryEvidenceWorkflow.RunAsync(unsupported, CancellationToken.None));
    }

    [Fact]
    public async Task PlanningPreflight_RejectsCancellationMissingOptionsAndExistingOutput()
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath("README.md")]);
        var output = NewPath("owned-output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Join(output, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        var options = PlanOptions(paths, CheckedInPolicyPath, output);
        await Assert.ThrowsAsync<ArgumentException>(() => RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFiles(output));

        options["--output"] = NewPath("cancelled-output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RepositoryEvidenceWorkflow.RunAsync(options, cancellation.Token));
        Assert.False(Directory.Exists(options["--output"]));
        options.Remove("--policy");
        await Assert.ThrowsAsync<ArgumentException>(() => RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));
    }

    [Fact]
    public async Task NativeRunPreflight_RequiresAnExistingDotnetHostBeforeCreatingOutput()
    {
        var paths = await WritePathsAsync([new NormalizedDiffPath(StoragePath)]);
        var planned = await PlanAsync(paths, CheckedInPolicyPath);
        var output = NewPath("missing-dotnet-output");
        var options = WorkflowOptions("run", paths, CheckedInPolicyPath,
            ("--plan", Path.Join(planned.Output, "evidence-plan.json")), ("--output", output),
            ("--dotnet", NewPath("missing-dotnet")));
        await Assert.ThrowsAsync<ArgumentException>(() => RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Verify_RejectsEditedManifestAndMissingPolicySnapshot()
    {
        var evidence = await CreateSyntheticEvidenceAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            evidence.Plan, evidence.Manifest with { ManifestDigest = new string('0', 64) }, evidence.ArtifactRoot, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            evidence.Plan with { PolicySnapshot = null }, evidence.Manifest, evidence.ArtifactRoot, CancellationToken.None));
    }

    private async Task<PlannedEvidence> PlanAsync(string[] pathValues, string? policyPath = null, string? scope = null)
    {
        var paths = await WritePathsAsync(pathValues.Select(path => new NormalizedDiffPath(path)).ToArray());
        return await PlanAsync(paths, policyPath ?? CheckedInPolicyPath, scope);
    }

    private async Task<PlannedEvidence> PlanAsync(string pathsPath, string policyPath, string? scope = null)
    {
        var output = NewPath("plan");
        var options = PlanOptions(pathsPath, policyPath, output);
        if (scope is not null) options["--scope"] = scope;
        Assert.Equal(0, await RepositoryEvidenceWorkflow.RunAsync(options, CancellationToken.None));
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(Path.Join(output, "evidence-plan.json")));
        return new PlannedEvidence(pathsPath, output, plan);
    }

    private EvidencePolicy ReadCheckedInPolicy() =>
        EvidenceCanonicalJson.Deserialize<EvidencePolicy>(File.ReadAllBytes(CheckedInPolicyPath));

    private async Task<string> WritePolicyAsync(EvidencePolicy policy)
    {
        var path = NewPath("policy");
        await File.WriteAllBytesAsync(path, EvidenceCanonicalJson.Serialize(policy));
        return path;
    }

    private async Task<string> WritePathsAsync(IReadOnlyList<NormalizedDiffPath> paths)
    {
        var path = NewPath("paths");
        await File.WriteAllBytesAsync(path, EvidenceCanonicalJson.Serialize(paths));
        return path;
    }

    private Dictionary<string, string> PlanOptions(string paths, string policy, string output) =>
        WorkflowOptions("plan", paths, policy, ("--output", output));

    private static Dictionary<string, string> WorkflowOptions(
        string operation,
        string paths,
        string policy,
        params (string Key, string Value)[] additional)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--operation"] = operation,
            ["--policy"] = policy,
            ["--changed-paths"] = paths,
        };
        foreach (var (key, value) in additional) options.Add(key, value);
        return options;
    }

    private async Task<SyntheticEvidence> CreateSyntheticEvidenceAsync()
    {
        var plan = RepositoryEvidenceWorkflow.ResolveScope(ReadCheckedInPolicy(), [new NormalizedDiffPath(StoragePath)], "native");
        var declaration = plan.Profile.Producers.Single();
        Assert.Equal(EvidenceDigest.CanonicalSha256(RepositoryEvidenceWorkflow.Declaration(declaration.Id)),
            EvidenceDigest.CanonicalSha256(declaration));
        var artifactRoot = NewPath("artifacts");
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(artifactRoot, declaration.Id));
        var resultBytes = Encoding.UTF8.GetBytes("{\"synthetic\":true}");
        await writer.WriteAsync("result", "proof/result.json", resultBytes);
        await writer.WriteAsync("transcript", "proof/transcript.json", Encoding.UTF8.GetBytes("[]"));
        var resultPath = Path.Join(artifactRoot, declaration.Id, "proof", "result.json");
        var result = new EvidenceProducerResult(declaration.Id, EvidenceProducerOutcome.Passed,
            declaration.AssertionIds, Artifacts: writer.WrittenArtifacts);
        var manifest = EvidenceManifestBuilder.Build(plan, [result]);
        return new SyntheticEvidence(plan, manifest, artifactRoot, resultPath);
    }

    private static EvidencePolicy RenameProducerEverywhere(EvidencePolicy policy, string oldId, string newId) =>
        policy with
        {
            Profiles = policy.Profiles.Select(profile => profile with
            {
                Producers = profile.Producers.Select(producer => producer.Id == oldId ? producer with { Id = newId } : producer).ToArray(),
                Obligations = profile.Obligations.Select(obligation => obligation with
                {
                    RequiredProducerIds = obligation.RequiredProducerIds
                        .Select(id => id == oldId ? newId : id).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };

    private string NewPath(string name) => Path.Join(_temporaryRoot, name + "-" + Guid.NewGuid().ToString("N"));

    private static string ExistingProcessPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)) return processPath;
        throw new InvalidOperationException("The running test process path is unavailable.");
    }

    private static string ResolveDotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host)) return host;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) return Path.Join(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).First(File.Exists);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
    }

    private sealed record PlannedEvidence(string Paths, string Output, EvidencePlan Plan);
    private sealed record SyntheticEvidence(EvidencePlan Plan, EvidenceManifest Manifest, string ArtifactRoot, string ResultPath);
}
