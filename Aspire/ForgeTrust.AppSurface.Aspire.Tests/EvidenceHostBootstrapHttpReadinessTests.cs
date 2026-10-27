using System.Net;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed partial class EvidenceHostBootstrapTests
{
    [Fact]
    public async Task HostResourceDeadline_WithIgnoringHttpHandler_DrainsClientBeforeLeaseAndWithholdsClaim()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var handler = new IgnoringReadinessHandler(entered, releaseResponse, () => order.Add("client"));
        var readiness = EvidenceAspireApplication.CreateHttpReadinessCore(
            "postgres", "/healthz", TimeSpan.FromSeconds(30), HttpReadinessSettings.Snapshot(new()),
            _ => Task.CompletedTask, _ => Task.FromResult<string?>("http://localhost/"),
            _ => new HttpClient(handler), () => { order.Add("lease"); return ValueTask.CompletedTask; }, time);
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(resourceDeadlineSeconds: 1), registration =>
        {
            registration.AddResource(new ObservedCleanupReadiness(readiness, cleanupStarted));
            registration.AddProducer(producer);
        }, timeProvider: time);

        var run = host.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(1));
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted);
        Assert.Empty(order);
        releaseResponse.SetResult();
        var manifest = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(["client", "lease"], order);
        Assert.Equal(EvidenceResourceOutcome.TimedOut, Assert.Single(manifest.ResourceResults).Outcome);
    }

    // Value: a fatal adapter cleanup failure cannot become a nonfatal host diagnostic or manifest claim.
    [Fact]
    public async Task FatalAdapterCleanup_PropagatesOriginalFailureThroughEvidenceHost()
    {
        var fatal = new OutOfMemoryException();
        var readiness = EvidenceAspireApplication.CreateHttpReadinessCore(
            "postgres", "/healthz", TimeSpan.FromSeconds(30), HttpReadinessSettings.Snapshot(new()),
            _ => Task.FromException(new InvalidOperationException("resource not ready")),
            _ => throw new InvalidOperationException("Endpoint must not be resolved."),
            _ => throw new InvalidOperationException("Client must not be created."),
            () => throw new AggregateException(new AggregateException(fatal)), TimeProvider.System);
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
        {
            registration.AddResource(readiness);
            registration.AddProducer(producer);
        });

        Assert.Same(fatal, await Record.ExceptionAsync(() => host.RunAsync()));
        Assert.Equal(0, producer.RunCount);
    }

    private sealed class ObservedCleanupReadiness(IEvidenceResourceReadiness inner, TaskCompletionSource cleaning) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id => inner.Id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => inner.WaitUntilReadyAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            cleaning.TrySetResult();
            return ((IAsyncDisposable)inner).DisposeAsync();
        }
    }

    private sealed class IgnoringReadinessHandler(TaskCompletionSource entered, TaskCompletionSource response, Action disposed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await response.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }
}
