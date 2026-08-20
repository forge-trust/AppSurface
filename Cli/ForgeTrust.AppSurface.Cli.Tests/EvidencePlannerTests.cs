using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidencePlannerTests
{
    [Fact]
    public void Resolve_ShouldSelectExplicitNoEvidenceProfileForDocumentationPath()
    {
        var planner = new EvidencePlanner();

        var plan = planner.Resolve(CreatePolicy(), [new NormalizedDiffPath("docs/readme.md")]);
        var manifest = EvidenceManifestBuilder.Build(plan, []);

        Assert.Equal("no-evidence", plan.Profile.Id);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Fact]
    public void Resolve_ShouldUseConservativeCoverageProfileForUnknownPath()
    {
        var plan = new EvidencePlanner().Resolve(CreatePolicy(), [new NormalizedDiffPath("src/Feature.cs")]);

        Assert.Equal("coverage", plan.Profile.Id);
        Assert.Contains("conservative:coverage", plan.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Single(plan.Profile.Obligations);
    }

    [Fact]
    public void Resolve_ShouldUseExplicitPrecedenceForOverlappingRules()
    {
        var policy = CreatePolicy() with
        {
            Rules =
            [
                new EvidencePolicyRule("docs", "docs/*", "no-evidence"),
                new EvidencePolicyRule("docs-specific", "docs/*", "coverage", 1),
            ],
        };

        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("docs/readme.md")]);

        Assert.Equal("coverage", plan.Profile.Id);
        Assert.Equal(["docs-specific"], plan.MatchedRuleIds);
    }

    [Fact]
    public void Resolve_ShouldRejectAmbiguousOverlappingProfiles()
    {
        var policy = CreatePolicy() with
        {
            Rules =
            [
                new EvidencePolicyRule("docs", "docs/*", "no-evidence"),
                new EvidencePolicyRule("docs-coverage", "docs/*", "coverage"),
            ],
        };

        var exception = Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("docs/readme.md")]));

        Assert.Contains("ASEVD117", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ShouldRejectEmptyConservativeProfile()
    {
        var policy = CreatePolicy() with { ConservativeProfileId = "no-evidence" };

        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));

        Assert.Contains("ASEVD105", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedDiffReader_ShouldReadAddedDeletedAndModifiedPaths()
    {
        var paths = EvidenceUnifiedDiffReader.Read(
            """
            --- /dev/null
            +++ b/src/New.cs
            --- a/src/Old.cs
            +++ /dev/null
            --- a/docs/Guide.md
            +++ b/docs/Guide.md
            """);

        Assert.Collection(
            paths,
            path =>
            {
                Assert.Equal("docs/Guide.md", path.Path);
                Assert.Equal("modified", path.Kind);
            },
            path =>
            {
                Assert.Equal("src/New.cs", path.Path);
                Assert.Equal("added", path.Kind);
            },
            path =>
            {
                Assert.Equal("src/Old.cs", path.Path);
                Assert.Equal("deleted", path.Kind);
            });
    }

    [Fact]
    public void UnifiedDiffReader_ShouldReadPureRenamePaths()
    {
        var path = Assert.Single(EvidenceUnifiedDiffReader.Read(
            """
            similarity index 100%
            rename from src/OldName.cs
            rename to src/NewName.cs
            """));

        Assert.Equal("renamed", path.Kind);
        Assert.Equal("src/NewName.cs", path.Path);
        Assert.Equal("src/OldName.cs", path.PreviousPath);
    }

    [Fact]
    public void Resolve_ShouldRejectSameLiteralSegmentSpecificityRatherThanChoosingLongerWildcardText()
    {
        var policy = CreatePolicy() with
        {
            Rules =
            [
                new EvidencePolicyRule("long-wildcard", "src/very-long-*", "coverage"),
                new EvidencePolicyRule("extension-wildcard", "src/*.cs", "no-evidence"),
            ],
        };

        var exception = Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/very-long-feature.cs")]));

        Assert.Contains("ASEVD117", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestBuilder_ShouldRejectReleaseClaimWithoutValidatedEnvelope()
    {
        var release = CreatePolicy() with
        {
            Profiles = [CreateCoverageProfile(EvidenceProfileScope.Release)],
            ConservativeProfileId = "coverage",
            Rules = [],
        };
        var plan = new EvidencePlanner().Resolve(release, [new NormalizedDiffPath("src/Feature.cs")]);
        var result = new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["appsurface/coverage/behavioral-patch@1"]);

        var incomplete = EvidenceManifestBuilder.Build(plan, [result]);
        var complete = EvidenceManifestBuilder.Build(plan, [result], envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, incomplete.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, incomplete.ClaimKind);
        Assert.Equal(EvidenceClaimKind.ReleaseComplete, complete.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.ReleaseGate, complete.Eligibility);
    }

    [Fact]
    public void ManifestBuilder_ShouldRejectClaimTamperingEvenWhenDigestIsRecomputed()
    {
        var plan = new EvidencePlanner().Resolve(CreatePolicy(), [new NormalizedDiffPath("docs/readme.md")]);
        var manifest = EvidenceManifestBuilder.Build(plan, []);
        var tampered = manifest with { ClaimKind = EvidenceClaimKind.TargetedComplete, Eligibility = EvidenceClaimEligibility.PullRequestGate, ManifestDigest = string.Empty };
        tampered = tampered with { ManifestDigest = EvidenceDigest.CanonicalSha256(tampered) };

        Assert.False(EvidenceManifestBuilder.Verify(plan, tampered));
    }

    [Fact]
    public void ManifestBuilder_ShouldInvalidatePassedProducerThatOmitsRequiredArtifact()
    {
        var profile = new EvidenceProfile(
            "artifact-coverage",
            EvidenceProfileScope.Targeted,
            [],
            [new EvidenceProducerDeclaration(
                "coverage",
                "coverage",
                "1.0.0",
                [],
                ["coverage/assertion@1"],
                [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 128)],
                60)],
            [new EvidenceObligation("coverage", "behavior", "Coverage report is required.", ["coverage"], "coverage/assertion@1")]);
        var policy = new EvidencePolicy("artifact", "1", "artifact-coverage", [profile], []);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature.cs")]);

        var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["coverage/assertion@1"])]);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldAllowOnlyDeclaredContainedArtifacts()
    {
        using var directory = TestDirectory.Create();
        var producer = new EvidenceProducerDeclaration(
            "coverage",
            "coverage",
            "1.0.0",
            [],
            [],
            [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 16)],
            60);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync("report", "../escape.txt", "bad"u8.ToArray()).AsTask());
        var artifact = await writer.WriteAsync("report", "coverage/result.txt", "ok"u8.ToArray());

        Assert.Equal("coverage/result.txt", artifact.RelativePath);
        Assert.True(EvidenceArtifactValidation.AreValid(producer, writer.WrittenArtifacts));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
        await File.WriteAllTextAsync(Path.Join(directory.Path, "coverage", "result.txt"), "changed");
        Assert.False(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task CliWorkflow_ShouldCreateMarkedStarterAndVerifyGeneratedNoEvidenceManifest()
    {
        using var directory = TestDirectory.Create();
        var root = Path.Join(directory.Path, ".appsurface", "evidence");
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var initialization = await workflow.InitializeAsync(root, force: false, CancellationToken.None);
        var plan = await workflow.ExplainAsync(
            new EvidencePlanningRequest(Path.Join(root, "evidence.policy.json"), ["docs/readme.md"], null),
            CancellationToken.None);
        var output = Path.Join(directory.Path, "TestResults", "evidence");
        await workflow.WritePlanAsync(plan, output, CancellationToken.None);
        var manifest = EvidenceManifestBuilder.Build(plan, []);
        await workflow.WriteManifestAsync(manifest, output, CancellationToken.None);
        var verified = await workflow.VerifyAsync(Path.Join(output, "evidence-plan.json"), Path.Join(output, "evidence-manifest.json"), CancellationToken.None);

        Assert.Equal(3, initialization.CreatedFiles.Count);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, verified.Manifest.ClaimKind);
        Assert.Contains("explicit no-evidence", EvidenceCliWorkflow.FormatSummary(plan), StringComparison.Ordinal);
        Assert.Contains("No evidence is required", EvidenceCliWorkflow.FormatSummary(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWorkflow_ShouldRefuseToOverwriteUnmarkedStarterFile()
    {
        using var directory = TestDirectory.Create();
        var root = Path.Join(directory.Path, "evidence");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Join(root, "README.md"), "consumer documentation");
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.InitializeAsync(root, force: true, CancellationToken.None));

        Assert.Contains("ASEVD201", exception.Message, StringComparison.Ordinal);
    }

    private static EvidencePolicy CreatePolicy() => new(
        "sample",
        "1",
        "coverage",
        [CreateNoEvidenceProfile(), CreateCoverageProfile(EvidenceProfileScope.Targeted)],
        [new EvidencePolicyRule("docs", "docs/**", "no-evidence")]);

    private static EvidenceProfile CreateNoEvidenceProfile() => new("no-evidence", EvidenceProfileScope.Targeted, [], [], []);

    private static EvidenceProfile CreateCoverageProfile(EvidenceProfileScope scope) => new(
        "coverage",
        scope,
        [],
        [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], ["appsurface/coverage/behavioral-patch@1"], [], 60)],
        [new EvidenceObligation("behavior", "behavior", "Changed behavior needs evidence.", ["coverage"], "appsurface/coverage/behavioral-patch@1")]);
}
