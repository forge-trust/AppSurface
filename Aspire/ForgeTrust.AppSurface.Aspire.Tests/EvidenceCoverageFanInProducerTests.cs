using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EvidenceCoverageFanInProducerCollection
{
    public const string Name = "Evidence coverage fan-in producer";
}

[Collection(EvidenceCoverageFanInProducerCollection.Name)]
public sealed class EvidenceCoverageFanInProducerTests
{
    private const string ProducerId = "coverage";
    private const string AssertionId = "coverage-accepted";
    private const string SlotId = "coverage-result";
    private const string SlotPath = "coverage/result.json";
    private const string DotnetPath = "dotnet";

    [Theory]
    [InlineData("id-null")]
    [InlineData("id-blank")]
    [InlineData("request")]
    [InlineData("policy")]
    [InlineData("test-projects")]
    [InlineData("supplements")]
    [InlineData("capture-paths")]
    [InlineData("assertion-null")]
    [InlineData("assertion-blank")]
    [InlineData("slot-null")]
    [InlineData("slot-blank")]
    public void ConstructorRejectsMissingIdentityAndCollectionShapes(string shape)
    {
        var policy = new CoverageFanInPolicy(new(new string('a', 40), new string('b', 40), "10.0.402", "Release"),
            new("local", "constructor-tests", "run-1", 1), ["checks/Verification.csproj"], [],
            new(CoverageGateMode.AggregateOnly));
        var request = new CoverageAcceptRequest(policy, "repository", "captured", [], "composed", "accepted");
        string id = ProducerId, assertion = AssertionId, slot = SlotId;
        switch (shape)
        {
            case "id-null": id = null!; break;
            case "id-blank": id = " \t"; break;
            case "request": request = null!; break;
            case "policy": request = request with { Policy = null! }; break;
            case "test-projects": request = request with { Policy = policy with { TestProjects = null! } }; break;
            case "supplements": request = request with { Policy = policy with { Supplements = null! } }; break;
            case "capture-paths": request = request with { SupplementaryCaptureDirectories = null! }; break;
            case "assertion-null": assertion = null!; break;
            case "assertion-blank": assertion = " \t"; break;
            case "slot-null": slot = null!; break;
            case "slot-blank": slot = " \t"; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }

        Assert.ThrowsAny<ArgumentException>(() => new EvidenceCoverageFanInProducer(id, request, assertion, slot));
    }

    [Fact]
    public async Task InvalidFacadePolicyReturnsSafeInvalidResultWithoutDeliveryOrAssertion()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.AcceptRequest() with
        {
            Policy = fixture.Policy with { Source = fixture.Policy.Source with { Sdk = string.Empty } }
        };
        var (producer, declaration, plan) = CreateProducer(request);
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.StartsWith("ASCOV301", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task NullContextIsRejectedBeforeAnyAcceptance()
    {
        using var fixture = await Fixture.CreateAsync();
        var (producer, _, _) = CreateProducer(fixture.AcceptRequest());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await producer.ProduceAsync(null!, CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Theory]
    [InlineData("line")]
    [InlineData("branch")]
    [InlineData("patch-line")]
    [InlineData("patch-branch")]
    [InlineData("tolerance")]
    [InlineData("line-mode")]
    [InlineData("missing")]
    public async Task DeclaredCoverageGateCannotBeSatisfiedByADifferentRequestPolicy(string difference)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var request = fixture.AcceptRequest();
        var declaration = Declaration() with
        {
            CoverageGate = difference switch
            {
                "line" => Declaration().CoverageGate! with { MinLinePercent = 100 },
                "branch" => Declaration().CoverageGate! with { MinBranchPercent = 100 },
                "patch-line" => Declaration().CoverageGate! with { MinPatchLinePercent = 100 },
                "patch-branch" => Declaration().CoverageGate! with { MinPatchBranchPercent = 100 },
                "tolerance" => Declaration().CoverageGate! with { TolerancePercent = 0 },
                "line-mode" => Declaration().CoverageGate! with { PatchLineMode = "measurable" },
                _ => null
            }
        };
        var producer = new EvidenceCoverageFanInProducer(ProducerId, request, AssertionId, SlotId);
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));

