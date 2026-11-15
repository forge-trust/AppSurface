using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

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
