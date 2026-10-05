using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceHostCleanupTests
{
    [Fact]
    public async Task RunAsync_ShouldBoundProducerCleanupAndInvalidateClaimWhenDisposalHangs()
    {
        var producer = new GatedProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        Task<EvidenceManifest>? run = host.RunAsync();

        try
        {
            await producer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.NotNull(manifest.Metrics.CleanupDiagnostic);
            Assert.Equal(1, producer.DisposeCount);
        }
        finally
        {
            producer.ReleaseCleanup();
            await producer.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, producer.DisposeCount);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldBoundSynchronousBlockingDisposalOnAnIsolatedInvocation()
    {
        using var cleanupGate = new ManualResetEventSlim();
        var producer = new BlockingSyncDisposableProducer("coverage", cleanupGate);
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        var run = Task.Run(() => host.RunAsync());

        try
        {
            await producer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
        }
        finally
        {
            cleanupGate.Set();
            await producer.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldSkipDisposalWhenCallbackIgnoresCancellationAndDoesNotSettle()
    {
        var producer = new CancellationIgnoringGatedProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(executionTimeout: TimeSpan.FromMilliseconds(250), cleanupTimeout: TimeSpan.FromSeconds(1)));
        Task<EvidenceManifest>? run = null;

        try
        {
            run = host.RunAsync();
            await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(EvidenceProducerOutcome.TimedOut, Assert.Single(manifest.ProducerResults).Outcome);
            Assert.Equal(0, producer.DisposeCount);
        }
        finally
        {
            producer.ReleaseCallback();
            if (run is not null)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(3));
            }

            await producer.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldCleanOtherRegistrationsWithinFairlyDividedTotalCleanupBudget()
    {
        var ordinary = new CountingProducer("ordinary");
        var hanging = new GatedProducer("hanging");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("ordinary", "hanging"),
            registration =>
            {
                registration.AddProducer(ordinary);
                registration.AddProducer(hanging);
            },
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
            timer.Stop();

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(1, ordinary.DisposeCount);
            Assert.InRange(timer.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }
        finally
        {
            hanging.ReleaseCleanup();
            await hanging.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldRemainBoundedWhenCallerCancelsDuringCleanup()
    {
        var producer = new GatedProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();
        var run = host.RunAsync(cancellationToken: cancellation.Token);

        try
        {
            await producer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
        }
        finally
        {
            producer.ReleaseCleanup();
            await producer.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldCancelProductionBeforeDisposingRegistration()
    {
        var producer = new CallerCancellableProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(executionTimeout: TimeSpan.FromSeconds(5), cleanupTimeout: TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();
        var run = host.RunAsync(cancellationToken: cancellation.Token);

        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ShouldThrowEvidenceHostExceptionWhenRegistrationDisposalHangs()
    {
        var producer = new GatedProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        Task? dispose = null;

        try
        {
            dispose = host.DisposeAsync().AsTask();
            await producer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var exception = await Assert.ThrowsAsync<EvidenceHostException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("ASEVD306", exception.Code);
            Assert.Equal(1, producer.DisposeCount);

            var repeated = await Assert.ThrowsAsync<EvidenceHostException>(() => host.DisposeAsync().AsTask());
            Assert.Equal("ASEVD306", repeated.Code);
            Assert.Equal(1, producer.DisposeCount);
        }
        finally
        {
            producer.ReleaseCleanup();
            await producer.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeRegistrationsOnceAcrossRepeatedAndConcurrentCalls()
    {
        var producer = new CountingProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));

        await Task.WhenAll(
            host.DisposeAsync().AsTask(),
            host.DisposeAsync().AsTask(),
            host.DisposeAsync().AsTask());
        await host.DisposeAsync();

        Assert.Equal(1, producer.DisposeCount);
    }

    // Value: protects=the cleanup allowance is shared by all owners; fails_when=each owner gets a fresh full budget; why_new=existing fair-slice test does not exhaust the total clock; seam=existing TimeProvider
    [Fact]
    public async Task DisposeAsync_ShouldReportExhaustedTotalBudgetBeforeStartingAnotherOwner()
    {
        var clock = new AdvancingTimeProvider();
        var remainingDisposals = 0;
        var completedDisposals = 0;
        var allowance = TimeSpan.FromSeconds(1);
        var host = EvidenceHostBootstrap.Create(CreatePlan("remaining", "completed"), registration =>
        {
            registration.AddProducer(new DisposableProducer("remaining", () => remainingDisposals++));
            registration.AddProducer(new DisposableProducer("completed", () =>
            {
                completedDisposals++;
                clock.Advance(allowance);
            }));
        }, Options(cleanupTimeout: allowance), clock);

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => host.DisposeAsync().AsTask());

        Assert.Equal("ASEVD306", failure.Code);
        Assert.Contains("before all registrations settled", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, completedDisposals);
        Assert.Equal(0, remainingDisposals);
        var repeated = await Assert.ThrowsAsync<EvidenceHostException>(() => host.DisposeAsync().AsTask());
        Assert.Equal("ASEVD306", repeated.Code);
        Assert.Equal(1, completedDisposals);
        Assert.Equal(0, remainingDisposals);
    }

    // Value: protects=bounded payload-free cleanup diagnostics; fails_when=raw identifiers or exception payloads are emitted; why_new=existing safe-error test uses a short plain identifier; seam=none
    [Fact]
    public async Task DisposeAsync_ShouldBoundAndSanitizeRegistrationDiagnosticWithoutExceptionPayload()
    {
        var id = "AZaz09-_.\n/é" + new string('x', 90) + "private-id-tail";
        var safeId = "AZaz09-_.___" + new string('x', 68);
        var host = EvidenceHostBootstrap.Create(CreatePlan(id), registration =>
            registration.AddProducer(new DisposableProducer(id, () => throw new InvalidOperationException("private-error-payload"))));

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => host.DisposeAsync().AsTask());

        Assert.Equal("ASEVD306", failure.Code);
        Assert.Equal($"Evidence cleanup failed for producer '{safeId}' with InvalidOperationException.", host.CleanupDiagnostic);
        Assert.DoesNotContain("private-id-tail", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-error-payload", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', host.CleanupDiagnostic!);
    }

    // Value: protects=one owner registered in two roles is disposed once after both callbacks; fails_when=cleanup deduplicates by role or loses a callback; why_new=existing repeated-dispose test uses one role; seam=none
    [Fact]
    public async Task RunAsync_ShouldJoinBothCallbacksAndDisposeSharedRegistrationOnce()
    {
        var owner = new SharedResourceProducer();
        var plan = CreatePlanWithResources([owner.Id], [new EvidenceResourceDeclaration(owner.Id, "completion", 30, [])], [owner.Id]);
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(owner);
            registration.AddProducer(owner);
        });

        var manifest = await host.RunAsync();

        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(1, owner.ReadinessCount);
        Assert.Equal(1, owner.ProductionCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    // Value: protects=direct disposal cannot wait indefinitely for an active run; fails_when=execution semaphore is awaited without its deadline; why_new=existing active-run disposal succeeds; seam=existing TimeProvider
    [Fact]
    public async Task DisposeAsync_ShouldBoundJoiningActiveRunWithoutDisposingUnsettledCallback()
    {
        var allowance = TimeSpan.FromMinutes(4);
        var clock = new CapturedTimerTimeProvider(allowance);
        var producer = new StopCapableGatedProducer("coverage", releaseOnStop: false);
        var host = EvidenceHostBootstrap.Create(CreatePlan(producer.Id), registration => registration.AddProducer(producer),
            Options(executionTimeout: TimeSpan.FromMinutes(5), cleanupTimeout: allowance), clock);
        var run = host.RunAsync();
        try
        {
            await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var disposal = host.DisposeAsync().AsTask();
            var expireDisposalDeadline = await clock.FirstDeadline.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await producer.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(EvidenceHostState.Cleaning, host.State);
            expireDisposalDeadline();
            var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.Equal("ASEVD306", failure.Code);
            Assert.Contains("could not join", failure.Message, StringComparison.Ordinal);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, producer.DisposeCount);
        }
        finally
        {
            producer.ReleaseCallback();
            await producer.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task CleanAsync_ShouldRetainDependencyOfFailedOwnerAndStillDisposeUnrelatedResource()
    {
        var dependent = new DisposableReadyResource("dependent");
        var unrelated = new DisposableReadyResource("unrelated");
        var producer = new GatedProducer("coverage");
        var plan = CreatePlanWithResources(
            ["coverage"],
            [
                new EvidenceResourceDeclaration("dependent", "completion", 30, []),
                new EvidenceResourceDeclaration("unrelated", "completion", 30, []),
            ],
            ["dependent"]);
        var host = EvidenceHostBootstrap.Create(
            plan,
            registration =>
            {
                registration.AddResource(dependent);
                registration.AddResource(unrelated);
                registration.AddProducer(producer);
            },
            Options(cleanupTimeout: TimeSpan.FromSeconds(1)));

        try
        {
            var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(1, producer.DisposeCount);
            Assert.Equal(0, dependent.DisposeCount);
            Assert.Equal(1, unrelated.DisposeCount);
        }
        finally
        {
            producer.ReleaseCleanup();
            await producer.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    // Value: protects=unsettled resource dependencies survive opposite registration order; fails_when=cleanup reverses registration only; why_new=existing retention test covers producers; seam=none
    [Fact]
    public async Task CleanAsync_ShouldRetainResourceDependencyWhenRegisteredAfterItsDependent()
    {
        var dependent = new GatedResource("dependent");
        var prerequisite = new DisposableReadyResource("prerequisite");
        var transitive = new DisposableReadyResource("transitive");
        var unrelated = new DisposableReadyResource("unrelated");
        var plan = CreatePlanWithResources(["coverage"],
            [new EvidenceResourceDeclaration("dependent", "completion", 30, ["prerequisite"]),
             new EvidenceResourceDeclaration("prerequisite", "completion", 30, ["transitive"]),
             new EvidenceResourceDeclaration("transitive", "completion", 30, []),
             new EvidenceResourceDeclaration("unrelated", "completion", 30, [])], ["dependent"]);
        var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(dependent);
            registration.AddResource(prerequisite);
            registration.AddResource(transitive);
            registration.AddResource(unrelated);
            registration.AddProducer(new CountingProducer("coverage"));
        }, Options(cleanupTimeout: TimeSpan.FromSeconds(1)));
        try
        {
            var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.Equal(0, prerequisite.DisposeCount);
            Assert.Equal(0, transitive.DisposeCount);
            Assert.Equal(1, unrelated.DisposeCount);
        }
        finally
        {
            dependent.Gate.TrySetResult();
            await dependent.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopAndJoinLifetimeBeforeOrdinaryDisposal()
    {
        var producer = new StopCapableGatedProducer("coverage");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan("coverage"),
            registration => registration.AddProducer(producer),
            Options(executionTimeout: TimeSpan.FromSeconds(5), cleanupTimeout: TimeSpan.FromSeconds(1)));
        var run = host.RunAsync();

        try
        {
            await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.True(manifest.Metrics.CleanupCompleted);
            Assert.Equal(1, producer.StopCount);
            Assert.Equal(1, producer.DisposeCount);
        }
        finally
        {
            producer.ReleaseCallback();
            await producer.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    // Value: protects=failed stop cannot claim cleanup or start disposal; fails_when=stop errors are swallowed or disposal runs anyway; why_new=existing lifetime test stops successfully; seam=none
    [Fact]
    public async Task RunAsync_ShouldInvalidateCleanupAndSkipDisposalWhenExplicitStopFails()
    {
        var producer = new FailingStopProducer();
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(producer.Id), registration => registration.AddProducer(producer));

        var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.False(manifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Contains("InvalidOperationException", manifest.Metrics.CleanupDiagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-stop-payload", manifest.Metrics.CleanupDiagnostic!, StringComparison.Ordinal);
        Assert.Equal(1, producer.StopCount);
        Assert.Equal(0, producer.DisposeCount);
        await host.DisposeAsync();
        Assert.Equal(1, producer.StopCount);
        Assert.Equal(0, producer.DisposeCount);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    public void Create_ShouldRejectNonPositiveLifecycleTimeouts(bool executionTimeout, int milliseconds)
    {
        var invalidTimeout = TimeSpan.FromMilliseconds(milliseconds);
        var options = executionTimeout
            ? Options(executionTimeout: invalidTimeout)
            : Options(cleanupTimeout: invalidTimeout);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvidenceHostBootstrap.Create(CreatePlan("coverage"), registration => registration.AddProducer(new GatedProducer("coverage")), options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ShouldBoundBlockingAndThrowingCancellationHandlers(bool throws)
    {
        using var gate = new ManualResetEventSlim();
        var producer = new CancellationHandlerProducer(gate, throws);
        var host = EvidenceHostBootstrap.Create(CreatePlan("coverage"), registration => registration.AddProducer(producer),
            Options(executionTimeout: TimeSpan.FromMilliseconds(100), cleanupTimeout: TimeSpan.FromMilliseconds(200)));
        Task<EvidenceManifest>? run = null;
        try
        {
            run = host.RunAsync();
            await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(EvidenceProducerOutcome.TimedOut, Assert.Single(manifest.ProducerResults).Outcome);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.Equal(throws, manifest.Metrics.CleanupCompleted);
            Assert.Equal(throws ? 1 : 0, producer.DisposeCount);
        }
        finally
        {
            gate.Set();
            await producer.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldBoundHangingEnvelopeValidation()
    {
        var verifier = new GatedVerifier();
        var host = EvidenceHostBootstrap.Create(CreatePlan("coverage"), registration =>
        {
            registration.AddProducer(new CountingProducer("coverage"));
            registration.SetEnvelopeVerifier(verifier);
        }, Options(executionTimeout: TimeSpan.FromMilliseconds(100), cleanupTimeout: TimeSpan.FromMilliseconds(200)) with { RequireTrustedEnvelope = true });
        try
        {
            var manifest = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(EvidenceProducerOutcome.TimedOut, Assert.Single(manifest.ProducerResults).Outcome);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        }
        finally
        {
            verifier.Gate.TrySetResult();
            await verifier.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_ShouldPreservePrimaryFailureWhenErrorUnwindCleanupFails()
    {
        var producer = new PrimaryFailureProducer();
        await using var host = EvidenceHostBootstrap.Create(CreatePlan("coverage"), registration => registration.AddProducer(producer),
            Options(cleanupTimeout: TimeSpan.FromMilliseconds(100)));
        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() => host.RunAsync());
        Assert.Equal("fixture-primary", error.Message);
        Assert.NotNull(host.CleanupDiagnostic);
        Assert.Contains("InvalidOperationException", host.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-cleanup", host.CleanupDiagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_ShouldRejectOversizedLifecycleBudget(bool execution)
    {
        var tooLarge = TimeSpan.FromMilliseconds(int.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceHostBootstrap.Create(CreatePlan("coverage"), _ => { },
            execution ? Options(executionTimeout: tooLarge) : Options(cleanupTimeout: tooLarge)));
    }

    private static EvidenceHostOptions Options(TimeSpan? executionTimeout = null, TimeSpan? cleanupTimeout = null) => new()
    {
        ExecutionTimeout = executionTimeout ?? TimeSpan.FromHours(1),
        CleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(30),
    };

    private static EvidencePlan CreatePlan(params string[] producerIds)
        => CreatePlanWithResources(producerIds, [], []);

    private static EvidencePlan CreatePlanWithResources(
        string[] producerIds,
        IReadOnlyList<EvidenceResourceDeclaration> resources,
        IReadOnlyList<string> requiredResources)
    {
        var producers = producerIds.Select(id => new EvidenceProducerDeclaration(
            id,
            id,
            "1.0.0",
            requiredResources,
            [$"{id}/assertion@1"],
            [],
            30)).ToArray();
        var obligations = producerIds.Length == 0
            ? []
            : new[] { new EvidenceObligation("persistence", "database", "Persistence changed.", [producerIds[0]], $"{producerIds[0]}/assertion@1") };

        return new EvidencePlan(
        "1.0",
        "policy",
        "policy-digest",
        "diff-digest",
        new EvidenceProfile(
            "persistence",
            EvidenceProfileScope.Targeted,
            resources,
            producers,
            obligations),
        [new NormalizedDiffPath("src/Persistence.cs")],
        obligations.Select(static obligation => obligation.Id).ToArray(),
        "plan-digest");
    }

    private sealed class CancellationHandlerProducer(ManualResetEventSlim gate, bool throws) : PassingProducer("coverage"), IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandlerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public override async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (throws) throw new InvalidOperationException("fixture-sensitive-data");
                    gate.Wait();
                }
                finally { HandlerFinished.TrySetResult(); }
            });
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ProduceAsync(context, cancellationToken);
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class GatedVerifier : IEvidenceExecutionEnvelopeVerifier
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
        {
            try { await Gate.Task; return new EvidenceEnvelopeResult(true, false); }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class PrimaryFailureProducer() : PassingProducer("coverage"), IDisposable
    {
        public override ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<EvidenceProducerResult>(new OutOfMemoryException("fixture-primary"));
        public void Dispose() => throw new InvalidOperationException("fixture-cleanup");
    }

    private class PassingProducer(string id) : IEvidenceProducer
    {
        public virtual string Id { get; } = id;

        public virtual ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]));
    }

    private sealed class DisposableProducer(string id, Action dispose) : PassingProducer(id), IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class FailingStopProducer() : PassingProducer("coverage"), IEvidenceExecutionLifetime, IDisposable
    {
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return ValueTask.FromException(new InvalidOperationException("private-stop-payload"));
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private sealed class CapturedTimerTimeProvider(TimeSpan dueTimeToCapture) : TimeProvider
    {
        public TaskCompletionSource<Action> FirstDeadline { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = System.CreateTimer(callback, state, dueTime, period);
            if (dueTime == dueTimeToCapture) FirstDeadline.TrySetResult(() => callback(state));
            return timer;
        }
    }

    private sealed class SharedResourceProducer() : PassingProducer("shared"), IEvidenceResourceReadiness, IDisposable
    {
        public int ReadinessCount { get; private set; }
        public int ProductionCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            ReadinessCount++;
            return Task.CompletedTask;
        }
        public override ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ProductionCount++;
            return base.ProduceAsync(context, cancellationToken);
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class GatedProducer(string id) : PassingProducer(id), IAsyncDisposable
    {
        private readonly TaskCompletionSource _cleanupGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            CleanupStarted.TrySetResult();
            try
            {
                await _cleanupGate.Task.ConfigureAwait(false);
            }
            finally
            {
                CleanupFinished.TrySetResult();
            }
        }

        public void ReleaseCleanup() => _cleanupGate.TrySetResult();
    }

    private sealed class BlockingSyncDisposableProducer(string id, ManualResetEventSlim cleanupGate) : PassingProducer(id), IDisposable
    {
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            CleanupStarted.TrySetResult();
            try
            {
                cleanupGate.Wait();
            }
            finally
            {
                CleanupFinished.TrySetResult();
            }
        }
    }

    private sealed class CancellationIgnoringGatedProducer(string id) : PassingProducer(id), IAsyncDisposable
    {
        private readonly TaskCompletionSource _callbackGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public override async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await _callbackGate.Task.ConfigureAwait(false);
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]);
            }
            finally
            {
                CallbackFinished.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void ReleaseCallback() => _callbackGate.TrySetResult();
    }

    private sealed class CallerCancellableProducer(string id) : PassingProducer(id), IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public override async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingProducer(string id) : PassingProducer(id), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedResource(string id) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id { get; } = id;
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            try { await Gate.Task; }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class DisposableReadyResource(string id) : IEvidenceResourceReadiness, IDisposable
    {
        public string Id { get; } = id;

        public int DisposeCount { get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => DisposeCount++;
    }

    private sealed class StopCapableGatedProducer(string id, bool releaseOnStop = true) : PassingProducer(id), IEvidenceExecutionLifetime, IAsyncDisposable
    {
        private readonly TaskCompletionSource _callbackGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await _callbackGate.Task.ConfigureAwait(false);
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]);
            }
            finally
            {
                CallbackFinished.TrySetResult();
            }
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            StopStarted.TrySetResult();
            if (releaseOnStop) _callbackGate.TrySetResult();
            await CallbackFinished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void ReleaseCallback() => _callbackGate.TrySetResult();
    }
}
