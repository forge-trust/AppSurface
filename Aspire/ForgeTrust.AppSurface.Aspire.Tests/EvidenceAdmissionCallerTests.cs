using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAdmissionCallerTests
{
    [Fact]
    public async Task Create_DefersConfigurationAndLegacyRunFailsBeforeCallback()
    {
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), _ => configured = true);

#pragma warning disable CS0618
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync());
#pragma warning restore CS0618

        Assert.Equal("ASEVD401", exception.Code);
        Assert.False(configured);
    }

    [Fact]
    public async Task LegacyObservationRun_RejectsBeforeConfiguration()
    {
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), _ => configured = true);

#pragma warning disable CS0618
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(observationOnly: true));
#pragma warning restore CS0618

        Assert.Equal("ASEVD402", exception.Code);
        Assert.False(configured);
    }

    [Fact]
    public async Task LegacyRun_RejectsBeforeApplicationFactory()
    {
        var factoryCalled = false;
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
            registration.SetApplicationFactory(() =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("The legacy path must reject before factory invocation.");
            }));

#pragma warning disable CS0618
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync());
#pragma warning restore CS0618

        Assert.Equal("ASEVD401", exception.Code);
        Assert.False(factoryCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedCore_LegacyOptionsAndAcceptedEnvelopeCannotUpgradeObservationOrRedirectOutput(
        bool requireTrustedEnvelope)
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var legacyOutput = TestPathUtils.PathUnder(Path.GetTempPath(), "evidence-legacy-output-" + Guid.NewGuid().ToString("N"));
        var legacyVerifier = new RecordingLegacyEnvelopeVerifier(new EvidenceEnvelopeResult(
            Accepted: true,
            Attested: true,
            Diagnostic: "Legacy acceptance must not grant runtime authority."));
        await using var host = EvidenceHostBootstrap.Create(
            plan,
            registration =>
            {
                registration.AddProducer(new PassingProducer());
                registration.SetEnvelopeVerifier(legacyVerifier);
            },
            new EvidenceHostOptions(requireTrustedEnvelope, legacyOutput));
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, host.Plan);

        var manifest = await run.RunAsync(host);

        Assert.Equal(EvidenceExecutionMode.Observation, manifest.Mode);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Null(manifest.EnvelopeAssertion);
        Assert.Equal(0, legacyVerifier.Calls);
        Assert.False(Directory.Exists(legacyOutput));
        Assert.True(Directory.Exists(run.ArtifactDirectory));
        Assert.Equal(EvidenceHostState.Completed, host.State);
        Assert.True(EvidenceManifestBuilder.Verify(host.Plan, manifest));
    }

    [Fact]
    public async Task RequestRun_FailsClosedBeforeConfigurationWhenProtectedInputsAreUnavailable()
    {
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), _ => configured = true);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, "/run/evidence.sock")));

        Assert.Equal("ASEVD402", exception.Code);
        Assert.False(configured);
    }

    [Fact]
    public async Task RequestRun_AuthenticationFailureConsumesHostBeforeSharedCoreRetry()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configured = true);
        using var retry = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);

        var failure = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => host.RunAsync(
            new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, "/run/evidence.sock")));
        Assert.Equal("ASEVD402", failure.Code);

        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.RunAsync(host));

        Assert.False(configured);
        Assert.False(Directory.Exists(retry.ArtifactDirectory));
        Assert.Equal(0, retry.Supervisor.StopRequests);
        Assert.Equal(0, retry.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task SharedCore_AdmissionFailureConsumesHostBeforeObservationRetry()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configured = true);
        using var rejected = EvidenceHostAdmissionTestRun.Create(
            EvidenceExecutionMode.Trusted, plan, consumerAcceptanceMatches: false);
        using var retry = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);

        var failure = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => rejected.RunAsync(host));
        Assert.Equal("ASEVD407", failure.Code);
        Assert.Equal(1, rejected.Supervisor.StopRequests);
        Assert.Equal(1, rejected.Supervisor.ExitAcknowledgements);

        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.RunAsync(host));

        Assert.False(configured);
        Assert.False(Directory.Exists(rejected.ArtifactDirectory));
        Assert.False(Directory.Exists(retry.ArtifactDirectory));
        Assert.Equal(0, retry.Supervisor.StopRequests);
        Assert.Equal(0, retry.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task SharedCore_CancellationBeforeOwnershipLeavesHostAvailable()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            configured = true;
            registration.AddProducer(new PassingProducer());
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.RunAsync(host, canceled.Token));
        Assert.False(configured);
        Assert.False(Directory.Exists(run.ArtifactDirectory));

        var manifest = await run.RunAsync(host);

        Assert.True(configured);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task SharedCore_ConcurrentRunAndDisposeWaitForOwnedProducerAndRejectReuse()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var producer = new HeldProducer();
        await using var host = EvidenceHostBootstrap.Create(plan, registration => registration.AddProducer(producer));
        using var first = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        using var second = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var running = first.RunAsync(host);
        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = host.DisposeAsync().AsTask();
        var retry = second.RunAsync(host);
        Assert.False(disposing.IsCompleted);
        Assert.False(retry.IsCompleted);
        Assert.Equal(0, producer.DisposeCount);

        producer.Release.SetResult();
        var manifest = await running.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(EvidenceHostState.Disposed, host.State);
        Assert.Equal(0, second.Supervisor.StopRequests);
        Assert.False(Directory.Exists(second.ArtifactDirectory));
    }

    [Fact]
    public async Task SharedCore_DisposeBeforeRunRejectsBeforeAdmissionAndConfiguration()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configured = true);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        await host.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(host));

        Assert.False(configured);
        Assert.Equal(EvidenceHostState.Disposed, host.State);
        Assert.Equal(0, run.Supervisor.StopRequests);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task SharedCore_AppliesLoweredStartAllowanceThroughProducerCompletion(int startAllowanceSeconds)
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            configured = true;
            registration.AddProducer(new PassingProducer());
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);

        var manifest = await run.RunAsync(host, loweredStartAllowance: TimeSpan.FromSeconds(startAllowanceSeconds));

        Assert.True(configured);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task SharedCore_RejectsOutOfBoundsStartAllowanceBeforeConfiguration(int startAllowanceSeconds)
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(plan, _ => configured = true);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.RunAsync(
            host,
            loweredStartAllowance: TimeSpan.FromSeconds(startAllowanceSeconds)));

        Assert.Equal("loweredStartAllowance", exception.ParamName);
        Assert.False(configured);
    }

    private static EvidencePlan CreatePlan() => new(
        "1.0",
        "policy",
        "policy-digest",
        "diff-digest",
        new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [], [], []),
        [],
        [],
        "plan-digest");

    private sealed class PassingProducer : IEvidenceProducer
    {
        public string Id => "inventory";

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult(
                Id,
                EvidenceProducerOutcome.Passed,
                ["inventory/assertion@1"]));
    }

    private sealed class RecordingLegacyEnvelopeVerifier(EvidenceEnvelopeResult result) : IEvidenceExecutionEnvelopeVerifier
    {
        internal int Calls { get; private set; }

        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HeldProducer : IEvidenceProducer, IAsyncDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RunCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public string Id => "inventory";

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["inventory/assertion@1"]);
        }

        public ValueTask DisposeAsync()
        {
            Assert.True(Release.Task.IsCompletedSuccessfully);
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
