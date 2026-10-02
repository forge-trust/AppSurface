using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class AppSurfaceEvidencePolicyTests
{
    private const string PolicyRelativePath = ".appsurface/evidence/evidence.policy.json";
    private const string ShadowFixturesRelativePath = "docs/fixtures/issue-777-policy-shadow/fixtures.json";
    private static readonly EvidencePolicy Policy = LoadPolicy();
    private static readonly EvidencePlanner Planner = new();

    [Theory]
    [InlineData("docs/overview.md")]
    [InlineData("guides/evidencehost-cookbook.md")]
    [InlineData("start-here/evidencehost.md")]
    [InlineData("README.md")]
    [InlineData("CONTRIBUTING.md")]
    public void ReviewedNarrativeDocumentation_ShouldProduceExplicitNoEvidenceClaim(string path)
    {
        // These paths contain human-facing reference and onboarding content only. The separately tested
        // workflow, policy, design, script, and gate-implementation paths retain evidence obligations.
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        Assert.Equal("documentation-only", plan.Profile.Id);
        Assert.Equal(EvidenceProfileScope.Targeted, plan.Profile.Scope);
        Assert.Empty(plan.Profile.Resources);
        Assert.Empty(plan.Profile.Producers);
        Assert.Empty(plan.Profile.Obligations);

        var manifest = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifest.Eligibility);
    }

    [Theory]
    [InlineData("docs/designs/new-design.md")]
    [InlineData("docs/plans/new-plan.md")]
    [InlineData("docs/evidence-gate-rollout.md")]
    [InlineData("guides/nested/behavior-contract.md")]
    public void DesignPlanRolloutAndNestedDocumentation_ShouldRemainFailClosed(string path)
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        AssertConservativeUnion(plan);
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "control-plane-integrity");
    }

    [Fact]
    public void ExecutableCode_ShouldSelectBoundedCoverageObligation()
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath("examples/web-app/ExampleModule.cs")]);

        Assert.Equal("code-coverage", plan.Profile.Id);
        var coverage = Assert.Single(plan.Profile.Producers);
        Assert.Equal("coverage", coverage.Id);
        Assert.Equal(1200, coverage.TimeoutSeconds);
        Assert.Equal(
            new EvidenceCoverageGateRequirements(95, 85, 95, 85, "codecov", 0.5m),
            coverage.CoverageGate);
        Assert.All(coverage.ArtifactSlots, slot => Assert.InRange(slot.MaximumBytes, 1, 20_971_520));
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "behavioral-coverage");

        var unregistered = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, unregistered.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, unregistered.ClaimKind);
    }

    [Theory]
    [InlineData("Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Store.cs", "postgresql-integration", "postgresql-contract")]
    [InlineData("examples/auth-aspire-keycloak-apphost/Program.cs", "keycloak-theme-integration", "keycloak-theme-contract")]
    [InlineData("packages/package-index.yml", "package-release-preparation", "package-contract")]
    [InlineData("src/Example/Example.csproj", "package-release-preparation", "package-contract")]
    [InlineData("tools/ForgeTrust.AppSurface.Release/ReleaseCommands.cs", "package-release-preparation", "release-preparation-contract")]
    public void ResourceAndPackagePaths_ShouldKeepTheirRequiredProducers(string path, string profileId, string producerId)
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        Assert.Equal(profileId, plan.Profile.Id);
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == producerId);
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "coverage");
        if (profileId != "postgresql-integration" && profileId != "keycloak-theme-integration")
        {
            Assert.Contains(plan.Profile.Producers, producer => producer.Id == "package-contract");
        }
        Assert.NotEmpty(plan.Profile.Obligations);
        Assert.All(plan.Profile.Producers, producer => Assert.InRange(producer.TimeoutSeconds, 1, 1800));
        Assert.All(plan.Profile.Resources, resource => Assert.InRange(resource.DeadlineSeconds, 1, 300));

        // Policy selection is not producer registration or a successful execution. With no host results,
        // every behavior-bearing profile must stay incomplete and ineligible.
        var missing = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, missing.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, missing.ClaimKind);
    }

    [Fact]
    public void MixedDocumentationAndCode_ShouldUseTheConservativeUnion()
    {
        var plan = Planner.ResolveForGate(
            Policy,
            [
                new NormalizedDiffPath("guides/evidencehost-cookbook.md"),
                new NormalizedDiffPath("examples/web-app/ExampleModule.cs"),
            ]);

        AssertConservativeUnion(plan);
        Assert.Contains("documentation-guides", plan.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Contains("coverage-csharp", plan.MatchedRuleIds, StringComparer.Ordinal);
    }

    [Fact]
    public void RenameAcrossDocumentationAndResourcePaths_ShouldEvaluateBothNames()
    {
        var renamed = new NormalizedDiffPath(
            "guides/evidencehost-cookbook.md",
            "renamed",
            "Durable/verify-postgresql.sh");
        var plan = Planner.ResolveForGate(Policy, [renamed]);

        AssertConservativeUnion(plan);
        Assert.Contains("documentation-guides", plan.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Contains("integration-durable-postgresql", plan.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Equal("Durable/verify-postgresql.sh", Assert.Single(plan.ChangedPaths).PreviousPath);
    }

    [Fact]
    public void DeletedCodePath_ShouldStillRequireCoverage()
    {
        var plan = Planner.ResolveForGate(
            Policy,
            [new NormalizedDiffPath("examples/web-app/ExampleModule.cs", "deleted")]);

        Assert.Equal("code-coverage", plan.Profile.Id);
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "coverage");
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "behavioral-coverage");
        Assert.Equal("deleted", Assert.Single(plan.ChangedPaths).Kind);
    }

    [Theory]
    [InlineData("Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj")]
    [InlineData("Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj")]
    [InlineData("Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.csproj")]
    public void ResourceProjectFiles_ShouldRetainPackageAndIntegrationObligations(string path)
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        AssertConservativeUnion(plan);
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "package-locked-build-and-artifacts");
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "postgresql-integration-contract");
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "keycloak-theme-rendering");
    }

    [Theory]
    [InlineData("src/client/app.ts")]
    [InlineData("src/client/app.tsx")]
    [InlineData("src/client/app.js")]
    [InlineData("src/client/app.css")]
    [InlineData("src/client/theme.scss")]
    [InlineData("Web/Pages/Index.razor")]
    [InlineData("data/migrations/0001_contract.sql")]
    public void NonDotNetBehavior_ShouldRemainFailClosedUntilAProducerCanProveIt(string path)
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        AssertConservativeUnion(plan);
        var manifest = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public void DurableSql_ShouldRequireResourceBackedDatabaseEvidence()
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath("Durable/Migrations/0001_contract.sql")]);

        Assert.Equal("postgresql-integration", plan.Profile.Id);
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "postgresql-contract");
        var manifest = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Theory]
    [InlineData(".github/workflows/build.yml")]
    [InlineData(".appsurface/evidence/evidence.policy.json")]
    [InlineData("docs/designs/issue-777-policy-driven-ci-evidence-gate.md")]
    [InlineData("docs/plans/issue-777-policy-driven-ci-evidence-gate-implementation.md")]
    [InlineData("docs/evidence-gate-policy-matrix.md")]
    [InlineData("docs/evidence-gate-rollout.md")]
    [InlineData("docs/evidence-gate-diagnostics.md")]
    [InlineData("scripts/coverage-solution.sh")]
    [InlineData("Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidencePlanner.cs")]
    [InlineData("Cli/ForgeTrust.AppSurface.Cli.Tests/AppSurfaceEvidencePolicyTests.cs")]
    [InlineData("tools/ForgeTrust.AppSurface.EvidenceGate/Program.cs")]
    [InlineData("tools/ForgeTrust.AppSurface.EvidenceGate.Tests/EvidenceGateVerifierTests.cs")]
    [InlineData("docs/fixtures/issue-777-policy-shadow/fixtures.json")]
    public void ControlPlanePaths_ShouldNeverUseNoEvidenceProfile(string path)
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath(path)]);

        AssertConservativeUnion(plan);
        Assert.NotEqual("documentation-only", plan.Profile.Id);
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "control-plane-integrity");
    }

    [Fact]
    public void UnclassifiedRepositoryPath_ShouldUseConservativeFallback()
    {
        var plan = Planner.ResolveForGate(Policy, [new NormalizedDiffPath("new-area/unrecognized-input.bin")]);

        AssertConservativeUnion(plan);
        Assert.Contains("conservative:pr-conservative", plan.MatchedRuleIds, StringComparer.Ordinal);
    }

    [Fact]
    public void CheckedInShadowFixtures_ShouldRemainCompatibleWithReviewedPolicy()
    {
        var fixtures = EvidenceCanonicalJson.Deserialize<EvidencePolicyShadowFixture[]>(
            File.ReadAllBytes(FindRepositoryFile(ShadowFixturesRelativePath)));

        var result = EvidencePolicyShadowValidator.Validate(Policy, Policy, fixtures, fixtures);

        Assert.True(result.IsCompatible);
        Assert.Equal(10, result.Selections.Count);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.DiagnosticsTruncated);
        Assert.Contains(result.Selections, selection =>
            selection.Kind == EvidencePolicyShadowFixtureKind.ControlPlane
            && selection.FixtureId == "fixture-control-plane"
            && selection.BaseProfileId == "pr-conservative");
    }

    [Fact]
    public void ConservativeProfile_ShouldPreserveEveryTargetedPrRequirement()
    {
        EvidencePlanner.ValidateGatePolicy(Policy);

        var conservative = GetProfile("pr-conservative");
        Assert.Equal(EvidenceProfileScope.Targeted, conservative.Scope);
        Assert.Equal(conservative.Producers.Count, conservative.Producers.Select(producer => producer.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(conservative.Obligations.Count, conservative.Obligations.Select(obligation => obligation.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(conservative.Resources.Count, conservative.Resources.Select(resource => resource.Id).Distinct(StringComparer.Ordinal).Count());

        var profilesById = Policy.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var targeted in Policy.Rules.Select(rule => profilesById[rule.ProfileId]).DistinctBy(profile => profile.Id))
        {
            Assert.All(targeted.Resources, required => AssertResourceIncluded(required, conservative.Resources));
            Assert.All(targeted.Producers, required => AssertProducerIncluded(required, conservative.Producers));
            Assert.All(targeted.Obligations, required => AssertObligationIncluded(required, conservative.Obligations));
        }

        var weakenedProfiles = Policy.Profiles
            .Select(profile => profile.Id == conservative.Id
                ? profile with
                {
                    Producers = profile.Producers
                        .Select(producer => producer.Id == "package-contract"
                            ? producer with { TimeoutSeconds = producer.TimeoutSeconds + 1 }
                            : producer)
                        .ToArray(),
                }
                : profile)
            .ToArray();
        var weakened = Policy with { Profiles = weakenedProfiles };
        var error = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidateGatePolicy(weakened));

        Assert.Equal("ASEVD129", error.Code);
        Assert.Contains("package-contract", error.Message, StringComparison.Ordinal);

        var weakenedReleaseProfile = Policy.Profiles
            .Select(profile => profile.Id == conservative.Id
                ? profile with
                {
                    Producers = profile.Producers
                        .Select(producer => producer.Id == "release-preparation-contract"
                            ? producer with { TimeoutSeconds = producer.TimeoutSeconds + 1 }
                            : producer)
                        .ToArray(),
                }
                : profile)
            .ToArray();
        var missingReleaseProducer = Policy with
        {
            Profiles = weakenedReleaseProfile,
        };
        var releaseError = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidateGatePolicy(missingReleaseProducer));
        Assert.Equal("ASEVD129", releaseError.Code);
        Assert.Contains("release-preparation-contract", releaseError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReleaseProfile_ShouldNotBeSelectableByAnyPrRule()
    {
        var release = GetProfile("protected-release");
        Assert.Equal(EvidenceProfileScope.Release, release.Scope);
        Assert.NotEmpty(release.Obligations);
        Assert.All(Policy.Rules, rule => Assert.Equal(EvidenceProfileScope.Targeted, GetProfile(rule.ProfileId).Scope));

        var invalidRules = Policy.Rules
            .Append(new EvidencePolicyRule("invalid-pr-release-selection", "release/**", release.Id))
            .ToArray();
        var invalidPrPolicy = Policy with { Rules = invalidRules };
        var error = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidateGatePolicy(invalidPrPolicy));

        Assert.Equal("ASEVD129", error.Code);
        Assert.Contains("non-targeted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertConservativeUnion(EvidencePlan plan)
    {
        Assert.Equal("pr-conservative", plan.Profile.Id);
        Assert.Equal(EvidenceProfileScope.Targeted, plan.Profile.Scope);
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "coverage");
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "postgresql-contract");
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "keycloak-theme-contract");
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "package-contract");
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "release-preparation-contract");
        Assert.Contains(plan.Profile.Producers, producer => producer.Id == "control-plane-validation");
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "release-preparation-evidence");
        Assert.Contains(plan.Profile.Obligations, obligation => obligation.Id == "control-plane-integrity");
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, EvidenceManifestBuilder.Build(plan, []).ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, EvidenceManifestBuilder.Build(plan, []).ClaimKind);
    }

    private static EvidenceProfile GetProfile(string id) =>
        Policy.Profiles.Single(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));

    private static void AssertResourceIncluded(
        EvidenceResourceDeclaration required,
        IReadOnlyList<EvidenceResourceDeclaration> declarations)
    {
        var actual = Assert.Single(declarations, declaration => declaration.Id == required.Id);
        Assert.Equal(required.Readiness, actual.Readiness);
        Assert.True(actual.DeadlineSeconds <= required.DeadlineSeconds);
        Assert.All(required.Requires, dependency => Assert.Contains(dependency, actual.Requires, StringComparer.Ordinal));
    }

    private static void AssertProducerIncluded(
        EvidenceProducerDeclaration required,
        IReadOnlyList<EvidenceProducerDeclaration> declarations)
    {
        var actual = Assert.Single(declarations, declaration => declaration.Id == required.Id);
        Assert.Equal(required.Kind, actual.Kind);
        Assert.Equal(required.Version, actual.Version);
        Assert.True(actual.TimeoutSeconds <= required.TimeoutSeconds);
        AssertCoverageGateIncluded(required.CoverageGate, actual.CoverageGate);
        Assert.All(required.RequiredResources, resource => Assert.Contains(resource, actual.RequiredResources, StringComparer.Ordinal));
        Assert.All(required.AssertionIds, assertion => Assert.Contains(assertion, actual.AssertionIds, StringComparer.Ordinal));

        foreach (var slot in required.ArtifactSlots)
        {
            var actualSlot = Assert.Single(actual.ArtifactSlots, candidate => candidate.LogicalName == slot.LogicalName);
            Assert.Equal(slot.RelativeRoot, actualSlot.RelativeRoot);
            Assert.Equal(slot.MediaType, actualSlot.MediaType);
            Assert.True(!slot.Required || actualSlot.Required);
            Assert.True(actualSlot.MaximumBytes <= slot.MaximumBytes);
        }
    }

    private static void AssertObligationIncluded(EvidenceObligation required, IReadOnlyList<EvidenceObligation> declarations)
    {
        var actual = Assert.Single(declarations, declaration => declaration.Id == required.Id);
        Assert.Equal(required.RiskClass, actual.RiskClass);
        Assert.Equal(required.Rationale, actual.Rationale);
        Assert.Equal(required.RequiredAssertionId, actual.RequiredAssertionId);
        Assert.All(required.RequiredProducerIds, producer => Assert.Contains(producer, actual.RequiredProducerIds, StringComparer.Ordinal));
    }

    private static void AssertCoverageGateIncluded(
        EvidenceCoverageGateRequirements? required,
        EvidenceCoverageGateRequirements? actual)
    {
        if (required is null)
        {
            return;
        }

        Assert.NotNull(actual);
        Assert.True(actual.MinLinePercent >= required.MinLinePercent);
        Assert.True(actual.MinBranchPercent >= required.MinBranchPercent);
        Assert.True(required.MinPatchLinePercent is null || actual.MinPatchLinePercent >= required.MinPatchLinePercent);
        Assert.True(required.MinPatchBranchPercent is null || actual.MinPatchBranchPercent >= required.MinPatchBranchPercent);
        Assert.Equal(required.PatchLineMode, actual.PatchLineMode, ignoreCase: true);
        Assert.True(actual.TolerancePercent <= required.TolerancePercent);
    }

    private static EvidencePolicy LoadPolicy()
    {
        return EvidenceCanonicalJson.Deserialize<EvidencePolicy>(
            File.ReadAllBytes(FindRepositoryFile(PolicyRelativePath)));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = TestPathUtils.PathUnder(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find the checked-in {relativePath} file from the test assembly directory.");
    }
}
