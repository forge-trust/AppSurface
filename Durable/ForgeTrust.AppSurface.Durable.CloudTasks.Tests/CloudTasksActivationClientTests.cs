using System.Diagnostics;
using ForgeTrust.AppSurface.Durable.CloudTasks;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

public sealed class CloudTasksActivationClientTests
{
    private static readonly string ExpectedName = new CloudTasksActivationRequest(Target(), "task_01").TaskName;

    [Fact]
    public async Task Every_rpc_status_maps_to_the_canonical_receipt_or_caller_cancellation()
    {
        // protects=complete closed SDK status mapping; fails_when=any status maps to the wrong disposition/code; why_new=guards every externally meaningful classification branch; seam=ICloudTasksRpcTransport throws status-only RpcException; no_claim=provider message semantics
        var mappings = new Dictionary<StatusCode, (CloudTasksDispatchDisposition Disposition, string Code)>
        {
            [StatusCode.OK] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
            [StatusCode.AlreadyExists] = (CloudTasksDispatchDisposition.AlreadyExists, "ASCT002"),
            [StatusCode.InvalidArgument] = (CloudTasksDispatchDisposition.Rejected, "ASCT201"),
            [StatusCode.Unauthenticated] = (CloudTasksDispatchDisposition.Rejected, "ASCT202"),
            [StatusCode.PermissionDenied] = (CloudTasksDispatchDisposition.Rejected, "ASCT203"),
            [StatusCode.NotFound] = (CloudTasksDispatchDisposition.Rejected, "ASCT204"),
            [StatusCode.FailedPrecondition] = (CloudTasksDispatchDisposition.Rejected, "ASCT205"),
            [StatusCode.OutOfRange] = (CloudTasksDispatchDisposition.Rejected, "ASCT206"),
            [StatusCode.Unimplemented] = (CloudTasksDispatchDisposition.Rejected, "ASCT207"),
            [StatusCode.DeadlineExceeded] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT301"),
            [StatusCode.Unavailable] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT302"),
            [StatusCode.ResourceExhausted] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT303"),
            [StatusCode.Unknown] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
            [StatusCode.Aborted] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
            [StatusCode.Internal] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
            [StatusCode.DataLoss] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
            [StatusCode.Cancelled] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT307"),
            [(StatusCode)999] = (CloudTasksDispatchDisposition.Indeterminate, "ASCT305"),
        };

        Assert.Equal(17, Enum.GetValues<StatusCode>().Length);
        foreach (var (status, expected) in mappings)
        {
            var client = Client(new FakeTokenSource("token"), new FakeTransport((_, _, _, _) => throw new RpcException(new Status(status, "provider-secret"))));
            var receipt = await client.DispatchAsync(Request());
            Assert.Equal(ExpectedName, receipt.TaskName);
            Assert.Equal(expected.Disposition, receipt.Disposition);
            Assert.Equal(expected.Code, receipt.DiagnosticCode);
            Assert.DoesNotContain("provider-secret", receipt.TaskName, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-secret", receipt.DiagnosticCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Success_requires_exact_name_and_missing_name_is_indeterminate()
    {
        // protects=validated successful creation evidence; fails_when=missing or altered response name claims Created; why_new=wire success is not sufficient without exact task identity; seam=ICloudTasksRpcTransport result; no_claim=delivery or work completion
        foreach (var (returned, code) in new (string? Name, string Code)[] { (ExpectedName, "ASCT001"), (null, "ASCT306"), ("projects/other/task", "ASCT306") })
        {
            var receipt = await Client(new FakeTokenSource("token"), new FakeTransport((_, _, _, _) => Task.FromResult(returned)))
                .DispatchAsync(Request());
            Assert.Equal(code, receipt.DiagnosticCode);
            Assert.Equal(ExpectedName, receipt.TaskName);
        }
    }

    [Fact]
    public async Task Invalid_token_and_token_acquisition_failures_never_invoke_transport()
    {
        // protects=pre-submission token failures and zero provider calls; fails_when=transport runs or failure leaks source data; why_new=invalid credentials must never cross invocation boundary; seam=public token-source interface plus private RPC transport; no_claim=token cryptographic validity
        foreach (var token in new string?[] { null, "", "Bearer xyz", "secret token", new string('x', 8193) })
        {
            var transport = new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName));
            var receipt = await Client(new FakeTokenSource(token!), transport).DispatchAsync(Request());
            Assert.Equal("ASCT102", receipt.DiagnosticCode);
            Assert.Equal(0, transport.Calls);
        }

        var failingTransport = new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName));
        var failed = await Client(new FakeTokenSource(() => ValueTask.FromException<string>(new InvalidOperationException("source-secret"))), failingTransport).DispatchAsync(Request());
        Assert.Equal("ASCT101", failed.DiagnosticCode);
        Assert.Equal(0, failingTransport.Calls);
        Assert.Equal(ExpectedName, failed.TaskName);
        Assert.DoesNotContain("source-secret", failed.TaskName, StringComparison.Ordinal);
        Assert.DoesNotContain("source-secret", failed.DiagnosticCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_failure_cancellation_is_sanitized_and_invalid_argument_has_zero_io()
    {
        // protects=caller cancellation and argument validation before I/O; fails_when=caller token is lost or a null request obtains credentials; why_new=invalid calls must be inert and cancellation must remain caller-owned; seam=token-source cancellation token; no_claim=remote operation outcome
        var tokenSource = new FakeTokenSource("token");
        var transport = new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName));
        var client = Client(tokenSource, transport);
        await Assert.ThrowsAsync<ArgumentException>(() => client.DispatchAsync(null!));
        Assert.Equal(0, tokenSource.Calls);
        Assert.Equal(0, transport.Calls);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DispatchAsync(Request(), canceled.Token));
        Assert.Equal(canceled.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, tokenSource.Calls);

        var sourceCancellation = new FakeTokenSource(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        });
        using var caller = new CancellationTokenSource();
        var pending = Client(sourceCancellation, transport).DispatchAsync(Request(), caller.Token);
        caller.Cancel();
        var canceledSource = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(caller.Token, canceledSource.CancellationToken);
        Assert.Null(canceledSource.InnerException);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Budget_expiry_before_invocation_is_rejected_and_after_invocation_is_indeterminate()
    {
        // protects=budget phase classification; fails_when=pre-call timeout is confused with ambiguous in-flight creation; why_new=invocation boundary changes what can safely be claimed; seam=FakeTimeProvider advances at token or transport; no_claim=whether server committed an interrupted request
        var beforeClock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var beforeTransport = new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName));
        var beforeSource = new FakeTokenSource(_ => { beforeClock.Advance(TimeSpan.FromSeconds(2)); return ValueTask.FromResult("token"); });
        var before = await Client(beforeSource, beforeTransport, beforeClock, TimeSpan.FromSeconds(1)).DispatchAsync(Request());
        Assert.Equal("ASCT103", before.DiagnosticCode);
        Assert.Equal(0, beforeTransport.Calls);

        var afterClock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var afterTransport = new FakeTransport((_, _, _, _) =>
        {
            afterClock.Advance(TimeSpan.FromSeconds(2));
            throw new OperationCanceledException("transport-secret");
        });
        var after = await Client(new FakeTokenSource("token"), afterTransport, afterClock, TimeSpan.FromSeconds(1)).DispatchAsync(Request());
        Assert.Equal("ASCT301", after.DiagnosticCode);
        Assert.Equal(1, afterTransport.Calls);
        Assert.Equal(ExpectedName, after.TaskName);
        Assert.DoesNotContain("transport-secret", after.TaskName, StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret", after.DiagnosticCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledged_success_and_definitive_rejection_win_late_caller_cancellation()
    {
        // protects=acknowledged provider evidence precedence; fails_when=late cancellation rewrites success or definitive rejection; why_new=completed evidence must win a racing cancellation; seam=transport synchronously signals caller then returns/throws definitive evidence; no_claim=acceptance when a call is still pending
        foreach (var definitiveRejection in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var transport = new FakeTransport((_, _, _, _) =>
            {
                cancellation.Cancel();
                if (definitiveRejection) throw new RpcException(new Status(StatusCode.PermissionDenied, "provider-secret"));
                return Task.FromResult<string?>(ExpectedName);
            });
            var receipt = await Client(new FakeTokenSource("token"), transport).DispatchAsync(Request(), cancellation.Token);
            Assert.Equal(definitiveRejection ? "ASCT203" : "ASCT001", receipt.DiagnosticCode);
        }
    }

    [Fact]
    public async Task Caller_signal_precedes_budget_for_cancellation_and_fatal_exceptions_escape()
    {
        // protects=specified caller-over-budget cancellation precedence and fatal exception propagation; fails_when=budget masks caller or fatal maps to receipt; why_new=distinguishes caller intent from expired internal deadline and process-fatal faults; seam=FakeTimeProvider plus transport exception; no_claim=process recovery from fatal failure
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var caller = new CancellationTokenSource();
        var transport = new FakeTransport((_, _, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            caller.Cancel();
            throw new OperationCanceledException("secret");
        });
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new FakeTokenSource("token"), transport, clock, TimeSpan.FromSeconds(1)).DispatchAsync(Request(), caller.Token));
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Null(canceled.InnerException);

        var fatal = new OutOfMemoryException("fatal-secret");
        var fatalTransport = new FakeTransport((_, _, _, _) => Task.FromException<string?>(fatal));
        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => Client(new FakeTokenSource("token"), fatalTransport).DispatchAsync(Request())));
    }

    [Fact]
    public async Task Concurrent_dispatches_use_invocation_local_tokens_budgets_and_clock_progress()
    {
        // protects=per-call token isolation and shared client concurrency; fails_when=token/header/budget crosses dispatches; why_new=singleton clients must preserve invocation-local state; seam=two overlapping calls through one fake transport and controlled clock; no_claim=provider-side authorization acceptance
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tokenIndex = 0;
        var source = new FakeTokenSource(_ => ValueTask.FromResult("token-" + Interlocked.Increment(ref tokenIndex)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var transport = new FakeTransport(async (_, token, remaining, _) =>
        {
            seen.Add(token);
            Assert.Equal(TimeSpan.FromSeconds(5), remaining);
            if (seen.Count == 2) entered.TrySetResult();
            await release.Task;
            return ExpectedName;
        });
        var client = Client(source, transport, clock, TimeSpan.FromSeconds(5));
        var first = client.DispatchAsync(Request());
        var second = client.DispatchAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        release.TrySetResult();
        var receipts = await Task.WhenAll(first, second);
        Assert.Equal(2, source.Calls);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.All(receipts, r => Assert.Equal("ASCT001", r.DiagnosticCode));

    }

    [Fact]
    public async Task Diagnostics_are_correlated_redacted_observational_and_concurrent_safe()
    {
        // protects=complete redacted diagnostics correlated to concurrent callers without outcome mutation; fails_when=any tag/metadata leaks a seeded secret, caller correlation is crossed, or an ordinary listener exception changes results; why_new=diagnostics cross into application callbacks and must remain observational; seam=ActivityListener observes sampled package events and throws on stop while two invocations are released together; no_claim=diagnostic event delivery to external exporters
        var events = new System.Collections.Concurrent.ConcurrentBag<ObservedDiagnostic>();
        var expectedParents = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ForgeTrust.AppSurface.Durable.CloudTasks",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.ParentId is null || !expectedParents.ContainsKey(activity.ParentId)) return;
                var failure = activity.Events.SingleOrDefault(e => e.Name == "cloudtasks.failure");
                if (failure.Name is null) return;
                var eventTags = failure.Tags.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
                var activityTags = activity.TagObjects.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
                events.Add(new ObservedDiagnostic(
                    activity.ParentId,
                    activity.OperationName,
                    activity.Source.Name,
                    activity.Source.Version,
                    activity.Kind,
                    activity.TraceId.ToHexString(),
                    activity.SpanId.ToHexString(),
                    activityTags,
                    eventTags));
                throw new InvalidOperationException("listener-secret");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        async Task<(string ParentId, CloudTasksDispatchReceipt Receipt)> DispatchUnderCallerActivityAsync(
            string callerName,
            ICloudTasksAccessTokenSource source,
            ICloudTasksRpcTransport transport)
        {
            using var callerActivity = new Activity(callerName).Start();
            var parentId = callerActivity.Id ?? throw new InvalidOperationException("Caller activity ID was not created.");
            expectedParents[parentId] = callerName;
            if (Interlocked.Increment(ref readyCount) == 4) ready.TrySetResult();
            await release.Task;
            var receipt = await new CloudTasksActivationClient(
                source,
                new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(5)),
                transport,
                TimeProvider.System).DispatchAsync(Request());
            return (parentId, receipt);
        }

        var callerA = DispatchUnderCallerActivityAsync(
            "caller-a",
            new FakeTokenSource("fixture-token-a"),
            new FakeTransport((_, _, _, _) => Task.FromException<string?>(new HttpRequestException("raw-a-secret"))));
        var callerB = DispatchUnderCallerActivityAsync(
            "caller-b",
            new FakeTokenSource("fixture-token-secret"),
            new FakeTransport((_, _, _, _) => Task.FromException<string?>(new RpcException(new Status(StatusCode.Unknown, "raw-b-secret provider-secret https://worker.example/activate")))));
        var callerC = DispatchUnderCallerActivityAsync(
            "caller-c",
            new FakeTokenSource(() => ValueTask.FromException<string>(new InvalidOperationException("raw-c-secret"))),
            new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName)));
        var callerD = DispatchUnderCallerActivityAsync(
            "caller-d",
            new FakeTokenSource("fixture-token-d"),
            new FakeTransport((_, _, _, _) => Task.FromException<string?>(new InvalidOperationException("raw-d-secret https://worker.example/activate"))));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        var results = await Task.WhenAll(callerA, callerB, callerC, callerD);
        var resultA = results.Single(result => expectedParents[result.ParentId] == "caller-a");
        var resultB = results.Single(result => expectedParents[result.ParentId] == "caller-b");
        var resultC = results.Single(result => expectedParents[result.ParentId] == "caller-c");
        var resultD = results.Single(result => expectedParents[result.ParentId] == "caller-d");

        Assert.NotEqual(resultA.ParentId, resultB.ParentId);
        Assert.Equal("ASCT302", resultA.Receipt.DiagnosticCode);
        Assert.Equal("ASCT305", resultB.Receipt.DiagnosticCode);
        Assert.Equal("ASCT101", resultC.Receipt.DiagnosticCode);
        Assert.Equal("ASCT305", resultD.Receipt.DiagnosticCode);
        Assert.Equal(4, events.Count);
        var httpFailure = Assert.Single(events, item => item.ParentId == resultA.ParentId);
        var rpcFailure = Assert.Single(events, item => item.ParentId == resultB.ParentId);
        var tokenFailure = Assert.Single(events, item => item.ParentId == resultC.ParentId);
        var unexpectedFailure = Assert.Single(events, item => item.ParentId == resultD.ParentId);
        Assert.All(events, observed => Assert.Equal("cloudtasks.dispatch.failure", observed.OperationName));
        Assert.All(events, observed => Assert.Equal("ForgeTrust.AppSurface.Durable.CloudTasks", observed.SourceName));
        var expectedPackageVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(ICloudTasksActivationClient).Assembly.Location).ProductVersion!.Split('+', 2)[0];
        Assert.All(events, observed => Assert.Equal(expectedPackageVersion, observed.SourceVersion));
        Assert.All(events, observed => Assert.Equal(expectedPackageVersion, observed.EventTags["package.version"]));
        Assert.All(events, observed => Assert.Equal(ActivityKind.Internal, observed.Kind));
        Assert.All(events, observed => Assert.NotEmpty(observed.TraceId));
        Assert.All(events, observed => Assert.NotEmpty(observed.SpanId));
        Assert.All(events, observed => Assert.Empty(observed.ActivityTags));
        var allowedTags = new[] { "cause", "code", "operation", "package.version", "phase.sequence", "stage" };
        Assert.All(events, observed => Assert.Equal(allowedTags.Order(StringComparer.Ordinal), observed.EventTags.Keys.Order(StringComparer.Ordinal)));
        Assert.Equal("create", httpFailure.EventTags["operation"]);
        Assert.Equal("transport", httpFailure.EventTags["cause"]);
        Assert.Equal("submission", httpFailure.EventTags["stage"]);
        Assert.Equal("ASCT302", httpFailure.EventTags["code"]);
        Assert.Equal("create", rpcFailure.EventTags["operation"]);
        Assert.Equal("rpc", rpcFailure.EventTags["cause"]);
        Assert.Equal("submission", rpcFailure.EventTags["stage"]);
        Assert.Equal("ASCT305", rpcFailure.EventTags["code"]);
        Assert.Equal("create", unexpectedFailure.EventTags["operation"]);
        Assert.Equal("transport", unexpectedFailure.EventTags["cause"]);
        Assert.Equal("submission", unexpectedFailure.EventTags["stage"]);
        Assert.Equal("ASCT305", unexpectedFailure.EventTags["code"]);
        Assert.Equal("token", tokenFailure.EventTags["operation"]);
        Assert.Equal("source", tokenFailure.EventTags["cause"]);
        Assert.Equal("acquisition", tokenFailure.EventTags["stage"]);
        Assert.Equal("ASCT101", tokenFailure.EventTags["code"]);
        Assert.All(events, observed =>
        {
            var emittedValues = observed.ActivityTags.Values
                .Concat(observed.EventTags.Values)
                .Append(observed.ParentId)
                .Append(observed.OperationName)
                .Append(observed.SourceName)
                .Append(observed.Kind.ToString())
                .Append(observed.TraceId)
                .Append(observed.SpanId);
            foreach (var secret in new[] { "raw-a-secret", "raw-b-secret", "raw-c-secret", "raw-d-secret", "provider-secret", "fixture-token-a", "fixture-token-secret", "fixture-token-d", "listener-secret", "https://worker.example/activate" })
            {
                Assert.DoesNotContain(emittedValues, value => value?.Contains(secret, StringComparison.Ordinal) == true);
            }
        });
    }

    [Fact]
    public async Task Fatal_diagnostic_listener_exception_propagates()
    {
        // protects=fatal listener failures retain process-fatal behavior; fails_when=fatal callback exception is swallowed or converted to a receipt; why_new=ordinary listener failures are isolated but fatal runtime faults must still escape; seam=ActivityListener throws OutOfMemoryException only for this caller's Cloud Tasks failure event; no_claim=process recovery after an out-of-memory condition
        string? parentId = null;
        var fatal = new OutOfMemoryException("fatal-listener-secret");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ForgeTrust.AppSurface.Durable.CloudTasks",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.ParentId == parentId && activity.Events.Any(e => e.Name == "cloudtasks.failure")) throw fatal;
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var callerActivity = new Activity("caller-fatal-listener").Start();
        parentId = callerActivity.Id;
        var client = Client(
            new FakeTokenSource(() => ValueTask.FromException<string>(new InvalidOperationException("source-failure"))),
            new FakeTransport((_, _, _, _) => Task.FromResult<string?>(ExpectedName)));

        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => client.DispatchAsync(Request())));
    }

    private static CloudTasksActivationClient Client(FakeTokenSource source, FakeTransport transport, TimeProvider? clock = null, TimeSpan? timeout = null) =>
        new(source, new CloudTasksActivationClientOptions(timeout ?? TimeSpan.FromSeconds(5)), transport, clock ?? TimeProvider.System);

    private static CloudTasksActivationRequest Request() => new(Target(), "task_01");

    private static CloudTasksActivationTarget Target() => new("123456789", "us-central1", "wake-queue", new Uri("https://worker.example/activate"), "worker@example.iam.gserviceaccount.com", "https://worker.example", TimeSpan.FromSeconds(30));

    private sealed class FakeTokenSource : ICloudTasksAccessTokenSource
    {
        private readonly Func<CancellationToken, ValueTask<string>> _get;
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal FakeTokenSource(string token) : this(_ => ValueTask.FromResult(token)) { }
        internal FakeTokenSource(Func<ValueTask<string>> get) : this(_ => get()) { }
        internal FakeTokenSource(Func<CancellationToken, ValueTask<string>> get)
        {
            _get = token => { Interlocked.Increment(ref _calls); return get(token); };
        }
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => _get(cancellationToken);
    }

    private sealed class FakeTransport(Func<CloudTasksActivationRequest, string, TimeSpan, CancellationToken, Task<string?>> create) : ICloudTasksRpcTransport
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<string?> CreateAsync(CloudTasksActivationRequest request, string token, TimeSpan remaining, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return create(request, token, remaining, cancellationToken);
        }
    }

    private sealed record ObservedDiagnostic(
        string ParentId,
        string OperationName,
        string SourceName,
        string? SourceVersion,
        ActivityKind Kind,
        string TraceId,
        string SpanId,
        IReadOnlyDictionary<string, string?> ActivityTags,
        IReadOnlyDictionary<string, string?> EventTags);
}
