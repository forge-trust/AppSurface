using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public async Task VerifyAsync_ShouldAllowNoCheckoutAttestationOnlyForExplicitEmptyProfile()
    {
        await using var documentation = await GateFixture.CreateAsync(documentationOnly: true);
        var noCheckout = documentation.Authority with
        {
            SubjectJobHeadRevision = string.Empty,
            SubjectEnvelopeAttested = false,
        };

        var documentationResult = await documentation.VerifyAsync(
            authority: noCheckout,
            trustedArtifactHandoffRootPath: null,
            artifactVerifier: null,
            omitArtifactRoot: true,
            useFakeArtifactVerifier: false);

        Assert.True(documentationResult.IsEligible);
        Assert.Equal("ASEVG000", documentationResult.Code);

        await using var code = await GateFixture.CreateAsync();
        var codeResult = await code.VerifyAsync(authority: code.Authority with
        {
            SubjectJobHeadRevision = string.Empty,
            SubjectEnvelopeAttested = false,
        });

        Assert.False(codeResult.IsEligible);
        Assert.Equal("ASEVG007", codeResult.Code);
    }

    [Fact]
    public async Task VerifyAsync_ShouldFailClosedWhenNonemptyProfileHasNoTrustedArtifactRoot()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var missingRoot = await fixture.VerifyAsync(
            trustedArtifactHandoffRootPath: null,
            artifactVerifier: null,
            omitArtifactRoot: true,
            useFakeArtifactVerifier: false);
        var missingVerifier = await fixture.VerifyAsync(
            trustedArtifactHandoffRootPath: fixture.ArtifactRoot,
            artifactVerifier: null,
            useFakeArtifactVerifier: false);

        Assert.False(missingRoot.IsEligible);
        Assert.Equal("ASEVG009", missingRoot.Code);
        Assert.NotNull(missingRoot.Summary);
        Assert.Empty(missingRoot.Summary.ClosedObligationIds);
        Assert.Equal(["compile"], missingRoot.Summary.MissingObligationIds);
        Assert.False(missingVerifier.IsEligible);
        Assert.Equal("ASEVG009", missingVerifier.Code);
        Assert.NotNull(missingVerifier.Summary);
        Assert.Empty(missingVerifier.Summary.ClosedObligationIds);
        Assert.Equal(["compile"], missingVerifier.Summary.MissingObligationIds);
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
    public async Task VerifyAsync_ShouldRejectMalformedControllerIdentityBeforeReadingAuthority()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var invalidIdentities = new[]
        {
            fixture.ExpectedIdentity with { Repository = "missing-owner" },
            fixture.ExpectedIdentity with { Repository = "forge-trust/AppSurface\n" },
            fixture.ExpectedIdentity with { EventName = "workflow_dispatch" },
            fixture.ExpectedIdentity with { WorkflowId = string.Empty },
            fixture.ExpectedIdentity with { SubjectJobId = "evidence\nsubject" },
            fixture.ExpectedIdentity with
            {
                RunIdentity = fixture.RunIdentity with { WorkflowRunAttempt = 0 },
            },
        };

        foreach (var invalidIdentity in invalidIdentities)
        {
            var result = await fixture.VerifyAsync(
                expectedIdentity: invalidIdentity,
                authorityProvider: new ThrowingAuthorityProvider());

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG001", result.Code);
            Assert.Null(result.Summary);
        }
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectMalformedRevisionBeforeReplanning()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var malformedPlans = new[]
        {
            fixture.Plan with { BaseRevision = "not-a-revision" },
            fixture.Plan with { HeadRevision = new string('A', 40) },
            fixture.Plan with { HeadRevision = new string('a', 64) },
        };

        foreach (var plan in malformedPlans)
        {
            var result = await fixture.VerifyAsync(plan: plan);

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG002", result.Code);
            Assert.Null(result.Summary);
        }
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
    public async Task VerifyAsync_ShouldRejectForkedOrSubstitutedCurrentAuthority()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var invalidSnapshots = new[]
        {
            fixture.Authority with { Repository = "other/AppSurface" },
            fixture.Authority with { EventName = "pull_request" },
            fixture.Authority with { WorkflowId = "other-workflow.yml" },
            fixture.Authority with { RunIdentity = fixture.RunIdentity with { HeadRepositoryId = 999 } },
            fixture.Authority with { RunIdentity = fixture.RunIdentity with { WorkflowRunAttempt = 2 } },
            fixture.Authority with { SubjectJobConclusion = "cancelled" },
        };

        foreach (var snapshot in invalidSnapshots)
        {
            var result = await fixture.VerifyAsync(authority: snapshot);

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG007", result.Code);
            Assert.NotNull(result.Summary);
        }
    }

    [Fact]
    public async Task VerifyAsync_ShouldFailClosedWhenTrustedReadersThrow()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var artifactFailure = await fixture.VerifyAsync(artifactVerifier: new ThrowingArtifactVerifier());
        var authorityFailure = await fixture.VerifyAsync(authorityProvider: new ThrowingAuthorityProvider());

        Assert.False(artifactFailure.IsEligible);
        Assert.Equal("ASEVG010", artifactFailure.Code);
        Assert.NotNull(artifactFailure.Summary);
        Assert.Equal(["compile"], artifactFailure.Summary.MissingObligationIds);
        Assert.False(authorityFailure.IsEligible);
        Assert.Equal("ASEVG006", authorityFailure.Code);
        Assert.NotNull(authorityFailure.Summary);
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
    public async Task VerifyAsync_ShouldFailClosedForMalformedAndOverBudgetContracts()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var producer = fixture.Plan.Profile.Producers.Single();
        var invalidPlans = new EvidencePlan[]
        {
            fixture.Plan with { PullRequestRunIdentity = fixture.RunIdentity with { TargetBranch = new string('m', 129) } },
            fixture.Plan with { PolicyDigest = new string('d', EvidenceGitChangeCapture.MaximumPathBytes + 1) },
            fixture.Plan with { MatchedRuleIds = ["source", new string('r', 129)] },
            fixture.Plan with { ChangedPaths = [null!] },
            fixture.Plan with { Profile = fixture.Plan.Profile with { Producers = [producer with { AssertionIds = null! }] } },
            fixture.Plan with { PolicySnapshot = fixture.Policy with { Profiles = [] } },
            fixture.Plan with
            {
                PolicySnapshot = fixture.Policy with
                {
                    Rules = Enumerable.Range(0, 4_000)
                        .Select(_ => new EvidencePolicyRule(new string('i', 128), new string('p', 128), "targeted"))
                        .ToArray(),
                },
            },
        };

        foreach (var plan in invalidPlans)
        {
            var result = await fixture.VerifyAsync(plan: plan);

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG002", result.Code);
            Assert.Null(result.Summary);
        }

        var producerResult = fixture.Manifest.ProducerResults.Single();
        var artifact = producerResult.Artifacts!.Single();
        var invalidManifests = new EvidenceManifest[]
        {
            fixture.Manifest with { Metrics = null! },
            fixture.Manifest with { ResourceResults = new EvidenceResourceResult[EvidenceProfileLimits.MaximumResources + 1] },
            fixture.Manifest with { SelectedObligationIds = Enumerable.Repeat("compile", EvidenceProfileLimits.MaximumObligations + 1).ToArray() },
            fixture.Manifest with
            {
                ProducerResults = [producerResult with { Diagnostic = new string('d', 513) }],
            },
            fixture.Manifest with
            {
                ProducerResults = [producerResult with { SatisfiedAssertionIds = [new string('a', 129)] }],
            },
            fixture.Manifest with
            {
                ProducerResults = [producerResult with { Artifacts = [artifact with { Sha256 = new string('a', EvidenceGitChangeCapture.MaximumPathBytes + 1) }] }],
            },
        };

        foreach (var manifest in invalidManifests)
        {
            var result = await fixture.VerifyAsync(manifest: manifest);

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG002", result.Code);
            Assert.Null(result.Summary);
        }
    }

    [Fact]
    public async Task VerifyAsync_ShouldFailClosedWhenContractCollectionsThrowDuringValidation()
    {
        await using var fixture = await GateFixture.CreateAsync();

        var malformedPlan = fixture.Plan with
        {
            ChangedPaths = new ThrowingReadOnlyList<NormalizedDiffPath>(),
        };
        var malformedManifest = fixture.Manifest with
        {
            ResourceResults = new ThrowingReadOnlyList<EvidenceResourceResult>(),
        };

        var planResult = await fixture.VerifyAsync(plan: malformedPlan);
        var manifestResult = await fixture.VerifyAsync(manifest: malformedManifest);

        Assert.Equal("ASEVG002", planResult.Code);
        Assert.Null(planResult.Summary);
        Assert.Equal("ASEVG002", manifestResult.Code);
        Assert.Null(manifestResult.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldReturnCancelledForPlanningArtifactAndAuthorityCancellation()
    {
        await using var fixture = await GateFixture.CreateAsync();
        using var planningCancellation = new CancellationTokenSource();
        planningCancellation.Cancel();
        using var artifactCancellation = new CancellationTokenSource();
        using var authorityCancellation = new CancellationTokenSource();

        var planningResult = await fixture.VerifyAsync(cancellationToken: planningCancellation.Token);
        var artifactResult = await fixture.VerifyAsync(
            artifactVerifier: new CancellingArtifactVerifier(artifactCancellation),
            cancellationToken: artifactCancellation.Token);
        var authorityResult = await fixture.VerifyAsync(
            authorityProvider: new CancellingAuthorityProvider(authorityCancellation),
            cancellationToken: authorityCancellation.Token);

        Assert.Equal("ASEVG008", planningResult.Code);
        Assert.Null(planningResult.Summary);
        Assert.Equal("ASEVG008", artifactResult.Code);
        Assert.NotNull(artifactResult.Summary);
        Assert.Equal(["compile"], artifactResult.Summary.MissingObligationIds);
        Assert.Equal("ASEVG008", authorityResult.Code);
        Assert.Null(authorityResult.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectNegativeExecutionDurationsAndInvalidEnvelopes()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var producerResult = fixture.Manifest.ProducerResults.Single();
        var invalidManifests = new EvidenceManifest[]
        {
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                envelopeStatus: EvidenceEnvelopeStatus.Invalid),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                envelopeStatus: EvidenceEnvelopeStatus.Unavailable),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                envelopeStatus: EvidenceEnvelopeStatus.NotRequired),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                metrics: new EvidenceExecutionMetrics(PlanningMilliseconds: -1)),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                metrics: new EvidenceExecutionMetrics(ResourceReadinessMilliseconds: -1)),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                metrics: new EvidenceExecutionMetrics(ProducerMilliseconds: -1)),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                metrics: new EvidenceExecutionMetrics(CleanupMilliseconds: -1)),
            EvidenceManifestBuilder.Build(fixture.Plan, fixture.Manifest.ProducerResults,
                metrics: new EvidenceExecutionMetrics(TotalMilliseconds: -1)),
            EvidenceManifestBuilder.Build(fixture.Plan,
                [producerResult with { ElapsedMilliseconds = -1 }],
                envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested),
        };

        foreach (var manifest in invalidManifests)
        {
            var result = await fixture.VerifyAsync(manifest: manifest, artifactVerifier: new FakeArtifactVerifier(true));

            Assert.False(result.IsEligible);
            Assert.Equal("ASEVG004", result.Code);
            Assert.NotNull(result.Summary);
        }

        var profile = fixture.Policy.Profiles.Single(candidate => candidate.Id == "targeted") with
        {
            Resources = [new EvidenceResourceDeclaration("database", "completion", 30, [])],
        };
        var policy = fixture.Policy with
        {
            Profiles = fixture.Policy.Profiles.Select(candidate => candidate.Id == profile.Id ? profile : candidate).ToArray(),
        };
        var plan = await fixture.ResolvePlanAsync(policy);
        var resourceManifest = EvidenceManifestBuilder.Build(
            plan,
            fixture.Manifest.ProducerResults,
            envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested,
            resourceResults: [new EvidenceResourceResult("database", EvidenceResourceOutcome.Ready, -1)]);

        var resourceResult = await fixture.VerifyAsync(
            plan: plan,
            manifest: resourceManifest,
            policy: policy,
            artifactVerifier: new FakeArtifactVerifier(true));

        Assert.False(resourceResult.IsEligible);
        Assert.Equal("ASEVG004", resourceResult.Code);
        Assert.NotNull(resourceResult.Summary);
    }

    [Fact]
    public async Task VerifyAsync_ShouldBoundSummariesAndDescribeConservativeFallback()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var fallbackPolicy = fixture.Policy with { Rules = [] };
        var fallbackPlan = await fixture.ResolvePlanAsync(fallbackPolicy);
        var fallbackManifest = EvidenceManifestBuilder.Build(
            fallbackPlan,
            fixture.Manifest.ProducerResults,
            envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested);

        var fallbackResult = await fixture.VerifyAsync(
            plan: fallbackPlan,
            manifest: fallbackManifest,
            policy: fallbackPolicy);

        Assert.True(fallbackResult.IsEligible);
        Assert.NotNull(fallbackResult.Summary);
        Assert.Equal("conservative-fallback", fallbackResult.Summary.SelectionRationale);
        var fallbackRule = Assert.Single(fallbackResult.Summary.MatchedRules);
        Assert.Equal("conservative:targeted", fallbackRule.Id);
        Assert.Equal("conservative fallback", fallbackRule.Pattern);

        var originalProfile = fixture.Policy.Profiles.Single(candidate => candidate.Id == "targeted");
        var obligations = Enumerable.Range(0, EvidenceProfileLimits.MaximumObligations)
            .Select(index => new EvidenceObligation(
                $"compile-{index:D3}",
                "code",
                new string('r', 300),
                ["build"],
                "build/passed"))
            .ToArray();
        var boundedProfile = originalProfile with { Obligations = obligations };
        var boundedPolicy = fixture.Policy with
        {
            Profiles = fixture.Policy.Profiles
                .Select(candidate => candidate.Id == boundedProfile.Id ? boundedProfile : candidate)
                .ToArray(),
        };
        var boundedPlan = await fixture.ResolvePlanAsync(boundedPolicy);
        var boundedManifest = EvidenceManifestBuilder.Build(
            boundedPlan,
            fixture.Manifest.ProducerResults,
            envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested);

        var boundedResult = await fixture.VerifyAsync(
            plan: boundedPlan,
            manifest: boundedManifest,
            policy: boundedPolicy);

        Assert.True(boundedResult.IsEligible);
        var summary = Assert.IsType<EvidencePullRequestGateSummary>(boundedResult.Summary);
        Assert.Equal(EvidencePullRequestGateVerifier.MaximumSummaryItems, summary.SelectedObligationIds.Count);
        Assert.Equal(EvidenceProfileLimits.MaximumObligations - EvidencePullRequestGateVerifier.MaximumSummaryItems, summary.OmittedSelectedObligationCount);
        Assert.Equal(EvidencePullRequestGateVerifier.MaximumSummaryItems, summary.ClosedObligationIds.Count);
        Assert.Equal(EvidenceProfileLimits.MaximumObligations - EvidencePullRequestGateVerifier.MaximumSummaryItems, summary.OmittedClosedObligationCount);
        Assert.Empty(summary.MissingObligationIds);
        Assert.Equal(EvidenceProfileLimits.MaximumObligations - EvidencePullRequestGateVerifier.MaximumSummaryItems, summary.OmittedObligationRationaleCount);
        Assert.All(summary.ObligationRationales, rationale => Assert.Equal(256, rationale.Rationale.Length));
        Assert.Equal("compile-000", summary.SelectedObligationIds[0]);
        Assert.Equal("compile-063", summary.SelectedObligationIds[^1]);
    }

    [Fact]
    public async Task VerifyAsync_ShouldNotCloseSummaryObligationsForInconsistentManifest()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var inconsistent = fixture.Manifest with
        {
            ExecutionVerdict = EvidenceExecutionVerdict.Incomplete,
            ManifestDigest = string.Empty,
        };
        inconsistent = inconsistent with { ManifestDigest = EvidenceDigest.CanonicalSha256(inconsistent) };

        var result = await fixture.VerifyAsync(manifest: inconsistent);

        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG004", result.Code);
        Assert.NotNull(result.Summary);
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
        if (!SupportsNoFollowVerifier)
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

        var linkedRoot = Path.Join(Path.GetDirectoryName(fixture.ArtifactRoot)!, "handoff-link");
        Directory.CreateSymbolicLink(linkedRoot, fixture.ArtifactRoot);
        Assert.False(await verifier.VerifyArtifactsAsync(linkedRoot, fixture.Plan, fixture.Manifest));

        File.Delete(fixture.ArtifactPath);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));

        var symlinkTarget = Path.Join(fixture.ArtifactRoot, "symlink-target.txt");
        File.WriteAllText(symlinkTarget, "artifact bytes");
        File.CreateSymbolicLink(fixture.ArtifactPath, symlinkTarget);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectSizeMismatchAndNonRegularFileOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var artifact = fixture.Manifest.ProducerResults.Single().Artifacts!.Single();

        File.WriteAllBytes(fixture.ArtifactPath, new byte[checked((int)artifact.LengthBytes + 1)]);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));

        File.Delete(fixture.ArtifactPath);
        Directory.CreateDirectory(fixture.ArtifactPath);
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectInvalidArtifactMetadataAndTraversalOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var artifact = fixture.Manifest.ProducerResults.Single().Artifacts!.Single();
        var invalidArtifacts = new[]
        {
            artifact with { LogicalName = "undeclared" },
            artifact with { MediaType = "application/json" },
            artifact with { LengthBytes = artifact.LengthBytes + 1 },
            artifact with { RelativePath = "../outside.txt" },
            artifact with { RelativePath = "other/build.txt" },
            artifact with { RelativePath = "reports\\build.txt" },
            artifact with { RelativePath = "reports/bad\nname.txt" },
            artifact with { RelativePath = "reports/build.txt " },
            artifact with { RelativePath = $"reports/{new string('x', EvidenceGitChangeCapture.MaximumPathBytes)}" },
            artifact with { Sha256 = "not-a-sha256" },
        };

        foreach (var invalidArtifact in invalidArtifacts)
        {
            Assert.False(await verifier.VerifyArtifactsAsync(
                fixture.ArtifactRoot,
                fixture.Plan,
                ReplaceArtifacts(fixture.Manifest, [invalidArtifact])));
        }

        var producer = fixture.Plan.Profile.Producers.Single();
        var traversalProducer = producer with { Id = "../outside" };
        var traversalPlan = fixture.Plan with
        {
            Profile = fixture.Plan.Profile with { Producers = [traversalProducer] },
        };
        var traversalResult = fixture.Manifest.ProducerResults.Single() with { ProducerId = traversalProducer.Id };
        var traversalManifest = ReplaceProducerResults(fixture.Manifest, [traversalResult]);

        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, traversalPlan, traversalManifest));

        foreach (var invalidProducerId in new[] { string.Empty, "build\n" })
        {
            var invalidProducer = producer with { Id = invalidProducerId };
            var invalidPlan = fixture.Plan with
            {
                Profile = fixture.Plan.Profile with { Producers = [invalidProducer] },
            };
            var invalidResult = fixture.Manifest.ProducerResults.Single() with { ProducerId = invalidProducerId };
            var invalidManifest = ReplaceProducerResults(fixture.Manifest, [invalidResult]);

            Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, invalidPlan, invalidManifest));
        }
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectSymlinkedProducerDirectoryOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var producerRoot = Path.Join(fixture.ArtifactRoot, "build");
        Directory.Delete(producerRoot, recursive: true);

        var alternateProducerRoot = Path.Join(Path.GetDirectoryName(fixture.ArtifactRoot)!, "alternate-build");
        var alternateArtifactPath = Path.Join(alternateProducerRoot, "reports", "build.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(alternateArtifactPath)!);
        await File.WriteAllTextAsync(alternateArtifactPath, "trusted artifact bytes");
        Directory.CreateSymbolicLink(producerRoot, alternateProducerRoot);

        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldAcceptProducerWithoutOptionalArtifactsOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync(includeArtifact: false);
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();

        Assert.Empty(fixture.Manifest.ProducerResults.Single().Artifacts!);
        Assert.True(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectDuplicateResultsAndArtifactsOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var producerResult = fixture.Manifest.ProducerResults.Single();
        var artifact = producerResult.Artifacts!.Single();

        var duplicateProducerResults = ReplaceProducerResults(fixture.Manifest, [producerResult, producerResult]);
        var missingProducerResult = ReplaceProducerResults(fixture.Manifest, []);
        var duplicateArtifacts = ReplaceArtifacts(fixture.Manifest, [artifact, artifact]);
        var unexpectedProducerResult = ReplaceProducerResults(
            fixture.Manifest,
            [producerResult, new EvidenceProducerResult("unexpected", EvidenceProducerOutcome.Passed, [])]);

        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, duplicateProducerResults));
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, missingProducerResult));
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, duplicateArtifacts));
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, unexpectedProducerResult));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectHardLinkedArtifactOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var producer = fixture.Plan.Profile.Producers.Single();
        var secondSlot = new EvidenceArtifactSlot("report-copy", "reports", "text/plain", Required: false, MaximumBytes: 1024);
        var plan = fixture.Plan with
        {
            Profile = fixture.Plan.Profile with
            {
                Producers = [producer with { ArtifactSlots = [.. producer.ArtifactSlots, secondSlot] }],
            },
        };
        var linkedArtifactPath = Path.Join(Path.GetDirectoryName(fixture.ArtifactPath)!, "copy.txt");
        Assert.Equal(0, CreateHardLink(fixture.ArtifactPath, linkedArtifactPath));

        var artifact = fixture.Manifest.ProducerResults.Single().Artifacts!.Single();
        var linkedArtifact = artifact with { LogicalName = secondSlot.LogicalName, RelativePath = "reports/copy.txt" };
        var manifest = ReplaceArtifacts(fixture.Manifest, [artifact, linkedArtifact]);

        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, plan, manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldStopAtMaximumVerifiedFileCountOnLinux()
    {
        const int maximumFileCount = EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedFileCount;
        const int fileCountAboveLimit = maximumFileCount + 1;
        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var slots = Enumerable.Range(0, fileCountAboveLimit)
            .Select(index => new EvidenceArtifactSlot($"slot-{index:D2}", "reports", "text/plain", Required: false, MaximumBytes: 0))
            .ToArray();
        var targetedProfile = fixture.Policy.Profiles.Single(profile => profile.Id == "targeted");
        var producer = targetedProfile.Producers.Single() with { ArtifactSlots = slots };
        var updatedPolicy = fixture.Policy with
        {
            Profiles = fixture.Policy.Profiles
                .Select(profile => profile.Id == targetedProfile.Id
                    ? profile with { Producers = [producer] }
                    : profile)
                .ToArray(),
        };
        var plan = new EvidencePlanner().ResolveForGate(updatedPolicy, fixture.Plan.ChangedPaths);
        var producerRoot = Path.GetDirectoryName(Path.GetDirectoryName(fixture.ArtifactPath)!)!;
        var writer = new EvidenceArtifactWriter(producer, producerRoot);
        for (var index = 0; index < fileCountAboveLimit; index++)
        {
            await writer.WriteAsync(slots[index].LogicalName, $"reports/empty-{index:D2}.txt", ReadOnlyMemory<byte>.Empty);
        }

        var artifacts = writer.WrittenArtifacts;
        Assert.Equal(fileCountAboveLimit, producer.ArtifactSlots.Count);
        Assert.Equal(fileCountAboveLimit, artifacts.Count);
        Assert.True(EvidenceArtifactValidation.AreValid(producer, artifacts));
        var manifestAtLimit = EvidenceManifestBuilder.Build(
            plan,
            [new EvidenceProducerResult("build", EvidenceProducerOutcome.Passed, ["build/passed"], Artifacts: artifacts.Take(maximumFileCount).ToArray())]);
        var manifestAboveLimit = EvidenceManifestBuilder.Build(
            plan,
            [new EvidenceProducerResult("build", EvidenceProducerOutcome.Passed, ["build/passed"], Artifacts: artifacts)]);

        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        Assert.True(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, plan, manifestAtLimit));
        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, plan, manifestAboveLimit));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldFailClosedOutsideSupportedLinuxArchitectures()
    {
        if (SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();

        Assert.False(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldAcceptEmptyProfileWithoutArtifactFiles()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync(documentationOnly: true);
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();

        Assert.True(await verifier.VerifyArtifactsAsync(fixture.ArtifactRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldAcceptEmptyProfileWithFilesystemRootOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync(documentationOnly: true);
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();

        Assert.True(await verifier.VerifyArtifactsAsync(
            Path.GetPathRoot(fixture.ArtifactRoot)!,
            fixture.Plan,
            fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldRejectRegularFileAsTrustedRootOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        var fileRoot = Path.Join(Path.GetDirectoryName(fixture.ArtifactRoot)!, "handoff-file");
        await File.WriteAllTextAsync(fileRoot, "not a directory");

        Assert.False(await verifier.VerifyArtifactsAsync(fileRoot, fixture.Plan, fixture.Manifest));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldPropagateCancellation()
    {
        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyArtifactsAsync(
            fixture.ArtifactRoot,
            fixture.Plan,
            fixture.Manifest,
            cancellation.Token));
    }

    [Fact]
    public async Task NoFollowArtifactVerifier_ShouldPropagateCancellationRaisedDuringVerificationOnLinux()
    {
        if (!SupportsNoFollowVerifier)
        {
            return;
        }

        await using var fixture = await GateFixture.CreateAsync();
        var verifier = new EvidencePullRequestGateNoFollowArtifactVerifier();
        using var cancellation = new CancellationTokenSource();
        var manifest = fixture.Manifest with
        {
            ProducerResults = new CancellingReadOnlyList<EvidenceProducerResult>(
                fixture.Manifest.ProducerResults,
                cancellation),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyArtifactsAsync(
            fixture.ArtifactRoot,
            fixture.Plan,
            manifest,
            cancellation.Token));
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectRecomputedManifestWithForgedArtifactHash()
    {
        if (!SupportsNoFollowVerifier)
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
        => ReplaceArtifacts(manifest, [replacement]);

    private static EvidenceManifest ReplaceArtifacts(
        EvidenceManifest manifest,
        IReadOnlyList<EvidenceArtifactResult> replacements,
        string producerId = "build")
    {
        var producerResults = manifest.ProducerResults
            .Select(producer => producer.ProducerId == producerId ? producer with { Artifacts = replacements } : producer)
            .ToArray();
        return ReplaceProducerResults(manifest, producerResults);
    }

    private static EvidenceManifest ReplaceProducerResults(
        EvidenceManifest manifest,
        IReadOnlyList<EvidenceProducerResult> producerResults)
    {
        var updated = manifest with { ProducerResults = producerResults, ManifestDigest = string.Empty };
        return updated with { ManifestDigest = EvidenceDigest.CanonicalSha256(updated) };
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    private static bool SupportsNoFollowVerifier =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    private sealed class FakeArtifactVerifier(bool result) : IEvidencePullRequestGateArtifactVerifier
    {
        public Task<bool> VerifyArtifactsAsync(
            string trustedArtifactHandoffRootPath,
            EvidencePlan verifiedPlan,
            EvidenceManifest candidateManifest,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ThrowingArtifactVerifier : IEvidencePullRequestGateArtifactVerifier
    {
        public Task<bool> VerifyArtifactsAsync(
            string trustedArtifactHandoffRootPath,
            EvidencePlan verifiedPlan,
            EvidenceManifest candidateManifest,
            CancellationToken cancellationToken = default) => throw new IOException("Artifact reader failed.");
    }

    private sealed class FakeAuthorityProvider(EvidencePullRequestGateAuthoritySnapshot? snapshot)
        : IEvidencePullRequestGateAuthorityProvider
    {
        public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class ThrowingAuthorityProvider : IEvidencePullRequestGateAuthorityProvider
    {
        public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            CancellationToken cancellationToken = default) => throw new IOException("Authority reader failed.");
    }

    private sealed class CancellingArtifactVerifier(CancellationTokenSource cancellation)
        : IEvidencePullRequestGateArtifactVerifier
    {
        public Task<bool> VerifyArtifactsAsync(
            string trustedArtifactHandoffRootPath,
            EvidencePlan verifiedPlan,
            EvidenceManifest candidateManifest,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CancellingAuthorityProvider(CancellationTokenSource cancellation)
        : IEvidencePullRequestGateAuthorityProvider
    {
        public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count => 0;

        public T this[int index] => throw new InvalidOperationException("The untrusted collection cannot be indexed.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("The untrusted collection cannot be enumerated.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingReadOnlyList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation)
        : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            cancellation.Cancel();
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
            bool useFakeArtifactVerifier = true,
            CancellationToken cancellationToken = default)
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
                manifest ?? Manifest,
                cancellationToken);
        }

        public async Task<EvidencePlan> ResolvePlanAsync(EvidencePolicy policy)
        {
            var snapshot = await EvidenceGitChangeCapture.CaptureAsync(_repositoryPath, Plan.BaseRevision!, Plan.HeadRevision!);
            return EvidenceRevisionPlanBuilder.ResolveForPullRequest(new EvidencePlanner(), policy, snapshot, RunIdentity);
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
