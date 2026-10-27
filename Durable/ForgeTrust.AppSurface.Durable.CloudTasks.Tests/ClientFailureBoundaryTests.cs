using ForgeTrust.AppSurface.Durable.CloudTasks;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

// protects: sanitization and cancellation provenance at each invocation phase.
// fails_when: source failure races become provider submissions or uncertain evidence is rewritten.
// why_new: these are phase/race branches with different recovery claims; seam: intentional transport/source interfaces.
// no_claim: token cryptographic validity or live provider acceptance.
public sealed class ClientFailureBoundaryTests
{
    private static readonly CloudTasksActivationRequest Request = new(new("project", "us-central1", "queue", new Uri("https://worker.example/"), "worker@example.com", "worker", TimeSpan.FromSeconds(15)), "task");

    [Fact]
    public async Task Expired_source_failure_and_post_token_caller_cancellation_prevent_rpc()
    {
        var clock = new FakeTimeProvider();
        var rpc = new Rpc((_, _) => Task.FromResult<string?>(Request.TaskName));
        var expired = new CloudTasksActivationClient(new Source(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return ValueTask.FromException<string>(new OperationCanceledException("secret"));
        }), new(TimeSpan.FromSeconds(1)), rpc, clock);
        Assert.Equal("ASCT103", (await expired.DispatchAsync(Request)).DiagnosticCode);
        Assert.Equal(0, rpc.Calls);
        using var canceled = new CancellationTokenSource();
        var client = new CloudTasksActivationClient(new Source(_ =>
        {
            canceled.Cancel();
            return ValueTask.FromResult("token");
        }), new(TimeSpan.FromSeconds(1)), rpc, TimeProvider.System);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => client.DispatchAsync(Request, canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rpc.Calls);
    }

    [Fact]
    public async Task Source_and_transport_preserve_fatal_failures_and_uncertain_statuses()
    {
        foreach (var fatal in new Exception[] { new OutOfMemoryException(), new AccessViolationException(), new StackOverflowException() })
        {
            var client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromException<string>(fatal)), new(TimeSpan.FromSeconds(1)), new Rpc((_, _) => Task.FromResult<string?>(Request.TaskName)), TimeProvider.System);
            Assert.Same(fatal, await Record.ExceptionAsync(() => client.DispatchAsync(Request)));
            client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromResult("token")), new(TimeSpan.FromSeconds(1)), new Rpc((_, _) => Task.FromException<string?>(fatal)), TimeProvider.System);
            Assert.Same(fatal, await Record.ExceptionAsync(() => client.DispatchAsync(Request)));
        }
        foreach (var (exception, code) in new (Exception, string)[]
        {
            (new OperationCanceledException("secret"), "ASCT307"),
            (new HttpRequestException("secret"), "ASCT302"),
            (new CloudTasksResponseLimitException(), "ASCT304")
        })
        {
            var client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromResult("token")), new(TimeSpan.FromSeconds(1)), new Rpc((_, _) => Task.FromException<string?>(exception)), TimeProvider.System);
            Assert.Equal(code, (await client.DispatchAsync(Request)).DiagnosticCode);
        }
    }

    [Fact]
    public async Task Noncancellation_transport_evidence_survives_late_caller_and_budget_signals()
    {
        foreach (var exceptionAndCode in new (Exception Error, string Code)[]
        {
            (new RpcException(new Status(StatusCode.Unavailable, "seeded-provider-secret")), "ASCT302"),
            (new RpcException(new Status(StatusCode.ResourceExhausted, "seeded-provider-secret")), "ASCT303"),
            (new HttpRequestException("seeded-provider-secret"), "ASCT302"),
            (new CloudTasksResponseLimitException(), "ASCT304"),
            (new InvalidOperationException("seeded-provider-secret"), "ASCT305")
        })
        {
            using var caller = new CancellationTokenSource();
            var clock = new FakeTimeProvider();
            var rpc = new Rpc((_, _) =>
            {
                caller.Cancel();
                clock.Advance(TimeSpan.FromSeconds(2));
                return Task.FromException<string?>(exceptionAndCode.Error);
            });
            var client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromResult("token")), new(TimeSpan.FromSeconds(1)), rpc, clock);
            Assert.Equal(exceptionAndCode.Code, (await client.DispatchAsync(Request, caller.Token)).DiagnosticCode);
            Assert.Equal(1, rpc.Calls);
        }
    }

    [Theory]
    [InlineData("=")]
    [InlineData("==")]
    public async Task Padding_without_token_characters_is_rejected_before_rpc(string token)
    {
        var rpc = new Rpc((_, _) => Task.FromResult<string?>(Request.TaskName));
        var client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromResult(token)), new(TimeSpan.FromSeconds(1)), rpc, TimeProvider.System);
        Assert.Equal("ASCT102", (await client.DispatchAsync(Request)).DiagnosticCode);
        Assert.Equal(0, rpc.Calls);
    }

    [Fact]
    public async Task Metadata_invalidity_and_unavailability_keep_zero_rpc_provenance()
    {
        foreach (var invalid in new[] { false, true })
        {
            var rpc = new Rpc((_, _) => Task.FromResult<string?>(Request.TaskName));
            var source = new Source(_ => ValueTask.FromException<string>(new TokenAcquisitionException(invalid)));
            var client = new CloudTasksActivationClient(source, new(TimeSpan.FromSeconds(1)), rpc, TimeProvider.System);
            Assert.Equal(invalid ? "ASCT102" : "ASCT101", (await client.DispatchAsync(Request)).DiagnosticCode);
            Assert.Equal(0, rpc.Calls);
        }
    }

    [Fact]
    public async Task Fatal_local_handler_failure_is_not_converted_by_the_official_sdk_to_a_receipt()
    {
        var fatal = new OutOfMemoryException("seeded-fatal");
        using var transport = new CloudTasksSdkTransport(new Uri("https://fixture.invalid"), new FatalHandler(fatal));
        var client = new CloudTasksActivationClient(new Source(_ => ValueTask.FromResult("token")), new(TimeSpan.FromSeconds(1)), transport, TimeProvider.System);
        Assert.Same(fatal, await Record.ExceptionAsync(() => client.DispatchAsync(Request)));
    }

    [Fact]
    public async Task Noncooperative_custom_source_is_awaited_until_it_finishes_without_detached_work()
    {
        var clock = new FakeTimeProvider();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new Rpc((_, _) => Task.FromResult<string?>(Request.TaskName));
        var client = new CloudTasksActivationClient(new Source(_ => new ValueTask<string>(release.Task)), new(TimeSpan.FromSeconds(1)), rpc, clock);
        var pending = client.DispatchAsync(Request);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, rpc.Calls);
        release.SetResult("token");
        Assert.Equal("ASCT103", (await pending).DiagnosticCode);
        Assert.Equal(0, rpc.Calls);
    }

    private sealed class FatalHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class Source(Func<CancellationToken, ValueTask<string>> acquire) : ICloudTasksAccessTokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => acquire(cancellationToken);
    }
    private sealed class Rpc(Func<TimeSpan, CancellationToken, Task<string?>> create) : ICloudTasksRpcTransport
    {
        internal int Calls { get; private set; }
        public Task<string?> CreateAsync(CloudTasksActivationRequest request, string token, TimeSpan remaining, CancellationToken cancellationToken)
        {
            Calls++;
            return create(remaining, cancellationToken);
        }
    }
}
