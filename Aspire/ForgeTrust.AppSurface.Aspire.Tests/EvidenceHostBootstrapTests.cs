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
    public void EvidenceHostException_ShouldExposeTheStableDiagnosticAndRecoveryAction()
    {
        var exception = new EvidenceHostException("ASEVD999", "Evidence collection failed.", "Register a compatible producer.");

        Assert.Equal("ASEVD999", exception.Code);
        Assert.Equal("Register a compatible producer.", exception.Fix);
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
        var context = Assert.IsType<EvidenceProducerContext>(producer.LastContext);
        Assert.Same(host.Plan, context.Plan);
        Assert.Equal("coverage", context.Producer.Id);
        Assert.Same(TimeProvider.System, context.TimeProvider);
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
    public async Task RunAsync_ShouldReturnNoClaimWhenTheRegisteredReleaseEnvelopeRejectsThePlan()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
                registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: false));
            });

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.Invalid, manifest.EnvelopeStatus);
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single(manifest.ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordUnavailableResourceAndFailedProducerOutcomes()
    {
        await using var unavailable = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new FailingResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var unavailableManifest = await unavailable.RunAsync();
        Assert.Equal(EvidenceResourceOutcome.Unavailable, Assert.Single(unavailableManifest.ResourceResults).Outcome);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(unavailableManifest.ProducerResults).Outcome);

        await using var failed = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new FailingProducer("coverage"));
            });

        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single((await failed.RunAsync()).ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCriticalProducerFailure()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new CriticalFailingProducer("coverage"));
            });

        await Assert.ThrowsAsync<OutOfMemoryException>(() => host.RunAsync());
    }

    [Fact]
    public void Registration_ShouldRequireOneDistinctEntryForEachExplicitCapability()
    {
        var registration = new EvidenceHostRegistration();
        registration.AddResource(new ReadyResource("postgres"));
        registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
        registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: true));

        Assert.Throws<InvalidOperationException>(() => registration.AddResource(new ReadyResource("postgres")));
        Assert.Throws<InvalidOperationException>(() => registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1")));
        Assert.Throws<InvalidOperationException>(() => registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: true)));
    }

    [Fact]
    public async Task RunAsync_ShouldRequireResourcesEnvelopesAndSingleExecutionExplicitly()
    {
        await using var missingResource = EvidenceHostBootstrap.Create(CreatePlan(), _ => { });
        var resourceException = await Assert.ThrowsAsync<EvidenceHostException>(() => missingResource.RunAsync());
        Assert.Contains("ASEVD302", resourceException.Message, StringComparison.Ordinal);

        await using var envelopeRequired = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            },
            new EvidenceHostOptions(RequireTrustedEnvelope: true));
        var envelopeManifest = await envelopeRequired.RunAsync();
        Assert.Equal(EvidenceEnvelopeStatus.Unavailable, envelopeManifest.EnvelopeStatus);

        await using var singleUse = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        await singleUse.RunAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => singleUse.RunAsync());
    }

    [Fact]
    public async Task RunAsync_ShouldReportCallerCancellationAndInvalidProducerOutputs()
    {
        using var cancellation = new CancellationTokenSource();
        var blockingResource = new CallerCancellableResource("postgres");
        await using var cancelled = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(blockingResource);
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var cancellationRun = cancelled.RunAsync(cancellationToken: cancellation.Token);
        await blockingResource.WaitStarted.Task;
        cancellation.Cancel();
        var cancelledManifest = await cancellationRun;
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(cancelledManifest.ProducerResults).Outcome);

        await using var wrongId = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new WrongIdProducer("coverage"));
            });
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single((await wrongId.RunAsync()).ProducerResults).Outcome);

        await using var spoofedArtifacts = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new SpoofedArtifactProducer("coverage"));
            });
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single((await spoofedArtifacts.RunAsync()).ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldProtectResourceOrderingAndRequiredArtifactIntegrity()
    {
        var cyclicPlan = CreatePlan() with
        {
            Profile = CreatePlan().Profile with
            {
                Resources =
                [
                    new EvidenceResourceDeclaration("postgres", "aspire_health", 30, ["redis"]),
                    new EvidenceResourceDeclaration("redis", "aspire_health", 30, ["postgres"]),
                ],
            },
        };
        await using var cyclic = EvidenceHostBootstrap.Create(
            cyclicPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddResource(new ReadyResource("redis"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var cycleException = await Assert.ThrowsAsync<EvidenceHostException>(() => cyclic.RunAsync());
        Assert.Contains("ASEVD304", cycleException.Message, StringComparison.Ordinal);

        var artifactPlan = CreatePlan() with
        {
            Profile = CreatePlan().Profile with
            {
                Producers =
                [
                    new EvidenceProducerDeclaration(
                        "coverage",
                        "coverage",
                        "1.0.0",
                        ["postgres"],
                        ["coverage/assertion@1"],
                        [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 16)],
                        30),
                ],
            },
        };
        await using var requiredArtifact = EvidenceHostBootstrap.Create(
            artifactPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var requiredArtifactManifest = await requiredArtifact.RunAsync();
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(requiredArtifactManifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, requiredArtifactManifest.ExecutionVerdict);
    }

    [Fact]
    public async Task RunAsync_ShouldProtectLifecycleBoundsSharedDependenciesAndCallerCancelledProducers()
    {
        var oversizedPlan = CreatePlan() with
        {
            Profile = CreatePlan().Profile with
            {
                Resources = Enumerable.Range(0, 17).Select(index => new EvidenceResourceDeclaration($"resource-{index}", "aspire_health", 30, [])).ToArray(),
            },
        };
        await using var oversized = EvidenceHostBootstrap.Create(oversizedPlan, _ => { });
        var oversizedException = await Assert.ThrowsAsync<EvidenceHostException>(() => oversized.RunAsync());
        Assert.Contains("ASEVD301", oversizedException.Message, StringComparison.Ordinal);

        var dependencyPlan = CreatePlan() with
        {
            Profile = CreatePlan().Profile with
            {
                Resources =
                [
                    new EvidenceResourceDeclaration("postgres", "aspire_health", 30, []),
                    new EvidenceResourceDeclaration("cache", "aspire_health", 30, ["postgres"]),
                    new EvidenceResourceDeclaration("search", "aspire_health", 30, ["postgres"]),
                ],
            },
        };
        var postgres = new SyncDisposableReadyResource("postgres");
        await using var dependencyHost = EvidenceHostBootstrap.Create(
            dependencyPlan,
            registration =>
            {
                registration.AddResource(postgres);
                registration.AddResource(new ReadyResource("cache"));
                registration.AddResource(new ReadyResource("search"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var dependencyManifest = await dependencyHost.RunAsync();
        Assert.Equal(EvidenceClaimKind.TargetedComplete, dependencyManifest.ClaimKind);
        Assert.Equal(1, postgres.DisposeCount);

        var missingDependencyPlan = CreatePlan() with
        {
            Profile = CreatePlan().Profile with
            {
                Resources = [new EvidenceResourceDeclaration("postgres", "aspire_health", 30, ["missing"])],
            },
        };
        await using var missingDependency = EvidenceHostBootstrap.Create(
            missingDependencyPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var missingDependencyException = await Assert.ThrowsAsync<EvidenceHostException>(() => missingDependency.RunAsync());
        Assert.Contains("ASEVD305", missingDependencyException.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        var producer = new CallerCancellableProducer("coverage");
        await using var cancelled = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(producer);
            });
        var cancelledRun = cancelled.RunAsync(cancellationToken: cancellation.Token);
        await producer.Started.Task;
        cancellation.Cancel();
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single((await cancelledRun).ProducerResults).Outcome);
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

    private sealed class FailingResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("unavailable"));
    }

    private sealed class CallerCancellableResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private class PassingProducer(string id, string assertion) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public int RunCount { get; private set; }

        public EvidenceProducerContext? LastContext { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            LastContext = context;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [assertion]));
        }
    }

    private sealed class StaticEnvelopeVerifier(bool accepted) : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceEnvelopeResult(accepted, Attested: false, accepted ? null : "Envelope rejected."));
    }

    private sealed class FailingProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<EvidenceProducerResult>(new InvalidOperationException("producer failed"));
    }

    private sealed class CriticalFailingProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<EvidenceProducerResult>(new OutOfMemoryException("critical producer failure"));
    }

    private sealed class WrongIdProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult("other", EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]));
    }

    private sealed class SpoofedArtifactProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult(
                Id,
                EvidenceProducerOutcome.Passed,
                ["coverage/assertion@1"],
                null,
                [new EvidenceArtifactResult("report", "coverage/report.txt", "text/plain", 1, new string('a', 64))]));
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

    private sealed class SyncDisposableReadyResource(string id) : ReadyResource(id), IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
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

    private sealed class CallerCancellableProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]);
        }
    }
}