        var result = await producer.ProduceAsync(Context(Plan(declaration, request.ExpectedPatchSha256), declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Theory]
    [InlineData(null, EvidenceProducerOutcome.Invalid, false)]
    [InlineData("other-patch", EvidenceProducerOutcome.Failed, true)]
    public async Task PatchObligationRequiresThePlannedPatch(string? planningInput, EvidenceProducerOutcome outcome, bool delivered)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var request = fixture.AcceptRequest() with { ExpectedPatchSha256 = null };
        var (producer, declaration, _) = CreateProducer(request);
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));
        var plan = Plan(declaration, planningInput is null ? null : new string('f', 64));

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, outcome);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Equal(delivered ? 1 : 0, writer.WrittenArtifacts.Count);
    }

    [Fact]
    public async Task CurrentAcceptedGenerationIsDeliveredBeforeDeclaredAssertionIsReturned()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        Assert.True(result.Outcome == EvidenceProducerOutcome.Passed,
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath)));
        Assert.Equal([AssertionId], result.SatisfiedAssertionIds);
        Assert.True(Directory.Exists(fixture.Accepted));
        var artifact = Assert.Single(writer.WrittenArtifacts);
        Assert.Equal(SlotId, artifact.LogicalName);
        Assert.Equal(SlotPath, artifact.RelativePath);
        var json = await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath));
        Assert.Contains("\"outcome\":\"succeeded\"", json, StringComparison.Ordinal);
        Assert.Contains("\"evaluationIntent\":\"currentTrustedRun\"", json, StringComparison.Ordinal);
        Assert.Contains("\"patchOutcome\":\"PatchAccepted\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.TempPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage.xml", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalReplayCannotCloseCurrentEvidenceObligation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var request = fixture.AcceptRequest() with { EvaluationIntent = CoverageEvaluationIntent.HistoricalReplay };
        var (producer, declaration, plan) = CreateProducer(request);
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.StartsWith("ASCOV310", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task WrongProducerAssertionOrArtifactDeclarationCannotCloseObligation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var request = fixture.AcceptRequest();

        var (producer, declaration, plan) = CreateProducer(request);
        var wrongProducer = declaration with { Id = "other-coverage" };
        var result = await producer.ProduceAsync(Context(plan, wrongProducer,
            new EvidenceArtifactWriter(wrongProducer, Path.Join(fixture.TempPath, "wrong-producer"))), CancellationToken.None);
        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);

        var wrongAssertion = declaration with { AssertionIds = ["other-assertion"] };
        var wrongAssertionPlan = Plan(wrongAssertion);
        result = await producer.ProduceAsync(Context(wrongAssertionPlan, wrongAssertion,
            new EvidenceArtifactWriter(wrongAssertion, Path.Join(fixture.TempPath, "wrong-assertion"))), CancellationToken.None);
        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);

        var missingSlot = declaration with { ArtifactSlots = [] };
        result = await producer.ProduceAsync(Context(Plan(missingSlot), missingSlot,
            new EvidenceArtifactWriter(missingSlot, Path.Join(fixture.TempPath, "missing-slot"))), CancellationToken.None);
        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Theory]
    [InlineData("missing-producer")]
    [InlineData("missing-gate")]
    [InlineData("different-gate")]
    public async Task CorrectContextCannotOverrideMissingOrMismatchedProducerInPlan(string difference)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        IReadOnlyList<EvidenceProducerDeclaration> selected = difference switch
        {
            "missing-producer" => [],
            "missing-gate" => [declaration with { CoverageGate = null }],
            "different-gate" => [declaration with
            {
                CoverageGate = declaration.CoverageGate! with { MinLinePercent = 100 }
            }],
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        var mismatchedPlan = plan with { Profile = plan.Profile with { Producers = selected } };
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));

        var result = await producer.ProduceAsync(Context(mismatchedPlan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.StartsWith("ASCOV310", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task AggregateOnlyPolicyCannotMeetDefaultPatchRequirement()
    {
        using var fixture = await Fixture.CreateAsync(requirePatch: false);
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        var result = await producer.ProduceAsync(Context(plan, declaration,
            new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"))), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task ExplicitAggregateOnlyObligationIsAcceptedAndDeliveredWithoutClaimingPatchAcceptance()
    {
        using var fixture = await Fixture.CreateAsync(requirePatch: false);
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest(), requirePatch: false);
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal([AssertionId], result.SatisfiedAssertionIds);
        Assert.Single(writer.WrittenArtifacts);
        using var delivered = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath)));
        Assert.True(delivered.RootElement.GetProperty("accepted").GetBoolean());
        Assert.True(delivered.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("PatchNotRequested", delivered.RootElement.GetProperty("patchOutcome").GetString());
        Assert.True(delivered.RootElement.GetProperty("effects").GetProperty("generationCommitted").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedPrimaryAcceptanceIsDeliveredButCannotMeetObservedPrimaryRequirement(bool claimsObserved)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var manifestPath = Path.Join(fixture.Captured, "manifest.json");
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        if (claimsObserved)
        {
            var claimed = manifestText.Replace("\"TrustedProducer\"", "\"CollectedExecution\"", StringComparison.Ordinal);
            Assert.NotEqual(manifestText, claimed);
            await File.WriteAllTextAsync(manifestPath, claimed);
        }
        using var manifest = JsonDocument.Parse(manifestText);
        var importedPath = Path.Join(fixture.TempPath, "imported-primary");
        var imported = await CoverageFanIn.CaptureAsync(new CoverageCaptureRequest(
            fixture.Policy, fixture.Repository, importedPath, "primary", ImportDirectory: fixture.Captured,
            OriginalSourceRoot: manifest.RootElement.GetProperty("sourceRoot").GetString()));
        Assert.Equal(CoverageOperationOutcome.Succeeded, imported.Outcome);
        Assert.True(imported.Effects.Imported);
        Assert.Equal(claimsObserved ? CoverageObservation.CollectedExecution : CoverageObservation.TrustedProducer, imported.Observation);
        var composedPath = Path.Join(fixture.TempPath, "imported-composed");
        var composed = await CoverageFanIn.ComposeAsync(new CoverageComposeRequest(
            fixture.Policy, fixture.Repository, importedPath, [], composedPath));
        Assert.Equal(CoverageOperationOutcome.Succeeded, composed.Outcome);
        var request = fixture.AcceptRequest() with
        {
            PrimaryCaptureDirectory = importedPath,
            ComposedOutputDirectory = composedPath
        };
        var (producer, declaration, plan) = CreateProducer(request, requireObservedPrimary: true);
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Failed);
        Assert.StartsWith("ASCOV310", result.Diagnostic, StringComparison.Ordinal);
        Assert.Single(writer.WrittenArtifacts);
        using var delivered = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath)));
        Assert.Equal("succeeded", delivered.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(claimsObserved ? "collectedExecution" : "trustedProducer", delivered.RootElement.GetProperty("observation").GetString());
        Assert.True(delivered.RootElement.GetProperty("primaryImported").GetBoolean());
        Assert.True(delivered.RootElement.GetProperty("accepted").GetBoolean());
        Assert.True(delivered.RootElement.GetProperty("verified").GetBoolean());
        var effects = delivered.RootElement.GetProperty("effects");
        Assert.True(effects.GetProperty("imported").GetBoolean());
        Assert.True(effects.GetProperty("generationCommitted").GetBoolean());
        Assert.True(effects.GetProperty("cleanupComplete").GetBoolean());
    }

    [Fact]
    public async Task FailedAcceptanceDeliversSafeResultWithoutSatisfyingAssertion()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        Directory.Delete(fixture.Captured, recursive: true);
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Failed);
        Assert.StartsWith("ASCOV", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Accepted));
        var json = await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath));
        Assert.DoesNotContain(fixture.TempPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage.xml", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancelledAcceptanceReturnsCancelledWithoutAnAssertion()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), cancellation.Token);

        AssertNoAssertion(result, EvidenceProducerOutcome.Cancelled);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task MissingArtifactWriterCannotAcceptCoverage()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());

        var result = await producer.ProduceAsync(Context(plan, declaration, null), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Invalid);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task RealWriterFailureAfterCommitDoesNotReturnAssertion()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var (producer, declaration, plan) = CreateProducer(fixture.AcceptRequest());
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        Directory.CreateDirectory(artifactRoot);
        var wrongWriterDeclaration = declaration with { ArtifactSlots = [] };
        var writer = new EvidenceArtifactWriter(wrongWriterDeclaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        AssertNoAssertion(result, EvidenceProducerOutcome.Failed);
        Assert.StartsWith("ASCOV309", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(Directory.Exists(fixture.Accepted));
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task ConstructorSnapshotsMutablePolicyAndRequestPathCollections()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareInputsAsync();
        var mutableProjects = new List<string> { "checks/Verification.csproj" };
        var mutableSupplements = new List<CoverageSupplement>();
        var mutableSupplementPaths = new List<string>();
        var originalPolicy = fixture.Policy with { TestProjects = mutableProjects, Supplements = mutableSupplements };
        var request = fixture.AcceptRequest() with
        {
            Policy = originalPolicy,
            SupplementaryCaptureDirectories = mutableSupplementPaths
        };
        var (producer, declaration, plan) = CreateProducer(request);
        mutableProjects[0] = "changed-after-construction.csproj";
        mutableSupplements.Add(new("unplanned", "linux", "all"));
        mutableSupplementPaths.Add(Path.Join(fixture.TempPath, "unplanned-capture"));
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(fixture.TempPath, "evidence-artifacts"));

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        Assert.True(result.Outcome == EvidenceProducerOutcome.Passed,
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(fixture.TempPath, "evidence-artifacts", SlotPath)));
        Assert.Equal([AssertionId], result.SatisfiedAssertionIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructorSnapshotsSupplementProjectInventoryAndReceivingPaths(bool explicitInventory)
    {
        var projects = new List<string> { "checks/Verification.csproj" };
        var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux";
        var supplements = new List<CoverageSupplement>
        {
            new("native", platform, "Category=fast", explicitInventory ? projects : null)
        };
        using var fixture = await Fixture.CreateAsync(supplements: supplements);
        await fixture.PrepareInputsAsync();
        var paths = fixture.SupplementaryCaptures.ToList();
        var request = fixture.AcceptRequest() with { SupplementaryCaptureDirectories = paths };
        var (producer, declaration, plan) = CreateProducer(request);

        projects[0] = "changed-after-construction.csproj";
        supplements[0] = supplements[0] with { TestProjects = ["unplanned-after-construction.csproj"] };
        paths[0] = Path.Join(fixture.TempPath, "unplanned-capture");
        var artifactRoot = Path.Join(fixture.TempPath, "evidence-artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactRoot);

        var result = await producer.ProduceAsync(Context(plan, declaration, writer), CancellationToken.None);

        Assert.True(result.Outcome == EvidenceProducerOutcome.Passed,
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactRoot, SlotPath)));
        Assert.Equal([AssertionId], result.SatisfiedAssertionIds);
        Assert.Single(writer.WrittenArtifacts);
    }

    private static EvidenceProducerContext Context(EvidencePlan plan, EvidenceProducerDeclaration declaration,
        EvidenceArtifactWriter? writer) => new(plan, declaration, TimeProvider.System, writer);

    private static (EvidenceCoverageFanInProducer Producer, EvidenceProducerDeclaration Declaration, EvidencePlan Plan)
        CreateProducer(CoverageAcceptRequest request, bool requirePatch = true, bool requireObservedPrimary = false)
    {
        var producer = new EvidenceCoverageFanInProducer(ProducerId, request, AssertionId, SlotId,
            requirePatch, requireObservedPrimary);
        var declaration = Declaration();
        if (request.Policy.Gate.Mode == CoverageGateMode.AggregateOnly)
            declaration = declaration with
            {
                CoverageGate = new EvidenceCoverageGateRequirements(95, 85, PatchLineMode: "codecov", TolerancePercent: 0.5m)
            };
        return (producer, declaration, Plan(declaration, request.ExpectedPatchSha256));
    }

    private static EvidenceProducerDeclaration Declaration() => new(
        ProducerId,
        "coverage-fan-in",
        "1",
        [],
        [AssertionId],
        [new EvidenceArtifactSlot(SlotId, "coverage", "application/json", true, 1024 * 1024)],
        300,
        new EvidenceCoverageGateRequirements(95, 85, 95, 85, "codecov", 0.5m));

    private static EvidencePlan Plan(EvidenceProducerDeclaration declaration, string? patchSha256 = null) => new EvidencePlan(
        "1",
        "coverage-policy",
        new string('a', 64),
        new string('b', 64),
        new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("coverage-obligation", "coverage", "Verify current coverage", [ProducerId], AssertionId)]),
        [],
        [],
        new string('c', 64))
    { PatchSha256 = patchSha256 };

    private static void AssertNoAssertion(EvidenceProducerResult result, EvidenceProducerOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        Assert.DoesNotContain("/Users/", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string? InitialArtifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        private static readonly string? InitialPath = Environment.GetEnvironmentVariable("PATH");
        private string? _priorArtifactHome;
        private string? _priorPath;
        private bool _environmentCaptured;

        private Fixture(string tempPath, string baselineCommit, string commit, string tree, string sdk,
            CoverageFanInPolicy policy, string? patchSha256)
        {
            TempPath = tempPath;
            BaselineCommit = baselineCommit;
            Commit = commit;
            Tree = tree;
            Sdk = sdk;
            Policy = policy;
            PatchSha256 = patchSha256;
        }

        internal string TempPath { get; }
        internal string Repository => Path.Join(TempPath, "repo");
        internal string Captured => Path.Join(TempPath, "captured");
        internal string Composed => Path.Join(TempPath, "composed");
        internal string Accepted => Path.Join(TempPath, "accepted");
        internal string CoveragePath => Path.Join(TempPath, "producer", "coverage.xml");
        internal string TimingsPath => Path.Join(TempPath, "producer", "timings.json");
        internal string BaselineCommit { get; }
        internal string Commit { get; }
        internal string Tree { get; }
        internal string Sdk { get; }
        internal CoverageFanInPolicy Policy { get; }
        internal string? PatchSha256 { get; }
        internal IReadOnlyList<string> SupplementaryCaptures { get; private set; } = [];

        internal static async Task<Fixture> CreateAsync(bool requirePatch = true,
            IReadOnlyList<CoverageSupplement>? supplements = null)
        {
            var tempPath = Path.Join(Path.GetTempPath(), "appsurface-evidence-coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            var dotnetDirectory = Path.GetDirectoryName(DotnetPath)!;
            var priorPath = Environment.GetEnvironmentVariable("PATH");
            if (File.Exists(DotnetPath)) Environment.SetEnvironmentVariable("PATH", dotnetDirectory + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(tempPath, "store"));
            try
            {
                Write(tempPath, "repo/checks/Verification.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                Write(tempPath, "repo/src/Example.cs", "namespace Fixture; public sealed class Example { public int Value => 1; }");
                var repository = Path.Join(tempPath, "repo");
                await CommandAsync(repository, "git", "init");
                await CommandAsync(repository, "git", "config", "user.email", "test@example.invalid");
                await CommandAsync(repository, "git", "config", "user.name", "AppSurface tests");
                await CommandAsync(repository, "git", "add", ".");
                await CommandAsync(repository, "git", "commit", "-m", "coverage baseline");
                var baselineCommit = await CommandAsync(repository, "git", "rev-parse", "HEAD");
                Write(tempPath, "repo/src/Example.cs", "namespace Fixture; public sealed class Example { public int Value => 2; }");
                await CommandAsync(repository, "git", "add", ".");
                await CommandAsync(repository, "git", "commit", "-m", "current coverage source");
                var commit = await CommandAsync(repository, "git", "rev-parse", "HEAD");
                var tree = await CommandAsync(repository, "git", "rev-parse", "HEAD^{tree}");
                var sdk = await CommandAsync(repository, File.Exists(DotnetPath) ? DotnetPath : "dotnet", "--version");
                var gate = requirePatch
                    ? new CoverageGatePolicy(CoverageGateMode.AggregateAndPatch, DiffBase: baselineCommit)
                    : new CoverageGatePolicy(CoverageGateMode.AggregateOnly);
                var policy = new CoverageFanInPolicy(
                    new(commit, tree, sdk, "Release"),
                    new("local", "evidence-adapter-tests", Guid.NewGuid().ToString("N"), 1),
                    ["checks/Verification.csproj"],
                    supplements ?? [],
                    gate);
                var patchSha256 = requirePatch ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(await CommandAsync(repository, "git", false, "--no-pager", "diff", "--unified=0",
                        "--no-ext-diff", "--no-textconv", "--relative", baselineCommit + "...HEAD", "--")))) : null;
                return new Fixture(tempPath, baselineCommit, commit, tree, sdk, policy, patchSha256)
                {
                    _priorArtifactHome = InitialArtifactHome,
                    _priorPath = InitialPath,
                    _environmentCaptured = true
                };
            }
            catch
            {
                Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", InitialArtifactHome);
                Environment.SetEnvironmentVariable("PATH", InitialPath);
                DeleteFixture(tempPath);
                throw;
            }
        }

        internal async Task PrepareInputsAsync()
        {
            Write(TempPath, "producer/coverage.xml",
                "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"2\" branches-valid=\"2\" line-rate=\"1\" branch-rate=\"1\"><packages><package name=\"Fixture\"><classes><class name=\"Fixture.Example\" filename=\"src/Example.cs\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (2/2)\"/></lines></class></classes></package></packages></coverage>");
            Write(TempPath, "producer/timings.json",
                "{\"configuration\":\"Release\",\"coverageDriver\":\"collector\",\"merge\":{\"exitCode\":0},\"artifacts\":{\"coverageFiles\":1},\"projects\":[{\"project\":\"checks/Verification.csproj\",\"exitCode\":0,\"executionStatus\":\"completed\",\"coverageArtifactStatus\":\"produced\"}]}");
            var capture = await CoverageFanIn.CaptureAsync(new CoverageCaptureRequest(
                Policy, Repository, Captured, "primary", CoveragePath, TimingsPath));
            Assert.Equal(CoverageOperationOutcome.Succeeded, capture.Outcome);
            var supplements = new List<string>();
            foreach (var supplement in Policy.Supplements)
            {
                var output = Path.Join(TempPath, "captured-" + supplement.Id);
                var native = await CoverageFanIn.CaptureAsync(new CoverageCaptureRequest(
                    Policy, Repository, output, supplement.Id, CoveragePath, RawCoveragePath: CoveragePath));
                Assert.Equal(CoverageOperationOutcome.Succeeded, native.Outcome);
                supplements.Add(output);
            }
            SupplementaryCaptures = supplements.AsReadOnly();
            var compose = await CoverageFanIn.ComposeAsync(new CoverageComposeRequest(
                Policy, Repository, Captured, SupplementaryCaptures, Composed));
            Assert.Equal(CoverageOperationOutcome.Succeeded, compose.Outcome);
        }

        internal CoverageAcceptRequest AcceptRequest() => new(Policy, Repository, Captured, SupplementaryCaptures, Composed, Accepted)
        { ExpectedPatchSha256 = PatchSha256 };

        public void Dispose()
        {
            if (_environmentCaptured)
            {
                Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _priorArtifactHome);
                Environment.SetEnvironmentVariable("PATH", _priorPath);
            }
            DeleteFixture(TempPath);
        }

        private static void DeleteFixture(string root)
        {
            if (!Directory.Exists(root)) return;
            var gitObjects = Path.Join(root, "repo", ".git", "objects");
            if (OperatingSystem.IsWindows() && Directory.Exists(gitObjects))
            {
                // Git writes read-only objects. Only clear that flag in this test's own repository.
                foreach (var file in Directory.EnumerateFiles(gitObjects, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            Directory.Delete(root, recursive: true);
        }

        private static void Write(string root, string relative, string contents)
        {
            var path = TestPathUtils.PathUnder(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private static Task<string> CommandAsync(string workingDirectory, string executable, params string[] arguments)
            => CommandAsync(workingDirectory, executable, true, arguments);

        private static async Task<string> CommandAsync(string workingDirectory, string executable, bool trim, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await stderr;
            Assert.True(process.ExitCode == 0, error);
            var output = await stdout;
            return trim ? output.Trim() : output;
        }
    }
}
