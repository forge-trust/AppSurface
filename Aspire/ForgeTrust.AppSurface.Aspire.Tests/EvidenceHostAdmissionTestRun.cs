using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

/// <summary>Internal accepted-supervisor fixture for exercising Aspire's real shared admission path.</summary>
/// <remarks>This fixture proves library behavior only; it does not mint a public admission token or prove consumer support.</remarks>
internal sealed class EvidenceHostAdmissionTestRun : IDisposable
{
    private static readonly TimeSpan TestJobAllowance = TimeSpan.FromHours(1);
    private readonly EvidenceEnvelopeAssertion? _assertion;
    private readonly bool _verifierAccepts;

    private EvidenceHostAdmissionTestRun(
        EvidenceExecutionMode mode,
        EvidencePlan plan,
        bool consumerAcceptanceMatches,
        bool verifierAccepts,
        TimeSpan? jobRemaining)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Mode = mode;
        var runId = $"aspire-test/{Guid.NewGuid():N}";
        _assertion = mode == EvidenceExecutionMode.Trusted ? CreateAssertion(runId) : null;
        _verifierAccepts = verifierAccepts;
        Context = new EvidenceAdmissionContext(
            runId,
            plan.PolicySnapshot ?? throw new ArgumentException("The test plan must retain its policy snapshot.", nameof(plan)),
            plan.ChangedPaths,
            plan,
            plan.Profile.Producers,
            plan.Profile.Resources,
            [plan.Profile.Id],
            plan.Profile.Producers.Select(static producer => producer.Id).ToArray(),
            [],
            ProtectedSecretsPresent: false,
            ConsumerAcceptanceMatches: consumerAcceptanceMatches,
            ExpectedAssertion: _assertion,
            ObservationProducerClasses: [new("inventory", "1.0.0")]);
        Supervisor = new FakeSupervisor(runId, jobRemaining ?? TestJobAllowance);
        ArtifactDirectory = Path.Combine(Path.GetTempPath(), $"appsurface-evidencehost-test-{Guid.NewGuid():N}");
    }

    internal EvidenceExecutionMode Mode { get; }

    internal EvidenceAdmissionContext Context { get; }

    internal FakeSupervisor Supervisor { get; }

    internal string ArtifactDirectory { get; }

    internal static EvidenceHostAdmissionTestRun Create(
        EvidenceExecutionMode mode,
        EvidencePlan plan,
        bool consumerAcceptanceMatches = true,
        bool verifierAccepts = true,
        TimeSpan? jobRemaining = null) =>
        new(mode, plan, consumerAcceptanceMatches, verifierAccepts, jobRemaining);

    internal static EvidencePlan CreatePlan(
        int resourceDeadlineSeconds = 30,
        EvidenceProfileScope scope = EvidenceProfileScope.Targeted,
        int producerTimeoutSeconds = 30) => SealPlan(new EvidenceProfile(
            "persistence",
            scope,
            [new EvidenceResourceDeclaration("postgres", "aspire_health", resourceDeadlineSeconds, [])],
            [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", ["postgres"], ["coverage/assertion@1"], [], producerTimeoutSeconds)],
            [new EvidenceObligation("persistence", "database", "Persistence changed.", ["coverage"], "coverage/assertion@1")]));

    internal static EvidencePlan CreateObservationPlan() => SealPlan(new EvidenceProfile(
        "observation",
        EvidenceProfileScope.Targeted,
        [],
        [new EvidenceProducerDeclaration("inventory", "inventory", "1.0.0", [], ["inventory/assertion@1"], [], 30)],
        []));

    internal static EvidencePlan SealPlan(
        EvidenceProfile profile,
        IReadOnlyList<NormalizedDiffPath>? changedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var diff = changedPaths ?? [new NormalizedDiffPath("src/Persistence.cs")];
        var policy = new EvidencePolicy("aspire-test-policy", "1.0", profile.Id, [profile], []);
        var draft = new EvidencePlan(
            "1.0",
            policy.Id,
            EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(diff),
            profile,
            diff,
            [],
            string.Empty,
            policy);
        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    internal async Task<EvidenceManifest> RunAsync(
        EvidenceHostBootstrap host,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, ValueTask>? completeWorker = null,
        TimeSpan? loweredStartAllowance = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        return await host.RunSharedCoreForTestsAsync(
            Mode,
            Context,
            Supervisor,
            Mode == EvidenceExecutionMode.Trusted ? new FakeVerifier(_assertion!, _verifierAccepts) : null,
            ArtifactDirectory,
            Supervisor.JobRemaining,
            completeWorker ?? Supervisor.CompleteWorkerAsync,
            cancellationToken,
            loweredStartAllowance).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Directory.Exists(ArtifactDirectory))
        {
            Directory.Delete(ArtifactDirectory, recursive: true);
        }
    }

    private static EvidenceEnvelopeAssertion CreateAssertion(string runId) => new(
        "1.0",
        "aspire-test-verifier",
        "1",
        "aspire-test-provider",
        "aspire-test-workflow",
        runId,
        "base-revision",
        "subject-revision",
        "protected-tool-root",
        "subject-root",
        "protected-output-parent",
        "unbound",
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        new string('d', 64),
        DateTimeOffset.UnixEpoch.AddDays(1));

    private sealed class FakeVerifier(EvidenceEnvelopeAssertion assertion, bool accepts) : IEvidenceAdmissionVerifier
    {
        public ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(accepts ? assertion : null);
    }

    internal sealed class FakeSupervisor(string runId, TimeSpan jobRemaining) : IEvidenceExecutionSupervisor
    {
        internal TimeSpan JobRemaining { get; } = jobRemaining;

        internal int StopRequests { get; private set; }

        internal int ExitAcknowledgements { get; private set; }

        internal int CompletionAcknowledgements { get; private set; }

        internal CancellationToken? CompletionToken { get; private set; }

        internal bool SawFreshStoppingToken { get; private set; }

        public bool IsArmed => JobRemaining > TimeSpan.Zero;

        public string RunId { get; } = runId;

        public void CloseAdmission()
        {
        }

        public ValueTask RequestStopAsync(CancellationToken stoppingToken)
        {
            StopRequests++;
            SawFreshStoppingToken = stoppingToken.CanBeCanceled;
            return ValueTask.CompletedTask;
        }

        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
        {
            ExitAcknowledgements++;
            return ValueTask.CompletedTask;
        }

        internal ValueTask CompleteWorkerAsync(CancellationToken completionToken)
        {
            CompletionAcknowledgements++;
            CompletionToken = completionToken;
            return ValueTask.CompletedTask;
        }
    }
}
