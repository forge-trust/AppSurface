using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceScopedCoveragePlannerTests
{
    private const string ProjectPath = "Config/ForgeTrust.AppSurface.Config.Tests/ForgeTrust.AppSurface.Config.Tests.csproj";
    private const string SourcePattern = "Config/ForgeTrust.AppSurface.Config/**";
    private const string Assembly = "ForgeTrust.AppSurface.Config";

    [Fact]
    public void ValidPolicy_ShouldValidateAndPlanTheScopedProducer()
    {
        var policy = CreatePolicy(CreateProducer());

        EvidencePlanner.ValidatePolicy(policy);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("Config/ForgeTrust.AppSurface.Config/Feature.cs")]);

        var producer = Assert.Single(plan.Profile.Producers);
        Assert.Equal(ProjectPath, Assert.Single(producer.CoverageScope!.Projects));
        Assert.Equal(Assembly, Assert.Single(producer.CoverageScope.Sources).Assembly);
    }

    [Fact]
    public void PolicyJson_ShouldRoundTripTheDocumentedDeclaration()
    {
        const string json = """
            {
              "id": "config-patch-coverage",
              "kind": "coverage",
              "version": "1.0.0",
              "requiredResources": [],
              "assertionIds": ["appsurface/coverage/patch-scoped@1"],
              "artifactSlots": [
                { "logicalName": "scoped-coverage-receipt", "relativeRoot": "coverage-scope", "mediaType": "application/json", "required": true, "maximumBytes": 262144 }
              ],
              "timeoutSeconds": 900,
              "coverageScope": {
                "projects": ["Config/ForgeTrust.AppSurface.Config.Tests/ForgeTrust.AppSurface.Config.Tests.csproj"],
                "sources": [
                  { "pattern": "Config/ForgeTrust.AppSurface.Config/**", "assembly": "ForgeTrust.AppSurface.Config" }
                ],
                "minPatchLinePercent": 95,
                "minPatchBranchPercent": 85,
                "patchLineMode": "codecov",
                "tolerancePercent": 0.5
              }
            }
            """;

        var producer = EvidenceCanonicalJson.Deserialize<EvidenceProducerDeclaration>(Encoding.UTF8.GetBytes(json));

        EvidencePlanner.ValidatePolicy(CreatePolicy(producer));
        Assert.Equal(95m, producer.CoverageScope!.MinPatchLinePercent);
        Assert.Equal("codecov", producer.CoverageScope.PatchLineMode);
        var canonical = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(producer));
        Assert.Contains("\"CoverageScope\":{", canonical, StringComparison.Ordinal);
        Assert.Contains("\"Assembly\":\"ForgeTrust.AppSurface.Config\"", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentScopeBlock_ShouldKeepExistingCanonicalDeclarationBytes()
    {
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/a@1"], [], 60,
            new EvidenceCoverageGateRequirements(90, 80));
        var shapeBeforeScopeBlock = new
        {
            declaration.Id,
            declaration.Kind,
            declaration.Version,
            declaration.RequiredResources,
            declaration.AssertionIds,
            declaration.ArtifactSlots,
            declaration.TimeoutSeconds,
            declaration.CoverageGate,
            declaration.CoverageArtifactRetention,
        };

        Assert.Equal(EvidenceCanonicalJson.Serialize(shapeBeforeScopeBlock), EvidenceCanonicalJson.Serialize(declaration));
        Assert.Equal(EvidenceDigest.CanonicalSha256(shapeBeforeScopeBlock), EvidenceDigest.CanonicalSha256(declaration));
    }

    [Fact]
    public void ScopeChange_ShouldChangeThePolicyDigest()
    {
        var original = CreatePolicy(CreateProducer());
        var changed = CreatePolicy(CreateProducer() with
        {
            CoverageScope = CreateScope() with { Sources = [new EvidenceCoverageScopeSource(SourcePattern, "Other.Assembly")] },
        });

        Assert.NotEqual(EvidenceDigest.CanonicalSha256(original), EvidenceDigest.CanonicalSha256(changed));
    }

    [Fact]
    public void ScopeOnAnotherKind_ShouldFailWithAsevd150() =>
        AssertCode(CreateProducer() with { Kind = "custom" }, "ASEVD150", "only kind 'coverage' may");

    [Fact]
    public void ScopedAssertionWithoutAScope_ShouldFailWithAsevd157() =>
        AssertCode(CreateProducer() with { CoverageScope = null }, "ASEVD157", "without a coverageScope block");

    [Fact]
    public void ScopedAssertionOnAnotherKind_ShouldFailWithAsevd157() =>
        AssertCode(
            new EvidenceProducerDeclaration("custom", "custom", "1", [], [EvidenceScopedCoverageContract.AssertionId], [], 60),
            "ASEVD157",
            "remove the scoped assertion id");

    [Theory]
    [InlineData("has space")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void UnsafeId_ShouldFailWithAsevd151(string id) =>
        AssertCode(CreateProducer() with { Id = id }, "ASEVD151", "one safe path segment");

    [Fact]
    public void ScopeWithACoverageGate_ShouldFailWithAsevd152() =>
        AssertCode(CreateProducer() with { CoverageGate = new EvidenceCoverageGateRequirements(90, 80) }, "ASEVD152", "both coverageScope and coverageGate");

    public static TheoryData<string[]?> InvalidProjects => new()
    {
        null,
        Array.Empty<string>(),
        Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumProjects + 1).Select(static index => $"tests/P{index}/P{index}.csproj").ToArray(),
        new[] { "tests/A/A.dll" },
        new[] { "../A/A.csproj" },
        new[] { "/abs/A.csproj" },
        new[] { "tests\\A\\A.csproj" },
        new[] { ProjectPath, ProjectPath.ToUpperInvariant() },
        new[] { (string)null! },
        new[] { new string('a', EvidenceScopedCoverageContract.MaximumPathLength) + ".csproj" },
    };

    [Theory]
    [MemberData(nameof(InvalidProjects))]
    public void InvalidProjects_ShouldFailWithAsevd153(string[]? projects) =>
        AssertCode(CreateProducer() with { CoverageScope = CreateScope() with { Projects = projects! } }, "ASEVD153", "coverageScope.projects");

    public static TheoryData<EvidenceCoverageScopeSource[]?> InvalidSources => new()
    {
        null,
        Array.Empty<EvidenceCoverageScopeSource>(),
        Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumSources + 1).Select(static index => new EvidenceCoverageScopeSource($"src/S{index}/**", "A")).ToArray(),
        new[] { (EvidenceCoverageScopeSource)null! },
        new[] { new EvidenceCoverageScopeSource(null!, Assembly) },
        new[] { new EvidenceCoverageScopeSource("src/a?.cs", Assembly) },
        new[] { new EvidenceCoverageScopeSource("src/**x/a.cs", Assembly) },
        new[] { new EvidenceCoverageScopeSource("src/" + new string('a', EvidenceScopedCoverageContract.MaximumPathLength), Assembly) },
        new[] { new EvidenceCoverageScopeSource(SourcePattern, "") },
        new[] { new EvidenceCoverageScopeSource(SourcePattern, null!) },
        new[] { new EvidenceCoverageScopeSource(SourcePattern, "Bad Name") },
        new[] { new EvidenceCoverageScopeSource(SourcePattern, new string('a', EvidenceScopedCoverageContract.MaximumAssemblyNameLength + 1)) },
        new[] { new EvidenceCoverageScopeSource(SourcePattern, Assembly), new EvidenceCoverageScopeSource(SourcePattern, Assembly) },
    };

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public void InvalidSources_ShouldFailWithAsevd154(EvidenceCoverageScopeSource[]? sources) =>
        AssertCode(CreateProducer() with { CoverageScope = CreateScope() with { Sources = sources! } }, "ASEVD154", "coverageScope.sources");

    public static TheoryData<EvidenceCoverageScope> InvalidThresholds => new()
    {
        CreateScope() with { MinPatchLinePercent = null, MinPatchBranchPercent = null },
        CreateScope() with { MinPatchLinePercent = 101 },
        CreateScope() with { MinPatchLinePercent = -1 },
        CreateScope() with { MinPatchBranchPercent = 100.5m },
        CreateScope() with { TolerancePercent = -0.1m },
        CreateScope() with { PatchLineMode = "lines" },
    };

    [Theory]
    [MemberData(nameof(InvalidThresholds))]
    public void InvalidThresholds_ShouldFailWithAsevd155(EvidenceCoverageScope scope) =>
        AssertCode(CreateProducer() with { CoverageScope = scope }, "ASEVD155", "invalid coverageScope thresholds");

    [Fact]
    public void BranchThresholdOnlyAndUppercaseMode_ShouldValidate() =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with
        {
            CoverageScope = CreateScope() with { MinPatchLinePercent = null, PatchLineMode = "CODECOV" },
        }));

    [Theory]
    [InlineData("")]
    [InlineData("coverage/repository@1")]
    [InlineData(EvidenceScopedCoverageContract.AssertionId + "|coverage/repository@1")]
    public void WrongAssertionIds_ShouldFailWithAsevd156(string assertionIds) =>
        AssertCode(
            CreateProducer() with { AssertionIds = assertionIds.Split('|', StringSplitOptions.RemoveEmptyEntries) },
            "ASEVD156",
            "never closes a repository-wide coverage assertion");

    public static TheoryData<EvidenceArtifactSlot[]> InvalidSlots => new()
    {
        Array.Empty<EvidenceArtifactSlot>(),
        new[] { Slot(262144), Slot(262144) with { LogicalName = "extra" } },
        new[] { Slot(262144) with { LogicalName = "receipt" } },
        new[] { Slot(262144) with { MediaType = "text/plain" } },
        new[] { Slot(262144) with { RelativeRoot = "coverage" } },
        new[] { Slot(262144) with { Required = false } },
        new[] { Slot(262144) with { MaximumFiles = 1 } },
    };

    [Theory]
    [MemberData(nameof(InvalidSlots))]
    public void InvalidReceiptSlot_ShouldFailWithAsevd158(EvidenceArtifactSlot[] slots) =>
        AssertCode(CreateProducer() with { ArtifactSlots = slots }, "ASEVD158", "\"maximumBytes\": 262144");

    [Fact]
    public void ReceiptSlotBelowTheDerivedMinimum_ShouldFailWithAsevd159()
    {
        var minimum = EvidenceScopedCoverageContract.GetMinimumReceiptBytes(CreateScope());

        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(minimum - 1)] }, "ASEVD159", $"at least {minimum}");
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with { ArtifactSlots = [Slot(minimum)] }));
    }

    [Fact]
    public void ReleaseProfile_ShouldFailWithAsevd160()
    {
        var producer = CreateProducer();
        var policy = CreatePolicy(producer) with
        {
            Profiles = [CreatePolicy(producer).Profiles[0] with { Scope = EvidenceProfileScope.Release }],
        };

        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));

        Assert.Equal("ASEVD160", exception.Code);
        Assert.Contains("only in a Targeted profile", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/A/**", "src/A/Feature.cs")]
    [InlineData("src/*/**", "src/B/**")]
    [InlineData("src/A", "src/A/**")]
    [InlineData("src/A/**", "src/A")]
    [InlineData("src/A/Feature.cs", "src/A/Feature.cs/**/**")]
    [InlineData("**/*.cs", "src/B/**")]
    public void OverlappingSourcesWithDifferentAssemblies_ShouldFailWithAsevd161(string left, string right) =>
        AssertCode(
            CreateProducer() with
            {
                CoverageScope = CreateScope() with
                {
                    Sources = [new EvidenceCoverageScopeSource(left, "Alpha"), new EvidenceCoverageScopeSource(right, "Beta")],
                },
            },
            "ASEVD161",
            "can match the same path with different assemblies");

    [Theory]
    [InlineData("src/A/**", "src/B/**")]
    [InlineData("src/A/Feature.cs", "src/A/Other.cs")]
    [InlineData("src/A", "src/A/Feature.cs")]
    [InlineData("src/A/x/**", "src/A/y/*.cs")]
    public void DisjointSourcesWithDifferentAssemblies_ShouldValidate(string left, string right) =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with
        {
            CoverageScope = CreateScope() with
            {
                Sources = [new EvidenceCoverageScopeSource(left, "Alpha"), new EvidenceCoverageScopeSource(right, "Beta")],
            },
        }));

    [Fact]
    public void OverlappingSourcesWithTheSameAssembly_ShouldValidate() =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with
        {
            CoverageScope = CreateScope() with
            {
                Sources = [new EvidenceCoverageScopeSource("src/A/**", "Alpha"), new EvidenceCoverageScopeSource("src/A/*.cs", "Alpha")],
            },
        }));

    [Fact]
    public void ObligationForARepositoryWideAssertion_ShouldFailWithAsevd115()
    {
        var producer = CreateProducer();
        var policy = CreatePolicy(producer);
        policy = policy with
        {
            Profiles =
            [
                policy.Profiles[0] with
                {
                    Obligations = [new EvidenceObligation("coverage", "code", "Repository coverage.", [producer.Id], "appsurface/coverage/solution-complete@1")],
                },
            ],
        };

        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));

        Assert.Equal("ASEVD115", exception.Code);
    }

    [Fact]
    public void MinimumReceiptBytes_ShouldGrowWithDeclaredPathsAndRejectNull()
    {
        var small = EvidenceScopedCoverageContract.GetMinimumReceiptBytes(CreateScope());
        var larger = EvidenceScopedCoverageContract.GetMinimumReceiptBytes(CreateScope() with { Projects = [ProjectPath, "tests/Other/Other.csproj"] });
        var nullLists = EvidenceScopedCoverageContract.GetMinimumReceiptBytes(new EvidenceCoverageScope(null!, [null!, new EvidenceCoverageScopeSource(null!, null!)]));

        Assert.True(larger > small);
        Assert.True(small <= 262144, "The documented example slot must hold a one-project, one-source receipt.");
        Assert.Equal(8192 + (EvidenceScopedCoverageContract.MaximumUnknownFiles * ((6L * EvidenceScopedCoverageContract.MaximumReceiptPathLength) + 64)) + 256, nullLists);
        Assert.Throws<ArgumentNullException>(() => EvidenceScopedCoverageContract.GetMinimumReceiptBytes(null!));
    }

    internal static EvidenceCoverageScope CreateScope() =>
        new([ProjectPath], [new EvidenceCoverageScopeSource(SourcePattern, Assembly)], 95, 85, "codecov", 0.5m);

    internal static EvidenceArtifactSlot Slot(long maximumBytes) =>
        new(EvidenceScopedCoverageContract.ReceiptSlotLogicalName, EvidenceScopedCoverageContract.ReceiptRelativeRoot, EvidenceScopedCoverageContract.ReceiptMediaType, true, maximumBytes);

    internal static EvidenceProducerDeclaration CreateProducer() =>
        new("config-patch-coverage", "coverage", "1.0.0", [], [EvidenceScopedCoverageContract.AssertionId], [Slot(262144)], 900)
        {
            CoverageScope = CreateScope(),
        };

    internal static EvidencePolicy CreatePolicy(EvidenceProducerDeclaration producer) => new(
        "sample",
        "1",
        "config-scoped",
        [
            new EvidenceProfile(
                "config-scoped",
                EvidenceProfileScope.Targeted,
                [],
                [producer],
                [new EvidenceObligation("config-patch", "code", "Changed Config lines are covered by Config tests.", [producer.Id], EvidenceScopedCoverageContract.AssertionId)]),
        ],
        []);

    private static void AssertCode(EvidenceProducerDeclaration producer, string code, string messageOrActionFragment)
    {
        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(CreatePolicy(producer)));

        Assert.Equal(code, exception.Code);
        Assert.Contains(messageOrActionFragment, exception.Message + " " + exception.Fix, StringComparison.Ordinal);
    }
}
