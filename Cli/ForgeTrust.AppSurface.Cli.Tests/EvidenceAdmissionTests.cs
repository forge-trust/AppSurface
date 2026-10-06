using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceAdmissionTests
{
    [Theory]
    [InlineData(false, EvidenceEnvelopeStatus.NotRequired)]
    [InlineData(false, EvidenceEnvelopeStatus.ValidatedNotAttested)]
    [InlineData(true, EvidenceEnvelopeStatus.NotRequired)]
    public void LegacyBuilderCannotMintAnyClaim(bool observation, EvidenceEnvelopeStatus status)
    {
        var (plan, _) = Inputs();
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceManifestBuilder.Build(plan, [], observation, status));
        Assert.Equal("ASEVD400", error.Code);
    }

    [Fact]
    public async Task MatchingObservationDoesNotInvokeVerifierAndRequiresActivationCompletion()
    {
        var (plan, context) = Inputs();
        var verifier = new Verifier(context.ExpectedAssertion);
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), verifier, default);
        Assert.Equal(0, verifier.Calls);
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceManifestBuilder.Build(plan, [], admission));
        admission.Activate("root:1");
        admission.Complete(true, true, true);
        var manifest = EvidenceManifestBuilder.Build(plan, [], admission);
        Assert.Equal(EvidenceExecutionMode.Observation, manifest.Mode);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Null(manifest.EnvelopeAssertion);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceManifestBuilder.Build(plan, [], admission));
    }

    [Fact]
    public async Task ObservationAdmissionRejectsSecondActivationWithoutConsumingValidFinalization()
    {
        var (plan, context) = Inputs();
        var admission = await EvidenceAdmission.AdmitAsync(
            EvidenceExecutionMode.Observation, plan, context, new Worker(), null, default);
        admission.Activate("observation-output-first");

        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            admission.Activate("observation-output-second-canary"));

        Assert.Equal("ASEVD409", error.Code);
        Assert.StartsWith("ASEVD409: Output activation is unavailable.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("observation-output-second-canary", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);

        admission.Complete(ownedWorkStopped: true, artifactsVerified: true, cleanupCompleted: true);
        var manifest = EvidenceManifestBuilder.Build(plan, [], admission);

        Assert.Equal(context.RunId, admission.RunId);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceExecutionMode.Observation, manifest.Mode);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Equal(EvidenceEnvelopeStatus.NotRequired, manifest.EnvelopeStatus);
        Assert.Null(manifest.EnvelopeAssertion);
        Assert.Equal(plan.PlanDigest, manifest.PlanDigest);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Theory]
    [InlineData(false, "run/1", "ASEVD402")]
    [InlineData(true, "stale/1", "ASEVD402")]
    public async Task MissingOrMismatchedSupervisionRejectsBeforeVerifier(bool armed, string runId, string expected)
    {
        var (plan, context) = Inputs();
        var verifier = new Verifier(context.ExpectedAssertion);
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(armed, runId), verifier, default));
        Assert.Equal(expected, error.Code);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CoherentHeadSubstitutionOrHashDriftCannotAdmit(int variant)
    {
        var (plan, context) = Inputs();
        plan = variant switch
        {
            0 => plan with { PolicyDigest = new string('f', 64) },
            1 => plan with { DiffDigest = new string('f', 64) },
            2 => plan with { PlanDigest = new string('f', 64) },
            _ => new EvidencePlanner().Resolve(context.Policy with { Id = "head-canary-private-value" }, context.Diff),
        };
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), null, default));
        Assert.Equal("ASEVD403", error.Code);
        Assert.DoesNotContain("canary", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "ASEVD404")]
    [InlineData(1, "ASEVD405")]
    [InlineData(2, "ASEVD405")]
    [InlineData(3, "ASEVD406")]
    public async Task CatalogueSecretAndUndeclaredProfileReject(int variant, string expected)
    {
        var (plan, context) = Inputs();
        context = variant switch
        {
            0 => context with { Producers = [new EvidenceProducerDeclaration("extra", "privileged", "9", [], [], [], 1)] },
            1 => context with { ProtectedSecretsPresent = true },
            2 => context with { SensitiveProjectionKeys = ["private-canary"] },
            _ => context with { ObservationProfiles = [] },
        };
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), null, default));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedRequiresAcceptanceAndExactlyMatchingRegisteredAssertion()
    {
        var (plan, context) = Inputs();
        var verifier = new Verifier(context.ExpectedAssertion);
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context with { ConsumerAcceptanceMatches = false }, new Worker(), verifier, default));
        Assert.Equal("ASEVD407", error.Code);
        Assert.Equal(0, verifier.Calls);
        error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(), new Verifier(context.ExpectedAssertion! with { SubjectRevision = "substitute" }), default));
        Assert.Equal("ASEVD408", error.Code);
        error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(), new Verifier(null), default));
        Assert.Equal("ASEVD408", error.Code);
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(), verifier, default);
        Assert.Equal(1, verifier.Calls);
        admission.Activate("root:1");
        admission.Complete(true, true, true);
        var manifest = EvidenceManifestBuilder.Build(plan, [], admission);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifest.Eligibility);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, manifest.EnvelopeStatus);
        Assert.Equal("root:1", manifest.EnvelopeAssertion!.OutputIdentity);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailureLatchArtifactsAndCleanupCannotUpgradeAfterLatePass(int variant)
    {
        var (plan, context) = Inputs();
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(), new Verifier(context.ExpectedAssertion), default);
        admission.Activate("root:1");
        if (variant == 0) admission.LatchFailure();
        admission.Complete(true, variant != 1, variant != 2);
        var manifest = EvidenceManifestBuilder.Build(plan, [], admission);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Fact]
    public async Task UnstoppedWorkAndChangedPlanForbidFinalization()
    {
        var (plan, context) = Inputs();
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), null, default);
        admission.Activate("root:1");
        Assert.Throws<EvidenceAdmissionException>(() => admission.Complete(false, true, true));
        admission.Complete(true, true, true);
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceManifestBuilder.Build(plan with { Profile = plan.Profile with { Id = "changed" } }, [], admission));
        Assert.Equal(EvidenceClaimKind.ObservationOnly, EvidenceManifestBuilder.Build(plan, [], admission).ClaimKind);
    }

    [Fact]
    public async Task CancelledAndUnknownModesCannotInvokeVerifier()
    {
        var (plan, context) = Inputs();
        var verifier = new Verifier(context.ExpectedAssertion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Trusted, plan, context, new Worker(), verifier, new CancellationToken(true)));
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync((EvidenceExecutionMode)99, plan, context, new Worker(), verifier, default));
        Assert.Equal("ASEVD401", error.Code);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData("coverage", "1.0.0", true)]
    [InlineData("privileged", "1.0.0", false)]
    [InlineData("coverage", "2.0.0", false)]
    public async Task ObservationRequiresTheExactProtectedSafeProducerClass(string kind, string version, bool accepted)
    {
        var (original, _) = Inputs();
        var producer = new EvidenceProducerDeclaration("selected", kind, version, [], ["coverage@1"], [], 1);
        var profile = new EvidenceProfile("observation", EvidenceProfileScope.Targeted, [], [producer], []);
        var policy = original.PolicySnapshot! with { ConservativeProfileId = profile.Id, Profiles = [profile], Rules = [] };
        var plan = new EvidencePlanner().Resolve(policy, original.ChangedPaths);
        var context = new EvidenceAdmissionContext("run/1", policy, plan.ChangedPaths, plan, [producer], [],
            [profile.Id], [producer.Id], [], false, false, null,
            ObservationProducerClasses: [new("coverage", "1.0.0")]);
        var verifier = new Verifier(null);

        if (accepted)
        {
            var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), verifier, default);
            Assert.Equal(EvidenceExecutionMode.Observation, admission.Mode);
        }
        else
        {
            var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
                await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, context, new Worker(), verifier, default));
            Assert.Equal("ASEVD406", error.Code);
        }

        Assert.Equal(0, verifier.Calls);
        var missingClasses = context with { ObservationProducerClasses = null };
        var missingClassError = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, missingClasses, new Worker(), null, default));
        Assert.Equal("ASEVD406", missingClassError.Code);
    }

    private static (EvidencePlan, EvidenceAdmissionContext) Inputs()
    {
        var profile = new EvidenceProfile("no-evidence", EvidenceProfileScope.Targeted, [], [], []);
        var coverage = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], ["coverage@1"], [], 1)],
            [new EvidenceObligation("coverage", "change", "required", ["coverage"], "coverage@1")]);
        var policy = new EvidencePolicy("base-policy", "1", "coverage", [profile, coverage], [new EvidencePolicyRule("docs", "docs/**", profile.Id)]);
        NormalizedDiffPath[] diff = [new("docs/readme.md")];
        var plan = new EvidencePlanner().Resolve(policy, diff);
        var hash = new string('a', 64);
        var assertion = new EvidenceEnvelopeAssertion("1.0", "base-verifier", "1", "GitHub", "workflow:base", "run/1", hash,
            new string('b', 64), "tool:1", "subject:1", "parent:1", "", hash, hash, hash, hash,
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        return (plan, new EvidenceAdmissionContext("run/1", policy, diff, plan, [], [], [profile.Id], [], [], false, true, assertion));
    }

    private sealed class Worker(bool armed = true, string runId = "run/1") : IEvidenceArmedWorker
    {
        public bool IsArmed => armed;
        public string RunId => runId;
    }

    private sealed class Verifier(EvidenceEnvelopeAssertion? result) : IEvidenceAdmissionVerifier
    {
        public int Calls { get; private set; }
        public ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }
}
