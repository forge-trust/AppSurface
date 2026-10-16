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
        var releaseResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        releaseResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        var manifest = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(["client", "lease"], order);
        Assert.Equal(EvidenceResourceOutcome.TimedOut, Assert.Single(manifest.ResourceResults).Outcome);
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

    private sealed class IgnoringReadinessHandler(TaskCompletionSource entered, TaskCompletionSource<HttpResponseMessage> response, Action disposed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            return response.Task;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }
}
