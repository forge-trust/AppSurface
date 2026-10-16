using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAspireHttpReadinessTests
{
    [Fact]
    public async Task SequentialAndConcurrentWaits_OwnIndependentClients_ThenDisposeLeaseOnce()
    {
        var clients = 0;
        var disposed = 0;
        var lease = 0;
        var adapter = Create(client: _ => { Interlocked.Increment(ref clients); return Client(dispose: () => Interlocked.Increment(ref disposed)); },
            cleanup: () => { lease++; return ValueTask.CompletedTask; });
        await adapter.WaitUntilReadyAsync(default);
        await Task.WhenAll(adapter.WaitUntilReadyAsync(default), adapter.WaitUntilReadyAsync(default));
        Assert.Equal(3, clients);
        Assert.Equal(3, disposed);
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        Assert.Equal(1, lease);
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.WaitUntilReadyAsync(default); });
    }

    [Fact]
    public async Task DisposalClosesAdmissionAndDrainsIgnoringWait_ClientBeforeLease_SharedFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var failure = new InvalidOperationException("lease cleanup failed");
        Task? wait = null;
        var adapter = Create(client: _ => Client((_, _) => { entered.SetResult(); return response.Task; }, () => order.Add("client")),
            cleanup: () => { Assert.True(wait!.IsCompleted); order.Add("lease"); throw failure; });
        wait = adapter.WaitUntilReadyAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = adapter.DisposeAsync().AsTask();
        var second = adapter.DisposeAsync().AsTask();
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.WaitUntilReadyAsync(default); });
        Assert.False(first.IsCompleted);
        Assert.Empty(order);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.DisposeAsync().AsTask()));
        Assert.Equal(["client", "lease"], order);
    }

    [Fact]
    public async Task ReentrantCleanupReturnsButReadinessCallbackCannotDisposeItsOwner()
    {
        EvidenceAspireHttpReadiness? adapter = null;
        var rejected = 0;
        adapter = Create(health: _ =>
        {
            Assert.Throws<InvalidOperationException>(() => adapter!.DisposeAsync());
            rejected++;
            return Task.CompletedTask;
        }, cleanup: async () => await adapter!.DisposeAsync());
        await adapter.WaitUntilReadyAsync(default);
        Assert.Equal(1, rejected);
        await adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("health")]
    [InlineData("endpoint")]
    [InlineData("client")]
    public async Task OriginalCallerCancellationWinsOverDeadlineInEveryPhase(string phase)
    {
        var time = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        void Cancel() { time.Advance(TimeSpan.FromSeconds(5)); caller.Cancel(); }
        var adapter = Create(time: time, timeout: TimeSpan.FromSeconds(5),
            health: _ => { if (phase == "health") Cancel(); return Task.CompletedTask; },
            endpoint: _ => { if (phase == "endpoint") Cancel(); return Task.FromResult<string?>("http://localhost/"); },
            client: _ => { if (phase == "client") { Cancel(); throw new OperationCanceledException(); } return Client(); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.WaitUntilReadyAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhaseDeadlineIsSafeAndClientIsNotCreatedBeforeHealth(bool endpointPhase)
    {
        var time = new FakeTimeProvider();
        var clients = 0;
        Task Expire(CancellationToken _) { time.Advance(TimeSpan.FromSeconds(5)); return Task.CompletedTask; }
        var adapter = Create(time: time, timeout: TimeSpan.FromSeconds(5), health: endpointPhase ? _ => Task.CompletedTask : Expire,
            endpoint: _ => { time.Advance(TimeSpan.FromSeconds(5)); return Task.FromResult<string?>("http://localhost/"); },
            client: _ => { clients++; return Client(); });
        var error = await Assert.ThrowsAsync<ReadinessException>(() => adapter.WaitUntilReadyAsync(default));
        Assert.Equal(endpointPhase ? ReadinessPhase.EndpointResolution : ReadinessPhase.ResourceHealth, error.Phase);
        Assert.Equal(ReadinessFailureCategory.Deadline, error.Category);
        Assert.Equal(0, clients);
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/healthz")]
    [InlineData("ftp://sensitive.invalid/")]
    public async Task InvalidAllocatedEndpointDoesNotCreateClientOrLeakValues(string? value)
    {
        var clients = 0;
        var adapter = Create(endpoint: _ => Task.FromResult(value), client: _ => { clients++; return Client(); });
        var error = await Assert.ThrowsAsync<ReadinessException>(() => adapter.WaitUntilReadyAsync(default));
        Assert.Equal(ReadinessFailureCategory.EndpointUnavailable, error.Category);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Equal(0, clients);
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCleanupFailure_IsSafeAndPreservesPrimary(bool primaryFails)
    {
        var primary = new InvalidOperationException("original callback failure");
        var adapter = Create(client: _ => Client((_, _) => primaryFails
            ? Task.FromException<HttpResponseMessage>(primary)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), () => throw new InvalidOperationException("raw-cleanup-secret")));
        var error = await Record.ExceptionAsync(() => adapter.WaitUntilReadyAsync(default));
        if (primaryFails)
        {
            Assert.Same(primary, error);
            Assert.Equal(ReadinessFailureCategory.CleanupFailure, error!.Data["AppSurface.Readiness.CleanupFailure"]);
        }
        else Assert.Equal(ReadinessPhase.Cleanup, Assert.IsType<ReadinessException>(error).Phase);
        Assert.DoesNotContain("raw-cleanup-secret", error!.ToString());
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task PreCanceledWaitAndNullClientNeverSucceed()
    {
        var health = 0;
        var adapter = Create(health: _ => { health++; return Task.CompletedTask; }, client: _ => null!);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.WaitUntilReadyAsync(caller.Token));
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Equal(0, health);
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.WaitUntilReadyAsync(default));
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-resource")]
    [InlineData("wrong-kind")]
    [InlineData("missing-endpoint")]
    [InlineData("duplicate-endpoint")]
    [InlineData("non-http")]
    [InlineData("valid")]
    public void NativeModelSelectionRequiresOneExactHttpEndpoint(string shape)
    {
        var web = new ExecutableResource("web", "dotnet", ".");
        if (shape != "missing-endpoint") web.Annotations.Add(new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: shape == "non-http" ? "tcp" : "http", name: "http"));
        if (shape == "duplicate-endpoint") web.Annotations.Add(new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http"));
        IEnumerable<IResource> resources = shape switch
        {
            "missing" => [],
            "duplicate-resource" => [web, new ExecutableResource("web", "dotnet", ".")],
            "wrong-kind" => [new PlainResource("web")],
            _ => [web],
        };
        if (shape == "duplicate-resource")
        {
            Assert.Throws<DistributedApplicationException>(() => new DistributedApplicationModel(resources));
            return;
        }

        var model = new DistributedApplicationModel(resources);
        if (shape == "valid") Assert.Equal("http", EvidenceAspireApplication.ResolveEndpointReference(model, "web", "http").EndpointName);
        else Assert.Equal(ReadinessFailureCategory.EndpointUnavailable,
            Assert.Throws<ReadinessException>(() => EvidenceAspireApplication.ResolveEndpointReference(model, "web", "http")).Category);
    }

    private static EvidenceAspireHttpReadiness Create(
        Func<CancellationToken, Task>? health = null,
        Func<CancellationToken, Task<string?>>? endpoint = null,
        Func<Uri, HttpClient>? client = null,
        Func<ValueTask>? cleanup = null,
        TimeProvider? time = null,
        TimeSpan? timeout = null) => new("web", "/healthz", timeout ?? TimeSpan.FromSeconds(30),
            HttpReadinessSettings.Snapshot(new()), health ?? (_ => Task.CompletedTask),
            endpoint ?? (_ => Task.FromResult<string?>("http://localhost/")), client ?? (_ => Client()),
            cleanup ?? (() => ValueTask.CompletedTask), time ?? TimeProvider.System);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null, Action? dispose = null) =>
        new(new Handler(send ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), dispose));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Action? dispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) dispose?.Invoke(); base.Dispose(disposing); }
    }
    private sealed class PlainResource(string name) : Resource(name);
}
