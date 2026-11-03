using System.Net;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAspireApplicationHttpReadinessTests
{
    [Fact]
    public void HttpReadinessCore_ValidatesArgumentsBeforeResolvingEndpoint()
    {
        var resolutions = 0;
        IEvidenceResourceReadiness Create(string id, string path, TimeSpan timeout) =>
            EvidenceAspireApplication.CreateHttpReadinessCore(id, path, timeout,
                HttpReadinessSettings.Snapshot(new()), _ => Task.CompletedTask,
                _ => { resolutions++; return Task.FromResult<string?>("http://localhost/"); },
                _ => new HttpClient(), () => ValueTask.CompletedTask, TimeProvider.System);

        Assert.Throws<ArgumentNullException>(() => Create(null!, "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => Create(" ", "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => Create("evidence", " ", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("evidence", "/health", TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new EvidenceAspireHttpReadinessOptions { Http = null! }.Snapshot());
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task ApplicationLifetime_RejectsNewRegistrationsAfterDisposalStarts()
    {
        var lifetime = new EvidenceAspireApplicationLifetime(() => ValueTask.CompletedTask);
        await lifetime.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(lifetime.ThrowIfDisposed);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Acquire());
    }

    [Fact]
    public async Task HealthAndHttpAdapters_KeepTheApplicationUntilTheFinalRegistrationDisposes()
    {
        var order = new List<string>();
        var lifetime = new EvidenceAspireApplicationLifetime(() =>
        {
            order.Add("application");
            return ValueTask.CompletedTask;
        });
        var health = new LeaseReadiness("health", lifetime.Acquire());
        var httpRegistration = lifetime.Acquire();
        var http = EvidenceAspireApplication.CreateHttpReadinessCore(
            "http", "/health", TimeSpan.FromSeconds(1), HttpReadinessSettings.Snapshot(new()),
            _ => Task.CompletedTask, _ => Task.FromResult<string?>("http://localhost/"),
            _ => new HttpClient(new OkHandler(() => order.Add("client"))), async () =>
            {
                order.Add("http-registration");
                await httpRegistration.DisposeAsync();
            }, TimeProvider.System);
        var final = new LeaseReadiness("final", lifetime.Acquire());

        await http.WaitUntilReadyAsync(CancellationToken.None);
        Assert.Equal(["client"], order);
        await health.DisposeAsync();
        Assert.Equal(["client"], order);
        await Assert.IsAssignableFrom<IAsyncDisposable>(http).DisposeAsync();
        Assert.Equal(["client", "http-registration"], order);
        await final.DisposeAsync();

        Assert.Equal(["client", "http-registration", "application"], order);
        Assert.Throws<ObjectDisposedException>(lifetime.ThrowIfDisposed);
    }

    private sealed class LeaseReadiness(string id, IAsyncDisposable lease) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id { get; } = id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class OkHandler(Action disposed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }
}
