using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceHostBootstrapTests
{
    [Fact]
    public async Task EvidenceAspireApplication_ShouldDisposePartialApplicationWhenAspireStartupFails()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(EvidenceHostBootstrapTests).Assembly.GetName().Name,
            ProjectDirectory = repoRoot,
            DisableDashboard = true,
        });

        var exception = await Record.ExceptionAsync(() => EvidenceAspireApplication.StartAsync(builder));

        Assert.NotNull(exception);
        Assert.IsNotType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task RunAsync_ShouldCloseObligationAfterExplicitResourceAndProducerRegistrations()
    {
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(["persistence"], manifest.ClosedObligationIds);
        var resourceResult = Assert.Single(manifest.ResourceResults);
        Assert.Equal(EvidenceResourceOutcome.Ready, resourceResult.Outcome);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(EvidenceHostState.Completed, host.State);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnIncompleteClaimWhenResourceDeadlineExpires()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(resourceDeadlineSeconds: 1),
            registration =>
            {
                registration.AddResource(new BlockingResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Contains("did not become ready", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldEnforceProducerDeadlineWhenProducerIgnoresCancellation()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(producerTimeoutSeconds: 1),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new IgnoringCancellationProducer("coverage"));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldRequireExplicitProducerRegistration()
    {
        var resource = new DisposableReadyResource("postgres");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration => registration.AddResource(resource));

        var exception = await Assert.ThrowsAsync<EvidenceHostException>(() => host.RunAsync());

        Assert.Contains("ASEVD303", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ShouldEmitObservationOnlyWhenRequested()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await host.RunAsync(observationOnly: true);

        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
    }

    [Fact]
    public async Task RunAsync_ShouldNotPromoteInvalidProducerToObservation()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "unexpected/assertion@1"));
            });

        var manifest = await host.RunAsync(observationOnly: true);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task RunAsync_ShouldRequireAcceptedEnvelopeForReleaseProfile()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
                registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: true));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.ReleaseComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, manifest.EnvelopeStatus);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnNoClaimWhenReleaseEnvelopeIsMissing()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.Unavailable, manifest.EnvelopeStatus);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeRegisteredResourcesAndProducersOnce()
    {
        var resource = new DisposableReadyResource("postgres");
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ShouldInvalidateCompleteClaimWhenCleanupFails()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ThrowingDisposableReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.False(manifest.Metrics.CleanupCompleted);
        Assert.Contains("cleanup failed", manifest.Metrics.CleanupDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static EvidencePlan CreatePlan(
        int resourceDeadlineSeconds = 30,
        EvidenceProfileScope scope = EvidenceProfileScope.Targeted,
        int producerTimeoutSeconds = 30) => new(
        "1.0",
        "policy",
        "policy-digest",
        "diff-digest",
        new EvidenceProfile(
            "persistence",
            scope,
            [new EvidenceResourceDeclaration("postgres", "aspire_health", resourceDeadlineSeconds, [])],
            [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", ["postgres"], ["coverage/assertion@1"], [], producerTimeoutSeconds)],
            [new EvidenceObligation("persistence", "database", "Persistence changed.", ["coverage"], "coverage/assertion@1")]),
        [new NormalizedDiffPath("src/Persistence.cs")],
        ["persistence"],
        "plan-digest");

    private class ReadyResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public int WaitCount { get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private class PassingProducer(string id, string assertion) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public int RunCount { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [assertion]));
        }
    }

    private sealed class StaticEnvelopeVerifier(bool accepted) : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceEnvelopeResult(accepted, Attested: false, accepted ? null : "Envelope rejected."));
    }

    private sealed class IgnoringCancellationProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(
                static _ => new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
    }

    private sealed class DisposableReadyResource(string id) : ReadyResource(id), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDisposableReadyResource(string id) : ReadyResource(id), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("cleanup"));
    }

    private sealed class DisposablePassingProducer(string id, string assertion) : PassingProducer(id, assertion), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
