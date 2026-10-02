using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidencePolicyShadowValidatorTests
{
    [Theory]
    [InlineData("deleted")]
    [InlineData("weakened")]
    public void Validate_ShouldDetectDeletedOrWeakenedResourceRule(string change)
    {
        var basePolicy = CreatePolicy();
        var candidatePolicy = CreatePolicyWithChangedResourceRule(basePolicy, change);
        var fixture = Fixture("database", EvidencePolicyShadowFixtureKind.Resource, "resources/migrations/001.sql");

        var result = EvidencePolicyShadowValidator.Validate(basePolicy, candidatePolicy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        var selection = Assert.Single(result.Selections);
        Assert.Equal("resource", selection.BaseProfileId);
        Assert.NotEqual(selection.BaseProfileId, selection.CandidateProfileId);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS007"
            && diagnostic.Path == fixture.ChangedPath.Path
            && diagnostic.Message.Contains("resource 'resource-database'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRetainBaseFixtureWhenCandidateRemovesIt()
    {
        var basePolicy = CreatePolicy();
        var candidatePolicy = CreatePolicyWithChangedResourceRule(basePolicy, "deleted");
        var baseFixture = Fixture("database", EvidencePolicyShadowFixtureKind.Resource, "resources/migrations/001.sql");

        var result = EvidencePolicyShadowValidator.Validate(basePolicy, candidatePolicy, [baseFixture], []);

        Assert.False(result.IsCompatible);
        var baseSelection = Assert.Single(result.Selections);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, baseSelection.FixtureSource);
        Assert.Equal(baseFixture.ChangedPath, baseSelection.ChangedPath);
        Assert.Equal("resource", baseSelection.BaseProfileId);
        Assert.Equal("code", baseSelection.CandidateProfileId);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS004"
            && diagnostic.FixtureId == baseFixture.Id
            && diagnostic.Message.Contains("removed", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ASEPS007" && diagnostic.Path == baseFixture.ChangedPath.Path);
    }

    [Fact]
    public void Validate_ShouldKeepBaseFixtureWhenCandidateEditsItsInput()
    {
        var policy = CreatePolicy();
        var baseFixture = Fixture("control", EvidencePolicyShadowFixtureKind.ControlPlane, ".github/workflows/evidence-gate.yml");
        var candidateFixture = Fixture("control", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [baseFixture], [candidateFixture]);

        Assert.False(result.IsCompatible);
        Assert.Collection(
            result.Selections.OrderBy(static selection => selection.FixtureSource),
            selection =>
            {
                Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
                Assert.Equal(baseFixture.ChangedPath, selection.ChangedPath);
                Assert.Equal("control", selection.BaseProfileId);
                Assert.Equal("control", selection.CandidateProfileId);
            },
            selection =>
            {
                Assert.Equal(EvidencePolicyShadowFixtureSource.Candidate, selection.FixtureSource);
                Assert.Equal(candidateFixture.ChangedPath, selection.ChangedPath);
                Assert.Equal("documentation", selection.BaseProfileId);
                Assert.Equal("documentation", selection.CandidateProfileId);
            });
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS004"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.Message.Contains("edited", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldReportUnrecognizedControlPlanePathEvenWhenFallbackIsConservative()
    {
        var basePolicy = CreatePolicy();
        var candidatePolicy = CreatePolicyWithChangedResourceRule(basePolicy, "deleted");
        var fixture = new EvidencePolicyShadowFixture(
            "new-workflow",
            EvidencePolicyShadowFixtureKind.ControlPlane,
            new NormalizedDiffPath(
                ".github/workflows/new-gate.yml",
                "renamed",
                ".github/workflows/old-gate.yml"));

        var result = EvidencePolicyShadowValidator.Validate(basePolicy, candidatePolicy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        var unrecognizedPaths = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "ASEPS008")
            .Select(static diagnostic => diagnostic.Path)
            .OrderBy(static path => path, StringComparer.Ordinal);
        Assert.Equal(
            new[] { ".github/workflows/new-gate.yml", ".github/workflows/old-gate.yml" },
            unrecognizedPaths);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ASEPS007");
    }

    [Fact]
    public void Validate_ShouldRequireExplicitControlPlaneMappingInTrustedBasePolicy()
    {
        var policy = CreatePolicy();
        var basePolicy = policy with
        {
            Rules = policy.Rules.Where(static rule => rule.Id != "control").ToArray(),
        };
        var candidatePolicy = basePolicy with
        {
            Rules =
            [
                .. basePolicy.Rules,
                new EvidencePolicyRule("control", ".github/**", policy.ConservativeProfileId),
            ],
        };
        var fixture = Fixture("new-workflow", EvidencePolicyShadowFixtureKind.ControlPlane, ".github/workflows/new-gate.yml");

        var result = EvidencePolicyShadowValidator.Validate(basePolicy, candidatePolicy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASEPS008", diagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, diagnostic.FixtureSource);
        Assert.Equal(fixture.Id, diagnostic.FixtureId);
        Assert.Equal(fixture.ChangedPath.Path, diagnostic.Path);
        Assert.Contains("base policy does not explicitly recognize", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldResolveDocumentationCodeResourceControlPlaneAndUnknownCasesUnderBothPolicies()
    {
        var policy = CreatePolicy();
        EvidencePolicyShadowFixture[] fixtures =
        [
            Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md"),
            Fixture("code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs"),
            Fixture("resource", EvidencePolicyShadowFixtureKind.Resource, "resources/database/seed.sql"),
            Fixture("control", EvidencePolicyShadowFixtureKind.ControlPlane, ".github/workflows/evidence-gate.yml"),
            Fixture("unknown", EvidencePolicyShadowFixtureKind.Unknown, "new-area/unclassified.bin"),
        ];

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, fixtures, fixtures);

        Assert.True(result.IsCompatible);
        Assert.Equal(5, result.Selections.Count);
        Assert.Collection(
            result.Selections,
            selection => AssertProfiles(selection, "code", "code"),
            selection => AssertProfiles(selection, "control", "control"),
            selection => AssertProfiles(selection, "docs", "documentation"),
            selection => AssertProfiles(selection, "resource", "resource"),
            selection => AssertProfiles(selection, "unknown", "conservative"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Validate_ShouldResolveBothSidesOfRenameFixtures()
    {
        var policy = CreatePolicy();
        var fixture = new EvidencePolicyShadowFixture(
            "cross-class-rename",
            EvidencePolicyShadowFixtureKind.Resource,
            new NormalizedDiffPath("src/NewFeature.cs", "renamed", "resources/OldFeature.cs"));

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [fixture], [fixture]);

        Assert.True(result.IsCompatible);
        var selection = Assert.Single(result.Selections);
        Assert.Equal("conservative", selection.BaseProfileId);
        Assert.Equal("conservative", selection.CandidateProfileId);
        Assert.Equal("resources/OldFeature.cs", selection.ChangedPath.PreviousPath);
    }

    [Fact]
    public void Validate_ShouldCapDiagnosticsDeterministically()
    {
        var policy = CreatePolicy();
        var baseFixtures = Enumerable.Range(0, 40)
            .Select(index => Fixture($"fixture-{index:D2}", EvidencePolicyShadowFixtureKind.Code, $"src/Feature{index:D2}.cs"))
            .ToArray();
        var candidateFixtures = baseFixtures
            .Select(fixture => fixture with
            {
                ChangedPath = new NormalizedDiffPath($"docs/{fixture.Id}.md"),
            })
            .ToArray();

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, baseFixtures, candidateFixtures);

        Assert.False(result.IsCompatible);
        Assert.Equal(EvidencePolicyShadowValidator.MaximumDiagnostics, result.Diagnostics.Count);
        Assert.True(result.DiagnosticsTruncated);
        Assert.Equal(
            result.Diagnostics.OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.FixtureSource)
                .ThenBy(static diagnostic => diagnostic.FixtureId, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal),
            result.Diagnostics);
    }

    [Fact]
    public void Validate_ShouldReportInvalidCandidateGatePolicyWithoutClaimingCompatibility()
    {
        var policy = CreatePolicy();
        var invalidCandidate = policy with { ConservativeProfileId = "documentation" };
        var fixture = Fixture("code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");

        var result = EvidencePolicyShadowValidator.Validate(policy, invalidCandidate, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        Assert.Empty(result.Selections);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ASEPS002");
    }

    [Fact]
    public void Validate_ShouldReportIncompletePolicyCollectionsForEitherPolicySide()
    {
        var policy = CreatePolicy();
        var fixture = Fixture("code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");
        var invalidPolicies = new[]
        {
            (EvidencePolicyShadowFixtureSource.Base, policy with { Profiles = null! }),
            (EvidencePolicyShadowFixtureSource.Base, policy with { Rules = null! }),
            (EvidencePolicyShadowFixtureSource.Candidate, policy with { Profiles = null! }),
            (EvidencePolicyShadowFixtureSource.Candidate, policy with { Rules = null! }),
        };

        foreach (var (source, invalidPolicy) in invalidPolicies)
        {
            var result = source == EvidencePolicyShadowFixtureSource.Base
                ? EvidencePolicyShadowValidator.Validate(invalidPolicy, policy, [fixture], [fixture])
                : EvidencePolicyShadowValidator.Validate(policy, invalidPolicy, [fixture], [fixture]);

            Assert.False(result.IsCompatible);
            Assert.Empty(result.Selections);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(source == EvidencePolicyShadowFixtureSource.Base ? "ASEPS001" : "ASEPS002", diagnostic.Code);
            Assert.Equal(source, diagnostic.FixtureSource);
            Assert.Contains("incomplete", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validate_ShouldReportInvalidTrustedBaseGatePolicyWithoutClaimingCompatibility()
    {
        var policy = CreatePolicy();
        var invalidBasePolicy = policy with { ConservativeProfileId = "missing-profile" };
        var fixture = Fixture("code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");

        var result = EvidencePolicyShadowValidator.Validate(invalidBasePolicy, policy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        Assert.Empty(result.Selections);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASEPS001", diagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, diagnostic.FixtureSource);
        Assert.Contains("base gate policy is invalid", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldPreserveBaseCasesWhenCandidateFixturesExceedTheLimit()
    {
        var policy = CreatePolicy();
        var baseFixture = Fixture("authoritative", EvidencePolicyShadowFixtureKind.Code, "src/Authoritative.cs");
        var candidateFixtures = Enumerable.Range(0, EvidencePolicyShadowValidator.MaximumFixtures + 1)
            .Select(index => Fixture($"candidate-{index:D2}", EvidencePolicyShadowFixtureKind.Code, $"src/Candidate{index:D2}.cs"))
            .ToArray();

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [baseFixture], candidateFixtures);

        Assert.False(result.IsCompatible);
        var selection = Assert.Single(result.Selections);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
        Assert.Equal(baseFixture.Id, selection.FixtureId);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS003"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.FixtureId is null
            && diagnostic.Path is null
            && diagnostic.Message.Contains("authoritative base cases remain active", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectCandidatePoliciesBeyondProfileAndRuleLimits()
    {
        var policy = CreatePolicy();
        var fixture = Fixture("code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");
        var oversizedProfiles = policy with
        {
            Profiles = Enumerable.Repeat(policy.Profiles[0], 65).ToArray(),
        };
        var oversizedRules = policy with
        {
            Rules = Enumerable.Repeat(policy.Rules[0], 257).ToArray(),
        };

        var profileResult = EvidencePolicyShadowValidator.Validate(policy, oversizedProfiles, [fixture], [fixture]);
        var ruleResult = EvidencePolicyShadowValidator.Validate(policy, oversizedRules, [fixture], [fixture]);

        Assert.False(profileResult.IsCompatible);
        Assert.Empty(profileResult.Selections);
        Assert.Contains(profileResult.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS002"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.Message.Contains("bounded policy shape", StringComparison.Ordinal));
        Assert.False(ruleResult.IsCompatible);
        Assert.Empty(ruleResult.Selections);
        Assert.Contains(ruleResult.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS002"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.Message.Contains("bounded policy shape", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldKeepBaseCasesWhenCandidateFixtureDataIsMalformedOrDuplicated()
    {
        var policy = CreatePolicy();
        var baseFixture = Fixture("base-code", EvidencePolicyShadowFixtureKind.Code, "src/Base.cs");
        EvidencePolicyShadowFixture[] candidateFixtures =
        [
            Fixture("supplemental", EvidencePolicyShadowFixtureKind.Code, "src/First.cs"),
            Fixture("supplemental", EvidencePolicyShadowFixtureKind.Code, "src/Second.cs"),
            Fixture(" ", EvidencePolicyShadowFixtureKind.Code, "src/Invalid.cs"),
        ];

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [baseFixture], candidateFixtures);

        Assert.False(result.IsCompatible);
        var selection = Assert.Single(result.Selections);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
        Assert.Equal(baseFixture.Id, selection.FixtureId);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS003"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.Message.Contains("must be unique", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ASEPS003"
            && diagnostic.FixtureSource == EvidencePolicyShadowFixtureSource.Candidate
            && diagnostic.Message.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldBoundDiagnosticsForMalformedCandidateFixtureFields()
    {
        var policy = CreatePolicy();
        var baseFixture = Fixture("base-code", EvidencePolicyShadowFixtureKind.Code, "src/Base.cs");
        var longIdentifier = new string('i', 129);
        var longPath = new string('p', 513);
        EvidencePolicyShadowFixture[] candidateFixtures =
        [
            null!,
            new("invalid-kind", (EvidencePolicyShadowFixtureKind)99, new NormalizedDiffPath("src/InvalidKind.cs")),
            new("null-path", EvidencePolicyShadowFixtureKind.Code, null!),
            Fixture("empty-path", EvidencePolicyShadowFixtureKind.Code, string.Empty),
            Fixture(longIdentifier, EvidencePolicyShadowFixtureKind.Code, "src/LongId.cs"),
            Fixture("long-path", EvidencePolicyShadowFixtureKind.Code, longPath),
            new("long-kind", EvidencePolicyShadowFixtureKind.Code, new NormalizedDiffPath("src/LongKind.cs", new string('k', 33))),
            new("long-previous", EvidencePolicyShadowFixtureKind.Code, new NormalizedDiffPath("src/New.cs", "renamed", longPath)),
        ];

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [baseFixture], candidateFixtures);

        Assert.False(result.IsCompatible);
        Assert.Collection(
            result.Selections,
            selection =>
            {
                Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
                Assert.Equal(baseFixture.Id, selection.FixtureId);
            });
        Assert.Equal(candidateFixtures.Length, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal("ASEPS003", diagnostic.Code);
            Assert.Equal(EvidencePolicyShadowFixtureSource.Candidate, diagnostic.FixtureSource);
        });
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.FixtureId is null
            && diagnostic.Kind is null
            && diagnostic.Path is null);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.FixtureId == "invalid-kind"
            && diagnostic.Kind is null);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.FixtureId == "null-path"
            && diagnostic.Kind == EvidencePolicyShadowFixtureKind.Code
            && diagnostic.Path is null);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.FixtureId == "empty-path"
            && diagnostic.Path == string.Empty);
        var boundedIdentifierDiagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.FixtureId?.Length == 128);
        Assert.Equal(128, boundedIdentifierDiagnostic.FixtureId!.Length);
        Assert.EndsWith("…", boundedIdentifierDiagnostic.FixtureId, StringComparison.Ordinal);
        var boundedPathDiagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.FixtureId == "long-path");
        Assert.Equal(512, boundedPathDiagnostic.Path!.Length);
        Assert.EndsWith("…", boundedPathDiagnostic.Path, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.FixtureId == "long-kind");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.FixtureId == "long-previous");
    }

    [Fact]
    public void Validate_ShouldReportCandidatePlannerFailureForAmbiguousPathRules()
    {
        var policy = CreatePolicy();
        var candidatePolicy = policy with
        {
            Rules =
            [
                .. policy.Rules,
                new EvidencePolicyRule("conflicting-code", "src/**", "resource"),
            ],
        };
        var fixture = Fixture("ambiguous-code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");

        var result = EvidencePolicyShadowValidator.Validate(policy, candidatePolicy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        Assert.Empty(result.Selections);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASEPS006", diagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Candidate, diagnostic.FixtureSource);
        Assert.Equal(fixture.Id, diagnostic.FixtureId);
        Assert.Equal(fixture.ChangedPath.Path, diagnostic.Path);
        Assert.Contains("candidate policy could not resolve", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldFailClosedForEmptyOrInvalidTrustedBaseFixtureSets()
    {
        var policy = CreatePolicy();
        var emptyResult = EvidencePolicyShadowValidator.Validate(policy, policy, [], []);
        var validCandidateFixture = Fixture("candidate-only", EvidencePolicyShadowFixtureKind.Code, "src/Candidate.cs");
        EvidencePolicyShadowFixture[] invalidBaseFixtures =
        [
            Fixture(" ", EvidencePolicyShadowFixtureKind.Code, "src/invalid-id.cs"),
            Fixture("duplicate", EvidencePolicyShadowFixtureKind.Code, "src/first.cs"),
            Fixture("duplicate", EvidencePolicyShadowFixtureKind.Code, "src/second.cs"),
        ];
        var invalidResult = EvidencePolicyShadowValidator.Validate(policy, policy, invalidBaseFixtures, [validCandidateFixture]);
        var oversizedBaseFixtures = Enumerable.Range(0, EvidencePolicyShadowValidator.MaximumFixtures + 1)
            .Select(index => Fixture($"base-{index:D2}", EvidencePolicyShadowFixtureKind.Code, $"src/Base{index:D2}.cs"))
            .ToArray();
        var oversizedResult = EvidencePolicyShadowValidator.Validate(policy, policy, oversizedBaseFixtures, []);

        Assert.False(emptyResult.IsCompatible);
        Assert.Empty(emptyResult.Selections);
        var emptyDiagnostic = Assert.Single(emptyResult.Diagnostics);
        Assert.Equal("ASEPS003", emptyDiagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, emptyDiagnostic.FixtureSource);
        Assert.Null(emptyDiagnostic.FixtureId);
        Assert.Null(emptyDiagnostic.Path);
        Assert.Contains("trusted base fixture set is empty", emptyDiagnostic.Message, StringComparison.Ordinal);

        Assert.False(invalidResult.IsCompatible);
        Assert.Empty(invalidResult.Selections);
        Assert.Equal(2, invalidResult.Diagnostics.Count);
        Assert.All(invalidResult.Diagnostics, diagnostic =>
        {
            Assert.Equal("ASEPS003", diagnostic.Code);
            Assert.Equal(EvidencePolicyShadowFixtureSource.Base, diagnostic.FixtureSource);
        });
        Assert.Contains(invalidResult.Diagnostics, diagnostic => diagnostic.Message.Contains("incomplete", StringComparison.Ordinal));
        Assert.Contains(invalidResult.Diagnostics, diagnostic => diagnostic.Message.Contains("must be unique", StringComparison.Ordinal));

        Assert.False(oversizedResult.IsCompatible);
        Assert.Empty(oversizedResult.Selections);
        var oversizedDiagnostic = Assert.Single(oversizedResult.Diagnostics);
        Assert.Equal("ASEPS003", oversizedDiagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, oversizedDiagnostic.FixtureSource);
        Assert.Contains("base fixture set exceeds", oversizedDiagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldIncludeNewCandidateFixturesAsSupplementalSelections()
    {
        var policy = CreatePolicy();
        var baseFixture = Fixture("base-code", EvidencePolicyShadowFixtureKind.Code, "src/Base.cs");
        var supplementalFixture = Fixture("added-docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/new-guide.md");

        var result = EvidencePolicyShadowValidator.Validate(policy, policy, [baseFixture], [baseFixture, supplementalFixture]);

        Assert.True(result.IsCompatible);
        Assert.Collection(
            result.Selections,
            selection =>
            {
                Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
                Assert.Equal(baseFixture.Id, selection.FixtureId);
                Assert.Equal("code", selection.BaseProfileId);
                Assert.Equal("code", selection.CandidateProfileId);
            },
            selection =>
            {
                Assert.Equal(EvidencePolicyShadowFixtureSource.Candidate, selection.FixtureSource);
                Assert.Equal(supplementalFixture.Id, selection.FixtureId);
                Assert.Equal("documentation", selection.BaseProfileId);
                Assert.Equal("documentation", selection.CandidateProfileId);
            });
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Validate_ShouldReportTrustedBasePlannerFailureForAmbiguousPathRules()
    {
        var policy = CreatePolicy();
        var ambiguousBasePolicy = policy with
        {
            Rules =
            [
                .. policy.Rules,
                new EvidencePolicyRule("conflicting-code", "src/**", "resource"),
            ],
        };
        var fixture = Fixture("ambiguous-base-code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");

        var result = EvidencePolicyShadowValidator.Validate(ambiguousBasePolicy, policy, [fixture], [fixture]);

        Assert.False(result.IsCompatible);
        Assert.Empty(result.Selections);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASEPS005", diagnostic.Code);
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, diagnostic.FixtureSource);
        Assert.Equal(fixture.Id, diagnostic.FixtureId);
        Assert.Equal(fixture.ChangedPath.Path, diagnostic.Path);
        Assert.Contains("base policy could not resolve", diagnostic.Message, StringComparison.Ordinal);
    }

    private static void AssertProfiles(EvidencePolicyShadowSelection selection, string fixtureId, string profileId)
    {
        Assert.Equal(EvidencePolicyShadowFixtureSource.Base, selection.FixtureSource);
        Assert.Equal(fixtureId, selection.FixtureId);
        Assert.Equal(profileId, selection.BaseProfileId);
        Assert.Equal(profileId, selection.CandidateProfileId);
    }

    private static EvidencePolicyShadowFixture Fixture(
        string id,
        EvidencePolicyShadowFixtureKind kind,
        string path) =>
        new(id, kind, new NormalizedDiffPath(path));

    private static EvidencePolicy CreatePolicy()
    {
        var documentation = new EvidenceProfile("documentation", EvidenceProfileScope.Targeted, [], [], []);
        var code = CreateRequirementProfile("code", "code", hasResource: false);
        var resource = CreateRequirementProfile("resource", "resource", hasResource: true);
        var control = CreateRequirementProfile("control", "control", hasResource: true);
        var conservative = new EvidenceProfile(
            "conservative",
            EvidenceProfileScope.Targeted,
            [.. code.Resources, .. resource.Resources, .. control.Resources],
            [.. code.Producers, .. resource.Producers, .. control.Producers],
            [.. code.Obligations, .. resource.Obligations, .. control.Obligations]);

        return new EvidencePolicy(
            "shadow-test",
            "1",
            "conservative",
            [conservative, documentation, code, resource, control],
            [
                new EvidencePolicyRule("docs", "docs/**", documentation.Id),
                new EvidencePolicyRule("code", "src/**", code.Id),
                new EvidencePolicyRule("resource", "resources/**", resource.Id),
                new EvidencePolicyRule("control", ".github/**", control.Id),
            ]);
    }

    private static EvidencePolicy CreatePolicyWithChangedResourceRule(EvidencePolicy policy, string change)
    {
        var rules = policy.Rules
            .Where(static rule => rule.Id is "docs" or "code")
            .ToList();
        if (change == "weakened")
        {
            rules.Add(new EvidencePolicyRule("resource", "resources/**", "documentation"));
        }

        return policy with
        {
            ConservativeProfileId = "code",
            Rules = rules,
        };
    }

    private static EvidenceProfile CreateRequirementProfile(string profileId, string prefix, bool hasResource)
    {
        var resourceId = $"{prefix}-database";
        var producerId = $"{prefix}-tests";
        var assertionId = $"{prefix}/passed@1";
        var resources = hasResource
            ? new[] { new EvidenceResourceDeclaration(resourceId, "aspire_health", 30, []) }
            : [];
        var producer = new EvidenceProducerDeclaration(
            producerId,
            "integration-tests",
            "1.0.0",
            hasResource ? [resourceId] : [],
            [assertionId],
            [new EvidenceArtifactSlot("report", prefix, "application/json", Required: true, MaximumBytes: 1024)],
            60);
        var obligation = new EvidenceObligation(
            $"{prefix}-obligation",
            "integration",
            $"The {prefix} profile must pass.",
            [producerId],
            assertionId);

        return new EvidenceProfile(profileId, EvidenceProfileScope.Targeted, resources, [producer], [obligation]);
    }
}
