using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceTestProducerPlannerTests
{
    private const string ProjectPath = "tests/Tooling.Tests/Tooling.Tests.csproj";

    [Fact]
    public void ValidPolicy_ShouldValidateAndPlanTheTestProducer()
    {
        var policy = CreatePolicy(CreateProducer());

        EvidencePlanner.ValidatePolicy(policy);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);

        var producer = Assert.Single(plan.Profile.Producers);
        Assert.Equal(ProjectPath, Assert.Single(producer.Test!.Projects).Path);
    }

    [Fact]
    public void PolicyJson_ShouldRoundTripTheDocumentedDeclaration()
    {
        const string json = """
            {
              "id": "release-tooling-tests",
              "kind": "test",
              "version": "1.0.0",
              "requiredResources": [],
              "assertionIds": ["appsurface/test/discovery-complete@1"],
              "artifactSlots": [
                { "logicalName": "test-receipt", "relativeRoot": "test", "mediaType": "application/json", "required": true, "maximumBytes": 65536 }
              ],
              "timeoutSeconds": 900,
              "test": {
                "projects": [
                  { "path": "tests/Tooling.Tests/Tooling.Tests.csproj", "filter": "FullyQualifiedName~ReleaseWorkflowPolicyTests", "minimumTests": 10 }
                ]
              }
            }
            """;

        var producer = EvidenceCanonicalJson.Deserialize<EvidenceProducerDeclaration>(Encoding.UTF8.GetBytes(json));

        EvidencePlanner.ValidatePolicy(CreatePolicy(producer));
        var project = Assert.Single(producer.Test!.Projects);
        Assert.Equal("FullyQualifiedName~ReleaseWorkflowPolicyTests", project.Filter);
        Assert.Equal(10, project.MinimumTests);
        var canonical = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(producer));
        Assert.Contains("\"Filter\":\"FullyQualifiedName~ReleaseWorkflowPolicyTests\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"MinimumTests\":10", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentTestBlock_ShouldKeepExistingCanonicalDeclarationBytes()
    {
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], [], [], 60);
        var shapeBeforeTestBlock = new
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

        Assert.Equal(EvidenceCanonicalJson.Serialize(shapeBeforeTestBlock), EvidenceCanonicalJson.Serialize(declaration));
        Assert.Equal(EvidenceDigest.CanonicalSha256(shapeBeforeTestBlock), EvidenceDigest.CanonicalSha256(declaration));
    }

    [Fact]
    public void AbsentFilterAndMinimum_ShouldBeOmittedFromCanonicalJson()
    {
        var canonical = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(new EvidenceTestProject(ProjectPath)));

        Assert.DoesNotContain("Filter", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumTests", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectFilterAndMinimum_ShouldEachChangePolicyAndPlanDigests()
    {
        var baseline = Resolve(CreateProducer());
        var variants = new[]
        {
            Resolve(CreateProducer(new EvidenceTestProject("tests/Other.Tests/Other.Tests.csproj"))),
            Resolve(CreateProducer(new EvidenceTestProject(ProjectPath) { Filter = "FullyQualifiedName~Alpha" })),
            Resolve(CreateProducer(new EvidenceTestProject(ProjectPath) { MinimumTests = 2 })),
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline.PolicyDigest, variant.PolicyDigest);
            Assert.NotEqual(baseline.PlanDigest, variant.PlanDigest);
        }

        Assert.Equal(3, variants.Select(static plan => plan.PlanDigest).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void UnknownPolicyProperty_ShouldLoadAndPlanAsAnOlderReaderWould()
    {
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(CreatePolicy(CreateProducer())))
            .Replace("\"Test\":", "\"FutureBlock\":", StringComparison.Ordinal);

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(json));

        Assert.Null(Assert.Single(policy.Profiles[0].Producers).Test);
        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));
        Assert.Equal("ASEVD137", exception.Code);
        var asOtherKind = policy with
        {
            Profiles = [policy.Profiles[0] with { Producers = [policy.Profiles[0].Producers[0] with { Kind = "future-kind" }] }],
        };
        Assert.NotNull(new EvidencePlanner().Resolve(asOtherKind, [new NormalizedDiffPath("scripts/build.sh")]));
    }

    [Fact]
    public void TestBlockOnAnotherKind_ShouldBeRejected() =>
        AssertCode(CreateProducer() with { Kind = "coverage" }, "ASEVD143", "coverage");

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("tést")]
    public void UnsafeProducerId_ShouldBeRejected(string id) =>
        AssertCode(CreateProducer() with { Id = id }, "ASEVD149", id);

    [Fact]
    public void SafeProducerIdCharacters_ShouldBeAccepted() =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with { Id = "Az09._-" }));

    [Fact]
    public void MissingOrEmptyProjects_ShouldBeRejected()
    {
        AssertCode(CreateProducer() with { Test = null }, "ASEVD137", "release-tooling-tests");
        AssertCode(CreateProducer() with { Test = new EvidenceTestDeclaration([]) }, "ASEVD137", "release-tooling-tests");
        AssertCode(CreateProducer() with { Test = new EvidenceTestDeclaration(null!) }, "ASEVD137", "release-tooling-tests");
    }

    [Fact]
    public void TooManyProjects_ShouldBeRejected()
    {
        var projects = Enumerable.Range(0, 33).Select(static index => new EvidenceTestProject($"tests/P{index}/P{index}.csproj")).ToArray();

        AssertCode(CreateProducer(projects) with { ArtifactSlots = [Slot(1_000_000)] }, "ASEVD138", "33");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/abs/A.csproj")]
    [InlineData("tests\\A\\A.csproj")]
    [InlineData("tests/../A.csproj")]
    [InlineData("./A.csproj")]
    [InlineData("tests//A.csproj")]
    [InlineData("tests/-p/A.csproj")]
    [InlineData("-A.csproj")]
    [InlineData("tests/A.sln")]
    [InlineData("tests/A/A.csproj ")]
    [InlineData("tests/ A/A.csproj")]
    [InlineData("C:/tests/A.csproj")]
    [InlineData("tests/A\n/A.csproj")]
    public void InvalidProjectPath_ShouldBeRejected(string path) =>
        AssertCode(CreateProducer(new EvidenceTestProject(path)), "ASEVD139", "test.projects path");

    [Fact]
    public void NullProjectEntryPathOrOverlongPath_ShouldBeRejected()
    {
        AssertCode(CreateProducer(new EvidenceTestProject(null!)), "ASEVD139", "test.projects path");
        AssertCode(CreateProducer((EvidenceTestProject)null!), "ASEVD139", "test.projects path");
        AssertCode(CreateProducer(new EvidenceTestProject(new string('a', 506) + ".csproj")) with { ArtifactSlots = [Slot(1_000_000)] }, "ASEVD139", "512");
    }

    [Theory]
    [InlineData("tests/A/A.csproj")]
    [InlineData("tests/A/A.fsproj")]
    [InlineData("tests/A/A.vbproj")]
    [InlineData("A.CSPROJ")]
    [InlineData("tests/My Tests/My Tests.csproj")]
    public void ValidProjectPath_ShouldBeAccepted(string path) =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer(new EvidenceTestProject(path))));

    [Fact]
    public void DuplicateProjectPath_ShouldBeRejectedWithoutRegardToCase() =>
        AssertCode(
            CreateProducer(new EvidenceTestProject(ProjectPath), new EvidenceTestProject(ProjectPath.ToUpperInvariant().Replace(".CSPROJ", ".csproj", StringComparison.Ordinal))),
            "ASEVD140",
            "more than once");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Name=A\nName=B")]
    [InlineData("Name=A\tB")]
    [InlineData("Name=A\u2028B")]
    [InlineData("Name=A\u2029B")]
    public void InvalidFilter_ShouldBeRejected(string filter) =>
        AssertCode(CreateProducer(new EvidenceTestProject(ProjectPath) { Filter = filter }), "ASEVD141", "filter");

    [Fact]
    public void FilterLengthBoundary_ShouldBeEnforced()
    {
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer(new EvidenceTestProject(ProjectPath) { Filter = new string('a', 1024) })));
        AssertCode(CreateProducer(new EvidenceTestProject(ProjectPath) { Filter = new string('a', 1025) }), "ASEVD141", "1025");
    }

    [Fact]
    public void FilterOperatorsAndPunctuation_ShouldBeAccepted() =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer(
            new EvidenceTestProject(ProjectPath) { Filter = "(FullyQualifiedName~A.B|Name=x\\&y)&Category!=Slow, 50%;" })));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MinimumTestsBelowOne_ShouldBeRejected(int minimum) =>
        AssertCode(CreateProducer(new EvidenceTestProject(ProjectPath) { MinimumTests = minimum }), "ASEVD142", "minimumTests");

    [Fact]
    public void CoverageFields_ShouldBeRejected()
    {
        AssertCode(CreateProducer() with { CoverageGate = new EvidenceCoverageGateRequirements(95, 85) }, "ASEVD144", "coverage");
        AssertCode(CreateProducer() with { CoverageExecution = new EvidenceCoverageExecutionOptions("Release") }, "ASEVD144", "coverage");
        AssertCode(CreateProducer() with { CoverageArtifactRetention = "full" }, "ASEVD144", "coverage");
    }

    [Fact]
    public void AssertionIds_ShouldBeExactlyTheDiscoveryCompleteId()
    {
        AssertCode(CreateProducer() with { AssertionIds = [] }, "ASEVD145", EvidenceTestProducerContract.AssertionId);
        AssertCode(CreateProducer() with { AssertionIds = ["appsurface/coverage/behavioral-patch@1"] }, "ASEVD145", EvidenceTestProducerContract.AssertionId);
        AssertCode(
            CreateProducer() with { AssertionIds = [EvidenceTestProducerContract.AssertionId, "appsurface/coverage/behavioral-patch@1"] },
            "ASEVD145",
            "\"assertionIds\": [\"appsurface/test/discovery-complete@1\"]");
    }

    [Fact]
    public void ReceiptSlot_ShouldBeTheSingleRequiredJsonReceipt()
    {
        AssertCode(CreateProducer() with { ArtifactSlots = [] }, "ASEVD146", "\"logicalName\": \"test-receipt\"");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536), new EvidenceArtifactSlot("extra", "test", "text/plain", false, 10)] }, "ASEVD146", "2 slot(s)");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536) with { LogicalName = "receipt" }] }, "ASEVD146", "test-receipt");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536) with { MediaType = "text/plain" }] }, "ASEVD146", "application/json");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536) with { Required = false }] }, "ASEVD146", "\"required\": true");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536) with { RelativeRoot = "receipts" }] }, "ASEVD146", "relativeRoot 'test'");
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(65536) with { RelativeRoot = "test/nested" }] }, "ASEVD146", "\"relativeRoot\": \"test\"");
    }

    [Fact]
    public void ReceiptSlotSizeFloor_ShouldScaleWithProjectsAndPrintTheMinimum()
    {
        var minimum = 4096 + 512 + (6 * ProjectPath.Length);

        Assert.Equal(minimum, EvidenceTestProducerContract.GetMinimumReceiptBytes([new EvidenceTestProject(ProjectPath)]));
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with { ArtifactSlots = [Slot(minimum)] }));
        AssertCode(CreateProducer() with { ArtifactSlots = [Slot(minimum - 1)] }, "ASEVD147", minimum.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ReceiptSlotSizeFloor_ShouldHoldForThirtyTwoMaximumLengthPaths()
    {
        var projects = Enumerable.Range(0, 32)
            .Select(static index => new EvidenceTestProject($"{index:D2}" + new string('é', 503) + ".csproj"))
            .ToArray();
        var minimum = EvidenceTestProducerContract.GetMinimumReceiptBytes(projects);

        Assert.All(projects, static project => Assert.Equal(512, project.Path.Length));
        Assert.Equal(4096 + (32 * (512 + (6 * 512))), minimum);
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer(projects) with { ArtifactSlots = [Slot(minimum)] }));
        var exception = Assert.Throws<EvidencePlanningException>(
            () => EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer(projects) with { ArtifactSlots = [Slot(minimum - 1)] })));
        Assert.Equal("ASEVD147", exception.Code);
        Assert.Contains(minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: the receipt escapes non-ASCII and HTML-sensitive characters as six-byte sequences, so a floor
    /// counted in raw UTF-8 let a valid policy fail to write its receipt after the tests ran.
    /// </summary>
    [Theory]
    [InlineData('é')]
    [InlineData('界')]
    [InlineData('<')]
    [InlineData('a')]
    public void ReceiptSlotSizeFloor_ShouldHoldTheSerializedReceiptForThirtyTwoMaximumLengthPaths(char fill)
    {
        var projects = Enumerable.Range(0, 32)
            .Select(index => new EvidenceTestProject($"{index:D2}" + new string(fill, 503) + ".csproj"))
            .ToArray();
        var outcomes = projects
            .Select(static project => new TestProjectOutcome(
                project.Path,
                new string('f', 64),
                int.MaxValue,
                new TestRunCounts(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
                TestRunReason.MultiTargetUnsupported,
                "ListingShapeUnrecognized",
                "discover",
                [],
                null))
            .ToArray();

        Assert.True(TestEvidenceProducer.SerializeReceipt(outcomes).Length <= EvidenceTestProducerContract.GetMinimumReceiptBytes(projects));
    }

    [Fact]
    public void MinimumReceiptBytes_ShouldRejectNullAndTolerateNullEntries()
    {
        Assert.Throws<ArgumentNullException>(() => EvidenceTestProducerContract.GetMinimumReceiptBytes(null!));
        Assert.Equal(4096 + 512 + 512, EvidenceTestProducerContract.GetMinimumReceiptBytes([null!, new EvidenceTestProject(null!)]));
    }

    [Fact]
    public void TimeoutBelowThirtySeconds_ShouldBeRejected()
    {
        EvidencePlanner.ValidatePolicy(CreatePolicy(CreateProducer() with { TimeoutSeconds = 30 }));
        AssertCode(CreateProducer() with { TimeoutSeconds = 29 }, "ASEVD148", "29");
    }

    [Fact]
    public void OtherKinds_ShouldKeepValidatingWithUnsafeIdsAndNoTestBlock() =>
        EvidencePlanner.ValidatePolicy(CreatePolicy(
            new EvidenceProducerDeclaration("has space/slash", "custom", "1", [], [EvidenceTestProducerContract.AssertionId], [], 5)));

    private static void AssertCode(EvidenceProducerDeclaration producer, string code, string messageOrActionFragment)
    {
        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(CreatePolicy(producer)));

        Assert.Equal(code, exception.Code);
        Assert.Contains(messageOrActionFragment, exception.Message + " " + exception.Fix, StringComparison.Ordinal);
    }

    private static EvidencePlan Resolve(EvidenceProducerDeclaration producer) =>
        new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("scripts/build.sh")]);

    private static EvidenceArtifactSlot Slot(long maximumBytes) =>
        new(EvidenceTestProducerContract.ReceiptSlotLogicalName, "test", EvidenceTestProducerContract.ReceiptMediaType, true, maximumBytes);

    private static EvidenceProducerDeclaration CreateProducer(params EvidenceTestProject[] projects) =>
        new("release-tooling-tests", EvidenceTestProducerContract.Kind, "1.0.0", [], [EvidenceTestProducerContract.AssertionId], [Slot(65536)], 900)
        {
            Test = new EvidenceTestDeclaration(projects.Length == 0 ? [new EvidenceTestProject(ProjectPath)] : projects),
        };

    private static EvidencePolicy CreatePolicy(EvidenceProducerDeclaration producer) => new(
        "sample",
        "1",
        "tests",
        [
            new EvidenceProfile(
                "tests",
                EvidenceProfileScope.Targeted,
                [],
                [producer],
                [new EvidenceObligation("tests-complete", "tooling", "Selected tests ran and passed.", [producer.Id], EvidenceTestProducerContract.AssertionId)]),
        ],
        []);
}
