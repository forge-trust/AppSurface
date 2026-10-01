using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidencePullRequestGateVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ShouldAcceptCompleteRevisionBoundEvidenceAndReturnTrustedSummary()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var result = await fixture.VerifyAsync();

        Assert.True(result.IsEligible);
        Assert.Equal("ASEVG000", result.Code);
        Assert.NotNull(result.Summary);
        Assert.Equal("targeted", result.Summary.ProfileId);
        Assert.Equal("matched-policy-rules", result.Summary.SelectionRationale);
        Assert.Equal(["compile"], result.Summary.SelectedObligationIds);
        Assert.Equal(["compile"], result.Summary.ClosedObligationIds);
        Assert.Empty(result.Summary.MissingObligationIds);
        Assert.Equal("source", Assert.Single(result.Summary.MatchedRules).Id);
        Assert.Equal(fixture.Plan.BaseRevision![..12], result.Summary.BaseRevisionShort);
        Assert.Equal(fixture.Plan.HeadRevision![..12], result.Summary.HeadRevisionShort);
        Assert.Equal("compile", Assert.Single(result.Summary.ObligationRationales).Id);
    }

    [Fact]
    public async Task VerifyAsync_ShouldAcceptExplicitEmptyDocumentationClaimWithoutArtifactHandoff()
    {
        await using var fixture = await GateFixture.CreateAsync(documentationOnly: true);

        var result = await fixture.VerifyAsync(
            trustedArtifactHandoffRootPath: null,
            artifactVerifier: null,
            omitArtifactRoot: true,
            useFakeArtifactVerifier: false);

        Assert.True(result.IsEligible);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, fixture.Manifest.ClaimKind);
        Assert.NotNull(result.Summary);
        Assert.Equal("docs", result.Summary.ProfileId);
        Assert.Empty(result.Summary.SelectedObligationIds);
        Assert.Empty(result.Summary.ClosedObligationIds);
        Assert.Empty(result.Summary.MissingObligationIds);
    }

    [Fact]
    public async Task VerifyAsync_ShouldFailClosedWhenNonemptyProfileHasNoTrustedArtifactRoot()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var result = await fixture.VerifyAsync(
            trustedArtifactHandoffRootPath: null,
            artifactVerifier: null,
            omitArtifactRoot: true,
            useFakeArtifactVerifier: false);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG009", result.Code);
        Assert.NotNull(result.Summary);
        Assert.Empty(result.Summary.ClosedObligationIds);
        Assert.Equal(["compile"], result.Summary.MissingObligationIds);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRequireDeclaredAndIndependentlyAttestedIsolationForNonemptyEvidence()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var missingManifestEnvelope = EvidenceManifestBuilder.Build(
            fixture.Plan,
            fixture.Manifest.ProducerResults);

        var missingEnvelopeResult = await fixture.VerifyAsync(manifest: missingManifestEnvelope);
        var missingAuthorityResult = await fixture.VerifyAsync(authority: fixture.Authority with { SubjectEnvelopeAttested = false });

        Assert.Equal("ASEVG004", missingEnvelopeResult.Code);
        Assert.Equal("ASEVG007", missingAuthorityResult.Code);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectForgeryEvenWhenPlanAndManifestDigestsAreRecomputed()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var forgedPlan = fixture.Plan with { MatchedRuleIds = ["forged-rule"], PlanDigest = string.Empty };
        forgedPlan = forgedPlan with { PlanDigest = EvidenceDigest.CanonicalSha256(forgedPlan) };
        var forgedManifest = EvidenceManifestBuilder.Build(forgedPlan, fixture.Manifest.ProducerResults);

        var result = await fixture.VerifyAsync(plan: forgedPlan, manifest: forgedManifest);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG003", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectLegacyV1Plan()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var legacyPlan = fixture.Plan with { ContractVersion = "1.0", PlanDigest = string.Empty };
        legacyPlan = legacyPlan with { PlanDigest = EvidenceDigest.CanonicalSha256(legacyPlan) };

        var result = await fixture.VerifyAsync(plan: legacyPlan);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG002", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectTrustedPolicySubstitution()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var result = await fixture.VerifyAsync(policy: fixture.Policy with { Id = "other-policy" });

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG003", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectExpectedRunMismatchBeforeReturningSummary()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var result = await fixture.VerifyAsync(expectedIdentity: fixture.ExpectedIdentity with
        {
            RunIdentity = fixture.ExpectedIdentity.RunIdentity with { WorkflowRunAttempt = 2 },
        });

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG007", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectManifestRunIdentityMismatch()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var mismatched = fixture.Manifest with
        {
            PullRequestRunIdentity = fixture.ExpectedIdentity.RunIdentity with { PullRequestNumber = 778 },
            ManifestDigest = string.Empty,
        };
        mismatched = mismatched with { ManifestDigest = EvidenceDigest.CanonicalSha256(mismatched) };

        var result = await fixture.VerifyAsync(manifest: mismatched);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG004", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectUnavailableOrMismatchedCurrentAuthority()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var unavailable = await fixture.VerifyAsync(authorityProvider: new FakeAuthorityProvider(null));
        var movedBase = await fixture.VerifyAsync(authority: fixture.Authority with { BaseRevision = new string('a', 40) });
        var movedHead = await fixture.VerifyAsync(authority: fixture.Authority with { HeadRevision = new string('b', 40) });
        var wrongJob = await fixture.VerifyAsync(authority: fixture.Authority with { SubjectJobId = "other-job" });
        var wrongCheckout = await fixture.VerifyAsync(authority: fixture.Authority with { SubjectJobHeadRevision = new string('c', 40) });
        var failedJob = await fixture.VerifyAsync(authority: fixture.Authority with { SubjectJobConclusion = "failure" });

        Assert.Equal("ASEVG006", unavailable.Code);
        Assert.All([movedBase, movedHead, wrongJob, wrongCheckout, failedJob], result => Assert.Equal("ASEVG007", result.Code));
        Assert.All([unavailable, movedBase, movedHead, wrongJob, wrongCheckout, failedJob], result => Assert.NotNull(result.Summary));
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectObservationOnlyAndCleanupFailure()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var observation = EvidenceManifestBuilder.Build(
            fixture.Plan,
            fixture.Manifest.ProducerResults,
            observationOnly: true);
        var cleanupFailure = EvidenceManifestBuilder.Build(
            fixture.Plan,
            fixture.Manifest.ProducerResults,
            metrics: new EvidenceExecutionMetrics(CleanupCompleted: false));
        var artifactVerifier = new FakeArtifactVerifier(true);

        var observationResult = await fixture.VerifyAsync(manifest: observation, artifactVerifier: artifactVerifier);
        var cleanupResult = await fixture.VerifyAsync(manifest: cleanupFailure, artifactVerifier: artifactVerifier);

        Assert.Equal("ASEVG005", observationResult.Code);
        Assert.Equal("ASEVG004", cleanupResult.Code);
        Assert.False(observationResult.IsEligible);
        Assert.False(cleanupResult.IsEligible);
    }

    [Fact]
    public async Task VerifyAsync_ShouldReportMissingObligationFromVerifiedPlanForIncompleteManifest()
    {
        await using var fixture = await GateFixture.CreateAsync(includeRequiredAssertion: false, includeArtifact: false);
        var incompleteManifest = EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults);

        var result = await fixture.VerifyAsync(manifest: incompleteManifest, artifactVerifier: new FakeArtifactVerifier(true));

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG004", result.Code);
        Assert.NotNull(result.Summary);
        Assert.Equal(["compile"], result.Summary.SelectedObligationIds);
        Assert.Empty(result.Summary.ClosedObligationIds);
        Assert.Equal(["compile"], result.Summary.MissingObligationIds);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectReleaseProfileSelectedForPullRequest()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var releasePlan = fixture.Plan with
        {
            Profile = fixture.Plan.Profile with { Scope = EvidenceProfileScope.Release },
            PlanDigest = string.Empty,
        };
        releasePlan = releasePlan with { PlanDigest = EvidenceDigest.CanonicalSha256(releasePlan) };
        var releaseManifest = EvidenceManifestBuilder.Build(releasePlan, fixture.Manifest.ProducerResults);

        var result = await fixture.VerifyAsync(plan: releasePlan, manifest: releaseManifest);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG003", result.Code);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectForgedDigestMissingFileAndSymlinkOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();

        var valid = await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest);
        Assert.True(valid);

        EvidenceArtifactResult artifact = fixture.Manifest.ProducerResults.Single().Artifacts!.Single();
        var forged = ReplaceArtifact(fixture.Manifest, artifact with { Sha256 = new string('0', 64) });
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, forged));

        File.Delete(fixture.ArtifactPath);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));

        var symlinkTarget = Path.Join(fixture.ArtifactRoot, "symlink-target.txt");
        File.WriteAllText(symlinkTarget, "artifact bytes");
        File.CreateSymbolicLink(fixture.ArtifactPath, symlinkTarget);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectRecomputedManifestWithForgedArtifactHash()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        EvidenceArtifactResult artifact = fixture.Manifest.ProducerResults.Single().Artifacts!.Single();
        var forgedManifest = ReplaceArtifact(fixture.Manifest, artifact with { Sha256 = new string('0', 64) });

        var result = await fixture.VerifyAsync(
            manifest: forgedManifest,
            artifactVerifier: new EvidencePullRequestGateNoFollowArtifactVerifier());

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG010", result.Code);
        Assert.NotNull(result.Summary);
        Assert.Empty(result.Summary.ClosedObligationIds);
        Assert.Equal(["compile"], result.Summary.MissingObligationIds);
    }

    private static EvidenceManifest ReplaceArtifact(EvidenceManifest manifest, EvidenceArtifactResult replacement)
    {
        var producer = Assert.Single(manifest.ProducerResults);
        var updated = manifest with
        {
            ProducerResults = [producer with { Artifacts = [replacement] }],
            ManifestDigest = string.Empty,
        };
        return updated with { ManifestDigest = EvidenceDigest.CanonicalSha256(updated) };
    }

    private sealed class FakeArtifactVerifier(bool result) : IEvidencePullRequestGateArtifactVerifier
    {
        public Task<bool> VerifyArtifactsAsync(
            string trustedArtifactHandoffRootPath,
            EvidencePlan verifiedPlan,
            EvidenceManifest candidateManifest,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FakeAuthorityProvider(EvidencePullRequestGateAuthoritySnapshot? snapshot)
        : IEvidencePullRequestGateAuthorityProvider
    {
        public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class GateFixture : IAsyncDisposable
    {
        private readonly string _repositoryPath;

        private GateFixture(
            string repositoryPath,
            string artifactRoot,
            string artifactPath,
            EvidencePolicy policy,
            EvidencePullRequestRunIdentity runIdentity,
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            EvidencePlan plan,
            EvidenceManifest manifest,
            EvidencePullRequestGateAuthoritySnapshot authority)
        {
            _repositoryPath = repositoryPath;
            ArtifactRoot = artifactRoot;
            ArtifactPath = artifactPath;
            Policy = policy;
            RunIdentity = runIdentity;
            ExpectedIdentity = expectedIdentity;
            Plan = plan;
            Manifest = manifest;
            Authority = authority;
        }

        public string ArtifactRoot { get; }

        public string ArtifactPath { get; }

        public EvidencePolicy Policy { get; }

        public EvidencePullRequestRunIdentity RunIdentity { get; }

        public EvidencePullRequestGateExpectedIdentity ExpectedIdentity { get; }

        public EvidencePlan Plan { get; }

        public EvidenceManifest Manifest { get; }

        public EvidencePullRequestGateAuthoritySnapshot Authority { get; }

        public static async Task<GateFixture> CreateAsync(
            bool documentationOnly = false,
            bool includeRequiredAssertion = true,
            bool includeArtifact = true)
        {
            var repositoryPath = Path.Join(Path.GetTempPath(), $"appsurface-pr-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            RunGit(repositoryPath, "init", "-q");
            var sourcePath = documentationOnly ? "docs/readme.md" : "src/project.cs";
            WriteFile(repositoryPath, sourcePath, "before\n");
            RunGit(repositoryPath, "add", sourcePath);
            Commit(repositoryPath, "base");
            var baseRevision = RunGit(repositoryPath, "rev-parse", "HEAD").Trim();
            WriteFile(repositoryPath, sourcePath, "after\n");
            RunGit(repositoryPath, "add", sourcePath);
            Commit(repositoryPath, "head");
            var headRevision = RunGit(repositoryPath, "rev-parse", "HEAD").Trim();

            var targetProducer = new EvidenceProducerDeclaration(
                "build",
                "build",
                "1",
                [],
                ["build/passed"],
                [new EvidenceArtifactSlot("report", "reports", "text/plain", Required: false, MaximumBytes: 1024)],
                TimeoutSeconds: 30);
            var targeted = new EvidenceProfile(
                "targeted",
                EvidenceProfileScope.Targeted,
                [],
                [targetProducer],
                [new EvidenceObligation("compile", "code", "Compile the changed source.", ["build"], "build/passed")]);
            var docs = new EvidenceProfile("docs", EvidenceProfileScope.Targeted, [], [], []);
            var policy = new EvidencePolicy(
                "pr-gate",
                "1",
                "targeted",
                [targeted, docs],
                [new EvidencePolicyRule("source", "src/**", "targeted"), new EvidencePolicyRule("documentation", "docs/**", "docs")]);

            var runIdentity = new EvidencePullRequestRunIdentity(321, 321, 777, "main", 654321, 1);
            var expectedIdentity = new EvidencePullRequestGateExpectedIdentity(
                "forge-trust/AppSurface",
                "pull_request_target",
                "evidence-gate.yml",
                "evidence-subject",
                runIdentity);
            var snapshot = await EvidenceGitChangeCapture.CaptureAsync(repositoryPath, baseRevision, headRevision);
            var planner = new EvidencePlanner();
            var plan = EvidenceRevisionPlanBuilder.ResolveForPullRequest(planner, policy, snapshot, runIdentity);

            var artifactRoot = Path.Join(repositoryPath, "handoff");
            var producerArtifactRoot = Path.Join(artifactRoot, "build");
            Directory.CreateDirectory(producerArtifactRoot);
            IReadOnlyList<EvidenceArtifactResult> artifacts = [];
            var artifactPath = Path.Join(producerArtifactRoot, "reports", "build.txt");
            if (includeArtifact)
            {
                var writer = new EvidenceArtifactWriter(targetProducer, producerArtifactRoot);
                artifacts = [await writer.WriteAsync("report", "reports/build.txt", "trusted artifact bytes"u8.ToArray())];
            }

            var producerResult = new EvidenceProducerResult(
                "build",
                EvidenceProducerOutcome.Passed,
                includeRequiredAssertion ? ["build/passed"] : [],
                Artifacts: artifacts);
            var manifest = EvidenceManifestBuilder.Build(
                plan,
                documentationOnly ? [] : [producerResult],
                envelopeStatus: documentationOnly ? EvidenceEnvelopeStatus.NotRequired : EvidenceEnvelopeStatus.ValidatedNotAttested);
            if (documentationOnly)
            {
                manifest = EvidenceManifestBuilder.Build(plan, []);
            }

            var authority = new EvidencePullRequestGateAuthoritySnapshot(
                expectedIdentity.Repository,
                expectedIdentity.EventName,
                baseRevision,
                headRevision,
                expectedIdentity.WorkflowId,
                runIdentity,
                expectedIdentity.SubjectJobId,
                headRevision,
                "success",
                SubjectEnvelopeAttested: !documentationOnly);
            return new GateFixture(
                repositoryPath,
                artifactRoot,
                artifactPath,
                policy,
                runIdentity,
                expectedIdentity,
                plan,
                manifest,
                authority);
        }

        public Task<EvidencePullRequestGateVerificationResult> VerifyAsync(
            EvidencePlan? plan = null,
            EvidenceManifest? manifest = null,
            EvidencePolicy? policy = null,
            EvidencePullRequestGateExpectedIdentity? expectedIdentity = null,
            EvidencePullRequestGateAuthoritySnapshot? authority = null,
            IEvidencePullRequestGateAuthorityProvider? authorityProvider = null,
            IEvidencePullRequestGateArtifactVerifier? artifactVerifier = null,
            string? trustedArtifactHandoffRootPath = null,
            bool omitArtifactRoot = false,
            bool useFakeArtifactVerifier = true)
        {
            var planner = new EvidencePlanner();
            return EvidencePullRequestGateVerifier.VerifyAsync(
                planner,
                policy ?? Policy,
                _repositoryPath,
                expectedIdentity ?? ExpectedIdentity,
                authorityProvider ?? new FakeAuthorityProvider(authority ?? Authority),
                omitArtifactRoot ? null : trustedArtifactHandoffRootPath ?? ArtifactRoot,
                artifactVerifier ?? (useFakeArtifactVerifier ? new FakeArtifactVerifier(true) : null),
                plan ?? Plan,
                manifest ?? Manifest);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_repositoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static void WriteFile(string root, string relativePath, string contents)
        {
            var path = TestPathUtils.PathUnder(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private static void Commit(string repository, string message) => RunGit(
            repository,
            "-c", "user.name=AppSurface Test",
            "-c", "user.email=appsurface@example.invalid",
            "commit", "-qm", message);

        private static string RunGit(string repository, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Git fixture command failed: {error}");
            return output;
        }
    }
}
