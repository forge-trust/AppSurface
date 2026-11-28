using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceHostPreparationOwnershipTests
{
    [Fact]
    public async Task ScopeDisposal_ShouldJoinActualApplicationCleanupOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ApplicationOwner(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var resource = new OwnedReadiness("database");
        var scope = new EvidenceHostResourceScope([resource], owner);

        var first = scope.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = scope.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, resource.Disposals);
        release.SetResult();
        await Task.WhenAll(first, second);
        await scope.DisposeAsync();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ReentrantScopeDisposal_ShouldReuseThePublishedCleanupTask()
    {
        EvidenceHostResourceScope? scope = null;
        var owner = new ReentrantOwner(() => scope!.DisposeAsync().AsTask());
        scope = new EvidenceHostResourceScope([], owner);

        var first = scope.DisposeAsync().AsTask();
        await owner.Reentered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = scope.DisposeAsync().AsTask();

        Assert.Same(first, second);
        Assert.Same(first, owner.ReentrantCleanup);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ThrowingFactoryCancellationCallback_ShouldReturnCallerCancellationAndRetainCallbackFailure()
    {
        using var fixture = new PreparedRunFixture();
        var producer = new PreparedProducer("check");
        var prepared = await fixture.PrepareAsync(producer);
        using var callerCancellation = new CancellationTokenSource();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("controlled factory cancellation callback failure");

        var execution = prepared.RunAsync(async (_, factoryCancellationToken) =>
        {
            using var registration = factoryCancellationToken.Register(() => throw callbackFailure);
            factoryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, factoryCancellationToken);
            return new EvidenceHostResourceScope([], new PreparedOwner());
        }, cancellationToken: callerCancellation.Token);

        EvidenceManifest result;
        Exception? cancellationFailure;
        Exception? cleanupFailure;
        try
        {
            await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellationFailure = Record.Exception(callerCancellation.Cancel);
            result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cleanupFailure = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());
        }

        Assert.Null(cancellationFailure);
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(result.ProducerResults).Outcome);
        var retainedFailure = Assert.IsType<AggregateException>(cleanupFailure);
        Assert.Contains(callbackFailure, retainedFailure.Flatten().InnerExceptions);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task CallerCancellationDuringOwnerDisposal_ShouldReturnCancelledWithCompletedCleanup()
    {
        using var fixture = new PreparedRunFixture();
        var producer = new PreparedProducer("check");
        await using var prepared = await fixture.PrepareAsync(producer);
        using var callerCancellation = new CancellationTokenSource();
        var owner = new PreparedOwner(callerCancellation.Cancel);

        var result = await prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([new PreparedReadiness("database")], owner)),
            cancellationToken: callerCancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(result.ProducerResults).Outcome);
        Assert.True(result.Metrics.CleanupCompleted);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, producer.Runs);
    }

    [Fact]
    public async Task BlockingSynchronousProducerDisposal_ShouldRespectGraceAndJoinOnDisposeAsync()
    {
        using var fixture = new PreparedRunFixture();
        var producer = new BlockingSynchronousProducer("check");
        await using var prepared = await fixture.PrepareAsync(producer, requiresResource: false,
            cleanupTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            var execution = Task.Run(() => prepared.RunAsync((_, _) => throw new InvalidOperationException(
                "The profile requires no resources.")));
            await producer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
            Assert.False(result.Metrics.CleanupCompleted);
            Assert.Contains("reuse.cleanup_unsettled", result.Metrics.CleanupDiagnostic, StringComparison.Ordinal);

            var disposal = prepared.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            producer.ReleaseDisposal.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            producer.ReleaseDisposal.TrySetResult();
        }

        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public void DuplicateBindings_ShouldFailBeforeOwnershipTransfer()
    {
        var first = new OwnedReadiness("database");
        var second = new OwnedReadiness("database");
        var owner = new ApplicationOwner(() => Task.CompletedTask);

        Assert.Throws<ArgumentException>(() => new EvidenceHostResourceScope([first, second], owner));

        Assert.Equal(0, first.Disposals);
        Assert.Equal(0, second.Disposals);
        Assert.Equal(0, owner.Disposals);
    }

    [Fact]
    public async Task FatalReadinessDisposal_ShouldPreserveFailureAndJoinApplicationOwner()
    {
        var fatal = new OutOfMemoryException("controlled disposal failure");
        var readiness = new OwnedReadiness("database", fatal);
        var owner = new ApplicationOwner(() => Task.CompletedTask);
        var scope = new EvidenceHostResourceScope([readiness], owner);

        var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() => scope.DisposeAsync().AsTask());

        Assert.Same(fatal, observed);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, readiness.Disposals);
    }

    [Fact]
    public void FrozenRegistration_ShouldCopyToolIdentityAndRejectLaterMutation()
    {
        var tools = new Dictionary<string, string> { ["sdk"] = "10.0.402" };
        var registration = new EvidenceHostPreparationRegistration();
        registration.AddProducer(new Producer("tests"), "tests@1", tools);
        registration.AddResource("database", "fixture-sha256");
        var frozen = registration.Freeze();

        tools["sdk"] = "different";

        Assert.Equal("10.0.402", frozen["tests"].ToolIdentity["sdk"]);
        Assert.Throws<InvalidOperationException>(() => registration.AddResource("browser", "browser@1"));
        Assert.Throws<InvalidOperationException>(() => registration.AddProducer(new Producer("other"), "other@1"));
        Assert.Throws<InvalidOperationException>(() => registration.SetEnvelopeVerifier(new EnvelopeVerifier()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EagerReuse_ShouldRejectBeforeRegistrationSideEffects(bool reuse, bool audit)
    {
        var configured = false;
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [], []);
        var plan = new EvidencePlan("1.0", "policy", "", "", profile, [], [], "");

        var failure = Assert.Throws<EvidenceHostException>(() => EvidenceHostBootstrap.Create(
            plan, _ => configured = true, new EvidenceHostOptions { Reuse = reuse, VerifyReuse = audit }));

        Assert.Equal("reuse.preparation_required", failure.Code);
        Assert.False(configured);
    }

    private sealed class ApplicationOwner(Func<Task> dispose) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await dispose();
        }
    }

    private sealed class ReentrantOwner(Func<Task> reenter) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal TaskCompletionSource Reentered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? ReentrantCleanup { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            ReentrantCleanup = reenter();
            Reentered.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreparedRunFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("prepared-host-ownership-").FullName;
        private string Repository => TestPathUtils.PathUnder(_root, "repo");

        internal PreparedRunFixture()
        {
            Directory.CreateDirectory(Repository);
        }

        internal Task<EvidencePreparedRun> PrepareAsync(IEvidenceProducer producer, bool requiresResource = true,
            TimeSpan? cleanupTimeout = null)
        {
            var resources = requiresResource
                ? new[] { new EvidenceResourceDeclaration("database", "completion", 30, []) }
                : [];
            var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, resources,
                [new EvidenceProducerDeclaration("check", "custom", "1", requiresResource ? ["database"] : [],
                    ["valid"], [], 30)],
                [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
            var plan = new EvidencePlanner().Resolve(
                new("fixture", "1", "local", [profile], [new("all", "**", "local")]),
                [new NormalizedDiffPath("input.txt")]);

            return EvidenceHostBootstrap.PrepareAsync(plan, registration =>
            {
                registration.AddProducer(producer, "fixture-v1");
                foreach (var resource in resources) registration.AddResource(resource.Id, $"{resource.Id}@sha256:fixture");
            }, new EvidenceHostOptions
            {
                Reuse = false,
                ProducerCleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(10),
                RepositoryRoot = Repository,
                ArtifactDirectory = TestPathUtils.PathUnder(_root, "artifacts"),
                EnvironmentReader = _ => null,
            });
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class PreparedProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public string Id => id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreparedReadiness(string id) : IEvidenceResourceReadiness
    {
        public string Id => id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PreparedOwner(Action? onDispose = null) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSynchronousProducer(string id) : IEvidenceProducer, IDisposable
    {
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals { get; private set; }
        public string Id => id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));

        public void Dispose()
        {
            Disposals++;
            DisposalStarted.TrySetResult();
            ReleaseDisposal.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class OwnedReadiness(string id, Exception? failure = null) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id => id;
        internal int Disposals { get; private set; }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class Producer(string id) : IEvidenceProducer
    {
        public string Id => id;
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, []));
    }

    private sealed class EnvelopeVerifier : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EvidenceEnvelopeResult(true, false));
    }
}
