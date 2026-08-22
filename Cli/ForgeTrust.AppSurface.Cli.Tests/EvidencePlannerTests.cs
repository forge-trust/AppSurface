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
    public void Resolve_ShouldCanonicalizeChangedPathsAndRenameProvenance()
    {
        var plan = new EvidencePlanner().Resolve(
            CreatePolicy(),
            [
                new NormalizedDiffPath("src/Feature.cs", "renamed", "src/Previous-Z.cs"),
                new NormalizedDiffPath("docs/readme.md"),
                new NormalizedDiffPath("src/Feature.cs", "renamed", "src/Previous-A.cs"),
            ]);

        Assert.Equal("coverage", plan.Profile.Id);
        Assert.Collection(
            plan.ChangedPaths,
            path => Assert.Equal("docs/readme.md", path.Path),
            path => Assert.Equal("src/Previous-A.cs", path.PreviousPath),
            path => Assert.Equal("src/Previous-Z.cs", path.PreviousPath));
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
    public void UnifiedDiffReader_ShouldIgnoreHeaderLikeHunkContentAndAvoidDuplicateRenameEntries()
    {
        var paths = EvidenceUnifiedDiffReader.Read(
            """
            diff --git a/src/OldName.cs b/src/NewName.cs
            similarity index 90%
            rename from src/OldName.cs
            rename to src/NewName.cs
            --- a/src/OldName.cs
            +++ b/src/NewName.cs
            @@ -1,2 +1,2 @@
            --- a/not-a-file-header
            +++ b/not-a-file-header
            """);

        var path = Assert.Single(paths);
        Assert.Equal("renamed", path.Kind);
        Assert.Equal("src/NewName.cs", path.Path);
        Assert.Equal("src/OldName.cs", path.PreviousPath);
    }

    [Fact]
    public void UnifiedDiffReader_ShouldRejectHunkedNonGitDiff()
    {
        var exception = Assert.Throws<EvidencePlanningException>(() => EvidenceUnifiedDiffReader.Read(
            """
            --- src/First.cs
            +++ src/First.cs
            @@ -1 +1 @@
            -before
            +after
            --- src/Second.cs
            +++ src/Second.cs
            @@ -1 +1 @@
            -before
            +after
            """));

        Assert.Equal("ASEVD128", exception.Code);
        Assert.Contains("git-formatted", exception.Fix, StringComparison.Ordinal);
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
    public void Resolve_ShouldMatchAnEmptyTrailingWildcardSegment()
    {
        var policy = CreatePolicy() with
        {
            Rules = [new EvidencePolicyRule("prefix", "src/Feature*", "no-evidence")],
        };

        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature")]);

        Assert.Equal("no-evidence", plan.Profile.Id);
        Assert.Equal(["prefix"], plan.MatchedRuleIds);
    }

    [Fact]
    public void EvidencePlanningException_ShouldExposeTheStableDiagnosticAndRecoveryAction()
    {
        var exception = new EvidencePlanningException("ASEVD999", "Evidence planning failed.", "Correct the policy.");

        Assert.Equal("ASEVD999", exception.Code);
        Assert.Equal("Correct the policy.", exception.Fix);
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
        File.Delete(Path.Join(directory.Path, "coverage", "result.txt"));
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

    [Fact]
    public async Task CliWorkflow_ShouldDescribeExternalAndTrustedPrerequisitesForAReleaseProfile()
    {
        using var directory = TestDirectory.Create();
        var policyPath = Path.Join(directory.Path, "evidence.policy.json");
        var profile = new EvidenceProfile(
            "release",
            EvidenceProfileScope.Release,
            [],
            [
                new EvidenceProducerDeclaration("database", "postgres-integration", "1", [], ["database/assertion@1"], [], 60),
                new EvidenceProducerDeclaration("browser", "playwright-browser", "1", [], ["browser/assertion@1"], [], 60),
            ],
            [
                new EvidenceObligation("database", "database", "Database behavior changed.", ["database"], "database/assertion@1"),
                new EvidenceObligation("browser", "browser", "Browser behavior changed.", ["browser"], "browser/assertion@1"),
            ]);
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(new EvidencePolicy("release-policy", "1", "release", [profile], [])));
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var report = await workflow.DoctorAsync(new EvidencePlanningRequest(policyPath, ["src/Feature.cs"], null), CancellationToken.None);

        Assert.Contains(report.Checks, check => check.Id == "docker:database");
        Assert.Contains(report.Checks, check => check.Id == "browser:browser");
        Assert.Contains(report.Checks, check => check.Id == "trusted-envelope");
        Assert.Contains(report.Checks, check => check.NextAction is not null);
    }

    [Fact]
    public async Task CliWorkflow_ShouldRejectMissingMalformedAndMismatchedEvidenceInputs()
    {
        using var directory = TestDirectory.Create();
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var missingPolicy = Path.Join(directory.Path, "missing.policy.json");

        var missingPolicyException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.ExplainAsync(new EvidencePlanningRequest(missingPolicy, ["src/Feature.cs"], null), CancellationToken.None));
        Assert.Contains("ASEVD204", missingPolicyException.Message, StringComparison.Ordinal);

        var malformedPolicy = Path.Join(directory.Path, "malformed.policy.json");
        await File.WriteAllTextAsync(malformedPolicy, "not-json");
        var malformedPolicyException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.ExplainAsync(new EvidencePlanningRequest(malformedPolicy, ["src/Feature.cs"], null), CancellationToken.None));
        Assert.Contains("ASEVD205", malformedPolicyException.Message, StringComparison.Ordinal);

        var policyPath = Path.Join(directory.Path, "policy.json");
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(CreatePolicy()));
        var noPathException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, [], null), CancellationToken.None));
        var missingDiffException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, [], Path.Join(directory.Path, "missing.diff")), CancellationToken.None));
        Assert.Contains("ASEVD207", noPathException.Message, StringComparison.Ordinal);
        Assert.Contains("ASEVD206", missingDiffException.Message, StringComparison.Ordinal);

        var diffPath = Path.Join(directory.Path, "change.diff");
        await File.WriteAllTextAsync(diffPath, "--- a/docs/Old.md\n+++ b/docs/New.md\n");
        var diffPlan = await workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, [], diffPath), CancellationToken.None);
        Assert.Equal("no-evidence", diffPlan.Profile.Id);
        Assert.Equal("docs/New.md", Assert.Single(diffPlan.ChangedPaths).Path);

        var plan = await workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, ["docs/readme.md"], null), CancellationToken.None);
        var output = Path.Join(directory.Path, "output");
        await workflow.WritePlanAsync(plan, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(plan, []), output, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Join(output, "evidence-manifest.json"), "not-json");

        var malformedManifestException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.VerifyAsync(Path.Join(output, "evidence-plan.json"), Path.Join(output, "evidence-manifest.json"), CancellationToken.None));
        Assert.Contains("ASEVD209", malformedManifestException.Message, StringComparison.Ordinal);

        var missingEvidenceException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.VerifyAsync(Path.Join(output, "missing-plan.json"), Path.Join(output, "missing-manifest.json"), CancellationToken.None));
        Assert.Contains("ASEVD208", missingEvidenceException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWorkflow_ShouldPermitIntentionalReplacementOfItsOwnStarterAndRejectTamperedPlans()
    {
        using var directory = TestDirectory.Create();
        var root = Path.Join(directory.Path, "evidence");
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        await workflow.InitializeAsync(root, force: false, CancellationToken.None);
        var replacement = await workflow.InitializeAsync(root, force: true, CancellationToken.None);
        Assert.Equal(3, replacement.CreatedFiles.Count);

        var policyPath = Path.Join(root, "evidence.policy.json");
        var plan = await workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, ["docs/readme.md"], null), CancellationToken.None);
        var output = Path.Join(directory.Path, "output");
        await workflow.WritePlanAsync(plan with { PolicySnapshot = null }, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(plan, []), output, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.VerifyAsync(Path.Join(output, "evidence-plan.json"), Path.Join(output, "evidence-manifest.json"), CancellationToken.None));
        Assert.Contains("ASEVD203", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWorkflow_ShouldRejectUnresolvableSnapshotsAndMismatchedPlanBindings()
    {
        using var directory = TestDirectory.Create();
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var policyPath = Path.Join(directory.Path, "policy.json");
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(CreatePolicy()));
        var plan = await workflow.ExplainAsync(new EvidencePlanningRequest(policyPath, ["docs/readme.md"], null), CancellationToken.None);
        var output = Path.Join(directory.Path, "output");

        var unresolvable = plan with { PolicySnapshot = new EvidencePolicy("invalid", "1", "missing", [], []) };
        await workflow.WritePlanAsync(unresolvable, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(unresolvable, []), output, CancellationToken.None);
        var snapshotException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.VerifyAsync(Path.Join(output, "evidence-plan.json"), Path.Join(output, "evidence-manifest.json"), CancellationToken.None));
        Assert.Contains("ASEVD203", snapshotException.Message, StringComparison.Ordinal);

        await workflow.WritePlanAsync(plan with { PlanDigest = "tampered-plan-digest" }, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(plan, []), output, CancellationToken.None);
        var bindingException = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.VerifyAsync(Path.Join(output, "evidence-plan.json"), Path.Join(output, "evidence-manifest.json"), CancellationToken.None));
        Assert.Contains("ASEVD203", bindingException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_ShouldRejectUntruthfulOrUnboundedPolicyDeclarations()
    {
        var planner = new EvidencePlanner();

        AssertPlanningCode(() => planner.Resolve(CreatePolicy(), []), "ASEVD101");
        AssertPlanningCode(() => planner.Resolve(CreatePolicy(), [new NormalizedDiffPath("./src/Feature.cs")]), "ASEVD120");
        AssertPlanningCode(() => EvidencePlanner.ValidatePolicy(CreatePolicy() with { Profiles = [] }), "ASEVD102");
        AssertPlanningCode(() => EvidencePlanner.ValidatePolicy(CreatePolicy() with { ConservativeProfileId = "missing" }), "ASEVD104");

        var invalidResource = CreateCoverageProfile(EvidenceProfileScope.Targeted) with
        {
            Resources = [new EvidenceResourceDeclaration("postgres", "unknown", 1, [])],
        };
        AssertPlanningCode(() => EvidencePlanner.ValidatePolicy(new EvidencePolicy("policy", "1", "coverage", [invalidResource], [])), "ASEVD123");

        var invalidProducer = CreateCoverageProfile(EvidenceProfileScope.Targeted) with
        {
            Producers =
            [
                new EvidenceProducerDeclaration(
                    "coverage",
                    "coverage",
                    "1",
                    [],
                    ["appsurface/coverage/behavioral-patch@1"],
                    [new EvidenceArtifactSlot("report", "../outside", "text/plain", false, 1)],
                    0,
                    new EvidenceCoverageGateRequirements(101, 85)),
            ],
        };
        AssertPlanningCode(() => EvidencePlanner.ValidatePolicy(new EvidencePolicy("policy", "1", "coverage", [invalidProducer], [])), "ASEVD110");

        var invalidObligation = CreateCoverageProfile(EvidenceProfileScope.Targeted) with
        {
            Obligations = [new EvidenceObligation("behavior", "risk", "why", [], "missing")],
        };
        AssertPlanningCode(() => EvidencePlanner.ValidatePolicy(new EvidencePolicy("policy", "1", "coverage", [invalidObligation], [])), "ASEVD113");
    }

    [Fact]
    public void Planner_ShouldRejectAnUnsupportedCoveragePatchLineMode()
    {
        var validProfile = CreateCoverageProfile(EvidenceProfileScope.Targeted);
        var policy = CreatePolicy() with
        {
            Profiles =
            [
                validProfile with
                {
                    Producers = [validProfile.Producers[0] with { CoverageGate = new EvidenceCoverageGateRequirements(95, 85, PatchLineMode: "unsupported") }],
                },
            ],
        };

        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));

        Assert.Equal("ASEVD127", exception.Code);
        Assert.Contains("patchLineMode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactWriter_ShouldRejectUndeclaredDuplicateAndOversizedWrites()
    {
        using var directory = TestDirectory.Create();
        var producer = new EvidenceProducerDeclaration(
            "coverage",
            "coverage",
            "1",
            [],
            [],
            [
                new EvidenceArtifactSlot("report", "coverage", "text/plain", false, 1),
                new EvidenceArtifactSlot("summary", "coverage", "text/plain", false, 1),
            ],
            1);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("missing", "coverage/report.txt", Array.Empty<byte>()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("report", "coverage/report.txt", "too-large"u8.ToArray()).AsTask());
        await writer.WriteAsync("report", "coverage/report.txt", "x"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("report", "coverage/again.txt", "x"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("summary", "coverage/report.txt", "x"u8.ToArray()).AsTask());

        Assert.False(EvidenceArtifactValidation.AreValid(producer, [new EvidenceArtifactResult("report", "outside.txt", "text/plain", 1, new string('a', 64))]));
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.NormalizeRelativePath("coverage/../outside.txt"));
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.NormalizeRelativePath("coverage/."));
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.NormalizeRelativePath("coverage/.."));
    }

    [Fact]
    public async Task ArtifactWriter_ShouldReleaseItsReservationWhenWritingFails()
    {
        using var directory = TestDirectory.Create();
        var blockedRoot = Path.Join(directory.Path, "artifact-root");
        await File.WriteAllTextAsync(blockedRoot, "not-a-directory");
        var producer = new EvidenceProducerDeclaration(
            "coverage",
            "coverage",
            "1",
            [],
            [],
            [new EvidenceArtifactSlot("report", "coverage", "text/plain", false, 16)],
            1);
        var writer = new EvidenceArtifactWriter(producer, blockedRoot);

        await Assert.ThrowsAnyAsync<IOException>(() => writer.WriteAsync("report", "coverage/report.txt", "first"u8.ToArray()).AsTask());

        File.Delete(blockedRoot);
        Directory.CreateDirectory(blockedRoot);
        var artifact = await writer.WriteAsync("report", "coverage/report.txt", "retry"u8.ToArray());

        Assert.Equal("report", artifact.LogicalName);
        Assert.Equal([artifact], writer.WrittenArtifacts);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public void Planner_ShouldFailClosedForEveryConnectedPolicyDeclaration()
    {
        var validProfile = CreateCoverageProfile(EvidenceProfileScope.Targeted);

        AssertPolicyCode(CreatePolicy() with { Profiles = [validProfile, validProfile] }, "ASEVD103");
        AssertPolicyCode(CreatePolicy() with { Rules = [new EvidencePolicyRule("unknown", "src/**", "missing")] }, "ASEVD106");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Resources = Enumerable.Range(0, 17).Select(index => new EvidenceResourceDeclaration($"resource-{index}", "aspire_health", 1, [])).ToArray() }],
        }, "ASEVD122");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Producers = [validProfile.Producers[0], validProfile.Producers[0]] }],
        }, "ASEVD107");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Resources = [new EvidenceResourceDeclaration("resource", "aspire_health", 1, []), new EvidenceResourceDeclaration("resource", "aspire_health", 1, [])] }],
        }, "ASEVD108");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Resources = [new EvidenceResourceDeclaration("resource", "aspire_health", 0, [])] }],
        }, "ASEVD109");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Resources = [new EvidenceResourceDeclaration("resource", "completion", 1, ["missing"])] }],
        }, "ASEVD124");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Producers = [validProfile.Producers[0] with { TimeoutSeconds = 0 }] }],
        }, "ASEVD110");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with
            {
                Producers = [validProfile.Producers[0] with { ArtifactSlots = [new EvidenceArtifactSlot("slot", "artifacts", "text/plain", false, 1), new EvidenceArtifactSlot("slot", "artifacts", "text/plain", false, 1)] }],
            }],
        }, "ASEVD125");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with
            {
                Producers = [validProfile.Producers[0] with { ArtifactSlots = [new EvidenceArtifactSlot("slot", "../outside", "", false, -1)] }],
            }],
        }, "ASEVD126");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with
            {
                Producers = [validProfile.Producers[0] with { ArtifactSlots = [new EvidenceArtifactSlot("slot", "../outside", "text/plain", false, 1)] }],
            }],
        }, "ASEVD126");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with
            {
                Producers = [validProfile.Producers[0] with { RequiredResources = ["missing"] }],
            }],
        }, "ASEVD111");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Obligations = [validProfile.Obligations[0], validProfile.Obligations[0]] }],
        }, "ASEVD112");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Obligations = [validProfile.Obligations[0] with { RequiredProducerIds = ["missing"] }] }],
        }, "ASEVD114");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Obligations = [validProfile.Obligations[0] with { RequiredAssertionId = "missing/assertion@1" }] }],
        }, "ASEVD115");
        AssertPolicyCode(new EvidencePolicy("policy", "1", "coverage", [validProfile, new EvidenceProfile("release-empty", EvidenceProfileScope.Release, [], [], [])], []), "ASEVD116");
        AssertPolicyCode(CreatePolicy() with { Id = new string('x', 129) }, "ASEVD121");
        AssertPolicyCode(CreatePolicy() with
        {
            Profiles = [validProfile with { Producers = [validProfile.Producers[0] with { CoverageGate = new EvidenceCoverageGateRequirements(-1, 101, PatchLineMode: "unknown") }] }],
        }, "ASEVD127");
    }

    [Fact]
    public void Planner_ShouldAcceptExplicitlyDeclaredResourceDependencies()
    {
        var profile = CreateCoverageProfile(EvidenceProfileScope.Targeted) with
        {
            Resources =
            [
                new EvidenceResourceDeclaration("database", "aspire_health", 1, []),
                new EvidenceResourceDeclaration("migrations", "completion", 1, ["database"]),
            ],
            Producers = [CreateCoverageProfile(EvidenceProfileScope.Targeted).Producers[0] with { RequiredResources = ["migrations"] }],
        };
        var policy = CreatePolicy() with { Profiles = [CreateNoEvidenceProfile(), profile] };

        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature.cs")]);

        Assert.Equal(["database", "migrations"], plan.Profile.Resources.Select(resource => resource.Id));
        Assert.Equal(["migrations"], Assert.Single(plan.Profile.Producers).RequiredResources);
    }

    [Fact]
    public void Planner_ShouldKeepRenamesAndUnmatchedGlobsConservativeAndStable()
    {
        var planner = new EvidencePlanner();
        var renamed = planner.Resolve(CreatePolicy(), [new NormalizedDiffPath("docs/readme.md", "renamed", "src/Feature.cs")]);

        Assert.Equal("coverage", renamed.Profile.Id);
        Assert.Contains("docs", renamed.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Contains("conservative:coverage", renamed.MatchedRuleIds, StringComparer.Ordinal);
        Assert.Equal("renamed", Assert.Single(renamed.ChangedPaths).Kind);

        var whitespaceException = Assert.Throws<EvidencePlanningException>(() => planner.Resolve(CreatePolicy(), [new NormalizedDiffPath(" src/Feature.cs ")]));
        var emptyException = Assert.Throws<EvidencePlanningException>(() => planner.Resolve(CreatePolicy(), [new NormalizedDiffPath(" ")]));
        Assert.Contains("ASEVD118", whitespaceException.Message, StringComparison.Ordinal);
        Assert.Contains("ASEVD119", emptyException.Message, StringComparison.Ordinal);

        var noMatch = planner.Resolve(CreatePolicy() with { Rules = [new EvidencePolicyRule("nonmatch", "docs/*z", "no-evidence")] }, [new NormalizedDiffPath("docs/readme.md")]);
        Assert.Equal("coverage", noMatch.Profile.Id);
        Assert.Empty(EvidenceUnifiedDiffReader.Read("--- /dev/null\n+++ /dev/null\n"));
    }

    [Fact]
    public void Contracts_ShouldFailClosedForInvalidArtifactMetadataAndManifestBindings()
    {
        var producer = new EvidenceProducerDeclaration(
            "coverage",
            "coverage",
            "1",
            [],
            ["coverage/assertion@1"],
            [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: EvidenceArtifactWriter.MaximumTotalArtifactBytes + 1)],
            1);
        var validArtifact = new EvidenceArtifactResult("report", "coverage/report.txt", "text/plain", 1, new string('a', 64));

        Assert.False(EvidenceArtifactValidation.AreValid(producer with { ArtifactSlots = [producer.ArtifactSlots[0], producer.ArtifactSlots[0]] }, [validArtifact]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [validArtifact, validArtifact with { LogicalName = "second" }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [validArtifact with { MediaType = "application/json" }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [validArtifact with { LengthBytes = EvidenceArtifactWriter.MaximumTotalArtifactBytes + 1 }]));
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.NormalizeRelativePath(" "));

        var plan = new EvidencePlanner().Resolve(CreatePolicy(), [new NormalizedDiffPath("src/Feature.cs")]);
        var validResult = new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["appsurface/coverage/behavioral-patch@1"]);
        var duplicateResult = EvidenceManifestBuilder.Build(plan, [validResult, validResult]);
        var unexpectedProducer = EvidenceManifestBuilder.Build(plan, [validResult, new EvidenceProducerResult("unexpected", EvidenceProducerOutcome.Passed, [])]);
        var unknownResource = EvidenceManifestBuilder.Build(plan, [validResult], resourceResults: [new EvidenceResourceResult("unexpected", EvidenceResourceOutcome.Ready, 0)]);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, duplicateResult.ExecutionVerdict);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, unexpectedProducer.ExecutionVerdict);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, unknownResource.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(plan with { PolicySnapshot = null }, EvidenceManifestBuilder.Build(plan, [validResult])));
        Assert.False(EvidenceManifestBuilder.Verify(plan, EvidenceManifestBuilder.Build(plan, [validResult]) with { ManifestDigest = "tampered" }));
    }

    [Fact]
    public void CliWorkflow_Summaries_ShouldExplainTargetedObservationAndReleaseClaims()
    {
        var targetPlan = new EvidencePlanner().Resolve(CreatePolicy(), [new NormalizedDiffPath("src/Feature.cs")]);
        var targetResult = new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["appsurface/coverage/behavioral-patch@1"]);
        var targeted = EvidenceManifestBuilder.Build(targetPlan, [targetResult]);
        var observation = EvidenceManifestBuilder.Build(targetPlan, [targetResult], observationOnly: true);
        var releasePolicy = CreatePolicy() with
        {
            Profiles = [CreateCoverageProfile(EvidenceProfileScope.Release)],
            Rules = [],
        };
        var releasePlan = new EvidencePlanner().Resolve(releasePolicy, [new NormalizedDiffPath("src/Feature.cs")]);
        var release = EvidenceManifestBuilder.Build(releasePlan, [targetResult], envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested);

        Assert.Contains("Required producers: coverage", EvidenceCliWorkflow.FormatSummary(targetPlan), StringComparison.Ordinal);
        Assert.Contains("Targeted evidence is complete", EvidenceCliWorkflow.FormatSummary(targeted), StringComparison.Ordinal);
        Assert.Contains("Observation recorded", EvidenceCliWorkflow.FormatSummary(observation), StringComparison.Ordinal);
        Assert.Contains("Release evidence is complete", EvidenceCliWorkflow.FormatSummary(release), StringComparison.Ordinal);
    }

    [Fact]
    public void CliWorkflow_Diagnostics_ShouldExposeStableCodeAndRecoveryAction()
    {
        var diagnostic = new EvidenceCliException("ASEVD999", "Evidence input is invalid.", "Correct the input and rerun.");

        Assert.Equal("ASEVD999", diagnostic.Code);
        Assert.Equal("Correct the input and rerun.", diagnostic.Fix);
    }

    private static void AssertPolicyCode(EvidencePolicy policy, string code)
    {
        if (!string.Equals(code, "ASEVD106", StringComparison.Ordinal))
        {
            policy = policy with { Rules = [] };
        }

        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(policy));
        Assert.Contains(code, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertPlanningCode(Action action, string code)
    {
        var exception = Assert.Throws<EvidencePlanningException>(action);

        Assert.Equal(code, exception.Code);
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
