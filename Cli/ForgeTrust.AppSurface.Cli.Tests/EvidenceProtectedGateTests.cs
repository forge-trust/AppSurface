using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProtectedGateTests
{
    private const string CoverageAssertionId = "coverage/behavioral-patch@1";
    private const string TestRunId = "fake-test-run/attempt-1";
    private const string TestOutputIdentity = "fake-test-root";

    [Fact]
    public void Allows_TrustedTargetedClaimWhenEveryProtectedFactMatches()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifest.Eligibility);
        Assert.False(expected.AllowReleaseValidatedNotAttested);
        var assertion = Assert.IsType<EvidenceEnvelopeAssertion>(manifest.EnvelopeAssertion);
        Assert.Equal(assertion.ToolRootIdentity, expected.ToolRootIdentity);
        Assert.Equal(assertion.SubjectRootIdentity, expected.SubjectRootIdentity);
        Assert.Equal(assertion.OutputParentIdentity, expected.OutputParentIdentity);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_TargetedNoEvidenceClaimWithoutReleaseOptIn()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted, noEvidence: true);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, []);
        var expected = ExpectedProtectedFacts(plan);

        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifest.Eligibility);
        Assert.False(expected.AllowReleaseValidatedNotAttested);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_ReleaseClaimOnlyWithExplicitValidatedNotAttestedOptIn()
    {
        var plan = CreatePlan(EvidenceProfileScope.Release);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);

        Assert.Equal(EvidenceClaimKind.ReleaseComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.ReleaseGate, manifest.Eligibility);
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
        Assert.True(EvidenceProtectedGate.Allows(
            plan,
            manifest,
            expected with { AllowReleaseValidatedNotAttested = true }));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("base")]
    [InlineData("subject")]
    [InlineData("workflow")]
    [InlineData("policy")]
    [InlineData("proof")]
    [InlineData("output")]
    [InlineData("verifier")]
    [InlineData("verifier-version")]
    [InlineData("provider")]
    [InlineData("catalogue")]
    [InlineData("capabilities")]
    [InlineData("allocation-policy")]
    [InlineData("tool-root")]
    [InlineData("subject-root")]
    [InlineData("output-parent")]
    public void Allows_RejectsWhenAProtectedExpectedFactDiffers(string fact)
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);
        var mismatched = fact switch
        {
            "run" => expected with { RunId = expected.RunId + "/stale-attempt" },
            "base" => expected with { BaseRevision = "different-protected-base" },
            "subject" => expected with { SubjectRevision = "different-tested-head" },
            "workflow" => expected with { WorkflowIdentity = "different-protected-workflow" },
            "policy" => expected with { PolicyDigest = "different-protected-policy-digest" },
            "proof" => expected with { AcceptanceProofDigest = new string('1', 64) },
            "output" => expected with { OutputIdentity = "different-output-handle" },
            "verifier" => expected with { VerifierId = "different-protected-verifier" },
            "verifier-version" => expected with { VerifierVersion = "different-protected-verifier-version" },
            "provider" => expected with { Provider = "different-ci-provider" },
            "catalogue" => expected with { CatalogueDigest = new string('2', 64) },
            "capabilities" => expected with { CapabilitiesDigest = new string('3', 64) },
            "allocation-policy" => expected with { AllocationPolicyDigest = new string('4', 64) },
            "tool-root" => expected with { ToolRootIdentity = "different-protected-tool-root" },
            "subject-root" => expected with { SubjectRootIdentity = "different-restricted-subject-root" },
            "output-parent" => expected with { OutputParentIdentity = "different-output-parent" },
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "Unknown protected fact."),
        };

        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, mismatched));
    }

    [Fact]
    public void Allows_RejectsAnEmptyExpectedOutputIdentity()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan) with { OutputIdentity = string.Empty };

        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_RejectsAnEmptyExpectedAcceptanceProofDigest()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan) with { AcceptanceProofDigest = string.Empty };

        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_RejectsAnEmptyExpectedRunIdentityEvenWhenTheAssertionMatches()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var trusted = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var assertion = Assert.IsType<EvidenceEnvelopeAssertion>(trusted.EnvelopeAssertion);
        var draft = trusted with { EnvelopeAssertion = assertion with { RunId = string.Empty } };
        var manifest = RecalculateStructuralDigest(draft);
        var expected = ExpectedProtectedFacts(plan) with { RunId = string.Empty };

        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("base")]
    [InlineData("subject")]
    [InlineData("workflow")]
    [InlineData("proof")]
    [InlineData("output")]
    [InlineData("verifier")]
    [InlineData("verifier-version")]
    [InlineData("provider")]
    [InlineData("catalogue")]
    [InlineData("capabilities")]
    [InlineData("allocation-policy")]
    [InlineData("tool-root")]
    [InlineData("subject-root")]
    [InlineData("output-parent")]
    public void Verify_IsStructuralAndDoesNotMakeAForgedAssertionGateEligible(string fact)
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);
        var assertion = Assert.IsType<EvidenceEnvelopeAssertion>(manifest.EnvelopeAssertion);
        var forgedAssertion = fact switch
        {
            "run" => assertion with { RunId = assertion.RunId + "/forged" },
            "base" => assertion with { BaseRevision = "forged-base-revision" },
            "subject" => assertion with { SubjectRevision = "forged-subject-revision" },
            "workflow" => assertion with { WorkflowIdentity = "forged-workflow" },
            "proof" => assertion with { AcceptanceProofDigest = new string('e', 64) },
            "output" => assertion with { OutputIdentity = assertion.OutputIdentity + "/forged" },
            "verifier" => assertion with { VerifierId = "forged-verifier" },
            "verifier-version" => assertion with { VerifierVersion = "forged-verifier-version" },
            "provider" => assertion with { Provider = "forged-provider" },
            "catalogue" => assertion with { CatalogueDigest = new string('e', 64) },
            "capabilities" => assertion with { CapabilitiesDigest = new string('e', 64) },
            "allocation-policy" => assertion with { AllocationPolicyDigest = new string('e', 64) },
            "tool-root" => assertion with { ToolRootIdentity = "forged-tool-root" },
            "subject-root" => assertion with { SubjectRootIdentity = "forged-subject-root" },
            "output-parent" => assertion with { OutputParentIdentity = "forged-output-parent" },
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "Unknown protected fact."),
        };
        var forgedManifest = RecalculateStructuralDigest(manifest with { EnvelopeAssertion = forgedAssertion });

        // The canonical digest is public and can be recomputed by an uploader; Verify checks structure, not origin.
        Assert.True(EvidenceManifestBuilder.Verify(plan, forgedManifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, forgedManifest, expected));
    }

    [Fact]
    public void Allows_RejectsObservationEvenWhenItsManifestVerifies()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var admission = EvidenceAdmissionTestFixture.AdmitObservation(plan);
        admission.Activate("fake-observation-output");
        admission.Complete(ownedWorkStopped: true, artifactsVerified: true, cleanupCompleted: true);
        var manifest = EvidenceManifestBuilder.Build(plan, PassingResults(), admission);
        var expected = ExpectedProtectedFacts(plan);

        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.NotRequired, manifest.EnvelopeStatus);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Theory]
    [InlineData(EvidenceEnvelopeStatus.NotRequired)]
    [InlineData(EvidenceEnvelopeStatus.Unavailable)]
    [InlineData(EvidenceEnvelopeStatus.Invalid)]
    public void Allows_RejectsStructurallyValidTrustedManifestWithWrongEnvelopeStatus(EvidenceEnvelopeStatus status)
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var trusted = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);
        var draft = trusted with
        {
            EnvelopeStatus = status,
            ExecutionVerdict = EvidenceExecutionVerdict.Incomplete,
            ClaimKind = EvidenceClaimKind.None,
            Eligibility = EvidenceClaimEligibility.None,
            ManifestDigest = string.Empty,
        };
        var manifest = RecalculateStructuralDigest(draft);

        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_RejectsNullManifestAndMissingAssertion()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var trusted = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var expected = ExpectedProtectedFacts(plan);
        var withoutAssertion = RecalculateStructuralDigest(trusted with
        {
            EnvelopeAssertion = null,
            ExecutionVerdict = EvidenceExecutionVerdict.Incomplete,
            ClaimKind = EvidenceClaimKind.None,
            Eligibility = EvidenceClaimEligibility.None,
            ManifestDigest = string.Empty,
        });

        Assert.False(EvidenceProtectedGate.Allows(plan, null, expected));
        Assert.True(EvidenceManifestBuilder.Verify(plan, withoutAssertion));
        Assert.False(EvidenceProtectedGate.Allows(plan, withoutAssertion, expected));
    }

    [Fact]
    public void Allows_RejectsLegacyManifestAlthoughStructuralVerificationSucceeds()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var admitted = EvidenceAdmissionTestFixture.BuildTrusted(plan, PassingResults());
        var manifest = RecalculateStructuralDigest(admitted with
        {
            Mode = null,
            EnvelopeAssertion = null,
            EnvelopeStatus = EvidenceEnvelopeStatus.NotRequired,
            ManifestDigest = string.Empty,
        });
        var expected = ExpectedProtectedFacts(plan);

        Assert.Null(manifest.Mode);
        Assert.Null(manifest.EnvelopeAssertion);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_RejectsFailedProducerAndOpenObligation()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var failedProducer = EvidenceAdmissionTestFixture.BuildTrusted(
            plan,
            [new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Failed, [CoverageAssertionId])]);
        var openObligation = EvidenceAdmissionTestFixture.BuildTrusted(
            plan,
            [new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, [])]);
        var expected = ExpectedProtectedFacts(plan);

        Assert.True(EvidenceManifestBuilder.Verify(plan, failedProducer));
        Assert.True(EvidenceManifestBuilder.Verify(plan, openObligation));
        Assert.False(EvidenceProtectedGate.Allows(plan, failedProducer, expected));
        Assert.False(EvidenceProtectedGate.Allows(plan, openObligation, expected));
    }

    [Fact]
    public void Allows_RejectsLatchedFailureAfterSuccessfulCleanup()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = BuildAdmittedManifest(plan, PassingResults(), latchFailure: true, cleanupCompleted: true);
        var expected = ExpectedProtectedFacts(plan);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.NotNull(manifest.Metrics.TerminalFailureCode);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    [Fact]
    public void Allows_RejectsCleanupFailureRecordedByTheAdmissionCapability()
    {
        var plan = CreatePlan(EvidenceProfileScope.Targeted);
        var manifest = BuildAdmittedManifest(plan, PassingResults(), latchFailure: false, cleanupCompleted: false);
        var expected = ExpectedProtectedFacts(plan);

        Assert.False(manifest.Metrics.CleanupCompleted);
        Assert.NotNull(manifest.Metrics.TerminalFailureCode);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceProtectedGate.Allows(plan, manifest, expected));
    }

    private static EvidencePlan CreatePlan(EvidenceProfileScope scope, bool noEvidence = false)
    {
        var planner = new EvidencePlanner();
        if (noEvidence)
        {
            var noEvidenceProfile = new EvidenceProfile("no-evidence", EvidenceProfileScope.Targeted, [], [], []);
            var coverageProfile = CreateCoverageProfile(EvidenceProfileScope.Targeted);
            var policy = new EvidencePolicy(
                "protected-gate-tests",
                "1",
                coverageProfile.Id,
                [noEvidenceProfile, coverageProfile],
                [new EvidencePolicyRule("docs", "docs/**", noEvidenceProfile.Id)]);

            return planner.Resolve(policy, [new NormalizedDiffPath("docs/readme.md")]);
        }

        var profile = CreateCoverageProfile(scope);
        return planner.Resolve(
            new EvidencePolicy("protected-gate-tests", "1", profile.Id, [profile], []),
            [new NormalizedDiffPath("src/Feature.cs")]);
    }

    private static EvidenceProfile CreateCoverageProfile(EvidenceProfileScope scope) => new(
        "coverage",
        scope,
        [],
        [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], [CoverageAssertionId], [], 60)],
        [new EvidenceObligation("coverage", "behavior", "Changed behavior requires coverage evidence.", ["coverage"], CoverageAssertionId)]);

    private static IReadOnlyList<EvidenceProducerResult> PassingResults() =>
        [new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, [CoverageAssertionId])];

    private static EvidenceProtectedGateExpectation ExpectedProtectedFacts(EvidencePlan plan) => new(
        TestRunId,
        "base-policy-revision",
        "subject-revision",
        "fake-test-workflow",
        plan.PolicyDigest,
        new string('d', 64),
        TestOutputIdentity,
        "fake-test-verifier",
        "1",
        "fake-test-provider",
        new string('b', 64),
        new string('c', 64),
        new string('a', 64),
        "tool-root",
        "subject-root",
        "output-parent");

    private static EvidenceManifest RecalculateStructuralDigest(EvidenceManifest manifest)
    {
        var draft = manifest with { ManifestDigest = string.Empty };
        return draft with { ManifestDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    private static EvidenceManifest BuildAdmittedManifest(
        EvidencePlan plan,
        IReadOnlyList<EvidenceProducerResult> producerResults,
        bool latchFailure,
        bool cleanupCompleted)
    {
        const string runId = TestRunId;
        var assertion = new EvidenceEnvelopeAssertion(
            "1.0",
            "fake-test-verifier",
            "1",
            "fake-test-provider",
            "fake-test-workflow",
            runId,
            "base-policy-revision",
            "subject-revision",
            "tool-root",
            "subject-root",
            "output-parent",
            "unallocated",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            DateTimeOffset.UnixEpoch.AddDays(1));
        var context = new EvidenceAdmissionContext(
            runId,
            plan.PolicySnapshot!,
            plan.ChangedPaths,
            plan,
            plan.Profile.Producers,
            plan.Profile.Resources,
            [plan.Profile.Id],
            plan.Profile.Producers.Select(static producer => producer.Id).ToArray(),
            [],
            ProtectedSecretsPresent: false,
            ConsumerAcceptanceMatches: true,
            ExpectedAssertion: assertion);
        var admission = EvidenceAdmission.AdmitAsync(
                EvidenceExecutionMode.Trusted,
                plan,
                context,
                new TestArmedWorker(runId),
                new TestVerifier(assertion),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        admission.Activate(TestOutputIdentity);
        if (latchFailure)
        {
            admission.LatchFailure();
        }

        admission.Complete(ownedWorkStopped: true, artifactsVerified: true, cleanupCompleted: cleanupCompleted);

        return EvidenceManifestBuilder.Build(
            plan,
            producerResults,
            admission,
            metrics: new EvidenceExecutionMetrics(CleanupCompleted: cleanupCompleted));
    }

    private sealed class TestArmedWorker(string runId) : IEvidenceArmedWorker
    {
        public bool IsArmed => true;

        public string RunId => runId;
    }

    private sealed class TestVerifier(EvidenceEnvelopeAssertion assertion) : IEvidenceAdmissionVerifier
    {
        public ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult<EvidenceEnvelopeAssertion?>(assertion);
    }
}
