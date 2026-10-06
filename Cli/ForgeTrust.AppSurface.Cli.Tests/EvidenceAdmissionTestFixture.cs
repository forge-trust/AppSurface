using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Test-only deterministic admission for library behavior; it proves no external CI or sandbox identity.</summary>
internal static class EvidenceAdmissionTestFixture
{
    public static EvidenceManifest BuildTrusted(
        EvidencePlan plan,
        IReadOnlyList<EvidenceProducerResult> producerResults,
        IReadOnlyList<EvidenceResourceResult>? resourceResults = null,
        EvidenceExecutionMetrics? metrics = null)
    {
        var assertion = CreateAssertion();
        var runId = assertion.RunId;
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
                new ArmedWorker(runId),
                new AcceptedVerifier(assertion),
                cancellationToken: CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        admission.Activate("fake-test-root");
        admission.Complete(ownedWorkStopped: true, artifactsVerified: true, cleanupCompleted: true);

        return EvidenceManifestBuilder.Build(
            plan,
            producerResults,
            admission,
            resourceResults,
            metrics ?? new EvidenceExecutionMetrics(CleanupCompleted: true));
    }

    public static EvidenceManifest BuildLegacyStructural(
        EvidencePlan plan,
        IReadOnlyList<EvidenceProducerResult> results,
        IReadOnlyList<EvidenceResourceResult>? resourceResults = null,
        EvidenceExecutionMetrics? metrics = null,
        EvidenceEnvelopeStatus envelopeStatus = EvidenceEnvelopeStatus.NotRequired)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(results);
        resourceResults ??= [];
        metrics ??= new EvidenceExecutionMetrics();

        var producerGroups = plan.Profile.Producers.GroupBy(static producer => producer.Id, StringComparer.Ordinal).ToArray();
        var resultGroups = results.GroupBy(static result => result.ProducerId, StringComparer.Ordinal).ToArray();
        var resourceGroups = plan.Profile.Resources.GroupBy(static resource => resource.Id, StringComparer.Ordinal).ToArray();
        var resourceResultGroups = resourceResults.GroupBy(static result => result.ResourceId, StringComparer.Ordinal).ToArray();
        var producers = producerGroups.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var producerResults = resultGroups.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var resources = resourceGroups.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var resourceResultsById = resourceResultGroups.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var invalid = producerGroups.Any(static group => group.Count() > 1)
            || resultGroups.Any(static group => group.Count() > 1)
            || resourceGroups.Any(static group => group.Count() > 1)
            || resourceResultGroups.Any(static group => group.Count() > 1)
            || producerResults.Keys.Any(id => !producers.ContainsKey(id))
            || resourceResultsById.Keys.Any(id => !resources.ContainsKey(id))
            || results.Any(result => !producers.TryGetValue(result.ProducerId, out var declaration)
                || result.SatisfiedAssertionIds.Any(id => !declaration.AssertionIds.Contains(id, StringComparer.Ordinal))
                || !EvidenceArtifactValidation.AreValid(declaration, result.Artifacts));
        var closed = plan.Profile.Obligations.Where(obligation => obligation.RequiredProducerIds.All(id =>
                producerResults.TryGetValue(id, out var result)
                && result.Outcome == EvidenceProducerOutcome.Passed
                && result.SatisfiedAssertionIds.Contains(obligation.RequiredAssertionId, StringComparer.Ordinal)))
            .Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var closedIds = closed.ToHashSet(StringComparer.Ordinal);
        var unmediated = plan.Profile.Obligations.Select(static obligation => obligation.Id)
            .Where(id => !closedIds.Contains(id)).OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var noEvidence = plan.Profile.Resources.Count == 0
            && plan.Profile.Producers.Count == 0
            && plan.Profile.Obligations.Count == 0;
        var passed = !invalid
            && plan.Profile.Producers.All(producer => producerResults.TryGetValue(producer.Id, out var result)
                && result.Outcome == EvidenceProducerOutcome.Passed)
            && plan.Profile.Resources.All(resource => resourceResultsById.TryGetValue(resource.Id, out var result)
                && result.Outcome == EvidenceResourceOutcome.Ready)
            && unmediated.Length == 0
            && (plan.Profile.Scope != EvidenceProfileScope.Release
                || envelopeStatus == EvidenceEnvelopeStatus.ValidatedNotAttested)
            && metrics.CleanupCompleted
            && metrics.TerminalFailureCode is null;
        var verdict = invalid
            ? EvidenceExecutionVerdict.Invalid
            : passed ? EvidenceExecutionVerdict.Passed : EvidenceExecutionVerdict.Incomplete;
        var claim = verdict != EvidenceExecutionVerdict.Passed
            ? EvidenceClaimKind.None
            : noEvidence ? EvidenceClaimKind.NoEvidenceRequired
            : plan.Profile.Scope == EvidenceProfileScope.Release ? EvidenceClaimKind.ReleaseComplete
            : EvidenceClaimKind.TargetedComplete;
        var manifest = new EvidenceManifest(
            plan.ContractVersion,
            plan.PlanDigest,
            verdict,
            claim,
            claim switch
            {
                EvidenceClaimKind.TargetedComplete or EvidenceClaimKind.NoEvidenceRequired => EvidenceClaimEligibility.PullRequestGate,
                EvidenceClaimKind.ReleaseComplete => EvidenceClaimEligibility.ReleaseGate,
                _ => EvidenceClaimEligibility.None,
            },
            envelopeStatus,
            resourceResults.OrderBy(static result => result.ResourceId, StringComparer.Ordinal).ToArray(),
            plan.Profile.Obligations.Select(static obligation => obligation.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            closed,
            unmediated,
            results.OrderBy(static result => result.ProducerId, StringComparer.Ordinal).ToArray(),
            metrics,
            string.Empty);
        return manifest with { ManifestDigest = EvidenceDigest.CanonicalSha256(manifest) };
    }

    public static EvidenceAdmissionResult AdmitObservation(EvidencePlan plan)
    {
        var runId = "fake-test-run/attempt-1";
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
            ConsumerAcceptanceMatches: false,
            ExpectedAssertion: null,
            ObservationProducerClasses: [new("coverage", "1.0.0")]);
        return EvidenceAdmission.AdmitAsync(
                EvidenceExecutionMode.Observation,
                plan,
                context,
                new ArmedWorker(runId),
                verifier: null,
                cancellationToken: CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }

    private static EvidenceEnvelopeAssertion CreateAssertion() => new(
        "1.0", "fake-test-verifier", "1", "fake-test-provider", "fake-test-workflow",
        "fake-test-run/attempt-1", "base-policy-revision", "subject-revision", "tool-root",
        "subject-root", "output-parent", "unbound", new string('a', 64), new string('b', 64),
        new string('c', 64), new string('d', 64), DateTimeOffset.UnixEpoch.AddDays(1));

    private sealed class ArmedWorker(string runId) : IEvidenceArmedWorker
    {
        public bool IsArmed => true;
        public string RunId => runId;
    }

    private sealed class AcceptedVerifier(EvidenceEnvelopeAssertion assertion) : IEvidenceAdmissionVerifier
    {
        public ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult<EvidenceEnvelopeAssertion?>(assertion);
    }
}
